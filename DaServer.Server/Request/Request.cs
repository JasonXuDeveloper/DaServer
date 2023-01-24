using System;
using System.Threading.Tasks;
using DaServer.Server.Core;
using DaServer.Shared.Message;
using DaServer.Shared.Misc;

namespace DaServer.Server.Request;

/// <summary>
/// Event request - 事件请求
/// </summary>
public interface IRequest
{
    /// <summary>
    /// Request - 请求
    /// </summary>
    /// <param name="actor">User actor - 用户模型</param>
    /// <param name="request">Requested data - 请求数据</param>
    /// <returns>Respond data - 返回数据</returns>
    public Task<IMessage?> OnRequest(Actor actor, IMessage request);
}

file interface IRequest<in TActor, in TRequest, TResponse> : IRequest
    where TActor : Actor
    where TRequest : IMessage
    where TResponse : IMessage
{
    public Task<TResponse?> OnRequest(TActor actor, TRequest request);
}


/// <summary>
/// Event request - 事件请求
/// </summary>
public abstract class Request<TActor, TRequest, TResponse> : IRequest<TActor, TRequest, TResponse>
    where TActor : Actor
    where TRequest : IMessage
    where TResponse : IMessage
{
    /// <summary>
    /// Request - 请求
    /// </summary>
    /// <param name="actor">User actor - 用户模型</param>
    /// <param name="request">Requested data - 请求数据</param>
    /// <returns>Respond data - 返回数据</returns>
    public async Task<IMessage?> OnRequest(Actor actor, IMessage request)
    {
        try
        {
            var ret = await OnRequest((TActor)actor, (TRequest)request);
            return ret;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Request {Type} Error", GetType());
        }

        return null;
    }

    
    /// <summary>
    /// Request - 请求
    /// </summary>
    /// <param name="actor">User actor - 用户模型</param>
    /// <param name="request">Requested data - 请求数据</param>
    /// <returns>Respond data - 返回数据</returns>
    public abstract Task<TResponse?> OnRequest(TActor actor, TRequest request);
}

