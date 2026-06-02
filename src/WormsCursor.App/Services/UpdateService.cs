using System.Diagnostics;
using System.Reflection;
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

    private readonly UpdateManager _manager;

    public UpdateService()
    {
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _manager = new UpdateManager(source);
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
