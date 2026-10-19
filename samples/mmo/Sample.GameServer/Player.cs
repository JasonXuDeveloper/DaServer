using DaServer;
using DaServer.Runtime;
using EasyEcs;
using Sample.Contracts;

namespace Sample.GameServer;

/// <summary>
/// The player: its profile (stored), which map it is in and the session it plays from. The map simulates the avatar;
/// the player only turns messages into data and asks the maps.
/// </summary>
[Actor<PlayerId>]
public static class Player
{
    /// <summary>A new player (no stored profile).</summary>
    [Handle] internal static void On(in Created _, ref PlayerProfile profile, in Context ctx) => profile = new PlayerProfile("player-" + ctx.Id<PlayerId>().Value, 0);

    /// <summary>The login attaches the client's session: the login is answered once the binding applied.</summary>
    [Handle]
    internal static Answer<bool> On(in Attach m, ref PlayerProfile profile, ref PlayerState state, in Context ctx)
    {
        profile.Logins++;
        state.Session = m.Session;
        ctx.Send(RealmId.The, new PlayerOnline(ctx.Id<PlayerId>()));
        return ctx.ReplyAfter(ctx.BindSession(m.Session), true); // GetInfo / EnterMap from the session reach this player from then on
    }

    [Handle]
    internal static void On(in SessionClosed _, ref PlayerState state, in Context ctx)
    {
        // The map got its own SessionClosed (the session was bound to it too) and removed the avatar.
        state = default;
        ctx.Send(RealmId.The, new PlayerOffline(ctx.Id<PlayerId>()));
    }

    [Handle] internal static void On(in SessionResumed e, ref PlayerState state) => state.Session = e.Session; // not offline: nothing to resend

    [Handle]
    internal static PlayerInfo On(in GetInfo _, in PlayerProfile profile, in PlayerState state, in Context ctx) =>
        new(ctx.Id<PlayerId>(), profile.Name, state.Map, profile.Logins);

    /// <summary>
    /// Leave the previous map (if any), then enter <see cref="EnterMap.Map"/>: one after the other, so the gateway
    /// unbinds the old map before it binds the new one. The steps are the answers below; the change in flight is
    /// <see cref="Entering"/>, the caller's reply token travels with the asks.
    /// </summary>
    [Handle]
    internal static Answer<bool> On(in EnterMap m, in PlayerState state, Entity self, in Context ctx)
    {
        var previous = state.Map; // read before the structural change below (it may move the row)
        if (previous == m.Map) return true;
        if (self.HasEntering) return false; // one change at a time (a client awaits its EnterMap)
        ClientSession session = ctx.Caller;
        self.SetEntering(m.Map, session);
        if (previous != default) ctx.Ask(previous, new Leave(session), ctx.ReplyLater());
        else ctx.Ask(m.Map, new Enter(ctx.Id<PlayerId>(), session), ctx.ReplyLater());
        return Answer<bool>.Later;
    }

    [Handle]
    internal static void On(in Answered<Leave, bool> a, ref PlayerState state, Entity self, in Context ctx)
    {
        if (!self.TryGetEntering(out var entering)) return;
        if (!a.Ok)
        {
            self.RemoveEntering();
            ctx.Fail(a.ReplyTo, a.Status); // still in the previous map
            return;
        }

        state.Map = default; // out of the previous map, not in the next one yet
        ctx.Ask(entering.Map, new Enter(ctx.Id<PlayerId>(), entering.Session), a.ReplyTo);
    }

    [Handle]
    internal static void On(in Answered<Enter, bool> a, ref PlayerState state, Entity self, in Context ctx)
    {
        if (!self.TryGetEntering(out var entering)) return;
        if (a.Ok) state.Map = entering.Map;
        self.RemoveEntering(); // `state` is invalid from here on
        if (a.Ok) ctx.Reply(a.ReplyTo, a.Value);
        else ctx.Fail(a.ReplyTo, a.Status);
    }
}
