# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Development order (agreed: one item at a time, each proven before the next)

1. Z2 core hot paths (done): H3, H2, M1 status-only refusals, M6 completion queue, M4 actor pools, M7 per-link chunk
   reuse, local sends without a copy, core allocation tests. M2/M3/M5 move with client connections (G steps 6 and 8);
   H1 comes with N3b, H4 with N8.
2. G0-G2 (step plan below): one message model (ADR-047), feature points and ingress, client connections as a
   built-in feature, Abstractions and Client packages (ADR-048).
3. T2 ECS bots and business isolation (bots use the gateway module).
4. N2 `SendAfter` (the delayed send primitive, per-shard timer wheel), N3 call cycles fail fast (`Chain` header
   extension), N3b no sync-over-async (incl. the await-only `Reply<T>`, which also fixes H1).
5. N8 Nino ref deserialization (H4).
6. D1-D4 graceful shutdown without a store, A-items (Aspire and hosting; A6 uses the `Trace` header extension).
7. C1-C4 cluster hardening (before persistence: fencing assumes correct downing).
8. N1 Contracts / Model / Logic split.
9. N5 persistence (PostgreSQL + in-memory store) with D5-D6 and P1 `DaServer.Testing`, N6 state handoff, N7
   placement affinity with K1-K2 placement strategies and the owner table.
10. V1-V3 versioning and rolling upgrades (before scaling: rolling deploys depend on them).
11. S1-S6 elastic scaling.
12. N9 hot reload.
13. N4 route completeness, N11 generated filters (on the G filter point), K3-K5, open cleanup, quality items. Later
    optional modules: `DaServer.Reminders`, `DaServer.Topics`, leases for strict singletons.
