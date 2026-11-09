using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Svg;
using Svg.Transforms;

namespace AgentsCursor.Core;

/// <summary>
/// Rasterises token icons (SVG, through SVG.NET) into cached sprites for one renderer. Every icon is
/// fitted by its <b>visible pixels</b>, not its viewBox, so icons with different padding all look the
/// same size, and each gets the same two-tone contrast rim (its alpha mask dilated: a dark inner band,
/// then a light outer one) so any icon reads over any window, dark or light, and a gallery of icons
/// is edged alike whatever their colours. The sprite also
/// reports where the icon sits in its box (<see cref="Sprite.Visual"/>), which is what the status badge
/// and the "+N" pill anchor to.
///
/// A mascot with an idle loop (<see cref="MascotLoops"/>) gets one sprite per distinct frame, built the
/// first time that frame is drawn. Every frame is fitted by the visible bounds of the whole loop, so the
/// mascot keeps one size and one <see cref="Sprite.Visual"/> (the badge never jitters) and no frame can
/// leave its box, and every frame's rim uses the rest frame's hole colour. Animated strips are kept
/// within <see cref="AnimatedBudget"/> bytes, the least recently drawn dropped first.
///
/// Not thread-safe, by design (GDI+ objects and SVG documents throw when two threads share them): each
/// <see cref="NotifierRenderer"/> owns one, and parses its own documents from the shared strings.
/// </summary>
internal sealed class TokenSprites : IDisposable
{
    /// <summary>A rasterised icon: the padded image (drawn at the box's corner minus
    /// <see cref="Pad"/>), and the icon's visible bounds within its <c>side x side</c> box.</summary>
    public sealed record Sprite(Bitmap Image, int Pad, RectangleF Visual);

    // An icon's parsed document, the bounds (in its own user units) of what it actually draws in any
    // frame, and its loop with the document elements it moves (none for a still icon).
    sealed record Parsed(SvgDocument Document, RectangleF Visible, MascotLoop? Loop, MovingPart[] Parts);

    // An animated element, with the transforms its file gives it.
    sealed record MovingPart(MascotPart Motion, SvgVisualElement Element, SvgTransform[] Original);

    // The sprites of one icon at one size: one per frame (one for a still icon), built on first use.
    sealed class Strip(Parsed? parsed, int frames)
    {
        public Parsed? Parsed { get; } = parsed;
        public Sprite?[] Frames { get; } = new Sprite?[frames];
        public bool[] Built { get; } = new bool[frames];
        public Color? Hole;      // the rim colour inside the icon's holes, from the rest frame
        public long Bytes;       // what its built frames hold (animated strips only)
        public long LastUse;
        public bool Animated => Frames.Length > 1;
    }

    const int ProbePx = 256;        // the resolution the visible bounds are measured at
    const float FitPad = 0.01f;     // breathing room around the visible bounds, of their larger side
    const byte Visible = 16;        // the alpha that counts as drawn (ignores the faintest AA dust)

    /// <summary>The most the animated strips of one instance hold, in bytes of pixels.</summary>
    internal const long AnimatedBudget = 48L << 20;

    readonly Dictionary<string, Parsed?> _parsed = new(StringComparer.Ordinal);
    readonly Dictionary<(string Tool, string Icon, int Side, int RimKey), Strip> _strips = new();
    long _animatedBytes, _uses;

    // The visible bounds of each animated built-in over its whole loop: the same for every instance,
    // so measured once per process (it renders every frame).
    static readonly ConcurrentDictionary<string, RectangleF?> LoopBounds = new(StringComparer.Ordinal);

    static TokenSprites() => EnsureConfigured();

    /// <summary>SVG.NET's process-wide switches: never fetch anything an icon points at (images,
    /// elements or XML entities), and skip DTDs. Runs once, before the first document is parsed.</summary>
    internal static void EnsureConfigured()
    {
        SvgDocument.ResolveExternalImages = ExternalType.None;
        SvgDocument.ResolveExternalElements = ExternalType.None;
        SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
        SvgDocument.DisableDtdProcessing = true;
    }

