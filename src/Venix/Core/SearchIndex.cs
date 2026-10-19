using System.Runtime.CompilerServices;
using System.Text;

namespace Venix.Core;

/// <summary>
/// In-memory search index over the catalog (one entry per game, built from <see cref="CatalogGroup"/>s).
/// Built once when the sources load and reused for every keystroke; a search over 25k titles takes a few ms.
///
/// Matching: title and query go through the same normalization (lower case, no diacritics/symbols,
/// "&amp;" = "and", roman numerals → arabic, "re4" → "re 4"). Every query token must match the title by
/// exact word, word prefix, prefix of the glued words ("spiderman"), initials ("cod", "aoe", "gta")
/// or one typo (tokens with 5+ letters). Well-known abbreviations are also expanded ("gta v" → "grand theft auto 5").
///
/// Ranking: exact title > title starts with the query > query appears as a run of words > every token at a
/// word start (in order first) > initials > typo. Within a tier shorter titles and titles with more
/// download options / sources come first.
/// </summary>
public sealed class SearchIndex
{
    /// <summary>Most results returned for a non-empty query; keeps the catalog page light.</summary>
    public const int MaxResults = 50;

    sealed class Entry
    {
        public CatalogGroup Group = null!;
        public string Norm = "";        // "age of empires 2 definitive edition"
        public string[] Tokens = Array.Empty<string>();
        public string Joined = "";      // "ageofempires2definitiveedition"
        public int[] Starts = Array.Empty<int>(); // offset of each token inside Joined
        public string Initials = "";    // "aoe2de"
        public double Popularity;       // ln(1+options)*12 + sources*3
    }

    readonly Entry[] _entries;
    readonly Dictionary<string, List<CatalogGroup>> _sorted = new();
    readonly object _sortLock = new();

    SearchIndex(Entry[] entries) => _entries = entries;

    public int Count => _entries.Length;

    public static SearchIndex Build(IEnumerable<CatalogGroup> groups)
    {
        var list = new List<Entry>();
        foreach (var g in groups)
        {
            var tokens = Tokenize(g.Key.Length > 0 ? g.Key : g.Title);
            if (tokens.Length > 0) list.Add(Make(tokens, g));
        }
        return new SearchIndex(list.ToArray());
    }

    static Entry Make(string[] tokens, CatalogGroup g)
    {
        var starts = new int[tokens.Length];
        var sb = new StringBuilder();
        var ini = new StringBuilder(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            starts[i] = sb.Length;
            sb.Append(tokens[i]);
            ini.Append(tokens[i][0]);
        }
        return new Entry
        {
            Group = g,
            Norm = string.Join(' ', tokens),
            Tokens = tokens,
            Joined = sb.ToString(),
            Starts = starts,
            Initials = ini.ToString(),
            Popularity = Math.Log(1 + g.Count) * 12 + g.Sources.Count * 3
        };
    }

    /* ---------------------------------------------------------- highlighting */

    /// <summary>
    /// Character spans of <paramref name="title"/> (as displayed) that made it match <paramref name="query"/>,
    /// sorted and non-overlapping; empty when the title does not match. Uses the same token matching as <see cref="Search"/>.
    /// </summary>
    public static List<(int start, int length)> Highlight(string title, string query)
    {
        var spans = new List<(int start, int length)>();
        var q = Tokenize(query);
        if (q.Length == 0 || string.IsNullOrEmpty(title)) return spans;
        var (tokens, map) = TokenizeWithMap(title);
        if (tokens.Length == 0) return spans;
        var e = Make(tokens, new CatalogGroup());

        string[]? best = null;
        double bestScore = 0;
        foreach (var (vt, norm, expanded) in Variants(q))
        {
            var s = Score(e, vt, norm);
            if (s <= 0) continue;
            if (expanded && s >= 3000) s += 500;
            if (s > bestScore) { bestScore = s; best = vt; }
        }
        if (best == null) return spans;

        // original span of the first n normalized chars of token j (the whole token when lengths disagree: "ii" → "2", "&" → "and")
        (int, int) Part(int j, int n)
        {
            var idx = map[j];
            if (n >= tokens[j].Length || idx.Length != tokens[j].Length) return (idx[0], idx[^1] + 1 - idx[0]);
            return (idx[0], idx[n - 1] + 1 - idx[0]);
        }

        foreach (var qt in best)
        {
            var (kind, pos) = MatchToken(e, qt);
            if (kind <= 0 || pos < 0) continue;
            if (kind >= 3) spans.Add(Part(pos, int.MaxValue));
            else if (kind >= 2) spans.Add(Part(pos, qt.Length));
            else if (kind >= 1.8)
            {
                var left = qt.Length;
                for (var j = pos; left > 0 && j < tokens.Length; j++) { var take = Math.Min(left, tokens[j].Length); spans.Add(Part(j, take)); left -= take; }
            }
            else if (kind >= 1.2)
                for (var k = 0; k < qt.Length && pos + k < tokens.Length; k++) spans.Add((map[pos + k][0], 1));
            else spans.Add(Part(pos, int.MaxValue));
        }

        spans.Sort((a, b) => a.start.CompareTo(b.start));
        var merged = new List<(int start, int length)>();
        foreach (var s in spans)
        {
            if (merged.Count > 0 && s.start <= merged[^1].start + merged[^1].length)
            {
                var last = merged[^1];
                merged[^1] = (last.start, Math.Max(last.start + last.length, s.start + s.length) - last.start);
            }
            else merged.Add(s);
        }
        return merged;
    }

