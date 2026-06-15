using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WormsCursor.Core;

/// <summary>Which cursor the engine should force on screen for visual testing
/// (Preferences "Test cursor"), or <see cref="Off"/> to resume normal behaviour.</summary>
public enum TestCursor { Off, Arrow, Hand, Wait, AppStarting, Help, Cross, Ibeam, SizeWE, SizeNS, SizeNWSE, SizeNESW, SizeAll, No }

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
    Bitmap? _arrowBase;                       // kept for compositing the busy cursor + click-pop scaling
    Bitmap? _handBase;                        // kept for click-pop scaling of the hand
    volatile bool _ibeamKick;                 // a keystroke arrived (set by NudgeIbeam) -> hop the I-beam
    volatile string[] _waitingTools = Array.Empty<string>(); // one tool id per waiting agent (dangling charms)
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

    /// <summary>Signal a keystroke so the on-screen I-beam does a little "hop" (click
    /// feedback). Called from the app's keyboard hook on the UI thread; just sets a flag,
    /// consumed on the next loop tick. No-op unless click feedback is on and an I-beam is
    /// actually showing.</summary>
    public void NudgeIbeam() => _ibeamKick = true;

    /// <summary>Set which agent sessions currently await the user — one tool id (e.g. "claude-code",
    /// "codex") per waiting agent. The loop hangs one logo charm per entry off the cursor's pendulum
    /// (empty = none, cheap pre-rendered frames resume). Thread-safe: copies into a fresh array and
    /// stores it as a volatile, consumed on the next tick. The tray re-pushes after an engine restart.</summary>
    public void SetWaitingAgents(IReadOnlyList<string>? tools)
    {
        if (tools is null || tools.Count == 0) { _waitingTools = Array.Empty<string>(); return; }
        var arr = new string[tools.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = tools[i];
        _waitingTools = arr;
    }

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
            using var fgBuf = new Bitmap(l.Canvas, l.Canvas); // reused back-buffer for foreground composites (Size is fixed for a run)
            int ciSize = Marshal.SizeOf<CURSORINFO>();

            // LoadCursor(NULL, OCR_*) returns a stable shared handle — fetch each once instead of
            // calling it up to 11×/frame in the on-screen detection chain below.
            IntPtr hcWait = LoadCursor(IntPtr.Zero, (IntPtr)OCR_WAIT);
            IntPtr hcApp  = LoadCursor(IntPtr.Zero, (IntPtr)OCR_APPSTARTING);
            IntPtr hcHelp = LoadCursor(IntPtr.Zero, (IntPtr)OCR_HELP);
            IntPtr hcCross= LoadCursor(IntPtr.Zero, (IntPtr)OCR_CROSS);
            IntPtr hcIbeam= LoadCursor(IntPtr.Zero, (IntPtr)OCR_IBEAM);
            IntPtr hcWE   = LoadCursor(IntPtr.Zero, (IntPtr)OCR_SIZEWE);
            IntPtr hcNS   = LoadCursor(IntPtr.Zero, (IntPtr)OCR_SIZENS);
            IntPtr hcD1   = LoadCursor(IntPtr.Zero, (IntPtr)OCR_SIZENWSE);
            IntPtr hcD2   = LoadCursor(IntPtr.Zero, (IntPtr)OCR_SIZENESW);
            IntPtr hcMove = LoadCursor(IntPtr.Zero, (IntPtr)OCR_SIZEALL);
            IntPtr hcNo   = LoadCursor(IntPtr.Zero, (IntPtr)OCR_NO);

            // Per-cursor enable flags: a cursor switched OFF in Preferences is left as the
            // Windows default — we simply never SetSystemCursor its slot (nor re-theme it on
            // screen). Read once: settings are immutable for a run (Apply stops+restarts the
            // engine). Arrow governs OCR_NORMAL + OCR_UP (alternate-select shares the arrow).
            bool onArrow = _settings.IsCursorEnabled(TestCursor.Arrow);
            bool onHand  = _settings.IsCursorEnabled(TestCursor.Hand);
            bool onWait  = _settings.IsCursorEnabled(TestCursor.Wait);
            bool onApp   = _settings.IsCursorEnabled(TestCursor.AppStarting);
            bool onHelp  = _settings.IsCursorEnabled(TestCursor.Help);
            bool onCross = _settings.IsCursorEnabled(TestCursor.Cross);
            bool onIbeam = _settings.IsCursorEnabled(TestCursor.Ibeam);
            bool onWE    = _settings.IsCursorEnabled(TestCursor.SizeWE);
            bool onNS    = _settings.IsCursorEnabled(TestCursor.SizeNS);
            bool onD1    = _settings.IsCursorEnabled(TestCursor.SizeNWSE);
            bool onD2    = _settings.IsCursorEnabled(TestCursor.SizeNESW);
            bool onMove  = _settings.IsCursorEnabled(TestCursor.SizeAll);
            bool onNo    = _settings.IsCursorEnabled(TestCursor.No);

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

            // --- agent-notifier charms: the waiting tools' logos hang on a pendulum, same world-space
            //     spring physics as the busy ring / help "?". The ANCHOR differs by cursor kind:
            //     the arrow & hand rotate to follow movement, so their logo hangs off the tail
            //     (ringCX/ringCY + helpAngleDeg) exactly like the "?"; the cursors that DON'T rotate
            //     (I-beam, crosshair, resize, move, unavailable, the no-arrow wait spinner) instead
            //     hang it straight BELOW the hotspot (belowCX/belowCY) so it doesn't appear to orbit
            //     them. Both are read by ApplyCharms. ---
            float lbx = 0, lby = 0, lvbx = 0, lvby = 0; bool belowInit = false;
            float belowCX = 0, belowCY = 0, belowDeg = 0; // hotspot-anchored bob (canvas px) + swing
            float belowDrop = sz * 0.42f;             // hangs below the hotspot, clear of the glyph
            float belowGravity = belowDrop * pendK;
            float belowMaxLen = sz * 0.60f;

            // --- crosshair state: a breathing gap + recoil spring + a slowly spinning ring ---
            float tsec = 0;                           // master clock for breathing / ring rotation
            float recoil = 0, vrec = 0;               // extra gap that springs out when moving fast
            float crossGap = 0, ringDeg = 0;          // values read by Cross()
            const float crossK = 120f, crossC = 12f, recoilRef = 1500f; // damped: spreads then snaps back

            // --- jelly text cursor state: the I-beam's top sways opposite to motion on a
            //     soft underdamped spring (wobbles like jelly), the bottom stays rigid ---
            float ibOffX = 0, ibOffY = 0, vibX = 0, vibY = 0; // top-of-beam offset + velocity
            const float ibK = 70f, ibC = 3.5f, ibSwayRef = 1600f; // low damping -> wobble

            // --- taffy resize state: one spring per axis (WE / NS / the two diagonals). Each
            //     stretches the double-arrow's waist with motion ALONG that axis, underdamped
            //     so the heads fly out, the waist necks, and it blobs back when you stop ---
            float rsWE = 0, vWE = 0, rsNS = 0, vNS = 0, rsD1 = 0, vD1 = 0, rsD2 = 0, vD2 = 0;
            const float rsK = 90f, rsC = 4.5f, rsRef = 1300f; // px/s of axis speed for full stretch

            // --- "no / unavailable" state: the ring is a jelly blob that stretches into an egg
            //     along the direction of travel and wobbles back. noE = signed deform (underdamped
            //     so it overshoots), noAng = the stretch axis (frozen when the cursor stops) ---
            float noE = 0, vno = 0, noAng = 0;
            const float noK = 130f, noC = 6f, noRef = 1100f, noMax = 0.28f;
            int fgEvery = Math.Max(1, _settings.Hz / 60);           // ~60 fps for the animated cursor
            bool busyInit = false;
            var lastTest = TestCursor.Off;
            int tick = 0;

            // --- click squash&pop: the pointer scales down while a mouse button is held and
            //     springs back with a little overshoot on release (underdamped). Mouse-button
            //     state is POLLED (GetAsyncKeyState) — no global mouse hook. ---
            bool clickFx = _settings.ClickFeedback;        // immutable for the run (Apply restarts)
            float popScale = 1f, vPop = 0f;                // 1 = rest
            const float popK = 220f, popC = 12f, popSquash = 0.86f;
            int lastPopIdx = -1, lastPopScaleQ = -1;       // skip re-rendering an unchanged scaled frame

            // --- I-beam typing hop: a keystroke (fed in via NudgeIbeam from the app's keyboard
            //     hook) gives the beam an upward + alternating-sideways impulse that springs back;
            //     read by Ibeam(). A refractory caps the kick rate so holding a key / very fast
            //     typing doesn't "recoil" like a machine gun. Toggled separately from click feedback. ---
            bool ibeamFx = _settings.IbeamFeedback;
            float hopY = 0f, vHop = 0f;
            const float hopK = 190f, hopC = 11f;
            float hopKick = sz * 1.15f;                    // gentler up-hop (was 1.6)
            float hopX = 0f, vHopX = 0f; int hopDir = 1;   // side shiver: alternates each key -> a little tremble
            const float hopXK = 300f, hopXC = 13f;
            float hopXKick = sz * 0.6f;                    // smaller sideways shiver (was 1.0)
            int lastKickTick = -100;
            const int kickRefractory = 8;                  // min ticks between kicks (~55ms at 144Hz)

            // Composites a busy frame and turns it into a cursor (hotspot = canvas centre).
            // App-starting: the bob swings off the arrow's tail (pendulum). Wait: no arrow
            // means no anchor to read, so the ring stays CENTRED on the pointer (spin only,
            // no physics) — that way you can always see where you're pointing.
            IntPtr Busy(bool withArrow)
            {
                double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                float rx = withArrow ? ringCX : l.HotX;
                float ry = withArrow ? ringCY : l.HotY;
                var bmp = ProgressRenderer.Compose(_settings, _arrowBase!, deg, rx, ry, phase, withArrow, fgBuf);
                if (withArrow) CharmsTail(bmp); else CharmsBelow(bmp); // arrow rotates → tail; bare wait spinner → below
                return MakeCursor(bmp, l.HotX, l.HotY);
            }

            // The help cursor: the arrow plus a "?" that hangs off the tail on the same
            // pendulum (ringCX/ringCY) but tilts with the string instead of spinning.
            IntPtr Help()
            {
                double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                var bmp = ProgressRenderer.ComposeHelp(_settings, _arrowBase!, deg, ringCX, ringCY, helpAngleDeg, fgBuf);
                CharmsTail(bmp); // has the arrow → hangs off the tail like the "?"
                return MakeCursor(bmp, l.HotX, l.HotY);
            }

            // The crosshair: a precision reticle, hotspot dead centre. No arrow, no
            // direction-follow — just the breathing/recoiling gap and the spinning ring.
            // Click feedback pulses the whole reticle about its centre (the precision point
            // stays put); pop == 1 at rest is a cheap pass-through.
            IntPtr Cross()
            {
                var bmp = ProgressRenderer.ComposeCross(_settings, crossGap, ringDeg, fgBuf);
                CharmsBelow(bmp); // no rotation → hang straight below
                return ScaledCursor(bmp, l.HotX, l.HotY, clickFx ? popScale : 1f);
            }

            // The text / I-beam cursor: rigid bottom, jelly top (ibOffX/ibOffY), hotspot centre.
            IntPtr Ibeam()
            {
                var bmp = ProgressRenderer.ComposeIbeam(_settings, ibOffX, ibOffY, hopY, hopX, fgBuf);
                CharmsBelow(bmp); // no rotation → hang straight below
                return MakeCursor(bmp, l.HotX, l.HotY);
            }

            // Resize cursors: a taffy double-arrow that necks/stretches along its axis (WE = ↔,
            // NS = ↕, D1 = ↘↖ / SIZENWSE, D2 = ↗↙ / SIZENESW). Move crosses a horizontal +
            // vertical taffy. Hotspot dead centre (the precision point Windows expects).
            IntPtr ResizeWE() { var b = ProgressRenderer.ComposeResize(_settings, 0f, rsWE, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }
            IntPtr ResizeNS() { var b = ProgressRenderer.ComposeResize(_settings, 90f, rsNS, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }
            IntPtr ResizeD1() { var b = ProgressRenderer.ComposeResize(_settings, 45f, rsD1, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }
            IntPtr ResizeD2() { var b = ProgressRenderer.ComposeResize(_settings, -45f, rsD2, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }
            IntPtr Move() { var b = ProgressRenderer.ComposeMove(_settings, rsWE, rsNS, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }

            // The "unavailable" cursor: a red circle-with-slash whose ring wobbles like jelly.
            IntPtr No() { var b = ProgressRenderer.ComposeNo(_settings, noE, noAng, fgBuf); CharmsBelow(b); return MakeCursor(b, l.HotX, l.HotY); }

            // Composites the waiting tools' logos onto a finished cursor bitmap when one or more
            // agents await the user. Two anchors (see the pendulum decls): CharmsTail hangs them off
            // the rotating tail like the "?" (helpAngleDeg is 180 at rest — its "?" hangs upside-down
            // — so logo swing = helpAngleDeg-180 keeps the logo upright at rest), for the arrow & hand;
            // CharmsBelow hangs them straight under the hotspot for cursors that don't rotate. Zero
            // cost when off / nothing waiting.
            void ApplyCharms(Bitmap bmp, float bobX, float bobY, float swingDeg)
            {
                var tools = _waitingTools;
                if (tools.Length == 0 || !_settings.AgentNotifierEnabled) return;
                using var g = Graphics.FromImage(bmp);
                NotifierRenderer.DrawCharms(g, _settings, tools, bobX, bobY, swingDeg);
            }
            void CharmsTail(Bitmap bmp) => ApplyCharms(bmp, ringCX, ringCY, helpAngleDeg - 180f);
            void CharmsBelow(Bitmap bmp) => ApplyCharms(bmp, belowCX, belowCY, belowDeg);

            // The pointer (arrow/hand) rendered live for the click-pop and/or with the hanging
            // agent logos. Without charms it returns the arrow-sized scaled frame (unchanged pop
            // behaviour); with charms it draws onto the full 2x canvas so the logos have room to
            // hang off the tail, hotspot still at the canvas centre.
            IntPtr PointerLive(Bitmap baseBmp, double baseAngleDeg, double deg, float scale, bool withCharms)
            {
                if (!withCharms) return RotatedScaled(baseBmp, baseAngleDeg, deg, scale);
                var bmp = fgBuf;
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    int a = baseBmp.Width;
                    g.TranslateTransform(l.HotX, l.HotY);
                    g.RotateTransform((float)(deg - baseAngleDeg));
                    g.ScaleTransform(scale, scale);
                    g.TranslateTransform(-a / 2f, -a / 2f);
                    g.DrawImage(baseBmp, 0, 0);
                }
                CharmsTail(bmp); // the pointer rotates → hang off the tail like the "?"
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

                // The "below" pendulum: same spring, but anchored at the HOTSPOT (not the rotating
                // tail), so the logo on a non-rotating cursor hangs straight under it and swings only
                // as the cursor translates — never orbiting. belowDeg is 0 (upright) at rest.
                {
                    float ax = p.x, ay = p.y;
                    if (!belowInit) { lbx = ax; lby = ay + belowDrop; lvbx = lvby = 0; belowInit = true; }
                    lvbx += ((ax - lbx) * pendK - lvbx * pendC) * dt;
                    lvby += ((ay - lby) * pendK + belowGravity - lvby * pendC) * dt;
                    lbx += lvbx * dt; lby += lvby * dt;
                    float dxn = lbx - ax, dyn = lby - ay;
                    float dlen = MathF.Sqrt(dxn * dxn + dyn * dyn);
                    if (dlen > belowMaxLen)
                    {
                        float nx = dxn / dlen, ny = dyn / dlen;
                        lbx = ax + nx * belowMaxLen; lby = ay + ny * belowMaxLen;
                        float vd = lvbx * nx + lvby * ny;
                        if (vd > 0) { lvbx -= vd * nx; lvby -= vd * ny; }
                    }
                    belowCX = Math.Clamp(l.HotX + (lbx - p.x), 0, l.Canvas);
                    belowCY = Math.Clamp(l.HotY + (lby - p.y), 0, l.Canvas);
                    belowDeg = (float)(Math.Atan2(lby - ay, lbx - ax) * 180.0 / Math.PI) - 90f;
                }

                // crosshair: advance the clock (breathing + ring spin) and the recoil
                // spring (the gap spreads with cursor speed, then snaps back to rest).
                tsec += dt;
                float spd = MathF.Sqrt((float)(dx * dx + dy * dy)) * _settings.Hz; // px/s
                float rtgt = MathF.Min(spd / recoilRef, 1f) * (sz * 0.105f);
                vrec += ((rtgt - recoil) * crossK - vrec * crossC) * dt;
                recoil += vrec * dt;
                if (recoil < 0) recoil = 0;
                crossGap = sz * ProgressRenderer.CrossBaseGap + sz * 0.0126f * MathF.Sin(tsec * MathF.PI * 2f * 0.6f) + recoil;
                ringDeg = tsec * 30f; // ~0.083 rev/s, slow

                // jelly I-beam: the top lags opposite to motion; soft/underdamped -> it wobbles
                float ibMaxSway = sz * 0.175f;
                float ibTgtX = 0, ibTgtY = 0;
                if (spd > 1f)
                {
                    float m = MathF.Min(spd / ibSwayRef, 1f) * ibMaxSway;
                    ibTgtX = -(dx * _settings.Hz / spd) * m;
                    ibTgtY = -(dy * _settings.Hz / spd) * m;
                }
                vibX += ((ibTgtX - ibOffX) * ibK - vibX * ibC) * dt;
                vibY += ((ibTgtY - ibOffY) * ibK - vibY * ibC) * dt;
                ibOffX += vibX * dt; ibOffY += vibY * dt;

                // taffy resize: stretch each axis by the |velocity component| along it (px/s),
                // through an underdamped spring so the waist necks out and blobs back.
                float vxs = dx * _settings.Hz, vys = dy * _settings.Hz; // signed px/s
                float tWE = MathF.Min(MathF.Abs(vxs) / rsRef, 1f);
                float tNS = MathF.Min(MathF.Abs(vys) / rsRef, 1f);
                float tD1 = MathF.Min(MathF.Abs(vxs + vys) * 0.70711f / rsRef, 1f); // along (1, 1)
                float tD2 = MathF.Min(MathF.Abs(vxs - vys) * 0.70711f / rsRef, 1f); // along (1,-1)
                vWE += ((tWE - rsWE) * rsK - vWE * rsC) * dt; rsWE += vWE * dt; if (rsWE < 0) rsWE = 0;
                vNS += ((tNS - rsNS) * rsK - vNS * rsC) * dt; rsNS += vNS * dt; if (rsNS < 0) rsNS = 0;
                vD1 += ((tD1 - rsD1) * rsK - vD1 * rsC) * dt; rsD1 += vD1 * dt; if (rsD1 < 0) rsD1 = 0;
                vD2 += ((tD2 - rsD2) * rsK - vD2 * rsC) * dt; rsD2 += vD2 * dt; if (rsD2 < 0) rsD2 = 0;

                // "no" jelly ring: stretch toward the travel direction (axis frozen while still),
                // underdamped so the egg overshoots and wobbles back to round when you stop.
                if (spd > 25f) noAng = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                float noTgt = MathF.Min(spd / noRef, 1f) * noMax;
                vno += ((noTgt - noE) * noK - vno * noC) * dt; noE += vno * dt;

                // click squash&pop: poll the mouse buttons, then spring popScale toward the
                // target (squashed while held, 1 at rest) — underdamped, so release overshoots.
                bool btnDown = clickFx && ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                                        || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0);
                float popTgt = btnDown ? popSquash : 1f;
                vPop += ((popTgt - popScale) * popK - vPop * popC) * dt;
                popScale += vPop * dt;
                if (!clickFx) { popScale = 1f; vPop = 0f; }

                // I-beam typing hop: consume a queued keystroke into an upward impulse, then
                // spring the offset back to 0 (underdamped -> it bounces). Cheap scalar math
                // every tick; Ibeam() reads hopY when it next re-renders the on-screen beam.
                if (_ibeamKick)
                {
                    _ibeamKick = false;
                    if (ibeamFx && tick - lastKickTick >= kickRefractory) // throttle so fast typing doesn't recoil
                    {
                        vHop = -hopKick; vHopX = hopDir * hopXKick; hopDir = -hopDir; // up + alternating side kick
                        lastKickTick = tick;
                    }
                }
                if (ibeamFx)
                {
                    vHop  += (-hopY * hopK  - vHop  * hopC)  * dt; hopY += vHop  * dt;
                    vHopX += (-hopX * hopXK - vHopX * hopXC) * dt; hopX += vHopX * dt;
                }
                else { hopY = 0f; vHop = 0f; hopX = 0f; vHopX = 0f; }

                // --- 4) apply cursors ---
                var test = _test;
                if (test != lastTest) { lastTest = test; normalDirty = true; curIdx = -1; }
                bool fgRender = tick % fgEvery == 0;

                if (test == TestCursor.Off)
                {
                    // While the click pop is in flight OR agents are waiting (hanging logos),
                    // render the pointer live (~60fps): scaled-about-hotspot for the pop, and/or with
                    // the agent logos (which swing, so re-render every frame). Otherwise fall back to
                    // the crisp pre-rendered direction frames — zero cost on an idle tray.
                    bool charmsActive = _settings.AgentNotifierEnabled && _waitingTools.Length > 0;
                    bool popActive = clickFx && (btnDown || MathF.Abs(popScale - 1f) > 0.004f || MathF.Abs(vPop) > 0.02f);
                    if (popActive || charmsActive)
                    {
                        int popScaleQ = (int)MathF.Round(popScale * 200f);
                        // Charms must re-render every frame to swing; a bare pop only when it moved.
                        if (fgRender && (charmsActive || idx != lastPopIdx || popScaleQ != lastPopScaleQ))
                        {
                            lastPopIdx = idx; lastPopScaleQ = popScaleQ;
                            double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                            float sc = clickFx ? popScale : 1f;
                            if (onArrow)
                            {
                                IntPtr h = PointerLive(_arrowBase!, 0.0, deg, sc, charmsActive);
                                SetSystemCursor(CopyIcon(h), OCR_UP);   // copy for the alternate-select slot first…
                                SetSystemCursor(h, OCR_NORMAL);          // …then hand off h itself (SetSystemCursor destroys it)
                            }
                            if (onHand) SetSystemCursor(PointerLive(_handBase!, HandShape.BaseAngleDeg, deg, sc, charmsActive), OCR_HAND);
                        }
                        curIdx = -1; normalDirty = true; // force a crisp re-apply once pop/charms end
                    }
                    else if (normalDirty || idx != curIdx)
                    {
                        curIdx = idx; normalDirty = false;
                        if (onArrow)
                        {
                            SetSystemCursor(CopyIcon(arrowFrames[idx]), OCR_NORMAL);
                            SetSystemCursor(CopyIcon(arrowFrames[idx]), OCR_UP); // alternate-select = same arrow
                        }
                        if (onHand) SetSystemCursor(CopyIcon(handFrames[idx]), OCR_HAND);
                    }
                    // Theme the busy slots once, then re-render them ONLY while a busy
                    // cursor is actually on screen (GetCursorInfo) — so an idle tray app
                    // doesn't burn CPU animating a cursor nobody is looking at.
                    if (!busyInit)
                    {
                        if (onWait)  SetSystemCursor(Busy(false), OCR_WAIT);
                        if (onApp)   SetSystemCursor(Busy(true), OCR_APPSTARTING);
                        if (onHelp)  SetSystemCursor(Help(), OCR_HELP);
                        if (onCross) SetSystemCursor(Cross(), OCR_CROSS);
                        if (onIbeam) SetSystemCursor(Ibeam(), OCR_IBEAM);
                        if (onWE)    SetSystemCursor(ResizeWE(), OCR_SIZEWE);
                        if (onNS)    SetSystemCursor(ResizeNS(), OCR_SIZENS);
                        if (onD1)    SetSystemCursor(ResizeD1(), OCR_SIZENWSE);
                        if (onD2)    SetSystemCursor(ResizeD2(), OCR_SIZENESW);
                        if (onMove)  SetSystemCursor(Move(), OCR_SIZEALL);
                        if (onNo)    SetSystemCursor(No(), OCR_NO);
                        busyInit = true;
                    }
                    else if (fgRender)
                    {
                        // Re-render an animated cursor ONLY while it's the one actually on
                        // screen, so an idle tray burns no CPU. Detect that by matching the
                        // displayed handle against the LIVE system handle for each slot
                        // (LoadCursor reflects the current system cursor table).
                        // NB: do NOT compare against the handles we passed to SetSystemCursor
                        // — that call DESTROYS them, so they never equal what GetCursorInfo
                        // reports (this is why these used to animate only via "Test cursor").
                        var ci = new CURSORINFO { cbSize = ciSize };
                        if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0)
                        {
                            IntPtr cur = ci.hCursor;
                            // Each branch is gated by its enable flag: a disabled slot still shows the
                            // Windows default (we never themed it), so there's nothing to re-render.
                            if (onWait && cur == hcWait) SetSystemCursor(Busy(false), OCR_WAIT);
                            else if (onApp && cur == hcApp) SetSystemCursor(Busy(true), OCR_APPSTARTING);
                            else if (onHelp && cur == hcHelp) SetSystemCursor(Help(), OCR_HELP);
                            else if (onCross && cur == hcCross) SetSystemCursor(Cross(), OCR_CROSS);
                            else if (onIbeam && cur == hcIbeam) SetSystemCursor(Ibeam(), OCR_IBEAM);
                            else if (onWE && cur == hcWE) SetSystemCursor(ResizeWE(), OCR_SIZEWE);
                            else if (onNS && cur == hcNS) SetSystemCursor(ResizeNS(), OCR_SIZENS);
                            else if (onD1 && cur == hcD1) SetSystemCursor(ResizeD1(), OCR_SIZENWSE);
                            else if (onD2 && cur == hcD2) SetSystemCursor(ResizeD2(), OCR_SIZENESW);
                            else if (onMove && cur == hcMove) SetSystemCursor(Move(), OCR_SIZEALL);
                            else if (onNo && cur == hcNo) SetSystemCursor(No(), OCR_NO);
                        }
                    }
                }
                else if (test == TestCursor.Arrow || test == TestCursor.Hand) // static (pops on click, for the demo)
                {
                    var frames = test == TestCursor.Hand ? handFrames : arrowFrames;
                    bool popActive = clickFx && (btnDown || MathF.Abs(popScale - 1f) > 0.004f || MathF.Abs(vPop) > 0.02f);
                    if (popActive)
                    {
                        int popScaleQ = (int)MathF.Round(popScale * 200f);
                        if (fgRender && (idx != lastPopIdx || popScaleQ != lastPopScaleQ))
                        {
                            lastPopIdx = idx; lastPopScaleQ = popScaleQ;
                            double deg = double.IsNaN(dispDeg) ? 0.0 : dispDeg;
                            var baseBmp = test == TestCursor.Hand ? _handBase! : _arrowBase!;
                            double baseAng = test == TestCursor.Hand ? HandShape.BaseAngleDeg : 0.0;
                            IntPtr h = RotatedScaled(baseBmp, baseAng, deg, popScale);
                            SetSystemCursor(CopyIcon(h), OCR_HAND);   // copy for the extra slots first…
                            SetSystemCursor(CopyIcon(h), OCR_UP);
                            SetSystemCursor(h, OCR_NORMAL);           // …then hand off h itself (SetSystemCursor destroys it)
                        }
                        curIdx = -1; normalDirty = true;              // crisp re-apply once the pop settles
                    }
                    else if (idx != curIdx || normalDirty)
                    {
                        curIdx = idx; normalDirty = false;
                        SetSystemCursor(CopyIcon(frames[idx]), OCR_NORMAL);
                        SetSystemCursor(CopyIcon(frames[idx]), OCR_HAND);
                        SetSystemCursor(CopyIcon(frames[idx]), OCR_UP);
                    }
                }
                else if (fgRender) // any composited test cursor: force it onto the visible slots
                {
                    IntPtr h = test switch
                    {
                        TestCursor.Cross => Cross(),
                        TestCursor.Help => Help(),
                        TestCursor.Ibeam => Ibeam(),
                        TestCursor.SizeWE => ResizeWE(),
                        TestCursor.SizeNS => ResizeNS(),
                        TestCursor.SizeNWSE => ResizeD1(),
                        TestCursor.SizeNESW => ResizeD2(),
                        TestCursor.SizeAll => Move(),
                        TestCursor.No => No(),
                        _ => Busy(test == TestCursor.AppStarting), // Wait / AppStarting
                    };
                    SetSystemCursor(h, OCR_NORMAL);
                    SetSystemCursor(CopyIcon(h), OCR_HAND);
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
        // Both bases are kept (not disposed): the arrow for busy-cursor compositing, and both
        // for re-rendering scaled-about-hotspot frames during the click squash&pop.
        _arrowBase = ArrowRenderer.DrawArrow(_settings);
        _handBase = HandRenderer.DrawHand(_settings);
        _managed = new[]
        {
            new Managed(OCR_NORMAL, BuildFrames(_arrowBase, 0.0, n)),
            new Managed(OCR_HAND, BuildFrames(_handBase, HandShape.BaseAngleDeg, n)),
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

    // One rotated + uniformly-scaled cursor frame from a base bitmap, scaled about the centre
    // (which is the hotspot, so it doesn't drift). Used per-frame during the click squash&pop;
    // the pre-rendered BuildFrames handles cover the steady (scale == 1) state.
    static IntPtr RotatedScaled(Bitmap baseBmp, double baseAngleDeg, double deg, float scale)
    {
        int size = baseBmp.Width, pivot = size / 2;
        using var rot = new Bitmap(size, size);
        using (var g = Graphics.FromImage(rot))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TranslateTransform(pivot, pivot);
            g.RotateTransform((float)(deg - baseAngleDeg));
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-pivot, -pivot);
            g.DrawImage(baseBmp, 0, 0);
        }
        return MakeCursor(rot, pivot, pivot);
    }

    // Scales a composed bitmap about its hotspot (no rotation) into a same-size cursor — used to
    // pulse the crosshair on click. scale ~= 1 is the cheap pass-through (no redraw).
    static IntPtr ScaledCursor(Bitmap src, int hotX, int hotY, float scale)
    {
        if (MathF.Abs(scale - 1f) < 0.001f) return MakeCursor(src, hotX, hotY);
        using var dst = new Bitmap(src.Width, src.Height);
        using (var g = Graphics.FromImage(dst))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TranslateTransform(hotX, hotY);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-hotX, -hotY);
            g.DrawImage(src, 0, 0);
        }
        return MakeCursor(dst, hotX, hotY);
    }

    void DestroyCursors()
    {
        foreach (var m in _managed)
            foreach (var h in m.Frames)
                if (h != IntPtr.Zero) DestroyIcon(h);
        _managed = Array.Empty<Managed>();
        _arrowBase?.Dispose();
        _arrowBase = null;
        _handBase?.Dispose();
        _handBase = null;
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
    const uint OCR_UP = 32516;        // "alternate select" — themed with the same rotating arrow
    const uint OCR_IBEAM = 32513;
    const uint OCR_CROSS = 32515;
    const uint OCR_WAIT = 32514;
    const uint OCR_APPSTARTING = 32650;
    const uint OCR_HELP = 32651;
    const uint OCR_HAND = 32649;
    const uint OCR_SIZENWSE = 32642;
    const uint OCR_SIZENESW = 32643;
    const uint OCR_SIZEWE = 32644;
    const uint OCR_SIZENS = 32645;
    const uint OCR_SIZEALL = 32646;
    const uint OCR_NO = 32648;
    const uint SPI_SETCURSORS = 0x0057;
    const int CURSOR_SHOWING = 0x00000001;
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02; // polled for the click squash&pop (no mouse hook)

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
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
