# DaServer 新编程模型设计（草案 v10，未实现，迭代中）

本稿是完整的一份，不依赖之前的版本。各版本的审查结论与处理见第 16 节，需要拍板的点在第 17 节。

目标：写起来最简洁、最直观、最 ECS；每条消息从哪个实体、哪个节点，到哪个实体、哪个节点，都能从类型看出来；高性能，高并发。

---

## 0. 概念一览

**用户写的（7 个）：**

| 概念 | 写在哪 | 写法 |
|---|---|---|
| 种类与键 | Contracts | `struct Map : IKind<MapId>`；`record struct MapId(long Value) : IKey<Map>` |
| 消息 | Contracts | `[NinoType, From<Player>] record struct EnterMap(…) : IRequest<Map, bool>` |
| 组件 | Server | `record struct Grid(…) : IComponent`；要持久化的加 `[Persist]` |
| prefab | Server | `[Spawns<Map>] static readonly Prefab` |
| 服务器 | Server | `[Hosts<Map>] struct MapServer : IServer` |
| 系统 | Server.Logic | EasyEcs `EcsSystem`，含 `[Receive]` 和 `[ForEach]` 方法 |
| 系统组的频率 | Server | `[Every(50)] struct Simulation : ISystemGroup` |

**框架给的（用户只用不写）：**
- 地址：`ClientRef`（一个客户端），`Self<TKind>`（处理中的实体自己）；
- 回复：`From`、`ReplyTo`、`Answer<TReq, TRep>`；
- 链接：`Linked` 组件，`Link` 回复选项；
- 生命周期 tag：`Activated`、`Restored`、`Unloading`；`IdleAfter` 组件；
- 时间：`Due` 组件；
- 广播：`ClientGroup`。

---

## 1. 保证

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。**
   - 同一发送方发给同一实体的消息，按发送顺序到达，迁移期间也一样（第 11 节的栅栏）。
   - 不同目标之间没有全局顺序。客户端发给 Player 和发给 Map 的消息，谁先处理没有保证。
2. **消息处理只做判定，并把结果写成数据；每 tick 的重活由 `[ForEach]` 批量完成。**
3. **单向消息最多投递一次；请求一定有结果**：回复、拒绝（带原因）或超时。
4. **失败的结果是确定的**，见第 12 节。

---

## 2. 节点、服务器、shard、实体

```
服务器类型     节点 = 一个进程，只跑一种服务器      shard = 线程 + world        实体
GameServer ──▶ game-0, game-1, …          ──▶  按核数              ──▶  Player 42、Guild 3
MapServer  ──▶ map-0, map-1, …                                         Map 7 + 子实体 Avatar
Gateway    ──▶ gate-0, …                                               会话（每个客户端一个）
```

```csharp
// Server project
[Hosts<Player>, Hosts<Guild>] public struct GameServer : IServer;
[Hosts<Map>]                  public struct MapServer  : IServer;
[AcceptsClients]              public struct Gateway    : IServer;
```

- **节点就是服务器，就是一个进程，只跑一种服务器类型。** 同一种服务器部署几个节点，由 AppHost 决定：
  ```csharp
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<Gateway>("gate", replicas: 2).WithClientEndpoints();
  ```
  - 节点启动时校验服务器类型存在，写错就启动失败。
  - AppHost 构建时检查：每个被消息引用的种类，至少有一组节点承载它。
- 一个种类可以由多种服务器承载，例如小规模部署用的 `[Hosts<Player>, Hosts<Map>, AcceptsClients] AllInOne`。放置只在承载它的节点中选。

**种类、实例、服务器、节点的关系：**

```
种类 Map ──(由 [Hosts<Map>] 绑定)──▶ 服务器类型 MapServer ──(AppHost 部署)──▶ 节点 map-0, map-1, map-2
   │                                                                          ▲
   └── 实例：Map 1, Map 2, …, Map 9000（每个键一个实体）──(按键放置)───────────┘
        Map 7 此刻只在 map-1 上；Map 8 在 map-0 上
```

| 关系 | 数量 | 由谁决定 |
|---|---|---|
| 种类 → 服务器类型 | 一对一或一对多（`AllInOne`） | 服务器上的 `[Hosts<…>]`，编译期确定 |
| 服务器类型 → 节点 | 一对多 | AppHost 的 `replicas`，可以扩缩 |
| 种类 → 实例 | 一对多：每个键一个实例 | 第一条消息到达时激活 |
| 实例 → 节点 | **任一时刻恰好一个** | 放置：按键在承载该种类的节点中选；节点增减时迁移 |

