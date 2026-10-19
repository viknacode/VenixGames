using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Venix.Core;

/// <summary>
/// Notification-area presence of the launcher: the icon with a live tooltip ("Venix · 2 downloads · 5,3 MB/s"),
/// its context menu, and the "keep downloading in the tray" behaviour when the main window is closed
/// while a download is active. Torrents keep running because the window is only hidden, never closed.
/// </summary>
public sealed class TrayService : IDisposable
{
    readonly MainWindow _win;
    readonly TrayIcon _icon;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly ContextMenu _menu;
    readonly MenuItem _pauseItem;
    bool _hintShown;

    /// <summary>"Sair" was chosen (or the app is shutting down for real): closing the window must not hide it.</summary>
    public bool Exiting { get; private set; }

    public TrayService(MainWindow win)
    {
        _win = win;
        _icon = new TrayIcon("Venix");
        _icon.Activated += Restore;
        _icon.ContextRequested += OpenMenu;

        _menu = new ContextMenu { Style = (Style)Application.Current.FindResource("Tray.Menu"), Placement = PlacementMode.MousePoint };
        _menu.Items.Add(Item("Abrir o Venix", Restore));
        _pauseItem = Item("Pausar todos os downloads", PauseOrResumeAll);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("Tray.Separator") });
        _menu.Items.Add(Item("Sair", Exit));
        _menu.Opened += (_, _) => UpdateMenu();

        Hub.Torrents.Completed += OnCompleted;
        _timer.Tick += (_, _) => UpdateTip();
        _timer.Start();
        UpdateTip();
    }

    static MenuItem Item(string header, Action click)
    {
        var mi = new MenuItem { Header = header, Style = (Style)Application.Current.FindResource("Tray.MenuItem") };
        mi.Click += (_, _) => click();
        return mi;
    }

    /* ---------------------------------------------------------- window */

    /// <summary>
    /// Called from the window's Closing: with an active download (and the setting on) the window hides
    /// instead and the download goes on in the tray. Returns true when the close was taken over.
    /// </summary>
    public bool TryHideOnClose()
    {
        if (Exiting || !Hub.Store.Settings.CloseToTray) return false;
        if (!Hub.Store.Downloads.Any(d => d.IsActive)) return false;
        HideToTray();
        if (!_hintShown)
        {
            _hintShown = true;
            _icon.Notify("Venix", "O Venix continua baixando na bandeja");
        }
        return true;
    }

    public void HideToTray()
    {
        _win.Hide();
        UpdateTip();
    }

    public void Restore()
    {
        if (!_win.IsVisible) _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        TrayIcon.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(_win).Handle);
    }

    public void Exit()
    {
        Exiting = true;
        Application.Current.Shutdown();
    }

    /* ---------------------------------------------------------- state */

    void UpdateTip()
    {
        var active = Hub.Store.Downloads.Where(d => d.IsActive).ToList();
        if (active.Count == 0) { _icon.Tip = "Venix"; return; }
        var speed = active.Sum(d => d.Speed);
        var tip = $"Venix · {active.Count} download{(active.Count > 1 ? "s" : "")}";
        if (speed > 0) tip += $" · {Format.Speed(speed)}";
        _icon.Tip = tip;
    }

    void UpdateMenu()
    {
        var downloads = Hub.Store.Downloads;
        if (downloads.Any(d => d.IsActive)) { _pauseItem.Header = "Pausar todos os downloads"; _pauseItem.IsEnabled = true; }
        else if (downloads.Any(d => d.CanResume)) { _pauseItem.Header = "Retomar todos os downloads"; _pauseItem.IsEnabled = true; }
        else { _pauseItem.Header = "Pausar todos os downloads"; _pauseItem.IsEnabled = false; }
    }

    void OpenMenu()
    {
        UpdateMenu();
        _menu.IsOpen = false;
        _menu.IsOpen = true;
    }

    async void PauseOrResumeAll()
    {
        var active = Hub.Store.Downloads.Where(d => d.IsActive).ToList();
        if (active.Count > 0)
        {
            foreach (var d in active) await Hub.Torrents.PauseAsync(d);
            _icon.Notify("Venix", active.Count == 1 ? "Download pausado" : $"{active.Count} downloads pausados");
        }
        else
        {
            var paused = Hub.Store.Downloads.Where(d => d.CanResume).ToList();
            foreach (var d in paused) await Hub.Torrents.ResumeAsync(d);
            if (paused.Count > 0) _icon.Notify("Venix", paused.Count == 1 ? "Download retomado" : $"{paused.Count} downloads retomados");
        }
        UpdateTip();
    }

    void OnCompleted(DownloadItem d)
    {
        // the window shows its own toast when it is on screen
        if (!_win.IsVisible) _icon.Notify("Download concluído", d.GameTitle);
        UpdateTip();
    }

    public void Dispose()
    {
        _timer.Stop();
        Hub.Torrents.Completed -= OnCompleted;
        _menu.IsOpen = false;
        _icon.Dispose();
    }
}
