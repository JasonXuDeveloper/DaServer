using System;
using System.Linq;
using DaServer.Server.Component;
using DaServer.Server.Core;
using DaServer.Shared.Core;

namespace DaServer.Server.Extension;

public static class ActorSystemComponentExtension
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
            RemoveActor(sysComp, actor);
        }
    }

    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="actor"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, Actor actor)
    {
        for (int i = 0; i < sysComp.ActorList.Count; i++)
        {
            if (i >= sysComp.ActorList.Count) break;
            if (sysComp.ActorList[i] == actor)
            {
                actor.Session.Dispose();
                sysComp.Actors.TryRemove(actor.Session, out _);
                sysComp.ActorsFromId.TryRemove(actor.Id, out _);
                sysComp.ActorList.RemoveAt(i);
                sysComp.RemoveAllComponents(actor);
                return;
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
            RemoveActor(sysComp, actor);
        }
    }
    
    /// <summary>
    /// 删除Actor
    /// </summary>
    /// <param name="sysComp"></param>
    /// <param name="match"></param>
    public static void RemoveActor(this ActorSystemComponent sysComp, Predicate<Actor> match)
    {
        for (int i = 0; i < sysComp.ActorList.Count; i++)
        {
            if (i >= sysComp.ActorList.Count) break;
            var actor = sysComp.ActorList[i];
            if (match(actor))
            {
                RemoveActor(sysComp, actor);
                return;
            }
        }
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