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
  `System.Diagnostics.Metrics` + OpenTelemetry for metrics. Superseded for deployment by ADR-015.

## ADR-010 Benchmarks and tests
- Decision: BenchmarkDotNet only, over real loopback sockets, modelling real load (thousands of sessions, broadcast
  fan-out, concurrent callers, cross-shard traffic). Integration tests run multi-node clusters in-process. Allocation
  tests assert 0 B in steady-state loops.

## ADR-011 Naming: Shard (DaServer executor) vs World (EasyEcs)
- Context: EasyEcs v4 adopted standard ECS vocabulary (`World`, `Query`, `EcsSystem.OnUpdate`).
- Decision: DaServer's single-threaded executor is a `Shard`; each shard owns one EasyEcs `World`
  (`shard.World`, `actor.World`). Actors/services expose `Shard`, `World` and `Cluster` (the node); ECS code reaches
  them through the `ShardContext` singleton (`world.Cluster()`).
- Consequences: no name clash; "world" always means the ECS world.

## ADR-012 Source generation instead of registration code
- Decision: `ContractGenerator` emits, per contract, a struct proxy (arguments serialized straight into a pooled
  frame), a dispatcher switch (arguments deserialized straight from the received frame), wire ids, a module-initializer
  registration and cluster accessors (`Cluster.Player(key)`, `Cluster.Auth()`, `Cluster.PlayerClient(session)`,
  `Cluster.PlayerClient(group)`). `HostGenerator` registers implementations (with `[Role]`), EasyEcs systems, and
  runs the module initializers of referenced contract assemblies (found via `[assembly: RpcContractsAssembly]`).
- Consequences: no reflection, no configuration strings, no hand-maintained lists; invalid contracts are compile
  errors (DAS001-DAS008).

## ADR-013 The shard runtime is ECS
- Context: the shard was an imperative loop (drain a shard-wide queue, sweep an in-flight list, run timers, tick)
  with ad-hoc per-actor queues for activation. EasyEcs was only used by user systems. a studied game server's useful idea:
  actors are entities with their own mailbox, drained by systems; infrastructure (receive dispatch, timers) is systems
  over singleton state; dirty sets avoid scanning everything.
- Decision: `Shard` keeps only the thread, the cross-thread inbox and the loop. Activations are entities
  (`ActorLink`, `ActorIdentity`, `Mailbox`, `Turn`, `Deactivating`); directories, the ready queue, timers and
  running service calls are singletons; `InboxSystem`, `TurnSystem`, `MailboxSystem`, `ServiceTaskSystem`,
  `TimerSystem` and `RebalanceSystem` each do one job. They run in the `ShardMessages` system group on every wake;
  user systems run in the default group at the tick rate (EasyEcs system groups were added for this).
- Consequences: the runtime obeys the ECS rules it asks users to follow. Per message: one dictionary lookup, one
  queue push, the handler. Busy actors are found by archetype and idle actors with mail come from a ready queue
  (dirty set), so no system scans all actors per wake. Measured: zero allocation per cross-shard call.

## ADR-014 Turn-based actors
- Context: handlers used to interleave at awaits (reentrant), so actor state could change between an await and the
  code after it, unlike the actor model most developers expect.
- Decision: one message at a time per actor, including its awaits (`Turn` component). Services stay concurrent
  (stateless).
- Consequences: no interleaving bugs in actor state. Call cycles (A awaits B awaits A) deadlock until the call timeout;
  use one-way calls or events for such flows. A `[Reentrant]` opt-in can be added if a real need appears.

## ADR-015 Orchestration with Aspire
- Context: hand-written Dockerfile, compose and Kubernetes files duplicated the topology (ports, seeds, roles) and
  drifted from the code.
- Decision: `DaServer.Aspire` describes a cluster in the AppHost (`AddDaServerCluster`, `AddNodes`, `WithGateways`).
  The same model runs locally with the dashboard and publishes Docker Compose or Kubernetes through Aspire publishers.
  Nodes export logs and metrics over OTLP when Aspire (or any collector) configures it. `deploy/` was removed.
- Consequences: one source of truth for topology. Ports are assigned deterministically per node. Replicas are
  separate resources because cluster members are addressed individually, not through a load balancer.

## ADR-016 Analyzer separate from generators
- Context: generators that report diagnostics carry symbols and locations in their models, which defeats caching.
- Decision: generators use syntax-only predicates and equatable, symbol-free models with tracking names, and report
  nothing. `DaServerAnalyzer` reports DAS001–DAS008 (collisions in a compilation-end action), and reads symbols
  through the same `Shapes` code as the generators.
- Consequences: edits that do not touch contracts hit the cache (tested); "skipped by the generator" and "reported by
  the analyzer" cannot disagree.
