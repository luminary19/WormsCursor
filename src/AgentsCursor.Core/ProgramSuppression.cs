using System.Collections.ObjectModel;

namespace AgentsCursor.Core;

/// <summary>
/// The pure half of per-app focus suppression: turning what a user typed or picked into a canonical
/// program name, and deciding whether a program is on the "don't show the token over this" list.
/// No Win32 here, so it is unit-tested directly; <see cref="ForegroundWatcher"/> supplies the live
/// foreground program name to match.
/// </summary>
public static class ProgramSuppression
{
    /// <summary>The list a fresh install starts with.</summary>
    public static IReadOnlyList<string> Defaults { get; } = Normalize(new[] { "dota2.exe" });

    /// <summary>
    /// Canonical program name for one entry, or null when nothing usable is left. Trims whitespace and
    /// surrounding quotes, reduces a full path to its file name, and appends ".exe" when it is missing,
    /// so <c>"C:\Games\dota 2 beta\game\bin\win64\dota2.exe"</c>, <c>dota2.exe</c> and <c>Dota2</c>
    /// all name the same program. The user's casing is kept for display; matching ignores case.
    /// </summary>
    public static string? NormalizeName(string? raw)
    {
        if (raw is null) return null;
        string name = Path.GetFileName(raw.Trim().Trim('"').Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>Normalizes every entry, drops blanks and invalid names, and removes case-insensitive
    /// duplicates (the first spelling wins). The result is a fresh read-only list nobody else holds,
    /// so it can be shared between settings copies without being changed underneath them.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string? raw in names ?? Array.Empty<string?>())
        {
            if (NormalizeName(raw) is string name && seen.Add(name)) result.Add(name);
        }
        return new ReadOnlyCollection<string>(result.ToArray());
    }

    /// <summary>True when <paramref name="program"/> (a file name or a full image path) is one of the
    /// listed programs. Case-insensitive; an unknown (null) program never matches.</summary>
    public static bool Matches(string? program, IReadOnlyList<string> list)
    {
        if (string.IsNullOrEmpty(program)) return false;
        string name = Path.GetFileName(program);
        foreach (string listed in list)
        {
            if (string.Equals(listed, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
