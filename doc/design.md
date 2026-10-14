# DaServer design

DaServer is a .NET 10 framework for building **clusters**: game servers (many gateways, many map/logic nodes) and
other low-latency, high-concurrency backends. Developers write contracts (interfaces), implementations, and ECS
systems. Source generators write the rest: proxies, dispatchers, registration and query jobs.

Priorities, in order:

1. Latency and throughput, with zero GC on hot paths.
2. Correctness under concurrency and failures.
3. Code a person or an AI can extend without touching plumbing.

## 1. Building blocks

| Concept | What it is | Threading |
|---|---|---|
| **Node** | One process. Has a `NodeId`, roles, hosted contracts, a cluster endpoint and optionally a client endpoint. | IO threads plus the shards it owns. |
| **Shard** | A single-threaded executor. It has a lock-free mailbox, an EasyEcs `World`, timers and a fixed-rate tick. A node runs N shards (default: one per core). | One dedicated thread. Every handler and system of the shard runs on it, so user code needs no locks. |
| **Actor** (`IActor` contract) | Keyed, stateful (player, map instance, room, guild). One activation per key in the cluster, living in one shard. Its state lives in ECS components on its entity. | Its shard's thread. |
| **Service** (`IService` contract) | Stateless (login, matchmaking, chat routing). One instance per shard; calls are load-balanced. | The shard thread of the instance that handles the call. |
| **Client callback** (`IClientCallback`) | Server → client push contract. | Fire-and-forget. |
| **Event** (`[EventHandler]`) | Synchronous, in-shard, decoupled module events (e.g. `LevelUp` → quests, achievements). | The publisher's shard thread. |
| **System** (`[Query]` methods) | Per-tick ECS logic (movement, AOI, buffs). | The shard thread. |

Data flow of a call:

```
caller (shard thread or client)
  → generated proxy serializes the arguments once into a pooled frame
    → same node:  frame handed to the target shard's mailbox (zero copy)
    → other node: frame copied into the node link's batch buffer → TCP → peer IO thread → target shard's mailbox
  → target shard thread: generated dispatcher deserializes the arguments and invokes the implementation
  → reply: same path in reverse; the caller's continuation resumes on the caller's shard thread
```

## 2. Wire protocol

- Frames carry a fixed 24-byte header: `length`, `kind`, `status`, `hops`, `flags`, `requestId`, `methodId`, `key`. The payload is Nino-serialized arguments.
- `methodId` is FNV-1a of `Namespace.Interface.Method(ParamTypes)`, computed at compile time. Collisions are build errors from the generator and registration errors at runtime.
- Frame kinds:
  - `Request` / `OneWay` / `Response`
  - `Push` (one session)
  - `Broadcast` (a group of sessions)
  - `Control` (handshake, gossip, acks, session binding)
  - `Ping` / `Pong`
- Gateways route client frames by patching header fields (`requestId`, `key`). They never deserialize payloads.

## 3. Performance model (zero GC)

| Concern | Design |
|---|---|
| Message objects | None. Arguments are serialized straight into pooled `FrameWriter` buffers; dispatchers deserialize straight from the received bytes. |
| Buffers | `BufferPool`: power-of-two buckets on the **Pinned Object Heap**. Sockets use the buffers without pinning, and they never fragment gen0/1/2. |
| Queues | Bounded Vyukov MPMC rings with struct slots. Producer and consumer cursors are padded onto separate 64-byte cache lines. |
| Awaiting | `PendingCall<T>` is a pooled `IValueTaskSource<T>`. Dispatch helpers use `PoolingAsyncValueTaskMethodBuilder`. A synchronously completed handler replies without any state machine. |
| Sending | Each connection copies frames into one batch buffer under a short lock. One send loop swaps and writes whole batches, so N frames cost one syscall. A broadcast serializes once and fans out by `memcpy`. |
| Receiving | Frames are parsed in place. Zero-byte reads mean an idle client connection holds no receive buffer, which matters with tens of thousands of sockets. |
| Proxies | Generated `readonly struct` proxies, so `runtime.Player(42).AddGold(10)` allocates nothing. |
| ECS | EasyEcs v4: struct components, `[Query]` jobs, zero allocations per tick. |
| GC mode | Server GC, concurrent, `SustainedLowLatency` during steady state. Allocation tests in CI hold the zero-GC budget. |

