namespace AgentsCursor.Core;

/// <summary>How a keyframe is reached from the one before it.</summary>
public enum Ease
{
    /// <summary>Eases in and out (smoothstep).</summary>
    Smooth,
    /// <summary>At a constant rate.</summary>
    Linear,
    /// <summary>Holds the previous pose and jumps at the key: pixel-art pose swaps.</summary>
    Step,
}

/// <summary>
/// Where a part of a mascot is at one moment, relative to where its SVG draws it: moved by
/// (<see cref="Dx"/>, <see cref="Dy"/>), turned by <see cref="Turn"/> degrees (clockwise) and scaled by
/// <see cref="ScaleX"/> / <see cref="ScaleY"/> about the part's pivot, at <see cref="Opacity"/> (absolute,
/// 0-1: a part hidden in the file has a rest opacity of 0). All in the part's parent's user units.
/// </summary>
public readonly record struct PartPose(float Dx, float Dy, float Turn, float ScaleX, float ScaleY, float Opacity)
{
    /// <summary>Where the file draws it.</summary>
    public static PartPose Rest { get; } = new(0f, 0f, 0f, 1f, 1f, 1f);

    /// <summary>Whether the two poses draw the same.</summary>
    public bool Near(PartPose other)
        => MathF.Abs(Dx - other.Dx) < 1e-3f && MathF.Abs(Dy - other.Dy) < 1e-3f && TurnsAlike(Turn, other.Turn)
           && MathF.Abs(ScaleX - other.ScaleX) < 1e-4f && MathF.Abs(ScaleY - other.ScaleY) < 1e-4f
           && MathF.Abs(Opacity - other.Opacity) < 1e-3f;

    // Whole turns apart draw the same.
    static bool TurnsAlike(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d < 1e-3f || 360f - d < 1e-3f;
    }

    internal static PartPose Lerp(PartPose a, PartPose b, float u) => new(
        a.Dx + (b.Dx - a.Dx) * u, a.Dy + (b.Dy - a.Dy) * u, a.Turn + (b.Turn - a.Turn) * u,
        a.ScaleX + (b.ScaleX - a.ScaleX) * u, a.ScaleY + (b.ScaleY - a.ScaleY) * u,
        Math.Clamp(a.Opacity + (b.Opacity - a.Opacity) * u, 0f, 1f));
}

/// <summary>A part's pose <paramref name="Pose"/> at <paramref name="At"/> seconds into the loop, reached
/// from the previous key with <paramref name="Ease"/>.</summary>
public readonly record struct MascotKey(float At, PartPose Pose, Ease Ease = Ease.Smooth);

/// <summary>One animated part of a mascot: the SVG element with id <paramref name="Id"/>, the point its
/// turn and scale are about (<paramref name="PivotX"/>, <paramref name="PivotY"/>, in its parent's user
/// units), and its keys in time order. Before the first key it holds that key's pose, after the last
/// the last one's.</summary>
public sealed record MascotPart(string Id, float PivotX, float PivotY, IReadOnlyList<MascotKey> Keys)
{
    /// <summary>The part's pose <paramref name="t"/> seconds into the loop.</summary>
    public PartPose At(float t)
    {
        var keys = Keys;
        if (keys.Count == 0) return PartPose.Rest;
        if (t <= keys[0].At) return keys[0].Pose;
        for (int i = 1; i < keys.Count; i++)
        {
            var (a, b) = (keys[i - 1], keys[i]);
            if (t >= b.At) continue;
            float span = b.At - a.At;
            float u = span <= 0f ? 1f : (t - a.At) / span;
            u = b.Ease switch
            {
                Ease.Step => 0f,
                Ease.Linear => u,
                _ => u * u * (3f - 2f * u),
            };
            return PartPose.Lerp(a.Pose, b.Pose, u);
        }
        return keys[^1].Pose;
    }
}

