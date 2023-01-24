namespace DaServer.Shared.Extension;

public static class ComponentExtension
{
    public static T? GetComponent<T>(this Core.Component component) where T : Core.Component
        => component.Holder.GetComponent<T>();

    public static T? AddComponent<T>(this Core.Component component) where T : Core.Component
        => component.Holder.AddComponent<T>();

    public static void RemoveComponent<T>(this Core.Component component, T comp) where T : Core.Component
        => component.Holder.RemoveComponent<T>(comp);
}