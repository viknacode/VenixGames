using System.IO;
using System.Windows;
using System.Windows.Threading;
using MonoTorrent;
using MonoTorrent.Client;

namespace Venix.Core;

/// <summary>Magnet downloads through MonoTorrent; mirrors live stats into <see cref="DownloadItem"/>.</summary>
public class TorrentService
{
    readonly AppStore _store;
    readonly Dictionary<string, TorrentManager> _managers = new();
    readonly DispatcherTimer _timer;
    ClientEngine? _engine;

    public TorrentService(AppStore store)
    {
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>
    /// MonoTorrent ships with very conservative defaults (150 connections, 8 simultaneous connection
    /// attempts, 5 MB disk cache). Against a big swarm that caps Venix well under 1 MB/s while
    /// libtorrent clients (Hydra, qBittorrent) pull 50+ Mbps from the same magnet. These values mirror
    /// qBittorrent's defaults; the listen port is fixed so the UPnP mapping and DHT identity stay the
    /// same between runs.
    /// </summary>
    EngineSettings BuildSettings()
    {
        var s = _store.Settings;
        var port = ListenPort();
        return new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(AppStore.DataDir, "torrent-cache"),
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadDhtCache = true,
            AllowPortForwarding = true,
            AllowLocalPeerDiscovery = true,
            ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
            {
                ["ipv4"] = new(System.Net.IPAddress.Any, port),
                ["ipv6"] = new(System.Net.IPAddress.IPv6Any, port),
            },
            DhtEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, port),
            MaximumConnections = 500,
            MaximumHalfOpenConnections = 50,
            // 2 MB pieces × thousands of small files: a large read+write cache lets completed pieces be
            // hashed from memory instead of re-reading them from disk.
            DiskCacheBytes = 128 * 1024 * 1024,
            DiskCachePolicy = MonoTorrent.PieceWriter.CachePolicy.ReadsAndWrites,
            MaximumOpenFiles = 512,
            MaximumDownloadRate = s.SpeedLimitMbps > 0 ? s.SpeedLimitMbps * 1_000_000 / 8 : 0
        }.ToSettings();
    }

    static TorrentSettings BuildTorrentSettings() => new TorrentSettingsBuilder
    {
        MaximumConnections = 200,
        UploadSlots = 8,
        AllowDht = true,
        AllowPeerExchange = true,
    }.ToSettings();

    int ListenPort()
    {
        var s = _store.Settings;
        if (s.ListenPort is < 1024 or > 65535)
        {
            s.ListenPort = Random.Shared.Next(20000, 60000);
            _store.SaveSoon();
        }
        return s.ListenPort;
    }

    ClientEngine Engine => _engine ??= new ClientEngine(BuildSettings());

    public async Task ApplySettingsAsync()
    {
        if (_engine != null) await _engine.UpdateSettingsAsync(BuildSettings());
    }

    public async Task<DownloadItem> StartAsync(SourceEntry entry, string uri, string gameId, int? appId, string gameTitle, string savePath)
    {
        var d = new DownloadItem
        {
            Title = entry.Title, Uri = uri, GameId = gameId, AppId = appId, GameTitle = gameTitle,
            SourceName = entry.SourceName, FileSize = entry.FileSize, SavePath = savePath
        };
        _store.Downloads.Insert(0, d);
        _store.SaveSoon();
        await RunAsync(d);
        return d;
    }

    async Task RunAsync(DownloadItem d)
    {
        try
        {
            d.Status = "connecting";
            d.Error = null;
            Directory.CreateDirectory(d.SavePath);
            if (_managers.TryGetValue(d.Id, out var stale) && stale.Torrent == null)
            {
                // still waiting for metadata: re-add so the current tracker list is used
                _managers.Remove(d.Id);
                try { await stale.StopAsync(); await Engine.RemoveAsync(stale, RemoveMode.CacheDataOnly); } catch { }
            }
            if (!_managers.TryGetValue(d.Id, out var m))
            {
                var (bare, trackers) = SplitTrackers(d.Uri);
                m = await Engine.AddAsync(MagnetLink.Parse(bare), d.SavePath, BuildTorrentSettings());
                await AddTrackersAsync(m, trackers);
                _managers[d.Id] = m;
                m.TorrentStateChanged += (_, e) => Application.Current.Dispatcher.InvokeAsync(() => OnState(d, m, e.NewState));
            }
            await m.StartAsync();
        }
        catch (Exception ex)
        {
            d.Status = "error";
            d.Error = ex.Message;
        }
    }

    /// <summary>
    /// MonoTorrent puts every "tr=" of a magnet in ONE tier and talks to one tracker at a time, so a
    /// dead tracker first in the list stalls the download at "metadata" forever. Venix strips them and
    /// re-adds each tracker (the magnet's own plus the configured public ones) as its own tier, which
    /// makes all of them announce in parallel. Many list magnets also carry no tracker at all.
    /// </summary>
    static (string bare, List<string> trackers) SplitTrackers(string magnet)
    {
        var trackers = System.Text.RegularExpressions.Regex.Matches(magnet, @"[?&]tr=([^&]+)")
            .Select(m => Uri.UnescapeDataString(m.Groups[1].Value).Trim())
            .Where(t => t.Length > 0).ToList();
        var bare = System.Text.RegularExpressions.Regex.Replace(magnet, @"&tr=[^&]*", "");
        bare = System.Text.RegularExpressions.Regex.Replace(bare, @"\?tr=[^&]*&?", "?");
        return (bare, trackers);
    }

    async Task AddTrackersAsync(TorrentManager m, IEnumerable<string> fromMagnet)
    {
        var s = _store.Settings;
        var all = fromMagnet.Concat(s.AddPublicTrackers ? s.Trackers : Enumerable.Empty<string>())
            .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var present = m.TrackerManager.Tiers.SelectMany(t => t.Trackers).Select(t => t.Uri.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in all)
        {
            if (present.Contains(t) || !Uri.TryCreate(t, UriKind.Absolute, out var uri)) continue;
            try { await m.TrackerManager.AddTrackerAsync(uri); } catch { /* unsupported scheme (e.g. wss) */ }
        }
    }


    void OnState(DownloadItem d, TorrentManager m, TorrentState s)
    {
        switch (s)
        {
            case TorrentState.Metadata: d.Status = "metadata"; break;
            case TorrentState.Hashing: d.Status = "hashing"; break;
            case TorrentState.Downloading: d.Status = "downloading"; break;
            case TorrentState.Paused: d.Status = "paused"; break;
            case TorrentState.Error: d.Status = "error"; d.Error = m.Error?.Exception?.Message; break;
            case TorrentState.Seeding:
                Complete(d, m);
                if (!_store.Settings.SeedAfterDownload) _ = m.StopAsync();
                else d.Status = "seeding";
                break;
        }
    }

    void Complete(DownloadItem d, TorrentManager m)
    {
        if (d.CompletedAt != null) return;
        d.Progress = 1;
        d.Status = "complete";
        d.Speed = 0;
        d.CompletedAt = DateTime.UtcNow;
        var g = _store.Find(d.GameId);
        if (g != null && m.Torrent != null) g.InstallPath = Path.Combine(d.SavePath, m.Torrent.Name);
        _store.SaveSoon();
        Completed?.Invoke(d);
    }

    public event Action<DownloadItem>? Completed;

    void Poll()
    {
        foreach (var d in _store.Downloads)
        {
            if (!_managers.TryGetValue(d.Id, out var m) || d.CompletedAt != null) continue;
            if (m.Torrent != null)
            {
                d.Name = m.Torrent.Name;
                d.Size = m.Torrent.Size;
            }
            d.Progress = m.Progress / 100.0;
            d.Speed = m.Monitor.DownloadRate;
            d.Peers = m.Peers.Seeds + m.Peers.Leechs;
            d.Seeds = m.Peers.Seeds;
            d.Connections = m.OpenConnections;
            d.Eta = d.Speed > 0 && d.Size > 0 ? TimeSpan.FromSeconds((d.Size * (1 - d.Progress)) / d.Speed) : null;
        }
    }

    public async Task PauseAsync(DownloadItem d)
    {
        if (_managers.TryGetValue(d.Id, out var m)) try { await m.PauseAsync(); } catch { }
        d.Status = "paused";
        d.Speed = 0;
        d.Eta = null;
        _store.SaveSoon();
    }

    public Task ResumeAsync(DownloadItem d) => RunAsync(d);

    /// <summary>
    /// The item leaves the queue immediately; stopping the torrent (which announces "stopped" to every
    /// tracker, slow with ~100 of them) and deleting files run afterwards. Awaiting them first made the
    /// first click look like a no-op, and a second click then hit the fast path.
    /// </summary>
    public async Task RemoveAsync(DownloadItem d, bool deleteFiles)
    {
        d.Status = "cancelling";
        d.Speed = 0;
        d.Eta = null;
        _store.Downloads.Remove(d);
        _store.SaveSoon();
        if (!_managers.Remove(d.Id, out var m)) return;
        try { await m.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { await Engine.RemoveAsync(m, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly); } catch { }
        if (deleteFiles) DeleteLeftovers(d, m);
    }

    /// <summary>MonoTorrent only deletes the files it knows about; drop the (now empty) torrent folder too.</summary>
    static void DeleteLeftovers(DownloadItem d, TorrentManager m)
    {
        try
        {
            var name = m.Torrent?.Name ?? d.Name;
            if (string.IsNullOrEmpty(name)) return;
            var dir = Path.Combine(d.SavePath, name);
            if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, true);
        }
        catch { /* best effort */ }
    }

    public async Task ShutdownAsync()
    {
        if (_engine == null) return;
        try { await _engine.StopAllAsync(TimeSpan.FromSeconds(3)); } catch { }
    }
}
