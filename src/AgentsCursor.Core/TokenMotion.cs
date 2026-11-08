using System.Drawing;

namespace AgentsCursor.Core;

/// <summary>Which axes of a pointer placement's rest offset are mirrored to keep the token on screen.</summary>
[Flags]
public enum Flip
{
    None = 0,
    /// <summary>Left of the pointer instead of right (or the other way round).</summary>
    X = 1,
    /// <summary>Above the pointer instead of below (or the other way round).</summary>
    Y = 2,
}

/// <summary>Where a pointer-following token springs to, and how it is moving there. Owned by one
/// animation (the overlay's thread, or one preview), never shared.</summary>
public struct TetherState
{
    /// <summary>Token centre and velocity, in screen px.</summary>
    public float X, Y, Vx, Vy;
    /// <summary>Seconds since the tether was seeded (drives the idle bob).</summary>
    public float T;
    /// <summary>False until the first <see cref="TokenMotion.Step"/> places the token at rest.</summary>
    public bool Seeded;
}

/// <summary>
/// The token's motion, as pure functions shared by the overlay and the control panel's preview, so
/// both move the same way.
///
/// Every pointer placement (below, above, beside) is one model: the token is tied to the pointer by a
/// springy string whose rest point is a fixed offset from the pointer (<see cref="RestFor"/>). Moving
/// the pointer whips the anchor and the token lags, swings and settles back to rest, with a gentle bob
/// along the string. Near a screen edge the rest offset is mirrored per axis (<see cref="Edges"/>) and
/// then slid along the edge if the token would still overhang (<see cref="KeepOnScreen"/>), and the
/// spring carries the token over. The corner placement bounces in place (<see cref="CornerBounce"/>).
/// </summary>
public static class TokenMotion
{
    /// <summary>Offset (px, from the anchor) and tilt (degrees, 0 = upright) of the token.</summary>
    public readonly record struct Pose(float OffsetX, float OffsetY, float SwingDeg);

    /// <summary>The token centre's rest offset from the pointer, in px.</summary>
    public readonly record struct Rest(float Dx, float Dy)
    {
        public float Length => MathF.Sqrt(Dx * Dx + Dy * Dy);
    }

    /// <summary>Spring stiffness and damping (per second): the old cursor pendulum's, for every placement.</summary>
    public const float Stiffness = 130f, Damping = 3f;

    /// <summary>How far past its rest distance the string lets the token stretch, as a fraction of its size.</summary>
    public const float Slack = 0.30f;

    /// <summary>The largest tilt a token beside the pointer takes, either way (a tooltip shouldn't spin).</summary>
    public const float BesideMaxSwing = 35f;

    /// <summary>How far above its anchor the corner bounce can lift the token, as a fraction of its size.</summary>
    public const float CornerHop = 0.22f;

    /// <summary>How far the idle bob moves the token along its string, as a fraction of its size.</summary>
    public const float IdleBobHeight = 0.05f;

    /// <summary>The entrance hop: the token's starting speed back along its string, in token sizes per second.</summary>
    public const float EntranceSpeed = 3f;

    /// <summary>The rest offset for <paramref name="placement"/> with a token of <paramref name="tokenPx"/>
    /// px on a monitor at <paramref name="dpi"/> (1 = 96 DPI), mirrored per <paramref name="flip"/>.
    /// Below hangs just under the pointer; above floats clear of the arrow; beside trails off the arrow's
    /// bottom-right like a tooltip. (Corner has no pointer rest; it gets the below offset.)</summary>
    public static Rest RestFor(NotifierPlacement placement, float tokenPx, float dpi, Flip flip)
    {
        var (dx, dy) = placement switch
        {
            NotifierPlacement.AbovePointer => (0f, -0.62f * tokenPx),
            NotifierPlacement.BesidePointer => (14f * dpi + 0.50f * tokenPx, 18f * dpi + 0.30f * tokenPx),
            _ => (0f, 0.42f * tokenPx),
        };
        if (flip.HasFlag(Flip.X)) dx = -dx;
        if (flip.HasFlag(Flip.Y)) dy = -dy;
        return new Rest(dx, dy);
    }

