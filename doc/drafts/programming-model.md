# DaServer 新编程模型设计（草案 v12，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP；能用 EasyEcs 的功能就用，其余由我们的特性加生成器完成，不重复造轮子。第 0 节末尾的表列出了每个机制由谁负责。

目标：写起来最简洁、最直观、最 ECS；从类型就看得出每条消息从哪个实体发到哪个实体，以及它们在哪种服务器上；客户端只知道它收发的消息，不知道服务端怎么划分；高性能，高并发。

本稿是完整的一份。第 12 节是一个完整的例子，分别列出用户写的代码和生成的代码。第 13 节是版本记录，第 14 节是待拍板的点。

---

## 0. 一页看懂

### 三个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息，只有数据，不出现任何服务端的种类 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | 种类、服务器之间的消息、组件、prefab、服务器类型、系统组，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有系统（`[Receive]`、`[ForEach]`） | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

### 用户写的

| 写什么 | 写法 | 在哪个工程 |
|---|---|---|
| 客户端消息 | `record struct MoveTo(Vector2 Target) : IFromClient;` | Protocol |
| 推给客户端的消息 | `record struct Nearby(…) : IToClient;` | Protocol |
| 种类（同时是地址，也是实体上的组件） | `record struct Map(long Id) : IKind<Map>;` | Server |
| 服务器之间的消息 | `[From<Player>] record struct EnterMap(…) : IRequest<Map, LinkId>;` | Server |
| 组件 | `record struct Grid(…) : IComponent;`，需要持久化的再加 `[Persist]` | Server |
| prefab | `[Spawns<Map>] static readonly Prefab Map = …;` | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 系统组的频率 | `[Every(50)] struct Simulation : ISystemGroup;` | Server |
| 系统 | `EcsSystem` 里的 `[Receive]`、`[ForEach]` 方法 | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `Client` | 框架内置的种类：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Self<K>` | 正在处理的实体自己：强类型的键，以及发消息的入口 |
| `LinkId`、`Linked` | 客户端在某个目标上的链接，目标上对应一个链接实体（例如地图上的 Avatar） |
| `ReplyTo`、`Answer<TReq, TRep>`、`[Pending]` | 晚些回复；等别的实体的结果 |
| `Activated`、`Restored`、`MovedIn`、`Unloading`、`Unlinking`、`RouteLost` | 生命周期 tag |
| `IdleAfter`、`Due` | 空闲回收；定时 |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 现成的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 种类组件的值索引（`IIndexed`） | — | 收到消息时查索引 |
| 一类实体的系统 | `[With<Map>]` 过滤 | — | — |
| 每 tick 的逻辑 | `[ForEach]` 生成的 job、`Parallel`、批量方法、`[Changed<T>]` | — | — |
| 频率 | 系统组 | `[Every(ms)]` | shard 循环按时调用 `Update<T>()` |
| 消息处理 | — | `[Receive]` 生成的分发表 | `MailSystem` 按到达顺序调用 |
| 发送方向 | — | `[From<K>]` 生成 `Self<K>` 上的方法 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Receive<K>]` 生成路由表 | `Client` 实体转发 |
| 等待、回复 | `ChildOf`（随父销毁）、`ICleanupComponent`（恰好一次的失败回复） | `[Pending]` 参数绑定 | 调用 id、`Due` |
| 链接 | 子实体、值索引 | — | `LinkConfirmed`、摘要 |
| 生命周期 | tag、`[With<T>]` | 生成器排它们和 `MailSystem` 的先后 | 加、去 tag |
| 定时 | 有序值索引的 `Range` | — | `TimerSystem` |
| 广播的按地图遍历 | `IGroup`、`GroupBy`、`InGroup` | — | — |
| 只写改过的 | `ITrackChanges`、`ICleanupComponent` | — | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` 建集合 | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收上下文 | 分发表放在 Logic 程序集 | 两次更新之间切换 |
| 对象复用 | `DynamicBuffer` 池化 | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。** 一个实体同一时刻只处理一条消息，所以它的状态不会乱。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是两边都没有迁移。
   - 迁移期间只尽力保序，和 Orleans 一样：迁移那一两秒里，在途的消息可能后发先到。
   - 业务需要严格的先后关系时，用请求和回复串起来。
   - 发给不同目标的消息之间，没有顺序可言。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或超时。
4. **客户端重连就是一个新的客户端实体**：断线前没送达的推送会丢失，重连前后也没有顺序关系。
5. **每种失败的结果都是确定的**，见第 11 节。

---

## 2. 服务器与节点

```csharp
// Game.Server
[Hosts<Player>, Hosts<Guild>] public struct GameServer    : IServer;
[Hosts<Map>]                  public struct MapServer     : IServer;
[Hosts<Client>]               public struct GatewayServer : IServer;   // hosting Client = accepting clients
```

- **节点 = 一个进程 = 一种服务器。**
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程不跑多种服务器。
  - 本地开发由 Aspire 起多个进程；测试用 `TestCluster`。
  - 小规模部署定义一个 `[Hosts<Player>, Hosts<Map>, Hosts<Client>] AllInOne`。
- **`[Hosts<Client>]` 就是"接受客户端"。** 没有单独的 `IAcceptsClients` 标记。
- **种类绑定的是服务器类型，不是节点**：

| 关系 | 数量 | 由谁决定 |
|---|---|---|
| 种类 → 服务器类型 | 一般一对一，`AllInOne` 时一对多 | 服务器上的 `[Hosts<…>]` |
| 服务器类型 → 节点 | 一对多 | AppHost 部署几个 |
| 种类 → 实例 | 每个键一个 | 第一条消息到达时激活 |
| 实例 → 节点 | **任一时刻恰好一个** | 在承载该种类的节点中按键放置，节点增减时迁移 |

  同一个键任一时刻只有一个实例，这由现有的放置、记录版本和隔离机制保证，和 Orleans、ET 一样。`Client` 例外，它固定在连接所在的节点上（第 7 节）。
- **shard**：节点里的一个线程加一个 world。节点承载的所有种类共用 shard 的 world，实例落在哪个 shard 由键的哈希决定。
- **AppHost：**
  ```csharp
  var cluster = builder.AddDaServerCluster("game");
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientEndpoints();
  ```
  - AppHost 不引用服务端程序集（Aspire 的工程引用默认不引用程序集）。DaServer.Aspire 的生成器读取服务端工程提交的 `wire.manifest`，在 AppHost 里生成同名的服务器类型，所以能直接写 `AddNodes<GameServer>`。
  - 承载 `Client` 的服务器，生成的类型实现 `IAcceptsClients`。`WithClientEndpoints()` 的约束是 `where T : IAcceptsClients`，给别的服务器开客户端端口编译不过。
  - 接受客户端的服务器没开客户端端口，AppHost 构建时警告。
  - 消息引用的种类没有任何节点承载，AppHost 构建时报错。
  - 节点启动时，按配置里的服务器类型再校验一遍，写错就启动失败。

---

## 3. 种类与地址

**种类就是它的地址，也是实体上的组件：一个只装键的值类型。**

```csharp
// Game.Server
public readonly record struct Player(long Id) : IKind<Player>;
public readonly record struct Map(long Id)    : IKind<Map>;
public readonly record struct Room(long Id)   : IKind<Room>;
[Stateless] public readonly record struct Pricing(int Region) : IKind<Pricing>;   // any node, a local instance, never persisted
```

- `new Map(7)` 就是"7 号地图"这个实例的地址。消息、组件里存别的实例的地址，直接用 `Map`、`Player` 类型的字段。
- **同一个值也是组件**：地图实体上带着 `Map(7)` 这个组件。
  - 按键找实体，用 EasyEcs 为它生成的值索引：`world.MapIndex.Find(new Map(7))`；
  - 只处理地图的系统写 `[With<Map>]`；
  - `[ForEach]` 里取 `in Map self` 就知道自己是谁。
  - 框架不再另外定义 `ActorKey`。
- `IKind<T>` 继承 `IComponent` 和 `IIndexed<T>`，键就是组件自己。
  - **需要 EasyEcs 支持两点**（已列入需求清单第 10 条）：
    - 生成器认出从别的程序集的接口继承来的 `IComponent`、`IIndexed`；
    - 允许"组件自己就是键"，不必写 `Key` 属性。
  - EasyEcs 支持之前，用户写成 `: IKind<Map>, IComponent, IIndexed<Map> { public Map Key => this; }`，由我们的分析器一键补全。
- **键**：种类里的字段合计不超过 16 字节，并且是非托管类型，因为它要建值索引、要保存。字符串键用 64 位哈希代替，原文另存在组件里。
- 种类 id 是 Nino 类型 id，生成器写进 `wire.manifest`。
- **种类只在 Server 工程里**，客户端不知道服务端有哪些种类。
- **`[Stateless]` 种类**：不保存，不迁移，每个节点可以有自己的实例，键只用来选实例。用于令牌校验、计价这类无状态的工作。
- **子实体不可寻址**，例如地图上的 Avatar、等待中的请求。它们跟父实体一起保存、一起迁移。
- **没有"无键种类"**：
  - 每个节点或 shard 一份的状态，用单例；
  - 无状态的工作，用 `[Stateless]`；
  - 全局服务，用固定的键，例如 `Matchmaker(int Region)`。

---

## 4. 消息

### 客户端收发的消息（Protocol）

```csharp
// Game.Protocol: data only, no server kinds
[NinoType] public record struct TravelTo(long MapId)          : IFromClient<bool>;   // a request: the client waits for a bool
[NinoType] public record struct MoveTo(Vector2 Target)        : IFromClient;
[NinoType] public record struct Cast(int Skill, long Target)  : IFromClient;
[NinoType] public record struct Say(long To, string Text)     : IFromClient, IAddressed;   // To picks one of several rooms
[NinoType] public record struct Nearby(NearbyEvent[] Events)  : IToClient;
```

- `IFromClient` 是客户端发出的单向消息，`IFromClient<TReply>` 是客户端发出的请求，`IToClient` 是推给客户端的消息。
- 消息里不写目标是哪个种类，所以客户端不知道服务端有 Player、Map、Room。
- **目标由服务端决定**：处理这条消息的 `[Receive<Map>]` 写在哪个种类上，消息就转发给哪个种类（第 5 节）。生成器据此生成客户端消息的路由表。一条客户端消息只能有一个目标种类。
- `IAddressed` 的消息带一个 `To` 字段，用来在同种类的多个链接里选一个，例如选哪个聊天室。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Player>] public record struct EnterMap(Player Player, ClientId Client) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player)                  : IMessage<Map>;
[NinoType, From<Client>] public record struct Attach(ClientId Client)                  : IRequest<Player, LinkId>;
```

