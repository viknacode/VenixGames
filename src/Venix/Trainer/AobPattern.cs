namespace Venix.Trainer;

/// <summary>
/// Byte pattern with wildcards in Cheat Engine's aobscan syntax:
/// "48 89 53 10 ?? 89 D8" — "?", "??", "*", "**" and "xx" are wildcards, and the
/// unspaced form ("48xxxxxx48") used by some table scripts is accepted too.
/// </summary>
public sealed class AobPattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _fixed;   // true = byte must match, false = wildcard
    private readonly int _anchor;     // index of the first fixed byte (used to speed up scanning)

    public string Text { get; }
    public int Length => _bytes.Length;

    private AobPattern(string text, byte[] bytes, bool[] isFixed)
    {
        Text = text;
        _bytes = bytes;
        _fixed = isFixed;
        _anchor = Array.IndexOf(isFixed, true);
        if (_anchor < 0) throw new ArgumentException("Pattern must contain at least one non-wildcard byte.", nameof(text));
    }

    public static AobPattern Parse(string text)
    {
        var bytes = new List<byte>();
        var isFixed = new List<bool>();

        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Single-character wildcard ("?" / "*").
            if (token.Length == 1 && IsWildcardChar(token[0]))
            {
                bytes.Add(0);
                isFixed.Add(false);
                continue;
            }
            if (token.Length % 2 != 0)
                throw new FormatException($"Invalid pattern token '{token}' in \"{text}\".");

            for (int i = 0; i < token.Length; i += 2)
            {
                string pair = token.Substring(i, 2);
                if (IsWildcardChar(pair[0]) && IsWildcardChar(pair[1]))
                {
                    bytes.Add(0);
                    isFixed.Add(false);
                }
                else
                {
                    bytes.Add(Convert.ToByte(pair, 16));
                    isFixed.Add(true);
                }
            }
        }

        return new AobPattern(text, bytes.ToArray(), isFixed.ToArray());
    }

    private static bool IsWildcardChar(char c) => c is '?' or '*' or 'x' or 'X';

    /// <summary>Returns the fixed bytes of a sub-range (throws if the range contains a wildcard).</summary>
    public byte[] FixedBytes(int start, int count)
    {
        for (int i = start; i < start + count; i++)
            if (!_fixed[i]) throw new InvalidOperationException($"Pattern byte {i} is a wildcard.");
        return _bytes.AsSpan(start, count).ToArray();
    }

    public bool MatchesAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + Length > data.Length) return false;
        for (int i = 0; i < Length; i++)
            if (_fixed[i] && data[offset + i] != _bytes[i]) return false;
        return true;
    }

    /// <summary>
    /// CE "aobscanmodule(name,$process,pattern)": every match inside the game's main module,
    /// in address order.
    /// </summary>
    public List<nint> ScanModule(GameProcess game, GameModule module, int maxResults = int.MaxValue)
    {
        var results = new List<nint>();
        ScanRange(game, module.Base, module.Size, results, maxResults);
        return results;
    }

    public List<nint> ScanRegion(GameProcess game, nint start, nint end, int maxResults = int.MaxValue)
    {
        var results = new List<nint>();
        if (end > start) ScanRange(game, start, (long)(end - start), results, maxResults);
        return results;
    }

    /// <summary>Scans an in-memory copy of a region (see <see cref="GameProcess.ReadModuleImage"/>).</summary>
    public List<nint> ScanBytes(ReadOnlySpan<byte> data, nint baseAddress, int maxResults = int.MaxValue)
    {
        var results = new List<nint>();
        int lastStart = data.Length - Length;
        int pos = 0;
        while (pos <= lastStart)
        {
            int rel = data.Slice(pos + _anchor, lastStart - pos + 1).IndexOf(_bytes[_anchor]);
            if (rel < 0) break;
            int start = pos + rel;
            if (MatchesAt(data, start))
            {
                results.Add(baseAddress + start);
                if (results.Count >= maxResults) break;
            }
            pos = start + 1;
        }
        return results;
    }

    /// <summary>
    /// CE Lua "AOBScan(pattern)": scans every committed, readable region of the process.
    /// Stops after <paramref name="maxResults"/> matches.
    /// </summary>
    public List<nint> ScanAllMemory(GameProcess game, int maxResults = 16, Action<long, long>? progress = null, bool privateOnly = false)
    {
        var results = new List<nint>();
        var regions = game.ReadableRegions();
        long total = regions.Sum(r => r.Size), done = 0;
        foreach (var (start, size) in regions)
        {
            ScanRange(game, start, size, results, maxResults);
            done += size;
            progress?.Invoke(done, total);
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    private void ScanRange(GameProcess game, nint rangeStart, long size, List<nint> results, int maxResults)
    {
        const int ChunkSize = 4 * 1024 * 1024;
        int overlap = Length - 1;
        var buffer = new byte[ChunkSize + overlap];
        nint rangeEnd = rangeStart + (nint)size;

        for (nint chunkStart = rangeStart; chunkStart < rangeEnd; chunkStart += ChunkSize)
        {
            if (results.Count >= maxResults) return;
            int toRead = (int)Math.Min(ChunkSize + overlap, rangeEnd - chunkStart);
            game.ReadLenient(chunkStart, buffer, toRead);
            var span = buffer.AsSpan(0, toRead);

            // Only report matches that *start* inside this chunk; the overlap exists so a
            // match straddling the boundary is still fully visible here.
            int lastStart = Math.Min(toRead - Length, ChunkSize - 1);
            int pos = 0;
            while (pos <= lastStart)
            {
                int rel = span.Slice(pos + _anchor, lastStart - pos + 1).IndexOf(_bytes[_anchor]);
                if (rel < 0) break;
                int start = pos + rel;
                if (MatchesAt(span, start))
                {
                    results.Add(chunkStart + start);
                    if (results.Count >= maxResults) return;
                }
                pos = start + 1;
            }
        }
    }

    public override string ToString() => Text;
}
