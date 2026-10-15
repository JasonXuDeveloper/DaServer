# Architecture decision records

Short records of non-obvious decisions, each stating the decision that holds now. Numbers are stable (gaps are
records that no longer hold). Each: context → decision → consequences.

## ADR-001 .NET 10 only, single runtime assembly
- Context: supporting netstandard (Unity) and the server from shared sources duplicates code paths with `#if` and
  rules out the APIs the hot paths need.
- Decision: target `net10.0` only; one runtime assembly `DaServer` (server + client), plus `DaServer.Generator`,
  `DaServer.Hosting` and `DaServer.Aspire`.
- Consequences: use .NET 10 APIs freely (`Lock`, frozen collections, POH, `PoolingAsyncValueTaskMethodBuilder`,
  C# 14). Clients on other runtimes (Unity) need a separate client package.

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
- Consequences: gateways route by patching header fields without parsing payloads; collisions are build errors
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
  messages, pushes and session control queued on it; a network blip between a game node and a gateway looks like a
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
  session with a ticket instead (ADR-036), so gateways keep no replay buffer per session.
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
  tests assert 0 B in steady-state loops.

## ADR-011 Naming: Shard (DaServer executor) vs World (EasyEcs)
- Context: EasyEcs uses standard ECS vocabulary (`World`, `Query`, `EcsSystem.OnUpdate`).
- Decision: DaServer's single-threaded executor is a `Shard`; each shard owns one EasyEcs `World`. Actors and services
  expose `World` and `Cluster` (`ICluster`); ECS code reaches the cluster through the `ShardContext` singleton
  (`world.Cluster()`).
- Consequences: no name clash; "world" always means the ECS world.

## ADR-012 Source generation instead of registration code
- Decision: `MessageGenerator` emits typed `Ask` overloads per request and registers `Flow.FromClient` messages for
  gateways in a module initializer of the declaring assembly (marked `[assembly: MessagesAssembly]`). `HostGenerator`
  registers every actor and service with one dispatcher per `[Handle]` method, client handler classes and EasyEcs
  systems, runs the registration of referenced message assemblies, and emits the node entry point (ADR-021).
- Consequences: no reflection, no configuration strings, no hand-maintained lists; misuse is a build error
  (DAS001-DAS008, ADR-016).

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
- Decision: `DaServer.Aspire` describes a cluster in the AppHost (`AddDaServerCluster`, `AddNodes`, `AddGateways`,
  `AddBalancer`, `WithGateways`). The same model runs locally with the dashboard and publishes Docker Compose or
  Kubernetes through Aspire publishers. Nodes export logs and metrics over OTLP when Aspire (or any collector)
  configures it. There are no hand-written deployment files.
- Consequences: one source of truth for topology. Ports are assigned deterministically per node. Replicas are
  separate resources because cluster members are addressed individually, not through a load balancer (ADR-030).

## ADR-016 Analyzer separate from generators
- Context: generators that report diagnostics carry symbols and locations in their models, which defeats caching.
- Decision: generators use syntax-only predicates and equatable, symbol-free models with tracking names, and report
  nothing. `DaServerAnalyzer` reports DAS001–DAS008 (collisions in a compilation-end action), and reads symbols
  through the same `Shapes` code as the generators.
- Consequences: edits that do not touch messages or handlers hit the cache (tested); "skipped by the generator" and
  "reported by the analyzer" cannot disagree.

## ADR-017 No roles: the deployment unit is a project
- Context: role strings on implementations and in configuration repeat the topology in code, configuration and the
  AppHost, and a typo silently hosts nothing.
- Decision: a node hosts every actor, service and system compiled into its process. Splitting the cluster means
  splitting projects (`Sample.LoginServer`, `Sample.GameServer`, `Sample.MapServer`, `Sample.Gateway`); the AppHost
  lists projects and replica counts. Accepting clients is a capability (`ClientPort`, `AddGateways`), not a role.
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
  `SessionClosed` is a framework message sent to every actor a closing session was bound to.
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
- Decision: `HostGenerator` emits `DaServer.Generated.NodeProgram.Main` (Generic Host + `AddDaServer`) for an
  executable that references `DaServer.Hosting` and declares no entry point. Writing a `Program.cs` opts out.
- Consequences: a node project is business classes only; names, ports and seeds come from the AppHost (environment).

## ADR-022 One ordered stream per node direction
- Context: if a node answers requests on the accepted connection while its own binds, pushes and messages to the
  caller travel on its outgoing link, a reply can overtake a `Bind` sent before it (the client calls the actor before
  the gateway knows the binding).
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
  session before the first await; and a crashed gateway must not leave actors believing their sessions are alive.
- Decision: `BindSession(session)` also puts the session in the actor's `Group`; `UnbindSession` and close leave it.
  Every loaded actor records the sessions bound to it (a `ClientSessions` component); when a session closes the
  gateway publishes `SessionClosed` to each, and when a gateway is declared dead every node closes that gateway's
  sessions for its own actors. `Caller` (the origin, ADR-047) is the session of the running turn and holds across its
  awaits.
- Consequences: business code binds and handles `SessionClosed`; nothing else. Gateway crash tests cover it.
  Session control and pushes go to the gateway's link from the view, or, while gossip has not listed the gateway
  yet, from its Hello (a client call can reach a node first): a Bind is never dropped while the cluster forms.

## ADR-025 Gateway groups: slot arrays under a per-group lock
- Context: a group of thousands of sessions must take joins and leaves in O(1) and fan out without a snapshot
  allocation per broadcast.
- Decision: each group is a slot array; a session knows its slot in each group, leave is swap-remove. Membership
  changes and fan-out both take the group's lock (fan-out reaches every earlier join). `ClientGroup.All` is a group
  every session joins on accept. `Connection.Send` closes a slow consumer off the caller's thread, so a fan-out never
  re-enters a group. Lock order: group gate, group, connection send lock. Balancers receive no broadcasts.
- Alternatives: copy-on-write member arrays (O(n) per join, O(n^2) to fill a group, large array churn); a
  `ConcurrentDictionary.Values` snapshot per broadcast (8 B per session per broadcast); gateway sessions as ECS
  entities on a gateway shard (one more thread hop per push; kept on the roadmap).
- Consequences: join/leave O(1); a broadcast allocates nothing (allocation tests); a 3k-session fan-out holds one
  group lock for about a millisecond, which only delays membership changes of that group.

## ADR-026 Balancers and client gateway discovery
- Context: clients that are given every gateway address and spread themselves round robin must be reconfigured when
  a gateway is added, and load follows connection order, not load.
- Decision: members gossip their client host, port and session count. A gateway with `Balancer` (Aspire
  `AddBalancer`) answers `PickGateway` with the less loaded of two random gateways that accept sessions (power of two
  choices: even spread from stale counts, no herd). `ClusterClient.ConnectAsync(host, port)` follows the answer, and
  asks again while the cluster is still forming (no gateway known yet) until its call timeout.
- Consequences: clients know one address; gateways scale by replica count. Any gateway can answer `PickGateway`, so
  a balancer is a deployment choice, not a separate component.

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

## ADR-029 Group broadcasts go to every gateway
- Context: a `ClientGroup` push is sent once to every gateway, and gateways with no member of the group drop it.
  Tracking which gateways hold members would save those frames.
- Decision: keep sending to every gateway. The cost is one small frame per gateway per broadcast, independent of
  the group size, and a gateway without the group answers it with one dictionary miss. Tracking members per gateway
  needs membership gossip from gateways to every sender (sessions join groups on whichever node binds them), more
  ordering cases on gateway loss, and it only pays when there are many gateways and groups live on few of them.
- Consequences: simple and ordered with binds (ADR-022). Revisit when deployments run tens of gateways with small
  groups; the roadmap keeps the item.

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

## ADR-031 Gossip carries pulses, cards travel once
- Context: serializing the whole member table every round costs about 10 KB/s per idle node at one round a second,
  growing with the square of the cluster size, and allocates new member objects on every receiver; a view that keeps
  the load hints of the last status change never shows gateways filling up.
- Decision: a member is its card (identity, endpoints, hosted handlers: fixed while it runs) plus its pulse
  (incarnation, heartbeat, status, load, sessions: 29 bytes). Gossip and its reply carry pulses, written into a pooled
  frame and read in place; a pulse naming an unknown node is answered with `CardRequest`/`Cards`. Pulses update
  members in place; incarnation and status changes publish a copy and rebuild the view. Because counts lag a round,
  the balancer adds the clients it directed to a gateway since its last pulse.
- Consequences: a steady-state round allocates nothing (allocation test at 300 rounds/s); idle nodes in the 10k
  topology allocate 0 KB/s; gossip size is 29 bytes per member. Load hints are fresh within a round.

## ADR-032 Names from the distributed-server vocabulary
- Context: names borrowed from one framework (Orleans' `OnActivateAsync`) or describing a mechanism instead of its
  role read wrong to people who know other distributed servers.
- Decision: name things by their role in a cluster, in pairs where they are pairs: `OnLoad` / `OnUnload` (the actor is
  loaded on / leaves this node), `Flow.FromClient` / `Flow.ToClient` (the two directions across the gateway),
  `Caller` (who sent the message from outside; invalid for server calls), `Disconnect` (not a game's "kick"), balancer
  (`AddBalancer`, `Balancer`; "dispatch" means message dispatch only), `ClusterMember` (no clash with
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
- Alternatives: a timed flush (adds latency to every message); shared reference-counted payloads with gather writes
  (saves the copy, which profiling showed is not the cost).
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
    the one service handling the message (several service handlers is an `Ambiguous` error). `Publish(e)` reaches
    every service type handling it once.
  - A `Flow.FromClient` message to an actor goes to the actor bound to the client's session for that actor type
    (`AnyId` lets the client name the actor); the actor binds itself: `await BindSession(session)` (acknowledged by
    the gateway before it returns).
  - Messages to clients are sends to their address: `Send(session, m)` / `Send(group, m)`, handled on clients by
    `[Handle]` methods of a registered object.
  - Generated code: ADR-012.
- Alternatives: keeping interfaces with a message-shaped escape hatch would leave two models; making the receiver's
  type explicit (`Ask<Player>(...)`) couples senders to implementations.
- Consequences: senders depend on message (and id) types only, so handlers move between actors, services and projects
  without touching callers. Hot paths: messages are `in` structs of generic methods, serialized once into pooled
  frames. A client that cannot drain its pushes (send backlog over 256 KB) is disconnected rather than throttled; it
  reconnects and the server resends full state, which is cheaper and simpler than per-session drop policies in a
  state-synchronized game. ADR-047 (agreed, roadmap G0) folds pushes into `Send` to session and group addresses.

## ADR-036 Clients resume sessions with signed tickets
- Context: a network blip, a slow client disconnected at its send bound (ADR-035) or a gateway restart closes the
  session: without resumption, actors remove the avatar, the player logs in again, and players near it see it vanish
  and reappear.
- Decision: the session's gateway sends its client a reconnect ticket after every binding change: session, bindings
  (handler type id, key) and expiry, signed with HMAC-SHA256 of the cluster secret (`DaServer__ClusterSecret`,
  generated by Aspire). Gateways keep no state for it, so any gateway accepts it. `ClusterClient` reconnects through
  its original address (a balancer picks a live gateway) with exponential backoff and presents the ticket; the new
  session takes over the bindings and the actors' groups, and every bound actor receives
  `SessionResync(Session, Previous)` before the client's next message (same links). Actors swap the session and send
  the full state; pushes sent meanwhile are lost by design (state, not a log). A lost connection is reported as
  `SessionClosed` only after `ReconnectGrace` (30 s), also for a crashed gateway; the runtime drops the late
  `SessionClosed` of a resumed session. An explicit close (client dispose, `Disconnect`) is reported at once. Tickets
  live `ReconnectTicketLifetime` (10 min); clients refresh theirs at half of it.
- Alternatives: resumable client streams like node links (ADR-008) keep per-session replay buffers on gateways, which
  is memory per connection and lost with the gateway anyway; a session store (Redis) adds a dependency and a hop.
- Consequences: without a cluster secret there are no tickets and a lost connection closes the session at once. A
  ticket is a bearer token for its lifetime, carried on the same connection as the login. Groups joined by `AnyId`
  messages are rejoined by the client (`client.Reconnected`).

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
- Decision: `[NinoType, Message]` is a message; plain `[NinoType]` is data. `[Message(Flow)]` says who sends it to
  whom: `Internal` (default, servers only), `FromClient` (clients may send it; `AnyId` lets them name the actor),
  `ToClient` (servers push it, clients handle it); flags combine. The analyzer checks handlers (servers never handle a
  `ToClient`-only message, client handlers only `ToClient` ones) and call sites (a send to a session or group
  address needs `ToClient`, `ClusterClient` sends need `FromClient`, servers do not send `ToClient`-only messages to
  each other): DAS006. DAS008 rejects asking a session or group and clients addressing anything but actors. Gateways also accept only `FromClient` messages at run time.
  Agreed for roadmap G step 7: `Flow`/`AnyId` move to `[FromClient(AnyId)]` / `[ToClient]` attributes of the gateway
  module, so the core's `[Message]` carries no client concept (ADR-045).
- Alternatives: marker interfaces (`IClientMessage`) would allow generic constraints, but a message's flow is
  metadata for the analyzer and the gateway, not a type relation.
- Consequences: a contracts file reads as a protocol (who talks to whom); leaking server-internal messages to clients
  is a build error. Gateway-to-node traffic stays framework frames, so roles never appear in message declarations.

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
  only; actors are told one by one with `Send`. A client may name an actor only with an `AnyId` message whose handler
  is that actor type; otherwise the session binding decides.
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
  part of it: logins, maps, interest management, matchmaking, inventories live in samples (`samples/mmo`,
  `samples/flashsale`) or user code.
- Consequences: a capability enters the framework only if backends of different domains need it; it is built on the
  same public extension points users have (node features, ingress, address handlers, filters), lives in its own
  namespace and is off unless configured. The runtime (`DaServer.Runtime`, `DaServer.Cluster`, `DaServer.Rpc`) never
  depends on a capability namespace (`DaServer.Clients`); an architecture test checks it.

## ADR-046 Two planes per node: DaServer TCP for data, ASP.NET Core for HTTP
- Status: agreed; implemented in roadmap A1 (nodes have no HTTP plane yet).
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
  request volume, since the actor interior stays zero-GC. Health endpoints are always mapped, admin endpoints require the cluster secret.

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
  ingress that issued the origin delivers it; for client connections the origin is the session), and `SessionRef`
  converts implicitly from it where a session is stored or bound. The origin is a 16-byte trailer before the call
  id, set by the ingress (a client's own bytes never become it) and kept by every hop. Session operations are
  messages to a client address that its node applies instead of delivering them (`JoinGroup`, `LeaveGroup`,
  `Disconnect`; to a group they apply to every member on every node). Binding stays an actor operation
  (`BindSession`: it binds the actor itself) and is an `Ask` of a `Bind` message to the session, so it travels on the
  ordered link with the actor's other messages to that session (ADR-022) and returns once applied.
- Consequences: sessions and groups are addresses of the gateway module, not core verbs; `ICluster` is `Send`,
  `Ask`, `Publish` and `NodeId`; node-to-node and client wire
  protocols break once (pre-1.0, no rolling upgrade across it); the old verbs are removed outright, no aliases; send
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
  signed tokens) and `DaServer.Clients` (client connections as a node feature: transports, sessions, `SessionRef` and
  `ClientGroup` addresses, push and fan-out, resume tickets, a directory answering "which node to connect to") are
  namespaces of the `DaServer` assembly, built on the public feature points. A node accepts clients when a client
  port is configured; "gateway" is only a deployment word for such a node group (co-hosted in small clusters,
  dedicated in production). There is no balancer role: any client-facing node answers the directory, and the
  well-known address is the platform's LB or DNS (redirect mode or behind-LB mode). The HTTP plane (ADR-046) is the
  second ingress consumer; WebSocket clients connect through it. Packages: `DaServer.Abstractions` (contracts:
  attributes, `IRequest`, addresses incl. `SessionRef`/`ClientGroup`, `Origin`, wire ids; netstandard2.1 + net10),
  `DaServer` (runtime + generator and analyzers), `DaServer.Client` (client SDK on Abstractions; netstandard2.1 +
  net10, Unity), `DaServer.Hosting`, `DaServer.Aspire`. Waiting rooms are not part of the framework: they are policy for human traffic
  surges, belong at the CDN/SaaS edge to protect origins, and compose from the ingress primitives plus a queue actor
  (shown in `samples/flashsale`).
- Consequences: dedicated client-facing nodes may carry high request volume (the actor interior stays zero-GC;
  ADR-046). Abstractions and the client SDK cannot use net10-only language or runtime features. Redirect mode needs
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
  exception (N3b adds non-throwing status results). A refusal no longer names the type or message id: the caller's
  stack trace names the call.
