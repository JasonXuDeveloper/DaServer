using System.Text;

namespace DaServer.Generator;

/// <summary>A field as Nino writes it: in order, by name and wire type.</summary>
internal readonly record struct WireField(string Name, string Type)
{
    public override string ToString() => Name + ": " + Type;
}

/// <summary>
/// A [NinoType] type of this assembly as the wire manifest records it: its fields in Nino's order, whether its bytes
/// may grow at the end (a message or a persisted component is always a whole buffer or record section), which
/// [NinoType] types of this assembly its fields use and, for a request, its reply type of this assembly (a whole
/// buffer too).
/// </summary>
internal sealed record WireType(string Name, bool Grows, EquatableArray<WireField> Fields, EquatableArray<string> Uses, string? Reply);

/// <summary>
/// The wire manifest (ADR-076): the committed layout of every [NinoType] type an assembly declares. Nino reads a
/// buffer that ends early with defaults for the missing trailing fields (WEAK_VERSION_TOLERANCE) and ignores trailing
/// bytes it does not know, so old and new nodes understand each other as long as fields are only appended, and only
/// to a type whose bytes end where the buffer ends: a message or a persisted component that no other type nests.
/// Everything else (nested types, reordered, retyped or removed fields) breaks deployed peers and stored records.
/// </summary>
internal static class WireManifest
{
    public const string FileName = "wire.manifest";
    public const string Grows = "grows", Frozen = "frozen";

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>The manifest entry of <paramref name="type"/>, or null when it is not a [NinoType] declared in source.</summary>
    public static WireType? Read(INamedTypeSymbol type)
    {
        if (!Support.HasAttribute(type, Support.NinoTypeAttribute) || !type.Locations.Any(l => l.IsInSource)) return null;
        var fields = new List<WireField>();
        var uses = new SortedSet<string>(StringComparer.Ordinal);
        var settings = Settings(type);
        var levels = new Stack<INamedTypeSymbol>();
        for (var t = type; t != null && Support.HasAttribute(t, Support.NinoTypeAttribute); t = t.BaseType) levels.Push(t);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels) // base types first, as Nino writes them
            foreach (var member in Members(level, settings))
            {
                if (!seen.Add(member.Name)) continue;
                var memberType = member switch { IFieldSymbol f => f.Type, IPropertySymbol p => p.Type, _ => ((IParameterSymbol)member).Type };
                fields.Add(new WireField(member.Name, memberType.ToDisplayString(TypeFormat)));
                Collect(memberType, uses);
            }

        bool grows = Support.HasAttribute(type, Support.MessageAttribute) || Support.HasAttribute(type, Support.PersistAttribute) ||
                     Support.HasAttribute(type, Support.HandoffAttribute);
        uses.Remove(Name(type));
        var replies = new SortedSet<string>(StringComparer.Ordinal);
        if (Shapes.ReplyOf(type) is { } reply) Collect(reply, replies);
        return new WireType(Name(type), grows, new(fields.ToArray()), new(uses.ToArray()), replies.Count == 1 ? replies.Min : null);
    }

    private static string Name(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    /// <summary>(autoCollect, containNonPublicMembers) of the nearest [NinoType], as Nino reads it.</summary>
    private static (bool Auto, bool NonPublic) Settings(INamedTypeSymbol type)
    {
        var args = Support.GetAttribute(type, Support.NinoTypeAttribute)!.ConstructorArguments;
        return (args.Length < 1 || args[0].Value is not false, args.Length > 1 && args[1].Value is true);
    }

    /// <summary>The members Nino serializes for one level of the hierarchy, in Nino's order.</summary>
    private static IEnumerable<ISymbol> Members(INamedTypeSymbol type, (bool Auto, bool NonPublic) settings)
    {
        var members = new List<ISymbol>();
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member.IsStatic || (!settings.NonPublic && member.DeclaredAccessibility != Accessibility.Public)) continue;
            if (member is IFieldSymbol || member is IPropertySymbol { GetMethod: not null, SetMethod: not null }) members.Add(member);
        }

        if (type.IsRecord)
            foreach (var constructor in type.InstanceConstructors)
                if (constructor.DeclaredAccessibility == Accessibility.Public && !constructor.IsImplicitlyDeclared)
                {
                    members.AddRange(constructor.Parameters); // the primary constructor's properties, after the body's members
                    break;
                }

        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<ISymbol>();
        foreach (var member in members)
        {
            if (settings.Auto)
            {
                if (Support.HasAttribute(member, "Nino.Core.NinoIgnoreAttribute") || order.ContainsKey(member.Name)) continue;
                order[member.Name] = order.Count;
            }
            else if (Support.GetAttribute(member, "Nino.Core.NinoMemberAttribute") is { ConstructorArguments: { Length: > 0 } args } && args[0].Value is { } index)
                order[member.Name] = Convert.ToInt32(index);
            else continue;
            kept.Add(member);
        }

        if (Support.GetAttribute(type, "Nino.Core.NinoExplicitOrderAttribute") is { ConstructorArguments: { Length: > 0 } order0 })
            for (int i = 0; i < order0[0].Values.Length; i++)
                if (order0[0].Values[i].Value is string name && order.ContainsKey(name)) order[name] = i;
        return kept.OrderBy(m => order[m.Name]);
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
    /// Whether <paramref name="type"/> may append fields: it is a message, a persisted component or a reply (a whole
    /// buffer or record section) and no type of <paramref name="all"/> nests it.
    /// </summary>
    public static bool MayGrow(WireType type, IReadOnlyList<WireType> all)
    {
        bool whole = type.Grows;
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
            .Append("# 'grows' types may append fields, 'frozen' ones may not change. Update: dotnet build -p:UpdateWireManifest=true\n");
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
                yield return (type.Name, $"field {changed + 1} was '{entry.Fields[changed]}' and is '{type.Fields[changed]}': fields keep their place, name and type; only append new ones", true);
            else if (type.Fields.Length < entry.Fields.Count)
                yield return (type.Name, $"lost field '{entry.Fields[common]}': fields are never removed (ignore an unused one instead)", true);
            else if (type.Fields.Length > entry.Fields.Count && (!entry.Grows || !MayGrow(type, current)))
                yield return (type.Name, $"appends '{type.Fields[common]}' but is frozen: only a message, reply or persisted component that no other type nests may grow (add a new type instead)", true);
            else if (type.Fields.Length > entry.Fields.Count || entry.Grows != MayGrow(type, current))
                yield return (type.Name, "changed compatibly", false);
        }

        foreach (var type in current)
            if (!baseline.ContainsKey(type.Name))
                yield return (type.Name, "is new", false);
    }
}
