using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Venix.Core;
using Venix.Dialogs;

namespace Venix.Pages;

/// <summary>
/// Sidebar page listing every update, patch and fix found in the download lists — the entries the catalog does not
/// show as games — tied to the game they belong to. One tab per kind; rows open the game, "Baixar" opens the
/// download dialog with that very option checked.
/// </summary>
public partial class UpdatesPage : UserControl, IPage
{
    public string Title => "Atualizações";
    public string Subtitle => "Atualizações, patches e fixes das suas fontes, separados dos jogos";
    public string NavKey => "updates";

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    List<ExtraRow> _rows = new();   // every kind, newest first; built off the UI thread and kept until the index changes
    EntryKind _kind = EntryKind.Update;
    int _version;                   // bumps on every reload so stale async work is dropped
    bool _ready;

    public UpdatesPage()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };
        Loaded += (_, _) =>
        {
            Hub.Sources.IndexChanged += OnIndex;
            _ready = true;
            _ = LoadAsync();
        };
        Unloaded += (_, _) => Hub.Sources.IndexChanged -= OnIndex;
    }

    void OnIndex() => _ = LoadAsync();

    async Task LoadAsync()
    {
        var v = ++_version;
        var rows = await Task.Run(() => Hub.Sources.Extras.Select(x => new ExtraRow(x)).ToList());
        if (v != _version) return;
        _rows = rows;
        TabUpdates.Content = TabLabel("Atualizações", EntryKind.Update);
        TabPatches.Content = TabLabel("Patches", EntryKind.Patch);
        TabFixes.Content = TabLabel("Fixes", EntryKind.Fix);
        Apply();
    }

    string TabLabel(string name, EntryKind k)
    {
        var n = _rows.Count(r => r.Kind == k);
        return n > 0 ? $"{name} · {Format.Number(n)}" : name;
    }

    void Apply()
    {
        if (!_ready) return;
        var q = Titles.Normalize(Search.Text);
        var items = _rows.Where(r => r.Kind == _kind && (q.Length == 0 || r.GameNorm.Contains(q) || r.TitleNorm.Contains(q))).ToList();
        List.ItemsSource = items;
        KindLabel.Text = EntryClassifier.Section(_kind);
        Total.Text = Format.Number(items.Count);
        TotalUnit.Text = items.Count == 1 ? "item" : "itens";

        var any = _rows.Any(r => r.Kind == _kind);
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = !any ? $"Nenhum{(_kind == EntryKind.Update ? "a" : "")} {EntryClassifier.Label(_kind).ToLowerInvariant()} nas suas fontes" : "Nada encontrado";
        EmptyText.Text = Hub.Store.Sources.Count == 0
            ? "Os itens desta página vêm das suas listas de download. Coloque os arquivos .json na pasta sources (ao lado do Venix.exe) e recarregue."
            : !any ? "As listas carregadas não trazem itens deste tipo separados do jogo completo."
            : "Tente outro termo de busca ou outra aba.";

        foreach (var r in items) ResolveArt(r);
    }

    /// <summary>Steam lookups are cached per title, so the same game across many rows costs one request.</summary>
    static async void ResolveArt(ExtraRow r)
    {
        if (r.AppId != null) return;
        var app = await Hub.Steam.LookupAsync(r.GameTitle);
        if (app != null) r.AppId = app.AppId;
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    void OnTab(object sender, RoutedEventArgs e)
    {
        _kind = (sender as RadioButton)?.Tag switch { "patch" => EntryKind.Patch, "fix" => EntryKind.Fix, _ => EntryKind.Update };
        Apply();
    }

    void OnRow(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExtraRow r) MainWindow.Instance.OpenGame(r.AppId, r.GameTitle);
    }

    void OnDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtraRow r) return;
        var name = r.GameTitle;
        var id = Hub.Store.Library.FirstOrDefault(g => (r.AppId != null && g.AppId == r.AppId) || g.Id == Hub.GameId(r.AppId, name))?.Id ?? Hub.GameId(r.AppId, name);
        // every option of that game, with this row's entry guaranteed to be among them
        var options = Hub.Sources.FindRepacks(name);
        if (!options.Contains(r.Entry)) options.Add(r.Entry);
        MainWindow.Instance.ShowModal(new DownloadOptionsDialog(id, r.AppId, name, options, r.Entry));
    }
}

/// <summary>Row of <see cref="UpdatesPage"/>: an update/patch/fix entry plus the cover of its base game.</summary>
public class ExtraRow : Observable
{
    readonly ExtraEntry _x;
    int? _appId;
    string[] _urls;

    public ExtraRow(ExtraEntry x)
    {
        _x = x;
        GameNorm = Titles.Normalize(x.GameTitle);
        TitleNorm = Titles.Normalize(x.Entry.Title);
        _urls = Img.Chain(null, x.GameTitle);
    }

    public SourceEntry Entry => _x.Entry;
    public EntryKind Kind => _x.Entry.Kind;
    public string GameTitle => _x.GameTitle;
    public string Title => _x.Entry.Title;
    public string KindLabel => _x.Entry.KindLabel;
    public string Meta => _x.Entry.Meta;
    public string GameNorm { get; }
    public string TitleNorm { get; }

    public string[] Urls { get => _urls; private set => Set(ref _urls, value); }
    public int? AppId
    {
        get => _appId;
        set { _appId = value; if (value is { } id) Urls = Img.Chain(SteamService.Portrait(id), GameTitle); }
    }
}
