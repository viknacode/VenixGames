using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Venix.Core;
using Venix.Dialogs;
using Venix.Trainer;

namespace Venix.Pages;

public partial class CheatsPage : UserControl, IPage
{
    const int PageSize = 60;

    public string Title => "Cheats";
    public string Subtitle => "Tabelas de cheats da comunidade, aplicadas direto no jogo";
    public string NavKey => "cheats";

    readonly ObservableCollection<CheatTable> _items = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    readonly LibraryGame? _forGame;
    bool _grouped;
    int _offset, _total;
    bool _ready;

    public CheatsPage(string query = "", LibraryGame? forGame = null)
    {
        InitializeComponent();
        _forGame = forGame;
        List.ItemsSource = _items;
        Search.Text = query;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        Loaded += async (_, _) =>
        {
            UpdateRun();
            if (_ready) return;
            try { await Hub.Cheats.LoadAsync(); }
            catch (Exception ex) { Status.Text = "Não foi possível carregar o índice: " + ex.Message; return; }
            _ready = true;
            UpdateStats();
            Reload();
        };
    }

    void UpdateStats()
    {
        StatTables.Text = Format.Number(Hub.Cheats.Tables.Count);
        StatTablesSub.Text = $"{Hub.Cheats.GroupCount} jogos organizados em pastas" +
                             (Hub.Cheats.IndexedAt is { } d ? $"  ·  índice de {d:dd/MM HH:mm}" : "");
    }

    void UpdateRun()
    {
        var sessions = TrainerHub.All.ToList();
        var attached = sessions.Count(s => s.IsAttached);
        RunStatus.Text = attached > 0 ? $"{attached} jogo(s) conectado(s)" : sessions.Count > 0 ? $"{sessions.Count} trainer(s) aberto(s)" : "Pronto";
        RunDot.Fill = (Brush)FindResource(attached > 0 ? "Success" : "Accent");
        RunInfo.Text = sessions.Count > 0 ? string.Join("  ·  ", sessions.Select(s => s.Name)) : "Escolha uma tabela e clique em \"Ativar cheats\"";
        ResumeBtn.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Reload()
    {
        if (!_ready) return;
        _offset = 0;
        _items.Clear();
        LoadPage();
        Scroller.ScrollToTop();
    }

    void LoadPage()
    {
        var (total, items) = Hub.Cheats.Query(Search.Text, _grouped, _offset, PageSize);
        _total = total;
        _offset += items.Count;
        foreach (var t in items) _items.Add(t);
        ResultInfo.Text = $"{Format.Number(total)} tabela{(total == 1 ? "" : "s")}";
        Status.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = "Nenhuma tabela encontrada para essa busca.";
    }

    void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_ready && _offset < _total && e.VerticalOffset + e.ViewportHeight > e.ExtentHeight - 500) LoadPage();
    }

    void OnSearch(object sender, TextChangedEventArgs e) { _debounce.Stop(); _debounce.Start(); }

    void OnScope(object sender, RoutedEventArgs e)
    {
        _grouped = (string)((RadioButton)sender).Tag == "grouped";
        Reload();
    }

    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshBtn.IsEnabled = false;
        try
        {
            await Hub.Cheats.LoadAsync(force: true);
            UpdateStats();
            Reload();
            MainWindow.Instance.Toast("Índice de cheats atualizado");
        }
        catch (Exception ex) { MainWindow.Instance.Toast(ex.Message, true); }
        finally { RefreshBtn.IsEnabled = true; }
    }

    void OnResume(object sender, RoutedEventArgs e)
    {
        var s = TrainerHub.All.FirstOrDefault(x => x.IsAttached) ?? TrainerHub.All.FirstOrDefault();
        if (s != null) MainWindow.Instance.Push(new TrainerPage(s));
    }

    void OnTable(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CheatTable t) MainWindow.Instance.ShowModal(new CheatTableDialog(t, _forGame));
    }
}
