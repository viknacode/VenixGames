using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Venix.Core;

/// <summary>Segoe Fluent Icons glyphs.</summary>
public static class Icons
{
    public const string Home = "", Catalog = "", Library = "", Download = "", Settings = "";
    public const string Search = "", Back = "", Play = "", Pause = "", Add = "", Delete = "";
    public const string Refresh = "", Folder = "", Close = "", Check = "", Clock = "", Link = "";
    public const string ArrowUpRight = "", Import = "", Star = "", Game = "", Sync = "", Globe = "";
    public const string Minimize = "", Maximize = "", Restore = "", ChromeClose = "", Info = "";
    public const string Cheat = "", Shield = "", Code = "";
    public const string Theme = "", Speed = "", People = "", Hourglass = "", Disk = "", Chevron = "";
    public const string Update = "";
}

public static class Ui
{
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached("Badge", typeof(object), typeof(Ui), new PropertyMetadata(null));
    public static void SetBadge(DependencyObject d, object? v) => d.SetValue(BadgeProperty, v);
    public static object? GetBadge(DependencyObject d) => d.GetValue(BadgeProperty);

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static void SetIcon(DependencyObject d, string? v) => d.SetValue(IconProperty, v);
    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);

    /// <summary>Height = ActualWidth * ratio.</summary>
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.RegisterAttached("AspectRatio", typeof(double), typeof(Ui),
        new PropertyMetadata(0.0, (d, _) =>
        {
            if (d is not FrameworkElement fe) return;
            fe.SizeChanged -= OnAspectSize;
            fe.SizeChanged += OnAspectSize;
        }));
    public static void SetAspectRatio(DependencyObject d, double v) => d.SetValue(AspectRatioProperty, v);
    public static double GetAspectRatio(DependencyObject d) => (double)d.GetValue(AspectRatioProperty);
    static void OnAspectSize(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var fe = (FrameworkElement)sender;
        fe.Height = Math.Round(e.NewSize.Width * GetAspectRatio(fe));
    }

    /// <summary>
    /// Real frosted glass: put on a container that holds a blurred copy of the artwork;
    /// the container gets clipped to the bounds of the target panel (rounded).
    /// </summary>
    public static readonly DependencyProperty ClipToProperty = DependencyProperty.RegisterAttached("ClipTo", typeof(FrameworkElement), typeof(Ui),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is not FrameworkElement host || e.NewValue is not FrameworkElement target) return;
            Rect last = Rect.Empty;
            void Update(object? s, EventArgs a)
            {
                if (!host.IsLoaded || !target.IsLoaded || target.ActualWidth == 0 || !target.IsVisible) return;
                Rect r;
                try { r = target.TransformToVisual(host).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight)); }
                catch { return; }
                if (r == last) return;
                last = r;
                var radius = target is Border b ? b.CornerRadius.TopLeft : 0;
                host.Clip = new RectangleGeometry(r, radius, radius);
            }
            host.LayoutUpdated += Update;
        }));
    public static void SetClipTo(DependencyObject d, FrameworkElement? v) => d.SetValue(ClipToProperty, v);
    public static FrameworkElement? GetClipTo(DependencyObject d) => (FrameworkElement?)d.GetValue(ClipToProperty);

    /// <summary>True pill shape: CornerRadius = half of the smaller side (WPF does not clamp huge radii like CSS).</summary>
    public static readonly DependencyProperty PillProperty = DependencyProperty.RegisterAttached("Pill", typeof(bool), typeof(Ui),
        new PropertyMetadata(false, (d, e) =>
        {
            if (d is not Border b || e.NewValue is not true) return;
            void Apply() { var r = Math.Min(Math.Min(b.ActualWidth, b.ActualHeight) / 2, 24); if (r > 0) b.CornerRadius = new CornerRadius(r); }
            b.SizeChanged += (_, _) => Apply();
            b.Loaded += (_, _) => Apply();
        }));
    public static void SetPill(DependencyObject d, bool v) => d.SetValue(PillProperty, v);
    public static bool GetPill(DependencyObject d) => (bool)d.GetValue(PillProperty);

    public static void FadeIn(UIElement el, double fromY = 12, int ms = 320)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.Opacity = 0;
        var t = new TranslateTransform(0, fromY);
        el.RenderTransform = t;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }
}

/// <summary>Wrap-like grid: as many equal columns as fit MinItemWidth.</summary>
public class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }

    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(nameof(MaxRows), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    /// <summary>0 = unlimited; otherwise extra children are hidden.</summary>
    public int MaxRows { get => (int)GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }

    int _cols = 1;
    readonly List<double> _rows = new();

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinItemWidth * 4 : available.Width;
        _cols = Math.Max(1, (int)(width / MinItemWidth));
        var itemW = width / _cols;
        _rows.Clear();
        var limit = MaxRows > 0 ? MaxRows * _cols : int.MaxValue;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            if (i >= limit) { c.Measure(new Size(0, 0)); continue; }
            c.Measure(new Size(itemW, double.PositiveInfinity));
            var row = i / _cols;
            if (_rows.Count <= row) _rows.Add(0);
            _rows[row] = Math.Max(_rows[row], c.DesiredSize.Height);
        }
        return new Size(width, _rows.Sum());
    }

    protected override Size ArrangeOverride(Size final)
    {
        var itemW = final.Width / _cols;
        double y = 0;
        var limit = MaxRows > 0 ? MaxRows * _cols : int.MaxValue;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
            if (i >= limit) { c.Arrange(new Rect(0, 0, 0, 0)); continue; }
            var row = i / _cols;
            if (i % _cols == 0 && row > 0) y += _rows[row - 1];
            c.Arrange(new Rect((i % _cols) * itemW, y, itemW, _rows[row]));
        }
        return final;
    }
}

/// <summary>View model for a game tile.</summary>
public class Tile : Observable
{
    string[] _urls = Array.Empty<string>();
    string _sub = "";
    int? _appId;

    public string Title { get; set; } = "";
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    public string? Badge { get; set; }
    public string BadgeIcon { get; set; } = Icons.Clock;
    public Visibility BadgeVisibility => string.IsNullOrEmpty(Badge) ? Visibility.Collapsed : Visibility.Visible;
    public string[] Urls { get => _urls; set => Set(ref _urls, value); }
    /// <summary>Steam artwork when the game is known there, always ending in a generated cover; null = no Steam match, cover only.</summary>
    public int? AppId { get => _appId; set { _appId = value; Urls = Img.Chain(value is { } id ? SteamService.Portrait(id) : null, Title); } }
    public object? Payload { get; set; }
}
