# DaServer 新编程模型设计（草案 v28，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。ECS 一路到底：能用 EasyEcs 的功能就用 EasyEcs，状态都是数据，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个 Actor 发到哪个 Actor，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分，也看不到节点和 Actor 的地址；
- 分布式下也正确：同一个 Actor 只有一个激活能写，连接有先后，时间不依赖各节点的时钟一致；
- 高性能，高并发。

本稿是完整的一份。每条规则只在它所在的那一节写一次，别处只给链接。第 12 节是完整的例子，分别列出用户写的代码、生成的代码和测试。第 13 节是检查和版本记录，第 14 节是已定的取舍和转交 EasyEcs 的需求。

文中引用的 EasyEcs 文档都在它仓库的 `docs/content/` 下。本稿基于 EasyEcs 4.6：`entity.Run(ref job, filter)`、接口带的组过滤（`IGroupWith<T>`）、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（以上自 4.5），`world.Load(bytes, markWritten: false)`、`AddSystem(…, changesFromNow: true)`、带追踪组件的程序 Native AOT 0 警告（4.6）。

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
| Actor 类型（地址、根上的组件、系统组） | `[TickRate(30)] readonly partial record struct Map(long Id) : IActor<Map>;`；需要时再写 `[Stateless]`、`[OnError]`、`[IdleTimeout]`、`[ActivatedBy<T>]`、`[MaxScheduled]` | Server |
| 服务器之间的消息 | `[NinoType, From<Map>] record struct GrantItem(int Item) : IRequestTo<Player, bool>;` | Server |
| 本地事件 | `[NinoType] record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；持久化的写成 `[NinoType] record struct Grid(…) : IPersisted;`（第 10 节）；不随迁移的标 `[Transient]` | Server |
| prefab | `[Prefab<Map>] static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);`；根的写 `[RootPrefab<Map>]`；Actor 里的实体都用 `root.Spawn(prefab, …)` 建 | Server |
| 身份 | `[Claims] readonly partial record struct Account(long Id);`：令牌里的声明，用户自己的类型 | Server |
| 连上就加入 | `[JoinOnConnect<Account>] readonly partial record struct Player(long Id) : IActor<Player>;` | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 对消息和事件的反应 | `[Handles<Map>] static partial class Combat { [On<CastSkill>, Without<Stunned>] … }` | Server |
| 每 tick 或对数据的反应 | `[UpdateInGroup<Map>] class Movement : EcsSystem { [ForEach] … }` | Server |
| 本 Actor 里现在或以后做一件事 | `me.Schedule(new Strike(…), after: Skills.Windup(skill))`，不写 `after` 就是紧接着 | Server |
| 发给别的 Actor | `me.Send(new Player(id), new Defeated(map))` | Server |
| 等别的 Actor 的结果 | `me.Ask(new Player(who.Id), new GrantItem(item), new Looting(drop.Id, reply))`，回信是 `Answer<GrantItem, Looting>`；不带状态时是 `Answer<GrantItem>` | Server |
| 测试一个 Actor | `new TestActor<Map>(new Map(7), Registrations.Create())`；多个节点用 `TestCluster` | 测试 |
| 部署 | `cluster.AddNodes<MapServer>("map", replicas: 4)` | `Game.Server/apphost.cs`（第 2 节） |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `IActor<T>` | Actor 类型的接口：组件、值索引、EasyEcs 的系统组，带组过滤 `In<T>`（第 3 节） |
| `Connection` | 内置的 Actor 类型：每个客户端连接，在接受客户端的服务器上有一个实体，键是 `ClientId`（第 7 节） |
| `Entity` | 唯一的句柄。ECS 的成员来自 EasyEcs；发送、推送、加入、`Root` 等来自 DaServer（第 5 节） |
| `World` | 只做 ECS：找实体、读写组件、值索引。一个 world 里可能有很多个 Actor（第 2 节） |
| `ActorId` | 任意 Actor 的地址（Actor 类型的 `TypeId` 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键。只在服务端 |
| `TypeId<T>.Value` | 类型的 id（Nino 类型 id）。Actor 类型、消息、事件、组件都用这一个名字 |
| `Sender<K>` | 处理方法的参数：这条消息的发送方 |
| `[To]` | 客户端消息的字段：目标 Actor 的键（第 4 节） |
| `PresenceId`、`Presence`、`Joined`、`Leaving` | 客户端在某个 Actor 上的 Presence：Actor 里的一个子实体，例如地图上的 Avatar（第 7 节） |
| `Subscribed` | Presence 订阅的 `ClientGroup`：`DynamicBuffer<Subscribed>`（第 9 节） |
| `In<K>` | tag：这个实体属于一个 K 类型的 Actor。给系统组过滤用（第 5 节） |
| `ReplyTo<T>`、`Answer<TReq>`、`Answer<TReq, TState>`、`CallId` | 回复令牌（普通数据）；等别的 Actor 的结果，`TState` 是发问时存下的状态；调用 id 是幂等键；回信里 `a.Value` 是回复的值，`a.Status` 是结果（第 8 节） |
| `Activated`、`Activation`、`Deactivating` | Actor 的生命周期消息，用 `[On<T>]` 处理（第 6 节） |
| `PresenceLost` | `Connection` 上的通知：这个客户端的一个 Presence 没了（第 7 节） |
| `Incoming`、`Verdict` | `[Handles<Connection>]` 里每条客户端消息转发前的过滤（第 7 节） |
| `[Claims]`、`[JoinOnConnect<TClaims>]`、`Takeover` | 身份；连上就加入的 Actor 类型；谁胜出（第 7 节） |
| `IPersisted`、`[Transient]` | 持久化的组件：`IComponent, ITrackChanges`；不随迁移的组件（第 10 节） |
| `[TickRate(n)]` | 写在 Actor 类型上的帧率 |
| `[Stateless]` | 不保存、不迁移的 Actor 类型，只做当场回复（第 3 节） |
| `[OnError(…)]` | 处理方法抛出之后：`Resume`（默认）或 `Restart`（第 5 节） |
| `[IdleTimeout(秒)]` | 写在 Actor 类型上：空闲多久停用；不写用节点配置 `DaServer:IdleTimeout`（第 6 节） |
| `[ActivatedBy<T>]` | 只有 T 能激活这个 Actor 类型（第 6 节） |
| `[MaxScheduled(n)]` | 每个 Actor 待执行的延后事件上限，默认 65,536（第 9 节） |
| `Time` | world 的单例：`Tick`、`DeltaTime`、`TickRate`、`Elapsed`（单调时间，第 9 节） |
| `Scheduled<T>` | 本 Actor 事件的句柄，普通数据；`e.IsPending(s)`、`e.Cancel(s)` |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |
| `Status` | 请求的结果和拒绝原因：`Ok`、`TimedOut`、`Skipped`、`NotJoined`、`Busy`、`NoActor`、`NoOwner`、`NotHandled`、`Filtered`、`Superseded` 等（第 11 节） |
| `ICluster` | Actor 以外的入口（HTTP、后台、测试）：发服务器之间的消息，`Disconnect(clientId)`（第 4 节） |
| `Registration`、`TestActor<K>`、`TestCluster` | 生成的注册对象（实例，不是静态表）；一个 Actor 的测试台；进程内的多节点集群（第 12 节） |

用户不写、但在文档和诊断里会看到的名字：
- 客户端 SDK 的：槽号、`Ready`、`JoinConfirmed(槽号, 调用 id)`、`PresenceClosed(槽号)`、`PresenceLost(槽号)`；
- 框架内部的：`Route`、`Lost`、`Mail`、`Due`、`Ordered`、`Current`、`Outbox`（测试里按数据断言）、`Loading`、`Saving`、`Unsaved`、`Persisted`。

### 几个词

后文先用后讲的词，各一句：
- **主体**：一条消息或一个事件作用的实体，处理方法的组件参数都绑定到它（第 5 节）。
- **有序阶段**（`OrderedPhase`）：每个 tick 开头，把邮件和到期项按 `(时间, 序号)` 逐个执行的框架系统（第 1 节）。
- **即时链**：一项执行完，紧接着执行的不带 `after` 的 `Schedule`（第 5 节）。
- **`Due`**：到期时刻；延后事件、请求期限、加入确认期限都是带 `Due` 的实体（第 9 节）。
- **`Outbox`**：一项发出的东西先追加到这个单例，这一项执行完一起发出（第 5 节）。
- **Presence**：客户端在一个 Actor 上的存在，是这个 Actor 里的一个子实体（第 7 节）。
- **epoch**：激活时占的序号；写记录按它比较并交换，所以一个 Actor 只有一个激活能写（第 10 节）。
- **占位**：还没激活或正在写记录的 Actor 的根实体，邮件在它上面排队（第 6 节）。

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 值索引（`world.MapIndex`） | 为 `partial` 的 `IIndexed<T>` 记录结构生成显式实现 `T IIndexed<T>.Key => this` | 收到消息时查索引 |
| 逻辑属于哪个 Actor 类型 | — | `[Handles<K>]` | 分发表按 Actor 类型建 |
| 事件的顺序 | 有序值索引（`Range`）、单例 | `Due` | 运行时系统 `OrderedPhase` 按 `(时间, 序号)` 归并邮箱和到期项，状态在单例 `Ordered` 里 |
| 消息处理 | 单实体 job（`entity.Run(ref job[, filter])`，4.5）、`[With<T>]`、`[Without<T>]`、`INoStructuralChanges` | `[On<T>]` 生成 job 结构 | 逐个调用，逐个隔离异常 |
| 参数绑定 | `[ForEach]` 的参数规则 | 一个规则：组件参数绑定到主体 | job 拿到主体的组件 |
| 发出的东西 | 单例 | — | 单例 `Outbox`，每项执行完一次发出；当前项的上下文是单例 `Current` |
| 每 tick 的逻辑 | `EcsSystem`、`[ForEach]`、批量方法、`Parallel`、`[Changed<T>]`、`[UpdateAfter<T>]`、`ISystemGroup`、接口带的组过滤（`IGroupWith<T>`，4.5） | Actor 类型就是系统组，`[TickRate]`、`In<K>` | 有序阶段 → `world.Update<Map>()` → 框架的收尾系统 |
| 本 Actor 的事件 | `ChildOf`、有序值索引 | `Schedule(ev, after:)`、`Scheduled<T>`、`IEvent`、`Due` | 到期项和消息一起进有序阶段；发起者销毁时一起消失 |
| 发送方向 | — | `[From<K>]` 生成发送方法，分析器按调用处的 Actor 类型检查 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Handles<K>]`、`[To]` 生成路由表 | `Connection` 实体直接转发给目标 |
| 等待、回复 | `ChildOf` | `Answer<TReq>`、`Answer<TReq, TState>`、`ReplyTo<T>` | 调用 id、`Due` 期限、剩余时间传递 |
| Presence | 子实体、值索引、清理组件（`ICleanupComponent`、`Destroyed`） | `[JoinOnConnect]`、`Join()`、`Leaving` | 连上就加入、连接戳、写路由、确认、核对、释放路由（框架组 `Cleanup`） |
| 订阅 | `DynamicBuffer`、`[Changed<DynamicBuffer<T>>]` | `Subscribed`、`SetGroups` | 系统 `SyncSubscriptions` 同步网关扇出 |
| 生命周期 | tag | `[On<Activated>]`、对 `Leaving` 的系统 | 占位、激活 epoch、隔离、`Depart` |
| 一个 Actor 的实体 | `ChildOf`、`world.Children(map)`、prefab | `In<K>`、`root.Spawn(prefab, in a, in b)`、`e.Root` | 注册时的 prefab 副本 |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ICleanupComponent` | `IPersisted` | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`Descendants`、`SaveEntities`、`LoadEntitiesInto`（恢复时 `markWritten: false`） | `IPersisted`、`[Transient]` | 读写存储，epoch CAS，布局哈希 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热重启 | `world.Save`、`world.Load(…, markWritten: false)`（单例随 world 保存，系统不保存；加载的行不算变更，4.6） | `wire.manifest` 比对选路径、交接格式 | 两个 tick 之间停下 world，交给新进程；不重新激活 |
| 测试、诊断 | 单个 world、`SystemErrorPolicy.Throw`、`Capture`/`Restore`、`world.Count<Destroyed>()`、`world.Describe()` | `Registration`、`TestActor<K>` | 手动时钟、`Outbox` 是数据、分配断言 |
| 对象复用 | `DynamicBuffer` 池化、Nino 的 `[NinoPooled]` | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个 Actor 按 `(时间, 序号)` 逐条处理自己的消息和事件，Actor 之间并行。** 生命周期消息也是消息，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，同 Orleans：迁移那一两秒里，在途的消息可能后发先到。
   - 需要严格先后的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。**超时不等于没执行**：调用 id 是幂等键（第 8 节）。
4. **同一个 Actor 任一时刻最多一个激活能写记录**：激活先占 epoch，每次写都按 epoch 比较并交换；比较失败的激活被隔离（第 10 节）。
5. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。新连接的连接戳更大，旧连接留下的 Presence 被它接管（第 7 节）。
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
| a. 有序阶段 | 运行时系统 `OrderedPhase`：tick 窗口里的消息和到期项按 `(时间, 序号)` 归并（第 9 节） | 串行。每一项执行完，紧接着执行它的即时链（不带 `after` 的 `Schedule`），每项最多连锁 1,024 个；然后发出 `Outbox` |
| b. 系统 | `world.Update<Map>()`：这个 Actor 类型的 EasyEcs 系统（第 5 节） | 默认单线程；`[ForEach(Parallel = true)]` 的方法分块多线程 |
| c. 收尾 | 框架的系统：`Depart` 销毁带 `Leaving` 的 Presence，`SyncSubscriptions` 同步订阅（第 7、9 节）；然后框架组 `Cleanup` 处理已销毁实体留下的清理组件（`ReleaseRoutes` 等，第 6 节）；最后发出 `Outbox` | 单线程 |

- 有序阶段的状态（序号计数器、当前 tick 窗口、即时链预算）在运行时的单例 `Ordered` 里。有序阶段、收尾和清理都是 DaServer 自己的 `EcsSystem`，在框架的系统组里，每个只做一件事。
- 有序阶段开始时取一次邮箱快照；这期间新到的消息进下一帧。
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
1. 第 k 帧：A 的 `Strike` 到期时刻 t_A + 0.3 秒；B 移动；A 移动；B 的 `Strike` 到期时刻 t_B + 0.3 秒，比 A 的晚。两个都带着 `Due` 进有序索引。
2. 第 k+1 到 k+8 帧：B 继续发 `MoveTo` 走开，系统推进 NPC。
3. 到期的那一帧，有序阶段按时间执行：A 的 `Strike`、B 的 `Strike`，以及这一帧里时间夹在它们之间的消息（例如 B 在 A 到期前一刻发来的移动）。A 的结算读 B 此刻的位置：已经走出范围，就是闪避。如果 A 打死了 B，B 的 `Strike` 带 `[Without<Dead>]`，不执行。

**并行从哪里来：**

| 来源 | 做法 |
|---|---|
| 很多个 Actor | 节点里每个工作线程跑一个 world，一个 world 里有很多张地图；节点有好几个 world，集群有很多节点。TrinityCore 的 MapUpdater、EVE 的星系、LoL 和 Valorant 的对局都是这样 |
| 系统 | 一次 `world.Update<Map>()` 覆盖这个 world 上这个 Actor 类型的所有 Actor；重活的方法写 `Parallel = true` |

- Actor 内的有序阶段永远串行。没有上市的游戏把一个模拟步按空间切开并行：那需要框架懂位置、作用距离、最大速度这些游戏概念（违反 ADR-045），还会加一帧延迟，语义也脆弱。
- 以后再考虑：
  - 一个超大的 Actor，只有在基准测试显示它的有序阶段超出帧预算时，才提供用户自己给出分区的可选方案；
  - 无缝大世界用 New World 的做法：世界按格子分给进程，人移动时交接。

**不采用的：**
- 乐观执行（Time Warp 回滚）：保存状态或写反向计算的开销和一个约 1 µs 的事件本身差不多，而且回滚前已经推给客户端的结果收不回来。
- 按消息类型分批：会打乱 `(时间, 序号)`。只有系统按列批量处理。
- 把命令排到下一帧统一执行：等于把效果挪后，会出现"发包时打得到、执行时人跑了"。

**用户写的只有两种**：对消息和事件的反应（`[On<T>]` 方法，EasyEcs 的单实体 job），以及系统（`[UpdateInGroup<K>]` 的 `EcsSystem`）。第 12 节是完整的例子：`Maps` 加入地图，`Combat` 施法和结算。下面两段摘自第 12 节：

```csharp
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

