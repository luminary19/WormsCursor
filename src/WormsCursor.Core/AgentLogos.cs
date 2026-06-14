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
    /// aspect-preserved), filled in its brand colour. Unknown tools get a filled dot in
    /// <paramref name="fallback"/>. Leaves no transform behind.</summary>
    public static void Draw(Graphics g, string tool, RectangleF box, Color fallback)
    {
        if (!_byTool.TryGetValue(tool ?? "", out var logo))
        {
            using var fb = new SolidBrush(fallback);
            float d = Math.Min(box.Width, box.Height) * 0.7f;
            g.FillEllipse(fb, box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d);
            return;
        }

        var b = logo.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        float scale = Math.Min(box.Width / b.Width, box.Height / b.Height);
        float w = b.Width * scale, h = b.Height * scale;

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Map the logo's native bounds into the centred, scaled target box.
        g.TranslateTransform(box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-b.X, -b.Y);
        using (var brush = new SolidBrush(logo.Color))
            g.FillPath(brush, logo.Path);
        g.Restore(saved);
    }
}
