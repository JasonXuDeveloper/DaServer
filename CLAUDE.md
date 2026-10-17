# DaServer

**DaServer is a general-purpose ECS distributed server framework for high performance and high concurrency** (.NET 11, ADR-054).
Users write messages, actors and services with `[Handle]` methods, and ECS systems; source generators write the
plumbing. The framework covers what every real-time backend needs (actors, services, messaging, membership,
placement, persistence, ingress, client connections; ADR-045/048) and knows no business domain: logins, maps,
matchmaking, inventories live in samples (`samples/mmo`) or user code. Load balancing, TLS and autoscaling are the
platform's.
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
src/DaServer.Abstractions/ contracts (netstandard2.1, net10.0, net11.0): attributes, IRequest, addresses, type ids (ADR-052)
src/DaServer.Core/         wire and transport shared by servers and clients (netstandard2.1, net10.0, net11.0): Protocol/
                           Buffers/ Net/ Rpc/ (calls, dispatch) Concurrency/
src/DaServer.Client/       client SDK (netstandard2.1, net10.0, net11.0): ClusterClient
src/DaServer.Simulation/   simulated users (client side, net10.0/net11.0): ClientLoop, Planning/ (HTN as ECS data),
                           Swarm + SwarmMeter (ADR-056)
src/Polyfills/             Lock, IsExternalInit, FrozenDictionary for netstandard2.1 (compiled into Abstractions)
src/DaServer/              server runtime (net11.0): Runtime/ Cluster/ Rpc/ Net/ Concurrency/ and the built-in capabilities on the
                           feature points: Features/ Ingress/ Clients/
  Runtime/Shard.cs         thread + dispatch queue (the cross-thread handoff) + wake/tick loop (nothing else)
  Runtime/ShardData.cs     shard ECS data: actor components and lifecycle tags (Mailbox, Ready, Turn, ...), singletons
  Runtime/ShardDispatch.cs routing/delivery/invoke operations shared by the runtime systems
  Runtime/Systems/         runtime systems: Dispatch, Turn, ServiceCall, Removal, Unload, Mailbox (ShardMessages group),
                           Rebalance (ShardRebalance), Idle (ShardIdleScan); each reacts to one lifecycle tag (ADR-037)
src/DaServer.Generator/    incremental generators (AskGenerator, RegistrationGenerator, PolyfillGenerator) + DaServerAnalyzer
                           (DAS001-016);
                           Shapes.cs reads symbols once for both, so "skipped by the generator" == "reported"
src/DaServer.Hosting/      Generic Host integration, configuration, OpenTelemetry (OTLP when configured), shutdown
src/DaServer.Persistence.PostgreSql/  actor records in PostgreSQL (ADR-068): PostgreSqlActorStore, AddPostgreSqlActorStore
src/DaServer.Testing/      in-process clusters for users' tests (ADR-071): TestCluster, a manual clock, stepped ticks
src/DaServer.Aspire/       Aspire hosting integration: AddDaServerCluster / AddNodes / WithClientConnections /
                           WithClientEndpoints
samples/mmo/               MMO scenario: Sample.Contracts / Sample.Shared (rules both sides run) / Sample.Client (the game
                           client as ECS) / Sample.LoginServer / Sample.GameServer / Sample.MapServer / Sample.GatewayServer
                           (one project per server, a five-line Program.cs, no appsettings; a server's components in
                           Sample.XServer.Components, DAS013) / Sample.Bot (the client as a swarm
                           with an HTN behavior: 10k-bot load test and e2e) / Sample.Tests / Sample.AppHost (Aspire)
samples/flashsale/         a million buyers for 100 units over the client protocol: FlashSale.Contracts / .Components /
                           .Server (stock shards, orders, waiting room, the Gate service) / .Crowd (the buyers as a
                           swarm, DaServer.Simulation) / .Tests / .AppHost
