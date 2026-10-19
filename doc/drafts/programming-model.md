# DaServer 新编程模型设计（草案 v9，未实现，迭代中）

本稿是完整的一份，不依赖之前的版本。v8 的问题与本稿的处理见第 15 节；需要拍板的点在第 16 节。

目标：写起来最简洁、最直观、最 ECS；概念最少；每条消息从哪个实体、哪个节点，到哪个实体、哪个节点，都能从类型看出来；高性能，高并发。

---

## 0. 概念一览（共七个）

| 概念 | 写在哪 | 写法 | 是什么 |
|---|---|---|---|
| 种类 | Contracts | `struct Map : IKind<MapId>` | 一类可寻址的实体。地址是 `(种类, 键)`，全局唯一 |
| 消息 | Contracts | `[From<Player>] record struct EnterMap(…) : IRequest<Map, bool>` | 接口写接收方，`[From<…>]` 写发送方 |
| 组件 | Server | `record struct Grid(…) : IComponent` | EasyEcs 原生 |
| prefab | Server | `[Spawns<Map>] static readonly Prefab` | 种类的实体刚创建时的样子 |
| 系统 | Server.Logic | EasyEcs `EcsSystem`，含 `[Receive]` 和 `[ForEach]` 方法 | 全部逻辑 |
| 服务器 | Server | `[Hosts<Map>] struct MapServer : IServer` | 一个节点（进程）跑哪些种类 |
| 链接 | 框架 | `world.Link(session, target)` | 客户端会话与目标实体的关系，客户端消息靠它转发 |

另外有三个框架提供的东西，用户只用不写：`Session` 种类（每个客户端一个）、`ReplyTo`（晚些回复用的令牌）、`Due`（定时）。

---

## 1. 保证

先写清楚框架保证什么、不保证什么，后面的设计都服从这里。

1. **每个实体按到达顺序逐条处理自己的消息，实体之间并行。**
   - 同一发送方发给同一实体的消息，按发送顺序到达。迁移期间也一样，靠第 11 节的栅栏保证。
   - 不同目标之间没有全局顺序。例如客户端发给 Player 的消息和发给 Map 的消息，谁先处理没有保证。
2. **消息处理只做判定，并把结果写成数据；每 tick 的重活由 `[ForEach]` 系统批量完成。**
3. **单向消息最多投递一次；请求一定有结果**：回复、拒绝或超时。
4. **失败的结果是确定的**，见第 12 节的表。

---

## 2. 节点、服务器、shard、实体

```
服务器类型     节点 = 一个进程，只跑一种服务器      shard = 线程 + world        实体
GameServer ──▶ game-0, game-1, …          ──▶  按核数              ──▶  Player 42、Guild 3
MapServer  ──▶ map-0, map-1, …                                         Map 7 + 子实体 Avatar
Gateway    ──▶ gate-0, …                                               Session（每个客户端一个）
```

```csharp
// Server project: which kinds a node of each server type hosts
[Hosts<Player>, Hosts<Guild>] public struct GameServer : IServer;
[Hosts<Map>]                  public struct MapServer  : IServer;
[Hosts<Session>]              public struct Gateway    : IServer;   // hosting Session = accepting clients
```

- **节点就是服务器，就是一个进程。** 一个节点只跑一种服务器类型；同一种服务器部署几个节点，由 AppHost 决定。
- 一个种类可以由多种服务器承载，例如小规模部署用的 `[Hosts<Player>, Hosts<Map>, Hosts<Session>] AllInOne`。放置只在承载它的节点中选，与 Orleans 的"兼容 silo"相同。
- 一个工程可以定义多种服务器类型。AppHost 为每组节点选一种：
  ```csharp
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<Gateway>("gate", replicas: 2).WithClientEndpoints();
  ```
  - 节点启动时校验配置里的服务器类型存在，写错就启动失败。
  - AppHost 构建时检查：每个被消息引用的种类，至少有一组节点承载它。
- **一个进程只跑一种服务器**，原因：
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，合进一个进程三样都没了；
  - ET 的单进程模式、Akka 的一节点多角色，都只用于开发和小集群；
  - 本地开发 Aspire 本来就会起多个进程，测试有 `TestCluster`，小规模部署用 `AllInOne`。
