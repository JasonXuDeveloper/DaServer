using System;

namespace DaServer.Shared.Misc;

public static class Time
{
    /// <summary>
    /// Current ms - 当前ms （毫秒时间戳）
    /// </summary>
    public static long CurrentMs => (Now.Ticks - 621355968000000000) / 10000;

    /// <summary>
    /// UTC Time - UTC时间
    /// </summary>
    public static DateTime Now => DateTime.Now.ToUniversalTime();
}