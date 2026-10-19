using Nino.Core;

namespace DaServer.Tests;

// The "business code" the tests drive: messages, handlers and systems written exactly as users write them, one file
// per area (Contracts.*.cs). The ids used across areas are here.

[NinoType] public partial record struct Point(int X, int Y);

public readonly record struct CounterId(long Value) : IActorId;
public readonly record struct VisitorId(long Value) : IActorId;
public readonly record struct GuestId(long Value) : IActorId;
public readonly record struct PlayerId(long Value) : IActorId;
public readonly record struct RoomId(long Value) : IActorId;