/// <summary>
/// A mascot's idle loop: <see cref="Seconds"/> long, sampled at <see cref="Fps"/> frames a second, its
/// <see cref="Parts"/> moving by their keys. A loop starts at rest (every part where its file draws it)
/// and spends most of its time there, so the mascot does one small thing every few seconds rather than
/// fidgeting. Samples are numbered 0 to <see cref="FrameCount"/> - 1; each is drawn as the first sample
/// with the same poses (every sample at rest as frame 0), so a host caches one picture per distinct pose. Immutable: any thread may read it.
/// </summary>
public sealed class MascotLoop
{
    readonly int[] _canonical; // sample index -> frame drawn (0 for every sample at rest)

    public MascotLoop(float seconds, int fps, IReadOnlyList<MascotPart> parts)
    {
        if (seconds <= 0f || fps <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Seconds = seconds;
        Fps = fps;
        Parts = parts;
        FrameCount = Math.Max(1, (int)MathF.Round(seconds * fps));
        // Each sample draws as the first one with the same poses: every sample at rest as frame 0, and a
        // held pose as the sample it started on.
        _canonical = new int[FrameCount];
        var poses = Enumerable.Range(0, FrameCount).Select(f => parts.Select(p => p.At(f / (float)fps)).ToArray()).ToArray();
        var frames = new List<int>();
        for (int f = 0; f < FrameCount; f++)
        {
            int same = frames.FindIndex(g => Alike(poses[g], poses[f]));
            if (same < 0) frames.Add(f);
            _canonical[f] = same < 0 ? f : frames[same];
        }
        Frames = frames.ToArray();
    }

    static bool Alike(PartPose[] a, PartPose[] b)
    {
        for (int i = 0; i < a.Length; i++) if (!a[i].Near(b[i])) return false;
        return true;
    }

    public float Seconds { get; }
    public int Fps { get; }
    public IReadOnlyList<MascotPart> Parts { get; }

    /// <summary>The samples in one loop.</summary>
    public int FrameCount { get; }

    /// <summary>The distinct frames, in loop order: 0 (rest), then the first sample of every other pose.</summary>
    public IReadOnlyList<int> Frames { get; }

    /// <summary>The frame to draw <paramref name="clock"/> seconds into the animation (any value, it
    /// wraps): the same clock always gives the same frame.</summary>
    public int FrameAt(double clock)
    {
        if (double.IsNaN(clock) || double.IsInfinity(clock)) return 0;
        long sample = (long)Math.Floor(clock * Fps);
        int index = (int)(((sample % FrameCount) + FrameCount) % FrameCount);
        return _canonical[index];
    }

    /// <summary>The pose of <paramref name="part"/> in <paramref name="frame"/>.</summary>
    public PartPose PoseAt(MascotPart part, int frame) => part.At(frame / (float)Fps);
}

/// <summary>
/// The built-in mascots' idle loops (see <see cref="IconCatalog"/>), by icon id. Original logos and
/// added icons have none and always stand still. Whether loops play at all is <see cref="Animate"/>.
/// The parts are named by <c>id</c> in each mascot's SVG, which draws the rest pose.
/// </summary>
public static class MascotLoops
{
    /// <summary>The loop of the built-in icon <paramref name="iconId"/>, or null when it stands still.</summary>
    public static MascotLoop? For(string? iconId) => iconId is not null && Loops.TryGetValue(iconId, out var loop) ? loop : null;

    /// <summary>Whether mascots move: <see cref="CursorSettings.AnimateMascots"/>, unless the token itself
    /// is still (the Still motion, or Windows' animation effects off).</summary>
    public static bool Animate(CursorSettings s, bool animationEffects)
        => s.AnimateMascots && MotionProfile.Effective(s.Motion, animationEffects) != MotionStyle.Still;

    // ---------- the loops ----------

    static PartPose P(float dx = 0f, float dy = 0f, float turn = 0f, float sx = 1f, float sy = 1f, float o = 1f)
        => new(dx, dy, turn, sx, sy, o);

    static readonly PartPose R = PartPose.Rest;
    static readonly PartPose Hidden = P(o: 0f);

    static MascotKey K(float at, PartPose pose, Ease ease = Ease.Smooth) => new(at, pose, ease);

