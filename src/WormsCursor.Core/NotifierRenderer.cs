using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the agent-notifier indicator: a little garland of tool-logo charms that <b>hang and swing
/// off the bottom of the cursor</b> when one or more AI agents are waiting for you. Each charm is a
/// small rounded tile bearing the waiting tool's own logo (Claude Code's critter, Codex's OpenAI
/// knot — see <see cref="AgentLogos"/>), so you can tell at a glance which tool needs you.
///
/// It's stateless — the engine owns the pendulum (a bob on a springy string, simulated in world
/// coords so the charms swing when the cursor moves and settle straight down at rest) and passes in
/// the anchor (cursor hotspot), the bob position, and the string angle, exactly like
/// <see cref="ProgressRenderer.ComposeHelp"/> does for the "?". A visible thread runs from the
/// hotspot down to the hub so the cluster reads as genuinely <i>suspended</i> from the cursor.
///
/// One tile per waiting agent up to <c>cap</c>; beyond that, <c>cap</c> tiles plus a small "+N" so
/// the exact number stays legible. Composited onto whatever cursor bitmap is on screen, so every
/// themed cursor carries it uniformly. The cluster lives in the canvas padding below the hotspot, so
/// it never disturbs the hotspot.
/// </summary>
public static class NotifierRenderer
{
    /// <summary>Draws one logo charm per entry in <paramref name="tools"/> (each is a tool id like
    /// "claude-code" / "codex"), hanging from the pendulum bob (<paramref name="bobX"/>/<paramref
    /// name="bobY"/>, canvas px) on a thread that starts at the cursor hotspot (<paramref
    /// name="anchorX"/>/<paramref name="anchorY"/>). The cluster swings by <paramref name="stringDeg"/>
    /// (0 = hanging straight down). <paramref name="cap"/> bounds how many tiles are drawn before the
    /// count switches to a "+N" tag. Draws into <paramref name="g"/> without leaving a transform
    /// behind.</summary>
    public static void DrawCharms(Graphics g, CursorSettings s, IReadOnlyList<string> tools,
                                  float anchorX, float anchorY, float bobX, float bobY, float stringDeg, int cap)
    {
        if (tools is null || tools.Count == 0) return;
        int count = tools.Count;
        int sz = Math.Max(8, s.Size);
        cap = Math.Clamp(cap, 1, 6);
        int shown = Math.Min(count, cap);

        var outline = Parse(s.OutlineColor, Color.Black);
        var tileFill = Color.White;                  // a light pad so the brand logos always read
        float ob = (float)(s.OutlineThickness * (sz / 64f));

        float tile = sz * 0.42f;                      // charm tile edge
        float radius = tile * 0.30f;                  // rounded corners
        float spacing = tile * 1.12f;                 // gap between tiles in the fan
        float hubGap = tile * 0.20f;                  // gap between the hub (bob) and the tile tops
        float tileCY = hubGap + tile / 2f;            // tile centre, below the hub

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // The thread that makes it read as "hanging": hotspot -> bob, in canvas space.
        using (var thread = new Pen(Color.FromArgb(210, outline), Math.Max(1.2f, sz * 0.022f)))
            g.DrawLine(thread, anchorX, anchorY, bobX, bobY);

        g.TranslateTransform(bobX, bobY);
        g.RotateTransform(stringDeg);                 // the whole cluster tilts as it swings

        float x0 = -(shown - 1) * spacing / 2f;
        using (var sub = new Pen(Color.FromArgb(210, outline), Math.Max(1f, sz * 0.018f)))
            for (int i = 0; i < shown; i++)
                g.DrawLine(sub, 0, 0, x0 + i * spacing, tileCY - tile / 2f); // hub -> each tile top

        for (int i = 0; i < shown; i++)
            DrawTile(g, tools[i], x0 + i * spacing, tileCY, tile, radius, tileFill, outline, ob);

        // Overflow: a count badge on the corner of the last tile (drawing "+N" below would fall off
        // the cursor's small canvas). Shows the true total so the exact number stays legible.
        if (count > cap)
            DrawBadge(g, count, x0 + (shown - 1) * spacing + tile * 0.42f, tileCY - tile * 0.42f, tile * 0.36f, outline);

        g.Restore(saved);
    }

    // One charm: a rounded tile (light pad + theme outline) with the tool's logo inset. Local coords:
    // (cx,cy) is the tile centre.
    static void DrawTile(Graphics g, string tool, float cx, float cy, float tile, float radius,
                         Color fill, Color outline, float ob)
    {
        var rect = new RectangleF(cx - tile / 2f, cy - tile / 2f, tile, tile);
        using var path = RoundedRect(rect, radius);
        using (var bf = new SolidBrush(fill))
            g.FillPath(bf, path);
        if (ob > 0.01f)
            using (var po = new Pen(outline, Math.Max(1.2f, ob * 1.6f)) { LineJoin = LineJoin.Round })
                g.DrawPath(po, path);

        float pad = tile * 0.17f;
        var inner = new RectangleF(rect.X + pad, rect.Y + pad, rect.Width - 2 * pad, rect.Height - 2 * pad);
        AgentLogos.Draw(g, tool, inner, outline);
    }

    static GraphicsPath RoundedRect(RectangleF r, float rad)
    {
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2f);
        float d = rad * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // A round count badge on the corner of the last tile (the total number of waiting agents),
    // white-on-dark like a notification pip so big counts read without extra vertical room.
    static void DrawBadge(Graphics g, int count, float cx, float cy, float r, Color ring)
    {
        string txt = count > 99 ? "99+" : count.ToString();
        using (var bg = new SolidBrush(ring))
            g.FillEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
        using (var rp = new Pen(Color.White, Math.Max(1f, r * 0.16f)))
            g.DrawEllipse(rp, cx - r, cy - r, 2 * r, 2 * r);

        using var path = new GraphicsPath();
        float em = r * (txt.Length >= 2 ? 0.95f : 1.25f);
        using var fam = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        path.AddString(txt, fam, (int)FontStyle.Bold, em, new PointF(cx, cy), fmt);
        using var tb = new SolidBrush(Color.White);
        g.FillPath(tb, path);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
