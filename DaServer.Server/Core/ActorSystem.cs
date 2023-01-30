using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaServer.Shared.Core;
using DaServer.Shared.Interface;

namespace DaServer.Server.Core;

public class ActorSystem<TActorComponent> : ComponentHolder, IActorSystem<TActorComponent>, IUpdatable
    where TActorComponent : Shared.Core.Component
{
    private readonly ConcurrentDictionary<Actor, TActorComponent> _actorComponentDict = new();
    private readonly ConcurrentDictionary<TActorComponent, Actor> _componentActorDict = new();
    private readonly List<TActorComponent> _componentList = new();

    public TActorComponent AddComponent(Actor actor)
    {
        if (_actorComponentDict.ContainsKey(actor))
        {
            throw new InvalidOperationException($"Actor already has component: {actor}");
        }

        var type = typeof(TActorComponent).BaseType!;
        //check generic parameter
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ActorComponent<>))
        {
            throw new InvalidOperationException($"Can not create component: {type}");
        }

        //get first generic parameter
        var genericType = type.GetGenericArguments()[0];
        //check generic parameter
        if (genericType != GetType())
        {
            throw new InvalidOperationException($"Can not create component: {type}");
        }

        var ret = base.AddComponent<TActorComponent>()!;
        _actorComponentDict[actor] = ret;
        _componentActorDict[ret] = actor;
        _componentList.Add(ret);
        ret.Create();
        return ret;
    }

    public void RemoveComponent(Actor actor)
    {
        if (_actorComponentDict.TryRemove(actor, out var component))
        {
            component.Destroy();
            _componentActorDict.TryRemove(component, out _);
            _componentList.Remove(component);
        }
    }

    public sealed override void RemoveAllComponents()
    {
        for (int i = 0; i < _componentList.Count; i++)
        {
            if (i >= _componentList.Count)
            {
                break;
            }

            var component = _componentList[i];
            var actor = _componentActorDict[component];
            RemoveComponent(actor);
        }
    }

    public TActorComponent? GetComponent(Actor actor)
    {
        if (!_actorComponentDict.TryGetValue(actor, out var ret))
        {
            return null;
        }

        return ret;
    }

    public Actor? GetActor(Shared.Core.Component actorComponent)
    {
        if (!_componentActorDict.TryGetValue((TActorComponent)actorComponent, out var ret))
        {
            return null;
        }

        return ret;
    }


    public sealed override T? AddComponent<T>() where T : class
    {
        throw new InvalidOperationException("Can not add component directly, use AddComponent(Actor actor) instead.");
    }

    public sealed override void RemoveComponent<T>()
    {
        throw new InvalidOperationException(
            "Can not remove component directly, use RemoveComponent(Actor actor) instead.");
    }

    public sealed override T? GetComponent<T>() where T : class
    {
        throw new InvalidOperationException("Can not get component directly, use GetComponent(Actor actor) instead.");
    }

    public virtual async Task Update(long currentMs)
    {
        for (int i = 0; i < _componentList.Count; i++)
        {
            if (i >= _componentList.Count)
            {
                break;
            }

            var component = _componentList[i];

            if (currentMs > component.LastExecuteTime + component.TimeInterval)
            {
                component.LastExecuteTime = currentMs;
                await component.Update(currentMs);
            }
        }
    }
}

public interface IActorSystem<out TActorComponent>: IActorSystem
{
    TActorComponent AddComponent(Actor actor);
    TActorComponent? GetComponent(Actor actor);
}

public interface IActorSystem
{
    void RemoveComponent(Actor actor);
    Actor? GetActor(Shared.Core.Component actorComponent);
}