## 4. Latency

- A shard wakes **as soon as** a message arrives. It doesn't wait for the next tick. It waits on its mailbox signal with a timeout set to the next tick deadline, so message latency is independent of tick rate.
- At most two thread hops per remote call: the IO thread, then the target shard. Same-node calls skip the socket.
- `TCP_NODELAY`. Batching comes from the send loop draining everything queued while the previous write was in flight. It adds no timer delay.
- Gateways forward bytes without parsing payloads.

## 5. Reliability

| Layer | Guarantee |
|---|---|
| Ordering | FIFO per (sender shard → target actor): one link per node pair and one mailbox per shard. |
| Requests | At most once, with timeouts (`RpcStatus.Timeout`, `Disconnected`, `NotHosted`). Retrying is the caller's choice, and only for idempotent operations. |
| Link resumption | Node links and client sessions number their stream by **byte offset**. Sent batches stay in an unacked list (the batch buffers themselves, so no extra copy) until the peer acks the offset. On reconnect, the handshake exchanges the last received offset and the sender replays from there. A network blip becomes a delay instead of a lost message. Clients resume within a grace period (default 30 s) after switching networks. |
| Backpressure | Mailboxes are bounded. When a shard is full, the remote link's receive loop pauses, which pushes back through TCP. A slow client hits a per-session send limit and is disconnected (it can resume). |
| Single activation | Rendezvous hashing over the `Up` nodes that host the actor type: every node computes the same owner from the same view. A node activates an actor only if it is the owner in its own view. Otherwise it forwards the call (hop limit 2) or answers `NotHosted`, and the caller retries once membership converges. When ownership changes, the old owner deactivates (`OnDeactivateAsync`, the persistence hook) before the new one activates on the next call. |
| Graceful shutdown | `SIGTERM` → `Leaving` (gossiped) → stop accepting activations → drain mailboxes → deactivate actors → exit. |

## 6. Membership and discovery

- Seeds come from static `host:port` entries or DNS names (a Kubernetes headless service resolves to every pod). A node joins through any reachable seed.
- Gossip runs every second, from each node to up to 3 random peers. The digest is the full member table: `NodeId`, endpoints, roles, hosted contract ids, incarnation, heartbeat counter, status and load. Merge rule: the higher incarnation wins; within the same incarnation, the higher heartbeat wins, and the status order is `Joining < Up < Leaving < Dead`.
- Failure detection doesn't depend on synchronized clocks:
  - A member whose heartbeat hasn't advanced for `SuspectAfter` (3 s) becomes `Suspect`.
  - After `DeadAfter` (10 s) it is `Dead`.
  - A node that hears it is suspected refutes by bumping its incarnation.
- Routing reads an immutable snapshot (`ClusterView`) that is swapped atomically on change. Lookups are lock-free.

## 7. Game server reference architecture

```
              clients (TCP, session resumption)
                 │            │
           ┌─────▼────┐  ┌────▼─────┐        role: gateway
           │ gateway1 │  │ gateway2 │        sessions, auth binding, push/broadcast fan-out
           └─────┬────┘  └────┬─────┘
                 │  node links (reliable, batched)
     ┌───────────┼────────────┼───────────┐
┌────▼───┐  ┌────▼───┐   ┌────▼───┐  ┌────▼───┐    role: game
│ game1  │  │ game2  │   │ game3  │  │ login  │    IPlayer actors, IMap actors (AOI via ECS systems),
│shards×N│  │shards×N│   │shards×N│  │services│    stateless services; any node can host any contract
└────────┘  └────────┘   └────────┘  └────────┘
```

