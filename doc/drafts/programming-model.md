# DaServer 新编程模型设计（草案 v19，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。能用 EasyEcs 的功能就用 EasyEcs，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个实体发到哪个实体，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分；
- 高性能，高并发。

本稿是完整的一份。第 12 节是完整的例子，分别列出用户写的代码和生成的代码。第 13 节是版本记录，第 14 节是待拍板的点。

---

## 0. 一页看懂

### 三个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息。只有数据，不出现任何服务端的种类 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | 种类、服务器之间的消息、组件、prefab、服务器类型、系统组，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有逻辑：`[Receive]` 方法和 `[ForEach]` 系统，以及生成的分发表、客户端路由表 | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

### 用户写的

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 推给客户端的消息 | `partial class Nearby : IToClient`（含集合的消息是 `[NinoPooled]` 类） | Protocol |
| 种类（地址，也是实体上的组件） | `partial record struct Map(long Id) : IKind<Map>;` | Server |
| 服务器之间的消息 | `[From<Player>] record struct EnterMap(…) : IRequest<Map, LinkId>;` | Server |
| 组件 | `record struct Grid(…) : IComponent;`；持久化的再加 `[Persist]` | Server |
| prefab | `[Spawns<Map>] static readonly Prefab Map = …;` | Server |
| 令牌里的声明 | `[Claims] record struct PlayerClaims(long PlayerId) : IComponent;` | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 系统组的频率 | `[Every(50)] struct Simulation : ISystemGroup;` | Server |
| 处理消息 | `static partial class` 里的 `[Receive]` 方法 | Logic |
| 每 tick 的逻辑 | `partial class … : EcsSystem` 里的 `[ForEach]` | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `Client` | 内置的种类：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Self<K>` | 正在处理消息的实体自己：强类型的键，以及发消息的入口 |
| `KindKey` | 任意种类的地址（种类 id 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键 |
| `LinkId`、`Linked`、`Confirmed` | 客户端在某个目标上的链接；目标上的链接实体，例如地图上的 Avatar |
| `ReplyTo<T>`、`Answer<TReq, TRep>`、`[Pending]`、`[Link]` | 晚些回复；等别的实体的结果；参数绑定到哪个实体 |
| `Activated`、`Unlinked`、`LinkLost`、`Unloading` | 生命周期消息：框架发给实体自己，用 `[Receive]` 处理 |
| `IdleAfter`、`Due`、`Clock` | 空闲回收；定时；shard 的时钟单例（`DeltaTime`、`Now`） |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 种类组件的值索引 | 为 `partial` 种类生成 `Key => this` 和种类 id 常量 | 收到消息时查索引 |
| 只处理一类实体 | `[With<Map>]` | — | — |
| 每 tick 的逻辑 | `[ForEach]` 生成的 job、`Parallel`、批量方法、`[Changed<T>]` | — | — |
| 频率 | 系统组 | `[Every(ms)]` | shard 循环按时调用 `Update<T>()` |
| 消息处理 | — | `[Receive]` 生成的分发跳板 | `MailSystem` 按到达顺序调用，逐条隔离异常 |
| 发送方向 | — | `[From<K>]` 生成 `Self<K>` 上的方法 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Receive<K>]` 生成路由表 | `Client` 实体转发 |
| 等待、回复 | `ChildOf`、`ICleanupComponent` | `[Pending]`、`ReplyTo<T>` | 调用 id、`Due`、期限传递 |
| 链接 | 子实体、值索引 | `[Link]` 参数绑定 | 确认、核对、重连 |
| 生命周期 | — | 用户写 `[Receive]` | 投递生命周期消息 |
| 定时 | 有序值索引的 `Range` | — | `TimerSystem` |
| 一张地图的实体 | `ChildOf`、`world.Children(map)` | — | — |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ICleanupComponent` | `[Persist]` | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收的上下文 | 分发表、路由表在 Logic 程序集里 | 两次更新之间切换 |
| 对象复用 | `DynamicBuffer` 池化、Nino 的 `[NinoPooled]` | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。** 生命周期事件也是消息，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，同 Orleans：迁移那一两秒里，在途的消息可能后发先到。
   - 需要严格先后的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。
4. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。
5. **每种失败的结果都是确定的**，见第 11 节。

### 执行模型：一致性域内串行、当场生效；域之间并行；域内的模拟并行

（结论来自调研：actor 运行时与分区数据库、游戏服务器、确定性并行执行、EasyEcs 4.4 的能力。出处见 `doc/drafts/research-ordering.md`。）

**语义：在一个一致性域内，结果严格等于按到达顺序逐条执行，每条消息的效果当场生效。**
- 用户不需要判断"哪些要即时"：`[Receive]` 里写的一切都是即时的，没有例外。
- `[ForEach]` 只写"时间流逝"带来的变化（tick 是排在同一条线上的一条消息，见第 5 节）。代码写在哪里，就决定了它是消息的效果还是时间的效果，不存在第三种选择。

**一致性域**：一组需要同步交互、必须看到同一个顺序的状态，放在同一个实例里，影响它的消息都发给它。
- MMO：地图（或地图的一个区域）。同一张地图上的移动、战斗都发给地图，攻击当场看到刚才的移动。
- 背包、钱包：Player。交易：一个 `Trade` 实例。秒杀库存：多个库存分片。
- 无状态的工作：`[Stateless]`，不排队，哪个节点先收到就哪个处理，并发交给数据库。

**多核从哪里来：**

| 层次 | 怎么并行 | 同行 |
|---|---|---|
| 域之间 | 每个核一个 shard 线程，许多个域按键哈希分到各个 shard | ET：每张地图一个 Fiber，Fiber 跑在线程池上；Valorant：一个核跑三局以上；VoltDB：每个分区一个线程 |
| 域内的模拟 | tick 里的 `[ForEach(Parallel = true)]`、批量方法、SIMD：移动推进、buff、回复、AOI | Unity、Bevy、Flecs：按读写集并行跑系统 |
| 域内的消息 | 串行。处理方法便宜：1,000 人、每人每秒 20 条、每条约 1 µs，约占一个核的 2% | LMAX：单线程每秒 600 万笔；Redis：单线程 |
| 太热的域 | 业务把它拆成多个实例：地图分区域（边界交接）、分线、副本 | New World、SpatialOS、Hadean：分格子交接；WoW：分片、分层；EVE：独占节点、时间膨胀 |
| 输出 | 序列化、广播合并、AOI、写记录移出业务处理：放到并行的 tick 系统和网络线程 | 天谕手游：业务单线程，视野序列化和广播挪到网络线程，单图 1150+ 人；倩女 Avatar：热点场景拆到多个进程，约 500 人，代价是异步结算 |

**缓存友好从哪里来：**
- 一个域永远在同一个 shard 线程上，数据留在这个核的缓存里（单写者原则：只有一个核写某块内存）。
- `MailSystem` 处理一个实体时，把它邮箱里已到的消息一次处理完，最多 N 条（默认 64）再换下一个，同 Akka 的 `throughput`、Orleans 的 `ActivationSchedulingQuantum`。
- 组件按列存放；tick 系统按列批量遍历。

**和常见的"单线程游戏服"对比：**

| | 单线程游戏服（一条游戏线程，玩家的处理方法直接调用地图） | 本设计 |
|---|---|---|
| 消息效果 | 当场生效 | 当场生效（相同） |
| 地图上的顺序 | 取决于遍历玩家的顺序，不是到达顺序 | 按到达地图实体的顺序 |
| 多核 | 只能多开进程 | 每核一个 shard，很多张地图并行；地图内的模拟并行 |
| 数据布局 | 组件里常装堆对象，系统遍历对象列表 | 组件按列，`[ForEach]` 查询，批量方法可 SIMD |

**域内消息的并行，按代价排序：**

| 做法 | 结果等于串行吗 | 代价 | 结论 |
|---|---|---|---|
| 串行逐条处理（默认） | 是 | 无额外开销 | 默认。处理方法便宜，占核很少 |
| **孤岛并行**：每批消息按"写到同一个（实体, 组件）"用并查集分成孤岛，每个孤岛在一个线程上按到达顺序串行，孤岛之间并行 | 是 | 每条消息几十纳秒；需要读写集 | 以后的可选优化，见下 |
| 分波次并行（按依赖层级，每层按消息类型成批，可 SIMD） | 是 | 每层一个屏障；一群人打同一个 boss 时退化成几百层 | 只在孤岛很大、层数很少时用 |
| 乐观并行再校验（Block-STM） | 是 | 要多版本内存、能回滚写入和结构变更；为 100 µs 级的交易设计，我们的消息约 1 µs | 不采用 |
| 重排或冲突重试（Aria、Project Darkstar） | 否，打乱到达顺序 | — | 不采用 |
| 延迟的命令缓冲（Unity ECB） | 效果被推迟 | 用户又要判断"哪些是即时的" | 不采用 |
| tick 语义（Overwatch、Unity Netcode、Photon、帧同步）：同一 tick 的输入视为同时，按固定规则裁决 | 不是到达顺序 | 天然可批量 | 框架不内置；需要的游戏在 tick 系统里自己读输入缓冲 |

**孤岛并行的读写集从哪里来**（以后做，前提是实测证明消息处理占 shard CPU 超过约 20%）：
- 生成器从 `[Receive]` 的签名得出静态部分：`ref Health` 是写、`in Position` 是读，作用在接收实体（或 `[Link]` 实体）上。
- 作用在别的实体上的，由消息里标注的字段声明，例如 `[Target] Player Victim`，参数写成 `[On(nameof(Attack.Victim))] ref Health victimHp`。
- 运行时通过查询、`World` 访问到的，算作"宽"消息：它在批次里是一个串行屏障，前面的做完、它单独执行、后面再继续。分析器标出哪些处理方法是宽的。
- 热点目标（一群人打同一个 boss）：只有这个 boss 所在的孤岛串行，其余照常并行。对只做累加的组件（`hp -= dmg`），可以用按顺序的前缀和并行算出每一步的值，包括哪一下是致命一击，结果仍等于串行。
- 需要 EasyEcs 的第 12、13 条（运行时读读写集、对给定实体列表并行执行）。

**一个 shard 上多张地图的 AOI 并行**：需要 EasyEcs 的第 11 条（按分组值并行 `[ForEach]`）。

---

## 2. 服务器与节点

```csharp
// Game.Server
[Hosts<Player>, Hosts<Guild>] public struct GameServer    : IServer;
[Hosts<Map>]                  public struct MapServer     : IServer;
[Hosts<Client>]               public struct GatewayServer : IServer;   // hosting Client = accepting clients
```

- **节点 = 一个进程 = 一种服务器。**
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程只跑一种服务器。
  - 本地开发由 Aspire 起多个进程；测试用 `TestCluster`。
  - 小规模部署可以写一个 `[Hosts<Player>, Hosts<Map>, Hosts<Client>] AllInOne`。
- **`[Hosts<Client>]` 就是"接受客户端"。**
- **种类绑定的是服务器类型，不是节点：**

  | 关系 | 数量 | 由谁决定 |
  |---|---|---|
  | 种类 → 服务器类型 | 一般一对一；`AllInOne` 时一对多 | 服务器上的 `[Hosts<…>]` |
  | 服务器类型 → 节点 | 一对多 | AppHost 部署几个 |
  | 种类 → 实例 | 每个键一个 | 第一条消息到达时激活 |
  | 实例 → 节点 | **任一时刻恰好一个** | 在承载该种类的节点中按键放置，节点增减时迁移 |

  - 同一个键任一时刻只有一个实例，由现有的放置、记录版本和隔离机制保证。
  - `Client` 是例外，它固定在连接所在的节点（第 7 节）。
- **shard**：节点里的一个线程加一个 world。节点承载的所有种类共用 shard 的 world，实例落在哪个 shard 由键的哈希决定。
- **AppHost：**
  ```csharp
  var cluster = builder.AddDaServerCluster("game");
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientEndpoints();
  ```
  - AppHost 不引用服务端程序集。DaServer.Aspire 的生成器读服务端工程提交的 `wire.manifest`，在 AppHost 里生成同名的服务器类型。
  - `AddNodes<T>` 返回 `NodeGroup<T>`。承载 `Client` 的服务器，生成的类型实现 `IAcceptsClients`；`WithClientEndpoints()` 是 `NodeGroup<T>` 上 `where T : IAcceptsClients` 的扩展方法，所以给别的服务器开客户端端口编译不过。
  - 接受客户端的服务器没开端口时，AppHost 构建警告；消息引用的种类没有节点承载时，构建报错。
  - 节点启动时按配置的服务器类型再校验一遍，写错就启动失败。

---

## 3. 种类与地址

**种类就是它的地址，也是实体上的组件。**

```csharp
// Game.Server
public readonly partial record struct Player(long Id) : IKind<Player>;
public readonly partial record struct Map(long Id)    : IKind<Map>;
public readonly partial record struct Room(long Id)   : IKind<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IKind<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个实例的地址。消息和组件里存别的实例的地址，直接用 `Map`、`Player` 类型的字段。要存"任意种类的地址"时用 `KindKey`。

