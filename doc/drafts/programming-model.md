# DaServer 新编程模型设计（草案 v8，未实现，迭代中）

v8 在 v7 的基础上解决一个问题：**谁（哪个实体，在哪个节点）把消息发给谁（哪个实体，在哪个节点），中间经过哪些服务器。**
v7 的 `IMessage<Client, Map>` 只写了两端：
- 没说客户端的消息要先经过网关；
- 没说 `Player` 在哪类服务器上；
- 没说一个键怎么找到节点。

v8 把这三件事都写进类型，由生成器和分析器检查，并为每个工程生成一张路线表。

系统、`[Receive]`、等待子实体、AOI 流水线、SIMD、热更，这些和 v7 相同，见第 8 节。

---

## 1. 同行怎么做

| | ET | Orleans | v8 |
|---|---|---|---|
| 进程的角色 | Scene 类型（Realm、Gate、Map、Location…），由配表指定哪个进程跑哪些 Scene | 默认每个 silo 跑所有 grain 类型；不同 silo 也可以加载不同的 grain 类型，放置只在兼容的 silo 中选 | **服务器类型**：一个节点就是一个服务器，服务器类型列出它承载的种类 |
| 实体的地址 | `ActorId = (进程, Fiber, InstanceId)`：钉在某个进程上，进程没了就失效 | `GrainId = (类型, 键)`：位置透明，由 grain 目录查到所在 silo | **两种地址**：定位地址 `(种类, 键)`，即 Orleans 的做法；钉住地址 `(节点, 本地 id)`，即 ET 的做法，只给会话用 |
| 能迁移的实体 | Unit 跨 Map 传送时，Location 服务器记录"Unit id → 当前 ActorId"，消息经 Location 转发 | grain 被重新激活到别的 silo，目录随之更新 | 定位地址的实体由放置决定节点，节点变化时迁移，消息跟着过去（现有机制） |
| 客户端到地图 | 客户端只连 Gate。发给地图的消息是"位置消息"：Gate 上的 Session 按 Unit id 转给 Unit 当前所在的 Map | 客户端连某个 silo 的网关端口，调用 grain 引用，网关 silo 转发 | 客户端只连网关，消息先到网关上的**会话实体**，会话按自己的**链接**转发（第 4 节） |
| 地图到客户端 | Unit 上存着 Gate 会话的 ActorId，按它发；AOI 广播是对每个观察者各发一次 | grain observer 或 stream | 实体存会话的钉住地址；AOI 广播按客户端组发，每个网关节点一帧，由网关扇出 |
| 消息方向 | 协议名带方向：`C2G_`、`C2M_`、`M2C_`、`G2M_` | 接口方法，不分方向 | `IMessage<发送方种类, 接收方种类>`，方向由编译期检查 |

v8 吸收 ET 的两点：每种服务器承载什么是明确的，消息方向是明确的。
同时保留 Orleans 的两点：键就是地址，放置交给框架，不需要配表写死"哪个进程跑哪张地图"。

---

## 2. 服务器（节点）、shard、实体

```
服务器类型          节点 = 服务器 = 一个进程            shard (线程 + world)        实体
  GameServer  ──▶  game-0, game-1, …        ──▶   每个节点按核数配置   ──▶   Player 42
  MapServer   ──▶  map-0, map-1, …                                            Map 7（地图）+ 子实体 Avatar
  Gateway     ──▶  gateway-0, …                                               Session（每条客户端连接一个）
```

- **节点就是服务器，就是一个进程。** 一个节点只跑一种服务器类型。部署时，同一种服务器起几个节点，由平台和 AppHost 决定。用户代码里不出现节点名。下文的 `map-0`、`game-1` 只是"某个 MapServer 节点"的简称。
- **服务器类型**列出它承载的种类，写在服务器工程里。这是类型，不是拓扑字符串：
  ```csharp
  [Hosts<Player>, Hosts<Guild>] public partial struct GameServer : IServer;
  [Hosts<Map>]                  public partial struct MapServer  : IServer;
  public partial struct Gateway : IServer, IAcceptsClients;        // hosts the framework's Session kind
  ```
  一个种类可以由多种服务器承载，例如小规模部署用的 `[Hosts<Player>, Hosts<Map>] AllInOne`。放置只在承载它的节点中选，与 Orleans 的"兼容 silo"相同。
