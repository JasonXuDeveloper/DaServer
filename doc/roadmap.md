# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (INinoBufferWriter)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: contract attributes, `RpcRegistry`, `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013): actor entities with mailbox/turn components, runtime systems, system groups
- [x] Turn-based actors (ADR-014), stateless services
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip, seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-contract bindings, per-method `[Client]` access, push, `ClientGroup` broadcast fan-out
      (O(1) membership, `All` is a group, no allocation per broadcast), `SessionClosed` to bound actors, client frame
      limit (ADR-025)
- [x] Runtime-owned sessions: Bind joins the activation's group, `Caller` holds for the turn, a crashed gateway's
      sessions close (ADR-024)
- [x] Dispatch gateways and client gateway discovery (power of two choices; clients wait while the cluster forms)
      (ADR-026)
- [x] One ordered link per node direction: replies travel with binds, pushes and events (ADR-022)
- [x] Placement: keys stay with suspect members, a dead node's keys move, deactivating actors keep their mail (ADR-023)
- [x] Client: `ClusterClient` (generated proxies, keyless session-actor accessors, DNS endpoints over every resolved
      address, dispatch redirect,
      callbacks inline or queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] Cluster events: `[NinoType]` structs, `[Key]`, `[Handle]`, gossiped handlers, mailbox delivery (ADR-019)
- [x] Backpressure: bounded shard inbox and actor mailboxes refuse with `Overloaded` (ADR-027)
- [ ] Backpressure on links: pause a link's receive while its target shard is full (today: refuse and reply)
- [x] Deadlines: the caller's remaining time travels with each request; expired work is dropped (ADR-028)
- [ ] Send group broadcasts only to gateways that have members of the group (deferred: ADR-029)
- [x] Resumable node links: a dropped connection replays what the peer did not process (ADR-008)
- [ ] Placement hints (co-locate actors that talk a lot)
- [ ] Gateway sessions as ECS entities (session components + flush system) on a gateway shard
- [ ] Actor self-deactivation (idle timeout) and persistence helpers

## Generator / analyzer (`src/DaServer.Generator`)
- [x] Contracts: struct proxies, dispatcher switch, registration, method ids, cluster accessors
- [x] Hosting: actor/service factories with handled events, EasyEcs system registration, contract assembly loading
- [x] Node entry point generated when a node project has no `Program.cs` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: DAS001-DAS014 (wire types, keyed actor events, actor/service mix, contract attributes,
      handler shape, event keys)
- [x] Attributes instead of contract markers (ADR-018, revised)

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), periodic stats line, graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / AddGateways / AddDispatch / WithGateways; compose publishing
      (ADR-015); no configuration files in node projects
- [x] 10k-player scenario: login, game, 3 gateways, dispatch, 10k bots (Sample.Bot fails on any gap, disconnect or
      p99 over the limit); passes through Aspire (README "Performance")
- [x] E2E in CI: `tests/DaServer.E2E` runs the AppHost with `Aspire.Hosting.Testing` (1000 bots in CI) and
      fails unless the bot exits 0
- [x] Aspire: one resource per replica (members are addressed individually), DNS seeds for headless services
      (ADR-030)

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard actor calls, keyed events, gateway fan-out and client calls through a gateway to
      another node allocate nothing per message
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), keyed events (local/remote), group broadcast (1k/5k
      sockets), broadcast to everyone through 3 gateways (3k/9k sockets)
- [x] CI workflow (build, test, sibling EasyEcs checkout)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [x] Record benchmark numbers of this release in the README
- [ ] Tests: crash with calls in flight, suspect flapping, hostile client frames beyond size, link backpressure