    /// <summary>Same tokens as <see cref="Tokenize"/>, plus the source index of every normalized character of each token.</summary>
    static (string[] tokens, int[][] map) TokenizeWithMap(string s)
    {
        var chars = new List<char>(s.Length);
        var src = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            foreach (var d in char.ToLowerInvariant(s[i]).ToString().Normalize(System.Text.NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(d) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (d is '\'' or '’' or '`' or '™' or '®' or '©') continue;
                if (d == '&') { foreach (var x in " and ") { chars.Add(x); src.Add(i); } continue; }
                chars.Add(d is >= 'a' and <= 'z' or >= '0' and <= '9' ? d : ' ');
                src.Add(i);
            }
        }
        var tokens = new List<string>();
        var map = new List<int[]>();
        var start = -1;
        void Flush(int end)
        {
            if (start < 0) return;
            var segStart = start;
            for (var i = start + 1; i <= end; i++)
            {
                if (i == end || char.IsDigit(chars[i]) != char.IsDigit(chars[i - 1]))
                {
                    var tok = new string(chars.GetRange(segStart, i - segStart).ToArray());
                    if (tok.Length <= 5 && Roman.TryGetValue(tok, out var num)) tok = num;
                    tokens.Add(tok);
                    map.Add(src.GetRange(segStart, i - segStart).ToArray());
                    segStart = i;
                }
            }
            start = -1;
        }
        for (var i = 0; i < chars.Count; i++)
        {
            if (chars[i] == ' ') Flush(i);
            else if (start < 0) start = i;
        }
        Flush(chars.Count);
        return (tokens.ToArray(), map.ToArray());
    }

    /* ---------------------------------------------------------- normalization */

