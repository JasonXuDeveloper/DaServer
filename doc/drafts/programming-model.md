# DaServer 新编程模型设计（草案 v32，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。ECS 一路到底：能用 EasyEcs 的功能就用 EasyEcs，状态都是数据，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个 Actor 发到哪个 Actor，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分，也看不到节点和 Actor 的地址；
- 分布式下也正确：同一个 Actor 只有一个激活能写，连接有先后，先后和超时不依赖各节点的时钟一致；
- 高性能，高并发。

本稿是完整的一份。每条规则只在它所在的那一节写一次，别处只给链接。第 12 节是完整的例子，分别列出用户写的代码、生成的代码和测试。第 13 节是检查、删掉的东西和版本记录，第 14 节是已定的取舍和待办，包括对 EasyEcs 的需求，第 15 节是可选的扩展能力（各自一个包，不引用就没有）。

文中引用的 EasyEcs 文档都在它仓库的 `docs/content/` 下。本稿基于 EasyEcs 4.6.0（已在 NuGet）：`entity.Run(ref job, filter)`、接口带的组过滤（`IGroupWith<T>`）、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（以上自 4.5），`world.Load(bytes, markWritten: false)`、`AddSystem(…, changesFromNow: true)`、带追踪组件的程序 Native AOT 0 警告（4.6）。另外用到 EasyEcs 4.7（计划中，未发布）的：`Mut<T>` 参数和 `SetIfChanged`、带组件的 `world.Instantiate`、`[ChangedAny<…>]`/`[RemovedAny<…>]`、`WorldOptions.DestroyLog` 和 `world.DestroyedSince`、`IUniqueIndexed<T>`、`world.TrimExcess()`、`[ForEach(Slices = N)]`、`SystemStats` 的分配字节和匹配的实体数、`world.DescribeSchedule()`；用到它们的步骤在路线图里等 4.7 发布。

---

## 0. 一页看懂

### 两个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息。只有数据，不出现任何服务端的 Actor 类型、节点或地址 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | Actor 类型、服务器之间的消息、本地事件、组件、身份、prefab、服务器类型（一个工程可以定义多种）；所有逻辑：`[Handles<K>]` 类里的 `[On<T>]` 方法、`[UpdateInGroup<K>]` 的 `EcsSystem`；生成的 `Registration`；五行的 Program.cs；`apphost.cs`（部署，不参与编译） | 服务端。换代码就是换二进制：节点原地热重启，JIT 和 Native AOT 一样（第 10 节） |

部署不另开工程：`Game.Server/apphost.cs` 是 Aspire 的单文件 AppHost（第 2 节）。

### 用户写的

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `[NinoType] record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 发给指定 Actor 的客户端消息 | `[NinoType] record struct EnterMap([To] long Map) : IFromClient<bool>;` | Protocol |
| 推给客户端的消息 | `[NinoType] record struct SnapBack(Vector2 At) : IToClient;`；含集合的是 `[NinoType, NinoPooled] sealed partial class Nearby : IToClient` | Protocol |
| Actor 类型（地址、根上的组件、系统组） | `[TickRate(30)] readonly partial record struct Map(long Id) : IActor<Map>;`；根要带组件时在类型里写 `public static Prefab RootPrefab { get; } = …`；要改默认的设置时写 `[ActorOptions(IdleTimeout = …, DetachGrace = …, MaxPending = …, OnError = …)]`（第 3 节）；少见的 `[Stateless]`、`[ActivatedBy<T>]` 见下面的进阶表 | Server |
| 服务器之间的消息 | `[NinoType, From<Map>] record struct GrantItem(int Item) : IRequestTo<Player, bool>;`；必须落定一次、两端都要扛住宕机的请求加 `[Durable]`（第 8 节） | Server |
| 本地事件 | `record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；持久化的写成 `[NinoType] record struct Grid(…) : IPersisted;`（第 10 节）；不随迁移的标 `[Transient]` | Server |
| 子实体的键 | `partial record struct NetId(Map Map, int Value) : IChildKey<NetId>;`，第一个字段是 Actor 类型（第 3 节） | Server |
| prefab | `[Prefab<Map>] static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);`；Actor 里的实体都用 `root.Spawn(prefab, …)` 建；按键找到或建出用 `root.GetOrSpawn(prefab, key, …)` | Server |
| 身份 | `[Claims] readonly partial record struct Account(long Id);`：令牌里的声明，用户自己的类型 | Server |
| 连上就加入 | `[JoinOnConnect<Account>] readonly partial record struct Player(long Id) : IActor<Player>;` | Server |
| 服务器类型 | `[Hosts<Map>] public readonly struct MapServer : IServer;` | Server |
| 对消息和事件的反应 | `[Handles<Map>] static partial class Combat { [On<CastSkill>, Without<Stunned>] … }` | Server |
| 每 tick 或对数据的反应 | `[UpdateInGroup<Map>] class Movement : EcsSystem { [ForEach] … }`；热重启前后的反应也是系统：`[ForEach, With<Restarting>]`、`[With<Resumed>]`（第 10 节） | Server |
| 本 Actor 里现在或以后做一件事 | `me.Schedule(new Strike(…), after: Skills.Windup(skill))`，不写 `after` 就是紧接着 | Server |
| 发给别的 Actor | `me.Send(new Player(id), new Defeated(map))` | Server |
| 等别的 Actor 的结果 | `drop.Ask(new Player(who.Id), new GrantItem(item), new Looting(reply), deadline: …)`，回信是 `Replied<GrantItem, Looting>`；不带状态时是 `Replied<GrantItem>` | Server |
| 测试一个 Actor | `new TestActor<Map>(new Map(7), Registrations.Create())`；多个节点用 `TestCluster` | 测试 |
| 部署 | `cluster.AddNodes<MapServer>("map", replicas: 4)` | `Game.Server/apphost.cs`（第 2 节） |

### 框架给的

核心：

| 名字 | 是什么 |
|---|---|
| `IActor<T>` | Actor 类型的接口：组件、值索引、`IEquatable<T>`、EasyEcs 的系统组，带组过滤 `In<T>`；静态的 `RootPrefab`（第 3 节） |
| `IChildKey<T>` | 子实体唯一值索引的键：`IComponent, IUniqueIndexed<T>, IEquatable<T>`（第 3 节） |
| `Connection` | 内置的 Actor 类型：每个客户端连接，在接受客户端的服务器上有一个实体，键是 `ClientId`（第 7 节） |
| `Entity` | 唯一的句柄。ECS 的成员来自 EasyEcs；发送、推送、加入、`Root` 等来自 DaServer（第 5 节） |
| `World` | 只做 ECS：找实体、读写组件、值索引。一个 world 里可能有很多个 Actor（第 2 节） |
| `ActorId` | 任意 Actor 的地址（Actor 类型的 `TypeId` 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键。只在服务端 |
| `TypeId<T>.Value` | 类型的 id（Nino 类型 id）。Actor 类型、消息、事件、组件都用这一个名字 |
| `Sender<K>` | 处理方法的参数：这条消息的发送方 |
| `[To]` | 客户端消息的参数：目标 Actor 的键（第 4 节） |
| `PresenceId`、`Presence`、`Joined`、`Leaving`、`Detached` | 客户端在某个 Actor 上的 Presence：Actor 里的一个子实体，例如地图上的 Avatar。`Joined(bool MissedPushes)` 是确认后的组件，快照系统按 `[Changed<Joined>]` 反应，`MissedPushes` 为假时跳过；`Detached` 是断线后宽限期里的 tag（第 7 节） |
| `Subscribed` | Presence 订阅的 `ClientGroup`：`DynamicBuffer<Subscribed>`（第 9 节） |
| `In<K>` | tag：这个实体属于一个 K 类型的 Actor。给系统组过滤用（第 5 节） |
| `ReplyTo<T>`、`Replied<TReq>`、`Replied<TReq, TState>`、`CallId` | 回复令牌（普通数据）；回信：`a.TryGetValue(out var v)` 取回复的值，`a.Status` 只说明失败的原因，`a.State` 是发问时存下的状态；调用 id 是幂等键（第 8 节） |
| `[Durable]` | 写在 `IRequestTo` 上，两端由框架负责：发问方的等待实体随记录保存，重问到有结果或者到了 `deadline:`（`Expired`）；接收方按调用 id 去重，回复在记录写成之后才发出（第 8 节） |
| `Activated`、`Activation` | Actor 激活后的第一条邮件，用 `[On<Activated>]` 处理（第 6 节） |
| `Deactivating` | tag：Actor 停用前的最后一个 tick，系统按它反应（第 6 节） |
| `Time` | world 的单例：`Tick`、`DeltaTime`、`TickRate`、`Elapsed`（单调时间）、`Utc`（第 9 节） |
| `Scheduled<T>` | 本 Actor 事件的句柄，普通数据；`e.IsPending(s)`、`e.Cancel(s)` |
| `ClientGroup` | 一次序列化、按网关扇出的广播组；`root.Group(key[, every: n])` 得到本 Actor 的组，`every` 是每 n 个 tick 发一次（第 9 节） |
| `Status` | 请求的结果和拒绝原因；两个分类 `s.NotExecuted`（确定没执行）和 `s.Transient`（值得重试）；`Busy` 带 `RetryAfter`（第 11 节） |
| `IPersisted` | 持久化的组件：`IComponent, ITrackChanges`（第 10 节） |
| `[TickRate(n)]` | 写在 Actor 类型上的帧率：它的系统组每秒跑几次（第 5 节） |
| `[ActorOptions(…)]` | Actor 类型的设置，都有默认值：`IdleTimeout`（秒，空闲多久停用，第 6 节）、`DetachGrace`（秒，断线的宽限期，第 7 节）、`MaxPending`（待处理项的上限，第 9 节）、`OnError`（处理方法抛出之后 `ErrorAction.Restart` 或 `Resume`；有持久化组件的 Actor 类型默认 `Restart`，其余默认 `Resume`，第 5 节） |
| `[Claims]`、`[JoinOnConnect<TClaims>]` | 身份；连上就加入的 Actor 类型（第 7 节） |
| `ICluster` | Actor 以外的入口（HTTP、后台、测试）：发服务器之间的消息，带期限或 `CancellationToken`、可选的调用 id；`Disconnect(clientId)`（第 4 节） |
| `Registration`、`TestActor<K>`、`TestCluster` | 生成的注册对象（实例，不是静态表）；一个 Actor 的测试台；进程内的多节点集群（第 12 节） |

进阶（用到时再看）：

| 名字 | 是什么 |
|---|---|
| `[Stateless]` | 不保存、不迁移的 Actor 类型，只做当场回复（第 3 节） |
| `[Transient]` | 不随迁移的组件（第 10 节） |
| `[WorldLocal]` | 写在单例类型上：world 自己的缓存或配置，不是 Actor 的状态，不迁移、不保存；只有带它的用户单例能作为 `ref` 参数（第 5 节） |
| `[ActivatedBy<T>]` | 只有 T 能激活这个 Actor 类型（第 6 节） |
| `Incoming`、`Verdict` | `[Handles<Connection>]` 里每条客户端消息转发前的过滤（第 7 节） |
| `Takeover` | `[JoinOnConnect]` 上谁胜出：`NewestWins`（默认）或 `Reject`（第 7 节） |
| `PresenceLost` | `Connection` 上的通知：这个客户端的一个 Presence 没了（第 7 节） |
| `Restarting`、`Resumed`、`RestartingNodes` | 热重启：冻结前最后一个 tick 根上的 tag、加载后第一个 tick 的 tag（带实测的停顿，或者 `Aborted`）；列出正在热重启的节点和预计停顿的节点单例（第 7、10 节） |

