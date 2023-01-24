using System;
using Destructurama;
using Serilog;

namespace DaServer.Shared.Misc;

public static class Logger
{
    static Logger()
    {
        Log.Logger = new LoggerConfiguration()
            .Destructure.UsingAttributes()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File("log.txt",
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true)
            .CreateLogger();
        Info("Initialized logger");
    }

    public static void Info(string message)
    {
        Log.Information(message);
    }

    public static void Info(string message, params object[] objs)
    {
        Log.Information(message, objs);
    }

    public static void Info(object obj)
    {
        Log.Information("{@Data}", obj);
    }

    public static void Error(string message)
    {
        Log.Error(message);
    }

    public static void Error(Exception ex, string message)
    {
        Log.Error(ex, message);
    }

    public static void Error(Exception ex, string message, params object[] objs)
    {
        Log.Error(ex, message, objs);
    }

    public static void Error(Exception ex)
    {
        Log.Error(ex, ex.Message);
    }

    public static void Error(string message, params object[] objs)
    {
        Log.Error(message, objs);
    }

    public static void Fatal(string message)
    {
        Log.Fatal(message);
    }

    public static void Fatal(Exception ex, string message)
    {
        Log.Fatal(ex, message);
    }

    public static void Fatal(Exception ex, string message, params object[] objs)
    {
        Log.Fatal(ex, message, objs);
    }

    public static void Fatal(Exception ex)
    {
        Log.Fatal(ex, ex.Message);
    }

    public static void Fatal(string message, params object[] objs)
    {
        Log.Fatal(message, objs);
    }

    public static void Debug(string message)
    {
        Log.Debug(message);
    }

    public static void Debug(string message, params object[] objs)
    {
        Log.Debug(message, objs);
    }

    public static void Debug(object obj)
    {
        Log.Debug("{@Data}", obj);
    }

    public static void Warning(string message)
    {
        Log.Warning(message);
    }

    public static void Warning(string message, params object[] objs)
    {
        Log.Warning(message, objs);
    }

    public static void Warning(object obj)
    {
        Log.Warning("{@Data}", obj);
    }
}