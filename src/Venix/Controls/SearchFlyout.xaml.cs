using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Venix.Core;

namespace Venix.Controls;

/// <summary>
/// Dropdown under the title-bar search box, Steam style: "Buscas recentes" (persisted in settings) when the box is focused,
/// and "Sugestões" (top hits of <see cref="SearchIndex"/>, matched parts highlighted) while typing.
/// Nothing in here takes keyboard focus, so the search box keeps it and drives the selection with ↑/↓/Enter.
/// </summary>
public partial class SearchFlyout : UserControl
{
    public const int MaxHistory = 8;
    const int MaxSuggestions = 6;

    /// <summary>A recent query was clicked: put it back in the box and search again.</summary>
    public event Action<string>? HistoryPicked;
    /// <summary>A suggestion was chosen (appId when Steam already resolved it).</summary>
    public event Action<int?, string>? GamePicked;
    /// <summary>"Ver todos no Catálogo" was chosen.</summary>
    public event Action<string>? SeeAllPicked;

    readonly List<(Border row, Action act)> _rows = new();
    int _selected = -1;
    string _query = "";
    bool _searching;
    List<CatalogGroup> _items = new();
    int _total;
    CancellationTokenSource? _cts;

    public SearchFlyout()
    {
        InitializeComponent();
        // the popup's events bubble to the title bar, whose mouse-down starts a window drag
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        Render();
    }

    public string Query => _query;

    /* ---------------------------------------------------------- history */

    public static List<string> History => Hub.Store.Settings.SearchHistory;

    /// <summary>Puts a query on top of the history (no duplicates, newest first) and saves it.</summary>
    public static void Remember(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var h = History;
        h.RemoveAll(x => string.Equals(x, query, StringComparison.OrdinalIgnoreCase));
        h.Insert(0, query);
        if (h.Count > MaxHistory) h.RemoveRange(MaxHistory, h.Count - MaxHistory);
        Hub.Store.SaveSoon();
    }

    void ClearHistory()
    {
        History.Clear();
        Hub.Store.SaveSoon();
        Render();
    }

    /* ---------------------------------------------------------- suggestions */

