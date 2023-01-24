using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using DaServer.Shared.Misc;

namespace DaServer.Shared.Core;

public abstract class ComponentHolder
{
    /// <summary>
    /// Updating components - 更新组件
    /// </summary>
    public readonly List<Component> Components = new List<Component>();
    
    /// <summary>
    /// Cache of all components - 所有组件的缓存
    /// </summary>
    private readonly ConcurrentDictionary<Type, Component> _cache = new ConcurrentDictionary<Type, Component>();

    /// <summary>
    /// Add a component - 添加组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public T? AddComponent<T>() where T: Component
    {
        T? component = FormatterServices.GetUninitializedObject(typeof(T)) as T;
        if (component == null)
        {
            throw new InvalidOperationException($"Can not create component: {typeof(T)}");
        }
        component.Owner = this;
        //get constructor
        ConstructorInfo? constructor = typeof(T).GetConstructor(Type.EmptyTypes);
        if (constructor == null)
        {
            throw new InvalidOperationException($"Can not find constructor for component: {typeof(T)}");
        }
        //invoke constructor
        constructor.Invoke(component, Array.Empty<object>());
        //check interval
        if (component.TimeInterval < 10)
        {
            throw new InvalidOperationException($"Component interval must be greater than or equal to 10: {typeof(T)}");
        }
        component.Create().Wait();
        Components.Add(component);
        _cache.TryAdd(typeof(T), component);
        return component;
    }
    
    /// <summary>
    /// Get a component - 获取组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T? GetComponent<T>() where T: Component
    {
        if(_cache.TryGetValue(typeof(T), out Component? component))
        {
            return component as T;
        }
        return null;
    }
    
    /// <summary>
    /// Remove a component - 删除组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public void RemoveComponent<T>() where T: Component
    {
        var component = GetComponent<T>();
        if (component == null) return;
        if (component.Role == ComponentRole.LowLevel)
        {
            Logger.Error("LowLevel component: {comp} can not be removed", component);
            return;
        }
        
        component.Destroy().Wait();
        Components.Remove(component);
        _cache.TryRemove(typeof(T), out _);
    }
    
    /// <summary>
    /// Remove all components - 删除所有组件
    /// </summary>
    public void RemoveAllComponents()
    {
        int cnt = Components.Count;
        for (int i = 0; i < cnt; i++)
        {
            Components[i].Destroy().Wait();
        }
        Components.Clear();
        _cache.Clear();
    }
}