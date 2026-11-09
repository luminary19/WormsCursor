using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AgentsCursor.Core;
using Wpf.Ui.Appearance;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// A GitHub-style activity calendar: one column per week (the current one last), one row per weekday
/// (the culture's first day on top), each day a rounded square in one of five greens by how many
/// times agents needed you that day (<see cref="ActivityStats.HeatLevel"/>, relative to the days
/// shown). Month names run along the top, Mon / Wed / Fri down the side, "Less … More" underneath.
///
/// It fills its width with GitHub-sized squares: about ten months at the panel's usual width, a year
/// on a wide window, never less than half a year, with every square snapped to whole device pixels so
/// it stays crisp at any scale. Hovering a day shows "Needed you 12 times on Sat, Nov 8"; with keyboard
/// focus the arrow keys move between days (left and right by week, up and down by day, Home and End)
/// and screen readers hear the same line.
/// </summary>
public sealed class ActivityHeatmap : FrameworkElement
{
    const int MinWeeks = 26, MaxWeeks = 53;
    const double TargetPitch = 13;   // DIPs per week column, before fitting to the width (GitHub's 10 + 3)
    const double LabelColumn = 34;   // the weekday names
    const double MonthRow = 22;      // the month names
    const double GapDip = 3;
    const double LegendGap = 12, LegendCell = 11, LegendRow = 14;
    const double FontSize = 12;

    IReadOnlyDictionary<DateOnly, int> _daily = new Dictionary<DateOnly, int>();
    DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    readonly DayOfWeek _firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    readonly ToolTip _tip;

    // Layout, in DIPs that land on whole device pixels.
    int _weeks = MinWeeks;
    double _pitch = 16, _gap = GapDip, _left = LabelColumn, _top = MonthRow;
    DateOnly _start;
    DateOnly? _hover, _focus;

