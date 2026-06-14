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
    // the UI thread so the low-level hook has a message loop. See SyncTypingHook.
    LowLevelKeyboardHook? _keyHook;

    // Agent notifier: tracks how many agents await the user (fed by the pipe), pushes the count
    // into the engine, and reflects it in the tray tooltip. The pipe server + sweep timer live
    // here (not in the engine), so they survive ApplySettings' engine stop/restart.
    readonly AgentActivity _agents = new();
    AgentPipeServer? _agentPipe;
    System.Windows.Forms.Timer? _agentSweep;

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
        menu.Items.Add("Agent notifications…", null, OnAgentHooks);
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
        SyncTypingHook();
        StartAgentNotifier();
        MaybeShowWhatsNew();
    }

    // Brings up the agent-notifier plumbing: the named-pipe server that `WormsCursor.exe hook`
    // invocations write to, a periodic sweep that evicts stale sessions, and the wiring that turns
    // a changed waiting count into an engine update + tray tooltip. Independent of the engine's
    // lifetime so it keeps listening across ApplySettings restarts.
    void StartAgentNotifier()
    {
        _agents.WaitingCountChanged += OnWaitingCountChanged;

        _agentPipe = new AgentPipeServer(msg =>
        {
            // The pipe fires on its own thread; hop to the UI thread so AgentActivity's events
            // (which touch the tray) stay single-threaded, like every other callback here.
            if (_marshal.IsHandleCreated)
                _marshal.BeginInvoke(new Action(() => HandleAgentEvent(msg)));
        });
        _agentPipe.Start();

        _agentSweep = new System.Windows.Forms.Timer { Interval = 60_000 };
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
        _engine.SetWaitingAgents(_agents.WaitingTools);
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
            using var dlg = new ChangelogForm(_updates, current);
            dlg.ShowDialog();
        }));
    }

    void OnToggleEnabled(object? sender, EventArgs e)
    {
        if (_engine.IsRunning) _engine.Stop();
        else _engine.Start();
        _enabledItem.Checked = _engine.IsRunning;
        SyncTypingHook();
    }

    // Installs the typing-hop keyboard hook only while I-beam feedback is ON and the engine is
    // running, and tears it down otherwise — so we don't hold a global hook when that feature is
    // off. Must run on the UI thread (it has the message loop). The hook exists solely to nudge
    // the I-beam; the mouse squash & pop polls and needs no hook.
    void SyncTypingHook()
    {
        bool want = _engine.IsRunning && _settings.IbeamFeedback;
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

    void OnAgentHooks(object? sender, EventArgs e) => OpenAgentHooks();

    /// <summary>Shows the "Agent notifications" settings panel (hook registration + charm display).
    /// Marshals onto the UI thread so it's safe to call from anywhere.</summary>
    public void OpenAgentHooks()
    {
        if (_marshal.InvokeRequired) { _marshal.BeginInvoke(OpenAgentHooks); return; }
        using var dlg = new AgentHooksForm(
            _settings.AgentNotifierEnabled, _settings.AgentNotifierCap, ApplyAgentDisplay, PreviewWaitingCount);
        dlg.ShowDialog();
    }

    // Drives the live cursor for the "Preview on cursor" test in the dialog: a non-null value fakes
    // that many waiting agents (cycling the supported tools so you see each logo); null ends the
    // preview and restores the real set (so a real agent event arriving mid-preview, or just closing
    // the dialog, leaves the genuine charms on screen).
    static readonly string[] PreviewTools = { "claude-code", "codex" };
    void PreviewWaitingCount(int? testCount)
    {
        if (testCount is not int n) { _engine.SetWaitingAgents(_agents.WaitingTools); return; }
        var tools = new string[Math.Max(0, n)];
        for (int i = 0; i < tools.Length; i++) tools[i] = PreviewTools[i % PreviewTools.Length];
        _engine.SetWaitingAgents(tools);
    }

    // The engine reads AgentNotifierEnabled / AgentNotifierCap live each tick (see ApplyCharms), so
    // a display change takes effect immediately — no engine restart needed, just persist it.
    void ApplyAgentDisplay(bool enabled, int cap)
    {
        _settings.AgentNotifierEnabled = enabled;
        _settings.AgentNotifierCap = Math.Clamp(cap, 1, 6);
        SettingsStore.Save(_settings);
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
        _engine.SetWaitingAgents(_agents.WaitingTools); // a fresh engine starts empty — re-push the live set
        SyncTypingHook(); // ClickFeedback / IbeamFeedback may have been toggled
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
        _agentSweep?.Dispose();
        _agentPipe?.Dispose();
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
            _agentSweep?.Dispose();
            _agentPipe?.Dispose();
            _keyHook?.Dispose();
            _tray.Dispose();
            _engine.Dispose();
            _appIcon.Dispose();
            _marshal.Dispose();
        }
        base.Dispose(disposing);
    }
}