    /// <summary>Advances the tether by <paramref name="dt"/> seconds toward the pointer at
    /// (<paramref name="ax"/>, <paramref name="ay"/>) plus <paramref name="rest"/>, and returns the pose to
    /// draw. The first call seeds the token at rest; with <paramref name="entrance"/> it starts with a hop
    /// back along the string, which springs out again. <paramref name="maxSwing"/> limits the tilt
    /// either way (<see cref="BesideMaxSwing"/> beside the pointer). With <paramref name="workArea"/>
    /// the screen's edges are a wall: the token (with its badge, <see cref="RestHalfExtent"/>) stops at
    /// them and loses its speed into them, so an overshoot never carries it off screen. Use the same
    /// area as <see cref="KeepOnScreen"/>, so the rest point is always inside the wall.</summary>
    public static Pose Step(ref TetherState s, float ax, float ay, Rest rest, float tokenPx, float dt,
                            bool entrance, float maxSwing = 180f, RectangleF? workArea = null)
    {
        float restLength = Math.Max(1e-3f, rest.Length);
        float ux = rest.Dx / restLength, uy = rest.Dy / restLength; // the string's rest direction
        if (!s.Seeded)
        {
            s = new TetherState { X = ax + rest.Dx, Y = ay + rest.Dy, Seeded = true };
            if (entrance) { s.Vx = -ux * EntranceSpeed * tokenPx; s.Vy = -uy * EntranceSpeed * tokenPx; }
        }
        s.T += dt;

        // A spring toward the rest point; explicit Euler, as the old pendulum (dt is capped by the host).
        s.Vx += ((ax + rest.Dx - s.X) * Stiffness - s.Vx * Damping) * dt;
        s.Vy += ((ay + rest.Dy - s.Y) * Stiffness - s.Vy * Damping) * dt;
        s.X += s.Vx * dt;
        s.Y += s.Vy * dt;

        // The taut string: never further than the rest distance plus the slack, and it stops the token
        // moving further out (it can still move back in).
        float dx = s.X - ax, dy = s.Y - ay;
        float len = MathF.Sqrt(dx * dx + dy * dy), maxLen = restLength + Slack * tokenPx;
        if (len > maxLen)
        {
            float nx = dx / len, ny = dy / len;
            s.X = ax + nx * maxLen;
            s.Y = ay + ny * maxLen;
            float outward = s.Vx * nx + s.Vy * ny;
            if (outward > 0f) { s.Vx -= outward * nx; s.Vy -= outward * ny; }
            dx = s.X - ax;
            dy = s.Y - ay;
        }

        if (workArea is { } wall) StopAtEdges(ref s, wall, RestHalfExtent(tokenPx));
        dx = s.X - ax;
        dy = s.Y - ay;

        // Tilt = how far the string has swung from its rest direction, so the token is upright at rest.
        float swing = (MathF.Atan2(dy, dx) - MathF.Atan2(uy, ux)) * 180f / MathF.PI;
        if (swing > 180f) swing -= 360f;
        else if (swing < -180f) swing += 360f;
        swing = Math.Clamp(swing, -maxSwing, maxSwing);

        float bob = IdleBob(s.T, tokenPx); // negative: back along the string
        return new Pose(dx + ux * bob, dy + uy * bob, swing);
    }

    // Keeps the token centre at least <half> inside <wall>; speed into an edge it has reached is dropped.
    static void StopAtEdges(ref TetherState s, RectangleF wall, float half)
    {
        float left = wall.Left + half, right = wall.Right - half, top = wall.Top + half, bottom = wall.Bottom - half;
        if (left > right) { left = right = (wall.Left + wall.Right) / 2f; }
        if (top > bottom) { top = bottom = (wall.Top + wall.Bottom) / 2f; }
        if (s.X < left) { s.X = left; s.Vx = Math.Max(0f, s.Vx); }
        else if (s.X > right) { s.X = right; s.Vx = Math.Min(0f, s.Vx); }
        if (s.Y < top) { s.Y = top; s.Vy = Math.Max(0f, s.Vy); }
        else if (s.Y > bottom) { s.Y = bottom; s.Vy = Math.Min(0f, s.Vy); }
    }

    /// <summary>The flips that keep a token resting at <paramref name="anchor"/> + <paramref name="rest"/>
    /// (unflipped) inside <paramref name="workArea"/>, given the flips used so far. An axis flips as soon
    /// as the token would leave the work area on its rest side, and flips back only once there is room
    /// for it plus a quarter of its size, so a pointer parked on the threshold doesn't make it
    /// flip-flop.</summary>
    public static Flip Edges(Flip previous, PointF anchor, Rest rest, float tokenPx, RectangleF workArea)
    {
        float half = RestHalfExtent(tokenPx), room = 0.25f * tokenPx;
        var flip = previous;
        flip = Axis(flip, Flip.X, anchor.X, rest.Dx, half, room, workArea.Left, workArea.Right);
        flip = Axis(flip, Flip.Y, anchor.Y, rest.Dy, half, room, workArea.Top, workArea.Bottom);
        return flip;
    }

