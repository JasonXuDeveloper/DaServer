using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>What a [Handle] method returns: nothing to reply, or the reply of a request, now or later (<c>Reply</c>).</summary>
internal enum HandlerReturn
{
    Void,
    Later,
    Value,
    ValueLater,
}

/// <summary>A <c>[Handle]</c> method: the message type it takes and how it returns.</summary>
internal sealed record HandlerModel(string MessageType, string Method, HandlerReturn Return);

internal enum HostedKind
{
    System,
    Actor,
    Service,

    /// <summary>Any other class with [Handle] methods: a client-side handler object (<c>client.Register(obj)</c>).</summary>
    ClientHandler,
}

/// <summary>Symbol-free description of a type the host registers: actor, service, client handler or shard system.</summary>
internal sealed record HostedModel(string FullName, HostedKind Kind, EquatableArray<HandlerModel> Handlers);

/// <summary>A request type (<c>IRequest&lt;TReply&gt;</c>): the generator emits its typed <c>Ask</c> overloads.</summary>
internal sealed record RequestModel(string Namespace, string ClassName, string FullName, string ReplyType, bool Public);

/// <summary>A <c>[FromClient]</c> message type: registered for client-facing nodes in its assembly.</summary>
internal sealed record ClientMessageModel(string FullName);

/// <summary>
/// Reads messages and hosted types from symbols. The single source of truth for validity: the generators call it
/// without a reporter and skip invalid input; <see cref="DaServerAnalyzer"/> calls it with one and reports.
/// </summary>
internal static class Shapes
{
    /// <summary>The reply type of a request struct (<c>IRequest&lt;TReply&gt;</c>), or null.</summary>
    public static ITypeSymbol? ReplyOf(ITypeSymbol type)
    {
        foreach (var i in type.AllInterfaces)
            if (Is(i, RequestInterface)) return i.TypeArguments[0];
        return null;
    }

    /// <summary>
    /// A message type: a non-generic [NinoType, Message] struct (what crosses the wire as one message; plain [NinoType]
    /// types are data inside messages). Reports why not when <paramref name="report"/> is set.
    /// </summary>
    public static bool IsMessage(ITypeSymbol type, Location? location, string usedBy, Report? report)
    {
        string? problem = type.TypeKind != TypeKind.Struct ? "must be a struct"
            : !HasAttribute(type, NinoTypeAttribute) || !HasAttribute(type, MessageAttribute) ? "must be a [NinoType, Message] struct"
            : type is INamedTypeSymbol { IsGenericType: true } ? "cannot be generic"
            : null;
        if (problem == null) return true;
        report?.Invoke(Rules.BadMessage, location, type.ToDisplayString(), usedBy, problem);
        return false;
    }

    /// <summary>A request struct with a valid reply type, or null (reported).</summary>
    public static RequestModel? ReadRequest(INamedTypeSymbol type, Report? report = null)
    {
        var reply = ReplyOf(type);
        if (reply == null) return null;
        var location = type.Locations.FirstOrDefault();
        if (!IsMessage(type, location, "IRequest", report)) return null;
        if (!IsWireType(reply))
        {
            report?.Invoke(Rules.NotWireType, location, reply.ToDisplayString(), "the reply of " + type.Name);
            return null;
        }

        string ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        string name = ns.Length > 0 ? type.ToDisplayString().Substring(ns.Length + 1) : type.ToDisplayString();
        return new RequestModel(ns, Identifier(name) + "Ask", type.ToDisplayString(Fqn), reply.ToDisplayString(Fqn),
            IsPublic(type));
    }

    /// <summary>Who besides servers a message travels between: <c>[FromClient]</c> and <c>[ToClient]</c> bits (0: servers only).</summary>
    public static int FlowOf(ITypeSymbol type) =>
        (HasAttribute(type, FromClientAttribute) ? FlowFromClient : 0) | (HasAttribute(type, ToClientAttribute) ? FlowToClient : 0);

