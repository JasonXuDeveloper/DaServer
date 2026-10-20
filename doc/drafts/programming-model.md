# DaServer 新编程模型设计（草案 v34，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。ECS 一路到底：能用 EasyEcs 的功能就用 EasyEcs，状态都是数据，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个 Actor 发到哪个 Actor，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分，也看不到节点和 Actor 的地址；
- 分布式下也正确：同一个 Actor 只有一个激活能写，连接有先后，先后和超时不依赖各节点的时钟一致；
- 高性能，高并发。

本稿是完整的一份。每条规则只在它所在的那一节写一次，别处只给链接。第 12 节是完整的例子，分别列出用户写的代码、生成的代码和测试。第 13 节是编译期检查，第 14 节是已定的取舍和对 EasyEcs 的需求，第 15 节是可选的扩展能力（各自一个包，不引用就没有）。框架内部的数据、机制和协议细节在 [architecture.md](architecture.md)；本稿先用后讲的词（主体、有序阶段、`Due`、`Outbox`、epoch、占位等）在它第 2 节的 [Terms](architecture.md#terms) 里各有一句话的定义。

文中引用的 EasyEcs 文档都在它仓库的 `docs/content/` 下。本稿基于 EasyEcs 4.6.0（已在 NuGet）：`entity.Run(ref job, filter)`、接口带的组过滤（`IGroupWith<T>`）、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（以上自 4.5），`world.Load(bytes, markWritten: false)`、`AddSystem(…, changesFromNow: true)`、带追踪组件的程序 Native AOT 0 警告（4.6）。另外用到 EasyEcs 4.7（计划中，未发布）的：`Mut<T>` 参数和 `SetIfChanged`、带组件的 `world.Instantiate`、`[ChangedAny<…>]`/`[RemovedAny<…>]` 和带起点的 `QueryFilter.ChangedAny(since)`、`IUniqueIndexed<T>`、`world.TrimExcess()`、`[ForEach(Slices = N)]`、`SystemStats` 的分配字节和匹配的实体数、`world.DescribeSchedule()`、`world.Save(…, exclude)` 和 `world.Load(…, keep)`；用到它们的步骤在路线图里等 4.7 发布，发布之前各自的 4.6 替代做法和要核对的 EasyEcs 行为只列在[路线图的 W1](../roadmap.md)（第 14 节第 8 条）。

---

## 0. 一页看懂

### 用户写的

用户只有两个工程：`Game.Protocol` 是客户端收发的消息，只有数据，编译成 netstandard2.1 的 DLL 交给 Unity；`Game.Server` 是其余的一切，加上五行的 Program.cs 和部署用的 `apphost.cs`（第 2 节）。换代码就是换二进制：节点原地热重启，JIT 和 Native AOT 一样（第 10 节）。

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `[NinoType] record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 发给指定 Actor 的客户端消息 | `[NinoType] record struct EnterMap([To] long Map) : IFromClient<bool>;` | Protocol |
| 推给客户端的消息 | `[NinoType] record struct SnapBack(Vector2 At) : IToClient;`；含集合的是 `[NinoType, NinoPooled] sealed partial class Nearby : IToClient` | Protocol |
| Actor 类型（地址、根、系统组） | `[TickRate(30)] readonly partial record struct Map(long Id) : IActor<Map>;`；根要带组件时在类型里写 `public static Prefab RootPrefab { get; } = …`，根要保存再加 `[Persist]`；改默认的设置写 `[ActorOptions(IdleTimeout = …, DetachGrace = …, MaxPending = …, OnError = …)]`（第 3 节） | Server |
| 服务器之间的消息 | `[NinoType, From<Map>] record struct GrantItem(int Item) : IRequestTo<Player, bool>;`；必须落定一次、两端都要扛住宕机的请求加 `[Durable]`（第 8 节） | Server |
| 本地事件 | `record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；不随迁移、不进记录的标 `[NotSaved]`（第 10 节） | Server |
| 子实体的键 | `partial record struct NetId(Map Map, int Value) : IChildKey<NetId>;`，第一个字段是 Actor 类型（第 3 节） | Server |
| prefab | `[Prefab<Map>] internal static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);`；要保存的子实体写 `[Prefab<Map>(Persist = true)]`（第 10 节）；Actor 里的实体都用 `root.Spawn(prefab, …)` 建，按键找到或建出用 `root.GetOrSpawn(prefab, key, …)` | Server |
| 配置表 | `readonly record struct SkillTable(SkillRow[] Rows) : IConfigTable<SkillTable>`：静态的 `Name` 和 `TryLoad`，处理方法和系统取 `in SkillTable skills`（第 10 节） | Server |
| world 自己的缓存 | `struct PathCache : IWorldSingleton<PathCache>`：静态的 `Create()`，可以取 `ref`（第 5 节） | Server |
| 身份 | `readonly partial record struct Account(long Id) : IClaims;`：令牌里的声明，用户自己的类型（第 7 节） | Server |
| 连上就加入 | `[JoinOnConnect<Account>] readonly partial record struct Player(long Id) : IActor<Player>;` | Server |
| 服务器类型 | `[Hosts<Map>] public readonly struct MapServer : IServer;` | Server |
| 对消息和事件的反应 | `[Handles<Map>] static partial class Combat { [On<CastSkill>, Without<Stunned>] … }` | Server |
| 每 tick 或对数据的反应 | `[UpdateInGroup<Map>] class Movement : EcsSystem { [ForEach] … }`；热重启前后的反应也是系统：`[ForEach, With<Restarting>]`、`[With<Resumed>]`（第 10 节） | Server |
| 本 Actor 里现在或以后做一件事 | `me.Schedule(new Strike(…), after: skills.Windup(skill))`，不写 `after` 就是紧接着 | Server |
| 发给别的 Actor | `me.Send(new Player(id), new Defeated(map))` | Server |
| 等别的 Actor 的结果 | `drop.Ask(new Player(who.Id), new GrantItem(item), new Looting(reply), settleBy: …)`（`[Durable]` 写 `settleBy:`，普通请求可写 `deadline:`），回信是 `Replied<GrantItem, Looting>`；不带状态时是 `Replied<GrantItem>` | Server |
| 测试一个 Actor | `new TestActor<Map>(new Map(7), Registration.Create())`；多个节点用 `TestCluster` | 测试 |
| 部署 | `cluster.AddNodes<MapServer>("map", replicas: 4)` | `Game.Server/apphost.cs`（第 2 节） |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `IActor<T>`、`IChildKey<T>` | Actor 类型和子实体键的接口：组件、唯一值索引、`IEquatable<T>`；`IActor<T>` 还是系统组，带组过滤 `In<T>` 和静态的 `RootPrefab`（第 3 节） |
| `Entity`、`World` | 唯一的句柄和 ECS 本身。ECS 的成员来自 EasyEcs；发送、推送、加入、`Root` 等是 DaServer 加在 `Entity` 上的操作（第 5 节） |
| `ActorId`、`TypeId<T>.Value` | 任意 Actor 的地址（只在服务端）；类型的 id（Nino 类型 id），Actor 类型、消息、事件、组件都用它 |
| `Sender`、`ReplyTo<T>`、`Replied<TReq[, TState]>`、`CallId` | 发送方（`Id`，`TryGet(out K key)`）；回复令牌（普通数据）；回信（`TryGetValue` 取值，`Status` 说明原因，`State` 是发问时存下的状态）；调用 id（第 5、8 节） |
| `Connection`、`IClaims`、`Presence`、`Joined`、`Leaving`、`Detached` | 每个客户端连接一个 Actor；身份；客户端在一个 Actor 上的存在和它的几个状态（第 7 节） |
| `Activated`、`Deactivating` | 激活后的第一条邮件；停用前最后一个 tick 根上的 tag（第 6 节） |
| `Time`、`Scheduled<T>`、`ClientGroup` | 时间单例；延后事件的句柄；一次序列化、按网关扇出的广播组（第 9 节） |
| `Status` | 请求的结果和拒绝原因，分 `NotExecuted` 和 `Transient` 两类（第 11 节） |
| `[TickRate(n)]`、`[ActorOptions(…)]`、`[Durable]`、`[Persist]`、`[Prefab<K>(Persist = true)]` | 帧率（第 5 节）；Actor 类型的设置（第 3 节）；落定一次的请求（第 8 节）；要保存的根、要保存的子实体（第 10 节） |
| `IConfigTable<T>`、`IWorldSingleton<T>` | 用户单例的两种：配置表（节点读入、校验，只能取 `in`，第 10 节）；world 自己的缓存或配置（每个 world 用 `Create()` 建一份，可以取 `ref`，第 5 节） |
| `ICluster`、`Registration`、`TestActor<K>`、`TestCluster` | Actor 以外的入口（第 4 节）；生成的注册对象；测试台（第 12 节） |

进阶（用到时再看）：

| 名字 | 是什么 |
|---|---|
| `[Stateless]` | 不保存、不迁移的 Actor 类型，只做当场回复（第 3 节） |
| `[NotSaved]` | 不随迁移、不进记录的组件（第 10 节） |
| `[ActivatedBy<T>]` | 只有 T 能激活这个 Actor 类型（第 6 节） |
| `Incoming`、`Verdict` | `[Handles<Connection>]` 里每条客户端消息转发前的过滤（第 7 节） |
| `Takeover` | `[JoinOnConnect]` 上谁胜出：`NewestWins`（默认）或 `Reject`（第 7 节） |
| `PresenceLost` | `Connection` 上的通知：这个客户端的一个 Presence 没了（第 7 节） |
| `Restarting`、`Resumed`、`RestartingNodes` | 热重启：冻结前最后一个 tick 根上的 tag、加载后第一个 tick 的 tag；列出正在热重启的节点的节点单例（第 7、10 节） |

框架内部的数据、机制表（每个机制用了 EasyEcs 的什么）和诊断里看得到的内部名字，在 [architecture.md 第 2 节](architecture.md#2-runtime-structure)。

## 一段用户代码

第 12 节的完整例子里的四段：一个 Actor 类型、一条客户端消息的处理、一次问和它的回信、一个系统；最后是它们用到的声明，和第 12 节一字不差。两个词先说清：**主体**是一条消息或一个事件作用的实体，处理方法的组件参数都绑定到它（第 5 节）；**有序阶段**是每个 tick 开头，把这个 tick 窗口里的邮件和到期项按 `(时间, 序号)` 逐项执行的那一段（第 1 节）。

```csharp
[TickRate(30)]                                                             // the Map group runs 30 times a second
public readonly partial record struct Map(long Id) : IActor<Map>           // address, root component, system group
{
    [Persist]                                                              // the root and its components are the map's record
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetBuffer<Shout>();
}

[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>, Without<Dead>]                                            // subject: this client's avatar
    static void Move(in MoveTo m, Entity me, Mut<Position> p, Mut<Velocity> v, in Speed speed)
    {
        if (!Steps.IsValid(p.Value.Value, m.At, speed.Value)) { me.Push(new SnapBack(p.Value.Value)); return; }
        p.SetIfChanged(new Position(m.At));                                // applied now: later items see it
        v.SetIfChanged(new Velocity(m.Velocity));
    }
}

[Handles<Map>]
public static partial class Pickups
{
    [On<PickUp>]                                                           // the drop asks the player; the client is answered later
    static void Take(in PickUp m, in Account who, in Map map, in ReplyTo<bool> reply, Entity me, World world)
    {
        if (!world.NetIdIndex.TryFind(new NetId(map, m.Drop), out var drop) || !drop.TryGetLoot(out var loot)
            || drop.IsPending<GrantItem>()) { me.Reply(in reply, false); return; }
        drop.Ask(new Player(who.Id), new GrantItem(loot.Item), new Looting(reply), settleBy: TimeSpan.FromMinutes(5));
    }

    [On<Replied<GrantItem, Looting>>]                                      // subject: the drop; only final outcomes arrive
    static void Granted(in Replied<GrantItem, Looting> a, Entity drop)
    {
        var taken = a.TryGetValue(out var ok) && ok;
        drop.Reply(in a.State.Reply, taken);
        if (taken || a.Status == Status.Expired) drop.Destroy();
    }
}

[UpdateInGroup<Map>]
public class Movement : EcsSystem
{
    [ForEach, With<Steered>]                                               // every Map tick, after the ordered phase
    static void Integrate(Mut<Position> p, in Velocity v, in Time time) =>
        p.SetIfChanged(new Position(p.Value.Value + v.Value * (float)time.DeltaTime.TotalSeconds));
}

// ── the declarations used above, as in section 12 ──
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // Game.Protocol
[NinoType] public record struct PickUp(int Drop) : IFromClient<bool>;
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;
[NinoType] public record struct NearbyEvent(int Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);
public enum NearbyKind : byte { Appear, Moved, Disappear }
public readonly partial record struct Account(long Id) : IClaims;                      // Game.Server
[JoinOnConnect<Account>]
public readonly partial record struct Player(long Id) : IActor<Player>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(0, 40, 0);
}
[NinoType, From<Map>, Durable] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;
[NinoType] public partial record struct Grid(int Size, int CellSize) : IComponent;
[NinoType] public partial record struct Inventory(int Count, int Capacity, int Last) : IComponent;
public partial record struct NetIds(int Next) : IComponent;
public partial record struct NetId(Map Map, int Value) : IChildKey<NetId>;
public partial record struct Cell(Map Map, int X, int Y) : IComponent, IIndexed<Cell>, ITrackChanges;
public record struct Position(Vector2 Value) : IComponent, ITrackChanges;
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;
public record struct Steered : IComponent;
public record struct Speed(float Value) : IComponent;
public record struct Dead : IComponent;
[NinoType] public partial record struct Loot(int Item) : IComponent;
[NinoType] public record struct Looting(ReplyTo<bool> Reply) : IComponent;
[NotSaved] public record struct Shout(Cell Cell, NearbyEvent Event);
```

---
## 1. 保证

1. **每个 Actor 按 `(时间, 序号)` 逐条处理自己的消息和事件，Actor 之间并行。** `Activated` 也是邮件，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，同 Orleans：迁移那一两秒里，在途的消息可能后发先到。
   - 需要严格先后的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。**超时不等于没执行**：普通的请求结果未知时由发问方决定；`[Durable]` 的请求两端由框架负责：同一个调用 id 最多执行一次，重问到有结果；`Expired` 只表示收尾的一问也联系不上（第 8 节）。每条路径的投递、顺序、去重、超时和重试由谁负责，见第 11 节的表。
4. **同一个 Actor 任一时刻最多一个激活能写记录**：激活先占 epoch，每次写都按 epoch 比较并交换；比较失败的激活被隔离（第 10 节）。
5. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。旧连接留下的 Presence 在宽限期内保留，新连接接管它（第 7 节；先后由连接戳决定，见 [architecture.md 第 3 节](architecture.md#connection-stamps)）。
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
| c. 收尾（`Settling`） | 框架的系统：`Depart` 销毁带 `Leaving` 的 Presence，`SyncSubscriptions` 同步订阅（第 7、9 节），去掉 `Restarting` 和 `Resumed`（第 10 节）；然后框架组 `Cleanup` 处理已销毁实体留下的清理组件（`ReleaseRoutes` 等，第 6 节）；最后把 `Outbox` 交给链路 | 单线程 |

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
- **Program.cs**：大约五行，把生成的 `Registration.Create()`（第 12 节）交给节点，并显式声明三种服务管理器，各自不在对应的服务管理器下就什么也不做，所以同一个二进制到处能跑（[deployment.md 第 0 节](deployment.md#0-launch-flows)）：
  ```csharp
  var builder = WebApplication.CreateSlimBuilder(args);
  builder.Host.UseSystemd().UseWindowsService().UseLaunchd();   // the official two and DaServer's macOS counterpart
  builder.AddDaServer(Registration.Create());
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
  var cluster = builder.AddDaServerCluster("mmo");                         // also creates the Secret mmo-signing
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
  - **记录存储**：`WithActorStore(actors)` 给承载有状态 Actor 的组接上 DaServer.Persistence.PostgreSql；只承载 `Connection` 和 `[Stateless]` 的组不用。签名密钥是集群的一个 Secret `{cluster}-signing`（键 `current` 和可选的 `accepted`），由 `AddDaServerCluster` 生成，所有节点组共用，用户不写（[deployment.md 第 4 节](deployment.md#4-secrets-and-configuration)）。
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
    [Persist]                                                                      // saved: the map has a record
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetBuffer<Shout>();   // the root's own components
}
public readonly partial record struct Room(long Id)  : IActor<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IActor<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个 Actor 的地址。消息和组件里存别的 Actor 的地址，直接用 `Map`、`Player` 类型的字段。要存"任意 Actor 的地址"时用 `ActorId`。地址只在服务端：客户端看不到 `ActorId`、`PresenceId`、`EntityId`，也看不到节点（第 4 节）。

**同一个值也是组件**：地图的根实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 处理方法里知道自己是谁：取 Actor 类型参数 `in Map map`，它总是绑定到当前项所在 Actor 的根，不论主体是根还是子实体（第 5 节）。

**接口**：`IActor<T>` 继承 `IComponent`、`IUniqueIndexed<T>`、`IEquatable<T>` 和 `ISystemGroup`，并带着组过滤 `In<T>`；子实体的键用 `IChildKey<T>`。键都是唯一值索引（EasyEcs 4.7 计划的 `IUniqueIndexed<T>`）：一个 world 里两个同键的根、两个同 `NetId` 的子实体都是错误，按 EasyEcs 唯一值索引的语义在写入时抛出；查找照常是生成的 `world.NetIdIndex.TryFind`：

```csharp
public interface IActor<TSelf> : IComponent, IUniqueIndexed<TSelf>, IEquatable<TSelf>, ISystemGroup, IGroupWith<In<TSelf>>
    where TSelf : struct, IActor<TSelf>
{
    static virtual Prefab RootPrefab => Prefab.Empty;   // an actor type with root components declares its own
}
public interface IChildKey<TSelf> : IComponent, IUniqueIndexed<TSelf>, IEquatable<TSelf> where TSelf : struct, IChildKey<TSelf>;
```

- `IEquatable<TSelf>` 是继承的接口，不是约束：值索引要求 `T : IEquatable<T>`，写成约束的话，每个用到 `IActor<T>` 的泛型都要重复这条约束，否则 CS0314。record struct 本来就实现它。
- **根的 prefab 是 Actor 类型上的静态属性** `public static Prefab RootPrefab { get; } = …`：`Registration` 按 `K.RootPrefab` 读它，建占位和根都用它的副本（第 6 节）。没有根组件、也不保存的 Actor 类型不写。根要保存时在它上面写 `[Persist]`：属于哪个 Actor 类型由所在的类型推出，不写 `[Prefab<K>]`（第 10 节）；子实体的 prefab 才写 `[Prefab<K>(Persist = true)]`。叫 `RootPrefab` 不叫 `Root`：`e.Root` 是实体所在 Actor 的根实体（第 5 节）。

- EasyEcs 认源码里声明的接口，包括继承来的：它的生成器和分析器都用 `AllInterfaces` 判断（`Symbols.Implements`）。所以：
  - `[UpdateInGroup<TGroup>]` 的约束是 `struct, ISystemGroup`，C# 认继承来的接口；
  - EasyEcs 的分析器判断"是不是系统组"时，EECS023 不会误报。
  - EasyEcs 的生成器和分析器看不到别的生成器补上的接口和特性，所以要让它们看见的，都由用户写的接口或特性带着。生成器为要保存的组件类型补上的 `ITrackChanges` 只在运行时起作用，后果见第 10 节的"只写改过的"。
- **组过滤由接口带上（EasyEcs 4.5 的 `IGroupWith<T>`）**：EasyEcs 从组类型实现的接口上读组过滤，`IActor<TSelf>` 带着 `In<TSelf>`，所以 `[UpdateInGroup<Map>]` 的每个 `[ForEach]` 方法都只看 Map 的实体，用户不写 `[With<In<Map>>]`。不能写成接口上的特性 `[With<In<TSelf>>]`：C# 不允许特性的类型实参用类型形参（CS8968）。
- **`Key` 由生成器补上，写成显式接口实现** `Map IIndexed<Map>.Key => this;`（W1 核对 `IUniqueIndexed<T>` 也用它）：`IActor<T>`、`IChildKey<T>` 和以自身作键的 `IIndexed<T>` 的 `partial record struct` 都一样，Actor 类型、`Avatar`、`NetId` 和第 12 节的非唯一索引 `Cell` 都是。用户不写 `Key`。不能写成公有属性 `public Map Key => this;`：record 合成的 `ToString()` 会打印 `Key`，`Key` 又是自己，无限递归到栈溢出。
- EasyEcs 生成的成员是属性，不带括号：组件是 `e.HasLoot`，tag 是 `e.IsDead`。
- **按值找实体只有一种写法**，就是 EasyEcs 生成的索引成员：唯一键 `world.NetIdIndex.TryFind(key, out var e)`，非唯一键 `world.CellIndex.Find(key)`（枚举器，不分配）。
- Actor 类型的 id 是 `TypeId<Map>.Value`，没有单独的常量。
- `[TickRate]` 的规则在第 5 节。

**子实体的值索引，键里带上 Actor 的键**：一个 world 里有很多 Actor，所以子实体的键必须带着它所在的 Actor，否则两张地图的同名键会撞。写法只有一种：实现 `IChildKey<T>`，第一个字段是 Actor 类型（分析器检查），例如 `NetId(Map Map, int Value) : IChildKey<NetId>`、`Avatar(Map Map, Player Player)`；非唯一的索引（例如格子 `Cell(Map Map, int X, int Y)`）同样带上 Actor 类型。Actor 的键不随迁移和恢复改变，不用改写；不用根的 `EntityId` 作键。框架的 `PresenceId`（全 world 唯一的随机值）除外。

**只有运行时能写 Actor 类型组件：**
- 分析器禁止用户代码对 Actor 类型组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时建占位时，唯一索引保证这个键在 world 里还不存在（重复就抛出，是运行时的错误）。占位就是以后的根（第 6 节），键从建占位起一直在它上面，不搬动。
- 运行时按键找根用同一个索引的泛型写法 `world.Index<K, K>().TryFind(key, out var root)`（`world.MapIndex` 就是它）：泛型的 `Actor<K>` 取一次、按 world 缓存，生成的注册代码里没有查找委托（第 12 节）。

**键**：
- Actor 类型的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[ActorOptions]`**：Actor 类型可调的设置都在这一个特性里（`IdleTimeout`、`DetachGrace`、`MaxPending`、`OnError`），各自的规则在引用的那一节，不写就是默认值。`[TickRate]` 和 `[Stateless]` 不放进去：帧率定义了这个类型的系统组多久跑一次，`[Stateless]` 改变的是这个类型是什么（不保存、不迁移、每个节点都能有，分析器据此禁止一些操作），都不是调参。
- **`[Stateless]` Actor 类型**：不保存，不迁移；每个节点可以有自己的 Actor，键只用来选 Actor。它的地址不含节点，回信、确认和到期都找不回同一个实例，所以分析器禁止在它上面 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。它只做"收到请求，当场回复"。
- **子实体不可寻址**，例如 Avatar、等待中的请求、延后的事件。它们跟根一起保存、一起迁移。
- **没有"无键 Actor"**：
  - 每个 world 一份的缓存和配置，用 `IWorldSingleton<T>` 单例，调参的表用 `IConfigTable<T>`（第 5 节）；
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

- **接收方写在接口上**：`IMessageTo<TTo>` 是单向消息，`IRequestTo<TTo, TReply>` 是请求（它继承标记接口 `IRequest`，给 `IsPending<T>` 这类泛型操作做约束）。
- **发送方写在特性上**：`[From<K>]`，可以写多个。客户端只能发 `IFromClient`。
- **`[Durable]`**：写在 `IRequestTo` 上，发问方和接收方都由框架扛住宕机（第 8 节）。
- **消息里不写发送方的键**：接收方取 `Sender sender` 参数：`sender.Id` 是发送方的 `ActorId`，`sender.TryGet(out Map map)` 在发送方是 Map 时取出它的键（`TryGet<K>(out K key) where K : struct, IActor<K>`）。只有这一个 `Sender` 类型，所以一条消息有一个还是多个 `[From<…>]` 写法都一样。
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
- **没有组件参数的处理方法**（例如第 12 节的 `Enter`、`Cast`）编成不带组件的 job，同样经 `entity.Run(ref job[, filter])` 执行（W1 核对）。
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
  - **身份参数**：`IClaims` 类型的参数（例如 `in Account who`）绑定到发这条客户端消息的客户端的身份。身份随信封带着（不超过 16 字节）。只能用在客户端消息的处理方法里（分析器检查）。
- 系统没有当前项，所以 `[UpdateInGroup<K>]` 系统的 `[ForEach]` 方法不取 Actor 类型或 `IClaims` 参数（分析器报错）：要所在 Actor 的键，读 `e.Root` 上的组件。
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
| 身份 `in TClaims`（`IClaims` 类型） | 发消息的客户端的身份（上面的例外） |
| `ref DynamicBuffer<T>` | 主体上的缓冲 |
| `Sender` | 发送方：`sender.Id` 是 `ActorId`，`sender.TryGet(out K key)` 取出 K 类型发送方的键（第 4 节） |
| `in ReplyTo<T>` | 请求的回复令牌，要晚些回复时取；`reply.Call` 是调用 id |
| 单例 `in` | 本 world 的单例；`ref` 只给 `IWorldSingleton<T>`（下面） |
| `World` | ECS 操作 |

**单例不是 Actor 之间共享的可变状态**：一个 world 里有很多个 Actor，一个单例被它们共用。
- **用户的单例只有两种，都是接口**（同 `IClaims`：泛型约束和 EasyEcs 都看得见，生成器不用补任何东西）：
  ```csharp
  public interface IConfigTable<TSelf> : ISingletonComponent where TSelf : struct, IConfigTable<TSelf>
  {
      static abstract string Name { get; }                                   // DaServer:Tables:{Name}, POST /admin/tables/{Name}
      static abstract bool TryLoad(ReadOnlySpan<byte> bytes, out TSelf table, out string? error);   // decode and validate
  }
  public interface IWorldSingleton<TSelf> : ISingletonComponent where TSelf : struct, IWorldSingleton<TSelf>
  {
      static abstract TSelf Create();                                         // one per world, when the world is built
  }
  ```
  - `IConfigTable<T>`：调参的表，由集群统一读入、换版本（第 10 节的"不重启就能改的"）；处理方法和系统只能取 `in`，例如第 12 节的 `in SkillTable skills`。
  - `IWorldSingleton<T>`：world 自己的缓存或配置（例如寻路缓存），不是 Actor 的状态。生成的 `Registration` 列出处理方法和系统用到的每个这种类型，每个 world 建立时用 `T.Create()` 建一份。只有它能取 `ref`。
  - 两种都不迁移、不保存，也不进热重启的快照（新进程自己读入或建，第 10 节），所以类型不用能序列化。框架的单例（`Time`、`Current`、`Outbox`、节点单例）只能 `in`。
- 每个 Actor 一份的状态放在它的根上（`K.RootPrefab` 的组件）。
- 违反时分析器报错：`A singleton is shared by every actor in this world: take it 'in'; only an IWorldSingleton<T> (a world-local cache or configuration) may be taken 'ref'; keep per-actor state on the actor root`。不实现这两个接口之一的用户单例类型也报错。
- 为什么：Overwatch 的单例是一局一份，因为一局有自己的 world（出自 [GDC 2017 的演讲](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)）；这里一个 world 有很多 Actor，可写的单例会让一个 Actor 的处理方法改到别的 Actor 看得见的数据，迁移时还丢掉它。

### 操作

只有一个句柄：`Entity`。所有发送、推送、加入的操作都是 `Entity` 上的扩展方法（DaServer 提供，通过实体所在的 world 找到 world），只写在这张表里：

| 写法 | 做什么 |
|---|---|
| `e.Schedule(ev)` | 本地事件，`ev` 是 `IEvent`，发给本 Actor 的实体 `e`；当前项返回后、下一项之前执行（即时链，最多 1,024 个）。不建实体：事件是 world 的链栈里的一个值。返回 `Scheduled<T>` |
| `e.Schedule(ev, after: t)` | 同上，到期时刻 = 当前项的时间 + t（第 9 节）；建一个带 `Due` 的子实体。`t` 为 `TimeSpan.Zero` 时同上一行 |
| `e.IsPending<T>()`、`e.Cancel<T>()` | `e` 上还有没执行的 T 事件、或者还在等回信的 T 请求吗（链栈里的和子实体）；取消它们：事件不再执行，请求不再等、不再重问，回信到了就丢弃（第 8、9 节）。框架的两组扩展方法，放在两个静态类里：`where T : struct, IEvent` 和 `where T : struct, IRequest`（每个 `IRequestTo<,>` 都继承的标记接口）。两组签名相同、只差约束，同一个类里写不下（CS0111）；分在两个类里，C# 在重载决议时去掉约束不满足的候选，所以 `IsPending<Strike>()` 和 `IsPending<GrantItem>()` 各自只有一个候选（W1 核对）。`Cancel` 一个 `[Durable]` 的请求是编译错误（分析器）：要放弃就等期限（第 8 节） |
| `e.IsPending(s)`、`e.Cancel(s)` | `e.Schedule` 返回的那一个事件还在等吗；取消它 |
| `e.Send(to, msg)` | 服务器之间的单向消息，只发给别的 Actor，例如 `me.Send(new Player(id), new Defeated(map))` |
| `e.Ask(to, req[, state][, settleBy: t][, deadline: t][, call: id])` | 请求，回信是 `Replied`，主体是 `e`；返回调用 id。`deadline` 是每一次问的超时，不写用 `DaServer:AskTimeout`；`settleBy` 只有 `[Durable]` 的请求有，而且必写：整个承诺的期限。`call` 用同一个调用 id 重问（第 8 节） |
| `e.Reply(in token, value)` | 用回复令牌回复；令牌是 `default` 时什么也不做 |
| `e.Push(msg)` | `e` 是 Presence 或 `Connection` 的根：推给它的客户端 |
| `e.Push(in group, msg)` | 推给一个 `ClientGroup`（第 9 节） |
| `root.Broadcast(msg)` | `root` 是 Actor 的根：推给这个 Actor 所有带 `Joined` 的 Presence |
| `root.Group(key)` | `root` 是 Actor 的根：本 Actor 的 `ClientGroup`，一个值，键带着 Actor 的 `ActorId`；不改任何数据（第 9 节） |
| `root.SetGroupRate(key, every: n)` | `root` 是 Actor 的根：这个组的帧每 n 个 tick 交出一次，写在根上（第 9 节） |
| `e.SetGroups(groups)` | `e` 是 Presence：把它的 `DynamicBuffer<Subscribed>` 换成 `ReadOnlySpan<ClientGroup>` 里的组（可以 `stackalloc`，第 9 节） |
| `root.Spawn(prefab[, in a[, in b …]])` | 在 Actor 里建实体：用注册时的 prefab 副本，`world.Instantiate(副本, in new ChildOf(root), in a, in b …)`（EasyEcs 4.7 计划，最多 9 个组件）直接写进最终的表，当场返回实体 |
| `root.GetOrSpawn(prefab, key[, in a …])` | `key` 是 `IChildKey<T>`：按它的值索引找到这个 Actor 里的子实体；没有就像 `Spawn` 一样建一个，带上 `key` 和给的组件。所以加入消息再来一次，结果一样（第 7 节）。给的组件照 C# 的规则总会求值，只在新建时用 |
| `root.GetOrSpawn(prefab, key, static (Entity root, in K key) => c)`、`root.GetOrSpawn(prefab, key, in state, static (Entity root, in K key, in S state) => c)` | 同上，组件只在新建时由这个静态 lambda 算出（返回一个组件或几个组件的值元组）：不捕获，不分配；找到已有的实体时不调用它，所以不白花编号这类计数（第 12 节的 `Enter`） |
| `e.Join()` | 让 `e` 成为当前客户端在本 Actor 上的 Presence；返回 `Status`：`Ok`、`NoClient`（这一项没有客户端上下文）、`Superseded`（第 7 节） |
| `e.Leave()` | Presence `e` 离开：加上 `Leaving`（第 7 节） |
| `root.Deactivate()` | `root` 是 Actor 的根：停用这个 Actor（第 6 节）。不是根时运行时断言；分析器看得出来时报错 |
| `connection.Disconnect()` | `connection` 是 `Connection` 的根：断开这个客户端 |
| `root.IssueClientToken(in claims, lifetime, into)` | `root` 是 Actor 的根：签发一个客户端令牌（第 7 节）：新的 `ClientId`，签发序号是根上框架保存的计数器加一；写进 `into`（`Span<byte>`，至少 `SignedToken.MaxLength` 字节），返回写入的字节数 |
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
  - 没有 `[TickRate]` 的 Actor 类型也可以有系统：每批邮件之后跑（第 1 节）。所以这种组里的每个 `[ForEach]` 方法必须只访问有事的行：带 `[Changed<T>]`、`[Removed<T>]` 或过滤一个 tag 的 `[With<T>]`，或者系统设了 `UpdateInterval`；一样都没有就是每批邮件一趟全扫，分析器报错。代价相差约 70 倍：EasyEcs 复制 10 万个实体里改过的 10 个，只走改过的行是 525 ns，全扫是 38.6 µs（[change tracking](https://ecs.xgamedev.net/en/docs/features.md#change-tracking)）。
- **只处理自己的实体**：组过滤 `In<K>` 由 `IActor<K>` 带上（`IGroupWith<In<K>>`），加到这个组每个系统的每个 `[ForEach]` 方法上（EasyEcs `guide/systems.mdx` 的 "Group filters"）。
  - 查询缓存只存匹配的表，所以同一个 world 上别的 Actor 类型的表，对这个组的系统不增加任何开销（`guide/systems.mdx` 的 "What it costs"，`why-fast.mdx` 的 "query cache"）。
  - `In<K>` 是框架的 tag（`public struct In<T> : IComponent where T : struct, IActor<T>;`），K 类型 Actor 的每个实体都带着：
    - 根：运行时激活时加上；
    - `root.Spawn(prefab, in a, in b)`：`Registration` 注册 `[Prefab<K>]` 时做一份副本（EasyEcs 的 `new Prefab(basePrefab)`），带上 `In<K>` 和一列 `ChildOf(default)`，静态的 prefab 不被改动。`Spawn` 用 `world.Instantiate(副本, in new ChildOf(root), in a, in b)` 一次写进最终的表，不移动行（EasyEcs 4.7 计划；在那之前是实例化后一次 `Set`，移动一次行）；
    - Actor 里不用 `CreateEntity`（分析器报错），一律 `root.Spawn`，所以用户从不自己加 `In<K>`。
- **怎么跑**：每个 world 的线程按 Actor 类型依次调用框架组 `Ordering`（`OrderedPhase`）、`world.Update<Map>()`、框架组 `Settling`（`Depart`、`SyncSubscriptions`、去掉 `Restarting` 和 `Resumed`），最后是框架组 `Cleanup`（第 6 节）；循环的写法见 [architecture.md 第 2 节](architecture.md#2-runtime-structure)。`Ordering` 和 `Settling` 的系统是 DaServer 自己写的 `EcsSystem`，在 `OnUpdate` 里用按 Actor 类型建好的 `static readonly QueryFilter`（`.With<In<K>>()`）跑 job，所以不需要以类型形参为实参的特性。一个 world 上同一 Actor 类型的 Actor 共用 tick 边界，所以一次覆盖这个 world 上这个 Actor 类型的所有 Actor。
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
  - `Registration.Create()` 是生成在服务端工程根命名空间里的 C# 14 静态扩展成员，容器类按程序集命名（`Game.Server.GameServerRegistration`）：`using Game.Server;` 之后写 `Registration.Create()`。一个测试工程引用两个服务端工程、两个命名空间都 `using` 时，这个调用有歧义，改写成容器类上的调用 `GameServerRegistration.Create()`。

### 处理方法抛出异常

**不回滚。** EasyEcs 的结构变更当场生效，没有命令缓冲（EasyEcs `decisions.mdx` D2），组件写入也是当场的。所以抛出时：
- 抛出之前做的修改都留着：建的实体、加删的组件和 tag、经 `ref` 参数和方法体写的组件；结构变更之后经 `ref` 参数的写入，EasyEcs 在 job 抛出时也搬到新行（4.5 的 `entity.Run` 保证）；
- 已经追加进 `Outbox` 的照样发出（`[Durable]` 留住的输出按下面 `Reload` 的规则）；
- 这条消息的请求以 `Faulted` 结束（终止的结果，不是 `Transient`：同样的输入会再抛一次）；它的即时链里还没执行的事件作废。

之后怎么办，看 Actor 类型上的 `[ActorOptions(OnError = …)]`：
- `ErrorAction.Reload`：不写记录，不进 `Leaving`，不加 `Deactivating`。
  1. 销毁根的所有子孙；
  2. 根回到注册时 `K.RootPrefab` 的副本：去掉副本以外的组件（tag `Unsaved` 也明确去掉），副本上有的组件改回副本的值；键和排队的邮件（`Mail`）留着，带上 `Loading`，就是占位；
  3. 这一批里这个根还没执行的邮件，按原来的 `(时间, 序号)` 移进 `Mail`；
  4. 等这个根正在进行的写记录有结果，再从最后一次写成的记录重新激活（第 6 节的表，同样经 `ActivationSlots` 和退避）。写记录的结果按发起它的激活的 epoch 认：旧激活的结果不去掉新激活的 `Unsaved`，也不放出新激活留住的输出。

  上次写记录之后的改动丢失，排队的邮件不丢。为 `[Durable]` 留住的输出（第 8 节）跟着它等的那次写：那次写已经发出并且写成，就放出；还没发出的随内存丢弃，记录里本来就没有它们。Presence 是清理组件，销毁时框架释放它们的路由，客户端 SDK 收到 `PresenceLost` 后重新发加入消息（第 7 节）。抛出的是 `[Durable]` 请求时，它的调用 id 留在占位上；重新激活、记录装好之后并进 `ReplyLog`，记成 `Faulted`，根标 `Unsaved` 当场写记录，写成之后才回答 `Faulted`。之后重复的请求得到同样的回答，不再执行。同一个键连续 `Reload`（节点配置的次数）就进入下面激活失败的退避。
- `ErrorAction.Continue`：实体就是现在的样子，接着处理下一项。
- **默认按根要不要保存分**：根的 `RootPrefab` 带 `[Persist]` 的 Actor 类型默认 `Reload`，其余默认 `Continue`。原因是不回滚：改了一半的项留在内存里，下一个检查点就把它写进记录，宕机和迁移都去不掉它；`Reload` 丢掉内存，从上一份完整的记录接着来。没有记录的 Actor 只有内存这一份，丢掉它不比留着好。`[Durable]` 请求的接收方只能用 `Reload`（第 8 节）。

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
| 加载中 | 占位，带 `Loading` | 第一条消息到达本节点，本节点按自己的视图是这个键的主人（不是主人就转发，不激活）；或者处理方法抛出、`OnError` 是 `Reload`（根要保存的 Actor 类型的默认，第 5 节） | 邮件留在占位上；拿到 `ActivationSlots` 的名额后，一条语句占 epoch 并读记录（第 10 节） |
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
- `OnError = Reload` 不走这张表的停用，见第 5 节。
- **框架销毁整棵子树之前，先从子孙上移除不该触发清理的清理组件。** 销毁带清理组件（`ICleanupComponent`）的实体时，EasyEcs 不释放它：它失去其他所有组件（包括 `In<K>`），离开所有索引，带上 `Destroyed`，等清理系统移除最后一个清理组件（`guide/cleanup-and-disable.mdx`）。框架的两个清理组件是 `Presence`（释放路由，第 7 节）和要保存的实体上的 `Persisted`（给根标 `Unsaved`，第 10 节）：

  | 销毁的原因 | 先移除 `Persisted` | 先移除 `Presence` |
  |---|---|---|
  | 停用（写完 `SaveEntities` 之后） | 是：记录已经写进缓冲，根也要销毁 | 不用：这时已经没有 Presence（第 1 步 `Leave()`，`Depart` 已经移除） |
  | 迁移（写完迁移字节之后） | 是 | 是：Presence 随字节到了新节点，路由不释放 |
  | `OnError = Reload`、隔离、激活失败 | 是：不写记录 | 不移除：路由要释放，客户端 SDK 据此重新加入 |

  所以只有用户自己的销毁、`Reload`、隔离和激活失败留下待清理的实体，没有白做的清理。
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

本节是用户看得到的部分。连接戳、`TakeOver`、路由的登记和核对、`Retire` 和 `HandedOff` 栅栏、按负载的再平衡和接纳令牌桶是框架内部的协议，在 [architecture.md 第 3 节](architecture.md#3-messaging)。

### `Connection` 实体和令牌

- **令牌**：客户端连接时出示一个令牌：DaServer 的 `SignedToken`（集群的签名密钥签名，带签发和过期时刻），内容是 `(ClientId, 签发序号, 身份)`。
  - **签发**：`cluster.IssueClientToken(in claims, ClientId.New(), issueSeq, lifetime, into)`：`into` 是调用方给的 `Span<byte>`（至少 `SignedToken.MaxLength` 字节），返回写入的字节数，不分配。Actor 里用 `root.IssueClientToken(in claims, lifetime, into)`（第 5 节的操作表），签发序号是根上框架保存的计数器，每次加一。
  - **签发序号先落定，令牌才离开**：否则签发方在令牌发出之后、记录写成之前宕机，从旧记录恢复后同一个序号会再签给另一次登录。所以令牌由应用在一个 `[Durable]` 请求的处理方法里签发，令牌就是回复，回复在记录写成之后才放出（第 8 节）。mmo 样例的登录服务就这样问账号 Actor。
  - **刷新**：`cluster.RefreshClientToken(token, lifetime, into)` 换新的过期时刻，保留 `ClientId` 和签发序号。
  - **`ClientId`**：`readonly record struct ClientId(ulong Value)`，每次登录随机取一个，重连和刷新都不变；它是 `Connection` 的键、客户端调用 id 的前缀（第 8 节），也是 `cluster.Disconnect(clientId)` 的参数。
- **激活**：客户端连上一个承载 `Connection` 的节点后，框架用令牌里的 `ClientId` 激活 `Connection(ClientId)`。
- **身份**：令牌里的声明解码成用户写的 `IClaims` 类型，例如 `Account`，作为组件加到 `Connection` 上，并随它转发的每条客户端消息带上（第 5 节的身份参数）。框架不认识任何业务字段。
- **位置固定**：`Connection` 固定在连接所在的节点，不参与放置，也不迁移；连接断开，它就停用。
- **扩展**：写 `[Handles<Connection>]` 类；要给它加组件（例如令牌桶），在它的 `[On<Activated>]` 里当场 `Set` 上（`Connection` 是框架的类型，没有用户写的 `RootPrefab`）。
- **找不到它**：别的 Actor 不能用 `ClientId` 找到 `Connection`。推送只经过 Presence，所以不需要位置目录。

### Presence

客户端在一个 Actor 上的存在，叫 **Presence**：目标 Actor 的根的一个直接子实体，例如地图上的 Avatar。框架给它加 `Presence`（清理组件，按 `PresenceId` 建值索引）；网关上有一条通往它的路由；客户端 SDK 只知道一个**槽号**（这个连接上每个加入过的 Actor 类型一个小整数，不含 Actor 类型、节点和地址）。

- **每个 Actor 类型最多一个 Presence**：一个客户端同一时刻最多在一张地图上、一个 Player 上。
- **`PresenceId`** 是 64 位随机数，只在服务端；接管时换新的。
- Presence 的状态都是数据：

  | 状态 | 是什么 | 谁反应 |
  |---|---|---|
  | `Joined(bool MissedPushes)` | 网关确认了路由；推送和 AOI 只认带它的 Presence。`MissedPushes` 为真时客户端可能漏过推送（第一次加入、断线后重连、接管），只有在客户端因网关升级换连接、一个推送也没漏时为假 | 快照系统 `[ForEach, Changed<Joined>]`，`MissedPushes` 为假时跳过（第 12 节的 `SendFullState`） |
  | `Leaving` | 正在离开 | 用户的系统 `[ForEach, With<Leaving>]`；之后框架的 `Depart` 销毁它 |
  | `Detached` | 连接断了，宽限期内保留（下面） | 一般不用反应 |

### 连上就加入：`[JoinOnConnect<TClaims>]`

```csharp
public readonly partial record struct Account(long Id) : IClaims;                       // the token's claims: the user's own type
[JoinOnConnect<Account>] public readonly partial record struct Player(long Id) : IActor<Player>;   // joined at connect, keyed by the claims
```

- **身份是用户自己的类型**，实现 `IClaims`（`public interface IClaims : IComponent`），不绑在 Player 上；非托管、不超过 16 字节，一个工程最多一个（分析器检查）。
- **想在连上时就加入的 Actor 类型，自己声明** `[JoinOnConnect<Account>]`（`where T : struct, IClaims`）。这个 Actor 类型的键和身份形状相同（同样类型、同样顺序的字段），生成器按字段一一对应，`Account(42)` → `Player(42)`；形状不同，分析器报错。可以有多个 Actor 类型挂在同一个身份上。
- **谁胜出是声明上的参数**：`[JoinOnConnect<Account>(Takeover = Takeover.NewestWins)]`，默认 `NewestWins`：更新的连接接管，旧连接被关闭；`Reject`：已有的连接留下，新的收到 `AlreadyConnected` 后断开（半开的旧连接要等它超时）。
- 框架做完整件事，用户不写代码：建或接管 Presence，失败时按 `RetryAfter` 或带抖动的退避重试，超过次数以 `Unavailable(retryAfter)` 结束会话；Presence 丢失（Player 重启）时框架重新加入。
- 加入好之前，客户端消息在 `Connection` 上排队，有上限（节点配置），超过的回 `Busy`；加入好后框架推一条 `Ready` 给客户端。
- Player 上的 Presence 和别的 Presence 一样：不带 `[To]`、由 Player 处理的客户端消息，主体就是它。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Connection` 实体。
2. 先过过滤：`[Handles<Connection>]` 类里的 `[On<Incoming>] static Verdict Allow(in Incoming m, ref RateLimit r)`。`Incoming` 是帧头的视图（`TypeId`、长度、身份），网关从不解码负载。它逐条执行，返回放行或者拒绝的原因；没写就全部放行。
3. 按生成的路由表，得到处理 Actor 类型和目标：带 `[To]` 的是这个 Actor 类型、键是这个参数的 Actor，没有就激活；不带 `[To]` 的是这个客户端在该 Actor 类型上的 Presence，没有就拒绝，`NotJoined`。
4. 直接转发给目标，信封带着客户端上下文。Presence 带着 `Leaving`，或者已经被接管，回 `NotJoined`。
5. 被拒绝时，客户端收到原因：`NotJoined`、`Busy`、`NoActor`、`Filtered`、`Superseded` 等。

### 加入：`e.Join()`

例子是第 12 节的 `Maps.Enter`：`root.GetOrSpawn` 找到或建好 Avatar，然后 `return avatar.Join() == Status.Ok;`。

**本地当场生效**（EasyEcs 的结构变更当场生效，`decisions.mdx` D2），结果是一个 `Status` 值，不是异常：
1. `e` 必须是根的直接子实体，带 `In<K>`（`root.Spawn` 建的就是），否则抛出（这是代码错误）；
2. 当前项没有客户端上下文（系统里、`Activated` 里，或者请求是从没有客户端的地方发出的，第 4 节）：返回 `NoClient`，什么也不改；
3. `e` 已经是 Presence 时：同一个连接返回 `Ok`（幂等）；已有的连接更新，返回 `Superseded`，什么也不改；当前的连接更新，就接管：去掉 `Joined`、`Leaving` 和 `Detached`，换新的 `PresenceId`，旧连接的路由被释放，旧连接被关闭；
4. 加上 `Presence` 和确认期限，把路由追加到 `Outbox`，返回 `Ok`。路由一定排在这条请求的回复前面（第 5 节的 `Outbox`）。

- 分析器要求用到 `Join()` 的返回值。
- 网关写好路由后确认，Presence 带上 `Joined`；有 `[TickRate]` 的 Actor 类型先给客户端推一条框架通知，带帧长、当前 tick 号和服务器时间（第 9 节），之后推送才开始。确认期限到了还没确认（客户端已经断开），框架对它 `Leave()`。
- 客户端的请求已经超时的，Presence 照样建好，只是回复被丢弃。
- **换地图**：客户端发 `EnterMap(8)`。地图 8 加入后，网关删掉地图 7 的路由，地图 7 对那个 Presence `Leave()`。两张地图是两个 Actor，它们之间没有先后：短时间内客户端可能同时出现在两张图上。需要严格先后的业务，用请求串起来。

**`Superseded` 的意思只有一个：有更新的连接或者更新的登录。** 它出现在三处：`Join()` 返回它（已有的连接更新）；客户端消息被拒绝时的原因（这个连接已被接管）；客户端 SDK 收到的终止事件（同一账号在别处登录了更新的令牌：SDK 报给应用，不自动重连）。同一次登录就是同一个 `ClientId`：它换了连接（重连、网关升级）不是 `Superseded`，旧连接安静地关闭。签发序号相等、`ClientId` 不同的（同一个序号签了两次），也按更新的登录处理，旧连接以 `Superseded` 关闭。

### 推送

`e.Push(msg)`、`root.Broadcast(msg)`、`ClientGroup`（第 5 节的操作表、第 9 节）。只推给带 `Joined` 的 Presence。

### 离开：`Leaving` 是数据

- **`e.Leave()`** 当场：加上 `Leaving`，去掉 `Joined`，把路由的释放追加到 `Outbox`；客户端 SDK 收到 `PresenceClosed(槽号)`，不重新加入这个槽。
- **用户的系统反应**：`[ForEach, With<Leaving>]`，例如广播"消失"（第 12 节的 `Vanish`）。可能已经不在的组件用 `TryGetX`。
- **收尾组的 `Depart`** 排在用户的系统之后：去掉 `Presence`，销毁实体。
- 带 `Leaving` 的 Presence 不再收客户端消息（`NotJoined`）。同一项或同一帧里再 `Join()` 它，就去掉 `Leaving`，算作接管。
- **外部触发的离开**（被 `Disconnect` 踢掉、加入了同一 Actor 类型的另一个 Actor）由框架作为邮件送到目标，在目标的处理过程中 `Leave()`。
- **同一个键不会有两个 Presence 实体**：同一个 Player 再进同一张图时，`GetOrSpawn` 找到已有的 Avatar，`Join()` 接管它。

### 断线：宽限期内的 `Detached`

- 连接断开时（客户端断线、被接管后关闭、读得太慢被断开），或者网关节点离开成员视图时，目标不立刻 `Leave()` 这些 Presence：去掉 `Joined`，加上 tag `Detached` 和期限（现在 + 宽限期）。
- 宽限期默认 30 秒，在 Actor 类型上写 `[ActorOptions(DetachGrace = 秒)]` 改；0 就是立刻 `Leave()`。
- 带 `Detached` 的 Presence：不收推送，不让 Actor 保持活跃（第 6 节的空闲判断不算它），实体和它的组件都还在。
- 这期间客户端的消息：断开时还没送达的丢失；重连之后、这个槽重新加入之前，不带 `[To]` 的请求回 `NotJoined`，单向消息丢弃并计数（第 11 节的表）。
- 客户端重连，新连接接管它：去掉 `Detached`，照常确认，`Joined.MissedPushes` 为真，所以快照系统重推一份。`[JoinOnConnect]` 的 Presence 也一样。
- 期限到了还没被接管，框架对它 `Leave()`。
- `cluster.Disconnect(clientId)`、`connection.Disconnect()` 是有意的断开：Presence 立刻 `Leave()`，没有宽限。
- 一个地区的网络抖一下、或者一个网关宕机，客户端重连回来时 Presence 还在，Actor 不用重建它们。

### Presence 丢失：`PresenceLost` 和 SDK 重新加入

目标重启了（从记录恢复、`OnError = Reload`、隔离），或者 Presence 没经过 `Leave()` 就被销毁（父实体级联销毁、用户 `Destroy()`）：`Presence` 是清理组件，框架释放它的路由，网关投递 `PresenceLost`。迁移不是丢失：Presence 随 Actor 一起到新节点。
1. 投递：给客户端 SDK `PresenceLost(槽号)`；给 `[Handles<Connection>]` 的 `[On<PresenceLost>]`，写了才投递。
2. **SDK 重新发这个槽的加入消息**：SDK 为每个槽记着最后一条加入它的消息，收到 `PresenceLost(槽号)` 时按带随机抖动的退避重发它（收到 `Busy` 时按它的 `RetryAfter`），带着当前的身份；目标因此重新激活，加入消息的处理方法再调用一次 `Join()`。
   - 同一个槽后来加入的消息替换先前的；`PresenceClosed(槽号)` 清掉它；
   - 重连之后，`Ready` 一到，SDK 重发所有槽的加入消息，新连接接管目标上旧连接留下的 Presence；
   - 重发的回复由 SDK 自己收下，应用只看到 SDK 的事件。
- **限制**：
  - 只重发那一条加入消息，不重发之后的消息；之后的状态以目标的记录为准；
  - 加入消息的处理方法用 `root.GetOrSpawn` 建 Presence 的实体，再来一次结果一样，不用另写幂等的逻辑（第 12 节的 `Enter`）；
  - 连上就加入（`[JoinOnConnect]`）的 Presence 不靠 SDK，由框架重新加入。

### 节点热重启、网关升级：客户端不断线

- **承载 Actor 的节点热重启**（第 10 节）：发给它的消息在网关和对端的重发窗口里等着，它回来后送达。想让客户端看到提示（例如"地图重启中"）的，在网关上写一个带 `UpdateInterval` 的系统（例如每秒一次）：节点单例 `RestartingNodes` 列出正在热重启的节点和各自的预计停顿；`nodes.TryGetPause(connection, out var pause)` 看这个连接的路由有没有指向其中一个，有就 `connection.Push(…)`。
- **网关升级、缩容、扩容后的再平衡**：框架让客户端先连上新网关、再关旧连接，每个 Actor 的推送保持顺序，`Joined.MissedPushes` 为假，快照系统不重推。用户不写代码，客户端不多知道任何拓扑；做法见 [architecture.md 第 3 节](architecture.md#retire-and-rebalancing)，各平台的部署见 [deployment.md 第 2 节](deployment.md#stateless-groups-and-gateways-deployment)。

---

## 8. 等待别的 Actor 的结果

例子是第 12 节的 `Pickups`：客户端要捡起地上的掉落，掉落自己向 Player 要求发放物品（`[Durable]` 的 `GrantItem`），框架替它问到有结果为止。

- **`e.Ask(to, in message[, state][, settleBy: t][, deadline: t][, call: id])`** 创建一个等待实体，它是 `e` 的子实体，带：
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
  - 发问的实体先被销毁了（例如 Avatar 离开），等待实体随它一起销毁，回信到达时丢弃。所以要收尾的，从根或者不会离开的子实体发问（在 Presence 上 `Ask`，分析器给提示）。`[Durable]` 的等待实体不能这样丢掉，见下面。
- **超时**：`Due` 到了，框架投递一个 `Status = TimedOut` 的 `Replied`，和别的项一样按 `(时间, 序号)` 执行。
- **超时不等于没执行**：请求可能已经执行，只是回复没赶上。普通的请求，结果未知时由发问方决定：
  - `a.Status.Transient` 说明值得重试；要重试，延后 `a.RetryAfter`，用同一个调用 id 再问（`me.Ask(to, req, state, call: a.Call)`）；
  - 框架不替普通请求去重；接收方要去重，自己按 `reply.Call` 记。改别人状态、不能执行两次的请求，写成 `[Durable]`（下面），不自己记。

### `[Durable]`：两端由框架负责

写在 `IRequestTo` 上（第 4 节），给必须落定一次的请求：发物品、扣款、下单、签发令牌。普通请求照旧：当场回复，结果未知由发问方决定。

- **落定之前，输出排队**（两端共用这一条）：一项要先落定时（发出 `[Durable]` 请求、处理 `[Durable]` 请求、处理 `[Durable]` 的 `Replied`），这一项追加进 `Outbox` 的全部输出（请求、回复、推送、单向消息，不只是请求或回复）都不交给链路，按顺序留在根上（内部的 `HeldOutput`）；这个 Actor 之后各项的输出也排在它们后面，先进先出。根标 `Unsaved`，`CheckpointSystem` 不等检查点间隔当场写记录，epoch 比较并交换成功后按顺序放出。所以对外的先后和执行的先后一致，没有一条输出走在它依赖的那次落定之前。
- **发问方**：
  - `Ask` 必写 `settleBy:`（生成的方法里它不是可选参数），它是整个承诺的期限；`deadline:` 照常是每一次问的超时，不写用 `DaServer:AskTimeout`，不超过剩下的期限。等待实体带着请求、状态、调用 id 和到期的 UTC 时刻（`Time.Utc` + `settleBy`），是要保存的实体，随 Actor 的记录保存（状态因此要能写进记录，第 10 节）。
  - **请求在等待实体写进记录之后才发出**（上面的规则）。否则发问方在请求发出、记录写成之前宕机，从旧记录恢复后不知道自己问过，同一件东西会再许给别人。
  - 结果是 `Transient`（`TimedOut`、`ConnectionLost`、`Busy`、`NoOwner`）时，框架用同一个调用 id 再问：等 `RetryAfter`（没有就指数退避），加完全随机抖动，计入每个节点的重试预算（预算空了就等下一次退避，不算失败）。Actor 从记录重新激活之后（宕机、迁移退回记录）和热重启之后，框架对每个还在等的等待实体再问一次；接收方去重，无害。
  - **收尾的一问**：离期限还剩一个 `AskTimeout` 时，框架不再普通地重问，改发一次收尾的问（同一个调用 id，带收尾标记）。接收方回答它：`ReplyLog` 里有结果就用那个结果；回复还留着没放出就等它放出；两样都没有，就把这个调用 id 作为"拒绝"记进 `ReplyLog`（和别的回复一样，记录写成后才回答），回 `TooLate`。所以之后迟到的同一个请求（例如在重发窗口里等过的）查到拒绝，永远不会执行。收尾的问也没有回答，回信才是 `Status.Expired`：它只表示"联系不上，结果未知"。
  - 所以 `Replied` 处理方法只看到最终的结果：回复的值、确定没执行的拒绝（`NotExecuted` 而不是 `Transient` 的，例如 `TooLate`、`NoActor`、`Skipped`），或者 `Expired`。
  - **处理回信也要先落定**（上面的规则）：否则发问方在回信之后、记录写成之前宕机，从记录恢复的等待实体再问一次，同一个结果被处理两次，对外说的话不一致。
  - **等待实体不能被用户的销毁带走**：销毁一个下面还有等待中的 `[Durable]` 请求的实体（发问的实体或它的祖先）算作这一项抛出，按 `OnError` 处理（第 5 节；`Reload` 从记录恢复它）。等待实体带框架的清理组件，项结束时运行时据此发现它被连带销毁。正在执行的那个 `Replied` 的等待实体不算（第 12 节的 `Granted` 销毁掉落）。分析器看得出时给提示。
  - `[Durable]` 的请求不能取消（`Cancel` 它是编译错误，第 5 节的操作表）：要放弃就等期限，期限以收尾的一问结束。
  - 还在等的 `[Durable]` 请求让 Actor 保持激活（算在待处理的个数里，第 6 节）。等待实体带着持久化的 `WakeAt`，值是请求发出的时刻：发问方宕机后，它的键在 `DueScan` 的下一次扫描就到时，Actor 被唤醒、再问（第 10 节的"唤醒"）。
- **接收方**：
  - **接收方的 Actor 类型必须要保存，并且用 `OnError = Reload`**（分析器报错）：`Continue` 会让改了一半的状态和 `Faulted` 的回答一起写进记录。
  - 请求到达时依次：
    1. **去重**：同一个调用 id 已经在 `ReplyLog` 里，不调用处理方法：回答已经落定的，用 `ReplyLog` 里的回答；还在排队等落定的、或者处理方法取了 `ReplyTo<T>` 还没回复的，丢弃（之后放出的那一个回复回答这个调用 id）。
    2. **不开始来不及落定的请求**：请求剩下的时间不够写一次记录（节点按最近的写延迟估计）时，不调用处理方法，回 `TooLate`（确定没执行）。
    3. 调用处理方法：框架把 (调用 id, 回复, 发问方的期限) 记进根上的内部环 `ReplyLog`，它和处理方法的改动在同一次写记录里落定；回复（返回值或者 `Reply(token)`）和这一项的其他输出按上面的规则排队，写成之后才放出。
  - 处理方法抛出时，调用 id 记成 `Faulted`，写进记录之后才回答（第 5 节的 `Reload`）：终止的结果，重问得到同样的回答。
  - `[Durable]` 请求的 `ReplyTo<T>` 只能存进随记录保存的数据（要保存的实体上的组件、`[Durable]` 的 `Ask` 状态，分析器按看得出的地方检查）：令牌丢了，重复的请求又被丢弃，发问方的收尾一问只能等到 `Expired`。
  - `ReplyLog` 的项留到发问方的期限再加 `DaServer:ClockSkew` 之后（过了期限的请求不执行，第 9 节）。回复和 `Faulted` 的项每个 Actor 最多 `DaServer:ReplyLogCapacity` 个（默认 1,024）；满了、又都没过期时，新的 `[Durable]` 请求回 `Busy`，`RetryAfter` 是最早一项过期的时刻（确定没执行）。收尾一问记下的"拒绝"不计入这个上限：它只有调用 id 和期限，到期就删，所以收尾的一问从不因为满了而得不到回答。
  - 比较失败说明这个激活已经被隔离：排队的输出都不发，改动和 `ReplyLog` 都没写进存储，发问方重问时由真正的主人执行一次。
- **结果**：一个调用 id 最多执行一次；调用方看到的结果（成功或确定没执行），任一端宕机都不会丢；`Expired` 只在收尾的一问也联系不上时出现。代价是两端各多一次写存储的延迟，期间这个 Actor 的输出排队。不标的请求宕机时最多丢一个检查点间隔的改动（第 10 节）。
- `ICluster` 带 `CallId.From(外部键)` 发 `[Durable]` 的请求时，接收方一样去重；调用方在 Actor 外面，不持久化，重试由它自己（第 4 节）。

### 客户端的调用 id、`ReplyTo<T>`

- **客户端的调用 id**：SDK 每个会话一个计数器，从一个随机的 63 位值开始，重连后接着用（应用重启后用同一个令牌重连，新的计数器也不会撞上旧会话的调用 id）；网关在前面加上 `ClientId`，所以处理方法读到的 `reply.Call` 就是 `(ClientId, 客户端调用 id)`，全集群唯一，去重就用它。应用重试时写 `client.Ask(msg, call: id)`，用同一个 id。连接断了还没收到回复的调用，SDK 以 `ConnectionLost` 结束：结果未知，同超时。单向的客户端消息不带调用 id：最多一次，SDK 不重发，不用去重。
- **问多个目标**：对每个目标各 `Ask` 一次，每个目标一个 `Replied`。要汇总时，把计数放进发问实体上的组件。
- **`ReplyTo<T>` 是普通数据**：
  - 请求的处理方法取 `in ReplyTo<T> reply` 参数；`T` 就是请求声明的回复类型，`Reply` 在编译期检查类型；
  - 要先问别人再回复：把令牌放进 `Ask` 的状态（第 12 节的 `Looting.Reply`），回信时 `me.Reply(in a.State.Reply, …)`；
  - 要晚些回复、不问别人：把令牌存进任何组件，之后 `Reply`；存在要保存的实体上的令牌跨过停用和宕机，期限内照样能回复：写进记录时它的期限按 UTC 时刻存（同 `[Durable]` 的等待实体，比较时多留 `DaServer:ClockSkew`），在内存里和迁移的字节里照常是剩余时间（第 9 节）；
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

- **一条时间规则：Actor 里的代码只从 `Time` 读时间。** 先后、延后、超时用 `Elapsed`（单调）；要跨停用、跨进程留下来的绝对时刻（buff 在某日到期、`[Durable]` 的期限、提醒）用 `Utc`，存进要保存的实体（第 10 节）。
  - `Utc` 来自 world 的 `TimeProvider`（测试里是 DaServer 自己的手动时钟，ADR-071），每一项开始时读一次，同一项里不变；
  - 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`，分析器给警告；
  - 各节点的 UTC 只差对时的误差，所以框架用到 UTC 的地方（`ReplyLog` 留多久、`[Durable]` 的期限、提醒）都多留一个余量 `DaServer:ClockSkew`，排序和期限从不比较两个节点的 UTC。

- **`Elapsed` 是正在执行的那一项的时间**：处理消息时是它进入 world 的时刻，执行延后事件时是它的到期时刻，系统里是 tick 时刻 t。
- 系统运行期间，`Tick`、`DeltaTime`、`TickRate` 是这个 Actor 类型的值；没有 tick 的 Actor 类型，它们是 0。
- **`after` 从当前项的时间算起**：到期时刻 = `Time.Elapsed` + after，存精确值，不存 tick 号。它在 tick 窗口所在的那一帧执行（第 1 节），从不提前。
- 数据里写秒或毫秒，例如配置表 `skills.Windup(id)` 返回 `TimeSpan`（第 12 节的 `SkillTable`）。
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

- **`ClientGroup`**：一个值，`root.Group(long key)` 得到：键前面带着这个 Actor 的 `ActorId`，所以两张地图的同号格子不会撞。取这个值不读也不写任何数据。
- **订阅是数据**：Presence 上的 `DynamicBuffer<Subscribed>`，`Subscribed(ClientGroup Group)`。
  - `e.SetGroups(groups)` 就是改写这个缓冲，`groups` 是 `ReadOnlySpan<ClientGroup>`（可以 `stackalloc`）；
  - 收尾组的 `SyncSubscriptions`（`[Changed<DynamicBuffer<Subscribed>>]`，以及刚加上 `Joined`、刚被接管的 Presence）把这些 Presence 的完整订阅集合发给网关，网关整个替换（幂等，不用记上次发了什么）。只有带 `Joined` 的 Presence 是成员。迁来的 Presence 也由它订阅（迁移到达算作写入，第 10 节）；
  - `e.Push(in group, msg)` 一次序列化，每个有成员的网关节点收到一帧，由网关扇出；`root.Broadcast` 也一样。
- **每个组的发送频率是数据**：`root.SetGroupRate(key, every: n)`，n 是 tick 数，写进根上框架的 `DynamicBuffer<GroupRate>`（`GroupRate(long Key, int Every)`；随迁移和热重启的快照走，不进记录）。一般在 `[On<Activated>]` 里给每个要低频的组设一次（每次激活都会跑）；最近一次设的生效（从下一个该发的 tick 起），`every: 1` 或者没设过就是每个 tick。推给这个组的消息照常一次序列化、追加进 `Outbox` 里这个组的池化帧，但这一帧到了组该发的那个 tick 才交给链路，期间的推送合在同一帧里，一条不少。计数器就在这个组的帧缓冲上，不分配。
  - 频率不改变组的身份：订阅和推送都用 `root.Group(key)`，是同一个组。远处的格子、不重要的实体用低频，近处用高频，同 Unity Netcode 的 `MaxSendRate` 和按重要性排的快照（[optimizations](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/optimizations.html)）、SpatialOS 兴趣查询的 frequency。
  - 只对有 `[TickRate]` 的 Actor 类型有意义，别的 Actor 类型调用 `SetGroupRate` 分析器报错。推给同一个客户端的直接推送和低频组的推送之间不保持先后。
  - **成员变化前先交出**：一个组的成员变化（`SyncSubscriptions` 发给网关的订阅集合里加上或去掉这个组，Presence 离开）追加进 `Outbox` 之前，这个组没到点的帧先交给链路。所以网关总是先按旧的成员扇出旧的推送：刚离开的不会漏掉离开前的推送，刚加入的不会收到加入前的推送。
  - Actor 停用、迁出，或者节点热重启冻结之前的最后一个 tick，没到点的组帧当场交出，所以记录、迁移的字节和快照里都没有它。被隔离的 Actor 的组帧直接丢弃：隔离不发出任何东西。
- **整个 Actor**：`root.Broadcast(msg)`，推给它所有带 `Joined` 的 Presence。
- **小组**（公会、队伍）：一个固定键的 Actor，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热重启、升级

组件类型的集合都用 EasyEcs 的 `ComponentSet`（`reference/componentset.mdx`）：注册时建好一次，存在 `Registration` 里，框架按 `Type` 加入它在注册时才知道的类型（`ComponentSet.With(Type)`）。

### 要保存的是实体，不是组件类型

**保存按实体的种类决定**：从要保存的 prefab 建出的实体是要保存的实体，它带的组件除了 `[NotSaved]` 的都进记录；别的实体不进记录。根要保存，就在 `K.RootPrefab` 上写 `[Persist]`；子实体的 prefab 写 `[Prefab<K>(Persist = true)]`（第 3 节）。

- 同一个组件类型可以既在要保存的实体上，也在不保存的实体上：第 12 节的掉落带着 `NetId`、`Position`、`Loot` 保存，Avatar 带着同样的 `NetId` 和 `Position` 不保存。按组件类型决定的话，要么掉落恢复后缺了 `NetId`（客户端认不出它，它的等待实体也挂在一个缺数据的实体上），要么 `NetId` 把所有 Avatar 拖进记录。
- 注册时，要保存的 prefab 的副本带上框架的清理组件 `Persisted(EntityId Root, int Prefab)`：`Prefab` 是这个 prefab 的 id（它全名的 FNV-1a，同 `TypeId`，写在 `wire.manifest` 的 `prefab` 行），`root.Spawn` 填上 `Root`。根的副本也带它（`Root` 是自己）。所以"这个实体要不要保存、从哪个 prefab 来"是数据：系统按 `[With<Persisted>]` 过滤，加载时按 `Prefab` 补上新版本加的组件（下面的恢复）。
- **要保存的实体只能建在根或另一个要保存的实体下面**：记录是一棵完整的树。`Spawn` 时父实体不是，就抛出（分析器看得出时报错）；根不保存的 Actor 类型不能有要保存的 prefab（分析器报错）。
- `[Durable]` 的等待实体（第 8 节）由框架标成要保存的实体，所以 `[Durable]` 的请求只能从根或要保存的实体发问。根上的 `ReplyLog`、`NextDue` 和签发令牌的计数器（第 7 节）是框架的持久化组件。

### 记录

一份记录就是一次 `SaveEntities`：根放在第一个，然后是带 `Persisted` 的子孙。收集用 `world.Descendants(root)`（深度优先，池建好之后不分配），跳过不带 `Persisted` 的子树。

**保存用的组件集合 `persist`** 在注册时建一次：要保存的 prefab（包括根的）上的组件类型，加上 `root.Spawn`、`root.GetOrSpawn` 给要保存的 prefab 传进去的组件类型（生成器在调用处看得到），去掉 `[NotSaved]` 的；再加上框架的 `ChildOf`、`In<K>`、`Persisted`、`ReplyLog`、`NextDue`、`WakeAt`、签发令牌的计数器和等待实体的组件。之后才加到要保存的实体上的组件，类型在这个集合里才保存：要存，就在 prefab 上带一个默认值（第 12 节掉落的 `Cell`）。框架只在内存里有意义的组件（`Due`、`Presence`、`Joined`、`Detached`、`Leaving`、`Subscribed`、`GroupRate` 等，[architecture.md 第 2 节](architecture.md#runtime-data)）不在里面。

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
- 子孙没有 prefab 默认值，需要的组件都要在记录里，包括 `In<K>`（它是 tag，不占列）。所以 `persist` 集合带上 `In<K>`。
- **恢复用 `markWritten: false`，迁移到达不用**，只在这里写一次：
  - 恢复：读进来的值不算变更，被它覆盖的 prefab 默认值也不算（EasyEcs 4.5），所以激活不会白写一次记录，`ChangeSystem` 不用重置任何基准；
  - 迁移到达用默认的 `LoadEntities`（有占位时 `LoadEntitiesInto`），`markWritten` 为 true：追踪变更的组件都算写入。所以 `[Changed<T>]` 的系统都把迁来的实体当作刚写过：`SyncSubscriptions` 按 `Subscribed` 重新订阅网关（第 9 节），`CellAssign` 重算格子，到达后的第一个检查点在新的 epoch 下写一次记录。
- **记录比代码旧时，按 prefab 补上**：加载之后，运行时按每个实体的 `Persisted.Prefab` 找到注册的 prefab 副本，给实体补上副本里有、记录里没有的组件（新版本给 prefab 加的组件，值是副本的默认值），并给根加上 `Unsaved`：这些默认值是新数据，下一次检查点存下来。没有记录（新键）时也给根加上 `Unsaved`：占位上的默认值是建占位时写的，那时没有系统看得到它，`[Changed<T>]` 不会替它补上。
- 记录里有、这个版本不认识的 `TypeId`（例如回滚到旧版本之后）跳过并计数，加载照常完成；下一次写记录就不再有它。所以回滚会丢掉新版本的字段和类型，滚动升级的检查拦着这种发布（下面的滚动升级）。

### 只写改过的

- **追踪的是 `persist` 里的用户类型**：EasyEcs 按组件类型追踪写入（每行一个 8 字节的变更戳列，写时一次存储），不按实体。`Registration` 在注册时给 `persist` 里的用户组件类型打开追踪：EasyEcs 的需求 #27（运行时按类型打开；已提出，还没转交给 EasyEcs，第 14 节第 8 条）。在它之前，生成器给这些类型补上 `ITrackChanges` 的声明（同 `Key`），所以它们要写成 `partial`，分析器给一键修复；不在 `persist` 里的类型不用。
  - 自己写了 `ITrackChanges` 的类型（第 12 节的 `Position`、`Cell`）不用 `partial`。
  - `persist` 里的类型没有 `ITrackChanges`，在这个编译里又补不上（不是 `partial`，或者来自别的程序集）：分析器报错，修复是在声明它的地方写上 `ITrackChanges`。
  - **读走 `in`**：EasyEcs 的生成器看不到框架打开的追踪，所以对这些类型，它生成的 `entity.X` 属性按可写的 `ref` 给出，经它读也算一次写入：多写一次记录，不会漏写。处理方法和系统里用 `in` 参数或 `entity.Read<T>()` 读（分析器给提示；第 12 节的 `CellAssign` 用 `map.Read<Grid>()`）。
- **结构变更也是变更**：加上一个被追踪的组件算一次写入，去掉它记下一次移除（EasyEcs `reference/itrackchanges.mdx`、`guide/change-tracking.mdx`），所以下面的 `ChangedAny`/`RemovedAny` 看得到它们。tag 和按 #27 打开追踪的类型也这样，由 W1 核对；核对之前，`persist` 里的 tag 只能随 prefab 加上，对它 `Set`/`Remove` 是分析器错误（第 12 节掉落的 `Visible` 就是随 prefab 来的）。核对不过，就一直这样，框架自己的操作另外给根标 `Unsaved`。
- **`root.Spawn`、`root.GetOrSpawn` 建出要保存的实体时，当场给根标 `Unsaved`**，不靠变更戳。
- `ChangeSystem` 用注册时建好的 `QueryFilter.ChangedAny(persist).With<Persisted>()` 和对应的 `RemovedAny`（EasyEcs 4.7 计划）：要保存的实体上任何一个组件被写或者被删，这一行访问一次，按 `Persisted.Root` 给根标 `Unsaved`。一行改了几个组件也只访问一次；不保存的实体上同类型的写只多付变更戳，`ChangeSystem` 不访问它们。不分配。
- **占位不写**：根带着 `Loading`、`Saving` 或 `ActivationFailed` 时，`ChangeSystem` 不给它标 `Unsaved`，`CheckpointSystem` 也不写它（`[Without<Loading>, Without<Saving>, Without<ActivationFailed>]`）。否则 `Reload` 把根改回 prefab 默认值的那些写，会被当成改动写进记录，盖掉最后一份好记录（第 5 节）。
- `CheckpointSystem` 按配置的检查点间隔，把带 `Unsaved` 的根 `SaveEntities` 写一次（`[ForEach(Slices = N)]`：每次一片，写入摊在整个间隔里，不一齐涌向存储），按 epoch 比较并交换；写成后去掉 `Unsaved`。有输出在为 `[Durable]` 排队的根（第 8 节）不等间隔，当场写，写成后按顺序放出它们。根上的 `NextDue` 同时写进记录的 `next_due` 列（下面的唤醒）。
- **`Unsaved` 是根上的 tag，这是有意的**，虽然 EasyEcs 的 `ecs-principles.mdx` 第 5 条说变更检测用变更戳和 `[Changed<T>]`，不用加了又删的标记：
  - 要回答的问题是"这个 Actor 的记录要不要写"，答案属于根：子孙的变更（写、删、销毁）都要汇总到根上，记录也是按根整份写的。`[Changed<T>]` 只能回答"这一行这一趟写过没有"，回答不了"这棵树上次写成之后改过没有"；
  - 它要活过多个 tick：写记录失败时 tag 一直留着，直到某一次写成功，所以不能每 tick 从变更戳重新推出来；
  - `CheckpointSystem` 用 `[With<Unsaved>]` 只看这些根，没改过的 Actor 不花任何开销。
- 要保存的子孙被销毁（例如掉落被捡走）时，EasyEcs 的移除检测不报告销毁的实体，所以靠清理组件：框架组 `Cleanup` 里的清理系统（`[With<Destroyed>, With<Persisted>]`，不带 `In<K>`，第 6 节）按 `Root` 给根标 `Unsaved`，然后移除自己。所以删除也会写进记录。框架自己销毁整棵子树时先移除 `Persisted`，不触发它（第 6 节的表）。
- 加载的实体不算写入，`Persisted` 在 `persist` 集合里，随记录一起恢复，`Root` 由加载改写成新的 id。

### 唤醒：`WakeAt`、`NextDue`、`DueScan`

没激活的 Actor 也要在某个 UTC 时刻醒来：宕机的 `[Durable]` 发问方要再问，`DaServer.Reminders` 的提醒要触发（第 15 节）。这是运行时的事，扩展能力只写数据（ADR-045）：
- 要叫醒 Actor 的子孙带持久化的 `WakeAt(DateTime Utc)`：`[Durable]` 的等待实体是请求发出的时刻（第 8 节），提醒是它的 `UtcAt`。
- 运行时的 `WakeSystem` 按 `[ChangedAny<WakeAt>]`、`[RemovedAny<WakeAt>]` 和 `Persisted` 的清理找出受影响的根，只对这些根沿 `world.Descendants(root)` 重算最小的 `WakeAt`，写进根上持久化的 `NextDue`。只有运行时算它，扩展能力不写。
- 检查点把 `NextDue` 写进记录的 `next_due` 列（没有就是空）。每个节点的 `DueScan` 每隔 `DaServer:DueScanInterval` 向存储查自己是主人的槽里 `next_due` 已到的键（`IActorStore.DueKeys(slots, until)`，按这一列的索引），给还没激活的发控制邮件 `Wake`，Actor 因此激活。注册里有 `[Durable]` 请求或者 `DaServer.Reminders` 时才查。
- **由 `Wake` 激活的 Actor 总是重写 `next_due`**：激活给根标 `Unsaved`，下一个检查点写下现在的 `NextDue`。否则一个过时的 `next_due` 会让 `DueScan` 每一轮都叫醒它。

### 序列化规则（Nino）

| 数据 | 要求 |
|---|---|
| 进记录的组件（`persist` 里的用户组件类型）、`[Durable]` 请求的 `Ask` 状态 | `[NinoType]`：改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式；或者非托管、布局冻结：按原始字节写，布局哈希记在 `wire.manifest`，改了布局构建报错（DAS018），要改先改成 `[NinoType]`。布局冻结的是类型，所以它用到的每个地方都一样冻结（第 12 节 Avatar 上的 `Position` 也是）。托管的必须是 `[NinoType]`（分析器检查） |
| 其他非托管的组件、本地事件、`Ask` 的状态 | 迁移和热重启时按原始字节搬运，不用标：`wire.manifest` 里每个这样的类型都有布局哈希，两边对不上就不搬字节（下面的迁移、热重启的路径） |
| 托管的组件、事件、状态 | 要有 Nino 格式化器（`[NinoType]`，分析器检查） |

这是唯一的一条序列化规则，别处只引用它。记录里的类型按 `TypeId` 存。

### 迁移

- 保存整棵子树，组件集合是注册时建好的 `migrate = ComponentSet.Create().With(typeof(每个不带 [NotSaved] 的组件))`：默认都带，标了 `[NotSaved]` 的除外。
- 等待中的请求、Presence 实体（连同 `Subscribed`）、待执行的延后事件随 Actor 一起迁移。
  - `Due` 在字节里是剩余时间，到达后按新 world 的单调时钟重新起算，再按它的 tick 边界归入 tick 窗口；已经过期的在第一个 tick 执行（第 9 节）。`Due.Seq` 到达时用新 world 的序号重新编号，保持它们之间原来的先后，然后重建到期堆。
- **到达时占一个新的 epoch**：从字节里带来的 epoch `e` 比较并加一（同一条 `UPDATE … RETURNING`，不读记录），主人改成本节点；条件是 `epoch = e OR (epoch = e + 1 AND owner = (本节点名, 代数))`，所以这条语句重试时、或者到达后本节点热重启再来一次时，照样成功。占到之前，到达的子树像占位一样不运行，邮件排队；比较失败（别人已经占了）就隔离这份到达。所以迁移之后，源节点的任何迟到的写都会失败。
- **迁移头里带着每个组件的布局哈希**（来自 `wire.manifest`）。目标节点的布局有一个对不上（滚动升级时新旧版本混跑），就不搬字节：源节点改为停用、写记录，目标节点从版本兼容的记录加载。这时只有记录里的留下；Presence 丢失，由 SDK 重新加入。
- 源节点销毁子树前先移除 `Presence` 和 `Persisted`：路由不释放，根也不标 `Unsaved`（第 6 节的表）。
- **墓碑**：源节点在迁移后保留一段时间的 `(键 → 新节点)`，这个键的所有邮件都转过去（控制消息、回信和普通消息都是）；墓碑还在时本节点不激活这个键。墓碑指向的节点已经离开视图时，按当前的放置转发。
- 停用写记录时不保存待执行的延后事件：需要跨停用保留的状态，放在要保存的实体上（`DaServer.Reminders` 的提醒就是）。
- 到达后的第一条邮件是 `Activated(Activation.Migrated)`。
- 跨 world 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。
- 重新平衡引起的迁移限速：每个节点同时迁出的 Actor 有上限，节点增减时不会一下子搬空。

### Native AOT

节点可以用 `PublishAot=true` 发布成原生程序（ADR-083），也可以照常 JIT 运行。两者行为相同：都不在运行时加载程序集，改代码都走热重启或滚动升级（下面）。

- **生成的注册是封闭类型，不用反射**：`Registration.Create()`（C# 14 的静态扩展成员，第 12 节）在用户的程序集里写出每个封闭的类型（`.Actor<Map>()`、分发表、job、`world.AddSystem<…>()`），框架按 Actor 类型的系统也由生成的代码写出封闭的实例。没有反射、`MakeGenericType`、`Activator`、程序集扫描，也没有模块初始化器。
- 框架的泛型系统不写 `[ForEach]` 方法，在 `OnUpdate` 里用 EasyEcs 的泛型入口跑 job，所以 EasyEcs 不需要在运行时用 `MakeGenericType` 封闭它们的代码（EasyEcs D20：只由别的程序集封闭的泛型系统才走这条路，AOT 下不行）。用户的泛型 `[ForEach]` 系统只在别的程序集里封闭时，DaServer 的分析器报错（第 13 节）：EasyEcs 对这条路不报警告，错了只会在运行时失败。
- 序列化都是 Nino 生成的格式化器；HTTP 平面和它的 JSON 规则见 [architecture.md 第 8 节](architecture.md#8-native-aot)（ADR-083）。
- 发布要求 0 警告，不接受压掉任何警告。EasyEcs 4.6 带追踪组件和禁用组件的程序发布时没有警告（EasyEcs 的 CI 以警告为错误发布并运行原生程序，`faq.mdx` 的 "Native AOT"）；W8 要验证的只剩第三方包（[architecture.md 第 8 节](architecture.md#8-native-aot)）。

### `wire.manifest`

- 每个工程写出自己的一段。
- Server 的一段：Actor 类型、承载它的服务器类型、帧率、身份、连上就加入的 Actor 类型、每条消息的接收方、发送方、回复类型、是否 `durable`、事件、状态类型、prefab（它的 id 和要不要保存）、进记录的组件类型（`persist` 的全部用户类型，写法 `nino`、`raw` 或 `tag`）、每个组件的布局哈希、推送；客户端消息的路由（带 `[To]` 的写明参数）、事件和 `Replied` 的处理、`Activated` 的处理、对 tag（`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）的消费。
- 热重启按它比对新旧版本，选走哪条路（下面）。
- AppHost 只读 `actor` 和 `hosted-by`。

### 滚动升级

- 旧版本节点收到不认识的消息，返回 `NotHandled`。调用方在重新解析放置之后可以重试一次：放置可能已经换到新版本的节点。
- 旧版本的网关收到不认识 `TypeId` 的客户端消息，回 `NotHandled`；SDK 在握手里带上自己的 `wire.manifest` 哈希，收到 `NotHandled` 时向应用报告版本不一致，而不是一个普通的失败。
- **回滚要安全**：`{cluster}-upgrade-check`（[deployment.md 第 3 节](deployment.md#3-jobs)）还检查回滚：新版本会往记录里写旧版本保留不了的字段，或者新的记录类型（旧版本读进来时跳过，再写回就丢掉，上面的恢复），就拒绝，除非强制。
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
  | 布局变了，但只变在新版本读得了记录的组件上（`VersionTolerant`、`Keyed` 的 `[NinoType]`，或者不进记录的组件），或者去掉了上一行的处理方法 | **经记录**：所有 Actor 停用、写记录，重启，按需从记录激活 | 更长；不持久化的状态按停用的规则丢掉，Presence 由 SDK 重新加入 |
  | 承载关系（`[Hosts]`）或客户端路由变了 | 拒绝；走**滚动升级**（上面），路由变了要连网关一起升级 | — |

  - 去掉的处理方法可能正有数据在等：待执行的事件、等待实体、正在离开的 Presence，所以原地不接受它。
  - 没配 `DaServer:SnapshotPath` 的节点没有原地这条路：命令报告它，走经记录。
  - **拒绝武装**：有成员在 `NodeLeaving` 或 `NodeJoining`（放置正在变）；全集群唯一的热重启许可已经发给了别的节点（第 2 节）；工作集加上快照的估计大小超过 cgroup 内存上限的约 80%（快照目录是内存盘时，它也算在这个上限里）。
  - 一个节点组逐个节点热重启，协调者排在最后；期间新旧版本混跑，规则同滚动升级。
- **步骤**：
  1. **SIGTERM 才开始**：武装状态下收到 SIGTERM（Windows 服务是停止命令），节点向协调者宣布成员状态 `NodeRestarting`（带预计停顿和武装的随机数）。协调者确认之后，期限才开始算，节点才冻结。期限由协调者按自己的单调时钟计时，其他节点只照它的决定做。
  2. **冻结前的最后一个 tick**：框架给每个 Actor 的根加上 `Restarting(TimeSpan ExpectedPause)`，用户的系统按需要反应（下面）；同一个 tick 的 `Settling` 去掉它，所以快照里没有 `Restarting`。
  3. 每个 world 在两个 tick 之间冻结：不再执行任何项，等在途的存储操作（写记录、占 epoch）有结果：写成的去掉了 `Unsaved`，没写成的还带着，随快照过去，新进程的下一个检查点接着写。这个等待有上限，过了就放弃（下面）。**冻结只停 world 的线程**：IO 线程照常收帧，放进调度队列，冻结期间发来的消息不被拒绝，也不用让客户端或别的节点停发。
  4. 进程停止读链路，这一刻就是切换点。快照先写进 `DaServer:SnapshotPath` 目录里的临时文件，`fsync`，再改成正式的文件名（普通的文件写入：几 MB 用不着内存映射），然后进程退出。快照里有：
     - 头：节点名、节点代数、武装的随机数、旧版本和武装时新版本 `wire.manifest` 的哈希、每个组件的布局哈希、world 的个数、冻结时的 `Environment.TickCount64` 和内核的 boot id、校验和；
     - 每个 world：`world.Save(output, exclude)` 的字节（实体连同 id 和版本、组件、`DynamicBuffer`（包括占位上等着的 `Mail`）、单例；不含系统、索引、写入戳，EasyEcs `guide/saves.mdx`；`exclude` 是用户的单例，下面）、调度队列里还没进 world 的邮件、`Time.Elapsed`；即时链在两个 tick 之间是空的，`Outbox` 在冻结前的最后一个 tick 交出了所有的帧（包括没到点的组帧，第 9 节）；
     - 节点的运行时状态：调用 id 的序号、墓碑、连接戳的计数；每条链路收到的字节数，发出的偏移和还没确认的尾巴；邮件和期限已经等了多久（第 9 节）。
  5. 平台用新的二进制或镜像重启进程（下表）。新进程找属于自己节点名的快照，逐项检查：校验和对；boot id 相同（机器没重启过）；按 `TickCount64` 算出的停顿没超过期限；自己的 `wire.manifest` 哈希等于武装时的新版本哈希（换错了镜像就不加载）。不合格就删掉它，以新的代数加入，走经记录。合格就按头里的个数建 world（键按哈希落在哪个 world，个数不同就对不上；建不了那么多，同样删掉快照、走经记录），再流式加载（`world.Load(bytes, markWritten: false, keep)`，边读边装；新进程在加载之前已经建好用户的单例，`keep` 就是它们，下面），接上时钟、加上系统、重建到期堆和调用 id 索引，然后**在任何 world 运行之前**删掉这个文件：快照只用一次，加载之后再崩溃，下次启动只能经记录。
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
- **用户的单例不进快照**：配置表（`IConfigTable<T>`）和 world 自己的缓存（`IWorldSingleton<T>`）是新进程自己的数据（集群当前版本的表、重新建的缓存），类型也不用能序列化。旧进程用 `world.Save(output, exclude)` 保存，`exclude` 是这些单例（`ComponentSet.WithSingleton<T>()`，注册时建一次）；新进程先读入表、建好缓存，再用 `world.Load(input, markWritten: false, keep)` 加载，`keep` 是同一个集合：加载跳过快照里的值，保留 world 现在的（EasyEcs 4.7 计划）。
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
  | systemd | `RuntimeDirectory=` 加 `RuntimeDirectoryPreserve=restart`（`/run` 下的 tmpfs，重启时保留，[systemd.exec(5)](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html#RuntimeDirectory=)） | `systemctl restart` |
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
- **配置表**（调参的表）：实现 `IConfigTable<T>` 的用户单例（第 5 节），`T.Name` 是表名，`T.TryLoad` 解码并校验；处理方法和系统取 `in`（第 12 节的 `SkillTable`）。
  - **整个集群一个当前版本**：存储里每张表一行 `(名字, 版本, 哈希, 字节)`，协调者把 `(名字, 版本, 哈希)` 放进视图。每个启动的、热重启的节点都读这个版本（按哈希核对字节），所以新节点和老节点用的是同一份表。存储里还没有这张表时，第一个组成集群的节点用自己的 `DaServer:Tables:{名字}` 写入第一版。没有记录存储的集群（只有 `Connection` 和 `[Stateless]`）把字节放在协调者那里，集群整个冷启动后回到配置里的版本。
  - **换版本**：管理端点 `POST /admin/tables/{名字}`（也是 Aspire 命令）把新字节交给协调者：先 `TryLoad` 校验，失败就不换，命令报告原因；通过就写进存储、版本加一，视图传开后每个节点读入一次，每个 world 在两个 tick 之间换上。
  - **回滚**就是把旧版本的字节再 `POST` 一次：它成为新的一版。
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
| 网关滚动升级或缩容 | 不断线：先扩后缩，分批 `Retire`，SDK 先连上新网关、接管各个 Presence，`HandedOff` 栅栏保持每个 Actor 的推送顺序，再关旧连接（[architecture.md 第 3 节](architecture.md#retire-and-rebalancing)） |
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
| `[Durable]` 的请求快到期限还没有结果 | 收尾的一问：接收方回答已有的结果，或者把调用 id 记成拒绝、回 `TooLate`（确定没执行，迟到的同一个请求也不会执行）；收尾的一问也没有回答，回信是 `Expired`：联系不上，结果未知，处理方法按业务对账（第 8 节） |
| `[Durable]` 的请求到达时剩下的时间不够写一次记录 | 不执行，回 `TooLate` |
| `[Durable]` 的发问方处理回信后、记录写成前宕机 | 回信处理的输出留到记录写成才发出，所以什么也没发；从记录恢复后再问，接收方从 `ReplyLog` 回答同样的结果，处理方法再执行一次 |
| `ReplyLog` 满了 | 新的 `[Durable]` 请求回 `Busy`（确定没执行），`RetryAfter` 是最早一项过期的时刻 |
| 拿着回复令牌的 Actor 在回复前停用 | 令牌在要保存的实体上，重新激活后期限内照样回复；否则调用方按期限超时 |
| 过了期限才 `Reply` | 不发出，计数 |
| 发问的实体在回信前被销毁 | 等待实体一起销毁，回信丢弃 |
| 停用时写记录失败 | 重试；这期间到达的消息留在 `Saving` 占位上，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；晚到的邮件由墓碑转发（墓碑指向的节点已经离开时按当前放置）；目标节点宕机时，在途的单向消息可能丢失 |
| 迁移两端的组件布局不同 | 不搬字节：停用、写记录，目标从记录加载 |
| 记录里有这个版本不认识的类型（回滚之后） | 跳过并计数，加载照常；新版本 prefab 上多出的组件按 prefab 的默认值补上（第 10 节） |
| 新版本去掉了还在用的处理方法 | 热重启不走原地，改走经记录（第 10 节）；分发时仍然找不到就是 `NotHandled` |
| 武装之后没有等来 SIGTERM | 武装静默作废（第 10 节） |
| 热重启冻结时存储挂住、快照写失败 | 放弃：撤回 `NodeRestarting`，根带 `Resumed(Aborted: true)`，节点优雅停止，新进程经记录（第 10 节） |
| 热重启后快照不合格（校验和、节点名、boot id、过期、镜像不是武装的那个、建不了头里那么多 world） | 新进程删掉它，以新的代数加入，Actor 从记录激活 |
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
| `[On<T>]` 抛出异常 | 不回滚，见第 5 节：请求以 `Faulted` 失败，即时链里还没执行的事件作废；`Reload`（根要保存的 Actor 类型的默认）把根恢复成 prefab 的副本，保留排队的邮件，从最后的记录重新激活，改了一半的项不会进检查点，`[Durable]` 的调用 id 记成 `Faulted`、写进记录后才回答；连续 `Reload` 进入激活失败的退避；`Continue`（其余的默认）照现状继续；`Connection` 直接断开 |
| 一项的即时链超过 1,024 个 | 按 `OnError` 处理 |
| 系统抛出异常 | `SystemErrorPolicy.Disable`：这个系统停用，异常留在 `system.Error`，健康检查报告；下一次热重启或升级换上新的系统 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 过滤不满足就不调用；方法体里重新校验目标后作废 |

### 每条路径保证什么

| 路径 | 会丢吗 | 会执行两次吗 | 去重 | 谁重试 |
|---|---|---|---|---|
| `Send`（Actor 之间的单向消息） | 会：最多一次。节点宕机、链路窗口满、迁移途中目标宕机时丢失 | 不会 | — | 没人 |
| `Ask`（普通请求） | 请求会丢，但一定有结果：回复、拒绝或者 `TimedOut` | 会：结果未知后用同一个调用 id 重问时 | 接收方的处理方法按 `reply.Call` 自己记 | 发问方的代码（`Transient` 时延后 `RetryAfter`）；框架只重试转发一跳上的（下面） |
| `[Durable]` 的 `Ask` | 不会：等待实体在记录里，宕机后被唤醒再问；收尾的一问也联系不上才是 `Expired` | 不会：一个调用 id 最多执行一次，收尾的一问把没执行的记成拒绝 | 框架：接收方的 `ReplyLog` | 框架：`Transient` 时和重新激活后，按 `RetryAfter` 和抖动，计入重试预算 |
| `Schedule(ev, after:)` | 会：停用、节点宕机时丢失（不在记录里）；迁移、热重启不丢 | 不会 | — | 没人；要扛住宕机的定时用 `DaServer.Reminders`（第 15 节） |
| 组件的状态 | 会：宕机时丢最多一个检查点间隔的改动；`[Durable]` 落定的不丢 | — | — | — |
| `Push` | 会：断线、`Detached` 期间、读得太慢被断开时丢失 | 不会 | — | 没人；重新加入后快照系统补全（`Joined.MissedPushes`） |
| `Broadcast`、`ClientGroup` | 同 `Push`；只发给带 `Joined` 的 Presence | 不会 | — | 同 `Push` |
| 客户端请求（`IFromClient<R>`） | 一定有结果：回复、拒绝、`TimedOut` 或者 `ConnectionLost` | 会：应用用同一个调用 id 重试时 | 处理方法按 `reply.Call`，即 `(ClientId, 客户端调用 id)` | 应用：`client.Ask(msg, call: id)` |
| 客户端单向消息（`IFromClient`） | 会：最多一次。断开时没送达的丢失；断线后到这个槽重新加入之前，不带 `[To]` 的被网关丢弃并计数（`NotJoined`，第 7 节） | 不会：SDK 不重发 | — | 没人 |
| 加入（`[JoinOnConnect]`、SDK 重发的加入消息） | 不会：失败就重试，直到成功或者 `Unavailable` | 无害：`Join()` 按连接戳幂等，`GetOrSpawn` 找到已有的实体 | 连接戳 | 框架（`[JoinOnConnect]`）和 SDK（各槽的加入消息），按 `RetryAfter` 和抖动 |
| 转发的一跳 | 同它转发的消息 | 不会：只重试确定没执行的拒绝（`NoOwner`、`NotHandled`） | — | 框架，计入重试预算 |
| `ICluster` 的 `Ask`、`Send` | 同 `Ask`、`Send`：调用方在 Actor 外面，不持久化 | 同 `Ask`；带 `CallId.From(外部键)` 问 `[Durable]` 的请求时不会 | `CallId.From(外部键)` | 外部调用方 |
| 存着的 `ReplyTo<T>` 跨过停用 | 令牌在要保存的实体上不丢，重新激活后期限内照样回复；在别的组件里就丢了，调用方超时 | 不会：同一个令牌只有第一次回复有效 | — | 普通请求是发问方，`[Durable]` 是框架 |

先后的保证在第 1 节；期限怎么传、停顿不延长预算在第 9 节。

### 重试、预算与过载

- **`Status` 的两个分类**是 `Status` 上的扩展属性 `s.NotExecuted` 和 `s.Transient`：

  | `Status` | `NotExecuted`：确定没执行，再发一次不会执行两次 | `Transient`：过一会儿可能成功 |
  |---|---|---|
  | `Ok` | | |
  | `Busy`（带 `RetryAfter`）、`NoOwner` | 是 | 是 |
  | `NotHandled`、`NoActor`、`Filtered`、`NotJoined`、`Skipped` | 是 | |
  | `TimedOut`、`ConnectionLost` | 结果未知 | 是：只能用同一个调用 id 重问 |
  | `TooLate` | 是：`[Durable]` 的请求来不及落定，或者收尾的一问时还没执行（第 8 节） | |
  | `Faulted` | 处理方法抛出了；终止的结果，用同一个调用 id 再问得到同样的回答（第 5 节） | |
  | `Expired` | 结果未知：`[Durable]` 的收尾一问也联系不上（第 8 节） | |
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
| `DaServer:AskTimeout` | 5 秒 | 不写 `deadline:` 时每一次问的超时，`[Durable]` 的也是（第 8 节） |
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
public readonly partial record struct Account(long Id) : IClaims;                      // decoded from the token
[JoinOnConnect<Account>]                                                               // joined at connect: Account(42) -> Player(42)
public readonly partial record struct Player(long Id) : IActor<Player>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(0, 40, 0);
}
[TickRate(30)]                                                                         // 30 ticks a second; its systems see only map entities
public readonly partial record struct Map(long Id) : IActor<Map>
{
    [Persist]                                                                          // the map's record: Grid, NetIds and the drops
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetBuffer<Shout>();
}

[Hosts<Player>]     public readonly struct GameServer    : IServer;
[Hosts<Map>]        public readonly struct MapServer     : IServer;
[Hosts<Connection>] public readonly struct GatewayServer : IServer;

[NinoType, From<Map>, Durable] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // lands once: section 8
public record struct Strike(int Skill, EntityId Target) : IEvent;                      // a cast's resolution, after the windup
public record struct Fell(int Loot, Vector2 At) : IEvent;                               // an npc died here: scheduled on the map's root

// ── Game.Server: tuning data, swapped without a restart (section 10) ─────────
[NinoType] public record struct SkillRow(TimeSpan Windup, float Reach, int Damage);
public readonly record struct SkillTable(SkillRow[] Rows) : IConfigTable<SkillTable>   // indexed by skill id
{
    public static string Name => "skills";                                             // DaServer:Tables:skills, POST /admin/tables/skills
    public static bool TryLoad(ReadOnlySpan<byte> bytes, out SkillTable table, out string? error) =>
        SkillRows.TryDecode(bytes, out table, out error);                              // the game's own decode and checks (cold path)
    public TimeSpan Windup(int skill) => Rows[skill].Windup;
    public bool InRange(int skill, Vector2 from, Vector2 to) =>
        Vector2.DistanceSquared(from, to) <= Rows[skill].Reach * Rows[skill].Reach;
    public int Damage(int skill) => Rows[skill].Damage;
}

// ── Game.Server: data ─────────────────────────────────────────────────────────
// partial: a persist-set type without ITrackChanges; the generator declares it (section 10)
[NinoType] public partial record struct Grid(int Size, int CellSize) : IComponent;      // on a map's root
[NinoType] public partial record struct Inventory(int Count, int Capacity, int Last) : IComponent;   // on a player's root
public partial record struct NetIds(int Next) : IComponent;                             // on a map's root: the last number given out,
                                                                                        // saved with it, so no restored drop's number is reused
public partial record struct NetId(Map Map, int Value) : IChildKey<NetId>;              // what clients call an entity; Key => this is generated
public partial record struct Avatar(Map Map, Player Player) : IChildKey<Avatar>;
public partial record struct Cell(Map Map, int X, int Y) : IComponent, IIndexed<Cell>, ITrackChanges;   // AOI cell: many entities
                                                                                        // per cell (a non-unique index); Map keeps maps apart
public partial record struct Visible : IComponent;                                      // a tag; on drops, so in the persist set
public record struct Position(Vector2 Value) : IComponent, ITrackChanges;               // tracked: CellAssign reacts to writes
public record struct Velocity(Vector2 Value) : IComponent, ITrackChanges;               // tracked: Diff reacts to writes
public record struct NetSync(Velocity Sent) : IComponent;                               // the velocity last told to observers
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Dead : IComponent;
public record struct Stunned : IComponent;
[NinoType] public partial record struct Loot(int Item) : IComponent;                    // what a drop on the map holds
[NinoType] public record struct Looting(ReplyTo<bool> Reply) : IComponent;              // the durable ask's state: in the map's record
[NotSaved] public record struct Shout(Cell Cell, NearbyEvent Event);                    // news for one cell, on the map; empty between ticks
                                                                                        // (a buffer element: EasyEcs asks for no interface)

internal static class Prefabs                                                           // the generator reads them: at least internal
{
    static readonly Prefab BodyPrefab = new Prefab().SetPosition(default).SetVelocity(default).SetNetSync(default)
        .SetHealth(100).SetVisible();                                      // what everything walking on a map has; only a base
    [Prefab<Map>] internal static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);   // not saved: rejoins after a restart
    [Prefab<Map>] internal static readonly Prefab NpcPrefab = new Prefab(BodyPrefab).SetSpeed(3).SetDestination(default).SetSteered();
    [Prefab<Map>(Persist = true)]                                          // saved: NetId and Loot (given at spawn), Position, Cell,
    internal static readonly Prefab DropPrefab =                           // Visible and its waiting ask; Cell is here so it is saved
        new Prefab().SetPosition(default).SetCell(default).SetVisible();
}
```

```csharp
// ── Game.Server: a map's membership ───────────────────────────────────────────
[Handles<Map>]
public static partial class Maps
{
    [On<EnterMap>]                                                         // [To] Map: the subject is the map's root
    static bool Enter(in EnterMap m, in Account who, in Map map, Entity root)
    {
        var avatar = root.GetOrSpawn(Prefabs.AvatarPrefab, new Avatar(map, new Player(who.Id)),   // a re-sent join finds it
            static (Entity root, in Avatar key) => new NetId(key.Map, ++root.NetIds.Next));      // run only when it spawns
        return avatar.Join() == Status.Ok;                                 // a newer connection takes over; the previous map leaves it
    }
}

// ── Game.Server: steps a client took ──────────────────────────────────────────
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>, Without<Dead>]                                            // subject: this client's avatar; a dead one is not called
    static void Move(in MoveTo m, Entity me, Mut<Position> p, Mut<Velocity> v, in Speed speed)   // Mut: a refused step stamps nothing
    {
        if (!Steps.IsValid(p.Value.Value, m.At, speed.Value)) { me.Push(new SnapBack(p.Value.Value)); return; }
        p.SetIfChanged(new Position(m.At));                                // applied now: later items see it
        v.SetIfChanged(new Velocity(m.Velocity));
    }
}

// ── Game.Server: combat ───────────────────────────────────────────────────────
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>, Without<Dead>, Without<Stunned>]                       // subject: the caster's avatar
    static void Cast(in CastSkill m, in Map map, in SkillTable skills, Entity me, World world)   // map: this actor's key
    {
        if (me.IsPending<Strike>()) return;                               // one cast at a time
        if (!world.NetIdIndex.TryFind(new NetId(map, m.Target), out var target)) return;   // the number -> the entity on this map
        me.Schedule(new Strike(m.Skill, target.Id), after: skills.Windup(m.Skill));   // Zero resolves right after this handler
    }

    [On<Strike>, Without<Dead>, Without<Stunned>]                          // died or got stunned during the windup: not called
    static void Resolve(in Strike s, in Position at, in SkillTable skills, World world)
    {
        if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
        if (!skills.InRange(s.Skill, at.Value, target.Position.Value)) return;   // out of range: dodged
        ref var hp = ref target.Health;
        hp.Value -= skills.Damage(s.Skill);
        if (hp.Value <= 0) target.SetDead();                               // structural change last: hp is not used after it
    }
}

// ── Game.Server: drops and picking them up (section 8) ────────────────────────
[Handles<Map>]
public static partial class Pickups
{
    [On<Fell>]                                                             // subject: the map's root; a drop where the npc fell
    static void Drop(in Fell f, in Map map, Entity root, ref NetIds ids) =>
        root.Spawn(Prefabs.DropPrefab, new NetId(map, ++ids.Next), new Loot(f.Loot), new Position(f.At));

    [On<PickUp>]                                                           // subject: the client's avatar; replies later
    static void Take(in PickUp m, in Account who, in Map map, in ReplyTo<bool> reply, Entity me, World world)
    {
        if (!world.NetIdIndex.TryFind(new NetId(map, m.Drop), out var drop) || !drop.TryGetLoot(out var loot)
            || drop.IsPending<GrantItem>())                                // already promised to someone
        {
            me.Reply(in reply, false);
            return;
        }
        drop.Ask(new Player(who.Id), new GrantItem(loot.Item), new Looting(reply),   // the drop asks: it is saved, the answer outlives
            settleBy: TimeSpan.FromMinutes(5));                            // the avatar; [Durable]: re-asked, settled by then
    }

    [On<Replied<GrantItem, Looting>>]                                      // subject: the drop; only final outcomes arrive, and
    static void Granted(in Replied<GrantItem, Looting> a, Entity drop)     // what this sends leaves once the map's record has it
    {
        var taken = a.TryGetValue(out var ok) && ok;
        drop.Reply(in a.State.Reply, taken);                               // past the client's own deadline nothing is sent
        if (taken || a.Status == Status.Expired) drop.Destroy();           // Expired: unreachable, it may have run; never grant twice;
                                                                           // this answer's own waiting entity may go with the drop
    }                                                                      // "no" or NotExecuted (TooLate, ...): the drop stays
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
        var cell = Aoi.CellOf(map.Map, p.Value, map.Read<Grid>().CellSize);   // Read: not a write of the tracked Grid
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
public class SendFullState : EcsSystem
{
    [ForEach, Changed<Joined>]                                             // joined, reconnected or taken over: confirmed this tick
    static void Send(Entity avatar, in Joined joined, in Cell cell, World world)
    {
        if (!joined.MissedPushes) return;                                  // handed over by a gateway upgrade: nothing missed
        avatar.Push(Aoi.Snapshot(world, in cell));                         // a pooled Nearby of the 3×3 cells, from world.CellIndex.Find
    }
}

[UpdateInGroup<Map>, UpdateAfter<SendFullState>]
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
    static void Publish(Entity map, Mut<DynamicBuffer<Shout>> shouts)      // Mut: the clear is not news, so it does not
    {                                                                      // bring this map back next tick
        Aoi.Publish(map, shouts.Value.AsReadOnlySpan());                   // one Nearby per cell with news: map.Push(map.Group(Aoi.Key(cell)), nearby)
        shouts.Value.Clear();
    }
}
```

NPC 死亡时（例子里没写出），地图对根 `Schedule(new Fell(…))`，`Pickups.Drop` 在那里建掉落。被打断的前摇：眩晕的处理方法里 `me.Cancel<Strike>()`。

客户端连上时由 `[JoinOnConnect<Account>]` 加入 Player；进地图的 `EnterMap` 直接发给地图，不经过 Player。Player 只回答地图的 `GrantItem`。

**保存的是什么**：地图的根（`Grid`、`NetIds`）和掉落（`NetId`、`Position`、`Loot`、`Cell`、`Visible`，以及它还在等的 `GrantItem`）在地图的记录里；Avatar 和 NPC 不在。地图宕机后，掉落带着原来的编号恢复，`NetIds` 接着往下发编号，不会和恢复的掉落撞号；客户端重新加入，Avatar 重新建出来。Player 的根（`Inventory`）在 Player 的记录里。`Shout` 在两个 tick 之间是空的，标了 `[NotSaved]`。所以 `persist` 里的用户类型是 `Grid`、`NetIds`、`Inventory`（根）和 `NetId`、`Loot`、`Position`、`Cell`、`Visible`（掉落）；`Position`、`Cell` 自己声明了 `ITrackChanges`，其余写成 `partial`，由生成器补上（第 10 节）。`Enter` 用只在新建时调用的 lambda 取编号，重发的加入找到已有的 Avatar，不白花编号。

AOI 的状态全在组件里：客户端看到的编号是 `NetId`，格子是 `Cell`（非唯一值索引，`world.CellIndex.Find(cell)` 列出格子里的实体），上次发出的速度是 `NetSync`，确认和有没有漏过推送是框架的 `Joined(MissedPushes)`，订阅是 `Subscribed`，消息是地图上的 `DynamicBuffer<Shout>`。`Aoi` 是用户写的无状态帮助类。每个系统只做一件事：
1. `CellAssign`：位置被写过的实体，算出格子；跨格才写 `Cell`。
2. `Regroup`：`Cell` 变了的 Presence，改写它订阅的 3×3 `ClientGroup`（`root.Group(格子的键)`，键带着这张地图）。框架的 `SyncSubscriptions` 按这份数据同步网关（第 9 节），所以用户只写一处。
3. `SendFullState`：这个 tick 确认的 Avatar（`Joined` 被写过：第一次加入、断线后重连、被别的连接接管），`MissedPushes` 为真才推一份完整的状态；因网关升级换连接的一个推送也没漏，不推。
4. `Diff`：速度被写过、并且和 `NetSync` 不同的实体，写一条 `Moved` 进地图的 `Shout`。
5. `Vanish`：带 `Leaving` 的 Avatar，写一条 `Disappear`。
6. `Flush`：`Shout` 被写过的地图（`[Changed<DynamicBuffer<Shout>>]`，没消息的地图不访问），按格子合并，每个有内容的格子发一帧 `Nearby`，然后经 `Mut<T>` 清空，不打戳（W1 核对 EasyEcs 4.7 的 `Mut<DynamicBuffer<T>>` 这样做）。

所有系统都只看带 `In<Map>` 的实体（`IActor<Map>` 带着的组过滤，第 5 节）。`Steering`、`Movement` 和 `Moves.Move` 用 `Mut<T>`（第 5 节的"只在值变了时写"）：站着的 NPC 和被拒绝的一步都不打戳，`CellAssign` 和 `Diff` 就不访问它们。除了 `Steering` 都是单线程：`Movement` 每行只是一次加法和一次比较，其余的发消息、写别的实体或做结构变更。`Steering` 也只在节点的 world 比核少时才多线程（第 5 节）。

捡东西没有用户写的预留、重问和去重：`GrantItem` 是 `[Durable]`，掉落上还在等的问（`drop.IsPending<GrantItem>()`）就是"已经许给了别人"，它随地图的记录保存。地图宕机后框架唤醒它，用同一个调用 id 再问；Player 那边框架认出问过的调用，回答第一次的结果，不执行第二次。快到期限时框架发收尾的一问：Player 还没执行就记成拒绝，掉落留着；收尾的一问也联系不上（`Expired`），掉落销毁：宁可丢，不发两次。

NPC 也由用户建：`root.Spawn(Prefabs.NpcPrefab, new NetId(map, ++ids.Next))`，同样从 `NetIds` 取一个编号。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: Key => this for every partial IActor<T> / IChildKey<T> / IIndexed<T>-of-itself record struct
partial record struct Map    { Map IIndexed<Map>.Key => this; }        // explicit: a public Key would recurse in ToString()
partial record struct Player { Player IIndexed<Player>.Key => this; }
partial record struct Avatar { Avatar IIndexed<Avatar>.Key => this; }
partial record struct Cell   { Cell IIndexed<Cell>.Key => this; }      // non-unique: world.CellIndex.Find(cell)
partial record struct NetId  { NetId IIndexed<NetId>.Key => this; }
```

```csharp
// <auto-generated/> Game.Server: typed sends on Entity and the typed reply value, one class per message
public static class GrantItemSends
{
    public static CallId Ask(this Entity from, Player to, in GrantItem message, TimeSpan settleBy,
        TimeSpan deadline = default, CallId call = default) =>             // [Durable]: settleBy is required; answer: Replied<GrantItem>
        Runtime.AskDurable(from, in to, in message, settleBy, deadline, call);

    public static CallId Ask<TState>(this Entity from, Player to, in GrantItem message, in TState state, TimeSpan settleBy,
        TimeSpan deadline = default, CallId call = default) where TState : struct, IComponent =>   // answer: Replied<GrantItem, TState>
        Runtime.AskDurable(from, in to, in message, in state, settleBy, deadline, call);   // held until the asker's record has it

    extension(Replied<GrantItem> a)                                        // C# 14 extension block: the reply's type comes from the request
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);   // reads the reply bytes in the mail's frame
    }

    extension<TState>(Replied<GrantItem, TState> a) where TState : struct, IComponent
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);
    }
}
// A request without [Durable] has no settleBy: TimeSpan deadline = default (the node's AskTimeout), into Outbox at once.
// The receiver is in the signature; the sender is checked where it is called: inside [Handles<K>] or [UpdateInGroup<K>],
// K must be in [From<…>] (here Map), else the analyzer reports it; elsewhere a Debug build asserts the root's actor type.
// Schedule<T>, and IsPending<T>()/Cancel<T>() for events (T : struct, IEvent) and requests (T : struct, IRequest, the marker
// every IRequestTo<,> inherits), are the framework's own extension methods: the two IsPending/Cancel groups live in two static
// classes, so overload resolution keeps the one whose constraint T meets. Cancel<GrantItem>() is an analyzer error ([Durable]).
```

```csharp
// <auto-generated/> Game.Server: each [On<T>] method as an EasyEcs per-entity job, inside its partial class
partial class Maps
{
    internal struct Enter_Job : IForEachEntity                             // no component parameter: the zero-component form (W1)
    {
        public EnterMap Message;                                           // filled from the mail
        public Account Who;                                                // the sending client's claims, not the subject's
        public Map Map;                                                    // the item's actor key, not a component of the subject
        public bool Reply;
        public static int Writes => 0;
        public void Execute(Entity e) => Reply = Enter(in Message, in Who, in Map, e);
    }                                                                      // spawns and joins: no INoStructuralChanges

    internal static void On_Enter(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Enter_Job { Message = mail.Read<EnterMap>(), Who = mail.Claims<Account>(), Map = mail.Target<Map>() };
        if (!subject.Run<Enter_Job>(ref job)) { outcome.Skipped(); return; }   // no filter on the method: none passed
        outcome.Reply(job.Reply);                                          // Join() put the Route in Outbox ahead of this reply
    }
}

partial class Moves
{
    internal struct Move_Job : IForEachEntity<Position, Velocity, Speed>, INoStructuralChanges   // Entity only pushed to
    {
        public static readonly QueryFilter Filter = QueryFilter.Create().Without<Dead>();   // [Without<Dead>], built once
        public MoveTo Message;
        public static int Writes => 0;                                     // Mut<T> stamps on SetIfChanged, not on the call
        public void Execute(Entity e, ref Position p, ref Velocity v, ref Speed speed) =>
            Move(in Message, e, new Mut<Position>(e, ref p), new Mut<Velocity>(e, ref v), in speed);   // EasyEcs 4.7's Mut (W1)
    }

    internal static void On_Move(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Move_Job { Message = mail.Read<MoveTo>() };
        if (!subject.Run<Position, Velocity, Speed, Move_Job>(ref job, Move_Job.Filter)) outcome.Skipped();   // no copy, no write-back
    }
}
// Combat.Cast_Job has no component parameter either (SkillTable is a singleton: a job field). Cast schedules after a windup
// (a child entity), Resolve adds Dead to another entity, Pickups.Drop spawns, Take asks (a waiting child), Granted destroys:
// none gets INoStructuralChanges. Items.Grant_Job only writes Inventory and returns its reply: it gets it.
// GrantItem is [Durable]: before the job the dispatch looks the call id up in the root's ReplyLog (a repeat is answered from it,
// a settling re-ask of an unknown call id is recorded as refused and answered TooLate) and refuses with TooLate when the
// remaining budget is below the store's write latency; after it, the item's whole output (the reply among it) and every later
// output of that player wait, in order, for the record write. Granted handles a [Durable] Replied: the same for the map.
```

```csharp
// <auto-generated/> Game.Server: a C# 14 static extension member in the project's root namespace, so the call is
// Registration.Create() (GameServerRegistration.Create() where two server projects' namespaces are imported, section 5);
// no static registries, no module initializers
namespace Game.Server;

public static class GameServerRegistration
{
    extension(Registration)
    {
        public static Registration Create() => new Registration()
            .Actor<Map>()                                                  // root prefab: Map.RootPrefab, persistent; the runtime finds a Map
                                                                           // by world.Index<Map, Map>(); also adds the framework's closed
                                                                           // OrderedPhase<Map>, Depart<Map>, SyncSubscriptions<Map>
            .Prefab<Map>(Prefabs.AvatarPrefab)                             // a registered copy with In<Map> and a ChildOf column
            .Prefab<Map>(Prefabs.NpcPrefab)                                // the static prefabs stay as written
            .Prefab<Map>(Prefabs.DropPrefab, persist: true)                // the copy also carries Persisted(Root, its prefab id)
            .Actor<Player>()                                               // root prefab: Player.RootPrefab, persistent
            .Record(static set => set                                      // the persist set (section 10): the persistent prefabs' types
                .With<Grid>().With<NetIds>().With<Inventory>()             // and those spawned into them, no [NotSaved] (not Shout);
                .With<NetId>().With<Loot>().With<Position>().With<Cell>().With<Visible>())   // change tracking on for them (#27)
            .ConfigTable<SkillTable>()                                     // loaded from the cluster's current version, swapped live
            .Claims<Account>()
            .JoinOnConnect<Player, Account>(static (in Account c) => new Player(c.Id), Takeover.NewestWins)
            .Dispatch<Map>(static table => table
                .Client<EnterMap>(Maps.On_Enter)                           // [To]: subject is the root; may Join
                .Client<MoveTo>(Moves.On_Move)                             // subject: the presence
                .Client<CastSkill>(Combat.On_Cast)
                .Client<PickUp>(Pickups.On_Take)
                .Replied<GrantItem, Looting>(Pickups.On_Granted)
                .Event<Strike>(Combat.On_Resolve)
                .Event<Fell>(Pickups.On_Drop))
            .Dispatch<Player>(static table => table
                .Request<GrantItem>(Items.On_Grant))                       // [Durable]: deduplicated through the ReplyLog
            .Routes(static table => table
                .Route<EnterMap, Map>(to: static (in EnterMap m) => new Map(m.Map))   // [To] long Map -> Map(long Id)
                .Route<MoveTo, Map>()                                      // no [To]: this client's map presence
                .Route<CastSkill, Map>()
                .Route<PickUp, Map>())
            .Systems<Map>(static world =>                                  // the Map group; EasyEcs orders them by [UpdateAfter]
            {
                world.AddSystem<Steering>();
                world.AddSystem<Movement>();
                world.AddSystem<CellAssign>();
                world.AddSystem<Regroup>();
                world.AddSystem<SendFullState>();
                world.AddSystem<Diff>();
                world.AddSystem<Vanish>();
                world.AddSystem<Flush>();
            });                                                            // the node runs Ordering, Update<Map>(), Settling, Cleanup at 30 Hz
    }
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
actor   DaServer.Connection id 3E0F7A21  key uint64  hosted-by GatewayServer
claims  Game.Server.Account  int64
request Game.Server.GrantItem  Map -> Player  reply bool  durable
event   Game.Server.Strike   layout 7C21A0F3
event   Game.Server.Fell     layout 4B90D2E6
state   Game.Server.Looting  for GrantItem  durable
table   Game.Server.SkillTable  skills
prefab  Map     Game.Server.Map.RootPrefab          id 2F61A0C7  persist
prefab  Map     Game.Server.Prefabs.DropPrefab      id 7D2E10A4  persist
prefab  Map     Game.Server.Prefabs.AvatarPrefab    id 51B3E9F0
prefab  Map     Game.Server.Prefabs.NpcPrefab       id C40A6B12
prefab  Player  Game.Server.Player.RootPrefab       id 9E07D355  persist
record  Game.Server.Grid       nino  layout 19E4B2D8      # the persist set, one line per user type
record  Game.Server.NetIds     raw   layout 3D5A0B19      # raw: a layout change fails the build (DAS018), wherever the type is used
record  Game.Server.Inventory  nino  layout 82C7F015
record  Game.Server.NetId      raw   layout A04C7E52
record  Game.Server.Loot       nino  layout 6E12C4A7
record  Game.Server.Position   raw   layout 5C19F2B0      # also on avatars, which are not saved: the layout is frozen there too
record  Game.Server.Cell       raw   layout E83D4471
record  Game.Server.Visible    tag
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
client  Game.Protocol.EnterMap   -> Map  to Map  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
client  Game.Protocol.PickUp     -> Map  reply bool
on      Game.Server.GrantItem    Player
on      Game.Server.Strike       Map
on      Game.Server.Fell         Map
replied Game.Server.GrantItem Looting  Map
leaving Map  Vanish
```

### 测试一个 Actor：`TestActor<K>`

一个 world、一个根、DaServer 自己的手动时钟（ADR-071 的 `ManualClock`）；`Outbox` 解码成数据，直接断言。不起节点，不连网络。用户的系统用 `SystemErrorPolicy.Throw`：系统抛出就是测试失败，不像节点上那样停用了事。

```csharp
[Test]
public void Walking_out_of_range_during_the_windup_dodges()
{
    using var map = new TestActor<Map>(new Map(7), Registration.Create());    // one world, the root, a manual clock
    map.SetTable(Skills.Table);                                                 // the test project's SkillTable (Slash: reach 2)
    var a = map.Connect(new Account(1));                                        // test clients: a connection and the claims
    var b = map.Connect(new Account(2));
    map.Deliver(new EnterMap(7), client: a);                                    // both avatars spawn at (0, 0)
    map.Deliver(new EnterMap(7), client: b);
    map.Tick();                                                                 // ordered phase, the Map group, settling

    map.Deliver(new CastSkill(Skills.Slash, map.PresenceOf(b).NetId.Value), client: a);   // PresenceOf: b's avatar, an Entity
    map.Deliver(new MoveTo(new Vector2(5, 0), default), client: b);             // one step within Speed 6, beyond Slash's reach of 2
    map.Advance(Skills.Table.Windup(Skills.Slash));
    map.Tick();

    Assert.That(map.PresenceOf(b).Position.Value, Is.EqualTo(new Vector2(5, 0)));   // the step was taken, not snapped back
    Assert.That(map.PresenceOf(b).Health.Value, Is.EqualTo(100));
    Assert.That(map.Outbox.Pushes<Nearby>(to: a), Is.Not.Empty);                // what left the actor, decoded
}

[Test]
public void A_busy_tick_allocates_nothing_and_threads_change_nothing()
{
    using var map = new TestActor<Map>(new Map(7), Registration.Create());
    map.SpawnNpcs(20_000);                                                      // the test project's own extension over map.Root
    map.Tick();                                                                 // warm the pools, query caches and buffers

    Assert.That(map.AllocatedBytes(static d => d.Tick()), Is.Zero);             // 0 B, counted on every thread
    map.AssertParallelMatchesSerial(ticks: 30);                                 // Capture/Restore, then a serial twin: same result
}
```

- **`TestActor<K>` 只给通用的东西**：`Root`、`SetTable`（换上一张配置表）、`Connect`、`Deliver`、`Tick`、`Advance`、`Outbox`、`PresenceOf(client)`（返回这个客户端在本 Actor 上的 Presence 实体，`Entity`）、`AllocatedBytes`、`AssertParallelMatchesSerial`。游戏自己的帮助方法（例如 `SpawnNpcs`、"某个客户端的 NetId"）是测试工程里对 `TestActor<Map>` 的扩展方法，测试用的数据（`Skills.Table`、技能编号 `Skills.Slash`）也在测试工程里。
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
                      Regroup 已写好 Subscribed，SendFullState 按 Changed<Joined> 推完整状态，收尾的 SyncSubscriptions 把订阅集合发给网关
⑦ MoveTo              不带 [To]：Connection(c1) → c1 的 Map 路由 → Map 7 的邮箱；下一个 tick 的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Schedule(Strike, after: 前摇)，带着 Due 进到期堆；IsPending<Strike> 拒绝第二次施法
⑨ Strike 到期         tick 窗口所在那一帧的有序阶段，和消息按 (时间, 序号) 归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ PickUp(9)           主体是 Avatar：掉落向 Player 42 Ask GrantItem（[Durable]：等待实体是掉落的子实体，带 Looting、调用 id、到期 UTC、WakeAt，
                      地图的记录写成后请求才发出）；Player 42：调用 id 不在 ReplyLog 里，Grant 改背包，回复和调用 id 记进 ReplyLog，
                      记录写成后才发出；Replied 按调用 id 找到等待实体：TryGetValue 得到 true，回复客户端 true，销毁掉落；
                      这些输出在地图的记录写成后才发出。map-0 在回信前宕机：DueScan 按 next_due 唤醒 Map 7，掉落和它的等待实体从记录恢复，
                      框架用同一个调用 id 再问，Player 从 ReplyLog 回答 true，不执行第二次
⑪ 每 33 ms 一个 tick   Ordering（OrderedPhase）→ Update<Map>()：Steering、Movement、CellAssign、Regroup、SendFullState、Diff、
                      Vanish、Flush → Settling：Depart、SyncSubscriptions、去掉 Restarting/Resumed；只遍历带 In<Map> 的表 → Cleanup：ReleaseRoutes 等（按 world）；
                      Nearby 序列化一次 → 有观察者的网关各一帧 → 各个连接
⑫ map-0 宕机          map-0 超过租约就自己冻结；Map 7 在 map-1 建占位（键和等着的邮件都在它上面），占到新的 epoch，记录装进占位，它就是根：
                      Grid、NetIds 和掉落恢复，Avatar 和 NPC 不在记录里；
                      Connection(c1) 看到 map-0 离开视图：删路由，PresenceLost(槽 1) 给 SDK；SDK 重发 EnterMap(7) → Enter 新建 Avatar，Join → 同 ④–⑥
⑬ map-0 热重启        武装；SIGTERM 后宣布 NodeRestarting，协调者确认；最后一个 tick 根上带 Restarting；冻结、写快照、退出；
                      gate-0 把发给 map-0 的帧留在重发窗口；新进程加载快照、删掉文件，以同一个身份重连，协调者 Readmit 后续上流；
                      第一个 tick 带 Resumed；Avatar、Presence、路由都没变，客户端只看到一次停顿
⑭ c1 与 gate-0 的连接半开（gate-0 还没发现断线），c1 换到 gate-1 重连
                      新的 Connection(c1) 带更大的戳 S2：连上就加入，接管 Player 上的 Presence；Ready 之后 SDK 重发 EnterMap(7)：
                      GetOrSpawn 找到原来的 Avatar，Join() 按戳接管（新的 PresenceId，去掉 Joined 直到确认）；gate-0 上的旧路由按旧的 PresenceId 释放；
                      目标向 gate-0 发 TakeOver(S2)：签发序号相同，旧的 Connection(c1) 安静地关闭；确认后 Joined.MissedPushes 为真，SendFullState 重推
⑮ gate-1 宕机          Player 42 和 Map 7 上 c1 的 Presence 加上 Detached（宽限 30 秒），不推送；c1 经负载均衡器连到 gate-0，
                      戳更大，接管两个 Presence，去掉 Detached；没回来的，宽限期过后 Leave()
```

---

## 13. 编译期检查

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：接收方不对没有对应的方法；`[Handles<K>]` 类或 `[UpdateInGroup<K>]` 系统里发送，K 不在消息的 `[From<…>]` 里。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个 Actor 类型处理。
- 消息（`IFromClient`、`IToClient`、`IMessageTo<K>`、`IRequestTo<K, R>`）没有 `[NinoType]`。
- `[On<Incoming>]` 不在 `[Handles<Connection>]` 里。
- 一个请求有两个处理方法；`Replied` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`；或者既返回回复，又取 `ReplyTo<T>`；`Reply` 的值类型和令牌的 `T` 不同。
- `[On<T>]` 的 T 是框架的 tag 或组件（`Joined`、`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）：它们不是消息，修复是"写一个 `[ForEach, With<T>]` 的系统"。
- 主体一定是根的处理方法（带 `[To]` 的客户端消息、服务器之间的消息、`Activated`）取 `Presence`、`Joined` 这类只在 Presence 上的组件参数或过滤。
- `Join()` 的返回值没有用到。
- 客户端消息有多个 `[To]` 参数；`[To]` 参数的类型和处理 Actor 类型的键形状不一致；`[Handles<Connection>]` 处理的消息带 `[To]`。
- 身份参数（`IClaims` 类型）用在客户端消息以外的处理方法里；`[UpdateInGroup<K>]` 系统的方法取 Actor 类型或 `IClaims` 参数（用 `e.Root`，第 5 节）。
- `[On<T>]` 方法的参数和过滤不合 EasyEcs `[ForEach]` 的规则。EasyEcs 的分析器只检查 `[ForEach]` 方法，所以 DaServer 的分析器对 `[On<T>]` 方法做同样的检查：参数类型、`Maybe<T>` 参数（job 只收必有的组件）、过滤、`ref`/`in`、复制 `DynamicBuffer<T>`（同 EECS012）。
- 用户代码写 Actor 类型组件，或者把 Actor 类型组件放进 prefab、`CreateEntity`。
- Actor 里调用 `CreateEntity`（用 `root.Spawn`）；对看得出不是根的实体调用 `Deactivate()`。
- 承载 `Connection` 的服务器类型在 AppHost 里没有 `WithClientConnections`；消息引用的 Actor 类型没有服务器类型承载（AppHost 构建时检查，第 2 节）。
- `IChildKey<T>` 的第一个字段不是 Actor 类型（第 3 节）。
- 有索引的组件作为 `ref` 或 `Mut<T>` 参数。
- 方法体里的 `ref` 跨过这个实体的结构变更使用（DAS022）。
- `Parallel = true` 的 `[ForEach]` 方法里调用 `Entity` 上的 DaServer 操作（第 5 节的操作表）。结构变更、并行、有索引的组件，EasyEcs 另有 EECS010、EECS013、EECS015。
- `[On<T>]` 方法或 Actor 类型组里的系统以 `ref` 取 `IWorldSingleton<T>` 以外的单例；用户的单例类型既不实现 `IConfigTable<T>` 也不实现 `IWorldSingleton<T>`（第 5 节）。
- 没有 `[TickRate]` 的 Actor 类型组里，`[ForEach]` 方法既没有 `[Changed<T>]`、`[Removed<T>]`、过滤 tag 的 `[With<T>]`，系统也没有 `UpdateInterval`：每批邮件一趟全扫（第 5 节）。
- 没有 `[TickRate]` 的 Actor 类型调用 `root.SetGroupRate(key, every: n)`（第 9 节）。
- 一种服务器上各帧率不能都整除最快的那个。
- `IClaims` 类型超过 16 字节或不是非托管类型；一个工程有两个 `IClaims` 类型。
- `[JoinOnConnect<C>]` 的 Actor 类型的键和 C 的形状不同；一个 Actor 类型写了两个 `[JoinOnConnect]`。
- Protocol 里出现 Actor 类型、服务器之间的消息、`ActorId`、`PresenceId` 或 `EntityId`；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- Actor 类型的键超过 16 字节，或者不是非托管类型。
- 生成器要读的 prefab（`[Prefab<K>]` 字段、`RootPrefab`）不是至少 `internal`；`[Persist]` 写在 Actor 类型的 `RootPrefab` 以外，或者 `RootPrefab` 上写了 `[Prefab<K>]`（第 3 节）。
- 根不保存的 Actor 类型有 `[Prefab<K>(Persist = true)]` 的 prefab；看得出要保存的 prefab 建在不保存的实体下面（第 10 节）。
- 进记录的组件类型是托管的却没有 `[NinoType]`；按原始字节进记录的组件改了布局（DAS018，第 10 节的序列化规则）。
- `persist` 里的类型没有 `ITrackChanges`，在这个编译里又补不上：不是 `partial`，或者来自别的程序集（第 10 节的"只写改过的"；不是 `partial` 的可一键修复）。
- 对 `persist` 里的 tag 调用 `Set` 或 `Remove`（W1 核对 tag 的增删会打戳之前，第 10 节）。
- `[Stateless]` 的 Actor 类型里 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、带 `after` 的 `Schedule`。
- `[Durable]` 写在不是 `IRequestTo` 的消息上，或者接收方是 `[Stateless]` 的 Actor 类型（它不写记录）；`[Durable]` 请求的 `Ask` 状态不能写进记录（第 10 节）；看得出 `[Durable]` 请求的 `ReplyTo<T>` 存进了不保存的实体，或者从不保存的实体发问（第 8 节）；`Cancel` 一个 `[Durable]` 的请求；`[Durable]` 请求的接收方 Actor 类型不保存，或者 `OnError` 不是 `Reload`（第 8 节）。`[Durable]` 的 `Ask` 没写 `settleBy:` 是编译错误：生成的方法里它不是可选参数。
- `[ActorOptions]` 的值是负数。
- 要迁移的托管组件、托管的事件或 `Ask` 状态没有 Nino 格式化器（第 10 节的序列化规则）。
- 用户的泛型 `[ForEach]` 系统只在别的程序集里封闭（EasyEcs 要在运行时 `MakeGenericType`，Native AOT 下不行，第 10 节）。

**警告和提示**（不报错）：
- 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`：改读 `Time.Utc`（警告，第 9 节）；
- 处理方法里结构变更之后还有 `throw` 或可能抛出的调用：先校验，最后做结构变更（第 5 节）；
- 追踪变更的组件作为 `ref` 参数，却不是每条路径都写它：改用 `in` 或 `Mut<T>`，不写就不打戳（第 5 节；第 12 节的 `Moves.Move`）；
- 经 EasyEcs 生成的 `entity.X` 读一个由框架打开追踪的组件类型：改用 `in` 参数或 `entity.Read<T>()`，否则这次读也算写入（第 10 节）；
- `Replied` 处理方法里没有延后就重新 `Ask`（普通请求）：改成 `Schedule(…, after: a.RetryAfter)` 再问，不在对方忙的时候立刻压上去（第 11 节）；
- 在 Presence 实体上 `Ask`：Presence 离开时等待实体随它销毁，回信丢弃；要收尾的从根或者不会离开的子实体问（第 8 节）；
- 销毁一个看得出下面有等待中的 `[Durable]` 请求的实体：这一项会按抛出处理（第 8 节）。

**启动时检查**：配置的服务器类型不存在，或者和 `wire.manifest` 对不上，节点启动失败。热重启的快照不合格时忽略它，照常从记录激活（第 10 节）。

---

## 14. 已定的取舍，以及待办

本稿没有需要拍板的项。下面是起草时比较过的做法，以及按需求（通用、ECS、解耦、DRY、易维护、分布式正确）选定的理由。

1. **身份用用户自己的类型 `Account : IClaims` 加 `[JoinOnConnect<Account>]`**，不用框架包装 `Claim<Player>`。框架是通用的，不是每个应用都有 Player（例如秒杀的买家）；身份绑在某个 Actor 的键上，令牌格式会随那个键变。身份是一个接口（`IClaims : IComponent`），不是特性：EasyEcs 和 C# 的泛型约束都看得见它（`[JoinOnConnect<T>] where T : struct, IClaims`），生成器不用补任何东西。代价是 `Account` 和 `Player` 的字段写两遍，由分析器保证形状相同。
2. **客户端路由由 `[On<T>]` 所在的 `[Handles<K>]` 类推出**，不另写路由表。同一件事只写一处；改动路由的版本要连网关一起滚动升级，热重启由 `wire.manifest` 比对拦住（第 10 节）。
3. **`[From<K>]` 保持必写**。"从类型就看得出谁发给谁"是目标之一；新的发送方要改消息声明，这正是想要的显式。
4. **消息不做成实体**。邮箱、到期堆和即时链在有序阶段归并三个来源。把每条消息做成带 `Due` 的实体能统一表示，但每条消息多一次建实体和销毁；到期堆和链栈已经给出同样的顺序，而延后事件本来就是实体（要保存、迁移）。所以不做。
5. **EasyEcs 4.6.0 已提供本稿依赖的功能**：`entity.Run(ref job[, filter])`（抛出时 `ref` 写入也搬到新行；job 带 `INoStructuralChanges` 时冻结结构）、接口带的组过滤 `IGroupWith<T>` / `IGroupWithout<T>`、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（被覆盖的 prefab 默认值也不算写入），分析器把 `entity.Run` 和 `LoadEntitiesInto` 当结构调用（EECS010/013/014）；4.6 又加上 `world.Load(bytes, markWritten: false)`（EasyEcs D39）、`AddSystem(…, changesFromNow: true)`，带追踪组件的程序 Native AOT 发布 0 警告（EasyEcs D38）。EasyEcs 评估后放弃的：并行批量方法、`LastWriteTick`、公开读写集、多 world 共享并行预算。重构的第一步是 [W1](../roadmap.md)：`EasyEcsVersion` 升到 4.7（发布之前用 4.6.0），本稿依赖、要先核对的 EasyEcs 编译和行为（每一项一个测试）以及 4.7 的各项在 4.6 上的替代做法，只列在那里。
6. **改代码只有热重启，不用 `AssemblyLoadContext`；每个平台一种机制**：理由见 ADR-084 和 ADR-088，做法见第 10 节；Kubernetes 上的形态见 ADR-085。`Save` 写进 `NinoArrayBufferWriter` 再写进文件，多一次拷贝；几 MB 可以忽略，不另提需求。
7. **能是数据的都是数据**（ECS 优先于 OOP）：停用、热重启前后、离开、断线、确认都是根或 Presence 上的 tag 或组件（`Deactivating`、`Restarting`、`Resumed`、`Leaving`、`Detached`、`Joined`），由系统反应，不是回调或虚方法；要不要保存是实体上的 `Persisted`；待处理的个数、为 `[Durable]` 排队的输出、`ReplyLog`、`NextDue`、组的发送频率在根上；节点状态（`RestartingNodes`，以及内部的存储降级、激活名额、各目标节点的在途上限）是节点单例；`Join()` 的结果是值。只有 `Activated` 是邮件：它必须排在占位上等着的邮件之前。保留的：
   - 单例 `Current`（当前项的上下文）：它是 world 的数据，由运行时在每项之前写；改成每个操作显式传参，每个 `Send`/`Ask`/`Join` 都要多一个参数，换不来别的；
   - `Registration` 的构建写法：它是生成的，只在启动时跑一次，写出封闭类型给 AOT；
   - `ICluster` 和 `TestActor<K>`：它们在 world 外面（HTTP、测试），不是 Actor 里的逻辑。
8. **对 EasyEcs 的需求**（EasyEcs 仓库的 issue）。排进 4.7（计划中，未发布）的：
   - #23 值不同才打戳的写入（同 Bevy 的 `set_if_neq`）：`Mut<T>` 参数、`SetIfChanged(v)` 和生成的 `entity.SetXIfChanged(v)`（第 5 节、第 12 节的 `Steering`、`Movement` 和 `Moves.Move`）；
   - #24 带组件实例化：`world.Instantiate(prefab, in c1 … c9)`，一次写进最终的表；`root.Spawn` 和 `GetOrSpawn` 用它，不再移动行；
   - 4.7 第 3 项：`[ChangedAny<…>]`/`[RemovedAny<…>]`，以及 #25 带起点的 `QueryFilter.ChangedAny<T1..Tn>(ulong since)` / `RemovedAny<…>(since)`（每个过滤一个起点，`Since(tick)` 改它，所以存在字段里的一个过滤能服务每个客户端确认过的 tick；第 15 节的 `DaServer.Replication` 用它）；
   - 4.7 第 9 项（#26）：`world.Save(output, in ComponentSet exclude)` 和 `world.Load(input, markWritten, in ComponentSet keep)`，加上 `ComponentSet.WithSingleton<T>()`：热重启时用户的单例（配置表和 `IWorldSingleton<T>`）不进快照、加载时保留新进程的（第 10 节）；
   - 另外用到的：`IUniqueIndexed<T>`、`TrimExcess()`、`[ForEach(Slices = N)]`、`SystemStats` 的分配字节和匹配的实体数、`DescribeSchedule()`（4.6 上的替代做法见 W1）。

   已提出、还没转交给 EasyEcs 的：#27 运行时按类型打开变更追踪（第 10 节的"只写改过的"：`Registration` 给 `persist` 里的类型打开它）。在它之前，生成器给这些类型补上 `ITrackChanges` 的声明。

   本稿依赖的已有行为：加上一个被追踪的组件算一次写入，去掉它记下一次移除（EasyEcs `reference/itrackchanges.mdx`）；tag 和按 #27 打开追踪的类型是不是一样，W1 核对（第 10 节）。

   EasyEcs 文档里的模式照着用，不另造：祖先遍历（`e.Root`）。固定步长和追帧上限（[architecture.md 第 2 节](architecture.md#the-shard-loop)的循环）、延迟补偿的历史环（样例要做命中判定时用）是 DaServer 自己的做法。
9. **`[Durable]` 两端都由框架负责，普通请求不去重**。去重的记录（`ReplyLog`）搭在 `[Durable]` 本来就要的那次写记录上，不多一次存储往返；每个请求都去重，就要每个请求查一次存储（ADR-082）。发问方的等待实体在请求发出之前写进记录：否则发问方宕机后从旧记录恢复，同一件东西会再许给别人；处理回信的输出在记录写成之后才发出，同一个结果不会被处理两次；期限以收尾的一问结束，所以一个调用 id 最多执行一次，`Expired` 只剩"联系不上"。用户不再写预留、重问和去重的组件。
10. **宕机后的唤醒（`WakeAt`、`NextDue`、`DueScan`、`IActorStore.DueKeys`）在运行时，不在 `DaServer.Reminders`**：`[Durable]` 的发问方也要它，而运行时不能依赖扩展能力（ADR-045）。`NextDue` 只由运行时按 `WakeAt` 算；`DaServer.Reminders` 只在提醒的实体上写 `WakeAt`（第 15 节）。
11. **保存按实体的种类，不按组件类型**（第 10 节，ADR-082）：同一个组件类型（`NetId`、`Position`）既在要保存的掉落上，也在不保存的 Avatar 上；按类型保存，要么掉落恢复后缺数据，要么 Avatar 被拖进记录。
12. **对照别的 ECS 之后保留的**（flecs、Bevy、Unity Netcode for Entities、Photon Quantum、Overwatch、SpatialOS）：
    - 一个 world 里有很多 Actor：系统按表（archetype）一趟走完这个 world 上同类型的所有 Actor，不是一个 Actor 一个 world（Overwatch 一局一个 world，所以单例能一局一份；这里的单例规则见第 5 节）；
    - 对数据的反应是对 tag 反应的系统（`Leaving`、`Deactivating`、`Restarting`），不是 observer：flecs 自己的建议是"能用系统解决的，就该用系统"（[Observers manual](https://www.flecs.dev/flecs/ObserversManual.html)），系统成批、有固定的位置和顺序；
    - 邮件和事件只有一个按 `(时间, 序号)` 的全序（第 1 节），比 Bevy 按帧缓冲的 message 更严：同一帧里的先后不会因为读的系统排在哪而变；
    - 格子是一个组件的非唯一值索引（`Cell`），不是每个格子一个 tag 或关系：那样每个格子一种表，实体跨格就换表，表越分越碎（flecs [relationships 手册](https://www.flecs.dev/flecs/Relationships.html)讲的碎片问题）；
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
- 提醒的实体是要保存的实体（包里的 prefab 带 `Persist = true`），带运行时的 `WakeAt`，值就是 `UtcAt`。
- **激活着的时候**：包里的系统给每个提醒挂一个 `Due`（离 `UtcAt` 还有多久，第 9 节），到期时按 `(时间, 序号)` 给根投递 `Reminded<T>(UtcAt)`；处理方法返回后，同一项里 `UtcAt` 前进到第一个还没到的时刻（`Period` 为 0 就销毁这个子实体），`WakeAt` 跟着改。从记录加载的提醒没有 `Due`（它不在记录里），由 `[With<Reminder<T>>, Without<Due>]` 的系统挂上；过了时刻的在第一个 tick 执行。
- **停机之后只补一次**：Actor 停了好几个周期（宕机、停用、整个集群停机）之后，过了时刻的提醒只投递一次，`UtcAt` 直接前进到第一个将来的时刻，不按错过的周期一个个补。
- **没激活的时候**：运行时按 `WakeAt` 算出根上的 `NextDue`，检查点把它写进记录的 `next_due` 列，`DueScan` 到时激活这个 Actor（第 10 节的唤醒）。所以提醒能叫醒一个停用了的 Actor，节点宕机也不丢。包不写 `NextDue`。
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

- `Topic(long Id)` 是包里内置的 Actor 类型，根要保存，根上是订阅者表 `DynamicBuffer<Subscriber>`（`Subscriber(ActorId Actor, long Epoch)`，`Epoch` 是订阅者那次激活的 epoch）。`e.Subscribe(topic)`、`e.Unsubscribe(topic)` 是发给它的消息，订阅的是 `e.Root` 那个 Actor。`Feeds.Prices` 是用户自己的常量。
- **订阅也是订阅方的数据**：订阅方的根上有持久化的 `DynamicBuffer<Subscription>`。包里的系统在 `Activated` 之后重新订阅（幂等），在 `Deactivating` 时退订；所以 Topic 的表里只有激活着的订阅者，投递从不激活 Actor。投递到已经不在的订阅者时，那个节点回一条带着这个 epoch 的控制邮件，Topic 删掉它（它下次激活时自己再订阅）。
- **删除只认同一个激活**：`Subscribe` 写下或更新订阅者的 epoch；退订和"已经不在"的通知都带着发出它的激活的 epoch，只有和表里的相同才删。所以旧激活迟到的退订或通知，删不掉新激活刚做的订阅。
- **`e.Publish(topic, msg)`**：发给 Topic；Topic 不解码负载，按订阅者所在的节点分组，每个节点一帧（只序列化一次，池化的帧），那个节点再放进各订阅者的邮箱。`ICluster` 也能发布。
- **最多一次**；同一个发布者经同一个 Topic 发给同一个订阅者的按发送顺序到达。没有回放，没有持久的偏移：要持久的流（回放、消费组）用 Kafka 或 NATS JetStream，从 Actor 里调用。
- 一个 Topic 的订阅者数有上限（节点配置）；更大的扇出分到多个 Topic 键，推给客户端用 `ClientGroup`（第 9 节）。
- Actor 之间的扇出由它做；框架本身没有别的发布。

### 非 .NET 的客户端

- **客户端的线协议写成公开的规范**：帧格式、握手（令牌、`wire.manifest` 哈希）、`Ready`、重连和重新加入（槽号、`JoinConfirmed`、`PresenceLost`、`PresenceClosed`）、`Retire` 和 `HandedOff`、`Superseded`、`Unavailable`、`Busy` 的 `RetryAfter`、心跳和静默断开。C# SDK 是参考实现，一套一致性测试同时跑 C# 和其他实现。
- **TypeScript 客户端**（npm 包，经浏览器的 WebSocket 连 `/clients/ws`）：消息的编解码是 Nino 按 `Game.Protocol` 的 `wire.manifest` 生成的 TypeScript 代码，浏览器拿到和 Unity 一样的类型；槽、重新加入、`Retire`、退避按规范实现。
- Unreal / C++ 以后按同一份规范做。

### `DaServer.Replication`：组件同步到客户端

```csharp
[Replicated] public record struct Health(int Value) : IComponent, ITrackChanges;   // changed fields reach the clients
```

- `[Replicated]` 写在追踪变更的组件上（分析器检查 `ITrackChanges`，以及非托管或 `[NinoType]`）。实体同步给谁，由用户写的 `ReplicatedTo(ClientGroup Group)` 决定，例如 AOI 系统写它所在格子的组。客户端看到的实体编号是包分配的 `ReplicaId`（每个 Actor 一个计数器），不是 `EntityId`。
- **状态是 DaServer 自己的数据**，不依赖 EasyEcs 记住发过什么：
  - 每个 `[Replicated]` 组件旁边有内部组件 `Replica<T>`：上次发出的值和发出它的 tick（`Time.Tick`）；
  - 每个组有一个有界的环，记着离开这个组的实体（换组或销毁）和离开的 tick；
  - 每个 Actor 有一个**化身号**（随机 64 位，在根上）：从记录激活、迁移到达、热重启加载、`OnError = Reload` 时都换新的。tick 只在同一个化身里可比。
- **每个 tick**：每个 Actor 类型一个包里的系统，`[ForEach, ChangedAny<所有 [Replicated] 类型>, With<ReplicatedTo>]`（EasyEcs 4.7 计划）：一个实体改了几个组件也只出一份补丁。生成器给每个类型生成按字段的位掩码，和 `Replica<T>` 比较，按 `ClientGroup` 把改了的字段写进池化的帧，一个组一帧（序列化一次，网关扇出，第 9 节），再更新 `Replica<T>` 和它的 tick。离开组的实体写成移除。
- **全量**：实体换组（`[Changed<ReplicatedTo>]`）时，新组收到它的全量、旧组收到移除；Presence 新订阅一个组时收到它订阅的组里所有实体的全量。
- **重新加入只补差**：`[Changed<Joined>]` 并且 `MissedPushes` 为真时，SDK 在加入时带着它最后应用的（化身号, tick）。化身号相同、组的离开环还够得着那个 tick，就只发那之后的：离开环里的移除，以及 `Replica<T>` 的 tick 更新的组件；查询用 EasyEcs 4.7 的 `QueryFilter.ChangedAny<…>(since)`，一个存在字段里的过滤用 `Since(tick)` 换起点，不分配（只是更快的查找，不靠它判断正确性）。化身号不同（Actor 重新加载、迁移、热重启、`Reload`），或者环已经被覆盖，就发全量。
- **重发全量就是 `Reset(化身号)`**：先发一条 `Reset(化身号)`，客户端清掉这个 Actor 的所有副本，再应用全量。所以离线期间被移除的实体不会留在客户端上，客户端也从此按新的化身号报告（化身号, tick）。
- 客户端 SDK 应用补丁：C# 的 `DaServer.Replication.Client` 写进客户端的 EasyEcs world，TypeScript 的写进按 `ReplicaId` 索引的实体表。
- 最多一次，和推送一样；漏了的由全量补上。先在 mmo 样例上证明：它的 AOI 推送（`Nearby`）改成同步组件。

### 测试和管理的小工具

- **`TestCluster` 的故障注入**（`DaServer.Testing`）：`cluster.Partition(a, b)`（双向或单向）、`cluster.Kill(node)`（不经过优雅停止）、`cluster.DelayLinks(a, b, delay)`。用户用它测自己的逻辑在分区、宕机、慢链路下的结果，和框架自己的测试是同一套。
- **查看端点**（只读，也是 Aspire 命令）：一个 Actor 的子树、最忙的 Actor、每个 world 的系统、按组件计数；端点和规则见 [architecture.md 第 8 节](architecture.md#inspector)。
