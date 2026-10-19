using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Venix.Trainer;

public enum ScanType { Byte, Int16, Int32, Int64, Float, Double, String }

public enum ScanMode
{
    Exact, Unknown, Increased, Decreased, Changed, Unchanged, IncreasedBy, DecreasedBy, Between
}

/// <summary>Parameters of one scan step.</summary>
public sealed class ScanOptions
{
    public ScanType Type { get; set; } = ScanType.Int32;
    public ScanMode Mode { get; set; } = ScanMode.Exact;
    /// <summary>Primary value (Exact, IncreasedBy, DecreasedBy, Between: lower bound).</summary>
    public double Value { get; set; }
    /// <summary>Upper bound of Between.</summary>
    public double Value2 { get; set; }
    /// <summary>Text to find (String type).</summary>
    public string Text { get; set; } = "";
    public bool Unicode { get; set; }
    /// <summary>Only addresses aligned to the value size (4 for 4/8-byte values): much faster, finds nearly everything.</summary>
    public bool Aligned { get; set; } = true;
    /// <summary>Only committed pages that are writable (RW / RWX). Read-only pages rarely hold game state.</summary>
    public bool WritableOnly { get; set; } = true;
    /// <summary>Tolerance used by Exact / IncreasedBy / DecreasedBy on Float/Double; null = ±0.5.</summary>
    public double? FloatTolerance { get; set; }
    /// <summary>Abort with an error when the result list would exceed this many addresses.</summary>
    public int MaxResults { get; set; } = 20_000_000;
    /// <summary>Abort an "unknown" scan when the committed memory to snapshot exceeds this.</summary>
    public long MaxSnapshotBytes { get; set; } = 3L << 30;

    public static int SizeOf(ScanType t) => t switch
    {
        ScanType.Byte => 1, ScanType.Int16 => 2, ScanType.Int32 or ScanType.Float => 4,
        ScanType.Int64 or ScanType.Double => 8, _ => 1
    };

    public int ValueSize => Type == ScanType.String ? Math.Max(1, Text.Length) * (Unicode ? 2 : 1) : SizeOf(Type);
    public int Step => Type == ScanType.String ? 1 : Aligned ? Math.Min(4, SizeOf(Type)) : 1;

    public ScanOptions Clone() => (ScanOptions)MemberwiseClone();

    public static string TypeLabel(ScanType t) => t switch
    {
        ScanType.Byte => "1 byte", ScanType.Int16 => "2 bytes", ScanType.Int32 => "4 bytes", ScanType.Int64 => "8 bytes",
        ScanType.Float => "Float", ScanType.Double => "Double", _ => "Texto"
    };

    public static string ModeLabel(ScanMode m) => m switch
    {
        ScanMode.Exact => "Valor exato", ScanMode.Unknown => "Valor desconhecido", ScanMode.Increased => "Aumentou",
        ScanMode.Decreased => "Diminuiu", ScanMode.Changed => "Mudou", ScanMode.Unchanged => "Não mudou",
        ScanMode.IncreasedBy => "Aumentou em", ScanMode.DecreasedBy => "Diminuiu em", _ => "Entre"
    };

    public static bool NeedsPrevious(ScanMode m) => m is ScanMode.Increased or ScanMode.Decreased or ScanMode.Changed or ScanMode.Unchanged or ScanMode.IncreasedBy or ScanMode.DecreasedBy;
    public static bool NeedsValue(ScanMode m) => m is ScanMode.Exact or ScanMode.IncreasedBy or ScanMode.DecreasedBy or ScanMode.Between;

    /// <summary>Tolerance for Float/Double matches: half of the last typed decimal ("100" → ±0.5, "2.5" → ±0.05).</summary>
    public static double ToleranceFor(string typed)
    {
        typed = typed.Trim().Replace(',', '.');
        var dot = typed.IndexOf('.');
        int decimals = dot < 0 ? 0 : typed.Length - dot - 1;
        return 0.5 * Math.Pow(10, -decimals);
    }
}

