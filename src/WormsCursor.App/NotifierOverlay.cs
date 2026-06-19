using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// The free-floating "an agent is waiting" token — a transparent, click-through, always-on-top overlay
/// that draws the waiting tool's logo (Claude's critter, etc.) and bounces it. It replaces the old
/// SetSystemCursor charm entirely, so the token no longer rides on a themed cursor: it shows whatever
/// the system cursor is, decoupled from any cursor theming.
///
/// Two placements (see <see cref="CursorSettings.Placement"/>):
/// <list type="bullet">
/// <item><b>Cursor</b> — the token hangs off the mouse pointer on a springy pendulum and follows it,
/// swinging as you move and settling (with a gentle idle bob) when you stop.</item>
/// <item><b>Corner</b> — the token pins to a screen corner and bounces in place.</item>
/// </list>
///
/// The window is painted with <c>UpdateLayeredWindow</c> (true per-pixel alpha). The animation runs on
/// a dedicated background thread with a high-resolution clock (<c>timeBeginPeriod(1)</c> +
/// <see cref="Stopwatch"/>) and integrates with the <i>real</i> elapsed time, so the bounce stays
/// buttery-smooth and time-correct even if the UI thread is busy (e.g. a dialog is open) — exactly how
/// the old cursor engine stayed smooth. The thread only spins while at least one agent is waiting; idle,
/// it parks on an event and the token is painted away, so a quiet tray burns no CPU.
/// </summary>
public sealed class NotifierOverlay : IDisposable
{
    readonly CursorSettings _settings;
    readonly LayeredWindow _window = new();
    readonly Bitmap _empty = new(1, 1, PixelFormat.Format32bppArgb); // painted when hiding the token

    readonly Thread _thread;
    readonly ManualResetEventSlim _wake = new(false);
    volatile bool _running = true;
    volatile bool _active;
    volatile string[] _tools = Array.Empty<string>();
    volatile bool _entrancePending;  // pop-in bounce when the token first appears
    volatile bool _resetPhysics;     // re-seed the pendulum on (re)appearance
    bool _disposed;

    // --- animation state (touched only by the animation thread) ---
    Bitmap? _buffer;          // reused back-buffer, sized to the current canvas
    int _canvas;              // current back-buffer side (physical px)
    float _t;                 // master clock (s)
    bool _physInit;           // pendulum bob seeded?
    float _bx, _by, _vbx, _vby; // pendulum bob position + velocity, in absolute screen px

    const int SleepMs = 6;    // ~150 Hz loop cap; real elapsed dt keeps the motion time-correct
    const float MaxDt = 0.05f;// clamp a scheduling hiccup so the explicit-Euler spring can't blow up

    public NotifierOverlay(CursorSettings settings)
    {
        _settings = settings;
        _ = _window.Handle;                                  // realise the handle on the UI thread
        ShowWindow(_window.Handle, SW_SHOWNOACTIVATE);       // a layered window draws nothing until UpdateLayeredWindow
        HideToken();                                         // …so this just parks it transparent + off-screen

        _thread = new Thread(Loop) { IsBackground = true, Name = "WormsCursorNotifier" };
        _thread.Start();
    }

    /// <summary>Set which agent sessions currently await the user — one tool id per waiting agent
    /// (empty = none). Starts/stops the animation accordingly. Thread-safe (just swaps volatile state
    /// and wakes the animation thread); call from anywhere.</summary>
    public void SetWaitingAgents(IReadOnlyList<string>? tools)
    {
        _tools = tools is null
            ? Array.Empty<string>()
            : tools.Where(t => !string.IsNullOrEmpty(t)).ToArray();
        Reevaluate();
    }

    /// <summary>Re-check whether the token should be showing after a settings change (the enable
    /// toggle, size, or placement). Size/placement are read live each frame; this flips the animation
    /// on/off when the enable toggle changed.</summary>
    public void RefreshSettings() => Reevaluate();

