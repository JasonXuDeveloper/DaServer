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
/// its memory, its members, or its members behind a type id), its fields in the order its bytes hold them and, for a
/// request, its reply type.
/// </summary>
internal sealed record WireType(string Name, string Encoding, EquatableArray<WireField> Fields, string? Reply);

/// <summary>
/// The wire manifest (ADR-076): the committed layout of every type an assembly puts on the wire or in records, so a
/// change a deployed peer or a stored record would misread fails the build. Layouts are frozen: a new field goes into
/// a new message or component (Nino writes no names, lengths or versions; see the roadmap for its next version).
/// </summary>
internal static class WireManifest
{
    public const string FileName = "wire.manifest";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>The manifest entry of <paramref name="type"/>, or null when Nino does not serialize it (not a public [NinoType] of source).</summary>
    public static WireType? Read(INamedTypeSymbol type)
    {
        if (!HasEntry(type)) return null;
        var (encoding, members) = Layout(type);
        var fields = new List<WireField>();
        foreach (var (name, fieldType) in members) fields.Add(new WireField(name, Render(fieldType, type.ContainingAssembly, Open(type))));
        string? reply = Shapes.ReplyOf(type) is { } r ? Render(r, type.ContainingAssembly, Open(type)) : null;
        return new WireType(Name(type), encoding, new(fields.ToArray()), reply);
    }