- 接收方写在接口上：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。
- 发送方写在特性上：`[From<K>]`，可以写多个，例如 `[From<Map>, From<Npc>]`。
- `[From<Client>]` 指服务端的 `Client` 实体（例如它激活时发 `Attach`），不是远端的客户端。远端的客户端只能发 `IFromClient` 消息。
- **方向由生成的方法检查**：
  - 每条消息只在它的发送方的 `Self<K>` 上生成发送方法。例如 `self.Ask(map, in enterMap, in pending, in reply)` 只存在于 `Self<Player>` 上，方向写错就没有这个方法，编译不过。
  - `Self` 由框架传入，用户造不出来，所以也冒充不了别的发送方。
- **`ICluster`**（HTTP、后台、测试）不属于任何种类，可以发任何服务器之间的消息，回复回到调用方。踢人用 `cluster.Disconnect(clientId)`。

---

## 5. 系统

### 两种方法

| 方法 | 执行时机 | 能做什么 |
|---|---|---|
| `[Receive]`、`[Receive<K>]` | 每条消息一次，按到达顺序 | 判定，写组件，增删实体，发消息，回复 |
| 顺序执行的 `[ForEach]` | 每次所在组运行 | 同上；参数里拿 `Entity`、`World` |
| `[ForEach(Parallel = true)]`、批量（`Span`）方法 | 同上，可以并行、可以 SIMD | 只读写组件；不发消息，不做结构变更，不写有索引的组件 |

