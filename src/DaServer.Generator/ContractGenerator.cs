using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For every interface deriving from IService / IActor / IClientCallback:
/// a struct proxy (serializes arguments straight into a pooled frame), a dispatcher switch (deserializes and invokes),
/// wire ids, registration in a module initializer, and invoker accessors (<c>runtime.Player(key)</c>).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ContractGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InterfaceDeclarationSyntax { BaseList: not null },
                static (ctx, _) => ContractModel.TryCreate((INamedTypeSymbol)ctx.SemanticModel.GetDeclaredSymbol(ctx.Node)!))
            .Where(static m => m != null)
            .Select(static (m, _) => m!);

        // Distinct: a partial interface declared in several files yields the same model more than once.
        var unique = contracts.Collect().SelectMany(static (all, _) => all.Distinct());
        context.RegisterSourceOutput(unique, static (spc, contract) => Emit(spc, contract));

        // Wire-id collisions across all contracts of the compilation.
        context.RegisterSourceOutput(contracts.Collect(), static (spc, all) =>
        {
            var seen = new Dictionary<uint, string>();
            foreach (var contract in all.Distinct())
            foreach (var method in contract.Methods)
            {
                string name = contract.FullName + "." + method.Name;
                if (seen.TryGetValue(method.Id, out var other) && other != name)
                    spc.ReportDiagnostic(Diagnostic.Create(Rules.IdCollision, method.Location?.ToLocation(), name, other));
                else
                    seen[method.Id] = name;
            }

            // Marker so host assemblies can find (and force-register) this contracts assembly.
            var first = all.Distinct().Where(c => c.Diagnostics.Length == 0).OrderBy(c => c.FullName).FirstOrDefault();
            if (first != null)
                spc.AddSource("RpcContractsAssembly.g.cs",
                    $"{Header}[assembly: global::DaServer.Rpc.RpcContractsAssembly(typeof({first.Namespace}{(first.Namespace.Length > 0 ? "." : "")}{first.RpcClassName}))]\n");
        });
    }

    private static void Emit(SourceProductionContext spc, ContractModel c)
    {
        foreach (var d in c.Diagnostics) spc.ReportDiagnostic(d.ToDiagnostic());
        if (c.Diagnostics.Length > 0) return;

        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing System.Threading.Tasks;\nusing DaServer.Buffers;\nusing DaServer.Rpc;\nusing Nino.Core;\n\n");
        if (c.Namespace.Length > 0) sb.Append($"namespace {c.Namespace};\n\n");

        EmitProxy(sb, c);
        EmitRpcClass(sb, c);
        spc.AddSource($"{HintName(c.FullName)}.Rpc.g.cs", sb.ToString());
    }

    private static string TargetKind(ContractModel c) => c.Kind switch
    {
        "Actor" => "RpcTargetKind.Actor",
        "Service" => "RpcTargetKind.Service",
        _ => "_kind",
    };

    private static void EmitProxy(StringBuilder sb, ContractModel c)
    {
        bool callback = c.Kind == "ClientCallback";
        sb.Append($"/// <summary>Allocation-free proxy of <see cref=\"{c.Name}\"/> (generated). Obtain it from the invoker accessor.</summary>\n");
        sb.Append($"public readonly struct {c.ProxyName} : {c.FullName}\n{{\n");
        sb.Append("    private readonly IRpcInvoker _invoker;\n    private readonly long _key;\n");
        if (callback) sb.Append("    private readonly RpcTargetKind _kind;\n    private readonly ulong _gateway;\n");
        sb.Append('\n');

        if (callback)
            sb.Append($"    public {c.ProxyName}(IRpcInvoker invoker, RpcTargetKind kind, long key, ulong gateway)\n" +
                      "    {\n        _invoker = invoker;\n        _kind = kind;\n        _key = key;\n        _gateway = gateway;\n    }\n\n");
        else
            sb.Append($"    public {c.ProxyName}(IRpcInvoker invoker, long key)\n    {{\n        _invoker = invoker;\n        _key = key;\n    }}\n\n");

        foreach (var m in c.Methods)
        {
            string parameters = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
            sb.Append($"    public {m.ReturnType} {m.Name}({parameters})\n    {{\n");
            sb.Append("        var __frame = FrameWriter.Rent();\n");
            if (m.Parameters.Length > 0)
            {
                sb.Append("        var __writer = __frame.Nino;\n");
                foreach (var p in m.Parameters) sb.Append($"        NinoSerializer.Serialize({p.Name}, ref __writer);\n");
            }

            string kind = TargetKind(c);
            string id = $"0x{m.Id:X8}u";
            sb.Append(m.Shape switch
            {
                MethodShape.OneWay => callback
                    ? $"        _invoker.Send(__frame, {id}, {kind}, _key, _gateway);\n"
                    : $"        _invoker.Send(__frame, {id}, {kind}, _key);\n",
                MethodShape.Task => $"        return _invoker.CallVoid(__frame, {id}, {kind}, _key);\n",
                _ => $"        return _invoker.Call<{m.ResultType}>(__frame, {id}, {kind}, _key);\n",
            });
            sb.Append("    }\n\n");
        }

        sb.Length -= 1;
        sb.Append("}\n\n");
    }

    private static void EmitRpcClass(StringBuilder sb, ContractModel c)
    {
        sb.Append($"/// <summary>Wire ids, registration, dispatcher and invoker accessors of <see cref=\"{c.Name}\"/> (generated).</summary>\n");
        sb.Append($"public static class {c.RpcClassName}\n{{\n");
        sb.Append($"    public const uint ContractId = 0x{c.Id:X8}u;\n\n");
        foreach (var m in c.Methods) sb.Append($"    public const uint {m.Name}Id = 0x{m.Id:X8}u;\n");
        sb.Append('\n');

        // Registration
        sb.Append("    [ModuleInitializer]\n    internal static void Register() => RpcRegistry.Register(new RpcContractInfo(\n");
        sb.Append($"        typeof({c.FullName}), ContractId, RpcContractKind.{c.Kind}, {Bool(c.ClientAccessible)}, {Bool(c.AnyKey)}, Dispatch,\n");
        sb.Append("        [\n");
        foreach (var m in c.Methods)
            sb.Append($"            new RpcMethodInfo({m.Name}Id, \"{m.Name}\", {Bool(m.Shape == MethodShape.OneWay)}),\n");
        sb.Append("        ]));\n\n");

        // Dispatcher
        sb.Append("    private static ValueTask Dispatch(object target, uint methodId, ref Reader reader, in CallContext context)\n    {\n");
        sb.Append($"        var impl = Unsafe.As<{c.FullName}>(target);\n        switch (methodId)\n        {{\n");
        foreach (var m in c.Methods)
        {
            sb.Append($"            case {m.Name}Id:\n            {{\n");
            for (int i = 0; i < m.Parameters.Length; i++)
                sb.Append($"                NinoDeserializer.Deserialize(out {m.Parameters[i].Type} __a{i}, ref reader);\n");
            string call = $"impl.{m.Name}({string.Join(", ", Enumerable.Range(0, m.Parameters.Length).Select(i => $"__a{i}"))})";
            sb.Append(m.Shape == MethodShape.OneWay
                ? $"                {call};\n                return default;\n"
                : $"                return context.Reply({call});\n");
            sb.Append("            }\n");
        }

        sb.Append("            default:\n                return context.UnknownMethod();\n        }\n    }\n\n");

        // Accessors
        string accessor = StripInterfacePrefix(c.Name);
        switch (c.Kind)
        {
            case "Actor":
                sb.Append($"    /// <summary>Proxy to the <see cref=\"{c.Name}\"/> actor with key <paramref name=\"key\"/>.</summary>\n");
                sb.Append($"    public static {c.ProxyName} {accessor}(this IRpcInvoker invoker, long key) => new(invoker, key);\n");
                break;
            case "Service":
                sb.Append($"    /// <summary>Proxy to the <see cref=\"{c.Name}\"/> service (load-balanced, local node preferred).</summary>\n");
                sb.Append($"    public static {c.ProxyName} {accessor}(this IRpcInvoker invoker) => new(invoker, 0);\n");
                break;
            default:
                sb.Append($"    /// <summary>Push to one client session.</summary>\n");
                sb.Append($"    public static {c.ProxyName} {accessor}(this IRpcInvoker invoker, SessionRef session) =>\n");
                sb.Append("        new(invoker, RpcTargetKind.Session, session.SessionId, session.GatewayNodeId);\n\n");
                sb.Append($"    /// <summary>Broadcast to every session of a group (0 = all sessions), serialized once per call.</summary>\n");
                sb.Append($"    public static {c.ProxyName} {accessor}Group(this IRpcInvoker invoker, long groupId) =>\n");
                sb.Append("        new(invoker, RpcTargetKind.Group, groupId, 0);\n");
                break;
        }

        sb.Append("}\n");
    }

    private static string Bool(bool value) => value ? "true" : "false";

    // ---------------- model ----------------

    internal enum MethodShape
    {
        OneWay,
        Task,
        TaskOfT,
    }

    internal sealed record ParameterModel(string Type, string Name);

    internal sealed record MethodModel(string Name, uint Id, MethodShape Shape, string ReturnType, string ResultType,
        EquatableArray<ParameterModel> Parameters, LocationInfo? Location);

    internal sealed record ContractModel(string Namespace, string Name, string FullName, string Kind, uint Id,
        bool ClientAccessible, bool AnyKey, EquatableArray<MethodModel> Methods, EquatableArray<DiagnosticInfo> Diagnostics)
    {
        public string ProxyName => StripInterfacePrefix(Name) + "Proxy";

        public string RpcClassName => StripInterfacePrefix(Name) + "Rpc";

        private static readonly string[] Markers = ["DaServer.Rpc.IService", "DaServer.Rpc.IActor", "DaServer.Rpc.IClientCallback"];

        public static ContractModel? TryCreate(INamedTypeSymbol type)
        {
            string self = type.ToDisplayString();
            if (Markers.Contains(self)) return null;

            string? kind = null;
            if (Implements(type, "DaServer.Rpc.IActor")) kind = "Actor";
            else if (Implements(type, "DaServer.Rpc.IService")) kind = "Service";
            else if (Implements(type, "DaServer.Rpc.IClientCallback")) kind = "ClientCallback";
            if (kind == null) return null;

            var diagnostics = new List<DiagnosticInfo>();
            var location = LocationInfo.From(type.Locations.FirstOrDefault());
            if (type.IsGenericType)
                diagnostics.Add(new DiagnosticInfo(Rules.GenericMember, location, type.Name, ""));

            var attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "DaServer.Rpc.ClientAccessibleAttribute");
            bool anyKey = attribute?.NamedArguments.Any(a => a.Key == "AnyKey" && a.Value.Value is true) == true;

            // Methods of the interface and of the contract interfaces it extends (markers excluded).
            var interfaces = new[] { type }.Concat(type.AllInterfaces.Where(i => !Markers.Contains(i.ToDisplayString())));
            var methods = new List<MethodModel>();
            foreach (var iface in interfaces)
            foreach (var member in iface.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } method)
                {
                    var model = CreateMethod(type, method, kind, diagnostics);
                    if (model != null) methods.Add(model);
                }
                else if (member is IPropertySymbol or IEventSymbol)
                {
                    diagnostics.Add(new DiagnosticInfo(Rules.NoMethods, LocationInfo.From(member.Locations.FirstOrDefault()), type.Name, member.Name));
                }
            }

            string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
            return new ContractModel(ns, type.Name, type.ToDisplayString(Fqn), kind, Fnv1a(self), attribute != null, anyKey,
                new EquatableArray<MethodModel>(methods.ToArray()), new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        private static MethodModel? CreateMethod(INamedTypeSymbol contract, IMethodSymbol method,
            string kind, List<DiagnosticInfo> diagnostics)
        {
            var location = LocationInfo.From(method.Locations.FirstOrDefault());
            if (method.IsGenericMethod)
            {
                diagnostics.Add(new DiagnosticInfo(Rules.GenericMember, location, contract.Name + "." + method.Name, ""));
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
                diagnostics.Add(new DiagnosticInfo(Rules.BadReturnType, location, method.Name, contract.Name));
                return null;
            }

            if (kind == "ClientCallback" && shape != MethodShape.OneWay)
            {
                diagnostics.Add(new DiagnosticInfo(Rules.CallbackMustBeVoid, location, method.Name, contract.Name));
                return null;
            }

            var parameters = new List<ParameterModel>();
            foreach (var p in method.Parameters)
            {
                if (p.RefKind != RefKind.None || p.IsParams)
                {
                    diagnostics.Add(new DiagnosticInfo(Rules.BadParameter, LocationInfo.From(p.Locations.FirstOrDefault()), p.Name, method.Name));
                    return null;
                }

                parameters.Add(new ParameterModel(p.Type.ToDisplayString(Fqn), "@" + p.Name));
            }

            // Wire id: [RpcId] override or FNV-1a of "Namespace.Contract.Method(ParamTypes)" (the contract, not the
            // declaring interface, so a contract extending another one never registers duplicate ids).
            var idAttribute = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "DaServer.Rpc.RpcIdAttribute");
            uint id = idAttribute?.ConstructorArguments.FirstOrDefault().Value is uint explicitId
                ? explicitId
                : Fnv1a($"{contract.ToDisplayString()}.{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString()))})");

            return new MethodModel(method.Name, id, shape, method.ReturnType.ToDisplayString(Fqn), resultType,
                new EquatableArray<ParameterModel>(parameters.ToArray()), location);
        }
    }
}
