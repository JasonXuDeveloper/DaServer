# DaServer 新编程模型设计（草案 v13，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP；能用 EasyEcs 的功能就用，其余由我们的特性加生成器完成，不重复造轮子。

目标：从类型就看得出每条消息从哪个实体发到哪个实体，以及它们在哪种服务器上；客户端只知道它收发的消息，不知道服务端怎么划分；高性能，高并发。

本稿是完整的一份。第 12 节是完整的例子，分别给出用户写的代码和生成的代码。第 13 节是版本记录，第 14 节是待拍板的点。

---

## 0. 一页看懂

### 三个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息，只有数据，不出现任何服务端的种类 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | 种类、服务器之间的消息、组件、prefab、服务器类型、系统组，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有系统（`[Receive]`、`[ForEach]`），以及生成的分发表和客户端路由表 | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

### 用户写的

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `record struct MoveTo(Vector2 Target) : IFromClient;` | Protocol |
| 推给客户端的消息 | `record struct Nearby(…) : IToClient;` | Protocol |
| 种类（地址，也是实体上的组件） | `partial record struct Map(long Id) : IKind<Map>;` | Server |
| 服务器之间的消息 | `[From<Player>] record struct EnterMap(…) : IRequest<Map, LinkId>;` | Server |
| 组件 | `record struct Grid(…) : IComponent;`，持久化的再加 `[Persist]` | Server |
| prefab | `[Spawns<Map>] static readonly Prefab Map = …;` | Server |
| 令牌里的声明 | `[Claims] record struct PlayerClaims(long PlayerId) : IComponent;` | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 系统组的频率 | `[Every(50)] struct Simulation : ISystemGroup;` | Server |
| 系统 | `partial class … : EcsSystem` 里的 `[Receive]`、`[ForEach]` | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `Client` | 框架内置的种类：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Self<K>` | 正在处理消息的实体自己：强类型的键，以及发消息的入口 |
| `LinkId`、`Linked`、`Confirmed` | 客户端在某个目标上的链接；目标上的链接实体，例如地图上的 Avatar |
| `ReplyTo<T>`、`Answer<TReq, TRep>`、`[Pending]`、`[Link]` | 晚些回复；等别的实体的结果；参数绑定到哪个实体 |
| `Activated`、`Unloading`、`Unlinked`、`RouteLost`、`LinkLost` | 生命周期消息：框架发给实体自己，用 `[Receive]` 处理 |
| `IdleAfter`、`Due` | 空闲回收；定时 |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 种类组件的值索引 | 为 `partial` 种类生成 `Key => this` | 收到消息时查索引 |
| 只处理一类实体 | `[With<Map>]` | — | — |
| 每 tick 的逻辑 | `[ForEach]` 的 job、`Parallel`、批量方法、`[Changed<T>]` | — | — |
| 频率 | 系统组 | `[Every(ms)]` | shard 循环按时调用 `Update<T>()` |
| 消息处理 | — | `[Receive]` 生成的分发跳板 | `MailSystem` 按到达顺序调用，逐条隔离异常 |
| 发送方向 | — | `[From<K>]` 生成 `Self<K>` 上的方法 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Receive<K>]` 生成路由表 | `Client` 实体转发 |
| 等待、回复 | `ChildOf`、`ICleanupComponent` | `[Pending]`、`ReplyTo<T>` | 调用 id、`Due`、期限传递 |
| 链接 | 子实体、值索引 | `[Link]` 参数绑定 | 确认、核对、重连 |
| 生命周期 | — | 用户写 `[Receive]` | 发出生命周期消息 |
| 定时 | 有序值索引的 `Range` | — | `TimerSystem` |
| 按地图遍历 | `IGroup`、`GroupBy`、`InGroup` | — | — |
| 只写改过的 | `ITrackChanges`、`[Removed<T>]`、`ICleanupComponent` | `[Persist]` | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收上下文 | 分发表、路由表在 Logic 程序集 | 两次更新之间切换 |
| 对象复用 | `DynamicBuffer` 池化 | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。** 生命周期事件也是消息，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，和 Orleans 一样：迁移的那一两秒里，在途的消息可能后发先到。
   - 需要严格先后关系的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。
4. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。
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
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程只跑一种服务器。
  - 本地开发由 Aspire 起多个进程，测试用 `TestCluster`。
  - 小规模部署可以定义一个 `[Hosts<Player>, Hosts<Map>, Hosts<Client>] AllInOne`，一个节点承载所有种类。
- **`[Hosts<Client>]` 就是"接受客户端"。**
- **种类绑定的是服务器类型，不是节点：**

| 关系 | 数量 | 由谁决定 |
|---|---|---|
| 种类 → 服务器类型 | 一般一对一；`AllInOne` 时一对多 | 服务器上的 `[Hosts<…>]` |
| 服务器类型 → 节点 | 一对多 | AppHost 部署几个 |
| 种类 → 实例 | 每个键一个 | 第一条消息到达时激活 |
| 实例 → 节点 | **任一时刻恰好一个** | 在承载该种类的节点中按键放置，节点增减时迁移 |

  同一个键任一时刻只有一个实例，由现有的放置、记录版本和隔离机制保证。`Client` 例外，它固定在连接所在的节点（第 7 节）。
