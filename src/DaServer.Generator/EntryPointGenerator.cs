using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// A node project needs no Program.cs: an executable referencing DaServer.Hosting without its own entry point gets one
/// (WebApplication + AddDaServer + MapDaServer). Writing Program.cs (top-level statements or Main) opts out.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntryPointGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var userEntryPoint = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is GlobalStatementSyntax ||
                                                    node is MethodDeclarationSyntax { Identifier.ValueText: "Main" },
                static (_, _) => true)
            .Collect()
            .Select(static (all, _) => all.Length > 0)
            .WithTrackingName("UserEntryPoint");
        var hostedExecutable = context.CompilationProvider
            .Select(static (c, _) => c.Options.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication &&
                                     c.GetTypeByMetadataName("DaServer.Hosting.DaServerHostingExtensions") != null)
            .WithTrackingName("HostedExecutable");
        context.RegisterSourceOutput(userEntryPoint.Combine(hostedExecutable), static (spc, pair) =>
        {
            if (pair.Left || !pair.Right) return;
            spc.AddSource("DaServer.Program.g.cs", SourceText.From(EntryPoint, Encoding.UTF8));
        });
    }

    private const string EntryPoint = Header + """
        namespace DaServer.Generated;

        /// <summary>
        /// Entry point of a DaServer node (generated because the project has none): the node hosts every actor, service
        /// and system compiled into it and runs its HTTP plane; options come from configuration (appsettings.json,
        /// DaServer__* environment variables, command line). Write a Program.cs to customize the host or map endpoints.
        /// </summary>
        internal static class NodeProgram
        {
            public static async global::System.Threading.Tasks.Task Main(string[] args)
            {
                var builder = global::Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
                global::DaServer.Hosting.DaServerHttpExtensions.AddDaServer(builder);
                var app = builder.Build();
                global::DaServer.Hosting.DaServerHttpExtensions.MapDaServer(app);
                await app.RunAsync();
            }
        }

        """;
}
