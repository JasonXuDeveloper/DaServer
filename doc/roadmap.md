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
- [x] Gateway: sessions, binding, [ClientAccessible] routing, push, `ClientGroup` broadcast fan-out
- [x] Client: `GameClient` (generated proxies, callbacks inline or queued)
- [ ] Reliability: resumable links (ADR-008)
- [ ] Gateway sessions as ECS entities (session components + flush system) on a gateway shard
- [ ] Actor self-deactivation (idle timeout) and persistence helpers

## Generator / analyzer (`src/DaServer.Generator`)
- [x] Contracts: struct proxies, dispatcher switch, registration, method ids, cluster accessors
- [x] Hosting: actor/service factories + roles, EasyEcs system registration, contract assembly loading
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: DAS001-DAS008

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / WithGateways; compose publishing in the sample (ADR-015)
- [ ] E2E in CI: AppHost-based run of the sample + bot

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation test: cross-shard actor calls allocate nothing per call
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), broadcast fan-out (1k/5k sockets)
- [ ] Record benchmark numbers in the README
- [ ] CI workflow (build, test, sibling EasyEcs checkout)
