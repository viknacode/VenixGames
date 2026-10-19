using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Iced.Intel;

namespace Venix.Trainer;

/// <summary>
/// Turns one line of Cheat Engine–style Intel assembly ("mov [rbx+10],(float)100") into an Iced
/// <see cref="Instruction"/>. The opcode is chosen by matching the parsed operands against every
/// Iced <see cref="Code"/> for that mnemonic, so it covers the whole x86/x64 instruction set
/// (GPR, x87, SSE/AVX) without a hand-written table.
/// Numbers are hexadecimal by default (CE convention); "#123" is decimal.
/// Operands that use symbols always get fixed-size encodings (rel32, disp32, imm32/imm64)
/// so the layout stays stable while label addresses are being resolved.
/// </summary>
public static class AsmParser
{
    public sealed class AsmException(string message) : Exception(message);

    enum OpType { Reg, Mem, Imm }

    sealed class Operand
    {
        public OpType Type;
        public Register Reg;
        // memory
        public Register Base, Index, Segment;
        public int Scale = 1;
        public long Disp;
        public int SizeHint;          // bytes from "dword ptr" etc. (0 = none)
        public bool DispSymbolic;     // displacement came from a symbol (may be far away)
        // immediate
        public long Imm;
        public bool ImmSymbolic;
    }

    static readonly Dictionary<Mnemonic, List<Code>> ByMnemonic = BuildIndex();

