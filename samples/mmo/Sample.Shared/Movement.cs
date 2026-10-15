using EasyEcs;

namespace Sample.Shared;

// Movement rules every side runs the same way: the map node simulates avatars with them, and clients predict their own
// avatar with them between server updates (dead reckoning), so prediction and authority never disagree on the rules.

public record struct Position(float X, float Y) : IComponent;

public record struct Velocity(float X, float Y) : IComponent;

/// <summary>The map's extent and its grid of interest cells (100 x 100 units, 20 x 20 cells).</summary>
public static class MapGeometry
{
    public const float Size = 2000f;
    public const float CellSize = 100f;
    public const int Side = (int)(Size / CellSize);

    /// <summary>Fastest an avatar moves, per tick and axis; faster input is clamped.</summary>
    public const float MaxSpeed = 5f;

    public static Velocity Clamp(float x, float y) => new(Math.Clamp(x, -MaxSpeed, MaxSpeed), Math.Clamp(y, -MaxSpeed, MaxSpeed));

    public static int CellOf(in Position p) =>
        Math.Min((int)(p.Y / CellSize), Side - 1) * Side + Math.Min((int)(p.X / CellSize), Side - 1);

    /// <summary>The cells of the 3x3 around <paramref name="cell"/> that are inside the map, into <paramref name="cells"/> (9 long).</summary>
    public static Span<int> Around(int cell, Span<int> cells)
    {
        int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            if (Offset(cell, dx, dy) is var c and >= 0)
                cells[n++] = c;
        return cells[..n];
    }

    /// <summary>The cell at (<paramref name="dx"/>, <paramref name="dy"/>) from <paramref name="cell"/>; -1 outside the map.</summary>
    public static int Offset(int cell, int dx, int dy)
    {
        int x = cell % Side + dx, y = cell / Side + dy;
        return x < 0 || y < 0 || x >= Side || y >= Side ? -1 : y * Side + x;
    }

    /// <summary>Whether <paramref name="a"/> is in the 3x3 around <paramref name="b"/>.</summary>
    public static bool Near(int a, int b) => Math.Abs(a % Side - b % Side) <= 1 && Math.Abs(a / Side - b / Side) <= 1;
}

/// <summary>Integrates velocities inside the map; the border stops an avatar (its viewers learn it from the velocity).</summary>
public sealed partial class MovementSystem : EcsSystem
{
    [ForEach]
    private static void Move(ref Position p, ref Velocity v)
    {
        if (v.X == 0 && v.Y == 0) return;
        float x = p.X + v.X, y = p.Y + v.Y;
        float cx = Math.Clamp(x, 0f, MapGeometry.Size), cy = Math.Clamp(y, 0f, MapGeometry.Size);
        p = new Position(cx, cy);
        if (cx == x && cy == y) return;
        v = new Velocity(cx == x ? v.X : 0, cy == y ? v.Y : 0); // clients extrapolate: they must learn about the stop
    }
}
