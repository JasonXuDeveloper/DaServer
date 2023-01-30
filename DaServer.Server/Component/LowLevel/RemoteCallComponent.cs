using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Server.Extension;
using DaServer.Server.GameActor;
using DaServer.Server.Request;
using DaServer.Shared.Core;
using DaServer.Shared.Extension;

namespace DaServer.Server.Component;

public class RemoteCallComponent: Shared.Core.Component
{
    public override ComponentRole Role => ComponentRole.LowLevel;

    private ConcurrentQueue<(Session session, RemoteCall call)>? _requestQueue;

    public override Task Create()
    {
        _requestQueue = new ();
        //确保初始化RequestFactory
        _ = RequestFactory.GetRequest(1);
        return Task.CompletedTask;
    }

    public override Task Destroy()
    {
        _requestQueue!.Clear();
        _requestQueue = null;
        return Task.CompletedTask;
    }
    
    public void AddRequest(Session session, RemoteCall call)
    {
        _requestQueue!.Enqueue((session, call));
    }
    
    public override Task Update(long currentMs)
    {
        var actorComp = this.GetComponent<ActorSystemComponent>()!;
        while (_requestQueue!.TryDequeue(out var request))
        {
            var actor = actorComp.GetActor(request.session) ?? actorComp.AddActor(request.session);
            actor.GetSystem<RequestSystem>().GetComponent(actor)?.AddRequest(request.call);
        }
        return Task.CompletedTask;
    }
}