# DaServer 新编程模型设计（草案 v11，未实现，迭代中）

目标：写起来最简洁、最直观、最 ECS；从类型就看得出每条消息从哪个实体发到哪个实体，以及它们落在哪种服务器上；高性能，高并发。

本稿是完整的一份。各版本改了什么见第 13 节，待拍板的点在第 14 节。

---

## 0. 一页看懂

```
Protocol 工程 (客户端和服务端共用)          Server 工程                              Server.Logic 工程 (可热更)
  种类和键：Map, MapId, Player …             组件、prefab                             系统：
  客户端收发的消息：                          服务器类型：[Hosts<Map>] MapServer       [Receive] 一条消息执行一次
    [From<Client>] MoveTo : IMessage<Map>     服务器之间的消息：                       [ForEach]  每 tick 批量执行
    [From<Map>] Nearby : IMessage<Client>       [From<Player>] EnterMap : IRequest<Map, bool>
```

**用户写的：** 种类和键、消息、组件、prefab、服务器类型、系统。

**框架给的：**

| 名字 | 是什么 |
|---|---|
| `Client` | 每个连上来的客户端在服务端的实体，是一个框架内置的种类，键是 `ClientId` |
| `Self<TKind>` | 正在处理的实体自己：强类型的键，以及发消息的入口 |
| `ReplyTo`、`Answer<TReq, TRep>`、`Pending<T>` | 晚些回复，以及等别人的结果 |
| `Linked` | 客户端在某个目标上的链接实体，例如地图上的 Avatar |
| `Activated`、`Restored`、`Unloading`、`Unlinking` | 生命周期 tag |
| `IdleAfter`、`Due` | 空闲回收、定时 |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

---

## 1. 保证

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。**
2. **同一个发送方发给同一个目标的消息，按发送顺序处理。**
   - 目标迁移期间也成立（第 10 节的迁移栅栏）。
   - 发送方自己迁移后，算作新的发送方，新旧两段之间不保证顺序。
   - 发给不同目标的消息之间没有顺序保证。例如客户端发给 Player 的和发给 Map 的，谁先处理不确定。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。
4. **每种失败的结果都是确定的**，见第 11 节。

---

## 2. 种类、服务器、节点

```csharp
// Server project
[Hosts<Player>, Hosts<Guild>] public struct GameServer : IServer;
[Hosts<Map>]                  public struct MapServer  : IServer;
public struct Gateway : IServer, IAcceptsClients;          // hosts the framework's Client kind
```

| 关系 | 数量 | 由谁决定 |
|---|---|---|
| 种类 → 服务器类型 | 一般一对一；小规模部署可以一个服务器承载所有种类 | 服务器上的 `[Hosts<…>]` |
| 服务器类型 → 节点 | 一对多 | AppHost 部署几份 |
| 种类 → 实例 | 每个键一个 | 第一条消息到达时激活 |
| 实例 → 节点 | **任一时刻恰好一个** | 按键在承载该种类的节点中放置，节点增减时迁移 |

- **节点 = 一个进程 = 一种服务器。** 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程不跑多种服务器。本地开发由 Aspire 起多个进程；测试用 `TestCluster`；小规模部署定义一个 `AllInOne : IServer, IAcceptsClients`，让它承载所有种类。
- **种类绑定服务器类型，不绑节点**：节点会增减，实例跟着迁移，这和 Orleans、ET 的做法一样。同一个键任一时刻只有一个实例，由现有的放置、记录版本和隔离机制保证。
- **shard** 是节点里的一个线程加一个 world。节点承载的所有种类共用 shard 的 world；一个实例落在哪个 shard，由键的哈希决定。
- **AppHost：**
  ```csharp
  cluster.AddNodes(Servers.GameServer, "game", replicas: 3);
  cluster.AddNodes(Servers.MapServer, "map", replicas: 4, shards: 8);
  cluster.AddNodes(Servers.Gateway, "gate", replicas: 2).WithClientEndpoints();
  ```
  - `Servers.*` 是生成的强类型句柄，生成器读的是服务端工程提交的 `wire.manifest`。AppHost 不引用服务端程序集。
  - `WithClientEndpoints()` 只出现在 `IAcceptsClients` 服务器的句柄上，给别的服务器调用会编译不过。
  - 接受客户端的服务器没开客户端端口，AppHost 构建时警告。
  - 消息引用的种类如果没有任何节点承载，AppHost 构建时报错。
  - 节点启动时还会再校验一遍。
