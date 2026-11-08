using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// The "blocked on you" badge: a warm-yellow disc with a bold "?" that sits on the top-right of a
/// tool logo, like an app's notification badge. A plain logo means the agent simply finished its
/// turn; the badge means it is waiting for your answer (an approval, a question, an elicitation).
///
/// Shared by the tray overlay (<see cref="NotifierRenderer"/>) and the standalone OpenCode helper,
/// which compiles this file in, so both draw the identical badge. The "?" is vector geometry (an arc,
/// a curve and a dot stroked with round caps), not a font glyph, so it looks the same on every machine.
///
/// Stateless: every call creates and disposes its own GDI+ objects, so it is safe on any thread.
/// </summary>
public static class AttentionBadge
{
    /// <summary>Badge radius as a fraction of the token side, so it scales with the token.</summary>
    public const float RadiusRatio = 0.22f;

    /// <summary>How far the badge's centre sits inside the logo's top-right corner, as a fraction of
    /// the badge radius: enough to overlap the logo like an app badge, little enough to keep clear
    /// of the logo's features (the Claude critter's right eye).</summary>
    const float Inset = 0.12f;

    /// <summary>How far the badge reaches past its logo's top and right edges, as a fraction of the
    /// token side (the disc only; the soft shadow fades out just beyond it). Hosts that keep the
    /// token on screen, like the corner placement, allow for this.</summary>
    public const float Overhang = RadiusRatio * (1f - Inset);

    static readonly Color Shadow = Color.FromArgb(110, 0, 0, 0);
    static readonly Color Rim = Color.FromArgb(0x9A, 0x5C, 0x00);
    static readonly Color FillTop = Color.FromArgb(0xFF, 0xE0, 0x5A);
    static readonly Color FillBottom = Color.FromArgb(0xFF, 0xB8, 0x0A);
    static readonly Color Glyph = Color.FromArgb(0x2E, 0x1E, 0x00);

    /// <summary>Where the badge goes for a logo drawn in <paramref name="logo"/> on a token of side
    /// <paramref name="tokenSide"/>: its centre near the logo's top-right corner and its radius.</summary>
    public static (PointF Center, float Radius) Place(RectangleF logo, float tokenSide)
    {
        float r = tokenSide * RadiusRatio;
        return (new PointF(logo.Right - r * Inset, logo.Top + r * Inset), r);
    }

    /// <summary>Draws the badge centred at (<paramref name="cx"/>, <paramref name="cy"/>) with radius
    /// <paramref name="radius"/>, in the current transform of <paramref name="g"/> (so it rotates with
    /// the token). Its soft shadow spills about 20% past the radius. Leaves no state behind.</summary>
    public static void Draw(Graphics g, float cx, float cy, float radius)
    {
        if (radius <= 0f) return;
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(radius, radius);   // from here on, the badge is a unit disc

        DrawShadow(g);
        using (var rim = new SolidBrush(Rim))
            g.FillEllipse(rim, -1f, -1f, 2f, 2f);
        const float face = 0.88f;
        using (var fill = new LinearGradientBrush(new PointF(0, -face), new PointF(0, face), FillTop, FillBottom))
            g.FillEllipse(fill, -face, -face, 2 * face, 2 * face);
        DrawQuestionMark(g);

        g.Restore(saved);
    }

    // A soft, slightly lowered shadow: a radial fade from translucent black to clear.
    static void DrawShadow(Graphics g)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(-1.2f, -1.12f, 2.4f, 2.4f);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(0f, 0.08f),
            CenterColor = Shadow,
            SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) },
            FocusScales = new PointF(0.72f, 0.72f),
        };
        g.FillPath(brush, path);
    }

    // The "?" in unit-disc coordinates: a hook (an arc over the top that curls into a short stem)
    // stroked with round caps, plus a round dot.
    static void DrawQuestionMark(Graphics g)
    {
        const float stroke = 0.23f;
        const float hookR = 0.28f, hookCy = -0.22f;
        using var hook = new GraphicsPath();
        // GDI+ angles run clockwise from +x (y points down): start just below the left side,
        // sweep over the top and stop below the right side.
        hook.AddArc(-hookR, hookCy - hookR, 2 * hookR, 2 * hookR, 168f, 222f);
        var end = hook.GetLastPoint();
        // Leave the arc along its own tangent and ease into a vertical stem: one smooth S-curve.
        const float lead = 0.13f;
        float a = 30f * MathF.PI / 180f;
        hook.AddBezier(end, new PointF(end.X - lead * MathF.Sin(a), end.Y + lead * MathF.Cos(a)),
                       new PointF(0f, -0.02f), new PointF(0f, 0.12f));
        hook.AddLine(0f, 0.12f, 0f, 0.16f);

        using (var pen = new Pen(Glyph, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawPath(pen, hook);
        const float dotR = stroke * 0.64f, dotY = 0.44f;
        using (var dot = new SolidBrush(Glyph))
            g.FillEllipse(dot, -dotR, dotY - dotR, 2 * dotR, 2 * dotR);
    }
}
