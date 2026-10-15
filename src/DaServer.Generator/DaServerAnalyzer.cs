using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DaServer.Generator;

/// <summary>
/// Reports misuse of DaServer messages and handlers. It shares <see cref="Shapes"/> with the generators, so "the
/// generator skipped it" and "the analyzer reported it" always agree.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DaServerAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.BadHandler, Rules.BadMessage, Rules.NotWireType, Rules.IdCollision, Rules.NoDefaultConstructor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Wire ids of every message type declared in this compilation, for the collision check at the end.
            var ids = new ConcurrentDictionary<uint, (string Name, Location? Location)>();
            var collisions = new ConcurrentBag<Diagnostic>();

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
                    symbolContext.ReportDiagnostic(Diagnostic.Create(d, l ?? type.Locations.FirstOrDefault(), a));

                Shapes.ReadHosted(type, Report);
                Shapes.ReadRequest(type, Report);
                Shapes.ReadClientMessage(type, Report);

                if (type.TypeKind != TypeKind.Struct || !Support.HasAttribute(type, Support.NinoTypeAttribute)) return;
                string name = type.ToDisplayString();
                var location = type.Locations.FirstOrDefault();
                var existing = ids.GetOrAdd(Shapes.MessageId(type), (name, location));
                if (existing.Name != name)
                    collisions.Add(Diagnostic.Create(Rules.IdCollision, location, name, existing.Name));
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var d in collisions) end.ReportDiagnostic(d);
            });
        });
    }
}

internal static class Rules
{
    private const string Category = "DaServer";

    public static readonly DiagnosticDescriptor BadHandler = new("DAS001", "Invalid message handler",
        "[Handle] method '{0}' must {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadMessage = new("DAS002", "Invalid message type",
        "'{0}' (used by {1}) {2}: a message is a non-generic [NinoType] struct", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotWireType = new("DAS003", "Type cannot cross the wire",
        "'{0}' (used by {1}) must be a primitive, string, enum, array, BCL collection or a [NinoType] type", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor IdCollision = new("DAS004", "Message id collision",
        "Messages '{0}' and '{1}' hash to the same wire id; add [WireId] to one of them", Category,
        DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor NoDefaultConstructor = new("DAS005", "Missing parameterless constructor",
        "'{0}' must have a public parameterless constructor (the runtime creates one instance per shard/activation)",
        Category, DiagnosticSeverity.Error, true);
}
