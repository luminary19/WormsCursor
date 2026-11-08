using System.Drawing;
using System.Drawing.Drawing2D;

namespace AgentsCursor.Core;

/// <summary>
/// Draws the agent-notifier token for a <see cref="TokenFace"/>: one waiting agent's icon (the one
/// <see cref="CursorSettings.IconFor"/> picks: its original logo — Claude Code's critter, Codex's OpenAI
/// knot, OpenCode's mark, Pi's Powerline pi — a mascot from <see cref="IconCatalog"/>, or an uploaded
/// SVG) as a bare, free-floating glyph, with the yellow "?"
/// <see cref="AttentionBadge"/> on its top-right when that session is blocked on you, and a small "+N"
/// count pill beside it when other sessions wait too.
///
/// This is UI-agnostic GDI+ drawing only — the host (the tray app's overlay window) decides where the
/// token lives and how it bounces, then calls <see cref="DrawToken"/> with the computed centre and
/// swing angle. There is intentionally never a fan of logos — multiple waiting agents collapse to one
/// logo plus a "+N" count, which stays legible at any size.
///
/// An instance owns the GDI+ objects it draws with (parsed icons + cached sprites), and GDI+ objects
/// are not thread-safe — so each drawing thread creates its own renderer (the overlay's animation
/// thread and the Preferences preview each hold one) and never shares it with another thread.
/// </summary>
public sealed class NotifierRenderer : IDisposable
{
    readonly TokenSprites _sprites = new();

    /// <summary>Draws <paramref name="face"/>'s logo in a box of side <paramref name="tokenSidePx"/>
    /// centred at (<paramref name="centerX"/>/<paramref name="centerY"/>) and tilted by
    /// <paramref name="swingDeg"/> (0 = upright), plus its badges: the "+N" pill when others wait and
    /// the "?" badge (drawn last, on top, and kept upright) when it is blocked on you. A thin contrast
    /// rim keeps the bare logo legible over any window behind it. Leaves no transform behind.</summary>
    public void DrawToken(Graphics g, CursorSettings s, TokenFace face,
                          float centerX, float centerY, float tokenSidePx, float swingDeg)
        => DrawToken(g, s, face, s.IconFor(face.Tool), centerX, centerY, tokenSidePx, swingDeg);

    /// <summary>As the other overload, but with <paramref name="iconId"/> instead of the icon the
    /// settings choose for the agent (the panel's icon gallery draws every choice).</summary>
    public void DrawToken(Graphics g, CursorSettings s, TokenFace face, string iconId,
                          float centerX, float centerY, float tokenSidePx, float swingDeg)
    {
        float box = Math.Max(8f, tokenSidePx);
        float edge = box * 0.06f;          // contrast rim so a bare logo reads on any background
        var square = new RectangleF(-box / 2f, -box / 2f, box, box);

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(centerX, centerY);
        g.RotateTransform(swingDeg);

        var icon = DrawIcon(g, face.Tool, iconId, square, edge);
        var (corner, radius) = AttentionBadge.Place(icon, box);
        if (face.Others > 0)
            DrawCount(g, face.Others, corner, face.Blocked ? radius : 0f, box, Parse(s.OutlineColor, Color.Black));
        if (face.Blocked)
            AttentionBadge.Draw(g, corner.X, corner.Y, radius);

        g.Restore(saved);
    }

    public void Dispose() => _sprites.Dispose();

    // The unknown-tool dot's diameter as a fraction of the box.
    const float DotRatio = 0.7f;

