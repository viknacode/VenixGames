using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Venix.Core;

namespace Venix.Trainer;

/// <summary>
/// One cheat discovered with the scanner: where the value lives and how to find it again after the game restarts.
/// Resolution order on attach: pointer chains (first one that resolves), then module+offset, then AOB signature,
/// then the last raw address (only valid within the same game run).
/// </summary>
public class SavedCheat
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>Table variable type: "4 Bytes", "Float", "Byte", "2 Bytes", "8 Bytes", "Double", "String".</summary>
    public string Type { get; set; } = "4 Bytes";
    /// <summary>Module the static address or pointer chains start in (e.g. "Game.exe").</summary>
    public string? Module { get; set; }
    /// <summary>Static offset inside <see cref="Module"/> when the value itself sits in the module image.</summary>
    public long? ModuleOffset { get; set; }
    /// <summary>
    /// Pointer chains, best first: [moduleOffset, off1, off2, ...]: read pointer at Module+moduleOffset, add off1, read, add off2...
    /// The last offset is added without a read (it points at the value).
    /// </summary>
    public List<List<long>> PointerChains { get; set; } = new();
    /// <summary>Byte signature inside <see cref="Module"/> and the offset from the match to the value (optional).</summary>
    public string? Aob { get; set; }
    public long AobOffset { get; set; }
    /// <summary>Last raw address seen (hex). Only meaningful while the same game run is alive.</summary>
    public string? LastAddress { get; set; }
    /// <summary>Value the user wants written when the cheat is activated (frozen). Null = freeze whatever is there.</summary>
    public string? Value { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public bool IsPersistent => PointerChains.Count > 0 || ModuleOffset != null || Aob != null;
    [JsonIgnore] public string HowLabel => PointerChains.Count > 0 ? $"ponteiro ({PointerChains[0].Count - 1} nível{(PointerChains[0].Count - 1 == 1 ? "" : "is")})"
        : ModuleOffset != null ? "endereço estático" : Aob != null ? "assinatura" : "endereço desta execução";

    /// <summary>Address expression + offsets in the table format used by <see cref="TrainerSession.Resolve"/>.</summary>
    public CtEntry ToEntry(int id, string symbolName)
    {
        // The session registers symbolName → resolved address at attach time (pointer chains / AOB are
        // re-validated there), so the entry itself stays a plain symbol with no offsets.
        return new CtEntry
        {
            Id = id,
            Description = Name,
            VariableType = Type,
            Address = symbolName,
            Length = Type.Equals("String", StringComparison.OrdinalIgnoreCase) ? 32 : 0
        };
    }

    /// <summary>Finds the value in a live process using the saved pointer chains / static offset / signature.</summary>
    public long? Resolve(GameProcess game, out string how)
    {
        how = "";
        var mod = Module != null ? game.FindModule(Module) : null;
        if (mod != null)
        {
            foreach (var chain in PointerChains)
            {
                var a = ResolveChain(game, mod, chain);
                if (a != null) { how = "ponteiro"; return a; }
            }
            if (ModuleOffset is { } off) { how = "estático"; return mod.Base + off; }
            if (!string.IsNullOrWhiteSpace(Aob))
            {
                try
                {
                    var m = AobPattern.Parse(Aob).ScanModule(game, mod, 1);
                    if (m.Count > 0) { how = "assinatura"; return m[0] + AobOffset; }
                }
                catch { }
            }
        }
        if (LastAddress != null && long.TryParse(LastAddress, System.Globalization.NumberStyles.HexNumber, null, out var raw) && game.TryRead((nint)raw, 1) != null)
        {
            how = "endereço desta execução";
            return raw;
        }
        return null;
    }

    static long? ResolveChain(GameProcess game, GameModule mod, List<long> chain)
    {
        if (chain.Count == 0) return null;
        long addr = mod.Base + chain[0];
        for (int i = 1; i < chain.Count; i++)
        {
            if (!game.TryReadPointer((nint)addr, out var p) || p == 0) return null;
            addr = p + chain[i];
        }
        // the final address must be readable to count as resolved
        return game.TryRead((nint)addr, 1) != null ? addr : null;
    }

    public string ChainLabel(List<long> chain) => chain.Count == 0 ? "" :
        $"[{Module}+{chain[0]:X}]" + string.Concat(chain.Skip(1).Select((o, i) => i == chain.Count - 2 ? $"+{o:X}" : $"→[+{o:X}]"));
}

/// <summary>Cheats saved for one library game (%AppData%\Venix\cheats\profiles\&lt;gameId&gt;.json).</summary>
public class CheatProfile
{
    public string GameId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Executable file name (e.g. "Game.exe") used to auto-attach.</summary>
    public string? Exe { get; set; }
    public long ExeSize { get; set; }
    public string? ExeVersion { get; set; }
    public bool Is64Bit { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SavedCheat> Cheats { get; set; } = new();

    [JsonIgnore] public string FilePath => CheatProfiles.PathFor(GameId);

    /// <summary>Human text when the saved executable differs from the one running now (updated game → offsets may be stale).</summary>
    public string? VersionWarning(GameProcess game)
    {
        try
        {
            var path = game.Process.MainModule?.FileName;
            if (path == null || ExeSize == 0) return null;
            var fi = new FileInfo(path);
            var ver = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (fi.Length == ExeSize && (ver ?? "") == (ExeVersion ?? "")) return null;
            return "O executável do jogo mudou desde que os cheats foram salvos (jogo atualizado?). Cheats por ponteiro costumam continuar funcionando; os estáticos podem precisar de nova busca.";
        }
        catch { return null; }
    }

    public void StampExe(GameProcess game)
    {
        try
        {
            var path = game.Process.MainModule?.FileName;
            if (path == null) return;
            Exe = Path.GetFileName(path);
            ExeSize = new FileInfo(path).Length;
            ExeVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
            Is64Bit = game.Is64Bit;
        }
        catch { }
    }
}

/// <summary>Profile files on disk.</summary>
public static class CheatProfiles
{
    public static readonly string Dir = Path.Combine(AppStore.DataDir, "cheats", "profiles");

    public static string PathFor(string gameId) => Path.Combine(Dir, Sanitize(gameId) + ".json");

    static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id;
    }

    public static CheatProfile? Load(string gameId)
    {
        var file = PathFor(gameId);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<CheatProfile>(File.ReadAllText(file), AppStore.Json); }
        catch { return null; }
    }

    public static CheatProfile LoadOrCreate(string gameId, string title, string? exe)
    {
        var p = Load(gameId) ?? new CheatProfile { GameId = gameId, Title = title };
        if (string.IsNullOrEmpty(p.Title)) p.Title = title;
        if (exe != null && string.IsNullOrEmpty(p.Exe)) p.Exe = Path.GetFileName(exe);
        return p;
    }

    public static void Save(CheatProfile p)
    {
        Directory.CreateDirectory(Dir);
        p.UpdatedAt = DateTime.UtcNow;
        var file = PathFor(p.GameId);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p, AppStore.Json));
        File.Move(tmp, file, true);
    }

    public static void Delete(string gameId)
    {
        var file = PathFor(gameId);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Number of saved cheats for a game (0 when there is no profile).</summary>
    public static int Count(string gameId) => Load(gameId)?.Cheats.Count ?? 0;

    public static IEnumerable<CheatProfile> All()
    {
        if (!Directory.Exists(Dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            CheatProfile? p = null;
            try { p = JsonSerializer.Deserialize<CheatProfile>(File.ReadAllText(f), AppStore.Json); } catch { }
            if (p != null) yield return p;
        }
    }
}