**同一个值也是组件**：地图实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 只处理地图的系统写 `[With<Map>]`；
- `[ForEach]` 里取 `in Map self`，就知道自己是谁。

**接口**：`IKind<T>` 继承 `IComponent` 和 `IIndexed<T>`。
- EasyEcs 的生成器会读继承来的接口，所以 EasyEcs 不用改。
- 种类必须是 `partial`，我们的生成器给它补上 `public T Key => this;` 和种类 id 常量 `public const uint KindId`（Nino 类型 id）。

**只有运行时能写种类组件：**
- 分析器禁止用户代码对种类组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时创建实例时，断言这个键在索引里还不存在。

**键**：
- 种类的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[Stateless]` 种类**：不保存，不迁移；每个节点可以有自己的实例，键只用来选实例。
- **子实体不可寻址**，例如 Avatar、等待中的请求。它们跟父实体一起保存、一起迁移。
- **没有"无键种类"**：
  - 每个节点一份的状态，用单例；
  - 无状态的工作，用 `[Stateless]`；
  - 全局服务，用固定键，例如 `Matchmaker(int Region)`。

---

## 4. 消息

### 客户端收发的消息（Protocol）

```csharp
// Game.Protocol: data only, no server kinds
[NinoType] public record struct TravelTo(long MapId)       : IFromClient<bool>;   // a request: the client waits for a bool
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;
[NinoType] public record struct Say(long To, string Text)  : IFromClient, IAddressed;   // To picks one of several rooms
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
```

- `IFromClient` 是客户端发的单向消息，`IFromClient<TReply>` 是客户端发的请求，`IToClient` 是推给客户端的消息。
- 含集合的消息是 `[NinoPooled]` 类（Nino 4 的规则），发送后自动归还，不会每次推送都分配。
- **消息里不写目标**，所以客户端不知道服务端有 Player、Map、Room。
- **目标由服务端决定**：
  - 处理这条消息的 `[Receive<Map>]` 写的是哪个种类，消息就转发给哪个种类；
  - 生成器据此在 Logic 里生成客户端路由表；
  - 一条客户端消息只能有一个目标种类。
- `IAddressed` 的消息带一个 `To` 字段，用来在同种类的多个链接里选一个。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Client>] public record struct Attach                   : IRequest<Player, LinkId>;
[NinoType, From<Player>] public record struct EnterMap(Player Player)  : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player)  : IMessage<Map>;
```

- **接收方写在接口上**：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。
  - `[From<Client>]` 指服务端的 `Client` 实体，不是远端的客户端。
  - 远端的客户端只能发 `IFromClient`。
- **方向由生成的方法保证**：每条消息只在它的发送方的 `Self<K>` 上生成发送方法，方向写错就没有这个方法，编译不过。
  - 这是帮助写对的手段，不是安全边界。
  - 安全边界在网关：只转发 `IFromClient`。
- **代替客户端发请求**：Player 处理客户端的 `TravelTo` 时去问地图，用 `self.AskFor(from, map, in enterMap, …)`。
  - 框架把这个客户端的连接信息（`ClientId`、所在节点、连接代数）放进信封，地图建链接时用；等待实体也记下它，回信时用来写路由。
  - 用户不接触这些信息。
- **`ICluster`**（HTTP、后台、测试）不属于任何种类，可以发任何服务器之间的消息，回复回到调用方。
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Client` 的节点，由连着它的那个处理。

---

## 5. 逻辑

### v18：动作是随时间推进的过程；消息开始或改变动作，帧系统推进和结算

**真实的玩法不是"一条消息、一个瞬间的结果"：**
- 移动可能走 1 帧，也可能走 n 帧；
- 攻击先有 3 帧前摇，第 4 帧才结算伤害。目标在前 3 帧里躲开了，就是闪避。

所以一次攻击的结果，取决于结算那一帧时目标在哪里，而不是攻击包和移动包谁先到。这正好把顺序问题交还给了时间，也正好适合 ECS。

**模型：**

| 步骤 | 在哪里 | 做什么 | 执行方式 |
|---|---|---|---|
| 1. 收到消息 | `[On<消息>]` | 校验（被眩晕了不能放技能、蓝够不够），然后把"开始一个动作"写成数据：`Casting(技能, 开始帧, 序号)`、`MoveIntent(方向, 开始帧)`、`Stop` | 按到达顺序逐条执行，便宜，当场生效 |
| 2. 每一帧 | 帧系统（`[On<Tick>]`） | 推进所有进行中的动作：移动按帧积分；前摇到了结算帧的技能，用这一帧所有人的实际位置判定命中；施加伤害、判定死亡；buff | 批量，按列并行，可 SIMD |
| 3. 同一帧内的先后 | 帧系统之间的顺序 | 先移动，再判定命中，再施加伤害，再判定死亡：这就是这个游戏的规则，由系统顺序明确写出来 | 确定 |
| 4. 同一帧的冲突 | 结算系统 | 两人抢同一格、两刀同时打死同一个人：按 `(帧, 到达序号)` 排序裁决，谁的包先到谁优先 | 确定，可批量 |

**回到"攻击 A、移动 B、移动 A、攻击 B"：**
- 四个包只是在各自到达时开始了各自的动作：A 开始前摇、B 开始移动、A 改变移动、B 开始前摇。
- 第 4 帧时，A 的攻击用"B 在第 4 帧的实际位置"判定：B 在前摇期间走开了，就是闪避。
- 结果由玩法规定的时间决定，不再依赖哪个包先被处理。同一帧里真正要比先后的地方，用到达序号作为确定的裁决依据。

**和 v17 比：**
- 消息处理更简单了：只校验、只记录动作的开始，不做结算。
- 所有结算都在帧系统里，就是你倾向的"消息改状态，系统做事"。并行、SIMD 都落在这里，而这里正好是 CPU 花得最多的地方。
- "立即生效"的含义变成：消息的效果（动作开始了、状态改了）立即对之后的消息可见。例如开始施法后马上收到的第二个施法请求会被拒绝。动作的结果在它该发生的那一帧生效。
- 没有前摇的瞬时动作（交易、拾取、使用道具）：
  - 可以在消息里当场完成（先到先得）；
  - 也可以当作 0 帧的动作，由下一帧的系统按到达序号批量结算。
  - 两种结果一致，由玩法选。

**用户写的：**

```csharp
[On<CastSkill>]                                   // validate, then start the action as data
static void Cast(in CastSkill m, [Link] Entity me, [Link] in Stunned? stunned, in Clock clock, in Arrival seq)
{
    if (stunned is not null || me.Has<Casting>()) return;
    me.SetCasting(new Casting(m.Skill, m.Target, ResolveAt: clock.Frame + Skills.Windup(m.Skill), seq.Value));
}

