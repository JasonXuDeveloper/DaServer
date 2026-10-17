using System.Collections.Immutable;

namespace DaServer.Generator;

/// <summary>The options of the <c>[NinoType]</c> that applies to a type, read as Nino's generator reads them (by parameter name).</summary>
internal sealed record NinoOptions(bool AutoCollect, bool NonPublic, bool Inherit, int Format, bool Track)
{
    public const int Positional = 0, VersionTolerant = 1, Keyed = 2;

    /// <summary>The type's own [NinoType], else the nearest base's that allows inheritance, else an interface's that does; null when none applies.</summary>
    public static NinoOptions? Of(ITypeSymbol type)
    {
        if (Support.InSystemNamespace(type) || type.TypeKind is TypeKind.Array or TypeKind.Pointer or TypeKind.TypeParameter
                or TypeKind.Enum or TypeKind.Delegate or TypeKind.Error) return null;
        if (Support.GetAttribute(type, Support.NinoTypeAttribute) is { } own) return From(own);
        for (var b = type.BaseType; b != null; b = b.BaseType)
            if (Support.GetAttribute(b, Support.NinoTypeAttribute) is { } attribute)
            {
                var options = From(attribute);
                if (options.Inherit) return options;
                break;
            }

        foreach (var i in type.AllInterfaces)
            if (Support.GetAttribute(i, Support.NinoTypeAttribute) is { } attribute && From(attribute) is { Inherit: true } options)
                return options;
        return null;
    }

    private static NinoOptions From(AttributeData attribute)
    {
        bool auto = true, nonPublic = false, inherit = true, track = false;
        int format = Positional;
        var parameters = attribute.AttributeConstructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty;
        for (int i = 0; i < parameters.Length && i < attribute.ConstructorArguments.Length; i++)
        {
            object? value = attribute.ConstructorArguments[i].Value;
            switch (parameters[i].Name)
            {
                case "format" when value is int f: format = f; break;
                case "autoCollect" when value is bool b: auto = b; break;
                case "containNonPublicMembers" when value is bool b: nonPublic = b; break;
                case "allowInheritance" when value is bool b: inherit = b; break;
                case "trackReferences" when value is bool b: track = b; break;
            }
        }

        return new NinoOptions(auto, nonPublic, inherit, format, track);
    }
}

/// <summary>
/// How Nino 4 writes a type, mirrored from its generator (ADR-076): which members a [NinoType] serializes and in what
/// order, and which values are copied as their raw memory. The manifest is only as true as this mirror; Nino issue
/// #197 asks Nino to publish the layout itself.
/// </summary>
internal static class WireLayout
{
    private const string NinoMember = "Nino.Core.NinoMemberAttribute", NinoIgnore = "Nino.Core.NinoIgnoreAttribute",
        NinoConstructor = "Nino.Core.NinoConstructorAttribute", NinoCustomFormatter = "Nino.Core.NinoCustomFormatterAttribute",
        StructLayout = "System.Runtime.InteropServices.StructLayoutAttribute";

    public static bool IsNinoType(ITypeSymbol type) => NinoOptions.Of(type) != null;

    /// <summary>The types whose members Nino writes before <paramref name="type"/>'s: its [NinoType] base, then its [NinoType] interfaces.</summary>
    public static IEnumerable<INamedTypeSymbol> Parents(INamedTypeSymbol type)
    {
        if (type.BaseType is { } baseType && IsNinoType(baseType)) yield return baseType;
        foreach (var i in type.AllInterfaces)
            if (IsNinoType(i)) yield return i;
    }

