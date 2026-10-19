using System.Windows;
using System.Windows.Controls;
using Venix.Core;

namespace Venix.Pages;

/// <summary>Home: everything shown here comes from the loaded download lists.</summary>
public partial class HomePage
{
    string _tab = "recent";
    HashSet<int>? _steamTop;   // Steam top sellers, used only to rank list games for "Em alta"
    int _version;              // bumps on every reload so stale async work is dropped

    void OnIndex()
    {
        UpdateStats();
        var hasLists = Hub.Sources.GameCount > 0;
        NoLists.Visibility = hasLists ? Visibility.Collapsed : Visibility.Visible;
        Hero.Visibility = hasLists ? Visibility.Visible : Visibility.Collapsed;
        DiscoverHeader.Visibility = hasLists ? Visibility.Visible : Visibility.Collapsed;
        DiscoverList.Visibility = hasLists ? Visibility.Visible : Visibility.Collapsed;
        DiscoverStatus.Visibility = Visibility.Collapsed;
        var errors = Hub.Store.Sources.Count(s => s.Status == "error");
        NoListsTitle.Text = Hub.Store.Sources.Count == 0 ? "Nenhuma lista carregada" : errors > 0 ? "Listas com erro" : "Listas vazias";
        NoListsText.Text = Hub.Store.Sources.Count == 0
            ? "Os jogos do Venix vêm das suas listas de download. Coloque os arquivos .json na pasta sources (ao lado do Venix.exe) e recarregue."
            : $"{errors} lista(s) não puderam ser sincronizadas. Veja o motivo em Ajustes → Fontes de download; confira se os arquivos .json da pasta sources estão completos.";
        if (!hasLists) { _rotate.Stop(); _heroItems.Clear(); DiscoverList.ItemsSource = null; return; }
        _ = LoadFromLists(++_version);
    }

    async Task LoadFromLists(int version)
    {
        ShowTab(_tab);
        // Hero: the most recent list games that have Steam art
        var hero = new List<SteamApp>();
        foreach (var g in Hub.Sources.Query("", null, "recent", 0, 40).items)
        {
            var app = await Hub.Steam.LookupAsync(g.Title);
            if (version != _version) return;
            if (app != null && hero.All(h => h.AppId != app.AppId)) hero.Add(new SteamApp { AppId = app.AppId, Name = g.Title });
            if (hero.Count == 5) break;
        }
        _heroItems = hero;
        BuildDots();
        _rotate.Stop();
        _rotate.Start();
        ShowHero(0);
    }

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;
        _tab = key;
        if (IsLoaded && Hub.Sources.GameCount > 0) ShowTab(key);
    }

    async void ShowTab(string key)
    {
        List<CatalogGroup> groups;
        if (key == "popular")
        {
            // list games that are currently selling well on Steam
            try { _steamTop ??= (await Hub.Steam.FeaturedAsync()).Values.SelectMany(l => l).Select(a => a.AppId).ToHashSet(); }
            catch { _steamTop = new(); }
            var all = Hub.Sources.Query("", null, "options", 0, 400).items;
            groups = new();
            foreach (var g in all)
            {
                var app = await Hub.Steam.LookupAsync(g.Title);
                if (app != null && _steamTop.Contains(app.AppId)) groups.Add(g);
            }
            if (groups.Count < 10) groups.AddRange(all.Where(g => !groups.Contains(g)).Take(20 - groups.Count));
            if (_tab != key) return;
        }
        else groups = Hub.Sources.Query("", null, key == "options" ? "options" : "recent", 0, 20).items;

        var tiles = groups.Take(20).Select(g => new Tile
        {
            Title = g.Title,
            Sub = string.Join("  ·  ", new[] { string.Join(" · ", g.Sources), g.ExtrasLabel }.Where(s => s.Length > 0)),
            Badge = g.Count.ToString(),
            BadgeIcon = Icons.Download,
            Payload = g
        }).ToList();
        DiscoverList.ItemsSource = tiles;
        DiscoverStatus.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscoverStatus.Text = "Nada por aqui.";
        foreach (var t in tiles) ResolveArt(t);
    }

    static async void ResolveArt(Tile t)
    {
        var app = await Hub.Steam.LookupAsync(t.Title);
        t.AppId = app?.AppId; // null → cover generated from the title
    }

    void OnSurprise(object sender, RoutedEventArgs e)
    {
        var (total, _) = Hub.Sources.Query("", null, "recent", 0, 0);
        if (total == 0) return;
        var g = Hub.Sources.Query("", null, "recent", Random.Shared.Next(total), 1).items.First();
        MainWindow.Instance.OpenGame(null, g.Title);
    }

    void OnEditConfig(object sender, RoutedEventArgs e)
    {
        SourceService.ReadConfig();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SourceService.ConfigFolder) { UseShellExecute = true }); }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    async void OnReloadLists(object sender, RoutedEventArgs e)
    {
        ReloadLists.IsEnabled = false;
        await MainWindow.Instance.ApplySourceConfig();
        ReloadLists.IsEnabled = true;
    }
}
