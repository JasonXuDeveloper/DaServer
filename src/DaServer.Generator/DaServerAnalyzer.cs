using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DaServer.Generator;

/// <summary>
/// Reports misuse of DaServer messages and handlers. It shares <see cref="Shapes"/> with the generators, so "the
/// generator skipped it" and "the analyzer reported it" always agree.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class DaServerAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.BadHandler, Rules.BadMessage, Rules.NotWireType, Rules.IdCollision, Rules.NoDefaultConstructor, Rules.WrongFlow, Rules.StatefulHandlerType, Rules.WrongAddress);

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
                CheckStateless(type, Report);

                if (type.TypeKind != TypeKind.Struct || !Support.HasAttribute(type, Support.MessageAttribute)) return;
                if (!Shapes.IsMessage(type, type.Locations.FirstOrDefault(), "[Message]", Report)) return;
                string name = type.ToDisplayString();
                var location = type.Locations.FirstOrDefault();
                var existing = ids.GetOrAdd(Shapes.MessageId(type), (name, location));
                if (existing.Name != name)
                    collisions.Add(Diagnostic.Create(Rules.IdCollision, location, name, existing.Name));
            }, SymbolKind.NamedType);

            start.RegisterOperationAction(CheckSend, OperationKind.Invocation);

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var d in collisions) end.ReportDiagnostic(d);
            });
        });
    }
}

/// <summary>Actors and services hold no state of their own: it lives in components and singletons.</summary>
public sealed partial class DaServerAnalyzer
{
    /// <summary>
    /// Mutable fields of an actor or service type (instance fields, auto-properties, static non-readonly fields): the
    /// runtime recreates actor and service objects (load, move, code reload) and persists, moves and inspects components
    /// only, so such state would be lost or shared across shard threads.
    /// </summary>
    private static void CheckStateless(INamedTypeSymbol type, Report report)
    {
        if (type.TypeKind != TypeKind.Class ||
            !(Support.DerivesFrom(type, Support.ActorBase) || Support.DerivesFrom(type, Support.ServiceBase)))
            return;

        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsConst || (field.IsStatic && field.IsReadOnly)) continue;
            var named = field.AssociatedSymbol ?? field;
            report(Rules.StatefulHandlerType, named.Locations.FirstOrDefault(), type.Name, named.Name);
        }
    }
}

/// <summary>Call sites: what is sent must be a message, and travel its flow.</summary>
public sealed partial class DaServerAnalyzer
{
    private static readonly string[] Senders = ["DaServer.IMessageSender", "DaServer.Runtime.ICluster", Support.ClusterClientType];

    /// <summary>
    /// <c>Send</c> / <c>Ask</c> / <c>Publish</c> of the cluster or a client: the last parameter is the message, the one
    /// before it (when there is one besides the sender) the address. A client address (session, origin, client group)
    /// needs <c>[ToClient]</c>; only actors are asked; a <c>ClusterClient</c> needs <c>[FromClient]</c> and addresses
    /// actors only; servers may not send <c>[ToClient]</c>-only messages to each other.
    /// </summary>
    private static void CheckSend(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name is not ("Send" or "Ask" or "Publish") || method.Parameters.Length == 0) return;

        var sender = method.IsExtensionMethod ? method.Parameters[0].Type : method.ContainingType;
        if (sender == null || !IsSender(sender)) return;
        var receiver = method.IsExtensionMethod ? Unwrap(invocation.Arguments.FirstOrDefault()?.Value) : Unwrap(invocation.Instance);
        bool client = receiver != null && receiver.ToDisplayString() == Support.ClusterClientType;

        var message = method.Parameters[method.Parameters.Length - 1].Type;
        if (message is ITypeParameterSymbol) return; // generic plumbing: checked where the type is known
        int addressIndex = method.Parameters.Length - 2;
        var address = addressIndex >= (method.IsExtensionMethod ? 1 : 0) ? method.Parameters[addressIndex].Type : null;
        if (address is ITypeParameterSymbol) return;
        bool toClient = address != null && Support.ClientAddresses.Contains(address.ToDisplayString());
        bool notActor = address != null && !Implements(address, Support.ActorId);

        var location = invocation.Syntax.GetLocation();
        void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
            context.ReportDiagnostic(Diagnostic.Create(d, l ?? location, a));
        string usedBy = method.Name;
        if (notActor && method.Name == "Ask")
            Report(Rules.WrongAddress, location, address!.ToDisplayString(), "is not an actor id: send to it, only actors are asked");
        else if (client && notActor)
            Report(Rules.WrongAddress, location, address!.ToDisplayString(), "is not an actor id: clients send to actors and services only");
        else if (Shapes.IsMessage(message, location, usedBy, Report))
            Shapes.CheckFlow(message, toClient, fromClient: client, location, usedBy, Report);
    }

    private static bool Implements(ITypeSymbol type, string interfaceName) =>
        type.AllInterfaces.Any(i => i.ToDisplayString() == interfaceName);

    private static bool IsSender(ITypeSymbol type)
    {
        foreach (var name in Senders)
            if (type.ToDisplayString() == name || type.AllInterfaces.Any(i => i.ToDisplayString() == name)) return true;
        return false;
    }

    private static ITypeSymbol? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
        return operation?.Type;
    }
}

internal static class Rules
{
    private const string Category = "DaServer";

    public static readonly DiagnosticDescriptor BadHandler = new("DAS001", "Invalid message handler",
        "[Handle] method '{0}' must {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadMessage = new("DAS002", "Invalid message type",
        "'{0}' (used by {1}) {2}: a message is a non-generic [NinoType, Message] struct", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotWireType = new("DAS003", "Type cannot cross the wire",
        "'{0}' (used by {1}) must be a primitive, string, enum, array, BCL collection or a [NinoType] type", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor IdCollision = new("DAS004", "Message id collision",
        "Messages '{0}' and '{1}' hash to the same wire id; add [WireId] to one of them", Category,
        DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor NoDefaultConstructor = new("DAS005", "Missing parameterless constructor",
        "'{0}' must have a public parameterless constructor (the runtime creates one instance per shard/loaded actor)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor StatefulHandlerType = new("DAS007", "Actor or service with fields",
        "'{0}.{1}' holds state outside ECS: keep actor state in components (Actor<TId, TState>, Entity) and shared data in singletons",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor WrongFlow = new("DAS006", "Message used against its flow",
        "'{0}' (used by {1}) {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor WrongAddress = new("DAS008", "Wrong kind of address",
        "'{0}' {1}", Category, DiagnosticSeverity.Error, true);
}
