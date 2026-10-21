# Architecture decision records

Short records of non-obvious decisions, each stating the decision that holds now. Numbers are stable (gaps are
records that no longer hold; a record replaced by a later one keeps its number as a one-line pointer, so references
resolve). Each: context → decision → consequences.

## ADR-001 The server runs on the newest .NET; shared code also builds for netstandard2.1
- Context: the hot paths need the newest runtime and its APIs, while contracts and the client SDK must run in Unity
  (netstandard2.1) and on the previous .NET.
- Decision: the server runtime (`DaServer`, `DaServer.Hosting`, `DaServer.Aspire`) targets one framework, the newest
  .NET (ADR-054); what clients share (`DaServer.Abstractions`, `DaServer.Core`, `DaServer.Client`, ADR-052) also
  builds for netstandard2.1 and the previous .NET, with the newer APIs behind `Platform` and internal polyfills.
- Consequences: the server uses the newest APIs freely (`Lock`, frozen collections, POH, C# latest); shared code
  pays a few `#if NET` lines, never a second implementation.

## ADR-002 Shards: single-threaded shards with a dispatch queue, not a thread-pool actor runtime
- Context: a thread-pool actor runtime (as in a typical in-house game server) resumes continuations on pool threads and funnels many
  producers into queues built for one, which needs locks or careful ownership in user code.
- Decision: each node runs N shards (default one per core), each a dedicated thread owning an EasyEcs `World`, a
  bounded lock-free MPMC dispatch queue for new calls and messages (refused beyond its capacity, ADR-027), a second
  one for completions of the shard's own asks and runtime callbacks (never refused, drained first) and a fixed tick.
  An actor lives in exactly one shard; a service runs on the shard that received its message.
- Consequences: no locks in user code; deterministic ordering per shard; messages are processed immediately (tick rate
  only drives systems). Systems and handlers are synchronous: a handler runs to completion and an ask's answer comes
  back as mail (ADR-081), so the shard never holds suspended work.

## ADR-003 Completing RPCs on the caller's shard (no SynchronizationContext.Post)
- Context: `ManualResetValueTaskSourceCore` with a captured SynchronizationContext allocates a Tuple per Post.
- Decision: a call made on a shard records that shard (`CompletionThread`): a handler's ask (`AnswerCall`, pooled per
  shard) has its reply, refusal or timeout posted to the shard's dispatch queue and turned there into the asker's
  `Answered` mail (ADR-081). A call from outside the worlds (HTTP, tests, hosted services) is a pooled
  `PendingCall<T>` (`IValueTaskSource`) completed on the thread the reply arrives on. A `ShardSynchronizationContext`
  is installed on shard threads, so a continuation that reaches one is marshalled back to it.
- Consequences: zero allocation per call; ExecutionContext/AsyncLocal does not flow across calls.

## ADR-005 Wire format
- Decision: fixed 24-byte little-endian header (length, kind, status, hops, flags, target, message id, key), the
  Nino-serialized message, then trailers named by the flags. Three kinds: `Message` (a request when it carries a call
  id), `Reply` and `Control` (link protocol). The target is always set: the handler type id of an actor or service,
  or an address scope (sessions, client groups), so a receiver routes a frame with one lookup. The call id is a
  4-byte trailer (`HasCall`): the payload is serialized before routing picks the call id, and every hop that relays a
  request rewrites it in place, so neither needs to move the payload. Message id = FNV-1a 32 of the message type's
  full name (or its `[NinoFormerName]`, as Nino's `NinoTypeId`), computed once per type at run time and by the analyzer at build time; handler type
  ids are FNV-1a of the actor id type or service type name.
- Consequences: client-facing nodes route by patching header fields without parsing payloads; collisions are build errors
  (DAS004) and registration errors, resolvable with `[NinoFormerName]`. A request costs the same bytes as a one-way message
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
- References: highest-random-weight (rendezvous) hashing, Thaler & Ravishankar, "Using name-based mappings to increase
  hit rates" (1998).

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
- Decision: DaServer's single-threaded executor is a `Shard`; each shard owns one EasyEcs `World`. Handlers reach the
  world and the cluster through their `Context` (`ctx.World`, `ctx.Cluster`, `ICluster`); systems through the
  `ShardContext` singleton (`world.Cluster`).
- Consequences: no name clash; "world" always means the ECS world.

## ADR-012 Source generation instead of registration code
- Decision: each generator writes one kind of code. `AskGenerator`: a `{Request}Ask` class of typed `Ask` overloads
  per request. `RegistrationGenerator`: one `DaServer.Generated.Registration_{Assembly}` class per assembly whose
  static constructor runs the registrations of referenced assemblies, then registers the assembly's `[FromClient]`
  messages, client handler classes and, with the server runtime, its actors and services (one dispatcher per
  `[Handle]` method) and EasyEcs systems; `[assembly: GeneratedRegistration(typeof(..))]` names it.
  `PolyfillGenerator`: `IsExternalInit` for contract
  assemblies built for netstandard2.1.
- Registration runs in a static constructor because that runs exactly once on every runtime, Unity's IL2CPP included:
  `Registrations.Run(assembly)` triggers it (the client SDK does on `Register(handler)`, and every registration
  triggers those of the assemblies it references). On .NET the class also triggers itself from a module initializer
  when its assembly loads, so a server needs no call; targets without module initializers (netstandard2.1) get none.
- Consequences: no reflection, no configuration strings, no hand-maintained lists; misuse is a build error
  (the DAS rules, ADR-016).

## ADR-013 The shard runtime is ECS
- Context: an imperative shard loop (drain a queue, sweep in-flight work, run timers, tick) with ad-hoc per-actor
  queues hides state in procedures. A useful idea from such servers: actors are entities with their own mailbox, drained by
  systems; infrastructure is systems over singleton state.
- Decision: `Shard` keeps only the thread, the cross-thread dispatch queue and the wake/tick loop. Loaded actors are
  entities (`ActorKey`, indexed; their mailbox as a `DynamicBuffer<Envelope>`; lifecycle tags, ADR-037; known
  sessions as child entities), shard-wide state is singletons (`ShardActivity`, `ShardTimers`, `AnswerCalls`,
  `ShardStore`). `TimerSystem`, `DispatchSystem`, `LoadSystem`, `RemovalSystem`, `UnloadSystem`, `MailSystem` and the
  persistence and handoff systems run in the `ShardMessages` system group on every wake, ordered with `[UpdateAfter]`
  along the data flow; `RebalanceSystem` in `ShardRebalance`; `IdleSystem`, `ReplySlotSystem`, `ChangeSystem` and
  `CheckpointSystem` in `ShardIdleScan` (ADR-039); user systems run in the default group at the tick rate.
- Consequences: the runtime obeys the ECS rules it asks users to follow (ADR-081). Per message: one index lookup and
  the handler; a live actor without waiting mail never touches its mailbox. Every system visits only the entities in
  the state it handles, so no system scans all actors per wake. Measured: zero allocation per cross-shard call.

## ADR-014 Turn-based actors
Replaced by ADR-081.

## ADR-015 Orchestration with Aspire
- Context: hand-written Dockerfile, compose and Kubernetes files duplicate the topology (ports, seeds, projects) and
  drift from the code.
- Decision: `DaServer.Aspire` describes a cluster in the AppHost (`AddDaServerCluster`, `AddNodes`, `WithClientConnections`,
  `WithClientEndpoints`). The same model runs locally with the dashboard and publishes Docker Compose or
  Kubernetes through Aspire publishers. Nodes export logs and metrics over OTLP when Aspire (or any collector)
  configures it. There are no hand-written deployment files.
- Consequences: one source of truth for topology. Run locally, ports are assigned deterministically per node and
  replicas are separate resources (cluster members are addressed individually); published, a group is one workload
  with replicas that the platform scales (ADR-030).

## ADR-016 Analyzer separate from generators
- Context: generators that report diagnostics carry symbols and locations in their models, which defeats caching.
- Decision: generators use syntax-only predicates and equatable, symbol-free models with tracking names, and report
  nothing. `DaServerAnalyzer` reports the DAS rules (collisions in a compilation-end action), and reads symbols
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
  (`Publish`) travel as `Message` frames: a message to an actor goes to the owner of its id and runs on the actor's
  entity, one message at a time (waiting in its mailbox while it loads; loaded on demand); `Publish` serializes once
  and sends one frame per service type handling the message, local node first. Delivery is at most once and ordered with the calls of the same sender.
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

## ADR-021 A node's Program.cs is short and written by hand
- Context: a node needs a host (configuration, logging, shutdown, the HTTP plane). Orleans (`UseOrleans`) and Aspire
  (`AddServiceDefaults`, `AddNpgsqlDataSource`) put this in a short `Program.cs`, one line per capability.
- Decision: a node project has a `Program.cs` of five lines: `WebApplication.CreateBuilder`, `AddDaServer`, `Build`,
  `MapDaServer`, `Run`. A capability is one more line (`AddPostgreSqlActorStore("actors")`). No entry point is
  generated, and no capability turns on just because a package is referenced.
- Alternatives: a generated entry point when the project has none. It saves five lines, but a node then has no visible
  place to add a store, an endpoint or a service, debugging has no start, and capabilities would have to turn on by
  reference (a hidden rule).
- Consequences: what a node runs is readable in one file. Names, ports, seeds and connection strings come from the
  AppHost (environment).

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
- Decision: `ctx.BindSession(session)` also puts the session in the actor's `Group`; `UnbindSession` and close leave
  it. Every loaded actor records the sessions it knows (its callers and the sessions it bound or granted) as child
  entities (`KnownSession` + `ChildOf`, indexed by session; tags `Known`, `Granted`, `Superseded`), which go with the
  actor; when a session closes the client-facing node publishes `SessionClosed` to each actor holding it, and when a
  client-facing node is declared dead every node closes that client-facing node's sessions for its own actors.
  `ctx.Caller` (the origin, ADR-047) is the session that sent the current message.
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

## ADR-026 Clients connect through the platform's load balancer
- Context: clients need one well-known address, and a node group scales by replica count. Kubernetes and the clouds
  give it as a load balancer in front of the client-facing pods, whose own addresses are internal; Orleans keeps end
  users off its silos, Nakama puts every socket behind the balancer. Answering "connect to that node" instead (Photon,
  Colyseus) needs a public address per node (a host port, a balancer per pod) and re-implements the balancer's job
  in the cluster. A balancer spreads connections only when they open: after a scale-out long-lived connections stay
  where they are, which is why gRPC servers and Envoy close connections past a maximum age.
- Decision: a client connects through one or several entry addresses (the balancer, a DNS name: every resolved
  address is tried; reconnects use them again) and stays on the node it reached; actors are reachable from any
  client-facing node, so no client needs a particular one. `ClientConnectionOptions.MaxConnectionAge` (off by default,
  jittered by a tenth, on the node's clock) retires connections older than it, and a leaving node retires its
  connections in waves (D3): the node sends `ControlOp.Retire`, the client resumes its session
  (ADR-036) on a new connection through the entry addresses while the old one keeps working, switches, and seals the
  old one with its goodbye as the last frame (`IFrameChannel.SendLast`): what was sent on it before reaches the node
  first, a send that still picked it is refused there and goes on the new connection (its call registered again
  there: a call belongs to the connection its frame went on, so the old connection's close fails only its own calls),
  and the old connection closes once the calls made on it are answered. Pushes keep each actor's order: the new
  connection's wait until every actor that took the new session (the resume's reply counts them) sent `SessionMoved`
  on the old one, behind its last push there; then the goodbye goes, and the old node takes the session out of its
  groups before answering. `ClientMoveTests` force each interleaving
  against a scripted node. The seal is the fence, as HTTP/2's GOAWAY and gRPC's draining connection
  (no new stream on a draining connection, open streams finish): the client waits for the markers and the goodbye's
  answer (bounded by its call timeout), never for a guessed delay, so a slow or preempted device is as safe as a fast
  one. Make before break: no push or call is lost, and
  after a scale-out the old connections spread over the new nodes too. The node decides: a client that does not move
  within `RetireGrace` (10 s) is closed and reconnects as after a lost connection; a client without a session to
  resume is closed at once. The new connection is admitted by its signed, single-use ticket alone.
- Alternatives: the cluster names the node to connect to (removed: a second balancer, with an address per node the
  platform does not give); shedding only above the group's average (the target adopts it, ADR-089: an age also
  moves the clients of a balanced group); an edge tier in front of the client-facing nodes (ET's router: another
  hop; for UDP transports).
- Consequences: one public address and one TLS/DDoS surface per node group; clients without a resume ticket (not
  logged in) are disconnected at their age and connect again themselves. The balancer, its health checks and its
  draining are the platform's (S4 publishes them).

## ADR-027 Bounded queues refuse instead of growing
- Context: unbounded dispatch queues and mailboxes let a flood (or a slow actor) grow memory until the process dies,
  and callers learn nothing.
- Decision: `NodeOptions.DispatchQueueCapacity` bounds the new calls and messages in a shard's dispatch queue and
  `ActorMailboxCapacity` an actor's mailbox (an actor type sets its own with `[Actor<TId>(MailboxCapacity = n)]`: a hot
  map or sale takes more, an actor that should shed load early less; mail waits there only while the actor loads,
  leaves or works off a backlog, ADR-081); a call or message beyond either is refused with
  `RpcStatus.Overloaded` (requests get the status, one-way messages are dropped), counted by reason in
  `daserver.messages.dropped{reason=queue_full|mailbox_full|deleting}` (`Node.Dropped`; `deleting`: one-way mail to a
  root being deleted, expected, ADR-099). Node links are
  never paused while a target shard is full: the link from B to A also carries replies to A's own calls, so pausing it
  stalls the work waiting for those answers until their timeouts, which can block the very shard that is full.
  Completions of a shard's own asks and runtime callbacks finish work already admitted, so they have a queue of their
  own (16k slots per shard, drained first): sharing the full queue with new work would push them into an unbounded
  overflow exactly under overload. They are bounded by the asks in flight; a burst beyond the slots spills into a
  segment queue once.
- Consequences: memory is bounded under any load; overload is visible to callers and in metrics, and refusing early
  is cheaper than TCP backpressure. Refusals are status-only (ADR-049).

## ADR-028 Request deadlines travel as a budget
- Context: a caller that timed out may still have its request run (and relayed further) after it gave up, so a
  backlog keeps doing work nobody waits for and recovery after a spike takes longer than the spike.
- Decision: a request's header status byte (unused on requests) carries the caller's remaining time in 100 ms units
  (up to 25.5 s). Every hop turns it into a local deadline on arrival (`Environment.TickCount64`, no clock sync) and
  forwards what is left; clients send their call timeout. A shard drops a request whose deadline passed before it
  runs: no reply (the caller has already failed it), counted in `daserver.messages.dropped{reason=expired}`.
  A call's own timeout (`CallOptions`) sets its first deadline instead of the call timeout; a handler reads it as
  `ctx.Deadline` (on `Platform.Milliseconds`), and a reply it keeps for later (`ctx.ReplyLater()`) keeps that deadline:
  `ReplySlotSystem` forgets it once its caller stopped waiting.
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
- Consequences: simple and ordered with binds (ADR-022). In the target model gateways hold full subscription sets
  (roadmap W5), so a push goes only to the gateways with members (drafts/programming-model.md §9); this record is
  rewritten when W10 promotes the drafts.

## ADR-030 Run: a resource per replica; published: a workload per group
- Context: cluster members are addressed individually: each gossips its own cluster endpoint and links go to a
  specific node. Run locally, Aspire's `WithReplicas` puts replicas behind one proxy with shared endpoints. Published,
  a resource per replica becomes a Compose service or Deployment per replica, with container names, fixed host ports
  and seeds naming each one: the platform cannot scale it, and a replica it adds would find nobody.
- Decision: run mode creates one resource per replica (`gateway-0`, `gateway-1`, ...) with its own ports, every node
  seeded with every other one. Publish mode creates one workload per group with its replica count (a Compose service
  with `deploy.replicas`, a Kubernetes Deployment): every replica listens on the same ports, is named after its host
  (`Environment.MachineName`: the pod's name) and advertises its own address (a node in a container advertises the
  container's IP when no host is configured), and the seeds are each group's DNS name, which resolves to every replica
  (a Compose service name; on Kubernetes a headless `{group}-peers` Service that lists pods before they are ready, since
  a node joins before it is ready). A seed that resolves to several addresses yields one seed per address; a node skips
  itself. Client entries are the group's own name (the platform's Service or load balancer in front of the replicas).
- Consequences: replicas are visible and individually addressable on the dashboard; published deployments scale with
  the platform's tools (`kubectl scale`, an HPA, `docker compose up --scale`), and nothing is configured by hand. No
  StatefulSet: a node's identity is its gossip incarnation and its actors' state lives in the store, so pods need no
  stable names or volumes (actor-hosting groups become StatefulSets with roadmap W11, ADR-085). No Kubernetes API
  access on the data plane (no RBAC): DNS is the only discovery (ADR-085's hot-restart command is the one API user).
- Seeds resolve through `NodeOptions.SeedResolver` (`ISeedResolver`), in the background while a node is alone: gossip
  rounds never wait for DNS or a registry. The default reads "host:port" and expands DNS names to every address;
  `DaServer.Hosting` resolves seeds written as service names (`tcp://peers`) through .NET service discovery
  (`Microsoft.Extensions.ServiceDiscovery`): configuration such as an Aspire reference writes, DNS SRV records with its
  DNS provider (a headless Service's pods), or any registry that has a provider. The AppHost keeps passing plain DNS
  names (ADR-030 above); service names are for clusters wired another way.

## ADR-031 Gossip carries pulses, cards travel once
- Context: serializing the whole member table every round costs about 10 KB/s per idle node at one round a second,
  growing with the square of the cluster size, and allocates new member objects on every receiver; a view that keeps
  the load hints of the last status change never shows client-facing nodes filling up.
- Decision: a member is its card (identity, endpoint, hosted handlers and address scopes, its features' card
  entries: fixed while it runs) plus its pulse (incarnation, heartbeat, status, load and three feature slots: 37 bytes). Gossip and its reply carry pulses, written into a pooled frame and read in place; a pulse naming
  an unknown node is answered with `CardRequest`/`Cards`. Pulses update members in place; incarnation and status
  changes publish a copy and rebuild the view.
- Consequences: a steady-state round allocates nothing (allocation test at 300 rounds/s); idle nodes in the 10k
  topology allocate 0 KB/s; gossip size is 37 bytes per member. Load hints are fresh within a round.
- References: gossip-style failure detection by heartbeat counters, van Renesse, Minsky & Hayden, "A Gossip-Style
  Failure Detection Service" (1998); incarnation numbers to refute a suspicion, as in SWIM (Das, Gupta & Motivala,
  2002, section 4.2). There is no SWIM ping / ping-req probing: SWIM's probe parameters do not apply.

## ADR-032 Names from the distributed-server vocabulary
- Context: names borrowed from one framework (Orleans' `OnActivateAsync`) or describing a mechanism instead of its
  role read wrong to people who know other distributed servers.
- Decision: name things by their role in a cluster, in pairs where they are pairs: `Loaded` / `Unloading` (the actor
  is loaded on / leaves this node; `Created` when it has no record yet), `[FromClient]` / `[ToClient]` (the two
  directions across the client-facing node),
  `Caller` (who sent the message from outside; invalid for server calls), `Disconnect` (not a game's "kick"), dispatch (message
  dispatch only), `ClusterMember` (no clash with
  `System.Reflection.MemberInfo`), `ListenAddress` / `DispatchQueueCapacity` (what they configure). An actor's
  components start as their defaults when a handler first takes them (a `Created` handler gives a new actor its first
  data), and systems read an actor's id and group from its public `ActorKey` component instead of copying them into
  state.
- Consequences: type ids do not depend on these names (message and handler type names are hashed).

## ADR-033 Actors unload when idle, by their own declaration
- Context: actors that stay loaded until their key moves make actors used now and then (offline players, finished
  matches) hold memory forever.
- Decision: `[Actor<TId>(UnloadAfterIdleSeconds = n)]` (default zero: never). Actors that set it get an
  `UnloadWhenIdle` component; an `IdleSystem` scans only those, ten times a second, and unloads the ones without mail
  for that long through the same path as rebalancing (`Unloading` as the last mail; mail arriving later loads the
  actor again). `LastMail` records when the actor last took mail.
- Alternatives: a node-wide timeout (would unload maps and realms that are busy in systems but receive no mail).
- Consequences: one store per delivered message (`LastMail`); RPC benchmark unchanged.

## ADR-034 One write per connection per batch
- Context: a send loop that wakes on the first queued message gives a session in the 9 interest cells around a
  player up to 9 writes per update. Profiling the 10k-in-one-map scenario put a third of gateway time in send loops
  (socket writes), against 4% in the fan-out copy.
- Decision: `Connection.BeginSendBatch` (thread-local, nestable) defers send-loop wakes to the end of the batch. The
  receive loop batches the frames of one receive buffer; a shard batches one wake (messages and tick).
- Alternatives:
  - A timed flush that wakes each connection's send loop: gateway `sendto` calls fell 42%, but one beat thread waking
    thousands of send loops cost more thread-pool wake-ups than the writes it saved (gateway CPU 45% -> 58%). The beat
    that writes directly instead, without waking anything, is ADR-070 (opt-in).
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
  - Handlers are static `[Handle]` methods of actor and service classes (ADR-081; and instance methods of any class on
    clients, for pushes). The message type decides the handler: the cluster gossips `(message id, handler type id,
    actor)` per handler.
  - `Ask(id, m)` / `Send(id, m)` go to the actor `id` (its id type names the actor type, ADR-043); without an id, to
    the one service handling the message (several service handlers is an error: `RpcStatus.Error`, cold path). `Publish(e)` reaches
    every service type handling it once.
  - A `[FromClient]` message to an actor goes to the actor bound to the client's session for that actor type, or to
    the actor the client names when that actor granted the session (ADR-051); the actor binds or grants itself:
    `ctx.BindSession(session)`, `ctx.GrantSession(session)`, answered once the client-facing node applied them (a reply
    that must follow, `ctx.ReplyAfter(ctx.BindSession(session), value)`, finds the binding in place).
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
  its entry addresses with a jittered backoff and presents the ticket. The ticket only
  names what the session held: the node asks the actors in it (`SessionResumed(Session, Previous)`, answered by the
  runtime before the actor's handler runs), and an actor accepts only while it holds `Previous` bound or granted (the
  `Granted` tag of its `KnownSession` entity). The actors are asked in the ticket's order until one accepts, then the
  rest at once: an actor that already took `Previous` for another session answers `Superseded`, so a ticket presented
  on two connections at once resumes on one and the other stops. A resume whose new connection closed before it
  completed (the client gave up waiting) is reverted: the node sends the actors that took it a resume back to the
  previous session, which they restore without running their handler, and the client, retrying on `Superseded`,
  resumes with the same ticket. So a ticket resumes at most once, a revoked grant or a closed session
  (past the grace) does not come back, and an unloaded actor (which forgot its sessions) refuses without loading. The
  new session takes over what was accepted and the actors' groups, then the client gets its reply, so its next
  message reaches actors that know the new session; a ticket nothing accepts is refused. Actors swap the session and send
  the full state; after a lost connection, pushes sent meanwhile are lost by design (state, not a log); a planned move
  (ADR-026) keeps the old connection until the actors switched, so it loses nothing. A lost connection is reported as
  `SessionClosed` only after `ResumeGrace` (30 s), also for a crashed client-facing node; the runtime drops the late
  `SessionClosed` of a resumed session, and forgets a closed session in every actor holding it (loaded, or arriving with a
  handoff record: the close and a resume wait behind it), whether or not the actor handles `SessionClosed`. An explicit close (client dispose, `Disconnect`) is reported at once; an
  actor kicks with `DisconnectSession`, which withdraws the session's grant before the close is sent, so a resume
  waiting in its mailbox is refused (DAS020 for a `Disconnect` sent by an actor). Tickets
  live `ClientConnectionOptions.TicketLifetime` (10 min); clients refresh theirs at half of it.
- Alternatives: resumable client streams like node links (ADR-008) keep per-session replay buffers on client-facing nodes, which
  is memory per connection and lost with the client-facing node anyway; a session store (Redis) adds a dependency and a hop.
- Consequences: without a signing secret there are no tickets and a lost connection closes the session at once. A
  ticket is a bearer token for one resume within its lifetime, carried on the same connection as the login; a stolen
  ticket resumes a session the client still holds (transport security is the platform's, TLS). The ticket carries the
  session's grants and bindings (ADR-051) and their actors' groups; other groups are rejoined by the actors on
  `SessionResumed`.

## ADR-037 Actor lifecycle is tags; each system reacts to one
- Context: ready queues with `Scheduled` flags, lifecycle enums and static procedures called from several systems use
  ECS storage without ECS style: the state lives in code paths instead of data.
- Decision: state changes are data, each system reacts to one state.
  - Lifecycle tags: `Ready` (mail waiting), `Loading` (its record is being restored; `AwaitingRecord` while the store
    is asked), `UnloadRequested` (asked to leave, by `RebalanceSystem`, `IdleSystem` or a stop), `Leaving` (its
    `Unloading` mail ran; its record is written or handed on), `Fenced` (removed without `Unloading`, ADR-042) and the
    `Removing(status)` component. `LoadSystem` restores records and makes actors live (`Loaded` or `Created` mail
    first), `UnloadSystem` runs `Unloading` and hands off or saves, `RemovalSystem` destroys the entity and routes its
    mail again, `MailSystem` runs the mail of live `Ready` actors.
  - Handlers run to completion (ADR-081), so no system waits for one: a stopping shard is drained once no actor is left.
  - The sample follows the same rule: an avatar without `GridCell` is not placed yet; `SentMotion` (last sent velocity
    and cell) is compared each tick instead of a flag; leaving is the `Despawning` tag handled by `DespawnSystem`; the
    grid is `MapGrid` data on the map entity, its steps are systems (`PlacementSystem`, `MotionSyncSystem`,
    `SnapshotSystem`).
- Alternatives: a ready queue avoids the archetype move per idle-to-ready transition, but it is a second
  representation of the same state; benchmarks showed no measurable difference.
- Consequences: no system scans all actors and nothing polls; every lifecycle step is a filter that tests and users
  can read.

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
  `RebalanceSystem` and `IdleSystem` are plain `[ForEach]` methods; samples use `UpdateInterval`; `ActorKey` alone
  identifies a loaded actor (id, group); time-based logic is a component with a due tick and a system (no timer queue
  on the shard).
- Consequences: no co-development checkout in builds or CI; EasyEcs changes reach DaServer through releases.

## ADR-040 One noun per concept
- Context: one word for two things ("link" for a node connection and for an entity's pointer to its actor;
  load/unload next to activate/deactivate/retire for one lifecycle) makes code and docs ambiguous.
- Decision: `ActorKey` is the actor component (id, client group); "link" is only a node connection. `HandlerType`
  (`HandlerTypeInfo`, `HandlerTypeId`) is an actor or service type with `[Handle]` methods. The lifecycle is load/unload
  only: `Loading` (its record is restored), `Loaded` / `Created` (its first mail), `UnloadRequested` (asked to leave),
  `Unloading` (its last mail), `Leaving` (it goes), `Removing` (entity destroyed, mail routed again); a running actor
  is a "loaded actor". The shard's cross-thread queue is the dispatch queue (`DispatchSystem`, as the Akka/Orleans
  dispatcher); the per-actor queue is the mailbox (`DynamicBuffer<Envelope>`, `MailSystem`).
- Consequences: one term per concept in code, docs and tests.

## ADR-042 A node declared dead drops its actors without unloading them
- Context: a node that learns it was declared dead (a partition, a long pause) and unloads its actors normally runs
  their `Unloading` mail and saves after the cluster has already given their keys to other nodes: a stale save could
  overwrite the new owner's.
- Decision: on Dead every loaded actor is tagged `Fenced` and removed without its `Unloading` mail and without a save;
  its mail is routed again to the new owner. The node then rejoins with a higher incarnation (as Orleans fences a
  silo that sees itself dead).
- Consequences: work since the actor's last save is lost on that node, which is the price of a partition. A write
  already in flight is refused by the store's versions (ADR-068).

## ADR-043 Typed actor ids name the actor type
- Context: with a bare `long` key whose meaning depends on the actor type handling the message, a player id can be
  passed where a map id is meant, a message handled by two actor types is ambiguous, and a keyed publish reaches every
  actor type at the same number.
- Decision: each actor type declares its own id type in contracts (`readonly record struct MapId(long Value) :
  IActorId`) and names it on its static class, `[Actor<MapId>]` (`ctx.Id<MapId>()` is typed). `Send(id, m)` / `Ask(id,
  m)` route by the id type: its type id (FNV of the full name) is the actor's handler type id, so placement and the
  wire use the id type and messages need no owner. Two actor types may not share an id type. Every message carries the
  handler type id in the header's target (ADR-005). `Publish` reaches services only; actors are told one by one with
  `Send`. A client may name an actor only if that actor granted its session (ADR-051); otherwise the session binding
  decides.
- Alternatives: `long` keys with a generated per-actor proxy (`Cluster.Map(id).Enter(..)`), which ADR-035 rejects; the
  id inside the message (an `[ActorKey]` member), which couples messages to one receiver.
- Consequences: the compiler checks targets, ids are plain data that travel inside messages (`Enter(PlayerId)`), and
  the same message can be handled by several actor types. Ids are one `long` (checked once per id type).

## ADR-044 Actors, services and systems hold no fields (DAS007)
- Context: only components and singletons are persisted, handed over and kept across a reload; state anywhere else
  silently loses its value on those paths, and a mutable static field is shared by every shard thread.
- Decision: actors and services are static classes (ADR-081), and DAS007 makes a mutable static field of one, or a
  field of a server system, a build error. Constants and `static readonly` data stay allowed. Per-actor state goes
  into components of the actor's entity (handler parameters), shared state into singletons.
- Consequences: behaviour is code only, so persistence (N5), state handoff (N6) and code changes by hot restart
  (ADR-084) touch data alone and can swap the code freely. There is no actor object to create, pool or leak: a load creates an entity, and actor
  churn (logins, short matches, idle unloads) allocates nothing.

## ADR-045 DaServer is a general ECS distributed server framework
- Context: a framework for high-concurrency backends must not read as one game server, yet it must cover what every
  real-time backend needs.
- Decision: DaServer is a general-purpose ECS distributed server framework for high performance and high
  concurrency. The framework covers the capabilities any such backend needs: ECS shards, actors and services,
  messages and RPC, membership, placement, persistence, reload, ingress and client connections (sessions, push,
  groups, resume; chat, IoT, market data, collaboration and games all need them, ADR-048). Business domains are not
  part of it: logins, maps, interest management, matchmaking, inventories live in samples (`samples/mmo`;
  `samples/flashsale` planned, G5) or user code. Persistence (N5) is in; code changes ship by hot restart (ADR-084).
- Consequences: a capability enters the framework only if backends of different domains need it; it is built on the
  same public extension points users have (node features, ingress, address handlers, filters), lives in its own
  namespace and is off unless configured. The runtime (`DaServer.Runtime`, `DaServer.Cluster`, `DaServer.Rpc`) never
  depends on a capability namespace (`DaServer.Clients`); an architecture test checks it.

## ADR-046 Two planes per node: DaServer TCP for data, ASP.NET Core for HTTP
- Status: minimum implemented (Kestrel on `DaServer:HttpPort`, `MapAsk`/`MapSend`, `/clients/ws`);
  health, admin endpoints and Aspire probes in roadmap A1.
- Context: Kubernetes probes and Aspire health are HTTP; operators want admin commands; applications want REST
  endpoints (webhooks, payment callbacks, HTTP login, back-office). A hand-written HTTP responder would re-implement
  Kestrel and the official health-check middleware.
- Decision: every node hosts Kestrel on a separate HTTP port for health (`/health` readiness, `/alive` liveness),
  admin endpoints and user REST endpoints, all as minimal APIs (no MVC controllers: lighter, AOT/trim friendly, and
  one endpoint source; code changes by hot restart, ADR-084). Node-to-node and client traffic stays on DaServer's TCP links (zero GC,
  ordered, batched). HTTP handlers run on the thread pool and reach actors only through `ICluster`.
- Consequences: nodes reference the ASP.NET Core shared framework (aspnet runtime image, a few MB of memory, tens of
  ms of start-up). The HTTP plane is for low-frequency traffic only: probes, admin, internal REST (webhooks, payment
  callbacks, back-office, an HTTP login). A request there crosses two protocols (HTTP parsing, middleware and JSON,
  then a cluster message) and costs a `recv` and a `send` of its own (HTTP/1.1 does not batch), and it allocates like
  any ASP.NET request. Client traffic at volume, per-player or per-tick traffic and anything a crowd sends goes over
  the client protocol (`DaServer.Clients`: TCP, or WebSocket at `/clients/ws` for browsers, behind the platform's
  TLS as wss), which batches a connection's frames into one write. Measured with the flash sale (one 4-core VM, a
  million buyers, the crowd on the same machine): HTTP 22,600 requests per node core with the defaults below, the
  client protocol 79,000 (TCP) per node core, 3.5 times more. The node's defaults keep what HTTP costs low: hosting
  diagnostics off (any level enabled there starts an Activity per request whether or not anything traces, 18,100 ->
  22,600 requests per core) and no `Server` header. Health endpoints (always on) and admin endpoints (authorized with
  the signing secret) come with A1. `MapAsk` and `MapSend` read JSON, or Nino for a caller that sends
  `application/x-nino`, and answer as the caller accepts (Nino.AspNetCore): the message's own bytes, typed for .NET
  and TypeScript callers (Nino.TypeScript generates the same codecs), and a one-way Nino body goes on as it came after it is checked (400 when it is not the message).

