using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Venix.Core;
using Venix.Dialogs;

namespace Venix.Pages;

public partial class SettingsPage : UserControl, IPage
{
    public string Title => "Ajustes";
    public string Subtitle => "Preferências, fontes e downloads";
    public string NavKey => "settings";

    bool _init;

    public SettingsPage(string tab = "general")
    {
        InitializeComponent();
        var s = Hub.Store.Settings;
        (s.Theme == "Dark" ? ThemeDark : ThemeLight).IsChecked = true;
        DownloadDir.Text = s.DownloadDir;
        StartOnLibrary.IsChecked = s.StartOnLibrary;
        Seed.IsChecked = s.SeedAfterDownload;
        AddTrackers.IsChecked = s.AddPublicTrackers;
        TrackerList.Text = string.Join(Environment.NewLine, s.Trackers);
        SpeedLimit.Text = s.SpeedLimitMbps.ToString();
        SourceList.ItemsSource = Hub.Store.Sources;
        Version.Text = $"Versão {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}";
        DataPath.Text = $"Dados em {AppStore.DataDir}";
        UpdateEmpty();
        UpdateConfigInfo();
        Hub.Store.Sources.CollectionChanged += (_, _) => UpdateEmpty();
        GamepadNavToggle.IsChecked = s.GamepadNavigation;
        GamepadLaunchToggle.IsChecked = s.GamepadLaunchSupport;
        BuildGamepadHints();
        UpdateGamepad();
        Loaded += (_, _) => { Hub.Gamepad.Changed += UpdateGamepad; UpdateGamepad(); };
        Unloaded += (_, _) => Hub.Gamepad.Changed -= UpdateGamepad;
        _init = true;
        (tab switch { "sources" => TabSources, "gamepad" => TabGamepad, _ => TabGeneral }).IsChecked = true;
    }

