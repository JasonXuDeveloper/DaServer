using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Message;

namespace DaServer.Shared.Component;

public class MessageComponent: Core.Component
{
    internal override ComponentRole Role => ComponentRole.LowLevel;

    private ConcurrentQueue<(Session session, int requestId, Task<IMessage> requestTask)>? _requestQueue;

    public override Task Create()
    {
        _requestQueue = new ConcurrentQueue<(Session session, int requestId, Task<IMessage> requestTask)>();
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _requestQueue!.Clear();
        _requestQueue = null;
        return Task.CompletedTask;
    }
    
    public void AddRequest(Session session, int requestId, Task<IMessage> requestTask)
    {
        _requestQueue!.Enqueue((session, requestId, requestTask));
    }
    
    public override Task Update(int currentTick)
    {
        var actorComp = System.GetComponent<ActorComponent>()!;
        while (_requestQueue!.TryDequeue(out var request))
        {
            var actor = actorComp.GetActor(request.session) ?? actorComp.AddActor(request.session);
            actor.AddRequest(request.requestId, request.requestTask!);
        }
        return Task.CompletedTask;
    }
}