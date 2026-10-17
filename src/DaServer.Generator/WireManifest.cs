using System.Collections.Immutable;
using System.Text;

namespace DaServer.Generator;

/// <summary>A field as its bytes hold it: in order, by name and wire type.</summary>
internal readonly record struct WireField(string Name, string Type)
{
    public override string ToString() => Name + ": " + Type;
}

/// <summary>
/// A [NinoType] type of this assembly as the wire manifest records it: how Nino encodes it (<paramref name="Encoding"/>:
/// its raw memory, or its header and body format), its fields in the order its bytes hold them, for a request its reply
/// type, its [NinoType] ancestors and whether one of them is another assembly's (a new subtype of a recorded or foreign
/// type is a type id older builds lack).
/// </summary>
internal sealed record WireType(string Name, string Encoding, EquatableArray<WireField> Fields, string? Reply,
    EquatableArray<string> Ancestors, bool ForeignAncestor);

/// <summary>
/// The wire manifest (ADR-076): the committed layout of every type an assembly puts on the wire or in records, so a
/// change a deployed peer or a stored record would misread fails the build. A positional layout is frozen (a new
/// field goes into a new message or component), a version-tolerant type takes new fields at its end, a keyed type
/// any key that keeps its type. How Nino writes a type is mirrored in <see cref="WireLayout"/>.
/// </summary>
internal static class WireManifest
{
    public const string FileName = "wire.manifest";
    private const string Tolerant = "version tolerant", KeyedFormat = "keyed";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>The manifest entry of <paramref name="type"/>, or null when it has none (<see cref="HasEntry"/>).</summary>
    /// <param name="weak">The assembly defines <c>WEAK_VERSION_TOLERANCE</c>: its positional types are version tolerant.</param>
    public static WireType? Read(INamedTypeSymbol type, bool weak)
    {
        if (!HasEntry(type)) return null;
        var home = type.ContainingAssembly;
        var (encoding, members) = Layout(type, weak, home);
        var fields = members.Select(m => new WireField(m.Name, Render(m.Type, weak, home, Open(type)) + m.Note)).ToArray();
        string? reply = Shapes.ReplyOf(type) is { } r ? Render(r, weak, home, Open(type)) : null;
        var ancestors = new List<INamedTypeSymbol>();
        void Up(INamedTypeSymbol t)
        {
            foreach (var parent in WireLayout.Parents(t))
                if (!ancestors.Contains(parent, SymbolEqualityComparer.Default))
                {
                    ancestors.Add(parent);
                    Up(parent);
                }
        }

        Up(type);
        return new WireType(Name(type), encoding, new(fields), reply, new(ancestors.Select(Name).ToArray()),
            ancestors.Any(a => !SymbolEqualityComparer.Default.Equals(a.ContainingAssembly, home)));
    }

    /// <summary>
    /// Every manifest entry of <paramref name="compilation"/>'s assembly, with its symbol: the one walk the generator
    /// and the analyzer share, so both count the same types (the analyzer's symbol actions skip generated files).
    /// </summary>
    public static List<(WireType Type, INamedTypeSymbol Symbol)> All(Compilation compilation, CancellationToken ct)
    {
        bool weak = compilation.SyntaxTrees.FirstOrDefault()?.Options.PreprocessorSymbolNames.Contains("WEAK_VERSION_TOLERANCE") == true;
        var types = new List<(WireType, INamedTypeSymbol)>();
        void Visit(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                ct.ThrowIfCancellationRequested();
                if (member is INamespaceSymbol ns) Visit(ns);
                else if (member is INamedTypeSymbol type)
                {
                    if (Read(type, weak) is { } wire) types.Add((wire, type));
                    Visit(type);
                }
            }
        }

