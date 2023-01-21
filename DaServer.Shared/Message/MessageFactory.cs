using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DaServer.Shared.Core;
using DaServer.Shared.Misc;
using Nino.Serialization;

namespace DaServer.Shared.Message;

public static class MessageFactory
{
    /// <summary>
    /// 缓存
    /// </summary>
    private static readonly ConcurrentDictionary<Type, int> _msgIdCache = new ConcurrentDictionary<Type, int>();
    private static readonly ConcurrentDictionary<int, Type> _idMsgCache = new ConcurrentDictionary<int, Type>();

    static MessageFactory()
    {
        LoadMsgTypes();
    }

    public static void Reload() => LoadMsgTypes();

    private static void LoadMsgTypes()
    {
        //反射全部类型
        var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(x => x.GetTypes());
        //获取所有包含MessageAttribute的类型
        var messageTypes = types.Where(t =>
            t.GetCustomAttributes(typeof(MessageAttribute), false).Length > 0 &&
            t.GetInterface(typeof(IMessage).FullName!) != null);
        //遍历所有类型
        foreach (var type in messageTypes)
        {
            //获取MessageAttribute
            var attribute = type.GetCustomAttribute<MessageAttribute>();
            //将类型和MessageAttribute的Id对应起来
            _msgIdCache[type] = attribute!.Id;
            _idMsgCache[attribute!.Id] = type;
            Logger.Info("加载消息类型: {FullName} Id: {Id}", type.FullName!, attribute!.Id);
        }
    }
    
    public static Type? GetMsgType(int id)
    {
        if (_idMsgCache.TryGetValue(id, out var type))
        {
            return type;
        }
        return null;
    }
    
    public static int GetMsgId(Type type)
    {
        if (_msgIdCache.TryGetValue(type, out var id))
        {
            return id;
        }

        return 0;
    }

    public static byte[] GetMessage<T>(int requestId, T val) where T: IMessage
    {
        var type = typeof(T);
        if(type == typeof(IMessage))
            type = val.GetType();
        if (!_msgIdCache.TryGetValue(type, out _))
        {
            throw new Exception($"消息类型{type.FullName}未注册");
        }

        var msgBytes = Serializer.Serialize(type != typeof(T) ? (object)val : val, CompressOption.NoCompression);
        RemoteCall remoteCall = new RemoteCall
        {
            RequestId = requestId,
            MsgId = GetMsgId(type),
            MessageData = msgBytes
        };
        return Serializer.Serialize(remoteCall);
    }
    
    public static RemoteCall GetRemoteCall(ReadOnlySequence<byte> data)
    {
        //try stackalloc if len <= 1024
        if (data.Length <= 1024)
        {
            Span<byte> buffer = stackalloc byte[(int)data.Length];
            data.CopyTo(buffer);
            return GetRemoteCall(buffer);
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent((int)data.Length);
            data.CopyTo(buffer);
            return GetRemoteCall(new ArraySegment<byte>(buffer, 0, (int)data.Length));
        }
    }
    
    public static RemoteCall GetRemoteCall(scoped Span<byte> bytes)
    {
        var ret = Deserializer.Deserialize<RemoteCall>(bytes);
        var type = GetMsgType(ret.MsgId);
        if (type == null)
        {
            throw new Exception($"消息类型{ret.MsgId}未注册");
        }
        ret.MessageObj = (IMessage)Deserializer.Deserialize(type, ret.MessageData, CompressOption.NoCompression);
        return ret;
    }
}