[On<Tick>, UpdateAfter<MovementSystem>]           // every frame: casts whose windup ends now resolve against positions now
static void Resolve(Entity e, in Casting c, in Position me, in Clock clock, World world)
{
    if (c.ResolveAt != clock.Frame) return;
    world.Hits.Add(new Hit(e, c.Target, Skills.Damage(c.Skill), c.Seq));   // collected; applied by HitSystem sorted by (frame, seq)
    e.RemoveCasting();
}
```

- 命中判定读的是这一帧的位置；前摇期间目标走开，就判定为闪避。
- `HitSystem` 按目标汇总这一帧的伤害，按到达序号排序后施加，按目标并行；同一帧里谁先打死谁，也是确定的。

### v19：n = 0 也要支持——动作是"延后 n 帧的事件"，n = 0 就当场执行

前摇为 0 的动作要立即结算：瞬发技能、喝药、买东西、交易。它们不能统一推到下一帧：
- **资源类的必须当场结算**：连发两次"购买"，如果都推到下一帧再扣钱，两次校验看到的都是扣钱之前的余额，就会超支。
- **后面的消息要看到结果**：喝完药立刻放技能，技能的校验要看到加血之后的血量。

**做法：把"结算"写成一个事件，由消息决定它延后几帧。用户只写一份结算代码。**

```csharp
[On<CastSkill>]                                   // validate, then schedule the resolution n frames later
static void Cast(in CastSkill m, [Link] Self<Avatar> me, [Link] in Stunned? stunned)
{
    if (stunned is not null) return;
    me.After(Skills.Windup(m.Skill), new Strike(m.Skill, m.Target));   // n = 0: runs right now, inline
}

[On<Strike>]                                      // the one resolution, whether n is 0 or 10
static void Resolve(in Strike s, [Link] in Position me, [Of(nameof(Strike.Target))] in Position target,
    [Of(nameof(Strike.Target))] ref Health hp)
{
    if (Skills.InRange(s.Skill, me.Value, target.Value)) hp.Value -= Skills.Damage(s.Skill);   // out of range now: dodged
}
```

**运行时怎么执行 `After(n, 事件)`：**

| n | 何时执行 | 顺序 |
|---|---|---|
| 0 | 当场，作为当前消息处理的一部分（同一个事务） | 和消息的到达顺序一致：后面的消息一定看到它的结果 |
| > 0 | 第 当前帧 + n 帧，和这一帧到期的其他事件一起执行 | 按 `(帧, 到达序号)` 排序；结算前先跑完这一帧的移动，所以读到的是这一帧的位置 |

- 到期的事件是数据（带 `Due` 的子实体）。同一帧到期的同类事件可以成批执行：互不相干的并行，同一目标的按到达序号依次执行（孤岛）。
- 前摇期间被打断：打断的处理方法删掉这个待执行的事件，它就不会发生。
- 瞬发（n = 0）和前摇（n > 0）走的是同一个 `[On<Strike>]`，所以不存在两套结算逻辑。

**为什么 n = 0 当场执行，和推到帧边界结算的结果一样（除了资源类）：**
- 位置只在帧推进时改变。所以同一个帧窗口里，当场判定和帧边界判定读到的是同一份位置。
- 血量、资源在当场判定时按到达顺序修改，在帧边界时按到达序号修改，顺序一致。
- 区别只在资源类的校验：当场执行时，后面的消息立刻看得到扣款；推到帧边界则看不到。所以 n = 0 一律当场执行。

**于是整个模型只剩三样东西：**
1. **消息**：校验，修改即时的状态（资源、冷却、动作开始），按到达顺序当场生效。
2. **事件**：`After(n, …)`。n = 0 当场执行；n > 0 在第 n 帧和其他到期事件一起成批执行。
3. **帧**：推进随时间变化的状态（移动、buff、回复），按列并行，可以 SIMD。

### 结论（v17）：消息是事务，tick 是并行计算；并行由运行时决定，用户不拆分

调研下来，没有一个已发布的系统同时做到三件事：写起来像顺序代码、结果等于按到达顺序执行、一个域内由运行时自动并行（出处见 `doc/drafts/research-ordering.md` 第 6、7 节）。各家的取舍：

| 系统 | 写法 | 一个域内的执行 | 规模从哪来 |
|---|---|---|---|
| SpacetimeDB（BitCraft） | 每个 reducer 是一个事务 | 每个数据库单线程，约 30 万笔/秒 | 按区域分库；周期性的"agent" reducer 批量处理 |
| Verse（Fortnite） | 函数默认是事务，效果写在签名里（`<reads>`/`<writes>`/`<computes>`） | 事务目前单线程，用于失败回滚 | 并行是远期目标 |
| Sweeney（2006） | 游戏逻辑用事务；数值计算纯函数并行 | 估计约 80% 的 CPU 在可并行的数值部分 | — |
| Roblox Parallel Luau | 用户自己切换并行阶段和串行阶段 | 并行阶段基本只读，写回串行 | 用户自己拆 Actor |
| Bevy / Unity DOTS / Unreal Mass | 用户在"立即但串行"（observer）和"并行但延后"（系统、命令缓冲）之间选 | — | — |
| Paradox CK3 | 每个系统先并行读、只写私有数据，再按确定顺序写 | — | 开发者评价"最有效、最好教" |

另外几条有数据的结论：
- 消息处理本身很便宜：1,000 人的地图每秒约 2.5 万条消息，只占一个核的 0.1–5%。
- 并行化很小的工作得不偿失：Factorio 并行电网，CPU 用量从 0.5% 涨到 15%，这一项只快了 22%，整体没有变快。
- 分散访问基本吃不到 SIMD 的好处：gather 最多快 1.1 倍。SIMD 的收益在对同类组件列做统一计算的地方。

**所以 v17 的模型是：**

1. **`[On<消息>]` 是一个事务**：按到达顺序执行，效果当场生效，判定和状态修改都在这里。用户不判断"要不要即时"，因为一律即时。
2. **`[On<Tick>]` 是按时间推进的批量计算**：也排在同一条顺序线上，内部按组件列并行、用 SIMD。这是 Sweeney 的划分：游戏逻辑走事务，数值计算并行。
3. **两者写法相同**（参数是组件），只差在由哪个事件触发。判断标准只有一条：**这件事由什么引起？** 由消息引起就写在 `[On<消息>]` 里，由时间流逝引起就写在 `[On<Tick>]` 里。这是策划本来就在想的问题，不需要再拆一遍逻辑。
4. **性能主要来自 tick 的并行和许多个域并行**，和 BitCraft、网易、ET 一样。消息处理先串行，因为它便宜，而且最容易保证正确。
5. **消息的并行以后再加，而且不需要用户声明。** 实测证明消息处理占 shard CPU 超过约 20% 时再做：
   - 签名就能看出只碰自己和消息目标的消息，按孤岛并行；
   - 目标要运行时才知道的（"打最近的敌人"），先在同一个快照上乐观执行，记下实际读写，再按到达顺序提交互不冲突的，冲突的串行重跑。这就是 Aria 的做法，但关掉它的重排。
   - 所以正确性永远不依赖声明；声明（例如参数上标明目标）只让它更快。之前的 `[Of(nameof(…))]` 不再是必需的。
   - 前提是有副作用的操作（发消息、推送、写记录）都先进发件箱，提交时才真正发出。框架现在就是这样做的。
6. **给用户的反馈**：
   - 分析器标出哪些处理方法要走乐观路径；
   - 运行时给每个域报指标：串行占比、孤岛大小、重跑率；
   - 调试模式把每批改成串行执行，并核对结果是否完全一致。
7. **按客户端时刻判定**（延迟补偿）是可选手段：消息带上时刻，位置存一段历史，判定时读那一刻的位置。这个只给需要的玩法用，不作为默认。

### 统一的写法：一切都是对事件的反应

**用户只写一种东西：某个事件发生时，对哪些实体的哪些组件做什么。**

```csharp
[On<MoveTo>]                                   // a point event: one target, applied now
static void Step(in MoveTo m, [Link] ref Position p, [Link] ref Velocity v, in Grid grid) { … }

[On<Cast>]                                     // a point event that touches another entity, declared by a field
static void Hit(in Cast m, [Link] in Position me, [Of(nameof(Cast.Target))] in Position target,
    [Of(nameof(Cast.Target))] ref Health hp) { … }

[On<Tick>]                                     // a broadcast event: every matching entity, applied now
static void Advance(in Tick t, Span<Position> p, ReadOnlySpan<Velocity> v) { … }   // batch form, SIMD
```

- **tick 也是一个事件**：`Tick` 是一条发给本 shard 所有匹配实体的消息，排在同一条线上：`m1, m2, Tick, m3 …`。
- **写法完全相同**：参数都是组件（`in` 读、`ref` 写），由绑定规则决定是哪个实体的组件。点事件和广播事件只差在 `[On<…>]` 写的是哪个事件。
- **所有效果都当场生效**：每个事件的处理方法执行完，它的效果就对之后的所有事件可见。
  - 移动是 `MoveTo` 事件的效果，当场生效；
  - 惯性推进是 `Tick` 事件的效果，在 tick 那一刻对所有实体同时生效。
  - 所以不存在"ECS 的模拟要过一会儿才看到"：模拟就是 tick 这个事件的效果，从 tick 之后就可见。
- **用户不拆分逻辑**：要写的只是"什么时候发生"（哪个事件），这是策划本来就在想的。用户不需要决定"即时还是延后"，也不需要决定"串行还是并行"。

**怎么执行由运行时根据签名决定，结果总是等于按顺序逐条执行：**

| 事件 | 运行时怎么做 | 用到的 |
|---|---|---|
| 广播事件（`Tick`、按地图的事件） | 每个实体只读写自己的组件（从签名可证），所以所有匹配实体并行；批量方法用 SIMD | EasyEcs 的并行 `ForEach`、批量方法 |
| 一串点事件 | 按签名得到每条的读写集（实体由绑定和 `[Of(…)]` 字段决定），分成互不相交的孤岛：孤岛之间并行，孤岛内按到达顺序；同一孤岛的同类事件连续执行，缓存友好 | 生成的读写集；EasyEcs 第 12、13 条 |
| 宽事件（处理方法通过查询或 `World` 访问了签名之外的实体） | 一个串行屏障：前面的做完，它单独执行，后面再继续 | — |
| 一个热点目标（很多人打同一个 boss） | 只有这个孤岛串行；对只做累加的组件（`[Accumulate] Health`），用按顺序的前缀和并行算出每一步，结果仍等于串行 | — |

**一个完整的例子：用户什么都不用判断。**

用户写的（全部是普通的同步代码）：

```csharp
[On<MoveTo>] static void Step(in MoveTo m, [Link] ref Position p) => p.Value = m.At;