EasyEcs in parallel when DaServer waits on benchmarks: the Friflo gap, comparison tables in the docs.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (INinoBufferWriter)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: messages (`IRequest<TReply>`, `MessageRegistry`), `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013): actor entities with mailbox/turn components, runtime systems, system groups;
      dispatch queue (`DispatchSystem`, `DispatchQueueCapacity`) and per-actor mailboxes (ADR-040)
- [x] Turn-based actors (ADR-014), stateless services
- [x] Typed actor ids (ADR-043): `Actor<TId, TState>`, `Send`/`Ask(id, m)` route by the id type, `Publish` to services
      only
- [x] Fence a node declared dead: its actors are removed without `OnUnload` (ADR-042, from Orleans)
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip of pulses (cards once, ADR-031), seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-handler-type bindings, per-message `Flow.FromClient` access, push, `ClientGroup` broadcast fan-out
      (O(1) membership, `All` is a group, no allocation per broadcast), `SessionClosed` to bound actors, client frame
      limit (ADR-025)
- [x] Runtime-owned sessions: Bind joins the actor's group, `Caller` holds for the turn, a crashed gateway's
      sessions close (ADR-024)
- [x] Balancers and client gateway discovery (power of two choices; clients wait while the cluster forms)
      (ADR-026)
- [x] One ordered link per node direction: replies travel with binds, pushes and events (ADR-022)
- [x] Placement: keys stay with suspect members, a dead node's keys move, unloading actors keep their mail (ADR-023)
- [x] Client: `ClusterClient` (the same messages as servers, session-bound actor messages without an id, DNS
      endpoints over every resolved address, balancer redirect, callbacks inline or queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] One-way and published messages: `Send`, `Publish` to every handling service, gossiped handlers, mailbox
      delivery (ADR-019)
- [x] Backpressure: bounded dispatch queues and actor mailboxes refuse with `Overloaded`; links are never paused
      (ADR-027)
- [x] Deadlines: the caller's remaining time travels with each request; expired work is dropped (ADR-028)
- [ ] Send group broadcasts only to gateways that have members of the group (deferred: ADR-029)
- [x] Resumable node links: a dropped connection replays what the peer did not process (ADR-008)
- [ ] Gateway sessions as ECS entities (session components + flush system) on a gateway shard
- [x] Actors unload when idle (`UnloadAfterIdle`, ADR-033)

## Now: zero GC (agreed; Z2 is step 1 of the development order)

Steady state allocates nothing: per message, call, reply, tick, broadcast, connection, gossip round and timer.

- [x] Z1 Audit every runtime path (src/DaServer, Hosting, generated dispatchers and `Ask` overloads, what samples
      teach): `TaskCompletionSource`/`Task`/timers/`CancellationTokenSource` per operation, unpooled awaitables, async
      state machines, closures and per-call delegates, LINQ, boxed enumerators, `params`, string formatting and
      logging without guards, boxing (structs in object fields), per-operation arrays/lists/records, Nino collection
      and string deserialization per message (N8), gossip/membership rebuilds, reconnect tickets, metrics.
      Findings (audit of this branch, fix order below):
      - H1 user `async ValueTask` handlers use the default builder: one state-machine box per suspended call (the
        samples teach it). Fix: shard-pooled builder through the await-only `Reply<T>` handler type (N3b.4) or an
        analyzer + code fix adding the builder; update samples.
      - H2 `CallContext.AwaitReply` (pooled builder hits its per-thread cache cap; `ConfigureAwait(true)` allocates a
        context continuation per await). Fix: pooled `ReplyWatcher<T>` like `TurnWatcher`.
      - H3 pool caps below in-flight bounds (PendingCall<T> 1024, RelayCall 1024, FrameWriter 4096): bursts allocate
        and drop. Fix: per-shard-thread stacks for PendingCall, caps 64k (configurable) for frames and relays.
      - H4 Nino collections/strings per message (`Motion` list per client, strings in Say/Chat/replies). Fix: N8
        `DeserializeRef` into a per-thread instance for messages with collections; handlers must not keep refs.
      - M1 error paths allocate exactly under overload (interpolated NotHosted/Overloaded strings, `RpcException` per
        timeout). Fix: status-only frames, lazy exception message, non-throwing status results (N3b).
      - M2 `GroupMembers` created/removed per bind and AOI join/leave. Fix: pool + lazy removal from the maintain timer.
      - M3 `Task.Delay` + async per lost connection (grace) and boxed tuples per shard on gateway loss. Fix: timer heap
        drained by the maintain timer, cached state objects.
      - M4 per actor load: actor object, `Queue<Envelope>`, two HashSets. Fix: per-shard pools refilled on removal.
      - M5 `ClusterClient` pending table 2^14 (≈917 KB LOH per client). Fix: 256 by default, configurable.
      - M6 dispatch queue overflow `ConcurrentQueue` segments. Fix: second bounded ring for completions/callbacks.
      - M7 resend window chunks exceed the 64 KB pool budget across backlogged links. Fix: per-window free list.
      - M8 string concatenation on hostile client rejects. Fix: constant/precomputed messages.
      - L1-L7 rare (view rebuilds, binding arrays, client sweeper enumerator, metrics tags, reconnects, foreign awaits,
        local Send copy): fix where trivial.
      Allocation tests still missing (they come with the items that change those paths): client Ask through a gateway
      and Push/remote broadcast (G step 6), many suspended `async` handlers (H1, N3b), group and binding churn (M2),
      client callback queue (G step 8), collection messages (N8).
- [x] Z2 Core fixes: H3 (per-shard call stacks, 64k frame/relay pools), H2 (pooled `ReplyWatcher<T>`, 264 -> 0 B per
      reply of a suspended handler; tests build optimized so async state machines are structs as users ship them),
      M1 (status-only refusals, ADR-049), M6 (completions, callbacks and turn ends in their own queue, ADR-027), M4
      (actor objects, mailboxes and session sets pooled per shard, ADR-044; a lifecycle hook that completes
      synchronously no longer registers a continuation: that cost an allocation and a thread-pool hop per load and
      unload), M7 (a resend window recycles up to 16 released chunks), L7 (a local `Send` hands its frame over
      instead of copying it). New allocation tests: calls to another node, service Send/Ask/Publish (local and
      remote), refusals, relayed timeouts, actor churn, resend window reuse, idle nodes. Kept: L1 view rebuilds and
      L5 reconnects (per membership change), L4 metric sampling (a few hundred bytes per collection, only with a
      listener), L6 foreign awaits (a closure per await in a foreign UI-style context). H1 comes with N3b, H4 with N8,
      M2/M3/L2 with G step 6, M5/L3 with G step 8.
- [ ] Z3 Prove it: an allocation test per hot path the audit finds uncovered (0 B per operation after warm-up) and
      BenchmarkDotNet A/B (time and allocated) against the baseline (G step 0).
- [ ] Z4 Keep it: CLAUDE.md rule plus analyzer coverage where a misuse is detectable in user code (e.g.
      `TaskCompletionSource`/`Task.Delay` in handlers and systems: use pooled framework awaitables and `SendAfter`).

## Next: one message model, built-in ingress and client connections (G0-G5, agreed)

One framework (ADR-045, ADR-048): ingress (`DaServer.Ingress`) and client connections (`DaServer.Clients`) are
built-in capabilities on public feature points, off unless configured; business domains live in samples. Load
balancing, TLS, CDN/WAF, Gateway API routes and autoscaling stay with the platform.

- [x] G0 One message model (ADR-047), steps 1-4: `Send`/`Ask`/`Publish` plus addresses (`IActorId` keyed,
      `INodeAddress` pinned, `IGroupAddress` fan-out; scope = `TypeId<TAddr>`, kind a JIT constant per address type);
      `Push`/`Broadcast` are sends to session / group addresses, `JoinGroup`/`LeaveGroup`/`Disconnect` messages to
      them, `BindSession` an `Ask` of `Bind` (removed verbs, no aliases). Wire: `Message`/`Reply`/`Control`, `Target`
      always set, trailers by flag (`Origin` 16 B, call id; `Chain` with N3, `Trace` with A6). Wire break, no rolling
      upgrade across it (pre-1.0). A/B against Z2: see the benchmark table below.
- [ ] G1 Feature points and ingress, step 5, proven by two consumers (client connections and the HTTP plane).
- [ ] G2 Client connections as a built-in feature, packages, tests, docs: steps 6-10.
- [ ] G5 `samples/flashsale` (after N5): HTTP edge with admission and a waiting room (signed queue tokens, release
      paced by capacity), `StockShard` actors keyed (sku, shard) so the turn is the atomic decrement (no lock, no
      Redis), `Order` actors keyed (user, sale) so retries are idempotent, async order pipeline, push or poll for the
      result; AppHost + HTTP load generator; E2E.
- Not in the framework: waiting rooms (virtual queues for human surges). They are policy for one kind of traffic,
  belong at the very edge (CDN/SaaS) to protect origins, and compose from ingress primitives (admission filters,
  token bucket, `SignedToken`, load-aware refusal) plus a queue actor; `samples/flashsale` shows the pattern.

Step plan (each step builds and keeps every test green; logic, docs, CI in separate commits):

0. Baseline BenchmarkDotNet: Rpc, Message, Broadcast (1k/5k x 1/9), Scenario (3k/9k) (done: d676d2a numbers).
1. Wire unification inside core (done): `Target` always set, call-id trailer rewritten per hop, 3 frame kinds,
   receiver dispatch by one `Target` lookup (handler type -> shard, session/group scope -> client connections).
2. Addresses (done): `IAddress`/`INodeAddress`/`IGroupAddress`, `Send(addr, m)`, address-generic generated `Ask`
   overloads, `Push`/`Broadcast` removed; DAS006 checks `[ToClient]` by address kind; asking a session or group and
   a client addressing anything but actors are DAS008.
3. `Origin(ulong Node, long Id)` (done): raw 16-byte trailer, `CallContext(ReplyTo, CallId, Origin)`,
   `ShardObject.Caller` replaces `CallerSession`; the origin is an `INodeAddress` (`Send(Caller, m)`) and `SessionRef`
   converts implicitly from it.
4. Session operations become messages to `SessionRef` (done: `JoinGroup`/`LeaveGroup`/`Disconnect` to a session or
   a group; `BindSession` asks a `Bind`, so ADR-022 ordering holds).
5. Feature points and ingress (`DaServer.Features`, `DaServer.Ingress`); done so far: `NodeFeature` hooks,
   `HandleAddress<TAddr>` with scopes in the card and group fan-out by scope, `IIngress` (raw and typed, origin,
   capped deadline, `IngressLoadLimit` early refusal), admission (`IIngressFilter`, `RateLimited` with a retry hint,
   `TokenBucket`, `ConcurrencyLimit`, `SignedToken` under the reconnect tickets), `TimerWheel<T>` (hashed wheel, one
   owner, O(1), allocation-free; consumers: grace timers in step 6, `SendAfter` in N2), dispatch-stage message filters
   (`MessageFilters.Register<T>` / `Register(IMessageFilter)`, run by generated dispatchers; N11 generates into
   them), the HTTP plane minimum (generated entry point on `WebApplication`, Kestrel on `DaServer:HttpPort`,
   `MapAsk`/`MapSend` through the ingress, statuses to 429/503/504/403 with `Retry-After`, `DaServer-Budget` header;
   one origin id space per node for sessions and HTTP requests); client connections use them. Left for step 6 with
   the client feature: feature options bound from `DaServer:{Section}` and enabled by configuration, metrics
   registration, pulse slots. The plan:
   - `NodeFeature`: Configure, ConfigureShard(World), Start(INodeContext), OnViewChanged, OnMemberDead,
     `Drain(deadline)`, Stop(crashed), a readiness contribution, metrics registration; options bound from
     `DaServer:{Section}`; enabled by configuration.
   - `HandleAddress<TAddr>(IAddressHandler)`: runs on the IO thread with the raw frame, never blocks; hosted address
     scopes gossiped in the card; fixed pulse slots assigned at registration (sessions, in-flight, ...).
   - `INodeContext.Ingress`: `Send`/`Ask` to any address with origin, deadline (capped budget) and a public
     `IReplySink`; a raw pass-through variant (message id + target + payload span: edges never deserialize); one hop
     (the edge picks the owner from its gossip view); `LoadOf(node)` and load-aware early refusal before the network.
   - Admission primitives: ingress filter stage (reject with status + retry-after), `RateLimited` / `Overloaded`
     statuses with a retry hint, an allocation-free token bucket / concurrency limit, `SignedToken` (HMAC with the
     cluster secret: resume tickets, admission tokens, internal HTTP auth).
   - One timer wheel implementation (IO side here, per-shard for N2); `DaServer.Net` (`Connection`, `Listener`,
     batched send) as the transport base.
   - Message filters (`RegisterFilter<T>`, static/generic, ingress and dispatch stages; N11 generates into them).
   - The HTTP plane minimum pulled forward from A1: Kestrel minimal APIs in `DaServer.Hosting`, `MapAsk`/`MapSend`
     helpers, status -> 429/503 + `Retry-After`, deadline header -> budget. Tests with a toy feature and a toy HTTP
     endpoint.
6. Client connections as a feature (`src/DaServer/Clients`, namespace `DaServer.Clients`), enabled by a client port:
   transports (TCP on `DaServer.Net`; WebSocket through the HTTP plane in `DaServer.Hosting`), sessions, bindings,
   `GroupMembers` (M2 pool), address handlers for `SessionRef` and `ClientGroup` (fan-out to the nodes advertising
   the scope), resume tickets on `SignedToken` and grace timers on the timer wheel (M3), `BoundSessions` + filters for
   `SessionClosed`/`SessionResync` (special cases leave `ShardDispatch`), dead-node session close through a system,
   ingress checks (size, `[FromClient]` set, destination, per-session rate limit, origin overwrite, budget cap),
   `ClientOptions` (`DaServer__Clients__Port`, ...), `Drain` (readiness off, leave the directory, staggered move +
   ticket, close the listener). The balancer role goes: every client-facing node answers "which node" (power of two
   over gossiped sessions) as a control op and `GET /clients/endpoint`; the well-known address is the platform LB /
   DNS; redirect mode or behind-LB mode. `ClusterMember` keeps no client fields (they are feature card data).
7. Attributes `[FromClient(AnyId)]` / `[ToClient]` replace `Flow`/`AnyId` on `[Message]`.
8. Packages: `DaServer.Abstractions` (netstandard2.1 + net10: `[Message]`, `[FromClient]`/`[ToClient]`,
   `IRequest`, addresses incl. `SessionRef`/`ClientGroup`, `Origin`, `RpcStatus`, `FrameHeader`, wire ids,
   `TypeIds`, `SessionClosed`/`SessionResync`; no runtime, no EasyEcs; `TypeId<T>` static classes, no static
   abstract members), `DaServer` (runtime incl. Ingress and Clients, generator and analyzers bundled), `DaServer.Client`
   (SDK on Abstractions only, `ClusterClient`, M5 smaller table; netstandard2.1 + net10, Unity), `DaServer.Hosting`,
   `DaServer.Aspire` (`AddNodes(...).WithClients(port)` replaces `AddGateways`/`AddBalancer`; `WithClientEndpoints`
   replaces `WithGateways`).
9. Tests: architecture test (`DaServer.Runtime`/`Cluster`/`Rpc` reference no `DaServer.Clients` type;
   Abstractions and Client reference no runtime or EasyEcs); client tests stay in `tests/DaServer.Tests`; A/B against
   step 0 (no regression), allocation tests < 1 B per delivery.
10. Docs: ADR-047/048, design/README/samples/CLAUDE.md, requirements R0/R2/R3/R6/R7/R11 (directory instead of
    balancer), wire break noted; `samples/mmo` drops the balancer project and `Sample.Gateway` becomes a client-facing
    node group in the AppHost.

## Next: graceful shutdown and no data loss

Detection: the Generic Host turns SIGTERM, SIGINT (Ctrl+C) and SIGQUIT into `IHostApplicationLifetime.ApplicationStopping`
and calls `IHostedService.StopAsync` with a token that fires at `HostOptions.ShutdownTimeout` (30 s by default);
after it the process exits whatever is still running. Platforms send SIGTERM and kill later: Kubernetes after
`terminationGracePeriodSeconds` (30 s default), Docker Compose after `stop_grace_period` (10 s default). SIGKILL,
OOM kills, crashes and power loss give no signal at all, so no shutdown code can be the only protection.
Today `NodeHostedService.StopAsync` ignores the token, no time limit is configured anywhere, there is no readiness
signal, gateways drop their clients on stop and nothing is persisted yet (N5).

- [ ] D1 One shutdown budget: `NodeOptions.ShutdownTimeout` (default 25 s) drives `HostOptions.ShutdownTimeout`
      and the published Kubernetes grace period / Compose `stop_grace_period` (budget + margin), so the platform never
      kills a node that is still saving. `StopAsync` honors the token; past the deadline it logs which actors were not
      saved instead of hanging.
- [ ] D2 Ordered stop: readiness off -> Leaving (callers reroute) -> stop accepting new external connections and new
      calls -> finish in-flight turns and calls -> unload every actor (`OnUnload`, parallel, bounded) -> flush the store
      -> close links -> exit. Each phase logged with its duration and exported as a metric.
- [ ] D3 Gateways drain: clients are told to move (resume ticket to another gateway, staggered) before the listener
      closes; the balancer stops directing clients to a leaving gateway.
- [ ] D4 Tests: SIGTERM-style stop under load loses no acknowledged write and no session; a stop that exceeds its
      budget reports the unsaved actors; Kill (crash) loses at most what the persistence policy allows.
- [ ] D5 (with N5) Persistence that survives a crash: per actor type a save policy: on unload (default), write-behind
      with a maximum dirty age (checkpoint every N seconds), and immediate saves for declared critical changes
      (trades, purchases). Versioned saves reject a fenced node's late write (ADR-042).
- [ ] D6 (with N5) Store flush and backpressure: the store's write queue is bounded, flushed in D2 before links close,
      and its health feeds readiness.

## Next: Aspire and hosting best practices (A-items, with D1-D4; checked against Aspire 13.6)

Aspire generates manifests and runs the local cluster; it never autoscales and publishes no grace period, no
Compose health check and no probe except HTTP probes declared with `WithHttpProbe`. What a node needs from it:

- [ ] A1 HTTP plane on ASP.NET Core (ADR-046): every node runs Kestrel on a separate HTTP port (`DaServer__HttpPort`,
      Aspire endpoint `http`) next to its own TCP data plane. Official health checks: `daserver-ready` (joined, not
      leaving/draining, store healthy) mapped to `/health`, `daserver-live` (shard stall watchdog, N3b) tagged `live`
      and mapped to `/alive`, always on (not Development-only like the template). `DaServer.Aspire` declares the
      endpoint, `WithHttpHealthCheck("/health")` (local `WaitFor` and dashboard) and readiness / liveness / startup
      probes (`WithHttpProbe`, ASPIREPROBES001) for Kubernetes. Users map REST endpoints in their node project (admin,
      webhooks, payment callbacks, HTTP login); an endpoint reaches actors only through `ICluster` (Ask/Send post to
      the shard), never shard state.
- [ ] A2 Drain budget published (D1): `WithDrain(grace)` sets the pod `terminationGracePeriodSeconds`, Compose
      `stop_grace_period` and `DaServer__ShutdownTimeout` below it, plus an optional preStop sleep so endpoint removal
      propagates before Leave; stateful groups default longer than stateless ones.
- [ ] A3 Compose gaps: `PublishAsDockerComposeService` sets `healthcheck`, `stop_grace_period` and `restart` per node
      (Aspire writes none and maps `WaitFor` to `service_started`).
- [ ] A4 Publish mode = one workload per node group (with S4): `IsPublishMode` emits one Deployment with `replicas`,
      a headless Service (`clusterIP: None`, `publishNotReadyAddresses` so joining pods gossip), pod-IP advertising
      through the downward API; run mode keeps one resource per replica (ADR-030). HPA v2 / KEDA / PDB via
      `PublishAsKubernetesService` + `AdditionalResources` (PDB and KEDA as small custom resource types).
- [ ] A5 Dashboard: a cluster parent resource (`WithParentRelationship`) showing "n/n joined"; custom states Joining /
      Joined / Draining / Suspected; commands Drain, Membership (JSON), Stats, Unload actor type (admin endpoints on the HTTP
      plane, authorized with the cluster secret). Usable from `aspire resource <node> <command>`,
      MCP and tests.
- [ ] A6 Tracing: an allocation-free `ActivitySource` for cross-node calls (spans only when a listener is attached),
      added to `AddDaServer`'s OpenTelemetry setup with the ASP.NET Core and HttpClient instrumentation, so a REST call
      and the actor calls it causes are one trace. `AddDaServer` is the framework's service defaults (the template's
      content: OTel, health, `ConfigureHttpClientDefaults` with service discovery and resilience for user HttpClients),
      no separate ServiceDefaults project.
- [ ] A7 Store wiring (with N5): `cluster.WithActorStore(postgres.AddDatabase("actors"))` = `WithReference` +
      `WaitFor` on stateful groups; persistent container lifetime in samples; store health feeds readiness (D6).
- [ ] A8 Parameters and environments: shards per node, drain budget, replicas and HPA bounds as AppHost parameters
      (become Helm values per `--environment`); `AddConnectionString` for an existing production database.
- [ ] A9 Tests through Aspire: wait for the cluster resource to be healthy before bots; S6 scale/failover E2E through
      the stop/start/Drain commands and `WithExplicitStart`.
- [ ] A10 Kubernetes packaging: `WithHelm` (namespace, release and chart names, version) on the cluster's
      environment; `AddHelmChart` installs what HPA on custom metrics needs (KEDA or Prometheus Adapter) next to the
      app; `WithNodePool` pins stateful groups to their node pool.
- [ ] A11 Pipeline step (`aspire do`): run the N4 route-completeness check and the analyzers before `publish`/`deploy`,
      so a cluster with an unreachable message or service never ships.
- [ ] A12 Operator docs: `aspire describe/ps/logs/otel`, `aspire resource <node> drain`, and the Aspire MCP server
      (`list_resources`, logs, traces, `execute_resource_command`) to operate and debug a cluster.
- [ ] A13 Seeds through Aspire's service discovery format: nodes also read `services__<group>__tcp__N` (what
      `WithReference(group)` writes), so the AppHost wiring is standard Aspire and our env callbacks shrink. Gossip
      stays the membership layer (who is alive, suspected, leaving, which types each node hosts, load); service
      discovery only bootstraps it.
- Not for node membership: `WithReference` service discovery alone (one load-balanced address, not membership), persistent project
  lifetimes (no replicas, fixed ports); fine for databases and user HTTP services.
- [ ] A14 Production views: the published dashboard (on by default in Kubernetes/Compose environments) is kept for
      diagnosis behind its login; durable views ship with the framework: DaServer metrics (members, shards, mailbox
      backlog, call latency, drain phases) with a Grafana dashboard JSON and alert rules, the cluster view from the
      admin `Membership` endpoint, orchestration state from Kubernetes itself (kubectl/k9s/Helm/cloud console).
- Notes: the Aspire dashboard in deployments is diagnostic only (production observability is OTLP); Azure Container
  Apps exposes ingress, not per-replica addresses, so it is a later target than Kubernetes.

## Next: cluster hardening (C, before N5)
- [ ] C1 Failure detection robust to GC/CPU pauses: SWIM indirect probes (ping-req through k members) or a phi-accrual
      detector over pulse arrivals; suspicion before death.
- [ ] C2 Split brain: a downing rule in core (keep-majority / static quorum: the minority side stops itself), plus an
      optional authoritative lease store (Kubernetes Lease, PostgreSQL) as a module for strict ownership.
- [ ] C3 Node authentication: HMAC challenge in Hello with the cluster secret, cluster id and protocol version in
      Hello; a process that only reaches the port can no longer join.
- [ ] C4 Supervision: per actor type `OnError` policy (Resume default, Reload = unload and reload state); poison
      message counter and metric.

## Next: versioning and rolling upgrades (V, before S)
- [ ] V1 Generated wire manifest (message ids, field layout) committed with a baseline test: fields are append-only,
      removals and reorders fail the build; check Nino's handling of missing trailing fields.
- [ ] V2 Protocol and build version in Hello and in the card.
- [ ] V3 During a rolling upgrade route only to nodes that understand the message (manifest per version in the view).

## Next: packages and other core gaps (P, K)
- [ ] P1 `DaServer.Testing` (with N5): the in-process cluster as a public package, `TimeProvider` and deterministic
      shard stepping, so users test their own actors and systems. (`DaServer.Abstractions` is G step 8.)
- [ ] K1 Pluggable placement strategy per actor type: rendezvous hash (default), prefer caller node, load aware,
      sticky (S3).
- [ ] K2 Owner table per handler type rebuilt on view change (jump/Maglev hash) instead of O(nodes) rendezvous per send.
- [ ] K3 Load-aware service choice (power of two over the pulse load).
- [ ] K4 Per-call options (`CallOptions` struct overload: timeout), the turn's deadline readable in handlers without
      a `CancellationTokenSource`.
- [ ] K5 Per actor type mailbox capacity and a `messages.dropped{reason}` metric (replies and completions have their
      own queue, ADR-027).
- Out of scope in core: durable streams (use Kafka/NATS from a service), transactions (a turn is atomic; sagas are
  messages plus persistence), distributed locks (an actor is the lock), multi-tenancy, event sourcing (documented
  pattern only).

## Next: elastic scaling (after N5/N6: moving stateful actors needs persistence and handoff)

DaServer does not write an autoscaler. The platform decides replica counts (Kubernetes HPA, KEDA, Azure Container
Apps scale rules); Aspire only generates the manifests. The framework's job is that adding or removing a node of any
kind is safe and cheap, that it exports the signals to scale on, and that the generated manifests are correct.

What is stateful is decided by code, not by a role: a node hosting actors is stateful, a node hosting only services is
stateless. In the MMO sample the balancer and the login server are stateless; gateways hold connections (soft state:
clients resume on another gateway with their ticket); the game server (players, realm) and the map servers are
stateful, maps being heavy actors.

- [ ] S1 Scale signals: per-node OpenTelemetry metrics fit for HPA/KEDA (dispatch queue depth, mailbox backlog, tick
      overrun, loaded actors per type, connections, CPU), documented as scaling metrics.
- [ ] S2 Scale-in is a drain: SIGTERM -> graceful Leave -> actors hand off (N6) or unload and save (N5) -> exit within
      the termination grace period; readiness turns false first. Gateways drain by directing their clients to other
      gateways over time (resume tickets) instead of dropping them.
- [ ] S3 Scale-out moves little: a joining node takes only the keys rendezvous hashing gives it (~1/N); per actor type
      a move policy: light actors move at once (handoff), heavy actors (maps) stay where they are loaded and only new
      keys go to the new node (lease/sticky placement for declared heavy types), so a scale-out never moves a map
      with players in it.
- [ ] S4 Generated manifests: `DaServer.Aspire` emits, per node group, HPA (or KEDA `ScaledObject`) with min/max and the
      S1 metric, a PodDisruptionBudget (stateful groups: one node at a time), termination grace period and probes,
      scale-down stabilization for stateful groups; through Aspire's Kubernetes publisher (generated resources can be
      customized and extra resources such as `HorizontalPodAutoscaler` added). Plain Deployments: identity comes from
      gossip incarnations and state from the store, so no StatefulSet is needed.
- [ ] S5 Vertical scaling: shards follow the CPU limit (cgroup-aware core count). VPA in recommendation mode for
      stateful groups (applying it restarts pods, i.e. a drain and a rejoin); automatic VPA only for stateless groups.
- [ ] S6 Prove it: E2E that scales a stateful group out and in under load (10k bots) with no lost state, no dropped
      session and bounded latency spikes.

## Next: architecture borrowed from RandomGame, ET and Orleans (agreed scope; order: the development order above)

Patterns only, never business code. Each item: tests, BenchmarkDotNet where a hot path changes, an ADR, logic and docs
in separate commits.

- [ ] T2 ECS bots (agreed): `Sample.Bot` and the E2E/benchmarks drive bots as entities on a client world: components
      for input and stats, `[ForEach]` systems on a tick for behaviour, `[Handle]` for pushes, systems shared with the
      server where the logic is the same (dead reckoning); no imperative loops.
      Business isolation: Contracts (messages, id types: everyone) / Shared (pure rules and ECS systems over plain
      components: server, real client and bots; may not reference DaServer.Runtime or server projects, enforced by an
      analyzer rule) / Server (authority, storage, other players' data: never visible to bots). Bots talk to the
      cluster only through gateways with `FromClient` messages, like players; unit tests exercise Shared systems
      without a cluster; benchmarks load a real cluster with the same bots.
- [ ] N1 Fixed/reloadable split on top of stateless handler types (DAS007, ADR-044): projects Contracts (messages) +
      Model (components, singletons: never reloaded) + Logic (actors, services, systems: reloadable), samples
      included. Prerequisite of N5 and N9. (RandomGame, ET)
- [ ] N2 `SendAfter(delay, ...)`: per-shard timer wheel of serialized frames, cancellable, delivered as ordinary
      mail (turn-based, follows moved keys), zero allocation; replaces `await Task.Delay` in handlers (DAS009 of N3b
      points to it). (ET timers as data, Orleans timers)
- [ ] N3 Call cycles fail fast: a chain id travels with requests (the `Chain` header extension of G0); mail carrying
      the running turn's chain id is answered `CallCycle` at once instead of waiting for the timeout. (Orleans call
      chains)
- [ ] N3b No sync-over-async on shard threads (agreed: all four layers):
      1. DAS009 (error) in actors, services and systems: `.Result`, `.Wait()`, `GetAwaiter().GetResult()`,
         `AsTask().Wait*`, `Thread.Sleep`, `Task.Delay`, `SemaphoreSlim.Wait`, `Monitor.Enter`/`lock` waits; the
         message names the replacement for the context.
      2. Replacements: handlers `await` (a turn: only that actor waits); delays use `SendAfter` (N2, the actor stays
         free); systems use requests as data: `Cluster.Ask(target, request, replyTo: entity)` delivers the reply as a
         `Reply<TReply>` component (or a `CallFailed` tag) on that entity for a later `[ForEach]` system (no
         continuation, zero allocation).
      3. Runtime: reading a pooled call's result before completion throws; a shard stall watchdog (shard thread away
         from its loop longer than a threshold) logs the stack and marks the node degraded in health checks.
      4. `Ask` returns a framework awaitable (`Reply<T>`-style, await-only: no `.Result`/`.Wait`/`AsTask`), whose
         awaiter throws instead of blocking when read before completion; `Cluster.WhenAll(...)` combinators.
- [ ] N4 Route completeness check: one handler type per request / one-way message kind across all node projects,
      a handler for every `FromClient` message; run in tests and at AppHost start. (Orleans compile-time interfaces)
- [ ] N7 Placement affinity: an actor type may declare a placement key (`PlacementKey(key)`), hashed instead of the
      key: same placement key = same node (and shard); zones without topology strings, no directory. (ET addresses)
- [ ] N5 Persistence as ECS: `[Persist]` components of an actor form one record (sections by component wire id);
      `LoadSystem` batches the loads of one wake; `PersistSystem` (cadence group) serializes on the shard into pooled
      buffers, skips unchanged state by hash, hands bytes to a bounded IO queue; `SaveNow` tag for critical changes;
      unload waits for the last write; versioned writes (conflict = fenced). `IActorStore` (LoadMany / SaveMany with
      expected version) is all a backend implements: PostgreSQL (default, Aspire `WithActorStore`) and in-memory
      (tests). Relational business data (accounts, payments, mail, auctions) stays in the user's ORM inside services.
      (RandomGame write-behind, ET serialized components, Orleans IPersistentState + ETags)
- [ ] N6 State handoff on moves: the old owner sends the same bytes to the new owner on the ordered link ahead of the
      forwarded mail; the store is the fallback. (ET transfer, Orleans migration)
- [ ] N8 Collections in messages without allocation: use Nino's own ref deserialization (`[NinoRefDeserialization]`
      factories, deserializing into a reused instance) for messages with arrays/lists instead of a new mechanism.
      (ET pooled messages, as RandomGame does with Nino)
- [ ] N9 Hot reload: code (Logic assembly in a collectible load context; each shard swaps handler registry and
      systems at a wake boundary, recreates actor objects over unchanged components; old turns finish on old code;
      the old context unloads when unused, weak-reference leak check, no forced GC; a changed Model/Contracts refuses
      and needs a rolling deploy) and config (validated snapshot, pinned per shard wake, cluster-wide trigger, version
      in gossip). HTTP endpoints (ADR-046) reload with the Logic assembly: the framework owns a reloadable
      `EndpointDataSource` whose change token swaps the endpoint set (minimal API handlers declared in Logic,
      registered by the generator), in-flight requests finish on the old delegates; the unload test proves no routing
      or System.Text.Json cache keeps the old context alive (JSON through source-generated contexts in Logic). Benchmark
      the hot path before and after. (RandomGame, ET)
- [ ] N11 Generated filters: `[Filter<T>]` with static `Before`/`After` inlined into the dispatchers (rate limits on
      `FromClient`, auth, metrics), nothing for handlers without filters. (Orleans call filters)
- Not adopted: Orleans interface proxies, per-activation Task scheduling, grain directory / repartitioning,
  transactions; ET location server, coroutine locks, class entity trees, global singletons; RandomGame stop-the-world
  reload with forced GC, manual dirty marking. GM commands (N10) are out of scope for now.

## Open cleanup (review rounds)
- [ ] B4 one serialize-into-pooled-frame helper; B5 `DeliverTo(nodeId, frame)` (local or link); B8 one pending-table
      scan; B9 registries; B10 shared hash helpers; B11 sample nits
- [ ] Split `Node` into links and router (the gateway part leaves with G step 6); membership talks to the node
      through an interface; one endpoint parser for seeds and `DaServer__Gateways`
- [ ] ECS misuse: the turn's caller (`Actor.TurnCaller`) as a component
- Kept on purpose: B6 (local replies hand the frame over without a copy), B12 (the shard checks runnable actors after
  tick systems sent local mail)

## Generator / analyzer (`src/DaServer.Generator`)
- [x] Messages instead of contracts (ADR-035): typed `Ask` overloads per request, one dispatcher per `[Handle]`
      method, client message registration, message ids
- [x] Hosting: actor/service factories with their handlers, client handler classes, EasyEcs system registration
- [x] Node entry point generated when a node project has no `Program.cs` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: DAS001-DAS008 (handlers, messages, wire types, id collisions, constructors, message flow
      (ADR-038), stateless actors and services (ADR-044), address kinds (ADR-047))

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), periodic stats line, graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / AddGateways / AddBalancer / WithGateways; compose publishing
      (ADR-015); no configuration files in node projects
- [x] 10k-player scenario: login, game, map, 3 gateways, balancer, 10k bots (Sample.Bot fails on any gap, disconnect or
      p99 over the limit); passes through Aspire (README "Performance")
- [x] E2E in CI: `tests/DaServer.E2E` runs the AppHost with `Aspire.Hosting.Testing` (1000 bots in CI) and
      fails unless the bot exits 0
- [x] Aspire: one resource per replica (members are addressed individually), DNS seeds for headless services
      (ADR-030)

## Release review (this branch)
- [x] Checklist 1-3: 0 warnings, 0 errors; 80 unit/integration tests and the end-to-end test pass (CI green); topology
      names only in the AppHost
- [x] Checklist 4: RPC unchanged within noise by resumable links (BenchmarkDotNet medium job)
- [x] Checklist 5: 10,000 bots, 60 s: E2E OK, 2.4M realm broadcasts, p50 35.1 ms, p99 57.2 ms, no gap or
      disconnect; servers without telemetry: 0 KB/s except business payloads (chat strings) and < 0.5 B per pushed
      message on gateways, no GC (README "Performance")
- [x] Checklist 6: R1-R11 and E1-E10 hold; open items are the unchecked roadmap entries
- [x] Naming review (ADR-032, ADR-040): `OnLoad`/`OnUnload`, `Caller`, balancers, one noun per concept;
      actor state from field initializers, `ActorRef` for systems
- [x] Moving scenario: 10,000 bots in one map (AOI and dead reckoning, ~2.5M positions/s, p99 106 ms); servers
      0–5 KB/s, no GC; scale-out: a gateway started mid-run
      receives the new clients (test + process run)
- [x] One write per connection per batch (ADR-034)
- [x] Message-first model (ADR-035); client send backlog bound 256 KB (a slow client is disconnected and
      resynchronizes on reconnect)
- [x] Auto-reconnect (ADR-036): signed reconnect tickets, `SessionClosed` after a grace period, `SessionResync`,
      Aspire-generated cluster secret
- [x] Lifecycle as tags (ADR-037): turn ends posted by `TurnWatcher` (no polling), `Ready`/`Loading`/`UnloadRequested`/
      `Unloading`/`Removing` with one system each; sample grid as data + systems (`DespawnSystem`, `PlacementSystem`)
- [x] `[Message]` (ADR-038): `[NinoType, Message]` is a message, plain `[NinoType]` is data; `Flow` (`Internal`,
      `FromClient`, `ToClient`) checked at build time (DAS006)
- [x] EasyEcs from NuGet, used as its docs teach (ADR-039): cadence as system groups (`ShardRebalance`,
      `ShardIdleScan`), `UpdateInterval` in samples, `ActorRef` as the only identity, timer queue removed

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard and cross-node actor calls, replies of suspended handlers, call bursts, one-way
      messages, service messages, refusals and relayed timeouts, actor churn, gateway fan-out, client messages through
      a gateway to another node, gossip rounds, resend windows and idle nodes allocate nothing per operation
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), one-way messages (local/remote), group broadcast (1k/5k
      sockets), broadcast to everyone through 3 gateways (3k/9k sockets)
- [x] CI workflow (build, test, end to end; EasyEcs from NuGet)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [x] Record benchmark numbers of this release in the README
- [x] Test: calls in flight to a crashed node fail when it is declared dead
- [ ] Tests: suspect flapping, hostile client frames beyond the size check
