using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For every request struct (<c>IRequest&lt;TReply&gt;</c>): a static class <c>{Request}Ask</c> of typed <c>Ask</c>
/// overloads on <c>IMessageSender</c> (<c>await Cluster.Ask(playerId, new GetInfo())</c> returns the reply type without
/// boxing the request). Incremental rules: syntax-only predicates, symbol-free equatable models, invalid input skipped
/// (DaServerAnalyzer reports it), tracking names on every stage.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AskGenerator : IIncrementalGenerator
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
            spc.AddSource($"DaServer.Ask.{HintName(request!.FullName)}.g.cs", SourceText.From(EmitAsk(request), Encoding.UTF8)));
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
        sb.Append("    /// <summary>Ask the actor at <paramref name=\"address\"/>; completes with its reply.</summary>\n");
        sb.Append($"    public static {reply} Ask<TAddr>(this global::DaServer.Rpc.IMessageSender sender, TAddr address, in {r.FullName} request)\n");
        sb.Append("        where TAddr : struct, global::DaServer.Rpc.IAddress =>\n");
        sb.Append($"        sender.Ask<TAddr, {r.FullName}, {r.ReplyType}>(address, in request);\n\n");
        sb.Append("    /// <summary>Ask the service handling the request (from a client: the actor bound to its session, else the service).</summary>\n");
        sb.Append($"    public static {reply} Ask(this global::DaServer.Rpc.IMessageSender sender, in {r.FullName} request) =>\n");
        sb.Append($"        sender.Ask<{r.FullName}, {r.ReplyType}>(in request);\n}}\n");
        return sb.ToString();
    }
}
