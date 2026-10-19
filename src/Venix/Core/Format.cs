using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Venix.Core;

public static class Format
{
    static readonly CultureInfo Br = new("pt-BR");

    public static string Bytes(double b)
    {
        if (b <= 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b.ToString(b >= 100 || i == 0 ? "0" : "0.0", Br)} {u[i]}";
    }

    public static string Speed(long b) => b > 0 ? $"{Bytes(b)}/s" : "0 B/s";

    public static string Eta(TimeSpan? t)
    {
        if (t is not { } v || v <= TimeSpan.Zero || v.TotalDays > 30) return "—";
        if (v.TotalMinutes < 1) return $"{v.Seconds}s";
        if (v.TotalHours < 1) return $"{(int)v.TotalMinutes} min";
        return $"{(int)v.TotalHours}h {v.Minutes:00}min";
    }

    public static string Playtime(long sec)
    {
        if (sec <= 0) return "0 min";
        var m = sec / 60;
        if (m < 60) return $"{m} min";
        var h = m / 60.0;
        return h < 10 ? $"{h.ToString("0.0", Br)} h" : $"{Math.Round(h)} h";
    }

    public static string Number(long n) => n.ToString("N0", Br);

    /// <summary>Turns Steam's HTML description into readable plain text.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Regex.Replace(html, @"<(br|BR)\s*/?>", "\n");
        s = Regex.Replace(s, @"</(p|h1|h2|h3|li|ul|div)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "•  ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n");
        return s.Trim();
    }
}

/// <summary>
/// Repack titles carry versions, editions and group tags ("Game [v1.06] (2019) RePack by X").
/// Clean keeps only the game name so entries from different sources group together.
/// </summary>
public static class Titles
{
    // release tags that never belong to the game name: versions, "Update 3", "Hotfix", "Crackfix", "MULTi12", "READNFO", "Free Download"…
    static readonly Regex Cut = new(@"\s*(?:\[|\(|\||/|,|\s[-–—]\s*v\.?\s?\d|\sv\.?\s?\d+(?:\.\d+)|\sv\d{4,}\b|\sbuild\s?\d|\s\+\s|\srepack\b|\sby\s|\supdate\b|\spatch\b|\shotfix\b|\scrack\s?fix\b|\scrack\b|\sfix\s*$|\smulti\d|\sreadnfo\b|\sfree download\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Sequel = new(@"^(\d|i{1,3}\b|iv\b|v\b|vi{1,3}\b|ix\b|x\b|remake|remaster)", RegexOptions.Compiled);
    // scene release name: dotted words ending in a "-GROUP" tag with at least two capitals ("No.Mans.Sky.v6.05.UPDATE-KaOs", "Deathloop-EMPRESS")
    static readonly Regex SceneGroup = new(@"-(?=[A-Za-z0-9_]*[A-Z][A-Za-z0-9_]*[A-Z])[A-Za-z0-9_]{2,}$", RegexOptions.Compiled);

    /// <summary>
    /// Scene-style names ("Game.Name.v1.2.UPDATE-KaOs") use dots instead of spaces and end with the group tag.
    /// Turns the first token of such a title into plain words and drops the group; other titles come back unchanged.
    /// </summary>
    public static string SceneToSpaces(string title, out bool scene)
    {
        scene = false;
        var t = (title ?? "").Trim();
        var sp = t.IndexOf(' ');
        var first = sp < 0 ? t : t[..sp];
        var rest = sp < 0 ? "" : t[sp..];
        var dots = first.Count(c => c == '.');
        var grouped = SceneGroup.IsMatch(first);
        if (!(dots >= 2 && (grouped || rest.Length == 0)) && !(grouped && rest.Length == 0)) return t;
        scene = true;
        first = SceneGroup.Replace(first, "");
        // dots between digits are version separators ("v6.05"), every other dot is a space
        return Regex.Replace(first, @"(?<!\d)\.|\.(?!\d)", " ").Trim() + rest;
    }

    public static string Clean(string raw)
    {
        var t = Regex.Replace(SceneToSpaces(raw ?? "", out _), "[™®©]", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        var m = Cut.Match(t);
        if (m.Success && m.Index > 0) t = t[..m.Index];
        return t.TrimEnd(' ', ':', '-', '–', '—').Trim();
    }

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’' or '`' or '™' or '®' or '©') continue;
            if (c == '&') { sb.Append(" and "); continue; }
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>a = wanted game, b = candidate. Accepts editions of the same game, rejects sequels.</summary>
    public static bool Match(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shortS, longS) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shortS.Length < 4 || !longS.StartsWith(shortS + " ")) return false;
        return !Sequel.IsMatch(longS[(shortS.Length + 1)..]);
    }
}