- **`[Receive]` 属于哪个种类**：
  - 服务器之间的消息，从接口推出来：`EnterMap : IRequest<Map, …>` 的处理方法就属于 `Map`；
  - 客户端消息没写目标，必须写成 `[Receive<Map>]`。这同时就是这条消息的路由。
- **系统按组件查询，不按种类划分。** 一个移动系统可以同时处理 Avatar 和 NPC。
- **返回值就是回复**；返回 `void` 就是单向消息。
- **`[ForEach]` 里发消息**：取种类组件 `in Client self`，再用 `world.Self(in self)` 得到 `Self<Client>`。
  - `World.Self` 只接受本 shard 上真实存在的实体的种类组件（按值索引核对），所以同样冒充不了别的发送方。
  - `Self<K>` 不是组件，EasyEcs 的生成器不认识它，所以 `[ForEach]` 不能直接拿它做参数。

### `[Receive]` 的参数

| 参数 | 绑定到 |
|---|---|
| 消息 `in T` | 这条消息 |
| `Self<K>` | 目标实体自己 |
| `Entity` 和组件（`ref`/`in`） | 接收实体：<br>- 客户端消息：该客户端在目标上的链接实体（例如这个玩家的 Avatar）<br>- 其他消息：目标实体自己 |
| `[Pending] in T` | 处理 `Answer` 时，等待实体上的组件（第 8 节） |
| `in ReplyTo` | 要晚些回复时取。处理 `Answer` 时，绑定到等待实体上保存的令牌 |
| `From` | 发送方：`From.Client` 是 `ClientId`，`From.Key` 是实体的地址 |
| 单例 | 本 shard 的单例 |

### 频率与 `Messages` 组

- **`[Every(50)]`** 写在系统组上，表示每 50 毫秒调用一次 `Update<Simulation>()`。
  - 它按真实时间计算，建立在 EasyEcs 之上。EasyEcs 的 `UpdateInterval` 按更新次数计算，不是时间。
- **`Messages` 组**是框架的，每次唤醒都跑，里面的顺序是：
  1. `TimerSystem`：投递到期的邮件。
  2. 对 `Activated`、`Restored`、`MovedIn`、`RouteLost` 做反应的用户系统。
  3. `MailSystem`：按到达顺序逐条调用生成的分发表。
  4. 对 `Unlinking`、`Unloading` 做反应的用户系统。
  5. `LifecycleSystem`：去掉第 4 步开始时就有的那些生命周期 tag，销毁该销毁的实体。第 4 步里新加的 tag 留到下一次更新。
- **生命周期系统的位置由生成器排**：系统的 `With<…>` 里有哪个 tag，生成器就给它加对应的 `UpdateBefore<MailSystem>` 或 `UpdateAfter<MailSystem>`。
  - 一个系统同时对第 2 步和第 4 步的 tag 做反应，分析器报错。
  - 对生命周期 tag 做反应的系统，必须在 `Messages` 组里，因为 tag 只存在一次 `Messages` 更新。
- **框架的系统不受 EasyEcs 的 `SystemErrorPolicy` 管理**，它们自己处理异常。不然用户把策略设成 `Disable` 时，`TimerSystem`、`MailSystem` 也可能被停掉，所有等待都永远不会结束。

### 处理消息时抛出异常

- `MailSystem` 逐条捕获异常，这条消息的请求以失败结束。
- 之后怎么办，由种类上的 `[OnError]` 决定，和现在的 `ErrorAction` 相同：
  - `Resume`（默认）：实体保持原样，接着处理下一条消息；
  - `Reload`：实体照正常流程卸载（第 6 节），即先断开它所有的链接，再从最近的记录重新激活。
- `Client` 种类抛异常时，断开这个客户端。
- 每个处理方法的耗时，以及所属系统的 `Enabled`，都由 `MailSystem` 记录和检查。

---

## 6. 生命周期

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达，实体还不存在 | 按 prefab 建实体，加 `Loading`，异步读记录；邮件留在邮箱里 |
| 激活 | 记录到了，或者没有记录 | 装上记录，加 `Activated`；从记录恢复的再加 `Restored`，迁移来的再加 `MovedIn`。生命周期系统在同一次更新里先于 `MailSystem` 运行 |
| 运行 | — | 邮件按到达顺序投递 |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或调用 `world.Unload(e)` | 1. 给自己的链接实体加 `Unlinking`，通知各个 `Client`，只发不等<br>2. 加 `Unloading`，运行对应的用户系统<br>3. 开始写记录，实体进入"写入中"<br>4. 写成功后销毁实体 |

