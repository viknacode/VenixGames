using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Venix.Core;
using Venix.Pages;

namespace Venix;

/// <summary>Implemented by every page shown in the main area.</summary>
public interface IPage
{
    string Title { get; }
    string Subtitle { get; }
    string NavKey { get; }
}

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;
    readonly Stack<FrameworkElement> _history = new();
    FrameworkElement? _current;
    readonly System.Windows.Threading.DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    bool _clearingSearch;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); LiveSearch(); };
        SearchFlyout.HistoryPicked += q => { GlobalSearch.Text = q; GlobalSearch.CaretIndex = q.Length; GlobalSearch.Focus(); };
        SearchFlyout.GamePicked += (appId, title) => { Controls.SearchFlyout.Remember(GlobalSearch.Text); FinishSearch(); OpenGame(appId, title); };
        SearchFlyout.SeeAllPicked += q => { Controls.SearchFlyout.Remember(q); FinishSearch(); OpenCatalog(q, true); };
        Deactivated += (_, _) => SearchPopup.IsOpen = false;
        LocationChanged += (_, _) => SearchPopup.IsOpen = false;
        SizeChanged += (_, _) => SearchPopup.IsOpen = false;
        SourceInitialized += (_, _) => { RoundCorners(); InitGamepad(); };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? Icons.Restore : Icons.Maximize;
        };

        var store = Hub.Store;
        SideLibrary.ItemsSource = store.Library;
        store.Library.CollectionChanged += (_, _) => UpdateSide();
        store.Downloads.CollectionChanged += (_, _) => UpdateDownloads();
        Hub.Torrents.Completed += d => Toast($"Download concluído: {d.GameTitle}");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateDownloads();
        timer.Start();
        UpdateSide();
        UpdateDownloads();

        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--page");
            Go(i > 0 && i + 1 < args.Length ? args[i + 1] : store.Settings.StartOnLibrary ? "library" : "home");
            // sources were loaded by the splash screen
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) Back();
            // a click anywhere outside the search box and its dropdown closes the dropdown
            if (SearchPopup.IsOpen && e.OriginalSource is DependencyObject d && !IsWithin(d, SearchFlyout) && !IsWithin(d, GlobalSearch) && !IsWithin(d, SearchClear))
                SearchPopup.IsOpen = false;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) CloseModal(); };
    }

    /// <summary>Re-reads the sources folder (next to Venix.exe) and reports what changed.</summary>
    public async Task ApplySourceConfig(bool quiet = false)
    {
        try { ReportSources(await Hub.Sources.ApplyConfigAsync(), quiet); }
        catch (Exception ex) { Toast("Fontes: " + ex.Message, true); }
    }

    /// <summary>Toast for a sources-folder sync (null = the splash was skipped and lists are still syncing).</summary>
    public void ReportSources((int added, int removed, int failed)? result, bool quiet = true)
    {
        if (result is not { } r) { Toast("Algumas listas ainda estão sincronizando em segundo plano."); return; }
        var (added, removed, failed) = r;
        var parts = new List<string>();
        if (added > 0) parts.Add($"{added} adicionada{(added > 1 ? "s" : "")}");
        if (removed > 0) parts.Add($"{removed} removida{(removed > 1 ? "s" : "")}");
        if (failed > 0) Toast($"{failed} fonte(s) com erro ao sincronizar. Veja em Ajustes → Fontes.", true);
        else if (parts.Count > 0) Toast("Fontes: " + string.Join(", ", parts));
        else if (!quiet) Toast("Fontes sincronizadas");
    }

    /* ---------------------------------------------------------- navigation */

    public void Go(string key)
    {
        if (key.StartsWith("trainer:")) { _history.Clear(); var f = key[8..]; Show(new TrainerPage(Venix.Trainer.TrainerHub.Open(f, System.IO.Path.GetFileNameWithoutExtension(f)))); return; }
        if (key.StartsWith("game:") && int.TryParse(key[5..], out var gid)) { _history.Clear(); Show(new GamePage(gid, "")); return; }
        if (key.StartsWith("settings:")) { _history.Clear(); Show(new SettingsPage(key[9..])); return; }
        _history.Clear();
        Show(key switch
        {
            "catalog" => new CatalogPage(),
            "updates" => new UpdatesPage(),
            "library" => new LibraryPage(),
            "downloads" => new DownloadsPage(),
            "settings" => new SettingsPage(),
            "cheats" => new CheatsPage(),
            _ => new HomePage()
        });
    }

    public void Push(FrameworkElement page)
    {
        if (_current != null) _history.Push(_current);
        Show(page);
    }

    public void OpenGame(int? appId, string title) => Push(new GamePage(appId, title));

    public void OpenCatalog(string query, bool focusSearch = false)
    {
        _history.Clear();
        Show(new CatalogPage(query, focusSearch));
    }

    public void Back()
    {
        if (Overlay.Visibility == Visibility.Visible) { CloseModal(); return; }
        if (_history.Count == 0) return;
        Show(_history.Pop());
    }

    void Show(FrameworkElement page)
    {
        _current = page;
        PageHost.Content = page;
        var p = page as IPage;
        PageTitle.Text = p?.Title ?? "";
        PageSubtitle.Text = p?.Subtitle ?? "";
        PageSubtitle.Visibility = string.IsNullOrEmpty(PageSubtitle.Text) ? Visibility.Collapsed : Visibility.Visible;
        BackBtn.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (rb, key) in new[] { (NavHome, "home"), (NavCatalog, "catalog"), (NavUpdates, "updates"), (NavLibrary, "library"), (NavDownloads, "downloads"), (NavSettings, "settings"), (NavCheats, "cheats") })
            rb.IsChecked = p?.NavKey == key;
        Ui.FadeIn(page, 14, 300);
    }

    public void RefreshHeader()
    {
        if (_current is IPage p)
        {
            PageTitle.Text = p.Title;
            PageSubtitle.Text = p.Subtitle;
            PageSubtitle.Visibility = string.IsNullOrEmpty(p.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    void OnNav(object sender, RoutedEventArgs e)
    {
        var key = sender == NavCatalog ? "catalog" : sender == NavUpdates ? "updates" : sender == NavLibrary ? "library" : sender == NavDownloads ? "downloads" : sender == NavSettings ? "settings" : sender == NavCheats ? "cheats" : "home";
        Go(key);
    }

    void OnBack(object sender, RoutedEventArgs e) => Back();

    void OnSideGame(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LibraryGame g) OpenGame(g.AppId, g.Title);
    }

    /* ---------------------------------------------------------- title-bar search */

    static bool IsWithin(DependencyObject? d, DependencyObject root)
    {
        while (d != null)
        {
            if (d == root) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    void OpenSearchPopup()
    {
        SearchFlyout.SetQuery(GlobalSearch.Text);
        SearchPopup.IsOpen = true;
    }

    void OnSearchFocus(object sender, KeyboardFocusChangedEventArgs e) => OpenSearchPopup();
    void OnSearchBlur(object sender, KeyboardFocusChangedEventArgs e) => SearchPopup.IsOpen = false;

    /// <summary>Clicking the box again after Esc reopens the dropdown.</summary>
    void OnSearchClick(object sender, MouseButtonEventArgs e)
    {
        if (GlobalSearch.IsKeyboardFocused && !SearchPopup.IsOpen) OpenSearchPopup();
    }

    void OnSearchClear(object sender, RoutedEventArgs e)
    {
        GlobalSearch.Text = "";
        GlobalSearch.Focus();
    }

    /// <summary>Clears the box and closes the dropdown after a result was opened.</summary>
    void FinishSearch()
    {
        _searchDebounce.Stop();
        SearchPopup.IsOpen = false;
        _clearingSearch = true;
        GlobalSearch.Text = "";
        _clearingSearch = false;
        SearchClear.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    /// <summary>Suggestions refresh 150 ms after the last key; the box keeps focus.</summary>
    void OnGlobalSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchClear.Visibility = GlobalSearch.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_clearingSearch) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    void LiveSearch()
    {
        var q = GlobalSearch.Text.Trim();
        SearchFlyout.SetQuery(q);
        if (!SearchPopup.IsOpen && GlobalSearch.IsKeyboardFocused) SearchPopup.IsOpen = true;
        // when the catalog is already on screen it follows the box live
        if (_current is CatalogPage cp) cp.SetQuery(q);
    }

    /// <summary>↑/↓ move through the dropdown, Enter opens the selection (or the first suggestion), Esc closes/clears.</summary>
    void OnGlobalSearch(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                if (!SearchPopup.IsOpen) OpenSearchPopup();
                SearchFlyout.Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            case Key.Escape:
                if (SearchPopup.IsOpen) SearchPopup.IsOpen = false;
                else GlobalSearch.Text = "";
                e.Handled = true;
                return;
            case Key.Enter:
                break;
            default:
                return;
        }
        _searchDebounce.Stop();
        var q = GlobalSearch.Text.Trim();
        // the dropdown may still hold the previous text if Enter came within the debounce window
        if (SearchFlyout.Query != q) SearchFlyout.SetQuery(q);
        if (SearchPopup.IsOpen && SearchFlyout.Activate()) return;
        if (q.Length == 0) return;
        Controls.SearchFlyout.Remember(q);
        FinishSearch();
        if (_current is CatalogPage cp) { cp.SetQuery(q); cp.FocusSearch(); }
        else OpenCatalog(q, true);
    }

    void OnMiniDownload(object sender, MouseButtonEventArgs e) => Go("downloads");

    /* ---------------------------------------------------------- sidebar state */

    void UpdateSide()
    {
        var n = Hub.Store.Library.Count;
        LibCount.Text = n > 0 ? n.ToString() : "";
        SideEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateDownloads()
    {
        var active = Hub.Store.Downloads.Where(d => d.IsActive).ToList();
        NavDownloads.SetValue(Ui.BadgeProperty, active.Count > 0 ? MakeBadge(active.Count) : null);
        var d = active.FirstOrDefault();
        MiniDownload.Visibility = d != null ? Visibility.Visible : Visibility.Collapsed;
        if (d == null) return;
        MiniTitle.Text = d.GameTitle;
        MiniSpeed.Text = d.SpeedLabel;
        MiniBar.Value = d.ProgressPercent;
        MiniPct.Text = $"{d.ProgressPercent:0}%";
    }

    static Border MakeBadge(int n) => new()
    {
        Background = (Brush)Application.Current.Resources["Accent"],
        CornerRadius = new CornerRadius(9),
        MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0),
        Child = new TextBlock { Text = n.ToString(), FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
    };

    /* ---------------------------------------------------------- gamepad */

    /// <summary>Controller navigation of the launcher (created once the window has a handle).</summary>
    public GamepadNavigation? GamepadNav { get; private set; }

    void InitGamepad()
    {
        var pads = Hub.Gamepad;
        pads.Notify = (text, error) => Toast(text, error);
        pads.Changed += UpdateGamepadChip;
        pads.Connected += p => Toast(Hub.Store.Settings.GamepadNavigation ? $"Controle {p.KindLabel} conectado · pressione A para navegar" : $"Controle {p.KindLabel} conectado");
        pads.Disconnected += p => Toast($"Controle {p.KindLabel} desconectado");
        pads.Attach(this);
        GamepadNav = new GamepadNavigation(this, pads);
        UpdateGamepadChip();
    }

    void UpdateGamepadChip()
    {
        var pads = Hub.Gamepad;
        var p = pads.Primary;
        if (p == null) { GamepadChip.Visibility = Visibility.Collapsed; return; }
        GamepadChip.Visibility = Visibility.Visible;
        var n = pads.Controllers.Count;
        GamepadChipText.Text = n > 1 ? $"{n} controles · {p.KindLabel}" : $"Controle conectado · {p.KindLabel}";
        if ((GamepadChipArt.Source as System.Windows.Media.Imaging.BitmapImage)?.UriSource != p.ArtUri)
        {
            try { GamepadChipArt.Source = new System.Windows.Media.Imaging.BitmapImage(p.ArtUri); }
            catch { GamepadChipArt.Source = null; }
        }
        GamepadChipArt.Visibility = GamepadChipArt.Source != null ? Visibility.Visible : Visibility.Collapsed;
        GamepadChipBattery.Text = p.BatteryGlyph;
        GamepadChipBattery.Visibility = p.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        GamepadChip.ToolTip = string.Join("  ·  ", new[] { p.Name, p.Wireless ? "Sem fio" : null, p.BatteryLabel.Length > 0 ? p.BatteryLabel : null, pads.Emulating ? "Emulando controle Xbox" : null }.Where(s => !string.IsNullOrEmpty(s)));
    }

    void OnGamepadChip(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Go("settings:gamepad");
    }

    /* ---------------------------------------------------------- overlay & toasts */

    public void ShowModal(FrameworkElement content)
    {
        OverlayHost.Content = content;
        Overlay.Visibility = Visibility.Visible;
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        Shell.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, 14, TimeSpan.FromMilliseconds(220)));
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var s = new ScaleTransform(0.96, 0.96);
        content.RenderTransformOrigin = new Point(0.5, 0.5);
        content.RenderTransform = s;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    public void CloseModal()
    {
        Overlay.Visibility = Visibility.Collapsed;
        OverlayHost.Content = null;
        Shell.Effect = null;
    }

    void OnScrimClick(object sender, MouseButtonEventArgs e) => CloseModal();

    public void Toast(string text, bool error = false)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Glass"),
            Background = (Brush)FindResource(error ? "Danger" : "Ink"),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = text, Foreground = error ? Brushes.White : (Brush)FindResource("Ink.Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
        };
        Toasts.Children.Add(card);
        Ui.FadeIn(card, 16, 260);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 6 : 3.8) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => Toasts.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        };
        t.Start();
    }

    /* ---------------------------------------------------------- window chrome */

    void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.ClickCount == 2) { OnMax(sender, e); return; }
        try { DragMove(); } catch { }
    }

    void OnMin(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMax(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void RoundCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND (Windows 11)
        try { DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch { }
    }
}