[UpdateInGroup<Map>, UpdateAfter<Steering>]                                // the actor type is the system group
public class Movement : EcsSystem
{
    [ForEach, With<Steered>]                                               // a batch method: runs of rows as spans; Vectors is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Time time) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities),
            (float)time.DeltaTime.TotalSeconds);
}
```

用户不判断"立即还是延后"（`after` 来自数据，0 也合法），也不判断"每条消息还是每 tick"（反应写 `[On<T>]`，每 tick 和对数据的反应写系统）。

**用户能看到的反馈：**
- 每个 Actor 的指标：每个 tick 有序阶段的耗时，由运行时自己按 Actor 测量；
- 每个系统的耗时：EasyEcs 的 `MeasureSystems` 和 `system.Stats`。它按 world 统计每个系统，一次 `world.Update<Map>()` 覆盖这个 world 上所有的 Map，所以是这些 Actor 合在一起的值，不分 Actor；
- `world.Count<Destroyed>()` 导出为指标：已销毁、清理组件还没处理完的实体数。一直涨，说明有清理系统没跑或者漏了（第 6 节）；
- 管理端点返回 `world.Describe()`：每个 world 的表、行数和内存（它会分配，只按请求调用）；
- 并行和串行结果一致的核对，是一个测试（第 12 节的 `TestActor<K>`）。

---

## 2. 服务器与节点

```csharp
// Game.Server
[Hosts<Player>, Hosts<Guild>] public struct GameServer    : IServer;
[Hosts<Map>]                  public struct MapServer     : IServer;
[Hosts<Connection>]           public struct GatewayServer : IServer;   // hosting Connection = accepting clients
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
- **节点代数**：节点每次加入集群，由集群分配一个单调递增的代数（不是进程里随机生成的数）。同名的节点重启后是新的代数，不会被当成原来那个。热重启不算重新加入：代数不变，化身号加一（第 10 节）。
- **world**：节点里有几个工作线程，每个线程跑一个 EasyEcs world；节点承载的所有 Actor 类型共用它，Actor 落在哪个 world 由键的哈希决定。world 的个数由节点按分到的 CPU 自己定（`DaServer:Worlds` 可以覆盖），用户代码和 AppHost 都不写它；框架内部把"一个线程加一个 world"叫 shard。
- **帧率**写在 Actor 类型上（第 5 节），不在配置里。
- **Program.cs**：五行，把生成的 `Registrations.Create()` 交给节点（第 12 节）。没有静态的注册表。
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
  - 运行：`dotnet run --file apphost.cs`（或 `aspire run`）；发布：`aspire publish`。在这个目录里直接 `dotnet run` 跑的是节点本身。
  - 本地运行时，DaServer.Aspire 先把服务端编译一次，再把节点作为可执行文件直接启动（每个副本一个进程，不套 `dotnet run`，调试器按进程附加）。默认是 JIT；`dotnet run --file apphost.cs -- Aot=true` 先按 Native AOT 发布，再跑本机二进制，和线上镜像是同一种程序。
  - `DaServer` 包的构建属性把 `apphost.cs` 移出服务端工程的编译（`<Compile Remove="apphost.cs" />`），用户不用写。
  - **AppHost 不引用服务端程序集**：`#:project` 只给 Aspire 工程元数据（`Projects.Game_Server`）。DaServer.Aspire 的生成器读这个工程提交的 `wire.manifest`，只读 `actor` 和 `hosted-by`（第 10 节），在 AppHost 里生成同名的服务器类型，每个都绑定到这个工程：`AddNodes<MapServer>` 起的是 Game.Server 的二进制，节点跑 `MapServer`。
  - **只有一个 `IServer`**（DaServer.Core）：服务端的服务器类型和 AppHost 里生成的同名类型都实现它。`AddNodes<T>(string name, int replicas) where T : struct, IServer` 返回 `DaServerNodeGroup<T>`。
  - **两个名字各管一件事**：`name` 是 Aspire 的资源名，也是 Pod 名和节点名的前缀（`map-0`、`map-1`）；服务器类型的身份是它的 `FullName`（`Game.Server.MapServer`），节点启动时按它和 `wire.manifest` 对上。集群名和组名都是资源名，不能重名：集群叫 `mmo`，组叫 `game`、`map`、`gate`。
  - **记录存储**：`WithActorStore(actors)` 给承载有状态 Actor 的组接上 DaServer.Persistence.PostgreSql；只承载 `Connection` 和 `[Stateless]` 的组不用。签名密钥由 `AddDaServerCluster` 生成（参数 `{cluster}-secret`），用户不写。
  - **端口按节点组在 AppHost 里显式声明。** 一个组的所有副本监听同样的端口（Kubernetes 上是同一个模板，各 Pod 有自己的 IP）：
    - 集群端口和 HTTP 端口（健康检查、管理端点）由 DaServer.Aspire 分配，只在集群内部可达；
    - 对外的只有 AppHost 写出来的：`WithClientConnections(port)` 是客户端的 TCP / WebSocket 端口，默认在一个负载均衡的地址后面（负载均衡器的种类是应用和平台的事）；对外的 HTTP（例如 webhook）用 Aspire 自己的 `WithExternalHttpEndpoints()`；
    - 客户端程序（例如压测机器人）用 `WithClientEndpoints(gateways)` 拿到网关的地址；
    - 不做：每个副本单独的公网地址（Agones 那样的直连），以后由一个直连特性加上。
  - **客户端端口和 `[Hosts<Connection>]` 互相对上**：承载 `Connection` 的服务器，生成的类型还实现 `IAcceptsClients`；`WithClientConnections(port)` 是 `DaServerNodeGroup<T>` 上 `where T : IAcceptsClients` 的扩展方法，给别的服务器开客户端端口编译不过。反过来，承载 `Connection` 的服务器在 AppHost 里没写 `WithClientConnections`，构建报错。消息引用的 Actor 类型没有节点承载时，构建也报错。
  - 节点启动时，配置的服务器类型不存在，或者和 `wire.manifest` 对不上，启动失败。
  - **热重启命令**是 `{cluster}-restart`：AppHost 里的命令，例如 `aspire do mmo-restart`，背后是节点的管理端点（第 10 节）。
  - 限制：Visual Studio 还打不开单文件 AppHost（VS Code、Rider 和命令行可以）；想要的团队照样可以另建一个 AppHost 工程，代码相同。

---

## 3. Actor 类型与地址

**Actor 类型就是它的地址，也是根实体上的组件，也是它的系统组。**

```csharp
// Game.Server
[JoinOnConnect<Account>] public readonly partial record struct Player(long Id) : IActor<Player>;   // §7
public readonly partial record struct Guild(long Id) : IActor<Guild>;
[TickRate(30)] public readonly partial record struct Map(long Id) : IActor<Map>;   // 30 ticks a second
public readonly partial record struct Room(long Id)  : IActor<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IActor<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个 Actor 的地址。消息和组件里存别的 Actor 的地址，直接用 `Map`、`Player` 类型的字段。要存"任意 Actor 的地址"时用 `ActorId`。地址只在服务端：客户端看不到 `ActorId`、`PresenceId`、`EntityId`，也看不到节点（第 4 节）。

**同一个值也是组件**：地图的根实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 处理方法里知道自己是谁：主体是根时取组件参数 `in Map map`；主体是子实体时用 `me.Root`（第 5 节）。

**接口**：`IActor<T>` 继承 `IComponent`、`IIndexed<T>` 和 `ISystemGroup`，并带着组过滤 `In<T>`：

```csharp
public interface IActor<TSelf> : IComponent, IIndexed<TSelf>, ISystemGroup, IGroupWith<In<TSelf>>
    where TSelf : struct, IActor<TSelf>, IEquatable<TSelf>;   // IIndexed<T> requires T : IEquatable<T>; a record struct has it
```

- EasyEcs 认源码里声明的接口，包括继承来的：它的生成器和分析器都用 `AllInterfaces` 判断（`Symbols.Implements`）。所以：
  - `[UpdateInGroup<TGroup>]` 的约束是 `struct, ISystemGroup`，C# 认继承来的接口；
  - EasyEcs 的分析器判断"是不是系统组"时，EECS023 不会误报。
  - EasyEcs 看不到的，只有别的生成器补上的接口和特性。所以要让 EasyEcs 看见的东西，都由用户写的接口或特性带着（同第 10 节的 `IPersisted`）。
- **组过滤由接口带上（EasyEcs 4.5 的 `IGroupWith<T>`）**：EasyEcs 从组类型实现的接口上读组过滤，`IActor<TSelf>` 带着 `In<TSelf>`，所以 `[UpdateInGroup<Map>]` 的每个 `[ForEach]` 方法都只看 Map 的实体，用户不写 `[With<In<Map>>]`。不能写成接口上的特性 `[With<In<TSelf>>]`：C# 不允许特性的类型实参用类型形参（CS8968）。
- **`Key` 由生成器补上，写成显式接口实现** `Map IIndexed<Map>.Key => this;`：任何实现 `IIndexed<T>`、键就是它自己的 `partial record struct` 都一样，Actor 类型、`Avatar`、`Cell`、`NetId` 都是。用户不写 `Key`。不能写成公有属性 `public Map Key => this;`：record 合成的 `ToString()` 会打印 `Key`，`Key` 又是自己，无限递归到栈溢出。
- EasyEcs 生成的 `HasX` 是属性：写 `e.HasCasting`，不是 `e.HasCasting()`。
- Actor 类型的 id 是 `TypeId<Map>.Value`，没有单独的常量。
- `[TickRate]` 的规则在第 5 节。

**子实体的值索引，键里带上 Actor 的键**：一个 world 里有很多 Actor，所以子实体的键必须带着它所在的 Actor，否则两张地图的同名键会撞。写法只有一种：第一个字段是 Actor 类型，例如 `Avatar(Map Map, Player Player)`、`NetId(Map Map, int Value)`、`Cell(Map Map, int X, int Y)`。Actor 的键不随迁移和恢复改变，不用改写；不用根的 `EntityId` 作键。框架的 `PresenceId`（全 world 唯一的随机值）和 `Due`（有意跨 Actor，按 world 排序）除外。

**只有运行时能写 Actor 类型组件：**
- 分析器禁止用户代码对 Actor 类型组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时建占位时断言这个键在索引里还不存在。占位就是以后的根（第 6 节），键从建占位起一直在它上面，不搬动。
- 运行时按键找实体用 `world.Index<K, K>()`：泛型的 `Actor<K>` 自己取一次、按 world 缓存，生成的注册代码里没有查找委托（第 12 节）。

**键**：
- Actor 类型的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[Stateless]` Actor 类型**：不保存，不迁移；每个节点可以有自己的 Actor，键只用来选 Actor。它的地址不含节点，回信、确认和到期都找不回同一个实例，所以分析器禁止在它上面 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。它只做"收到请求，当场回复"。
- **子实体不可寻址**，例如 Avatar、等待中的请求、延后的事件。它们跟根一起保存、一起迁移。
- **没有"无键 Actor"**：
  - 每个节点一份的状态，用单例；
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
  - 带 `[To]` 字段的消息，发给键等于这个字段的 Actor，例如 `EnterMap(7)` 发给 `Map(7)`。客户端只写了一个数，不知道它是哪种 Actor 的键；
  - 不带 `[To]` 的消息，发给这个客户端在该 Actor 类型上当前的 Presence（第 7 节）；
  - `[To]` 最多一个字段，类型必须和处理 Actor 类型的键形状一致（`Map(long Id)` 对 `long`；多字段的键对同样字段的结构），分析器检查。`[Handles<Connection>]` 处理的消息不能有 `[To]`。
- **客户端消息直接到目标**：网关上的 `Connection` 实体过滤后直接转发给目标 Actor，中间不经过 Player（第 7 节）。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Map>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;
[NinoType, From<Map>] public record struct Defeated(Map Where) : IMessageTo<Player>;
```

- **接收方写在接口上**：`IMessageTo<TTo>` 是单向消息，`IRequestTo<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。客户端只能发 `IFromClient`。
- **消息里不写发送方的键**：接收方取 `Sender<K> sender` 参数，`sender.Key` 就是发送方。
- **方向由生成的方法和分析器保证**：
  - 每条消息生成 `Entity` 上的 `Send`/`Ask` 方法，参数类型是接收方的 Actor 类型，所以接收方写错编译不过；
  - `Entity` 不带 Actor 类型，所以发送方由调用处决定：`[Handles<K>]` 类和 `[UpdateInGroup<K>]` 系统里，K 不在消息的 `[From<…>]` 里，分析器报错；在别处（帮助类）调用时，运行时断言发送实体所属 Actor 的类型。
  - 这是帮助写对的手段，不是安全边界。安全边界在网关：只转发 `IFromClient`。
- **发送方是哪个 Actor**：发送实体所属的 Actor（`e.Root`），见第 5 节的操作表。
- **客户端上下文自动带上**：处理客户端消息、`PresenceLost` 时，发出的请求信封里带着这个客户端的上下文（`ClientId`、所在节点、连接戳、身份、客户端的调用 id），和期限一样。接收方在处理这个请求时可以 `Join()`（第 7 节）。
  - 用户不接触上下文本身；它在运行时的单例 `Current` 里（第 5 节）。
