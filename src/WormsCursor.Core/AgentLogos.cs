using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// The baked vector logos for the supported agent tools, used as the dangling charm so you can tell
/// at a glance <i>which</i> tool is waiting (Claude Code's pixel critter, Codex's OpenAI knot). Each
/// logo is parsed once from its SVG path (see <see cref="SvgPath"/>) into a <see cref="GraphicsPath"/>
/// in its native viewBox, then scaled to fit wherever it's drawn — crisp at any cursor size.
///
/// Tool ids match the bridge's normalised <c>tool</c> field ("claude-code", "codex"). Unknown tools
/// fall back to a generic dot so a future/unmapped tool still shows <i>something</i> hanging.
/// </summary>
static class AgentLogos
{
    sealed class Logo
    {
        public required GraphicsPath Path;
        public required Color Color;   // brand fill, drawn on the light charm tile
        public RectangleF Bounds;      // cached native bounds for fitting
        // Rasterised sprites keyed on (on-screen side px, quantised rim width) — built once, blitted
        // every frame instead of re-stroking the vector path. A settings change (new size) just adds
        // an entry; the handful of tiny ARGB bitmaps live for the process lifetime.
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(int side, int eKey), Bitmap> Sprites = new();
    }

    // Claude Code — the pixel-art critter (fill #D97757, even-odd so the two eyes punch through).
    const string ClaudeData =
        "M20.998 10.949H24v3.102h-3v3.028h-1.487V20H18v-2.921h-1.487V20H15v-2.921H9V20H7.488v-2.921H6V20" +
        "H4.487v-2.921H3V14.05H0V10.95h3V5h17.998v5.949zM6 10.949h1.488V8.102H6v2.847zm10.51 0H18V8.102h-1.49v2.847z";

    // Codex — the OpenAI knot (monochrome line-art; non-zero winding). Drawn near-black on the tile.
    const string OpenAiData =
        "M474.123 209.81c11.525-34.577 7.569-72.423-10.838-103.904-27.696-48.168-83.433-72.94-137.794-61.414a127.14 127.14 0 00-95.475-42.49c-55.564 0-104.936 35.781-122.139 88.593-35.781 7.397-66.574 29.76-84.637 61.414-27.868 48.167-21.503 108.72 15.826 150.007-11.525 34.578-7.569 72.424 10.838 103.733 27.696 48.34 83.433 73.111 137.966 61.585 24.084 27.18 58.833 42.835 95.303 42.663 55.564 0 104.936-35.782 122.139-88.594 35.782-7.397 66.574-29.76 84.465-61.413 28.04-48.168 21.676-108.722-15.654-150.008v-.172zm-39.567-87.218c11.01 19.267 15.139 41.803 11.354 63.65-.688-.516-2.064-1.204-2.924-1.72l-101.152-58.49a16.965 16.965 0 00-16.687 0L206.621 194.5v-50.232l97.883-56.597c45.587-26.32 103.732-10.666 130.052 34.921zm-227.935 104.42l49.888-28.9 49.887 28.9v57.63l-49.887 28.9-49.888-28.9v-57.63zm23.223-191.81c22.364 0 43.867 7.742 61.07 22.02-.688.344-2.064 1.204-3.097 1.72L186.666 117.26c-5.161 2.925-8.258 8.43-8.258 14.45v136.934l-43.523-25.116V130.333c0-52.64 42.491-95.13 95.131-95.302l-.172.172zM52.14 168.697c11.182-19.268 28.557-34.062 49.544-41.803V247.14c0 6.02 3.097 11.354 8.258 14.45l118.354 68.295-43.695 25.288-97.711-56.425c-45.415-26.32-61.07-84.465-34.75-130.052zm26.665 220.71c-11.182-19.095-15.139-41.802-11.354-63.65.688.516 2.064 1.204 2.924 1.72l101.152 58.49a16.965 16.965 0 0016.687 0l118.354-68.467v50.232l-97.883 56.425c-45.587 26.148-103.732 10.665-130.052-34.75h.172zm204.54 87.39c-22.192 0-43.867-7.741-60.898-22.02a62.439 62.439 0 003.097-1.72l101.152-58.317c5.16-2.924 8.429-8.43 8.257-14.45V243.527l43.523 25.116v113.022c0 52.64-42.663 95.303-95.131 95.303v-.172zM461.22 343.303c-11.182 19.267-28.729 34.061-49.544 41.63V264.687c0-6.021-3.097-11.526-8.257-14.45L284.893 181.77l43.523-25.116 97.883 56.424c45.587 26.32 61.07 84.466 34.75 130.053l.172.172z";

    static readonly Dictionary<string, Logo> _byTool = Build();

