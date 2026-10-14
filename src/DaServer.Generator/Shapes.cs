using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

internal enum ContractKind
{
    Service,
    Actor,
    ClientCallback,
}

internal enum MethodShape
{
    OneWay,
    Task,
    TaskOfT,
}

internal sealed record ParameterModel(string Type, string Name);

internal sealed record MethodModel(string Name, uint Id, MethodShape Shape, string ResultType,
    EquatableArray<ParameterModel> Parameters);

/// <summary>Symbol-free description of an RPC contract (cached by the incremental pipeline).</summary>
/// <summary>How clients may call a contract (mirrors DaServer.Rpc.ClientAccess).</summary>
internal enum ClientAccess
{
    None,
    Service,
    SessionKey,
    AnyKey,
}

internal sealed record ContractModel(string Namespace, string Name, string FullName, ContractKind Kind, uint Id,
    ClientAccess Access, EquatableArray<MethodModel> Methods)
{
    public string Accessor => StripInterfacePrefix(Name);
    public string ProxyName => Accessor + "Proxy";
    public string RpcClassName => Accessor + "Rpc";
}

/// <summary>Symbol-free description of a hosted implementation (contracts it serves, events it handles) or shard system.</summary>
internal sealed record HostedModel(string FullName, bool IsSystem, ContractKind Kind, EquatableArray<string> Contracts,
    EquatableArray<string> Events);

