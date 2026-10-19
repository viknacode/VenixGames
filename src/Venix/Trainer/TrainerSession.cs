using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Venix.Core;

namespace Venix.Trainer;

public enum EntryKind { Group, Script, Value, Unsupported }

/// <summary>View model of one table entry inside a running trainer.</summary>
public class TrainerEntry : Observable
{
    bool _active, _expanded = true, _visible = true, _busy;
    string _value = "";
    string? _error;

    public TrainerEntry(CtEntry entry, TrainerEntry? parent)
    {
        Entry = entry;
        Parent = parent;
        Kind = entry.UnsupportedReason != null ? EntryKind.Unsupported
             : entry.IsScript ? EntryKind.Script
             : entry.IsValue ? EntryKind.Value
             : EntryKind.Group;
    }

    public CtEntry Entry { get; }
    public TrainerEntry? Parent { get; }
    public List<TrainerEntry> Children { get; } = new();
    public EntryKind Kind { get; }
    public string Description => string.IsNullOrWhiteSpace(Entry.Description) ? "(sem nome)" : Entry.Description;
    public Thickness Indent => new(Entry.Depth * 22, 0, 0, 0);
    public bool HasChildren => Children.Count > 0;
    public bool IsScript => Kind == EntryKind.Script;
    public bool IsValue => Kind == EntryKind.Value;
    public bool IsGroupOnly => Kind == EntryKind.Group;
    public bool IsUnsupported => Kind == EntryKind.Unsupported;
    public bool HasDropDown => IsValue && Entry.DropDown.Count > 0;
    public List<string> Options => Entry.DropDown.Select(d => d.Label).ToList();
    public bool CanToggle => IsScript || IsValue;
    public string TypeLabel => Kind switch
    {
        EntryKind.Script => "Script",
        EntryKind.Value => Entry.VariableType,
        EntryKind.Unsupported => Entry.UnsupportedReason ?? "",
        _ => HasChildren ? $"{Children.Count} itens" : ""
    };

    public bool IsActive { get => _active; set { if (Set(ref _active, value)) Raise(nameof(StatusText)); } }
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(StatusText)); } }
    public bool Expanded { get => _expanded; set { if (Set(ref _expanded, value)) Raise(nameof(Chevron)); } }
    public string Chevron => Expanded ? "" : "";
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string? Error { get => _error; set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StatusText)); } } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string StatusText => IsBusy ? "Aplicando…" : HasError ? "Erro" : IsActive ? (IsValue ? "Congelado" : "Ativo") : "";

    /// <summary>Bytes kept in memory while frozen.</summary>
    public byte[]? Frozen { get; set; }

    /// <summary>Profile record behind this entry when it was discovered with the scanner (null for .ct entries).</summary>
    public SavedCheat? Saved { get; set; }
    public bool IsDiscovered => Saved != null;
    /// <summary>How the address was found on the last attach ("ponteiro", "estático"...), for discovered cheats.</summary>
    public string ResolvedHow { get => _how; set { if (Set(ref _how, value)) Raise(nameof(HowLabel)); } }
    string _how = "";
    public string HowLabel => Saved == null ? "" : ResolvedHow.Length > 0 ? ResolvedHow : Saved.HowLabel;
    /// <summary>Discovered cheats that survive a game restart (pointer chain / static / signature).</summary>
    public bool IsPersistent => Saved?.IsPersistent == true;
}

/// <summary>
/// A cheat table running against a game: attaches to the process, keeps values live,
/// freezes them, and enables/disables Auto Assembler scripts through <see cref="AutoAssembler"/>.
/// </summary>
public class TrainerSession : Observable, IDisposable
{
    readonly SymbolTable _globals = new();
    readonly Dictionary<CtEntry, ScriptState> _states = new();
    readonly object _lock = new();
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly System.Threading.Timer _freezer;
    GameProcess? _game;
    string _status = "Aguardando o jogo…";

