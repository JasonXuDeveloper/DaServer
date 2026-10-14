# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (INinoBufferWriter)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: contracts/markers, `RpcRegistry`, `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013): actor entities with mailbox/turn components, runtime systems, system groups
- [x] Turn-based actors (ADR-014), stateless services
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip, seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-contract bindings, access by contract marker, push, `ClientGroup` broadcast fan-out,
      empty groups dropped, `SessionClosed` to bound actors
- [x] Client: `ClusterClient` (generated proxies, keyless session-actor accessors, DNS endpoints, callbacks inline or
      queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] Cluster events: `IEvent`/`IKeyedEvent`/`IHandle<T>`, gossiped handlers, mailbox delivery (ADR-019)
- [ ] Backpressure: bounded actor mailboxes (`Overloaded`), pause link receive while a shard is full
- [ ] Reliability: resumable links (ADR-008)
- [ ] Placement hints (co-locate actors that talk a lot)
- [ ] Gateway sessions as ECS entities (session components + flush system) on a gateway shard
- [ ] Actor self-deactivation (idle timeout) and persistence helpers

## Generator / analyzer (`src/DaServer.Generator`)
- [x] Contracts: struct proxies, dispatcher switch, registration, method ids, cluster accessors
- [x] Hosting: actor/service factories with handled events, EasyEcs system registration, contract assembly loading
- [x] Node entry point generated when a node project has no `Program.cs` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: DAS001-DAS012 (wire types, keyed actor events, actor/service mix, conflicting markers)
- [x] Contract markers instead of attributes (ADR-018)

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / AddGateways / WithGateways; compose publishing (ADR-015)
- [ ] E2E in CI: AppHost-based run of the sample + bot

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard actor calls and keyed events allocate nothing per message
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), keyed events (local/remote), broadcast (1k/5k sockets)
- [x] CI workflow (build, test, sibling EasyEcs checkout)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [ ] Record benchmark numbers of this release in the README
