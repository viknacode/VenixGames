using System.Globalization;

namespace Venix.Trainer;

/// <summary>Case-insensitive symbol table ("registersymbol", user-defined symbols, labels).</summary>
public class SymbolTable
{
    readonly Dictionary<string, long> _map = new(StringComparer.OrdinalIgnoreCase);
    public bool TryGet(string name, out long value) => _map.TryGetValue(name, out value);
    public void Set(string name, long value) => _map[name] = value;
    public void Remove(string name) => _map.Remove(name);
    public bool Contains(string name) => _map.ContainsKey(name);
    public IEnumerable<KeyValuePair<string, long>> All => _map;
}

/// <summary>
/// Evaluates Cheat Engine address expressions:
/// "Game.exe+1A2B", "[[\"Game.exe\"+10]+8]+4", "pPlayer+10", "#100" (decimal), "$process".
/// Plain numbers are hexadecimal, like in CE. Returns null when a symbol is not known yet.
/// </summary>
public class AddressExpr
{
    readonly GameProcess _gp;
    readonly Func<string, long?> _lookup;
    string _s = "";
    int _i;
    bool _unresolved;

    public AddressExpr(GameProcess gp, Func<string, long?> lookup)
    {
        _gp = gp;
        _lookup = lookup;
    }

    public long? Eval(string text)
    {
        _s = text.Trim();
        _i = 0;
        _unresolved = false;
        if (_s.Length == 0) return null;
        var v = Expr();
        Skip();
        if (_i < _s.Length) throw new FormatException($"Endereço inválido: \"{text}\"");
        return _unresolved ? null : v;
    }

    void Skip() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }
    bool Peek(char c) { Skip(); return _i < _s.Length && _s[_i] == c; }

    long Expr()
    {
        var v = Term();
        while (true)
        {
            if (Peek('+')) { _i++; v += Term(); }
            else if (Peek('-')) { _i++; v -= Term(); }
            else return v;
        }
    }

    long Term()
    {
        var v = Factor();
        while (Peek('*')) { _i++; v *= Factor(); }
        return v;
    }

    long Factor()
    {
        Skip();
        if (_i >= _s.Length) throw new FormatException("Expressão incompleta");
        var c = _s[_i];
        if (c == '[')
        {
            _i++;
            var inner = Expr();
            if (!Peek(']')) throw new FormatException("Falta ']'");
            _i++;
            if (_unresolved) return 0;
            if (!_gp.TryReadPointer((nint)inner, out var p)) { _unresolved = true; return 0; }
            return p;
        }
        if (c == '(')
        {
            _i++;
            var inner = Expr();
            if (!Peek(')')) throw new FormatException("Falta ')'");
            _i++;
            return inner;
        }
        if (c == '-') { _i++; return -Factor(); }
        if (c == '"' || c == '\'')
        {
            var end = _s.IndexOf(c, _i + 1);
            if (end < 0) throw new FormatException("Aspas sem fechamento");
            var name = _s[(_i + 1)..end];
            _i = end + 1;
            return Atom(name);
        }
        // module names may contain '-' or spaces ("Tutorial-x86_64.exe"): match them before splitting on operators
        foreach (var m in _gp.Modules.OrderByDescending(m => m.Name.Length))
        {
            if (string.Compare(_s, _i, m.Name, 0, m.Name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            int after = _i + m.Name.Length;
            if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_')) continue;
            _i = after;
            return m.Base;
        }
        int start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '.' or '$' or '#' or '@' or '?' or '!')) _i++;
        if (_i == start) throw new FormatException($"Caractere inesperado '{c}'");
        return Atom(_s[start.._i]);
    }

    long Atom(string token)
    {
        if (token.StartsWith('#') && long.TryParse(token[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)) return dec;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(token[2..], NumberStyles.HexNumber, null, out var hx)) return hx;
        if (token.StartsWith('$') && token.Length > 1 && token != "$process" && long.TryParse(token[1..], NumberStyles.HexNumber, null, out var hd)) return hd;

        var sym = _lookup(token);
        if (sym is { } s) return s;
        if (token.Equals("$process", StringComparison.OrdinalIgnoreCase)) return _gp.MainModule.Base;
        var mod = _gp.FindModule(token);
        if (mod != null) return mod.Base;
        if (IsHex(token) && long.TryParse(token, NumberStyles.HexNumber, null, out var h)) return h;
        _unresolved = true;
        return 0;
    }

    public static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);
}
