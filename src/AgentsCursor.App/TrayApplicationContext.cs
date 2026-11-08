using System.Drawing;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Velopack;
using AgentsCursor.App.ControlPanel;
using AgentsCursor.App.Services;
using AgentsCursor.Core;

namespace AgentsCursor.App;

/// <summary>
/// Tray-only application context: there is no main window. It owns the tray icon (WinForms), the
/// agent-notifier overlay (the bouncing tool logo shown while an AI agent is waiting for you), the
/// named-pipe plumbing that feeds it, the one <see cref="LiveSettings"/> every edit goes through, and
/// the WPF control panel, which it creates on first use and keeps for the app's lifetime.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext, IControlPanelHost
{
    readonly LiveSettings _settings;
    readonly NotifierOverlay _overlay;
    readonly NotifyIcon _tray;
    readonly Icon _appIcon;
    readonly RegistryAutostart _autostart = new();
    readonly UpdateService _updates = new();
    readonly ToolStripMenuItem _enabledItem;

    // A hidden, handle-backed control bound to the UI thread. Cross-process signals
    // (single-instance guard) and other threads BeginInvoke through it to hop onto the UI thread
    // before touching WinForms or WPF. We force its handle now, while we're on the STA UI thread.
    readonly Control _marshal = new();
    ControlPanelWindow? _panel;

    // Agent notifier: tracks how many agents await the user (fed by the pipe), pushes the waiting set
    // into the overlay, and reflects the count in the tray tooltip. The pipe server + sweep timer live
    // here so they survive a settings change.
    readonly AgentActivity _agents = new();
    AgentPipeServer? _agentPipe;
    System.Windows.Forms.Timer? _agentSweep;

    // The agent connections changed during this run (by the startup refresh or on the Agents page),
    // and why the startup refresh couldn't update a tool; the Agents page shows both on its cards.
    readonly Dictionary<string, ConnectionChange> _changedThisRun = new();
    readonly Dictionary<string, string> _refreshErrors = new();

    public TrayApplicationContext()
    {
        _ = _marshal.Handle; // realise the window handle on the UI thread for BeginInvoke

        MigrateAutostartFromWormsCursor();
        // Load reads WormsCursor's settings file while there is no AgentsCursor one yet.
        _settings = new LiveSettings(SettingsStore.Load(), Persist);
        IconSource.Log = AppLog.Write;

        // Force the notifier on at every startup: a persisted `false` (e.g. an accidental "Enabled"
        // toggle, or a stray hand-edit) self-heals back to `true` so the token can never get stuck off
        // across launches. The tray "Enabled" item still toggles it for the current run — it just no
        // longer survives a restart. Only persist when we actually changed something.
        if (!_settings.Live.AgentNotifierEnabled) _settings.Change(s => s.AgentNotifierEnabled = true);

        // Self-heal for anyone upgrading from the cursor-theming version: if that older build was
        // force-killed it may have left a themed system cursor via SetSystemCursor. Reload the real
        // scheme once on startup. Harmless when nothing was hijacked; this app never themes cursors.
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { /* best effort */ }

        _overlay = new NotifierOverlay(_settings.Live);
        _appIcon = LoadAppIcon();

        _enabledItem = new ToolStripMenuItem("Enabled", null, OnToggleEnabled)
        {
            CheckOnClick = false, // we manage Checked ourselves so double-click can't desync it
            Checked = _settings.Live.AgentNotifierEnabled,
        };

        // The tray menu is deliberately minimal — Enabled / Preferences… / Exit. Everything else
        // (the token's look, agent hooks, hidden-while-using apps, updates, autostart) lives in the panel.
        var menu = new ContextMenuStrip();
        menu.Items.Add(_enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Preferences…", null, (_, _) => OpenPreferences());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExit);

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "AgentsCursor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OnToggleEnabled(this, EventArgs.Empty);
        _tray.BalloonTipClicked += (_, _) => OpenPanel(PanelSection.Agents);

        // The overlay touches no global system state, so there's nothing to undo on exit — but still
        // dispose it cleanly so the layered window goes away promptly.
        Application.ApplicationExit += (_, _) => _overlay.Dispose();

        _settings.Changed += OnSettingsChanged;
        StartAgentNotifier();
        // After the pipe is up, and once the message loop runs, so startup stays quick.
        _marshal.BeginInvoke(RefreshAgentConnections);
        MaybeShowWhatsNew();
    }

    // ---------- IControlPanelHost ----------

    public LiveSettings Settings => _settings;
    public IAutostart Autostart => _autostart;
    public UpdateService Updates => _updates;
    public IReadOnlyDictionary<string, ConnectionChange> ChangedThisRun => _changedThisRun;
    public IReadOnlyDictionary<string, string> RefreshErrors => _refreshErrors;

    public void NoteConnected(string toolId)
    {
        _changedThisRun[toolId] = ConnectionChange.ByYou;
        _refreshErrors.Remove(toolId);
    }

    public void NoteDisconnected(string toolId)
    {
        _changedThisRun.Remove(toolId);
        _refreshErrors.Remove(toolId);
    }

    // Drives the live token for the panel's "Preview on screen": a non-null value fakes that many
    // waiting agents (one logo + "+N"); null ends the preview and restores the real set (so a real
    // agent event arriving mid-preview, or just hiding the panel, leaves the genuine token on screen).
    static readonly WaitingAgent PreviewAgent = new("claude-code", WaitingState.Blocked);
    public void Preview(int? waitingAgents)
    {
        if (waitingAgents is not int n) { _overlay.SetWaitingAgents(_agents.Waiting); return; }
        _overlay.SetWaitingAgents(Enumerable.Repeat(PreviewAgent, Math.Max(0, n)).ToArray());
    }

    /// <summary>Hands <paramref name="release"/> to Velopack's updater, then exits the normal way; the
    /// updater installs it once we're gone and starts the new version. If the updater can't start, it
    /// throws before anything is taken down. Settings are already saved: every change is.</summary>
    public void RestartToUpdate(VelopackAsset release)
    {
        _updates.InstallOnExit(release);
        OnExit(this, EventArgs.Empty);
    }

    // ---------- agent notifier ----------

    // Brings up the agent-notifier plumbing: the named-pipe server that `AgentsCursor.exe hook`
    // invocations write to, a periodic sweep that evicts stale sessions, and the wiring that turns
    // a changed waiting set into an overlay update + tray tooltip.
    void StartAgentNotifier()
    {
        _agents.WaitingChanged += OnWaitingChanged;
        _agents.Ttl = TimeSpan.FromSeconds(_settings.Live.AgentNotifierTimeoutSeconds);

        // The pipe fires on its own threads; hop to the UI thread so AgentActivity's events (which
        // touch the tray + overlay) stay single-threaded, like every other callback here. A connection's
        // messages and its release are posted from the same reader, so they keep their order.
        _agentPipe = new AgentPipeServer(
            (msg, lease) => Post(() => HandleAgentEvent(msg, lease)),
            lease => Post(() => _agents.Release(LeaseOwner(lease))));
        _agentPipe.Start();

        // Sweep often (every 3 s) so even the shortest timeout (10 s) clears a stuck logo within a few
        // seconds of it elapsing. The sweep just scans a tiny dictionary; it's cheap.
        _agentSweep = new System.Windows.Forms.Timer { Interval = 3_000 };
        _agentSweep.Tick += (_, _) => _agents.Sweep(DateTime.UtcNow);
        _agentSweep.Start();
    }

    void Post(Action action)
    {
        if (_marshal.IsHandleCreated) _marshal.BeginInvoke(action);
    }

    // The owner recorded on sessions reported over a plugin's persistent connection (null for hooks).
    static string LeaseOwner(long lease) => "lease:" + lease;

    void HandleAgentEvent(AgentEventMessage msg, long? lease)
    {
        if (msg.TryGetKind(out var kind))
            _agents.Report(msg.Tool, msg.SessionId, msg.Project, kind, DateTime.UtcNow, msg.InputIds, msg.AgentId,
                msg.CallIds, lease is long id ? LeaseOwner(id) : null);
    }

    // Keeps the connections the user made pointing at this copy of AgentsCursor: a tool whose
    // connection is ours but outdated (the app moved, was renamed from WormsCursor, or registered an
    // older shape) is refreshed in place. Anything not connected, current or unreadable is left alone.
    // When refreshed tools need a step of their own (Codex: approve the changed hooks), one balloon
    // says so, and the Agents page shows each step on its card.
    void RefreshAgentConnections()
    {
        var refreshed = new List<HookTool>();
        foreach (var (tool, error) in AgentHookRegistrar.RefreshConnected())
        {
            if (error is null)
            {
                AppLog.Write($"updated the outdated {tool.DisplayName} connection to this copy of AgentsCursor");
                _changedThisRun[tool.Id] = ConnectionChange.RefreshedAtStartup;
                refreshed.Add(tool);
            }
            else
            {
                AppLog.Write($"couldn't update the outdated {tool.DisplayName} connection: {error}");
                _refreshErrors[tool.Id] = error;
            }
        }
        if (refreshed.Count > 0)
            _tray.ShowBalloonTip(10_000, "AgentsCursor updated its agent connections", BalloonText(refreshed), ToolTipIcon.Info);
    }

    // One line per refreshed tool with the step it needs. A balloon holds at most 255 characters;
    // when the steps don't fit, it names the tools, and the Agents page (where clicking the balloon
    // leads) has each step on its card.
    const int MaxBalloonText = 255;
    static string BalloonText(IReadOnlyList<HookTool> tools)
    {
        string steps = string.Join("\n", tools.Select(t => $"{t.DisplayName}: {t.AfterChange}"));
        return steps.Length <= MaxBalloonText
            ? steps
            : $"Updated {string.Join(", ", tools.Select(t => t.DisplayName))}. Click to see the step each one needs.";
    }

    void OnWaitingChanged(int count)
    {
        _overlay.SetWaitingAgents(_agents.Waiting);
        _tray.Text = count > 0
            ? $"AgentsCursor — {count} agent{(count == 1 ? "" : "s")} waiting"
            : "AgentsCursor";
    }

    // Every settings change, from the panel or the tray, lands here: push the linger timeout into the
    // activity model, mirror the master switch in the menu, and let the overlay re-read size,
    // placement, the switch and the hidden-while-using list (it reads them live; no restart).
    void OnSettingsChanged()
    {
        _agents.Ttl = TimeSpan.FromSeconds(_settings.Live.AgentNotifierTimeoutSeconds);
        _enabledItem.Checked = _settings.Live.AgentNotifierEnabled;
        _overlay.RefreshSettings();
    }

    // Master on/off for the notifier (the tray "Enabled" item + double-click).
    void OnToggleEnabled(object? sender, EventArgs e)
        => _settings.Change(s => s.AgentNotifierEnabled = !s.AgentNotifierEnabled);

    // ---------- the control panel ----------

    /// <summary>Shows the control panel where it was left (the Token page the first time), or brings
    /// it to the front. Safe to call from any thread (e.g. the single-instance signal handler): it
    /// marshals onto the UI thread, where both the WinForms tray and the WPF panel live.</summary>
    public void OpenPreferences() => OpenPanel(null);

    void OpenPanel(PanelSection? section)
    {
        if (_marshal.InvokeRequired)
        {
            _marshal.BeginInvoke(() => OpenPanel(section));
            return;
        }
        if (_panel is null)
        {
            WpfHost.EnsureStarted();
            _panel = new ControlPanelWindow(this);
            // The WinForms message loop pumps this WPF window; route its keyboard input (Tab, arrows,
            // access keys) through WPF so it is fully keyboard-usable.
            ElementHost.EnableModelessKeyboardInterop(_panel);
        }
        _panel.Open(section);
    }

    // First launch after an update: show the new version's "What's new" notes once. Compares
    // the installed version against the last one we recorded and stores the current version so
    // it pops only once. Skipped for dev builds and for a brand-new install (no prior version).
    void MaybeShowWhatsNew()
    {
        if (!_updates.IsVelopackInstalled) return; // dev build out of bin\ — don't pop
        string current = _updates.CurrentVersionText;
        string lastSeen = _settings.Live.LastSeenVersion;
        if (string.Equals(lastSeen, current, StringComparison.OrdinalIgnoreCase)) return;

        _settings.Change(s => s.LastSeenVersion = current); // record now, so it pops only once

        if (string.IsNullOrEmpty(lastSeen)) return; // nothing recorded yet (first run): just remember, don't pop

        // We're still in the ctor (before Application.Run); defer until the message loop pumps.
        _marshal.BeginInvoke(() => OpenPanel(PanelSection.WhatsNew));
    }

    // The app was called WormsCursor before. Its settings need no step here (SettingsStore.Load reads
    // the old file while there is no new one), and neither do its hooks (the startup refresh replaces
    // them, as outdated connections); only the Start with Windows entry moves. Best effort: a failure
    // is logged and the app starts without it.
    void MigrateAutostartFromWormsCursor()
    {
        try
        {
            if (_autostart.MigrateLegacy()) AppLog.Write("moved WormsCursor's Start with Windows entry over");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            AppLog.Write("couldn't move WormsCursor's Start with Windows entry: " + ex.Message);
        }
    }

    // Every settings save goes through here (LiveSettings calls it after each change). Persisting is
    // best-effort: a read-only or locked settings.json must never crash the tray (some saves run in the
    // constructor, before the message loop, where a throw would end the app with no tray icon). The
    // in-memory settings stay live for this run; the failure is logged so it can be diagnosed.
    static void Persist(CursorSettings settings)
    {
        try { SettingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("couldn't save settings: " + ex.Message);
        }
    }

    void OnExit(object? sender, EventArgs e)
    {
        StopEverything();
        ExitThread();
    }

    // Takes down everything visible and every background listener: the panel, the tray icon, the
    // pipe, the sweep and the overlay. Used on Exit and before restarting into an update.
    void StopEverything()
    {
        _panel?.Shutdown();
        _panel = null;
        _tray.Visible = false;
        _agentSweep?.Dispose();
        _agentPipe?.Dispose();
        _overlay.Dispose();
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
