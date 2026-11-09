using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AgentsCursor.Core;
using Wpf.Ui.Appearance;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// A donut of each agent's share, clockwise from the top in the agents' fixed order, each in its own
/// colour (<see cref="ChartColors.Agent"/>) with a 2 DIP gap between neighbours. Hovering a segment
/// lifts it and shows "Codex: 52 (24%)". With nothing to show it is an empty ring. The page puts the
/// total in the hole and the legend beside it; the legend is what screen readers read, so the ring
/// itself only names its summary.
/// </summary>
public sealed class AgentDonut : FrameworkElement
{
    const double Thickness = 16, Lift = 3, GapDip = 2;

    IReadOnlyList<AgentCount> _agents = Array.Empty<AgentCount>();
    IReadOnlyList<int> _percents = Array.Empty<int>();
    int _total;
    int? _hover;
    readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse };

    public AgentDonut()
    {
        Loaded += (_, _) => ApplicationThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            _tip.IsOpen = false;
        };
    }

    void OnThemeChanged(ApplicationTheme theme, Color accent) => InvalidateVisual();

    public void Show(IReadOnlyList<AgentCount> agents, IReadOnlyList<int> percents)
    {
        _agents = agents;
        _percents = percents;
        _total = agents.Sum(a => a.Count);
        _hover = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    double Outer => Math.Min(RenderSize.Width, RenderSize.Height) / 2 - Lift;
    Point Center => new(RenderSize.Width / 2, RenderSize.Height / 2);

    protected override void OnRender(DrawingContext dc)
    {
        double outer = Outer;
        if (outer <= Thickness) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hover anywhere on it
        double mid = outer - Thickness / 2;
        if (_total == 0 || _agents.Count(a => a.Count > 0) == 1)
        {
            var only = _agents.FirstOrDefault(a => a.Count > 0);
            var brush = _total == 0 ? ChartColors.Track : ChartColors.Agent(only.Tool);
            double thick = _total > 0 && _hover is not null ? Thickness + Lift : Thickness;
            dc.DrawEllipse(null, new Pen(brush, thick), Center, mid + (thick - Thickness) / 2, mid + (thick - Thickness) / 2);
            return;
        }
        double gap = GapDip / mid; // radians
        double angle = 0;
        for (int i = 0; i < _agents.Count; i++)
        {
            double sweep = 2 * Math.PI * _agents[i].Count / _total;
            if (sweep > 0)
            {
                double extra = _hover == i ? Lift : 0;
                double from = angle + gap / 2, to = angle + sweep - gap / 2;
                if (to > from)
                    dc.DrawGeometry(ChartColors.Agent(_agents[i].Tool), null,
                        Segment(Center, outer + extra, outer - Thickness, from, to));
            }
            angle += sweep;
        }
    }

    // A ring segment between two angles (radians, clockwise from 12 o'clock).
    static Geometry Segment(Point c, double outer, double inner, double from, double to)
    {
        Point At(double r, double a) => new(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        bool large = to - from > Math.PI;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(outer, from), true, true);
            g.ArcTo(At(outer, to), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            g.LineTo(At(inner, to), true, true);
            g.ArcTo(At(inner, from), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    int? SegmentAt(Point p)
    {
        if (_total == 0) return null;
        var c = Center;
        double dx = p.X - c.X, dy = p.Y - c.Y, r = Math.Sqrt(dx * dx + dy * dy);
        if (r < Outer - Thickness - 2 || r > Outer + Lift + 2) return null;
        double a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        double angle = 0;
        for (int i = 0; i < _agents.Count; i++)
        {
            angle += 2 * Math.PI * _agents[i].Count / _total;
            if (a < angle && _agents[i].Count > 0) return i;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = SegmentAt(e.GetPosition(this));
        if (hit == _hover) return;
        _hover = hit;
        _tip.IsOpen = false;
        if (hit is int i)
        {
            var agent = _agents[i];
            _tip.Content = $"{IconCatalog.AgentName(agent.Tool)}: {agent.Count:N0} ({_percents.ElementAtOrDefault(i)}%)";
            _tip.PlacementTarget = this;
            _tip.IsOpen = true;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed class Peer(AgentDonut owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(AgentDonut);
        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            if (owner._total == 0) return name;
            var parts = owner._agents.Where(a => a.Count > 0).Select(a => $"{IconCatalog.AgentName(a.Tool)} {a.Count:N0}");
            return $"{name}: {string.Join(", ", parts)}";
        }
    }
}
