namespace AgentsCursor.Core;

/// <summary>The name a waiting session's project goes by: the last folder of the agent's working
/// directory (the <c>cwd</c> every agent sends with its events).</summary>
public static class ProjectName
{
    static readonly char[] Separators = { '\\', '/' };

    /// <summary>The last folder of <paramref name="path"/>, ignoring trailing separators:
    /// <c>C:\Users\me\Lumicity\</c> is "Lumicity", <c>\\server\share</c> is "share",
    /// <c>/home/me/app</c> is "app". A root has no folder, so it names itself: <c>C:\</c> is "C:\",
    /// <c>/</c> is "/". Null when there is no path at all (missing, empty or blank).</summary>
    public static string? Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string trimmed = path.Trim();
        string folders = trimmed.TrimEnd(Separators);
        if (folders.Length == 0) return trimmed[..1]; // "/" or "\": the root of the current drive
        string last = folders[(folders.LastIndexOfAny(Separators) + 1)..];
        // A drive ("C:", or "\\?\C:") is a root: name it as Explorer's address bar does.
        return last.Length == 2 && last[1] == ':' && char.IsAsciiLetter(last[0]) ? last + "\\" : last;
    }
}

/// <summary>
/// The label under the token naming the project the shown session is in (<see cref="TokenFace.Project"/>),
/// in the control panel's theme: a black box with white text when the panel is dark, a white box with
/// black text when it is light. Only drawn, never stored: the history knows nothing of projects.
/// <see cref="NotifierRenderer"/> draws it; <see cref="TokenFootprint"/> makes room for it.
/// </summary>
public sealed record TokenLabel(string Text, bool Dark)
{
    /// <summary>The project name the previews show.</summary>
    public const string SampleProject = "my-project";

    /// <summary>The label for <paramref name="face"/>: its project, while
    /// <see cref="CursorSettings.ShowProjectName"/> is on; null when off or the session named no
    /// project. <paramref name="dark"/> is the panel's theme (<see cref="IsDark"/>).</summary>
    public static TokenLabel? For(CursorSettings settings, TokenFace face, bool dark)
        => settings.ShowProjectName && !string.IsNullOrEmpty(face.Project) ? new TokenLabel(face.Project, dark) : null;

    /// <summary>Whether the panel is dark: <paramref name="theme"/>, or with
    /// <see cref="ThemePreference.System"/> Windows' app mode (<paramref name="windowsDark"/>).</summary>
    public static bool IsDark(ThemePreference theme, bool windowsDark)
        => theme == ThemePreference.Dark || (theme == ThemePreference.System && windowsDark);

    // ---------- geometry (px, for a token of tokenPx px at dpi, 1 = 96 DPI) ----------

    /// <summary>The text's em size: about a seventh of the token, but never below
    /// <see cref="MinEm"/> px at 96 DPI, where it would stop being readable.</summary>
    public static float EmPx(float tokenPx, float dpi) => Math.Max(tokenPx * EmRatio, MinEm * Math.Max(dpi, 0.5f));

    /// <summary>The widest the text gets before a long name is cut short with an ellipsis: one and a
    /// half times the token, or eight ems on a small one (about a dozen letters).</summary>
    public static float MaxTextPx(float tokenPx, float dpi) => Math.Max(tokenPx * 1.5f, EmPx(tokenPx, dpi) * 8f);

    /// <summary>How far below the token's centre the label's top sits, at most: the icon's box, its
    /// widest contrast rim and the gap. (The renderer hangs it from the icon's own bottom, which is never
    /// lower.)</summary>
    public static float Top(float tokenPx) => tokenPx * (0.5f + NotifierRenderer.MaxRimRatio + GapRatio);

    /// <summary>The gap between the icon's rim and the label, as a fraction of the token.</summary>
    public const float GapRatio = 0.03f;

    const float EmRatio = 0.14f;
    const float MinEm = 12f;
}
