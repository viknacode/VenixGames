using System.IO;

namespace Venix.Trainer;

/// <summary>Keeps trainer sessions alive while the user navigates; disables everything on exit.</summary>
public static class TrainerHub
{
    static readonly Dictionary<string, TrainerSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<TrainerSession> All => Sessions.Values;

    /// <summary>Gets the running session for a table file or starts a new one.</summary>
    public static TrainerSession Open(string tableFile, string name, string? processHint = null)
    {
        if (Sessions.TryGetValue(tableFile, out var existing))
        {
            if (processHint != null) existing.ProcessHint = processHint;
            return existing;
        }
        var table = CtTable.Parse(File.ReadAllText(tableFile));
        var session = new TrainerSession(name, tableFile, table, processHint);
        Sessions[tableFile] = session;
        return session;
    }

    /// <summary>Session hosting the cheats the user discovered with the scanner for a game (one per game id).</summary>
    public static TrainerSession OpenProfile(string gameId, string title, string? exe)
    {
        var key = "profile:" + gameId;
        if (Sessions.TryGetValue(key, out var existing))
        {
            if (exe != null && existing.ProcessHint == null) existing.ProcessHint = Path.GetFileName(exe);
            return existing;
        }
        var profile = CheatProfiles.LoadOrCreate(gameId, title, exe);
        var session = new TrainerSession(title, null, new CtTable(), exe != null ? Path.GetFileName(exe) : profile.Exe, profile);
        Sessions[key] = session;
        return session;
    }

    public static TrainerSession? FindProfile(string gameId) => Sessions.GetValueOrDefault("profile:" + gameId);

    public static void Close(TrainerSession s)
    {
        var key = s.TablePath ?? (s.Profile != null ? "profile:" + s.Profile.GameId : null);
        if (key != null) Sessions.Remove(key);
        s.Dispose();
    }

    public static void ShutdownAll()
    {
        foreach (var s in Sessions.Values.ToList())
            try { s.Dispose(); } catch { }
        Sessions.Clear();
    }
}
