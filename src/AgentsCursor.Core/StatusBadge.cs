using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgentsCursor.Core;

/// <summary>
/// The status badge on the token's top-right: a fluffy little cloud that says what the waiting agent
/// wants. A green cloud with a white check means it finished its turn; a yellow cloud with a "?" means
/// it is blocked on you (an approval, a question, an elicitation). Both share one cloud shape, so the
/// colour and the glyph are the whole difference.
///
/// Drawn in the icons' own style: a flat fill inside the same two-tone edge the token gives every icon
/// (a dark outline with a fine light halo outside it), so it reads on dark and light screens and sits
/// with the flat mascots rather than on top of them. Everything is vector geometry (overlapping puffs
/// filled as one shape, glyphs stroked with round caps), not a font, so it looks the same everywhere.
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
    /// token side. Hosts that keep the token on screen, like the corner placement, allow for this.</summary>
    public const float Overhang = RadiusRatio * (1f - Inset);

    // The cloud's puffs (centre x, centre y, radius) in unit-badge coordinates, y down: wider than it is
    // tall, like a cumulus. One big puff on top just right of centre with a smaller one beside it, a low
    // shoulder at each end, four small puffs along the bottom so it is soft and scalloped all the way
    // under rather than cut flat, and one in the middle to fill the body. Filled as one winding path they
    // make a single outline; with its edge it stays inside the unit square's width, so the badge reaches
    // no further than a unit disc would. The body leaves the glyph room on every side.
    static readonly (float X, float Y, float R)[] Puffs =
    {
        ( 0.13f, -0.28f, 0.43f),
        (-0.33f, -0.13f, 0.33f),
        ( 0.58f, -0.02f, 0.28f),
        (-0.61f,  0.07f, 0.26f),
        (-0.48f,  0.31f, 0.24f),
        (-0.17f,  0.39f, 0.26f),
        ( 0.17f,  0.39f, 0.26f),
        ( 0.48f,  0.31f, 0.24f),
        ( 0.00f,  0.07f, 0.48f),
    };
    // The edge is stroked around every puff and then the fill goes on top: the fill covers the inner
    // arcs and the inner half of each stroke, so only the outer half shows. These are the visible widths.
    const float Outline = 0.08f;   // the dark edge
    const float Halo = 0.05f;      // the light hairline outside it
    const float GlyphScale = 0.86f;

    static readonly Color Yellow = Color.FromArgb(0xFF, 0xCC, 0x33);
    static readonly Color Green = Color.FromArgb(0x3D, 0xC2, 0x5F);

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
    /// while staying level, so it reads at a glance. Leaves no state behind.</summary>
    public static void Draw(Graphics g, float cx, float cy, float radius, WaitingState state)
    {
        if (radius <= 0f) return;
        bool blocked = state == WaitingState.Blocked;
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float tilt;
        using (var current = g.Transform)
            tilt = MathF.Atan2(current.Elements[1], current.Elements[0]) * 180f / MathF.PI;
        g.TranslateTransform(cx, cy);
        g.RotateTransform(-tilt);           // undo the token's swing about the badge's own centre
        g.ScaleTransform(radius, radius);   // from here on, the badge lives in unit coordinates

        using (var cloud = Cloud())
        {
            using (var halo = new Pen(TokenSprites.RimLight, 2f * (Outline + Halo)))
                g.DrawPath(halo, cloud);
            using (var edge = new Pen(TokenSprites.RimDark, 2f * Outline))
                g.DrawPath(edge, cloud);
            using var fill = new SolidBrush(blocked ? Yellow : Green);
            g.FillPath(fill, cloud);
        }
        g.ScaleTransform(GlyphScale, GlyphScale);
        if (blocked) DrawQuestionMark(g, TokenSprites.RimDark);
        else DrawCheck(g, Color.White);

        g.Restore(saved);
    }

    // Every puff as one winding path, so the overlaps fill as a single shape.
    static GraphicsPath Cloud()
    {
        var path = new GraphicsPath(FillMode.Winding);
        foreach (var (x, y, r) in Puffs) path.AddEllipse(x - r, y - r, 2 * r, 2 * r);
        return path;
    }

    // The "?": a round hook (an arc over the top easing into a short stem) and a round dot, stroked
    // with round caps.
    static void DrawQuestionMark(Graphics g, Color glyph)
    {
        using var hook = new GraphicsPath();
        hook.AddArc(-0.24f, -0.46f, 0.48f, 0.48f, 165f, 215f);
        var end = hook.GetLastPoint();
        hook.AddBezier(end, new PointF(end.X - 0.04f, end.Y + 0.09f), new PointF(0f, 0.00f), new PointF(0f, 0.12f));
        using (var pen = new Pen(glyph, 0.23f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawPath(pen, hook);
        const float dotR = 0.135f, dotY = 0.42f;
        using (var dot = new SolidBrush(glyph))
            g.FillEllipse(dot, -dotR, dotY - dotR, 2 * dotR, 2 * dotR);
    }

    // The check: a short down-stroke and a long up-stroke with round caps and join.
    static void DrawCheck(Graphics g, Color glyph)
    {
        var points = new[] { new PointF(-0.38f, 0.04f), new PointF(-0.11f, 0.31f), new PointF(0.40f, -0.20f) };
        using var pen = new Pen(glyph, 0.24f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }
}