**写入中**（现在的 `Saving`/`FinalSave`，保留）：
- 这期间到达该键的消息，在这个占位里排队；
- 写成功后，排队的消息照常激活新的实例，新实例读到的是刚写好的记录；
- 写失败就重试，绝不丢弃；
- 只有确认写入的状态，才会被新实例读到。

**激活的条件：**
- 默认任何发给它的消息都能激活实体（Orleans 的虚拟 actor）。
- 种类可以写 `[ActivatedBy<EnterMap>]` 收紧。其他消息遇到未激活的实体，返回 `NoActor`。
- 框架的控制消息（`Answer`、`LinkConfirmed` 等）不会激活实体，找不到就丢弃。
- `Client` 永远不会被消息激活，只在客户端连接时激活。

---

## 7. 客户端与链接

### `Client` 实体

- 客户端连上一个承载 `Client` 的节点后，框架用令牌里的 `ClientId` 在这个节点上激活 `Client(ClientId)` 实体，并给它一个连接代数（epoch）。
- `Client` 固定在连接所在的节点上：
  - 不参与放置和重新平衡，也不迁移；
  - 连接断开，它就卸载。
- 用户可以给它加组件和系统：
  - 用 `[Spawns<Client>]` 的 prefab 加组件，例如限流用的令牌桶；
  - 在 Logic 里给它写系统，例如激活时的 `Attach`。