## ADR-047 One message model: message, address, optional reply
- Context: messages and RPC are one thing; an API with a verb per target (push to a session, broadcast to a group,
  join, leave, disconnect, bind) and a wire with a frame kind per verb, where one field means either a call id or a
  target, grows with every scenario, and most such verbs are one scenario (client sessions) in the core.
- Decision: four concepts: message, address, optional reply (a request is a message whose type declares a reply),
  origin (who caused it; set only by ingress). Addresses are structs: `IActorId` (keyed, rendezvous), the one service
  (balanced), `INodeAddress` (pinned to a node, handled by the feature owning its scope) and `IGroupAddress`
  (fan-out to the nodes advertising its scope). Verbs: `Send` and `Ask` (kept apart, as Akka Tell/Ask: an ask
  registers a call with a timeout whose answer comes back as mail, so the call site must show it), `Publish` (every service type handling the message; no
  address value), `SendAfter` (N2). Wire: `Message`, `Reply`, `Control` (link protocol only); `Target` always set;
  trailers by flag. One verb per target kind is not needed: the address type names the target at the call site (as
  the id type does since ADR-043), and DAS006 checks `[ToClient]` by address kind. The origin is an address itself
  (`Origin(Node, Id)` implements `INodeAddress`): `ctx.Send(ctx.Caller, m)` reaches whoever sent the message (the
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
  `ClientGroup` addresses, push and fan-out, resume tickets, a maximum connection age) are
  namespaces of the `DaServer` assembly, built on the public feature points. A node accepts clients when a client
  port is configured; "gateway" is only a deployment word for such a node group (co-hosted in small clusters,
  dedicated in production). There is no balancer role: the well-known address is the platform's load balancer or DNS
  (ADR-026). The HTTP plane (ADR-046) is the
  second ingress consumer; WebSocket clients connect through it. Packages: ADR-052. Waiting rooms are not part of the framework: they are policy for human traffic
  surges, and compose from the ingress primitives plus a queue actor (`samples/flashsale`).
- Consequences: dedicated client-facing nodes may carry high request volume (the actor interior stays zero-GC;
  ADR-046). Abstractions and the client SDK use newer runtime features only behind netstandard2.1 fallbacks (ADR-001).

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
  groups, tickets, session operations, client messages entering the cluster), and an actor part, on
  every node hosting actors (the sessions each actor knows, `SessionClosed` when they end, even when the node holding
  them crashed, `SessionResumed` swapping a resumed one). A feature runs only where it is configured, and the runtime
  may not depend on `DaServer.Clients`.
- Decision: the edge part is `ClientConnections` (`DaServer.Clients`), a `NodeFeature` enabled by the
  `DaServer:Clients` section (`ClientConnectionOptions`): it registers the session, origin and client group address scopes,
  publishes its endpoint as a card entry and its session count as a pulse slot, and sends every client message
  through the ingress. The actor part stays in the runtime (`ActorSessions`): it needs only contract types
  (`ClientSession`, `SessionClosed`, `SessionResumed`, `SessionAccess`), records an actor's callers and bound or granted
  sessions, handles
  closes and resumes before the actor's handler runs (not as dispatch filters: a user's filter could run first and
  refuse a close, leaving the closed session resumable) and closes the sessions of a dead node holding the
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
- Decision: the actor decides who may address it, once: `ctx.GrantSession(session)` / `ctx.RevokeSession(session)`
  (an `Ask` of `SessionAccess` to the session, like `BindSession`, which is a grant that is also the session's actor
  of its type). The client-facing node keeps the session's grants (at most 64, an immutable array scanned lock-free)
  and refuses a message naming any other actor with `Forbidden` before it enters the cluster. A grant puts the
  session in the actor's `Group`, the runtime forgets or moves it on the actor's close and resume (an actor that handles
  `SessionClosed` / `SessionResumed` receives them too), and the resume
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
  - `DaServer.Abstractions` (netstandard2.1, net11.0; namespace `DaServer`: one using in a contract file): what
    contract assemblies reference: `[Message]`, `[Handle]`,
    `[FromClient]`/`[ToClient]`, `IRequest`, addresses (`IActorId`, `ClientSession`, `ClientGroup`,
    `Origin`), `IMessageSender`, session messages, type ids, the client message registry and generated-registration
    support.
  - `DaServer.Core` (netstandard2.1, net11.0): the wire and transport both sides run: `FrameHeader`/`RpcStatus`/
    `ControlOp`, `FrameWriter`/`BufferPool`, `Connection`/`WebSocketChannel`, pending calls (`CompletionThread`: a thread
    that completes its own calls, a shard on servers), `CallContext`, `RpcException`, handler dispatch,
    `SignedToken`.
  - `DaServer.Client` (netstandard2.1, net11.0): `ClusterClient`.
  - `DaServer` (net11.0, ADR-054): the server runtime (shards, actors, services, cluster, ingress, client connections), plus
    `DaServer.Hosting` and `DaServer.Aspire`.
  - `DaServer.Simulation` (net11.0; on Client and EasyEcs, not the server): simulated users for end-to-end,
    load and behavior tests (ADR-056).
  The generator and analyzers ship with Abstractions, so every assembly using DaServer gets them, in two builds of the
  same sources as Nino ships its own: against Roslyn 5.9 (`analyzers/dotnet/roslyn5.9`, the .NET 10.0.4xx and 11 SDKs)
  and against Roslyn 4.3 (`analyzers/dotnet/roslyn4.3`, the oldest Unity supports); NuGet gives each compiler the newest
  build it can run. What they emit for an assembly without the server runtime (contracts, client handlers) is C# 9,
  which Unity compiles; server assemblies get current C#. Newer BCL APIs are
  used on .NET and replaced on netstandard2.1 (`Platform`, internal polyfills of `Lock` and `FrozenDictionary`), so
  servers pay nothing for the shared code. An architecture test enforces the references.
- Alternatives: transport inside the client package (the server would depend on "Client"); inside Abstractions
  (every contract assembly carries sockets); a separate client transport (two implementations of one protocol).
- Consequences: a Unity client references Abstractions, Core and Client (one package reference, the rest are
  dependencies); contract projects reference Abstractions only. Roadmap W2 merges the packages (ADR-086).

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
- Decision: the server core targets `net11.0` only; code both sides load (Core, Client, users' protocol and shared
  rules) targets `net10.0` and netstandard2.1 (target, W2; today netstandard2.1 and `net11.0`) (ADR-086: `net10.0` for Godot 4 .NET games, which cannot load a
  `net11.0` assembly; netstandard2.1 for engines below .NET 10). `global.json` pins the 11.0 SDK (RC2 until GA,
  then the GA band); CI installs it from there. Runtime async (`<Features>runtime-async=on</Features>`, preview) is on
  for every `net11.0` build (`Directory.Build.targets`; a project opts out with `UseRuntimeAsync=false`: the
  benchmarks, whose `[AsyncMethodBuilder(PoolingAsyncValueTaskMethodBuilder)]` methods it would ignore, and the
  PostgreSQL store, whose pooled builders keep a batch write at 0 B). `Reply<T>`, what code outside the shards
  awaits (ADR-062), is a custom task-like whose pooled builder runtime async leaves alone. Measured on RC2 (a method
  two calls deep): completing synchronously, `Reply<T>` 20.3 ns, runtime-async `ValueTask<T>` 21.2 ns, a compiler
  state machine 26.6 ns, none allocating; suspended on a reply, 240 ns / 0 B, 236 ns / 320 B and 341 ns / 336 B. So
  `Reply<T>` is as fast as runtime async and allocates nothing. Across the suite (calls, messages, broadcasts, the
  client scenario) turning runtime async on moves nothing beyond noise: the hot paths are pooled `IValueTaskSource`s,
  not async methods, and handlers never await (ADR-081). Code built for netstandard2.1 declares no
  `params ReadOnlySpan<T>` (the compiler allocates an array per call there; a span overload goes under `#if NET`).
- Alternatives: stay on .NET 10 LTS (gives up 2.3-2.7x on concurrent local calls, 1.1-1.3x remote); multi-target the
  server core for `net10.0` too (doubles its build and test time for servers that all deploy one runtime, and gives
  up runtime async there); `ValueTask<T>` under runtime async as the awaitable (no faster than `Reply<T>`, 320 B per
  suspension).
- Consequences: servers need the .NET 11 runtime (x86-64-v2 or newer); BenchmarkDotNet 0.16 (preview) hosts .NET 11.
  Nothing in the shared code may need a .NET 11 API: runtime async, socket passing for the handover and the slim
  builder live in the server core. Today's shared packages still build netstandard2.1 and `net11.0`; W2 moves them
  to `net10.0`.
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
Replaced by ADR-081.

## ADR-058 Delays are messages sent later, held by the shard
- Context: every backend needs delays (retries, cooldowns, timeouts of business flows, scheduled ticks of one
  actor), and handlers never wait (ADR-081); a timer per delay would allocate and fire on a pool thread, off the shard
  that owns the state.
- Decision: `ctx.SendAfter(delay, address, message)` (`ICluster.SendAfter` in systems; to a service too) serializes
  the message at once into a pooled frame and schedules it in the calling shard's timer wheel (`ShardTimers`
  singleton, `TimerWheel<T>`: hashed, O(1), 5 ms resolution); `TimerSystem` routes what is due exactly like a `Send`
  made then, so the message follows an actor that moved and is ordinary mail (one message at a time, bounded by the
  mailbox). A reply kept for later can ride on it: the actor sends itself its `ReplyTo` and replies when that message
  arrives. `CancelSend(handle)` cancels on the scheduling shard (its index is in the handle's top byte, another
  shard's handle is refused). The shard sleeps no longer than the resolution while a send waits; sends still pending
  when the shard stops are dropped (one-way delivery is at most once). Calling it off a shard thread throws: timers
  are a shard's data.
- Alternatives: one wheel per node (a lock or a hop per schedule); `Task.Delay` continuations (allocations, and a
  continuation off the shard); durable reminders (a store; they come with persistence, N5, and are a different guarantee).
- Consequences: `DelayedSendsDoNotAllocate` holds < 8 B per send; `TimerBenchmark` shows 0 B, a mean dominated by one
  wheel slot shared by the batch. Delayed sends do not outlive their node: a stop (a scale-in
  too) or a crash drops them; a business deadline that must outlive the node is persisted state a system checks.
- References: hashed timing wheels, Varghese & Lauck, "Hashed and Hierarchical Timing Wheels" (1987), as Netty's
  `HashedWheelTimer` and Kafka's purgatory use them.

## ADR-059 Call cycles fail at once through call chains
Replaced by ADR-081.

## ADR-060 No synchronous waits on shard threads
- Context: one thread runs every actor of a shard. A synchronous wait in a handler or system (`.Result`, `.Wait()`,
  `GetAwaiter().GetResult()`, `Thread.Sleep`, `Task.Delay`, locks) stops all of them, and a wait for a reply that the
  same shard must deliver never ends. Pooled awaitables make it worse: they cannot block, so a premature read returned
  garbage.
- Decision:
  - DAS009 (error) reports such waits in actors, services and systems, naming the replacement: `ctx.Ask`, whose answer
    comes back as a message (ADR-081), `ctx.SendAfter` or a system's `UpdateInterval` for delays, nothing for locks
    (one thread owns shard state). Handlers cannot `await` at all (DAS001).
  - Reading a pooled call's result before it completed throws instead of returning garbage.
  - A watchdog on the node's maintenance timer reports a shard pass longer than `NodeOptions.StallAfter` once per
    pass (log, `Node.StalledShards`, `daserver.shard.stalled`).
- Alternatives: a dedicated thread per blocking call (hides the bug, costs a thread); sampling the stuck thread's
  stack (not possible from managed code without a diagnostics session).
- Consequences: the waits the analyzer can see do not compile; the rest (a long loop, a wait inside a library) are
  visible within `StallAfter`. Tests that need a foreign await on purpose suppress DAS009 locally.
- The other half of the same rule, DAS015 (error): actors, services and systems start no work on other threads:
  `Task.Run`, `TaskFactory.StartNew`, `ContinueWith`, `ThreadPool`, `Parallel`, `new Thread`, timers,
  `ConfigureAwait(false)`. Their state needs no locks because one thread owns it, and code that runs elsewhere would
  touch it unguarded. The rule is all or nothing on purpose (not "only delegates that touch state"): users never weigh
  which off-thread code is safe. IO a handler needs is asked of whoever owns it, as a message. Concurrency comes from
  many actors on many shards; heavy work is split over actors or services.

## ADR-061 Systems read and write components; other worlds get messages
- Context: a system sometimes needs another entity's data or decision (may this NPC step on that cell, may this
  player take that item). Each shard is one EasyEcs world. Actors are entities of their shard's world, so their state
  is components there.
- Decision:
  - In its own world a system does what every ECS system does. It queries with `[ForEach]` and reads and writes
    components through `ref`, its own entity's or another's. Relations are `EntityId`s kept in components and
    resolved on access (`TryGetEntity`). For example, an avatar is a `ChildOf` its map's entity, and
    `PlacementSystem` reads the map's `MapGrid` through it and files the avatar under a `GridCell`. There is no message, no await and no reply: within a
    world, data and ordering (`[UpdateBefore]`/`[UpdateAfter]`) are the whole protocol (EasyEcs ecs-principles).
  - Placement decides what shares a world. Keys that must be read together are put on one shard: the map's grid and
    the NPCs that walk it live in the map's world, and N7 placement affinity generalises this.
  - Data in another world (another shard or node) cannot be referenced. A system sends an intent there (`Send`). The
    owner of the data validates the intent against its current state, applies or refuses it, and sends the outcome
    back as a one-way message, which a handler writes into components for the next tick. Systems do not `Ask`
    (DAS012): an answer that arrives ticks later describes a state that may have changed, so acting on it is a
    check-then-act race across worlds.
  This is SpatialOS's split: the authoritative worker writes, others send commands. Unity Netcode for Entities and
  Entitas sync also exchange one-way commands between worlds, applied at a tick boundary.
- Alternatives:
  - The reply as a component on the asking entity: still check-then-act on a stale answer.
  - Systems that wait per entity: per-entity state machines, interleaving with the entity's mail, and a tick that no
    longer finishes in one pass.
- Consequences: no new API. A system in the right world writes directly. Across worlds, only the owner decides, so
  an intent never applies to stale data. Handlers ask (ADR-081): an answer arrives as mail for the handler that
  decides what it means for the actor's data now.

## ADR-062 Reply<T>: the awaitable of calls from outside the shards
- Context: code outside the shards' worlds awaits replies: HTTP endpoints, tests, hosted services through `ICluster`,
  and clients through `ClusterClient`. A `ValueTask<T>` boxes an `async` method's state machine on every suspension and
  exposes `.Result`, which on a pooled call is a wait that cannot work (ADR-060). Handlers never await (ADR-081).
- Decision:
  - `Reply<T>` and `Reply` (Abstractions, namespace `DaServer`) wrap a `ValueTask`. `ICluster.Ask` and
    `ClusterClient.Ask` return `Reply<T>`.
  - Both types carry their builder (`ReplyMethodBuilder`), so an `async Reply<T>` method (a client flow, a test helper)
    keeps no box while it completes synchronously; on the first suspension the state machine moves into a box pooled
    per thread and state machine type, bounded at 4096 idle boxes, returned when its result is read.
  - The types expose `GetAwaiter`, `AsValueTask` (combinators) and `AsTask`. They have no `.Result`; a `T` converts
    to `Reply<T>`.
- Alternatives:
  - `ValueTask` with `[AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]` on every method: a line of
    plumbing each time, and a small cache.
  - A custom awaitable without `ValueTask` inside: more code for the same result.
- Consequences: an outside caller's await allocates nothing once warm (`BurstsOfCallsFromOutsideTheClusterDoNotAllocate`);
  user code names one awaitable. ExecutionContext does not flow across a call.

## ADR-063 A graceful stop has a budget, phases and readiness
- Context: platforms send SIGTERM and kill later (Kubernetes 30 s, Compose 10 s by default). A stop that saves actors
  can take longer than the platform waits, and one that hangs blocks a rolling deploy. Clients of a leaving
  client-facing node lost their connection all at once.
- Decision:
  - `NodeOptions.ShutdownTimeout` (25 s) is the stop's budget. `StopAsync` honors the host's token too. The host
    waits the budget + 5 s, and the published grace period (Compose `stop_grace_period`, Kubernetes
    `terminationGracePeriodSeconds`) is the budget + 10 s, so the platform's kill never lands while the host is still
    stopping. Past the budget the node
    logs, per shard, the actors still loaded (not unloaded and saved yet) and exits.
  - The stop runs in phases, each logged and recorded with its duration (`daserver.stop.phase.duration`): leave,
    drain, unload, close.
  - `/health` is readiness: joined, Up and not leaving. `/alive` is liveness: no stalled shard. Aspire declares both
    as HTTP probes, so `WaitFor` and the dashboard use readiness.
  - A leaving client-facing node closes its listener, then its connections in waves over `DrainSpread`. Clients
    resume their sessions on other nodes; actors see no close.
- Alternatives:
  - An unbounded stop: a hung save blocks the deploy, and the platform kills the node anyway.
  - Telling clients to move with a new control message: the resume path already moves them, with no new protocol.
- Consequences: the budget is the user's choice per node group (`WithShutdownTimeout`). Stateful groups need room for
  their saves (N5).

## ADR-064 Failure detection tolerates pauses; the minority side of a split downs itself
- Context: a node that stalls (a GC pause, a starved CPU, a frozen container) wakes up to peers that look silent and,
  judged by its clock alone, would declare them all dead. A network split left both sides running, so each side loaded the keys it believed it owned:
  the same actor twice.
- Decision:
  - Pauses: membership measures each member's silence on this node's clock. A round that runs more than three
    intervals late moves every member's progress forward by the pause, so verdicts wait for real silence. Heartbeats
    spread by gossip through any member, so a peer's progress arrives indirectly too.
  - Splits: when members die, keep-majority decides (on by default, `NodeOptions.KeepMajority`). A node that reaches
    fewer of the cluster's members than it lost stops at once. In a tie, the side holding the lowest node id stays.
    Members that left do not count, and dead ones count until they are forgotten. A downed node saves nothing,
    because the majority already owns its keys; it raises `Node.Downed`, and the host exits with code 1 so the
    platform restarts it into the majority.
  - Clusters of fewer than three members never down themselves: there a crash and a split look the same, and
    downing would turn every crash into an outage.
- Alternatives:
  - Phi-accrual detection: it adapts thresholds to jitter, but not to this node's own pause, which is the failure
    seen in practice.
  - A minority that only refuses ownership and waits: it holds the process alive with stale state; a restart is
    simpler and the platform already does it.
  - An external lease (Kubernetes Lease, PostgreSQL) for strict ownership: later, as a module with persistence.
- Consequences: losing half the nodes at once stops the other half too. Deployments need at least three members per
  cluster for split safety, and rolling deploys take nodes down one at a time.
- References: excusing this node's own pause plays the part of Lifeguard's local health awareness (Dadgar et al.,
  2018), which scales timeouts by a local health score instead; `SuspectAfter`/`DeadAfter` correspond to Akka's
  `acceptable-heartbeat-pause` and down-removal margin; keep-majority is Akka's Split Brain Resolver `keep-majority`
  (its tie also goes to the side with the lowest address).

## ADR-065 Node links are authenticated by the cluster's secret
- Context: any process that reached a node's cluster port could send a Hello, then gossip and messages, and join the
  cluster. A node of another environment pointed at the wrong seeds joined just as easily.
- Decision:
  - An accepted link connection starts with a fresh random challenge (`ControlOp.Challenge`). The dialing node
    answers with its Hello: its link protocol levels (`ControlOp.Link`, ADR-077), the cluster name
    (`NodeOptions.ClusterName`, which Aspire sets to the cluster resource's name), who it is, the endpoint replies go
    to, its stream id and a proof: an HMAC-SHA256 with the signing secret over the cluster name, node id, stream id,
    host, port and the challenge. A Hello is good on one connection only: a recorded one proves nothing elsewhere, and
    the endpoint it names is its sender's.
  - An accepted connection is closed unless its first frame is such a Hello; nothing else is read before it. A
    handshake that hears nothing within the keep-alive timeout is closed.
  - Without a secret (development), protocol levels and cluster name are still checked.
- Alternatives:
  - A timestamped proof accepted once per node: no round trip, but a recorded Hello replays to another node within
    its window, and nodes need synchronized clocks.
  - Mutual TLS: certificates to issue and rotate, and encryption the platform's network already provides where it
    is needed.
- Consequences: one more round trip when a link connects (a cold path), no clock requirement. Links are
  authenticated but not encrypted. A node whose protocol levels do not overlap this one's is refused (ADR-077).
- Rotation: `NodeOptions.AcceptedSigningSecret` is a second secret whose proofs, tickets and admin tokens are accepted
  too; signing always uses `SigningSecret`. A rotation is three rolling steps (old signing and new accepted, new
  signing and old accepted, new alone), each accepting what the previous signs. Trying the second secret only when the
  first fails costs one more HMAC on a refused or rotating proof, so tokens need no key id (JWT's `kid`) and the
  formats stay as they are.

## ADR-066 Supervision is a decision per actor type
- Context: a handler that throws leaves its actor's state as far as it got. Some actor types tolerate that (counters,
  rooms); others must not continue from a half-applied change (a trade, an inventory move). Repeated failures of one
  message type had no signal.
- Decision:
  - The actor type says what to do as a constant, `[Actor<TId>(OnError = ErrorAction.Reload)]`.
    `ErrorAction.Resume` (the default) keeps the actor and its components. `ErrorAction.Reload` removes it without
    its `Unloading` mail and without a save, as a fenced actor is removed, and its next message loads it again from
    storage.
  - The caller receives the error either way. An `Answered` handler that throws also fails the request whose reply
    token it carried, so that caller learns at once rather than at its timeout.
  - Every failed handler, actor or service, and every system pass that throws is counted (`Node.Failed`, metric
    `daserver.handler.failed`).
- Alternatives:
  - Akka-style supervisor hierarchies with restart strategies: no actor hierarchy exists here (actors are entities
    addressed by id), and backoff or escalation would add concepts for a decision one method can make.
  - Stopping the actor for good: the next message would load it anyway, since actors are virtual.
  - A per-type error callback (an overridable method): behaviour where a constant suffices, and no virtual members
    in the ECS model (ADR-081).
- Consequences: Reload only helps once state is persisted (N5): without a store it resets the actor to what its
  `Created` handler gives.

## ADR-067 Components and logic live in separate projects; the node project is the reloadable logic
- Context: an in-process hot reload (since replaced by hot restart, ADR-084) replaces code while actors keep their state. EasyEcs stores components by type. A
  component declared in the reloaded assembly becomes a different type after the reload, and the stored data is
  unreachable. Logic that keeps state in object fields loses it the same way.
- What other projects do: a typical game server keeps components in its server assembly and systems, handlers and extensions in
  a hot-reloadable assembly, with analyzers and a generator that runs only in the reloadable project. ET splits
  Model (entities and components) from Hotfix (systems) and forbids fields in Hotfix through analyzers. Orleans does
  not reload code, so its grains keep their state classes beside them.
- Decision: split only what a reload needs, in the application layer. The framework's own runtime is never reloaded
  (it updates by a rolling deploy) and is not split.
  - DAS013: a project on the server runtime (it references `DaServer`) declares no component or singleton. They live
    in a components project that references contracts and EasyEcs only (`Sample.MapServer.Components`).
  - DAS007: actors, services and server systems hold no fields. A system's state is a singleton.
  - Not split: contracts (already their own project, shared with clients); shared logic (`DaServerShared`: clients run
    it, so it is never reloaded and may hold both); the node host (its `Program.cs` can load the node project's own
    assembly collectibly, so the node project is the reloadable logic).
  - Projects are recognised by what they reference: no property to set, no extra host project.
- The reload is superseded by ADR-084's hot restart (this ADR is rewritten when W10 promotes the drafts): no cluster-wide stop, each shard
  swapping its handler table and system list between two passes on its own thread (handlers run to completion, so
  nothing of the old code is in flight), components and singletons staying where they are because their types do not
  change (a typical game server stops the
  world and forces a GC instead). The split above holds now, so the reload adds no rule to existing code.
- Consequences: one components project per server that has data. Changing a component's layout is a rolling deploy;
  changing behaviour is a hot restart (ADR-084). Data and behaviour are split exactly as ECS splits them.

## ADR-068 Actor records: one row per actor, claimed by epoch, written at tick end
- Context: actors are activated on demand and moved between nodes, so their state needs a store that also tells two
  activations of one key apart. Orleans writes a grain's state object when the grain asks (`WriteStateAsync`, one row
  per grain, an ETag). A typical game server writes documents per type, marked dirty by hand and written back in
  batches; a renamed class loses its collection. Akka.NET and Proto.Actor journal events.