- **shard** 是节点里的一个线程加一个 world。节点承载的所有种类共用 shard 的 world，实例落在哪个 shard 由键的哈希决定。
- **AppHost：**
  ```csharp
  var cluster = builder.AddDaServerCluster("game");
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientEndpoints();
  ```
  - AppHost 不引用服务端程序集。DaServer.Aspire 的生成器读服务端工程提交的 `wire.manifest`，在 AppHost 里生成同名的服务器类型。
  - 承载 `Client` 的服务器，生成的类型实现 `IAcceptsClients`。`WithClientEndpoints()` 的约束是 `where T : IAcceptsClients`，给别的服务器开客户端端口编译不过。
  - 接受客户端的服务器没开端口时，AppHost 构建警告；消息引用的种类没有节点承载时，AppHost 构建报错。
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

- **地址**：`new Map(7)` 就是 7 号地图这个实例的地址。消息和组件里存别的实例的地址，直接用 `Map`、`Player` 类型的字段。
- **同一个值也是组件**：地图实体上带着 `Map(7)`。
  - 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
  - 只处理地图的系统写 `[With<Map>]`；
  - `[ForEach]` 里取 `in Map self`，就知道自己是谁。
- **接口**：`IKind<T>` 继承 `IComponent` 和 `IIndexed<T>`。
  - EasyEcs 的生成器会读继承来的接口，已经支持。
  - 种类必须是 `partial`，我们的生成器为它补上 `public T Key => this;`，EasyEcs 不必改动。
- **只有运行时能写种类组件**：
  - 分析器禁止用户代码对种类组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`；
  - 也禁止把种类组件放进 `CreateEntity` 或 prefab；
  - 运行时创建实例时，断言这个键在索引里还不存在。
  - 这样不会出现同一个键的第二个副本，也不会有改了键的实体覆盖别人的记录。
- **键**：种类的字段合计不超过 16 字节，并且是非托管类型。字符串键用 64 位哈希代替，原文另存在组件里。种类 id 是 Nino 类型 id，记在 `wire.manifest` 里。
- **`[Stateless]` 种类**：不保存、不迁移，每个节点可以有自己的实例，键只用来选实例。
- **子实体不可寻址**，例如 Avatar、等待中的请求，它们跟父实体一起保存、一起迁移。
- **没有"无键种类"**：
  - 每个节点一份的状态，用单例；
  - 无状态的工作，用 `[Stateless]`；
  - 全局服务，用固定键，例如 `Matchmaker(int Region)`。

---

## 4. 消息

### 客户端收发的消息（Protocol）

```csharp
// Game.Protocol: data only, no server kinds
[NinoType] public record struct TravelTo(long MapId)          : IFromClient<bool>;   // a request: the client waits for a bool
[NinoType] public record struct MoveTo(Vector2 Target)        : IFromClient;
[NinoType] public record struct Say(long To, string Text)     : IFromClient, IAddressed;   // To picks one of several rooms
[NinoType] public record struct Nearby(NearbyEvent[] Events)  : IToClient;
```

- `IFromClient` 是客户端发的单向消息，`IFromClient<TReply>` 是客户端发的请求，`IToClient` 是推给客户端的消息。
- **消息里不写目标**，所以客户端不知道服务端有 Player、Map、Room。
- **目标由服务端决定**：
  - 处理这条消息的 `[Receive<Map>]` 写的是哪个种类，消息就转发给哪个种类；
  - 生成器据此在 Logic 里生成客户端路由表；
  - 一条客户端消息只能有一个目标种类。
- **`IAddressed` 的消息**带一个 `To` 字段，用来在同种类的多个链接里选一个，例如多个聊天室。

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
  - 远端的客户端只能发 `IFromClient` 消息。
- **方向由生成的方法保证**：每条消息只在它的发送方的 `Self<K>` 上有发送方法。方向写错，这个方法就不存在，编译不过。
  - 这是帮助写对的手段，不是安全边界：服务端的代码本来就可信。
  - 安全边界在网关：网关只转发 `IFromClient` 消息。
- **代替客户端发请求**：Player 处理客户端的 `TravelTo` 时去问地图，写成 `self.AskFor(from, map, in enterMap, …)`。
  - 框架把这个客户端的连接信息放进信封：`ClientId`、所在节点、连接代数；
  - 地图建链接时直接用；
  - 用户不接触这些信息。
- **`ICluster`**（HTTP、后台、测试）不属于任何种类，可以发任何服务器之间的消息，回复回到调用方。
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Client` 的节点，由连着它的那个节点处理。

---

## 5. 系统

### 方法

| 方法 | 执行时机 | 能做什么 |
|---|---|---|
| `[Receive]`、`[Receive<K>]` | 每条消息一次，按到达顺序 | 判定，写组件，增删实体，发消息，回复 |
| 顺序执行的 `[ForEach]` | 所在组每次运行时 | 同上；参数里拿 `Entity`、`World` |
| `[ForEach(Parallel = true)]`、批量方法 | 同上，可以并行、可以 SIMD | 只读写组件（结构变更、写有索引的组件，EasyEcs 已用 EECS013、EECS015 禁止），另外不发消息 |

- **`[Receive]` 属于哪个种类**：
  - 服务器之间的消息，从接口推出；
  - 客户端消息写 `[Receive<Map>]`，这同时就是路由；
  - 生命周期消息从 `Self<K>` 参数推出。
- **系统按组件查询，不按种类划分。**
- **返回值就是回复**；返回 `void` 就是单向消息。
- **系统必须是 `partial`**：生成器要在系统里生成调用 `[Receive]` 的跳板。分析器一键修复。