    private static string Name(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static HashSet<ITypeSymbol> Open(ITypeSymbol type) => new(SymbolEqualityComparer.Default) { type };

    /// <summary>Whether <paramref name="type"/> has its own manifest entry: a public type of source Nino serializes.</summary>
    public static bool HasEntry(INamedTypeSymbol type) =>
        type.DeclaredAccessibility == Accessibility.Public && type.Locations.Any(l => l.IsInSource) && IsNinoType(type);

    /// <summary>
    /// How Nino writes <paramref name="type"/>: a value-only struct as its memory (every field, private ones too, in
    /// declaration order, with its <c>[StructLayout]</c>), behind a type id when it has [NinoType] parents; a class or
    /// another struct with [NinoType] parents as its members behind a type id; otherwise its members (Nino's rules).
    /// </summary>
    private static (string Encoding, List<(string Name, ITypeSymbol Type)> Members) Layout(INamedTypeSymbol type)
    {
        bool parents = Parents(type).Any();
        if (type.IsUnmanagedType && type.TypeKind == TypeKind.Struct)
        {
            string layout = Support.GetAttribute(type, "System.Runtime.InteropServices.StructLayoutAttribute") is { } a
                ? "(" + string.Join(", ", a.ConstructorArguments.Select(c => c.Value?.ToString()).Concat(a.NamedArguments.Select(n => n.Key + "=" + n.Value.Value))) + ")"
                : "";
            return ((parents ? "typed memory" : "memory") + layout, MemoryFields(type));
        }

        return (type.TypeKind == TypeKind.Struct && !parents ? "members" : "typed members", NinoMembers(type));
    }

    /// <summary>
    /// A field's type as its bytes depend on it: an enum with its underlying type; arrays and generic arguments
    /// element by element; a type without its own manifest entry here (another assembly's, a plain struct, an id)
    /// with its fields inline, so a change there shows here.
    /// </summary>
    private static string Render(ITypeSymbol type, IAssemblySymbol home, HashSet<ITypeSymbol> open)
    {
        if (type is IArrayTypeSymbol array) return Render(array.ElementType, home, open) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is not INamedTypeSymbol named) return Name(type);
        if (named is { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying }) return Name(named) + "(" + Name(underlying) + ")";
        if (named.IsGenericType && named.TypeArguments.Length > 0 && (named.SpecialType != SpecialType.None || Support.InSystemNamespace(named)))
            return Name(named.ConstructedFrom).Split('<')[0] + "<" + string.Join(", ", named.TypeArguments.Select(t => Render(t, home, open))) + ">";
        if (named.SpecialType != SpecialType.None || Support.InSystemNamespace(named) || named.TypeKind is not (TypeKind.Struct or TypeKind.Class))
            return Name(named);
        // Its own entry here records it; a type already being rendered (it refers to itself) is named only.
        if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, home) && HasEntry(named) || !open.Add(named)) return Name(named);
        var (encoding, members) = Layout(named);
        string inline = Name(named) + " " + encoding + " {" + string.Join(", ", members.Select(m => m.Name + ": " + Render(m.Type, home, open))) + "}";
        open.Remove(named);
        return inline;
    }

    /// <summary>The instance fields of a value-only struct in memory order (Nino copies its memory, private fields included).</summary>
    private static List<(string Name, ITypeSymbol Type)> MemoryFields(INamedTypeSymbol type)
    {
        var fields = new List<(string, ITypeSymbol)>();
        foreach (var member in type.GetMembers())
            if (member is IFieldSymbol { IsStatic: false, IsConst: false } field)
                fields.Add((field.AssociatedSymbol?.Name ?? PropertyOf(field.Name) + (field.IsFixedSizeBuffer ? "[" + field.FixedSize + "]" : ""), field.Type));
        return fields;
    }

    /// <summary>The property an auto-property's backing field (<c>&lt;Name&gt;k__BackingField</c>) belongs to (metadata types name only the field).</summary>
    private static string PropertyOf(string field) =>
        field.StartsWith("<", StringComparison.Ordinal) && field.IndexOf('>') is > 1 and var end ? field.Substring(1, end - 1) : field;

    /// <summary>
    /// Whether Nino treats <paramref name="type"/> as a [NinoType]: its own attribute, else the nearest base's that allows
    /// inheritance, else an interface's that does.
    /// </summary>
    private static bool IsNinoType(ITypeSymbol type)
    {
        if (Support.HasAttribute(type, Support.NinoTypeAttribute)) return true;
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (Support.GetAttribute(b, Support.NinoTypeAttribute) is { } attribute)
            {
                if (Inherits(attribute)) return true;
                break;
            }

        return type.AllInterfaces.Any(i => Support.GetAttribute(i, Support.NinoTypeAttribute) is { } attribute && Inherits(attribute));
    }

    private static bool Inherits(AttributeData ninoType) =>
        ninoType.ConstructorArguments.Length < 3 || ninoType.ConstructorArguments[2].Value is not false;

    /// <summary>The types whose members Nino writes before <paramref name="type"/>'s: its [NinoType] base, then its [NinoType] interfaces.</summary>
    private static IEnumerable<INamedTypeSymbol> Parents(INamedTypeSymbol type)
    {
        if (type.BaseType is { } baseType && IsNinoType(baseType)) yield return baseType;
        foreach (var i in type.AllInterfaces)
            if (IsNinoType(i)) yield return i;
    }

    /// <summary>The members Nino writes for <paramref name="type"/>, in its order: parents first (depth first), names once.</summary>
    private static List<(string Name, ITypeSymbol Type)> NinoMembers(INamedTypeSymbol type)
    {
        var attribute = Support.GetAttribute(type, Support.NinoTypeAttribute);
        for (var b = type.BaseType; attribute == null && b != null; b = b.BaseType) attribute = Support.GetAttribute(b, Support.NinoTypeAttribute);
        var args = attribute?.ConstructorArguments ?? ImmutableArray<TypedConstant>.Empty;
        bool auto = args.Length < 1 || args[0].Value is not false, nonPublic = args.Length > 1 && args[1].Value is true;
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        if (Support.GetAttribute(type, "Nino.Core.NinoExplicitOrderAttribute") is { ConstructorArguments: { Length: > 0 } explicitOrder })
            for (int i = 0; i < explicitOrder[0].Values.Length; i++)
                if (explicitOrder[0].Values[i].Value is string name) order[name] = i;

        var members = new List<(string, ITypeSymbol)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // The encoding note travels with the name (`Text utf8: string`), after names were matched for order and once.
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        void AddLevel(INamedTypeSymbol level)
        {
            if (!visited.Add(level)) return;
            foreach (var parent in Parents(level)) AddLevel(parent);
            var collected = Collected(level, type, auto, nonPublic);
            if (order.Count > 0) collected = collected.OrderBy(m => order.TryGetValue(m.Name, out int i) ? i : int.MaxValue).ToList();
            foreach (var member in collected)
                if (seen.Add(member.Name)) members.Add((member.Name + member.Encoding, member.Type));
        }

        AddLevel(type);
        return members;
    }

    /// <summary>One level's members as Nino collects them (Nino's parser: fields, settable properties, a record's primary constructor).</summary>
    private static List<(string Name, ITypeSymbol Type, string Encoding)> Collected(INamedTypeSymbol level, INamedTypeSymbol leaf, bool auto, bool nonPublic)
    {
        var members = new List<ISymbol>();
        foreach (var member in level.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member.IsStatic || (!nonPublic && member.DeclaredAccessibility != Accessibility.Public)) continue;
            if (member is IFieldSymbol || member is IPropertySymbol { GetMethod: not null, SetMethod: not null }) members.Add(member);
        }

        var primary = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (level.IsRecord)
            foreach (var constructor in level.InstanceConstructors)
                if (constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsImplicitlyDeclared)
                {
                    members.AddRange(constructor.Parameters);
                    primary.UnionWith(constructor.Parameters);
                    break;
                }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<(ISymbol Symbol, string Name, ITypeSymbol Type)>();
        foreach (var member in members)
        {
            if (index.ContainsKey(member.Name)) continue;
            // A base's private or internal member is not the leaf's to write (protected ones are).
            if (member.DeclaredAccessibility != Accessibility.Public && !primary.Contains(member) &&
                !SymbolEqualityComparer.Default.Equals(member.ContainingType, leaf) && member.DeclaredAccessibility != Accessibility.Protected) continue;
            if (auto)
            {
                if (Support.HasAttribute(member, "Nino.Core.NinoIgnoreAttribute")) continue;
                index[member.Name] = index.Count;
            }
            else if (Support.GetAttribute(member, "Nino.Core.NinoMemberAttribute") is { ConstructorArguments: { Length: > 0 } a } && a[0].Value is { } i)
                index[member.Name] = Convert.ToInt32(i);
            else continue;
            var memberType = member switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => ((IParameterSymbol)member).Type };
            kept.Add((member, member.Name, memberType));
        }

        return kept.OrderBy(m => index[m.Name]).Select(m => (m.Name, m.Type, Encoding(m.Symbol))).ToList();
    }

    /// <summary>What changes a member's bytes beyond its type: <c>[NinoUtf8]</c>, a <c>[NinoCustomFormatter]</c>.</summary>
    private static string Encoding(ISymbol member)
    {
        if (Support.HasAttribute(member, "Nino.Core.NinoUtf8Attribute")) return " utf8";
        return Support.GetAttribute(member, "Nino.Core.NinoCustomFormatterAttribute") is { ConstructorArguments: { Length: > 0 } a }
            ? " via " + a[0].Value
            : "";
    }

    /// <summary>The manifest text of <paramref name="types"/>, sorted by name.</summary>
    public static string Format(IReadOnlyList<WireType> types)
    {
        var sb = new StringBuilder()
            .Append("# DaServer wire manifest (ADR-076): the layout of this assembly's [NinoType] types on the wire and in stored records.\n")
            .Append("# Frozen: a new field goes into a new message or component. Record new types: dotnet build -p:UpdateWireManifest=true\n");
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
    /// What changed against <paramref name="baseline"/>: (type, what, breaking). Breaking: a type gone, its encoding,
    /// a field or its reply changed (fields appended included). Not breaking but not recorded yet: a new type.
    /// </summary>
    public static IEnumerable<(string Type, string What, bool Breaking)> Compare(Dictionary<string, Entry> baseline, IReadOnlyList<WireType> current)
    {
        var byName = current.ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var entry in baseline.Values.OrderBy(e => e.Line))
        {
            if (!byName.TryGetValue(entry.Name, out var type))
            {
                yield return (entry.Name, "is gone (removed or renamed): deployed peers and stored records still carry it; delete it from the manifest once no deployed version uses it", true);
                continue;
            }

            int changed = -1;
            for (int i = 0; i < Math.Max(entry.Fields.Count, type.Fields.Length) && changed < 0; i++)
                if (i >= entry.Fields.Count || i >= type.Fields.Length || entry.Fields[i] != type.Fields[i].ToString()) changed = i;
            if (entry.Encoding != type.Encoding)
                yield return (type.Name, $"was written as {entry.Encoding} and is now {type.Encoding}", true);
            else if (changed >= 0)
                yield return (type.Name, $"field {changed + 1} was '{(changed < entry.Fields.Count ? entry.Fields[changed] : "nothing")}' and is '{(changed < type.Fields.Length ? type.Fields[changed].ToString() : "nothing")}': a layout is frozen, a new field goes into a new message or component", true);
            else if (entry.Reply != type.Reply)
                yield return (type.Name, $"replied '{entry.Reply ?? "nothing"}' and now replies '{type.Reply ?? "nothing"}': callers of the other build misread it", true);
        }

        foreach (var type in current)
            if (!baseline.ContainsKey(type.Name))
                yield return (type.Name, "is new", false);
    }
}
