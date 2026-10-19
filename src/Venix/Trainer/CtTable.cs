using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Venix.Trainer;

/// <summary>One &lt;CheatEntry&gt; of a Cheat Engine table.</summary>
public class CtEntry
{
    public int Id { get; init; }
    public string Description { get; init; } = "";
    public string VariableType { get; init; } = "";
    public string? Address { get; init; }
    /// <summary>Pointer offsets in XML order (CE stores the last-applied offset first).</summary>
    public List<string> Offsets { get; init; } = new();
    public string? Script { get; init; }
    public int Length { get; init; }
    public bool Unicode { get; init; }
    public int ByteLength { get; init; }
    public int BitStart { get; init; }
    public int BitLength { get; init; }
    public bool ShowAsHex { get; init; }
    public bool ShowAsSigned { get; init; }
    public bool IsGroupHeader { get; init; }
    public bool ActivateChildren { get; init; }
    public List<(string Value, string Label)> DropDown { get; init; } = new();
    public List<CtEntry> Children { get; } = new();
    public CtEntry? Parent { get; set; }
    public int Depth { get; set; }

    public bool IsScript => VariableType.Equals("Auto Assembler Script", StringComparison.OrdinalIgnoreCase) && Script != null;
    public bool HasAddress => !string.IsNullOrWhiteSpace(Address) && !IsScript;
    public bool IsValue => HasAddress && ValueKind != ValueKind.Unsupported;

    public ValueKind ValueKind => VariableType.ToLowerInvariant() switch
    {
        "byte" => ValueKind.Byte,
        "2 bytes" => ValueKind.Int16,
        "4 bytes" => ValueKind.Int32,
        "8 bytes" => ValueKind.Int64,
        "float" => ValueKind.Float,
        "double" => ValueKind.Double,
        "string" => ValueKind.String,
        "array of byte" => ValueKind.Bytes,
        "binary" => ValueKind.Binary,
        _ => ValueKind.Unsupported
    };

    /// <summary>Why this entry cannot run inside Venix (null = supported).</summary>
    public string? UnsupportedReason
    {
        get
        {
            if (IsScript) return AutoAssembler.Unsupported(Script!);
            if (HasAddress && ValueKind == ValueKind.Unsupported) return $"Tipo \"{VariableType}\" não suportado";
            return null;
        }
    }
}

public enum ValueKind { Byte, Int16, Int32, Int64, Float, Double, String, Bytes, Binary, Unsupported }

/// <summary>Parsed .CT file.</summary>
public class CtTable
{
    public List<CtEntry> Entries { get; } = new();
    public List<(string Name, string Address)> UserSymbols { get; } = new();
    public bool HasTableLua { get; private set; }
    public string? ProcessHint { get; private set; }

    public IEnumerable<CtEntry> Flatten()
    {
        IEnumerable<CtEntry> Walk(IEnumerable<CtEntry> list)
        {
            foreach (var e in list)
            {
                yield return e;
                foreach (var c in Walk(e.Children)) yield return c;
            }
        }
        return Walk(Entries);
    }

    public static CtTable Parse(string xml)
    {
        var t = new CtTable();
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        t.HasTableLua = !string.IsNullOrWhiteSpace(root.Element("LuaScript")?.Value);
        foreach (var s in root.Element("UserdefinedSymbols")?.Elements("SymbolEntry") ?? Enumerable.Empty<XElement>())
        {
            var n = s.Element("Name")?.Value;
            var a = s.Element("Address")?.Value;
            if (!string.IsNullOrWhiteSpace(n) && !string.IsNullOrWhiteSpace(a)) t.UserSymbols.Add((n.Trim(), a.Trim()));
        }
        ReadEntries(root.Element("CheatEntries"), t.Entries, null, 0);

        // process name: "Game.exe" used in addresses / aobscanmodule is the most reliable hint
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(xml, @"[""'\s,(\[]([\w\-. ]{1,60}?\.exe)\b", RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value.Trim();
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        t.ProcessHint = counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        return t;
    }

    static void ReadEntries(XElement? parent, List<CtEntry> into, CtEntry? owner, int depth)
    {
        if (parent == null) return;
        foreach (var x in parent.Elements("CheatEntry"))
        {
            string? Val(string n) => x.Element(n)?.Value;
            bool Flag(string n) => Val(n) == "1";
            var options = x.Element("Options");
            var dropdown = new List<(string, string)>();
            foreach (var line in (Val("DropDownList") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var i = line.IndexOf(':');
                if (i > 0) dropdown.Add((line[..i].Trim(), line[(i + 1)..].Trim()));
            }
            var e = new CtEntry
            {
                Id = int.TryParse(Val("ID"), out var id) ? id : 0,
                Description = (Val("Description") ?? "").Trim().Trim('"').Trim(),
                VariableType = (Val("VariableType") ?? "").Trim(),
                Address = Val("Address")?.Trim(),
                Offsets = x.Element("Offsets")?.Elements("Offset").Select(o => o.Value.Trim()).ToList() ?? new(),
                Script = Val("AssemblerScript"),
                Length = int.TryParse(Val("Length"), out var len) ? len : 0,
                Unicode = Flag("Unicode"),
                ByteLength = int.TryParse(Val("ByteLength"), out var bl) ? bl : 0,
                BitStart = int.TryParse(Val("BitStart"), out var bs) ? bs : 0,
                BitLength = int.TryParse(Val("BitLength"), out var bln) ? bln : 1,
                ShowAsHex = Flag("ShowAsHex"),
                ShowAsSigned = Flag("ShowAsSigned"),
                IsGroupHeader = Flag("GroupHeader"),
                ActivateChildren = options?.Attribute("moActivateChildrenAsWell")?.Value == "1",
                DropDown = dropdown,
                Parent = owner,
                Depth = depth
            };
            into.Add(e);
            ReadEntries(x.Element("CheatEntries"), e.Children, e, depth + 1);
        }
    }
}
