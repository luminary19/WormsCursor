using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WormsCursor.Core;

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
    Thread? _thread;
    volatile bool _running;
    bool _restored = true;

    public CursorEngine(CursorSettings settings) => _settings = settings;

    public bool IsRunning => _running;

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

            GetCursorPos(out POINT prev);

            double accDx = 0, accDy = 0;     // travel accumulated since the last recompute
            double sx = 1, sy = 0;           // smoothed direction vector (start: pointing right)
            double targetDeg = double.NaN;   // where the cursor should point
            double dispDeg = double.NaN;     // what we currently show (animated toward target)
            int idle = 0, curIdx = -1;

            while (_running)
            {
                Thread.Sleep(sleepMs);
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
                if (double.IsNaN(targetDeg)) continue;
                double diff = targetDeg - dispDeg;
                diff -= 360.0 * Math.Floor((diff + 180.0) / 360.0); // wrap to [-180, 180)
                if (Math.Abs(diff) <= maxStep) dispDeg = targetDeg;
                else dispDeg += Math.Sign(diff) * maxStep;
                dispDeg -= 360.0 * Math.Floor(dispDeg / 360.0);     // keep within [0, 360)

                int idx = ((int)Math.Round(dispDeg / stepDeg)) % _settings.Steps;
                if (idx < 0) idx += _settings.Steps;
                if (idx != curIdx)
                {
                    curIdx = idx;
                    foreach (var m in _managed)
                        SetSystemCursor(CopyIcon(m.Frames[idx]), m.Ocr);
                    if (_settings.Debug) Debug.WriteLine($"disp={idx} target={(int)Math.Round(targetDeg)}");
                }
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
        using Bitmap arrowBase = ArrowRenderer.DrawArrow(_settings);
        using Bitmap handBase = HandRenderer.DrawHand(_settings);
        _managed = new[]
        {
            new Managed(OCR_NORMAL, BuildFrames(arrowBase, 0.0, n)),
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
    const uint OCR_HAND = 32649;
    const uint SPI_SETCURSORS = 0x0057;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

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
