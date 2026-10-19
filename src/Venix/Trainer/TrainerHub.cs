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

    public static void Close(TrainerSession s)
    {
        if (s.TablePath != null) Sessions.Remove(s.TablePath);
        s.Dispose();
    }

    public static void ShutdownAll()
    {
        foreach (var s in Sessions.Values.ToList())
            try { s.Dispose(); } catch { }
        Sessions.Clear();
    }
}
