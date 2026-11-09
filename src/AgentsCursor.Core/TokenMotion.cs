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

/// <summary>How lively the token moves (<see cref="CursorSettings.Motion"/>).</summary>
public enum MotionStyle
{
    /// <summary>A springy string: it swings as the pointer moves, hops in, bobs and bounces.</summary>
    Bouncy,
    /// <summary>A softer spring with a smaller swing, hop and bob.</summary>
    Calm,
    /// <summary>No swing, bounce, bob or entrance hop: the token simply follows the pointer, or sits in
    /// its corner. What every style becomes while Windows' animation effects are off.</summary>
    Still,
}

/// <summary>
/// The numbers behind a <see cref="MotionStyle"/>. The spring pulls the token toward its rest point
/// (<see cref="Stiffness"/>, <see cref="Damping"/>, per second); an infinite stiffness is a rigid rod,
/// so the token sits exactly at its rest point. <see cref="SwingScale"/> scales the largest tilt a
/// placement allows (<see cref="TokenMotion.MaxSwingFor"/>) and the corner's sway; the entrance starts
/// the token back along its string at <see cref="EntranceSpeed"/> token sizes per second; the idle bob
/// and the corner hop rise <see cref="BobHeight"/> and <see cref="HopHeight"/> token sizes, at
/// <see cref="Tempo"/> times Bouncy's pace.
/// </summary>
public sealed record MotionProfile(float Stiffness, float Damping, float SwingScale, float EntranceSpeed,
                                   float BobHeight, float HopHeight, float Tempo)
{
    /// <summary>The original motion: the old cursor pendulum's spring (lightly damped), a lively swing,
    /// a hop in, a bob at rest and a ball bounce in a corner.</summary>
    public static readonly MotionProfile Bouncy = new(130f, 3f, 1f, 3f, 0.05f, 0.22f, 1f);

    /// <summary>A slower spring that barely overshoots, with half the swing, hop and bob, at a slower pace.</summary>
    public static readonly MotionProfile Calm = new(60f, 9f, 0.5f, 1.5f, 0.025f, 0.10f, 0.7f);

    /// <summary>Rigid and motionless: the token keeps its rest point and stays upright.</summary>
    public static readonly MotionProfile Still = new(float.PositiveInfinity, 0f, 0f, 0f, 0f, 0f, 0f);

    /// <summary>True when the token is held exactly at its rest point (no spring).</summary>
    public bool IsRigid => float.IsPositiveInfinity(Stiffness);

    /// <summary>The style that applies: <paramref name="style"/>, or <see cref="MotionStyle.Still"/>
    /// while Windows' animation effects are off.</summary>
    public static MotionStyle Effective(MotionStyle style, bool animationEffects)
        => animationEffects ? style : MotionStyle.Still;

    /// <summary>The profile for <paramref name="style"/> as it applies (see <see cref="Effective"/>).</summary>
    public static MotionProfile For(MotionStyle style, bool animationEffects) => Effective(style, animationEffects) switch
    {
        MotionStyle.Calm => Calm,
        MotionStyle.Still => Still,
        _ => Bouncy,
    };

    /// <summary>The largest tilt the token takes, either way, in <paramref name="placement"/>.</summary>
    public float MaxSwingFor(NotifierPlacement placement) => TokenMotion.MaxSwingFor(placement) * SwingScale;
}

