using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Venix.Core;

namespace Venix;

/// <summary>
/// Startup screen: loads the cached index and syncs every .json of the sources folder
/// before the main window opens. "Continuar sem esperar" lets slow lists finish in background.
/// </summary>
public partial class SplashWindow : Window
{
    readonly TaskCompletionSource _skip = new();
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public SplashWindow()
    {
        InitializeComponent();
        Version.Text = $"v{typeof(SplashWindow).Assembly.GetName().Version?.ToString(3)}";
        SourceList.ItemsSource = Hub.Store.Sources;
        _tick.Tick += (_, _) => UpdateProgress();
        Loaded += (_, _) => Animate();
    }

    void Animate()
    {
        var pulse = new DoubleAnimation(1, 1.06, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);
        IndeterminateX.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(-90, 440, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
    }

    /// <summary>Runs the startup work. Returns the sync result, or null when the user skipped.</summary>
    public async Task<(int added, int removed, int failed)?> RunAsync()
    {
        var started = DateTime.UtcNow;
        Status.Text = "Carregando dados locais…";
        await Hub.Sources.LoadAsync();

        Status.Text = "Verificando fontes…";
        var work = Hub.Sources.ApplyConfigAsync();
        _tick.Start();
        _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(() => { if (!work.IsCompleted) SkipBtn.Visibility = Visibility.Visible; }));

        var first = await Task.WhenAny(work, _skip.Task);
        _tick.Stop();
        if (first != work) return null; // keeps syncing in background; MainWindow refreshes on IndexChanged

        var result = await work;
        UpdateProgress();
        Status.Text = result.failed > 0 ? $"{result.failed} fonte(s) com erro" : "Tudo pronto";
        Indeterminate.Visibility = Visibility.Collapsed;
        Bar.Width = ((FrameworkElement)Bar.Parent).ActualWidth;

        // keep the splash on screen long enough to be read, never longer than needed
        var min = TimeSpan.FromMilliseconds(result.failed > 0 ? 1800 : 900) - (DateTime.UtcNow - started);
        if (min > TimeSpan.Zero) await Task.Delay(min);
        return result;
    }

    void UpdateProgress()
    {
        var sources = Hub.Store.Sources;
        if (sources.Count == 0)
        {
            Counter.Text = "";
            Hint.Visibility = Visibility.Visible;
            Hint.Text = "A pasta sources está vazia. Coloque os arquivos .json das listas nela (ao lado do Venix.exe) e reabra o Venix.";
            return;
        }
        Hint.Visibility = Visibility.Collapsed;
        var done = sources.Count(s => s.Status != "syncing");
        Counter.Text = $"{done}/{sources.Count}";
        var syncing = sources.FirstOrDefault(s => s.Status == "syncing");
        if (syncing != null) Status.Text = $"Lendo {syncing.Name}…";
        var track = ((FrameworkElement)Bar.Parent).ActualWidth;
        Indeterminate.Visibility = done < sources.Count ? Visibility.Visible : Visibility.Collapsed;
        Bar.BeginAnimation(WidthProperty, new DoubleAnimation(track * done / sources.Count, TimeSpan.FromMilliseconds(200)));
    }

    void OnSkip(object sender, RoutedEventArgs e) => _skip.TrySetResult();

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
