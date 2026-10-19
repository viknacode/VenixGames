using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Venix.Core;
using Venix.Dialogs;
using Venix.Trainer;

namespace Venix.Pages;

public partial class GamePage : UserControl, IPage
{
    int? _appId;
    string _name;
    string _id;
    SteamDetails? _details;
    List<SourceEntry> _repacks = new();
    bool _aboutExpanded;

    public string Title => _name;
    public string Subtitle => _details != null ? string.Join("  ·  ", _details.Genres.Take(3)) : "";
    public string NavKey => "";

    public GamePage(int? appId, string name)
    {
        InitializeComponent();
        _appId = appId;
        _name = name;
        _id = Hub.Store.Library.FirstOrDefault(g => (appId != null && g.AppId == appId) || g.Id == Hub.GameId(appId, name))?.Id ?? Hub.GameId(appId, name);
        NameText.Text = name;
        Loaded += async (_, _) =>
        {
            Hub.Store.LibraryChanged += UpdateLibrary;
            Hub.Store.RunningChanged += OnRunning;
            Hub.Sources.IndexChanged += UpdateRepacks;
            UpdateLibrary();
            UpdateRepacks();
            UpdateCheats();
            if (_details == null) await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            Hub.Store.LibraryChanged -= UpdateLibrary;
            Hub.Store.RunningChanged -= OnRunning;
            Hub.Sources.IndexChanged -= UpdateRepacks;
        };
    }