    void UpdateEmpty() => NoSources.Visibility = Hub.Store.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        General.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SourcesPanel.Visibility = TabSources.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPanel.Visibility = TabDownloads.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GamepadPanel.Visibility = TabGamepad.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = TabAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var visible = new FrameworkElement[] { General, SourcesPanel, DownloadsPanel, GamepadPanel, AboutPanel }.First(p => p.Visibility == Visibility.Visible);
        Ui.FadeIn(visible, 10, 260);
    }

    /* ---------------------------------------------------------- gamepad */

    void UpdateGamepad()
    {
        var pads = Hub.Gamepad;
        var list = pads.Controllers;
        var primary = pads.Primary;
        GamepadCount.Text = list.Count == 0 ? "Nenhum" : list.Count == 1 ? "1 conectado" : $"{list.Count} conectados";
        GamepadStatus.Text = primary == null
            ? "Nenhum controle conectado."
            : string.Join("  ·  ", new[] { $"{primary.Name} ({primary.KindLabel})", primary.Wireless ? "sem fio" : "USB", primary.BatteryLabel.Length > 0 ? primary.BatteryLabel.ToLowerInvariant() : null,
                primary.IsXInput ? "XInput nativo" : pads.Emulating ? "emulando controle Xbox" : "HID" }.Where(s => !string.IsNullOrEmpty(s)));
        NoGamepad.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (primary != null && (GamepadArt.Source as System.Windows.Media.Imaging.BitmapImage)?.UriSource != primary.ArtUri)
        {
            try { GamepadArt.Source = new System.Windows.Media.Imaging.BitmapImage(primary.ArtUri); }
            catch { GamepadArt.Source = null; }
        }
        GamepadArt.Visibility = primary != null && GamepadArt.Source != null ? Visibility.Visible : Visibility.Collapsed;

        PrimaryList.Children.Clear();
        foreach (var pad in list)
        {
            var rb = new RadioButton { Style = (Style)FindResource("FilterChip"), GroupName = "pad", IsChecked = pad == primary, Tag = pad };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Style = (Style)FindResource("T.Icon"), Text = Icons.Game, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = $"{pad.Name}  ·  {pad.KindLabel}", VerticalAlignment = VerticalAlignment.Center });
            if (pad.HasBattery) sp.Children.Add(new TextBlock { Style = (Style)FindResource("T.Icon"), Text = pad.BatteryGlyph, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), ToolTip = pad.BatteryLabel });
            rb.Content = sp;
            rb.Checked += (_, _) => { if (_init && rb.Tag is GamepadInfo g && g != Hub.Gamepad.Primary) Hub.Gamepad.SetPrimary(g); };
            PrimaryList.Children.Add(rb);
        }

        var installed = ViGEm.IsInstalled;
        ViGEmDot.Background = (System.Windows.Media.Brush)FindResource(installed ? "Success" : "Danger");
        ViGEmStatus.Text = installed
            ? "Driver ViGEmBus instalado: controles PlayStation, Switch e genéricos são apresentados ao jogo como controle Xbox."
            : "Driver ViGEmBus não encontrado. Sem ele, só controles Xbox funcionam em jogos que exigem XInput.";
        ViGEmBtn.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    void BuildGamepadHints()
    {
        GamepadHints.Children.Clear();
        foreach (var (key, what) in new[] { ("D-pad / analógico", "mover"), ("A", "selecionar"), ("B", "voltar"), ("Y", "buscar"), ("LB / RB", "trocar seção"), ("Start", "jogar"), ("Analógico direito", "rolar") })
        {
            var chip = new Border { Style = (Style)FindResource("Chip"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 8) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("Text") });
            sp.Children.Add(new TextBlock { Text = "  " + what, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Text.3") });
            chip.Child = sp;
            GamepadHints.Children.Add(chip);
        }
    }

    void OnGamepadToggle(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        var s = Hub.Store.Settings;
        s.GamepadLaunchSupport = GamepadLaunchToggle.IsChecked == true;
        if (MainWindow.Instance.GamepadNav is { } nav) nav.SetEnabled(GamepadNavToggle.IsChecked == true);
        else s.GamepadNavigation = GamepadNavToggle.IsChecked == true;
        Hub.Store.SaveSoon();
    }

    void OnViGEm(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ViGEm.Homepage) { UseShellExecute = true }); }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    /* ---------------------------------------------------------- general */

    void OnTheme(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        var theme = (string)((RadioButton)sender).Tag;
        Hub.Store.Settings.Theme = theme;
        App.ApplyTheme(theme);
        Hub.Store.SaveSoon();
    }

    void OnPickDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta de downloads", InitialDirectory = Hub.Store.Settings.DownloadDir };
        if (dlg.ShowDialog() != true) return;
        Hub.Store.Settings.DownloadDir = dlg.FolderName;
        DownloadDir.Text = dlg.FolderName;
        Hub.Store.SaveSoon();
    }

    void OnToggle(object sender, RoutedEventArgs e)
    {
        Hub.Store.Settings.StartOnLibrary = StartOnLibrary.IsChecked == true;
        Hub.Store.Settings.SeedAfterDownload = Seed.IsChecked == true;
        Hub.Store.SaveSoon();
    }

    /* ---------------------------------------------------------- downloads */

    void OnTrackersToggle(object sender, RoutedEventArgs e)
    {
        Hub.Store.Settings.AddPublicTrackers = AddTrackers.IsChecked == true;
        Hub.Store.SaveSoon();
    }

    void OnTrackersChanged(object sender, RoutedEventArgs e)
    {
        Hub.Store.Settings.Trackers = TrackerList.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "udp" or "http" or "https" or "wss")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Hub.Store.SaveSoon();
    }

    void OnTrackersReset(object sender, RoutedEventArgs e)
    {
        Hub.Store.Settings.Trackers = AppSettings.DefaultTrackers.ToList();
        TrackerList.Text = string.Join(Environment.NewLine, Hub.Store.Settings.Trackers);
        Hub.Store.SaveSoon();
    }

    void OnDigits(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    async void OnApplySpeed(object sender, RoutedEventArgs e)
    {
        int.TryParse(SpeedLimit.Text, out var v);
        Hub.Store.Settings.SpeedLimitMbps = Math.Max(0, v);
        SpeedLimit.Text = Hub.Store.Settings.SpeedLimitMbps.ToString();
        Hub.Store.SaveSoon();
        await Hub.Torrents.ApplySettingsAsync();
        MainWindow.Instance.Toast(v > 0 ? $"Limite de {v} Mbps aplicado" : "Velocidade ilimitada");
    }

    /* ---------------------------------------------------------- sources */

    void UpdateConfigInfo()
    {
        var n = SourceService.ReadConfig().Count;
        ConfigInfo.Text = $"{n} arquivo{(n == 1 ? "" : "s")} .json  ·  {SourceService.ConfigFolder}";
    }

    void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        SourceService.ReadConfig(); // creates the folder if missing
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SourceService.ConfigFolder) { UseShellExecute = true }); }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    async void OnReloadConfig(object sender, RoutedEventArgs e)
    {
        ReloadBtn.IsEnabled = false;
        await MainWindow.Instance.ApplySourceConfig();
        UpdateConfigInfo();
        ReloadBtn.IsEnabled = true;
    }

    void OnOpenSourceUrl(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadSource { Url: { } url } && url.StartsWith("http"))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnOpenSourceFolder(object sender, RoutedEventArgs e)
    {
        SourceService.ReadConfig(); // creates the folder
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SourceService.ConfigFolder) { UseShellExecute = true });
    }

    void OnUrlKey(object sender, KeyEventArgs e)
    {
        // Enter adds; Shift+Enter inserts a new line
        if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift) return;
        e.Handled = true;
        OnAdd(sender, e);
    }

    async void OnAdd(object sender, RoutedEventArgs e)
    {
        var urls = NewUrl.Text.Split(new[] { '\n', '\r', ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(u => u.Trim()).Distinct().ToList();
        if (urls.Count == 0) return;
        AddBtn.IsEnabled = false;
        int ok = 0, failed = 0;
        var errors = new List<string>();
        try
        {
            foreach (var url in urls)
            {
                try
                {
                    var src = await Hub.Sources.AddAsync(url);
                    if (src.HasError) { failed++; errors.Add($"{src.Name}: {src.Error}"); }
                    else ok++;
                }
                catch (Exception ex) { failed++; errors.Add($"{url}: {ex.Message}"); }
            }
            NewUrl.Text = "";
            if (failed == 0) MainWindow.Instance.Toast(ok == 1 ? "Fonte adicionada" : $"{ok} fontes adicionadas");
            else MainWindow.Instance.Toast($"{ok} sincronizada(s), {failed} com erro. {errors.First()}", true);
        }
        finally { AddBtn.IsEnabled = true; }
    }

    async void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Importar fonte", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var src = await Hub.Sources.ImportAsync(dlg.FileName);
            MainWindow.Instance.Toast($"{src.Name}: {Format.Number(src.Count)} downloads importados");
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    async void OnSyncAll(object sender, RoutedEventArgs e)
    {
        await Hub.Sources.SyncAsync();
        MainWindow.Instance.Toast("Fontes sincronizadas");
    }

    async void OnSyncOne(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadSource s) await Hub.Sources.SyncAsync(s);
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadSource s) return;
        if (s.FromConfig)
        {
            // it would come back on the next start; the file is the source of truth
            MainWindow.Instance.Toast("Esta lista vem da pasta sources. Apague o arquivo .json da pasta e sincronize.");
            return;
        }
        MainWindow.Instance.ShowModal(new ConfirmDialog("Remover fonte?", $"\"{s.Name}\" e suas {Format.Number(s.Count)} opções sairão do catálogo.",
            "Remover fonte", "Manter", async remove => { if (remove) await Hub.Sources.RemoveAsync(s); }));
    }
}
