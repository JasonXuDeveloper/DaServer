# Architecture decision records

Short records of non-obvious decisions, each stating the decision that holds now. Numbers are stable (gaps are
records that no longer hold). Each: context → decision → consequences.

## ADR-001 The server runs on the newest .NET; shared code also builds for netstandard2.1
- Context: the hot paths need the newest runtime and its APIs, while contracts and the client SDK must run in Unity
  (netstandard2.1) and on the previous .NET.
- Decision: the server runtime (`DaServer`, `DaServer.Hosting`, `DaServer.Aspire`) targets one framework, the newest
  .NET (ADR-054); what clients share (`DaServer.Abstractions`, `DaServer.Core`, `DaServer.Client`, ADR-052) also
  builds for netstandard2.1 and the previous .NET, with the newer APIs behind `Platform` and internal polyfills.
- Consequences: the server uses the newest APIs freely (`Lock`, frozen collections, POH, C# latest); shared code
  pays a few `#if NET` lines, never a second implementation.

## ADR-002 Shards: single-threaded shards with a dispatch queue, not a thread-pool actor runtime
- Context: a thread-pool actor runtime (as in a studied game server) resumes continuations on pool threads and funnels many
  producers into queues built for one, which needs locks or careful ownership in user code.
- Decision: each node runs N shards (default one per core), each a dedicated thread owning an EasyEcs `World`, a
  bounded lock-free MPMC dispatch queue for new calls and messages (refused beyond its capacity, ADR-027), a second
  one for completions, callbacks and turn ends (never refused, drained first) and a fixed tick. Actors and service instances live in exactly one shard.
- Consequences: no locks in user code; deterministic ordering per shard; messages are processed immediately (tick rate
  only drives systems). ECS systems are synchronous (EasyEcs 4); a handler that awaits suspends only its actor
  (a `Turn`), and the shard keeps running other actors and systems (ADR-037).

## ADR-003 Completing RPCs on the caller's shard (no SynchronizationContext.Post)
- Context: `ManualResetValueTaskSourceCore` with a captured SynchronizationContext allocates a Tuple per Post.
- Decision: `PendingCall<T>` is a custom pooled `IValueTaskSource` that records the caller's shard; responses are
  posted to that shard's dispatch queue and completed there, running the continuation inline. A
  `ShardSynchronizationContext` is still installed so foreign awaits (DB drivers, Task.Delay) marshal back to the shard
  thread.
- Consequences: zero allocation per awaited RPC; ExecutionContext/AsyncLocal does not flow across RPC awaits.

## ADR-005 Wire format
- Decision: fixed 24-byte little-endian header (length, kind, status, hops, flags, target, message id, key), the
  Nino-serialized message, then trailers named by the flags. Three kinds: `Message` (a request when it carries a call
  id), `Reply` and `Control` (link protocol). The target is always set: the handler type id of an actor or service,
  or an address scope (sessions, client groups), so a receiver routes a frame with one lookup. The call id is a
  4-byte trailer (`HasCall`): the payload is serialized before routing picks the call id, and every hop that relays a
  request rewrites it in place, so neither needs to move the payload. Message id = FNV-1a 32 of the message type's
  full name (or its `[WireId]`), computed once per type at run time and by the analyzer at build time; handler type
  ids are FNV-1a of the actor id type or service type name.
- Consequences: client-facing nodes route by patching header fields without parsing payloads; collisions are build errors
  (DAS004) and registration errors, resolvable with `[WireId]`. A request costs the same bytes as a one-way message
  plus its call id; a reply carries the call id as a trailer too (4 bytes).

## ADR-006 Connection send path: copy into a per-connection batch
- Decision: `Connection.Send` copies the frame into one pending batch buffer under a short lock; one send loop swaps
  and writes whole batches. Broadcast serializes once, then memcpy per recipient.
- Consequences: N small frames = 1 syscall; no per-frame objects; per-connection backlog bounded by
  `MaxPendingSendBytes` (slow consumers are disconnected; node links then resume, see ADR-008).

## ADR-007 Placement by rendezvous hashing + forwarding
- Decision: actor owner = highest-random-weight node among the members hosting the actor type (Up and Suspect,
  ADR-023). A node loads an actor only if it is the owner in its own view, otherwise forwards (hop limit 3) or answers
  `NotHosted`. On view changes shards unload actors they no longer own.
- Consequences: no central directory, O(nodes) placement computation; brief disagreement during membership changes is
  handled by forwarding/retry rather than a lease protocol (documented limitation).

## ADR-008 Reliability via byte-offset resumable links
- Context: without resumption, a dropped node connection fails every call in flight and silently loses one-way
  messages, pushes and session control queued on it; a network blip between a game node and a client-facing node looks like a
  crash.
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
  because frames are whole in every batch. Client connections are not resumed byte by byte: a client resumes its
  session with a ticket instead (ADR-036), so client-facing nodes keep no replay buffer per session.
- Consequences: a transient disconnect is a delay: no loss, no duplicate (`LinkTests` drop connections mid-stream).
  Cost: one memcpy per batch on the send loop and one type check per received frame; BenchmarkDotNet RPC numbers are
  unchanged within noise. A crashed peer fails calls when it is declared dead (or at their timeout), not at the
  first socket error.

## ADR-009 Hosting on the Generic Host
- Decision: `DaServer.Hosting` runs a node on the .NET Generic Host (configuration, ILogger, SIGTERM → graceful
  leave), with `System.Diagnostics.Metrics` + OpenTelemetry (OTLP when configured) and a periodic stats line.
  Deployment is ADR-015.

## ADR-010 Benchmarks and tests
- Decision: BenchmarkDotNet only, over real loopback sockets, modelling real load (thousands of sessions, broadcast
  fan-out, concurrent callers, cross-shard traffic). Integration tests run multi-node clusters in-process. Allocation
  tests assert < 8 B per operation (< 1 B per fan-out delivery) in steady-state loops.

## ADR-011 Naming: Shard (DaServer executor) vs World (EasyEcs)
- Context: EasyEcs uses standard ECS vocabulary (`World`, `Query`, `EcsSystem.OnUpdate`).
- Decision: DaServer's single-threaded executor is a `Shard`; each shard owns one EasyEcs `World`. Actors and services
  expose `World` and `Cluster` (`ICluster`); ECS code reaches the cluster through the `ShardContext` singleton
  (`world.Cluster()`).
- Consequences: no name clash; "world" always means the ECS world.

## ADR-012 Source generation instead of registration code
- Decision: each generator writes one kind of code. `AskGenerator`: a `{Request}Ask` class of typed `Ask` overloads
  per request. `RegistrationGenerator`: one `DaServer.Generated.Registration_{Assembly}` class per assembly whose
  static constructor runs the registrations of referenced assemblies, then registers the assembly's `[FromClient]`
  messages, client handler classes and, with the server runtime, its actors and services (one dispatcher per
  `[Handle]` method) and EasyEcs systems; `[assembly: GeneratedRegistration(typeof(..))]` names it.
  `EntryPointGenerator`: the node entry point (ADR-021). `PolyfillGenerator`: `IsExternalInit` for contract
  assemblies built for netstandard2.1.
- Registration runs in a static constructor because that runs exactly once on every runtime, Unity's IL2CPP included:
  `Registrations.Run(assembly)` triggers it (the client SDK does on `Register(handler)`, and every registration
  triggers those of the assemblies it references). On .NET the class also triggers itself from a module initializer
  when its assembly loads, so a server needs no call; targets without module initializers (netstandard2.1) get none.
- Consequences: no reflection, no configuration strings, no hand-maintained lists; misuse is a build error
  (DAS001-DAS011, ADR-016).

## ADR-013 The shard runtime is ECS
- Context: an imperative shard loop (drain a queue, sweep in-flight work, run timers, tick) with ad-hoc per-actor
  queues hides state in procedures. a studied game server's useful idea: actors are entities with their own mailbox, drained by
  systems; infrastructure is systems over singleton state.
- Decision: `Shard` keeps only the thread, the cross-thread dispatch queue and the wake/tick loop. Loaded actors are
  entities (`ActorRef`, `Mailbox`, `Turn` and lifecycle tags, ADR-037); directories and pooled watchers are
  singletons. `DispatchSystem`, `TurnSystem`, `MailboxSystem`, `ServiceCallSystem`, `UnloadSystem` and
  `RemovalSystem` run in the `ShardMessages` system group on every wake, `RebalanceSystem` in `ShardRebalance`,
  `IdleSystem` in `ShardIdleScan` (ADR-039); user systems run in the default group at the tick rate.
- Consequences: the runtime obeys the ECS rules it asks users to follow. Per message: one dictionary lookup, one
  queue push, the handler. Every system visits only the entities in the state it handles, so no system scans all
  actors per wake. Measured: zero allocation per cross-shard call.

## ADR-014 Turn-based actors
- Context: handlers that interleave at awaits (reentrant) let actor state change between an await and the code after
  it, unlike the actor model most developers expect.
- Decision: one message at a time per actor, including its awaits (`Turn` component). Services stay concurrent
  (stateless).
- Consequences: no interleaving bugs in actor state. Call cycles (A awaits B awaits A) deadlock until the call timeout
  (roadmap N3 makes them fail fast); use one-way messages for such flows. A `[Reentrant]` opt-in can be added if a
  real need appears.

## ADR-015 Orchestration with Aspire
- Context: hand-written Dockerfile, compose and Kubernetes files duplicate the topology (ports, seeds, projects) and
  drift from the code.
- Decision: `DaServer.Aspire` describes a cluster in the AppHost (`AddDaServerCluster`, `AddNodes`, `WithClientConnections`,
  `WithClientEndpoints`). The same model runs locally with the dashboard and publishes Docker Compose or
  Kubernetes through Aspire publishers. Nodes export logs and metrics over OTLP when Aspire (or any collector)
  configures it. There are no hand-written deployment files.
- Consequences: one source of truth for topology. Ports are assigned deterministically per node. Replicas are
  separate resources because cluster members are addressed individually, not through a load balancer (ADR-030).

## ADR-016 Analyzer separate from generators
- Context: generators that report diagnostics carry symbols and locations in their models, which defeats caching.
- Decision: generators use syntax-only predicates and equatable, symbol-free models with tracking names, and report
  nothing. `DaServerAnalyzer` reports DAS001–DAS011 (collisions in a compilation-end action), and reads symbols
  through the same `Shapes` code as the generators.
- Consequences: edits that do not touch messages or handlers hit the cache (tested); "skipped by the generator" and
  "reported by the analyzer" cannot disagree.

## ADR-017 No roles: the deployment unit is a project
- Context: role strings on implementations and in configuration repeat the topology in code, configuration and the
  AppHost, and a typo silently hosts nothing.
- Decision: a node hosts every actor, service and system compiled into its process. Splitting the cluster means
  splitting projects (`Sample.LoginServer`, `Sample.GameServer`, `Sample.MapServer`, `Sample.GatewayServer`); the AppHost
  lists projects and replica counts. Accepting clients is a capability (the `DaServer:Clients` section, `WithClientConnections`), not a role.
  In-process tests narrow hosting with `NodeOptions.Hosts`, a type filter.
- Consequences: no topology strings anywhere (checked by the release checklist). The compiler decides what a node can
  host; references between projects decide what can be split.

## ADR-019 Messages are delivered through mailboxes, at most once
- Context: an in-world event bus is not an ECS concern (state changes are data) and cannot cross shards or nodes;
  actors and services anywhere in the cluster need one-way messages and published messages.
- Decision: handlers are part of the gossiped member card. One-way messages (`Send`) and published messages
  (`Publish`) travel as `Message` frames: a message to an actor goes to the owner of its id and enters the actor's
  mailbox (turn-based, loaded on demand); `Publish` serializes once and sends one frame per service type handling the
  message, local node first. Delivery is at most once and ordered with the calls of the same sender.
  `SessionClosed` is a framework message sent to every actor a closing session was bound or granted to.
- Alternatives: a broker (Kafka, NATS) on the call path adds a hop and an external dependency to every message;
  subscription state per actor would have to survive rebalancing. Durable events are an application concern (outbox).
- Consequences: publishers never depend on handlers; a one-way message costs the same as a call without a reply and
  allocates nothing.

## ADR-020 Business code sees ICluster, not Node
- Context: exposing `Node` gives business code lifecycle, membership and internals.
- Decision: `ICluster` (`Send`/`Ask` of `IMessageSender` over addresses, `NodeId`, `Publish`); session operations are
  messages to the session (ADR-047) and binding is an actor operation (`BindSession`). Actors get a `Group` of their
  own (`ClientGroup.Of(handler type id, id)`), so group ids of different features cannot collide.
- Consequences: the surface business code can touch is small and testable; runtime refactors do not break it.

## ADR-021 Generated node entry point
- Context: every node project would carry the same three-line `Program.cs`.
- Decision: `EntryPointGenerator` emits `DaServer.Generated.NodeProgram.Main` (WebApplication + `AddDaServer`) for an
  executable that references `DaServer.Hosting` and declares no entry point. Writing a `Program.cs` opts out.
- Consequences: a node project is business classes only; names, ports and seeds come from the AppHost (environment).

## ADR-022 One ordered stream per node direction
- Context: if a node answers requests on the accepted connection while its own binds, pushes and messages to the
  caller travel on its outgoing link, a reply can overtake a `Bind` sent before it (the client calls the actor before
  the client-facing node knows the binding).
- Decision: a node names itself with a `Hello` control frame on every link it opens; the receiver then sends replies
  to that node through its own outgoing link to it (`PeerLink` is the reply sink). Everything node A sends to node B,
  replies included, is one ordered TCP stream.
- Consequences: "the binding is in place when the call that made it returns" holds (tested with a push burst before
  the bind). A dropped connection resumes the stream (ADR-008).

## ADR-023 Placement keeps keys on suspect members
- Context: placement over Up members only would move every key of a node that misses a few gossip rounds (GC pause,
  network blip) and move them back when it recovers: duplicate actors and lost state on every flap.
- Decision: actor placement ranks Up and Suspect members; services and published messages use Up members only. When
  a member is declared Dead its keys move to the others, and the node itself, once it learns it, removes its loaded
  actors before rejoining (ADR-042). Mail for a loaded actor, including one being unloaded, enters its mailbox, so
  order holds across a move.
- Consequences: a slow node keeps its keys (calls to it wait or time out instead of loading the actor twice). A
  partitioned node can still run its actors until it is declared dead.

## ADR-024 The runtime owns client sessions
- Context: business code should not pair binding with group joins, remember sessions to clean up, or copy the caller
  session before the first await; and a crashed client-facing node must not leave actors believing their sessions are alive.
- Decision: `BindSession(session)` also puts the session in the actor's `Group`; `UnbindSession` and close leave it.
  Every loaded actor records the sessions bound to it (a `ClientSessions` component); when a session closes the
  client-facing node publishes `SessionClosed` to each, and when a client-facing node is declared dead every node closes that client-facing node's
  sessions for its own actors. `Caller` (the origin, ADR-047) is the session of the running turn and holds across its
  awaits.
- Consequences: business code binds and handles `SessionClosed`; nothing else. Client-facing node crash tests cover it.
  Session control and pushes go to the client-facing node's link from the view, or, while gossip has not listed the client-facing node
  yet, from its Hello (a client call can reach a node first): a Bind is never dropped while the cluster forms.

## ADR-025 Client groups: slot arrays under a per-group lock
- Context: a group of thousands of sessions must take joins and leaves in O(1) and fan out without a snapshot
  allocation per broadcast.
- Decision: each group is a slot array; a session knows its slot in each group, leave is swap-remove. Membership
  changes and fan-out both take the group's lock (fan-out reaches every earlier join). `ClientGroup.All` is a group
  every session joins on accept. `Connection.Send` closes a slow consumer off the caller's thread, so a fan-out never
  re-enters a group. Lock order: group gate, group, connection send lock.
- Alternatives: copy-on-write member arrays (O(n) per join, O(n^2) to fill a group, large array churn); a
  `ConcurrentDictionary.Values` snapshot per broadcast (8 B per session per broadcast); client-facing node sessions as ECS
  entities on a client-facing node shard (one more thread hop per push; kept on the roadmap).
- Consequences: join/leave O(1); a broadcast allocates nothing (allocation tests); a 3k-session fan-out holds one
  group lock for about a millisecond, which only delays membership changes of that group.

## ADR-026 Client-facing nodes direct clients to each other
- Context: clients that are given every client-facing node address and spread themselves round robin must be reconfigured when
  a client-facing node is added, and load follows connection order, not load; a dedicated directing role is one more thing to
  deploy, and the platform already offers the well-known address (a load balancer, a DNS name).
- Decision: client-facing nodes publish their client endpoint (a card entry) and gossip their session count (a pulse
  slot). Any of them answers `PickNode` (and `GET /clients/endpoint`) with the less loaded of two random client-facing
  nodes (power of two choices: even spread from stale counts, no herd), adding the clients it directed since the
  node's last count, or "stay" when that is itself. `ClusterClient.ConnectAsync` connects through one or several
  entry addresses (tried in turn; reconnects use them again) and moves to the named node; a node it cannot reach is
  not fatal (it stays). With `ClientConnectionOptions.Redirect` off (nodes behind a load balancer without addresses of their
  own) the answer is always "stay".
- Consequences: clients know one address; client-facing nodes scale by replica count; there is no balancer role. A
  load balancer in front spreads the first connection, the directory the session.

## ADR-027 Bounded queues refuse instead of growing
- Context: unbounded dispatch queues and mailboxes let a flood (or a slow actor) grow memory until the process dies,
  and callers learn nothing.
- Decision: `NodeOptions.DispatchQueueCapacity` bounds the new calls and messages in a shard's dispatch queue and
  `ActorMailboxCapacity` an actor's mailbox; a call or message beyond either is refused with `RpcStatus.Overloaded`
  (requests get the status, one-way messages are dropped and counted in `daserver.shard.rejected`). Node links are
  never paused while a target shard is full: the link from B to A also carries replies to A's own calls, so pausing it
  stalls those turns until their timeouts, which can block the very shard that is full. Replies, continuations and
  turn ends finish work already admitted, so they have a queue of their own (16k slots per shard, drained first):
  sharing the full queue with new work would push them into an unbounded overflow exactly under overload. They are
  bounded by the calls and turns in flight; a burst beyond the slots spills into a segment queue once.
- Consequences: memory is bounded under any load; overload is visible to callers and in metrics, and refusing early
  is cheaper than TCP backpressure. Refusals are status-only (ADR-049).

## ADR-028 Request deadlines travel as a budget
- Context: a caller that timed out may still have its request run (and relayed further) after it gave up, so a
  backlog keeps doing work nobody waits for and recovery after a spike takes longer than the spike.
- Decision: a request's header status byte (unused on requests) carries the caller's remaining time in 100 ms units
  (up to 25.5 s). Every hop turns it into a local deadline on arrival (`Environment.TickCount64`, no clock sync) and
  forwards what is left; clients send their call timeout. A shard drops a request whose deadline passed before it
  runs: no reply (the caller has already failed it), counted in `daserver.shard.expired`.
- Alternatives: absolute timestamps (need synchronized clocks); a separate header field (24-byte header, one byte
  was free on requests).
- Consequences: work whose caller gave up is not run, on any node; one-way messages never expire. A request in
  flight longer than its budget is reported as a timeout, never run late.

## ADR-029 Group broadcasts go to every client-facing node
- Context: a `ClientGroup` push is sent once to every client-facing node, and client-facing nodes with no member of the group drop it.
  Tracking which client-facing nodes hold members would save those frames.
- Decision: keep sending to every client-facing node. The cost is one small frame per client-facing node per broadcast, independent of
  the group size, and a client-facing node without the group answers it with one dictionary miss. Tracking members per client-facing node
  needs membership gossip from client-facing nodes to every sender (sessions join groups on whichever node binds them), more
  ordering cases on client-facing node loss, and it only pays when there are many client-facing nodes and groups live on few of them.
- Consequences: simple and ordered with binds (ADR-022). Revisit when deployments run tens of client-facing nodes with small
  groups; the roadmap keeps the item.

## ADR-030 One Aspire resource per replica
- Context: Aspire's `WithReplicas` runs replicas as one resource behind a proxy with shared endpoints. Cluster
  members are addressed individually: each gossips its own cluster and client endpoint, links go to a specific node
  and clients are directed to a specific client-facing node.
- Decision: `AddNodes` creates one resource per replica (`gateway-0`, `gateway-1`, ...) with its own
  endpoints, and every node gets every other node as a seed; clients get the client-facing nodes' addresses as entries. Deployments that do have per-replica DNS (Kubernetes headless services) use DNS seeds instead: a seed that
  resolves to several addresses yields one seed per address.
- Consequences: replicas are visible and individually addressable on the dashboard; replica count is still a single
  argument, and no endpoints are configured by hand.

## ADR-031 Gossip carries pulses, cards travel once
- Context: serializing the whole member table every round costs about 10 KB/s per idle node at one round a second,
  growing with the square of the cluster size, and allocates new member objects on every receiver; a view that keeps
  the load hints of the last status change never shows client-facing nodes filling up.
- Decision: a member is its card (identity, endpoint, hosted handlers and address scopes, its features' card
  entries: fixed while it runs) plus its pulse (incarnation, heartbeat, status, load and three feature slots such as
  sessions: 37 bytes). Gossip and its reply carry pulses, written into a pooled frame and read in place; a pulse naming
  an unknown node is answered with `CardRequest`/`Cards`. Pulses update members in place; incarnation and status
  changes publish a copy and rebuild the view. Because counts lag a round, the directory adds the clients it directed
  to a node since its last pulse.
- Consequences: a steady-state round allocates nothing (allocation test at 300 rounds/s); idle nodes in the 10k
  topology allocate 0 KB/s; gossip size is 37 bytes per member. Load hints are fresh within a round.

## ADR-032 Names from the distributed-server vocabulary
- Context: names borrowed from one framework (Orleans' `OnActivateAsync`) or describing a mechanism instead of its
  role read wrong to people who know other distributed servers.
- Decision: name things by their role in a cluster, in pairs where they are pairs: `OnLoad` / `OnUnload` (the actor is
  loaded on / leaves this node), `[FromClient]` / `[ToClient]` (the two directions across the client-facing node),
  `Caller` (who sent the message from outside; invalid for server calls), `Disconnect` (not a game's "kick"), directory
  ("dispatch" means message dispatch only), `ClusterMember` (no clash with
  `System.Reflection.MemberInfo`), `ListenAddress` / `DispatchQueueCapacity` (what they configure). Actor state starts
  as `new TState()` (field initializers of a state struct run, so `OnLoad` is only for loading), and systems read an
  actor's id and group from its public `ActorRef` component instead of copying them into state.
- Consequences: wire ids do not depend on these names (message and handler type names are hashed).

## ADR-033 Actors unload when idle, by their own declaration
- Context: actors that stay loaded until their key moves make actors used now and then (offline players, finished
  matches) hold memory forever.
- Decision: `Actor.UnloadAfterIdle` (default zero: never). Actors that set it get an `UnloadWhenIdle` component; an
  `IdleSystem` scans only those, ten times a second, and unloads the ones without mail for that long through the same
  path as rebalancing (`OnUnload` as the last turn; mail arriving meanwhile loads the actor again). The mailbox records
  when the actor last took mail.
- Alternatives: a node-wide timeout (would unload maps and realms that are busy in systems but receive no mail).
- Consequences: one store per ready actor on the mailbox path; RPC benchmark unchanged.

## ADR-034 One write per connection per batch
- Context: a send loop that wakes on the first queued message gives a session in the 9 interest cells around a
  player up to 9 writes per update. Profiling the 10k-in-one-map scenario put a third of gateway time in send loops
  (socket writes), against 4% in the fan-out copy.
- Decision: `Connection.BeginSendBatch` (thread-local, nestable) defers send-loop wakes to the end of the batch. The
  receive loop batches the frames of one receive buffer; a shard batches one wake (messages and tick).
- Alternatives:
  - A timed flush per client connection, as a studied game server and ET flush once per logic frame. It was measured on 10k
    lean bots in one map with a 2 ms beat for one-way pushes (replies at once). Gateway `sendto` calls fell 42% and bot
    CPU fell from 213% to 176%, but gateway CPU rose from 45% to 58% per node: one beat thread waking thousands of
    send loops costs more thread-pool wake-ups (futex calls doubled) than the writes it saves. It also adds latency to
    every push. A shard already flushes once per tick, so each session gets about one write per map tick.
  - Reading what the kernel already holds on a link before flushing: no change in writes or CPU (the chunks of a
    tick are not queued yet when the first one is parsed).
  - Shared reference-counted payloads with gather writes: they save the copy, which profiling showed is not the cost.
- Consequences: one write per connection per batch, no added latency beyond the batch itself, no allocation. 10k in
  one map: gateway CPU 13-14% -> 11-12%, kernel time 30% -> 20%, p99 139 -> 106 ms.

## ADR-035 Messages instead of contracts
- Context: interface contracts with generated proxies (`Cluster.Player(42).AddGold(1)`) plus separate event and push
  mechanisms mean three ways to say "send this data somewhere", three sets of generated code and analyzer rules, and
  senders that depend on the receiver's interface; moving a method between actors breaks every caller.
- Decision: one concept, the message: a `[NinoType, Message]` struct (ADR-038), answered when it implements
  `IRequest<TReply>`.
  - Handlers are `[Handle]` methods of actors and services (and of any class on clients, for pushes). The message type
    decides the handler: the cluster gossips `(message id, handler type id, actor)` per handler.
  - `Ask(id, m)` / `Send(id, m)` go to the actor `id` (its id type names the actor type, ADR-043); without an id, to
    the one service handling the message (several service handlers is an error: `RpcStatus.Error`, cold path). `Publish(e)` reaches
    every service type handling it once.
  - A `[FromClient]` message to an actor goes to the actor bound to the client's session for that actor type, or to
    the actor the client names when that actor granted the session (ADR-051); the actor binds or grants itself:
    `await BindSession(session)`, `await GrantSession(session)` (acknowledged by the client-facing node before they return).
  - Messages to clients are sends to their address: `Send(session, m)` / `Send(group, m)`, handled on clients by
    `[Handle]` methods of a registered object.
  - Generated code: ADR-012.
- Alternatives: keeping interfaces with a message-shaped escape hatch would leave two models; making the receiver's
  type explicit (`Ask<Player>(...)`) couples senders to implementations.
- Consequences: senders depend on message (and id) types only, so handlers move between actors, services and projects
  without touching callers. Hot paths: messages are `in` structs of generic methods, serialized once into pooled
  frames. A client that cannot drain its pushes (send backlog over 256 KB) is disconnected rather than throttled; it
  reconnects and the server resends full state, which is cheaper and simpler than per-session drop policies in a
  state-synchronized game. Pushes are `Send` to session and group addresses (ADR-047).

## ADR-036 Clients resume sessions with signed tickets
- Context: a network blip, a slow client disconnected at its send bound (ADR-035) or a client-facing node restart closes the
  session: without resumption, actors remove the avatar, the player logs in again, and players near it see it vanish
  and reappear.
- Decision: the session's client-facing node sends its client a resume ticket after every grant change: session, grants
  (handler type id, key, bound) and expiry, signed with HMAC-SHA256 of the signing secret (`DaServer__SigningSecret`,
  generated by Aspire). Client-facing nodes keep no state for it, so any client-facing node accepts it. `ClusterClient` reconnects through
  its entry addresses (a live node directs it) with exponential backoff and presents the ticket. The ticket only
  names what the session held: the node asks every actor in it (`SessionResumed(Session, Previous)`, answered by the
  runtime before the actor's handler runs), and an actor accepts only while it holds `Previous` bound or granted
  (actors keep that set with their sessions). So a ticket resumes at most once, a revoked grant or a closed session
  (past the grace) does not come back, and an unloaded actor (which forgot its sessions) refuses without loading. The
  new session takes over what was accepted and the actors' groups, then the client gets its reply, so its next
  message reaches actors that know the new session; a ticket nothing accepts is refused. Actors swap the session and send
  the full state; pushes sent meanwhile are lost by design (state, not a log). A lost connection is reported as
  `SessionClosed` only after `ResumeGrace` (30 s), also for a crashed client-facing node; the runtime drops the late
  `SessionClosed` of a resumed session. An explicit close (client dispose, `Disconnect`) is reported at once. Tickets
  live `ClientConnectionOptions.TicketLifetime` (10 min); clients refresh theirs at half of it.
- Alternatives: resumable client streams like node links (ADR-008) keep per-session replay buffers on client-facing nodes, which
  is memory per connection and lost with the client-facing node anyway; a session store (Redis) adds a dependency and a hop.
- Consequences: without a signing secret there are no tickets and a lost connection closes the session at once. A
  ticket is a bearer token for one resume within its lifetime, carried on the same connection as the login; a stolen
  ticket resumes a session the client still holds (transport security is the platform's, TLS). The ticket carries the
  session's grants and bindings (ADR-051) and their actors' groups; other groups are rejoined by the actors on
  `SessionResumed`.

## ADR-037 Actor lifecycle is tags; turn ends are posted, not polled
- Context: polling every busy actor's task on every wake, ready queues with `Scheduled` flags, lifecycle enums and
  static procedures called from several systems use ECS storage without ECS style: the state lives in code paths
  instead of data.
- Decision: state changes are data, each system reacts to one state.
  - A `TurnWatcher` (pooled, one cached delegate) registers on the turn's task; its continuation posts it to the
    shard and `DispatchSystem` tags the entity `TurnEnded`. `TurnSystem` visits only ended turns, by filter
    (`EndCall`, `EndLoad` with `Loading`, `EndUnload` with `Unloading`); `GetResult` is read once after the end, as
    `await` does. Running service calls are `ServiceCall` entities ended the same way (`ServiceCallSystem`).
  - Lifecycle tags: `Ready` (mail waiting), `Loading` (`OnLoad` running), `UnloadRequested` (asked to leave, by
    `RebalanceSystem` or `IdleSystem`), `Unloading` (`OnUnload` running), `Fenced` (removed without `OnUnload`,
    ADR-042) and the `Removing(status)` component. `UnloadSystem` starts `OnUnload` once the actor is idle,
    `RemovalSystem` destroys it and routes its mail again, `MailboxSystem` runs `Ready` actors without a turn.
  - Rebalancing marks busy actors too; they leave when their turn ends.
  - Synchronous handlers allocate nothing; an async handler allocates only its own state machine (none with a pooling
    builder). Tracking turns as entities also gives graceful drain: a stopping shard waits until no actor and no
    service call is left.
  - The sample follows the same rule: an avatar without `GridCell` is not placed yet; `SentMotion` (last sent velocity
    and cell) is compared each tick instead of a flag; leaving is the `Despawning` tag handled by `DespawnSystem`; the
    grid is `MapGrid` data on the map entity, its steps are systems (`PlacementSystem`, `MotionSyncSystem`,
    `SnapshotSystem`).
- Alternatives: a ready queue avoids the archetype move per idle-to-ready transition, but it is a second
  representation of the same state; benchmarks showed no measurable difference.
- Consequences: no system scans busy actors and nothing polls tasks; every lifecycle step is a filter that tests and
  users can read. Async turns cost a continuation post (no allocation).

## ADR-038 `[Message]` marks messages and their flow
- Context: if any `[NinoType]` struct counted as a message, data types (a reply, a struct inside a list) would be
  indistinguishable from messages, wire-id collisions would be checked for data that never travels alone, and nothing
  would say which messages clients send or receive. A server could push an internal message to a client, or a client
  handler could expect one, and only testing would notice.
- Decision: `[NinoType, Message]` is a message; plain `[NinoType]` is data. Who sends it to whom is
  said by the clients module's attributes: none (servers only), `[FromClient]` (clients may send it; they name an actor only
  if it granted their session, ADR-051), `[ToClient]` (servers push it, clients handle it); both may be applied. The analyzer checks handlers (servers never handle a
  `ToClient`-only message, client handlers only `ToClient` ones) and call sites (a send to a session or group
  address needs `ToClient`, `ClusterClient` sends need `FromClient`, servers do not send `ToClient`-only messages to
  each other): DAS006. DAS008 rejects asking a session or group and clients addressing anything but actors. Client-facing nodes also accept only `FromClient` messages at run time.
  The attributes are contracts (`DaServer.Abstractions`, ADR-052); the client connections feature enforces them.
- Alternatives: marker interfaces (`IClientMessage`) would allow generic constraints, but who sends a message is
  metadata for the analyzer and the client connections, not a type relation.
- Consequences: a contracts file reads as a protocol (who talks to whom); leaking server-internal messages to clients
  is a build error. Traffic between client-facing nodes and other nodes stays framework frames, so roles never appear in message declarations.

## ADR-039 EasyEcs from NuGet, used as its documentation teaches
- Context: building against a sibling checkout couples the two repositories, and runtime code that gates systems with
  flags, runs manual queries in `OnUpdate` or counts ticks in components goes against EasyEcs's own guidance.
- Decision: reference the `EasyEcs` package (`EasyEcsVersion` in `Directory.Build.props`; it ships its generator and
  analyzer). Cadence is a system group the shard loop updates when due (`ShardRebalance`, `ShardIdleScan`), so
  `RebalanceSystem` and `IdleSystem` are plain `[ForEach]` methods; samples use `UpdateInterval`; `ActorRef` alone
  identifies a loaded actor (id, group); time-based logic is a component with a due tick and a system (no timer queue
  on the shard).
- Consequences: no co-development checkout in builds or CI; EasyEcs changes reach DaServer through releases.

## ADR-040 One noun per concept
- Context: one word for two things ("link" for a node connection and for an entity's pointer to its actor;
  load/unload next to activate/deactivate/retire for one lifecycle) makes code and docs ambiguous.
- Decision: `ActorRef` is the actor component (id, client group); "link" is only a node connection. `HandlerType`
  (`HandlerTypeInfo`, `HandlerTypeId`) is an actor or service type with `[Handle]` methods. The lifecycle is load/unload
  only: `Loading` (`OnLoad` running), `UnloadRequested` (asked to leave), `Unloading` (`OnUnload` running), `Removing`
  (entity destroyed, mail routed again); a running actor is a "loaded actor". The shard's cross-thread queue is the
  dispatch queue (`DispatchSystem`, as the Akka/Orleans dispatcher); the per-actor queue is the mailbox
  (`Mailbox`, `MailboxSystem`).
- Consequences: one term per concept in code, docs and tests.

## ADR-042 A node declared dead drops its actors without OnUnload
- Context: a node that learns it was declared dead (a partition, a long pause) and unloads its actors normally runs
  `OnUnload` after the cluster has already given their keys to other nodes: a stale save could overwrite the new
  owner's.
- Decision: on Dead every loaded actor is tagged `Fenced` and removed without `OnUnload`, as soon as its running turn
  ends; its mail is routed again to the new owner. The node then rejoins with a higher incarnation (as Orleans fences a
  silo that sees itself dead).
- Consequences: work since the actor's last save is lost on that node, which is the price of a partition. A turn in
  flight still finishes; stores that must reject late writes need versioned saves (roadmap N5/D5).

## ADR-043 Typed actor ids name the actor type
- Context: with a bare `long` key whose meaning depends on the actor type handling the message, a player id can be
  passed where a map id is meant, a message handled by two actor types is ambiguous, and a keyed publish reaches every
  actor type at the same number.
- Decision: each actor type declares its own id type in contracts (`readonly record struct MapId(long Value) :
  IActorId`) and derives from `Actor<MapId, MapState>` (`Id` is typed). `Send(id, m)` / `Ask(id, m)` route by the id
  type: its type id (FNV of the full name) is the actor's handler type id, so placement and the wire use the id type
  and messages need no owner. Two actor types may not share an id type. Every message carries the handler type id in
  the header's target (ADR-005). `Publish` reaches services
  only; actors are told one by one with `Send`. A client may name an actor only if that actor granted its session
  (ADR-051); otherwise the session binding decides.
- Alternatives: `long` keys with a generated per-actor proxy (`Cluster.Map(id).Enter(..)`), which ADR-035 rejects; the
  id inside the message (an `[ActorKey]` member), which couples messages to one receiver.
- Consequences: the compiler checks targets, ids are plain data that travel inside messages (`Enter(PlayerId)`), and
  the same message can be handled by several actor types. Ids are one `long` (checked once per id type).

## ADR-044 Actors and services hold no fields (DAS007)
- Context: an actor or service object is recreated whenever its actor loads, moves to another node or (later) its
  code is reloaded, and only components are persisted and handed over. A field on the object silently loses its value
  on those paths; a mutable static field is shared by every shard thread.
- Decision: DAS007 makes instance fields, auto-properties and non-readonly static fields of `Actor`/`Service` types a
  build error. Constants and `static readonly` data stay allowed. Per-actor state goes into `Actor<TId, TState>` or
  other components of the actor's entity, shared state into singletons.
- Consequences: the object is behaviour only, so persistence (N5), state handoff (N6) and hot reload (N9) touch data
  alone and can swap the code freely. It is also reusable: a removed actor's object, mailbox queue and session sets
  return to per-shard pools (`ActorPools`, bounded) and serve the next load of that type, so actor churn (logins,
  short matches, idle unloads) allocates nothing. Code must not keep a reference to an actor object beyond its turns
  (an un-awaited task that touches it after unload would reach the actor that reused it).

## ADR-045 DaServer is a general ECS distributed server framework
- Context: a framework for high-concurrency backends must not read as one game server, yet it must cover what every
  real-time backend needs.
- Decision: DaServer is a general-purpose ECS distributed server framework for high performance and high
  concurrency. The framework covers the capabilities any such backend needs: ECS shards, actors and services,
  messages and RPC, membership, placement, persistence, reload, ingress and client connections (sessions, push,
  groups, resume; chat, IoT, market data, collaboration and games all need them, ADR-048). Business domains are not
  part of it: logins, maps, interest management, matchmaking, inventories live in samples (`samples/mmo`;
  `samples/flashsale` planned, G5) or user code. Persistence (N5) and reload (N9) are planned.
- Consequences: a capability enters the framework only if backends of different domains need it; it is built on the
  same public extension points users have (node features, ingress, address handlers, filters), lives in its own
  namespace and is off unless configured. The runtime (`DaServer.Runtime`, `DaServer.Cluster`, `DaServer.Rpc`) never
  depends on a capability namespace (`DaServer.Clients`); an architecture test checks it.

## ADR-046 Two planes per node: DaServer TCP for data, ASP.NET Core for HTTP
- Status: minimum implemented (Kestrel on `DaServer:HttpPort`, `MapAsk`/`MapSend`, `/clients/endpoint`, `/clients/ws`);
  health, admin endpoints and Aspire probes in roadmap A1.
- Context: Kubernetes probes and Aspire health are HTTP; operators want admin commands; applications want REST
  endpoints (webhooks, payment callbacks, HTTP login, back-office). A hand-written HTTP responder would re-implement
  Kestrel and the official health-check middleware.
- Decision: every node hosts Kestrel on a separate HTTP port for health (`/health` readiness, `/alive` liveness),
  admin endpoints and user REST endpoints, all as minimal APIs (no MVC controllers: lighter, AOT/trim friendly, and
  one reloadable endpoint source for N9). Node-to-node and client traffic stays on DaServer's TCP links (zero GC,
  ordered, batched). HTTP handlers run on the thread pool and reach actors only through `ICluster`.
- Consequences: nodes reference the ASP.NET Core shared framework (aspnet runtime image, a few MB of memory, tens of
  ms of start-up); HTTP requests allocate like any ASP.NET request: on nodes hosting actors the HTTP plane carries
  low-frequency work, never per-tick or per-player traffic; dedicated HTTP edge nodes (ADR-048) may carry high
  request volume, since the actor interior stays zero-GC. Health endpoints (always on) and admin endpoints (authorized with the signing secret) come with A1.

## ADR-047 One message model: message, address, optional reply
- Context: messages and RPC are one thing; an API with a verb per target (push to a session, broadcast to a group,
  join, leave, disconnect, bind) and a wire with a frame kind per verb, where one field means either a call id or a
  target, grows with every scenario, and most such verbs are one scenario (client sessions) in the core.
- Decision: four concepts: message, address, optional reply (a request is a message whose type declares a reply),
  origin (who caused it; set only by ingress). Addresses are structs: `IActorId` (keyed, rendezvous), the one service
  (balanced), `INodeAddress` (pinned to a node, handled by the feature owning its scope) and `IGroupAddress`
  (fan-out to the nodes advertising its scope). Verbs: `Send` and `Ask` (kept apart: `Ask` holds the turn and a cycle
  deadlocks, so the call site must show it, as Akka Tell/Ask), `Publish` (every service type handling the message; no
  address value), `SendAfter` (N2). Wire: `Message`, `Reply`, `Control` (link protocol only); `Target` always set;
  trailers by flag. One verb per target kind is not needed: the address type names the target at the call site (as
  the id type does since ADR-043), and DAS006 checks `[ToClient]` by address kind. The origin is an address itself
  (`Origin(Node, Id)` implements `INodeAddress`): `Cluster.Send(Caller, m)` reaches whoever sent the message (the
  ingress that issued the origin delivers it; for client connections the origin is the session), and `ClientSession`
  converts implicitly from it where a session is stored or bound. The origin is a 16-byte trailer before the call
  id, set by the ingress (a client's own bytes never become it) and kept by every hop. Session operations are
  messages to a client address that its node applies instead of delivering them (`JoinGroup`, `LeaveGroup`,
  `Disconnect`; to a group they apply to every member on every node). Binding stays an actor operation
  (`BindSession`/`GrantSession`: the actor binds or grants itself) and is an `Ask` of a `SessionAccess` message to
  the session, so it travels on the
  ordered link with the actor's other messages to that session (ADR-022) and returns once applied.
- Consequences: sessions and groups are addresses of the client connections feature, not core verbs; `ICluster` is `Send`,
  `Ask`, `Publish` and `NodeId`; node-to-node and client wire
  protocols break once (pre-1.0, no rolling upgrade across it); no verb per target exists, no aliases; send
  paths stay generic over struct addresses (no boxing, the kind is a JIT constant).

## ADR-048 Ingress and client connections are built in; load balancing stays with the platform
- Status: agreed; implemented in roadmap G steps 5-10.
- Context: "gateway" covers three problems. Ingress (an external request becomes a cluster message with an origin,
  a deadline, admission and a reply path) is needed by every edge. Long-lived client connections (sessions, push,
  groups, resume) are needed by every real-time backend. Surge admission (flash sale, ticketing, waiting rooms) is
  policy over ingress plus partitioned state. Load balancing, TLS, CDN/WAF, Gateway API routes and autoscaling are
  solved by the platform, which cannot route to the node that owns an actor, push to a session, fan out to groups
  across nodes or tie back-pressure to mailboxes. Splitting the connection layer into its own package family made
  one framework look like two products.
- Decision: one framework. `DaServer.Ingress` (origin, capped deadline, reply sink, raw pass-through, one hop to the
  owner from the gossip view, load-aware early refusal, filters that reject with status + retry hint, token bucket,
  signed tokens) and `DaServer.Clients` (client connections as a node feature: transports, sessions, `ClientSession` and
  `ClientGroup` addresses, push and fan-out, resume tickets, a directory answering "which node to connect to") are
  namespaces of the `DaServer` assembly, built on the public feature points. A node accepts clients when a client
  port is configured; "gateway" is only a deployment word for such a node group (co-hosted in small clusters,
  dedicated in production). There is no balancer role: any client-facing node answers the directory, and the
  well-known address is the platform's LB or DNS (redirect mode or behind-LB mode). The HTTP plane (ADR-046) is the
  second ingress consumer; WebSocket clients connect through it. Packages: ADR-052. Waiting rooms are not part of the framework: they are policy for human traffic
  surges, belong at the CDN/SaaS edge to protect origins, and compose from the ingress primitives plus a queue actor
  (to be shown in `samples/flashsale`, G5).
- Consequences: dedicated client-facing nodes may carry high request volume (the actor interior stays zero-GC;
  ADR-046). Abstractions and the client SDK use newer runtime features only behind netstandard2.1 fallbacks (ADR-001). Redirect mode needs
  a public address per client-facing node; behind-LB mode leaves long connections unbalanced after a scale-out until
  clients reconnect.

## ADR-049 Refusals are status-only
- Context: the runtime refuses calls exactly when load is highest (full queues, a stale view, expired deadlines,
  lost links). Formatting a message per refusal (`$"No node hosts actor type 0x{id:x8}"`), serializing it into the
  error frame and building the exception at the refusing node turned overload into GC pressure on the node that is
  already struggling.
- Decision: refusals by the runtime (`NotHosted`, `Overloaded`, `Timeout`, `Disconnected`, `Forbidden`) are
  response frames without payload. `RpcException.Message` describes the status with constant text; a handler's own
  error keeps its message (`RpcException.Detail`, the only payload an error frame carries). `IPendingCall.Fail` takes
  a status, so relayed calls pass refusals on without an exception; the pending-call table refuses as `Overloaded`
  instead of throwing. Programming errors found on a cold path (a call to a message several services handle) still
  carry text.
- Consequences: refusing, relaying and timing out allocate nothing (`AllocationTests`); the caller still gets an
  exception (N3b adds non-throwing status results). A refusal does not name the type or message id: the caller's
  stack trace names the call.

## ADR-050 Client connections are a feature; the actor side of sessions stays in the runtime
- Context: client connections (ADR-048) have an edge part, on the nodes clients connect to (listener, sessions,
  groups, tickets, the directory, session operations, client messages entering the cluster), and an actor part, on
  every node hosting actors (the sessions each actor knows, `SessionClosed` when they end, even when the node holding
  them crashed, `SessionResumed` swapping a resumed one). A feature runs only where it is configured, and the runtime
  may not depend on `DaServer.Clients`.
- Decision: the edge part is `ClientConnections` (`DaServer.Clients`), a `NodeFeature` enabled by the
  `DaServer:Clients` section (`ClientConnectionOptions`): it registers the session, origin and client group address scopes,
  publishes its endpoint as a card entry and its session count as a pulse slot, and sends every client message
  through the ingress. The actor part stays in the runtime (`ActorSessions`): it needs only contract types
  (`ClientSession`, `SessionClosed`, `SessionResumed`, `SessionAccess`), records an actor's callers and bound or granted
  sessions, handles
  closes and resumes as dispatch filters of their message types and closes the sessions of a dead node holding the
  session scope. Card entries and pulse slots are generic (`SetCardEntry`, `AddPulse`: keys chosen by features, at
  most three slots), so `ClusterMember` keeps no client fields. `ResumeGrace` stays a node option: both parts wait
  it.
- Consequences: a node without the `Clients` section runs no client code on its IO side; the runtime's dependency
  rule holds (an architecture test, roadmap G step 9); a feature of this assembly may use the node's internals
  (`ClientConnections` sends `SessionClosed` to granted actors directly), never the other way round.

## ADR-051 Clients name only actors that granted their session
- Context: a client message to an actor goes to the actor bound to the session. Rooms, matches and shared boards need
  the client to name the actor (`client.Send(roomId, new Say(..))`), and a session may be in several of one type. A
  per-message flag letting clients name any id would make every such message an open door: any id
  activates an actor (and, with persistence, loads it), every handler must check `Caller` itself, and internal ids
  can be enumerated. Orleans keeps untrusted clients behind a frontend that authorizes before calling grains;
  Nakama and Colyseus expose match and room ids but accept messages only from clients that joined (a server-side
  decision); ET routes client messages by the session's bound unit.
- Decision: the actor decides who may address it, once: `await GrantSession(session)` / `RevokeSession(session)`
  (an `Ask` of `SessionAccess` to the session, like `BindSession`, which is a grant that is also the session's actor
  of its type). The client-facing node keeps the session's grants (at most 64, an immutable array scanned lock-free)
  and refuses a message naming any other actor with `Forbidden` before it enters the cluster. A grant puts the
  session in the actor's `Group`, the actor receives `SessionClosed` / `SessionResumed` for it, and the resume
  ticket carries grants with their bound flag. Unbind and revoke remove the entry of that actor only.
- Alternatives: bindings only (one actor per type, no two rooms at once); opaque per-session handles instead of ids
  (hides ids clients usually display anyway, one more mapping); handler-side checks (repeated in every handler, and
  the actor is activated before it can refuse).
- Consequences: `[FromClient]` has no options; an unknown or unadmitted id costs the edge a scan and a reply, never an
  actor activation or a load. Entering a room is a request to something that may admit (a lobby service asking the
  room, or the room's own bound player flow).

## ADR-052 Four packages: contracts, shared wire, client, server
- Context: contract assemblies (messages, ids) are shared by servers and clients, Unity clients included, so they and
  the client SDK must build for netstandard2.1 and must not pull in the server runtime or EasyEcs. Client and server
  speak the same protocol over the same transport (frames, pooled buffers, TCP and WebSocket connections, pending
  calls, handler dispatch); writing it twice would duplicate the protocol, and making the server depend on a package
  called "Client" (or putting sockets into "Abstractions") would misname what the packages are.
- Decision: as Orleans (Core.Abstractions / Core / Client / Server), gRPC (Core.Api / Net.Common / Net.Client /
  AspNetCore.Server) and MagicOnion (Abstractions / Shared / Client / Server) do, four layers, each depending only on
  the one below:
  - `DaServer.Abstractions` (netstandard2.1, net10.0, net11.0; namespace `DaServer`: one using in a contract file): what
    contract assemblies reference: `[Message]`, `[Handle]`,
    `[FromClient]`/`[ToClient]`, `[WireId]`, `IRequest`, addresses (`IActorId`, `ClientSession`, `ClientGroup`,
    `Origin`), `IMessageSender`, session messages, wire ids, the client message registry and generated-registration
    support.
  - `DaServer.Core` (netstandard2.1, net10.0, net11.0): the wire and transport both sides run: `FrameHeader`/`RpcStatus`/
    `ControlOp`, `FrameWriter`/`BufferPool`, `Connection`/`WebSocketChannel`, pending calls (`CompletionThread`: a thread
    that completes its own calls, a shard on servers), `CallContext`, `RpcException`, handler dispatch,
    `SignedToken`.
  - `DaServer.Client` (netstandard2.1, net10.0, net11.0): `ClusterClient`.
  - `DaServer` (net11.0, ADR-054): the server runtime (shards, actors, services, cluster, ingress, client connections), plus
    `DaServer.Hosting` and `DaServer.Aspire`.
  - `DaServer.Simulation` (net10.0, net11.0; on Client and EasyEcs, not the server): simulated users for end-to-end,
    load and behavior tests (ADR-056).
  The generator and analyzers ship with Abstractions, so every assembly using DaServer gets them. Newer BCL APIs are
  used on .NET and replaced on netstandard2.1 (`Platform`, internal polyfills of `Lock` and `FrozenDictionary`), so
  servers pay nothing for the shared code. An architecture test enforces the references.
- Alternatives: transport inside the client package (the server would depend on "Client"); inside Abstractions
  (every contract assembly carries sockets); a separate client transport (two implementations of one protocol).
- Consequences: a Unity client references Abstractions, Core and Client (one package reference, the rest are
  dependencies); contract projects reference Abstractions only.

## ADR-053 Keep-alive: silence closes a connection, pings only fill idle directions
- Context: TCP does not report a peer that vanished (a host gone, a dropped NAT or load balancer mapping, a mobile
  client that switched networks or went to the background) until a write times out, about 15 minutes on Linux, and
  never while nothing is written; TCP keepalive probes only idle sockets, cannot see a peer process that stopped
  reading, does not cross proxies and has no netstandard2.1 API. Client sessions had no liveness check, and a
  half-open node link stayed stuck while gossip through other nodes showed the peer alive.
- Decision: as Kubernetes' client-go HTTP/2 health check, SignalR, MQTT and RabbitMQ do, liveness is "something
  arrived on this connection lately": every connection stamps when it last received and last wrote
  (`IFrameChannel.LastReceivedMs`/`LastSentMs`, once per read and per flush). A side that has sent nothing for an
  interval sends a header-only `Ping`; the other side answers `Pong` on the same connection. Any frame counts, so
  busy connections carry nothing extra. A connection that received nothing for its timeout is closed
  (`KeepAlive.Check`); closing is never a verdict on the peer, the owner reconnects and resumes:
  - Clients ping after `ClusterClient.KeepAliveInterval` (15 s) without sending and close after `ServerTimeout`
    (30 s) without receiving, then reconnect and resume with their ticket; `CheckConnection()` probes at once (app
    back in the foreground, network changed). Client-facing nodes never ping: once a second they close sessions
    silent for `ClientConnectionOptions.IdleTimeout` (45 s), which then wait out the resume grace.
  - Node links ping on their outgoing connection after a gossip interval without sending; either side closes a link
    connection silent for max(`SuspectAfter`, three gossip intervals), and the link reconnects and replays its resend
    window (ADR-008). Membership alone decides that a node is dead.
- Alternatives: TCP keepalive or `TCP_USER_TIMEOUT` (see context); server-driven pings to every session (traffic
  from the side with the most connections, and the client still needs its own timeout); per-session timers (a
  reschedule per frame, where one sweep a second costs two comparisons a session).
- Consequences: a lost client is noticed within 45 s by its node and 30 s by itself (seconds with
  `CheckConnection`), a stuck link within a few seconds; idle connections exchange 24-byte frames every interval,
  which also keeps NAT and load balancer mappings open. Node timeouts must stay a few client intervals apart.

## ADR-054 The server runtime targets .NET 11
- Context: .NET 11 (STS, supported to about November 2028, the same horizon as .NET 10 LTS) is at RC2 with a go-live
  license; GA is November 2026. Its gains are in the runtime (JIT, threading, the BCL's own async), so they were
  measured, not assumed: the same code and BenchmarkDotNet suite hosted on each runtime, back to back on one machine.
- Measured (.NET 10 -> .NET 11): calls with 1 / 64 / 1,024 in flight, local 25.0 -> 19.8 us, 515 -> 223 us,
  4.50 -> 1.65 ms; remote 103 -> 101 us, 379 -> 335 us, 2.22 -> 1.66 ms; one-way messages local 1.93 us -> 641 ns,
  remote 1.57 us -> 975 ns; the 3k/9k client scenario 14.0 -> 13.4 ms and 48.3 -> 45.6 ms; group broadcasts to
  1k/5k clients 3-8% slower (5.09 -> 5.25 ms ... 27.8 -> 30.1 ms); allocations equal.
- Decision: `DaServer`, `DaServer.Hosting`, `DaServer.Aspire`, the samples' nodes, tests and benchmarks target
  `net11.0`; the shared packages add `net11.0` beside netstandard2.1 and net10.0 (ADR-001). `global.json` pins the 11.0
  SDK (RC2 until GA, then the GA band); CI installs it from there. Runtime async (`runtime-async=on`) stays off until
  measured on its own: the framework's hot paths have no async methods (pooled `IValueTaskSource`s).
- Alternatives: stay on .NET 10 LTS (gives up 2.3-2.7x on concurrent local calls, 1.1-1.3x remote); multi-target the server (doubles build and test
  time for users who all deploy one runtime).
- Consequences: servers need the .NET 11 runtime (x86-64-v2 or newer); BenchmarkDotNet 0.16 (preview) hosts .NET 11.
  Broadcast fan-out is the one path that did not gain: profile it on .NET 11 (roadmap, "Open after G").


## ADR-055 Shared rules, a real client, and bots that run it
- Context: the sample's bot called the protocol directly (log in, send random `Move`s, count pushes): a second
  client written for the test, so the end-to-end and load tests proved the servers against code no player runs, and
  rules both sides need (movement, the interest grid) lived in the map server only. ET runs its robots as real
  clients (each robot a fiber running the client's scenes).
- Decision: three layers in samples. `Sample.Shared` (`<DaServerShared>true</DaServerShared>`) holds rules and ECS
  systems both sides run (`MovementSystem`, `MapGeometry`); the generator marks it `[assembly: DaServerShared]` and a
  node project referencing it runs its public systems on every shard, as its own; DAS010 makes a reference from a
  shared assembly to the server runtime or the transport (`DaServer`, `DaServer.Hosting`, `DaServer.Core`,
  `DaServer.Client`) a build error. `Sample.Client` is the game client's logic as data and systems
  (`ClientSystems`): a player entity per connected client, pushes handled into components on the world's thread
  (`PushSystem` drains the client's callback queue), prediction with the shared rules, input sent when it changes,
  the view kept to the 3x3, late pushes of a map the player left dropped (they carry the map id); flows (log in,
  enter a map) are awaits on the world's thread. Per-player state lives on the player entity, never in world
  singletons, so one world holds one player in a game and thousands in a bot process. `Sample.Bot` is the client run
  as simulated users (ADR-056) with a behavior domain at the controls (change maps, chat, wander) and its
  measurements.
- Alternatives: a fiber (an isolated world, scheduler and mailbox) per robot as in ET: 10,000 worlds polled every
  millisecond for the isolation a player entity already gives; systems over many players batch the same work.
  Keeping protocol-level bots: cheap, but tests the servers against a client nobody ships.
- Consequences: the end-to-end test and the load test run the client a player runs; a client change is load-tested
  by the next run. Bots hold what a client holds (avatars in view as entities), which costs memory a protocol bot
  did not spend. Shared systems run on every node that references the shared assembly, so a node references it only
  when it simulates what the rules are about (the map server does; the game server does not).

## ADR-056 Simulated users are a framework package: client worlds, HTN planning as data, swarms
- Context: every backend built on DaServer needs to test itself with realistic users: players in a game, buyers in a
  flash sale, fans in a ticket queue, members of a chat. A sample-only bot would be rewritten by each of them. Behavior
  beyond scripts (decide what to do next from what is true now, several steps ahead) is what game AI solves with
  priority selectors (ET's AI handlers: the first handler whose check passes runs as a cancellable coroutine) and
  hierarchical task networks (HTN: compound tasks decomposed by ordered methods into primitive tasks, with
  preconditions and effects).
- Decision: `DaServer.Simulation`, a client-side package (ADR-052; it never references the server runtime, so it
  runs against any cluster):
  - `ClientLoop`: a client world on its own thread, a `SynchronizationContext` so flows awaited on it resume there;
    each pass runs posted work and the client's arrival handling, the world's systems tick at the servers' rate.
  - Planning as ECS data (`DaServer.Simulation.Planning`): facts are a bit set component (`Facts`) written by sensor
    systems; a domain is a singleton of plain tables (`HtnDomain`: tasks, methods, steps; conditions and effects are
    bit masks), built once with `DomainBuilder<TFact>`; a plan is a component of task ids (`Plan`, inline buffer).
    `PlannerSystem` decomposes on a copy of the facts (method order, effects, backtracking) and starts tasks one at
    a time; a primitive task is a component, started by adding it and done when the system carrying it out removes
    it. Planning allocates nothing and holds no references; a priority selector is a root with one task per method,
    so one mechanism covers both.
  - `Swarm`: users spread over one loop per core, started in waves (`MaxStarting`), worlds configured on their own
    threads, sums read on each loop's thread, a measured window with a shared `SwarmMeter` (latency histogram, named
    counters, disconnects) reachable from systems as the `SwarmMetrics` singleton. Passing is the scenario's call.
  A scenario writes only its fact enum, its domain, its sensor and task systems, how a user starts, and its verdict.
- Alternatives: coroutine behaviors (ET): state hidden on await stacks, one timer and one coroutine per agent,
  handlers found by reflection; behavior trees: a tree walk per agent per tick, state in node objects; goal-oriented
  action planning: a search per agent, costlier than ordered decomposition for scripted users; keeping it in samples:
  every other sample (G5's flash sale) would copy it.
- Consequences: the MMO sample's bots are a `Swarm` of `Sample.Client` players with a three-method domain (change
  maps, chat, wander); the flash sale's buyers will be another domain over the same package. In-process cluster tests
  (P1 `DaServer.Testing`) stay a separate, server-side package. Server-side NPC planning would reuse the same model in
  user code; the framework does not ship a server AI.

## ADR-057 ReplyWhen: a reply that waits without holding the turn
- Context: actors are turn-based (ADR-002): an actor awaiting inside a handler takes no other message until the
  handler returns. A handler that only waits for an acknowledgement before replying (a map binding the entering
  player's session: `await BindSession(session); return true;`) therefore serves one caller per round trip; 10,000
  players entering two maps queued behind those round trips and were refused (`Overloaded`). The await is needed for
  ordering (the client's next message must find the binding), not for the actor's state.
- Decision: `return ReplyWhen(task, reply);` (actors and services, `ValueTask` or `ValueTask<T>` tasks) ends the turn
  at once and replies `reply` when the task completes, its failure instead. The handler leaves a pooled watcher in a
  thread-static slot that the generated dispatcher's `CallContext.Reply` takes on the same thread (one volatile read
  for reply types never used with it): no allocation per call. DAS011 keeps `ReplyWhen` to the returned expression of
  a handler that is not `async`, so nothing of the handler runs after the turn ended.
- Alternatives: reentrancy (Orleans `[AlwaysInterleave]`, interleaving turns) gives up the turn guarantee for the
  whole handler; a one-way access change followed by a push on the same link changes the business protocol (wait for
  a push instead of a reply); per-region actors only spread the queue.
- Consequences: `HotActorBenchmark` (1,024 callers through one actor waiting for another node): 143.8 ms awaited,
  3.1 ms with `ReplyWhen`, 236 B vs 114 B per batch; `RepliesSentLaterDoNotAllocate` holds < 8 B per call. The sample's
  maps reply to `Enter` and `Leave` this way.

## ADR-058 Delays are messages sent later, held by the shard
- Context: `await Task.Delay` in a handler holds the actor's turn for the whole delay, allocates a timer and a task,
  and resumes through the thread pool; every backend needs delays (retries, cooldowns, timeouts of business flows,
  scheduled ticks of one actor).
- Decision: `ICluster.SendAfter(delay, address, message)` (and to a service) serializes the message at once into a
  pooled frame and schedules it in the calling shard's timer wheel (`ShardTimers` singleton, `TimerWheel<T>`: hashed,
  O(1), 5 ms resolution); `TimerSystem` routes what is due exactly like a `Send` made then, so the message follows an
  actor that moved and is ordinary mail (turn-based, bounded by the mailbox). `CancelSend(handle)` cancels on the
  scheduling shard (its index is in the handle's top byte, another shard's handle is refused). The shard sleeps no
  longer than the resolution while a send waits; sends still pending when the shard stops are dropped (one-way
  delivery is at most once). Calling it off a shard thread throws: timers are a shard's data.
- Alternatives: one wheel per node (a lock or a hop per schedule); `Task.Delay` continuations (allocations, a held
  turn); durable reminders (a store; they come with persistence, N5, and are a different guarantee).
- Consequences: `DelayedSendsDoNotAllocate` holds < 8 B per send; `TimerBenchmark` shows 0 B, a mean dominated by one
  wheel slot shared by the batch. Delayed sends do not survive a restart.
