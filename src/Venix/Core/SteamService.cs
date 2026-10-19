using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Venix.Core;

/// <summary>Game metadata and artwork from the public Steam store API (no key required).</summary>
public partial class SteamService
{
    const string Cdn = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps";
    static readonly string CacheFile = Path.Combine(AppStore.DataDir, "steam-cache.json");

    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(4);
    readonly ConcurrentDictionary<string, SteamApp?> _lookup;
    readonly ConcurrentDictionary<int, SteamDetails> _details = new();
    readonly ConcurrentDictionary<string, Task<SteamApp?>> _pending = new();

    /// <summary>
    /// Bump when title cleaning or search terms improve: cached misses (null) from older builds are dropped so those
    /// titles get searched again; confirmed matches are kept.
    /// </summary>
    const int LookupVersion = 2;
    static readonly string VersionFile = Path.Combine(AppStore.DataDir, "steam-cache.version");

    public SteamService(HttpClient http)
    {
        _http = http;
        try { _lookup = JsonSerializer.Deserialize<ConcurrentDictionary<string, SteamApp?>>(File.ReadAllText(CacheFile)) ?? new(); }
        catch { _lookup = new(); }
        try
        {
            var v = File.Exists(VersionFile) && int.TryParse(File.ReadAllText(VersionFile), out var n) ? n : 1;
            if (v < LookupVersion)
            {
                foreach (var key in _lookup.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) _lookup.TryRemove(key, out _);
                Directory.CreateDirectory(AppStore.DataDir);
                File.WriteAllText(VersionFile, LookupVersion.ToString());
                SaveCacheSoon();
            }
        }
        catch { }
    }

        public static string[] Portrait(int id) => new[] { $"steamasset:{id}:portrait" };
    public static string[] Header(int id) => new[] { $"steamasset:{id}:header" };
    public static string[] Hero(int id) => new[] { $"steamasset:{id}:hero" };
    public static string[] Logo(int id) => new[] { $"steamasset:{id}:logo" };

    async Task<JsonNode?> Get(string url)
    {
        await _gate.WaitAsync();
        try { return JsonNode.Parse(await _http.GetStringAsync(url)); }
        finally { _gate.Release(); }
    }

    public async Task<Dictionary<string, List<SteamApp>>> FeaturedAsync()
    {
        var root = await Get("https://store.steampowered.com/api/featuredcategories?cc=BR&l=portuguese");
        List<SteamApp> Pick(string key) =>
            (root?[key]?["items"]?.AsArray() ?? new JsonArray())
            .Where(i => (i?["type"]?.GetValue<int>() ?? 0) == 0)
            .Select(i => new SteamApp { AppId = i!["id"]!.GetValue<int>(), Name = i["name"]?.ToString() ?? "" })
            .DistinctBy(a => a.AppId).ToList();
        return new()
        {
            ["top"] = Pick("top_sellers"),
            ["new"] = Pick("new_releases"),
            ["specials"] = Pick("specials"),
            ["soon"] = Pick("coming_soon")
        };
    }

