using System.Collections.Immutable;
using System.Text;

namespace DaServer.Generator;

/// <summary>A field as its bytes hold it: in order, by name and wire type.</summary>
internal readonly record struct WireField(string Name, string Type)
{
    public override string ToString() => Name + ": " + Type;
}

/// <summary>
/// A [NinoType] type of this assembly as the wire manifest records it: its fields in the order its bytes hold them,
/// whether Nino writes it as its memory (<paramref name="Raw"/>: value fields only), whether it is a whole buffer or
/// record section (<paramref name="Whole"/>: a message or a persisted component; a reply is found through
/// <see cref="Reply"/>), which types of this assembly its fields nest and, for a request, its reply type.
/// </summary>
internal sealed record WireType(string Name, bool Raw, bool Whole, EquatableArray<WireField> Fields, EquatableArray<string> Uses, string? Reply);

/// <summary>
/// The wire manifest (ADR-076): the committed layout of every [NinoType] type an assembly declares, so a change that
/// a deployed peer or a stored record would misread fails the build. Nino writes a type made only of value fields as
/// its memory; <see cref="DaServer.Rpc.WirePayload"/> reads an older, shorter payload of such a type with zeros for
/// the fields it lacks, and Nino ignores bytes past the end of a type. So appending fields is safe only for such a
/// type that is a whole message, reply or record section and that no other type nests; every other type is frozen.
/// </summary>
internal static class WireManifest
{
    public const string FileName = "wire.manifest";
    public const string Grows = "grows", Frozen = "frozen";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>The manifest entry of <paramref name="type"/>, or null when Nino does not serialize it (not a public [NinoType] of source).</summary>
    public static WireType? Read(INamedTypeSymbol type)
    {
        if (!Support.HasAttribute(type, Support.NinoTypeAttribute) || type.DeclaredAccessibility != Accessibility.Public ||
            !type.Locations.Any(l => l.IsInSource)) return null;
        var uses = new SortedSet<string>(StringComparer.Ordinal);
        bool raw = type.IsUnmanagedType && type.TypeKind == TypeKind.Struct && !Parents(type).Any();
        var fields = new List<WireField>();
        foreach (var (name, fieldType) in raw ? MemoryFields(type) : NinoMembers(type))
        {
            fields.Add(new WireField(name, Render(fieldType, type.ContainingAssembly, 0)));
            Collect(fieldType, uses);
        }

        bool whole = Support.HasAttribute(type, Support.MessageAttribute) || Support.HasAttribute(type, Support.PersistAttribute) ||
                     Support.HasAttribute(type, Support.HandoffAttribute);
        uses.Remove(Name(type));
        var replies = new SortedSet<string>(StringComparer.Ordinal);
        if (Shapes.ReplyOf(type) is { } reply) Collect(reply, replies);
        return new WireType(Name(type), raw, whole, new(fields.ToArray()), new(uses.ToArray()), replies.Count == 1 ? replies.Min : null);
    }

