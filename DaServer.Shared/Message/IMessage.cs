using System;

namespace DaServer.Shared.Message;

public interface IMessage
{
    
}
[AttributeUsage(AttributeTargets.Struct)]
public class MessageAttribute: Attribute
{
    public int Id;

    public MessageAttribute(int id)
    {
        Id = id;
    }
}