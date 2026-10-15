using System.Collections.Concurrent;
using System.Diagnostics;
using EasyEcs;

namespace DaServer.Simulation;

/// <summary>
/// A client world on its own thread: what a game's main loop is to its client. Each pass runs the work posted to it and
/// <c>onPass</c> (the client's push handling: frames as they arrive); the world's systems run at
/// <c>tickRate</c>. Awaits started on the loop resume on it (it is their <see cref="SynchronizationContext"/>), so
/// every component is touched by this thread only. One loop holds one player in a game client and many simulated
/// users in a <see cref="Swarm"/>.
/// </summary>
public sealed class ClientLoop : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();
    private readonly Action<World>? _onPass;
    private readonly long _interval;
    private readonly Thread _thread;
    private volatile bool _stopped;

    public ClientLoop(string name, int tickRate = 20, Action<World>? onPass = null)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        _onPass = onPass;
        _interval = Stopwatch.Frequency / tickRate;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>The loop's world: touch it only on the loop (<see cref="Run(Action{World})"/>, systems, its awaits).</summary>
    public World World { get; } = new();

    public override void Post(SendOrPostCallback callback, object? state) => _posted.Enqueue((callback, state));

    /// <summary>Run <paramref name="work"/> on the loop.</summary>
    public Task Run(Action<World> work) => Run(() =>
    {
        work(World);
        return Task.FromResult(true);
    });

    /// <summary>Run <paramref name="work"/> on the loop (a flow: log in, then enter a map); its awaits resume there.</summary>
    public Task<T> Run<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                done.TrySetResult(await work());
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }, null);
        return done.Task;
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        long nextTick = Stopwatch.GetTimestamp();
        while (!_stopped)
        {
            while (_posted.TryDequeue(out var item)) item.Callback(item.State);
            _onPass?.Invoke(World);
            if (Stopwatch.GetTimestamp() >= nextTick)
            {
                World.Update();
                nextTick += _interval;
            }

            Thread.Sleep(1); // a game renders here; arrivals wait at most this long
        }

        World.Dispose();
    }

    public void Dispose()
    {
        _stopped = true;
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