    public async Task<List<SteamApp>> SearchAsync(string term)
    {
        var root = await Get($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&l=portuguese&cc=BR");
        return (root?["items"]?.AsArray() ?? new JsonArray())
            .Where(i => i?["type"]?.ToString() == "app")
            .Select(i => new SteamApp { AppId = i!["id"]!.GetValue<int>(), Name = i["name"]?.ToString() ?? "" })
            .ToList();
    }

    /// <summary>Finds the Steam app that corresponds to a repack title (cached on disk).</summary>
    public Task<SteamApp?> LookupAsync(string title)
    {
        var key = Titles.Normalize(title);
        if (_lookup.TryGetValue(key, out var hit)) return Task.FromResult(hit);
        return _pending.GetOrAdd(key, _ => LookupCore(title, key));
    }

    async Task<SteamApp?> LookupCore(string title, string key)
    {
        try
        {
            SteamApp? best = null;
            // Store search is picky with edition suffixes, so try progressively simpler names; every candidate is
            // still checked against the FULL title so a short term can't pick a different game of the same franchise
            foreach (var term in SearchTerms(title))
            {
                var apps = await SearchAsync(term);
                best = apps.Select(a => (app: a, score: Score(key, Titles.Normalize(a.Name))))
                           .Where(x => x.score >= 0).OrderBy(x => x.score).Select(x => x.app).FirstOrDefault();
                if (best != null) break;
            }
            _lookup[key] = best;
            SaveCacheSoon();
            return best;
        }
        catch { return null; }
        finally { _pending.TryRemove(key, out _); }
    }

    static readonly System.Text.RegularExpressions.Regex Edition = new(
        @"\s*[-–:]?\s*\b(digital\s+)?(deluxe|goty|game of the year|complete|definitive|ultimate|gold|platinum|premium|anniversary|collector'?s|standard|enhanced|director'?s cut|special|legendary|royal|extended|collection|bundle|pack)\b.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // "Metal Slug v3", "Secrets of Grindea v38": a short trailing version Titles.Clean leaves in place
    static readonly System.Text.RegularExpressions.Regex TrailingVersion = new(@"\s+v\.?\d+[a-z]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // What may differ between a list title and the Steam name of the SAME game: edition/bundle words and a year.
    // "hollow knight" vs "hollow knight silksong" or "cities skylines" vs "cities skylines ii" are different games → rejected.
    static readonly System.Text.RegularExpressions.Regex EditionWords = new(
        @"^(?:(?:digital|deluxe|goty|game|of|the|year|complete|definitive|ultimate|gold|platinum|premium|anniversary|collectors?|standard|enhanced|directors?|cut|special|legendary|royal|extended|full|edition|version|collection|bundle|packs?|all|dlcs?|bonus|content|hd|pc|windows|steam|and|for|\d{4}|v\d+[a-z]?)\s?)+$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    // "Necronomicon Edition", "Dragon's Hoard Edition", "Master Collection Version": a short named edition is still the same game
    static readonly System.Text.RegularExpressions.Regex NamedEdition = new(@"^(?:\w+\s){1,3}(?:edition|version)$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // "40,000" normalizes to "40 000"; compare digit groups without the gap so "Warhammer 40000" meets "Warhammer 40,000"
    static readonly System.Text.RegularExpressions.Regex DigitGap = new(@"(?<=\d) (?=\d{3}\b)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Steam entries that are never the game itself, even when their name starts with the game's name
    static readonly System.Text.RegularExpressions.Regex NotTheGame = new(@"\b(dlc|soundtrack|ost|demo|upgrade|season pass|playtest|dedicated server|artbook|trailer)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 0 = same name; n &gt; 0 = same game, the lower the closer (a list title with edition words beats a Steam entry
    /// with extra words, so the base game wins over its own "Deluxe Edition DLC"); -1 = not the same game.
    /// </summary>
    static int Score(string wanted, string candidate)
    {
        wanted = DigitGap.Replace(wanted, "");
        candidate = DigitGap.Replace(candidate, "");
        if (wanted.Length == 0 || candidate.Length == 0) return -1;
        if (wanted == candidate) return 0;
        var steamLonger = candidate.Length > wanted.Length;
        var (shortS, longS) = steamLonger ? (wanted, candidate) : (candidate, wanted);
        if (shortS.Length < 4 || !longS.StartsWith(shortS + " ")) return -1;
        var rest = longS[(shortS.Length + 1)..];
        if (steamLonger && NotTheGame.IsMatch(rest)) return -1;
        if (!EditionWords.IsMatch(rest) && !NamedEdition.IsMatch(rest)) return -1;
        return rest.Split(' ').Length + (steamLonger ? 10 : 0);
    }

    static readonly System.Text.RegularExpressions.Regex Dash = new(@"\s+[-–—]\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Progressively simpler names: full title, without edition, before " - " (DODI/xatab subtitles), before ":".
    /// Candidates are still validated against the full title by <see cref="Titles.Match"/>, so a shorter term can
    /// only match the same game or an edition of it, never a different game.
    /// </summary>
    static IEnumerable<string> SearchTerms(string title)
    {
        var seen = new HashSet<string>();
        title = TrailingVersion.Replace(title.Trim(), "");
        var noEdition = Edition.Replace(title, "").Trim(' ', '-', ':', '–', '—');
        var beforeDash = Dash.Split(title)[0].Trim();
        var beforeColon = title.Split(':')[0].Trim();
        var beforeDashNoEdition = Edition.Replace(beforeDash, "").Trim(' ', '-', ':', '–', '—');
        foreach (var t in new[] { title, noEdition, beforeDash, beforeDashNoEdition, beforeColon, beforeColon.Split(" - ")[0].Trim() })
            if (t.Length >= 3 && seen.Add(Titles.Normalize(t))) yield return t;
    }

    int _saveQueued;
    void SaveCacheSoon()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Delay(2000).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _saveQueued, 0);
            try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(_lookup)); } catch { }
        });
    }

    public async Task<SteamDetails?> DetailsAsync(int appId)
    {
        if (_details.TryGetValue(appId, out var c)) return c;
        var root = await Get($"https://store.steampowered.com/api/appdetails?appids={appId}&l=portuguese&cc=BR");
        var d = root?[appId.ToString()]?["data"];
        if (d == null) return null;
        List<string> Strings(JsonNode? n) => n?.AsArray().Select(x => x?.ToString() ?? "").ToList() ?? new();
        var details = new SteamDetails
        {
            AppId = appId,
            Name = d["name"]?.ToString() ?? "",
            ShortDescription = Format.HtmlToText(d["short_description"]?.ToString() ?? ""),
            About = Format.HtmlToText(d["about_the_game"]?.ToString() ?? ""),
            Developers = Strings(d["developers"]),
            Publishers = Strings(d["publishers"]),
            Genres = d["genres"]?.AsArray().Select(g => g?["description"]?.ToString() ?? "").ToList() ?? new(),
            ReleaseDate = d["release_date"]?["date"]?.ToString() ?? "",
            Metacritic = d["metacritic"]?["score"]?.GetValue<int>(),
            Recommendations = d["recommendations"]?["total"]?.GetValue<int>(),
            Screenshots = d["screenshots"]?.AsArray().Select(s => s?["path_full"]?.ToString() ?? "").Where(s => s.Length > 0).ToList() ?? new(),
            Controller = d["controller_support"]?.ToString()
        };
        _details[appId] = details;
        return details;
    }
}
