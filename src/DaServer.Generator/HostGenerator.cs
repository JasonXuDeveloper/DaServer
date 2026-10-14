using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For assemblies referencing the DaServer runtime: one module initializer registering every actor/service
/// implementation (with the events it handles) and every EasyEcs system, and forcing registration of referenced contract
/// assemblies. No reflection scanning, no configuration strings, no hand-written registration list.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HostGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var hosted = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type ? Shapes.ReadHosted(type) : null)
            .Where(static t => t is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<HostedModel>(all.Distinct().OrderBy(t => t!.FullName).ToArray()!))
            .WithTrackingName("HostedTypes");

        // Equatable projection of the compilation: recomputed on edits, but downstream stages stay cached.
        var assembly = context.CompilationProvider
            .Select(static (c, _) => new AssemblyModel(
                c.GetTypeByMetadataName("DaServer.Runtime.ServerRegistry") != null,
                Identifier(c.AssemblyName ?? "Assembly"),
                new EquatableArray<string>(ContractAssemblyAnchors(c))))
            .WithTrackingName("HostAssembly");

        context.RegisterSourceOutput(hosted.Combine(assembly), static (spc, pair) =>
        {
            var (types, info) = pair;
            if (!info.ReferencesRuntime || types.Length + info.ContractAnchors.Length == 0) return;
            spc.AddSource("DaServer.Registration.g.cs", SourceText.From(Emit(types, info), Encoding.UTF8));
        });

        // A node project needs no Program.cs: an executable referencing DaServer.Hosting without its own entry point
        // gets one (Generic Host + AddDaServer). Writing Program.cs (top-level statements or Main) opts out.
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
        /// and system compiled into it; options come from configuration (appsettings.json, DaServer__* environment
        /// variables, command line). Write a Program.cs to customize the host.
        /// </summary>
        internal static class NodeProgram
        {
            public static async global::System.Threading.Tasks.Task Main(string[] args)
            {
                var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
                global::DaServer.Hosting.DaServerHostingExtensions.AddDaServer(builder);
                await global::Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(builder.Build());
            }
        }

        """;

    /// <summary>Anchor types of referenced assemblies marked [RpcContractsAssembly].</summary>
    private static string[] ContractAssemblyAnchors(Compilation compilation)
    {
        var anchors = new List<string>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        foreach (var attribute in assembly.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == "DaServer.Rpc.RpcContractsAssemblyAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol anchor)
                anchors.Add(anchor.ToDisplayString(Fqn));
        anchors.Sort();
        return anchors.ToArray();
    }

    private static string Emit(EquatableArray<HostedModel> types, AssemblyModel info)
    {
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing DaServer.Rpc;\nusing DaServer.Runtime;\n\nnamespace DaServer.Generated;\n\n");
        sb.Append("/// <summary>Registers this assembly's DaServer implementations and systems (generated).</summary>\n");
        sb.Append($"internal static class Registration_{info.AssemblyHint}\n{{\n    [ModuleInitializer]\n    internal static void Register()\n    {{\n");

        foreach (var anchor in info.ContractAnchors)
            sb.Append($"        RuntimeHelpers.RunModuleConstructor(typeof({anchor}).Module.ModuleHandle);\n");

        foreach (var t in types)
        {
            if (t.IsSystem)
            {
                sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({t.FullName}), static () => new {t.FullName}()));\n");
                continue;
            }

            string contracts = string.Join(", ", t.Contracts.Select(c => $"typeof({c})"));
            sb.Append($"        ServerRegistry.RegisterImplementation(new ImplementationInfo(typeof({t.FullName}), RpcContractKind.{t.Kind},\n");
            sb.Append($"            static () => new {t.FullName}(), [{contracts}],\n            [");
            foreach (var h in t.Handlers)
            {
                // The handler's dispatcher: deserialize the event straight from the frame and call the [Handle] method.
                string call = $"Unsafe.As<{t.FullName}>(target).{h.Method}(e)";
                sb.Append($"\n                EventHandlerInfo.Of<{h.EventType}>(static (object target, uint _, ref global::Nino.Core.Reader reader, in CallContext __) =>\n");
                sb.Append($"                {{\n                    global::Nino.Core.NinoDeserializer.Deserialize(out {h.EventType} e, ref reader);\n");
                sb.Append(h.ReturnsVoid
                    ? $"                    {call};\n                    return default;\n                }}),"
                    : $"                    return {call};\n                }}),");
            }

            sb.Append(t.Handlers.Length > 0 ? "\n            ]));\n" : "]));\n");
        }

        return sb.Append("    }\n}\n").ToString();
    }

    private sealed record AssemblyModel(bool ReferencesRuntime, string AssemblyHint, EquatableArray<string> ContractAnchors);
}
