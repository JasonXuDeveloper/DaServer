using System.Buffers.Binary;
using System.Numerics;

namespace DaServer.Benchmark.MapTick;

/// <summary>
/// What both maps of <see cref="MapTickBenchmark"/> simulate, so they do the same work: avatars on a grid of cells
/// (about ten per cell), a few of them turning each tick, everyone moving every tick (wrapping at the edges), and
/// every avatar a client that sees the 3x3 cells around its own. Viewers are told when an avatar appears, disappears
/// or changes velocity (dead reckoning). Decisions come from a hash of (avatar, tick): deterministic, no state.
/// </summary>
public sealed class MapScenario
{
    public const float CellSize = 32, Speed = 40, DeltaTime = 0.05f;
    public const int MovedSize = 20, AppearSize = 12, DisappearSize = 4;

    public readonly int Avatars, Side, Cells, TurnPermille;
    public readonly float Size;

    /// <summary>The avatars of each cell (indexes), kept by whichever map runs.</summary>
    public readonly List<int>[] Members;

    /// <summary>What each avatar's client was sent (frames and bytes): the sink of both maps.</summary>
    public readonly Session[] Sessions;

    /// <summary>Room for one tick's serialized frames: reset every tick, never reallocated.</summary>
    public readonly byte[] Wire;
    public int WireLength;

    public MapScenario(int avatars, int turnPermille)
    {
        Avatars = avatars;
        TurnPermille = turnPermille;
        Side = Math.Max(3, (int)MathF.Ceiling(MathF.Sqrt(avatars / 10f)));
        Cells = Side * Side;
        Size = Side * CellSize;
        Members = new List<int>[Cells];
        for (int c = 0; c < Cells; c++) Members[c] = new List<int>(32);
        Sessions = new Session[avatars];
        Wire = new byte[avatars * 64 * 16];
    }

    public static uint Hash(int avatar, long tick)
    {
        ulong x = (ulong)(uint)avatar * 0x9E3779B97F4A7C15UL ^ (ulong)tick * 0xC2B2AE3D27D4EB4FUL;
        x ^= x >> 31;
        x *= 0xBF58476D1CE4E5B9UL;
        return (uint)(x ^ (x >> 29));
    }

    public Vector2 Start(int avatar) =>
        new(Hash(avatar, -1) % 10_000 / 10_000f * Size, Hash(avatar, -2) % 10_000 / 10_000f * Size);

    /// <summary>Whether the avatar turns at <paramref name="tick"/>, and its new velocity.</summary>
    public bool Turns(int avatar, long tick, out Vector2 velocity)
    {
        uint h = Hash(avatar, tick);
        velocity = default;
        if (h % 1000 >= (uint)TurnPermille) return false;
        float angle = (h >> 10) % 6283 / 1000f;
        velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Speed;
        return true;
    }

    public int CellOf(Vector2 p) => (int)(p.Y / CellSize) % Side * Side + (int)(p.X / CellSize) % Side;

    public float Wrap(float v) => v < 0 ? v + Size : v >= Size ? v - Size : v;

    /// <summary>Whether cell <paramref name="b"/> is in the 3x3 neighbourhood of <paramref name="a"/> (wrapping).</summary>
    public bool Near(int a, int b)
    {
        int dx = Math.Abs(a % Side - b % Side), dy = Math.Abs(a / Side - b / Side);
        return (dx <= 1 || dx == Side - 1) && (dy <= 1 || dy == Side - 1);
    }

    /// <summary>The 9 cells around <paramref name="cell"/> (fewer distinct ones on a 3-wide grid: duplicates skipped).</summary>
    public int Around(int cell, Span<int> into)
    {
        int x = cell % Side, y = cell / Side, n = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int c = (y + dy + Side) % Side * Side + (x + dx + Side) % Side;
            if (into[..n].IndexOf(c) < 0) into[n++] = c;
        }

        return n;
    }

    public void MoveMember(int avatar, int from, int to)
    {
        if (from >= 0)
        {
            var list = Members[from];
            int i = list.IndexOf(avatar);
            list[i] = list[^1];
            list.RemoveAt(list.Count - 1);
        }

        Members[to].Add(avatar);
    }

    // ---------------- the wire: a frame is the bytes of one or more events ----------------

    public int Begin() => WireLength;

    public void WriteMoved(int avatar, Vector2 at, Vector2 velocity)
    {
        var span = Wire.AsSpan(WireLength, MovedSize);
        BinaryPrimitives.WriteInt32LittleEndian(span, avatar);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], at.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], at.Y);
        BinaryPrimitives.WriteSingleLittleEndian(span[12..], velocity.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], velocity.Y);
        WireLength += MovedSize;
    }

    public void WriteAppear(int avatar, Vector2 at)
    {
        var span = Wire.AsSpan(WireLength, AppearSize);
        BinaryPrimitives.WriteInt32LittleEndian(span, avatar);
        BinaryPrimitives.WriteSingleLittleEndian(span[4..], at.X);
        BinaryPrimitives.WriteSingleLittleEndian(span[8..], at.Y);
        WireLength += AppearSize;
    }

    public void WriteDisappear(int avatar)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Wire.AsSpan(WireLength, DisappearSize), avatar);
        WireLength += DisappearSize;
    }

    public ReadOnlyMemory<byte> End(int start) => Wire.AsMemory(start, WireLength - start);

    public void Send(int session, ReadOnlyMemory<byte> frame) => Sessions[session].Enqueue(frame);

    /// <summary>Frames and bytes sent to every client since the start (both maps must send the same bytes).</summary>
    public (long Frames, long Bytes) Totals()
    {
        long frames = 0, bytes = 0;
        foreach (ref readonly var s in Sessions.AsSpan()) (frames, bytes) = (frames + s.Frames, bytes + s.Bytes);
        return (frames, bytes);
    }
}

/// <summary>A client's outgoing queue as the benchmark models it: a ring of frames and its counters.</summary>
public struct Session
{
    private ReadOnlyMemory<byte>[]? _ring;
    private int _next;
    public long Frames, Bytes;

    public void Enqueue(ReadOnlyMemory<byte> frame)
    {
        _ring ??= new ReadOnlyMemory<byte>[64];
        _ring[_next++ & 63] = frame;
        Frames++;
        Bytes += frame.Length;
    }
}
