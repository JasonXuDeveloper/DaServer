namespace DaServer.Net;

/// <summary>
/// Writes the queued bytes of many connections on a beat, from one thread (<see cref="ConnectionOptions.Flusher"/>):
/// the frames a connection receives within one interval (a map's positions, a realm broadcast from another node) leave
/// in one write, written directly without waking anything (a woken send loop per connection costs more than the write,
/// ADR-034). Each connection is listed once per beat, when its queue goes from empty to not empty. One thread on
/// purpose: on saturated cores, splitting a beat across the thread pool queues its parts behind the receive loops
/// (ADR-070).
/// </summary>
public sealed class SendFlusher : IDisposable
{
    private static readonly Dictionary<TimeSpan, SendFlusher> SharedByInterval = new();
    private readonly Lock _gate = new(), _beat = new();
    private List<Connection> _queued = new(), _writing = new();
    private readonly TimeSpan _interval;
    private readonly Thread _thread;
    private volatile bool _stopped;

    public SendFlusher(TimeSpan interval, string name = "send-flusher")
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "A beat needs a positive interval");
        _interval = interval;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>
    /// The process's flusher of <paramref name="interval"/>: every connection with that beat shares its thread (a node's
    /// sessions, a process's clients). Lives as long as the process; not disposed.
    /// </summary>
    public static SendFlusher Shared(TimeSpan interval)
    {
        lock (SharedByInterval) // cold: once per connection or node
        {
            if (!SharedByInterval.TryGetValue(interval, out var flusher)) SharedByInterval[interval] = flusher = new SendFlusher(interval);
            return flusher;
        }
    }

    /// <summary>Write what is queued now, without waiting for the beat (a node writes its sessions' last frames before closing them).</summary>
    public void Flush() => Beat();

    internal void Add(Connection connection)
    {
        lock (_gate) _queued.Add(connection);
    }

    private void Run()
    {
        while (!_stopped)
        {
            Thread.Sleep(_interval);
            Beat();
        }

        Beat(); // what was queued before the stop
    }

    private void Beat()
    {
        lock (_beat) // the beat's thread, or a Flush
        {
            lock (_gate) (_queued, _writing) = (_writing, _queued);
            var writing = _writing;
            for (int i = 0; i < writing.Count; i++) writing[i].FlushNow();
            writing.Clear();
        }
    }

    public void Dispose()
    {
        _stopped = true;
        if (Thread.CurrentThread != _thread) _thread.Join();
    }
}
