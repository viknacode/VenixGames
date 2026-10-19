using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Venix.Core;

namespace Venix.Pages;

public partial class HomePage : UserControl, IPage
{
    public string Title => Greeting();
    public string Subtitle => DateTime.Now.ToString("dddd, d 'de' MMMM", new System.Globalization.CultureInfo("pt-BR"));
    public string NavKey => "home";

    List<SteamApp> _heroItems = new();
    int _heroIndex;
    readonly DispatcherTimer _rotate = new() { Interval = TimeSpan.FromSeconds(8) };

    public HomePage()
    {
        InitializeComponent();
        _rotate.Tick += (_, _) => ShowHero(_heroIndex + 1);
        Loaded += (_, _) =>
        {
            Hub.Sources.IndexChanged += OnIndex;
            OnIndex();
        };
        Unloaded += (_, _) =>
        {
            Hub.Sources.IndexChanged -= OnIndex;
            _rotate.Stop();
        };
    }

    static string Greeting()
    {
        var h = DateTime.Now.Hour;
        return h < 12 ? "Bom dia" : h < 18 ? "Boa tarde" : "Boa noite";
    }

    void UpdateStats()
    {
        StatTitles.Text = Format.Number(Hub.Sources.GameCount);
        var n = Hub.Store.Sources.Count;
        StatTitlesSub.Text = n == 0 ? "Adicione fontes em Ajustes" : $"{Format.Number(Hub.Sources.EntryCount)} opções em {n} fonte{(n > 1 ? "s" : "")}";

        var lib = Hub.Store.Library;
        StatLib.Text = lib.Count.ToString();
        StatLibSub.Text = $"{Format.Playtime(lib.Sum(g => g.PlaytimeSeconds))} jogados no total";

        var dl = Hub.Store.Downloads;
        var active = dl.Count(d => d.IsActive);
        StatDl.Text = active.ToString();
        StatDlSub.Text = active > 0 ? "em andamento" : $"{dl.Count(d => d.IsComplete)} concluídos";

        var recent = lib.Where(g => g.LastPlayed != null).OrderByDescending(g => g.LastPlayed).Take(4).ToList();
        if (recent.Count == 0) recent = lib.OrderByDescending(g => g.AddedAt).Take(4).ToList();
        ContinueList.ItemsSource = recent;
        ContinueSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void BuildDots()
    {
        Dots.Children.Clear();
        for (int i = 0; i < _heroItems.Count; i++)
        {
            var idx = i;
            var dot = new Border { Height = 6, Width = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0), Background = Brushes.White, Opacity = 0.45, Cursor = System.Windows.Input.Cursors.Hand };
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowHero(idx); _rotate.Stop(); _rotate.Start(); };
            Dots.Children.Add(dot);
        }
    }

    async void ShowHero(int index)
    {
        if (_heroItems.Count == 0) return;
        _heroIndex = (index + _heroItems.Count) % _heroItems.Count;
        var app = _heroItems[_heroIndex];
        for (int i = 0; i < Dots.Children.Count; i++)
        {
            var d = (Border)Dots.Children[i];
            var on = i == _heroIndex;
            d.BeginAnimation(WidthProperty, new DoubleAnimation(on ? 22 : 6, TimeSpan.FromMilliseconds(250)));
            d.Opacity = on ? 1 : 0.45;
        }

        HeroTag.Text = $"NAS SUAS LISTAS  ·  {_heroIndex + 1}/{_heroItems.Count}";
        HeroTitle.Text = app.Name;
        HeroLogo.Source = null;
        HeroDesc.Text = "";
        var repacks = Hub.Sources.FindRepacks(app.Name).Count;
        HeroOptions.Text = repacks > 0 ? $"{repacks} opç{(repacks > 1 ? "ões" : "ão")} de download" : "";

        var art = await Img.LoadAsync(SteamService.Hero(app.AppId), 1600);
        if (_heroItems.Count == 0 || _heroItems[_heroIndex] != app) return;
        HeroArt.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(500)));
        HeroBrush.ImageSource = art;
        HeroBlurBrush.ImageSource = art;

        var logo = await Img.LoadAsync(SteamService.Logo(app.AppId), 600);
        if (_heroItems[_heroIndex] != app) return;
        HeroLogo.Source = logo;
        HeroTitle.Visibility = logo == null ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var details = await Hub.Steam.DetailsAsync(app.AppId);
            if (_heroItems[_heroIndex] == app) HeroDesc.Text = details?.ShortDescription ?? "";
        }
        catch { }
    }

    void OnHeroClick(object sender, RoutedEventArgs e)
    {
        if (_heroItems.Count == 0) return;
        var a = _heroItems[_heroIndex];
        MainWindow.Instance.OpenGame(a.AppId, a.Name);
    }

    void OnTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tile t) MainWindow.Instance.OpenGame(t.AppId, t.Title);
    }

    void OnContinue(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryGame g) MainWindow.Instance.OpenGame(g.AppId, g.Title);
    }

    void OnGoCatalog(object sender, RoutedEventArgs e) { e.Handled = true; MainWindow.Instance.Go("catalog"); }
    void OnGoLibrary(object sender, RoutedEventArgs e) { e.Handled = true; MainWindow.Instance.Go("library"); }
    void OnGoDownloads(object sender, RoutedEventArgs e) { e.Handled = true; MainWindow.Instance.Go("downloads"); }
}
