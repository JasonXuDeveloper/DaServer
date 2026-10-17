using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>
/// One registration class per assembly, <c>DaServer.Generated.Registration_{Assembly}</c>, whose static constructor runs
/// once: the registrations of referenced assemblies first, then this assembly's <c>[FromClient]</c> messages (read by
/// client-facing nodes) and, with DaServer.Core referenced, its client handler types and, with the server runtime, its
/// actors and services (one dispatcher per [Handle] method) and EasyEcs systems. No reflection scanning, no
/// configuration strings, no hand-written registration list.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RegistrationGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Types with [Handle] methods (actors, services, client handlers), found through the compiler's attribute index,
        // and EcsSystem types (no attribute: classes with a base list, checked by symbol, allocation-free).
        var handlerTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(HandleAttribute, static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol.ContainingType is { } type && IsFirstHandler(type, ctx.TargetSymbol)
                    ? Shapes.ReadHosted(type) // once per type, not once per [Handle] method
                    : null)
            .Where(static t => t is not null)
            .Collect();
        var systems = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type && DerivesFrom(type, EcsSystem)
                    ? Shapes.ReadHosted(type)
                    : null)
            .Where(static t => t is not null)
            .Collect();
        var hosted = handlerTypes.Combine(systems)
            .Select(static (pair, _) => new EquatableArray<HostedModel>(
                pair.Left.Concat(pair.Right).Distinct().OrderBy(t => t!.FullName).ToArray()!))
            .WithTrackingName("HostedTypes");

        var clientMessages = context.SyntaxProvider
            .ForAttributeWithMetadataName(FromClientAttribute, static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => Shapes.ReadClientMessage((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<ClientMessageModel>(all.Distinct().OrderBy(m => m!.FullName).ToArray()!))
            .WithTrackingName("ClientMessages");

        // Actor id types placed by IPlaced (ADR-073): every node referencing them hashes their placement.
        var placed = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is StructDeclarationSyntax { BaseList: not null }
                    or RecordDeclarationSyntax { BaseList: not null, ClassOrStructKeyword.Text: "struct" },
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type &&
                                    type.IsValueType && Implements(type, ActorId) && Implements(type, Placed)
                    ? type.ToDisplayString(Fqn)
                    : null)
            .Where(static t => t is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<string>(all.Distinct().OrderBy(t => t, System.StringComparer.Ordinal).ToArray()!))
            .WithTrackingName("PlacedIds");

        // Equatable projection of the compilation: recomputed on edits, but downstream stages stay cached.
        var assembly = context.CompilationProvider
            .Select(static (c, _) => new AssemblyModel(
                c.GetTypeByMetadataName("DaServer.Rpc.HandlerRegistry") != null,
                c.GetTypeByMetadataName("DaServer.Runtime.ServerRegistry") != null,
                HasModuleInitializers(c),
                Identifier(c.AssemblyName ?? "Assembly"),
                new EquatableArray<string>(ContractAssemblyAnchors(c)),
                new EquatableArray<string>(SharedSystems(c)),
                new EquatableArray<string>(PersistedComponents(c))))
            .WithTrackingName("RegistrationAssembly");

        context.RegisterSourceOutput(hosted.Combine(clientMessages).Combine(placed).Combine(assembly), static (spc, input) =>
        {
            var (((types, messages), placedIds), info) = input;
            // Handler registration lives in DaServer.Core; actors, services and systems need the server runtime.
            if (!info.ReferencesCore) types = default;
            else if (!info.ReferencesRuntime)
                types = new EquatableArray<HostedModel>(types.Where(static t => t.Kind == HostedKind.ClientHandler).ToArray());
            var sharedSystems = info.ReferencesRuntime ? info.SharedSystems : default;
            int persisted = info.ReferencesRuntime ? info.Persisted.Length : 0;
            if (types.Length + messages.Length + placedIds.Length + info.ContractAnchors.Length + sharedSystems.Length + persisted == 0) return;
            spc.AddSource("DaServer.Registration.g.cs", SourceText.From(Emit(types, messages, placedIds, sharedSystems, info), Encoding.UTF8));
        });

        // An assembly built with <DaServerShared>true</DaServerShared> says so, for the nodes that reference it.
        var shared = context.AnalyzerConfigOptionsProvider
            .Select(static (o, _) => IsShared(o.GlobalOptions))
            .WithTrackingName("DaServerShared");
        context.RegisterSourceOutput(shared, static (spc, isShared) =>
        {
            if (isShared) spc.AddSource("DaServer.Shared.g.cs", SourceText.From(Header + "[assembly: global::DaServer.DaServerShared]\n", Encoding.UTF8));
        });
    }

    private static bool IsFirstHandler(INamedTypeSymbol type, ISymbol method)
    {
        foreach (var member in type.GetMembers())
            if (member is IMethodSymbol && HasAttribute(member, HandleAttribute))
                return SymbolEqualityComparer.Default.Equals(member, method);
        return false;
    }

    /// <summary>Generated registration classes of referenced assemblies ([GeneratedRegistration]).</summary>
    private static string[] ContractAssemblyAnchors(Compilation compilation)
    {
        var anchors = new List<string>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        foreach (var attribute in assembly.GetAttributes())
            if (Is(attribute.AttributeClass, "DaServer.GeneratedRegistrationAttribute") &&
                attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol anchor)
                anchors.Add(anchor.ToDisplayString(Fqn));
        anchors.Sort();
        return anchors.ToArray();
    }

    /// <summary>Whether the project sets <c>&lt;DaServerShared&gt;true&lt;/DaServerShared&gt;</c> (made visible by DaServer's build props).</summary>
    internal static bool IsShared(Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions options) =>
        options.TryGetValue("build_property.DaServerShared", out var value) &&
        string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The EcsSystem types of referenced shared-logic assemblies ([DaServerShared]): this node runs them too.</summary>
    private static string[] SharedSystems(Compilation compilation)
    {
        var systems = new List<string>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            bool isShared = false;
            foreach (var attribute in assembly.GetAttributes())
                isShared |= Is(attribute.AttributeClass, "DaServer.DaServerSharedAttribute");
            if (isShared) CollectSystems(assembly.GlobalNamespace, systems);
        }

        systems.Sort(System.StringComparer.Ordinal);
        return systems.ToArray();
    }

    /// <summary>
    /// The <c>[Persist]</c> and <c>[Handoff]</c> components of this compilation and the assemblies it references
    /// (components projects): the node registers them so actor records can hold them (ADR-068, ADR-072). Entries are
    /// "type|type id|stored" (stored: 1 for [Persist], 0 for [Handoff] only).
    /// </summary>
    private static string[] PersistedComponents(Compilation compilation)
    {
        var found = new List<string>();
        CollectPersisted(compilation.Assembly.GlobalNamespace, found);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            if (DeclaresComponents(assembly)) CollectPersisted(assembly.GlobalNamespace, found);
        found.Sort(System.StringComparer.Ordinal);
        return found.ToArray();
    }

    private static readonly string[] FrameworkAssemblies =
        ["DaServer", "DaServer.Core", "DaServer.Abstractions", "DaServer.Client", "DaServer.Hosting", "DaServer.Simulation"];

    /// <summary>Only application assemblies that can hold [Persist] components are walked (they see the attribute).</summary>
    private static bool DeclaresComponents(IAssemblySymbol assembly)
    {
        if (System.Array.IndexOf(FrameworkAssemblies, assembly.Name) >= 0) return false;
        foreach (var module in assembly.Modules)
        foreach (var reference in module.ReferencedAssemblies)
            if (reference.Name == "DaServer.Abstractions") return true;
        return false;
    }

    private static void CollectPersisted(INamespaceSymbol ns, List<string> found)
    {
        foreach (var type in ns.GetTypeMembers())
            if (type is { TypeKind: TypeKind.Struct, DeclaredAccessibility: Accessibility.Public, IsGenericType: false })
            {
                bool stored = HasAttribute(type, PersistAttribute);
                if (stored || HasAttribute(type, HandoffAttribute))
                    found.Add(type.ToDisplayString(Fqn) + "|" + Shapes.TypeIdOf(type) + (stored ? "|1" : "|0"));
            }
        foreach (var inner in ns.GetNamespaceMembers()) CollectPersisted(inner, found);
    }

    private static void CollectSystems(INamespaceSymbol ns, List<string> systems)
    {
        foreach (var type in ns.GetTypeMembers())
            if (type is { DeclaredAccessibility: Accessibility.Public, IsAbstract: false, IsGenericType: false } &&
                DerivesFrom(type, EcsSystem))
                systems.Add(type.ToDisplayString(Fqn));
        foreach (var inner in ns.GetNamespaceMembers()) CollectSystems(inner, systems);
    }

    private static string Emit(EquatableArray<HostedModel> types, EquatableArray<ClientMessageModel> messages,
        EquatableArray<string> placedIds, EquatableArray<string> sharedSystems, AssemblyModel info)
    {
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing DaServer;\n");
        if (info.ReferencesCore) sb.Append("using DaServer.Rpc;\n");
        if (info.ReferencesRuntime) sb.Append("using DaServer.Runtime;\n");
        string className = $"Registration_{info.AssemblyHint}";
        sb.Append($"\n[assembly: global::DaServer.GeneratedRegistration(typeof(global::DaServer.Generated.{className}))]\n");
        sb.Append("\nnamespace DaServer.Generated;\n\n");
        sb.Append("/// <summary>Registers this assembly's DaServer client messages, handler types and systems (generated).</summary>\n");
        BeginRegistration(sb, className, info.ModuleInitializer);

        foreach (var anchor in info.ContractAnchors) // referenced assemblies' registrations first
            sb.Append($"        RuntimeHelpers.RunClassConstructor(typeof({anchor}).TypeHandle);\n");
        foreach (var m in messages)
            sb.Append($"        global::DaServer.ClientMessages.Register<{m.FullName}>();\n");
        foreach (var id in placedIds)
            sb.Append($"        global::DaServer.Placements.Register<{id}>();\n");
        foreach (var system in sharedSystems)
            sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({system}), static () => new {system}()));\n");
        if (info.ReferencesRuntime)
            foreach (var entry in info.Persisted) // "type|type id|stored": the [Persist] and [Handoff] components
            {
                var parts = entry.Split('|');
                sb.Append($"        ServerRegistry.RegisterPersistent<{parts[0]}>({parts[1]}u, stored: {(parts[2] == "1" ? "true" : "false")});\n");
            }

        foreach (var t in types)
        {
            switch (t.Kind)
            {
                case HostedKind.System:
                    sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({t.FullName}), static () => new {t.FullName}()));\n");
                    continue;
                case HostedKind.ClientHandler:
                    sb.Append($"        HandlerRegistry.RegisterHandlers(typeof({t.FullName}), ");
                    break;
                default:
                    sb.Append($"        ServerRegistry.RegisterHandlerType(new HandlerTypeInfo(typeof({t.FullName}), ");
                    sb.Append($"{(t.Kind == HostedKind.Actor ? "true" : "false")},\n            static () => new {t.FullName}(), ");
                    break;
            }

            sb.Append('[');
            foreach (var h in t.Handlers)
            {
                // Deserialize the message straight from the frame, call the [Handle] method, reply to a request.
                string call = $"Unsafe.As<{t.FullName}>(target).{h.Method}(m)";
                sb.Append($"\n            MessageHandler.Of<{h.MessageType}>(static (object target, ref global::Nino.Core.Reader reader, in CallContext context) =>\n");
                sb.Append($"            {{\n                global::Nino.Core.NinoDeserializer.Deserialize(out {h.MessageType} m, ref reader);\n");
                if (t.Kind != HostedKind.ClientHandler)
                    sb.Append($"                if (!MessageFilters<{h.MessageType}>.Admit(target, in m, in context)) return default;\n");
                sb.Append(h.Return switch
                {
                    HandlerReturn.Void => $"                {call};\n                return default;\n",
                    HandlerReturn.Later => $"                return {call}.AsValueTask();\n",
                    HandlerReturn.Value => $"                context.ReplyValue({call});\n                return default;\n",
                    _ => $"                return context.Reply({call}.AsValueTask());\n",
                });
                sb.Append("            }),");
            }

            sb.Append(t.Handlers.Length > 0 ? "\n        ]" : "]");
            sb.Append(t.Kind == HostedKind.ClientHandler ? ");\n" : "));\n");
        }

        EndRegistration(sb);
        return sb.ToString();
    }

    /// <summary>
    /// Open a generated registration class: registrations go into its static constructor (runs once on every runtime,
    /// IL2CPP included, when triggered through [GeneratedRegistration]); with module initializers available (.NET) it
    /// also triggers itself when its assembly loads. Close with <see cref="EndRegistration"/>.
    /// </summary>
    private static void BeginRegistration(System.Text.StringBuilder sb, string className, bool moduleInitializer)
    {
        sb.Append("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        sb.Append($"public static class {className}\n{{\n");
        if (moduleInitializer)
            sb.Append($"    [global::System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Load() =>\n" +
                      $"        global::System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof({className}).TypeHandle);\n\n");
        sb.Append($"    static {className}()\n    {{\n");
    }

    private static void EndRegistration(System.Text.StringBuilder sb) => sb.Append("    }\n}\n");

    /// <summary>Whether the compilation can declare module initializers (not on netstandard2.1).</summary>
    private static bool HasModuleInitializers(Compilation c) =>
        c.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute") != null;

    private sealed record AssemblyModel(bool ReferencesCore, bool ReferencesRuntime, bool ModuleInitializer, string AssemblyHint,
        EquatableArray<string> ContractAnchors, EquatableArray<string> SharedSystems, EquatableArray<string> Persisted);
}
