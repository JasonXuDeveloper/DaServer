namespace DaServer.Shared.Message;

public struct MVoid : IMessage
{
    public static MVoid Empty => new MVoid();
}