- **shard**：节点内的一个线程加一个 world。一个实体只在一个 shard 上，由键的哈希决定。
- **种类**写在 Contracts 里，只有地址，不提服务器：
  ```csharp
  public readonly partial struct Player : IKind<PlayerId>;   // located: (Player, key) -> a node whose server hosts Player
  public readonly partial struct Map    : IKind<MapId>;
  ```
  客户端和 Contracts 都不需要知道服务器怎么划分；划分是服务器工程和部署的事。

**一个实体在哪里，由类型和键决定：**

| 地址 | 例子 | 服务器 | 节点 | shard |
|---|---|---|---|---|
| 定位地址 `(种类, 键)` | `PlayerId(42)` | 承载 `Player` 的服务器（`GameServer`） | 放置：在这些服务器的节点中按键选（现有的成员视图和放置） | 节点内按键哈希 |
| 钉住地址 | `SessionId(node, shard, local)` | `Gateway` | 键里带着节点，客户端连在哪就在哪 | 键里带着 shard |
| 子实体 | 地图上的 `Avatar` | 跟父实体走 | 跟父实体走 | 跟父实体走，不能单独寻址 |

所以"Player 在哪个服务器"有了明确答案：在承载它的 `GameServer` 上，具体哪个节点由键 42 经放置决定，生成的路线表（第 6 节）里写着。地图同理在 `MapServer` 上。

---

## 3. 消息：发送方种类、接收方种类

```csharp
// Contracts
public record struct Login(string Token)               : IRequest<Client, Session, LoginResult>;
public record struct TravelTo(MapId Map)               : IRequest<Client, Player, bool>;
public record struct MoveTo(Vector2 Target)            : IMessage<Client, Map>;
public record struct Cast(SkillId Skill, PlayerId On)  : IMessage<Client, Map>;
public record struct Attach(SessionId Session)         : IMessage<Session, Player>;
public record struct EnterMap(PlayerId Player, SessionId Session) : IRequest<Player, Map, bool>;
public record struct NearbyBatch(...)                  : IMessage<Map, Client>;
public record struct Wallet(int Gold)                  : IMessage<Player, Client>;
```

- `Client` 不是种类，它代表"客户端，经由它的会话"。
- 每条消息的路径由两端的服务器类型推出来，规则只有三条（第 4 节）。
- 发送方检查：只有 `Player` 组的系统能发 `IMessage<Player, …>`。在别的组里发，编译报错。
- 接收方检查：`IMessage<Client, Map>` 只能由 `Map` 组的 `[Receive]` 接收。

---

## 4. 路径的三条规则

**规则一：服务器之间直连。** 集群内任意两个节点之间有一条有序链路（现在的 PeerLink）。`Player@game-1 → Map@map-0` 是一跳，不经过别的服务器。

**规则二：客户端只连网关，客户端的消息一律先到它的会话。**
- 客户端连上某个网关节点时，框架在那个节点上创建一个 `Session` 实体，地址是钉住的 `SessionId(gateway-0, shard 2, 1734)`。
- 目标是 `Session` 的消息（如 `Login`），由会话自己的 `[Receive]` 处理。
- 目标是其他种类的消息（如 `MoveTo : IMessage<Client, Map>`），会话按自己身上的**链接** `Link<Map>` 转发。链接就是一个组件，里面存着目标的键：
  ```csharp
  public record struct Link<TKind>(TKind.Key Target) : IComponent;   // on the Session entity, one per target kind
  ```
- 会话没有对应的链接时，消息被拒绝（`NotLinked`），客户端收到错误，不会被静默丢弃。
- 转发由生成器生成，用户不写。

**规则三：发给客户端，就是发给会话。**
- 实体在组件里存会话的钉住地址，例如 `Avatar.Session`、`Player` 的 `Attached`。
- 消息先到会话所在的网关节点，再由网关写到 socket。
- 发给客户端组（AOI）时，每个有成员的网关节点收到一帧，由网关扇出到各个 socket。

由此每条消息的每一跳都是确定的：

