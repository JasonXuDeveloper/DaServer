using System.Text;

namespace DaServer.Generator;

/// <summary>A field as its bytes hold it: in order, by name and how it is written.</summary>
internal readonly record struct WireField(string Name, string Type)
{
    public override string ToString() => Name + ": " + Type;
}

/// <summary>
/// A [NinoType] of an assembly as the wire manifest records it: how Nino writes it (<paramref name="Encoding"/>: its
/// header, format, whether it is raw memory, its type id), its members in the order its bytes hold them, for a request
/// its reply type, its [NinoType] ancestors and whether one of them is another assembly's (a new subtype of a recorded
/// or foreign type is a type id older builds lack).
/// </summary>
internal sealed record WireType(string Name, string Encoding, WireField[] Fields, string? Reply, string[] Ancestors, bool ForeignAncestor);

/// <summary>
/// Nino's own description of how it writes a [NinoType] (<c>[assembly: NinoTypeLayout]</c>, emitted by its generator for
/// every type it writes, read from source and from metadata alike).
/// </summary>
internal sealed record NinoLayout(INamedTypeSymbol Type, int Format, int Header, bool Track, bool Raw, int Id, string Members)
{
    public const string Attribute = "Nino.Core.NinoTypeLayoutAttribute";

    /// <summary>The layouts an assembly declares, by type (generic types by their definition).</summary>
    public static Dictionary<INamedTypeSymbol, NinoLayout> Of(IAssemblySymbol assembly)
    {
        var layouts = new Dictionary<INamedTypeSymbol, NinoLayout>(SymbolEqualityComparer.Default);
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!Support.Is(attribute.AttributeClass, Attribute)) continue;
            var a = attribute.ConstructorArguments;
            if (a.Length < 7 || a[0].Value is not INamedTypeSymbol type) continue;
            layouts[type.OriginalDefinition] = new NinoLayout(type.OriginalDefinition, (int)a[1].Value!, (int)a[2].Value!, (bool)a[3].Value!,
                (bool)a[4].Value!, (int)a[5].Value!, a[6].Value as string ?? "");
        }

        return layouts;
    }

    /// <summary>The whole layout on one line (how a foreign [NinoType] member is recorded inline).</summary>
    public override string ToString() => WireManifest.EncodingOf(this) + " {" + Members + "}";
}

/// <summary>
/// The wire manifest (ADR-076): the committed layout of every type an assembly puts on the wire or in records, so a
/// change a deployed peer or a stored record would misread fails the build. A positional layout is frozen (a new
/// field goes into a new message or component), a version-tolerant type takes new fields at its end, a keyed type
/// any key that keeps its type. What Nino writes comes from Nino itself (<see cref="NinoLayout"/>); the manifest adds
/// what Nino does not know: replies, ancestors, and the layout of another assembly's [NinoType] members.
/// </summary>
internal static class WireManifest
{
    public const string FileName = "wire.manifest";

