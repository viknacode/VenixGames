using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Venix.Core;

public class FuncConverter(Func<object?, object?> f) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => f(value);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class Conv
{
    // Steam art first, then a cover generated from the title, so games without a Steam match still get an image
    public static readonly IValueConverter LibraryIcon = new FuncConverter(v => v is LibraryGame g ? Img.Chain(g.AppId is { } id ? SteamService.Portrait(id) : null, g.Title) : null);
    public static readonly IValueConverter LibraryHeader = new FuncConverter(v => v is LibraryGame g ? Img.Chain(g.AppId is { } id ? SteamService.Header(id) : null, g.Title, wide: true) : null);
    public static readonly IValueConverter DownloadArt = new FuncConverter(v => v is DownloadItem d
        ? Img.Chain(d.AppId is { } id ? SteamService.Header(id) : null, string.IsNullOrWhiteSpace(d.GameTitle) ? d.Title : d.GameTitle, wide: true) : null);
    public static readonly IValueConverter Visible = new FuncConverter(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    public static readonly IValueConverter Hidden = new FuncConverter(v => v is true ? Visibility.Collapsed : Visibility.Visible);
}