    void Reevaluate()
    {
        bool active = _tools.Length > 0 && _settings.AgentNotifierEnabled;
        if (active && !_active) { _entrancePending = true; _resetPhysics = true; } // pop-in fresh
        _active = active;
        _wake.Set();
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
                    hidden = false;
                    double now = sw.Elapsed.TotalSeconds;
                    float dt = (float)(now - last);
                    last = now;
                    if (dt > MaxDt) dt = MaxDt;
                    if (dt > 0f) Tick(dt);
                    Thread.Sleep(SleepMs);
                }
                else
                {
                    if (!hidden) { HideToken(); hidden = true; }
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
        var tools = _tools;
        if (tools.Length == 0) return;

        if (_resetPhysics) { _physInit = false; _t = 0f; _resetPhysics = false; }
        _t += dt;

        bool corner = _settings.Placement == NotifierPlacement.Corner;

        // Anchor (screen px) the token is positioned around, plus the DPI of the monitor it's on, so
        // the token keeps a consistent physical size across mixed-DPI monitors.
        int anchorX, anchorY;
        float dpi;
        if (corner)
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            dpi = DpiScaleForPoint(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
            float tpx = _settings.Size * dpi;
            int margin = (int)(tpx * 0.35f) + (int)(16 * dpi);
            int half = (int)(tpx / 2f);
            anchorX = _settings.Corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft
                ? wa.Left + margin + half : wa.Right - margin - half;
            anchorY = _settings.Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight
                ? wa.Top + margin + half : wa.Bottom - margin - half;
        }
        else
        {
            GetCursorPos(out POINT c);
            anchorX = c.x; anchorY = c.y;
            dpi = DpiScaleForPoint(c.x, c.y);
        }

        float tokenPx = _settings.Size * dpi;
        int canvas = Math.Max(8, (int)MathF.Ceiling(tokenPx * 2.6f));
        EnsureBuffer(canvas);

        float offX, offY, swing;
        if (corner) ComputeCornerBounce(tokenPx, out offX, out offY, out swing);
        else ComputeCursorPendulum(anchorX, anchorY, tokenPx, dt, out offX, out offY, out swing);

        using (var g = Graphics.FromImage(_buffer!))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            NotifierRenderer.DrawToken(g, _settings, tools,
                canvas / 2f + offX, canvas / 2f + offY, tokenPx, swing);
        }

        _window.SetBitmap(_buffer!, anchorX - canvas / 2, anchorY - canvas / 2);
    }

    // The pointer pendulum: a springy string from the cursor (anchor) to the token (bob), under
    // gravity, integrated in screen coords so moving the cursor whips the anchor and the token
    // lags/swings, then settles straight below with a gentle idle bob. Ported from the old engine's
    // "below" pendulum (same constants), so it bounces exactly like the cursor charm used to.
    void ComputeCursorPendulum(int ax, int ay, float tokenPx, float dt, out float offX, out float offY, out float swing)
    {
        const float pendK = 130f, pendC = 3f;
        float drop = tokenPx * 0.42f;          // rest distance below the cursor
        float gravity = drop * pendK;          // equilibrium = drop below the anchor
        float maxLen = tokenPx * 0.70f;        // taut-string clamp

        if (!_physInit)
        {
            _bx = ax; _by = ay + drop; _vbx = 0; _vby = 0; _physInit = true;
            if (_entrancePending) { _vby = -tokenPx * 3f; _entrancePending = false; } // hop up, then settle
        }
        _vbx += ((ax - _bx) * pendK - _vbx * pendC) * dt;
        _vby += ((ay - _by) * pendK + gravity - _vby * pendC) * dt;
        _bx += _vbx * dt; _by += _vby * dt;

        float dx = _bx - ax, dy = _by - ay;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > maxLen)
        {
            float nx = dx / len, ny = dy / len;
            _bx = ax + nx * maxLen; _by = ay + ny * maxLen;
            float vd = _vbx * nx + _vby * ny;
            if (vd > 0) { _vbx -= vd * nx; _vby -= vd * ny; }
            dx = _bx - ax; dy = _by - ay;
        }

        float idle = -tokenPx * 0.05f * MathF.Abs(MathF.Sin(_t * 3.2f)); // alive even when the mouse is still
        offX = dx;
        offY = dy + idle;
        swing = MathF.Atan2(dy, dx) * 180f / MathF.PI - 90f; // 0 = hanging straight down
    }

    // The corner badge: a continuous "ball" bounce (|sin| hops it up from the anchor and back down)
    // with a tiny sway + tilt so it reads as alive without the cursor driving it.
    void ComputeCornerBounce(float tokenPx, out float offX, out float offY, out float swing)
    {
        offY = -tokenPx * 0.22f * MathF.Abs(MathF.Sin(_t * 3.0f));
        offX = tokenPx * 0.04f * MathF.Sin(_t * 1.6f);
        swing = 3f * MathF.Sin(_t * 1.6f);
    }

    void EnsureBuffer(int canvas)
    {
        if (_buffer is not null && _canvas == canvas) return;
        _buffer?.Dispose();
        _buffer = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
        _canvas = canvas;
    }

    void HideToken() => _window.SetBitmap(_empty, -32000, -32000);

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
        _running = false;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(500); // stop touching the window before we dispose it
        _buffer?.Dispose();
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
            Text = "WormsCursor Notifier";
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

        /// <summary>Blit an ARGB bitmap as the window's content at screen (<paramref name="x"/>,
        /// <paramref name="y"/>), honouring its per-pixel alpha. Safe to call from the animation thread:
        /// it only updates the layered surface (no message pump needed). (Canonical Forms layered-window
        /// pattern: <c>GetHbitmap(Color.FromArgb(0))</c> + AC_SRC_ALPHA.)</summary>
        public void SetBitmap(Bitmap bmp, int x, int y)
        {
            if (!IsHandleCreated) return;
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
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    // ---------- P/Invoke ----------
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    const int ULW_ALPHA = 0x02;
    const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    const int SW_SHOWNOACTIVATE = 4;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
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
