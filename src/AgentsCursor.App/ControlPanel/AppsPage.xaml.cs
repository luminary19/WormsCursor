using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentsCursor.Core;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// The apps that hide the token while they're in front (a game, say): the list with a Remove per row,
/// an "Add an app" card that offers the apps open right now (with their window titles) or takes a
/// typed exe name, and a way back to the default list. Each edit replaces
/// <see cref="CursorSettings.SuppressedPrograms"/> through <see cref="LiveSettings.Change"/>, which
/// also re-checks the app in front at once.
///
/// The list stores exe names only, so an app's icon and description are known just while its exe is:
/// when it has a window open now, or had one earlier while the page was open.
/// </summary>
public partial class AppsPage : Page
{
    readonly LiveSettings _settings;
    // Exe name -> what its file says about itself, for the apps seen with a window open.
    readonly Dictionary<string, AppIdentity> _known = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<string>? _shown; // the program list the rows were built from

    public AppsPage(LiveSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        AddExpander.Expanded += (_, _) => ShowOpenApps();
        RefreshButton.Click += (_, _) => ShowOpenApps();
        NameBox.TextChanged += (_, _) => AddNameButton.IsEnabled = TypedName() is not null;
        NameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddTyped(); };
        AddNameButton.Click += (_, _) => AddTyped();
        DefaultsButton.Click += (_, _) => _settings.Change(s => s.SuppressedPrograms = ProgramSuppression.Defaults);
        DefaultsButton.ToolTip = "Replace the list with " + string.Join(", ", ProgramSuppression.Defaults);

        Loaded += (_, _) => { LearnOpenApps(); ShowList(); };
        _settings.Changed += Refresh;
        Refresh();
    }

    // Everything here follows the program list, which is replaced (never changed in place) on each
    // edit, so other settings changes (a size drag on the Token page) leave the page alone.
    void Refresh()
    {
        var programs = _settings.Live.SuppressedPrograms;
        if (ReferenceEquals(programs, _shown)) return;
        ShowList();
        bool empty = programs.Count == 0;
        ListSurface.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DefaultsButton.IsEnabled = !programs.SequenceEqual(ProgramSuppression.Defaults, StringComparer.OrdinalIgnoreCase);
        AddNameButton.IsEnabled = TypedName() is not null;
        if (AddExpander.IsExpanded) ShowOpenApps(); // added ones leave the "open now" list
    }

    void ShowList()
    {
        _shown = _settings.Live.SuppressedPrograms;
        AppList.ItemsSource = _shown.Select(p => AppRow.Listed(p, _known.GetValueOrDefault(p))).ToArray();
    }

    // The apps showing a window right now, minus those already listed.
    void ShowOpenApps()
    {
        var listed = _settings.Live.SuppressedPrograms;
        var open = LearnOpenApps().Where(w => !ProgramSuppression.Matches(w.Program, listed))
            .Select(w => AppRow.Open(w, _known[w.Program]))
            .ToArray();
        OpenList.ItemsSource = open;
        NothingOpen.Visibility = open.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowList(); // a listed app that just opened a window now has its icon
    }

    // Lists the apps with a window open and remembers what each one's exe says about itself.
    IReadOnlyList<WindowProgram> LearnOpenApps()
    {
        var open = WindowPrograms.ListVisible();
        foreach (var window in open)
            if (!_known.ContainsKey(window.Program)) _known[window.Program] = AppIdentity.Read(window.Path);
        return open;
    }

    // The typed name, normalized, if it is a usable exe name that isn't listed yet.
    string? TypedName()
    {
        string? name = ProgramSuppression.NormalizeName(NameBox.Text);
        return name is null || ProgramSuppression.Matches(name, _settings.Live.SuppressedPrograms) ? null : name;
    }

    void AddTyped()
    {
        if (TypedName() is not string name) return;
        NameBox.Text = "";
        Add(name);
    }

    void OnAddOpen(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppRow app) Add(app.Program);
    }

    void Add(string program) =>
        _settings.Change(s => s.SuppressedPrograms = s.SuppressedPrograms.Append(program).ToArray());

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AppRow app) return;
        _settings.Change(s => s.SuppressedPrograms = s.SuppressedPrograms
            .Where(p => !string.Equals(p, app.Program, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
}
