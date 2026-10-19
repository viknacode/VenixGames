using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Venix.Core;

/// <summary>
/// Async remote images with a disk cache and a fallback chain:
/// &lt;Image core:Img.Urls="{Binding Urls}"/&gt; tries each URL until one loads.
/// Besides http(s) URLs the chain accepts "steamasset:{id}:{kind}" (resolved by <see cref="Resolver"/>)
/// and "placeholder:p:{title}" / "placeholder:w:{title}", a cover generated locally from the game name,
/// so a game whose artwork is unknown or unreachable still gets a cover.
/// </summary>
public static class Img
{
    static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Venix", "images");
    static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Memory = new();
    static readonly SemaphoreSlim Gate = new(8);
    public static HttpClient Http { get; set; } = new();

    public static readonly DependencyProperty UrlsProperty = DependencyProperty.RegisterAttached(
        "Urls", typeof(object), typeof(Img), new PropertyMetadata(null, OnUrlsChanged));
    public static void SetUrls(DependencyObject d, object? v) => d.SetValue(UrlsProperty, v);
    public static object? GetUrls(DependencyObject d) => d.GetValue(UrlsProperty);

    /// <summary>Decode width; keeps memory low for grid thumbnails.</summary>
    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(Img), new PropertyMetadata(0));
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);

    static async void OnUrlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var urls = e.NewValue switch
        {
            string s => s.Split('|', StringSplitOptions.RemoveEmptyEntries),
            IEnumerable<string> l => l.ToArray(),
            _ => Array.Empty<string>()
        };
        var width = GetDecodeWidth(d);
        void Apply(ImageSource? src)
        {
            if (d is Image img) img.Source = src;
            else if (d is ImageBrush brush) brush.ImageSource = src;
        }
        Apply(null);
        if (d is FrameworkElement fe) fe.Opacity = 0;
        var bmp = await LoadAsync(urls, width);
        if (!ReferenceEquals(GetUrls(d), e.NewValue)) return; // recycled for another item meanwhile
        Apply(bmp);
        if (d is FrameworkElement el && bmp != null)
            el.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }

    public static Func<string, Task<string[]>>? Resolver { get; set; }

    /* ---------------------------------------------------------- fallback chain helpers */

    const string PortraitPrefix = "placeholder:p:", WidePrefix = "placeholder:w:";

    /// <summary>Generated cover token for a game without known artwork (portrait 2:3, or wide 2:1 for heroes/headers).</summary>
    public static string[] Placeholder(string title, bool wide = false) => new[] { (wide ? WidePrefix : PortraitPrefix) + (title ?? "") };

    /// <summary>Artwork candidates followed by the generated cover, so the chain always ends in an image.</summary>
    public static string[] Chain(IEnumerable<string>? art, string title, bool wide = false) =>
        (art ?? Array.Empty<string>()).Concat(Placeholder(title, wide)).ToArray();

    public static async Task<BitmapSource?> LoadAsync(IEnumerable<string> urls, int decodeWidth = 0)
    {
        var expanded = new List<string>();
        foreach (var u in urls)
        {
            if (string.IsNullOrWhiteSpace(u)) continue;
            if (u.StartsWith("steamasset:") && Resolver != null) expanded.AddRange(await Resolver(u));
            else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { expanded.Add("https://" + u[7..]); expanded.Add(u); } // prefer TLS, keep the original as fallback
            else expanded.Add(u);
        }
        foreach (var url in expanded)
        {
            var key = $"{url}#{decodeWidth}";
            var bmp = await Memory.GetOrAdd(key, _ => url.StartsWith("placeholder:") ? RenderPlaceholder(url, decodeWidth) : Fetch(url, decodeWidth));
            if (bmp != null) return bmp;
            // a 404 is final; anything else (timeout, 429, offline) may work next time, so don't pin the miss in memory
            if (!File.Exists(MissingMarker(url))) Memory.TryRemove(key, out _);
        }
        return null;
    }

    static string CacheFile(string url) => Path.Combine(CacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
    static string MissingMarker(string url) => CacheFile(url) + ".404";

    static async Task<BitmapSource?> Fetch(string url, int decodeWidth)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var file = CacheFile(url);
            var missing = MissingMarker(url);
            if (File.Exists(missing)) return null;
            byte[] bytes;
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
            else
            {
                await Gate.WaitAsync();
                try
                {
                    using var res = await Http.GetAsync(url);
                    if (!res.IsSuccessStatusCode)
                    {
                        if ((int)res.StatusCode == 404) await File.WriteAllTextAsync(missing, "");
                        else Debug.WriteLine($"[Img] HTTP {(int)res.StatusCode} {url}");
                        return null;
                    }
                    bytes = await res.Content.ReadAsByteArrayAsync();
                    if (bytes.Length == 0) return null;
                    await File.WriteAllBytesAsync(file, bytes);
                }
                finally { Gate.Release(); }
            }
            try
            {
                return await Task.Run(() => Decode(bytes, decodeWidth));
            }
            catch (Exception ex)
            {
                // corrupted/partial cache file (interrupted write, HTML error page cached by an older build): drop it and refetch once
                Debug.WriteLine($"[Img] decode failed {url}: {ex.Message}");
                try { File.Delete(file); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Img] {url}: {ex.Message}");
            return null;
        }
    }

    static BitmapSource Decode(byte[] bytes, int decodeWidth)
    {
        var b = new BitmapImage();
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth > 0) b.DecodePixelWidth = decodeWidth;
        b.StreamSource = new MemoryStream(bytes);
        b.EndInit();
        b.Freeze();
        return b;
    }

    /* ---------------------------------------------------------- generated cover */

    static Task<BitmapSource?> RenderPlaceholder(string token, int decodeWidth)
    {
        var wide = token.StartsWith(WidePrefix);
        var title = token[(wide ? WidePrefix : PortraitPrefix).Length..].Trim();
        var app = Application.Current;
        if (app == null) return Task.FromResult<BitmapSource?>(null);
        return app.Dispatcher.InvokeAsync(() =>
        {
            try { return (BitmapSource?)DrawCover(title, wide, decodeWidth); }
            catch (Exception ex) { Debug.WriteLine($"[Img] placeholder failed: {ex.Message}"); return null; }
        }).Task;
    }

    static readonly FontFamily CoverFont = new("Segoe UI Variable Display, Segoe UI");

    static RenderTargetBitmap DrawCover(string title, bool wide, int decodeWidth)
    {
        var w = decodeWidth > 0 ? Math.Clamp(decodeWidth, 160, 1600) : (wide ? 960 : 360);
        var h = wide ? w / 2 : w * 3 / 2;
        var hue = Hue(title);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(Hsl(hue, 0.42, 0.30), Hsl(hue + 36, 0.48, 0.15), new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            // soft light in the upper corner + darker bottom so the title reads
            var glow = new RadialGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255))
            { Center = new Point(0.2, 0.1), GradientOrigin = new Point(0.2, 0.1), RadiusX = 0.8, RadiusY = 0.6 };
            dc.DrawRectangle(glow, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0x70, 0, 0, 0), 90), null, new Rect(0, h * 0.45, w, h * 0.55));

            // huge faded monogram behind the title
            var mono = Monogram(title);
            if (mono.Length > 0)
            {
                var ft = new FormattedText(mono, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(CoverFont, FontStyles.Normal, FontWeights.Black, FontStretches.Normal),
                    wide ? h * 0.9 : w * 0.78, new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)), 1.0);
                dc.DrawText(ft, new Point(wide ? w * 0.04 : (w - ft.Width) / 2, wide ? (h - ft.Height) / 2 : h * 0.12));
            }

            var pad = w * 0.08;
            var text = new FormattedText(title.Length > 0 ? title : "Sem título", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(CoverFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), FontSizeFor(title, w, wide), Brushes.White, 1.0)
            {
                MaxTextWidth = w - pad * 2,
                MaxTextHeight = h * (wide ? 0.6 : 0.5),
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = wide ? TextAlignment.Left : TextAlignment.Center
            };
            dc.DrawText(text, new Point(pad, wide ? h - pad - text.Height : h * 0.56));

            var tag = new FormattedText("VENIX", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(CoverFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), Math.Max(9, w * 0.032), new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), 1.0);
            dc.DrawText(tag, new Point(wide ? w - pad - tag.Width : pad, pad * 0.9));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static double FontSizeFor(string title, int w, bool wide)
    {
        var len = Math.Max(1, title.Length);
        var size = (wide ? w * 0.075 : w * 0.11) * (len > 40 ? 0.7 : len > 24 ? 0.85 : 1);
        return Math.Max(wide ? w * 0.04 : w * 0.07, size);
    }

    static string Monogram(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => char.IsLetterOrDigit(x[0]) && !x.Equals("the", StringComparison.OrdinalIgnoreCase)).ToArray();
        return words.Length switch
        {
            0 => "",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(2).Select(x => char.ToUpperInvariant(x[0])))
        };
    }

    static double Hue(string s)
    {
        // stable per title so the same game always gets the same colour, in any view
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Titles.Normalize(s)));
        return BitConverter.ToUInt16(hash, 0) % 360;
    }

    static Color Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
