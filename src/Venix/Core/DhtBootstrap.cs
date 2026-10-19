using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Venix.Core;

/// <summary>
/// Makes MonoTorrent 3.0.2's DHT actually come up.
///
/// That release bootstraps from a single router, router.bittorrent.com:6881, which does not answer from
/// many networks (verified from here: no reply, while dht.transmissionbt.com and dht.libtorrent.org do).
/// The DHT then sits in "Initialising" forever and the only peers Venix ever sees come from trackers;
/// libtorrent (Hydra, qBittorrent) tries six routers and is fine on the same magnet. Upstream fixed it
/// (2024-08, 2026-06) but no stable package has shipped the fix.
///
/// Workaround: the engine loads <c>dht_nodes.cache</c> as a raw run of 26-byte compact nodes and sends
/// find_node to each of them before falling back to its own router. Venix rewrites that file with every
/// bootstrap router that resolves, right before the engine starts; the routing table is then filled in a
/// couple of seconds. The nodes the engine saves on shutdown are deliberately not reused: 3.0.2 writes
/// them as a bencoded list the raw loader misreads, and in testing a seed of stale nodes plus routers
/// left the DHT stuck at ~25 nodes while routers alone reached 60+ and "Ready" in 3 s.
/// </summary>
public static class DhtBootstrap
{
    public static readonly (string Host, int Port)[] Routers =
    {
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("router.utorrent.com", 6881),
        ("router.bittorrent.com", 6881),
        ("dht.aelitis.com", 6881),
        ("router.bitcomet.com", 6881),
    };

    static byte[]? _routerNodes;
    static DateTime _resolvedAt;

    /// <summary>
    /// Writes the cache file MonoTorrent loads at DHT start. Returns how many router nodes were written
    /// (0 means nothing resolved, e.g. offline; the file is then left as it was).
    /// </summary>
    public static async Task<int> SeedCacheAsync(string cacheDirectory, TimeSpan? dnsTimeout = null)
    {
        var routers = await ResolveRoutersAsync(dnsTimeout ?? TimeSpan.FromSeconds(4));
        if (routers.Length == 0) return 0;
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, "dht_nodes.cache"), routers);
        return routers.Length / 26;
    }

    /// <summary>
    /// Routers as BEP 5 compact nodes (20-byte id, IPv4, big-endian port) with random ids: the id only
    /// matters for find_node targeting, which the engine does with its own id. Cached for an hour.
    /// </summary>
    static async Task<byte[]> ResolveRoutersAsync(TimeSpan timeout)
    {
        if (_routerNodes is { Length: > 0 } && DateTime.UtcNow - _resolvedAt < TimeSpan.FromHours(1)) return _routerNodes;
        using var cts = new CancellationTokenSource(timeout);
        var lookups = Routers.Select(async r =>
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync(r.Host, AddressFamily.InterNetwork, cts.Token);
                return ips.Select(ip => (ip, r.Port)).ToArray();
            }
            catch { return Array.Empty<(IPAddress, int)>(); }
        }).ToArray();
        try { await Task.WhenAll(lookups); } catch { }

        using var ms = new MemoryStream();
        foreach (var (ip, port) in lookups.Where(t => t.IsCompletedSuccessfully).SelectMany(t => t.Result))
        {
            var id = new byte[20];
            Random.Shared.NextBytes(id);
            ms.Write(id);
            ms.Write(ip.GetAddressBytes());
            ms.WriteByte((byte)(port >> 8));
            ms.WriteByte((byte)(port & 0xff));
        }
        if (ms.Length > 0)
        {
            _routerNodes = ms.ToArray();
            _resolvedAt = DateTime.UtcNow;
        }
        return _routerNodes ?? Array.Empty<byte>();
    }
}
