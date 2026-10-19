using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Venix.Core;

/// <summary>A cheat table (.ct) from the community repository.</summary>
public class CheatTable
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public string Key { get; set; } = "";      // normalized name with spaces
    public string Compact { get; set; } = "";  // normalized name without spaces

    /// <summary>Ships inside Venix (cheats\ folder next to the exe) instead of coming from the repository.</summary>
    public bool IsBundled => Path.StartsWith(CheatService.BundledPrefix, StringComparison.Ordinal);

    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeLabel => Format.Bytes(Size);
    public string Meta => IsBundled ? $"{FileName}  ·  {SizeLabel}  ·  incluída no Venix" : $"{FileName}  ·  {SizeLabel}";
}

/// <summary>One cheat inside a table, flattened for display.</summary>
public class CheatEntryInfo
{
    public string Description { get; init; } = "";
    public string Type { get; init; } = "";
    public int Depth { get; init; }
    public bool IsGroup { get; init; }
    public System.Windows.Thickness Indent => new(Depth * 18, 0, 0, 6);
    public string Icon => IsGroup ? Icons.Folder : Type.Contains("Script") ? "" : "";
}

public class CheatTableDetails
{
    public List<CheatEntryInfo> Entries { get; init; } = new();
    public string? Process { get; init; }
    public bool HasLua { get; init; }
    public int CheatCount => Entries.Count(e => !e.IsGroup);
}

/// <summary>
/// Index of https://github.com/Hexorg/CheatEngineTables (cached for a day), table download/parse,
/// for the built-in trainer.
/// </summary>
public class CheatService
{
    const string Repo = "Hexorg/CheatEngineTables";
    const string Branch = "master";
    static readonly string Dir = System.IO.Path.Combine(AppStore.DataDir, "cheats");
    static readonly string IndexFile = System.IO.Path.Combine(AppStore.DataDir, "cheats-index.json");

    readonly HttpClient _http;
    readonly AppStore _store;
    List<CheatTable> _tables = new();
    Task? _loading;

    public CheatService(HttpClient http, AppStore store)
    {
        _http = http;
        _store = store;
        Directory.CreateDirectory(Dir);
    }

    public IReadOnlyList<CheatTable> Tables => _tables;
    public int GroupCount => _tables.Where(t => t.Group != null).Select(t => t.Group).Distinct().Count();
    public DateTime? IndexedAt { get; private set; }

    /// <summary>Index path prefix of bundled tables ("venix/&lt;Game&gt;/file.ct"); never collides with repository paths ("tables/...").</summary>
    public const string BundledPrefix = "venix/";
    static readonly string BundledDir = System.IO.Path.Combine(AppContext.BaseDirectory, "cheats");

    public static string? RepoUrl(CheatTable t) => t.IsBundled ? null : $"https://github.com/{Repo}/blob/{Branch}/{EncodePath(t.Path)}";
    static string RawUrl(CheatTable t) => $"https://raw.githubusercontent.com/{Repo}/{Branch}/{EncodePath(t.Path)}";
    static string EncodePath(string p) => string.Join('/', p.Split('/').Select(Uri.EscapeDataString));

    /* ---------------------------------------------------------- index */

    public Task LoadAsync(bool force = false)
    {
        if (!force && _loading != null) return _loading;
        return _loading = LoadCore(force);
    }