- 种类绑定的是**服务器类型**，不是节点。种类不绑节点，是因为节点会增减：扩容时新节点马上分走一部分实例，宕机时它的实例在别的节点重新激活。
- 一个种类的实例分布在该服务器类型的所有节点上，但**同一个实例（同一个键）任一时刻只在一个节点上**。这一点由放置和防重复激活保证（现有的成员视图、记录版本和隔离机制）。
- 这和 Orleans 一样：grain 类型对应能承载它的 silo，每个 grain（类型加键）同时只有一个激活。也和 ET 一样：Map 这种 Scene 可以配在多个进程里，每张具体的地图只在其中一个进程里。
- 为什么种类不在 Contracts 里写自己在哪种服务器上（v8 曾写成 `IKind<MapId, MapServer>`）：
  - Contracts 也给客户端用，客户端不需要知道服务器怎么划分；
  - 绑定写在服务器一侧，同一份 Contracts 就能部署成"四种服务器"或"`AllInOne` 一种"，不用改 Contracts。
  - "Map 在哪种服务器上"可以在 `[Hosts<Map>]` 和 `wire.manifest` 的路由列里查到。
- **一个进程只跑一种服务器**：拆服务器就是为了独立扩缩、独立发布、故障隔离。本地开发由 Aspire 起多个进程，测试用 `TestCluster`，小规模部署用 `AllInOne`。
- **shard**：一个线程加一个 world。一个节点上，所有承载的种类共用各 shard 的 world。实体落在哪个 shard 由键的哈希决定。

---

## 3. 地址

**实体的地址是 `(种类, 键)`**，放置按键在承载该种类的节点中选，节点变化时迁移：

```csharp
// Contracts (netstandard2.1, no EasyEcs)
public readonly record struct MapId(long Value)    : IKey<Map>;
public readonly record struct PlayerId(long Value) : IKey<Player>;
public readonly partial struct Map    : IKind<MapId>;
public readonly partial struct Player : IKind<PlayerId>;
[Stateless] public readonly partial struct Pricing : IKind<Region>;   // any node, a local instance, never persisted
```

- 键是不超过 16 字节的非托管值类型，因为它要放进有值索引、会被保存的 `ActorKey` 组件。名字之类的字符串键，用它的 64 位哈希做键，原文放在组件里。
- 实体上表示地址的组件是框架的非泛型 `ActorKey`（种类 id + 键）。不用 `Key<MapId>` 这类泛型组件，因为 EasyEcs 的生成器不处理泛型组件，而且这样 Contracts 不碰 EasyEcs。
- `[Stateless]` 的种类不保存、不迁移，键只用来选实例，每个节点可以有自己的实例。
- 子实体（地图上的 Avatar、等待中的请求）不可寻址，跟父实体一起迁移。

**客户端的地址是 `ClientRef(网关节点, ClientId)`。**
- 客户端连在哪个网关，就在哪。`ClientId` 来自签名令牌。
- 会话是网关上代表这个客户端的实体，由框架在连接时激活。用户可以往会话上加系统，例如限流、登录后的初始化。
- `ClientRef` 只存在于 `Linked` 组件里（第 7 节）。所以客户端换网关时，框架只需更新各目标上的 `Linked`，不会有散落各处的过期地址。

**没有"无键种类"。**
- 每个节点或 shard 一份的状态（缓存、寻路网格）：单例；
- 无状态的工作者：`[Stateless]` 种类；
- 全局服务：固定键的普通种类，例如 `Matchmaker(Region)`。

---

## 4. 消息：接收方写在接口上，发送方写在特性上

```csharp
// Contracts
[NinoType, From<Client>] public record struct TravelTo(MapId Map)              : IRequest<Player, bool>;
[NinoType, From<Client>] public record struct MoveTo(Vector2 Target)           : IMessage<Map>;
[NinoType, From<Client>] public record struct Cast(SkillId Skill, PlayerId On) : IMessage<Map>;
[NinoType, From<Client>] public record struct Say(RoomId Room, string Text)    : IMessage<Room>;   // names one of several rooms
[NinoType, From<Player>] public record struct EnterMap(PlayerId Player, ClientRef Client) : IRequest<Map, bool>;
[NinoType, From<Player>] public record struct LeaveMap(PlayerId Player)        : IRequest<Map, bool>;
[NinoType, From<Map>]    public record struct Nearby(…)                        : IMessage<Client>;  // pushed to a client
```