- **`ICluster`**（HTTP、后台、测试）不属于任何 Actor，可以发任何服务器之间的消息，回复回到调用方。
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Connection` 的节点，由连着它的那个处理。

### 本地事件（Server）

```csharp
[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;
```

- 本地事件只在一个 Actor 里传：`e.Schedule(ev)` 发给本 Actor 的一个实体，由同一个 Actor 类型的 `[On<T>]` 处理。
- 带 `[NinoType]`：延后的事件随 Actor 迁移。
- 影响别的 Actor 用消息（`e.Send(to, msg)`）。`Schedule` 只收 `IEvent`，`Send` 只收服务器之间的消息，类型约束就分开了它们，不用分析器。

---

## 5. 逻辑

逻辑只有两种写法：
- 对消息和事件的反应：`[Handles<K>]` 的 `static partial class` 里的 `[On<T>]` 方法；
- 每 tick 的逻辑和对数据的反应：`[UpdateInGroup<K>]` 的 EasyEcs 系统。

```csharp
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>, Without<Dead>] static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed) { … }
}
```

- `[Handles<K>]` 写一次，类里所有方法都属于 Actor 类型 K：客户端消息发给 K。
- 一个 Actor 类型可以有多个类，例如 `Maps`、`Moves`、`Combat`。

### `[On<T>]` 的 T

| T | 是什么 | 例子 |
|---|---|---|
| 服务器之间的消息 | 消息的 `IMessageTo<K>`、`IRequestTo<K, R>` 必须是类上的 K | `[On<GrantItem>]` |
| 客户端消息 | 发给类上的 K | `[On<EnterMap>]`、`[On<MoveTo>]` |
| `Incoming` | 只在 `[Handles<Connection>]` 里：每条客户端消息转发前的过滤（第 7 节） | `[On<Incoming>]` |
| `PresenceLost` | 只在 `[Handles<Connection>]` 里：这个客户端的一个 Presence 没了（第 7 节） | `[On<PresenceLost>]` |
| 生命周期消息 | 框架发给 Actor 的（第 6 节） | `[On<Activated>]`、`[On<Deactivating>]` |
| 回信 | 等别的 Actor 的结果（第 8 节） | `[On<Answer<GrantItem, Looting>>]`、`[On<Answer<GrantItem>>]` |
| 本地事件 | `IEvent`，由 `Schedule` 发起 | `[On<Strike>]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个处理 Actor 类型。
- 离开不是消息，是数据：`Leave()` 加上 `Leaving`，由系统反应（第 7 节）。

### 处理方法是 EasyEcs 的单实体 job

生成器把每个 `[On<T>]` 方法编成一个 job 结构，和 EasyEcs 为 `[ForEach]` 方法生成的一样（`IForEachEntity<T1..Tn>`）。运行时用 `subject.Run<T1..Tn, TJob>(ref job)` 在主体上执行一次，方法有过滤时用 `subject.Run<T1..Tn, TJob>(ref job, in Filter)`，`Filter` 是 job 上的静态字段（EasyEcs 4.5；基准是同样工作手写代码的 0.69–1.06 倍，0 分配）。所以处理方法的参数规则就是 EasyEcs `[ForEach]` 的规则，只有一套：
- **`ref` 参数直接指向存储**。方法体里改了主体的结构（加 tag、建实体、`Ask` 等），`ref` 参数照样可写：EasyEcs 在返回后把写入搬到新行；`in` 参数保持改之前的值（`guide/refs-and-await.mdx` 的 "Inside passes"）。job 只声明它写的组件（`Writes`），追踪变更的组件只在被写时打戳。
- **过滤**：`[With<T>]`、`[Without<T>]` 写在方法上，和 `[ForEach]` 一样。生成器把它编成 job 上的 `static readonly QueryFilter Filter`，建一次；没有过滤的方法不带 filter 调用 `Run`，不在每次调用时 `QueryFilter.Create()`。主体缺少组件参数或不满足过滤时不调用：请求以 `Skipped` 失败，单向消息和事件跳过（计数）。
- **组件参数都是必有的**：`IForEachEntity` 只收必有的组件，所以没有 `Maybe<T>` 参数。可能没有的数据在方法体里用 `TryGet<T>` 或生成的 `TryGetX(out …)` 读。
- **`INoStructuralChanges`**：方法没有 `World` 参数，`Entity` 参数只用来读、或者只交给不改结构的操作（`Send`、`Push`、`Reply`、`Broadcast`、`Root`）时，生成的 job 带上它，推断规则同 EasyEcs（`guide/structural-changes.mdx` 的 "Passes that freeze the structure"）。`Schedule`、`Ask`、`Join`、`Leave`、`Spawn`、`Deactivate` 会建实体或改 tag，算结构变更。带它的 job 运行时不做逐行记账，结构被冻结，违约就抛出。
- **检查**：EasyEcs 的分析器只检查 `[ForEach]` 方法，不认识 `[On<T>]`。DaServer 的分析器对 `[On<T>]` 方法套用同样的检查（参数、过滤、有索引的组件、结构变更），见第 13 节。
- 消息、发送方、回复令牌、身份、单例是 job 的字段，由分发表在调用前填好。
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
| 服务器之间的消息、`Activated`、`Deactivating` | 接收 Actor 的根实体，例如地图 |
| `Answer` | 发问的实体（第 8 节） |

- **自己的键**：主体是根时，取 Actor 类型组件参数 `in Map map`。主体是子实体时用 **`me.Root`**：DaServer 在 `Entity` 上的扩展，沿 `ChildOf` 走到没有父的实体，就是 Actor 的根，例如 `me.Root.Map`、`me.Root.Grid`。
- **例外：身份参数**：`[Claims]` 类型的参数（例如 `in Account who`）不绑定到主体，绑定到发这条客户端消息的客户端的身份。身份随信封带着（不超过 16 字节）。只能用在客户端消息的处理方法里（分析器检查）。

**别的实体在方法体里显式读写：**

```csharp
if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
ref var hp = ref target.Health;
var grid = me.Root.Grid;
```

- 首选的写法：发送方把接收方需要的数据放进消息，接收方少读别的实体。
- 用 EasyEcs 生成的成员：tag 是 `me.IsDead`，组件是 `target.Health`，可能没有的组件用 `TryGetX(out …)`，主体上的也一样。
- 对主体的条件写成过滤（`[Without<Dead>]`），不写成方法体里的 `if (me.IsDead) return;`。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Entity` | 主体 |
| 组件 `in`/`ref` | 主体上的组件。缺少时不调用（上面的过滤）。有索引的组件（包括 Actor 类型组件、`ChildOf`）只能是 `in` |
| 身份 `in TClaims` | 发消息的客户端的身份（上面的例外） |
| `ref DynamicBuffer<T>` | 主体上的缓冲 |
| `Sender<K>` | 发送方。消息有多个 `[From<…>]` 时写 `Sender<ActorId>` |
| `in ReplyTo<T>` | 请求的回复令牌，要晚些回复时取；`reply.Call` 是调用 id |
| 单例 `in`/`ref` | 本 world 的单例；`Time` 只能 `in` |
| `World` | ECS 操作 |

### 操作

只有一个句柄：`Entity`。所有发送、推送、加入的操作都是 `Entity` 上的扩展方法（DaServer 提供，通过实体所在的 world 找到 world），只写在这张表里：

| 写法 | 做什么 |
|---|---|
| `e.Schedule(ev)` | 本地事件，`ev` 是 `IEvent`，发给本 Actor 的实体 `e`；当前项返回后、下一项之前执行（即时链，最多 1,024 个）。返回 `Scheduled<T>` |
| `e.Schedule(ev, after: t)` | 同上，到期时刻 = 当前项的时间 + t（第 9 节）；`t` 为 `TimeSpan.Zero` 时同上一行 |
| `e.IsPending<T>()`、`e.Cancel<T>()` | `e` 上还有没执行的 T 事件吗；取消它们（第 9 节） |
| `e.IsPending(s)`、`e.Cancel(s)` | `e.Schedule` 返回的那一个事件还在等吗；取消它 |
| `e.Send(to, msg)` | 服务器之间的单向消息，只发给别的 Actor，例如 `me.Send(new Player(id), new Defeated(map))` |
| `e.Ask(to, req[, state][, call: id])` | 请求，回信是 `Answer`，主体是 `e`；`call` 用同一个调用 id 重试（第 8 节） |
| `e.Reply(in token, value)` | 用回复令牌回复；令牌是 `default` 时什么也不做 |
| `e.Push(msg)` | `e` 是 Presence：推给它的客户端 |
| `e.Push(in group, msg)` | 推给一个 `ClientGroup`（第 9 节） |
| `root.Broadcast(msg)` | `root` 是 Actor 的根：推给这个 Actor 所有带 `Joined` 的 Presence |
| `e.SetGroups(groups)` | `e` 是 Presence：把它的 `DynamicBuffer<Subscribed>` 换成这些组（第 9 节） |
| `root.Spawn(prefab[, in a[, in b …]])` | 在 Actor 里建实体：用注册时的 prefab 副本，一次 `Set(new ChildOf(root), a, b …)` 写上父和给的组件（最多一次行移动），当场返回实体 |
| `e.Join()` | 让 `e` 成为当前客户端在本 Actor 上的 Presence，返回 `PresenceId`（第 7 节） |
| `e.Leave()` | Presence `e` 离开：加上 `Leaving`（第 7 节） |
| `root.Deactivate()` | `root` 是 Actor 的根：停用这个 Actor（第 6 节）。不是根时运行时断言；分析器看得出来时报错 |
| `connection.Disconnect()` | `connection` 是 `Connection` 的根：断开这个客户端 |
| `e.Root` | `e` 所在 Actor 的根 |

- **跨 Actor 的操作，发送方是 `e.Root` 那个 Actor。**
- **发出的东西先是数据**：`Send`、`Ask`、`Reply`、`Push`、`Broadcast` 和 `Join()` 的路由，都追加到运行时的单例 `Outbox`；当前项执行完（连同即时链），运行时按追加的顺序一次发出。所以 `Join()` 的路由一定排在回复前面（第 7 节），测试可以直接断言 `Outbox`（第 12 节）。
- **当前项的上下文也是数据**：运行时的单例 `Current` 里是当前项的时间、根、发送方和客户端上下文。操作从它取客户端上下文；系统里没有客户端上下文。
- `Send`/`Ask` 的方向检查见第 4 节。
- 用户不判断"立即还是延后"：`after` 来自数据，0 也是合法值。
- 本地事件只能发给本 Actor 的实体，运行时断言。

### 每 tick：Actor 类型就是系统组

每 tick 的逻辑就是普通的 EasyEcs 系统，DaServer 不另造一套：

```csharp
// Game.Server: the actor type carries its tick rate; its group filter In<Map> comes with IActor<Map>
[TickRate(30)] public readonly partial record struct Map(long Id) : IActor<Map>;

