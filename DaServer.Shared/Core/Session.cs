using System.Threading.Tasks;

namespace DaServer.Shared.Core;

public class Session
{
    public Task Send<T>(int requestId, T val)
    {
        //TODO 发送任务
        return Task.CompletedTask;
    }
}