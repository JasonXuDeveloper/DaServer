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
    private const string FilterAttribute = "DaServer.Rpc.FilterAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.BadHandler, Rules.BadMessage, Rules.NotWireType, Rules.IdCollision, Rules.WrongFlow, Rules.StatefulHandlerType, Rules.WrongAddress, Rules.SharedReferencesServer, Rules.BlockingCall, Rules.AskInSystem, Rules.ComponentInLogic, Rules.BadPersisted, Rules.OffShard, Rules.BadPlacement, Rules.WireBreak, Rules.WireUnrecorded, Rules.ClientCollection, Rules.KickWithDisconnectSession, Rules.BadFilter, Rules.StaleComponentRef);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(CheckShared);
        context.RegisterCompilationAction(CheckWireManifest);
        context.RegisterCompilationStartAction(start =>
        {
            // Wire ids of every message type declared in this compilation, for the collision check at the end.
            var ids = new ConcurrentDictionary<uint, (string Name, Location? Location)>();
            var collisions = new ConcurrentBag<Diagnostic>();
            bool logic = IsLogic(start.Compilation, start.Options);

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
                    symbolContext.ReportDiagnostic(Diagnostic.Create(d, l ?? type.Locations.FirstOrDefault(), a));

                Shapes.ReadHosted(type, Report);
                ServerHandlers.Read(type, Report);
                Shapes.ReadRequest(type, Report);
                Shapes.ReadClientMessage(type, Report);
                if (Support.HasAttribute(type, FilterAttribute)) Shapes.ReadFilter(type, Report);
                foreach (var member in type.GetMembers())
                    if (member is IMethodSymbol && Support.HasAttribute(member, FilterAttribute)) Shapes.ReadFilter(member, Report);
                CheckStateless(type, logic, Report);
                if (logic) CheckNoComponents(type, Report);
                CheckPersisted(type, Report);
                if (type.TypeKind != TypeKind.Interface && Support.Implements(type, Support.Placed) && !(type.IsValueType && Support.Implements(type, Support.ActorId)))
                    Report(Rules.BadPlacement, null, type.Name);

                if (type.TypeKind != TypeKind.Struct || !Support.HasAttribute(type, Support.MessageAttribute)) return;
                if (!Shapes.IsMessage(type, type.Locations.FirstOrDefault(), "[Message]", Report)) return;
                CheckClientCollections(type, Report);
                string name = type.ToDisplayString();
                var location = type.Locations.FirstOrDefault();
                var existing = ids.GetOrAdd(Shapes.TypeIdOf(type), (name, location));
                if (existing.Name != name)
                    collisions.Add(Diagnostic.Create(Rules.IdCollision, location, name, existing.Name));
            }, SymbolKind.NamedType);

            start.RegisterOperationAction(CheckSend, OperationKind.Invocation);
            start.RegisterOperationAction(CheckBlocking, OperationKind.Invocation, OperationKind.PropertyReference, OperationKind.Lock);
            start.RegisterOperationAction(CheckOffShard, OperationKind.Invocation, OperationKind.ObjectCreation);
            start.RegisterOperationAction(CheckStaleRefs, OperationKind.MethodBody);

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var d in collisions) end.ReportDiagnostic(d);
            });
        });
    }
}

/// <summary>Shared logic (rules and systems clients run too) cannot depend on the server or the transport.</summary>
public sealed partial class DaServerAnalyzer
{
    private static readonly string[] ServerAssemblies = ["DaServer", "DaServer.Hosting", "DaServer.Core", "DaServer.Client"];

    private static void CheckShared(CompilationAnalysisContext context)
    {
        if (!RegistrationGenerator.IsShared(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions)) return;
        foreach (var reference in context.Compilation.ReferencedAssemblyNames)
            if (System.Array.IndexOf(ServerAssemblies, reference.Name) >= 0)
                context.ReportDiagnostic(Diagnostic.Create(Rules.SharedReferencesServer, Location.None,
                    context.Compilation.AssemblyName, reference.Name));
    }
}

