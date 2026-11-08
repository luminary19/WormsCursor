using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentsCursor.Core;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AgentsCursor.App.ControlPanel;

/// <summary>The control panel's sidebar sections.</summary>
public enum PanelSection { Token, Icons, Agents, Apps, About, WhatsNew }

/// <summary>
/// The one control panel window: a Windows 11 Settings-style sidebar (Token, Icons, Agents, Apps, About).
/// Every change applies the moment it is made; there is no OK / Cancel.
///
/// The tray creates it once and keeps it for the app's lifetime: closing only hides it, so it keeps
/// following the Windows theme (when <see cref="CursorSettings.Theme"/> says to) and reopens where it
/// was. <see cref="Shutdown"/> closes it for real when the app exits.
/// </summary>
public partial class ControlPanelWindow : FluentWindow
{
    readonly IControlPanelHost _host;
    readonly AgentsPage _agents;
    readonly AboutPage _about;
    bool _shuttingDown;
    const double WidePaneMinWidth = 860; // DIPs; narrower and the sidebar shows icons only
    PanelSection _firstSection = PanelSection.Token; // shown when the window first loads
    ThemePreference? _theme;      // the preference last applied
    bool _followingSystem;        // SystemThemeWatcher is watching this window

    public ControlPanelWindow(IControlPanelHost host)
    {
        _host = host;
        ApplyTheme(host.Settings.Live.Theme);
        InitializeComponent();

        var icon = PanelAssets.AppIcon;
        if (icon is not null)
        {
            Icon = icon;
            TitleIcon.Source = icon;
        }

        _agents = new AgentsPage(host);
        _about = new AboutPage(host);
        Navigation.SetPageProviderService(new PageProvider(new Dictionary<Type, object>
        {
            [typeof(TokenPage)] = new TokenPage(host.Settings),
            [typeof(IconsPage)] = new IconsPage(host.Settings),
            [typeof(AgentsPage)] = _agents,
            [typeof(AppsPage)] = new AppsPage(host.Settings),
            [typeof(AboutPage)] = _about,
        }));

        Loaded += (_, _) => Navigate(_firstSection);
        SizeChanged += (_, e) => FitPane(e.NewSize.Width);
        Navigation.PaneOpened += (_, _) => ShowPaneLabels(true);
        Navigation.PaneClosed += (_, _) => ShowPaneLabels(false);
        Navigation.Navigated += (_, e) => Dispatcher.BeginInvoke(() => ScrollToTop(e.Page), System.Windows.Threading.DispatcherPriority.Loaded);
        host.Settings.Changed += OnSettingsChanged;
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
        _host.Settings.Changed -= OnSettingsChanged;
        Close();
    }

    void OnSettingsChanged() => ApplyTheme(_host.Settings.Live.Theme);

    // System: WPF-UI's watcher applies Windows' light/dark now and again whenever Windows switches.
    // Light / Dark: the watcher lets go and the chosen theme stays. Either way the theme goes through
    // ApplicationThemeManager, whose Changed event re-derives the opaque text brushes (WpfHost), and the
    // window's Mica backdrop is re-applied for the new mode.
    void ApplyTheme(ThemePreference preference)
    {
        if (preference == _theme) return;
        _theme = preference;

        bool follow = preference == ThemePreference.System;
        if (follow && !_followingSystem) SystemThemeWatcher.Watch(this);
        else if (!follow && _followingSystem) SystemThemeWatcher.UnWatch(this);
        _followingSystem = follow;

        if (follow) ApplicationThemeManager.ApplySystemTheme();
        else ApplicationThemeManager.Apply(preference == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        WindowBackgroundManager.UpdateBackground(this, ApplicationThemeManager.GetAppTheme(), WindowBackdropType.Mica);
    }

    void Navigate(PanelSection section)
    {
        Type page = section switch
        {
            PanelSection.Icons => typeof(IconsPage),
            PanelSection.Agents => typeof(AgentsPage),
            PanelSection.Apps => typeof(AppsPage),
            PanelSection.About or PanelSection.WhatsNew => typeof(AboutPage),
            _ => typeof(TokenPage),
        };
        Navigation.Navigate(page);
        if (section == PanelSection.WhatsNew) _about.ShowWhatsNew();
    }

    // Like Windows Settings: on a narrow window the sidebar folds to icons so the page keeps its room,
    // and the menu button there unfolds it again.
    void FitPane(double width)
    {
        bool wide = width >= WidePaneMinWidth;
        Navigation.IsPaneToggleVisible = !wide;
        if (Navigation.IsPaneOpen != wide) Navigation.SetCurrentValue(NavigationView.IsPaneOpenProperty, wide);
        ShowPaneLabels(Navigation.IsPaneOpen);
    }

    // Folded to icons, each section names itself in a tooltip.
    void ShowPaneLabels(bool open)
    {
        foreach (var item in Navigation.MenuItems.Cast<object>().Concat(Navigation.FooterMenuItems.Cast<object>()).OfType<NavigationViewItem>())
            item.ToolTip = open ? null : item.Content;
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

    /// <summary>Hands the NavigationView the pages this window built (they need the host, so
    /// NavigationView can't create them itself).</summary>
    sealed class PageProvider(IReadOnlyDictionary<Type, object> pages) : INavigationViewPageProvider
    {
        public object? GetPage(Type pageType) => pages.GetValueOrDefault(pageType);
    }
}
