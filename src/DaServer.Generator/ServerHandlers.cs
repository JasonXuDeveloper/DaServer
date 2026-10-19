using System.Text;
using static DaServer.Generator.Support;

namespace DaServer.Generator;

/// <summary>What a static handler's parameter is bound to by its generated dispatch.</summary>
internal enum ParamKind
{
    Message,
    Component,
    Singleton,
    Entity,
    World,
    Context,
}

internal sealed record HandlerParam(ParamKind Kind, string Type, bool Ref);

/// <summary>How a static handler answers: nothing (one-way), its reply now, or an <c>Answer&lt;T&gt;</c> (now or later).</summary>
internal enum StaticReturn
{
    Void,
    Value,
    Answer,
}

/// <summary>A static <c>[Handle]</c> method of an actor or service (ADR-081).</summary>
internal sealed record StaticHandlerModel(string MessageType, string Method, StaticReturn Return, EquatableArray<HandlerParam> Params,
    string? AnsweredRequest, string? AnsweredReply);

/// <summary>An <c>[Actor&lt;TId&gt;]</c> or <c>[Service]</c> static class: its settings (attribute constants) and handlers.</summary>
internal sealed record ServerTypeModel(string FullName, string? IdType, int UnloadAfterIdleSeconds, int SaveEverySeconds,
    int MailboxCapacity, int OnError, EquatableArray<StaticHandlerModel> Handlers);

/// <summary>Reads and emits the actors and services of the ECS model: static classes of stateless handlers.</summary>
internal static class ServerHandlers
{
    public const string ActorAttribute = "DaServer.Runtime.ActorAttribute`1";
    public const string ServiceAttribute = "DaServer.Runtime.ServiceAttribute";
    private const string ContextType = "DaServer.Runtime.Context";
    private const string AnswerType = "DaServer.Runtime.Answer`1";
    private const string AnsweredType = "DaServer.Runtime.Answered`2";
    private const string EntityType = "EasyEcs.Entity";
    private const string WorldType = "EasyEcs.World";
    private const string ComponentInterface = "EasyEcs.IComponent";
    private const string SingletonInterface = "EasyEcs.ISingletonComponent";

    /// <summary>Whether <paramref name="type"/> is an actor's handler class (<c>[Actor&lt;TId&gt;]</c>).</summary>
    public static bool IsActor(INamedTypeSymbol type) => HasAttribute(type, ActorAttribute);

    /// <summary>Whether <paramref name="type"/> is an actor's or a service's handler class.</summary>
    public static bool IsHandlerType(INamedTypeSymbol type) => HasAttribute(type, ActorAttribute) || HasAttribute(type, ServiceAttribute);

    /// <summary>An actor or service type, or null when it is not one or a handler is invalid (reported).</summary>
    public static ServerTypeModel? Read(INamedTypeSymbol type, Report? report = null)
    {
        var actor = GetAttribute(type, ActorAttribute);
        bool service = HasAttribute(type, ServiceAttribute);
        if (actor == null && !service) return null;
        var location = type.Locations.FirstOrDefault();
        if (!type.IsStatic || type.IsGenericType)
        {
            report?.Invoke(Rules.BadHandler, location, type.Name, "be a non-generic static class: an actor's data is its components, its behavior static handlers");
            return null;
        }

        var handlers = new List<StaticHandlerModel>();
        var seen = new HashSet<string>();
        bool valid = true;
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (!HasAttribute(method, HandleAttribute)) continue;
            var handler = ReadHandler(method, actor != null, report);
            if (handler == null)
            {
                valid = false;
                continue;
            }

            if (!seen.Add(handler.MessageType))
            {
                report?.Invoke(Rules.BadHandler, method.Locations.FirstOrDefault(), type.Name + "." + method.Name,
                    "be the only handler of its message in " + type.Name);
                valid = false;
                continue;
            }

            handlers.Add(handler);
        }