[On<Cast>] static void Hit(in Cast m, [Link] in Position me,
    [Of(nameof(Cast.Target))] in Position target, [Of(nameof(Cast.Target))] ref Health hp)
{ if (Vector2.Distance(me.Value, target.Value) < 5) hp.Value -= 10; }

[On<Cast>] static void Aoe(in CastAoe m, [Link] in Position me, World world)   // reaches entities through World
{ foreach (var e in world.Near(me.Value, 8)) e.WriteHealth().Value -= 5; }

[On<Tick>] static void Regen(in Tick t, Span<Health> hp) { /* +1 each, SIMD */ }
```

地图依次收到：`① P1 移动  ② P2 移动  ③ P3 打 P2  ④ P4 移动  ⑤ P5 群攻  ⑥ P6 移动  ⑦ Tick`

运行时看签名就知道每条事件读写什么：

| 事件 | 读 | 写 |
|---|---|---|
| ① P1 移动 | — | P1.Position |
| ② P2 移动 | — | P2.Position |
| ③ P3 打 P2 | P3.Position、P2.Position | P2.Health |
| ④ P4 移动 | — | P4.Position |
| ⑤ P5 群攻 | 签名之外（用了 `World`） | 未知：宽事件 |
| ⑥ P6 移动 | — | P6.Position |
| ⑦ Tick | 每个实体自己的 Health | 每个实体自己的 Health |

于是这样执行：

```
第一段 ①–④（⑤ 之前）：
  孤岛 A：② → ③       （③ 读 P2.Position，必须在 ② 之后：P3 打的是 P2 移动后的位置）
  孤岛 B：①
  孤岛 C：④            A、B、C 三个孤岛并行；孤岛内按到达顺序