- **同一份 Protocol 可以部署成不同的服务器划分**：哪个种类在哪种服务器上，写在服务端的 `[Hosts]` 里，不写在 Protocol 里，所以客户端不知道也不需要知道。

---

## 3. 地址：只有一种，`(种类, 键)`

```csharp
// Protocol (netstandard2.1, no EasyEcs)
public readonly record struct MapId(long Value)    : IKey<Map>;
public readonly record struct PlayerId(long Value) : IKey<Player>;
public readonly partial struct Map    : IKind<MapId>;
public readonly partial struct Player : IKind<PlayerId>;
```

- **键**是不超过 16 字节的非托管值类型，因为它存在实体的 `ActorKey` 组件里，要建值索引、要保存。字符串键用 64 位哈希代替，原文另存在组件里。
- **客户端也是 `(种类, 键)`**：种类是框架的 `Client`，键是 `ClientId`（来自签名令牌）。
  - 这个实体由框架激活在客户端连着的那个 `IAcceptsClients` 节点上。
  - 发给客户端的消息写成 `self.Send(clientId, msg)`，和发给其他实体的写法一样。
  - `Client` 实体在哪个节点，记在框架的位置目录里：目录按 `ClientId` 哈希放在接受客户端的节点上，发送方会缓存。客户端换节点时，目录随之更新，旧节点把消息转发过去。用户只看到 `ClientId`。
- **`[Stateless]` 种类**：不保存、不迁移，每个节点可以有自己的实例，键只用来选实例。用于令牌校验、计价这类无状态的工作。
- **子实体不可寻址**，例如地图上的 Avatar、等待中的请求。它们跟父实体一起保存、一起迁移。
- **没有"无键种类"**：每个节点一份的状态用单例；无状态的工作用 `[Stateless]`；全局服务用固定键，例如 `Matchmaker(Region)`。

---

## 4. 消息

```csharp
// Protocol: what clients send and receive
[NinoType, From<Client>] public record struct TravelTo(MapId Map)              : IRequest<Player, bool>;
[NinoType, From<Client>] public record struct MoveTo(Vector2 Target)           : IMessage<Map>;
[NinoType, From<Client>] public record struct Cast(SkillId Skill, PlayerId On) : IMessage<Map>;
[NinoType, From<Client>] public record struct Say(RoomId Room, string Text)    : IMessage<Room>;   // names which room
[NinoType, From<Map>]    public record struct Nearby(…)                        : IMessage<Client>;

// Server: between servers, never shipped to clients
[NinoType, From<Player>] public record struct EnterMap(PlayerId Player, ClientId Client) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(PlayerId Player)        : IMessage<Map>;
[NinoType, From<Client>] public record struct Attach(ClientId Client)          : IRequest<Player, LinkId>;
```

- **接收方写在接口上**：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。
- **方向由生成的方法检查**：每条消息只在它的发送方的 `Self<K>` 上生成发送方法。
  - 例如 `self.Ask(mapId, in enterMap, in pending)` 只存在于 `Self<Player>` 上。方向写错，这个方法就不存在，编译不过。
  - `Self` 由框架传入，用户造不出来，所以发送方也冒充不了。
- **Protocol 里只放客户端收发的消息**和它们用到的键、种类名。服务器之间的消息放在 Server 工程，不会发给客户端；分析器检查这一条。
  - Protocol 编译成 netstandard2.1 的 DLL 交给 Unity，所以里面的 `record struct` 和泛型特性没有问题。
  - 运行时也不靠反射读这些特性。
- **`ICluster`（HTTP、后台、测试）**不属于任何种类，可以发任何消息，回复回到调用方。踢人用 `cluster.Disconnect(clientId)`。

---

## 5. 系统

| 方法 | 执行时机 | 能做什么 |
|---|---|---|
| `[Receive]` | 每条消息一次，按到达顺序 | 判定，写组件，增删实体，发消息，回复 |
| `[ForEach]`（顺序执行） | 每次所在组运行时 | 同上；参数里拿 `Entity`、`World` |
| `[ForEach(Parallel = true)]`、批量（`Span`）方法 | 同上，可并行、可 SIMD | 只读写组件：不发消息，不做结构变更，不写有索引的组件 |