### `[Receive]` 的参数

| 参数 | 绑定到 |
|---|---|
| 消息 `in T` | 这条消息 |
| `Self<K>` | 目标实体自己 |
| `Entity`、组件（`ref`/`in`） | **目标实体**（默认） |
| `[Link] Entity`、`[Link]` 组件 | 这个客户端在目标上的链接实体，例如这个玩家的 Avatar。只用于客户端消息和 `Unlinked` |
| `[Pending]` 组件 | 处理 `Answer` 时，等待实体上的数据 |
| `in ReplyTo<T>` | 要晚些回复时取；处理 `Answer` 时，是等待实体上保存的令牌 |
| `From` | 发送方 |
| 单例 | 本 shard 的单例 |

**引用不会失效**：处理方法里做了结构变更（建实体、`Ask`、`Link` 等）之后，参数照常可用。
- 生成的跳板把 `in` 参数复制成局部变量；
- `ref` 参数传局部变量进去，调用结束后，如果实体还在，再用一次新的 `Get` 写回。
- 这和 EasyEcs 对 `[ForEach]` 参数的处理一样。

### 频率与 `Messages` 组

- **`[Every(50)]`** 写在系统组上，按真实时间每 50 毫秒调用一次 `Update<Simulation>()`。EasyEcs 自带的 `UpdateInterval` 按更新次数计算，不是时间。
- **每次唤醒**，先跑框架的 `Messages` 组，再跑到了时间的 `[Every]` 组。
- **`Messages` 组里**依次是：
  1. `TimerSystem`：投递到期的邮件；
  2. `MailSystem`：按到达顺序逐条调用分发跳板，生命周期消息也在其中；
  3. `LifecycleSystem`：销毁处理完的链接实体、等待实体，以及已经写完记录的实例。
- **框架的系统自己处理异常**，不受 EasyEcs 的 `SystemErrorPolicy` 管理。
- **生成的分发表和路由表不在加载时生效**：新版本先暂存，各个 shard 在两次更新之间换上。

### 处理消息时抛出异常

- `MailSystem` 逐条捕获异常，这条消息对应的请求以失败结束。
- 种类上的 `[OnError]` 决定之后怎么办：
  - `Resume`（默认，和现在一样）：实体保持原样，接着处理下一条；
  - `Reload`：按正常流程卸载（第 6 节），再从记录重新激活。
- `Client` 种类抛异常时，断开这个客户端。
- 生命周期消息也是邮件，所以一个实体的初始化代码出错，只影响这个实体，不会卡住整个 shard。

---

## 6. 生命周期

生命周期事件是框架发给实体自己的消息，和其他邮件排在同一个顺序里，用 `[Receive]` 处理。

| 消息 | 何时 | 常见用途 |
|---|---|---|
| `Activated(bool Restored, bool MovedIn)` | 实例激活后的第一条邮件，排在所有其他邮件之前 | 初始化；分配 shard 内的编号 |
| `Unlinked` | 某个客户端的链接断开（第 7 节）；`[Link]` 绑定到正在断开的链接实体 | 广播离开 |
| `RouteLost(int Kind, long Key)` | `Client` 上的某条路由失效 | 重新建立链接 |
| `LinkLost(int Kind, long Key, From Client)` | 发给这条路由的 `Via`（当初回复 `Reply.Linking` 的实体） | 重新进入地图 |
| `Unloading` | 卸载之前的最后一条邮件 | 收尾 |

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达，实体还不存在 | 按 prefab 建实体，异步读记录；邮件留在邮箱里 |
| 激活 | 记录到了，或者没有记录 | 装上记录，投递 `Activated` |
| 运行 | — | 邮件按到达顺序投递 |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或者调用 `world.Unload(e)` | 依次：<br>1. 对每个链接实体投递 `Unlinked`，并通知对应的 `Client`，只发不等；<br>2. 投递 `Unloading`；<br>3. 进入写入中，写记录；<br>4. 写成功后销毁 |

- **写入中**（就是现在的 `Saving`/`FinalSave`）：
  - 这期间到达该键的消息，在占位里排队；
  - 写成功后，排队的消息激活新的实例，读到的是刚写好的记录；
  - 写失败就重试，绝不丢弃。
- **激活的条件**：
  - 默认任何发给它的消息都能激活（Orleans 的虚拟 actor）。
  - 种类可以写 `[ActivatedBy<EnterMap>]` 收紧；其他消息遇到未激活的实体，返回 `NoActor`。
  - 框架的控制消息（`Answer`、`LinkConfirmed` 等）不会激活实体，找不到就丢弃。
  - `Client` 只在客户端连接时激活，消息不能激活它。

---

## 7. 客户端与链接

### `Client` 实体

- **激活**：客户端连上一个承载 `Client` 的节点后，框架用令牌里的 `ClientId` 激活 `Client(ClientId)`。如果这个节点上已经有同一个 `ClientId` 的 `Client`（旧连接还没断干净），先卸载旧的。
- **连接信息**：`(ClientId, 节点, 连接代数)`，由框架内部使用，用户看不到。
  - 连接代数全集群单调递增：节点启动时随机生成的节点 id，加上节点内的计数。
  - 所以不同节点上的两次连接也能比出新旧。