- **shard**：节点内的一个线程加一个 world。一个节点上同一服务器的所有种类共用 shard 的 world，所以同 shard 的不同实体可以在同一个系统里一起处理。实体落在哪个 shard 由键的哈希决定。

---

## 3. 地址：只有一种

**地址就是 `(种类, 键)`。** 键是 Contracts 里的值类型：

```csharp
// Contracts (netstandard2.1, no EasyEcs)
public readonly record struct MapId(long Value)    : IKey<Map>;
public readonly record struct PlayerId(long Value) : IKey<Player>;
public readonly partial struct Map    : IKind<MapId>;
public readonly partial struct Player : IKind<PlayerId>;
```

种类用放置策略决定一个地址落在哪个节点：

| 放置 | 用于 | 节点怎么定 |
|---|---|---|
| `Hashed`（默认） | Player、Map、Guild … | 在承载它的节点中按键选（现有的成员视图和放置）；节点变化时迁移 |
| `Pinned` | `Session` | 实体所在的节点就是客户端连着的节点；客户端在别的网关恢复时，实体迁过去（复用现有迁移） |
| `Any` | 无状态的工作者，例如令牌校验、计价 | 优先本节点，每个节点可以有自己的实例 |

- `Session` 的键是稳定的 `ClientId`，来自签名令牌（ADR 已有）。客户端断线重连到另一个网关后，地址不变，别处保存的会话地址仍然有效。
- 子实体（地图上的 Avatar、等待中的请求）不可寻址，跟父实体一起迁移。
- 实体上表示地址的组件是框架的非泛型组件 `ActorKey`（种类 id + 键），有值索引。用户通过生成的 `me.Key` 拿到强类型的键。
  - 不用 `Key<MapId>` 这类泛型组件，因为 EasyEcs 的生成器不处理泛型组件。这样 Contracts 也完全不碰 EasyEcs。

**没有"无键种类"。** v8 的无键种类拆成三种已有的东西：
- 每个节点或 shard 一份的状态（缓存、寻路网格）：单例；
- 无状态的工作者：`Any` 放置的种类；
- 全局服务（匹配、排行榜）：固定键的普通种类，例如 `Matchmaker(Region)`。

---

## 4. 消息：接收方写在接口上，发送方写在特性上

```csharp
// Contracts
[From<Client>] public record struct Login(string Token)              : IRequest<Session, LoginResult>;
[From<Client>] public record struct TravelTo(MapId Map)              : IRequest<Player, bool>;
[From<Client>] public record struct MoveTo(Vector2 Target)           : IMessage<Map>;
[From<Client>] public record struct Cast(SkillId Skill, PlayerId On) : IMessage<Map>;
[From<Client>] public record struct Say(RoomId Room, string Text)    : IMessage<Room>;     // names its target: one of several links
[From<Session>] public record struct Attach(ClientId Client)         : IRequest<Player, bool>;
[From<Player>] public record struct EnterMap(PlayerId Player, ClientId Client) : IRequest<Map, bool>;
[From<Map>]    public record struct Nearby(…)                        : IMessage<Client>;   // pushed to the client
[From<Player>] public record struct Wallet(int Gold)                 : IMessage<Client>;
```

- **接收方**：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。`TTo = Client` 表示推给客户端。
- **发送方**：`[From<K>]` 可以写多个，例如 `[From<Map>, From<Npc>]`。`Client` 表示由客户端发、经它的会话转发。
- **检查不靠分析器猜，而是生成强类型的发送方法**。每条消息生成一个重载，发送方的键是第一个参数：
  ```csharp
  // generated
  public static void Send(this World world, PlayerId from, MapId to, in EnterMap message);   // only [From<Player>]
  ```
  - 用别的种类的键调用，没有对应的重载，编译不过。
  - 发送方的键同时就是回复地址。
- HTTP、后台和测试通过 `ICluster` 发送，不属于任何种类，可以发任何消息；回复回到调用方。
- `[From<Client>]` 和 `IMessage<Client>` 就是现在的 `[FromClient]`/`[ToClient]`，两个旧特性删掉。

---

## 5. 系统：全部逻辑

逻辑只有两种方法，都写在 EasyEcs 系统里：