⑤ 单独执行             （宽事件：前面全部做完，它独占）
第二段 ⑥ 单独执行
⑦ Tick：所有实体并行回血，SIMD
```

结果和按 ①②③④⑤⑥⑦ 一条条执行完全一样：
- ③ 看到的是 P2 移动之后的位置；
- ⑤ 看到的是 ①–④ 全部生效之后的世界。

**用户要知道的只有：**
- **所有效果都是立即生效的**，不需要判断。
- **并行是运行时自动做的**，不改变结果，只影响速度。
- **想让它更快**：把要访问的实体和组件写在参数上（像 `Hit`）；用 `World` 查询（像 `Aoe`）也是对的，只是这条事件会单独执行。
  - 分析器在 `Aoe` 上给一条提示："这个处理方法通过 World 访问实体，执行时会独占地图"。
  - 构建产物里有一张表，列出每个处理方法读写什么、是否独占，用来查哪条事件拖慢了地图。

**用户付出的代价只有一条纪律：用参数声明要访问的组件，少用 `World` 查询。**
- 用了也没错，只是这条事件会变成串行屏障；分析器会提示"这个处理方法是宽的"以及原因。
- 共用的逻辑（伤害公式、占格规则）写成静态函数，参数是组件，被多个处理方法调用。

**这还是 ECS 吗？是，只是调度方式变了：**
- 存储、查询、并行任务、SIMD，全部用 EasyEcs；
- 变的是"系统什么时候运行"：从"每帧按顺序跑一遍所有系统"，变成"按事件发生的顺序运行这个事件的处理方法"。tick 只是其中一种事件。
- 不需要新的存储架构；需要的是 DaServer 的事件调度器，加上 EasyEcs 的第 12、13 条（运行时读写集、对给定实体列表并行执行）。

**实现上的一个约束：** EasyEcs 的生成器看不到我们生成的代码，所以 `[On<Tick>]` 不能由我们生成一个 `[ForEach]` 交给 EasyEcs 去生成 job。两种做法：
1. 我们的生成器直接调用 EasyEcs 的运行时并行 API（`ForEachParallel` 等），自己生成 job 结构；
2. 或者 tick 的处理方法仍写成 EasyEcs 原生的 `[ForEach]`（放在 `[Every]` 组里），写法相同，只是特性名不同。

推荐 1：用户只看到一种特性 `[On<…>]`；分析器由我们负责检查（EasyEcs 的分析器不覆盖这些方法）。

### 两种方法

| 方法 | 写在哪 | 执行时机 | 能做什么 |
|---|---|---|---|
| `[Receive]`、`[Receive<K>]` | `static partial class` | 每条消息一次，按到达顺序 | 判定，写组件，增删实体，发消息，回复 |
| 顺序执行的 `[ForEach]` | `partial class … : EcsSystem` | 所在组每次运行时 | 同上；参数里拿 `Entity`、`World` |
| `[ForEach(Parallel = true)]`、批量方法 | 同上 | 同上，可以并行、可以 SIMD | 只读写组件。结构变更、写有索引的组件，EasyEcs 已用 EECS013、EECS015 禁止；我们另外禁止发消息 |

- 只含 `[Receive]` 的类不是系统，不继承 `EcsSystem`，不会被 EasyEcs 当成每次更新都跑的空系统。
- **`[Receive]` 属于哪个种类：**
  - 服务器之间的消息，从接口推出来；
  - 客户端消息写 `[Receive<Map>]`，这同时就是它的路由；
  - 生命周期消息和 `Answer`，从 `Self<K>` 参数推出来。
- **一个请求只能有一个处理方法**（分析器检查）。单向消息和生命周期消息可以有多个处理方法，按类名的顺序依次执行。
- **`Answer` 按 `(请求类型, [Pending] 的类型)` 分发**。所以同一种请求用不同的等待数据，可以有不同的处理方法；组合重复时报错。
- **返回值就是回复**；返回 `void` 就是单向消息。

### 边界：什么放 `[Receive]`，什么放 `[ForEach]`

**先换个角度看 tick：tick 就是一条发给本 shard 所有实体的消息 `Tick(dt)`。**
- shard 上发生的一切排成一条线：`m1, m2, Tick, m3, m4, Tick, …`。
- `[Receive]` 是某条消息的处理方法，一次处理一个实体；
- `[ForEach]` 是 `Tick` 的处理方法，一次批量处理所有匹配的实体。
- tick 本身也排在这条线上，所以 `[ForEach]` 不会打乱顺序。

**会打乱顺序的只有一种做法：把某条消息的效果挪到之后的 tick 里才做。**
- 例如移动消息只记下来、到 tick 才改位置，这个效果就被挪到了 m3、m4 之后，后到的消息先看到了结果。
- 所以边界不在"什么逻辑重"，而在"这个变化是谁引起的"。

**判断方法：对每一个状态变化，依次问三个问题。**

| 问题 | 回答 | 放在哪 |
|---|---|---|
| 1. 谁引起的？ | 某条消息 | 这条消息的 `[Receive]`，当场写 |
| | 时间流逝（每 tick、到了某个时刻） | `[ForEach]`（每 tick），或 `Due` 定时（到时刻） |
| 2. 同一个 shard 里，之后有没有判定要读它？ | 有：它是状态 | 在引起它的地方立刻写完，包括它的连锁后果（例如扣血到 0 就死亡） |
| | 没有：它只是输出（推送、广播、日志、指标、写记录） | 先追加到发件箱，由 tick 里的系统合并发送。追加是按发生顺序的，所以输出的顺序也不会乱 |
| 3. 只读写自己这个实体，并且不读同一趟里别人会写的数据？ | 是 | 可以 `Parallel`、批量、SIMD |
| | 否（会改别的实体，或者要增删实体） | 顺序执行的 `[ForEach]`，或者单线程的 `OnUpdate`。也可以先并行算出结果，存进不追踪的组件，再顺序提交 |

**常见情况：**

| 状态变化 | 谁引起的 | 别人会读吗 | 放在哪 |
|---|---|---|---|
| 玩家走了一步（步进式） | 消息 | 会：命中、碰撞、占格 | `[Receive]` |
| 寻路、NPC、投射物前进 | 时间 | 会 | tick：并行算方向，批量积分位置 |
| 攻击命中、扣血 | 消息 | 会 | `[Receive]` |
| 扣血导致死亡 | 消息的连锁后果 | 会：死人不能出手 | 同一个 `[Receive]`，调用共用的静态函数 `Combat.Damage` |
| 施放 buff | 消息 | 会 | `[Receive]` 当场建 buff 实体 |
| buff 每秒伤害 | 时间 | 会 | tick：并行算伤害，顺序提交死亡 |
| buff 到期、尸体 30 秒后消失 | 到了某个时刻 | 会 | `Due` 定时 |
| 技能冷却 | 消息（放了技能） | 会 | `[Receive]` 写 `ReadyAt = Now + 冷却`；判定时和 `Clock.Now` 比较，不需要任何系统每 tick 去减 |
| 回血、回蓝 | 时间 | 会 | tick，可并行 |
| 占格子（一个格子只能进一个人） | 消息 | 会 | `[Receive]`，有值索引的组件 |
| AOI 分格、广播 `Moved` | 由前面的变化导出 | 不会，只是输出 | tick：按格子合并，一帧发出 |
| NPC 的 AI 决策、出手 | 时间 | 会 | tick，顺序执行：同一时刻的 NPC 按固定顺序（实体 id）逐个当场判定、当场生效 |
| 检查点写记录 | 由修改导出 | 不会 | tick（`CheckpointSystem`） |

**两条推论：**
- **能用时间戳表示的，就不要每 tick 去改。**
  - 冷却、到期、复活时间存成"在某个时刻"，判定时直接和 `Clock.Now` 比较。
  - 数据一次写好，不需要系统，也就不存在顺序问题。
- **不要把命令排队到 tick 再执行**（例如"本 tick 收到的所有攻击，下个 tick 一起结算"）。
  - 这正是把消息的效果挪后，会打乱顺序。
  - 帧同步（lockstep）那类"命令下一帧统一执行"的游戏，是另一种模型，不在本框架的默认范围内。

**移动属于哪一类，取决于游戏的移动模型：**

| 移动模型 | 消息的含义 | `[Receive]` 当场做的 | tick 做的 |
|---|---|---|---|
| **步进式**（多数 MMO：客户端按方向键移动，持续上报位置） | `MoveTo(At, Velocity, ClientTime)`：我走到了这里 | 校验速度、碰撞、格子占用，当场写 `Position`、`Velocity`；不合法就拉回 | 不积分这个实体的位置，只做 AOI 广播 |
| **点击寻路式**（MOBA、RTS） | `MoveTo(Target)`：我要去那里 | 写 `Destination`，也就是新的计划，当场生效 | 按计划随时间推进位置，所有实体同一时刻前进 |

- 点击寻路时，移动消息改变的是计划，不是位置；位置是时间推进的结果，所有人在 tick 里同一时刻前进。所以同一个 tick 内的攻击看到的是同一份位置，这正是这种游戏的规则，不是顺序错误。
- 步进式时，移动消息本身就是位置的变化，所以必须当场写。
- 两种实体区分开：服务器驱动移动的实体带 `Steered` 组件，tick 只积分 `[With<Steered>]` 的实体，不会把客户端上报的位置再积分一遍。

**套用到例子（步进式）：**

| 到达 | `[Receive]` 当场做的 |
|---|---|
| 攻击 A | 用此刻的位置判定，命中就扣 B 的血，事件写进发件箱 |
| 移动 B | 校验后把 B 移到新位置，必要时换格子 |
| 移动 A | 校验后把 A 移到新位置 |
| 攻击 B | 先检查 B 还活着，再用 A、B 此刻的新位置判定 |
| （tick） | 只做 AOI：把这一 tick 的事件按格子合并广播 |

每一步看到的都是前面所有消息的结果，和到达顺序完全一致。

**为什么可以这样做：**
- 一张地图上所有玩家的消息都发给同一个地图实体，在同一个 shard 线程上逐条执行；
- 所以"地图上的到达顺序"就是唯一的顺序，当场写不会有竞争；
- 这就是地图要做成一个实例，并且由它拥有位置和格子的原因。

**延迟补偿**（按客户端看到的那一刻判定，可选）：
- 实体上放一个位置历史组件（环形缓冲），由 `[Receive]` 和 tick 写入；
- `Cast` 带上客户端的时间戳，`[Receive]` 按那个时刻的历史位置判定；
- 判定仍然在 `[Receive]` 里按到达顺序进行，只是读的是历史。

**解耦**：
- 每个 `[Receive]` 只处理一种消息，只读写它需要的组件。攻击不知道移动的存在，移动也不知道攻击的存在。
- 它们之间唯一的联系是组件数据（`Position`、`Health`），顺序由框架保证，不靠系统之间互相调用。
- 校验、占格这类多处用到的规则，写成读写组件的静态函数，由各个 `[Receive]` 调用，不复制代码。

### `[Receive]` 的参数

| 参数 | 绑定到 |
|---|---|
| 消息 `in T` | 这条消息 |
| `Self<K>` | 目标实体自己 |
| `Entity`、组件（`ref`/`in`） | **目标实体**（默认） |
| `[Link] Entity`、`[Link]` 组件 | 这个客户端在目标上的链接实体，例如这个玩家的 Avatar。只用于客户端消息和 `Unlinked` |
| `[Pending]` 组件 | 处理 `Answer` 时，等待实体上的数据 |
| `in ReplyTo<T>` | 要晚些回复时取；处理 `Answer` 时，取的是等待实体上存的令牌 |
| `From` | 发送方 |
| 单例 | 本 shard 的单例 |

**引用不会失效**：处理方法里做了结构变更（建实体、`Ask`、`Link` 等），参数仍然可用。
- 生成的跳板把 `in` 参数复制成局部变量。
- `ref` 参数传入局部变量，调用结束后用 `TryGetRef` 写回。组件如果已被处理方法删掉，就不写回，不会把它加回来。
- `DynamicBuffer` 参数直接按 `ref` 传入：它是指向存储的句柄，跳板只在调用前确保存储已经建好，之后不写回。
- 分析器检查：取了 `ref T` 的处理方法，不能再用别的方式写目标上的 `T`，否则写回会覆盖那次写入。

### 频率与 `Messages` 组

- **`[Every(50)]`** 写在系统组上，按真实时间每 50 毫秒调用一次 `Update<Simulation>()`。EasyEcs 自带的 `UpdateInterval` 按更新次数计算，不是时间。
- **每次唤醒**先跑框架的 `Messages` 组，再跑到了时间的 `[Every]` 组。
- **`Messages` 组里**依次是：
  1. `TimerSystem`：投递到期的邮件；
  2. `MailSystem`：按到达顺序逐条调用分发跳板，生命周期消息也在其中；
  3. `LifecycleSystem`：销毁已经回复的等待实体，以及已经写完记录的实例。
- **框架的系统自己处理异常**，不受 EasyEcs 的 `SystemErrorPolicy` 管理。
- **分发表和路由表**在加载时只暂存，各个 shard 在两次更新之间换上。表在暂存时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理消息时抛出异常

- `MailSystem` 逐条捕获异常，这条消息的请求以失败结束。
- 之后怎么办，看种类上的 `[OnError]`：
  - `Resume`（默认，与现在相同）：实体保持原样，接着处理下一条；
  - `Reload`：照正常流程卸载（第 6 节），之后从记录重新激活。
- **`Activated` 的处理方法抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次。
  - 这样不会留下初始化了一半的实体。
- `Client` 种类抛出异常时，断开这个客户端。

---

## 6. 生命周期

生命周期事件是框架发给实体自己的消息，用 `[Receive]` 处理，和其他邮件排在同一个顺序里。

| 消息 | 何时 | 常见用途 |
|---|---|---|
| `Activated(bool Restored, bool MovedIn)` | 实例激活后的第一条邮件 | 初始化 |
| `Unlinked` | 链接实体被断开时，由 `world.Unlink` 当场执行；`[Link]` 绑定到这个链接实体 | 广播离开 |
| `LinkLost(KindKey Target, From Client)` | 一条路由失效，发给当初建立它的实体（`Via`） | 重新建立链接 |
| `Unloading` | 卸载前的最后一条邮件 | 收尾 |

**占位**：一个键在"还没有实体"或"实体正在卸载"时，框架用一个占位接住发给它的邮件。

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达 | 占位接住邮件，异步读记录。这时还没有实体，`[ForEach]` 系统看不到它 |
| 激活 | 记录到了，或者没有记录 | 按 prefab 建实体，装上记录；邮箱里第一条是 `Activated`，后面才是占位接住的邮件 |
| 运行 | — | 邮件按到达顺序投递 |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或者调用 `world.Unload(e)` | 依次：<br>1. 决定卸载的那一刻起，新到的邮件由占位接住，所以 `Unloading` 之后不会再有别的邮件；<br>2. 对每个链接实体执行 `world.Unlink`，通知对应的 `Client`，只发不等；<br>3. 投递 `Unloading`；<br>4. 写记录；<br>5. 写成功后销毁实体。占位接住的邮件激活新实例，新实例读到的是刚写好的记录 |

- 写记录失败就重试，绝不丢弃。
- **激活的条件：**
  - 默认任何发给它的消息都能激活（Orleans 的虚拟 actor）；
  - 种类可以写 `[ActivatedBy<EnterMap>]` 收紧，其他消息遇到不存在的实体，返回 `NoActor`；
  - 框架的控制消息（`Answer`、`LinkConfirmed` 等）不会激活实体，找不到就丢弃；
  - `Client` 只在客户端连接时激活。

---

## 7. 客户端与链接

### `Client` 实体

- **激活**：客户端连上一个承载 `Client` 的节点后，框架用令牌里的 `ClientId` 激活 `Client(ClientId)`。如果这个节点上已经有同一个 `ClientId` 的 `Client`，先卸载旧的。
- **连接信息**：`(ClientId, 节点, 连接代数)`，框架内部使用。
  - 连接代数只用来判断是不是同一个连接：只比较相等，不比较大小。
- **声明**：令牌里的声明放进用户声明的 `[Claims]` 组件，例如 `PlayerClaims(long PlayerId)`。框架只负责解码令牌、把组件加到 `Client` 上，不认识任何业务字段。
- **位置固定**：`Client` 固定在连接所在的节点，不参与放置和重新平衡，也不迁移；连接断开，它就卸载。
- **扩展**：用 `[Spawns<Client>]` 的 prefab 给它加组件，例如令牌桶；在 Logic 里给它写处理方法。
- **找不到它**：别的实体不能用 `ClientId` 找到 `Client`。推送只经过链接，所以不需要位置目录。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Client` 实体。
2. 第一条链接建好之前，消息在 `Client` 上排队。链接建好后，框架推一条 `Ready` 给客户端。
3. 先过 `Client` 的过滤方法 `[Filter] static Verdict Allow(in Envelope e, ref RateLimit r)`。它逐条执行，返回放行或者拒绝的原因。
4. 按生成的路由表，得到目标种类。
5. 在路由表 `DynamicBuffer<Route>` 里找这个种类的链接：`IAddressed` 的消息用 `To` 选具体的一项，其他消息用最近建立的那一项。
6. 转发给目标。转发的消息带着 `LinkId`，目标据此找到链接实体，供 `[Link]` 参数绑定。
7. 被拒绝时，客户端收到原因：`NotLinked`、`Busy`、`NoActor`、`Filtered` 等。

