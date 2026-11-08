using System.Globalization;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Velopack;
using Wpf.Ui.Controls;
using AgentsCursor.App.Services;
using AgentsCursor.Core;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// Version, updates (check, download, restart into the new version), the release notes ("What's
/// new"), the panel's theme, Start with Windows, and links. Updating needs the installed app: a dev build out of bin\
/// says so instead of offering it.
/// </summary>
public partial class AboutPage : Page
{
    readonly IControlPanelHost _host;
    UpdateService Updates => _host.Updates;

    // The update card's next action, set by each state.
    Func<Task>? _updateAction;
    bool _notesRequested;

    public AboutPage(IControlPanelHost host)
    {
        _host = host;
        InitializeComponent();

        AppIcon.Source = PanelAssets.AppIcon;
        VersionText.Text = "Version " + Updates.CurrentVersionText;
        RepoLink.Click += (_, _) => Updates.OpenRepoPage();
        ReleasesLink.Click += (_, _) => Updates.OpenReleasesPage();
        ReleasesButton.Click += (_, _) => Updates.OpenReleasesPage();

        UpdateButton.Click += async (_, _) => { if (_updateAction is { } act) await act(); };
        ShowInitialUpdateState();

        WhatsNew.Expanded += async (_, _) => await LoadNotesAsync();
        NotesError.IsOpen = false;

        // The theme list is in ThemePreference order. The handler compares against the live value, so
        // showing the current choice (here and on each settings change) writes nothing.
        ThemeBox.SelectedIndex = (int)host.Settings.Live.Theme;
        ThemeBox.SelectionChanged += (_, _) =>
        {
            var theme = (ThemePreference)ThemeBox.SelectedIndex;
            if (ThemeBox.SelectedIndex >= 0 && theme != _host.Settings.Live.Theme) _host.Settings.Change(s => s.Theme = theme);
        };
        host.Settings.Changed += () => ThemeBox.SelectedIndex = (int)_host.Settings.Live.Theme;

        AutostartSwitch.Checked += (_, _) => SetAutostart(true);
        AutostartSwitch.Unchecked += (_, _) => SetAutostart(false);
        Loaded += (_, _) => AutostartSwitch.IsChecked = ReadAutostart(); // may have changed in Task Manager
    }

    /// <summary>Opens the release notes (after an update, or from "What's new").</summary>
    public void ShowWhatsNew()
    {
        WhatsNew.IsExpanded = true;
        WhatsNew.BringIntoView();
    }

    // ---------- updates ----------
    //
    // Every step below runs from a click, so a failure must land on the card, never escape: the click
    // handler is async void on a WPF dispatcher the WinForms loop pumps, where an unhandled exception
    // ends the whole tray. Velopack's own failures (ChecksumFailedException, AcquireLockFailedException,
    // NotInstalledException) derive straight from Exception, beside the HTTP and IO errors underneath,
    // and that set isn't fixed, so each step catches every failure, logs it, and shows it.

    void ShowInitialUpdateState()
    {
        if (!Updates.IsVelopackInstalled)
        {
            // A copy built from source can't update itself: the button stays, off, and says why on hover.
            SetUpdateState("", null, busy: false);
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.Content = "Check now";
            UpdateButton.ToolTip = "Updates install only in the released app. This copy was built from source.";
            ToolTipService.SetShowOnDisabled(UpdateButton, true);
            return;
        }
        if (Updates.PendingRestart is { } pending)
        {
            ShowReadyToRestart(pending);
            return;
        }
        SetUpdateState("", ("Check now", CheckAsync), busy: false);
    }

    async Task CheckAsync()
    {
        SetUpdateState("Checking for updates...", null, busy: true);
        try
        {
            var update = await Updates.CheckForUpdatesAsync();
            if (update is null)
                SetUpdateState("You're up to date.", ("Check again", CheckAsync), busy: false);
            else
                SetUpdateState($"Version {update.TargetFullRelease.Version} is available.",
                    ("Download and restart", () => DownloadAsync(update)), busy: false, accent: true);
        }
        catch (Exception ex)
        {
            AppLog.Write("update check failed: " + ex);
            SetUpdateState("Couldn't check for updates: " + ex.Message, ("Try again", CheckAsync), busy: false);
        }
    }

    async Task DownloadAsync(UpdateInfo update)
    {
        string version = update.TargetFullRelease.Version.ToString();
        SetUpdateState($"Downloading version {version}...", null, busy: true);
        try
        {
            await Updates.DownloadAsync(update, percent => Dispatcher.BeginInvoke(() =>
                UpdateHeader.Description = $"Downloading version {version}... {percent}%"));
        }
        catch (Exception ex)
        {
            AppLog.Write("update download failed: " + ex);
            SetUpdateState("Couldn't download the update: " + ex.Message, ("Try again", () => DownloadAsync(update)), busy: false);
            return;
        }
        Restart(update.TargetFullRelease);
    }

