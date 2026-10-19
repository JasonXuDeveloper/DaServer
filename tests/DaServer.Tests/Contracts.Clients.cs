using EasyEcs;
using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- client-facing: login, player, rooms

[NinoType, Message, FromClient] public partial record struct Login(PlayerId Player) : IRequest<bool>;

/// <summary>Logs in behind a burst of 4 KB pushes to the caller (the binding must not be overtaken).</summary>
[NinoType, Message, FromClient] public partial record struct LoginAfterPushes(PlayerId Player, int Pushes) : IRequest<bool>;

[NinoType, Message, FromClient] public partial record struct Logout : IRequest<bool>;
[NinoType, Message, FromClient] public partial record struct AddGold(int Amount) : IRequest<int>;
[NinoType, Message, FromClient] public partial record struct Tip(int Amount);            // one-way

/// <summary>The player kicks the calling session.</summary>
[NinoType, Message, FromClient] public partial record struct Kick : IRequest<bool>;

/// <summary>Server-only: the login service attaches a client session to its player.</summary>
[NinoType, Message] public partial record struct Attach(ClientSession Session) : IRequest<bool>;

/// <summary>
/// A chat room: the lobby admits clients (<see cref="Enter"/>), the room grants their sessions, and they name the room
/// from then on (<c>client.Send(roomId, ..)</c>) until they <see cref="Exit"/>.
/// </summary>
[NinoType, Message, FromClient] public partial record struct Enter(RoomId Room) : IRequest<bool>;
[NinoType, Message] public partial record struct Admit(ClientSession Session) : IRequest<bool>;
[NinoType, Message, FromClient] public partial record struct Say(string Text);
[NinoType, Message, FromClient] public partial record struct Exit : IRequest<bool>;

/// <summary>A lounge grants sessions but handles no <see cref="SessionClosed"/>: the runtime still forgets them.</summary>
[NinoType, Message, FromClient] public partial record struct SitDown(LoungeId Lounge) : IRequest<bool>;
[NinoType, Message] public partial record struct Seat(ClientSession Session) : IRequest<bool>;
[NinoType, Message] public partial record struct Seated : IRequest<int>;
/// <summary>A seated client asks the lounge itself (allowed only while its session holds the grant).</summary>
[NinoType, Message, FromClient] public partial record struct Stand : IRequest<bool>;

// Pushes (server to client).
[NinoType, Message, ToClient] public partial record struct Chat(RoomId From, string Text);
[NinoType, Message, ToClient] public partial record struct GoldChanged(int Gold);

[Service]
public static class LobbyService
{
    [Handle]
    internal static Answer<bool> On(in Enter m, in Context ctx)
    {
        if (m.Room.Value <= 0) return false;
        ctx.Ask(m.Room, new Admit(ctx.Caller), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle] internal static void On(in Answered<Admit, bool> a, in Context ctx) => Pass(a.Status, a.Value, a.ReplyTo, in ctx);

    [Handle]
    internal static Answer<bool> On(in SitDown m, in Context ctx)
    {
        ctx.Ask(m.Lounge, new Seat(ctx.Caller), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle] internal static void On(in Answered<Seat, bool> a, in Context ctx) => Pass(a.Status, a.Value, a.ReplyTo, in ctx);

    /// <summary>The asked actor's answer is the client's reply; its failure too.</summary>
    internal static void Pass(RpcStatus status, bool value, ReplyTo to, in Context ctx)
    {
        if (status == RpcStatus.Ok) ctx.Reply(to, value);
        else ctx.Fail(to, status);
    }
}

[Service]
public static class AuthService
{
    [Handle] internal static Answer<bool> On(in Login m, in Context ctx) => LogIn(m.Player, in ctx);

    [Handle]
    internal static Answer<bool> On(in LoginAfterPushes m, in Context ctx)
    {
        var text = new string('x', 2048); // 4 KB (UTF-16)
        for (int i = 0; i < m.Pushes; i++) ctx.Cluster.Send(ctx.Caller, new Chat(default, text));
        return LogIn(m.Player, in ctx);
    }

    [Handle] internal static void On(in Answered<Attach, bool> a, in Context ctx) => LobbyService.Pass(a.Status, a.Value, a.ReplyTo, in ctx);

    /// <summary>A client session attaches to its player: replied once the player bound it.</summary>
    private static Answer<bool> LogIn(PlayerId player, in Context ctx)
    {
        if (!ctx.Caller.IsValid || player.Value <= 0) return false;
        ctx.Ask(player, new Attach(ctx.Caller), ctx.ReplyLater());
        return Answer<bool>.Later;
    }
}

[Actor<PlayerId>]
public static class Player
{
    [Handle] internal static Answer<bool> On(in Attach m, in Context ctx) => ctx.ReplyAfter(ctx.BindSession(m.Session), true);

    [Handle] internal static Answer<bool> On(in Logout _, in Context ctx) => ctx.ReplyAfter(ctx.UnbindSession(ctx.Caller), true);

    [Handle]
    internal static int On(in AddGold m, ref Wallet wallet, in Context ctx)
    {
        int gold = wallet.Gold += m.Amount;
        if (ctx.Caller.IsValid) ctx.Cluster.Send(ctx.Caller, new GoldChanged(gold)); // push to the calling client
        return gold;
    }

    [Handle] internal static void On(in Tip m, ref Wallet wallet) => wallet.Gold += m.Amount;

    [Handle]
    internal static bool On(in Kick _, in Context ctx)
    {
        ctx.DisconnectSession(ctx.Caller);
        return true;
    }

    [Handle] internal static void On(in SessionClosed _, in Context ctx) => Observed.Events.Enqueue($"closed:{ctx.Key}");

    [Handle] internal static void On(in SessionResumed _, in Context ctx) => Observed.Events.Enqueue($"resumed:{ctx.Key}");
}

[Actor<RoomId>]
public static class Room
{
    /// <summary>The session joins the room's group with the grant.</summary>
    [Handle] internal static Answer<bool> On(in Admit m, in Context ctx) => ctx.ReplyAfter(ctx.GrantSession(m.Session), true);

    [Handle] internal static void On(in Say m, in Context ctx) => ctx.Cluster.Send(ctx.Group, new Chat(ctx.Id<RoomId>(), m.Text));

    [Handle] internal static Answer<bool> On(in Exit _, in Context ctx) => ctx.ReplyAfter(ctx.RevokeSession(ctx.Caller), true);

    [Handle] internal static void On(in SessionClosed _, in Context ctx) => Observed.Events.Enqueue($"room-closed:{ctx.Key}");

    [Handle] internal static void On(in SessionResumed _, in Context ctx) => Observed.Events.Enqueue($"room-resumed:{ctx.Key}");
}

public readonly record struct LoungeId(long Value) : IActorId;

[Actor<LoungeId>]
public static class Lounge
{
    [Handle] internal static Answer<bool> On(in Seat m, in Context ctx) => ctx.ReplyAfter(ctx.GrantSession(m.Session), true);

    [Handle] internal static bool On(in Stand _) => true;

    /// <summary>The sessions the runtime holds for this actor.</summary>
    [Handle]
    internal static int On(in Seated _, Entity self, World world)
    {
        int held = 0;
        foreach (var child in world.Children(self.Id))
        {
            if (!child.HasKnownSession) continue;
            if (child.IsKnown) held++;
            if (child.IsGranted) held++;
        }

        return held;
    }
}
