using DaServer.Simulation;
using DaServer.Simulation.Planning;
using EasyEcs;
using Sample.Client;
using Sample.Contracts;

namespace Sample.Bot;

// What a bot does, as a planning domain: change maps when due, chat when due, otherwise wander. Facts come from the
// bot's schedule (SenseSystem); each task is a component carried out by one system on the client's own data
// (Steering, Speaking, EnterMapAsync): a bot is a player whose hands are a planner.

[Flags]
public enum BotFact : ulong
{
    None = 0,
    ChatDue = 1,
    TravelDue = 2,
}

/// <summary>A bot's temperament and schedule (ms on <see cref="Environment.TickCount64"/>), and its own random sequence.</summary>
public struct Bot(uint seed, int turnEveryMs, int chatEveryMs, int travelEveryMs, int maps, long start) : IComponent
{
    public uint Random = seed;
    public readonly int TurnEveryMs = turnEveryMs, ChatEveryMs = chatEveryMs, TravelEveryMs = travelEveryMs, Maps = maps;
    public long NextTurn = start, NextChat = start + chatEveryMs, NextTravel = start + travelEveryMs;

    /// <summary>xorshift32 in [0, 1).</summary>
    public float Next()
    {
        uint x = Random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        Random = x;
        return (x >> 8) * (1f / (1 << 24));
    }
}

/// <summary>Task: walk about for a second, turning now and then (0: not started).</summary>
public record struct Wandering(long Until) : IComponent;

/// <summary>Task: say something.</summary>
public struct Chatting : IComponent;

/// <summary>Task: move to another map (the client's own flow, <see cref="TaskFlow"/>; a default map id: not started).</summary>
public record struct Travelling(MapId To) : IComponent;

/// <summary>The scenario's failure counter, as a singleton of every loop's world.</summary>
public readonly record struct Trips(SwarmCounter Failed) : ISingletonComponent;

public static class Behavior
{
    /// <summary>The bots' domain: the first method whose condition holds, in order.</summary>
    public static HtnDomain Domain()
    {
        var d = new DomainBuilder<BotFact>();
        int wander = d.Primitive<Wandering>();
        int chat = d.Primitive<Chatting>(requires: BotFact.ChatDue, clears: BotFact.ChatDue);
        int travel = d.Primitive<Travelling>(requires: BotFact.TravelDue, clears: BotFact.TravelDue);
        int play = d.Compound();
        d.Method(play, BotFact.TravelDue, travel, wander) // change maps, then look around
            .Method(play, BotFact.ChatDue, chat, wander)
            .Method(play, BotFact.None, wander);
        return d.Build(play);
    }

    /// <summary>The bot's systems: its sensor, the planner and one system per task.</summary>
    public static void AddTo(World world, SwarmMeter meter)
    {
        world.AddSingleton<HtnDomain>() = Domain();
        world.AddSingleton<Trips>() = new Trips(meter.Counter("failed map changes", SwarmCounterKind.Failure));
        world.AddSystem<SenseSystem>();
        world.AddSystem<PlannerSystem>();
        world.AddSystem<WanderSystem>();
        world.AddSystem<ChatSystem>();
        world.AddSystem<TravelSystem>();
    }
}

/// <summary>Sensor: what is due, from the bot's schedule.</summary>
public sealed partial class SenseSystem : EcsSystem
{
    public override int Priority => -40; // before planning

    [ForEach]
    private static void Sense(in Bot bot, ref Facts facts)
    {
        long now = Environment.TickCount64;
        var due = BotFact.None;
        if (bot.ChatEveryMs > 0 && now >= bot.NextChat) due |= BotFact.ChatDue;
        if (bot.TravelEveryMs > 0 && bot.Maps > 1 && now >= bot.NextTravel) due |= BotFact.TravelDue;
        facts = new Facts((ulong)due);
    }
}

/// <summary>Carries out <see cref="Wandering"/>: a new random heading every so often, as a player at the stick would.</summary>
public sealed partial class WanderSystem : EcsSystem
{
    private const int WanderMs = 1000;

    public override int Priority => -20; // before the client turns steering into messages

    [ForEach]
    private static void Wander(Entity bot, ref Wandering wandering, ref Bot b, ref Steering steering)
    {
        long now = Environment.TickCount64;
        if (wandering.Until == 0) wandering = new Wandering(now + WanderMs);
        if (b.TurnEveryMs > 0 && now >= b.NextTurn)
        {
            b.NextTurn = now + b.TurnEveryMs;
            steering = new Steering(b.Next() * 2 - 1, b.Next() * 2 - 1);
        }

        if (now >= wandering.Until) bot.Remove<Wandering>(); // last: the refs are invalid after it
    }
}

/// <summary>Carries out <see cref="Chatting"/>: the client says it.</summary>
public sealed partial class ChatSystem : EcsSystem
{
    public override int Priority => -20;

    [ForEach, With<Chatting>]
    private static void Chat(Entity bot, ref Bot b)
    {
        b.NextChat = Environment.TickCount64 + b.ChatEveryMs;
        bot.Remove<Chatting>();
        bot.Add<Speaking>() = new Speaking("hello");
    }
}

/// <summary>Carries out <see cref="Travelling"/>: the client's own flow to another map.</summary>
public sealed partial class TravelSystem : EcsSystem
{
    public override int Priority => -20;

    [ForEach]
    private static void Travel(Entity bot, ref Travelling travelling, ref Bot b, in Me me, in Trips trips)
    {
        if (travelling.To.Value != 0) return; // on the way: the flow removes the task when it ends, however long it takes
        b.NextTravel = Environment.TickCount64 + b.TravelEveryMs;
        long from = me.Map.Value - 1; // map ids are 1..Maps
        var to = new MapId(1 + (from + 1 + (long)(b.Next() * (b.Maps - 1))) % b.Maps); // any other map
        travelling = new Travelling(to);
        TaskFlow.Start<Travelling>(bot, () => Player.EnterMapAsync(bot, to), trips.Failed);
    }
}
