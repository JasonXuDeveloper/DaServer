using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Message;

namespace DaServer.Shared.Core;

public class Actor
{
    public Session Session;

    public readonly ConcurrentQueue<(int requestId, Task<IMessage?> requestTask)> Requests = new();

    public void AddRequest(int requestId, Task<IMessage?> requestTask)
    {
        Requests.Enqueue((requestId, requestTask));
    }
    
    public Actor(Session session)
    {
        Session = session;
    }

    public void ChangeSession(Session newSession)
    {
        //TODO 老Session下线
        
        Session = newSession;
    }
}