**`[Receive]` 的参数：**

| 参数 | 绑定到 |
|---|---|
| 消息 `in T` | 这条消息 |
| `Self<TKind>` | 目标实体自己 |
| `Entity`、组件（`ref`/`in`） | 接收实体。客户端发来的消息，是该客户端在目标上的链接实体（第 7 节）；其他消息，是目标实体自己 |
| `Pending<T>` | 处理 `Answer` 时，等待实体上的数据（第 8 节） |
| `From` | 发送方的地址 |
| `ReplyTo` | 要晚些回复时才取 |
| 单例 | 本 shard 的单例 |

- **返回值就是回复**；返回 `void` 就是单向消息。
- **系统按组件查询，不按种类划分**：`Position` 可以同时在 Avatar 和 NPC 上，一个系统一起处理。
- **执行频率**：
  - 系统组上写 `[Every(50)]`，由 shard 循环定时调用 `Update<Simulation>()`。EasyEcs 的组本身没有频率。
  - `Messages` 组是框架的，每次唤醒都跑。
- **`Messages` 组里的顺序**：
  1. `TimerSystem`：投递到期的邮件；
  2. 用户的生命周期系统：处理带 `Activated`/`Restored` 的实体；
  3. `MailSystem`：按到达顺序逐条调用生成的分发表；
  4. 用户的生命周期系统：处理带 `Unlinking`/`Unloading` 的实体；
  5. 框架去掉生命周期 tag，销毁该销毁的实体。
- **生命周期 tag 只存在一次 `Messages` 更新**。所以对它们做反应的系统必须放在 `Messages` 组里，由分析器检查。
- **`MailSystem` 逐条捕获异常**：
  - 某个 `[Receive]` 抛异常时，这个请求以失败结束；
  - 这个实体不保存就卸载，之后从上一份记录重新激活；
  - shard 照常运行。
  - 每个处理方法的耗时、所属系统的 `Enabled`，都由 `MailSystem` 自己记录和检查。

---

## 6. 生命周期

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达，实体还不存在 | 按 prefab 建实体，异步读记录；邮件留在邮箱里 |
| 激活 | 记录到了，或者没有记录 | 装上记录；加 `Activated` tag，从记录恢复的还加 `Restored`；生命周期系统在同一次更新里先于 `MailSystem` 运行 |
| 运行 | — | 邮件按到达顺序投递 |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或者 `world.Unload(e)` | 立刻给自己的链接实体加 `Unlinking`；通知各个 `Client`，只发不等；加 `Unloading`；保存记录；销毁 |

- 默认任何发给它的消息都能激活实体（Orleans 的虚拟 actor）。种类可以写 `[ActivatedBy<EnterMap>]` 收紧：其他消息遇到未激活的实体，返回 `NoActor`。
- 框架的控制消息（`Answer`、链接确认等）不会激活实体，找不到实体就丢弃。

---

## 7. 客户端与链接

**客户端的消息先到它的 `Client` 实体，再按路由转发：**
- `Client` 实体上有一张路由表 `DynamicBuffer<Route>`，每项是 `(种类, 键, LinkId)`。
- 消息里带了目标的键（如 `Say.Room`），这个键必须在路由表里；没带，就用该种类最近链接的那一项（如 `MoveTo` 去当前地图）。
- 转发之前，先过 `Client` 种类的过滤方法 `[Filter] static bool Allow(in Envelope e, ref RateLimit r)`。它按消息逐条执行，可以做限流。
- 被拒绝的消息，客户端收到原因：`NotLinked`、`Busy`、`NoActor` 等。

**链接的两边：**
- `Client` 上是一条路由；
- 目标上是一个链接实体，它是目标的子实体，带 `Linked(ClientId, LinkId)`，按 `LinkId` 建值索引。
- `LinkId` 由目标在创建链接实体时分配，全集群唯一（节点 id 加计数）。

**建立链接，三步：**
1. 目标在处理请求时创建链接实体，并把 `LinkId` 作为回复返回。例如地图在 `EnterMap` 里建好 Avatar，返回它的 `LinkId`。
2. 拿到 `LinkId` 的一方建立路由：
   - 客户端发起的请求（例如 Player 处理 `TravelTo`）：Player 回复客户端时，回复里带上链接，`Reply.Linking(true, mapId, linkId)`。`Client` 收到这条回复后先写路由，再把结果交给客户端。
   - `Client` 自己发起的请求（例如激活时向 Player 发 `Attach`，见下面的"第一条链接"）：`Client` 收到 `Answer` 后直接调用 `self.Route(playerId, linkId)`。
