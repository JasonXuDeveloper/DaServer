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
/// implementation and every EasyEcs system (with their [Role]s), and forcing registration of referenced contract
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
    }

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
            string roles = "[" + string.Join(", ", t.Roles.Select(r => "\"" + r + "\"")) + "]";
            if (t.IsSystem)
            {
                sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({t.FullName}), static () => new {t.FullName}(), {roles}));\n");
                continue;
            }

            string contracts = string.Join(", ", t.Contracts.Select(c => $"typeof({c})"));
            sb.Append($"        ServerRegistry.RegisterImplementation(new ImplementationInfo(typeof({t.FullName}), RpcContractKind.{t.Kind},\n");
            sb.Append($"            static () => new {t.FullName}(), [{contracts}], {roles}));\n");
        }

        return sb.Append("    }\n}\n").ToString();
    }

    private sealed record AssemblyModel(bool ReferencesRuntime, string AssemblyHint, EquatableArray<string> ContractAnchors);
}
