using System.Threading;

namespace DaServer.Runtime;

/// <summary>
/// Installed on every shard thread so awaits on foreign tasks (database drivers, Task.Delay, ...) resume on the shard
/// thread. RPC awaits do not go through it: the runtime completes them on the shard thread directly.
/// </summary>
internal sealed class ShardSynchronizationContext(Shard shard) : SynchronizationContext
{
    public Shard Shard { get; } = shard;

    public override void Post(SendOrPostCallback d, object? state) =>
        Shard.Post(new WorkItem(WorkKind.Callback, d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Shard.Current == Shard)
            d(state);
        else
            throw new System.NotSupportedException("Synchronous Send into a shard is not supported; use Post.");
    }

    public override SynchronizationContext CreateCopy() => this;
}