    /// <summary>The sprite of <paramref name="iconId"/> for <paramref name="tool"/> in a box of side
    /// <paramref name="side"/> px with a rim <paramref name="rim"/> px wide, built on first use (the
    /// tool's original when that icon can't be loaded or drawn); null when the tool has no icon (the
    /// caller draws a placeholder). A mascot with a loop shows the frame for <paramref name="clock"/>
    /// seconds into it (<see cref="MascotLoop.FrameAt"/>), or its rest frame when that is null.</summary>
    public Sprite? Get(string tool, string iconId, float side, float rim, double? clock = null)
    {
        int px = Math.Max(1, (int)MathF.Round(side));
        int rimKey = (int)MathF.Round(rim * 4f); // quarter-px rim resolution
        var key = (tool, iconId, px, rimKey);
        if (!_strips.TryGetValue(key, out var strip))
        {
            var parsed = ParseOrOriginal(tool, iconId);
            strip = new Strip(parsed, parsed?.Loop?.FrameCount ?? 1);
            _strips[key] = strip;
        }
        strip.LastUse = ++_uses;
        int frame = clock is { } c && strip.Parsed?.Loop is { } loop ? loop.FrameAt(c) : 0;
        Ensure(strip, 0, px, rimKey / 4f); // first: the rest frame sets the hole colour
        Ensure(strip, frame, px, rimKey / 4f);
        if (strip.Animated) Trim(strip);
        return strip.Frames[frame];
    }

    void Ensure(Strip strip, int frame, int px, float rim)
    {
        if (strip.Built[frame]) return;
        strip.Built[frame] = true;
        if (strip.Parsed is not { } parsed) return;
        var sprite = Build(parsed, px, rim, frame, ref strip.Hole);
        strip.Frames[frame] = sprite;
        if (!strip.Animated) return;
        long bytes = 4L * sprite.Image.Width * sprite.Image.Height;
        strip.Bytes += bytes;
        _animatedBytes += bytes;
    }

    // Drops the least recently drawn animated strips (never <keep>) until they fit the budget.
    void Trim(Strip keep)
    {
        while (_animatedBytes > AnimatedBudget)
        {
            var victims = _strips.Where(p => p.Value.Animated && p.Value != keep && p.Value.Bytes > 0)
                                 .OrderBy(p => p.Value.LastUse).Take(1).ToArray();
            if (victims.Length == 0) return;
            var (key, strip) = (victims[0].Key, victims[0].Value);
            foreach (var sprite in strip.Frames) sprite?.Image.Dispose();
            _animatedBytes -= strip.Bytes;
            _strips.Remove(key);
        }
    }

    /// <summary>The bytes the animated strips hold now (tests).</summary>
    internal long AnimatedBytes => _animatedBytes;

    /// <summary>The pixel size of what <paramref name="svgText"/> draws when its whole viewBox is fitted
    /// into <paramref name="px"/> x <paramref name="px"/>; null when it draws nothing. Parses its own
    /// document, so any thread may call it.</summary>
    internal static SizeF? VisibleSize(string svgText, int px)
    {
        var document = SvgDocument.FromSvg<SvgDocument>(svgText);
        var view = ViewBoxOf(document);
        if (MeasureVisible(document) is not { } visible) return null;
        float scale = px / Math.Max(view.Width, view.Height);
        return new SizeF(visible.Width * scale, visible.Height * scale);
    }

    // The icon, or the tool's original when it can't be drawn (a broken or empty upload), so a bad
    // icon never leaves the token blank. Null only when even the original can't be drawn.
    Parsed? ParseOrOriginal(string tool, string iconId)
    {
        var parsed = Parse(tool, iconId);
        string original = IconCatalog.OriginalId(tool);
        if (parsed is not null || iconId == original) return parsed;
        if (IconCatalog.Original(tool) is null) return null;
        IconSource.Report(tool, iconId, "couldn't be drawn");
        return Parse(tool, original);
    }

    Parsed? Parse(string tool, string iconId)
    {
        string cacheKey = tool + "\n" + iconId;
        if (_parsed.TryGetValue(cacheKey, out var parsed)) return parsed;
        parsed = null;
        if (IconSource.Load(tool, iconId) is { } text)
        {
            try
            {
                var document = SvgDocument.FromSvg<SvgDocument>(text);
                // Only a built-in has a loop: an added icon's id is never a built-in's.
                var loop = IconCatalog.Find(iconId)?.Tool == tool ? MascotLoops.For(iconId) : null;
                var parts = loop is null ? Array.Empty<MovingPart>() : PartsOf(document, loop, iconId);
                var visible = loop is null ? MeasureVisible(document)
                    : LoopBounds.GetOrAdd(cacheKey, _ => MeasureLoop(document, loop, parts));
                Pose(loop, parts, 0);
                if (visible is { } v) parsed = new Parsed(document, v, loop, parts);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An uploaded file is untrusted input, and SVG.NET reports a malformed value with whatever
                // its parsers throw. Any failure means "can't be drawn": logged, and the caller falls back
                // to the original, instead of failing every frame.
                IconSource.Log?.Invoke($"icon '{iconId}' for {tool} couldn't be parsed: {ex.Message}");
            }
        }
        _parsed[cacheKey] = parsed;
        return parsed;
    }