    /// <summary>A <c>[FromClient]</c> message, or null (invalid ones are reported).</summary>
    public static ClientMessageModel? ReadClientMessage(INamedTypeSymbol type, Report? report = null)
    {
        if (!HasAttribute(type, FromClientAttribute)) return null;
        if (!IsMessage(type, type.Locations.FirstOrDefault(), "[FromClient]", report)) return null;
        return new ClientMessageModel(type.ToDisplayString(Fqn));
    }

    /// <summary>
    /// Whether <paramref name="message"/> may travel the way a call or handler uses it (reported when not):
    /// <paramref name="toClient"/> (sent to a session or group, handled on a client) needs <c>[ToClient]</c>;
    /// <paramref name="fromClient"/> (sent by a client) needs <c>[FromClient]</c>; anything a server handles or sends to
    /// servers must not be a <c>[ToClient]</c>-only message (no server handles it).
    /// </summary>
    public static bool CheckFlow(ITypeSymbol message, bool toClient, bool fromClient, Location? location, string usedBy, Report? report)
    {
        int flow = FlowOf(message);
        string? problem = toClient ? ((flow & FlowToClient) == 0 ? "needs [Message, ToClient] to reach clients" : null)
            : fromClient ? ((flow & FlowFromClient) == 0 ? "needs [Message, FromClient] to be sent by clients" : null)
            : flow == FlowToClient ? "is a [ToClient] message: only clients handle it" : null;
        if (problem == null) return true;
        report?.Invoke(Rules.WrongFlow, location, message.ToDisplayString(), usedBy, problem);
        return false;
    }

