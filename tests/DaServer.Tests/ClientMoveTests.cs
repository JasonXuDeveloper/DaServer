using DaServer.Client;

namespace DaServer.Tests;

/// <summary>
/// Make before break (ADR-026), interleaving by interleaving against a <see cref="ScriptedNode"/>: what a real network
/// leaves to chance (a move while a sender holds the old connection, a node that dies with calls in flight, a refused
/// resume) is forced here, so the outcome does not depend on a machine's speed.
/// </summary>
public class ClientMoveTests
{
    private static async Task<(ClusterClient Client, ScriptedNode Node)> Connected()
    {
        var node = new ScriptedNode();
        var client = new ClusterClient { Connector = node.Open, CallTimeout = TimeSpan.FromSeconds(30) };
        await client.ConnectAsync("node", 1);
        node[0].GiveTicket();
        await Until(() => client.Ticket != null);
        return (client, node);
    }

    private static async Task Until(Func<bool> condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string what = "")
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(5); !condition(); await Task.Delay(10))
            if (DateTime.UtcNow > deadline) Assert.Fail("not within 5 s: " + what);
    }

    private static Task Moved(ClusterClient client)
    {
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Moved += () => moved.TrySetResult();
        return moved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ASendThatPickedTheOldConnectionAsTheMoveSealedItGoesOnTheNewOne()
    {
        var (client, node) = await Connected();
        var old = node[0];
        // The sender has picked the old connection; before its frame is taken the whole move runs (new connection,
        // resume, switch, goodbye sealing the old one).
        old.BeforeNextSend(() =>
        {
            old.Retire();
            Assert.That(SpinWait.SpinUntil(() => old.Sealed, 5000), Is.True, "the move sealed the old connection");
        });

        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the call went on the new connection");
        Assert.That(old.Sent[^1], Is.EqualTo((FrameKind.Control, ControlOp.Goodbye)), "the goodbye is the old connection's last frame");
        Assert.That(old.Requests, Is.Zero);
        Assert.That(node[1].Requests, Is.EqualTo(1));
        await Until(() => old.IsClosed); // no call of its own left
    }

    [Test]
    public async Task TheOldConnectionClosesOnlyOnceItsCallsAreAnswered()
    {
        var (client, node) = await Connected();
        var old = node[0];
        old.Hold();
        var call = client.Ask(new AddGold(1)).AsTask();
        var moved = Moved(client);
        old.Retire();
        await moved;
        Assert.That(SpinWait.SpinUntil(() => old.Sealed, 5000), Is.True);

        await Task.Delay(300);
        Assert.That(old.IsClosed, Is.False, "a call made on it is still waiting for its reply there");
        Assert.That(call.IsCompleted, Is.False);
        old.Release();
        Assert.That(await call, Is.EqualTo(1));
        await Until(() => old.IsClosed);
    }

    [Test]
    public async Task CallsOfAConnectionLeftBehindFailAtOnceWhenItsNodeDies()
    {
        var (client, node) = await Connected();
        var old = node[0];
        old.Hold();
        var call = client.Ask(new AddGold(1)).AsTask();
        var moved = Moved(client);
        old.Retire();
        await moved;

        old.Close(new IOException("node crashed"));
        var failure = Assert.ThrowsAsync<RpcException>(async () => await call.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(failure!.Status, Is.EqualTo(RpcStatus.Disconnected), "not after the 30 s call timeout");
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1), "the session goes on on the new connection");
    }

    [Test]
    public async Task ARefusedMoveLeavesTheSessionOnItsConnection()
    {
        var (client, node) = await Connected();
        node.RefuseResume = true;
        int moves = 0;
        client.Moved += () => moves++;
        node[0].Retire();
        await Until(() => node.Count == 2 && node[1].IsClosed);

        Assert.That(moves, Is.Zero);
        Assert.That(node[0].Sealed, Is.False, "the connection it stays on is not sealed");
        Assert.That(await client.Ask(new AddGold(1)), Is.EqualTo(1));
        Assert.That(node[0].Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task ARetireWhileMovingStartsNoSecondMove()
    {
        var (client, node) = await Connected();
        var moved = Moved(client);
        node[0].Retire();
        node[0].Retire();
        await moved;
        await Task.Delay(200);
        Assert.That(node.Count, Is.EqualTo(2), "one new connection");
    }

    [Test]
    public async Task ARetireOnALeftConnectionIsIgnored()
    {
        var (client, node) = await Connected();
        node[0].Hold(); // keeps the old connection open after the move
        _ = client.Ask(new AddGold(1)).AsTask();
        var moved = Moved(client);
        node[0].Retire();
        await moved;
        node[0].Retire();
        await Task.Delay(200);
        Assert.That(node.Count, Is.EqualTo(2));
        node[0].Release();
    }

    [Test]
    public async Task CallsFromManyThreadsAcrossManyMovesAllSucceed()
    {
        // No half-switched state: a sender reads one field, a switch is one write, and the seal sends late frames on.
        var (client, node) = await Connected();
        int moves = 0;
        client.Moved += () => Interlocked.Increment(ref moves);
        using var stop = new CancellationTokenSource();
        int replies = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<RpcStatus>();
        var senders = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await client.Ask(new AddGold(1));
                    Interlocked.Increment(ref replies);
                }
                catch (RpcException e)
                {
                    failures.Add(e.Status);
                }
            }
        })).ToArray();

        for (int i = 0; i < 20; i++)
        {
            int before = Volatile.Read(ref moves);
            node[node.Count - 1].Retire();
            await Until(() => Volatile.Read(ref moves) > before);
        }

        stop.Cancel();
        await Task.WhenAll(senders);
        Assert.That(failures, Is.Empty);
        Assert.That(replies, Is.GreaterThan(100));
        for (int i = 0; i < node.Count - 1; i++)
        {
            await Until(() => node[i].IsClosed);
            Assert.That(node[i].Sent[^1], Is.EqualTo((FrameKind.Control, ControlOp.Goodbye)), $"connection {i}: the goodbye came last");
        }
    }
}
