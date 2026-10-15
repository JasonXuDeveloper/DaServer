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
        Rules.BadHandler, Rules.BadMessage, Rules.NotWireType, Rules.IdCollision, Rules.NoDefaultConstructor, Rules.WrongFlow);

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

/// <summary>Call sites: what is sent must be a message, and travel its flow.</summary>
public sealed partial class DaServerAnalyzer
{
    private static readonly string[] Senders = ["DaServer.Rpc.IMessageSender", "DaServer.Runtime.ICluster", Support.ClusterClientType];

    /// <summary>
    /// <c>Send</c> / <c>Ask</c> / <c>Publish</c> of the cluster or a client: the last parameter is the message. Pushing to
    /// a session or group needs <c>Flow.ToClient</c>; a <c>ClusterClient</c> needs <c>Flow.FromClient</c>; servers may not
    /// send <c>Flow.ToClient</c>-only messages to each other.
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
        var first = method.Parameters[method.IsExtensionMethod ? 1 : 0].Type.ToDisplayString();
        bool push = !client && (first == Support.SessionRefType || first == Support.ClientGroupType);

        var message = method.Parameters[method.Parameters.Length - 1].Type;
        if (message is ITypeParameterSymbol) return; // generic plumbing: checked where the type is known
        var location = invocation.Syntax.GetLocation();
        void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
            context.ReportDiagnostic(Diagnostic.Create(d, l ?? location, a));
        string usedBy = method.Name;
        if (Shapes.IsMessage(message, location, usedBy, Report))
            Shapes.CheckFlow(message, toClient: push, fromClient: client, location, usedBy, Report);
    }

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
        "'{0}' must have a public parameterless constructor (the runtime creates one instance per shard/activation)",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor WrongFlow = new("DAS006", "Message used against its flow",
        "'{0}' (used by {1}) {2}", Category, DiagnosticSeverity.Error, true);
}