/// <summary>Actors and services hold no state of their own: it lives in components and singletons.</summary>
public sealed partial class DaServerAnalyzer
{
    /// <summary>
    /// Mutable fields of an actor or service class (static: they are static classes) or of a server system type: the
    /// runtime persists, moves and inspects components only, and every shard thread runs the same handlers, so such state
    /// would be lost or shared across shard threads. An actor's state is its components, a system's a singleton.
    /// </summary>
    private static void CheckStateless(INamedTypeSymbol type, bool logic, Report report)
    {
        if (type.TypeKind != TypeKind.Class || !(ServerHandlers.IsHandlerType(type) || (logic && Support.DerivesFrom(type, Support.EcsSystem))))
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
    private static readonly string[] Senders = ["DaServer.IMessageSender", "DaServer.Runtime.ICluster", "DaServer.Runtime.Context", Support.ClusterClientType];

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
        if (method.Name is not ("Send" or "SendAfter" or "Ask" or "Publish") || method.Parameters.Length == 0) return;

        var sender = method.IsExtensionMethod ? method.Parameters[0].Type : method.ContainingType;
        if (sender == null || !IsSender(sender)) return;
        var receiver = method.IsExtensionMethod ? Unwrap(invocation.Arguments.FirstOrDefault()?.Value) : Unwrap(invocation.Instance);
        bool client = Support.Is(receiver, Support.ClusterClientType);

        int messageIndex = method.Parameters.Length - 1;
        if (messageIndex > 0 && (Support.Is(method.Parameters[messageIndex].Type, "DaServer.CallOptions") || // Ask(.., options)
                                 Support.Is(method.Parameters[messageIndex].Type, "DaServer.Runtime.ReplyTo"))) messageIndex--; // ctx.Ask(.., forward)
        var message = method.Parameters[messageIndex].Type;
        if (message is ITypeParameterSymbol) return; // generic plumbing: checked where the type is known
        int addressIndex = messageIndex - 1;
        var address = addressIndex >= (method.IsExtensionMethod ? 1 : 0) ? method.Parameters[addressIndex].Type : null;
        if (Support.Is(address, "System.TimeSpan")) address = null; // SendAfter's delay, not an address
        if (address is ITypeParameterSymbol) return;
        bool toClient = address != null && IsClientAddress(address);
        bool notActor = address != null && !Support.Implements(address, Support.ActorId);

        var location = invocation.Syntax.GetLocation();
        void Report(DiagnosticDescriptor d, Location? l, params object[] a) =>
            context.ReportDiagnostic(Diagnostic.Create(d, l ?? location, a));
        string usedBy = method.Name;
        if (notActor && method.Name == "Ask")
            Report(Rules.WrongAddress, location, address!.ToDisplayString(), "is not an actor id: send to it, only actors are asked");
        else if (client && notActor)
            Report(Rules.WrongAddress, location, address!.ToDisplayString(), "is not an actor id: clients send to actors and services only");
        else if (Shapes.IsMessage(message, location, usedBy, Report))
        {
            Shapes.CheckFlow(message, toClient, fromClient: client, location, usedBy, Report);
            if (Support.Is(message, "DaServer.Disconnect") && (Support.Is(address, "DaServer.ClientSession") || Support.Is(address, "DaServer.Origin")) &&
                context.ContainingSymbol?.ContainingType is { } container && ServerHandlers.IsActor(container))
                Report(Rules.KickWithDisconnectSession, location);
        }
    }

    private static bool IsClientAddress(ITypeSymbol type)
    {
        foreach (var name in Support.ClientAddresses)
            if (Support.Is(type, name)) return true;
        return false;
    }

    private static bool IsSender(ITypeSymbol type)
    {
        foreach (var name in Senders)
            if (Support.Is(type, name) || Support.Implements(type, name)) return true;
        return false;
    }

    private static ITypeSymbol? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion) operation = conversion.Operand;
        return operation?.Type;
    }
}

/// <summary>
/// Components and the logic over them live in separate projects (ADR-067): a project on the server runtime (its actors,
/// services and systems are what a reload replaces) declares no component or singleton; those live in a components
/// project, which is never reloaded, so their types (the keys of ECS storage) stay the same. Shared logic is exempt: it
/// is never reloaded (clients run it too).
/// </summary>
public sealed partial class DaServerAnalyzer
{
    private static bool IsLogic(Compilation compilation, AnalyzerOptions options)
    {
        if (RegistrationGenerator.IsShared(options.AnalyzerConfigOptionsProvider.GlobalOptions)) return false;
        foreach (var reference in compilation.ReferencedAssemblyNames)
            if (reference.Name == "DaServer") return true;
        return false;
    }

    /// <summary>A [Persist] or [Handoff] type is a [NinoType] struct component: its bytes are a section of its actor's record.</summary>
    private static void CheckPersisted(INamedTypeSymbol type, Report report)
    {
        string? attribute = Support.HasAttribute(type, Support.PersistAttribute) ? "Persist"
            : Support.HasAttribute(type, Support.HandoffAttribute) ? "Handoff" : null;
        if (attribute == null) return;
        if (type.TypeKind != TypeKind.Struct || !Support.Implements(type, "EasyEcs.IComponent") || type.IsGenericType)
            report(Rules.BadPersisted, type.Locations.FirstOrDefault(), attribute, type.Name, "must be a non-generic struct component (EasyEcs.IComponent)");
        else if (type.ContainingType != null || type.DeclaredAccessibility != Accessibility.Public)
            report(Rules.BadPersisted, type.Locations.FirstOrDefault(), attribute, type.Name,
                "must be a public top-level type: the generated registration names it (a nested or internal one would never be saved)");
        else if (NinoOptions.Of(type) is not { } options)
            report(Rules.BadPersisted, type.Locations.FirstOrDefault(), attribute, type.Name, "must be a [NinoType]: its record section is its Nino bytes");
        else if (options.Format == NinoOptions.Positional)
            report(Rules.BadPersisted, type.Locations.FirstOrDefault(), attribute, type.Name,
                "must be [NinoType(format: NinoFormat.VersionTolerant)] (or Keyed): its bytes outlive the build that wrote them, and a positional layout cannot change");
    }