| 方法 | 何时执行 | 可以做什么 |
|---|---|---|
| `[Receive]` | 每条消息一次，按到达顺序，在 shard 线程上 | 判定，写组件，创建或删除实体，发消息，回复 |
| `[ForEach]` | 每 tick，批量（EasyEcs 生成 job，可 SIMD、可并行） | 读写组件；不发消息、不做结构变更（这些写进自己的缓冲，由后面的单线程系统处理） |

```csharp
public sealed partial class MovementSystem : EcsSystem
{
    [Receive]                                             // the client's intent becomes data, in arrival order
    static void Move(in MoveTo m, ref Destination destination) => destination.Target = m.Target;
}
```

- `[Receive]` 的参数：
  - 消息本身；
  - 接收的实体，或它的组件（`ref`/`in`）；
  - `From`：发送方地址；
  - `ReplyTo`：要晚些回复时才取；
  - 单例。
- 客户端消息到达时，接收的实体是该客户端在目标上的**链接实体**（第 7 节），例如地图上那个玩家的 Avatar，不需要自己查表。
- **返回值就是回复**；返回 `void` 表示单向消息。
- **系统不按种类分组。** 系统按组件查询，这是 ECS 的本义：`Position` 可以同时在 Avatar 和 NPC 上，一个移动系统同时处理两者。
- **执行频率用 EasyEcs 的组**：`[UpdateInGroup<Simulation>]` 加 `UpdateInterval`。地图模拟每 50 ms 一次，玩家逻辑一般没有 tick。
- **消息由框架的 `MailSystem` 执行**：它在 `Messages` 组里，按到达顺序逐条调用生成的分发器。所以 `[Receive]` 也走 EasyEcs 的调度，受 `Enabled`、`SystemErrorPolicy`、`MeasureSystems` 管理，而不是绕开调度直接调用。

---

## 6. 生命周期：tag 加系统，不用回调消息

| 阶段 | 框架做什么 | 用户写什么 |
|---|---|---|
| 激活 | 第一条消息到达时，按 prefab `Instantiate`；有记录就 `LoadEntitiesInto` 恢复；加 `Activated` tag（从记录恢复的还加 `Restored`） | `[ForEach, With<Activated>]` 系统，例如给新玩家发初始物品 |
| 运行 | `MailSystem` 先跑完 `Activated` 的系统、去掉 tag，再投递邮件，所以初始化一定在第一条消息之前 | `[Receive]` 和 `[ForEach]` |
| 空闲 | `IdleAfter(TimeSpan)` 组件：超时就加 `UnloadRequested` tag | 不写就是常驻 |
| 卸载 | 先跑 `With<Unloading>` 的系统，再按 `ComponentSet` 保存记录，最后销毁 | `[ForEach, With<Unloading>]`，如需要 |
| 主动卸载 | `world.Unload(entity)`，就是加 `UnloadRequested` tag | — |

- 去掉 `Created`/`Loaded`/`Unloading` 回调和 `Despawn`、`Lifetime`。
- **谁能创建实体**：默认任何发给它的消息都会激活它（Orleans 的虚拟 actor）。客户端只能发到链接过的目标，所以客户端不能借此乱建实体。种类也可以写 `[ActivatedBy<EnterMap>]`，只让这些消息激活，其余消息遇到未激活的实体时返回 `NoActor`。

---

## 7. 客户端：会话与链接

**规则一：客户端只连承载 `Session` 的节点（网关）。** 连上并登录后，框架在该节点上为它激活 `Session(ClientId)` 实体。

**规则二：客户端发的消息先到它的会话，会话按链接转发。**
- 发给 `Session` 的消息（如 `Login`），由会话上的系统处理。
- 发给其他种类的消息，会话查自己的路由表：一个 `DynamicBuffer<Route>`，每项是 `(种类, 键)`。
  - 消息里有目标种类的键（如 `Say.Room`）：必须在路由表里，否则拒绝；
  - 没有：用该种类最近链接的那一项（如 `MoveTo` 去当前地图）。
  - 一个客户端可以同时链接多个同种目标（聊天室、队伍）。
- 没有路由时，拒绝并回 `NotLinked` 给客户端，不静默丢弃。
- 转发前先跑会话上的过滤系统，例如限流：令牌桶是会话上的组件，取代 `[Filter]`。

**规则三：发给客户端，就是发给它的会话。** `world.Send(map, clientId, in nearby)` 先到会话所在的网关，再写到 socket。

**链接的两边**：

