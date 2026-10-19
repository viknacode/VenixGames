using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Venix.Core;

namespace Venix.Dialogs;

public partial class DownloadOptionsDialog : UserControl
{
    readonly string _gameId;
    readonly int? _appId;
    readonly string _name;
    readonly List<SourceEntry> _all;
    string? _source;
    SourceEntry? _picked;
    string _savePath = Hub.Store.Settings.DownloadDir;

    public DownloadOptionsDialog(string gameId, int? appId, string name, List<SourceEntry> entries)
    {
        InitializeComponent();
        _gameId = gameId;
        _appId = appId;
        _name = name;
        _all = entries;
        Sub.Text = $"{name}  ·  {Summary(entries)}";
        SavePath.Text = _savePath;

        void AddChip(string label, string? value)
        {
            var rb = new RadioButton { Style = (Style)FindResource("FilterChip"), Content = label, GroupName = "dsrc", IsChecked = value == null, Height = 30, Padding = new Thickness(14, 0, 14, 0) };
            rb.Checked += (_, _) => { _source = value; Apply(); };
            Sources.Children.Add(rb);
        }
        var names = entries.Select(e => e.SourceName).Distinct().ToList();
        if (names.Count > 1)
        {
            AddChip("Todas", null);
            foreach (var n in names) AddChip(n, n);
        }
        Apply();
    }

    /// <summary>"3 opções de jogo  ·  2 atualizações  ·  1 fix" (kinds that are absent are left out).</summary>
    static string Summary(List<SourceEntry> entries)
    {
        int games = entries.Count(e => e.Kind == EntryKind.Game), ups = entries.Count(e => e.Kind == EntryKind.Update), fixes = entries.Count(e => e.Kind == EntryKind.Fix);
        var parts = new List<string>();
        if (games > 0) parts.Add($"{games} opç{(games > 1 ? "ões" : "ão")} de jogo");
        if (ups > 0) parts.Add($"{ups} atualizaç{(ups > 1 ? "ões" : "ão")}");
        if (fixes > 0) parts.Add($"{fixes} fix{(fixes > 1 ? "es" : "")}");
        return parts.Count > 0 ? string.Join("  ·  ", parts) : "nenhuma opção";
    }

    void Apply()
    {
        var q = Titles.Normalize(Filter.Text);
        var items = _all.Where(e => (_source == null || e.SourceName == _source) && (q.Length == 0 || Titles.Normalize(e.Title).Contains(q)))
            .OrderBy(e => e.Kind)                                                    // Jogo, Atualizações, Fixes
            .ThenByDescending(e => e.UploadDate ?? "", StringComparer.Ordinal)
            .ToList();
        // one section per kind, in that order; a kind with no entries simply has no section
        var view = new ListCollectionView(items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceEntry.Section)));
        List.ItemsSource = view;
        _picked = null;
        Go.IsEnabled = false;
    }

    void OnFilter(object sender, TextChangedEventArgs e) => Apply();

    void OnPick(object sender, RoutedEventArgs e)
    {
        _picked = (sender as FrameworkElement)?.DataContext as SourceEntry;
        Go.IsEnabled = _picked != null;
    }

    void OnChangePath(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta do download", InitialDirectory = _savePath };
        if (dlg.ShowDialog() != true) return;
        _savePath = dlg.FolderName;
        SavePath.Text = _savePath;
    }

    async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_picked == null) return;
        var uri = _picked.Uris.FirstOrDefault(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) ?? _picked.Uris.FirstOrDefault();
        if (uri == null) { MainWindow.Instance.Toast("Esta opção não tem link.", true); return; }

        Hub.Store.AddToLibrary(_gameId, _appId, _name);
        MainWindow.Instance.CloseModal();

        if (!uri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            // hosted/direct links open in the default browser
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            {
                MainWindow.Instance.Toast("Tipo de link não suportado.", true);
                return;
            }
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            MainWindow.Instance.Toast("Link aberto no navegador.");
            return;
        }
        await Hub.Torrents.StartAsync(_picked, uri, _gameId, _appId, _name, _savePath);
        MainWindow.Instance.Toast($"Download iniciado: {_name}");
    }

    void OnClose(object sender, RoutedEventArgs e) => MainWindow.Instance.CloseModal();
}
