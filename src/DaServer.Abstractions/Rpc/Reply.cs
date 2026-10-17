using System.Threading.Tasks.Sources;
using System.ComponentModel;

namespace DaServer;

/// <summary>
/// The reply of a request, and what an asynchronous handler returns (<c>async Reply&lt;int&gt; On(Add m)</c>): awaited,
/// never waited for. Its builder keeps the handler's suspended state in a box pooled per thread, so a handler that
/// awaits allocates nothing once warm (a <see cref="ValueTask{TResult}"/> handler boxes its state machine per
/// suspension). A synchronous handler returns the value itself (<c>Reply&lt;int&gt; On(Get _) => State.Value;</c>).
/// </summary>
[AsyncMethodBuilder(typeof(ReplyMethodBuilder<>))]
public readonly struct Reply<T>
{
    private readonly ValueTask<T> _task;

    public Reply(T result) => _task = new ValueTask<T>(result);

    /// <summary>A reply that completes with <paramref name="task"/> (a pooled call, a foreign task).</summary>
    public Reply(ValueTask<T> task) => _task = task;

    public static implicit operator Reply<T>(T result) => new(result);

    public ValueTaskAwaiter<T> GetAwaiter() => _task.GetAwaiter();

    /// <summary>The underlying task, read once (the runtime; <c>Task.WhenAll</c> outside shards).</summary>
    public ValueTask<T> AsValueTask() => _task;

    /// <summary>A <see cref="Task{TResult}"/> for code outside shards (tests, tools): allocates when pending.</summary>
    public Task<T> AsTask() => _task.AsTask();
}

/// <summary>What an asynchronous handler of a one-way message returns, and what framework operations a handler awaits.</summary>
[AsyncMethodBuilder(typeof(ReplyMethodBuilder))]
public readonly struct Reply
{
    private readonly ValueTask _task;

    public Reply(ValueTask task) => _task = task;

    public ValueTaskAwaiter GetAwaiter() => _task.GetAwaiter();

    /// <inheritdoc cref="Reply{T}.AsValueTask"/>
    public ValueTask AsValueTask() => _task;

    /// <inheritdoc cref="Reply{T}.AsTask"/>
    public Task AsTask() => _task.AsTask();
}

/// <summary>Builder of <c>async Reply&lt;T&gt;</c> methods (generated code calls it).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ReplyMethodBuilder<T>
{
    private ReplyBuilderCore<T> _core;

    public static ReplyMethodBuilder<T> Create() => default;

    public Reply<T> Task => new(_core.Task);

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        default(AsyncValueTaskMethodBuilder).Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult(T result) => _core.SetResult(result);

    public void SetException(Exception exception) => _core.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(_core.BoxOf(ref stateMachine).MoveNextAction);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.UnsafeOnCompleted(_core.BoxOf(ref stateMachine).MoveNextAction);
}

/// <summary>Builder of <c>async Reply</c> methods (generated code calls it).</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ReplyMethodBuilder
{
    private ReplyBuilderCore<bool> _core;

    public static ReplyMethodBuilder Create() => default;

    public Reply Task => new(_core.VoidTask);

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
        default(AsyncValueTaskMethodBuilder).Start(ref stateMachine);

    public void SetStateMachine(IAsyncStateMachine stateMachine) { }

    public void SetResult() => _core.SetResult(true);

    public void SetException(Exception exception) => _core.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.OnCompleted(_core.BoxOf(ref stateMachine).MoveNextAction);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine =>
        awaiter.UnsafeOnCompleted(_core.BoxOf(ref stateMachine).MoveNextAction);
}

/// <summary>
/// The state of both builders: nothing until the method first suspends (a synchronous completion is a value, no box);
/// then the state machine moves into a pooled <see cref="ReplyBox{T, TStateMachine}"/> that is the method's task.
/// </summary>
internal struct ReplyBuilderCore<T>
{
    private ReplyBox<T>? _box;
    private T _result;
    private Exception? _error;

    public ValueTask<T> Task =>
        _box != null ? new ValueTask<T>(_box, _box.Version)
        : _error != null ? new ValueTask<T>(System.Threading.Tasks.Task.FromException<T>(_error))
        : new ValueTask<T>(_result);

    public ValueTask VoidTask =>
        _box != null ? new ValueTask(_box, _box.Version)
        : _error != null ? new ValueTask(System.Threading.Tasks.Task.FromException(_error))
        : default;

    public void SetResult(T result)
    {
        if (_box == null) _result = result;
        else _box.Core.SetResult(result);
    }

    public void SetException(Exception exception)
    {
        if (_box == null) _error = exception;
        else _box.Core.SetException(exception);
    }

    /// <summary>
    /// The box of the suspending method. The first suspension runs on the caller's copy of the state machine: the box is
    /// recorded in it first, so the copy moved into the box (and the caller reading <see cref="Task"/>) see it.
    /// </summary>
    public ReplyBox<T> BoxOf<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        if (_box != null) return _box;
        var box = ReplyBox<T, TStateMachine>.Rent();
        _box = box;
        box.StateMachine = stateMachine;
        return box;
    }
}

/// <summary>A suspended <c>async Reply</c> method: its completion source and the continuation that resumes it.</summary>
internal abstract class ReplyBox<T> : IValueTaskSource<T>, IValueTaskSource
{
    public ManualResetValueTaskSourceCore<T> Core;

    protected ReplyBox()
    {
        MoveNextAction = MoveNext;
        Core.RunContinuationsAsynchronously = false; // the awaiter resumes where the method completed, as with ValueTask
    }

    /// <summary>Resumes the method; created once per box, so awaiting allocates nothing.</summary>
    public readonly Action MoveNextAction;

    public short Version => Core.Version;

    protected abstract void MoveNext();

    protected abstract void Return();

    public T GetResult(short token)
    {
        try
        {
            return Core.GetResult(token);
        }
        finally
        {
            Return(); // read once: the box goes back to its pool
        }
    }

    void IValueTaskSource.GetResult(short token) => GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => Core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        Core.OnCompleted(continuation, state, token, flags);
}

internal sealed class ReplyBox<T, TStateMachine> : ReplyBox<T> where TStateMachine : IAsyncStateMachine
{
    public TStateMachine StateMachine = default!;

    /// <summary>A box of this thread's (idle boxes are kept per thread and method: the peak of its suspended calls on a shard).</summary>
    public static ReplyBox<T, TStateMachine> Rent() =>
        PerThread<ReplyBox<T, TStateMachine>>.TryRent(out var box) ? box : new ReplyBox<T, TStateMachine>();

    protected override void MoveNext() => StateMachine.MoveNext();

    protected override void Return()
    {
        StateMachine = default!;
        Core.Reset();
        PerThread<ReplyBox<T, TStateMachine>>.Return(this);
    }
}