3. `Client` 写好路由后，先向目标发送 `LinkConfirmed(linkId)`，之后才转发这个客户端的消息。这两条消息从同一个 `Client` 发往同一个目标，所以确认一定先到。

**第一条链接**：`Client` 激活时，它的生命周期系统根据令牌里的声明（例如 `PlayerId`）向 Player 发 `Attach`，拿到 `LinkId` 后建立路由。之后客户端才能和 Player 通信。

**断开：**
- **目标主动断开**：例如收到 `LeaveMap`，或者自己被卸载。
  - 目标给链接实体加 `Unlinking`，并通知 `Client` 删掉这条路由。
  - 客户端会收到一条框架通知，知道自己已经离开这个目标。
- **`Client` 断开**：客户端断线，或者被踢、被顶号。
  - `Client` 按路由表通知每个目标。
  - 目标给对应的链接实体加 `Unlinking`。
- **租约兜底**：
  - 每个接受客户端的节点每 10 秒给每个对端发一份摘要，列出指向该对端、仍然有效的 `LinkId`。
  - 目标端如果连续两份摘要里都没有某个链接实体，或者它的 `Client` 所在节点已经离开成员视图，就给它加 `Unlinking`。
  - 这样，断开通知丢了也不会永远留下幽灵 Avatar。
- **未确认的链接**：超过回复路径的最长时间仍没收到 `LinkConfirmed`，就删除。这个时限取请求超时的两倍。
- 链接相关的控制消息不受 `Busy` 限制。
- 用户在 `Messages` 组里写 `[ForEach, With<Unlinking>]` 系统，做离开时的清理，例如广播 `Disappear`。

**换地图**由业务决定先后：
- Player 先发 `LeaveMap` 给旧地图，再向新地图请求 `EnterMap`。框架不会因为建了新链接就自动断开旧的。
- 一个客户端可以同时链接同种类的多个目标，例如多个聊天室。

---

## 8. 等待别的实体的结果

```csharp
public sealed partial class TravelSystem : EcsSystem
{
    [Receive]                                         // the client asks the player; the player asks the map
    static void Travel(in TravelTo m, in Self<Player> self, in From from, ReplyTo reply) =>
        self.Ask(m.Map, new EnterMap(self.Key, from.Client), new Travelling(m.Map), reply);

    [Receive]                                         // the answer is mail, in the player's arrival order
    static void Entered(in Answer<EnterMap, LinkId> answer, in Pending<Travelling> trip, ReplyTo reply, World world) =>
        world.Reply(reply, answer.Ok ? Reply.Linking(true, trip.Value.Map, answer.Value) : Reply.Of(false));
}
```

- **`self.Ask(to, message, pending, reply)` 创建一个等待子实体**，上面有：
  - 用户的数据（`Pending<T>`）；
  - 调用 id，全集群唯一；
  - `Due` 期限；
  - 可选的 `ReplyTo`。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后回信会跟过去。回信是发给发问方的 `Answer` 邮件，按到达顺序处理；处理完，框架删除等待实体。
- **超时**：到期时，框架去掉 `Due`，投递一个 `Ok = false` 的 `Answer`，这次投递同样由上面的规则处理。
- **`ReplyTo` 是清理组件，只能放在实体上，不能复制**（由分析器检查）：
  - `world.Reply(reply, 值)` 先读出令牌，再移除它，然后发回复。
  - 等待实体在回复之前被销毁时，例如发问方被卸载，清理系统恰好执行一次，给调用方发一个失败回复。
  - 实体迁移时，旧的那一份在销毁之前先移除 `ReplyTo`，所以不会发出误报的失败回复。
- **晚些回复**：用 `world.Hold(reply, 数据)` 建一个等待实体，之后对它调用 `world.Reply`。
- **一对多**：`self.AskAll(keys, message, pending)` 只建一个等待实体，回信累积在它的缓冲里。全部到齐或超时后，投递一个 `Answers<TReq, TRep>`，里面是池化的集合。

---

## 9. 时间与广播

