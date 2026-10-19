using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Venix.Core;
using Venix.Dialogs;

namespace Venix.Pages;

public partial class DownloadsPage : UserControl, IPage
{
    public string Title => "Downloads";
    public string Subtitle => "Fila, progresso e histórico";
    public string NavKey => "downloads";

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public DownloadsPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateSummary();
        Loaded += (_, _) =>
        {
            Hub.Store.Downloads.CollectionChanged += OnChanged;
            foreach (var d in Hub.Store.Downloads) d.PropertyChanged += OnItemChanged;
            Rebuild();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            Hub.Store.Downloads.CollectionChanged -= OnChanged;
            foreach (var d in Hub.Store.Downloads) d.PropertyChanged -= OnItemChanged;
            _timer.Stop();
        };
    }

    void OnChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        foreach (DownloadItem d in e.NewItems ?? Array.Empty<DownloadItem>()) d.PropertyChanged += OnItemChanged;
        foreach (DownloadItem d in e.OldItems ?? Array.Empty<DownloadItem>()) d.PropertyChanged -= OnItemChanged;
        Rebuild();
    }

    void OnItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadItem.Status)) Rebuild();
    }

    void Rebuild()
    {
        var all = Hub.Store.Downloads;
        var queue = all.Where(d => !d.IsComplete).ToList();
        var done = all.Where(d => d.IsComplete).OrderByDescending(d => d.CompletedAt).ToList();
        ActiveList.ItemsSource = queue;
        DoneList.ItemsSource = done;
        Empty.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveHeader.Visibility = queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneHeader.Visibility = done.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var all = Hub.Store.Downloads;
        TotalSpeed.Text = Format.Speed(all.Sum(d => d.Speed));
        ActiveCount.Text = all.Count(d => d.IsActive).ToString();
        DoneCount.Text = all.Count(d => d.IsComplete).ToString();
    }

    static DownloadItem? Item(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    async void OnPause(object sender, RoutedEventArgs e) { if (Item(sender) is { } d) await Hub.Torrents.PauseAsync(d); }
    async void OnResume(object sender, RoutedEventArgs e) { if (Item(sender) is { } d) await Hub.Torrents.ResumeAsync(d); }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } d) return;
        var target = d.Name != null ? Path.Combine(d.SavePath, d.Name) : d.SavePath;
        if (File.Exists(target) || Directory.Exists(target)) Process.Start("explorer.exe", $"/select,\"{target}\"");
        else if (Directory.Exists(d.SavePath)) Process.Start(new ProcessStartInfo(d.SavePath) { UseShellExecute = true });
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } d) return;
        if (d.IsComplete)
        {
            _ = Hub.Torrents.RemoveAsync(d, false);
            return;
        }
        MainWindow.Instance.ShowModal(new ConfirmDialog(
            "Cancelar download?",
            $"O download de {d.GameTitle} será removido da fila.",
            "Cancelar e apagar arquivos", "Cancelar e manter arquivos",
            async deleteFiles => await Hub.Torrents.RemoveAsync(d, deleteFiles)));
    }
}