    /// <summary>
    /// DAS019: a <c>[FromClient]</c>/<c>[ToClient]</c> message travels at client volume, and a collection it holds
    /// directly is a new array or collection on every message read. A <c>[NinoPooled]</c> class holding it is reused.
    /// </summary>
    private static void CheckClientCollections(INamedTypeSymbol type, Report report)
    {
        if (Shapes.FlowOf(type) == 0) return;
        foreach (var member in type.GetMembers())
        {
            var held = member switch
            {
                IFieldSymbol { IsStatic: false, IsImplicitlyDeclared: false } f => f.Type,
                IPropertySymbol { IsStatic: false } p when !p.IsIndexer => p.Type,
                _ => null,
            };
            if (held is IArrayTypeSymbol || held is INamedTypeSymbol { IsGenericType: true } g && Support.InSystemNamespace(g) && g.TypeKind == TypeKind.Class)
                report(Rules.ClientCollection, member.Locations.FirstOrDefault(), type.Name, member.Name, held.ToDisplayString());
        }
    }

    private static void CheckNoComponents(INamedTypeSymbol type, Report report)
    {
        if (type.TypeKind != TypeKind.Struct || type.IsImplicitlyDeclared) return;
        if (Support.Implements(type, "EasyEcs.IComponent") || Support.Implements(type, "EasyEcs.ISingletonComponent"))
            report(Rules.ComponentInLogic, type.Locations.FirstOrDefault(), type.Name);
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
        "'{0}' (used by {1}) must be a primitive, string, enum, unmanaged struct, array, BCL collection or a [NinoType] type (not object or a delegate)", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor IdCollision = new("DAS004", "Message id collision",
        "Messages '{0}' and '{1}' hash to the same type id; give one of them another id with [NinoFormerName(\"...\")]", Category,
        DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor StatefulHandlerType = new("DAS007", "Actor, service or system with fields",
        "'{0}.{1}' holds state outside ECS: an actor's state is its components (handler parameters), a service's and a system's singletons",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor WrongFlow = new("DAS006", "Message used against its flow",
        "'{0}' (used by {1}) {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor SharedReferencesServer = new("DAS010", "Shared logic references the server",
        "Shared assembly '{0}' references {1}: shared logic (<DaServerShared>true</DaServerShared>) runs on clients too and may reference contracts and EasyEcs only",
        Category, DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor BlockingCall = new("DAS009", "Synchronous wait on a shard thread",
        "'{0}' blocks the shard thread, and every actor of the shard with it: {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor AskInSystem = new("DAS012", "A system asks",
        "Systems do not wait for replies (an answer arriving ticks later may be stale): read and write the components of this world directly (queries, Entity handles), or Send an intent to the owner of data in another world, which validates it and sends the outcome back as a message",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor ComponentInLogic = new("DAS013", "Component declared beside logic",
        "Component '{0}' is declared in a project that runs on the server runtime: declare it in a components project (it references contracts and EasyEcs, not the DaServer runtime), so the data outlives a reload of this project's actors, services and systems",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadPersisted = new("DAS014", "Invalid persisted component",
        "[{0}] '{1}' {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadPlacement = new("DAS016", "IPlaced on a type that is not an actor id",
        "'{0}' implements IPlaced: only actor id types (structs implementing IActorId) are placed", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor OffShard = new("DAS015", "Work started off the shard thread",
        "'{0}' {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor WireBreak = new("DAS017", "Wire layout breaks the manifest",
        "'{0}' {1} (wire.manifest, ADR-076; a deliberate break: dotnet build -p:UpdateWireManifest=force)", Category, DiagnosticSeverity.Error, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor StaleComponentRef = new("DAS022", "A component parameter used after a structural change",
        "'{0}' is used after '{1}' moved the actor's entity to another table: it points to the old row, so a write is lost; finish with the components before adding or removing one, or read it again (self.Get<T>())",
        Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadFilter = new("DAS021", "Invalid filter",
        "[Filter] '{0}' {1}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor KickWithDisconnectSession = new("DAS020", "An actor disconnects a session with Send",
        "An actor disconnects a session with ctx.DisconnectSession(session): it withdraws the session's grant at once, so a resume racing the kick is refused",
        Category, DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor ClientCollection = new("DAS019", "Collection held directly by a client message",
        "'{0}.{1}' ({2}) allocates on every message read: client messages travel at client volume; hold it in a [NinoPooled] class the handler returns",
        Category, DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor WireUnrecorded = new("DAS018", "Wire manifest out of date",
        "'{0}' {1}: record it with dotnet build -p:UpdateWireManifest=true and commit wire.manifest", Category,
        DiagnosticSeverity.Warning, true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor WrongAddress = new("DAS008", "Wrong kind of address",
        "'{0}' {1}", Category, DiagnosticSeverity.Error, true);
}