- **`Due(long UnixMs)`**：绝对的 UTC 毫秒数，带有序值索引。因为是绝对时间，迁移和从记录恢复之后仍然正确。
  - `TimerSystem` 先取出 `Range(long.MinValue, 现在)` 的实体，再逐个处理：去掉 `Due`，投递到期邮件。
  - 先取出再修改，因为遍历有序索引时不能改它的键。
- **延时消息**：`self.After(delay, in message)` 建一个带 `Due` 的子实体，到期时作为邮件投给父实体。
- **周期性逻辑**用 `[Every]` 组，不用定时器。
- **`ClientGroup`**：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出到各个连接。
  - 成员关系记在链接实体上，链接实体进入 `Unlinking` 时自动退出所有组。
- **小组**（公会、队伍）：一个固定键的实体，成员表是它的组件，由它转发。取代现在的 `Publish`。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热更、升级（EasyEcs 4.4）

**一个实例的记录 = 一次 `SaveEntities`**，内容是：
- 根实体；
- 带至少一个 `[Persist]` 组件的子孙，以及它们到根的路径上的实体。

组件集合在注册时建好，一个用于保存，一个用于迁移：
- `persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`；
- `handoff = persist`，加上 `[Handoff]` 组件和等待、链接、定时这些运行时子实体的组件。

**恢复**：
- 记录头里存着实体数 n；
- 先准备 `into`：`into[0]` 是按 prefab 建好的根实体，其余是 n−1 个空实体；
- 一次 `LoadEntitiesInto(record, into)`。同一次保存和加载里，`ChildOf` 和 `EntityId` 会被改写成新的 id。
- 子孙没有 prefab 默认值，需要的组件都要在记录里。

**其他：**
- 只写改过的实例：`[Persist]` 组件实现 `ITrackChanges`，现有的 `ChangeSystem` 给它标 `Unsaved`。子孙改了，也标在根实体上。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式。
- **迁移**用 `handoff` 集合，做法同上。跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。等待中的请求和链接实体随实例一起迁移。

**迁移栅栏**（保证第 1 节第 2 条）：
- **触发**：放置的版本变化时，即成员视图或槽位表变化。每个节点在发往每个对端的链路上发一个 `Fence(版本)`，这之前发出的消息都排在它前面。
  - 每次变化共 N×(N−1) 个标记帧，代价很小。
- **旧主人**：
  - 收到发送方 S 的标记时，S 在旧版本下发来的消息已经全部到达；
  - 旧主人把这些消息连同标记一起转给新主人；
  - 如果旧主人自己还没采用新版本，就先把标记留着，等采用后再转。
- **新主人**：
  - 按槽位暂停：迁来的槽位上，S 的新消息先收下但不处理；
  - 等到经旧主人转来的、S 的版本不低于当前版本的标记，才开始处理；
  - S 离开视图，或者旧主人离开视图（实例改从记录恢复），也不再等。
  - 暂停按槽位，不影响其他实例。
- **兜底**：最长暂停 2 秒，超时就放行并计一次指标，保证不会永远卡住。

**热更：**
- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有系统和生成的分发表。
- 每个 shard 在两次 `Update` 之间：
  - 执行 `world.RemoveTypes(旧上下文)`；
  - 加入新系统；
  - 换上新的分发表。
- 所有 shard 都换完之后，旧上下文才卸载。
- 组件、消息、种类都不热更，新增它们需要滚动升级。

**滚动升级：**
- `wire.manifest` 里有每条消息的接收方、发送方、回复类型。
- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，各节点的放置结果一致。
- 节点 id 每个进程启动时随机生成，不是节点名，所以重启后的同名节点不会被当成旧节点。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或收到 `NoOwner`；实例在新节点从最近的记录激活，最多丢一个检查点间隔的改动 |
| 网关宕机 | 客户端断开后带令牌重连；各目标看到那个节点离开视图，清掉相关的链接实体 |
| 发问方在回信之前被卸载 | `ReplyTo` 的清理系统立即给调用方发失败回复；之后到达的回信找不到调用 id，丢弃 |
| 迁移期间的消息 | 有栅栏，不丢也不乱；目标节点宕机时，在途消息最多丢失（单向消息本来就是最多一次） |
| 目标的邮箱满了 | 发送方收到 `Busy`；客户端的消息由网关回绝 |
| 客户端读得太慢 | 断开（ADR-035） |
| 链接通知丢失 | 租约到期后链接实体被清理 |
| `[Receive]` 抛出异常 | 请求以失败结束；实体不保存就卸载，之后从记录重新激活 |
| `[Every]` 组里的系统抛出异常 | 按 `SystemErrorPolicy` 停掉该系统，并在健康检查里报告 |