// Game.Server, anywhere in the project
[UpdateInGroup<Map>]
public class Steering : EcsSystem
{
    [ForEach(Parallel = true), With<Steered>] static void Steer(in Position p, in Destination d, in Speed s, ref Velocity v) { … }
}
```

- **Actor 类型就是它的系统组**：`IActor<T>` 继承 `ISystemGroup`（第 3 节），`[UpdateInGroup<Map>]` 就是"每个 Map tick 跑一次"。不另写组类型。
- **帧率写在 Actor 类型上**：`[TickRate(30)]`。
  - 帧率写进 `wire.manifest` 的 Server 一段；改帧率就是改了 Server，要滚动升级。
  - 一种服务器承载多个有 tick 的 Actor 类型时，各帧率必须整除其中最快的一个（例如 60、30、10），避免节拍抖动。
  - 没有 `[TickRate]` 的 Actor 类型也可以有系统：每批邮件之后跑（第 1 节）。
- **只处理自己的实体**：组过滤 `In<K>` 由 `IActor<K>` 带上（`IGroupWith<In<K>>`），加到这个组每个系统的每个 `[ForEach]` 方法上（EasyEcs `guide/systems.mdx` 的 "Group filters"）。
  - 查询缓存只存匹配的表，所以同一个 world 上别的 Actor 类型的表，对这个组的系统不增加任何开销（`guide/systems.mdx` 的 "What it costs"，`why-fast.mdx` 的 "query cache"）。
  - `In<K>` 是框架的 tag（`public struct In<T> : IComponent where T : struct, IActor<T>;`），K 类型 Actor 的每个实体都带着：
    - 根：运行时激活时加上；
    - `root.Spawn(prefab, in a, in b)`：`Registration` 注册 `[Prefab<K>]` 时做一份副本（EasyEcs 的 `new Prefab(basePrefab)`），带上 `In<K>` 和一列 `ChildOf(default)`，静态的 prefab 不被改动。`Spawn` 实例化副本，再一次 `Set(new ChildOf(root), a, b)`：`ChildOf` 已经有列，只写值；`a`、`b` 是 prefab 没有的组件时，整个实体只移动一次行；
    - Actor 里不用 `CreateEntity`（分析器报错），一律 `root.Spawn`，所以用户从不自己加 `In<K>`。
- **怎么跑**：每个 world 的线程在每个 tick 依次调用框架组 `Ordering`（`OrderedPhase`）、`world.Update<Map>()`、框架组 `Settling`（`Depart`、`SyncSubscriptions`），最后是框架组 `Cleanup`（第 6 节）。`Ordering` 和 `Settling` 的系统是 DaServer 自己写的 `EcsSystem`，在 `OnUpdate` 里用按 Actor 类型建好的 `static readonly QueryFilter`（`.With<In<K>>()`）跑 job，所以不需要以类型形参为实参的特性。一个 world 上同一 Actor 类型的 Actor 共用 tick 边界，所以一次覆盖这个 world 上这个 Actor 类型的所有 Actor。
- **系统就是 EasyEcs 的系统**：`[ForEach]` 方法、批量方法（`Span<T>`）、`[Changed<T>]`、`[With<T>]`、`[UpdateAfter<T>]`、`UpdateInterval`，规则全按 EasyEcs 的文档。生成代码是 EasyEcs 自己的生成器写的。
- **默认单线程**。`[ForEach(Parallel = true)]` 只给重活用（EasyEcs 的建议）：各个 world 本来就已经占满各个核。
  - **每个 world 设 `WorldOptions.MaxDegreeOfParallelism` = 节点的核数 / 节点的 world 数，最少 1**（EasyEcs D36：并行预算按 world 设，不跨 world 共享；`guide/threading.mdx`、`guide/hosting.mdx`）。默认值是全部核数，八个 world 各开满就是八倍超额。
  - 并行方法按块切行，一块至少 1,024 行（`guide/systems.mdx` 的 "Parallel methods"），所以实体少的 world 本来就单线程跑，开了也不多花。
- **系统里的发送**：`Entity` 上的操作只能在非并行的系统里用，追加到 `Outbox`；`Parallel = true` 的方法里用，分析器报错。
- `Time` 在系统里是这个 tick 的值，`Time.Elapsed` 是 tick 时刻 t（第 9 节）。
- **`[Changed<T>]` 看的是"写过"，不是"变了"**：被系统每 tick 改写的组件，每 tick 都算写入。例如被驱动的 NPC，`Steering` 和 `Movement` 每 tick 改写它的 `Velocity` 和 `Position`，所以第 12 节的 `CellAssign` 和 `Diff` 每 tick 都访问它们（跨格才写 `Cell`，速度和上次发出的相同就不发）。这是接受的取舍：多的只是一趟按列的遍历和比较。

### 其他

- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理；节点上用户的系统用 `SystemErrorPolicy.Disable`（第 11 节），`TestActor<K>` 用 `Throw`，让测试直接失败（第 12 节）。
- **`Registration`** 是生成的一个实例对象，不是静态表：Actor 类型、prefab 副本、身份、连上就加入、分发表、路由表、系统。节点、`TestCluster`、`TestActor<K>` 都接这个对象。表在构造时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理方法抛出异常

**不回滚。** EasyEcs 的结构变更当场生效，没有命令缓冲（EasyEcs `decisions.mdx` D2），组件写入也是当场的。所以抛出时：
- 抛出之前做的修改都留着：建的实体、加删的组件和 tag、经 `ref` 参数和方法体写的组件；结构变更之后经 `ref` 参数的写入，EasyEcs 在 job 抛出时也搬到新行（4.5 的 `entity.Run` 保证）；
- 已经追加进 `Outbox` 的照样发出；
- 这条消息的请求以失败结束；它的即时链里还没执行的事件作废。

之后怎么办，看 Actor 类型上的 `[OnError]`：
- `Resume`（默认）：实体就是现在的样子，接着处理下一项；
- `Restart`：不写记录，不进 `Leaving`，不投递 `Deactivating`，直接销毁整棵子树，之后从最后一次写成的记录重新激活。上次写记录之后的改动丢失。Presence 是清理组件，销毁时框架释放它们的路由，客户端 SDK 收到 `PresenceLost` 后重新发加入消息（第 7 节）。

- 分析器给建议：**先校验，最后做结构变更**。方法里结构变更之后还有 `throw` 或可能抛出的调用时，给出提示。第 12 节的 `Resolve` 就是这样写的。
- 一项的即时链超过 1,024 个，或者一个 Actor 待执行的延后事件超过上限（第 9 节），也按 `[OnError]` 处理。
- **`[On<Activated>]` 抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次。
  - 这样不会留下初始化了一半的 Actor。
- `Connection` 抛出异常时，断开这个客户端。

---

## 6. 生命周期

生命周期消息是框架发的，用 `[On<T>]` 处理，和其他事件排在同一条 `(时间, 序号)` 线上：

| 消息 | 主体 | 何时 | 常见用途 |
|---|---|---|---|
| `Activated(Activation Origin)` | 根 | Actor 激活后的第一条邮件；`enum Activation { Created, Loaded, Migrated }` | 初始化 |
| `Deactivating` | 根 | 停用前的最后一条邮件 | 收尾 |

**`Activated` 是消息，不是 tag**：初始化必须在排队的邮件之前执行，而系统在有序阶段之后才跑，所以它只能是第一条邮件。

`PresenceLost` 不在这张表里：它不是 Actor 的生命周期，是 `Connection` 上的通知（第 7 节）。

离开、确认这些不需要排在邮件前面的，都是数据：`Leaving`、`Joined`（第 7 节），由系统反应。

**占位是 ECS 数据，也是以后的根**（EasyEcs `guide/migration-and-unload.mdx` 的 "A record of what persists"：先有实体，记录后到，装到它上面）：一个键在"还没有激活"或"正在写记录"时，它的根实体是一个占位。占位按注册时的根 prefab 副本建，这份副本不带 `In<K>`；占位上还有 Actor 类型组件（键）、一个 tag（`Loading` 或 `Saving`）和等着的邮件（框架内部的 `DynamicBuffer<Mail>`）。没有 `In<K>`，所以系统和处理方法都匹配不到它。

| 状态 | 根实体 | 怎么进入 | 发生什么 |
|---|---|---|---|
| 加载中 | 占位，带 `Loading` | 第一条消息到达 | 邮件留在占位上；异步占 epoch、读记录（第 10 节） |
| 激活 | 同一个实体，成为根 | epoch 占到了，记录读到了或者没有记录 | 有记录时 `LoadEntitiesInto(record, [占位, n−1 个空实体], markWritten: false)`（第 10 节）；然后加上 `In<K>`，去掉 `Loading`。键和邮件本来就在它上面，不搬动；邮箱里第一条是 `Activated`，后面才是等着的邮件 |
| 运行 | 完整的根实体 | — | 按 `(时间, 序号)` 执行消息和事件，再跑系统和收尾（第 1 节） |
| 停用 | 完整的根实体 → 占位，带 `Saving` | 空闲超过 `[IdleTimeout]`（有 Presence 时不算空闲），或者调用 `root.Deactivate()` | 依次：<br>1. 对每个 Presence `Leave()`；<br>2. 这一轮的系统和收尾跑完（用户对 `Leaving` 的反应、`Depart`）后，投递 `Deactivating`，它之后不会再有别的邮件；<br>3. `SaveEntities` 写进缓冲，销毁整棵子树（待执行的延后事件随之消失），换成带 `Saving` 的占位，之后的邮件留在占位上；<br>4. 异步写记录，按 epoch 比较并交换；<br>5. 写成功后：占位上有邮件，就变成 `Loading`，读到的是刚写好的记录；没有就销毁占位 |
| 隔离 | 销毁 | 写记录时 epoch 比较失败，或者本节点被移出成员视图 | 不写记录、不进 `Leaving`、不投递 `Deactivating`，直接销毁整棵子树；Presence 的路由由清理组件释放（第 7 节）；排队的请求以 `NoOwner` 失败 |

- 写记录失败就重试，绝不丢弃；epoch 比较失败不是失败，是隔离。
- `[OnError] Restart` 不走这张表的停用，见第 5 节。
- **框架销毁整棵子树之前，先从子孙上移除不该触发清理的清理组件。** 销毁带清理组件（`ICleanupComponent`）的实体时，EasyEcs 不释放它：它失去其他所有组件（包括 `In<K>`），离开所有索引，带上 `Destroyed`，等清理系统移除最后一个清理组件（`guide/cleanup-and-disable.mdx`）。框架的两个清理组件是 `Presence`（释放路由，第 7 节）和 `Persisted`（给根标 `Unsaved`，第 10 节）：

  | 销毁的原因 | 先移除 `Persisted` | 先移除 `Presence` |
  |---|---|---|
  | 停用（写完 `SaveEntities` 之后） | 是：记录已经写进缓冲，根也要销毁 | 不用：这时已经没有 Presence（第 1 步 `Leave()`，`Depart` 已经移除） |
  | 迁移（写完迁移字节之后） | 是 | 是：Presence 随字节到了新节点，路由不释放 |
  | `[OnError] Restart`、隔离、激活失败 | 是：不写记录 | 不移除：路由要释放，客户端 SDK 据此重新加入 |

  所以只有用户自己的销毁、`Restart`、隔离和激活失败留下待清理的实体，没有白做的清理。
- **对 `Destroyed` 反应的框架系统不带 `In<K>` 过滤**：退役的实体已经没有 `In<K>`，带着它就永远匹配不到。所以 `ReleaseRoutes`（`[With<Destroyed>, With<Presence>]`）和 `Persisted` 的清理系统在框架组 `Cleanup` 里，按 world 跑，不按 Actor 类型；它们需要的信息（连接戳、根的 `EntityId`）都在清理组件自己里面。
- **激活的条件：**
  - 默认任何发给它的消息都能激活（Orleans 的虚拟 actor）；
  - Actor 类型可以写 `[ActivatedBy<EnterMap>]` 收紧，其他消息遇到不存在的 Actor，返回 `NoActor`；
  - 框架的控制消息（`Answer`、`JoinConfirmed`、路由的释放等）不会激活 Actor，找不到就丢弃；
  - `Connection` 只在客户端连接时激活。

---

## 7. 客户端与 Presence

### `Connection` 实体

- **激活**：客户端连上一个承载 `Connection` 的节点后，框架用令牌里的 `ClientId` 激活 `Connection(ClientId)`。如果这个节点上已经有同一个 `ClientId` 的 `Connection`，按连接戳决定谁留下（下面）。
- **连接信息**：`(ClientId, 节点, 连接戳)`，框架内部使用。
- **连接戳是有序的**：`(令牌签发序号, 网关节点代数, 节点上的连接计数)`，按字典序比较。
  - 令牌签发序号由签发方单调递增，写进签过名的令牌：重新登录拿到的令牌更大；
  - 同一个令牌重连时，后两项区分连接。
  - 比较结果只有三种：**更小**的回复 `Superseded`，那个 `Connection` 断开；**相等**的是同一个连接在重试，幂等地再回复一次；**更大**的接管。
- **身份**：令牌里的声明解码成用户写的 `[Claims]` 类型，例如 `Account`。框架只负责解码令牌、把它作为组件加到 `Connection` 上，并随它转发的每条客户端消息带上（第 5 节的身份参数）。框架不认识任何业务字段。
- **位置固定**：`Connection` 固定在连接所在的节点，不参与放置和重新平衡，也不迁移；连接断开，它就停用。
- **扩展**：用 `[RootPrefab<Connection>]` 的 prefab 给它加组件，例如令牌桶；写 `[Handles<Connection>]` 类。
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
  - `NewestWins`：连接戳更大的接管，旧连接的 Presence 被换掉，旧的 `Connection` 断开；
  - `Reject`：已有的连接留下，新的收到 `AlreadyConnected` 后断开（半开的旧连接要等它超时）。
- 框架做完整件事，用户不写代码：
  1. `Connection` 激活后，带着连接戳向 `Player(Id)` 发框架内部的加入请求；
  2. Player 一侧按连接戳和 `Takeover` 建或者接管这个客户端的 Presence；
  3. 失败（超时、`NoOwner`、`Busy`）时按带随机抖动的退避重试，超过次数就断开客户端。一个游戏节点宕机时，十万个客户端不会同时重试；
  4. 这样加入的 Presence 丢失（Player 重启）时，框架重新加入：它没有客户端消息可以重发。
- 加入好之前，客户端消息在 `Connection` 上排队；加入好后框架推一条 `Ready` 给客户端。
- Player 上的 Presence 和别的 Presence 一样：不带 `[To]`、由 Player 处理的客户端消息，主体就是它。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Connection` 实体。
2. 先过过滤：`[Handles<Connection>]` 类里的 `[On<Incoming>] static Verdict Allow(in Incoming m, ref RateLimit r)`。它逐条执行，返回放行或者拒绝的原因；没写就全部放行。
3. 按生成的路由表，得到处理 Actor 类型。
4. 得到目标：
   - 带 `[To]`：目标是这个 Actor 类型、键是这个字段的 Actor，没有就激活；
   - 不带 `[To]`：路由表里这个 Actor 类型的那一项；没有就拒绝，`NotJoined`。
5. 直接转发给目标，信封带着客户端上下文；不带 `[To]` 的还带着 `PresenceId`，目标据此找到 Presence 实体，作为主体。Presence 带着 `Leaving`，或者连接戳对不上，回 `NotJoined`。
6. 被拒绝时，客户端收到原因：`NotJoined`、`Busy`、`NoActor`、`Filtered`、`Superseded` 等。

### 加入：`e.Join()`

```csharp
[On<EnterMap>]                                                             // [To] Map: the subject is the map's root
static bool Enter(in EnterMap m, in Account who, Entity root, in Map map, ref NetIds ids, World world)
{
    var key = new Avatar(map, new Player(who.Id));
    if (!world.AvatarIndex.TryFind(key, out var avatar))                  // idempotent: a re-sent join finds it
    {
        var id = new NetId(map, ++ids.Next);                               // the number clients see, unique on this map
        avatar = root.Spawn(Prefabs.AvatarPrefab, key, id);                      // one Set: ChildOf, Avatar, NetId; immediate (EasyEcs D2)
    }
    avatar.Join();                                                         // this client's presence here, at once; the route follows
    return true;
}
```

**本地当场生效。** EasyEcs 的结构变更当场生效（EasyEcs `decisions.mdx` D2），所以 `root.Spawn(…)` 要么返回带好组件的实体，要么抛出。`Join()` 也是当场的：
1. `e` 必须是根的直接子实体，带 `In<K>`（`root.Spawn` 建的就是），否则抛出；
2. `e` 已经是 Presence 时，按连接戳：
   - 同一个连接：返回原来的 `PresenceId`（幂等）；
   - 当前连接的戳更小：抛出 `Superseded`，请求失败；
   - 更大：接管。去掉 `Joined` 和 `Leaving`，换新的 `PresenceId`；旧连接的路由按旧的 `(PresenceId, 连接戳)` 释放；
3. 加上 `Presence(新的 PresenceId, 连接信息)` 和确认期限 `Due`，把路由追加到 `Outbox`，返回 `PresenceId`。处理方法随后可以直接返回 `true`。
- 客户端来自 `Current` 里的客户端上下文；没有上下文时，`Join()` 抛出 `NoClient`，请求失败。

**在网关登记路由是异步的：**
1. `Outbox` 里路由排在这条请求的回复前面。两者同一来源、同一目标，所以 `Connection` 一定先写路由，再把回复交给客户端。
2. `Connection` 写路由时，如果已经有同一个 Actor 类型、别的目标的路由，先删掉它，并把 `(旧 PresenceId, 连接戳)` 发给旧目标：对得上的话，旧目标对那个 Presence 执行 `Leave()`（**每个 Actor 类型一个 Presence**）。槽号不变。
3. `Connection` 写好路由后：
   - 先向目标发 `JoinConfirmed(PresenceId)`，之后才转发消息。两者同一来源、同一目标，所以确认一定先到。确认如果丢了，这个 Presence 上第一条转发来的消息也算确认；
   - 给客户端 SDK 发 `JoinConfirmed(槽号, 调用 id)`，排在回复前面：SDK 据此记下"这个槽是哪条消息加入的"。
4. 目标收到确认后：给 Presence 实体加 `Joined`，去掉确认期限。
   - 推送和 AOI 只认带 `Joined` 的 Presence；
   - 有 `[TickRate]` 的 Actor 类型，先给客户端推一条框架通知，带帧长、当前 tick 号和服务器时间（第 9 节），之后推送才开始；
   - 完整快照是用户的逻辑，例如 `[ForEach, With<Joined>, Without<Synced>]` 推一份，再加上用户的 tag `Synced`（第 12 节）。框架不知道"快照"。
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
- **外部触发的离开**（`Connection` 断线、被踢、被顶号、加入了同一 Actor 类型的另一个 Actor，或者节点离开视图）由框架作为邮件送到目标，在目标的处理过程中 `Leave()`。邮件带着 `(PresenceId, 连接戳)`，对不上就丢弃。
- **同一个键不会有两个 Presence 实体**：同一个 Player 再进同一张图时，`Enter` 用 `Join()` 接管已有的 Avatar。

**没经过 `Leave()` 就销毁的 Presence**（父实体级联销毁、`Restart`、隔离、用户 `Destroy()`）：`Presence` 是清理组件，实体留着 `Presence` 和 EasyEcs 的 `Destroyed` tag。框架组 `Cleanup` 的 `ReleaseRoutes`（`[With<Destroyed>, With<Presence>]`，不带 `In<K>`，第 6 节）把 `Lost(PresenceId, 连接戳)` 追加到 `Outbox`，再移除 `Presence`，实体随之释放。`Connection` 对得上就删掉路由，投递 `PresenceLost`（下面）。
- **迁移不是丢失**：迁移的字节里已经带着 Presence，框架销毁源节点的子树前先移除它们的 `Presence`，不释放路由（第 6 节的表）。

**通知丢失时的兜底：**
- 两个节点之间的链路重连时，目标一侧按网关列出自己持有的 Presence，网关指出其中哪些已经不认识，目标对这些执行 `Leave()`；
- 某个节点离开成员视图时，各节点对记在该节点上的 Presence 执行 `Leave()`；
- 网络抖动、GC 停顿不会让 Presence 失效，只有节点离开视图才会。
- Presence 相关的控制消息走控制通道，不受 `Busy` 限制，但有界（第 11 节）。

### Presence 丢失：客户端 SDK 重新加入

目标重启了（从记录恢复、`[OnError] Restart`、隔离），旧的 Presence 实体已经没有了：
1. `Connection` 在三种情况下删掉这条路由：收到 `Lost`；转发时收到 `NotJoined`；看到目标所在的节点离开视图。即使客户端一直不发消息，也能发现。
2. 投递 `PresenceLost`：
   - 给客户端 SDK：`PresenceLost(槽号)`；
   - 给 `[Handles<Connection>]` 的 `[On<PresenceLost>]`，写了才投递。
