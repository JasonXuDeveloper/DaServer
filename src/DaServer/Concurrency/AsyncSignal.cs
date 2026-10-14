using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace DaServer.Concurrency;

/// <summary>
/// Allocation-free auto-reset signal with a single awaiter (one consumer loop, many setters).
/// Multiple <see cref="Set"/> calls before the consumer wakes coalesce into one wake-up.
/// </summary>
public sealed class AsyncSignal : IValueTaskSource
{
    private const int Idle = 0, Signaled = 1, Waiting = 2;

    private ManualResetValueTaskSourceCore<bool> _core;
    private int _state;

    public AsyncSignal()
    {
        // Never run the consumer inline on the setter's thread (setters are often shard/IO threads).
        _core.RunContinuationsAsynchronously = true;
    }

    public void Set()
    {
        while (true)
        {
            int s = Volatile.Read(ref _state);
            if (s == Signaled) return;
            if (s == Idle)
            {
                if (Interlocked.CompareExchange(ref _state, Signaled, Idle) == Idle) return;
            }
            else if (Interlocked.CompareExchange(ref _state, Idle, Waiting) == Waiting)
            {
                _core.SetResult(true);
                return;
            }
        }
    }

    /// <summary>Wait for the next signal. Only one waiter at a time.</summary>
    public ValueTask WaitAsync()
    {
        if (Interlocked.CompareExchange(ref _state, Idle, Signaled) == Signaled)
            return default;

        _core.Reset();
        if (Interlocked.CompareExchange(ref _state, Waiting, Idle) == Idle)
            return new ValueTask(this, _core.Version);

        // Signaled between the two checks
        Interlocked.Exchange(ref _state, Idle);
        return default;
    }

    void IValueTaskSource.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);
}