    // The elements <loop> moves, each with its file's transforms. A part missing from the file is a bug
    // in the built-in art (the tests catch it); at run time it is logged and simply doesn't move.
    static MovingPart[] PartsOf(SvgDocument document, MascotLoop loop, string iconId)
    {
        var parts = new List<MovingPart>();
        foreach (var motion in loop.Parts)
        {
            if (document.GetElementById(motion.Id) is not SvgVisualElement element)
            {
                IconSource.Log?.Invoke($"icon '{iconId}' has no part '{motion.Id}' to animate");
                continue;
            }
            parts.Add(new MovingPart(motion, element, element.Transforms?.ToArray() ?? Array.Empty<SvgTransform>()));
        }
        return parts.ToArray();
    }

    // Puts every moving part where <frame> has it: its pose (about its pivot, in its parent's units)
    // ahead of its own transforms, at the pose's opacity.
    static void Pose(MascotLoop? loop, MovingPart[] parts, int frame)
    {
        if (loop is null) return;
        foreach (var part in parts)
        {
            var pose = loop.PoseAt(part.Motion, frame);
            float px = part.Motion.PivotX, py = part.Motion.PivotY;
            var transforms = new SvgTransformCollection();
            if (pose.Dx != 0f || pose.Dy != 0f || pose.Turn != 0f || pose.ScaleX != 1f || pose.ScaleY != 1f)
            {
                transforms.Add(new SvgTranslate(pose.Dx + px, pose.Dy + py));
                if (pose.Turn != 0f) transforms.Add(new SvgRotate(pose.Turn));
                if (pose.ScaleX != 1f || pose.ScaleY != 1f) transforms.Add(new SvgScale(pose.ScaleX, pose.ScaleY));
                transforms.Add(new SvgTranslate(-px, -py));
            }
            foreach (var own in part.Original) transforms.Add(own);
            part.Element.Transforms = transforms;
            part.Element.Opacity = pose.Opacity;
        }
    }

    /// <summary>Each distinct frame of built-in <paramref name="iconId"/>'s loop with the bounds it
    /// draws (null: nothing), and the viewBox they are measured in, which must hold every frame with
    /// room to spare (what spills out of it is clipped). Throws when a part has no element. Tests.</summary>
    internal static IReadOnlyList<(int Frame, RectangleF? Visible, RectangleF View)> MeasureFrames(string tool, string iconId)
    {
        var loop = MascotLoops.For(iconId) ?? throw new ArgumentException(iconId + " has no loop");
        var document = SvgDocument.FromSvg<SvgDocument>(IconSource.Load(tool, iconId)!);
        var parts = PartsOf(document, loop, iconId);
        if (parts.Length != loop.Parts.Count)
            throw new InvalidOperationException($"{iconId}: {loop.Parts.Count - parts.Length} part(s) missing from the SVG");
        var view = ViewBoxOf(document);
        return loop.Frames.Select(frame => { Pose(loop, parts, frame); return (frame, MeasureVisible(document), view); }).ToArray();
    }

    // The union of every distinct frame's visible bounds; null when no frame draws anything.
    static RectangleF? MeasureLoop(SvgDocument document, MascotLoop loop, MovingPart[] parts)
    {
        RectangleF? union = null;
        foreach (int frame in loop.Frames)
        {
            Pose(loop, parts, frame);
            if (MeasureVisible(document) is not { } visible) continue;
            union = union is { } u ? RectangleF.Union(u, visible) : visible;
        }
        return union;
    }

    // The document's own coordinate space: its viewBox, or its width x height when it has none.
    static RectangleF ViewBoxOf(SvgDocument document)
    {
        var vb = document.ViewBox;
        if (vb.Width > 0 && vb.Height > 0) return new RectangleF(vb.MinX, vb.MinY, vb.Width, vb.Height);
        var size = document.GetDimensions();
        return new RectangleF(0, 0, Math.Max(1f, size.Width), Math.Max(1f, size.Height));
    }

