using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AgentsCursor.Core;

namespace AgentsCursor.App;

/// <summary>
/// The free-floating "an agent is waiting" token — a transparent, click-through, always-on-top overlay
/// that draws the waiting tool's logo (Claude's critter, etc.) and bounces it. It replaces the old
/// SetSystemCursor charm entirely, so the token no longer rides on a themed cursor: it shows whatever
/// the system cursor is, decoupled from any cursor theming.
///
/// Placements (see <see cref="CursorSettings.Placement"/>):
/// <list type="bullet">
/// <item><b>Below / above / beside the pointer</b> — the token is tied to the pointer by a springy
/// string (<see cref="TokenMotion.Step"/>) and follows it, swinging as you move and settling (with a
/// gentle idle bob) at its rest offset when you stop; near a screen edge the offset mirrors so the token
/// stays on screen.</item>
/// <item><b>Corner</b> — the token pins to a screen corner and bounces in place.</item>
/// </list>
/// The layered window is re-centred on the token every frame, sized for any tilt plus the badges
/// (<see cref="TokenCanvas"/>), so no swing or offset can clip it.
///
/// The window is painted with <c>UpdateLayeredWindow</c> (true per-pixel alpha). The animation runs on
/// a dedicated background thread with a high-resolution clock (<c>timeBeginPeriod(1)</c> +
/// <see cref="Stopwatch"/>) and integrates with the <i>real</i> elapsed time, so the bounce stays
/// buttery-smooth and time-correct even if the UI thread is busy (e.g. a dialog is open) — exactly how
/// the old cursor engine stayed smooth. The thread only spins while at least one agent is waiting; idle,
/// it parks on an event and the token is painted away, so a quiet tray burns no CPU.
///
/// Whether the token shows is <see cref="TokenVisibility.Decide"/>'s call, the one model of why it is
/// hidden: nothing waiting, turned off, a program on <see cref="CursorSettings.SuppressedPrograms"/>
/// owning the foreground window (a game, say), or a meeting (<see cref="MeetingWatcher"/>, unless
/// <see cref="CursorSettings.ShowDuringMeetings"/>). Hidden for any of them, the thread parks. A
/// <see cref="ForegroundWatcher"/> reports focus changes (event-driven); the meeting watcher checks
/// only while a token would otherwise show. When the reason goes away and something still waits, the
/// token reappears with its entrance. How it moves is <see cref="CursorSettings.Motion"/>, as Still
/// while Windows' animation effects are off.
/// </summary>
public sealed class NotifierOverlay : IDisposable
{
    readonly CursorSettings _settings;
    readonly LayeredWindow _window = new();
    // The window's HWND, captured once on the UI thread. The animation thread passes only this raw
    // handle to user32 — it never touches a Control member (reading Control.Handle off-thread throws
    // "Cross-thread operation not valid" whenever CheckForIllegalCrossThreadCalls is on, e.g. under a debugger).
    readonly IntPtr _hwnd;
    readonly Bitmap _empty = new(1, 1, PixelFormat.Format32bppArgb); // painted when hiding the token

    readonly Thread _thread;
    readonly ManualResetEventSlim _wake = new(false);
    readonly ForegroundWatcher _foreground = new();
    readonly MeetingWatcher _meetings = new(AppLog.Write);
    readonly object _gate = new(); // Reevaluate runs on the UI thread and the watchers' threads
    volatile bool _running = true;
    volatile bool _active;
    volatile TokenVisibility _visibility = TokenVisibility.Idle;
    volatile bool _animationEffects = true; // Windows' "Animation effects" (off = every style is Still)
    volatile TokenFace? _face;       // what the token shows; null = nothing waiting
    volatile bool _entrancePending;  // pop-in bounce when the token first appears
    volatile bool _resetPhysics;     // re-seed the motion on (re)appearance
    bool _disposed;

    // --- animation state (touched only by the animation thread) ---
    readonly NotifierRenderer _renderer = new(); // its GDI+ objects belong to the animation thread
    bool _frameFailureLogged; // log the first failed frame only, so a persistent fault can't flood the log
    Bitmap? _buffer;          // reused back-buffer, sized to the current canvas
    int _canvas;              // current back-buffer side (physical px)
    float _t;                 // master clock (s), for the corner bounce
    TetherState _tether;      // the pointer placements' spring (seeded on first use)
    Flip _flip;               // which axes the rest offset is mirrored on, near screen edges
    NotifierPlacement _placement; // the placement the motion state belongs to

