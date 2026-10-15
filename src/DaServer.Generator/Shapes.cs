using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>What a [Handle] method returns: nothing to reply, or the reply of a request.</summary>
internal enum HandlerReturn
{
    Void,
    ValueTask,
    Reply,
    ValueTaskOfReply,
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

/// <summary>A [ClientMessage] type: registered for gateways in its assembly.</summary>
internal sealed record ClientMessageModel(string FullName, bool AnyKey);

/// <summary>
/// Reads messages and hosted types from symbols. The single source of truth for validity: the generators call it
/// without a reporter and skip invalid input; <see cref="DaServerAnalyzer"/> calls it with one and reports.
/// </summary>
internal static class Shapes
{
    /// <summary>The reply type of a request struct (<c>IRequest&lt;TReply&gt;</c>), or null.</summary>
    public static ITypeSymbol? ReplyOf(ITypeSymbol type) =>
        type.AllInterfaces.FirstOrDefault(i => i.ConstructedFrom.ToDisplayString() == RequestInterface)?.TypeArguments[0];

    /// <summary>
    /// A message type: a non-generic [NinoType] struct (what crosses the wire as one message). Reports why not when
    /// <paramref name="report"/> is set.
    /// </summary>
    public static bool IsMessage(ITypeSymbol type, Location? location, string usedBy, Report? report)
    {
        string? problem = type.TypeKind != TypeKind.Struct ? "must be a struct"
            : !HasAttribute(type, NinoTypeAttribute) ? "must be a [NinoType] struct"
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

    /// <summary>A [ClientMessage] struct, or null (reported).</summary>
    public static ClientMessageModel? ReadClientMessage(INamedTypeSymbol type, Report? report = null)
    {
        var attribute = GetAttribute(type, ClientMessageAttribute);
        if (attribute == null || !IsMessage(type, type.Locations.FirstOrDefault(), "[ClientMessage]", report)) return null;
        bool anyKey = attribute.NamedArguments.Any(a => a.Key == "AnyKey" && a.Value.Value is true);
        return new ClientMessageModel(type.ToDisplayString(Fqn), anyKey);
    }

    /// <summary>
    /// The runtime's wire id of a message type: [WireId] or FNV-1a of <c>Type.FullName</c> (nested types joined by '+').
    /// </summary>
    public static uint MessageId(ITypeSymbol type)
    {
        var wireId = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == WireIdAttribute);
        if (wireId?.ConstructorArguments.FirstOrDefault().Value is uint id) return id;
        string name = type.MetadataName;
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType) name = outer.MetadataName + "+" + name;
        if (!type.ContainingNamespace.IsGlobalNamespace) name = type.ContainingNamespace.ToDisplayString() + "." + name;
        return Fnv1a(name);
    }

    /// <summary>
    /// Types that cross the wire: primitives, strings, enums, arrays and BCL generics of wire types, and [NinoType]
    /// types. Anything else would fail at run time in the serializer, so it is a build error.
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

        if (type.SpecialType != SpecialType.None || type.TypeKind == TypeKind.Enum) return true;
        if (HasAttribute(type, NinoTypeAttribute)) return true;
        if (type is INamedTypeSymbol named && named.ContainingNamespace?.ToDisplayString() is { } ns &&
            (ns == "System" || ns.StartsWith("System.", System.StringComparison.Ordinal)))
            return named.TypeArguments.All(IsWireType);
        return false;
    }

    /// <summary>
    /// <c>[Handle]</c> methods of <paramref name="type"/>: one message parameter by value, reachable from generated code,
    /// one handler per message type; a request's handler returns its reply, other handlers void or ValueTask. Null
    /// when one is invalid.
    /// </summary>
    public static List<(HandlerModel Model, ITypeSymbol Message)>? ReadHandlers(INamedTypeSymbol type, Report? report)
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

            if (!IsMessage(message, location, "handler " + name, report))
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
                    ? "return void or ValueTask (" + message.Name + " is not a request)"
                    : "return " + reply.ToDisplayString() + " or ValueTask<" + reply.ToDisplayString() + "> (the reply of " + message.Name + ")");
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
            return returnType?.ToDisplayString() == "System.Threading.Tasks.ValueTask" ? HandlerReturn.ValueTask : null;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ReturnType, reply)) return HandlerReturn.Reply;
        return returnType is { IsGenericType: true } && returnType.ConstructedFrom.ToDisplayString() == "System.Threading.Tasks.ValueTask<TResult>" &&
               SymbolEqualityComparer.Default.Equals(returnType.TypeArguments[0], reply)
            ? HandlerReturn.ValueTaskOfReply
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

        var handlers = ReadHandlers(type, report);
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