- **接收方**：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。`TTo = Client` 表示推给客户端。
- **发送方**：`[From<K>]` 可以写多个，例如 `[From<Map>, From<Npc>]`。`Client` 表示客户端经它的会话发来。
- **检查靠生成的方法**：发送方法是 `Self<TKind>` 上的扩展方法，只为 `[From<TKind>]` 的消息生成：
  ```csharp
  // generated: only for [From<Player>] messages
  public static void Ask(this in Self<Player> self, MapId to, in EnterMap message, in Wait wait);
  ```
  - `Self<Player>` 由框架传给 `[Receive]`，用户造不出别的种类的 `Self`，所以发送方既伪造不了，也写不错。
  - 方向写错，就没有这个方法，编译不过。
- 不在消息里处理的系统（例如每 tick 的 `AoiSystem`）用 `world.As(mapKey).Send(...)`。返回的同样是 `Self<Map>`，但只能拿本 shard 上存在的实体去构造。
- HTTP、后台和测试通过 `ICluster` 发送，可以发任何消息，回复回到调用方。管理操作也走它，例如 `cluster.Disconnect(clientRef)` 踢人。
- `[From<Client>]` 和 `IMessage<Client>` 取代现在的 `[FromClient]`/`[ToClient]`。

---

## 5. 系统

| 方法 | 何时执行 | 可以做什么 |
|---|---|---|
| `[Receive]` | 每条消息一次，按到达顺序，在 shard 线程上 | 判定，写组件，创建或删除实体，发消息，回复 |
| `[ForEach]` | 每 tick（EasyEcs 生成 job，可 SIMD、可并行） | 读写组件。不发消息，不做结构变更，不写有索引的组件；需要时写进自己的缓冲，由后面的单线程系统处理 |

**`[Receive]` 的参数：**

| 参数 | 绑定到 |
|---|---|
| 消息（`in T`） | 这条消息 |
| `Self<TKind>` | 目标实体自己：强类型的键、它的 `Entity`、发送方法 |
| `Entity` 和组件（`ref`/`in`） | **接收实体**：客户端消息是该客户端在目标上的链接实体（第 7 节）；`Answer` 是等待实体（第 8 节）；其余是目标实体自己 |
| `From` | 发送方：`From.Client`（客户端消息时）或 `From.Key`（实体发来时） |
| `ReplyTo` | 要晚些回复时取（第 8 节） |
| 单例 | 本 shard 的单例 |

- **返回值就是回复**；返回 `void` 表示单向消息。
- **系统按组件查询，不按种类分组。** `Position` 可以同时在 Avatar 和 NPC 上，一个移动系统同时处理两者。
- **频率**：系统组上写 `[Every(50)]`，由 shard 循环按时调用 `Update<Simulation>()`；没写的组不按时间跑。
  - 这是 DaServer 的特性：EasyEcs 的组本身没有频率，由调用方决定什么时候 `Update<T>()`。
- **消息由框架的 `MailSystem` 执行**：
  - 它在 `Messages` 组里，每次唤醒都跑；
  - 按到达顺序逐条调用生成的分发表，所以 `[Receive]` 受 EasyEcs 调度的 `Enabled`、`SystemErrorPolicy`、`MeasureSystems` 管理。

---

## 6. 生命周期：tag 加系统

| 状态 | 进入条件 | 期间 | 用户写什么 |
|---|---|---|---|
| 加载中 | 第一条消息到达，实体还不存在 | 按 prefab 创建实体并加 `Loading` tag，开始异步读记录；邮件留在邮箱里 | — |
| 激活 | 记录到了（或者没有记录） | `LoadEntitiesInto` 装上记录，加 `Activated`（从记录恢复的再加 `Restored`）。`Activated` 的系统在 `MailSystem` 之前运行，之后由框架去掉 tag | `[ForEach, With<Activated>]`，例如发新手物品 |
| 运行 | `Activated` 去掉之后 | 邮件按到达顺序投递 | `[Receive]`、`[ForEach]` |
| 请求卸载 | 空闲超过 `IdleAfter`，或调用 `world.Unload(e)` | 加 `UnloadRequested`。有链接实体的不会因为空闲被卸载；主动卸载会先给每个链接的客户端发 `Unlinked` | — |
| 卸载中 | 链接都已断开 | 加 `Unloading`，跑对应的系统，再按 `[Persist]` 集合保存，最后销毁 | `[ForEach, With<Unloading>]`，如需要 |

- **谁能激活实体**：默认任何发给它的消息都能激活（Orleans 的虚拟 actor）。客户端只能发到链接过的目标，所以客户端不能借此乱建实体。种类可以写 `[ActivatedBy<EnterMap>]`，只允许这些消息激活，其余消息遇到未激活的实体时返回 `NoActor`。

