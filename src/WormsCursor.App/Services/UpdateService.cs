using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Sources;

namespace WormsCursor.App.Services;

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/>: reports the current version, checks
/// GitHub Releases for a newer one, downloads it and restarts into it, and fetches the release notes
/// for the in-app "What's new". Nothing updates on its own: the control panel's "Check for updates"
/// drives every step.
///
/// Modeled on PowerLink's UpdateService. <see cref="IsVelopackInstalled"/> is false for an
/// ad-hoc dev build out of bin\, where updating is unavailable and version reporting falls back to the
/// assembly version.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/luminary19/WormsCursor";

    // GitHub REST endpoint for the repo's releases (derived from RepoUrl). Each release's
    // body is the CHANGELOG section the release workflow extracted, so this feeds the
    // in-app "What's new" dialog with already-per-version, dated notes.
    private static readonly string ApiReleasesUrl =
        RepoUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases";

    // One shared client; GitHub requires a User-Agent and rejects requests without one.
    private static readonly HttpClient Http = CreateHttp();

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WormsCursor");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// Fetches the repo's published releases (newest first) for the in-app changelog. Drafts
    /// are dropped; each entry's <see cref="ReleaseNote.Body"/> is the release notes markdown.
    /// Throws on network / rate-limit / parse failure — the caller falls back to the Releases
    /// page. Anonymous GitHub API is rate-limited (~60/h per IP), ample for occasional use.
    /// <paramref name="cancellationToken"/> abandons the request (e.g. the dialog was closed).
    /// </summary>
    public async Task<IReadOnlyList<ReleaseNote>> FetchReleaseNotesAsync(CancellationToken cancellationToken = default)
    {
        var json = await Http.GetStringAsync(ApiReleasesUrl, cancellationToken).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<GithubRelease>>(json) ?? new();
        return releases
            .Where(r => !r.Draft)
            .Select(r => new ReleaseNote(
                Version: (r.TagName ?? r.Name ?? string.Empty).TrimStart('v', 'V'),
                Published: r.PublishedAt,
                Body: (r.Body ?? string.Empty).Trim(),
                Prerelease: r.Prerelease))
            .ToList();
    }

    // True for both flavors that ship from CI (Setup.exe install or Velopack
    // Portable.zip extracted). False for ad-hoc builds run out of bin\Debug
    // or any layout missing Update.exe in the parent folder.
    public bool IsVelopackInstalled => _manager.IsInstalled;

    public string CurrentVersionText
    {
        get
        {
            var v = _manager.CurrentVersion;
            if (v != null) return v.ToString();
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>A newer release on GitHub, or null when this is the latest. Only for an installed copy
    /// (<see cref="IsVelopackInstalled"/>); throws on a network or feed failure.</summary>
    public Task<UpdateInfo?> CheckForUpdatesAsync() => _manager.CheckForUpdatesAsync();

    /// <summary>Downloads <paramref name="update"/>, reporting 0-100 through <paramref name="progress"/>.</summary>
    public Task DownloadAsync(UpdateInfo update, Action<int> progress, CancellationToken cancellationToken = default)
        => _manager.DownloadUpdatesAsync(update, progress, cancellationToken);

    /// <summary>A release that was downloaded earlier and installs on restart, or null.</summary>
    public VelopackAsset? PendingRestart => _manager.UpdatePendingRestart;

    /// <summary>Starts Velopack's updater, which waits (up to a minute) for this app to exit, installs
    /// <paramref name="release"/> and starts the new version. Throws if the updater can't start; the
    /// caller then exits normally (the tray's <c>RestartToUpdate</c>).</summary>
    public void InstallOnExit(VelopackAsset release) => _manager.WaitExitThenApplyUpdates(release, silent: false, restart: true);

    public string RepoPageUrl => RepoUrl;

    public string ReleasesPageUrl => $"{RepoUrl}/releases/latest";

    public void OpenReleasesPage() => OpenUrl(ReleasesPageUrl);

    public void OpenRepoPage() => OpenUrl(RepoUrl);

    // Opens a page in the default browser. Best-effort: a machine with no browser association just
    // does nothing, which is logged.
    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write($"couldn't open {url}: {ex.Message}");
        }
    }
}

/// <summary>One published release for the in-app changelog.</summary>
public sealed record ReleaseNote(string Version, DateTimeOffset? Published, string Body, bool Prerelease);

/// <summary>Subset of the GitHub release JSON we read.</summary>
internal sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
}