- 别的实体不能用 `ClientId` 直接找到 `Client`。推送只经过链接（见下面），所以不需要位置目录。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Client` 实体。
2. 先过 `Client` 的过滤方法：`[Filter] static Verdict Allow(in Envelope e, ref RateLimit r)`。它逐条执行，返回放行或拒绝的原因。
3. 按生成的路由表，得到这条消息的目标种类（来自 `[Receive<Map>]`）。
4. 在 `Client` 的路由表 `DynamicBuffer<Route>` 里找该种类的链接：
   - `IAddressed` 的消息，用 `To` 选具体的一项；
   - 其他消息，用该种类最近建立的那一项。
5. 转发给目标。转发的消息带着 `LinkId`，目标据此找到链接实体，`[Receive]` 的组件参数就绑定到这个实体上。
6. 被拒绝时，客户端收到原因：`NotLinked`、`Busy`、`NoActor`、`Filtered` 等。

### 链接

- **目标一侧**：一个子实体，带 `Linked(ClientId, LinkId)`，按 `LinkId` 建值索引。确认之后，它还记下 `Client` 所在的节点和连接代数，推送就靠这两项。
- **`Client` 一侧**：路由表里的一项 `Route(种类 id, 键, LinkId)`。
- `LinkId` 由目标在创建链接实体时分配，全集群唯一：节点 id（每个进程启动时随机生成）加计数。

**建立链接：**
1. 目标处理请求时创建链接实体，回复 `LinkId`。例如地图在 `EnterMap` 里建好 Avatar。
2. 路由写在 `Client` 上，有两种情况：
   - 客户端发起的请求，由别的实体回复它。例如 Player 处理 `TravelTo` 时去问地图，拿到 `LinkId` 后回复 `Reply.Linking(true, map, linkId)`。这个回复先到 `Client`，`Client` 写好路由，再把结果交给客户端。
   - `Client` 自己发起的请求。例如它激活时向 Player 发 `Attach`，收到 `Answer` 后调用 `self.Route(player, linkId)`。
3. `Client` 写好路由后，先给目标发 `LinkConfirmed(linkId, 节点, 连接代数)`，之后才转发这条链接上的消息。两者都从同一个 `Client` 发往同一个目标，所以确认一定先到。
4. 如果 `Reply.Linking` 到达时，客户端的这次请求已经超时，`Client` 不写路由。目标那边的链接实体等不到确认，到期删除。

**第一条链接**：`Client` 激活时，它的生命周期系统根据令牌里的声明（例如玩家 id）向 Player 发 `Attach`，拿到 `LinkId` 后写好路由。从这时起，客户端才能和 Player 通信。

**顶号**：Player 处理 `Attach` 时，如果已经有一个旧连接代数的链接实体，就给它加 `Unlinking`，并让旧的 `Client` 断开。

**推送：**
- `world.Push(linkEntity, in msg)`：推给某个链接实体对应的客户端，例如地图推给一个 Avatar 的玩家。
- `self.Push(in msg)`：推给自己所有链接对应的客户端，例如 Player 推给自己的客户端。
- 推送按链接实体里记下的节点直接发过去，不需要查目录。
- `ClientGroup`：广播（第 9 节）。

### 断开

- **目标断开**：收到 `LeaveMap`，或者自己被卸载。目标给链接实体加 `Unlinking`，并通知 `Client` 删掉路由，客户端会收到一条框架通知。
- **`Client` 断开**：客户端断线、被踢、被顶号。`Client` 按路由表通知每个目标，目标给对应的链接实体加 `Unlinking`。
- **用户的清理**：在 `Messages` 组里写 `[ForEach, With<Unlinking>]` 系统，例如广播 `Disappear`。
- **通知丢失时的兜底**：
  - 两个节点之间的链路断开再连上时，双方交换一次摘要，列出指向对方的有效链接。对方不认的，加 `Unlinking`。
  - 某个节点离开成员视图时，各节点给"记录在该节点的链接实体"加 `Unlinking`。
  - 摘要只看已确认的链接。网络抖动、GC 停顿不会让链接过期，只有节点离开视图才会。
- **未确认的链接**：建立后在两倍请求超时之内没收到 `LinkConfirmed`，就删除。
- **目标重启**：目标节点宕机后，实例从记录恢复，旧的链接实体没有了。
  - `Client` 再转发时收到 `NotLinked`，或者看到目标节点离开视图，就删掉这条路由，加 `RouteLost(种类, 键)` tag。
  - `Client` 的生命周期系统据此重新发 `Attach`，客户端不会一直卡在 `NotLinked`。
- 链接相关的控制消息不受 `Busy` 限制。

**换地图**由业务决定先后：Player 先发 `LeaveMap` 给旧地图，再向新地图请求 `EnterMap`。框架不会因为建了新链接就自动断开旧的。一个客户端可以同时链接同一种类的多个目标，例如多个聊天室。

---

## 8. 等待别的实体的结果

- **`self.Ask(to, in message, in pending, in reply)`** 创建一个等待子实体，上面有：
  - 用户的数据：一个普通组件，例如 `Travelling`；
  - 调用 id：全集群唯一；
  - `Due`：期限；
  - 可选的 `ReplyTo`。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后回信会跟过去。
- **回信是发给发问方的 `Answer` 邮件**，和其他消息一样按到达顺序处理：
  - 标了 `[Pending]` 的组件参数和 `in ReplyTo` 绑定到等待实体；
  - 组件参数仍然绑定到发问方；
  - 处理完，框架删除等待实体。
- **超时**：期限到时，框架去掉等待实体上的 `Due`，投递一个 `Ok = false` 的 `Answer`，处理方式同上。
- **`ReplyTo` 是清理组件**，由框架搬运，用户不能复制，分析器检查：
  - `world.Reply(in reply, 值)` 先读出令牌，再移除组件，然后发出回复；
  - 等待实体在回复之前被销毁（例如发问方被卸载）时，清理系统恰好执行一次，给调用方发一个失败回复；
  - 迁移时，旧的那一份在销毁前先移除 `ReplyTo`，所以不会误发失败回复。
- **晚些回复**：`world.Hold(in reply, in 数据)` 建一个等待实体，之后对它调用 `world.Reply`。
- **一对多**：`self.AskAll(keys, in message, in pending)` 只建一个等待实体，回信累积在它的缓冲里。全部到齐或超时后，投递一个 `Answers<TReq, TRep>`，里面的集合是池化的。
- 等待的数据是组件，必须声明在 Server 工程；`Answer` 的类型参数同样必须在 Server 工程，因为 Logic 热更时会被移除。

---

## 9. 时间与广播

**时间：**
- `Due(long UnixMs)` 是绝对的 UTC 毫秒数，带有序值索引。迁移和从记录恢复之后仍然正确，前提是各节点用 NTP 对时。
- `TimerSystem` 先取出 `Range(long.MinValue, 现在)` 的实体，再逐个去掉 `Due`、投递到期邮件。先取出再修改，是因为遍历有序索引时不能改它的键。
- `self.After(delay, in message)` 建一个带 `Due` 的子实体，到期时把消息作为邮件投给父实体。
- 周期性的逻辑用 `[Every]` 的组，不用定时器。

**广播：**
- **`ClientGroup`**：
  - 消息只序列化一次，每个有成员的网关节点收到一帧，由网关扇出给各个连接。
  - 成员关系记在链接实体上；链接实体进入 `Unlinking` 时，自动退出所有组。
- **小组**（公会、队伍）：一个固定键的实例，成员表是它的组件，由它转发。取代现在的 `Publish`。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热更、升级（EasyEcs 4.4）

**记录**：一次 `SaveEntities`，保存三类实体：
- 根实体，放在第一个；
- 带至少一个 `[Persist]` 组件的子孙；
- 这些子孙到根的路径上的实体。

**组件集合**：注册时建好 `persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`。

**恢复：**
- 记录头里存着实体数 n。
- `into[0]` 是按 prefab 建好的根实体，其余是 n−1 个空实体；然后一次 `LoadEntitiesInto(record, into)`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会被改写成新的 id。指向组外的 `EntityId` 会变成 `Null`，例如指向另一张地图上实体的 `BuffOf`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里。

**只写改过的：**
- `[Persist]` 组件实现 `ITrackChanges`，现有的 `ChangeSystem` 给它的根实体标 `Unsaved`。
- 带 `[Persist]` 组件的子孙还有一个框架的清理组件 `Persisted`。子孙被销毁（例如物品用掉了）时，清理系统给根实体标 `Unsaved`，再移除这个组件。否则这次销毁不会被写进记录，重新加载后物品又回来了。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式。

**迁移：**
- 保存整棵子树的所有组件，标了 `[Transient]` 的除外。是"默认都带，标注才不带"，所以 Avatar 的位置、速度、发件箱这些运行时组件都会跟过去。
- 等待中的请求、链接实体也随实例迁移。
- 到达后，实例带着 `Activated` 和 `MovedIn`。生命周期系统据此重新分配 shard 内的编号，例如 `MapSlot`。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间只尽力保序（第 1 节），没有迁移栅栏。

**热更：**
- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有系统和生成的分发表。
- 每个 shard 在两次 `Update` 之间：
  1. `world.RemoveTypes(旧上下文)`；
  2. 加入新系统；
  3. 换上新的分发表。
- 所有 shard 都换完，旧上下文才卸载。
- 组件、消息、种类都在 Server 和 Protocol，不热更；新增它们要走滚动升级。

**滚动升级：**
- `wire.manifest` 记着每条消息的接收方、发送方、回复类型，以及客户端消息的路由。
- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，各节点算出的放置一致。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或收到 `NoOwner`。实例在新节点从最近的记录激活，最多丢一个检查点间隔的改动。客户端的路由收到 `RouteLost`，重新 `Attach` |
| 网关宕机 | 客户端断开，带令牌重连，得到一个新的 `Client`。各目标看到那个节点离开视图，清掉相关的链接实体 |
| 半开连接，客户端已经连到了别的网关 | 新 `Client` 的 `Attach` 顶掉旧链接，旧 `Client` 被断开 |
| 发问方在回信之前被卸载 | `ReplyTo` 的清理系统立即给调用方发失败回复。之后到达的回信找不到调用 id，丢弃 |
| 卸载时写记录失败 | 重试；这期间到达的消息排队，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；目标节点宕机时，在途的单向消息可能丢失 |
| 目标的邮箱满了 | 发送方收到 `Busy`；客户端的消息由网关回绝 |
| 客户端读得太慢 | 断开（ADR-035） |
| 断开通知丢失 | 链路重连时的摘要，或者节点离开视图，会清掉多余的链接实体 |
| `[Receive]` 抛出异常 | 请求失败。默认 `Resume`，实体继续运行；`Reload` 则断开链接后从记录重新激活；`Client` 断开连接 |
| `[Every]` 组里的用户系统抛出异常 | 按 `SystemErrorPolicy` 处理，并在健康检查里报告 |

---

## 12. 完整例子：玩家进地图、移动、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct TravelTo(long MapId)         : IFromClient<bool>;
[NinoType] public record struct MoveTo(Vector2 Target)       : IFromClient;
[NinoType] public record struct Nearby(NearbyEvent[] Events) : IToClient;
[NinoType] public record struct NearbyEvent(long Who, byte Kind, Vector2 At, Vector2 Velocity);
```