---

## 7. 客户端：会话与链接

**规则一：客户端只连 `[AcceptsClients]` 的节点（网关）。** 连接时，框架用令牌里的 `ClientId` 激活会话实体。登录的鉴权在令牌里完成，不需要单独的登录消息。

**规则二：客户端发的消息先到它的会话，会话按路由转发。**
- 会话上有路由表 `DynamicBuffer<Route>`，每项是 `(种类, 键, 代数)`。
- 消息里有目标种类的键（如 `Say.Room`）：这个键必须在路由表里，否则拒绝。
- 消息里没有目标的键：用该种类的那一项（如 `MoveTo` 去当前地图）。
- 转发前先跑会话上的过滤系统，例如限流：令牌桶是会话上的组件，取代 `[Filter]`。
- 被拒绝时，客户端收到带原因的结果：`NotLinked`、`Busy`、`NoActor` 等。

**规则三：发给客户端，就是发给它的 `ClientRef`**，先到那个网关，再写到 socket。

**链接的两边：**

| 位置 | 数据 |
|---|---|
| 会话上 | `Route(种类, 键, 代数)` |
| 目标上 | 一个子实体（`ChildOf` 目标），带 `Linked(ClientRef, 代数)`。按 `(目标, ClientId)` 查找，不按 world 范围查，所以同一个 shard 上的两张地图互不干扰 |

**链接随回复建立，没有单独的链接消息：**
1. 目标在处理请求时创建链接实体，例如地图在 `EnterMap` 里创建 Avatar。
2. 回复客户端的那个实体在回复上带上链接：`return Reply.Linking(true, to: mapKey)`。
3. 会话收到这个回复时，先写路由（代数加一），再把结果写给客户端，然后给目标发 `LinkConfirmed(代数)`。

这样不需要任何顺序论证：路由在客户端看到回复之前就写好了，两者是同一个消息。回复丢失时，客户端收到超时；目标上的链接实体收不到确认，`Due` 期限到了就删除，不会留下孤儿。

**一个种类能链接几个目标**由种类声明：
- 默认 `[Links(Single)]`：建立新链接时，会话给旧目标发 `Unlinked(旧代数)`，例如换地图；
- `[Links(Many)]`：可以同时链接多个，例如聊天室、队伍频道。

**代数**防止新旧混淆：
- 目标只按代数相同的链接实体处理 `Unlinked` 和转发来的消息；
- 重新进入同一张地图时，旧 Avatar 的 `Unlinked` 删不掉新 Avatar；
- 找不到对应的链接实体时，目标回 `NotLinked`，会话删掉这条路由，客户端收到拒绝。不静默丢弃。

**换地图**：同一种类在建立新链接和删除旧链接之间，有一段时间同时存在于两张地图上。不能容忍时，由 Player 先问旧地图 `LeaveMap`，再问新地图 `EnterMap`。这是业务的选择，框架不强制。

**断开**：
- **客户端断线或被踢**：会话按路由表给每个目标发 `Unlinked`。目标框架给链接实体加 `Unlinking` tag，用户的 `[ForEach, With<Unlinking>]` 系统清理（例如广播 `Disappear`），下一次更新时框架销毁它。
- **网关宕机**：没人发 `Unlinked`。各节点在成员视图里看到网关离开时，自己给 `ClientRef` 指向它的链接实体加 `Unlinking`。

**顶号**：Player 发现已有别的客户端的链接实体时，调用 `self.Disconnect(old)`，旧会话随之关闭。

**换网关恢复**（计划内的重连，例如网关排空）：
- 旧网关把会话迁到新网关；
- 再给路由表里的每个目标发 `Moved(新 ClientRef)`；
- 目标更新 `Linked` 里的 `ClientRef`。
- `Moved` 从旧网关发出，排在旧网关此前转发的消息之后。

---

## 8. 等待别的实体的结果

```csharp
public sealed partial class TravelSystem : EcsSystem
{
    [Receive]                                          // the client asks the player; the player asks the map
    static void Travel(in TravelTo m, in Self<Player> self, in From from, ReplyTo reply) =>
        self.Ask(m.Map, new EnterMap(self.Key, from.Client), new Wait(new Travelling(m.Map), reply));

    [Receive]                                          // the answer is mail; trip is a component of the wait entity
    static void Entered(in Answer<EnterMap, bool> answer, in Travelling trip, in ReplyTo reply, World world) =>
        world.Reply(reply, answer is { Ok: true, Value: true }
            ? Reply.Linking(true, to: trip.Map)        // link with the reply: no separate link message
            : Reply.Of(false));
}
```

