using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

internal enum ContractKind
{
    Service,
    Actor,
    Push,
}

internal enum MethodShape
{
    OneWay,
    Task,
    TaskOfT,
}

/// <summary>How clients may call a method (mirrors DaServer.Rpc.ClientAccess).</summary>
internal enum ClientAccess
{
    None,
    Service,
    SessionKey,
    AnyKey,
}

internal sealed record ParameterModel(string Type, string Name);

internal sealed record MethodModel(string Name, uint Id, MethodShape Shape, string ResultType,
    EquatableArray<ParameterModel> Parameters, ClientAccess Access);

/// <summary>Symbol-free description of an RPC contract (cached by the incremental pipeline).</summary>
internal sealed record ContractModel(string Namespace, string Name, string FullName, ContractKind Kind, uint Id,
    EquatableArray<MethodModel> Methods)
{
    public string Accessor => StripInterfacePrefix(Name);
    public string ProxyName => Accessor + "Proxy";
    public string RpcClassName => Accessor + "Rpc";
    public bool HasSessionMethods => Methods.Any(m => m.Access == ClientAccess.SessionKey);
}

/// <summary>A <c>[Handle]</c> method: the event it takes and whether it returns void.</summary>
internal sealed record HandlerModel(string EventType, string Method, bool ReturnsVoid);

/// <summary>Symbol-free description of a hosted implementation (contracts it serves, events it handles) or shard system.</summary>
internal sealed record HostedModel(string FullName, bool IsSystem, ContractKind Kind, EquatableArray<string> Contracts,
    EquatableArray<HandlerModel> Handlers);

/// <summary>A struct with a <c>[Key]</c> member: the generator implements DaServer.Rpc.IKeyed on it.</summary>
internal sealed record KeyedEventModel(string Namespace, string Declaration, string Name, string Member);

/// <summary>
/// Reads contracts, implementations and events from symbols. The single source of truth for validity: the generators
/// call it without a reporter and skip invalid input; <see cref="DaServerAnalyzer"/> calls it with one and reports.
/// </summary>
internal static class Shapes
{
    private static readonly string[] KindAttributes = [ServiceAttribute, ActorAttribute, PushAttribute];

    public static ContractKind? KindOf(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Interface) return null;
        if (HasAttribute(type, ActorAttribute)) return ContractKind.Actor;
        if (HasAttribute(type, ServiceAttribute)) return ContractKind.Service;
        if (HasAttribute(type, PushAttribute)) return ContractKind.Push;
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

        if (KindAttributes.Count(a => HasAttribute(type, a)) > 1)
        {
            report?.Invoke(Rules.BadContractAttributes, location, type.Name, "only one of [Service], [Actor] and [Push] applies");
            valid = false;
        }

        var interfaceClient = GetAttribute(type, ClientAttribute);

        // Methods of the interface and of the interfaces it extends.
        var methods = new List<MethodModel>();
        foreach (var iface in new[] { type }.Concat(type.AllInterfaces))
        foreach (var member in iface.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
            {
                var model = ReadMethod(type, method, kind, ClientAccessOf(type, kind, method, interfaceClient, report), report);
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
            new EquatableArray<MethodModel>(methods.ToArray()));
    }

    /// <summary>[Client] on the method, else on the interface; AnyKey only on actors; never on [Push] contracts.</summary>
    private static ClientAccess? ClientAccessOf(INamedTypeSymbol contract, ContractKind kind, IMethodSymbol method,
        AttributeData? interfaceClient, Report? report)
    {
        var client = GetAttribute(method, ClientAttribute) ?? interfaceClient;
        if (client == null) return ClientAccess.None;
        bool anyKey = client.NamedArguments.Any(a => a.Key == "AnyKey" && a.Value.Value is true);
        string? problem = kind == ContractKind.Push ? "[Client] applies to [Service] and [Actor] methods, not [Push]"
            : kind == ContractKind.Service && anyKey ? "AnyKey applies to actor methods only"
            : null;
        if (problem != null)
        {
            report?.Invoke(Rules.BadContractAttributes, method.Locations.FirstOrDefault(), contract.Name + "." + method.Name, problem);
            return null;
        }

        return kind == ContractKind.Service ? ClientAccess.Service : anyKey ? ClientAccess.AnyKey : ClientAccess.SessionKey;
    }

    private static MethodModel? ReadMethod(INamedTypeSymbol contract, IMethodSymbol method, ContractKind kind,
        ClientAccess? access, Report? report)
    {
        if (access == null) return null;
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

        if (kind == ContractKind.Push && shape != MethodShape.OneWay)
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
            new EquatableArray<ParameterModel>(parameters.ToArray()), access.Value);
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