    /// <summary>
    /// The runtime's type id of a type, Nino's (<c>NinoTypeId.Of</c>): FNV-1a of its <c>[NinoFormerName]</c> or of its
    /// full name (nested types joined by '+').
    /// </summary>
    public static uint TypeIdOf(ITypeSymbol type)
    {
        if (GetAttribute(type, NinoFormerNameAttribute)?.ConstructorArguments.FirstOrDefault().Value is string former) return Fnv1a(former);
        string name = type.MetadataName;
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) name = outer.MetadataName + "+" + name;
        if (!type.ContainingNamespace.IsGlobalNamespace) name = type.ContainingNamespace.ToDisplayString() + "." + name;
        return Fnv1a(name);
    }

    /// <summary>
    /// Types that cross the wire as Nino writes them: primitives and strings, enums, unmanaged structs (copied as their
    /// memory), [NinoType] types (their own attribute or an inherited one), arrays and BCL generics of wire types and
    /// other BCL value types and classes but <c>object</c> and delegates. Anything else has no Nino formatter, so it is
    /// a build error.
    /// </summary>
    public static bool IsWireType(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsWireType(array.ElementType);
            case ITypeParameterSymbol:
                return false;
        }

        if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Delegate) return false;
        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum) return true;
        if (WireLayout.IsNinoType(type) || type is { IsUnmanagedType: true, TypeKind: TypeKind.Struct }) return true;
        if (type is INamedTypeSymbol named && InSystemNamespace(named))
            return named.TypeArguments.All(IsWireType);
        return false;
    }

    /// <summary>
    /// <c>[Handle]</c> methods of <paramref name="type"/>: one message parameter by value, reachable from generated code,
    /// one handler per message type; a request's handler returns its reply (or Reply of it), other handlers void or Reply. Null
    /// when one is invalid.
    /// </summary>
    public static List<(HandlerModel Model, ITypeSymbol Message)>? ReadHandlers(INamedTypeSymbol type, bool client, Report? report)
    {
        var handlers = new List<(HandlerModel, ITypeSymbol)>();
        bool valid = true;
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!HasAttribute(method, HandleAttribute)) continue;
            var location = method.Locations.FirstOrDefault();
            string name = type.Name + "." + method.Name;
            var message = method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None ? method.Parameters[0].Type : null;
            if (message == null || method.IsStatic || method.IsGenericMethod ||
                method.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            {
                report?.Invoke(Rules.BadHandler, location, name, "take one message parameter by value and be a public or internal instance method");
                valid = false;
                continue;
            }

            if (!IsMessage(message, location, "handler " + name, report) ||
                !CheckFlow(message, toClient: client, fromClient: false, location, "handler " + name, report))
            {
                valid = false;
                continue;
            }

            if (handlers.Any(h => SymbolEqualityComparer.Default.Equals(h.Item2, message)))
            {
                report?.Invoke(Rules.BadHandler, location, name, "be the only handler of " + message.Name + " in its type");
                valid = false;
                continue;
            }

            var returns = ReturnOf(method, ReplyOf(message));
            if (returns == null)
            {
                var reply = ReplyOf(message);
                report?.Invoke(Rules.BadHandler, location, name, reply == null
                    ? "return void or Reply (" + message.Name + " is not a request)"
                    : "return " + reply.ToDisplayString() + " or Reply<" + reply.ToDisplayString() + "> (the reply of " + message.Name + ")");
                valid = false;
                continue;
            }

            handlers.Add((new HandlerModel(message.ToDisplayString(Fqn), method.Name, returns.Value), message));
        }

        return valid ? handlers : null;
    }

    private static HandlerReturn? ReturnOf(IMethodSymbol method, ITypeSymbol? reply)
    {
        var returnType = method.ReturnType as INamedTypeSymbol;
        if (reply == null)
        {
            if (method.ReturnsVoid) return HandlerReturn.Void;
            return Is(returnType, "DaServer.Reply") ? HandlerReturn.Later : null;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, reply)) return HandlerReturn.Value;
        return returnType is { IsGenericType: true } && Is(returnType, "DaServer.Reply`1") &&
               SymbolEqualityComparer.Default.Equals(returnType.TypeArguments[0], reply)
            ? HandlerReturn.ValueLater
            : null;
    }

    public static HostedModel? ReadHosted(INamedTypeSymbol type, Report? report = null)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType) return null;

        HostedKind kind = DerivesFrom(type, EcsSystem) ? HostedKind.System
            : DerivesFrom(type, ActorBase) ? HostedKind.Actor
            : DerivesFrom(type, ServiceBase) ? HostedKind.Service
            : HostedKind.ClientHandler;
        if (kind == HostedKind.System) return new HostedModel(type.ToDisplayString(Fqn), kind, default);

        var handlers = ReadHandlers(type, client: kind == HostedKind.ClientHandler, report);
        if (handlers == null || (kind == HostedKind.ClientHandler && handlers.Count == 0)) return null;
        if (!IsReachable(type))
        {
            report?.Invoke(Rules.BadHandler, type.Locations.FirstOrDefault(), type.Name,
                "be declared in a public or internal type (generated registration calls it)");
            return null;
        }

        if (kind != HostedKind.ClientHandler &&
            !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
        {
            report?.Invoke(Rules.NoDefaultConstructor, type.Locations.FirstOrDefault(), type.Name);
            return null;
        }

        return new HostedModel(type.ToDisplayString(Fqn), kind,
            new EquatableArray<HandlerModel>(handlers.Select(h => h.Model).OrderBy(h => h.MessageType).ToArray()));
    }

    /// <summary>Accessible from generated code in the same assembly (no private or protected type on the way).</summary>
    private static bool IsReachable(ISymbol symbol)
    {
        for (var s = symbol; s != null && s is not INamespaceSymbol; s = s.ContainingSymbol)
            if (s.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                return false;
        return true;
    }

    private static bool IsPublic(ISymbol symbol)
    {
        for (var s = symbol; s != null && s is not INamespaceSymbol; s = s.ContainingSymbol)
            if (s.DeclaredAccessibility != Accessibility.Public) return false;
        return true;
    }
}