    // Blits the tool's icon sprite (fitted to the box, rim included) and returns the rectangle the
    // icon actually covers, which the badges anchor to. A tool without an icon gets a grey dot, so a
    // future or unmapped tool still shows something.
    RectangleF DrawIcon(Graphics g, string tool, string iconId, RectangleF box, float edge)
    {
        var sprite = _sprites.Get(tool, iconId, box.Width, edge);
        if (sprite is null)
        {
            float d = Math.Min(box.Width, box.Height) * DotRatio;
            var dot = new RectangleF(box.X + (box.Width - d) / 2f, box.Y + (box.Height - d) / 2f, d, d);
            using (var rim = new Pen(TokenSprites.RimFor(Color.Gray), edge))
                g.DrawEllipse(rim, dot);
            using (var fill = new SolidBrush(Color.Gray))
                g.FillEllipse(fill, dot);
            return dot;
        }
        // The swing rotation is already in g's transform, so the sprite rotates with it. It is padded
        // for its rim, so it goes back at the matching negative offset to put the icon in the box.
        var saved = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(sprite.Image, box.X - sprite.Pad, box.Y - sprite.Pad, sprite.Image.Width, sprite.Image.Height);
        g.InterpolationMode = saved;
        var v = sprite.Visual;
        return new RectangleF(box.X + v.X, box.Y + v.Y, v.Width, v.Height);
    }

    // The "+N" count for the other waiting sessions: white bold digits on a pill filled with the
    // theme outline colour, ringed in a light hairline and then a thin dark edge, so it reads as its own
    // shape on dark and light screens (the dark edge also parts it from dark logo strokes behind it). It
    // lives in the logo's top-right badge corner (the side facing the cursor, so it never swings out of
    // the canvas). Alone it is centred on that corner; beside the "?" badge (radius > 0) it tucks under
    // the badge's left edge, the digits centred in the part left showing, so the two read as one cluster.
    // The digits scale with the token but never drop below MinDigitsPx, where the "+" would blur into a dot.
    const float MinDigitsPx = 11f;
    const float DigitsRatio = 0.19f, PillRatio = 0.24f; // of the token side, before the floor

    static void DrawCount(Graphics g, int others, PointF corner, float badgeRadius, float box, Color backing)
    {
        float em = Math.Max(box * DigitsRatio, MinDigitsPx);
        using var text = new GraphicsPath();
        using (var fam = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            text.AddString("+" + others, fam, (int)FontStyle.Bold, em, PointF.Empty, fmt);
        var ink = text.GetBounds();

        float h = em * (PillRatio / DigitsRatio);
        float shown = Math.Max(h, ink.Width + h * 0.62f);     // the pill's visible length
        // Tucking a full pill height under the badge hides the pill's whole rounded end inside the disc.
        float tuck = badgeRadius > 0f ? h : 0f;
        float right = badgeRadius > 0f ? corner.X - badgeRadius + tuck : corner.X + h / 2f;
        float left = right - tuck - shown;
        float cy = corner.Y;
        // Centre the digits' ink (not the font's line box) in the visible part of the pill.
        using (var m = new Matrix())
        {
            m.Translate(left + shown / 2f - (ink.X + ink.Width / 2f), cy - (ink.Y + ink.Height / 2f));
            text.Transform(m);
        }

        using var pill = Pill(left, cy - h / 2f, right - left, h);
        float rimWidth = Math.Max(1f, box * 0.018f);
        using (var fill = new SolidBrush(backing))
            g.FillPath(fill, pill);
        using (var edge = new Pen(Color.FromArgb(170, 0, 0, 0), rimWidth + 2f)) // 1 px past the hairline
            g.DrawPath(edge, pill);
        using (var rim = new Pen(Color.FromArgb(230, 255, 255, 255), rimWidth))
            g.DrawPath(rim, pill);
        using (var digits = new SolidBrush(Color.White))
            g.FillPath(digits, text);
    }

    // A stadium (fully rounded rectangle).
    static GraphicsPath Pill(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        if (w <= h) { p.AddEllipse(x, y, w, h); return p; }
        p.AddArc(x, y, h, h, 90f, 180f);
        p.AddArc(x + w - h, y, h, h, 270f, 180f);
        p.CloseFigure();
        return p;
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