        Visit(compilation.Assembly.GlobalNamespace);
        return types;
    }

    private static string Name(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static HashSet<ITypeSymbol> Open(ITypeSymbol type) => new(SymbolEqualityComparer.Default) { type };

    /// <summary>
    /// Whether <paramref name="type"/> has its own manifest entry: a [NinoType] of source Nino generates a formatter
    /// for (a class, struct or interface, public or internal down its containing types, not static or ref-like).
    /// </summary>
    public static bool HasEntry(INamedTypeSymbol type)
    {
        if (type.IsStatic || type.IsRefLikeType || type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface) ||
            !type.Locations.Any(l => l.IsInSource)) return false;
        for (var t = type; t != null; t = t.ContainingType)
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)) return false;
        return WireLayout.IsNinoType(type);
    }

    /// <summary>
    /// How Nino writes <paramref name="type"/> and the fields its bytes hold. A raw value (a plain unmanaged struct, a
    /// [NinoType] struct that qualifies) is its memory: every field in declaration order, private ones too, with its
    /// <c>[StructLayout]</c> (not for another assembly's type, whose metadata does not show it, so the IDE and the
    /// command line agree). Otherwise its header (a struct none, a sealed class a null flag, a class a type id, an
    /// abstract type its subtype's) and body format, and the members Nino serializes; a member hidden by a derived one
    /// (<c>new</c>) is named with its declaring type, as Nino keys it.
    /// </summary>
    private static (string Encoding, List<(string Name, ITypeSymbol Type, string Note)> Members) Layout(INamedTypeSymbol type, bool weak, IAssemblySymbol home)
    {
        bool own = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, home);
        var options = NinoOptions.Of(type);
        if (options == null || WireLayout.IsRawStruct(type, weak, home))
            return ("memory" + (own ? WireLayout.LayoutOf(type) : ""), WireLayout.MemoryFields(type).Select(f => (f.Name, f.Type, "")).ToList());

        string kind = type.TypeKind == TypeKind.Interface || type.IsAbstract ? "abstract" : type.IsValueType ? "struct" : type.IsSealed ? "sealed class" : "class";
        int format = options.Format == NinoOptions.Positional && weak && own ? NinoOptions.VersionTolerant : options.Format;
        string encoding = kind + ", " + (format switch { NinoOptions.VersionTolerant => Tolerant, NinoOptions.Keyed => KeyedFormat, _ => "positional" }) +
                          (options.Track ? ", tracked" : "");
        var members = WireLayout.Members(type, options);
        var result = new List<(string, ITypeSymbol, string)>();
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            bool hidden = members.Skip(i + 1).Any(m => m.Name == member.Name);
            result.Add(((hidden ? member.ContainingType.Name + "." : "") + member.Name, WireLayout.TypeOf(member), WireLayout.EncodingOf(member)));
        }

        return (encoding, result);
    }

    /// <summary>
    /// A field's type as its bytes depend on it: an enum with its underlying type; arrays and generic arguments
    /// element by element; an unmanaged struct without its own entry (a plain struct, an id, another assembly's) with
    /// its layout inline, so a change there shows here; other types by name (their entry, here or in their own
    /// assembly's manifest, records them).
    /// </summary>
    private static string Render(ITypeSymbol type, bool weak, IAssemblySymbol home, HashSet<ITypeSymbol> open)
    {
        if (type is IArrayTypeSymbol array) return Render(array.ElementType, weak, home, open) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is not INamedTypeSymbol named) return Name(type);
        if (named is { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }) return Name(named) + "(" + Name(underlying) + ")";
        if (named.IsGenericType && named.TypeArguments.Length > 0 && (named.SpecialType != SpecialType.None || Support.InSystemNamespace(named)))
            return Name(named.ConstructedFrom).Split('<')[0] + "<" + string.Join(", ", named.TypeArguments.Select(t => Render(t, weak, home, open))) + ">";
        if (named.SpecialType != SpecialType.None || Support.InSystemNamespace(named) || named.TypeKind != TypeKind.Struct || !named.IsUnmanagedType)
            return Name(named);
        bool foreign = !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, home);
        if (!foreign && HasEntry(named) || !open.Add(named)) return Name(named);
        var (encoding, members) = Layout(named, weak, home);
        string inline = Name(named) + " " + encoding + " {" + string.Join(", ", members.Select(m => m.Name + ": " + Render(m.Type, weak, home, open) + m.Note)) + "}";
        open.Remove(named);
        return inline;
    }

    /// <summary>The manifest text of <paramref name="types"/>, sorted by name.</summary>
    public static string Format(IReadOnlyList<WireType> types)
    {
        var sb = new StringBuilder()
            .Append("# DaServer wire manifest (ADR-076): the layout of this assembly's [NinoType] types on the wire and in stored records.\n")
            .Append("# Positional layouts are frozen, version-tolerant types take new fields at their end, keyed types any key that keeps\n")
            .Append("# its type. Record changes: dotnet build -p:UpdateWireManifest=true\n");
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
        if (entry.Encoding.Contains(KeyedFormat))
        {
            // A key's value is read by its type: another type under the same key is misread, any other change is skipped or defaulted.
            static Dictionary<string, string> Keys(IEnumerable<string> fields)
            {
                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string field in fields)
                {
                    int colon = field.IndexOf(": ", StringComparison.Ordinal);
                    keys[colon < 0 ? field : field.Substring(0, colon)] = colon < 0 ? "" : field.Substring(colon + 2);
                }

                return keys;
            }

            var before = Keys(entry.Fields);
            var after = Keys(now);
            foreach (var key in before)
                if (after.TryGetValue(key.Key, out var value) && value != key.Value)
                    return ($"key '{key.Key}' was '{key.Value}' and is '{value}': the other build misreads it (a new type takes a new key)", true);
            if (before.Count == after.Count && before.Keys.All(after.ContainsKey)) return null;
            return ($"has keys added ({string.Join(", ", after.Keys.Where(k => !before.ContainsKey(k)))}) or removed ({string.Join(", ", before.Keys.Where(k => !after.ContainsKey(k)))})", false);
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
}
