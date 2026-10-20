# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Development order (agreed: one item at a time, each proven before the next)

1. Z2 core hot paths (done): H3, H2, M1 status-only refusals, M6 completion queue, M4 actor pools, M7 per-link chunk
   reuse, local sends without a copy, core allocation tests. M2/M3/M5 move with client connections (G steps 6 and 8);
   H1 comes with N3b, H4 with N8.
2. G0-G2 (done, below): one message model (ADR-047), feature points and ingress, client connections as a
   built-in feature, Abstractions and Client packages (ADR-048).
3. T2 ECS bots and business isolation (done): shared rules, a real client, simulated users that run it.
4. N2 `SendAfter` (done) (the delayed send primitive, per-shard timer wheel), N3b no sync-over-async (done; the
   await-only `Reply<T>` for callers outside the shards).
5. N8 (done) collections in messages without garbage (H4), on Nino 4 (ADR-078).
6. D1-D4 (done) graceful shutdown without a store, A-items (Aspire and hosting; A6 uses the `Trace` header extension).
7. C1-C4 (done) cluster hardening (before persistence: fencing assumes correct downing).
8. N1 (done) Contracts / Model / Logic split.
9. N5 (done) persistence (PostgreSQL + in-memory store) with D5-D6 and P1 `DaServer.Testing`, N6 state handoff, N7
   placement affinity with K1-K2 placement strategies and the owner table.
10. V1-V3 (done) versioning and rolling upgrades (before scaling: rolling deploys depend on them).
11. S1-S6 (done) elastic scaling.
12. The ECS rewrite (ADR-081, the "ECS rewrite" entry under "Open cleanup" below): P1-P3 and P5 done; P4, P6 and P7
    superseded by R.
