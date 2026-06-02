using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WormsCursor.Core;

/// <summary>Which cursor the engine should force on screen for visual testing
/// (Preferences "Test cursor"), or <see cref="Off"/> to resume normal behaviour.</summary>
public enum TestCursor { Off, Arrow, Hand, Wait, AppStarting, Help }

/// <summary>
/// Rotates the system arrow and hand cursors (OCR_NORMAL + OCR_HAND) to follow
/// mouse-movement direction, like in Worms 3D. UI-agnostic: host it from a tray app,
/// a console, tests, etc.
///
/// Direction is computed from ACCUMULATED travel (not from each micro-sample), so
/// ±1 px jitter doesn't cause wobble on straight/vertical moves. The displayed angle
/// is animated toward the target so turns slew smoothly instead of snapping.
///
/// IMPORTANT: <see cref="Stop"/>/<see cref="Dispose"/> restores the default cursors.
/// If the host process is force-killed while running, the cursor stays rotated until
/// the next logon or until tools/RestoreCursor.ps1 is run.
/// </summary>
public sealed class CursorEngine : IDisposable
{
    readonly CursorSettings _settings;
    readonly object _gate = new();
    Managed[] _managed = Array.Empty<Managed>();
    Bitmap? _arrowBase;                       // kept for compositing the busy cursor
    volatile TestCursor _test = TestCursor.Off;
    Thread? _thread;
    volatile bool _running;
    bool _restored = true;

    public CursorEngine(CursorSettings settings) => _settings = settings;

    public bool IsRunning => _running;

    /// <summary>Force a specific cursor on screen for visual testing, or
    /// <see cref="TestCursor.Off"/> to resume normal behaviour. Applied on the next
    /// loop tick; safe to call from the UI thread (just sets a volatile flag).</summary>
    public void SetTestCursor(TestCursor t) => _test = t;

