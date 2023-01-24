namespace DaServer.Shared.Extension;

public static class ComponentExtension
{
    public static T? GetComponent<T>(this Core.Component component) where T : Core.Component
        => component.Owner.GetComponent<T>();

    public static T? AddComponent<T>(this Core.Component component) where T : Core.Component
        => component.Owner.AddComponent<T>();

    public static void RemoveComponent<T>(this Core.Component component) where T : Core.Component
        => component.Owner.RemoveComponent<T>();
}