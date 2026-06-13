using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Sources;

namespace WormsCursor.App.Services;

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/> that knows how to
/// report the current version and check / download / apply updates published as
/// GitHub releases on the WormsCursor repo.
///
/// Modeled on PowerLink's UpdateService. Guarded to no-op cleanly when the app
/// is NOT running from a Velopack-managed install (i.e. an ad-hoc dev build out
/// of bin\Debug): <see cref="IsVelopackInstalled"/> is false there and
/// <see cref="ApplyAsync"/> refuses to run.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/dawidope/WormsCursor";

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
    /// </summary>
    public async Task<IReadOnlyList<ReleaseNote>> FetchReleaseNotesAsync()
    {
        var json = await Http.GetStringAsync(ApiReleasesUrl).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<GithubRelease>>(json) ?? new();
        return releases
            .Where(r => !r.Draft)
            .Select(r => new ReleaseNote(
                Version: (r.TagName ?? r.Name ?? string.Empty).TrimStart('v', 'V'),
                Title: r.Name ?? r.TagName ?? "(untitled)",
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

    public string ReleasesPageUrl => $"{RepoUrl}/releases/latest";

    // No CancellationToken: Velopack's CheckForUpdatesAsync / DownloadUpdatesAsync
    // don't accept one, so we'd be lying about cancellation.
    public async Task<UpdateCheckResult> CheckAsync()
    {
        // Dev builds have no Update.exe; CheckForUpdatesAsync would still hit
        // GitHub but there'd be no way to apply, so short-circuit honestly.
        if (!IsVelopackInstalled)
            return new UpdateCheckResult(UpdateAvailability.NotInstalled, null, null);

        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
                return new UpdateCheckResult(UpdateAvailability.UpToDate, null, null);

            var version = info.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(UpdateAvailability.Available, version, info);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateAvailability.Failed, null, null, ex.Message);
        }
    }

    public async Task ApplyAsync(UpdateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!IsVelopackInstalled)
            throw new InvalidOperationException(
                "ApplyAsync requires the app to be running from a Velopack-managed " +
                "install (Setup.exe or Portable.zip). Dev builds out of bin\\ can't update.");

        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _manager.ApplyUpdatesAndRestart(info);
    }

    public void OpenReleasesPage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ReleasesPageUrl,
            UseShellExecute = true,
        });
    }
}

public enum UpdateAvailability
{
    UpToDate,
    Available,
    Failed,
    // Running from a dev build (no Update.exe) — in-app update can't restart it.
    NotInstalled,
}

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string? AvailableVersion,
    UpdateInfo? VelopackInfo,
    string? ErrorMessage = null);

/// <summary>One published release for the in-app changelog.</summary>
public sealed record ReleaseNote(string Version, string Title, DateTimeOffset? Published, string Body, bool Prerelease);

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
