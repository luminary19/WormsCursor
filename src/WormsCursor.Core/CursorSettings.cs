namespace WormsCursor.Core;

/// <summary>
/// Tunable parameters for the rotating-cursor engine. Defaults match the original
/// prototype. These are what the preferences UI will eventually edit.
/// </summary>
public sealed class CursorSettings
{
    /// <summary>Number of precomputed angles. 360 = one cursor per degree.</summary>
    public int Steps { get; set; } = 360;

    /// <summary>Mouse-position polling rate in Hz.</summary>
    public int Hz { get; set; } = 144;

    /// <summary>Pixels of accumulated travel before the direction is recomputed
    /// (less = snappier, more = steadier).</summary>
    public double AimDist { get; set; } = 8.0;

    /// <summary>Direction smoothing between re-aims, 0..1 (lower = smoother/lazier).</summary>
    public double AimSmooth { get; set; } = 0.50;

    /// <summary>Dead zone: don't move the target for direction changes below this
    /// many degrees.</summary>
    public double HystDeg { get; set; } = 3.0;

    /// <summary>Frames of full stillness before accumulated travel is forgotten
    /// (~0.4s @144Hz).</summary>
    public int IdleReset { get; set; } = 60;

    /// <summary>Rotation animation speed in degrees/second. 0 = instant (no animation).</summary>
    public double TurnDps { get; set; } = 720;

    /// <summary>Side length of the square cursor bitmap, in pixels.</summary>
    public int Canvas { get; set; } = 64;

    /// <summary>When true, the engine emits the current angle via Debug.WriteLine.</summary>
    public bool Debug { get; set; } = false;
}
