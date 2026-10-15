# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (INinoBufferWriter)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: messages (`IRequest<TReply>`, `MessageRegistry`), `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013): actor entities with mailbox/turn components, runtime systems, system groups
- [x] Turn-based actors (ADR-014), stateless services
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip of pulses (cards once, ADR-031), seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-handler-type bindings, per-message `Flow.FromClient` access, push, `ClientGroup` broadcast fan-out
      (O(1) membership, `All` is a group, no allocation per broadcast), `SessionClosed` to bound actors, client frame
      limit (ADR-025)
- [x] Runtime-owned sessions: Bind joins the actor's group, `CallerSession` holds for the turn, a crashed gateway's
      sessions close (ADR-024)
- [x] Balancers and client gateway discovery (power of two choices; clients wait while the cluster forms)
      (ADR-026)
- [x] One ordered link per node direction: replies travel with binds, pushes and events (ADR-022)
- [x] Placement: keys stay with suspect members, a dead node's keys move, unloading actors keep their mail (ADR-023)
- [x] Client: `ClusterClient` (the same messages as servers, keyless session-actor messages, DNS endpoints over every resolved
      address, balancer redirect,
      callbacks inline or queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] Cluster events: `[NinoType]` structs, `Publish(key, e)`, `[Handle]`, gossiped handlers, mailbox delivery (ADR-019)
- [x] Backpressure: bounded shard inbox and actor mailboxes refuse with `Overloaded` (ADR-027)
- [x] Backpressure on links: decided against pausing a link's receive (ADR-027): the link also carries replies to
      the receiver's own calls, so a paused link stalls them until timeouts; full shards keep refusing with `Overloaded`
- [x] Deadlines: the caller's remaining time travels with each request; expired work is dropped (ADR-028)
- [ ] Send group broadcasts only to gateways that have members of the group (deferred: ADR-029)
- [x] Resumable node links: a dropped connection replays what the peer did not process (ADR-008)
- [ ] Placement hints (co-locate actors that talk a lot)
- [ ] Gateway sessions as ECS entities (session components + flush system) on a gateway shard
- [x] Actors unload when idle (`UnloadAfterIdle`, ADR-033)
- [ ] Persistence helpers

## Generator / analyzer (`src/DaServer.Generator`)
- [x] Messages (ADR-035): typed `Ask` overloads per request, one dispatcher per `[Handle]` method, client message
      registration, message ids
- [x] Hosting: actor/service factories with their handlers, client handler classes, EasyEcs system registration
- [x] Node entry point generated when a node project has no `Program.cs` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: DAS001-DAS005 (handlers, messages, wire types, id collisions, constructors)
- [x] Messages instead of contracts: one concept for requests, one-way messages, events and pushes (ADR-035)

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), periodic stats line, graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / AddGateways / AddBalancer / WithGateways; compose publishing
      (ADR-015); no configuration files in node projects
- [x] 10k-player scenario: login, game, 3 gateways, balancer, 10k bots (Sample.Bot fails on any gap, disconnect or
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
- [x] Naming review (ADR-032): `[FromClient]`/`[ToClient]`, `[ActorKey]`, `OnLoad`/`OnUnload`, `CallerSession`,
      balancers; actor state from field initializers, `ActorRef` for systems
- [x] Moving scenarios: 10,000 bots in 200 maps (full sync, ~10M positions/s, p99 139 ms) and in one map (AOI and
      dead reckoning, ~2.5M positions/s, p99 106 ms); servers 0–5 KB/s, no GC; scale-out: a gateway started mid-run
      receives the new clients (test + process run)
- [x] One write per connection per batch (ADR-034)
- [x] Message-first model (ADR-035): contracts, proxies and `[ActorKey]` removed; client send backlog bound 256 KB
      (a slow client is disconnected and resynchronizes on reconnect)
- [x] Auto-reconnect (ADR-036): signed reconnect tickets, `SessionClosed` after a grace period, `SessionResync`,
      Aspire-generated cluster secret
- [x] Lifecycle as tags (ADR-037): turn ends posted by `TurnWatcher` (no polling), `Ready`/`Loading`/`UnloadRequested`/
      `Unloading`/`Removing` with one system each; sample grid as data + systems (`DespawnSystem`, `PlacementSystem`)
- [x] `[Message]` (ADR-038): `[NinoType, Message]` is a message, plain `[NinoType]` is data; `Flow` (`Internal`,
      `FromClient`, `ToClient`) checked at build time (DAS006); `[ClientMessage]` removed
- [x] EasyEcs from NuGet, used as its docs teach (ADR-039): cadence as system groups (`ShardRebalance`,
      `ShardIdleScan`), `UpdateInterval` in samples, `ActorRef` as the only identity, timer queue removed

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard actor calls, keyed events, gateway fan-out and client calls through a gateway to
      another node allocate nothing per message
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), keyed events (local/remote), group broadcast (1k/5k
      sockets), broadcast to everyone through 3 gateways (3k/9k sockets)
- [x] CI workflow (build, test, end to end; EasyEcs from NuGet)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [x] Record benchmark numbers of this release in the README
- [x] Test: calls in flight to a crashed node fail when it is declared dead
- [ ] Tests: suspect flapping, hostile client frames beyond size, link backpressure
