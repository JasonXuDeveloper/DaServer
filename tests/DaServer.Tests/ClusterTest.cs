
namespace DaServer.Tests;

/// <summary>
/// Base class of the framework's integration tests: an in-process cluster on loopback with the test node options of
/// <c>DaServer.Testing</c>, plus access to node internals; torn down after every test (nodes and clients).
/// </summary>
public abstract class ClusterTest
{
    private readonly List<Node> _nodes = [];
    private readonly List<ClusterClient> _clients = [];

    private protected IReadOnlyList<Node> Nodes => _nodes;

    /// <summary>The cluster's log, watched for faults: <see cref="Eventually(Func{bool}, string, int)"/> stops at the first.</summary>
    private protected FaultLog Faults { get; private set; } = new();

    [TearDown]
    public async Task TearDownCluster()
    {
        foreach (var client in _clients) await client.DisposeAsync();
        foreach (var node in _nodes) await node.StopAsync();
        _clients.Clear();
        _nodes.Clear();
        var faults = Faults;
        Faults = new FaultLog();
        try
        {
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed) faults.ThrowIfAny();
        }
        finally
        {
            faults.Dispose();
        }
    }

    /// <summary>Start a node; every node after the first joins through the first one.</summary>
    /// <param name="gateway">Run client connections (on any free port).</param>
    /// <param name="clients">Adjust the client connections' options (implies <paramref name="gateway"/>).</param>
    private protected async Task<Node> Start(string name, int shards = 2, bool gateway = false, Func<Type, bool>? hosts = null,
        Action<NodeOptions>? configure = null, Action<ClientConnectionOptions>? clients = null)
    {
        var options = TestNodes.NodeOptionsFor(name, shards, tickRate: 50, _nodes.Count == 0 ? null : _nodes[0]);
        options.Hosts = hosts;
        options.LoggerFactory = Faults.Factory;
        configure?.Invoke(options);
        var clientOptions = gateway || clients != null ? new ClientConnectionOptions() : null;
        if (clientOptions != null) clients?.Invoke(clientOptions);

        var node = await TestNodes.StartAsync(options, clientOptions);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Start <paramref name="count"/> nodes and wait until they all see each other.</summary>
    private protected async Task<Node[]> StartCluster(int count, int shards = 2)
    {
        var nodes = new Node[count];
        for (int i = 0; i < count; i++) nodes[i] = await Start(((char)('a' + i)).ToString(), shards);
        await Converged();
        return nodes;
    }

    /// <summary>Wait until every running node sees every other one as Up.</summary>
    protected Task Converged() =>
        Eventually(() => TestNodes.Converged(_nodes), "cluster did not converge");

    private protected async Task Stop(Node node)
    {
        await node.StopAsync();
        _nodes.Remove(node);
    }

    /// <summary>Dispose <paramref name="client"/> with the cluster.</summary>
    protected ClusterClient Track(ClusterClient client)
    {
        _clients.Add(client);
        return client;
    }

    private protected async Task<ClusterClient> Connect(Node gateway)
    {
        var client = new ClusterClient();
        await client.ConnectAsync("127.0.0.1", gateway.ClientPort());
        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// Block every shard of <paramref name="node"/> until <paramref name="gate"/> is set (at most
    /// <paramref name="seconds"/>): its mail and handoff frames wait meanwhile. The stall it causes is expected.
    /// </summary>
    private protected void HoldShards(Node node, ManualResetEventSlim gate, int seconds = 20)
    {
        Faults.Allow("ShardStalled");
        node.PostToEveryShard((_, g) => g.Wait(TimeSpan.FromSeconds(seconds)), gate);
    }

    /// <summary>First key from <paramref name="start"/> whose actor lives on <paramref name="owner"/>.</summary>
    private protected static long KeyOwnedBy<TId>(Node owner, long start = 1) where TId : struct, IActorId =>
        owner.KeyOwnedBy<TId>(owner, start);

    /// <summary>
    /// Polls a condition that depends on asynchronous delivery (membership, pushes to other sockets); a fault the cluster
    /// logs meanwhile ends the wait at once, naming it.
    /// </summary>
    protected Task Eventually(Func<bool> condition, string message, int timeoutMs = 10_000)
    {
        var faults = Faults;
        return TestNodes.WaitUntilAsync(() =>
        {
            faults.ThrowIfAny();
            return condition();
        }, TimeSpan.FromMilliseconds(timeoutMs), message);
    }

    /// <summary>Polls an asynchronous condition (a call into the cluster) until it holds; a fault ends the wait at once.</summary>
    protected Task Eventually(Func<ValueTask<bool>> condition, string message, int timeoutMs = 10_000)
    {
        var faults = Faults;
        return TestNodes.WaitUntilAsync(() =>
        {
            faults.ThrowIfAny();
            return condition();
        }, TimeSpan.FromMilliseconds(timeoutMs), message);
    }
}
