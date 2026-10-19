using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Venix.Core;

namespace Venix.Dialogs;

public partial class CheatTableDialog : UserControl
{
    readonly CheatTable _table;
    LibraryGame? _target;

    public CheatTableDialog(CheatTable table, LibraryGame? forGame = null)
    {
        InitializeComponent();
        _table = table;
        _target = forGame;
        NameText.Text = table.Name;
        FileText.Text = table.IsBundled ? $"{table.FileName}  ·  {table.SizeLabel}  ·  incluída no Venix" : $"{table.Path}  ·  {table.SizeLabel}";
        GithubBtn.Visibility = table.IsBundled ? Visibility.Collapsed : Visibility.Visible;
        BuildLibraryChips();
        Loaded += async (_, _) => await LoadDetails();
    }

    async Task LoadDetails()
    {
        try
        {
            var d = await Hub.Cheats.DetailsAsync(_table);
            Loading.Visibility = Visibility.Collapsed;
            Entries.ItemsSource = d.Entries;
            AddChip(Icons.Cheat, $"{d.CheatCount} cheat{(d.CheatCount == 1 ? "" : "s")}");
            if (d.Process != null) AddChip(Icons.Game, d.Process);
            if (d.HasLua) AddChip(Icons.Code, "Contém scripts Lua", warn: true);
            if (d.Entries.Count == 0) { Loading.Visibility = Visibility.Visible; Loading.Text = "Não foi possível listar os cheats desta tabela."; }
        }
        catch (Exception ex)
        {
            Loading.Text = "Falha ao baixar a tabela: " + ex.Message;
        }
    }

    void AddChip(string icon, string text, bool warn = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Style = (Style)FindResource("T.Icon"), Text = icon, FontSize = 11, Margin = new Thickness(0, 0, 7, 0), Foreground = (Brush)FindResource(warn ? "Accent" : "Text.3") });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource("Text.2") });
        Chips.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = sp });
    }

    void BuildLibraryChips()
    {
        var lib = Hub.Store.Library;
        if (lib.Count == 0) { LinkPanel.Visibility = Visibility.Collapsed; LinkBtn.Visibility = Visibility.Collapsed; return; }
        foreach (var g in lib.OrderBy(g => g.Title))
        {
            var linked = g.CheatTable == _table.Path;
            var rb = new RadioButton
            {
                Style = (Style)FindResource("FilterChip"), GroupName = "cheatlink", Height = 30, Padding = new Thickness(14, 0, 14, 0),
                Content = linked ? $"{g.Title}  ✓" : g.Title, IsChecked = g == _target, Tag = g
            };
            rb.Checked += (_, _) => { _target = (LibraryGame)rb.Tag; LinkBtn.IsEnabled = true; };
            LibraryChips.Children.Add(rb);
        }
        LinkBtn.IsEnabled = _target != null;
    }

    void OnLink(object sender, RoutedEventArgs e)
    {
        if (_target == null) return;
        _target.CheatTable = _table.Path;
        Hub.Store.SaveSoon();
        MainWindow.Instance.Toast($"Tabela vinculada a {_target.Title}");
        MainWindow.Instance.CloseModal();
    }

    /// <summary>Runs the table inside Venix (memory trainer).</summary>
    async void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await Hub.Cheats.DownloadAsync(_table);
            var hint = _target?.Exe != null ? Path.GetFileName(_target.Exe) : null;
            var session = Venix.Trainer.TrainerHub.Open(file, _target?.Title ?? _table.Name, hint);
            MainWindow.Instance.CloseModal();
            MainWindow.Instance.Push(new Pages.TrainerPage(session));
        }
        catch (Exception ex) { MainWindow.Instance.Toast("Não foi possível abrir a tabela: " + ex.Message, true); }
    }

    async void OnFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await Hub.Cheats.DownloadAsync(_table);
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
    }

    void OnGithub(object sender, RoutedEventArgs e)
    {
        if (CheatService.RepoUrl(_table) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnClose(object sender, RoutedEventArgs e) => MainWindow.Instance.CloseModal();
}
