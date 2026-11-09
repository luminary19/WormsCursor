using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace AgentsCursor.Core;

/// <summary>
/// Draws and caches the bitmaps of <see cref="TokenLabel"/>s: the project's name in a semibold UI font
/// on a small rounded box (black with white text in the dark theme, white with black text in the light
/// one) with a thin edge of the other tone, so it reads over any window. Text is grid-fitted on the
/// opaque box and the bitmap is blitted at whole pixels, so it stays crisp at every size. A name wider
/// than <see cref="TokenLabel.MaxTextPx"/> ends in an ellipsis.
///
/// Owned by one <see cref="NotifierRenderer"/> and so by one drawing thread, like its other GDI+
/// objects. Holds the few labels in use; a new size or name past <see cref="Capacity"/> starts over.
/// </summary>
internal sealed class LabelSprites : IDisposable
{
    const int Capacity = 16;
    const float PadXEm = 0.6f;     // the box's side padding
    const float BoxEm = 1.6f;      // the box's height inside its edge
    const float CapEm = 0.70f;     // Segoe UI's capital height: the letters are centred on it
    const float RadiusRatio = 0.3f; // of the box's height
    const string Ellipsis = "…";

    static readonly Color DarkFill = TokenSprites.RimDark, DarkText = Color.White;
    static readonly Color DarkEdge = Color.FromArgb(120, 255, 255, 255);
    static readonly Color LightFill = Color.White, LightText = TokenSprites.RimDark;
    static readonly Color LightEdge = Color.FromArgb(90, 0, 0, 0);

    readonly record struct Key(string Text, bool Dark, float Em, float MaxText, int Edge);

    readonly Dictionary<Key, Bitmap> _cache = new();
    (FontFamily Family, FontStyle Style)? _font;

    /// <summary>The bitmap of <paramref name="label"/> for a token of <paramref name="tokenPx"/> px at
    /// <paramref name="dpi"/>: the box with its edge, whole pixels.</summary>
    public Bitmap Get(TokenLabel label, float tokenPx, float dpi)
    {
        var key = new Key(label.Text, label.Dark, TokenLabel.EmPx(tokenPx, dpi), TokenLabel.MaxTextPx(tokenPx, dpi),
                          Math.Max(1, (int)dpi)); // a hairline: 1 px up to 200%, 2 px from there
        if (_cache.TryGetValue(key, out var cached)) return cached;
        if (_cache.Count >= Capacity) Clear();
        var bitmap = Render(key);
        _cache[key] = bitmap;
        return bitmap;
    }

    Bitmap Render(Key key)
    {
        var (family, style) = _font ??= SemiboldFamily();
        using var font = new Font(family, key.Em, style, GraphicsUnit.Pixel);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap;

        string text;
        float textWidth;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            text = Fit(g, font, format, key.Text, key.MaxText);
            textWidth = g.MeasureString(text, font, PointF.Empty, format).Width;
        }

        int edge = key.Edge;
        int padX = (int)MathF.Round(key.Em * PadXEm), boxHeight = (int)MathF.Round(key.Em * BoxEm);
        int width = (int)MathF.Ceiling(textWidth) + 2 * padX + 2 * edge, height = boxHeight + 2 * edge;
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var box = RoundedBox(edge / 2f, edge / 2f, width - edge, height - edge, (height - edge) * RadiusRatio))
            {
                using (var fill = new SolidBrush(key.Dark ? DarkFill : LightFill))
                    g.FillPath(fill, box);
                using (var pen = new Pen(key.Dark ? DarkEdge : LightEdge, edge))
                    g.DrawPath(pen, box);
            }
            // The capitals centred in the box, on a whole-pixel baseline.
            float ascent = key.Em * family.GetCellAscent(style) / family.GetEmHeight(style);
            float baseline = MathF.Round(edge + boxHeight / 2f + key.Em * CapEm / 2f);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var ink = new SolidBrush(key.Dark ? DarkText : LightText);
            g.DrawString(text, font, ink, new PointF(edge + padX, baseline - ascent), format);
        }
        return bitmap;
    }

    // The whole name if it fits in max px, else its longest start (whole characters) that fits with an
    // ellipsis after it.
    static string Fit(Graphics g, Font font, StringFormat format, string text, float max)
    {
        float Width(string s) => g.MeasureString(s, font, PointF.Empty, format).Width;
        if (Width(text) <= max) return text;
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        string Cut(int elements) => text[..(elements < starts.Length ? starts[elements] : text.Length)].TrimEnd() + Ellipsis;
        int lo = 0, hi = starts.Length - 1; // elements kept: lo always fits (an ellipsis alone, at worst)
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Width(Cut(mid)) <= max) lo = mid;
            else hi = mid - 1;
        }
        return Cut(lo);
    }

    // Segoe UI Semibold where Windows has it, else the closest bold sans serif.
    static (FontFamily, FontStyle) SemiboldFamily()
    {
        foreach (var (name, style) in new[] { ("Segoe UI Semibold", FontStyle.Regular), ("Segoe UI", FontStyle.Bold) })
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(style)) return (family, style);
                family.Dispose();
            }
            catch (ArgumentException) { /* not installed: try the next */ }
        }
        return (new FontFamily(GenericFontFamilies.SansSerif), FontStyle.Bold);
    }

    static GraphicsPath RoundedBox(float x, float y, float w, float h, float r)
    {
        float d = Math.Min(2f * r, Math.Min(w, h));
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180f, 90f);
        path.AddArc(x + w - d, y, d, d, 270f, 90f);
        path.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
        path.AddArc(x, y + h - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    void Clear()
    {
        foreach (var bitmap in _cache.Values) bitmap.Dispose();
        _cache.Clear();
    }

    public void Dispose()
    {
        Clear();
        _font?.Family.Dispose();
        _font = null;
    }
}