13. R — refactor to the v24 programming model ([drafts/programming-model.md](drafts/programming-model.md),
    [drafts/architecture.md](drafts/architecture.md)) on EasyEcs 4.5.0 (now; ADR-082, ADR-083). Breaking; each step
    proven before the next:
    - [ ] R1 EasyEcs 4.5.0 (`EasyEcsVersion`) and the compile checks: the generated explicit `Key`
      (`Map IIndexed<Map>.Key => this`) is accepted by value indexes; `IActor<TSelf>` with its constraints
      (`ISystemGroup`, `IGroupWith<In<TSelf>>`) compiles in the framework assembly.
    - [ ] R2 Abstractions and attributes: `IFromClient`/`IToClient`/`[To]`/`IServer` in Abstractions; `IActor<T>`,
      `[Handles<K>]`, `[On<T>]`, `IMessageTo<K>`, `IRequestTo<K,R>`, `[From<K>]`, `IEvent`, `IPersisted`, `[Claims]`,
      `[JoinOnConnect<T>]`, `[TickRate]`, `[Stateless]`, `[Transient]` in the runtime.
    - [ ] R3 Generator and analyzers: explicit `Key`, typed sends, `[On<T>]` jobs, the closed `Registration`,
      `wire.manifest` (one section per project; user projects are Protocol and Server), the draft's §13 checks.
    - [ ] R4 Runtime core: shards (one world each, `MaxDegreeOfParallelism` per world), the ordered phase, `Due`,
      `Outbox`, `Current`, the framework groups (Ordering, Settling, Cleanup); messages-as-entities measured once.
    - [ ] R5 Presences, the gateway (`Client` on `[Hosts<Client>]`), `[To]` routing, `[JoinOnConnect]`, SDK re-join,
      `ClientGroup` subscriptions.
    - [ ] R6 Persistence, activation and migration: epochs and fencing, placeholder roots, `IPersisted` records
      through `Descendants` and `ComponentSet`, migration bytes with layout hashes. Hot restart's runtime side (draft
      §10, JIT and AOT): shards stopped between two ticks, the handoff bytes (`world.Save`, pending mail, shard
      clocks, runtime state) over a local named pipe or a node-local snapshot file, `Restarting` with a deadline in
      membership, the successor joining with the same generation and the next incarnation and keeping the epochs;
      in-cluster tests for both transports and for each failure before and after the commit point (gated on EasyEcs
      request #22, `world.Load(input, markWritten: false)`).
    - [ ] R7 Samples (mmo, flashsale) on the new model.
    - [ ] R8 Native AOT publish of the samples' nodes with 0 warnings (gated on EasyEcs request #21: IL3054 for tracked
      components); the AOT E2E run. Admin endpoints as Nino (or plain text for `Accept: text/plain`), `MapAsk`/`MapSend`
      Nino by default with per-endpoint opt-in JSON through the application's source-generated context (ADR-083).
    - [ ] R9 BenchmarkDotNet against the pre-R baseline, allocation tests, the 10k-bot E2E, review rounds; the hot
      restart pause measured end to end (BenchmarkDotNet per shard, E2E with clients connected; target under 1 s per
      node, typically hundreds of milliseconds).
    - [ ] R10 Promote the drafts into design.md and decisions.md (CLAUDE.md's Layout, ADR-017), delete the drafts.
14. N9 hot restart, on R's model (draft §10; JIT and AOT, ADR-083): the operator side of what R6 builds.
15. N4 route completeness, N11 generated filters (on the G filter point), K3-K5, open cleanup, quality items. Later
    optional modules: `DaServer.Reminders`, `DaServer.Topics`, leases for strict singletons.
16. After the self-review converged (agreed): Z3 and Z4; P2 `DaServer.Persistence.Redis` and G7 browser clients;
    group broadcasts only to nodes with members (ADR-029); the server-side NPC sample.
EasyEcs in parallel when DaServer waits on benchmarks: the Friflo gap, comparison tables in the docs.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (over Nino's `NinoArrayBufferWriter`, ADR-078)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: messages (`IRequest<TReply>`, `HandlerRegistry`), `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013, ADR-081): actor entities (`ActorKey`, lifecycle tags, a `DynamicBuffer<Envelope>`
      mailbox used while the actor is not live), runtime systems, system groups; dispatch queue (`DispatchSystem`,
      `DispatchQueueCapacity`) (ADR-040)
- [x] Actors as static `[Actor<TId>]` classes of `[Handle]` methods over components, one message at a time, handlers
      that never wait (answers as `Answered` mail); stateless `[Service]` classes (ADR-081)
- [x] Typed actor ids (ADR-043): `[Actor<TId>]`, `Send`/`Ask(id, m)` route by the id type, `Publish` to services only
- [x] Fence a node declared dead: its actors are removed without their `Unloading` mail (ADR-042, from Orleans)
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip of pulses (cards once, ADR-031), seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-handler-type bindings, per-message `[FromClient]` access, push, `ClientGroup` broadcast fan-out
      (O(1) membership, `All` is a group, no allocation per broadcast), `SessionClosed` to bound actors, client frame
      limit (ADR-025)
- [x] Runtime-owned sessions: Bind joins the actor's group, an actor's known sessions are child entities, a crashed
      gateway's sessions close (ADR-024)
- [x] Clients connect through the platform's load balancer and stay; `MaxConnectionAge` moves old connections to new
      nodes, the clients resuming their sessions (ADR-026)
- [x] One ordered link per node direction: replies travel with binds, pushes and events (ADR-022)
- [x] Placement: keys stay with suspect members, a dead node's keys move, unloading actors keep their mail (ADR-023)
- [x] Client: `ClusterClient` (the same messages as servers, session-bound actor messages without an id, DNS
      endpoints over every resolved address, entry addresses, callbacks
      inline or queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] One-way and published messages: `Send`, `Publish` to every handling service, gossiped handlers, mailbox
      delivery (ADR-019)
- [x] Backpressure: bounded dispatch queues and actor mailboxes refuse with `Overloaded`; links are never paused
      (ADR-027)
- [x] Deadlines: the caller's remaining time travels with each request; expired work is dropped (ADR-028)
- [ ] Send group broadcasts only to client-facing nodes that have members of the group (deferred: ADR-029)
- [x] Resumable node links: a dropped connection replays what the peer did not process (ADR-008)
- [ ] Client sessions as ECS entities in net loops (ECS rewrite P4; superseded by R5: `Client` actor entities)
- [x] Actors unload when idle (`UnloadAfterIdleSeconds`, ADR-033)

## Now: zero GC (agreed; Z2 is step 1 of the development order)

Steady state allocates nothing: per message, call, reply, tick, broadcast, connection, gossip round and timer.

- [x] Z1 Audit every runtime path (src/DaServer, Hosting, generated dispatchers and `Ask` overloads, what samples
      teach): `TaskCompletionSource`/`Task`/timers/`CancellationTokenSource` per operation, unpooled awaitables, async
      state machines, closures and per-call delegates, LINQ, boxed enumerators, `params`, string formatting and
      logging without guards, boxing (structs in object fields), per-operation arrays/lists/records, Nino collection
      and string deserialization per message (N8), gossip/membership rebuilds, resume tickets, metrics.
      Findings (audit of this branch, fix order below):
      - H1 user `async ValueTask` handlers used the default builder: one state-machine box per suspended call. Fixed:
        handlers never await (ADR-081); code outside the shards awaits the pooled `Reply<T>` (ADR-062).
      - H2 `CallContext.AwaitReply` (pooled builder hits its per-thread cache cap; `ConfigureAwait(true)` allocates a
        context continuation per await). Fix: pooled `ReplyWatcher<T>`.
      - H3 pool caps below in-flight bounds (PendingCall<T> 1024, RelayCall 1024, FrameWriter 4096): bursts allocate
        and drop. Fix: per-shard-thread stacks for PendingCall, caps 64k (configurable) for frames and relays.
      - H4 Nino collections/strings per message (`Motion` list per client, strings in Say/Chat/replies). Fixed by N8
        for collections (`[NinoPooled]` holders the handler returns); a string is a new string per message (low
        volume: chat and logins; Nino writes ASCII text one byte a char).
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
      Allocation tests still missing (they come with the items that change those paths): grant churn, the client
      callback queue, collection messages (N8).
- [x] Z2 Core fixes: H3 (per-shard call stacks, 64k frame/relay pools), H2 (pooled `ReplyWatcher<T>`; tests build
      optimized so async state machines are structs as users ship them), M1 (status-only refusals, ADR-049), M6
      (completions and callbacks in their own queue, ADR-027), M4 (actor churn allocates nothing: an actor is an
      entity, ADR-044), M7 (a resend window recycles up to 16 released chunks), L7 (a local `Send` hands its frame over
      instead of copying it). New allocation tests: calls to another node, service Send/Ask/Publish (local and
      remote), refusals, relayed timeouts, actor churn, resend window reuse, idle nodes. Kept: L1 view rebuilds and
      L5 reconnects (per membership change), L4 metric sampling (a few hundred bytes per collection, only with a
      listener), L6 foreign awaits (a closure per await in a foreign UI-style context). H1 comes with N3b, H4 with N8,
      M2/M3/L2 with G step 6, M5/L3 with G step 8.
- [ ] Z3 Prove it: an allocation test per hot path the audit finds uncovered (0 B per operation after warm-up); the
      BenchmarkDotNet A/B against the baseline is done (G step 12). Done: calls from outside the cluster (thread-pool
      callers rent a call on one thread and return it on another; the shared pool held 1,024, so bursts above it
      allocated: 29 -> under 1 B per call, `BurstsOfCallsFromOutsideTheClusterDoNotAllocate`); the benchmarks' own
      harness allocates nothing (pooled `ValueTask` workloads) and their actors are `[Sticky]` (load balancing moved
      "remote" actors onto the caller mid-run). Every call, hot-actor and timer benchmark now reads 0-3 B per
      operation; what remains in the longest ones is the process background (about 1 KB/s). Scenario benchmarks to add: a hot
      actor (many concurrent callers on one key: the flash-sale shape), a burst beyond the mailbox and dispatch
      bounds (refusal cost and time to drain), a dense area-of-interest cell (broadcast to one crowded cell). Surges
      over time (p99, refusals, recovery) are load tests: `Sample.Bot` for crowded maps, `FlashSale.Crowd` (client
      protocol) for flash sales.
- [x] Z5 Client writes on a beat (ADR-070): `ClientConnectionOptions.FlushInterval` (off by default; the sample uses
      2 ms) writes each session once per beat from one flush thread, directly, without waking send loops; a full socket
      hands the rest to the connection's carry loop. Clients keep their receive buffers (no zero-byte read per push);
      node-side sessions still release idle ones. The 10k-in-one-map scenario on one 4-core VM shared with the bots:
      p99 1,010 ms with drops -> 432 ms (client buffers) -> 163 ms with a 2 ms beat, no drop or gap; through Aspire
      (`tests/DaServer.E2E`, 10,000 bots, one map, 60 s): E2E OK, p50 75 ms, p99 199 ms, six nodes exit 0. Rejected on the
      way, with numbers in ADR-070: writing at the end of a batch on the calling thread, per-core resume queues
      (Kestrel's IOQueue), io_uring (not in .NET 11), a beat split across the thread pool (faster on idle cores,
      p99 up to 795 ms on saturated ones).
- [ ] Z4 Keep it: CLAUDE.md rule plus analyzer coverage where a misuse is detectable in user code (e.g.
      `TaskCompletionSource`/`Task.Delay` in handlers and systems: use pooled framework awaitables and `SendAfter`).

## One message model, built-in ingress and client connections (G0-G2 and G5 done, G7 open)

One framework (ADR-045, ADR-048): ingress (`DaServer.Ingress`) and client connections (`DaServer.Clients`) are
built-in capabilities on public feature points, off unless configured; business domains live in samples. Load
balancing, TLS, CDN/WAF, Gateway API routes and autoscaling stay with the platform.

- [x] G0 One message model (ADR-047), steps 1-4: `Send`/`Ask`/`Publish` plus addresses (`IActorId` keyed,
      `INodeAddress` pinned, `IGroupAddress` fan-out; scope = `TypeId<TAddr>`, kind a JIT constant per address type);
      `Push`/`Broadcast` are sends to session / group addresses, `JoinGroup`/`LeaveGroup`/`Disconnect` messages to
      them, `BindSession` an `Ask` of `SessionAccess` (removed verbs, no aliases). Wire: `Message`/`Reply`/`Control`, `Target`
      always set, trailers by flag (`Origin` 16 B, call id; `Trace` with A6). Wire break, no rolling
      upgrade across it (pre-1.0). A/B of the G work against the baseline: G step 9 below and the README.
- [x] G1 Feature points and ingress, step 5, proven by two consumers (client connections and the HTTP plane).
- [x] G2 Client connections as a built-in feature, grants, packages, keep-alive, .NET 11, tests, docs: steps 6-10.
- [x] G5 `samples/flashsale`: buyers over the client protocol (TCP or WebSocket) to a stateless `Gate` service on
      every node; 16 `StockShard` actors per sale (a handler is the atomic decrement), `Order` actors keyed (user,
      sale) so retries get the same answer, the `Sale` actor collecting the shards' buyers so each shard's gate answers
      the late crowd itself; an optional queue (`PerSecond`, `AdmissionSystem`). `FlashSale.Crowd` is a
      `DaServer.Simulation` swarm (`MeasureUntilAsync`); AppHost, E2E (100,000 buyers in CI); the README compares it
      with the usual designs. One 4-core VM with the crowd on it: 3,000,000 buyers on one node, 6,000,101 requests in
      13.2 s with requests on a 1 ms beat (548,000/s steady, 1.51 node cores; ADR-070); the machine is the limit.
- [ ] G7 Browser clients: `DaServer.Client.Browser`, the client (contracts, Nino's generated code, `ClusterClient`)
      compiled to `browser-wasm` over the browser's WebSocket, with a thin `[JSExport]` API for JS (connect, send, ask,
      push callbacks); no TS code generation. Prove: the flash sale from a browser page. Unity WebGL (IL2CPP) needs a
      jslib WebSocket transport for the existing client instead.
- [ ] P2 Optional `DaServer.Persistence.Redis`: actor records in Redis (versioned writes in Lua) for teams that run
      Redis already; durability is Redis's (AOF). Decide with the user before building it.
- Not in the framework: waiting rooms (virtual queues for human surges). They are policy for one kind of traffic,
  belong at the very edge (CDN/SaaS) to protect origins, and compose from ingress primitives (admission filters,
  token bucket, load-aware refusal) plus a queue actor; `samples/flashsale` shows the pattern.

What G delivered (each step kept every test green):

0. Baseline BenchmarkDotNet: Rpc, Message, Broadcast (1k/5k x 1/9), Scenario (3k/9k) on d676d2a.
1. Wire: `Target` always set, call-id trailer rewritten per hop, 3 frame kinds, receiver dispatch by one `Target`
   lookup (handler type -> shard, session/group scope -> client connections).
2. Addresses: `IAddress`/`INodeAddress`/`IGroupAddress`, `Send(addr, m)`, address-generic generated `Ask` overloads;
   DAS006 checks `[ToClient]` by address kind; asking a session or group and a client addressing anything but actors
   are DAS008.
3. `Origin(ulong Node, long Id)`: raw 16-byte trailer, `CallContext(ReplyTo, CallId, Origin)`, `ctx.Caller`;
   the origin is an `INodeAddress` (`ctx.Send(ctx.Caller, m)`) and `ClientSession` converts implicitly from it.
4. Session operations are messages to `ClientSession` (`JoinGroup`/`LeaveGroup`/`Disconnect` to a session or a group;
   `BindSession` asks a `SessionAccess`, so ADR-022 ordering holds).
5. Feature points and ingress (`DaServer.Features`, `DaServer.Ingress`): `NodeFeature` hooks, options bound from
   `DaServer:{name}` and enabled by configuration (`AddDaServerFeature`), `INodeContext.AddGauge`, pulse slots and
   card entries, `HandleAddress<TAddr>` with scopes in the card and group fan-out by scope, `IIngress` (raw and
   typed, origin, capped deadline, `IngressLoadLimit` early refusal), admission (`IIngressFilter`, `RateLimited` with
   a retry hint, `TokenBucket`, `SignedToken`), `TimerWheel<T>` (hashed wheel, one owner, O(1),
   allocation-free), dispatch-stage message filters (`MessageFilters.Register<T>` / `Register(IMessageFilter)`, run
   by generated dispatchers; N11 generates into them), the HTTP plane minimum (nodes on
   `WebApplication`, Kestrel on `DaServer:HttpPort`, `MapAsk`/`MapSend` through the ingress, statuses to
   429/503/504/403 with `Retry-After`, `DaServer-Budget` header; one origin id space per node for sessions and HTTP
   requests).
6. Client connections as a feature (`DaServer.Clients`, enabled by `DaServer:Clients`): generic card entries and
   pulse slots (`ClusterMember` keeps no client fields), the actor side in `ActorSessions` (ADR-050), client entry
   addresses (ADR-026), M2 (group objects pooled, empty
   groups removed lazily: group churn allocates nothing), M3 (resume graces on a `TimerWheel`), a per-session rate
   limit (`ClientConnectionOptions.MessagesPerSecond`/`Burst`), WebSocket transport (`WebSocketChannel` behind `IFrameChannel`,
   `/clients/ws`, `ClusterClient.ConnectAsync(Uri)`). L2 kept: a grant change copies the session's small grants
   array (a login or map change, not per message). Moving clients off a draining node is D3 (retired in waves, make
   before break).
7. `[FromClient]` / `[ToClient]` on `[Message]`; grants (ADR-051): `GrantSession`/`RevokeSession` admit a session to an
   actor, the edge refuses any other named actor; resuming asks the granted actors (ADR-036).
8. Packages (ADR-052): `DaServer.Abstractions` (contracts) <- `DaServer.Core` (shared wire and transport) <-
   `DaServer.Client` / `DaServer` (server runtime); the first three build for netstandard2.1 and net11.0, and
   `samples/mmo/Sample.Contracts` builds for both. Generators split by output (ADR-012): `AskGenerator`,
   `RegistrationGenerator` (static-constructor registration, IL2CPP-safe), `PolyfillGenerator`. M5: call tables allocate slots in doubling chunks as calls in flight reach them
   (`ClusterClient(maxPendingCalls: 4096)` is only a bound), connections with `ReleaseIdleBuffers` hold no send
   buffer when idle, clients share handler tables: 10k sample bots use 182 MB resident / 80 MB managed. Aspire:
   `AddNodes(...).WithClientConnections()` and `WithClientEndpoints(group)` (`DaServer__ClientEndpoints__{i}`).
   Names: `ClientSession(Node, Id)`, `NodeOptions.SigningSecret`, contract types in the root `DaServer` namespace,
   `HandlerRegistry`, `ClusterClient`.
9. .NET 11 (ADR-054): the server runtime targets net11.0 (local calls 2.3-2.7x faster under concurrency, remote
   1.1-1.3x; messages 1.6-3x).
10. Generator cost (`GeneratorBenchmark`: 1,000 messages, 250 handler types, 5,000 call sites): symbol checks compare
    names in place, requests and handlers are found through the compiler's attribute index
    (`ForAttributeWithMetadataName`), a handler type is read once: full run 36.0 ms (13.0 MB), an unrelated edit
    9.8 ms (2.4 MB). The analyzer costs what an empty analyzer watching the same symbols and invocations costs
    (720-750 ms, the compiler's binding).
11. Keep-alive (ADR-053): `Ping`/`Pong` when a direction sent or received nothing for an interval, silence closes
    client sessions (45 s), client connections (30 s, `CheckConnection` probes) and node link connections;
    `KeepAliveTests`.
12. Tests: `ArchitectureTests` (package references point down only; the runtime's folders use no client-connection
    type); allocation tests (< 1 B per delivery hold); A/B against step 0, d676d2a on .NET 10 vs the G head on
    .NET 11, back to back on one machine (BenchmarkDotNet default job):

    | Case | d676d2a | G head | |
    |---|---:|---:|---:|
    | 1 call local / remote | 31.5 / 121 us | 20.2 / 101 us | -36% / -17% |
    | 64 calls local / remote | 480 / 377 us | 218 / 335 us | -55% / -11% |
    | 1,024 calls local / remote | 4.16 / 2.22 ms | 1.88 / 1.67 ms | -55% / -25% |
    | messages local / remote | 5.10 / 1.78 us | 620 / 981 ns | -88% / -45% |
    | broadcast 1k x 1 / 1k x 9 | 5.37 / 5.81 ms | 5.35 / 5.86 ms | 0 / +1% |
    | broadcast 5k x 1 / 5k x 9 | 25.1 / 28.2 ms | 26.2 / 28.9 ms | +5% / +2% |
    | scenario 3k / 9k | 14.9 / 44.0 ms | 13.2 / 47.2 ms | -12% / +7% |

    Allocated per operation: equal or lower everywhere (messages 0 on both).

Open after G:

- [x] Trim `BufferPool` buckets back after a peak (41 MB of a 10k-bot process's 80 MB managed heap were 4 KB buffers
      kept from the all-sessions broadcast peak): every 60 s each bucket releases half of the buffers it held through the
      whole period without lending them. A bucket is a FIFO queue, so those are exactly the ones queued before the last
      trim that its renters' cursor has not reached since (no bookkeeping on rent: a first try that tracked a low-water
      mark on every rent cost 26% on four threads); buffers in use, even in rare bursts, never go
      (`APeaksBuffersGoBackHalfPerTrimWhileUsedOnesStay`; `PoolBenchmark.Buffers` unchanged).
- [x] Group broadcasts on .NET 11 against d676d2a on .NET 10: -9% to +8% (the release checklist's run), inside
      the runs' spread; nothing to profile further.
- [x] Release checklist recorded on 7c5b50b (October 2026, one 4-core VM): build 0 warnings; 395 tests pass (32
      skipped: PostgreSQL ones without `DASERVER_TEST_POSTGRES`, which CI sets); topology strings only in AppHosts and
      doc comments; the 10,000-bot E2E (one map, 60 s, lean bots): E2E OK, 2.4M broadcast samples, p50 75.8 ms, p99
      208.9 ms, p99.9 278.5 ms, no gap or disconnect, six nodes exit 0. BenchmarkDotNet default job, d676d2a on
      .NET 10 then the head, back to back:

      | Case | d676d2a | head | | Allocated d676d2a -> head |
      |---|---:|---:|---:|---:|
      | 1 call local / remote | 30.3 / 132 us | 5.17 / 85.9 us | -83% / -35% | 120 / 175 B -> 1 / 0 B |
      | 64 calls local / remote | 403 / 357 us | 169 / 315 us | -58% / -12% | 174 / 159 B -> 0 / 0 B |
      | 1,024 calls local / remote | 3.98 / 1.91 ms | 0.78 / 1.05 ms | -81% / -45% | 124 / 174 B -> 0 / 1 B |
      | messages local / remote | 4.62 / 1.58 us | 454 / 730 ns | -90% / -54% | 0 / 0 B -> 0 / 0 B |
      | broadcast 1k x 1 / 1k x 9 | 5.35 / 6.33 ms | 5.21 / 5.78 ms | -3% / -9% | 277 / 277 B -> 7 / 7 B |
      | broadcast 5k x 1 / 5k x 9 | 29.0 / 30.1 ms | 26.5 / 32.5 ms | -9% / +8% | 280 / 280 B -> 50 / 46 B |
      | scenario 3k / 9k | 15.3 / 48.4 ms | 15.5 / 52.0 ms | +2% / +8% | 142 / 152 B -> 16 / 50 B |

      Allocated per operation: calls 120-175 B -> 0-1 B, broadcasts 277-280 B -> 7-50 B, scenario 142-152 B ->
      16-50 B. Every case is within the 10% bound; the two +8% cases are inside one standard deviation of either side
      (2.6 and 4.2-5.3 ms). The 2 ms flush beat (head only, off by default) trades latency for CPU on idle cores, as
      ADR-070 records.

## Next: graceful shutdown and no data loss

Detection: the Generic Host turns SIGTERM, SIGINT (Ctrl+C) and SIGQUIT into `IHostApplicationLifetime.ApplicationStopping`
and calls `IHostedService.StopAsync` with a token that fires at `HostOptions.ShutdownTimeout` (30 s by default);
after it the process exits whatever is still running. Platforms send SIGTERM and kill later: Kubernetes after
`terminationGracePeriodSeconds` (30 s default), Docker Compose after `stop_grace_period` (10 s default). SIGKILL,
OOM kills, crashes and power loss give no signal at all, so no shutdown code can be the only protection.
- [x] D1 One shutdown budget (ADR-063): `NodeOptions.ShutdownTimeout` (25 s) bounds `StopAsync`, which honors the
      host's token; the host waits 5 s longer, Compose `stop_grace_period` is the budget + 10 s
      (`DaServerNodeGroup.WithShutdownTimeout`). Past the deadline the node logs, per shard, the actors not unloaded and
      saved yet, and exits instead of hanging.
- [x] D2 Ordered stop: readiness off (`/health` fails once Leaving) -> Leaving (callers reroute) -> drain features
      (client connections) -> unload every actor -> close links. Each phase is logged with its duration and recorded
      as `daserver.stop.phase.duration` (phase tag). Store flush joins with N5 (D6).
- [x] D3 Client-facing nodes drain: the listener closes, clients are retired in waves over
      `ClientConnectionOptions.DrainSpread` (3 s) and move to other nodes, make before break (ADR-026; readiness turned
      the balancer away first); the stop waits up to `RetireGrace` for them. `ClientsOfALeavingGatewayMoveAndResumeOnAnother`.
- [x] D4 Tests: a stop that exceeds its budget reports the unsaved actors and returns
      (`AStopThatExceedsItsBudgetReportsAndReturns`); a leaving gateway's clients keep their sessions; a stop under load
      loses no acknowledged deposit (`AStopUnderLoadLosesNoAcknowledgedDeposit`, 32 callers through the node that stays);
      a kill loses at most the last checkpoint interval (`AKillLosesAtMostTheLastCheckpointInterval`); both stores.
- [x] D5 Persistence that survives a crash (ADR-068): on unload always, a checkpoint every `SaveEverySeconds` when the
      record changed, `SaveNow` for critical changes; versioned saves reject a stale owner's late write.
- [x] D6 Store flush and backpressure: one save batch (at most 512 records) in flight per shard; the stop's unload phase
      waits for every final write (actors are removed only once written); a failing store is retried every second; the
      store's health joins readiness (the Aspire.Npgsql data source's own health check).

## Next: Aspire and hosting best practices (A-items, with D1-D4; checked against Aspire 13.6)

Aspire generates manifests and runs the local cluster; it never autoscales and publishes no grace period, no
Compose health check and no probe except HTTP probes declared with `WithHttpProbe`. What a node needs from it:

- [x] A1 HTTP plane on ASP.NET Core (ADR-046): every node runs Kestrel on a separate HTTP port (`DaServer__HttpPort`,
      Aspire endpoint `http`) next to its own TCP data plane. Official health checks: `daserver-ready` (joined, not
      leaving/draining, store healthy) mapped to `/health`, `daserver-live` (shard stall watchdog, N3b) tagged `live`
      and mapped to `/alive`, always on (not Development-only like the template). `DaServer.Aspire` declares the
      endpoint and readiness / liveness probes (`WithHttpProbe`, ASPIREPROBES001: Kubernetes probes, and the HTTP health
      check local `WaitFor` and the dashboard use). Users map REST endpoints in their node project (admin,
      webhooks, payment callbacks, HTTP login); an endpoint reaches actors only through `ICluster` (Ask/Send post to
      the shard), never shard state.
- [x] A2 Drain budget published (D1): `WithShutdownTimeout` sets Compose `stop_grace_period` and Kubernetes
      `terminationGracePeriodSeconds` (budget + 10 s: the host's 5 s margin and 5 s more) for whichever environment the model publishes to, and on Kubernetes a 5 s
      preStop sleep (counted in the grace period) so endpoint removal propagates before the node leaves.
- [x] A3 Compose: `stop_grace_period`, `restart: unless-stopped` and a `healthcheck` per node. The .NET images ship
      no curl or wget, so the check runs the node itself in probe mode (`dotnet /app/{assembly}.dll
      --DaServer:Probe=/health`: it asks `/health` on its own HTTP port and exits 0 or 1 before anything starts, about
      0.2 s); services waiting for a node group (`WaitFor`) depend on it `service_healthy`, so they start once a node
      joined. Checked in a container built by `PublishContainer`: healthy once joined, 1 against a closed port.
- [x] A4 Publish mode = one workload per node group (ADR-030): a Compose service with `deploy.replicas` or a
      Deployment with `replicas`; a headless `{group}-peers` Service (`clusterIP: None`, `publishNotReadyAddresses` so
      joining pods gossip); a node in a container advertises its own address; run mode keeps one resource per replica.
- [x] A5 Dashboard and admin: nodes map `/admin/membership`, `/admin/stats`, `/admin/drain` and `/admin/unload/{type}` on
      their HTTP plane, authorized by a bearer token the signing secret derives (HMAC, so the token gives no ticket
      away); `DaServer.Aspire` gives every node the commands Membership, Stats, Drain and Unload actors (dashboard,
      `aspire resource`, MCP) and, run locally, a cluster resource parenting its nodes whose state counts the joined ones
      ("6/6 joined"). A node's own state is Aspire's health (ready = joined and not leaving), so no custom node states
      (`AdminEndpointTests`, the E2E runs).
- [x] A6 Tracing (ADR-080): a handler whose message carries a trace context runs in a span of the `DaServer`
      activity source, child of the sender's on any node; asks, sends and publishes carry the running handler's
      context (or an ASP.NET Core request's), an ask's `Answered` mail continues the asking handler's trace, in the frame's extension block, the first entry of a block readers skip unknown entries
      of (new metadata needs no new link protocol). `AddDaServer` exports the traces with the ASP.NET Core and
      HttpClient instrumentation when OTLP is configured, so a REST call and the actor calls it causes are one trace.
      Traces start where the application starts them, never per message (`TracingTests`).
- [x] A7 Store wiring: `group.WithActorStore(postgres.AddDatabase("actors"))` (`WithReference` on the group's nodes,
      `WaitFor` when published, ADR-069), `builder.AddPostgreSqlActorStore()` in the node's Program.cs; the sample keeps player profiles in
      PostgreSQL on a data volume. Clients wait for every node of the cluster (`WithClientEndpoints`).
- [x] A8 Parameters and environments: `DaServerNodeGroup.WithEnvironment(name, parameter)` sets a node option on every
      node of a group from an AppHost parameter, published as a Helm value (`{{ .Values.config.map.map_shards }}`, its
      default with `publishValueAsDefault`) or a Compose variable (`.env`, filled by `aspire deploy` like every other);
      the MMO sample sets its map nodes' shards so, and points at an existing database with `AddConnectionString`
      (`-- ExistingDatabase=true`). Replica counts stay ints of the model (Aspire publishes no parameter there); the
      platform's autoscaler owns them in production.
- [x] A9 Tests through Aspire: the E2E runs wait for the cluster resource to count every node joined and read a node's
      Membership command; the scale-in under load drains the node through its command and starts it again.
- [x] A10 Kubernetes packaging through Aspire's own API, nothing in the framework: the samples' Kubernetes environment
      names its chart, release and namespace (`WithHelm`), and the MMO sample's stateful groups run on a node pool of
      their own (`AddNodePool`, `WithNodePool`: `nodeSelector: agentpool: stateful`). Installing KEDA or the Prometheus
      adapter is `AddHelmChart`'s, for an application that autoscales on custom metrics.
- [x] A11 Pipeline step: `aspire publish`/`deploy` first run the cluster's route check (`{cluster}-routes`, required by
      the publish and deploy prerequisites): each node project starts in check mode (`DaServer__ListRoutes`: it writes
      the messages its contracts declare and its actors and services handle, then exits before anything starts), and
      the step fails with the N4 problems of their union (`RouteRules`, the file `RouteCheck` uses). The listing is the
      node's own build, so it checks what the containers run; the analyzers already run in that build.
- [x] A12 Operator docs (`doc/operations.md`): run, watch (`aspire describe/logs/otel/wait`), the node commands, the
      admin endpoints with the derived token, the MCP server for agents, publish and deploy; every command checked
      against a running sample.
- Not for node membership: `WithReference` service discovery (`services__{group}__{endpoint}__N`: one load-balanced
  address per group; a Kubernetes Service lists ready pods only, and a joining node is not ready until it joined), and
  persistent project lifetimes (no replicas, fixed ports); fine for databases and user HTTP services. Seeds are the
  platform's DNS for each group instead (Compose service names, a headless `{group}-peers` Service listing every pod),
  as in Akka.NET's DNS discovery; Orleans keeps membership in a store instead (`WithClustering`), which DaServer's
  gossip does not need.
- [x] A14 Production views (`doc/operations.md`): a Grafana dashboard (`observability/grafana-dashboard.json`,
      imported into Grafana 12 and every query answered by Prometheus 3.5 scraping a node) and alert rules
      (`observability/prometheus-rules.yaml`, `promtool check rules`: 7 rules, all evaluating) over the metrics the
      nodes export; the cluster view from the admin `membership` command; orchestration state from the platform. Call
      latency has no metric: a histogram per call costs every call, and traces (A6) carry it where it is needed.
- Notes: the Aspire dashboard in deployments is diagnostic only (production observability is OTLP); Azure Container
  Apps exposes ingress, not per-replica addresses, so it is a later target than Kubernetes.

## Next: cluster hardening (C, before N5)
- [x] C1 Failure detection robust to pauses (ADR-064): heartbeats travel by gossip through any member (indirect by
      nature, suspicion before death); a round that runs late moves every member's progress clock forward by this
      process's pause, so its own GC or CPU stall declares nobody dead (`APauseOfThisProcessDeclaresNobodyDead`).
- [x] C2 Split brain (ADR-064): keep-majority downing in core, on by default (`NodeOptions.KeepMajority`): the
      minority side stops without saving and exits for a restart; ties keep the lowest node id; clusters under three
      members never down. Open: an optional authoritative lease store (Kubernetes Lease, PostgreSQL) as a module for
      strict ownership, with N5.
- [x] C3 Node authentication (ADR-065): Hello carries its link protocol levels (ADR-077), the cluster name and an HMAC of
      the signing secret over the acceptor's challenge and its endpoint; an accepted connection is read only after it
      (`LinkAuthTests`). Links are authenticated, not encrypted: TLS stays the platform's (service mesh, private
      network).
- [x] C4 Supervision (ADR-066): `[Actor<TId>(OnError = ...)]` is `ErrorAction.Resume` (default) or `Reload` (removed
      without saving, the next message loads it again); every failed handler, and every system pass that throws, is
      counted (`Node.Failed`, `daserver.handler.failed`).

## Next: versioning and rolling upgrades (V, before S)
- [x] V1 Wire manifest (ADR-076): each project commits `wire.manifest` (its types, their encodings and formats, the
      members their bytes hold, replies); DAS017 fails the build on a change the type's format does not tolerate
      (positional: any; version tolerant: anything but members appended at the end; keyed: a key's type), DAS018 asks
      to record the rest (`dotnet build -p:UpdateWireManifest=true`, `=force` for a deliberate break). Messages are
      positional, persisted components version tolerant or keyed (DAS014). The samples commit theirs.
- Nino 4.0.0-rc.3 resolved everything DaServer asked for (#197 layout metadata, now what the manifest reads; #198 a
  pool for `NinoArrayBufferWriter`, frames rent from the pinned `BufferPool`; #199 `NinoRegistry.Remove` for collectible
  contexts, for N9; #200 another assembly's auto-property struct raw in every build; #201 NINO026; #202 raw memory
  recorded field by field with offsets, `Pack`/`Size`/`[FieldOffset]` included, so the manifest adds no fields of its
  own).
- [x] V2 Build on the card (ADR-077): `ClusterMember.Build` (framework and application versions); a node logs while
      its view holds several builds.
- [x] V3 Messages only where the build has them (ADR-077): services (sent or forwarded) among nodes whose build
      handles the message; an actor's owner stays its type's, and a message its build lacks for that type is refused
      (`NotHosted`) instead of sent; a client message unknown to its node's build is `NotHosted` too.

## Next: packages and other core gaps (P, K)
- [x] P1 `DaServer.Testing` (ADR-071): `TestCluster` starts real nodes in the test process; `NodeOptions.TimeProvider`
      is the shards' clock (ticks, `SendAfter`, idle unloads, checkpoints, `world.Time` in handlers and systems);
      with `ManualTime` the test steps ticks (`TickAsync`, `AdvanceAsync`), each run once on every shard. The
      framework's `ClusterTest` builds on its node options; `Sample.Tests` tests the sample's servers with it.
- [x] K2 Slot table (ADR-074): actor keys (placements for `IPlaced` ids) hash into 4,096 slots; each view gives every
      slot of an actor type an owner among the type's nodes by rendezvous hashing, so finding a key's node is one
      table read; a node joining or leaving moves only the slots it wins or held. BDN `PlacementBenchmark` (one owner
      lookup): 3 nodes 7.98 -> 6.44 ns, 30 nodes 60.9 -> 6.45 ns, 300 nodes 558 -> 6.36 ns, 0 B. With N7, against
      N6 (three trees back to back): local calls 13.3 / 174 / 1,317 µs -> 11.1 / 171 / 1,316 µs (1 / 64 / 1,024 in
      flight), one-way messages 428 / 748 ns -> 426 / 769 ns (local / remote), allocations unchanged. Two earlier
      runs that ran the new tree second showed local calls 10-15% slower; run order on this VM, not the change.
- K1 placement strategies (agreed: Akka sharding style, ADR-074): no per-actor directory and no "prefer the caller's
  node" (`IPlaced` co-locates what talks); load-aware and sticky placement move whole slots and are S3.
- [x] K3 Load-aware service choice: a caller not hosting the service sends to the less loaded of two hosting nodes
      (power of two choices over the queue length each member gossips), the first of the two in turn, so equal loads
      stay evenly spread and a node with a backlog gets nothing while another is idler (`ServiceCallsGoToTheLessLoadedOfTwoNodes`).
- [x] K4 `CallOptions` (agreed: a struct overload): every generated `Ask` has an overload taking `in CallOptions`, whose
      `Timeout` bounds that call (zero: the sender's `CallTimeout`), on nodes and clients; the deadline travels as before
      (ADR-028). Handlers read when their caller stops waiting as `ctx.Deadline`, without a `CancellationTokenSource`
      (`CallOptionsTests`).
- [x] K5 `[Actor<TId>(MailboxCapacity = n)]` (an actor type's own bound; zero: the node's `ActorMailboxCapacity`,
      read from the type's settings when mail is queued) and `daserver.messages.dropped{reason}` (`queue_full`, `mailbox_full`,
      `expired`; `Node.Dropped(DropReason)`), replacing `daserver.shard.rejected` and `daserver.shard.expired`
      (`AnActorTypeSetsItsOwnMailboxCapacity`; replies and completions have their own queue, ADR-027).
- Out of scope in core: durable streams (use Kafka/NATS from a service), transactions (a handler is atomic; sagas are
  messages plus persistence), distributed locks (an actor is the lock), multi-tenancy, event sourcing (documented
  pattern only).

## Next: elastic scaling (after N5/N6: moving stateful actors needs persistence and handoff)

DaServer does not write an autoscaler. The platform decides replica counts (Kubernetes HPA, KEDA, Azure Container
Apps scale rules); Aspire only generates the manifests. The framework's job is that adding or removing a node of any
kind is safe and cheap, that it exports the signals to scale on, and that the generated manifests are correct.

What is stateful is decided by code, not by a role: a node hosting actors is stateful, a node hosting only services is
stateless. In the MMO sample the login server is stateless; gateways hold connections (soft state:
clients resume on another gateway with their ticket); the game server (players, realm) and the map servers are
stateful, maps being heavy actors.

- [x] S1 Scale signals: per-node OpenTelemetry metrics fit for HPA/KEDA (dispatch queue depth, mailbox backlog, loaded
      actors, connections, `daserver.shard.busy_ratio` from the shards' idle ticks, `daserver.shard.tick_overruns`),
      scraped from a Prometheus `/metrics` endpoint on the HTTP plane by default (OTLP when configured); the default
      scaling metric is the busy ratio (design.md, metrics). `HttpPlaneTests.MetricsAreScrapedFromTheHttpPlane`.
- [x] S2 Scale-in is a drain: SIGTERM -> readiness off -> graceful Leave -> actors hand off (N6) or unload and save
      (N5) -> exit within the termination grace period (D1-D3, the preStop pause first); gateways retire their clients
      in waves (make before break, ADR-026). `HandoffTests`, `ClientsOfALeavingGatewayMoveAndResumeOnAnother`.
- [x] S3 Slot allocation (ADR-079): the coordinator (oldest Up member by the card's `UpSince`) keeps a versioned slot
      table it sends to every member and on every new link; nodes report their loaded slots per actor type when they
      change; `[Sticky]` types keep loaded slots where they are (a join moves only empty slots: new keys go to the new
      node, a map with players stays); other types are evened out by load, up to two slots per round from the most to
      the least loaded node (handoff). A new coordinator drops its earlier term's reports and plans once every member
      reported (a stale report of its own once unpinned loaded slots for a round). Steady rounds allocate nothing
      (`AllocationTests` caught 16 KB of counts per type per round). `SlotAllocationTests`: sticky across a join, an
      imbalance evened out, a coordinator replaced with its pins.
- [x] S4 Generated manifests. Done: `DaServer.Aspire` adds only what follows from DaServer (probes, grace period, the
      preStop wait, one workload per group with its peers Service, client ports as external endpoints); scaling policy
      is each application's, in its AppHost (`Platform.cs`): the MMO sample's gateways behind a LoadBalancer, PDBs and
      in-place VPAs for game and map; the flash sale's sale nodes under an HPA on CPU and a VPA in recommendation
      mode. Plain Deployments: identity comes from gossip incarnations and state from the store, no StatefulSet.
      Publish tests (`PublishTests`, E2E): both samples in both environments, the Compose services' health checks,
      grace periods and healthy dependencies, the chart's probes, preStop and peers Services, `helm lint` and
      `helm template` on the chart. An HPA on the shards' busy ratio (exported, S1) needs KEDA or the Prometheus
      adapter in the cluster, so it is the application's (`doc/operations.md`); the flash sale scales on CPU.
- [x] S5 Vertical scaling: shards follow the CPU limit (`Environment.ProcessorCount` honors the cgroup quota and
      `DOTNET_PROCESSOR_COUNT`; documented on `NodeOptions.Shards`). A VPA is the
      application's: in place where it can (the mmo sample: CPU resized in place, a memory change restarts the
      container so the GC heap limit follows; an eviction is a drain and a rejoin), recommendations only where a
      restart must wait (the flash sale). The node reads its limits at start.
- [x] S6 Prove it: `SampleClusterTests.AStatefulGroupScalesInAndOutUnderLoad` runs the mmo AppHost with two game
      nodes and 1,000 bots; one game node leaves while they play and joins again: no gap, no failed map change, no
      disconnect, p99 31 ms. It found what the in-process tests could not: the sample's player and realm state did not
      move with their actors (now `[Handoff]`), and the realm's status sequence was the hosting shard's tick (now the
      realm's own). The 10k-bot run with a scale step belongs to the release checklist.

## Next: architecture borrowed from in-house game servers, ET and Orleans (agreed scope; order: the development order above)

Patterns only, never business code. Each item: tests, BenchmarkDotNet where a hot path changes, an ADR, logic and docs
in separate commits.

- [x] T2 ECS bots and business isolation (ADR-055, ADR-056): `Sample.Shared` (rules both sides run; DAS010 keeps the
      server and the transport out), `Sample.Client` (the game client as ECS data and systems), `Sample.Bot` (that
      client as a `DaServer.Simulation` swarm with an HTN behavior: change maps, chat, wander), `Sample.Tests`; the
      e2e test changes maps every 5 s. `DaServer.Simulation`: `ClientLoop`, HTN planning as ECS data, `Swarm`/`SwarmMeter`.
- [ ] Optional sample: server-side NPCs (patrol, chase, flee) planned with the same HTN model in a map server, to
      show heavy AI batched on shards without allocation.
- [x] N1 Components apart from logic (ADR-067): an application project on the server runtime declares no component
      or singleton (DAS013) and its systems hold no fields (DAS007); components live in `X.Components` projects
      (contracts and EasyEcs only), which a reload never replaces. Shared logic is exempt (clients run it, it never
      reloads); the node project is the reloadable logic itself (no separate host project). Samples, tests and
      benchmarks follow it. (game servers' server/hot-reload split, ET Model/Hotfix)
- [x] N2 `SendAfter(delay, address, message)` / `CancelSend(timer)` (ADR-058): per-shard timer wheel of serialized
      frames (`ShardTimers`, `TimerSystem`), delivered as ordinary mail when due (follows moved keys), 5 ms resolution,
      zero allocation (`DelayedSendsDoNotAllocate`, `TimerBenchmark`); DAS009 of N3b points `await Task.Delay` to it.
      (ET timers as data, Orleans timers)
- [x] N3 Call cycles: handlers never wait (ADR-081), so actors asking each other in a chain or a cycle (A asks B asks
      A, an actor asking itself, through a service, across nodes) complete (`CallChainTests`).
- [x] N3b No sync-over-async on shard threads (agreed: all four layers):
      1. Done: DAS009 (error) in actors, services and systems: `.Result`, `.Wait()`, `GetAwaiter().GetResult()`,
         `AsTask().Wait*`, `Thread.Sleep`, `Task.Delay`, `SemaphoreSlim.Wait`, `Monitor.Enter`/`lock` waits; the
         message names the replacement for the context.
      2. Done (ADR-061, ADR-081): handlers never wait (an ask's answer is mail); delays use `SendAfter` (N2); systems
         never wait for replies (DAS012): in their own world they query and `ref` components directly, other entities'
         through `Entity` handles (N7 placement affinity puts what is read together in one world); data in another
         world gets an intent, validated by its owner, whose outcome comes back as a message.
      3. Done: reading a pooled call's result before completion throws; a stall watchdog reports a shard pass longer
         than `NodeOptions.StallAfter` (1 s) once per pass, in the log and as `Node.StalledShards`
         (`daserver.shard.stalled`); the `daserver-live` health check (A-items) reads it. A managed thread cannot
         read another thread's stack, so the log names the shard, not the blocking frame.
      4. Done (ADR-062): `Ask` from outside the shards returns `Reply<T>`, await-only (no `.Result`/`.Wait`;
         `AsTask()` for combinators); an `async Reply<T>` method keeps its state machine in a box pooled per thread
         and method, so awaiting allocates nothing.
- [x] N4 Route completeness (`DaServer.Testing.RouteCheck`): given a cluster's node projects, every message their
      contracts declare has an actor or service handling it (`[ToClient]`-only messages excepted) and no request is
      answered by two service types; both samples check theirs in a test (`EveryMessageHasItsHandler`,
      `RouteCheckTests`). Running it before an AppHost publishes is A11. (Orleans finds these at compile time.)
- [x] N7 Placement affinity (ADR-073): an actor id type implementing `IPlaced` is hashed by its `Placement` (a pure
      function of its value) instead of its value, in group 0: actors of any type with the same placement share a
      node and a shard. The wire is unchanged; the registration of the assembly declaring the id type registers it;
      DAS016 reports `IPlaced` on anything but an actor id type.
- [x] N5 Persistence as ECS (ADR-068): `[Persist]` components of an actor form one record (a section per component
      type id); `LoadSystem` batches the loads of a wake and restores components before the actor's `Loaded` mail;
      `CheckpointSystem` writes changed records every `SaveEverySeconds` (30 s; change-tracked, so only actors whose
      stored components were written are visited), `PersistSystem` writes
      `SaveNow` and unloaded actors (removed once written); `StoreSystem` keeps one load and one save batch in flight
      per shard; a rejected versioned write fences the local copy; `IActorStore`, `InMemoryActorStore` and
      `PostgreSqlActorStore` (`DaServer.Persistence.PostgreSql`: a row per actor, one round trip per batch); DAS014;
      `PersistenceBenchmark`. Relational business
      data (accounts, payments, mail, auctions) stays in the user's ORM inside services. (game-server write-behind, ET
      serialized components, Orleans IPersistentState + ETags)
- [x] Z7 A/B against d676d2a on one VM (default job, both built here; the VM is slower than when README numbers were
      taken, so only same-machine pairs compare): calls 26-74% faster (1 local call 60.1 -> 16.5 us, 1,024 local
      5,435 -> 1,391 us), one-way messages 54-88% faster (local 5.26 us -> 0.64 us), allocations equal or lower. Group
      broadcasts measured +1% to +32% and the broadcast scenario +7% / +13%, but repeated runs of one build vary by
      +-25% on this VM (35-46 ms for the same 5,000 x 9 case); a bisection on single runs pointed at a commit that two
      interleaved runs then cleared. Broadcast work goes to Z5.
- [ ] Z6 .NET 11 / C# 15 (audit of October 2026, probes on RC2, Roslyn 5.12): done: runtime async on for net11.0
      builds, net10.0 builds dropped, `Reply<T>` kept as the awaitable of callers outside the shards (as fast as
      runtime async, 0 B suspended; ADR-054),
      C# 14 extension members for the server-only `World` API (`world.Tick`, `world.Cluster`). Open: Zstandard span
      codecs (`ZstandardEncoder`) for large actor records, measured with BDN. Not worth it now: interceptors (the generated
      `Ask` overloads already avoid the generic hop), unions (they box struct cases), closed hierarchies (net11-only
      attributes), a generated `AddSystems` (the order of systems is the user's to choose). Already in effect without
      changes: JIT bounds-check and devirtualization work, continuations that skip `ExecutionContext` restore when
      nothing flows.
- [ ] N12 Nino (audit of October 2026; Nino's reads check every length, bound depth and counts): done: enum members in
      `wire.manifest` (a member removed, renamed or renumbered is DAS017), pinned bytes for raw structs, strings,
      arrays and the framework's version-tolerant frames (`WireBytesTests`), DAS019 for collections held directly in
      `[FromClient]`/`[ToClient]` messages, `Registrations.Run` clean under AOT analysis. On 4.0.0-rc.6: Latin-1 strings
      one byte a char (link level 3, client level 5: a full restart), the manifest in Nino's byte descriptions with its
      layout text named in its header and keyed members matched by key, padded wire structs packed, `[NinoUtf8]` kept
      for chat only, the registration's module initializer only where one is usable (Nino's own Unity fix), `System.Type`
      refused as a wire type, Nino.AspNetCore bodies on the HTTP plane (ADR-046). Next: `GetFixedSize` for fixed-size frames (only if BDN
      says so), `IsSupported` checked at start. Open: release on Nino 4.0.0, not an RC (drop the NU5104 suppression then).
- [x] N6 State handoff on moves (ADR-072): an actor that changes node (scaling out or in, a node stopping) sends its
      [Persist] and [Handoff] components, version and last-written hash to the new owner on the link its forwarded
      mail takes next; the new owner restores them without reading the store, the old one is removed once accepted
      without writing. A new owner that gets mail first asks the previous owner and waits; refusals, silence and gone
      nodes fall back to the store. Scaling a 40-actor node out and in: 0 store reads, 0 writes. Also fixed: an actor
      unloaded and waiting for its final write no longer runs mail (it waits and moves with the actor).
- [x] N8 Collections in messages without garbage (ADR-078): a message that holds a collection holds it in a
      `[NinoPooled]` class its handlers return (`Moves` in `Motion`); each handler deserializes its own copy, so
      fan-out to several handlers and bursts held at once (200 per thread) allocate 0 B per push (Sample.Tests
      `ContractTests`). Nino's default pool (64 per thread, 256 shared) needs no sizing for a bot loop. Text the
      clients send and receive takes Nino's default (one byte a char for ASCII names); chat (`Say`, `Chat`) is
      `[NinoUtf8]`, as chat mixes ASCII with emoji and other scripts.
- [ ] N9 Hot restart (after R, on its model: draft §10; JIT and AOT alike, ADR-083): code changes replace a node's
      process in place, the world handed over (the runtime side is R6). Here: the admin command and the AppHost command
      (the new build's `wire.manifest` in, the path taken out: in place, through records, or refused for a rolling
      upgrade), node groups restarted one node at a time, the Kubernetes path (the running pod's image changed in place,
      a memory-backed `emptyDir` for the snapshot, an update strategy that does not replace pods) through the publisher
      hooks, the service-manager note (systemd `MAINPID` handover or a launcher). Changes without a restart as data
      swaps between two ticks: tuning tables (validated snapshot, cluster-wide trigger, version on the member card) and
      feature flags choosing between handlers compiled in. Gateways take rolling upgrades; socket handoff
      (`SCM_RIGHTS`, Unix only) stays a possible later optimization. (in-house game servers, ET, Envoy hot restart)
- [x] N11 Declared filters: `[Filter]` on a static `RpcStatus M(in Context context, in TMessage message)` (that
      message type) or on an `IMessageFilter` class (every message) is registered by the assembly's
      generated registration: rate limits, authorization and auditing without startup code; DAS021 for a wrong shape
      (`FiltersAreRegisteredWithTheAssembly`, `AFilterAttributeRegistersItself`). Registered rather than inlined into
      the dispatchers: a filter in another assembly (shared rules) applies too, and a message without filters still
      costs two length checks. (Orleans call filters)
- Not adopted: Orleans interface proxies, per-activation Task scheduling, grain directory / repartitioning,
  transactions; ET location server, coroutine locks, class entity trees, global singletons; game-server stop-the-world
  reload with forced GC, manual dirty marking. GM commands (N10) are out of scope for now.

## Edge cases (audit of October 2026: each found case gets a deterministic test before its fix)

Four read-only audits (moves and placement, membership and links, client sessions, persistence and the actor
lifecycle) listed the
interleavings a real network leaves to chance. Each is forced in a test (a gated store, a scripted node, crafted views
and cards, a manual clock), fails before its fix, and passes after it.

- [x] Client moves (`ClientMoveTests`, `ScriptedNode`): a move between a sender's pick and its send; calls of a left
      connection fail when its node dies; a refused resume; a second Retire; the old connection kept until its calls
      are answered; eight senders across twenty moves.
- [x] Slot planner (`SlotPlannerTests`): takeover without reports, a left node, sticky pins, a slot reported twice,
      moves until rendezvous agrees, the threshold and the move limit, determinism.
- [x] P1 A checkpoint in flight as the actor unloads was taken for its final save (later changes lost).
- [x] P2 A failed handler's SaveNow (and checkpoints of fenced actors) wrote half-changed state; a fenced unload handed
      off or wrote its stale copy.
- [x] P3 A fenced actor dropped while its write was in flight (the reload could read before it): `PersistenceEdgeTests`.
- [x] P4 A stale owner writing after the new owner loaded fenced the new owner: loading claims the record (its version
      goes up), so the stale copy is refused (`ANewOwnerClaimsTheRecordSoTheStaleOneCannotWriteOverIt`,
      `AClaimFencesWhoeverLoadedTheRecordBefore`).
- [x] P5 A failed write waited for the next checkpoint: it is retried after the delay (`AFailedCriticalSaveIsRetriedSoon`);
      P7 loads go in batches of at most 512 (`LoadsWaitingForASlowStoreGoInBoundedBatches`).
- [x] P6 A failed load failed the batch's callers at once: retried after 200 ms until the call timeout
      (`ALoadThatFailsIsRetriedWithinTheCallTimeout`).
- [x] P8 An actor's lifecycle handler asking back the actor whose request loads it waited for the call timeout: with
      answers as mail (ADR-081) it is answered (`ACreatedHandlerAskingTheActorWhoseRequestLoadsItIsAnswered`).
- [x] P9 A store call that never finished held its shard's loads or writes for good (one batch in flight each): store calls
      are cancelled after `NodeOptions.StoreTimeout` (30 s) and retried like any failure, with one reusable token source
      per batch (`AHungLoadIsCancelledAfterTheStoreTimeoutAndRetried`, `AHungWriteIsCancelledAfterTheStoreTimeoutAndRetried`:
      held for good before).
- [x] H1 The new owner stopped waiting for the record after the call timeout while the old owner still ran the actor
      (duplicate activation): it asks again while the previous owner is alive and loads only once it is dead
      (`ANewOwnerWaitsForTheRecordWhileThePreviousOneHoldsTheActor`).
- [x] H2 "None" from a node that still owned the key in its own view, which then loaded it (two copies while views
      converge): it keeps the request until ownership changes (`ANodeThatStillOwnsAKeyDoesNotAnswerNone`). H12 a node
      loading right after another joined asked the newcomer, which never held the key, and waited on it
      (`ANodeLoadingRightAfterAnotherJoinedDoesNotWaitForIt`).
- [x] H3/H7/H8 A node kept one previous view, and a record refused for a key that moved on was lost without a store:
      nodes keep the views they replaced within the move window; a node that took a key moments ago defers "none"
      and asks the node it took it from; a record whose key moved on goes on once, to the asker or the owner
      (`ARecordThatArrivesAfterItsKeyMovedOnFollowsIt`, `ANodeThatHeldAKeyBrieflyWaitsForItsPreviousOwner`).
- [x] H4 The old owner timed out and wrote, then the new owner's late accept arrived: it waits while the new owner
      is alive (`ALateAnswerFromALiveNewOwnerIsWaitedFor`).
- [x] H5 A pending SaveNow is written after the record was sent (`Without<SaveNow>` on the handoff).
- [x] H6 User `[ForEach]` systems ran on actors that are not live (the mmo realm broadcast one status twice from
      both copies): an actor's state exists only while it is live; a copy that sent its record gives it up (restored
      if the handoff falls back), a new copy gets it when its record is restored (`NoSystemSeesACopyThatHandedItsStateOff`).
- [x] Handoff answers were dropped by a node that just joined (its view did not list the sender yet): sends use the
      link a node introduced itself on (the flake of `ScalingOutAndInMovesRecordsWithoutTheStore`, traced).
- [x] H11 Mail admitted to a stopped shard waited until it timed out: it goes on to the key's owner, and handoff frames
      are answered (none, refused) so the other side falls back at once (`MailForAStoppedShardGoesOnToTheKeysOwner`).
- [x] H9 A delayed-send handle kept across a move cancelled another timer on the new node (every wheel started at
      generation zero): handles carry a random salt per wheel (`AHandleCancelsOnlyOnTheWheelThatMadeIt`).
- [x] M1 A node restarting on the same address with a new id: the older of two live members at one address is dead at
      once, a node's own address holds only itself, a dead id resets a link only when the link's stream is its own
      (`ANodeRestartedAtTheSameAddressLosesNoCallWhenItsOldIdIsDeclaredDead`: a call timed out on every run before).
- [x] M2 Forgotten members came back through later nodes' gossip for good: forgotten ids are kept for ten DeadAfter,
      unknown Dead or Leaving members are not learned (`AForgottenMemberDoesNotComeBack`).
- [x] M3 A pause past DeadAfter fences the node (its actors go without a write, a new incarnation)
      (`APauseLongerThanDeadAfterFencesThisNode`).
- [x] M4/H10 Two coordinators wrote one table version: the view's coordinator's wins, a coordinator publishes above
      every version it saw (`TablesOfOneVersionFromTwoCoordinatorsConverge`).
- [x] M5 A client-facing node falsely declared dead then refuting had its sessions closed after the grace: skipped when
      it is back (`AClientFacingNodeFalselyDeclaredDeadKeepsItsSessions`).
- [x] M6 Actors already asked to leave, unloading or holding a final save were not fenced by a later Dead verdict (they
      unloaded as planned and wrote): every copy not yet removed is fenced (`AnActorAskedToLeaveIsFencedWhenItsNodeIsDeclaredDead`).
- [x] M7 A process late on every tick credited every gap to the others' clocks (a dead member kept its keys): the next
      pause is excused no sooner than DeadAfter and twice this one later, so the others age at half speed or more
      however late the ticks are (with DeadAfter alone, ticks late by nearly DeadAfter under a loaded machine excused
      nearly everything and the test failed in a full run; it now pauses 1.35 s of 1.5) (`ANodeLateOnEveryTickStillDeclaresTheDeadDead`). M10 a worse status at an older heartbeat
      lowered the heartbeat, so stale pulses counted as progress: the newest is kept (`AStalePulseDoesNotLowerAHeartbeat`).
      M11 introductions were kept for nodes that left: dropped once the view lists them (`ANodeThatLeftIsForgottenEverywhere`).
- [x] M9 A seed spelled otherwise than the member's card opened a second link to the node, and the two took its inbound
      slot from each other: the link reaching a member serves its card's address too (`ASeedSpelledOtherwiseIsOneLink`).
- [x] M8 A recorded Hello could be replayed to another node within its minute, and its endpoint was not signed: the
      acceptor challenges, the Hello proves the challenge and its endpoint (`AHelloProvesItsChallengeEndpointStreamAndCluster`).
- [x] S1 An actor that moved forgot its client sessions (resume refused, the mmo's map changes Forbidden): sessions
      travel in the handoff record, never stored (`AClientResumesWithAnActorThatMovedToAnotherNode`).
- [x] S2 A resume cut off after the actors accepted it (the client gave up waiting behind a backlog, a drain closed the
      new connection) left the session unrecoverable: the client-facing node reverts it (a resume back to the previous
      session, which the actors restore without telling their handler), a client retries on `Superseded`, and drains and
      connection ages skip sessions mid-resume (`AResumeCutOffAfterTheActorsAcceptedItIsRetried`).
- [x] S3 Calls on the old connection are failed when it dies while the move is still resuming; S4 a dispose during a
      reconnect or move says goodbye and keeps the client out of the sweep; S5 a probe only closes the connection it
      pinged; S8 a reconnect attempt ends with ReconnectTimeout (`ClientMoveTests`, all failing before).
- [x] S6 A move's two connections dispatched pushes from two loops (callbacks overlapped, a new session's push could
      come before an older one of the session left), and the actor's group broadcasts reached both sessions until the
      goodbye: pushes are one connection at a time and the new one's wait until the old drained
      (`PushesOfTheConnectionLeftComeBeforeThoseOfTheNewOne`); an actor taking a resumed session makes the previous one
      leave its group, ahead of its later broadcasts (`AResumedSessionTakesTheActorsGroupFromThePreviousOne`).
- [x] S7 A kick was evaded by a resume waiting behind the kicking handler (accepted, and the close then filtered as
      superseded): actors kick with `DisconnectSession`, which withdraws the grant at once, so the resume is refused
      (DAS020 for `Send(session, new Disconnect())` in actors; `AKickIsNotEvadedByAResumeWaitingBehindIt`). S9 One ticket
      presented on two gateways at once could resume the player on one and the room on the other: the first actor that
      takes the session decides, asked alone, and the other resume stops at its `Superseded`
      (`ATicketPresentedOnTwoGatewaysAtOnceResumesOnOne`). S10 A close or resume for a moving actor was refused: one
      sent on a stale view is forwarded, one reaching the new owner before the handoff record waits behind it, and an
      actor without a `SessionClosed` handler still forgets the session (`ACloseSentOnAStaleViewIsForwardedToTheOwner`,
      `ACloseThatReachesTheNewOwnerBeforeTheRecordIsNotLost`, `AResumeThatReachesTheNewOwnerBeforeTheRecordKeepsTheGrant`,
      `AnActorThatHandlesNoCloseStillForgetsAClosedSession`).
- [x] Fail fast: a test waiting on a condition stops at the first fault the cluster logs (a crashed or stalled shard, a
      failed maintenance tick or gossip round, a refused link, a stop past its budget, anything critical), naming it,
      instead of at its time limit (`FaultLog`); the E2E runs stop at the first resource that fails to start, exits or
      turns unhealthy, with its last lines (`Watchdog`). It found a refused node reconnecting thousands of times a second:
      a closed handshake now backs off (100 ms to 5 s).
- [x] P11 The PostgreSQL table was created by a store's first call (its readiness said nothing of it, and a failed create
      failed the first loads): the node initializes its store before it starts, and `createTable: false` leaves the
      schema to a migration job, checked at start (`AStoreThatDoesNotCreateItsTableRefusesToStartWithoutIt`).
- [x] N12a A payload that is not its message is refused (`Malformed`) before the handler runs: no failure counted, no
      `OnError` action (`AMessageThatCannotBeReadIsRefusedWithoutFailingTheActor`).
- [x] P10 Nodes starting together raced on `CREATE TABLE IF NOT EXISTS` (23505, the first loads failed): an advisory
      lock serializes the create (`NodesStartingTogetherCreateTheTableOnce`).

## Open cleanup (review rounds)
- Audit round 2 (agreed; each change with tests and BenchmarkDotNet where a hot path changes):
  - [x] Cache lines: a shard's per-pass counters and its sleeping flag on lines of their own, its own counters
        without `Interlocked`, the actor count written when it changes; channels touch their activity stamps at most
        every 100 ms; `DeferredReply.Used` written once.
  - [x] Pools and the call table: per-thread magazines in `ObjectPool` (the depot's cursors touched once per 32
        objects); call table entries 32 B (int tag, the deadline as the low 32 bits of `Platform.Milliseconds`, passed
        in absolute: one clock read less per call). `PoolBenchmark` (1/4/16 concurrent threads) before: frames 33 /
        189 / 198 ns, calls 41 / 230 / 232 ns; after: frames 24 / 12 / 11 ns, calls 43 / 173 / 162 ns, nothing
        allocated.
  - [x] The call table's free stack, the one CAS every call and reply contended on, in stripes (a node: one per core
        up to 16; a client: one): a thread frees onto its own stripe and takes from it first, then from the others, so
        the table grows no more than with one stack. `PoolBenchmark.Calls` (1/4/16 threads): 41 / 260 / 241 ns before,
        42 / 36 / 33 ns after; end to end (`RpcBenchmark`) within run-to-run noise, nothing allocated.
  - [x] Group fan-out: a channel array next to the members; `FrameChannel`, the shell (send, close, tag, stamps)
        shared by TCP and WebSocket.
  - [x] Routing: one route per handler type (nodes, kind, slot owners) found by one lookup.
  - [x] Small: the flusher's registrations in the thread's send batch (one lock per flusher and batch); the swarm's
        latency histogram striped by thread with log-linear buckets (1,024, at most 3% wide); the link's received
        counter on a line of its own.
  - [x] Clocks: resume graces on `NodeOptions.TimeProvider` (steppable in `TestCluster`); call deadlines, membership,
        keep-alives and rate limits on `Platform.Milliseconds` (real time, one clock).
  - [x] One backoff with full jitter (client reconnect, node links, swarm retries).
  - [x] Generated registration uses `TypeId<T>` (no literal ids); a test pins the analyzer's ids to Nino's (nested,
        namespaced, `[NinoFormerName]`).
  - [x] One host:port parser (`HostPort`, IPv6 in brackets); `[LoggerMessage]` on message and actor failures, the
        dropped-backlog warning at most every 10 s with a count.
- Audit round 3 (agreed):
  - [x] Planned reconnects make before they break (`ControlOp.Retire`, ADR-026): at
        `MaxConnectionAge` or a drain the node asks the client to move; it resumes on a new connection while the old one
        keeps working, then leaves the old one once its calls are answered; the node closes it after `RetireGrace`
        whatever the client does. `ResumeTests.AConnectionRetiredForItsAgeLosesNoPush`: every call answered and every
        push received across several moves.
  - [x] mmo: a VPA in `InPlaceOrRecreate` for game and map nodes, CPU resized in place, a memory change restarting
        the container (.NET reads its limits at start: the GC heap limit follows), one at a time under their PDBs;
        flashsale keeps recommendations only (nothing restarts during a sale).
  - [x] The actors' side of sessions stays in the runtime (decided against moving it into `Clients/`): nodes that only
        host actors run no client feature yet must close the sessions of a gone client-facing node, so that tracking
        is the runtime's (`SessionNodes`, out of `Node`); `ResumeGrace` and the signing secret serve both sides.
  - [x] One clock name: `Platform.Milliseconds` in the runtime, `nowMs` passed down from `Maintain`; one deadline
        helper (`PendingCallTable.DeadlineAfter`); every runtime log a `[LoggerMessage]` in `Log`; the stats line
        reads the node's gauges.
  - [x] Gossip rounds on the node's maintenance tick (one timer per node, one pause rule: the silence clock).
  - [x] PostgreSQL store: a pooled connection per batch (Npgsql keeps prepared statements per physical connection,
        prunes and replaces connections); only the parameter lists are reused.
  - [x] Samples share `samples/Shared/Publishing.cs` (the Compose/Kubernetes switch, `Manifest`, the VPA). The HPA
        stays plain data: Aspire's typed HPA writes empty metric sources Kubernetes rejects.
  - [x] Node options validated at construction (every problem in one message); one polling helper (`Poll`).
  - Declined: constants shared between `DaServer.Aspire` and the node (it would make the AppHost package
        depend on the node's, Nino included); the stop budget's defaults are documented on both sides instead.
  - Declined: client entry addresses through `IConfiguration`. Aspire passes them as `DaServer__ClientEndpoints__{i}`,
        which is the configuration key `DaServer:ClientEndpoints` already: a hosted caller binds it with its own
        configuration, the swarms read it with `Endpoints.FromEnvironment` (no configuration dependency in the
        simulation), and game clients (Unity) take addresses from the application.
  - [x] Files over ~400 lines split by concern: `ClientConnections` (+ Sessions, Groups, Resume), `ClusterClient` (+
        Connection, Resume), `Membership` (+ Gossip), `PendingCallTable` / `PendingCall`, `Connection` (+ Flushed),
        `Node` (+ Stop, Maintenance).
  - [x] The pools documented by who returns: `PerThread` (the renting thread), `ObjectPool` (another thread), a shard's
        `AnswerCalls` singleton (per-shard state, as ECS wants: not moved to `PerThread`).
  - [x] Docs: the stale statements (client redirect remnants, analyzer range DAS001-018, ADR-015/036, design §11
        publishing, READMEs on `Publish=k8s`, flashsale in samples/README, R11's bot options, CLAUDE.md layout,
        roadmap states).
- [x] Deployment: one AppHost publishes to Docker Compose or Kubernetes (`Publish=compose|k8s`); in publish
  mode a node group is one workload with replicas (A4): no container names, no fixed host ports per replica, seeds
  through the group's DNS name (Compose service DNS, Kubernetes headless Service), each replica advertising its own
  address (ADR-030); both samples, each adding its own platform objects (mmo: a load balancer for the gateways,
  disruption budgets and in-place VPAs for game and map; flashsale: an HPA, a VPA recommending requests). No StatefulSet (identity from gossip incarnations, state in the store), no etcd or
  Kubernetes API access (DNS seeds only); secrets are Aspire secret parameters (a Kubernetes Secret, a Compose
  `.env`); a migration Job is the application's.
- [x] Nino rc.4: `FrameWriter.Write<T>(in T)` over `NinoSerializer.Serialize<T>(in T, IBufferWriter<byte>)` (no copy of
      a large struct per write). BDN, rc.3 -> rc.4 on one machine: Message local 624 -> 601 ns, remote 960 -> 910 ns
      (0 B both); Broadcast within noise (1,000 clients 5.96 -> 6.00 ms, 5,000 clients 33.2 -> 34.0 ms); Persistence
      within noise, two interleaved runs each (Load memory 2.01/2.71 -> 1.91/2.14 ms, SaveNow PostgreSQL
      9.9/12.3 -> 10.5/10.7 ms). Load allocates per actor it activates (its record, index growth): activation is the
      cold path, not counted against the 0-GC thresholds.
- [x] Kestrel's raw TCP evaluated, not adopted: a `PipeChannel` over a Kestrel connection (`ListenOptions.Run`)
      feeding the same client connections (`TransportBenchmark`, kept to measure again). Broadcast to 1,000 clients
      equal (5.86 / 5.84 ms); to 5,000 clients 26.2 ms and 55 KB against 43.7 ms and 14.8 MB with Gen2 collections
      (each connection's pipe grows blocks, each batch awaits a flush); 64 concurrent client requests 29-30 against
      34 us. Both time and allocation regress, so the own `Connection`/`Listener` stay; TLS stays the platform's.
- [x] B4 one serialize-into-pooled-frame helper (`MessageWriter.Write`, replies included); B8 one pending-table scan
      (timeouts, a link's calls, all calls); B9 one add-unless-registered for handler types, systems and persisted
      components; B10 one mix (`ClusterView.Weight`) for slots, rendezvous and shards. BDN `RpcBenchmark` (short job,
      three interleaved runs each): one local call 4.25-4.62 us before, 4.67-5.19 us after, a run without B4 5.30 us
      (the machine's drift); 0 B both. Kept: B5, the local branches deliver to different places (an actor's shard, an
      address's feature) and the group fan-out holds the member already, so one helper saves no line.
- [x] B11 sample review: a queued flash sale admitted position 0 (never issued) and so let any buyer skip the queue
      (`AQueuedSaleAdmitsBuyersAtItsPace` now asks for it); a bot whose map change outlasted `TravelEvery` started a second one
      (the task carries its destination: started is data); maps are `[Sticky]` as ADR-079 describes; a late buyer's
      answer is a binary search over the sorted buyers; stale comments (the realm's tick count, the map's size, the
      tests that run a cluster), unused usings, one Deployment reference for both samples' autoscalers. Kept: the mmo
      sample's `Sample.*` names (renaming touches every project, the E2E tests and the published charts, for no
      behavior).
- [x] Split `Node` into links and router; membership talks to the node through an interface: links (handshake, one
      link per node) in `Node.Links.cs`, routing in `Node.Routing.cs`; `Membership` sees only `IMembershipHost`
      (identity, options, links, what to advertise and pulse, view changed, fenced, downed).
- [x] A handoff answer was dropped when the sender joined moments before: the shard read a view not listing it yet,
      gossip then published one that did and dropped its introduction, and the second read found neither. The old
      owner kept waiting on its copy; when the key came back it refused the record, and both sides fell back to the
      store (`ScalingOutAndInMovesRecordsWithoutTheStore`), or a stopping node waited out its budget for the answer
      (`WithoutAStoreStateStillMoves`). `LinkTo` reads the view again after a missed introduction (one lookup for
      handoffs and session operations). Under four parallel test runs on four cores: 2-6 failures per 48 runs before,
      0 in 192 after.
- [x] Test clusters had one name (`daserver`): a node taking a port an earlier test's node just released could be
      gossiped with by that test's stopping nodes and route keys to them (the suspect of a NotHosted in
      `AReloadWaitsForTheWriteInFlight`, CI run 39099216040). `TestNodes` gives each cluster a name of its own.
- [x] `ResendWindowsRecycleTheirOwnChunks` read the process-wide pool counter, which parallel tests and the pool's
      trimming move: it counts the window's own rents from the shared pool.
- Kept on purpose: B6 (local replies hand the frame over without a copy), B12 (the shard checks runnable actors after
  tick systems sent local mail)
- [x] Self-review round 1 after the release checklist (three independent read-only reviews: distributed correctness,
      hot paths, code and docs; each finding checked against the code before acting):
  - A handoff could stall until a node died when its record or answer was lost on a live link (a link that restarted
    its stream, a backlog past its bound): the previous owner sends the record again when the new owner asks or the
    answer is late while it lives, and the new owner answers "accepted" again for a record it already took
    (`HandedFrom`). `ALostRecordGoesAgainAndTheStoreStaysOut` (fails without the resend: the asks time out).
  - Call ids keep 32 - `PendingCallBits` bits of generation (14 on a node) and the free stacks reuse the last freed
    slot, so a late reply could, rarely, match a later call: a failed call's slot cools for 30 s (longer than any
    budget) before reuse, unless the table is full (`AFailedCallsSlotCoolsSoItsLateReplyFindsNoOtherCall`).
  - A seed resolver that never answers stopped the refreshes for good: each resolution is bounded by the refresh
    period (`AResolverThatNeverAnswersDoesNotStopTheRefreshes`). A Hello racing a new view could leave an
    introduction behind; a shard answering a given-up slot round counted into the next one (rounds are numbered).
  - A relayed reply's frame is sized for its call id; a handler's frame header is read only to log. Measured and not
    kept: node links keeping their written buffer as a spare (instead of renting 256 KB from the pool per burst after
    idle): `RpcBenchmark` default job, interleaved off/on/off/on, 1 remote call 95.2 / 96.2 / 89.4 / 82.2 us, 1,024
    remote calls 1,057 / 1,041 / 1,039 / 1,052 us, 0 B either way: no gain beyond the noise, for 256 KB more per link.
  - `LoadBatch` and `SaveBatch` share one `StoreCall`; `AsyncSignal`, `Listener` and `NodeMetrics` are internal;
    `SwarmResult.Failures` removed (unused).
  - Docs: hot reload described as planned (ADR-067, ADR-078, the mmo README), analyzer ranges, R11 as measured, the
    flash sale's numbers as ADR-070 measured them, history wording.
  - Kept, with the reason: the trace check reads `Activity.Current` on every send (an `AsyncLocal` read, a few ns;
    skipping it would drop spans users start themselves); the header decoded per stage and the
    placement computed per stage (cheap while no placed types are registered; a carried handler needs `Envelope` to
    grow); shard queues' CAS dequeue (the drain after close is a second consumer); the timeout sweep scanning the table
    every 100 ms (sequential, bounded by the peak); a group's lock held across its fan-out (orders broadcasts and
    joins); `ClusterClient.cs` at 454 lines (already split in three by concern).
- [x] Self-review round 2 (independent reviews of round 1's changes, the client edge, the public API and the docs):
  - Round 1's duplicate check (sender, version, hash) could match a newer record (an actor moved back and forth
    without a checkpoint carries the same header), and a resend arriving after the actor moved on could be adopted or
    refused into a stale write. Each handoff now carries a random id that resends keep; resends are their own frame,
    taken only by a copy waiting for the key's record, never adopted or passed on fresh; each shard remembers the
    handoffs it took or passed on for two handoff timeouts (`HandoffsSeen`, bounded), so a resend is answered
    "accepted" wherever the actor went, or follows the first send to the node it was passed to. A resend reaching a
    stopping shard goes unanswered (it may have been taken there); its sender falls back once that node left.
  - Tickets and resumes draw on the session's message rate (each costs a signature;
    `ResumesAndTicketsDrawOnTheSessionsRate`). `Take` refuses ids that are no call id (0 and the cooling tags).
  - `NodeFeature.IsReady` removed (nothing read it). Generated-code entry points hidden from IntelliSense
    (`ServerRegistry`, `HandlerTypeInfo`, `SystemInfo`, `MessageHandler(s)`, `MessageFilters<T>`); the transport types
    users never touch are internal (`Connection`, `ConnectionOptions`, `FrameChannel`, `KeepAlive`, `SendFlusher`,
    `WebSocketChannel`, `MpmcQueue`, `BufferPool`, `FrameExtensions`, `ClientConnections.Open`/`Channels`, the hosting
    margins and status mapping); `ClusterMember`'s fields stay public (the card is a Nino wire type), its `Clone` and
    `Supersedes` are internal.
  - Docs: the README's benchmark table and one-map latencies from the release checklist's run, its realm example as
    the sample writes it, design's shutdown order and `ICluster` scope, the roadmap's development order and Z6 state.
  - Kept, with the reason: `ClientConnections` configured under `DaServer:Clients` (the section names what is
    configured, the clients; the feature type names what runs); `ResumeGrace` on `NodeOptions` (both sides of a
    resume read it, the actor nodes without client connections included).
  - E2E: the first sample run after round 1 failed once with game-0 not exiting 0 (its log was not kept); three
    reruns of the three sample tests passed. Watched in the next rounds.
- [x] Self-review round 3 (the handoff protocol attacked on its own; the generator, Aspire, hosting, the PostgreSQL store,
  the testing package and the client SDK with fresh eyes):
  - A copy declared dead while past its `Unloading` mail (its final save waiting for the write in flight, its record about
    to go or gone) was never removed, so its shard never drained and a downed node never exited: fencing asks every
    copy to leave and the drop takes `HandingOff` copies too
    (`ACopyDeclaredDeadWhileItsFinalSaveWaitsIsStillDropped`, fails without the fix).
  - Handoffs: the restored copy keeps its handoff id (`TakenHandoff`, no bound), the shard's memory evicts oldest first
    and knows when it forgot early (65,536 entries); a resend is taken only from the sender the copy waits for, or by the
    owner holding no copy while nothing was forgotten (as a first send would be); one following a pass-on to a node
    that is gone is refused instead of unanswered.
  - Generator and analyzer: a nested or non-public `[Persist]`/`[Handoff]` component is DAS014 (it was never
    registered, so never saved); `[Handle]` on an abstract or generic class is DAS001 (nothing was generated and derived
    types do not inherit it); generated registrations sort ordinally on every machine.
  - Client: a push handler that throws no longer closes the connection (a reconnect loop when the resent state throws
    again): `ClusterClient.HandlerFailed` reports it, the other handlers still run
    (`AHandlerThatThrowsOnAPushIsReportedAndTheConnectionStays`); a client whose first connect fails leaves the
    sweeper's list.
  - Hosting and Aspire: the admin bearer scheme is case-insensitive (the token still compared in constant time); a
    budget header past `int.MaxValue` ms is clamped instead of a 500; probe mode without `HttpPort` says so; the
    route check lists node projects one after another (their builds share outputs) and kills a listing on
    cancellation.
  - Kept: a private nested `EcsSystem` fails the build in generated code (CS0122) rather than with a DAS rule: an
    error either way, and DAS001 names handlers, not systems.
- [x] Self-review round 4 (every lifecycle tag combination against every system's filter; every change since the
  release checklist):
  - Two copies each waiting for the other's record (ownership flapping X -> N -> X with the key loaded nowhere) waited
    for as long as both nodes lived: a copy still waiting for its record when it is asked to leave goes at once
    (`UnloadSystem.Abandon`; `ACopyStillWaitingForItsRecordGoesWhenItsNodeStops`, a stop that ran out its budget
    without it).
  - `ErrorAction.Reload` after an unload was asked for (a rebalance, a stop, an operator) saved or handed on the
    half-changed state: the copy is fenced whatever was asked before; a refused checkpoint fences a copy already
    leaving too.
  - A late "none" from a node other than the one asked no longer starts a store load; a fenced copy refuses a record
    reaching it in the same wake (its sender keeps it and writes); a failed non-final write no longer holds a copy
    leaving by handoff (its record carries the last written hash).
  - A given-up slot round no longer clears the flag of the round counting after it; a full call table takes cooling
    slots back from a rotating start; the README's per-case allocations are in the release record's table; the flash
    sale's G5 numbers are ADR-070's; `HandlerFailed` says failures go unobserved without a subscriber.
  - Kept: answers carry no handoff id (a stale "accepted" needs the key to move M -> N -> M within one answer's
    flight); `Observe` allocates a state machine per push whose handler completes asynchronously (synchronous handlers
    pay nothing, and the task is now observed instead of dropped).
- [x] Self-review round 5 (ownership across nodes; membership, links and sessions):
  - A link that closed while its backlog was being flushed after a resume dropped the rest of the backlog (frames the
    connection refused reach no resend window): the flush stops at the first refusal and keeps the rest for the next
    connection. Not covered by a deterministic test (it needs the socket to fail between the replay and the flush).
  - A minority node that heard of the deaths from its own side's gossip before its detector declared them never
    counted the sides (its detector skips members already dead), so it ran on until the other minority node went
    silent: every member turning Dead, detected or merged, triggers the count (`BothNodesOfATwoNodeMinorityDownPromptly`;
    the race itself needs both sides cut within one gossip round, so the test pins the behaviour, not the race).
  - A copy that handed its record to a node other than the one that asked told the asker "none", which loaded a second
    copy from the store: it tells the asker where the record went (`MovedOn`), and the asker waits for that node.
  - A node remembered previous owners for one call timeout, but a leaving copy can hold its key through a handoff and
    a write in flight: the move window is 2 x (call timeout + store timeout), over up to 16 recent views. A copy held
    longer meets a new owner that loaded from the store; versions decide and its changes since its last write are lost
    (ADR-072).
  - Fixed after the round: a `Loaded` handler failing on a copy restored from a handoff writes the moved record before
    the copy goes (`AMovedRecordWhoseLoadedHandlerFailsIsWrittenNotLost`, a zero balance without it); a critical write that failed on a
    copy leaving by handoff travels in the record, and the new owner writes it at once
    (`ACriticalChangeWhoseWriteFailedIsWrittenAtOnceByItsNewOwner`, lost on the new owner's crash without it); a
    restart of the link's stream closes a handshake that said Hello with the old stream id (no deterministic test:
    it needs the peer to restart while the handshake is in flight).
  - Planned client moves (agreed: a move marker): each actor that takes the new session sends `SessionMoved` to the
    previous one behind its last push there, and the resume's reply says how many actors took it; the client goes on
    with the new connection's pushes once that many markers came on the old one (or the call timeout), then says
    goodbye; the old node takes the session out of every group before answering the goodbye
    (`APushStillOnItsWayToTheOldSessionComesBeforeTheNewOnes`, the push arrived after the new one without it). A group
    broadcast fanned out between the new session's join and the old one's goodbye still reaches both: duplicates in
    that window need per-group sequence numbers, not adopted.
  - Kept, with the reason (each needs rare events together, and its fix costs more than it saves):
    - A resume reverted after the previous session's late close was already consumed leaves that session granted
      until the actor unloads (the close of a superseded session is consumed on every resume; keeping it for a revert
      would grow per resume). It needs the previous session's grace to end during the new connection's resume and
      that connection to die before the resume completes.
    - A resume whose reply is lost cannot be retried with the previous ticket (Superseded): the client reports
      `ReconnectFailed` and logs in again; the new session closes after its grace.
    - A stopped shard's handoff memory is gone, so a resend reaching it goes unanswered and its sender falls back to
      the store once that node left (versions decide). It needs a lost answer and the receiving node scaling in within
      one handoff timeout.
    - Sessions of a drained node reach `SessionClosed` one view removal after the grace (the drained node counts as
      present while Leaving).
- [x] Self-review round 6 (ownership and handoff; links, sessions and the client):
  - A copy abandoned while waiting for its record told its asker "none" while the real holder still had the state
    (two quick joins): it sends the asker to the node it waited for. A shard remembers for the move window where it
    handed each key's record, so a later ask for a key gone from it is sent there instead of hearing "none". A copy
    adopted from a sender ahead of this node's view, for a key its asker owns here, leaves when asked. Recent views
    are bounded by the move window (up to 256), not cut at 16. A failed `Loaded` handler writes the record as it came.
  - A resume answered after its asker timed out took the session too late (the actor then belonged to neither
    session): a late resume is dropped, and the client-facing node reverts the grants that timed out or disconnected.
    The new session joins the actor's group before the old one leaves it, so a group push in between reaches one of
    them. A link resumed while the view thread found it down is no longer reset under it; a flush cut short by a
    closing connection does not publish it (a later send would overtake the rest). A session joins no group after its
    goodbye. The resume reply's format changed, so the client protocol is 4.
  - No deterministic tests for these interleavings (each needs messages to cross at one point); the existing handoff,
    resume and move tests pass, and the sample E2E runs.
- [x] Self-review round 7 (the whole distributed runtime under the round-7 criterion; every change since the release
  checklist against CLAUDE.md):
  - Must-fix: a resume whose session closed while the actors were asked did not revert the actors whose answer timed
    out, so an actor that took it before its deadline never heard `SessionClosed`: every timed-out or disconnected
    grant is reverted, closed or not.
  - A revert puts the actor's group back on the previous session; a shard forgets where it handed a key once the key
    is here again (an asker no longer bounces between two nodes); `Handoff.cs` and `ClusterClient.cs` split by concern
    (`Handoff.Memory.cs`, `ClusterClient.Pushes.cs`); ADR-026 and ADR-072 updated; the failed-`Loaded` test now
    changes the state before throwing, so it fails without the as-received write.
  - Also from round 7 (should-fix): a flush cut by a closing connection reconnects only after that connection's bytes
    reached the resend window; a node owning a key in its own view defers an ask (`MustWait`) instead of sending the
    asker where it handed the key earlier. Kept: a revert routed by a newer view can overtake the late resume it undoes
    (two faults: a view change and a timeout on one resume).
  - Kept: no deterministic tests for "no group join after a goodbye" and "a late resume is dropped" (the first is
    indistinguishable from a closed session through the client API, the second needs a resume queued past its deadline
    behind other mail); an actor reverted after a timeout may have sent a `SessionMoved` that a moving client counts (its
    pushes then keep their order but may overtake a real taker's last push in that one case).
- [x] Self-review round 8 (the distributed runtime as a whole; rounds 5 to 7 against CLAUDE.md):
  - Must-fix: an exception from any ECS system (a user `[ForEach]` system's bug) ended its shard's loop for good: the
    shard's actors were dropped unsaved, their keys forwarded in a circle, and `/alive` stayed healthy. A system that
    throws is caught per system now (round 9: `SystemFailed`); a shard whose loop still crashes keeps its pass start, so
    the watchdog reports it stalled and `/alive` fails.
  - Must-fix (test): the critical-write handoff test counted every store write, so a's own retries could satisfy it
    before b wrote; it now waits for each moved key's landed write.
  - Also from round 8 (should-fix): a timer wheel scheduled after a long idle period no longer walks every idle tick;
    a moving client waits for its `SessionMoved` markers by a completion instead of polling, and `SessionMoved` is
    internal; a real-cluster test checks that the node's count of actors taking a session equals the markers it sends;
    `SendAfter` and ADR-058 say that delayed sends do not outlive their node; stale move-window comments.
  - Kept: delayed sends are not handed over on a graceful stop (sending them early would break "never earlier", and
    a deadline that must survive is state); `Handoff.cs` and `Membership.cs` are at the size limit (the next change
    to either splits it); the two eviction loops of `HandoffsSeen` stay apart (one also records how far it forgot).
- [x] Self-review round 9 (the distributed runtime; the round-8 changes against CLAUDE.md):
  - Must-fix: round 8 guarded each group's pass, not each system: a user system throwing on every pass in
    `ShardMessages` skipped the runtime's mail, load, save and unload systems after it. EasyEcs now catches per
    system (`SystemErrorPolicy.Disable`); the shard reports the error (`SystemFailed`, at most once a second per shard)
    and enables the system again for its next pass.
  - Must-fix: one actor whose state does not serialize threw on every pass of the persistence system, starving the
    shard's other writes, its handoffs and its stop; its write is now given up (the store keeps the last record, a
    leaving copy goes) and so is a handoff of it (it falls back).
  - Must-fix: without a store, an actor whose first handler threw left the key loading for good; a `Created` or
    `Loaded` handler that throws now fails the load, and the message that loaded it is answered with `Error` instead of
    a timeout.
  - Must-fix: a shard that crashed while building its world (a system's `OnCreate`) read as
    healthy; it reads as stalled from before its world is built, so `/alive` fails.
  - Also (should-fix): a test for the idle timer wheel's skip; the move's timeout delay is cancelled; stale
    "handoff timeout" comments; the throwing-system test waits by condition and resets its flag.
  - Kept: a delayed send whose delivery throws part-way through a wheel slot leaves that slot's later entries for the
    next round of the wheel (never early; delivery only enqueues); two moves overlapping (the new connection retired
    while the previous move still drains) wait the call timeout before the first goodbye, as before; a node whose shard
    fails to start still joins and is restarted by `/alive` rather than refusing to start.
- [x] Self-review round 10 (every place the runtime calls user code or reads user data; the round-9 changes):
  - Must-fix: a record whose receiving actor threw while being set up (its `Loaded` handler) was never answered: the
    sender resent it while the receiver lived, or (the handler throwing after the record was taken)
    the receiver kept a copy that never ran while the sender dropped its own. Now refused, so the sender writes it.
  - Must-fix: a reclaimed copy (a refused or unanswered handoff) whose `Loaded` handler threw kept unloading for
    good; it falls back to its final write either way.
  - Must-fix: a feature's `DrainAsync` that threw (or ran out of budget) skipped the actors' unload; every feature hook
    (drain, stop, member dead, view changed) is guarded (`FeatureFailed`) and the node's own steps run.
  - Must-fix (tests): the stalled-shard test could reset its flag before the shard built its world; the idle timer
    test's 100 ms bound included NUnit's work. A stale round-8 line here.
  - Also (should-fix): system failures count in `daserver.handler.failed` (and its alert); repeating errors share a
    once-a-second log limit per shard; a client event subscriber that throws is reported through `HandlerFailed`
    instead of stopping the reconnect; a test that an unserializable state does not move.
  - Kept: an unreadable record refused by a copy that has mail loads from the store at once, possibly before its
    sender's fallback write lands (versions then fence it; needs a record one build cannot read); a load or save
    batch's `Apply` has no `finally` around its loop (nothing reachable throws there outside the per-actor guards).
- [x] Self-review round 11 (the whole distributed runtime; the round-10 changes against CLAUDE.md):
  - Must-fix: a write that landed but whose answer was lost (a reset after the commit, a failover) made the retry
    expect the old version, so the store refused the owner as a stale one and the changes it acknowledged since were
    lost. Writes carry their hash, the store keeps it, and a retry names its lost write: a store that took it writes
    after it (ADR-068; `IActorStore` and the PostgreSQL table changed, decided with the owner).
  - Must-fix: a feature's pulse source that threw ended every gossip round before failure detection: a crashed peer
    was never declared dead. Each source is guarded.
  - Must-fix: a reconnected connection that closed before the reconnect flag cleared was never reported or replaced.
  - Must-fix (docs): ADR-072 and `Handoff.Restore` did not list the refusals added in rounds 9 and 10.
  - Also (should-fix): `Maintain` is guarded per feature (one helper for every feature hook, logged at most once a
    second per node); a drain that ignores its token ends with the stop budget; a copy with mail that refuses a record
    asks its sender again after the refusal, so it reads the sender's write instead of racing it; a reclaim that
    cannot read its own record writes nothing; every subscriber of a client event runs; docs of the failure metric.
  - Kept: only the latest lost write is remembered (two lost answers in a row can fence the owner); a drain that
    ignores its token takes the whole stop budget, leaving the unload none (the shards still stop and write while the
    process lives); a reclaim whose initializer throws has no deterministic test (it needs a refused handoff and a
    state not in the record).
- [x] Nino 4.0.0-rc.6 and the reviews of duplication and coupling (October 2026):
  - BenchmarkDotNet, rc.4 (3997eb8) against rc.6 (a794210), each class twice in both orders: calls 1 local/remote
    4.45, 4.48 / 131, 129 us -> 4.16, 4.50 / 129, 134 us; 1024 remote 1.17, 1.17 -> 1.17, 1.18 ms; messages
    local/remote 479, 475 / 843, 803 ns -> 506, 470 / 835, 816 ns; broadcast 1,000 x 9 7.86 -> 7.36, 7.77 ms, 5,000 x 9
    42.9 -> 41.2 ms; scenario 3,000 / 9,000 22.2, 21.2 / 66.5, 67.0 ms -> 22.3, 22.6 / 65.8, 65.4 ms; allocations
    unchanged (an outlier of 16-20 KB in one run each side: pool growth). Even: the benchmarks' messages are raw
    ints, which both copy as memory; rc.6 pays in bytes (Latin-1 strings, packed structs) and in Unity's lists.
  - Duplication removed: one test store (`TestStore`, `StoreTest`), shared test helpers, test files split by
    section; one `LogThrottle`, one `SigningKeys`, `Platform.After`, one handoff `Notify`; `Handoff`, `Node` split.
    Coupling: closes handled by the runtime, not a filter; Hosting takes `INodeContext` and lets `ClientConnections`
    accept WebSockets.
  - Kept: the frame channels' send and receive loops (`Connection`, `WebSocketChannel`, the benchmark's pipe channel)
    stay separate until a transport benchmark proves one shared loop costs nothing; `ClientConnections` still uses
    the node's internals (a capability of the runtime's own assembly, ADR-048) beyond `INodeContext`; configuration
    keys spelled in the Aspire integration (`DaServer__...`) are not shared constants yet.
- [ ] ECS rewrite (agreed October 2026, ADR-081): everything is components, no inheritance, no polymorphism, no
  objects with state; only the DI shell (Generic Host, ASP.NET Core, Aspire) stays object-oriented.
  - [x] P0 EasyEcs (4.2.0, now 4.3.0): ordering by `[UpdateBefore]`/`[UpdateAfter]` along the data flow (no priorities),
    failed systems reported and enabled again, `Set`/`GetOrAdd` for `Add`, `EntityId` in components.
  - [x] P1 The design as an ADR (ADR-081): an actor is an entity whose identity is a component (`ActorKey`); handlers
    are stateless static functions of message, components and a context value; an answer to an ask is a message (no
    await in handlers); settings are attribute constants, the lifecycle is mail.
  - [x] P2 The runtime on it: `ActorKey` indexed, the mailbox a `DynamicBuffer<Envelope>` used only while the actor is
    not live, generated dispatch of static handlers (components passed `ref`/`in`, added with their default), asks
    answered as `Answered` mail, replies kept as `PendingReply` entities (`ReplyTo`, `ReplyAfter`); the turn machinery,
    call chains and cycle detection, the long-turn watcher, `ReplyWhen`, the service-call entities, the actor directory
    and pools, and the `Actor` and `Service` base classes deleted.
  - [x] P3 Sessions and persistence as data: an actor's known sessions are `KnownSession` child entities (tags `Known`,
    `Granted`, `Superseded`, indexed by session); `Created`/`Loaded`/`Unloading` mail; the generator declares
    `ITrackChanges` for `[Persist]` components, `ChangeSystem` tags written actors `Unsaved` and `CheckpointSystem`
    writes only those.
  - [ ] P4 The transport and the capabilities as ECS (plan below). Superseded by R (client sessions become `Client`
    actor entities and presences).
  - [x] P5 Samples, tests and benchmarks on static handlers; the analyzers updated (async handlers rejected by DAS001,
    the rules of the removed model gone, DAS022: a component parameter used after a structural change of its entity).
  - [ ] P6 Records as EasyEcs saves (needs component sets, Nino encoding and stable type ids in EasyEcs). Superseded by
    R6.
  - [ ] P7 BenchmarkDotNet against the P0 baseline (the README's numbers are from before the rewrite), the 10k-bot
    run and the flash sale, review rounds. Superseded by R9.
- P4 plan: the network layer on ECS, under the same rule.
  - Byte pumps stay plain code, every decision is a system: a pump (`SocketPump<TReceiver>`, `WebSocketPump`) owns a
    socket and its buffers, splits frames, classifies each with a pure function of the header and the immutable
    snapshots, and posts it to the world that owns what it is about; it writes the blocks it is handed. The client SDK
    (netstandard2.1, Unity) keeps its object model but shares the pump, the frame code and the pending-call table.
  - One loop machinery, two kinds of world: `Loop` = one thread, one bounded `Inbox` (the only cross-thread handoff,
    one wake per receive buffer) and one EasyEcs world with today's wake, spin and sleep policy (ADR-075). Shard loops
    own actors; net loops own connections, client sessions, groups and relayed calls; net loop 0 is also the node world
    (links, listeners, membership, gossip, slots). `NetLoops` defaults to `ProcessorCount / 4` on client-facing nodes
    (configurable) and 1 elsewhere.
  - Identity is unmanaged data: endpoint, link and session ids, `ReplyRoute` (where a reply goes) instead of reply sink
    objects, and call ids that name the loop that made them, so a reply goes to that loop's inbox without a node-wide
    table. Call ids widen to 64 bits (a new client and link protocol level), so no loop is limited to 65,536 calls in
    flight.
  - One writer per producer and destination: a shard writes link-bound frames into its own block per link, flushed
    once per pass; everything for a client session (pushes, replies, session operations) goes through the session's
    net loop, which keeps per-producer order.
  - Pending asks are entities (`PendingAsk` and a slot table per loop), expired by a system; rows in the slot table
    replace them only if `ActorAskBenchmark` says the entity churn costs.
  - Links as data in the node world: the resend window keeps the blocks the pump wrote (no second copy), released by
    acknowledgements; dial, handshake, resume, replay and backlog are tags and systems. Sessions, groups, relays and
    resumes are entities of net loops; membership, gossip and the maintenance timer become systems of net loop 0.
  - The ingress hop (client frame -> net loop -> shard) is the main risk: if `ClientRequestBenchmark` regresses, the
    fallback is taken: the pump reads an immutable grants and rate snapshot the session's loop publishes and posts
    requests straight to the shard, and only replies hop.
  - Mail of a moved actor is re-routed in order, as today.
  - Order of steps, tests green and BenchmarkDotNet A/B after each: `Loop` and `Inbox`; `ReplyRoute` for reply sinks;
    call ids that name their owner with asks as shard data; the mailbox on frame references in a store; pumps;
    per-shard outboxes and link ports; net loop 0 (membership, maintenance); links as entities; client sessions in net
    loops; the beat (ADR-070) in net loops; optionally bots as entities. New benchmarks: `ActorAskBenchmark`,
    `ClientRequestBenchmark`, `FanOutBenchmark`, `MailboxBenchmark`, `InboxBenchmark`, `LinkStreamBenchmark`.
- [x] Self-review rounds 12-15 (the distributed runtime and the rules, tests and docs, each round two reviewers):
  - Must-fix: a state that changed back after a lost write answer was never written again (a write in doubt is now
    written whatever its hash, and a claim clears the stored hash); a link whose handshake closed before its connect
    returned stayed down until the peer was declared dead; closes and resumes for a moving actor were refused (S10);
    stale docs after the protocol levels (ADR-065, ADR-076, design, C3); claims of the round-13 fixes without tests.
  - Also (should-fix): the manifest compares a keyed member of another assembly's type again and asks to record a
    keyed rename; `MapSend` refuses a Nino body that is not the message; the HTTP plane resolves the node internally;
    unreadable closes and resumes are refused, not thrown out of the mailbox; allocation tests check pool growth;
    `ShardDispatch.Sessions.cs`.
  - Kept: a close or resume reaching a key moved within the move window loads its actor from the store when its
    previous owner held nothing (it forgets nothing, and stays loaded like any actor: until it idles out when `UnloadAfterIdleSeconds` is set); a close that bounces between
    two nodes with lasting different views reaches `MaxHops` and is dropped, so that actor keeps the closed session
    until its client-facing node leaves; the link test cannot force the early-close window every run.
- Review criterion from round 7 on (agreed): a finding is must-fix when a single fault (one view change, one link
  restart, one node crash, one slow store call) leads to lost acknowledged state, a second live copy or a wait that
  never ends; one that needs two or more rare events together is should-fix and is listed here. Convergence is two
  rounds in a row without a must-fix.


## Generator / analyzer (`src/DaServer.Generator`)
- [x] Messages instead of contracts (ADR-035): typed `Ask` overloads per request, one dispatcher per `[Handle]`
      method, client message registration, message ids
- [x] Hosting: actor and service types with their generated dispatchers, client handler classes, EasyEcs system registration
- [x] A node's `Program.cs` is five lines: `AddDaServer`, `MapDaServer` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: the DAS rules (handlers, messages, wire types, id collisions, message flow
      (ADR-038), actors, services and systems without fields (ADR-044), address kinds (ADR-047), shared logic
      (ADR-055), static non-`async` handlers (ADR-081), a component used after a structural change (DAS022))

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), periodic stats line, graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / WithClientConnections / WithClientEndpoints; Compose and
      Kubernetes publishing (ADR-015, ADR-030); no configuration files in node projects
- [x] 10k-player scenario: login, game, map, 3 gateways, 10k bots (Sample.Bot fails on any gap, disconnect or
      p99 over the limit); passes through Aspire (README "Performance")
- [x] E2E in CI: `tests/DaServer.E2E` runs the AppHost with `Aspire.Hosting.Testing` (1000 bots in CI) and
      fails unless the bot exits 0
- [x] Aspire: run, one resource per replica (members are addressed individually); published, one workload per group
      seeded through its DNS name (ADR-030)

## Release review
- [x] The release checklist: its current record is in "Open after G" (7c5b50b); R1-R11 and E1-E10 hold, open items
      are the unchecked roadmap entries
- [x] Naming review (ADR-032, ADR-040): `Loaded`/`Unloading`, `Caller`, one noun per concept; components start
      as their defaults, `ActorKey` for systems
- [x] Moving scenario: 10,000 bots in one map (AOI and dead reckoning, ~2.5M positions/s); servers 0–5 KB/s, no GC;
      scale-out: a gateway started mid-run
      receives the new clients (test + process run)
- [x] One write per connection per batch (ADR-034)
- [x] Message-first model (ADR-035); client send backlog bound 256 KB (a slow client is disconnected and
      resynchronizes when it resumes)
- [x] Auto-reconnect (ADR-036): signed resume tickets, `SessionClosed` after a grace period, `SessionResumed`,
      Aspire-generated signing secret
- [x] Lifecycle as tags (ADR-037): `Ready`/`Loading`/`UnloadRequested`/`Leaving`/`Removing` with one system each
      (no polling); sample grid as data + systems (`DespawnSystem`, `PlacementSystem`)
- [x] `[Message]` (ADR-038): `[NinoType, Message]` is a message, plain `[NinoType]` is data; `[FromClient]`/
      `[ToClient]` checked at build time (DAS006)
- [x] EasyEcs from NuGet, used as its docs teach (ADR-039): cadence as system groups (`ShardRebalance`,
      `ShardIdleScan`), `UpdateInterval` in samples, `ActorKey` as the only identity, timer queue removed

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard and cross-node actor calls, replies of suspended handlers, call bursts, one-way
      messages, service messages, refusals and relayed timeouts, actor churn, client fan-out, client messages through
      a client-facing node to another node, gossip rounds, resend windows and idle nodes allocate nothing per operation
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), one-way messages (local/remote), group broadcast (1k/5k
      sockets), broadcast to everyone through 3 client-facing nodes (3k/9k sockets)
- [x] CI workflow (build, test, end to end; EasyEcs from NuGet)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [x] Record benchmark numbers of this release in the README
- [x] Test: calls in flight to a crashed node fail when it is declared dead
- [x] Tests: hostile client frames beyond the size check (trailers or an extension block longer than the frame close
      the connection; an extension entry running past its block or a payload that does not read is refused as
      malformed; the node serves everyone else: `HostileFramesHurtOnlyTheirConnection`), suspect flapping (a member
      whose links drop for 0.9 s five times is suspected, never declared dead, and keeps its keys and actors:
      `AFlappingLinkMakesAMemberSuspectNeverDead`).
