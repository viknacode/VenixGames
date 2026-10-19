using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Venix.Core;

/// <summary>
/// Loads user-configured download sources (Hydra-compatible JSON: { name, downloads: [{ title, uris, uploadDate, fileSize }] })
/// and keeps an in-memory index used by the catalog and the per-game download options.
/// </summary>
public class SourceService
{
    static readonly string Dir = Path.Combine(AppStore.DataDir, "sources");
    readonly AppStore _store;
    readonly HttpClient _http;
    List<SourceEntry> _entries = new();
    List<CatalogGroup>? _groups;
    List<ExtraEntry> _extras = new();
    SearchIndex? _index;
    readonly object _indexLock = new();

    public event Action? IndexChanged;
    public int EntryCount => _entries.Count;
    public int GameCount => Groups.Count;
    /// <summary>Every update, patch and fix of every list, newest first, each tied to its catalog game (built with the index, off the UI thread).</summary>
    public IReadOnlyList<ExtraEntry> Extras => _extras;

    public SourceService(AppStore store, HttpClient http)
    {
        _store = store;
        _http = http;
        Directory.CreateDirectory(Dir);
    }

    // the search index is built right after the entries, still off the UI thread, so the first keystroke is instant
    public Task LoadAsync() => Task.Run(() => { Rebuild(); _ = Index; }).ContinueWith(_ => Ui(() => IndexChanged?.Invoke()));

    static void Ui(Action a) => Application.Current.Dispatcher.Invoke(a);

