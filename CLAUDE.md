# DaServer

.NET 10 framework for high-performance clusters (game servers: many gateways + many map/logic nodes, and other
low-latency backends). Users write contracts, implementations and ECS systems; source generators write the plumbing.
Code, comments, commits and docs are in English.

**Start here when resuming work:** `doc/roadmap.md` (what is done / next), `doc/decisions.md` (why things are the
way they are), `doc/design.md` (architecture). Update the roadmap when finishing a step and add a decision record
for every non-obvious choice.

## Commands

```bash
dotnet build DaServer.slnx
dotnet test DaServer.slnx                                              # unit + in-process cluster integration tests
dotnet run -c Release --project benchmarks/DaServer.Benchmark -- --filter '*'   # BenchmarkDotNet only
```

EasyEcs is consumed from the sibling checkout `../EasyEcs` when present (co-development), otherwise from NuGet
(`Directory.Build.props`).

## Layout

```
src/DaServer/              runtime (net10.0): Protocol/ Buffers/ Concurrency/ Net/ Rpc/ Runtime/ Cluster/ Gateway/ Client/
src/DaServer.Generator/    Roslyn incremental generator: proxies, dispatchers, registration, diagnostics (DASxxx)
src/DaServer.Hosting/      Generic Host integration, configuration, OpenTelemetry metrics, graceful shutdown
samples/                   Sample.Contracts / Sample.Server / Sample.Client / Sample.AppHost (Aspire)
tests/DaServer.Tests/      NUnit: unit, generator, in-process cluster integration, allocation tests
benchmarks/DaServer.Benchmark/   BenchmarkDotNet over real loopback sockets
deploy/                    Dockerfile, docker-compose, Kubernetes manifests
doc/                       design.md, decisions.md, roadmap.md
```

## Engineering principles (apply to every change)

1. **Performance is the first constraint; GC is part of performance.** Hot paths (send/receive, dispatch, replies,
   mailbox, proxies, ECS ticks) allocate nothing in steady state: pooled `FrameWriter`/`BufferPool` (POH), struct
   mailbox items, pooled `IValueTaskSource`s, struct proxies. No LINQ, closures, boxing, string formatting there.
2. **CPU**: inline fast paths, `NoInlining` cold paths, no redundant bounds checks (only behind documented
   invariants), batch syscalls.
3. **Memory / cache lines (64 bytes)**: pad contended fields onto separate lines, keep hot structs small, bound every
   buffer/queue (no unbounded growth per connection).
4. **Thread safety is explicit**: user code runs on shard threads only (single-threaded per shard, no locks);
   cross-thread handoff only through mailboxes; shared runtime state is immutable snapshots swapped atomically.
5. **Concise, elegant, no duplication**: generate repetitive code with the source generator; one shared core with
   thin typed wrappers; no copy-pasted variants.
6. **Developers only write business code**: contracts (`IService`/`IActor`/`IClientCallback`), implementations,
   ECS systems with `[Each]` methods. Registration, routing, serialization, placement are the framework's job.
7. **Every change is proven**: NUnit tests (unit + in-process cluster + allocation) and BenchmarkDotNet numbers.
   Benchmarks model real high-concurrency usage (many connections, broadcast fan-out, concurrent callers) and are
   measured with BenchmarkDotNet only, never ad-hoc stopwatches.

## Rules

- Never block a shard thread on IO; never touch shard/actor state from another thread (post to the shard).
- Never keep a `ref` into ECS storage across an `await`; re-acquire after awaiting.
- Wire ids are generated (FNV-1a); never renumber by hand except `[RpcId]` to resolve a reported collision.
- Commit logic, docs and CI changes separately. Commits are authored with the repository owner's git identity.