- `Ask` 创建一个等待子实体（`ChildOf` 发问方），带：
  - 用户的数据（`Travelling`）；
  - 调用 id（有值索引）；
  - `Due` 期限；
  - 可选的 `ReplyTo`。
- **调用 id 在全集群唯一**：发出节点的 id 加该节点的计数。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后回信会跟过去。
- 回信 `Answer<TReq, TRep>` 是发给发问方的邮件，按到达顺序处理。处理时，组件参数绑定到等待实体；处理完，框架删除等待实体。
- **超时**：到期时，框架投递一个 `Ok = false` 的 `Answer`。

**晚些回复，只有一种写法：`ReplyTo` 放在实体上，不复制。**
- 要晚些回复时，`[Receive]` 取 `ReplyTo` 参数，放进 `Wait`，或用 `world.Hold(reply, 数据)` 建一个等待实体。
- `world.Reply(reply, 值)` 先移除 `ReplyTo` 组件，再销毁等待实体。
- `ReplyTo` 是清理组件（`ICleanupComponent`）：等待实体在回复之前被销毁（发问方被卸载）时，清理系统恰好执行一次，失败地回复调用方，调用方不用等到超时。
- 迁移时，离开的那一份先移除 `ReplyTo` 再销毁，不会误发失败回复。

**一对多**：`self.AskAll(keys, message, wait)` 只建一个等待实体，回信逐个到达。全部到齐或超时后，投递一个汇总的 `Answers<TReq, TRep>`。

---

## 9. 时间

- `Due(long UnixMs)` 组件：绝对的 UTC 毫秒数，带有序值索引。用绝对时间，迁移和从记录恢复后照样正确。
- 框架的 `TimerSystem` 每 tick 取 `Range(long.MinValue, 现在)`：
  - 到期的投递它的到期邮件，然后移除 `Due` 或销毁实体，所以不会触发两次；
  - 恢复时已经过期的，下一 tick 立即触发。
- `world.After(delay, self, in message)`：建一个带 `Due` 和消息的子实体，到期时作为邮件投给父实体。等待请求的超时、链接确认的期限，都是同一个机制。
- 周期性的逻辑不用定时器，用 `[Every]` 的组。

---

## 10. 广播

- **客户端组 `ClientGroup`**：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出到各个 socket（ADR-029 改成"只发给有成员的网关"）。
  - 成员关系记在链接实体上（例如 Avatar 的 `Watching`）。链接实体进入 `Unlinking` 时，框架让它退出所有组。
- **实体组**：固定键的种类，成员表是它的组件，发给它的消息由它转发给成员。
  - 取代现在的 `Publish`。
  - 只适合小组（公会、队伍）：所有扇出都经过一个实体。大规模扇出用 `ClientGroup`。

---

## 11. 持久化、迁移、热更、升级（EasyEcs 4.4）

**一个实体的记录 = 根实体加它的全部子孙**（递归 `world.Children`，它只返回一层）：
- 框架在注册时建好两个集合：
  - `persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`；
  - `handoff = persist 加上 [Handoff] 组件`。