    async Task LoadCore(bool force)
    {
        string? json = null;
        if (!force && File.Exists(IndexFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexFile) < TimeSpan.FromDays(1))
            json = await File.ReadAllTextAsync(IndexFile);
        if (json == null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/git/trees/{Branch}?recursive=1");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var res = await _http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                json = await res.Content.ReadAsStringAsync();
                await File.WriteAllTextAsync(IndexFile, json);
            }
            catch when (File.Exists(IndexFile))
            {
                // GitHub rate limit or offline: keep using the last index
                json = await File.ReadAllTextAsync(IndexFile);
            }
        }
        IndexedAt = File.Exists(IndexFile) ? File.GetLastWriteTime(IndexFile) : null;
        _tables = await Task.Run(() => Bundled().Concat(Parse(json!)).ToList());
    }

    /// <summary>Tables shipped in cheats\&lt;Game&gt;\*.ct next to the exe. They come first and count as curated folders.</summary>
    static List<CheatTable> Bundled()
    {
        var list = new List<CheatTable>();
        if (!Directory.Exists(BundledDir)) return list;
        foreach (var file in Directory.EnumerateFiles(BundledDir, "*.ct", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = System.IO.Path.GetRelativePath(BundledDir, file).Replace('\\', '/');
            var parts = rel.Split('/');
            var group = parts.Length >= 2 ? parts[0] : NameFromFile(parts[^1]);
            var key = Titles.Normalize(group);
            if (key.Length == 0) continue;
            list.Add(new CheatTable
            {
                Path = BundledPrefix + rel,
                Size = new FileInfo(file).Length,
                Name = group,
                Group = group,
                Key = key,
                Compact = key.Replace(" ", "")
            });
        }
        return list;
    }

    static List<CheatTable> Parse(string json)
    {
        var list = new List<CheatTable>();
        foreach (var n in JsonNode.Parse(json)?["tree"]?.AsArray() ?? new JsonArray())
        {
            var path = n?["path"]?.ToString() ?? "";
            if (n?["type"]?.ToString() != "blob" || !path.EndsWith(".ct", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = path.Split('/');
            // tables/<Game>/file.ct → grouped by folder; tables/file.ct → name from file
            var group = parts.Length >= 3 ? parts[1] : null;
            var name = group ?? NameFromFile(parts[^1]);
            var key = Titles.Normalize(name);
            if (key.Length == 0) continue;
            list.Add(new CheatTable
            {
                Path = path,
                Size = n?["size"]?.GetValue<long>() ?? 0,
                Name = name,
                Group = group,
                Key = key,
                Compact = key.Replace(" ", "")
            });
        }
        // folders curated per game first, then readable names, numeric/garbage names last
        return list.OrderBy(t => t.Group != null ? 0 : char.IsLetter(t.Name[0]) && t.Key.Length >= 4 ? 1 : 2)
                   .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static readonly Regex TrailingId = new(@"_\d{3}$", RegexOptions.Compiled);

    /// <summary>"01_evoland2_685.ct" → "Evoland2"; "dead_island_riptide_150.ct" → "Dead Island Riptide".</summary>
    static string NameFromFile(string file)
    {
        var s = System.IO.Path.GetFileNameWithoutExtension(file);
        s = TrailingId.Replace(s, "");
        s = Regex.Replace(s, @"^\d+[._]", "");
        s = Regex.Replace(s, @"[_\-.]+", " ").Trim();
        s = Regex.Replace(s, @"\s+", " ");
        if (s.Length == 0) s = System.IO.Path.GetFileNameWithoutExtension(file);
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    public (int total, List<CheatTable> items) Query(string q, bool groupedOnly, int offset, int limit)
    {
        IEnumerable<CheatTable> list = _tables;
        if (groupedOnly) list = list.Where(t => t.Group != null);
        var nq = Titles.Normalize(q);
        if (nq.Length > 0)
        {
            var compact = nq.Replace(" ", "");
            list = list.Where(t => t.Key.Contains(nq) || t.Compact.Contains(compact) || t.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        var arr = list.ToList();
        return (arr.Count, arr.Skip(offset).Take(limit).ToList());
    }

    /// <summary>
    /// Tables whose name looks like the given game (folder matches rank first). Synchronous, in-memory.
    /// Score 3 = same name, 2 = edition/variant of the same name, 1 = same franchise only (name before ":" or "-").
    /// <paramref name="minScore"/> 2 keeps only tables that are really for this game, dropping franchise guesses.
    /// </summary>
    public List<CheatTable> FindForGame(string gameName, int minScore = 1)
    {
        var key = Titles.Normalize(Titles.Clean(gameName));
        var compact = key.Replace(" ", "");
        if (compact.Length < 4) return new();
        var noEdition = Titles.Normalize(Regex.Replace(gameName, @"[-–:].*$", "")).Replace(" ", "");
        return _tables
            .Select(t => (t, score:
                t.Compact == compact ? 3 :
                t.Compact.StartsWith(compact) && !char.IsDigit(t.Compact.ElementAtOrDefault(compact.Length)) ? 2 :
                noEdition.Length >= 5 && t.Compact.StartsWith(noEdition) && !char.IsDigit(t.Compact.ElementAtOrDefault(noEdition.Length)) ? 1 : 0))
            .Where(x => x.score >= Math.Max(1, minScore))
            .OrderByDescending(x => x.score).ThenByDescending(x => x.t.Group != null).ThenByDescending(x => x.t.Size)
            .Select(x => x.t).Take(30).ToList();
    }

    /* ---------------------------------------------------------- files */

    public string LocalPath(CheatTable t) => t.IsBundled
        ? System.IO.Path.Combine(BundledDir, t.Path[BundledPrefix.Length..].Replace('/', '\\'))
        : System.IO.Path.Combine(Dir, t.Path.Replace('/', '\\'));
    public bool IsDownloaded(CheatTable t) => File.Exists(LocalPath(t));

    public async Task<string> DownloadAsync(CheatTable t)
    {
        var file = LocalPath(t);
        if (File.Exists(file)) return file;
        if (t.IsBundled) throw new FileNotFoundException("Tabela incluída não encontrada ao lado do Venix.exe.", file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var bytes = await _http.GetByteArrayAsync(RawUrl(t));
        await File.WriteAllBytesAsync(file, bytes);
        return file;
    }

    public async Task<CheatTableDetails> DetailsAsync(CheatTable t)
    {
        var file = await DownloadAsync(t);
        return await Task.Run(() => ParseTable(File.ReadAllText(file)));
    }

    static CheatTableDetails ParseTable(string xml)
    {
        var entries = new List<CheatEntryInfo>();
        string? process = null;
        try
        {
            var doc = XDocument.Parse(xml);
            void Walk(XElement? parent, int depth)
            {
                if (parent == null) return;
                foreach (var e in parent.Elements("CheatEntry"))
                {
                    var desc = (e.Element("Description")?.Value ?? "").Trim().Trim('"').Trim();
                    var children = e.Element("CheatEntries");
                    var isGroup = children != null && children.Elements("CheatEntry").Any();
                    if (desc.Length > 0)
                        entries.Add(new CheatEntryInfo { Description = desc, Type = e.Element("VariableType")?.Value ?? (isGroup ? "Grupo" : ""), Depth = Math.Min(depth, 4), IsGroup = isGroup });
                    Walk(children, depth + 1);
                }
            }
            Walk(doc.Root?.Element("CheatEntries"), 0);
        }
        catch { /* malformed tables: show no preview */ }
        var m = Regex.Match(xml, @"([\w\-. ]{2,60}\.exe)\b", RegexOptions.IgnoreCase);
        if (m.Success) process = m.Groups[1].Value.Trim();
        return new CheatTableDetails { Entries = entries, Process = process, HasLua = xml.Contains("<LuaScript", StringComparison.OrdinalIgnoreCase) || xml.Contains("{$lua}", StringComparison.OrdinalIgnoreCase) };
    }


}
