using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For every contract interface ([Service], [Actor] or [ToClient]): a struct proxy (serializes arguments straight
/// into a pooled frame), a dispatcher switch (deserializes and invokes), wire ids, registration in a module
/// initializer, and cluster accessors (<c>Cluster.Player(key)</c>, <c>Cluster.PlayerClient(session or group)</c>).
/// For every struct with a [ActorKey] member: the DaServer.Rpc.IKeyed implementation that routes the event to the actor of
/// that key. Incremental rules: ForAttributeWithMetadataName or syntax-only predicates, symbol-free equatable models,
/// invalid input skipped (DaServerAnalyzer reports it), tracking names on every stage.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ContractGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Distinct: a partial interface declared in several files yields the same model more than once.
        var contracts = ContractsWith(context, ServiceAttribute).Collect()
            .Combine(ContractsWith(context, ActorAttribute).Collect())
            .Combine(ContractsWith(context, ToClientAttribute).Collect())
            .SelectMany(static (all, _) => all.Left.Left.Concat(all.Left.Right).Concat(all.Right).Distinct())
            .WithTrackingName("Contracts");
        context.RegisterSourceOutput(contracts, static (spc, contract) =>
            spc.AddSource($"{HintName(contract.FullName)}.Rpc.g.cs", SourceText.From(Emit(contract), Encoding.UTF8)));

        // Not ForAttributeWithMetadataName: [property: ActorKey] on a positional record parameter is not reported there.
        var keyedEvents = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax &&
                                                      ((TypeDeclarationSyntax)node).Modifiers.Any(m => m.ValueText == "partial"),
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type ? Shapes.ReadKeyedEvent(type) : null)
            .Where(static m => m is not null)
            .Collect()
            .SelectMany(static (all, _) => all.Distinct())
            .WithTrackingName("KeyedEvents");
        context.RegisterSourceOutput(keyedEvents, static (spc, e) =>
            spc.AddSource($"{HintName((e!.Namespace.Length > 0 ? e.Namespace + "." : "") + e.Name)}.Key.g.cs",
                SourceText.From(EmitKeyed(e), Encoding.UTF8)));

        // Marker so hosts can force-run this assembly's registrations (it only depends on "any contract" + the name).
        var marker = contracts.Collect()
            .Select(static (all, _) => all.Length > 0)
            .Combine(context.CompilationProvider.Select(static (c, _) => Identifier(c.AssemblyName ?? "Contracts")))
            .WithTrackingName("ContractsMarker");
        context.RegisterSourceOutput(marker, static (spc, pair) =>
        {
            if (!pair.Left) return;
            string anchor = $"DaServer.Generated.Contracts_{pair.Right}";
            spc.AddSource("RpcContractsAssembly.g.cs", SourceText.From(
                $"{Header}[assembly: global::DaServer.Rpc.RpcContractsAssembly(typeof(global::{anchor}))]\n\n" +
                $"namespace DaServer.Generated\n{{\n    /// <summary>Anchor of this contracts assembly (referenced by generated registration code).</summary>\n    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n    public static class Contracts_{pair.Right} {{ }}\n}}\n",
                Encoding.UTF8));
        });
    }

    private static IncrementalValuesProvider<ContractModel> ContractsWith(IncrementalGeneratorInitializationContext context,
        string attribute) =>
        context.SyntaxProvider
            .ForAttributeWithMetadataName(attribute, static (node, _) => node is InterfaceDeclarationSyntax,
                static (ctx, _) => Shapes.ReadContract((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)!;

    private static string EmitKeyed(KeyedEventModel e)
    {
        var sb = new StringBuilder(Header);
        if (e.Namespace.Length > 0) sb.Append($"namespace {e.Namespace};\n\n");
        sb.Append($"{e.Declaration} : global::DaServer.Rpc.IKeyed\n{{\n");
        sb.Append($"    long global::DaServer.Rpc.IKeyed.EventKey => {e.Member};\n}}\n");
        return sb.ToString();
    }

    private static string Emit(ContractModel c)
    {
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing System.Threading.Tasks;\nusing DaServer.Buffers;\nusing DaServer.Rpc;\nusing Nino.Core;\n\n");
        if (c.Namespace.Length > 0) sb.Append($"namespace {c.Namespace};\n\n");
        EmitProxy(sb, c);
        EmitRpcClass(sb, c);
        return sb.ToString();
    }

    private static void EmitProxy(StringBuilder sb, ContractModel c)
    {
        bool callback = c.Kind == ContractKind.ToClient;
        string kind = c.Kind switch
        {
            ContractKind.Actor => "RpcTargetKind.Actor",
            ContractKind.Service => "RpcTargetKind.Service",
            _ => "_kind",
        };

        sb.Append($"/// <summary>Allocation-free proxy of <see cref=\"{c.Name}\"/> (generated). Obtain it from the cluster accessor.</summary>\n");
        sb.Append($"public readonly struct {c.ProxyName} : {c.FullName}\n{{\n");
        sb.Append("    private readonly IRpcInvoker _invoker;\n    private readonly long _key;\n");
        if (callback)
        {
            sb.Append("    private readonly RpcTargetKind _kind;\n    private readonly ulong _gateway;\n\n");
            sb.Append($"    internal {c.ProxyName}(IRpcInvoker invoker, RpcTargetKind kind, long key, ulong gateway)\n");
            sb.Append("    {\n        _invoker = invoker;\n        _kind = kind;\n        _key = key;\n        _gateway = gateway;\n    }\n");
        }
        else
        {
            sb.Append($"\n    internal {c.ProxyName}(IRpcInvoker invoker, long key)\n    {{\n        _invoker = invoker;\n        _key = key;\n    }}\n");
        }

        foreach (var m in c.Methods)
        {
            string parameters = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
            string returns = m.Shape switch
            {
                MethodShape.OneWay => "void",
                MethodShape.Task => "ValueTask",
                _ => $"ValueTask<{m.ResultType}>",
            };
            sb.Append($"\n    public {returns} {m.Name}({parameters})\n    {{\n        var __frame = FrameWriter.Rent();\n");
            if (m.Parameters.Length > 0)
            {
                sb.Append("        var __writer = __frame.Nino;\n");
                foreach (var p in m.Parameters) sb.Append($"        NinoSerializer.Serialize({p.Name}, ref __writer);\n");
            }

            string id = $"0x{m.Id:X8}u";
            sb.Append(m.Shape switch
            {
                MethodShape.OneWay => callback
                    ? $"        _invoker.Send(__frame, {id}, {kind}, _key, _gateway);\n"
                    : $"        _invoker.Send(__frame, {id}, {kind}, _key);\n",
                MethodShape.Task => $"        return _invoker.CallVoid(__frame, {id}, {kind}, _key);\n",
                _ => $"        return _invoker.Call<{m.ResultType}>(__frame, {id}, {kind}, _key);\n",
            });
            sb.Append("    }\n");
        }

        sb.Append("}\n\n");
    }

    private static void EmitRpcClass(StringBuilder sb, ContractModel c)
    {
        sb.Append($"/// <summary>Wire ids, registration, dispatcher and cluster accessors of <see cref=\"{c.Name}\"/> (generated).</summary>\n");
        sb.Append($"public static class {c.RpcClassName}\n{{\n");
        sb.Append($"    public const uint ContractId = 0x{c.Id:X8}u;\n\n");
        foreach (var m in c.Methods) sb.Append($"    public const uint {m.Name}Id = 0x{m.Id:X8}u;\n");

        sb.Append("\n    [ModuleInitializer]\n    internal static void Register() => RpcRegistry.Register(new RpcContractInfo(\n");
        sb.Append($"        typeof({c.FullName}), ContractId, RpcContractKind.{c.Kind}, Dispatch,\n        [\n");
        foreach (var m in c.Methods)
            sb.Append($"            new RpcMethodInfo({m.Name}Id, \"{m.Name}\", {Bool(m.Shape == MethodShape.OneWay)}, ClientRoute.{m.Route}),\n");
        sb.Append("        ]));\n\n");

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

        switch (c.Kind)
        {
            case ContractKind.Actor:
                sb.Append($"    /// <summary>Proxy to the <see cref=\"{c.Name}\"/> actor with key <paramref name=\"key\"/> (activated on demand).</summary>\n");
                sb.Append($"    public static {c.ProxyName} {c.Accessor}(this IRpcInvoker cluster, long key) => new(cluster, key);\n");
                if (c.HasSessionMethods)
                {
                    sb.Append($"\n    /// <summary>Client side: the <see cref=\"{c.Name}\"/> actor bound to this client's session by the server.</summary>\n");
                    sb.Append($"    public static {c.ProxyName} {c.Accessor}(this DaServer.Client.ClusterClient client) => new(client, 0);\n");
                }

                break;
            case ContractKind.Service:
                sb.Append($"    /// <summary>Proxy to the <see cref=\"{c.Name}\"/> service (load-balanced, local node preferred).</summary>\n");
                sb.Append($"    public static {c.ProxyName} {c.Accessor}(this IRpcInvoker cluster) => new(cluster, 0);\n");
                break;
            default:
                sb.Append("    /// <summary>Push to one client session.</summary>\n");
                sb.Append($"    public static {c.ProxyName} {c.Accessor}(this IRpcInvoker cluster, SessionRef session) =>\n");
                sb.Append("        new(cluster, RpcTargetKind.Session, session.SessionId, session.GatewayNodeId);\n\n");
                sb.Append("    /// <summary>Broadcast to every session of a group on every gateway (serialized once).</summary>\n");
                sb.Append($"    public static {c.ProxyName} {c.Accessor}(this IRpcInvoker cluster, ClientGroup group) =>\n");
                sb.Append("        new(cluster, RpcTargetKind.Group, group.Id, 0);\n");
                break;
        }

        sb.Append("}\n");
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
