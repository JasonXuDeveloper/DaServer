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
  `daserver.messages.dropped{reason=queue_full|mailbox_full}` (`Node.Dropped`). Node links are
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
- Decision: everything targets `net11.0`; the shared packages (Abstractions, Core, Client) add netstandard2.1 for Unity
  and other clients (ADR-001) and nothing between: no net10.0 build. `global.json` pins the 11.0 SDK (RC2 until GA,
  then the GA band); CI installs it from there. Runtime async (`<Features>runtime-async=on</Features>`, preview) is on
  for every net11.0 build (`Directory.Build.targets`; a project opts out with `UseRuntimeAsync=false`: the
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
  server or keep a net10.0 build of the shared packages (doubles build and test time for users who all deploy one
  runtime; clients that cannot run .NET 11 take netstandard2.1); `ValueTask<T>` under runtime async as the awaitable
  (no faster than `Reply<T>`, 320 B per suspension).
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
  - **What:** an actor's record is `SaveEntities` of its root and its `Persisted` descendants with the persist
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
  - **Writes:** each world writes the roots due at a tick end in one statement per 512 rows over `unnest` arrays, at
    most `DaServer:StoreWritesInFlight` statements in flight per world (roots beyond wait for the next round), every
    row a CAS on `(type_id, key)`, the epoch and `write_gen < :write_gen`; the rows not updated come back as stored. A
    root whose `SaveEntities` throws (a state that does not serialize) is left out of the batch and logged
    (`RecordUnwritable`); the rest of the batch is written. Deactivation, stop and migration also save before they
    destroy: when that throws, an idle deactivation is cancelled, a forced one or a stop gives the write up (the last
    stored record stays, `RecordUnwritable`), and a migration falls back to such a forced deactivation (placement
    already names the target, which activates from the last record); an in-place hot restart leaves the root out of
    the snapshot (destroyed in the frozen world as by fencing, `RecordUnwritable`), so it re-activates from its last
    record and the restart goes on. The write that releases a root's held `[Durable]` output before a migration or a
    freeze takes the same fallback at once, its held output dropped. A refused
    row fences only its actor; a timed-out write is retried with the same bytes and generation, and a row found at its
    own epoch and generation counts as written. `[Durable]` items ride the next write; other changes at most every
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
- Consequences: one statement per 512 changed actors per world per tick end, at most `StoreWritesInFlight` in flight
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
    is Up or Suspect; any other slot keeps its rendezvous owner, so an empty table is ADR-074's behaviour.
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
    (`ITrackChanges`, declared by the generator for `[Persist]` types) so a checkpoint visits only written actors.
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
  interfaces (`IGroupWith<T>`), `world.Descendants(root)` and `LoadEntitiesInto(…, markWritten: false)`.
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
    the SDK re-joining. What a client message may activate, which key fields come from its claims, client `[Durable]`
    requests and where a message without `[To]` lands are ADR-095.
  - **Distribution**: activation epochs claimed with the placement version and fenced writes, a membership lease,
    placeholder roots, records of persistent entities through `Descendants` and `ComponentSet`, migration bytes checked by layout
    hashes under a new epoch, remaining time across processes. Each piece closes a hole a crash, a pause or a
    partition would open:
    - the lease is renewed only by a coordinator ack that echoes the node's own heartbeat counter, timed from the
      send, and checked by every world before its ordered phase, before the store write and before handing frames
      to the links (a node that hears nobody freezes even when the others still hear it); an ack renews only when its
      sender is on the majority side (it heard more than half of max(`DaServer:FormationSize`, the last committed view)
      within `SuspectAfter`, `NodeRestarting` members counted and unheard), and the coordinator's own lease needs acks from a
      majority, so in a symmetric partition the minority freezes before the majority re-places its actors; the next
      member in coordinator order acks once the coordinator is `Suspect`, so a coordinator crash expires nobody's
      lease; peers re-place after max(`DeadAfter`, `SuspectAfter` + lease). `FormationSize` (default 3) is the
      configured size, so a three-member cluster down to two still needs both, and a split of the survivors freezes
      both. Only a cluster configured with `FormationSize = 2` lets a lone member keep renewing; it relies on the epoch
      fence alone, so production runs at least three members of a stateful server type; `FormationSize = 1` is a
      single-node cluster, and `DaServer.Aspire` sets it from the stateful groups' declared replicas;
    - **a view change that removes members commits only with a majority of the last committed view** (the
      coordinator proposes it; a removal a minority side makes locally never commits), and the lease's denominator
      is that last committed view, so a 5-member cluster split 2|3 renews on the 3 side only: neither side can shrink
      the cluster to itself and then count itself a majority (Akka's keep-majority decides over the last agreed
      membership for the same reason);
    - **the coordinator is a function of the view**, replacing ADR-079's "oldest Up member": every member carries an
      admission number, assigned by the coordinator at admission and again at readmission after a hot restart (node
      generations stay), and the coordinator is the member Up, not `NodeRestarting`, with the smallest number. Every
      node computes the same coordinator from the same view, nothing is elected, and a readmitted former coordinator
      is last in line, so a rolling restart moves the role once;
    - taking a key from another owner needs a strictly newer placement version (the coordinator bumps a type's version on every
      placement change, a membership change or a slot-table move (ADR-079); frames carry the highest seen; a
      migration's arrival writes it too), and a claim that displaces a live owner sends it
      `Fence(key, epoch)`, so two activations never overlap until the loser's next write; claims are idempotent, so
      a retried statement or a restart after a migration's arrival does not fence the claimant itself;
    - a move hands off before the target claims (ADR-072's "mail first", kept): mail for a key of a slot the node
      just received parks on an `Arriving` placeholder while the node asks the source (`HandoffQuery`, carrying the
      target's placement version, which the source adopts so it forwards the slot's keys from then on), which answers
      with the migration bytes, "not here" or "postponed" (it keeps the actor and the mail is forwarded to it), so a
      claim never races a live source and the placement version decides only after a source has left;
    - a root's record writes never overlap in one process (the next waits for the one in flight), and the CAS
      compares the epoch and the root's write generation (`write_gen < :write_gen`; every epoch claim resets it to 0,
      an in-place hot restart continues it from the snapshot), so a timed-out statement that lands late never
      overwrites newer state; a failed write is retried with the same bytes and generation, and a row found at its own
      epoch and generation counts as written; a new row's first epoch comes from a sequence, so a key whose
      never-written row was swept never reuses an epoch and a late statement or an old `Fence` cannot match it;
    - generations are max(last seen + 1, Unix ms), and only the lowest-ordinal seed forms a cluster, so a cold restart
      of the whole cluster never reuses a generation and parallel seeds never form two clusters.
  - **Replies, presences and retries are data with stated guarantees** (the table "每条路径保证什么" in the draft's §11):
    - `[Durable]` on an `IRequestTo` makes both ends the framework's, and actors keep running. The asker's waiting
      entity (request, state, call id, settle-by instant) is in its record before the first attempt leaves, and it is
      re-asked with the same call id on transient outcomes and after re-activation (armed from its `WakeAt`, like every
      wake); the receiver answers a repeat from a bounded `ReplyLog` written in the same record write as the reply's
      state. Only the first attempt and the answer are held, until the tick-end write that holds them succeeds; plain
      output leaves at once. A single settling re-ask before the settle-by instant turns a late duplicate into
      `TooLate`, so a call id runs at most once, an outcome the caller saw survives either side's crash, and
      `Expired` means only "unreachable, outcome unknown". Both ends persist (an analyzer error otherwise); a
      `[Durable]` request cannot be cancelled. The mechanics (held output, timing of the settling re-ask, refusals,
      system passes, deactivation and migration) are the draft's §8 and §10, and §11's table "每条路径保证什么" states
      each path's guarantee. Opt-in, since it holds the request and the reply for a tick-end write on each side; other
      requests stay immediate and an unknown outcome stays the asker's to decide. The edges are refusals, not
      surprises: `Ask` refuses a settle-by shorter than the attempt's deadline + `DaServer:ClockSkew` + the write
      latency (`SettleTooShort`, not executed); any transient outcome of the settling re-ask (`Busy`, `NoOwner`, a
      full window) ends `Expired`, never `NotExecuted`; `ReplyLog` is capped in bytes too (`DaServer:ReplyLogBytes`,
      a request past it is answered `Busy`). `Writing`, `WriteNow` and the write generation are fields of the
      framework's `RootState` component, not tags, so a `Reply` that answers a stored token is no structural change.
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
      retries only not-executed refusals on forwarding hops and `[Durable]` asks, under a per-node retry budget
      (gRPC's retry throttling), and joins with jittered back-off; `Busy` carries a retry-after: blind retries of unknown outcomes duplicate
      effects, unbounded ones amplify an outage. Client call ids are per session across reconnects and prefixed by
      `ClientId`; the SDK's counter starts at (Unix ms << 20), so a restarted app's calls stay above the receiver's
      low-water mark, which applies only when the `ReplyLog` lookup misses; `ICluster` derives call ids from external
      idempotency keys.
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
    it from its last record (a reload: the root is reset to its `RootPrefab` copy's values keeping only the key, the
    mail and the faulting call id, the batch's remaining items move into the mail, the root's in-flight write finishes
    before the claim and write completions count only for their own activation epoch, consecutive reloads back off
    like failed activations), since EasyEcs applies changes at once without rollback and a half-applied item must
    never reach a checkpoint; a type without a record continues. There is no setting: the other choice for either
    kind is unsound.
  - **Writes are asynchronous and batched by default**: changes tag roots `Unsaved`; at the end of every tick (after
    every mail batch for actor types without `[TickRate]`) a world writes the roots due in one statement (ADR-068's
    batch over arrays with `unnest`, kept), each row with its own epoch CAS: roots that ran a `[Durable]` item every
    time, other `Unsaved` roots at most every checkpoint interval, deactivations' final writes with them. Graceful exits (deactivation, scale-in, rolling upgrade, hot restart, shutdown) lose nothing
    but the unwritten changes of a root whose state does not serialize, which keeps its last record;
    only a crash loses at most the last checkpoint interval. Arming a hot restart writes every `Unsaved` root once
    while the actors keep running, so a snapshot the new process rejects (another image or boot, the deadline passed,
    the file gone) is crash-equivalent and loses only the changes made between that write and the freeze.
  - **Persistence is per entity kind, not per component type**: entities spawned from a persistent prefab (`[Persist]`
    on the actor type's `RootPrefab` or on a child prefab, `[Prefab<K>, Persist]`) are saved with all their
    components but `[NotSaved]` ones, and carry `Persisted(Root)`; others are not saved. Every entity spawned from a
    prefab carries `FromPrefab(Prefab, Composition)`. The draft's own example forced this: a drop's
    record on the non-ticking `Loot` actor must be restored with its `Prize` and `Position` (and its waiting `[Durable]`
    ask), while the map's visible drops and its avatars carry the same `Position` and must stay out of records;
    per-type persistence (a marker interface on component types) either lost the record's `Position` or dragged every
    avatar into the record. Change tracking is a per-type column in
    EasyEcs whose filter (`ChangedAny<T1..Tn>`) names its types at compile time, so persisted component types declare
    `ITrackChanges` in source, the generator emits the tracked-type list and the filters from those declarations, and
    `Registration` throws at start when a type on a `[Persist]` prefab (the run-time persist set) lacks it; the change
    system runs `ChangedAny` over the tracked types that reach records (`[NotSaved]` types and buffers of `[NotSaved]`
    elements left out, so a buffer emptied every tick does not rewrite every record; balanced calls of 2 to 9 types, a
    single type as `Changed<T>`) filtered by `Persisted`: one visit per changed persistent row, nothing allocated.
    Generated accessors (`entity.X`) of tracked types are `ref readonly` in EasyEcs 4.7, so reading never stamps;
    records are taken outside every pass and job (`SaveEntities` throws inside one). Spawning a persistent entity tags its root
    `Unsaved` directly, and placeholders (`Loading`, `Saving`, `ActivationFailed`) are never checkpointed, so a
    `Reload` cannot write prefab defaults over the last good record. Records evolve by prefab: every load (record, migration bytes,
    snapshot) adds the `[NotSaved]` components (they arrive with the prefab's default) and those the entity's prefab
    gained since its composition (the manifest keeps earlier compositions; components removed at run time stay
    removed), writes the record again when it added persisted ones, skips unknown type ids, counted, and the upgrade
    check refuses a release whose new fields or record types a rollback would drop, unless forced. Ask states are in
    the record but not tracked: written once at `Ask`, which tags the root `Unsaved` itself.
  - **The wake is the runtime's alone**: descendants that must wake an unloaded actor carry a persisted `WakeAt`
    (a durable ask, a reminder); the runtime keeps the root's `NextDue` at their minimum and rewrites `next_due` on
    every activation by `Wake`, so a stale column never wakes an actor twice. One runtime system, `ArmSystem`, arms a
    `Due` from `WakeAt` on load or activation, so a durable re-ask and a reminder wake the same way.
  - **Identity and user singletons are interfaces**: `Account(long Id) : IClaims` with `IClaims : IComponent`, so the
    constraint of `[JoinOnConnect<T>] where T : struct, IClaims` and EasyEcs both see it; no attribute for the
    generator to complete. User singletons are the same: `IConfigTable<TSelf>` (static abstract `Name` and `TryLoad`;
    one cluster-wide current version per table with a store, per node from configuration without one) and
    `IWorldSingleton<TSelf>` (static abstract `Create`).
  - **EasyEcs 4.7 features replace workarounds** (the draft's header): `Mut<T>` with `SetIfChanged` for components
    recomputed every tick, `world.Instantiate` with given components for `root.Spawn`, `ChangedAny`/`RemovedAny`
    (one filter moved with `Since(tick)`) for the change system and replication, `world.Save(output, in exclude)` and
    `Load(input, in keep, markWritten: false)` so user singletons (configuration tables, world singletons) stay out of
    a hot restart's snapshot, `IUniqueIndexed<T>` (`Get`/`TryGet`) for actor and child keys, `TrimExcess`, `Slices` for
    the idle scan, `Descendants(root).AddTo` for collecting records and migrations, O(1) `Find(key).Count`,
    `Children(parent).Count` and `ValueIndex.KeyCount` for interest checks and the inspector, and `SystemStats` and
    `DescribeSchedule` for the inspector. Tracking is declared in source, so no run-time switch is needed.
  - One project may define several server types; a node runs one.
- Alternatives: keep ADR-081's handlers and add the missing pieces (copy and write-back stays, two vocabularies stay);
  handlers as systems over message entities (an entity per message costs a create and a destroy; the runtime's due
  heap and chain stack give the same order without one); optimistic execution with rollback (state copies cost as
  much as a ~1 µs event, pushes cannot be taken back); spatial parallelism inside an actor (needs game concepts in the
  framework, ADR-045); deduplicating every request (a store lookup on every request; `[Durable]` requests get it on
  the write they already force); user-written reservations, re-asks and call rings for requests that must land once
  (every such feature repeats the same components, and a crash between the ask and the asker's next write still
  promised the same thing twice); `Leave()` at once on a lost connection (a
  reconnect storm rebuilds every presence); every reply durable (a store write of latency on every request);
  persistence per component type (above); `Expired` straight at the deadline (a duplicate still in a resend window
  could run after the asker gave up); a lease renewed by any ack (in a symmetric partition both sides keep
  renewing); a target claiming a moved key at once and letting the store decide (a claim races a source still
  running the actor); `UpdateInterval` for time-gated systems (it counts updates, and a world without `[TickRate]`
  updates only on mail); pausing the actor until its durable write lands (a
  ticking actor stalls with every presence on it); a durable root written alone (a statement per root where one per
  world serves them all); checkpoints sliced across the interval (several
  statements per interval where one serves the world); a separate log beside the records (Akka Persistence's journal
  plus snapshots: batching records already writes many actors in one statement, and a journal adds compaction, replay
  and fenced appends for little gain at our record sizes; revisited only if BenchmarkDotNet shows record size
  dominating the write); holding all later output behind a durable item (a FIFO on the root
  with a write sequence in the CAS; special cases everywhere: held output travelling in placeholders, migration bytes
  and snapshots, released after the receiver's first write, kept across `Reload`).
- Consequences: breaking for every user project and most of the runtime (roadmap phase W). The timer defaults are one
  table in the draft's §11 (lease 6 s, `SuspectAfter` 3 s, `DeadAfter` and re-placement 10 s, link silence 3 s):
  `SuspectAfter`, `DeadAfter` and the link timeout keep ADR-053's and ADR-064's values; ADR-023's "keys move when a
  member is Dead" becomes re-placement after max(`DeadAfter`, `SuspectAfter` + lease), the lease ack echoes ADR-031's
  heartbeat counter, and a hot restart past its deadline counts as Left, not Dead, for ADR-064's keep-majority;
  those ADRs are rewritten when W10 promotes the drafts, together with ADR-068 (a claim without its `INSERT`
  branch for mail that may not create a key, the `CanCreate` gate, no rows for types that persist nothing, the
  never-written rows' sweep), ADR-072 (the handoff query before a claim, carrying the target's version) and ADR-079 (the
  coordinator by admission number, removals committed by a majority of the last committed view). Business code is
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
  - **One mechanism on every platform: the snapshot file** (ADR-088). The worlds freeze between two ticks while IO
    keeps receiving; the process writes the worlds, the queued mail and its link state into a checksummed file under
    `DaServer:SnapshotPath` and exits; the platform's own supervisor restarts it with the new build; the new process
    loads the file.
  - **Identity and epochs are kept**: the new process reconnects as the same (name, generation), and peers resume its
    streams from their resend windows, as after any dropped link. Activation epochs live in the roots and in the
    store, so nothing re-activates and nothing is fenced.
  - **`NodeRestarting` (the membership state) with a deadline kept by the coordinator**: peers keep the node in the view and in placement and
    hold messages in their bounded resend windows until the coordinator (ADR-079) declares it gone; ADR-088 says when
    the restart starts, how the new process rejoins and what the coordinator guards meanwhile.
  - **Fallbacks when layouts change**: records that stay readable take the path through records; changes of hosting
    or client routes take a rolling upgrade. Gateways always roll, surge then drain with `Retire` (ADR-026). The
    upgrade check also refuses a release whose records the running build could not keep (no safe rollback) unless
    forced, and load moves wait until the stateful groups have rolled (moves between builds fall back to records).
- Evidence: moving a world costs milliseconds and allocates nothing. BenchmarkDotNet ShortRun, EasyEcs 4.5.0,
  .NET 11 RC2, one world with 5 unmanaged components per entity: 10,000 entities `Save` 44 µs, `Load` 183 µs,
  440 KB; 100,000 entities `Save` 0.76 ms, `Load` 2.7 ms, 4.4 MB; 0 B allocated in both. The pause is therefore
  process start and reconnects: the target is hundreds of milliseconds, measured end to end in roadmap W9.
- Alternatives: a collectible `AssemblyLoadContext` with `RemoveTypes` and a dispatch-table swap between ticks (JIT
  only, unload leaks, a Logic project and its cycle, swaps the logic only); re-activation from records on every
  change (a checkpoint interval of state lost, a load storm); handing the listening sockets to the new process
  (`SCM_RIGHTS`, Envoy's hot restart: Unix only; a possible later optimization for gateways); `exec` into the new
  binary (no Windows equivalent); a warm successor handed the world over a local pipe (ADR-088).
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
  - The new process loads with `world.Load(bytes, in keep, markWritten: false)` (EasyEcs D39), so no actor rewrites
    its record and no `[Changed<T>]` method sees the loaded rows; a system that needs a full pass filters `Resumed`.
    User singletons (configuration tables, world singletons) are the new process's own: the old one saves without them
    and the new one loads the cluster's current tables and creates its world singletons before the load, which keeps
    them (`Save(output, in exclude)` and the `keep` set of singletons). The snapshot records its world count, and the new process
    creates that many worlds whatever its CPU share; only a configuration pinning another count goes through records.

## ADR-085 Actor-hosting groups publish as StatefulSets with `OnDelete`; hot restart patches pods in place
- Context: ADR-030 publishes every node group as a Deployment: a node's identity was its random id and its actors'
  state lived in the store, so pods needed no stable names. Hot restart (ADR-084) keeps a node's identity (name and
  generation) and its in-memory world across a code change; on Kubernetes the process is restarted by the kubelet,
  so the snapshot has to survive in the pod and the pod must not be replaced. A Deployment replaces pods on every
  template change (new names, new IPs, empty volumes).
- Decision:
  - **Node groups that host actors publish as a StatefulSet with `updateStrategy: OnDelete`**: pods get stable names
    (`{group}-{ordinal}`, the node name, `Environment.MachineName`), and a template change replaces no pod ("the
    StatefulSet controller will not automatically update the Pods"; pods are replaced only when deleted). The
    headless `{group}-peers` Service stays (seeds, `publishNotReadyAddresses`) and is the StatefulSet's `serviceName`.
    Gateways (surge then drain) and stateless groups stay Deployments.
  - **Hot restart on Kubernetes**: the release itself (`helm upgrade` of the published chart) sets the StatefulSet's
    template image; with `OnDelete` that replaces no pod, and pods created later get the new build. The rollout then
    runs as DaServer's own in-cluster Job with a namespaced Role (its verbs: [drafts/deployment.md](drafts/deployment.md)
    section 5): it first has every node pull the new image (a pod with minimal requests on each node, so the pull is not inside
    the pause; a node that refuses it for lack of room is a warning, and that node pulls in its pause), then, one pod at a
    time, arms the node, patches the running pod's `spec.containers[].image` and waits for it to be ready. Only the
    image can change on a running pod, so before patching the Job compares the ControllerRevision the pod was created
    from (its `controller-revision-hash` label) with the StatefulSet's `updateRevision`, revision to revision and
    never the live pod (which a VPA, the hostname and default tolerations change); a pod whose revision differs in
    more than the image (env, probes, volumes, resources, the grace period) is deleted and recreated one at a time
    instead, and the Job says so. It never patches the template (the next `helm upgrade` would put the old image back). That field is mutable on a running
    Pod; the kubelet restarts the container inside the same pod (same pod, UID and IP; the Pod is not rescheduled),
    and an `emptyDir` survives container restarts. The snapshot directory is an `emptyDir` with `medium: Memory`
    (tmpfs; its bytes count against the container's memory limit).
  - **A same-image roll** (a config change on an `OnDelete` group) has no image to patch: the Job arms with
    `/admin/restart?exit=true`, the node runs hot-restart step 1 itself and exits, and the kubelet restarts the
    container in the same pod (`restartPolicy: Always`), re-reading its env and `envFrom`. The kubelet backs off
    restarts of an unchanged container that exited within 10 minutes of its start (10 s, then doubling, whatever the
    exit code), so the Job never restarts one pod twice within 10 minutes by any path (an image patch counts: the kubelet's
    back-off is per container, not per spec) and the coordinator's restart deadline covers the first 10 s step. Secret rotation needs no roll: nodes
    reload the mounted key files (the Secret mounted whole, without `items`, read through a polling file provider every 4 s,
    since file events miss the Secret volume's symlink swap; a reload that reads an empty or short key keeps the old
    keys) and report a key fingerprint, which the rotation step waits for
    on every node before the next step ([drafts/deployment.md](drafts/deployment.md) section 4).
  - **The image is pinned by digest**: the rollout resolves the release's reference to `image@sha256:…` once when
    the roll starts, patches that, and compares each pod's `imageID` before and after, so a mutable tag never
    restarts nothing or runs a stale cached image. The StatefulSet's `serviceName`, `podManagementPolicy`,
    `selector` and `volumeClaimTemplates` are frozen from the first release (Kubernetes refuses to change them), and
    a publish test compares them with the previous chart.
  - **One rollout loop, one adapter per platform**: pre-pull, arm, trigger, wait until ready, next, the coordinator
    last ([drafts/deployment.md](drafts/deployment.md) section 5); on Kubernetes the trigger is the image patch, and
    the Job writes through Server-Side Apply as its own field manager `daserver-rollout`: the pod's container image,
    and a pause of the group's scale-in while it runs, so an autoscaler never deletes an armed pod and its snapshot
    (ADR-094). It derives the admin token from the mounted signing secret.
  - **preStop runs on an image-patch restart too** (the kubelet's container kill runs it whatever the reason; proved
    on kind in W11), so hot-restart groups have no preStop `sleep`: it would add to every pause.
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
  against the memory limit). No page says in one sentence that an image patch restarts the container in place; the
  Pod page implies it (the `ImageID` line above), and the W11 E2E proves it on a real cluster (kind).
- Alternatives: Deployments with in-place pod patches (a later template change still rolls every pod, and pod names
  change on any replacement); `RollingUpdate` StatefulSets (the controller replaces each pod on a template change,
  emptying its `emptyDir`); a persistent volume for the snapshot (slower, zone-bound, outlives the snapshot's use);
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
    (netstandard2.1, net11.0: the client-facing contracts and the wire and transport; carries the generator, the
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
  then. A Unity client references `DaServer.Client` (Core comes with it); a Protocol project references
  `DaServer.Core`; a node references `DaServer`; tests reference `DaServer.Testing`. Today's `DaServer:Shards`,
  `map_shards` and the `daserver.shard.*` metrics are renamed in W2.

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
    `AddDaServer` calls neither. A same-image roll ends the process on purpose, so the supervisor must start it
    again whatever the exit: the systemd unit sets `Restart=always` (`RestartSec=1`); on Windows the node ends such a
    restart with exit code 75 (`DaServer.ExitCodes.RestartRequested`), set as `ServiceBase.ExitCode` on the
    `WindowsServiceLifetime` before `StopApplication` (the service manager reads only that), and the documented install sets
    `sc failure <svc> reset= 0 actions= restart/1000` and `sc failureflag <svc> 1`, since the service manager runs
    recovery actions only for a non-zero exit. launchd needs no code: its plist (`ThrottleInterval` 1, so a restart
    is not held back 10 s) sets the working directory and the environment
    (console format, `DaServer__SnapshotPath`), and the Generic Host already stops on SIGTERM. DaServer documents the
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

## ADR-088 Hot restart is one mechanism; user behaviour before it is data
- Context: a hot restart's bytes can reach the new process two ways: a warm successor launched by the node and fed
  over a local pipe, or a snapshot file, which every supervisor that watches only the process it started
  (Kubernetes, Compose, launchd, Windows services, local Aspire) needs anyway. The warm path needs standby peer links
  beside the live ones, incarnation numbers on the wire, a handoff token, a commit protocol between two live
  processes and rules for picking the transport, and gains a few hundred milliseconds only on systemd and bare
  processes. Games also need a say in what happens before the pause (an MMO tells its players; a sale lets messages
  wait); a one-shot `[ForEach]` gated by hand-made data is a recipe rather than a model.
- Decision:
  - **The snapshot file on every platform**: Kubernetes (a memory `emptyDir`, the pod image patch, ADR-085), Compose
    (a named volume per node, made writable for the node's uid by a one-shot `snapshot-init` service running the node
    image in `--DaServer:PrepareSnapshotDir` mode, and `docker compose up -d --no-deps --force-recreate <node>` as
    the trigger (the deploy itself runs `up -d --no-recreate` first, so a new image recreates no running node before
    the loop arms it), since without `--force-recreate` a same-image roll recreates nothing; the admin calls run in a
    `docker compose run --rm --no-deps rollout` container on the internal network), systemd (`RuntimeDirectory=` with `RuntimeDirectoryPreserve=restart`, `systemctl
    restart`), launchd and Windows services (a local directory, `launchctl kill SIGTERM` with `KeepAlive`, `Restart-Service`) and local
    Aspire (a temp directory per node, the resource restart command). `DaServer:SnapshotPath` names the directory;
    a plain file write, no memory-mapped file for a few MB. The node never launches its successor.
  - **Peers see the same identity reconnect**: (name, generation) on the link, the streams resumed from the resend
    windows with the byte counts in the snapshot: today's resume machinery, no second link.
  - **Behaviour before and after is data**: the last tick before the freeze every root carries
    `Restarting(TimeSpan ExpectedPause)`, and user systems filter `[With<Restarting>]` (Settling removes it in the
    same tick, so no snapshot holds it); the first tick after the load every root carries
    `Resumed(TimeSpan Pause, bool Aborted)` with the measured pause, and an aborted restart raises
    `Resumed(0, Aborted: true)` so systems can undo. With no system nothing happens and messages wait. Gateways see the
    restarting nodes in the `RestartingNodes` singleton.
  - **Arming only prepares; the platform's stop starts it**: `/admin/restart` is the one admin verb (`mode=snapshot`
    or `unload`, `exit=true` for a same-image roll, `dryRun=true` to report the path the manifest diff picks without
    arming). The node freezes only after the coordinator acks its `NodeRestarting`, so the deadline starts at the
    freeze and an arm the platform never follows expires silently. Arming is refused while a member joins or leaves,
    while another node holds the cluster-wide restart permit, or when the snapshot would not fit the memory limit;
    the rollout restarts the coordinator last. A freeze that cannot finish within its bound stops gracefully through
    records. Arming writes every `Unsaved` root once while the actors run, so the snapshot carries only the changes
    since; the freeze writes nothing, since a short pause matters more.
  - **A snapshot is used once, by the right binary, and only with the coordinator's consent**: its header names the
    arm nonce, the manifest, the layout hashes and the boot; the new process loads it once and waits in `Rejoining`
    for `Readmit` or `Gone`, the only place the deadline is judged. Meanwhile the coordinator pins the restarting
    node's slots and moves nothing into or out of it; an expired restart counts as Left, not Dead, for keep-majority
    (ADR-064), and a peer that sees a Hello from the restarted identity resets its dial back-off. A snapshot the new
    process rejects (another boot or image, the deadline passed, the file gone after a reschedule) is
    crash-equivalent: its actors re-activate from their records, losing what changed after the arm's write. The
    steps, checks and their tests are the draft's §10.
- Alternatives: the warm handoff above (a second transport, standby links, incarnations and a token for a gain on two
  platforms); OpenKruise for Kubernetes (ADR-085); a virtual "before restart" hook on actors (a callback where a tag
  and a system do).
- Consequences: one code path to test (W6: snapshot written, loaded once, stale, torn or from another image,
  deadline passed, freeze aborted; W11: the rollout's order and a Job killed between arm and patch); the pause
  target is hundreds of milliseconds everywhere (ADR-084's evidence). Without `DaServer:SnapshotPath` a node has no
  in-place path and takes the path through records. The node's HTTP plane is down during the pause; `ICluster`
  callers outside see it unavailable and retry on their side.

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
    whose next firing is its `WakeAt`; one per (T, key) and actor, its unique index keyed by the actor first, so two
    actors in one world keep their own and an actor can hold several of a kind (`root.Remind<T>(at, every, key: 0)` finds
    or spawns it and leaves an existing one with the same period untouched, so calling it on every activation never
    skips an overdue firing; a new period or `move: true` reschedules; `root.CancelReminder<T>(key: 0)` destroys it). The runtime arms it like every wake (its `ArmSystem` turns a `WakeAt` into a `Due` on load and
    activation; while unloaded, `NextDue` goes into the record's `next_due` column and the slot owner's `DueScan`
    activates the actor); the package has no arming system of its own. `Reminded<T>` is at least once, deduplicated by
    (reminder, `UtcAt`): the advance lands in the same record as the handler's effects. After an outage a reminder
    fires once and moves to its first future occurrence, not once per missed period.
  - **The wake is the runtime's, not the package's**: `WakeAt`, `NextDue`, `next_due`, `DueKeys` and `DueScan` belong
    to the runtime because `[Durable]` asks need them too (ADR-082) and the runtime depends on no capability
    (ADR-045). Only the runtime computes `NextDue`; the package writes `WakeAt` on its reminders.
  - **`DaServer.Topics`**: a built-in `Topic(long Id)` actor type with a persisted table of subscribers and their
    activation epochs; `Subscribe`, `Unsubscribe` and `Publish` on `Entity` send the Topic one-way package messages
    (exempt from `[From<…>]`, since any actor type sends them); `Publish` is serialized once and sent as one frame per
    subscriber node. Subscribers renew their own `Subscription` buffer every `DaServer:Topics:RenewInterval`, so a
    crash or a lost message costs at most two intervals of missed publishes (the renewal scans in slices, and a row
    moved to another table during a round is missed once); the receiving node checks that buffer at
    dispatch and answers a stale delivery with `NotSubscribed`, so a lost `Unsubscribe` is pruned at the next publish.
    Delivery is at most once and never activates an actor; `[Stateless]` actor types and `Connection` cannot subscribe
    (DAS023). It replaces today's `Publish`; durable streams (replay, offsets, consumer groups) stay with Kafka or NATS
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
- Consequences: roadmap W6a (Reminders; the wake itself is built in W6 for `[Durable]` asks), W6b (Topics), W7a
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
  - **A saga in `samples/flashsale`**: the order as a process-manager actor driving payment and stock with
    `[Durable]` asks: a step refused as not executed (`NotExecuted`, `TooLate` included) is compensated; a step that
    ends `Expired` (unreachable, outcome unknown) is reconciled by asking its owner again later; with a pattern page in
    the sample on why sagas replace ACID transactions across actors (each step runs at most once per call id, no lock
    or two-phase commit spans actors). The buyer's identity comes from the token, not the message: `PlaceOrder`
    carries only the sale. Only one actor type handles it (a client message has one): today's stateless `Gate`
    service becomes `Gate(long Sale, int Shard)`, an actor type that is not persisted (so it claims no store row),
    with `[ActivatedBy<PlaceOrder>]`, `CanCreate` (the sale exists) and `Route(in Account, in PlaceOrder)` to the shard
    `who.Id % Shards` (`Shards` a constant: `Route` reads no tables, so changing it means a gateway roll): it handles the client's `PlaceOrder`, answers the late crowd `SoldOut` from what it has learned
    (a sold-out answer from any `Order` it asked, or the sale's `Describe`), runs the optional admission queue, and for
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
    root (a `[JoinOnConnect]` type, ADR-095); spending a precious item and buying with premium currency are client
    `[Durable]` requests; a recharge never goes through the client: the payment provider's webhook sends a `[Durable]` `Paid` through
    `ICluster.Ask` to `Payment(payment id)`, a persisted actor whose record is the payment's state; it asks `Player`
    durably to `Credit(payment id, amount)`, and `Player` deduplicates by the persisted set of payment ids it has
    credited, so a payment is credited at most once whatever the call ids; a `Credit` that ends `Expired` is asked
    again with a fresh call id until the answer is definite (credited, or already credited); a guild resets its daily
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
    store, client connections, changes, an actor, audit; and runs the actions (restart a node, hot restart a type on
    its current build, a config table) through the nodes' admin plane, each confirmed by typing its target and
    written to the audit log. Restarting one node calls `/admin/restart?exit=true` on it: a node that hosts actors
    hot restarts in place and the platform starts it again; a gateway or stateless node has no snapshot, so it retires
    (a gateway hands its clients over first) and exits, and the platform starts a fresh one. Hot restarting a type is the same-image roll, and the Dashboard never loops over
    nodes itself (its own restart would leave a half-rolled group, and only the rollout loop pauses scale-in): it calls
    the deploy pipeline's webhook, which runs the one rollout loop (one node at a time, the coordinator last), or shows
    `aspire do {cluster}-restart` without one; a new build, a rollback included, reaches the nodes through the deploy
    (`aspire deploy`, then `{cluster}-restart`), since only the platform changes an image: the Dashboard's roll back
    calls the deploy pipeline's webhook (`DaServer:Dashboard:RollbackWebhook`: GitHub Actions, Argo CD, any CI) with
    the server type, the build to return to, who asked and why, and audits the call; without a webhook it shows the
    command to run. Taking a node out is the platform's scale-down, not a Dashboard action. It mounts the signing secret's
    files and derives the admin token from them (drafts/deployment.md section 4), deriving it again when the files
    change and trying the current key, then the accepted one; it calls no Kubernetes API and has
    no autoscaler and no PodDisruptionBudget (one replica; its cost follows the nodes, not the viewers).
  - **Live state is gathered once per refresh, never per viewer or per request**, so its cost grows with nodes, not
    with people watching. Nodes keep no history: each serves its current readings and its own ranking of its slowest
    actors. When Prometheus is configured the Dashboard reads per-type aggregates and per-node readings with a fixed
    set of queries, and charts come from range queries; otherwise a collector in the Dashboard polls every node's
    stats summary (fixed size) every 10 s, at most 32 nodes at a time, into one in-memory snapshot that every viewer
    reads, and keeps a bounded ring of those polls for the charts (they start when the Dashboard started, and the
    evidence chart says so). Distributions, outliers (a state other than running, or more than four median absolute
    deviations from the type's median) and the cluster's slowest actors (the nodes' rankings merged) are computed on
    the server; a screen receives only the bins and the few rows it shows. Membership comes from one node's `/admin/membership`:
    each member's pulse carries the hash of its view, so that one node also tells when views disagree. An actor's detail is asked on demand of the one node that owns it,
    found through placement; Find resolves actor ids the same way and searches nodes in the snapshot. The snapshot is
    a cache rebuilt at start, so replicas stay interchangeable.
  - **Something is wrong when an alert fires.** `observability/prometheus-rules.yaml` is the one definition: triage
    shows the firing alerts (Prometheus's `ALERTS`, or Alertmanager's API) and uses the outlier rule only to name the
    culprit nodes and the rows that stand out. Without Prometheus, triage covers only the states that need no
    threshold: a stalled shard, a node not ready, views that disagree.
  - **Logs come from Loki.** Nodes export logs over OTLP to the Collector, which sends them to Loki. The node is the
    OpenTelemetry resource's `service.instance.id`, which Loki indexes as a label; framework logs add
    `daserver.actor.type`, `daserver.actor.key` and the trace id as attributes, kept as structured metadata (the actor
    key is never a label: it is unbounded). The Dashboard queries Loki's HTTP API (LogQL `query_range`) for the lines
    of what the screen shows (the incident's server type and time, a node, an actor) and shows them inline; the audit
    log is a log stream of the Dashboard read back the same way.
  - **History is Grafana's.** Each view links to Grafana at the same time range (the shipped dashboard, or Explore
    for logs and traces); there are no ad-hoc queries, no dashboard editor and no long-term storage of the
    Dashboard's own. The Aspire dashboard is for local runs.
  - **Optional sources.** Without Loki the log screens and the audit screen say that Loki is not configured (audit
    entries still go to the Dashboard's stdout); without Grafana the history links are absent.
  - **Sign-in is OIDC**; roles allow viewing or acting.
  - **The front end is vanilla TypeScript built by esbuild**, its assets prebuilt and embedded in the package, so only
    Dashboard contributors need a JavaScript toolchain; the look and motion follow doc/dashboard/design.md.
- Alternatives: Grafana dashboards plus the Aspire dashboard (three sites, no actions, no audit); a plugin for the
  Aspire dashboard (it has no plugin model); our own log store (Loki already indexes OTLP logs at scale); Elasticsearch
  or OpenSearch (heavier to run for what the screens query); Svelte with Vite (a framework for a few screens, slower
  builds).
- Consequences: roadmap W7e builds it; the samples' AppHosts add the Collector, Prometheus, Loki, Tempo and Grafana
  for published runs through the publisher hooks; doc/operations.md describes the telemetry path; framework log calls
  use `LoggerMessage` with those attributes, off the hot paths.

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
    scale-in; a VPA on memory only (`InPlaceOrRecreate` or `Off`), CPU scaling horizontally; PDBs with
    `maxUnavailable: 1` for stateful groups, a percentage for gateways, `unhealthyPodEvictionPolicy: AlwaysAllow`;
    none for the one-replica Dashboard. DA only names the scale target (StatefulSet or Deployment) and makes the
    `Manifest` type public for objects Aspire has no class for.
  - **Field ownership through Server-Side Apply**: the chart (`helm`) owns what the AppHost declares; an autoscaled
    group renders no `replicas` (the hook that adds the autoscaler clears it), so the HPA owns the count; the rollout
    Job applies as field manager `daserver-rollout`, forcing only the pod's container image and, without force, a
    pause of the group's scale-in while it rolls (an armed pod deleted by a scale-in would lose its snapshot). Nodes
    and the Dashboard write nothing to the Kubernetes API.
  - **In-place resize is for memory only**: a node reads its cgroup memory limit and calls `GC.RefreshMemoryLimit()`
    when it changes, so `resizePolicy memory: NotRequired` resizes without a restart; CPU requests set the world count
    only at start (`DOTNET_PROCESSOR_COUNT`), and node pods set no CPU limit.
- Alternatives: rendering `replicas` and having the deploy fight the autoscaler (each deploy resets the size, a mass
  leave for a stateful group); `pod-deletion-cost` to pick which gateway goes (needs API access from someone, and
  rebalancing, ADR-089, already keeps gateways even); StatefulSet `maxUnavailable` (beta) or the `Recreate` strategy
  (alpha); a pre-pull DaemonSet (pulls on every machine); native sidecars (nothing to run beside a node);
  client-side `kubectl apply` (no conflict detection, the annotation's size cap, stale state).
- Consequences: roadmap W11 (scale-target helper, `replicas` cleared with the autoscaler, the samples' HPA/KEDA
  behaviors and PDBs, the rollout's SSA writes and scale-in pause) and the memory-limit refresh step;
  doc/operations.md tells operators the same rules for clusters that predate them.

## ADR-095 Client messages: activation by record, keys from claims, durable requests, several presences
- Context: the first target model let a client `[To]` message activate any key (an invented room number inserted a
  row), trusted every key field the client wrote (`Order(Sale, User)` let one buyer order for another), had no way
  to make a client request land once (a purchase with premium currency, a precious item), allowed
  one presence per actor type (a client in two chat rooms, or a party and a guild, could not exist) and left unsaid
  where a message without `[To]` lands on a `[JoinOnConnect]` actor whose per-player state is on the root.
- Decision (the rules are [drafts/programming-model.md](drafts/programming-model.md) §4, §6, §7 and §8):
  - **A client `[To]` message reaches only keys that have a record**, unless the actor type lists it in
    `[ActivatedBy<TMessage>]` (several allowed), whose handler validates what it creates. The claim then runs without
    its `INSERT` branch and requires a written record, so a made-up key answers `NoActor` and writes nothing. A client
    `[To]` message to a type without `[ActivatedBy]` is analyzer warning DAS024 (suppressible where intended). An
    actor type whose root does not persist has no record, so a client `[To]` message activates it only through
    `[ActivatedBy]` (server messages activate it as usual), and its activation leaves no row in the store.
    `[ActivatedBy]` narrows only first activations: server `[Durable]` requests and `Wake` always activate, while a
    client `[Durable]` request obeys it (a key without a written record never ran a durable request, so `NoActor` is
    final).
  - **A pure gate before the claim**: beside `[ActivatedBy]`, `static bool CanCreate(in TClaims who, in TMessage m)`
    (which may also take `in` configuration tables, the node's immutable snapshots) runs on the owner before the
    claim; `false` turns the claim into the no-insert one, so a key that already has a record still activates (a
    `[Durable]` retry of an order placed before the sale closed finds its answer) and only a key without a record
    answers `NoActor`, writing nothing; validation that needs no actor state (a map id in the map table, a sale that
    exists) costs no row. An analyzer error flags a `CanCreate` whose message is not in `[ActivatedBy]` and a
    `CanCreate`-shaped method under another name. Rows still never written expire after `DaServer:EmptyRecordTtl`
    (1 day): the member with a store and the smallest admission number deletes them in key-set batches through a
    `SECURITY DEFINER` function, and the runtime role has no
    `DELETE` grant.
  - **Part of a key may come from the claims**: the message carries only the fields a client may state, and the
    actor type declares `static K Route(in TClaims who, in TMessage m)`, a pure function the gateway's generated route
    calls without allocating. A message without a matching `Route`, or a `Route` whose identity type is not the
    project's `IClaims`, is an analyzer error when the key has fields the message does not carry.
  - **`[Durable]` on `IFromClient<R>`**: the receiver behaves exactly as for a server `[Durable]` request
    (deduplication by call id, `ReplyLog`, the reply released after the record write, `TooLate`); the client does not
    persist, so the SDK retries with the same call id (prefixed by `ClientId`) after a timeout or `ConnectionLost`.
    Every attempt carries the call's age (monotonic time since its first send), and the gateway sets settle-by =
    arrival − age + `DaServer:ClientSettle` (3 minutes; a node refuses to start when it is below `ClockSkew` +
    `AskTimeout` + 30 s, since the receiver judges a gateway instant by its own clock), so a retry through another
    gateway keeps the first attempt's settle-by; the SDK stops at first send + `ClientSettle` − `AskTimeout`. The
    receiver keeps an entry until the latest settle-by it accepted for the call id, and keeps per `ClientId` the
    highest purged client call id (`DaServer:ReplyLogClients`, 256 per root, evicting only clients idle past
    `ClientSettle` + `ClockSkew`), answering `TooLate` at or below it once the `ReplyLog` lookup has missed (entries
    are purged by settle-by, not by id). The SDK's call-id counter starts at (Unix ms << 20), so an app restarted with
    the same token never starts below that mark. The receiving type must persist (analyzer
    error). For sensitive operations that change the economy; everything else stays at most once. A business
    operation repeated under a new call id (an app restart, a hostile client) is deduplicated by its own key. A
    recharge never goes through the client: the payment provider's webhook asks `Payment(payment id)`, a persisted
    actor with no settle-by limit, which asks `Player` durably to `Credit(payment id, amount)`; `Player` keeps the
    credited payment ids in its record and answers a repeat "already credited", and `Payment` re-asks an `Expired`
    credit under a fresh call id until the answer is definite, so a payment is credited exactly once.
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
  the handler (each handler repeats the check, and a forgotten one is a cross-user write); client requests at most
  once only (every game rebuilds purchase deduplication); a fixed limit of one presence per type (above); handlers on
  the presence for every client message (per-player state would be copied or duplicated onto presences).
- Consequences: the generator emits per-message routes on `Connection`, `[ActivatedBy]` lists and the claim's
  no-insert path in W4/W5; the SDK retries `[Durable]` calls; samples use each (ADR-091: the starter's rooms, the
  flash sale's gate, the mmo's purchases and recharge credit). Tests are listed under W4, W5 and W7.
