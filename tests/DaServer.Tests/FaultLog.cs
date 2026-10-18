using Microsoft.Extensions.Logging;

namespace DaServer.Tests;

/// <summary>
/// The test cluster's log, watched for faults of the framework itself (a crashed or stalled shard, a failed maintenance
/// tick or gossip round, a refused link, a stop past its budget, anything critical). A test waiting on a condition fails
/// at the first one, with its message, instead of at its time limit; a test that provokes one on purpose allows it.
/// Handler, load and save failures are not faults here: tests provoke them all the time.
/// </summary>
internal sealed class FaultLog : ILoggerProvider, ILogger
{
    private static readonly HashSet<string> Watched =
    [
        "ShardCrashed", "ShardFailed", "ShardStalled", "TurnTooLong", "MaintenanceFailed", "GossipFailed", "LinkRefused", "BudgetExceeded",
    ];

    private readonly HashSet<string> _allowed = [];
    private readonly List<string> _faults = [];
    private readonly Dictionary<string, int> _counts = [];

    public ILoggerFactory Factory { get; }

    public FaultLog() => Factory = LoggerFactory.Create(builder => builder.AddProvider(this));

    /// <summary>The test provokes <paramref name="eventName"/> (a <c>Log</c> method's name): it is no fault.</summary>
    public void Allow(string eventName)
    {
        lock (_faults) _allowed.Add(eventName);
    }

    /// <summary>How often <paramref name="eventName"/> was logged, allowed or not.</summary>
    public int Count(string eventName)
    {
        lock (_faults) return _counts.GetValueOrDefault(eventName);
    }

    /// <summary>Throw at the first fault logged so far.</summary>
    public void ThrowIfAny()
    {
        lock (_faults)
            if (_faults.Count > 0)
                throw new InvalidOperationException($"The cluster logged a fault ({_faults.Count} in all): {_faults[0]}");
    }

    ILogger ILoggerProvider.CreateLogger(string categoryName) => this;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string name = eventId.Name ?? "";
        if (logLevel < LogLevel.Critical && !Watched.Contains(name)) return;
        lock (_faults)
        {
            _counts[name] = _counts.GetValueOrDefault(name) + 1;
            if (!_allowed.Contains(name))
                _faults.Add($"{name}: {formatter(state, exception)}{(exception == null ? "" : " — " + exception.Message)}");
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Dispose() => Factory.Dispose();
}
