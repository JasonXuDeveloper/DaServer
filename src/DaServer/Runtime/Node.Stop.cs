namespace DaServer.Runtime;

// Leaving: the graceful stop in phases within its budget, a kill, downing on the minority side of a split.
internal sealed partial class Node
{
    /// <summary>Called after each phase of a graceful stop with its name and duration (the shutdown metric).</summary>
    internal event Action<string, TimeSpan>? StopPhase;

    /// <summary>
    /// Leave the cluster gracefully within <see cref="NodeOptions.ShutdownTimeout"/> (or until
    /// <paramref name="cancellationToken"/>, the host's own limit, fires): announce Leaving so callers reroute, drain the
    /// features (client connections close their sessions), unload every actor (<see cref="Actor.OnUnload"/>), close the
    /// links. Each phase is logged with its duration; actors still loaded at the deadline are reported, not waited for.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 2) != 1)
            return;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.ShutdownTimeout);
        var phase = System.Diagnostics.Stopwatch.StartNew();

        // Leave() pushes Leaving to every member directly; give those frames time to be delivered (not a gossip
        // round: that can be long), and skip the wait when there is nobody to tell.
        Membership.Leave();
        bool hasPeers = false;
        foreach (var member in View.Members) hasPeers |= member.NodeId != Id;
        if (hasPeers)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(Options.GossipInterval.TotalMilliseconds, 500)), budget.Token)
                .ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        Phase("leave", phase);

        foreach (var feature in Options.Features)
        {
            try
            {
                // A drain that ignores its token ends with the budget anyway: the actors must still unload and save.
                await feature.DrainAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception e) // the budget ran out, or the feature failed
            {
                if (!budget.IsCancellationRequested) Log.FeatureFailed(Logger, e, feature.GetType().Name, nameof(NodeFeature.DrainAsync));
            }
        }

        Phase("drain", phase);

        var stopped = StopShards();
        await Task.WhenAny(stopped, Task.Delay(Timeout.Infinite, budget.Token)).ConfigureAwait(false);
        if (!stopped.IsCompleted) ReportUnloaded();
        Phase("unload", phase);

        ForEachFeature(false, static (f, crashed) => f.Stop(crashed), nameof(NodeFeature.Stop));
        CloseLinks();
        Phase("close", phase);
        Log.NodeStopped(Logger, Options.Name, Id);
    }

    private void Phase(string name, System.Diagnostics.Stopwatch watch)
    {
        var elapsed = watch.Elapsed;
        watch.Restart();
        Log.StopPhase(Logger, name, Options.Name, (long)elapsed.TotalMilliseconds);
        StopPhase?.Invoke(name, elapsed);
    }

    /// <summary>
    /// After <see cref="StopAsync"/>: whether every actor was unloaded (and its record written) within the stop budget.
    /// The host exits with an error otherwise, so the platform sees a stop that lost state.
    /// </summary>
    public bool StoppedClean { get; private set; } = true;

    /// <summary>The budget ran out while actors were unloading: say how many were not saved (their shards keep trying).</summary>
    private void ReportUnloaded()
    {
        StoppedClean = false;
        foreach (var shard in _shards)
            if (shard.ActorCount > 0)
                Log.BudgetExceeded(Logger, Options.ShutdownTimeout, shard.Index, Options.Name, shard.ActorCount);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private volatile bool _killed;

    /// <summary>
    /// Tests: simulate a process crash. Gossip stops and every socket drops without announcing Leaving, closing client
    /// sessions or unloading actors: the cluster finds out by failure detection only.
    /// </summary>
    internal async Task KillAsync()
    {
        if (Interlocked.Exchange(ref _started, 2) != 1) return;
        _killed = true;
        CloseLinks();
        ForEachFeature(true, static (f, crashed) => f.Stop(crashed), nameof(NodeFeature.Stop));
        await StopShards().ConfigureAwait(false);
    }

    /// <summary>The node downed itself (minority side of a split brain): the host exits so the platform restarts it.</summary>
    public event Action? Downed;

    /// <summary>
    /// Membership found this node on the minority side: stop at once without announcing Leaving or saving (the majority
    /// owns the keys now and may already run the actors), then tell the host.
    /// </summary>
    internal async Task DownAsync(int reachable, int lost)
    {
        if (Interlocked.Exchange(ref _started, 2) != 1) return;
        Log.MinoritySide(Logger, Options.Name, reachable, lost);
        _killed = true;
        CloseLinks();
        ForEachFeature(true, static (f, crashed) => f.Stop(crashed), nameof(NodeFeature.Stop));
        await StopShards(fenced: true).ConfigureAwait(false);
        Downed?.Invoke();
    }

    private Task StopShards(bool fenced = false)
    {
        foreach (var shard in _shards) shard.RequestStop(fenced);
        return Task.WhenAll(Array.ConvertAll(_shards, static s => s.Stopped));
    }

    /// <summary>Stop gossip, maintenance and every node link.</summary>
    private void CloseLinks()
    {
        _maintenance?.Dispose(); // gossip rounds too
        _clusterListener?.Dispose();
        foreach (var link in Volatile.Read(ref _linkList)) link.Dispose(); // once each: a link may serve two addresses
    }
}
