# DaServer

.NET 10 framework for high-performance clusters (game servers: many gateways + many map/logic nodes, and other
low-latency backends). Users write messages, actors and services with `[Handle]` methods, and ECS systems; source generators
write the plumbing.
Code, comments, commits and docs are in English.

**Start here when resuming work:** `doc/requirements.md` (what must hold, release checklist), `doc/roadmap.md` (what
is done / next), `doc/decisions.md` (why things are the way they are), `doc/design.md` (architecture). Update the
roadmap when finishing a step and add a decision record for every non-obvious choice.

## Commands

```bash
dotnet build DaServer.slnx
dotnet test DaServer.slnx                                              # unit + in-process cluster integration tests
dotnet run -c Release --project benchmarks/DaServer.Benchmark -- --filter '*'   # BenchmarkDotNet only
```

EasyEcs comes from NuGet (`EasyEcsVersion` in `Directory.Build.props`). Use it as its docs teach
(https://github.com/JasonXuDeveloper/EasyEcs/tree/master/doc): `[ForEach]` systems, groups for cadence,
`UpdateInterval`, data instead of flags.

## Layout

```
src/DaServer/              runtime (net10.0): Protocol/ Buffers/ Concurrency/ Net/ Rpc/ Runtime/ Cluster/ Gateway/ Client/
  Runtime/Shard.cs         thread + cross-thread inbox + wake/tick loop (nothing else)
  Runtime/ShardData.cs     shard ECS data: actor components and lifecycle tags (Mailbox, Ready, Turn, ...), singletons
  Runtime/ShardDispatch.cs routing/delivery/invoke operations shared by the runtime systems
  Runtime/Systems/         runtime systems: Inbox, Turn, ServiceCall, Retire, Unload, Mailbox (ShardMessages group),
                           Rebalance (ShardRebalance), Idle (ShardIdleScan); each reacts to one lifecycle tag (ADR-037)
src/DaServer.Generator/    incremental generators (MessageGenerator, HostGenerator incl. node entry point)
                           + DaServerAnalyzer (DAS001-006);
                           Shapes.cs reads symbols once for both, so "skipped by the generator" == "reported"
src/DaServer.Hosting/      Generic Host integration, configuration, OpenTelemetry (OTLP when configured), shutdown
src/DaServer.Aspire/       Aspire hosting integration: AddDaServerCluster / AddNodes / AddGateways / AddBalancer /
                           WithGateways
samples/                   Sample.Contracts / Sample.LoginServer / Sample.GameServer / Sample.MapServer / Sample.Gateway
                           (one project per server, no Program.cs,
                           no appsettings) / Sample.Bot (10k-bot e2e) /
                           Sample.AppHost (Aspire: run + publish)
tests/DaServer.Tests/      NUnit: ClusterTest fixture, per-feature tests, generator/analyzer, allocation
benchmarks/DaServer.Benchmark/   BenchmarkDotNet over real loopback sockets (requests, published messages, broadcast fan-out)
doc/                       requirements.md, design.md, decisions.md, roadmap.md
```

## Engineering principles (apply to every change)

1. **Performance is the first constraint; GC is part of performance.** Hot paths (send/receive, dispatch, replies,
   mailbox, message sends, ECS ticks) allocate nothing in steady state: pooled `FrameWriter`/`BufferPool` (POH), struct
   mailbox items, pooled `IValueTaskSource`s, `in` struct messages. No LINQ, closures, boxing, string formatting there.
2. **CPU**: inline fast paths, `NoInlining` cold paths, no redundant bounds checks (only behind documented
   invariants), batch syscalls.
3. **Memory / cache lines (64 bytes)**: pad contended fields onto separate lines, keep hot structs small, bound every
   buffer/queue (no unbounded growth per connection).
4. **Thread safety is explicit**: user code runs on shard threads only (single-threaded per shard, no locks);
   cross-thread handoff only through mailboxes; shared runtime state is immutable snapshots swapped atomically.
5. **Concise, elegant, no duplication**: generate repetitive code with the source generator; one shared core with
   thin typed wrappers; no copy-pasted variants.
6. **Developers only write business code**: messages (`[NinoType, Message]` structs with a `Flow`,
   `IRequest<TReply>`), actors/services with `[Handle]` methods (ADR-035), EasyEcs systems with `[ForEach]`
   methods. Registration, routing, serialization, placement, entry points, orchestration are the framework's job;
   if users would write the same lines twice, generate them.
7. **ECS all the way down**: the runtime follows the same ECS rules as user code (EasyEcs `doc/ecs-principles.md`):
   actors are entities, per-actor runtime state is components, per-shard state is singletons, each runtime system does
   one job. Actors only turn calls into data; per-tick logic lives in systems.
8. **Every change is proven**: NUnit tests (unit + in-process cluster + allocation) and BenchmarkDotNet numbers.
   Benchmarks model real high-concurrency usage (many connections, broadcast fan-out, concurrent callers) and are
   measured with BenchmarkDotNet only, never ad-hoc stopwatches.

## Rules

- Never block a shard thread on IO; never touch shard/actor state from another thread (post to the shard).
- Never keep a `ref` into ECS storage across an `await`; re-acquire after awaiting.
- Actors are turn-based: one message at a time including its awaits (no interleaving). Calls in a cycle (A awaits B
  awaits A) deadlock until timeout: model such flows with one-way messages (`Send`/`Publish`).
- Orchestration is the Aspire model (`samples/Sample.AppHost`); do not add hand-written compose/k8s files.
- Wire ids are generated (FNV-1a); never renumber by hand except `[WireId]` to resolve a reported collision.
- No topology strings (roles, node names) in code: a node hosts what its project compiles (ADR-017).
- Business code sees `ICluster`, never `Node` (ADR-020).
- Commits: logic, docs and CI in separate commits. The author is the repository owner; the committer is the
  environment's signing identity so the signature shows as Verified. No session links in commit messages.
