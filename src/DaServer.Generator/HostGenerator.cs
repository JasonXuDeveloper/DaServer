using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// For assemblies that reference the DaServer runtime: one module initializer registering every actor/service
/// implementation (with its [Role]s), every non-abstract EasyEcs system, and forcing registration of referenced
/// contract assemblies. No reflection scanning, no configuration strings, no hand-written registration lists.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HostGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classes = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, _) => HostedType.TryCreate((INamedTypeSymbol)ctx.SemanticModel.GetDeclaredSymbol(ctx.Node)!))
            .Where(static t => t != null)
            .Select(static (t, _) => t!)
            .Collect();

        var compilationInfo = context.CompilationProvider.Select(static (c, _) => new CompilationInfo(
            c.GetTypeByMetadataName("DaServer.Runtime.ServerRegistry") != null,
            Identifier(c.AssemblyName ?? "Assembly"),
            new EquatableArray<string>(ContractAssemblyAnchors(c))));

        context.RegisterSourceOutput(classes.Combine(compilationInfo), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
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

    private static void Emit(SourceProductionContext spc, ImmutableArray<HostedType> types, CompilationInfo info)
    {
        if (!info.ReferencesRuntime) return;

        var distinct = types.Distinct().OrderBy(t => t.FullName).ToArray();
        foreach (var t in distinct)
        foreach (var d in t.Diagnostics)
            spc.ReportDiagnostic(d.ToDiagnostic());

        var valid = distinct.Where(t => t.Diagnostics.Length == 0).ToArray();
        if (valid.Length == 0 && info.ContractAnchors.Length == 0) return;

        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing DaServer.Rpc;\nusing DaServer.Runtime;\n\n");
        sb.Append($"namespace DaServer.Generated;\n\n");
        sb.Append($"/// <summary>Registers this assembly's DaServer implementations and systems (generated).</summary>\n");
        sb.Append($"internal static class Registration_{info.AssemblyHint}\n{{\n");
        sb.Append("    [ModuleInitializer]\n    internal static void Register()\n    {\n");

        foreach (var anchor in info.ContractAnchors)
            sb.Append($"        RuntimeHelpers.RunModuleConstructor(typeof({anchor}).Module.ModuleHandle);\n");

        foreach (var t in valid)
        {
            string roles = t.Roles.Length == 0 ? "[]" : "[" + string.Join(", ", t.Roles.Select(r => "\"" + r + "\"")) + "]";
            if (t.IsSystem)
            {
                sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({t.FullName}), static w => w.AddSystem<{t.FullName}>(), {roles}));\n");
                continue;
            }

            string contracts = string.Join(", ", t.Contracts.Select(c => $"typeof({c})"));
            sb.Append($"        ServerRegistry.RegisterImplementation(new ImplementationInfo(typeof({t.FullName}), RpcContractKind.{t.Kind},\n");
            sb.Append($"            static () => new {t.FullName}(), [{contracts}], {roles}));\n");
        }

        sb.Append("    }\n}\n");
        spc.AddSource("DaServer.Registration.g.cs", sb.ToString());
    }

    private sealed record CompilationInfo(bool ReferencesRuntime, string AssemblyHint, EquatableArray<string> ContractAnchors);

    private sealed record HostedType(string FullName, bool IsSystem, string Kind, EquatableArray<string> Contracts,
        EquatableArray<string> Roles, EquatableArray<DiagnosticInfo> Diagnostics)
    {
        public static HostedType? TryCreate(INamedTypeSymbol type)
        {
            if (type.IsAbstract || type.IsGenericType || type.IsStatic) return null;

            var roles = type.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == "DaServer.Runtime.RoleAttribute")
                .SelectMany(a => a.ConstructorArguments.SelectMany(arg => arg.Kind == TypedConstantKind.Array ? arg.Values : ImmutableArray.Create(arg)))
                .Select(v => v.Value as string).Where(v => v != null).Select(v => v!).ToArray();

            var location = LocationInfo.From(type.Locations.FirstOrDefault());
            bool hasDefaultCtor = type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
            string name = type.ToDisplayString(Fqn);

            if (DerivesFrom(type, "EasyEcs.Core.Systems.SystemBase"))
            {
                var systemDiagnostics = hasDefaultCtor
                    ? []
                    : new[] { new DiagnosticInfo(Rules.NoDefaultConstructor, location, type.Name, "") };
                return new HostedType(name, true, "", default, new EquatableArray<string>(roles),
                    new EquatableArray<DiagnosticInfo>(systemDiagnostics));
            }

            var contracts = type.AllInterfaces.Where(IsContract).ToArray();
            if (contracts.Length == 0) return null;

            bool isActor = contracts.Any(c => Implements(c, "DaServer.Rpc.IActor"));
            var diagnostics = new List<DiagnosticInfo>();
            if (isActor && !DerivesFrom(type, "DaServer.Runtime.Actor"))
                diagnostics.Add(new DiagnosticInfo(Rules.ActorBase, location, type.Name, contracts[0].Name));
            if (!hasDefaultCtor)
                diagnostics.Add(new DiagnosticInfo(Rules.NoDefaultConstructor, location, type.Name, ""));

            return new HostedType(name, false, isActor ? "Actor" : "Service",
                new EquatableArray<string>(contracts.Select(c => c.ToDisplayString(Fqn)).ToArray()),
                new EquatableArray<string>(roles), new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
        }

        /// <summary>A server-side contract (actor/service; client callbacks are implemented on clients).</summary>
        private static bool IsContract(INamedTypeSymbol i)
        {
            string name = i.ToDisplayString();
            if (name is "DaServer.Rpc.IService" or "DaServer.Rpc.IActor" or "DaServer.Rpc.IClientCallback") return false;
            return Implements(i, "DaServer.Rpc.IService") || Implements(i, "DaServer.Rpc.IActor");
        }
    }
}
