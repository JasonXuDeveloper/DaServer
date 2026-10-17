namespace DaServer.Ingress;

/// <summary>
/// What an <see cref="IIngressFilter"/> decides about a message from outside: let it in (<see cref="Admit"/>) or refuse
/// it with a status (<see cref="RpcStatus.RateLimited"/>, <see cref="RpcStatus.Overloaded"/>, <see cref="RpcStatus.Forbidden"/>)
/// and when the sender may try again (HTTP <c>Retry-After</c>, <see cref="RpcException.RetryAfter"/> on clients).
/// </summary>
public readonly record struct IngressDecision(RpcStatus Status, TimeSpan RetryAfter)
{
    public static IngressDecision Admit => default;

    public bool Admitted => Status == RpcStatus.Ok;

    public static IngressDecision Refuse(RpcStatus status, TimeSpan retryAfter = default) => new(status, retryAfter);
}

/// <summary>
/// Admission at the edge (<see cref="INodeContext.AddIngressFilter"/>): runs for every message from outside before it
/// is routed, on the receiving thread, in registration order; the first refusal wins. Never block.
/// </summary>
public interface IIngressFilter
{
    IngressDecision Admit(uint messageId, uint target, long key, Origin origin);
}

/// <summary>
/// Allows <c>rate</c> operations a second on average and bursts of up to <c>burst</c>, without allocating. Not thread-safe:
/// one owner (a connection's receive loop, a lock the caller holds).
/// </summary>
public struct TokenBucket(double rate, double burst)
{
    private readonly double _rate = rate, _burst = burst;
    private double _tokens = burst;
    private long _last = Stopwatch.GetTimestamp();

    /// <summary>Take one token if there is one; otherwise <paramref name="retryAfter"/> is how long until there is.</summary>
    public bool TryTake(out TimeSpan retryAfter)
    {
        long now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(_burst, _tokens + (now - _last) * _rate / Stopwatch.Frequency);
        _last = now;
        if (_tokens >= 1)
        {
            _tokens--;
            retryAfter = TimeSpan.Zero;
            return true;
        }

        retryAfter = TimeSpan.FromSeconds((1 - _tokens) / _rate);
        return false;
    }
}