- 保存：根实体一组，子孙一组，各自 `world.SaveEntities(output, ids, persist)`。
- 恢复：
  - 根实体用 `world.LoadEntitiesInto(根的记录, [按 prefab 建好的实体])`；
  - 子孙用 `world.LoadEntities`。它们的 `ChildOf` 被改写成指向根实体。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`；加字段用 `VersionTolerant` 或 `Keyed` 格式。
- 只写改过的实体：`[Persist]` 组件实现 `ITrackChanges`，现有的 `ChangeSystem` 标 `Unsaved`；子孙的修改标在根实体上。
- 跟着地图走的东西必须是地图的子孙。例如 buff 是 Avatar 的子实体，再用关系指向施加者。

**迁移**：
- 用同样的两组保存，集合换成 `handoff`；新节点同样用 `LoadEntitiesInto` 加 `LoadEntities` 恢复。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 等待中的请求随实体迁移，回信按键寻址，会跟过来。
- `[Stateless]` 种类不迁移。

**迁移栅栏**（保证第 1 节的严格顺序）：
- 成员视图从 v 变到 v+1 时，每个节点在发往每个对端的链路上发一个 `Fence(v+1)` 标记。在这之前发的消息都排在它前面。
- 旧主人收到发送方 S 的标记时，S 在旧视图下发来的消息已经全部到达。旧主人把这些消息和这个标记一起转给新主人。
- 新主人对迁来的实体：S 的新消息先收下但暂不处理，等收到经旧主人转来的 S 的标记，或者 S 已经离开视图，再开始处理。
- 旧主人保留一段转发表，用来处理之后还用旧视图发来的消息。

**热更**：
- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有系统和生成的分发表，两者一起替换。
- 每个 shard 在两次 `Update` 之间：
  - 执行 `world.RemoveTypes(旧上下文)`；
  - 加入新的系统；
  - 把框架注册表里的分发表换成新的，否则旧上下文会被引用住、卸载不掉。
- 所有 shard 都移除之后，旧上下文才卸载。
- 组件、消息、种类都在 Server 和 Contracts 工程里，不热更。新增这些需要滚动升级。
- `[From<T>]`、`[Hosts<T>]` 这些泛型特性只在构建时由生成器读，运行时不靠反射，所以 Unity/Mono 上没有问题。

**滚动升级**（ADR-077）：
- `wire.manifest` 加上路由列：每条消息的接收方、发送方、回复类型。
- 新消息到了旧版本的节点，返回 `NotHandled`。
- `[Hosts]` 记在成员卡片里，放置按卡片计算，新旧版本混跑时结果一致。

---

## 12. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标所在节点宕机 | 请求超时或收到 `NoOwner`；实体在新主人处从最近的记录激活，最多丢失一个检查点间隔的改动 |
| 网关宕机 | 客户端断开，带令牌重连并重新进入。各目标看到网关离开视图，自行清理链接实体 |
| 发问方在回信前被卸载 | `ReplyTo` 的清理系统立即失败地回复调用方；回信到达时找不到调用 id，丢弃 |
| 单向消息在迁移中 | 有栅栏，不丢不乱；目标节点宕机时，在途消息最多丢失（最多一次） |
| 目标邮箱满 | 发送方收到 `Busy`；客户端消息的拒绝由网关回给客户端，不阻塞网关 |
| 客户端读得太慢 | 断开（ADR-035） |
| 回复丢失，链接没有确认 | 客户端超时；链接实体到期删除 |
| 客户端消息到了没有链接的目标 | 回 `NotLinked`，会话删掉这条路由，客户端收到拒绝 |

---

## 13. 完整示例：地图服

```csharp
// Server project: data
[Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;   // on a map
public record struct Avatar(PlayerId Player) : IComponent;                // on a link entity, child of its map
public record struct Visible : IComponent;                                // seen by viewers: avatars and NPCs
public record struct Position(Vector2 Value) : IComponent;
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges; // what viewers extrapolate
public record struct Destination(Vector2 Target) : IComponent;
public record struct Speed(float Value) : IComponent;
public record struct OnMap(int Map) : IComponent, IGroup { public int Group => Map; }   // iterate one map's entities
public record struct Health(int Value, int Max) : IComponent;
public record struct Shout(NearbyEvent Event);                            // an entity's own outbox element
public record struct BuffOf(EntityId Caster) : IRelation;                 // a buff is a child entity of its target

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16);
    public static readonly Prefab Avatar = new Prefab()
        .SetPosition(default).SetVelocity(default).SetDestination(default).SetSpeed(5)
        .SetHealth(100, 100).SetVisible().SetBuffer<Shout>();
}
```

```csharp
// Server.Logic project: behaviour
public sealed partial class EntrySystem : EcsSystem
{
    [Receive]                                             // the player asks to enter; the avatar is the client's link entity
    static bool Enter(in EnterMap m, in Self<Map> map, World world)
    {
        world.Instantiate(Prefabs.Avatar).Set(new Avatar(m.Player), new OnMap((int)map.Key.Value), map.LinkTo(m.Client));
        return true;                                      // the player replies to its client with Reply.Linking
    }
}

public sealed partial class MovementSystem : EcsSystem
{
    [Receive]                                             // bound to the sender's avatar (the link entity)
    static void Move(in MoveTo m, ref Destination destination) => destination.Target = m.Target;
}

[UpdateInGroup<Simulation>]                              // [Every(50)] struct Simulation : ISystemGroup
public sealed partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true)]                            // Velocity is written only when it changes (tracked)
    static void Steer(in Position p, in Destination d, in Speed s, ref Velocity v)
    {
        var next = Steering.Toward(p.Value, d.Target, s.Value);
        if (next != v.Value) v.Value = next;
    }
}

[UpdateInGroup<Simulation>, UpdateAfter<SteeringSystem>]
public sealed partial class IntegrateSystem : EcsSystem
{
    [ForEach]                                             // a batch over whole columns, SIMD
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Simd.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), clock.DeltaTime);
}

