using System;
using DaServer.Server.Component;

namespace DaServer.Server;

public static class Program
{
    public static void Main(string[] args)
    {
        var sys = new Shared.Core.System();
        sys.AddComponent<NetComponent>();
        sys.AddComponent<MessageComponent>();
        sys.AddComponent<ActorProcessComponent>();
        while (true)
        {
            Console.ReadKey();
        }
    }
}