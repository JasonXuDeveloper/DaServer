# Target architecture (draft, not implemented)

The architecture DaServer is refactored to (breaking, roadmap phase R): the v24 programming model of
[programming-model.md](programming-model.md) on EasyEcs 4.5.0, with Native AOT support. The programming model draft is
the source of truth for what users write and what each rule means; this file says how the pieces fit together and
links to the draft's sections instead of repeating them. Today's architecture is [design.md](../design.md); the
decisions are ADR-082 and ADR-083 in [decisions.md](../decisions.md). When R is done, this file and the draft are
promoted into design.md and decisions.md and deleted (R10).

## 1. Layers and projects

### Framework packages

| Project | Targets | Holds |
|---|---|---|
| `DaServer.Abstractions` | netstandard2.1, net11.0 | What Protocol projects and clients see: `IFromClient`, `IFromClient<R>`, `IToClient`, `[To]`, `IServer`, `TypeId<T>`, status codes. No server types. |
| `DaServer.Core` | netstandard2.1, net11.0 | Wire and transport shared by servers and clients: frames, buffers, channels, the pending-call table. |
| `DaServer.Client` | netstandard2.1, net11.0 | The client SDK: connect, send `IFromClient`, receive `IToClient`, slots and re-joining ([draft §7](programming-model.md#7-客户端与-presence)). |
| `DaServer.Simulation` | net11.0 | Simulated users over `DaServer.Client`. |
| `DaServer` (runtime) | net11.0 | The server side of the model: `IActor<T>`, `[Handles<K>]`, `[On<T>]`, `IMessageTo<K>`, `IRequestTo<K,R>`, `[From<K>]`, `IEvent`, `IPersisted`, `Entity` operations, `Client`, shards, the framework systems, cluster, placement, persistence, migration. |
| `DaServer.Generator` (+ Roslyn 4.3 build) | netstandard2.0 | The generators (explicit `Key`, typed sends, `[On<T>]` jobs, `Registration`, `wire.manifest`) and the analyzers ([draft §13](programming-model.md#13-编译期检查删掉的东西版本记录)). |
| `DaServer.WireTool` | net11.0 | Writes a project's `wire.manifest` (Server and Logic sections, [draft §10](programming-model.md#10-持久化迁移热更升级easyecs-45)). |
| `DaServer.Hosting` | net11.0 | Generic Host and ASP.NET Core integration, configuration, OpenTelemetry, shutdown, the HTTP plane. |
| `DaServer.Persistence.PostgreSql` | net11.0 | The record store: epoch-fenced, compare-and-swap writes. |
| `DaServer.Testing` | net11.0 | `TestCluster` (in-process nodes, manual clock) and `TestDomain<K>` (one domain, one world). |
| `DaServer.Aspire` | net11.0 | AppHost side: `AddDaServerCluster`, `AddNodes<TServer>`, `WithClientEndpoints()`, and a generator that writes the server types from the committed `wire.manifest` (`actor` and `hosted-by` lines). References no server assembly. |

### User projects

| Project | Targets | Holds | Hot reload |
|---|---|---|---|
| `Game.Protocol` | netstandard2.1, net11.0 | Client messages only: data, no actor types, no addresses, no nodes | No (shipped to clients) |
| `Game.Server` | net11.0 | Actor types, server-to-server messages, events, components, claims, prefabs, server types (several per project), the node's five-line `Program.cs` | No: rolling upgrade |
| `Game.Server.Logic` | net11.0 | `[Handles<K>]` classes of `[On<T>]` methods, `[UpdateInGroup<K>]` systems, the generated `Registration` | JIT deployments only (section 8) |

- **One project may define several server types** (`[Hosts<Map>] struct MapServer : IServer`); **a node is one process
  running one server type** ([draft §2](programming-model.md#2-服务器与节点)). The AppHost picks the type per node
  group (`AddNodes<MapServer>("map", replicas: 4, shards: 8)`); a small deployment declares an `AllInOne` type.
- `[Hosts<Client>]` is "accepts clients": the gateway is a server type hosting the built-in `Client` actor type.

### Dependency directions

```mermaid
flowchart LR
    A[DaServer.Abstractions] --> C[DaServer.Core] --> CL[DaServer.Client] --> SIM[DaServer.Simulation]
    C --> R[DaServer runtime] --> H[DaServer.Hosting]
    R --> T[DaServer.Testing]
    R --> PG[DaServer.Persistence.PostgreSql]
    A --> P[Game.Protocol]
    P --> S[Game.Server]
    R --> S --> L[Game.Server.Logic]
    P --> GC[game client]
    CL --> GC
    ASP[DaServer.Aspire] -.reads wire.manifest.-> S
```

- Everything points one way: the runtime never references a capability, a sample or a user project.
- `Game.Protocol` references only `DaServer.Abstractions`: the analyzer refuses actor types, server messages,
  `ActorId`, `PresenceId` and `EntityId` there.
- `Game.Server.Logic` references `Game.Server`; nothing in `Game.Server` references Logic types except the host's
  `Registrations.Create()` call (open point 1 in section 10).
- The AppHost references `DaServer.Aspire` and reads `wire.manifest`; it references no server assembly.

## 2. Runtime structure

| Level | What it is | Owns |
|---|---|---|
| Node | One process, one server type, a cluster-assigned node generation ([draft §2](programming-model.md#2-服务器与节点)) | Links, membership, placement view, shards, the HTTP plane |
| Shard | One thread and one EasyEcs world; the actor types the server hosts share it; an actor lands on a shard by its key's hash | The dispatch queue, the framework systems, singletons (`Time`, `Ordered`, `Current`, `Outbox`) |
| Domain | One actor: its root entity (carrying the actor type component, e.g. `Map(7)`) and its descendants | Its mail, its `Due` items, its presences, its waiting asks |
| Entity | A root, an avatar, a presence, a waiting ask, a scheduled event | Components; `In<K>` on every entity of a K domain |

- **Parallelism**: many domains per shard, many shards per node, many nodes. Inside a domain the ordered phase is
  serial; heavy system methods use `[ForEach(Parallel = true)]`. Each shard's world sets
  `WorldOptions.MaxDegreeOfParallelism` = node cores / node shards, at least 1 (EasyEcs D36: the budget is per world).
- **The dispatch queue** is the only cross-thread handoff: IO threads and other shards post frames to it; the shard
  thread moves them into its mailbox snapshot. Queues are bounded (`Busy`); the control channel (presence control,
  answers) is bounded by what the node itself holds ([draft §11](programming-model.md#11-失败时的结果)).

### A tick

Per actor type with `[TickRate(n)]`, at time t ([draft §1](programming-model.md#执行模型每个域是一个离散事件模拟)):

| Step | Systems | What happens |
|---|---|---|
| 1. Ordering | `OrderedPhase` (framework group) | Mail of the tick window and due items merged by `(time, seq)`; each item runs as a single-entity job, then its immediate chain (at most 1,024); `Outbox` flushed after each item |
| 2. The actor type's group | `world.Update<Map>()` | The user's `[UpdateInGroup<Map>]` systems; the group filter `In<Map>` comes with `IActor<Map>` (`IGroupWith<In<Map>>`) |
| 3. Settling | `Depart`, `SyncSubscriptions` (framework group) | Presences with `Leaving` destroyed; changed `DynamicBuffer<Subscribed>` sent to the gateways |
| 4. Cleanup | `ReleaseRoutes`, the `Persisted` cleanup (framework group, per shard, no `In<K>`) | Cleanup components of destroyed entities processed; `Outbox` flushed |

- Actor types without `[TickRate]` (Player, `Client`) run items as they arrive; their group and settling run after
  each batch of mail with `Time.DeltaTime` 0.
- Server types hosting several ticking actor types keep rates that divide the fastest one.

### Runtime data

| Data | Kind | Role |
|---|---|---|
| `Due(TimeSpan At, long Seq)` | ordered value index | Delayed events, request deadlines, join confirmation deadlines, attach back-off: one timer mechanism ([draft §9](programming-model.md#9-时间与广播)). The ordered phase takes the first `Range(Due.Min, (t, max))` entry, compares it with the mailbox head, runs the earlier, and queries again |
| `Ordered` | singleton | Sequence counter, current tick window, immediate chain budget |
| `Current` | singleton | The running item's time, root, sender and client context; operations read the client context from it |
| `Outbox` | singleton | Everything an item sends (`Send`, `Ask`, `Reply`, `Push`, `Broadcast`, join routes), flushed in append order after the item: a join's route always precedes its reply; tests assert it as data |
| `Time` | singleton | `Tick`, `DeltaTime`, `TickRate`, `Now` (monotonic, process-local) |
| Placeholder | entity | A key not yet active or being saved: the root prefab copy without `In<K>`, the key, `Loading` or `Saving`, `DynamicBuffer<Mail>` |

### `Registration`

- Generated, one instance per call of `Registrations.Create()` (no static registries, no module initializers): the
  Server part (actor types, root and child prefab copies with `In<K>`, claims, join-on-connect) and the Logic part
  (dispatch tables per actor type, client routes, systems per group) ([draft §12](programming-model.md#12-完整例子连接进地图移动放技能看见别人)).
- Nodes, `TestCluster` and `TestDomain<K>` take the object. Hot reload swaps its Logic part.
- Every type in it is closed at compile time in the user's assembly (section 8).

## 3. Messaging

| Path | Shape | Route |
|---|---|---|
| Client → server | `IFromClient`, `IFromClient<R>` in Protocol | Client → its `Client` entity on the gateway → `[On<Incoming>]` filter → generated route table → straight to the target actor (no hop through Player) |
| `[To]` field | `EnterMap([To] long Map)` | To the actor of the handling type whose key is the field; the subject is the root ([draft §4](programming-model.md#4-消息)) |
| No `[To]` | `MoveTo(...)` | To the client's presence on the handling actor type; the subject is the presence entity; none: `NotJoined` |
| Server → client | `IToClient` | `e.Push(msg)` to a presence, `root.Broadcast(msg)` to every joined presence, `e.Push(in group, msg)` to a `ClientGroup` |
| Server → server | `IMessageTo<K>`, `IRequestTo<K,R>` with `[From<K>]` | Generated typed `Send`/`Ask` on `Entity`; the answer is `Answer<TReq>` or `Answer<TReq,TState>` mail to the asking entity ([draft §8](programming-model.md#8-等待别的-actor-的结果)) |
| Inside a domain | `IEvent` | `e.Schedule(ev[, after:])`: a child entity with `Due`, run in the ordered phase |
| Outside actors | `ICluster` | HTTP, back-office, tests: any server-to-server message; `cluster.Disconnect(clientId)` |

- **Presences** ([draft §7](programming-model.md#7-客户端与-presence)): a client's existence on an actor, one per
  actor type: a child entity with `Presence` (a cleanup component) on the target, a `Route` in the `Client`'s
  `DynamicBuffer<Route>`, a slot number in the SDK. `e.Join()` is immediate and appends the route to `Outbox`;
  `Leave()` adds `Leaving`, systems react, `Depart` destroys. Connection stamps are ordered (token issue sequence,
  gateway generation, connection counter): the larger takes over.
- **`[JoinOnConnect<TClaims>]`**: the framework attaches the client to the actor whose key has the claims' shape
  (Player) and re-attaches it when that presence is lost.
- **Gateway**: a server type with `[Hosts<Client>]`. `Client` is pinned to its connection's node, never placed or
  migrated, activated on connect, deactivated on disconnect. It filters, routes, keeps the routes and fans out groups.
- **`ClientGroup` fan-out**: subscriptions are data (`DynamicBuffer<Subscribed>` on presences); `SyncSubscriptions`
  sends the differences to the gateways; a group push is serialized once, one frame per gateway with members, fanned
  out there.
- **Order**: one sender to one target arrives in send order unless one of them migrates
  ([draft §1](programming-model.md#1-保证)).

## 4. Placement, activation, persistence

- **Placement**: an actor type is bound to server types (`[Hosts<…>]`); its keys are placed among the nodes hosting
  it, moved when nodes join or leave (rate-limited per node). `[Stateless]` actors live on any node, never persisted.
  `Client` is pinned.
- **Activation** ([draft §6](programming-model.md#6-生命周期), [§10](programming-model.md#10-持久化迁移热更升级easyecs-45)):
  1. The first message creates the placeholder (`Loading`); mail waits on it.
  2. The activation takes an epoch: a conditional write `(key, epoch) → epoch + 1`, even without a record.
  3. The record is loaded into the placeholder and n−1 empty entities:
     `LoadEntitiesInto(record, into, markWritten: false)`. **The placeholder stays the root**: key and mail never move.
  4. `In<K>` added, `Loading` removed; the first mail is `Activated(Activation)`.
- **Epoch fencing**: every record write compares the epoch; a failed compare fences the activation (destroyed without
  a save, `Leaving` or `Deactivating`; presences' routes released by their cleanup components). A node removed from
  the view fences all its actors before re-joining with a new generation.
- **Persistence**: components marked `IPersisted : IComponent, ITrackChanges`. `ChangeSystem` (`[Changed<T>]`,
  `[Removed<T>]`) tags the root `Unsaved`; the `Persisted` cleanup component tags it when a persisted descendant is
  destroyed; `CheckpointSystem` writes `[With<Unsaved>]` roots at the checkpoint interval with an epoch CAS.
- **Records**: `SaveEntities` over `world.Descendants(root)` with the component set
  `persist` = every `IPersisted` + `ChildOf` + `In<K>` + `Persisted`, built once in `Registration`. Restored values
  do not count as writes (`markWritten: false`), so an activation does not write the record back.
- **Deactivation**: idle past `IdleTimeout` or `e.Deactivate()`: presences leave, `Deactivating` runs, the subtree is
  saved and destroyed, a `Saving` placeholder holds new mail until the write lands.

## 5. Migration, upgrades, hot reload

- **Migration**: the whole subtree with the component set `migrate` (every component not `[Transient]`), including
  waiting asks, presences with `Subscribed`, and delayed events with their remaining time. The header carries each
  component's layout hash from `wire.manifest`; one mismatch (mixed versions) falls back to deactivate, write the
  record, load it on the target. Arrivals load with `markWritten: true`, so `[Changed<T>]` systems treat them as
  written (re-subscribe, re-cell, first checkpoint). A tombstone forwards late control messages. Across shards it is
  bytes too (`MoveEntities` needs one thread).
- **Time across processes** is remaining time only; each process runs on its own monotonic clock.
- **`wire.manifest`**: a Server section (actor types, hosting server types, tick rates, claims, message endpoints,
  events, state types, persisted components, layout hashes, pushes) and a Logic section (client routes, event and
  `Answer` handlers, lifecycle handlers, `Leaving` consumers). The AppHost reads `actor` and `hosted-by`; nodes check
  their server type against it at start.
- **Rolling upgrades**: `[Hosts]` on the member card, `NotHandled` from old nodes retried once after placement is
  resolved again, migration falls back to records across layouts. Anything in `Game.Server` changes only this way.
- **Hot reload** (JIT deployments): the Logic assembly in a collectible `AssemblyLoadContext`; between two ticks
  each shard calls `world.RemoveTypes(old context)`, installs the new `Registration`'s tables and adds its systems;
  the old context unloads after every shard switched. `wire.manifest` refuses a reload that changes a client route or
  drops a handler or `Leaving` consumer still in use.

## 6. Distributed guarantees and failure handling

The guarantees are [draft §1](programming-model.md#1-保证); the outcome of each failure is the table in
[draft §11](programming-model.md#11-失败时的结果). In short:

| Guarantee | Mechanism |
|---|---|
| Each domain runs its items one at a time in `(time, seq)` order; domains run in parallel | Ordered phase, `Due` index |
| Per sender-target order, best effort while migrating | One ordered link per node direction; no migration fence |
| One-way at most once; a request always ends (reply, refusal with a reason, timeout); a timeout may have run | Deadlines as `Due`; call ids are idempotency keys; reserve then commit |
| At most one activation writes a key's record | Epoch claim and CAS writes; fencing |
| A reconnect is a new connection; the larger stamp takes over | Ordered connection stamps; SDK re-joins per slot |
| Handler exceptions do not roll back | `[OnError] Resume` (default) or `Restart` from the last record |

## 7. EasyEcs 4.5 features used

| Part | EasyEcs features |
|---|---|
| Actor lookup | Value index `IIndexed<T>` on the actor type component (`world.Index<K,K>()`), explicit `Key` (generated) |
| `[On<T>]` handlers | Single-entity jobs `entity.Run<T1..Tn, TJob>(ref job[, in filter])`, `IForEachEntity<…>`, `Writes`, `QueryFilter`, `INoStructuralChanges` |
| Per-tick logic | `EcsSystem`, `[ForEach]`, batch methods, `Parallel = true`, `[Changed<T>]`, `[UpdateAfter<T>]`, `ISystemGroup`, `IGroupWith<T>` group filters, `world.Update<TGroup>()` |
| Ordering and timers | Ordered value index (`Range`) on `Due`, singletons |
| Domains | `ChildOf`, `world.Children`, `world.Descendants(root)`, prefabs (`new Prefab(base)` copies) |
| Presences, cleanup | `ICleanupComponent`, `Destroyed`, `DynamicBuffer<T>`, `[Changed<DynamicBuffer<T>>]` |
| Persistence | `ITrackChanges`, `[Changed<T>]`, `[Removed<T>]`, `ComponentSet`, `SaveEntities`, `LoadEntitiesInto(…, markWritten: false)`, Nino type ids |
| Hot reload | `world.RemoveTypes(context)`, collectible contexts, `AddSystem<T>()` |
| Diagnostics, tests | `SystemErrorPolicy` (`Disable` on nodes, `Throw` in `TestDomain<K>`), `MeasureSystems`, `system.Stats`, `world.Count<Destroyed>()`, `world.Describe()`, `Capture`/`Restore` |
| Allocation-free reuse | Pooled `DynamicBuffer` storage, `MaxDegreeOfParallelism` per world |

## 8. Native AOT

DaServer nodes publish with `PublishAot=true` (ADR-083, requirement E11, [draft §10](programming-model.md#native-aot)).
JIT stays supported; the two are a deployment choice:

| | JIT deployment | Native AOT deployment |
|---|---|---|
| Logic | Collectible `AssemblyLoadContext` | Compiled into the node |
| Hot reload | Yes (section 5) | No: a reload request is refused; changes ship as a rolling upgrade |
| Start-up, memory | JIT warm-up, tiered compilation | Native start, smaller working set |
| Reflection | Allowed where documented as JIT-only | None |

### What makes it work

- **Registration is generated and closed**: `Registrations.Create()` names every closed type in the user's assembly
  (`.Actor<Map>()`, `.Dispatch<Map>(…)`, `world.AddSystem<Depart<Map>>()`). No reflection, no `MakeGenericType`, no
  `Activator`, no assembly scanning, no module initializers.
- **Framework per-actor-type systems are emitted closed by the generator**: the generated `Registration` names each
  instantiation. The framework's generic systems have no `[ForEach]` methods (they run their jobs from `OnUpdate`
  through EasyEcs's generic runners, closed by their type arguments), so EasyEcs has no generic system code to close
  at run time. EasyEcs closes a generic system named only by another assembly with `MakeGenericType`, which Native AOT
  cannot do (EasyEcs D20; `faq.mdx#native-aot`), and suppresses the warning for it: such a mistake would fail at run
  time, so DaServer's analyzer reports a user's generic `[ForEach]` system closed outside its own assembly
  ([draft §13](programming-model.md#13-编译期检查删掉的东西版本记录)).
- **Handlers are jobs**: `[On<T>]` methods become struct jobs called through generic `entity.Run<…, TJob>`, closed at
  compile time; dispatch tables hold static method-group delegates.
- **Serialization is Nino's generated formatters** (messages, persisted components, records, migration bytes); type
  ids are Nino's. EasyEcs records types by Nino type id (D30).
- **EasyEcs**: `EasyEcs.Core` is marked `IsAotCompatible`; its generated code calls user methods directly.
- **The HTTP plane**: `WebApplication.CreateSlimBuilder`; minimal APIs through ASP.NET Core's request delegate
  generator; JSON through source-generated `JsonSerializerContext`s. Today's admin endpoints serialize anonymous
  objects (`DaServerAdmin`) and `MapAsk` reads bodies with reflection-based JSON: the admin replies become typed
  records in a framework context; JSON bodies of `MapAsk` need a context the application declares (the System.Text.Json
  generator does not see other generators' output, so DaServer cannot declare it), while Nino bodies need nothing.
- **Configuration**: `Configure<NodeOptions>(section)` is handled by the configuration binding generator;
  `configuration.Get<TOptions>()` inside the generic `AddDaServerFeature<TOptions>` has an open type there and
  moves to a closed call site (to verify with the first AOT publish).
- **Route listing**: `Routes` (the `DaServer__ListRoutes` check mode) reads assemblies by reflection; the target reads
  the routes from `wire.manifest`'s Logic section instead.
- **Framework projects** set `IsAotCompatible` on their net11.0 targets, so the trim and AOT analyzers run on every
  build under `TreatWarningsAsErrors`.

### To verify (R8)

| Dependency | Version in the repo | Status |
|---|---|---|
| OpenTelemetry (`Extensions.Hosting`, OTLP exporter, ASP.NET Core, HTTP and runtime instrumentation) | 1.19.x | To verify: warnings under `PublishAot` |
| `OpenTelemetry.Exporter.Prometheus.AspNetCore` | 1.19.1-beta.1 | To verify |
| Npgsql through `Aspire.Npgsql` | 13.6.1 | To verify: whether the Aspire integration builds the data source in a way Native AOT accepts |
| `Nino.AspNetCore`, `Microsoft.Extensions.ServiceDiscovery`, `Microsoft.Extensions.Http.Resilience` | see `Directory.Packages.props` | To verify |
| ASP.NET Core with `CreateSlimBuilder` | .NET 11 | To verify that `AddDaServer` needs nothing the slim builder drops (TLS is the platform's) |

### The zero-warning gate

- The AOT compiler reports one **IL3054** (generic recursion through EasyEcs's change-tick column type) for any
  program with tracked components, and every stateful DaServer node has them (`IPersisted : ITrackChanges`).
- Builds and publishes have 0 warnings (release checklist step 1, E11). A suppression of a library's warning in the
  user's program is not accepted, scoped or documented.
- So the AOT publish check is **gated on EasyEcs request #21** (remove IL3054 for programs with tracked components,
  with an AOT sample in EasyEcs's CI). Until it ships, R8 prepares everything else and records the one remaining
  warning; the release checklist's AOT step stays open.
- **Proof**: `dotnet publish -p:PublishAot=true` for each sample node (mmo, flashsale) with 0 warnings, and the E2E
  tests passing against the AOT binaries.

## 9. From today's code to the target

Derived from [design.md](../design.md) and the deleted list in
[draft §13](programming-model.md#13-编译期检查删掉的东西版本记录).

| Today | Target |
|---|---|
| `[Actor<TId>]` static class, a separate id type, `ActorKey` component | The actor type `record struct Map(long Id) : IActor<Map>`: address, root component (value index) and system group; `ActorId` for any actor |
| `[Handle]` methods, generated dispatchers, `in Context` | `[On<T>]` methods in `[Handles<K>]` classes, compiled into EasyEcs single-entity jobs; one binding rule (the subject) |
| `[Service]` | `[Stateless]` actor type |
| `ctx.Send`/`Ask`/`ReplyLater`/`BindSession` | `Entity` operations (`Send`, `Ask`, `Reply`, `Push`, `Join`, `Leave`, `Schedule`, `Spawn`), appended to `Outbox` |
| `Answered<TRequest,TReply>`, `PendingReply`, `Answer<T>.Later` | `Answer<TReq>` / `Answer<TReq,TState>` to the asking entity (a waiting child with `Due`); `ReplyTo<T>` as data |
| `IRequest<TReply>`, `[Message]`, `[FromClient]`/`[ToClient]` | `IFromClient`, `IFromClient<R>`, `IToClient` (Protocol); `IMessageTo<K>`, `IRequestTo<K,R>`, `[From<K>]` (Server); `IEvent` |
| `Publish` | Deleted: explicit sends, `ClientGroup` |
| `[Filter]`, `IMessageFilter` | `[On<Incoming>]` in `[Handles<Client>]` |
| Mailbox used only while an actor is not live; `DispatchSystem`, `MailSystem` | Every item through the ordered phase, merged with `Due` by `(time, seq)`; mail waits on the placeholder |
| Lifecycle tags (`Loading`, `Ready`, `UnloadRequested`, `Leaving`, `Fenced`, `Removing`), `Created`/`Loaded`/`Unloading` | Placeholder root (`Loading`, `Saving`), `Activated(Activation)`, `Deactivating`, fencing by epoch |
| `TimerSystem`, timer wheel, `SendAfter` | `Due` ordered index; `e.Schedule(ev, after:)`; `Scheduled<T>` |
| Sessions: bind and grant, `KnownSession` children, `Known`/`Granted`/`Superseded`, `SessionClosed` | Presences: `Join()`/`Leave()`, `Presence`/`Joined`/`Leaving`, `PresenceLost`, `[JoinOnConnect<TClaims>]`, ordered stamps |
| `ClientConnections` feature (`DaServer:Clients`) | `Client` actor type hosted by a `[Hosts<Client>]` server type |
| Tick rate in configuration, the default group at the tick rate | `[TickRate(n)]` on the actor type; `[UpdateInGroup<K>]` |
| `UnloadAfterIdleSeconds` | `IdleTimeout` |
| `[Persist]` with a generated `ITrackChanges`, the own record format | `IPersisted`; records as `SaveEntities` over `Descendants` with a `ComponentSet`; epoch CAS |
| State handoff (ADR-072) | Migration bytes with layout hashes; fallback to records |
| `Registration_{Assembly}` run by a module initializer (ADR-012) | `Registration` instance from `Registrations.Create()` |
| One project per server (ADR-017), Contracts / Model / Logic (N1) | Protocol / Server / Logic; several server types per Server project; node = one server type |
| `AddNodes<TProject>` | `AddNodes<TServer>` over server types generated from `wire.manifest` |
| Random `NodeId` per process | Cluster-assigned node generation |
| Deadlines in UTC milliseconds | Monotonic `TimeSpan`; remaining time across processes |
| Route listing by reflection (`Routes`) | Routes from `wire.manifest` |
| Admin JSON of anonymous objects | Typed records, source-generated JSON |

CLAUDE.md's Layout and ADR-017 change with R (one Server project may define several server types).

## 10. Open points to settle during R

1. **The host and the Logic assembly.** `Game.Server`'s `Program.cs` calls Logic's `Registrations.Create()` while
   Logic references `Game.Server`: a project cycle. On JIT nodes that hot reload, the first Logic version must also
   load into a collectible context, so the host must not bind it statically; on AOT nodes it is bound statically.
   One option: the five-line `Program.cs` moves to a host project referencing Server and Logic, and the host calls a
   generated entry that, under `PublishAot`, calls `Registrations.Create()` and otherwise loads Logic by name into a
   collectible context. Settled in R2–R4.
2. **Messages as entities** ([draft §14](programming-model.md#14-已定的取舍以及待办) item 4): measured with
   BenchmarkDotNet in R4; changed only if not worse by more than 10%.
3. **EasyEcs 4.5.0 compile checks** (R1): the generated explicit `Key` is accepted by value indexes; `IActor<TSelf>`
   compiles in the framework assembly.
