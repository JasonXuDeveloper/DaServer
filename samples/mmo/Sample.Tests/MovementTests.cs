using EasyEcs;
using NUnit.Framework;
using Sample.Shared;

namespace Sample.Tests;

/// <summary>The shared movement rules (the map simulates with them, clients predict with them).</summary>
public class MovementTests
{
    [Test]
    public void TheBorderStopsAnAvatar()
    {
        using var world = new World();
        world.AddSystem<MovementSystem>();
        var avatar = world.CreateEntity(new Position(MapGeometry.Size - 2, 10), new Velocity(5, 1));
        world.Update();
        Assert.That(avatar.Position, Is.EqualTo(new Position(MapGeometry.Size, 11)));
        Assert.That(avatar.Velocity, Is.EqualTo(new Velocity(0, 1)), "viewers extrapolate: the stop is part of the state");
    }

    [Test]
    public void InputIsClampedAndCellsAreTheGrid()
    {
        Assert.That(MapGeometry.Clamp(9, -9), Is.EqualTo(new Velocity(MapGeometry.MaxSpeed, -MapGeometry.MaxSpeed)));
        Assert.That(MapGeometry.CellOf(new Position(MapGeometry.Size, MapGeometry.Size)), Is.EqualTo(MapGeometry.Side * MapGeometry.Side - 1));
        Assert.That(MapGeometry.Around(0, stackalloc int[9]).Length, Is.EqualTo(4), "a corner has 4 cells around it");
        Assert.That(MapGeometry.Near(0, MapGeometry.Side + 1), Is.True);
        Assert.That(MapGeometry.Near(0, 2), Is.False);
    }
}