| 位置 | 数据 | 用途 |
|---|---|---|
| 会话上 | `DynamicBuffer<Route>` | 转发 |
| 目标上 | 一个子实体，带 `Linked(ClientId)` 组件（有值索引） | 转发来的消息投递给它；推送时知道客户端是谁；会话关闭时一起删除 |

**建立链接分两步，回复客户端的那个实体负责第二步：**
1. 目标在处理请求时创建链接实体（地图上就是 Avatar）：`world.Spawn(prefab).With(new Linked(client), new ChildOf(map))`。
2. 请求方拿到成功的回复后，调用 `world.Link(client, target)`，然后回复客户端。
   - 会话收到 `Link` 后写路由。如果该种类有旧路由（换地图），会话给旧目标发 `Unlinked`。
   - 会话再给新目标发 `Linked` 确认。

为什么这样就有序，不需要额外的版本号或往返：
- `Link` 和回复都从请求方发往同一个会话，走同一条链路，会话一定先有路由，客户端才看到回复。
- 发给旧目标的 `Unlinked` 和此前转发给它的消息都从会话发出，也走同一条链路，所以旧目标先处理完换图前的消息，再收到 `Unlinked`。之后来的消息找不到链接实体，直接丢弃，不会有"双重存在"。
- 目标创建了链接实体，但请求方在第 2 步之前崩溃：链接实体带着 `Due` 期限，期限内没收到会话的 `Linked` 确认就删除，不会留下孤儿 Avatar。

**会话关闭**（断线、被顶号）：
- 会话按路由表给每个目标发 `Unlinked`；
- 目标框架按值索引找到链接实体并删除；
- 用户在 `[Removed<Linked>]` 系统里清理，例如广播 `Disappear`。

**顶号**：Player 处理新会话的 `Attach` 时，发现已有别的链接实体，就调用 `world.Unlink(oldClient, player)`，旧会话随之关闭。

**恢复**：客户端带令牌连到另一个网关，`Session(ClientId)` 迁移过去，路由表随之过去，目标端不用改。

---

## 8. 等待别的实体的结果

```csharp
public sealed partial class TravelSystem : EcsSystem
{
    [Receive]                                          // the client asks the player; the player asks the map
    static void Travel(in TravelTo m, Entity player, in From from, ReplyTo reply, World world) =>
        world.Ask(player.Key, m.Map, new EnterMap(player.Key, from.Client), wait: new Travelling(m.Map), reply);

    [Receive]                                          // the answer arrives as mail, in the player's arrival order
    static void Entered(in Answer<EnterMap, bool> answer, in Travelling trip, World world)
    {
        if (answer.Ok && answer.Value) world.Link(answer.Asker.Client, trip.Map);   // link first, then reply
        world.Reply(answer.ReplyTo, answer.Ok && answer.Value);
    }
}
```

- `world.Ask` 创建一个等待子实体（`ChildOf` 发问方），带：
  - 用户给的数据（`Travelling`）；
  - 调用 id（值索引）；
  - `Due` 期限；
  - 可选的 `ReplyTo`。
- **回信按 `(种类, 键, 调用 id)` 寻址**，不按实体 id：
  - 发问方迁移后，回信跟着去；
  - 实体 id 只在本节点有效，迁移后会变。
- 回信 `Answer<TReq, TRep>` 是发给发问方的邮件，和其他消息一样按到达顺序处理。处理完，框架删除等待子实体。
- **超时**：到期时，框架投递一个失败的 `Answer`（`Ok = false`）。
- **`ReplyTo` 是清理组件**（`ICleanupComponent`）：
  - 等待实体在回复之前被删除（发问方被卸载），清理系统恰好执行一次，失败地回复调用方，调用方不用等到超时。
  - 已经回复过的令牌会先被移除，所以正常完成时不会再触发清理。
  - 迁移时，离开的那一份先移除清理组件再销毁，不会误发失败回复。
- **晚些回复**：把 `ReplyTo` 存进任何组件，之后调用 `world.Reply(token, 值)`。这就是现在的 `ReplyTo`，名字不变。

---

## 9. 时间

- `Due(long Tick)` 组件，有序值索引。框架的 `TimerSystem` 每 tick 用 `Range(上次, 现在)` 取到期的实体，投递它们的到期邮件。
- `world.After(delay, self, in message)`：创建一个带 `Due` 和消息的子实体，到期时作为邮件投给父实体。等待请求的超时、链接确认的期限，都是同一个机制。
- 周期性的逻辑不用定时器，用 `UpdateInterval` 的组。

