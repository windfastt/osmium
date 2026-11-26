using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Proto.Views;

public sealed partial class MainWindow : Window
{
    private readonly Panel _host;
    private readonly ListBox _nav;
    private readonly Dictionary<int, Control> _pages = new();

    public IReadOnlyList<NavItem> NavItems => NavItem.All;

    /// <summary>The Archives page once it has been visited (null before).</summary>
    public ArchivesPage? Archives => _pages.GetValueOrDefault(NavItem.ArchivesIndex) as ArchivesPage;

    /// <summary>Raised after the page for a rail entry became the visible page (index into <see cref="NavItem.All"/>).</summary>
    public event Action<int, Control>? PageShown;

    public MainWindow() : this(useMica: false) { }

    /// <param name="useMica">Windows 11: Mica behind the rail (the page area stays opaque --page).</param>
    public MainWindow(bool useMica)
    {
        App.Trace?.Invoke("window-ctor-start");
        DataContext = this;
        AvaloniaXamlLoader.Load(this);
        App.Trace?.Invoke("window-xaml-done");
        _host = this.FindControl<Panel>("PageHost")!;
        _nav = this.FindControl<ListBox>("Nav")!;
        _pages[0] = this.FindControl<CompressPage>("Compress")!;
        _nav.SelectionChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        App.Trace?.Invoke("window-ctor-done");
        if (useMica && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            Background = Brushes.Transparent;
        }
    }

    /// <summary>Selects a rail entry (as a click or UI Automation selection would).</summary>
    public void Navigate(int index) => _nav.SelectedIndex = index;

    private void ShowPage(int index)
    {
        if (index < 0) return;
        if (!_pages.TryGetValue(index, out var page))
        {
            page = index == NavItem.ArchivesIndex ? new ArchivesPage() : new PlaceholderPage(NavItem.All[index].Name);
            _pages[index] = page;
            _host.Children.Add(page);
        }
        foreach (var p in _pages.Values) p.IsVisible = ReferenceEquals(p, page);
        PageShown?.Invoke(index, page);
    }
}
