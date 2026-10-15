# Requirements and acceptance

What DaServer must be, and how each requirement is checked before a change is accepted. Every release candidate
goes through the checklist at the end; the results (numbers, test counts) are recorded in [roadmap.md](roadmap.md).

## 1. Product requirements

| # | Requirement | Acceptance |
|---|---|---|
| R0 | **General-purpose core.** DaServer is a general ECS distributed server framework for high performance and high concurrency. The core contains no application scenario (game clients, sessions, gateways, logins, maps); such capabilities are optional modules on public extension points, scenarios are samples. | No type in `DaServer.dll` named `*Session*`, `*Gateway*`, `*Client*` (architecture test, after G2); scenario code only under `samples/`. |
| R1 | **Business code only.** Users write messages, actors and services with `[Handle]` methods, and ECS systems. Registration, routing, serialization, placement, discovery and orchestration are generated or provided. | The sample server contains no registration list, no routing code, no serializer calls, no configuration strings for topology. |
| R2 | **One vocabulary, no hidden meaning.** A message is a `[NinoType, Message]` struct (`IRequest<TReply>` when answered; its `Flow` says whether clients send it or receive it; plain `[NinoType]` types are data inside messages), handled by `[Handle]` methods; the message type decides the handler, the actor key is a call parameter; wire data is `[NinoType]`; ECS is EasyEcs. `[WireId]` only resolves a reported collision. | README vocabulary table covers every attribute; analyzer reports every misuse (R10). |
| R3 | **No hard-coded topology.** No role strings. A node hosts what its process contains; the deployment unit is a project. Gateways are a hosting capability, not a role. | `grep` finds no role names in `src/`, `samples/`; the AppHost declares node groups by project type only. |
| R4 | **Stateful and stateless.** Keyed actors (one loaded actor per key, on demand, rendezvous placement, turn-based) and stateless services (load-balanced, local first). | Cluster tests: single loaded actor per key across nodes, rebalance on join/leave without losing calls, service load balancing. |
| R5 | **Discovery and failure handling.** Nodes find each other through seeds and gossip; failed nodes are suspected and removed; graceful leave drains and unloads actors; a dropped node connection resumes without losing or repeating messages; a request whose caller already timed out is not run (on any node). Dispatch queues and mailboxes are bounded and refuse with `Overloaded`. | Cluster tests for join, crash (dead after timeout), graceful leave with in-flight calls, connections dropped mid-stream (`LinkTests`), expired requests (local and across nodes), full mailboxes. |
| R6 | **Messages + ECS.** `Ask` (request/response), `Send` (one-way) and `Publish` (every handler) without naming the receiver's type; per-tick logic in EasyEcs systems of the shard world. | Tests for each delivery kind; the sample uses all three. |
| R7 | **Clients and gateways.** Many gateways behind one balancer address; client messages validated by `Flow.FromClient`; session-bound actor keys; pushes to a session or a group serialized once and fanned out per gateway; `SessionClosed` delivered to bound actors, also when a gateway crashes. | Gateway tests: server-only messages refused, binding isolation, group broadcast across gateways, exact delivery under churn, balancer spread, `SessionClosed` on close and crash. |
| R8 | **Orchestration.** One Aspire AppHost runs the cluster locally (dashboard, OpenTelemetry) and publishes it (Docker Compose / Kubernetes). Node projects carry no names, ports or seeds. | `tests/DaServer.E2E` starts the AppHost with `Aspire.Hosting.Testing` in CI and passes only when the bot exits 0 (`E2E OK`). |
| R9 | **ECS all the way down.** The shard runtime follows EasyEcs's ECS principles: actors are entities, per-actor runtime state is components, per-shard state is singletons, each runtime system has one job, no event bus inside a world. | Design review against [EasyEcs ecs-principles](https://github.com/JasonXuDeveloper/EasyEcs/blob/master/doc/ecs-principles.md); no EECS warnings. |
| R10 | **Misuse is a build error.** Handler shapes (parameter, return type vs. the request's reply, duplicates, accessibility), message shapes (struct, `[NinoType, Message]`, non-generic), types that cannot cross the wire, id collisions, missing parameterless constructors, messages used against their flow, actors and services with fields (DAS001-DAS007). | One generator test case per diagnostic; generators never report diagnostics themselves. |
| R11 | **Scale.** 10,000 players through 3 gateways, and a balancer to one game node, chatting in maps while the realm broadcasts to everyone every 5 ticks: no gap, no disconnect, p99 broadcast latency under 250 ms on a 4-core machine, servers allocate nothing in steady state. | `Sample.Bot 10000 60 100 0 10` against the AppHost topology exits 0; node stats lines show 0 KB/s without OTLP. |

## 2. Engineering requirements

| # | Requirement | Acceptance |
|---|---|---|
| E1 | **Zero GC on hot paths** in steady state: calls, one-way messages, replies, events, pushes, broadcasts, mailbox, ECS ticks. | `AllocationTests` (process-wide counter, so background runtime work is included; any per-message allocation is >= 24 B; `BufferPool` growth to a new peak is pinned memory kept for reuse, counted apart and bounded): < 1 B per delivery for gateway fan-out, < 8 B per call/event across shards and through a gateway to another node. BenchmarkDotNet `Allocated` per operation reported for RPC, events and broadcast (only harness allocations allowed, explained). |
| E2 | **Latency and throughput** measured with BenchmarkDotNet over real loopback sockets. | RPC round trip (same node, cross node), concurrent calls, events, group broadcast to 1k/5k clients, broadcast to everyone through 3 gateways (3k/9k): numbers recorded in the README; no regression > 10% against the previous recorded run without an explanation. |
| E3 | **Bounded memory.** Every queue and buffer is bounded or pooled; slow consumers are disconnected; pools retain at most a configured budget. | Code review checklist; `BufferPool` and connection limits documented next to the code. |
| E4 | **Thread safety is explicit.** User code runs on shard threads only; cross-thread handoff only through the shard dispatch queue; shared runtime state is immutable snapshots. | Review: no locks in user-facing code paths; every shared structure documents its contract. |
| E5 | **Decoupled layers.** Business code sees `ICluster` (messages, groups, pushes), never `Node`. Dependencies point one way: Protocol → Buffers/Net → Rpc → Cluster → Runtime → Hosting/Aspire. | Review of `using`s; `ShardObject.Cluster` and `world.Cluster()` return `ICluster`. |
| E6 | **DRY and concise.** No copy-pasted variants (generated or shared helpers instead); no file over ~400 lines without a reason. | Review; `wc -l`. |
| E7 | **Incremental generator rules.** Syntax predicates first, equatable symbol-free models, tracking names, no diagnostics from generators. | `GeneratorTests.UnrelatedEditsHitTheCache` covers every stage. |
| E8 | **Tests.** Unit, in-process cluster, gateway/client, generator/analyzer, allocation and end-to-end (AppHost + bots) tests; all green in CI on every push. | `dotnet test`: all pass; CI green. |
| E9 | **Docs.** README (what/why/how with mermaid), design, decisions (one ADR per non-obvious choice), roadmap, this file. Code, comments, commits and docs in English. | Review. |
| E10 | **Commits.** Logic, docs and CI in separate commits; author = repository owner, committer = signing identity (Verified); no session links. | GitHub shows Verified on every commit. |

## 3. Release checklist

Run in order; record results in the roadmap.

1. `dotnet build DaServer.slnx` — 0 warnings, 0 errors.
2. `dotnet test DaServer.slnx` — all pass (count recorded).
3. `grep -rn "Role\|\"game\"\|\"gateway\"" src samples` — no topology strings.
4. BenchmarkDotNet: `RpcBenchmark`, `MessageBenchmark`, `BroadcastBenchmark`, `ScenarioBenchmark` — mean and
   allocated recorded.
5. Sample end to end: the AppHost's 10k-bot scenario prints `E2E OK` (R11).
6. Review against R1–R11 and E1–E10 (this file); open items go to the roadmap.
7. CI green on the pushed branch; commits Verified.
