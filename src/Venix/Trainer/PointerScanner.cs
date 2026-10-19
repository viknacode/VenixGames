using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Venix.Trainer;

/// <summary>
/// Every pointer-sized, aligned value in the game that points into committed memory, sorted by the value it points to.
/// Built once per run; pointer searches query it with binary searches instead of re-reading the game.
/// </summary>
public sealed class PointerMap
{
    /// <summary>Pointer values (sorted ascending).</summary>
    public long[] Values { get; }
    /// <summary>Where each pointer is stored (parallel to <see cref="Values"/>).</summary>
    public long[] At { get; }
    public int Count => Values.Length;
    public IReadOnlyList<GameModule> Modules { get; }
    public DateTime BuiltAt { get; } = DateTime.UtcNow;

    PointerMap(long[] values, long[] at, IReadOnlyList<GameModule> modules)
    {
        Values = values;
        At = at;
        Modules = modules;
    }

    public GameModule? ModuleOf(long address)
    {
        foreach (var m in Modules)
            if (address >= m.Base && address < m.Base + m.Size) return m;
        return null;
    }

    /// <summary>Index range [lo, hi) of pointers whose value is within [from, to].</summary>
    public (int lo, int hi) Range(long from, long to)
    {
        int lo = LowerBound(from), hi = LowerBound(to + 1);
        return (lo, hi);
    }

    int LowerBound(long v)
    {
        int lo = 0, hi = Values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Values[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Reads the whole process once and collects every plausible pointer (aligned to the pointer size).</summary>
    public static PointerMap Build(MemoryScanner scanner, IProgress<ScanProgress>? progress, CancellationToken ct, int maxEntries = 60_000_000)
    {
        var game = scanner.Game;
        game.RefreshModules();
        int psize = game.PointerSize;
        // candidate targets: anything committed and readable (the pointer map must include static module data too)
        var regions = scanner.Regions(writableOnly: false);
        var starts = regions.Select(r => r.Start).ToArray();
        var ends = regions.Select(r => r.End).ToArray();
        long minAddr = starts.Length > 0 ? starts[0] : 0, maxAddr = ends.Length > 0 ? ends[^1] : 0;
        bool PointsSomewhere(long v)
        {
            if (v < minAddr || v >= maxAddr || (v & 3) != 0) return false;
            int lo = 0, hi = starts.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (ends[mid] <= v) lo = mid + 1; else hi = mid;
            }
            return lo < starts.Length && starts[lo] <= v;
        }

        // pointers are stored in writable memory or in module images (static pointers); mapped files rarely hold any
        var sources = regions.Where(r => r.Writable || r.Image).ToList();
        long total = sources.Sum(r => r.Size), done = 0, count = 0;
        var parts = new ConcurrentBag<(long[] v, long[] a)>();
        const int Chunk = 4 << 20;
        try
        {
            Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
            {
                var r = sources[i];
                var buf = new byte[Chunk];
                var vs = new List<long>();
                var at = new List<long>();
                for (long off = 0; off < r.Size; off += Chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(Chunk, r.Size - off);
                    if (!game.TryRead((nint)(r.Start + off), buf, n))
                    {
                        // unreadable pages inside: fall back to page reads
                        game.ReadLenient((nint)(r.Start + off), buf, n);
                    }
                    var span = buf.AsSpan(0, n);
                    if (psize == 8)
                    {
                        var longs = MemoryMarshal.Cast<byte, long>(span.Slice(0, n & ~7));
                        for (int k = 0; k < longs.Length; k++)
                            if (longs[k] != 0 && PointsSomewhere(longs[k])) { vs.Add(longs[k]); at.Add(r.Start + off + k * 8L); }
                    }
                    else
                    {
                        var ints = MemoryMarshal.Cast<byte, uint>(span.Slice(0, n & ~3));
                        for (int k = 0; k < ints.Length; k++)
                            if (ints[k] != 0 && PointsSomewhere(ints[k])) { vs.Add(ints[k]); at.Add(r.Start + off + k * 4L); }
                    }
                }
                if (vs.Count > 0)
                {
                    parts.Add((vs.ToArray(), at.ToArray()));
                    if (Interlocked.Add(ref count, vs.Count) > maxEntries)
                        throw new ScanLimitException("O jogo tem ponteiros demais para mapear na memória disponível.");
                }
                var d = Interlocked.Add(ref done, r.Size);
                progress?.Report(new ScanProgress(d, total, Interlocked.Read(ref count)));
            });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions.FirstOrDefault(e => e is ScanLimitException) ?? ex.InnerExceptions[0]).Throw();
        }
        int totalCount = (int)Math.Min(int.MaxValue, parts.Sum(p => (long)p.v.Length));
        var values = new long[totalCount];
        var where = new long[totalCount];
        int pos = 0;
        foreach (var (v, a) in parts)
        {
            Array.Copy(v, 0, values, pos, v.Length);
            Array.Copy(a, 0, where, pos, a.Length);
            pos += v.Length;
        }
        Array.Sort(values, where);
        return new PointerMap(values, where, game.Modules.ToList());
    }
}