    void ShowReadyToRestart(VelopackAsset release) =>
        SetUpdateState($"Version {release.Version} is ready. Restart AgentsCursor to finish.",
            ("Restart now", () => { Restart(release); return Task.CompletedTask; }), busy: false, accent: true);

    // Exits into the updater; if it can't start, everything keeps running and the card says why.
    void Restart(VelopackAsset release)
    {
        try { _host.RestartToUpdate(release); }
        catch (Exception ex)
        {
            AppLog.Write("updater didn't start: " + ex);
            SetUpdateState($"Version {release.Version} is downloaded, but the updater didn't start: {ex.Message}",
                ("Try again", () => { Restart(release); return Task.CompletedTask; }), busy: false, accent: true);
        }
    }

    void SetUpdateState(string description, (string Label, Func<Task> Run)? action, bool busy, bool accent = false)
    {
        UpdateHeader.Description = description;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _updateAction = action?.Run;
        UpdateButton.Visibility = action is null && !busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = action is not null;
        UpdateButton.Content = action?.Label ?? "Working...";
        UpdateButton.Appearance = accent ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // ---------- what's new ----------

    async Task LoadNotesAsync()
    {
        if (_notesRequested) return;
        _notesRequested = true;
        NotesLoading.Visibility = Visibility.Visible;
        NotesError.IsOpen = false;
        try
        {
            var notes = await Updates.FetchReleaseNotesAsync();
            NotesPanel.Children.Clear();
            if (notes.Count == 0) NotesPanel.Children.Add(Secondary("No releases yet."));
            foreach (var note in notes) AddRelease(note);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                   or System.Text.Json.JsonException)
        {
            NotesError.Message = ex.Message;
            NotesError.IsOpen = true;
            _notesRequested = false; // let a later expand try again
        }
        finally { NotesLoading.Visibility = Visibility.Collapsed; }
    }

    void AddRelease(ReleaseNote note)
    {
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, NotesPanel.Children.Count == 0 ? 8 : 28, 0, 4) };
        heading.Children.Add(new TextBlock { Text = "Version " + note.Version, FontTypography = FontTypography.Subtitle });
        string meta = note.Published is { } p ? p.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : "";
        if (note.Prerelease) meta = meta.Length > 0 ? meta + " · Preview" : "Preview";
        if (meta.Length > 0)
        {
            var date = Secondary(meta);
            date.Margin = new Thickness(12, 0, 0, 3);
            date.VerticalAlignment = VerticalAlignment.Bottom;
            heading.Children.Add(date);
        }
        NotesPanel.Children.Add(heading);

        foreach (var block in ReleaseNotesFormat.Parse(note.Body))
            NotesPanel.Children.Add(BlockElement(block));
    }

    static FrameworkElement BlockElement(NoteBlock block)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var run in block.Runs) text.Inlines.Add(InlineFor(run));

        switch (block.Kind)
        {
            case NoteBlockKind.Heading:
                text.FontTypography = FontTypography.BodyStrong;
                text.Margin = new Thickness(0, 14, 0, 4);
                return text;
            case NoteBlockKind.Bullet:
                var bullet = new Grid { Margin = new Thickness(4, 4, 0, 4) };
                bullet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                bullet.ColumnDefinitions.Add(new ColumnDefinition());
                var dot = Secondary("•");
                bullet.Children.Add(dot);
                Grid.SetColumn(text, 1);
                bullet.Children.Add(text);
                return bullet;
            default:
                text.Margin = new Thickness(0, 4, 0, 4);
                return text;
        }
    }

    static Inline InlineFor(NoteRun run)
    {
        var inline = new Run(run.Text);
        if (run.Code)
        {
            inline.FontFamily = CodeFont;
            inline.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        }
        return run.Bold ? new Bold(inline) : inline;
    }

    static readonly System.Windows.Media.FontFamily CodeFont = new("Cascadia Mono, Consolas");

    static TextBlock Secondary(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    // ---------- start with Windows ----------

    // Commits straight to the registry. On failure the switch snaps back to the real state (the
    // handler then sees nothing to do) and the error bar says why.
    void SetAutostart(bool on)
    {
        if (on == ReadAutostart()) return;
        AutostartError.IsOpen = false;
        try
        {
            if (on) _host.Autostart.Enable();
            else _host.Autostart.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AutostartError.Message = ex.Message;
            AutostartError.IsOpen = true;
            AutostartSwitch.IsChecked = ReadAutostart();
        }
    }

    // Best-effort: a registry hiccup shows the switch off rather than breaking the page.
    bool ReadAutostart()
    {
        try { return _host.Autostart.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { return false; }
    }
}