tests/DaServer.Tests/      NUnit: ClusterTest fixture, per-feature tests, generator/analyzer, allocation, architecture
tests/DaServer.E2E/        the AppHosts through Aspire.Hosting.Testing: 1,000 bots, 100,000 buyers
benchmarks/DaServer.Benchmark/   BenchmarkDotNet over real loopback sockets (requests, one-way messages, broadcast fan-out,
                           scenarios) and generator cost
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
6. **Developers only write business code**: messages (`[NinoType, Message]` structs, `IRequest<TReply>`,
   `[FromClient]`/`[ToClient]` when clients send or receive them), actors/services with `[Handle]` methods (ADR-035), EasyEcs systems with `[ForEach]`
   methods. Registration, routing, serialization, placement, orchestration are the framework's job (a node's
   Program.cs is five lines, ADR-021); if users would write the same lines twice, generate them.
7. **ECS all the way down**: the runtime follows the same ECS rules as user code (EasyEcs `doc/ecs-principles.md`):
   actors are entities, per-actor runtime state is components, per-shard state is singletons, each runtime system does
   one job. Actors only turn calls into data; per-tick logic lives in systems.
8. **Every change is proven**: NUnit tests (unit + in-process cluster + allocation) and BenchmarkDotNet numbers.
   Benchmarks model real high-concurrency usage (many connections, broadcast fan-out, concurrent callers) and are
   measured with BenchmarkDotNet only, never ad-hoc stopwatches.

## Rules

- Never block a shard thread on IO; never touch shard/actor state from another thread (post to the shard). Shard code
  (actors, services, systems) starts no work on other threads (DAS015): it awaits, and continuations come back.
- Never keep a `ref` into ECS storage across an `await`; re-acquire after awaiting.
- Actors are turn-based: one message at a time including its awaits (no interleaving). Calls in a cycle (A awaits B
  awaits A) fail at once with `CallCycle` (ADR-059): model such flows with one-way messages (`Send`/`Publish`).
- Orchestration is the Aspire model (`samples/mmo/Sample.AppHost`); do not add hand-written compose/k8s files.
  What Aspire does not publish (probes, grace periods, Compose health checks, HPA/PDB) is added by `DaServer.Aspire`
  through the publisher hooks (`PublishAsKubernetesService`, `PublishAsDockerComposeService`), never by hand.
  Scaling decisions belong to the platform (HPA/KEDA); the framework makes joining and leaving safe, it is not an
  autoscaler. `DaServer.Hosting.AddDaServer` is the framework's service defaults. Nodes run ASP.NET Core for their HTTP plane
  (health, admin, internal REST endpoints, ADR-046); the data plane stays DaServer's own TCP links. HTTP code reaches
  actors only through `ICluster`.
- The HTTP plane is for low-frequency traffic (probes, admin, webhooks, back-office). Client traffic at volume (a
  game's players, a sale's crowd, anything per player or per tick) uses the client protocol: TCP, or WebSocket at
  `/clients/ws` for browsers (wss behind the platform's TLS). An HTTP request crosses two protocols and costs a
  syscall pair of its own; the client protocol batches (ADR-046: 3.5 times the requests per core).
- Type ids (messages on the wire, actor id types, services, persisted components) are generated (FNV-1a); set one by
  hand only with `[TypeId]`, to resolve a reported collision or to keep a renamed type's identity and stored records.
- No topology strings (roles, node names) in code: a node hosts what its project compiles (ADR-017).
- The framework is general (ADR-045): a capability enters only if backends of different domains need it, in its own
  namespace on the public feature points, off unless configured; the runtime never depends on it. Domain logic goes
  into samples.
- Business code sees `ICluster`, never `Node` (ADR-020).
- Nothing stale: a replaced API is deleted, not kept as an `[Obsolete]` alias (pre-1.0); docs describe what is true
  now (a superseded ADR is removed or rewritten, no "first version used..." history, no dead roadmap items).
- Commits: logic, docs and CI in separate commits. The author is the repository owner; the committer is the
  environment's signing identity so the signature shows as Verified. No session links in commit messages.
