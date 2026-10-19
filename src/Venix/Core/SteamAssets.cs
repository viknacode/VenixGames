using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Venix.Core;

/// <summary>
/// Resolves real artwork URLs. Newer Steam apps store art under hashed paths, so the
/// fixed "steam/apps/{id}/header.jpg" pattern 404s; IStoreBrowseService/GetItems returns the
/// actual file names. Requests are batched (up to 50 apps per call).
/// </summary>
public partial class SteamService
{
    const string AssetBase = "https://shared.akamai.steamstatic.com/store_item_assets/";
    readonly ConcurrentDictionary<int, Task<Dictionary<string, string>>> _assets = new();
    readonly object _batchLock = new();
    Dictionary<int, TaskCompletionSource<Dictionary<string, string>>> _batch = new();
    bool _batchScheduled;

    public Task<Dictionary<string, string>> AssetsAsync(int appId) => _assets.GetOrAdd(appId, id =>
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string>>();
        lock (_batchLock)
        {
            _batch[id] = tcs;
            if (!_batchScheduled)
            {
                _batchScheduled = true;
                _ = Task.Delay(60).ContinueWith(_ => FlushBatch());
            }
        }
        return tcs.Task;
    });

    async Task FlushBatch()
    {
        Dictionary<int, TaskCompletionSource<Dictionary<string, string>>> batch;
        lock (_batchLock) { batch = _batch; _batch = new(); _batchScheduled = false; }
        foreach (var chunk in batch.Chunk(50))
        {
            var found = new Dictionary<int, Dictionary<string, string>>();
            try
            {
                var input = new JsonObject
                {
                    ["ids"] = new JsonArray(chunk.Select(kv => (JsonNode)new JsonObject { ["appid"] = kv.Key }).ToArray()),
                    ["context"] = new JsonObject { ["language"] = "brazilian", ["country_code"] = "BR" },
                    ["data_request"] = new JsonObject { ["include_assets"] = true }
                };
                var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input.ToJsonString());
                var root = JsonNode.Parse(await _http.GetStringAsync(url));
                foreach (var item in root?["response"]?["store_items"]?.AsArray() ?? new JsonArray())
                {
                    var a = item?["assets"];
                    var id = item?["id"]?.GetValue<int>() ?? 0;
                    var fmt = a?["asset_url_format"]?.ToString();
                    if (a == null || fmt == null) continue;
                    var map = new Dictionary<string, string>();
                    foreach (var (k, v) in a.AsObject())
                        if (v is JsonValue && k != "asset_url_format" && v.ToString().Contains('.'))
                            map[k] = AssetBase + fmt.Replace("${FILENAME}", v.ToString());
                    found[id] = map;
                }
            }
            catch { /* fall back to legacy paths */ }
            foreach (var (id, tcs) in chunk) tcs.TrySetResult(found.GetValueOrDefault(id) ?? new());
        }
    }

    /// <summary>Expands "steamasset:{id}:{kind}" placeholders into candidate URLs.</summary>
    public async Task<string[]> ResolveAsync(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var id)) return Array.Empty<string>();
        var a = await AssetsAsync(id);
        string? Get(string k) => a.GetValueOrDefault(k);
        IEnumerable<string?> list = parts[2] switch
        {
            "portrait" => new[] { Get("library_capsule_2x"), Get("library_capsule"), $"{Cdn}/{id}/library_600x900_2x.jpg", Get("header") },
            "hero" => new[] { Get("library_hero"), Get("page_background"), $"{Cdn}/{id}/library_hero.jpg", Get("main_capsule"), Get("header") },
            "header" => new[] { Get("header"), Get("main_capsule"), $"{Cdn}/{id}/header.jpg" },
            "logo" => new[] { Get("library_logo"), $"{Cdn}/{id}/logo.png" },
            _ => Array.Empty<string>()
        };
        return list.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToArray()!;
    }
}
