using System.Windows;
using System.Windows.Controls;
using Venix.Core;

namespace Venix.Pages;

public partial class LibraryPage : UserControl, IPage
{
    public string Title => "Biblioteca";
    public string Subtitle => "Seus jogos, tempo de jogo e atalhos";
    public string NavKey => "library";

    string _filter = "all";
    string _sort = "recent";

    public LibraryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { Hub.Store.LibraryChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => Hub.Store.LibraryChanged -= Refresh;
    }

    void Refresh()
    {
        if (!IsLoaded) return;
        var lib = Hub.Store.Library;
        StatGames.Text = lib.Count.ToString();
        StatTime.Text = Format.Playtime(lib.Sum(g => g.PlaytimeSeconds));
        StatReady.Text = lib.Count(g => g.HasExe).ToString();

        IEnumerable<LibraryGame> list = lib;
        if (_filter == "ready") list = list.Where(g => g.HasExe);
        if (_filter == "played") list = list.Where(g => g.PlaytimeSeconds > 0);
        var q = Titles.Normalize(Search.Text);
        if (q.Length > 0) list = list.Where(g => Titles.Normalize(g.Title).Contains(q));
        list = _sort switch
        {
            "az" => list.OrderBy(g => g.Title),
            "time" => list.OrderByDescending(g => g.PlaytimeSeconds),
            _ => list.OrderByDescending(g => g.LastPlayed ?? g.AddedAt)
        };

        TileList.ItemsSource = list.Select(g => new Tile
        {
            Title = g.Title,
            AppId = g.AppId,
            Badge = g.PlaytimeLabel,
            BadgeIcon = Icons.Clock,
            Sub = Hub.Store.IsRunning(g.Id) ? "Em execução" : g.HasExe ? "Pronto para jogar" : "Executável não definido",
            Payload = g
        }).ToList();
        Empty.Visibility = lib.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnFilter(object sender, RoutedEventArgs e) { _filter = (string)((RadioButton)sender).Tag; Refresh(); }
    void OnSort(object sender, RoutedEventArgs e) { _sort = (string)((RadioButton)sender).Tag; Refresh(); }
    void OnSearch(object sender, TextChangedEventArgs e) => Refresh();
    void OnExplore(object sender, RoutedEventArgs e) => MainWindow.Instance.Go("catalog");

    void OnTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tile { Payload: LibraryGame g }) MainWindow.Instance.OpenGame(g.AppId, g.Title);
    }
}