### 链接

| 在哪 | 是什么 |
|---|---|
| 目标一侧 | 一个子实体，带 `Linked(LinkId, 连接信息)`，按 `LinkId` 建值索引。连接信息在建链接时就有：来自 `Attach` 或 `AskFor` 的信封 |
| `Client` 一侧 | 路由表里的一项：`Route(KindKey 目标, LinkId, KindKey Via)`。`Via` 是建立这条路由的实体，例如 Player；`Client` 自己建立的路由，`Via` 就是 `Client` 本身 |

**`LinkId`** 是 64 位：高 32 位是创建它的节点 id，低 32 位是该节点上的计数。
- 节点 id 在每个进程启动时随机生成，所以全集群唯一。
- 链接迁移后，`LinkId` 照用，只是一个值，不代表它现在在哪个节点。

**建立链接：**
1. 目标在处理请求时建链接实体，回复 `LinkId`。
2. 在 `Client` 上写路由，有两种情况：
   - **请求经别的实体转了一手**（例如 `TravelTo` → Player → 地图）：Player 处理 `Answer` 时调用 `world.Route(in answer)`。等待实体记着当初 `AskFor` 的客户端，所以框架知道路由该写给哪个 `Client`，`Via` 就是 Player。
     - 如果这次回答是在回复客户端的请求，就写成 `world.Reply(in reply, true, routeFrom: in answer)`。路由和回复一起到达 `Client`：先写路由，再把回复交给客户端。
   - **请求是 `Client` 自己发的**（例如 `Attach`）：`Client` 处理 `Answer` 时调用 `world.Route(in answer)`。
3. `Client` 写好路由后，先向目标发 `LinkConfirmed(linkId)`，之后才转发消息。两者同一来源、同一目标，所以确认一定先到。确认如果丢了，这条链接上第一条转发来的消息也算确认。
4. 目标收到确认后，给链接实体加 `Confirmed` tag。
   - 推送和 AOI 只认带 `Confirmed` 的链接实体；
   - AOI 第一次看到 `Confirmed` 时推一份完整的快照。迁移过来的链接实体（`MovedIn`）也按第一次看到处理，因为 `ClientGroup` 的订阅只在本节点。
5. **期限**：
   - `AskFor` 的期限取客户端请求剩下的时间；
   - 回答到达时，客户端的请求已经超时的，不写路由；
   - 目标的链接实体过了期限仍未确认，就删除。

**第一条链接**：
- `Client` 的 `Activated` 处理方法，根据声明向 Player 发 `Attach`，等待数据是 `Attempt(1)`。
- 失败（超时、`NoOwner`、`Busy`）时，用 `self.After(退避, …)` 重试，`Attempt` 加一，退避带随机抖动；超过 N 次就断开客户端。
  - 尝试次数是看得见的组件，不藏在框架里。
  - 一个游戏节点宕机时，十万个客户端不会同时重试。

**顶号**：
- Player 处理 `Attach` 时，最新到达的连接胜出。旧的链接实体被 `world.Unlink`，框架按它记下的连接信息让旧的 `Client` 断开。
- 旧连接之后如果还发来 `Attach`（半开连接在重试），Player 回复 `Superseded`，旧的 `Client` 收到后直接断开，不再重试。

**推送：**
- `world.Push(linkEntity, in msg)`：推给某个链接实体对应的客户端；
- `self.Push(in msg)`：推给自己所有已确认的链接；
- `ClientGroup` 广播（第 9 节）。
- 都按链接实体记下的节点直接发送。

### 断开

- **`world.Unlink(linkEntity)`** 当场完成断开，因为调用它时本来就在目标的处理过程中，顺序不会乱：
  1. 执行 `Unlinked` 的处理方法；
  2. 退出所有 `ClientGroup`；
  3. 销毁链接实体；
  4. 通知 `Client` 删掉这条路由，客户端收到一条框架通知。
- **外部触发的断开**（`Client` 断线、被踢、被顶号，或者节点离开视图）由框架作为邮件送到目标，再在目标的处理过程中调用 `world.Unlink`。
- **同一个键不会有两个链接实体**：`Enter` 发现旧的 Avatar 时调用 `world.Unlink`，旧的当场销毁，之后才建新的。
- **通知丢失时的兜底：**
  - 两个节点之间的链路重连时，目标一侧按网关列出自己持有的链接实体，网关指出其中哪些已经不认识，目标对这些执行断开；
  - 某个节点离开成员视图时，各节点对记在该节点上的链接实体执行断开；
  - 网络抖动、GC 停顿不会让链接失效，只有节点离开视图才会。
- **目标重启**（实例从记录恢复，旧的链接实体已经没有了）：
  - `Client` 在两种情况下删掉这条路由：转发时收到 `NotLinked`；或者看到目标所在的节点离开视图。即使客户端一直不发消息，也能发现。
  - 然后给 `Via` 发 `LinkLost(目标, 客户端)`。
  - 例如地图重启后，Player 收到 `LinkLost`，重新 `EnterMap`；Player 重启后，`Client` 给自己发 `LinkLost`，重新 `Attach`。
- 链接相关的控制消息不受 `Busy` 限制。

**换地图**：由业务决定先后，Player 先发 `LeaveMap`，再请求 `EnterMap`。框架不会自动断开旧链接。一个客户端可以同时链接同一种类的多个目标，例如多个聊天室。

---

## 8. 等待别的实体的结果

- **`self.Ask(to, in message[, in pending][, in reply])`、`self.AskFor(client, to, in message[, in pending][, in reply])`**：创建一个等待子实体，带：
  - 可选的用户数据：一个普通组件；
  - 调用 id：全集群唯一；
  - `Due` 期限；
  - 可选的 `ReplyTo<T>`；
  - `AskFor` 时，还带上客户端的连接信息。
  - 生成器为有没有 `pending`、有没有 `reply` 各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer` 邮件**：
  - 标了 `[Pending]` 的组件参数和 `in ReplyTo<T>` 绑定到等待实体，其余组件参数绑定到发问方；
  - 处理完，等待实体由框架删除。
- **超时**：框架去掉等待实体上的 `Due`，投递一个 `Ok = false` 的 `Answer`。
- **`ReplyTo<T>` 是带类型的清理组件**，由框架搬运，用户不能复制（分析器检查）：
  - `T` 就是请求声明的回复类型，`world.Reply` 在编译期检查类型；
  - `world.Reply` 先读出令牌，再移除组件，然后发出回复；
  - 等待实体在回复之前被销毁时，清理系统恰好执行一次，给调用方发一个失败回复；
  - 迁移时，旧的那一份在销毁前先移除 `ReplyTo`，不会误发；到了新节点，它作为普通组件装上，清理规则照常生效。
- 请求的处理方法必须返回回复，或者取 `ReplyTo<T>`（分析器检查）。
- **晚些回复**：`world.Hold(in reply, in 数据)` 建一个等待实体，之后对它调用 `world.Reply`。
- **一对多**：`self.AskAll(keys, in message)` 只建一个等待实体，回信累积在它的缓冲里。全部到齐或者超时后，投递一个 `Answers<TReq, TRep>`，里面的集合是池化的。
- 等待的数据和 `Answer` 的类型参数必须声明在 Server 工程，因为 Logic 热更时，它里面的类型会被移除。

---

## 9. 时间与广播

**时间：**
- `Due(long UnixMs)` 是绝对的 UTC 毫秒数，带有序值索引。迁移和恢复之后仍然正确，前提是各节点用 NTP 对时。
- `TimerSystem` 遍历 `Range(long.MinValue, Clock.Now)`：
  - 到期的 `After` 子实体：投递它的消息，然后销毁。EasyEcs 允许在遍历中销毁实体。
  - 到期的等待实体：遍历结束后再统一去掉 `Due`、投递超时，因为遍历时不能改索引的键。
- `self.After(delay, in message[, in pending])` 建一个带 `Due` 的子实体，到期时把消息作为邮件投给父实体。
- 周期性的逻辑用 `[Every]` 组，不用定时器。
- `Clock` 是框架给每个 shard 的单例：`Now` 是这次唤醒的时间，`DeltaTime` 是距离该组上次运行的秒数。

**广播：**
- **`ClientGroup`**：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出；
  - 成员是已确认的链接实体，`world.Unlink` 时自动退出。
- **小组**（公会、队伍）：一个固定键的实例，成员表是它的组件，由它转发。它取代现在的 `Publish`；大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热更、升级（EasyEcs 4.4）

### 记录

一份记录就是一次 `SaveEntities`，包括：
- 根实体，放在第一个；
- 带至少一个 `[Persist]` 组件的子孙；
- 这些子孙到根的路径上的实体。

保存用的组件集合在注册时建好：`persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`。

### 恢复

- 记录头里存着实体数 n。
- 按 prefab 建好根实体放在 `into[0]`，其余 n−1 个是空实体，然后一次 `LoadEntitiesInto(record, into)`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会改写成新的 id；指向这组以外的 `EntityId` 变成 `Null`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里。
- 加载完，`ChangeSystem` 把它的基准重置到这一刻。EasyEcs 会把加载的每一行都算作写入，不重置的话，每次激活都会白写一次。

### 只写改过的

- `[Persist]` 组件必须实现 `ITrackChanges`（分析器检查），因为 `[Changed<T>]`、`[Removed<T>]` 都依赖它。
- `ChangeSystem` 同时看 `[Changed<T>]` 和 `[Removed<T>]`：持久化组件被写或者被删，都给根实体标 `Unsaved`。
- 带持久化组件的子孙，在同一趟里加上框架的清理组件 `Persisted(EntityId Root)`。新建和 `LoadEntitiesInto` 都算写入，所以加载后它会自动补上。
- 子孙被销毁（例如物品用掉了）时，清理系统按 `Root` 给根实体标 `Unsaved`，然后移除自己。所以删除也会写进记录。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式。

### 迁移

- 保存整棵子树的所有组件，标了 `[Transient]` 的除外，默认都带。
- 托管类型的组件必须有 Nino 格式化器（分析器检查）。
- 等待中的请求、链接实体随实例一起迁移。
- 到达后的第一条邮件是 `Activated(MovedIn: true)`。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。

### 热更

- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有所有逻辑、分发跳板、分发表和路由表。
- 每个 shard 在两次 `Update` 之间：
  1. `world.RemoveTypes(旧上下文)`；
  2. 加入新的系统；
  3. 换上新的分发表和路由表。
- 所有 shard 都换完之后，旧上下文才卸载。
- 新版本如果改了某条客户端消息的目标种类，`wire.manifest` 比对后拒绝这次热更。
- 组件、消息、种类不热更，新增它们要走滚动升级。

### 滚动升级

- `wire.manifest` 记着每条消息的接收方、发送方、回复类型，以及客户端消息的路由。
- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点 id 在每个进程启动时随机生成，所以同名的节点重启后，不会被当成原来那个。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。实例在新节点从记录激活，最多丢一个检查点间隔的改动。`Client` 看到节点离开视图，给 `Via` 发 `LinkLost`，链接重新建立 |
| 网关宕机 | 客户端断开，带令牌重连，得到新的 `Client`。各目标看到那个节点离开视图，执行断开 |
| 半开连接时客户端已经连到别处 | 新连接的 `Attach` 胜出，旧的 `Client` 被断开；旧连接再发 `Attach` 会收到 `Superseded` |
| `Attach` 失败 | 带抖动退避重试，超过 N 次断开 |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活 |
| 发问方在回信之前被卸载 | `ReplyTo` 的清理立即给调用方发失败回复；之后到达的回信丢弃 |
| 卸载时写记录失败 | 重试；这期间到达的消息由占位接住，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；目标节点宕机时，在途的单向消息可能丢失 |
| 邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回绝；链接控制消息不受限 |
| 客户端读得太慢 | 断开（ADR-035） |
| 断开通知丢失 | 链路重连时核对，或者节点离开视图时，补做断开 |
| `[Receive]` 抛出异常 | 请求失败。默认 `Resume`；`Reload` 时断开所有链接后从记录重新激活；`Client` 直接断开 |
| `[Every]` 组里的系统抛出异常 | 按 `SystemErrorPolicy` 处理，并在健康检查里报告 |

---

## 12. 完整例子：连接、进地图、移动、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct TravelTo(long MapId)   : IFromClient<bool>;
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
[NinoType] public record struct NearbyEvent(long Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: a LinkId or an npc's id
```

