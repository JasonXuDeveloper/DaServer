using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
    /// Components to be added - 要添加的组件
    /// </summary>
    private readonly ConcurrentQueue<Component> _createQueue = new ConcurrentQueue<Component>();
    
    /// <summary>
    /// Components to be removed - 要删除的组件
    /// </summary>
    private readonly ConcurrentQueue<Component> _destroyQueue = new ConcurrentQueue<Component>();
    
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
        _createQueue.Enqueue(component);
        return component;
    }
    
    /// <summary>
    /// Get a component - 获取组件
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public T? GetComponent<T>() where T: Component
    {
        return _components.OfType<T>().FirstOrDefault();
    }
    
    /// <summary>
    /// Remove a component - 删除组件
    /// </summary>
    /// <param name="component"></param>
    /// <typeparam name="T"></typeparam>
    public void RemoveComponent<T>(T component) where T: Component
    {
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
        _destroyQueue.Enqueue(component);
    }
    
    /// <summary>
    /// Clear all components - 清除所有组件
    /// </summary>
    public void ClearComponents()
    {
        if (!_timer.Enabled)
        {
            Logger.Error("System is not enabled, can not clear components");
            return;
        }
        _components.RemoveAll(c => c.Role != ComponentRole.LowLevel);
    }
    
    /// <summary>
    /// Update all components - 更新所有组件
    /// </summary>
    private async void Update()
    {
        var diff = Time.CurrentTick - _startTick;

        foreach (var component in _components.Where(component => diff % component.TickInterval == 0))
        {
            await component.Update(Time.CurrentTick);
        }
        
        while (_createQueue.TryDequeue(out var component))
        {
            await component.Create();
            _components.Add(component);
        }
        
        while (_destroyQueue.TryDequeue(out var component))
        {
            await component.Destroy();
            _components.Remove(component);
        }
    }
}