    // One axis of Edges: on its rest side, the token (centre +/- half) must fit between min and max.
    static Flip Axis(Flip flip, Flip axis, float anchor, float offset, float half, float room, float min, float max)
    {
        if (offset == 0f) return flip & ~axis; // straight above or below: nothing to mirror sideways
        bool flipped = flip.HasFlag(axis);
        float centre = anchor + offset;
        bool fits = offset > 0f ? centre + half <= max : centre - half >= min;
        bool roomy = offset > 0f ? centre + half + room <= max : centre - half - room >= min;
        if (!flipped && !fits) return flip | axis;
        if (flipped && roomy) return flip & ~axis;
        return flip;
    }

    /// <summary><paramref name="rest"/> (already flipped by <see cref="Edges"/>), shifted just enough that a
    /// token resting there stays inside <paramref name="workArea"/>. Mirroring can't help an axis the rest
    /// doesn't offset (below the pointer at the right edge, say), and near a corner even a mirrored rest
    /// can overhang; this slides the rest point along the edge instead, like a tooltip. The token is still
    /// upright at rest, because <see cref="Step"/> measures its tilt from this rest direction.</summary>
    public static Rest KeepOnScreen(Rest rest, PointF anchor, float tokenPx, RectangleF workArea)
    {
        float half = RestHalfExtent(tokenPx);
        float x = Clamp(anchor.X + rest.Dx, workArea.Left + half, workArea.Right - half);
        float y = Clamp(anchor.Y + rest.Dy, workArea.Top + half, workArea.Bottom - half);
        return new Rest(x - anchor.X, y - anchor.Y);
    }

    // Math.Clamp, except that a work area narrower than the token centres it instead of throwing.
    static float Clamp(float value, float min, float max) => min > max ? (min + max) / 2f : Math.Clamp(value, min, max);

    /// <summary>Half the side of the box a resting token needs: the icon plus the "?" badge and its
    /// shadow (the same reach <see cref="TokenCanvas"/> allows for).</summary>
    public static float RestHalfExtent(float tokenPx) => tokenPx * (0.5f + AttentionBadge.RadiusRatio * 1.25f);

    /// <summary>The corner token's continuous "ball" bounce at time <paramref name="t"/> seconds: |sin|
    /// hops it up from its anchor and back down, with a small sway and tilt so it reads as alive without
    /// the cursor driving it.</summary>
    public static Pose CornerBounce(float t, float tokenPx) => new(
        tokenPx * 0.04f * MathF.Sin(t * 1.6f),
        -tokenPx * CornerHop * MathF.Abs(MathF.Sin(t * 3.0f)),
        3f * MathF.Sin(t * 1.6f));

    /// <summary>How far (px, negative = back toward the pointer) the token bobs along its string at
    /// rest, so it looks alive while the mouse is still.</summary>
    public static float IdleBob(float t, float tokenPx) => -tokenPx * IdleBobHeight * MathF.Abs(MathF.Sin(t * 3.2f));
}

/// <summary>
/// The square canvas the token is drawn on, centred on the token's centre: big enough for the icon at
/// any tilt, the "?" badge with its shadow and the "+N" pill beside it, and the contrast rim, so
/// nothing is ever clipped whatever the placement or swing.
/// </summary>
public static class TokenCanvas
{
    /// <summary>Half the canvas side for a token of <paramref name="tokenPx"/> px: the box's corner at
    /// any rotation (half its diagonal), plus the badge and its shadow, plus the rim.</summary>
    public static float HalfExtent(float tokenPx)
        => tokenPx * (0.5f * MathF.Sqrt(2f) + AttentionBadge.RadiusRatio * 1.25f) + RimPad(tokenPx);

    /// <summary>The canvas side, in whole px (even, so the token centre is a pixel corner).</summary>
    public static int Side(float tokenPx) => 2 * (int)MathF.Ceiling(HalfExtent(tokenPx));

    // The contrast rim (6% of the side, see NotifierRenderer) plus anti-aliasing.
    static float RimPad(float tokenPx) => tokenPx * 0.06f + 2f;
}
