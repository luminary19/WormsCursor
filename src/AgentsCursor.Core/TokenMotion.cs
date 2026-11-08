namespace AgentsCursor.Core;

/// <summary>
/// The token's self-driven motion, as pure functions of time, so the overlay and the control panel's
/// preview bounce the same way. (The cursor pendulum itself also depends on where the pointer goes, so
/// only its idle bob lives here.)
/// </summary>
public static class TokenMotion
{
    /// <summary>Offset (px) and tilt (degrees) of the token at time <paramref name="t"/> seconds.</summary>
    public readonly record struct Pose(float OffsetX, float OffsetY, float SwingDeg);

    /// <summary>The corner token's continuous "ball" bounce: |sin| hops it up from its anchor and back
    /// down, with a small sway and tilt so it reads as alive without the cursor driving it.</summary>
    public static Pose CornerBounce(float t, float tokenPx) => new(
        tokenPx * 0.04f * MathF.Sin(t * 1.6f),
        -tokenPx * CornerHop * MathF.Abs(MathF.Sin(t * 3.0f)),
        3f * MathF.Sin(t * 1.6f));

    /// <summary>How far (px, negative = up) the hanging token bobs at rest, so it looks alive while the
    /// mouse is still.</summary>
    public static float IdleBob(float t, float tokenPx) => -tokenPx * IdleBobHeight * MathF.Abs(MathF.Sin(t * 3.2f));

    /// <summary>How far above its anchor the corner bounce can lift the token, as a fraction of its size.</summary>
    public const float CornerHop = 0.22f;

    /// <summary>How far the idle bob lifts the hanging token, as a fraction of its size.</summary>
    public const float IdleBobHeight = 0.05f;
}