- Decision:
  - **Today (until roadmap W6):** a record is the actor's `[Persist]` components, keyed by (handler type id, key),
    versioned and hashed. It is written after the actor's `Unloading` mail, every `SaveEverySeconds` when changed
    (change-tracked) and at once with `SaveNow`. Loading claims the record by raising its version and every write names
    the version it replaces, so a stale owner's write is refused and its copy fenced; the hash recognizes a write whose
    answer was lost. A state that does not serialize gives its write up and is logged (`RecordUnwritable`): the store
    keeps the last record and the shard's other writes and handoffs go on. The rest of this decision is W6's target.
  - **What:** an actor's record is `SaveEntitiesWith` of its root and its `Persisted` descendants with the persist
    component set built in `Registration` (draft §10). Persisted components are version tolerant or keyed (DAS014,
    ADR-076); a load completes every entity from its prefab copy (`[NotSaved]` components and those the prefab gained)
    and skips unknown type ids, counted. Transient data is rebuilt on activation. Relational and queried data
    (accounts, payments, mail, rankings) stays in services with the user's ORM.
  - **The table:** one row per actor, primary key `(type_id, key)`, so equal keys of two actor types (`Map(7)`,
    `Loot(7)`) are two rows. Columns: `type_id integer`, `key bytea`, `slot smallint`, `epoch bigint`,
    `write_gen bigint`, `owner` (node name, node generation), `placement bigint`, `record bytea`,
    `next_due timestamptz`; an index on `(type_id, slot, next_due)`. The key is `bytea`, not two `bigint`s: a key is any
    unmanaged struct of at most 16 bytes, which one column holds whatever its shape, with no zero-padded second column
    in the primary key; its fields are written in order, each big-endian at its own width, so a `long` key is
    `int8send(key)`. The slot is the key's placement slot (ADR-074), computed by the node with the hash placement uses,
    so SQL never hashes.
  - **Claim:** an activation claims the epoch and reads the record in one upsert (`INSERT … ON CONFLICT (type_id, key)
    DO UPDATE … WHERE … RETURNING new.epoch, new.record, old.owner`, PostgreSQL 18): it inserts a placeholder row for
    a new key, raises the epoch, resets `write_gen` and takes the owner only with a strictly newer placement version
    (or the same version and owner, a retry), and a displaced owner gets `Fence(key, epoch)` at once. A migration's
    arrival claims with an `UPDATE` comparing the epoch it carries, writing `GREATEST(stored, seen)` placement.
  - **Writes:** each world writes the roots due at a tick end in writes over `unnest` arrays, each one transaction of
    several statements in one round trip (the records' compare and swap, then the roots' row statements, each applying
    only where its record landed), split between roots at 512 roots (a root's record and row changes never split), at
    most `DaServer:Store:WritesInFlight` writes in flight per world (roots beyond wait for the next round), every
    row a CAS on `(type_id, key)`, the epoch and `write_gen < :write_gen`; the rows not updated come back as stored. A
    root whose `SaveEntitiesWith` throws (a state that does not serialize) is left out of the batch and logged
    (`RecordUnwritable`); the rest of the batch is written. Deactivation, stop and migration also save before they
    destroy: when that throws, an idle deactivation is cancelled, a forced one or a stop gives the write up (the last
    stored record stays, `RecordUnwritable`), and a migration falls back to such a forced deactivation (placement
    already names the target, which activates from the last record); an in-place hot restart leaves the root out of
    the handover (destroyed in the frozen world as by fencing, `RecordUnwritable`), so it re-activates from its last
    record and the restart goes on. The write that releases a root's output held by `Commit` before a migration or a
    freeze takes the same fallback at once, its held output dropped. A refused
    row fences only its actor; a timed-out write is retried with the same bytes and generation, and a row found at its
    own epoch and generation counts as written. Roots that call `Commit` ride the next write (ADR-097); other changes at most every
    `DaServer:CheckpointInterval`. Change tracking on the persist set tags written roots `Unsaved`, so only those are
    written.
  - **Wake:** every write stores the root's `NextDue` in `next_due`; `IActorStore.DueKeys(slots, until, after)` reads,
    over the `(type_id, slot, next_due)` index, the keys of the node's `(type, slot)` pairs whose `next_due` is at or
    before `until`, in pages of 512 keyset-ordered by `(type_id, slot, next_due, key)` after the row `after`, and
    returns the page's last row; `DueScan` wakes them. A scan fixes `until` once; its first page passes a sentinel
    below every real row, `(-2147483648, -1, '-infinity', '\x')` (`type_id` is `integer`, `slot` a `smallint` whose
    real values are 0..4095), and each page probes only the pairs at or after the cursor's pair, taking at most 512
    rows from each (`LATERAL … LIMIT` over that pair's index range) with the pairs in order, so a page sorts rows per
    pair and stops at 512 instead of sorting every later pair's backlog.
  - **Schema:** only the node image in schema mode (`--DaServer:Migrate`: create under an advisory lock and exit)
    creates the `actor_records` table: a Job when published (an owner role with DDL, nodes on a DML-only role), a
    one-shot service in Compose, a one-shot resource in a local run; `TestCluster` calls the same schema code directly.
    Nodes never create it: a node whose table is missing or whose store is unreachable at start stays not ready and
    retries with one backoff, without a deadline (the startup probe treats the wait as progress, so an outage does
    not restart nodes in a loop). Once started, a node keeps answering ready through a
    store outage (the store is not part of readiness: an outage would otherwise take every node out of its Service at
    once); the outage shows in `StoreDegraded` and the metrics. Hand-written SQL, no migration library: one table and
    one index. Today's `daserver_actors` table is neither read nor converted (pre-1.0).
  - **Backends:** `IActorStore` (claims, the batched write, `DueKeys`) is all a backend implements.
    `InMemoryActorStore` serves tests and development and behaves like the database (epochs, generations, slots).
    `PostgreSqlActorStore` is the default for deployments, written with Npgsql and no ORM (the record is opaque bytes,
    so the schema never follows the components); the record bytes are the world's pooled buffers, passed without
    copying. `AddPostgreSqlActorStore()` wires it on Aspire's Npgsql integration; the AppHost passes the connection
    string (`WithActorStore`).
- Alternatives:
  - A write per message: no loss, but write volume and latency grow with traffic.
  - Explicit saves in handlers: forgotten saves, and saves while the state is half-changed.
  - One table or collection per component type: a load becomes many queries, and renames break storage.
  - The key as two `bigint` columns: fixed width, but every key pads to 16 bytes and the primary key has three
    columns; `bytea` holds every shape in one.
  - Event sourcing: out of core; it can be built as a pattern on actors.
- Consequences: one write (one transaction, one round trip) per 512 changed actors per world per tick end, at most `Store:WritesInFlight` in flight
  per world; a stale owner can never overwrite the new one, nor a late statement a newer state; `DueScan` costs one
  index range per owned `(type, slot)` pair. A store
  outage delays deactivations and stops, bounded by the stop budget. Records are opaque bytes for performance;
  inspecting them needs the manifest (the inspector decodes them through `wire.manifest`).
- References: the epoch refusing an older writer is a fencing token (Kleppmann, "How to do distributed locking", 2016;
  Designing Data-Intensive Applications, ch. 8).

## ADR-069 The e2e is a smoke test of the deployed model, timed by phase
- Context: the e2e starts the sample's AppHost (PostgreSQL, six nodes, bots) through Aspire.Hosting.Testing. It took
  60 to 70 s for 15 s of load. Measured causes: the .NET 11 CLI behind `dotnet run` exports its own telemetry whenever
  an `OTEL_EXPORTER_OTLP_*` variable is present and waited out a 10 s export timeout on every exit; stopping an AppHost
  stops every resource at once, so the database died before the nodes' final saves and the node was killed after
  retrying; a node waited for the database before starting.
- Decision:
  - The e2e removes every `OTEL_*` variable and sets `DOTNET_CLI_TELEMETRY_OPTOUT`, uses a fresh database, gives
    every node and database three minutes to become healthy (failing with the resource and its health report), stops
    the AppHost and checks that every node exited with 0 (every player saved before the database stopped).
  - `DaServer.Aspire` stops a cluster's nodes before the AppHost's other resources: when the AppHost is asked to stop,
    the nodes get SIGTERM from an `ApplicationStopping` callback (registered last, so it runs before Aspire's own
    shutdown; nodes that exit after it began leave the orchestrator waiting out the whole shutdown timeout), and each
    node's exit code is logged. A node exits with 1 when its stop lost state (`Node.StoppedClean`). Windows falls back
    to Aspire's stop command.
  - The sample's PostgreSQL also has a persistent lifetime: the next run reuses the running container.
  - `WithActorStore` makes the nodes wait for the database, run and published alike: a node prepares its store before
    it starts (ADR-068), and Compose orders start and stop by it.
  - The e2e measures 10 s (two map changes) with 1,000 bots; long and large runs (10k bots) are the same test with
    `E2E_*` variables, run on demand, not on every commit. Behavior is covered by the in-process cluster tests.
- Consequences: about 32 s: 6 s of the testing host, about 7 s of seven processes starting cold through `dotnet run` on
  four cores (a node alone is up in 0.5 s), 2 s of health polling, 13 s of bots (10 s measured), 4 s of stopping.
  The phases are printed in the test log.

## ADR-070 Client writes on a beat, written directly (opt-in)
- Context: a client-facing node writes each session once per batch (ADR-034). In the 10k-in-one-map scenario a session
  gets positions from the map node and broadcasts from the game node, each its own batch: about 14 writes a second per
  session, each a thread-pool wake of the session's send loop and a `send`, and as many reads on the client. On one
  4-core VM shared with 10,000 bots this saturated the cores (broadcast p99 over 500 ms, slow-consumer drops).
  Measured and rejected: writing at the end of a batch on the calling thread (5,000 sends in a row on one thread
  doubled a broadcast's time, and concurrent remote calls got 2-3x slower without the send loop's coalescing);
  resuming send loops through per-core queues (Kestrel's IOQueue; broadcasts 20-30% slower without work stealing).
- Decision: `ClientConnectionOptions.FlushInterval` (zero by default: written at once). When set, a session's
  connection gets a `SendFlusher`: `Send` queues the frame and lists the connection once when its queue goes from
  empty to not empty; one flush thread per node writes every listed connection on each beat with a non-blocking
  `send`, waking nothing. What the socket does not take goes to the connection's carry loop, which owns the writer
  until it has written that and whatever queued meanwhile. Node links, replies between nodes and connections without
  a flusher keep the send loop unchanged. The sample's gateways use 2 ms.
- Alternatives: a beat equal to the tick (50 ms: more merging, a tick of latency); merging across sessions (no such
  syscall for TCP in .NET; io_uring is not in .NET 11); splitting a beat into parts of 256 connections on the thread
  pool (idle cores: broadcast to 1,000 clients 18.6 -> 8.5 ms, to 5,000 80 -> 31 ms; saturated 4 cores: p99 141-182 ->
  237-795 ms, the parts queue behind the receive loops).
- Consequences: everything sent to a client, replies included, leaves up to one interval later. 10,000 bots in one
  map on one 4-core VM: without it p99 559 ms and failed; 10 ms 190 ms, 5 ms 169 ms, 2 ms 163 ms, 1 ms 170 ms, with
  no drop or gap, the bots' allocations 5.9 MB/s -> 0.3 MB/s (they keep up, so nothing is resent).
  On idle cores the beat costs latency (BDN `BroadcastBenchmark`, 2 ms against none: 1,000 clients 6.5 -> 18.6 ms,
  5,000 33 -> 80 ms; `ScenarioBenchmark` 9,000 clients 61 -> 69 ms) for half the thread-pool work items: it is for
  nodes whose cores are the limit, hence off by default.

- Clients too (`ClusterClient.FlushInterval`, off by default): a process's connections with one interval share one
  flush thread (`SendFlusher.Shared`, `ConnectionOptions.FlushedEvery`, used by both sides), for clients that send
  many requests at once (load generators, bots, server-side callers). The flash sale's crowd, 3,000,000 buyers on
  one node: 1 ms beat against none, 19.7 -> 13.2 s, 350,000 -> 548,000 requests a second steady, node CPU 1.64 ->
  1.51 cores (the crowd spent half its CPU in `send` and on the connection's lock).
- Kestrel as the client transport was measured too (`TransportBenchmark`: Kestrel's connection pipes feeding the same
  client connections): broadcasts to 5,000 clients 43.7 ms and 14.8 MB against 26.2 ms and 55 KB, client requests
  13-17% slower, equal at 1,000 clients. The own sockets stay.

## ADR-071 Tests drive the shards' clock
- Context: users test actors, services and systems whose behavior depends on time: per-tick systems, `SendAfter`,
  idle unloading, checkpoints. On real time such tests sleep and assert ranges ("about 20 ticks"), which is slow and
  flaky; a test harness of our own (`ClusterTest`) was internal.
- Decision: `NodeOptions.TimeProvider` is the shards' clock. The shard loop schedules ticks and timers on it, a wake's
  start time is `ShardActivity.NowMs` (what idle unloading and checkpoints compare with), and user code reads it as
  `world.Time` (`ctx.World.Time` in handlers). Transport keeps real time: call deadlines, keep-alives, membership and links run over real
  sockets. `DaServer.Testing` ships `TestCluster`: real nodes in the test process with fast membership timings and,
  with `ManualTime`, a clock that starts at 2000-01-01 and moves only in `TickAsync`/`AdvanceAsync`, one tick
  interval at a time, waiting for every shard to run that tick. Messages still flow as they arrive. The manual
  clock refuses timers (shard code delays with `SendAfter`, DAS015).
- Alternatives: a simulated network with a single-threaded scheduler (fully deterministic, but a second runtime to
  keep true to the real one); `FakeTimeProvider` (its timers fire on advance, but nothing in shard code may use
  timers, and stepping must wait for the shards anyway); faking time for membership too (gossip and failure
  detection on a frozen clock over real sockets would declare nothing or everything dead).
- Consequences: a test counts what systems send per tick exactly (`Sample.Tests`: one realm status every 5 ticks).
  The shard loop reads the clock through a virtual call once per wake: BDN against the commit before, back to back,
  one-way messages -3% / +1% (local / remote), calls -1% to +8% (within the error), allocations unchanged. The framework's own
  integration tests and users' tests start nodes with the same options.

## ADR-072 Actors take their record along when they change node
- Context: when ownership moves (a node joins, leaves or stops), the old owner wrote the actor's record and the new
  one read it back: a store round trip per moved actor, state lost without a store, and a new owner that got mail
  before the write read a stale record. ET transfers a unit explicitly: its `ITransfer` components are serialized to
  the new map while the location server holds the unit's messages (lock, then unlock with the new address). Orleans
  migration carries the persisted state with its ETag (no reread, no write) and lets grains add more in
  `OnDehydrate`/`OnRehydrate`.
- Decision:
  - **What moves:** the `[Persist]` components (stored and moved) and `[Handoff]` components (moved only: in-memory
    state worth keeping across a move, like ET's `ITransfer`), in the record format of ADR-068, with the version last
    written and that write's hash.
  - **How:** the old owner, after the actor's `Unloading` mail and any write in flight, sends the record to the key's
    owner in its view as a runtime message to the actor's handler type. Its forwarded mail takes the same link
    afterwards, so the record arrives first and no lock is needed. The new owner restores the record and runs the
    actor's `Loaded` mail; its checkpoint
    writes what changed since the old owner's last write, with the version continuing (fencing as in ADR-068). The
    old owner is removed once the new one accepts, without writing.
  - **Mail first:** while keys move after view changes (within the move window, twice the call and store timeouts), a node that loads a key asks the
    key's latest other owner in the views it replaced in that time (or, when it just joined, the node owning the key
    without it, if that node is up longer) and the actor's mail waits. The answer is the record or "not here"; an
    actor that leaves without handing over to the asking node tells it so. "Not here" is never early: a node owning
    the key in its own view, or one that took the key from a live node within the timeout, keeps the request (and asks
    that node in turn) until ownership changes or the record comes. A record whose key moved on meanwhile goes on once
    (the frame's hop count): to the node that asked for it, else to the key's owner in the receiver's view.
  - **Waiting and losses:** both sides wait while the other is alive: a new owner asks again instead of loading beside a
    previous owner still holding the actor; an old owner keeps its record instead of writing over a new owner that may have
    taken it. A link that restarts its stream can lose the record or the answer, so each handoff carries a random id
    and the old owner sends the record again (a resend, its own frame, same id) when the new owner asks or the answer
    is late. A copy restored from a handoff keeps its id, and each shard remembers for two handoff timeouts the
    handoffs it took or passed on (bounded, oldest out first): the same id again is answered "accepted" wherever the
    actor went since, or follows to the node it was passed to (gone: refused). A resend never seen is taken only by a
    copy waiting for that sender's record, or by the key's owner holding no copy while nothing was forgotten early;
    otherwise it is refused and the store decides.
  - **How long a previous owner counts:** a node that loads a key waits for the owners it replaced within the move
    window (2 x (call timeout + store timeout), up to 256 views): a handoff and a write in flight fit in it. A copy that
    gave its record to a node other than the asker tells the asker where it went, and the asker waits for that node;
    each shard also remembers for the move window where it handed each key, so an ask that comes after the copy is
    gone is sent there too (forgotten once the key is here again).
  - **Fallback:** a refused handoff (the new owner already runs the actor or is stopping, cannot read the record, or
    its actor's `Loaded` handler throws), a dead node, or a record that does
    not serialize: the old owner takes its state back and writes it to the store, and the new one reads (a state that
    does not serialize is not written either: ADR-068). A node declared dead hands nothing over and writes nothing (its
    copies are fenced). A cluster stopping as a whole writes everything.
- Alternatives: writing to the store on every move as well (survives a crash of the new owner right after the move,
  at the cost of a write per moved actor and version handshakes); moving every component (all would have to be
  serializable, and transient data such as grids and caches is cheaper to rebuild); a lock in a directory as ET does
  (ordered links already order the record before the mail).
- Consequences: scaling out and in moves actors without store traffic (tests: 40 actors out and back in, 0 reads, 0
  writes) and keeps state without a store. A new owner that crashes before its first checkpoint loses the changes
  since the old owner's last write, as any crash loses up to `SaveEverySeconds`. Actors leaving while unloaded and waiting
  for a write take no mail (it moves with them).

## ADR-073 Placement affinity on the id type
- Context: actors that talk all the time (a room and its seats, a guild and its members' guild state) pay a network
  hop per message when rendezvous hashing scatters them. ET addresses carry the process and the scene, so units are
  placed by hand; Orleans offers placement strategies per grain class and a directory; neither lets two actor types
  meet without a lookup.
- Decision: an actor id type may implement `IPlaced`: its `Placement`, a pure function of the id's value, is hashed in
  place of the value, with handler type group 0, onto nodes (rendezvous) and onto shards. Actors of any type with the
  same placement land on the same node and shard wherever both types are hosted. Senders compute it from the id
  alone, so the wire is unchanged and there is no directory. The generated registration of the assembly declaring
  the id type registers it; a node that does not reference the id type forwards once. DAS016 reports `IPlaced` on
  anything but an actor id type.
- Alternatives: a `[PlacedWith<RoomId>]` attribute on the actor class (fixes how ids encode their group in the
  framework); a placement key field in the frame (a wire change for every message); a directory (a lookup per
  first message, a component to run).
- Consequences: co-located actors exchange messages through their shard's mailbox. A placement shared by many actors
  is a hot spot by design: placement groups should stay small (a room, a party).

## ADR-074 Slots own keys; strategies move slots
- Context: every send to an actor computed its owner by rendezvous hashing, one weight per node hosting the type:
  cheap at three nodes, a measurable cost per message at hundreds. Placement strategies beyond hashing (by load, by
  the caller's node, sticky) were asked for. Orleans decides a grain's silo at activation (random, prefer local,
  activation count, resource-optimized by default since 9.2) and records it in the grain directory, moving grains
  later (rebalancer, repartitioner); ET places by hand (the id names the process) with a location server for units
  that move; Kubernetes schedules a pod once (filter, score) and records the binding in etcd. Every strategy that a
  key alone cannot compute needs a record of the decision; they differ in its grain. Akka cluster sharding hashes
  entities into a fixed number of shards and lets a coordinator allocate whole shards to nodes (least shards first,
  rebalancing a few at a time), so the record is one small table that every node caches.
- Decision: actor keys (the placement for `IPlaced` ids) hash into 4,096 slots. A view gives each slot of an actor
  type an owner among the type's nodes by rendezvous hashing over (type, slot), or (0, slot) for placed types so
  they meet: lookups read a table, and a node joining or leaving moves only the slots it wins or held. Strategies act
  on slots, not actors (S3): a coordinator may override slot owners in a versioned, gossiped table (load-aware
  rebalancing, sticky slots for heavy actors), moving actors with handoff (ADR-072). No per-actor directory, no
  "prefer the caller's node": what talks a lot is co-located by `IPlaced`.
- Alternatives: an Orleans-style directory (a lookup and a registration per activation, and a consistent directory
  through failures and splits); jump or Maglev hashing (no better than rendezvous per slot once tables are built,
  and rendezvous gives the "owner without this node" a handoff needs); keeping per-key rendezvous (O(nodes) per send).
- Consequences: a view change rebuilds one table per actor type (4,096 slots times its nodes, milliseconds, cold).
  Keys of one slot move together. BenchmarkDotNet numbers: the roadmap's K2 entry.

## ADR-075 A busy shard spins before it sleeps; one waker per sleep
- Context: under load a shard drains its queue in a few microseconds and goes to sleep just before the next message
  arrives. Every message then paid a wake (the event's lock and a futex call) and the shard a sleep; with many
  connections delivering at once, every producer set the event and they queued on its lock (15% of a flash-sale
  node's CPU in `Lock.TryEnterSlow`).
- Decision: a shard whose last pass had work spins a little (20 rounds of `Thread.SpinWait(50)`, tens of
  microseconds) while its queues stay empty before it sleeps; an idle shard sleeps at once. A producer wakes a
  sleeping shard only if it is the one that clears the sleeping flag (`Interlocked.Exchange`); the others see it
  cleared and leave the event alone.
- Alternatives: no spin (every message under load pays a sleep and a wake); a longer or unconditional spin (burns a
  core per idle shard, and steals from the threads that would produce the work on an oversubscribed machine).
- Consequences: BenchmarkDotNet, before -> after: one local call 12.3 -> 5.6 µs, 1,024 concurrent local calls
  1,383 -> 952 µs, one remote call 84 -> 75 µs, remote calls at 64 / 1,024 in flight +7% / +6% (within the error),
  one-way messages 438 -> 412 ns local, 789 -> 797 ns remote, allocations unchanged. The 10k-bot scenario with the
  bots on the same 4 cores (oversubscribed): broadcast p99 141-182 -> 108-141 ms.


## ADR-076 The wire manifest: a change is judged by the format its type is written in
- Context: a rolling upgrade runs two builds side by side, and stored records outlive the code that wrote them. Nino
  4 writes a type in one of three formats (`[NinoType(format: ...)]`): positional (members in order, no names or
  lengths; a raw struct, every field serialized and raw, as its memory with padding zeroed), version tolerant (one
  `int32` length around the whole body: members appended to the most derived type are skipped by older readers and
  defaulted by newer ones) and keyed (a key and a length per member: members are added, removed and reordered freely).
  A sealed class adds a null flag, another class a type id. The other build misreads any change its format does not
  tolerate, and nothing at run time says so.
- Decision: messages stay positional (the cheapest bytes and code; a new field goes into a new message), persisted
  and handed-off components must be version tolerant or keyed (DAS014, ADR-068). Each project commits
  `wire.manifest`, built from Nino's own description of every type it writes (`[assembly: NinoTypeLayout]`, emitted by
  Nino's generator): its header (none, null flag, type id), format, whether it is raw memory, `tracked`, its type id,
  and its members in written order with their types and how each is written, as Nino describes the bytes (`raw(4)`,
  `string`, `utf8`, `seq(raw(8))`, `type(T)`; a keyed member with its key and former keys). The manifest names the
  layout text it was recorded in (a line of its header): one recorded in another, after a Nino update that changed the
  text, is reported once (DAS017), not as a change in every member. The
  manifest adds what Nino does not record: a request's reply, a plain unmanaged struct's fields (Nino copies an id's
  memory and names only its type), another assembly's [NinoType] as its layout inline (a package update that changes
  it shows), the [NinoType] ancestors, and every enum those types hold with its underlying type and each member's
  value (the bytes are the number; what it means is kept nowhere else). The analyzer (DAS017) fails the build on a
  type gone, its encoding (a new
  type id included) or reply changed, a new subtype of a recorded or another assembly's type (older builds lack its
  type id), and a member change the format does not tolerate: any, positional; anything but members appended at the
  end, version tolerant (one appended to a base shifts its subtypes' members, which their own entries show); another
  type under a key, keyed (members matched by key, so one renamed with `[NinoFormerName]` keeps its key); an enum
  member removed, renamed or renumbered. It asks to record the rest (DAS018: new
  types, appended members, keys added, removed or renamed with their key kept, new enum members)
  with `dotnet build -p:UpdateWireManifest=true` (which still stops on DAS017; `=force` records a deliberate break).
  The analyzer reads the layouts in the compilation; the manifest is written after the compile by
  `DaServer.WireTool` (in the Abstractions package), which runs the same code over the built assembly's metadata:
  analyzers cannot write files and generators cannot see another generator's output. Nino 4 checks every read
  against its span, so a truncated or hostile payload throws instead of being read past its end. Nino's diagnostics
  for what it would skip silently (NINO006, NINO007, NINO022, NINO024) are errors in every project that references
  DaServer.Abstractions.
- Alternatives: version-tolerant messages (4 bytes and a back-patched length per message for a tolerance a new
  message gives for free); mirroring Nino's rules in DaServer's generator (every Nino change would need a matching one
  here); appending to positional structs with zero padding
  (unsound: an appended field can land in trailing padding); field tags (protobuf: bytes and CPU on every field).
- Consequences: renames are reported though positional types write no names (deliberately conservative). Raw memory
  is recorded field by field with offsets (`raw(16){Node:ulong@0:raw(8),Id:long@8:raw(8)}`), nested structs and
  `[StructLayout(Pack/Size)]`/`[FieldOffset]` included, whichever assembly declares the struct. The framework's own frames
  (cards, the hello, session operations) are guarded by the protocol levels instead (ADR-077).

## ADR-077 Rolling upgrades: builds on the card, messages only where the build has them
- Context: during a rolling upgrade old and new nodes host the same actor and service types, but a new build may handle
  messages the old one does not. Routing by handler type alone sends such a message to an old node, which has no
  handler for it.
- Decision: every node's card lists the (message, handler type) pairs it handles and its build (`daserver.build`:
  the framework's and the application's informational versions, `ClusterMember.Build`); a node logs while its view
  holds more than one build. A service message, sent or forwarded, goes only to nodes whose build handles it. An
  actor keeps the owner its type gives the key, so a key never has two activations; a message the owner's build
  lacks for that actor type is not sent: a request fails with `NotHosted` (retry), a one-way message is dropped,
  until the owner is upgraded or leaves. A client message unknown to its client-facing node's build is refused with
  `NotHosted` too (retryable, unlike `Forbidden`).
- Protocol levels: what the framework itself writes is versioned by a range of levels per protocol, [Min, Max]
  (`ProtocolLevels`; `ControlOp.Link`, `ControlOp.Client`), the levels a build reads, exchanged outside the
  serializer's bytes so any build can read them: the accepting node's in its `Challenge` key, the dialer's as 8 raw
  bytes ahead of its Nino `LinkHello`, a client's in its `ClientHello` key. Two builds talk when their ranges overlap;
  otherwise the side that sees the mismatch logs a refusal naming both ranges: the dialer on the challenge, the acceptor on a Hello (a client gets an error naming both, is left out of
  `ClientGroup.All` and closes the connection; anything it sends but keep-alives before an accepted hello or after a
  refused one closes it, and it sends nothing else until the hello is answered). A level changes only for what every
  peer must read alike: the framing, the shared encoding, or a behavior all must agree on. Added control ops (older
  builds ignore them), fields at the end of version-tolerant framework types, card entries and new messages (routed by
  the card above) change none. A change that new builds read but old ones do not is two releases, as Kafka, Akka and
  Cassandra roll it: a bridge release reads both and writes the old form (Max + 1), the next writes the new one and
  drops the old (Min + 1); upgrades pass through the bridge. A change no bridge can carry raises Min past every older
  Max and is a full restart, said so in its release notes (Orleans 3 to 7). Nino 4.0.0-rc.6's strings (link 3, client
  5) are such a restart: Nino writes no older form, and DaServer is not released yet.
- Alternatives: route the new message to a node of the new build (a second activation of the key, what the slot table
  prevents); per-interface version numbers (Orleans: the card already says what each build handles, and the manifest
  keeps layouts compatible); stop-the-world deploys. For the framework's own protocol: one number raised on every wire
  change and compared for equality (every change a restart, and inside the Nino hello, so a build that cannot read the
  hello was refused without saying why); capability flags per feature (Envoy, Kafka's feature versions: a second
  mechanism next to the card, for one codebase whose wire evolves in a line); a cluster-wide write level finalized once
  every member supports it (CockroachDB, Kafka's KIP-584), added when a bridge release first needs it.
- Consequences: new actor messages become available key by key as owners are replaced; callers retry `NotHosted`
  meanwhile. While every node runs one build the routing cost is one empty-set check per actor message.

## ADR-078 Nino 4: written in place, read from the span, pooled where messages hold collections
- Context: Nino 4 writes in place (`NinoSerializer.Serialize(value, NinoArrayBufferWriter)`), reads from a
  span checked against it (`NinoDeserializer.Deserialize<T>(span)`), computes type ids (`NinoTypeId`) and deserializes
  `[NinoPooled]` sealed classes into instances from a pool: what a framework on top would otherwise build itself.
- Decision: `FrameWriter` wraps a `NinoArrayBufferWriter` (frames over `ArrayPool`, the header reserved first; a pooled
  writer keeps at most 4 KB of array, a larger one goes back to `ArrayPool` on dispose, so 65,536 pooled writers hold
  at most 256 MB) and serializes with one call; messages, replies and record sections
  are read straight from their span; values are passed by reference down to Nino (`in T`, no copy of a large
  struct per write); type ids are `NinoTypeId.Of` (`TypeId<T>` caches it) and `[NinoFormerName]`
  keeps one across a rename or resolves a collision (DAS004); a message that holds a collection holds it in a
  `[NinoPooled]` class its handlers return (`Moves` in the MMO sample: every handler deserializes its own copy and
  returns it, so fan-out to several handlers allocates nothing). Messages are positional, persisted and handed-off components
  version tolerant or keyed (DAS014, ADR-076). On 4.0.0-rc.6: a string whose chars all fit Latin-1 is written one byte
  a char, any other as UTF-16, so names, hosts and keys take the default and `[NinoUtf8]` is for text that is mostly
  ASCII with some wider chars (chat in the MMO sample); a `[NinoType]` class that is a reply or held by a message is
  `sealed` (a 1-byte null flag instead of a type id, no dispatch, inlined into collections); a raw struct on the wire
  has no padding (Nino writes padding as zero bytes: `StructLayout(Pack)` where fields leave holes).
- Alternatives: stay on Nino 3 (no checked reads, no pools, our own ids to keep in step with Nino's); Nino's
  polymorphic dispatch for messages (boxes struct messages and writes a type id the frame header already carries);
  Nino's interceptors (messages live in other assemblies and calls are generic, which they do not see).
- Consequences: frames rent from the pinned `BufferPool` through Nino's pool parameter, so sockets send them as
  they are. Nino's registries can be released with a collectible load context (`NinoRegistry.Remove`), which an in-process reload would use; code changes ship by hot restart instead (ADR-084).
  Moving to rc.6 changed every string's bytes, which earlier releases cannot read: a full restart (ADR-077), and no
  rollback once records hold rc.6 strings (rc.6 reads the earlier UTF-16 ones).
  BenchmarkDotNet: see the roadmap's Nino 4 row.

## ADR-079 A coordinator allocates slots: sticky types and load-aware moves
- Context: rendezvous hashing (ADR-074) decides every slot's owner from the view alone, so a node joining takes its
  share of slots at once: an MMO map with players moves the moment a node is added, and actors stay where hashing put
  them whatever the load. Akka's cluster sharding lets one coordinator allocate whole shards (least shards first) and
  rebalance a few at a time; Orleans' rebalancer moves activations by load. Both record the decision.
- Decision:
  - **Coordinator:** the oldest Up member (its card's `UpSince`, then the node id): every node computes it from its
    view, nothing is elected. A new coordinator continues from the table it received, drops the reports it kept from
    an earlier term, and plans only once every Up member hosting actors reported to it (or after three rounds): a
    plan from partial reports would unpin loaded slots for a round, and the shards act on every table at once.
  - **Slot table:** the coordinator's overrides of rendezvous owners, (actor type, slot) to node, with a version; it
    sends the whole table to every member on a change, to members it has not sent it to yet, and again every minute
    (`ControlOp.SlotTable`, ignored by older builds); every node also sends its table on each new link, so a joining
    node routes by it from its first link. A view applies an override whose node hosts the type and
    is in the placement set (in the target design: `Active`, or written `Dead` and not yet re-placed, ADR-082); any other slot keeps its rendezvous owner, so an empty table is ADR-074's behaviour.
  - **Reports:** every node counts, every five seconds, which slots of each actor type hold loaded actors and how many,
    and tells the coordinator when that changed, the coordinator changed, or a minute passed (`ControlOp.SlotReport`),
    with which of its types are sticky. The coordinator plans after a new report or a membership change only: an
    unchanged round allocates nothing.
  - **Sticky types** (`[Sticky]` on the actor class, e.g. maps): the coordinator pins every reported loaded slot
    of a sticky type to the node that holds it, so a join moves only empty slots: new keys reach the new node, loaded
    actors stay. A slot loaded since the last report may still move at a join (a handoff, as before).
  - **Load-aware moves** (the default policy): per actor type, when the most loaded node holds more than 1.25 times
    the average and at least two slots more than the least loaded, the coordinator moves up to two of its loaded slots
    to the least loaded node per round; the move is a handoff (ADR-072). Sticky types are never rebalanced.
  - Overrides naming a node that left are dropped; an override whose node has not reported, or whose type no report
    names, is kept; a moved slot whose override now matches rendezvous is dropped (the
    table stays small). A sticky type's pins stay while their slots hold actors, matching rendezvous or not: a join
    changes rendezvous, and the pin must already be there.
- Alternatives: an Orleans-style per-actor directory (a registration per activation); a coordinator elected by
  consensus (gossip already agrees on the oldest member, and a split's minority downs itself, ADR-064); moving by CPU
  (shared by everything on the node: actor counts per type are the signal the coordinator can act on).
- Consequences: a view change still moves only what rendezvous moves, minus what the table keeps; a move costs one
  handoff per actor. The coordinator's work is a few thousand table entries per round, cold. Tests: sticky slots stay
  across a join, an imbalance is evened out in rounds, a coordinator that leaves is replaced with its table.
- References: slots and the coordinator are Akka cluster sharding's shards and `ShardCoordinator`, the coordinator on
  the oldest member as Akka's cluster singleton; the imbalance threshold and the two moves per round play the part of
  `least-shard-allocation-strategy`'s `rebalance-relative-limit` and `max-simultaneous-rebalance`.

## ADR-080 Frame extensions: optional metadata that readers skip; traces travel in them
- Context: a frame's trailers (call id, origin) are fixed-size and named by header flags, so a reader that
  does not know a flag misreads the frame: every new piece of metadata would need a new link protocol and a cluster
  restart. Distributed tracing (A6) needs one now, and priorities, tenants or baggage may follow. Peers: Kubernetes'
  CRI keeps its major version and allows only changes old and new both read, new features degrading gracefully;
  protobuf (gRPC, CRI) skips unknown fields; Orleans breaks its wire between majors (3 to 7); ET deploys server and
  client together.
- Decision:
  - **Three layers of change.** The protocol levels (`ControlOp.Link`, `ControlOp.Client`, ranges, ADR-077) change
    only for what every peer must read alike, bridged over two releases or, when no bridge can carry it, a full restart. Optional metadata goes into the **extension block**
    (`FrameFlags.HasExtensions`): entries of [id][length][data] between the payload and the fixed trailers, closed by
    the block's uint16 size; readers look entries up by id and skip the rest, hops relay the block untouched, so a
    new entry needs no version and mixes with nodes that ignore it. A feature both sides must support is announced
    on the member card (as handled messages are, ADR-077) and used only towards nodes that have it.
  - **Tracing** is the first entry (`FrameExtensions.Trace`: W3C trace id, parent span id, flags; 25 bytes). A
    handler whose message carries one runs in a span of the `DaServer` activity source, child of the sender's, while
    something listens; the span is the thread's current activity while the handler runs and ends when it returns
    (handlers run to completion, ADR-081). Asks, sends and publishes carry the current activity's context: the running
    handler's span, or outside the shards an ASP.NET Core request's. An ask keeps the asking handler's context and
    writes it on its `Answered` mail, so the handler of the answer continues the asker's trace, not that of whatever
    ran when the reply arrived. Traces start only where the application starts them: an untraced message starts no
    span. Messages to clients carry none.
- Alternatives: a new fixed trailer per piece of metadata (a protocol level each); W3C `traceparent` as text (55
  bytes, parsed); the answer in the trace of whatever handler runs when the reply arrives (an ask's answer would join
  an unrelated trace); a span per message whether traced or not (Orleans' default: a sampler then drops most, after
  they cost).
- Consequences: an untraced frame is unchanged (no block, the golden bytes hold); a send reads the current activity,
  a dispatch one flag. A traced message pays 29 bytes and a span. The block's flag is the
  one change builds without it misread, and none of those was released; later metadata is added without a break.

## ADR-081 Actors are entities: static handlers, answers as mail, everything else components
- Context: actors were objects (`Actor<TId, TState>` subclasses with overrides, `async` handlers holding a turn across
  awaits, call chains to detect cycles, turn watchers for long turns), and the runtime kept per-actor objects beside
  the entity (mailbox queues, session sets, directories). That is two models in one process: EasyEcs data and systems
  for the simulation, objects and continuations for messaging. Objects and suspended turns cost allocations and
  cache misses, make interleaving rules subtle (a turn that awaits blocks its actor; cycles deadlock until ADR-059
  failed them) and duplicate what EasyEcs (4.2 and later; DaServer is on 4.3) does natively: value indexes, hierarchies, dynamic buffers, change
  tracking, ordering, error policies. Peers: Unity DOTS and Bevy keep behaviour in stateless systems over data;
  Orleans and Akka keep it in objects with turn-based or message-only semantics (Akka's `ask` returns a future the
  actor pipes back to itself as a message).
- Decision:
  - **An actor is an entity** with an `ActorKey` component (`IIndexed<ActorKey>`: one value-index lookup routes a
    message), its lifecycle as tags (`Loading`, `Ready`, `UnloadRequested`, `Leaving`, `Fenced`, `Removing`), its mail
    in a `DynamicBuffer<Envelope>` while it is not live, and its user state as components. A service is a static class
    with no entity; its state is singletons. Per-session state is child entities (`KnownSession` + `ChildOf`, indexed
    by session; tags `Known`, `Granted`, `Superseded`), destroyed with the actor.
  - **Behaviour is static `[Handle]` methods** in `[Actor<TId>]` / `[Service]` static classes. The generator reads each
    method's parameters (the message `in`, components `ref`/`in`, singletons, `Entity`, `World`, `in Context`) and
    emits one dispatcher per handler: no reflection, no virtual call, components added with their default when absent.
    Settings are attribute constants (`UnloadAfterIdleSeconds`, `SaveEverySeconds`, `MailboxCapacity`, `OnError`).
    Lifecycle is mail: `Created`, `Loaded`, `Unloading`.
  - **Handlers never wait.** A handler returns its reply (`T`), or `Answer<T>.Later` after taking `ctx.ReplyLater()`;
    `ctx.Ask` returns a call number and the answer arrives as `Answered<TRequest, TReply>` mail, carrying the
    `ReplyTo` the asker forwarded, so a chain of asks needs no state of its own. `ctx.ReplyAfter(call, value)` replies
    when a call completes. A waiting request is an entity (`PendingReply`; `ReplyTo` is its `EntityId` and call id, so
    a stale token replies to nobody) expired by a system. There are no turns, so no call chains, no cycle detection
    (A asks B asks A simply works) and no long-turn watcher.
  - **The runtime follows the same rules:** one system per lifecycle tag, ordered with `[UpdateAfter]` along the data
    flow, failed systems disabled and revived (EasyEcs `SystemErrorPolicy`), stored components change-tracked
    (`ITracked`, declared by the generator for `[Persist]` types) so a checkpoint visits only written actors.
- Alternatives: keep actor objects and move only their state into components (two models stay; a suspended turn
  still holds the actor); a shard-level mail queue instead of a per-actor buffer (faster to drain, but mail is the
  actor's data and leaves with it); `await` inside handlers with continuations posted back (allocations per await,
  interleaving rules, cycles).
- Consequences: business code is data and pure functions; what the old model did with an `await` is now an
  `Answered` handler and, when it must remember something, a component. A map change or a purchase that spans asks is
  explicit state the actor can be inspected in. A second request may arrive while a first one waits for an answer: a
  handler that must serialize such work says so in data (a component marking the work in progress). The client SDK
  and code outside worlds (HTTP, tests) still `await cluster.Ask(..)`.

## ADR-082 Programming model: actors as entities in EasyEcs 4.7 worlds, [On<T>] handlers as single-entity jobs, presences
- Context: ADR-081 made actors entities, but kept a second vocabulary beside EasyEcs: `[Handle]` dispatchers that copy
  components out and back, a `Context` value, an `ActorKey` beside the id type, a mailbox drained apart from the
  tick, session tags (`Known`, `Granted`, `Superseded`), tick rates in configuration and messages that do not say who
  sends them to whom. Writes copied back stamp tracked components whether they changed or not, so "write only what
  changed" leaks; the order between mail, delayed sends and systems is implicit. EasyEcs provides what a
  model without these needs: single-entity jobs (`entity.Run(ref job[, filter])`), group filters carried by
  interfaces (`ISystemGroupWith<T>`), `world.Descendants(root)` and `LoadEntitiesInto(…, markWritten: false)`.
- Decision: the model of [drafts/programming-model.md](drafts/programming-model.md), structured as in
  [drafts/architecture.md](drafts/architecture.md):
  - **The actor type is its address, its root component, its root prefab and its system group**:
    `[TickRate(30)] partial record struct Map(long Id) : IActor<Map>` (`IActor<T>` inherits `IEquatable<T>`, so
    consumers repeat no constraint); an actor is the root and its descendants, all tagged `In<Map>`;
    `[UpdateInGroup<Map>]` systems see only them.
  - **`[On<T>]` methods in `[Handles<K>]` classes are EasyEcs single-entity jobs** on one subject entity: the
    parameter rules are `[ForEach]`'s, `ref` points into storage, only written components are stamped; the actor key
    parameter binds to the item's root whatever the subject. The exception is a handler with no component
    parameters: EasyEcs's `entity.Run` takes one to nine components, so the dispatch calls it directly.
  - **Each actor is a discrete-event simulation**: mail, due items and the immediate chain run one at a time by
    `(time, seq)` in an ordered phase, then the actor type's systems, then the framework's settling and cleanup. What
    leaves an item is data in the `Outbox` singleton, serialized on append.
  - **Messages state their endpoints**: `IFromClient`/`IToClient` in Protocol (no actor types, no addresses),
    `IMessageTo<K>`/`IRequestTo<K,R>` with `[From<K>]` in Server; answers are `Replied<TReq[,TState]>` mail whose value
    is read with `TryGetValue`.
  - **Data over callbacks**: deactivation, the moments around a hot restart, leaving and confirmation are tags that
    systems react to; only `Activated` is mail, since it must precede the queued mail.
  - **Presences replace sessions**: `Join()`/`Leave()` (`root.JoinClient(out presence, prefab?)` finds or spawns the client's
    presence and returns a `Status` like `Join()`), at most one presence per actor and `PresencesPerClient` per actor type, `[To]` client messages straight
    to the target, the gateway a server type hosting the built-in `Connection` actor type, ordered connection stamps,
    the SDK re-joining. What a client message may activate, which key fields come from its claims,
    several presences and where a message without `[To]` lands are ADR-095.
  - **Distribution**: activation epochs claimed with the placement version and fenced writes, membership as a table in
    the store, placeholder roots, records of persistent entities through `Descendants` and `ComponentSet`, migration
    bytes checked by layout hashes under a new epoch, remaining time across processes. Each piece closes a hole a
    crash, a pause or a partition would open:
    - **membership is Orleans' protocol** ([cluster management](https://learn.microsoft.com/dotnet/orleans/implementation/cluster-management)),
      behind the feature point `IMembershipTable` with a PostgreSQL implementation and an in-memory one: one row per
      node incarnation (`daserver_members`: name, generation, address, server type, status `Joining`/`Active`/
      `ShuttingDown`/`Dead`, suspicions, `restart_until`, `dead_seen_at`, start time, `i_am_alive`) and one version row per cluster
      (`daserver_membership`: version, slot table, per-type placement versions). Every change is one transaction that
      compares and swaps the version, so changes are totally ordered and every node that read version v derives the
      same members, coordinator and slot table. The store is the arbiter, so there is no quorum, ballot or configured
      cluster size, and any number of members may fail;
    - a node joins by inserting itself `Joining` with generation max(Unix ms, recorded max + 1), marking older
      generations of its name `Dead` in the same transaction, checking two-way connectivity to every `Active` member
      (skipping rows whose `i_am_alive` is older than the newest one in the table by more than the staleness bound,
      so the store's clock alone decides; a member that skipped one casts no suspicion vote until it has probed every
      member once) and writing itself `Active`; it hosts and serves nothing before. No
      seeds: nodes find each other in the table;
    - failure detection: members probe up to `ProbedNodes` (10) successors on a ring every `ProbeInterval` (1 s);
      after `MissedProbes` (3) and a failed indirect probe they write a suspicion stamped with the store's `now()`, and `VotesForDeath` (2, or every
      other `Active` member when fewer remain) suspicions within `SuspicionExpiry` (3 min) make the writer declare the
      target `Dead`. A writer must be `Active` at the version it writes, so in a partition the first death written
      wins and the other side's compares fail: a node that reads itself `Dead` freezes, fences and exits, and a
      two-node split keeps exactly one node. Nodes read the table every `TableRefresh` (5 s), both tables as one snapshot (one `UNION ALL` statement), and push snapshots after
      their writes; frames carry the table version;
    - **an isolation lease**: a node runs its worlds only within `IsolationTimeout` (20 s) of *issuing* its last table
      read that returned it `Active` (a snapshot pushed by a peer never renews it), measured on a clock that counts
      suspend (`CLOCK_BOOTTIME`, `QueryInterruptTime`, `mach_continuous_time`), and every shard checks the published
      deadline itself before each ordered phase and before handing frames to the links, so neither a starved thread
      pool nor a suspended machine lets stale work through; that clock cannot see a VM the hypervisor stopped or a
      CRIU restore in a time namespace, so every read also returns the store's `now()` and a store clock running more
      than a second ahead of the lease clock between reads is treated as a stall (stale work is then possible until
      the next read, at most `TableRefresh`, stopped by epoch fencing for persisted writes); membership uses a small
      data source of its own, never queued behind the actors' write backlog;
    - **no table, no work**: past its lease a node freezes its worlds (it stops draining its dispatch queues, which
      stay bounded) and keeps its process and connections; a read that shows it `Active` resumes it, `Dead` stops it.
      While frozen it answers probes `Isolated` (a failed probe for a prober whose own lease has held for
      `IsolationTimeout` and that is not itself recovering from a cluster-wide freeze: one more `IsolationTimeout`
      and its write backlog drained, while its store traffic ramps up from one write per world, so a recovering
      store does not cascade into deaths); isolated alone (some peer is not), it also fails readiness and exits with 75 after
      `IsolationExit` (1 min), so a node that reaches its peers but not the store gives its slots up. The cost is stated plainly:
      **a store outage longer than `IsolationTimeout` pauses the whole cluster** until the store is back (the store
      already holds every record, so a stateful cluster could not save through the outage either), and then nobody
      exits, since every peer is isolated too. A stall guard (a gap in the membership loop longer than `ProbeInterval`
      keeps the worlds frozen until a read confirms `Active`; probe misses across it do not count) only shortens stale
      work;
    - **a dead member keeps its slots until it is re-placed**: it stays in the placement set, its keys answered
      `Unavailable(retryAfter)` by the senders, until the coordinator removes it in one versioned write (the
      placement-version bump) once the store's `now()` passes `dead_seen_at + IsolationTimeout + DeathMargin` (5 s).
      `dead_seen_at` is the store's `now()` of the coordinator's first write after it read the death, so it is later
      than the death's commit even when the death's writer stalled before `COMMIT`; so a cut-off node has frozen
      before any other node activates its keys, whatever the nodes' clocks say, and a persisted key is fenced by the
      epoch compare as well. Indirect probes keep one-way unreachability from killing anyone: it is reported
      (`daserver.link.unreachable{from,to}`), not guessed at;
    - **the coordinator is a function of the table**, replacing ADR-079's "oldest Up member" by its table form: the
      `Active` member with no `restart_until` (whatever its value) with the oldest start time (then name) at the
      version read: a rule on the table alone, never a clock, so every node reading that version agrees; a node
      hot-restarting is never coordinator, so the next one by the rule writes it `Dead` if its deadline passes. A
      node whose hot restart was aborted (a hung store) may clear its own `restart_until` by a versioned write while
      its row is still `Active` with its own generation, deadline passed or not, so a store that comes back late does not
      cost it its unsaved state. Its decisions (slot moves, a hot
      restart's permit and deadline, declaring an expired restart `Dead`) are versioned writes, so a stale
      coordinator cannot act; a hot restart keeps the row, generation and start time;
    - without a store a node starts only with `DaServer:Membership:SingleNode = true` (one node: local runs,
      `AllInOne`), served by the in-memory table; any other node without a membership store (the actor store's
      PostgreSQL or `WithMembershipStore(db)`) refuses to start, so a misconfigured replica never forms a second
      cluster of one, and `SingleNode` together with a store refuses to start too; a single node's hot-restart handover carries its member row in its header, which seeds the standby's in-memory table, so its hot restart keeps its worlds. `DaServer.Aspire` sets `SingleNode` for an AppHost with one node and no store and adds PostgreSQL
      when it has more than one. `TestCluster` shares one in-memory table, wrapped per node in a fault-injecting
      `IMembershipTable` (failed or delayed reads and writes per node);
    - taking a key from another owner needs a strictly newer placement version (the coordinator bumps a type's version on every
      placement change, a membership change or a slot-table move (ADR-079): the table version of that change, so
      every node derives the same versions; frames carry the highest seen; a
      migration's arrival writes it too), and a claim that displaces a live owner sends it
      `Fence(key, epoch)`, so two activations never overlap until the loser's next write; claims are idempotent, so
      a retried statement or a restart after a migration's arrival does not fence the claimant itself;
    - a move hands off before the target claims (ADR-072's "mail first", kept): mail for a key of a slot the node
      just received parks on an `Arriving` placeholder while the node asks the source (`HandoffQuery`, carrying the
      slot and the target's placement version, which the source records for that slot only, so it forwards the slot's
      keys from then on while its own claims keep its own view's version), which answers
      with the migration bytes, "not here" or "postponed" (it keeps the actor and the mail is forwarded to it), so a
      claim never races a live source and the placement version decides only after a source has left;
    - a root's record writes never overlap in one process (the next waits for the one in flight), and the CAS
      compares the epoch and the root's write generation (`write_gen < :write_gen`; every epoch claim resets it to 0,
      an in-place hot restart continues it from the handover), so a timed-out statement that lands late never
      overwrites newer state; a failed write is retried with the same bytes and generation, and a row found at its own
      epoch and generation counts as written; every claim takes its epoch from a sequence (an insert, a takeover and the
      no-insert claim alike), so epochs only rise across the store and a key whose never-written row was swept never
      reuses an epoch and a late statement or an old `Fence` cannot match it;
    - generations are max(Unix ms, recorded max + 1), so a cold restart of the whole cluster never reuses a
      generation, and the first writer creates the version row, so parallel starts never form two clusters.
  - **Replies, presences and retries are data with stated guarantees** (the table "每条路径保证什么" in the draft's §11):
    - Everything one sender sends to one target arrives in send order, answers included (ADR-096). Ordinary progress is
      written in batches; a valuable change calls `root.Commit()`, which holds the item's output until the tick-end
      write lands; an order is its own actor, and the step that moves value into another actor is a `[Once]` request (framework
      sequence numbers, the reply kept in `Unacked` until the cumulative acknowledgement, ADR-099) sent by
      `AskUntilAnswered`, whose request is the persisted intent the runtime asks again after a crash (ADR-097, ADR-100). `Writing`, `WriteNow` and the
      write generation are fields of the framework's `RootState` component, not tags, so `Commit` is no structural
      change.
    - A lost connection or gateway leaves its presences `Detached` for a grace (30 s, `DetachGrace`) instead of
      `Leave()`: after a region blip or a gateway crash, clients come back to presences that are still there, and
      actors rebuild nothing. A takeover sends `TakeOver` to the old connection's gateway; a newer login (a larger
      token issue sequence, a per-account counter persisted before the token leaves and kept by a refresh; one login is
      one `ClientId`, so an equal sequence under another `ClientId` counts as newer too) closes the old device as
      terminal, so two devices never fight by reconnecting; one login's connections are ordered by an attempt counter
      the SDK carries, not by gateway generation, so a reconnect through an older gateway still takes over. A `Connection` is keyed by a
      per-connection `ConnectionId` and placed by its hash like every actor, with the `ClientId` a non-unique index used
      only to find a login's connections (`cluster.Disconnect` asks each world): one login can hold two connections on one
      node (a retire or a half-open reconnect landing on the same gateway), an actor key is unique, and every frame to a
      connection carries its `ConnectionId` (the presence records it), so one login's connections are settled by stamps
      at the targets, on one node as across nodes.
    - `Joined(MissedPushes)` tells snapshot systems whether to resend (`[Changed<Joined>]`; users need not learn
      `Retire` or stamps), and `Retire` keeps ADR-026's fence (a `HandedOff` marker on the old route) so each actor's
      pushes stay in order; the client's handshake carries the retire flag, so `[JoinOnConnect]` presences get the fence
      too.
    - A pause never extends a request's budget (ADR-028's rule, kept across resend windows, migration and hot
      restart); nested asks inherit the remaining time; `ReplyTo` carries its deadline (a UTC instant once it is in a record).
    - A status has two classes, `NotExecuted` (definitely not run) and `Transient` (worth retrying); the framework
      retries only not-executed refusals on forwarding hops, under a per-node retry budget (gRPC's retry throttling),
      and joins with jittered back-off; `Busy` carries a retry-after: blind retries of unknown outcomes duplicate
      effects, unbounded ones amplify an outage. Client call ids are per session across reconnects and prefixed by
      `ClientId`; they correlate replies only, and a retried change carries its business key (ADR-097).
    - Time inside an actor comes from `Time` only: `Elapsed` (monotonic) for order and deadlines, `Utc` (the world's
      `TimeProvider`) for instants kept across deactivation; the analyzer warns on `DateTime.UtcNow`.
    - Overload is data too, internal and exported as metrics: `Pending` on the root (idle only at zero), the node
      singletons `StoreDegraded`, `ActivationSlots` and `PeerHealth` (node limits split into per-world shares, so no
      counter is contended), a per-actor item budget per batch, an admission bucket for new connections,
      and an `ActivationFailed` placeholder for an actor that throws on every activation. The due heap compacts
      in place once its stale entries outnumber the live ones, so answered asks' timeouts cost no memory for long.
  - **Rules for sharing a world** (the draft's §5; checked against Overwatch, Bevy and EasyEcs): a world holds many
    actors, so `[On<T>]` methods and actor-group systems take a user singleton `in` only, and only an
    `IWorldSingleton<T>` (a world's cache or configuration, not actor state) may be taken `ref`; systems of an actor type without `[TickRate]`
    run after every mail batch, so each `[ForEach]` there must narrow by `[Changed<T>]`, `[Removed<T>]`, a tag or
    `[EverySeconds(n)]` (an analyzer error otherwise; EasyEcs's `UpdateInterval` counts updates, which such a world
    runs only on mail, so time-gated systems use `[EverySeconds]`, and a quiet pass every `DaServer:QuietPass` updates an idle
    world); a throwing handler of an actor type whose root persists restarts
    it from its last record (a reload: the root is reset to its `RootPrefab` copy's values keeping only the key and the
    mail, the batch's remaining items move into the mail, the root's in-flight write finishes
    before the claim and write completions count only for their own activation epoch, consecutive reloads back off
    like failed activations), since EasyEcs applies changes at once without rollback and a half-applied item must
    never reach a checkpoint; a type without a record continues. There is no setting: the other choice for either
    kind is unsound.
  - **Writes are asynchronous and batched by default**: changes tag roots `Unsaved`; at the end of every tick (after
    every mail batch for actor types without `[TickRate]`) a world writes the roots due in one write (ADR-068's
    batch over arrays with `unnest`, kept, one transaction), each row with its own epoch CAS: roots that called `Commit` every
    time, other `Unsaved` roots at their staggered checkpoint instant, deactivations' final writes with them (ADR-097
    has the caps, the error handling and the checkpoint order). Graceful exits (deactivation, scale-in, rolling upgrade,
    hot restart, shutdown) lose nothing but the unwritten changes of a root whose state does not serialize or whose row
    the store refuses, which keeps its last record, and, for a shutdown, what its stop budget could not drain (logged);
    a crash loses the larger of the last checkpoint interval and the backlog's drain time. Arming a hot restart writes every `Unsaved` root once
    while the actors keep running, so a crash of the new process after a handover loses only the changes made since.
  - **Persistence is per entity kind, not per component type**: entities spawned from a persistent prefab (`[Persist]`
    on the actor type's `RootPrefab` or on a child prefab, `[Prefab<K>, Persist]`) are saved with all their
    components but `[NotSaved]` ones, and carry `Persisted(Root)`; others are not saved. Every entity spawned from a
    prefab carries `FromPrefab(Prefab, Composition)`. The draft's own example forced this: a drop's
    record on the non-ticking `Loot` actor must be restored with its `Prize` and `Position` (and its `Granting` intent),
    while the map's visible drops and its avatars carry the same `Position` and must stay out of records;
    per-type persistence (a marker interface on component types) either lost the record's `Position` or dragged every
    avatar into the record. Change tracking is a per-type column in
    EasyEcs whose filter (`ChangedAny<T1..Tn>`) names its types at compile time, so persisted component types declare
    `ITracked` in source, the generator emits the tracked-type list and the filters from those declarations (each DaServer package's generator emits its share (its tracked types, prefabs and systems) as `internal static Registration <package namespace>.Generated.PackageHooks.Register(Registration)`, always, an empty one when it has nothing to register, and the server project's generator calls the hook of every referenced DaServer package in `Registration.Create()`'s chain; buffers enter the server project's list only when their element type is declared there), and
    `Registration` throws at start when a type on a `[Persist]` prefab (the run-time persist set) lacks it; the change
    system runs `ChangedAny` over the tracked types that reach records (`[NotSaved]` types and buffers of `[NotSaved]`
    elements left out, so a buffer emptied every tick does not rewrite every record; balanced calls of 2 to 9 types, a
    single type as `ChangedAny<T, T>`/`RemovedAny<T, T>`, never `Changed<T>`, which would AND with the group) filtered by `Persisted`: one visit per changed persistent row, nothing allocated.
    Generated accessors (`entity.X`) of tracked types are `ref readonly` in EasyEcs 4.7, so reading never stamps;
    records are taken outside every pass and job (`SaveEntitiesWith` throws inside one). Spawning a persistent entity tags its root
    `Unsaved` directly, and placeholders (`Loading`, `Saving`, `ActivationFailed`) are never checkpointed, so a
    `Reload` cannot write prefab defaults over the last good record. Records evolve by prefab: every load (record, migration bytes,
    hot-restart handover) adds the `[NotSaved]` components (they arrive with the prefab's default) and those the entity's prefab
    gained since its composition (the manifest keeps earlier compositions; components removed at run time stay
    removed), writes the record again when it added persisted ones, skips unknown type ids, counted, and the upgrade
    check refuses a release whose new fields or record types a rollback would drop, unless forced. Ask states and waiting entities are not in
    the record.
  - **The wake is the runtime's alone**: descendants that must wake an unloaded actor carry a persisted `WakeAt`
    (a reminder, the delete check), and an `AskUntilAnswered`'s ask row carries its own next try; the runtime keeps the
    root's `NextDue` at the minimum of both and
    rewrites `next_due` on every activation by `Wake`, so a stale column never wakes an actor twice. One runtime system,
    `ArmSystem`, arms a `Due` from `WakeAt` on load or activation, re-arms it when `WakeAt` is rewritten and removes it
    when `WakeAt` is removed; when it fires, the wake handler registered for the entity's component runs (the runtime's
    re-ask, a package's reminder delivery), so a re-ask and a reminder wake the same way and no user system reacts to a
    wake.
  - **Identity and user singletons are interfaces**: `Account(long Id) : IClaims` with `IClaims : IComponent`, so the
    constraint of `[JoinOnConnect<T>] where T : struct, IClaims` and EasyEcs both see it; no attribute for the
    generator to complete. User singletons are the same: `IConfigTable<TSelf>` (static abstract `Name` and `TryLoad`;
    one cluster-wide current version per table with a store, per node from configuration without one) and
    `IWorldSingleton<TSelf>` (static abstract `Create`).
  - **EasyEcs 4.7 features replace workarounds** (the draft's header): `Mut<T>` with `SetIfChanged` for components
    recomputed every tick, `world.Instantiate` with given components for `root.Spawn`, `ChangedAny`/`RemovedAny`
    (one filter moved with `Since(tick)`) for the change system and replication, `world.SaveWithout(output, in exclude)` and
    `LoadKeeping(input, in keep, markWritten: false)` so user singletons (configuration tables, world singletons) stay out of
    a hot restart's handover, `IUniqueIndexed<T>` (`Get`/`TryGet`) for actor and child keys, `TrimExcess`, `Slices` for
    the idle scan, `Descendants(root).AddTo` for collecting records and migrations, O(1) `Find(key).Count`,
    `Children(parent).Count` and `index.KeyCount` for interest checks and the inspector, `DestroyEntities(ids)` for destroying a subtree in one structural gate, `world.Read` for the inspector's and the admin plane's read-only passes (no tick stamped), and `SystemStats` and
    `DescribeSchedule` for the inspector. Tracking is declared in source, so no run-time switch is needed.
  - One project may define several server types; a node runs one.
- Alternatives: keep ADR-081's handlers and add the missing pieces (copy and write-back stays, two vocabularies stay);
  handlers as systems over message entities (an entity per message costs a create and a destroy; the runtime's due
  heap and chain stack give the same order without one); optimistic execution with rollback (state copies cost as
  much as a ~1 µs event, pushes cannot be taken back); spatial parallelism inside an actor (needs game concepts in the
  framework, ADR-045); deduplicating every request (a store write on every request; ADR-099 deduplicates only the `[Once]` step that moves
  value, until its sender acknowledges); `Leave()` at once on a lost connection (a
  reconnect storm rebuilds every presence); every reply durable (a store write of latency on every request);
  persistence per component type (above); a self-made quorum membership (majority-committed views, a lease renewed by
  majority acks and a configured formation size: it freezes a whole cluster when fewer than half its configured
  members remain, cannot tell a two-member split from a crash, and needs seeds and a formation rule, while the store
  every stateful cluster already depends on can arbitrate by itself); keeping nodes running through a store outage, as Orleans
  does (it only stops changing membership): a node cut off from the store and from part of the cluster could keep
  serving keys of a type that saves nothing after the others re-placed them, since only a persisted key has an epoch
  to fence it, and a node paused past its death would wake up serving; the freeze trades availability during an
  outage for one activation per key everywhere;
  Raft or an external coordination service for membership (a second consensus system to operate beside the store);
  a target claiming a moved key at once and letting the store decide (a claim races a source still
  running the actor); `UpdateInterval` for time-gated systems (it counts updates, and a world without `[TickRate]`
  updates only on mail); pausing the actor until its write lands (a
  ticking actor stalls with every presence on it); a committing root written alone (a write per root where one per
  world serves them all); checkpoints sliced across the interval (several
  writes per interval where one serves the world); a separate log beside the records (Akka Persistence's journal
  plus snapshots: batching records already writes many actors in one write, and a journal adds compaction, replay
  and fenced appends for little gain at our record sizes; revisited only if BenchmarkDotNet shows record size
  dominating the write).
- Consequences: breaking for every user project and most of the runtime (roadmap phase W). The timer defaults are one
  table in the draft's §11 (probes every 1 s, 3 missed, 2 votes within 3 min, table refresh 5 s, isolation 20 s,
  link silence 3 s). A cluster of more than one node needs a store, gateways included (the membership table is
  there). ADR-023's gossip membership, seeds and "keys move when a member is Dead", ADR-031's heartbeats as the
  failure detector and ADR-064's keep-majority are replaced by the table: death is written by suspicion votes,
  a dead member keeps its place (ADR-007's and ADR-079's "Up or Suspect" becomes "`Active`, or `Dead` and not yet re-placed") until the coordinator's re-placement write after `dead_seen_at + IsolationTimeout + DeathMargin`, and a hot restart past its deadline is written `Dead` by the
  coordinator; those ADRs are rewritten when W10 promotes the drafts, together with ADR-068 (a claim without its `INSERT`
  branch for mail that may not create a key, the `CanCreate` gate, no rows for types that persist nothing, the
  never-written rows' sweep), ADR-072 (the handoff query before a claim, carrying the target's version) and ADR-079 (the
  coordinator and the slot table derived from the membership table). Business code is
  `[On<T>]` methods and EasyEcs systems over data; the generator emits jobs, typed sends, typed reply accessors and
  one `Registration` instance. ADR-081's handler, `Context`, session and lifecycle parts, ADR-012's module initializer
  registration and ADR-017's one project per server are replaced when W10 promotes the drafts.

## ADR-083 Native AOT support
- Context: a native executable starts without JIT warm-up, keeps a smaller working set and ships without a runtime;
  the owner decided DaServer must support it. EasyEcs's core is `IsAotCompatible`; Nino serializes through generated
  formatters. What blocks AOT today is DaServer's own reflection (route listing, admin JSON of anonymous objects,
  reflection-based JSON bodies, generic configuration binding); after W it would be generic code closed at run time,
  and in-process hot reload, which loads assemblies at run time.
- Decision:
  - **Every node can publish with `PublishAot=true`**, chosen per node; JIT images stay supported (requirement E11).
    The release checklist publishes the samples' nodes with AOT. Framework projects set `IsAotCompatible` on net11.0,
    so the trim and AOT analyzers run on every build.
  - **Registration is generated as closed types in the user's assembly**: `Registration.Create()` names every actor
    type, dispatch table, job and system instantiation, the framework's per-actor-type systems included (they have
    no `[ForEach]` methods, so EasyEcs never closes their code with `MakeGenericType`, EasyEcs D20). No reflection,
    `MakeGenericType`, `Activator` or assembly scanning; the analyzer reports a generic `[ForEach]` system closed
    outside its assembly, which EasyEcs would close at run time without a warning.
  - **The HTTP plane** uses `WebApplication.CreateSlimBuilder`, minimal APIs through the request delegate generator
    and no JSON in the framework: admin endpoints and `MapAsk`/`MapSend` take and answer `application/x-nino` through
    Nino.AspNetCore's Minimal API bodies and results, with plain text for humans ([drafts/architecture.md](drafts/architecture.md)
    section 8 has the rules).
  - **Code changes load no assemblies at run time**: JIT and AOT nodes take a new build the same way, by hot restart
    or rolling upgrade (ADR-084), so user projects are `Game.Protocol` and `Game.Server`.
  - **0 warnings, no suppression**: a suppression of a library's warning in user programs is not accepted. EasyEcs
    4.7 publishes without warnings with tracked components, which every stateful node has (its persistent entities').
- Alternatives: AOT only for stateless nodes (no tracked components; splits the support matrix); suppressing a
  library's AOT warning (accepts it on the library's behalf); in-process hot reload on JIT only (rejected in ADR-084);
  reflection-based registration with trimming descriptors (fragile, and generic closure at run time is impossible
  under AOT anyway).
- Consequences: the release checklist publishes each sample node with `PublishAot=true` with 0 warnings and runs the
  E2E tests against the AOT binaries. What remains to verify is third-party: OpenTelemetry, the Prometheus exporter
  (a beta, pinned; OTLP to a Collector if it warns), Npgsql through Aspire, Nino.AspNetCore and the other hosting
  dependencies, in W8
  ([drafts/architecture.md](drafts/architecture.md) section 8). How a new build reaches running nodes is ADR-084.

## ADR-084 Code changes ship by in-place hot restart (JIT and AOT); no in-process hot reload
- Context: games change code while players stay connected. The usual way (ET, in-house game servers) loads a Logic
  assembly into a collectible `AssemblyLoadContext` and swaps it. Native AOT (ADR-083) cannot load code at all, so
  AOT nodes would need a second mechanism. Unloading is unreliable: any lingering reference to the old assembly (a
  static cache, a delegate, an event, a thread-local) keeps it alive, a leak per reload. A separate Logic project
  caused a cycle between the host and the logic projects (ADR-067), and a load context swaps only the logic:
  framework, runtime and dependency updates restart anyway. Re-activating every actor from its record on each change
  costs a checkpoint interval of state and a load storm on the store.
- Decision (the mechanics are [drafts/programming-model.md](drafts/programming-model.md) §10, nowhere else):
  - **A code change replaces the node's process in place**, the same on JIT and AOT nodes. The `{cluster}-restart`
    command arms the node with the new build's `wire.manifest`; the diff picks *in place* (same layout hashes, no
    handler or tag consumer dropped, same client routes), *through records* (deactivate, restart, activate) or a
    refusal in favour of a rolling upgrade.
  - **One path on every platform: a warm standby handover** (ADR-098). The new build starts beside the node as its
    pair's other instance and warms up; the worlds freeze between two ticks while IO keeps receiving; the node streams
    the worlds, the queued mail, its link state and its listening sockets over a local pipe, and exits after the
    standby's ack, so process start and warm-up stay out of the pause.
  - **Identity and epochs are kept**: the new process reconnects as the same (name, generation), and peers resume its
    streams from their resend windows, as after any dropped link. Activation epochs live in the roots and in the
    store, so nothing re-activates and nothing is fenced.
  - **`NodeRestarting` (the node's membership row holds `restart_until`, its deadline)**: peers keep the node in the
    table and in placement and hold messages in their bounded resend windows until it clears the deadline or the
    coordinator (ADR-079) writes it `Dead` once it has passed; ADR-098 says when
    the restart starts, how the standby rejoins and what the coordinator guards meanwhile.
  - **Fallbacks when layouts change**: records that stay readable take the path through records; changes of hosting
    or client routes take a rolling upgrade. Gateways always roll, surge then drain with `Retire` (ADR-026). The
    upgrade check also refuses a release whose records the running build could not keep (no safe rollback) unless
    forced, and load moves wait until the stateful groups have rolled (moves between builds fall back to records).
- Evidence: moving a world costs milliseconds and allocates nothing. BenchmarkDotNet ShortRun, EasyEcs 4.5.0,
  .NET 11 RC2, one world with 5 unmanaged components per entity: 10,000 entities `Save` 44 µs, `Load` 183 µs,
  440 KB; 100,000 entities `Save` 0.76 ms, `Load` 2.7 ms, 4.4 MB; 0 B allocated in both. With the standby already
  started, the pause is the freeze, the stream and the load: about 0.1–0.3 s at 100,000 actors and 0.3–0.7 s at
  1,000,000 (ADR-098), measured end to end in roadmap W9.
- Alternatives: a collectible `AssemblyLoadContext` with `RemoveTypes` and a dispatch-table swap between ticks (JIT
  only, unload leaks, a Logic project and its cycle, swaps the logic only; weighed again as an optional JIT-only mode
  in ADR-098 and rejected); `MetadataUpdater` deltas (a development tool, off in Release builds); re-activation from
  records on every change (a checkpoint interval of state lost, a load storm); `exec` into the new binary (no Windows
  equivalent); a successor launched by the node itself and joined beside it (ADR-098).
- Consequences:
  - Two user projects, `Game.Protocol` and `Game.Server`; ADR-067's reloadable logic project goes when W10 promotes
    the drafts.
  - What changes without a restart is data swapped between two ticks: tuning tables (`IConfigTable<T>` singletons
    with one current version per table in the store, loaded by every starting or hot-restarted node, seeded from
    configuration when the store has none, replaced cluster-wide by `POST /admin/tables/{name}`; a rollback posts the
    old version again; a node that cannot load the current version is not ready, and the upgrade check tries the new
    build's `TryLoad` when the manifest's table format hash changed; without a store, tables are per node from
    configuration) and feature flags choosing between handlers compiled into the binary.
  - `[Stateless]` actor types deployed as their own group are replaced start-new-then-stop-old, without a pause.
  - The new process loads with `world.LoadKeeping(bytes, in keep, markWritten: false)` (EasyEcs D39), so no actor rewrites
    its record and no `[Changed<T>]` method sees the loaded rows; a system that needs a full pass filters `Resumed`.
    User singletons (configuration tables, world singletons) are the new process's own: the old one saves without them
    and the new one loads the cluster's current tables and creates its world singletons before the load, which keeps
    them (`Save(output, in exclude)` and the `keep` set of singletons). The handover records its world count, and the standby
    creates that many worlds whatever its CPU share; only a configuration pinning another count goes through records.

## ADR-085 Actor-hosting groups publish as StatefulSets with `OnDelete`; hot restart hands over between a pod's two containers
- Context: ADR-030 publishes every node group as a Deployment: a node's identity was its random id and its actors'
  state lived in the store, so pods needed no stable names. Hot restart (ADR-084) keeps a node's identity (name and
  generation) and its in-memory world across a code change by a handover to a standby beside it (ADR-098); on
  Kubernetes the standby is the pod's second container, so the pod must not be replaced. A Deployment replaces pods on every
  template change (new names, new IPs, empty volumes).
- Decision:
  - **Node groups that host actors publish as a StatefulSet with `updateStrategy: OnDelete`**: pods get stable names
    (`{group}-{ordinal}`, the node name, `Environment.MachineName`), and a template change replaces no pod ("the
    StatefulSet controller will not automatically update the Pods"; pods are replaced only when deleted). The
    headless `{group}-peers` Service stays (seeds, `publishNotReadyAddresses`) and is the StatefulSet's `serviceName`.
    Gateways (surge then drain) and stateless groups stay Deployments.
  - **Two node containers per pod**, `node-a` and `node-b`: the same image and configuration, the pod's network
    namespace and a small `medium: Memory` `emptyDir` for the handover's lock, pipe and arm file; the one holding the
    lock is the node, the other parks (a few tens of MB) or, during a roll, is the standby. Each serves its probes on
    its own port and the idle one reports ready, so the pod's readiness follows the node. Both carry the same memory
    limit; requests follow the role (the node size on the node's container, the idle size on the parked one, `node-a`
    rendered node-sized so it is the node at a cold start), and the rollout swaps them in place at each handover (the
    pod's `resize` subresource, `resizePolicy` `NotRequired`; the processor count is the node size as a literal, so no
    resize changes a process's parallelism); when the machine cannot fit the resize, the arm is refused and the pod
    takes the delete-and-recreate path through records.
  - **Hot restart on Kubernetes**: the release itself (`helm upgrade` of the published chart) sets the StatefulSet's
    template image; with `OnDelete` that replaces no pod, and pods created later get the new build. The rollout then
    runs as DaServer's own in-cluster Job with a namespaced Role (its verbs: [drafts/deployment.md](drafts/deployment.md)
    section 5): it first has every node pull the new image (a pod with minimal requests on each node, so the pull is not inside
    the pause; a node that refuses it for lack of room is a warning, and that node pulls in its pause), then, one pod at a
    time, resizes the parked container, arms the node, applies the parked container's `spec.containers[].image` and
    waits for the node's new instance id with the armed manifest hash. Only the
    image can change on a running pod, so before applying the Job compares the ControllerRevision the pod was created
    from (its `controller-revision-hash` label) with the StatefulSet's `updateRevision`, revision to revision and
    never the live pod (which a VPA, the hostname and default tolerations change); a pod whose revision differs in
    more than the image (env, probes, volumes, resources, the grace period) is deleted and recreated one at a time
    instead, and the Job says so. It never patches the template (the next `helm upgrade` would put the old image back). That field is mutable on a running
    Pod; the kubelet restarts only that container inside the same pod (same pod, UID and IP; the Pod is not
    rescheduled), it comes up as the standby and the node hands over to it; the old process parks in place (it never exits, so its
    container keeps running and the pod keeps its readiness).
  - **A same-image roll** (a config change on an `OnDelete` group) has no image to apply: the Job arms the node with
    its running manifest and has it tell the parked instance over the pipe to exit; the kubelet restarts that
    container in the same pod (`restartPolicy: Always`), re-reading its env and `envFrom`, and the handover follows.
    The kubelet backs off restarts of a container that exited within 10 minutes of its start (10 s, then doubling,
    whatever the exit code); that only delays a same-image roll's standby (a handed-over instance parks without
    exiting), never the pause, so a second same-image roll of a pod within 10 minutes waits before its handover. Secret rotation needs no roll: nodes
    reload the mounted key files (the Secret mounted whole, without `items`, read through a polling file provider every 4 s,
    since file events miss the Secret volume's symlink swap; a reload that reads an empty or short key keeps the old
    keys) and report a key fingerprint, which the rotation step waits for
    on every node before the next step ([drafts/deployment.md](drafts/deployment.md) section 4).
  - **The image is pinned by digest**: the rollout resolves the release's reference to `image@sha256:…` once when
    the roll starts, applies that, and compares the container's `imageID` before and after, so a mutable tag never
    restarts nothing or runs a stale cached image. The StatefulSet's `serviceName`, `podManagementPolicy`,
    `selector` and `volumeClaimTemplates` are frozen from the first release (Kubernetes refuses to change them), and
    a publish test compares them with the previous chart.
  - **One rollout loop, one adapter per platform**: pre-pull, arm, trigger, wait until ready, next, the coordinator
    last ([drafts/deployment.md](drafts/deployment.md) section 5); on Kubernetes the trigger is the parked container's
    image, and the Job writes through Server-Side Apply as its own field manager `daserver-rollout`: that container's
    image, both containers' CPU and memory requests (`resize`), and a pause of the group's scale-in while it runs, so an
    autoscaler never deletes a pod in the middle of its handover (ADR-094). It authenticates with the cluster's admin key
    (the `{cluster}-admin` Secret, ADR-093), never the signing secret.
  - **preStop runs on an image-apply restart too** (the kubelet's container kill runs it whatever the reason; proved
    on kind in W11), so hot-restart groups have no preStop `sleep`: it would only delay the standby.
  - The StatefulSet is named `{group}` (pods `game-0`, …) with `podManagementPolicy: Parallel`. A stateful group
    that also accepts clients itself (no separate gateway, e.g. the flash sale's `sale`) uses `RollingUpdate` (one pod
    at a time, the default): its clients reconnect on any restart, so it gains nothing from keeping the pod.
  - Nodes need no Kubernetes API access: the data plane discovers peers through DNS only, and node pods run without
    a service-account token. The full target shape (Secrets, Jobs, probes, security context, network policy) is
    [drafts/deployment.md](drafts/deployment.md).
- Sources: [StatefulSets: OnDelete, stable network ID](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/);
  [Pods: Pod update and replacement](https://kubernetes.io/docs/concepts/workloads/pods/) ("Pod updates may not
  change fields other than `spec.containers[*].image`, …"; "`ContainerStatus.ImageID` reflects the image from the
  previous generation until the new image is pulled and the container is updated"; one IP per Pod shared by its
  containers); [Pod lifecycle](https://kubernetes.io/docs/concepts/workloads/pods/pod-lifecycle/) (a Pod is
  scheduled once; the kubelet restarts containers within it); [Volumes: emptyDir](https://kubernetes.io/docs/concepts/storage/volumes/#emptydir)
  ("The data in an `emptyDir` volume is safe across container crashes"; `medium: Memory` mounts a tmpfs counted
  against the memory limit); [resize CPU and memory](https://kubernetes.io/docs/tasks/configure-pod-container/resize-container-resources/)
  (in-place pod resize, GA in 1.35). No page says in one sentence that an image patch restarts the container in place; the
  Pod page implies it (the `ImageID` line above), and the W11 E2E proves it on a real cluster (kind).
- Alternatives: Deployments with in-place pod patches (a later template change still rolls every pod, and pod names
  change on any replacement); `RollingUpdate` StatefulSets (the controller replaces each pod on a template change,
  emptying its `emptyDir`); one container per pod with the snapshot file in a memory `emptyDir` (the kubelet's
  container restart and the process start fall inside the pause, ADR-098); a permanent warm standby (twice the memory
  always);
  OpenKruise's Advanced StatefulSet with `InPlaceOnly` updates (rejected: a cluster-wide operator plus a
  host-access DaemonSet, Kubernetes 1.33 to 1.35 not in its support matrix, image pre-pull skipped for three replicas
  or fewer, and arming each node before its restart would still need DaServer's own controller);
  DaemonSets for nodes (their count follows the machines, an HPA cannot scale them, `kubectl drain` does not evict
  their pods so a node gets no graceful leave, and their pod names are not stable).
- Consequences: ADR-030's "no StatefulSet" holds only for gateways and stateless groups once roadmap W11 lands, and
  ADR-030 is rewritten then. Scaling a stateful group stays the platform's and the application's (`kubectl scale`, an
  HPA or KEDA added by the AppHost); a scaled-in pod leaves gracefully. An autoscaled group renders no `replicas`,
  since `aspire deploy` applies with `--force-conflicts` and would take the field back on every deploy; its scale-in
  is one pod per period, set in the autoscaler's behavior (ADR-094). W11 changes `DaServer.Aspire` and the samples'
  AppHosts (roadmap).

## ADR-086 Six packages and four words: Core absorbs Abstractions, the server absorbs Hosting, shards are internal
- Context: the target architecture listed eleven projects (Abstractions, Core, Client, Simulation, `DaServer`,
  Generator, WireTool, Hosting, Persistence.PostgreSql, Testing, Aspire) and four levels of runtime structure (node,
  shard, domain, entity). Several packages are never referenced apart: every client and every server takes both
  Abstractions and Core, every node takes both `DaServer` and Hosting, WireTool only runs inside a build. "Domain" named
  nothing but "an actor's root and its descendants", and "shard" leaked an implementation choice (one thread, one
  world) into the AppHost (`AddNodes(..., shards: 8)`) and configuration.
- Decision:
  - **A package exists when something references it without the others.** Six packages: `DaServer.Core`
    (netstandard2.1, net10.0: the client-facing contracts and the wire and transport; carries the generator, the
    analyzers and the `wire.manifest` tool), `DaServer.Client`, `DaServer` (the server runtime and the node
    host: Generic Host, ASP.NET Core, configuration, OpenTelemetry, the HTTP plane), `DaServer.Persistence.PostgreSql`
    (a capability, ADR-045), `DaServer.Testing` (`TestActor<K>`, `TestCluster`, simulated users) and
    `DaServer.Aspire` (AppHost only). Merged away: Abstractions into Core, Hosting into `DaServer`, Simulation into
    Testing. WireTool is no package of its own: it ships inside Core beside the generator and stays out of process
    (`dotnet exec` after the build when `UpdateWireManifest=true`), since an in-process MSBuild task needs a second
    build for Visual Studio's .NET Framework MSBuild and loads `Microsoft.CodeAnalysis` into the MSBuild node, where its
    version conflicts; it reads prefab compositions by building one `Registration` from the built assembly, which a
    generator cannot see (prefabs are composed by EasyEcs's generated `SetX` members). `ManifestDiff`, the one comparison of two `wire.manifest`s
    (used by the arm and its dry run, the upgrade check, the route check and the AppHost's build check), is compiled
    into Core and linked into the generator ([drafts/architecture.md](drafts/architecture.md) section 5).
  - **Users see four words**: server type, node, actor (its root entity and all descendants) and entity/component.
    "Domain" is gone: it is the actor. A node runs one EasyEcs world per thread and sizes the count from the CPUs it is
    given (the container's request through `DOTNET_PROCESSOR_COUNT`, [drafts/deployment.md](drafts/deployment.md)
    section 2); `DaServer:Worlds` overrides it, and the AppHost has no shard parameter. Operator-facing names
    (configuration, metrics, admin output) say "world", the word EasyEcs users already know; "shard" stays the
    runtime's internal name for a thread with its world and appears in no public API.
- Alternatives: keeping Abstractions apart so `Game.Protocol` carries no transport (the analyzer already refuses
  server types there, a client ships Core anyway, and the server-only API is not in Core); keeping Hosting apart for
  hosts without ASP.NET Core (none: every node serves probes and admin over HTTP, ADR-046; `TestCluster` starts no
  HTTP); WireTool as an in-process MSBuild task (above); a separate Simulation package (a test tool, never in production); keeping "domain" as a technical term for the
  subtree (two words for one thing).
- Consequences: ADR-052's four layers become Core / Client / server once roadmap W2 lands, and ADR-052 is rewritten
  then. A game client references `DaServer.Client` (Core comes with it); a Protocol project references
  `DaServer.Core`; a node references `DaServer`; tests reference `DaServer.Testing`. Today's `DaServer:Shards`,
  `map_shards` and the `daserver.shard.*` metrics are renamed in W2. Targets: code both sides load (Core, Client,
  `DaServer.Replication.Client`, users' `Game.Protocol`/`Game.Shared`) targets `net10.0` and netstandard2.1
  (`DaServer.Replication.Client` `net10.0` only, as EasyEcs ships `net10.0` only); the server core (`DaServer`,
  Persistence, Testing, Aspire, the optional server packages) targets `net11.0` only. The first client engine is
  Godot 4 .NET, whose game projects target `net10.0` (EasyEcs's own guidance) and cannot load a `net11.0` assembly;
  netstandard2.1 is kept for a later Unity client. This replaces ADR-054's "no net10.0 build" and drops the shared
  packages' `net11.0` build in W2; .NET 11 features (runtime async) stay in the server core.

## ADR-087 Deployment lives in `Game.Server/apphost.cs`; ports are declared per node group; the connection actor is `Connection`
- Context: the target model had a third user project (`Game.AppHost`), opened the client port implicitly next to an
  explicit `WithClientEndpoints()` whose name already meant "point a client app at the gateways", and called the
  built-in actor type of a client connection `Client`, the same word as the remote client (`IFromClient`, the SDK).
- Decision:
  - **No AppHost project.** The AppHost is `Game.Server/apphost.cs`, an Aspire file-based AppHost (`#:sdk
    Aspire.AppHost.Sdk`, `#:package DaServer.Aspire`, `#:project Game.Server.csproj`), run with
    `dotnet run --file apphost.cs` or `aspire run` and published with `aspire publish` (build, run and Compose publish
    verified with Aspire 13.6.1 on the .NET 11 RC2 SDK). The `DaServer` package's build props remove `apphost.cs` from
    the server's compilation; `#:project` gives Aspire the project's metadata, not its assembly. User projects stay
    Protocol and Server. The node's entry point stays the user's `Program.cs` of about five lines.
  - **Local runs start the binary.** Aspire starts an `AddProject` resource as `dotnet run --project … --no-build`,
    which keeps a `dotnet` CLI process beside every replica. DaServer.Aspire uses Aspire's own `AddDotnetProject`
    (`Aspire.Hosting.Dotnet`), which runs the built project's `RunCommand` (the apphost executable, computed by
    `ComputeRunArguments`) with the same project defaults; no page states this, it rests on the implementation, and the
    package is prerelease, so it is pinned and W8 verifies it, with `AddProject` as the fallback. Aspire
    has no API to launch a published native binary, so AOT is checked locally by running the published AOT images
    with Compose (the production images), not by a custom executable resource. On macOS and Windows hosts AOT images
    build in a container (`sdk:11.0-resolute-aot` stage, `runtime-deps:11.0-resolute-chiseled` final stage), since
    Native AOT does not compile across operating systems; on Linux the SDK container publish builds them. JIT images
    run on `aspnet:11.0-resolute-chiseled`; health checks run the node's own executable in probe mode (neither image
    has `dotnet`, curl or a shell). A hot restart in a local run is the dashboard's `{cluster}-restart` resource
    command (`aspire do` runs in a fresh process that cannot reach the running AppHost).
  - **One binary on every host, the service managers declared in `Program.cs`.**
    `builder.Host.UseSystemd().UseWindowsService();` names both explicitly, each a no-op off its platform;
    `AddDaServer` calls neither. The one exit that wants a restart is the isolation exit (a node cut off from the
    store alone, ADR-082), code 75 (`DaServer.ExitCodes.RestartRequested`) on every platform; an instance that handed
    over (ADR-098) parks in place and never exits, so every supervisor restarts only a non-zero exit (a crash, the
    isolation exit): the systemd unit
    sets `Restart=on-failure` (`RestartSec=1`), launchd `KeepAlive = {SuccessfulExit = false}` (Compose and Kubernetes
    are the exceptions: Compose's `restart: unless-stopped`, since Docker does not restart `on-failure` containers after
    a daemon restart or a reboot, and the pod's `restartPolicy: Always`); on Windows the code is set as `ServiceBase.ExitCode` on the
    `WindowsServiceLifetime` before `StopApplication` (the service manager reads only that), and the documented install sets
    `sc failure <svc> reset= 0 actions= restart/1000` and `sc failureflag <svc> 1`, since the service manager runs
    recovery actions only for a non-zero exit. launchd needs no code: its plist (`ThrottleInterval` 1, so a restart
    is not held back 10 s) sets the working directory and the environment
    (console format, `DaServer__Instance`, `DaServer__HandoverPath`), and the Generic Host already stops on SIGTERM. DaServer documents the
    plist (`Label`, `ProgramArguments`, `KeepAlive`, `ExitTimeOut` set explicitly to at least the stop budget, since
    launchd.plist(5) leaves the default system-defined, `WorkingDirectory`, `EnvironmentVariables`,
    `StandardOutPath`/`StandardErrorPath`), the systemd unit and the Windows service:
    [drafts/deployment.md](drafts/deployment.md) section 0.
  - **Deployment is explicit.** Group names, replica counts, the store and every public port are written in
    `apphost.cs`; nothing is inferred from code. All replicas of a group listen on the same ports. The cluster and
    HTTP ports are assigned by DaServer.Aspire and reachable only inside the cluster; only `WithClientConnections(port)`
    (behind one load-balanced address; its kind is the application's) and Aspire's `WithExternalHttpEndpoints()` are
    exposed. `[Hosts<Connection>]` without `WithClientConnections`, or the reverse, is a build error. Per-replica public
    addresses (direct connect to one node, as Agones gives each game server a port) are a non-goal until a
    direct-connect feature needs them.
  - **`Connection`** is the built-in actor type of a client connection (`[Hosts<Connection>]`,
    `[Handles<Connection>]`); "client" means only the remote side.
- Alternatives: a separate AppHost project (still works, e.g. for Visual Studio, which does not open file-based
  AppHosts yet); the AppHost inside the node's `Program.cs` (the node image would carry Aspire.Hosting and the DCP
  orchestrator, which are not AOT-compatible and have no place on a production node); inferring client ports from
  `[Hosts<Connection>]` (less visible; the owner prefers explicit deployment); the node host calling the service
  managers itself (hidden; the owner prefers the calls in sight); a `UseLaunchd()` helper (launchd has no official
  .NET package, its detection would rest on an unverified heuristic, and the plist already sets what it would).
- Consequences: W5 adds `Connection`; W7 moves the samples
  to `apphost.cs` with `WithClientConnections(port)` and the build check.

## ADR-088 Hot restart shapes
Replaced by ADR-098.

## ADR-089 Gateways rebalance connections by load, not by age
- Context: a load balancer spreads connections only when they open, so after a scale-out long-lived connections stay
  on the old gateways. ADR-026 answered with a maximum connection age (gRPC's and Envoy's answer) and rejected
  shedding above the group average as cluster logic. An age retires every client on a schedule even when the group
  is balanced: each retire is a reconnect, a presence takeover on every actor the client is joined to and a resent
  snapshot, paid by a balanced cluster for nothing. The cluster already has what shedding needs: every member's load
  travels in its gossip pulse (ADR-031).
- Decision (mechanics in [drafts/architecture.md](drafts/architecture.md#retire-and-rebalancing) section 3):
  - Each client-facing node puts its connection count in its pulse, computes its group's average from its own view
    over the gateways that are Up and ready (not `NodeJoining` ones, whose empty counts would make the others shed
    before they can take connections), and only when it is above the average by more than a tolerance (10%, `DaServer:Clients:RebalanceTolerance`)
    retires its excess through `Retire` (make before break, ADR-026), at most 1% of its connections a second. The
    load balancer picks where they go.
  - A balanced group retires nobody. No coordinator is involved: each node decides from the view alone, and the
    rate and the tolerance keep nodes that act on slightly different views from overshooting.
  - Paused while a gateway of the group is `NodeLeaving` (draining) or `RestartingNodes` is not empty: counts then do not show the steady state.
  - `MaxConnectionAge` stays only as an optional setting, off by default, for deployments that want periodic
    reconnection (credential rotation at the edge, long-lived NAT mappings).
- Alternatives: the maximum age as the rebalancer (ADR-026: moves clients of balanced groups); a cluster-side
  coordinator assigning target counts per gateway (a second placement service for something each node can decide
  from the view; one more failure to handle); the cluster naming a gateway to connect to (ADR-026: an address per
  node the platform does not give).
- Consequences: after a scale-out only the excess moves, at a bounded rate; a balanced or shrinking group pays
  nothing. Tests: a balanced group retires nobody, a scale-out sheds only the excess and a joining gateway is left out
  of the average (roadmap W5, again on real
  gateways in W11); rebalancing pauses during a drain and a hot restart (W6, which builds the hot restart). ADR-026's age becomes optional when W10 promotes the drafts.

## ADR-090 Optional capabilities: reminders, topics, non-.NET clients, replication, test and admin tools
- Context: four needs recur across domains and are hard to get right in user code. Work due at a wall-clock time even
  when the actor is unloaded or its node crashed (daily resets, auction ends, renewals): `Schedule(after)` lives in
  memory and is lost on a crash. Fan-out between actors (prices, world events): today's `Publish`
  reaches only services and is deleted in W. Clients that are not .NET (browsers, Unreal): G7 planned the .NET client
  compiled to `browser-wasm` with no TypeScript code generation. Client copies of server components (every game
  writes its own diff and snapshot code; the mmo sample's `Nearby` is one). Users also cannot test their own logic
  under partitions and crashes, and operators cannot see one actor's state.
- Decision: each is a package in its own namespace, off unless referenced, written in the model's own terms
  (components, tags, singletons, systems; no allocation on hot paths). The mechanics are
  [drafts/programming-model.md](drafts/programming-model.md) §15, nowhere else.
  - **Which fan-out**: an actor to many actors is Topics; an actor to many clients is `ClientGroup` (a chat channel
    included); a membership with rules of its own (a guild, a party) is a fixed-key actor that holds the members and
    forwards, through `ClientGroup` to their clients (§15's opening rule).
  - **`DaServer.Reminders`**: a `Reminder<T>(ActorId Actor, long Key, TimeSpan Period)` on a persistent child entity
    whose next firing is its `WakeAt`; one per (T, key) and actor, its unique index keyed by `(Actor, Key)` only (`ReminderKey`, not the whole struct, so a new period finds the existing one), so two
    actors in one world keep their own and an actor can hold several of a kind (`root.Remind<T>(DateTime at, TimeSpan every = default, long key = 0, bool move = false)` finds
    or spawns it and leaves an existing one with the same period untouched, so calling it on every activation never
    skips an overdue firing; a new period or `move: true` reschedules; `root.CancelReminder<T>(key: 0)` destroys it). The runtime arms it like every wake (its `ArmSystem` turns a `WakeAt` into a `Due` on load and
    activation; while unloaded, `NextDue` goes into the record's `next_due` column and the slot owner's `DueScan`
    activates the actor); the package has no arming system of its own. `Reminded<T>` is at least once, deduplicated by
    (reminder, `UtcAt`): the advance lands in the same record as the handler's effects. After an outage a reminder
    fires once and moves to its first future occurrence, not once per missed period.
  - **The wake is the runtime's, not the package's**: `WakeAt`, `NextDue`, `next_due`, `DueKeys` and `DueScan` belong
    to the runtime because `AskUntilAnswered` needs them too (ADR-100) and the runtime depends on no capability
    (ADR-045). Only the runtime computes `NextDue`; the package writes `WakeAt` on its reminders and registers a wake
    handler that delivers `Reminded<T>` in the firing item.
  - **`DaServer.Topics`**: a built-in `Topic(long Id)` actor type with a persisted table of subscribers and their
    activation epochs; `Subscribe`, `Unsubscribe` and `Publish` on `Entity` send the Topic one-way package messages
    (exempt from `[From<…>]`, since any actor type sends them); `Publish` is serialized once and sent as one frame per
    subscriber node. Subscribers renew their own `Subscription` buffer every `DaServer:Topics:RenewInterval`, so a
    crash or a lost message costs at most two intervals of missed publishes (the renewal scans in slices, and a row
    moved to another table during a round is missed once); the receiving node checks that buffer at
    dispatch and answers a stale delivery with `NotSubscribed`, so a lost `Unsubscribe` is pruned at the next publish.
    Delivery is at most once and never activates an actor; `[Stateless]` actor types, `Connection` and actor types whose root
    is not saved cannot subscribe (DAS023): Topics needs the subscriber's epoch and a saved `Subscription` on its root. It replaces today's `Publish`; durable streams (replay, offsets, consumer groups) stay with Kafka or NATS
    JetStream.
  - **Non-.NET clients**: the client wire protocol is published as a specification (frames, handshake, `Ready`,
    re-join and slots, `Retire` and `HandedOff`, `Busy` with its retry-after, keep-alive), with the C# SDK as the
    reference and one conformance suite run against every implementation. A TypeScript client over WebSocket uses
    the classes and codecs Nino.TypeScript (a prerelease package) generates for the protocol's `[NinoType]`s after each
    build, structs and version-tolerant types included; DaServer writes no TypeScript generator. This reverses G7's "no TS code generation": a
    `browser-wasm` client ships a .NET runtime to every page (megabytes before the first frame) and gives JavaScript a
    `[JSExport]` shim instead of types, while generated codecs give browsers the types Unity gets. Unreal/C++ follows
    the same specification later.
  - **`DaServer.Replication`**: `[Replicated]` on a change-tracked component; per actor type, a `ChangedAny` system
    writes the changed fields (generated per-type bitmasks against the last sent value) per `ClientGroup` once per tick
    into pooled frames. Each `Replica<T>` keeps the last sent value and its stamp (the world's `ChangeTick`), each
    group a bounded ring of exits, each actor an incarnation renewed whenever it is rebuilt (activation, migration
    arrival, hot-restart load, a reload after a throw), which clears the rings and the stamps. A rejoining SDK gets a
    delta when its incarnation and stamp are still covered, otherwise a full resend starting with `Reset`. The SDKs
    apply the patches (C# into an EasyEcs world through `DaServer.Replication.Client`, TypeScript into an entity
    table). At most once, like pushes.
  - **Tools**: `TestCluster` fault injection (`Partition`, `Kill`, `DelayLinks`) for users' own tests; a read-only
    inspector on the HTTP plane (an actor's subtree, the busiest actors, each world's tables and systems, counts by
    component; flecs's REST API is the model); its endpoints are the single source, and Aspire commands (local runs),
    the Dashboard (production, ADR-093) and curl are thin clients of them; writes go only through `ICluster` messages.
    The endpoints are [drafts/architecture.md](drafts/architecture.md) section 8, nowhere else.
- Alternatives: reminders as a periodic scan of every record or a timer service with its own store (a full scan per
  poll, or a second store to keep consistent); reminders in the runtime (only some applications need them); topics as
  `ClientGroup`s (gateway fan-out to sockets, not mailboxes); durable topics (a log store is Kafka's job); keeping the
  `browser-wasm` client (above); replication left as a recipe (every game repeats bitmasks and snapshots); fault
  injection only in the framework's own tests.
- Consequences: roadmap W6a (Reminders; the wake itself is built in W6 for `AskUntilAnswered`), W6b (Topics), W7a
  (Replication, proven against the mmo's hand-written AOI in a benchmark of its own, ADR-091), W7b (protocol specification and TypeScript client, proven by the
  flash sale from a browser page; G7 points to it), W7c (tools). Each is proven by tests, BenchmarkDotNet numbers and
  allocation tests on its hot path before the next.

## ADR-091 Business features are samples and `dotnet new` templates, not framework
- Context: teams start from working examples: matchmaking, parties, leaderboards, a purchase with payment. These are
  business features (ADR-045) whose rules differ per product (queue policies, rating systems, payment providers); a
  framework version would be too narrow or grow configuration for every policy. Today's samples show patterns but are
  not starting points a team can copy without dragging the repository's build along.
- Decision:
  - **Business features live in samples written as templates**: detailed, high-performance, tested and benchmarked,
    each a project a team can start from.
  - **`samples/starter`**, the chat room behind `dotnet new daserver`: a `Room` actor created by its join message
    (`[ActivatedBy<JoinRoom>]`), several rooms per client (`PresencesPerClient = 16`), `root.JoinClient(out _)` and
    `root.TryGetClient`, no record; short enough to read in one screen
    ([drafts/programming-model.md](drafts/programming-model.md) §0).
  - **`samples/arena`**: a matchmaking queue actor per region and mode (a fixed key, the model's "service"), ranked and
    casual queues, `Match` actors that its players join as presences, `Party` actors, and a sharded `Leaderboard`
    (score shards by key, merged on read). The queue decides what a disconnected ticket means
    (a `[ForEach, With<Detached>]` system marks it not matchable while detached, so a reconnect within the grace
    period takes it over (`ReloginTakesTicketOver`); its `Expire`, after its own timeout, removes it, scheduled once: a re-join finds
    `IsPending<Expire>` and does not schedule again), tickets are keyed by the account (`Ticket(Queue, long Account)`,
    so a re-login takes the same ticket over rather than queueing twice), and a match is set up before its players
    are told: the queue `Ask`s the `Match` its `Setup` and pushes `Matched` from the `Replied` handler, so a client's
    `JoinMatch` never reaches a `Match` that does not know it yet. Its tests cover a player detaching while queued and
    while being matched.
  - **A shared payment sample, `samples/payments`**, which the mmo's recharge and the flash sale both use, written as
    the standard provider flow with every external call on the HTTP plane, off the shards (DAS015): the business owner
    (the mmo's `Player`, the flash sale's `Order`) opens a `Payment(id)` with an `AskUntilAnswered` of `[Once]
    Open(id, for, amount, deadline)`, where `PayFor` says whom it is for and what they get, and `Settled` echoes it,
    so the owner keeps no record of payments it waits for; `Payment` sets a `DaServer.Reminders` reminder at the
    deadline (it does not tick). The client asks a node's HTTP endpoint for a checkout, which reads the payment through
    `ICluster.Ask` (retrying `NoActor` briefly while `Open` lands, else 409) and creates the checkout at the provider
    with the payment id as the provider's idempotency key, so two presses are one checkout; the client pays there; the
    provider calls our webhook with the payment id and its token; the endpoint checks the callback's signature,
    verifies the payment server to server with the provider, then `ICluster.Ask(Payment(id), Paid(amount,
    providerRef))`; `Payment` writes the outcome with `Commit` and the webhook answers 2xx for every definite outcome
    (accepted, already paid, refunding; a non-2xx only for a link failure, which the provider retries for days), then
    `AskUntilAnswered`s the owner a `[Once] Settled(id, for, outcome)` (the mmo credits diamonds, the flash sale
    confirms the order). The charge has two paths only, `Open → Paid → Settled` and `Open → Expired` (a payment not
    made by its deadline, settled as expired); every payment that is not the accepted one (late, the wrong amount, a
    second payment) goes into the charge's refund buffer once per provider reference, a repeated reference answering
    the earlier result, and changes no state, so the deadline still notifies the owner; a daily reconciliation
    against the provider's report lists what is missing, stuck or listed for refund, and deletes (`root.Delete()`)
    payments settled, refunded and past `Payments.Retention` (30 days, a constant of the sample), which also forgets their `[Once]` rows at
    the owners (ADR-099): it sends each reconciled payment a `Purge`, which deletes only a charge that is `Settled` or
    `Expired`, whose refunds are all done (each refund element carries the time the provider's verified refund
    callback, `Refunded(providerRef)` through `ICluster`, recorded) and whose `SettledAt` plus the retention has
    passed, and answers `false` otherwise. A flash-sale `Order` reminds itself at the sale's end plus the payment
    window and deletes itself in a final state; `Gate`, which is not saved, compares `time.Utc` with the sale table's `EndsAt`
    on every request and answers `SaleEnded` past it (no reminder, nothing written), so a deleted order is never created
    again, across `Gate` deactivations and restarts too. `Open` is `partial` in the sample, and each game adds its `[From<Player>]` or
    `[From<Order>]` beside its `PayFor`, `Notify`, `[Once] Settled` and `[Hosts<Payment>]` on one of its server types.
    The sample's provider is a simulator (checkout, a signed callback, a verify API, refunds and a daily report) that
    the bots pay through in the E2Es. The game supplies its `PayFor` type and its `Notify`; nothing in it is a
    framework capability (ADR-045).
  - **A saga in `samples/flashsale`**: the order as a process-manager actor driving stock and payment, each step a
    state of the order (`Reserving`, `AwaitingPayment`, `Releasing`, ending `Confirmed`, `SoldOut` or `Released`)
    written with an `AskUntilAnswered` of a `[Once]`
    request (ADR-100): the stock shard runs each `Reserve` once (ADR-099) and keeps its reservations as business state;
    the payment is a `Payment` of the shared sample opened with the sale's payment deadline; a sold-out answer
    ends the order (`SoldOut`: nothing was reserved, nothing to release); and compensation has one trigger, the payment
    of a reserved order settled as expired, which writes `Releasing` and asks `[Once] Release` until answered
    (a late `Reserve` attempt is a duplicate or stale to `[Once]`, so it never lands on a released order); released
    units are sold again: the shard adds them back, and `Gate` asks a shard it learned is sold out again every
    `RecheckSoldOut` (5 s) through that shard's `Describe`; link outcomes never reach the order, so a timeout is never
    compensated, and a reconciliation lists orders stuck in one step; with
    a pattern page in the sample on why sagas replace ACID transactions across actors (each step is idempotent by its
    key, no lock or two-phase commit spans actors). The buyer's identity comes from the token, not the message: `PlaceOrder`
    carries only the sale. Only one actor type handles it (a client message has one): today's stateless `Gate`
    service becomes `Gate(long Sale, int Shard)`, an actor type that is not persisted (so it claims no store row),
    with `[ActivatedBy<PlaceOrder>]`, `CanCreate` (the sale exists) and `Route(in Account, in PlaceOrder)` to the shard
    `who.Id % SaleShards.Count` (`SaleShards.Count` a constant: `Route` reads no tables, so changing it means a gateway roll): it handles the client's `PlaceOrder`, answers the late crowd `SoldOut` from what it has learned
    (a sold-out answer from any `Order` it asked, or a stock shard's `Describe`), runs the optional admission queue, and for
    the rest plainly asks `Order(m.Sale, who.Id)` with the server message `[From<Gate>] Place : IRequestTo<Order,
    OrderStatus>` (the user from the claims, so no buyer orders for another; `Order` needs no `Route` or
    `[ActivatedBy]`, since server messages activate), replying to the buyer from `Replied`. A
    `[Stateless]` type cannot do this (it cannot `Ask` or keep a `ReplyTo`), and letting `PlaceOrder` reach `Order`
    directly would leave a row per losing buyer. `Order` dedups by its key (a second `Place` returns the order's
    status), so the plain ask may repeat.
  - **Input ticks and corrections in `samples/mmo`**, the pattern every server-authoritative game with client
    prediction needs (Unity Netcode's [command stream](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/command-stream.html)
    and [prediction](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/prediction-details.html)): `MoveTo`
    carries a `uint InputTick`, `Moves` writes it into a `LastInput` component on the avatar, and the `Nearby` and
    snapshot pushes echo it, so the client replays only the inputs the server has not applied yet. A `Resync` system
    with an `UpdateInterval` pushes the positions of each cell at a low rate as a correction keyframe, so clients' extrapolation
    drift heals without a full snapshot; NPC AI runs as `[ForEach(Slices = N)]`, a slice of
    the NPCs per tick. The mmo keeps its hand-written area of interest (`Nearby`, crossing snapshots, `Resync`):
    `DaServer.Replication` is proven by a benchmark project that runs the same scenario both ways (bytes per client,
    broadcast p99, allocation per tick), not by rewriting the sample, so the sample stays a pattern a game can copy
    without the package. The player's inventory is slots on the `Player` root: the client's `UseItem` binds to the
    root (a `[JoinOnConnect]` type, ADR-095); spending a precious item and buying with premium currency are `[Once]`
    client requests, which the SDK resends under the same sequence number and the player runs once (ADR-099); a
    recharge uses the shared payment sample: the client's `[Once] Recharge(pack)` makes `Player` open a `Payment`
    whose `PayFor` carries the diamonds (so `Player` keeps no record of it), the client checks out and pays at the provider, the verified webhook reaches
    `Payment` through `ICluster.Ask`, and `Payment`'s `[Once] Settled` reaches `Player`, which credits the diamonds once
    by `Payment`'s sequence number and keeps its reply in `Unacked` until the cumulative acknowledgement (ADR-099), so a
    payment is credited once however long the asking takes; the provider's retries (three days at Stripe) cover a crash
    before `Payment`'s write; the client claims nothing. The flow is written out as code in the draft's §8. A guild resets its daily
    quests with a `DaServer.Reminders` reminder.
  - **`samples/relay`**, a relay room in Photon Quantum's style ([Quantum intro](https://doc.photonengine.com/quantum/v3/quantum-intro)):
    a `[TickRate(30)]` `Room` actor gathers its players' inputs for each tick and broadcasts one frame per tick to its
    `ClientGroup`; it holds inputs only, the deterministic simulation runs on the clients. It proves the model fits a
    game whose server knows no game rules, and suits games that tolerate TCP (ADR-092).
  - Each sample has BenchmarkDotNet numbers and an E2E with bots (`DaServer.Testing` swarms) in CI.
  - **`DaServer.Templates`**, a `dotnet new` package: `daserver` (the starter above: Protocol, Server with one actor
    type and `apphost.cs`, a test project), `daserver-mmo`, `daserver-flashsale`, `daserver-arena`, `daserver-relay`. The templates are
    generated from the samples, so a sample and its template never drift; CI instantiates each, builds it with 0
    warnings and runs its tests.
- Alternatives: matchmaking, parties or leaderboards as framework capabilities (domain logic in the framework,
  ADR-045); samples without templates (copying out of the repository drags its build files along); templates written
  apart from the samples (two copies drift).
- Consequences: roadmap W7d (arena, relay, the mmo's input ticks and `Resync`, saga, templates) once the samples run
  on the new model (W7); the README's getting
  started becomes `dotnet new daserver`; the roadmap's out-of-scope note on transactions points at the saga sample.

## ADR-092 Client transports are TCP and WebSocket; a datagram transport is an extension, not built now
- Context: clients reach gateways over TCP or WebSocket (`/clients/ws`, wss behind the platform's TLS), both
  reliable, ordered streams behind one feature point, `IFrameChannel` (`Connection`, `WebSocketChannel`). A lost
  segment holds back every later byte of the stream until it is resent, at least a round trip later (head-of-line
  blocking). Fast-paced shooters therefore send state over UDP and drop what is stale:
  Overwatch's netcode runs on UDP (from the [GDC 2017 talk](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)),
  Unity Netcode for Entities on Unity Transport, which runs "over either UDP sockets or WebSockets" ([Unity Transport](https://docs.unity3d.com/Packages/com.unity.transport@2.4/manual/index.html)),
  and QUIC exists to give streams without that blocking ([RFC 9000](https://www.rfc-editor.org/rfc/rfc9000)).
- Decision:
  - **Client transports are TCP and WebSocket.** They fit MMOs, flash sales, turn-based games, chat and IoT, where a
    late message is still wanted and order matters, and WebSocket is what browsers have.
  - **A datagram transport (UDP or QUIC) is not built now.** Twitch shooters suffer TCP's head-of-line blocking and
    need one; they are not DaServer's target until a user needs it.
  - **The client channel stays a feature point**: a datagram link is another `IFrameChannel` plus a way to mark
    messages that may be dropped, not a rewrite of the gateway, presences or the SDK.
- Alternatives: UDP with reliability layered on top now (a second transport to secure and test, browsers cannot use
  it, no user needs it yet); WebTransport over QUIC for browsers (not in every browser, and .NET's QUIC needs msquic
  on each platform).
- Consequences: the docs state the scope (the drafts' §2 ports and architecture section 3); samples stay within it
  (the relay room of ADR-091 suits games that tolerate TCP); a datagram transport enters later as a capability of its
  own (ADR-045).

## ADR-093 One production console, DaServer.Dashboard; logs from Grafana Loki
- Context: an operator of a deployed cluster otherwise switches between three sites: the Aspire dashboard (diagnostic
  only in deployments, no plugins, commands only in run mode), Grafana over Prometheus, and a log viewer. Consoles of
  comparable systems (Temporal, Redpanda Console, CockroachDB, RabbitMQ management, the Orleans dashboard) keep a
  short live window themselves and leave history to the monitoring stack. Nodes already export metrics, logs and
  traces over OTLP and serve `/metrics`, and Grafana Loki 3 ingests OTLP logs natively (`/otlp/v1/logs`), keeping
  resource attributes such as `service.name` as labels and the rest as structured metadata.
- Decision:
  - **`DaServer.Dashboard` is the one console for production**: a stateless ASP.NET Core app, its own Deployment and
    Service (with the platform's Ingress or HTTPRoute) in Kubernetes, added by `AddDaServerDashboard` in the
    AppHost. It answers the operator's jobs (doc/dashboard/design.md): triage, evidence, cluster, server types,
    store, client connections, changes, an actor, audit; and runs the actions: a node's restart, a type's hot
    restart and a rollback go through the deploy pipeline's webhook (ADR-098); a config table push and a silence go
    through the nodes' admin plane and Alertmanager. Each is confirmed by typing its target and written to the audit
    log. Restarting one node that hosts actors is a same-image handover of that node (ADR-098),
    started through the deploy pipeline's webhook like a type's; a gateway or stateless node has nothing to hand over,
    so the same loop retires it (a gateway hands its clients over first) and the platform starts a fresh one. A node
    restart carries the node's incarnation as the operator saw it, and the loop refuses it for an incarnation the node
    no longer is. Hot restarting a type is the same-image roll, and the Dashboard never loops over
    nodes itself (its own restart would leave a half-rolled group, and only the rollout loop pauses scale-in): it calls
    the deploy pipeline's webhook, which runs the one rollout loop (one node at a time, the coordinator last), or shows
    `aspire do {cluster}-restart` without one; a new build, a rollback included, reaches the nodes through the deploy
    (`aspire deploy`, then `{cluster}-restart`), since only the platform changes an image: the Dashboard's roll back
    calls the deploy pipeline's webhook (`DaServer:Dashboard:DeployWebhook`: GitHub Actions, Argo CD, any CI) with
    the server type, the build to return to, who asked and why, and audits the call. The webhook call also carries the
    build the type is expected to leave (the pipeline refuses when the type has moved on) and the action id, which the
    pipeline puts on its run; the pipeline runs one concurrency group per server type, so CI rollouts and the
    Dashboard's actions are serialized by the pipeline, not by the Dashboard's lock. A group holds one running and one
    pending run, and a newer pending run cancels the older pending one whatever `cancel-in-progress` says (GitHub
    Actions), so a cancelled run is the action's Failed, as is a run the pipeline refuses. The pipeline reports the
    end of a run on a completion callback signed with HMAC over its body (which carries the action id), a hint the
    Dashboard confirms with a run lookup before it writes a state, so a replayed callback is harmless; the callback's
    `current` and `accepted` keys (for rotation), the webhook token and the run-lookup token are the `{cluster}-deploy`
    Secret, mounted into the Dashboard only: nodes hold no pipeline credential, since a pipeline token cannot be scoped
    to one kind of run. A rollback during a
    half-finished rollout names the build expected to leave, the minority one included. Without a webhook the Dashboard
    shows the command to run. Taking a node out is the platform's scale-down, not a Dashboard action.
  - **It holds an admin key of its own, never the cluster's signing secret**, so a compromised Dashboard cannot pose
    as a node or sign a resume ticket. The key is the `{cluster}-admin` Secret, mounted key-per-file like the signing
    secret with `current` and `accepted` files for rotation and re-read when they change. The Dashboard signs admin
    requests with a scope: `view` (readings, membership, an actor's state) or `act` (anything that changes state);
    nodes verify the key and the scope and refuse an `act` request signed for `view`. The rollout and upgrade-check
    Jobs mount the same Secret and sign with `act`. The Dashboard calls no Kubernetes API and has no autoscaler and no
    PodDisruptionBudget (one replica, two for availability when the cluster has a store; without a store there is no
    shared lock, so exactly one; its cost follows the nodes, not the viewers).
  - **Actions run on the server, once, one per type, audited twice.**
    - Every action runs on the Dashboard's server and outlives the browser.
    - **Preconditions.** An action carries what the operator saw: the node's incarnation for a node restart, the
      type's build for a rollback or a type's hot restart. The loop or the pipeline refuses it when that has changed.
    - **Idempotency.** An action has a key (the action, the target and what was seen, never the operator). For ten
      minutes from the request, and only while the action has not finished, a second press, a retry or another
      operator's identical request is the same action. A finished action (Done or Failed) no longer dedupes.
    - **One at a time.** Each server type has one action at a time: a rollback, a hot restart, a node restart or a
      rollout. Another action waits, and its button names the running one. The confirmation says when another node
      of the type is not running. Every action on a type takes the type's lock, a node's restart included (a Resize does not: its row has no type, a unique index on (node, incarnation,
      attempt) admits its automatic first attempt and one retry once across replicas, an operator's Resize has no
      attempt number and is deduplicated by its idempotency key, it runs in a per-node concurrency group and
      sets requests by role, so it is idempotent, never waits behind a rollback, and a Stalled one holds only its node); an action
      on the whole cluster (a config push) takes the sentinel type `*cluster`, since `type` is never NULL (a unique
      index never conflicts on NULL). The two exclude each other: a config push is refused while any type holds a lock (naming
      the type, its action and, when Stalled, Mark failed), and a type's action is refused while `*cluster` is held,
      since a standby that read the old table could take over a node after a push (the handover also carries the table
      version). Different keys never meet in the unique index and READ COMMITTED hides uncommitted rows, so every
      lock-taking insert first takes one serialising `pg_advisory_xact_lock` in its transaction, then checks and inserts. The lock is `daserver_actions`: a unique partial
      index on `(type)` over every state that has not finished (Requested, Accepted, Running, Unknown, Stalled), and a
      lease (`claimed_by`, `lease_until`) of three cadences, renewed on a timer of its own (never in the poll loop, so
      a slow refresh cannot let it lapse), every renewal and state write conditioned on `claimed_by`; a renewal that updates no row
      (another replica holds it) stops the owner following the run, and one that fails with a store error writes
      nothing and retries until its own lease has run out.
    - **Reconcile.** A replica claims a lease only once it expired a whole lease ago, by compare-and-set on the
      store's clock (`UPDATE daserver_actions SET claimed_by = $me, lease_until = now() + $lease WHERE action_id = $1
      AND lease_until < now() - $lease`), so a briefly late renewal is never taken over and one replica wins; after a
      store outage the replica that was following the run usually renews first, a race either outcome of which is
      correct. It then looks the run up by the
      action id. A run not found yet (dispatch creates runs asynchronously, late during a pipeline incident) is looked
      for during an hour and re-attached if it appears; after that the action is Failed, and the pipeline itself refuses a run that
      appears later: every dispatch carries `--action`, and the rollout step's first step asks the
      Dashboard (`GET /admin/actions/{id}`, signed with the admin key, at the in-cluster address DA gives the Job,
      which the Dashboard's network policy admits from the rollout Job's pods) whether the action is still
      unfinished, retrying for five minutes and then refusing (fail closed: a Dashboard that cannot read the store
      answers 503, counted as no answer, never "unknown id"); it runs on any unfinished state, Stalled included (a Job
      that sat Pending or built slowly reads Stalled before its first step), and refuses only a Done, Failed or unknown
      action; so a run created
      after an operator marked its action Failed ten minutes in never runs, beside the action requested again or after
      it. The Dashboard accepts the admin key on that one read-only route only; its other routes need an operator's
      sign-in and the antiforgery token. A NetworkPolicy cannot restrict a path, so the route rule is the Dashboard's
      and the policy only narrows who reaches its port. A lookup that
      fails keeps the state and the lock.
    - **State.** What runs comes from the server's action records and from builds and incarnations in membership,
      never from the page. The records are in the store; without one they are in memory and the Dashboard runs as one
      replica.
    - **Outcomes.** Each action is Requested, Accepted, Running, then Done, Failed or Stalled. Every state write is a
      compare-and-set from a non-final state, so a late Done never turns Failed into Done or brings a row back to a
      held state (a late outcome is only audited). After Accepted the Dashboard follows the run, by polling the run
      each cadence and by the pipeline's completion callback, confirmed by a lookup. It is Stalled when it shows no
      progress for ten minutes while the run still runs (no node moved build or incarnation; for a Resize, the node's
      requests-low gauge has not cleared); Stalled holds the lock until an operator marks it Failed, which is offered
      for Requested, Unknown and Accepted too once they have lasted ten minutes (a replica that crashed between writing
      Requested and the call would otherwise hold the lock for an hour). Done and Failed are the only terminal states, and the lock is
      held until one of them, so a 400-node roll
      keeps it for its whole length, and a same-image hot restart, which changes no build, keeps it too. Unknown means
      a timeout: the action may have run, and the run lookup by action id settles it. A webhook without a run id
      (`workflow_dispatch`) is not retried after a timeout; the pipeline is first asked for a run carrying the action
      id.
    - **Mark failed** is an action of its own: act scope, a reason, audited, refused while the store is down (the lock
      stays until it answers). In one transaction it sets the action Failed, conditioned on the state the operator saw
      (`state = $seen`), so an action that moved on meanwhile is not failed while healthy, and writes its
      own row already Done (for a Resize a `kind = resize` row for that node with no attempt number), so it never
      needs the lock it releases. It does not cancel the pipeline's run.
    - **Reasons.** An action on a whole type or on the cluster asks for a reason.
    - **Audit.** The audit holds the intent before the call and the outcome after it, with the same action id, which
      the page shows. The sink is durable: the store's `daserver_audit` table, owned by the owner role and
      insert-only for the Dashboard's role, or an append-only file without a store. Retention is the operator's
      setting (`DaServer:Dashboard:AuditRetention`). The same lines also go to the logs.
    - **No store, no action.** The intent, the key and the lock live in the store. When it cannot be reached, the
      Dashboard sends nothing: the confirmation says so and gives the deploy pipeline's command for the same action,
      whose run log is the audit for it (a config push and Mark failed have no pipeline command: they wait for the
      store). An outcome that cannot be written is kept in a local spill file and written, in order, before any new
      action once the store answers; the spill file is lost with a restarted pod, so the store's return also triggers
      a reconcile of every unfinished action from its run.
    - **Silences.** "Silence" creates an expiring Alertmanager silence. A silence needs the act scope, is audited,
      carries the incident's matchers (`alertname`, `cluster`, `type`), and is written with an Alertmanager credential
      of the Dashboard's own.
    - **Config tables** go out in two phases: every node validates and stages the table, then all switch together. If
      any node has not staged it within a minute, the push aborts and nothing switches; a node that joins meanwhile
      reads the table from the store. Staging acknowledgements are keyed by node and incarnation, so a node handed over
      during staging (a new incarnation that never staged) stages from the store or the push aborts, never switching
      nodes apart.
    - **No second approver.**
  - **Live state is gathered once per refresh, never per viewer or per request**, so its cost grows with nodes, not
    with people watching. Nodes keep no history: each serves its current readings and its own ranking of its slowest
    actors. When Prometheus is configured the Dashboard reads per-type aggregates and per-node readings with a fixed
    set of queries, and charts come from range queries; otherwise a collector in the Dashboard polls every node's
    stats summary (fixed size) every 10 s into one in-memory snapshot that every viewer
    reads, and keeps a bounded ring of those polls for the charts (they start when the Dashboard started, and the
    evidence chart says so).
    - **Freshness.** Each node's reading carries its age on the snapshot's clock. The poll asks each node with a 2 s
      timeout. A node that does not answer, a node isolated from the store (it fails readiness and answers probes as
      isolated) and a reading older than two cadences each get a state of their own, and none of them enters a
      distribution. The status line gives the snapshot's time and how many nodes read fresh.
    - **The poll always runs**, with or without Prometheus (only it says which nodes answer). A node that failed is
      asked only every fourth cadence until it answers. Concurrency is sized as nodes ÷ (cadence ÷ timeout), at least
      32. Each replica polls on its own.
    - **Computed on the server.** Distributions, outliers and the cluster's slowest actors are computed on the
      server; a screen receives only the bins and the few rows it shows. Outliers, in this order: no fresh reading; a
      state other than running; no requests while the type has them; more than four median absolute deviations from
      the median of the nodes on the same build, either way, with a floor of 2% of the median and an absolute floor
      (0.5 ms for latencies). A build with fewer than five fresh nodes is compared with the majority build's median
      and deviation, so a one-node canary flags. The slowest actors are
      the nodes' rankings merged; an actor needs 100 calls in the window to rank, and an actor seen on two nodes
      during a handoff counts once. A type's p99 comes from its nodes' histogram buckets merged as deltas, never as a
      percentile of percentiles; with Prometheus these are recording rules
      (`cluster_type:daserver_ask_duration_seconds_bucket:rate2m`, `cluster_type:daserver_ask_duration_seconds:p99`,
      `cluster_type_node:daserver_ask_duration_seconds:p99`). The histogram's bucket boundaries are explicit (0.0005 to
      10 s, log-spaced, 0.025 among them), set in `AddDaServerTelemetry`: .NET's default boundaries are meant for
      milliseconds, and in seconds they would put every ask under 5 s in one bucket and a healthy p99 near 4.95 s.
    - **One metrics path.** Nodes push OTLP every 15 s to one Collector, which sends to Prometheus's `/api/v1/otlp`
      (`--web.enable-otlp-receiver`, `otlp.promote_resource_attributes: [daserver.cluster]`); Prometheus scrapes no
      node. Pushed series get no staleness markers, so gauges are read with `last_over_time(...[2m])` and rates use
      windows of at least four pushes. Nodes set no `service.namespace` (it would make `job` read "ns/name").
    - **Builds.** A type on two builds shows both counts; outliers and the incident's cause are computed per build.
    - **Swapped requests.** A node whose requests stayed swapped after a crash takeover (`daserver.node.requests_low`,
      alert `DaServerNodeRequestsLow`) shows "Requests swapped since takeover". The node's `/health` says so (its request:
      the Dashboard polls every node); the Dashboard checks it (the gauge set, the same incarnation) and dispatches the
      `--resize-only` run (with `--type` and `--incarnation`) with its own token (ADR-094) once per (node, incarnation) plus one retry after a five-minute backoff, outside the type's lock (the per-node concurrency group and the by-role sizing make it idempotent, so it never waits behind a long rollback), so the state
      normally clears on its own; after the retry the row offers Resize, the same run, and the alert stays. A Resize's
      progress is the gauge clearing, not a build or incarnation change: it reads Stalled when the gauge has not cleared
      ten minutes after the run started.
    - **Parked instances** of hot-restart pods export no metrics and are never shown as nodes; a node's telemetry
      identity is its name only while it holds the lock.
    - **Membership** is read from up to three ready nodes, chosen again each refresh from the newest view, keeping
      the highest version; while no node is ready the last view stays, with its age. Each member's pulse carries
      the hash of its view, so a disagreement shows; it is an incident only when it lasts (`for: 2m`). An actor
      lookup that meets `NotHosted` asks again.
    - An actor's detail is asked on demand of the one node that owns it,
    found through placement; Find resolves actor ids the same way and searches nodes in the snapshot. The snapshot is
    a cache rebuilt at start, so replicas stay interchangeable.
  - **Something is wrong when an alert fires.** `observability/prometheus-rules.yaml` is the one definition.
    - A type is unhealthy when an alert fires for it. A reading's limit is its rule's `daserver_limit` annotation,
      read from Prometheus's rules API (`/api/v1/rules`); a reading with no rule has no limit line, and the Dashboard
      keeps no threshold of its own.
    - **Incidents.** An incident is the alerts of one rule on one server type of one cluster, grouped by
      `alertname`, `cluster` and `type`, from Alertmanager's API with its grouping, silences and inhibitions. Every
      rule keeps those labels plus `node`, where `type` is `service.name` and `node` is `service.instance.id`.
      `DaServerNodeSilent` fires for a node seen in the last 15 minutes that stops pushing, which would otherwise read as
      healthy because its series vanish, unless its last push carried the final stop phase (`close`); it needs the
      OTLP path, since a scrape carries no final push. `DaServerMembersDisagree` fires for a node whose membership view
      version is behind the cluster's newest and has not moved, not on member counts, which a rollout's churn keeps
      different. Incidents rank by severity, then by node count; each gets a screen, and the
      first one counts the rest.
    - **Cannot know.** The alerts are unreadable when Prometheus or Alertmanager does not answer, when the newest
      sample is older than two evaluation intervals, or when rule evaluation fails. The Incident section then says
      "Alerts cannot be read", never that nothing is wrong, and shows the threshold-free checks from the poll: a
      stalled shard, a node not ready, views that disagree. Two failed reads enter the state and two good reads leave
      it; the newest sample is measured as `max(timestamp(daserver_cluster_members))`. Without Prometheus, triage
      covers only those checks.
    - **The store down is an incident.** When most nodes are isolated, the first incident screen is "The store
      cannot be reached", which needs no rule; the status line counts fresh readings, not answers.
    - The outlier rule only names culprits.
  - **Logs come from Loki.** Nodes export logs over OTLP to the Collector, which sends them to Loki. The node is the
    OpenTelemetry resource's `service.instance.id`, which Loki indexes as a label; framework logs add
    `daserver.actor.type`, `daserver.actor.key` and the trace id as attributes, kept as structured metadata (the actor
    key is never a label: it is unbounded). The Dashboard queries Loki's HTTP API (LogQL `query_range` for an actor's
    or a node's lines, and the patterns API, from the pattern ingester, for the incident's grouped patterns), one
    query per server type with the node as a label, never one per node. Results are cached in the snapshot and shown
    inline (the incident's server type and time, a node, an actor); the audit is read from its durable sink, not from
    Loki.
  - **History is Grafana's.** Each view links to Grafana at the same time range (the shipped dashboard, or Explore
    for logs and traces); there are no ad-hoc queries, no dashboard editor and no long-term storage of the
    Dashboard's own. The Aspire dashboard is for local runs.
  - **Optional sources.** Without Loki the log screens say that Loki is not configured (the audit is
    unaffected: it is in its own sink); without Grafana the history links are absent.
  - **Sign-in is OIDC**, with the roles view and act. The cookie's Data Protection key ring is shared by the replicas
    (in the store, or a mounted key). Every action carries an antiforgery token, and the cookie is `SameSite=Lax`
    (Strict would drop it on links from the pager and on the identity provider's redirect back). An expired session
    answers 401, not a redirect, and the confirmation keeps what was typed.
    The front end writes all data as text, never as markup, under a strict Content Security Policy (`default-src
    'self'`, no inline script, `frame-ancestors 'none'`); fonts and the chart library are served by the Dashboard,
    never a CDN, and styles are set through CSSOM, never as `style` attributes. Component values, which may hold personal data, are shown to
    the view role only when the deployment allows it (`DaServer:Dashboard:ViewComponents`).
  - **The front end is vanilla TypeScript built by esbuild**, its assets prebuilt and embedded in the package, so only
    Dashboard contributors need a JavaScript toolchain; the look and motion follow doc/dashboard/design.md.
- Alternatives: Grafana dashboards plus the Aspire dashboard (three sites, no actions, no audit); a plugin for the
  Aspire dashboard (it has no plugin model); our own log store (Loki already indexes OTLP logs at scale); Elasticsearch
  or OpenSearch (heavier to run for what the screens query); Svelte with Vite (a framework for a few screens, slower
  builds).
- Consequences: roadmap W7e builds it; the samples' AppHosts add the Collector, Prometheus, Loki, Tempo and Grafana
  for published runs through the publisher hooks; doc/operations.md describes the telemetry path; framework log calls
  use `LoggerMessage` with those attributes, off the hot paths; the roles step creates `daserver_audit`,
  `daserver_actions` and `daserver_dataprotection` and the Dashboard's login role; deployment adds the
  `{cluster}-admin` Secret.

## ADR-094 Kubernetes objects and controllers: which object each part uses, who owns which field
- Context: a published cluster is acted on by several writers at once: the chart (applied by `aspire deploy`, which
  runs `helm upgrade --install --server-side=true --force-conflicts`, so every field the chart renders is taken back
  on each deploy), autoscalers (HPA, KEDA's HPA, the VPA), node drains through the eviction API, and DaServer's own
  rollout Job. Each workload kind behaves differently under them: a StatefulSet with `Parallel` pod management
  deletes its highest ordinals at once, a PodDisruptionBudget guards evictions but not a scale-in, a DaemonSet is not
  evicted by `kubectl drain` and cannot be scaled by an HPA, and .NET fixes its processor count at start.
- Decision (the tables are [drafts/deployment.md](drafts/deployment.md) sections 10 and 11):
  - **Kinds**: StatefulSets for actor-hosting groups (ADR-085), Deployments for gateways, stateless groups, the
    Dashboard and the Collector's gateway, Jobs for schema, upgrade check, rollout and load generators; no DaemonSet
    for nodes (the count follows machines, no HPA, no graceful leave on a drain, no stable names) and no CronJob.
    DaemonSets stay the platform's (a Collector agent, node-exporter, CNI and CSI).
  - **Scaling policy is the application's, written by the sample's hook**: KEDA or an HPA on the group's average
    busy ratio (`avg`, never `max`; an explicit minimum, KEDA's default is 0); scale-in one pod per period, the
    period at least the graceful stop and a stabilization window of 600 s or more, since a PDB does not slow a
    scale-in; CPU scaling horizontally; a VPA `Off` or `Initial` on gateways and stateless groups
    (memory only when active), and `Off` on hot-restart pods, whose node and parked containers swap roles at every handover: VPA
    recommends per container name, so an active one would size the parked container as a node or race the rollout's
    resize; the rollout reads the recommendation instead (the larger memory target of the two containers, never below
    the chart's node size) when it sizes the standby. The busy ratio comes from node processes only, so the HPA's
    average counts nodes whichever container runs them; a `ContainerResource` HPA cannot name the node's container;
    PDBs with
    `maxUnavailable: 1` for stateful groups, a percentage for gateways, `unhealthyPodEvictionPolicy: AlwaysAllow`;
    none for the one-replica Dashboard. DA only names the scale target (StatefulSet or Deployment) and makes the
    `Manifest` type public for objects Aspire has no class for.
  - **Field ownership through Server-Side Apply**: the chart (`helm`) owns what the AppHost declares; an autoscaled
    group renders no `replicas` (the hook that adds the autoscaler clears it), so the HPA owns the count; the rollout
    Job applies as field manager `daserver-rollout`, forcing only the parked container's image, swapping the two containers'
    requests, and, without force, a pause of the group's scale-in while it rolls (a pod deleted mid-handover stops
    gracefully, but its roll fails and its actors move). Nodes
    and the Dashboard write nothing to the Kubernetes API.
  - **In-place resize never changes a running process's parallelism**: a node reads its cgroup memory limit and calls
    `GC.RefreshMemoryLimit()` when it changes, so `resizePolicy memory: NotRequired` resizes without a restart; CPU
    requests set the world count only at start (`DOTNET_PROCESSOR_COUNT`), and node pods set no CPU limit. A
    hot-restart pod's CPU request is resized only by the rollout, which sets it by role, and its processor count is the chart's
    node size as a literal, not the request; a crash takeover that leaves the requests swapped is reported by the
    node (`daserver.node.requests_low`), whose `/health` says so; the Dashboard, polling every node, checks it and dispatches the rollout's resize-only run with its own token, once per incarnation plus one retry, outside the type's lock (it is idempotent). The run sets
    requests by role (the lock holder node-sized, the other idle-sized), so repeats are harmless, in a per-node
    concurrency group. Nodes hold no pipeline credential: a pipeline token cannot be scoped to one kind of run.
    Without a Dashboard, the `DaServerNodeRequestsLow` alert and the manual command.
- Alternatives: rendering `replicas` and having the deploy fight the autoscaler (each deploy resets the size, a mass
  leave for a stateful group); `pod-deletion-cost` to pick which gateway goes (needs API access from someone, and
  rebalancing, ADR-089, already keeps gateways even); StatefulSet `maxUnavailable` (beta) or the `Recreate` strategy
  (alpha); a pre-pull DaemonSet (pulls on every machine); native sidecars (nothing to run beside a node);
  client-side `kubectl apply` (no conflict detection, the annotation's size cap, stale state).
- Consequences: roadmap W11 (scale-target helper, `replicas` cleared with the autoscaler, the samples' HPA/KEDA
  behaviors and PDBs, the rollout's SSA writes and scale-in pause) and the memory-limit refresh step;
  doc/operations.md tells operators the same rules for clusters that predate them.

## ADR-095 Client messages: activation by record, keys from claims, several presences
- Context: the first target model let a client `[To]` message activate any key (an invented room number inserted a
  row), trusted every key field the client wrote (`Order(Sale, User)` let one buyer order for another), allowed
  one presence per actor type (a client in two chat rooms, or a party and a guild, could not exist) and left unsaid
  where a message without `[To]` lands on a `[JoinOnConnect]` actor whose per-player state is on the root.
- Decision (the rules are [drafts/programming-model.md](drafts/programming-model.md) §4, §6, §7 and §8):
  - **A client `[To]` message reaches only keys that have a record**, unless the actor type lists it in
    `[ActivatedBy<TMessage>]` (several allowed), whose handler validates what it creates. The claim then runs without
    its `INSERT` branch and requires a written record, so a made-up key answers `NoActor` and writes nothing. A client
    `[To]` message to a type without `[ActivatedBy]` is analyzer warning DAS024 (suppressible where intended). An
    actor type whose root does not persist has no record, so a client `[To]` message activates it only through
    `[ActivatedBy]`, and its activation leaves no row in the store. One rule for every type: without `[ActivatedBy]`
    server messages create keys and client messages do not; with it, only the listed messages create keys, server
    messages included (the list is the type's whole set of creating messages).
    `[ActivatedBy]` narrows only first activations: `Wake` and the `[JoinOnConnect]` join (its key is computed from the
    claims, so a first login creates the row) always activate.
  - **A pure gate before the claim**: beside `[ActivatedBy]`, `static bool CanCreate(in TClaims who, in TMessage m)` for a client message, `CanCreate(in TMessage m)` for a server message (it has no claims)
    (which may also take at most one `in` configuration table, the node's immutable snapshot; more is an analyzer
    error, so the generated registration has one overload shape and what a gate needs lives in one table) runs on the owner before the
    claim; for a saved type `false` turns the claim into the no-insert one, so a key that already has a record still
    activates (a map taken out of the map table keeps its record and still runs) and only a key without a record
    answers `NoActor`, writing nothing (a type that saves nothing answers `NoActor` at once); validation that needs no actor state (a map id in the map table, a sale that
    exists) costs no row. An analyzer error flags a `CanCreate` whose message is not in `[ActivatedBy]` and a
    `CanCreate`-shaped method under another name, and one taking more than one table or anything else. Rows still never written expire after `DaServer:EmptyRecordTtl`
    (1 day): the oldest `Active` member hosting a persisted type deletes them in key-set batches through a
    `SECURITY DEFINER` function, and the runtime role has no
    `DELETE` grant.
  - **Part of a key may come from the claims**: the message carries only the fields a client may state, and the
    actor type declares `static K Route(in TClaims who, in TMessage m)`, a pure function the gateway's generated route
    calls without allocating. A message without a matching `Route`, or a `Route` whose identity type is not the
    project's `IClaims`, is an analyzer error when the key has fields the message does not carry.
  - **Several presences of one type**: at most one presence per actor and `[ActorOptions(PresencesPerClient = n)]`
    per actor type (default 1: a second map leaves the first; a chat or a queue raises it); joining past the limit
    leaves the oldest. A type above 1 handles only `[To]` client messages (analyzer check), and handlers recognise a
    member with `root.TryGetClient(out var presence)`. `Status root.JoinClient(out presence, prefab?)` (like `Join()`) finds the client's presence by the
    framework's `(ActorId, ClientId)` index or spawns one, then joins; `ClientId` is one login, so membership that
    must survive a re-login keys its member by the account instead (`Ticket(Queue, long Account)`).
  - **A client message without `[To]` handled by a `[JoinOnConnect]` type binds to the root**, where per-player state
    (inventory, wallet) lives; the presence is `root.TryGetClient`. A component parameter missing from `K.RootPrefab`
    (or from the presence's prefab, for presence subjects) fails `Registration` at construction, since the generator
    cannot see prefab compositions.
- Alternatives: any message activates (a client fills the store with invented keys); a server-side lookup actor in
  front of every creation (a hop on every message for what one attribute says); client-written key fields checked by
  the handler (each handler repeats the check, and a forgotten one is a cross-user write); a fixed limit of one presence per type (above); handlers on
  the presence for every client message (per-player state would be copied or duplicated onto presences).
- Consequences: the generator emits per-message routes on `Connection`, `[ActivatedBy]` lists and the claim's
  no-insert path in W4/W5; samples use each (ADR-091: the starter's rooms, the flash sale's gate). Client requests
  that must not run twice are `[Once]`, numbered by the SDK within a session that is a gateway-signed token with a
  sliding end `T`, refused by any gateway past `T` (ADR-099). Tests are listed under W4, W5 and W7.

## ADR-096 One order per sender and target; answers are messages; `[Latest]` for state; links fragment and coalesce
- Context: the drafts kept answers (`Replied`, a `ReplyTo<T>` reply) and presence notices on a control channel apart
  from the data stream, unordered against it, so an answer and a later message from the same sender could arrive in
  either order. The mmo sample needed a `Vanishing` tag, two branches and a test only because Loot's answer to a
  `Claim` could land after Loot's `Gone`, and every protocol of the shape "answer, then tell" would repeat that. How
  others draw the line:
  - Erlang/OTP: "if an entity sends multiple signals to the same destination entity, the order is preserved"
    ([processes](https://www.erlang.org/doc/system/ref_man_processes.html)); a `gen_server:call` reply is an ordinary
    message, so users learn one rule. Large distribution messages are fragmented since OTP 22 so that other traffic
    interleaves, with the same per-pair order ([OTP 22](https://www.erlang.org/news/132),
    [distribution protocol](https://www.erlang.org/doc/apps/erts/erl_dist_protocol.html)).
  - Akka/Pekko: per sender-receiver order for direct tells; system messages travel apart and "are not ordered relative
    to ordinary user messages", and Artery keeps control traffic apart so "a large user message cannot block an urgent
    system message" ([delivery reliability](https://doc.akka.io/libraries/akka-core/current/general/message-delivery-reliability.html),
    [Artery](https://doc.akka.io/libraries/akka-core/current//remoting-artery.html)); an `ask` reply comes from a
    temporary actor, so it has no order against the target's later tells: the same trap as above. Proto.Actor follows
    Akka ([durability](https://proto.actor/docs/protoactor/durability/)).
  - Orleans has no per-pair sequence and leaves order to awaiting each call
    ([delivery guarantees](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/messaging-delivery-guarantees)).
- Decision:
  - **One rule users see**: everything one sender sends to one target (messages, requests, answers, presence notices
    `Route`, `Lost`, `JoinConfirmed`, `TakeOver`, `HandedOff`) arrives in send order; an answer counts as a message from
    the actor that gives it; sends to different targets never wait on each other; the only exception is best effort
    during the second or two of a migration, besides the ones the draft's §1 lists for `[Latest]`, `Commit`'s group
    pushes and `AskUntilAnswered` (asks are ordered only among their first sends to the same target, not against the
    root's other mail to it and not across retries, ADR-100). Clients are covered the same way (a client to an actor, an actor to a
    client). The sender's `Outbox` already appends per destination node in order, so answers simply stay in that
    stream, and a `Replied` goes through the asker's mailbox in arrival order.
  - **Never refusing an answer is an admission rule, not a lane**: an answer's room is reserved in bytes when its ask
    is made (the request type's maximum reply size, `DaServer:MaxReplyBytes` or `[MaxReplySize(n)]`), in the asker's
    dispatch queue and its link window, released on the answer or at the deadline, and a new ask is refused locally
    past half of either, so one actor's 65,536 asks cannot fill a shard and stall everyone until a timeout; presence
    notices are bounded by the join rate; a full queue or window refuses new messages and asks, never an answer.
  - **Admission is per actor**: each mailbox is bounded (`[ActorOptions(MaxMail, MaxMailBytes)]`), so a hot actor
    refuses only its own mail and never makes its shard's neighbours answer `Busy`; the dispatch queue's bound is the
    node's memory guard behind it (Akka's bounded mailboxes, Orleans' per-grain queue limit). `MaxPending` past its
    limit answers an ask `Busy` instead of throwing.
  - **One pair per gateway**: everything an actor sends to one gateway node (group frames, `TakeOver`, `HandedOff`,
    direct pushes) is one pair for ordering and interleaving, so a fragmented group frame stays ahead of the fence
    behind it and an actor's pushes to a client keep their order.
  - **Full is not broken**: a link never disconnects because its window is full; it stops admitting (`Busy`, one-way
    mail dropped) and drains. A push dropped at a link or a gateway, or conflated for a slow client (the newest frame
    per group kept, the client disconnected only after `SlowClientGrace`, 10 s), marks the affected presences
    `MissedPushes`, and the snapshot system resends their state within `DaServer:ResyncsPerTick` per actor and tick.
  - **Lanes only for what no handler can observe**: heartbeats, membership and view exchange, link acks, `Fence`,
    `Wake`, `HandoffQuery`, a large message's completion notice and `[Once]`'s acknowledgements, `AckQuery` and window updates are written ahead of queued data, so failure detection never waits behind bulk traffic.
  - **`[Latest]` instead of unordered messages**: on a one-way message (`IFromClient`, `IMessageTo<K>`) a newer message
    of the type from the same sender to the same target replaces an older one still unhandled in the mailbox: in place
    (its slot and time kept) when the old one is the sender's last item to that target, so 100,000 senders at 30 Hz
    behind a backlog are each still served instead of being pushed back to the tail forever; otherwise the old one is
    dropped and the new one appended (taking its slot would put the new one ahead of messages sent before it), so what
    arrives is always a subsequence of what was sent, in order. Each such message carries a sequence per (sender,
    target, type), the sender a client's `ClientId`, and an older one is dropped, so a stale message forwarded by a
    tombstone or still in flight from an old gateway never replaces a newer one. Movement input and view
    direction lose nothing that matters when an actor falls behind, and nothing else is reordered. Requests, pushes and
    topic messages cannot carry it (DAS030).
  - **Links fragment and coalesce**: a message above `DaServer:Links:FragmentSize` (64 KiB) is split and its fragments
    interleave round robin with other pairs' frames, so a 4 MB message delays another pair's answer by one fragment;
    at most `DaServer:Links:LargeInFlight` (4) large messages are in progress per link and the receiver reassembles
    within `DaServer:Links:ReassemblyBytes` (64 MiB), admitting a message whole on its first fragment, keeping partial
    buffers across a reconnect and dropping them when the peer's generation changes; the window is at least
    `MaxMessageSize` plus the answer reservations; a rate-limited group frame flushes early at
    `DaServer:GroupFrameMaxBytes` (256 KiB);
    small frames keep being copied into one pooled batch buffer per link or connection and written with one `send` per
    flush (ADR-034, ADR-070's optional beat); a flush that carries a fragment hands it over without copying as a
    gather write, so it stays one system call; receiving parses every frame of a pooled 64 KiB buffer per `recv`; acks
    ride on data frames and heartbeats only fill idle directions (ADR-053).
- Alternatives:
  - Two lanes, documented: every "answer, then tell" protocol becomes a tag, two branches and a test in user code, to
    save a framework admission rule.
  - Unordered messages that jump the queue ("run at once"): an actor runs one message at a time, so "at once" means
    "ahead of what was sent earlier", which reorders state changes, and users must judge per message type which may
    jump; messages to different targets already never wait on each other, and `[Latest]` covers the real need (stale
    state that need not be handled).
  - Per-pair sequence numbers reassembled at the receiver, so answers could be prioritised on the wire: the receiver
    must still hold an answer until the earlier data arrives, so it adds buffering and bookkeeping and gains no latency.
  - Akka's per-actor large-message lane: needs configuration per destination; fragmentation needs none.
  - Gather writes for every frame: ADR-034 measured that the copy of small frames is not the cost.
- Consequences: the drafts' §1 guarantee 2 and architecture.md section 3 ("Links") state it; the mmo sample needs no tag
  for the order of an answer and a later message, and asks Loot from the map's root; roadmap W4 builds the links and `[Latest]` (tests
  `AnswerThenMessageArriveInSendOrder`, `LatestNeverOvertakesOtherMessages`, `LatestUnderBacklogStillDelivered`,
  `StaleLatestAfterMigrationDropped`, `HotActorRefusesOnlyItsOwnMail`, `AskReservationRefusesLocally`,
  `FenceHoldsBehindFragmentedGroupFrame`, `ReassemblyBudgetBounded`, `DroppedPushMarksMissedPushes`,
  `SlowClientConflatedNotDisconnected`, `SmallAnswerInterleavesWithFragments`, …),
  with BenchmarkDotNet's system calls per message at 10,000 connections and a small answer's latency behind a 4 MB
  message on one link.

## ADR-097 Game persistence: batched checkpoints, `Commit` for valuable changes, orders as actors
- Context: a game must not lose a purchase or a payment, yet saving every change at once costs a store write per
  item. A framework-level exactly-once delivery protocol (persisted waiting entities, a deadline on every ask, a
  reply log per root) is heavy, and business flows still need their own deduplication on top. What others do:
  - Game servers save periodically and at logout, and commit value transfers at once: TrinityCore's
    `PlayerSaveInterval` defaults to 90 s with a save in `LogoutPlayer`, and a completed trade writes both players'
    inventory and gold in one transaction ([worldserver.conf.dist](https://github.com/TrinityCore/TrinityCore/blob/master/src/server/worldserver/worldserver.conf.dist),
    [TradeHandler.cpp](https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Handlers/TradeHandler.cpp));
    AzerothCore saves every 15 minutes, with optional extra saves after important changes
    ([worldserver.conf.dist](https://github.com/azerothcore/azerothcore-wotlk/blob/master/src/server/apps/worldserver/worldserver.conf.dist)).
  - Actor frameworks write explicitly and reply after the write, and none deliver exactly once: Orleans' grains call
    `WriteStateAsync()` and "it is the responsibility of the caller to retry" end to end
    ([best practices](https://learn.microsoft.com/en-us/dotnet/orleans/resources/best-practices)); Akka Persistence
    replies after `persist` ([persistence](https://doc.akka.io/libraries/akka-core/current/typed/persistence.html#replies))
    and Petabridge argues for idempotent receivers over exactly-once
    ([blog](https://petabridge.com/blog/akkadotnet-exactly-once-message-delivery/)); Dapr and Service Fabric save after
    each method ([Dapr](https://v1-14.docs.dapr.io/developing-applications/sdks/dotnet/dotnet-actors/dotnet-actors-howto),
    [Service Fabric](https://learn.microsoft.com/en-us/azure/service-fabric/service-fabric-reliable-actors-access-save-remove-state)).
  - Payments deduplicate by business keys and reconcile: Stripe retries webhooks for three days and tells receivers to
    log processed event ids ([webhooks](https://docs.stripe.com/webhooks)); Google Play refunds an unacknowledged
    purchase after three days ([lifecycle](https://www.revenuecat.com/blog/engineering/google-play-lifecycle.md));
    Nakama records each validated receipt ([IAP](https://heroiclabs.com/docs/nakama/concepts/iap-validation)); PlayFab
    requires an `IdempotencyId` ([fraud prevention](https://learn.microsoft.com/en-us/gaming/playfab/features/economy-v2/fraud-prevention));
    the transactional outbox writes state and intent together and lets the consumer deduplicate
    ([outbox](https://microservices.io/patterns/data/transactional-outbox)).
- Decision:
  - **Default persistence is the game convention**: change-tracked roots are written in batches every
    `DaServer:CheckpointInterval` (30 s, each root's instant staggered by its key's hash, oldest unsaved first, a quarter
    of every write round kept for them while commits flow), and always before deactivation (a logout), a migration
    and a hot restart's handover; a graceful stop writes within its stop budget and logs what it could not. A crash
    loses the larger of one interval and the time the write backlog takes to drain
    (`daserver.store.oldest_unsaved_age`), consistently within each actor. Writes are split between roots at 512 roots and
    `DaServer:Store:StatementBytes` of records (a root's record and row changes never split: its row changes go in as many
    row statements as they need in its write's transaction), a record at `DaServer:Store:MaxRecordBytes`, rows sorted; transient errors retry
    the same bytes (a timed-out write cancelled first), and a deterministic error bisects the write between roots to its one
    bad root (`RecordUnwritable`), so one root never blocks a world's writes; a world whose backlog passes
    `DaServer:Store:BacklogPause` stops taking mail until it drains.
  - **`root.Commit()` for valuable changes**: the root joins this tick's batched write (group commit, writes split
    between roots at 512 roots), and everything the item sent is held per root with its write generation, released in order when the
    write lands and dropped on a fence; the held output is a barrier per (root, destination actor), so the one order
    of ADR-096 holds; a held group push joins its group's frame only on release (ordered against other items' group
    pushes by release, the one exception besides migration, so one write never stalls a group's broadcast); a reply
    past its deadline is dropped on release; held output is bounded per root and per world
    (`DaServer:Commit:HeldBytesPerRoot`, `HeldBytesPerWorld`), past which new requests to the root are answered
    `Busy(RetryAfter ≈ write latency)`; handlers never wait (ADR-081); a `Commit` with nothing unwritten writes nothing,
    and with an earlier change still unwritten it waits for that write (so a repeat's answer never runs ahead of the
    first one's write). Only saved roots may call it (DAS031). `root.IssueClientToken` commits implicitly.
  - **Orders are their own actors**: a payment is `Payment(payment id)`, a flash-sale order `Order(sale, buyer)`, each a
    record row of its own (the orders table); a repeated webhook or a buyer coming back is answered from that actor's
    state, with no other deduplication.
  - **The step that moves value into another actor runs exactly once**: a `[Once]` request with framework-assigned
    sequence numbers, cumulative acknowledgements, windows and probes, as TCP does it (ADR-099), sent by
    `AskUntilAnswered`, whose request is the persisted intent the runtime keeps asking (ADR-100).
  - **One rule for moving value between actors**: the debit side writes its own state and `AskUntilAnswered`s a
    `[Once]` request (committed together); the credit side runs it once, stores its reply and commits before
    answering; the debit side records completion in the `Replied`, which comes once with a value or the receiver's
    definite refusal. The effect happens exactly once and is never lost, with no condition on time or capacity: the
    numbers never repeat, a repeat before the acknowledgement gets the stored reply, an older attempt after it is
    refused as stale, a full window makes the sender wait instead of forgetting anything. An ordinary item that reads
    uncommitted state and tells another destination is not held back: it commits too when it must not run ahead of
    the write.
  - Samples: the shared payment sample (ADR-091: `Payment` commits `Paid` before answering the verified webhook and
    `AskUntilAnswered`s the owner `[Once] Settled`; the mmo's `Player` credits once; a daily reconciliation; written out
    as code in the draft's §8), purchases as `[Once]` client requests, Loot's `Granting` and `AskUntilAnswered` of
    `[Once] GrantItem`, with `Granted` called only on a value or a definite refusal; the flash sale's order saga, each
    step a state of the order and an `AskUntilAnswered` (`Reserve`, then a `Payment` of the shared sample, `Release` on
    a definite refusal).
- Alternatives:
  - Keeping `[Durable]` and trimming it: the delivery protocol still costs the settle-by machinery on every durable ask
    and on the client, and still cannot replace keyed deduplication where an operation is asked again under a new
    call id (a provider's retry, an asker's next activation after a crash).
  - Framework exactly-once by call id for every request: a stored reply and a write per request; no actor framework
    above does it.
  - Writing every reply durably: a store write of latency on every request.
  - Pausing the actor until its write lands: a ticking actor stalls with every presence on it (ADR-081).
- Consequences: the drafts' §8 and §10 and architecture.md sections 2 to 4 state it; the framework has no delivery
  protocol (no durable attribute, settle-by instant or settling re-ask, and no `TooLate`, `SettleTooShort` or
  `Expired` status); roadmap W6 builds `Commit` (tests `CommitReplyHeldUntilWrite`, `CommitFencedDropsOutput`,
  `CommitsShareTickStatement`, `CommitBarrierKeepsPairOrder`, `IssueClientTokenCommitsImplicitly`,
  `HeldGroupPushJoinsFrameOnRelease`, `HeldOutputCapAnswersBusy`, `PoisonRowBisectedRestLands`,
  `TimedOutStatementHalved`) with BenchmarkDotNet's commit throughput per world at a store latency of 1 and 5 ms;
  `[Once]` and `AskUntilAnswered` are ADR-099's and ADR-100's; W7 rewrites the samples.

## ADR-098 Hot update has one path: a warm standby handover over a local pipe
- Context: the drafts had two hot-restart shapes: a snapshot file written before the process exits and read by its
  restarted successor (every platform), and an optional warm standby on platforms that can run a second instance. The
  file path's pause is mostly process start (about 0.3–1 s under AOT, 1–2 s under JIT plus tier-0 code until tiering
  catches up) and on Kubernetes the container restart on top; the standby path is about 0.1–0.3 s at 100,000 actors
  and 0.3–0.7 s at 1,000,000 (the freeze, a parallel stream, the standby's load; EasyEcs `Save` and `Load` at about
  5.8 and 1.6 GB/s, ADR-084's numbers). In-process reload was weighed again:
  - a collectible `AssemblyLoadContext` unloads only cooperatively, and statics, delegates, `Type` objects, constructed
    generics, timers or thread statics that reach it keep it alive without an error
    ([unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability),
    [collectible assemblies](https://learn.microsoft.com/en-us/dotnet/fundamentals/reflection/collectible-assemblies));
    collectible contexts ignore ReadyToRun code; a type loaded again is a different type, so components, messages and
    Nino formatters cannot live in it; and Native AOT loads no assemblies at all
    ([limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/));
  - `MetadataUpdater.ApplyUpdate` makes "no guarantees as to the state of the assembly and process if the delta includes
    unsupported changes" and Release builds turn it off
    ([ApplyUpdate](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadataupdater.applyupdate)).
- Decision:
  - **One path on every platform, JIT and AOT alike: the warm standby handover** (mechanics only in the draft's §10,
    architecture.md section 5). A stateful node is a pair of instances sharing a small directory; the lock on
    `node.lock` makes one the node; the other is the standby while the node is armed with its manifest hash and parks
    otherwise (holding nothing, a few tens of MB; it takes the lock when the node dies). The trigger restarts the
    instance that is not the node, on the new build; the armed node connects over a local named pipe (a Unix domain
    socket, `CurrentUserOnly`), passes the handshake (nonce, manifest hash, deadline, process id), takes the restart
    permit, freezes, streams each world on its own thread with an incremental checksum, passes its listening sockets
    (`SCM_RIGHTS` on Unix, `Socket.DuplicateAndClose` on Windows; Kestrel's `ListenHandle` where it takes one), and
    after the standby's ack releases the lock, destroys its worlds, returns their memory (one compacting full
    collection) and parks in place as the pair's parked instance, never exiting, so no handover waits on a supervisor
    and a Kubernetes pod keeps its readiness; on a nack, a broken stream, a timeout or a freeze past its bound it resumes in
    place and nothing is lost. There is no snapshot file, no exit after a failed handover and no SIGTERM-started hot
    restart: SIGTERM is always a graceful stop (ADR-063).
  - **Paths**: in place (the handover carries the worlds), through records (`mode=unload` or a layout the manifest diff
    says needs it: every actor written first, empty worlds handed over), or refused (a rolling upgrade).
  - **Platforms**: systemd (two template instances, the directory from `tmpfiles.d`), launchd (two jobs, `kickstart
    -k`), Windows (two services, one account or a pipe ACL), Compose (an a/b pair joined to a pinned holder's network
    namespace, `restart: unless-stopped`), local Aspire (two replicas) and **Kubernetes: one pod with two containers**
    `node-a` and `node-b` sharing the pod's network and a small memory `emptyDir`; the old process parks after the
    handover rather than exit (an exited container would only be restarted into the same pod, freeing nothing); requests
    follow the role, so the pod is sized as one node plus an idle instance: the rollout Job raises the parked
    container's CPU and memory requests to the node size in place, applies only its image, lowers the old container's
    to the idle size after the handover (the kubelet restarts only that container), and for a
    same-image roll has the node tell the parked instance to exit; each container has its own probe port and the idle
    one reports ready, so the pod's readiness follows the node except for the seconds the parked container restarts
    (no client Service depends on a hot-restart pod; the PDB counts it as one unavailable); the autoscalers see one node
    per pod: the HPA averages the busy ratio, which only node processes export, and the VPA is `Off` on these pods and
    read by the Job (ADR-094); the in-place resize needs
    Kubernetes 1.33 or later and Linux pods, and a resize that is `Infeasible` or still `Deferred` at its deadline is
    set back, so that pod, like one that cannot fit the standby, takes the delete-and-recreate path through records
    (ADR-085).
  - **User behaviour before and after is data**: `Restarting(ExpectedPause)` on every root for the last tick before the
    freeze, `Resumed(Pause, Aborted)` for the first tick after, reacted to by systems; `RestartingNodes` lets gateway
    systems tell clients.
- Alternatives:
  - The snapshot file, alone or beside the standby: process start lands inside the pause, the file needs a tmpfs
    sized to the state and checks for a rebooted machine or a wrong image, and two shapes double the tests.
  - A JIT-only `AssemblyLoadContext` logic reload beside it: a third project per server type, analyzer rules against
    statics, threads and delegates in the logic, leak detection, cache-reset APIs from EasyEcs and Nino, two code paths,
    nothing for AOT nodes, and nothing for a change that touches data or the framework.
  - `MetadataUpdater` deltas: a development tool, off in Release builds.
  - A standby on Kubernetes started as an ephemeral container: ephemeral containers cannot have ports or probes and
    are for debugging; a permanent warm standby per pod: twice the memory always.
- Consequences: the draft's §10, architecture.md section 5 and deployment.md sections 0, 2, 3, 5, 8, 10 and 11 state
  it; there is no snapshot directory, restart mode or exit-on-arm setting, `/admin/restart` takes only
  `mode=unload` and `dryRun=true`, and the exit code 75 belongs to the isolation exit alone; roadmap W6 builds the handover with its platform triggers, W11 the
  two-container pod, the Job's sizing of the requests by role and the autoscalers on it (`VpaOffOnHotRestartPods`, `ParkedInstanceExportsNoBusyRatio`), W8 checks the socket passing under AOT, and BenchmarkDotNet's `HandoverPause`
  measures the pause at 100,000 and 1,000,000 actors under JIT and AOT.

## ADR-099 `[Once]`: exactly-once requests the way TCP does it
- Context: the value-moving step of ADR-097 must run exactly once, under retries, crashes, migrations, hot restarts
  and reconnects, without the user choosing keys (a key the user chooses can repeat: a map's drop numbers after a
  crash) and without a second concept beside `[Once]`. TCP solves the same problem (RFC 9293,
  [rfc-editor.org/rfc/rfc9293](https://www.rfc-editor.org/rfc/rfc9293)): the sender numbers what it sends, the
  receiver checks numbers against what it has accepted, acknowledgements are cumulative and ride on later traffic so a
  lost one costs nothing, a receive window bounds what each sender may have outstanding, and probes cover silence.
  What TCP's TIME-WAIT does (keep a connection's state until no old segment can arrive) is the hard part for a
  receiver that must remember senders: forgetting a sender by what the receiver last saw from it is wrong whenever
  a later attempt is still in a link window, in reassembly or behind a frozen world, and forgetting a client session
  by its token's end is wrong whenever the client keeps the session alive while its resends cannot reach the
  receiver.
- Decision:
  - **One marker, `[Once]`, on the request type**; the user writes no key. It is allowed on `IFromClient<R>` and
    `IRequestTo<K, R>`; only a client session or an actor whose root is saved may send one, and a server
    `[Once]` request is sent only by `AskUntilAnswered` (ADR-100); DAS032 covers every `[Once]` shape and sending rule,
    DAS031 every case of an unsaved root where a saved one is needed (handling or sending `[Once]` from one included);
    the two never report the same code.
  - **Sequence numbers per sender, assigned by the framework**: an actor's counter is `RootState.OnceNext`, taken by
    `AskUntilAnswered` and written with that item (the ask implies `Commit`), so a crash, a migration or a handover
    continues it and a number is never reused. A sequence is a pair `(seed: long, counter: uint)`, compared seed
    first, on the wire, in `Unacked` and in the sender rows: an actor's seed is its record's first claim epoch (the
    store's sequence), so a deleted and recreated actor (a map's `Loot`, a recreated `Order`) numbers above every
    number its earlier incarnation used, with no shift to overflow however large epochs grow; the counter alerts with
    2^24 left (`daserver.once.counter_low`) and the root stops sending `[Once]` when it runs out. A client session's
    seed is 0 and its counter the call id within the session (below). Per sender rather than per pair: the sender
    keeps one counter and one "lowest unsettled per receiver", and a receiver seeing gaps (numbers sent elsewhere)
    changes nothing, since its checks are "at or below the acknowledged mark" and "in my entries".
  - **Each attempt has its own identity**: a new call id per attempt for an actor sender; for a client, whose call id
    is the sequence, an attempt number in the envelope. A `Faulted` or `NotHandled` counts only for the current attempt.
  - **Each attempt carries a UTC sent-at**, stamped by the sender's node for an actor and by the gateway on receipt
    for a client (a device clock is not trusted); the receiver drops any attempt sent more than
    `MaxAskTimeout + ClockSkew` ago (counted, alert `DaServerOnceTooOld`: a clock drifting past `ClockSkew` would
    otherwise drop every attempt silently). Deadlines travel as remaining time, which does not shrink while a frame
    waits; the sent-at is absolute, so the two forgetting rules below rest on two absolute instants (a sender's
    deletion, a client call's resend deadline) and never on what the receiver happened to see.
  - **The receiver**: `Unacked` entries in the root's record (sender, sequence, the stored reply of at most 32 bytes,
    the write generation; at most `[ActorOptions(Unacked)]`, 1,024, about 84 bytes an entry with a 32-byte sender id,
    about 84 KiB full, rewritten with every write of the record). An entry lives about one reply write, one write of
    the sender's answer and one acknowledgement, 0.1–0.2 s on a healthy store, so a receiver taking 5,000 `[Once]`
    requests a second holds 500–1,000 entries; such a receiver raises `Unacked`. Per sender a row `OnceSenders` (the
    mark `n`; `forget_after` once the sending actor is deleted; `delete_after` for a client session), about 52 bytes,
    **a store row of its own** (`daserver_once_senders`), outside the record because rows follow senders, not entries.
    - Changed rows (a moved mark, a new sender) are written in the record's write transaction and only where its
      compare succeeded, since moving `n` and dropping `Unacked` entries must land together. A new sender costs two
      changes in two writes (the row, then the acknowledgement moving `n`), about 52 bytes each; a root's row changes
      go in as many row statements as they need inside its write's transaction, so nothing caps them per write and a
      receiver's new-sender rate is bounded by its handler throughput and the store (5,000 new senders a second at 20
      writes a second is about 500 row changes, 26 KB, a write).
    - A sending actor's row is inserted only while that actor's record exists (`FOR KEY SHARE` on it), so a forgotten
      row is never written back.
    - Rows are not in migration or handover bytes: the move's and the freeze's last write carries the root's
      unwritten row changes, the target range-reads the rows after its claim commits (read earlier, a fenced owner's
      late write could roll `n` back), and acknowledgements arriving after that last write are dropped (the next
      cumulative one reaches the new owner).
    - At most `DaServer:Once:SendersPerReceiver` (1,048,576, about 55 MB) rows per receiver, past which a new sender
      gets `WindowFull`; no row is evicted.
    - A receiver holding entries carries a framework `WakeAt` at its oldest entry plus `ProbeAfter`, so a deactivated
      receiver wakes to probe. A request at or below `n` is stale and refused (`Stale`, counted); one in
      `Unacked` gets its stored reply, after that entry's write; one to a missing receiver it may not activate gets
      `NoActor`; a root being deleted answers `Busy`, not stored; otherwise the handler runs once, commits
      implicitly, and its reply is stored. A value, `Stale`, `WindowFull` and `NoActor` are definite; `Faulted` and
      `NotHandled` are definite too but not stored, so the sender accepts them only when they answer the current
      attempt; only link outcomes (and `Busy`) are unknown. The handler must answer at once (no `ReplyTo<T>`,
      DAS032), so the stored reply and the change land in one write.
  - **Forgetting an actor sender is tied to its life, not to time**: its row at a receiver is deleted only when the
    sender's record is deleted (`root.Delete()`) or the receiver's is. Deleting a record (`daserver_delete_actor`)
    marks the actor's rows at every receiver `forget_after = now() + MaxAskTimeout + 3 × ClockSkew` in a second
    statement of the same transaction (an index on the sender finds them, so the sender remembers no receivers and
    sends no notices it must see confirmed), and `daserver_once_expire` deletes them then: every attempt the deleted
    actor sent was sent before the deletion, and with the sender's, the store's and the receiver's clocks within
    `ClockSkew` of each other each is older than `MaxAskTimeout + ClockSkew` by then, so none runs as new. Until
    `forget_after` the row and entries stay and in-flight duplicates are still answered (a probe answered "forgotten"
    changes nothing before then). An active receiver also gets a best-effort "forget" on the internal lane, and its
    idle scan range-reads its rows with `forget_after` set every `ProbeAfter`, so one that missed the notice and
    holds no entry still learns it; the notice and the read carry the row's seed and apply only when it matches the
    in-memory row's, so an old deletion's mark never lands on a re-created sender not yet written. A sender deleted and created again under the same key numbers from a higher seed:
    the first change with the higher seed raises `n` to `(seed, 0)` and clears the row's `forget_after` (in memory and
    in the upsert), so the new actor's row does not go at the old deletion's time; a second deletion re-marks it with
    the later time (`GREATEST`).
  - **Deletion is a one-way passage**: `root.Delete()` answers `Outstanding` and deletes nothing while the root owns
    any ask row, in any state (no intent dropped silently); otherwise it tags the root `Deleting`, written with that item
    and releasing its held output. From then on the actor does nothing: the tag sits on the root and every entity of
    its subtree (spawns under a tagged root inherit it), and `IActor<T>` declares `ISystemGroupWithout<Deleting>`, so
    no system, reminder or `[ForEach]` of the type runs on the root or any descendant, and a Deleting actor never sends
    from a system (the cost is one tag per descendant, written once); its pending ordinary `Ask`s are cancelled (late
    answers are dropped), callers holding a `ReplyTo` token from it are not answered and time out (their requests ran, so no status saying
    otherwise is sent), and `NextDue`
    counts only the delete's `WakeAt`; new requests get `Busy` (not stored), one-way mail is dropped and counted (`DropReason.Deleting`,
    `daserver.messages.dropped{reason=deleting}`), only
    `[Once]` repeats are answered from `Unacked`; and it cannot ask or send (doing so after `Delete()` returned `Ok`
    in the same item is a runtime error that reloads the item, undoing the delete, counted in
    `daserver.delete.send_after` and alerted when it repeats, as a reloaded `Replied` item is delivered again; a build
    error, DAS033, on every path where the analyzer sees `Delete()` returned `Ok`, a hint where it cannot tell). The tag carries a framework `WakeAt`, so a crash or an idle unload resumes it through `DueScan`. The record
    goes once the tag's write has landed, `Unacked` is empty (every stored reply acknowledged, probes bounding the
    wait), the root owns no ask row, and `MaxAskTimeout + 2 × ClockSkew` has passed since the later of that write and the
    last `[Once]` admission (an attempt in flight meanwhile meets the root, not a fresh actor): one function, four
    statements in one transaction (the epoch-compared delete of the record, the `forget_after` marking of its rows as a
    sender, the deletion of its ask rows, of which `Outstanding` guarantees none, and the `orphan_after` marking of its
    rows as a receiver). Deleting earlier would turn a request that ran, and
    whose effects went on to a third actor, into a definite `NoActor` on retry, and the sender would compensate. The
    receiver's rows are kept until `orphan_after` because a key can be activated again ( `[ActivatedBy]`): the new
    actor reads the old marks after its claim, so a late attempt the old one already ran gets `Stale` (the payment
    sample's `Open` has exactly that shape); any change to the row clears the mark in memory at once and its next write
    clears it in the store, and the idle scan never drops a row that holds entries or unwritten changes. A Deleting root is not
    migrated out, and a freeze writes it with its tag so the new process continues; a retried deletion, after a commit
    whose acknowledgement was lost, counts as done whether it finds no record or a newer activation's. A request after
    the deletion that never ran here is new to that key: it activates a new actor or gets `NoActor`.
    Rows therefore follow live senders: a flash-sale stock shard keeps one row per order that reached it, deleted when
    the sale's orders are deleted; a player keeps one per `Payment` until the payment's retention ends.
  - **Cumulative acknowledgement, from written state only**: every mail from a sender to a receiver carries `a`, the
    lowest sequence among the sender's **sent** asks to that receiver **in its last written state** (else that state's
    `OnceNext`): a queued ask has no sequence yet (it takes one from `OnceNext` only in the write that sends it, ADR-100),
    so an acknowledgement never passes an ask not yet sent; an ask row deleted in memory whose write is still in flight
    is not settled, since a crash before that write brings it back to re-ask, and a receiver that had pruned it could
    only answer `Stale`. The receiver drops
    entries below `a` and moves `n` up to `a` (exclusive), with no commit (a crash brings older entries back and the next
    acknowledgement clears them). After the item that handled the answer is written, the framework sends one more
    acknowledgement (TCP's delayed ACK) on the internal lane, handled at admission and never through a mailbox, so a
    full mailbox cannot drop it; losing any of them is harmless. A `Stale` answer to a sent ask therefore cannot
    happen; if it does it is counted and alerted and never delivered.
  - **Windows instead of refusals**: each answer advertises how many unacknowledged entries this sender may have
    (`[ActorOptions(OnceWindow)]`, 16; the root's total `[ActorOptions(Unacked)]`, 1,024); the sender holds further
    asks locally (they are persisted already). The receiver pushes window updates (TCP's window update), on the
    internal lane, to the senders it refused (a bounded FIFO, `DaServer:Once:RefusedQueue`, 4,096), in the order it
    refused them, when the window opens (entries clear; for the row limit, when rows are deleted), so a sender on a long
    backoff is not starved by others taking the pool; if an update is lost a queued sender probes the zero window
    every `DaServer:Once:ZeroWindowProbe` (1 s, fixed); a sender that did not fit the queue backs off on
    `RetryAfter`, then `Once:Retry`. The sender side never refuses for its own asks either: they are rows of their own
    (ADR-100), so no number of them can make its record unwritable, and back-pressure is applied only at the root's
    admission of new work (ADR-100). Actors' `[Once]` requests are exempt from the receiver's `MaxMail`: they are bounded
    by `OnceWindow` per sender and the `Unacked` total (the `[Once]` items already in the mailbox count toward it at
    admission, so a request past it gets `WindowFull` at the door and the mailbox holds at most that many), and a full mailbox refusing them could leave two roots asking each
    other (a trade, a guild bank) each refusing the other. One sender cannot fill a receiver.
  - **Probes clear entries whose acknowledgement was lost**: an entry unacknowledged for `DaServer:Once:ProbeAfter`
    (30 s) with no newer mail makes the receiver send `AckQuery` (internal lane). An actor sender's node answers its
    written `a` without activating it (an inactive sender's sent ask rows and `OnceNext` read in one statement, one
    snapshot), probes batched per node so a node crash does not activate
    thousands of senders; a missing record answers "forgotten", and the receiver keeps the row and entries until
    `forget_after`. A client session's gateway answers from its session table; a session it does not hold answers
    "unknown", and the entries go with the session's row (below). Probes carry no time reasoning.
  - **Client sessions are gateway-signed tokens**: `Session(ClientId, session nonce, valid until T, remaining R,
    call-id high-water marks)`, signed with the cluster's signing secret like client tokens; `T` slides on every
    reply and heartbeat to now plus `DaServer:Clients:ResumeWindow` (2 minutes), a new token is signed only at a
    heartbeat or when `T` moved more than a quarter of the window, any gateway refuses a session past `T`, resuming
    needs a valid client token for the same `ClientId`, and `T` never passes that token's expiry.
    - **A single `[Once]` call is resent for at most `ResumeWindow` from its first attempt**, not for the session's
      life: the SDK counts it from the first attempt on a monotonic clock, and every gateway enforces it through the
      token's high-water marks (the largest call id seen by the end of each quarter of `ResumeWindow`, the last five,
      older ones folded into one): a call id's resend deadline is the end of the quarter it first appeared in plus
      `ResumeWindow`, and any gateway refuses an attempt past it and stamps the deadline on every attempt it lets
      through. A call id above every mark of the token presented counts as first seen in the quarter that token was
      signed in (the call cannot be older than the token), so reconnecting elsewhere with an older token only makes
      the deadline earlier, never later. A session kept alive by heartbeats while its resends cannot reach the receiver therefore cannot make a
      call run twice.
    - **A client session's rows are deleted by time** (the only time rule, because the resend horizon is enforced at
      the gateway): `delete_after` is the latest resend deadline the receiver saw for the session's calls plus
      `MaxAskTimeout + ClockSkew`. By then no gateway passes an attempt of any call the receiver saw and the receiver
      drops attempts older than `MaxAskTimeout + ClockSkew`; a call it never saw never ran here.
    - The gateway records the SDK's cumulative acknowledgement from every request and heartbeat, refuses a `[Once]`
      call id below it (plain requests are not checked), tracks at most `Clients:OnceReceiversPerSession` (64)
      receivers holding entries per session, forwards acknowledgements to them (internal lane) and answers their
      probes. The SDK resends `[Once]` requests under the same number until answered or past the call's deadline, and
      maps `Stale` to an unknown outcome; the application never resends one. An app restart is a new session.
  - `wire.manifest` marks every `[Once]` request type (`once`), since adding or removing it changes semantics across
    a rolling upgrade.
- Alternatives:
  - A user-declared key on the request (`[DedupKey]` on a field's type): keys can repeat (a crash reissues numbers), a
    request becomes deduplicated by accident when it happens to carry the type, and it is a second concept beside
    `[Once]`.
  - Forgetting a sender a fixed time after the receiver last saw it (TIME-WAIT counted at the receiver): a later
    attempt can still be in a link window or a frozen world, and arrive as new after the row went.
  - Deleting a client session's rows at its token's end `T`: heartbeats keep `T` moving while resends cannot reach
    the receiver, which still holds the old `T`.
  - Retaining acknowledged entries for a time: caps a receiver's throughput at its capacity divided by the retention.
  - A filter of evicted keys (a Bloom filter): refuses genuinely new operations by false positives.
  - A dedup table beside the records, expired by age: a store lookup before the handler on a cache miss, and an age
    still bounds how long a sender may retry.
  - A sender's persisted list of receivers it must notify on deletion, driven to completion: a second protocol whose
    failure modes (a receiver gone, a notice lost) need their own handling; marking the rows in the deleting
    transaction by an index on the sender is atomic and needs no list.
  - Per-pair sequences: a counter and a persisted mark per (sender, receiver) on the sender too; per-sender numbers
    need one counter and lose nothing.
- Consequences: the draft's §5 (`root.Delete()`), §8 and §11, architecture.md sections 2 and 4 state it; roadmap W6
  builds it (tests `OnceRunsOnceSameSequence`, `StaleSequenceRefused`, `CumulativeAckPrunes`, `LostAckHarmless`,
  `ProbeClearsLostAck`, `ProbeDeletedSenderForgotten`, `WindowHoldsAtSender`, `ZeroWindowProbeOpens`,
  `ForgetOnSenderDestroy`, `DeleteAndForgetAtomic`, `DeletedReceiverRefusesLateAttempt`, `DeleteWaitsForUnackedEmpty`,
  `DeletingRecordResumedAfterCrash`, `DeletingRootRunsNothing`, `SendAfterDeleteReloadsItem`,
  `StaleForgetNoticeIgnoredForRecreatedSender`, `AckNeverSkipsQueuedAsk`, `OnceRequestsExemptFromMaxMail`,
  `AskInLoopIsDAS034Warning`, `RecreatedReceiverKeepsEntriesPastOrphanAfter`, `DeletingTagCoversSubtree`,
  `DeleteRefusedWhileAsking`, `RecreatedSenderOutlivesOldForgetAfter`, `IdleScanLearnsForgetAfter`,
  `RetriedDeleteCountsAsDone`, `ForgottenRowNeverWrittenBack`, `HandoverKeepsUnwrittenRowChanges`,
  `OnceSequenceSurvivesCrashAndHandover`, `ClientSessionAckPrunes`, `GatewayRefusesStaleCallId`,
  `ClientCallResendWindowEnforcedByGateway`, `ClientRowsDeletedAfterLastResendDeadline`, `OnceFromClusterIsDAS032`,
  `OnceWithReplyToIsDAS032`, `OnceFlaggedInManifest`, `AckFromWrittenStateOnly`, `RecreatedSenderNumbersAboveOld`,
  `ShardRowsBounded`, `LateFaultedFromEarlierAttemptIgnored`, `TooOldAttemptDropped`, `WindowUpdatePushedInOrder`,
  `SessionTokenRefusedPastTByAnyGateway`) and BenchmarkDotNet's credit throughput per receiver with windows of 16 at
  a store latency of 1 and 5 ms.

## ADR-100 `AskUntilAnswered`: the request is the intent
- Context: asking until answered across crashes, written by hand, is the same lines in every flow that moves value
  (the recharge, the loot grant, each step of a saga): an intent component with a default on the prefab, a `WakeAt`,
  a system on a wake tag that asks again if nothing is pending, and a `Replied` handler that must tell a value from an
  unknown outcome. Getting that last part wrong duplicates value: a handler that compensates on a re-ask's
  `NotExecuted` releases a drop an earlier attempt may already have granted (the ninth design audit found it in the
  mmo sample). Users should not write the same lines twice (principle 6), and the rule "compensate only on a
  value" should not depend on each handler.
- Decision:
  - `e.AskUntilAnswered(to, in request[, state])` on a saved entity writes **one row** in the root's ask table
    (`daserver_once_asks`: the root's call ordinal, phase `queued`/`sent`/`stopped`, the sequence, NULL while queued,
    target, the asking entity, request bytes, the call's `state` (`ask_state`, given back in `Replied<TReq, TState>`;
    its type follows the persisted-component layout rules, with its `NinoTypeId` in `state_type` so a reload finds the
    `Replied<TReq, TState>` handler; an unknown type is not delivered on this build: the row stays sent and unanswered for a build that knows it, counted and alerted; the upgrade check compares the manifest's `state` lines both ways: a type an older build cannot read makes the release not rollback-safe, a type is removed in two releases, one that stops creating it and a later one that removes it, refused while the check still sees its rows, and the alert covers a rollback past the build that added a type, or a skipped first step, whose remedy is re-releasing a build that knows it), created at, next try, attempt), in the same write as the item's record and joined
    to its compare, and the item commits implicitly, so the sender's state and the intent are written together (a
    transactional outbox: [microservices.io](https://microservices.io/patterns/data/transactional-outbox)). The row
    belongs to the root, so an asking entity destroyed by another path (a drop expiring) does not lose the ask in flight.
    The record holds no ask: one table for every ask, in every state, so no number of asks can make a record
    unwritable and there is no second place an ask can be in. A request and its state are at most
    `DaServer:Once:MaxRequestBytes` (1 MiB) together, checked at the call. **Per-item caps, no write budget**: a
    started item (a handler run, or a system's work on one entity) may add at most `[ActorOptions(AsksPerItem)]` asks
    (16) and `DaServer:Once:BytesPerItem` bytes (1 MiB); the call past either throws, deterministically whatever the
    batching (in a `[Once]` handler a deterministic `Faulted`, like any throw). They bound memory and abuse only: a
    root's write is one transaction whose row statements (ask rows, `OnceSenders` rows) apply only where its record
    landed (joined on the epoch and the new write generation), chunked into as many statements as `StatementBytes`
    needs, so no number of row changes can make a root unwritable, and every item, system and reaction runs. A retry's
    attempt/next-try update goes with the root's next write and the retry does not wait for it, while a first send
    waits for its own write. The move's and the freeze's last write carries every row change. A row answered in a
    write goes only as a deletion: its pending change is dropped, and the delete is a separate statement after the
    upsert (in one statement PostgreSQL would keep the upsert).
  - **The sequence is taken when the ask is sent**: a row moves to `sent` and takes its sequence from `OnceNext` in the
    write that sends it (the item's own write when the window and `OnceInFlight` allow and no earlier row to the same target is queued,
    so an ask waits for one write; else the earlier rows go first, in ordinal order),
    and the attempt leaves once that write lands; a re-send after a crash reuses the stored
    sequence. So the cumulative acknowledgement (ADR-099) is computed from sent rows only and never passes a queued ask.
  - **Sending** goes per destination in ordinal order, as the receiver's window allows, at most
    `[ActorOptions(OnceInFlight)]` (1,024) sent rows per root: a bound on memory and send rate that refuses nothing;
    one destination's asks are re-asked on one timer, in ordinal order. Asks are ordered only among their first sends
    to the same target, not against the root's other mail to it (a queued ask is overtaken by a later `Send`) and not
    across retries: the exception the draft's §1 and ADR-096 name. A row's next try is set by its phase (now for a
    sendable queued row, the zero-window probe or `RetryAfter` while a window is shut, the re-ask time once sent, NULL
    once stopped). An active root holds every sent row's metadata in memory (so its acknowledgement to each receiver
    is exact, even after `OnceInFlight` is lowered) and, per destination, a bounded head of queued rows
    (`DaServer:Once:QueuedHeadPerTarget`, 64, for up to `DaServer:Once:TargetsInMemory`, 256, destinations served
    round-robin), paging the rest in per destination by ordinal; a held destination with nothing sendable (backoff
    or a zero window) gives up its head when destinations not held wait, and destinations not held come in turn by a
    target cursor walking `daserver_once_asks_queued` (the first queued row after the last target paged in, wrapping),
    so a destination that yielded goes to the back. The bound that holds: a slow or stuck destination holds at most
    `OnceWindow` (16) of the root's `OnceInFlight` slots, its sent rows keeping their sequences, so healthy
    destinations keep sending while fewer than `OnceInFlight / OnceWindow` (64) are stuck, past which new first sends
    wait (nothing lost, `DaServerOnceAskOld` alerts), a destination not held waits at most one rotation of the cursor,
    and memory is bounded (E3). The root's queued totals (`QueuedRows`, `QueuedBytes`, a count per held destination)
    live in `RootState`, written with the row changes, so admission is exact after a reload; a destination not held
    counts as having queued rows only while `QueuedRows` exceeds the held destinations' counts. A new ask is inserted already sent only when
    no earlier row to its target is queued in memory or paged out; request and state bytes stay in memory until their
    insert lands, and a sent row then keeps metadata only, its bytes re-read for each re-ask round by the same
    read-ahead range read that serves a destination's next sends, each read bounded by `StatementBytes`; the activation reads the sent rows and the queued heads after its claim (as `OnceSenders` rows are); the move's and the freeze's last write carries every unwritten ask-row change, and the
    rows stay in the store.
  - **The wake**: the root's `NextDue` is the earliest next try among the rows it holds (sent rows, held heads) and,
    while destinations with queued rows are not held, no later than the next rotation (now + `Once:Retry`, doubling
    to a minute over rotations that send nothing), so a crash, an idle unload or a stop with asks queued or sent
    always wakes the root through `DueScan`, and a root left with only stuck work is not woken again at once.
  - **Back-pressure only at admission**: while a root's queued rows pass `[ActorOptions(OnceQueued)]` (1,024) or
    `DaServer:Once:QueuedBytes` (64 MiB), new ordinary requests to it and client `[Once]` requests get `Busy` before they
    run (client sessions are advertised a window of 0). Its systems, reactions (`Leaving`, `Deactivating`,
    `Restarting`, `Resumed`, `[Changed<T>]`), timers, actors' `[Once]` requests and answers always run; the last two
    each settle some root's ask, so roots asking each other always progress. No tag, no stopped system, no missed
    reaction. Growth past the cap comes only from those items, so it is alerted, not refused: written rows are
    bounded by the store, unwritten ones grow in memory while writes fail and land in one write when the store
    returns (every write's client timeout is `statement_timeout` times its statement count). A full outage is bounded
    by the isolation freeze (about `IsolationTimeout` times the ask rate); a data-only outage (membership reads still
    succeed, data writes fail) freezes nothing and grows until `DaServerStoreWritesBehind` (the oldest unsaved change
    past four `CheckpointInterval`s for 5 minutes) is acted on, the stated E3 exception; `DaServerOnceAskOld`, on the age of the oldest ask row not stopped per actor type (from the sweeping
    node's index-only scan), past three `MaxAskTimeout`s for 10 minutes, catches a stuck downstream whether the
    backlog grows or stays flat, and a healthy ramp's young rows never trip it; `DaServerOnceQueuedFull` fires on a
    node holding a root past the queued cap for 10 minutes (unwritten queued rows counted too). A count of rows is a trend only, since it scales with
    traffic. A
    call in a loop of a `Replied` or `[Once]` handler is warning DAS034.
  - Only `[Once]` requests, and `[Once]` server requests only this way (DAS032): a request that is not deduplicated
    would run again on every retry; a deduplicated one sent by a plain `Ask` has nobody to keep asking.
  - The runtime retries link outcomes (`TimedOut`, `Busy`, `NoOwner`, `Unavailable`, `ConnectionLost`) with the same
    number, after `RetryAfter` or `DaServer:Once:Retry` (5 s, doubling to 1 minute, jittered); a zero window is waited
    out (ADR-099).
  - The answer is an ordinary `Replied<TReq[, TState]>`, delivered once, with a value or the receiver's definite
    refusal (`Faulted`, `NotHandled` answering the current attempt); the item that handles it commits implicitly,
    deletes the row, and the framework acknowledges once it is written. If the asking entity is gone the answer still
    deletes the row and is acknowledged, and is dropped, counted. Link outcomes never reach the user, so a handler
    cannot mistake a re-ask's `NotExecuted` for "never ran".
  - The asking entity is recorded by its identity in the record (the root, a child's unique key, or its place in the
    saved tree remapped through `LoadEntitiesInto`), never by a raw entity id, which EasyEcs reuses, so an answer after
    a reload or a migration reaches the right entity or none.
  - `AskUntilAnswered` returns nothing and never fails because of a downstream (only the call past `AsksPerItem`,
    `BytesPerItem` or `MaxRequestBytes` throws, in the calling item). A `Stale` answer, impossible by construction, moves its row to
    `stopped` for reconciliation; the sweeping node (only while it sweeps and its count succeeds, labelled by the
    hosting server type and the actor type) counts stopped rows from the store and exports the cluster gauge, so the
    alert holds across unloads and restarts until it is cleared. `root.Delete()` answers `Outstanding` while the root
    owns any row. An `AskUntilAnswered` on a `Deleting` root is a runtime error.
  - `WakeAt` stays the runtime's durable-time mechanism for entities (`DaServer.Reminders`, the delete check); ask rows
    carry their own next try. There is no wake tag and no user system reacts to a wake.
- Alternatives:
  - Leaving the intent loop to users (component, `WakeAt`, `Woken` system, `IsPending` guard): the same lines in every
    sample, and every `Replied` handler must classify statuses correctly.
  - Asks in flight as saved children in the record and overflow asks as rows of their own: two places for one intent,
    and every seam between them was a defect (a sequence taken at overflow and passed by the acknowledgement, a root
    left with rows and no wake, promotion pushing the record past `MaxRecordBytes`, a subtree tag that stopped
    systems and skipped `Leaving` reactions while full mailboxes refused the requests that would have drained it).
  - Back-pressure by pausing the root's systems and mail: it skips reactions users rely on and can still deadlock
    roots that ask each other; refusing new work at admission does neither.
  - Deferring an item's extra row changes to a later write: inserts written later lose the intent on a crash after
    the item's effects landed, deletes written later run `Replied` again.
  - A per-write budget so a root's record and rows fit one statement: a hard one is not implementable (an item's asks
    are known only as it runs), and a soft one made systems skip a root past it, which loses `[Changed<T>]` and
    `[Removed<T>]` reactions for good (they select rows written since the system's last run) and can starve a busy
    root's systems. One transaction of several statements, each joined to its record's write, removes the reason for
    a budget.
  - Keeping every queued ask row in memory: unbounded under a stuck downstream (E3). Paging queued rows by one
    ordinal across destinations: a slow target fills the head and holds the others, and a new ask checked against
    memory alone overtakes paged-out rows.
  - Alerting on the number of ask rows: it rises with traffic on a healthy ramp and goes flat on a stuck downstream
    once admission refuses; the oldest row's age does neither.
  - `AskUntilAnswered` for any request: retrying a request that is not deduplicated runs it again.
  - Delivering link outcomes to the user as well: the classification mistake the round found.
- Consequences: the draft's §8, §10, §11 and §12 (the samples have no re-ask systems) and
  architecture.md section 2 state it; roadmap W6 builds it (tests `AskUntilAnsweredCommitsIntent`,
  `LinkOutcomeNeverReachesReplied`, `ReaskAfterCrashSameSequence`, `RefusalDeliveredOnce`,
  `AskRowDeletedOnAnswer`, `AskRowsOneTable`, `SeqAssignedAtSend`, `AckNeverSkipsQueuedAsk`,
  `QueuedAsksWakeAfterCrash`, `AdmissionBusyAtQueuedCap`, `OnceRequestsExemptFromMaxMail`, `RootRowChangesSpanStatementsInOneTransaction`,
  `ProbeAnswerSingleSnapshot`, `AskSentInItsOwnWrite`, `AskStateKeptInRow`, `AskRetriesPerDestinationInOrder`,
  `AsksPerItemThrowsDeterministically`, `SlowTargetNeverHoldsOthers`, `StuckTargetsBoundedByWindow`,
  `ManyTargetsPastMemoryAllProgress`, `QueuedTotalsSurviveReload`, `OutageBacklogLandsInOneWrite`,
  `UpgradeCheckComparesStateLines`, `StoreWritesBehindAlerts`, `LoweringOnceInFlightNeverAcksEarly`,
  `AnsweredRowWithPendingRetryDeleted`, `SentInsertNeverOvertakesQueued`, `AskRowsPagedPerTarget`, `UnknownStateTypeWaitsForBuild`,
  `QueuedOnceCountsTowardUnacked`, `ReactionsRunUnderBackPressure`, `AskRowSurvivesStashDestroy`, `OrphanReplyDroppedAckSent`, `PlainAskOfOnceIsDAS032`,
  `AskingEntitySurvivesReload`, `MutualOnceRootsProgress`, `MigrationCarriesPendingAskRows`,
  `StoppedAskingCountedFromStore`) and BenchmarkDotNet's ask throughput per root at a store latency of 1 and 5 ms.
