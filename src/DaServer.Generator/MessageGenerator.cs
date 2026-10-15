using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For every request struct (<c>IRequest&lt;TReply&gt;</c>): typed <c>Ask</c> overloads on <c>IMessageSender</c>
/// (<c>await Cluster.Ask(playerId, new GetInfo())</c> returns the reply type without boxing the request). For every
/// <c>Flow.FromClient</c> message: its registration for gateways, in a module initializer of its assembly, plus the
/// assembly marker hosts use to run it. Incremental rules: syntax-only predicates or ForAttributeWithMetadataName,
/// symbol-free equatable models, invalid input skipped (DaServerAnalyzer reports it), tracking names on every stage.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MessageGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // A partial struct declared in several files yields the same model more than once: Distinct.
        var requests = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is StructDeclarationSyntax { BaseList: not null } or
                                                          RecordDeclarationSyntax { BaseList: not null, ClassOrStructKeyword.ValueText: "struct" },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type ? Shapes.ReadRequest(type) : null)
            .Where(static m => m is not null)
            .Collect()
            .SelectMany(static (all, _) => all.Distinct())
            .WithTrackingName("Requests");
        context.RegisterSourceOutput(requests, static (spc, request) =>
            spc.AddSource($"{HintName(request!.FullName)}.Ask.g.cs", SourceText.From(EmitAsk(request), Encoding.UTF8)));

        var clientMessages = context.SyntaxProvider
            .ForAttributeWithMetadataName(MessageAttribute, static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => Shapes.ReadClientMessage((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<ClientMessageModel>(all.Distinct().OrderBy(m => m!.FullName).ToArray()!))
            .Combine(context.CompilationProvider.Select(static (c, _) => Identifier(c.AssemblyName ?? "Messages")))
            .WithTrackingName("ClientMessages");
        context.RegisterSourceOutput(clientMessages, static (spc, pair) =>
        {
            if (pair.Left.Length == 0) return;
            spc.AddSource("DaServer.ClientMessages.g.cs", SourceText.From(EmitClientMessages(pair.Left, pair.Right), Encoding.UTF8));
        });
    }

    private static string EmitAsk(RequestModel r)
    {
        var sb = new StringBuilder(Header);
        if (r.Namespace.Length > 0) sb.Append($"namespace {r.Namespace};\n\n");
        string access = r.Public ? "public" : "internal";
        string reply = $"global::System.Threading.Tasks.ValueTask<{r.ReplyType}>";
        sb.Append($"/// <summary>Typed <c>Ask</c> of <see cref=\"{r.FullName.Replace("global::", "")}\"/> (generated).</summary>\n");
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"{access} static class {r.ClassName}\n{{\n");
        sb.Append("    /// <summary>Ask the actor of <paramref name=\"key\"/> handling the request; completes with its reply.</summary>\n");
        sb.Append($"    public static {reply} Ask(this global::DaServer.Rpc.IMessageSender sender, long key, in {r.FullName} request) =>\n");
        sb.Append($"        sender.Ask<{r.FullName}, {r.ReplyType}>(key, in request);\n\n");
        sb.Append("    /// <summary>Ask the service handling the request (from a client: or the actor bound to its session).</summary>\n");
        sb.Append($"    public static {reply} Ask(this global::DaServer.Rpc.IMessageSender sender, in {r.FullName} request) =>\n");
        sb.Append($"        sender.Ask<{r.FullName}, {r.ReplyType}>(in request);\n}}\n");
        return sb.ToString();
    }

    private static string EmitClientMessages(EquatableArray<ClientMessageModel> messages, string assembly)
    {
        string anchor = $"DaServer.Generated.Messages_{assembly}";
        var sb = new StringBuilder(Header);
        sb.Append($"[assembly: global::DaServer.Rpc.MessagesAssembly(typeof(global::{anchor}))]\n\n");
        sb.Append("namespace DaServer.Generated\n{\n");
        sb.Append("    /// <summary>Registers the Flow.FromClient messages of this assembly for gateways (generated).</summary>\n");
        sb.Append("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"    public static class Messages_{assembly}\n    {{\n");
        sb.Append("        [global::System.Runtime.CompilerServices.ModuleInitializer]\n        internal static void Register()\n        {\n");
        foreach (var m in messages)
            sb.Append($"            global::DaServer.Rpc.MessageRegistry.RegisterClientMessage<{m.FullName}>(anyKey: {(m.AnyKey ? "true" : "false")});\n");
        sb.Append("        }\n    }\n}\n");
        return sb.ToString();
    }
}
