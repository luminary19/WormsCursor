using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AgentsCursor.Core;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// The panel's home page: how you and your agents have been getting on, from the local history
/// (<see cref="WaitHistory"/>). Three tiles (current streak, longest streak, average response time),
/// the activity calendar (<see cref="ActivityHeatmap"/>), and which agents needed you in the last 30
/// days (<see cref="AgentDonut"/> + a legend). The numbers are <see cref="ActivityStats"/>'s.
///
/// The history is read and summarised on a background task whenever the page is shown and whenever a
/// wait is recorded while it is; the controls keep their size until the numbers arrive, so nothing
/// jumps.
/// </summary>
public partial class AnalyticsPage : Page
{
    readonly IControlPanelHost _host;
    int _loads;       // the latest load; an older one finishing late is dropped
    bool _stale = true;
    ActivitySummary? _shown;

    public AnalyticsPage(IControlPanelHost host)
    {
        _host = host;
        InitializeComponent();
        _host.History.Recorded += () => Dispatcher.BeginInvoke(() => { _stale = true; ReloadIfShown(); });
        IsVisibleChanged += (_, _) => { _stale = true; ReloadIfShown(); };
        Loaded += (_, _) => ReloadIfShown();
        // The legend's swatches and logos are drawn for one theme and one scale.
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += (_, _) => { if (_shown is not null) Show(_shown); };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_shown is not null) Show(_shown);
    }

    void ReloadIfShown()
    {
        if (!IsVisible || !_stale) return;
        _stale = false;
        int load = ++_loads;
        var history = _host.History;
        Task.Run(() => ActivityStats.Summarize(history.Load(), DateTime.UtcNow, TimeZoneInfo.Local))
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (load != _loads) return;
                if (t.IsFaulted)
                {
                    AppLog.Write("couldn't read the activity history: " + t.Exception?.GetBaseException().Message);
                    return;
                }
                Show(t.Result);
            }), TaskScheduler.Default);
    }

    void Show(ActivitySummary summary)
    {
        _shown = summary;
        var culture = CultureInfo.CurrentCulture;

        ShowDays(CurrentTile, CurrentValue, CurrentDetail, "Current streak", summary.CurrentStreak,
            CurrentSince(summary.CurrentStreak, summary.Today, culture), culture);
        ShowDays(LongestTile, LongestValue, LongestDetail, "Longest streak", summary.LongestStreak,
            Span(summary.LongestStreak, summary.Today, culture), culture);
        string response = summary.AverageResponse is { } average ? ActivityStats.FormatDuration(average) : "—";
        ResponseValue.Inlines.Clear();
        // "3 m 10 s": the numbers large, the units small, as on the streak tiles.
        foreach (var (part, i) in response.Split(' ').Select((p, i) => (p, i)))
            ResponseValue.Inlines.Add(part.Any(char.IsDigit) || part == "—" ? Number((i > 0 ? " " : "") + part) : Unit(" " + part));
        AutomationProperties.SetName(ResponseTile, $"Average response: {(summary.AverageResponse is null ? "none yet" : response)}, {ResponseDetail.Text}");

        Heatmap.Show(summary.Daily, summary.Today);

        var agents = summary.RecentAgents;
        var percents = ActivityStats.Percentages(agents.Select(a => a.Count).ToArray());
        Donut.Show(agents, percents);
        TotalText.Text = summary.RecentTotal.ToString("N0", culture);
        BuildLegend(agents, percents);
        bool any = agents.Count > 0;
        Legend.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = summary.IsEmpty
            ? "Nothing yet. Agents show up here once one needs you."
            : "No agent needed you in the last 30 days.";
    }

    // A streak tile: the number large, its unit small ("12 days"), and its dates.
    static void ShowDays(FrameworkElement tile, TextBlock value, TextBlock detail, string label, Streak streak, string dates, CultureInfo culture)
    {
        string number = streak.Days.ToString("N0", culture), unit = streak.Days == 1 ? "day" : "days";
        value.Inlines.Clear();
        value.Inlines.Add(Number(number));
        value.Inlines.Add(Unit(" " + unit));
        detail.Text = dates;
        AutomationProperties.SetName(tile, dates.Length == 0 ? $"{label}: {number} {unit}" : $"{label}: {number} {unit}, {dates}");
    }

    static Run Number(string text) => new(text) { FontSize = 28, FontWeight = FontWeights.SemiBold };

    static Run Unit(string text)
    {
        var run = new Run(text) { FontSize = 14, FontWeight = FontWeights.Normal };
        run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        return run;
    }

    static string CurrentSince(Streak streak, DateOnly today, CultureInfo culture)
    {
        if (streak.From is not { } from) return "";
        if (streak.Days == 1) return streak.To == today ? "Today" : "Yesterday";
        return "Since " + Date(from, today, culture);
    }

    static string Span(Streak streak, DateOnly today, CultureInfo culture)
    {
        if (streak.From is not { } from || streak.To is not { } to) return "";
        if (from == to) return Date(from, today, culture);
        return $"{Date(from, today, culture, withYear: false)} – {Date(to, today, culture)}";
    }

    // "Oct 29", with the year only when it isn't this one.
    static string Date(DateOnly day, DateOnly today, CultureInfo culture, bool withYear = true)
        => day.ToString(withYear && day.Year != today.Year ? "MMM d, yyyy" : "MMM d", culture);

    // One row per agent: its colour, logo and name, then the count and its share.
    void BuildLegend(IReadOnlyList<AgentCount> agents, IReadOnlyList<int> percents)
    {
        Legend.Children.Clear();
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var culture = CultureInfo.CurrentCulture;
        for (int i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            string name = IconCatalog.AgentName(agent.Tool);
            if (i > 0) Legend.Children.Add(new Border { Style = (Style)FindResource("RowDivider") });

            var row = new Grid { Height = 36 };
            foreach (var width in new[] { 10.0, 10, 20, 10 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            AutomationProperties.SetName(row, $"{name}: {agent.Count.ToString("N0", culture)}, {percents[i]} percent");

            var swatch = new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(3),
                Background = ChartColors.Agent(agent.Tool), VerticalAlignment = VerticalAlignment.Center,
            };
            var logo = new System.Windows.Controls.Image
            {
                Source = ToolLogo.Render(agent.Tool, 20, dpi), Width = 20, Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var count = new TextBlock
            {
                Text = agent.Count.ToString("N0", culture), VerticalAlignment = VerticalAlignment.Center,
                FontTypography = FontTypography.BodyStrong, Margin = new Thickness(12, 0, 0, 0),
            };
            var share = new TextBlock
            {
                Text = percents[i] + "%", VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right,
            };
            share.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            Grid.SetColumn(swatch, 0);
            Grid.SetColumn(logo, 2);
            Grid.SetColumn(label, 4);
            Grid.SetColumn(count, 5);
            Grid.SetColumn(share, 6);
            foreach (UIElement child in new UIElement[] { swatch, logo, label, count, share }) row.Children.Add(child);
            Legend.Children.Add(row);
        }
    }
}