/// <summary>Live (committed, readable) region of the target, as enumerated for a scan.</summary>
public readonly record struct ScanRegion(long Start, long Size, bool Writable, bool Image, bool Private)
{
    public long End => Start + Size;
}

/// <summary>
/// Compact result list: parallel arrays of addresses and the value bytes seen at the last scan, no object per hit.
/// 10 million 4-byte hits cost 120 MB instead of gigabytes of objects.
/// </summary>
public sealed class ScanResults
{
    public ScanType Type { get; }
    public int ValueSize { get; }
    public long[] Addresses { get; }
    public byte[] Values { get; }
    public int Count { get; }

    public ScanResults(ScanType type, int valueSize, long[] addresses, byte[] values, int count)
    {
        Type = type;
        ValueSize = valueSize;
        Addresses = addresses;
        Values = values;
        Count = count;
    }

    public static ScanResults Empty(ScanType type, int valueSize) => new(type, valueSize, Array.Empty<long>(), Array.Empty<byte>(), 0);

    public ReadOnlySpan<byte> ValueAt(int i) => Values.AsSpan(i * ValueSize, ValueSize);

    public string Format(int i) => MemoryScanner.FormatValue(Type, ValueAt(i));

    public int IndexOf(long address)
    {
        int i = LowerBound(address);
        return i < Count && Addresses[i] == address ? i : -1;
    }

    /// <summary>Index range [lo, hi) of addresses within [start, end).</summary>
    public (int lo, int hi) Range(long start, long end) => (LowerBound(start), LowerBound(end));

    int LowerBound(long value)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Addresses[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Keeps only the given indices (ascending), e.g. after a user filter.</summary>
    public ScanResults Subset(IReadOnlyList<int> indices)
    {
        var a = new long[indices.Count];
        var v = new byte[indices.Count * ValueSize];
        for (int k = 0; k < indices.Count; k++)
        {
            a[k] = Addresses[indices[k]];
            ValueAt(indices[k]).CopyTo(v.AsSpan(k * ValueSize));
        }
        return new ScanResults(Type, ValueSize, a, v, indices.Count);
    }
}

/// <summary>Raw copy of every scanned region (an "unknown initial value" scan keeps this instead of a list of every address).</summary>
public sealed class MemorySnapshot
{
    public sealed class Block
    {
        public long Start;
        public byte[] Data = Array.Empty<byte>();
        /// <summary>Per 4 KiB page: true when the page could be read; unreadable pages hold zeros and are skipped.</summary>
        public bool[] PageOk = Array.Empty<bool>();
        public bool Writable;
        public bool Image;
        public bool Private;
        public long End => Start + Data.Length;
    }

    public List<Block> Blocks { get; } = new();
    public long TotalBytes => Blocks.Sum(b => (long)b.Data.Length);
    public DateTime TakenAt { get; init; } = DateTime.UtcNow;

    /// <summary>Approximate candidate count for a given step (what the UI shows after an unknown scan).</summary>
    public long Candidates(int step) => Blocks.Sum(b => (long)b.Data.Length / step);

    public Block? BlockAt(long address)
    {
        int lo = 0, hi = Blocks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Blocks[mid].End <= address) lo = mid + 1; else hi = mid;
        }
        return lo < Blocks.Count && Blocks[lo].Start <= address ? Blocks[lo] : null;
    }

    public bool TryGet(long address, int size, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        var b = BlockAt(address);
        if (b == null || address + size > b.End) return false;
        int off = (int)(address - b.Start);
        if (!b.PageOk[off >> 12] || !b.PageOk[(off + size - 1) >> 12]) return false;
        bytes = b.Data.AsSpan(off, size);
        return true;
    }
}

/// <summary>Progress of a scan: bytes (or candidates) done / total, and the hits so far.</summary>
public readonly record struct ScanProgress(long Done, long Total, long Hits)
{
    public double Percent => Total == 0 ? 0 : Math.Min(100, 100.0 * Done / Total);
}

public sealed class ScanLimitException(string message) : Exception(message);