---

## 10. 广播

- **客户端组**（现有的 `ClientGroup`）：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出到各个 socket。
  - 成员关系记在链接实体上（例如 Avatar 的 `Watching`），链接实体删除时由框架退组，用户不必在 `SessionClosed` 里手动退。
- **实体组**：`Group(name)` 是一个固定键的种类，成员表是它的组件，发给它的消息由它转发给成员。现在的 `Publish` 由此取代：发布 = 发给组。

---

## 11. 持久化、迁移、热更、升级（用 EasyEcs 4.4）

**持久化**：
- 框架在注册时，用 `ComponentSet.Create().With(typeof(T))` 把所有 `[Persist]` 组件建成一个集合。
- 保存：`world.SaveEntities(output, ids, persistSet)`，只写这些组件，运行时组件不进记录。
- 恢复：激活时先按 prefab 创建实体，再 `world.LoadEntitiesInto(record, [entity])` 把记录装上去，邮箱保留。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`；加字段用 `VersionTolerant` 或 `Keyed` 格式。
- 只写改过的实体：`[Persist]` 组件实现 `ITrackChanges`，现有的 `ChangeSystem` 标 `Unsaved`。

**迁移**：
- 迁移的对象是实体加它的全部子实体（`world.Children`）：`SaveEntities(output, group, persistAndHandoffSet)`。
- 新节点上用 `LoadEntitiesInto` 装到已建好的实体上。`ChildOf` 和 `EntityId` 字段会被改写成新的 id。
- 跨 shard（跨线程）同样走字节，因为 `MoveEntities` 要求两个 world 在同一线程。
- **迁移栅栏**（保证第 1 节的严格顺序）：
  - 新主人先收下发给该实体的新消息，但暂不处理；
  - 旧主人转发完它手里的消息，在每条发送方链路上发一个结束标记；
  - 新主人收到标记后，才开始处理新消息。
- 等待中的请求随实体迁移，因为回信按键寻址，会跟到新节点。

**热更**：
- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有系统和生成的分发表，两者一起替换。
- 每个 shard 在两次 `Update` 之间：
  - `world.RemoveTypes(旧上下文)`；
  - 加入新的系统；
  - 换掉框架注册表里的分发表，否则旧上下文会被引用住、卸载不掉。
- 所有 shard 都移除之后，旧上下文才卸载。
- 组件、消息、种类都在 Server 和 Contracts 工程里，不热更。新增消息或组件需要滚动升级，不能热更。

**滚动升级**（ADR-077）：
- `wire.manifest` 加上路由的列：每条消息的接收方、发送方、回复类型。
- 新消息到了旧版本的节点，返回 `NotHandled`。
- `[Hosts]` 记在成员卡片里，放置按卡片计算，所以新旧版本混跑时放置结果一致。

---

## 12. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标所在节点宕机 | 请求超时或收到 `NoOwner`；实体在新主人处从最近的记录激活（丢失最多一个检查点间隔的改动，ADR 已有） |
| 网关宕机 | 客户端带令牌连到别的网关，`Session` 迁过去并从记录恢复路由。来不及恢复时，会话超时关闭，各目标收到 `Unlinked` |
| 发问方在回信前被卸载 | `ReplyTo` 的清理系统立即失败地回复调用方；回信到达时找不到调用 id，丢弃 |
| 单向消息在迁移中 | 有栅栏时不丢；目标节点宕机时最多丢失在途的（最多一次） |
| 目标邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回给客户端，不阻塞网关 |
| 客户端读得太慢 | 断开（ADR-035） |
| 链接实体建了但没确认 | 期限到就删除 |

---

## 13. 完整示例：地图服

```csharp
// Server project: data
public record struct Grid(int Width, int CellSize) : IComponent;                         // on a map
public record struct Position(Vector2 Value) : IComponent;
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;               // what viewers extrapolate
public record struct Destination(Vector2 Target) : IComponent;
public record struct Cell(int Index) : IComponent, IIndexed<int>, ITrackChanges { public int Key => Index; }
public record struct OnMap(MapId Map) : IComponent, IGroup { public long Group => Map.Value; }  // a few hundred maps per world
public record struct Health(int Value, int Max) : IComponent;
public record struct Watching(int Cell) : IComponent;                                    // the 3x3 it subscribed to
public record struct Shout(NearbyEvent Event);                                           // an avatar's own outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16);
    public static readonly Prefab Avatar = new Prefab()
        .SetPosition(default).SetVelocity(default).SetDestination(default).SetHealth(100, 100).SetBuffer<Shout>();
}
```

```csharp
// Server.Logic project: behaviour
public sealed partial class EntrySystem : EcsSystem
{
    [Receive]                                             // the player asks to enter; the avatar is the client's link entity
    static bool Enter(in EnterMap m, Entity map, World world)
    {
        world.Instantiate(Prefabs.Avatar).Set(new Linked(m.Client), new ChildOf(map.Id), new OnMap(map.Key));
        return true;
    }
}

