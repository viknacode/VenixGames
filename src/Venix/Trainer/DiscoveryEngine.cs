using System.Globalization;

namespace Venix.Trainer;

public enum DiscoveryKind { Health, Money, Ammo, Stamina, Custom }

/// <summary>One guided step: what the user should do in the game and how the scanner filters afterwards.</summary>
public sealed record DiscoveryStep(string Title, string Instruction, ScanMode Mode, int WaitSeconds = 0)
{
    public bool IsWait => WaitSeconds > 0;
}

/// <summary>A ranked address that probably holds what the user is looking for.</summary>
public sealed class DiscoveryCandidate
{
    public long Address { get; init; }
    public ScanType Type { get; init; }
    public double Value { get; init; }
    public double Score { get; init; }
    public string Reasons { get; init; } = "";
    public bool Static { get; init; }
    public string TypeLabel => ScanOptions.TypeLabel(Type);
    public string AddressLabel => Address.ToString("X");
    public string ValueLabel => Type is ScanType.Float or ScanType.Double ? Value.ToString("0.###", CultureInfo.InvariantCulture) : Value.ToString(CultureInfo.InvariantCulture);
    public int Percent => (int)Math.Round(Math.Clamp(Score, 0, 1) * 100);
}

/// <summary>
/// Smart Discovery: finds a value by behaviour instead of numbers. Takes a snapshot of the game, then after each
/// action the user performs ("lose health", "wait") keeps only the addresses that moved the expected way, for
/// several value types at once, and ranks what is left.
/// </summary>
public sealed class DiscoveryEngine
{
    readonly MemoryScanner _scanner;
    readonly Dictionary<ScanType, ScanResults?> _results = new();
    MemorySnapshot? _snapshot;

    public DiscoveryKind Kind { get; }
    public IReadOnlyList<ScanType> Types { get; }
    public List<DiscoveryStep> Steps { get; }
    public int StepIndex { get; private set; }
    public int StepsApplied { get; private set; }
    public bool Started => _snapshot != null || _results.Count > 0;
    public bool WritableOnly { get; set; } = true;
    public long MaxSnapshotBytes { get; set; } = 3L << 30;

    public DiscoveryEngine(MemoryScanner scanner, DiscoveryKind kind)
    {
        _scanner = scanner;
        Kind = kind;
        Types = kind switch
        {
            DiscoveryKind.Health => new[] { ScanType.Int32, ScanType.Float, ScanType.Int16, ScanType.Byte, ScanType.Double },
            DiscoveryKind.Money => new[] { ScanType.Int32, ScanType.Int64, ScanType.Float, ScanType.Double },
            DiscoveryKind.Ammo => new[] { ScanType.Int32, ScanType.Int16, ScanType.Byte },
            DiscoveryKind.Stamina => new[] { ScanType.Float, ScanType.Int32, ScanType.Double, ScanType.Byte },
            _ => new[] { ScanType.Int32, ScanType.Float, ScanType.Int16, ScanType.Byte, ScanType.Int64, ScanType.Double }
        };
        Steps = StepsFor(kind);
    }

    public static string KindLabel(DiscoveryKind k) => k switch
    {
        DiscoveryKind.Health => "Vida", DiscoveryKind.Money => "Dinheiro", DiscoveryKind.Ammo => "Munição",
        DiscoveryKind.Stamina => "Stamina", _ => "Personalizado"
    };