    const int SleepMs = 6;    // ~150 Hz loop cap; real elapsed dt keeps the motion time-correct
    const float MaxDt = 0.05f;// clamp a scheduling hiccup so the explicit-Euler spring can't blow up
    const int FailureBackoffMs = 1000; // pause after a failed frame before trying again

    public NotifierOverlay(CursorSettings settings)
    {
        _settings = settings;
        _hwnd = _window.Handle;                              // realise the handle on the UI thread
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);                // a layered window draws nothing until UpdateLayeredWindow
        HideToken();                                         // …so this just parks it transparent + off-screen

        _thread = new Thread(Loop) { IsBackground = true, Name = "AgentsCursorNotifier" };
        _thread.Start();
        _foreground.Changed += Reevaluate;
        _meetings.Changed += Reevaluate;
        // Windows' animation effects switched on or off (WM_SETTINGCHANGE) re-reads them.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Reevaluate();
    }

    /// <summary>Whether the token is on screen and, if not, why. Safe to read from any thread.</summary>
    public TokenVisibility Visibility => _visibility;

    /// <summary>Raised when <see cref="Visibility"/> changes, on whichever thread changed it (the UI
    /// thread, or a watcher's). Handlers must be thread-safe and quick.</summary>
    public event Action? VisibilityChanged;

    /// <summary>Set which agent sessions currently await the user — one entry per waiting session
    /// (empty = none), each with its tool and whether it is blocked on you or finished. The token shows
    /// their <see cref="TokenFace"/>: a blocked session's logo with the "?" cloud if any is blocked,
    /// plus a "+N" count for the rest. Starts/stops the animation accordingly. Thread-safe (just swaps
    /// an immutable face and wakes the animation thread); call from anywhere.</summary>
    public void SetWaitingAgents(IReadOnlyList<WaitingAgent>? agents)
    {
        _face = TokenFace.For(agents);
        Reevaluate();
    }

    /// <summary>Re-check whether the token should be showing after a settings change (the enable
    /// toggle, the suppressed programs, the meetings rule, size, placement or motion). Size, placement
    /// and motion are read live each frame; this flips the animation on/off when the outcome
    /// changed.</summary>
    public void RefreshSettings() => Reevaluate();

    void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Reevaluate();

    // Called from the UI thread (agents, settings) and the watchers' threads (focus, meetings).
    void Reevaluate()
    {
        bool changed;
        lock (_gate)
        {
            _animationEffects = AnimationEffectsOn();
            bool waiting = _face is not null, enabled = _settings.AgentNotifierEnabled;
            bool appInFront = ProgramSuppression.Matches(_foreground.ForegroundProgram, _settings.SuppressedPrograms);
            bool showDuringMeetings = _settings.ShowDuringMeetings;
            _meetings.Watch(TokenVisibility.NeedsMeetingCheck(waiting, enabled, appInFront, showDuringMeetings));
            var visibility = TokenVisibility.Decide(waiting, enabled, appInFront, showDuringMeetings, _meetings.Current);
            changed = visibility != _visibility;
            _visibility = visibility;
            bool active = visibility.Shown;
            if (active && !_active) { _entrancePending = true; _resetPhysics = true; } // pop-in fresh
            _active = active;
            _wake.Set();
        }
        if (changed) VisibilityChanged?.Invoke();
    }

    // The animation thread: a high-resolution loop that advances the physics by the real elapsed time
    // and repaints. Parks on _wake (zero CPU) whenever nothing is waiting.
    void Loop()
    {
        timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        double last = sw.Elapsed.TotalSeconds;
        bool hidden = true;
        try
        {
            while (_running)
            {
                if (_active)
                {
                    // One frame is the failure boundary: an exception here must never escape this
                    // background thread, which would terminate the whole tray process. A failed frame
                    // is logged (once), the token is hidden, and the loop backs off and tries again.
                    try
                    {
                        // Re-assert the top of the always-on-top band on each hidden->shown transition.
                        // The window sets WS_EX_TOPMOST once at creation and never re-raises itself, so a
                        // later-created topmost overlay (e.g. the alacritty-blackhole GL overlay) would
                        // otherwise sit above the token. One raise per appearance is enough — UpdateLayeredWindow
                        // never touches z-order, so it holds for as long as the token stays visible.
                        if (hidden) { BringToTop(_hwnd); hidden = false; }
                        double now = sw.Elapsed.TotalSeconds;
                        float dt = (float)(now - last);
                        last = now;
                        if (dt > MaxDt) dt = MaxDt;
                        if (dt > 0f) Tick(dt);
                    }
                    catch (Exception ex)
                    {
                        FrameFailed(ex);
                        hidden = true;                  // re-raise on the next good frame
                        last = sw.Elapsed.TotalSeconds; // no giant dt after the back-off
                    }
                    Thread.Sleep(SleepMs);
                }
                else
                {
                    if (!hidden)
                    {
                        hidden = true;
                        try { HideToken(); } catch (Exception ex) { FrameFailed(ex); }
                    }
                    _wake.Wait();
                    if (!_running) break;
                    _wake.Reset();
                    last = sw.Elapsed.TotalSeconds; // fresh clock on resume (no giant first dt)
                }
            }
        }
        finally { timeEndPeriod(1); }
    }

    void Tick(float dt)
    {
        var face = _face;
        if (face is null) return;

        var placement = _settings.Placement;
        var motion = MotionProfile.For(_settings.Motion, _animationEffects);
        if (_resetPhysics || placement != _placement)
        {
            _tether = default; _flip = Flip.None; _t = 0f; _resetPhysics = false; _placement = placement;
        }
        _t += dt;

        // The token centre (screen px), its tilt, and the DPI of the monitor it's on, so the token keeps
        // a consistent physical size across mixed-DPI monitors.
        float cx, cy, swing, tokenPx, dpi;
        if (placement == NotifierPlacement.Corner)
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            dpi = DpiScaleForPoint(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
            tokenPx = _settings.Size * dpi;
            // Room for the bounce (0.22) and the status cloud's reach past the logo, so the hopping
            // token stays fully on screen in a top corner.
            int margin = (int)(tokenPx * (0.25f + StatusBadge.Overhang)) + (int)(16 * dpi);
            int half = (int)(tokenPx / 2f);
            int anchorX = _settings.Corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft
                ? wa.Left + margin + half : wa.Right - margin - half;
            int anchorY = _settings.Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight
                ? wa.Top + margin + half : wa.Bottom - margin - half;
            var pose = TokenMotion.CornerBounce(_t, tokenPx, motion);
            (cx, cy, swing) = (anchorX + pose.OffsetX, anchorY + pose.OffsetY, pose.SwingDeg);
        }
        else
        {
            GetCursorPos(out POINT c);
            dpi = DpiScaleForPoint(c.x, c.y);
            tokenPx = _settings.Size * dpi;
            // Mirror the rest offset per axis near the edges of the monitor under the pointer
            // (physical px under PerMonitorV2), then slide it along an edge it would still overhang;
            // the spring carries the token across.
            var work = Screen.FromPoint(new Point(c.x, c.y)).WorkingArea;
            _flip = TokenMotion.Edges(_flip, new PointF(c.x, c.y), TokenMotion.RestFor(placement, tokenPx, dpi, Flip.None), tokenPx, work);
            var rest = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, tokenPx, dpi, _flip), new PointF(c.x, c.y), tokenPx, work);
            var pose = TokenMotion.Step(ref _tether, c.x, c.y, rest, tokenPx, dt, _entrancePending, motion, motion.MaxSwingFor(placement), work);
            _entrancePending = false;
            (cx, cy, swing) = (c.x + pose.OffsetX, c.y + pose.OffsetY, pose.SwingDeg);
        }

        // The canvas is centred on the token, sized for any tilt plus the badges, so nothing clips.
        int canvas = TokenCanvas.Side(tokenPx);
        EnsureBuffer(canvas);
        int left = (int)MathF.Round(cx) - canvas / 2, top = (int)MathF.Round(cy) - canvas / 2;

        using (var g = Graphics.FromImage(_buffer!))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // The sub-pixel remainder keeps slow motion smooth while the window moves in whole pixels.
            _renderer.DrawToken(g, _settings, face, cx - left, cy - top, tokenPx, swing, dpi);
        }

        SetBitmap(_hwnd, _buffer!, left, top);
    }

    // A frame threw: log the first failure, park the token off-screen, and pause before retrying.
    void FrameFailed(Exception ex)
    {
        if (!_frameFailureLogged)
        {
            _frameFailureLogged = true;
            AppLog.Write("overlay frame failed (token hidden, retrying): " + ex);
        }
        try { HideToken(); } catch { /* best-effort; the next good frame repaints anyway */ }
        Thread.Sleep(FailureBackoffMs);
    }

    void EnsureBuffer(int canvas)
    {
        if (_buffer is not null && _canvas == canvas) return;
        _buffer?.Dispose();
        _buffer = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
        _canvas = canvas;
    }

    void HideToken() => SetBitmap(_hwnd, _empty, -32000, -32000);

    // Windows' "Animation effects" (Settings > Accessibility > Visual effects); on if it can't be read.
    static bool AnimationEffectsOn()
        => !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool on, 0) || on;

    // DPI scale (1.0 = 96 DPI) of the monitor under a screen point, so the token stays a consistent
    // physical size on mixed-DPI setups. Best-effort: falls back to 1.0 if the shell call fails.
    static float DpiScaleForPoint(int x, int y)
    {
        try
        {
            IntPtr mon = MonitorFromPoint(new POINT { x = x, y = y }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                return dx / 96f;
        }
        catch { /* shcore missing / pre-8.1: assume 96 DPI */ }
        return 1f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _foreground.Dispose(); // first: the watchers' threads call Reevaluate, which uses _wake
        _meetings.Dispose();
        _running = false;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(500); // stop touching the window before we dispose it
        _buffer?.Dispose();
        _renderer.Dispose();
        _empty.Dispose();
        _wake.Dispose();
        _window.Dispose();
    }

    /// <summary>A borderless, click-through, no-activate, top-most tool window painted via
    /// <c>UpdateLayeredWindow</c> (true per-pixel alpha). It never takes focus and mouse events fall
    /// straight through to whatever is underneath.</summary>
    sealed class LayeredWindow : Form
    {
        public LayeredWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "AgentsCursor Notifier";
            Bounds = new Rectangle(-32000, -32000, 1, 1); // off-screen until the first paint positions it
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_TOPMOST is set here at creation rather than relying on Form.TopMost: the window
                // is realised via raw ShowWindow(SW_SHOWNOACTIVATE) (never Form.Show), so WinForms never
                // issues the SetWindowPos(HWND_TOPMOST) that TopMost would. Without this the token sits in
                // the normal z-band and is hidden under any always-on-top window (e.g. a full-screen
                // click-through overlay).
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>Re-assert the window at the top of the always-on-top band, without moving,
    /// resizing, or activating it. Called once per token appearance so a topmost overlay created
    /// after us (e.g. the alacritty-blackhole GL overlay) can't keep the token buried. Safe from
    /// the animation thread — a plain z-order call on the raw HWND, like <see cref="SetBitmap"/>.</summary>
    static void BringToTop(IntPtr hwnd)
        => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Blit an ARGB bitmap as the window's content at screen (<paramref name="x"/>,
    /// <paramref name="y"/>), honouring its per-pixel alpha. Safe to call from the animation thread:
    /// it only updates the layered surface of the raw HWND (no message pump, no Control members).
    /// (Canonical Forms layered-window pattern: <c>GetHbitmap(Color.FromArgb(0))</c> + AC_SRC_ALPHA.)</summary>
    static void SetBitmap(IntPtr hwnd, Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBmp);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = x, y = y };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------- P/Invoke ----------
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    const int ULW_ALPHA = 0x02;
    const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    const int SW_SHOWNOACTIVATE = 4;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    // Re-assert topmost on each token appearance (LayeredWindow.BringToTop): a z-order-only move.
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);
}