    public ActivityHeatmap()
    {
        Focusable = true;
        FocusVisualStyle = null; // the focused day draws its own ring
        SnapsToDevicePixels = true;
        _tip = new ToolTip { Placement = PlacementMode.Top, PlacementTarget = this, VerticalOffset = -4 };
        _start = ActivityStats.GridStart(_today, _weeks, _firstDay);
        Loaded += (_, _) => ApplicationThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            _tip.IsOpen = false;
        };
    }

    void OnThemeChanged(ApplicationTheme theme, Color accent) => InvalidateVisual();

    /// <summary>Shows these day counts, with <paramref name="today"/> in the last column.</summary>
    public void Show(IReadOnlyDictionary<DateOnly, int> daily, DateOnly today)
    {
        _daily = daily;
        if (today != _today)
        {
            _today = today;
            _focus = null;
            InvalidateMeasure();
        }
        _start = ActivityStats.GridStart(_today, _weeks, _firstDay);
        InvalidateVisual();
        if (_focus is { } day) ShowTip(day);
        AnnounceName();
    }

    // ---------- layout ----------

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? LabelColumn + MinWeeks * TargetPitch : available.Width;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double grid = Math.Max(0, width - LabelColumn);
        _weeks = Math.Clamp((int)Math.Round(grid / TargetPitch), MinWeeks, MaxWeeks);
        // Whole device pixels for the pitch, the gap and the origin: every square is crisp.
        double pitchPx = Math.Max(4, Math.Floor(grid * scale / _weeks));
        double gapPx = Math.Max(2, Math.Round(GapDip * scale));
        _pitch = pitchPx / scale;
        _gap = gapPx / scale;
        _left = Math.Round(LabelColumn * scale) / scale;
        _top = Math.Round(MonthRow * scale) / scale;
        _start = ActivityStats.GridStart(_today, _weeks, _firstDay);
        return new Size(width, GridBottom + LegendGap + LegendRow);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
    }

    double Cell => _pitch - _gap;
    double GridBottom => _top + 7 * _pitch - _gap;

    Rect CellRect(DateOnly day)
    {
        int index = day.DayNumber - _start.DayNumber;
        return new Rect(_left + index / 7 * _pitch, _top + index % 7 * _pitch, Cell, Cell);
    }

    DateOnly? DayAt(Point p)
    {
        if (p.X < _left || p.Y < _top) return null;
        int column = (int)((p.X - _left) / _pitch), row = (int)((p.Y - _top) / _pitch);
        if (column >= _weeks || row >= 7) return null;
        var day = _start.AddDays(column * 7 + row);
        return day <= _today ? day : null;
    }

    // ---------- drawing ----------

    protected override void OnRender(DrawingContext dc)
    {
        var culture = CultureInfo.CurrentCulture;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var text = Resource("TextFillColorSecondaryBrush", Brushes.Gray);
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(string s) => new(s, culture, FlowDirection.LeftToRight, typeface, FontSize, text, scale);

        // Transparent behind everything, so the gaps take the mouse too and the tooltip doesn't flicker.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var thresholds = ActivityStats.HeatThresholds(VisibleDays().Select(d => _daily.GetValueOrDefault(d)));
        double radius = Math.Min(4, Cell * 0.2);
        foreach (var day in VisibleDays())
        {
            int level = ActivityStats.HeatLevel(_daily.GetValueOrDefault(day), thresholds);
            dc.DrawRoundedRectangle(ChartColors.Heat(level), null, CellRect(day), radius, radius);
        }

        // Hover and keyboard focus: a ring around the day (focus in the system focus colour).
        if (_hover is { } hovered)
            dc.DrawRoundedRectangle(null, new Pen(Resource("TextFillColorPrimaryBrush", Brushes.Black), 1.5),
                Inset(CellRect(hovered), 0.75), radius, radius);
        if (_focus is { } focused && IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Resource("FocusStrokeColorOuterBrush", Brushes.Black), 2),
                Inset(CellRect(focused), -2), radius + 2, radius + 2);

        // Weekday names: Mon, Wed and Fri, as GitHub does, in this culture's words.
        for (int row = 0; row < 7; row++)
        {
            var dow = (DayOfWeek)(((int)_firstDay + row) % 7);
            if (dow is not (DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday)) continue;
            var label = Label(culture.DateTimeFormat.AbbreviatedDayNames[(int)dow]);
            dc.DrawText(label, new Point(0, _top + row * _pitch + (Cell - label.Height) / 2));
        }

        // Month names over the week a month starts in, never overlapping.
        double end = double.NegativeInfinity;
        foreach (var (column, month) in MonthLabels())
        {
            double x = _left + column * _pitch;
            if (x < end + 6) continue;
            var label = Label(culture.DateTimeFormat.AbbreviatedMonthNames[month - 1]);
            dc.DrawText(label, new Point(x, (_top - _gap - label.Height) / 2 + 1));
            end = x + label.Width;
        }

        // "Less ■■■■■ More", right-aligned under the grid.
        var less = Label("Less");
        var more = Label("More");
        double cellPx = Math.Round(LegendCell * scale) / scale, step = cellPx + _gap;
        double y = Math.Round((GridBottom + LegendGap) * scale) / scale;
        double right = Math.Round((_left + _weeks * _pitch - _gap) * scale) / scale;
        double x0 = right - more.Width - 6 - 5 * step + _gap;
        dc.DrawText(more, new Point(right - more.Width, y + (cellPx - more.Height) / 2));
        for (int level = 0; level < 5; level++)
            dc.DrawRoundedRectangle(ChartColors.Heat(level), null, new Rect(x0 + level * step, y, cellPx, cellPx), 2.5, 2.5);
        dc.DrawText(less, new Point(x0 - 6 - less.Width, y + (cellPx - less.Height) / 2));
    }

    static Rect Inset(Rect r, double by) => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    IEnumerable<DateOnly> VisibleDays()
    {
        for (var day = _start; day <= _today; day = day.AddDays(1)) yield return day;
    }

    // The column of each month's 1st (up to today); the first column also names its month when no 1st
    // falls in it, unless the next label is too close.
    List<(int Column, int Month)> MonthLabels()
    {
        var labels = new List<(int Column, int Month)>();
        bool partialFirst = false;
        for (int column = 0; column < _weeks; column++)
        {
            var top = _start.AddDays(column * 7);
            int? month = null;
            for (int row = 0; row < 7; row++)
            {
                var day = top.AddDays(row);
                if (day.Day == 1 && day <= _today) month = day.Month;
            }
            if (month is null && column == 0) { month = top.Month; partialFirst = true; }
            if (month is { } m) labels.Add((column, m));
        }
        if (partialFirst && labels.Count > 1 && labels[1].Column < 3) labels.RemoveAt(0);
        return labels;
    }

    // ---------- hover, keyboard, screen readers ----------

    string DayText(DateOnly day)
    {
        int count = _daily.GetValueOrDefault(day);
        string date = ActivityStats.ShortDate(day, CultureInfo.CurrentCulture);
        return count switch
        {
            0 => $"No agent needed you on {date}",
            1 => $"Needed you once on {date}",
            _ => $"Needed you {count:N0} times on {date}",
        };
    }

    void ShowTip(DateOnly day)
    {
        _tip.Content = DayText(day);
        _tip.PlacementRectangle = CellRect(day);
        _tip.IsOpen = false; // re-open, so it moves to the new day
        _tip.IsOpen = true;
    }

    void HideTip()
    {
        if (_focus is { } day && IsKeyboardFocused) ShowTip(day);
        else _tip.IsOpen = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var day = DayAt(e.GetPosition(this));
        if (day == _hover) return;
        _hover = day;
        if (day is { } d) ShowTip(d);
        else HideTip();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        HideTip();
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _focus ??= _today;
        ShowTip(_focus.Value);
        InvalidateVisual();
        AnnounceName();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_hover is null) _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var from = _focus ?? _today;
        DateOnly? to = e.Key switch
        {
            Key.Left => from.AddDays(-7),
            Key.Right => from.AddDays(7),
            Key.Up => from.AddDays(-1),
            Key.Down => from.AddDays(1),
            Key.Home => _start,
            Key.End => _today,
            _ => null,
        };
        if (to is not { } day) return;
        e.Handled = true;
        if (day < _start) day = _start;
        if (day > _today) day = _today;
        _focus = day;
        ShowTip(day);
        InvalidateVisual();
        AnnounceName();
    }

    string _announced = "";

    void AnnounceName()
    {
        string name = AccessibleName();
        if (name == _announced) return;
        string old = _announced;
        _announced = name;
        if (UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, name);
    }

    // The focused day, or a summary of what is shown.
    string AccessibleName()
    {
        if (_focus is { } day && IsKeyboardFocused) return DayText(day);
        int total = VisibleDays().Sum(d => _daily.GetValueOrDefault(d));
        return $"Activity: agents needed you {(total == 1 ? "once" : $"{total:N0} times")} in the last {_weeks} weeks";
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(ActivityHeatmap owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.AccessibleName();
        protected override string GetHelpTextCore() => "Use the arrow keys to move between days.";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override string GetLocalizedControlTypeCore() => "chart";
        protected override string GetClassNameCore() => nameof(ActivityHeatmap);
    }
}
