using System;
using System.Linq;
using DaServer.Server.Component;
using DaServer.Server.Core;
using DaServer.Shared.Core;

namespace DaServer.Server.Extension;

public static class ActorSystemExtension
{
    /// <summary>
    /// 添加Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="session"></param>
    /// <returns></returns>
    public static Actor AddActor(this ActorSystemComponent sysComp, Session session)
    {
        Actor actor = new Actor((Entity)sysComp.Owner, session);
        sysComp.Actors.TryAdd(session, actor);
        sysComp.ActorList.Add(actor);
        return actor;
    }
    
    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="session"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, Session session)
    {
        if(sysComp.Actors.TryRemove(session, out var actor))
        {
            sysComp.ActorList.Remove(actor);
            sysComp.ActorsFromId.TryRemove(actor.Id, out _);
        }
        sysComp.ActorList.RemoveAll(x => x.Session == session);
    }

    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="actor"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, Actor actor)
    {
        foreach (var pair in sysComp.Actors)
        {
            if (pair.Value == actor)
            {
                sysComp.Actors.TryRemove(pair.Key, out _);
                sysComp.ActorsFromId.TryRemove(actor.Id, out _);
                sysComp.ActorList.Remove(actor);
                break;
            }
        }
    }
    
    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="id"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, long id)
    {
        if (sysComp.ActorsFromId.TryRemove(id, out var actor))
        {
            sysComp.Actors.TryRemove(actor.Session, out _);
            sysComp.ActorList.Remove(actor);
        }
    }
    
    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="match"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, Predicate<Actor> match)
    {
        sysComp.ActorList.RemoveAll(match);
    }
    
    /// <summary>
    /// 获取Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="session"></param>
    /// <returns></returns>
    public static Actor? GetActor(this ActorSystemComponent sysComp, Session session)
    {
        sysComp.Actors.TryGetValue(session, out var actor);
        return actor;
    }
    
    /// <summary>
    /// 获取Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="id"></param>
    /// <returns></returns>
    public static Actor? GetActor(this ActorSystemComponent sysComp, long id)
    {
        sysComp.ActorsFromId.TryGetValue(id, out var actor);
        return actor;
    }
    
    /// <summary>
    /// 获取Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="predicate"></param>
    /// <returns></returns>
    public static Actor? GetActor(this ActorSystemComponent sysComp, Predicate<Actor> predicate) => sysComp.ActorList.FirstOrDefault(actor => predicate(actor));

}