    /// <summary>
    /// <c>[Handle]</c> methods of <paramref name="type"/>: one event parameter (a [NinoType] struct), void or ValueTask,
    /// reachable from generated code, one handler per event type. Null when one is invalid.
    /// </summary>
    private static List<(HandlerModel Model, ITypeSymbol Event)>? ReadHandlers(INamedTypeSymbol type, Report? report)
    {
        var handlers = new List<(HandlerModel, ITypeSymbol)>();
        bool valid = true;
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!HasAttribute(method, HandleAttribute)) continue;
            var location = method.Locations.FirstOrDefault();
            string name = type.Name + "." + method.Name;
            bool returnsVoid = method.ReturnsVoid;
            var e = method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None ? method.Parameters[0].Type : null;
            if (e == null || method.IsStatic || method.IsGenericMethod || method.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected ||
                !(returnsVoid || method.ReturnType.ToDisplayString() == "System.Threading.Tasks.ValueTask"))
            {
                report?.Invoke(Rules.BadHandler, location, name, "take one event parameter by value, return void or ValueTask and be public or internal");
                valid = false;
            }
            else if (e.TypeKind != TypeKind.Struct || !HasAttribute(e, NinoTypeAttribute))
            {
                report?.Invoke(Rules.NotWireType, location, e.ToDisplayString(), "event handler " + name);
                valid = false;
            }
            else if (handlers.Any(h => SymbolEqualityComparer.Default.Equals(h.Item2, e)))
            {
                report?.Invoke(Rules.BadHandler, location, name, "handle each event type once (" + e.Name + " has another handler)");
                valid = false;
            }
            else
            {
                handlers.Add((new HandlerModel(e.ToDisplayString(Fqn), method.Name, returnsVoid), e));
            }
        }

        return valid ? handlers : null;
    }

    /// <summary>A [Key] member, or IKeyed implemented by hand (the runtime's own SessionClosed).</summary>
    public static bool IsKeyed(ITypeSymbol type) =>
        KeyMember(type) != null || type.AllInterfaces.Any(i => i.ToDisplayString() == "DaServer.Rpc.IKeyed");

    private static ISymbol? KeyMember(ITypeSymbol type) =>
        type.GetMembers().FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol && HasAttribute(m, KeyAttribute));

    /// <summary>A struct with a [Key] member: an integer member of a partial, non-nested struct, one per type.</summary>
    public static KeyedEventModel? ReadKeyedEvent(INamedTypeSymbol type, Report? report = null)
    {
        var keys = type.GetMembers().Where(m => m is IFieldSymbol or IPropertySymbol && HasAttribute(m, KeyAttribute)).ToArray();
        if (keys.Length == 0) return null;
        var key = keys[0];
        var keyType = key is IFieldSymbol f ? f.Type : ((IPropertySymbol)key).Type;
        bool partial = type.DeclaringSyntaxReferences.All(r =>
            r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax d && d.Modifiers.Any(m => m.ValueText == "partial"));
        string? problem = keys.Length > 1 ? "an event has one [Key]"
            : type.TypeKind != TypeKind.Struct || !partial || type.ContainingType != null || type.IsGenericType
                ? "[Key] belongs to a partial, non-nested, non-generic struct"
            : keyType.SpecialType is not (SpecialType.System_Int64 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
                SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte)
                ? "the key must be a long (or a smaller integer)"
            : null;
        if (problem != null)
        {
            report?.Invoke(Rules.BadKey, key.Locations.FirstOrDefault(), type.Name + "." + key.Name, problem);
            return null;
        }

        string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        string declaration = (type.IsRecord ? "partial record struct " : "partial struct ") + type.Name;
        return new KeyedEventModel(ns, declaration, type.Name, key.Name);
    }

    public static HostedModel? ReadHosted(INamedTypeSymbol type, Report? report = null)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType) return null;

        bool isSystem = DerivesFrom(type, EcsSystem);
        var contracts = isSystem
            ? []
            : type.AllInterfaces.Where(i => KindOf(i) is ContractKind.Actor or ContractKind.Service).ToArray();
        bool isActorType = DerivesFrom(type, ActorBase);
        if (!isSystem && contracts.Length == 0 && !isActorType && !DerivesFrom(type, ServiceBase)) return null;

        var handlers = isSystem ? new List<(HandlerModel Model, ITypeSymbol Event)>() : ReadHandlers(type, report);
        bool valid = handlers != null;
        var location = type.Locations.FirstOrDefault();
        bool actorContract = contracts.Any(c => KindOf(c) == ContractKind.Actor);
        bool isActor = actorContract || isActorType;
        if (actorContract && !isActorType)
        {
            report?.Invoke(Rules.ActorBase, location, type.Name, contracts.First(c => KindOf(c) == ContractKind.Actor).Name);
            valid = false;
        }

        if (isActor && (contracts.Any(c => KindOf(c) == ContractKind.Service) || DerivesFrom(type, ServiceBase)))
        {
            report?.Invoke(Rules.MixedKinds, location, type.Name);
            valid = false;
        }

        if (isActor && handlers != null)
        {
            foreach (var h in handlers.Where(h => !IsKeyed(h.Event)))
            {
                report?.Invoke(Rules.UnkeyedActorEvent, location, type.Name, h.Event.Name);
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
            new EquatableArray<HandlerModel>(handlers!.Select(h => h.Model).OrderBy(h => h.EventType).ToArray()));
    }
}
