using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Core;

namespace DaServer.Client.Response;

public static class ResponseFactory
{
    private static ConcurrentDictionary<int, RemoteCall> _responseQueue = new();

    public static void AddResponse(ref RemoteCall remoteCall)
    {
        //id > 0 => response, 0 => callback
        if (remoteCall.RequestId > 0)
        {
            _responseQueue.TryAdd(remoteCall.RequestId, remoteCall);
        }
        else
        {
            //TODO 派发客户端事件
        }
    }
    
    public static bool TryGetResponse(int requestId, out RemoteCall remoteCall)
    {
        return _responseQueue.TryGetValue(requestId, out remoteCall);
    }

    public static async Task<RemoteCall> GetResponse(int requestId, float timeout)
    {
        await Task.Yield();
        RemoteCall remoteCall;
        //请求
        while (!TryGetResponse(requestId, out remoteCall))
        {
            await Task.Delay(10).ConfigureAwait(false);
            //timeout
            if (timeout > 0)
            {
                timeout -= 0.01f;
                if (timeout <= 0)
                {
                    throw new TimeoutException($"Get response for request [{requestId}] timeout");
                }
            }
        }

        return remoteCall;
    }

    public static bool RemoveResponse(int requestId)
    {
        return _responseQueue.TryRemove(requestId, out _);
    }
}