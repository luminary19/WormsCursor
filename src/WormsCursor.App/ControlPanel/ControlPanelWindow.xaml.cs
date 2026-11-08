using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WormsCursor.App.ControlPanel;

/// <summary>The control panel's sidebar sections.</summary>
public enum PanelSection { Token, Agents, Apps, About, WhatsNew }

/// <summary>
/// The one control panel window: a Windows 11 Settings-style sidebar (Token, Agents, Apps, About)
/// with an at-a-glance status at the top. Every change applies the moment it is made; there is no
/// OK / Cancel.
///
/// The tray creates it once and keeps it for the app's lifetime: closing only hides it, so it keeps
/// following the Windows theme (WPF-UI's <see cref="SystemThemeWatcher"/> listens on this window) and
/// reopens where it was. <see cref="Shutdown"/> closes it for real when the app exits.
/// </summary>
public partial class ControlPanelWindow : FluentWindow
{
    readonly IControlPanelHost _host;
    readonly AgentsPage _agents;
    readonly AboutPage _about;
    bool _shuttingDown;
    const double WidePaneMinWidth = 960; // DIPs; narrower and the sidebar shows icons only
    PanelSection _firstSection = PanelSection.Token; // shown when the window first loads

    public ControlPanelWindow(IControlPanelHost host)
    {
        _host = host;
        SystemThemeWatcher.Watch(this); // light/dark + accent follow Windows, with Mica
        InitializeComponent();

        var icon = PanelAssets.AppIcon;
        if (icon is not null)
        {
            Icon = icon;
            AppIcon.Source = icon;
            TitleIcon.Source = icon;
        }

        _agents = new AgentsPage(host);
        _about = new AboutPage(host);
        Navigation.SetPageProviderService(new PageProvider(new Dictionary<Type, object>
        {
            [typeof(TokenPage)] = new TokenPage(host.Settings),
            [typeof(AgentsPage)] = _agents,
            [typeof(AppsPage)] = new AppsPage(host.Settings),
            [typeof(AboutPage)] = _about,
        }));

        Loaded += (_, _) => Navigate(_firstSection);
        SizeChanged += (_, e) => FitPane(e.NewSize.Width);
        Navigation.Navigated += (_, e) => Dispatcher.BeginInvoke(() => ScrollToTop(e.Page), System.Windows.Threading.DispatcherPriority.Loaded);
        host.StatusChanged += ShowStatus;
        ShowStatus();
        // A hidden panel leaves nothing pretend on screen.
        IsVisibleChanged += (_, _) => { if (!IsVisible) _agents.EndPreview(); };
    }

    /// <summary>Shows the panel, or brings it to the front, on <paramref name="section"/> — or, with
    /// null, where it was left (the Token page the first time).</summary>
    public void Open(PanelSection? section = null)
    {
        if (section is { } target)
        {
            if (IsLoaded) Navigate(target);
            else _firstSection = target;
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closes the window for good (the app is exiting).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        _host.StatusChanged -= ShowStatus;
        Close();
    }

    void Navigate(PanelSection section)
    {
        Type page = section switch
        {
            PanelSection.Agents => typeof(AgentsPage),
            PanelSection.Apps => typeof(AppsPage),
            PanelSection.About or PanelSection.WhatsNew => typeof(AboutPage),
            _ => typeof(TokenPage),
        };
        Navigation.Navigate(page);
        if (section == PanelSection.WhatsNew) _about.ShowWhatsNew();
    }

    // Like Windows Settings: on a narrow window the sidebar folds to icons so the page keeps its room.
    void FitPane(double width)
    {
        bool open = width >= WidePaneMinWidth;
        if (Navigation.IsPaneOpen != open) Navigation.SetCurrentValue(NavigationView.IsPaneOpenProperty, open);
        PaneHeaderPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // Pages share the NavigationView's scroller; each one opens at its top.
    static void ScrollToTop(object page)
    {
        for (var node = page as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller) { scroller.ScrollToTop(); return; }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_shuttingDown) return;
        e.Cancel = true; // the panel lives as long as the tray: closing just hides it
        Hide();
    }

    void ShowStatus()
    {
        var status = _host.Status;
        StatusText.Text = status.Text;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, status.Tone switch
        {
            StatusTone.Attention => "SystemFillColorCautionBrush",
            StatusTone.Idle => "SystemFillColorSuccessBrush",
            StatusTone.Paused => "SystemFillColorNeutralBrush",
            _ => "TextFillColorTertiaryBrush",
        });
    }

    /// <summary>Hands the NavigationView the pages this window built (they need the host, so
    /// NavigationView can't create them itself).</summary>
    sealed class PageProvider(IReadOnlyDictionary<Type, object> pages) : INavigationViewPageProvider
    {
        public object? GetPage(Type pageType) => pages.GetValueOrDefault(pageType);
    }
}
