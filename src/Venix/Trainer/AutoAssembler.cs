using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Iced.Intel;

namespace Venix.Trainer;

/// <summary>What an enabled script leaves behind, needed by its [DISABLE] half.</summary>
public class ScriptState
{
    public Dictionary<string, long> Locals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, nint Address)> Allocs { get; } = new();
}

public sealed class AutoAssemblerException(string message) : Exception(message);

/// <summary>
/// Interpreter for Cheat Engine Auto Assembler scripts: runs the [ENABLE] or [DISABLE] half
/// directly against the game's memory (aobscan, alloc near, labels, injected code, data, assert,
/// readmem, reassemble, register/unregister symbols, dealloc). Lua and other CE-only features
/// are reported by <see cref="Unsupported"/> and shown as not supported.
/// </summary>
public class AutoAssembler
{
    readonly GameProcess _gp;
    readonly SymbolTable _globals;

    public AutoAssembler(GameProcess gp, SymbolTable globals)
    {
        _gp = gp;
        _globals = globals;
    }

    static readonly (Regex Rx, string Reason)[] UnsupportedChecks =
    {
        (new(@"\{\$lua\}", RegexOptions.IgnoreCase), "Usa Lua"),
        (new(@"\bluacall\s*\(", RegexOptions.IgnoreCase), "Usa Lua"),
        (new(@"\{\$c(code)?\}", RegexOptions.IgnoreCase), "Usa código C"),
        (new(@"^\s*createthread(andwait)?\s*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline), "Cria thread no jogo"),
        (new(@"^\s*(loadlibrary|loadbinary|include)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline), "Carrega arquivos externos"),
        (new(@"^\s*(aobscanex|aobscanall)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline), "Comando de scan não suportado")
    };

    public static string? Unsupported(string script)
    {
        foreach (var (rx, reason) in UnsupportedChecks)
            if (rx.IsMatch(script)) return reason;
        return null;
    }

    /* ---------------------------------------------------------- items */

    abstract record Item(int Line);
    sealed record Instr(int Line, string Text) : Item(Line);
    sealed record Data(int Line, int Width, string Values) : Item(Line);
    sealed record Nops(int Line, int Count) : Item(Line);
    sealed record ReadMem(int Line, string Address, int Size) : Item(Line);
    sealed record Reassemble(int Line, string Address) : Item(Line);
    sealed record LabelDef(int Line, string Name) : Item(Line);

    sealed class Block(long origin)
    {
        public long Origin = origin;
        public List<Item> Items = new();
        public byte[] Bytes = Array.Empty<byte>();
    }

    /* ---------------------------------------------------------- run */

    public void Run(string script, bool enable, ScriptState state)
    {
        var lines = Section(script, enable);
        if (lines.Count == 0) return;

        var locals = new Dictionary<string, long>(state.Locals, StringComparer.OrdinalIgnoreCase);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var register = new List<string>();
        var unregister = new List<string>();
        var dealloc = new List<string>();
        var newAllocs = new List<(string, nint)>();
        var code = new List<(int line, string text)>();

        try
        {
            foreach (var (ln, raw) in lines)
            {
                var m = Regex.Match(raw, @"^([A-Za-z_]\w*)\s*\((.*)\)\s*$");
                if (!m.Success) { code.Add((ln, raw)); continue; }
                var cmd = m.Groups[1].Value.ToLowerInvariant();
                var args = SplitArgs(m.Groups[2].Value);
                string Arg(int i) => i < args.Count ? args[i] : throw new AutoAssemblerException($"Linha {ln}: faltam argumentos em {cmd}()");

                switch (cmd)
                {
                    case "aobscan":
                    case "aobscanmodule":
                    case "aobscanregion":
                    {
                        var name = Arg(0);
                        var pattern = AobPattern.Parse(Arg(cmd == "aobscan" ? 1 : cmd == "aobscanmodule" ? 2 : 3));
                        List<nint> hits;
                        if (cmd == "aobscan") hits = pattern.ScanAllMemory(_gp, 1);
                        else if (cmd == "aobscanmodule")
                        {
                            var modName = Arg(1);
                            var mod = modName.Equals("$process", StringComparison.OrdinalIgnoreCase) ? _gp.MainModule : _gp.FindModule(modName)
                                      ?? throw new AutoAssemblerException($"Linha {ln}: módulo {modName} não encontrado no jogo");
                            hits = pattern.ScanModule(_gp, mod, 1);
                        }
                        else hits = pattern.ScanRegion(_gp, (nint)Eval(Arg(1), locals, ln), (nint)Eval(Arg(2), locals, ln), 1);
                        if (hits.Count == 0)
                            throw new AutoAssemblerException($"O padrão de \"{name}\" não foi encontrado na memória do jogo. A tabela provavelmente é de outra versão do jogo.");
                        locals[name] = hits[0];
                        break;
                    }
                    case "alloc":
                    case "globalalloc":
                    {
                        var name = Arg(0);
                        if (cmd == "globalalloc" && _globals.TryGet(name, out var existing)) { locals[name] = existing; break; }
                        var size = (int)Math.Clamp(ParseSize(Arg(1)), 0x100, 0x1000000);
                        long near = args.Count > 2 ? Eval(Arg(2), locals, ln) : _gp.MainModule.Base;
                        var addr = _gp.Allocate(size, (nint)near);
                        locals[name] = addr;
                        if (cmd == "globalalloc") _globals.Set(name, addr);
                        else newAllocs.Add((name, addr));
                        break;
                    }
                    case "dealloc": dealloc.Add(Arg(0)); break;
                    case "label": foreach (var a in args) declared.Add(a); break;
                    case "registersymbol": register.AddRange(args); break;
                    case "unregistersymbol": unregister.AddRange(args); break;
                    case "fullaccess": _gp.FullAccess((nint)Eval(Arg(0), locals, ln), (int)ParseSize(Arg(1))); break;
                    case "assert":
                    {
                        var addr = Eval(Arg(0), locals, ln);
                        var pattern = AobPattern.Parse(Arg(1));
                        var bytes = _gp.TryRead((nint)addr, pattern.Length);
                        if (bytes == null || !pattern.MatchesAt(bytes, 0))
                            throw new AutoAssemblerException("O código do jogo não confere com a tabela (assert). A tabela é de outra versão do jogo.");
                        break;
                    }
                    case "readmem":
                    case "reassemble":
                        code.Add((ln, raw));
                        break;
                    default:
                        throw new AutoAssemblerException($"Linha {ln}: comando não suportado: {cmd}()");
                }
            }

            var blocks = BuildBlocks(code, locals, declared);
            var positions = Layout(blocks, locals, declared);

            using (_gp.SuspendThreads())
                foreach (var b in blocks.Where(b => b.Bytes.Length > 0))
                    _gp.Write((nint)b.Origin, b.Bytes);

            foreach (var (k, v) in positions) locals[k] = v;
            foreach (var name in register)
                if (locals.TryGetValue(name, out var v)) _globals.Set(name, v);
            foreach (var name in unregister) _globals.Remove(name);

            if (enable)
            {
                state.Locals.Clear();
                foreach (var (k, v) in locals) state.Locals[k] = v;
                state.Allocs.AddRange(newAllocs);
            }
            foreach (var name in dealloc)
            {
                var hit = state.Allocs.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (hit.Address != 0) { _gp.Free(hit.Address); state.Allocs.Remove(hit); }
            }
            if (!enable)
            {
                // anything the script forgot to dealloc stays allocated (CE behaves the same); keep state small
                state.Locals.Clear();
            }
        }
        catch
        {
            // a failed enable must not leak caves
            foreach (var (_, addr) in newAllocs) _gp.Free(addr);
            throw;
        }
    }

    /* ---------------------------------------------------------- parsing helpers */

    static List<(int, string)> Section(string script, bool enable)
    {
        var text = script.Replace("\r\n", "\n");
        var sec = Regex.Matches(text, @"^\s*\[(ENABLE|DISABLE)\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        string body;
        if (sec.Count == 0) body = enable ? text : "";
        else
        {
            var want = enable ? "ENABLE" : "DISABLE";
            var sb = new StringBuilder();
            // code before the first marker belongs to both halves (CE behaviour)
            sb.Append(text[..sec[0].Index]).Append('\n');
            for (int i = 0; i < sec.Count; i++)
            {
                if (!sec[i].Groups[1].Value.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                var start = sec[i].Index + sec[i].Length;
                var end = i + 1 < sec.Count ? sec[i + 1].Index : text.Length;
                sb.Append(text[start..end]).Append('\n');
            }
            body = sb.ToString();
        }

        // block comments { ... } (CE directives like {$try} included), then // comments
        body = Regex.Replace(body, @"\{[^}]*\}", m => new string('\n', m.Value.Count(c => c == '\n')));
        body = Regex.Replace(body, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = new List<(int, string)>();
        var defines = new List<(Regex, string)>();
        var split = body.Split('\n');
        for (int i = 0; i < split.Length; i++)
        {
            var l = StripLineComment(split[i]).Trim();
            if (l.Length == 0) continue;
            var d = Regex.Match(l, @"^define\s*\(\s*([^,\s]+)\s*,\s*(.*)\)\s*$", RegexOptions.IgnoreCase);
            if (d.Success) { defines.Add((new Regex($@"(?<![\w.]){Regex.Escape(d.Groups[1].Value)}(?![\w])", RegexOptions.IgnoreCase), d.Groups[2].Value.Trim())); continue; }
            lines.Add((i + 1, l));
        }
        for (int pass = 0; pass < 3 && defines.Count > 0; pass++)
            for (int i = 0; i < lines.Count; i++)
                foreach (var (rx, val) in defines)
                    lines[i] = (lines[i].Item1, rx.Replace(lines[i].Item2, val));
        return lines;
    }

    static string StripLineComment(string s)
    {
        bool inQuote = false;
        char q = '\0';
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (inQuote) { if (s[i] == q) inQuote = false; continue; }
            if (s[i] is '\'' or '"') { inQuote = true; q = s[i]; continue; }
            if (s[i] == '/' && s[i + 1] == '/') return s[..i];
        }
        return s;
    }

    static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        bool quote = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') quote = !quote;
            if (quote) continue;
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ',' && depth == 0) { list.Add(s[start..i].Trim()); start = i + 1; }
        }
        list.Add(s[start..].Trim());
        return list.Where(a => a.Length > 0).ToList();
    }

    static long ParseSize(string s)
    {
        s = s.Trim();
        if (s.StartsWith('#')) return long.Parse(s[1..], CultureInfo.InvariantCulture);
        if (s.StartsWith('$')) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.Parse(s, NumberStyles.HexNumber);
    }

    long? Lookup(string name, Dictionary<string, long> locals)
    {
        if (locals.TryGetValue(name, out var v)) return v;
        if (_globals.TryGet(name, out v)) return v;
        if (name.Equals("$process", StringComparison.OrdinalIgnoreCase)) return _gp.MainModule.Base;
        var mod = _gp.FindModule(name);
        return mod?.Base;
    }

    long Eval(string expr, Dictionary<string, long> locals, int line)
    {
        var e = new AddressExpr(_gp, n => Lookup(n, locals));
        return e.Eval(expr) ?? throw new AutoAssemblerException($"Linha {line}: endereço desconhecido \"{expr}\"");
    }

    /* ---------------------------------------------------------- code blocks */

    List<Block> BuildBlocks(List<(int line, string text)> code, Dictionary<string, long> locals, HashSet<string> declared)
    {
        var blocks = new List<Block>();
        Block? current = null;
        foreach (var (ln, raw) in code)
        {
            var text = raw;
            var lm = Regex.Match(text, @"^((?:""[^""]+""|[^\s:""])+(?:\s*[+\-]\s*(?:""[^""]+""|[^\s:""])+)*)\s*:(?!\[)\s*(.*)$");
            if (lm.Success && !Regex.IsMatch(lm.Groups[1].Value, @"^(es|cs|ss|ds|fs|gs)$", RegexOptions.IgnoreCase))
            {
                var target = lm.Groups[1].Value.Trim();
                bool isPlainLabel = declared.Contains(target) && !locals.ContainsKey(target);
                if (isPlainLabel)
                {
                    if (current == null) throw new AutoAssemblerException($"Linha {ln}: label \"{target}\" fora de um bloco de código");
                    current.Items.Add(new LabelDef(ln, target));
                }
                else
                {
                    current = new Block(Eval(target, locals, ln));
                    blocks.Add(current);
                }
                text = lm.Groups[2].Value.Trim();
                if (text.Length == 0) continue;
            }
            if (current == null) throw new AutoAssemblerException($"Linha {ln}: instrução sem endereço de destino: {raw}");
            current.Items.Add(ParseItem(ln, text));
        }
        return blocks;
    }

    static Item ParseItem(int ln, string text)
    {
        var m = Regex.Match(text, @"^(db|dw|dd|dq)\s+(.*)$", RegexOptions.IgnoreCase);
        if (m.Success)
            return new Data(ln, m.Groups[1].Value.ToLowerInvariant() switch { "db" => 1, "dw" => 2, "dd" => 4, _ => 8 }, m.Groups[2].Value);
        m = Regex.Match(text, @"^nop\s+(\S+)$", RegexOptions.IgnoreCase);
        if (m.Success) return new Nops(ln, (int)ParseSize(m.Groups[1].Value));
        m = Regex.Match(text, @"^readmem\s*\((.*),(.*)\)$", RegexOptions.IgnoreCase);
        if (m.Success) return new ReadMem(ln, m.Groups[1].Value.Trim(), (int)ParseSize(m.Groups[2].Value));
        m = Regex.Match(text, @"^reassemble\s*\((.*)\)$", RegexOptions.IgnoreCase);
        if (m.Success) return new Reassemble(ln, m.Groups[1].Value.Trim());
        return new Instr(ln, text);
    }

    /// <summary>
    /// Assigns label addresses and encodes every block. Forward labels start at a placeholder
    /// (the current IP) and the layout is repeated until no label moves.
    /// </summary>
    Dictionary<string, long> Layout(List<Block> blocks, Dictionary<string, long> locals, HashSet<string> declared)
    {
        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (int pass = 0; pass < 8; pass++)
        {
            var next = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in blocks)
            {
                var bytes = new List<byte>();
                long ip = b.Origin;
                foreach (var item in b.Items)
                {
                    if (item is LabelDef ld) { next[ld.Name] = ip; continue; }
                    long here = ip;
                    long? Resolve(string name)
                    {
                        if (positions.TryGetValue(name, out var p)) return p;
                        var v = Lookup(name, locals);
                        if (v != null) return v;
                        if (declared.Contains(name)) return here; // placeholder until the label is placed
                        return null;
                    }
                    byte[] enc;
                    try { enc = Emit(item, (ulong)ip, Resolve); }
                    catch (AsmParser.AsmException ex) { throw new AutoAssemblerException($"Linha {item.Line}: {ex.Message}"); }
                    bytes.AddRange(enc);
                    ip += enc.Length;
                }
                b.Bytes = bytes.ToArray();
            }
            bool stable = pass > 0 && next.Count == positions.Count && next.All(kv => positions.TryGetValue(kv.Key, out var v) && v == kv.Value);
            positions = next;
            if (stable) return positions;
        }
        return positions;
    }

    byte[] Emit(Item item, ulong ip, Func<string, long?> resolve)
    {
        switch (item)
        {
            case Nops n: return Enumerable.Repeat((byte)0x90, n.Count).ToArray();
            case ReadMem r:
            {
                var addr = new AddressExpr(_gp, resolve).Eval(r.Address) ?? throw new AutoAssemblerException($"Linha {r.Line}: endereço desconhecido em readmem");
                return _gp.Read((nint)addr, r.Size);
            }
            case Reassemble ra:
            {
                var addr = new AddressExpr(_gp, resolve).Eval(ra.Address) ?? throw new AutoAssemblerException($"Linha {ra.Line}: endereço desconhecido em reassemble");
                var raw = _gp.Read((nint)addr, 16);
                var dec = Iced.Intel.Decoder.Create(_gp.Bitness, new ByteArrayCodeReader(raw));
                dec.IP = (ulong)addr;
                var instr = dec.Decode();
                return AsmParser.Encode(instr, _gp.Bitness, ip);
            }
            case Data d: return EncodeData(d, resolve);
            case Instr i:
            {
                var instr = AsmParser.Build(i.Text, _gp.Bitness, ip, resolve, out var unresolved);
                if (unresolved) throw new AutoAssemblerException($"Linha {i.Line}: símbolo desconhecido em \"{i.Text}\"");
                return AsmParser.Encode(instr, _gp.Bitness, ip);
            }
        }
        return Array.Empty<byte>();
    }

    static byte[] EncodeData(Data d, Func<string, long?> resolve)
    {
        var bytes = new List<byte>();
        foreach (var token in DataTokens(d.Values))
        {
            if (token.Length >= 2 && (token[0] == '\'' || token[0] == '"') && token[^1] == token[0])
            {
                var s = Encoding.ASCII.GetBytes(token[1..^1]);
                foreach (var c in s)
                {
                    bytes.Add(c);
                    for (int k = 1; k < d.Width; k++) bytes.Add(0);
                }
                continue;
            }
            bool unresolved = false, symbolic = false;
            var v = AsmParser.EvalImmediate(token, resolve, ref unresolved, ref symbolic);
            if (unresolved) throw new AutoAssemblerException($"Linha {d.Line}: valor desconhecido \"{token}\"");
            var all = BitConverter.GetBytes(v);
            bytes.AddRange(all.Take(d.Width));
        }
        return bytes.ToArray();
    }

    static IEnumerable<string> DataTokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
            if (i >= s.Length) yield break;
            if (s[i] is '\'' or '"')
            {
                var q = s[i];
                var end = s.IndexOf(q, i + 1);
                if (end < 0) end = s.Length - 1;
                yield return s[i..(end + 1)];
                i = end + 1;
                continue;
            }
            int start = i;
            int depth = 0;
            while (i < s.Length && !(depth == 0 && (char.IsWhiteSpace(s[i]) || s[i] == ',')))
            {
                if (s[i] == '(') depth++;
                if (s[i] == ')') depth--;
                i++;
            }
            yield return s[start..i];
        }
    }
}
