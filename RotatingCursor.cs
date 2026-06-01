using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;

// Global rotating-cursor prototype (Windows). The arrow rotates to follow the
// mouse-movement direction, like in Worms 3D. Only OCR_NORMAL (the plain arrow).
//
// Direction is computed from ACCUMULATED travel (not from each micro-sample), so
// +/-1 px jitter doesn't cause wobble on straight/vertical moves.
// Ctrl+C / closing the console restores the default cursors.
class RotatingCursor
{
    // ---------- configuration (tweak freely) ----------
    const int    STEPS      = 360;  // number of angles; 360 = 1 degree per step
    const int    HZ         = 144;  // position polling rate (Hz)
    const double AIM_DIST   = 8.0;  // px of travel before recomputing direction (less = snappier, more = steadier)
    const double AIM_SMOOTH = 0.50; // direction smoothing between re-aims 0..1 (lower = smoother/lazier)
    const double HYST_DEG   = 3.0;  // dead zone: don't move the cursor for changes below this many degrees
    const int    IDLE_RESET = 5;    // frames without movement before clearing the accumulator (anti-jitter at rest)
    const bool   DEBUG      = false; // true = print the current angle to the console

    const int    CANVAS     = 64;
    const int    PIVOT      = CANVAS / 2; // rotation pivot = arrow tip = hotspot

    // ---------- P/Invoke ----------
    const uint OCR_NORMAL     = 32512;
    const uint SPI_SETCURSORS = 0x0057;

    delegate bool ConsoleCtrlDelegate(uint ctrlType);

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
    [DllImport("gdi32.dll")]  static extern bool DeleteObject(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate h, bool add);
    [DllImport("winmm.dll")]  static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")]  static extern uint timeEndPeriod(uint p);

    // ---------- state ----------
    static IntPtr[] cursors = new IntPtr[STEPS];
    static ConsoleCtrlDelegate ctrl;
    static volatile bool running = true;
    static bool restored;

    static void Main()
    {
        Console.WriteLine("Building " + STEPS + " cursor rotations...");
        BuildCursors();

        ctrl = OnConsoleCtrl;
        SetConsoleCtrlHandler(ctrl, true);
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };
        AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();

        timeBeginPeriod(1); // more accurate Thread.Sleep for even polling
        Console.WriteLine("Active: " + STEPS + " angles, " + HZ + " Hz. Move the mouse. Ctrl+C = restore.");

        int sleepMs = Math.Max(1, 1000 / HZ);
        POINT prev; GetCursorPos(out prev);

        double accDx = 0, accDy = 0;     // travel accumulated since the last recompute
        double sx = 1, sy = 0;           // smoothed direction vector (start: pointing right)
        double appliedDeg = double.NaN;  // last applied angle
        int idle = 0, curIdx = -1;

        while (running)
        {
            Thread.Sleep(sleepMs);
            POINT p; GetCursorPos(out p);
            int dx = p.x - prev.x, dy = p.y - prev.y;
            prev = p;

            if (dx == 0 && dy == 0)
            {
                if (++idle >= IDLE_RESET) { accDx = 0; accDy = 0; } // at rest -> don't rotate from noise
                continue;
            }
            idle = 0;
            accDx += dx; accDy += dy;

            double dist = Math.Sqrt(accDx * accDx + accDy * accDy);
            if (dist < AIM_DIST) continue;          // too little travel to determine direction reliably

            double nx = accDx / dist, ny = accDy / dist;
            sx = sx * (1 - AIM_SMOOTH) + nx * AIM_SMOOTH;
            sy = sy * (1 - AIM_SMOOTH) + ny * AIM_SMOOTH;
            accDx = 0; accDy = 0;

            double deg = Math.Atan2(sy, sx) * 180.0 / Math.PI; // screen: Y points down => clockwise positive
            if (!double.IsNaN(appliedDeg) && CircDiff(deg, appliedDeg) < HYST_DEG) continue;
            appliedDeg = deg;

            int idx = ((int)Math.Round(deg / (360.0 / STEPS))) % STEPS;
            if (idx < 0) idx += STEPS;
            if (idx != curIdx)
            {
                curIdx = idx;
                SetSystemCursor(CopyIcon(cursors[idx]), OCR_NORMAL);
                if (DEBUG) Console.WriteLine("angle=" + ((int)Math.Round(deg)) + " idx=" + idx);
            }
        }
        Cleanup();
    }

    // smallest angular difference (0..180)
    static double CircDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }

    static void BuildCursors()
    {
        using (Bitmap baseBmp = DrawArrow())
            for (int i = 0; i < STEPS; i++)
            {
                using (var rot = new Bitmap(CANVAS, CANVAS))
                {
                    using (var g = Graphics.FromImage(rot))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.TranslateTransform(PIVOT, PIVOT);
                        g.RotateTransform((float)(i * (360.0 / STEPS)));
                        g.TranslateTransform(-PIVOT, -PIVOT);
                        g.DrawImage(baseBmp, 0, 0);
                    }
                    cursors[i] = MakeCursor(rot, PIVOT, PIVOT);
                }
            }
    }

    // arrow pointing +X (right), tip at the canvas center (= hotspot)
    static Bitmap DrawArrow()
    {
        var bmp = new Bitmap(CANVAS, CANVAS);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = PIVOT, cy = PIVOT;
            PointF[] pts = {
                new PointF(cx,      cy),       // tip
                new PointF(cx - 14, cy - 9),   // upper barb
                new PointF(cx - 14, cy - 3),
                new PointF(cx - 22, cy - 3),   // shaft
                new PointF(cx - 22, cy + 3),
                new PointF(cx - 14, cy + 3),
                new PointF(cx - 14, cy + 9),   // lower barb
            };
            using (var fill = new SolidBrush(Color.White))
            using (var pen = new Pen(Color.Black, 1.4f))
            {
                g.FillPolygon(fill, pts);
                g.DrawPolygon(pen, pts);
            }
        }
        return bmp;
    }

    // alpha-correct cursor with an explicit hotspot (GetHicon -> CreateIconIndirect)
    static IntPtr MakeCursor(Bitmap bmp, int hotX, int hotY)
    {
        IntPtr hIcon = bmp.GetHicon();
        ICONINFO ii; GetIconInfo(hIcon, out ii);
        ii.fIcon = false; ii.xHotspot = hotX; ii.yHotspot = hotY;
        IntPtr hCur = CreateIconIndirect(ref ii);
        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        if (ii.hbmMask  != IntPtr.Zero) DeleteObject(ii.hbmMask);
        DestroyIcon(hIcon);
        return hCur;
    }

    static bool OnConsoleCtrl(uint t) { Cleanup(); return false; }

    static void Cleanup()
    {
        if (restored) return; restored = true;
        SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); // restore default cursors
        timeEndPeriod(1);
    }
}