    static Dictionary<Mnemonic, List<Code>> BuildIndex()
    {
        var map = new Dictionary<Mnemonic, List<Code>>();
        foreach (Code code in Enum.GetValues(typeof(Code)))
        {
            if (code == Code.INVALID) continue;
            var op = code.ToOpCode();
            if (!op.IsInstruction) continue;
            if (op.Encoding is not (EncodingKind.Legacy or EncodingKind.VEX)) continue;
            if (!map.TryGetValue(op.Mnemonic, out var list)) map[op.Mnemonic] = list = new();
            list.Add(code);
        }
        return map;
    }

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jz"] = "je", ["jnz"] = "jne", ["jc"] = "jb", ["jnae"] = "jb", ["jnc"] = "jae", ["jnb"] = "jae",
        ["jna"] = "jbe", ["jnbe"] = "ja", ["jnge"] = "jl", ["jnl"] = "jge", ["jng"] = "jle", ["jnle"] = "jg",
        ["jpe"] = "jp", ["jpo"] = "jnp", ["sal"] = "shl", ["retn"] = "ret", ["wait"] = "fwait",
        ["setz"] = "sete", ["setnz"] = "setne", ["setc"] = "setb", ["setnae"] = "setb", ["setnc"] = "setae", ["setnb"] = "setae",
        ["setna"] = "setbe", ["setnbe"] = "seta", ["setnge"] = "setl", ["setnl"] = "setge", ["setng"] = "setle", ["setnle"] = "setg",
        ["cmovz"] = "cmove", ["cmovnz"] = "cmovne", ["cmovc"] = "cmovb", ["cmovnae"] = "cmovb", ["cmovnc"] = "cmovae", ["cmovnb"] = "cmovae",
        ["cmovna"] = "cmovbe", ["cmovnbe"] = "cmova", ["cmovnge"] = "cmovl", ["cmovnl"] = "cmovge", ["cmovng"] = "cmovle", ["cmovnle"] = "cmovg",
        ["pushfd"] = "pushfd", ["popfd"] = "popfd"
    };

    static readonly Regex SizeRx = new(@"^(byte|word|dword|qword|tword|tbyte|xmmword|dqword|oword|ymmword|fword|real4|real8|real10|float|double)\b\s*(ptr\b)?\s*(?=\[|[a-z]{2}:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>CE accepts the classic operand-less x87 forms ("fmulp" = "fmulp st(1),st(0)").</summary>
    static readonly Dictionary<string, string> X87Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["faddp"] = "st(1),st(0)", ["fsubp"] = "st(1),st(0)", ["fsubrp"] = "st(1),st(0)", ["fmulp"] = "st(1),st(0)",
        ["fdivp"] = "st(1),st(0)", ["fdivrp"] = "st(1),st(0)", ["fxch"] = "st(0),st(1)", ["fcom"] = "st(1)",
        ["fcomp"] = "st(1)", ["fucom"] = "st(1)", ["fucomp"] = "st(1)"
    };

    /// <summary>Parses and builds the instruction. <paramref name="resolve"/> returns null for not-yet-known symbols.</summary>
    public static Instruction Build(string text, int bitness, ulong ip, Func<string, long?> resolve, out bool usesUnresolved)
    {
        usesUnresolved = false;
        var line = text.Trim();
        bool rep = false, repne = false, lockp = false;
        while (true)
        {
            var m = Regex.Match(line, @"^(rep|repe|repz|repne|repnz|lock)\s+", RegexOptions.IgnoreCase);
            if (!m.Success) break;
            var p = m.Groups[1].Value.ToLowerInvariant();
            if (p == "lock") lockp = true; else if (p is "repne" or "repnz") repne = true; else rep = true;
            line = line[m.Length..];
        }

        var sp = line.IndexOfAny(new[] { ' ', '\t' });
        var mnemText = (sp < 0 ? line : line[..sp]).Trim().ToLowerInvariant();
        var opText = sp < 0 ? "" : line[(sp + 1)..].Trim();
        if (Aliases.TryGetValue(mnemText, out var alias)) mnemText = alias;

        // string instructions without operands: movsb, stosd, lodsq, scasb, cmpsw...
        if (opText.Length == 0 && Regex.IsMatch(mnemText, @"^(movs|stos|lods|scas|cmps|ins|outs)[bwdq]$"))
            return StringInstruction(mnemText, bitness, rep, repne);

        if (opText.Length == 0 && X87Defaults.TryGetValue(mnemText, out var x87)) opText = x87;

        if (!Enum.TryParse<Mnemonic>(mnemText, true, out var mnemonic) || !ByMnemonic.TryGetValue(mnemonic, out var codes))
            throw new AsmException($"Instrução desconhecida: {mnemText}");

        bool unresolved = false;
        var ops = SplitOperands(opText).Select(o => ParseOperand(o, bitness, resolve, ref unresolved)).ToList();
        usesUnresolved = unresolved;

        Instruction? best = null;
        int bestScore = int.MinValue;
        foreach (var code in codes)
        {
            var info = code.ToOpCode();
            if (bitness == 64 ? !info.Mode64 : !info.Mode32) continue;
            if (info.OpCount != ops.Count) continue;
            if (!TryCreate(code, info, ops, bitness, ip, out var instr, out var score)) continue;
            if (score > bestScore) { best = instr; bestScore = score; }
        }
        if (best is not { } chosen) throw new AsmException($"Não foi possível montar \"{text.Trim()}\" (operandos não suportados)");
        if (rep) chosen.HasRepPrefix = true;
        if (repne) chosen.HasRepnePrefix = true;
        if (lockp) chosen.HasLockPrefix = true;
        return chosen;
    }

    static Instruction StringInstruction(string mnem, int bitness, bool rep, bool repne)
    {
        var name = "Create" + char.ToUpperInvariant(mnem[0]) + mnem[1..];
        var method = typeof(Instruction).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().FirstOrDefault()?.Name == "addressSize")
            ?? throw new AsmException($"Instrução de string não suportada: {mnem}");
        var args = method.GetParameters().Select(p =>
            p.Name == "addressSize" ? (object)bitness :
            p.ParameterType == typeof(Register) ? Register.None :
            p.ParameterType == typeof(RepPrefixKind) ? (repne ? RepPrefixKind.Repne : rep ? RepPrefixKind.Repe : RepPrefixKind.None) :
            p.HasDefaultValue ? p.DefaultValue! : throw new AsmException(name)).ToArray();
        return (Instruction)method.Invoke(null, args)!;
    }

    /* ---------------------------------------------------------- operand parsing */

    static List<string> SplitOperands(string s)
    {
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '[' or '(') depth++;
            else if (s[i] is ']' or ')') depth--;
            else if (s[i] == ',' && depth == 0) { list.Add(s[start..i].Trim()); start = i + 1; }
        }
        if (s.Trim().Length > 0) list.Add(s[start..].Trim());
        return list;
    }

    static Operand ParseOperand(string text, int bitness, Func<string, long?> resolve, ref bool unresolved)
    {
        var t = text.Trim();
        t = Regex.Replace(t, @"^(short|near|far|long)\s+", "", RegexOptions.IgnoreCase);
        int size = 0;
        var sm = SizeRx.Match(t);
        if (sm.Success)
        {
            size = sm.Groups[1].Value.ToLowerInvariant() switch
            {
                "byte" => 1, "word" => 2, "dword" or "real4" or "float" => 4, "qword" or "real8" or "double" => 8,
                "tword" or "tbyte" or "real10" => 10, "fword" => 6, "xmmword" or "dqword" or "oword" => 16, "ymmword" => 32, _ => 0
            };
            t = t[sm.Length..].Trim();
        }

        var segment = Register.None;
        var seg = Regex.Match(t, @"^(es|cs|ss|ds|fs|gs)\s*:\s*(?=\[)", RegexOptions.IgnoreCase);
        if (seg.Success)
        {
            segment = ParseRegister(seg.Groups[1].Value)!.Value;
            t = t[seg.Length..];
        }

        if (t.StartsWith('[') && t.EndsWith(']'))
            return ParseMemory(t[1..^1], size, segment, bitness, resolve, ref unresolved);

        var reg = ParseRegister(t);
        if (reg != null && size == 0) return new Operand { Type = OpType.Reg, Reg = reg.Value };

        bool symbolic = false;
        var value = EvalImmediate(t, resolve, ref unresolved, ref symbolic);
        return new Operand { Type = OpType.Imm, Imm = value, ImmSymbolic = symbolic };
    }

    static Register? ParseRegister(string t)
    {
        t = t.Trim().ToLowerInvariant();
        var st = Regex.Match(t, @"^st\(?([0-7])?\)?$");
        if (st.Success && t.StartsWith("st")) return Register.ST0 + (st.Groups[1].Success ? int.Parse(st.Groups[1].Value) : 0);
        if (t is "rip" or "eip") return null;
        return Enum.TryParse<Register>(t, true, out var r) && r != Register.None && char.IsLetter(t[0]) && t.All(char.IsLetterOrDigit) ? r : null;
    }

    static Operand ParseMemory(string inner, int size, Register segment, int bitness, Func<string, long?> resolve, ref bool unresolved)
    {
        var op = new Operand { Type = OpType.Mem, SizeHint = size, Segment = segment, Base = Register.None, Index = Register.None };
        // split into signed terms
        var terms = new List<(int sign, string text)>();
        int sign = 1, depth = 0, start = 0;
        var s = inner.Trim();
        for (int i = 0; i <= s.Length; i++)
        {
            if (i < s.Length && s[i] == '(') depth++;
            if (i < s.Length && s[i] == ')') depth--;
            bool cut = i == s.Length || (depth == 0 && (s[i] == '+' || s[i] == '-') && i > start);
            if (!cut) continue;
            var part = s[start..i].Trim();
            if (part.Length > 0) terms.Add((sign, part));
            if (i < s.Length) sign = s[i] == '-' ? -1 : 1;
            start = i + 1;
        }
        foreach (var (sg, part) in terms)
        {
            var mul = part.Split('*');
            if (mul.Length == 2)
            {
                var r1 = ParseRegister(mul[0]);
                var r2 = ParseRegister(mul[1]);
                if (r1 != null || r2 != null)
                {
                    op.Index = r1 ?? r2!.Value;
                    op.Scale = (int)ParseNumber(r1 != null ? mul[1] : mul[0]);
                    continue;
                }
            }
            var reg = ParseRegister(part);
            if (reg != null)
            {
                if (op.Base == Register.None) op.Base = reg.Value;
                else if (op.Index == Register.None) op.Index = reg.Value;
                else throw new AsmException($"Endereço de memória inválido: [{inner}]");
                continue;
            }
            bool symbolic = false;
            op.Disp += sg * EvalImmediate(part, resolve, ref unresolved, ref symbolic);
            op.DispSymbolic |= symbolic;
        }
        return op;
    }

    static long ParseNumber(string t)
    {
        t = t.Trim();
        if (t.StartsWith('#')) return long.Parse(t[1..], CultureInfo.InvariantCulture);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.StartsWith('$')) t = t[1..];
        return long.Parse(t, NumberStyles.HexNumber);
    }

    /// <summary>Numbers (hex by default), "#dec", "(float)1.5", "(double)2", symbols and +/- combinations.</summary>
    public static long EvalImmediate(string text, Func<string, long?> resolve, ref bool unresolved, ref bool symbolic)
    {
        var t = text.Trim();
        var im = Regex.Match(t, @"^(?:\((?:int|integer|long)\)\s*|#)(-?\d+)$", RegexOptions.IgnoreCase);
        if (im.Success) return long.Parse(im.Groups[1].Value, CultureInfo.InvariantCulture);
        var fm = Regex.Match(t, @"^\((float|double)\)\s*(-?[0-9]*\.?[0-9]+(?:e[+-]?\d+)?)$", RegexOptions.IgnoreCase);
        if (fm.Success)
        {
            var v = double.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture);
            return fm.Groups[1].Value.Equals("float", StringComparison.OrdinalIgnoreCase)
                ? BitConverter.SingleToInt32Bits((float)v) & 0xFFFFFFFFL
                : BitConverter.DoubleToInt64Bits(v);
        }
        // sum of terms
        long total = 0;
        foreach (Match m in Regex.Matches(t, @"([+-]?)\s*([^+\-]+)"))
        {
            var term = m.Groups[2].Value.Trim();
            if (term.Length == 0) continue;
            long v;
            if (term.StartsWith('#') || term.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || (term.StartsWith('$') && term.Length > 1 && AddressExpr.IsHex(term[1..])))
                v = ParseNumber(term);
            else if (term.StartsWith('\'') && term.EndsWith('\'') && term.Length >= 3)
                v = term[1..^1].Select((c, i) => (long)c << (8 * i)).Sum();
            else
            {
                var sym = resolve(term);
                if (sym != null) { v = sym.Value; symbolic = true; }
                else if (AddressExpr.IsHex(term)) v = long.Parse(term, NumberStyles.HexNumber);
                else { v = 0; unresolved = true; symbolic = true; }
            }
            total += m.Groups[1].Value == "-" ? -v : v;
        }
        return total;
    }

    /* ---------------------------------------------------------- matching */

    static bool TryCreate(Code code, OpCodeInfo info, List<Operand> ops, int bitness, ulong ip, out Instruction instr, out int score)
    {
        instr = default;
        instr.Code = code;
        score = 0;
        bool anyWordReg = ops.Any(o => o.Type == OpType.Reg && o.Reg.IsGPR16()) || ops.Any(o => o.SizeHint == 2);
        if (info.OperandSize == 16 && !anyWordReg) score -= 50;
        if (info.Encoding == EncodingKind.VEX) score -= 5;

        for (int i = 0; i < ops.Count; i++)
        {
            var kind = info.GetOpKind(i);
            var name = kind.ToString();
            var op = ops[i];
            switch (op.Type)
            {
                case OpType.Reg:
                    if (!RegisterFits(name, op.Reg)) return false;
                    instr.SetOpKind(i, OpKind.Register);
                    instr.SetOpRegister(i, op.Reg);
                    break;

                case OpType.Mem:
                    if (!(name.EndsWith("_or_mem") || name is "mem" or "mem_mpx" or "mem_offs" or "sibmem" || name.EndsWith("_or_mem_mpx"))) return false;
                    if (name == "mem_offs") score -= 20; // prefer modrm forms; moffs only when nothing else fits
                    instr.SetOpKind(i, OpKind.Memory);
                    SetMemory(ref instr, op, bitness, ip);
                    break;

                case OpType.Imm:
                    if (name.StartsWith("br"))
                    {
                        if (op.Imm < 0) return false;
                        bool shortForm = name.EndsWith("_1");
                        score += shortForm ? -10 : 0; // near branches keep the size stable
                        instr.SetOpKind(i, bitness == 64 ? OpKind.NearBranch64 : OpKind.NearBranch32);
                        if (bitness == 64) instr.NearBranch64 = (ulong)op.Imm; else instr.NearBranch32 = (uint)op.Imm;
                        break;
                    }
                    if (!name.StartsWith("imm") || name == "imm4_m2z") return false;
                    if (!ImmediateFits(name, op, bitness, out var opKind, out var immScore)) return false;
                    score += immScore;
                    instr.SetOpKind(i, opKind);
                    instr.SetImmediate(i, (ulong)op.Imm);
                    break;
            }
        }

        // memory size: explicit "dword ptr" must match; without it prefer dword like CE
        var memOp = ops.FirstOrDefault(o => o.Type == OpType.Mem);
        if (memOp != null)
        {
            int msize = instr.MemorySize.GetSize();
            if (memOp.SizeHint != 0 && msize != 0 && msize != memOp.SizeHint)
            {
                // x87 "fld st" style or packed ops report element sizes; only reject plain scalar mismatches
                if (!(memOp.SizeHint == 16 && msize >= 16)) return false;
            }
            if (memOp.SizeHint == 0 && ops.All(o => o.Type != OpType.Reg)) score += msize == 4 ? 20 : msize == 8 ? (bitness == 64 ? 5 : 0) : 0;
        }
        return true;
    }

    static bool RegisterFits(string kind, Register r) => kind switch
    {
        "al" => r == Register.AL, "cl" => r == Register.CL, "ax" => r == Register.AX, "dx" => r == Register.DX,
        "eax" => r == Register.EAX, "rax" => r == Register.RAX, "st0" => r == Register.ST0,
        "es" => r == Register.ES, "cs" => r == Register.CS, "ss" => r == Register.SS, "ds" => r == Register.DS, "fs" => r == Register.FS, "gs" => r == Register.GS,
        "sti_opcode" => r.IsST(),
        "seg_reg" => r.IsSegmentRegister(),
        "cr_reg" => r.IsCR(), "dr_reg" => r.IsDR(),
        _ when kind.StartsWith("r8_") => r.IsGPR8(),
        _ when kind.StartsWith("r16_") => r.IsGPR16(),
        _ when kind.StartsWith("r32_") => r.IsGPR32(),
        _ when kind.StartsWith("r64_") => r.IsGPR64(),
        _ when kind.StartsWith("mm_") => r.IsMM(),
        _ when kind.StartsWith("xmm") => r.IsXMM(),
        _ when kind.StartsWith("ymm") => r.IsYMM(),
        _ => false
    };

    static bool ImmediateFits(string kind, Operand op, int bitness, out OpKind opKind, out int score)
    {
        long v = op.Imm;
        bool sym = op.ImmSymbolic;
        score = 0;
        opKind = OpKind.Immediate8;
        bool fits8s = v is >= -128 and <= 127;
        bool fits32 = v is >= int.MinValue and <= uint.MaxValue;
        // a 32-bit value written as FFFFFFF0 is the same as -10 for a 32-bit operand
        long as32 = fits32 ? (int)(uint)(v & 0xFFFFFFFF) : v;
        switch (kind)
        {
            case "imm8": opKind = OpKind.Immediate8; score = 10; return !sym && v is >= -128 and <= 255;
            case "imm8_const_1": opKind = OpKind.Immediate8; score = 20; return !sym && v == 1;
            case "imm16": opKind = OpKind.Immediate16; return v is >= -32768 and <= 65535;
            case "imm8sex16": opKind = OpKind.Immediate8to16; score = 10; return !sym && (fits8s || (short)(ushort)(v & 0xFFFF) is >= -128 and <= 127 && v is >= 0 and <= 0xFFFF);
            case "imm8sex32": opKind = OpKind.Immediate8to32; score = 10; return !sym && fits32 && as32 is >= -128 and <= 127;
            case "imm8sex64": opKind = OpKind.Immediate8to64; score = 10; return !sym && fits8s;
            case "imm32": opKind = OpKind.Immediate32; return fits32;
            case "imm32sex64": opKind = OpKind.Immediate32to64; score = 5; return !(sym && bitness == 64) && v is >= int.MinValue and <= int.MaxValue;
            case "imm64": opKind = OpKind.Immediate64; score = -5; return true;
            default: return false;
        }
    }

    static void SetMemory(ref Instruction instr, Operand op, int bitness, ulong ip)
    {
        instr.SegmentPrefix = op.Segment;
        instr.MemoryIndexScale = op.Scale;
        instr.MemoryIndex = op.Index;
        if (op.Base == Register.None && op.Index == Register.None && bitness == 64 && op.Segment is Register.None)
        {
            // [symbol] in 64-bit code → RIP-relative (what CE emits)
            instr.MemoryBase = Register.RIP;
            instr.MemoryDisplacement64 = (ulong)op.Disp;
            instr.MemoryDisplSize = 8;
            return;
        }
        instr.MemoryBase = op.Base;
        instr.MemoryDisplacement64 = (ulong)op.Disp;
        // Iced wants the address size here: 8 for 64-bit addressing, 4 for 32-bit (1 = disp8 hint)
        int wide = bitness == 64 ? 8 : 4;
        if (op.Base == Register.None) instr.MemoryDisplSize = wide;
        else if (op.Disp == 0 && !op.DispSymbolic) instr.MemoryDisplSize = 0;
        else instr.MemoryDisplSize = !op.DispSymbolic && op.Disp is >= -128 and <= 127 ? 1 : wide;
    }

    /// <summary>Encodes an instruction at <paramref name="ip"/>.</summary>
    public static byte[] Encode(Instruction instr, int bitness, ulong ip)
    {
        var w = new ByteWriter();
        var enc = Encoder.Create(bitness, w);
        if (!enc.TryEncode(instr, ip, out _, out var error))
            throw new AsmException(error.Contains("Branch") || error.Contains("displacement") ? "Destino fora do alcance (±2 GB)" : error);
        return w.ToArray();
    }

    sealed class ByteWriter : CodeWriter
    {
        readonly List<byte> _bytes = new();
        public override void WriteByte(byte value) => _bytes.Add(value);
        public byte[] ToArray() => _bytes.ToArray();
    }
}