    private static string Name(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    /// <summary>
    /// A field's type as its bytes depend on it: an enum with its underlying type, a value type or [NinoType] of
    /// another assembly with its own fields (its manifest, if any, is that assembly's: a change there must show here).
    /// </summary>
    private static string Render(ITypeSymbol type, IAssemblySymbol home, int depth)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            return Name(type) + "(" + Name(underlying) + ")";
        if (type is not INamedTypeSymbol named || depth > 4 || named.SpecialType != SpecialType.None ||
            SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, home) || Support.InSystemNamespace(named))
            return Name(type);
        var members = named.IsUnmanagedType && named.TypeKind == TypeKind.Struct && !Parents(named).Any() ? MemoryFields(named)
            : Support.HasAttribute(named, Support.NinoTypeAttribute) ? NinoMembers(named)
            : null;
        return members == null ? Name(type)
            : Name(type) + "{" + string.Join(", ", members.Select(m => m.Name + ": " + Render(m.Type, home, depth + 1))) + "}";
    }

    /// <summary>The instance fields of a value-only struct in memory order (Nino copies its memory, private fields included).</summary>
    private static List<(string Name, ITypeSymbol Type)> MemoryFields(INamedTypeSymbol type)
    {
        var fields = new List<(string, ITypeSymbol)>();
        foreach (var member in type.GetMembers())
            if (member is IFieldSymbol { IsStatic: false, IsConst: false } field)
                fields.Add((field.AssociatedSymbol?.Name ?? PropertyOf(field.Name), field.Type));
        return fields;
    }

    /// <summary>The property an auto-property's backing field (<c>&lt;Name&gt;k__BackingField</c>) belongs to (metadata types name only the field).</summary>
    private static string PropertyOf(string field) =>
        field.StartsWith("<", StringComparison.Ordinal) && field.IndexOf('>') is > 1 and var end ? field.Substring(1, end - 1) : field;

    /// <summary>Whether Nino treats <paramref name="type"/> as a [NinoType]: its own attribute, an inherited one or an interface's.</summary>
    private static bool IsNinoType(ITypeSymbol type)
    {
        if (Support.HasAttribute(type, Support.NinoTypeAttribute)) return true;
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (Support.GetAttribute(b, Support.NinoTypeAttribute) is { } attribute)
                return attribute.ConstructorArguments.Length < 3 || attribute.ConstructorArguments[2].Value is not false;
        return type.AllInterfaces.Any(i => Support.HasAttribute(i, Support.NinoTypeAttribute));
    }

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
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        void AddLevel(INamedTypeSymbol level)
        {
            if (!visited.Add(level)) return;
            foreach (var parent in Parents(level)) AddLevel(parent);
            var collected = Collected(level, type, auto, nonPublic);
            if (order.Count > 0) collected = collected.OrderBy(m => order.TryGetValue(m.Name, out int i) ? i : int.MaxValue).ToList();
            foreach (var member in collected)
                if (seen.Add(member.Name)) members.Add(member);
        }

        AddLevel(type);
        return members;
    }

    /// <summary>One level's members as Nino collects them (Nino's parser: fields, settable properties, a record's primary constructor).</summary>
    private static List<(string Name, ITypeSymbol Type)> Collected(INamedTypeSymbol level, INamedTypeSymbol leaf, bool auto, bool nonPublic)
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

        return kept.OrderBy(m => index[m.Name]).Select(m => (m.Name, m.Type)).ToList();
    }

    /// <summary>The [NinoType] types of source that <paramref name="type"/> nests (through arrays and generic arguments).</summary>
    private static void Collect(ITypeSymbol type, ISet<string> uses)
    {
        if (type is IArrayTypeSymbol array)
        {
            Collect(array.ElementType, uses);
            return;
        }

        if (type is not INamedTypeSymbol named) return;
        if (Support.HasAttribute(named.OriginalDefinition, Support.NinoTypeAttribute) && named.Locations.Any(l => l.IsInSource))
            uses.Add(Name(named.OriginalDefinition));
        foreach (var argument in named.TypeArguments) Collect(argument, uses);
    }

    /// <summary>
    /// Whether <paramref name="type"/> may append fields: Nino writes it as its memory, it is a whole message, reply or
    /// persisted component, and no type of <paramref name="all"/> nests it.
    /// </summary>
    public static bool MayGrow(WireType type, IReadOnlyList<WireType> all)
    {
        if (!type.Raw) return false;
        bool whole = type.Whole;
        foreach (var other in all)
        {
            if (other.Uses.Contains(type.Name)) return false;
            whole |= other.Reply == type.Name;
        }

        return whole;
    }

    /// <summary>The manifest text of <paramref name="types"/>, sorted by name.</summary>
    public static string Format(IReadOnlyList<WireType> types)
    {
        var sb = new StringBuilder()
            .Append("# DaServer wire manifest (ADR-076): the layout of this assembly's [NinoType] types on the wire and in stored records.\n")
            .Append("# 'grows' types (value fields only, whole messages, replies, records) may append fields, 'frozen' ones may not change. Update: dotnet build -p:UpdateWireManifest=true\n");
        foreach (var type in types.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(type.Name).Append(' ').Append(MayGrow(type, types) ? Grows : Frozen).Append('\n');
            foreach (var field in type.Fields) sb.Append("- ").Append(field).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>A type as a committed manifest records it, with the line it starts on.</summary>
    public sealed record Entry(string Name, bool Grows, List<string> Fields, int Line);

    public static Dictionary<string, Entry> Parse(string text)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        Entry? current = null;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                current?.Fields.Add(line.Substring(2));
                continue;
            }

            int space = line.LastIndexOf(' ');
            string name = space < 0 ? line : line.Substring(0, space);
            current = entries[name] = new Entry(name, space >= 0 && line.Substring(space + 1) == Grows, [], i);
        }

        return entries;
    }

    /// <summary>
    /// What changed against <paramref name="baseline"/>: (type, what, breaking). Breaking: a type is gone, a field was
    /// removed, renamed, retyped or moved, or a frozen type grew. Not breaking but not recorded yet: new types, appended
    /// fields of a type that may grow.
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

            int common = Math.Min(entry.Fields.Count, type.Fields.Length);
            int changed = -1;
            for (int i = 0; i < common && changed < 0; i++)
                if (entry.Fields[i] != type.Fields[i].ToString()) changed = i;
            if (changed >= 0)
                yield return (type.Name, $"field {changed + 1} was '{entry.Fields[changed]}' and is '{type.Fields[changed]}': fields keep their place, name and type (a rename counts: the manifest keeps names to be read); only append new ones", true);
            else if (type.Fields.Length < entry.Fields.Count)
                yield return (type.Name, $"lost field '{entry.Fields[common]}': fields are never removed (ignore an unused one instead)", true);
            else if (type.Fields.Length > entry.Fields.Count && (!entry.Grows || !MayGrow(type, current)))
                yield return (type.Name, $"appends '{type.Fields[common]}' but is frozen: only a message, reply or persisted component of value fields that no other type nests may grow (add a new message or component instead)", true);
            else if (type.Fields.Length > entry.Fields.Count || entry.Grows != MayGrow(type, current))
                yield return (type.Name, "changed compatibly", false);
        }

        foreach (var type in current)
            if (!baseline.ContainsKey(type.Name))
                yield return (type.Name, "is new", false);
    }
}