    /// <summary>
    /// The members Nino serializes for <paramref name="type"/>, in wire order: level by level (bases root first, each
    /// led by the [NinoType] interfaces it implements), each level by declaration or <c>[NinoMember]</c> order.
    /// </summary>
    public static List<ISymbol> Members(INamedTypeSymbol type, NinoOptions options)
    {
        var levels = new List<INamedTypeSymbol>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        void AddLevel(INamedTypeSymbol t)
        {
            if (!visited.Add(t)) return;
            if (t.BaseType is { } b && IsNinoType(b)) AddLevel(b);
            foreach (var i in t.AllInterfaces)
                if (IsNinoType(i)) AddLevel(i);
            levels.Add(t);
        }

        AddLevel(type);
        var constructorNames = ConstructorNames(type);
        var inserted = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ISymbol>();
        foreach (var level in levels)
        {
            var candidates = new List<(ISymbol Symbol, int Index)>();
            int declaration = 0;
            foreach (var member in level.GetMembers())
            {
                if (!IsCandidate(member, options.NonPublic, constructorNames)) continue;
                if (options.AutoCollect && Support.HasAttribute(member, NinoIgnore)) continue;
                // A base's private members are not the derived type's (protected ones are).
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type) && member.DeclaredAccessibility == Accessibility.Private) continue;
                if (options.AutoCollect) candidates.Add((member, declaration++));
                else if (Support.GetAttribute(member, NinoMember) is { ConstructorArguments: { Length: > 0 } a } && a[0].Value is { } index)
                    candidates.Add((member, Convert.ToInt32(index)));
            }

