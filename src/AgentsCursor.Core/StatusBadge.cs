using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgentsCursor.Core;

/// <summary>
/// The status badge on the token's top-right: a fluffy little cloud that says what the waiting agent
/// wants. A green cloud with a white check means it finished its turn; a yellow cloud with a "?" means
/// it is blocked on you (an approval, a question, an elicitation). Both share one cloud shape, so the
/// colour and the glyph are the whole difference.
///
/// Drawn by <see cref="NotifierRenderer"/> on every agent's icon, built-in or not, anchored to the
/// icon's visible bounds. Everything is vector geometry (overlapping puffs filled as one shape, glyphs
/// stroked with round caps), not a font, so it looks the same on every machine.
///
/// Stateless: every call creates and disposes its own GDI+ objects, so it is safe on any thread.
/// </summary>
public static class StatusBadge
{
    /// <summary>Badge radius as a fraction of the token side, so it scales with the token. The cloud
    /// spans about two radii across, a little less from top to bottom.</summary>
    public const float RadiusRatio = 0.25f;

    /// <summary>How far the badge's centre sits inside the logo's top-right corner, as a fraction of
    /// the badge radius. None: the centre is on the corner, so a quarter of the cloud overlaps the logo,
    /// like an app's notification badge, and the logo's own features keep clear of it. (The icons keep
    /// that corner, and the band along the top where the "+N" pill tucks in, free of faces.)</summary>
    const float Inset = 0f;

    /// <summary>How far the badge reaches past its logo's top and right edges, as a fraction of the
    /// token side (the cloud only; the soft shadow fades out just beyond it). Hosts that keep the
    /// token on screen, like the corner placement, allow for this.</summary>
    public const float Overhang = RadiusRatio * (1f - Inset);

    // The cloud's puffs (centre x, centre y, radius) in unit-badge coordinates, y down: a row of three
    // along a gently rounded base, and two bigger ones piled on top, the right one tallest. Filled as one
    // winding path they make a single seamless outline; with the rim added it stays inside the unit
    // square's width, so the badge reaches no further than a unit disc did.
    static readonly (float X, float Y, float R)[] Puffs =
    {
        (-0.56f, 0.20f, 0.33f),
        ( 0.00f, 0.27f, 0.38f),
        ( 0.56f, 0.20f, 0.33f),
        (-0.28f, -0.14f, 0.40f),
        ( 0.22f, -0.26f, 0.47f),
    };
    const float RimWidth = 0.11f;

    // Each look: the rim (a deeper tone of the cloud, so it reads on light screens), the face's gradient
    // top to bottom, and the glyph colour.
    sealed record Look(Color Rim, Color Top, Color Bottom, Color Glyph);
    static readonly Look Done = new(Color.FromArgb(0x1E, 0x7E, 0x3A), Color.FromArgb(0xA6, 0xEE, 0xAE), Color.FromArgb(0x3C, 0xBE, 0x5C), Color.White);
    static readonly Look Question = new(Color.FromArgb(0xC2, 0x82, 0x00), Color.FromArgb(0xFF, 0xEE, 0x9A), Color.FromArgb(0xFF, 0xC4, 0x26), Color.FromArgb(0x6E, 0x45, 0x00));

    /// <summary>Where the badge goes for a logo drawn in <paramref name="logo"/> on a token of side
    /// <paramref name="tokenSide"/>: its centre near the logo's top-right corner and its radius.</summary>
    public static (PointF Center, float Radius) Place(RectangleF logo, float tokenSide)
    {
        float r = tokenSide * RadiusRatio;
        return (new PointF(logo.Right - r * Inset, logo.Top + r * Inset), r);
    }