    void Rebuild()
    {
        var list = new List<SourceEntry>();
        foreach (var src in _store.Sources.ToList())
        {
            // lists from the sources folder are read in place; the others use the cached copy
            var file = src.FromConfig && src.FilePath != null && File.Exists(src.FilePath) ? src.FilePath : Path.Combine(Dir, src.Id + ".json");
            if (!File.Exists(file)) continue;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file));
                foreach (var d in root?["downloads"]?.AsArray() ?? new JsonArray())
                {
                    var title = d?["title"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var clean = Titles.Clean(title);
                    list.Add(new SourceEntry
                    {
                        SourceId = src.Id,
                        SourceName = src.Name,
                        Title = title,
                        Clean = clean,
                        Norm = Titles.Normalize(clean),
                        Uris = d?["uris"]?.AsArray().Select(u => u?.GetValue<string>() ?? "").Where(u => u.Length > 0).ToList() ?? new(),
                        UploadDate = d?["uploadDate"]?.ToString(),
                        FileSize = d?["fileSize"]?.ToString() ?? "",
                        Kind = EntryClassifier.Classify(title)
                    });
                }
            }
            catch { /* a corrupted cache file just means the source needs a re-sync */ }
        }
        // groups (and the update/patch/fix links) are built here, still off the UI thread
        var (groups, extras) = BuildGroups(list);
        _entries = list;
        _extras = extras;
        lock (_indexLock) { _groups = groups; _index = null; }
    }

    static (string? name, JsonArray downloads) Parse(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch
        {
            if (text.Contains("<html", StringComparison.OrdinalIgnoreCase))
                throw new Exception("O servidor respondeu com uma página HTML em vez do JSON (provável proteção anti-bot). Baixe o arquivo .json pelo navegador e use \"Importar arquivo\".");
            throw new Exception("O conteúdo não é um JSON válido.");
        }
        if (root?["downloads"] is not JsonArray arr) throw new Exception("JSON sem a lista \"downloads\".");
        return (root["name"]?.ToString(), arr);
    }

    void Store(DownloadSource src, string text)
    {
        var (name, arr) = Parse(text);
        if (!src.FromConfig) File.WriteAllText(Path.Combine(Dir, src.Id + ".json"), text);
        Ui(() =>
        {
            if (!string.IsNullOrWhiteSpace(name)) src.Name = name!;
            src.Count = arr.Count;
            src.Status = "ok";
            src.Error = null;
            src.SyncedAt = DateTime.UtcNow;
        });
    }

    async Task SyncOne(DownloadSource src)
    {
        if (string.IsNullOrEmpty(src.Url)) return;
        Ui(() => { src.Status = "syncing"; src.Error = null; });
        try
        {
            // a source that hangs must not hold the splash screen forever
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var text = await _http.GetStringAsync(src.Url, cts.Token);
            await Task.Run(() => Store(src, text));
        }
        catch (Exception ex)
        {
            var msg = ex switch
            {
                OperationCanceledException => "Tempo esgotado ao baixar a lista.",
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.ServiceUnavailable } =>
                    "O site bloqueou o download automático (proteção anti-bot, HTTP 403). Abra o link no navegador, salve o .json na pasta \"sources\" ao lado do Venix.exe e reabra o Venix.",
                _ => ex.Message
            };
            Ui(() => { src.Status = "error"; src.Error = msg; });
        }
    }

    public async Task<DownloadSource> AddAsync(string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new Exception("Informe uma URL http(s) válida.");
        if (_store.Sources.Any(s => s.Url == url)) throw new Exception("Essa fonte já foi adicionada.");
        var src = new DownloadSource { Url = url, Name = Path.GetFileNameWithoutExtension(uri.AbsolutePath) };
        _store.Sources.Add(src);
        await SyncOne(src);
        _store.SaveSoon();
        await LoadAsync();
        return src;
    }

    public async Task<DownloadSource> ImportAsync(string file)
    {
        var text = await File.ReadAllTextAsync(file);
        var src = new DownloadSource { FilePath = file, Name = Path.GetFileNameWithoutExtension(file) };
        await Task.Run(() => Store(src, text));
        _store.Sources.Add(src);
        _store.SaveSoon();
        await LoadAsync();
        return src;
    }

    public async Task RemoveAsync(DownloadSource src)
    {
        _store.Sources.Remove(src);
        try { File.Delete(Path.Combine(Dir, src.Id + ".json")); } catch { }
        _store.SaveSoon();
        await LoadAsync();
    }

    public async Task SyncAsync(DownloadSource? only = null)
    {
        var list = only != null ? new List<DownloadSource> { only } : _store.Sources.ToList();
        await Task.WhenAll(list.Select(SyncOne));
        _store.SaveSoon();
        await LoadAsync();
    }

    /* ---------------------------------------------------------- sources folder */

    /// <summary>Every .json in this folder (next to Venix.exe) is a download list; the folder is the source of truth.</summary>
    public static readonly string ConfigFolder = Path.Combine(AppContext.BaseDirectory, "sources");

    /// <summary>The .json files currently in the sources folder (creates the folder when missing).</summary>
    public static List<string> ReadConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigFolder);
            var legacy = Path.Combine(AppContext.BaseDirectory, "fontes"); // older builds used this name
            return new[] { ConfigFolder, legacy }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.json"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new(); } // read-only install folder: just skip
    }

    /// <summary>
    /// Makes the folder-managed sources match the sources folder: new files are added, removed files
    /// leave the catalog, changed files are re-read. Returns (added, removed, failed).
    /// </summary>
    public async Task<(int added, int removed, int failed)> ApplyConfigAsync()
    {
        var files = ReadConfig();
        int added = 0, removed = 0;

        foreach (var src in _store.Sources.Where(s => s.FromConfig && !files.Contains(s.FilePath ?? "", StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _store.Sources.Remove(src);
            try { File.Delete(Path.Combine(Dir, src.Id + ".json")); } catch { }
            removed++;
        }

        var toLoad = new List<DownloadSource>();
        foreach (var file in files)
        {
            var existing = _store.Sources.FirstOrDefault(s => string.Equals(s.FilePath, file, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.FromConfig = true;
                if (existing.Status == "error" || existing.Count == 0 || File.GetLastWriteTimeUtc(file) > (existing.SyncedAt ?? DateTime.MinValue))
                    toLoad.Add(existing);
                continue;
            }
            var src = new DownloadSource { FilePath = file, Name = Path.GetFileNameWithoutExtension(file), FromConfig = true, Status = "syncing" };
            _store.Sources.Add(src);
            toLoad.Add(src);
            added++;
        }

        await Task.WhenAll(toLoad.Select(LoadLocal));
        _store.SaveSoon();
        await LoadAsync();
        var failed = _store.Sources.Count(s => s.FromConfig && s.Status == "error");
        return (added, removed, failed);
    }

    async Task LoadLocal(DownloadSource src)
    {
        Ui(() => { src.Status = "syncing"; src.Error = null; });
        try
        {
            await Task.Run(async () => Store(src, await File.ReadAllTextAsync(src.FilePath!)));
        }
        catch (Exception ex)
        {
            var msg = ex is FileNotFoundException or DirectoryNotFoundException ? "Arquivo não encontrado." : ex.Message;
            Ui(() => { src.Status = "error"; src.Error = msg; });
        }
    }

    /* ---------------------------------------------------------- queries */

    List<CatalogGroup> Groups
    {
        get { lock (_indexLock) return _groups ??= BuildGroups(_entries).groups; }
    }

    static (List<CatalogGroup> groups, List<ExtraEntry> extras) BuildGroups(List<SourceEntry> entries)
    {
        var map = new Dictionary<string, CatalogGroup>();
        // only full games become catalog entries…
        foreach (var e in entries)
        {
            if (!e.IsGame || e.Norm.Length == 0) continue;
            if (!map.TryGetValue(e.Norm, out var g)) map[e.Norm] = g = new CatalogGroup { Key = e.Norm, Title = e.Clean };
            if (!g.Sources.Contains(e.SourceName)) g.Sources.Add(e.SourceName);
            g.Count++;
            if (e.UploadDate != null && string.CompareOrdinal(e.UploadDate, g.Latest) > 0) g.Latest = e.UploadDate;
        }
        // …updates, patches and fixes hang off the game they belong to (one with no game in any list is simply not a catalog item)
        var extras = new List<ExtraEntry>();
        foreach (var e in entries)
        {
            if (e.IsGame) continue;
            var g = e.Norm.Length > 0 ? FindGroup(map, e.Norm) : null;
            extras.Add(new ExtraEntry { Entry = e, Game = g });
            if (g == null) continue;
            switch (e.Kind)
            {
                case EntryKind.Update: g.Updates++; break;
                case EntryKind.Patch: g.Patches++; break;
                default: g.Fixes++; break;
            }
            if (!g.Sources.Contains(e.SourceName)) g.Sources.Add(e.SourceName);
        }
        extras.Sort((a, b) => string.CompareOrdinal(b.Entry.UploadDate ?? "", a.Entry.UploadDate ?? ""));
        return (map.Values.ToList(), extras);
    }

    /// <summary>
    /// The game an update/fix title belongs to: the exact name first, then shorter and shorter prefixes
    /// ("no mans sky worlds1" → "no mans sky"); <see cref="Titles.Match"/> keeps sequels apart.
    /// </summary>
    static CatalogGroup? FindGroup(Dictionary<string, CatalogGroup> map, string norm)
    {
        var key = norm;
        while (key.Length > 0)
        {
            if (map.TryGetValue(key, out var g) && Titles.Match(key, norm)) return g;
            var i = key.LastIndexOf(' ');
            if (i < 0) break;
            key = key[..i];
        }
        return null;
    }

    /// <summary>Ranked, typo-tolerant search over the catalog (see <see cref="SearchIndex"/>); built once per index rebuild.</summary>
    public SearchIndex Index
    {
        get { lock (_indexLock) return _index ??= SearchIndex.Build(Groups); }
    }

    public (int total, List<CatalogGroup> items) Query(string query, string? source, string sort, int offset, int limit)
    {
        IEnumerable<CatalogGroup> list = Groups;
        var q = Titles.Normalize(query);
        if (q.Length > 0) list = list.Where(g => g.Key.Contains(q));
        if (!string.IsNullOrEmpty(source)) list = list.Where(g => g.Sources.Contains(source));
        list = sort switch
        {
            "az" => list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
            "options" => list.OrderByDescending(g => g.Count),
            _ => list.OrderByDescending(g => g.Latest ?? "", StringComparer.Ordinal)
        };
        var arr = list.ToList();
        return (arr.Count, arr.Skip(offset).Take(limit).ToList());
    }

    /// <summary>Every option for a game — full game first, then updates, patches and fixes, newest first within each kind.</summary>
    public List<SourceEntry> FindRepacks(string gameName)
    {
        var target = Titles.Normalize(Titles.Clean(gameName));
        return _entries.Where(e => Titles.Match(target, e.Norm))
            .OrderBy(e => e.Kind)
            .ThenByDescending(e => e.UploadDate ?? "", StringComparer.Ordinal)
            .ToList();
    }

    public List<string> SourceNames => _store.Sources.Select(s => s.Name).Distinct().ToList();
}
