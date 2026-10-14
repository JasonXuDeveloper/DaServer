using System.Collections.Generic;
using System.Collections.Immutable;
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
internal sealed record ContractModel(string Namespace, string Name, string FullName, ContractKind Kind, uint Id,
    bool ClientAccessible, bool AnyKey, EquatableArray<MethodModel> Methods)
{
    public string Accessor => StripInterfacePrefix(Name);
    public string ProxyName => Accessor + "Proxy";
    public string RpcClassName => Accessor + "Rpc";
}

/// <summary>Symbol-free description of a hosted implementation or shard system.</summary>
internal sealed record HostedModel(string FullName, bool IsSystem, ContractKind Kind, EquatableArray<string> Contracts,
    EquatableArray<string> Roles);

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

        var accessible = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "DaServer.Rpc.ClientAccessibleAttribute");
        bool anyKey = accessible?.NamedArguments.Any(a => a.Key == "AnyKey" && a.Value.Value is true) == true;

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
            accessible != null, anyKey, new EquatableArray<MethodModel>(methods.ToArray()));
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

            parameters.Add(new ParameterModel(p.Type.ToDisplayString(Fqn), "@" + p.Name));
        }

        return new MethodModel(method.Name, MethodId(contract, method), shape, resultType,
            new EquatableArray<ParameterModel>(parameters.ToArray()));
    }

    /// <summary>
    /// Wire id: [RpcId] override or FNV-1a of "Namespace.Contract.Method(ParamTypes)" (the contract, not the declaring
    /// interface, so a contract extending another one never registers duplicate ids).
    /// </summary>
    public static uint MethodId(INamedTypeSymbol contract, IMethodSymbol method)
    {
        var idAttribute = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "DaServer.Rpc.RpcIdAttribute");
        return idAttribute?.ConstructorArguments.FirstOrDefault().Value is uint explicitId
            ? explicitId
            : Fnv1a($"{contract.ToDisplayString()}.{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})");
    }

    public static HostedModel? ReadHosted(INamedTypeSymbol type, Report? report = null)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType) return null;

        bool isSystem = DerivesFrom(type, EcsSystem);
        var contracts = isSystem
            ? []
            : type.AllInterfaces.Where(i => KindOf(i) is ContractKind.Actor or ContractKind.Service).ToArray();
        if (!isSystem && contracts.Length == 0) return null;

        bool valid = true;
        var location = type.Locations.FirstOrDefault();
        bool isActor = contracts.Any(c => KindOf(c) == ContractKind.Actor);
        if (isActor && !DerivesFrom(type, ActorBase))
        {
            report?.Invoke(Rules.ActorBase, location, type.Name, contracts[0].Name);
            valid = false;
        }

        if (!type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            report?.Invoke(Rules.NoDefaultConstructor, location, type.Name);
            valid = false;
        }

        if (!valid) return null;
        var roles = type.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == RoleAttribute)
            .SelectMany(a => a.ConstructorArguments.SelectMany(arg => arg.Kind == TypedConstantKind.Array ? arg.Values : ImmutableArray.Create(arg)))
            .Select(v => v.Value as string).Where(v => v != null).Select(v => v!).ToArray();
        return new HostedModel(type.ToDisplayString(Fqn), isSystem, isActor ? ContractKind.Actor : ContractKind.Service,
            new EquatableArray<string>(contracts.Select(c => c.ToDisplayString(Fqn)).ToArray()),
            new EquatableArray<string>(roles));
    }
}