- **声明**：令牌里的声明放在用户声明的 `[Claims]` 组件里，例如 `PlayerClaims(long PlayerId)`。框架解码令牌，把它加到 `Client` 上；框架本身不认识任何业务字段。
- **固定在连接所在的节点**：不参与放置和重新平衡，不迁移；连接断开，它就卸载。
- **扩展**：用 `[Spawns<Client>]` 的 prefab 给它加组件，例如限流用的令牌桶；在 Logic 里给它写系统。
- **寻址**：别的实体不能用 `ClientId` 找到它。推送只经过链接，所以不需要位置目录。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Client` 实体。
2. 第一条链接建好之前，消息在 `Client` 上排队；链接建好后，框架推一条 `Ready` 给客户端。
3. 过 `Client` 的过滤方法 `[Filter] static Verdict Allow(in Envelope e, ref RateLimit r)`：逐条执行，返回放行或者拒绝的原因。
4. 按生成的路由表，得到目标种类。
5. 在路由表 `DynamicBuffer<Route>` 里找该种类的链接：
   - `IAddressed` 的消息，用 `To` 选具体的一项；
   - 其他消息，用最近建立的那一项。
6. 转发给目标。转发的消息带着 `LinkId`，目标据此找到链接实体，供 `[Link]` 参数绑定。
7. 被拒绝时，客户端收到原因：`NotLinked`、`Busy`、`NoActor`、`Filtered` 等。

### 链接

| 在哪 | 是什么 |
|---|---|
| 目标一侧 | 一个子实体，带 `Linked(LinkId, 连接信息)`，按 `LinkId` 建值索引。连接信息在建链接时就有：来自 `Attach` 的信封，或者 `AskFor` 带来的信封 |
| `Client` 一侧 | 路由表的一项 `Route(种类 id, 键, LinkId, Via)`。`Via` 是回复 `Reply.Linking` 的那个实体，例如 Player |

`LinkId` 由目标在建链接实体时分配，全集群唯一：节点 id 加计数。

**建立链接：**
1. 目标处理请求时建好链接实体，回复 `LinkId`。
2. 在 `Client` 上写路由，有两种情况：
   - 客户端发起的请求（例如 `TravelTo`）：回复客户端的实体返回 `Reply.Linking(true, map, linkId)`。这个回复先到 `Client`，`Client` 先写路由，再把结果交给客户端。
   - `Client` 自己发起的请求（例如 `Attach`）：处理 `Answer` 时调用 `self.Route(answer.From, answer.Value)`。
3. `Client` 写好路由后，先给目标发 `LinkConfirmed(linkId)`，之后才转发消息。这两条消息从同一个 `Client` 发往同一个目标，所以确认一定先到。确认如果丢了，这条链接上第一条转发来的消息也算确认。
4. 目标收到确认后，给链接实体加 `Confirmed` tag。
   - 推送和 AOI 只认带 `Confirmed` 的链接实体。
   - 第一次看到 `Confirmed` 时，AOI 推一份完整的快照。
   - 所以客户端一定先收到 `TravelTo` 的回复，再收到第一条 `Nearby`，并且不会漏掉已经在场的人。
5. **期限**：
   - `AskFor` 的期限取客户端请求剩下的时间，所以内层的请求不会比外层更晚超时。
   - `Reply.Linking` 到达时，如果客户端的这次请求已经超时，就不写路由。
   - 目标那边的链接实体过了期限仍未确认，就删除。

**第一条链接：**
- `Client` 的 `Activated` 处理方法，根据声明向 Player 发 `Attach`。
- `Attach` 对同一个连接是幂等的。
- 失败（超时、`NoOwner`、`Busy`）时，用 `self.After` 按带抖动的退避重试，超过 N 次就断开客户端。这样一个游戏节点宕机时，十万个客户端不会同时重试。

**顶号**：Player 处理 `Attach` 时，以最新到达的连接为准。旧的链接实体收到 `Unlinked`，框架按它记下的连接信息断开旧的 `Client`。

**推送：**
- `world.Push(linkEntity, in msg)`：推给某个链接实体对应的客户端；
- `self.Push(in msg)`：推给自己所有已确认的链接；
- `ClientGroup`：广播（第 9 节）。
- 推送按链接实体里记下的节点直接发出。

### 断开

- **目标断开**（收到 `LeaveMap`，或者自己被卸载）：
  - 框架对链接实体投递 `Unlinked`，处理完后销毁它；
  - 同时通知 `Client` 删掉这条路由，客户端会收到一条框架通知。
- **`Client` 断开**（客户端断线、被踢、被顶号）：`Client` 按路由表通知每个目标，目标对相应的链接实体投递 `Unlinked`。
- **通知丢失时的兜底**：
  - 两个节点之间的链路重连时，目标一侧按网关列出自己持有的链接实体，网关指出其中哪些已经不认识，目标对这些投递 `Unlinked`。
  - 某个节点离开成员视图时，各节点对"记在该节点的链接实体"投递 `Unlinked`。
  - 网络抖动、GC 停顿不会让链接失效，只有节点离开成员视图才会。
- **目标重启**（实例从记录恢复，旧的链接实体没有了）：
  - `Client` 转发时收到 `NotLinked`，就删掉这条路由，给自己投递 `RouteLost(种类, 键)`，并给 `Via` 发 `LinkLost(种类, 键)`。
  - 例如地图重启后，Player 收到 `LinkLost(Map, 7)`，重新 `EnterMap`；Player 重启后，`Client` 收到 `RouteLost(Player, 42)`，重新 `Attach`。
- 链接相关的控制消息不受 `Busy` 限制。

**换地图**由业务决定先后：Player 先发 `LeaveMap`，再请求 `EnterMap`，框架不会自动断开旧链接。一个客户端可以同时链接同一种类的多个目标，例如多个聊天室。

---

## 8. 等待别的实体的结果

- **`self.Ask(to, in message)`、`self.Ask(to, in message, in pending)`、`self.Ask(to, in message, in pending, in reply)`** 会建一个等待子实体，上面有：
  - 可选的用户数据：一个普通组件，例如 `Travelling`；
  - 调用 id：全集群唯一；
  - `Due`：期限；
  - 可选的 `ReplyTo<T>`。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer` 邮件**：
  - 标了 `[Pending]` 的组件参数和 `in ReplyTo<T>` 绑定到等待实体，其余组件参数绑定到发问方；
  - 处理完，框架删除等待实体。
