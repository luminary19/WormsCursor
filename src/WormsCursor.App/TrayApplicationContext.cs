using System.Runtime.InteropServices;
using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// Tray-only application context: there is no main window. It owns the tray icon and the agent-notifier
/// overlay (the bouncing tool logo shown while an AI agent is waiting for you), and the named-pipe
/// plumbing that feeds it.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    readonly CursorSettings _settings = SettingsStore.Load();
    readonly NotifierOverlay _overlay;
    readonly NotifyIcon _tray;
    readonly Icon _appIcon;
    readonly IAutostart _autostart = new RegistryAutostart();
    readonly UpdateService _updates = new();
    readonly ToolStripMenuItem _enabledItem;

    // A hidden, handle-backed control bound to the UI thread. Cross-process signals
    // (single-instance guard) BeginInvoke through it to hop onto the UI thread before
    // touching WinForms. We force its handle now, while we're on the STA UI thread.
    readonly Control _marshal = new();
    bool _preferencesOpen;

    // Agent notifier: tracks how many agents await the user (fed by the pipe), pushes the waiting set
    // into the overlay, and reflects the count in the tray tooltip. The pipe server + sweep timer live
    // here so they survive a settings change.
    readonly AgentActivity _agents = new();
    AgentPipeServer? _agentPipe;
    System.Windows.Forms.Timer? _agentSweep;

    public TrayApplicationContext()
    {
        _ = _marshal.Handle; // realise the window handle on the UI thread for BeginInvoke

        // Force the notifier on at every startup: a persisted `false` (e.g. an accidental "Enabled"
        // toggle, or a stray hand-edit) self-heals back to `true` so the token can never get stuck off
        // across launches. The tray "Enabled" item still toggles it for the current run — it just no
        // longer survives a restart. Only persist when we actually changed something.
        if (!_settings.AgentNotifierEnabled)
        {
            _settings.AgentNotifierEnabled = true;
            SettingsStore.Save(_settings);
        }

        // Self-heal for anyone upgrading from the cursor-theming version: if that older build was
        // force-killed it may have left a themed system cursor via SetSystemCursor. Reload the real
        // scheme once on startup. Harmless when nothing was hijacked; this app never themes cursors.
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { /* best effort */ }

        _overlay = new NotifierOverlay(_settings);
        _appIcon = LoadAppIcon();

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = false, // we manage Checked ourselves so double-click can't desync it
            Checked = _settings.AgentNotifierEnabled,
        };

        // The tray menu is deliberately minimal — Enabled / Preferences… / Exit. Autostart, the
        // agent-notifications dialog, and update checks all live inside Preferences.
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

        // The overlay touches no global system state, so there's nothing to undo on exit — but still
        // dispose it cleanly so the layered window goes away promptly.
        Application.ApplicationExit += (_, _) => _overlay.Dispose();

        StartAgentNotifier();
        MaybeShowWhatsNew();
    }

    // Brings up the agent-notifier plumbing: the named-pipe server that `WormsCursor.exe hook`
    // invocations write to, a periodic sweep that evicts stale sessions, and the wiring that turns
    // a changed waiting set into an overlay update + tray tooltip.
    void StartAgentNotifier()
    {
        _agents.WaitingCountChanged += OnWaitingCountChanged;
        _agents.Ttl = TimeSpan.FromSeconds(_settings.AgentNotifierTimeoutSeconds);

        _agentPipe = new AgentPipeServer(msg =>
        {
            // The pipe fires on its own thread; hop to the UI thread so AgentActivity's events
            // (which touch the tray + overlay) stay single-threaded, like every other callback here.
            if (_marshal.IsHandleCreated)
                _marshal.BeginInvoke(new Action(() => HandleAgentEvent(msg)));
        });
        _agentPipe.Start();

        // Sweep often (every 3 s) so even the shortest timeout (10 s) clears a stuck logo within a few
        // seconds of it elapsing. The sweep just scans a tiny dictionary; it's cheap.
        _agentSweep = new System.Windows.Forms.Timer { Interval = 3_000 };
        _agentSweep.Tick += (_, _) => _agents.Sweep(DateTime.UtcNow);
        _agentSweep.Start();
    }

    void HandleAgentEvent(AgentEventMessage msg)
    {
        if (msg.TryGetKind(out var kind))
            _agents.Report(msg.Tool, msg.SessionId, msg.Project, kind, DateTime.UtcNow);
    }

    void OnWaitingCountChanged(int count)
    {
        _overlay.SetWaitingAgents(_agents.WaitingTools);
        _tray.Text = count > 0
            ? $"WormsCursor — {count} agent{(count == 1 ? "" : "s")} waiting"
            : "WormsCursor";
    }

    // First launch after an update: show the new version's "What's new" notes once. Compares
    // the installed version against the last one we recorded and stores the current version so
    // it pops only once. Skipped for dev builds and for a brand-new install (no prior version).
    void MaybeShowWhatsNew()
    {
        if (!_updates.IsVelopackInstalled) return; // dev build out of bin\ — don't pop
        string current = _updates.CurrentVersionText;
        string lastSeen = _settings.LastSeenVersion;
        if (string.Equals(lastSeen, current, StringComparison.OrdinalIgnoreCase)) return;

        _settings.LastSeenVersion = current; // record now, so it pops only once even if the dialog fails
        SettingsStore.Save(_settings);

        if (string.IsNullOrEmpty(lastSeen)) return; // nothing recorded yet (first run): just remember, don't pop

        // We're still in the ctor (before Application.Run); defer until the message loop pumps.
        _marshal.BeginInvoke(new Action(() =>
        {
            using var dlg = new ChangelogForm(_updates);
            dlg.ShowDialog();
        }));
    }

    // Master on/off for the notifier (the tray "Enabled" item + double-click). Flips
    // AgentNotifierEnabled, persists it, and re-evaluates the overlay live.
    void OnToggleEnabled(object? sender, EventArgs e)
    {
        _settings.AgentNotifierEnabled = !_settings.AgentNotifierEnabled;
        SettingsStore.Save(_settings);
        _enabledItem.Checked = _settings.AgentNotifierEnabled;
        _overlay.RefreshSettings();
    }

    void OnPreferences(object? sender, EventArgs e) => OpenPreferences();

    /// <summary>Shows the "Agent notifications" settings panel (display + hook registration).
    /// Opened from Preferences (the "Agent settings…" button); marshals onto the UI thread so it's
    /// safe to call from anywhere.</summary>
    public void OpenAgentHooks()
    {
        if (_marshal.InvokeRequired) { _marshal.BeginInvoke(OpenAgentHooks); return; }
        using var dlg = new AgentHooksForm(
            _settings.AgentNotifierEnabled, _settings.AgentNotifierTimeoutSeconds, ApplyAgentDisplay, PreviewWaitingCount);
        dlg.ShowDialog();
    }

    // Drives the live token for the "Preview" test in a dialog: a non-null value fakes that many
    // waiting agents (one logo + "+N"); null ends the preview and restores the real set (so a real
    // agent event arriving mid-preview, or just closing the dialog, leaves the genuine token on screen).
    static readonly string[] PreviewTools = { "claude-code" }; // Codex hidden for now
    void PreviewWaitingCount(int? testCount)
    {
        if (testCount is not int n) { _overlay.SetWaitingAgents(_agents.WaitingTools); return; }
        var tools = new string[Math.Max(0, n)];
        for (int i = 0; i < tools.Length; i++) tools[i] = PreviewTools[i % PreviewTools.Length];
        _overlay.SetWaitingAgents(tools);
    }

    // The overlay reads AgentNotifierEnabled live each frame and AgentActivity holds the linger
    // timeout, so a display change takes effect immediately — push the TTL, persist, and re-evaluate.
    void ApplyAgentDisplay(bool enabled, int timeoutSeconds)
    {
        _settings.AgentNotifierEnabled = enabled;
        _settings.AgentNotifierTimeoutSeconds = timeoutSeconds;
        _settings.Normalize(); // clamp the timeout into range
        _agents.Ttl = TimeSpan.FromSeconds(_settings.AgentNotifierTimeoutSeconds);
        SettingsStore.Save(_settings);
        _enabledItem.Checked = _settings.AgentNotifierEnabled;
        _overlay.RefreshSettings();
    }

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
            using var dlg = new PreferencesForm(_settings.Clone(), _updates, ApplySettings, _autostart, OpenAgentHooks);
            var result = dlg.ShowDialog();
            if (result == DialogResult.OK) ApplySettings(dlg.Settings);
        }
        finally
        {
            _preferencesOpen = false;
        }
    }

    /// <summary>
    /// Apply edited settings — used by both Preferences "Apply" and "OK". Persists them and re-reads
    /// them into the live overlay (size/placement/corner take effect on the next frame; no restart).
    /// </summary>
    void ApplySettings(CursorSettings source)
    {
        _settings.CopyFrom(source);
        SettingsStore.Save(_settings);
        _enabledItem.Checked = _settings.AgentNotifierEnabled;
        _overlay.RefreshSettings();
    }

    void OnExit(object? sender, EventArgs e)
    {
        _tray.Visible = false;
        _agentSweep?.Dispose();
        _agentPipe?.Dispose();
        _overlay.Dispose();
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
            _agentSweep?.Dispose();
            _agentPipe?.Dispose();
            _tray.Dispose();
            _overlay.Dispose();
            _appIcon.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }

    // Reload the user's configured cursor scheme from the registry (used once on startup to undo a
    // themed cursor a killed older version may have left behind).
    const uint SPI_SETCURSORS = 0x0057;
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
}