3. **SDK 重新发这个槽的加入消息**：SDK 为每个槽记着最后一条加入它的消息（`JoinConfirmed(槽号, 调用 id)` 指出是哪一条）。收到 `PresenceLost(槽号)` 时，按带随机抖动的退避重发它，带着当前的身份；目标因此重新激活，加入消息的处理方法再调用一次 `Join()`。
   - 同一个槽后来加入的消息替换先前的；`PresenceClosed(槽号)` 清掉它；
   - 重连之后，`Ready` 一到，SDK 重发所有槽的加入消息：新连接的戳更大，目标上旧连接留下的 Presence 被接管（上面的 `Join()` 第 2 步）；
   - 重发的回复由 SDK 自己收下，应用只看到 SDK 的事件。
- **限制**：
  - 只重发那一条加入消息，不重发之后的消息；之后的状态以目标的记录为准；
  - 加入消息的处理方法必须幂等：同一个客户端再来一次，结果一样（第 12 节的 `Enter` 先找已有的 Avatar）；
  - 连上就加入（`[JoinOnConnect]`）的 Presence 不靠 SDK，由框架重新加入。

---

## 8. 等待别的 Actor 的结果

```csharp
// Game.Server
[NinoType, From<Map>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;
[NinoType] public record struct Looting(EntityId Drop, ReplyTo<bool> Reply) : IComponent;   // the Ask state
public record struct Reserved : IComponent;                                                 // a drop promised to a pending ask
public record struct Loot(int Item) : IComponent;                                           // what a drop on the map holds

// Game.Server, in a [Handles<Map>] class
[On<PickUp>]                                                               // subject: the client's avatar
static void PickUp(in PickUp m, in Account who, in ReplyTo<bool> reply, Entity me, World world)
{
    if (!Drops.TryFind(world, me.Root, m.Drop, out var drop) || drop.IsReserved) { me.Reply(in reply, false); return; }
    var item = drop.Loot.Item;
    drop.SetReserved();                                                    // reserve: nobody else gets it while we ask
    me.Ask(new Player(who.Id), new GrantItem(item), new Looting(drop.Id, reply));
}

[On<Answer<GrantItem, Looting>>]                                           // subject: the avatar that asked
static void Granted(in Answer<GrantItem, Looting> a, Entity me, World world)
{
    bool ok = a.Ok && a.Value;
    if (world.TryGetEntity(a.State.Drop, out var drop))
    {
        if (ok) drop.Destroy();                                            // commit
        else if (a.Status != Status.TimedOut) drop.RemoveReserved();       // refused: release
    }                                                                      // timed out: it may have run, so the drop stays reserved
    me.Reply(in a.State.Reply, ok);
}
```

