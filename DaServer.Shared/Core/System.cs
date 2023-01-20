using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Timers;
using DaServer.Shared.Misc;

namespace DaServer.Shared.Core;

/// <summary>
/// System - 系统
/// </summary>
public sealed class System
{
    /// <summary>
    /// Constructor - 构造函数
    /// </summary>
    public System()
    {
        // Add timer
        _timer = new Timer(10);
        _timer.Elapsed += (_, _) => Update();
        _timer.AutoReset = true;
        _timer.Enabled = true;
    }

    /// <summary>
    /// Timer - 计时器
    /// </summary>
    private readonly Timer _timer;
    
    /// <summary>
    /// Updating components - 更新组件
    /// </summary>
    private readonly List<Component> _components = new List<Component>();

    /// <summary>
    /// Start Tick - 开始 Tick
    /// </summary>
    private readonly int _startTick = Time.CurrentTick;

    /// <summary>
    /// Enable the system - 启用系统
    /// </summary>
    public void Enable()
    {
        _timer.Enabled = true;
    }
    
    /// <summary>
    /// Disable the system - 禁用系统
    /// </summary>
    public void Disable()
    {
        _timer.Enabled = false;
    }
    
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
        if (!_timer.Enabled)
        {
            Logger.Error("System is not enabled, can not add {comp}", component);
            return null;
        }
        component.System = this;
        //get constructor
        ConstructorInfo? constructor = typeof(T).GetConstructor(Type.EmptyTypes);
        if (constructor == null)
        {
            throw new InvalidOperationException($"Can not find constructor for component: {typeof(T)}");
        }
        //invoke constructor
        constructor.Invoke(component, Array.Empty<object>());
        component.Create().Wait();
        _components.Add(component);
        return component;
    }
    
    /// <summary>
    /// Get a component - 获取组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T? GetComponent<T>() where T: Component
    {
        int cnt = _components.Count;
        for (int i = 0; i < cnt; i++)
        {
            if (_components[i] is T component)
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
        if(!_timer.Enabled)
        {
            Logger.Error("System is not enabled, can not remove {comp}", component);
            return;
        }
        if (component.Role == ComponentRole.LowLevel)
        {
            Logger.Error("LowLevel component: {comp} can not be removed", component);
            return;
        }
        
        component.Destroy().Wait();
        _components.Remove(component);
    }
    
    /// <summary>
    /// Remove all components - 清除所有组件
    /// </summary>
    public void RemoveAllComponents()
    {
        foreach (Component component in _components)
        {
            if (component.Role == ComponentRole.LowLevel) continue;
            RemoveComponent(component);
        }
    }
    
    /// <summary>
    /// Update all components - 更新所有组件
    /// </summary>
    private async void Update()
    {
        var diff = Time.CurrentTick - _startTick;

        int cnt = _components.Count;
        for (int i = 0; i < cnt; i++)
        {
            if (i >= _components.Count) break;
            var component = _components[i];
            await component.Update(diff);
        }
    }
}