    async Task LoadAsync()
    {
        if (_appId == null)
        {
            var hit = await Hub.Steam.LookupAsync(_name);
            if (hit != null)
            {
                _appId = hit.AppId;
                // keep a library entry created by title pointing at the same game
                var g = Hub.Store.Find(_id);
                if (g != null && g.AppId == null) { g.AppId = hit.AppId; Hub.Store.SaveSoon(); }
            }
        }
        if (_appId is not { } id)
        {
            MetaText.Text = "Sem metadados da Steam para este título";
            ShortDesc.Text = "Não encontramos este jogo na Steam, mas você ainda pode ver as opções de download disponíveis nas suas fontes.";
            MoreBtn.Visibility = Visibility.Collapsed;
            ShotsHeader.Visibility = Visibility.Collapsed;
            AddInfo("Título", _name);
            LoadPlaceholderArt();
            return;
        }

        LoadArt(id);
        try { _details = await Hub.Steam.DetailsAsync(id); }
        catch (Exception ex) { MainWindow.Instance.Toast("Steam: " + ex.Message, true); }
        if (_details == null) return;

        _name = _details.Name;
        NameText.Text = _name;
        MainWindow.Instance.RefreshHeader();
        MetaText.Text = string.Join("   ·   ", new[] { _details.ReleaseDate, _details.Developers.FirstOrDefault() }.Where(s => !string.IsNullOrEmpty(s)));
        ShortDesc.Text = _details.ShortDescription;
        About.Text = _details.About;
        MoreBtn.Visibility = _details.About.Length > 500 ? Visibility.Visible : Visibility.Collapsed;
        Shots.ItemsSource = _details.Screenshots;
        ShotsCount.Text = $"{_details.Screenshots.Count} imagens";
        ShotsHeader.Visibility = _details.Screenshots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_details.Metacritic is { } mc) AddMetric(mc);
        AddInfo("Lançamento", _details.ReleaseDate);
        AddInfo("Desenvolvedor", string.Join(", ", _details.Developers));
        AddInfo("Distribuidora", string.Join(", ", _details.Publishers));
        if (_details.Recommendations is { } r) AddInfo("Avaliações na Steam", Format.Number(r));
        if (_details.Controller == "full") AddInfo("Controle", "Suporte completo");
        if (_details.Genres.Count > 0) AddChips(_details.Genres);
        UpdateRepacks();
        UpdateCheats();
    }

    async void LoadArt(int id)
    {
        // Steam hero, falling back to a cover generated from the title when Steam has no art for this app
        var art = await Img.LoadAsync(Img.Chain(SteamService.Hero(id), _name, wide: true), 1800);
        Art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
        ArtBrush.ImageSource = art;
        BlurBrush.ImageSource = art;
        var logo = await Img.LoadAsync(SteamService.Logo(id), 700);
        Logo.Source = logo;
        NameText.Visibility = logo == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>No Steam match: the hero still gets an image, generated from the game name.</summary>
    async void LoadPlaceholderArt()
    {
        var art = await Img.LoadAsync(Img.Placeholder(_name, wide: true), 1800);
        if (_appId != null || art == null) return;
        Art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
        ArtBrush.ImageSource = art;
        BlurBrush.ImageSource = art;
    }

    /* ---------------------------------------------------------- info card */

    void AddInfo(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("T.Label"), FontSize = 12.5 });
        var v = new TextBlock { Text = value, Foreground = (Brush)FindResource("Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        Info.Children.Add(g);
    }

    void AddMetric(int score)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(new TextBlock { Text = score.ToString(), Style = (Style)FindResource("T.Big") });
        sp.Children.Add(new TextBlock { Text = "Metacritic", Style = (Style)FindResource("T.Label"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 11) });
        Info.Children.Add(sp);
    }

    void AddChips(IEnumerable<string> items)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var i in items)
            wp.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = new TextBlock { Text = i, FontSize = 12, Foreground = (Brush)FindResource("Text.2") } });
        Info.Children.Add(wp);
    }

    /* ---------------------------------------------------------- state */

    void UpdateRepacks()
    {
        _repacks = Hub.Sources.FindRepacks(_name);
        var games = _repacks.Count(r => r.IsGame);
        RepackCount.Text = games.ToString();
        DownloadText.Text = _repacks.Count > 0 ? $"Baixar  ·  {_repacks.Count}" : "Sem downloads";
        DownloadBtn.IsEnabled = _repacks.Count > 0;
        RepackSources.Children.Clear();
        void Chip(string text) => RepackSources.Children.Add(new Border
        {
            Style = (Style)FindResource("Chip"),
            Child = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource("Text.2") }
        });
        foreach (var grp in _repacks.Where(r => r.IsGame).GroupBy(r => r.SourceName)) Chip($"{grp.Key}  {grp.Count()}");
        // updates, patches and fixes are counted apart from the full-game options
        foreach (var grp in _repacks.Where(r => !r.IsGame).GroupBy(r => r.Kind).OrderBy(g => g.Key)) Chip(EntryClassifier.Count(grp.Key, grp.Count()));
    }

    void UpdateLibrary()
    {
        var g = Hub.Store.Find(_id);
        var inLib = g != null;
        LibBtn.Content = inLib ? Icons.Check : Icons.Add;
        LibBtn.ToolTip = inLib ? "Na biblioteca" : "Adicionar à biblioteca";
        LibCard.Visibility = inLib ? Visibility.Visible : Visibility.Collapsed;
        PlayBtn.Visibility = inLib ? Visibility.Visible : Visibility.Collapsed;
        if (g == null) return;
        Playtime.Text = g.PlaytimeLabel;
        LastPlayed.Text = g.LastPlayed is { } lp ? $"Última sessão em {lp.ToLocalTime():dd/MM/yyyy}" : "Ainda não jogado";
        ExePath.Text = g.Exe ?? "Nenhum executável definido";
        PlayText.Text = Hub.Store.IsRunning(g.Id) ? "Em execução" : g.HasExe ? "Jogar" : "Definir executável";
    }

    void OnRunning(string id)
    {
        if (id == _id) UpdateLibrary();
    }

    /* ---------------------------------------------------------- actions */

    void OnToggleLibrary(object sender, RoutedEventArgs e)
    {
        if (Hub.Store.Find(_id) != null) return;
        Hub.Store.AddToLibrary(_id, _appId, _name);
        MainWindow.Instance.Toast($"{_name} adicionado à biblioteca");
    }

    async void OnPlay(object sender, RoutedEventArgs e)
    {
        var g = Hub.Store.Find(_id);
        if (g == null) return;
        if (!g.HasExe) { OnPickExe(sender, e); return; }
        try
        {
            Hub.Store.Launch(g);
            UpdateLibrary();
            if (g.CheatsOnLaunch && LinkedTable() is { } t)
            {
                // the trainer page attaches by itself as soon as the game process shows up
                await OpenTrainer(t);
            }
            else if (CheatProfiles.Count(g.Id) > 0)
            {
                // cheats found with the scanner: the page reconnects on its own and offers to activate them
                OpenScanner();
            }
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    void OnPickExe(object sender, RoutedEventArgs e)
    {
        var g = Hub.Store.Find(_id);
        if (g == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar executável do jogo",
            Filter = "Executável (*.exe;*.lnk;*.bat)|*.exe;*.lnk;*.bat",
            InitialDirectory = Directory.Exists(g.InstallPath) ? g.InstallPath : Hub.Store.Settings.DownloadDir
        };
        if (dlg.ShowDialog() != true) return;
        g.Exe = dlg.FileName;
        Hub.Store.SaveSoon();
        UpdateLibrary();
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var g = Hub.Store.Find(_id);
        var dir = g?.Exe != null ? Path.GetDirectoryName(g.Exe) : g?.InstallPath;
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        else MainWindow.Instance.Toast("Pasta do jogo ainda não definida.");
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        Hub.Store.RemoveFromLibrary(_id);
        MainWindow.Instance.Toast($"{_name} removido da biblioteca");
    }

    void OnDownloads(object sender, RoutedEventArgs e)
    {
        if (_repacks.Count == 0) { MainWindow.Instance.Toast("Nenhuma opção de download nas suas fontes para este jogo."); return; }
        MainWindow.Instance.ShowModal(new DownloadOptionsDialog(_id, _appId, _name, _repacks));
    }

    /* ---------------------------------------------------------- cheats */

    List<CheatTable> _cheats = new();
    /// <summary>Tables that are really for this game (same name or an edition of it), not just the same franchise.</summary>
    List<CheatTable> _ownCheats = new();

    async void UpdateCheats()
    {
        // the index is loaded once (LoadAsync caches its task); the lookups below are synchronous, in memory
        try { await Hub.Cheats.LoadAsync(); }
        catch { CheatCount.Text = "—"; CheatEmpty.Visibility = Visibility.Visible; CheatEmpty.Text = "Índice de cheats indisponível no momento."; return; }
        _cheats = Hub.Cheats.FindForGame(_name);
        _ownCheats = Hub.Cheats.FindForGame(_name, minScore: 2);
        UpdateMyCheats();
        CheatCount.Text = _cheats.Count.ToString();
        CheatSuggestions.ItemsSource = _cheats.Take(3).ToList();
        CheatEmpty.Visibility = _cheats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var g = Hub.Store.Find(_id);
        var linked = g?.CheatTable != null ? Hub.Cheats.Tables.FirstOrDefault(t => t.Path == g.CheatTable) : null;
        LinkedCheat.Visibility = linked != null ? Visibility.Visible : Visibility.Collapsed;
        if (linked != null)
        {
            LinkedName.Text = $"{linked.Name}  ·  {linked.FileName}";
            CheatsOnLaunch.IsChecked = g!.CheatsOnLaunch;
        }
        UpdateHeroCheats(linked);
    }

    int _myCheats;

    /// <summary>Cheats the user discovered with the memory scanner for this game (profile on disk).</summary>
    void UpdateMyCheats()
    {
        _myCheats = CheatProfiles.Count(_id);
        MyCheatsText.Text = _myCheats == 0
            ? "Nenhum ainda. Ache vida, dinheiro ou munição na memória do jogo e salve como cheat, sem precisar de tabela."
            : _myCheats == 1 ? "1 cheat salvo. O Venix reconecta e ativa quando o jogo abrir." : $"{_myCheats} cheats salvos. O Venix reconecta e ativa quando o jogo abrir.";
        MyCheatsBtn.Content = _myCheats == 0 ? "Criar cheats" : "Abrir meus cheats";
    }

    void OnScanner(object sender, RoutedEventArgs e) => OpenScanner();

    void OpenScanner()
    {
        var g = Hub.Store.Find(_id);
        var page = new ScannerPage(Venix.Trainer.TrainerHub.OpenProfile(_id, _name, g?.Exe));
        page.Unloaded += (_, _) => { UpdateMyCheats(); UpdateHeroCheats(LinkedTable()); };
        MainWindow.Instance.Push(page);
    }

    /// <summary>Hero bar "Cheats · N" button: when this game has a table in the index, one linked to it, or cheats found with the scanner.</summary>
    void UpdateHeroCheats(CheatTable? linked)
    {
        var has = linked != null || _ownCheats.Count > 0 || _myCheats > 0;
        CheatsBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (_myCheats > 0 && linked == null && _ownCheats.Count == 0)
        {
            CheatsBtnText.Text = $"Cheats  ·  {_myCheats}";
            CheatsBtn.ToolTip = _myCheats == 1 ? "Abrir o cheat que você salvou para este jogo" : $"Abrir os {_myCheats} cheats que você salvou para este jogo";
            return;
        }
        if (!has) return;
        CheatsBtnText.Text = _ownCheats.Count > 0 ? $"Cheats  ·  {_ownCheats.Count}" : "Cheats";
        CheatsBtn.ToolTip = linked != null
            ? $"Abrir trainer com a tabela vinculada ({linked.Name})"
            : _ownCheats.Count == 1 ? "Ver a tabela de cheats deste jogo" : $"Ver as {_ownCheats.Count} tabelas de cheats deste jogo";
    }

    async void OnHeroCheats(object sender, RoutedEventArgs e)
    {
        // a linked table goes straight to the trainer; otherwise show this game's tables
        if (LinkedTable() is { } linked) { await OpenTrainer(linked); return; }
        if (_ownCheats.Count == 0 && _myCheats > 0) { OpenScanner(); return; }
        if (_ownCheats.Count == 1) { OpenTableDialog(_ownCheats[0]); return; }
        OnAllCheats(sender, e);
    }

    void OpenTableDialog(CheatTable t)
    {
        var dlg = new CheatTableDialog(t, Hub.Store.Find(_id));
        dlg.Unloaded += (_, _) => UpdateCheats();
        MainWindow.Instance.ShowModal(dlg);
    }

    void OnCheat(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CheatTable t) OpenTableDialog(t);
    }

    void OnAllCheats(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Push(new CheatsPage(Titles.Clean(_name), Hub.Store.Find(_id)));

    async void OnOpenLinked(object sender, RoutedEventArgs e)
    {
        var t = LinkedTable();
        if (t != null) await OpenTrainer(t);
    }

    async Task OpenTrainer(CheatTable t)
    {
        try
        {
            var file = await Hub.Cheats.DownloadAsync(t);
            var g = Hub.Store.Find(_id);
            var hint = g?.Exe != null ? Path.GetFileName(g.Exe) : null;
            MainWindow.Instance.Push(new TrainerPage(Venix.Trainer.TrainerHub.Open(file, _name, hint)));
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    void OnUnlink(object sender, RoutedEventArgs e)
    {
        var g = Hub.Store.Find(_id);
        if (g == null) return;
        g.CheatTable = null;
        g.CheatsOnLaunch = false;
        Hub.Store.SaveSoon();
        UpdateCheats();
    }

    void OnCheatsOnLaunch(object sender, RoutedEventArgs e)
    {
        var g = Hub.Store.Find(_id);
        if (g == null) return;
        g.CheatsOnLaunch = CheatsOnLaunch.IsChecked == true;
        Hub.Store.SaveSoon();
    }

    CheatTable? LinkedTable()
    {
        var path = Hub.Store.Find(_id)?.CheatTable;
        return path == null ? null : Hub.Cheats.Tables.FirstOrDefault(t => t.Path == path);
    }

    void OnMore(object sender, RoutedEventArgs e)
    {
        _aboutExpanded = !_aboutExpanded;
        About.MaxHeight = _aboutExpanded ? double.PositiveInfinity : 190;
        MoreBtn.Content = _aboutExpanded ? "Mostrar menos" : "Mostrar mais";
    }

    void OnShot(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string url) return;
        var img = new Image { Stretch = Stretch.Uniform, MaxWidth = ActualWidth * 0.95, MaxHeight = ActualHeight * 0.9 };
        Img.SetUrls(img, url);
        var frame = new Border { CornerRadius = new CornerRadius(22), ClipToBounds = true, Child = img, Cursor = Cursors.Hand };
        frame.MouseLeftButtonUp += (_, _) => MainWindow.Instance.CloseModal();
        MainWindow.Instance.ShowModal(frame);
    }

    void OnShotsWheel(object sender, MouseWheelEventArgs e)
    {
        // vertical wheel scrolls the page, shift+wheel scrolls the strip
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            ShotsScroll.ScrollToHorizontalOffset(ShotsScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        e.Handled = true;
        var parent = (UIElement)VisualTreeHelper.GetParent(ShotsScroll);
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = ShotsScroll });
    }
}