```csharp
// ── Game.Server: kinds, servers, messages between servers ───────────────────
public readonly partial record struct Player(long Id) : IKind<Player>;
public readonly partial record struct Map(long Id)    : IKind<Map>;

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Client>] public struct GatewayServer : IServer;

[NinoType, From<Client>] public record struct Attach                  : IRequest<Player, LinkId>;
[NinoType, From<Player>] public record struct EnterMap(Player Player) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player) : IMessage<Map>;

// ── Game.Server: data ─────────────────────────────────────────────────────────
[Claims] public record struct PlayerClaims(long PlayerId) : IComponent;                // decoded from the token onto Client
public record struct Attempt(int N) : IComponent;                                       // waiting data: which try this is
public record struct OnMap(Map Map) : IComponent;                                       // on a player that is on a map
public record struct Travelling(Map To) : IComponent;                                   // waiting data
public record struct Rejoining : IComponent;                                            // waiting data
[NinoType, Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;   // on a map
public record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar> { public Avatar Key => this; }
public record struct Visible : IComponent;
public record struct Position(Vector2 Value) : IComponent;
public record struct Heading(Vector2 Value) : IComponent;                               // computed in parallel, untracked
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;
public record struct Destination(Vector2 Target) : IComponent;            // server-driven movers (npcs)
public record struct Steered : IComponent;                                // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Shout(NearbyEvent Event);                                          // a map's outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetBuffer<Shout>();
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetVelocity(default)
        .SetVisible();                                                     // a player's position comes from its steps
    public static readonly Prefab Npc = new Prefab().SetPosition(default).SetHeading(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(3).SetSteered().SetVisible();
}

[Every(50)] public struct Simulation : ISystemGroup;
```

```csharp
// ── Game.Server.Logic: messages ───────────────────────────────────────────────
public static partial class Connecting
{
    [Receive]                                                              // the client connected: link it to its player
    static void Connected(in Activated a, in Self<Client> self, in PlayerClaims claims) =>
        self.Ask(new Player(claims.PlayerId), new Attach(), new Attempt(1));

    [Receive]
    static void Attached(in Answer<Attach, LinkId> answer, [Pending] in Attempt attempt, in Self<Client> self, in PlayerClaims claims, World world)
    {
        if (answer.Ok) world.Route(in answer);                             // Via = the Client itself
        else if (answer.Status == RpcStatus.Superseded || attempt.N >= 5) self.Disconnect();
        else self.After(Backoff.Jittered(attempt.N), new Player(claims.PlayerId), new Attach(), new Attempt(attempt.N + 1));
    }

    [Receive]                                                              // the player restarted: attach again
    static void Lost(in LinkLost lost, in Self<Client> self, in PlayerClaims claims)
    {
        if (lost.Target.Is<Player>(out _)) self.Ask(new Player(claims.PlayerId), new Attach(), new Attempt(1));
    }
}

public static partial class Players
{
    [Receive]                                                              // newest connection wins; an old one's retry gets Superseded
    static LinkId Attach(in Attach m, in Self<Player> self) => self.LinkFrom(supersede: true);

    [Receive<Player>]                                                      // TravelTo goes to the player
    static void Travel(in TravelTo m, in Self<Player> self, in From from, in ReplyTo<bool> reply, Maybe<OnMap> onMap)
    {
        if (onMap.Exists) self.Send(onMap.Value.Map, new LeaveMap(self.Key));   // leave first: no double presence
        var map = new Map(m.MapId);
        self.AskFor(from, map, new EnterMap(self.Key), new Travelling(map), reply);
    }

    [Receive]                                                              // the map answered: route the client, then reply to it
    static void Entered(in Answer<EnterMap, LinkId> answer, [Pending] in Travelling trip, in ReplyTo<bool> reply, Entity player, World world)
    {
        if (answer.Ok) player.SetOnMap(trip.To);
        world.Reply(in reply, answer.Ok, routeFrom: in answer);           // the Client writes the route, then hands over the reply
    }

    [Receive]                                                              // the map restarted: enter it again for the same client
    static void Lost(in LinkLost lost, in Self<Player> self, Maybe<OnMap> onMap)
    {
        if (lost.Target.Is<Map>(out var map) && onMap.Exists && onMap.Value.Map == map)
            self.AskFor(lost.Client, map, new EnterMap(self.Key), new Rejoining());
    }

    [Receive]                                                              // re-entered: write the route; no client request to reply to
    static void Rejoined(in Answer<EnterMap, LinkId> answer, [Pending] in Rejoining r, World world)
    {
        if (answer.Ok) world.Route(in answer);
    }
}

public static partial class Maps
{
    [Receive]                                                              // EnterMap is IRequest<Map, …>
    static LinkId Enter(in EnterMap m, in Self<Map> map, World world)
    {
        var key = new Avatar(map.Key, m.Player);
        if (world.AvatarIndex.TryFind(key, out var old)) world.Unlink(old);   // gone at once: never two avatars per key
        return map.LinkFrom(world.Instantiate(Prefabs.Avatar).Set(key));  // the client from the envelope; sets ChildOf(map)
    }

    [Receive]
    static void Leave(in LeaveMap m, in Self<Map> map, World world)
    {
        if (world.AvatarIndex.TryFind(new Avatar(map.Key, m.Player), out var avatar)) world.Unlink(avatar);
    }

    [Receive]                                                              // run inside Unlink: [Link] is the avatar, the outbox the map's
    static void Gone(in Unlinked u, [Link] in Linked link, ref DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(new NearbyEvent(link.Id.Value, NearbyKind.Disappear, default, default)));

    [Receive<Map>]                                                         // a step: applied now, so later messages see it
    static void Move(in MoveTo m, [Link] Entity avatar, [Link] ref Position position, in Grid grid, World world)
    {
        if (!Movement.IsValidStep(in position, in m, in grid)) { world.Push(avatar, new SnapBack(position.Value)); return; }
        position.Value = m.At;
        avatar.SetVelocity(m.Velocity);                                    // tracked: AOI broadcasts it this tick
    }
}
```

```csharp
// ── Game.Server.Logic: per tick ───────────────────────────────────────────────
[UpdateInGroup<Simulation>]
public sealed partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true), With<Steered>]                              // only what time moves; into an untracked component
    static void Steer(in Position p, in Destination d, in Speed s, ref Heading h) => h.Value = Steering.Toward(p.Value, d.Target, s.Value);

    [ForEach, With<Steered>]                                               // sequential: Velocity changes only when it differs
    static void Commit(Entity e, in Heading h, in Velocity v) { if (h.Value != v.Value) e.SetVelocity(h.Value); }
}

[UpdateInGroup<Simulation>, UpdateAfter<SteeringSystem>]
public sealed partial class IntegrateSystem : EcsSystem
{
    [ForEach, With<Steered>]                                               // a batch over whole columns; Vectors is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), clock.DeltaTime);
}

[UpdateInGroup<Simulation>, UpdateAfter<IntegrateSystem>]
public sealed partial class AoiSystem : EcsSystem                         // single-threaded: cells, subscriptions, sends
{
    [ForEach]
    static void Map(Entity map, in Grid grid, ref DynamicBuffer<Shout> outbox, World world) => Aoi.Run(world, map, grid, ref outbox);
}
```

`Aoi.Run` 处理一张地图：
1. 遍历 `world.Children(map)`，每 tick 按位置把实体分进格子；格子不是组件，所以跨格不需要结构变更。
2. 只看带 `Confirmed` 的 Avatar。
3. 一个客户端跨了格，或者它的 Avatar 第一次带上 `Confirmed`（包括迁移来的）：
   - 更新它订阅的 3×3 `ClientGroup`；
   - 推一份它看得到的人的完整快照。