    static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
        ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15", ["xvi"] = "16", ["xvii"] = "17", ["xviii"] = "18", ["xix"] = "19", ["xx"] = "20"
    };

    /// <summary>Common abbreviations typed by players; expanded to a second query variant.</summary>
    static readonly Dictionary<string, string[]> Abbreviations = new()
    {
        ["cod"] = new[] { "call", "of", "duty" }, ["mw"] = new[] { "modern", "warfare" }, ["bo"] = new[] { "black", "ops" },
        ["gta"] = new[] { "grand", "theft", "auto" }, ["aoe"] = new[] { "age", "of", "empires" }, ["aom"] = new[] { "age", "of", "mythology" },
        ["re"] = new[] { "resident", "evil" }, ["ac"] = new[] { "assassins", "creed" }, ["nfs"] = new[] { "need", "for", "speed" },
        ["mk"] = new[] { "mortal", "kombat" }, ["tlou"] = new[] { "the", "last", "of", "us" }, ["rdr"] = new[] { "red", "dead", "redemption" },
        ["ff"] = new[] { "final", "fantasy" }, ["dmc"] = new[] { "devil", "may", "cry" }, ["mgs"] = new[] { "metal", "gear", "solid" },
        ["bf"] = new[] { "battlefield" }, ["kcd"] = new[] { "kingdom", "come", "deliverance" }, ["hzd"] = new[] { "horizon", "zero", "dawn" },
        ["botw"] = new[] { "breath", "of", "the", "wild" }, ["totk"] = new[] { "tears", "of", "the", "kingdom" }, ["pes"] = new[] { "pro", "evolution", "soccer" },
        ["fm"] = new[] { "football", "manager" }, ["coh"] = new[] { "company", "of", "heroes" }, ["hoi"] = new[] { "hearts", "of", "iron" },
        ["ck"] = new[] { "crusader", "kings" }, ["sotf"] = new[] { "sons", "of", "the", "forest" }, ["wow"] = new[] { "world", "of", "warcraft" },
        ["lol"] = new[] { "league", "of", "legends" }, ["pubg"] = new[] { "playerunknowns", "battlegrounds" }, ["gow"] = new[] { "god", "of", "war" },
        ["tes"] = new[] { "the", "elder", "scrolls" }, ["nms"] = new[] { "no", "mans", "sky" }, ["fnaf"] = new[] { "five", "nights", "at", "freddys" },
        ["r6"] = new[] { "rainbow", "six" }, ["civ"] = new[] { "civilization" }, ["bg"] = new[] { "baldurs", "gate" }, ["poe"] = new[] { "path", "of", "exile" },
        ["dbd"] = new[] { "dead", "by", "daylight" }, ["sf"] = new[] { "street", "fighter" }, ["mh"] = new[] { "monster", "hunter" }, ["mhw"] = new[] { "monster", "hunter", "world" },
        ["tw"] = new[] { "total", "war" }, ["cs"] = new[] { "counter", "strike" }, ["csgo"] = new[] { "counter", "strike", "global", "offensive" },
        ["ow"] = new[] { "overwatch" }, ["tf"] = new[] { "team", "fortress" }, ["ror"] = new[] { "risk", "of", "rain" }, ["rl"] = new[] { "rocket", "league" },
        ["ds"] = new[] { "dark", "souls" },
        ["wd"] = new[] { "watch", "dogs" }, ["fh"] = new[] { "forza", "horizon" },
        ["dl"] = new[] { "dying", "light" }, ["ds3"] = new[] { "dark", "souls", "3" }, ["bg3"] = new[] { "baldurs", "gate", "3" }, ["hd2"] = new[] { "helldivers", "2" }
    };

    /// <summary>
    /// Normalizes text the same way for titles and queries: <see cref="Titles.Normalize"/> (lower, ASCII, no symbols,
    /// "&amp;" → "and"), then letters and digits become separate tokens ("re4" → "re 4") and roman numerals become numbers.
    /// </summary>
    public static string[] Tokenize(string text)
    {
        var norm = Titles.Normalize(text);
        if (norm.Length == 0) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var word in norm.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = 0;
            for (var i = 1; i <= word.Length; i++)
            {
                if (i == word.Length || char.IsDigit(word[i]) != char.IsDigit(word[i - 1]))
                {
                    var tok = word[start..i];
                    if (tok.Length <= 5 && Roman.TryGetValue(tok, out var num)) tok = num;
                    list.Add(tok);
                    start = i;
                }
            }
        }
        return list.ToArray();
    }

    /* ---------------------------------------------------------- search */

    /// <summary>
    /// Ranked search. <paramref name="sort"/> ("recent", "az", "options") orders the full list for an empty query
    /// and breaks ties between equally relevant hits otherwise. Returns the number of matches and up to
    /// <see cref="MaxResults"/> groups (all of them when the query is empty).
    /// </summary>
    public (int total, List<CatalogGroup> items) Search(string query, string? source, string sort, CancellationToken ct = default)
    {
        var q = Tokenize(query);
        if (q.Length == 0)
        {
            var all = Sorted(sort);
            if (string.IsNullOrEmpty(source)) return (all.Count, all);
            var filtered = all.Where(g => g.Sources.Contains(source)).ToList();
            return (filtered.Count, filtered);
        }

        var variants = Variants(q);
        var hits = new List<(double score, Entry e)>();
        for (var i = 0; i < _entries.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var e = _entries[i];
            if (source != null && !e.Group.Sources.Contains(source)) continue;
            double best = 0;
            foreach (var (tokens, norm, expanded) in variants)
            {
                var s = Score(e, tokens, norm);
                if (s <= 0) continue;
                if (expanded && s >= 3000) s += 500; // an expanded abbreviation is a strong intent signal
                if (s > best) best = s;
            }
            if (best > 0) hits.Add((best + e.Popularity, e));
        }
        ct.ThrowIfCancellationRequested();

        var tie = Comparer(sort);
        hits.Sort((a, b) =>
        {
            var c = b.score.CompareTo(a.score);
            return c != 0 ? c : tie(a.e.Group, b.e.Group);
        });
        var items = new List<CatalogGroup>(Math.Min(hits.Count, MaxResults));
        for (var i = 0; i < hits.Count && i < MaxResults; i++) items.Add(hits[i].e.Group);
        return (hits.Count, items);
    }

    static List<(string[] tokens, string norm, bool expanded)> Variants(string[] q)
    {
        var list = new List<(string[], string, bool)> { (q, string.Join(' ', q), false) };
        if (q.Any(Abbreviations.ContainsKey))
        {
            var ex = q.SelectMany(t => Abbreviations.TryGetValue(t, out var words) ? words : new[] { t }).ToArray();
            list.Add((ex, string.Join(' ', ex), true));
        }
        return list;
    }

    /// <summary>Relevance of one entry for one query variant; 0 when some token has no match.</summary>
    static double Score(Entry e, string[] q, string qNorm)
    {
        var t = e.Tokens;
        double coverage, tokenScore;
        int tier, firstPos;

        if (e.Norm == qNorm) { tier = 6; coverage = 1; tokenScore = 3 * q.Length; firstPos = 0; }
        else if (e.Norm.StartsWith(qNorm, StringComparison.Ordinal)) { tier = 5; coverage = (double)q.Length / t.Length; tokenScore = 3 * q.Length - 1; firstPos = 0; }
        else if (e.Norm.Contains(" " + qNorm, StringComparison.Ordinal)) { tier = 4; coverage = (double)q.Length / t.Length; tokenScore = 3 * q.Length - 1; firstPos = 1; }
        else
        {
            // every query token must match somewhere; remember the weakest kind of match
            var minKind = 3.0; tokenScore = 0; firstPos = int.MaxValue;
            var lastPos = -1; var inOrder = true; var covered = 0;
            foreach (var qt in q)
            {
                var (kind, pos) = MatchToken(e, qt);
                if (kind <= 0) return 0;
                tokenScore += kind;
                if (kind < minKind) minKind = kind;
                if (pos < firstPos) firstPos = pos;
                if (pos <= lastPos) inOrder = false;
                lastPos = pos;
                covered++;
            }
            tier = minKind >= 1.8 ? 3 : minKind >= 1.2 ? 2 : 1;
            coverage = Math.Min(1.0, (double)covered / t.Length);
            if (tier == 3 && inOrder) tokenScore += 5;
        }

        return tier * 1000 + tokenScore * 10 + coverage * 100 + (firstPos == 0 ? 30 : 0);
    }

    /// <summary>Best match of one query token against a title: 3 exact, 2 prefix, 1.8 glued-words prefix, 1.5/1.2 initials, 1 one typo.</summary>
    static (double kind, int pos) MatchToken(Entry e, string qt)
    {
        var t = e.Tokens;
        var prefixPos = -1;
        for (var j = 0; j < t.Length; j++)
        {
            var tt = t[j];
            if (tt.Length < qt.Length) continue;
            if (tt.Length == qt.Length) { if (tt == qt) return (3, j); }
            else if (prefixPos < 0 && tt.StartsWith(qt, StringComparison.Ordinal)) prefixPos = j;
        }
        if (prefixPos >= 0) return (2, prefixPos);

        if (qt.Length >= 3)
            for (var j = 0; j < e.Starts.Length; j++)
                if (e.Joined.AsSpan(e.Starts[j]).StartsWith(qt, StringComparison.Ordinal)) return (1.8, j);

        if (qt.Length >= 2 && t.Length >= 2 && IsLetters(qt))
        {
            if (e.Initials.StartsWith(qt, StringComparison.Ordinal)) return (1.5, 0);
            if (qt.Length >= 3)
            {
                var at = e.Initials.IndexOf(qt, StringComparison.Ordinal);
                if (at >= 0) return (1.2, at);
            }
        }

        if (qt.Length >= 5)
            for (var j = 0; j < t.Length; j++)
            {
                var tt = t[j];
                if (tt.Length + 1 < qt.Length) continue;
                var span = tt.AsSpan();
                if (Within1(qt, span.Length > qt.Length + 1 ? span[..(qt.Length + 1)] : span)) return (1, j);
                if (span.Length > qt.Length && Within1(qt, span[..qt.Length])) return (1, j);
            }
        return (0, -1);
    }

    static bool IsLetters(string s)
    {
        foreach (var c in s) if (c is < 'a' or > 'z') return false;
        return true;
    }

    /// <summary>Damerau-Levenshtein distance ≤ 1 (one substitution, insertion, deletion or adjacent swap), in O(n).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool Within1(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > 1) return false;
        var i = 0;
        while (i < la && i < lb && a[i] == b[i]) i++;
        if (i == la && i == lb) return true;
        if (la == lb)
        {
            if (a[(i + 1)..].SequenceEqual(b[(i + 1)..])) return true;                       // substitution
            return i + 1 < la && a[i] == b[i + 1] && a[i + 1] == b[i] && a[(i + 2)..].SequenceEqual(b[(i + 2)..]); // swap
        }
        return la < lb ? a[i..].SequenceEqual(b[(i + 1)..]) : a[(i + 1)..].SequenceEqual(b[i..]);   // insertion / deletion
    }

    /* ---------------------------------------------------------- ordering */

    static Comparison<CatalogGroup> Comparer(string sort) => sort switch
    {
        "az" => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title),
        "options" => (a, b) => b.Count.CompareTo(a.Count),
        _ => (a, b) => string.CompareOrdinal(b.Latest ?? "", a.Latest ?? "")
    };

    /// <summary>The whole catalog in one of the chip orders (cached per order).</summary>
    List<CatalogGroup> Sorted(string sort)
    {
        lock (_sortLock)
        {
            if (_sorted.TryGetValue(sort, out var list)) return list;
            list = _entries.Select(e => e.Group).ToList();
            list.Sort(Comparer(sort));
            return _sorted[sort] = list;
        }
    }
}
