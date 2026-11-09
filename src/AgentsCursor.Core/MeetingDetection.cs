namespace AgentsCursor.Core;

/// <summary>What makes AgentsCursor think you're in a meeting, strongest first (the order
/// <see cref="MeetingDetection.Detect"/> reports them in).</summary>
public enum MeetingSignal
{
    /// <summary>Windows' presentation mode is on (<c>SHQueryUserNotificationState</c> says
    /// <c>QUNS_PRESENTATION_MODE</c>).</summary>
    Presenting,
    /// <summary>The displays are duplicated (Win+P "Duplicate", typical for a projector).</summary>
    DisplayDuplicated,
    /// <summary>An app is capturing the screen or a window through Windows' capture API (a meeting app's
    /// "Share screen", OBS window capture, a browser's tab or screen share).</summary>
    ScreenShared,
    /// <summary>An app is using the camera.</summary>
    Camera,
    /// <summary>An app is using the microphone.</summary>
    Microphone,
}

/// <summary>Why the token thinks you're in a meeting: the signal, and the app behind it when there is
/// one (a full exe path, or a packaged app's family name).</summary>
public sealed record Meeting(MeetingSignal Signal, string? App);

/// <summary>An app holding a device right now (its consent-store entry has a start time and no stop
/// time): which signal, which app (exe path or package family name), since when, and since when that
/// app has been running (null when no copy of it runs).</summary>
public readonly record struct DeviceUse(MeetingSignal Signal, string App, DateTime StartUtc, DateTime? RunningSinceUtc);

/// <summary>
/// The pure half of "hide the token during meetings and screen sharing": deciding from what Windows
/// reports whether you're presenting, sharing your screen or in a call. No Win32 here, so it is
/// unit-tested directly; <see cref="MeetingSignals"/> reads the live signals.
///
/// Windows records every app's use of the microphone, camera and screen capture (the privacy
/// indicators' "recently used" list) with a start time and, once it lets go, a stop time. An entry with
/// no stop time is in use, unless the app that wrote it is gone (killed mid-call, or the PC shut down
/// during one): such an entry stays "in use" forever, so it only counts while a copy of the app runs
/// that started before the use did. A use must also have lasted a moment: the microphone
/// <see cref="MicrophoneSettle"/> (dictating a sentence isn't a call; a call lasts longer), the camera
/// and screen capture <see cref="DeviceSettle"/> (a screenshot holds the capture for milliseconds).
/// </summary>
public static class MeetingDetection
{
    /// <summary>How long the microphone must be in use before it counts as a call.</summary>
    public static readonly TimeSpan MicrophoneSettle = TimeSpan.FromSeconds(10);

    /// <summary>How long the camera or a screen capture must be in use before it counts.</summary>
    public static readonly TimeSpan DeviceSettle = TimeSpan.FromSeconds(1);

    /// <summary>The meeting these signals add up to, or null for none. <paramref name="inUse"/> lists
    /// the devices held right now (<see cref="DeviceUse"/>); the strongest signal wins.</summary>
    public static Meeting? Detect(bool presentationMode, bool displayDuplicated, IEnumerable<DeviceUse> inUse, DateTime nowUtc)
    {
        if (presentationMode) return new Meeting(MeetingSignal.Presenting, null);
        if (displayDuplicated) return new Meeting(MeetingSignal.DisplayDuplicated, null);
        DeviceUse? strongest = null;
        foreach (var use in inUse)
        {
            if (use.RunningSinceUtc is not { } since || since > use.StartUtc) continue; // left behind by a copy that's gone
            var settle = use.Signal == MeetingSignal.Microphone ? MicrophoneSettle : DeviceSettle;
            if (nowUtc - use.StartUtc < settle) continue;
            if (strongest is not { } best || use.Signal < best.Signal) strongest = use;
        }
        return strongest is { } found ? new Meeting(found.Signal, found.App) : null;
    }

    /// <summary>A short name for <paramref name="app"/> as <see cref="Meeting.App"/> holds it: an exe's
    /// file name without ".exe" ("Zoom"), or a packaged app's name without its publisher and hash
    /// ("MSTeams_8wekyb3d8bbwe" -> "MSTeams", "5319275A.WhatsAppDesktop_cv1g1gvanyjgm" ->
    /// "WhatsAppDesktop").</summary>
    public static string AppName(string app)
    {
        if (IsExePath(app)) return Path.GetFileNameWithoutExtension(app);
        string name = app.Split('_')[0];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    /// <summary>True when <paramref name="app"/> is an exe path (an unpackaged app), not a package family name.</summary>
    public static bool IsExePath(string app) => app.Contains('\\');
}
