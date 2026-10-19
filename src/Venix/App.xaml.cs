using System.IO;
using System.Windows;
using System.Windows.Threading;
using Venix.Core;

namespace Venix;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };
        // the splash is the first window; don't let closing it end the app
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            // a Venix already running (possibly only in the tray) is brought back instead of a second copy
            if (!SingleInstance.TryAcquire(() => Dispatcher.BeginInvoke(() => Hub.Tray?.Restore())))
            {
                Shutdown();
                return;
            }
            Hub.Init();
            ApplyTheme(Hub.Store.Settings.Theme);

            var splash = new SplashWindow();
            splash.Show();
            var result = await splash.RunAsync();

            var win = new MainWindow();
            MainWindow = win;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            Hub.InitTray(win);
            if (Hub.Store.Settings.StartInTray)
            {
                // the handle must exist for the controller hook; the window itself stays hidden until the icon is clicked
                new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();
                Hub.Tray!.HideToTray();
            }
            else win.Show();
            splash.Close();
            win.ReportSources(result);
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show(ex.Message, "Venix", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static void ApplyTheme(string theme)
    {
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri($"Themes/{(theme == "Dark" ? "Dark" : "Light")}.xaml", UriKind.Relative) };
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppStore.DataDir);
            File.AppendAllText(Path.Combine(AppStore.DataDir, "error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { }
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log(e.Exception);
        if (MainWindow is MainWindow { IsLoaded: true } w) w.Toast(e.Exception.Message, true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Hub.Tray?.Dispose(); } catch { } // removes the icon; otherwise it lingers until hovered
        try { Hub.Store?.Save(); } catch { }
        try { Venix.Trainer.TrainerHub.ShutdownAll(); } catch { } // restores game code patched by active scripts
        Hub.Torrents?.ShutdownAsync().Wait(TimeSpan.FromSeconds(4));
        SingleInstance.Release();
        base.OnExit(e);
    }
}
