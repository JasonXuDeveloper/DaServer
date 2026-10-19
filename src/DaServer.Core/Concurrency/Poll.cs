namespace DaServer.Concurrency;

/// <summary>Waiting for a condition that asynchronous work makes true (tests, the swarm's end): cold paths only.</summary>
internal static class Poll
{
    /// <summary>Check <paramref name="condition"/> every <paramref name="interval"/> until it holds (true) or <paramref name="timeout"/> passes (false).</summary>
    public static async ValueTask<bool> UntilAsync(Func<ValueTask<bool>> condition, TimeSpan timeout, TimeSpan interval)
    {
        long deadline = Platform.After(timeout);
        while (!await condition().ConfigureAwait(false))
        {
            if (Platform.Milliseconds >= deadline) return false;
            await Task.Delay(interval).ConfigureAwait(false);
        }

        return true;
    }
}