/// <summary>
/// Reads contracts and implementations from symbols. The single source of truth for validity: the generator calls it
/// without a reporter and skips invalid input; <see cref="DaServerAnalyzer"/> calls it with one and reports.
/// </summary>
internal static class Shapes
{
    public static ContractKind? KindOf(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Interface || Markers.Contains(type.ToDisplayString())) return null;
        if (Implements(type, IActor)) return ContractKind.Actor;
        if (Implements(type, IService)) return ContractKind.Service;
        if (Implements(type, IClientCallback)) return ContractKind.ClientCallback;
        return null;
    }

    public static ContractModel? ReadContract(INamedTypeSymbol type, Report? report = null)
    {
        if (KindOf(type) is not { } kind) return null;
        bool valid = true;
        var location = type.Locations.FirstOrDefault();
        if (type.IsGenericType)
        {
            report?.Invoke(Rules.GenericMember, location, type.Name);
            valid = false;
        }

        var access = Implements(type, ISessionActor) ? ClientAccess.SessionKey
            : Implements(type, IPublicActor) ? ClientAccess.AnyKey
            : Implements(type, IPublicService) ? ClientAccess.Service
            : ClientAccess.None;
        if (kind == ContractKind.Actor && Implements(type, ISessionActor) && Implements(type, IPublicActor))
        {
            report?.Invoke(Rules.ConflictingMarkers, location, type.Name);
            valid = false;
        }

        // Methods of the interface and of the contract interfaces it extends (markers excluded).
        var methods = new List<MethodModel>();
        foreach (var iface in new[] { type }.Concat(type.AllInterfaces.Where(i => !Markers.Contains(i.ToDisplayString()))))
        foreach (var member in iface.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
            {
                var model = ReadMethod(type, method, kind, report);
                if (model == null) valid = false;
                else methods.Add(model);
            }
            else if (member is IPropertySymbol or IEventSymbol)
            {
                report?.Invoke(Rules.NoMethods, member.Locations.FirstOrDefault(), type.Name, member.Name);
                valid = false;
            }
        }

        if (!valid) return null;
        string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        return new ContractModel(ns, type.Name, type.ToDisplayString(Fqn), kind, Fnv1a(type.ToDisplayString()),
            access, new EquatableArray<MethodModel>(methods.ToArray()));
    }

    private static MethodModel? ReadMethod(INamedTypeSymbol contract, IMethodSymbol method, ContractKind kind, Report? report)
    {
        var location = method.Locations.FirstOrDefault();
        if (method.IsGenericMethod)
        {
            report?.Invoke(Rules.GenericMember, location, contract.Name + "." + method.Name);
            return null;
        }

        MethodShape shape;
        string resultType = "";
        var returnType = method.ReturnType as INamedTypeSymbol;
        string returnName = returnType?.ConstructedFrom.ToDisplayString() ?? "";
        if (method.ReturnsVoid) shape = MethodShape.OneWay;
        else if (returnName == "System.Threading.Tasks.ValueTask") shape = MethodShape.Task;
        else if (returnName == "System.Threading.Tasks.ValueTask<TResult>")
        {
            shape = MethodShape.TaskOfT;
            resultType = returnType!.TypeArguments[0].ToDisplayString(Fqn);
            if (!IsWireType(returnType.TypeArguments[0]))
            {
                report?.Invoke(Rules.NotWireType, location, returnType.TypeArguments[0].ToDisplayString(), contract.Name + "." + method.Name);
                return null;
            }
        }
        else
        {
            report?.Invoke(Rules.BadReturnType, location, method.Name, contract.Name);
            return null;
        }

        if (kind == ContractKind.ClientCallback && shape != MethodShape.OneWay)
        {
            report?.Invoke(Rules.CallbackMustBeVoid, location, method.Name, contract.Name);
            return null;
        }

        var parameters = new List<ParameterModel>();
        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.None || p.IsParams)
            {
                report?.Invoke(Rules.BadParameter, p.Locations.FirstOrDefault(), p.Name, method.Name);
                return null;
            }

            if (!IsWireType(p.Type))
            {
                report?.Invoke(Rules.NotWireType, p.Locations.FirstOrDefault(), p.Type.ToDisplayString(), contract.Name + "." + method.Name);
                return null;
            }

            parameters.Add(new ParameterModel(p.Type.ToDisplayString(Fqn), "@" + p.Name));
        }

        return new MethodModel(method.Name, MethodId(contract, method), shape, resultType,
            new EquatableArray<ParameterModel>(parameters.ToArray()));
    }

    /// <summary>
    /// Wire id: [WireId] override or FNV-1a of "Namespace.Contract.Method(ParamTypes)" (the contract, not the declaring
    /// interface, so a contract extending another one never registers duplicate ids).
    /// </summary>
    public static uint MethodId(INamedTypeSymbol contract, IMethodSymbol method)
    {
        var idAttribute = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == WireIdAttribute);
        return idAttribute?.ConstructorArguments.FirstOrDefault().Value is uint explicitId
            ? explicitId
            : Fnv1a($"{contract.ToDisplayString()}.{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})");
    }

    /// <summary>
    /// Types that cross the wire: primitives, strings, enums, arrays and BCL generics of wire types, and [NinoType]
    /// types. Anything else would fail at run time in the serializer, so it is a build error (DAS009).
    /// </summary>
    public static bool IsWireType(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsWireType(array.ElementType);
            case ITypeParameterSymbol:
                return false;
        }

        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum) return true;
        if (HasAttribute(type, NinoTypeAttribute)) return true;
        if (type is INamedTypeSymbol named && named.ContainingNamespace?.ToDisplayString() is { } ns &&
            (ns == "System" || ns.StartsWith("System.", System.StringComparison.Ordinal)))
            return named.TypeArguments.All(IsWireType);
        return false;
    }

    /// <summary>Event types handled by <paramref name="type"/> (its <c>IHandle&lt;T&gt;</c> interfaces).</summary>
    public static INamedTypeSymbol[] HandledEvents(INamedTypeSymbol type) =>
        type.AllInterfaces.Where(i => i.ConstructedFrom.ToDisplayString() == IHandle)
            .Select(i => i.TypeArguments[0]).OfType<INamedTypeSymbol>().ToArray();

    /// <summary>An event type (a struct implementing IEvent) that is not serializable (DAS009).</summary>
    public static void CheckEvent(INamedTypeSymbol type, Report? report)
    {
        if (type.TypeKind == TypeKind.Struct && Implements(type, IEvent) && !HasAttribute(type, NinoTypeAttribute))
            report?.Invoke(Rules.NotWireType, type.Locations.FirstOrDefault(), type.ToDisplayString(), "event " + type.Name);
    }

    public static HostedModel? ReadHosted(INamedTypeSymbol type, Report? report = null)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType) return null;

        bool isSystem = DerivesFrom(type, EcsSystem);
        var contracts = isSystem
            ? []
            : type.AllInterfaces.Where(i => KindOf(i) is ContractKind.Actor or ContractKind.Service).ToArray();
        var events = isSystem ? [] : HandledEvents(type);
        if (!isSystem && contracts.Length == 0 && events.Length == 0) return null;

        bool valid = true;
        var location = type.Locations.FirstOrDefault();
        bool actorContract = contracts.Any(c => KindOf(c) == ContractKind.Actor);
        bool isActor = actorContract || DerivesFrom(type, ActorBase);
        if (actorContract && !DerivesFrom(type, ActorBase))
        {
            report?.Invoke(Rules.ActorBase, location, type.Name, contracts.First(c => KindOf(c) == ContractKind.Actor).Name);
            valid = false;
        }

        if (isActor && contracts.Any(c => KindOf(c) == ContractKind.Service))
        {
            report?.Invoke(Rules.MixedKinds, location, type.Name);
            valid = false;
        }

        if (isActor)
        {
            foreach (var e in events.Where(e => !Implements(e, IKeyedEvent)))
            {
                report?.Invoke(Rules.UnkeyedActorEvent, location, type.Name, e.Name);
                valid = false;
            }
        }

        if (!type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            report?.Invoke(Rules.NoDefaultConstructor, location, type.Name);
            valid = false;
        }

        if (!valid) return null;
        return new HostedModel(type.ToDisplayString(Fqn), isSystem, isActor ? ContractKind.Actor : ContractKind.Service,
            new EquatableArray<string>(contracts.Select(c => c.ToDisplayString(Fqn)).ToArray()),
            new EquatableArray<string>(events.Select(e => e.ToDisplayString(Fqn)).OrderBy(e => e).ToArray()));
    }
}