用户不写、但在文档、指标和诊断里会看到的名字（框架内部的数据和导出的指标在 [architecture.md 第 2 节](architecture.md#runtime-data)）：
- 客户端 SDK 的：槽号、`Ready`、`JoinConfirmed(槽号, 调用 id)`、`PresenceClosed(槽号)`、`PresenceLost(槽号)`、`HandedOff(槽号)`、`Superseded`、`Unavailable(retryAfter)`；
- 框架内部的：根上的 `Pending`（待处理的个数）、`HeldReply` 和 `HeldAsk`（等记录写成的 `[Durable]` 回复和请求）、`ReplyLog`（`[Durable]` 的调用 id 到回复）、`NextDue`（记录里最早要唤醒的时刻）；节点的 `StoreDegraded`、`ActivationSlots`、`PeerHealth`、`DueScan`；占位上的 `Loading`、`Saving`、`ActivationFailed`；控制邮件 `Route`、`Lost`、`TakeOver(连接戳)`、`Fence(键, epoch)`、`Wake`；`Mail`、`Due`、`Ordered<K>`、`Current`、`Outbox`（测试里解码成数据断言）、`Unsaved`、`Persisted`；成员状态 `NodeRestarting`、`Rejoining`。

### 几个词

后文先用后讲的词，各一句：
- **主体**：一条消息或一个事件作用的实体，处理方法的组件参数都绑定到它（第 5 节）。
- **有序阶段**（`OrderedPhase`）：每个 tick 开头，把邮件和到期项按 `(时间, 序号)` 逐个执行的框架系统（第 1 节）。
- **即时链**：一项执行完，紧接着执行的不带 `after` 的 `Schedule`；不建实体，是 world 的链栈里的值（第 5 节）。
- **`Due`**：到期时刻；延后事件、请求期限、加入确认期限都是带 `Due` 的实体，运行时用自己的堆排序（第 9 节）。
- **`Outbox`**：一项发出的东西，追加时就按目标序列化进池化的帧，每个阶段结束时交给链路（第 5 节）。
- **Presence**：客户端在一个 Actor 上的存在，是这个 Actor 里的一个子实体（第 7 节）。
- **epoch**：激活时占的序号；写记录按它比较并交换，所以一个 Actor 只有一个激活能写（第 10 节）。
- **占位**：还没激活或正在写记录的 Actor 的根实体，邮件在它上面排队（第 6 节）。
- **收尾**（`Settling`）：tick 的第 c 步，框架的系统 `Depart`、`SyncSubscriptions`、去掉 `Resumed`（第 1 节）。
- **连接戳**：`(令牌签发序号, 网关节点代数, 节点上的连接计数)`，按字典序比较；更大的接管 Presence（第 7 节）。
- **`Retire`**：网关让一个连接先连上别的网关、再关掉自己（先连后断），用于网关升级、缩容和按负载再平衡（第 7 节）。
- **节点单例**：节点级的状态，例如 `RestartingNodes`。节点持有一份不可变的快照，有变化时整个换掉；每个 world 有它自己的一份同名单例，每批邮件和每个 tick 开始时从快照刷新，所以 world 的线程读它不加锁，同一项里读到的值不变（[architecture.md 第 2 节](architecture.md#2-runtime-structure)）。

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 唯一值索引（`IUniqueIndexed<T>`，4.7 计划；`world.MapIndex`） | 为 `IActor<T>`、`IChildKey<T>` 的 `partial record struct` 生成显式实现 `T IIndexed<T>.Key => this` | 收到消息时查索引 |
| 逻辑属于哪个 Actor 类型 | — | `[Handles<K>]` | 分发表按 Actor 类型建 |
| 事件的顺序 | 单例 | `Due` 组件 | 运行时系统 `OrderedPhase` 按 `(时间, 序号)` 归并邮箱快照、到期堆和即时链；状态在单例 `Ordered<K>` 里 |
| 消息处理 | 单实体 job（`entity.Run(ref job[, filter])`，4.5）、`[With<T>]`、`[Without<T>]`、`INoStructuralChanges` | `[On<T>]` 生成 job 结构 | 逐个调用，逐个隔离异常 |
| 参数绑定 | `[ForEach]` 的参数规则 | 组件参数绑定到主体；Actor 类型参数绑定到根 | job 拿到主体的组件 |
| 发出的东西 | 单例 | — | 单例 `Outbox`：追加时序列化进按目标分的池化帧，每个阶段结束时交给链路；当前项的上下文是单例 `Current` |
| 每 tick 的逻辑 | `EcsSystem`、`[ForEach]`、批量方法、`Parallel`、`[Changed<T>]`、`[UpdateAfter<T>]`、`ISystemGroup`、接口带的组过滤（`IGroupWith<T>`，4.5） | Actor 类型就是系统组，`[TickRate]`、`In<K>` | 有序阶段 → `world.Update<Map>()` → 框架的收尾系统 |
| 本 Actor 的事件 | `ChildOf` | `Schedule(ev, after:)`、`Scheduled<T>`、`IEvent`、`Due` | 到期堆、即时链；发起者销毁时一起消失 |
| 发送方向 | — | `[From<K>]` 生成发送方法，分析器按调用处的 Actor 类型检查 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Handles<K>]`、`[To]` 生成路由表 | `Connection` 读帧头直接转发给目标，不解码负载 |
| 等待、回复 | `ChildOf` | `Replied<TReq>`、`Replied<TReq, TState>`、`ReplyTo<T>`、`[Durable]` | 调用 id 到等待实体的哈希索引、`Due` 期限、剩余时间传递；`[Durable]` 的等待实体进记录、重问、`ReplyLog` 去重、`DueScan` 唤醒 |
| Presence | 子实体、值索引、清理组件（`ICleanupComponent`、`Destroyed`） | `[JoinOnConnect]`、`GetOrSpawn`、`Join()`、`Leaving`、`Detached`、`Joined(MissedPushes)` | 连上就加入、连接戳、写路由、确认、宽限、核对、释放路由（框架组 `Cleanup`） |
| 订阅 | `DynamicBuffer`、`[Changed<DynamicBuffer<T>>]` | `Subscribed`、`SetGroups` | 系统 `SyncSubscriptions` 同步网关扇出 |
| 生命周期 | tag | `[On<Activated>]`、对 `Leaving`、`Deactivating`、`Restarting`、`Resumed` 的系统 | 占位、激活 epoch、隔离、`Depart` |
| 一个 Actor 的实体 | `ChildOf`、`world.Children(map)`、prefab、带组件的 `world.Instantiate`（4.7 计划）、唯一值索引 | `In<K>`、`K.RootPrefab`、`root.Spawn(prefab, in a, in b)`、`root.GetOrSpawn(prefab, key, …)`、`e.Root` | 注册时的 prefab 副本 |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ChangedAny`/`RemovedAny`、`Mut<T>` 和 `SetIfChanged`、`[ForEach(Slices = N)]`（后三个 4.7 计划）、`ICleanupComponent`、`DynamicBuffer` | `IPersisted`、`[Durable]` | `ChangeSystem`、`CheckpointSystem`（带 `HeldReply` 的根先写） |
| 记录、迁移 | `ComponentSet`、`Descendants`、`SaveEntities`、`LoadEntitiesInto`（恢复时 `markWritten: false`） | `IPersisted`、`[Transient]` | 读写存储，epoch CAS，布局哈希 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热重启 | `world.Save`、`world.Load(…, markWritten: false)`（单例随 world 保存，系统不保存；加载的行不算变更，4.6） | `wire.manifest` 比对选路径、快照格式 | 两个 tick 之间冻结 world，写快照文件，新进程加载；不重新激活 |
| 测试、诊断 | 单个 world、`SystemErrorPolicy.Throw`、`Capture`/`Restore`、`world.Count<Destroyed>()`、`world.Describe()`、`world.DescribeSchedule()`、`SystemStats`（4.7 计划加上分配字节和匹配的实体数） | `Registration`、`TestActor<K>` | 手动时钟、解码后的 `Outbox`、分配断言 |
| 对象复用 | `DynamicBuffer` 池化、Nino 的 `[NinoPooled]` | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个 Actor 按 `(时间, 序号)` 逐条处理自己的消息和事件，Actor 之间并行。** `Activated` 也是邮件，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，同 Orleans：迁移那一两秒里，在途的消息可能后发先到。
   - 需要严格先后的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。**超时不等于没执行**：普通的请求结果未知时由发问方决定；`[Durable]` 的请求两端由框架负责，重问到有结果或者 `Expired`，期限内同一个调用 id 只落定一次（第 8 节）。每条路径的投递、顺序、去重、超时和重试由谁负责，见第 11 节的表。
4. **同一个 Actor 任一时刻最多一个激活能写记录**：激活先占 epoch，每次写都按 epoch 比较并交换；比较失败的激活被隔离（第 10 节）。
5. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。旧连接留下的 Presence 在宽限期内保留，新连接的连接戳更大，接管它（第 7 节）。
6. **每种失败的结果都是确定的**，见第 11 节。

### 执行模型：每个 Actor 是一个离散事件模拟

**Actor** 是 Actor 类型的一个实例，例如 7 号地图：它的根实体（带 Actor 类型组件 `Map(7)`）加上所有子孙，例如一张地图和地图上的所有实体。Actor 里发生的一切，都是排好序的事件：

| 事件 | 时间 | 例子 |
|---|---|---|
| 客户端或别的 Actor 发来的消息 | 进入本 world 的时刻 | 移动一步、放技能、交易 |
| 本地事件 `e.Schedule(ev)` | 紧接着当前处理方法 | 立即结算的技能 |
| 延后的事件 `e.Schedule(ev, after: 时间)` | 当前项的时间 + after，精确值，不取整 | 前摇 0.3 秒后的结算、5 秒后 buff 到期 |
| 框架的期限 | 到期时刻 | 请求超时、加入确认期限 |
| 系统 | 每个 tick，有序阶段之后 | 移动推进、AOI、对 `Leaving` 的反应 |

所有时刻都在本进程的单调时钟上（第 9 节）。

**排序只有一条：所有项按 `(时间, 序号)` 执行。**
- 时间：消息是进入本 world 的时刻，延后事件和期限是精确的到期时刻。序号是 world 的计数器，时间相同时按先后打破平局。
- **tick 窗口**：时间落在 (t − Δ, t] 的项，在时刻 t 那个 tick 的有序阶段里按 `(时间, 序号)` 执行（Δ 是帧长）。tick 只决定"什么时候执行"，不改变"谁先谁后"。
- 所以 0.29 秒到达的闪避排在 0.30 秒到期的结算前面，即使它们落在同一帧里。
- 每个项看到的，正好是它之前所有项的全部效果。

**时刻 t 的一个 tick：**

| 阶段 | 做什么 | 怎么跑 |
|---|---|---|
| a. 有序阶段 | 运行时系统 `OrderedPhase`：tick 窗口里的消息和到期项按 `(时间, 序号)` 归并（第 9 节） | 串行。每一项执行完，紧接着执行它的即时链（不带 `after` 的 `Schedule`），每项最多连锁 1,024 个；阶段结束时把 `Outbox` 交给链路 |
| b. 系统 | `world.Update<Map>()`：这个 Actor 类型的 EasyEcs 系统（第 5 节） | 单线程；`[ForEach(Parallel = true)]` 只在 world 比核少时分块多线程（第 5 节） |
| c. 收尾（`Settling`） | 框架的系统：`Depart` 销毁带 `Leaving` 的 Presence，`SyncSubscriptions` 同步订阅（第 7、9 节）；然后框架组 `Cleanup` 处理已销毁实体留下的清理组件（`ReleaseRoutes` 等，第 6 节）；最后把 `Outbox` 交给链路 | 单线程 |

- 有序阶段的状态（序号计数器、当前 tick 窗口、即时链预算）按 Actor 类型存在运行时的单例 `Ordered<K>` 里。有序阶段、收尾和清理都是 DaServer 自己的 `EcsSystem`，在框架的系统组里，每个只做一件事。
- 有序阶段开始时取一次邮箱快照；这期间新到的消息进下一帧。
- **一个 Actor 不能饿死同一 world 的邻居**：每批邮件里每个 Actor 最多执行一定数量的项（节点配置），其余留给下一批，仍按 `(时间, 序号)`。
- **先看租约**：每个 world 在有序阶段之前读一次租约到期戳（`volatile`，第 2 节），过期就不执行，整个 world 冻结。
- **没有 `[TickRate]` 的 Actor 类型**（例如 Player、Guild、`Connection`）不按 tick 走：消息到了就处理，延后事件到了时间就执行，顺序规则不变；它的系统组和收尾在每批邮件处理完后跑一次（`Time.DeltaTime` 为 0）。所以对数据的反应（`[With<Leaving>]`、`[Changed<T>]`）在哪种 Actor 类型上都一样写。
- 到期时要重新校验：施法者还活着吗？被眩晕了吗？目标还在、还够得着吗？过滤 `[Without<Dead>]` 就是这种校验（第 5 节）。

**结果：** 在第 k 帧执行的结算，看到的是第 k−1 帧的系统跑完后的位置，再加上时间比它早的所有消息和事件，包括同一帧里先到的移动。所以前摇期间走开，就是闪避。

**例子：同一帧里依次到达 [A 打 B，B 移动，A 移动，B 打 A]**

前摇为 0（`after` 是 `TimeSpan.Zero`）：
1. A 的 `Cast` 返回后，紧接着执行 `Strike`：读 B 还没动的位置，够得着就扣血；B 被打死则带上 `Dead`。
2. B 移动：`Move` 带 `[Without<Dead>]`，B 已死就不调用。
3. A 移动。
4. B 的 `Cast`：B 已死则不调用；否则紧接着结算，读 A 移动后的位置。

前摇 0.3 秒，30 Hz（9 帧）：
1. 第 k 帧：A 的 `Strike` 到期时刻 t_A + 0.3 秒；B 移动；A 移动；B 的 `Strike` 到期时刻 t_B + 0.3 秒，比 A 的晚。两个都带着 `Due` 进到期堆。
2. 第 k+1 到 k+8 帧：B 继续发 `MoveTo` 走开，系统推进 NPC。
3. 到期的那一帧，有序阶段按时间执行：A 的 `Strike`、B 的 `Strike`，以及这一帧里时间夹在它们之间的消息（例如 B 在 A 到期前一刻发来的移动）。A 的结算读 B 此刻的位置：已经走出范围，就是闪避。如果 A 打死了 B，B 的 `Strike` 带 `[Without<Dead>]`，不执行。

**并行从哪里来：**

| 来源 | 做法 |
|---|---|
| 很多个 Actor | 节点里每个工作线程跑一个 world，一个 world 里有很多张地图；节点有好几个 world，集群有很多节点。TrinityCore 的 MapUpdater、EVE 的星系、LoL 和 Valorant 的对局都是这样 |
| 系统 | 一次 `world.Update<Map>()` 覆盖这个 world 上这个 Actor 类型的所有 Actor；节点的 world 比核少时（例如一个节点只跑一张超大地图），重活的方法写 `Parallel = true` |

- Actor 内的有序阶段永远串行。没有上市的游戏把一个模拟步按空间切开并行：那需要框架懂位置、作用距离、最大速度这些游戏概念（违反 ADR-045），还会加一帧延迟，语义也脆弱。
- 以后再考虑：
  - 一个超大的 Actor，只有在基准测试显示它的有序阶段超出帧预算时，才提供用户自己给出分区的可选方案；
  - 无缝大世界用 New World 的做法：世界按格子分给进程，人移动时交接。

**不采用的：**
- 乐观执行（Time Warp 回滚）：保存状态或写反向计算的开销和一个约 1 µs 的事件本身差不多，而且回滚前已经推给客户端的结果收不回来。
- 按消息类型分批：会打乱 `(时间, 序号)`。只有系统按列批量处理。
- 把命令排到下一帧统一执行：等于把效果挪后，会出现"发包时打得到、执行时人跑了"。

**用户写的只有两种**：对消息和事件的反应（`[On<T>]` 方法，EasyEcs 的单实体 job），以及系统（`[UpdateInGroup<K>]` 的 `EcsSystem`）。例子见第 12 节：`Maps` 加入地图，`Moves` 移动，`Combat` 施法和结算，`Movement` 等系统每 tick 推进。

用户不判断"立即还是延后"（`after` 来自数据，0 也合法），也不判断"每条消息还是每 tick"（反应写 `[On<T>]`，每 tick 和对数据的反应写系统）。

**用户能看到的反馈：**
- 每个 Actor 的指标：每个 tick 有序阶段的耗时，由运行时自己按 Actor 测量；
- 每个系统的耗时、分配和匹配的实体数：EasyEcs 的 `MeasureSystems` 和 `system.Stats`（后两项 4.7 计划），导出为指标。它按 world 统计每个系统，一次 `world.Update<Map>()` 覆盖这个 world 上所有的 Map，所以是这些 Actor 合在一起的值，不分 Actor；
- `world.Count<Destroyed>()` 导出为指标：已销毁、清理组件还没处理完的实体数。一直涨，说明有清理系统没跑或者漏了（第 6 节）；
- 只读的查看端点：一个 Actor 的子树、最忙的 Actor、每个 world 的表和系统、按组件计数（只按请求运行，会分配；[architecture.md 第 8 节](architecture.md#inspector)）；
- 并行和串行结果一致的核对，是一个测试（第 12 节的 `TestActor<K>`）。

---

## 2. 服务器与节点

```csharp
// Game.Server
[Hosts<Player>, Hosts<Guild>] public readonly struct GameServer    : IServer;
[Hosts<Map>]                  public readonly struct MapServer     : IServer;
[Hosts<Connection>]           public readonly struct GatewayServer : IServer;   // hosting Connection = accepting clients
```

- **一个工程可以定义多种服务器类型；节点 = 一个进程 = 一种服务器。**
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程只跑一种服务器。
  - 本地开发由 Aspire 起多个进程；测试用 `TestCluster` 和 `TestActor<K>`。
  - 小规模部署可以写一个 `[Hosts<Player>, Hosts<Map>, Hosts<Connection>] AllInOne`。
- **`[Hosts<Connection>]` 就是"接受客户端"。**
- **Actor 类型绑定的是服务器类型，不是节点：**

  | 关系 | 数量 | 由谁决定 |
  |---|---|---|
  | Actor 类型 → 服务器类型 | 一般一对一；`AllInOne` 时一对多 | 服务器上的 `[Hosts<…>]` |
  | 服务器类型 → 节点 | 一对多 | AppHost 部署几个 |
  | Actor 类型 → Actor | 每个键一个 | 第一条消息到达时激活 |
  | Actor → 节点 | **任一时刻恰好一个能写** | 在承载该 Actor 类型的节点中按键放置，节点增减时迁移 |

  - 同一个键任一时刻只有一个 Actor：放置决定它在哪，激活 epoch 保证分区和停顿之后也只有一个能写（第 10 节）。
  - `Connection` 是例外，它固定在连接所在的节点（第 7 节）。
- **协调者**：最老的、Up 且不在 `NodeRestarting` 的成员（ADR-079）。它分配节点代数，发热重启的许可（全集群同时只有一张，写在视图里），定热重启的期限（第 10 节）。继任的协调者接手时，正在进行的热重启的期限从头算。
- **节点代数**：代数 = max(这个节点名见过的最大代数 + 1, 当前的 Unix 毫秒数)，由协调者分配，随成员视图传播。所以整个集群冷启动、谁也不记得见过的代数之后，代数也不会重复。同名、代数更大的节点加入时，旧代数立刻算作 `Left`（例如热重启超过期限后迟到的进程）。热重启不算重新加入：代数不变（第 10 节）。
- **组成集群**：只有序号最小的种子在等过种子等待时间、还找不到别人时，自己组成集群；其他节点只加入。成员数达到下限或者组成窗口过去之前，不激活任何 Actor。所以并行启动的种子只组成一个集群。
- **成员租约**：节点只在收到协调者的确认、并且确认回显了它自己的心跳计数时续租，租期从发出那次心跳时算起。协调者被判 `Suspect` 后，次老的成员开始确认，所以协调者宕机不会让所有人的租约过期。每个 world 在有序阶段之前查租约到期戳（第 1 节），过期就冻结：不再执行、不再写。单向分区里听不到协调者的节点因此自己停下。其他节点在 max(`DeadAfter`, `SuspectAfter` + 租约) 之后才重新放置它的 Actor，所以重新放置时旧节点一定已经停下（第 10 节的激活 epoch 是第二道防线）。各个定时的默认值见第 11 节。
- **world**：节点里有几个工作线程，每个线程跑一个 EasyEcs world；节点承载的所有 Actor 类型共用它，Actor 落在哪个 world 由键的哈希决定。world 的个数由节点按分到的 CPU 自己定（`DaServer:Worlds` 可以覆盖），用户代码和 AppHost 都不写它；框架内部把"一个线程加一个 world"叫 shard。
- **帧率**写在 Actor 类型上（第 5 节），不在配置里。
- **Program.cs**：大约五行，把生成的 `Registrations.Create()`（第 12 节）交给节点，并显式声明三种服务管理器，各自不在对应的服务管理器下就什么也不做，所以同一个二进制到处能跑（[deployment.md 第 0 节](deployment.md#0-launch-flows)）：
  ```csharp
  var builder = WebApplication.CreateSlimBuilder(args);
  builder.Host.UseSystemd().UseWindowsService().UseLaunchd();   // the official two and DaServer's macOS counterpart
  builder.AddDaServer(Registrations.Create());
  var app = builder.Build();
  app.MapDaServer(); app.Run();
  ```
  没有静态的注册表；`AddDaServer` 不替用户调用服务管理器。
- **AppHost**：部署写在 `Game.Server/apphost.cs`，一个 Aspire 的单文件 AppHost：
  ```csharp
  // Game.Server/apphost.cs
  #:sdk Aspire.AppHost.Sdk@13.6.1
  #:package DaServer.Aspire@<version>
  #:package Aspire.Hosting.PostgreSQL@13.6.1
  #:project Game.Server.csproj
  using Game.Server;                                                       // server types generated from Game.Server's wire.manifest

  var builder = DistributedApplication.CreateBuilder(args);
  var actors = builder.AddPostgres("postgres").AddDatabase("actors");
  var cluster = builder.AddDaServerCluster("mmo");                         // also creates the cluster's signing secret
  cluster.AddNodes<GameServer>("game", replicas: 3).WithActorStore(actors);
  cluster.AddNodes<MapServer>("map", replicas: 4).WithActorStore(actors);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientConnections(port: 7100);
  builder.Build().Run();
  ```
  - 运行：`dotnet run --file apphost.cs`（或 `aspire run`）；发布：`aspire publish`。在这个目录里直接 `dotnet run` 跑的是节点本身。本地、AOT、Compose、Kubernetes 和裸机的启动流程见 [deployment.md 第 0 节](deployment.md#0-launch-flows)。
  - `DaServer` 包的构建属性把 `apphost.cs` 移出服务端工程的编译（`<Compile Remove="apphost.cs" />`），用户不用写。
  - **AppHost 不引用服务端程序集**：`#:project` 只给 Aspire 工程元数据（`Projects.Game_Server`）。DaServer.Aspire 的生成器读这个工程提交的 `wire.manifest`，只读 `actor` 和 `hosted-by`（第 10 节），在 AppHost 里生成同名的服务器类型（同样是 `public readonly struct MapServer : IServer`），每个都绑定到这个工程：`AddNodes<MapServer>` 起的是 Game.Server 的二进制，节点跑 `MapServer`。
  - **只有一个 `IServer`**（DaServer.Core）：服务端的服务器类型和 AppHost 里生成的同名类型都实现它。`AddNodes<T>(string name, int replicas) where T : struct, IServer` 返回 `DaServerNodeGroup<T>`。
  - **两个名字各管一件事**：`name` 是 Aspire 的资源名，也是 Pod 名和节点名的前缀（`map-0`、`map-1`）；服务器类型的身份是它的 `FullName`（`Game.Server.MapServer`），节点启动时按它和 `wire.manifest` 对上。集群名和组名都是资源名，不能重名：集群叫 `mmo`，组叫 `game`、`map`、`gate`。
  - **记录存储**：`WithActorStore(actors)` 给承载有状态 Actor 的组接上 DaServer.Persistence.PostgreSql；只承载 `Connection` 和 `[Stateless]` 的组不用。签名密钥由 `AddDaServerCluster` 生成（参数 `{cluster}-secret`），用户不写。
  - **端口按节点组在 AppHost 里显式声明**（端口的规则只写在这里）。一个组的所有副本监听同样的端口（Kubernetes 上是同一个模板，各 Pod 有自己的 IP）：
    - 集群端口和 HTTP 端口（健康检查、管理端点）由 DaServer.Aspire 分配，只在集群内部可达；
    - 对外的只有 AppHost 写出来的：`WithClientConnections(port)` 是客户端的 TCP / WebSocket 端口（客户端传输只有这两种，适用范围见 ADR-092），默认在一个负载均衡的地址后面（负载均衡器的种类是应用和平台的事）；对外的 HTTP（例如 webhook）用 Aspire 自己的 `WithExternalHttpEndpoints()`；
    - 客户端程序（例如压测机器人）用 `WithClientEndpoints(gateways)` 拿到网关的地址；
    - 不做：每个副本单独的公网地址（Agones 那样的直连），以后由一个直连特性加上。
  - **客户端端口和 `[Hosts<Connection>]` 互相对上**：承载 `Connection` 的服务器，生成的类型还实现 `IAcceptsClients`；`WithClientConnections(port)` 是 `DaServerNodeGroup<T>` 上 `where T : IAcceptsClients` 的扩展方法，给别的服务器开客户端端口编译不过。反过来，承载 `Connection` 的服务器在 AppHost 里没写 `WithClientConnections`，构建报错。消息引用的 Actor 类型没有节点承载时，构建也报错。
  - 节点启动时，配置的服务器类型不存在，或者和 `wire.manifest` 对不上，启动失败。
  - **热重启命令**是 `{cluster}-restart`（第 10 节）：本地运行时是 Aspire 仪表盘里资源上的命令；发布之后是 `aspire do mmo-restart`。
  - 限制：Visual Studio 还打不开单文件 AppHost（VS Code、Rider 和命令行可以）；想要的团队照样可以另建一个 AppHost 工程，代码相同。

---

## 3. Actor 类型与地址

**Actor 类型就是它的地址，也是根实体上的组件，也是它的系统组。**

```csharp
// Game.Server
[JoinOnConnect<Account>] public readonly partial record struct Player(long Id) : IActor<Player>;   // §7
public readonly partial record struct Guild(long Id) : IActor<Guild>;
[TickRate(30)] public readonly partial record struct Map(long Id) : IActor<Map>   // 30 ticks a second
{
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default);   // the root's own components
}
public readonly partial record struct Room(long Id)  : IActor<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IActor<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个 Actor 的地址。消息和组件里存别的 Actor 的地址，直接用 `Map`、`Player` 类型的字段。要存"任意 Actor 的地址"时用 `ActorId`。地址只在服务端：客户端看不到 `ActorId`、`PresenceId`、`EntityId`，也看不到节点（第 4 节）。

**同一个值也是组件**：地图的根实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 处理方法里知道自己是谁：取 Actor 类型参数 `in Map map`，它总是绑定到当前项所在 Actor 的根，不论主体是根还是子实体（第 5 节）。

**接口**：`IActor<T>` 继承 `IComponent`、`IUniqueIndexed<T>`、`IEquatable<T>` 和 `ISystemGroup`，并带着组过滤 `In<T>`；子实体的键用 `IChildKey<T>`。键都是唯一值索引（EasyEcs 4.7 计划的 `IUniqueIndexed<T>`）：一个 world 里两个同键的根、两个同 `NetId` 的子实体都是错误，写入时抛出（`TrySet` 返回 false），`Get(key)` 直接取：

```csharp
public interface IActor<TSelf> : IComponent, IUniqueIndexed<TSelf>, IEquatable<TSelf>, ISystemGroup, IGroupWith<In<TSelf>>
    where TSelf : struct, IActor<TSelf>
{
    static virtual Prefab RootPrefab => Prefab.Empty;   // an actor type with root components declares its own
}
public interface IChildKey<TSelf> : IComponent, IUniqueIndexed<TSelf>, IEquatable<TSelf> where TSelf : struct, IChildKey<TSelf>;
```

- `IEquatable<TSelf>` 是继承的接口，不是约束：值索引要求 `T : IEquatable<T>`，写成约束的话，每个用到 `IActor<T>` 的泛型都要重复这条约束，否则 CS0314。record struct 本来就实现它。
- **根的 prefab 是 Actor 类型上的静态属性** `public static Prefab RootPrefab { get; } = …`：`Registration` 按 `K.RootPrefab` 读它，建占位和根都用它的副本（第 6 节）。没有根组件的 Actor 类型不写。叫 `RootPrefab` 不叫 `Root`：`e.Root` 是实体所在 Actor 的根实体（第 5 节）。

- EasyEcs 认源码里声明的接口，包括继承来的：它的生成器和分析器都用 `AllInterfaces` 判断（`Symbols.Implements`）。所以：
  - `[UpdateInGroup<TGroup>]` 的约束是 `struct, ISystemGroup`，C# 认继承来的接口；
  - EasyEcs 的分析器判断"是不是系统组"时，EECS023 不会误报。
  - EasyEcs 看不到的，只有别的生成器补上的接口和特性。所以要让 EasyEcs 看见的东西，都由用户写的接口或特性带着（同第 10 节的 `IPersisted`）。
- **组过滤由接口带上（EasyEcs 4.5 的 `IGroupWith<T>`）**：EasyEcs 从组类型实现的接口上读组过滤，`IActor<TSelf>` 带着 `In<TSelf>`，所以 `[UpdateInGroup<Map>]` 的每个 `[ForEach]` 方法都只看 Map 的实体，用户不写 `[With<In<Map>>]`。不能写成接口上的特性 `[With<In<TSelf>>]`：C# 不允许特性的类型实参用类型形参（CS8968）。
- **`Key` 由生成器补上，写成显式接口实现** `Map IIndexed<Map>.Key => this;`（假定 `IUniqueIndexed<T>` 沿用 `IIndexed<T>` 的 `Key`，W1 验证）：`IActor<T>` 和 `IChildKey<T>` 的 `partial record struct` 都一样，Actor 类型、`Avatar`、`Cell`、`NetId` 都是。用户不写 `Key`。不能写成公有属性 `public Map Key => this;`：record 合成的 `ToString()` 会打印 `Key`，`Key` 又是自己，无限递归到栈溢出。
- EasyEcs 生成的 `HasX` 是属性：写 `e.HasCasting`，不是 `e.HasCasting()`。
- Actor 类型的 id 是 `TypeId<Map>.Value`，没有单独的常量。
- `[TickRate]` 的规则在第 5 节。

**子实体的值索引，键里带上 Actor 的键**：一个 world 里有很多 Actor，所以子实体的键必须带着它所在的 Actor，否则两张地图的同名键会撞。写法只有一种：实现 `IChildKey<T>`，第一个字段是 Actor 类型（分析器检查），例如 `NetId(Map Map, int Value) : IChildKey<NetId>`、`Avatar(Map Map, Player Player)`、`Cell(Map Map, int X, int Y)`。Actor 的键不随迁移和恢复改变，不用改写；不用根的 `EntityId` 作键。框架的 `PresenceId`（全 world 唯一的随机值）除外。

**只有运行时能写 Actor 类型组件：**
- 分析器禁止用户代码对 Actor 类型组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时建占位时，唯一索引保证这个键在 world 里还不存在（重复就抛出，是运行时的错误）。占位就是以后的根（第 6 节），键从建占位起一直在它上面，不搬动。
- 运行时按键找实体用 `world.Index<K, K>()`：泛型的 `Actor<K>` 自己取一次、按 world 缓存，生成的注册代码里没有查找委托（第 12 节）。

**键**：
- Actor 类型的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[ActorOptions]`**：Actor 类型可调的设置都在这一个特性里（`IdleTimeout`、`DetachGrace`、`MaxPending`、`OnError`），各自的规则在引用的那一节，不写就是默认值。`[TickRate]` 和 `[Stateless]` 不放进去：帧率定义了这个类型的系统组多久跑一次，`[Stateless]` 改变的是这个类型是什么（不保存、不迁移、每个节点都能有，分析器据此禁止一些操作），都不是调参。
- **`[Stateless]` Actor 类型**：不保存，不迁移；每个节点可以有自己的 Actor，键只用来选 Actor。它的地址不含节点，回信、确认和到期都找不回同一个实例，所以分析器禁止在它上面 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。它只做"收到请求，当场回复"。
- **子实体不可寻址**，例如 Avatar、等待中的请求、延后的事件。它们跟根一起保存、一起迁移。
- **没有"无键 Actor"**：
  - 每个 world 一份的缓存和配置，用 `[WorldLocal]` 单例（第 5 节）；
  - 无状态的工作，用 `[Stateless]`；
  - 全局服务，用固定键，例如 `Matchmaker(int Region)`。

---

## 4. 消息

### 客户端收发的消息（Protocol）

```csharp
// Game.Protocol: data only, no actor types, no addresses
[NinoType] public record struct EnterMap([To] long Map)    : IFromClient<bool>;   // a request to the map whose key is Map
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
```

- `IFromClient` 是客户端发的单向消息，`IFromClient<TReply>` 是客户端发的请求，`IToClient` 是推给客户端的消息。
- 含集合的消息是 `[NinoPooled]` 类（Nino 4 的规则），发送后自动归还，不会每次推送都分配。
- **消息里不写 Actor 类型**，所以客户端不知道服务端有 Player、Map、Room。
- **客户端看不到节点和地址**：
  - `PresenceId`、`ActorId`、`EntityId` 不出现在 Protocol 里，也不随框架通知发给客户端；
  - 客户端知道的 Presence 只是一个**槽号**（第 7 节）；
  - 客户端看得到的实体编号是用户自己的组件，例如 `NetId`（第 12 节）。
- **处理它的 Actor 类型由服务端决定**：处理它的 `[On<T>]` 在哪个 `[Handles<K>]` 类里，消息就发给哪个 Actor 类型。
  - 生成器据此生成客户端路由表；
  - 一条客户端消息只能有一个处理 Actor 类型。
- **哪一个 Actor**：
  - 带 `[To]` 的消息，发给键等于这个参数的 Actor，例如 `EnterMap(7)` 发给 `Map(7)`。客户端只写了一个数，不知道它是哪种 Actor 的键；
  - 不带 `[To]` 的消息，发给这个客户端在该 Actor 类型上当前的 Presence（第 7 节）；
  - `[To]` 写在 record struct 的位置参数上（`ToAttribute` 的目标是参数，生成器读位置参数），最多一个，类型必须和处理 Actor 类型的键形状一致（`Map(long Id)` 对 `long`；多字段的键对同样字段的结构），分析器检查。`[Handles<Connection>]` 处理的消息不能有 `[To]`。
- **客户端消息直接到目标**：网关上的 `Connection` 实体过滤后直接转发给目标 Actor，中间不经过 Player（第 7 节）。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Map>, Durable] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // lands once, both ends survive a crash
[NinoType, From<Map>] public record struct Defeated(Map Where) : IMessageTo<Player>;
```

- **接收方写在接口上**：`IMessageTo<TTo>` 是单向消息，`IRequestTo<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。客户端只能发 `IFromClient`。
- **`[Durable]`**：写在 `IRequestTo` 上，发问方和接收方都由框架扛住宕机（第 8 节）。
- **消息里不写发送方的键**：接收方取 `Sender<K> sender` 参数，`sender.Key` 就是发送方。
- **方向由生成的方法和分析器保证**：
  - 每条消息生成 `Entity` 上的 `Send`/`Ask` 方法，参数类型是接收方的 Actor 类型，所以接收方写错编译不过；
  - `Entity` 不带 Actor 类型，所以发送方由调用处决定：`[Handles<K>]` 类和 `[UpdateInGroup<K>]` 系统里，K 不在消息的 `[From<…>]` 里，分析器报错；在别处（帮助类）调用时，Debug 构建沿 `ChildOf` 走到根、断言它的 Actor 类型，Release 不做这一步。
  - 这是帮助写对的手段，不是安全边界。安全边界在网关：只转发 `IFromClient`。
- **发送方是哪个 Actor**：发送实体所属的 Actor（`e.Root`），见第 5 节的操作表。
- **客户端上下文自动带上**：处理客户端消息、`PresenceLost` 时，发出的请求信封里带着这个客户端的上下文（`ClientId`、所在节点、连接戳、身份、客户端的调用 id），和期限一样。接收方在处理这个请求时可以 `Join()`（第 7 节）。
  - 用户不接触上下文本身；它在运行时的单例 `Current` 里（第 5 节）。系统里、`Activated` 里和定时事件里没有客户端上下文，从那里发出的请求也不带：接收方的 `Join()` 得到 `NoClient`（第 7 节），不是默认值。
- **`ICluster`**（HTTP、后台、测试）不属于任何 Actor，可以发任何服务器之间的消息，回复回到调用方：`await cluster.Ask(to, msg, deadline, call: CallId.From(externalKey))`，期限也可以换成 `CancellationToken`。
  - 期限作为预算发出（第 9 节）；取消只停止等待，已经发出的请求照常执行，结果未知，同超时；
  - `CallId.From(externalKey)` 由外部的幂等键（例如 webhook 的投递 id）算出确定的调用 id，外部重试同一个投递时调用 id 相同：`[Durable]` 的请求由框架去重，其余由处理方法按 `reply.Call` 去重（第 8 节）；
  - 节点热重启期间，它的 HTTP 平面不可用（进程已经退出、还没起来），外部调用方按自己的重试处理；
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Connection` 的节点，由连着它的那个处理。

### 本地事件（Server）

```csharp
public record struct Strike(int Skill, EntityId Target) : IEvent;
```

- 本地事件只在一个 Actor 里传：`e.Schedule(ev)` 发给本 Actor 的一个实体，由同一个 Actor 类型的 `[On<T>]` 处理。
- 延后的事件随 Actor 迁移，序列化规则见第 10 节。
- 影响别的 Actor 用消息（`e.Send(to, msg)`）。`Schedule` 只收 `IEvent`，`Send` 只收服务器之间的消息，类型约束就分开了它们，不用分析器。

---

## 5. 逻辑

逻辑只有两种写法（例子见第 12 节的 `Moves`、`Combat` 和 `Movement`）：
- 对消息和事件的反应：`[Handles<K>]` 的 `static partial class` 里的 `[On<T>]` 方法；
- 每 tick 的逻辑和对数据的反应：`[UpdateInGroup<K>]` 的 EasyEcs 系统。

- `[Handles<K>]` 写一次，类里所有方法都属于 Actor 类型 K：客户端消息发给 K。
- 一个 Actor 类型可以有多个类，例如 `Maps`、`Moves`、`Combat`。

### `[On<T>]` 的 T

| T | 是什么 | 例子 |
|---|---|---|
| 服务器之间的消息 | 消息的 `IMessageTo<K>`、`IRequestTo<K, R>` 必须是类上的 K | `[On<GrantItem>]` |
| 客户端消息 | 发给类上的 K | `[On<EnterMap>]`、`[On<MoveTo>]` |
| `Incoming` | 只在 `[Handles<Connection>]` 里：每条客户端消息转发前的过滤（第 7 节） | `[On<Incoming>]` |
| `PresenceLost` | 只在 `[Handles<Connection>]` 里：这个客户端的一个 Presence 没了（第 7 节） | `[On<PresenceLost>]` |
| `Activated` | 激活后的第一条邮件（第 6 节） | `[On<Activated>]` |
| 回信 | 等别的 Actor 的结果（第 8 节） | `[On<Replied<GrantItem, Looting>>]`、`[On<Replied<GrantItem>>]` |
| 本地事件 | `IEvent`，由 `Schedule` 发起 | `[On<Strike>]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个处理 Actor 类型。
- 离开、确认、断线、停用、热重启不是消息，是 tag 和组件：`Leaving`、`Joined`、`Detached`、`Deactivating`、`Restarting`、`Resumed` 由系统反应（第 6、7、10 节）。`[On<T>]` 写这些 tag，分析器报错，修复是"写一个 `[ForEach, With<T>]` 的系统"。

### 处理方法是 EasyEcs 的单实体 job

生成器把每个 `[On<T>]` 方法编成一个 job 结构，和 EasyEcs 为 `[ForEach]` 方法生成的一样（`IForEachEntity<T1..Tn>`）。运行时用 `subject.Run<T1..Tn, TJob>(ref job)` 在主体上执行一次，方法有过滤时用 `subject.Run<T1..Tn, TJob>(ref job, in Filter)`，`Filter` 是 job 上的静态字段（EasyEcs 4.5；基准是同样工作手写代码的 0.69–1.06 倍，0 分配）。所以处理方法的参数规则就是 EasyEcs `[ForEach]` 的规则，只有一套：
- **`ref` 参数直接指向存储**。方法体里改了主体的结构（加 tag、建实体、`Ask` 等），`ref` 参数照样可写：EasyEcs 在返回后把写入搬到新行；`in` 参数保持改之前的值（`guide/refs-and-await.mdx` 的 "Inside passes"）。job 只声明它写的组件（`Writes`），追踪变更的组件只在被写时打戳。
- **过滤**：`[With<T>]`、`[Without<T>]` 写在方法上，和 `[ForEach]` 一样。生成器把它编成 job 上的 `static readonly QueryFilter Filter`，建一次；没有过滤的方法不带 filter 调用 `Run`，不在每次调用时 `QueryFilter.Create()`。主体缺少组件参数或不满足过滤时不调用：请求以 `Skipped` 失败，单向消息和事件跳过（计数）。
- **组件参数都是必有的**：`IForEachEntity` 只收必有的组件，所以没有 `Maybe<T>` 参数。可能没有的数据在方法体里用 `TryGet<T>` 或生成的 `TryGetX(out …)` 读。
- **`INoStructuralChanges`**：`Entity` 和 `World` 参数只用来读、查索引，或者只交给不改结构的操作（`Send`、`Push`、`Reply`、`Broadcast`、`Root`）时，生成的 job 带上它，推断规则同 EasyEcs（`guide/structural-changes.mdx` 的 "Passes that freeze the structure"）：生成器看参数的用法，不看有没有这个参数。`Ask`、`Join`、`Leave`、`Spawn`、`GetOrSpawn`、`Deactivate`、带 `after` 的 `Schedule`，以及对任何实体的 `Set`/`Remove`/`Destroy`，算结构变更。带它的 job 运行时不做逐行记账，结构被冻结，违约就抛出。
- **检查**：EasyEcs 的分析器只检查 `[ForEach]` 方法，不认识 `[On<T>]`。DaServer 的分析器对 `[On<T>]` 方法套用同样的检查（参数、过滤、有索引的组件、结构变更），见第 13 节。
- 消息、发送方、回复令牌、身份、Actor 类型、单例是 job 的字段，由分发表在调用前填好。
- 不复制，不写回，也就没有别名问题：`target.Health` 和 `ref Health` 参数是同一个主体时，指向同一份存储，和 EasyEcs 的系统一样。
- 方法体里自己取的 `ref`（`target.Health`）不能跨过这个实体的结构变更使用（DAS022）。

### 一个绑定规则：参数绑定到主体

每条消息、每个事件都有一个**主体**实体。`Entity` 参数就是主体，组件参数（`in`/`ref`）和过滤总是针对主体。

| 进来的是 | 主体 |
|---|---|
| 带 `[To]` 的客户端消息 | 目标 Actor 的根实体，不论这个客户端是否已经在上面有 Presence |
| 不带 `[To]` 的客户端消息，处理 Actor 类型不是 `Connection` | 这个客户端在目标上的 Presence 实体，例如地图上它的 Avatar、Player 上它的 Presence |
| 客户端消息，`[Handles<Connection>]` 处理 | `Connection` 的根实体 |
| `Incoming`、`PresenceLost` | `Connection` 的根实体 |
| 本地事件 | 它被发给的实体：`caster.Schedule(new Strike(…), after: …)` 的 caster |
| 服务器之间的消息、`Activated` | 接收 Actor 的根实体，例如地图 |
| `Replied` | 发问的实体（第 8 节） |

- **两个例外，都不看主体**：
  - **Actor 类型参数**（例如 `in Map map`）总是绑定到当前项所在 Actor 的根上的键，主体是子实体也一样。这是知道"自己是谁"的唯一写法；它不会因为主体是子实体而 `Skipped`。
  - **身份参数**：`[Claims]` 类型的参数（例如 `in Account who`）绑定到发这条客户端消息的客户端的身份。身份随信封带着（不超过 16 字节）。只能用在客户端消息的处理方法里（分析器检查）。
- 主体在表里一定是根时（带 `[To]` 的客户端消息、服务器之间的消息、`Activated`），`Presence`、`Joined` 这类只在 Presence 上的组件参数和过滤永远不满足，分析器报错。

**别的实体在方法体里显式读写：**

```csharp
if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
ref var hp = ref target.Health;
```

- 首选的写法：发送方把接收方需要的数据放进消息，接收方少读别的实体。
- 用 EasyEcs 生成的成员：tag 是 `me.IsDead`，组件是 `target.Health`，可能没有的组件用 `TryGetX(out …)`，主体上的也一样。
- 对主体的条件写成过滤（`[Without<Dead>]`），不写成方法体里的 `if (me.IsDead) return;`。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Entity` | 主体 |
| 组件 `in`/`ref`/`Mut<T>` | 主体上的组件。缺少时不调用（上面的过滤）。`Mut<T>`（EasyEcs 4.7 计划）读时不打戳，`Write()` 打戳，`SetIfChanged(v)` 值不同才写和打戳；生成的 job 同样处理它。有索引的组件（包括 `ChildOf`、`IChildKey<T>`）只能是 `in` |
| Actor 类型 `in K` | 当前项所在 Actor 的根上的键（上面的例外） |
| 身份 `in TClaims` | 发消息的客户端的身份（上面的例外） |
| `ref DynamicBuffer<T>` | 主体上的缓冲 |
| `Sender<K>` | 发送方。消息有多个 `[From<…>]` 时写 `Sender<ActorId>` |
| `in ReplyTo<T>` | 请求的回复令牌，要晚些回复时取；`reply.Call` 是调用 id |
| 单例 `in` | 本 world 的单例；`ref` 只给带 `[WorldLocal]` 的用户单例（下面） |
| `World` | ECS 操作 |

**单例不是 Actor 之间共享的可变状态**：一个 world 里有很多个 Actor，一个单例被它们共用。
- `[On<T>]` 方法和 Actor 类型组里的系统，用户定义的单例只能取 `in`；取 `ref` 时单例类型要带 `[WorldLocal]`，说明它是 world 自己的缓存或配置（例如寻路缓存），不是 Actor 的状态：不迁移、不保存，热重启时随 `world.Save` 过去。框架的单例（`Time`、`Current`、`Outbox`、节点单例）只能 `in`。
- 每个 Actor 一份的状态放在它的根上（`K.RootPrefab` 的组件）。
- 违反时分析器报错：`A singleton is shared by every actor in this world: take it 'in', mark its type [WorldLocal] if it is a world-local cache or configuration, or keep per-actor state on the actor root`。
- 为什么：Overwatch 的单例是一局一份，因为一局有自己的 world（[GDC 2017](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)）；这里一个 world 有很多 Actor，可写的单例会让一个 Actor 的处理方法改到别的 Actor 看得见的数据，迁移时还丢掉它。

### 操作

只有一个句柄：`Entity`。所有发送、推送、加入的操作都是 `Entity` 上的扩展方法（DaServer 提供，通过实体所在的 world 找到 world），只写在这张表里：

| 写法 | 做什么 |
|---|---|
| `e.Schedule(ev)` | 本地事件，`ev` 是 `IEvent`，发给本 Actor 的实体 `e`；当前项返回后、下一项之前执行（即时链，最多 1,024 个）。不建实体：事件是 world 的链栈里的一个值。返回 `Scheduled<T>` |
| `e.Schedule(ev, after: t)` | 同上，到期时刻 = 当前项的时间 + t（第 9 节）；建一个带 `Due` 的子实体。`t` 为 `TimeSpan.Zero` 时同上一行 |
| `e.IsPending<T>()`、`e.Cancel<T>()` | `e` 上还有没执行的 T 事件、或者还在等回信的 T 请求吗（链栈里的和子实体）；取消它们：事件不再执行，请求不再等、不再重问，回信到了就丢弃（第 8、9 节） |
| `e.IsPending(s)`、`e.Cancel(s)` | `e.Schedule` 返回的那一个事件还在等吗；取消它 |
| `e.Send(to, msg)` | 服务器之间的单向消息，只发给别的 Actor，例如 `me.Send(new Player(id), new Defeated(map))` |
| `e.Ask(to, req[, state][, deadline: t][, call: id])` | 请求，回信是 `Replied`，主体是 `e`；返回调用 id。`deadline` 是这次问的超时；`[Durable]` 的请求必写，是整个承诺的期限。`call` 用同一个调用 id 重问（第 8 节） |
| `e.Reply(in token, value)` | 用回复令牌回复；令牌是 `default` 时什么也不做 |
| `e.Push(msg)` | `e` 是 Presence 或 `Connection` 的根：推给它的客户端 |
| `e.Push(in group, msg)` | 推给一个 `ClientGroup`（第 9 节） |
| `root.Broadcast(msg)` | `root` 是 Actor 的根：推给这个 Actor 所有带 `Joined` 的 Presence |
| `root.Group(key[, every: n])` | `root` 是 Actor 的根：本 Actor 的 `ClientGroup`，键带着 Actor 的 `ActorId`；`every` 是这个组每 n 个 tick 发一次（第 9 节） |
| `e.SetGroups(groups)` | `e` 是 Presence：把它的 `DynamicBuffer<Subscribed>` 换成 `ReadOnlySpan<ClientGroup>` 里的组（可以 `stackalloc`，第 9 节） |
| `root.Spawn(prefab[, in a[, in b …]])` | 在 Actor 里建实体：用注册时的 prefab 副本，`world.Instantiate(副本, in new ChildOf(root), in a, in b …)`（EasyEcs 4.7 计划，最多 9 个组件）直接写进最终的表，当场返回实体 |
| `root.GetOrSpawn(prefab, key[, in a …])` | `key` 是 `IChildKey<T>`：按它的值索引找到这个 Actor 里的子实体；没有就像 `Spawn` 一样建一个，带上 `key` 和给的组件。所以加入消息再来一次，结果一样（第 7 节）。给的组件只在新建时用，但照 C# 的规则总会求值 |
| `e.Join()` | 让 `e` 成为当前客户端在本 Actor 上的 Presence；返回 `Status`：`Ok`、`NoClient`（这一项没有客户端上下文）、`Superseded`（第 7 节） |
| `e.Leave()` | Presence `e` 离开：加上 `Leaving`（第 7 节） |
| `root.Deactivate()` | `root` 是 Actor 的根：停用这个 Actor（第 6 节）。不是根时运行时断言；分析器看得出来时报错 |
| `connection.Disconnect()` | `connection` 是 `Connection` 的根：断开这个客户端 |
| `e.Root` | `e` 所在 Actor 的根：沿 `ChildOf` 往上走的循环（EasyEcs 文档的祖先遍历） |

- **跨 Actor 的操作，发送方是 `e.Root` 那个 Actor。**
- **发出的东西先是数据**：`Send`、`Ask`、`Reply`、`Push`、`Broadcast` 和 `Join()` 的路由，都追加到运行时的单例 `Outbox`：追加时就序列化进按目标节点分的池化 `FrameWriter`，`Broadcast` 和组推送只序列化一次，每个网关一帧。有序阶段结束时（没有 tick 的 Actor 类型是每批邮件之后）和收尾之后，运行时把这些帧交给链路一次；同一个目标的帧按追加的顺序发出。所以 `Join()` 的路由一定排在回复前面（第 7 节），`TestActor<K>` 把 `Outbox` 解码成数据断言（第 12 节）。
- **当前项的上下文也是数据**：运行时的单例 `Current` 里是当前项的时间、根、发送方和客户端上下文。操作从它取客户端上下文；没有时是明确的结果（`Join()` 返回 `NoClient`），不是默认值。
- `Send`/`Ask` 的方向检查见第 4 节。
- 本地事件只能发给本 Actor 的实体，运行时断言。

### 每 tick：Actor 类型就是系统组

每 tick 的逻辑就是普通的 EasyEcs 系统，DaServer 不另造一套（例子见第 12 节的 `Steering`、`Movement`）。

- **Actor 类型就是它的系统组**：`IActor<T>` 继承 `ISystemGroup`（第 3 节），`[UpdateInGroup<Map>]` 就是"每个 Map tick 跑一次"。不另写组类型。
- **帧率写在 Actor 类型上**：`[TickRate(30)]`。
  - 帧率写进 `wire.manifest` 的 Server 一段；改帧率就是改了 Server，要滚动升级。
  - 一种服务器承载多个有 tick 的 Actor 类型时，各帧率必须整除其中最快的一个（例如 60、30、10），避免节拍抖动。
  - 没有 `[TickRate]` 的 Actor 类型也可以有系统：每批邮件之后跑（第 1 节）。所以这种组里的每个 `[ForEach]` 方法必须只访问有事的行：带 `[Changed<T>]`、`[Removed<T>]` 或过滤一个 tag 的 `[With<T>]`，或者系统设了 `UpdateInterval`；一样都没有就是每批邮件一趟全扫，分析器报错。代价相差约 70 倍：EasyEcs 在 10 万行里只走改过的行是 525 ns，全扫是 38.6 µs（[change tracking](https://ecs.xgamedev.net/en/docs/features.md#change-tracking)）。
- **只处理自己的实体**：组过滤 `In<K>` 由 `IActor<K>` 带上（`IGroupWith<In<K>>`），加到这个组每个系统的每个 `[ForEach]` 方法上（EasyEcs `guide/systems.mdx` 的 "Group filters"）。
  - 查询缓存只存匹配的表，所以同一个 world 上别的 Actor 类型的表，对这个组的系统不增加任何开销（`guide/systems.mdx` 的 "What it costs"，`why-fast.mdx` 的 "query cache"）。
  - `In<K>` 是框架的 tag（`public struct In<T> : IComponent where T : struct, IActor<T>;`），K 类型 Actor 的每个实体都带着：
    - 根：运行时激活时加上；
    - `root.Spawn(prefab, in a, in b)`：`Registration` 注册 `[Prefab<K>]` 时做一份副本（EasyEcs 的 `new Prefab(basePrefab)`），带上 `In<K>` 和一列 `ChildOf(default)`，静态的 prefab 不被改动。`Spawn` 用 `world.Instantiate(副本, in new ChildOf(root), in a, in b)` 一次写进最终的表，不移动行（EasyEcs 4.7 计划；在那之前是实例化后一次 `Set`，移动一次行）；
    - Actor 里不用 `CreateEntity`（分析器报错），一律 `root.Spawn`，所以用户从不自己加 `In<K>`。
- **怎么跑**：每个 world 的线程按 Actor 类型依次调用框架组 `Ordering`（`OrderedPhase`）、`world.Update<Map>()`、框架组 `Settling`（`Depart`、`SyncSubscriptions`、去掉 `Resumed`），最后是框架组 `Cleanup`（第 6 节）；循环的写法见 [architecture.md 第 2 节](architecture.md#2-runtime-structure)。`Ordering` 和 `Settling` 的系统是 DaServer 自己写的 `EcsSystem`，在 `OnUpdate` 里用按 Actor 类型建好的 `static readonly QueryFilter`（`.With<In<K>>()`）跑 job，所以不需要以类型形参为实参的特性。一个 world 上同一 Actor 类型的 Actor 共用 tick 边界，所以一次覆盖这个 world 上这个 Actor 类型的所有 Actor。
- **系统就是 EasyEcs 的系统**：`[ForEach]` 方法、批量方法（`Span<T>`）、`[Changed<T>]`、`[With<T>]`、`[UpdateAfter<T>]`、`UpdateInterval`，规则全按 EasyEcs 的文档。生成代码是 EasyEcs 自己的生成器写的。
- **单线程**。每个 world 设 `WorldOptions.MaxDegreeOfParallelism` = 节点的核数 / 节点的 world 数，最少 1（EasyEcs D36：并行预算按 world 设，不跨 world 共享；`guide/threading.mdx`、`guide/hosting.mdx`）。默认每个核一个 world，所以默认就是 1：`[ForEach(Parallel = true)]` 不起作用，各个 world 已经占满各个核。
  - 只有一种服务器跑的 world 比核少时（`DaServer:Worlds`，例如一个节点只跑一张超大地图），`Parallel = true` 的方法才分块多线程；`TestActor<K>` 的 `AssertParallelMatchesSerial` 就是给这种部署用的（第 12 节）。
  - 并行方法按块切行，一块至少 1,024 行（`guide/systems.mdx` 的 "Parallel methods"），所以实体少的 world 开了也不多花。
- **系统里的发送**：`Entity` 上的操作只能在非并行的系统里用，追加到 `Outbox`；`Parallel = true` 的方法里用，分析器报错。
- `Time` 在系统里是这个 tick 的值，`Time.Elapsed` 是 tick 时刻 t（第 9 节）。
- **只在值变了时写**：`[Changed<T>]` 看的是"写过"，不是"变了"，`ref` 参数和批量方法的 `Span<T>` 一取就算写入。所以每 tick 重算一个追踪组件的系统取 `Mut<T>` 参数，用 `SetIfChanged(v)` 写：值相同就不写、不打戳（EasyEcs 4.7 计划，同 Bevy 的 `set_if_neq`；不是参数的组件用生成的 `entity.SetXIfChanged(v)`）。见第 12 节的 `Steering` 和 `Movement`：站着的 NPC 不打戳，`CellAssign` 和 `Diff` 不访问它。
- **几个组件合成一份输出时**，用 `[ChangedAny<A, B, C>]`（`[RemovedAny<…>]`、`QueryFilter.ChangedAny`，4.7 计划）：其中任何一个写过，这一行只访问一次，不是每个组件一趟。
  - 真的每 tick 都变的组件不要追踪：每次写都打戳，`[Changed<T>]` 每 tick 都是全部的行，比全扫还多一份开销（EasyEcs 的建议，[change tracking](https://ecs.xgamedev.net/en/docs/features.md#change-tracking)）。第 12 节追踪 `Position` 和 `Velocity`，因为多数实体多数 tick 不动。

### 其他

- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理；节点上用户的系统用 `SystemErrorPolicy.Disable`（第 11 节），`TestActor<K>` 用 `Throw`，让测试直接失败（第 12 节）。
- **`Registration`** 是生成的一个实例对象，不是静态表：Actor 类型、prefab 副本、身份、连上就加入、分发表、路由表、系统。节点、`TestCluster`、`TestActor<K>` 都接这个对象。表在构造时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理方法抛出异常

**不回滚。** EasyEcs 的结构变更当场生效，没有命令缓冲（EasyEcs `decisions.mdx` D2），组件写入也是当场的。所以抛出时：
- 抛出之前做的修改都留着：建的实体、加删的组件和 tag、经 `ref` 参数和方法体写的组件；结构变更之后经 `ref` 参数的写入，EasyEcs 在 job 抛出时也搬到新行（4.5 的 `entity.Run` 保证）；
- 已经追加进 `Outbox` 的照样发出；
- 这条消息的请求以失败结束；它的即时链里还没执行的事件作废。

之后怎么办，看 Actor 类型上的 `[ActorOptions(OnError = …)]`：
- `ErrorAction.Restart`：不写记录，不进 `Leaving`，不加 `Deactivating`，直接销毁根的所有子孙；根变回带 `Loading` 的占位，排队的邮件（`Mail`）留在它上面，然后从最后一次写成的记录重新激活（第 6 节的表，同样经 `ActivationSlots` 和退避）。上次写记录之后的改动丢失，排队的邮件不丢。Presence 是清理组件，销毁时框架释放它们的路由，客户端 SDK 收到 `PresenceLost` 后重新发加入消息（第 7 节）。
- `ErrorAction.Resume`：实体就是现在的样子，接着处理下一项。
- **默认按有没有持久化组件分**：有 `IPersisted` 组件的 Actor 类型（它的 `RootPrefab`、`[Prefab<K>]`、处理方法或系统用到一个，生成器判断）默认 `Restart`；其余默认 `Resume`。原因是不回滚：改了一半的项留在内存里，下一个检查点就把它写进记录，宕机和迁移都去不掉它；`Restart` 丢掉内存，从上一份完整的记录接着来。没有记录的 Actor 只有内存这一份，丢掉它不比留着好。

- 分析器给建议：**先校验，最后做结构变更**。方法里结构变更之后还有 `throw` 或可能抛出的调用时，给出提示。第 12 节的 `Resolve` 就是这样写的。
- 一项的即时链超过 1,024 个，或者一个 Actor 待处理的项超过上限（第 9 节），也按 `OnError` 处理。
- **`[On<Activated>]` 抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次；
  - 连续失败若干次（节点配置）后，留下一个带 `ActivationFailed(until)` 的占位：到 `until` 之前，发来的请求立刻以 `Busy` 拒绝（`RetryAfter` 是剩下的时间），单向消息丢弃并计数，不再去占 epoch。一个每次激活都抛出的 Actor 不会反复压存储。
  - 这样不会留下初始化了一半的 Actor。
- `Connection` 抛出异常时，断开这个客户端。

---

## 6. 生命周期

| 生命周期 | 形式 | 何时 | 常见用途 |
|---|---|---|---|
| `Activated(Activation Origin)` | 邮件，`[On<Activated>]` 处理，主体是根 | Actor 激活后的第一条邮件；`enum Activation { Created, Loaded, Migrated }` | 初始化 |
| `Deactivating` | 根上的 tag，系统 `[ForEach, With<Deactivating>]` 反应 | 停用前的最后一个 tick（没有 tick 的 Actor 类型：最后一批邮件之后的那一轮系统） | 收尾 |

- **`Activated` 是消息，不是 tag**：初始化必须在排队的邮件之前执行，而系统在有序阶段之后才跑，所以它只能是第一条邮件。顺序固定：`Activated` 在占位上等着的邮件之前，等着的邮件在之后到达的邮件之前（运行时激活时按这个顺序重新编序号）。
- **`Deactivating` 是数据**：它之后不再有邮件，不需要排在邮件里，所以和 `Leaving`、`Detached`（第 7 节）、`Restarting`、`Resumed`（第 10 节）一样是 tag，由系统反应。
- `PresenceLost` 不在这张表里：它不是 Actor 的生命周期，是 `Connection` 上的通知（第 7 节）。

**占位是 ECS 数据，也是以后的根**（EasyEcs `guide/migration-and-unload.mdx` 的 "A record of what persists"：先有实体，记录后到，装到它上面）：一个键在"还没有激活"或"正在写记录"时，它的根实体是一个占位。占位按注册时 `K.RootPrefab` 的副本建，这份副本不带 `In<K>`；占位上还有 Actor 类型组件（键）、一个 tag（`Loading` 或 `Saving`）和等着的邮件（框架内部的 `DynamicBuffer<Mail>`）。没有 `In<K>`，所以系统和处理方法都匹配不到它。占位上等着的邮件有上限（和 `MaxPending` 同一个数），超过时请求回 `Busy`，单向消息丢弃并计数。

| 状态 | 根实体 | 怎么进入 | 发生什么 |
|---|---|---|---|
| 加载中 | 占位，带 `Loading` | 第一条消息到达本节点，本节点按自己的视图是这个键的主人（不是主人就转发，不激活）；或者处理方法抛出、`OnError` 是 `Restart`（有持久化组件的 Actor 类型的默认，第 5 节） | 邮件留在占位上；拿到 `ActivationSlots` 的名额后，一条语句占 epoch 并读记录（第 10 节） |
| 激活 | 同一个实体，成为根 | epoch 占到了，记录读到了或者没有记录 | 有记录时 `LoadEntitiesInto(record, [占位, n−1 个空实体], markWritten: false)`（第 10 节）；然后加上 `In<K>`，去掉 `Loading`。键和邮件本来就在它上面，不搬动；邮箱里第一条是 `Activated`，后面才是等着的邮件 |
| 运行 | 完整的根实体 | — | 按 `(时间, 序号)` 执行消息和事件，再跑系统和收尾（第 1 节） |
| 停用 | 完整的根实体 → 占位，带 `Saving` | 空闲超过 `IdleTimeout`（不写用节点配置 `DaServer:IdleTimeout`；空闲检查是 `[ForEach(Slices = N)]` 的框架系统，每次看每张表的 1/N 行）：没有待执行的延后事件和等待中的请求，也没有不带 `Detached` 的 Presence；或者调用 `root.Deactivate()` | 依次：<br>1. 对每个 Presence `Leave()`；<br>2. 根加上 `Deactivating`，之后的邮件不再执行，留给占位；<br>3. 这一轮的系统和收尾跑完（用户对 `Leaving` 和 `Deactivating` 的反应、`Depart`）后，`SaveEntities` 写进缓冲，销毁整棵子树（待执行的延后事件随之消失），换成带 `Saving` 的占位，邮件都留在占位上；<br>4. 异步写记录，按 epoch 比较并交换；<br>5. 写成功后：占位上有邮件，就变成 `Loading`，读到的是刚写好的记录；没有就销毁占位 |
| 隔离 | 销毁 | 写记录时 epoch 比较失败；收到 `Fence(键, epoch)` 控制邮件（第 10 节）；或者本节点被移出成员视图 | 不写记录、不进 `Leaving`、不加 `Deactivating`，直接销毁整棵子树；Presence 的路由由清理组件释放（第 7 节）；排队的请求以 `NoOwner` 失败 |

- 写记录失败就重试，绝不丢弃；epoch 比较失败不是失败，是隔离。
- **框架在根上数着待处理的项**（内部的 `Pending`，就是 `MaxPending` 限的那个个数，第 9 节）：建延后事件、发出请求时加一，执行、取消、收到回信时减一。空闲的判断只看这个数为 0 的根，所以 Actor 不会在等回信或等定时事件时被停用。节点宕机时延后事件和普通请求的等待照样丢失（停用和记录本来就不保存它们）；`[Durable]` 的等待随记录保存，宕机后由 `DueScan` 唤醒（第 8 节）。
- **存储慢或不可用**：
  - 框架限制本节点同时在占 epoch 的激活数（内部的 `ActivationSlots`）；拿不到名额的占位按带随机抖动的退避再试，存储恢复时不会一齐涌上去；
  - 存储的延迟或错误超过阈值时，节点进入存储降级（内部的 `StoreDegraded`）：这期间不做空闲停用（停用要写记录），带 `Saving` 的占位数有上限，超过的停用推迟；
  - 占位等过了期限（节点配置）还没激活，发来的请求立刻回 `Busy`，不再排队到超时；
  - 就绪检查不看存储：存储出问题时节点照样就绪，已经激活的 Actor 照常运行。
- `OnError = Restart` 不走这张表的停用，见第 5 节。
- **框架销毁整棵子树之前，先从子孙上移除不该触发清理的清理组件。** 销毁带清理组件（`ICleanupComponent`）的实体时，EasyEcs 不释放它：它失去其他所有组件（包括 `In<K>`），离开所有索引，带上 `Destroyed`，等清理系统移除最后一个清理组件（`guide/cleanup-and-disable.mdx`）。框架的两个清理组件是 `Presence`（释放路由，第 7 节）和 `Persisted`（给根标 `Unsaved`，第 10 节）：

  | 销毁的原因 | 先移除 `Persisted` | 先移除 `Presence` |
  |---|---|---|
  | 停用（写完 `SaveEntities` 之后） | 是：记录已经写进缓冲，根也要销毁 | 不用：这时已经没有 Presence（第 1 步 `Leave()`，`Depart` 已经移除） |
  | 迁移（写完迁移字节之后） | 是 | 是：Presence 随字节到了新节点，路由不释放 |
  | `OnError = Restart`、隔离、激活失败 | 是：不写记录 | 不移除：路由要释放，客户端 SDK 据此重新加入 |

  所以只有用户自己的销毁、`Restart`、隔离和激活失败留下待清理的实体，没有白做的清理。
- **对 `Destroyed` 反应的框架系统不带 `In<K>` 过滤**：退役的实体已经没有 `In<K>`，带着它就永远匹配不到。所以 `ReleaseRoutes`（`[With<Destroyed>, With<Presence>]`）和 `Persisted` 的清理系统在框架组 `Cleanup` 里，按 world 跑，不按 Actor 类型；它们需要的信息（连接戳、根的 `EntityId`）都在清理组件自己里面。
- **激活的条件：**
  - 默认任何发给它的消息都能激活（Orleans 的虚拟 actor）；
  - Actor 类型可以写 `[ActivatedBy<EnterMap>]` 收紧，其他消息遇到不存在的 Actor，返回 `NoActor`；
  - 框架的控制消息（`Replied`、`JoinConfirmed`、路由的释放等）不会激活 Actor，找不到就丢弃；`DueScan` 发的 `Wake` 是例外，它就是来激活的（第 8 节）；
  - 只有按本节点的视图是这个键的主人时才激活；不是主人就转发给主人（跳数有上限），主人的激活再由存储里的放置版本把关（第 10 节）；
  - 本节点上还有这个键的墓碑（迁移走了，第 10 节）时不激活，邮件都转发到新节点；
  - `Connection` 只在客户端连接时激活。

---

## 7. 客户端与 Presence

### `Connection` 实体

- **激活**：客户端连上一个承载 `Connection` 的节点后（新连接先过网关的接纳令牌桶，第 11 节），框架用令牌里的 `ClientId` 激活 `Connection(ClientId)`。如果这个节点上已经有同一个 `ClientId` 的 `Connection`，按连接戳决定谁留下（下面）。
- **连接信息**：`(ClientId, 节点, 连接戳)`，框架内部使用。
- **连接戳是有序的**：`(令牌签发序号, 网关节点代数, 节点上的连接计数)`，按字典序比较。
  - 令牌签发序号是每个账号的计数器，由签发方持久化（例如在账号的 Actor 里），每次登录加一，写进签过名的令牌：重新登录拿到的令牌更大。刷新令牌保留原来的签发序号，所以刷新过令牌的旧设备排不到更新的登录前面；
  - 同一个令牌重连时，后两项区分连接。
  - 比较结果只有三种：**更小**的回复 `Superseded`，那个 `Connection` 断开；**相等**的是同一个连接在重试，幂等地再回复一次；**更大**的接管。
- **身份**：令牌里的声明解码成用户写的 `[Claims]` 类型，例如 `Account`。框架只负责解码令牌、把它作为组件加到 `Connection` 上，并随它转发的每条客户端消息带上（第 5 节的身份参数）。框架不认识任何业务字段。
- **位置固定**：`Connection` 固定在连接所在的节点，不参与放置和重新平衡，也不迁移；连接断开，它就停用。
- **扩展**：写 `[Handles<Connection>]` 类；要给它加组件（例如令牌桶），在它的 `[On<Activated>]` 里当场 `Set` 上（`Connection` 是框架的类型，没有用户写的 `RootPrefab`）。
- **找不到它**：别的 Actor 不能用 `ClientId` 找到 `Connection`。推送只经过 Presence，所以不需要位置目录。

### Presence

客户端在一个 Actor 上的存在，叫 **Presence**。

| 在哪 | 是什么 |
|---|---|
| 目标一侧 | 根的一个直接子实体，带 `Presence(PresenceId Id, 连接信息)` 和 `In<K>`，按 `PresenceId` 建值索引，例如地图上的 Avatar。`Presence` 是清理组件（`ICleanupComponent`） |
| `Connection` 一侧 | 路由表 `DynamicBuffer<Route>` 里的一项：`Route(ActorId 目标, PresenceId, Slot 槽号)`，框架内部使用 |
| 客户端 SDK | 一个**槽号**：网关给这个连接上每个加入过的 Actor 类型分配的小整数。不含 Actor 类型、节点和地址 |

- **每个 Actor 类型最多一个 Presence**：一个客户端同一时刻最多在一张地图上、一个 Player 上。
- **`PresenceId`** 是 64 位随机数，不含节点位，只在服务端：网关用它认路由，目标用它找 Presence 实体。Presence 迁移后照用。
- **接管总是换新的 `PresenceId`**，所以旧连接留下的释放、离开都对不上新的路由。

### 连上就加入：`[JoinOnConnect<TClaims>]`

```csharp
[Claims] public readonly partial record struct Account(long Id);                         // the token's claims: the user's own type
[JoinOnConnect<Account>] public readonly partial record struct Player(long Id) : IActor<Player>;   // joined at connect, keyed by the claims
```

- **身份是用户自己的类型**，不绑在 Player 上：`[Claims]` 写成 `partial`，字段自己写，生成器补上 `IComponent`；不超过 16 字节、非托管。一个工程最多一个 `[Claims]`。
- **想在连上时就加入的 Actor 类型，自己声明** `[JoinOnConnect<Account>]`。规则：这个 Actor 类型的键和身份形状相同（同样类型、同样顺序的字段），生成器按字段一一对应，`Account(42)` → `Player(42)`；形状不同，分析器报错。可以有多个 Actor 类型挂在同一个身份上。
- **谁胜出是声明上的参数**：`[JoinOnConnect<Account>(Takeover = Takeover.NewestWins)]`，默认 `NewestWins`：
  - `NewestWins`：连接戳更大的接管，旧连接的 Presence 被换掉，旧的 `Connection` 由控制邮件 `TakeOver` 关闭（下面"加入"的接管）；
  - `Reject`：已有的连接留下，新的收到 `AlreadyConnected` 后断开（半开的旧连接要等它超时）。
- 框架做完整件事，用户不写代码：
  1. `Connection` 激活后，带着连接戳向 `Player(Id)` 发框架内部的加入请求；
  2. Player 一侧按连接戳和 `Takeover` 建或者接管这个客户端的 Presence；
  3. 失败时重试（加入按连接戳幂等）：`Busy` 按它带的 `RetryAfter`，其余按带随机抖动的退避；超过次数，以 `Unavailable(retryAfter)` 结束这个会话，SDK 按 `retryAfter` 再连。一个游戏节点宕机时，十万个客户端不会同时重试（第 11 节）；
  4. 这样加入的 Presence 丢失（Player 重启）时，框架重新加入：它没有客户端消息可以重发。
- 加入好之前，客户端消息在 `Connection` 上排队，有上限（节点配置），超过的回 `Busy`；加入好后框架推一条 `Ready` 给客户端。
- Player 上的 Presence 和别的 Presence 一样：不带 `[To]`、由 Player 处理的客户端消息，主体就是它。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Connection` 实体。
2. 先过过滤：`[Handles<Connection>]` 类里的 `[On<Incoming>] static Verdict Allow(in Incoming m, ref RateLimit r)`。`Incoming` 是帧头的视图（`TypeId`、长度、身份），网关从不解码负载。它逐条执行，返回放行或者拒绝的原因；没写就全部放行，这时转发是对一批帧的一趟遍历，不按消息跑 job。
3. 按生成的路由表，得到处理 Actor 类型。
4. 得到目标：
   - 带 `[To]`：目标是这个 Actor 类型、键是这个参数的 Actor，没有就激活；
   - 不带 `[To]`：路由表里这个 Actor 类型的那一项；没有就拒绝，`NotJoined`。
5. 直接转发给目标，信封带着客户端上下文；不带 `[To]` 的还带着 `PresenceId`，目标据此找到 Presence 实体，作为主体。Presence 带着 `Leaving`，或者连接戳对不上，回 `NotJoined`。
6. 被拒绝时，客户端收到原因：`NotJoined`、`Busy`、`NoActor`、`Filtered`、`Superseded` 等。

### 加入：`e.Join()`

例子是第 12 节的 `Maps.Enter`：`root.GetOrSpawn` 找到或建好 Avatar，然后 `return avatar.Join() == Status.Ok;`。

**本地当场生效。** EasyEcs 的结构变更当场生效（EasyEcs `decisions.mdx` D2），所以 `root.Spawn(…)` 要么返回带好组件的实体，要么抛出。`Join()` 也是当场的，结果是一个 `Status` 值，不是异常：
1. `e` 必须是根的直接子实体，带 `In<K>`（`root.Spawn` 建的就是），否则抛出（这是代码错误）；
2. 当前项没有客户端上下文（系统里、`Activated` 里，或者请求是从没有客户端的地方发出的，第 4 节）：返回 `NoClient`，什么也不改；
3. `e` 已经是 Presence 时，按连接戳：
   - 同一个连接：返回 `Ok`（幂等）；
   - 当前连接的戳更小：返回 `Superseded`，什么也不改；
   - 更大：接管。去掉 `Joined`、`Leaving` 和 `Detached`，换新的 `PresenceId`；旧连接的路由按旧的 `(PresenceId, 连接戳)` 释放，并向旧连接所在的网关发 `TakeOver`（下面）；
4. 加上 `Presence(新的 PresenceId, 连接信息)` 和确认期限 `Due`，把路由追加到 `Outbox`，返回 `Ok`。
- 分析器要求用到 `Join()` 的返回值。

**接管时关掉旧连接**：目标把控制邮件 `TakeOver(新的连接戳)` 追加到 `Outbox`，按旧 Presence 记下的节点送到旧连接所在的网关（跨网关也一样）。旧的 `Connection` 比较签发序号：
- 新的更大（另一台设备的新登录）：以 `Superseded` 关闭。SDK 把 `Superseded` 当作终止：报一个事件给应用，不自动重连；
- 相同（同一次登录换了连接，或者 `Retire`）：安静地关闭；退役中的连接要等各个路由都换走、它上面还没回复的调用都回复了再关（下面"网关自己升级或缩容"）。退役途中旧连接断了，Presence 按断线处理（`Detached`），新连接接管后 `Joined.MissedPushes` 为真，快照照推。

**在网关登记路由是异步的：**
1. `Outbox` 里路由排在这条请求的回复前面。两者同一来源、同一目标，所以 `Connection` 一定先写路由，再把回复交给客户端。
2. `Connection` 写路由时，如果已经有同一个 Actor 类型、别的目标的路由，先删掉它，并把 `(旧 PresenceId, 连接戳)` 发给旧目标：对得上的话，旧目标对那个 Presence 执行 `Leave()`（**每个 Actor 类型一个 Presence**）。槽号不变。
3. `Connection` 写好路由后：
   - 先向目标发 `JoinConfirmed(PresenceId)`，之后才转发消息。两者同一来源、同一目标，所以确认一定先到。确认如果丢了，这个 Presence 上第一条转发来的消息也算确认；
   - 给客户端 SDK 发 `JoinConfirmed(槽号, 调用 id)`，排在回复前面：SDK 据此记下"这个槽是哪条消息加入的"。
4. 目标收到确认后：给 Presence 实体加 `Joined(MissedPushes)`，去掉确认期限。
   - 推送和 AOI 只认带 `Joined` 的 Presence；
   - 有 `[TickRate]` 的 Actor 类型，先给客户端推一条框架通知，带帧长、当前 tick 号和服务器时间（第 9 节），之后推送才开始；
   - `MissedPushes` 只在 SDK 因 `Retire` 换连接时为假（加入请求带着这个标记，旧路由上有 `HandedOff` 栅栏，一个推送也没漏）；第一次加入、断线后重连、接管 `Detached` 的 Presence 都为真；
   - 完整快照是用户的逻辑：`[ForEach, Changed<Joined>]` 的系统，`MissedPushes` 为假时跳过（第 12 节）。框架不知道"快照"，用户也不用懂 `Retire` 和连接戳。
5. **登记失败**（`Connection` 已经不在了，路由没写成）：确认期限到了，框架对这个 Presence 执行 `Leave()`。这和客户端加入后马上断线走的是同一条路。
6. **期限**：
   - 请求的期限取客户端请求剩下的时间；
   - 回复到达 `Connection` 时，客户端的请求已经超时的，照样写路由（Presence 已经在目标上了），只丢弃回复。

**推送**：`e.Push(msg)`、`root.Broadcast(msg)`、`ClientGroup`（第 5 节的操作表）。都按 Presence 实体记下的节点直接发送。

**换地图**：客户端发 `EnterMap(8)`。地图 8 加入后，`Connection` 写路由时删掉地图 7 的路由，地图 7 对那个 Presence `Leave()`（上面第 2 步）。两张地图是两个 Actor，它们之间没有先后：短时间内客户端可能同时出现在两张图上。需要严格先后的业务，用请求串起来。

### 离开：`Leaving` 是数据

- **`e.Leave()`** 当场：
  1. 加上 `Leaving`，去掉 `Joined`；
  2. 把路由的释放 `(PresenceId, 连接戳)` 追加到 `Outbox`：`Connection` 对得上就删掉路由，网关上这个 Presence 的组成员一起删掉；客户端 SDK 收到 `PresenceClosed(槽号)`，丢掉这个槽存的加入消息，不重新加入。
- **用户的系统反应**：`[ForEach, With<Leaving>]`，例如广播"消失"（第 12 节的 `Vanish`）。可能已经不在的组件用 `TryGetX`。
- **收尾组的 `Depart`** 排在用户的系统之后：去掉 `Presence`（路由已经释放过），销毁实体。
- 带 `Leaving` 的 Presence 不再收客户端消息（`NotJoined`）。同一项或同一帧里再 `Join()` 它，就去掉 `Leaving`，算作接管。
- **外部触发的离开**（被 `Disconnect` 踢掉、加入了同一 Actor 类型的另一个 Actor）由框架作为邮件送到目标，在目标的处理过程中 `Leave()`；**断线**（连接断开、被顶号后关闭、网关离开视图）送到目标的是宽限（下面的 `Detached`）。邮件带着 `(PresenceId, 连接戳)`，对不上就丢弃。
- **同一个键不会有两个 Presence 实体**：同一个 Player 再进同一张图时，`GetOrSpawn` 找到已有的 Avatar，`Join()` 接管它。

### 断线：宽限期内的 `Detached`

- 连接断开时（客户端断线、被顶号后关闭、读得太慢被断开），或者网关节点离开成员视图时，目标不立刻 `Leave()` 这些 Presence：去掉 `Joined`，加上 tag `Detached` 和期限 `Due`（现在 + 宽限期）。
- 宽限期默认 30 秒，在 Actor 类型上写 `[ActorOptions(DetachGrace = 秒)]` 改；0 就是立刻 `Leave()`。
- 带 `Detached` 的 Presence：不收推送（推送和 AOI 只认 `Joined`），不让 Actor 保持活跃（第 6 节的空闲判断不算它），实体和它的组件都还在。
- 这期间客户端的消息：断开时还没送达的丢失；重连之后、这个槽重新加入之前，不带 `[To]`、发给这个 Actor 类型的消息在网关上没有路由，请求回 `NotJoined`，单向消息丢弃并计数（第 11 节的表）。
- 客户端重连，新连接的戳更大，`Join()` 接管它：去掉 `Detached`，照常确认，`Joined.MissedPushes` 为真，所以快照系统重推一份。`[JoinOnConnect]` 的 Presence 也一样。
- 期限到了还没被接管，框架对它 `Leave()`。
- `cluster.Disconnect(clientId)`、`connection.Disconnect()` 是有意的断开：Presence 立刻 `Leave()`，没有宽限。
- 一个地区的网络抖一下、或者一个网关宕机，客户端重连回来时 Presence 还在，Actor 不用重建它们。

**没经过 `Leave()` 就销毁的 Presence**（父实体级联销毁、`Restart`、隔离、用户 `Destroy()`）：`Presence` 是清理组件，实体留着 `Presence` 和 EasyEcs 的 `Destroyed` tag。框架组 `Cleanup` 的 `ReleaseRoutes`（`[With<Destroyed>, With<Presence>]`，不带 `In<K>`，第 6 节）把 `Lost(PresenceId, 连接戳)` 追加到 `Outbox`，再移除 `Presence`，实体随之释放。`Connection` 对得上就删掉路由，投递 `PresenceLost`（下面）。
- **迁移不是丢失**：迁移的字节里已经带着 Presence，框架销毁源节点的子树前先移除它们的 `Presence`，不释放路由（第 6 节的表）。

**通知丢失时的兜底：**
- 两个节点之间的链路重连时，目标一侧按网关列出自己持有的 Presence，网关指出其中哪些已经不认识，目标对这些按断线处理（`Detached`）；
- 某个网关离开成员视图时，各节点对记在它上面的 Presence 按断线处理（`Detached`）；
- 网络抖动、GC 停顿不会让 Presence 失效：节点离开视图之后，还要等宽限期过去。
- Presence 相关的控制消息走控制通道，不受 `Busy` 限制，但有界（第 11 节）。

### Presence 丢失：客户端 SDK 重新加入

目标重启了（从记录恢复、`OnError = Restart`、隔离），旧的 Presence 实体已经没有了：
1. `Connection` 在三种情况下删掉这条路由：收到 `Lost`；转发时收到 `NotJoined`；看到目标所在的节点离开视图。即使客户端一直不发消息，也能发现。
2. 投递 `PresenceLost`：
   - 给客户端 SDK：`PresenceLost(槽号)`；
   - 给 `[Handles<Connection>]` 的 `[On<PresenceLost>]`，写了才投递。
3. **SDK 重新发这个槽的加入消息**：SDK 为每个槽记着最后一条加入它的消息（`JoinConfirmed(槽号, 调用 id)` 指出是哪一条）。收到 `PresenceLost(槽号)` 时，按带随机抖动的退避重发它（收到 `Busy` 时按它的 `RetryAfter`），带着当前的身份；目标因此重新激活，加入消息的处理方法再调用一次 `Join()`。
   - 同一个槽后来加入的消息替换先前的；`PresenceClosed(槽号)` 清掉它；
   - 重连之后，`Ready` 一到，SDK 重发所有槽的加入消息：新连接的戳更大，目标上旧连接留下的 Presence 被接管（上面的 `Join()` 第 3 步）；
   - 重发的回复由 SDK 自己收下，应用只看到 SDK 的事件。
- **限制**：
  - 只重发那一条加入消息，不重发之后的消息；之后的状态以目标的记录为准；
  - 加入消息的处理方法用 `root.GetOrSpawn` 建 Presence 的实体，再来一次结果一样，不用另写幂等的逻辑（第 12 节的 `Enter`）；
  - 连上就加入（`[JoinOnConnect]`）的 Presence 不靠 SDK，由框架重新加入。

### 节点热重启、网关升级：客户端不断线，用户不写代码

- **承载 Actor 的节点热重启**（成员视图里是 `NodeRestarting`，第 10 节）：网关把转发给它的消息留在链路的有界重发窗口里，它以同一个身份重连后送达。想让客户端看到提示（例如"地图重启中"）的，在网关上写一个带 `UpdateInterval` 的系统（例如每秒一次，第 5 节）：节点单例 `RestartingNodes` 列出正在热重启的节点和各自的预计停顿；`nodes.TryGetPause(connection, out var pause)` 看这个连接的路由有没有指向其中一个（`pause` 是它的预计停顿），有就 `connection.Push(…)`。
- **网关自己升级或缩容**：网关不热重启（`Connection` 钉在套接字上），先扩后缩：新网关全部就绪后，旧网关退出负载均衡，再分批发 `ControlOp.Retire`（先连后断，ADR-026；最多 20 批，每批至少 100 个连接）。SDK 经同一个公开地址连上新网关，新连接重发各槽的加入消息（带着"因 `Retire` 换连接"的标记），连接戳更大，接管各个 Presence（`Takeover`）。
  - **每个 Actor 的推送保持顺序**（ADR-026 的栅栏）：目标接管时，先在旧路由上推一个 `HandedOff(槽号)`，排在它在旧连接上的最后一个推送后面，再换到新路由；SDK 把新连接上这个槽的推送先扣住，直到旧连接上的 `HandedOff` 到达，或者等过一个上限（它的调用超时）。
  - 旧连接在它上面还没回复的调用都回复之后才关闭；它释放的路由按旧的 `PresenceId` 对不上新的 Presence，SDK 也忽略退役中的旧连接送来的 `PresenceLost`。
  - 超过 `RetireGrace` 还没走的被断开，按断线处理（`Detached`）。每个客户端只换一次，只由负载均衡器选网关，客户端不多知道任何拓扑。各平台的做法见 [deployment.md 第 2 节](deployment.md#stateless-groups-and-gateways-deployment)。
- **扩容后的再平衡按负载，不按年龄**（ADR-089）：长连接不会自己挪到新网关上。每个接受客户端的节点把自己的连接数放进成员信息（脉搏里的负载，ADR-031），各自算出本组的平均；只有比平均多出一个容差（默认 10%，`DaServer:Clients:RebalanceTolerance`）时，才把多出的那部分分批 `Retire`（同上，先连后断），每秒不超过本节点连接的 1%，由负载均衡器选新网关。
  - 均衡的组谁也不退役；不经过协调者，每个节点只看视图自己决定；
  - 有网关在排空（`Leaving`）或者 `RestartingNodes` 不为空时暂停：那时的连接数不代表稳态。
  - 连接的最长年龄（`WithClientConnections(port, maxConnectionAge:)` 或 `DaServer:Clients:MaxConnectionAge`）只作为可选设置留着，默认关，给需要客户端定期重连的部署；到龄的连接同样经 `Retire` 换网关。

---

## 8. 等待别的 Actor 的结果

例子是第 12 节的 `Pickups`：客户端要捡起地上的掉落，掉落自己向 Player 要求发放物品（`[Durable]` 的 `GrantItem`），框架替它问到有结果为止。

- **`e.Ask(to, in message[, state][, deadline: t][, call: id])`** 创建一个等待实体，它是 `e` 的子实体，带：
  - `state`：一个普通组件，存在等待实体上，回信时作为 `a.State` 交回；不写时回信是 `Replied<TReq>`，没有 `State`；
  - 调用 id：全集群唯一，`a.Call`；运行时按 world 维护"调用 id → 等待实体"的哈希索引，回信到达时一次查到；
  - 期限：`Due`（第 9 节），在线上是剩余时间。`deadline` 是这次问的超时，不写用节点配置 `DaServer:AskTimeout`。在带请求上下文的项里（处理一个请求，或者它链上的回信），取 min(这个超时, 这个请求剩下的时间)：嵌套的问不会比外层的请求活得久；
  - 客户端上下文：处理客户端消息、`PresenceLost` 时自动带上（第 4 节）。
  - 生成器为有状态和无状态各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(Actor 类型, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Replied<TRequest>` 或 `Replied<TRequest, TState>` 邮件**，按（请求类型, 状态类型）分发；主体是发问的实体 `e`。
  - `Replied` 是只读结构，字段都是 `readonly`：`Status`、`Call`、`RetryAfter`（`Busy` 时建议的等待，否则为 0），有状态时还有 `State`；
  - **回复的值只能经 `a.TryGetValue(out var v)` 取**：成功时返回 `true` 和值；`a.Status` 只说明失败的原因（`TimedOut`、`Busy`、`NoOwner`…），成功时是 `Ok`。没有"失败时也能读到的默认值"；
  - 值的类型来自请求：生成器为每个请求类型写一个 C# 14 的扩展块（`extension<TState>(Replied<GrantItem, TState> a)`，里面是 `bool TryGetValue(out bool value)`）；回复的字节留在这封邮件的帧里（收到它的池化接收缓冲），`TryGetValue` 当场反序列化，不另存一份；
  - **等待实体在它的 `Replied` 处理方法返回后立即销毁**，帧随之归还。
  - 发问的实体先被销毁了（例如 Avatar 离开），等待实体随它一起销毁，回信到达时丢弃。所以要收尾的，从根或者不会离开的子实体发问（在 Presence 上 `Ask`，分析器给提示）。
- **超时**：`Due` 到了，框架投递一个 `Status = TimedOut` 的 `Replied`，和别的项一样按 `(时间, 序号)` 执行。
- **超时不等于没执行**：请求可能已经执行，只是回复没赶上。普通的请求，结果未知时由发问方决定：
  - `a.Status.Transient` 说明值得重试；要重试，延后 `a.RetryAfter`，用同一个调用 id 再问（`me.Ask(to, req, state, call: a.Call)`）；
  - 框架不替普通请求去重；接收方要去重，自己按 `reply.Call` 记。改别人状态、不能执行两次的请求，写成 `[Durable]`（下面），不自己记。

### `[Durable]`：两端由框架负责

写在 `IRequestTo` 上（第 4 节），给必须落定一次的请求：发物品、扣款、下单。普通请求照旧：当场回复，结果未知由发问方决定。

- **发问方**：
  - `Ask` 必写 `deadline:`（生成的方法里它不是可选参数），它是整个承诺的期限。等待实体带着请求、状态、调用 id 和到期的 UTC 时刻（`Time.Utc` + `deadline`），随 Actor 的记录保存（状态因此要能序列化，第 10 节）。
  - **请求在等待实体写进记录之后才发出**：先留在根上（内部的 `HeldAsk`），根标 `Unsaved`，`CheckpointSystem` 不等检查点间隔当场写记录。否则发问方在请求发出、记录写成之前宕机，从旧记录恢复后不知道自己问过，同一件东西会再许给别人。
  - 每一次问的超时是 `DaServer:AskTimeout`，不超过剩下的期限。结果是 `Transient`（`TimedOut`、`ConnectionLost`、`Busy`、`NoOwner`）时，框架用同一个调用 id 再问：等 `RetryAfter`（没有就指数退避），加完全随机抖动，计入每个节点的重试预算（预算空了就等下一次退避，不算失败）。Actor 从记录重新激活之后（宕机、迁移退回记录），框架对每个还在等的 `[Durable]` 等待实体再问一次；热重启之后也再问一次，接收方去重，无害。
  - 到了期限还没有结果，回信是 `Status.Expired`：结果未知，框架不再问。所以 `Replied` 处理方法只看到最终的结果：回复的值、确定没执行的拒绝（`NotExecuted` 而不是 `Transient` 的，例如 `NoActor`、`Skipped`），或者 `Expired`。
  - 还在等的 `[Durable]` 请求让 Actor 保持激活（算在待处理的个数里，第 6 节）。宕机之后由谁来再问：根上的 `NextDue` 是记录里最早要醒来的 UTC 时刻（这里是下一次再问的时刻），检查点把它写进记录的 `next_due` 列；每个节点的 `DueScan` 每隔 `DaServer:DueScanInterval` 向存储查自己是主人的槽里到时的键（`IActorStore.DueKeys(slots, until)`），给还没激活的发 `Wake`，Actor 因此激活、再问。注册里有 `[Durable]` 请求或者 `DaServer.Reminders`（第 15 节）时才查。
- **接收方**：
  - 框架按调用 id 去重。处理方法执行时，框架把 (调用 id, 回复, 发问方的期限) 记进根上的内部环 `ReplyLog`；它和处理方法的改动在同一次写记录里落定。回复（返回值或者 `Reply(token)`）不进 `Outbox`，先留在根上（内部的 `HeldReply`），根标 `Unsaved`，`CheckpointSystem` 当场写记录，按 epoch 比较并交换成功之后才放出回复。
  - 重复的请求（同一个调用 id）不调用处理方法：回复已经落定的，用 `ReplyLog` 里的回答；还留着没放出、或者处理方法取了 `ReplyTo<T>` 还没回复的，丢弃（之后放出的那一个回复回答这个调用 id）。
  - `[Durable]` 请求的 `ReplyTo<T>` 只能存进随记录保存的数据（`IPersisted` 组件、`[Durable]` 的 `Ask` 状态，分析器检查）：令牌丢了，重复的请求又被丢弃，发问方只能等到 `Expired`。
  - `ReplyLog` 的项留到发问方的期限再加 `DaServer:ClockSkew` 之后（发问方过了期限就不再问，第 9 节）。每个 Actor 最多 `DaServer:ReplyLogCapacity` 项（默认 1,024）；满了、又都没过期时，新的 `[Durable]` 请求回 `Busy`，`RetryAfter` 是最早一项过期的时刻（确定没执行）。
  - 比较失败说明这个激活已经被隔离：回复不发，改动和 `ReplyLog` 都没写进存储，发问方重问时由真正的主人执行一次。
- **结果**：调用方看到的成功，任一端宕机都不会丢；一个调用 id 在期限内只落定一次。代价是两端各多一次写存储的延迟。不标的请求宕机时最多丢一个检查点间隔的改动（第 10 节）。
- `ICluster` 带 `CallId.From(外部键)` 发 `[Durable]` 的请求时，接收方一样去重；调用方在 Actor 外面，不持久化，重试由它自己（第 4 节）。

### 客户端的调用 id、`ReplyTo<T>`

- **客户端的调用 id**：SDK 每个会话一个计数器，重连后接着用；网关在前面加上 `ClientId`，所以处理方法读到的 `reply.Call` 就是 `(ClientId, 客户端调用 id)`，全集群唯一，去重就用它。应用重试时写 `client.Ask(msg, call: id)`，用同一个 id。连接断了还没收到回复的调用，SDK 以 `ConnectionLost` 结束：结果未知，同超时。单向的客户端消息不带调用 id：最多一次，SDK 不重发，不用去重。
- **问多个目标**：对每个目标各 `Ask` 一次，每个目标一个 `Replied`。要汇总时，把计数放进发问实体上的组件。
- **`ReplyTo<T>` 是普通数据**：
  - 请求的处理方法取 `in ReplyTo<T> reply` 参数；`T` 就是请求声明的回复类型，`Reply` 在编译期检查类型；
  - 要先问别人再回复：把令牌放进 `Ask` 的状态（第 12 节的 `Looting.Reply`），回信时 `me.Reply(in a.State.Reply, …)`；
  - 要晚些回复、不问别人：把令牌存进任何组件，之后 `Reply`；存在 `IPersisted` 组件里的令牌跨过停用和宕机，期限内照样能回复；
  - `default` 的令牌，`Reply` 什么也不做；
  - 同一个令牌只有第一次回复有效；一直没有回复，调用方按期限超时；
  - 令牌带着请求的期限：过了期限的 `Reply` 不发出，计数（调用方已经按超时处理了）。
- 请求的处理方法要么返回回复，要么取 `ReplyTo<T>`，不能两样都做，也不能都不做（分析器检查）。
- 等待实体和它的状态随 Actor 迁移（序列化规则见第 10 节）；`[Durable]` 的还随记录保存。
- 回信走控制通道，不受 `Busy` 限制：它的数量不超过本节点发出的、还在等的请求数（第 11 节）。

---

## 9. 时间与广播

### 时间

**一个时间类型：`TimeSpan`，本进程单调时钟上的时间，下文叫单调时间。**
- 单调时间不是 UTC，不依赖各节点对时。它只在一个进程里有意义。
- **跨进程只传剩余时间**：请求的期限在线上是"还剩多久"；迁移的字节里，延后事件和期限也存剩余时间。接收方用自己的单调时钟重新起算：到期时刻 = 收到时的 `Time.Elapsed` + 剩余时间。
- **停顿不延长请求的预算**（同 ADR-028）：请求在执行之前已经过了期限，就丢弃、计数，不执行，调用方已经按超时处理了。所以框架的期限都算上途中和停顿的时间：
  - 链路记下每个帧进重发窗口的时刻，重放时从请求的剩余时间里减去它在窗口里等的时间；
  - 迁移的字节和热重启的快照里带着"已经等了多久"：占位上和调度队列里的邮件、`ReplyTo` 的期限、等待中的请求的期限，都减去途中和停顿的时间；
  - 用户的延后事件不减：那是 Actor 自己的时间，迁移和热重启的停顿不计入（第 10 节）。
- `Time` 是 world 的单例，由运行时写，用户只读：

  ```csharp
  public record struct Time(long Tick, TimeSpan DeltaTime, int TickRate, TimeSpan Elapsed, DateTime Utc) : ISingletonComponent;
  ```

- **一条时间规则：Actor 里的代码只从 `Time` 读时间。** 先后、延后、超时用 `Elapsed`（单调）；要跨停用、跨进程留下来的绝对时刻（buff 在某日到期、`[Durable]` 的期限、提醒）用 `Utc`，存进 `IPersisted` 组件。
  - `Utc` 来自 world 的 `TimeProvider`（测试里是 `FakeTimeProvider`），每一项开始时读一次，同一项里不变；
  - 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`，分析器给警告；
  - 各节点的 UTC 只差对时的误差，所以框架用到 UTC 的地方（`ReplyLog` 留多久、`[Durable]` 的期限、提醒）都多留一个余量 `DaServer:ClockSkew`，排序和期限从不比较两个节点的 UTC。

- **`Elapsed` 是正在执行的那一项的时间**：处理消息时是它进入 world 的时刻，执行延后事件时是它的到期时刻，系统里是 tick 时刻 t。
- 系统运行期间，`Tick`、`DeltaTime`、`TickRate` 是这个 Actor 类型的值；没有 tick 的 Actor 类型，它们是 0。
- **`after` 从当前项的时间算起**：到期时刻 = `Time.Elapsed` + after，存精确值，不存 tick 号。它在 tick 窗口所在的那一帧执行（第 1 节），从不提前。
- 数据里写秒或毫秒，例如 `Skills.Windup(id)` 返回 `TimeSpan`。
- 为什么用时间不用 tick：改帧率不能改技能的手感。Minecraft 的 `/tick rate` 改了帧率，按帧写的一切都跟着变快变慢；Photon Quantum 的 `FrameTimer.FromSeconds` 让用户写秒，内部存帧。
- 没有 tick 的 Actor：到了时间就执行。
- **帧率每个构建固定**：写在 Actor 类型上，进 `wire.manifest`（第 10 节）。
- **客户端的时间**：只有有 `[TickRate]` 的 Actor 类型推。Presence 确认后，目标先推一条框架通知，带帧长、当前 tick 号和服务器时间（单调时间，客户端只用它的差值，第 7 节）；之后用户的第一份快照对齐到这个 tick。加入另一个有 tick 的 Actor 类型（可能是另一个帧率）、或者 Actor 迁移到别的 world 之后，再推一次。

### 一个计时机制：`Due`

延后的事件和框架的期限（请求超时、加入确认期限、连上就加入的退避）是同一种东西：**带 `Due` 的实体**。

```csharp
// DaServer: data on the entity (saved, migrated); At is on this process's monotonic clock
public record struct Due(TimeSpan At, long Seq) : IComponent;
```

| 谁带 `Due` | 是什么 |
|---|---|
| 延后的事件 | 发起实体的子实体：`ChildOf(发起者)`、`Due`、事件本身 |
| 请求的期限、`[Durable]` 的下一次重问 | 等待实体 |
| 加入确认期限 | 确认前的 Presence 实体 |
| 断线的宽限期 | 带 `Detached` 的 Presence 实体（第 7 节） |
| 连上就加入的退避 | `Connection` 的一个子实体 |

- **`Due` 是组件，排序是运行时的索引**：每个 world 一个 4 叉最小堆，项是 `(At, Seq, EntityId 和版本)`。加 `Due` 时入堆；实体销毁或事件取消时不去堆里找，堆顶出堆时版本对不上就跳过（惰性删除）。EasyEcs 的有序值索引是排好序的数组，每取走队首都要搬动整个数组，所以不用它。
- 堆不保存：`world.Load` 和迁移到达之后，运行时按带 `Due` 的实体重建一次。
- `OrderedPhase` 每一步比较三处的队首：即时链（当前项刚 `Schedule` 的，先执行）、到期堆里不晚于 t 的、邮箱快照，执行最早的那个。执行中新加的、时间不晚于 t 的项，下一步就按顺序取到。序号取自单例 `Ordered<K>`。
- **不带 `after` 的 `Schedule` 不建实体**：事件是 world 链栈里的一个值，当前项返回后依次执行，两个项之间链栈总是空的，所以它从不进快照或迁移的字节。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- 周期性的逻辑写进系统；稀疏的周期（例如每秒跳一次伤害）在事件处理方法里再 `Schedule(…, after:)` 下一次。
- **每个 Actor 待处理的项有一个上限**：待执行的延后事件和等待中的请求合计（框架在根上数的那个个数，第 6 节），默认 65,536，Actor 类型上可以写 `[ActorOptions(MaxPending = n)]`。超过时 `Schedule` 和 `Ask` 抛出，按 `OnError` 处理：一个失控的循环不会吃光内存。

**`Scheduled<T>` 是普通数据**：`record struct Scheduled<T>(EntityId Event, long Seq) where T : struct, IEvent`：延后的事件指向那个子实体；即时链里的事件 `Event` 是空的，按 `Seq` 认。
- 还在等吗：`e.IsPending(s)`；取消：`e.Cancel(s)`（`e` 是发起者）。执行或取消后 `IsPending` 就是 `false`。
- 存进组件的 `Scheduled<T>` 不需要清理，也不会卡住：`EntityId` 带版本，执行或取消后永远不再指向活着的实体。
- 多数时候不用存句柄：`e.IsPending<T>()` 问 `e` 上有没有没执行的 T（查 `e` 的子实体和链栈），`e.Cancel<T>()` 取消它们。"正在施法"就是 `me.IsPending<Strike>()`，不另设组件（第 12 节）。

### 广播

- **`ClientGroup`**：一个值，`root.Group(long key)` 得到：键前面带着这个 Actor 的 `ActorId`，所以两张地图的同号格子不会撞。
- **订阅是数据**：Presence 上的 `DynamicBuffer<Subscribed>`，`Subscribed(ClientGroup Group)`。
  - `e.SetGroups(groups)` 就是改写这个缓冲，`groups` 是 `ReadOnlySpan<ClientGroup>`（可以 `stackalloc`）；
  - 收尾组的 `SyncSubscriptions`（`[Changed<DynamicBuffer<Subscribed>>]`，以及刚加上 `Joined`、刚被接管的 Presence）把这些 Presence 的完整订阅集合发给网关，网关整个替换（幂等，不用记上次发了什么）。只有带 `Joined` 的 Presence 是成员。迁来的 Presence 也由它订阅（迁移到达算作写入，第 10 节）；
  - `e.Push(in group, msg)` 一次序列化，每个有成员的网关节点收到一帧，由网关扇出；`root.Broadcast` 也一样。
- **每个组的发送频率**：`root.Group(key, every: n)`，n 是 tick 数。推给这个组的消息照常一次序列化、追加进 `Outbox` 里这个组的池化帧，但这一帧到了组该发的那个 tick 才交给链路，期间的推送合在同一帧里，一条不少。计数器就在这个组的帧缓冲上，不分配。
  - `every` 只决定这个 Actor 往这个组的帧多久交出一次，不改变组的身份：订阅用 `root.Group(key)`，推送用 `root.Group(key, every: n)`，是同一个组。远处的格子、不重要的实体用低频，近处用高频，同 Unity Netcode 的 `MaxSendRate` 和按重要性排的快照（[optimizations](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/optimizations.html)）、SpatialOS 兴趣查询的 frequency。
  - 只对有 `[TickRate]` 的 Actor 类型有意义，别的 Actor 类型写 `every` 分析器报错。推给同一个客户端的直接推送和低频组的推送之间不保持先后。
  - Actor 停用、迁出，或者节点热重启冻结之前的最后一个 tick，没到点的组帧当场交出，所以记录、迁移的字节和快照里都没有它。
- **整个 Actor**：`root.Broadcast(msg)`，推给它所有带 `Joined` 的 Presence。
- **小组**（公会、队伍）：一个固定键的 Actor，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热重启、升级（EasyEcs 4.6）

组件类型的集合都用 EasyEcs 的 `ComponentSet`（`reference/componentset.mdx`）：注册时建好一次，存在 `Registration` 里，框架按 `Type` 加入它在注册时才知道的类型（`ComponentSet.With(Type)`）。

### 记录

一份记录就是一次 `SaveEntities`，包括：
- 根实体，放在第一个；
- 带至少一个 `IPersisted` 组件的子孙；
- 这些子孙到根的路径上的实体；
- `[Durable]` 的等待实体（第 8 节），连同根上的 `ReplyLog`、`NextDue`：它们是框架的持久化组件。

收集用 `world.Descendants(root)`（深度优先，池建好之后不分配）。保存用的组件集合：`persist = ComponentSet.Create().With(typeof(每个 IPersisted 组件)).With<ChildOf>().With<In<K>>().With<Persisted>()`。

### 激活 epoch：一个 Actor 只有一个激活能写

- **放置版本**：协调者每次因成员变化改动一个 Actor 类型的放置，就把这个类型的放置版本加一，随视图传播。节点之间的每个帧捎带发送方见过的最高版本；一个节点从不按比它见过的更旧的版本激活。
- **激活先占 epoch，占和读是一条语句**（键还没有记录时插入一行占位）：
  ```sql
  UPDATE actors SET epoch = epoch + 1, owner = (:name, :gen), placement = :v
  WHERE key = :key AND (placement < :v OR (placement = :v AND owner = (:name, :gen)))
  RETURNING new.epoch, new.record, old.owner                    -- PostgreSQL 18: OLD in RETURNING
  ```
  存储里记着 `(epoch, 主人, 放置版本)`。从别的主人手里拿走一个键，必须带严格更新的放置版本：同一个版本里第二个来占的节点被拒绝，按过时视图做出的放置也抢不走新主人；自己在同一个版本里重试（语句超时、连接断开后）照样成功。占不到就按退避重来。
- **被挤掉的主人马上隔离**：占的语句返回了被挤掉的主人（`old.owner` 是另一个还活着的节点代数），本节点给它发控制邮件 `Fence(键, epoch)`；那边的框架系统隔离这个根（第 6 节的表），不用等它下一次写记录失败才发现。
- 只有按自己的视图是主人的节点才去占（第 6 节）；不是主人的转发。
- **每次写记录都按 epoch 比较并交换**：存储里的 epoch 不是自己的，写失败。
- **比较失败就隔离**（第 6 节的表）：不写、不进 `Leaving`，销毁整棵子树。Presence 的路由由清理组件释放，客户端 SDK 重新加入，到达真正的主人那里。
- **节点被移出成员视图时**，它在重新加入之前先隔离自己所有的 Actor，再以新的节点代数加入（第 2 节）。成员租约让它在别人重新放置之前就停下（第 2 节）；分区或长停顿之后醒来的节点即使漏过了租约，epoch 比较也拦住它的写。
- `[Stateless]` 和 `Connection` 不占 epoch：它们不写记录。

### 恢复

- 记录头里存着实体数 n。
- `into[0]` 是占位，它就是根（第 6 节）；其余 n−1 个是空实体。然后一次 `LoadEntitiesInto(record, into, markWritten: false)`，再给根加上 `In<K>`、去掉 `Loading`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会改写成新的 id；指向这组以外的 `EntityId` 变成 `Null`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里，包括 `In<K>`（它是 tag，不占列）。所以 `persist` 集合也带上 `In<K>`。
- **恢复用 `markWritten: false`，迁移到达不用**，只在这里写一次：
  - 恢复：读进来的值不算变更，被它覆盖的 prefab 默认值也不算（EasyEcs 4.5），所以激活不会白写一次记录，`ChangeSystem` 不用重置任何基准；
  - 迁移到达用默认的 `LoadEntities`（有占位时 `LoadEntitiesInto`），`markWritten` 为 true：追踪变更的组件都算写入。所以 `[Changed<T>]` 的系统都把迁来的实体当作刚写过：`SyncSubscriptions` 按 `Subscribed` 重新订阅网关（第 9 节），`CellAssign` 重算格子，到达后的第一个检查点在新的 epoch 下写一次记录。
- 占位上 prefab 的默认值是建占位时写的，那时没有系统看得到它，`[Changed<T>]` 不会替它补上。所以没有记录，或者记录缺了根 prefab 带的某个持久化组件（新版本加的）时，激活给根加上 `Unsaved`：这些默认值是新数据，下一次检查点存下来。

### 只写改过的

- **持久化的组件实现 `IPersisted`**：`public interface IPersisted : IComponent, ITrackChanges;`。
  - 一个接口同时说明"要保存"和"要追踪变更"，不会只写了一半；
  - EasyEcs 的生成器和分析器用 `AllInterfaces` 判断 `ITrackChanges`（`Symbols.Implements`），继承来的也认，所以生成的成员把写当成写、把读当成读；
  - EasyEcs 看不到的只有别的生成器补上的接口，所以它必须是用户写在源码里的接口，不是由特性推出来的。
- `ChangeSystem` 用注册时按所有 `IPersisted` 类型建好的 `QueryFilter.ChangedAny` 和 `RemovedAny`（EasyEcs 4.7 计划）：任何一个持久化组件被写或者被删，这一行访问一次，给根实体标 `Unsaved`。
- `CheckpointSystem` 按配置的检查点间隔，把带 `Unsaved` 的根 `SaveEntities` 写一次（`[ForEach(Slices = N)]`：每次一片，写入摊在整个间隔里，不一齐涌向存储），按 epoch 比较并交换；写成后去掉 `Unsaved`。留着 `[Durable]` 的回复或请求的根（第 8 节）不等间隔，当场写，写成后放出它们。根上有 `NextDue` 时，它的值同时写进记录的 `next_due` 列（`DueScan` 按它唤醒，第 8 节）。
- **`Unsaved` 是根上的 tag，这是有意的**，虽然 EasyEcs 的 `ecs-principles.mdx` 第 5 条说变更检测用变更戳和 `[Changed<T>]`，不用加了又删的标记：
  - 要回答的问题是"这个 Actor 的记录要不要写"，答案属于根：子孙的变更（写、删、销毁）都要汇总到根上，记录也是按根整份写的。`[Changed<T>]` 只能回答"这一行这一趟写过没有"，回答不了"这棵树上次写成之后改过没有"；
  - 它要活过多个 tick：写记录失败时 tag 一直留着，直到某一次写成功，所以不能每 tick 从变更戳重新推出来；
  - `CheckpointSystem` 用 `[With<Unsaved>]` 只看这些根，没改过的 Actor 不花任何开销。
- 带持久化组件的子孙，在同一趟里加上框架的清理组件 `Persisted(EntityId Root)`。新建的子孙在第一次写入时补上；加载的子孙不算写入，所以 `Persisted` 在 `persist` 集合里，随记录一起恢复，`Root` 由加载改写成新的 id。
- 子孙被销毁（例如物品用掉了）时，框架组 `Cleanup` 里的清理系统（`[With<Destroyed>, With<Persisted>]`，不带 `In<K>`，第 6 节）按 `Root` 给根实体标 `Unsaved`，然后移除自己。所以删除也会写进记录。框架自己销毁整棵子树时先移除 `Persisted`，不触发它（第 6 节的表）。

### 序列化规则（Nino）

| 数据 | 要求 |
|---|---|
| `IPersisted` 组件 | `[NinoType]`；改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式 |
| `[Durable]` 请求的 `Ask` 状态 | 随记录保存，要求同 `IPersisted` 组件（`[NinoType]`，分析器检查） |
| 其他非托管的组件、本地事件、`Ask` 的状态 | 迁移和热重启时按原始字节搬运，不用标：`wire.manifest` 里每个这样的类型都有布局哈希，两边对不上就不搬字节（下面的迁移、热重启的路径） |
| 托管的组件、事件、状态 | 要有 Nino 格式化器（`[NinoType]`，分析器检查） |

这是唯一的一条序列化规则，别处只引用它。记录里的类型按 `TypeId` 存。

### 迁移

- 保存整棵子树，组件集合是注册时建好的 `migrate = ComponentSet.Create().With(typeof(每个不带 [Transient] 的组件))`：默认都带，标了 `[Transient]` 的除外。
- 等待中的请求、Presence 实体（连同 `Subscribed`）、待执行的延后事件随 Actor 一起迁移。
  - `Due` 在字节里是剩余时间，到达后按新 world 的单调时钟重新起算，再按它的 tick 边界归入 tick 窗口；已经过期的在第一个 tick 执行（第 9 节）。`Due.Seq` 到达时用新 world 的序号重新编号，保持它们之间原来的先后，然后重建到期堆。
- **到达时占一个新的 epoch**：从字节里带来的 epoch `e` 比较并加一（同一条 `UPDATE … RETURNING`，不读记录），主人改成本节点；条件是 `epoch = e OR (epoch = e + 1 AND owner = (本节点名, 代数))`，所以这条语句重试时、或者到达后本节点热重启再来一次时，照样成功。占到之前，到达的子树像占位一样不运行，邮件排队；比较失败（别人已经占了）就隔离这份到达。所以迁移之后，源节点的任何迟到的写都会失败。
- **迁移头里带着每个组件的布局哈希**（来自 `wire.manifest`）。目标节点的布局有一个对不上（滚动升级时新旧版本混跑），就不搬字节：源节点改为停用、写记录，目标节点从版本兼容的记录加载。这时只有 `IPersisted` 的数据留下；Presence 丢失，由 SDK 重新加入。
- 源节点销毁子树前先移除 `Presence` 和 `Persisted`：路由不释放，根也不标 `Unsaved`（第 6 节的表）。
- **墓碑**：源节点在迁移后保留一段时间的 `(键 → 新节点)`，这个键的所有邮件都转过去（控制消息、回信和普通消息都是）；墓碑还在时本节点不激活这个键。墓碑指向的节点已经离开视图时，按当前的放置转发。
- 停用写记录时不保存待执行的延后事件：需要跨停用保留的状态，写进 `IPersisted` 组件。
- 到达后的第一条邮件是 `Activated(Activation.Migrated)`。
- 跨 world 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。
- 重新平衡引起的迁移限速：每个节点同时迁出的 Actor 有上限，节点增减时不会一下子搬空。

### Native AOT

节点可以用 `PublishAot=true` 发布成原生程序（ADR-083），也可以照常 JIT 运行。两者行为相同：都不在运行时加载程序集，改代码都走热重启或滚动升级（下面）。

- **生成的注册是封闭类型，不用反射**：`Registrations.Create()` 在用户的程序集里写出每个封闭的类型（`.Actor<Map>()`、分发表、job、`world.AddSystem<…>()`），框架按 Actor 类型的系统也由生成的代码写出封闭的实例。没有反射、`MakeGenericType`、`Activator`、程序集扫描，也没有模块初始化器。
- 框架的泛型系统不写 `[ForEach]` 方法，在 `OnUpdate` 里用 EasyEcs 的泛型入口跑 job，所以 EasyEcs 不需要在运行时用 `MakeGenericType` 封闭它们的代码（EasyEcs D20：只由别的程序集封闭的泛型系统才走这条路，AOT 下不行）。用户的泛型 `[ForEach]` 系统只在别的程序集里封闭时，DaServer 的分析器报错（第 13 节）：EasyEcs 对这条路不报警告，错了只会在运行时失败。
- 序列化都是 Nino 生成的格式化器；HTTP 平面和它的 JSON 规则见 [architecture.md 第 8 节](architecture.md#8-native-aot)（ADR-083）。
- 发布要求 0 警告，不接受压掉任何警告。EasyEcs 4.6 带追踪组件（`IPersisted`）和禁用组件的程序发布时没有警告（EasyEcs 的 CI 以警告为错误发布并运行原生程序，`faq.mdx` 的 "Native AOT"）；W8 要验证的只剩第三方包（[architecture.md 第 8 节](architecture.md#8-native-aot)）。

### `wire.manifest`

- 每个工程写出自己的一段。
- Server 的一段：Actor 类型、承载它的服务器类型、帧率、身份、连上就加入的 Actor 类型、每条消息的接收方、发送方、回复类型、是否 `durable`、事件、状态类型、持久化组件、每个组件的布局哈希、推送；客户端消息的路由（带 `[To]` 的写明参数）、事件和 `Replied` 的处理、`Activated` 的处理、对 tag（`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）的消费。
- 热重启按它比对新旧版本，选走哪条路（下面）。
- AppHost 只读 `actor` 和 `hosted-by`。

### 滚动升级

- 旧版本节点收到不认识的消息，返回 `NotHandled`。调用方在重新解析放置之后可以重试一次：放置可能已经换到新版本的节点。
- 旧版本的网关收到不认识 `TypeId` 的客户端消息，回 `NotHandled`；SDK 在握手里带上自己的 `wire.manifest` 哈希，收到 `NotHandled` 时向应用报告版本不一致，而不是一个普通的失败。
- **回滚要安全**：`{cluster}-upgrade-check`（[deployment.md 第 3 节](deployment.md#3-jobs)）还检查回滚：新版本会往记录里写旧版本保留不了的字段（旧版本读进来再写回就丢掉）时拒绝，除非强制。
- 有状态 Actor 的节点组（例如地图）先滚完，协调者才恢复按负载的迁移：混跑期间的迁移只能退回到停用加记录。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点代数由协调者分配（第 2 节），所以同名的节点重启后，不会被当成原来那个。
- 新旧版本的帧率可以不同：延后事件跨进程时存的是剩余时间。
- 组件布局不同的节点之间，迁移退回到停用加记录（上面的迁移）。

### 热重启

改代码就换二进制：节点在原地换成新版本的进程，Actor 不重新激活，客户端不断线。JIT 和 Native AOT、每个平台都是同一个机制：**快照文件**（ADR-084、ADR-088）。热重启的做法只写在这一节。

- **武装只做准备**：运维命令 `{cluster}-restart`（第 2 节）带上新版本的 `wire.manifest`，经节点的管理端点武装节点。节点比对新旧两份，选一条路，在命令的结果里报告；协调者在视图里记下这次武装的随机数。武装之后没有等来 SIGTERM，过一段时间就静默作废，什么也不发生。

  | 比对结果 | 路径 | 停顿 |
  |---|---|---|
  | 每个组件的布局哈希相同；旧版本有的事件、`Replied` 组合、`Activated` 的处理方法和对 tag 的消费，新版本都还有；客户端路由不变 | **原地**：快照 | 目标几百毫秒（ADR-084） |
  | 布局变了，但只变在新版本读得了记录的组件上（`VersionTolerant`、`Keyed` 的 `IPersisted`，或者不持久化的组件），或者去掉了上一行的处理方法 | **经记录**：所有 Actor 停用、写记录，重启，按需从记录激活 | 更长；不持久化的状态按停用的规则丢掉，Presence 由 SDK 重新加入 |
  | 承载关系（`[Hosts]`）或客户端路由变了 | 拒绝；走**滚动升级**（上面），路由变了要连网关一起升级 | — |

  - 去掉的处理方法可能正有数据在等：待执行的事件、等待实体、正在离开的 Presence，所以原地不接受它。
  - 没配 `DaServer:SnapshotPath` 的节点没有原地这条路：命令报告它，走经记录。
  - **拒绝武装**：有成员在 `Leaving` 或 `Joining`（放置正在变）；全集群唯一的热重启许可已经发给了别的节点（第 2 节）；工作集加上快照的估计大小超过 cgroup 内存上限的约 80%（快照目录是内存盘时，它也算在这个上限里）。
  - 一个节点组逐个节点热重启，协调者排在最后；期间新旧版本混跑，规则同滚动升级。
- **步骤**：
  1. **SIGTERM 才开始**：武装状态下收到 SIGTERM（Windows 服务是停止命令），节点向协调者宣布成员状态 `NodeRestarting`（带预计停顿和武装的随机数）。协调者确认之后，期限才开始算，节点才冻结。期限由协调者按自己的单调时钟计时，其他节点只照它的决定做。
  2. **冻结前的最后一个 tick**：框架给每个 Actor 的根加上 `Restarting(TimeSpan ExpectedPause)`，用户的系统按需要反应（下面）；同一个 tick 的 `Settling` 去掉它，所以快照里没有 `Restarting`。
  3. 每个 world 在两个 tick 之间冻结：不再执行任何项，等在途的存储操作（写记录、占 epoch）有结果：写成的去掉了 `Unsaved`，没写成的还带着，随快照过去，新进程的下一个检查点接着写。这个等待有上限，过了就放弃（下面）。**冻结只停 world 的线程**：IO 线程照常收帧，放进调度队列，冻结期间发来的消息不被拒绝，也不用让客户端或别的节点停发。
  4. 进程停止读链路，这一刻就是切换点。快照先写进 `DaServer:SnapshotPath` 目录里的临时文件，`fsync`，再改成正式的文件名（普通的文件写入：几 MB 用不着内存映射），然后进程退出。快照里有：
     - 头：节点名、节点代数、武装的随机数、旧版本和武装时新版本 `wire.manifest` 的哈希、每个组件的布局哈希、冻结时的 `Environment.TickCount64` 和内核的 boot id、校验和；
     - 每个 world：`world.Save` 的字节（实体连同 id 和版本、组件、`DynamicBuffer`（包括占位上等着的 `Mail`）、单例；不含系统、索引、写入戳，EasyEcs `guide/saves.mdx`）、调度队列里还没进 world 的邮件、`Time.Elapsed`；即时链在两个 tick 之间是空的，`Outbox` 在冻结前的最后一个 tick 交出了所有的帧（包括没到点的组帧，第 9 节）；
     - 节点的运行时状态：调用 id 的序号、墓碑、连接戳的计数；每条链路收到的字节数，发出的偏移和还没确认的尾巴；邮件和期限已经等了多久（第 9 节）。
  5. 平台用新的二进制或镜像重启进程（下表）。新进程找属于自己节点名的快照，逐项检查：校验和对；boot id 相同（机器没重启过）；按 `TickCount64` 算出的停顿没超过期限；自己的 `wire.manifest` 哈希等于武装时的新版本哈希（换错了镜像就不加载）。不合格就删掉它，以新的代数加入，走经记录。合格就流式加载（`world.Load(bytes, markWritten: false)`，边读边装），接上时钟、加上系统、重建到期堆和调用 id 索引，然后**在任何 world 运行之前**删掉这个文件：快照只用一次，加载之后再崩溃，下次启动只能经记录。
  6. **`Rejoining`**：新进程以同一个身份（节点名，代数）重连，带着武装的随机数做恢复握手；它的 world 保持冻结，直到协调者回答（期限只在这里判定一次）：
     - `Readmit`：只给当前的随机数，给了就清掉。节点先核对一遍：根所在的槽现在的主人已经不是自己的（期间放置变了），隔离或者迁出这些根；然后各 world 开始跑。其他节点认出同一个身份，按快照里的字节数续上各条流，重放各自的重发窗口：就是今天链路断了重连时的续流，不重复也不丢。对端收到这个身份的 Hello 时清掉对它的重连退避，马上连回来。
     - `Gone`：期限已过、随机数不对，或者已经有更大的代数：丢掉加载的 world，不写，以新的代数加入，从记录激活。
  7. 第一个 tick，每个根带着 `Resumed(TimeSpan Pause, bool Aborted)`，`Pause` 是实测的停顿；有 `[TickRate]` 的 Actor 给带 `Joined` 的 Presence 重推时间通知（第 7 节）；收尾组 `Settling` 去掉 `Resumed`。系统在有序阶段之后跑，所以看到 `Resumed` 时，停顿期间到的消息已经执行过了。
- **放弃**：冻结等过上限，或者快照写失败，节点撤回 `NodeRestarting`；每个根在下一个 tick 带着 `Resumed(Pause: 0, Aborted: true)`，用户的系统可以撤销对 `Restarting` 的反应。然后节点照普通的优雅停止退出（所有 Actor 停用、写记录，ADR-063），新进程以新的代数加入，等于走了经记录。
- **用户的反应是数据**：
  ```csharp
  [UpdateInGroup<Map>]
  public class RestartNotice : EcsSystem
  {
      [ForEach, With<Restarting>]                                          // the last tick before the freeze
      static void Warn(Entity map, in Restarting r) => map.Broadcast(new MapRestarting(r.ExpectedPause));
  }
  ```
  - 不写系统时什么也不做：停顿期间的消息在调度队列和对方的重发窗口里等着，之后照常执行（过了期限的请求按第 9 节丢弃）。秒杀这类就是这样。
  - 例如 MMO 推一条通知，再让 Presence 离开（`Leave()`），客户端按通知里的停顿，大约一秒后重新进图；
  - 要在重启后全量走一遍的系统，过滤 `[With<Resumed>]`；`Resumed.Aborted` 为真时，是撤销的时候。
  - 网关上的系统读节点单例 `RestartingNodes`，见第 7 节。
- **时间**：新进程的 world 时钟从保存时的 `Time.Elapsed` 接着走，所以用户的延后事件不用改写，剩下的时间不变，停顿不计入；框架的期限减去停顿（第 9 节）。
- **所有权**：
  - 不重新占 epoch，不隔离：每个 Actor 的 epoch 在它的根上，随快照过去；存储里的 epoch 没变，新进程写记录照样比较成功。
  - 任一时刻只有一个进程跑 world：旧进程写完快照才退出，新进程在它退出之后才启动，加载之后还要等 `Readmit`。
  - `NodeRestarting` 期间其他节点不把它当作失效：不判 `Suspect`、`Dead`；协调者钉住它所有的槽，不规划迁入或迁出它的移动，不重新激活。发给它的消息留在各自链路的有界重发窗口里（网关上的客户端消息也是）。窗口满了：单向消息丢弃并计数，请求立刻以 `Busy` 失败（发问方收到 `Status = Busy` 的 `Replied`，`RetryAfter` 是预计停顿），重试由发问方的代码按第 11 节决定；请求的期限照常走，可能超时。
  - 有成员在 `NodeRestarting` 时，别的节点的离开在自己的停止预算之内等它回来再走：两件事同时改放置，第 6 步的核对就对不上了。
  - 期限过了，协调者宣布它离开：按节点离开视图处理，算作 `Left`，不算 `Dead`，所以两个超时的热重启不会让多数派以为自己成了少数（ADR-064）。Actor 在别处占新的 epoch，从记录激活；迟到的新进程收到 `Gone`。
  - 协调者自己热重启时，次老的成员接任，期限从头算（第 2 节）。
- **平台**：重启都由平台自己的监管者做，DaServer 不另起管理进程，也不自己起继任进程。

  | 平台 | 快照目录（`DaServer:SnapshotPath`） | 怎么重启 |
  |---|---|---|
  | Kubernetes | `medium: Memory` 的 `emptyDir` | 发布改模板里的镜像；`{cluster}-restart` 的 rollout Job 逐个改运行中 Pod 的容器镜像（StatefulSet `OnDelete`，同一个 Pod、UID 和 IP，ADR-085） |
  | Docker Compose | 每个节点自己的命名卷 | 部署机器上逐个节点 `docker compose up -d --no-deps map-0` |
  | systemd | `RuntimeDirectory=` 加 `RuntimeDirectoryPreserve=restart`（`/run` 下的 tmpfs，重启时保留） | `systemctl restart` |
  | launchd、Windows 服务 | 本地目录 | `launchctl kickstart -k`、`Restart-Service` |
  | 本地 Aspire | 每个节点一个临时目录 | 仪表盘里的 `{cluster}-restart`：重新编译，逐个节点武装，再用 Aspire 资源的重启命令（不是 `aspire do`：它跑在一个新进程里，碰不到正在运行的 AppHost） |

  DaServer.Aspire 设置 `DaServer:SnapshotPath`；裸机上由 unit、plist 或服务的配置设置（[deployment.md 第 0 节](deployment.md#0-launch-flows)）。每个平台的重启都先发停止信号（SIGTERM，或 Windows 服务的停止命令），所以"武装 + 平台的重启"就是热重启。
- **写入戳**：新进程用 `world.Load(bytes, markWritten: false)` 加载（EasyEcs 4.6，`guide/saves.mdx` 的 "Restart"，D39）：加载的行没有写入戳（tick 0），也不算移除过；系统加在加载之后，第一次运行看的是 tick 1 起的写入（`guide/change-tracking.mdx` 的 "Ticks"）。
  - 所以 `ChangeSystem` 不会给每个根标 `Unsaved`，Actor 不会把记录重写一遍；真正没写的记录由带过来的 `Unsaved` 负责。
  - **用户看得到的后果**：热重启之后，所有 `[Changed<T>]` / `[Removed<T>]` 方法（包括用户的，例如 AOI、`SyncSubscriptions`、`CellAssign`）都不把加载的行当作变更。由追踪组件算出来的组件本来就随快照过去，一般不用重算；确实要全量走一遍的，用 `[With<Resumed>]`。
  - `AddSystem(system, changesFromNow: true)`（EasyEcs 4.6）只给运行中途才加入的系统，例如功能开关打开时才加上的。框架注册时加的系统都用默认值。
  - `world.Restore(snapshot)`（`Capture` / `Restore`）总是把恢复的行算作写入：只用于测试，不用于热重启。
- **网关不热重启**：`Connection` 钉在套接字上，换进程连接就断。网关先扩后缩、分批 `Retire`，客户端不断线（第 7 节，[deployment.md 第 2 节](deployment.md#stateless-groups-and-gateways-deployment)）。
- **`[Stateless]` 的 Actor 类型**没有要交接的状态：可以单独部署成节点组，先起新节点、再停旧节点，零停顿。

### 不重启就能改的

两种改动都只是换数据，在两个 tick 之间生效：
- **配置表**（调参的表）：一个 world 的单例，处理方法和系统取 `in`（第 5 节）；节点读入、校验新版本后，每个 world 在两个 tick 之间换上；版本号写在成员信息里。
- **功能开关**：二进制里编译好的几种做法，由一个单例或 tag 选（处理方法和系统按它过滤）；开关就是改这份数据。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。Actor 在新节点占到新的 epoch，从记录激活，最多丢一个检查点间隔的改动（`[Durable]` 的请求回复过的不丢，第 8 节）。`Connection` 看到节点离开视图，删路由，`PresenceLost(槽号)` 给 SDK，SDK 重发加入消息（第 7 节） |
| 两个节点都以为自己有同一个 Actor（分区、长停顿之后） | 只有占到最新 epoch 的能写；被挤掉的收到 `Fence` 或者写记录时比较失败，被隔离：不写、不进 `Leaving`，路由由清理组件释放，SDK 重新加入到真正的主人 |
| 节点确认不了成员视图超过租约（包括只有它听不到协调者的单向分区） | 每个 world 在有序阶段前发现租约过期，冻结，不再执行、不再写；其他节点在 max(`DeadAfter`, `SuspectAfter` + 租约) 之后才重新放置（第 2 节） |
| 协调者宕机 | 次老的成员在协调者被判 `Suspect` 后开始确认心跳，租约不会过期；它接任后正在进行的热重启期限从头算（第 2 节） |
| 节点被移出视图后又回来 | 先隔离自己所有的 Actor，再以新的节点代数加入 |
| 按过时的视图去激活 | 节点不按比见过的更旧的放置版本激活；存储里的放置版本更新、或者同一版本已经有别的主人，占 epoch 被拒绝；不是主人的节点本来就转发，不激活 |
| 整个集群冷启动、几个种子同时启动 | 代数取 max(见过的 + 1, Unix 毫秒)，不重复；只有序号最小的种子自己组成集群，成员数够了才激活（第 2 节） |
| 网关滚动升级或缩容 | 不断线：先扩后缩，分批 `Retire`，SDK 先连上新网关、接管各个 Presence，`HandedOff` 栅栏保持每个 Actor 的推送顺序，再关旧连接（第 7 节） |
| 网关宕机、客户端断线 | 客户端带令牌重连，得到新的 `Connection` 和更大的连接戳；目标上的 Presence 带着 `Detached` 等着，SDK 重发各槽的加入消息，新连接接管它们，快照系统重推一份；宽限期过了没人接管才 `Leave()`（第 7 节） |
| 半开连接时客户端已经连到别处 | 连接戳更大的胜出（`Takeover.NewestWins`），目标发 `TakeOver`，旧的 `Connection` 关闭；旧连接再来加入，戳更小，收到 `Superseded`；同一个连接重试，戳相等，幂等再回复 |
| 另一台设备登录了同一个账号 | 签发序号更大的接管；旧设备的连接以 `Superseded` 关闭，SDK 报给应用，不自动重连；只是刷新过令牌的旧设备排不到新登录前面（第 7 节） |
| 连上就加入失败 | 框架按 `RetryAfter` 或带抖动的退避重试，超过次数以 `Unavailable(retryAfter)` 结束会话 |
| `Join()` 时没有客户端上下文 | 返回 `NoClient`，处理方法决定怎么回复 |
| `Join()` 时已有的 Presence 连接戳更大 | 返回 `Superseded`，处理方法决定怎么回复 |
| 加入后路由没写成（客户端已断开） | 确认期限到，`Leave()` |
| SDK 重发加入消息失败 | SDK 带抖动退避重试，超过次数放弃；应用只看到 `PresenceLost` |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活；连续失败若干次后留下激活失败的占位，到期前立刻回 `Busy`（第 5 节） |
| 存储慢或不可用 | 存储降级：不做空闲停用，`Saving` 的占位有上限；同时占 epoch 的激活有上限，带抖动退避；等过期限的占位立刻回 `Busy`；节点照样就绪，已激活的 Actor 照常运行（第 6 节） |
| 主体缺少组件参数，或者不满足过滤 | 不调用处理方法；请求以 `Skipped` 失败，单向消息和事件跳过（`TestActor<K>` 里缺少组件参数抛出，那是代码错误；过滤不满足照常跳过） |
| 请求超时、`ConnectionLost` | 不等于没执行：普通的请求由发问方决定，用同一个调用 id 重问；`[Durable]` 的由框架重问到有结果或者 `Expired`（第 8 节） |
| 请求在执行前已经过了期限（排队、重发窗口、热重启、迁移途中） | 丢弃并计数，不执行（第 9 节） |
| `[Durable]` 的接收方在记录写成之前宕机或被隔离 | 回复不发，改动和 `ReplyLog` 也没写进存储；发问方重问，真正的主人执行一次 |
| `[Durable]` 的发问方在回信前宕机 | 等待实体在记录里（请求在它写成之后才发出）；`DueScan` 唤醒 Actor，框架用同一个调用 id 再问，接收方从 `ReplyLog` 回答（第 8 节） |
| `[Durable]` 的请求到了期限还没有结果 | 回信是 `Expired`：结果未知，框架不再问；处理方法按业务收尾（例如销毁掉落、补偿） |
| `ReplyLog` 满了 | 新的 `[Durable]` 请求回 `Busy`（确定没执行），`RetryAfter` 是最早一项过期的时刻 |
| 拿着回复令牌的 Actor 在回复前停用 | 令牌在 `IPersisted` 组件里，重新激活后期限内照样回复；否则调用方按期限超时 |
| 过了期限才 `Reply` | 不发出，计数 |
| 发问的实体在回信前被销毁 | 等待实体一起销毁，回信丢弃 |
| 停用时写记录失败 | 重试；这期间到达的消息留在 `Saving` 占位上，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；晚到的邮件由墓碑转发（墓碑指向的节点已经离开时按当前放置）；目标节点宕机时，在途的单向消息可能丢失 |
| 迁移两端的组件布局不同 | 不搬字节：停用、写记录，目标从记录加载 |
| 新版本去掉了还在用的处理方法 | 热重启不走原地，改走经记录（第 10 节）；分发时仍然找不到就是 `NotHandled` |
| 武装之后没有等来 SIGTERM | 武装静默作废（第 10 节） |
| 热重启冻结时存储挂住、快照写失败 | 放弃：撤回 `NodeRestarting`，根带 `Resumed(Aborted: true)`，节点优雅停止，新进程经记录（第 10 节） |
| 热重启后快照不合格（校验和、节点名、boot id、过期、镜像不是武装的那个） | 新进程删掉它，以新的代数加入，Actor 从记录激活 |
| 热重启加载之后又崩溃 | 快照在 world 运行前已经删掉，下次启动经记录 |
| 热重启超过协调者的期限还没回来 | 协调者宣布它离开，算作 `Left`；迟到的新进程收到 `Gone`，丢掉加载的 world，以新的代数加入 |
| 热重启期间放置变了 | 协调者本来不动它的槽；回来时的核对隔离或迁出主人已经不是自己的根（第 10 节） |
| 滚动升级中收到 `NotHandled` | 重新解析放置后可以重试一次；客户端消息到了旧网关，SDK 报告版本不一致 |
| 邮箱满、占位上等着的邮件满、加入好之前 `Connection` 上排队的满 | 请求回 `Busy`；单向消息丢弃并计数；客户端的消息由网关回绝 |
| 发往一个节点的链路窗口满（例如它在热重启） | 单向消息丢弃并计数；请求立刻以 `Busy` 失败（发问方收到 `Status = Busy` 的 `Replied`） |
| 目标节点变慢 | 它的自适应在途上限到了，新请求立刻 `Busy`（下面） |
| 一个 Actor 收到的消息远多于邻居 | 每批里它的项数有上限，其余留到下一批；同 world 的其他 Actor 照常推进（第 1 节） |
| 新连接一下子太多（网关或节点宕机后） | 网关的接纳令牌桶回 `Busy(retryAfter)`，SDK 指数退避加完全随机抖动（下面） |
| 控制通道 | 不受 `Busy` 限制，但有界：Presence 的控制消息不超过本节点的 Presence 数，回信不超过本节点还在等的请求数 |
| 一个 Actor 待处理的项超过 `MaxPending` | `Schedule`、`Ask` 抛出，按 `OnError` 处理 |
| 客户端读得太慢 | 断开（ADR-035），按断线处理 |
| 离开通知丢失 | 链路重连时核对，或者网关离开视图时，按断线处理 |
| Presence 没经过 `Leave()` 就销毁 | 清理组件释放路由，SDK 收到 `PresenceLost` 后重新加入 |
| `[On<T>]` 抛出异常 | 不回滚，见第 5 节：请求失败，即时链里还没执行的事件作废；`Restart`（有持久化组件的 Actor 类型的默认）丢掉内存里的状态，保留排队的邮件，从最后的记录重新激活，改了一半的项不会进检查点；`Resume`（其余的默认）照现状继续；`Connection` 直接断开 |
| 一项的即时链超过 1,024 个 | 按 `OnError` 处理 |
| 系统抛出异常 | `SystemErrorPolicy.Disable`：这个系统停用，异常留在 `system.Error`，健康检查报告；下一次热重启或升级换上新的系统 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 过滤不满足就不调用；方法体里重新校验目标后作废 |

### 每条路径保证什么

| 路径 | 会丢吗 | 会执行两次吗 | 去重 | 谁重试 |
|---|---|---|---|---|
| `Send`（Actor 之间的单向消息） | 会：最多一次。节点宕机、链路窗口满、迁移途中目标宕机时丢失 | 不会 | — | 没人 |
| `Ask`（普通请求） | 请求会丢，但一定有结果：回复、拒绝或者 `TimedOut` | 会：结果未知后用同一个调用 id 重问时 | 接收方的处理方法按 `reply.Call` 自己记 | 发问方的代码（`Transient` 时延后 `RetryAfter`）；框架只重试转发一跳上的（下面） |
| `[Durable]` 的 `Ask` | 不会：等待实体在记录里，宕机后被唤醒再问；到期限还没有结果是 `Expired` | 不会：期限内一个调用 id 只落定一次 | 框架：接收方的 `ReplyLog` | 框架：`Transient` 时和重新激活后，按 `RetryAfter` 和抖动，计入重试预算 |
| `Schedule(ev, after:)` | 会：停用、节点宕机时丢失（不在记录里）；迁移、热重启不丢 | 不会 | — | 没人；要扛住宕机的定时用 `DaServer.Reminders`（第 15 节） |
| 组件的状态 | 会：宕机时丢最多一个检查点间隔的改动；`[Durable]` 落定的不丢 | — | — | — |
| `Push` | 会：断线、`Detached` 期间、读得太慢被断开时丢失 | 不会 | — | 没人；重新加入后快照系统补全（`Joined.MissedPushes`） |
| `Broadcast`、`ClientGroup` | 同 `Push`；只发给带 `Joined` 的 Presence | 不会 | — | 同 `Push` |
| 客户端请求（`IFromClient<R>`） | 一定有结果：回复、拒绝、`TimedOut` 或者 `ConnectionLost` | 会：应用用同一个调用 id 重试时 | 处理方法按 `reply.Call`，即 `(ClientId, 客户端调用 id)` | 应用：`client.Ask(msg, call: id)` |
| 客户端单向消息（`IFromClient`） | 会：最多一次。断开时没送达的丢失；断线后到这个槽重新加入之前，不带 `[To]` 的被网关丢弃并计数（`NotJoined`，第 7 节） | 不会：SDK 不重发 | — | 没人 |
| 加入（`[JoinOnConnect]`、SDK 重发的加入消息） | 不会：失败就重试，直到成功或者 `Unavailable` | 无害：`Join()` 按连接戳幂等，`GetOrSpawn` 找到已有的实体 | 连接戳 | 框架（`[JoinOnConnect]`）和 SDK（各槽的加入消息），按 `RetryAfter` 和抖动 |
| 转发的一跳 | 同它转发的消息 | 不会：只重试确定没执行的拒绝（`NoOwner`、`NotHandled`） | — | 框架，计入重试预算 |
| `ICluster` 的 `Ask`、`Send` | 同 `Ask`、`Send`：调用方在 Actor 外面，不持久化 | 同 `Ask`；带 `CallId.From(外部键)` 问 `[Durable]` 的请求时不会 | `CallId.From(外部键)` | 外部调用方 |
| 存着的 `ReplyTo<T>` 跨过停用 | 令牌在 `IPersisted` 组件里不丢，重新激活后期限内照样回复；在别的组件里就丢了，调用方超时 | 不会：同一个令牌只有第一次回复有效 | — | 普通请求是发问方，`[Durable]` 是框架 |

先后的保证在第 1 节；期限怎么传、停顿不延长预算在第 9 节。

### 重试、预算与过载

- **`Status` 的两个分类**是 `Status` 上的扩展属性 `s.NotExecuted` 和 `s.Transient`：

  | `Status` | `NotExecuted`：确定没执行，再发一次不会执行两次 | `Transient`：过一会儿可能成功 |
  |---|---|---|
  | `Ok` | | |
  | `Busy`（带 `RetryAfter`）、`NoOwner` | 是 | 是 |
  | `NotHandled`、`NoActor`、`Filtered`、`NotJoined`、`Skipped` | 是 | |
  | `TimedOut`、`ConnectionLost` | 结果未知 | 是：只能用同一个调用 id 重问 |
  | `Expired` | 结果未知，`[Durable]` 的期限到了，框架不再问（第 8 节） | |
  | `NoClient`、`Superseded`、`AlreadyConnected`、`Unavailable` | 加入和连接的结果（第 7 节） | |

- **框架自己重试的只有**：转发的一跳上确定没执行的拒绝（例如按过时视图转发后收到 `NoOwner`，滚动升级中的 `NotHandled`）、`[Durable]` 的请求（第 8 节）、加入（`[JoinOnConnect]`，第 7 节）。转发和 `[Durable]` 的重试共用每个节点一个重试预算：令牌桶，约为请求数的 10%（同 gRPC 的重试节流）；桶空了，转发把拒绝原样交回调用方，`[Durable]` 等下一次退避。普通请求的重试是发问方代码的事：`Replied` 处理方法按 `Status` 决定，要重试的用 `Schedule(…, after: a.RetryAfter)` 延后再问（第 13 节的提示）。
- **`Busy` 带 `RetryAfter`**：SDK、`[JoinOnConnect]`、SDK 的重新加入和 `[Durable]` 的重问都按它等；没有时按指数退避加完全随机抖动。
- **接纳新连接**：网关按令牌桶接纳新连接，桶空了回 `Busy(retryAfter)`；SDK 按指数退避加完全随机抖动重连。迁移引起的重新加入也走这个桶。
- **慢的目标节点**：框架按目标节点记着在途请求数和延迟，算出一个自适应的在途上限（内部的 `PeerHealth`）；超过的新请求立刻以 `Busy` 失败，不在一个慢节点上堆到超时。
- **热点 Actor**：每批邮件里每个 Actor 的项数有上限（第 1 节）。

### 定时的默认值

| 定时 | 默认 | 说明 |
|---|---|---|
| 成员租约 | 6 秒 | 从发出心跳时算；协调者的确认回显了心跳计数才续（第 2 节） |
| `SuspectAfter` | 3 秒 | 心跳多久没有进展判 `Suspect`（ADR-064） |
| `DeadAfter` | 10 秒 | 判 `Dead`；重新放置在 max(`DeadAfter`, `SuspectAfter` + 租约) 之后，默认就是 10 秒 |
| 节点链路的静默关闭 | 3 秒 | max(`SuspectAfter`, 三个 gossip 间隔)；之后重连、重放重发窗口（ADR-053） |
| 断线的宽限期 | 30 秒 | `[ActorOptions(DetachGrace = …)]`（第 7 节） |
| `DaServer:AskTimeout` | 5 秒 | 不写 `deadline:` 的 `Ask` 的超时，也是 `[Durable]` 每一次问的超时（第 8 节） |
| `DaServer:DueScanInterval` | 1 秒 | `DueScan` 查到时的键的间隔（第 8 节） |
| `DaServer:ClockSkew` | 1 分钟 | 用到 UTC 的地方多留的余量（第 9 节） |
| `RetireGrace` | 10 秒 | 退役的连接多久不走就断开（ADR-026） |
| SDK 的心跳、静默断开 | 15 秒、30 秒 | ADR-053 |
| 网关对静默客户端的断开 | 45 秒 | ADR-053；之后才开始宽限期 |

`SuspectAfter` + 租约不大于 `DeadAfter`：别人重新放置时，旧节点的租约一定已经过期。

---

## 12. 完整例子：连接、进地图、移动、放技能、捡东西、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct EnterMap([To] long Map) : IFromClient<bool>;         // goes straight to that map
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took; to its current map
[NinoType] public record struct CastSkill(int Skill, int Target) : IFromClient;       // Target: a NetId number from Nearby
[NinoType] public record struct PickUp(int Drop) : IFromClient<bool>;                 // Drop: a NetId number; to its current map
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
[NinoType] public record struct NearbyEvent(int Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: the entity's NetId number
public enum NearbyKind : byte { Appear, Moved, Disappear }
```

```csharp
// ── Game.Server: identity, actor types, servers, messages, events ─────────────
[Claims] public readonly partial record struct Account(long Id);                       // decoded from the token
[JoinOnConnect<Account>]                                                               // joined at connect: Account(42) -> Player(42)
public readonly partial record struct Player(long Id) : IActor<Player>
{
    public static Prefab RootPrefab { get; } = new Prefab().SetInventory(0, 40, 0);
}
[TickRate(30)]                                                                         // 30 ticks a second; its systems see only map entities
public readonly partial record struct Map(long Id) : IActor<Map>
{
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetBuffer<Shout>();
}

[Hosts<Player>]     public readonly struct GameServer    : IServer;
[Hosts<Map>]        public readonly struct MapServer     : IServer;
[Hosts<Connection>] public readonly struct GatewayServer : IServer;

[NinoType, From<Map>, Durable] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // lands once: section 8
public record struct Strike(int Skill, EntityId Target) : IEvent;                      // a cast's resolution, after the windup

// ── Game.Server: data ─────────────────────────────────────────────────────────
[NinoType] public record struct Grid(int Size, int CellSize) : IPersisted;              // on a map; IPersisted brings ITrackChanges
[NinoType] public record struct Inventory(int Count, int Capacity, int Last) : IPersisted;   // on a player
public record struct NetIds(int Next) : IComponent;                                     // on a map: the last number given out
public partial record struct NetId(Map Map, int Value) : IChildKey<NetId>;              // what clients call an entity; Key => this is generated
public partial record struct Avatar(Map Map, Player Player) : IChildKey<Avatar>;
public partial record struct Cell(Map Map, int X, int Y) : IChildKey<Cell>, ITrackChanges;   // AOI cell
public record struct Visible : IComponent;
public record struct Position(Vector2 Value) : IComponent, ITrackChanges;               // tracked: CellAssign reacts to writes
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;               // tracked: Diff reacts to writes
public record struct NetSync(Velocity Sent) : IComponent;                               // the velocity last told to observers
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Dead : IComponent;
public record struct Stunned : IComponent;
[NinoType] public record struct Loot(int Item) : IPersisted;                           // what a drop on the map holds
[NinoType] public record struct Looting(ReplyTo<bool> Reply) : IComponent;              // the durable ask's state: saved with the map's record
public record struct Shout(Cell Cell, NearbyEvent Event);                               // news for one cell, kept on the map

public static class Prefabs
{
    static readonly Prefab BodyPrefab = new Prefab().SetPosition(default).SetVelocity(default).SetNetSync(default)
        .SetHealth(100).SetVisible();                                      // what everything walking on a map has
    [Prefab<Map>] public static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);   // a player's position comes from its steps
    [Prefab<Map>] public static readonly Prefab NpcPrefab = new Prefab(BodyPrefab).SetSpeed(3).SetDestination(default).SetSteered();
}
```

```csharp
// ── Game.Server: a map's membership ───────────────────────────────────────────
[Handles<Map>]
public static partial class Maps
{
    [On<EnterMap>]                                                         // [To] Map: the subject is the map's root
    static bool Enter(in EnterMap m, in Account who, in Map map, Entity root, ref NetIds ids)
    {
        var avatar = root.GetOrSpawn(Prefabs.AvatarPrefab, new Avatar(map, new Player(who.Id)),   // a re-sent join finds it
            new NetId(map, ++ids.Next));                                   // used only when spawning (a re-join skips a number)
        return avatar.Join() == Status.Ok;                                 // a newer stamp takes over; the client's previous map leaves it
    }
}

// ── Game.Server: steps a client took ──────────────────────────────────────────
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>, Without<Dead>]                                            // subject: this client's avatar; a dead one is not called
    static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed)
    {
        if (!Steps.IsValid(p.Value, m.At, speed.Value)) { me.Push(new SnapBack(p.Value)); return; }
        p.Value = m.At;                                                    // applied now: later items see it
        v.Value = m.Velocity;
    }
}

// ── Game.Server: combat ───────────────────────────────────────────────────────
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>, Without<Dead>, Without<Stunned>]                       // subject: the caster's avatar
    static void Cast(in CastSkill m, in Map map, Entity me, World world)  // map: this actor's key, whatever the subject
    {
        if (me.IsPending<Strike>()) return;                               // one cast at a time
        if (!world.NetIdIndex.TryFind(new NetId(map, m.Target), out var target)) return;   // the number -> the entity on this map
        me.Schedule(new Strike(m.Skill, target.Id), after: Skills.Windup(m.Skill));   // Zero resolves right after this handler
    }

    [On<Strike>, Without<Dead>, Without<Stunned>]                          // died or got stunned during the windup: not called
    static void Resolve(in Strike s, in Position at, World world)
    {
        if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
        if (!Skills.InRange(s.Skill, at.Value, target.Position.Value)) return;   // out of range: dodged
        ref var hp = ref target.Health;
        hp.Value -= Skills.Damage(s.Skill);
        if (hp.Value <= 0) target.SetDead();                               // structural change last: hp is not used after it
    }
}

// ── Game.Server: picking up a drop (section 8) ────────────────────────────────
[Handles<Map>]
public static partial class Pickups
{
    [On<PickUp>]                                                           // subject: the client's avatar; replies later
    static void Take(in PickUp m, in Account who, in Map map, in ReplyTo<bool> reply, Entity me, World world)
    {
        if (!world.NetIdIndex.TryFind(new NetId(map, m.Drop), out var drop) || !drop.TryGetLoot(out var loot)
            || drop.IsPending<GrantItem>())                                // already promised to someone
        {
            me.Reply(in reply, false);
            return;
        }
        drop.Ask(new Player(who.Id), new GrantItem(loot.Item), new Looting(reply),   // the drop asks: the answer outlives the avatar
            deadline: TimeSpan.FromMinutes(5));                            // [Durable]: re-asked until answered or Expired
    }

    [On<Replied<GrantItem, Looting>>]                                      // subject: the drop; only final outcomes arrive
    static void Granted(in Replied<GrantItem, Looting> a, Entity drop)
    {
        var taken = a.TryGetValue(out var ok) && ok;
        drop.Reply(in a.State.Reply, taken);                               // past the client's own deadline nothing is sent
        if (taken || a.Status == Status.Expired) drop.Destroy();           // Expired: it may have run; lose the drop, never grant twice
    }                                                                      // "no" or a refusal: the drop stays for the next one
}

// ── Game.Server: a player's side ──────────────────────────────────────────────
[Handles<Player>]
public static partial class Items
{
    [On<GrantItem>]                                                        // subject: the player's root; [Durable]: the framework answers
    static bool Grant(in GrantItem m, ref Inventory inv)                   // a repeat itself, and the reply waits for the record
    {
        if (inv.Count >= inv.Capacity) return false;
        inv.Count++;
        inv.Last = m.Item;                                                 // a real bag is a [NinoPooled] collection; kept small here
        return true;
    }
}

// ── Game.Server: every Map tick, after the ordered phase ─────────────────────
[UpdateInGroup<Map>]
public class Steering : EcsSystem
{
    [ForEach(Parallel = true), With<Steered>]                              // path finding is heavy: threads when worlds < cores
    static void Steer(in Position p, in Destination d, in Speed s, Mut<Velocity> v) =>
        v.SetIfChanged(new Velocity(Paths.Toward(p.Value, d.Target, s.Value)));   // stamped only when it changed
}

[UpdateInGroup<Map>, UpdateAfter<Steering>]
public class Movement : EcsSystem
{
    [ForEach, With<Steered>]                                               // per row: a batch Span<Position> would stamp every row
    static void Integrate(Mut<Position> p, in Velocity v, in Time time) =>  // standing still: same value, no stamp
        p.SetIfChanged(new Position(p.Value.Value + v.Value * (float)time.DeltaTime.TotalSeconds));
}

[UpdateInGroup<Map>, UpdateAfter<Movement>]
public class CellAssign : EcsSystem
{
    [ForEach, Changed<Position>, With<Visible>]                            // moved since the last tick (a new entity counts): which cell now
    static void Assign(Entity e, in Position p)
    {
        var map = e.Root;
        var cell = Aoi.CellOf(map.Map, p.Value, map.Grid.CellSize);
        if (!e.TryGetCell(out var old) || old != cell) e.Set(cell);       // only a crossing writes Cell
    }
}

[UpdateInGroup<Map>, UpdateAfter<CellAssign>]
public class Regroup : EcsSystem
{
    [ForEach, Changed<Cell>, With<Presence>]                               // a presence's first cell or a crossing: its 3×3 ClientGroups
    static void Crossed(Entity avatar, in Cell cell)
    {
        Span<ClientGroup> around = stackalloc ClientGroup[9];
        avatar.SetGroups(Aoi.Around(avatar.Root, in cell, around));        // root.Group(key) per cell; the framework syncs the gateway
    }
}

[UpdateInGroup<Map>, UpdateAfter<Regroup>]
public class Snapshot : EcsSystem
{
    [ForEach, Changed<Joined>]                                             // joined, reconnected or taken over: confirmed this tick
    static void Push(Entity avatar, in Joined joined, in Cell cell, World world)
    {
        if (!joined.MissedPushes) return;                                  // handed over by Retire behind the fence: nothing missed
        avatar.Push(Aoi.Snapshot(world, in cell));                         // a pooled Nearby of the 3×3 cells, from world.CellIndex
    }
}

[UpdateInGroup<Map>, UpdateAfter<Snapshot>]
public class Diff : EcsSystem
{
    [ForEach, Changed<Velocity>, With<Visible>]                            // velocity written: tell the cell if it differs from what was sent
    static void Moved(Entity e, in Velocity v, in Position p, in Cell cell, in NetId id, ref NetSync sync)
    {
        if (v == sync.Sent) return;
        sync = new NetSync(v);
        e.Root.WriteShout().Add(new Shout(cell, Aoi.Moved(in id, in p, in v)));   // WriteShout: a tracked write
    }
}

[UpdateInGroup<Map>, UpdateAfter<Diff>]
public class Vanish : EcsSystem
{
    [ForEach, With<Leaving>]                                               // leaving now; the framework destroys it after the user's systems
    static void Disappear(Entity avatar, in NetId id)
    {
        if (!avatar.TryGetCell(out var cell)) return;                      // never placed: nobody saw it
        avatar.Root.WriteShout().Add(new Shout(cell, new NearbyEvent(id.Value, NearbyKind.Disappear, default, default)));
    }
}

[UpdateInGroup<Map>, UpdateAfter<Vanish>]
public class Flush : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Shout>>]                               // only maps whose shouts were written this tick
    static void Publish(Entity map, ref DynamicBuffer<Shout> shouts)
    {
        Aoi.Publish(map, shouts.AsReadOnlySpan());                         // one Nearby per cell with news: map.Push(map.Group(Aoi.Key(cell)), nearby)
        shouts.Clear();
    }
}
```

被打断的前摇：眩晕的处理方法里 `me.Cancel<Strike>()`。

客户端连上时由 `[JoinOnConnect<Account>]` 加入 Player；进地图的 `EnterMap` 直接发给地图，不经过 Player。Player 只回答地图的 `GrantItem`。

AOI 的状态全在组件里：客户端看到的编号是 `NetId`，格子是 `Cell`，上次发出的速度是 `NetSync`，确认和有没有漏过推送是框架的 `Joined(MissedPushes)`，订阅是 `Subscribed`，消息是地图上的 `DynamicBuffer<Shout>`。`Aoi` 是用户写的无状态帮助类。每个系统只做一件事：
1. `CellAssign`：位置被写过的实体，算出格子；跨格才写 `Cell`。
2. `Regroup`：`Cell` 变了的 Presence，改写它订阅的 3×3 `ClientGroup`（`root.Group(格子的键)`，键带着这张地图）。框架的 `SyncSubscriptions` 按这份数据同步网关（第 9 节），所以用户只写一处。
3. `Snapshot`：这个 tick 确认的 Avatar（`Joined` 被写过：第一次加入、断线后重连、被别的连接接管），`MissedPushes` 为真才推一份完整快照；因 `Retire` 换连接的一个推送也没漏，不推。
4. `Diff`：速度被写过、并且和 `NetSync` 不同的实体，写一条 `Moved` 进地图的 `Shout`。
5. `Vanish`：带 `Leaving` 的 Avatar，写一条 `Disappear`。
6. `Flush`：`Shout` 被写过的地图（`[Changed<DynamicBuffer<Shout>>]`，没消息的地图不访问），按格子合并，每个有内容的格子发一帧 `Nearby`，然后清空。

所有系统都只看带 `In<Map>` 的实体（`IActor<Map>` 带着的组过滤，第 5 节）。`Steering` 和 `Movement` 用 `Mut<T>` 的 `SetIfChanged`（第 5 节的"只在值变了时写"）：`Movement` 因此逐行，不用批量方法，站着的 NPC 不打戳，`CellAssign` 和 `Diff` 就不访问它。除了 `Steering` 都是单线程：`Movement` 每行只是一次加法和一次比较，其余的发消息、写别的实体或做结构变更。`Steering` 也只在节点的 world 比核少时才多线程（第 5 节）。

捡东西没有用户写的预留、重问和去重：`GrantItem` 是 `[Durable]`，掉落上还在等的问（`drop.IsPending<GrantItem>()`）就是"已经许给了别人"，它随地图的记录保存。地图宕机后框架唤醒它，用同一个调用 id 再问；Player 那边框架认出问过的调用，回答第一次的结果，不执行第二次。到了期限还没有结果（`Expired`），掉落销毁：宁可丢，不发两次。

NPC 和掉落也由用户建：`root.Spawn(Prefabs.NpcPrefab, new NetId(map, ++ids.Next))`（`map` 是根上的 `Map` 组件），同样从 `NetIds` 取一个编号；掉落是带 `Loot` 和 `NetId` 的子实体。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: Key => this for every partial IActor<T> / IChildKey<T> record struct
partial record struct Map    { Map IIndexed<Map>.Key => this; }        // explicit: a public Key would recurse in ToString()
partial record struct Player { Player IIndexed<Player>.Key => this; }
partial record struct Avatar { Avatar IIndexed<Avatar>.Key => this; }
partial record struct Cell   { Cell IIndexed<Cell>.Key => this; }
partial record struct NetId  { NetId IIndexed<NetId>.Key => this; }

// [Claims]: the claims are a component of Connection
partial record struct Account : IComponent;
```

```csharp
// <auto-generated/> Game.Server: typed sends on Entity and the typed reply value, one class per message
public static class GrantItemSends
{
    public static CallId Ask(this Entity from, Player to, in GrantItem message, TimeSpan deadline, CallId call = default) =>
        Runtime.AskDurable(from, in to, in message, deadline, call);       // [Durable]: deadline is required; answer: Replied<GrantItem>

    public static CallId Ask<TState>(this Entity from, Player to, in GrantItem message, in TState state, TimeSpan deadline,
        CallId call = default) where TState : struct, IComponent =>       // answer: Replied<GrantItem, TState>
        Runtime.AskDurable(from, in to, in message, in state, deadline, call);   // held until the asker's record has the waiting entity

    extension(Replied<GrantItem> a)                                        // C# 14 extension block: the reply's type comes from the request
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);   // reads the reply bytes in the mail's frame
    }

    extension<TState>(Replied<GrantItem, TState> a) where TState : struct, IComponent
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);
    }
}
// A request without [Durable] gets TimeSpan deadline = default (the node's AskTimeout) and goes into Outbox at once.
// The receiver is in the signature; the sender is checked where it is called: inside [Handles<K>] or [UpdateInGroup<K>],
// K must be in [From<…>] (here Map), else the analyzer reports it; elsewhere a Debug build asserts the root's actor type.
// Scheduled<T> Entity.Schedule<T>(in T, TimeSpan after = default) where T : struct, IEvent is the framework's own generic method.
```

```csharp
// <auto-generated/> Game.Server: each [On<T>] method as an EasyEcs per-entity job, inside its partial class
partial class Maps
{
    internal struct Enter_Job : IForEachEntity<NetIds>
    {
        public EnterMap Message;                                           // filled from the mail
        public Account Who;                                                // the sending client's claims, not the subject's
        public Map Map;                                                    // the item's actor key, not a component of the subject
        public bool Reply;
        public static int Writes => 0b1;                                   // NetIds
        public void Execute(Entity e, ref NetIds ids) => Reply = Enter(in Message, in Who, in Map, e, ref ids);
    }                                                                      // spawns and joins: no INoStructuralChanges

    internal static void On_Enter(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Enter_Job { Message = mail.Read<EnterMap>(), Who = mail.Claims<Account>(), Map = mail.Target<Map>() };
        if (!subject.Run<NetIds, Enter_Job>(ref job)) { outcome.Skipped(); return; }   // no filter on the method: none passed
        outcome.Reply(job.Reply);                                          // Join() put the Route in Outbox ahead of this reply
    }
}

partial class Moves
{
    internal struct Move_Job : IForEachEntity<Position, Velocity, Speed>, INoStructuralChanges   // Entity only pushed to
    {
        public static readonly QueryFilter Filter = QueryFilter.Create().Without<Dead>();   // [Without<Dead>], built once
        public MoveTo Message;
        public static int Writes => 0b011;                                 // Position, Velocity: Speed is read, so not stamped
        public void Execute(Entity e, ref Position p, ref Velocity v, ref Speed speed) => Move(in Message, e, ref p, ref v, in speed);
    }

    internal static void On_Move(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Move_Job { Message = mail.Read<MoveTo>() };
        if (!subject.Run<Position, Velocity, Speed, Move_Job>(ref job, Move_Job.Filter)) outcome.Skipped();   // no copy, no write-back
    }
}
// Combat.Cast_Job, Combat.Resolve_Job: the same shape; their filter is Without<Dead>().Without<Stunned>(). Cast schedules after a
// windup (a child entity), Resolve adds Dead to another entity: neither gets INoStructuralChanges. Pickups.Take_Job asks (a waiting
// child) and Pickups.Granted_Job destroys: neither gets it. Items.Grant_Job only writes Inventory and returns its reply: it gets it.
// GrantItem is [Durable]: before the job, the dispatch looks the call id up in the root's ReplyLog (a repeat is answered from it and
// the job does not run); after it, the reply is held until the record write.
```

```csharp
// <auto-generated/> Game.Server: one Registration object; no static registries, no module initializers
public static partial class Registrations
{
    public static Registration Create() => new Registration()
        .Actor<Map>()                                                      // root prefab: Map.RootPrefab; the runtime finds a Map by world.Index<Map, Map>();
                                                                           // also adds the framework's closed OrderedPhase<Map>, Depart<Map>, SyncSubscriptions<Map>
        .Prefab<Map>(Prefabs.AvatarPrefab)                                 // a registered copy with In<Map> and a ChildOf column
        .Prefab<Map>(Prefabs.NpcPrefab)                                    // the static prefabs stay as written
        .Actor<Player>()                                                   // root prefab: Player.RootPrefab
        .Claims<Account>()
        .JoinOnConnect<Player, Account>(static (in Account c) => new Player(c.Id), Takeover.NewestWins)
        .Dispatch<Map>(static table => table
            .Client<EnterMap>(Maps.On_Enter)                               // [To]: subject is the root; may Join
            .Client<MoveTo>(Moves.On_Move)                                 // subject: the presence
            .Client<CastSkill>(Combat.On_Cast)
            .Client<PickUp>(Pickups.On_Take)
            .Replied<GrantItem, Looting>(Pickups.On_Granted)
            .Event<Strike>(Combat.On_Resolve))
        .Dispatch<Player>(static table => table
            .Request<GrantItem>(Items.On_Grant))                           // [Durable]: deduplicated through the ReplyLog
        .Routes(static table => table
            .Route<EnterMap, Map>(to: static (in EnterMap m) => new Map(m.Map))   // [To] long Map -> Map(long Id)
            .Route<MoveTo, Map>()                                          // no [To]: this client's map presence
            .Route<CastSkill, Map>()
            .Route<PickUp, Map>())
        .Systems<Map>(static world =>                                      // the Map group; EasyEcs orders them by [UpdateAfter]
        {
            world.AddSystem<Steering>();
            world.AddSystem<Movement>();
            world.AddSystem<CellAssign>();
            world.AddSystem<Regroup>();
            world.AddSystem<Snapshot>();
            world.AddSystem<Diff>();
            world.AddSystem<Vanish>();
            world.AddSystem<Flush>();
        });                                                                // the node runs Ordering, Update<Map>(), Settling, Cleanup at 30 Hz
}
```

```csharp
// <auto-generated/> AppHost, from the actor / hosted-by lines of Game.Server's wire.manifest
namespace Game.Server
{
    public readonly struct GameServer    : IServer;
    public readonly struct MapServer     : IServer;
    public readonly struct GatewayServer : IServer, IAcceptsClients;
}
// The same form and the same IServer as the server side (DaServer.Core). Each type is bound to the Game.Server project:
// AddNodes<MapServer>("map", 4) runs that project's binary as MapServer. "map" is the resource and pod prefix; the server
// type's identity is its FullName.
```

```text
# wire.manifest (excerpt, committed)
# ── Game.Server ──
actor   Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer   tick 30Hz
actor   Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer  join-on-connect Account newest-wins
actor   DaServer.Connection id 3E0F7A21  key int64   hosted-by GatewayServer
claims  Game.Server.Account  int64
request Game.Server.GrantItem  Map -> Player  reply bool  durable
event   Game.Server.Strike   layout 7C21A0F3
state   Game.Server.Looting  for GrantItem  durable
persist Game.Server.Grid     layout 19E4B2D8
persist Game.Server.Inventory layout 82C7F015
persist Game.Server.Loot     layout 6E12C4A7
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
client  Game.Protocol.EnterMap   -> Map  to Map  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
client  Game.Protocol.PickUp     -> Map  reply bool
on      Game.Server.GrantItem    Player
on      Game.Server.Strike       Map
replied Game.Server.GrantItem Looting  Map
leaving Map  Vanish
```

### 测试一个 Actor：`TestActor<K>`

一个 world、一个根、手动时钟（`FakeTimeProvider`）；`Outbox` 解码成数据，直接断言。不起节点，不连网络。用户的系统用 `SystemErrorPolicy.Throw`：系统抛出就是测试失败，不像节点上那样停用了事。

```csharp
[Test]
public void Walking_out_of_range_during_the_windup_dodges()
{
    using var map = new TestActor<Map>(new Map(7), Registrations.Create());   // one world, the root, a manual clock
    var a = map.Connect(new Account(1));                                        // test clients: a stamp and the claims
    var b = map.Connect(new Account(2));
    map.Deliver(new EnterMap(7), client: a);
    map.Deliver(new EnterMap(7), client: b);
    map.Tick();                                                                 // ordered phase, the Map group, settling

    map.Deliver(new CastSkill(Skills.Slash, map.PresenceOf(b).NetId.Value), client: a);   // PresenceOf: b's avatar, an Entity
    map.Deliver(new MoveTo(new Vector2(50, 0), default), client: b);
    map.Advance(Skills.Windup(Skills.Slash));
    map.Tick();

    Assert.That(map.PresenceOf(b).Health.Value, Is.EqualTo(100));
    Assert.That(map.Outbox.Pushes<Nearby>(to: a), Is.Not.Empty);                // what left the actor, decoded
}

[Test]
public void A_busy_tick_allocates_nothing_and_threads_change_nothing()
{
    using var map = new TestActor<Map>(new Map(7), Registrations.Create());
    map.SpawnNpcs(20_000);                                                      // the test project's own extension over map.Root
    map.Tick();                                                                 // warm the pools, query caches and buffers

    Assert.That(map.AllocatedBytes(static d => d.Tick()), Is.Zero);             // 0 B, counted on every thread
    map.AssertParallelMatchesSerial(ticks: 30);                                 // Capture/Restore, then a serial twin: same result
}
```

- **`TestActor<K>` 只给通用的东西**：`Root`、`Connect`、`Deliver`、`Tick`、`Advance`、`Outbox`、`PresenceOf(client)`（返回这个客户端在本 Actor 上的 Presence 实体，`Entity`）、`AllocatedBytes`、`AssertParallelMatchesSerial`。游戏自己的帮助方法（例如 `SpawnNpcs`、"某个客户端的 NetId"）是测试工程里对 `TestActor<Map>` 的扩展方法。
- **`Skipped` 在测试台里分两种**：主体缺少组件参数是代码错误，`Deliver` 抛出；过滤不满足（`[Without<Dead>]`）是有意的跳过，照常记成 `Skipped`。
- **`map.AllocatedBytes(action)`**：执行一次 `action`（静态 lambda，不捕获），返回分配的字节数。Actor 里有 `Parallel = true` 的方法时用 `GC.GetTotalAllocatedBytes(precise: true)`，否则用 `GC.GetAllocatedBytesForCurrentThread()`（EasyEcs `guide/testing.mdx`）；所以测试按 fixture 串行跑。
- **`map.AssertParallelMatchesSerial(ticks)`**：给一种服务器跑的 world 比核少的部署（第 5 节）。测试台的 world 按这种部署建（`MaxDegreeOfParallelism` 用全部核）。`MaxDegreeOfParallelism` 在建 world 时定下（init-only），`Restore` 只收同一个 world 的快照（别的 world 的抛 `ArgumentException`），所以测试台自己带一个串行的孪生 world（`MaxDegreeOfParallelism = 1`），收到同样的输入：
  1. 主 world `Capture()` 存下起点，跑 `ticks` 帧，记下 `Outbox` 和整棵子树 `SaveEntities` 的字节；
  2. `Restore(起点)`，再跑 `ticks` 帧，和第 1 步比较：并行方法有数据竞争时，两次多线程的结果就可能不同；
  3. 孪生 world 跑同样的 `ticks` 帧，和第 1 步比较：串行和并行一致。

### 运行时的路径

每一跳经过的队列、索引和帧见 [architecture.md 第 3 节](architecture.md#3-messaging)。

```
① 客户端连 gate-0      框架在 gate-0 激活 Connection(c1)，加上 Account(42) 和连接戳 S1；第一条邮件是 Activated
② 连上就加入           [JoinOnConnect<Account>]：向 Player 42@game-1 发加入请求（带 S1）；Player 建 c1 的 Presence（戳更大的胜出），回复 P1
                      Connection 写路由 (Player 42, P1, 槽 0)，发 JoinConfirmed(P1)；推 Ready，放出排队的消息
③ EnterMap(7)         Connection(c1)@gate-0：过滤（帧头）→ 路由表：带 [To]，目标 Map 7@map-0；直接转发，信封带着 c1 的上下文（身份、S1、调用 id）
④ Enter               Map 7 的有序阶段：主体是根；GetOrSpawn 找到或建出 Avatar（新建的带 NetId）；Join() 当场返回 Ok（P2），Enter 返回 true
                      Outbox 里给 gate-0 的帧先是 Route(Map 7, P2)，再是回复；阶段结束时交给链路
⑤ Connection(c1)      写路由，槽 1；原来有 Map 3 的路由就删掉，Map 3 对旧 Presence Leave()；向 Map 7 发 JoinConfirmed(P2)；
                      给 SDK 发 JoinConfirmed(槽 1, 调用 id)，再交回 true：SDK 记下 EnterMap(7) 是槽 1 的加入消息
⑥ JoinConfirmed(P2)   Map 7：Avatar 带上 Joined(MissedPushes: true)；推时间通知（33 ms、tick 号、服务器时间）；
                      Regroup 已写好 Subscribed，Snapshot 按 Changed<Joined> 推快照，收尾的 SyncSubscriptions 把订阅集合发给网关
⑦ MoveTo              不带 [To]：Connection(c1) → c1 的 Map 路由 → Map 7 的邮箱；下一个 tick 的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Schedule(Strike, after: 前摇)，带着 Due 进到期堆；IsPending<Strike> 拒绝第二次施法
⑨ Strike 到期         tick 窗口所在那一帧的有序阶段，和消息按 (时间, 序号) 归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ PickUp(9)           主体是 Avatar：掉落向 Player 42 Ask GrantItem（[Durable]：等待实体是掉落的子实体，带 Looting、调用 id、到期 UTC，
                      地图的记录写成后请求才发出）；Player 42：调用 id 不在 ReplyLog 里，Grant 改背包，回复和调用 id 记进 ReplyLog，
                      记录写成后才发出；Replied 按调用 id 找到等待实体：TryGetValue 得到 true，回复客户端 true，销毁掉落；
                      map-0 在回信前宕机：DueScan 唤醒 Map 7，框架用同一个调用 id 再问，Player 从 ReplyLog 回答 true，不执行第二次
⑪ 每 33 ms 一个 tick   Ordering（OrderedPhase）→ Update<Map>()：Steering、Movement、CellAssign、Regroup、Snapshot、Diff、
                      Vanish、Flush → Settling：Depart、SyncSubscriptions；只遍历带 In<Map> 的表 → Cleanup：ReleaseRoutes 等（按 world）；
                      Nearby 序列化一次 → 有观察者的网关各一帧 → 各个连接
⑫ map-0 宕机          map-0 超过租约就自己冻结；Map 7 在 map-1 建占位（键和等着的邮件都在它上面），占到新的 epoch，记录装进占位，它就是根（Avatar 不在记录里）；
                      Connection(c1) 看到 map-0 离开视图：删路由，PresenceLost(槽 1) 给 SDK；SDK 重发 EnterMap(7) → Enter 新建 Avatar，Join → 同 ④–⑥
⑬ map-0 热重启        武装；SIGTERM 后宣布 NodeRestarting，协调者确认；最后一个 tick 根上带 Restarting；冻结、写快照、退出；
                      gate-0 把发给 map-0 的帧留在重发窗口；新进程加载快照、删掉文件，以同一个身份重连，协调者 Readmit 后续上流；
                      第一个 tick 带 Resumed；Avatar、Presence、路由都没变，客户端只看到一次停顿
⑭ c1 与 gate-0 的连接半开（gate-0 还没发现断线），c1 换到 gate-1 重连
                      新的 Connection(c1) 带更大的戳 S2：连上就加入，接管 Player 上的 Presence；Ready 之后 SDK 重发 EnterMap(7)：
                      GetOrSpawn 找到原来的 Avatar，Join() 按戳接管（新的 PresenceId，去掉 Joined 直到确认）；gate-0 上的旧路由按旧的 PresenceId 释放；
                      目标向 gate-0 发 TakeOver(S2)：签发序号相同，旧的 Connection(c1) 安静地关闭；确认后 Joined.MissedPushes 为真，Snapshot 重推
⑮ gate-1 宕机          Player 42 和 Map 7 上 c1 的 Presence 加上 Detached（宽限 30 秒），不推送；c1 经负载均衡器连到 gate-0，
                      戳更大，接管两个 Presence，去掉 Detached；没回来的，宽限期过后 Leave()
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：接收方不对没有对应的方法；`[Handles<K>]` 类或 `[UpdateInGroup<K>]` 系统里发送，K 不在消息的 `[From<…>]` 里。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个 Actor 类型处理。
- `[On<Incoming>]` 不在 `[Handles<Connection>]` 里。
- 一个请求有两个处理方法；`Replied` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`；或者既返回回复，又取 `ReplyTo<T>`；`Reply` 的值类型和令牌的 `T` 不同。
- `[On<T>]` 的 T 是框架的 tag 或组件（`Joined`、`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）：它们不是消息，修复是"写一个 `[ForEach, With<T>]` 的系统"。
- 主体一定是根的处理方法（带 `[To]` 的客户端消息、服务器之间的消息、`Activated`）取 `Presence`、`Joined` 这类只在 Presence 上的组件参数或过滤。
- `Join()` 的返回值没有用到。
- `Sender<K>` 的 K 不是这条消息声明的发送方（多个 `[From<…>]` 时只能是 `Sender<ActorId>`）。
- 客户端消息有多个 `[To]` 参数；`[To]` 参数的类型和处理 Actor 类型的键形状不一致；`[Handles<Connection>]` 处理的消息带 `[To]`。
- 身份参数（`[Claims]` 类型）用在客户端消息以外的处理方法里。
- `[On<T>]` 方法的参数和过滤不合 EasyEcs `[ForEach]` 的规则。EasyEcs 的分析器只检查 `[ForEach]` 方法，所以 DaServer 的分析器对 `[On<T>]` 方法做同样的检查：参数类型、`Maybe<T>` 参数（job 只收必有的组件）、过滤、`ref`/`in`、复制 `DynamicBuffer<T>`（同 EECS012）。
- 用户代码写 Actor 类型组件，或者把 Actor 类型组件放进 prefab、`CreateEntity`。
- Actor 里调用 `CreateEntity`（用 `root.Spawn`）；对看得出不是根的实体调用 `Deactivate()`。
- 承载 `Connection` 的服务器类型在 AppHost 里没有 `WithClientConnections`；消息引用的 Actor 类型没有服务器类型承载（AppHost 构建时检查，第 2 节）。
- `IChildKey<T>` 的第一个字段不是 Actor 类型（第 3 节）。
- 有索引的组件作为 `ref` 或 `Mut<T>` 参数。
- 方法体里的 `ref` 跨过这个实体的结构变更使用（DAS022）。
- `Parallel = true` 的 `[ForEach]` 方法里调用 `Entity` 上的 DaServer 操作（第 5 节的操作表）。结构变更、并行、有索引的组件，EasyEcs 另有 EECS010、EECS013、EECS015。
- `[On<T>]` 方法或 Actor 类型组里的系统以 `ref` 取不带 `[WorldLocal]` 的用户单例（第 5 节）。
- 没有 `[TickRate]` 的 Actor 类型组里，`[ForEach]` 方法既没有 `[Changed<T>]`、`[Removed<T>]`、过滤 tag 的 `[With<T>]`，系统也没有 `UpdateInterval`：每批邮件一趟全扫（第 5 节）。
- 没有 `[TickRate]` 的 Actor 类型用 `root.Group(key, every: n)`（第 9 节）。
- 一种服务器上各帧率不能都整除最快的那个。
- `Schedule` 的类型没有实现 `IEvent`。
- `[Claims]` 类型不是 `partial`、超过 16 字节或不是非托管类型；一个工程有两个 `[Claims]`。
- `[JoinOnConnect<C>]` 的 Actor 类型的键和 C 的形状不同；一个 Actor 类型写了两个 `[JoinOnConnect]`。
- Protocol 里出现 Actor 类型、服务器之间的消息、`ActorId`、`PresenceId` 或 `EntityId`；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- Actor 类型的键超过 16 字节，或者不是非托管类型。
- `IPersisted` 组件不是 `[NinoType]`。
- `[Stateless]` 的 Actor 类型里 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。
- `[Durable]` 写在不是 `IRequestTo` 的消息上，或者接收方是 `[Stateless]` 的 Actor 类型（它不写记录）；`[Durable]` 请求的 `Ask` 状态不是 `[NinoType]`（它随记录保存，第 10 节）；`[Durable]` 请求的 `ReplyTo<T>` 存进了不随记录保存的组件（第 8 节）。`[Durable]` 的 `Ask` 没写 `deadline:` 是编译错误：生成的方法里它不是可选参数。
- `[ActorOptions]` 的值是负数。
- 要迁移的托管组件、托管的事件或 `Ask` 状态没有 Nino 格式化器（第 10 节的序列化规则）。
- 用户的泛型 `[ForEach]` 系统只在别的程序集里封闭（EasyEcs 要在运行时 `MakeGenericType`，Native AOT 下不行，第 10 节）。

**警告和提示**（不报错）：
- 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`：改读 `Time.Utc`（警告，第 9 节）；
- 处理方法里结构变更之后还有 `throw` 或可能抛出的调用：先校验，最后做结构变更（第 5 节）；
- `Replied` 处理方法里没有延后就重新 `Ask`（普通请求）：改成 `Schedule(…, after: a.RetryAfter)` 再问，不在对方忙的时候立刻压上去（第 11 节）；
- 在 Presence 实体上 `Ask`：Presence 离开时等待实体随它销毁，回信丢弃；要收尾的从根或者不会离开的子实体问（第 8 节）。

**启动时检查**：配置的服务器类型不存在，或者和 `wire.manifest` 对不上，节点启动失败。热重启的快照不合格时忽略它，照常从记录激活（第 10 节）。

**删掉的东西**：
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调（现在是 `Activated` 的 `Activation` 值）、`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`（并入 `[On<Incoming>]`）、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded 三个会话 tag（`Superseded` 现在只是拒绝原因）、自研的记录格式、`ActorKey`；
- `[Receive]`、`[Receive<K>]`、用 `EcsSystem` 处理消息、`[Every]` 系统组、`self.After`、作为业务入口的 `TimerSystem`/`MailSystem`；
- `[Frame(ms)]`、`Raise`、`SendAfter`、`[On<T, K>]`、`[Link]`、`[Of(…)]`、`[Pending]`、`AskFor`、`world.Route`、`routeFrom:`、用户写的 `Attach`、空间区域；
- `[On<Tick>]`、`[On<Tick>(Serial)]`、`Tick` 事件、`[After<TClass>]`、配置里的帧率和 `.WithTickRate`；
- 时间轮、`Due(long UnixMs)`、`Scheduled<T>.Cancel()`/`Pending`；
- `reply:`、`onBehalfOf:`、`a.ReplyTo`、`Hold`、`AskAll`/`Answers<,>`、`ReplyTo` 不能复制的规则；
- `KindId`、`MessageId`、`EventId`（统一成 `TypeId`）、`Activated(bool Restored, bool MovedIn)`；
- v22 的名字：`IKind<T>` 和"种类"、`KindKey`、链接、`Linked`、`LinkId`、`LinkFrom()`、`Unlink()`、`Unlinked`、`LinkLost`、`Confirmed`、`Unsynced`、`LinkConfirmed`、`NotLinked`、`From<K>` 参数、`IAddressed`、`IMessage<K>`、`IRequest<K, R>`、`Envelope` 过滤参数、`Activation.Fresh`/`Restored`/`MovedIn`、`IdleAfter`、`[OnError] Reload`、`[Spawns<K>]`、`Clock`、`Self<K>`、`e.Watch`、`IFrameGroup<K>`、`[Frame(Hz)]`、`MapFrame`；v22 例子里经 Player 中转进图的写法；
- v23 的：
  - 跳板的复制和写回、`TryGetRef` 写回、可能别名的两个可写引用的分析器规则（处理方法是 EasyEcs 的 job）；
  - `Left` 消息和 `[On<Left>]`（→ `Leaving` tag 和系统）、`NeedsSnapshot`（→ 用户的 `Synced`）、`Via`；
  - 服务端存的加入消息、它的大小上限、重放和重放的退避（→ SDK 重新发）；
  - `[Claims<K>]`、`PlayerClaims`、由 K 的键生成声明字段（→ `[Claims] Account` 和 `[JoinOnConnect<Account>]`）；
  - `[Persist]` 和"`[Persist]` 必须声明 `ITrackChanges`"的规则（→ `IPersisted`）；
  - Actor 类型上的 `[With<In<K>>]` 和对应的分析器规则（→ `IActor<T>` 带组过滤 `IGroupWith<In<T>>`，EasyEcs 4.5）；
  - `[Prefab<K>(Root = true)]`（→ `K.RootPrefab`）、`[ModuleInitializer]` 注册和改写静态 prefab（→ `Registration`、`root.Spawn`）；
  - `e.Send(ev[, after:])`（→ `e.Schedule`）、区分 `Send(ev)` 和 `Send(to, msg)` 的分析器规则、`world.IsAlive(s.Id)`（→ `e.IsPending(s)`）、例子里的 `Casting` 组件（→ `IsPending<T>`）；
  - `world.GetEntity(on.Parent)`（→ `e.Root`）、`None`（→ `Replied<TReq>`）、`IServerType`（→ 一个 `IServer`）；
  - `PresenceId` 的节点位、进程启动时随机的节点 id（→ 集群分配的节点代数）、只比较相等的连接代数（→ 有序的连接戳）；
  - UTC 纪元的时间、依赖 NTP 的绝对到期时刻（→ 单调时钟、剩余时间）；
  - 自动的 `NeedsSnapshot` 和"挂接/重放后推完整快照"（→ 用户逻辑）；Logic 决定有没有 tick 和对应的热更规则（→ `[TickRate]` 在 Server）；
- v24 修订中去掉的：处理方法的 `Maybe<T>` 参数（→ 方法体里 `TryGetX`）、激活时把 Actor 类型组件和 `DynamicBuffer<Mail>` 从占位搬到新根（→ 占位就是根）、生成的 `find:` 委托（→ 运行时的 `world.Index<K, K>()`）、"迁移到达后按 `Subscribed` 重新订阅"的单独规则（→ 迁移到达算作写入）、每次调用 `QueryFilter.Create()`、`Spawn(…).SetX().SetY()` 的多次行移动（→ `Spawn(prefab, in a, in b)`）、按 Actor 的系统耗时（→ `system.Stats` 按 world）；
- v25 的：`Game.Server.Logic` 工程和工程之间的循环、可回收的 `AssemblyLoadContext`、`RemoveTypes`、两个 tick 之间换分发表的热更、`wire.manifest` 的 Logic 一段、`Registration` 的 Logic 部分、"组件声明在 Logic"的检查、"AOT 不热更"（→ 两个工程、原地热重启，JIT 和 AOT 一样）；
- v27 的：`Client` 这个 Actor 类型名（→ `Connection`）、`a.Reply`（→ `TryGetValue`）、`Time.Now`（→ `Time.Elapsed`）、`world.IsPending`/`world.Cancel` 和 `HasPending`/`CancelPending`（→ `e.IsPending`/`e.Cancel`）、`Entity` 上任意实体的 `Deactivate()`（→ 只在根上）、子实体键里的根 `EntityId`（→ Actor 的键）、AppHost 节点组上的 `WithClientEndpoints()`（→ `WithClientConnections(port)`）、`NodeGroup<T>`（→ `DaServerNodeGroup<T>`）、“挂接”的说法（→ 连上就加入）；
- v28 的：`Answer<…>`、`a.Value`、`a.Ok`（→ `Replied<…>`、`TryGetValue`、`Status`）、`[RootPrefab<K>]`（→ `K.RootPrefab`）、`[MaxScheduled]`（→ `MaxPending`，也管等待中的请求）、`Due` 的有序值索引（→ 运行时的到期堆）、单例 `Ordered`（→ `Ordered<K>`）、`Deactivating` 消息（→ tag）、`Join()` 抛出 `NoClient`/`Superseded`（→ 返回 `Status`）、处理方法里的 `me.Root.Map`（→ Actor 类型参数）、`Drops.TryFind` 这类帮助方法（→ 值索引）、事件和状态必须 `[NinoType]`（→ 非托管的按布局哈希搬字节）、宿主隐式调用服务管理器（→ Program.cs 显式写）、热重启的预热交接和它的本机管道、`MAINPID`、备用链路、化身号和交接令牌（→ 每个平台一种机制：快照文件，ADR-088）；
- v29 的：用户的 `Synced` tag（→ 框架的 `Joined(MissedPushes)` 和 `[Changed<Joined>]`）、网关离开视图或连接断开时立刻 `Leave()`（→ `Detached` 宽限期）、武装时就宣布 `Restarting`（→ SIGTERM 之后、协调者确认之后）、快照写失败后旧进程接着跑（→ 放弃后优雅停止，`Resumed(Aborted)`）、占 epoch 的条件 `placement <= 放置版本`（→ 严格更新的版本或同一个主人）、扩容后的再平衡靠 `MaxConnectionAge`（→ 按负载，ADR-089）、"发送方按已有的退避重试"（→ 两类失败、框架只重试转发一跳上没执行的拒绝）、`Looting.Drop` 和从 Avatar 发问（→ 掉落自己发问）；
- v30 的：`JoinedBy` 和 `Clean`（→ `Joined(MissedPushes)`）、`Status.SafeToRetry`（→ `NotExecuted`、`Transient`）、Actor 类型上叫 `Root` 的 prefab（→ `RootPrefab`）、`IKey<T>`（→ `IChildKey<T>`）、控制邮件 `Supersede` 和 `Moved`（→ `TakeOver`、`HandedOff`）、`nodes.Waits`（→ `nodes.TryGetPause`）、成员状态 `Restarting`（→ `NodeRestarting`；tag `Restarting` 不变）、用户的 `CallRing` 和接收方自己给 `[Durable]` 的请求去重（→ 框架的 `ReplyLog`）、例子里的 `Reserved`、`Asking`、`AskAgain`、`Reasks`、`SeenCalls`（→ `[Durable]` 的发问方由框架重问到 `Expired`）、"加入消息的处理方法要写成幂等"的规则（→ `root.GetOrSpawn`）、四个单独的特性 `[IdleTimeout]`、`[DetachGrace]`、`[MaxPending]`、`[OnError]`（→ `[ActorOptions]`）、"框架给的"表里的内部名字（`HeldReply`、`Pending`、`StoreDegraded`、`ActivationSlots`、`PeerHealth`、`ActivationFailed`、`Rejoining`，→ architecture.md 第 2 节）、只给服务的 `Publish`（→ `DaServer.Topics`，第 15 节）；
- v31 的：以 `ref` 取任意单例（→ 用户单例只能 `in`，`ref` 要 `[WorldLocal]`）、"每个节点一份的状态用单例"（→ `[WorldLocal]` 单例或根上的组件）、`OnError` 一律默认 `Resume`（→ 有持久化组件的默认 `Restart`）、例子里每 tick 无条件改写 `Velocity` 的 `Steering` 和批量方法的 `Movement`（→ `Mut<T>` 的 `SetIfChanged`）、单独的 `world.Describe()` 管理端点（→ architecture.md 第 8 节的查看端点）；
- 草案历次加了又去掉的：位置目录、定时租约摘要、迁移栅栏、`MapSlot`、`RouteLost`。

**版本记录**：

| 版本 | 主要变化 |
|---|---|
| v8 | 消息写明发送方和接收方；会话与链接；一个节点只跑一种服务器 |
| v9 | 地址统一；系统按组件查询；生命周期用 tag；记录改用 EasyEcs 4.4 |
| v10 | 链接随回复建立；参数绑定表；示例重写 |
| v11 | 客户端也是一个种类；Protocol 和 Server 分开；`LinkId` 由目标分配；第一条链接；记录一次保存、一次加载 |
| v12 | 种类就是它的地址，也是实体上的组件；Protocol 不出现种类；`[Hosts<Client>]`；`AddNodes<T>`；去掉位置目录和迁移栅栏；写入中占位；完整例子和生成的代码 |
| v13 | 生命周期改成发给自己的消息；只有运行时能写种类组件；`Confirmed` 之后才推送；`Attach` 退避；`[Claims]`；`ReplyTo<T>`；期限传递；删除也触发保存 |
| v14 | 同一个键不会有两个链接实体；`Answer` 按（请求类型, 等待数据类型）分发；`Activated` 抛异常算作激活失败；从决定卸载起，新邮件由占位接住；`DynamicBuffer` 按 `ref` 传入；含集合的消息用 `[NinoPooled]`；加载后重置变更基准 |
| v15 | 研究执行模型：消息、帧、并行之间的先后 |
| v16 | 一切都是对事件的反应：只有 `[On<T>]`，Tick 也是事件 |
| v17 | 一条消息是一个事务；Tick 按列并行 |
| v18 | 动作跨多帧：`Raise(e, after: n)`、`Scheduled<T>`，到期时重新校验 |
| v19 | n = 0 在当前处理方法之后紧接着执行，属于同一个事务 |
| v20 | 每个 Actor 是一个离散事件模拟：按 `(时间, 序号)` 逐个执行 |
| v21 | 延后用时间；一个绑定规则；[Handles<K>]；Answer<TReq,TState>；去掉空间分区 |
| v22 | 每帧逻辑用 EasyEcs 系统；一个时间类型、一个计时机制（Due 有序索引）；Scheduled 是数据；ReplyTo 放进状态；实体操作在 Entity 上 |
| v23 | 改名（Actor、Presence/Join/Leave、Sender、IMessageTo/IRequestTo、TickRate、Time…）；客户端消息带 [To] 直达目标，每个 Actor 类型一个 Presence；只留 Entity；帧率写在 Actor 类型上、Actor 类型即系统组；异常不回滚如实说明 |
| v24 | handler = EasyEcs 单实体 job；离开/订阅/有序阶段都是数据；客户端看不到节点；SDK 负责重新加入；身份与 Player 解耦；IPersisted；Entity.Root；Schedule/Send 分开；激活 epoch 隔离、有序连接戳、相对时间、迁移版本校验、有界队列 |
| v25 | 基于 EasyEcs 4.5（`entity.Run`、`IGroupWith`、`Descendants`、`markWritten: false`）；占位就是根；`Key` 显式实现、`IActor<TSelf>` 带 `IEquatable<TSelf>`；按 EasyEcs 文档修正（到期索引、清理组件、`Maybe<T>`、并行度）；Native AOT（JIT 可热更，AOT 不热更；注册是封闭类型、无反射） |
| v26 | 原地热重启取代进程内热更，JIT 和 AOT 一样；两个用户工程（ADR-083、ADR-084） |
| v27 | 名词精简：Actor、节点、服务器类型、world；六个包（ADR-086） |
| v28 | `Connection`、单文件 AppHost、端口按组声明（ADR-085、ADR-087） |
| v29 | 热重启每个平台都是快照文件，重启前后的反应是 tag（`Restarting`、`Resumed`），`Deactivating` 也是 tag（ADR-088）；`Replied<…>` 和 `TryGetValue`；`IKey<T>`、`K.Root`、Actor 类型参数绑定到根；`Join()` 返回 `Status`；`Due` 进运行时的到期堆、即时链不建实体、`Outbox` 追加时序列化、网关只读帧头；成员租约、放置版本、迁移占新 epoch、墓碑转发所有邮件、协调者定期限和分配代数；`[MaxPending]`；Program.cs 显式写三种服务管理器；网关先扩后缩 |
| v30 | 分布式正确性的补漏：`[Durable]` 回复、断线宽限 `Detached`、`Supersede` 关旧连接、`JoinedBy` 和 `Moved` 栅栏、按负载的连接再平衡（ADR-089）；热重启 SIGTERM 才开始、快照一次性、`Rejoining` 等协调者回答、协调者钉住槽、`Resumed(Pause, Aborted)`；租约按回显的心跳续、放置版本严格更新、`Fence`、代数取 Unix 毫秒；预算不因停顿延长、嵌套的问继承剩余时间、两类失败和重试预算、客户端调用 id、`Pending`、`StoreDegraded`、`PeerHealth`、接纳令牌桶；第 11 节的保证表和定时默认值 |
| v31 | `[Durable]` 两端由框架负责：发问方的等待实体随记录保存、请求在它写成后才发出、按 `deadline:` 重问到有结果或 `Expired`，接收方按调用 id 用 `ReplyLog` 去重，`NextDue` 和 `DueScan` 在宕机后唤醒；`Status.NotExecuted`/`Transient`、`Expired`、`AlreadyConnected`；`Joined(MissedPushes)`；`RootPrefab`、`IChildKey<T>`、`[ActorOptions]`、`root.GetOrSpawn`、`Time.Utc` 和一条时间规则；控制邮件 `TakeOver`、`HandedOff`，成员状态 `NodeRestarting`；"框架给的"分成核心和进阶，内部的数据移到 architecture.md；第 11 节的保证表按"会丢吗、会执行两次吗、去重、谁重试"重写；第 15 节的扩展能力：`DaServer.Reminders`、`DaServer.Topics`、客户端协议和 TypeScript 客户端、`DaServer.Replication`、测试和管理工具（ADR-090）；业务功能做成样例和 `dotnet new` 模板（ADR-091） |
| v32 | 对照 flecs、Bevy、Unity Netcode for Entities、Photon Quantum、Overwatch、SpatialOS：用户单例只能 `in`，`ref` 要 `[WorldLocal]`；没有 `[TickRate]` 的组里不许每批全扫；有持久化组件的 Actor 类型默认 `OnError = Restart`；每 tick 重算的组件用 `Mut<T>` 的 `SetIfChanged`；用到 EasyEcs 4.7（计划）的 `Instantiate`、`ChangedAny`、`DestroyLog`、`IUniqueIndexed`、`TrimExcess`、`Slices` 和诊断；只读的查看端点（architecture.md 第 8 节）；`root.Group(key, every: n)`；客户端传输只有 TCP 和 WebSocket（ADR-092）；mmo 的输入 tick 和校正关键帧、帧同步中继房间样例（ADR-091）；第 14 节记下保留的取舍 |

---

## 14. 已定的取舍，以及待办

本稿没有需要拍板的项。下面是起草时比较过的做法，以及按需求（通用、ECS、解耦、DRY、易维护、分布式正确）选定的理由。

1. **身份用用户自己的类型 `[Claims] Account` 加 `[JoinOnConnect<Account>]`**，不用框架包装 `Claim<Player>`。框架是通用的，不是每个应用都有 Player（例如秒杀的买家）；身份绑在某个 Actor 的键上，令牌格式会随那个键变。代价是 `Account` 和 `Player` 的字段写两遍，由分析器保证形状相同。
2. **客户端路由由 `[On<T>]` 所在的 `[Handles<K>]` 类推出**，不另写路由表。同一件事只写一处；改动路由的版本要连网关一起滚动升级，热重启由 `wire.manifest` 比对拦住（第 10 节）。
3. **`[From<K>]` 保持必写**。"从类型就看得出谁发给谁"是目标之一；新的发送方要改消息声明，这正是想要的显式。
4. **消息不做成实体**。邮箱、到期堆和即时链在有序阶段归并三个来源。把每条消息做成带 `Due` 的实体能统一表示，但每条消息多一次建实体和销毁；到期堆和链栈已经给出同样的顺序，而延后事件本来就是实体（要保存、迁移）。所以不做。
5. **EasyEcs 4.6.0 已提供本稿依赖的功能**：`entity.Run(ref job[, filter])`（抛出时 `ref` 写入也搬到新行；job 带 `INoStructuralChanges` 时冻结结构）、接口带的组过滤 `IGroupWith<T>` / `IGroupWithout<T>`、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（被覆盖的 prefab 默认值也不算写入），分析器把 `entity.Run` 和 `LoadEntitiesInto` 当结构调用（EECS010/013/014）；4.6 又加上 `world.Load(bytes, markWritten: false)`（EasyEcs D39）、`AddSystem(…, changesFromNow: true)`，带追踪组件的程序 Native AOT 发布 0 警告（EasyEcs D38）。EasyEcs 评估后放弃的：并行批量方法、`LastWriteTick`、公开读写集、多 world 共享并行预算。重构的第一步（W1）是把 `EasyEcsVersion` 升到 4.7（发布之前用 4.6.0），并编译验证：DaServer 生成器补的 `Key` 显式实现被 EasyEcs 的唯一值索引接受；`IActor<TSelf>`（继承 `IEquatable<TSelf>`、带静态的 `RootPrefab`）在框架程序集和用户程序集里编译通过。
6. **改代码只有热重启，不用 `AssemblyLoadContext`；每个平台一种机制**：理由见 ADR-084 和 ADR-088，做法见第 10 节；Kubernetes 上的形态见 ADR-085。`Save` 写进 `NinoArrayBufferWriter` 再写进文件，多一次拷贝；几 MB 可以忽略，不另提需求。
7. **能是数据的都是数据**（ECS 优先于 OOP）：停用、热重启前后、离开、断线、确认都是根或 Presence 上的 tag 或组件（`Deactivating`、`Restarting`、`Resumed`、`Leaving`、`Detached`、`Joined`），由系统反应，不是回调或虚方法；待处理的个数、留着的 `[Durable]` 回复和请求、`ReplyLog`、`NextDue` 在根上；节点状态（`RestartingNodes`，以及内部的存储降级、激活名额、各目标节点的在途上限）是节点单例（第 0 节的"几个词"）；`Join()` 的结果是值。只有 `Activated` 是邮件：它必须排在占位上等着的邮件之前。保留的：
   - 单例 `Current`（当前项的上下文）：它是 world 的数据，由运行时在每项之前写；改成每个操作显式传参，每个 `Send`/`Ask`/`Join` 都要多一个参数，换不来别的；
   - `Registration` 的构建写法：它是生成的，只在启动时跑一次，写出封闭类型给 AOT；
   - `ICluster` 和 `TestActor<K>`：它们在 world 外面（HTTP、测试），不是 Actor 里的逻辑。
8. **对 EasyEcs 的需求**（EasyEcs 仓库的 issue），都已排进 4.7（计划中，未发布）：
   - #23 值不同才打戳的写入（同 Bevy 的 `set_if_neq`）：计划为 `Mut<T>` 参数、`SetIfChanged(v)` 和生成的 `entity.SetXIfChanged(v)`（第 5 节、第 12 节的 `Steering` 和 `Movement`）；
   - #24 带组件实例化：计划为 `world.Instantiate(prefab, in c1 … c9)`，一次写进最终的表；`root.Spawn` 和 `GetOrSpawn` 用它，不再移动行。子实体的 prefab 照用 `[Prefab<K>]` 和 `Registration` 的副本。
   - 4.7 另外带来、本稿用到的：`[ChangedAny<…>]`、`DestroyLog`、`IUniqueIndexed<T>`、`TrimExcess()`、`[ForEach(Slices = N)]` 和诊断（第 0 节"谁做什么"）。EasyEcs 只写成文档的模式照着用，不另造：固定步长和追帧上限（[architecture.md 第 2 节](architecture.md#the-shard-loop)的循环）、祖先遍历（`e.Root`）、延迟补偿的历史环（样例要做命中判定时用）。
9. **`[Durable]` 两端都由框架负责，普通请求不去重**。去重的记录（`ReplyLog`）搭在 `[Durable]` 本来就要的那次写记录上，不多一次存储往返；每个请求都去重，就要每个请求查一次存储（ADR-082）。发问方的等待实体在请求发出之前写进记录：否则发问方宕机后从旧记录恢复，同一件东西会再许给别人。用户不再写预留、重问和去重的组件。
10. **宕机后的唤醒（`NextDue`、`DueScan`、`IActorStore.DueKeys`）在运行时，不在 `DaServer.Reminders`**：`[Durable]` 的发问方也要它，而运行时不能依赖扩展能力（ADR-045）。它是通用的"记录里最早要醒来的时刻"；`DaServer.Reminders` 只往里写提醒的时刻（第 15 节）。
11. **对照别的 ECS 之后保留的**（flecs、Bevy、Unity Netcode for Entities、Photon Quantum、Overwatch、SpatialOS）：
    - 一个 world 里有很多 Actor：系统按表（archetype）一趟走完这个 world 上同类型的所有 Actor，不是一个 Actor 一个 world（Overwatch 一局一个 world，所以单例能一局一份；这里的单例规则见第 5 节）；
    - 对数据的反应是对 tag 反应的系统（`Leaving`、`Deactivating`、`Restarting`），不是 observer：flecs 自己的建议是"能用系统解决的，就该用系统"（[Observers manual](https://www.flecs.dev/flecs/md_docs_2ObserversManual.html)），系统成批、有固定的位置和顺序；
    - 邮件和事件只有一个按 `(时间, 序号)` 的全序（第 1 节），比 Bevy 按帧缓冲的 message 更严：同一帧里的先后不会因为读的系统排在哪而变；
    - 格子是 `IChildKey<T>` 的值索引（`Cell`），不是每个格子一个 tag 或关系：那样每个格子一种表，实体跨格就换表，表越分越碎（flecs [relationships 手册](https://www.flecs.dev/flecs/md_docs_2Relationships.html)讲的碎片问题）；
    - 换代码是热重启，不是动态加载：Bevy 因为不健全弃用了动态插件（[bevy_dynamic_plugin](https://docs.rs/bevy_dynamic_plugin)），理由同 ADR-084。

---

## 15. 扩展能力（可选包）

每一项是一个独立的包、自己的命名空间，不引用就没有（ADR-045、ADR-090）；运行时不依赖它们。写法和框架本身一样：数据是组件、tag 和单例，逻辑是系统，热路径不分配。业务功能（匹配、组队、排行榜、下单和支付）不在这里：它们是样例，也是 `dotnet new` 模板（ADR-091）。

### `DaServer.Reminders`：扛得住宕机的提醒

```csharp
public record struct DailyReset;                                            // a marker type: which reminder
root.Remind<DailyReset>(at: Quests.NextMidnightUtc(time.Utc), every: TimeSpan.FromDays(1));   // in a handler of a Guild
[On<Reminded<DailyReset>>] static void Reset(in Reminded<DailyReset> r, ref Quests q) => q = default;   // r.UtcAt: which firing
```

- 提醒是 Actor 里的一个子实体，带持久化的 `Reminder<T>(DateTime UtcAt, TimeSpan Period)`；T 是用户的标记类型，每个 Actor 每个 T 一个（`root.Remind<T>` 用 `GetOrSpawn`），`root.Unremind<T>()` 销毁它。
- **激活着的时候**：包里的系统给每个提醒挂一个 `Due`（离 `UtcAt` 还有多久，第 9 节），到期时按 `(时间, 序号)` 给根投递 `Reminded<T>(UtcAt)`；处理方法返回后，同一项里 `UtcAt` 加一个 `Period`（`Period` 为 0 就销毁这个子实体）。从记录加载的提醒没有 `Due`（它不在记录里），由 `[With<Reminder<T>>, Without<Due>]` 的系统挂上；过了时刻的在第一个 tick 执行。
- **没激活的时候**：包里的系统把所有提醒里最小的 `UtcAt` 写进根上的 `NextDue`，检查点把它写进记录的 `next_due` 列，运行时的 `DueScan`（第 8 节）到时激活这个 Actor。所以提醒能叫醒一个停用了的 Actor，节点宕机也不丢。
- **至少一次，按 (提醒, `UtcAt`) 去重**：`UtcAt` 的前进和处理方法的改动在同一份记录里落定。宕机落在两次写记录之间时，重新激活后同一个 `UtcAt` 再执行一次；同一个激活里不会。
- 和 `Schedule(after)` 的分工：

  | | `e.Schedule(ev, after:)` | `DaServer.Reminders` |
  |---|---|---|
  | 时间 | 单调时间，精确到项 | UTC，精度是 `DueScan` 的间隔（秒级） |
  | 停用、宕机 | 丢失（迁移、热重启不丢） | 留在记录里，到时激活 Actor |
  | 投递 | 最多一次 | 至少一次，按 (提醒, `UtcAt`) 去重 |
  | 用途 | 技能前摇、buff、普通请求的延后重问 | 每日重置、拍卖结束、订阅续费 |

### `DaServer.Topics`：Actor 之间的发布订阅

```csharp
[NinoType] public record struct PriceChanged(int Item, long Price) : ITopicMessage;   // handled by any subscribing actor type
me.Subscribe(new Topic(Feeds.Prices));                                     // me: an entity of the subscribing actor
me.Publish(new Topic(Feeds.Prices), new PriceChanged(item, price));
[On<PriceChanged>] static void Reprice(in PriceChanged m, ref Shop shop) { … }   // in the subscriber's [Handles<K>] class
```

- `Topic(long Id)` 是包里内置的 Actor 类型，根上是订阅者表 `DynamicBuffer<Subscriber>`（`Subscriber(ActorId Actor)`，持久化）。`e.Subscribe(topic)`、`e.Unsubscribe(topic)` 是发给它的消息，订阅的是 `e.Root` 那个 Actor。`Feeds.Prices` 是用户自己的常量。
- **订阅也是订阅方的数据**：订阅方的根上有持久化的 `DynamicBuffer<Subscription>`。包里的系统在 `Activated` 之后重新订阅（幂等），在 `Deactivating` 时退订；所以 Topic 的表里只有激活着的订阅者，投递从不激活 Actor。投递到已经不在的订阅者时，那个节点回一条控制邮件，Topic 删掉它（它下次激活时自己再订阅）。
- **`e.Publish(topic, msg)`**：发给 Topic；Topic 不解码负载，按订阅者所在的节点分组，每个节点一帧（只序列化一次，池化的帧），那个节点再放进各订阅者的邮箱。`ICluster` 也能发布。
- **最多一次**；同一个发布者经同一个 Topic 发给同一个订阅者的按发送顺序到达。没有回放，没有持久的偏移：要持久的流（回放、消费组）用 Kafka 或 NATS JetStream，从 Actor 里调用。
- 一个 Topic 的订阅者数有上限（节点配置）；更大的扇出分到多个 Topic 键，推给客户端用 `ClientGroup`（第 9 节）。
- 今天只发给服务的 `Publish`（每个处理这条消息的服务类型都收到）在 W 里删掉（第 13 节）；Actor 之间的扇出由它做。

### 非 .NET 的客户端

- **客户端的线协议写成公开的规范**：帧格式、握手（令牌、`wire.manifest` 哈希）、`Ready`、重连和重新加入（槽号、`JoinConfirmed`、`PresenceLost`、`PresenceClosed`）、`Retire` 和 `HandedOff`、`Superseded`、`Unavailable`、`Busy` 的 `RetryAfter`、心跳和静默断开。C# SDK 是参考实现，一套一致性测试同时跑 C# 和其他实现。
- **TypeScript 客户端**（npm 包，经浏览器的 WebSocket 连 `/clients/ws`）：消息的编解码是 Nino 按 `Game.Protocol` 的 `wire.manifest` 生成的 TypeScript 代码，浏览器拿到和 Unity 一样的类型；槽、重新加入、`Retire`、退避按规范实现。
- Unreal / C++ 以后按同一份规范做。

### `DaServer.Replication`：组件同步到客户端

```csharp
[Replicated] public record struct Health(int Value) : IComponent, ITrackChanges;   // changed fields reach the clients
```

- `[Replicated]` 写在追踪变更的组件上（分析器检查 `ITrackChanges`，以及非托管或 `[NinoType]`）。实体同步给谁，由用户写的 `ReplicatedTo(ClientGroup Group)` 决定，例如 AOI 系统写它所在格子的组。
- 每个 Actor 类型一个包里的系统，`[ForEach, ChangedAny<所有 [Replicated] 类型>, With<ReplicatedTo>]`（EasyEcs 4.7 计划）：一个实体改了几个组件也只出一份补丁。生成器给每个类型生成按字段的位掩码，和上次发出的值（内部组件 `Replica<T>`）比较，每个 tick 按 `ClientGroup` 把改了的字段写进池化的帧，一个组一帧（序列化一次，网关扇出，第 9 节）。客户端看到的实体编号是包分配的 `ReplicaId`（每个 Actor 一个计数器），不是 `EntityId`。
- **移除**：包打开 `WorldOptions.DestroyLog`（容量是节点配置，EasyEcs 4.7 计划），每个组记着上次发出的 tick，用 `world.DestroyedSince(tick, span)` 取那之后销毁的实体，写成移除；不用给每个实体挂清理组件。
- **全量**：实体换组（`[Changed<ReplicatedTo>]`）时，新组收到它的全量、旧组收到移除；Presence 新订阅一个组时收到它订阅的组里所有实体的全量。`[Changed<Joined>]` 并且 `MissedPushes` 为真时，SDK 在加入时带着它最后应用的 tick：`DestroyedSince` 还够得着就只补那之后的移除和写入戳更新的组件，返回 false（日志已经被覆盖）才发全量。
- 客户端 SDK 应用补丁：C# 的 `DaServer.Replication.Client` 写进客户端的 EasyEcs world，TypeScript 的写进按 `ReplicaId` 索引的实体表。
- 最多一次，和推送一样；漏了的由全量补上。先在 mmo 样例上证明：它的 AOI 推送（`Nearby`）改成同步组件。

### 测试和管理的小工具

- **`TestCluster` 的故障注入**（`DaServer.Testing`）：`cluster.Partition(a, b)`（双向或单向）、`cluster.Kill(node)`（不经过优雅停止）、`cluster.DelayLinks(a, b, delay)`。用户用它测自己的逻辑在分区、宕机、慢链路下的结果，和框架自己的测试是同一套。
- **查看端点**（只读，也是 Aspire 命令）：一个 Actor 的子树、最忙的 Actor、每个 world 的系统、按组件计数；端点和规则见 [architecture.md 第 8 节](architecture.md#inspector)。
