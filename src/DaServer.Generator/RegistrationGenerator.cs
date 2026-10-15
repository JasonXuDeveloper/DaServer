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
        var hosted = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is ClassDeclarationSyntax c &&
                                                    (c.BaseList != null || c.Members.Any(static m => m is MethodDeclarationSyntax { AttributeLists.Count: > 0 })),
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is INamedTypeSymbol type ? Shapes.ReadHosted(type) : null)
            .Where(static t => t is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<HostedModel>(all.Distinct().OrderBy(t => t!.FullName).ToArray()!))
            .WithTrackingName("HostedTypes");

        var clientMessages = context.SyntaxProvider
            .ForAttributeWithMetadataName(FromClientAttribute, static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => Shapes.ReadClientMessage((INamedTypeSymbol)ctx.TargetSymbol))
            .Where(static m => m is not null)
            .Collect()
            .Select(static (all, _) => new EquatableArray<ClientMessageModel>(all.Distinct().OrderBy(m => m!.FullName).ToArray()!))
            .WithTrackingName("ClientMessages");

        // Equatable projection of the compilation: recomputed on edits, but downstream stages stay cached.
        var assembly = context.CompilationProvider
            .Select(static (c, _) => new AssemblyModel(
                c.GetTypeByMetadataName("DaServer.Rpc.MessageRegistry") != null,
                c.GetTypeByMetadataName("DaServer.Runtime.ServerRegistry") != null,
                HasModuleInitializers(c),
                Identifier(c.AssemblyName ?? "Assembly"),
                new EquatableArray<string>(ContractAssemblyAnchors(c))))
            .WithTrackingName("RegistrationAssembly");

        context.RegisterSourceOutput(hosted.Combine(clientMessages).Combine(assembly), static (spc, input) =>
        {
            var ((types, messages), info) = input;
            if (!info.ReferencesCore) types = default; // handler registration lives in DaServer.Core
            if (types.Length + messages.Length + info.ContractAnchors.Length == 0) return;
            spc.AddSource("DaServer.Registration.g.cs", SourceText.From(Emit(types, messages, info), Encoding.UTF8));
        });
    }

    /// <summary>Generated registration classes of referenced assemblies ([GeneratedRegistration]).</summary>
    private static string[] ContractAssemblyAnchors(Compilation compilation)
    {
        var anchors = new List<string>();
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        foreach (var attribute in assembly.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == "DaServer.Rpc.GeneratedRegistrationAttribute" &&
                attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol anchor)
                anchors.Add(anchor.ToDisplayString(Fqn));
        anchors.Sort();
        return anchors.ToArray();
    }

    private static string Emit(EquatableArray<HostedModel> types, EquatableArray<ClientMessageModel> messages, AssemblyModel info)
    {
        var sb = new StringBuilder(Header);
        sb.Append("using System.Runtime.CompilerServices;\nusing DaServer.Rpc;\n");
        if (info.ReferencesRuntime) sb.Append("using DaServer.Runtime;\n");
        string className = $"Registration_{info.AssemblyHint}";
        sb.Append($"\n[assembly: global::DaServer.Rpc.GeneratedRegistration(typeof(global::DaServer.Generated.{className}))]\n");
        sb.Append("\nnamespace DaServer.Generated;\n\n");
        sb.Append("/// <summary>Registers this assembly's DaServer client messages, handler types and systems (generated).</summary>\n");
        BeginRegistration(sb, className, info.ModuleInitializer);

        foreach (var anchor in info.ContractAnchors) // referenced assemblies' registrations first
            sb.Append($"        RuntimeHelpers.RunClassConstructor(typeof({anchor}).TypeHandle);\n");
        foreach (var m in messages)
            sb.Append($"        global::DaServer.Clients.ClientMessages.Register<{m.FullName}>();\n");

        foreach (var t in types)
        {
            switch (t.Kind)
            {
                case HostedKind.System:
                    sb.Append($"        ServerRegistry.RegisterSystem(new SystemInfo(typeof({t.FullName}), static () => new {t.FullName}()));\n");
                    continue;
                case HostedKind.ClientHandler:
                    sb.Append($"        MessageRegistry.RegisterHandlers(typeof({t.FullName}), ");
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
                    HandlerReturn.ValueTask => $"                return {call};\n",
                    HandlerReturn.Reply => $"                context.ReplyValue({call});\n                return default;\n",
                    _ => $"                return context.Reply({call});\n",
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

    private sealed record AssemblyModel(bool ReferencesCore, bool ReferencesRuntime, bool ModuleInitializer, string AssemblyHint, EquatableArray<string> ContractAnchors);
}