    static Dictionary<string, Logo> Build()
    {
        var claude = Make(ClaudeData, FillMode.Alternate, Color.FromArgb(0xD9, 0x77, 0x57));
        var openai = Make(OpenAiData, FillMode.Winding, Color.FromArgb(0x10, 0x10, 0x10));
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-code"] = claude,
            ["claude"] = claude,
            ["codex"] = openai,
            ["openai"] = openai,
        };
    }

    static Logo Make(string data, FillMode fill, Color color)
    {
        var path = SvgPath.Parse(data, fill);
        return new Logo { Path = path, Color = color, Bounds = path.GetBounds() };
    }

    public static bool IsKnown(string tool) => _byTool.ContainsKey(tool ?? "");

    /// <summary>Draws the <paramref name="tool"/> logo to fit inside <paramref name="box"/> (centred,
    /// aspect-preserved), filled in its brand colour with a thin contrast rim (<paramref name="edge"/>
    /// px wide, auto light/dark vs. the brand colour) so the bare logo reads on any background —
    /// the same outline-under/fill-over look the rest of the cursor set uses. Unknown tools get a
    /// filled dot. Leaves no transform behind.</summary>
    public static void Draw(Graphics g, string tool, RectangleF box, float edge)
    {
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (!_byTool.TryGetValue(tool ?? "", out var logo))
        {
            float d = Math.Min(box.Width, box.Height) * 0.7f;
            var dot = new RectangleF(box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d);
            using (var rim = new Pen(RimFor(Color.Gray), edge))
                g.DrawEllipse(rim, dot);
            using (var fb = new SolidBrush(Color.Gray))
                g.FillEllipse(fb, dot);
            g.Restore(saved);
            return;
        }

        var b = logo.Bounds;
        if (b.Width <= 0 || b.Height <= 0) { g.Restore(saved); return; }

        // The logo content is fixed for a run (brand path + colour + size + rim), so rasterise it
        // ONCE into a small sprite and just blit it here — the per-frame cost drops from stroking +
        // filling the whole vector path (a widened round-join pen) to a single DrawImage. The swing
        // rotation is already in g's transform (set by DrawCharms), so the sprite rotates with it,
        // exactly like the pre-rendered arrow frames.
        // The sprite is padded all round (see PadFor) so the rim stroke that straddles the logo's
        // edge isn't clipped — blit it back at the matching negative offset so the logo itself still
        // lands exactly inside `box`, the rim spilling into the surrounding canvas as it did before.
        int pad = PadFor(edge);
        var sprite = GetSprite(logo, box.Width, edge);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(sprite, box.X - pad, box.Y - pad, sprite.Width, sprite.Height);
        g.Restore(saved);
    }

    // Padding (px) around the logo inside its sprite. The contrast rim is a pen that straddles the
    // path edge by ~edge px (plus a little anti-aliasing), so without this margin the protruding
    // parts of a logo (e.g. the Claude critter's arms) get their outline clipped at the sprite edge.
    static int PadFor(float edge) => (int)MathF.Ceiling(edge) + 2;

    // The cached sprite for this logo at the given on-screen box size + rim width, built on first use.
    static Bitmap GetSprite(Logo logo, float boxSide, float edge)
    {
        int side = Math.Max(1, (int)MathF.Round(boxSide));
        int eKey = (int)MathF.Round(edge * 4f);                 // quarter-px rim resolution
        return logo.Sprites.GetOrAdd((side, eKey), k => BuildSprite(logo, k.side, edge));
    }

    // Rasterises a logo upright into a padded transparent sprite: brand fill over a contrast rim,
    // centred + aspect-preserved in the inner side×side area — the exact look the live vector path
    // produced, baked once. The pad margin keeps the rim from being clipped (see PadFor).
    static Bitmap BuildSprite(Logo logo, int side, float edge)
    {
        int pad = PadFor(edge);
        int dim = side + 2 * pad;
        var bmp = new Bitmap(dim, dim);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var b = logo.Bounds;
        float scale = Math.Min(side / b.Width, side / b.Height);
        float w = b.Width * scale, h = b.Height * scale;

        // Map the logo's native bounds into the centred, scaled inner area (offset by the pad margin).
        g.TranslateTransform(pad + (side - w) / 2f, pad + (side - h) / 2f);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-b.X, -b.Y);

        // Contrast rim under, brand fill over. The pen is in native units (it gets scaled with the
        // graphics), so 2*edge/scale lands a ~edge-px rim on screen. LineJoin.Round keeps the pixel
        // critter's corners from spiking.
        float penW = 2f * edge / scale;
        using (var rim = new Pen(RimFor(logo.Color), penW) { LineJoin = LineJoin.Round })
            g.DrawPath(rim, logo.Path);
        using (var brush = new SolidBrush(logo.Color))
            g.FillPath(brush, logo.Path);
        return bmp;
    }

    // A light rim for dark logos, a dark rim for light ones — whichever gives contrast on the
    // opposite background (a near-black logo needs a light edge to show on a dark screen, etc.).
    static Color RimFor(Color fill)
    {
        float lum = 0.299f * fill.R + 0.587f * fill.G + 0.114f * fill.B;
        return lum < 110 ? Color.FromArgb(240, 240, 240) : Color.FromArgb(36, 36, 36);
    }
}