        if (!valid) return null;
        string? idType = actor?.AttributeClass!.TypeArguments[0].ToDisplayString(Fqn);
        int Named(string name, int fallback)
        {
            if (actor == null) return fallback; // a service: no settings
            foreach (var pair in actor.NamedArguments)
                if (pair.Key == name && pair.Value.Value is int value) return value;
            return fallback;
        }

        return new ServerTypeModel(type.ToDisplayString(Fqn), idType, Named("UnloadAfterIdleSeconds", 0), Named("SaveEverySeconds", 30),
            Named("MailboxCapacity", 0), Named("OnError", 0),
            new EquatableArray<StaticHandlerModel>(handlers.OrderBy(h => h.MessageType, StringComparer.Ordinal).ToArray()));
    }

    private static StaticHandlerModel? ReadHandler(IMethodSymbol method, bool actor, Report? report)
    {
        var location = method.Locations.FirstOrDefault();
        string name = method.ContainingType.Name + "." + method.Name;
        bool Bad(string problem)
        {
            report?.Invoke(Rules.BadHandler, location, name, problem);
            return false;
        }

        if (!method.IsStatic || method.IsGenericMethod || method.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
        {
            Bad("be an internal or public static method, not generic (the generated dispatch calls it)");
            return null;
        }

        if (method.IsAsync)
        {
            Bad("not be async: a handler runs to completion, and an ask's answer comes back as a message (Answered<TRequest, TReply>)");
            return null;
        }

        if (method.Parameters.Length == 0)
        {
            Bad("take its message as its first parameter");
            return null;
        }

        var message = method.Parameters[0].Type;
        string? answeredRequest = null, answeredReply = null;
        ITypeSymbol? reply;
        if (message is INamedTypeSymbol { IsGenericType: true } answered && Is(answered, AnsweredType))
        {
            (answeredRequest, answeredReply) = (answered.TypeArguments[0].ToDisplayString(Fqn), answered.TypeArguments[1].ToDisplayString(Fqn));
            reply = null; // an answer is one-way mail
        }
        else
        {
            if (!Shapes.IsMessage(message, location, "handler " + name, report) ||
                !Shapes.CheckFlow(message, toClient: false, fromClient: false, location, "handler " + name, report))
                return null;
            reply = Shapes.ReplyOf(message);
        }

        var parameters = new List<HandlerParam> { new(ParamKind.Message, message.ToDisplayString(Fqn), false) };
        foreach (var p in method.Parameters.Skip(1))
        {
            var t = p.Type;
            bool byRef = p.RefKind == RefKind.Ref;
            bool reads = p.RefKind == RefKind.In;
            HandlerParam? bound =
                Is(t, ContextType) && p.RefKind is RefKind.In or RefKind.None ? new HandlerParam(ParamKind.Context, "", false)
                : Is(t, EntityType) && p.RefKind == RefKind.None ? new HandlerParam(ParamKind.Entity, "", false)
                : Is(t, WorldType) && p.RefKind == RefKind.None ? new HandlerParam(ParamKind.World, "", false)
                : t.IsValueType && Implements(t, SingletonInterface) && (byRef || reads) ? new HandlerParam(ParamKind.Singleton, t.ToDisplayString(Fqn), byRef)
                : t.IsValueType && Implements(t, ComponentInterface) && (byRef || reads) ? new HandlerParam(ParamKind.Component, t.ToDisplayString(Fqn), byRef)
                : null;
            if (bound == null)
            {
                Bad("take after its message only components and singletons (ref or in), Entity, World and in Context: " + p.Name + " is none");
                return null;
            }

            if (!actor && bound.Kind is ParamKind.Component or ParamKind.Entity)
            {
                Bad("not take components or Entity: a service has no entity (its state is singletons)");
                return null;
            }

            parameters.Add(bound);
        }

        StaticReturn returns;
        var returnType = method.ReturnType as INamedTypeSymbol;
        if (reply == null)
        {
            if (!method.ReturnsVoid)
            {
                Bad("return void (" + message.Name + " is not a request)");
                return null;
            }

            returns = StaticReturn.Void;
        }
        else if (SymbolEqualityComparer.Default.Equals(method.ReturnType, reply)) returns = StaticReturn.Value;
        else if (returnType is { IsGenericType: true } && Is(returnType, AnswerType) &&
                 SymbolEqualityComparer.Default.Equals(returnType.TypeArguments[0], reply)) returns = StaticReturn.Answer;
        else
        {
            Bad("return " + reply.ToDisplayString() + " or Answer<" + reply.ToDisplayString() + "> (the reply of " + message.Name + ")");
            return null;
        }

        return new StaticHandlerModel(message.ToDisplayString(Fqn), method.Name, returns,
            new EquatableArray<HandlerParam>(parameters.ToArray()), answeredRequest, answeredReply);
    }

    /// <summary>The registration of one actor or service type: its settings and one generated dispatch per handler.</summary>
    public static void Emit(StringBuilder sb, ServerTypeModel t)
    {
        string id = t.IdType != null ? $"typeof({t.IdType})" : "null";
        sb.Append($"        ServerRegistry.RegisterHandlerType(new HandlerTypeInfo(typeof({t.FullName}), {id},\n");
        sb.Append($"            new ActorSettings({t.UnloadAfterIdleSeconds}, {t.SaveEverySeconds}, {t.MailboxCapacity}, (ErrorAction){t.OnError}), [");
        foreach (var h in t.Handlers)
        {
            string messageType = h.AnsweredRequest != null
                ? $"global::DaServer.Runtime.Answered<{h.AnsweredRequest}, {h.AnsweredReply}>"
                : h.MessageType;
            sb.Append($"\n            ActorHandler.Of<{messageType}>(static (in Context ctx, global::System.ReadOnlySpan<byte> payload) =>\n            {{\n");
            sb.Append(h.AnsweredRequest != null
                ? $"                if (!HandlerCall.TryRead<{h.AnsweredRequest}, {h.AnsweredReply}>(payload, out var m)) return;\n"
                : $"                if (!global::DaServer.Rpc.MessageReader.TryRead(payload, out {messageType} m)) {{ HandlerCall.Malformed(in ctx); return; }}\n" +
                  $"                if (!global::DaServer.Rpc.MessageFilters<{messageType}>.Admit(in ctx, in m)) return;\n");

            var components = h.Params.Where(p => p.Kind == ParamKind.Component).ToArray();
            if (components.Length > 0 || h.Params.Any(p => p.Kind == ParamKind.Entity))
                sb.Append("                var e = ctx.Entity;\n");
            if (components.Length > 0) // an actor's components exist from their first use: added with their default value
            {
                sb.Append("                if (!(").Append(string.Join(" && ", components.Select(c => $"e.Has<{c.Type}>()"))).Append("))\n");
                sb.Append("                {\n");
                foreach (var c in components) sb.Append($"                    e.GetOrAdd<{c.Type}>();\n");
                sb.Append("                }\n");
            }

            if (h.Params.Any(p => p.Kind is ParamKind.Singleton or ParamKind.World)) sb.Append("                var w = ctx.World;\n");
            var args = h.Params.Select(p => p.Kind switch
            {
                ParamKind.Message => "in m",
                ParamKind.Component => p.Ref ? $"ref e.Get<{p.Type}>()" : $"in e.Read<{p.Type}>()",
                ParamKind.Singleton => p.Ref ? $"ref w.GetSingleton<{p.Type}>()" : $"in w.GetSingleton<{p.Type}>()",
                ParamKind.Entity => "e",
                ParamKind.World => "w",
                _ => "in ctx",
            });
            string call = $"{t.FullName}.{h.Method}({string.Join(", ", args)})";
            sb.Append(h.Return switch
            {
                StaticReturn.Void => $"                {call};\n",
                StaticReturn.Value => $"                HandlerCall.Reply(in ctx, {call});\n",
                _ => $"                HandlerCall.Answer(in ctx, {call});\n",
            });
            sb.Append("            }),");
        }

        sb.Append(t.Handlers.Length > 0 ? "\n        " : "").Append("]));\n");
    }
}
