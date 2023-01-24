using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Client.Response;

public static class ResponseFactory
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<IMessage?>> ResponseTcs = new();

    public static void AddResponse(ref RemoteCall remoteCall)
    {
        //id > 0 => response, 0 => callback
        if (remoteCall.RequestId > 0)
        {
            if(ResponseTcs.TryRemove(remoteCall.RequestId, out var tcs))
            {
                IMessage? msgObj = remoteCall.MessageObj;
                tcs.SetResult(msgObj);
            }
        }
        else
        {
            //TODO 派发客户端事件
        }
    }

    public static async Task<IMessage?> GetResponse(int requestId, float timeout)
    {
        var tcs = new TaskCompletionSource<IMessage?>();
        ResponseTcs.TryAdd(requestId, tcs);
        IMessage? ret;
        
        if (tcs.Task.IsCompleted)
        {
            ret = tcs.Task.Result;
            return ret;
        }
        ret = timeout > 0 ? await tcs.Task.TimeoutAfter(TimeSpan.FromSeconds(timeout)) : await tcs.Task;
        return ret;
    }
}