---

## 12. 示例：地图服

```csharp
// Server: data
[Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;
public record struct MapSlot(int Value) : IComponent, IGroup { public int Group => Value; }   // shard-local map number
public record struct Avatar(PlayerId Player) : IComponent;                // on a link entity, child of its map
public record struct Visible : IComponent;                                // avatars and NPCs
public record struct Position(Vector2 Value) : IComponent;
public record struct Heading(Vector2 Value) : IComponent;                 // computed in parallel, untracked
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges; // what viewers extrapolate
public record struct Destination(Vector2 Target) : IComponent;
public record struct Speed(float Value) : IComponent;
public record struct Shout(NearbyEvent Event);                            // an entity's own outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16);
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetHeading(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(5).SetVisible().SetBuffer<Shout>();
}
```

```csharp
// Server.Logic: behaviour
public sealed partial class EntrySystem : EcsSystem
{
    [Receive]                                             // the avatar is the client's link entity on this map
    static LinkId Enter(in EnterMap m, in Self<Map> map, in MapSlot slot, World world)
    {
        var avatar = world.Instantiate(Prefabs.Avatar).Set(new Avatar(m.Player), slot);
        return map.Link(avatar, m.Client);                // sets Linked and ChildOf(map); the player routes the client
    }

    [Receive]
    static void Leave(in LeaveMap m, Entity avatar, World world) => world.Unlink(avatar);   // tags Unlinking, tells the Client
}

public sealed partial class MovementSystem : EcsSystem
{
    [Receive]                                             // bound to the sender's avatar
    static void Move(in MoveTo m, ref Destination destination) => destination.Target = m.Target;
}

[UpdateInGroup<Simulation>]                              // [Every(50)] struct Simulation : ISystemGroup
public sealed partial class SteeringSystem : EcsSystem
{
    [ForEach(Parallel = true)]                            // into an untracked component: no change stamps
    static void Steer(in Position p, in Destination d, in Speed s, ref Heading h) => h.Value = Steering.Toward(p.Value, d.Target, s.Value);

    [ForEach]                                             // sequential: write Velocity only when it differs
    static void Commit(Entity e, in Heading h, in Velocity v) { if (h.Value != v.Value) e.SetVelocity(h.Value); }
}

[UpdateInGroup<Simulation>, UpdateAfter<SteeringSystem>]
public sealed partial class IntegrateSystem : EcsSystem
{
    [ForEach]                                             // a batch over whole columns; Vectors.AddScaled is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), clock.DeltaTime);
}

[UpdateInGroup<Simulation>, UpdateAfter<IntegrateSystem>]
public sealed partial class NearbySystem : EcsSystem
{
    [ForEach, With<Visible>, Changed<Velocity>]          // into its own outbox; no write to other entities
    static void Moved(Entity e, in Velocity v, in Position p, ref DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(NearbyEvent.Moved(e.Id, p.Value, v.Value)));
}

[UpdateInGroup<Simulation>, UpdateAfter<NearbySystem>]
public sealed partial class AoiSystem : EcsSystem         // single-threaded: cells, subscriptions and sends
{
    public override void OnUpdate(World world) => Aoi.Run(world);
}

public sealed partial class LeaveSystem : EcsSystem       // in Messages: lifecycle tags live one Messages update
{
    [ForEach, With<Unlinking>, With<Visible>]
    static void Gone(Entity e, ref DynamicBuffer<Shout> outbox) => outbox.Add(new Shout(NearbyEvent.Disappear(e.Id)));
}
```

**AOI：**
- **格子**：`AoiSystem` 每 tick 按位置现算的分桶，不是组件。跨格时不需要结构变更。
- **订阅**：
  - 每个客户端订阅自己周围 3×3 格子的 `ClientGroup`，只在跨格时变化；
  - 新看到的人先收到完整的 `Appear`。
- **发布**：
  - 任何"附近应该看到"的事件，先写进事件发生者自己的发件箱；
  - `AoiSystem` 按格子合并，每个有内容的格子发一帧；
  - NPC 也带 `Visible`，所以同样会被广播。