```csharp
// ── Game.Server: kinds, servers, messages between servers ───────────────────
public readonly record struct Player(long Id) : IKind<Player>;   // also the player entity's component, indexed
public readonly record struct Map(long Id)    : IKind<Map>;

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Client>] public struct GatewayServer : IServer;

[NinoType, From<Client>] public record struct Attach(ClientId Client)                  : IRequest<Player, LinkId>;
[NinoType, From<Player>] public record struct EnterMap(Player Player, ClientId Client) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player)                  : IMessage<Map>;

// ── Game.Server: data ─────────────────────────────────────────────────────────
[NinoType, Persist] public record struct Bank(long Gold) : IComponent, ITrackChanges;          // on a player
public record struct Travelling(Map To) : IComponent;                                           // waiting data
public record struct Attaching : IComponent;
public record struct CurrentMap(Map Map) : IComponent;
[NinoType, Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;   // on a map
public record struct MapSlot(int Value) : IComponent, IGroup { public int Group => Value; }    // shard-local number
public record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar> { public Avatar Key => this; }
public record struct Visible : IComponent;
public record struct Position(Vector2 Value) : IComponent;
public record struct Heading(Vector2 Value) : IComponent;                // computed in parallel, untracked
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;
public record struct Destination(Vector2 Target) : IComponent;
public record struct Speed(float Value) : IComponent;
public record struct Shout(NearbyEvent Event);                           // an outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetBuffer<Shout>();
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetHeading(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(5).SetVisible();
}

[Every(50)] public struct Simulation : ISystemGroup;
```

