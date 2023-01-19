using System;

namespace DaServer.Shared.Misc;

public static class Time
{
    /// <summary>
    /// Current tick - 当前Tick
    /// </summary>
    public static int CurrentTick => unchecked((int)DateTime.UtcNow.Ticks);
        
    /// <summary>
    /// UTC Time - UTC时间
    /// </summary>
    public static DateTime Now => DateTime.UtcNow;
}