| 消息 | 路径 |
|---|---|
| `Login : IRequest<Client, Session, …>` | 客户端 → `Session@gateway-0`（会话处理并回复） |
| `TravelTo : IRequest<Client, Player, …>` | 客户端 → `Session@gateway-0` →（`Link<Player>`）→ `Player 42@game-1`，回复原路返回 |
| `MoveTo : IMessage<Client, Map>` | 客户端 → `Session@gateway-0` →（`Link<Map>`）→ `Map 7@map-0` |
| `EnterMap : IRequest<Player, Map, …>` | `Player 42@game-1` → `Map 7@map-0`，回复返回 `Player 42` |
| `NearbyBatch : IMessage<Map, Client>`（组） | `Map 7@map-0` → 每个有观察者的网关节点一帧 → 各 socket |
| `Wallet : IMessage<Player, Client>` | `Player 42@game-1` → `Session@gateway-0` → socket |

**顺序**：
- 同一个客户端发出的消息，经同一个会话、同一条链路转发，到达目标时保持发送顺序。
- 不同发送方之间，按到达目标实体的顺序处理（v7 原则一）。
- 链接改变（换地图）之后发出的消息去新地图。改变之前已在路上的消息仍到旧地图，旧地图找不到这个会话的 Avatar 就丢弃。这和 ET 的 Location 换位时的语义一致。

---

## 5. 完整流程：登录、进地图、移动、看到别人

```
① 客户端 TCP/WS 连 gateway-0         框架：创建 Session@gateway-0/s2（钉住地址）
② Login(token)  客户端 → Session      会话系统：校验签名令牌，得到 PlayerId 42
                                       Link<Player>(42) 写到会话上；Send(Attach(session)) 给 Player 42
③ Attach        Session → Player 42@game-1（放置决定 game-1）
                                       Player：首条消息到达时按 prefab 创建或从存档恢复；把会话存进 Attached
④ TravelTo(7)   客户端 → Session →(Link<Player>)→ Player 42
                                       Player：创建等待子实体 Travelling，Ask(Map 7, EnterMap(42, session))
⑤ EnterMap      Player 42@game-1 → Map 7@map-0
                                       Map：创建 Avatar 子实体（存着会话），Link(session, map 7)，回复 true
⑥ Link 生效     Map 7 → Session（框架消息）  会话写上 Link<Map>(7)
⑦ Answer        Map 7 → Player 42 的等待子实体 → 回复客户端 TravelTo = true
⑧ MoveTo(x,y)   客户端 → Session →(Link<Map>)→ Map 7    Map：写 Destination
⑨ 每 tick       Map 7 的流水线：移动、格子、AOI；NearbyBatch → 观察者所在的网关 → socket
```

第 ⑥ 步必须"先链上，再回复"，否则客户端可能先收到 `TravelTo = true`，紧接着发出的 `MoveTo` 却因为会话还没有 `Link<Map>` 而被拒绝。
- 两条消息走的链路不同：链接消息是 map-0 → gateway-0，回复是 map-0 → game-1 → gateway-0。跨链路没有顺序保证。
- 所以 `world.Link(session, map)` 是一次 Ask：Map 的 `EnterMap` 处理先把"进图中"写成等待子实体，等会话确认链接后再回复 Player。
- 框架提供的 `Link` 自带这一步：`[Receive]` 返回 `Linking<bool>`，框架在会话确认后才把值作为回复发出。确认失败（会话已关闭）时回复失败，并删掉刚建的 Avatar。

**换地图**：Player 向新地图请求 `EnterMap`，成功后新地图链接会话（覆盖 `Link<Map>`），再通知旧地图 `LeaveMap` 删掉 Avatar。

**会话断开**：网关销毁 Session 实体。`Session` 种类的清理组件给它链接的每个目标发 `SessionClosed`，Player 和 Map 各自清理。这是 `ICleanupComponent`，恰好执行一次。

---

## 6. 生成的路线表

每个服务器工程构建时生成 `routes.manifest`（按服务器类型分节），与 `wire.manifest` 一起提交。review 时一眼可以看到路径：

