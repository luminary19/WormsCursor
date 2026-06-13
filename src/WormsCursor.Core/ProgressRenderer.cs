using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the "progress / busy" cursor: an optional arrow plus a trailing comet ring of
/// dots. It's stateless — the engine owns the spin phase and the spring that drags the
/// ring behind movement, and passes the current ring centre + phase in here.
///
/// The canvas is square and the arrow glyph is drawn at its native <see cref="ArrowRenderer"/>
/// size, so the busy cursor matches the plain arrow's on-screen scale; the extra canvas
/// around it is transparent room for the ring to swing into.
/// </summary>
public static class ProgressRenderer
{
    public const int Dots = 8;
    public const float CrossBaseGap = 0.063f; // crosshair gap fraction at rest (engine adds breathing + recoil)

    /// <summary>Geometry for a given size: canvas, the arrow-tip hotspot, ring/dot radii
    /// and the tail length (tip → string anchor), all in canvas pixels.</summary>
    public readonly struct LayoutInfo
    {
        public readonly int Canvas, HotX, HotY;
        public readonly float RingR, DotR, TailLen;
        public LayoutInfo(int canvas, int hotX, int hotY, float ringR, float dotR, float tailLen)
        {
            Canvas = canvas; HotX = hotX; HotY = hotY;
            RingR = ringR; DotR = dotR; TailLen = tailLen;
        }
    }

    public static LayoutInfo Layout(CursorSettings s)
    {
        int sz = Math.Max(8, s.Size);
        // The ring hangs off the arrow's tail and can swing to any side, so the hotspot
        // (arrow tip) sits at the canvas centre with room all around. Clamp to 256
        // (Windows' practical custom-cursor limit) at very large sizes.
        int canvas = Math.Min((int)MathF.Round(sz * 2.0f), 256);
        int c = canvas / 2;
        return new LayoutInfo(canvas, c, c, sz * 0.16f, sz * 0.06f, sz * 0.40f);
    }

