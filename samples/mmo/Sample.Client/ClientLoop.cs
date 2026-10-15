using System.Collections.Concurrent;
using System.Diagnostics;
using EasyEcs;
using Sample.Contracts;
using Sample.Shared;

namespace Sample.Client;

/// <summary>
/// One client world on its own thread: what a game's main loop is to its client. Pushes run as soon as they arrive,
/// the simulation ticks at the server's rate, and awaits started on the loop (logging in, entering a map) resume on it,
/// so every component is touched by this thread only. A game runs one loop with one player; a bot process runs one
/// loop per core with many players each.
/// </summary>
public sealed class ClientLoop : SynchronizationContext, IDisposable
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();
    private readonly Thread _thread;
    private volatile bool _stopped;

    public ClientLoop(string name)
    {
        World.AddSystem<PushSystem>();
        World.AddSystem<SteeringSystem>();
        World.AddSystem<MovementSystem>();
        World.AddSystem<ViewSystem>();
        World.AddSystem<SpeechSystem>();
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>The client's ECS world: touch it only from the loop (<see cref="Post"/>, <see cref="Run{T}"/>).</summary>
    public World World { get; } = new();

    /// <summary>A system that runs with the simulation besides the client's own (a bot's input, a game's view).</summary>
    public void Add<TSystem>() where TSystem : EcsSystem, new() => Post(static s => ((ClientLoop)s!).World.AddSystem<TSystem>(), this);

    public override void Post(SendOrPostCallback callback, object? state) => _posted.Enqueue((callback, state));

    /// <summary>Run <paramref name="work"/> on the loop (a flow: log in, enter a map); its awaits resume there.</summary>
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
        long interval = Stopwatch.Frequency / Timing.TickRate, nextTick = Stopwatch.GetTimestamp();
        while (!_stopped)
        {
            while (_posted.TryDequeue(out var item)) item.Callback(item.State);
            World.Update<ClientPushes>();
            if (Stopwatch.GetTimestamp() >= nextTick)
            {
                World.Update();
                nextTick += interval;
            }

            Thread.Sleep(1); // a game renders here; pushes wait at most this long
        }

        World.Dispose();
    }

    public void Dispose()
    {
        _stopped = true;
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