/// <summary>
/// The box a token takes up around its centre, in px, for a token of <see cref="TokenPx"/> px with an
/// optional <see cref="TokenLabel"/> of <see cref="Label"/> px (as <see cref="NotifierRenderer.LabelSize"/>
/// measures it; empty without one). The icon with its status cloud reaches <see cref="Half"/> on every
/// side; the label hangs below it, centred, from <see cref="TokenLabel.Top"/>. It is what the pointer
/// placements rest beside the pointer, what the screen's edges keep on screen, where a corner token
/// sits, and what the canvas makes room for, so a label never clips or leaves the screen.
/// </summary>
public readonly record struct TokenFootprint(float TokenPx, SizeF Label = default)
{
    /// <summary>The icon and its status cloud with the cloud's shadow, either way from the centre.</summary>
    public float Half => TokenPx * (0.5f + StatusBadge.RadiusRatio * 1.25f);

    /// <summary>The reach left of the centre (and, the label being centred, right of it).</summary>
    public float Left => HasLabel ? Math.Max(Half, Label.Width / 2f + LabelSnap) : Half;

    /// <summary>The reach right of the centre.</summary>
    public float Right => Left;

    /// <summary>The reach above the centre (the label is below).</summary>
    public float Top => Half;

    /// <summary>The reach below the centre.</summary>
    public float Bottom => Math.Max(Half, Below);

    /// <summary>How far what is drawn hangs below the centre: the icon's box, or the label's bottom.
    /// What a placement above the pointer keeps clear of its tip.</summary>
    public float Below => HasLabel ? TokenLabel.Top(TokenPx) + Label.Height + LabelSnap : TokenPx / 2f;

    bool HasLabel => Label.Width > 0f && Label.Height > 0f;

    // The label is drawn at whole pixels, so up to half a pixel off its exact place.
    const float LabelSnap = 0.5f;
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
/// How springy it is, how far it swings and how high it hops and bobs is the <see cref="MotionProfile"/>.
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

    /// <summary>How far past its rest distance the string lets the token stretch, as a fraction of its size.</summary>
    public const float Slack = 0.30f;

    /// <summary>The largest tilt a Bouncy token beside the pointer takes, either way (a tooltip shouldn't spin).</summary>
    public const float BesideMaxSwing = 35f;

    /// <summary>The largest tilt a Bouncy token hanging below or floating above the pointer takes, either way:
    /// a lively swing, but never upside down (the "?" stays upright; the logo would not).</summary>
    public const float HangingMaxSwing = 80f;

    /// <summary>The arrow pointer's reach below its hotspot, plus a small gap, at 96 DPI: the standard
    /// Windows arrow is about 12 x 19 px, and the real pointer is always drawn on top of the token.</summary>
    public const float PointerClearance = 22f;

    /// <summary>The most the corner bounce of any style lifts the token above its anchor, as a fraction
    /// of its size: the room a corner leaves for it.</summary>
    public static float CornerHop => MotionProfile.Bouncy.HopHeight;

    /// <summary>The largest tilt a Bouncy token takes, either way, in <paramref name="placement"/>
    /// (<see cref="MotionProfile.MaxSwingFor"/> scales it per style).</summary>
    public static float MaxSwingFor(NotifierPlacement placement)
        => placement == NotifierPlacement.BesidePointer ? BesideMaxSwing : HangingMaxSwing;

    /// <summary>The rest offset for <paramref name="placement"/> with <paramref name="token"/> on a
    /// monitor at <paramref name="dpi"/> (1 = 96 DPI), mirrored per <paramref name="flip"/>. Below hangs
    /// under the pointer's arrow with its top, badges included, clear of it; above floats just over the
    /// pointer's tip, its label too; beside trails off the arrow's bottom-right like a tooltip. Below and
    /// above are each other's mirror: flipped, each takes the other's place, so a flip never puts the
    /// token under the arrow; beside flipped up puts the bottom of what it draws where its top was.
    /// (Corner has no pointer rest; it gets the below offset.)</summary>
    public static Rest RestFor(NotifierPlacement placement, TokenFootprint token, float dpi, Flip flip)
    {
        bool flipY = flip.HasFlag(Flip.Y);
        float tokenPx = token.TokenPx;
        float below = PointerClearance * dpi + tokenPx * (0.5f + StatusBadge.Overhang);
        float above = -(token.Below + 0.12f * tokenPx);
        float besideTop = 18f * dpi - 0.20f * tokenPx; // the token's top edge, below the pointer's tip
        var (dx, dy) = placement switch
        {
            NotifierPlacement.AbovePointer => (0f, flipY ? below : above),
            NotifierPlacement.BesidePointer => (14f * dpi + 0.50f * tokenPx, flipY ? -(besideTop + token.Below) : besideTop + tokenPx / 2f),
            _ => (0f, flipY ? above : below),
        };
        if (flip.HasFlag(Flip.X)) dx = -dx;
        return new Rest(dx, dy);
    }

    /// <summary>Advances the tether by <paramref name="dt"/> seconds toward the pointer at
    /// (<paramref name="ax"/>, <paramref name="ay"/>) plus <paramref name="rest"/>, moving the way
    /// <paramref name="motion"/> says, and returns the pose to draw. The first call seeds the token at
    /// rest; with <paramref name="entrance"/> it starts with a hop back along the string, which springs
    /// out again. A rigid profile (<see cref="MotionProfile.Still"/>) keeps the token at its rest point,
    /// upright. <paramref name="maxSwing"/> limits the tilt either way
    /// (<see cref="MotionProfile.MaxSwingFor"/> the placement). With <paramref name="workArea"/>
    /// the screen's edges are a wall: the token (all of <paramref name="token"/>, badge and label) stops
    /// at them and loses its speed into them, so an overshoot never carries it off screen. Use the same
    /// area as <see cref="KeepOnScreen"/>, so the rest point is always inside the wall.</summary>
    public static Pose Step(ref TetherState s, float ax, float ay, Rest rest, TokenFootprint token, float dt,
                            bool entrance, MotionProfile motion, float maxSwing = 180f, RectangleF? workArea = null)
    {
        float tokenPx = token.TokenPx;
        float restLength = Math.Max(1e-3f, rest.Length);
        float ux = rest.Dx / restLength, uy = rest.Dy / restLength; // the string's rest direction
        if (!s.Seeded)
        {
            s = new TetherState { X = ax + rest.Dx, Y = ay + rest.Dy, Seeded = true };
            if (entrance) { s.Vx = -ux * motion.EntranceSpeed * tokenPx; s.Vy = -uy * motion.EntranceSpeed * tokenPx; }
        }
        s.T += dt;

        if (motion.IsRigid)
        {
            // No spring: the token keeps its rest point (inside the work area, by KeepOnScreen), upright.
            s.X = ax + rest.Dx;
            s.Y = ay + rest.Dy;
            s.Vx = s.Vy = 0f;
            if (workArea is { } area) StopAtEdges(ref s, area, token);
            return new Pose(s.X - ax, s.Y - ay, 0f);
        }

        // A spring toward the rest point; explicit Euler, as the old pendulum (dt is capped by the host).
        s.Vx += ((ax + rest.Dx - s.X) * motion.Stiffness - s.Vx * motion.Damping) * dt;
        s.Vy += ((ay + rest.Dy - s.Y) * motion.Stiffness - s.Vy * motion.Damping) * dt;
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

        if (workArea is { } wall) StopAtEdges(ref s, wall, token);
        dx = s.X - ax;
        dy = s.Y - ay;

        // Tilt = how far the string has swung from its rest direction, so the token is upright at rest.
        float swing = (MathF.Atan2(dy, dx) - MathF.Atan2(uy, ux)) * 180f / MathF.PI;
        if (swing > 180f) swing -= 360f;
        else if (swing < -180f) swing += 360f;
        swing = Math.Clamp(swing, -maxSwing, maxSwing);

        float bob = IdleBob(s.T, tokenPx, motion); // negative: back along the string
        return new Pose(dx + ux * bob, dy + uy * bob, swing);
    }

    // Keeps the whole token inside <wall>; speed into an edge it has reached is dropped.
    static void StopAtEdges(ref TetherState s, RectangleF wall, TokenFootprint token)
    {
        float left = wall.Left + token.Left, right = wall.Right - token.Right;
        float top = wall.Top + token.Top, bottom = wall.Bottom - token.Bottom;
        if (left > right) { left = right = (wall.Left + wall.Right) / 2f; }
        if (top > bottom) { top = bottom = (wall.Top + wall.Bottom) / 2f; }
        if (s.X < left) { s.X = left; s.Vx = Math.Max(0f, s.Vx); }
        else if (s.X > right) { s.X = right; s.Vx = Math.Min(0f, s.Vx); }
        if (s.Y < top) { s.Y = top; s.Vy = Math.Max(0f, s.Vy); }
        else if (s.Y > bottom) { s.Y = bottom; s.Vy = Math.Min(0f, s.Vy); }
    }

    /// <summary>The flips that keep <paramref name="token"/> resting at <paramref name="anchor"/> +
    /// <paramref name="rest"/> (unflipped) inside <paramref name="workArea"/>, given the flips used so
    /// far. An axis flips as soon as the token would leave the work area on its rest side, and flips back
    /// only once there is room for it plus a quarter of its size, so a pointer parked on the threshold
    /// doesn't make it flip-flop.</summary>
    public static Flip Edges(Flip previous, PointF anchor, Rest rest, TokenFootprint token, RectangleF workArea)
    {
        float room = 0.25f * token.TokenPx;
        var flip = previous;
        flip = Axis(flip, Flip.X, anchor.X, rest.Dx, token.Left, token.Right, room, workArea.Left, workArea.Right);
        flip = Axis(flip, Flip.Y, anchor.Y, rest.Dy, token.Top, token.Bottom, room, workArea.Top, workArea.Bottom);
        return flip;
    }

    // One axis of Edges: on its rest side, the token (reaching `back` before its centre and `on` past
    // it) must fit between min and max.
    static Flip Axis(Flip flip, Flip axis, float anchor, float offset, float back, float on, float room, float min, float max)
    {
        if (offset == 0f) return flip & ~axis; // straight above or below: nothing to mirror sideways
        bool flipped = flip.HasFlag(axis);
        float centre = anchor + offset;
        bool fits = offset > 0f ? centre + on <= max : centre - back >= min;
        bool roomy = offset > 0f ? centre + on + room <= max : centre - back - room >= min;
        if (!flipped && !fits) return flip | axis;
        if (flipped && roomy) return flip & ~axis;
        return flip;
    }

    /// <summary><paramref name="rest"/> (already flipped by <see cref="Edges"/>), shifted just enough that a
    /// token resting there stays inside <paramref name="workArea"/>. Mirroring can't help an axis the rest
    /// doesn't offset (below the pointer at the right edge, say), and near a corner even a mirrored rest
    /// can overhang; this slides the rest point along the edge instead, like a tooltip. The token is still
    /// upright at rest, because <see cref="Step"/> measures its tilt from this rest direction.</summary>
    public static Rest KeepOnScreen(Rest rest, PointF anchor, TokenFootprint token, RectangleF workArea)
    {
        float x = Clamp(anchor.X + rest.Dx, workArea.Left + token.Left, workArea.Right - token.Right);
        float y = Clamp(anchor.Y + rest.Dy, workArea.Top + token.Top, workArea.Bottom - token.Bottom);
        return new Rest(x - anchor.X, y - anchor.Y);
    }

    /// <summary>Where the centre of <paramref name="token"/> pinned to <paramref name="corner"/> of
    /// <paramref name="area"/> sits: all of it <paramref name="margin"/> px inside the area's edges, with
    /// room above it for the corner bounce's hop.</summary>
    public static PointF CornerAnchor(ScreenCorner corner, RectangleF area, TokenFootprint token, float margin)
    {
        float x = corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft
            ? area.Left + margin + token.Left : area.Right - margin - token.Right;
        float y = corner is ScreenCorner.TopLeft or ScreenCorner.TopRight
            ? area.Top + margin + token.Top + CornerHop * token.TokenPx : area.Bottom - margin - token.Bottom;
        return new PointF(x, y);
    }

    // Math.Clamp, except that a work area narrower than the token centres it instead of throwing.
    static float Clamp(float value, float min, float max) => min > max ? (min + max) / 2f : Math.Clamp(value, min, max);

    /// <summary>The corner token's continuous "ball" bounce at time <paramref name="t"/> seconds: |sin|
    /// hops it up from its anchor and back down, with a small sway and tilt so it reads as alive without
    /// the cursor driving it. Still sits at its anchor, upright.</summary>
    public static Pose CornerBounce(float t, float tokenPx, MotionProfile motion)
    {
        float pace = t * motion.Tempo;
        return new(
            tokenPx * 0.04f * motion.SwingScale * MathF.Sin(pace * 1.6f),
            -tokenPx * motion.HopHeight * MathF.Abs(MathF.Sin(pace * 3.0f)),
            3f * motion.SwingScale * MathF.Sin(pace * 1.6f));
    }

    /// <summary>How far (px, negative = back toward the pointer) the token bobs along its string at
    /// rest, so it looks alive while the mouse is still. Still doesn't bob.</summary>
    public static float IdleBob(float t, float tokenPx, MotionProfile motion)
        => -tokenPx * motion.BobHeight * MathF.Abs(MathF.Sin(t * motion.Tempo * 3.2f));
}

