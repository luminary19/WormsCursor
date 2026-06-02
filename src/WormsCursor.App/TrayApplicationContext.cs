using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Tray-only application context: there is no main window. It owns the tray icon and
/// the cursor engine, and guarantees the default cursors are restored on exit.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    readonly CursorSettings _settings = new();
    readonly CursorEngine _engine;
    readonly NotifyIcon _tray;
    readonly Icon _appIcon;
    readonly ToolStripMenuItem _enabledItem;

    public TrayApplicationContext()
    {
        _engine = new CursorEngine(_settings);
        _appIcon = LoadAppIcon();

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = false, // we manage Checked ourselves so double-click can't desync it
            Checked = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
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

        _engine.Start();
    }

    void OnToggleEnabled(object? sender, EventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
    }

    void OnPreferences(object? sender, EventArgs e)
    {
        using var dlg = new PreferencesForm(_settings);
        if (dlg.ShowDialog() == DialogResult.OK && _engine.IsRunning)
        {
            // Rebuild cursors/loop so changed settings take effect immediately.
            _engine.Stop();
            _engine.Start();
            _enabledItem.Checked = _engine.IsRunning;
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
