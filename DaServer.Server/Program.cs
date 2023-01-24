using System;
using DaServer.Server.Component;
using DaServer.Shared.Core;

namespace DaServer.Server;

public static class Program
{
    public static void Main(string[] args)
    {
        var ent = new Entity();
        ent.AddComponent<NetComponent>();
        ent.AddComponent<MessageComponent>();
        ent.AddComponent<ActorSystemComponent>();
        while (true)
        {
            Console.ReadKey();
        }
    }
}