- **`e.Ask(to, in message[, state])`** 创建一个等待实体，它是 `e` 的子实体，带：
  - `state`：一个普通组件，存在等待实体上，回信时作为 `a.State` 交回；不写时回信是 `Answer<TReq>`，没有 `State`；
  - 调用 id：全集群唯一，`a.Call`；
  - 期限：`Due`（第 9 节），在线上是剩余时间；
  - 客户端上下文：处理客户端消息、`PresenceLost` 时自动带上（第 4 节）。
  - 生成器为有状态和无状态各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(Actor 类型, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer<TRequest>` 或 `Answer<TRequest, TState>` 邮件**，按（请求类型, 状态类型）分发；主体是发问的实体 `e`。
  - `a.Ok`、`a.Status`、`a.Value`（回复的值，类型是请求声明的回复类型）、`a.Call`，有状态时还有 `a.State`；
  - **等待实体在它的 `Answer` 处理方法返回后立即销毁。**
  - 发问的实体先被销毁了（例如 Avatar 离开），等待实体随它一起销毁，回信到达时丢弃。要在这种情况下也收尾，从根发问。
- **超时**：`Due` 到了，框架投递一个 `Ok = false`、`Status = TimedOut` 的 `Answer`，和别的项一样按 `(时间, 序号)` 执行。
- **超时不等于没执行**：请求可能已经执行，只是回复没赶上。所以：
  - 改别人状态的请求用"先预留、再提交"：发问前把资源标成预留（上例的 `Reserved`），成功时提交，明确拒绝时释放，超时时保持预留，不交给第二个人；
  - **调用 id 是幂等键**：要重试，用同一个调用 id 再问一次（`me.Ask(to, req, state, call: a.Call)`），接收方从 `reply.Call` 读到它，把处理过的 id 存进组件去重。框架不替接收方去重。
- **问多个目标**：对每个目标各 `Ask` 一次，每个目标一个 `Answer`。要汇总时，把计数放进发问实体上的组件。
- **`ReplyTo<T>` 是普通数据**：
  - 请求的处理方法取 `in ReplyTo<T> reply` 参数；`T` 就是请求声明的回复类型，`Reply` 在编译期检查类型；
  - 要先问别人再回复：把令牌放进 `Ask` 的状态（上例的 `Looting.Reply`），回信时 `me.Reply(in a.State.Reply, …)`；
  - 要晚些回复、不问别人：把令牌存进任何组件，之后 `Reply`；
  - `default` 的令牌，`Reply` 什么也不做；
  - 同一个令牌只有第一次回复有效；一直没有回复，调用方按期限超时。
- 请求的处理方法必须返回回复，或者取 `ReplyTo<T>`（分析器检查）。
- 状态类型带 `[NinoType]`：等待实体随 Actor 迁移。
- 回信走控制通道，不受 `Busy` 限制：它的数量不超过本节点发出的、还在等的请求数（第 11 节）。

---

## 9. 时间与广播

### 时间

**一个时间类型：`TimeSpan`，本进程单调时钟上的时间，下文叫单调时间。**
- 单调时间不是 UTC，不依赖各节点对时。它只在一个进程里有意义。
- **跨进程只传剩余时间**：请求的期限在线上是"还剩多久"；迁移的字节里，延后事件和期限也存剩余时间。接收方用自己的单调时钟重新起算：到期时刻 = 收到时的 `Time.Elapsed` + 剩余时间。迁移途中花的时间不算进去（通常几毫秒）。
- **绝对的 UTC 只出现在用户的持久化数据里**：要跨停用保留的时刻（例如"buff 在某日到期"），用户把 UTC 写进 `IPersisted` 组件，激活时自己换算。
- `Time` 是 world 的单例，由运行时写，用户只读：

  ```csharp
  public record struct Time(long Tick, TimeSpan DeltaTime, int TickRate, TimeSpan Elapsed) : ISingletonComponent;
  ```

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
// DaServer: an ordered value index; Key => this, compared by (At, Seq); At is on this process's monotonic clock
public partial record struct Due(TimeSpan At, long Seq) : IComponent, IIndexed<Due>, IComparable<Due>
{
    public static readonly Due Min = new(TimeSpan.MinValue, long.MinValue);
    public int CompareTo(Due o) => At != o.At ? At.CompareTo(o.At) : Seq.CompareTo(o.Seq);
}
```

| 谁带 `Due` | 是什么 |
|---|---|
| 延后的事件 | 发起实体的子实体：`ChildOf(发起者)`、`Due`、事件本身 |
| 请求的期限 | 等待实体 |
| 加入确认期限 | 确认前的 Presence 实体 |
| 连上就加入的退避 | `Connection` 的一个子实体 |

- `OrderedPhase` 用 EasyEcs 的有序值索引 `world.DueIndex`，它已经按 `(时间, 序号)` 排好，再和邮箱快照按同一个顺序归并。序号取自单例 `Ordered`。
- **不边遍历 `Range` 边加项**：有序索引的遍历允许销毁实体，不允许改动 `Due`（`reference/valueindex-ordered.mdx`），而执行一项可能 `Schedule` 出新的 `Due`。所以每一步都重新查：取 `Range(Due.Min, new Due(t, long.MaxValue))` 的第一项（最小的、不晚于 t 的），和邮箱快照的队首比较，执行较早的那个，然后从头再查。每次查找 O(log 项数)；执行中新加的、时间不晚于 t 的项，下一次查找就按顺序取到。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- 周期性的逻辑写进系统；稀疏的周期（例如每秒跳一次伤害）在事件处理方法里再 `Schedule(…, after:)` 下一次。
- **每个 Actor 待执行的延后事件有上限**（默认 65,536，Actor 类型上可以写 `[MaxScheduled(n)]`）。超过时 `Schedule` 抛出，按 `[OnError]` 处理：一个失控的循环不会吃光内存。

**`Scheduled<T>` 是普通数据**：`record struct Scheduled<T>(EntityId Id) where T : struct, IEvent`，指向那个子实体。
- 还在等吗：`e.IsPending(s)`；取消：`e.Cancel(s)`（`e` 是发起者）。执行或取消后 `IsPending` 就是 `false`。
- 存进组件的 `Scheduled<T>` 不需要清理，也不会卡住：`EntityId` 带版本，执行或取消后永远不再指向活着的实体。
- 多数时候不用存句柄：`e.IsPending<T>()` 问 `e` 上有没有没执行的 T（查 `e` 的子实体），`e.Cancel<T>()` 取消它们。"正在施法"就是 `me.IsPending<Strike>()`，不另设组件（第 12 节）。

### 广播

- **`ClientGroup`**：一个值（`ClientGroup(long Key)`），按键区分。
- **订阅是数据**：Presence 上的 `DynamicBuffer<Subscribed>`，`Subscribed(ClientGroup Group)`。
  - `e.SetGroups(groups)` 就是改写这个缓冲；
  - 收尾组的 `SyncSubscriptions`（`[Changed<DynamicBuffer<Subscribed>>]`，以及刚加上 `Joined`、刚被接管的 Presence）把差异发给网关的扇出表。只有带 `Joined` 的 Presence 是成员。迁来的 Presence 也由它订阅（迁移到达算作写入，第 10 节）；
  - `e.Push(in group, msg)` 一次序列化，每个有成员的网关节点收到一帧，由网关扇出。
- **整个 Actor**：`root.Broadcast(msg)`，推给它所有带 `Joined` 的 Presence。
- **小组**（公会、队伍）：一个固定键的 Actor，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热重启、升级（EasyEcs 4.6）

组件类型的集合都用 EasyEcs 的 `ComponentSet`（`reference/componentset.mdx`）：注册时建好一次，存在 `Registration` 里，框架按 `Type` 加入它在注册时才知道的类型（`ComponentSet.With(Type)`）。

### 记录

一份记录就是一次 `SaveEntities`，包括：
- 根实体，放在第一个；
- 带至少一个 `IPersisted` 组件的子孙；
- 这些子孙到根的路径上的实体。

收集用 `world.Descendants(root)`（深度优先，预热后不分配）。保存用的组件集合：`persist = ComponentSet.Create().With(typeof(每个 IPersisted 组件)).With<ChildOf>().With<In<K>>().With<Persisted>()`。

### 激活 epoch：一个 Actor 只有一个激活能写

- **激活先占 epoch**：读记录之前，对 `(键, epoch)` 做一次条件写：存储里的 epoch 还是读到的那个，就写成 +1，即使这个键还没有记录（只写一行占位）。占不到就按退避重来。
- **每次写记录都按 epoch 比较并交换**：存储里的 epoch 不是自己的，写失败。
- **比较失败就隔离**（第 6 节的表）：不写、不进 `Leaving`，销毁整棵子树。Presence 的路由由清理组件释放，客户端 SDK 重新加入，到达真正的主人那里。
- **节点被移出成员视图时**，它在重新加入之前先隔离自己所有的 Actor，再以新的节点代数加入（第 2 节）。分区或长停顿之后醒来的节点，不会再写。
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
- `ChangeSystem` 同时看 `[Changed<T>]` 和 `[Removed<T>]`：持久化组件被写或者被删，都给根实体标 `Unsaved`。
- `CheckpointSystem` 按配置的检查点间隔，把带 `Unsaved` 的根 `SaveEntities` 写一次，按 epoch 比较并交换；写成后去掉 `Unsaved`。
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
| `Ask` 的状态类型、本地事件 | `[NinoType]` |
| 其他非托管组件 | 迁移时按原始字节搬运，不用标 |
| 托管组件 | 要有 Nino 格式化器（分析器检查） |

记录里的类型按 `TypeId` 存。

### 迁移

- 保存整棵子树，组件集合是注册时建好的 `migrate = ComponentSet.Create().With(typeof(每个不带 [Transient] 的组件))`：默认都带，标了 `[Transient]` 的除外。
- 等待中的请求、Presence 实体（连同 `Subscribed`）、待执行的延后事件随 Actor 一起迁移。
  - `Due` 在字节里是剩余时间，到达后按新 world 的单调时钟重新起算，再按它的 tick 边界归入 tick 窗口；已经过期的在第一个 tick 执行（第 9 节）。
- **迁移头里带着每个组件的布局哈希**（来自 `wire.manifest`）。目标节点的布局有一个对不上（滚动升级时新旧版本混跑），就不搬字节：源节点改为停用、写记录，目标节点从版本兼容的记录加载。这时只有 `IPersisted` 的数据留下；Presence 丢失，由 SDK 重新加入。
- 源节点销毁子树前先移除 `Presence` 和 `Persisted`：路由不释放，根也不标 `Unsaved`（第 6 节的表）。
- **墓碑**：源节点在迁移后保留一段时间的 `(键 → 新节点)`，把晚到的控制消息（例如 `JoinConfirmed`、回信）转过去。
- 停用写记录时不保存待执行的延后事件：需要跨停用保留的状态，写进 `IPersisted` 组件。
- 到达后的第一条邮件是 `Activated(Activation.Migrated)`。
- 跨 world 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。
- 重新平衡引起的迁移限速：每个节点同时迁出的 Actor 有上限，节点增减时不会一下子搬空。

### Native AOT

节点可以用 `PublishAot=true` 发布成原生程序（ADR-083），也可以照常 JIT 运行。两者行为相同：都不在运行时加载程序集，改代码都走热重启或滚动升级（下面）。

- **生成的注册是封闭类型，不用反射**：`Registrations.Create()` 在用户的程序集里写出每个封闭的类型（`.Actor<Map>()`、分发表、job、`world.AddSystem<…>()`），框架按 Actor 类型的系统也由生成的代码写出封闭的实例。没有反射、`MakeGenericType`、`Activator`、程序集扫描，也没有模块初始化器。
- 框架的泛型系统不写 `[ForEach]` 方法，在 `OnUpdate` 里用 EasyEcs 的泛型入口跑 job，所以 EasyEcs 不需要在运行时用 `MakeGenericType` 封闭它们的代码（EasyEcs D20：只由别的程序集封闭的泛型系统才走这条路，AOT 下不行）。用户的泛型 `[ForEach]` 系统只在别的程序集里封闭时，DaServer 的分析器报错（第 13 节）：EasyEcs 对这条路不报警告，错了只会在运行时失败。
- 序列化都是 Nino 生成的格式化器；HTTP 平面用 `CreateSlimBuilder` 和源生成的 JSON。
- 发布要求 0 警告，不接受压掉任何警告。EasyEcs 4.6 带追踪组件（`IPersisted`）和禁用组件的程序发布时没有警告（EasyEcs 的 CI 以警告为错误发布并运行原生程序，`faq.mdx` 的 "Native AOT"）；R 里要验证的只剩第三方包（architecture.md 第 8 节）。

### `wire.manifest`

- 每个工程写出自己的一段。
- Server 的一段：Actor 类型、承载它的服务器类型、帧率、身份、连上就加入的 Actor 类型、每条消息的接收方、发送方、回复类型、事件、状态类型、持久化组件、每个组件的布局哈希、推送；客户端消息的路由（带 `[To]` 的写明字段）、事件和 `Answer` 的处理、生命周期消息的处理、对 `Leaving` 的消费。
- 热重启按它比对新旧版本，选走哪条路（下面）。
- AppHost 只读 `actor` 和 `hosted-by`。

### 滚动升级

- 旧版本节点收到不认识的消息，返回 `NotHandled`。调用方在重新解析放置之后可以重试一次：放置可能已经换到新版本的节点。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点代数由集群分配（第 2 节），所以同名的节点重启后，不会被当成原来那个。
- 新旧版本的帧率可以不同：延后事件跨进程时存的是剩余时间。
- 组件布局不同的节点之间，迁移退回到停用加记录（上面的迁移）。

### 热重启

改代码就换二进制：节点在原地换成新版本的进程，Actor 不重新激活，客户端不断线。JIT 和 Native AOT 节点做法相同。

- **请求**：运维命令 `{cluster}-restart`（例如 `aspire do mmo-restart`，背后是节点的管理端点，第 2 节）带上新版本的 `wire.manifest`。节点比对新旧两份，选一条路，并在命令的结果里报告走了哪条：

  | 比对结果 | 路径 | 停顿 |
  |---|---|---|
  | 每个组件的布局哈希相同；旧版本有的事件、`Answer` 组合、生命周期消息的处理方法和对 `Leaving` 的消费，新版本都还有；客户端路由不变 | **原地**：交接 world | 预热之后几十毫秒；平台重启时几百毫秒 |
  | 布局变了，但只变在新版本读得了记录的组件上（`VersionTolerant`、`Keyed` 的 `IPersisted`，或者不持久化的组件），或者去掉了上一行的处理方法 | **经记录**：所有 Actor 停用、写记录，重启，按需从记录激活 | 更长；不持久化的状态按停用的规则丢掉，Presence 由 SDK 重新加入 |
  | 承载关系（`[Hosts]`）或客户端路由变了 | 拒绝；走**滚动升级**（上面），路由变了要连网关一起升级 | — |

  - 去掉的处理方法可能正有数据在等：待执行的事件、等待实体、正在离开的 Presence，所以原地交接不接受它。
  - 一个节点组逐个节点热重启；期间新旧版本混跑，规则同滚动升级。
- **交接的字节**（下面两种传输方式完全相同）：
  - 头：节点名、节点代数、化身号、交接令牌、旧版本 `wire.manifest` 的哈希、校验和；
  - 每个 world：`world.Save` 的字节（实体连同 id 和版本、组件、`DynamicBuffer`（包括占位上等着的 `Mail`）、单例；不含系统、索引、写入戳，EasyEcs `guide/saves.mdx`）；调度队列里还没进 world 的邮件；这个 world 的 `Time.Elapsed`；
  - 节点的运行时状态：调用 id 的序号、墓碑、连接戳的计数；每条链路对方发来的流（流 id、处理到的字节数）和发出去的流（流 id、偏移、还没确认的尾巴）。
  - 记录写入在交接之前都有了结果：写成的去掉了 `Unsaved`，没写成的还带着。`Unsaved` 是根上的 tag，随 `world.Save` 一起过去，所以等着写的记录不会丢，新进程的下一个检查点接着写。`Outbox` 每项执行完就发出，两个 tick 之间是空的。
- **时间**：新进程的 world 时钟从保存时的 `Time.Elapsed` 接着走，所以每个 `Due` 不用改写，剩下的时间不变，停顿不计入（同迁移的剩余时间）。
- **步骤：先预热，再交接**（节点自己起继任进程时的标准顺序）：
  1. 新进程先启动，做完 JIT 预热或 AOT 初始化，建好空的 world、按 `Registration` 加上系统，并提前和其他节点握手，建好备用链路（见下面的"备用链路"）；它不绑定客户端端口；
  2. 旧节点在成员视图里宣布 `Restarting`，带一个期限和交接令牌的哈希；
  3. 这时旧进程的每个 world 才在两个 tick 之间停下：不再从链路取帧，等在途的记录写入有结果，然后 `world.Save`，连同运行时状态流给新进程；
  4. 新进程 `world.Load(bytes, markWritten: false)` 进已经建好的 world（见下面的"写入戳"），接上时钟；
  5. 新进程以一次成员更新接管：同一个节点名和代数、化身号加一（成员视图里已有的化身号）、带着交接令牌；它的成员信息比 `Restarting` 新，其他节点关掉旧进程的链路，改用备用链路，重放各自的重发窗口（处理到的字节数在交接里，所以不重复也不丢）；
  6. 旧进程退出。
  - 平台重启进程时做不到预热：旧进程先退出，新进程启动后才做第 1、4、5 步。
- **所有权怎么交过去**：
  - 不重新占 epoch，不隔离：每个 Actor 的 epoch 在它的根上，随 world 过去；存储里的 epoch 没变，新进程写记录照样比较成功。
  - 安全靠"任一时刻只有一个进程跑 world"：旧进程在 `Save` 之前停下所有 world，之后不再跑，除非新进程在**提交点**之前报告失败；新进程过了提交点才跑 world。提交点是新进程加载完、回报"已加载"（本机管道），或者新进程找到合格的快照（平台重启，那时旧进程已经退出）。
  - `Restarting` 期间其他节点不把它当作失效：不判 `Suspect`、`Dead`，放置不变，不迁移，不重新激活。期限按各自的单调时钟、从第一次看到时算起，不依赖时钟一致。
  - 发给它的消息留在链路的有界重发窗口里（网关上的客户端消息也是），满了回 `Busy`，发送方按已有的退避重试；请求的期限照常走，可能超时（第 11 节）。
  - 交接令牌只有收到字节的进程知道，所以别的进程冒充不了继任者。
  - 期限过了还没接管：按节点离开视图处理（第 11 节），Actor 在别处占新的 epoch，从记录激活。迟到的新进程不被接受，丢弃快照，以新的代数加入；它没跑过 world，不会写。新进程已经在跑、却被别的节点判了 `Dead` 时，epoch 比较会拦住它的写，它按上面激活 epoch 的规则隔离自己，再以新的代数加入。
- **备用链路**（ADR-084）：
  - 现在的链路按对方地址一条、只有一个入站位置；Hello 里是进程启动时随机的节点 id，`Resume` 回来的 id 变了就当作对方重启：丢掉流，在途的调用失败。同一个地址上来的第二个 Hello 会抢走旧进程的入站位置。所以热重启要改链路：
  - 链路上的身份是（节点名，代数），化身号单独带上；继任进程是同一个身份，续上旧进程的流，不算重启；
  - `LinkHello`（版本容忍，可以追加字段）加上化身号、`Standby` 标记和继任进程临时监听的端点（旧进程还占着公布的端口）；
  - 其他节点照常验证这个 Hello，把连接放在现有连接旁边的备用位置：不回 `Resume`，不收发业务帧；只接受视图里已有的成员、化身号更高的；期限到了或 `Restarting` 撤回时关掉；
  - 带着这个化身号和交接令牌的成员更新到了才切换：旧进程的连接关掉，继任进程用交接里恢复的字节数回 `Resume`，两边各自重放；
  - 化身号变大时重新取成员卡片（新版本的构建号在卡片上）；
  - 平台重启时没有备用链路：新进程绑好公布的端口后以同一个身份重连，流照样续上。
- **两种传输方式**，同一套字节、同一套所有权规则：
  - **节点自己起继任进程**（物理机、虚拟机、Windows 服务、macOS、开发机）：旧进程启动新二进制，两者经本机的命名管道（`System.IO.Pipes`：Windows 上是命名管道，Unix 上是 Unix 域套接字）连上；新进程预热好之后，旧进程停 world、把字节流过去；新进程加载、回报"已加载"；旧进程关掉监听端口退出，新进程绑定同样的端口。不需要 `exec`（Windows 没有）。服务管理器按 PID 盯进程时，要允许交接（例如 systemd 由新进程 `sd_notify` 报告 `MAINPID`），或者留一个很小的启动器进程作为被监管的父进程。
  - **平台重启进程**（Kubernetes 同一个 Pod 里的容器重启、systemd 重启）：旧进程把字节写进节点本地目录里的一个文件（Kubernetes 上是 `medium: Memory` 的 emptyDir，有 tmpfs 用 tmpfs，否则普通本地目录），经 `MemoryMappedFile.CreateFromFile` 写入（跨平台；Unix 上没有具名映射），然后退出。新进程启动时找属于自己节点名、校验和正确、没过期限的快照：有就加载，不重新激活；过期或者不合格就删掉，照常加入、从记录激活。Kubernetes 上，承载 Actor 的节点组是 `updateStrategy: OnDelete` 的 StatefulSet（Pod 名就是节点名，改模板不替换 Pod）；发布（`helm upgrade`）改 StatefulSet 模板里的镜像（`OnDelete` 下不替换 Pod），`{cluster}-restart` 再逐个改运行中 Pod 的容器镜像，从不改模板：kubelet 在同一个 Pod 里重启容器（同一个 UID、同一个 IP），emptyDir 保留，不重新调度（ADR-085）。网关和无状态的节点组仍是 Deployment。
- **停顿**：先预热再交接时，停顿只有 `Save`、传输、`Load` 加上一次成员更新，目标是几十毫秒；平台重启时还要加上进程启动和重连，目标是几百毫秒。搬 world 只要几毫秒：一个 world、每个实体 5 个非托管组件，1 万个实体 `Save` 44 µs、`Load` 183 µs、440 KB，10 万个实体 `Save` 0.76 ms、`Load` 2.7 ms（合计约 3.5 ms）、4.4 MB，都不分配（BenchmarkDotNet ShortRun，EasyEcs 4.5.0，.NET 11 RC2）。R 里端到端测量：BenchmarkDotNet 测每个 world 的交接，E2E 测停顿时长、客户端不断线。
- **写入戳**：新进程用 `world.Load(bytes, markWritten: false)` 加载（EasyEcs 4.6，`guide/saves.mdx` 的 "Restart"，D39）：加载的行没有写入戳（tick 0），也不算移除过。
  - 加载之前已经跑过的系统（预热）和加载之后才加上的系统（平台重启）都不访问这些行：第一次运行看的是 tick 1 起的写入，tick 0 的行不在内（`guide/change-tracking.mdx` 的 "Ticks"）。所以 `ChangeSystem` 不会给每个根标 `Unsaved`，Actor 不会把记录重写一遍；两种顺序都不需要别的参数。
  - 真正没写的记录由带过来的 `Unsaved` 负责。
  - **用户看得到的后果**：热重启之后，所有 `[Changed<T>]` / `[Removed<T>]` 方法（包括用户的，例如 AOI、`SyncSubscriptions`、`CellAssign`）都不把加载的行当作变更，下一次运行只看之后的写入。由追踪组件算出来的组件本来就随 `world.Save` 一起过去，一般不用重算。确实要在热重启后全量走一遍的系统，写一个不带 `[Changed<T>]` 的 `[ForEach]` 方法（它访问每个匹配的行），用数据控制它只跑一次，例如一个 tag 或单例（EasyEcs 的系统不带状态，EECS007）。不要放在 `OnCreate` 里：它在 `AddSystem` 时运行，预热顺序下那时还没 `Load`，看不到加载的行。
  - `AddSystem(system, changesFromNow: true)`（EasyEcs 4.6）只给运行中途才加入的系统，例如功能开关打开时才加上的：它的第一次运行只看加入之后的写入，不把 world 里已有的状态当作变更。框架注册时加的系统都用默认值。
  - `world.Restore(snapshot)`（`Capture` / `Restore`）没有这个参数，总是把恢复的行算作写入：只用于测试，不用于热重启。
- **网关不热重启**：`Connection` 钉在连接上，换进程连接就断。网关节点改代码走滚动升级，SDK 重连、重新加入。以后可以考虑把套接字交给新进程（`SCM_RIGHTS`，Envoy 的热重启），只在 Unix 上有。
- **`[Stateless]` 的 Actor 类型**没有要交接的状态：可以单独部署成节点组，先起新节点、再停旧节点，零停顿。

### 不重启就能改的

两种改动都只是换数据，在两个 tick 之间生效：
- **配置表**（调参的表）：一个 world 的单例，节点读入、校验新版本后，每个 world 在两个 tick 之间换上；版本号写在成员信息里。
- **功能开关**：二进制里编译好的几种做法，由一个单例或 tag 选（处理方法和系统按它过滤）；开关就是改这份数据。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。Actor 在新节点占到新的 epoch，从记录激活，最多丢一个检查点间隔的改动。`Connection` 看到节点离开视图，删路由，`PresenceLost(槽号)` 给 SDK，SDK 重发加入消息（第 7 节） |
| 两个节点都以为自己有同一个 Actor（分区、长停顿之后） | 只有占到最新 epoch 的能写；另一个写记录时比较失败，被隔离：不写、不进 `Leaving`，路由由清理组件释放，SDK 重新加入到真正的主人 |
| 节点被移出视图后又回来 | 先隔离自己所有的 Actor，再以新的节点代数加入 |
| 网关宕机 | 客户端断开，带令牌重连，得到新的 `Connection` 和更大的连接戳；SDK 重发各槽的加入消息，目标按连接戳接管旧的 Presence。各目标看到那个节点离开视图，对记在它上面的 Presence `Leave()` |
| 半开连接时客户端已经连到别处 | 连接戳更大的胜出（`Takeover.NewestWins`），旧的 `Connection` 断开；旧连接再来加入，戳更小，收到 `Superseded`；同一个连接重试，戳相等，幂等再回复 |
| 连上就加入失败 | 框架带抖动退避重试，超过次数断开 |
| `Join()` 时没有客户端上下文 | 抛出 `NoClient`，请求失败 |
| `Join()` 时已有的 Presence 连接戳更大 | 抛出 `Superseded`，请求失败 |
| 加入后路由没写成（客户端已断开） | 确认期限到，`Leave()` |
| SDK 重发加入消息失败 | SDK 带抖动退避重试，超过次数放弃；应用只看到 `PresenceLost` |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活 |
| 主体缺少组件参数，或者不满足过滤 | 不调用处理方法；请求以 `Skipped` 失败，单向消息和事件跳过 |
| 请求超时 | 不等于没执行：先预留再提交，调用 id 是幂等键（第 8 节） |
| 拿着回复令牌的 Actor 在回复前停用 | 调用方按期限超时；之后到达的回信丢弃 |
| 发问的实体在回信前被销毁 | 等待实体一起销毁，回信丢弃 |
| 停用时写记录失败 | 重试；这期间到达的消息留在 `Saving` 占位上，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；晚到的控制消息由墓碑转发；目标节点宕机时，在途的单向消息可能丢失 |
| 迁移两端的组件布局不同 | 不搬字节：停用、写记录，目标从记录加载 |
| 新版本去掉了还在用的处理方法 | 热重启不走原地，改走经记录（第 10 节）；分发时仍然找不到就是 `NotHandled` |
| 热重启时新进程起不来，或者 `Load` 失败、校验和不对 | 提交点之前：旧进程撤回 `Restarting`，接着跑 world（平台重启时旧进程已经退出：新进程丢弃快照，照常从记录激活）；命令报告失败 |
| 热重启超过期限还没接管 | 按节点离开视图处理；迟到的新进程丢弃快照，以新的代数加入 |
| 滚动升级中收到 `NotHandled` | 重新解析放置后可以重试一次 |
| 邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回绝 |
| 控制通道 | 不受 `Busy` 限制，但有界：Presence 的控制消息不超过本节点的 Presence 数，回信不超过本节点还在等的请求数 |
| 一个 Actor 的延后事件超过上限 | `Schedule` 抛出，按 `[OnError]` 处理 |
| 客户端读得太慢 | 断开（ADR-035） |
| 离开通知丢失 | 链路重连时核对，或者节点离开视图时，补做 `Leave()` |
| Presence 没经过 `Leave()` 就销毁 | 清理组件释放路由，SDK 收到 `PresenceLost` 后重新加入 |
| `[On<T>]` 抛出异常 | 不回滚，见第 5 节：请求失败，即时链里还没执行的事件作废；`Resume` 照现状继续，`Restart` 从最后的记录重新激活；`Connection` 直接断开 |
| 一项的即时链超过 1,024 个 | 按 `[OnError]` 处理 |
| 系统抛出异常 | `SystemErrorPolicy.Disable`：这个系统停用，异常留在 `system.Error`，健康检查报告；下一次热重启或升级换上新的系统 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 过滤不满足就不调用；方法体里重新校验目标后作废 |

---

## 12. 完整例子：连接、进地图、移动、放技能、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct EnterMap([To] long Map) : IFromClient<bool>;         // goes straight to that map
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took; to its current map
[NinoType] public record struct CastSkill(int Skill, int Target) : IFromClient;       // Target: a NetId number from Nearby
[NinoType] public record struct PickUp(int Drop) : IFromClient<bool>;                 // Drop: a NetId number; to its current map (section 8)
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
[NinoType] public record struct NearbyEvent(int Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: the entity's NetId number
public enum NearbyKind : byte { Appear, Moved, Disappear }
```

```csharp
// ── Game.Server: identity, actor types, servers, events ───────────────────────
[Claims] public readonly partial record struct Account(long Id);                       // decoded from the token
[JoinOnConnect<Account>]                                                               // joined at connect: Account(42) -> Player(42)
public readonly partial record struct Player(long Id) : IActor<Player>;
[TickRate(30)]                                                                         // 30 ticks a second; its systems see only map entities
public readonly partial record struct Map(long Id) : IActor<Map>;

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Connection>] public struct GatewayServer : IServer;

[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;           // a cast's resolution, after the windup

// ── Game.Server: data ─────────────────────────────────────────────────────────
[NinoType] public record struct Grid(int Size, int CellSize) : IPersisted;              // on a map; IPersisted brings ITrackChanges
public record struct NetIds(int Next) : IComponent;                                     // on a map: the last number given out
public partial record struct NetId(Map Map, int Value) : IComponent, IIndexed<NetId>;        // what clients call an entity
public partial record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar>;  // Key => this is generated
public partial record struct Cell(Map Map, int X, int Y) : IComponent, IIndexed<Cell>, ITrackChanges;   // AOI cell
public record struct Visible : IComponent;
public record struct Synced : IComponent;                                               // this presence has had its snapshot
public record struct Position(Vector2 Value) : IComponent, ITrackChanges;               // tracked: CellAssign reacts to writes
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;               // tracked: Diff reacts to writes
public record struct NetSync(Velocity Sent) : IComponent;                               // the velocity last told to observers
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Dead : IComponent;
public record struct Stunned : IComponent;
public record struct Shout(Cell Cell, NearbyEvent Event);                               // news for one cell, kept on the map

public static class Prefabs
{
    [RootPrefab<Map>] public static readonly Prefab MapPrefab = new Prefab().SetGrid(512, 16).SetNetIds(default).SetBuffer<Shout>();
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
    static bool Enter(in EnterMap m, in Account who, Entity root, in Map map, ref NetIds ids, World world)
    {
        var key = new Avatar(map, new Player(who.Id));
        if (!world.AvatarIndex.TryFind(key, out var avatar))              // idempotent: a re-sent join finds it
        {
            avatar = root.Spawn(Prefabs.AvatarPrefab, key, new NetId(map, ++ids.Next));   // one Set, one row move; immediate (EasyEcs D2)
        }
        avatar.Join();                                                     // this client's presence; a newer stamp takes over
        return true;                                                       // the client's previous map, if any, leaves it
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
        p.Value = m.At;
        v.Value = m.Velocity;
    }
}

// ── Game.Server: combat ───────────────────────────────────────────────────────
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>, Without<Dead>, Without<Stunned>]                       // subject: the caster's avatar
    static void Cast(in CastSkill m, Entity me, World world)
    {
        if (me.IsPending<Strike>()) return;                               // one cast at a time
        if (!world.NetIdIndex.TryFind(new NetId(me.Root.Map, m.Target), out var target)) return;   // the number -> the entity on this map
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

// ── Game.Server: every Map tick, after the ordered phase ─────────────────────
[UpdateInGroup<Map>]
public class Steering : EcsSystem
{
    [ForEach(Parallel = true), With<Steered>]                              // path finding is heavy: opt in to threads
    static void Steer(in Position p, in Destination d, in Speed s, ref Velocity v) =>
        v.Value = Paths.Toward(p.Value, d.Target, s.Value);
}

[UpdateInGroup<Map>, UpdateAfter<Steering>]
public class Movement : EcsSystem
{
    [ForEach, With<Steered>]                                               // a batch method: runs of rows as spans; Vectors is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Time time) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities),
            (float)time.DeltaTime.TotalSeconds);
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
    static void Crossed(Entity avatar, in Cell cell) => avatar.SetGroups(Aoi.Around(in cell));   // data: the framework syncs the gateway
}

[UpdateInGroup<Map>, UpdateAfter<Regroup>]
public class Snapshot : EcsSystem
{
    [ForEach, With<Joined>, Without<Synced>]                               // confirmed, not yet in sync
    static void Push(Entity avatar, in Cell cell, World world)
    {
        avatar.Push(Aoi.Snapshot(world, in cell));                         // a pooled Nearby of the 3×3 cells, from world.CellIndex
        avatar.SetSynced();
    }

    [ForEach, With<Synced>, Without<Joined>]                               // taken over: Joined comes back on confirmation
    static void Unsync(Entity avatar) => avatar.RemoveSynced();
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
        Aoi.Publish(map, shouts.AsReadOnlySpan());                         // one Nearby per cell with news: map.Push(Aoi.Group(cell), nearby)
        shouts.Clear();
    }
}
```

被打断的前摇：眩晕的处理方法里 `me.Cancel<Strike>()`。

这个例子没有 Player 的逻辑：客户端连上时由 `[JoinOnConnect<Account>]` 加入 Player，进地图的 `EnterMap` 直接发给地图，不经过 Player。

AOI 的状态全在组件里：客户端看到的编号是 `NetId`，格子是 `Cell`，上次发出的速度是 `NetSync`，已经推过快照是 `Synced`，订阅是 `Subscribed`，消息是地图上的 `DynamicBuffer<Shout>`。`Aoi` 是用户写的无状态帮助类。每个系统只做一件事：
1. `CellAssign`：位置被写过的实体，算出格子；跨格才写 `Cell`。
2. `Regroup`：`Cell` 变了的 Presence，改写它订阅的 3×3 `ClientGroup`。框架的 `SyncSubscriptions` 按这份数据同步网关（第 9 节），所以用户只写一处。
3. `Snapshot`：带 `Joined`、没有 `Synced` 的 Avatar，推一份完整快照，加上 `Synced`；被接管而暂时没有 `Joined` 的，去掉 `Synced`，确认后重新推。
4. `Diff`：速度被写过、并且和 `NetSync` 不同的实体，写一条 `Moved` 进地图的 `Shout`。
5. `Vanish`：带 `Leaving` 的 Avatar，写一条 `Disappear`。
6. `Flush`：`Shout` 被写过的地图（`[Changed<DynamicBuffer<Shout>>]`，没消息的地图不访问），按格子合并，每个有内容的格子发一帧 `Nearby`，然后清空。

所有系统都只看带 `In<Map>` 的实体（`IActor<Map>` 带着的组过滤，第 5 节）。除了 `Steering` 都是单线程：`Movement` 是批量方法（SIMD 已经够快：EasyEcs 评估过批量方法的并行，和单线程批量、`ForEachParallel` 打平，放弃了），其余的发消息、写别的实体或做结构变更。

NPC 也由用户建：`root.Spawn(Prefabs.NpcPrefab, new NetId(map, ++ids.Next))`（`map` 是根上的 `Map` 组件），同样从 `NetIds` 取一个编号。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: Key => this for every partial IIndexed<T> record struct whose key is itself
partial record struct Map    { Map IIndexed<Map>.Key => this; }   // explicit: a public Key would recurse in ToString()
partial record struct Avatar { Avatar IIndexed<Avatar>.Key => this; }   // explicit: a public Key would recurse in ToString()
partial record struct Cell   { Cell IIndexed<Cell>.Key => this; }   // explicit: a public Key would recurse in ToString()
partial record struct NetId  { NetId IIndexed<NetId>.Key => this; }   // explicit: a public Key would recurse in ToString()

// [Claims]: the claims are a component of Connection
partial record struct Account : IComponent;
```

