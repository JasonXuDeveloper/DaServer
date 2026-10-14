# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (INinoBufferWriter)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: contracts/markers, `RpcRegistry`, `CallContext` replies, pending-call table, pooled awaitables
- [x] Runtime: `Shard` (mailbox, EasyEcs World, timers, actors, services, in-flight tracking, async activation)
- [x] Runtime: `Node` (shards, local/remote routing, IRpcInvoker, forwarding/relay, reply sinks), `Actor`/`Service` bases
- [x] Cluster: push-pull gossip, seeds (static + DNS names), heartbeat failure detection, `ClusterView`, rendezvous placement, graceful leave + rebalancing
- [x] Gateway: client sessions, binding, [ClientAccessible] routing, push, groups + broadcast fan-out
- [x] Client: `GameClient` (generated proxies, callbacks inline or queued)
- [ ] Reliability: resumable links (ADR-008)
- [ ] In-world events (`[EventHandler]`)

## Generator (`src/DaServer.Generator`)
- [x] Contracts: struct proxies, dispatcher switch, registration, method ids, accessors
- [x] Implementations: actor/service factories + roles; ECS system registration; contract assembly loading
- [x] Diagnostics DAS001-DAS008 (return types, parameters, generics, callbacks, id collisions, actor base, ctor, members)

## Hosting / ops
- [ ] `DaServer.Hosting`: Generic Host, config binding, metrics, graceful shutdown
- [ ] Aspire AppHost sample, Dockerfile, docker-compose, Kubernetes manifests

## Next
- [ ] Generator tests (Roslyn driver: output + DAS diagnostics)
- [ ] Allocation tests for RPC/push/broadcast hot paths
- [ ] Unit tests: MpmcQueue, BufferPool, FrameHeader/Connection parsing, PendingCallTable, ClusterView/rendezvous, membership merge

## Quality
- [ ] Unit tests (queue, pool, frames, pending table, membership, placement, generator)
- [x] In-process cluster integration tests (RPC, ordering, single activation, failover, gateway flow) — 16 tests
- [ ] Allocation tests (steady-state RPC / push / broadcast = 0 B)
- [ ] BenchmarkDotNet suite (RPC round trip, broadcast 10k sessions, cross-world, gateway load)
- [ ] E2E: docker compose + bot client in CI
- [ ] CI workflow