public sealed partial class CombatSystem : EcsSystem
{
    [Receive]                                             // judged against where everyone is at this moment
    static void Cast(in Cast m, Entity me, World world)
    {
        if (world.TryFindAvatar(me.OnMap, m.On, out var target) && Skills.InRange(m.Skill, me.Position, target.Position))
            world.Instantiate(Skills.BuffOf(m.Skill)).Set(new BuffOn(target.Id));   // a buff is an entity (RelationIndex, ends with its target)
    }
}

[UpdateInGroup<Simulation>]                              // Simulation: UpdateInterval = 50 ms
public sealed partial class IntegrateSystem : EcsSystem
{
    [ForEach]                                             // a batch: whole columns, SIMD
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Simd.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), clock.DeltaTime);
}

[UpdateInGroup<Simulation>, UpdateAfter<IntegrateSystem>]
public sealed partial class CellSystem : EcsSystem
{
    [ForEach]                                             // only a crossing writes Cell (tracked); no table move
    static void Locate(in Position p, ref Cell cell, in Grid grid) { int c = grid.CellOf(p.Value); if (c != cell.Index) cell.Index = c; }
}

[UpdateInGroup<Simulation>, UpdateAfter<CellSystem>]
public sealed partial class NearbySystem : EcsSystem
{
    [ForEach, Changed<Velocity>]                         // into the avatar's own outbox: no cross-entity write in a job
    static void Moved(in Velocity v, in Position p, in Linked me, DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(NearbyEvent.Moved(me.Client, p.Value, v.Value)));
}