    static List<DiscoveryStep> StepsFor(DiscoveryKind k) => k switch
    {
        DiscoveryKind.Health => new()
        {
            new("Perca vida", "Volte ao jogo e tome dano (deixe um inimigo acertar você, caia de uma altura…). Depois clique em Continuar.", ScanMode.Decreased),
            new("Não faça nada", "Fique parado por 3 segundos sem tomar dano nem curar. O Venix descarta o que oscila sozinho.", ScanMode.Unchanged, 3),
            new("Recupere vida", "Use uma cura, poção ou espere regenerar. Se não der para curar, tome dano de novo e clique em \"Perdi de novo\".", ScanMode.Increased),
            new("Não faça nada", "Mais 3 segundos parado para confirmar que o valor é estável.", ScanMode.Unchanged, 3),
            new("Perca vida de novo", "Tome dano mais uma vez para fechar a lista.", ScanMode.Decreased),
        },
        DiscoveryKind.Money => new()
        {
            new("Gaste ou ganhe", "Compre algo, venda algo ou pegue dinheiro no jogo. Se o jogo mostra o total, digite quanto ficou e clique em Filtrar pelo valor; senão clique em Continuar.", ScanMode.Changed),
            new("Não faça nada", "3 segundos sem mexer no dinheiro.", ScanMode.Unchanged, 3),
            new("Gaste dinheiro", "Agora gaste: o valor tem que diminuir.", ScanMode.Decreased),
            new("Não faça nada", "Mais 3 segundos parado.", ScanMode.Unchanged, 3),
            new("Ganhe dinheiro", "Venda algo ou pegue dinheiro: o valor tem que aumentar.", ScanMode.Increased),
        },
        DiscoveryKind.Ammo => new()
        {
            new("Atire", "Dê alguns tiros (sem recarregar). Depois clique em Continuar.", ScanMode.Decreased),
            new("Não faça nada", "3 segundos sem atirar nem recarregar.", ScanMode.Unchanged, 3),
            new("Atire de novo", "Mais alguns tiros.", ScanMode.Decreased),
            new("Recarregue", "Recarregue a arma ou pegue munição: o valor tem que aumentar.", ScanMode.Increased),
            new("Não faça nada", "Mais 3 segundos parado.", ScanMode.Unchanged, 3),
        },
        DiscoveryKind.Stamina => new()
        {
            new("Gaste stamina", "Corra, role ou ataque até a barra baixar. Clique em Continuar enquanto ela ainda está baixa.", ScanMode.Decreased),
            new("Deixe recuperar", "Fique parado até a barra encher de novo.", ScanMode.Increased),
            new("Não faça nada", "3 segundos com a barra cheia e parada.", ScanMode.Unchanged, 3),
            new("Gaste de novo", "Corra de novo e clique em Continuar com a barra baixa.", ScanMode.Decreased),
        },
        _ => new()
    };

    /* ---------------------------------------------------------- flow */