    /// <summary>New text in the box: refreshes the suggestions off the UI thread (the previous search is cancelled).</summary>
    public void SetQuery(string query)
    {
        query = query.Trim();
        _cts?.Cancel();
        _query = query;
        if (query.Length == 0)
        {
            _items = new();
            _total = 0;
            _searching = false;
            Render();
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        _searching = true;
        Render();
        _ = RunSearch(query, cts);
    }

    async Task RunSearch(string query, CancellationTokenSource cts)
    {
        (int total, List<CatalogGroup> items) r;
        try { r = await Task.Run(() => Hub.Sources.Index.Search(query, null, "options", cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        _items = r.items.Take(MaxSuggestions).ToList();
        _total = r.total;
        _searching = false;
        Render();
    }

    /* ---------------------------------------------------------- keyboard */

    public bool HasRows => _rows.Count > 0;

    /// <summary>↓ / ↑ : moves the selection through history, suggestions and "ver todos", wrapping around.</summary>
    public void Move(int delta)
    {
        if (_rows.Count == 0) return;
        Select(_selected < 0 ? (delta > 0 ? 0 : _rows.Count - 1) : (_selected + delta + _rows.Count) % _rows.Count);
    }

    void Select(int i)
    {
        if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].row.Tag = null;
        _selected = i;
        if (_selected >= 0 && _selected < _rows.Count) _rows[_selected].row.Tag = "sel";
    }

    /// <summary>Enter: the selected row, else the first suggestion, else the full catalog. False when there is nothing to open.</summary>
    public bool Activate()
    {
        if (_selected >= 0 && _selected < _rows.Count) { _rows[_selected].act(); return true; }
        if (_items.Count > 0) { PickGame(_items[0]); return true; }
        if (_query.Length > 0) { SeeAllPicked?.Invoke(_query); return true; }
        return false;
    }

    void PickGame(CatalogGroup g)
    {
        var app = Hub.Steam.LookupAsync(g.Title);
        GamePicked?.Invoke(app.IsCompletedSuccessfully ? app.Result?.AppId : null, g.Title);
    }

    /* ---------------------------------------------------------- rendering */

    Brush B(string key) => (Brush)FindResource(key);

    void Render()
    {
        Body.Children.Clear();
        _rows.Clear();
        _selected = -1;

        var history = History;
        if (history.Count > 0)
        {
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = "BUSCAS RECENTES", Style = (Style)FindResource("Section") });
            var clear = new Button { Content = "LIMPAR", Style = (Style)FindResource("Link"), HorizontalAlignment = HorizontalAlignment.Right, ToolTip = "Apagar o histórico de buscas" };
            clear.Click += (_, _) => ClearHistory();
            head.Children.Add(clear);
            Body.Children.Add(head);
            foreach (var q in history)
            {
                var text = q;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Style = (Style)FindResource("T.Icon"), Text = Icons.Clock, FontSize = 13, Foreground = B("Text.3"), Margin = new Thickness(2, 0, 12, 0) });
                content.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = B("Text"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                AddRow(content, () => HistoryPicked?.Invoke(text));
            }
        }

        if (_query.Length > 0)
        {
            if (history.Count > 0) Body.Children.Add(new Border { Height = 1, Background = B("Line"), Margin = new Thickness(10, 6, 10, 2) });
            Body.Children.Add(new TextBlock { Text = "SUGESTÕES", Style = (Style)FindResource("Section") });

            if (_items.Count == 0)
            {
                Body.Children.Add(new TextBlock
                {
                    Text = _searching ? "Buscando…" : $"Nenhum jogo encontrado para “{_query}”.",
                    Style = (Style)FindResource("T.Body"), FontSize = 13, Margin = new Thickness(12, 2, 12, 10)
                });
            }
            foreach (var g in _items) AddSuggestion(g);

            if (_total > 0)
            {
                var more = new StackPanel { Orientation = Orientation.Horizontal };
                more.Children.Add(new TextBlock { Style = (Style)FindResource("T.Icon"), Text = Icons.Catalog, FontSize = 13, Foreground = B("Text.2"), Margin = new Thickness(2, 0, 12, 0) });
                more.Children.Add(new TextBlock
                {
                    Text = _total == 1 ? "Ver no Catálogo" : $"Ver todos os {Format.Number(_total)} resultados no Catálogo",
                    FontSize = 13, Foreground = B("Text.2"), VerticalAlignment = VerticalAlignment.Center
                });
                var query = _query;
                AddRow(more, () => SeeAllPicked?.Invoke(query));
            }
        }
        else if (history.Count == 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = "Digite o nome de um jogo. Siglas (\"gta 5\", \"re4\", \"aoe2\") e pequenos erros de digitação também funcionam.",
                Style = (Style)FindResource("T.Body"), FontSize = 13, Margin = new Thickness(12, 10, 12, 10)
            });
        }
    }

    void AddSuggestion(CatalogGroup g)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // small portrait cover: generated placeholder first, Steam art as soon as the lookup resolves
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        Img.SetDecodeWidth(brush, 80);
        Img.SetUrls(brush, Img.Chain(null, g.Title));
        var cover = new Border { Width = 32, Height = 44, CornerRadius = new CornerRadius(7), Background = B("Placeholder"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        cover.Child = new Border { CornerRadius = new CornerRadius(7), Background = brush };
        grid.Children.Add(cover);
        ResolveArt(g.Title, brush);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(HighlightedTitle(g.Title, _query));
        var extras = new[] { g.Count == 1 ? "1 opção de download" : $"{g.Count} opções de download", string.Join(" · ", g.Sources.Take(3)) }.Where(s => s.Length > 0);
        text.Children.Add(new TextBlock { Text = string.Join("  ·  ", extras), FontSize = 11.5, Foreground = B("Text.3"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        AddRow(grid, () => PickGame(g));
    }

    static async void ResolveArt(string title, ImageBrush brush)
    {
        SteamApp? app;
        try { app = await Hub.Steam.LookupAsync(title); } catch { return; }
        if (app != null) Img.SetUrls(brush, Img.Chain(SteamService.Portrait(app.AppId), title));
    }

    /// <summary>Title with the matched parts as orange runs (Steam style).</summary>
    TextBlock HighlightedTitle(string title, string query)
    {
        var tb = new TextBlock { FontSize = 13.5, Foreground = B("Text"), TextTrimming = TextTrimming.CharacterEllipsis };
        var pos = 0;
        foreach (var (start, length) in SearchIndex.Highlight(title, query))
        {
            if (start > pos) tb.Inlines.Add(new Run(title[pos..start]));
            tb.Inlines.Add(new Run(title.Substring(start, length)) { Background = B("Accent"), Foreground = B("Ink.Text") });
            pos = start + length;
        }
        if (pos < title.Length) tb.Inlines.Add(new Run(title[pos..]));
        return tb;
    }

    void AddRow(UIElement content, Action act)
    {
        var row = new Border { Style = (Style)FindResource("Row"), Child = content };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        row.MouseEnter += (_, _) => Select(_rows.FindIndex(r => r.row == row));
        Body.Children.Add(row);
        _rows.Add((row, act));
    }
}
