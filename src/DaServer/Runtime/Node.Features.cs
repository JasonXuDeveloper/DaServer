namespace DaServer.Runtime;

// The node's feature points (INodeContext, ADR-048): what features register (address handlers, pulses, card entries,
// gauges, ingress filters) and how the node calls them, each hook guarded.
sealed partial class Node
{
    void INodeContext.HandleAddress<TAddr>(IAddressHandler handler)
    {
        var registering = _registering ?? throw new InvalidOperationException("Address handlers are registered in NodeFeature.Start");
        if (!registering.TryAdd(Addresses<TAddr>.Scope, handler))
            throw new InvalidOperationException($"{typeof(TAddr)} already has a handler on this node");
    }

    private uint[] _pulseKeys = [];
    private Func<int>[] _pulseSources = [];
    private CardEntry[] _cardEntries = [new(ClusterMember.BuildKey, Build)];

    /// <summary>
    /// What this node runs (V2): the framework's and the application's informational versions, on the card so a
    /// rolling upgrade is visible in every view (<see cref="ClusterMember.Build"/>).
    /// </summary>
    internal static readonly string Build = "daserver " + VersionOf(typeof(Node).Assembly) + "; app " +
        VersionOf(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(Node).Assembly);

    private static string VersionOf(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute v, ..]
            ? v.InformationalVersion
            : assembly.GetName().Version?.ToString() ?? "unknown";

    void INodeContext.AddPulse(uint key, Func<int> read)
    {
        if (_registering == null) throw new InvalidOperationException("Pulses are added in NodeFeature.Start");
        if (_pulseKeys.Length == MemberPulse.Slots) throw new InvalidOperationException($"A node gossips at most {MemberPulse.Slots} feature counters");
        _pulseKeys = [.. _pulseKeys, key];
        _pulseSources = [.. _pulseSources, read];
    }

    /// <summary>Gauges the node's features report (<see cref="INodeContext.AddGauge"/>).</summary>
    internal (string Name, string Description, Func<long> Read)[] Gauges { get; private set; } = [];

    void INodeContext.AddGauge(string name, string description, Func<long> read)
    {
        if (_registering == null) throw new InvalidOperationException("Gauges are added in NodeFeature.Start");
        Gauges = [.. Gauges, (name, description, read)];
    }

    void INodeContext.SetCardEntry(uint key, string value)
    {
        if (_registering == null) throw new InvalidOperationException("Card entries are set in NodeFeature.Start");
        _cardEntries = [.. _cardEntries, new CardEntry(key, value)];
    }

    void INodeContext.PostToShards(Action<World, object?> callback, object? state)
    {
        foreach (var shard in _shards)
            shard.Post(static s =>
            {
                var (target, callback, state) = ((Shard, Action<World, object?>, object?))s!;
                callback(target.World, state);
            }, (shard, callback, state));
    }

    /// <summary>What this node's features publish on its card and gossip in its pulse.</summary>
    internal (uint[] PulseKeys, CardEntry[] Entries) FeatureCard() => (_pulseKeys, _cardEntries);

    /// <summary>The features' counters for this round's pulse (gossip timer).</summary>
    internal void ReadPulses(int[] values)
    {
        var sources = _pulseSources;
        for (int i = 0; i < sources.Length && i < values.Length; i++)
        {
            try
            {
                Volatile.Write(ref values[i], sources[i]());
            }
            catch (Exception e) // a feature's source is user code: the round (failure detection, gossip) goes on
            {
                if (FeatureErrorLogDue()) Log.FeatureFailed(Logger, e, "pulse source", nameof(INodeContext.AddPulse)); // the last value stays
            }
        }
    }

    void INodeContext.AddIngressFilter(IIngressFilter filter)
    {
        if (_registering == null) throw new InvalidOperationException("Ingress filters are added in NodeFeature.Start");
        _ingressFilters = [.. _ingressFilters, filter];
    }

    /// <summary>The feature of type <typeparamref name="T"/> this node runs, or null (tools, tests, metrics).</summary>
    internal T? Feature<T>() where T : NodeFeature
    {
        foreach (var feature in Options.Features)
            if (feature is T match) return match;
        return null;
    }

    /// <summary>The address scopes this node's features handle (advertised in its card).</summary>
    internal uint[] AdvertisedScopes() => [.. _addressHandlers.Keys];

    /// <summary>
    /// Run a hook of every feature, each guarded: a feature is user code too, so one that throws is logged
    /// (<c>FeatureFailed</c>) and the other features and the node's own work go on. Static hooks: nothing is allocated.
    /// </summary>
    private void ForEachFeature<TState>(TState state, Action<NodeFeature, TState> hook, string name)
    {
        foreach (var feature in Options.Features)
        {
            try
            {
                hook(feature, state);
            }
            catch (Exception e)
            {
                if (FeatureErrorLogDue()) Log.FeatureFailed(Logger, e, feature.GetType().Name, name);
            }
        }
    }

    /// <summary>
    /// Whether a feature's failure is logged now: at most once a second per node, as a hook that throws on every
    /// maintenance tick or gossip round would flood the log otherwise. Any thread.
    /// </summary>
    private bool FeatureErrorLogDue() => _featureErrors.DueOnAnyThread(out _);

    private LogThrottle _featureErrors = new(1000);
}
