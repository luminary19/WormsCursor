using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the agent-notifier token: a single waiting tool's logo (Claude Code's critter, Codex's
/// OpenAI knot — see <see cref="AgentLogos"/>) as a bare, free-floating glyph, with a small "+N"
/// superscript when more than one agent is waiting.
///
/// This is UI-agnostic GDI+ drawing only — the host (the tray app's overlay window) decides where the
/// token lives and how it bounces, then calls <see cref="DrawToken"/> with the computed centre and
/// swing angle. There is intentionally never a fan of logos — multiple waiting agents collapse to one
/// logo plus a "+N" count, which stays legible at any size.
/// </summary>
public static class NotifierRenderer
{
    /// <summary>Draws a single logo (the first entry in <paramref name="tools"/>, tool ids like
    /// "claude-code" / "codex") of side <paramref name="tokenSidePx"/> centred at
    /// (<paramref name="centerX"/>/<paramref name="centerY"/>) and tilted by <paramref name="swingDeg"/>
    /// (0 = upright). When more than one agent waits, a small "+N" superscript (N = the rest) sits at
    /// the logo's top-right. A thin contrast rim keeps the bare logo legible over any window behind it.
    /// Leaves no transform behind.</summary>
    public static void DrawToken(Graphics g, CursorSettings s, IReadOnlyList<string> tools,
                                 float centerX, float centerY, float tokenSidePx, float swingDeg)
    {
        if (tools is null || tools.Count == 0) return;
        int count = tools.Count;
        float box = Math.Max(8f, tokenSidePx);
        float edge = box * 0.06f;          // contrast rim so a bare logo reads on any background

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(centerX, centerY);
        g.RotateTransform(swingDeg);

        // Always exactly one logo (the most recent waiting tool); never a fan.
        AgentLogos.Draw(g, tools[0], new RectangleF(-box / 2f, -box / 2f, box, box), edge);
        if (count > 1)
        {
            var outline = Parse(s.OutlineColor, Color.Black);
            DrawPlusN(g, count - 1, box * 0.40f, -box * 0.40f, box, outline);
        }

        g.Restore(saved);
    }

    // A small outlined "+N" (white fill / theme outline, like the other glyphs) standing in for the
    // other waiting agents — a frameless superscript at the single logo's top-right corner.
    static void DrawPlusN(Graphics g, int extra, float cx, float cy, float box, Color outline)
    {
        using var path = new GraphicsPath();
        float em = box * 0.26f;
        using var fam = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        path.AddString("+" + extra, fam, (int)FontStyle.Bold, em, new PointF(cx, cy), fmt);

        using (var po = new Pen(outline, Math.Max(1.5f, box * 0.045f)) { LineJoin = LineJoin.Round })
            g.DrawPath(po, path);
        using (var bf = new SolidBrush(Color.White))
            g.FillPath(bf, path);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
