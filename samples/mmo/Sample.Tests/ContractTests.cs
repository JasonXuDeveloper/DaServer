using Nino.Core;
using NUnit.Framework;
using Sample.Contracts;

namespace Sample.Tests;

/// <summary>The pushes that make up the bulk of the traffic are received without garbage.</summary>
public class ContractTests
{
    [Test]
    public void ReceivedMovesComeFromAPoolAndGoBack()
    {
        var sent = new Moves();
        for (int i = 0; i < 50; i++) sent.Items.Add(new Moved(new PlayerId(i + 1), i, i, 1, 0));
        byte[] payload = NinoSerializer.Serialize(new Motion(new MapId(1), sent));

        Assert.That(Receive(payload), Is.EqualTo(50)); // warm-up: the pool's first instance and its list
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Receive(payload);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated / 1000.0, Is.LessThan(1), $"{allocated} B for 1,000 pushes of 50 avatars");

        static int Receive(byte[] payload)
        {
            var reader = new Reader(payload);
            Motion m = NinoDeserializer.Deserialize<Motion>(ref reader);
            int count = m.Avatars.Items.Count;
            m.Avatars.ReturnToPool();
            return count;
        }
    }

    [Test]
    public void PushesHeldByManyHandlersAtOnceStillComeFromThePool()
    {
        // Each handler of a push deserializes its own copy (a client's game state, a bot's telemetry), and a burst
        // can hold many before they go back: beyond a thread's own pool stack (64) the shared stack takes them.
        const int Held = 200;
        byte[] payload = NinoSerializer.Serialize(new Motion(new MapId(1), new Moves { Items = [new Moved(new PlayerId(1), 1, 1, 0, 0)] }));
        var held = new Moves[Held];
        void Burst()
        {
            for (int i = 0; i < Held; i++) held[i] = NinoDeserializer.Deserialize<Motion>(payload).Avatars;
            for (int i = 0; i < Held; i++) held[i].ReturnToPool();
        }

        Burst();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Burst();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated / (100.0 * Held), Is.LessThan(1), $"{allocated} B for 100 bursts of {Held} pushes held at once");
    }
}