    static MascotPart Part(string id, float px, float py, params MascotKey[] keys) => new(id, px, py, keys);

    // A part that blinks off (opacity 0) at each of <offs> for <length> seconds, as a caret does.
    static MascotPart Blink(string id, float length, params float[] offs)
    {
        var keys = new List<MascotKey> { K(0f, R) };
        foreach (float at in offs)
        {
            keys.Add(K(at, Hidden, Ease.Step));
            keys.Add(K(at + length, R, Ease.Step));
        }
        return new MascotPart(id, 0f, 0f, keys);
    }

    // A part hidden at rest that shows (opacity 1) from each <on> for <length> seconds.
    static MascotPart Flash(string id, float length, params float[] ons)
    {
        var keys = new List<MascotKey> { K(0f, Hidden) };
        foreach (float at in ons)
        {
            keys.Add(K(at, R, Ease.Step));
            keys.Add(K(at + length, Hidden, Ease.Step));
        }
        return new MascotPart(id, 0f, 0f, keys);
    }

    static readonly Dictionary<string, MascotLoop> Loops = new(StringComparer.Ordinal)
    {
        ["claude-code.waving"] = Waving(),
        ["claude-code.party"] = Party(),
        ["claude-code.peek"] = Peek(),
        ["codex.buddy"] = CodexBuddy(),
        ["codex.spin"] = Spin(),
        ["codex.sleepy"] = Sleepy(),
        ["opencode.buddy"] = OpenCodeBuddy(),
        ["opencode.brackets"] = Brackets(),
        ["opencode.stack"] = Stack(),
        ["pi.walker"] = Walker(),
        ["pi.shades"] = Shades(),
        ["pi.pie"] = Pie(),
    };

    // Claude Code waving: the hand flaps out and in three times in pixel pose swaps (a whole pixel
    // either side, each held, as pixel art moves; a smooth turn would blur its hard edges), and the
    // motion ticks flick on each swing in.
    static MascotLoop Waving()
    {
        var keys = new List<MascotKey> { K(0f, R) };
        foreach (float at in new[] { 0.8f, 1.3f, 1.8f })
        {
            keys.Add(K(at, P(dx: -1.5f), Ease.Step));
            keys.Add(K(at + 0.25f, P(dx: 1.5f), Ease.Step));
        }
        keys.Add(K(2.3f, R, Ease.Step));
        return new(4f, 15, new[]
        {
            new MascotPart("hand", 0f, 0f, keys),
            Part("ticks", 0f, 0f,
                K(0f, R), K(0.8f, Hidden, Ease.Step), K(1.05f, R, Ease.Step), K(1.3f, Hidden, Ease.Step),
                K(1.55f, R, Ease.Step), K(1.8f, Hidden, Ease.Step), K(2.05f, R, Ease.Step)),
        });
    }

    // Claude Code party: the hat wiggles on its base while the confetti twinkles and two more bits pop
    // and drift down.
    static MascotLoop Party() => new(4f, 12, new[]
    {
        Part("hat", 7.5f, 5f, K(0f, R), K(0.9f, R), K(1.1f, P(turn: -12f)), K(1.35f, P(turn: 9f)), K(1.6f, P(turn: -5f)), K(1.85f, R)),
        Blink("confetti1", 0.25f, 0.9f, 1.75f),
        Blink("confetti2", 0.25f, 1.3f, 2.15f),
        Part("confetti3", 0f, 0f, K(0f, Hidden), K(1.0f, Hidden), K(1.05f, R, Ease.Step), K(1.75f, P(dy: 3f)), K(1.8f, Hidden, Ease.Step)),
        Part("confetti4", 0f, 0f, K(0f, Hidden), K(1.45f, Hidden), K(1.5f, R, Ease.Step), K(2.2f, P(dy: 3f)), K(2.25f, Hidden, Ease.Step)),
    });

