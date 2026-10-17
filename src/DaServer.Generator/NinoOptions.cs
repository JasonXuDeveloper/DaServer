using System.Collections.Immutable;

namespace DaServer.Generator;

/// <summary>The options of the <c>[NinoType]</c> that applies to a type, read as Nino's generator reads them (by parameter name).</summary>
internal sealed record NinoOptions(bool AutoCollect, bool NonPublic, bool Inherit, int Format, bool Track)
{
    public const int Positional = 0, VersionTolerant = 1, Keyed = 2;

    public static bool IsNinoType(ITypeSymbol type) => Of(type) != null;

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
