using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentsCursor.Core;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// What each agent looks like on the token: a preview of the chosen icon in both states ("Done", and
/// "Needs you" with the "?" and a "+1"), each over dark and over light, a row of agents to pick from,
/// and that agent's gallery: its original logo, its mascots, the SVGs you added (each removable, with
/// Undo), and "Add SVG…". Choosing applies at once through <see cref="LiveSettings.Change"/>. An added
/// file is sanitised, test-drawn and stored by <see cref="CustomIconStore"/> on a background task; if
/// it can't be used, the reason shows right under the tiles and the agent keeps its icon.
///
/// The tiles are radio groups: Tab reaches the selected one, the arrow keys move the choice. The
/// thumbnails are drawn by the panel's own renderer on the UI thread (<see cref="ToolLogo"/>).
///
/// "Animate mascots" (<see cref="CursorSettings.AnimateMascots"/>) plays the mascots' idle loops in
/// the previews and the gallery tiles, and on the token; it shows off, disabled, while the token is
/// still anyway (<see cref="MascotLoops.Animate"/>). The tiles step through their frames from
/// <see cref="CompositionTarget.Rendering"/> only while the page is visible and a mascot is shown,
/// each frame drawn once by the page's own renderer (UI thread only) and kept by its tile.
/// </summary>
public partial class CustomizationPage : Page
{
    readonly LiveSettings _settings;
    string _tool = IconCatalog.Tools[0];
    AgentTile[] _agents = Array.Empty<AgentTile>();
    IconTile[] _icons = Array.Empty<IconTile>();
    IReadOnlyDictionary<string, string>? _shownChoices; // the choices the tiles reflect
    double _dpi;
    bool _uploading;          // an added file is being checked and stored
    RemovedIcon? _removed;    // the icon just removed, while its Undo is offered
    NotifierRenderer? _tileRenderer;            // draws the tiles' mascot frames, on the UI thread
    readonly Stopwatch _tileClock = new();      // the tiles' loop clock, running while they animate
    bool _tilesAnimating;
    const double MinAgentTileWidth = 150; // DIPs; narrower and the agents go two to a row

