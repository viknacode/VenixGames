using System.Net.Http;

namespace Venix.Core;

/// <summary>Application-wide services.</summary>
public static class Hub
{
    public static HttpClient Http { get; } = CreateHttp();
    public static AppStore Store { get; private set; } = null!;
    public static SourceService Sources { get; private set; } = null!;
    public static SteamService Steam { get; private set; } = null!;
    public static TorrentService Torrents { get; private set; } = null!;
    public static CheatService Cheats { get; private set; } = null!;
    public static GamepadService Gamepad { get; private set; } = null!;
    /// <summary>Notification-area icon; exists once the main window does.</summary>
    public static TrayService? Tray { get; private set; }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Venix/1.0 (+Windows)");
        return h;
    }

    public static void Init()
    {
        Img.Http = Http;
        Store = AppStore.Load();
        Sources = new SourceService(Store, Http);
        Steam = new SteamService(Http);
        Torrents = new TorrentService(Store);
        Img.Resolver = Steam.ResolveAsync;
        Cheats = new CheatService(Http, Store);
        Gamepad = new GamepadService(Store);
    }

    public static void InitTray(MainWindow win) => Tray = new TrayService(win);

    public static string GameId(int? appId, string title) => appId is { } id ? $"steam-{id}" : $"t-{Titles.Normalize(title)}";
}