/// <summary>
/// The square canvas the token is drawn on, centred on the token's centre: big enough for the icon at
/// any tilt, the status cloud with its shadow and the "+N" pill beside it, the contrast rim, and the
/// upright label below, so nothing is ever clipped whatever the placement or swing.
/// </summary>
public static class TokenCanvas
{
    /// <summary>Half the canvas side for <paramref name="token"/>: the box's corner at any rotation
    /// (half its diagonal), plus the badge and its shadow, plus the rim; or the label's reach, if
    /// further.</summary>
    public static float HalfExtent(TokenFootprint token)
    {
        float tokenPx = token.TokenPx;
        float swung = tokenPx * (0.5f * MathF.Sqrt(2f) + StatusBadge.RadiusRatio * 1.25f) + RimPad(tokenPx);
        return Math.Max(swung, Math.Max(token.Left, token.Bottom) + AntiAliasPad);
    }

    /// <summary>The canvas side, in whole px (even, so the token centre is a pixel corner).</summary>
    public static int Side(TokenFootprint token) => 2 * (int)MathF.Ceiling(HalfExtent(token));

    // The contrast rim at its widest (see NotifierRenderer.RimWidth) plus anti-aliasing.
    static float RimPad(float tokenPx) => tokenPx * NotifierRenderer.MaxRimRatio + AntiAliasPad;

    const float AntiAliasPad = 2f;
}