    /// <summary>Builds the rotated cursors and starts the background tracking loop.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            BuildCursors();
            _restored = false;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "WormsCursorLoop" };
            _thread.Start();
        }
    }

    /// <summary>Stops the loop, restores the default cursors, and frees cursor handles.</summary>
    public void Stop()
    {
        Thread? toJoin;
        lock (_gate)
        {
            toJoin = _thread;
            _running = false;
            _thread = null;
        }
        toJoin?.Join(1000);
        RestoreCursors();
        DestroyCursors();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Immediately stop applying rotated cursors and restore the Windows defaults.
    /// Safe to call from crash handlers (AppDomain.UnhandledException /
    /// Application.ThreadException): unlike <see cref="Stop"/> it does not join the
    /// loop thread or free handles — it just undoes the global system change so a
    /// dying process never leaves a rotated cursor behind.
    /// </summary>
    public void RestoreNow()
    {
        _running = false;   // stop the loop from re-applying a rotated cursor
        RestoreCursors();
    }

    // ---------- the tracking + animation loop (background thread) ----------
    void Loop()
    {
        timeBeginPeriod(1); // more accurate Thread.Sleep for even polling
        try
        {
            int sleepMs = Math.Max(1, 1000 / _settings.Hz);
            double stepDeg = 360.0 / _settings.Steps;
            double maxStep = _settings.TurnDps <= 0
                ? double.PositiveInfinity
                : _settings.TurnDps / _settings.Hz;       // max rotation per frame
            float dt = 1f / _settings.Hz;

            var arrowFrames = _managed[0].Frames;          // [0] = arrow, [1] = hand (see BuildCursors)
            var handFrames = _managed[1].Frames;
            var l = ProgressRenderer.Layout(_settings);
            int ciSize = Marshal.SizeOf<CURSORINFO>();

            GetCursorPos(out POINT prev);

            double accDx = 0, accDy = 0;     // travel accumulated since the last recompute
            double sx = 1, sy = 0;           // smoothed direction vector (start: pointing right)
            double targetDeg = double.NaN;   // where the cursor should point
            double dispDeg = double.NaN;     // what we currently show (animated toward target)
            int idle = 0, curIdx = -1;
            bool normalDirty = true;         // force a re-push of the normal cursors (e.g. after a test)

            // --- busy/progress spinner state: a pendulum bob (the ring) on a springy
            //     string anchored to the arrow's tail, under gravity, simulated in WORLD
            //     coords so it lags/swings when the cursor moves and settles when it stops ---
            float phase = 0;                          // comet rotation
            float bx = 0, by = 0, vbx = 0, vby = 0;   // bob world position + velocity
            bool bobInit = false;
            float ringCX = 0, ringCY = 0;             // bob centre in canvas px (read by Busy()/Help())
            float helpAngleDeg = 180f;                // string angle for the "?" (180 = hanging upside down)
            int sz = Math.Max(8, _settings.Size);
            const float pendK = 130f, pendC = 3f;     // string stiffness + damping (underdamped: it swings)
            float restDrop = sz * 0.30f;              // how far below the tail it hangs at rest
            float gravity = restDrop * pendK;         // chosen so the equilibrium is restDrop below the anchor
            float maxLen = sz * 0.42f;                // taut-string max length from the tail
            float phaseStep = 1.6f * MathF.PI * 2f * dt;            // ~1.6 rev/s
            int fgEvery = Math.Max(1, _settings.Hz / 60);           // ~60 fps for the animated cursor
            IntPtr lastWait = IntPtr.Zero, lastApp = IntPtr.Zero, lastHelp = IntPtr.Zero; // last handles set (vs GetCursorInfo)
            bool busyInit = false;
            var lastTest = TestCursor.Off;
            int tick = 0;

            // Composites a busy frame and turns it into a cursor (hotspot = canvas centre).
            // App-starting: the bob swings off the arrow's tail (pendulum). Wait: no arrow
            // means no anchor to read, so the ring stays CENTRED on the pointer (spin only,
            // no physics) — that way you can always see where you're pointing.
            IntPtr Busy(bool withArrow)
            {
                double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                float rx = withArrow ? ringCX : l.HotX;
                float ry = withArrow ? ringCY : l.HotY;
                using var bmp = ProgressRenderer.Compose(_settings, _arrowBase!, deg, rx, ry, phase, withArrow);
                return MakeCursor(bmp, l.HotX, l.HotY);
            }

            // The help cursor: the arrow plus a "?" that hangs off the tail on the same
            // pendulum (ringCX/ringCY) but tilts with the string instead of spinning.
            IntPtr Help()
            {
                double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                using var bmp = ProgressRenderer.ComposeHelp(_settings, _arrowBase!, deg, ringCX, ringCY, helpAngleDeg);
                return MakeCursor(bmp, l.HotX, l.HotY);
            }

            while (_running)
            {
                Thread.Sleep(sleepMs);
                tick++;
                GetCursorPos(out POINT p);
                int dx = p.x - prev.x, dy = p.y - prev.y;
                prev = p;

                // --- 1) update the TARGET from movement ---
                if (dx == 0 && dy == 0)
                {
                    if (++idle >= _settings.IdleReset) { accDx = 0; accDy = 0; } // at rest -> drop noise
                }
                else
                {
                    idle = 0;
                    accDx += dx; accDy += dy;
                    double dist = Math.Sqrt(accDx * accDx + accDy * accDy);
                    if (dist >= _settings.AimDist) // enough travel to determine direction reliably
                    {
                        double nx = accDx / dist, ny = accDy / dist;
                        sx = sx * (1 - _settings.AimSmooth) + nx * _settings.AimSmooth;
                        sy = sy * (1 - _settings.AimSmooth) + ny * _settings.AimSmooth;
                        accDx = 0; accDy = 0;

                        double deg = Math.Atan2(sy, sx) * 180.0 / Math.PI; // screen Y down => clockwise positive
                        if (double.IsNaN(targetDeg) || CircDiff(deg, targetDeg) > _settings.HystDeg)
                        {
                            targetDeg = deg;
                            if (double.IsNaN(dispDeg)) dispDeg = deg; // first time: no spin from an arbitrary angle
                        }
                    }
                }

                // --- 2) ANIMATE: move dispDeg toward targetDeg the short way ---
                if (!double.IsNaN(targetDeg))
                {
                    double diff = targetDeg - dispDeg;
                    diff -= 360.0 * Math.Floor((diff + 180.0) / 360.0); // wrap to [-180, 180)
                    if (Math.Abs(diff) <= maxStep) dispDeg = targetDeg;
                    else dispDeg += Math.Sign(diff) * maxStep;
                    dispDeg -= 360.0 * Math.Floor(dispDeg / 360.0);     // keep within [0, 360)
                }
                int idx = ((int)Math.Round((double.IsNaN(dispDeg) ? 0.0 : dispDeg) / stepDeg)) % _settings.Steps;
                if (idx < 0) idx += _settings.Steps;

                // --- 3) advance the spinner: spin the comet, and swing the bob. The
                //        bob hangs off the arrow's TAIL on a springy string under gravity;
                //        simulated in world coords so moving the cursor whips the anchor
                //        and the bob lags/swings, then settles straight down when you stop ---
                phase += phaseStep;
                if (phase > MathF.PI * 2f) phase -= MathF.PI * 2f;
                {
                    float rad = (float)((double.IsNaN(dispDeg) ? 0.0 : dispDeg) * Math.PI / 180.0);
                    float ax = p.x - l.TailLen * MathF.Cos(rad);  // anchor = arrow tail (world)
                    float ay = p.y - l.TailLen * MathF.Sin(rad);
                    if (!bobInit) { bx = ax; by = ay + restDrop; vbx = vby = 0; bobInit = true; }
                    vbx += ((ax - bx) * pendK - vbx * pendC) * dt;
                    vby += ((ay - by) * pendK + gravity - vby * pendC) * dt;
                    bx += vbx * dt; by += vby * dt;
                    float sx2 = bx - ax, sy2 = by - ay;
                    float slen = MathF.Sqrt(sx2 * sx2 + sy2 * sy2);
                    if (slen > maxLen) // taut string: clamp length and kill outward velocity
                    {
                        float nx = sx2 / slen, ny = sy2 / slen;
                        bx = ax + nx * maxLen; by = ay + ny * maxLen;
                        float vd = vbx * nx + vby * ny;
                        if (vd > 0) { vbx -= vd * nx; vby -= vd * ny; }
                    }
                    ringCX = Math.Clamp(l.HotX + (bx - p.x), l.DotR, l.Canvas - l.DotR);
                    ringCY = Math.Clamp(l.HotY + (by - p.y), l.DotR, l.Canvas - l.DotR);
                    // string angle (anchor -> bob); +90 so the "?" hangs upside-down at rest
                    // (straight-down string) and tilts as the bob swings to the sides.
                    helpAngleDeg = (float)(Math.Atan2(by - ay, bx - ax) * 180.0 / Math.PI) + 90f;
                }

                // --- 4) apply cursors ---
                var test = _test;
                if (test != lastTest) { lastTest = test; normalDirty = true; curIdx = -1; }
                bool fgRender = tick % fgEvery == 0;

                if (test == TestCursor.Off)
                {
                    if (normalDirty || idx != curIdx)
                    {
                        curIdx = idx; normalDirty = false;
                        SetSystemCursor(CopyIcon(arrowFrames[idx]), OCR_NORMAL);
                        SetSystemCursor(CopyIcon(handFrames[idx]), OCR_HAND);
                    }
                    // Theme the busy slots once, then re-render them ONLY while a busy
                    // cursor is actually on screen (GetCursorInfo) — so an idle tray app
                    // doesn't burn CPU animating a cursor nobody is looking at.
                    if (!busyInit)
                    {
                        lastWait = Busy(false); SetSystemCursor(lastWait, OCR_WAIT);
                        lastApp = Busy(true); SetSystemCursor(lastApp, OCR_APPSTARTING);
                        lastHelp = Help(); SetSystemCursor(lastHelp, OCR_HELP);
                        busyInit = true;
                    }
                    else if (fgRender)
                    {
                        var ci = new CURSORINFO { cbSize = ciSize };
                        if (GetCursorInfo(ref ci))
                        {
                            if (ci.hCursor == lastWait) { lastWait = Busy(false); SetSystemCursor(lastWait, OCR_WAIT); }
                            else if (ci.hCursor == lastApp) { lastApp = Busy(true); SetSystemCursor(lastApp, OCR_APPSTARTING); }
                            else if (ci.hCursor == lastHelp) { lastHelp = Help(); SetSystemCursor(lastHelp, OCR_HELP); }
                        }
                    }
                }
                else if (test == TestCursor.Wait || test == TestCursor.AppStarting || test == TestCursor.Help)
                {
                    if (fgRender) // force the animated busy/help cursor onto the visible slots
                    {
                        IntPtr h = test == TestCursor.Help ? Help() : Busy(test == TestCursor.AppStarting);
                        SetSystemCursor(h, OCR_NORMAL);
                        SetSystemCursor(CopyIcon(h), OCR_HAND);
                    }
                }
                else if (idx != curIdx || normalDirty) // test == Arrow or Hand (static)
                {
                    curIdx = idx; normalDirty = false;
                    var frames = test == TestCursor.Hand ? handFrames : arrowFrames;
                    SetSystemCursor(CopyIcon(frames[idx]), OCR_NORMAL);
                    SetSystemCursor(CopyIcon(frames[idx]), OCR_HAND);
                }

                if (_settings.Debug) Debug.WriteLine($"disp={idx} target={(int)Math.Round(targetDeg)} test={test}");
            }
        }
        finally
        {
            timeEndPeriod(1);
            RestoreCursors(); // restore even if the loop threw — never die rotated
        }
    }

    // smallest angular difference (0..180)
    static double CircDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }

    void BuildCursors()
    {
        DestroyCursors();
        int n = _settings.Steps;
        _arrowBase = ArrowRenderer.DrawArrow(_settings); // kept (not disposed) for busy-cursor compositing
        using Bitmap handBase = HandRenderer.DrawHand(_settings);
        _managed = new[]
        {
            new Managed(OCR_NORMAL, BuildFrames(_arrowBase, 0.0, n)),
            new Managed(OCR_HAND, BuildFrames(handBase, HandShape.BaseAngleDeg, n)),
        };
    }

    // Builds n cursor frames; frame i points at (i * 360/n) degrees. baseAngleDeg is the
    // direction the source bitmap already points, so it's subtracted out.
    static IntPtr[] BuildFrames(Bitmap baseBmp, double baseAngleDeg, int n)
    {
        int size = baseBmp.Width, pivot = size / 2;
        double step = 360.0 / n;
        var frames = new IntPtr[n];
        for (int i = 0; i < n; i++)
        {
            using var rot = new Bitmap(size, size);
            using (var g = Graphics.FromImage(rot))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TranslateTransform(pivot, pivot);
                g.RotateTransform((float)(i * step - baseAngleDeg));
                g.TranslateTransform(-pivot, -pivot);
                g.DrawImage(baseBmp, 0, 0);
            }
            frames[i] = MakeCursor(rot, pivot, pivot);
        }
        return frames;
    }

    void DestroyCursors()
    {
        foreach (var m in _managed)
            foreach (var h in m.Frames)
                if (h != IntPtr.Zero) DestroyIcon(h);
        _managed = Array.Empty<Managed>();
        _arrowBase?.Dispose();
        _arrowBase = null;
    }

    // A system cursor we manage: its OCR id and the n rotated frames (frame i points at
    // i * 360/n degrees, ready to apply directly).
    sealed class Managed
    {
        public readonly uint Ocr;
        public readonly IntPtr[] Frames;
        public Managed(uint ocr, IntPtr[] frames) { Ocr = ocr; Frames = frames; }
    }

    // alpha-correct cursor with an explicit hotspot (GetHicon -> CreateIconIndirect)
    static IntPtr MakeCursor(Bitmap bmp, int hotX, int hotY)
    {
        IntPtr hIcon = bmp.GetHicon();
        GetIconInfo(hIcon, out ICONINFO ii);
        ii.fIcon = false; ii.xHotspot = hotX; ii.yHotspot = hotY;
        IntPtr hCur = CreateIconIndirect(ref ii);
        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        DestroyIcon(hIcon);
        return hCur;
    }

    void RestoreCursors()
    {
        if (_restored) return;
        _restored = true;
        RestoreDefaultCursors();
    }

    /// <summary>
    /// Reloads the user's configured cursor scheme from the registry, undoing any
    /// <c>SetSystemCursor</c> change — including a rotated cursor left behind by a
    /// previous instance that was killed (Task Manager / "Stop Debugging") before it
    /// could restore. SetSystemCursor never touches the registry, so the real scheme
    /// is always still there. Cheap and safe to call when nothing was hijacked.
    /// </summary>
    public static void RestoreDefaultCursors()
        => SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

    // ---------- P/Invoke ----------
    const uint OCR_NORMAL = 32512;
    const uint OCR_WAIT = 32514;
    const uint OCR_APPSTARTING = 32650;
    const uint OCR_HELP = 32651;
    const uint OCR_HAND = 32649;
    const uint SPI_SETCURSORS = 0x0057;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
    [DllImport("user32.dll")] static extern IntPtr CopyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pii);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO pii);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);
}