```csharp
// <auto-generated/> Game.Server: typed sends on Entity, one per message and shape (§8's GrantItem; this example has none)
public static class GrantItemSends
{
    public static void Ask(this Entity from, Player to, in GrantItem message, CallId call = default) =>   // answer: Answer<GrantItem>
        Runtime.Ask(from, in to, in message, call);

    public static void Ask<TState>(this Entity from, Player to, in GrantItem message, in TState state, CallId call = default)
        where TState : struct, IComponent =>                               // answer: Answer<GrantItem, TState>
        Runtime.Ask(from, in to, in message, in state, call);              // appended to Outbox; the client's context comes from Current
}
// The receiver is in the signature; the sender is checked where it is called: inside [Handles<K>] or [UpdateInGroup<K>],
// K must be in [From<…>] (here Map), else the analyzer reports it; elsewhere the runtime asserts the root's actor type.
// Scheduled<T> Entity.Schedule<T>(in T, TimeSpan after = default) where T : struct, IEvent is the framework's own generic method.
```

```csharp
// <auto-generated/> Game.Server: each [On<T>] method as an EasyEcs per-entity job, inside its partial class
partial class Maps
{
    internal struct Enter_Job : IForEachEntity<Map, NetIds>
    {
        public EnterMap Message;                                           // filled from the mail
        public Account Who;                                                // the sending client's claims, not the subject's
        public World World;
        public bool Reply;
        public static int Writes => 0b10;                                  // NetIds only (Map is indexed: in)
        public void Execute(Entity e, ref Map map, ref NetIds ids) => Reply = Enter(in Message, in Who, e, in map, ref ids, World);
    }                                                                      // takes World and spawns: no INoStructuralChanges

    internal static void On_Enter(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Enter_Job { Message = mail.Read<EnterMap>(), Who = mail.Claims<Account>(), World = subject.World };
        if (!subject.Run<Map, NetIds, Enter_Job>(ref job)) { outcome.Skipped(); return; }   // no filter on the method: none passed
        outcome.Reply(job.Reply);                                          // Join() put the Route in Outbox ahead of this reply
    }
}

partial class Moves
{
    internal struct Move_Job : IForEachEntity<Position, Velocity, Speed>, INoStructuralChanges   // no World; Entity only pushed to
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
// Combat.Cast_Job, Combat.Resolve_Job: the same shape; their filter is Without<Dead>().Without<Stunned>().
// Cast schedules (a structural change); Resolve takes World: neither gets INoStructuralChanges.
```

```csharp
// <auto-generated/> Game.Server: one Registration object; no static registries, no module initializers
public static partial class Registrations
{
    public static Registration Create() => new Registration()
        .Actor<Map>(root: Prefabs.MapPrefab)                               // the runtime finds a Map by world.Index<Map, Map>(); also adds the
                                                                           // framework's closed OrderedPhase<Map>, Depart<Map>, SyncSubscriptions<Map>
        .Prefab<Map>(Prefabs.AvatarPrefab)                                 // a registered copy with In<Map> and a ChildOf column
        .Prefab<Map>(Prefabs.NpcPrefab)                                    // the static prefabs stay as written
        .Actor<Player>()
        .Claims<Account>()
        .JoinOnConnect<Player, Account>(static (in Account c) => new Player(c.Id), Takeover.NewestWins)
        .Dispatch<Map>(static table => table
            .Client<EnterMap>(Maps.On_Enter)                               // [To]: subject is the root; may Join
            .Client<MoveTo>(Moves.On_Move)                                 // subject: the presence
            .Client<CastSkill>(Combat.On_Cast)
            .Event<Strike>(Combat.On_Resolve))
        .Routes(static table => table
            .Route<EnterMap, Map>(to: static (in EnterMap m) => new Map(m.Map))   // [To] long Map -> Map(long Id)
            .Route<MoveTo, Map>()                                          // no [To]: this client's map presence
            .Route<CastSkill, Map>())
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
// No Dispatch<Player>: Player has no handlers in this example; its presences come from JoinOnConnect.
```

```csharp
// <auto-generated/> AppHost, from the actor / hosted-by lines of Game.Server's wire.manifest
namespace Game.Server
{
    public readonly struct GameServer    : IServer;
    public readonly struct MapServer     : IServer;
    public readonly struct GatewayServer : IServer, IAcceptsClients;
}
// The same IServer as the server side (DaServer.Core). Each type is bound to the Game.Server project: AddNodes<MapServer>("map", 4)
// runs that project's binary as MapServer. "map" is the resource and pod prefix; the server type's identity is its FullName.
```

```text
# wire.manifest (excerpt, committed)
# ── Game.Server ──
actor   Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer   tick 30Hz
actor   Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer  join-on-connect Account newest-wins
actor   DaServer.Connection id 3E0F7A21  key int64   hosted-by GatewayServer
claims  Game.Server.Account  int64
event   Game.Server.Strike   layout 7C21A0F3
persist Game.Server.Grid     layout 19E4B2D8
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
client  Game.Protocol.EnterMap   -> Map  to Map  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
on      Game.Server.Strike       Map
leaving Map  Vanish
```

### 测试一个 Actor：`TestActor<K>`

一个 world、一个根、手动时钟；`Outbox` 是数据，直接断言。不起节点，不连网络。用户的系统用 `SystemErrorPolicy.Throw`：系统抛出就是测试失败，不像节点上那样停用了事。

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

    map.Deliver(new CastSkill(Skills.Slash, map.NetIdOf(b)), client: a);
    map.Deliver(new MoveTo(new Vector2(50, 0), default), client: b);
    map.Advance(Skills.Windup(Skills.Slash));
    map.Tick();

    Assert.That(map.Avatar(b).Health.Value, Is.EqualTo(100));
    Assert.That(map.Outbox.Pushes<Nearby>(to: a), Is.Not.Empty);                // what left the actor, as data
}