/// <summary>A pointer path from a module to a value: [moduleOffset, off1, ..., offN] (see <see cref="SavedCheat.PointerChains"/>).</summary>
public sealed record PointerChain(string Module, List<long> Offsets)
{
    public int Levels => Offsets.Count - 1;
    public string Label => $"[{Module}+{Offsets[0]:X}]" + string.Concat(Offsets.Skip(1).Select((o, i) => i == Offsets.Count - 2 ? $"+{o:X}" : $"→[+{o:X}]"));
}

/// <summary>
/// Finds static pointer chains (module base + offsets, up to 4 levels, offsets up to 4096) to a confirmed address
/// by walking the <see cref="PointerMap"/> backwards from the target.
/// </summary>
public sealed class PointerScanner
{
    readonly GameProcess _game;
    readonly PointerMap _map;

    public int MaxLevels { get; set; } = 4;
    public long MaxOffset { get; set; } = 4096;
    public int MaxResults { get; set; } = 40;
    /// <summary>Pointers examined per node; keeps the search bounded in games with huge object graphs.</summary>
    public int MaxBranch { get; set; } = 800;
    public long MaxVisits { get; set; } = 3_000_000;

    public PointerScanner(GameProcess game, PointerMap map)
    {
        _game = game;
        _map = map;
    }

    public List<PointerChain> Find(long target, CancellationToken ct)
    {
        var found = new List<PointerChain>();
        long visits = 0;
        // the value itself inside a module: a 0-level "chain" is just module+offset (handled by the caller as static)
        var path = new List<long>();
        Walk(target, 0, path, found, ref visits, ct);
        // shortest chains first, then smaller final offsets; drop chains that do not resolve to the target right now
        return found
            .Where(c => Resolve(c) == target)
            .OrderBy(c => c.Levels).ThenBy(c => c.Offsets.Skip(1).Sum(Math.Abs))
            .DistinctBy(c => c.Label)
            .Take(MaxResults)
            .ToList();
    }

    void Walk(long addr, int level, List<long> path, List<PointerChain> found, ref long visits, CancellationToken ct)
    {
        if (level >= MaxLevels || found.Count >= MaxResults * 4 || visits > MaxVisits) return;
        ct.ThrowIfCancellationRequested();
        var (lo, hi) = _map.Range(addr - MaxOffset, addr);
        int taken = 0;
        // closest pointers (smallest offset) first: a struct base is usually just below the field
        for (int i = hi - 1; i >= lo && taken < MaxBranch; i--, taken++)
        {
            visits++;
            long ptrValue = _map.Values[i];
            long at = _map.At[i];
            long offset = addr - ptrValue;
            var mod = _map.ModuleOf(at);
            if (mod != null)
            {
                // forward order: [static, offset found at this (deepest) level, ..., offset nearest the value]
                var offsets = new List<long> { at - mod.Base, offset };
                for (int k = path.Count - 1; k >= 0; k--) offsets.Add(path[k]);
                found.Add(new PointerChain(mod.Name, offsets));
                if (found.Count >= MaxResults * 4) return;
                continue;
            }
            if (level + 1 < MaxLevels)
            {
                path.Add(offset);
                Walk(at, level + 1, path, found, ref visits, ct);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>Follows a chain in the live process; null when a read fails.</summary>
    public long? Resolve(PointerChain c)
    {
        var mod = _game.FindModule(c.Module);
        if (mod == null) return null;
        long addr = mod.Base + c.Offsets[0];
        for (int i = 1; i < c.Offsets.Count; i++)
        {
            if (!_game.TryReadPointer((nint)addr, out var p) || p == 0) return null;
            addr = p + c.Offsets[i];
        }
        return addr;
    }
}
