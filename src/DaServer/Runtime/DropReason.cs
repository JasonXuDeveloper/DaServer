namespace DaServer.Runtime;

/// <summary>Why a node dropped a message without running it (<see cref="Node.Dropped"/>, the <c>daserver.messages.dropped</c> metric).</summary>
public enum DropReason
{
    /// <summary>The shard's dispatch queue was full (<see cref="NodeOptions.DispatchQueueCapacity"/>): a request got <c>Overloaded</c>.</summary>
    QueueFull,

    /// <summary>The actor's mailbox was full (<see cref="Actor.MailboxCapacity"/>): a request got <c>Overloaded</c>.</summary>
    MailboxFull,

    /// <summary>The request's caller had timed out before it ran.</summary>
    Expired,
}