[Test]
public void A_busy_tick_allocates_nothing_and_threads_change_nothing()
{
    using var map = new TestActor<Map>(new Map(7), Registrations.Create());
    map.Populate(npcs: 20_000);                                                 // a test helper: enough rows for parallel chunks
    map.Tick();                                                                 // warm up: pools, query caches, buffers

    Assert.That(map.AllocatedBytes(static d => d.Tick()), Is.Zero);             // 0 B, counted on every thread
    map.AssertParallelMatchesSerial(ticks: 30);                                 // Capture/Restore, then a serial twin: same result
}
```

- **`map.AllocatedBytes(action)`**：执行一次 `action`（静态 lambda，不捕获），返回分配的字节数。Actor 里有 `Parallel = true` 的方法时用 `GC.GetTotalAllocatedBytes(precise: true)`，否则用 `GC.GetAllocatedBytesForCurrentThread()`（EasyEcs `guide/testing.mdx`）；所以测试按 fixture 串行跑。
- **`map.AssertParallelMatchesSerial(ticks)`**：一个测试里核对并行和串行的结果完全一致。`MaxDegreeOfParallelism` 在建 world 时定下（init-only），`Restore` 只收同一个 world 的快照（别的 world 的抛 `ArgumentException`），所以测试台自己带一个串行的孪生 world（`MaxDegreeOfParallelism = 1`），收到同样的输入：
  1. 主 world `Capture()` 存下起点，跑 `ticks` 帧，记下 `Outbox` 和整棵子树 `SaveEntities` 的字节；
  2. `Restore(起点)`，再跑 `ticks` 帧，和第 1 步比较：并行方法有数据竞争时，两次多线程的结果就可能不同；
  3. 孪生 world 跑同样的 `ticks` 帧，和第 1 步比较：串行和并行一致。

### 运行时的路径

```
① 客户端连 gate-0      框架在 gate-0 激活 Connection(c1)，加上 Account(42) 和连接戳 S1；第一条邮件是 Activated
② 连上就加入           [JoinOnConnect<Account>]：向 Player 42@game-1 发加入请求（带 S1）；Player 建 c1 的 Presence（戳更大的胜出），回复 P1
                      Connection 写路由 (Player 42, P1, 槽 0)，发 JoinConfirmed(P1)；推 Ready，放出排队的消息
③ EnterMap(7)         Connection(c1)@gate-0：过滤 → 路由表：带 [To]，目标 Map 7@map-0；直接转发，信封带着 c1 的上下文（身份、S1、调用 id）
④ Enter               Map 7 的有序阶段：主体是根；找不到 Avatar 就 Spawn 一个，给它 NetId；Join() 当场得到 P2，返回 true
                      Outbox 里先是 Route(Map 7, P2) 给 Connection(c1)，再是回复；这一项执行完一起发出
⑤ Connection(c1)          写路由，槽 1；原来有 Map 3 的路由就删掉，Map 3 对旧 Presence Leave()；向 Map 7 发 JoinConfirmed(P2)；
                      给 SDK 发 JoinConfirmed(槽 1, 调用 id)，再交回 true：SDK 记下 EnterMap(7) 是槽 1 的加入消息
⑥ JoinConfirmed(P2)   Map 7：Avatar 带上 Joined；推时间通知（33 ms、tick 号、服务器时间）；
                      下一个 tick：Regroup 已写好 Subscribed，Snapshot 推快照、加 Synced，收尾的 SyncSubscriptions 订阅网关
⑦ MoveTo              不带 [To]：Connection(c1) → c1 的 Map 路由 → Map 7 的邮箱；下一个 tick 的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Schedule(Strike, after: 前摇)，带着 Due 进有序索引；IsPending<Strike> 拒绝第二次施法
⑨ Strike 到期         tick 窗口所在那一帧的有序阶段，和消息按 (时间, 序号) 归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ 每 33 ms 一个 tick   Ordering（OrderedPhase）→ Update<Map>()：Steering（多线程）、Movement、CellAssign、Regroup、Snapshot、Diff、
                      Vanish、Flush → Settling：Depart、SyncSubscriptions；只遍历带 In<Map> 的表 → Cleanup：ReleaseRoutes 等（按 world）；
                      Nearby → 有观察者的网关 → 各个连接
⑪ map-0 宕机          Map 7 在 map-1 建占位（键和等着的邮件都在它上面），占到新的 epoch，记录装进占位，它就是根（Avatar 不在记录里）；map-0 若只是停顿，醒来后写记录 epoch 比较失败，被隔离；
                      Connection(c1) 看到 map-0 离开视图：删路由，PresenceLost(槽 1) 给 SDK；SDK 重发 EnterMap(7) → Enter 新建 Avatar，Join → 同 ④–⑥
⑫ c1 与 gate-0 的连接半开（gate-0 还没发现断线），c1 换到 gate-1 重连
                      新的 Connection(c1) 带更大的戳 S2：连上就加入，接管 Player 上的 Presence；Ready 之后 SDK 重发 EnterMap(7)：
                      Enter 找到原来的 Avatar，Join() 按戳接管（新的 PresenceId，去掉 Joined 直到确认）；gate-0 上的旧路由按旧的 PresenceId 释放
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：接收方不对没有对应的方法；`[Handles<K>]` 类或 `[UpdateInGroup<K>]` 系统里发送，K 不在消息的 `[From<…>]` 里。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个 Actor 类型处理。
- `[On<Incoming>]` 不在 `[Handles<Connection>]` 里。
- 一个请求有两个处理方法；`Answer` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`；`Reply` 的值类型和令牌的 `T` 不同。
- `Sender<K>` 的 K 不是这条消息声明的发送方（多个 `[From<…>]` 时只能是 `Sender<ActorId>`）。
- 客户端消息有多个 `[To]` 字段；`[To]` 字段的类型和处理 Actor 类型的键形状不一致；`[Handles<Connection>]` 处理的消息带 `[To]`。
- 身份参数（`[Claims]` 类型）用在客户端消息以外的处理方法里。
- `[On<T>]` 方法的参数和过滤不合 EasyEcs `[ForEach]` 的规则。EasyEcs 的分析器只检查 `[ForEach]` 方法，所以 DaServer 的分析器对 `[On<T>]` 方法做同样的检查：参数类型、`Maybe<T>` 参数（job 只收必有的组件）、过滤、`ref`/`in`、复制 `DynamicBuffer<T>`（同 EECS012）。
- 用户代码写 Actor 类型组件，或者把 Actor 类型组件放进 prefab、`CreateEntity`。
- Actor 里调用 `CreateEntity`（用 `root.Spawn`）；对看得出不是根的实体调用 `Deactivate()`。
- 承载 `Connection` 的服务器类型在 AppHost 里没有 `WithClientConnections`（AppHost 构建时检查）。
- 有索引的组件作为 `ref` 参数。
- 方法体里的 `ref` 跨过这个实体的结构变更使用（DAS022）。
- `Parallel = true` 的 `[ForEach]` 方法里调用 `Entity` 上的 DaServer 操作（第 5 节的操作表）。结构变更、并行、有索引的组件，EasyEcs 另有 EECS010、EECS013、EECS015。
- 一种服务器上各帧率不能都整除最快的那个。
- 事件类型不是 `[NinoType]`，或者没有实现 `IEvent`。
- `[Claims]` 类型不是 `partial`、超过 16 字节或不是非托管类型；一个工程有两个 `[Claims]`。
- `[JoinOnConnect<C>]` 的 Actor 类型的键和 C 的形状不同；一个 Actor 类型写了两个 `[JoinOnConnect]`。
- 一个 Actor 类型有两个 `[RootPrefab<K>]`。
- Protocol 里出现 Actor 类型、服务器之间的消息、`ActorId`、`PresenceId` 或 `EntityId`；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- Actor 类型的键超过 16 字节，或者不是非托管类型。
- `IPersisted` 组件不是 `[NinoType]`；`Ask` 的状态类型不是 `[NinoType]`。
- `[Stateless]` 的 Actor 类型里 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。
- 要迁移的托管组件没有 Nino 格式化器。
- 用户的泛型 `[ForEach]` 系统只在别的程序集里封闭（EasyEcs 要在运行时 `MakeGenericType`，Native AOT 下不行，第 10 节）。

**提示**（不报错）：处理方法里结构变更之后还有 `throw` 或可能抛出的调用：先校验，最后做结构变更（第 5 节）。

**启动时检查**：配置的服务器类型不存在，或者和 `wire.manifest` 对不上，节点启动失败。热重启的快照不合格时忽略它，照常从记录激活（第 10 节）。

**实现时要一起改的**：CLAUDE.md 的 Layout 和 ADR-017 还写着"一种服务器一个工程"（`Sample.XServer` 各一个工程）；本稿是一个工程定义多种服务器类型，重构时一并改掉。

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
  - `[Prefab<K>(Root = true)]`（→ `[RootPrefab<K>]`）、`[ModuleInitializer]` 注册和改写静态 prefab（→ `Registration`、`root.Spawn`）；
  - `e.Send(ev[, after:])`（→ `e.Schedule`）、区分 `Send(ev)` 和 `Send(to, msg)` 的分析器规则、`world.IsAlive(s.Id)`（→ `e.IsPending(s)`）、例子里的 `Casting` 组件（→ `IsPending<T>`）；
  - `world.GetEntity(on.Parent)`（→ `e.Root`）、`None`（→ `Answer<TReq>`）、`IServerType`（→ 一个 `IServer`）；
  - `PresenceId` 的节点位、进程启动时随机的节点 id（→ 集群分配的节点代数）、只比较相等的连接代数（→ 有序的连接戳）；
  - UTC 纪元的时间、依赖 NTP 的绝对到期时刻（→ 单调时钟、剩余时间）；
  - 自动的 `NeedsSnapshot` 和"挂接/重放后推完整快照"（→ 用户逻辑）；Logic 决定有没有 tick 和对应的热更规则（→ `[TickRate]` 在 Server）；
- v24 修订中去掉的：处理方法的 `Maybe<T>` 参数（→ 方法体里 `TryGetX`）、激活时把 Actor 类型组件和 `DynamicBuffer<Mail>` 从占位搬到新根（→ 占位就是根）、生成的 `find:` 委托（→ 运行时的 `world.Index<K, K>()`）、"迁移到达后按 `Subscribed` 重新订阅"的单独规则（→ 迁移到达算作写入）、每次调用 `QueryFilter.Create()`、`Spawn(…).SetX().SetY()` 的多次行移动（→ `Spawn(prefab, in a, in b)`）、按 Actor 的系统耗时（→ `system.Stats` 按 world）；
- v25 的：`Game.Server.Logic` 工程和工程之间的循环、可回收的 `AssemblyLoadContext`、`RemoveTypes`、两个 tick 之间换分发表的热更、`wire.manifest` 的 Logic 一段、`Registration` 的 Logic 部分、"组件声明在 Logic"的检查、"AOT 不热更"（→ 两个工程、原地热重启，JIT 和 AOT 一样）；
- v27 的：`Client` 这个 Actor 类型名（→ `Connection`）、`a.Reply`（→ `a.Value`）、`Time.Now`（→ `Time.Elapsed`）、`world.IsPending`/`world.Cancel` 和 `HasPending`/`CancelPending`（→ `e.IsPending`/`e.Cancel`）、`Entity` 上任意实体的 `Deactivate()`（→ 只在根上）、子实体键里的根 `EntityId`（→ Actor 的键）、AppHost 节点组上的 `WithClientEndpoints()`（→ `WithClientConnections(port)`）、`NodeGroup<T>`（→ `DaServerNodeGroup<T>`）、“挂接”的说法（→ 连上就加入）；
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
| v26 | 进程内热更换成原地热重启：两个 tick 之间 `world.Save` 交给新进程（本机管道或节点本地快照文件），同一个节点代数、化身号加一、epoch 不变，不重新激活；JIT 和 AOT 一样；去掉 Logic 工程，用户工程只有 Protocol 和 Server；`wire.manifest` 每个工程一段；不重启就能改的是数据（配置表、功能开关） |
| v27 | 名词精简：去掉“域”，就是 Actor（根实体加子孙）；shard 只在框架内部，用户看到的是节点里的 world，个数按 CPU 自动定，`AddNodes` 不再有 `shards`；`TestDomain<K>` 改名 `TestActor<K>`；包合并成 Core / Client / DaServer / Persistence.PostgreSql / Testing / Aspire（ADR-086） |
| v28 | 内置的 Actor 类型 `Client` 改名 `Connection`，“Client”只指远端的客户端；`a.Value`、`Time.Elapsed`；`e.IsPending`/`e.Cancel` 一对（按句柄或按类型）；`Deactivate()` 只在根上；子实体的键带 Actor 的键，不带根 `EntityId`；Actor 里不用 `CreateEntity`；AppHost 是 `Game.Server/apphost.cs`（单文件，`#:project`），集群名和组名分开，`DaServerNodeGroup<T>`，端口按组显式声明（`WithClientConnections(port)`、`WithExternalHttpEndpoints()`，集群和 HTTP 端口只在集群内），热重启命令 `{cluster}-restart`；“连上就加入”取代“挂接”；§0 补齐框架给的名字和术语；`PresenceLost` 不算 Actor 的生命周期 |

---

## 14. 已定的取舍，以及待办

本稿没有需要拍板的项。下面是起草时比较过的做法，以及按需求（通用、ECS、解耦、DRY、易维护、分布式正确）选定的理由。

1. **身份用用户自己的类型 `[Claims] Account` 加 `[JoinOnConnect<Account>]`**，不用框架包装 `Claim<Player>`。框架是通用的，不是每个应用都有 Player（例如秒杀的买家）；身份绑在某个 Actor 的键上，令牌格式会随那个键变。代价是 `Account` 和 `Player` 的字段写两遍，由分析器保证形状相同。
2. **客户端路由由 `[On<T>]` 所在的 `[Handles<K>]` 类推出**，不另写路由表。同一件事只写一处；改动路由的版本要连网关一起滚动升级，热重启由 `wire.manifest` 比对拦住（第 10 节）。
3. **`[From<K>]` 保持必写**。"从类型就看得出谁发给谁"是目标之一；新的发送方要改消息声明，这正是想要的显式。
4. **消息不做成实体**。邮箱和到期项在有序阶段归并两个来源。把每条消息做成带 `Due` 的实体能统一表示，但每条消息多一次建实体和销毁。重构时用 BenchmarkDotNet 测一次：快不差于 10% 才改。
5. **EasyEcs 4.6 已提供本稿依赖的功能**：`entity.Run(ref job[, filter])`（抛出时 `ref` 写入也搬到新行；job 带 `INoStructuralChanges` 时冻结结构）、接口带的组过滤 `IGroupWith<T>` / `IGroupWithout<T>`、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（被覆盖的 prefab 默认值也不算写入），分析器把 `entity.Run` 和 `LoadEntitiesInto` 当结构调用（EECS010/013/014）；4.6 又加上 `world.Load(bytes, markWritten: false)`、`AddSystem(…, changesFromNow: true)`（需求 #22），带追踪组件的程序 Native AOT 发布 0 警告（需求 #21）。EasyEcs 评估后放弃的：并行批量方法、`LastWriteTick`、公开读写集、多 world 共享并行预算。重构的第一步是把 `EasyEcsVersion` 升到 4.6.0（等它上了 NuGet），并编译验证两件事：DaServer 生成器补的 `Key` 显式实现被 EasyEcs 的值索引接受；`IActor<TSelf>` 在框架程序集里编译通过。
6. **改代码只有热重启，不用 `AssemblyLoadContext`**：理由和代价见 ADR-084，做法见第 10 节；Kubernetes 上的形态见 ADR-085。加载用 EasyEcs 4.6 的 `world.Load(bytes, markWritten: false)`，加载的行不算变更（第 10 节的"写入戳"）。`Save` 写进 `NinoArrayBufferWriter` 再拷进管道或文件，多一次拷贝；10 万个实体 4.4 MB，可以忽略，不另提需求。