- **超时**：框架去掉等待实体上的 `Due`，投递一个 `Ok = false` 的 `Answer`。
- **`ReplyTo<T>` 是带类型的清理组件**，由框架搬运，用户不能复制（分析器检查）：
  - `T` 就是请求声明的回复类型，`world.Reply(in reply, 值)` 在编译期检查类型；
  - `world.Reply` 先读出令牌，再移除组件，然后发出回复；
  - 等待实体在回复之前被销毁时，清理系统恰好执行一次，给调用方发一个失败回复；
  - 迁移时，旧的一份在销毁之前先移除 `ReplyTo`，不会误发失败回复；
  - 到了新节点，`LoadEntitiesInto` 把它作为普通组件装上，清理规则照常生效。
- **请求的处理方法**必须返回回复，或者取 `ReplyTo<T>`（分析器检查）。
- **晚些回复**：`world.Hold(in reply, in 数据)` 建一个等待实体，之后对它调用 `world.Reply`。
- **一对多**：`self.AskAll(keys, in message)` 只建一个等待实体，回信累积在它的缓冲里。全部到齐或者超时后，投递一个 `Answers<TReq, TRep>`，里面的集合是池化的。
- **类型放在哪**：等待的数据是组件，`Answer` 的类型参数也一样，都必须声明在 Server 工程。Logic 热更时，它里面的类型会被移除。

---

## 9. 时间与广播

**定时：**
- `Due(long UnixMs)` 是绝对的 UTC 毫秒数，带有序值索引。迁移和恢复后仍然正确，前提是各节点用 NTP 对时。
- `TimerSystem` 遍历 `Range(long.MinValue, 现在)`：
  - 到期的 `After` 子实体：投递它的消息，然后销毁。EasyEcs 允许在遍历中销毁实体。
  - 到期的等待实体：遍历结束后，再统一去掉 `Due` 并投递超时，因为遍历时不能改索引的键。
- `self.After(delay, in message)` 建一个带 `Due` 的子实体，到期时把消息作为邮件投给父实体。
- 周期性的逻辑用 `[Every]` 的组，不用定时器。

**广播：**
- `ClientGroup`：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出到各个连接；
  - 成员是已确认的链接实体，链接实体收到 `Unlinked` 后自动退出所有组。
- 小组（公会、队伍）：一个固定键的实例，成员表是它的组件，由它转发。它取代现在的 `Publish`。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热更、升级（EasyEcs 4.4）

**记录**：一次 `SaveEntities`，内容是：
- 根实体，放在第一个；
- 带至少一个 `[Persist]` 组件的子孙；
- 这些子孙到根的路径上的实体。

保存用的集合在注册时建好：`persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`。

**恢复：**
- 记录头里存着实体数 n。
- `into[0]` 是按 prefab 建好的根实体，其余是 n−1 个空实体，然后一次 `LoadEntitiesInto(record, into)`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会被改写成新的 id；指向组外的 `EntityId` 变成 `Null`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里。

**只写改过的：**
- `ChangeSystem` 同时看 `[Changed<T>]` 和 `[Removed<T>]`：持久化组件被写或者被删，都给根实体标 `Unsaved`。
- 带持久化组件的子孙，在同一趟里加上框架的清理组件 `Persisted(EntityId Root)`。新建和 `LoadEntitiesInto` 都算写入，所以加载后会自动补上。
- 子孙被销毁（例如物品用掉了）时，清理系统按 `Root` 给根实体标 `Unsaved`，然后移除自己。所以删除也会写进记录，重新加载后物品不会回来。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式。

**迁移：**
- 保存整棵子树的所有组件，标了 `[Transient]` 的除外，也就是默认都带。位置、速度这些运行时组件都会跟过去。
- 托管类型的组件必须有 Nino 格式化器（分析器检查）。
- 等待中的请求、链接实体也随实例迁移。
- 到达后的第一条邮件是 `Activated(MovedIn: true)`。shard 内的编号（例如 `MapSlot`）标了 `[Transient]`，在这里重新分配，并对子实体 `SetAll`。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。

**热更：**
- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有系统、分发跳板、分发表和客户端路由表。
- 每个 shard 在两次 `Update` 之间：
  1. `world.RemoveTypes(旧上下文)`；
  2. 加入新的系统；
  3. 换上新的分发表和路由表。
- 所有 shard 都换完之后，旧上下文才卸载。
- 新版本如果改了某条客户端消息的目标种类，`wire.manifest` 比对后拒绝这次热更，因为在途的路由会失效。
- 组件、消息、种类不热更，新增它们要走滚动升级。