    // Renders the part of the document inside <view> (user units) to fill width x height px at the
    // graphics' current origin.
    static void Render(SvgDocument document, Graphics g, RectangleF view, float width, float height)
    {
        document.ViewBox = new SvgViewBox(view.X, view.Y, view.Width, view.Height);
        document.AspectRatio = new SvgAspectRatio(SvgPreserveAspectRatio.none);
        document.Width = new SvgUnit(SvgUnitType.Pixel, width);
        document.Height = new SvgUnit(SvgUnitType.Pixel, height);
        document.Draw(g);
    }

    // Probe-renders the whole document and returns the bounds of its visible pixels in user units;
    // null when it draws nothing.
    static RectangleF? MeasureVisible(SvgDocument document)
    {
        var view = ViewBoxOf(document);
        float scale = ProbePx / Math.Max(view.Width, view.Height);
        int w = Math.Max(1, (int)MathF.Ceiling(view.Width * scale));
        int h = Math.Max(1, (int)MathF.Ceiling(view.Height * scale));
        using var probe = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(probe))
        {
            g.Clear(Color.Transparent);
            Render(document, g, view, w, h);
        }
        var alpha = Pixels(probe);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (alpha[(y * w + x) * 4 + 3] >= Visible)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return null;
        return new RectangleF(view.X + minX / scale, view.Y + minY / scale,
                              (maxX + 1 - minX) / scale, (maxY + 1 - minY) / scale);
    }

    static Sprite Build(Parsed parsed, int side, float rim, int frame, ref Color? hole)
    {
        int pad = (int)MathF.Ceiling(rim) + 2; // room for the rim (plus anti-aliasing) outside the box
        int dim = side + 2 * pad;

        var visible = parsed.Visible;
        float margin = Math.Max(visible.Width, visible.Height) * FitPad;
        var view = RectangleF.Inflate(visible, margin, margin);
        float k = side / Math.Max(view.Width, view.Height);
        float w = view.Width * k, h = view.Height * k;
        float ox = (side - w) / 2f, oy = (side - h) / 2f;

        var image = new Bitmap(dim, dim, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.Transparent);
            g.TranslateTransform(pad + ox, pad + oy);
            Pose(parsed.Loop, parsed.Parts, frame);
            Render(parsed.Document, g, view, w, h);
        }
        hole ??= HoleRim(Pixels(image));
        AddRim(image, rim, hole.Value);
        return new Sprite(image, pad, new RectangleF(ox, oy, w, h));
    }

    /// <summary>The rim's two bands around the icon: dark next to it (it edges the icon on a light
    /// screen), light outside that (it edges the whole on a dark one). Inside a hole in the icon (the
    /// Claude critter's eyes, the gaps in the Codex knot) the rim is the one of the two that contrasts
    /// with the icon itself, so the hole reads as part of the picture on any screen.</summary>
    internal static readonly Color RimDark = Color.FromArgb(28, 28, 28), RimLight = Color.FromArgb(246, 246, 246);
    const float DarkShare = 0.55f; // of the rim's width

    // Composites the icon, outside it, over its alpha mask dilated by the dark band's width, over the
    // mask dilated by the whole rim in the light colour; inside its holes, over the mask dilated by the
    // whole rim in the colour that contrasts with the icon (discs, anti-aliased at their edges).
    static void AddRim(Bitmap image, float rim, Color hole)
    {
        int w = image.Width, h = image.Height;
        var px = Pixels(image);
        var outerKernel = Disc(rim);
        var innerKernel = Disc(rim * DarkShare);
        var outside = Outside(px, w, h);
        bool NearOutside(int x, int y)
            => outside[y * w + x] || (x > 0 && outside[y * w + x - 1]) || (x < w - 1 && outside[y * w + x + 1])
               || (y > 0 && outside[(y - 1) * w + x]) || (y < h - 1 && outside[(y + 1) * w + x]);

        var output = new byte[px.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] == 255) // covered by the icon: no rim shows through
                {
                    Buffer.BlockCopy(px, i, output, i, 4);
                    continue;
                }
                float outer = Dilated(px, w, h, x, y, outerKernel), srcA = px[i + 3] / 255f;
                float dark, light;
                if (!NearOutside(x, y)) // in a hole
                {
                    dark = hole == RimDark ? outer * (1f - srcA) : 0f;
                    light = hole == RimLight ? outer * (1f - srcA) : 0f;
                }
                else
                {
                    // Premultiplied "source over", front to back: icon, then the dark band, then the light one.
                    float inner = Dilated(px, w, h, x, y, innerKernel);
                    dark = inner * (1f - srcA);
                    light = outer * (1f - srcA) * (1f - inner);
                }
                output[i] = (byte)Math.Min(255f, px[i] + RimDark.B * dark + RimLight.B * light + 0.5f);
                output[i + 1] = (byte)Math.Min(255f, px[i + 1] + RimDark.G * dark + RimLight.G * light + 0.5f);
                output[i + 2] = (byte)Math.Min(255f, px[i + 2] + RimDark.R * dark + RimLight.R * light + 0.5f);
                output[i + 3] = (byte)Math.Min(255f, px[i + 3] + 255f * (dark + light) + 0.5f);
            }
        var data = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(output, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { image.UnlockBits(data); }
    }

    // The rim colour inside the icon's holes: the one of the two that contrasts with the icon.
    static Color HoleRim(byte[] px) => Luminance(AverageColor(px)) < 110f ? RimLight : RimDark;

    // The alpha-weighted average colour of the icon's pixels (premultiplied sums / total alpha).
    static Color AverageColor(byte[] px)
    {
        double r = 0, g = 0, b = 0, a = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            b += px[i]; g += px[i + 1]; r += px[i + 2]; a += px[i + 3];
        }
        if (a <= 0) return Color.Gray;
        double scale = 255.0 / a;
        return Color.FromArgb((int)Math.Min(255, r * scale), (int)Math.Min(255, g * scale), (int)Math.Min(255, b * scale));
    }

    static float Luminance(Color c) => 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;

    // The pixels outside the icon: the mostly clear ones reachable from the image's edge (the sprite has
    // a clear border, so all of the outside is connected). A clear pixel not reached is in a hole.
    static bool[] Outside(byte[] px, int w, int h)
    {
        const byte Clear = 128;
        var outside = new bool[w * h];
        var queue = new Queue<int>();
        void Reach(int x, int y)
        {
            int k = y * w + x;
            if (outside[k] || px[k * 4 + 3] >= Clear) return;
            outside[k] = true;
            queue.Enqueue(k);
        }
        for (int x = 0; x < w; x++) { Reach(x, 0); Reach(x, h - 1); }
        for (int y = 0; y < h; y++) { Reach(0, y); Reach(w - 1, y); }
        while (queue.Count > 0)
        {
            int k = queue.Dequeue(), x = k % w, y = k / w;
            if (x > 0) Reach(x - 1, y);
            if (x < w - 1) Reach(x + 1, y);
            if (y > 0) Reach(x, y - 1);
            if (y < h - 1) Reach(x, y + 1);
        }
        return outside;
    }

    // The offsets within <radius> px, each with how much of that pixel the disc covers.
    static (int Dx, int Dy, float Cover)[] Disc(float radius)
    {
        int reach = (int)MathF.Ceiling(radius + 0.5f);
        var kernel = new List<(int Dx, int Dy, float Cover)>();
        for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                float cover = Math.Clamp(radius + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                if (cover > 0f) kernel.Add((dx, dy, cover));
            }
        return kernel.ToArray();
    }

    // The icon's alpha at (x, y) dilated by <kernel>, 0..1.
    static float Dilated(byte[] px, int w, int h, int x, int y, (int Dx, int Dy, float Cover)[] kernel)
    {
        float ring = 0f;
        foreach (var (dx, dy, cover) in kernel)
        {
            int sx = x + dx, sy = y + dy;
            if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) continue;
            float a = px[(sy * w + sx) * 4 + 3] / 255f * cover;
            if (a > ring) { ring = a; if (ring >= 1f) break; }
        }
        return ring;
    }

    // The bitmap's premultiplied BGRA bytes, tightly packed (w * 4 per row).
    static byte[] Pixels(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var bytes = new byte[w * h * 4];
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bytes;
    }

    public void Dispose()
    {
        foreach (var strip in _strips.Values)
            foreach (var sprite in strip.Frames) sprite?.Image.Dispose();
        _strips.Clear();
        _animatedBytes = 0;
        _parsed.Clear();
    }
}
