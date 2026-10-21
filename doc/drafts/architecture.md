# Target architecture (draft, not implemented)

The architecture DaServer is refactored to (breaking, roadmap phase W): the v61 programming model of
[programming-model.md](programming-model.md) on EasyEcs 4.7 (the draft's header lists the features it uses), with Native AOT support. The programming model draft is
the source of truth for what users write and what each rule means; this file says how the pieces fit together, holds the runtime's internal data and the client protocol's
internals, and links to the draft's sections instead of repeating them. Today's architecture is [design.md](../design.md); the
decisions are ADR-082 to ADR-092 in [decisions.md](../decisions.md). When W is done, this file and the draft are
promoted into design.md and decisions.md and deleted (W10).

## 1. Layers and projects

### Framework packages

Six packages. A package exists when something references it without the others: a Unity client takes no server, a
node takes no AppHost code, production takes no test tools, and a capability is off unless referenced (ADR-045).

| Package | Targets | Holds |
|---|---|---|
| `DaServer.Core` | netstandard2.1, net11.0 | What both sides see: the client-facing contracts (`IFromClient`, `IFromClient<R>`, `IToClient`, `[To]`, `IServer`, `TypeId<T>`, status codes) and the wire and transport (frames, buffers, channels, the pending-call table). Ships the generators, the analyzers and the `wire.manifest` task (below). |
| `DaServer.Client` | netstandard2.1, net11.0 | The client SDK: connect, send `IFromClient`, receive `IToClient`, slots and re-joining, moving on `Retire` ([draft §7](programming-model.md#7-客户端与-presence)). |
| `DaServer` | net11.0 | The server: the model (`IActor<T>`, `[Handles<K>]`, `[On<T>]`, `IMessageTo<K>`, `IRequestTo<K,R>`, `[From<K>]`, `IEvent`, `IClaims`, `IConfigTable<T>`, `IWorldSingleton<T>`, `[Persist]`, `[NotSaved]`, `Entity` operations, `Connection`), the framework systems, cluster, placement, persistence, migration, hot restart, and the node host: Generic Host and ASP.NET Core, configuration, OpenTelemetry, shutdown, the HTTP plane (service managers are .NET's own `UseSystemd()` and `UseWindowsService()`; launchd needs only its plist, [deployment.md section 0](deployment.md#0-launch-flows)). |
| `DaServer.Persistence.PostgreSql` | net11.0 | The record store: epoch-fenced, compare-and-swap writes. |
| `DaServer.Testing` | net11.0 | `TestActor<K>` (one actor, one world, DaServer's manual clock, ADR-071), `TestCluster` (in-process nodes) and simulated users (`Swarm`, HTN behaviors) over `DaServer.Client` for load tests and E2E. |
| `DaServer.Aspire` | net11.0 | AppHost side: `AddDaServerCluster`, `AddNodes<TServer>` (a `DaServerNodeGroup<TServer>`), `WithActorStore`, `WithClientConnections(port)` on a node group, `WithClientEndpoints(gateways)` for a client app, the `{cluster}-restart` command, and a generator that writes the server types from the committed `wire.manifest` of the `#:project` (`actor` and `hosted-by` lines), each bound to that project. References no server assembly. |

### Optional capability packages

Each is off unless referenced and lives in its own namespace; the runtime references none of them (ADR-045, ADR-090; design in [draft §15](programming-model.md#15-扩展能力可选包)).

| Package | Targets | Holds |
|---|---|---|
| `DaServer.Reminders` | net11.0 | `Reminder<T>`, `Reminded<T>`, `root.Remind<T>`; reminders are persistent entities whose next firing is the runtime's `WakeAt`; the runtime's `ArmSystem` arms their `Due`, the package only handles the firing |
| `DaServer.Topics` | net11.0 | The `Topic` actor type, `ITopicMessage`, `Subscribe`/`Unsubscribe`/`Publish` on `Entity` (each a one-way package message to the Topic without `[From<…>]`; `Publish` carries the payload's type id and bytes, which the Topic never decodes), the one-way `NotSubscribed(ActorId, Epoch) : IMessageTo<Topic>` (an ordinary package message, not control mail), the subscriber-side `Subscription` buffer and the system renewing it every `DaServer:Topics:RenewInterval`; table changes ride the normal checkpoint, the receiving node drops a delivery whose topic is not in the subscriber root's `Subscription` buffer and sends the Topic, from the subscriber's shard, a one-way `NotSubscribed` echoing the `(ActorId, Epoch)` the delivery frame carried (dropped under `Busy`, it is sent again at the next publish), which the Topic deletes only when that epoch equals its table's; delivery is at most once, a lost or reordered subscription message costs at most one renew interval of missed publishes, and once unsubscribed no handler runs for a later delivery and the Topic stops sending at the next publish |
| `DaServer.Replication` | net11.0 | `[Replicated]`, `ReplicatedTo`, the per-type replication systems with generated field bitmasks; its own state: `Replica<T>` (last sent value and the world's `ChangeTick` it was sent at), a bounded ring of exits per group, an incarnation per actor (renewed on activation from the record, migration arrival, hot-restart load and a reload after a throw, so the stamps compare only within one; a renewal clears the actor's exit rings and zeroes its `Replica<T>` stamps) |
| `DaServer.Replication.Client` | netstandard2.1, net11.0 | Applies replication patches into a client's EasyEcs world |
| `@daserver/client` (npm) | TypeScript | The client SDK over WebSocket, per the published client protocol; message classes and codecs generated by Nino.TypeScript from `Game.Protocol`'s build (no DaServer generator); replication patches into an entity table |

`DaServer.Templates` is a `dotnet new` template package (`daserver`, `daserver-mmo`, `daserver-flashsale`, `daserver-arena`, `daserver-relay`), generated from the samples; nothing references it (ADR-091).

`DaServer.Generator` (netstandard2.0) is not a package of its own: it is built twice (Roslyn 5.x and 4.3, Unity's
oldest) into `DaServer.Core`'s `analyzers/` folder, so every project using DaServer gets it. It holds the generators
(explicit `Key`, typed sends and reply accessors, `[On<T>]` jobs, `Registration`, `wire.manifest`), the analyzers
([draft §13](programming-model.md#13-编译期检查)). `DaServer.WireTool` ships in the same `analyzers/`-side tools folder
and stays out of process: `-p:UpdateWireManifest=true` runs it with `dotnet exec` after the build (one section per
project, [draft §10](programming-model.md#10-持久化迁移热重启升级)); an in-process MSBuild task would need a second build
for Visual Studio's .NET Framework MSBuild and would load `Microsoft.CodeAnalysis` into the MSBuild node, where its
version conflicts. It reads the built assembly and builds one `Registration` to read prefab compositions, which only
run time knows.

Merged away: `DaServer.Abstractions` into `DaServer.Core` (both are referenced by every client and every server; the
analyzer, not a package boundary, keeps server types out of `Game.Protocol`), `DaServer.Hosting` into `DaServer` (a
node always runs both; `TestCluster` simply starts no HTTP), `DaServer.Simulation` into `DaServer.Testing` (both are
test tools); `DaServer.WireTool` is no package of its own, it ships inside `DaServer.Core` beside the generator (ADR-086).

### User projects

| Project | Targets | Holds | Code changes |
|---|---|---|---|
| `Game.Protocol` | netstandard2.1, net11.0 | Client messages only: data, no actor types, no addresses, no nodes | Shipped to clients |
| `Game.Server` | net11.0 | Actor types, server-to-server messages, events, components, the `IClaims` type, prefabs, server types (several per project), `[Handles<K>]` classes of `[On<T>]` methods, `[UpdateInGroup<K>]` systems, the generated `Registration`, the node's `Program.cs` (about five lines), and `apphost.cs`, the deployment | Hot restart or rolling upgrade, JIT and AOT alike (section 5) |

- **One project may define several server types** (`[Hosts<Map>] public readonly struct MapServer : IServer`); **a
  node is one process running one server type** ([draft §2](programming-model.md#2-服务器与节点)). The AppHost picks the
  type per node group (`AddNodes<MapServer>("map", replicas: 4)`); a small deployment declares an `AllInOne` type.
- **The AppHost is `Game.Server/apphost.cs`**, an Aspire file-based AppHost; group names, the store and the ports per
  group are its business ([draft §2](programming-model.md#2-服务器与节点)), the launch flows per environment are
  [deployment.md section 0](deployment.md#0-launch-flows).
- A node sizes its own thread count from the CPUs it is given (section 2); nobody writes it.
- `[Hosts<Connection>]` is "accepts clients": the gateway is a server type hosting the built-in `Connection` actor type.

### Dependency directions

An arrow reads "references". Dotted: no assembly reference.

```mermaid
flowchart BT
    Core["DaServer.Core<br/>+ generator, analyzers"]
    Client[DaServer.Client] --> Core
    Server[DaServer] --> Core
    PG[DaServer.Persistence.PostgreSql] --> Server
    Testing[DaServer.Testing] --> Server
    Testing --> Client
    Aspire[DaServer.Aspire] --> Core
    Caps["DaServer.Reminders, Topics, Replication"] --> Server
    RepClient[DaServer.Replication.Client] --> Client

    Protocol[Game.Protocol] --> Core
    GameServer[Game.Server] --> Protocol
    GameServer --> Server
    GameServer -.->|optional| PG
    GameServer -.->|optional| Caps
    GameClient[game client] --> Protocol
    GameClient --> Client
    Tests[Game.Tests] --> GameServer
    Tests --> Testing
    GameServer -.->|"apphost.cs uses it, reads wire.manifest"| Aspire
```

- Everything points one way: `DaServer` never references a capability, a sample or a user project; `DaServer.Core`
  references only Nino; `DaServer.Client` never references `DaServer`.
- `Game.Protocol` references only `DaServer.Core`: the analyzer refuses actor types, server messages, `ActorId`,
  `PresenceId` and `EntityId` there, and server-only APIs are not in `DaServer.Core` to begin with.
- `apphost.cs` takes `DaServer.Aspire` by `#:package` and `Game.Server`'s project metadata by `#:project`, and the
  generator reads that project's `wire.manifest`; no server assembly is referenced. `DaServer.Aspire` references
  `DaServer.Core`: the generated server types implement `IServer`.

## 2. Runtime structure

Users see four words: **server type** (`MapServer`), **node** (a process running one server type), **actor**
(`Map(7)`: its root entity and all its descendants) and **entity/component** (EasyEcs's own). A node's threads are
the framework's business; inside DaServer one thread with its EasyEcs world is a **shard**, a word that appears in no
public API, attribute, configuration key or AppHost call.

| Level | What it is | Owns |
|---|---|---|
| Node | One process, one server type, a node generation it picks when it joins the membership table ([draft §2](programming-model.md#2-服务器与节点)) | Links, membership (its view of the table), placement view, its shards, the HTTP plane, `DueScan`, and the node state (`StoreDegraded`, `ActivationSlots`, `PeerHealth`, `RestartingNodes`) as one immutable snapshot swapped atomically |
| Shard (internal) | One thread and one EasyEcs world; the actor types the server hosts share it; an actor lands on a shard by its key's hash. A node runs one shard per CPU it is given (the container's CPU request, `DaServer:Worlds` overrides) | The dispatch queue, the framework systems, singletons (`Time`, `Ordered<K>`, `Current`, `Outbox`) and its copies of the node singletons, the due heap, the chain stack, the call index |
| Actor | Its root entity (carrying the actor type component, e.g. `Map(7)`) and the root's descendants | Its mail, its `Due` items, its presences, its waiting asks |
| Entity | A root, an avatar, a presence, a waiting ask, a scheduled event | Components; `In<K>` on every entity of a K actor; `Persisted` on every persistent one |

- **Parallelism**: many actors per shard, many shards per node, many nodes. Inside an actor the ordered phase is
  serial. Each world sets `WorldOptions.MaxDegreeOfParallelism` = node cores / node shards, at least 1 (EasyEcs D36:
  the budget is per world); with the default of one shard per core that is 1, so `[ForEach(Parallel = true)]` takes
  effect only when a server type runs fewer worlds than cores (`DaServer:Worlds`, e.g. one huge map per node).
- **Node singletons**: node-level state that user code may read (`RestartingNodes`) or the runtime consults (`StoreDegraded`, `ActivationSlots`, `PeerHealth`) is one immutable snapshot the node swaps atomically. Each world holds its own copy as a world singleton, refreshed from the snapshot at the start of each batch and tick, so world threads read it without locks and an item sees one value throughout.
- **Node-wide limits are per-world shares**: a stale snapshot cannot hold a counter, so the two node-wide budgets
  (`ActivationSlots`' concurrent epoch claims and `PeerHealth`'s in-flight requests per destination node) are split into
  shares, one per world, recomputed at each refresh from the node's limit and the world's recent use. A share never
  drops below what the world already has in flight (only the remainder is handed out), and when the worlds outnumber
  the limit some get 0, the non-zero shares rotating among the worlds with demand at each refresh; so the shares
  always sum to at most the limit. A share is recomputed only from what the worlds have reported releasing at the
  refresh (each world publishes its in-flight count then), never from an older published count, so the sum of what is
  really in flight cannot pass the limit between refreshes. A world spends only its own share, a plain field on its thread. The node never
  overshoots its limit and no counter is contended; the cost is that a burst on one world waits for the next refresh
  before it borrows another world's unused share.
- **The dispatch queue** is the only cross-thread handoff: IO threads and other shards post frames to it; the shard
  thread moves them into each actor's mailbox. **Admission is per actor**: a mailbox holds at most
  `[ActorOptions(MaxMail, MaxMailBytes)]` (4,096 items, 4 MiB by default), and past it only that actor refuses
  (`Busy`, one-way mail dropped and counted), so a hot actor (a world boss, a flash-sale stock shard) never makes its
  neighbours on the shard answer `Busy`; the dispatch queue's own bound (`DaServer:DispatchQueueCapacity`) is the
  node's memory guard behind it (Akka bounds mailboxes per actor, Orleans caps queued requests per grain). Room is
  kept for answers and presence notices: an answer's room is reserved in bytes at both ends (the receiver reserves its
  outbound link window when it admits the request, the asker its dispatch queue when it asks; section 3), and
  presence notices are bounded by the join rate; both travel in the pair's order like any mail (section 3's links;
  [draft §11](programming-model.md#11-失败时的结果)).

### Terms

The draft uses these before or apart from the section that defines them:
- **Subject**: the entity a message or event acts on; a handler's component parameters bind to it ([draft §5](programming-model.md#5-逻辑)).
- **Ordered phase** (`OrderedPhase`): the framework system that runs, at the start of each tick, the mail and due items one by one by `(time, seq)`.
- **Immediate chain**: `Schedule` without `after` inside an item, run right after the item; a value on the world's chain stack, not an entity. Outside an item (in a system) `Schedule(ev)` is `Schedule(ev, after: TimeSpan.Zero)`: a `Due` child at `Time.Elapsed`, run in the next ordered phase ([draft §9](programming-model.md#9-时间与广播)).
- **`Due`**: an instant; delayed events, request deadlines and join confirmation deadlines are entities with a `Due`, ordered by the runtime's heap.
- **`Outbox`**: what an item sends, serialized on append into pooled frames per destination and handed to the links after each ordered phase and once at the end of the loop iteration.
- **Epoch**: the number an activation claims; record writes compare and swap it, so only one activation of an actor writes.
- **Placeholder**: the root entity of an actor not yet active or being saved; mail waits on it.
- **Settling**: step c of a tick: `Depart`, `SyncSubscriptions`, and removing `Restarting` and `Resumed`.
- **Tick-end write**: at the end of every tick (actor types without `[TickRate]`: after every mail batch) a world writes the roots due for a write asynchronously in one store statement (ADR-068's `unnest`; one more per 512 rows, at most `DaServer:Store:WritesInFlight` in flight) ([draft §10](programming-model.md#默认异步成批地写); its runtime data below).
- **Held output**, **write generation**: the only output that waits for a record is what an item that called `root.Commit()` sends; it carries the generation of the root's write that holds the item, leaves when that write succeeds, and holds back the root's later output to the same destination actor; a held group push is appended to its group's frame only on release, so it orders against other items' group pushes by release (the one exception besides migration); held output is bounded per root and per world (`Busy` beyond); the actor keeps running meanwhile ([draft §8](programming-model.md#贵重的改动rootcommit)).
- **Connection stamp**, **`Retire`**: section 3.
- **Node singleton**: above.

### The shard loop

```text
loop on the shard's thread:
    wait for frames in the dispatch queue, the next due tick, or the due heap's head
    if the volatile frozen flag is set, or the lease clock has passed the published lease deadline (the node is Dead,
            isolated or restarting): run nothing (the world is frozen); drain nothing either, except in a hot
            restart's short freeze, whose IO keeps receiving (an isolation freeze keeps mail in the bounded dispatch
            queue, so a full queue answers Busy rather than growing mailboxes)
    unless the world's write backlog is past DaServer:Store:BacklogPause (then the queue fills and answers Busy):
    drain the dispatch queue (one bounded batch) into the actors' mailboxes, each bounded by MaxMail/MaxMailBytes
        (a full mailbox refuses that actor's mail only), split by actor type,
        at most a budget of items per actor (the rest waits in its mailbox for the next batch, still by (time, seq));
        a message's time is its arrival, stamped when the link reads the frame, so mail drained after a freeze
        still sorts by when it came, ahead of answers and events that came later
    for each actor type K without [TickRate] that has mail or due items, or whose quiet pass is due
            (every DaServer:QuietPass, 1 s, so that time-gated systems run in an idle world; the chain stack is empty between items):
        write Time         K's Tick, TickRate and DeltaTime (all 0 here) from Ordered<K>; Elapsed = the pass start (now), Utc;
                               Elapsed and Utc again per item
        Ordering<K>        OrderedPhase: chain stack, due heap (At <= now) and mailbox merged by (time, seq)
        hand Outbox frames to the links (output held by Commit stays, tagged with its write generation),
            after checking the lease deadline again: frames of a pass the lease ran out in stay in the Outbox
        write Time         Elapsed back to the pass start, Utc read once
        world.Update<K>()  K's systems, Time.DeltaTime 0
        Settling<K>        Depart, SyncSubscriptions, Restarting and Resumed removed
    for each actor type K with [TickRate], for each of its due ticks t, oldest first, at most the catch-up cap:
        write Time         K's Tick (advanced in Ordered<K>), TickRate, DeltaTime, Elapsed = t, Utc
        Ordering<K>        the window (t - Δ, t]
        hand Outbox frames to the links
        write Time         Elapsed back to t, Utc read once
        world.Update<K>()
        Settling<K>
    write Time           Utc and Elapsed; Tick, TickRate and DeltaTime keep the last pass's values, which Cleanup does not read
    Cleanup              per world: ReleaseRoutes, the Persisted cleanup, WakeSystem, then CheckpointSystem (collects
                               the ids of WriteNow roots first and of Unsaved roots whose staggered checkpoint instant has
                               passed, oldest unsaved first, keeping at least a quarter of each round's rows for checkpoint
                               roots when both wait, skipping roots with a write in flight, as many as the free statements
                               hold, and clears their flags; it saves nothing: World.Save throws inside a pass)
    StoreWrite           per world, outside world.Update: Descendants and SaveEntitiesWith for each collected root and each
                               deactivation (a framework job only marks them), then the tick-end write goes out when it
                               holds any record, one statement per 512 rows or DaServer:Store:StatementBytes, rows sorted
                               by (type_id, key), at most DaServer:Store:WritesInFlight in flight; held output is released
                               as its rows succeed
    hand Outbox frames to the links
```

- **Time-gated systems**: EasyEcs's `UpdateInterval` counts group updates, not time (`update % interval`), and an actor
  type without `[TickRate]` updates once per mail batch, irregularly and never while idle. A system that must run every
  N seconds there declares `[EverySeconds(n)]`: the runtime skips it until `Time.Elapsed` passes its next instant, and the
  world's quiet pass (above) guarantees an update at least every `DaServer:QuietPass`. `UpdateInterval` keeps EasyEcs's
  meaning and is for ticking types, where updates are regular.
- Ordering state (tick number, sequence counter, tick window, chain budget) is per actor type, the singleton `Ordered<K>`.
  `Time` is one world singleton, so the runtime writes it at the start of each actor type's pass: actor types with
  different tick rates share a world, and each pass sees its own type's `Tick`, `TickRate` and `DeltaTime`
  ([draft §9](programming-model.md#时间)); before `Cleanup` it writes only `Utc` and `Elapsed`, the values the per-world
  Cleanup systems read.
- `WriteNow` is set only by `root.Commit()` (and `root.IssueClientToken`, which commits), in handlers and systems
  alike, so an item of a system rides the same tick's write ([draft §8](programming-model.md#贵重的改动rootcommit)).
- **Fixed step with a catch-up cap** (EasyEcs's pattern, `guide/hosting.mdx` "A fixed step that catches up", applied per actor type): a shard that fell behind (a long tick, a GC pause)
  runs the missed ticks one by one, each with the same `Δ`, but at most a few per wake (node configuration); past the
  cap it skips the rest and moves its tick clock on, counted in `daserver.ticks.skipped`. Items keep their exact
  times, so a skipped tick shifts no order, it only widens the next window.
- **Memory after a peak**: `world.TrimExcess()` runs only explicitly: a framework system in
  Cleanup with a long `[EverySeconds(…)]` once the world's rows have dropped well below their peak (after a scale-in or
  migrations out), and the admin command `POST /admin/worlds/{i}/trim`; never on a hot path.
- Server types hosting several ticking actor types keep rates that divide the fastest one.
- Before a hot restart (the member is `NodeRestarting`) the runtime adds the `Restarting` tag to every root for the
  last tick (Settling removes it in that tick, so no handover carries it) and then stops calling the loop; after the load and the cleared `restart_until`
  every root carries `Resumed(Pause, Aborted)` for one tick ([draft §10](programming-model.md#热重启)).

### Runtime data

The draft names what users touch; everything below is internal and shows up in diagnostics and metrics (names as
exported, OpenTelemetry). Names users never write but meet in diagnostics: the SDK's slot numbers, `Ready`,
`JoinConfirmed(slot, call id)`, `PresenceClosed(slot)`, `PresenceLost(slot)`, `HandedOff(slot)`, `Superseded`,
`Unavailable(retryAfter)`; the runtime's `Mail`, `Unsaved` and the rows below.

| Data | Kind | Role | Metric |
|---|---|---|---|
| `Due(TimeSpan At, long Seq)` | component | Delayed events, request deadlines, armed `WakeAt` instants, join confirmation deadlines, a `Detached` presence's grace, join-on-connect back-off: one timer mechanism ([draft §9](programming-model.md#9-时间与广播)). Carried by migrations and hot-restart handovers, not in records (live only) | — |
| Live-only components | components | `Due`, `Presence`, `Joined`, `Detached`, `Leaving`, `Subscribed`, `Route`, `GroupRate` (the root's send rates, set by `SetGroupRate`; the root copy of a ticking actor type carries the empty buffer, and `Join()` adds an empty `Subscribed` buffer, so `SetGroupRate` and `SetGroups` are not structural changes): meaningful only in memory, so they migrate and are handed over but never go into records | — |
| `FromPrefab(int Prefab, int Composition)` | component | On every entity spawned from a registered prefab copy, persistent or not, the root included: the prefab's id and the hash of its component set, both from `wire.manifest`. On every load (record, migration bytes, hot-restart handover) the runtime adds the copy's `[NotSaved]` components the entity lacks and the components the prefab gained since `Composition` (known from the earlier compositions the manifest keeps; components removed at run time are not added back), then updates `Composition`, tagging the root `Unsaved` when it added persisted ones ([draft §10](programming-model.md#恢复)) | `daserver.records.unknown_types` |
| `Persisted(EntityId Root)` | cleanup component | On every persistent entity (from a persistent prefab's registered copy; the root's points at itself): `ChangeSystem` filters by it, the record collects it, and destroying a persistent descendant tags its root `Unsaved` ([draft §10](programming-model.md#要保存的是实体不是组件类型)) | — |
| `Deactivating` | tag on the root, declared `ITracked` | The last pass before a deactivation; declared `ITracked` because EasyEcs's `RemovedAttribute<T>` requires it, so `[Removed<Deactivating>]` sees a cancelled idle deactivation (`DaServer.Topics` re-subscribes there, [draft §15](programming-model.md#15-扩展能力可选包)) | |
| `Pending` | component on the root | Pending delayed events and waiting asks (the `MaxPending` count); idle deactivation considers only roots where it is zero ([draft §6](programming-model.md#6-生命周期)) | `daserver.actors.pending` |
| Tick-end write | runtime value per world | The records taken this tick (`WriteNow` roots first, then checkpoint and deactivation ones), sent as `IActorStore.SaveAsync` calls (one statement per 512 rows, as today); at most `DaServer:Store:WritesInFlight` statements per world in flight (default 2), never two writes for one root; roots beyond wait with their tags for the next round ([draft §10](programming-model.md#默认异步成批地写)). Each statement holds at most 512 rows and `DaServer:Store:StatementBytes` (8 MiB), rows sorted by `(type_id, key)` so racing writers lock in one order; a record above `DaServer:Store:MaxRecordBytes` (4 MiB) is unwritable. Outcomes come back per row: a refused row fences only its actor and discards only its held output; a transient failure (a timeout, cancelled first, a lost connection, `40001`, `40P01`, `53xxx`, `57P0x`) is retried with the same bytes and write generation, except that a statement which timed out three times in a row is halved (and halved again on the next three), since an 8 MiB statement the statement timeout can never fit would otherwise hold the world's write slots forever; a deterministic one (a value too large, a constraint, a data error) bisects the statement until the one row is found, which takes the `RecordUnwritable` path while the rest lands; a root whose `SaveEntitiesWith` throws is left out of the statement and logged (`RecordUnwritable`), the rest of the batch goes and the root is tagged `Unsaved` again for the next interval (on deactivation, stop, migration and freeze: section 4) | `daserver.store.writes_waiting`, `daserver.store.oldest_unsaved_age` |
| `Writing`, `WriteNow`, write generation | fields of `RootState`, a framework component every root carries from its prefab copy | Fields, not tags, so setting them is no structural change (`Commit` stays inside `INoStructuralChanges` jobs). `Writing`: a record write of the root is in flight; `CheckpointSystem` skips the root, so one process never overlaps two writes; changes meanwhile keep or regain `Unsaved` and go in the next write, which carries the latest state. The CAS compares the epoch and the write generation (`write_gen < :write_gen`, reset to 0 by every epoch claim, continued across a hot restart's handover), so a timed-out statement landing late never overwrites newer state ([draft §10](programming-model.md#激活-epoch一个-actor-只有一个激活能写)). `WriteNow`: the root called `Commit` (in a handler or a system), so the next tick-end write takes it whatever the checkpoint interval; a `Commit` with nothing unwritten writes nothing and releases at once. The write generation counts the root's records taken; held output carries the generation its item's state goes into and leaves once a write of that generation or later succeeds ([draft §8](programming-model.md#贵重的改动rootcommit)) | `daserver.commits.held`, `daserver.commits.held_bytes`, `daserver.commits.latency` |
| `OnceNext` | field of `RootState` (persisted) | The sender's `[Once]` sequence counter: monotonic, never reused, taken by `AskUntilAnswered` and written with the item that took it (the ask implies `Commit`), so a crash, a migration or a handover continues it ([draft §8](programming-model.md#去重订单是自己的-actor转移价值的那一步是-once)) | — |
| `Asking` | persisted framework child entity under the entity that called `AskUntilAnswered` | One `[Once]` ask still waiting for its answer: target, sequence number, request bytes, state, and a `WakeAt` for the next try. The runtime re-asks after link outcomes (`RetryAfter`, else `DaServer:Once:Retry` with backoff and jitter) and after crashes (the `WakeAt` through `DueScan`); only a value or the receiver's definite refusal is delivered as `Replied`, in an item that commits implicitly and destroys it. The sender's cumulative acknowledgement to a receiver is the lowest sequence among its `Asking` children for that receiver, else `OnceNext`. A sender holds asks beyond the receiver's advertised window locally; past `[ActorOptions(OnceOutstanding)]` (1,024) `AskUntilAnswered` returns `Busy` | `daserver.once.asking`, `daserver.once.oldest_age` |
| `Unacked`, `OnceSenders` | persisted `DynamicBuffer`s on the root of every actor type that receives a `[Once]` request; the root copy carries them empty, in the runtime's persisted set | TCP's receive side. `OnceSenders`: per sender (an actor's `ActorId`, or a client's `(ClientId, session)`) the mark `n` (every sequence at or below it is settled or never sent here), the advertised window used, and when it went quiet (for a client also the session's end). `Unacked`: (sender, sequence, the stored reply of at most 32 bytes, the write generation). The dispatch checks a `[Once]` request before its handler: at or below `n`, `Stale` (refused, counted); in `Unacked`, the stored reply (held until that generation is written); the sender's window (`[ActorOptions(OnceWindow)]`, 16) or the root's total (`[ActorOptions(Unacked)]`, 1,024) full, `WindowFull` with the current window; otherwise the handler runs once, commits implicitly and its reply is stored. Every mail from that sender carries its cumulative acknowledgement `a`; entries below `a` are dropped and `n` moves to `a − 1` (no commit needed: a crash restores older entries and the next acknowledgement or probe clears them again). An entry unacknowledged for `DaServer:Once:ProbeAfter` (30 s) with a quiet sender makes the receiver send `AckQuery` (the sender actor is activated to answer from its record, a missing record answers "all settled"; a client session's gateway answers from its session table). A sender's row stays `MaxAskTimeout + ClockSkew` after its last entry goes (TCP's TIME-WAIT), since no older attempt can arrive later | `daserver.once.entries`, `daserver.once.duplicates`, `daserver.once.stale`, `daserver.once.window_full`, `daserver.once.probes` |
| `WakeAt(DateTime Utc)` | persisted component on persistent entities | When the actor must be awake: an `Asking` child's next try, a reminder's next firing (framework and package code write it, user code does not); while active, `ArmSystem` arms `Due = Elapsed + (Utc − Time.Utc)` on load and arrival (`[With<WakeAt>, Without<Due>]`), re-arms it when `WakeAt` is written (`[Changed<WakeAt>]`: the `Due`'s `(At, Seq)` changes and the heap skips the old entry) and removes it when `WakeAt` is removed (`[Removed<WakeAt>]`), after which only the monotonic `Due` counts; when it fires, the wake handler registered for the entity's component runs in that item (the runtime's re-ask for `Asking`, `DaServer.Reminders`' delivery) and writes the next `WakeAt` if there is one | — |
| `IssueSeq` | persisted component on the root | The client-token issue sequence `root.IssueClientToken` increments ([draft §7](programming-model.md#7-客户端与-presence)) | — |
| `NextDue` | persisted component on the root | The minimum `WakeAt` of the root and its descendants, computed only by the runtime: `WakeSystem` (from `[Changed<WakeAt>]`, `[Removed<WakeAt>]` and the `Persisted` cleanup; it tags the root `Unsaved` whenever the value changes, since `NextDue` is the runtime's and not in the generated `ChangeSystem` filter) runs before `CheckpointSystem`, which takes every record; every write puts it into the record's `next_due` column ([draft §10](programming-model.md#唤醒wakeatnextdueduescan)) | — |
| `DueScan` | node system | Every `DaServer:DueScanInterval`, `IActorStore.DueKeys(slots, until, after)` over the slots the node owns (`until` fixed for the scan; the first page's `after` is a sentinel below every real row, each later page's the previous page's last row, which `DueKeys` returns; each page probes only the pairs at or after the cursor's, taking at most 512 rows from each through `LATERAL … LIMIT`, so no page sorts the backlog of every later pair); sends `Wake` (the one control mail that activates) to keys not active; an activation by `Wake` always rewrites `next_due`. The query does not exclude active keys (the store cannot tell); an active actor removes or moves its `WakeAt` as each instant comes, so its key shows up only between an instant and the checkpoint that writes the change: at most the instants of the last checkpoint interval, and its world drops the `Wake` | `daserver.wakes` |
| `Joined(bool MissedPushes)`, `Detached` | component and tag on a presence | Confirmed, and whether a push may have been missed (false only after a `Retire` behind the `HandedOff` fence); a presence of a lost connection in its grace period ([draft §7](programming-model.md#7-客户端与-presence)) | — |
| Due heap | runtime index per world | A 4-ary min-heap of `(At, Seq, EntityId with version)`; lazy deletion: a popped entry is valid only if its entity is alive (same version) and its `(At, Seq)` equals the entity's current `Due` (an intent or a reminder re-armed on the same entity keeps its version, so the version alone would fire it early); others are skipped. Rebuilt after `world.Load`, and for a migration's arrival only once its claim has succeeded (an arrival does not run before its claim, so its entries are not in the heap until then, neither skipped nor fired), and compacted in place (stale entries dropped, heap rebuilt in O(n)) whenever the stale count (heap size − `CountOf<Due>`, O(1)) passes the live one: most asks are answered in milliseconds while their timeout entry waits for its `At`, so without compaction the heap would hold rate × timeout dead entries. So the heap holds at most twice the world's live `Due` entities, which `MaxPending` bounds per actor. EasyEcs's ordered value index is a sorted array, so each pop would move the whole array | `daserver.dueheap.entries{state}` |
| Chain stack | runtime value per world | `Schedule` without `after`: the event as a value, run right after the item; empty between items, so never saved | — |
| Call index | runtime hash index per world | Call id → the waiting entity (`EntityId` with version), so a `Replied` finds its asker in one lookup | — |
| `Ordered<K>` | singleton per actor type | Tick number, sequence counter, current tick window, immediate chain budget | — |
| `Current` | singleton | The running item's time, root, sender and client context; operations read the client context from it, and its absence is a typed outcome (`Join()` returns `NoClient`) | — |
| `Outbox` | singleton | Everything an item sends (`Send`, `Ask`, `Reply`, `Push`, join routes), serialized on append into pooled `FrameWriter`s per destination node (a group push once, one frame per gateway); handed to the links after each ordered phase and once at the end of the loop iteration (a group with a rate from `SetGroupRate` only on its due tick, and before any membership change of that group, [draft §9](programming-model.md#广播)), each destination in append order, so a join's route always precedes its reply; what an item that called `Commit` sends is held apart per root (pooled) with its write generation, released in order into the destination frames when that write succeeds and discarded on fencing (a held group push is appended to its group's frame only then, a reply whose deadline passed is dropped, a held `[Latest]` message is replaced like mail), and the root's later output to the same destination actor queues behind it, while everything else leaves at once; held output is bounded (`DaServer:Commit:HeldBytesPerRoot` and `HeldBytesPerWorld`), past which new requests to the root are answered `Busy` before they run; `TestActor<K>` decodes it for assertions | — |
| `Time` | singleton | `Tick`, `DeltaTime`, `TickRate` (written at the start of each actor type's pass, from its `Ordered<K>`), `Elapsed` (monotonic, process-local), `Utc` (the world's `TimeProvider`, read once per item) | — |
| `RestartingNodes` | node singleton | The members in `NodeRestarting`, with their expected pauses; `TryGetPause(connection, out pause)` for gateway systems that tell clients | `daserver.members{state}` |
| `StoreDegraded`, `ActivationSlots`, `PeerHealth` | node singletons | The store is slow or down (no idle deactivation, `Saving` placeholders capped); concurrent epoch claims (jittered back-off); in-flight requests and latency per destination node with an adaptive limit (fail fast with `Busy`) ([draft §6](programming-model.md#6-生命周期), [§11](programming-model.md#11-失败时的结果)) | `daserver.store.degraded`, `daserver.activations.claiming`, `daserver.peer.inflight`, `daserver.peer.limit` |
| Placeholder | entity | A key not yet active or being saved: the copy of `K.RootPrefab` without `In<K>`, the key, `Loading`, `Arriving` (a key of a handoff-pending slot waiting for its source's answer, section 4), `Saving` or `ActivationFailed(until)`, a bounded `DynamicBuffer<Mail>` (`MaxWaitingMail` per key and `DaServer:WaitingMailBytes` per node, shared out per world); a throw in a persisted actor (reload) turns a root back into one in place, keeping only the key and the `Mail` (values reset to the prefab copy's) | `daserver.activations.failed` |
| Internal lane | frames no handler can relate to a message | Heartbeats, membership and view exchange, link acks, `Fence(key, epoch)`, `Wake` and `HandoffQuery`: written ahead of queued data so failure detection never waits behind bulk traffic; never `Busy` (section 3's links) | — |
| Presence notices | mail in the pair's order | `Route`, `Lost`, `JoinConfirmed`, `TakeOver`, `HandedOff`, `Missed` (a gateway dropped or conflated a push for that presence, which then gets `MissedPushes`): user-observable, so they travel in the one ordered stream with the messages and group frames of their pair (section 3), with room reserved so they are never `Busy` or dropped, bounded by the join rate times latency (a few per join); mail to a `Connection` is addressed by its `ConnectionId` (the presence records it), so the gateway routes it to the world by that key's hash like any actor's | `daserver.messages.dropped{reason}`, `daserver.messages.superseded` |
| `[Latest]` table | runtime value per actor (migrated and handed over, never recorded) | Per (root, sender, message type): the last unhandled mail (replaced in place when it is the sender's last item to the target, else dropped and the new one appended) and the highest sequence handled or queued; a `[Latest]` message carries `(sender epoch, sequence)` compared in that order, the sender being the sending actor (its epoch: the activation count on its root, saved) or, for a client, its `ClientId` (its epoch: the session's connection serial), the sequence counted per (sender, target, type) within an epoch, and an older pair is dropped, so a sender restarted from a lower counter still wins, so a stale message forwarded by a tombstone or still in flight from an old gateway never replaces a newer one; entries go after `DaServer:Mailbox:LatestWindow` (10 s) without a message | `daserver.messages.superseded` |
| Answer reservations | runtime value per world and per link | Bytes reserved for answers at both ends of their path: the receiver, when it admits a request into a mailbox, reserves the reply's size in its own outbound window to the asker's node (released when the reply is sent or the request's deadline passes; past half the window the request is refused `Busy`), and the asker, when it asks, reserves it in its own dispatch queue (released when the answer arrives or at the deadline; past half, the ask is refused locally `Busy`). The size is exact for fixed-size replies (computed by the generator: a `bool` reserves a few bytes) and `DaServer:MaxReplyBytes` (64 KiB, or `[MaxReplySize(n)]`) for variable ones; an answer larger than its reservation travels as ordinary traffic | `daserver.asks.reserved_bytes`, `daserver.replies.oversize` |
| Membership states | per member, from the membership table | `Joining`, `Active`, `ShuttingDown`, `Dead` (the table's statuses); `NodeJoining`, `NodeLeaving` (placement is changing), `NodeRestarting` (its row holds `restart_until`: a hot restart, announced once a standby passed the handshake; user systems see the `Restarting` tag and `RestartingNodes`), `Rejoining` (the standby that took over reading its row before it clears `restart_until`, or finding itself `Dead`) | `daserver.members{state}` |

### `Registration`

- Generated: `Registration.Create()` is a C# 14 static extension member in the server project's root namespace (its
  container named after the assembly, one per server project whatever server types it defines, so a test project
  importing two server projects calls the container, [draft §5](programming-model.md#其他)), one new instance per call (no static registries, no module initializers):
  actor types with their `K.RootPrefab` prefabs and `[ActorOptions]`, child prefab copies with `In<K>` (and `Persisted`
  for persistent ones), the persist set (computed here at run time from the registered persistent prefabs' components,
  read through EasyEcs request (b); until it ships, by instantiating each prefab once into a scratch world and reading
  the instance's `Entity.Describe()` types, cold and once; the generator cannot compute it, since prefabs are
  built with members another generator writes and a generator never sees another's output; checked against the
  generated tracked-type list, which comes from the `ITracked` declarations in source), each prefab's composition hash (the same way), configuration tables and
  world singletons, claims, join-on-connect, dispatch tables per actor type, client routes, systems per group
  ([draft §12](programming-model.md#12-完整例子连接进地图移动放技能捡东西看见别人)).
- Nodes, `TestCluster` and `TestActor<K>` take the object. Its constructor also checks what only the prefabs can tell:
  a handler's component parameter that no prefab of its subject carries (a client message bound to a root whose
  `RootPrefab` lacks it, for example) throws there, so the first test or node start reports it, never a message at
  run time (`Skipped` stays for an entity that lost a component).
- Every type in it is closed at compile time in the user's assembly (section 8).

## 3. Messaging

| Path | Shape | Route |
|---|---|---|
| Client → server | `IFromClient`, `IFromClient<R>` in Protocol | Client → its `Connection` entity on the gateway → `[On<Incoming>]` filter on the frame header → generated route table → straight to the target actor (no hop through Player) |
| `[To]` parameter | `EnterMap([To] long Map)` | To the actor of the handling type whose key is the parameter; the subject is the root ([draft §4](programming-model.md#4-消息)) |
| No `[To]` | `MoveTo(...)` | To the client's presence on the handling actor type; the subject is the presence entity; none: `NotJoined` |
| Server → client | `IToClient` | `e.Push(msg)` to a presence, `e.Push(in group, msg)` to a `ClientGroup` (`root.Group(key)`; `root.Everyone` is every joined presence of the actor, one fan-out path) |
| Server → server | `IMessageTo<K>`, `IRequestTo<K,R>` with `[From<K>]` | Generated typed `Send`/`Ask` on `Entity`; the answer is `Replied<TReq>` or `Replied<TReq,TState>` mail to the asking entity ([draft §8](programming-model.md#8-等待别的-actor-的结果贵重的改动)) |
| Inside an actor | `IEvent` | `e.Schedule(ev[, after:])`: a chain value, or a child entity with `Due`, run in the ordered phase |
| Actors to topics (optional) | `ITopicMessage` | `e.Publish(topic, msg)` to a `Topic` actor, one frame per subscriber node; `DaServer.Topics` ([draft §15](programming-model.md#15-扩展能力可选包)) |
| Outside actors | `ICluster` | HTTP, back-office, tests: any server-to-server message with a deadline (sent as the budget) or a `CancellationToken`; an external retry asks the same order actor (a payment id is `Payment`'s key), whose state answers it; `cluster.Disconnect(clientId)` |

Client transports are TCP and WebSocket (`/clients/ws`), both behind `IFrameChannel`; a datagram transport is out of
scope for now and would be another `IFrameChannel` (ADR-092).

### A client message, hop by hop

1. **Client SDK → gateway IO thread**: the frame lands in a pooled receive buffer (`BufferPool`).
2. **→ the gateway shard's dispatch queue** (bounded; full: `Busy` to the client), the world chosen by the hash of the
   connection's key, the `ConnectionId` made at accept. The shard finds the `Connection` root by that key.
3. **Filter and route**: `Incoming` is a view of the frame header (`TypeId`, length, claims); the payload is never
   decoded. With an `[On<Incoming>]` method it runs as a job per message; without one the forward is a pass over the
   batch. The route table maps the `TypeId` to the handling actor type; the target key comes from the `[To]`
   parameter or the `DynamicBuffer<Route>` entry of that actor type. Before `Ready`, messages wait on the
   `Connection` (bounded, `Busy` beyond). A connection has at most `DaServer:Clients:MaxInFlight` (64) requests
   unanswered; the gateway answers `Busy` beyond, so one client cannot queue without bound into a hot actor and the
   room answers to a gateway need stays bounded.
4. **→ the target node**: the frame, re-headed with the client context (and the `PresenceId` without `[To]`), goes into
   the `Outbox` frame for that node, handed to the link after the ordered phase; the link's resend window keeps it until
   acknowledged (and while the target is `NodeRestarting`), records when it entered and subtracts the time held from a
   request's budget on replay, so a pause never extends a budget.
5. **Target node IO thread → the dispatch queue of the shard** chosen by the key's hash.
6. **Target shard**: the mailbox snapshot; the root by `world.Index<K, K>().TryGet` (the generic form of the generated `world.MapIndex`; a placeholder if not active, its mail
   bounded); the subject is the root (`[To]`) or the presence by its `PresenceId` index.
7. **Ordered phase**: the handler's job runs; the reply (and a join's route before it) is serialized into the
   `Outbox` frame for the gateway and handed to the link after the ordered phase.
8. **Gateway**: the reply reaches the `Connection`, which writes the client frame (one write per connection per batch,
   ADR-034).

### An ask and its answer, hop by hop

1. **`me.Ask(to, req, state)`**: room for the answer's arrival is reserved first, in bytes (the reply's exact size
   when it is fixed, else the request type's maximum), in this shard's dispatch queue; past half of it, or past
   `MaxPending`, the ask is not sent and a `Busy` `Replied` is delivered at once. Then a waiting child entity with the
   state and a `Due` (inside a request's context at most that request's remaining time; for an ask held by `Commit` it
   starts at the release), an entry in the call index and in the due heap, `Pending` incremented; the request with its
   remaining time is serialized into the `Outbox` frame for the target's node, unless `PeerHealth` is at its limit for
   that node (`Busy` at once). The reservation is released when the answer arrives or at the deadline.
2. **Target**: link → dispatch queue → shard → mailbox (admission reserves the reply's size in the target's outbound
   window to the asker's node, or answers `Busy`) → root → the request's job (dropped and counted if its deadline
   passed); the reply (returned, or later through `Reply(token)`) is serialized into the `Outbox` frame
   addressed by (actor type, key, call id), in the pair's order with the target's other output to the asker, or, when
   the handler committed, held until that write succeeds.
3. **Back at the asker's node**: link → dispatch queue → shard; the reply waits in the asker's mailbox in arrival
   order like any mail and the call index finds the waiting entity when it is dequeued (a tombstone forwards it if the
   asker migrated); the `Replied` mail runs in the ordered phase; `TryGetValue` reads the reply
   bytes from the mail's frame; the waiting entity is destroyed after the handler and its heap entry is dropped
   lazily.
4. **Timeout**: the due heap pops the waiting entity first and the runtime delivers `Replied` with `TimedOut`. A full
   link fails the ask at once with `Busy`.
5. **`[Once]` and `AskUntilAnswered`** ([draft §8](programming-model.md#8-等待别的-actor-的结果贵重的改动)): an order is
   its own actor; the step that moves value into another actor is a `[Once]` request sent by `AskUntilAnswered`, the
   framework's TCP-shaped exactly-once (RFC 9293): the sender's sequence number from `OnceNext`, committed with the
   `Asking` child; the receiver's check against `OnceSenders` and `Unacked` (stale, duplicate, window full or new); the
   reply stored and committed before it leaves; cumulative acknowledgements on every later mail of the pair, a
   framework acknowledgement after the item that handled the answer is written, a probe (`AckQuery`) for a quiet
   sender; per-sender windows instead of refusals. Link outcomes never reach the user: the runtime asks again with the
   same number, and after a crash `DueScan` finds the asker's key through `next_due` and wakes it.

- **Presences** ([draft §7](programming-model.md#7-客户端与-presence)): a client's existence on an actor, at most one
  per actor and at most `[ActorOptions(PresencesPerClient = n)]` per actor type (default 1; the slot is keyed by
  (actor type, key)): a child entity with `Presence` (a cleanup component) on the target, a `Route` in the `Connection`'s
  `DynamicBuffer<Route>`, a slot number in the SDK. `e.Join()` is immediate, returns a `Status` and appends the route
  to `Outbox`; `Leave()` adds `Leaving`, systems react, `Depart` destroys. A lost connection or gateway leaves the
  presence `Detached` for its grace period instead. Stamps, takeovers and route registration: below.
- **Gateway**: a server type with `[Hosts<Connection>]`. `Connection` is pinned to its socket's node, never placed among nodes or
  migrated, activated on connect (admitted by a token bucket), deactivated on disconnect. It filters, routes, keeps
  the routes and fans out groups.
- **`Connection` is keyed per connection**: `Connection(ConnectionId)`, 16 random bytes made at accept, never the
  `ClientId`. One login can hold two connections on one node (a `Retire` that lands on the same gateway, a reconnect
  while a half-open connection lingers), and an actor key is a unique index, which throws on a duplicate key.
  A `Connection` lands on a world by its key's hash like every actor, and every frame addressed to a connection
  (`TakeOver`, `Lost`, `HandedOff`, pushes) routes by the `ConnectionId` alone, which the presence records.
  `ClientOf(ClientId)` on the root is a non-unique value index (`IIndexed<ClientId>`), used only where a `ClientId`
  must be found: `cluster.Disconnect(clientId)` goes to every gateway node, which asks each of its worlds (a rare
  admin operation).
- **`ClientGroup` fan-out**: subscriptions are data (`DynamicBuffer<Subscribed>` on presences); `SyncSubscriptions`
  sends the full set of each changed presence to the gateways, which replace it; a group push is serialized once, one
  frame per gateway with members, fanned out there.
- **Order**: everything one sender sends to one target (answers and presence notices included) arrives in send order
  unless one of them migrates ([draft §1](programming-model.md#1-保证), the links below).

### Links: one ordered stream, fragments and batches

What users are promised is one rule ([draft §1](programming-model.md#1-保证), ADR-096): everything one sender sends to
one target (messages, requests, answers, presence notices) arrives in send order; sends to different targets never wait
on each other. The transport keeps it with one ordered stream per link and lanes only for what users never observe.

- **One stream per link for everything a user can observe.** The `Outbox` already appends `Send`, `Ask`, `Reply`,
  `Push` and the join's route per destination node in append order; answers and the presence notices (`Route`,
  `Lost`, `JoinConfirmed`, `TakeOver`, `HandedOff`) stay in that stream. No sequence numbers are added: the order of
  the stream is the order. On the receiving node a `Replied` goes through the target's mailbox like any mail, found
  by the call index when it is dequeued, never ahead of mail that arrived before it.
- **Answers are never refused, and that is an admission rule, not a lane.** Room for an answer is reserved in bytes
  where it travels: the receiver reserves it in its own outbound window to the asker's node when it admits the
  request (so a map whose window to a gateway is full of pushes still has room to answer that gateway's clients, and
  a full window refuses the request, not the answer), and the asker reserves it in its own dispatch queue when it
  asks; both are released when the answer passes or at the deadline, and new requests or asks are refused past half,
  so 65,536 asks from one actor cannot fill a shard and liveness never waits for a timeout to free room. Fixed-size
  replies reserve their exact size, so a link is not limited to a few hundred `bool` asks in flight by a 64 KiB
  estimate. Presence notices are bounded by the join rate. A full queue or window refuses new messages and new asks
  (`Busy`, one-way mail dropped and counted), never an answer. Dropping a one-way message never reorders the ones that
  survive.
- **Everything one actor sends to one gateway is one pair.** Traffic to a gateway node is many logical targets (each
  `Connection`, the group fan-out), but its pair for ordering and interleaving is (sender actor, gateway node): a
  fragmented group frame, the `TakeOver` and `HandedOff` behind it and a direct push all leave in send order, so the
  push-order fence (below) and "an actor's pushes to a client arrive in order" hold even when a frame is fragmented.
- **A link never disconnects because it is full.** Its buffer is one bounded window: written and unacknowledged frames
  (the resend window) plus those not written yet; when it is full new admissions stop (`Busy`, one-way mail dropped,
  pushes dropped with their presences marked `MissedPushes`, below), while it stays connected and drains. Only
  silence closes a link (ADR-053). The window is at least `MaxMessageSize` plus the answer reservations, checked at
  start, so a 16 MB message is never refused forever.
- **Framework traffic users never observe has its own lane**: heartbeats, membership and view exchange, link acks,
  `Fence`, `Wake`, `HandoffQuery` and `[Once]`'s `AckQuery` and its answer are written ahead of queued data, so failure detection never waits behind bulk
  traffic (Akka's split of system messages, confined, as there, to what no handler can relate to a message).
- **A commit is a barrier per pair**: output held by `Commit` ([draft §8](programming-model.md#8-等待别的-actor-的结果贵重的改动))
  waits per (root, destination actor); later output of the root to that actor queues behind it in the `Outbox`, other
  destinations leave at once.
- **Large messages are fragmented and interleaved.** A message larger than `DaServer:Links:FragmentSize` (64 KiB) is
  split into fragments of that size. The link's writer takes, per flush, the small frames that are ready and at most
  one fragment from each pair with a large message in progress, round robin, so a 4 MB message to one actor delays
  another pair's answer or a heartbeat by at most one fragment (about 50 µs at 10 Gbit/s). Order within a pair holds:
  frames of a pair that has a large message in progress queue behind its fragments. At most
  `DaServer:Links:LargeInFlight` (4) large messages are in progress per link; others wait at the sender, so round
  robin cannot open a thousand partial messages at once. The receiver reassembles per pair into pooled buffers drawn
  from a per-link budget, `DaServer:Links:ReassemblyBytes` (64 MiB); the first fragment carries the whole size, so
  admission (the target's mailbox, the budget) is decided before anything is reassembled and a message that cannot be
  admitted is refused whole. A fragment is acknowledged on the link only once its whole message is reassembled
  (the link's acknowledgement covers whole messages), so partial buffers are never state anyone must keep: on a link
  reconnect, a peer's hot-restart handover (same generation, new process) or a generation change they are dropped and
  the resend window, which still holds the unacknowledged message from its first fragment, sends it again. `MaxMessageSize` (16 MB,
  today's `FrameHeader.MaxFrameSize`) still bounds a message. This is
  Erlang's fix since OTP 22 ([release](https://www.erlang.org/news/132)): fragments in the transport, the same order in
  the semantics.
- **Small frames are coalesced; one write per flush.** What stays from today: a `SendQueue` per link and per client
  connection copies frames into one pooled batch buffer under a short lock while one writer takes whole batches, so a
  shard's hand-over after its ordered phase, the frames of every shard that arrive while a send is in flight, and the
  fan-out of one group frame all leave in one `send` (ADR-034's one write per connection per batch); the optional beat
  (`FlushInterval`, ADR-070: off on links, 2 ms on the sample's gateways, `ClusterClient.FlushInterval` for clients)
  bounds the delay where cores are the limit. What is new: a flush that carries a fragment hands it to the socket
  without copying, as a gather write (`Socket.Send(IList<ArraySegment<byte>>)`, one `sendmsg` with the batch buffer
  and the fragment's slice of its pooled writer), so a flush is still one system call however large the message; a
  flush takes at most `SendQueue`'s batch size (bounded per connection, as today). Copying small frames stays: ADR-034
  measured that the copy is not the cost.
- **Receiving reads many frames per call**: each link and connection reads into a pooled 64 KiB buffer and parses every
  complete frame in it before the next `recv` (today's receive loop), handing them to the dispatch queues as one batch.
- **Acks and heartbeats ride on data.** A link's acknowledgement (bytes received) travels in the header of the next
  outgoing frame; a separate ack or heartbeat frame is written only when the direction has been idle (ADR-053's "pings
  only fill idle directions").
- **Gateways and clients**: a gateway writes each client connection once per batch it handles (the group frames and
  replies of one batch together), the SDK writes one batch per synchronous burst of the application's sends, and both
  follow the same fragment rule for large pushes and requests.
- **Pushes lost are made good, not silent.** A push dropped at a full link (the sending world finds the affected
  presences from their recorded gateway and subscriptions) or at a gateway (which sends the target a `Missed(PresenceId)`
  notice) marks those presences `Joined.MissedPushes`, so the snapshot system resends their state, at most
  `DaServer:ResyncsPerTick` (64) presences per actor per tick (the rest the next tick), so a gateway's 100,000
  reconnecting clients do not trigger thousands of full snapshots in one tick. A slow client is conflated, not cut: past
  its `MaxPendingSendBytes` (at least `MaxMessageSize`, checked at start) the gateway keeps only the newest frame per
  group, marks the presences whose frames it dropped, and disconnects only when the backlog lasts
  `DaServer:Clients:SlowClientGrace` (10 s).
- **Rate-limited group frames flush early at a size**: a group's frame that reaches `DaServer:GroupFrameMaxBytes`
  (256 KiB) is handed over before its due tick; order holds, only the rate is exceeded.
- **`[Latest]`** (draft §4): the target's mailbox replaces a sender's unhandled older message of the type in place when
  it is the sender's last item to that target (so 100,000 senders at 30 Hz behind a backlog are each still served), and
  drops it and appends the new one otherwise; a per-(sender, target, type) sequence, the sender a client's `ClientId`,
  keeps a stale message forwarded by a tombstone or an old gateway from replacing a newer one.
- Proved by BDN (W4): system calls per message at 10,000 connections under a broadcast and a request load, and the
  latency of a small answer behind a 4 MB message on the same link, against today's code.

### Connection stamps

The client protocol's internals behind [draft §7](programming-model.md#7-客户端与-presence).

- **A token** is a `SignedToken` whose content is `(ClientId, issue sequence, claims)`. The issue sequence is a
  per-account counter the issuer persists before the token leaves (`root.IssueClientToken` keeps it in `IssueSeq` on the
  root and commits, so the token leaves after the write; the mmo sample's account actor does so) and increments on
  every login; a refresh keeps it, so a refreshed token on an old device never outranks a newer login. A login is its
  `ClientId`: one login's reconnects share it.
- **A connection stamp** is `(issue sequence, ClientId, attempt)`. The attempt is a per-login counter the SDK carries
  in the handshake: it starts at the Unix milliseconds when the SDK starts (so an app restarted with the same token
  still outranks its old connection) and grows by one per connection attempt. Two connections of one login (the same
  issue sequence and `ClientId`) are ordered by the attempt alone, never by the gateway: a reconnect through a gateway
  with an older generation still takes over. A larger attempt takes over and the older connection closes quietly; a
  smaller one (a stale attempt arriving late) closes quietly too; an equal one is the same connection retrying,
  answered again idempotently. A device clock moved back would make every new attempt smaller than the one a target
  holds: a `Detached` presence therefore never refuses a newcomer of its own `ClientId` (it was lost; the newcomer takes
  it over and its attempt becomes the stamp), and a refusal of a smaller attempt carries the target's current attempt,
  above which the SDK continues (`attempt = max(own + 1, reported + 1)`). Between different `ClientId`s the larger issue sequence takes over and an equal one
  counts the newcomer as the newer login; only the side with the other `ClientId` and the smaller sequence (on a tie,
  the older connection) gets `Superseded`, which the SDK treats as terminal.
- **Two connections of one login** are never compared on a gateway, even on one node (a `Retire` that lands on the
  same gateway, a reconnect while the old connection is half-open): the stamps settle them at the targets. Each target's `TakeOver` reaches the old connection by its `ConnectionId`, whichever world holds it. A
  retiring old connection (it received `ControlOp.Retire`) stays until the takeovers have moved its routes and its
  open calls are answered; a non-retiring one closes quietly at the first `TakeOver` of its own `ClientId`, its
  remaining presences go `Detached`, and the new connection's joins take them over with `Joined.MissedPushes` true. A
  stale newcomer (a smaller attempt) closes quietly once its joins come back `Superseded` under its own `ClientId`.
- **A presence** remembers its connection: `Presence(PresenceId Id, ClientId, node, ConnectionId, stamp)` on the target, and
  `Route(ActorId target, PresenceId, Slot)` in the `Connection`'s `DynamicBuffer<Route>`.
- **Takeover** (`Join()` with a larger stamp): the target drops `Joined`, `Leaving` and `Detached`, draws a new
  `PresenceId`, releases the old route by the old `(PresenceId, stamp)` (so a late release or leave from the old
  connection matches nothing), and appends `TakeOver(old ConnectionId, new stamp, new ClientId)` for the old connection's gateway (the node
  and connection recorded on the old presence, across gateways too; the gateway routes it by the old `ConnectionId`). The old `Connection` compares by the stamp rule above: another
  `ClientId` with a larger or equal issue sequence closes it with `Superseded`; the same `ClientId` (the same login on
  a new connection, or a `Retire`) closes it quietly, a retiring one only once its routes have moved and its open calls
  are answered. If it drops half-way, its presences go `Detached` and the new connection takes them over with
  `Joined.MissedPushes` true.
- **`[JoinOnConnect]`**: the activated `Connection` sends an internal join request carrying its stamp (and the
  handshake's retire flag, so a retired client's `[JoinOnConnect]` presences get the `HandedOff` fence too) to the actor
  whose key has the claims' shape; that side creates or takes over the presence by stamp and `Takeover` (`Reject`
  refuses only another `ClientId` while the presence is `Joined`: the same `ClientId` always takes over by attempt, and a
  `Detached` presence never rejects); failures
  retry by `RetryAfter` or jittered back-off (joins are idempotent by stamp) until `Unavailable(retryAfter)`.
- **Registering a route** after `Join()` (asynchronous):
  1. The route precedes the request's reply in the same `Outbox` frame, so the `Connection` writes the route before it
     hands the reply to the client.
  2. When the client already holds `PresencesPerClient` routes of the actor type to other targets, the oldest is
     removed first and `(old PresenceId, stamp)` sent to its target, which `Leave()`s that presence if it matches (with
     the default of one, joining map 8 leaves map 7). That route's slot is released; the new route gets a slot keyed
     by (actor type, key).
  3. The `Connection` then sends `JoinConfirmed(PresenceId)` to the target before forwarding anything (same source and
     destination, so the confirmation arrives first; a lost one is implied by the first forwarded message) and
     `JoinConfirmed(slot, call id)` to the SDK ahead of the reply, so the SDK knows which message joined the slot.
  4. The target adds `Joined(MissedPushes)` and drops the confirmation deadline; `MissedPushes` is false only when the
     client moved because of a `Retire` (the join carries the flag, from the SDK's re-join or from the `Connection`'s
     `[JoinOnConnect]` join, and the old route holds a `HandedOff` fence).
  5. If the `Connection` is gone, the confirmation deadline passes and the target `Leave()`s the presence, the path of a
     client that disconnects right after joining.
  6. A request whose client already timed out still writes the route; only the reply is dropped.
- **Losing a presence**: `ReleaseRoutes` (framework group `Cleanup`, `[With<Retired>, With<Presence>]`, no `In<K>`)
  appends `Lost(PresenceId, stamp)` for a presence destroyed without `Leave()`; the `Connection` removes the route
  when it receives `Lost`, when a forward comes back `NotJoined`, or when the target's node leaves the view, and then
  delivers `PresenceLost`.
- **When notices get lost**: when a link between two nodes reconnects, the target lists its presences per gateway and
  the gateway names those it no longer knows, which go `Detached`; when a gateway leaves the view, every node marks
  the presences recorded on it `Detached`. Presence notices travel in the pair's ordered stream with room reserved,
  bounded by the node's presences, never `Busy`.

### Retire and rebalancing

- **Gateway upgrade or scale-in**: gateways do not hot restart (`Connection` is pinned to its socket). On Kubernetes they roll
  surge then drain (Compose: a plain rolling restart, a client may move twice): every new gateway is ready before an old one leaves the load balancer and sends `ControlOp.Retire`
  in waves (make before break, ADR-026; at most 20 waves of at least 100 connections). The SDK connects through the
  same public address with the handshake flagged "moved by `Retire`", re-sends each slot's join with the same flag (the
  new `Connection` passes it on in its `[JoinOnConnect]` joins), and the larger stamp takes over each presence.
- **The push-order fence** (ADR-026): on takeover the target first appends, for the old gateway, every group frame still
  held for the presence's groups (rate-limited ones included, ahead of their due tick), then removes the old
  connection's subscriptions (`TakeOver` carries them, so the old gateway stops fanning out to it at once), then pushes
  `HandedOff(slot)` on the old route, then switches. The flushed group frames, `TakeOver` and `HandedOff` go to the old
  gateway as one pair (the target actor and the old gateway node, above), so a fragmented group frame is still ahead
  of the small `TakeOver` behind it, and the gateway applies them in
  arrival order, fan-out and `TakeOver` alike, into the old connection's world with no hop through another world, so
  the flushed frames still find their subscriptions before `TakeOver` removes them. So `HandedOff`
  is the last frame the old connection gets for the slot. The SDK holds the new connection's pushes for that slot until the old connection's `HandedOff` arrives, and
  discards any push for the slot that the old connection delivers after it. Nothing is missed, so `Joined.MissedPushes`
  is false; an application can still owe a snapshot of its own after the handover (the mmo sample's `Resnap` for an avatar that crossed cells meanwhile, [draft §12](programming-model.md#12-完整例子连接进地图移动放技能捡东西看见别人)).
- **A fence that never comes**: when the old connection or its gateway dies before `HandedOff` arrives, the SDK stops
  holding after its call timeout and re-joins the slot with a "fence missing" flag (same connection, same attempt).
  An equal stamp is otherwise answered idempotently; with this flag the target sets `Joined.MissedPushes` true (and
  writes `Joined`, so `[Changed<Joined>]` systems see it) and the snapshot system sends the full state; pushes lost on
  the old connection are covered by it.
- The old connection closes once its open calls are answered; its released routes no longer match, and the SDK ignores
  `PresenceLost` from a retiring connection. A connection still there after `RetireGrace` is dropped and handled as a
  lost connection (`Detached`).
- **Rebalancing by load** (ADR-089): every client-facing node puts its connection count in its pulse (ADR-031), leaving
  out connections it is retiring (their replacements already count on the new gateway, and a count that included them
  would keep shedding up to `RetireGrace` × the shed rate past the excess), and
  computes its group's average from its own view over the gateways that are Up and ready (a `NodeJoining` one counts
  only once it is Up and ready: its empty count would make the others shed before it can take connections); only above the average by more than a tolerance (10%,
  `DaServer:Clients:RebalanceTolerance`) does it retire its excess as above, at most 1% of its connections a second,
  and the load balancer picks where they go. A balanced group retires nobody; no coordinator is involved. Paused
  while a gateway of the group is `NodeLeaving` (draining) or `RestartingNodes` is not empty. A maximum connection age
  (`WithClientConnections(port, maxConnectionAge:)` or `DaServer:Clients:MaxConnectionAge`) stays optional, off by
  default, for deployments that want periodic reconnection.
- **Admission**: a gateway admits new connections through a token bucket and answers `Busy(retryAfter)` when it is
  empty; the SDK reconnects with exponential back-off and full jitter. Re-joins after a migration go through it too.

## 4. Placement, activation, persistence

The rules are [draft §6](programming-model.md#6-生命周期) and [§10](programming-model.md#10-持久化迁移热重启升级);
in short:

- **Placement**: an actor type is bound to server types (`[Hosts<…>]`); its keys are placed among the nodes hosting
  it, moved when nodes join or leave (rate-limited per node) or by the coordinator's slot table (ADR-079). Every placement
  change of a type, slot-table moves included, bumps its placement version. A node activates a key only when its own view makes it
  the owner and no tombstone for the key is present; otherwise it forwards. `[ActivatedBy<T>]` narrows only a key's
  first activation (no record yet); `Wake` and the `[JoinOnConnect]` join always activate. `[Stateless]` actors live
  on any node, never persisted. `Connection` is pinned.
- **Moves hand off before the target claims** (ADR-072's "mail first", kept): when a placement change (a slot-table
  move, a node joining or leaving gracefully) moves key `k` from A to B, B treats the keys of every slot it received as
  handoff-pending until A reports the slot done or leaves the view. Mail for a pending key with no local state does not
  claim: B parks it on a placeholder (`Arriving`) and asks A (`HandoffQuery(k)`); A answers with the migration bytes
  (it moves the actor now, mail first, then the subtree), with "not here" (never active, or already written and
  deactivated), after which B claims as usual, or with "postponed" (A cannot write the root before moving, for example
  while the store is down), after which B forwards the mail to A, which keeps the actor running there, and asks again
  after a back-off. `HandoffQuery` carries the slot and B's placement version: A records "slot s left me at vB" for that slot only (it
  does not know the new table's other slots), so from then on A forwards that slot's keys instead of activating them,
  even before its view catches up; A's own claims always use its own view's version. A key never has a claim racing a live source: the store's placement version decides only between
  a source that has left and its successor. The coordinator does not move a slot again while its previous move is
  still handoff-pending (the slot table marks it until the source reports it done or leaves the view), so a chained
  move A→B→C never lets C ask a B that never held the key. Mail A forwards for a parked key goes ahead of the mail B
  parked (A sends it on the migration's stream before the bytes), so per-sender order holds across the move.
- **Membership is a table in the store** (Orleans' membership protocol, [Orleans: cluster
  management](https://learn.microsoft.com/dotnet/orleans/implementation/cluster-management), behind a feature point,
  `IMembershipTable`, with a PostgreSQL implementation and an in-memory one): the store decides who is in the cluster,
  so there is no quorum, ballot or formation size to reason about, and any number of members may fail.
  - **Rows**: `daserver_members` holds one row per node incarnation: cluster, name, node generation, address, server
    type, status (`Joining`, `Active`, `ShuttingDown`, `Dead`), the suspicions against it (suspecting member,
    generation, and the store's `now()` when written, so every node judges a vote's age by one clock), `restart_until`
    (a granted hot restart and its deadline), `dead_seen_at` (the store's `now()` in the coordinator's first write after it read the member `Dead`; below), start time and
    `i_am_alive`;
    `daserver_membership` holds one row per cluster: the table version, the slot table and its per-type placement
    versions. Every change (a join, a status, a suspicion, a death, a restart permit, a slot move) is one transaction
    that updates the changed rows and sets the version row to version + 1 where the version is still the one read
    (compare-and-swap); a conflict re-reads and retries with back-off. So all membership changes are totally ordered
    by the table version, and every node that has read version v computes the same members, coordinator and slot
    table. `i_am_alive` (every `DaServer:Membership:IAmAliveInterval`, 30 s, as the store's `now()`) is written without
    touching the version, and no versioned write ever sets it (the row upsert leaves it out of `DO UPDATE SET`, so a
    stale copy never overwrites a newer heartbeat).
  - **Joining**: a node reads the table, picks its generation as max(Unix ms, the largest generation recorded for its
    name + 1), and in one versioned transaction inserts its row as `Joining` and marks every older generation of its
    name `Dead` (an old process of the same name that is still running reads that and stops). It then checks two-way
    connectivity to every `Active` member (skipping a member whose `i_am_alive` is three intervals older than the
    newest `i_am_alive` in the table, not than the joiner's clock: a crashed node that was never declared dead does not
    block a restart of the whole cluster, and after a store outage, when every heartbeat is old, nobody is skipped) and
    writes itself `Active`; a member that skipped a check casts no suspicion vote until it has probed every member
    once. Until
    then it hosts no slots and serves no clients. If a member does not answer, it does not become `Active` and retries
    (a node cut off from part of the cluster never joins it). The first node of a cluster creates the version row.
    No seeds are needed for membership: the table is where nodes find each other.
  - **Failure detection**: every `Active` member probes up to `DaServer:Membership:ProbedNodes` (10) others, its
    successors on a ring of the active members' hashed identities, over the node links every
    `DaServer:Membership:ProbeInterval` (1 s); after `DaServer:Membership:MissedProbes` (3) missed replies it first
    asks one other member to probe the target (an indirect probe) and, if that also fails, writes a timestamped
    suspicion into the target's row. When a suspicion makes `DaServer:Membership:VotesForDeath` (2, or every other
    `Active` member when fewer remain) suspicions from distinct `Active` members younger than
    `DaServer:Membership:SuspicionExpiry` (3 min), the writer marks the target `Dead` in the same transaction. A writer
    must be `Active` at the version it writes against, so in a partition where both sides reach the store the first
    death written wins and the other side's attempts fail their compare: a side that finds itself `Dead` stops. A two-node
    cluster split in two therefore keeps exactly one node.
  - **Learning the view**: a node reads the table every `DaServer:Membership:TableRefresh` (5 s) and, after any write it
    makes, sends the new snapshot to the other members, who adopt it when its version is newer than theirs. Frames
    carry the sender's table version, so a peer with a newer view answers a stale one with its snapshot. A read is one
    snapshot: the version row and the member rows come from one statement (a `UNION ALL` of both tables), so under
    READ COMMITTED a node never pairs version v with rows of v + 1; the in-memory table hands out the same atomic
    copy.
  - **A node declared dead stops**: once it reads itself `Dead` (or a peer refuses its frames because its generation is
    `Dead`, which peers do once they see it), it freezes every world, fences all its actors and exits with a non-zero
    code; the platform starts a new process, which joins with a new generation. Peers stop sending to a `Dead` member
    at once.
  - **The isolation lease**: a node may run its worlds only within `DaServer:Membership:IsolationTimeout` (20 s) of
    the moment it **issued** its last table read that returned its generation `Active`. The read's snapshot is taken
    after it was issued, so a reply that arrives late, holding a state from before a death, never stretches the lease
    past that state. A snapshot a peer pushes, or sends in answer to a stale frame version, updates the view but never
    the lease: its age is unknown. Lease times come from a clock that keeps counting while the machine is suspended
    (`CLOCK_BOOTTIME` on Linux, `QueryInterruptTime` on Windows, `mach_continuous_time` on macOS; `Stopwatch` uses
    `CLOCK_MONOTONIC`, which stops during suspend), so a process whose machine suspended wakes with its lease expired.
    That clock sees only a suspend the guest performs: a VM the hypervisor stops (QEMU's kvmclock counts only while
    the VM runs) or a CRIU restore inside a time namespace (which offsets `CLOCK_MONOTONIC` and `CLOCK_BOOTTIME` to
    look continuous, `time_namespaces(7)`) hides the gap. So every table read also returns the store's `now()`, taken when
    the statement starts, which lies somewhere between the read's issue and its reply on the lease clock. The node
    keeps, for each read, the bracket [issue, reply]; a stall is counted only when the store's time between two reads
    advanced more than one second beyond the widest gap their brackets allow (the later reply minus the earlier issue),
    so a read that waited to start (a reconnect after a failover, a queue in front of the store) is not a stall, and a
    suspected stall triggers another read at once before anything freezes. A confirmed stall is treated as one
    (below) and stays frozen until a read confirms it `Active`; until that next read (at most a `TableRefresh`)
    stale work remains possible, which epoch fencing stops for persisted writes and peers stop by refusing a `Dead`
    generation's frames. The membership loop publishes the lease deadline as one value; **each shard
    compares it with that clock itself** before every ordered phase and before handing Outbox frames to the links, so a
    shard that wakes from a pause runs nothing past the lease even when the membership loop (on the thread pool) has
    not run yet.
  - **No table, no work**: a node whose lease has run out freezes its worlds: it runs no item and sends nothing, but
    keeps its process, its connections and its snapshot of the worlds. Its shards stop draining their dispatch queues
    (the loop checks the freeze before the drain, above), so mail stays in the queues, bounded by
    `DaServer:DispatchQueueCapacity` (65,536 per shard, ADR-040): beyond it a request is refused `Busy` and a one-way
    message dropped and counted, as for any full queue (draft §9), so a long outage does not grow memory. It keeps reading; when a read shows its generation still `Active`, the
    worlds resume, and when it shows `Dead`, it stops as above. A frozen node also **tells the cluster it is isolated**:
    it answers probes, direct and indirect, with `Isolated`, which the prober counts as a failed probe at once (no
    indirect probe first) when its own lease has been valid without a break for at least `IsolationTimeout` and it is not recovering from a
    cluster-wide freeze (below; so after a store outage, when the nodes' first reads land a few seconds apart, nobody
    votes against a node that simply has not read yet), and its peers vote it `Dead` as soon as they reach the store. While at least one peer answers its
    own probes as not isolated (the peers reach the store and it does not), it is **isolated alone**: its `/health`
    readiness fails, and once it has been frozen for `DaServer:Membership:IsolationExit` (1 min) that way, it exits
    with code 75 and the platform starts it
    again, to join with a new generation once it reaches the store. So a node that reaches its peers but not the store
    (an exhausted pool, a DNS failure, a network policy) gives its slots up instead of holding them frozen forever.
    When every peer it reaches is isolated too, the store is down for everyone: readiness holds (a store outage must
    not take every gateway out of the load balancer, deployment.md section 2) and nobody exits: the cost is stated
    plainly, **a store outage pauses the whole cluster** (no actor runs, clients stay connected and their requests wait
    or answer `Busy`) until the store is back, when every node resumes where it stopped. The pause is reported
    (`daserver.membership.frozen`, the `MembershipFrozen` log) so the store's outage is visible as the cluster's.
  - **Its own connections**: membership reads and writes use a small data source of their own (two connections,
    short statement timeouts), never the actor store's pool, even against the same database, so after a store outage
    a membership read never queues behind the actors' write backlog and no node looks isolated because of it. Its own
    pool does not stop the actor pools from using up the server's `max_connections`, so the roles step reserves room
    on the server: the runtime role gets a `CONNECTION LIMIT` of `max_connections` minus the membership role's share
    (two per node at the configured node count, plus headroom), and the membership data source logs in as its own role
    (`<cluster>_membership`), so the actor pools can never take its connections (on PostgreSQL 16 and later
    `reserved_connections` with `pg_use_reserved_connections` granted to that role is the alternative; the roles step
    prefers it when the server allows).
  - **Recovery after a cluster-wide freeze**: a node resuming from a freeze every peer shared ramps its store traffic
    (record writes and epoch claims start at one statement per world and double with each successful round up to
    their limits; `DueScan` starts after a random delay) and counts no peer's `Isolated` answer as a failed probe
    until one more `IsolationTimeout` has passed and its own write backlog has drained, so a recovering store does not
    turn into a cascade of death declarations.
  - **Stall guard**: a node whose loop finds a gap above `ProbeInterval` on the lease clock since its last pass (a GC
    pause, a stopped VM), or whose table reads show the store's time running ahead of the lease clock (above), keeps
    its worlds frozen until a table read confirms its generation is still `Active`, and
    probe misses measured across the gap are ignored, so a stall neither runs stale work nor votes a peer dead. The
    shards' own lease check is what safety rests on; the guard only shortens the stale work a pause shorter than the
    lease could still do.
  - **A dead member keeps its slots until it is re-placed**: a member written `Dead` stays in every node's placement
    set (rendezvous ranking and slot-table overrides alike) until the coordinator's re-placement write removes it, so
    no node activates its keys before that write, whatever the order in which nodes learn of the death. Peers stop
    sending to a `Dead` generation, so mail for its keys is answered by the sending node with
    `Unavailable(retryAfter)`, `retryAfter` the time left to the re-placement deadline as the view tells it (about
    `IsolationTimeout + DeathMargin`, 25 s, at most): asks retry by their own rules (`AskUntilAnswered` asks again by
    itself), client calls get `Unavailable`, one-way mail waits in the sender's window. A new generation of the same name
    hosts none of the old generation's slots before that write either.
  - **Re-placement**: the deadline counts from `dead_seen_at`, not from the death's own write. A death is a transaction
    of several statements whose `now()` is its start, and a writer that stalls before `COMMIT` (a GC pause, a slow
    synchronous commit) would commit long after that time, while the dead node, still reading itself `Active`, renews
    its lease until the commit. So the coordinator, on the first table read that shows a member `Dead` with no
    `dead_seen_at`, stamps it with the store's `now()` in a versioned write of its own: that transaction began after a
    read that saw the death committed, so `dead_seen_at` is later than the commit, and the dead node's last lease began
    before the commit. Once the store's `now()` passes `dead_seen_at + IsolationTimeout + DaServer:Membership:DeathMargin`
    (5 s; it covers the drift between the store's clock rate and the node's lease clock), the coordinator removes the
    member from the placement set in one versioned write, the placement-version bump of every type it hosted: by then
    the dead node has either read its death and stopped, or seen its lease run out and frozen. The coordinator reads
    both times from the store, so its own clock and the moment it learned of the death do not matter.
  - **Asymmetric reachability** (C cannot reach B, A can): A's indirect probe clears B, so B is not declared dead and
    C's mail to B waits and times out. Orleans has the same gap; DaServer reports it rather than guessing
    (`daserver.link.unreachable{from,to}`, and the Dashboard lists the pair).
  - **The coordinator** is the `Active` member with no `restart_until` (whatever its value) with the oldest start
    time (then the smallest name) at the table version a node has read. The rule reads only the table, never a clock,
    so every node with that version agrees; a hot-restarting node is never coordinator, so the next one by this rule
    stamps `dead_seen_at`, re-places dead members and writes the restarting node `Dead` if its deadline passes, and a stale coordinator cannot act, since everything it
    decides (slot moves, a restart permit, a hot restart's deadline) is a versioned write whose compare fails. It plans
    slot moves (ADR-079) and writes the slot table; the placement version of a type is the table version of the last
    change to its hosting nodes or its slots, so every node derives the same versions. A hot restart's permit and
    deadline are the restarting node's `restart_until`: granted by a versioned write when no other row holds one and no `Dead`
    member still awaits re-placement (a hot restart never overlaps a re-placement), and a member whose
    `restart_until` has passed is written `Dead` by the coordinator. A hot restart keeps the node's row
    and generation (draft §10).
  - **Without a store**: the in-memory table serves a single node (a local run, a starter without a database, an
    `AllInOne` deployment) and `TestCluster`, whose in-process nodes share one instance (wrapped per node in a
    fault-injecting `IMembershipTable` that tests use to cut one node off the store, delay its reads or stall it). A
    node without a membership store starts only when `DaServer:Membership:SingleNode = true` says it is alone; any
    other configuration refuses to start, so two storeless replicas never form two clusters running every actor twice; `SingleNode = true` on a node that also has a membership store (or an actor store) refuses to start as well, since a second replica configured the same way would bypass the store's table. A single node's hot-restart handover carries its member row (name, generation, start time, `restart_until`) in the stream's header, and the new process seeds its in-memory table from it before `Rejoining`, so the check "still `Active`, own generation, own `restart_until`" passes as on a cluster.
    `DaServer.Aspire` sets `SingleNode` when the AppHost has one node and no store, and adds a PostgreSQL resource for
    membership when it declares more than one node and no store. A cluster of more than one process needs the table in
    a store: the actor store's PostgreSQL by default, or a separate connection (`WithMembershipStore(db)`). Every node,
    gateways included, needs that connection, as the membership role (section 4's roles step).
  The timer defaults are one table in [draft §11](programming-model.md#定时的默认值).
- **Activation**: the placeholder (`Loading`) holds the mail; one statement claims the epoch and reads the record
  (`INSERT … ON CONFLICT (type_id, key) DO UPDATE SET … WHERE … RETURNING …`, inserting the placeholder row for a new key, its
  every epoch (a new key, a takeover, the no-insert claim) taken from a sequence so a key whose row was swept never
  reuses an epoch, an empty result under READ COMMITTED retried, and
  returning the displaced owner as PostgreSQL 18's `old.owner`; the store keeps epoch, write generation (reset to 0 by the claim), owner and placement version: taking a key from another owner needs a
  strictly newer version, a displaced owner gets `Fence(key, epoch)`), within `ActivationSlots`;
  `LoadEntitiesInto(record, into, markWritten: false)` with the placeholder as the root; `In<K>` added;
  `Activated` first, then the placeholder's mail, then later mail. When the claim does not take the key, the same
  statement returns the stored placement and owner and whether a record exists, so "a newer owner holds it" (forward)
  and "no record" (`NoActor`) are told apart.
- **Who may create a key**: a type without `[ActivatedBy]` lets every server message create a key and no client
  message; a type with `[ActivatedBy]` lets only the listed messages create one, server messages included (the list is
  the type's whole set of creating messages), with `Wake` and the `[JoinOnConnect]` join always activating (the join's key is computed from the claims, never chosen by the client, so neither `[ActivatedBy]` nor `CanCreate` applies to it and a first login creates the `Player` row). When the mail that triggers the activation may not create the key (a client `[To]`
  message to a type without `[ActivatedBy]` for it, or any message the type's
  `[ActivatedBy]` list leaves out), the claim runs without its `INSERT` branch and requires a written record
  (`record IS NOT NULL`): no row, or a row never written, answers `NoActor` and writes nothing. A message that may
  create the key first passes the type's pure gate, `static bool CanCreate(in TClaims, in TMessage)` (a server message has no claims: `CanCreate(in TMessage)`; another shape is an analyzer error) beside
  `[ActivatedBy]` (no world, no allocation; besides the claims and the message it may take at most one `in`
  configuration table, the node's immutable snapshot, an analyzer error otherwise; checked on the owner before the claim, for example a map id against the map
  table): for a saved type `false` turns the claim into the no-insert one above, so a key that already has a record
  still activates (a map taken out of the map table keeps its record and still runs), and only a key without a record
  answers `NoActor`, writing nothing; a type that saves nothing has no record to find, so `false` is `NoActor`. So a client cannot create rows by inventing keys.
- **Actor types that save nothing claim no row**: a type whose root is not persisted has no record, so its activation
  writes nothing to the store, no claim included. What keeps one activation per key for it is the view: a node
  activates only keys its view makes it the owner of and not handoff-pending, peers forward by the same rule, a
  node written `Dead` in the membership table stops when it reads it, a node that cannot read the table freezes after
  `IsolationTimeout`, and its slots move only after `dead_seen_at + IsolationTimeout + DeathMargin` on the store's clock. Between two views that briefly disagree (a slot-table change still
  spreading) two activations of such a key can exist for a moment; neither writes anything, so the cost is mail split
  between them, and the forward-by-version rule (frames carry the highest placement version seen) ends it within one
  view exchange. `[ActivatedBy]` and `CanCreate` apply to them as well: they decide whether an activation happens.
- **Rows never written expire**: a row whose record is still `NULL` `DaServer:EmptyRecordTtl` (default 1 day) after
  its last claim is deleted. Any live activation of a persisted type writes its first record within a checkpoint
  interval (a new key is tagged `Unsaved`), so such a row belongs to an activation that was refused or crashed before
  writing. The oldest `Active` member that hosts a persisted type (it has the actor store)
  runs the sweep in its first world every `DaServer:EmptyRecordSweepInterval` (1 h), in key-set batches of 1,000 rows
  that walk the primary key once per sweep (no index fits `record IS NULL`, and a partial index on it would make every
  record write non-HOT), through `daserver_sweep_empty(ttl, limit, after)`, a `SECURITY DEFINER` function owned by the
  schema's owner role that deletes only `record IS NULL AND claimed < now() - ttl` rows; the runtime role gets
  `EXECUTE` on it and no `DELETE` on the table. If an activation somehow outlived the TTL without writing, its first
  write finds no row, which is a failed compare: it fences, and the next mail activates the key again.
- **The table**: one row per actor, keyed by `(type_id, key)` (`Map(7)` and `Loot(7)` are two rows), with the columns
  `type_id, key, slot, epoch, write_gen, owner, placement, record, next_due` and an index on `(type_id, slot, next_due)`. The
  key is `bytea`, the key's fields big-endian (any key shape up to 16 bytes in one column); the slot is the key's
  placement slot, computed by the node ([draft §10](programming-model.md#激活-epoch一个-actor-只有一个激活能写), ADR-068).
- **Epoch fencing**: every record write compares the epoch; a failed compare fences the activation. A migration's
  arrival claims a new epoch too (writing `GREATEST` of the stored and the type's current placement version), so a late write from the source fails. A root's writes never overlap in one process (`Writing`,
  section 2), and the CAS also compares the write generation, so a late timed-out statement never overwrites newer state;
  a failed or timed-out write is retried with the same bytes and generation;
  `Unsaved` is cleared when the record is taken at the write's start and put back if the write fails, and changes
  meanwhile tag the root again and join the next write.
- **Writes** are asynchronous and batched: one statement per 512 rows or `DaServer:Store:StatementBytes` per world per
  tick end, rows sorted, at most `DaServer:Store:WritesInFlight` in flight, a root that called `Commit` in the next one,
  other changed roots at their staggered checkpoint instant (every `DaServer:CheckpointInterval`, the first at a
  fraction of it set by the key's hash, oldest unsaved first, a quarter of each round kept for them while commits
  wait). Transient errors retry the same bytes (a timed-out statement cancelled first); a deterministic error bisects
  the statement to the one bad row (`RecordUnwritable`). A world whose write backlog passes
  `DaServer:Store:BacklogPause` stops draining its dispatch queue until it shrinks. A crash loses the larger of one
  interval and the time the backlog takes to drain (`daserver.store.oldest_unsaved_age`); a graceful stop writes
  within its stop budget and logs what it could not; a root whose state does not serialize loses everything since
  its last good record (below); actors keep running, only what a committing item sends waits for its write (ADR-097)
  ([draft §10](programming-model.md#默认异步成批地写), section 2's tick-end write).
- **Persistence** is per entity kind: entities spawned from a persistent prefab (the root's `K.RootPrefab` with
  `[Persist]`, child prefabs with `[Prefab<K>, Persist]`) carry `Persisted` and are saved with every component
  but `[NotSaved]` ones; others are not. Records are `SaveEntitiesWith` of the root and its `Persisted` descendants
  (`world.Descendants(root).AddTo(ids, includeRoot: true)`, then the non-persistent ones removed in place, saved as
  `SaveEntitiesWith(output, CollectionsMarshal.AsSpan(ids), in persist)`) with the `persist` component set built once in `Registration`, components only (a group save refuses a set naming a singleton): the types on persistent
  prefabs and those spawned into persistent entities, plus the framework's (ask states are not recorded); it is used at run
  time only. Change tracking comes from the source: every persisted user component type declares `ITracked`
  (buffers are tracked already), the generator emits the tracked-type list and the `ChangedAny`/`RemovedAny` filters
  from those declarations (EasyEcs 4.7's filters name their types at compile time; each DaServer package's generator emits its share (its tracked types, prefabs and systems) as `internal static Registration <package namespace>.Generated.PackageHooks.Register(Registration)`, always, an empty one when it has nothing to register, and the server project's generator calls the hook of every referenced DaServer package in `Registration.Create()`'s chain; buffers enter the server project's list only when their element type is declared there), and `Registration` throws at start
  when a type on a `[Persist]` prefab is missing from that list. `root.Spawn` of a persistent prefab, `ChangeSystem`
  (`ChangedAny<…>(since)` over the tracked types that reach records, `[NotSaved]` types and buffers of `[NotSaved]` elements left out, split into balanced calls of 2 to 9 types joining one group, a single type as `ChangedAny<T, T>`/`RemovedAny<T, T>` (never `Changed<T>`, which would AND with the group), `.With<Persisted>()`, and `RemovedAny`) and the `Persisted` cleanup tag the root `Unsaved`;
  `CheckpointSystem` puts `WriteNow` roots and, each interval, the `[With<Unsaved>]` roots into the tick-end write, each row with an epoch CAS; both skip roots that are placeholders
  (`Loading`, `Saving`, `ActivationFailed`). Every load completes entities from their prefab copies by `FromPrefab`
  (`[NotSaved]` components and those the prefab gained since; section 2) and skips unknown type ids, counted. `Unacked`, `OnceSenders`, `RootState`'s `OnceNext`, `IssueSeq`, `NextDue` and the `Asking` children with their `WakeAt` are in the record (waiting entities are
  not), and a root that called `Commit` is written at the next tick end. **Wake**: the
  runtime's `WakeSystem` keeps the root's `NextDue` at the minimum `WakeAt` of the root and its descendants and tags the root `Unsaved` when it changes; each node's `DueScan`
  asks the store for keys of its slots whose `next_due` has passed (`IActorStore.DueKeys(slots, until, after)` over the node's `(type, slot)` pairs, the
  `(type_id, slot, next_due)` index, in keyset pages of 512 from a sentinel first row, `until` fixed per scan, at most a page per pair through `LATERAL … LIMIT`) and activates them, so an `AskUntilAnswered` or a reminder survives a crash.
- **Deactivation**: idle past `IdleTimeout` (`[ActorOptions]`; `Pending` zero, no presence other than `Detached` ones;
  not while `StoreDegraded`) or `root.Deactivate()` (while `StoreDegraded`, beyond the cap on `Saving` placeholders it
  is postponed): presences leave, the root carries
  `Deactivating` for the last pass of systems, the subtree is saved and destroyed, a `Saving` placeholder holds new
  mail until the write lands, and output held by `Commit` leaves then.
- **Unwritable root off the checkpoint path**: deactivation, stop and migration run `SaveEntitiesWith` before the destroy.
  If it throws, an idle deactivation is cancelled and the actor stays active (tagged `Unsaved` again); a forced
  deactivation (`root.Deactivate()` and the other uncancellable ones) or a graceful stop gives the write up, keeps the
  last stored record, logs `RecordUnwritable` and destroys as usual; a migration falls back to such a forced
  deactivation (placement already names the target, which activates from the last record); an in-place hot restart's
  handover leaves the root out (its subtree destroyed in the frozen world as by fencing, `RecordUnwritable`), so it
  re-activates from its last record in the new process and the restart goes on. The write that releases a root's held
  output before a migration or a freeze takes the same fallback at once, its held output dropped rather
  than waiting for a write that never succeeds ([draft §10](programming-model.md#只写改过的)).

### Store statements

The PostgreSQL statements behind the rules above ([draft §10](programming-model.md#激活-epoch一个-actor-只有一个激活能写)
and its wake section); the store implements `IActorStore` with them.

**The schema** (created by the schema Job as the owner role, in the schema `:schema`, default `public`; `:runtime_role` is
the `Username` of the runtime connection string, so the role names are the user's). DDL takes no bind parameters, so
`:schema` and `:runtime_role` are formatted into the text as quoted identifiers (`quote_ident`, `format('%I')`). Every
object is qualified with `:schema`, in the DDL and in the runtime statements alike (the store prefixes the names it
formats into its statements), so neither role depends on its `search_path`; the sketches below leave the prefix out
for brevity. The owner role needs `CREATE` on `:schema` (in PostgreSQL 15 and later, `public` grants it to nobody but
the database owner). Every statement is re-runnable, since the Job runs on every release: `IF NOT EXISTS` and
`CREATE OR REPLACE` throughout, and the index is created only when `to_regclass` does not find it, so a release takes
no index lock. There are no composite types: Npgsql cannot map one under Native AOT, so the owner is two columns.

```sql
CREATE SEQUENCE IF NOT EXISTS :schema.actor_records_epoch;                 -- every epoch: never reused after a sweep
CREATE TABLE IF NOT EXISTS :schema.actor_records (
  type_id    integer     NOT NULL,   -- the actor type's id (Nino's type id)
  key        bytea       NOT NULL,   -- the key's fields in order, each big-endian at its own width (at most 16 bytes)
  slot       smallint    NOT NULL,   -- the key's placement slot (0..4095, ADR-074), computed by the node
  epoch      bigint      NOT NULL,
  write_gen  bigint      NOT NULL,
  owner_name text        NOT NULL,   -- the node name of the last claim
  owner_gen  bigint      NOT NULL,   -- and its node generation
  placement  bigint      NOT NULL,   -- the placement version of the last claim
  claimed    timestamptz NOT NULL,   -- the last claim's time: a never-written row expires from it
  record     bytea,                  -- NULL: claimed, never written
  next_due   timestamptz,            -- NextDue; NULL: nothing due
  PRIMARY KEY (type_id, key));
-- when to_regclass(':schema.actor_records_due') IS NULL:
CREATE INDEX IF NOT EXISTS actor_records_due ON :schema.actor_records (type_id, slot, next_due) WHERE next_due IS NOT NULL;
CREATE TABLE IF NOT EXISTS :schema.daserver_membership (    -- one row per cluster: the version row
  cluster    text   PRIMARY KEY,
  version    bigint NOT NULL,        -- every membership change sets it to version + 1 where it is still the one read
  slots      bytea  NOT NULL,        -- the slot table (ADR-079)
  placements bytea  NOT NULL,        -- per actor type: the table version of its last hosting or slot change
  configs    bytea);                 -- configuration tables: (name, version, hash)
CREATE TABLE IF NOT EXISTS :schema.daserver_members (       -- one row per node incarnation
  cluster       text        NOT NULL,
  name          text        NOT NULL,
  generation    bigint      NOT NULL,
  address       text        NOT NULL,
  server_type   text        NOT NULL,
  status        smallint    NOT NULL,   -- Joining, Active, ShuttingDown, Dead
  suspicions    bytea,                  -- (suspecting name, generation, store now()) per suspicion
  restart_until timestamptz,            -- a granted hot restart's deadline; NULL: none
  dead_seen_at  timestamptz,            -- store now() of the coordinator's first write after reading it Dead
  started       timestamptz NOT NULL,
  i_am_alive    timestamptz NOT NULL,   -- diagnostics and the join's skip rule; written without a version change
  PRIMARY KEY (cluster, name, generation));
GRANT USAGE ON SCHEMA :schema TO :runtime_role;
GRANT SELECT, INSERT, UPDATE ON :schema.actor_records TO :runtime_role;  -- no DELETE: only the sweep function deletes
GRANT USAGE ON SEQUENCE :schema.actor_records_epoch TO :runtime_role;  -- nextval in both claims
GRANT SELECT, INSERT, UPDATE ON :schema.daserver_membership, :schema.daserver_members TO :runtime_role;
```

The schema Job does not create roles. The samples' AppHosts add an init step that runs with the database's superuser
(Aspire's PostgreSQL resource has only `postgres`) before the schema Job (which retries until the roles exist) and
creates the roles, none a superuser: the owner role (`CREATE` on the schema, owns the tables, the sequence and the
two `SECURITY DEFINER` functions), the runtime role and the membership role (`<cluster>_membership`: the membership
tables only, used by the nodes' small membership data source, section 2). The runtime role gets a `CONNECTION LIMIT` of
the server's `max_connections` minus the membership role's share and the superuser's reserve, set on every run, so the
actor pools can never take the connections membership reads need (on PostgreSQL 16 and later the step grants
`pg_use_reserved_connections` to the membership role instead when `reserved_connections` is configured). It is re-runnable, since on Kubernetes it runs as a Job with
a fresh name every release, and every run converges the database to the configuration: (1) each role is created if
missing and its password set on every run, so a changed password parameter reaches the database (`DO $$ BEGIN IF NOT
EXISTS (SELECT FROM pg_roles WHERE rolname = '<owner>') THEN CREATE ROLE … LOGIN; END IF; END $$`, then `ALTER ROLE
… PASSWORD …`; a `DO` body takes no bind parameters and psql does not substitute `:'var'` inside dollar quotes, so
the step builds each statement with `format('… %I … PASSWORD %L', name, password)` and runs it with `EXECUTE`); (2)
`CREATE SCHEMA IF NOT EXISTS <schema> AUTHORIZATION <owner>`, so a configured schema other than `public` exists and
the owner role owns it (its `GRANT USAGE ON SCHEMA … TO <runtime>` then takes effect; PostgreSQL ignores a grant from
a role without the privilege's grant option), and for `public`, `GRANT CREATE, USAGE ON SCHEMA public` to the owner
and `USAGE` to the runtime role; (3) `ALTER TABLE`/`SEQUENCE`/`FUNCTION … OWNER TO <owner>` for every DaServer
object that already exists under another owner (tables created by today's nodes as `postgres`), so the owner's
grants and later `ALTER`s on them work; (4) with `AddDaServerDashboard`, a third login role for the Dashboard
(`<cluster>_dashboard`, created like the others) and its tables, owned by the owner role:
`daserver_audit (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, action_id text NOT NULL, phase text NOT NULL
CHECK (phase IN ('intent','outcome')), at timestamptz NOT NULL DEFAULT now(), who text NOT NULL, action text NOT NULL,
target text NOT NULL, reason text, outcome text)` with indexes on `(at)` and `(action_id)`, on which the Dashboard's
role has `INSERT, SELECT` and `USAGE` on the identity sequence but no `UPDATE` or `DELETE`, so the audit is
insert-only; `daserver_actions (action_id text PRIMARY KEY, idem_key text NOT NULL, type text NOT NULL, state text NOT NULL
CHECK (state IN ('requested','accepted','running','done','failed','stalled','unknown')), requested_at timestamptz NOT
NULL DEFAULT now(), claimed_by text, lease_until timestamptz, run_id text)` (`type` is the server type, or the sentinel
`'*cluster'` for an action on the whole cluster, since a unique index never conflicts on NULL), with a unique partial
index on `(type) WHERE state IN ('requested','accepted','running','unknown','stalled')` (one unfinished action per
type: the lock; Done and Failed are the only terminal states) and an index on `(idem_key,
requested_at)`, on which the Dashboard's role has `SELECT, INSERT, UPDATE, DELETE` (it expires finished rows older
than a day); and `daserver_dataprotection` (the Data Protection key ring), which its role may update. Silences are
written to Alertmanager with the Dashboard's own token (deployment.md, the `{cluster}-alertmanager` Secret). The runtime role has no grant on
any of the three, and the Dashboard's role none on the actor and membership tables. The Dashboard deletes no audit
row: a `SECURITY DEFINER` function `daserver_audit_expire(retention interval)`, owned by the owner role and executable
by the Dashboard's role, removes expired rows when the Dashboard calls it daily with
`DaServer:Dashboard:AuditRetention` (unset: kept forever). Against an external database the operator provisions
the roles (deployment.md section 4).

**A membership change** (one transaction; `:v` is the version the node read, and the node must have been `Active` at
it unless it is joining):

```sql
BEGIN;
UPDATE daserver_membership SET version = :v + 1, slots = coalesce(:slots, slots), placements = coalesce(:placements, placements)
 WHERE cluster = :cluster AND version = :v;                 -- 0 rows: a conflict, ROLLBACK, re-read the table, retry
INSERT INTO daserver_members ... ON CONFLICT (cluster, name, generation) DO UPDATE SET ...;   -- the changed rows;
       -- SET lists status, suspicions, restart_until, address and
       --   dead_seen_at = CASE WHEN excluded.dead_seen_at IS NOT NULL      -- the coordinator asks for the stamp
       --                  THEN coalesce(daserver_members.dead_seen_at, now())  -- the store's clock, set once
       --                  ELSE daserver_members.dead_seen_at END;
       -- never i_am_alive, so a stale copy cannot overwrite a heartbeat written since
COMMIT;
```

The rows of members written `Dead` stay for `DaServer:Membership:DefunctRowAge` (7 days, for diagnostics) and are
deleted by `daserver_members_cleanup(cluster, age)`, a `SECURITY DEFINER` function the schema Job creates like the
sweep below, with the same `SET search_path = pg_catalog, pg_temp`, schema-qualified objects and grants
(`REVOKE ALL ON FUNCTION :schema.daserver_members_cleanup(text, interval) FROM PUBLIC; GRANT EXECUTE … TO
:runtime_role`: PostgreSQL grants `EXECUTE` to `PUBLIC` by default); the runtime role has no `DELETE`. The coordinator
calls it once a day.

**The claim** (a new key inserts the placeholder row; when the condition fails nothing changes and the stored row
comes back, `taken` false, so the node tells "forward to the newer owner" from "no record"):

```sql
WITH c AS (
  INSERT INTO actor_records AS a (type_id, key, slot, epoch, write_gen, owner_name, owner_gen, placement, claimed, record, next_due)
  VALUES (:type_id, :key, :slot, nextval('actor_records_epoch'), 0, :name, :gen, :v, now(), NULL, NULL)  -- a new key
  ON CONFLICT (type_id, key) DO UPDATE
     SET epoch = excluded.epoch, write_gen = 0, owner_name = excluded.owner_name, owner_gen = excluded.owner_gen,
         placement = excluded.placement, claimed = now()
     WHERE a.placement < excluded.placement
        OR (a.placement = excluded.placement AND a.owner_name = excluded.owner_name AND a.owner_gen = excluded.owner_gen)
  RETURNING new.epoch, new.record, old.owner_name, old.owner_gen, new.placement)  -- PostgreSQL 18: OLD, NULL if inserted
SELECT true AS taken, epoch, record, owner_name, owner_gen, placement FROM c
UNION ALL
SELECT false, a.epoch, NULL, a.owner_name, a.owner_gen, a.placement  -- not taken: the row as stored before the statement
FROM actor_records a
WHERE a.type_id = :type_id AND a.key = :key AND NOT EXISTS (SELECT 1 FROM c)
```

Every epoch comes from the sequence (`excluded.epoch` is the `nextval` of `VALUES`, which PostgreSQL evaluates on a
conflict too), so epochs rise across the whole store and a row created again after a sweep never repeats an epoch an
old activation held. Under READ COMMITTED the fallback `SELECT` uses the statement's snapshot, while the `ON CONFLICT`
condition is checked on the latest committed row, so two results are races, not answers, and the node runs the
statement again (a new statement sees the committed row): an empty result (two nodes claimed the same new key at once,
and the second one's snapshot predates the first one's row), and a not-taken row that would have met the condition
(a placement older than `:v`, or `:v` with this node as owner: the row the condition saw had changed since the
snapshot). Only a not-taken row that fails the condition means "forward".

**A claim that times out** may still commit. The node cancels it first (Npgsql's cancellation sends PostgreSQL's
cancel request and waits for the command to end) and only then runs it again; if the first attempt had committed, the
retry is taken with the node itself as the old owner, so the node remembers the owner it read before the first
attempt (the row's owner, or the previous placement's host) and treats that one as the displaced owner it notifies.

**The claim that may not create the key** (no `INSERT` branch; `has_record` false answers `NoActor`):

```sql
WITH c AS (
  UPDATE actor_records a
     SET epoch = nextval('actor_records_epoch'), write_gen = 0, owner_name = :name, owner_gen = :gen, placement = :v,
         claimed = now()
   WHERE a.type_id = :type_id AND a.key = :key AND a.record IS NOT NULL
     AND (a.placement < :v OR (a.placement = :v AND a.owner_name = :name AND a.owner_gen = :gen))
  RETURNING a.epoch, a.record, a.placement)
SELECT true AS taken, epoch, record, NULL::text AS owner_name, NULL::bigint AS owner_gen, placement, true AS has_record FROM c
UNION ALL
SELECT false, a.epoch, NULL, a.owner_name, a.owner_gen, a.placement, a.record IS NOT NULL
FROM actor_records a
WHERE a.type_id = :type_id AND a.key = :key AND NOT EXISTS (SELECT 1 FROM c)
```

(The displaced owner is read the same way as above when PostgreSQL 18's `old` is used; the sketch leaves it out for
brevity.) The same races are run again: a not-taken row with a record that would have met the condition, and a result
that says no record (no row, or `has_record` false) while the row may have been written meanwhile: the node runs the
statement once more, and answers `NoActor` only when the second run agrees.

**The sweep of never-written rows** (created by the schema Job; the runtime role has `EXECUTE` only):

```sql
CREATE OR REPLACE FUNCTION :schema.daserver_sweep_empty(ttl interval, max_rows integer, after_type integer, after_key bytea)
RETURNS TABLE (deleted integer, last_type integer, last_key bytea)       -- the cursor for the next batch
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
  WITH page AS (SELECT a.type_id, a.key FROM :schema.actor_records a      -- key-set walk: the table once per sweep
                WHERE (a.type_id, a.key) > (after_type, after_key)     -- first batch: the sentinel
                                                                         -- (-2147483648, '\x'), never NULL
                ORDER BY a.type_id, a.key LIMIT max_rows),
       d AS (DELETE FROM :schema.actor_records a USING page p
             WHERE a.type_id = p.type_id AND a.key = p.key
               AND a.record IS NULL AND a.claimed < now() - ttl         -- rechecked on the row being deleted
             RETURNING 1)
  SELECT (SELECT count(*)::integer FROM d), max(p.type_id), (SELECT p2.key FROM page p2
          ORDER BY p2.type_id DESC, p2.key DESC LIMIT 1) FROM page p $$;
REVOKE ALL ON FUNCTION :schema.daserver_sweep_empty(interval, integer, integer, bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION :schema.daserver_sweep_empty(interval, integer, integer, bytea) TO :runtime_role;
```

**The batched write** (compare and swap on epoch and write generation). The rows not updated come back as the
statement's snapshot stored them, and the node reads each: a different epoch, or a NULL one (the row is gone, for
example swept), is a failed compare and fences the activation; the same epoch with a `write_gen` at or above the
written one is a write that already landed (a retried batch); the same epoch with a lower `write_gen` is a race (a
concurrent claim changed the row after the snapshot, so the `UPDATE` saw the new epoch while the `SELECT` shows the
old one), and that row is written again in the next batch, where it fails or lands:

The node fills the arrays sorted by `(type_id, key)`, so two statements touching the same rows (a stale owner's and
the new owner's) take their row locks in one order and never deadlock, and caps a statement at 512 rows and
`DaServer:Store:StatementBytes`. **A write that times out** is cancelled before its retry, as a claim is (the same
bytes and write generation, so a statement that landed meanwhile reads back as written). Errors are classified:
transient ones (a timeout, a lost connection, `40001`, `40P01`, `53xxx`, `57P0x`) retry the statement; any other error
will recur, so the node splits the statement in half and writes each half, down to the single row that fails, which
is logged `RecordUnwritable` and keeps its last record while the others land.

```sql
WITH r AS (SELECT * FROM unnest(:type_ids, :keys, :records, :next_dues, :epochs, :write_gens)
               AS r(type_id, key, record, next_due, epoch, write_gen)),
     w AS (UPDATE actor_records a SET record = r.record, next_due = r.next_due, write_gen = r.write_gen
           FROM r WHERE a.type_id = r.type_id AND a.key = r.key
                    AND a.epoch = r.epoch AND a.write_gen < r.write_gen
           RETURNING a.type_id, a.key)
SELECT r.type_id, r.key, a.epoch, a.write_gen                 -- the rows not updated, as stored; a missing row
FROM r LEFT JOIN actor_records a ON a.type_id = r.type_id AND a.key = r.key   -- comes back with a NULL epoch:
WHERE NOT EXISTS (SELECT 1 FROM w WHERE w.type_id = r.type_id AND w.key = r.key)   -- a failed compare, it fences
```

**`DueKeys(slots, until, after)`: one page**:

```sql
SELECT s.type_id, s.slot, a.next_due, a.key
FROM (SELECT u.type_id, u.slot FROM unnest(:type_ids, :slots) AS u(type_id, slot)
      WHERE (u.type_id, u.slot) >= (:type_id, :slot)                               -- pairs at or after the cursor's pair
      ORDER BY u.type_id, u.slot) AS s
CROSS JOIN LATERAL (
  SELECT r.next_due, r.key FROM actor_records r
  WHERE r.type_id = s.type_id AND r.slot = s.slot
    AND r.next_due <= :until                                                       -- fixed once per scan
    AND (r.type_id, r.slot, r.next_due, r.key) > (:type_id, :slot, :next_due, :key) -- after: the previous page's last row,
  ORDER BY r.next_due, r.key                                                       -- first page (-2147483648, -1, '-infinity', '\x')
  LIMIT 512) AS a                                                                  -- at most a page from one pair
ORDER BY s.type_id, s.slot, a.next_due, a.key                                      -- pairs already in order: sorted per pair
LIMIT 512
```

## 5. Migration, upgrades, hot restart

- **Migration**: the whole subtree (`Descendants(root).AddTo`, as records) with the component set `migrate` (every component not `[NotSaved]`), including
  waiting asks, presences with `Subscribed`, and delayed events with their remaining time (their `Seq` re-stamped on
  arrival in their old order, the due heap rebuilt). The header carries each component's layout hash and each prefab's composition hash from
  `wire.manifest`; one layout mismatch (mixed versions) falls back to deactivate, write the record, load it on the
  target, while a changed composition only completes entities on arrival (`FromPrefab`, section 2). The source first
  writes the root and waits for its output held by `Commit` to leave (the store not answering postpones the move), so no
  held output travels; once that final write is issued, new mail for the root parks on it unrun (as on a `Saving`
  placeholder) and moves with the bytes, so a root that commits every tick still converges; the rebalancer picks no
  actor whose mailbox is hot (more than half of `MaxMail` in the last second), since its `Arriving` placeholder would
  overflow; a root whose `SaveEntitiesWith` throws drops its held output at once and falls back to a forced
  deactivation (section 4).
  Arrivals load with `markWritten: true`, so `[Changed<T>]` systems treat them as written (re-subscribe, re-cell,
  first checkpoint). A tombstone forwards all mail for the key. Across shards it is bytes too (`MoveEntities` needs
  one thread).
- **Time across processes** is remaining time only; each process runs on its own monotonic clock.
- **`wire.manifest`**: one section per project; `Game.Server`'s holds actor types, hosting server types, tick rates,
  claims, message endpoints, events, state types, configuration tables with their format hash, prefabs with their id, composition hash
  (earlier compositions kept) and whether they persist, the record's component types, layout hashes, pushes, client routes, event
  and `Replied` handlers, `Activated` handlers and tag consumers. The AppHost reads `actor` and `hosted-by`; nodes
  check their server type against it at start.
- **One manifest diff** (`ManifestDiff`, beside the manifest model: one source compiled into `DaServer.Core` and linked into the generator, which cannot load packages): compares two manifests
  and classifies every change (in place, through records, rolling, refused, unsafe to roll back). Four users, one
  implementation: the node's arm (`/admin/restart`, and `?dryRun=true` answers the path without arming), the upgrade
  check and the Compose deploy step (both call a running node's dry-run arm), the route check (the committed
  manifests of the AppHost's projects, read at build time: no node process is started) and the AppHost generator's build
  check (`actor` and `hosted-by` lines).
- **Rolling upgrades**: `[Hosts]` on the member card, `NotHandled` from old nodes retried once after placement is
  resolved again (an unknown client message at an old gateway is reported by the SDK as a version mismatch),
  migration falls back to records across layouts, load moves resume once the stateful groups have rolled, and the
  upgrade check refuses a release whose record fields or record types an older build could not keep (rollback safety). Changes of hosting
  or client routes change this way. **Gateways** always roll; on Kubernetes surge then drain: every new gateway is ready before an old one leaves the load
  balancer and retires its connections in waves (`ControlOp.Retire`, make before break, ADR-026), so each client
  moves once ([deployment.md section 2](deployment.md#stateless-groups-and-gateways-deployment)).
- **Hot restart** (ADR-084, ADR-098; mechanics only in [draft §10](programming-model.md#热重启)): one path on JIT and
  AOT and on every platform, a **warm standby handover**: the new build starts beside the running node, the node
  streams its worlds to it over a local pipe together with its listening sockets, and parks in place. There is no
  snapshot file and no in-process code loading.
  - **A node is a pair of instances** sharing the node name and a small local directory (`DaServer:HandoverPath`: a
    lock, the pipe and the arm file, a few KB); each knows its own name from `DaServer:Instance`. The **role is decided
    at run time, never by configuration**: the process holding an exclusive lock on `<dir>/node.lock` (`flock` on
    Unix, an exclusive file handle on Windows; the kernel releases it however the process ends) is the node. An
    instance without the lock reads the arm file: while the node is armed with a manifest hash equal to its own, it is
    the **standby**; otherwise it **parks** (it releases everything it opened, keeps only the process, a few tens of
    MB, and waits for the lock; it still listens on the pipe, refusing every handshake and accepting only "exit", which
    the same-image roll on Kubernetes uses). When the node's process dies, a parked instance takes the lock and runs the normal
    start (records, a new generation). There is no permanent standby: the trigger starts one only for an update.
  - **Arm** (`/admin/restart`, `?dryRun=true` answers the path without arming, `?mode=unload` forces the path through
    records) diffs the manifests and picks the path: **in place** (the handover carries every world), **through
    records** (every actor is deactivated and written first; the handover carries empty worlds and the new process
    activates on demand) or refused (a rolling upgrade). Arming writes every `Unsaved` root once while the actors run
    (so a crash of the new process later loses only the changes since), writes the arm file (nonce, armed manifest
    hash, deadline) and is refused while membership or a handoff is changing placement, while another node holds the
    restart permit, when the standby would not fit in memory (it needs about the node's working set: its own
    memory limit or the machine's available memory on bare metal and Compose; on Kubernetes the rollout first resizes
    the parked container's requests in place, below), or when the store's measured p99 write latency is above a
    quarter of `DaServer:HotRestart:FreezeBound` (2 s): the freeze waits for the writes in flight and one write of every
    root holding output, so a slow store would only make the freeze pass its bound and resume in place. A same-image roll (configuration only) is the same path:
    the armed hash is the running one.
  - **Trigger**: the platform restarts the pair's instance that is not the node, so it comes up on the new build,
    reads the arm file and becomes the standby: it finishes .NET start-up, warms its code (nothing to JIT under AOT;
    ReadyToRun plus the warm-up path under JIT), opens its store pool, joins nothing, serves nothing and **listens**
    (it is the pipe's server) on a `NamedPipeServerStream` with `PipeOptions.CurrentUserOnly`: a Unix domain socket in
    the directory on Linux and macOS (mode 0600, a stale socket unlinked; a path longer than the platform's limit,
    108 bytes on Linux and 104 on macOS, refuses to start, and `DaServer.Aspire` checks it at publish; no fallback under
    `/tmp`, which Compose's per-container `tmpfs` and systemd's `PrivateTmp=` do not share and where another user could
    take the name), a named pipe on Windows; AOT-safe; not an anonymous pipe, since the platform's supervisor starts
    the standby and no handle can be inherited. On Windows the pair runs under one service account (a dedicated user
    or gMSA) or the pipe carries a `PipeSecurity` ACL granting the other service's SID: with two default virtual
    accounts `CurrentUserOnly` would refuse the node.
  - **Handover**: the armed node is the pipe's **client**. From the arm on it connects and retries (with back-off)
    until a standby passes the handshake (arm nonce, the armed manifest hash equal to the standby's own, deadline, the
    standby's process id, all before any bytes) or the arm expires, in which case it never freezes and the rollout
    reports the failure. An instance still on the old build fails on the hash and the node simply retries. Once the
    handshake passes, the node takes the cluster's one restart permit (its row's `restart_until`, a versioned write;
    other members see `NodeRestarting` and pin its slots), roots carry `Restarting` for one last tick, and the worlds
    freeze between two ticks (bounded; in-flight writes land first and roots with output held by `Commit` are
    written, so no held output travels; a root whose `SaveEntitiesWith` throws drops its held output and is destroyed in
    the frozen world, left out) while IO keeps receiving. At the switch point the node stops reading its links and
    streams, from pooled buffers and with no intermediate file: a header (node name and generation, arm nonce, old and
    armed manifest hashes, layout hashes, world count, `TickCount64` at the freeze; a single node's member row), each
    world's `Save(output, in exclude)` bytes (each world serialized on its own thread with an incremental checksum,
    the worlds' chunks interleaved on the pipe, so the pause is about the slowest world's serialization plus the
    standby's load; `exclude` names the user singletons and the runtime's `Outbox`, `Current`, `Time` and node-singleton
    copies, which the new process creates before `LoadKeeping(input, in keep, markWritten: false)` keeps them), the dispatch
    queues' mail, the runtime state (call-id sequence, tombstones, every link's byte counts and unacknowledged tail,
    how long mail and deadlines have waited), and **the listening sockets**: `SCM_RIGHTS` over the Unix domain socket
    (P/Invoke `sendmsg`, AOT-safe; the standby wraps each descriptor with `new Socket(new SafeSocketHandle(fd,
    ownsHandle: true))`), and on Windows `Socket.DuplicateAndClose(standbyPid)` with the `SocketInformation` bytes sent
    over the pipe (`new Socket(SocketInformation)` on the other side). The ports are never unbound, and connections
    waiting in the accept backlog are kept. Kestrel's HTTP port is passed through `ListenHandle` where it accepts one
    (W6 checks each platform; elsewhere the HTTP port is re-bound, a short gap for probes and admin only).
  - **Ack and park**: the standby checks the bytes as they come (checksum, nonce, manifest hash, deadline), creates the
    recorded number of worlds whatever its CPU share (refusing only when configuration pins another count), loads the
    cluster's current configuration tables, creates its world singletons, loads each world keeping them, rebuilds the
    due heap and the call index, and answers an ack or a nack. On the ack the node releases the lock and the sockets,
    destroys its worlds, runs one compacting full collection (`GC.Collect(2, GCCollectionMode.Aggressive, blocking:
    true, compacting: true)`, which returns the freed memory to the system) and **parks in place**: it is the pair's
    parked instance from then on, and the next hot restart's trigger restarts it. It never exits, so no handover
    depends on a supervisor starting a process again: on Kubernetes its container keeps running, the pod never drops
    out of readiness for it, and no kubelet restart back-off applies. On a nack, a broken stream, no answer within the handover timeout or a freeze past its bound, **the node
    resumes in place**: it released nothing (lock, sockets and row are its own; it clears `restart_until`), its roots
    carry `Resumed(Pause, Aborted: true)` for one tick and nothing is lost; it reports the failure and the rollout
    stops for that node. A hot restart never ends the process on failure: nothing asked it to stop. SIGTERM is always
    a graceful stop (ADR-063), never a hot restart.
  - **Rejoining**: the standby learns the node is gone from the node's last word on the pipe ("handed over") and by
    taking the lock, adopts the sockets
    and reads its row: still `Active` with its generation and the old process's `restart_until`, it clears
    `restart_until` (a versioned write), checks that every root's slot is still its own (fencing or moving the others
    out), reconnects with the same identity (name, generation), and the peers resume its streams from their resend
    windows; written `Dead` (the deadline passed) or out-generationed, it drops the loaded worlds and joins with a new
    generation from records. Roots carry `Resumed` for one tick. No actor re-activates and epochs stay. A standby that
    takes the lock without a completed handover (the node crashed) discards unacknowledged bytes and runs the normal
    start; `Rejoining`'s `restart_until` check rules out a double load.
  - **Readiness**: `/health` reports the process's instance id (random at start), its instance name and the running
    manifest hash. The rollout waits for a new instance id **with the armed hash**: the listening ports move with the
    sockets, so the old process answers until it hands them over, and a node that resumed after a failed handover
    reports its old id.
  - **Platforms** (the trigger is the one platform-specific step of the one rollout loop, deployment.md section 5):
    systemd (two enabled instances of `daserver@.service`, `daserver@map-0-a` and `-b`, the directory from a
    `tmpfiles.d` entry since `RuntimeDirectory=` is removed when a unit stops; `systemctl restart` of the other
    instance), launchd (two jobs; `launchctl kickstart -k` of the other), Windows (two services; `Restart-Service` of
    the other), Compose (a holder service `map-0-net`, a pinned image no release recreates, owns the network namespace
    and hostname; `map-0-a` and `map-0-b` join it with `network_mode: service:map-0-net` and share a named volume for
    the directory; all three `restart: unless-stopped`; `docker compose up -d --no-deps --force-recreate` of the
    other), local Aspire (two replicas of the node resource) and **Kubernetes** (below). After a handover the old
    instance parks in place on every platform; the supervisors' restart policies (`Restart=on-failure`, `KeepAlive`
    with `SuccessfulExit = false`, Windows recovery, `restart: unless-stopped`) matter only for crashes and the
    isolation exit (code 75).
  - **Kubernetes**: one pod with two containers, `node-a` and `node-b`: the same image and configuration, the pod's
    network namespace, and a small `medium: Memory` `emptyDir` for the directory. Each container serves its probes on
    its own port (`DaServer:ProbePort`), and a parked or standby instance reports ready, since it serves nothing, so
    the pod's readiness depends only on the node. Both containers carry the same memory limit; requests follow the
    role: the node's container carries the node size (CPU and memory) and the parked one the idle size, so the pod is
    sized and scheduled as one node; the chart renders `node-a` node-sized, so at a cold start it becomes the node (an
    idle-sized instance never takes the lock first). The processor count is the chart's node size as a literal, never
    the request, so no resize changes a process's parallelism; a process reads its own container's current requests
    from its cgroup (`cpu.weight`, `memory.min`/`memory.low` as the kubelet sets them), never from a `resourceFieldRef`
    environment variable, which keeps the value from container start and goes stale after an in-place resize. The rollout Job resizes the parked container's requests
    up to the node size in place (memory: the larger of `nodeSize.memory` and the VPA's recommended target for either
    container, capped at the containers' memory limit, since a request above the limit is invalid; the pod's `resize` subresource, beta and on by
    default from Kubernetes 1.33, GA in 1.35, `resizePolicy` `NotRequired`, so nothing restarts; not available for Windows pods
    or on machines with the static CPU or memory manager, which take the delete-and-recreate path); when the resize
    reports `Infeasible`, or is still `Deferred` at its deadline (it might apply later), the Job sets the request back,
    the arm is refused and that pod takes the delete-and-recreate path through records, so no large request is left to
    apply later. Then it applies only that container's
    image (one forced server-side apply with the pod's `controller-revision-hash` label, ADR-085), so the kubelet
    restarts only that container, which comes up as the standby; after the handover the old process parks in place,
    and the Job resizes its requests down to the idle size. A crash takeover (the parked instance becomes the node in
    an idle-sized container) leaves the requests swapped: the pod's total is right, but the node's CPU weight is the
    idle one (starved under contention) and its OOM score is computed from the idle request (the first victim). So
    the swap back is automatic: a node that finds itself in an idle-sized container (from its cgroup) reports
    `daserver.node.requests_low` and asks the rollout's resize-only path through the deploy webhook (`--resize-only
    --node <name>`, the same Job image, its own Role), retrying with backoff until its cgroup shows the node size; the
    resize swaps the two containers' requests in place, so nothing restarts.
    **Autoscalers**: the busy ratio is exported by node processes only, so a KEDA or HPA average over the group
    counts nodes whichever container they run in (a CPU-utilization HPA also works, the parked container adding
    almost nothing to the pod's usage or request; a `ContainerResource` one cannot, since the node alternates between
    the two names); the VPA is `Off` on these pods, since it recommends per container name and an active one would
    size the parked container as a node or race the Job's swap, and the Job reads its recommendation instead
    (deployment.md section 10). A same-image roll
    has no image to apply: the Job asks the node, through its admin endpoint, to tell the parked instance over the
    pipe to exit, and the kubelet restarts it with a fresh read of env and `envFrom`. The kubelet's restart back-off
    (10 s, doubling, for a container that exited within 10 minutes of its start) only delays a parked instance's
    return, never the pause. **Readiness window**: a pod is Ready only when every container is, so for the seconds
    the parked container restarts (an image apply, the same-image exit) the pod is not Ready. Groups that hot restart
    host no `Connection` (gateways never hot restart), so no client Service depends on it; the PDB counts the pod as
    one unavailable, which blocks another eviction in the group for those seconds: one node at a time, as intended. Ephemeral containers stay for debugging: they cannot have ports or probes. The full target
    shape (StatefulSet with `OnDelete`, Services, probes, the rollout Job and its Role) is [deployment.md](deployment.md).
- **Changes without a restart** are data swaps between two ticks: tuning tables (`IConfigTable<T>` singletons; with a
  store it keeps one current version per table, (name, version, hash, bytes), the coordinator writes (name, version,
  hash) into the membership table's version row, every starting or hot-restarted node loads that version, the first reader seeds a table type
  the store has no version of from its configuration, `POST /admin/tables/{name}` validates and replaces it
  cluster-wide, a rollback posts the old bytes again, and a node whose `TryLoad` rejects the current version fails
  readiness, which the upgrade check prevents by trying the new build's `TryLoad` when a table's format hash changed;
  without a store each node reads `DaServer:Tables:{name}` and a `POST` changes only that node) and feature flags choosing between handlers compiled into the binary (a singleton or a
  tag that handlers and systems filter on).

## 6. Distributed guarantees and failure handling

The guarantees and their mechanisms are [draft §1](programming-model.md#1-保证); the outcome of each failure, what each
path guarantees (delivery, order, dedupe key, who times out and who retries), the retry rules and the timer defaults
are [draft §11](programming-model.md#11-失败时的结果).

## 7. EasyEcs features used

Which EasyEcs 4.7 feature each mechanism uses, what DaServer's attributes and
generator add, and what the runtime does:

| Mechanism | EasyEcs | DaServer attributes and generator | Runtime |
|---|---|---|---|
| Finding entities by key | Value indexes and their generated members (`world.NetIdIndex.TryGet`, `world.CellIndex.Find`, its O(1) `Count` for an empty cell); unique ones `IUniqueIndexed<T>` (`Get`/`TryGet`; a write that would repeat a key throws before it changes anything) | Explicit `T IIndexed<T>.Key => this` for every partial `IActor<T>` and self-keyed `IIndexed<T>` record struct | `world.Index<K, K>().TryGet` on receipt (the generic form of the same index) |
| Which actor type logic belongs to | — | `[Handles<K>]` | Dispatch tables per actor type |
| Order of events | Singletons | `Due` component | `OrderedPhase` merges the mailbox snapshot, the due heap and the chain by `(time, seq)`; state in `Ordered<K>` |
| Message handling | Single-entity jobs (`entity.Run(ref job[, filter])`), `[With<T>]`, `[Without<T>]`, `INoStructuralChanges` | `[On<T>]` methods compiled into job structs with EasyEcs's own `WriteMask`/`SelfStamped` contract (`WriteMask` = the `ref` and `Mut<T>` bits, which also decides which refs are carried to a new row after a structural change; `SelfStamped` = the `Mut<T>` bits, which stamp only when written, as EasyEcs's `SystemGenerator` emits); `Run` takes one to nine components, so a method without component parameters is no job: the dispatch tests the filter itself (`IsAlive`, `Has<T>` per `[With<T>]`/`[Without<T>]`) and calls it directly | One item at a time, exceptions isolated per item |
| Parameter binding | `[ForEach]`'s parameter rules, `Mut<T>` | Component parameters bind to the subject; the actor type and `IClaims` parameters are fields of the job | The job gets the subject's components |
| Output | Singletons | — | `Outbox`, serialized on append; the item's context in `Current` |
| Per-tick logic | `EcsSystem`, `[ForEach]`, batch methods, `Parallel`, `[Changed<T>]`, `[UpdateAfter<T>]`, `ISystemGroup`, `ISystemGroupWith<T>`, `SetIfChanged`/`SetXIfChanged` | The actor type is the system group, `[TickRate]`, `In<K>` | Ordering → `world.Update<K>()` → Settling |
| Events inside an actor | `ChildOf` | `Schedule(ev, after:)`, `Pending<T>`, `IEvent`, `Due` | Due heap, chain stack; gone with their initiator |
| Send direction | — | `[From<K>]`: typed sends, the analyzer checks the calling actor type | Send, reply, timeout |
| Client routing | — | `[Handles<K>]` and `[To]` give the route table | `Connection` forwards on the frame header without decoding the payload |
| Asks and replies | `ChildOf` | `Replied<TReq[, TState]>`, `ReplyTo<T>`, `Commit`, `[Once]`, `AskUntilAnswered` | Call index, `Due` deadlines, remaining time; output held by `Commit`; `OnceNext`, `Asking`, `Unacked`, `OnceSenders` |
| Presences | Child entities, value index, cleanup components (`ICleanupComponent`, `Retired`) | `[JoinOnConnect]`, find by the unique index or `root.Spawn`, `Join()`, `Leaving`, `Detached`, `Joined(MissedPushes)` | Stamps, routes, confirmation, grace, reconciliation, `ReleaseRoutes` |
| Subscriptions | `DynamicBuffer`, `[Changed<DynamicBuffer<T>>]` | `Subscribed`, `SetGroups` | `SyncSubscriptions` |
| Lifecycle | Tags | `[On<Activated>]`, systems on `Leaving`, `Deactivating`, `Restarting`, `Resumed` | Placeholders, epochs, fencing, `Depart` |
| An actor's entities | `ChildOf`, `world.Children`, prefabs, `world.Instantiate` with up to nine given components (`ChildOf` one of them) | `In<K>`, `K.RootPrefab`, `[Prefab<K>]`, `[Persist]`, `root.Spawn`, `e.Root` | Registered prefab copies |
| Writing only what changed | `ITracked`, `[Changed<T>]`, `[Removed<T>]`, `ChangedAny`/`RemovedAny`, `ICleanupComponent`, `DynamicBuffer` (itself `ITracked`); persisted types declare `ITracked` in source, since 4.7's `ChangedAny<T1..Tn>` names its types at compile time (request (b) for prefab compositions, not in 4.7) | Persistent prefabs, `[NotSaved]`, `Commit`; `Registration` throws at start when a type on a `[Persist]` prefab lacks `ITracked` | `Persisted`, `ChangeSystem`, `CheckpointSystem` |
| Records, migration | `ComponentSet`, `Descendants(root).AddTo(ids, includeRoot: true)` (parents first, returns the count added, 0 B with a reused list), `SaveEntitiesWith` (ids as a span; a set without singletons), `DestroyEntities(ids)` (a subtree in one structural gate), `LoadEntitiesInto` (`markWritten: false` on restore) | Persistent prefabs, `[NotSaved]` | Store, epoch CAS, layout hashes |
| Wake | `[Changed<WakeAt>]`, `[Removed<WakeAt>]` | `WakeAt` (framework and packages) | `NextDue`, `DueScan`, wake handlers |
| Version compatibility | Nino type ids, `[NinoFormerName]` | `wire.manifest` | Rolling upgrades |
| Hot restart | `world.SaveWithout(output, in exclude)`, `world.LoadKeeping(input, in keep, markWritten: false)`, `ComponentSet.WithSingleton<T>()` | `wire.manifest` diff picks the path; the handover's stream format | Freeze between two ticks, the stream to the standby, no re-activation |
| Tests, diagnostics | One world, `SystemErrorPolicy.Throw`, `Capture`/`Restore`, `world.Count<Retired>()`, `world.DescribeTables()`, `world.Read<…>(filter)` (read-only passes for the inspector and the admin plane: no tick stamped), `DescribeSchedule()` (text or mermaid), `SystemStats` allocated bytes and entities visited, O(1) `Find(key).Count`, `Children(parent).Count` and `index.KeyCount` | `Registration`, `TestActor<K>` | Manual clock, decoded `Outbox`, allocation assertions |
| Pooling | `DynamicBuffer` pools, Nino's `[NinoPooled]` | — | Frames and send buffers |

## 8. Native AOT

A DaServer node can publish with `PublishAot=true`, chosen per node (ADR-083, requirement E11, [draft §10](programming-model.md#native-aot)); the release
checklist publishes the samples' nodes with it. JIT images stay supported and behave the same: neither loads assemblies at run time, and both change code by hot restart
or rolling upgrade (section 5).

| | JIT deployment | Native AOT deployment |
|---|---|---|
| Start-up, memory | JIT warm-up, tiered compilation | Native start (tens of milliseconds), smaller working set |
| Reflection | Allowed where documented as JIT-only | None |

### What makes it work

- **Registration is generated and closed**: `Registration.Create()` names every closed type in the user's assembly
  (`.Actor<Map>()`, which reads `Map.RootPrefab`; `.Dispatch<Map>(…)`; `world.AddSystem<Steering>()`). No reflection,
  no `MakeGenericType`, no `Activator`, no assembly scanning, no module initializers.
- **Per-actor-type framework groups are closed subclasses**: EasyEcs assigns a system to a group only through
  `[UpdateInGroup<T>]`, and C# forbids that attribute on a generic `OrderedPhase<K>` with `K` as the argument
  (CS8968). The generator therefore writes, per actor type, `[UpdateInGroup<Ordering<Map>>] sealed class
  OrderedPhase_Map : OrderedPhase<Map>;` and likewise `Depart_Map`, `SyncSubscriptions_Map` in `Settling<Map>`, and
  `Registration` adds those; the loop calls `world.Update<Ordering<Map>>()` and `world.Update<Settling<Map>>()`, so
  `DescribeSchedule()` shows each type's own groups (EasyEcs request (c), adding a system to a group without the
  attribute, would remove the subclasses).
- **Framework per-actor-type systems are emitted closed by the generator**: the generated `Registration` names each
  instantiation. The framework's generic systems have no `[ForEach]` methods (they run their jobs from `OnUpdate`
  through EasyEcs's generic runners, closed by their type arguments), so EasyEcs has no generic system code to close
  at run time. EasyEcs closes a generic system named only by another assembly with `MakeGenericType`, which Native AOT
  cannot do (EasyEcs D20; `faq.mdx#native-aot`), and suppresses the warning for it: such a mistake would fail at run
  time, so DaServer's analyzer reports a user's generic `[ForEach]` system closed outside its own assembly
  ([draft §13](programming-model.md#13-编译期检查)).
- **Handlers are jobs**: `[On<T>]` methods become struct jobs called through generic `entity.Run<…, TJob>`, closed at
  compile time; dispatch tables hold static method-group delegates.
- **Serialization is Nino's generated formatters** (messages, persisted components, records, managed components in
  migration bytes); unmanaged components travel as raw bytes under their layout hash; type ids are Nino's. EasyEcs
  records types by Nino type id (D30).
- **EasyEcs**: `EasyEcs.Core` is marked `IsAotCompatible`; its generated code calls user methods directly (what it publishes without warnings: the zero-warning gate below).
- **The HTTP plane, no JSON in the framework** (the one place for these rules): `WebApplication.CreateSlimBuilder`;
  minimal APIs through ASP.NET Core's request delegate generator.
  - **Admin endpoints** (`/admin/membership`, `/admin/stats`, `/admin/restart` with `mode=unload` and `dryRun=true`, unload, `/admin/tables/{name}`, the inspector below) answer typed `[NinoType]`
    records as Nino through Nino.AspNetCore's Minimal API results (`NinoResults`, `application/x-nino`), or a plain-text
    table for `Accept: text/plain` (an operator with curl). The Aspire commands decode the Nino reply and show the text.
  - **Probes and metrics** keep their platform formats: plain-text health, Prometheus text exposition.
  - **`MapAsk` / `MapSend`** take Nino bodies only by default (Nino.AspNetCore's `NinoBody<T>`; a wrong content type is
    415), and answer through `NinoResults`. An application that must accept JSON (a third-party
    webhook) opts in per endpoint with a source-generated `JsonSerializerContext` it declares (the System.Text.Json
    generator does not see other generators' output, so DaServer cannot declare it). No reflection-based JSON
    anywhere.
  - **Routes** are read from `wire.manifest`, not from assemblies.
- **Configuration**: the binding calls live in the framework library (`DaServer`, after ADR-086), where
  `PublishAot` of the user's project does not switch the configuration binding generator on, so the library sets
  `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` itself. `NodeOptions` splits into a
  bound POCO of values (`NodeSettings`: sizes, intervals, paths) and the runtime wiring set in code (the type filter,
  `ISeedResolver`, `TimeProvider`, `IActorStore`, `ILoggerFactory`, the feature list), which the binder cannot and
  must not bind. `GetValue<TimeSpan>` and the generic `configuration.Get<TOptions>()` of `AddDaServerFeature<TOptions>`
  move to closed call sites. A W8 test publishes a node with warnings as errors and checks no SYSLIB1100/1101,
  IL2026 or IL3050.
- **Framework projects** set `IsAotCompatible` on their net11.0 targets, so the trim and AOT analyzers run on every
  build under `TreatWarningsAsErrors`.

### Telemetry

- **One exit per node in production**: OTLP (metrics, logs, traces) to one OpenTelemetry Collector, which sends
  metrics to Prometheus's OTLP endpoint (`/api/v1/otlp`, enabled by `--web.enable-otlp-receiver`, with
  `otlp.promote_resource_attributes: [daserver.cluster]` in its configuration), logs to Loki and traces to Tempo. With a
  Collector, Prometheus does not also scrape the nodes: one metrics path, so no series arrives twice under two
  identities. The metric export interval is pinned to 15 s (`AddDaServerTelemetry` sets the periodic reader's
  `ExportIntervalMilliseconds` to 15000; `OTEL_METRIC_EXPORT_INTERVAL` overrides it), not the SDK's 60 s default, so
  the alert rules see each node at least eight times in two minutes. Pushed series get no staleness markers, so the
  rules read gauges with `last_over_time(...[2m])` and rates over at least four pushes. The ask latency
  `daserver.ask.duration` is a histogram in seconds (unit `s`), so it reaches Prometheus as
  `daserver_ask_duration_seconds`; `daserver.node.requests_low` is a 0/1 gauge. `/metrics` stays on the HTTP plane for an
  environment without a Collector, which scrapes it instead. KEDA and the Prometheus adapter query Prometheus, not the
  pod, so autoscaling works the same on either path. Stop-phase durations (`daserver.stop.phase.duration`) go out over
  OTLP with the final flush, and as stop-phase log lines in Loki; a scrape would rarely catch a stopping node.
- **One identity**: `AddDaServerTelemetry` sets one OpenTelemetry resource, `service.name` = the node group (server
  type) and `service.instance.id` = the node name, and no `service.namespace` (it would make `job` read "ns/name"). A
  parked hot-restart instance exports no metrics (its meter provider exports nothing until it holds the lock) and logs
  with `daserver.instance` set, so the node name is a telemetry identity only while its holder has the lock. Metrics keep a `node` label with the same value, which a direct
  scrape needs; logs carry no separate node attribute, since Loki indexes `service.instance.id`. DaServer's own
  instruments also carry `cluster` and `type` labels next to `node`, so alert rules group by cluster and type and one
  Prometheus can hold several clusters; runtime metrics get them from the resource (`service.name` → `job`,
  `service.instance.id` → `instance`, `daserver.cluster` promoted to `daserver_cluster`).
- **One spelling per place**: the docs name an instrument in OpenTelemetry form (`daserver.membership.frozen`,
  `daserver.shard.busy_ratio`); Prometheus shows it with underscores (`daserver_membership_frozen`), the form only
  PromQL uses (the alert rules, the autoscalers' queries in deployment.md).
- **One reading table**: the node's readings (name, unit, kind, reader) are declared once; the instruments, the
  `/admin/stats` answer and the stats line are generated from it. The stats line is off by default when OTLP is set
  (load tests turn it on); it would repeat Prometheus in Loki every 30 s per node.
- **One definition of "wrong"**: `observability/prometheus-rules.yaml`. The Dashboard's triage shows the alerts that
  fire and uses outliers only to find the culprit node within one; its history links open Grafana (Explore for logs
  and traces), which reads Prometheus, Loki and Tempo.

### Inspector and admin clients

The admin endpoints are the single source; every client is thin over them:

| Client | Where | What it shows or does |
|---|---|---|
| Aspire commands | Local runs (Aspire has resource commands in run mode only) | Membership, stats, the inspector, unload, `{cluster}-restart` |
| `DaServer.Dashboard` | Production (ADR-093) | The same endpoints, actions confirmed and audited; the view (members, their states, the slot table with its version) from up to three ready nodes' `/admin/membership`, chosen again each refresh from the newest view, keeping the highest version (the last view stays, with its age, while no node is ready), so it finds an actor's owner without being a member; a node's restart, a type's hot restart and a rollback go through the deploy pipeline's webhook (`DaServer:Dashboard:DeployWebhook`, the one rollout loop), a config table push through the admin endpoints |
| curl | Anywhere, with the admin token | `Accept: text/plain` answers |

The inspector's read-only endpoints are modelled on flecs's REST API behind its Explorer
([Remote API](https://www.flecs.dev/flecs/FlecsRemoteApi.html)):

| Endpoint | Answers |
|---|---|
| `GET /admin/actors/{type}/{key}` | `Describe()` of the actor's root and of each entity in `world.Descendants(root)`: component names and values, decoded through `wire.manifest`, and each entity's child count (`world.Children(e).Count`, O(1)) |
| `GET /admin/actors/top` | The busiest actors by mailbox depth and handler time |
| `GET /admin/worlds/{i}` | `world.DescribeTables()`: tables, rows, memory; per value index its distinct keys with entities (`index.KeyCount`, O(1); e.g. occupied cells), also exported as metrics |
| `GET /admin/worlds/{i}/systems` | `world.DescribeSchedule()` (per group its stages, why a system waits, the pairs only registration order orders; as text, or `ToMermaid()`) and each system's `system.Stats` (time, `LastAllocated`/`TotalAllocated`, `LastEntities`/`TotalEntities`, under `MeasureSystems`, also exported as metrics) and `system.Error` (a system disabled by `SystemErrorPolicy.Disable`) |
| `GET /admin/worlds/{i}/count?with=Health,Dead` | The number of entities with those components; names resolve to `TypeId`s through the registration's component table, no reflection |

- The cold path: each call runs on the world's thread between two items, allocates, and only on request.
- Answers are Nino or plain text, like every admin endpoint (above, ADR-083).
- Nothing writes through them: an operator changes an actor by sending it a message through `ICluster` (`MapSend`,
  `MapAsk`, an Aspire command), so the change runs as an ordered item under the actor's own rules.

### To verify (W8)

| Dependency | Version in the repo | Status |
|---|---|---|
| OpenTelemetry (`Extensions.Hosting`, OTLP exporter, ASP.NET Core, HTTP and runtime instrumentation) | 1.19.x | To verify: warnings under `PublishAot` |
| `OpenTelemetry.Exporter.Prometheus.AspNetCore` | 1.19.1-beta.1 (beta: pinned) | Marked trim- and AOT-compatible since 1.17.0-beta.1 (OpenTelemetry .NET PR #7441); W8 confirms with the publish. Production uses the Collector path anyway (Telemetry above) |
| Npgsql through `Aspire.Npgsql` | 13.6.1 | To verify: whether the Aspire integration builds the data source in a way Native AOT accepts |
| `Nino.AspNetCore` | see `Directory.Packages.props` | Its project sets `IsTrimmable` and `IsAotCompatible` (targets net8.0 and net10.0), but its docs claim no AOT support and Nino's AOT test app publishes `Nino.Core` only: verify a published node using `NinoBody<T>` and `NinoResults` |
| `Microsoft.Extensions.ServiceDiscovery`, `Microsoft.Extensions.Http.Resilience` | see `Directory.Packages.props` | To verify |
| ASP.NET Core with `CreateSlimBuilder` | .NET 11 | To verify that `AddDaServer` needs nothing the slim builder drops (TLS is the platform's) |
| The hot-restart handover's socket passing | .NET 11 | `sendmsg` with `SCM_RIGHTS` through `LibraryImport` and `SafeSocketHandle` adoption on Linux and macOS, `Socket.DuplicateAndClose`/`new Socket(SocketInformation)` on Windows, Kestrel's `ListenHandle` per platform: verify each in a published AOT node (W6, W8) |

### The zero-warning gate

- Builds and publishes have 0 warnings (release checklist step 1, E11). A suppression of a library's warning in the
  user's program is not accepted, scoped or documented.
- EasyEcs 4.7 publishes without warnings with tracked components (`faq.mdx` "Native AOT"; every stateful DaServer node has them:
  persistent entities' components are tracked), disabled components and saves (its CI publishes and runs a native server with
  warnings as errors). What W8 still verifies is the third-party packages in the table above.
- **Proof**: `dotnet publish -p:PublishAot=true` for each sample node (mmo, flashsale) with 0 warnings, and the E2E
  tests passing against the AOT binaries.

## 9. From today's code to the target

Derived from [design.md](../design.md); what today's code has and the target drops is this table.

| Today | Target |
|---|---|
| `[Actor<TId>]` static class, a separate id type, `ActorKey` component | The actor type `record struct Map(long Id) : IActor<Map>`: address, root component (value index), root prefab (`Map.RootPrefab`) and system group; `ActorId` for any actor |
| `[Handle]` methods, generated dispatchers, `in Context` | `[On<T>]` methods in `[Handles<K>]` classes, compiled into EasyEcs single-entity jobs; one binding rule (the subject; the actor key and the claims are the two exceptions) |
| `[Service]` | `[Stateless]` actor type |
| `ctx.Send`/`Ask`/`ReplyLater`/`BindSession` | `Entity` operations (`Send`, `Ask`, `Reply`, `Push`, `Join`, `Leave`, `Schedule`, `Spawn`), serialized into `Outbox` |
| `Answered<TRequest,TReply>`, `PendingReply`, `Answer<T>.Later` | `Replied<TReq>` / `Replied<TReq,TState>` to the asking entity (a waiting child with `Due`), the value through `TryGetValue`; `ReplyTo<T>` as data |
| `IRequest<TReply>`, `[Message]`, `[FromClient]`/`[ToClient]` | `IFromClient`, `IFromClient<R>`, `IToClient` (Protocol); `IMessageTo<K>`, `IRequestTo<K,R>`, `[From<K>]` (Server); `IEvent` |
| `Publish` (to every service type handling a message) | Deleted; pub/sub between actors is the optional `DaServer.Topics` (ADR-090), fan-out to clients is `ClientGroup` |
| `[Filter]`, `IMessageFilter` | `[On<Incoming>]` in `[Handles<Connection>]`, over the frame header |
| Mailbox used only while an actor is not live; `DispatchSystem`, `MailSystem` | Every item through the ordered phase, merged with the due heap and the chain by `(time, seq)`; mail waits on the placeholder |
| Lifecycle tags (`Loading`, `Ready`, `UnloadRequested`, `Leaving`, `Fenced`, `Removing`), `Created`/`Loaded`/`Unloading` | Placeholder root (`Loading`, `Saving`), `Activated(Activation)`, the `Deactivating` tag, fencing by epoch |
| `TimerSystem`, timer wheel, `SendAfter` | `Due` component with the runtime's due heap; `e.Schedule(ev, after:)`; `Pending<T>` (also what `Ask` returns) |
| Sessions: bind and grant, `KnownSession` children, `Known`/`Granted`/`Superseded`, `SessionClosed` | Presences: `Join()`/`Leave()`, `Presence`/`Joined`/`Leaving`, `PresenceLost`, `[JoinOnConnect<TClaims>]`, ordered stamps |
| `ClientConnections` feature (`DaServer:Clients`) | `Connection` actor type hosted by a `[Hosts<Connection>]` server type |
| `MaxConnectionAge` spreads connections after a scale-out | Each gateway sheds only its excess above the group average, rate-limited, through `Retire` (ADR-089); the age stays an optional setting, off by default |
| Tick rate in configuration, the default group at the tick rate | `[TickRate(n)]` on the actor type; `[UpdateInGroup<K>]` |
| `UnloadAfterIdleSeconds` | `[ActorOptions(IdleTimeout = …)]` on the actor type |
| `SaveEverySeconds` (30 s per actor type) | `DaServer:CheckpointInterval` (30 s per node) for plain changes; a root that calls `Commit` writes at the next tick end |
| Callers retry by hand and receivers deduplicate by hand | Orders are their own actors; the step that moves value is a `[Once]` request sent by `AskUntilAnswered`: framework-assigned sequence numbers, the reply stored in `Unacked` and committed before it leaves, cumulative acknowledgements, windows and probes as in TCP, and the request itself the persisted intent the runtime keeps asking (ADR-097, ADR-099) |
| `[Persist]` per component type with a generated `ITracked`, the own record format | Persistence per entity kind (`[Persist]` roots and children, `[NotSaved]` components left out); records as `SaveEntitiesWith` over `Descendants` with a `ComponentSet`; epoch CAS |
| State handoff (ADR-072) | Migration bytes with layout hashes, a new epoch on arrival; fallback to records |
| `Registration_{Assembly}` run by a module initializer (ADR-012) | `Registration` instance from the generated `Registration.Create()` |
| One project per server (ADR-017), Contracts / Model / Logic (N1) | Protocol / Server; several server types per Server project; node = one server type |
| Hot reload of the logic assembly (ADR-067, never built) | Hot restart by a warm standby handover over a local pipe, the same on every platform (JIT and AOT, ADR-098) |
| `AddNodes<TProject>` in a `Sample.AppHost` project | `AddNodes<TServer>` over server types generated from `wire.manifest`, in `Game.Server/apphost.cs`; ports declared per group |
| Packages Abstractions, Core, Client, Simulation, `DaServer`, Hosting, WireTool, Testing, Persistence.PostgreSql, Aspire (ADR-052) | Core, Client, `DaServer`, Persistence.PostgreSql, Testing, Aspire; the generator and the out-of-process manifest tool inside Core (ADR-086) |
| Shard count in configuration and AppHost parameters (`DaServer:Shards`, `map_shards`) | Sized by the node from its CPUs; `DaServer:Worlds` overrides; the word shard is internal |
| Random `NodeId` per process | Node generation assigned by the coordinator; identity on the link Hello is (name, generation), so a hot-restarted node resumes its streams |
| A Kubernetes Deployment per node group | StatefulSet with `OnDelete` for actor-hosting groups, Deployments for gateways (surge then drain) and stateless groups (ADR-085); one rollout loop with a restart adapter per platform |
| Deadlines in UTC milliseconds | Monotonic `TimeSpan`; remaining time across processes |
| Route listing by reflection (`Routes`, the route check starting each node project in `ListRoutes` mode) | Routes from `wire.manifest`; the route check reads the committed manifests through `ManifestDiff` (section 5) |
| Admin JSON of anonymous objects | Typed `[NinoType]` records as Nino, or plain text for `Accept: text/plain` |
| `MapAsk` bodies as JSON or Nino | Nino; JSON only per endpoint, through the application's source-generated context |
| The node host calls no service manager | `Program.cs` declares `UseSystemd().UseWindowsService()`, each a no-op off its platform; launchd is configured in its plist |
| `/admin/drain` (a graceful unloading stop that the platform restarts) and its Aspire command | `/admin/restart` only (`mode=unload`, `dryRun=true`); taking a node out is the platform's scale-down |
| Each node creates the table at start (`InitializeTimeout`, its own backoff) | A schema Job (`--DaServer:Migrate`); nodes are not ready until the table answers, one backoff |
| The Npgsql health check in `/health` | The store is not part of readiness once a node has started |
| `NodeMetrics`, `/admin/stats` and the stats line list the readings separately; the stats line always on | One reading table generates all three; the stats line off when OTLP is set |
| No OpenTelemetry resource; runtime metrics carry only `instance` | One resource (`service.name` = group, `service.instance.id` = node) |

## 10. Open points to settle during W

1. **EasyEcs compile and behaviour checks**: listed, each with its test, only in roadmap step W1
   ([roadmap.md](../roadmap.md)).
2. **Evaluations**, each adopted only without a regression: the Prometheus exporter under AOT (W8, section 8). The
   cluster and client ports keep the own listener: Kestrel's connection pipeline was measured and regressed (roadmap,
   "Open cleanup"), and `TransportBenchmark` stays to measure again when Kestrel changes.
3. **Replication field diff** (W7a): per-field bitmasks against the `Replica<T>` copy of the last sent value double the
   memory of replicated components; measure against diffing whole components on the mmo AOI benchmark (W7a) before adopting. The
   copy's sent stamp (the world's `ChangeTick` when the replication pass ran, opaque to the client) and the incarnation
   stay either way: they decide what a rejoining client still needs.