    /// <summary>Draws the badge for <paramref name="state"/> centred at (<paramref name="cx"/>,
    /// <paramref name="cy"/>) with radius <paramref name="radius"/>, both in the current transform of
    /// <paramref name="g"/>, but always upright: on a swinging token the cloud rides the logo's corner
    /// while staying level, so it reads at a glance. Its soft shadow falls a little downward on screen.
    /// Leaves no state behind.</summary>
    public static void Draw(Graphics g, float cx, float cy, float radius, WaitingState state)
    {
        if (radius <= 0f) return;
        var look = state == WaitingState.Blocked ? Question : Done;
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float tilt;
        using (var current = g.Transform)
            tilt = MathF.Atan2(current.Elements[1], current.Elements[0]) * 180f / MathF.PI;
        g.TranslateTransform(cx, cy);
        g.RotateTransform(-tilt);           // undo the token's swing about the badge's own centre
        g.ScaleTransform(radius, radius);   // from here on, the badge lives in unit coordinates

        using (var outline = Cloud(RimWidth))
        {
            DrawShadow(g, outline);
            using var rim = new SolidBrush(look.Rim);
            g.FillPath(rim, outline);
        }
        using (var face = Cloud(0f))
        {
            var bounds = face.GetBounds();
            using var fill = new LinearGradientBrush(new PointF(0, bounds.Top), new PointF(0, bounds.Bottom), look.Top, look.Bottom);
            g.FillPath(fill, face);
            DrawHighlight(g, face);
        }
        if (state == WaitingState.Blocked) DrawQuestionMark(g, look.Glyph);
        else DrawCheck(g, look.Glyph, look.Rim);

        g.Restore(saved);
    }

    // The cloud's outline grown by <grow>: every puff as one winding path, so overlaps fill as a union.
    static GraphicsPath Cloud(float grow)
    {
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var (x, y, r) in Puffs)
        {
            float rr = r + grow;
            path.AddEllipse(x - rr, y - rr, 2 * rr, 2 * rr);
        }
        return path;
    }

    // A soft shadow: the outline nudged down in small steps at very low opacity, so it builds up under
    // the cloud and fades out at its edge instead of cutting a band.
    static void DrawShadow(Graphics g, GraphicsPath outline)
    {
        using var shade = new SolidBrush(Color.FromArgb(20, 0, 0, 0));
        foreach (float dy in new[] { 0.03f, 0.06f, 0.09f, 0.12f, 0.15f })
        {
            var state = g.Save();
            g.TranslateTransform(0f, dy);
            g.FillPath(shade, outline);
            g.Restore(state);
        }
    }

    // Fluff: each upper puff catches a little light on its top, a soft white wash that fades to clear
    // towards the puff's middle, clipped to the cloud.
    static void DrawHighlight(Graphics g, GraphicsPath face)
    {
        var state = g.Save();
        g.SetClip(face);
        foreach (var (x, y, r) in Puffs.Where(p => p.Y < 0f))
        {
            var top = new RectangleF(x - r * 0.8f, y - r * 0.95f, r * 1.6f, r * 1.1f);
            using var wash = new LinearGradientBrush(new PointF(0, top.Top), new PointF(0, top.Bottom),
                Color.FromArgb(110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            g.FillEllipse(wash, top);
        }
        g.Restore(state);
    }

    // The "?": a round, generous hook (an arc over the top easing into a short stem) and a big dot,
    // stroked thick with round caps so it reads soft rather than sharp. Sits a touch low, in the body
    // of the cloud.
    static void DrawQuestionMark(Graphics g, Color glyph)
    {
        const float stroke = 0.27f;
        const float hookR = 0.27f, hookCy = -0.16f;
        using var hook = new GraphicsPath();
        // GDI+ angles run clockwise from +x (y points down): start below the left side, sweep over the
        // top and stop below the right side.
        hook.AddArc(-hookR, hookCy - hookR, 2 * hookR, 2 * hookR, 160f, 220f);
        var end = hook.GetLastPoint();
        hook.AddBezier(end, new PointF(end.X - 0.05f, end.Y + 0.10f), new PointF(0f, 0.05f), new PointF(0f, 0.17f));

        using (var pen = new Pen(glyph, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawPath(pen, hook);
        const float dotR = 0.16f, dotY = 0.49f;
        using (var dot = new SolidBrush(glyph))
            g.FillEllipse(dot, -dotR, dotY - dotR, 2 * dotR, 2 * dotR);
    }

    // The check: a short down-stroke and a long up-stroke with round caps and join, drawn white over a
    // slightly wider stroke in the rim's tone so its edge stays crisp on the light top of the cloud.
    static void DrawCheck(Graphics g, Color glyph, Color edge)
    {
        var points = new[] { new PointF(-0.42f, 0.06f), new PointF(-0.12f, 0.36f), new PointF(0.42f, -0.22f) };
        using (var under = new Pen(Color.FromArgb(150, edge), 0.37f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(under, points);
        using (var pen = new Pen(glyph, 0.26f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(pen, points);
    }
}