/// <summary>
/// Venix memory scanner: enumerates the target's committed pages, reads them in large blocks in parallel and
/// filters values by type and comparison mode. First scans produce a <see cref="ScanResults"/> (or a
/// <see cref="MemorySnapshot"/> for "unknown initial value"); next scans refine the previous result.
/// </summary>
public sealed class MemoryScanner
{
    const int ChunkSize = 4 << 20; // 4 MiB reads
    const int PageSize = 0x1000;

    readonly GameProcess _game;

    public MemoryScanner(GameProcess game) => _game = game;

    public GameProcess Game => _game;

    /* ---------------------------------------------------------- regions */

    /// <summary>Committed, readable, non-guard regions (optionally writable only), split so every item is at most 256 MiB.</summary>
    public List<ScanRegion> Regions(bool writableOnly)
    {
        var list = new List<ScanRegion>();
        nint address = 0;
        int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
        long limit = _game.Is64Bit ? 0x7FFFFFFEFFFF : 0xFFFFFFFF;
        while ((long)address < limit && Native.VirtualQueryEx(_game.Handle, address, out var mbi, mbiSize) != 0)
        {
            var next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= address) break;
            var cur = mbi;
            address = next;
            if (cur.State != Native.MEM_COMMIT) continue;
            uint p = cur.Protect & 0xFF;
            if ((cur.Protect & Native.PAGE_GUARD) != 0 || p == Native.PAGE_NOACCESS || p == 0) continue;
            bool writable = p is Native.PAGE_READWRITE or Native.PAGE_WRITECOPY or Native.PAGE_EXECUTE_READWRITE or Native.PAGE_EXECUTE_WRITECOPY;
            if (writableOnly && !writable) continue;
            long start = (long)cur.BaseAddress, size = (long)cur.RegionSize;
            const long MaxPiece = 256L << 20;
            for (long off = 0; off < size; off += MaxPiece)
                list.Add(new ScanRegion(start + off, Math.Min(MaxPiece, size - off), writable, cur.Type == Native.MEM_IMAGE, cur.Type == Native.MEM_PRIVATE));
        }
        return list;
    }

    /* ---------------------------------------------------------- reading */

    /// <summary>Reads [address, address+size) into <paramref name="buffer"/>; unreadable pages are zeroed and flagged false in <paramref name="pageOk"/>.</summary>
    bool ReadBlock(long address, byte[] buffer, int size, bool[] pageOk)
    {
        int pages = (size + PageSize - 1) / PageSize;
        if (Native.ReadProcessMemory(_game.Handle, (nint)address, buffer, size, out var read) && read == size)
        {
            Array.Fill(pageOk, true, 0, pages);
            return true;
        }
        bool any = false;
        var page = new byte[PageSize];
        for (int i = 0; i < pages; i++)
        {
            int n = Math.Min(PageSize, size - i * PageSize);
            bool ok = Native.ReadProcessMemory(_game.Handle, (nint)(address + i * PageSize), page, n, out var r) && r == n;
            pageOk[i] = ok;
            if (ok) { Array.Copy(page, 0, buffer, i * PageSize, n); any = true; }
            else Array.Clear(buffer, i * PageSize, n);
        }
        return any;
    }

    static void Run(int count, CancellationToken ct, Action<int> body)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, body);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            var first = ex.InnerExceptions.FirstOrDefault(e => e is ScanLimitException) ?? ex.InnerExceptions[0];
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>Copies every region into memory (base of an "unknown initial value" scan and of Smart Discovery).</summary>
    public MemorySnapshot Snapshot(ScanOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var regions = Regions(o.WritableOnly);
        long total = regions.Sum(r => r.Size);
        if (total > o.MaxSnapshotBytes)
            throw new ScanLimitException($"O jogo tem {Core.Format.Bytes(total)} de memória em uso, acima do limite de {Core.Format.Bytes(o.MaxSnapshotBytes)} para uma busca por valor desconhecido. Use \"Valor exato\" ou feche outros programas pesados.");
        var snap = new MemorySnapshot();
        long done = 0;
        var blocks = new MemorySnapshot.Block?[regions.Count];
        Run(regions.Count, ct, i =>
        {
            var r = regions[i];
            var data = new byte[r.Size];
            var ok = new bool[(r.Size + PageSize - 1) / PageSize];
            var chunkOk = new bool[ChunkSize / PageSize];
            bool any = false;
            for (long off = 0; off < r.Size; off += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize, r.Size - off);
                var tmp = new byte[n];
                if (ReadBlock(r.Start + off, tmp, n, chunkOk)) { Array.Copy(tmp, 0, data, off, n); any = true; }
                Array.Copy(chunkOk, 0, ok, off / PageSize, (n + PageSize - 1) / PageSize);
            }
            if (any) blocks[i] = new MemorySnapshot.Block { Start = r.Start, Data = data, PageOk = ok, Writable = r.Writable, Image = r.Image, Private = r.Private };
            var d = Interlocked.Add(ref done, r.Size);
            progress?.Report(new ScanProgress(d, total, 0));
        });
        foreach (var b in blocks) if (b != null) snap.Blocks.Add(b);
        snap.Blocks.Sort((a, b) => a.Start.CompareTo(b.Start));
        return snap;
    }

    /* ---------------------------------------------------------- first scan */

    /// <summary>Scans the whole process for the given value/range (Exact, Between, text). Unknown → <see cref="Snapshot"/>.</summary>
    public ScanResults FirstScan(ScanOptions o, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (o.Mode == ScanMode.Unknown) throw new InvalidOperationException("Use Snapshot() para valor desconhecido.");
        if (ScanOptions.NeedsPrevious(o.Mode)) throw new InvalidOperationException("Esse modo compara com um scan anterior. Comece com \"Valor exato\" ou \"Valor desconhecido\".");
        var regions = Regions(o.WritableOnly);
        long total = regions.Sum(r => r.Size), done = 0, hits = 0;
        var parts = new ConcurrentBag<(long[] a, byte[] v, int n)>();
        int size = o.ValueSize;
        byte[]? needle = o.Type == ScanType.String ? (o.Unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(o.Text) : null;

        Run(regions.Count, ct, i =>
        {
            var r = regions[i];
            var buf = new byte[ChunkSize + size];
            var ok = new bool[(ChunkSize + size + PageSize - 1) / PageSize];
            var hitsA = new List<int>();
            for (long off = 0; off < r.Size; off += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize + size - 1, r.Size - off); // overlap so values straddling chunks are seen once
                if (!ReadBlock(r.Start + off, buf, n, ok)) continue;
                int lastStart = Math.Min(n - size, ChunkSize - 1);
                hitsA.Clear();
                var span = buf.AsSpan(0, n);
                if (needle != null) MatchBytes(span, needle, lastStart, hitsA, ok);
                else Compare(o, span, lastStart, hitsA, ok, null, 0);
                if (hitsA.Count == 0) continue;
                parts.Add(Pack(r.Start + off, span, hitsA, size));
                var h = Interlocked.Add(ref hits, hitsA.Count);
                if (h > o.MaxResults) throw new ScanLimitException($"Mais de {Core.Format.Number(o.MaxResults)} resultados. Refine a busca (valor mais específico ou outro tipo).");
            }
            var d = Interlocked.Add(ref done, r.Size);
            progress?.Report(new ScanProgress(d, total, Interlocked.Read(ref hits)));
        });

        return Merge(o.Type, size, parts);
    }

    static (long[], byte[], int) Pack(long baseAddress, ReadOnlySpan<byte> span, List<int> offsets, int size)
    {
        var addrs = new long[offsets.Count];
        var vals = new byte[offsets.Count * size];
        for (int k = 0; k < offsets.Count; k++)
        {
            addrs[k] = baseAddress + offsets[k];
            span.Slice(offsets[k], size).CopyTo(vals.AsSpan(k * size));
        }
        return (addrs, vals, offsets.Count);
    }

    static ScanResults Merge(ScanType type, int size, IEnumerable<(long[] a, byte[] v, int n)> parts)
    {
        // chunks cover disjoint ranges and hits inside a chunk are ascending, so ordering chunks by first address sorts everything
        var list = parts.Where(p => p.n > 0).OrderBy(p => p.a[0]).ToList();
        long total = list.Sum(p => (long)p.n);
        if (total > int.MaxValue / 2) throw new ScanLimitException("Resultados demais para manter em memória.");
        var addrs = new long[total];
        var vals = new byte[total * size];
        int pos = 0;
        foreach (var (a, v, n) in list)
        {
            Array.Copy(a, 0, addrs, pos, n);
            Array.Copy(v, 0, vals, (long)pos * size, (long)n * size);
            pos += n;
        }
        return new ScanResults(type, size, addrs, vals, (int)total);
    }

    /* ---------------------------------------------------------- next scan */

    /// <summary>Refines a previous result (or snapshot) with a new comparison; values stored become the new "previous".</summary>
    public ScanResults NextScan(ScanOptions o, ScanResults? previous, MemorySnapshot? snapshot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (o.Mode == ScanMode.Unknown) throw new InvalidOperationException("Valor desconhecido só vale para o primeiro scan.");
        if (previous != null) return RefineResults(o, previous, progress, ct);
        if (snapshot != null) return RefineSnapshot(o, snapshot, progress, ct);
        throw new InvalidOperationException("Nenhum scan anterior.");
    }

    ScanResults RefineSnapshot(ScanOptions o, MemorySnapshot snap, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (o.Type == ScanType.String) throw new InvalidOperationException("Texto não pode ser comparado com um scan de valor desconhecido.");
        int size = o.ValueSize;
        long total = snap.TotalBytes, done = 0, hits = 0;
        var parts = new ConcurrentBag<(long[] a, byte[] v, int n)>();
        Run(snap.Blocks.Count, ct, i =>
        {
            var b = snap.Blocks[i];
            var buf = new byte[ChunkSize + size];
            var ok = new bool[(ChunkSize + size + PageSize - 1) / PageSize];
            var hitsA = new List<int>();
            for (long off = 0; off < b.Data.Length; off += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(ChunkSize + size - 1, b.Data.Length - off);
                if (!ReadBlock(b.Start + off, buf, n, ok)) continue;
                // pages that were unreadable in the snapshot cannot be compared
                int pageBase = (int)(off / PageSize);
                for (int p = 0; p < ok.Length; p++) ok[p] &= pageBase + p < b.PageOk.Length && b.PageOk[pageBase + p];
                int lastStart = Math.Min(n - size, ChunkSize - 1);
                hitsA.Clear();
                Compare(o, buf.AsSpan(0, n), lastStart, hitsA, ok, b.Data, (int)off);
                if (hitsA.Count == 0) continue;
                parts.Add(Pack(b.Start + off, buf.AsSpan(0, n), hitsA, size));
                var h = Interlocked.Add(ref hits, hitsA.Count);
                if (h > o.MaxResults) throw new ScanLimitException($"Mais de {Core.Format.Number(o.MaxResults)} resultados ainda. Faça outra ação no jogo e repita o scan antes de listar.");
            }
            var d = Interlocked.Add(ref done, b.Data.Length);
            progress?.Report(new ScanProgress(d, total, Interlocked.Read(ref hits)));
        });
        return Merge(o.Type, size, parts);
    }

    ScanResults RefineResults(ScanOptions o, ScanResults prev, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        int size = o.ValueSize;
        byte[]? needle = o.Type == ScanType.String ? (o.Unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(o.Text) : null;
        if (prev.ValueSize != size && ScanOptions.NeedsPrevious(o.Mode))
            throw new InvalidOperationException("O tipo do valor não pode mudar entre scans. Clique em \"Novo scan\".");
        var regions = Regions(o.WritableOnly);
        long total = prev.Count, done = 0, hits = 0;
        var parts = new ConcurrentBag<(long[] a, byte[] v, int n)>();
        Run(regions.Count, ct, i =>
        {
            var r = regions[i];
            var (lo, hi) = prev.Range(r.Start, r.End);
            if (hi <= lo) return;
            var buf = new byte[ChunkSize];
            var ok = new bool[ChunkSize / PageSize];
            var addrs = new List<long>();
            var vals = new List<byte>();
            int idx = lo;
            while (idx < hi)
            {
                ct.ThrowIfCancellationRequested();
                long chunkStart = prev.Addresses[idx];
                long chunkEnd = Math.Min(r.End, chunkStart + ChunkSize);
                int n = (int)(chunkEnd - chunkStart);
                bool readOk = n > 0 && ReadBlock(chunkStart, buf, n, ok);
                int before = idx;
                while (idx < hi && prev.Addresses[idx] + size <= chunkEnd)
                {
                    long a = prev.Addresses[idx];
                    int off = (int)(a - chunkStart);
                    if (readOk && ok[off >> 12] && ok[(off + size - 1) >> 12])
                    {
                        var cur = buf.AsSpan(off, size);
                        bool match = needle != null ? cur.SequenceEqual(needle) : Matches(o, cur, prev.ValueAt(idx));
                        if (match) { addrs.Add(a); vals.AddRange(cur); }
                    }
                    idx++;
                }
                if (idx == before) idx++; // value straddling the region end: drop it
            }
            if (addrs.Count > 0) parts.Add((addrs.ToArray(), vals.ToArray(), addrs.Count));
            Interlocked.Add(ref hits, addrs.Count);
            var d = Interlocked.Add(ref done, hi - lo);
            progress?.Report(new ScanProgress(d, total, Interlocked.Read(ref hits)));
        });
        return Merge(o.Type, size, parts);
    }

    /// <summary>Current bytes at every address of a result (live value column); null entries could not be read.</summary>
    public byte[]?[] ReadCurrent(ScanResults r, int from, int count)
    {
        var outp = new byte[]?[count];
        for (int i = 0; i < count && from + i < r.Count; i++)
            outp[i] = _game.TryRead((nint)r.Addresses[from + i], r.ValueSize);
        return outp;
    }

    /* ---------------------------------------------------------- comparisons */

    static void MatchBytes(ReadOnlySpan<byte> data, byte[] needle, int lastStart, List<int> hits, bool[] pageOk)
    {
        int pos = 0;
        while (pos <= lastStart)
        {
            int rel = data.Slice(pos, lastStart - pos + 1).IndexOf(needle[0]);
            if (rel < 0) break;
            int start = pos + rel;
            if (start + needle.Length <= data.Length && pageOk[start >> 12] && pageOk[(start + needle.Length - 1) >> 12] && data.Slice(start, needle.Length).SequenceEqual(needle))
                hits.Add(start);
            pos = start + 1;
        }
    }

    /// <summary>Compares every candidate of a block against the options (and the old bytes, when a snapshot is given).</summary>
    static void Compare(ScanOptions o, ReadOnlySpan<byte> cur, int lastStart, List<int> hits, bool[] pageOk, byte[]? old, int oldOffset)
    {
        switch (o.Type)
        {
            case ScanType.Byte: Compare<byte>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
            case ScanType.Int16: Compare<short>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
            case ScanType.Int32: Compare<int>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
            case ScanType.Int64: Compare<long>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
            case ScanType.Float: Compare<float>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
            case ScanType.Double: Compare<double>(o, cur, lastStart, hits, pageOk, old, oldOffset); break;
        }
    }

    static void Compare<T>(ScanOptions o, ReadOnlySpan<byte> cur, int lastStart, List<int> hits, bool[] pageOk, byte[]? old, int oldOffset)
        where T : unmanaged, INumber<T>
    {
        int size = Unsafe.SizeOf<T>();
        int step = o.Step;
        bool isFloat = typeof(T) == typeof(float) || typeof(T) == typeof(double);
        double v1 = o.Value, v2 = o.Value2;
        double tol = isFloat ? o.FloatTolerance ?? 0.5 : 0;
        var mode = o.Mode;
        T tv1 = default, tv2 = default;
        if (!isFloat)
        {
            // integer compare: a non-integral or out-of-range typed value can never match
            if (ScanOptions.NeedsValue(mode) && !TryConvert<T>(v1, out tv1)) return;
            if (mode == ScanMode.Between && !TryConvert<T>(v2, out tv2)) tv2 = T.CreateSaturating(v2);
        }
        for (int off = 0; off <= lastStart; off += step)
        {
            if (!pageOk[off >> 12] || !pageOk[(off + size - 1) >> 12])
            {
                // jump to the start of the next page (every step divides the page size)
                int nextPage = ((off >> 12) + 1) << 12;
                off = nextPage - step;
                continue;
            }
            T x = MemoryMarshal.Read<T>(cur.Slice(off, size));
            if (isFloat && !double.IsFinite(double.CreateTruncating(x))) continue;
            bool ok;
            if (old == null)
            {
                ok = mode switch
                {
                    ScanMode.Exact => isFloat ? Math.Abs(double.CreateTruncating(x) - v1) <= tol : x == tv1,
                    ScanMode.Between => isFloat ? InRange(double.CreateTruncating(x), v1, v2) : x >= tv1 && x <= tv2,
                    _ => false
                };
            }
            else
            {
                T y = MemoryMarshal.Read<T>(old.AsSpan(oldOffset + off, size));
                if (isFloat && !double.IsFinite(double.CreateTruncating(y))) continue;
                ok = Differential(mode, x, y, isFloat, v1, v2, tol, tv1, tv2);
            }
            if (ok) hits.Add(off);
        }
    }

    static bool InRange(double x, double lo, double hi) => lo <= hi ? x >= lo && x <= hi : x >= hi && x <= lo;

    static bool TryConvert<T>(double v, out T t) where T : INumber<T>
    {
        t = default!;
        if (!double.IsFinite(v) || v != Math.Floor(v)) return false;
        try { t = T.CreateChecked(v); return true; } catch (OverflowException) { return false; }
    }

    static bool Differential<T>(ScanMode mode, T x, T y, bool isFloat, double v1, double v2, double tol, T tv1, T tv2) where T : INumber<T>
    {
        switch (mode)
        {
            case ScanMode.Increased: return x > y;
            case ScanMode.Decreased: return x < y;
            case ScanMode.Changed: return x != y;
            case ScanMode.Unchanged: return x == y;
            case ScanMode.IncreasedBy:
                return isFloat ? Math.Abs(double.CreateTruncating(x) - double.CreateTruncating(y) - v1) <= tol : x == y + tv1;
            case ScanMode.DecreasedBy:
                return isFloat ? Math.Abs(double.CreateTruncating(y) - double.CreateTruncating(x) - v1) <= tol : x == y - tv1;
            case ScanMode.Exact:
                return isFloat ? Math.Abs(double.CreateTruncating(x) - v1) <= tol : x == tv1;
            case ScanMode.Between:
                return isFloat ? InRange(double.CreateTruncating(x), v1, v2) : x >= tv1 && x <= tv2;
            default: return false;
        }
    }

    /// <summary>Single-value version used by next scans over a result list.</summary>
    static bool Matches(ScanOptions o, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> old) => o.Type switch
    {
        ScanType.Byte => One<byte>(o, cur, old),
        ScanType.Int16 => One<short>(o, cur, old),
        ScanType.Int32 => One<int>(o, cur, old),
        ScanType.Int64 => One<long>(o, cur, old),
        ScanType.Float => One<float>(o, cur, old),
        ScanType.Double => One<double>(o, cur, old),
        _ => false
    };

    static bool One<T>(ScanOptions o, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> old) where T : unmanaged, INumber<T>
    {
        bool isFloat = typeof(T) == typeof(float) || typeof(T) == typeof(double);
        T x = MemoryMarshal.Read<T>(cur);
        T y = MemoryMarshal.Read<T>(old);
        double tol = isFloat ? o.FloatTolerance ?? 0.5 : 0;
        if (isFloat && (!double.IsFinite(double.CreateTruncating(x)) || !double.IsFinite(double.CreateTruncating(y)))) return false;
        T tv1 = default, tv2 = default;
        if (!isFloat)
        {
            if (ScanOptions.NeedsValue(o.Mode) && !TryConvert<T>(o.Value, out tv1)) return false;
            if (o.Mode == ScanMode.Between && !TryConvert<T>(o.Value2, out tv2)) tv2 = T.CreateSaturating(o.Value2);
        }
        return Differential(o.Mode, x, y, isFloat, o.Value, o.Value2, tol, tv1, tv2);
    }

    /* ---------------------------------------------------------- values */

    public static string FormatValue(ScanType t, ReadOnlySpan<byte> b) => t switch
    {
        ScanType.Byte => b[0].ToString(CultureInfo.InvariantCulture),
        ScanType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ScanType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ScanType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ScanType.Float => BinaryPrimitives.ReadSingleLittleEndian(b).ToString("0.###", CultureInfo.InvariantCulture),
        ScanType.Double => BinaryPrimitives.ReadDoubleLittleEndian(b).ToString("0.###", CultureInfo.InvariantCulture),
        _ => Encoding.UTF8.GetString(b).Split('\0')[0]
    };

    public static double ToDouble(ScanType t, ReadOnlySpan<byte> b) => t switch
    {
        ScanType.Byte => b[0],
        ScanType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b),
        ScanType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b),
        ScanType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b),
        ScanType.Float => BinaryPrimitives.ReadSingleLittleEndian(b),
        ScanType.Double => BinaryPrimitives.ReadDoubleLittleEndian(b),
        _ => 0
    };

    /// <summary>Parses a typed value for the scan type ("100", "2,5", "0x1F"), null when invalid.</summary>
    public static double? ParseValue(ScanType t, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (t is ScanType.Float or ScanType.Double)
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, null, out var h) ? h : null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
             : double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
    }

    /// <summary>Encodes a value as the bytes to write for the given type.</summary>
    public static byte[] Encode(ScanType t, double v) => t switch
    {
        ScanType.Byte => new[] { (byte)(long)v },
        ScanType.Int16 => BitConverter.GetBytes((short)(long)v),
        ScanType.Int32 => BitConverter.GetBytes((int)(long)v),
        ScanType.Int64 => BitConverter.GetBytes((long)v),
        ScanType.Float => BitConverter.GetBytes((float)v),
        ScanType.Double => BitConverter.GetBytes(v),
        _ => Array.Empty<byte>()
    };

    public static ValueKind ToValueKind(ScanType t) => t switch
    {
        ScanType.Byte => ValueKind.Byte, ScanType.Int16 => ValueKind.Int16, ScanType.Int32 => ValueKind.Int32,
        ScanType.Int64 => ValueKind.Int64, ScanType.Float => ValueKind.Float, ScanType.Double => ValueKind.Double, _ => ValueKind.String
    };

    /// <summary>Table "VariableType" text for a scan type (what <see cref="CtEntry"/> expects).</summary>
    public static string ToVariableType(ScanType t) => t switch
    {
        ScanType.Byte => "Byte", ScanType.Int16 => "2 Bytes", ScanType.Int32 => "4 Bytes", ScanType.Int64 => "8 Bytes",
        ScanType.Float => "Float", ScanType.Double => "Double", _ => "String"
    };

    public static ScanType? FromVariableType(string variableType) => variableType.ToLowerInvariant() switch
    {
        "byte" => ScanType.Byte, "2 bytes" => ScanType.Int16, "4 bytes" => ScanType.Int32, "8 bytes" => ScanType.Int64,
        "float" => ScanType.Float, "double" => ScanType.Double, "string" => ScanType.String, _ => null
    };
}