**滚动升级：**
- `wire.manifest` 记录每条消息的接收方、发送方、回复类型，以及客户端消息的路由。
- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点 id 由每个进程启动时随机生成，所以同名节点重启后不会被当成原来那个。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或收到 `NoOwner`；实例在新节点从记录激活，最多丢一个检查点间隔的改动；客户端那边收到 `RouteLost`，经 `Via` 重新建立链接 |
| 网关宕机 | 客户端断开，带令牌重连，得到一个新的 `Client`；各目标看到那个节点离开视图，投递 `Unlinked` |
| 半开连接时，客户端已经连到别处 | 新连接的 `Attach` 胜出，旧的 `Client` 被断开；同一节点上的旧 `Client` 在新连接到来时先被卸载 |
| `Attach` 失败 | 带抖动退避重试，超过 N 次断开 |
| 发问方在回信之前被卸载 | `ReplyTo` 的清理立即给调用方发失败回复；之后到达的回信丢弃 |
| 卸载时写记录失败 | 重试；这期间到达的消息排队，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；目标节点宕机时，在途的单向消息可能丢失 |
| 邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回绝；链接的控制消息不受限 |
| 客户端读得太慢 | 断开（ADR-035） |
| 断开通知丢失 | 链路重连时的核对，或者节点离开视图，会补投 `Unlinked` |
| `[Receive]` 抛出异常 | 这个请求失败；默认 `Resume`；`Reload` 时断开链接，从记录重新激活；`Client` 直接断开 |
| `[Every]` 组里的系统抛出异常 | 按 `SystemErrorPolicy` 处理，并在健康检查里报告 |

---