- **按地图遍历**：
  - 用 `world.GroupBy<MapSlot>()` 加 `InGroup`。一个 shard 上的地图只有几百张，适合 `IGroup`。
  - `MapSlot` 是 shard 内的编号，地图激活时分配。不用 `long` 的键直接截断，避免两张地图撞号。
- **buff**：Avatar 的子实体，随目标一起迁移和销毁。
  - 施加者用一个组件记录：`BuffOf(EntityId Caster) : IComponent, IIndexed<EntityId>`。
  - 施加者消失时，用索引的 `WhenTargetDestroyed(TargetPolicy.Remove)` 去掉这个组件。

**MapTickBenchmark 实测**（BDN ShortRun，提交 4663e97）：
- 5% 的角色转向时，两种做法持平（0.97 倍）；
- 50% 的角色转向时，ECS 流水线用时约为"每个对象一个 Update、移动后立即广播"的四成（0.40 / 0.39 倍），发出的帧约为三分之一；
- 两者都不分配内存。

---

## 13. 工程、生成的代码、版本记录

| 工程 | 内容 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息、它们用到的键和种类名 | 客户端和服务端（netstandard2.1，不依赖 EasyEcs） |
| `Game.Server` | 服务器之间的消息、只在服务端的种类、组件、prefab、服务器类型、系统组，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有系统 | 服务端，放在可回收的上下文里，可以热更 |

**我们的生成器写：**
- `Self<K>` 上的发送、询问、回复方法；
- 每个服务器的分发表，放在 Logic 程序集里，随热更替换；
- `Client` 的转发表；
- wire id 和 `wire.manifest`；
- AppHost 的 `Servers.*` 句柄。

**我们的生成器不写：**
- `[ForEach]` 的 job：由 EasyEcs 生成；
- `IComponent`、`ITrackChanges`、`[NinoType]`：各个生成器看不到彼此的输出，所以由用户写，我们的分析器检查并提供一键修复。

**编译期检查：**
- 发送方向错了，没有对应的方法；
- 并行方法和批量方法里，不能发消息、不能做结构变更、不能写有索引的组件；
- 对生命周期 tag 做反应的系统必须在 `Messages` 组；
- `ReplyTo` 只能放在实体上，不能复制；
- 客户端消息只能放在 Protocol，服务器之间的消息不能放进 Protocol；
- 客户端能发去的种类，必须有地方建立链接；
- 组件不能放在 Logic 工程；
- 键必须是不超过 16 字节的非托管类型；
- DAS022：结构变更之后不能继续使用旧的 `ref`。

**删掉的东西：**
- `[Actor]`、`[Handle]`、`[Service]`、`Context`；
- `Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调；
- `UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`；
- `Publish`；
- Known/Granted/Superseded；
- 自研的记录格式。

**版本记录：**

| 版本 | 主要变化 |
|---|---|
| v8 | 消息写明发送方和接收方；会话与链接；一个节点只跑一种服务器 |
| v9 | 地址统一；系统按组件查询；生命周期改成 tag；改用 EasyEcs 4.4 的记录 |
| v10 | 链接随回复建立；迁移栅栏改为每个发送方发标记；参数绑定表；示例重写 |
| v11 | 客户端也是 `(Client, ClientId)`，不带网关节点；Protocol 和 Server 分开；`LinkId` 由目标分配；用户态的第一条链接；链接租约；去掉自动替换旧链接；卸载不等待；生命周期反应放在 `Messages` 组；记录一次保存一次加载；栅栏补上版本跳跃、旧主人宕机和超时兜底；`Receive` 异常的处理；示例修正（不追踪的中间结果、子实体链接、shard 内的地图编号） |

---

## 14. 待拍板

1. **客户端是框架内置的种类 `Client`，键是 `ClientId`**：它的位置由框架的位置目录管理，用户看不到节点。
2. **Protocol 只放客户端收发的消息**，服务器之间的消息和只在服务端的种类放在 Server 工程。
3. **严格顺序**：用迁移栅栏保证"同一发送方发给同一目标"在目标迁移期间也有序（最长暂停 2 秒）。
   - 另一种做法：像 Orleans 一样，迁移期间只尽力保序。这样更简单，可以去掉栅栏。
4. **换地图时先离开再进入，由业务决定**，框架不自动替换旧链接。
5. **`Self<TKind>` 这个名字**，也可以叫 `Me<TKind>`。