[UpdateInGroup<Simulation>, UpdateAfter<NearbySystem>]
public sealed partial class AoiSystem : EcsSystem         // single-threaded: joins, leaves and sends happen here
{
    public override void OnUpdate(World world) => Aoi.Flush(world);   // per map: rewatch crossers, one frame per busy cell group
}
```

AOI 的规则：
- **订阅**：每个会话订阅自己周围 3×3 格子的客户端组，只在跨格时变化；新看到的人先收到完整的 `Appear`。
- **发布**：任何"附近该看到"的事件，先写进事件发生者自己的发件箱；`AoiSystem` 按格子合并，每个有内容的格子发一帧。
- **格子**是有值索引的、被追踪的组件，不是 `IGroup`（格子成千上万，而且经常变化，用 `IGroup` 会把 world 切得很碎）。`IGroup` 用在地图上：一个 shard 上的所有地图共用一个 world，按地图分组遍历。
- **buff 是实体**：通过 `RelationIndex` 指向目标，目标死亡时一起结束。这是 EasyEcs 规则 8"一项一个实体"的做法。

**MapTickBenchmark 实测**（BDN，同一台机器）：

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
| `Game.Server` | 组件、prefab、服务器类型，五行的 Program.cs | Contracts、DaServer |
| `Game.Server.Logic` | 所有系统 | Server；放在可回收的上下文里，可以热更 |

- 想按服务器拆成更多工程（例如让网关镜像不带 Npgsql）也可以，框架不要求。
- 节点只注册它这种服务器所承载种类用到的系统。

**我们的生成器写：**
- 每种消息的强类型 `Send`/`Ask`/`Reply` 重载（第 4 节）；
- 每个服务器的分发表：消息 id → `[Receive]` 方法，放在 Logic 程序集里，随热更替换；
- 会话的转发表；
- wire id 和 `wire.manifest`（含路由列）。

**我们的生成器不写：**
- 任何 `[ForEach]` 的 job，全部由 EasyEcs 生成；
- 任何 `IComponent`、`ITrackChanges` 之类的接口。需要时由用户写，我们的分析器检查并提供一键修复（EasyEcs 的生成器看不到别的生成器的输出）。

**编译期检查：**
- 发送方向不对，没有对应的重载，编译失败；
- `[Receive]` 的消息参数必须是该节点承载的某个种类的消息；
- `[ForEach]` 里发消息或做结构变更报错；
- 一个 `[From<Client>]` 消息的目标种类，必须有地方调用 `world.Link` 链接它；
- 组件不能放在 Logic 工程（热更要求，取代现在的 DAS013）；
- DAS022：结构变更之后不能继续用旧的 `ref`。

---

## 15. v8 的问题和本稿的处理

| v8 的问题 | 本稿 |
|---|---|
| 两种地址；钉住的会话地址重连后失效，存下来的会话地址变成死地址 | 一种地址，放置策略区分；`Session` 键是稳定的 `ClientId`，恢复就是迁移 |
| `Client`、`Session`、`IAcceptsClients` 三个名字说同一件事 | `Session` 一个种类；承载它就是接受客户端 |
| `IMessage<TFrom, TTo>` 只能有一个发送方，HTTP 和测试无法发送，编译期检查也做不到 | 接收方在接口上，发送方是可写多个的 `[From<…>]`；生成强类型重载来检查；`ICluster` 可以发任何消息 |
| 种类 = 系统组，"系统只看到这类实体"不成立，也不 ECS | 系统按组件查询；频率用 EasyEcs 的组 |
| 无键种类集中在 shard 0，成了串行点 | 单例、`Any` 种类、固定键种类三选一 |
| `Spawned`/`Restored` 是回调消息；`Despawn` 和现有 tag 重复 | `Activated`/`Restored`/`Unloading` tag 加系统；`world.Unload` |
| 会话与实体的关系有五种写法 | 一个链接关系，两边各有数据，其余全部从它推出 |
| `Linking<bool>` 只为修补自己造成的跨链路竞争 | 请求方先链接再回复，同一链路天然有序 |
| 换图后旧地图仍执行旧消息（双重存在） | 会话发 `Unlinked`，与此前的转发消息同一链路，之后的消息找不到链接实体 |
| 迁移时新旧主人交接，在途消息乱序 | 迁移栅栏 |
| 回信寄给子实体 id，迁移后寄丢 | 按 `(种类, 键, 调用 id)` 寻址 |
| 清理组件在正常完成和迁移时也会触发失败回复 | 回复时先移除令牌；迁移时离开的一份先移除 |
| 格子用 `IGroup`，成千上万个小组，跨格就搬一次行 | 格子是索引字段；按地图 `IGroup` |
| `[ForEach]` 里发消息、写别的实体（并行时有数据竞争） | 写自己的发件箱，单线程系统再发 |
| `world.Sessions`、`AvatarIndex` 是组件之外的注册表 | 全部是带索引的组件 |
| `-1` 这类哨兵值 | 用"没有这个组件"表示 |
| `Key<MapId>` 泛型组件，EasyEcs 生成器不支持 | 非泛型 `ActorKey`，强类型访问由我们生成 |
| `routes.manifest` 与 `wire.manifest` 重复 | 合并进 `wire.manifest` |
| 缺定时器、广播、限流、多目标链接、恢复协议、反压、失败语义 | 第 7–12 节 |
| 持久化、迁移的记录格式是自研的 | EasyEcs 4.4 的 `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` |

---

## 16. 需要拍板的点

1. **一种地址加放置策略**（`Hashed`/`Pinned`/`Any`）；`Session` 用稳定的 `ClientId` 作键，恢复就是迁移。
2. **消息只在接口上写接收方，发送方用 `[From<…>]`（可以写多个），由生成的强类型重载检查**；`ICluster` 可以发任何消息。
3. **系统不按种类分组**，按组件查询；频率用 EasyEcs 的组。
4. **客户端链接**：会话上存路由，目标上存链接实体；请求方先链接再回复；同一种类可以链接多个目标。
5. **严格顺序跨迁移也成立**：迁移栅栏，代价是迁移时新主人多等一次旧主人的转发。
6. **节点 = 服务器 = 进程**，一个工程可以定义多种服务器类型（v8 已提出，沿用）。
