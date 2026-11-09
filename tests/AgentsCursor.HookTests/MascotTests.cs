using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using AgentsCursor.Core;

/// <summary>The mascots' idle loops: frame selection, when they play, the art they move, how they
/// fit and render, and rendering them on two threads.</summary>
static class MascotTests
{
    static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}{(what is null ? "" : ": ")}expected '{expected}', got '{actual}'.");
    }

    static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    static IEnumerable<(IconInfo Icon, MascotLoop Loop)> Animated()
        => IconCatalog.BuiltIns.Where(i => MascotLoops.For(i.Id) is not null).Select(i => (i, MascotLoops.For(i.Id)!));

    // ---------- frames ----------

    public static void FrameSelection()
    {
        // Every mascot has a loop; originals don't.
        foreach (var icon in IconCatalog.BuiltIns)
            Equal(icon.Id.EndsWith(".original"), MascotLoops.For(icon.Id) is null, icon.Id + " has a loop iff it's a mascot");
        Equal(12, Animated().Count(), "twelve mascot loops");
        Equal(null, MascotLoops.For("custom:0123456789abcdef"), "an added icon has no loop");
        Equal(null, MascotLoops.For(null), "no id, no loop");

        foreach (var (icon, loop) in Animated())
        {
            Equal(0, loop.FrameAt(0), icon.Id + " starts at rest");
            Equal(0, loop.Frames[0], icon.Id + " frame 0 first");
            True(loop.Seconds >= 2.5f && loop.Seconds <= 6f, $"{icon.Id} loops in a few seconds ({loop.Seconds})");
            int atRest = Enumerable.Range(0, loop.FrameCount).Count(i => loop.FrameAt((i + 0.5) / loop.Fps) == 0);
            True(atRest * 2 >= loop.FrameCount, $"{icon.Id} is at rest most of the loop ({atRest}/{loop.FrameCount})");
            True(loop.Frames.Count > 1, icon.Id + " moves");

            // The same clock always gives the same frame, a whole loop later too, and backwards in time.
            var rng = new Random(icon.Id.Length);
            for (int i = 0; i < 200; i++)
            {
                int sample = rng.Next(loop.FrameCount);
                double t = (sample + 0.5) / loop.Fps;
                int frame = loop.FrameAt(t);
                Equal(frame, loop.FrameAt(t), icon.Id + " deterministic");
                Equal(frame, loop.FrameAt(t + 7 * (double)loop.FrameCount / loop.Fps), icon.Id + " wraps");
                Equal(frame, loop.FrameAt(t - 3 * (double)loop.FrameCount / loop.Fps), icon.Id + " wraps backwards");
                True(loop.Frames.Contains(frame), icon.Id + " frame is a distinct one");
                // A frame is drawn as the first sample with its poses.
                foreach (var part in loop.Parts)
                    True(loop.PoseAt(part, frame).Near(part.At(sample / (float)loop.Fps)), $"{icon.Id} {part.Id}: sample {sample} drawn as frame {frame}");
            }
            Equal(0, loop.FrameAt(double.NaN), icon.Id + " NaN");
            Equal(0, loop.FrameAt(double.PositiveInfinity), icon.Id + " infinity");
        }
    }

    // ---------- when they play ----------

    public static void AnimateSetting()
    {
        var s = new CursorSettings();
        Equal(true, s.AnimateMascots, "on by default");
        Equal(true, MascotLoops.Animate(s, animationEffects: true), "default animates");
        Equal(false, MascotLoops.Animate(s, animationEffects: false), "Windows animation effects off");
        s.Motion = MotionStyle.Calm;
        Equal(true, MascotLoops.Animate(s, true), "Calm animates");
        s.Motion = MotionStyle.Still;
        Equal(false, MascotLoops.Animate(s, true), "Still stops them");
        s.Motion = MotionStyle.Bouncy;
        s.AnimateMascots = false;
        Equal(false, MascotLoops.Animate(s, true), "switched off");

        string dir = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            SettingsStore.Save(s, path);
            Equal(false, SettingsStore.Load(path).AnimateMascots, "off round-trips");
            File.WriteAllText(path, "{\"Size\": 100}");
            Equal(true, SettingsStore.Load(path).AnimateMascots, "a file without it animates");
            Equal(false, s.Clone().AnimateMascots, "copied");
        }
        finally { Directory.Delete(dir, recursive: true); }

        // No clock draws the rest frame; a clock in a burst draws something else; an original never moves.
        using var renderer = new NotifierRenderer();
        foreach (var (icon, loop) in Animated())
        {
            int moving = loop.Frames[^1];
            double during = (moving + 0.5) / loop.Fps;
            using var still = DrawIcon(renderer, icon, null);
            using var rest = DrawIcon(renderer, icon, 0.5 / loop.Fps);
            using var busy = DrawIcon(renderer, icon, during);
            True(SamePixels(still, rest), icon.Id + ": no clock is the rest frame");
            True(!SamePixels(still, busy), icon.Id + $": frame {moving} differs from rest");
        }
        foreach (string tool in IconCatalog.Tools)
        {
            var original = IconCatalog.Original(tool)!;
            using var a = DrawIcon(renderer, original, null);
            using var b = DrawIcon(renderer, original, 1.234);
            True(SamePixels(a, b), tool + " original stands still");
        }
    }

    // ---------- the art ----------

    public static void MascotArt()
    {
        foreach (var (icon, loop) in Animated())
        {
            // Every part exists, and frame 0 is exactly what the file draws: no move, the file's opacity.
            var svg = XDocument.Parse(IconSource.Load(icon.Tool, icon.Id)!);
            foreach (var part in loop.Parts)
            {
                var element = svg.Descendants().SingleOrDefault(e => (string?)e.Attribute("id") == part.Id);
                True(element is not null, $"{icon.Id} has an element '{part.Id}'");
                float opacity = element!.Attribute("opacity") is { } o ? float.Parse(o.Value, System.Globalization.CultureInfo.InvariantCulture) : 1f;
                var pose = part.At(0f);
                // A part hidden in the file may wait anywhere, as long as it is hidden.
                bool rests = opacity == 0f ? pose.Opacity == 0f : pose.Near(PartPose.Rest with { Opacity = opacity });
                True(rests, $"{icon.Id} {part.Id} rests where its file draws it ({pose})");
            }
            // Every frame, the rest pose included, draws inside the viewBox it is measured in, clear of its
            // edges: what crosses an edge is clipped from the measurement, and then from the sprite.
            foreach (var (frame, visible, view) in TokenSprites.MeasureFrames(icon.Tool, icon.Id))
            {
                if (visible is not { } v) continue;
                float slack = Math.Max(view.Width, view.Height) / 200f;
                True(v.Left > view.Left + slack && v.Top > view.Top + slack && v.Right < view.Right - slack && v.Bottom < view.Bottom - slack,
                    $"{icon.Id} frame {frame} draws {v} inside its viewBox {view}");
            }
        }
    }

    // ---------- fit and canvas ----------

    public static void FramesInBox()
    {
        var settings = new CursorSettings();
        using var sprites = new TokenSprites();
        using var renderer = new NotifierRenderer();
        foreach (var (icon, loop) in Animated())
        {
            foreach (int side in new[] { 48, 96, 192 })
            {
                float rim = NotifierRenderer.RimWidth(side, 1f);
                var rest = sprites.Get(icon.Tool, icon.Id, side, rim)!;
                foreach (int frame in loop.Frames)
                {
                    var sprite = sprites.Get(icon.Tool, icon.Id, side, rim, (frame + 0.5) / loop.Fps)!;
                    Equal(rest.Visual, sprite.Visual, $"{icon.Id} {side}px frame {frame}: the badge anchor never moves");
                    Equal(rest.Pad, sprite.Pad, $"{icon.Id} {side}px frame {frame} pad");
                    // What it draws stays within the fitted bounds plus the rim.
                    var reach = RectangleF.Inflate(sprite.Visual, rim + 2f, rim + 2f); // the rim, plus edge blending
                    reach.Offset(sprite.Pad, sprite.Pad);
                    var px = Pixels(sprite.Image);
                    int w = sprite.Image.Width;
                    for (int y = 0; y < sprite.Image.Height; y++)
                        for (int x = 0; x < w; x++)
                            if (px[(y * w + x) * 4 + 3] >= 16 && !reach.Contains(x + 0.5f, y + 0.5f))
                                throw new InvalidOperationException($"{icon.Id} {side}px frame {frame}: pixel at {x},{y} outside {reach}.");
                }
            }

            // Every frame, with its badge and +9, at any swing, inside the token canvas.
            const int size = 96;
            int canvasSide = TokenCanvas.Side(new TokenFootprint(size));
            using var canvas = new Bitmap(canvasSide, canvasSide, PixelFormat.Format32bppArgb);
            foreach (int frame in loop.Frames)
                for (int swing = -180; swing < 180; swing += 45)
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        renderer.DrawToken(g, settings, new TokenFace(icon.Tool, frame % 2 == 0, 9), icon.Id,
                            canvasSide / 2f, canvasSide / 2f, size, swing, 1f, (frame + 0.5) / loop.Fps);
                    }
                    var px = Pixels(canvas);
                    for (int i = 0; i < canvasSide; i++)
                        foreach (var (x, y) in new[] { (i, 0), (i, canvasSide - 1), (0, i), (canvasSide - 1, i) })
                            if (px[(y * canvasSide + x) * 4 + 3] != 0)
                                throw new InvalidOperationException($"{icon.Id} frame {frame} swing {swing}: pixel on the canvas edge at {x},{y}.");
                }
        }
    }

    public static void FrameBudget()
    {
        using var sprites = new TokenSprites();
        long largestStrip = 0;
        foreach (int side in new[] { 192, 176, 160, 144 })
            foreach (var (icon, loop) in Animated())
            {
                long before = sprites.AnimatedBytes;
                float rim = NotifierRenderer.RimWidth(side, 1f);
                foreach (int frame in loop.Frames)
                    sprites.Get(icon.Tool, icon.Id, side, rim, (frame + 0.5) / loop.Fps);
                largestStrip = Math.Max(largestStrip, sprites.AnimatedBytes - before);
                True(sprites.AnimatedBytes <= TokenSprites.AnimatedBudget + largestStrip,
                    $"{icon.Id} {side}px: {sprites.AnimatedBytes} bytes held, budget {TokenSprites.AnimatedBudget}");
            }
        // A strip dropped from the cache is simply built again.
        var first = Animated().First();
        var again = sprites.Get(first.Icon.Tool, first.Icon.Id, 192, NotifierRenderer.RimWidth(192, 1f), (first.Loop.Frames[^1] + 0.5) / first.Loop.Fps);
        True(again is not null, "an evicted frame is rebuilt");
    }

    // ---------- threads ----------

    // The overlay's thread and the panel's draw mascots at once, each with its own renderer, and the same
    // clock draws the same picture on both.
    public static void ConcurrentFrames()
    {
        var settings = new CursorSettings();
        var mascots = Animated().ToArray();
        int failures = 0, frames = 0, mismatches = 0;
        var until = DateTime.UtcNow.AddSeconds(1.5);
        var results = new System.Collections.Concurrent.ConcurrentDictionary<(string, int), byte[]>();
        var threads = Enumerable.Range(0, 2).Select(t => new Thread(() =>
        {
            using var renderer = new NotifierRenderer();
            using var canvas = new Bitmap(160, 160, PixelFormat.Format32bppArgb);
            int n = 0;
            while (DateTime.UtcNow < until)
            {
                try
                {
                    var (icon, loop) = mascots[(n + t * 5) % mascots.Length];
                    int frame = loop.Frames[(n / mascots.Length) % loop.Frames.Count];
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        renderer.DrawToken(g, settings, new TokenFace(icon.Tool, true, 1), icon.Id, 80f, 80f, 96f, 0f, 1f, (frame + 0.5) / loop.Fps);
                    }
                    var px = Pixels(canvas);
                    var seen = results.GetOrAdd((icon.Id, frame), px);
                    if (!seen.AsSpan().SequenceEqual(px)) Interlocked.Increment(ref mismatches);
                    Interlocked.Increment(ref frames);
                }
                catch (InvalidOperationException) { Interlocked.Increment(ref failures); }
                n++;
            }
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        Equal(0, failures, "GDI+ failures");
        Equal(0, mismatches, "same clock, same picture");
        True(frames > 100, $"drew {frames} frames");
    }

    // ---------- helpers ----------

    static Bitmap DrawIcon(NotifierRenderer renderer, IconInfo icon, double? clock)
    {
        var bitmap = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        renderer.DrawIcon(g, icon.Tool, icon.Id, 48f, 48f, 80f, 1f, clock);
        return bitmap;
    }

    static bool SamePixels(Bitmap a, Bitmap b) => Pixels(a).AsSpan().SequenceEqual(Pixels(b));

    static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
