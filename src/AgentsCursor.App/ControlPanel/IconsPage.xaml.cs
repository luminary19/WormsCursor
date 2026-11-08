using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentsCursor.Core;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// What each agent looks like on the token: a preview of the chosen icon in both states ("Done", and
/// "Needs you" with the "?" and a "+1"), a row of agents to pick from, and that agent's gallery: its
/// original logo, its mascots, the SVGs you added (each removable), and "Add SVG…". Choosing applies
/// at once through <see cref="LiveSettings.Change"/>. An added file is sanitised, test-drawn and stored
/// by <see cref="CustomIconStore"/> on a background task; if it can't be used, the reason shows here
/// and the agent keeps its icon.
///
/// The tiles are radio groups: Tab reaches the selected one, the arrow keys move the choice. The
/// thumbnails are drawn by the panel's own renderer on the UI thread (<see cref="ToolLogo"/>).
/// </summary>
public partial class IconsPage : Page
{
    readonly LiveSettings _settings;
    string _tool = IconCatalog.Tools[0];
    AgentTile[] _agents = Array.Empty<AgentTile>();
    IconTile[] _icons = Array.Empty<IconTile>();
    IReadOnlyDictionary<string, string>? _shownChoices; // the choices the tiles reflect
    double _dpi;
    bool _uploading; // an added file is being checked and stored

    public IconsPage(LiveSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
        _settings.Changed += Refresh;
        Refresh();
    }

    // Moved to a monitor with another scale: redraw the thumbnails so they stay crisp.
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsLoaded) Rebuild();
    }

    static string NameOf(string tool) => IconCatalog.AgentName(tool);

    void Refresh()
    {
        var s = _settings.Current;
        DonePreview.Tool = BlockedPreview.Tool = _tool;
        DonePreview.Show(s);
        BlockedPreview.Show(s);
        ResetButton.IsEnabled = s.Icons.Count > 0;
        // Only an icon choice changes the tiles (a size drag on the Token page leaves them alone). The
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
            { IsSelected = id == chosen };
        _icons = IconCatalog.ForTool(_tool).Select(i => Tile(i.Id, i.Name, false))
            .Concat(IconSource.Custom.List(_tool).Select((id, n) => Tile(id, $"Your icon {n + 1}", true)))
            .ToArray();
        BuiltInList.ItemsSource = _icons.Where(i => !i.IsCustom).ToArray();
        CustomList.ItemsSource = _icons.Where(i => i.IsCustom).ToArray();
        _shownChoices = s.Icons;
        GalleryHeader.Text = $"Choose an icon for {agent}";
        ShowAddState();
        DonePreview.Tool = BlockedPreview.Tool = _tool;
        DonePreview.Show(s);
        BlockedPreview.Show(s);
    }

    void OnAgentChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AgentTile tile || tile.Tool == _tool) return;
        _tool = tile.Tool;
        foreach (var agent in _agents) agent.Update(agent.Tool == _tool, agent.Image);
        UploadError.IsOpen = false;
        RebuildGallery();
    }

    void OnIconChecked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IconTile tile) return;
        if (tile.Id != _settings.Live.IconFor(_tool))
            _settings.Change(s => s.Icons = IconChoices.With(s.Icons, _tool, tile.Id));
    }

    void OnReset(object sender, RoutedEventArgs e) => _settings.Change(s => s.Icons = IconChoices.None);

    void OnRemoveIcon(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not IconTile tile) return;
        UploadError.IsOpen = false;
        try { IconSource.Custom.Remove(_tool, tile.Id, _settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Couldn't remove that icon", ex.Message);
        }
        RebuildGallery();
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
        UploadError.IsOpen = false;
        _uploading = true;
        ShowAddState();
        try
        {
            string id = await Task.Run(() =>
            {
                if (new FileInfo(dialog.FileName).Length > SvgSanitizer.MaxBytes)
                    throw new InvalidIconException("it's larger than 256 KB");
                return IconSource.Custom.Add(tool, File.ReadAllText(dialog.FileName));
            });
            _settings.Change(s => s.Icons = IconChoices.With(s.Icons, tool, id));
            if (tool == _tool) RebuildGallery();
        }
        catch (InvalidIconException ex)
        {
            ShowError("That file couldn't be used", $"{Sentence(ex.Message)}. {agent} keeps its current icon.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("That file couldn't be used", $"It couldn't be read or saved ({ex.Message.TrimEnd('.')}). {agent} keeps its current icon.");
        }
        finally
        {
            _uploading = false;
            ShowAddState();
        }
    }

    // The "Add SVG…" tile: busy while a file is being checked (for any agent), off at the limit.
    void ShowAddState()
    {
        AddButton.IsEnabled = !_uploading && _icons.Count(i => i.IsCustom) < CustomIconStore.MaxPerTool;
        AddLabel.Visibility = _uploading ? Visibility.Collapsed : Visibility.Visible;
        AddBusy.Visibility = _uploading ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowError(string title, string message)
    {
        UploadError.Title = title;
        UploadError.Message = message;
        UploadError.IsOpen = true;
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

/// <summary>An agent on the Icons page: its name, its current icon, and whether it is being edited.</summary>
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

/// <summary>One icon in an agent's gallery: built-in, or one the user added (removable).</summary>
public sealed class IconTile(string id, string name, ImageSource image, bool custom, string agent) : INotifyPropertyChanged
{
    bool _selected;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public ImageSource Image { get; } = image;
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
