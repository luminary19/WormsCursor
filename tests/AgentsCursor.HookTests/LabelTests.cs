using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AgentsCursor.Core;

/// <summary>The project label under the token: the name it shows, which session it belongs to, the
/// setting and theme, and that it fits the canvas and keeps on screen.</summary>
static class LabelTests
{
    static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}{(what is null ? "" : ": ")}expected '{expected}', got '{actual}'.");
    }

    static void Near(float expected, float actual, string what)
        => True(MathF.Abs(expected - actual) < 1e-3f, $"{what}: expected {expected}, got {actual}");

    static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    const string Short = "Lumicity", Long = "agents-cursor-integration-experiments-2026";

    public static void ProjectNames()
    {
        Equal("Lumicity", ProjectName.Of(@"C:\Users\hoang\Lumicity"), "a folder");
        Equal("Lumicity", ProjectName.Of(@"C:\Users\hoang\Lumicity\"), "a trailing separator");
        Equal("Lumicity", ProjectName.Of(@"C:\Users\hoang\Lumicity\\//"), "several trailing separators");
        Equal("WormsCursor", ProjectName.Of("C:/Users/hoang/Projects/WormsCursor"), "forward slashes");
        Equal("app", ProjectName.Of("/home/me/app/"), "a POSIX path");
        Equal("Lumicity", ProjectName.Of("  C:\\Lumicity  "), "surrounding blanks");
        Equal("my project", ProjectName.Of(@"D:\work\my project"), "spaces inside the name");
        Equal(@"C:\", ProjectName.Of(@"C:\"), "a drive root");
        Equal(@"D:\", ProjectName.Of("d:".ToUpperInvariant()), "a bare drive");
        Equal(@"C:\", ProjectName.Of(@"\\?\C:\"), "an extended-length drive root");
        Equal("repo", ProjectName.Of(@"\\?\C:\src\repo"), "an extended-length path");
        Equal("repo", ProjectName.Of(@"\\server\share\repo"), "a UNC folder");
        Equal("share", ProjectName.Of(@"\\server\share\"), "a UNC share");
        Equal("server", ProjectName.Of(@"\\server"), "a UNC server");
        Equal("/", ProjectName.Of("/"), "the POSIX root");
        Equal(null, ProjectName.Of(null), "missing");
        Equal(null, ProjectName.Of(""), "empty");
        Equal(null, ProjectName.Of("   "), "blank");
    }

    public static void FaceAndSetting()
    {
        // The face carries the project of the session it shows: the blocked one first.
        var waiting = new[]
        {
            new WaitingAgent("claude-code", WaitingState.Finished, "a", "Lumicity"),
            new WaitingAgent("codex", WaitingState.Blocked, "b", "WormsCursor"),
            new WaitingAgent("pi", WaitingState.Blocked, "c", "Other"),
        };
        Equal(new TokenFace("codex", true, 2, "WormsCursor"), TokenFace.For(waiting), "the blocked session's project");
        Equal(new TokenFace("claude-code", false, 0, "Lumicity"), TokenFace.For(waiting[..1]), "the only session's project");
        Equal(new TokenFace("codex", false, 0, null), TokenFace.For(new[] { new WaitingAgent("codex", WaitingState.Finished) }), "no project");

        // The activity names each session after its working directory, keeps the name through events
        // that send none, and follows a new one.
        var now = DateTime.UtcNow;
        var activity = new AgentActivity(TimeSpan.FromMinutes(1));
        activity.Report("claude-code", "one", @"C:\Users\hoang\Lumicity\", AgentEventKind.TurnComplete, now);
        activity.Report("codex", "two", null, AgentEventKind.AwaitingUser, now);
        Equal(new TokenFace("codex", true, 1, null), TokenFace.For(activity.Waiting), "a blocked session with no cwd has no label");
        activity.Report("codex", "two", @"D:\repo\WormsCursor", AgentEventKind.AwaitingUser, now);
        Equal(new TokenFace("codex", true, 1, "WormsCursor"), TokenFace.For(activity.Waiting), "named once it sends one");
        activity.Report("codex", "two", null, AgentEventKind.AwaitingUser, now);
        Equal("WormsCursor", TokenFace.For(activity.Waiting)!.Project, "an event without a cwd keeps the name");
        activity.Report("codex", "two", null, AgentEventKind.ToolUse, now);
        Equal(new TokenFace("claude-code", false, 0, "Lumicity"), TokenFace.For(activity.Waiting), "the token goes back to the other session's project");
        activity.Report("claude-code", "one", @"C:\elsewhere", AgentEventKind.TurnComplete, now);
        Equal("elsewhere", TokenFace.For(activity.Waiting)!.Project, "a new cwd renames it");

        // The setting: on by default, off means no label, a face without a project has none.
        var s = new CursorSettings();
        Equal(true, s.ShowProjectName, "on by default");
        var face = new TokenFace("codex", true, 0, "Lumicity");
        Equal(new TokenLabel("Lumicity", true), TokenLabel.For(s, face, dark: true), "on");
        Equal(null, TokenLabel.For(s, face with { Project = null }, dark: true), "no project");
        s.ShowProjectName = false;
        Equal(null, TokenLabel.For(s, face, dark: false), "off");
        Equal(false, s.HasDefaultDisplay(), "part of the Display group");
        s.ResetDisplay();
        Equal(true, s.ShowProjectName, "Reset display turns it back on");

        // Theme: the panel's setting, System following Windows' app mode.
        Equal(true, TokenLabel.IsDark(ThemePreference.Dark, windowsDark: false), "Dark");
        Equal(false, TokenLabel.IsDark(ThemePreference.Light, windowsDark: true), "Light");
        Equal(true, TokenLabel.IsDark(ThemePreference.System, windowsDark: true), "System, dark Windows");
        Equal(false, TokenLabel.IsDark(ThemePreference.System, windowsDark: false), "System, light Windows");

        // It round-trips through settings.json.
        string dir = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.json");
            SettingsStore.Save(new CursorSettings { ShowProjectName = false }, path);
            Equal(false, SettingsStore.Load(path).ShowProjectName, "saved off");
            File.WriteAllText(path, "{\"Size\": 120}");
            Equal(true, SettingsStore.Load(path).ShowProjectName, "an older file gets the default");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Every icon with its badges, the +N and a short or long label, in both themes, at every swing and
    // at 96 to 200% DPI: nothing reaches the canvas edge. Upright, everything drawn stays inside the
    // footprint, which is what keeps it on screen. A long name is cut short with an ellipsis.
    public static void LabelFits()
    {
        var settings = new CursorSettings();
        using var renderer = new NotifierRenderer();
        foreach (var (size, dpi) in new[] { (48f, 1f), (96f, 1f), (192f, 1f), (72f, 1.5f), (96f, 2f), (384f, 2f) })
            foreach (string name in new[] { Short, Long })
                foreach (bool dark in new[] { true, false })
                {
                    var label = new TokenLabel(name, dark);
                    var labelSize = renderer.LabelSize(label, size, dpi);
                    True(labelSize.Width <= TokenLabel.MaxTextPx(size, dpi) + 4f * TokenLabel.EmPx(size, dpi), $"{name} {size}px: {labelSize.Width} px wide");
                    var token = new TokenFootprint(size, labelSize);
                    int side = TokenCanvas.Side(token);
                    using var canvas = new Bitmap(side, side, PixelFormat.Format32bppArgb);
                    foreach (var icon in IconCatalog.BuiltIns)
                        for (int swing = -180; swing <= 180; swing += dark ? 30 : 45)
                        {
                            using (var g = Graphics.FromImage(canvas))
                            {
                                g.Clear(Color.Transparent);
                                // Off the pixel grid, as the overlay's sub-pixel remainder puts it.
                                renderer.DrawToken(g, settings, new TokenFace(icon.Tool, swing % 60 == 0, 9, name), icon.Id,
                                    side / 2f + 0.4f, side / 2f + 0.6f, size, swing, dpi, null, label);
                            }
                            var px = Pixels(canvas);
                            for (int i = 0; i < side; i++)
                                foreach (var (x, y) in new[] { (i, 0), (i, side - 1), (0, i), (side - 1, i) })
                                    if (px[(y * side + x) * 4 + 3] != 0)
                                        throw new InvalidOperationException($"{icon.Id} {size}px@{dpi} '{name}' swing {swing}: pixel on the canvas edge at {x},{y}.");
                            if (swing != 0) continue;
                            float cx = side / 2f + 0.4f, cy = side / 2f + 0.6f;
                            for (int y = 0; y < side; y++)
                                for (int x = 0; x < side; x++)
                                    if (px[(y * side + x) * 4 + 3] >= 16
                                        && (x + 1 < cx - token.Left || x > cx + token.Right || y + 1 < cy - token.Top || y > cy + token.Bottom))
                                        throw new InvalidOperationException($"{icon.Id} {size}px@{dpi} '{name}': pixel at {x},{y} outside the footprint.");
                        }
                }
        // A long name is cut to the widest text plus the box's padding and edge; a short one is whole.
        float em = TokenLabel.EmPx(96f, 1f);
        var cut = renderer.LabelSize(new TokenLabel(Long, true), 96f, 1f);
        var whole = renderer.LabelSize(new TokenLabel(Short, true), 96f, 1f);
        True(cut.Width <= TokenLabel.MaxTextPx(96f, 1f) + 1.2f * em + 3f, $"a long name is cut ({cut.Width} px)");
        True(cut.Width > TokenLabel.MaxTextPx(96f, 1f) - em && whole.Width < cut.Width / 2f, $"cut near the limit, short name whole ({cut.Width}, {whole.Width})");
        Equal(cut.Height, whole.Height, "one height whatever the name");
    }

    // With a label, every pointer placement rests clear of the pointer's tip when above it, and the
    // whole footprint keeps inside the work area at every edge and corner, resting and in motion; a
    // corner token keeps all of it inside its margin.
    public static void LabelKeepsOnScreen()
    {
        using var renderer = new NotifierRenderer();
        const float t = 96f;
        var token = new TokenFootprint(t, renderer.LabelSize(new TokenLabel(Long, true), t, 1f));
        True(token.Bottom > token.Top && token.Left > token.Half, "a long label reaches further down and sideways than the icon");
        var work = new RectangleF(0, 0, 1000, 800);

        var above = TokenMotion.RestFor(NotifierPlacement.AbovePointer, token, 1f, Flip.None);
        True(above.Dy + token.Below < 0f, $"above: the label ends above the pointer's tip ({above.Dy + token.Below:0.0})");
        Near(-0.62f * t, TokenMotion.RestFor(NotifierPlacement.AbovePointer, new TokenFootprint(t), 1f, Flip.None).Dy, "no label: the original above rest");
        var besideUp = TokenMotion.RestFor(NotifierPlacement.BesidePointer, token, 1f, Flip.Y);
        Near(-(18f - 0.2f * t) - token.Below, besideUp.Dy, "beside flipped up: the label's bottom where the token's top was");
        Near(-(18f + 0.3f * t), TokenMotion.RestFor(NotifierPlacement.BesidePointer, new TokenFootprint(t), 1f, Flip.Y).Dy, "no label: the original flipped rest");

        bool Inside(float cx, float cy) => cx - token.Left >= work.Left - 0.01f && cx + token.Right <= work.Right + 0.01f
                                           && cy - token.Top >= work.Top - 0.01f && cy + token.Bottom <= work.Bottom + 0.01f;
        foreach (var placement in new[] { NotifierPlacement.BelowPointer, NotifierPlacement.AbovePointer, NotifierPlacement.BesidePointer })
        {
            foreach (var at in new[] { new PointF(0, 0), new PointF(999, 0), new PointF(0, 799), new PointF(999, 799), new PointF(500, 799), new PointF(999, 400), new PointF(0, 400) })
            {
                var plain = TokenMotion.RestFor(placement, token, 1f, Flip.None);
                var kept = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, token, 1f, TokenMotion.Edges(Flip.None, at, plain, token, work)), at, token, work);
                True(Inside(at.X + kept.Dx, at.Y + kept.Dy), $"{placement} at {at}: the resting token with its label leaves the work area");
            }
            var s = new TetherState();
            var flip = Flip.None;
            var rng = new Random(5);
            float px = 500, py = 400;
            foreach (var motion in new[] { MotionProfile.Bouncy, MotionProfile.Calm, MotionProfile.Still })
                for (int i = 0; i < 3000; i++)
                {
                    if (i % 9 == 0) { px = rng.Next(2) == 0 ? rng.Next(0, 40) : rng.Next(960, 1000); py = rng.Next(0, 800); }
                    var at = new PointF(px, py);
                    flip = TokenMotion.Edges(flip, at, TokenMotion.RestFor(placement, token, 1f, Flip.None), token, work);
                    var kept = TokenMotion.KeepOnScreen(TokenMotion.RestFor(placement, token, 1f, flip), at, token, work);
                    TokenMotion.Step(ref s, px, py, kept, token, 1f / 60f, i == 0, motion, motion.MaxSwingFor(placement), work);
                    True(Inside(s.X, s.Y), $"{placement} {motion} frame {i}: token at {s.X:0},{s.Y:0} crosses the screen edge");
                }
        }

        foreach (var corner in Enum.GetValues<ScreenCorner>())
        {
            var anchor = TokenMotion.CornerAnchor(corner, work, token, 16f);
            True(anchor.X - token.Left >= 16f - 0.01f && anchor.X + token.Right <= work.Right - 16f + 0.01f, $"{corner}: inside its margin sideways");
            float hop = TokenMotion.CornerHop * t;
            True(anchor.Y - token.Top - hop >= 16f - 0.01f && anchor.Y + token.Bottom <= work.Bottom - 16f + 0.01f, $"{corner}: inside its margin, hop included");
        }
    }

    static byte[] Pixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