    public string Name { get; }
    public string? TablePath { get; }
    public CtTable Table { get; }
    public string? ProcessHint { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<TrainerEntry> Entries { get; } = new();
    public GameProcess? Game => _game;
    public bool IsAttached => _game != null && !_game.HasExited;
    public string Status { get => _status; private set => Set(ref _status, value); }
    /// <summary>Scanner profile this session hosts (cheats discovered by the user), null for plain .ct sessions.</summary>
    public CheatProfile? Profile { get; }
    public string? ProfileWarning { get => _profileWarning; private set => Set(ref _profileWarning, value); }
    string? _profileWarning;
    /// <summary>Symbols visible to address expressions ("registersymbol", discovered cheats, user symbols).</summary>
    public SymbolTable Symbols => _globals;
    int _nextId = 100000;

    public event Action? AttachedChanged;
    /// <summary>Raised on the UI thread after a discovered cheat is added or removed.</summary>
    public event Action? EntriesChanged;

    public TrainerSession(string name, string? tablePath, CtTable table, string? processHint, CheatProfile? profile = null)
    {
        Name = name;
        TablePath = tablePath;
        Table = table;
        Profile = profile;
        ProcessHint = processHint ?? table.ProcessHint ?? profile?.Exe;
        void Add(IEnumerable<CtEntry> list, TrainerEntry? parent)
        {
            foreach (var e in list)
            {
                var vm = new TrainerEntry(e, parent);
                Entries.Add(vm);
                parent?.Children.Add(vm);
                Add(e.Children, vm);
            }
        }
        Add(table.Entries, null);
        if (profile != null)
            foreach (var c in profile.Cheats) AddSavedEntry(c);
        _refresh.Tick += (_, _) => Tick();
        _refresh.Start();
        _freezer = new System.Threading.Timer(_ => FreezeTick(), null, 50, 50);
    }

    /* ---------------------------------------------------------- discovered cheats (scanner) */

    TrainerEntry AddSavedEntry(SavedCheat c)
    {
        var entry = c.ToEntry(_nextId++, "venix_" + c.Id);
        var vm = new TrainerEntry(entry, null) { Saved = c };
        Entries.Add(vm);
        return vm;
    }

    /// <summary>Adds a cheat found by the scanner to this session (and to the profile on disk when there is one).</summary>
    public TrainerEntry AddDiscovered(SavedCheat c, long? liveAddress)
    {
        if (Profile != null && !Profile.Cheats.Contains(c)) Profile.Cheats.Add(c);
        var vm = AddSavedEntry(c);
        if (liveAddress is { } a)
        {
            _globals.Set("venix_" + c.Id, a);
            vm.ResolvedHow = c.IsPersistent ? "" : "endereço desta execução";
        }
        SaveProfile();
        EntriesChanged?.Invoke();
        return vm;
    }

    public void RemoveDiscovered(TrainerEntry vm)
    {
        if (vm.Saved == null) return;
        vm.IsActive = false;
        vm.Frozen = null;
        Entries.Remove(vm);
        Profile?.Cheats.Remove(vm.Saved);
        _globals.Remove("venix_" + vm.Saved.Id);
        SaveProfile();
        EntriesChanged?.Invoke();
    }

    /// <summary>Re-saves the profile after a change in a cheat (name, pointer chains, value).</summary>
    public void SaveProfile()
    {
        if (Profile == null) return;
        if (_game != null && !_game.HasExited) Profile.StampExe(_game);
        try { CheatProfiles.Save(Profile); } catch { }
    }

    /// <summary>Discovered cheats whose address is known right now.</summary>
    public IEnumerable<TrainerEntry> ResolvedDiscovered => Entries.Where(e => e.Saved != null && Resolve(e.Entry) != null);

    /// <summary>Resolves every discovered cheat against the attached game (pointer chains first) and registers its symbol.</summary>
    void ResolveDiscovered()
    {
        if (_game == null) return;
        foreach (var vm in Entries)
        {
            if (vm.Saved == null) continue;
            var addr = vm.Saved.Resolve(_game, out var how);
            if (addr is { } a)
            {
                _globals.Set("venix_" + vm.Saved.Id, a);
                vm.Saved.LastAddress = a.ToString("X");
                vm.ResolvedHow = how;
                vm.Error = null;
            }
            else
            {
                _globals.Remove("venix_" + vm.Saved.Id);
                vm.ResolvedHow = "";
                vm.Error = vm.Saved.IsPersistent ? "Não encontrado nesta execução (o jogo mudou?). Refaça a busca." : "Endereço de outra execução. Refaça a busca.";
            }
        }
        if (Profile != null) ProfileWarning = Profile.VersionWarning(_game);
    }

    /// <summary>Activates (writes the saved value and freezes) every discovered cheat that resolved. Returns how many.</summary>
    public async Task<int> ActivateDiscoveredAsync()
    {
        int n = 0;
        foreach (var vm in Entries.Where(e => e.Saved != null).ToList())
        {
            if (Resolve(vm.Entry) == null) continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(vm.Saved!.Value)) WriteValue(vm, vm.Saved.Value);
                await SetActiveAsync(vm, true);
                if (vm.IsActive) n++;
            }
            catch (Exception ex) { vm.Error = ex.Message; }
        }
        return n;
    }

    /* ---------------------------------------------------------- attach */

    /// <summary>Running processes that look like the table's game (by the .exe name found in the table).</summary>
    public Process? FindTargetProcess()
    {
        if (string.IsNullOrWhiteSpace(ProcessHint)) return null;
        var name = System.IO.Path.GetFileNameWithoutExtension(ProcessHint);
        return Process.GetProcessesByName(name).FirstOrDefault(p => { try { return !p.HasExited; } catch { return false; } });
    }

    public void Attach(Process process)
    {
        Detach();
        var gp = GameProcess.Attach(process);
        _game = gp;
        foreach (var (name, addr) in Table.UserSymbols)
        {
            try
            {
                var v = new AddressExpr(gp, n => _globals.TryGet(n, out var x) ? x : null).Eval(addr);
                if (v != null) _globals.Set(name, v.Value);
            }
            catch { }
        }
        ResolveDiscovered();
        Status = $"{process.ProcessName}.exe  ·  PID {process.Id}  ·  {gp.Bitness}-bit";
        AttachedChanged?.Invoke();
        Raise(nameof(IsAttached));
    }

    public void Detach(bool disableScripts = true)
    {
        if (_game == null) return;
        if (disableScripts && !_game.HasExited)
            foreach (var e in Entries.Where(e => e.IsScript && e.IsActive).Reverse())
                try { RunScript(e, false); } catch { }
        foreach (var e in Entries) { e.IsActive = false; e.Frozen = null; e.Error = null; e.Value = ""; e.ResolvedHow = ""; if (e.Saved != null) _globals.Remove("venix_" + e.Saved.Id); }
        _states.Clear();
        _game.Dispose();
        _game = null;
        ProfileWarning = null;
        Status = "Aguardando o jogo…";
        AttachedChanged?.Invoke();
        Raise(nameof(IsAttached));
    }

    /* ---------------------------------------------------------- scripts */

    public async Task SetActiveAsync(TrainerEntry e, bool on)
    {
        if (_game == null) { e.Error = "Abra o jogo primeiro"; e.IsActive = false; return; }
        if (e.IsValue)
        {
            if (on)
            {
                var addr = Resolve(e.Entry);
                var size = ValueSize(e.Entry);
                e.Frozen = addr != null ? _game.TryRead((nint)addr.Value, size) : null;
                if (e.Frozen == null) { e.Error = "Endereço não disponível (ative o script do grupo primeiro)"; e.IsActive = false; return; }
            }
            else e.Frozen = null;
            e.Error = null;
            e.IsActive = on;
            return;
        }
        if (!e.IsScript) return;
        if (e.IsActive == on) return;
        e.IsBusy = true;
        e.Error = null;
        try
        {
            await Task.Run(() => RunScript(e, on));
            e.IsActive = on;
            if (on && e.Entry.ActivateChildren)
                foreach (var c in e.Children.Where(c => c.IsScript || c.IsValue))
                    await SetActiveAsync(c, true);
            if (!on)
                foreach (var c in e.Children.Where(c => c.IsActive))
                    await SetActiveAsync(c, false);
        }
        catch (Exception ex)
        {
            e.Error = ex.Message;
            e.IsActive = false;
        }
        finally { e.IsBusy = false; }
    }

    void RunScript(TrainerEntry e, bool enable)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(e.Entry, out var st)) _states[e.Entry] = st = new ScriptState();
            new AutoAssembler(_game!, _globals).Run(e.Entry.Script!, enable, st);
        }
    }

    /* ---------------------------------------------------------- values */

    long? Lookup(string name) => _globals.TryGet(name, out var v) ? v : null;

    public long? Resolve(CtEntry e)
    {
        if (_game == null || string.IsNullOrWhiteSpace(e.Address)) return null;
        try
        {
            var expr = new AddressExpr(_game, Lookup);
            long? addr;
            var a = e.Address!.Trim();
            // "+10" is relative to the parent entry's address
            if ((a.StartsWith('+') || a.StartsWith('-')) && e.Parent != null)
            {
                var parent = Resolve(e.Parent);
                var off = expr.Eval(a.TrimStart('+'));
                addr = parent != null && off != null ? parent + (a.StartsWith('-') ? -Math.Abs(off.Value) : off) : null;
            }
            else addr = expr.Eval(a);
            if (addr == null) return null;
            for (int i = e.Offsets.Count - 1; i >= 0; i--)
            {
                if (!_game.TryReadPointer((nint)addr.Value, out var p) || p == 0) return null;
                var off = expr.Eval(e.Offsets[i]);
                if (off == null) return null;
                addr = p + off.Value;
            }
            return addr;
        }
        catch { return null; }
    }

    static int ValueSize(CtEntry e) => e.ValueKind switch
    {
        ValueKind.Byte => 1, ValueKind.Int16 => 2, ValueKind.Int32 or ValueKind.Float => 4,
        ValueKind.Int64 or ValueKind.Double => 8,
        ValueKind.String => Math.Max(1, e.Length) * (e.Unicode ? 2 : 1),
        ValueKind.Bytes => Math.Max(1, e.ByteLength),
        ValueKind.Binary => (e.BitStart + e.BitLength + 7) / 8,
        _ => 4
    };

    string Format(CtEntry e, byte[] b)
    {
        string Int(long signedV, ulong unsignedV) => e.ShowAsHex ? unsignedV.ToString("X") : e.ShowAsSigned ? signedV.ToString(CultureInfo.InvariantCulture) : signedV.ToString(CultureInfo.InvariantCulture);
        var text = e.ValueKind switch
        {
            ValueKind.Byte => Int((sbyte)b[0], b[0]),
            ValueKind.Int16 => Int(BitConverter.ToInt16(b), BitConverter.ToUInt16(b)),
            ValueKind.Int32 => Int(BitConverter.ToInt32(b), BitConverter.ToUInt32(b)),
            ValueKind.Int64 => Int(BitConverter.ToInt64(b), BitConverter.ToUInt64(b)),
            ValueKind.Float => BitConverter.ToSingle(b).ToString("0.###", CultureInfo.InvariantCulture),
            ValueKind.Double => BitConverter.ToDouble(b).ToString("0.###", CultureInfo.InvariantCulture),
            ValueKind.String => (e.Unicode ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b)).Split('\0')[0],
            ValueKind.Bytes => string.Join(" ", b.Select(x => x.ToString("X2"))),
            ValueKind.Binary => ((BitConverter.ToUInt64(b.Concat(new byte[8]).Take(8).ToArray()) >> e.BitStart) & ((1UL << e.BitLength) - 1)).ToString(),
            _ => ""
        };
        var dd = e.DropDown.FirstOrDefault(d => d.Value == text);
        return dd.Label != null ? $"{text} · {dd.Label}" : text;
    }

    public void WriteValue(TrainerEntry vm, string input)
    {
        var e = vm.Entry;
        if (_game == null) throw new InvalidOperationException("Abra o jogo primeiro.");
        var addr = Resolve(e) ?? throw new InvalidOperationException("Endereço não disponível (ative o script do grupo primeiro).");
        input = input.Trim();
        var dd = e.DropDown.FirstOrDefault(d => d.Label.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (dd.Value != null) input = dd.Value;
        long ParseInt(string s) => e.ShowAsHex || s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber)
            : long.Parse(s, CultureInfo.InvariantCulture);
        double ParseFloat(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        byte[] bytes = e.ValueKind switch
        {
            ValueKind.Byte => new[] { (byte)ParseInt(input) },
            ValueKind.Int16 => BitConverter.GetBytes((short)ParseInt(input)),
            ValueKind.Int32 => BitConverter.GetBytes((int)ParseInt(input)),
            ValueKind.Int64 => BitConverter.GetBytes(ParseInt(input)),
            ValueKind.Float => BitConverter.GetBytes((float)ParseFloat(input)),
            ValueKind.Double => BitConverter.GetBytes(ParseFloat(input)),
            ValueKind.String => (e.Unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(input),
            ValueKind.Bytes => input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToByte(x, 16)).ToArray(),
            ValueKind.Binary => BinaryWrite(e, (nint)addr, ParseInt(input)),
            _ => throw new InvalidOperationException("Tipo não suportado")
        };
        _game.Write((nint)addr, bytes);
        if (vm.IsActive) vm.Frozen = bytes; // keep the new value frozen
        vm.Value = Format(e, bytes);
        if (vm.Saved != null && vm.Saved.Value != input) { vm.Saved.Value = input; SaveProfile(); }
    }

    /// <summary>Current raw bytes of a value entry, or null when its address is unknown right now.</summary>
    public byte[]? ReadBytes(TrainerEntry vm)
    {
        var addr = Resolve(vm.Entry);
        return addr != null && _game != null ? _game.TryRead((nint)addr.Value, ValueSize(vm.Entry)) : null;
    }

    public long? Address(TrainerEntry vm) => Resolve(vm.Entry);

    byte[] BinaryWrite(CtEntry e, nint addr, long value)
    {
        var size = ValueSize(e);
        var cur = _game!.Read(addr, size).Concat(new byte[8]).Take(8).ToArray();
        var v = BitConverter.ToUInt64(cur);
        var mask = ((1UL << e.BitLength) - 1) << e.BitStart;
        v = (v & ~mask) | (((ulong)value << e.BitStart) & mask);
        return BitConverter.GetBytes(v).Take(size).ToArray();
    }

    /* ---------------------------------------------------------- timers */

    void Tick()
    {
        if (_game == null) return;
        if (_game.HasExited) { Detach(disableScripts: false); return; }
        foreach (var e in Entries)
        {
            if (!e.IsValue || !e.Visible) continue;
            var addr = Resolve(e.Entry);
            var b = addr != null ? _game.TryRead((nint)addr.Value, ValueSize(e.Entry)) : null;
            e.Value = b != null ? Format(e.Entry, b) : "??";
        }
    }

    void FreezeTick()
    {
        var game = _game;
        if (game == null) return;
        foreach (var e in Entries)
        {
            var frozen = e.Frozen;
            if (frozen == null || !e.IsActive) continue;
            var addr = Resolve(e.Entry);
            try { if (addr != null) game.TryWrite((nint)addr.Value, frozen); } catch { }
        }
    }

    public void Dispose()
    {
        _refresh.Stop();
        _freezer.Dispose();
        Detach();
    }
}