[UpdateInGroup<Simulation>, UpdateAfter<IntegrateSystem>]
public sealed partial class NearbySystem : EcsSystem
{
    [ForEach, With<Visible>, Changed<Velocity>]          // into the entity's own outbox: no cross-entity write in a job
    static void Moved(Entity e, in Velocity v, in Position p, ref DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(NearbyEvent.Moved(e.Id, p.Value, v.Value)));
}

[UpdateInGroup<Simulation>, UpdateAfter<NearbySystem>]
public sealed partial class AoiSystem : EcsSystem         // single-threaded: cells, joins, leaves and sends happen here
{
    public override void OnUpdate(World world) => Aoi.Run(world);   // per map group: bucket by cell, rewatch crossers, one frame per busy cell
}

[UpdateInGroup<Simulation>]
public sealed partial class LeaveSystem : EcsSystem
{
    [ForEach, With<Unlinking>, With<Visible>]            // the client left: viewers see it go
    static void Gone(Entity e, ref DynamicBuffer<Shout> outbox) => outbox.Add(new Shout(NearbyEvent.Disappear(e.Id)));
}
```

AOI 的规则：
- **格子**是 `AoiSystem` 每 tick 按位置算出来的分桶，不是组件。这样跨格不需要结构变更，`[ForEach]` 也不必写有索引的组件。
- **订阅**：每个会话订阅自己周围 3×3 格子的客户端组，只在跨格时变化。新看到的人先收到完整的 `Appear`。
- **发布**：任何"附近该看到"的事件，先写进事件发生者自己的发件箱；`AoiSystem` 按格子合并，每个有内容的格子发一帧。NPC 也有 `Visible`，所以同样会被广播。
- **按地图遍历**：用 `world.GroupBy<OnMap>()` 和 `InGroup`。一个 shard 上的所有地图共用一个 world，地图只有几百张，适合 `IGroup`。
  - `OnMap` 和 `ChildOf(map)` 各有用途：`ChildOf` 管生命周期和迁移，`OnMap` 管按地图遍历。
  - 两者都由 `EnterMap` 一次写入，不会不一致。
- **buff 是实体**：是 Avatar 的子实体（随目标死亡和迁移）；`BuffOf` 关系指向施加者，`Relations<BuffOf>().WhenTargetDestroyed(TargetPolicy.Remove)` 让施加者消失时关系自动去掉。

**MapTickBenchmark 实测**（BDN ShortRun，提交 4663e97，同一台机器）：

| 角色数 | 转向比例 | 传统做法（每个对象一个 Update，移动后立即广播） | ECS 流水线 | 比值 |
|---|---|---|---|---|
| 1,000 | 5% | 153.4 µs | 148.2 µs | 0.97 |
| 1,000 | 50% | 578.0 µs | 227.4 µs | 0.40 |
| 10,000 | 5% | 4,881.8 µs | 4,693.6 µs | 0.97 |
| 10,000 | 50% | 16,547.1 µs | 6,356.9 µs | 0.39 |

转向频繁时，ECS 流水线用时约为传统做法的四成，发出的帧数约为三分之一；两者都不分配内存。

---

## 14. 工程与生成的代码

| 工程 | 内容 | 依赖 |
|---|---|---|
| `Game.Contracts` | 种类、键、消息 | 不依赖 EasyEcs；客户端也引用（netstandard2.1） |
| `Game.Server` | 组件、prefab、服务器类型、系统组，五行的 Program.cs | Contracts、DaServer |
| `Game.Server.Logic` | 所有系统 | Server；放在可回收的上下文里，可以热更 |

- 想按服务器拆成更多工程（例如让网关镜像不带 Npgsql）也可以，框架不要求。
- 节点只注册它这种服务器所承载种类用到的系统。

**我们的生成器写：**
- 每种消息在 `Self<TKind>` 上的发送、询问、回复方法；
- 每个服务器的分发表（消息 id → `[Receive]` 方法），放在 Logic 程序集里，随热更替换；
- 会话的转发表；
- wire id 和 `wire.manifest`（含路由列）。

**我们的生成器不写：**
- `[ForEach]` 的 job，全部由 EasyEcs 生成；
- `IComponent`、`ITrackChanges`、`[NinoType]` 之类的声明。各个生成器看不到彼此的输出，所以这些由用户写，我们的分析器检查并提供一键修复。

**编译期检查：**
- 发送方向不对：没有对应的方法，编译失败；
- `[Receive]` 的消息必须发给这个工程的某个服务器所承载的种类；
- `[ForEach]` 里发消息、做结构变更、写有索引的组件，报错；
- `[From<Client>]` 消息的目标种类，必须有地方用 `Reply.Linking` 链接它；
- 组件不能放在 Logic 工程（热更要求，取代现在的 DAS013）；
- 键必须是不超过 16 字节的非托管类型；
- DAS022：结构变更之后不能继续用旧的 `ref`。

**指标**：每个种类的激活数、邮箱长度、消息处理耗时（来自 `MeasureSystems` 和 `MailSystem`）。

---

## 15. 和现在的代码比，删掉什么

`[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`、`Created`/`Loaded`/`Unloading` 回调、`UnloadAfterIdleSeconds`、`[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`、会话授权的 Known/Granted/Superseded，以及自研的记录格式。

---

## 16. 审查记录

**v8 → v9**（两个独立审查）：
- 地址只剩一种；`Client`、`Session`、`IAcceptsClients` 合并；
- 系统不按种类分组；
- 生命周期改成 tag 加系统；
- 会话与实体的关系统一成链接；
- 回信按键寻址；
- 不在 `[ForEach]` 里发消息；
- 格子不用 `IGroup`；
- 持久化改用 EasyEcs 4.4 的记录。

**v9 → v10**（第三次审查，必修 13 项）：

| v9 的问题 | v10 |
|---|---|
| `[Removed<Linked>]` 对销毁的实体不触发 | `Unlinking` tag 加系统，下一次更新再销毁 |
| `Linked(ClientId)` 的索引是整个 world 范围，同 shard 上的两张地图互相干扰；重进同一张地图时，旧的 `Unlinked` 会删掉新 Avatar | 按 `(目标, ClientId)` 查找；链接带代数 |
| 先链接再回复的顺序论证，在会话迁移或消息丢失时不成立；"不会双重存在"的说法不对；没有链接时静默丢弃 | 链接随回复一起到达，不需要顺序论证；如实写明双重存在的窗口和避免办法；回 `NotLinked` |
| 单链接和多链接互相矛盾 | 种类声明 `[Links(Single/Many)]` |
| 迁移栅栏的标记由旧主人发，挡不住还在用旧视图发送的发送方 | 每个发送方在视图变化时发标记，经旧主人转给新主人 |
| 带子实体的记录无法用 `LoadEntitiesInto` 加载 | 根实体和子孙分两组保存和加载 |
| `Due` 用节点本地的 tick；`Range` 两端都含，边界会触发两次 | 用绝对 UTC 毫秒；从最小值取到现在，触发后移除 |
| 示例里的 `IGroup.Group` 类型错，`[ForEach]` 写有索引的组件，buffer 按值传，Avatar 读不到地图的 `Grid`，没有速度来源，NPC 不广播，buff 关系写法错 | 示例重写 |
| EasyEcs 的组没有频率 | DaServer 的 `[Every]` 特性 |
| `MailSystem` 调用其他系统违反 EECS008；去掉了加载中状态 | `Activated` 的系统排在 `MailSystem` 之前；恢复加载中状态 |
| `[Receive]` 的参数绑定没有定义 | 第 5 节的绑定表，加上 `Self<TKind>` |
| 登录消息发给一个还没激活的会话 | 会话在连接时激活，鉴权在令牌里完成 |
| 钉住的会话需要一个没定义的位置目录 | 客户端地址带网关节点；`ClientRef` 只存在 `Linked` 里，换网关时用 `Moved` 更新 |
| 应修：`ReplyTo` 复制后无法正确清理；调用 id 迁移后可能重复；名字冲突；空闲卸载与链接的关系；`Any` 与全局唯一矛盾；键必须非托管；发送方可伪造；"七个概念"的说法不实 | 全部改了，见第 0、3、4、6、8 节 |

---

## 17. 需要拍板的点

1. **两种地址**：实体的地址是 `(种类, 键)`；客户端的地址是 `ClientRef(网关节点, ClientId)`，只存在 `Linked` 里。
2. **消息只在接口上写接收方，发送方用 `[From<…>]`**（可以写多个），由 `Self<TKind>` 上生成的方法检查；`ICluster` 可以发任何消息。
3. **系统不按种类分组**，按组件查询；频率用 `[Every]`。
4. **链接随回复建立**（`Reply.Linking`），带代数；种类声明单链接或多链接。
5. **严格顺序跨迁移也成立**：每个发送方发迁移栅栏，代价是迁移时新主人多等一会儿。
6. **节点 = 服务器 = 进程**，一个工程可以定义多种服务器类型。
