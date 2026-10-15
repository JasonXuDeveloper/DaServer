# Architecture decision records

Short records of non-obvious decisions. Newest last. Each: context → decision → consequences.

## ADR-001 .NET 10 only, single runtime assembly
- Context: earlier split Shared (netstandard2.1 for Unity) / Core (server) duplicated code paths with `#if`.
- Decision: target `net10.0` only; one runtime assembly `DaServer` (server + client), plus `DaServer.Generator`
  and `DaServer.Hosting`.
- Consequences: use .NET 10 APIs freely (`Lock`, frozen collections, POH, `PoolingAsyncValueTaskMethodBuilder`,
  C# 14). Unity clients would need a separate thin client later.

## ADR-002 Shards: single-threaded shards with a mailbox, not a thread-pool actor runtime
- Context: RandomGame resumed continuations on pool threads and enqueued from many producers into an SPSC queue.
- Decision: each node runs N shards (default one per core), each a dedicated thread owning an EasyEcs `World`, a
  bounded lock-free MPMC mailbox (overflow queue, never drops), timers and a fixed tick. Actors and service instances
  live in exactly one shard.
- Consequences: no locks in user code; deterministic ordering per shard; messages are processed immediately (tick rate
  only drives systems/timers). ECS systems must complete synchronously per tick (the shard pumps its mailbox while
  waiting, so an async system cannot deadlock it, but it stalls the tick).

## ADR-003 Completing RPCs on the caller's shard (no SynchronizationContext.Post)
- Context: `ManualResetValueTaskSourceCore` with a captured SynchronizationContext allocates a Tuple per Post.
- Decision: `PendingCall<T>` is a custom pooled `IValueTaskSource` that records the caller's shard; responses are
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
  `MaxPendingSendBytes` (slow consumers are disconnected; node links then resume, see ADR-008).

## ADR-007 Placement by rendezvous hashing + forwarding
- Decision: actor owner = highest-random-weight node among `Up` members hosting the implementation. A node activates
  only if it is the owner in its own view, otherwise forwards (hop limit) or returns `NotHosted`. On view changes shards
  deactivate actors they no longer own.
- Consequences: no central directory, O(nodes) placement computation; brief disagreement during membership changes is
  handled by forwarding/retry rather than a lease protocol (documented limitation).

## ADR-008 Reliability via byte-offset resumable links
- Context: a dropped node connection failed every call in flight and silently lost one-way calls, events, pushes
  and session control queued on it; a network blip between a game node and a gateway looked like a crash.
- Decision: each node link numbers its stream by byte offset (frames sent through the link, across connections).
  The connection's send loop copies each batch, before writing it, into the link's `ResendWindow` (pooled 64 KB
  chunks). A new connection opens with a Hello carrying the stream id; the receiver lets the previous inbound
  connection deliver its last frame, then answers `Resume` with the bytes it processed and its node id; the sender
  replays the rest, then the backlog, then new frames. The receiver acknowledges every 64 KB and on the 100 ms
  maintenance tick, which releases chunks. The stream restarts (calls in flight fail with `Disconnected`, outcome
  unknown as after a timeout) when the peer's node id changed (restart), when the window no longer holds what the
  peer needs (`NodeOptions.LinkResendWindow`, 32 MB), after the reconnect attempts run out, or when the member is
  declared dead.
- Alternatives: keep the sent batch buffers themselves (no copy) — node links batch into 256 KB buffers, so a few
  small batches would pin megabytes until acknowledged; one memcpy per batch on the send loop (not on producers)
  keeps memory proportional to unacknowledged bytes. Per-frame sequence numbers — bytes are simpler and exact
  because frames are whole in every batch. Client sessions: not resumed (a client reconnects and logs in again;
  resuming would keep session state alive on the gateway during the gap).
- Consequences: a transient disconnect is a delay: no loss, no duplicate (`LinkTests` drop connections mid-stream).
  Cost: one memcpy per batch on the send loop and one type check per received frame; BenchmarkDotNet RPC numbers are
  unchanged within noise. A crashed peer fails calls when it is declared dead (or at their timeout), not at the
  first socket error.

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
- Status: the contract-based generator below was replaced by `MessageGenerator` and per-handler dispatchers (ADR-035).
- Decision: `ContractGenerator` emits, per contract, a struct proxy (arguments serialized straight into a pooled
  frame), a dispatcher switch (arguments deserialized straight from the received frame), wire ids, a module-initializer
  registration and cluster accessors (`Cluster.Player(key)`, `Cluster.Auth()`, `Cluster.PlayerClient(session)`,
  `Cluster.PlayerClient(group)`). `HostGenerator` registers implementations (with their contracts and handled events),
  EasyEcs systems, and runs the module initializers of referenced contract assemblies (found via
  `[assembly: RpcContractsAssembly]`). Roles were removed by ADR-017.
- Consequences: no reflection, no configuration strings, no hand-maintained lists; invalid contracts are compile
  errors (DAS001-DAS012).

## ADR-013 The shard runtime is ECS
- Context: the shard was an imperative loop (drain a shard-wide queue, sweep an in-flight list, run timers, tick)
  with ad-hoc per-actor queues for activation. EasyEcs was only used by user systems. RandomGame's useful idea:
  actors are entities with their own mailbox, drained by systems; infrastructure (receive dispatch, timers) is systems
  over singleton state; dirty sets avoid scanning everything.
- Status: the data and systems were refined by ADR-037 (lifecycle tags, posted turn ends).
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
- Decision: `DaServer.Aspire` describes a cluster in the AppHost (`AddDaServerCluster`, `AddNodes`, `AddGateways`,
  `WithGateways`).
  The same model runs locally with the dashboard and publishes Docker Compose or Kubernetes through Aspire publishers.
  Nodes export logs and metrics over OTLP when Aspire (or any collector) configures it. `deploy/` was removed.
- Consequences: one source of truth for topology. Ports are assigned deterministically per node. Replicas are
  separate resources because cluster members are addressed individually, not through a load balancer.

## ADR-016 Analyzer separate from generators
- Context: generators that report diagnostics carry symbols and locations in their models, which defeats caching.
- Decision: generators use syntax-only predicates and equatable, symbol-free models with tracking names, and report
  nothing. `DaServerAnalyzer` reports DAS001–DAS012 (collisions in a compilation-end action), and reads symbols
  through the same `Shapes` code as the generators.
- Consequences: edits that do not touch contracts hit the cache (tested); "skipped by the generator" and "reported by
  the analyzer" cannot disagree.

## ADR-017 No roles: the deployment unit is a project
- Context: nodes used to carry role strings (`[Role("game")]` on implementations, `DaServer:Roles` in configuration).
  The same strings were repeated in code, configuration and the AppHost, a typo silently hosted nothing, and the
  sample hard-coded "game" and "gateway".
- Decision: a node hosts every actor, service and system compiled into its process. Splitting the cluster means
  splitting projects (`Sample.Game`, `Sample.Gateway`); the AppHost lists projects and replica counts. Accepting
  clients is a capability (`ClientPort`, `AddGateways`), not a role. In-process tests narrow hosting with
  `NodeOptions.Hosts`, a type filter.
- Consequences: no topology strings anywhere (checked by the release checklist). The compiler decides what a node can
  host; references between projects decide what can be split.

## ADR-018 Attributes on contracts (revised: markers were replaced)
- Status: superseded by ADR-035 (messages instead of contracts).
- Context: the first version used `[ClientRouteible]`; the second used base interfaces (`IService`, `IPublicService`,
  `IActor`, `ISessionActor`, `IPublicActor`, `IClientCallback`, `IEvent`, `IKeyedEvent`, `IHandle<T>`). Markers made
  client access all-or-nothing per contract, so one actor needed two contracts for one audience split (`IMap` and
  `IMapInput`), and event handling needed one interface per event type plus a fixed `Key` property name.
- Decision: attributes, read by the generator: `[Service]`, `[Actor]`, `[ToClient]` say how the target is chosen;
  `[FromClient]` (on a contract or on single methods, `AnyKey = true` for client-chosen actor keys) says what game
  clients may call; any `[NinoType]` struct is an event, `[ActorKey]` on one member makes it keyed (the generator adds
  `IKeyed`); `[Handle]` marks handler methods. `[NinoType]` marks wire data (DAS009), `[WireId]` resolves a reported
  collision. DAS012-DAS014 report conflicting contract attributes, bad handler shapes and bad keys.
- Consequences: one contract per concept with per-method access; handlers are ordinary methods named freely; the
  vocabulary table in the README covers every declaration.

## ADR-019 Cluster events are delivered through mailboxes, at most once
- Status: delivery stands; ADR-035 made every message travel this way and replaced `[ActorKey]` by `Publish(key, e)`.
- Context: EasyEcs had an in-world event bus, which is not an ECS concern (state changes are data) and could not
  cross shards or nodes. DaServer needed events between actors and services anywhere in the cluster.
- Decision: `[NinoType]` event structs (keyed by a `[ActorKey]` member), handled by `[Handle]` methods of actors and services. Handlers are part of the
  gossiped member info; `Publish` serializes once and sends one `Event` frame per handling implementation. Keyed
  events go to the owner of the key and enter the activation's mailbox (turn-based, activated on demand); service
  handlers run once per implementation, local node first. Delivery is at most once and ordered with the calls of the
  same sender. `SessionClosed` is a framework event sent to every actor a closing session was bound to.
- Alternatives: a broker (Kafka, NATS) on the call path adds a hop and an external dependency to every event;
  subscription state per actor would have to survive rebalancing. Durable events are an application concern (outbox).
- Consequences: publishers never depend on handlers; event delivery costs the same as a one-way call and allocates
  nothing (the key is read through a constrained generic call).

## ADR-020 Business code sees ICluster, not Node
- Context: `ShardObject.Cluster` returned `Node`, exposing lifecycle, membership and internals to business code.
- Decision: `ICluster` (proxies via `IRpcInvoker`, `NodeId`, `Bind`/`Unbind` per session-actor contract, groups,
  `Disconnect`, `Publish`). Actors get a `Group` of their own (`ClientGroup.Of(implementation, key)`), so group ids of
  different features cannot collide.
- Consequences: the surface business code can touch is small and testable; runtime refactors do not break it.

## ADR-021 Generated node entry point
- Context: every node project carried the same three-line `Program.cs`.
- Decision: `HostGenerator` emits `DaServer.Generated.NodeProgram.Main` (Generic Host + `AddDaServer`) for an
  executable that references `DaServer.Hosting` and declares no entry point. Writing a `Program.cs` opts out.
- Consequences: a node project is business classes only; names, ports and seeds come from the AppHost (environment).

## ADR-022 One ordered stream per node direction
- Context: a node answered requests on the accepted connection while its own binds, pushes and events to the caller
  travelled on its outgoing link, so a reply could overtake a `Bind` sent before it (the client called the actor
  before the gateway knew the binding).
- Decision: a node names itself with a `Hello` control frame on every link it opens; the receiver then sends replies
  to that node through its own outgoing link to it (`PeerLink` is the reply sink). Everything node A sends to node B, replies
  included, is one ordered TCP stream.
- Consequences: "the binding is in place when the call that made it returns" holds (tested with a push burst before
  the bind). A dropped connection resumes the stream (ADR-008).

## ADR-023 Placement keeps keys on suspect members
- Context: placement over Up members only moved every key of a node that missed a few gossip rounds (GC pause,
  network blip) and moved them back when it recovered: duplicate activations and lost state on every flap.
- Decision: actor placement ranks Up and Suspect members; services and service events use Up members only. A member
  declared Dead retires every actor it owned elsewhere (`RequestRetireAll` on the owners' view change). Mail for an
  existing activation, including one being deactivated, enters its mailbox, so order holds across a move.
- Consequences: a slow node keeps its keys (calls to it wait or time out instead of activating twice). A partitioned
  node can still run its activations until it is declared dead: fencing by epoch is future work.

## ADR-024 The runtime owns client sessions
- Context: business code had to pair `Bind` with `JoinGroup`, remember sessions to clean up, and copy `CallerSession`
  before the first await; a crashed gateway left actors believing their sessions were alive.
- Decision: `Bind<IX>(session, key)` also puts the session in the activation's `Group`; `Unbind` and close leave it.
  Every activation records the sessions bound to it (a `ClientSessions` component); when a session closes the
  gateway publishes `SessionClosed` to each, and when a gateway is declared dead every node closes that gateway's
  sessions for its own activations. `CallerSession` is the session of the running turn and holds across its awaits.
- Consequences: business code binds and handles `SessionClosed`; nothing else. Gateway crash tests cover it.
  Session control and pushes go to the gateway's link from the view, or, while gossip has not listed the gateway
  yet, from its Hello (a client call can reach a node first): a Bind is never dropped while the cluster forms.

## ADR-025 Gateway groups: slot arrays under a per-group lock
- Context: group membership was a copy-on-write array: O(n) per join (O(n^2) to fill a 3k-session group, with large
  array churn), and broadcasts to everyone took a `ConcurrentDictionary.Values` snapshot (an allocation of 8 B per
  session per broadcast).
- Decision: each group is a slot array; a session knows its slot in each group, leave is swap-remove. Membership
  changes and fan-out both take the group's lock (fan-out reaches every earlier join). `ClientGroup.All` is a group
  every session joins on accept. `Connection.Send` closes a slow consumer off the caller's thread, so a fan-out never
  re-enters a group. Lock order: group gate, group, connection send lock. Balancers receive no broadcasts.
- Alternatives: lock-free snapshots rebuilt on change (allocates per change, large arrays); gateway sessions as ECS
  entities on a gateway shard (one more thread hop per push; kept on the roadmap).
- Consequences: join/leave O(1); a broadcast allocates nothing (allocation tests); a 3k-session fan-out holds one
  group lock for about a millisecond, which only delays membership changes of that group.

## ADR-026 Balancers and client gateway discovery
- Context: clients were given every gateway address (`DaServer__Gateways__{i}`) and spread themselves round robin,
  so adding a gateway meant reconfiguring clients and load followed connection order, not load.
- Decision: members gossip their client host, port and session count. A gateway with `Balancer` (Aspire
  `AddBalancer`) answers `PickGateway` with the less loaded of two random gateways that accept sessions (power of two
  choices: even spread from stale counts, no herd). `ClusterClient.ConnectAsync(host, port)` follows the answer, and
  asks again while the cluster is still forming (no gateway known yet) until its call timeout.
- Consequences: clients know one address; gateways scale by replica count. Any gateway can answer `PickGateway`, so
  a balancer is a deployment choice, not a separate component.

## ADR-027 Bounded inboxes refuse instead of growing
- Context: shard inboxes and actor mailboxes were unbounded: a flood (or a slow actor) grew memory until the process
  died, and callers learned nothing.
- Decision: `NodeOptions.ShardInboxCapacity` bounds a shard's inbox and `ActorMailboxCapacity` an actor's mailbox; a call
  or event beyond either is refused with `RpcStatus.Overloaded` (requests get the status, one-way messages are
  dropped and counted in `daserver.shard.rejected`).
- Consequences: memory is bounded under any load; overload is visible to callers and in metrics. Pausing a link's
  receive while its target is full (true backpressure across nodes) is on the roadmap.

- Rejected follow-up: pausing a node link's receive while a target shard is full (TCP backpressure). The link from B
  to A also carries replies to A's own calls; pausing it stalls those turns until their timeouts, which can block
  the very shard that is full. Refusing early is cheaper and visible to the caller.

## ADR-028 Request deadlines travel as a budget
- Context: a caller that timed out still had its request run (and possibly relayed further) after it gave up, so a
  backlog kept doing work nobody waited for and recovery after a spike took longer than the spike.
- Decision: a request's header status byte (unused on requests) carries the caller's remaining time in 100 ms units
  (up to 25.5 s). Every hop turns it into a local deadline on arrival (`Environment.TickCount64`, no clock sync) and
  forwards what is left; clients send their call timeout. A shard drops a request whose deadline passed before it
  runs: no reply (the caller has already failed it), counted in `daserver.shard.expired`.
- Alternatives: absolute timestamps (need synchronized clocks); a separate header field (24-byte header, one byte
  was free on requests).
- Consequences: work whose caller gave up is not run, on any node; one-way messages and events never expire. A
  request in flight longer than its budget is reported as a timeout, never run late.

## ADR-029 Group broadcasts go to every gateway
- Context: a `ClientGroup` push is sent once to every gateway, and gateways with no member of the group drop it.
  Tracking which gateways hold members would save those frames.
- Decision: keep sending to every gateway. The cost is one small frame per gateway per broadcast, independent of
  the group size, and a gateway without the group answers it with one dictionary miss. Tracking members per gateway
  needs membership gossip from gateways to every sender (sessions join groups on whichever node binds them), more
  ordering cases on gateway loss, and it only pays when there are many gateways and groups live on few of them.
- Consequences: simple and ordered with binds (ADR-022). Revisit when deployments run tens of gateways with small
  groups; the roadmap keeps the item.

## ADR-042 A node declared dead drops its actors without OnUnload
- Context: a node that learns it was declared dead (a partition, a long pause) unloaded its actors normally, so
  `OnUnload` saved their state after the cluster had already given their keys to other nodes: a stale save could
  overwrite the new owner's.
- Decision: on Dead every loaded actor is tagged `Fenced` and removed without `OnUnload`, as soon as its running turn
  ends; its mail is routed again to the new owner. The node then rejoins with a higher incarnation (as Orleans fences a
  silo that sees itself dead).
- Consequences: work since the actor's last save is lost on that node, which is the price of a partition. A turn in
  flight still finishes; stores that must reject late writes need versioned saves (future persistence helpers).

## ADR-041 One verb per target
- Context: `Send` meant four things by the type of its first argument: an actor (`Send(key, m)`), a service
  (`Send(m)`), a client session (`Send(session, m)`) and a client group (`Send(group, m)`). A call site did not say
  whether it reached servers or clients.
- Decision: `Send`/`Ask` reach actors and services, `Publish` every handler, `Push(session, m)` one client session and
  `Broadcast(group, m)` a client group. DAS006 checks `Flow.ToClient` on `Push`/`Broadcast` by name.
- Alternatives: putting the actor key inside the message (the old `[ActorKey]`, removed by ADR-035) or typed addresses
  (`Cluster.Actor(key).Send(m)`), which couple a sender to its receiver. `[NinoType, Message]` stays a pair: the Nino
  generator only recognizes its own attribute, not a derived one.
- Consequences: the target of every call is visible at the call site, and the verbs line up with `Flow`.

## ADR-040 One noun per concept
- Context: "link" meant both a node-to-node connection (`PeerLink`) and the component pointing an entity at its actor
  (`ActorLink`); "implementation" named the actor or service type handling a message; the lifecycle mixed load/unload
  (public `OnLoad`, `OnUnload`, `UnloadAfterIdle`) with activate/deactivate/retire (internal tags and docs).
- Decision: `ActorRef` is the actor component (key, client group); "link" is only a node connection. `HandlerType`
  (`HandlerTypeInfo`, `HandlerTypeId`) is an actor or service type with `[Handle]` methods. The lifecycle is load/unload
  only: `Loading` (`OnLoad` running), `UnloadRequested` (asked to leave), `Unloading` (`OnUnload` running), `Removing`
  (entity destroyed, mail routed again); a running actor is a "loaded actor", not an activation.
- Consequences: wire values are unchanged (handler type ids are the same FNV-1a of the type name). Earlier ADRs keep
  the names of their time.

## ADR-039 EasyEcs from NuGet, used as its documentation teaches
- Context: DaServer built against a sibling EasyEcs checkout, and parts of the runtime used EasyEcs against its own
  guidance: systems gated themselves with flags and ran manual queries in `OnUpdate`, samples counted ticks in
  components instead of `UpdateInterval`, an actor had two components for one identity, and an unused timer queue
  (`Shard.Schedule`, `TimerSystem`) remained.
- Decision: reference the `EasyEcs` package (4.0.1; it ships its generator and analyzer). Cadence is a system group the
  shard loop updates when due (`ShardRebalance`, `ShardIdleScan`), so `RebalanceSystem` and `IdleSystem` are plain
  `[ForEach]` methods; samples use `UpdateInterval`; `ActorLink` alone identifies an activation (`Key`, `Group`);
  the timer queue is removed.
- Consequences: no co-development checkout in builds or CI; EasyEcs changes reach DaServer through releases.

## ADR-038 `[Message]` marks messages and their flow
- Context: any `[NinoType]` struct counted as a message, so data types (a reply, a struct inside a list) were
  indistinguishable from messages, wire-id collisions were checked for data that never travels alone, and nothing
  said which messages clients send or receive except `[ClientMessage]`. A server could push an internal message to a
  client, or a client handler could expect one, and only testing would notice.
- Decision: `[NinoType, Message]` is a message; plain `[NinoType]` is data. `[Message(Flow)]` says who sends it to
  whom: `Internal` (default, servers only), `FromClient` (clients may send it; replaces `[ClientMessage]`, `AnyKey`
  moves to `[Message]`), `ToClient` (servers push it, clients handle it); flags combine. The analyzer checks handlers
  (servers never handle a `ToClient`-only message, client handlers only `ToClient` ones) and call sites
  (`Send(session|group, m)` needs `ToClient`, `ClusterClient` sends need `FromClient`, servers do not send
  `ToClient`-only messages to each other): DAS006. Gateways still accept only `FromClient` messages at run time.
- Alternatives: marker interfaces (`IClientMessage`) would allow generic constraints but were removed by ADR-018 for
  attributes; separate attributes per direction would split one concept into three names.
- Consequences: a contracts file reads as a protocol (who talks to whom); leaking server-internal messages to clients
  is a build error. Gateway-to-node traffic stays framework frames, so roles never appear in message declarations.

## ADR-037 Actor lifecycle is tags; turn ends are posted, not polled
- Context: the runtime used ECS storage but not ECS style. `TurnSystem` visited every busy actor on every wake and
  polled `IsCompleted` on its task; a ready queue plus a `Scheduled` flag duplicated what a tag expresses; a
  `TurnKind` enum and static procedures (`Unload`, `Retire`, `Schedule`) called from several systems carried the
  lifecycle instead of data; running service calls were a polled list. The sample did the same: an interest grid
  class with behavior, a `-1` sentinel cell and a `MotionChanged` flag set from three places.
- Decision: state changes are data, each system reacts to one state.
  - A `TurnWatcher` (pooled, one cached delegate) registers on the turn's task; its continuation posts it to the
    shard and the inbox tags the entity `TurnEnded`. `TurnSystem` visits only ended turns, by filter (`EndCall`,
    `EndLoad` with `Loading`, `EndUnload` with `Deactivating`); `GetResult` is read once after the end, as `await`
    does. Running service calls are `ServiceCall` entities ended the same way (`ServiceCallSystem`).
  - Lifecycle tags: `Ready` (mail waiting), `Loading`, `Unloading` (asked to leave, by `RebalanceSystem` or
    `IdleSystem`), `Deactivating`, `Retiring(status)`. `UnloadSystem` starts `OnUnload` once the actor is idle,
    `RetireSystem` destroys it and routes its mail again, `MailboxSystem` runs `Ready` actors without a turn.
  - Rebalancing marks busy actors too, so the retry loop is gone.
  - Sample: an avatar without `GridCell` is not placed yet; `SentMotion` (last sent velocity and cell) is compared
    each tick instead of a flag; leaving is `Despawning` data handled by `DespawnSystem`; the grid is `MapGrid` data
    on the map entity, its steps are systems (`PlacementSystem`, `MotionSyncSystem`, `SnapshotSystem`).
- Alternatives: a ready queue avoids the archetype move per idle-to-ready transition, but it is a second
  representation of the same state; benchmarks showed no measurable difference.
- Consequences: no system scans busy actors and nothing polls tasks; every lifecycle step is a filter that tests and
  users can read. Async turns cost a continuation post (no allocation). Benchmarks before and after are in the README.

## ADR-036 Clients resume sessions with signed tickets
- Context: the game synchronizes state from servers to clients. A network blip, a slow client disconnected at its
  send bound (ADR-035) or a gateway restart closed the session: actors removed the avatar and the player logged in
  again, and players near it saw it vanish and reappear.
- Decision: the session's gateway sends its client a reconnect ticket after every binding change: session, bindings
  (implementation id, key) and expiry, signed with HMAC-SHA256 of the cluster secret (`DaServer__ClusterSecret`,
  generated by Aspire). Gateways keep no state for it, so any gateway accepts it. `ClusterClient` reconnects through
  its original address (a balancer picks a live gateway) with exponential backoff and presents the ticket; the new
  session takes over the bindings and the actors' groups, and every bound actor receives
  `SessionResync(Session, Previous)` before the client's next message (same links). Actors swap the session and send
  the full state; pushes sent meanwhile are lost by design (state, not a log). A lost connection is reported as
  `SessionClosed` only after `ReconnectGrace` (30 s), also for a crashed gateway; the runtime drops the late
  `SessionClosed` of a resumed session. An explicit close (client dispose, `Disconnect`) is reported at once. Clients
  refresh their ticket at half its lifetime (10 min).
- Alternatives: resumable client streams like node links (ADR-008) keep per-session replay buffers on gateways, which
  is memory per connection and lost with the gateway anyway; a session store (Redis) adds a dependency and a hop.
- Consequences: no cluster without a secret issues tickets (behavior as before). A ticket is a bearer token for its
  lifetime, carried on the same connection as the login. Groups joined by `AnyKey` messages are rejoined by the client
  (`client.Reconnected`).

## ADR-035 Messages instead of contracts
- Context: contracts were interfaces with attributes (`[Service]`, `[Actor]`, `[FromClient]`, `[ToClient]`), events
  were structs with an `[ActorKey]` member, and every call went through a generated proxy (`Cluster.Player(42).AddGold(1)`).
  Three ways to say "send this data somewhere" meant three sets of generated code, analyzer rules (DAS001-DAS014) and
  concepts to learn, and a sender had to know the receiver's interface. Splitting one actor's audience (servers vs.
  clients) needed per-method attributes; moving a method between actors broke every caller.
- Decision: one concept, the message: a `[NinoType]` struct, answered when it implements `IRequest<TReply>`.
  - Handlers are `[Handle]` methods of actors and services (and of any class on clients, for pushes). The message type
    decides the handler: the cluster gossips `(message id, implementation id, actor)` per handler.
  - `Ask(key, m)` / `Send(key, m)` go to the one actor type handling `M`; without a key, to the one service handling
    it (more than one handler of the requested kind is an `Ambiguous` error). `Publish(key, e)` / `Publish(e)` reach
    every handler once. The key is a parameter, so `[ActorKey]` and `IKeyed` are gone.
  - `[ClientMessage]` marks what clients may send (`AnyKey = true`: the client chooses the actor key); otherwise the
    gateway uses the key bound to the session for that actor type. Bindings are keyed by implementation id, and the
    actor binds itself: `await BindSession(session)` (acknowledged by the gateway before it returns).
  - Pushes are messages too: `Send(session, m)` / `Send(group, m)`, handled on clients by `[Handle]` methods of a
    registered object.
  - `MessageGenerator` emits typed `Ask` overloads per request (the reply type is inferred at the call site);
    `HostGenerator` emits one dispatcher per handler. Analyzer rules shrink to DAS001-DAS005.
  - Wire: the header field is `messageId` (FNV-1a of the message type's full name or `[WireId]`), the `Event` frame
    kind became `Message` (one-way and published), and requests carry a `ToActor` flag.
- Alternatives: keeping interfaces with a message-shaped escape hatch would have left two models; making the receiver
  explicit (`Ask<Player>(...)`) couples senders to implementations again.
- Consequences: senders depend on message types only, so handlers move between actors, services and projects without
  touching callers. Hot paths are unchanged: messages are `in` structs of generic methods, serialized once into pooled
  frames (allocation tests unchanged). A client that cannot drain its pushes (send backlog over 256 KB) is
  disconnected rather than throttled; it reconnects and the server resends full state, which is cheaper and simpler
  than per-session drop policies in a state-synchronized game.

## ADR-034 One write per connection per batch
- Context: a connection's send loop woke on the first queued message, so a session in the 9 interest cells around a
  player got up to 9 writes per update. Profiling the 10k-in-one-map scenario put a third of gateway time in send
  loops (socket writes), against 4% in the fan-out copy.
- Decision: `Connection.BeginSendBatch` (thread-local, nestable) defers send-loop wakes to the end of the batch. The
  receive loop batches the frames of one receive buffer; a shard batches one wake (messages and tick).
- Alternatives: a timed flush (adds latency to every message); shared reference-counted payloads with gather writes
  (saves the copy, which profiling showed is not the cost).
- Consequences: one write per connection per batch, no added latency beyond the batch itself, no allocation. 10k in
  one map: gateway CPU 13-14% -> 11-12%, kernel time 30% -> 20%, p99 139 -> 106 ms.

## ADR-033 Actors unload when idle, by their own declaration
- Context: actors stayed loaded until their key moved, so actors used now and then (offline players, finished
  matches) held memory forever.
- Decision: `Actor.UnloadAfterIdle` (default zero: never). Actors that set it get an `UnloadWhenIdle` component; an
  `IdleSystem` scans only those, ten times a second, and unloads the ones without mail for that long through the same
  path as rebalancing (`OnUnload` as the last turn; mail arriving meanwhile loads the actor again). The mailbox records
  when the actor last took mail.
- Alternatives: a node-wide timeout (would unload maps and realms that are busy in systems but receive no mail).
- Consequences: one store per ready actor on the mailbox path; RPC benchmark unchanged.

## ADR-032 Names from the distributed-server vocabulary
- Context: several public names were borrowed from one framework (Orleans' `OnActivateAsync`) or described a mechanism
  instead of its role (`[Client]` read like "the client contract", the opposite of `[Push]`; "dispatch" also meant
  RPC dispatch; `MemberInfo` clashed with `System.Reflection.MemberInfo`; `Kick` is one game's word for a disconnect).
- Decision: name things by their role in a cluster of game servers, in pairs where they are pairs:

  | Before | After | Why |
  |---|---|---|
  | `[Client]` / `[Push]` | `[FromClient]` / `[ToClient]` | the two directions across the gateway |
  | `ClientAccess` (`SessionKey`) | `ClientRoute` (`BoundKey`) | how a client call is routed |
  | `[Key]` (events) | `[ActorKey]` | the key of the actor that receives the event |
  | `OnActivateAsync` / `OnDeactivateAsync` | `OnLoad` / `OnUnload` | the actor is loaded on / leaves this node |
  | `Caller` | `CallerSession` | it is the client session, invalid for server calls |
  | `Kick` | `Disconnect` | not game-specific |
  | dispatch gateway, `AddDispatch`, `DispatchOnly` | balancer, `AddBalancer`, `Balancer` | its role; "dispatch" stays RPC dispatch |
  | `MemberInfo` | `ClusterMember` | no clash with reflection |
  | `NodeOptions.ClusterBind` / `MailboxCapacity` | `ListenAddress` / `ShardInboxCapacity` | what they configure |

  Two simplifications came with the review: actor state starts as `new TState()` (field initializers of a state
  struct run, so `OnLoad` is only for loading), and `ActorIdentity` is a public component, so systems read an actor's
  `Key` and `Group` instead of copying them into state.
- Consequences: wire format and wire ids are unchanged (contract and method names are hashed, not attribute names).
  Earlier records below use the new names.

## ADR-031 Gossip carries pulses, cards travel once
- Context: every gossip round serialized the whole member table and every receiver deserialized new `ClusterMember`
  objects: about 10 KB/s per idle node at one round a second, growing with the square of the cluster size. The view
  also kept the load hints of the last status change, so balancers never saw gateways fill up.
- Decision: a member is its card (identity, endpoints, hosted contracts: fixed while it runs) plus its pulse
  (incarnation, heartbeat, status, load, sessions: 29 bytes). Gossip and its reply carry pulses, written into a pooled
  frame and read in place; a pulse naming an unknown node is answered with `CardRequest`/`Cards`. Pulses update
  members in place; incarnation and status changes publish a copy and rebuild the view. Because counts lag a round,
  the balancer adds the clients it directed to a gateway since its last pulse.
- Consequences: a steady-state round allocates nothing (allocation test at 300 rounds/s); idle nodes in the 10k
  topology allocate 0 KB/s; gossip size is 29 bytes per member. Load hints are fresh within a round.

## ADR-030 One Aspire resource per replica
- Context: Aspire's `WithReplicas` runs replicas as one resource behind a proxy with shared endpoints. Cluster
  members are addressed individually: each gossips its own cluster and client endpoint, links go to a specific node
  and clients are directed to a specific gateway.
- Decision: `AddNodes` / `AddGateways` / `AddBalancer` create one resource per replica (`gateway-0`, `gateway-1`, ...)
  with its own endpoints, and every node gets every other node as a seed; the balancer is the only well-known client
  address. Deployments that do have per-replica DNS (Kubernetes headless services) use DNS seeds instead: a seed that
  resolves to several addresses yields one seed per address.
- Consequences: replicas are visible and individually addressable on the dashboard; replica count is still a single
  argument, and no endpoints are configured by hand.
