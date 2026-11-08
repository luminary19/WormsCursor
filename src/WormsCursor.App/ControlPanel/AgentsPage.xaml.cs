using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The agent tools WormsCursor listens to: one card per tool with its connection status and Connect /
/// Update / Disconnect (or, for a config it can't read, Show file), how long the token stays after an
/// agent is done, and an on-screen preview that shows pretend waiting agents on the real token.
/// Connecting edits the tool's own config (backed up first); the status is re-read whenever the page is
/// shown and after every action.
/// </summary>
public partial class AgentsPage : Page
{
    const int MaxPreviewAgents = 9;
    const int DefaultPreviewAgents = 2;

    readonly IControlPanelHost _host;
    ToolRow[] _rows = Array.Empty<ToolRow>();
    double _logoDpi;
    bool _previewing;

    public AgentsPage(IControlPanelHost host)
    {
        _host = host;
        InitializeComponent();

        TimeoutBox.Minimum = CursorSettings.MinTimeoutSeconds;
        TimeoutBox.Maximum = CursorSettings.MaxTimeoutSeconds;
        TimeoutBox.ValueChanged += (_, _) =>
        {
            if (TimeoutBox.Value is not double value) return;
            int seconds = (int)Math.Round(value);
            if (seconds != _host.Settings.Live.AgentNotifierTimeoutSeconds)
                _host.Settings.Change(s => s.AgentNotifierTimeoutSeconds = seconds);
        };

        PreviewCountBox.Minimum = 1;
        PreviewCountBox.Maximum = MaxPreviewAgents;
        PreviewCountBox.Value = DefaultPreviewAgents;
        PreviewCountBox.ValueChanged += (_, _) =>
        {
            PreviewCountLabel.Text = PreviewCount() == 1 ? "agent" : "agents";
            if (_previewing) StartPreview(); // show the new count straight away
        };
        PreviewButton.Click += (_, _) => { if (_previewing) EndPreview(); else StartPreview(); };
        TurnOnButton.Click += (_, _) => _host.Settings.Change(s => s.AgentNotifierEnabled = true);

        Loaded += (_, _) => ShowTools();
        _host.Settings.Changed += Refresh;
        Refresh();
    }

    // Moved to a monitor with another scale: redraw the logos so they stay crisp.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) ShowTools();
    }

    /// <summary>Takes the pretend agents off the token, if they're showing.</summary>
    public void EndPreview()
    {
        if (!_previewing) return;
        _previewing = false;
        _host.Preview(null);
        RefreshPreview();
    }

    void StartPreview()
    {
        _previewing = true;
        _host.Preview(PreviewCount());
        RefreshPreview();
    }

    int PreviewCount() => (int)Math.Clamp(Math.Round(PreviewCountBox.Value ?? DefaultPreviewAgents), 1, MaxPreviewAgents);

    void Refresh()
    {
        var s = _host.Settings.Current;
        if (TimeoutBox.Value != s.AgentNotifierTimeoutSeconds) TimeoutBox.Value = s.AgentNotifierTimeoutSeconds;
        if (!s.AgentNotifierEnabled) EndPreview(); // nothing to preview on a token that's off
        RefreshPreview();
    }

    void RefreshPreview()
    {
        // With the token off there is nothing to preview: the card offers to turn it on instead.
        bool enabled = _host.Settings.Live.AgentNotifierEnabled;
        PreviewControls.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TurnOnButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        PreviewButton.Content = _previewing ? "Stop" : "Show";
        PreviewButton.Icon = new Wpf.Ui.Controls.SymbolIcon(_previewing
            ? Wpf.Ui.Controls.SymbolRegular.Stop24 : Wpf.Ui.Controls.SymbolRegular.Play24);
        PreviewHeader.Description = enabled
            ? "Show pretend waiting agents on the real token, to check how it looks and where it sits."
            : "The token is off. Turn it on to preview it.";
    }

    // (Re)builds the tool cards at this screen's DPI, and re-reads every tool's registration.
    void ShowTools()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_rows.Length == 0 || dpi != _logoDpi)
        {
            _logoDpi = dpi;
            _rows = AgentHookRegistrar.Tools
                .Select(tool => new ToolRow(tool, ToolLogo.Render(tool.Id, 28, dpi), AgentHookRegistrar.ConfigPath(tool.Id)))
                .ToArray();
            ToolList.ItemsSource = _rows;
        }
        foreach (var row in _rows) row.Refresh();
    }

    void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ToolRow row) return;
        switch (row.PrimaryAction)
        {
            case ToolAction.Connect: Act(row, AgentHookRegistrar.Register); break;
            case ToolAction.Disconnect: Act(row, AgentHookRegistrar.Unregister); break;
            case ToolAction.ShowFile: ShowFile(row); break;
        }
    }

    void OnDisconnect(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ToolRow row) Act(row, AgentHookRegistrar.Unregister);
    }

    // Runs a connect/disconnect; a failure (a malformed or locked config) shows in the error bar.
    void Act(ToolRow row, Action<string> action)
    {
        ErrorBar.IsOpen = false;
        try { action(row.Tool.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError($"Couldn't change {row.Name}", ex.Message);
        }
        row.Refresh();
    }

    // Opens the config's folder in File Explorer with the file selected, so it can be fixed by hand.
    void ShowFile(ToolRow row)
    {
        ErrorBar.IsOpen = false;
        try
        {
            // A Windows path can't contain a quote, so quoting it is enough.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.ConfigPath}\""))?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Couldn't show {row.Name}'s config", ex.Message);
        }
    }

    void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