```
# server MapServer  (Game.Server)
hosts   Map   key MapId   placement: hash over MapServer nodes
receive MoveTo       Client  -> Map      via Session.Link<Map>
receive Cast         Client  -> Map      via Session.Link<Map>
receive EnterMap     Player  -> Map      direct (GameServer -> MapServer)   reply bool
send    NearbyBatch  Map     -> Client   via Session (group, one frame per gateway node)
send    Link<Map>    Map     -> Session  framework
```

构建期检查：
- 每条 `IMessage<A, B>`：接收方 B 的组里必须有 `[Receive]`，并且编译在某个服务器工程里（否则这条消息没人收）；发送方必须是 A 组里的系统。
- `IMessage<Client, X>` 且 X 不是 `Session`：某处必须调用 `Link(session, X)`，否则客户端永远发不到 X。
- 一个系统只能用同一种服务器上的种类的组件（不同服务器的实体不在同一个 world 里）。
- 客户端只能连 `IAcceptsClients` 的服务器。AppHost 把客户端端口只开在这些节点上（现有 `WithClientEndpoints`）。

---

## 7. 无键种类（取代 `[Service]`）

有些东西每个节点一个，例如网关上的令牌校验缓存，或地图服上的寻路网格缓存。

```csharp
public readonly partial struct PathCache : IKind<MapServer>;   // no key: one per node of MapServer, on shard 0
```

- 同节点的实体用 `world.Send<PathCache>(…)` 发给它。
- 要发给"任意一个 MapServer 节点上的它"，用 `world.SendAny<PathCache>(…)`，放置选一个节点。
- 跨节点发给"某个特定节点上的它"不提供：节点名不进代码。

---

## 8. 与 v7 相同的部分（简述）

- **两条原则**：
  - 每个实体按到达顺序逐条处理消息，实体之间并行；
  - `[Receive]` 只做判定并把结果写成数据，每 tick 的重活交给 `[ForEach]`（可用 SIMD、并行）。
- **种类就是系统组**：`[UpdateInGroup<Map>]` 的系统只看到 Map 种类的实体（`[With<MapId>]`）。子实体（Avatar）有自己的组（`[With<Avatar>]`）。
- **等待别的实体的结果**：等待的事情是一个 `ChildOf` 子实体。回信 `Answer<TReq, TRep>` 投递给它，按发问方邮箱的顺序处理。子实体带 `ICleanupComponent`，发问方被销毁时，调用方立即收到失败回复。
- **回复**：`[Receive]` 的返回值就是回复。要晚些再回复时，把 `From` 存进组件，之后调用 `world.Reply`。
- **AOI**：
  - 事件写进事件发生者所在格子的发件箱；
  - 每个会话订阅自己周围 3×3 格子的客户端组；
  - tick 末尾，每个有内容的格子发一帧。
- **每 tick 流水线**：Receive → Steering → Movement（SIMD）→ Cells → Buffs → Aoi → Nearby → Flush。
- **生命周期**：
  - 首条消息到达时按 prefab 创建，有存档就恢复；
  - `Lifetime` 组件决定空闲回收还是常驻；
  - `Despawn` 标签立即回收；
  - 迁移时连同子实体和邮箱一起走。
- **热更**：
  - Logic 工程放在可回收的 `AssemblyLoadContext` 里；
  - shard 在两条消息之间执行 `RemoveTypes`，换上新系统和新的分发表；
  - Logic 工程里不允许声明组件、消息、种类。
- **生成的代码**：每个种类一个按消息 id 跳转的分发器；客户端消息的会话转发；路线表。`[ForEach]` 全部由 EasyEcs 生成。
- **MapTickBenchmark 实测**（BDN，同一台机器）：
  - 5% 的角色转向时，两种做法持平（0.97 倍）；
  - 50% 的角色转向时，ECS 用时约为传统做法的 0.4 倍，帧数约为三分之一。

---

## 9. 去掉的东西

`[Actor]`、`[Handle]`、`[Service]`（改用无键种类）、`Context`、`Answered`、`ReplyTo`、`ReplyLater`、`Created`/`Loaded`/`Unloading`（改为 `[Receive]` 接收 `Spawned`/`Restored`）、`UnloadAfterIdleSeconds`（改为 `Lifetime` 组件），以及会话授权的 Known/Granted/Superseded（由 `Link<T>` 和 Session 实体取代）。

---

## 10. 自查发现的问题与处理

