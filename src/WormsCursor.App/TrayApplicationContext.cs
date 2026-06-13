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

    // A hidden, handle-backed control bound to the UI thread. Cross-process signals
    // (single-instance guard) BeginInvoke through it to hop onto the UI thread before
    // touching WinForms. We force its handle now, while we're on the STA UI thread.
    readonly Control _marshal = new();
    bool _preferencesOpen;

    // Typing-hop keyboard hook (click feedback). Non-null only while it's installed; lives on
    // the UI thread so the low-level hook has a message loop. See SyncClickFeedbackHook.
    LowLevelKeyboardHook? _keyHook;

    public TrayApplicationContext()
    {
        _ = _marshal.Handle; // realise the window handle on the UI thread for BeginInvoke

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
        SyncClickFeedbackHook();
    }

    void OnToggleEnabled(object? sender, EventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
        SyncClickFeedbackHook();
    }

    // Installs the typing-hop keyboard hook only while click feedback is ON and the engine is
    // running, and tears it down otherwise — so we don't hold a global hook when the feature is
    // off or the cursor theming is disabled. Must run on the UI thread (it has the message loop).
    void SyncClickFeedbackHook()
    {
        bool want = _engine.IsRunning && _settings.ClickFeedback;
        if (want && _keyHook is null)
        {
            _keyHook = new LowLevelKeyboardHook();
            _keyHook.KeyDown += _engine.NudgeIbeam;
        }
        else if (!want && _keyHook is not null)
        {
            _keyHook.Dispose();
            _keyHook = null;
        }
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

    void OnPreferences(object? sender, EventArgs e) => OpenPreferences();

    /// <summary>
    /// Shows the Preferences dialog and applies any changes. Safe to call from a
    /// background thread (e.g. the single-instance signal handler): it marshals onto
    /// the UI thread, since WinForms dialogs may only be shown there. Re-entrancy is
    /// guarded so a second signal while the dialog is already open is ignored.
    /// </summary>
    public void OpenPreferences()
    {
        if (_marshal.InvokeRequired)
        {
            _marshal.BeginInvoke(OpenPreferences);
            return;
        }

        if (_preferencesOpen) return; // already showing; bring no second dialog up
        _preferencesOpen = true;
        try
        {
            // Pass SetTestCursor + ApplySettings so the dialog's "Test cursor" control can
            // force a cursor on the live (still-running) engine, and its "Apply" button can
            // commit edits without closing.
            using var dlg = new PreferencesForm(_settings.Clone(), _updates, _engine.SetTestCursor, ApplySettings);
            var result = dlg.ShowDialog();
            _engine.SetTestCursor(TestCursor.Off); // always stop forcing a test cursor on close
            if (result == DialogResult.OK) ApplySettings(dlg.Settings);
        }
        finally
        {
            _preferencesOpen = false;
        }
    }

    /// <summary>
    /// Apply edited settings to the live engine — used by both Preferences "Apply" and
    /// "OK". Persists them and rebuilds the cursors, preserving the enabled/disabled
    /// state. A running test cursor survives the rebuild, so you can tweak size/colour
    /// and watch it update without closing the dialog.
    /// </summary>
    void ApplySettings(CursorSettings source)
    {
        _settings.CopyFrom(source);
        SettingsStore.Save(_settings);

        bool wasRunning = _engine.IsRunning;
        _engine.Stop();
        if (wasRunning) _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
        SyncClickFeedbackHook(); // ClickFeedback may have been toggled
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
        _keyHook?.Dispose();
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
            _keyHook?.Dispose();
            _tray.Dispose();
            _engine.Dispose();
            _appIcon.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
