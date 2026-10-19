using System.Text.RegularExpressions;

namespace Venix.Core;

/// <summary>What a download list entry actually is: the full game, an update, a patch/hotfix, or a fix.</summary>
public enum EntryKind { Game = 0, Update = 1, Patch = 2, Fix = 3 }

/// <summary>
/// Classifies a list title into <see cref="EntryKind"/> by reading the title alone.
/// Calibrated on real lists (FitGirl, DODI, xatab, GOG, KaOs, EMPRESS, SteamRIP…):
/// <list type="bullet">
/// <item>Scene names ("Game.v1.2.UPDATE-KaOs", "Game.Crackfix-EMPRESS") name the release itself, so an UPDATE token
/// means an update, Patch/Hotfix means a patch and Fix/Crackfix means a fix — unless REPACK is present, which is
/// always the full game.</item>
/// <item>In spaced titles, version info in (…), […] or after " + " belongs to a full repack
/// ("Palworld (v0.1.3.0 Hotfix + …)", "Shock Tactics + HotFix"), and a bare "Update 3" / "v1.0 hotfix" /
/// "v1.11-fix" after the name is just the version the full game ships at. Only explicit standalone releases
/// ("Patch from v1.21 to v1.22", "patches only", "update only") count as patch/update, and only a fix that is the
/// subject of the release ("Game Animation Fix", "Game CrackFix V2") with no game version before it counts as a fix.</item>
/// <item>Editions, bundles and "+ DLCs" stay <see cref="EntryKind.Game"/>.</item>
/// </list>
/// </summary>
public static class EntryClassifier
{
    const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // everything from the first "(" or "[" on, and everything after " + ", is version/bonus info of a full release
    static readonly Regex Extras = new(@"\s*[\(\[].*$|\s\+\s.*$", O);
    static readonly Regex Repack = new(@"\brepack\b", O);
    static readonly Regex SceneFix = new(@"(?<=\S\s)\b(?:crack\s?fix|crackfix|fix|crack\s+enhancement)\b", O);
    static readonly Regex ScenePatch = new(@"(?<=\S\s)\b(?:patch|hotfix)\b", O);
    static readonly Regex SceneUpdate = new(@"(?<=\S\s)\bupdate\b", O);
    static readonly Regex StandalonePatch = new(@"\bpatch(?:es)?\s+from\s+v?\d|\bpatch(?:es)?\s+only\b|\bonly\s+patch(?:es)?\b|\bhotfix\s+only\b", O);
    static readonly Regex StandaloneUpdate = new(@"\bupdate\s+only\b|\bonly\s+update\b|\bupdate\s+from\s+v?\d", O);
    static readonly Regex GameVersion = new(@"\b(?:v\.?\s?\d+(?:\.\d+)+|build\s?\d{4,})", O);
    static readonly Regex SpacedFix = new(@"(?<=\S\s)\b(?:crack\s?fix|crackfix)\b|(?<=\S\s)\bfix(?:es)?\b(?:\s+v\d+)?\s*$", O);

    public static EntryKind Classify(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return EntryKind.Game;
        var t = Titles.SceneToSpaces(title, out var scene);
        var core = Extras.Replace(t, "").Trim();
        if (core.Length == 0) return EntryKind.Game;

        if (scene)
        {
            if (Repack.IsMatch(core)) return EntryKind.Game;
            if (SceneFix.IsMatch(core)) return EntryKind.Fix;
            if (ScenePatch.IsMatch(core)) return EntryKind.Patch;
            if (SceneUpdate.IsMatch(core)) return EntryKind.Update;
            return EntryKind.Game;
        }

        if (StandalonePatch.IsMatch(core)) return EntryKind.Patch;
        if (StandaloneUpdate.IsMatch(core)) return EntryKind.Update;
        var fix = SpacedFix.Match(core);
        if (fix.Success && !GameVersion.IsMatch(core[..fix.Index])) return EntryKind.Fix;
        return EntryKind.Game;
    }

    /// <summary>Singular label: "Jogo", "Atualização", "Patch", "Fix".</summary>
    public static string Label(EntryKind k) => k switch { EntryKind.Update => "Atualização", EntryKind.Patch => "Patch", EntryKind.Fix => "Fix", _ => "Jogo" };

    /// <summary>Section header used when entries are grouped by kind: "Jogo", "Atualizações", "Patches", "Fixes".</summary>
    public static string Section(EntryKind k) => k switch { EntryKind.Update => "Atualizações", EntryKind.Patch => "Patches", EntryKind.Fix => "Fixes", _ => "Jogo" };

    /// <summary>"3 atualizações", "1 patch", "2 fixes"…</summary>
    public static string Count(EntryKind k, int n) => k switch
    {
        EntryKind.Update => $"{n} atualizaç{(n == 1 ? "ão" : "ões")}",
        EntryKind.Patch => $"{n} patch{(n == 1 ? "" : "es")}",
        EntryKind.Fix => $"{n} fix{(n == 1 ? "" : "es")}",
        _ => $"{n} opç{(n == 1 ? "ão" : "ões")} de jogo"
    };
}