## 12. 完整例子：连接、进地图、移动、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct TravelTo(long MapId)         : IFromClient<bool>;
[NinoType] public record struct MoveTo(Vector2 Target)       : IFromClient;
[NinoType] public record struct Nearby(NearbyEvent[] Events) : IToClient;
[NinoType] public record struct NearbyEvent(byte Who, long Id, byte Kind, Vector2 At, Vector2 Velocity);   // Who: avatar or npc
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
public record struct CurrentMap(Map Map) : IComponent;                                  // on a player
public record struct Travelling(Map To) : IComponent;                                   // waiting data
[NinoType, Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;   // on a map
[Transient] public record struct MapSlot(int Value) : IComponent, IGroup { public int Group => Value; }   // shard-local number
public record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar> { public Avatar Key => this; }
public record struct Visible : IComponent;
public record struct Position(Vector2 Value) : IComponent;
public record struct Heading(Vector2 Value) : IComponent;                               // computed in parallel, untracked
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;
public record struct Destination(Vector2 Target) : IComponent;
public record struct Speed(float Value) : IComponent;
public record struct Shout(NearbyEvent Event);                                          // a map's outbox element

public static class Prefabs
{
    [Spawns<Player>] public static readonly Prefab Player = new Prefab().SetCurrentMap(default);
    [Spawns<Map>]    public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetMapSlot(default).SetBuffer<Shout>();
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetHeading(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(5).SetVisible().SetMapSlot(default);
}

[Every(50)] public struct Simulation : ISystemGroup;
```

```csharp
// ── Game.Server.Logic: behaviour ─────────────────────────────────────────────
public sealed partial class ConnectSystem : EcsSystem
{
    [Receive]                                                              // the client connected: link it to its player
    static void Connected(in Activated a, in Self<Client> self, in PlayerClaims claims) =>
        self.Ask(new Player(claims.PlayerId), new Attach());

    [Receive]
    static void Attached(in Answer<Attach, LinkId> answer, in Self<Client> self)
    {
        if (answer.Ok) self.Route(answer.From, answer.Value);
        else self.RetryLater(in answer);                                   // jittered backoff; disconnect after N tries
    }

    [Receive]                                                              // the player restarted: attach again
    static void Lost(in RouteLost lost, in Self<Client> self, in PlayerClaims claims)
    {
        if (lost.Kind == Kinds.IdOf<Player>()) self.Ask(new Player(claims.PlayerId), new Attach());
    }
}

public sealed partial class PlayerLinkSystem : EcsSystem
{
    [Receive]                                                              // newest connection wins; the old one gets Unlinked
    static LinkId Attach(in Attach m, in Self<Player> self) => self.LinkFrom(supersede: true);
}

public sealed partial class TravelSystem : EcsSystem
{
    [Receive<Player>]                                                      // TravelTo goes to the player
    static void Travel(in TravelTo m, in Self<Player> self, in From from, in ReplyTo<bool> reply, in CurrentMap current)
    {
        if (current.Map.Id != 0) self.Send(current.Map, new LeaveMap(self.Key));   // leave first: no double presence
        var map = new Map(m.MapId);
        self.AskFor(from, map, new EnterMap(self.Key), new Travelling(map), reply);
    }

    [Receive]                                                              // the map answered: link the client, reply
    static void Entered(in Answer<EnterMap, LinkId> answer, [Pending] in Travelling trip, in ReplyTo<bool> reply,
        ref CurrentMap current, World world)
    {
        if (answer.Ok) current.Map = trip.To;
        world.Reply(in reply, answer.Ok ? Reply.Linking(true, trip.To, answer.Value) : Reply.Of(false));
    }

    [Receive]                                                              // the map restarted: enter it again
    static void Relink(in LinkLost lost, in Self<Player> self, in CurrentMap current)
    {
        if (lost.Kind == Kinds.IdOf<Map>() && current.Map.Id == lost.Key) self.AskFor(lost.Client, current.Map, new EnterMap(self.Key));
    }
}

public sealed partial class MapLifecycleSystem : EcsSystem
{
    [Receive]                                                              // a fresh shard-local number, on the map and its avatars
    static void Activated(in Activated a, in Self<Map> map, ref MapSlot slot, World world)
    {
        slot = MapSlots.Next(world);
        world.Children(map.Entity).SetAll(slot);
    }
}

public sealed partial class EntrySystem : EcsSystem
{
    [Receive]                                                              // EnterMap is IRequest<Map, …>
    static LinkId Enter(in EnterMap m, in Self<Map> map, in MapSlot slot, World world)
    {
        var key = new Avatar(map.Key, m.Player);
        if (world.AvatarIndex.TryFind(key, out var old)) world.Unlink(old);   // a reconnect replaces the old avatar
        return map.LinkFrom(world.Instantiate(Prefabs.Avatar).Set(key, slot));   // the client from the envelope; sets ChildOf(map)
    }

    [Receive]
    static void Leave(in LeaveMap m, in Self<Map> map, World world)
    {
        if (world.AvatarIndex.TryFind(new Avatar(map.Key, m.Player), out var avatar)) world.Unlink(avatar);
    }

    [Receive]                                                              // [Link] binds the avatar; the outbox binds the map
    static void Gone(in Unlinked u, [Link] in Linked link, ref DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(NearbyEvent.Disappear(link.Id)));
}

public sealed partial class MovementSystem : EcsSystem
{
    [Receive<Map>]                                                         // MoveTo goes to the map; [Link] binds the sender's avatar
    static void Move(in MoveTo m, [Link] ref Destination destination) => destination.Target = m.Target;
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
    public override void OnUpdate(World world) => Aoi.Run(world);
}
```

`Aoi.Run` 做的事：
- 按 `MapSlot` 分组遍历（`GroupBy<MapSlot>` 加 `InGroup`）；
- 每 tick 按位置把实体分进格子；格子不是组件，所以跨格不需要结构变更；
- 只处理带 `Confirmed` 的 Avatar；
- 客户端跨格，或者第一次带上 `Confirmed` 时，更新它订阅的 3×3 `ClientGroup`，并推送它看到的人的完整快照；
- `Changed<Velocity>` 的实体写成 `Moved` 事件，和地图发件箱里已有的事件一起按格子合并，每个有内容的格子发一帧 `Nearby`；
- 事件里的 `Id`：Avatar 用 `LinkId`，NPC 用它的键，再用 `Who` 区分两类，所以两类 id 不会撞。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: the kind's key and its registration
partial record struct Map { public Map Key => this; }

file static class MapKind
{
    [ModuleInitializer]
    internal static void Register() => Kinds.Register<Map>(
        id: 0x5A1C_93E1,                                                                       // NinoTypeId of Game.Server.Map
        find: static (World w, in Map key, out Entity e) => w.MapIndex.TryFind(key, out e),   // EasyEcs's value index
        spawn: static (World w, in Map key) => w.SpawnKind(Prefabs.Map, key));                 // the runtime's only writer of Map
}
```

```csharp
// <auto-generated/> Game.Server: typed sends, only on the declared senders
public static class PlayerSends
{
    // EnterMap is [From<Player>] IRequest<Map, LinkId>; AskFor carries the client's connection in the envelope
    public static void AskFor<TPending>(this in Self<Player> self, in From client, Map to, in EnterMap message,
        in TPending pending = default, in ReplyTo<bool> reply = default) where TPending : struct, IComponent =>
        self.Runtime.Ask(in to, MessageIds.EnterMap, in message, in pending, in reply, in client);

    // LeaveMap is [From<Player>] IMessage<Map>
    public static void Send(this in Self<Player> self, Map to, in LeaveMap message) =>
        self.Runtime.Send(in to, MessageIds.LeaveMap, in message);
}
// There is no Self<Map>.AskFor(…, EnterMap): a map cannot send it, so such a call does not compile.
```

```csharp
// <auto-generated/> Game.Server.Logic: a trampoline inside each partial system (it can reach private handlers)
partial class EntrySystem
{
    internal static void Receive_Enter(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<EnterMap>();
        var slot = target.Read<MapSlot>();                                 // copied: survives the handler's structural changes
        outcome.Reply(Enter(in m, Self<Map>.Of(target), in slot, world));
    }

    internal static void Receive_Gone(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var u = envelope.Read<Unlinked>();
        var linked = link.Read<Linked>();
        var outbox = target.Get<DynamicBuffer<Shout>>();                   // a ref parameter: a local in, written back after
        Gone(in u, in linked, ref outbox);
        if (target.IsAlive) target.Set(outbox);
    }
}

partial class MovementSystem
{
    internal static void Receive_Move(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<MoveTo>();
        var destination = link.Read<Destination>();                         // [Link]: the sender's avatar
        Move(in m, ref destination);
        if (link.IsAlive) link.SetDestination(destination);
    }
}
```

```csharp
// <auto-generated/> Game.Server.Logic: dispatch tables per kind, staged on load and adopted by each shard between updates
file static class Dispatch_Map
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Map>(static id => id switch
    {
        MessageIds.EnterMap  => EntrySystem.Receive_Enter,
        MessageIds.LeaveMap  => EntrySystem.Receive_Leave,
        MessageIds.Unlinked  => EntrySystem.Receive_Gone,
        MessageIds.Activated => MapLifecycleSystem.Receive_Activated,
        MessageIds.MoveTo    => MovementSystem.Receive_Move,
        _ => null,                                                          // NotHandled
    });
}

// <auto-generated/> Game.Server.Logic: the client routing table, from [Receive<K>]
file static class ClientRoutes
{
    [ModuleInitializer]
    internal static void Register() => Routes.Stage(static id => id switch
    {
        MessageIds.TravelTo => Kinds.IdOf<Player>(),                        // [Receive<Player>] Travel
        MessageIds.MoveTo   => Kinds.IdOf<Map>(),                           // [Receive<Map>] Move
        _ => 0,                                                             // not a client message: refused
    });
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
// IServerType declares `static abstract string Name { get; }`; AddNodes<T>() where T : IServerType reads T.Name.
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
① 客户端连 gate-0      框架在 gate-0 激活 Client(c1)，加上 PlayerClaims；投递 Activated
② Activated           Client → Attach → Player 42@game-1；Player 建链接实体（连接信息来自信封），回复 LinkId L1
③ Answer              Client 写路由 (Player, 42, L1)，发 LinkConfirmed(L1)；推 Ready 给客户端，放出排队的消息
④ TravelTo(7)         Client(c1)@gate-0 → 路由 → Player 42@game-1
⑤ AskFor              Player 42 → Map 7@map-0：EnterMap；Map 7 建 Avatar（连接信息来自信封，LinkId L2），回复 L2
⑥ 回复客户端           Reply.Linking(true, Map 7, L2) → Client(c1)：先写路由 (Map, 7, L2, Via Player 42)，再交给客户端
⑦ LinkConfirmed(L2)   Client → Map 7：Avatar 带上 Confirmed；下一次 Simulation 推完整快照
⑧ MoveTo              Client(c1) → 路由 → Map 7：[Link] 绑定到这个 Avatar，写 Destination
⑨ 每 50 ms            Map 7 的 Simulation：转向、积分、AOI；Nearby → 有观察者的网关 → 各个连接
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查：**
- 发送方向错了：没有对应的方法；
- 客户端消息没有 `[Receive<K>]`，或者两个种类都收它：报错；
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`：报错；
- 系统不是 `partial`：报错，可一键修复；
- 用户代码写种类组件，或者把种类组件放进 prefab、`CreateEntity`：报错；
- 并行方法、批量方法里发消息：报错（结构变更、写有索引的组件，EasyEcs 已经报 EECS013、EECS015）；
- 复制 `ReplyTo<T>`：报错；
- Protocol 里出现种类，或者服务器之间的消息：报错；
- 种类的键超过 16 字节，或者不是非托管类型：报错；
- 组件、等待数据、`Answer` 的类型参数声明在 Logic 里：报错；
- 要迁移的托管组件没有 Nino 格式化器：报错。

**删掉的东西：**
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调，`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded，自研的记录格式，`ActorKey`；
- v11 的位置目录、定时的租约摘要、迁移栅栏。

**版本记录：**

| 版本 | 主要变化 |
|---|---|
| v8 | 消息写明发送方和接收方；会话与链接；一个节点只跑一种服务器 |
| v9 | 地址统一；系统按组件查询；生命周期改成 tag；记录用 EasyEcs 4.4 |
| v10 | 链接随回复建立；参数绑定表；示例重写 |
| v11 | 客户端也是一个种类；Protocol 和 Server 分开；`LinkId` 由目标分配；第一条链接；记录一次保存一次加载 |
| v12 | 种类就是它的地址，也是实体上的组件；Protocol 不出现种类，目标由 `[Receive<K>]` 决定；`[Hosts<Client>]`；`AddNodes<T>`；去掉位置目录和迁移栅栏；写入中的占位；完整例子和生成的代码 |
| v13 | 生命周期改成发给自己的消息，异常按实体隔离；组件参数默认绑定到目标，`[Link]` 绑定到链接实体；生成的跳板复制参数，结构变更后引用不会失效；只有运行时能写种类组件；由我们为 `partial` 种类生成 `Key`，EasyEcs 不必改动；连接信息随信封传递，带上 `Confirmed` 之后才推送；`Attach` 幂等，失败时退避重试；路由记下 `Via`，目标重启后经 `LinkLost` 重新进入；顶号按全局递增的连接代数判断；`[Claims]` 由用户声明；`ReplyTo<T>` 带类型；期限向内层请求传递；删除也会触发保存；`MapSlot` 标 `[Transient]`，迁移后重新分配；分发表按种类划分，先暂存再切换；跳板生成在 `partial` 系统里 |

---

## 14. 待拍板

1. **生命周期事件改成发给自己的消息**（`Activated`、`Unlinked`、`RouteLost`、`LinkLost`、`Unloading`），用 `[Receive]` 处理，不再用 tag 加 `[ForEach]`。这样异常按实体隔离，顺序也和其他邮件一致。
2. **组件参数默认绑定到目标，`[Link]` 绑定到客户端的链接实体。**
3. **推送只经过链接**，不提供"按 `ClientId` 给任意客户端发消息"。要给某个玩家推送，发给他的 Player，由 Player 推送。
4. **`Self<K>` 这个名字**，也可以叫 `Me<K>`。
