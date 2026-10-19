using Nino.Core;

namespace DaServer.Tests;

// ---------------------------------------------------------------- client-facing: login, player, rooms

[NinoType, Message, FromClient] public partial record struct Login(PlayerId Player) : IRequest<bool>;

/// <summary>Logs in behind a burst of 4 KB pushes to the caller (the binding must not be overtaken).</summary>
[NinoType, Message, FromClient] public partial record struct LoginAfterPushes(PlayerId Player, int Pushes) : IRequest<bool>;

[NinoType, Message, FromClient] public partial record struct Logout : IRequest<bool>;
[NinoType, Message, FromClient] public partial record struct AddGold(int Amount) : IRequest<int>;
[NinoType, Message, FromClient] public partial record struct Tip(int Amount);            // one-way

/// <summary>The player holds its turn until <see cref="TurnGate"/> opens.</summary>
[NinoType, Message, FromClient] public partial record struct HoldPlayer : IRequest<bool>;

/// <summary>The player waits for <see cref="TurnGate"/>, then kicks the calling session.</summary>
[NinoType, Message, FromClient] public partial record struct KickAfterGate : IRequest<bool>;

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
/// <summary>The lounge holds its turn until <see cref="TurnGate"/> opens (it cannot move meanwhile).</summary>
[NinoType, Message] public partial record struct HoldLounge : IRequest<bool>;
/// <summary>A seated client asks the lounge itself (allowed only while its session holds the grant).</summary>
[NinoType, Message, FromClient] public partial record struct Stand : IRequest<bool>;

// Pushes (server to client).
[NinoType, Message, ToClient] public partial record struct Chat(RoomId From, string Text);
[NinoType, Message, ToClient] public partial record struct GoldChanged(int Gold);

public sealed class LobbyService : Service
{
    [Handle]
    public async Reply<bool> On(Enter m) => m.Room.Value > 0 && await Cluster.Ask(m.Room, new Admit(Caller));

    [Handle] public async Reply<bool> On(SitDown m) => await Cluster.Ask(m.Lounge, new Seat(Caller));
}

public sealed class AuthService : Service
{
    [Handle]
    public async Reply<bool> On(Login m) =>
        Caller.IsValid && m.Player.Value > 0 && await Cluster.Ask(m.Player, new Attach(Caller));

    [Handle]
    public Reply<bool> On(LoginAfterPushes m)
    {
        var text = new string('x', 2048); // 4 KB (UTF-16)
        for (int i = 0; i < m.Pushes; i++) Cluster.Send(Caller, new Chat(default, text));
        return On(new Login(m.Player));
    }
}


public sealed class Player : Actor<PlayerId, Wallet>
{
    [Handle]
    public async Reply<bool> On(Attach m)
    {
        await BindSession(m.Session);
        return true;
    }

    [Handle]
    public async Reply<bool> On(Logout _)
    {
        await UnbindSession(Caller);
        return true;
    }

    [Handle]
    public int On(AddGold m)
    {
        int gold = State.Gold += m.Amount;
        if (Caller.IsValid) Cluster.Send(Caller, new GoldChanged(gold)); // push to the calling client
        return gold;
    }

    [Handle] public void On(Tip m) => State.Gold += m.Amount;

    [Handle]
    public async Reply<bool> On(HoldPlayer _)
    {
        await TurnGate.Current.Task;
        return true;
    }

    [Handle]
    public async Reply<bool> On(KickAfterGate _)
    {
        await TurnGate.Current.Task;
        DisconnectSession(Caller);
        return true;
    }

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"closed:{Id.Value}");

    [Handle] public void On(SessionResumed _) => Observed.Events.Enqueue($"resumed:{Id.Value}");
}

public sealed class Room : Actor<RoomId>
{
    [Handle]
    public async Reply<bool> On(Admit m)
    {
        await GrantSession(m.Session); // the session joins Group with the grant
        return true;
    }

    [Handle] public void On(Say m) => Cluster.Send(Group, new Chat(Id, m.Text));

    [Handle]
    public async Reply<bool> On(Exit _)
    {
        await RevokeSession(Caller);
        return true;
    }

    [Handle] public void On(SessionClosed _) => Observed.Events.Enqueue($"room-closed:{Id.Value}");

    [Handle] public void On(SessionResumed _) => Observed.Events.Enqueue($"room-resumed:{Id.Value}");
}

public readonly record struct LoungeId(long Value) : IActorId;

public sealed class Lounge : Actor<LoungeId>
{
    [Handle]
    public async Reply<bool> On(Seat m)
    {
        await GrantSession(m.Session);
        return true;
    }

    [Handle]
    public async Reply<bool> On(HoldLounge _)
    {
        await TurnGate.Current.Task;
        return true;
    }

    [Handle] public bool On(Stand _) => true;

    /// <summary>The sessions the runtime holds for this actor.</summary>
    [Handle] public int On(Seated _) => Entity.TryGet<ClientSessions>(out var held) ? held.Sessions.Count + held.Granted.Count : 0;
}