    // Claude Code peekaboo: a blink; then it sinks behind the ledge until only its eyes peek over, looks
    // left, and pops back up.
    static MascotLoop Peek() => new(5f, 15, new[]
    {
        Flash("lids", 0.15f, 0.8f),
        Part("critter", 0f, 0f, K(0f, R), K(2.0f, R), K(2.3f, P(dy: 6f)), K(3.1f, P(dy: 6f)), K(3.3f, P(dy: -0.75f)), K(3.45f, R)),
        Part("pupils", 0f, 0f, K(0f, R), K(2.45f, R), K(2.5f, P(dx: -3f), Ease.Step), K(2.85f, P(dx: -3f)), K(2.9f, R, Ease.Step)),
    });

    // Codex buddy: a double blink (closed eyes drawn over the open ones, so the white eyes never
    // uncover the dark face behind them), then the googly pupils roll round.
    static MascotLoop CodexBuddy() => new(4.5f, 20, new[]
    {
        Flash("lids", 0.1f, 0.95f, 1.25f),
        Part("pupils", 0f, 0f, K(0f, R), K(2.3f, R), K(2.5f, P(dx: -22f, dy: -14f)), K(2.75f, P(dx: 6f, dy: -26f)),
            K(3.0f, P(dx: 14f, dy: -8f)), K(3.3f, R)),
    });

    // Codex spin: the knot twirls once round under its face, the speed arcs whoosh after it, and the
    // face wobbles as it stops.
    static MascotLoop Spin() => new(4.5f, 20, new[]
    {
        Part("knot", 256.5f, 256f, K(0f, R), K(0.9f, R), K(1.9f, P(turn: 360f))),
        Part("arcs", 256f, 256f, K(0f, R), K(0.9f, R), K(1.4f, P(turn: 30f)), K(1.9f, R)),
        Part("face", 256.5f, 300f, K(0f, R), K(1.75f, R), K(1.95f, P(turn: -9f)), K(2.15f, P(turn: 6f)), K(2.35f, P(turn: -3f)), K(2.5f, R)),
    });

    // Codex sleepy: z's drift up and away, one after the other: the far z fades out, the near one takes
    // its place and a new one rises from the knot into the near place.
    static MascotLoop Sleepy() => new(4f, 15, new[]
    {
        Part("zFar", -42f, -42f, K(0f, R), K(2.4f, R), K(3.4f, P(dx: -18f, dy: -18f, sx: 0.85f, sy: 0.85f, o: 0f), Ease.Linear)),
        Part("zNear", 46f, 52f, K(0f, R), K(2.4f, R), K(4f, P(dx: -88f, dy: -94f, sx: 0.7f, sy: 0.7f), Ease.Linear)),
        Part("zNew", 46f, 52f, K(0f, P(dx: 50f, dy: 56f, sx: 0.5f, sy: 0.5f, o: 0f)), K(2.4f, P(dx: 50f, dy: 56f, sx: 0.5f, sy: 0.5f, o: 0f)),
            K(4f, R, Ease.Linear)),
    });

    // OpenCode buddy: the block-cursor eyes blink twice, quick, like a caret, then a foot taps twice.
    static MascotLoop OpenCodeBuddy() => new(4f, 12, new[]
    {
        Blink("eyes", 0.17f, 0.9f, 1.15f),
        Part("footR", 0f, 0f, K(0f, R), K(2.6f, R), K(2.67f, P(dy: -6f), Ease.Step), K(2.83f, R, Ease.Step),
            K(3.0f, P(dy: -6f), Ease.Step), K(3.17f, R, Ease.Step)),
    });

    // OpenCode brackets: the < and > flex out and back like arms, twice, and the eyes squeeze.
    static MascotLoop Brackets() => new(3.6f, 15, new[]
    {
        Part("lt", -58f, 150f, K(0f, R), K(0.9f, R), K(1.1f, P(dx: -9f)), K(1.3f, R), K(1.5f, P(dx: -9f)), K(1.75f, R)),
        Part("gt", 298f, 150f, K(0f, R), K(0.9f, R), K(1.1f, P(dx: 9f)), K(1.3f, R), K(1.5f, P(dx: 9f)), K(1.75f, R)),
        Part("eyes", 120f, 180f, K(0f, R), K(0.9f, R), K(1.1f, P(sy: 0.45f)), K(1.6f, P(sy: 0.45f)), K(1.75f, R)),
    });

