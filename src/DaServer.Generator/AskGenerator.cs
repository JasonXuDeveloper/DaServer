using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For every request struct (<c>IRequest&lt;TReply&gt;</c>): a static class <c>{Request}Ask</c> of typed <c>Ask</c>
/// overloads on <c>IMessageSender</c> (<c>await Cluster.Ask(playerId, new GetInfo())</c> returns the reply type without
/// boxing the request). Incremental rules: the compiler's attribute index ([Message]), symbol-free equatable models, invalid input skipped
/// (DaServerAnalyzer reports it), tracking names on every stage.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AskGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // A partial struct declared in several files yields the same model more than once: Distinct.
        var requests = context.SyntaxProvider
            .ForAttributeWithMetadataName(MessageAttribute, static (node, _) => node is StructDeclarationSyntax or
                    RecordDeclarationSyntax { ClassOrStructKeyword.ValueText: "struct" },
                static (ctx, _) => Shapes.ReadRequest((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .Collect()
            .SelectMany(static (all, _) => all.Distinct())
            .WithTrackingName("Requests");
        var server = context.CompilationProvider.Select(static (c, _) => IsServer(c));
        context.RegisterSourceOutput(requests.Combine(server), static (spc, pair) =>
            spc.AddSource($"DaServer.Ask.{HintName(pair.Left!.FullName)}.g.cs", SourceText.From(EmitAsk(pair.Left!, pair.Right), Encoding.UTF8)));

        // Handlers ask through their Context (ADR-080): the answer comes back as mail. One internal class per server
        // compilation covers its own requests and those of the contracts it references (they are built without the
        // runtime), so the typed overloads exist wherever handlers are, without clashing between assemblies.
        var contextAsks = context.CompilationProvider
            .Select(static (c, _) => IsServer(c) ? new EquatableArray<RequestModel>(VisibleRequests(c)) : default)
            .WithTrackingName("ContextAsks");
        context.RegisterSourceOutput(contextAsks, static (spc, all) =>
        {
            if (all.Length > 0) spc.AddSource("DaServer.ContextAsks.g.cs", SourceText.From(EmitContextAsks(all), Encoding.UTF8));
        });
    }

    /// <summary>The public request types of this compilation and of the application assemblies it references.</summary>
    private static RequestModel[] VisibleRequests(Compilation compilation)
    {
        var found = new List<RequestModel>();
        void Walk(INamespaceSymbol ns, bool own)
        {
            foreach (var type in ns.GetTypeMembers())
                if (type is { TypeKind: TypeKind.Struct, IsGenericType: false } && (own || type.DeclaredAccessibility == Accessibility.Public) &&
                    HasAttribute(type, MessageAttribute) && Shapes.ReadRequest(type) is { } request)
                    found.Add(request);
            foreach (var inner in ns.GetNamespaceMembers()) Walk(inner, own);
        }

        Walk(compilation.Assembly.GlobalNamespace, own: true);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            if (assembly.Name != "DaServer" && assembly.Name != "DaServer.Core" && assembly.Name != "DaServer.Abstractions" &&
                assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => r.Name == "DaServer.Abstractions")))
                Walk(assembly.GlobalNamespace, own: false);
        return found.Distinct().OrderBy(r => r.FullName, StringComparer.Ordinal).ToArray();
    }

    private static string EmitContextAsks(EquatableArray<RequestModel> requests)
    {
        var sb = new StringBuilder(Header);
        sb.Append("namespace DaServer.Runtime;\n\n"); // where Context is: in scope wherever handlers are
        sb.Append("/// <summary>Typed asks of handlers: the answer comes back as <c>Answered&lt;TRequest, TReply&gt;</c> mail (generated).</summary>\n");
        sb.Append("internal static class ContextAsks\n{\n");
        foreach (var r in requests)
        {
            sb.Append($"    public static uint Ask<TAddr>(this in global::DaServer.Runtime.Context context, TAddr address, in {r.FullName} request, global::DaServer.Runtime.ReplyTo forward = default)\n");
            sb.Append("        where TAddr : struct, global::DaServer.IAddress =>\n");
            sb.Append($"        context.Ask<TAddr, {r.FullName}, {r.ReplyType}>(address, in request, forward);\n\n");
            sb.Append($"    public static uint Ask(this in global::DaServer.Runtime.Context context, in {r.FullName} request, global::DaServer.Runtime.ReplyTo forward = default) =>\n");
            sb.Append($"        context.Ask<{r.FullName}, {r.ReplyType}>(in request, forward);\n\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static string EmitAsk(RequestModel r, bool server)
    {
        var sb = new StringBuilder(Header);
        string close = OpenNamespace(sb, r.Namespace, server);
        string access = r.Public ? "public" : "internal";
        string reply = $"global::DaServer.Reply<{r.ReplyType}>";
        sb.Append($"/// <summary>Typed <c>Ask</c> of <see cref=\"{r.FullName.Replace("global::", "")}\"/> (generated).</summary>\n");
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{access} static class {r.ClassName}\n{{\n");
        sb.Append("    /// <summary>Ask the actor at <paramref name=\"address\"/>; completes with its reply.</summary>\n");
        sb.Append($"    public static {reply} Ask<TAddr>(this global::DaServer.IMessageSender sender, TAddr address, in {r.FullName} request)\n");
        sb.Append("        where TAddr : struct, global::DaServer.IAddress =>\n");
        sb.Append($"        sender.Ask<TAddr, {r.FullName}, {r.ReplyType}>(address, in request, default);\n\n");
        sb.Append("    /// <summary>Ask the actor at <paramref name=\"address\"/> with call options (a timeout of its own).</summary>\n");
        sb.Append($"    public static {reply} Ask<TAddr>(this global::DaServer.IMessageSender sender, TAddr address, in {r.FullName} request, in global::DaServer.CallOptions options)\n");
        sb.Append("        where TAddr : struct, global::DaServer.IAddress =>\n");
        sb.Append($"        sender.Ask<TAddr, {r.FullName}, {r.ReplyType}>(address, in request, in options);\n\n");
        sb.Append("    /// <summary>Ask the service handling the request (from a client: the actor bound to its session, else the service).</summary>\n");
        sb.Append($"    public static {reply} Ask(this global::DaServer.IMessageSender sender, in {r.FullName} request) =>\n");
        sb.Append($"        sender.Ask<{r.FullName}, {r.ReplyType}>(in request, default);\n\n");
        sb.Append("    /// <summary>Ask the service handling the request with call options (a timeout of its own).</summary>\n");
        sb.Append($"    public static {reply} Ask(this global::DaServer.IMessageSender sender, in {r.FullName} request, in global::DaServer.CallOptions options) =>\n");
        sb.Append($"        sender.Ask<{r.FullName}, {r.ReplyType}>(in request, in options);\n");
        sb.Append("}\n");
        sb.Append(close);
        return sb.ToString();
    }
}
