using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DaServer.Generator;

/// <summary>
/// Reports misuse of DaServer contracts and implementations. It shares <see cref="Shapes"/> with the generators, so
/// "the generator skipped it" and "the analyzer reported it" always agree.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DaServerAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.BadReturnType, Rules.BadParameter, Rules.GenericMember, Rules.CallbackMustBeVoid, Rules.IdCollision,
        Rules.ActorBase, Rules.NoDefaultConstructor, Rules.NoMethods, Rules.NotWireType, Rules.UnkeyedActorEvent,
        Rules.MixedKinds, Rules.ConflictingMarkers);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            // Wire ids of every contract method in this compilation, for the collision check at the end.
            var ids = new ConcurrentDictionary<uint, (string Name, Location? Location)>();
            var collisions = new ConcurrentBag<Diagnostic>();

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
                    symbolContext.ReportDiagnostic(Diagnostic.Create(d, l ?? type.Locations.FirstOrDefault(), a));

                Shapes.ReadHosted(type, Report);
                Shapes.CheckEvent(type, Report);
                if (Shapes.ReadContract(type, Report) == null) return;

                foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
                {
                    string name = type.ToDisplayString() + "." + method.Name;
                    var location = method.Locations.FirstOrDefault();
                    var existing = ids.GetOrAdd(Shapes.MethodId(type, method), (name, location));
                    if (existing.Name != name)
                        collisions.Add(Diagnostic.Create(Rules.IdCollision, location, name, existing.Name));
                }
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

    public static readonly DiagnosticDescriptor BadReturnType = new("DAS001", "Unsupported RPC return type",
        "Method '{0}' of RPC contract '{1}' must return ValueTask, ValueTask<T> or void (one-way); Task is not allowed (it allocates)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadParameter = new("DAS002", "Unsupported RPC parameter",
        "Parameter '{0}' of RPC method '{1}' must be passed by value (no ref/out/in/params)", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor GenericMember = new("DAS003", "Generic RPC member",
        "RPC contract '{0}' and its methods cannot be generic", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor CallbackMustBeVoid = new("DAS004", "Client callbacks are one-way",
        "Method '{0}' of client callback '{1}' must return void", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor IdCollision = new("DAS005", "RPC method id collision",
        "Methods '{0}' and '{1}' hash to the same wire id; add [WireId] to one of them", Category,
        DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ActorBase = new("DAS006", "Actor implementation must derive from Actor",
        "'{0}' implements actor contract '{1}' and must derive from DaServer.Runtime.Actor", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NoDefaultConstructor = new("DAS007", "Missing parameterless constructor",
        "'{0}' must have a public parameterless constructor (the runtime creates one instance per shard/activation)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NoMethods = new("DAS008", "Properties and events are not supported",
        "RPC contract '{0}' may only declare methods (member '{1}')", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotWireType = new("DAS009", "Type cannot cross the wire",
        "'{0}' (used by {1}) must be a primitive, string, enum, array, BCL collection or a [NinoType] type", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnkeyedActorEvent = new("DAS010", "Actors handle keyed events only",
        "Actor '{0}' handles '{1}', which is not an IKeyedEvent: an actor event is delivered to the activation of the event's key",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MixedKinds = new("DAS011", "Actor and service in one type",
        "'{0}' implements both actor and service contracts; an implementation is either keyed (actor) or stateless (service)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ConflictingMarkers = new("DAS012", "Conflicting client markers",
        "Actor contract '{0}' cannot be both ISessionActor and IPublicActor", Category, DiagnosticSeverity.Error, true);
}