    // OpenCode stack: the front window hops off the one behind and lands with a little squash, then
    // blinks its caret eyes.
    static MascotLoop Stack() => new(4f, 15, new[]
    {
        Part("front", 120f, 314f, K(0f, R), K(0.9f, R), K(1.0f, P(sx: 1.04f, sy: 0.94f)), K(1.2f, P(dy: -26f)),
            K(1.4f, R), K(1.47f, P(sx: 1.05f, sy: 0.93f)), K(1.6f, R)),
        Blink("eyes", 0.3f, 2.2f),
    });

    // Pi walker: four marching steps, each leg lifting two pixels in turn and the bar dipping a pixel as
    // each foot lands (one pixel is 5 units), big enough to read at token size.
    static MascotLoop Walker()
    {
        var up = P(dy: -10f);
        var dip = P(dy: 5f);
        MascotPart Steps(string id, params float[] lifts)
        {
            var keys = new List<MascotKey> { K(0f, R) };
            foreach (float at in lifts) { keys.Add(K(at, up, Ease.Step)); keys.Add(K(at + 0.25f, R, Ease.Step)); }
            return new MascotPart(id, 0f, 0f, keys);
        }
        var bar = new List<MascotKey> { K(0f, R) };
        foreach (float land in new[] { 1.125f, 1.5f, 1.875f, 2.25f }) { bar.Add(K(land, dip, Ease.Step)); bar.Add(K(land + 0.125f, R, Ease.Step)); }
        return new(3.6f, 8, new[]
        {
            Steps("legL", 0.875f, 1.625f),
            Steps("legR", 1.25f, 2.0f),
            new MascotPart("bar", 0f, 0f, bar),
        });
    }

    // Pi shades: a glint sweeps across both lenses and a sparkle pops at the frame's corner.
    static MascotLoop Shades() => new(4f, 10, new[]
    {
        Part("glint", 0f, 0f, K(0f, R), K(1.0f, R), K(1.1f, P(dx: 5f), Ease.Step), K(1.2f, P(dx: 10f), Ease.Step),
            K(1.3f, P(dx: 14f), Ease.Step), K(1.4f, P(dx: 14f, o: 0f), Ease.Step), K(1.7f, R, Ease.Step)),
        Part("sparkle", 0f, 0f, K(0f, Hidden), K(1.4f, Hidden), K(1.5f, P(sx: 0.6f, sy: 0.6f), Ease.Step),
            K(1.6f, R, Ease.Step), K(1.8f, P(sx: 0.6f, sy: 0.6f), Ease.Step), K(1.9f, Hidden, Ease.Step)),
    });

    // Pi pie: steam rises off the filling in pixel steps, a fresh wisp coming up under each one.
    static MascotLoop Pie()
    {
        PartPose Up(float dy, float o = 1f) => P(dy: dy, o: o);
        MascotPart Rise(string id, float start) => Part(id, 0f, 0f, K(0f, R), K(start, R),
            K(start + 0.25f, Up(-4f), Ease.Step), K(start + 0.5f, Up(-8f, 0.6f), Ease.Step), K(start + 0.75f, Up(-12f, 0f), Ease.Step));
        MascotPart Under(string id, float start) => Part(id, 0f, 0f, K(0f, Up(12f, 0f)), K(start, Up(12f, 0f)),
            K(start + 0.25f, Up(8f, 0.6f), Ease.Step), K(start + 0.5f, Up(4f), Ease.Step), K(start + 0.75f, R, Ease.Step));
        // Each pair ends as it started (the fresh wisp where the old one was), so the bursts end the loop.
        return new(4f, 8, new[]
        {
            Rise("steam1", 2.75f), Under("steam1b", 2.75f),
            Rise("steam2", 3.25f), Under("steam2b", 3.25f),
        });
    }
}