4. `Changed<Velocity>` 的实体写成 `Moved` 事件，和发件箱里已有的事件一起按格子合并。
5. 每个有内容的格子发一帧 `Nearby`，然后清空发件箱。

事件里的 `Id`：Avatar 用 `LinkId`，NPC 用一个不会与之相撞的编号（例如最高位置 1）。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: the kind's key, id and registration
partial record struct Map
{
    public Map Key => this;
    public const uint KindId = 0x5A1C_93E1;                               // NinoTypeId of Game.Server.Map
}

file static class MapKind
{
    [ModuleInitializer]
    internal static void Register() => Kinds.Register<Map>(Map.KindId,
        find: static (World w, in Map key, out Entity e) => w.MapIndex.TryFind(key, out e),   // EasyEcs's value index
        spawn: static (World w, in Map key) => w.SpawnKind(Prefabs.Map, key));                 // the only writer of Map
}
```

```csharp
// <auto-generated/> Game.Server: typed sends, only on the declared senders; one overload per shape, no default-argument inference
public static class PlayerSends
{
    public static void AskFor<TPending>(this in Self<Player> self, in From client, Map to, in EnterMap message, in TPending pending)
        where TPending : struct, IComponent =>
        self.Runtime.Ask(in to, EnterMap.MessageId, in message, in pending, reply: default, in client);

    public static void AskFor<TPending>(this in Self<Player> self, in From client, Map to, in EnterMap message, in TPending pending,
        in ReplyTo<bool> reply) where TPending : struct, IComponent =>
        self.Runtime.Ask(in to, EnterMap.MessageId, in message, in pending, in reply, in client);

    public static void Send(this in Self<Player> self, Map to, in LeaveMap message) =>
        self.Runtime.Send(in to, LeaveMap.MessageId, in message);
}
// There is no Self<Map>.AskFor(…, EnterMap): a map cannot send it, so such a call does not compile.
```

```csharp
// <auto-generated/> Game.Server.Logic: trampolines inside each partial class (they can reach private handlers)
partial class Maps
{
    internal static void Receive_Enter(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<EnterMap>();
        outcome.Reply(Enter(in m, Self<Map>.Of(target), world));
    }

    internal static void Receive_Gone(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var u = envelope.Read<Unlinked>();
        var linked = link.Read<Linked>();                                  // in: copied
        target.EnsureBuffer<Shout>();                                      // the store exists before the handle is used
        Gone(in u, in linked, ref target.Get<DynamicBuffer<Shout>>());     // a buffer is a handle: passed by ref, no write-back
    }

    internal static void Receive_Move(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<MoveTo>();
        var position = link.Read<Position>();                               // [Link] ref: a local in
        var grid = target.Read<Grid>();                                     // in, on the map: copied
        Move(in m, link, ref position, in grid, world);
        if (link.TryGetRef<Position>(out var slot)) slot = position;       // written back only if it still exists
    }
}
```

```csharp
// <auto-generated/> Game.Server.Logic: dispatch per kind and the client routes, staged on load, adopted between updates
file static class Dispatch_Map
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Map>(static table => table
        .Request(EnterMap.MessageId, Maps.Receive_Enter)
        .Message(LeaveMap.MessageId, Maps.Receive_Leave)
        .Message(Unlinked.MessageId, Maps.Receive_Gone)
        .Message(MoveTo.MessageId, Maps.Receive_Move));                   // built once when staged
}

file static class ClientRoutes
{
    [ModuleInitializer]
    internal static void Register() => Routes.Stage(static table => table
        .Route(TravelTo.MessageId, Player.KindId)                         // [Receive<Player>] Travel
        .Route(MoveTo.MessageId, Map.KindId));                            // [Receive<Map>] Move
}
```

```csharp
// <auto-generated/> AppHost, from Game.Server's wire.manifest
namespace Game.Server
{
    public readonly struct GameServer    : IServerType { public static string Name => "Game.Server.GameServer"; }
    public readonly struct MapServer     : IServerType { public static string Name => "Game.Server.MapServer"; }
    public readonly struct GatewayServer : IServerType, IAcceptsClients { public static string Name => "Game.Server.GatewayServer"; }
}
// IServerType declares `static abstract string Name { get; }`; AddNodes<T>() where T : IServerType returns NodeGroup<T>.
```

```text
# wire.manifest (excerpt, committed)
kind    Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer
kind    Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer
client  Game.Protocol.TravelTo  -> Player  reply bool
client  Game.Protocol.MoveTo    -> Map
server  Game.Server.EnterMap    Player -> Map     reply LinkId
server  Game.Server.LeaveMap    Player -> Map
server  Game.Server.Attach      Client -> Player  reply LinkId
push    Game.Protocol.Nearby    pooled
```

### 运行时的路径

```
① 客户端连 gate-0      框架在 gate-0 激活 Client(c1)，加上 PlayerClaims；第一条邮件是 Activated
② Activated           Client → Attach → Player 42@game-1；Player 建链接实体（连接信息来自信封），回复 LinkId L1
③ Answer              Client 写路由 (Player 42, L1, Via 自己)，发 LinkConfirmed(L1)；推 Ready，放出排队的消息
④ TravelTo(7)         Client(c1)@gate-0 → 路由 → Player 42@game-1
⑤ AskFor              Player 42 → Map 7@map-0：EnterMap；Map 7 建 Avatar（连接信息来自信封，LinkId L2），回复 L2
⑥ 回复客户端           world.Reply(…, routeFrom: answer) → Client(c1)：先写路由 (Map 7, L2, Via Player 42)，再交给客户端
⑦ LinkConfirmed(L2)   Client → Map 7：Avatar 带上 Confirmed；下一次 Simulation 推完整快照
⑧ MoveTo              Client(c1) → 路由 → Map 7：[Link] 绑定到这个 Avatar；校验这一步，当场改位置，后面的消息都看得到
⑨ 每 50 ms            Map 7 的 Simulation：转向、积分、AOI；Nearby → 有观察者的网关 → 各个连接
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：没有对应的方法。
- 客户端消息没有 `[Receive<K>]`，或者被两个种类接收。
- 一个请求有两个处理方法；`Answer` 的（请求类型, 等待数据类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`。
- 含 `[Receive]` 或 `[ForEach]` 的类不是 `partial`（可一键修复）。
- 用户代码写种类组件，或者把种类组件放进 prefab、`CreateEntity`。
- 取了 `ref T` 的处理方法，又用别的方式写目标上的 `T`。
- 在并行方法、批量方法里发消息。结构变更和写有索引的组件，EasyEcs 已经报 EECS013、EECS015。
- 复制 `ReplyTo<T>`。
- Protocol 里出现种类或者服务器之间的消息；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- 种类的键超过 16 字节，或者不是非托管类型。
- `[Persist]` 组件没有实现 `ITrackChanges`。
- 组件、等待数据、`Answer` 的类型参数声明在 Logic。
- 要迁移的托管组件没有 Nino 格式化器。

**删掉的东西**：
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调、`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded、自研的记录格式、`ActorKey`；
- 草案历次加了又去掉的：位置目录、定时租约摘要、迁移栅栏、`MapSlot`、`RouteLost`。

**版本记录**：

| 版本 | 主要变化 |
|---|---|
| v8 | 消息写明发送方和接收方；会话与链接；一个节点只跑一种服务器 |
| v9 | 地址统一；系统按组件查询；生命周期用 tag；记录改用 EasyEcs 4.4 |
| v10 | 链接随回复建立；参数绑定表；示例重写 |
| v11 | 客户端也是一个种类；Protocol 和 Server 分开；`LinkId` 由目标分配；第一条链接；记录一次保存、一次加载 |
| v12 | 种类就是它的地址，也是实体上的组件；Protocol 不出现种类，目标由 `[Receive<K>]` 决定；`[Hosts<Client>]`；`AddNodes<T>`；去掉位置目录和迁移栅栏；写入中占位；完整例子和生成的代码 |
| v13 | 生命周期改成发给自己的消息；`[Link]` 绑定；跳板复制参数；只有运行时能写种类组件；`Confirmed` 之后才推送；`Attach` 退避；`Via` 与 `LinkLost`；`[Claims]`；`ReplyTo<T>`；期限传递；删除也触发保存 |
| v14 | `world.Unlink` 当场完成，同一个键不会有两个链接实体；重新进图走 `world.Route(in answer)`；`Answer` 按（请求类型, 等待数据类型）分发；每种参数形态生成一个重载；`Activated` 抛异常算作激活失败；从决定卸载起，新邮件就由占位接住；加载中的实体在记录到达后才建；连接代数只比较是否相同，旧连接收到 `Superseded`；`KindKey`；`DynamicBuffer` 按 `ref` 直接传入，`ref` 参数用 `TryGetRef` 写回；去掉 `MapSlot`，AOI 按地图的子实体遍历；`RouteLost` 并进 `LinkLost`；只有 `[Receive]` 的类是 `static partial class`；用 `Maybe<OnMap>` 代替哨兵值；含集合的消息用 `[NinoPooled]`；加载后重置变更基准；`[Persist]` 必须实现 `ITrackChanges`；`LinkId` 的位布局 |

---

## 14. 待拍板

1. **生命周期事件是发给自己的消息**（`Activated`、`Unlinked`、`LinkLost`、`Unloading`），用 `[Receive]` 处理。
2. **组件参数默认绑定到目标，`[Link]` 绑定到客户端的链接实体。**
3. **推送只经过链接**：不提供"按 `ClientId` 给任意客户端发消息"。要给某个玩家推送，发给他的 Player，由 Player 来推。
4. **只含 `[Receive]` 的类写成 `static partial class`**，每 tick 的逻辑写成 `EcsSystem`。
5. **`Self<K>` 这个名字**，也可以叫 `Me<K>`。
