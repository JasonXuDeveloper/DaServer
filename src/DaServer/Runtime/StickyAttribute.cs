namespace DaServer;

/// <summary>
/// Actors of this type stay where they are loaded (ADR-079): when a node joins, only slots without loaded actors move to
/// it, so new keys go to the new node and a loaded actor (a map with players in it) does not move; load-aware
/// rebalancing leaves them alone. For actors whose move is expensive or visible. Without it a type's slots follow
/// rendezvous hashing and are evened out by load.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StickyAttribute : Attribute;