    /// <summary>Composites one busy frame. <paramref name="arrowDeg"/> rotates the arrow
    /// (0 = pointing right); <paramref name="ringX"/>/<paramref name="ringY"/> is the ring
    /// centre in canvas pixels; <paramref name="phase"/> spins the comet.</summary>
    public static Bitmap Compose(CursorSettings s, Bitmap arrowBase, double arrowDeg,
                                 float ringX, float ringY, float phase, bool withArrow)
    {
        var l = Layout(s);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (withArrow) DrawArrow(g, l, arrowBase, arrowDeg);

        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float pen = (float)(s.OutlineThickness * (s.Size / 64f));
        for (int d = 0; d < Dots; d++)
        {
            float ang = phase + d * (MathF.PI * 2f / Dots);
            float cx = ringX + MathF.Cos(ang) * l.RingR;
            float cy = ringY + MathF.Sin(ang) * l.RingR;
            float t = d / (float)Dots;               // 0 = head (big/opaque) -> tail
            float rr = l.DotR * (1f - 0.65f * t);
            int alpha = (int)(255 * (1f - 0.78f * t));
            using (var b = new SolidBrush(Color.FromArgb(alpha, fill)))
                g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            if (pen > 0.01f)
                using (var p = new Pen(Color.FromArgb(alpha, outline), pen))
                    g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        return bmp;
    }

    /// <summary>Composites the help cursor: the arrow plus a "?" glyph that hangs off the
    /// arrow's tail like the busy ring (same pendulum), but tilts with the string instead
    /// of spinning. <paramref name="glyphDeg"/> is the string angle (the engine sets it so
    /// the "?" hangs upside-down at rest and swings as the cursor moves).</summary>
    public static Bitmap ComposeHelp(CursorSettings s, Bitmap arrowBase, double arrowDeg,
                                     float qx, float qy, float glyphDeg)
    {
        var l = Layout(s);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        DrawArrow(g, l, arrowBase, arrowDeg);

        // Hand-drawn "?" built from primitives so we control the rounding: a rounded top
        // hook that curves down into a short vertical stem, plus a separate round dot below.
        int sz = Math.Max(8, s.Size);
        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float strokeW = sz * 0.05f;                              // stem / hook thickness
        float dotR = sz * 0.035f;                                // round dot radius
        float ob = (float)(s.OutlineThickness * (sz / 64f));     // outline border (each side)

        // Geometry in a local space (we'll re-centre on the combined bounds afterwards).
        // The hook is the upper curve of a "?": it opens at the lower-left, sweeps over the
        // top and comes down the right, then curls back to centre and turns into a short
        // vertical stem ending above the dot. Coordinates are tuned against a ~0.5*sz glyph.
        float r = sz * 0.085f;                                   // hook radius
        float topY = 0f;                                         // top of the hook arc
        float cxh = 0f;                                          // stem x (glyph centre line)
        using var hook = new GraphicsPath();
        // 1) Open top arc: from the lower-left opening, up and over the top, down to the
        //    right side at about mid-height. Symmetric control points give a clean round arc.
        hook.AddBezier(
            cxh - r * 0.95f, topY + r * 1.05f,                   // start (lower-left opening)
            cxh - r * 1.15f, topY - r * 0.55f,                   // control – out and up
            cxh + r * 1.15f, topY - r * 0.55f,                   // control – over the top
            cxh + r * 0.95f, topY + r * 1.05f);                  // end (right side, mid-height)
        // 2) Curl from the right side inward to the centre line, finishing with a vertical
        //    downward tangent so it flows into a straight stem (control directly above end).
        float stemEndY = topY + r * 2.45f;
        hook.AddBezier(
            cxh + r * 0.95f, topY + r * 1.05f,                   // continue from right side
            cxh + r * 0.70f, topY + r * 1.65f,                   // control – sweep down & in
            cxh, topY + r * 1.55f,                               // control – arrive at centre line
            cxh, stemEndY);                                      // end of stem (centred, above dot)

        float gap = sz * 0.0425f;                                // visible gap stem-end → dot
        float dotCx = cxh;                                       // dot centred under the stem
        float dotCy = stemEndY + gap + dotR;

        // Combined bounds = union of the stroked hook (path bounds grown by half the widest
        // pen) and the dot (grown by its outline). Centre the whole glyph on that union so
        // the existing translate-by-minus-centre keeps it positioned and rotated correctly.
        float half = strokeW / 2f + (ob > 0.01f ? ob : 0f);
        var hb = hook.GetBounds();
        float minX = MathF.Min(hb.X - half, dotCx - dotR - ob);
        float maxX = MathF.Max(hb.X + hb.Width + half, dotCx + dotR + ob);
        float minY = MathF.Min(hb.Y - half, dotCy - dotR - ob);
        float maxY = MathF.Max(hb.Y + hb.Height + half, dotCy + dotR + ob);
        float cX = (minX + maxX) / 2f, cY = (minY + maxY) / 2f;

        var st = g.Save();
        g.TranslateTransform(qx, qy);
        g.RotateTransform(glyphDeg);
        g.TranslateTransform(-cX, -cY); // centre the glyph on the bob

        // Outline-under / fill-over (mirrors ComposeCross): wide outline-coloured stroke
        // first, then the fill-coloured stroke on top; same for the dot's two circles.
        if (ob > 0.01f)
            using (var po = new Pen(outline, strokeW + 2 * ob)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawPath(po, hook);
        using (var pf = new Pen(fill, strokeW)
               { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawPath(pf, hook);

        if (ob > 0.01f)
            using (var bo = new SolidBrush(outline))
                g.FillEllipse(bo, dotCx - dotR - ob, dotCy - dotR - ob, (dotR + ob) * 2, (dotR + ob) * 2);
        using (var bf = new SolidBrush(fill))
            g.FillEllipse(bf, dotCx - dotR, dotCy - dotR, dotR * 2, dotR * 2);

        g.Restore(st);
        return bmp;
    }

    /// <summary>Composites the crosshair / precision cursor: a centre dot, four axis ticks
    /// and a slowly-rotating broken ring. <paramref name="gap"/> is the distance from the
    /// centre to where each tick starts (the engine breathes it and adds recoil when the
    /// cursor moves fast); <paramref name="ringDeg"/> rotates the outer ring. Hotspot is the
    /// centre (precision point). Lines are stroked outline-under/fill-over for the same
    /// filled-with-an-outline look as the other cursors.</summary>
    public static Bitmap ComposeCross(CursorSettings s, float gap, float ringDeg)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float cx = l.HotX, cy = l.HotY;
        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float ob = (float)(s.OutlineThickness * (sz / 64f)); // outline border (each side)
        float lineW = MathF.Max(sz * 0.049f, 2f);
        float tickLen = sz * 0.105f;
        float ringR = sz * 0.224f;

        // Draw a shape twice: a wider outline-coloured pen underneath, the fill on top.
        void Stroke(Action<Pen> draw)
        {
            if (ob > 0.01f)
                using (var po = new Pen(outline, lineW + 2 * ob) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    draw(po);
            using (var pf = new Pen(fill, lineW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                draw(pf);
        }

        Stroke(p =>
        {
            g.DrawLine(p, cx, cy - gap, cx, cy - gap - tickLen); // up
            g.DrawLine(p, cx, cy + gap, cx, cy + gap + tickLen); // down
            g.DrawLine(p, cx - gap, cy, cx - gap - tickLen, cy); // left
            g.DrawLine(p, cx + gap, cy, cx + gap + tickLen, cy); // right
        });
        Stroke(p =>
        {
            for (int i = 0; i < 4; i++)
                g.DrawArc(p, cx - ringR, cy - ringR, ringR * 2, ringR * 2, ringDeg + i * 90f + 12f, 66f);
        });

        float dr = MathF.Max(sz * 0.0245f, 1.25f); // centre dot
        if (ob > 0.01f)
            using (var bo = new SolidBrush(outline))
                g.FillEllipse(bo, cx - dr - ob, cy - dr - ob, (dr + ob) * 2, (dr + ob) * 2);
        using (var bf = new SolidBrush(fill))
            g.FillEllipse(bf, cx - dr, cy - dr, dr * 2, dr * 2);

        return bmp;
    }

    /// <summary>Composites the text / I-beam cursor as a flexible beam: the bottom is rigid
    /// (anchored at the hotspot) and the top sways by <paramref name="topOffX"/>/<paramref
    /// name="topOffY"/> — the engine drives that with an underdamped spring so the top wobbles
    /// like jelly when the cursor moves and settles afterwards. Bottom serif stays level; the
    /// top serif tilts with the bent tip. <paramref name="bounceY"/> shifts the whole glyph
    /// vertically (negative = up) for the typing "hop"; the hotspot stays at the canvas centre.</summary>
    public static Bitmap ComposeIbeam(CursorSettings s, float topOffX, float topOffY, float bounceY = 0f)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (bounceY != 0f) g.TranslateTransform(0f, bounceY); // typing hop: lift the beam, hotspot unchanged

        float cx = l.HotX, cy = l.HotY;
        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float ob = (float)(s.OutlineThickness * (sz / 64f));
        float beamW = MathF.Max(sz * 0.035f, 2f);
        float halfH = sz * 0.17f;
        float serif = sz * 0.065f;

        float bx = cx, by = cy + halfH;                       // rigid bottom anchor
        float tx = cx + topOffX, ty = cy - halfH + topOffY;   // swaying top
        float c2x = cx + topOffX * 0.55f, c2y = cy - halfH * 0.35f;

        // Cubic Bézier: vertical/centred tangent at the bottom (rigid lower section) bending
        // out to the swayed top, so the deflection grows toward the tip.
        using var beam = new GraphicsPath();
        beam.AddBezier(bx, by, cx, cy + halfH * 0.15f, c2x, c2y, tx, ty);

        // Top serif perpendicular to the beam's tip tangent (P3 - C2); bottom serif level.
        float tanx = tx - c2x, tany = ty - c2y;
        float tl = MathF.Sqrt(tanx * tanx + tany * tany);
        if (tl < 0.001f) { tanx = 0; tany = -1; tl = 1; }
        float px = -tany / tl, py = tanx / tl;

        void Stroke(Action<Pen> draw)
        {
            if (ob > 0.01f)
                using (var po = new Pen(outline, beamW + 2 * ob) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    draw(po);
            using (var pf = new Pen(fill, beamW) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                draw(pf);
        }
        Stroke(p =>
        {
            g.DrawPath(p, beam);
            g.DrawLine(p, bx - serif, by, bx + serif, by);                               // bottom serif (level)
            g.DrawLine(p, tx - px * serif, ty - py * serif, tx + px * serif, ty + py * serif); // top serif (tilts)
        });
        return bmp;
    }

    /// <summary>Builds the closed "taffy" double-arrow silhouette along <paramref name="angleDeg"/>,
    /// centred at (cx,cy): two pointed arrowheads joined by a liquid bar. <paramref name="stretch"/>
    /// (0 at rest, ~1 at full speed) pulls the heads apart, thins the waist and bulges the heads,
    /// so it reads like stretched taffy. Returned in world coords (already rotated).</summary>
    static GraphicsPath BuildTaffy(float cx, float cy, float angleDeg, float sz, float stretch, float baseFrac = 0.05f)
    {
        float st = MathF.Max(0f, stretch);
        float clamped = MathF.Min(st, 1f);
        float shaftHalf = sz * 0.044f;                         // shaft half-thickness at rest
        float waistHalf = shaftHalf * (1f - 0.62f * clamped);  // pinches thinner in the middle (taffy)
        float headHalfW = sz * 0.115f;                         // arrowhead half-base (narrow -> sharp, arrow-like)
        float headLen = sz * 0.175f;                           // arrowhead length (long -> sharp)
        float bs = sz * (baseFrac + 0.13f * st);               // half shaft length — the heads slide apart with stretch
        float tip = bs + headLen;
        float k = bs * 0.5f;                                   // shaft-neck bezier control offset

        // Points in a local frame (x = along the axis), rotated by angleDeg about (cx,cy).
        float ca = MathF.Cos(angleDeg * MathF.PI / 180f), sa = MathF.Sin(angleDeg * MathF.PI / 180f);
        PointF P(float x, float y) => new(cx + x * ca - y * sa, cy + x * sa + y * ca);

        // A barbed double-arrow: thin shaft (bowed inward when stretched) between two clear
        // triangular arrowheads. Walked clockwise from the left tip.
        var p = new GraphicsPath();
        p.AddLine(P(-tip, 0), P(-bs, -headHalfW));                                            // left head, top slope
        p.AddLine(P(-bs, -headHalfW), P(-bs, -shaftHalf));                                    // left barb notch (in)
        p.AddBezier(P(-bs, -shaftHalf), P(-k, -shaftHalf), P(-k, -waistHalf), P(0, -waistHalf)); // shaft top, neck in
        p.AddBezier(P(0, -waistHalf), P(k, -waistHalf), P(k, -shaftHalf), P(bs, -shaftHalf));     // shaft top, neck out
        p.AddLine(P(bs, -shaftHalf), P(bs, -headHalfW));                                      // right barb notch (out)
        p.AddLine(P(bs, -headHalfW), P(tip, 0));                                              // right head, top slope
        p.AddLine(P(tip, 0), P(bs, headHalfW));                                               // right head, bottom slope
        p.AddLine(P(bs, headHalfW), P(bs, shaftHalf));                                        // right barb notch (in)
        p.AddBezier(P(bs, shaftHalf), P(k, shaftHalf), P(k, waistHalf), P(0, waistHalf));         // shaft bottom, neck in
        p.AddBezier(P(0, waistHalf), P(-k, waistHalf), P(-k, shaftHalf), P(-bs, shaftHalf));       // shaft bottom, neck out
        p.AddLine(P(-bs, shaftHalf), P(-bs, headHalfW));                                      // left barb notch (out)
        p.AddLine(P(-bs, headHalfW), P(-tip, 0));                                             // left head, bottom slope
        p.CloseFigure();
        return p;
    }

    // Filled-with-outline look for one or more closed bodies: stroke every path with a wide
    // outline-coloured pen (straddling the edge) FIRST, then fill them all on top. Doing all
    // strokes before all fills keeps the union outline clean where two bodies overlap (the
    // move cursor) — the fills cover any outline segment that fell inside the union.
    static void FillOutline(Graphics g, CursorSettings s, float ob, params GraphicsPath[] paths)
    {
        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        if (ob > 0.01f)
            using (var po = new Pen(outline, 2f * ob) { LineJoin = LineJoin.Round })
                foreach (var path in paths) g.DrawPath(po, path);
        using (var bf = new SolidBrush(fill))
            foreach (var path in paths) g.FillPath(bf, path);
    }

    /// <summary>Composites a bi-directional resize cursor as stretched taffy along
    /// <paramref name="angleDeg"/> (0 = ↔, 90 = ↕, 45 = ↘↖, -45 = ↗↙). Hotspot is the centre;
    /// <paramref name="stretch"/> is the engine's spring value (0 at rest).</summary>
    public static Bitmap ComposeResize(CursorSettings s, float angleDeg, float stretch)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float ob = (float)(s.OutlineThickness * (sz / 64f));
        using var path = BuildTaffy(l.HotX, l.HotY, angleDeg, sz, stretch);
        FillOutline(g, s, ob, path);
        return bmp;
    }

    /// <summary>Composites the move cursor (OCR_SIZEALL): a horizontal and a vertical taffy arrow
    /// crossed into a 4-way glyph. <paramref name="stretchX"/>/<paramref name="stretchY"/> stretch
    /// the horizontal / vertical arms with motion along that axis. Hotspot is the centre.</summary>
    public static Bitmap ComposeMove(CursorSettings s, float stretchX, float stretchY)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float ob = (float)(s.OutlineThickness * (sz / 64f));
        // Spread the four arms (a longer rest shaft) so they read as a 4-way cross with a clear
        // centre instead of merging into a diamond when crossed.
        using var h = BuildTaffy(l.HotX, l.HotY, 0f, sz, stretchX, 0.15f);
        using var v = BuildTaffy(l.HotX, l.HotY, 90f, sz, stretchY, 0.15f);
        FillOutline(g, s, ob, h, v);
        return bmp;
    }

    /// <summary>Composites the "unavailable" cursor (OCR_NO): a red circle-with-slash whose
    /// ring is a soft jelly blob — at rest it's perfectly round, but the engine deforms it into
    /// an egg/oval along the direction of travel (<paramref name="deform"/> = signed stretch,
    /// <paramref name="axisDeg"/> = its axis) and wobbles it back. The diagonal slash stays put.</summary>
    public static Bitmap ComposeNo(CursorSettings s, float deform, float axisDeg)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float cx = l.HotX, cy = l.HotY;
        var outline = Parse(s.OutlineColor, Color.Black);
        var red = Color.FromArgb(222, 50, 50);                   // prohibition red (this cursor's accent)
        float ob = (float)(s.OutlineThickness * (sz / 64f));
        float ringR = sz * 0.20f;
        float ringW = MathF.Max(sz * 0.052f, 2f);
        float rx = ringR * (1f + deform);                        // stretched along the motion axis
        float ry = ringR * (1f - deform * 0.7f);                 // squashed across it (jelly)

        void Stroke(Action<Pen> draw)
        {
            if (ob > 0.01f)
                using (var po = new Pen(outline, ringW + 2 * ob) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    draw(po);
            using (var pf = new Pen(red, ringW) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                draw(pf);
        }

        // Slash first (it sits UNDER the ring), in world space so it keeps its fixed diagonal as
        // the ring wobbles. Then the ring on top, as an ellipse rotated to the motion axis
        // (uniform pen → even stroke).
        float h = ringR * 0.98f, c45 = 0.70711f;                 // slash along ↘ (top-left → bottom-right)
        Stroke(p => g.DrawLine(p, cx - c45 * h, cy - c45 * h, cx + c45 * h, cy + c45 * h));

        var st = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(axisDeg);
        Stroke(p => g.DrawEllipse(p, -rx, -ry, rx * 2, ry * 2));
        g.Restore(st);
        return bmp;
    }

    /// <summary>Renders a static "at rest" frame of a composited cursor, for the Preferences
    /// preview (arrow pointing right; ring/glyph hanging straight down off the tail; reticle
    /// at its rest gap). Arrow/Hand are drawn by their own renderers, not here.</summary>
    public static Bitmap RenderRest(CursorSettings s, TestCursor kind, Bitmap arrowBase)
    {
        var l = Layout(s);
        int sz = Math.Max(8, s.Size);
        float rx = l.HotX - l.TailLen, ry = l.HotY + sz * 0.30f; // bob hangs below the tail at rest
        return kind switch
        {
            TestCursor.Wait => Compose(s, arrowBase, 0, l.HotX, l.HotY, 0f, false),
            TestCursor.Help => ComposeHelp(s, arrowBase, 0, rx, ry, 180f),
            TestCursor.Cross => ComposeCross(s, sz * CrossBaseGap, 0f),
            TestCursor.Ibeam => ComposeIbeam(s, 0f, 0f),
            TestCursor.SizeWE => ComposeResize(s, 0f, 0f),
            TestCursor.SizeNS => ComposeResize(s, 90f, 0f),
            TestCursor.SizeNWSE => ComposeResize(s, 45f, 0f),
            TestCursor.SizeNESW => ComposeResize(s, -45f, 0f),
            TestCursor.SizeAll => ComposeMove(s, 0f, 0f),
            TestCursor.No => ComposeNo(s, 0f, 0f),
            _ => Compose(s, arrowBase, 0, rx, ry, 0f, true), // AppStarting
        };
    }

    // Draws the arrow rotated about its tip (the canvas-centre hotspot), at native size.
    static void DrawArrow(Graphics g, LayoutInfo l, Bitmap arrowBase, double arrowDeg)
    {
        int a = arrowBase.Width;                 // arrow tip sits at its own centre
        var st = g.Save();
        g.TranslateTransform(l.HotX, l.HotY);
        g.RotateTransform((float)arrowDeg);
        g.TranslateTransform(-a / 2f, -a / 2f);
        g.DrawImage(arrowBase, 0, 0);
        g.Restore(st);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
