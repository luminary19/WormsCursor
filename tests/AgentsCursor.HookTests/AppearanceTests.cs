using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentsCursor.Core;

/// <summary>The appearance regression groups: icon choice and uploads, the built-in mascots, the
/// token canvas, pointer placements and their settings migration.</summary>
static class AppearanceTests
{
    const string Ns = "http://www.w3.org/2000/svg";
    const string Square = $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#3A7\"/></svg>";

    static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}{(what is null ? "" : ": ")}expected '{expected}', got '{actual}'.");
    }

    static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    static void InTemp(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { test(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static string NewCustomId() => IconCatalog.CustomPrefix + Guid.NewGuid().ToString("N")[..16];

    // ---------- settings ----------

    public static void PlacementMigration() => InTemp(dir =>
    {
        string path = Path.Combine(dir, "settings.json");
        CursorSettings Load(string json) { File.WriteAllText(path, json); return SettingsStore.Load(path); }

        Equal(NotifierPlacement.BelowPointer, Load("{\"Version\": 1, \"Placement\": \"Cursor\"}").Placement, "v1 Cursor");
        Equal(NotifierPlacement.BelowPointer, Load("{\"Placement\": \"cursor\", \"Size\": 120}").Placement, "no version");
        Equal(120, SettingsStore.Load(path).Size, "the rest of a migrated file");
        Equal(NotifierPlacement.Corner, Load("{\"Version\": 1, \"Placement\": \"Corner\"}").Placement, "Corner kept");
        Equal(NotifierPlacement.BelowPointer, Load("{\"Placement\": 0}").Placement, "numeric 0");
        Equal(NotifierPlacement.Corner, Load("{\"Placement\": 1}").Placement, "numeric 1");
        Equal(NotifierPlacement.BelowPointer, Load("{\"Placement\": 7}").Placement, "an undefined number");
        var unknown = Load("{\"Placement\": \"Sideways\", \"Size\": 120}");
        Equal(NotifierPlacement.BelowPointer, unknown.Placement, "an unknown name");
        Equal(96, unknown.Size, "an unknown name makes the file unreadable, so defaults");
        Equal(CursorSettings.CurrentVersion, Load("{\"Version\": 1, \"Placement\": \"Cursor\"}").Version, "migrated version");
        var duplicate = Load("{\"Size\": 120, \"Size\": 130, \"Placement\": \"Cursor\"}");
        Equal(96, duplicate.Size, "a property named twice makes the file unreadable, so defaults (no crash)");

        // A v2 file round-trips by name.
        var s = new CursorSettings { Placement = NotifierPlacement.AbovePointer };
        SettingsStore.Save(s, path);
        string saved = File.ReadAllText(path);
        True(saved.Contains("\"AbovePointer\"") && saved.Contains("\"Version\": 2"), "saved v2 names its placement: " + saved);
        Equal(NotifierPlacement.AbovePointer, SettingsStore.Load(path).Placement, "v2 round trip");
        s.Placement = NotifierPlacement.BesidePointer;
        SettingsStore.Save(s, path);
        Equal(NotifierPlacement.BesidePointer, SettingsStore.Load(path).Placement, "beside round trip");
    });

    public static void IconSettings() => InTemp(dir =>
    {
        string custom = "custom:0123456789abcdef";
        var s = new CursorSettings
        {
            Icons = new Dictionary<string, string>
            {
                ["claude-code"] = "claude-code.party",
                ["codex"] = "pi.shades",             // another agent's icon: dropped
                ["mystery"] = "mystery.original",    // unknown agent: dropped
                ["pi"] = "  pi.walker ",             // trimmed
                ["opencode"] = "opencode.original",  // the default: not stored
            },
        };
        Equal("claude-code=claude-code.party|pi=pi.walker", Show(s.Icons), "normalised");
        Equal("codex.original", s.IconFor("codex"), "unset agent");
        Equal("opencode.original", s.IconFor("opencode"), "original");

        s.Icons = new Dictionary<string, string> { ["codex"] = custom, ["pi"] = "custom:../../evil", ["opencode"] = "custom:ABC" };
        Equal("codex=" + custom, Show(s.Icons), "only well-formed custom ids are kept");
        s.Icons = null!;
        Equal(0, s.Icons.Count, "null");

        // Read-only and replaced on edit, so a copy never changes under the reader.
        s.Icons = IconChoices.With(s.Icons, "pi", "pi.pie");
        var snapshot = s.Clone();
        s.Icons = IconChoices.With(s.Icons, "pi", "pi.shades");
        Equal("pi.pie", snapshot.IconFor("pi"), "snapshot");
        bool readOnly = false;
        try { ((IDictionary<string, string>)s.Icons).Add("codex", "codex.spin"); }
        catch (NotSupportedException) { readOnly = true; }
        True(readOnly, "the icon map must be read-only");

        // settings.json: a round trip, and a missing or null field loads as all originals.
        string path = Path.Combine(dir, "settings.json");
        s.Icons = new Dictionary<string, string> { ["pi"] = "pi.shades", ["codex"] = custom };
        SettingsStore.Save(s, path);
        Equal("codex=" + custom + "|pi=pi.shades", Show(SettingsStore.Load(path).Icons), "round trip");
        File.WriteAllText(path, "{\"Icons\": null, \"Size\": 100}");
        var loaded = SettingsStore.Load(path);
        Equal(0, loaded.Icons.Count, "null field");
        Equal(100, loaded.Size, "null field keeps the rest");
        File.WriteAllText(path, "{\"Size\": 100}");
        Equal(0, SettingsStore.Load(path).Icons.Count, "missing field");
        File.WriteAllText(path, "{\"Icons\": {\"pi\": \"pi.nope\", \"codex\": \"codex.sleepy\"}}");
        Equal("codex=codex.sleepy", Show(SettingsStore.Load(path).Icons), "hand-edited unknown built-in dropped");

        // Part of the look.
        Equal(false, s.HasDefaultAppearance(), "icons chosen");
        s.ResetAppearance();
        Equal(true, s.HasDefaultAppearance(), "after reset");
        Equal(0, s.Icons.Count, "reset clears icons");
    });

    static string Show(IReadOnlyDictionary<string, string> icons)
        => string.Join('|', icons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    // ---------- sanitiser ----------

    public static void Sanitizer()
    {
        string Clean(string body) => SvgSanitizer.Sanitize($"<svg xmlns=\"{Ns}\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 10 10\">{body}</svg>");
        void Rejects(string text, string reason)
        {
            try { SvgSanitizer.Sanitize(text); }
            catch (InvalidIconException ex)
            {
                True(ex.Message.Contains(reason, StringComparison.OrdinalIgnoreCase), $"reason '{ex.Message}' should mention '{reason}'");
                return;
            }
            throw new InvalidOperationException("should have been rejected: " + text[..Math.Min(80, text.Length)]);
        }

        // Active content goes.
        string active = Clean("<script>alert(1)</script><rect width=\"9\" height=\"9\" onclick=\"x()\" ONLOAD=\"y()\"/>"
                              + "<foreignObject><div xmlns=\"http://www.w3.org/1999/xhtml\"><style>@import url(http://x)</style></div></foreignObject>"
                              + "<iframe/><audio/><video/>");
        foreach (string gone in new[] { "script", "onclick", "ONLOAD", "foreignObject", "iframe", "audio", "video", "import" })
            True(!active.Contains(gone, StringComparison.Ordinal), $"'{gone}' should be removed: {active}");
        True(active.Contains("<rect"), "the drawing stays");

        // Links: only in-file fragments, and inline raster images on <image>.
        const string png = "data:image/png;base64,iVBORw0KGgo=";
        string links = Clean($"<defs><rect id=\"r\" width=\"5\" height=\"5\"/></defs><use href=\"#r\"/><use xlink:href=\"#r\"/>"
                             + "<image href=\"https://evil.example/p.png\" width=\"5\" height=\"5\"/>"
                             + "<image xlink:href=\"file:///C:/secret.png\" width=\"5\" height=\"5\"/>"
                             + "<use href=\"other.svg#r\"/><a href=\"http://example.com\"><rect width=\"1\" height=\"1\"/></a>"
                             + $"<image href=\"{png}\" width=\"5\" height=\"5\"/><use href=\"{png}\"/>"
                             + "<image href=\"data:image/svg+xml;base64,PHN2Zz4=\" width=\"5\" height=\"5\"/>");
        foreach (string gone in new[] { "evil.example", "file:", "other.svg", "example.com", "svg+xml" })
            True(!links.Contains(gone), $"'{gone}' should be removed: {links}");
        Equal(2, CountOf(links, "\"#r\""), "both fragment links kept");
        Equal(1, CountOf(links, png), "the inline PNG is kept on <image> only");

        // In-file CSS references are fine; anything else is rejected, as is @import.
        string css = Clean("<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\"/></linearGradient></defs>"
                           + "<style>.a{fill:url(#g)}</style><rect class=\"a\" fill=\"url(#g)\" style=\"stroke:url( '#g' )\" width=\"9\" height=\"9\"/>");
        True(css.Contains("url(#g)"), "in-file url() kept");
        Rejects(Wrap("<style>@import 'x.css';</style>"), "imports");
        Rejects(Wrap("<rect style=\"fill:url(http://x/p.svg#g)\" width=\"9\" height=\"9\"/>"), "outside");
        Rejects(Wrap("<rect fill=\"url(https://x/p.svg#g)\" width=\"9\" height=\"9\"/>"), "outside");
        Rejects(Wrap("<style>.a{fill:url(file:///c:/x)}</style>"), "outside");

        // Malformed, hostile or oversized files are refused.
        Rejects($"<!DOCTYPE svg [<!ENTITY x \"boom\">]><svg xmlns=\"{Ns}\">&x;</svg>", "DTD");
        Rejects("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>", "root");
        Rejects("<svg viewBox=\"0 0 1 1\"/>", "root"); // not in the SVG namespace
        Rejects("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect>", "XML");
        Rejects("not xml at all", "XML");
        Rejects(Wrap("<!--" + new string('x', SvgSanitizer.MaxBytes) + "-->"), "256 KB");
        Rejects(Wrap(string.Concat(Enumerable.Repeat("<g/>", SvgSanitizer.MaxElements)) ), "elements");
        Rejects(Wrap(string.Concat(Enumerable.Repeat("<g>", 70)) + string.Concat(Enumerable.Repeat("</g>", 70))), "nested");

        // The output is a well-formed SVG the renderer can draw.
        foreach (string output in new[] { active, links, css })
        {
            Equal(XName.Get("svg", Ns), XDocument.Parse(output).Root!.Name, "root");
            True(TokenSprites.VisibleSize(output, 64) is not null, "output draws: " + output);
        }
        Equal(SvgSanitizer.Sanitize(Square), SvgSanitizer.Sanitize(SvgSanitizer.Sanitize(Square)), "idempotent");
    }

    static string Wrap(string body) => $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 10 10\"><rect width=\"9\" height=\"9\"/>{body}</svg>";

    static int CountOf(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ---------- custom store ----------

    public static void CustomStore() => InTemp(root =>
    {
        var store = new CustomIconStore(root);
        string id = store.Add("pi", Square);
        True(IconCatalog.IsCustomId(id), "id shape: " + id);
        Equal(id, store.Add("pi", Square), "same picture, same id");
        Equal(id, store.Add("pi", Square.Replace("<rect", "<!-- a comment --><rect")), "comments don't change the stored picture");
        string path = store.PathFor("pi", id)!;
        Equal(SvgSanitizer.Sanitize(Square), File.ReadAllText(path), "the sanitised text is what's kept");
        var written = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        store.Add("pi", Square);
        Equal(written, File.GetLastWriteTimeUtc(path), "an existing icon is never rewritten");
        Equal(id, string.Join(',', store.List("pi")), "listed");
        Equal(0, store.List("codex").Count, "per agent");
        Equal(0, Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length, "no temp files left");

        // Ids that aren't ours never become paths.
        Equal<string?>(null, store.PathFor("pi", "custom:../../../x"), "traversal");
        Equal<string?>(null, store.PathFor("pi", "pi.walker"), "built-in");
        Equal<string?>(null, store.PathFor("mystery", id), "unknown agent");

        // An icon that draws nothing is refused, with the reason.
        string reason = "";
        try { store.Add("pi", $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 10 10\"><g/></svg>"); }
        catch (InvalidIconException ex) { reason = ex.Message; }
        True(reason.Contains("rendered empty"), "empty drawing reason: " + reason);

        // Removing the icon an agent shows puts the agent back on its original first.
        var live = new LiveSettings(new CursorSettings(), _ => { });
        live.Change(s => s.Icons = IconChoices.With(IconChoices.With(s.Icons, "pi", id), "codex", "codex.spin"));
        store.Remove("pi", id, live);
        Equal("pi.original", live.Live.IconFor("pi"), "removed icon deselected");
        Equal("codex.spin", live.Live.IconFor("codex"), "other agents untouched");
        Equal(false, File.Exists(path), "file deleted");

        // At most MaxPerTool per agent; re-adding one already kept still works at the limit.
        string first = "";
        for (int i = 0; i < CustomIconStore.MaxPerTool; i++)
        {
            string added = store.Add("codex", Square.Replace("width=\"10\"", $"width=\"{i + 1}\""));
            if (i == 0) first = added;
        }
        Equal(CustomIconStore.MaxPerTool, store.List("codex").Count, "filled up");
        bool limited = false;
        try { store.Add("codex", Square.Replace("#3A7", "#A37")); }
        catch (InvalidIconException ex) { limited = ex.Message.Contains(CustomIconStore.MaxPerTool.ToString()); }
        True(limited, "the 25th icon is refused");
        Equal(first, store.Add("codex", Square.Replace("width=\"10\"", "width=\"1\"")), "an existing icon at the limit");

        // Adds racing each other at the limit never break it: 22 kept, 6 more at once, 24 in the end.
        for (int i = 0; i < 22; i++)
            store.Add("opencode", Square.Replace("width=\"10\"", $"width=\"{i + 1}\"").Replace("#3A7", "#7A3"));
        int refused = 0;
        Parallel.For(0, 6, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i =>
        {
            try { store.Add("opencode", Square.Replace("width=\"10\"", $"width=\"{i + 40}\"").Replace("#3A7", "#7A3")); }
            catch (InvalidIconException) { Interlocked.Increment(ref refused); }
        });
        Equal(4, refused, "refused past the limit");
        Equal(CustomIconStore.MaxPerTool, store.List("opencode").Count, "exactly the limit");
    });

    // ---------- renderer fallback ----------

    public static void CustomFallback() => InTemp(root =>
    {
        var previousStore = IconSource.Custom;
        var previousLog = IconSource.Log;
        var log = new List<string>();
        IconSource.Custom = new CustomIconStore(root);
        IconSource.Log = message => { lock (log) log.Add(message); };
        try
        {
            string missing = NewCustomId(), broken = NewCustomId(), empty = NewCustomId();
            Directory.CreateDirectory(Path.Combine(root, "codex"));
            File.WriteAllText(IconSource.Custom.PathFor("codex", broken)!, "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect");
            File.WriteAllText(IconSource.Custom.PathFor("codex", empty)!, $"<svg xmlns=\"{Ns}\" viewBox=\"0 0 10 10\"><g/></svg>");

            using var original = Draw("codex", "codex.original");
            foreach (string id in new[] { missing, broken, empty })
            {
                for (int renderer = 0; renderer < 2; renderer++) // a fresh renderer each time
                {
                    using var drawn = Draw("codex", id);
                    True(SamePixels(original, drawn), $"{id} should draw the original logo");
                }
                Equal(1, log.Count(m => m.Contains(id)), $"{id} logged once");
            }

            // Two threads draw an added icon at once, each with its own renderer.
            string good = IconSource.Custom.Add("pi", Square);
            var settings = new CursorSettings { Icons = new Dictionary<string, string> { ["pi"] = good } };
            int failures = 0, frames = 0;
            var until = DateTime.UtcNow.AddSeconds(1);
            var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
            {
                using var renderer = new NotifierRenderer();
                using var canvas = new Bitmap(200, 200);
                while (DateTime.UtcNow < until)
                {
                    try
                    {
                        using var g = Graphics.FromImage(canvas);
                        renderer.DrawToken(g, settings, new TokenFace("pi", true, 2), 100f, 100f, 80f + frames % 3 * 8f, frames % 20 - 10f);
                        Interlocked.Increment(ref frames);
                    }
                    catch (Exception) { Interlocked.Increment(ref failures); }
                }
            })).ToArray();
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            Equal(0, failures, "concurrent failures");
            True(frames > 50, "frames drawn: " + frames);
            using var added = Draw("pi", good);
            using var piOriginal = Draw("pi", "pi.original");
            True(!SamePixels(piOriginal, added), "the added icon itself is drawn");
        }
        finally
        {
            IconSource.Custom = previousStore;
            IconSource.Log = previousLog;
        }
    });

    static Bitmap Draw(string tool, string iconId, int side = 96)
    {
        var bitmap = new Bitmap(side * 2, side * 2, PixelFormat.Format32bppArgb);
        using var renderer = new NotifierRenderer();
        using var g = Graphics.FromImage(bitmap);
        renderer.DrawToken(g, new CursorSettings(), new TokenFace(tool, false, 0), iconId, side, side, side, 0f);
        return bitmap;
    }

    static bool SamePixels(Bitmap a, Bitmap b) => Pixels(a).AsSpan().SequenceEqual(Pixels(b));

    static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bitmap.Width * 4, bitmap.Width * 4);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }

    // ---------- built-in icons ----------

    public static void BuiltInMascots()
    {
        Equal(16, IconCatalog.BuiltIns.Count, "four icons per agent");
        using var sprites = new TokenSprites();
        foreach (var icon in IconCatalog.BuiltIns)
        {
            using (var stream = typeof(IconCatalog).Assembly.GetManifestResourceStream(icon.Resource))
            {
                True(stream is not null, icon.Resource + " is embedded");
                True(stream!.Length <= 8 * 1024, $"{icon.Id} is {stream.Length} bytes; at most 8 KB");
            }
            const int side = 48;
            var sprite = sprites.Get(icon.Tool, icon.Id, side, side * 0.06f)!;
            var v = sprite.Visual;
            float aspect = v.Width / v.Height;
            True(aspect >= 0.6f && aspect <= 1.6f, $"{icon.Id} aspect {aspect:0.00}");
            int opaque = 0, area = 0;
            for (int y = (int)v.Top; y < (int)v.Bottom; y++)
                for (int x = (int)v.Left; x < (int)v.Right; x++)
                {
                    area++;
                    if (sprite.Image.GetPixel(x + sprite.Pad, y + sprite.Pad).A > 200) opaque++;
                }
            True(opaque >= area * 0.2, $"{icon.Id} covers {opaque}/{area} px of its bounds");
        }
        // Each mascot is its own picture, not the original drawn as a fallback.
        foreach (string tool in IconCatalog.Tools)
        {
            using var original = Draw(tool, IconCatalog.OriginalId(tool), 64);
            foreach (var icon in IconCatalog.ForTool(tool).Skip(1))
            {
                using var mascot = Draw(tool, icon.Id, 64);
                True(!SamePixels(original, mascot), icon.Id + " differs from the original");
            }
        }
    }

    // ---------- canvas ----------

    public static void CanvasExtent()
    {
        var settings = new CursorSettings();
        using var renderer = new NotifierRenderer();
        foreach (int size in new[] { 48, 96, 192 })
        {
            int side = TokenCanvas.Side(size);
            using var canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            foreach (var icon in IconCatalog.BuiltIns)
                foreach (bool blocked in new[] { true, false })
                    for (int swing = -180; swing <= 180; swing += 15)
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.Clear(Color.Transparent);
                            renderer.DrawToken(g, settings, new TokenFace(icon.Tool, blocked, 9), icon.Id, side / 2f, side / 2f, size, swing);
                        }
                        var px = Pixels(canvas);
                        for (int i = 0; i < side; i++)
                            foreach (var (x, y) in new[] { (i, 0), (i, side - 1), (0, i), (side - 1, i) })
                                if (px[(y * side + x) * 4 + 3] != 0)
                                    throw new InvalidOperationException($"{icon.Id} {size}px blocked={blocked} swing {swing}: pixel on the canvas edge at {x},{y}.");
                    }
        }
    }

    // ---------- motion ----------

    public static void Tether()
    {
        const float t = 96f, dt = 1f / 120f;
        foreach (var placement in new[] { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer })
        {
            var rest = TokenMotion.RestFor(placement, t, 1f, Flip.None);
            float len = rest.Length, ux = rest.Dx / len, uy = rest.Dy / len;

            // The entrance hops back along the string, then springs out and settles at rest.
            var s = new TetherState();
            TokenMotion.Step(ref s, 500, 500, rest, t, dt, entrance: true);
            float along = (s.X - 500) * ux + (s.Y - 500) * uy, across = (s.X - 500) * -uy + (s.Y - 500) * ux;
            True(along < len - 1f && MathF.Abs(across) < 0.01f, $"{placement}: entrance along {along:0.0} (rest {len:0.0}), across {across:0.00}");
            True(s.Vx * ux + s.Vy * uy < 0, $"{placement}: the hop heads back toward the pointer");
            float settled = float.MaxValue;
            for (int i = 1; i < 6 * 120; i++)
            {
                TokenMotion.Step(ref s, 500, 500, rest, t, dt, entrance: false);
                float off = MathF.Max(MathF.Abs(s.X - 500 - rest.Dx), MathF.Abs(s.Y - 500 - rest.Dy));
                if (off > 1f) settled = float.MaxValue;
                else if (settled == float.MaxValue) settled = i * dt;
            }
            True(settled <= 2.5f, $"{placement}: within 1 px of rest from {settled:0.00} s");
            var pose = TokenMotion.Step(ref s, 500, 500, rest, t, dt, false);
            True(MathF.Abs(pose.SwingDeg) < 0.5f, $"{placement}: upright at rest ({pose.SwingDeg:0.00})");

            // The string never stretches past its slack, however the pointer jerks.
            var rng = new Random(7);
            float ax = 500, ay = 500, maxLen = len + TokenMotion.Slack * t + 0.01f;
            for (int i = 0; i < 2000; i++)
            {
                if (i % 7 == 0) { ax = rng.Next(-3000, 3000); ay = rng.Next(-3000, 3000); }
                var p = TokenMotion.Step(ref s, ax, ay, rest, t, 0.05f, false,
                    placement == NotifierPlacement.BesidePointer ? TokenMotion.BesideMaxSwing : 180f);
                float d = MathF.Sqrt((s.X - ax) * (s.X - ax) + (s.Y - ay) * (s.Y - ay));
                True(d <= maxLen, $"{placement}: string at {d:0.0} > {maxLen:0.0}");
                if (placement == NotifierPlacement.BesidePointer)
                    True(MathF.Abs(p.SwingDeg) <= TokenMotion.BesideMaxSwing + 0.001f, "beside tilt clamped");
            }
        }

        // In motion too: however the pointer jerks along the edges, the token (with its badge) never
        // crosses the work area's edge, in any frame, for any placement.
        var screen = new RectangleF(0, 0, 1000, 800);
        foreach (var placement in new[] { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer })
        {
            var s = new TetherState();
            var moving = Flip.None;
            var rng = new Random(11);
            float h = TokenMotion.RestHalfExtent(t), px = 500, py = 400;
            for (int i = 0; i < 4000; i++)
            {
                if (i % 9 == 0) { px = rng.Next(2) == 0 ? rng.Next(0, 40) : rng.Next(960, 1000); py = rng.Next(0, 800); }
                var at = new PointF(px, py);
                moving = TokenMotion.Edges(moving, at, TokenMotion.RestFor(placement, t, 1f, Flip.None), t, screen);
                var kept = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, t, 1f, moving), at, t, screen);
                TokenMotion.Step(ref s, px, py, kept, t, 1f / 60f, i == 0, 180f, screen);
                True(s.X - h >= -0.01f && s.X + h <= screen.Right + 0.01f && s.Y - h >= -0.01f && s.Y + h <= screen.Bottom + 0.01f,
                    $"{placement} frame {i}: token at {s.X:0},{s.Y:0} crosses the screen edge");
            }
        }

        // Edges: flip as soon as the token would leave, back only with room to spare.
        var work = new RectangleF(0, 0, 1000, 800);
        var beside = TokenMotion.RestFor(NotifierPlacement.BesidePointer, t, 1f, Flip.None);
        Equal(Flip.None, TokenMotion.Edges(Flip.None, new PointF(500, 400), beside, t, work), "beside, mid-screen");
        Equal(Flip.X, TokenMotion.Edges(Flip.None, new PointF(900, 400), beside, t, work), "beside, right edge");
        Equal(Flip.Y, TokenMotion.Edges(Flip.None, new PointF(500, 780), beside, t, work), "beside, bottom edge");
        Equal(Flip.X | Flip.Y, TokenMotion.Edges(Flip.None, new PointF(990, 790), beside, t, work), "beside, bottom-right corner");
        var above = TokenMotion.RestFor(NotifierPlacement.AbovePointer, t, 1f, Flip.None);
        Equal(Flip.Y, TokenMotion.Edges(Flip.None, new PointF(500, 40), above, t, work), "above, top edge");
        Equal(Flip.None, TokenMotion.Edges(Flip.X, new PointF(990, 400), above, t, work), "above never flips sideways");
        var below = TokenMotion.RestFor(NotifierPlacement.BelowPointer, t, 1f, Flip.None);
        Equal(Flip.Y, TokenMotion.Edges(Flip.None, new PointF(500, 790), below, t, work), "below, bottom edge");
        Equal(new TokenMotion.Rest(-beside.Dx, -beside.Dy), TokenMotion.RestFor(NotifierPlacement.BesidePointer, t, 1f, Flip.X | Flip.Y), "mirrored rest");

        // Parked between "fits" and "room to spare", the choice never flip-flops.
        float half = TokenMotion.RestHalfExtent(t);
        float fitsUpTo = work.Right - half - beside.Dx, roomyUpTo = fitsUpTo - 0.25f * t;
        float parked = (fitsUpTo + roomyUpTo) / 2f;
        var flip = TokenMotion.Edges(Flip.None, new PointF(work.Right - 10, 400), beside, t, work);
        for (int i = 0; i < 10; i++) flip = TokenMotion.Edges(flip, new PointF(parked, 400), beside, t, work);
        Equal(Flip.X, flip, "stays flipped while parked");
        flip = Flip.None;
        for (int i = 0; i < 10; i++) flip = TokenMotion.Edges(flip, new PointF(parked, 400), beside, t, work);
        Equal(Flip.None, flip, "stays unflipped while parked");
        Equal(Flip.None, TokenMotion.Edges(Flip.X, new PointF(roomyUpTo - 1, 400), beside, t, work), "flips back with room");

        // Where mirroring can't help (below the pointer at a side edge, any rest in a corner), the rest
        // slides along the edge so a token resting there stays on screen, and stays upright.
        foreach (var placement in new[] { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer })
            foreach (var at in new[] { new PointF(0, 0), new PointF(999, 0), new PointF(0, 799), new PointF(999, 799), new PointF(500, 0), new PointF(999, 400) })
            {
                var plain = TokenMotion.RestFor(placement, t, 1f, Flip.None);
                var kept = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, t, 1f, TokenMotion.Edges(Flip.None, at, plain, t, work)), at, t, work);
                float cx = at.X + kept.Dx, cy = at.Y + kept.Dy, h = TokenMotion.RestHalfExtent(t);
                True(cx - h >= work.Left - 0.01f && cx + h <= work.Right + 0.01f && cy - h >= work.Top - 0.01f && cy + h <= work.Bottom + 0.01f,
                    $"{placement} at {at}: resting token at {cx:0},{cy:0} leaves the work area");
                var s = new TetherState();
                for (int i = 0; i < 600; i++) TokenMotion.Step(ref s, at.X, at.Y, kept, t, dt, false);
                True(MathF.Abs(TokenMotion.Step(ref s, at.X, at.Y, kept, t, dt, false).SwingDeg) < 0.5f, $"{placement} at {at}: upright at rest");
            }
        var untouched = TokenMotion.KeepOnScreen(below, new PointF(500, 400), t, work);
        True(MathF.Abs(untouched.Dx - below.Dx) < 1e-3f && MathF.Abs(untouched.Dy - below.Dy) < 1e-3f, "mid-screen rest untouched");

        // The corner bounce is unchanged.
        var corner = TokenMotion.CornerBounce(0.7f, t);
        Equal(t * 0.04f * MathF.Sin(0.7f * 1.6f), corner.OffsetX, "corner x");
        Equal(-t * 0.22f * MathF.Abs(MathF.Sin(0.7f * 3.0f)), corner.OffsetY, "corner y");
        Equal(3f * MathF.Sin(0.7f * 1.6f), corner.SwingDeg, "corner tilt");
    }
}