            foreach (var (candidate, _) in candidates.OrderBy(c => c.Index).ThenBy(c => c.Symbol.Name, StringComparer.Ordinal))
            {
                // An interface's member is the implementing type's member of that name, once; an override is the
                // member it overrides; a member hiding a base one (`new`) is another member.
                bool fromInterface = level.TypeKind == TypeKind.Interface;
                var symbol = fromInterface ? Implementation(type, candidate) : candidate;
                if ((fromInterface && names.Contains(symbol.Name)) || Overrides(symbol, inserted) || !inserted.Add(symbol)) continue;
                names.Add(symbol.Name);
                result.Add(symbol);
            }
        }

        return result;
    }

    public static ITypeSymbol TypeOf(ISymbol member) => member is IFieldSymbol f ? f.Type : ((IPropertySymbol)member).Type;

    /// <summary>What changes a member's bytes beyond its type: <c>[NinoUtf8]</c>, a <c>[NinoCustomFormatter]</c>.</summary>
    public static string EncodingOf(ISymbol member)
    {
        if (Support.HasAttribute(member, "Nino.Core.NinoUtf8Attribute")) return " utf8";
        return Support.GetAttribute(member, NinoCustomFormatter) is { ConstructorArguments: { Length: > 0 } a } ? " via " + a[0].Value : "";
    }

    /// <summary>
    /// Whether values of <paramref name="type"/> are written as their raw memory: an unmanaged type other than
    /// pointer-sized ones, <c>Nullable</c>, tuples, <c>KeyValuePair</c> and <c>DateTimeOffset</c>; a [NinoType]
    /// struct only when it qualifies (<see cref="IsRawStruct"/>).
    /// </summary>
    public static bool IsRawUnmanaged(ITypeSymbol type, bool weak, IAssemblySymbol home)
    {
        if (type is ITypeParameterSymbol || !type.IsUnmanagedType) return false;
        if (IsNinoType(type)) return type is INamedTypeSymbol nino && IsRawStruct(nino, weak, home);
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr || type is IPointerTypeSymbol) return false;
        if (type is INamedTypeSymbol { IsTupleType: true }) return false;
        if (type is INamedTypeSymbol named && Support.InSystemNamespace(named) && named.OriginalDefinition is var d &&
            (d.SpecialType == SpecialType.System_Nullable_T || d.MetadataName is "KeyValuePair`2" or "DateTimeOffset" || d.Name == "ValueTuple"))
            return false;
        return true;
    }

    /// <summary>
    /// Whether a [NinoType] struct is written as its raw memory: unmanaged, positional (not tolerant through
    /// <c>WEAK_VERSION_TOLERANCE</c> either, which applies to <paramref name="home"/>'s types), sequential, and every
    /// instance field serialized, without a custom formatter and raw itself. Another assembly's struct with
    /// auto-properties is not: Nino matches a backing field to its property through <c>AssociatedSymbol</c>, which
    /// metadata symbols lack, so the command line (metadata references) writes it member by member; the IDE (project
    /// references as source) would say raw, and the manifest follows the command line (Nino issue #200).
    /// </summary>
    public static bool IsRawStruct(INamedTypeSymbol type, bool weak, IAssemblySymbol home)
    {
        if (type.TypeKind != TypeKind.Struct || !type.IsUnmanagedType) return false;
        bool own = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, home);
        if (weak && own) return false;
        if (NinoOptions.Of(type) is not { Format: NinoOptions.Positional } options) return false;
        if (Support.GetAttribute(type, StructLayout) is { ConstructorArguments: { Length: > 0 } layout } && layout[0].Value is int kind && kind != 0) return false;
        var members = new HashSet<ISymbol>(Members(type.OriginalDefinition, options), SymbolEqualityComparer.Default);
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field) continue;
            if (!own && (field.AssociatedSymbol != null || field.Name.StartsWith("<", StringComparison.Ordinal))) return false;
            var serialized = field.AssociatedSymbol ?? field;
            if (!members.Contains(serialized.OriginalDefinition) || Support.HasAttribute(serialized, NinoCustomFormatter) ||
                !IsRawUnmanaged(field.Type, weak, home)) return false;
        }

        return true;
    }

    /// <summary>The <c>[StructLayout]</c> arguments of a raw value (<c>(0, Pack=4)</c>), which change its memory.</summary>
    public static string LayoutOf(INamedTypeSymbol type) =>
        Support.GetAttribute(type, StructLayout) is { } a
            ? "(" + string.Join(", ", a.ConstructorArguments.Select(c => c.Value?.ToString()).Concat(a.NamedArguments.Select(n => n.Key + "=" + n.Value.Value))) + ")"
            : "";

    /// <summary>The instance fields of a raw value in memory order (private ones too: the copy holds them).</summary>
    public static List<(string Name, ITypeSymbol Type)> MemoryFields(INamedTypeSymbol type)
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
    /// Fields and properties Nino may collect: not static, public unless non-public members are; fields declared in
    /// source (not backing fields, constants or fixed buffers); properties with a getter, no indexers or explicit
    /// interface implementations, with a setter, or get-only auto-properties a constructor takes.
    /// </summary>
    private static bool IsCandidate(ISymbol member, bool nonPublic, HashSet<string> constructorNames)
    {
        if (member.IsStatic || !(nonPublic || member.DeclaredAccessibility == Accessibility.Public)) return false;
        return member switch
        {
            IFieldSymbol field => !field.IsImplicitlyDeclared && !field.IsConst && !field.IsFixedSizeBuffer,
            IPropertySymbol property => !property.IsIndexer && property.GetMethod != null && property.ExplicitInterfaceImplementations.IsEmpty &&
                                        (property.SetMethod != null || IsGetOnlyAuto(property) && constructorNames.Contains(property.Name)),
            _ => false,
        };
    }

    private static bool IsGetOnlyAuto(IPropertySymbol property) =>
        property is { SetMethod: null, IsImplicitlyDeclared: false, IsAbstract: false } &&
        property.ContainingType.GetMembers().Any(m => m is IFieldSymbol { IsImplicitlyDeclared: true } f && SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    /// <summary>
    /// The parameter names (any case) of the constructors and <c>[NinoConstructor]</c> factories generated code may
    /// call, or the member names a <c>[NinoConstructor]</c> lists.
    /// </summary>
    private static HashSet<string> ConstructorNames(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method ||
                !(method.MethodKind == MethodKind.Constructor && !method.IsStatic || method.IsStatic && method.MethodKind == MethodKind.Ordinary)) continue;
            var marked = Support.GetAttribute(method, NinoConstructor);
            if (method.IsStatic && marked == null) continue;
            if (marked is { ConstructorArguments: { Length: > 0 } a } && a[0].Kind == TypedConstantKind.Array && a[0].Values.Length > 0)
                foreach (var v in a[0].Values)
                {
                    if (v.Value is string s) names.Add(s);
                }
            else if (method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
                foreach (var parameter in method.Parameters)
                    names.Add(parameter.Name);
        }

        return names;
    }

    private static ISymbol Implementation(INamedTypeSymbol type, ISymbol member)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.GetMembers(member.Name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol) is { } found) return found;
        return member;
    }

    private static bool Overrides(ISymbol member, HashSet<ISymbol> collected)
    {
        if (member is not IPropertySymbol { IsOverride: true } property) return false;
        for (var p = property.OverriddenProperty; p != null; p = p.OverriddenProperty)
            if (collected.Contains(p)) return true;
        return false;
    }
}
