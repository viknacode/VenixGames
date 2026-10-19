using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Venix.Core;

namespace Venix.Pages;

public partial class CatalogPage : UserControl, IPage
{
    const int PageSize = 42;

    public string Title => "Catálogo";
    public string Subtitle => "Tudo que as suas fontes de download oferecem";
    public string NavKey => "catalog";

    readonly ObservableCollection<Tile> _tiles = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    string _sort = "recent";
    string? _source;
    int _offset;
    int _total;
    bool _ready;
    bool _suppress;
    CancellationTokenSource? _cts;
    /// <summary>Ranked matches of the last search (the whole catalog when the query is empty); pages are cut from here.</summary>
    List<CatalogGroup> _matches = new();

    public CatalogPage(string query = "", bool focusSearch = false)
    {
        InitializeComponent();
        TileList.ItemsSource = _tiles;
        Search.Text = query;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        Loaded += (_, _) =>
        {
            Hub.Sources.IndexChanged += OnIndex;
            _ready = true;
            BuildChips();
            Reload();
            if (focusSearch && !string.IsNullOrEmpty(query)) FocusSearch();
        };
        Unloaded += (_, _) => { Hub.Sources.IndexChanged -= OnIndex; _cts?.Cancel(); };
    }

    void OnIndex()
    {
        BuildChips();
        Reload();
    }

    void BuildChips()
    {
        SourceChips.Children.Clear();
        var names = Hub.Sources.SourceNames;
        if (names.Count == 0) return;
        void Add(string label, string? value)
        {
            var rb = new RadioButton { Style = (Style)FindResource("FilterChip"), Content = label, GroupName = "src", IsChecked = _source == value, Tag = value };
            rb.Checked += (_, _) => { _source = (string?)rb.Tag; Reload(); };
            SourceChips.Children.Add(rb);
        }
        Add("Todas as fontes", null);
        foreach (var n in names) Add(n, n);
    }

    /// <summary>Driven by the search box in the title bar: same text, searched at once.</summary>
    public void SetQuery(string query)
    {
        if (Search.Text == query) return;
        _suppress = true;
        Search.Text = query;
        _suppress = false;
        _debounce.Stop();
        Reload();
    }

    public void FocusSearch()
    {
        Search.Focus();
        Search.CaretIndex = Search.Text.Length;
    }

    /// <summary>Searches off the UI thread; a newer search cancels the one in flight, so fast typing never queues work.</summary>
    async void Reload()
    {
        if (!_ready) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var (query, source, sort) = (Search.Text, _source, _sort);
        (int total, List<CatalogGroup> items) result;
        try { result = await Task.Run(() => Hub.Sources.Index.Search(query, source, sort, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || !_ready) return;

        _matches = result.items;
        _total = result.total;
        _offset = 0;
        _tiles.Clear();
        LoadPage();
        Scroller.ScrollToTop();
    }

    void LoadPage()
    {
        var items = _matches.Skip(_offset).Take(PageSize).ToList();
        var first = _offset == 0 && Search.Text.Trim().Length > 0;
        _offset += items.Count;
        Total.Text = Format.Number(_total);

        foreach (var g in items)
        {
            var sub = string.Join("  ·  ", new[] { string.Join(" · ", g.Sources), g.ExtrasLabel }.Where(s => s.Length > 0));
            var tile = new Tile
            {
                Title = g.Title,
                Sub = first ? "Melhor resultado  ·  " + sub : sub,
                Badge = g.Count.ToString(),
                BadgeIcon = Icons.Download,
                Payload = g
            };
            _tiles.Add(tile);
            ResolveArt(tile);
            first = false;
        }

        More.Visibility = _offset < _matches.Count ? Visibility.Visible : Visibility.Collapsed;
        var noSources = Hub.Store.Sources.Count == 0;
        Empty.Visibility = _total == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = noSources ? "Nenhuma fonte configurada" : "Nada encontrado";
        EmptyText.Text = noSources
            ? "Adicione a URL de uma fonte de download (arquivo .json) em Ajustes para preencher o catálogo."
            : "Tente outro termo de busca ou remova o filtro de fonte.";
        EmptyBtn.Visibility = noSources ? Visibility.Visible : Visibility.Collapsed;
    }

    static async void ResolveArt(Tile tile)
    {
        var app = await Hub.Steam.LookupAsync(tile.Title);
        tile.AppId = app?.AppId; // null → cover generated from the title
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        _debounce.Stop();
        _debounce.Start();
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        _sort = (string)((RadioButton)sender).Tag;
        Reload();
    }

    void OnMore(object sender, RoutedEventArgs e) => LoadPage();

    void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        // infinite scroll
        if (_ready && _offset < _matches.Count && e.VerticalOffset > 0 && e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 400) LoadPage();
    }

    void OnTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tile t) MainWindow.Instance.OpenGame(t.AppId, t.Title);
    }

    void OnAddSource(object sender, RoutedEventArgs e) => MainWindow.Instance.Push(new SettingsPage("sources"));
}
