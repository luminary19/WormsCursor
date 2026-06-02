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
    readonly ToolStripMenuItem _enabledItem;
    readonly ToolStripMenuItem _startupItem;

    // A hidden, handle-backed control bound to the UI thread. Cross-process signals
    // (single-instance guard) BeginInvoke through it to hop onto the UI thread before
    // touching WinForms. We force its handle now, while we're on the STA UI thread.
    readonly Control _marshal = new();
    bool _preferencesOpen;

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

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Preferences…", null, OnPreferences);
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
        finally
        {
            _preferencesOpen = false;
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
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