    public CustomizationPage(LiveSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        // The limit note under the tiles gives way to the status bar while it is open, however it closes.
        DependencyPropertyDescriptor.FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .AddValueChanged(StatusBar, (_, _) =>
            {
                ShowHint();
                if (!StatusBar.IsOpen) UndoButton.Visibility = Visibility.Collapsed;
            });
        Loaded += (_, _) => { _tileRenderer ??= new NotifierRenderer(); Rebuild(); };
        Unloaded += (_, _) =>
        {
            AnimateTiles(false);
            _tileRenderer?.Dispose();
            _tileRenderer = null;
        };
        IsVisibleChanged += (_, _) => UpdateTileAnimation();
        // Disabled while the token is still, so a change here is always the user's.
        AnimateSwitch.Checked += (_, _) => SetAnimate(true);
        AnimateSwitch.Unchecked += (_, _) => SetAnimate(false);
        // Windows' animation effects switched on or off: show what applies now.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Refresh();
        };
        _settings.Changed += Refresh;
        Refresh();
    }

    void SetAnimate(bool on)
    {
        if (AnimateSwitch.IsEnabled && on != _settings.Live.AnimateMascots) _settings.Change(s => s.AnimateMascots = on);
    }

    // ---------- the gallery's mascot loops ----------

    void UpdateTileAnimation()
    {
        bool on = IsVisible && _tileRenderer is not null
                  && MascotLoops.Animate(_settings.Live, SystemParameters.ClientAreaAnimation)
                  && _icons.Any(i => i.Loop is not null);
        AnimateTiles(on);
    }

    void AnimateTiles(bool on)
    {
        if (on == _tilesAnimating) return;
        _tilesAnimating = on;
        if (on)
        {
            _tileClock.Restart(); // each showing starts at rest
            CompositionTarget.Rendering += OnTileFrame;
            return;
        }
        CompositionTarget.Rendering -= OnTileFrame;
        _tileClock.Reset();
        foreach (var tile in _icons) tile.ShowRest();
    }

    void OnTileFrame(object? sender, EventArgs e)
    {
        if (_tileRenderer is not { } renderer) return;
        double clock = _tileClock.Elapsed.TotalSeconds;
        foreach (var tile in _icons)
        {
            if (tile.Loop is not { } loop) continue;
            // The middle of the frame's first sample, which FrameAt maps back to that frame.
            tile.ShowFrame(loop.FrameAt(clock), frame => ToolLogo.Render(renderer, _tool, tile.Id, 48, _dpi, (frame + 0.5) / loop.Fps));
        }
    }

    // Moved to a monitor with another scale: redraw the thumbnails so they stay crisp.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Rebuild();
    }

    static string NameOf(string tool) => IconCatalog.AgentName(tool);

    TokenPreview[] Previews => new[] { DoneOnDark, DoneOnLight, BlockedOnDark, BlockedOnLight };

    void ShowPreviews(CursorSettings s)
    {
        foreach (var preview in Previews)
        {
            preview.Tool = _tool;
            preview.Show(s);
        }
    }

    void Refresh()
    {
        var s = _settings.Current;
        ShowPreviews(s);
        FitSwatches();
        ResetButton.IsEnabled = s.Icons.Count > 0;
        bool effects = SystemParameters.ClientAreaAnimation;
        bool still = MotionProfile.Effective(s.Motion, effects) == MotionStyle.Still;
        AnimateSwitch.IsEnabled = !still;
        AnimateSwitch.IsChecked = !still && s.AnimateMascots;
        AnimateSwitch.ToolTip = !effects ? "Animation effects are off in Windows, so mascots stay still"
            : still ? "Motion is Still, so mascots stay still" : null;
        UpdateTileAnimation();
        // Only an icon choice changes the tiles (a size drag on the Sound and Display page leaves them alone). The
        // tiles update in place, so the keyboard focus stays where it is.
        if (ReferenceEquals(s.Icons, _shownChoices) || _agents.Length == 0) return;
        _shownChoices = s.Icons;
        foreach (var agent in _agents)
            agent.Update(agent.Tool == _tool, ToolLogo.Render(agent.Tool, s.IconFor(agent.Tool), 32, _dpi));
        foreach (var icon in _icons) icon.IsSelected = icon.Id == s.IconFor(_tool);
    }

    // (Re)builds every tile at this screen's DPI, for the agent being edited.
    void Rebuild()
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var s = _settings.Current;
        _agents = IconCatalog.Tools
            .Select(tool => new AgentTile(tool, NameOf(tool), ToolLogo.Render(tool, s.IconFor(tool), 32, _dpi), tool == _tool))
            .ToArray();
        AgentList.ItemsSource = _agents;
        RebuildGallery();
    }

    void RebuildGallery()
    {
        var s = _settings.Current;
        string chosen = s.IconFor(_tool), agent = NameOf(_tool);
        IconTile Tile(string id, string name, bool custom) => new(id, name, ToolLogo.Render(_tool, id, 48, _dpi), custom, agent)
            { IsSelected = id == chosen, Loop = custom ? null : MascotLoops.For(id) };
        AnimateTiles(false); // the new tiles start at rest
        _icons = IconCatalog.ForTool(_tool).Select(i => Tile(i.Id, i.Name, false))
            .Concat(IconSource.Custom.List(_tool).Select(id => Tile(id, IconSource.Custom.NameOf(_tool, id) ?? "Added icon", true)))
            .ToArray();
        BuiltInList.ItemsSource = _icons.Where(i => !i.IsCustom).ToArray();
        CustomList.ItemsSource = _icons.Where(i => i.IsCustom).ToArray();
        _shownChoices = s.Icons;
        GalleryHeader.Text = $"Choose an icon for {agent}";
        ShowAddState();
        ShowPreviews(s);
        UpdateTileAnimation();
    }

    // The two state swatches side by side when four whole tokens fit across, else one above the other.
    void OnSwatchesSize(object sender, SizeChangedEventArgs e) => FitSwatches();

    void FitSwatches()
    {
        double half = TokenPreview.IdleWidth(_settings.Live.Size);
        Swatches.Columns = Swatches.ActualWidth >= 4 * half + 2 * 16 ? 2 : 1;
    }

    // Four agents to a row; two when the page is too narrow for four.
    void OnAgentListSize(object sender, SizeChangedEventArgs e)
    {
        if (Find<System.Windows.Controls.Primitives.UniformGrid>(AgentList) is { } grid)
            grid.Columns = e.NewSize.Width >= 4 * MinAgentTileWidth ? 4 : 2;
    }

    void OnAgentChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AgentTile tile || tile.Tool == _tool) return;
        _tool = tile.Tool;
        foreach (var agent in _agents) agent.Update(agent.Tool == _tool, agent.Image);
        CloseStatus();
        RebuildGallery();
    }

    void OnIconChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IconTile tile) return;
        if (tile.Id != _settings.Live.IconFor(_tool))
            _settings.Change(s => s.Icons = IconChoices.With(s.Icons, _tool, tile.Id));
    }

    void OnReset(object sender, RoutedEventArgs e) => _settings.Change(s => s.Icons = IconChoices.None);

    // Removing is one click, so it can be undone: the bar under the tiles offers Undo until the next
    // thing happens there.
    void OnRemoveIcon(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IconTile tile) return;
        CloseStatus();
        try
        {
            _removed = IconSource.Custom.Remove(_tool, tile.Id, _settings);
            ShowStatus(InfoBarSeverity.Informational, "Icon removed", $"\u201C{tile.Name}\u201D is gone from {NameOf(_tool)}'s icons.", undo: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Couldn't remove that icon", ex.Message);
        }
        RebuildGallery();
    }

    void OnUndoRemove(object sender, RoutedEventArgs e)
    {
        if (_removed is not { } removed) return;
        CloseStatus();
        try { IconSource.Custom.Restore(removed, _settings); }
        catch (InvalidIconException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "That icon couldn't be put back", $"{Sentence(ex.Message)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "That icon couldn't be put back", ex.Message);
        }
        if (removed.Tool == _tool) RebuildGallery();
    }

    async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_uploading) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose an SVG icon for {NameOf(_tool)}",
            Filter = "SVG images (*.svg)|*.svg",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        string tool = _tool, agent = NameOf(tool);
        CloseStatus();
        _uploading = true;
        ShowAddState();
        try
        {
            string id = await Task.Run(() =>
            {
                if (new FileInfo(dialog.FileName).Length > SvgSanitizer.MaxBytes)
                    throw new InvalidIconException("it's larger than 256 KB");
                return IconSource.Custom.Add(tool, File.ReadAllText(dialog.FileName), Path.GetFileName(dialog.FileName));
            });
            _settings.Change(s => s.Icons = IconChoices.With(s.Icons, tool, id));
            if (tool == _tool) RebuildGallery();
        }
        catch (InvalidIconException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "That file couldn't be used", $"{Sentence(ex.Message)}. {agent} keeps its current icon.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "That file couldn't be used", $"It couldn't be read or saved ({ex.Message.TrimEnd('.')}). {agent} keeps its current icon.");
        }
        finally
        {
            _uploading = false;
            ShowAddState();
        }
    }

    // The "Add SVG…" tile: busy while a file is being checked (for any agent), off at the limit, and
    // at the limit a note under the tiles saying why (nothing there otherwise).
    void ShowAddState()
    {
        bool full = _icons.Count(i => i.IsCustom) >= CustomIconStore.MaxPerTool;
        AddButton.IsEnabled = !_uploading && !full;
        AddLabel.Visibility = _uploading ? Visibility.Collapsed : Visibility.Visible;
        AddBusy.Visibility = _uploading ? Visibility.Visible : Visibility.Collapsed;
        string limit = $"{NameOf(_tool)} has {CustomIconStore.MaxPerTool} of your icons, the most it keeps. Remove one to add another.";
        AddButton.ToolTip = full ? limit : null;
        AddHint.Text = full ? limit : "";
        ShowHint();
    }

    void ShowHint() => AddHint.Visibility = StatusBar.IsOpen || AddHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    // The bar under the tiles, in place of the hint: an error, or a removal with its Undo.
    void ShowStatus(InfoBarSeverity severity, string title, string message, bool undo = false)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
        UndoButton.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
    }

    void CloseStatus()
    {
        StatusBar.IsOpen = false;
        _removed = null;
    }

    static string Sentence(string reason) => reason.Length == 0 ? reason : char.ToUpperInvariant(reason[0]) + reason[1..];

    // Radio-group keys: the arrows move the choice along the group (the icons and your icons are one
    // group), wrapping at the ends; the choice follows the focus, as in any Windows radio group.
    void OnTileKey(object sender, KeyEventArgs e)
    {
        int step = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (step == 0 || Keyboard.FocusedElement is not RadioButton focused) return;
        var group = sender == AgentList ? Radios(AgentList).ToList() : Radios(BuiltInList).Concat(Radios(CustomList)).ToList();
        int index = group.IndexOf(focused);
        if (index < 0) return;
        var next = group[(index + step + group.Count) % group.Count];
        next.IsChecked = true;
        next.Focus();
        e.Handled = true;
    }

    static IEnumerable<RadioButton> Radios(ItemsControl list)
    {
        for (int i = 0; i < list.Items.Count; i++)
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject container && Find<RadioButton>(container) is { } radio)
                yield return radio;
    }

    static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T match) return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
        return null;
    }
}

