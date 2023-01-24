using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using DaServer.Shared.Misc;

namespace DaServer.Shared.Core;

public class ComponentHolder
{
    /// <summary>
    /// Updating components - 更新组件
    /// </summary>
    public readonly List<Component> Components = new List<Component>();
    
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
        component.Holder = this;
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
        return component;
    }
    
    /// <summary>
    /// Get a component - 获取组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T? GetComponent<T>() where T: Component
    {
        int cnt = Components.Count;
        for (int i = 0; i < cnt; i++)
        {
            if (Components[i] is T component)
            {
                return component;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Remove a component - 删除组件
    /// </summary>
    /// <param name="component"></param>
    /// <typeparam name="T"></typeparam>
    public void RemoveComponent<T>(T? component) where T: Component
    {
        if (component == null) return;
        if (component.Role == ComponentRole.LowLevel)
        {
            Logger.Error("LowLevel component: {comp} can not be removed", component);
            return;
        }
        
        component.Destroy().Wait();
        Components.Remove(component);
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
    }
}