```csharp
// ── Game.Server.Logic: behaviour ─────────────────────────────────────────────
public sealed partial class AttachSystem : EcsSystem                     // the client's first link: to its player
{
    [ForEach, With<Activated>]                                             // in Messages, before MailSystem
    static void OnConnect(in Client self, in Claims claims, World world) =>
        world.Self(in self).Ask(new Player(claims.PlayerId), new Attach(self.Id), new Attaching());

    [ForEach, With<RouteLost>]                                            // the player restarted: attach again
    static void Reattach(in Client self, in Claims claims, World world) =>
        world.Self(in self).Ask(new Player(claims.PlayerId), new Attach(self.Id), new Attaching());

    [Receive]
    static void Attached(in Answer<Attach, LinkId> answer, in Self<Client> self) { if (answer.Ok) self.Route(answer.From, answer.Value); }
}

public sealed partial class PlayerLinkSystem : EcsSystem
{
    [Receive]                                                              // a new connection supersedes the old one
    static LinkId Attach(in Attach m, in Self<Player> self, World world) => self.Link(world.CreateEntity(), m.Client, supersede: true);
}

public sealed partial class TravelSystem : EcsSystem
{
    [Receive<Player>]                                                      // TravelTo goes to the player
    static void Travel(in TravelTo m, in Self<Player> self, in From from, in ReplyTo reply, in CurrentMap current)
    {
        if (current.Map.Id != 0) self.Send(current.Map, new LeaveMap(self.Key));   // leave first: no double presence
        self.Ask(new Map(m.MapId), new EnterMap(self.Key, from.Client), new Travelling(new Map(m.MapId)), reply);
    }

    [Receive]                                                              // the map answered: link the client, reply
    static void Entered(in Answer<EnterMap, LinkId> answer, [Pending] in Travelling trip, in ReplyTo reply, ref CurrentMap current, World world)
    {
        if (answer.Ok) current.Map = trip.To;
        world.Reply(in reply, answer.Ok ? Reply.Linking(true, trip.To, answer.Value) : Reply.Of(false));
    }
}

public sealed partial class EntrySystem : EcsSystem
{
    [Receive]                                                              // EnterMap is IRequest<Map, …>: belongs to Map
    static LinkId Enter(in EnterMap m, in Self<Map> map, in MapSlot slot, World world)
    {
        var key = new Avatar(map.Key, m.Player);
        if (world.AvatarIndex.TryFind(key, out var old)) world.Unlink(old);   // a reconnect replaces the old avatar
        return map.Link(world.Instantiate(Prefabs.Avatar).Set(key, slot), m.Client);   // sets Linked and ChildOf(map)
    }

    [Receive]
    static void Leave(in LeaveMap m, in Self<Map> map, World world)
    {
        if (world.AvatarIndex.TryFind(new Avatar(map.Key, m.Player), out var avatar)) world.Unlink(avatar);
    }
}

public sealed partial class MovementSystem : EcsSystem
{
    [Receive<Map>]                                                         // MoveTo goes to the map; bound to the sender's avatar
    static void Move(in MoveTo m, ref Destination destination) => destination.Target = m.Target;
}

[UpdateInGroup<Simulation>]
public sealed partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true)]                                             // into an untracked component: no change stamps
    static void Steer(in Position p, in Destination d, in Speed s, ref Heading h) => h.Value = Steering.Toward(p.Value, d.Target, s.Value);

    [ForEach]                                                              // sequential: Velocity changes only when it differs
    static void Commit(Entity e, in Heading h, in Velocity v) { if (h.Value != v.Value) e.SetVelocity(h.Value); }
}

[UpdateInGroup<Simulation>, UpdateAfter<SteeringSystem>]
public sealed partial class IntegrateSystem : EcsSystem
{
    [ForEach]                                                              // a batch over whole columns; Vectors is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), clock.DeltaTime);
}

[UpdateInGroup<Simulation>, UpdateAfter<IntegrateSystem>]
public sealed partial class AoiSystem : EcsSystem                         // single-threaded: cells, subscriptions, sends
{
    public override void OnUpdate(World world) => Aoi.Run(world);        // per MapSlot group: Changed<Velocity> -> map outbox -> one frame per busy cell
}

public sealed partial class LeaveSystem : EcsSystem                       // in Messages, after MailSystem (generated order)
{
    [ForEach, With<Unlinking>, With<Visible>]                            // into the map's outbox: the avatar is destroyed this update
    static void Gone(in Linked link, in ChildOf parent, World world) =>
        world.Entity(parent.Parent).WriteShout().Add(new Shout(new NearbyEvent(link.Id.Value, NearbyKind.Disappear, default, default)));
}
```

`Aoi.Run` 做的事：
- 按 `MapSlot` 分组遍历；
- 每 tick 按位置把实体分进格子（格子不是组件）；
- 客户端跨格时更新它订阅的 3×3 `ClientGroup`，新看到的人先收到完整的 `Appear`；
- 把 `Changed<Velocity>` 的实体写成 `Moved` 事件，放进地图的发件箱；
- 每个有内容的格子发一帧 `Nearby`。

事件里的 `Who` 用 `LinkId` 或 NPC 的键，不用 `EntityId`：`EntityId` 只在本 shard 内有效，迁移后会变。

### 生成的代码

```csharp
// <auto-generated/> in Game.Server: one registration per kind
file static class MapKind
{
    [ModuleInitializer] internal static void Register() => Kinds.Register<Map>(
        id: 0x5A1C_93E1,                                                  // NinoTypeId of Game.Server.Map
        find: static (World w, in Map key, out Entity e) => w.MapIndex.TryFind(key, out e),   // EasyEcs's index
        spawn: static (World w, in Map key) => w.Instantiate(Prefabs.Map).Set(key));          // the [Spawns<Map>] prefab
```

```csharp
// <auto-generated/> in Game.Server: typed sends, only on the declared senders
public static class PlayerSends
{
    // EnterMap is [From<Player>] IRequest<Map, LinkId>
    public static void Ask<TPending>(this in Self<Player> self, Map to, in EnterMap message, in TPending pending, in ReplyTo reply = default)
        where TPending : struct, IComponent =>
        self.Runtime.Ask(in to, MessageIds.EnterMap, in message, in pending, in reply);

    // LeaveMap is [From<Player>] IMessage<Map>
    public static void Send(this in Self<Player> self, Map to, in LeaveMap message) =>
        self.Runtime.Send(in to, MessageIds.LeaveMap, in message);
}
// No Self<Map>.Ask(…, EnterMap): a map cannot send it, and the call does not compile.
```

```csharp
// <auto-generated/> in Game.Server.Logic: MapServer's dispatch table, swapped with the Logic assembly on hot reload
file static class MapServerDispatch
{
    [ModuleInitializer] internal static void Register() => Dispatch.Register<MapServer>(&Deliver);

    // Called by MailSystem for one envelope, in arrival order; target is the map, receiver the bound entity.
    static void Deliver(World world, Entity target, Entity receiver, in Envelope envelope, ref Outcome outcome)
    {
        switch (envelope.MessageId)                                       // a jump table
        {
            case MessageIds.EnterMap:
            {
                var m = envelope.Read<EnterMap>();
                outcome.Reply(EntrySystem.Enter(in m, Self<Map>.Of(target), in target.Read<MapSlot>(), world));
                break;
            }
            case MessageIds.LeaveMap:
            {
                var m = envelope.Read<LeaveMap>();
                EntrySystem.Leave(in m, Self<Map>.Of(target), world);
                break;
            }
            case MessageIds.MoveTo:                                       // a client message: receiver is the link entity
            {
                var m = envelope.Read<MoveTo>();
                MovementSystem.Move(in m, ref receiver.Get<Destination>());
                break;
            }
            default: outcome.Refuse(RpcStatus.NotHandled); break;
        }
    }
}
```