1. **Contracts 不能依赖 EasyEcs，但种类和键要写在 Contracts 里。**
   - Contracts 同时给客户端用（netstandard2.1），规范要求 Abstractions、Client、Contracts 不依赖 EasyEcs。v7 里的 `MapId : IComponent, IIndexed<MapId>` 和种类上的 `Prefab`、`[With<MapId>]` 都违反了这一点。
   - 处理：拆成两半。
     - Contracts 只写地址：`struct Map : IKind<MapId>`，`record struct MapId(long Value) : IKey<Map>`，不引用 EasyEcs。
     - 服务器的 Components 工程写实体的样子：`[Prefab<Map>] static class MapPrefab { … }`，以及系统组 `[With<Key<MapId>>] partial struct MapSystems : ISystemGroup`。
     - 键在实体上的组件是框架的泛型组件 `Key<TKey> : IComponent, IIndexed<Key<TKey>>`，用户不用写。
   - **需要 EasyEcs 确认**：EasyEcs 的生成器能否正确处理在框架程序集里定义、在用户程序集里以 `Key<MapId>`、`Link<Map>` 形式使用的泛型组件（成员生成、索引、job 访问集）。不能的话，就由用户在 Components 工程里写一行 `record struct MapKey(MapId Id) : IComponent, IIndexed<MapKey>`，并由我们的分析器检查。我会把这一条加进 EasyEcs 需求清单。
2. **"先链上，再回复"跨链路没有顺序保证。** 已改为 Ask 加确认，见第 5 节。
3. **`Session` 是框架的种类，但 `Gateway` 是用户自己的服务器类型。**
   - 处理：`Session` 由框架声明，承载在所有 `IAcceptsClients` 的服务器上。
   - 用户可以往 `Session` 组里加系统，例如登录，和其他种类一样。
4. **玩家重新加载后不知道自己的会话**（分布式审查的 should-fix）。
   - 处理：会话地址 `Attached` 是 `[Persist]` 组件，随记录保存。
   - 会话已不在时，发给它会失败，Player 的清理系统收到 `SessionClosed` 后自行处理。

## 11. 工程、进程、节点：一个节点就是一种服务器的一个进程

### 一个进程要不要跑多种服务器？调研结论：不要

| 同行 | 一个进程承载什么 | 一个进程跑多种角色，用在什么场合 |
|---|---|---|
| ET | 一个进程可以跑多个 Scene（Realm、Gate、Map…），由配表决定 | 开发时"单进程启动"方便调试；上线按配表拆成多进程 |
| Orleans | 一个 silo 是一个进程，默认承载所有 grain 类型；不同 silo 可以加载不同的 grain 类型 | 测试时一个进程里跑多个 silo（TestCluster）；生产一进程一 silo |
| Akka.NET | 一个节点可以同时有 0 个或多个角色 | 多用于小集群和开发；大集群按角色分开部署，各自扩缩 |
| Kubernetes 惯例 | 一个容器一个进程，一种工作负载一个 Deployment | 不同角色合在一个进程里会失去独立扩缩、独立发布和故障隔离 |

结论：
- **一个进程跑多种服务器，在生产环境里没有需求。** 拆成不同服务器，就是为了独立扩缩、独立发布和故障隔离（地图服的重 tick 不拖累登录，地图服崩了不连带网关）。合进一个进程，这三点都没有了。
- 它的价值只在开发调试和极小规模部署：
  - 本地调试：Aspire 本来就在本机起多个进程，一键启动、一起调试，不需要合进一个进程。
  - 测试：`TestCluster` 已经能在一个测试进程里跑多个节点。
  - 极小部署：定义一个承载所有种类的服务器类型（`AllInOne`），部署一个节点即可。它仍然是一个节点只跑一种服务器，不需要新机制。
- 所以 v8 采用**节点 = 服务器 = 进程**：一个节点只跑一种服务器类型。一个进程里不放多个节点，也不放多种服务器。

### 工程

工程数跟服务器种类数无关，可以一个工程定义多种服务器类型，部署时由 AppHost 选择每组节点跑哪一种：

