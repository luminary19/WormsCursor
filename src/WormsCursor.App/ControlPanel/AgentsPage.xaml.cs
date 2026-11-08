using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WormsCursor.App.Services;
using WormsCursor.Core;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// The agent tools WormsCursor listens to: one card per tool with its hook registration status and
/// Register / Update / Unregister, how long a finished notice lingers, and an on-screen preview that
/// shows pretend waiting agents on the real token. Registration edits the tool's own config (backed
/// up first); the status is re-read whenever the page is shown and after every action.
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
        bool enabled = _host.Settings.Live.AgentNotifierEnabled;
        PreviewButton.IsEnabled = enabled;
        PreviewCountBox.IsEnabled = enabled;
        PreviewButton.Content = _previewing ? "Stop" : "Show";
        PreviewButton.Icon = new Wpf.Ui.Controls.SymbolIcon(_previewing
            ? Wpf.Ui.Controls.SymbolRegular.Stop24 : Wpf.Ui.Controls.SymbolRegular.Play24);
        PreviewHeader.Description = enabled
            ? "Show pretend waiting agents on the real token, to check how it looks and where it sits."
            : "Turn on Show the token, on the Token page, to preview it.";
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
        if (((FrameworkElement)sender).DataContext is ToolRow row)
            Act(row, row.PrimaryRegisters ? AgentHookRegistrar.Register : AgentHookRegistrar.Unregister);
    }

    void OnUnregister(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ToolRow row) Act(row, AgentHookRegistrar.Unregister);
    }

    // Runs a registration action; a failure (a malformed or locked config) shows in the error bar.
    void Act(ToolRow row, Action<string> action)
    {
        ErrorBar.IsOpen = false;
        try { action(row.Tool.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorBar.Title = $"Couldn't change {row.Name}";
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        row.Refresh();
    }
}