    /// <summary>
    /// Starts the discovery. With <paramref name="knownValue"/> (what the game shows right now: money, health…)
    /// the first step is an exact search over every plausible type, which is a far better start than a raw
    /// snapshot; without it, takes the initial snapshot (memory copy of the game) and filters nothing yet.
    /// </summary>
    public Task StartAsync(double? knownValue, IProgress<ScanProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        _results.Clear();
        _snapshot = null;
        StepIndex = 0;
        StepsApplied = 0;
        if (knownValue is { } v)
        {
            ForEachType(progress, ct, t =>
            {
                var o = ExactOptions(t, v);
                try { return _scanner.FirstScan(o, null, ct); }
                catch (ScanLimitException) { return ScanResults.Empty(t, ScanOptions.SizeOf(t)); }
            });
            StepsApplied = 1;
            return;
        }
        var snap = new ScanOptions { Mode = ScanMode.Unknown, WritableOnly = WritableOnly, MaxSnapshotBytes = MaxSnapshotBytes };
        _snapshot = _scanner.Snapshot(snap, progress, ct);
    }, ct);

    /// <summary>Keeps only the candidates that now hold the value the user reads on screen.</summary>
    public Task ApplyExactAsync(double value, IProgress<ScanProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (_snapshot == null && _results.Count == 0) throw new InvalidOperationException("A descoberta ainda não começou.");
        ForEachType(progress, ct, t =>
        {
            _results.TryGetValue(t, out var prev);
            if (prev == null && _snapshot == null) return ScanResults.Empty(t, ScanOptions.SizeOf(t));
            try { return _scanner.NextScan(ExactOptions(t, value), prev, prev == null ? _snapshot : null, null, ct); }
            catch (ScanLimitException) { return prev ?? ScanResults.Empty(t, ScanOptions.SizeOf(t)); }
        });
        _snapshot = null;
        StepsApplied++;
    }, ct);

    ScanOptions ExactOptions(ScanType t, double v) => new()
    {
        Type = t, Mode = ScanMode.Exact, Value = v, WritableOnly = WritableOnly, Aligned = t != ScanType.Byte,
        FloatTolerance = Math.Max(0.001, Math.Abs(v - Math.Round(v)) < 1e-9 ? 0.5 : 0.05), MaxResults = 50_000_000
    };

    void ForEachType(IProgress<ScanProgress>? progress, CancellationToken ct, Func<ScanType, ScanResults> run)
    {
        int done = 0;
        foreach (var t in Types)
        {
            ct.ThrowIfCancellationRequested();
            _results[t] = run(t);
            done++;
            progress?.Report(new ScanProgress(done, Types.Count, _results[t].Count));
        }
    }

    /// <summary>Applies the given comparison to every type set (the first one compares against the snapshot).</summary>
    public Task ApplyAsync(ScanMode mode, IProgress<ScanProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (_snapshot == null && _results.Count == 0) throw new InvalidOperationException("A descoberta ainda não começou.");
        var types = Types.ToList();
        int done = 0;
        var stepProgress = progress == null ? null : new Progress<ScanProgress>(p =>
            progress.Report(new ScanProgress((long)(p.Percent * 100) + done * 10000L, types.Count * 10000L, p.Hits)));
        foreach (var t in types)
        {
            ct.ThrowIfCancellationRequested();
            var o = new ScanOptions { Type = t, Mode = mode, WritableOnly = WritableOnly, MaxResults = 50_000_000, Aligned = t != ScanType.Byte };
            _results.TryGetValue(t, out var prev);
            ScanResults r;
            try { r = _scanner.NextScan(o, prev, prev == null ? _snapshot : null, stepProgress, ct); }
            catch (ScanLimitException) { r = prev ?? ScanResults.Empty(t, ScanOptions.SizeOf(t)); } // keep going: next steps will cut it
            _results[t] = r;
            done++;
        }
        _snapshot = null; // every type now has a list; the raw copy is no longer needed
        StepsApplied++;
        if (StepIndex < Steps.Count) StepIndex++;
    }, ct);

    /// <summary>Waits the step's seconds and then keeps only what did not change.</summary>
    public async Task ApplyWaitAsync(int seconds, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        await ApplyAsync(ScanMode.Unchanged, progress, ct);
    }

    public DiscoveryStep? CurrentStep => StepIndex < Steps.Count ? Steps[StepIndex] : null;

    public long TotalCandidates => _snapshot != null ? _snapshot.Candidates(4) : _results.Values.Sum(r => (long)(r?.Count ?? 0));

    public IReadOnlyDictionary<ScanType, int> CountsByType => _results.ToDictionary(kv => kv.Key, kv => kv.Value?.Count ?? 0);

    /* ---------------------------------------------------------- ranking */

    /// <summary>Best candidates across all types, highest score first.</summary>
    public List<DiscoveryCandidate> Rank(int top = 25)
    {
        var all = new List<(long addr, ScanType type, double value, bool image, bool priv)>();
        foreach (var (t, r) in _results)
        {
            if (r == null) continue;
            for (int i = 0; i < r.Count; i++)
                all.Add((r.Addresses[i], t, MemoryScanner.ToDouble(t, r.ValueAt(i)), false, false));
        }
        if (all.Count == 0) return new();
        // region info (static vs heap)
        var regions = _scanner.Regions(false);
        var starts = regions.Select(r => r.Start).ToArray();
        (bool image, bool priv) RegionOf(long a)
        {
            int lo = 0, hi = starts.Length;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (regions[mid].End <= a) lo = mid + 1; else hi = mid; }
            return lo < regions.Count && regions[lo].Start <= a ? (regions[lo].Image, regions[lo].Private) : (false, false);
        }
        var modules = _scanner.Game.Modules;
        bool InModule(long a) => modules.Any(m => a >= m.Base && a < m.Base + m.Size);

        // proximity: sorted unique addresses; count neighbours within ±64 bytes
        var sortedAddrs = all.Select(x => x.addr).Distinct().OrderBy(x => x).ToArray();
        int Neighbours(long a)
        {
            int lo = Array.BinarySearch(sortedAddrs, a - 64); if (lo < 0) lo = ~lo;
            int hi = Array.BinarySearch(sortedAddrs, a + 64); if (hi < 0) hi = ~hi; else hi++;
            return Math.Max(0, hi - lo - 1);
        }

        // the same address matched as several widths (int32 100 also matches int16 100, byte 100): keep the widest plausible
        var byAddr = new Dictionary<long, List<(ScanType type, double value)>>();
        foreach (var x in all)
        {
            if (!byAddr.TryGetValue(x.addr, out var l)) byAddr[x.addr] = l = new();
            l.Add((x.type, x.value));
        }

        var scored = new List<DiscoveryCandidate>(Math.Min(all.Count, 100_000));
        foreach (var (addr, list) in byAddr)
        {
            var (image, priv) = RegionOf(addr);
            bool isStatic = InModule(addr);
            int neigh = Neighbours(addr);
            foreach (var (type, value) in list)
            {
                double s = TypeWeight(type);
                var why = new List<string>();
                if (!Plausible(type, value, out var rangeBonus, out var rangeWhy)) continue;
                s += rangeBonus;
                if (rangeWhy != null) why.Add(rangeWhy);
                int size = ScanOptions.SizeOf(type);
                if (size >= 4) { if (addr % 4 == 0) { s += 0.12; } else s -= 0.35; }
                else if (size == 2 && addr % 2 != 0) s -= 0.2;
                // narrower duplicates of a wider match at the same address are usually the same value seen through a smaller window
                if (list.Any(o => ScanOptions.SizeOf(o.type) > size && !IsFloat(o.type) && !IsFloat(type) && o.value == value)) s -= 0.4;
                if (IsFloat(type) && list.Any(o => !IsFloat(o.type) && IsRoundInt(o.value) && Math.Abs(value) < 1e-6)) continue; // denormal float = an int seen as float
                if (priv) { s += 0.08; why.Add("memória do jogo"); }
                if (isStatic) { s += 0.05; why.Add("endereço fixo"); }
                // a couple of neighbours hint at a player struct (health next to max health); a whole cluster moving
                // together is usually allocator metadata or an array, not the value we want
                if (neigh is >= 1 and <= 3) { s += 0.04 * neigh; why.Add(neigh == 1 ? "1 valor vizinho" : $"{neigh} valores vizinhos"); }
                else if (neigh >= 6) { s -= 0.12; why.Add($"bloco de {neigh} valores mudando juntos"); }
                if (value == 0 && Kind != DiscoveryKind.Ammo) s -= 0.1; // zero right now is rarely the stat the user just watched move
                scored.Add(new DiscoveryCandidate
                {
                    Address = addr, Type = type, Value = value, Score = s,
                    Reasons = string.Join(" · ", why), Static = isStatic
                });
            }
        }
        return scored.OrderByDescending(c => c.Score).ThenBy(c => c.Address).Take(top).ToList();
    }

    static bool IsFloat(ScanType t) => t is ScanType.Float or ScanType.Double;
    static bool IsRoundInt(double v) => v == Math.Floor(v);

    double TypeWeight(ScanType t) => Kind switch
    {
        DiscoveryKind.Health => t switch { ScanType.Int32 => 0.5, ScanType.Float => 0.48, ScanType.Int16 => 0.3, ScanType.Byte => 0.25, ScanType.Double => 0.3, _ => 0.2 },
        DiscoveryKind.Money => t switch { ScanType.Int32 => 0.5, ScanType.Int64 => 0.4, ScanType.Float => 0.35, ScanType.Double => 0.35, _ => 0.2 },
        DiscoveryKind.Ammo => t switch { ScanType.Int32 => 0.5, ScanType.Int16 => 0.35, ScanType.Byte => 0.3, _ => 0.2 },
        DiscoveryKind.Stamina => t switch { ScanType.Float => 0.5, ScanType.Int32 => 0.4, ScanType.Double => 0.3, ScanType.Byte => 0.25, _ => 0.2 },
        _ => t switch { ScanType.Int32 => 0.45, ScanType.Float => 0.42, ScanType.Int16 => 0.3, ScanType.Byte => 0.25, ScanType.Int64 => 0.3, ScanType.Double => 0.3, _ => 0.2 }
    };

    /// <summary>Value sanity for the kind being searched; also rejects garbage floats.</summary>
    bool Plausible(ScanType t, double v, out double bonus, out string? why)
    {
        bonus = 0;
        why = null;
        if (!double.IsFinite(v)) return false;
        if (IsFloat(t))
        {
            if (v != 0 && Math.Abs(v) < 1e-5) return false; // denormals / ints seen as floats
            if (Math.Abs(v) > 1e9) return false;
            if (v < 0 && Kind != DiscoveryKind.Custom) return false;
        }
        else if (v < 0 && Kind != DiscoveryKind.Custom) return false;
        double a = Math.Abs(v);
        switch (Kind)
        {
            case DiscoveryKind.Health:
                if (a > 1_000_000) return false;
                if (a <= 10_000) { bonus += 0.2; why = "faixa típica de vida"; }
                break;
            case DiscoveryKind.Money:
                if (a > 1e12) return false;
                if (a >= 1 && a <= 100_000_000) { bonus += 0.15; why = "faixa típica de dinheiro"; }
                break;
            case DiscoveryKind.Ammo:
                if (a > 100_000) return false;
                if (a <= 999) { bonus += 0.2; why = "faixa típica de munição"; }
                break;
            case DiscoveryKind.Stamina:
                if (a > 100_000) return false;
                if (a <= 1000) { bonus += 0.2; why = "faixa típica de stamina"; }
                break;
            default:
                if (a <= 1_000_000) bonus += 0.1;
                break;
        }
        if (!IsFloat(t) || IsRoundInt(v) || Math.Round(v, 2) == v) bonus += 0.05;
        return true;
    }

    /* ---------------------------------------------------------- testing a candidate */

    /// <summary>Writes a clearly different value (bigger, never zero) so the user can check the game's HUD. Returns the original bytes.</summary>
    public byte[]? TestWrite(DiscoveryCandidate c, out double written)
    {
        int size = ScanOptions.SizeOf(c.Type);
        var original = _scanner.Game.TryRead((nint)c.Address, size);
        written = 0;
        if (original == null) return null;
        double cur = MemoryScanner.ToDouble(c.Type, original);
        double max = c.Type switch { ScanType.Byte => 255, ScanType.Int16 => 32767, ScanType.Int32 => int.MaxValue / 2, _ => 1e15 };
        written = cur <= 0 ? Math.Min(max, 100) : Math.Min(max, cur * 2 + (IsFloat(c.Type) ? 0 : 1));
        if (written == cur) written = Math.Max(0, cur - 1);
        _scanner.Game.TryWrite((nint)c.Address, MemoryScanner.Encode(c.Type, written));
        return original;
    }

    public void Restore(DiscoveryCandidate c, byte[] original) => _scanner.Game.TryWrite((nint)c.Address, original);

    public void Reset()
    {
        _snapshot = null;
        _results.Clear();
        StepIndex = 0;
        StepsApplied = 0;
    }
}