- **Login.** The client calls `IAuth.Login`, a client-accessible service. The handler validates credentials and binds the session to a player id. Later client calls to `[ClientAccessible]` actor contracts are routed with the **bound** key, so a client can never address someone else's actor.
- **Player and map.**
  - `IPlayer` is keyed by player id. It holds persistent state: inventory, quests, currencies.
  - `IMap` is keyed by map instance id. It owns the spatial simulation: an ECS world with `[Query]` systems for movement, AOI and combat.
  - The player's avatar entity lives in the map. To switch maps, the client calls `Leave` on map A, then `Enter` on map B, and the player actor stays where it is.
- **Push and broadcast.**
  - `runtime.Client<IPlayerClient>(session).OnChat(msg)` pushes to one session.
  - `runtime.Broadcast<IPlayerClient>(groupId).OnChat(msg)` serializes once and sends one frame per gateway. Each gateway fans out to its local sessions in the group.
  - AOI lists, guild channels and shard chat are all groups.

## 8. Developer experience (generated code)

The developer writes:

```csharp
[ClientAccessible]
public interface IPlayer : IActor
{
    ValueTask<int> AddGold(int amount);
    void Emote(int id);                          // one-way
}

public sealed partial class Player : Actor, IPlayer
{
    public ValueTask<int> AddGold(int amount)
    {
        ref var wallet = ref Get<Wallet>();
        wallet.Gold += amount;
        return new(wallet.Gold);
    }

    public void Emote(int id) => Runtime.Broadcast<IPlayerClient>(MapGroup).OnEmote(Key, id);
}

public partial class RegenSystem : SystemBase
{
    [Query, Without<Dead>]
    static void Regen(ref Health hp, in Regen r) => hp.Value = Math.Min(hp.Max, hp.Value + r.PerTick);
}
```

The generator produces:

- the proxy struct, the dispatcher switch and contract registration
- actor and service factories, and system registration
- `[Query]` jobs
- `[EventHandler]` dispatch tables

It also reports diagnostics for invalid signatures, `Task` instead of `ValueTask`, non-partial types and id collisions. There is no reflection, no configuration strings and no hand-written registration list.

## 9. Orchestration

- One binary per application. Roles and hosted contracts come from configuration (`DaServer:Roles`, environment `DaServer__Roles=gateway`).
- `DaServer.Hosting` integrates with the .NET Generic Host: configuration, logging, `SIGTERM` → graceful leave.
- Container image, `docker-compose` (2 gateways + 3 game nodes) and Kubernetes manifests: Deployments, a headless Service for seed discovery, readiness on cluster join, and a `preStop` drain.

## 10. Quality gates

| Level | What | Where |
|---|---|---|
| Unit | Queues, buffer pool, frame parsing, pending-call table, membership merge, rendezvous placement, generator output (Roslyn driver) and diagnostics | `tests/DaServer.Tests` |
| Integration | An in-process cluster of N nodes on loopback. Covers: RPC round trip, ordering, single activation across nodes, failover after a node is killed, gateway login → bind → call → push → broadcast, session resumption after a socket drop | `tests/DaServer.Tests` |
| Allocation | Steady-state RPC, push and broadcast loops allocate 0 bytes (`GC.GetTotalAllocatedBytes(precise: true)`) | `tests/DaServer.Tests` |
| E2E | `docker compose` cluster with the sample server, plus the bot client running a scripted session | `deploy/`, CI job |
| Benchmarks | BenchmarkDotNet on real loopback sockets: RPC round trip at 1/64/1,024 outstanding calls, broadcast to 10k sessions, cross-shard actor messages, a gateway with 10k clients at 20 msg/s, and an EasyEcs map tick with 100k entities | `benchmarks/DaServer.Benchmark` |
