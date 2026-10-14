# Architecture decision records

Short records of non-obvious decisions. Newest last. Each: context → decision → consequences.

## ADR-001 .NET 10 only, single runtime assembly
- Context: earlier split Shared (netstandard2.1 for Unity) / Core (server) duplicated code paths with `#if`.
- Decision: target `net10.0` only; one runtime assembly `DaServer` (server + client), plus `DaServer.Generator`
  and `DaServer.Hosting`.
- Consequences: use .NET 10 APIs freely (`Lock`, frozen collections, POH, `PoolingAsyncValueTaskMethodBuilder`,
  C# 14). Unity clients would need a separate thin client later.

## ADR-002 Shards: single-threaded shards with a mailbox, not a thread-pool actor runtime
- Context: a studied game server resumed continuations on pool threads and enqueued from many producers into an SPSC queue.
- Decision: each node runs N shards (default one per core), each a dedicated thread owning an EasyEcs `World`, a
  bounded lock-free MPMC mailbox (overflow queue, never drops), timers and a fixed tick. Actors and service instances
  live in exactly one shard.
- Consequences: no locks in user code; deterministic ordering per shard; messages are processed immediately (tick rate
  only drives systems/timers). ECS systems must complete synchronously per tick (the shard pumps its mailbox while
  waiting, so an async system cannot deadlock it, but it stalls the tick).

## ADR-003 Completing RPCs on the caller's shard (no SynchronizationContext.Post)
- Context: `ManualResetValueTaskSourceCore` with a captured SynchronizationContext allocates a Tuple per Post.
- Decision: `PendingCallBase` is a custom pooled `IValueTaskSource` that records the caller's shard; responses are
  posted to that shard's mailbox and completed there, running the continuation inline. A `ShardSynchronizationContext`
  is still installed so foreign awaits (DB drivers, Task.Delay) marshal back to the shard thread.
- Consequences: zero allocation per awaited RPC; ExecutionContext/AsyncLocal does not flow across RPC awaits.

## ADR-004 Async handler tracking
- Decision: a handler returning a non-completed `ValueTask` is added to the shard's in-flight list and swept each loop
  iteration (`GetResult` returns pooled state machines to their pools). The list also enables graceful drain.
- Consequences: synchronous handlers allocate nothing; async ones allocate only their own state machine (zero if they
  use `PoolingAsyncValueTaskMethodBuilder`).

## ADR-005 Wire format
- Decision: fixed 24-byte little-endian header (length, kind, status, hops, flags, requestId, methodId, key) + Nino
  payload of the arguments serialized one by one (no generated argument structs: Nino's generator cannot see other
  generators' output). Method id = FNV-1a 32 of `Namespace.Interface.Method(ParamTypes)`, computed at compile time.
- Consequences: gateways route by patching header fields without parsing payloads; collisions are build/registration
  errors resolvable with `[RpcId]`.

## ADR-006 Connection send path: copy into a per-connection batch
- Decision: `Connection.Send` copies the frame into one pending batch buffer under a short lock; one send loop swaps
  and writes whole batches. Broadcast serializes once, then memcpy per recipient.
- Consequences: N small frames = 1 syscall; no per-frame objects; per-connection backlog bounded by
  `MaxPendingSendBytes` (slow consumers are disconnected and may resume, see ADR-008).

## ADR-007 Placement by rendezvous hashing + forwarding
- Decision: actor owner = highest-random-weight node among `Up` members hosting the implementation. A node activates
  only if it is the owner in its own view, otherwise forwards (hop limit) or returns `NotHosted`. On view changes shards
  deactivate actors they no longer own.
- Consequences: no central directory, O(nodes) placement computation; brief disagreement during membership changes is
  handled by forwarding/retry rather than a lease protocol (documented limitation).

## ADR-008 Reliability via byte-offset resumable links (planned)
- Decision: node links and client sessions number the stream by byte offset; sent batch buffers stay in an unacked
  list until acked; on reconnect the handshake exchanges the last received offset and the sender replays.
- Consequences: transient disconnects become delays (no lost messages) without an extra copy per frame.

## ADR-009 Hosting and orchestration
- Decision: `DaServer.Hosting` on the .NET Generic Host (configuration, ILogger, SIGTERM → graceful leave),
  `System.Diagnostics.Metrics` + OpenTelemetry for metrics, an Aspire AppHost sample for local multi-node runs, and
  docker-compose / Kubernetes (headless service for DNS seeds) for deployment.

## ADR-010 Benchmarks and tests
- Decision: BenchmarkDotNet only, over real loopback sockets, modelling real load (thousands of sessions, broadcast
  fan-out, concurrent callers, cross-shard traffic). Integration tests run multi-node clusters in-process. Allocation
  tests assert 0 B in steady-state loops.

## ADR-011 Naming: Shard (DaServer executor) vs World (EasyEcs)
- Context: EasyEcs v4 adopted standard ECS vocabulary (`World`, `Query`, `SystemBase.OnUpdate`).
- Decision: DaServer's single-threaded executor is a `Shard`; each shard owns one EasyEcs `World`
  (`shard.World`, `actor.World`). Actors/services expose `Shard`, `World` and `Runtime` (the node).
- Consequences: no name clash; "world" always means the ECS world.

## ADR-012 Source generation instead of registration code
- Decision: `ContractGenerator` emits, per contract, a struct proxy (arguments serialized straight into a pooled
  frame), a dispatcher switch (arguments deserialized straight from the received frame), wire ids, a module-initializer
  registration and invoker accessors (`runtime.Player(key)`, `runtime.Auth()`, `runtime.PlayerClient(session)`,
  `runtime.PlayerClientGroup(id)`). `HostGenerator` registers implementations (with `[Role]`), EasyEcs systems, and
  runs the module initializers of referenced contract assemblies (found via `[assembly: RpcContractsAssembly]`).
- Consequences: no reflection, no configuration strings, no hand-maintained lists; invalid contracts are compile
  errors (DAS001-DAS008).