    /// <summary>
    /// The layout text this manifest is written in (Nino's byte descriptions since 4.0.0-rc.6): a manifest recorded in
    /// another is reported once, not as a change in every member.
    /// </summary>
    public const string Grammar = "# Layout text: Nino byte descriptions (4.0.0-rc.6)";
    private const string Tolerant = "version tolerant", KeyedFormat = "keyed", EnumFormat = "enum";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    /// Every manifest entry of <paramref name="home"/> (the compilation's assembly for the analyzer, the built
    /// assembly read from metadata for the manifest's writer), with its symbol: one implementation for both, so they
    /// record the same types the same way.
    /// </summary>
    public static List<(WireType Type, INamedTypeSymbol Symbol)> All(IAssemblySymbol home, CancellationToken ct)
    {
        var layouts = new Layouts(home);
        var types = new List<(WireType, INamedTypeSymbol)>();
        foreach (var layout in layouts.Home.Values)
        {
            ct.ThrowIfCancellationRequested();
            types.Add((Read(layout, layouts), layout.Type));
        }

        // The enums those types hold: their bytes are numbers, what the numbers mean is the manifest's to keep.
        var enums = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var layout in layouts.Home.Values)
            foreach (var member in layout.Type.GetMembers())
                if (member is IFieldSymbol { IsStatic: false } f) EnumsIn(f.Type, enums, seen);
                else if (member is IPropertySymbol { IsStatic: false } p) EnumsIn(p.Type, enums, seen);
        foreach (var e in enums) types.Add((EnumOf(e), e));
        return types;
    }

    /// <summary>The enums <paramref name="type"/> holds: itself, its elements and type arguments, a plain struct's fields.</summary>
    private static void EnumsIn(ITypeSymbol type, HashSet<INamedTypeSymbol> enums, HashSet<ITypeSymbol> seen)
    {
        if (!seen.Add(type)) return;
        switch (type)
        {
            case IArrayTypeSymbol array:
                EnumsIn(array.ElementType, enums, seen);
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Enum } e when !Support.InSystemNamespace(e):
                enums.Add(e.OriginalDefinition);
                break;
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments) EnumsIn(argument, enums, seen);
                // A struct Nino writes as raw memory without a layout of its own: its fields are this type's bytes.
                if (named is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } && !Support.InSystemNamespace(named))
                    foreach (var member in named.GetMembers())
                        if (member is IFieldSymbol { IsStatic: false } f) EnumsIn(f.Type, enums, seen);
                break;
        }
    }

    /// <summary>An enum as the manifest records it: its underlying type and every member's value.</summary>
    private static WireType EnumOf(INamedTypeSymbol e)
    {
        var members = new List<WireField>();
        foreach (var member in e.GetMembers())
            if (member is IFieldSymbol { HasConstantValue: true } f)
                members.Add(new WireField(f.Name, Convert.ToString(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        return new WireType(Name(e), EnumFormat + " of " + e.EnumUnderlyingType!.ToDisplayString(), members.ToArray(), null, [], false);
    }

    /// <summary>The layouts of the home assembly and, on demand, of the assemblies its types refer to.</summary>
    private sealed class Layouts(IAssemblySymbol home)
    {
        private readonly Dictionary<IAssemblySymbol, Dictionary<INamedTypeSymbol, NinoLayout>> _byAssembly = new(SymbolEqualityComparer.Default);

        public IAssemblySymbol HomeAssembly { get; } = home;

        public Dictionary<INamedTypeSymbol, NinoLayout> Home => Of(HomeAssembly);

        public Dictionary<INamedTypeSymbol, NinoLayout> Of(IAssemblySymbol assembly)
        {
            if (!_byAssembly.TryGetValue(assembly, out var layouts)) _byAssembly[assembly] = layouts = NinoLayout.Of(assembly);
            return layouts;
        }

        public NinoLayout? Get(ITypeSymbol type) =>
            type is INamedTypeSymbol named && named.ContainingAssembly is { } assembly && Of(assembly).TryGetValue(named.OriginalDefinition, out var layout)
                ? layout
                : null;
    }

    private static WireType Read(NinoLayout layout, Layouts layouts)
    {
        var type = layout.Type;
        var fields = new List<WireField>();
        // A raw type is its memory: raw(size){name:type@offset:encoding,...}, every field (nested structs spelled out);
        // an empty one is just "raw".
        bool raw = layout.Members == "raw" || layout.Members.StartsWith("raw(", StringComparison.Ordinal);
        int open = layout.Members.IndexOf('{');
        IEnumerable<string> entries = !raw ? layout.Members.Split(';')
            : open < 0 ? new List<string>() : TopLevel(layout.Members.Substring(open + 1, layout.Members.Length - open - 2));
        foreach (string entry in entries)
        {
            if (entry.Length == 0) continue;
            // name:type:encoding[:key=0x..][:former=0x..,..], the encoding as the bytes read (seq(string), type(T), ...);
            // a [NinoType] collection's elements: [elements]::encoding
            var parts = entry.Split(new[] { ':' }, 3);
            string name = parts[0], written = parts.Length > 1 ? parts[1] : "", how = parts.Length > 2 ? parts[2] : "";
            string detail = Inline(MemberType(type, name), layouts, spelled: how.IndexOf('{') >= 0);
            fields.Add(new WireField(name, (written.Length > 0 ? written + " " : "") + how + detail));
        }

        string? reply = Shapes.ReplyOf(type) is { } r ? Name(r) + Inline(r, layouts) : null;
        var ancestors = new List<INamedTypeSymbol>();
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (layouts.Get(b) != null) ancestors.Add(b.OriginalDefinition);
        foreach (var i in type.AllInterfaces)
            if (layouts.Get(i) != null) ancestors.Add(i.OriginalDefinition);
        return new WireType(Name(type), EncodingOf(layout), fields.ToArray(), reply, ancestors.Select(Name).ToArray(),
            ancestors.Any(a => !SymbolEqualityComparer.Default.Equals(a.ContainingAssembly, layouts.HomeAssembly)));
    }

    /// <summary>The comma-separated parts of <paramref name="text"/> outside braces and parentheses.</summary>
    private static List<string> TopLevel(string text)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
            switch (text[i])
            {
                case '{' or '(': depth++; break;
                case '}' or ')': depth--; break;
                case ',' when depth == 0:
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                    break;
            }

        if (start < text.Length) parts.Add(text.Substring(start));
        return parts;
    }

    /// <summary>How Nino writes a type: its header, its format, raw memory or not, its type id (renames show).</summary>
    public static string EncodingOf(NinoLayout layout) =>
        (layout.Header switch { 1 => "null flag", 2 => "type id", _ => "no header" }) + ", " +
        (layout.Format switch { 1 => Tolerant, 2 => KeyedFormat, _ => "positional" }) +
        (layout.Raw ? ", raw" : "") + (layout.Track ? ", tracked" : "") + ", id " + layout.Id.ToString("X8");

    private static string Name(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    /// <summary>The type of <paramref name="type"/>'s member <paramref name="name"/> (a hidden base member: <c>Base.X</c>), or null.</summary>
    private static ITypeSymbol? MemberType(INamedTypeSymbol type, string name)
    {
        int dot = name.LastIndexOf('.');
        string member = dot < 0 ? name : name.Substring(dot + 1);
        for (var t = type; t != null; t = t.BaseType)
            if (dot < 0 || t.Name == name.Substring(0, dot))
                foreach (var symbol in t.GetMembers(member))
                    if (symbol is IFieldSymbol f) return f.Type;
                    else if (symbol is IPropertySymbol p) return p.Type;
        return null;
    }

    /// <summary>
    /// What Nino's layout leaves out of a member's bytes: another assembly's [NinoType] as its layout (a package update
    /// that changes it shows here); arrays and generic arguments element by element. Raw memory is Nino's to spell out
    /// (<c>raw(n){field:type@offset:encoding,...}</c>, nested structs and <c>[StructLayout]</c> included).
    /// </summary>
    /// <param name="spelled">Nino spelled the member's memory out: only the foreign type's encoding (its id) is added.</param>
    private static string Inline(ITypeSymbol? type, Layouts layouts, bool spelled = false)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Inline(array.ElementType, layouts, spelled);
            case INamedTypeSymbol { IsGenericType: true } generic when Support.InSystemNamespace(generic):
                return string.Concat(generic.TypeArguments.Select(t => Inline(t, layouts, spelled)));
            case INamedTypeSymbol named when named.SpecialType == SpecialType.None && !Support.InSystemNamespace(named):
                return layouts.Get(named) is { } layout && !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, layouts.HomeAssembly)
                    ? spelled ? " (" + EncodingOf(layout) + ")" : " " + layout
                    : "";
            default:
                return "";
        }
    }

    /// <summary>The manifest text of <paramref name="types"/>, sorted by name.</summary>
    public static string Format(IReadOnlyList<WireType> types)
    {
        var sb = new StringBuilder()
            .Append("# DaServer wire manifest (ADR-076): the layout of this assembly's [NinoType] types on the wire and in stored records.\n")
            .Append("# Positional layouts are frozen, version-tolerant types take new fields at their end, keyed types any key that keeps\n")
            .Append("# its type. Record changes: dotnet build -p:UpdateWireManifest=true\n")
            .Append(Grammar).Append('\n');
        foreach (var type in types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(type.Name).Append(" (").Append(type.Encoding).Append(")\n");
            foreach (var field in type.Fields) sb.Append("- ").Append(field).Append('\n');
            if (type.Reply != null) sb.Append("= ").Append(type.Reply).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>A type as a committed manifest records it, with the line it starts on.</summary>
    public sealed record Entry(string Name, string Encoding, List<string> Fields, int Line)
    {
        public string? Reply { get; set; }
    }

    public static Dictionary<string, Entry> Parse(string text)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        Entry? current = null;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("- ", StringComparison.Ordinal)) current?.Fields.Add(line.Substring(2));
            else if (line.StartsWith("= ", StringComparison.Ordinal)) { if (current != null) current.Reply = line.Substring(2); }
            else
            {
                int open = line.IndexOf(" (", StringComparison.Ordinal);
                string name = open < 0 ? line : line.Substring(0, open);
                string encoding = open < 0 ? "" : line.Substring(open + 2, line.Length - open - 2 - (line.EndsWith(")", StringComparison.Ordinal) ? 1 : 0));
                current = entries[name] = new Entry(name, encoding, [], i);
            }
        }

        return entries;
    }

    /// <summary>
    /// What changed against <paramref name="baseline"/>: (type, what, breaking). Breaking: a type gone, its encoding or
    /// reply changed, a new subtype of a recorded or foreign type, and a field change its format does not tolerate (any,
    /// positional; anything but fields appended, version tolerant; a key's type, keyed). Not breaking but not recorded
    /// yet: a new type, and the changes the format tolerates.
    /// </summary>
    public static IEnumerable<(string Type, string What, bool Breaking)> Compare(Dictionary<string, Entry> baseline, IReadOnlyList<WireType> current)
    {
        var byName = new Dictionary<string, WireType>(StringComparer.Ordinal);
        foreach (var type in current) byName[type.Name] = type; // last wins while duplicates do not compile anyway
        foreach (var entry in baseline.Values.OrderBy(e => e.Line))
        {
            if (!byName.TryGetValue(entry.Name, out var type))
                yield return (entry.Name, "is gone (removed or renamed): deployed peers and stored records still carry it; delete it from the manifest once no deployed version uses it", true);
            else if (entry.Encoding != type.Encoding)
                yield return (type.Name, $"was written as {entry.Encoding} and is now {type.Encoding}", true);
            else if (entry.Reply != type.Reply)
                yield return (type.Name, $"replied '{entry.Reply ?? "nothing"}' and now replies '{type.Reply ?? "nothing"}': callers of the other build misread it", true);
            else if (Fields(entry, type) is { } change)
                yield return (type.Name, change.What, change.Breaking);
        }

        foreach (var type in current)
        {
            if (baseline.ContainsKey(type.Name)) continue;
            string? recorded = type.Ancestors.FirstOrDefault(baseline.ContainsKey) ?? (type.ForeignAncestor ? type.Ancestors[type.Ancestors.Length - 1] : null);
            yield return recorded != null
                ? (type.Name, $"is a new subtype of '{recorded}': builds that lack it cannot read its type id where a '{recorded}' is expected", true)
                : (type.Name, "is new", false);
        }
    }

    /// <summary>How the fields changed, judged by the format both builds write (equal encodings); null when they did not.</summary>
    private static (string What, bool Breaking)? Fields(Entry entry, WireType type)
    {
        var now = type.Fields.Select(f => f.ToString()).ToList();
        if (entry.Encoding.StartsWith(EnumFormat + " ", StringComparison.Ordinal))
        {
            // Stored records and deployed peers keep the numbers: a member gone or renumbered changes what they mean.
            var members = new HashSet<string>(now, StringComparer.Ordinal);
            foreach (string member in entry.Fields)
                if (!members.Contains(member))
                    return ($"member '{member}' is gone or renumbered: stored records and deployed peers still mean it by its number (a new member takes a new number)", true);
            return now.Count == entry.Fields.Count ? null : ($"has new members ({string.Join(", ", now.Where(m => !entry.Fields.Contains(m)))})", false);
        }

        if (entry.Encoding.Contains(KeyedFormat))
        {
            // A key's value is read by its type: another type under the same key is misread, any other change is skipped or
            // defaulted. Members are matched by key (Nino's :key=0x..), a renamed one by its former keys too.
            var before = Keys(entry.Fields, formers: false);
            var after = Keys(now, formers: true);
            foreach (var key in before)
                if (after.TryGetValue(key.Key, out var member) && member.Type != key.Value.Type)
                    return ($"'{key.Value.Name}' ({key.Key}) was '{key.Value.Type}' and '{member.Name}' is '{member.Type}': the other build misreads it (a new type takes a new key)", true);
            var kept = new HashSet<string>(before.Keys.Where(after.ContainsKey).Select(k => after[k].Name), StringComparer.Ordinal);
            var added = now.Select(f => Keyed(f).Name).Where(n => !kept.Contains(n)).ToList();
            var removed = before.Where(k => !after.ContainsKey(k.Key)).Select(k => k.Value.Name).ToList();
            return added.Count == 0 && removed.Count == 0 ? null
                : ($"has keys added ({string.Join(", ", added)}) or removed ({string.Join(", ", removed)})", false);
        }

        int changed = -1;
        for (int i = 0; i < Math.Max(entry.Fields.Count, now.Count) && changed < 0; i++)
            if (i >= entry.Fields.Count || i >= now.Count || entry.Fields[i] != now[i]) changed = i;
        if (changed < 0) return null;
        bool tolerant = entry.Encoding.Contains(Tolerant);
        if (tolerant && changed == entry.Fields.Count) return ($"has new fields at its end ({string.Join(", ", now.Skip(changed))})", false);
        return ($"field {changed + 1} was '{(changed < entry.Fields.Count ? entry.Fields[changed] : "nothing")}' and is '{(changed < now.Count ? now[changed] : "nothing")}': " +
                (tolerant
                    ? "a version-tolerant type only takes new fields at its end (of its own: one appended to a base shifts its subtypes')"
                    : "a positional layout is frozen, a new field goes into a new message or component"), true);
    }

    /// <summary>A keyed member's field text: its name, key, former keys, and type (name and keys left out).</summary>
    private static (string Name, string Key, string[] Formers, string Type) Keyed(string field)
    {
        int colon = field.IndexOf(": ", StringComparison.Ordinal);
        string name = colon < 0 ? field : field.Substring(0, colon), rest = colon < 0 ? "" : field.Substring(colon + 2);
        string key = name;
        var formers = Array.Empty<string>();
        int at = rest.IndexOf(":key=", StringComparison.Ordinal);
        if (at >= 0)
        {
            var tags = rest.Substring(at + 1).Split(':');
            rest = rest.Substring(0, at);
            foreach (string tag in tags)
                if (tag.StartsWith("key=", StringComparison.Ordinal)) key = tag.Substring(4);
                else if (tag.StartsWith("former=", StringComparison.Ordinal)) formers = tag.Substring(7).Split(',');
        }

        return (name, key, formers, rest);
    }

    /// <summary>Members by key (and, with <paramref name="formers"/>, by each former key too).</summary>
    private static Dictionary<string, (string Name, string Type)> Keys(IEnumerable<string> fields, bool formers)
    {
        var keys = new Dictionary<string, (string Name, string Type)>(StringComparer.Ordinal);
        foreach (string field in fields)
        {
            var member = Keyed(field);
            keys[member.Key] = (member.Name, member.Type);
            if (formers)
                foreach (string former in member.Formers) keys[former] = (member.Name, member.Type);
        }

        return keys;
    }
}
