using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Tray-only application context: there is no main window. It owns the tray icon and
/// the cursor engine, and guarantees the default cursors are restored on exit.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    readonly CursorSettings _settings = SettingsStore.Load();
    readonly CursorEngine _engine;
    readonly NotifyIcon _tray;
    readonly Icon _appIcon;
    readonly IAutostart _autostart = new RegistryAutostart();
    readonly UpdateService _updates = new();
    readonly ToolStripMenuItem _enabledItem;
    readonly ToolStripMenuItem _startupItem;
    readonly ToolStripMenuItem _updatesItem;

    public TrayApplicationContext()
    {
        _engine = new CursorEngine(_settings);
        _appIcon = LoadAppIcon();

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = false, // we manage Checked ourselves so double-click can't desync it
            Checked = true,
        };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutostart)
        {
            CheckOnClick = false,
            Checked = _autostart.IsEnabled,
        };

        _updatesItem = new ToolStripMenuItem("Check for updates…", null, OnCheckForUpdates);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Preferences…", null, OnPreferences);
        menu.Items.Add(_updatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "WormsCursor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OnToggleEnabled(this, EventArgs.Empty);

        // Safety net: restore the default cursors no matter how we exit.
        // SetSystemCursor is a global, persistent change, so every interceptable
        // teardown path must undo it.
        Application.ApplicationExit += (_, _) => _engine.Dispose();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _engine.Dispose();
        // Crash paths: restore before the process goes down.
        Application.ThreadException += (_, _) => _engine.RestoreNow();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _engine.RestoreNow();

        // Self-heal: if a previous instance was killed (Task Manager / "Stop
        // Debugging") it may have left a rotated cursor. Reload the real scheme before
        // we install our own, so re-launching always starts clean.
        CursorEngine.RestoreDefaultCursors();

        _engine.Start();
    }

    void OnToggleEnabled(object? sender, EventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
    }

    void OnToggleAutostart(object? sender, EventArgs e)
    {
        try
        {
            if (_autostart.IsEnabled) _autostart.Disable();
            else _autostart.Enable();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(3000, "WormsCursor", "Couldn't change autostart: " + ex.Message, ToolTipIcon.Warning);
        }
        _startupItem.Checked = _autostart.IsEnabled;
    }

    void OnPreferences(object? sender, EventArgs e)
    {
        using var dlg = new PreferencesForm(_settings.Clone());
        if (dlg.ShowDialog() != DialogResult.OK) return;

        _settings.CopyFrom(dlg.Settings);   // apply edits to the live settings the engine holds
        SettingsStore.Save(_settings);

        // Rebuild so the new appearance takes effect, preserving enabled/disabled state.
        bool wasRunning = _engine.IsRunning;
        _engine.Stop();
        if (wasRunning) _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
    }

    // Tray-driven update check. Velopack has no UI of its own, so we surface
    // progress/results through balloon tips. await-ing keeps the continuations on
    // the UI thread (WinForms SynchronizationContext) so the NotifyIcon calls and
    // ApplyUpdatesAndRestart (which tears down this process) are thread-safe.
    async void OnCheckForUpdates(object? sender, EventArgs e)
    {
        _updatesItem.Enabled = false;
        try
        {
            var result = await _updates.CheckAsync();
            switch (result.Availability)
            {
                case UpdateAvailability.NotInstalled:
                    // Dev build (run from bin\) — can't self-update. Offer the
                    // Releases page so the user can grab a real Setup.exe.
                    _tray.ShowBalloonTip(4000, "WormsCursor",
                        "Dev build — auto-update disabled. Opening the Releases page.",
                        ToolTipIcon.Info);
                    _updates.OpenReleasesPage();
                    break;

                case UpdateAvailability.UpToDate:
                    _tray.ShowBalloonTip(3000, "WormsCursor",
                        $"You're up to date (v{_updates.CurrentVersionText}).",
                        ToolTipIcon.Info);
                    break;

                case UpdateAvailability.Available:
                    _tray.ShowBalloonTip(3000, "WormsCursor",
                        $"Downloading update v{result.AvailableVersion}… the app will restart.",
                        ToolTipIcon.Info);
                    // VelopackInfo is non-null when Availability == Available.
                    await _updates.ApplyAsync(result.VelopackInfo!);
                    // If we get here the restart didn't happen.
                    _tray.ShowBalloonTip(4000, "WormsCursor",
                        "Update downloaded but restart didn't happen — try again later.",
                        ToolTipIcon.Warning);
                    break;

                case UpdateAvailability.Failed:
                    _tray.ShowBalloonTip(4000, "WormsCursor",
                        "Update check failed: " + result.ErrorMessage, ToolTipIcon.Warning);
                    break;
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "WormsCursor",
                "Update failed: " + ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _updatesItem.Enabled = true;
        }
    }

    void OnExit(object? sender, EventArgs e)
    {
        _tray.Visible = false;
        _engine.Dispose();
        ExitThread();
    }

    // Loads the monochrome arrow built by tools/generate-icon.py, copied next to
    // the exe (CopyToOutputDirectory). Picks the small-icon size for a crisp tray
    // glyph; falls back to the stock icon if the asset is somehow missing.
    static Icon LoadAppIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icon.ico");
        try { return new Icon(path, SystemInformation.SmallIconSize); }
        catch { return (Icon)SystemIcons.Application.Clone(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _engine.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