```csharp
// <auto-generated/> in Game.Server: the client routing table, used by Client entities on GatewayServer
file static class ClientRoutes
{
    [ModuleInitializer] internal static void Register() => Routes.Register(static id => id switch
    {
        MessageIds.TravelTo => Kinds.IdOf<Player>(),                     // from [Receive<Player>] Travel
        MessageIds.MoveTo   => Kinds.IdOf<Map>(),                        // from [Receive<Map>] Move
        _ => 0,                                                           // not a client message: refused
    });
}
```

```csharp
// <auto-generated/> in the AppHost, from Game.Server's wire.manifest
namespace Game.Server
{
    public readonly struct GameServer    : IServerType { public static string Name => "Game.Server.GameServer"; }
    public readonly struct MapServer     : IServerType { public static string Name => "Game.Server.MapServer"; }
    public readonly struct GatewayServer : IServerType, IAcceptsClients { public static string Name => "Game.Server.GatewayServer"; }
}
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
push    Game.Protocol.Nearby
```

### 运行时的路径

```
① 客户端连 gate-0           框架在 gate-0 激活 Client(c1)；AttachSystem 发 Attach → Player 42@game-1
② Player 42 回 LinkId        Client 写路由 (Player, 42, L1)，发 LinkConfirmed → Player 42
③ 客户端 TravelTo(7)         Client(c1)@gate-0 → 路由表 → Player 42@game-1
④ Player 问地图              Player 42@game-1 → Map 7@map-0：EnterMap；Map 7 建 Avatar（LinkId L2），回复 L2
⑤ Player 回复客户端          Reply.Linking(true, Map 7, L2) → Client(c1)：先写路由 (Map, 7, L2)，再把 true 交给客户端
⑥ Client 确认                LinkConfirmed(L2, gate-0, 连接代数) → Map 7
⑦ 客户端 MoveTo              Client(c1) → 路由 (Map, 7, L2) → Map 7：MovementSystem 写这个 Avatar 的 Destination
⑧ 每 50 ms                  Map 7 的 Simulation：转向、积分、AOI；Nearby → 有观察者的网关 → 各个连接
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查：**
- 发送方向错了：没有对应的方法；
- 客户端消息没写 `[Receive<K>]`，或者两个种类都收它：报错；
- 并行方法和批量方法里，发消息、做结构变更、写有索引的组件：报错；
- 对生命周期 tag 做反应的系统不在 `Messages` 组里，或者同时对 `MailSystem` 之前和之后的 tag 做反应：报错；
- `ReplyTo` 被复制：报错；
- Protocol 里出现种类，或者出现服务器之间的消息：报错；
- 种类的键超过 16 字节，或者不是非托管类型：报错；
- 组件，以及 `Pending`、`Answer` 的类型参数，声明在 Logic 工程里：报错；
- DAS022：结构变更之后继续用旧的 `ref`：报错。

**删掉的东西：**
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调，`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded，自研的记录格式；
- v11 的位置目录、10 秒一次的租约摘要、迁移栅栏。

**版本记录：**

| 版本 | 主要变化 |
|---|---|
| v8 | 消息写明发送方和接收方；会话与链接；一个节点只跑一种服务器 |
| v9 | 地址统一；系统按组件查询；生命周期改成 tag；记录用 EasyEcs 4.4 |
| v10 | 链接随回复建立；参数绑定表；示例重写 |
| v11 | 客户端也是一个种类；Protocol 和 Server 分开；`LinkId` 由目标分配；第一条链接；链接租约；记录一次保存一次加载 |
| v12 | 种类就是它的地址，也是实体上带值索引的组件，去掉 `ActorKey`；等待的数据是普通组件加 `[Pending]`；新增"谁做什么"表；Protocol 不出现任何种类，客户端消息的目标由服务端的 `[Receive<K>]` 决定；服务器写 `[Hosts<Client>]`，名字统一成 `XxxServer`；AppHost 恢复 `AddNodes<T>`；去掉位置目录，推送走链接；去掉迁移栅栏，迁移期间尽力保序；卸载时写入中的占位；被销毁的持久化子孙会触发保存；迁移默认带上所有组件；`[OnError]` 默认 `Resume`；目标重启后重新 `Attach`；顶号；生命周期系统的顺序由生成器排；完整例子和生成的代码 |

---

## 14. 待拍板

1. **种类就是它的地址，也是实体上的组件**：`record struct Map(long Id) : IKind<Map>`，不再分开写键和种类，也不再有 `ActorKey`。要写成一行，需要 EasyEcs 支持第 10 条。
2. **Protocol 只有数据**：客户端消息不写目标，目标由服务端的 `[Receive<K>]` 决定。
3. **推送只经过链接**（`world.Push(linkEntity, …)`、`self.Push(…)`），不提供"按 `ClientId` 给任意客户端发消息"。要给不在线或没链接的玩家发消息，发给 Player，由 Player 推送。
4. **`[OnError]` 默认 `Resume`**，和现在一样。
5. **`Self<K>` 这个名字**，也可以叫 `Me<K>`。