| 工程 | 内容 | 依赖 |
|---|---|---|
| `Game.Contracts` | 消息、键、种类的地址 | 不依赖 EasyEcs；客户端也引用（netstandard2.1） |
| `Game.Server` | 组件、prefab、系统组、各服务器类型（`[Hosts<…>] … : IServer`），五行的 Program.cs | Contracts、DaServer |
| `Game.Server.Logic` | 所有 `[Receive]`、`[ForEach]` 系统 | Server（数据）；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

- Logic 单独成一个工程，是为了热更：可回收的上下文只能整个程序集卸载。
- 想按服务器拆成多个工程也可以，例如让网关的镜像不带 Npgsql。框架不禁止，只是不再要求。
- 节点只实例化它这种服务器承载的种类的系统：同一个程序集里别的服务器的系统不注册、不 tick。

### 部署

```csharp
// AppHost: types, not strings
cluster.AddNodes<GameServer>("game", replicas: 3, shards: 4);
cluster.AddNodes<MapServer>("map",   replicas: 4, shards: 8);
cluster.AddNodes<Gateway>("gate",    replicas: 2).WithClientEndpoints();
```

- AppHost 把服务器类型写进节点的配置。节点启动时检查这个类型在程序集里存在；写错时立即启动失败，不会出现"静默什么都不承载"。这正是 ADR-017 原本要防的问题。
- AppHost 构建时还会检查：每个被消息引用的种类，至少有一组节点承载它。运行时，种类没有节点承载时，发送方立刻收到 `NoOwner`。

### 线程

- 每个 shard 独占一个线程，不占线程池。一个节点只跑一种服务器，所以 shard 数直接按核数取默认值，可在 AppHost 里改。
- 线程池只跑 IO 完成和 HTTP 平面，不需要随服务器调大。框架启动时把最小线程数设为核数，避免突发时线程池缓慢扩容。

### 对现有约定的影响

- ADR-017 改写为"节点跑部署指定的一种服务器类型；服务器类型列出承载的种类；配置由 AppHost 从类型生成，启动时校验"。
- DAS013（组件必须放在 `X.Components` 工程）改为"组件不能放在 Logic 工程"，这是热更的要求。
- mmo 示例的服务端从 6 个工程收敛为 Server、Server.Logic 两个（加上 Contracts）。部署仍是 Login、Game、Map、Gateway 四组节点。
- `TestCluster` 不变：一个测试进程里跑多个节点，每个节点指定一种服务器类型。

## 12. 需要你拍板的点

1. **服务器类型列出它承载的种类**（`[Hosts<Map>] struct MapServer : IServer`），写在服务器工程里；Contracts 里的种类只有地址。
   - 推荐这样做：客户端不知道服务器怎么划分；"Player 在哪"从服务器类型和路线表就看得到。
   - 代价：ADR-017 要改写。仍然没有拓扑字符串。
2. **客户端消息靠会话上的 `Link<T>` 转发**（ET 的 Location 方式，但链接放在会话上，不需要单独的 Location 服务器）。
   - 推荐这样做：网关只查一个组件，O(1)，链路有序。
   - 另一种做法是客户端在每条消息里带目标键（Orleans 的引用方式），但这样客户端能随便指定发给哪张地图，需要额外的授权检查。
3. **会话是钉住地址，网关节点挂了会话就没了**，客户端重连后重新登录或恢复（ADR 已有的签名令牌恢复）。
   - 推荐这样做：会话本来就跟连接同生共死。
4. **地图的放置**：按键哈希到 MapServer 的节点（现状）。
   - 是否还需要"某张地图指定放在某个节点"？推荐不提供：这是拓扑，不进代码。真需要时，用 AppHost 起多个 MapServer 服务器类型（例如 `CityServer`、`DungeonServer`）来分。
5. **节点 = 服务器 = 进程，一个节点只跑一种服务器类型**；一个工程可以定义多种服务器类型，由 AppHost 选择每组节点跑哪一种（第 11 节）。
   - 推荐这样做：独立扩缩、独立发布、故障隔离，与 Orleans、Akka、Kubernetes 的惯例一致。开发用 Aspire 起多个进程，测试用 TestCluster，极小部署用一个承载所有种类的服务器类型。
   - 代价：ADR-017 和 DAS013 要改写，mmo 示例的服务端收敛为两个工程。