/// <summary>An agent on the Customization page: its name, its current icon, and whether it is being edited.</summary>
public sealed class AgentTile(string tool, string name, ImageSource image, bool selected) : INotifyPropertyChanged
{
    public string Tool { get; } = tool;
    public string Name { get; } = name;
    public ImageSource Image { get; private set; } = image;
    public bool IsSelected { get; private set; } = selected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(bool selected, ImageSource image)
    {
        IsSelected = selected;
        Image = image;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>One icon in an agent's gallery: built-in, or one the user added (removable). A mascot's
/// tile shows the frame of its loop the page asks for, each drawn once and kept.</summary>
public sealed class IconTile(string id, string name, ImageSource image, bool custom, string agent) : INotifyPropertyChanged
{
    bool _selected;
    int _frame;
    readonly Dictionary<int, ImageSource> _frames = new() { [0] = image };

    public string Id { get; } = id;
    public string Name { get; } = name;
    public ImageSource Image { get; private set; } = image;

    /// <summary>The mascot's idle loop; null for an icon that stands still.</summary>
    public MascotLoop? Loop { get; init; }

    /// <summary>Shows the rest pose (the tile's own image).</summary>
    public void ShowRest() => ShowFrame(0, _ => _frames[0]);

    /// <summary>Shows <paramref name="frame"/>, drawing it with <paramref name="render"/> the first time
    /// (frame 0, the rest pose, is the tile's own image).</summary>
    public void ShowFrame(int frame, Func<int, ImageSource> render)
    {
        if (frame == _frame) return;
        if (!_frames.TryGetValue(frame, out var picture)) _frames[frame] = picture = render(frame);
        _frame = frame;
        Image = picture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
    }
    public bool IsCustom { get; } = custom;
    public string AutomationName => $"{agent} icon: {Name}";
    public string RemoveName => $"Remove icon: {Name}";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
