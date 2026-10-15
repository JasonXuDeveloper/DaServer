using EasyEcs;
using Sample.Client;

namespace Sample.Bot;

/// <summary>
/// A bot's stand-in for a person at the controls: when to turn and when to speak, and its own random sequence (bots
/// differ, runs repeat).
/// </summary>
public struct BotInput(uint random, int turnEveryTicks, int chatEveryTicks, long ticks) : IComponent
{
    public uint Random = random;
    public readonly int TurnEveryTicks = turnEveryTicks, ChatEveryTicks = chatEveryTicks;
    public long Ticks = ticks;
}

/// <summary>
/// Writes the input a game's UI would write (<see cref="Steering"/>, <see cref="Speaking"/>); the client's own systems
/// do the rest, exactly as for a person.
/// </summary>
public sealed partial class BotInputSystem : EcsSystem
{
    public override int Priority => -20; // before the client turns input into messages

    [ForEach, With<Me>]
    private static void Decide(Entity player, ref BotInput input, ref Steering steering)
    {
        long tick = ++input.Ticks;
        if (input.TurnEveryTicks > 0 && tick % input.TurnEveryTicks == 0)
            steering = new Steering(Next(ref input.Random) * 2 - 1, Next(ref input.Random) * 2 - 1);
        if (input.ChatEveryTicks > 0 && tick % input.ChatEveryTicks == 0)
            player.Add<Speaking>() = new Speaking("hello"); // last: `input` and `steering` are invalid after it
    }

    /// <summary>xorshift32 in [0, 1).</summary>
    private static float Next(ref uint state)
    {
        uint x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x;
        return (x >> 8) * (1f / (1 << 24));
    }
}
