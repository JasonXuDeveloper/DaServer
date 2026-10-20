# DaServer 新编程模型设计（草案 v23，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。能用 EasyEcs 的功能就用 EasyEcs，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个 Actor 发到哪个 Actor，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分；
- 高性能，高并发。

本稿是完整的一份。每条规则只在它所在的那一节写一次，别处只给链接。第 12 节是完整的例子，分别列出用户写的代码和生成的代码。第 13 节是检查和版本记录，第 14 节是待拍板的点。

文中引用的 EasyEcs 文档都在它仓库的 `docs/content/` 下。

---

## 0. 一页看懂

### 三个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息。只有数据，不出现任何服务端的 Actor 类型 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | Actor 类型、服务器之间的消息、本地事件、组件、prefab、服务器类型，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有逻辑：`[Handles<K>]` 类里的 `[On<T>]` 方法、`[UpdateInGroup<K>]` 的 `EcsSystem`，以及生成的分发表和客户端路由表 | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

### 用户写的

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 发给指定 Actor 的客户端消息 | `record struct EnterMap([To] long Map) : IFromClient<bool>;` | Protocol |
| 推给客户端的消息 | `partial class Nearby : IToClient`（含集合的消息是 `[NinoPooled]` 类） | Protocol |
| Actor 类型（地址、实体上的组件、每 tick 的系统组） | `[TickRate(30), With<In<Map>>] readonly partial record struct Map(long Id) : IActor<Map>;` | Server |
| 服务器之间的消息 | `[From<Map>] record struct GrantItem(int Item) : IRequestTo<Player, bool>;` | Server |
| 本地事件 | `[NinoType] record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；持久化的写成 `[NinoType, Persist] record struct Grid(…) : IComponent, ITrackChanges;`（第 10 节） | Server |
| prefab | `[Prefab<Map>(Root = true)] static readonly Prefab Map = …;` | Server |
| 令牌里的声明 | `[Claims<Player>] partial record struct PlayerClaims;`：字段由生成器按 Player 的键补上 | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 对消息和事件的反应 | `[Handles<Map>] static partial class Combat { [On<CastSkill>] … }` | Logic |
| 每 tick 的逻辑 | `[UpdateInGroup<Map>] class Movement : EcsSystem { [ForEach] … }` | Logic |
| 现在或以后做一件事 | `me.Send(new Strike(…), after: Skills.Windup(skill))`，不写 `after` 就是紧接着 | Logic |
| 等别的 Actor 的结果 | `me.Ask(new Player(who.Id), new GrantItem(item), new Looting(drop, reply))`，回信是 `Answer<GrantItem, Looting>` | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `IActor<T>` | Actor 类型的接口：组件、值索引、EasyEcs 的系统组（第 3 节） |
| `Client` | 内置的 Actor 类型：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Entity` | 唯一的句柄。ECS 的成员来自 EasyEcs，发送、推送、加入等操作来自 DaServer（第 5 节） |
| `World` | 只做 ECS：找实体、建实体、读写组件；外加 `world.Cancel(in scheduled)` |
| `ActorId` | 任意 Actor 的地址（Actor 类型的 `TypeId` 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键 |
| `TypeId<T>.Value` | 类型的 id（Nino 类型 id）。Actor 类型、消息、事件、组件都用这一个名字 |
| `Sender<K>` | 处理方法的参数：这条消息的发送方 |
| `[To]` | 客户端消息的字段：目标 Actor 的键（第 4 节） |
| `PresenceId`、`Presence`、`Joined`、`NeedsSnapshot` | 客户端在某个 Actor 上的 Presence：Actor 里的一个子实体，例如地图上的 Avatar（第 7 节） |
| `In<K>` | tag：这个实体属于一个 K 类型的 Actor。给系统组过滤用（第 5 节） |
| `ReplyTo<T>`、`Answer<TReq, TState>` | 回复令牌（普通数据）；等别的 Actor 的结果，`TState` 是发问时存下的状态 |
| `Activated`、`Left`、`PresenceLost`、`Deactivating` | 生命周期消息，用 `[On<T>]` 处理 |
| `[TickRate(n)]` | 写在 Actor 类型上的帧率 |
| `Time` | shard 的单例：`Tick`、`DeltaTime`、`TickRate`、`Now` |
| `Scheduled<T>` | 延后的事件的句柄，普通数据：可以存进组件，用来取消或表示"正在进行" |
| `IdleTimeout` | 空闲回收 |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 值索引（`world.MapIndex`） | 为 `partial` 的 `IIndexed<T>` 记录结构生成 `Key => this` | 收到消息时查索引 |
| 逻辑属于哪个 Actor 类型 | — | `[Handles<K>]` | 分发表按 Actor 类型建 |
| 事件的顺序 | 有序值索引（`Range`） | `Due` | 每个 shard 按 `(时间, 序号)` 归并邮箱和到期项 |
| 消息处理 | — | `[On<T>]` 生成的分发跳板 | 逐个调用，逐个隔离异常 |
| 参数绑定 | — | 一个规则：组件参数绑定到主体 | 跳板读主体的组件 |
| 每 tick 的逻辑 | `EcsSystem`、`[ForEach]`、批量方法、`Parallel`、`[Changed<T>]`、`[UpdateAfter<T>]`、`ISystemGroup`、组过滤 | Actor 类型就是系统组，`[TickRate]`、`In<K>` | 有序阶段之后 `world.Update<Map>()` |
| 延后的事件、框架的期限 | `ChildOf`、有序值索引 | `Send(e, after:)`、`Scheduled<T>`、`IEvent`、`Due` | 到期项和消息一起进有序阶段；发起者销毁时一起消失 |
| 发送方向 | — | `[From<K>]` 生成发送方法，分析器按调用处的 Actor 类型检查 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Handles<K>]`、`[To]` 生成路由表 | `Client` 实体直接转发给目标 |
| 等待、回复 | `ChildOf` | `Answer<TReq, TState>`、`ReplyTo<T>` | 调用 id、`Due` 期限、期限传递 |
| Presence | 子实体、值索引 | `[Claims<K>]`、`Join()` | 挂接、重试、顶号、写路由、确认、核对、重放加入消息 |
| 生命周期 | tag（`Loading`、`Saving`） | 用户写 `[On<Activated>]` 等 | 占位、投递生命周期消息 |
| 一个 Actor 的实体 | `ChildOf`、`world.Children(map)` | `In<K>` | — |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ICleanupComponent` | `[Persist]` 要求声明 `ITrackChanges`（分析器） | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收的上下文 | 分发表、路由表、系统在 Logic 程序集里 | 两个 tick 之间切换 |
| 对象复用 | `DynamicBuffer` 池化、Nino 的 `[NinoPooled]` | — | 帧、发送缓冲池化 |

---

## 1. 保证

1. **每个域按 `(时间, 序号)` 逐条处理自己的消息和事件，域之间并行。** 生命周期事件也是消息，排在同一个顺序里。
2. **同一个发送方发给同一个目标的消息，按发送顺序到达**，前提是双方都没有在迁移。
   - 迁移期间只尽力保序，同 Orleans：迁移那一两秒里，在途的消息可能后发先到。
   - 需要严格先后的业务，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。
4. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。
5. **每种失败的结果都是确定的**，见第 11 节。

### 执行模型：每个域是一个离散事件模拟

**Actor** 是 Actor 类型的一个实例，例如 7 号地图。**域**是一个 Actor 的根实体和它的子孙，例如一张地图和地图上的所有实体。域里发生的一切，都是排好序的事件：

| 事件 | 时间 | 例子 |
|---|---|---|
| 客户端或别的 Actor 发来的消息 | 进入本 shard 的时刻 | 移动一步、放技能、交易 |
| 本地事件 `e.Send(ev)` | 紧接着当前处理方法 | 立即结算的技能 |
| 延后的事件 `e.Send(ev, after: 时间)` | 当前项的时间 + after，精确值，不取整 | 前摇 0.3 秒后的结算、5 秒后 buff 到期 |
| 框架的期限 | 绝对的到期时刻 | 请求超时、加入确认期限 |
| 系统 | 每个 tick，有序阶段之后 | 移动推进、AOI |

**排序只有一条：所有项按 `(时间, 序号)` 执行。**
- 时间：消息是进入本 shard 的时刻，延后事件和期限是精确的到期时刻。序号是 shard 的计数器，时间相同时按先后打破平局。
- **tick 窗口**：时间落在 (t − Δ, t] 的项，在时刻 t 那个 tick 的有序阶段里按 `(时间, 序号)` 执行（Δ 是帧长）。tick 只决定"什么时候执行"，不改变"谁先谁后"。
- 所以 0.29 秒到达的闪避排在 0.30 秒到期的结算前面，即使它们落在同一帧里。
- 每个项看到的，正好是它之前所有项的全部效果。

**时刻 t 的一个 tick：**

| 阶段 | 做什么 | 怎么跑 |
|---|---|---|
| a. 有序阶段 | tick 窗口里的消息和到期项按 `(时间, 序号)` 归并（第 9 节） | 串行。每一项执行完，紧接着执行它的即时链（不带 `after` 的 `Send`）；每项最多连锁 1,024 个 |
| b. 系统 | `world.Update<Map>()`：这个 Actor 类型的 EasyEcs 系统（第 5 节） | 默认单线程；`[ForEach(Parallel = true)]` 的方法分块多线程 |

- 有序阶段开始时取一次邮箱快照；这期间新到的消息进下一帧。
- 只有有系统的 Actor 类型按 tick 走（例如地图）。Player、Guild、`Client` 没有 tick：消息到了就处理，延后事件到了时间就执行，顺序规则不变。
- 到期时要重新校验：施法者还活着吗？被眩晕了吗？目标还在、还够得着吗？

**结果：** 在第 k 帧执行的结算，看到的是第 k−1 帧的系统跑完后的位置，再加上时间比它早的所有消息和事件，包括同一帧里先到的移动。所以前摇期间走开，就是闪避。

**例子：同一帧里依次到达 [A 打 B，B 移动，A 移动，B 打 A]**

前摇为 0（`after` 是 `TimeSpan.Zero`）：
1. A 的 `Cast` 返回后，紧接着执行 `Strike`：读 B 还没动的位置，够得着就扣血；B 被打死则带上 `Dead`。
2. B 移动：B 已死的话，`Move` 直接返回。
3. A 移动。
4. B 的 `Cast`：B 已死则拒绝；否则紧接着结算，读 A 移动后的位置。

前摇 0.3 秒，30 Hz（9 帧）：
1. 第 k 帧：A 的 `Strike` 到期时刻 t_A + 0.3 秒；B 移动；A 移动；B 的 `Strike` 到期时刻 t_B + 0.3 秒，比 A 的晚。两个都带着 `Due` 进有序索引。
2. 第 k+1 到 k+8 帧：B 继续发 `MoveTo` 走开，系统推进 NPC。
3. 到期的那一帧，有序阶段按时间执行：A 的 `Strike`、B 的 `Strike`，以及这一帧里时间夹在它们之间的消息（例如 B 在 A 到期前一刻发来的移动）。A 的结算读 B 此刻的位置：已经走出范围，就是闪避。如果 A 打死了 B，B 的结算发现施法者已死，作废。

**并行从哪里来：**

| 来源 | 做法 |
|---|---|
| 很多个域 | 一个 shard 是一个线程，跑很多张地图；节点有很多 shard，集群有很多节点。TrinityCore 的 MapUpdater、EVE 的星系、LoL 和 Valorant 的对局都是这样 |
| 系统 | 一次 `world.Update<Map>()` 覆盖这个 shard 上这个 Actor 类型的所有域；重活的方法写 `Parallel = true` |

- 域内的有序阶段永远串行。没有上市的游戏把一个模拟步按空间切开并行：那需要框架懂位置、作用距离、最大速度这些游戏概念（违反 ADR-045），还会加一帧延迟，语义也脆弱。
- 以后再考虑：
  - 一个超大的域，只有在基准测试显示它的有序阶段超出帧预算时，才提供用户自己给出分区的可选方案；
  - 无缝大世界用 New World 的做法：世界按格子分给进程，人移动时交接。

**不采用的：**
- 乐观执行（Time Warp 回滚）：保存状态或写反向计算的开销和一个约 1 µs 的事件本身差不多，而且回滚前已经推给客户端的结果收不回来。
- 按消息类型分批：会打乱 `(时间, 序号)`。只有系统按列批量处理。
- 把命令排到下一帧统一执行：等于把效果挪后，会出现"发包时打得到、执行时人跑了"。

**用户写的只有两种**：对消息和事件的反应（`[On<T>]` 方法），以及每 tick 的逻辑（`[UpdateInGroup<K>]` 的 `EcsSystem`）。第 12 节是完整的例子：`Maps` 加入地图，`Combat` 施法和结算。下面两段摘自第 12 节：

```csharp
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>]                                                           // subject: this client's avatar; applied now, later items see it
    static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed)
    {
        if (me.IsDead) return;
        if (!Steps.IsValid(p.Value, m.At, speed.Value)) { me.Push(new SnapBack(p.Value)); return; }
        p.Value = m.At;
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

用户不判断"立即还是延后"（`after` 来自数据，0 也合法），也不判断"每条消息还是每 tick"（反应写 `[On<T>]`，每 tick 写系统）。

**用户能看到的反馈：**
- 每个域的指标：每个 tick 有序阶段的耗时；每个系统的耗时（EasyEcs 的 `MeasureSystems` 和 `system.Stats`）；
- 调试模式：`MaxDegreeOfParallelism = 1`，把 `Parallel = true` 的方法改成单线程跑，核对结果是否完全一致。

---

## 2. 服务器与节点

```csharp
// Game.Server
[Hosts<Player>, Hosts<Guild>] public struct GameServer    : IServer;
[Hosts<Map>]                  public struct MapServer     : IServer;
[Hosts<Client>]               public struct GatewayServer : IServer;   // hosting Client = accepting clients
```

- **一个工程可以定义多种服务器类型；节点 = 一个进程 = 一种服务器。**
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程只跑一种服务器。
  - 本地开发由 Aspire 起多个进程；测试用 `TestCluster`。
  - 小规模部署可以写一个 `[Hosts<Player>, Hosts<Map>, Hosts<Client>] AllInOne`。
- **`[Hosts<Client>]` 就是"接受客户端"。**
- **Actor 类型绑定的是服务器类型，不是节点：**

  | 关系 | 数量 | 由谁决定 |
  |---|---|---|
  | Actor 类型 → 服务器类型 | 一般一对一；`AllInOne` 时一对多 | 服务器上的 `[Hosts<…>]` |
  | 服务器类型 → 节点 | 一对多 | AppHost 部署几个 |
  | Actor 类型 → Actor | 每个键一个 | 第一条消息到达时激活 |
  | Actor → 节点 | **任一时刻恰好一个** | 在承载该 Actor 类型的节点中按键放置，节点增减时迁移 |

  - 同一个键任一时刻只有一个 Actor，由现有的放置、记录版本和隔离机制保证。
  - `Client` 是例外，它固定在连接所在的节点（第 7 节）。
- **shard**：节点里的一个线程加一个 world。节点承载的所有 Actor 类型共用 shard 的 world，Actor 落在哪个 shard 由键的哈希决定。
- **帧率**写在 Actor 类型上（第 5 节），不在配置里。
- **AppHost：**
  ```csharp
  var cluster = builder.AddDaServerCluster("game");
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientEndpoints();
  ```
  - AppHost 不引用服务端程序集。DaServer.Aspire 的生成器读服务端工程提交的 `wire.manifest`，只读 `actor` 和 `hosted-by`（第 10 节），在 AppHost 里生成同名的服务器类型。
  - `AddNodes<T>` 返回 `NodeGroup<T>`。承载 `Client` 的服务器，生成的类型实现 `IAcceptsClients`；`WithClientEndpoints()` 是 `NodeGroup<T>` 上 `where T : IAcceptsClients` 的扩展方法，所以给别的服务器开客户端端口编译不过。
  - 接受客户端的服务器没开端口时，AppHost 构建警告；消息引用的 Actor 类型没有节点承载时，构建报错。
  - 节点启动时，配置的服务器类型不存在，或者和 `wire.manifest` 对不上，启动失败。

---

## 3. Actor 类型与地址

**Actor 类型就是它的地址，也是根实体上的组件，也是它每 tick 的系统组。**

```csharp
// Game.Server
public readonly partial record struct Player(long Id) : IActor<Player>;
public readonly partial record struct Guild(long Id)  : IActor<Guild>;
[TickRate(30), With<In<Map>>] public readonly partial record struct Map(long Id) : IActor<Map>;   // 30 ticks a second
public readonly partial record struct Room(long Id)   : IActor<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IActor<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个 Actor 的地址。消息和组件里存别的 Actor 的地址，直接用 `Map`、`Player` 类型的字段。要存"任意 Actor 的地址"时用 `ActorId`。

**同一个值也是组件**：地图的根实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryFind(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 处理方法里知道自己是谁：主体是根时取组件参数 `in Map map`；主体是子实体时，从 `ChildOf` 找到根，见第 5 节的绑定规则。

**接口**：`IActor<T>` 继承 `IComponent`、`IIndexed<T>` 和 `ISystemGroup`。
- EasyEcs 认继承来的接口，所以 EasyEcs 不用改：
  - `[UpdateInGroup<TGroup>]` 的约束是 `struct, ISystemGroup`，C# 认继承来的接口；
  - EasyEcs 的生成器从特性的类型参数取组，从组类型上读组过滤；分析器用 `AllInterfaces` 判断"是不是系统组"（EECS023 不会误报）。
- **`Key => this` 由生成器补上**：任何实现 `IIndexed<T>`、键就是它自己的 `partial record struct` 都一样，Actor 类型、`Avatar`、`Cell` 都是。用户不写 `Key`。
- Actor 类型的 id 是 `TypeId<Map>.Value`，没有单独的常量。
- `[TickRate]` 和组过滤 `[With<In<K>>]` 的规则在第 5 节。

**只有运行时能写 Actor 类型组件：**
- 分析器禁止用户代码对 Actor 类型组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时创建 Actor 时，断言这个键在索引里还不存在。

**键**：
- Actor 类型的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[Stateless]` Actor 类型**：不保存，不迁移；每个节点可以有自己的 Actor，键只用来选 Actor。
- **子实体不可寻址**，例如 Avatar、等待中的请求、延后的事件。它们跟根一起保存、一起迁移。
- **没有"无键 Actor"**：
  - 每个节点一份的状态，用单例；
  - 无状态的工作，用 `[Stateless]`；
  - 全局服务，用固定键，例如 `Matchmaker(int Region)`。

---

## 4. 消息

### 客户端收发的消息（Protocol）

```csharp
// Game.Protocol: data only, no actor types
[NinoType] public record struct EnterMap([To] long Map)    : IFromClient<bool>;   // a request to the map whose key is Map
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
```

- `IFromClient` 是客户端发的单向消息，`IFromClient<TReply>` 是客户端发的请求，`IToClient` 是推给客户端的消息。
- 含集合的消息是 `[NinoPooled]` 类（Nino 4 的规则），发送后自动归还，不会每次推送都分配。
- **消息里不写 Actor 类型**，所以客户端不知道服务端有 Player、Map、Room。
- **处理它的 Actor 类型由服务端决定**：处理它的 `[On<T>]` 在哪个 `[Handles<K>]` 类里，消息就发给哪个 Actor 类型。
  - 生成器据此在 Logic 里生成客户端路由表；
  - 一条客户端消息只能有一个处理 Actor 类型。
- **哪一个 Actor**：
  - 带 `[To]` 字段的消息，发给键等于这个字段的 Actor，例如 `EnterMap(7)` 发给 `Map(7)`。客户端只写了一个数，不知道它是哪种 Actor 的键；
  - 不带 `[To]` 的消息，发给这个客户端在该 Actor 类型上当前的 Presence（第 7 节）；
  - `[To]` 最多一个字段，类型必须和处理 Actor 类型的键形状一致（`Map(long Id)` 对 `long`；多字段的键对同样字段的结构），分析器检查。`[Handles<Client>]` 处理的消息不能有 `[To]`。
- **客户端消息直接到目标**：网关上的 `Client` 实体过滤后直接转发给目标 Actor，中间不经过 Player（第 7 节）。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Map>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;
[NinoType, From<Map>] public record struct Defeated(Map Where) : IMessageTo<Player>;
```

- **接收方写在接口上**：`IMessageTo<TTo>` 是单向消息，`IRequestTo<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。
  - `[From<Client>]` 指服务端的 `Client` 实体，不是远端的客户端。
  - 远端的客户端只能发 `IFromClient`。
- **消息里不写发送方的键**：接收方取 `Sender<K> sender` 参数，`sender.Key` 就是发送方。
- **方向由生成的方法和分析器保证**：
  - 每条消息生成 `Entity` 上的 `Send`/`Ask` 方法，参数类型是接收方的 Actor 类型，所以接收方写错编译不过；
  - `Entity` 不带 Actor 类型，所以发送方由调用处决定：`[Handles<K>]` 类和 `[UpdateInGroup<K>]` 系统里，K 不在消息的 `[From<…>]` 里，分析器报错；在别处（帮助类）调用时，运行时断言发送实体所属 Actor 的类型。
  - 这是帮助写对的手段，不是安全边界。安全边界在网关：只转发 `IFromClient`。
- **发送方是哪个 Actor**：发送实体所属的 Actor（它的域的根），见第 5 节的操作表。
- **客户端上下文自动带上**：处理客户端消息、`Left`、`PresenceLost` 时，发出的请求信封里带着这个客户端的连接信息（`ClientId`、所在节点、连接代数），和期限一样。接收方在处理这个请求时可以 `Join()`（第 7 节）。
  - 用户不接触连接信息本身。
- **`ICluster`**（HTTP、后台、测试）不属于任何 Actor，可以发任何服务器之间的消息，回复回到调用方。
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Client` 的节点，由连着它的那个处理。

### 本地事件（Server）

```csharp
[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;
```

- 本地事件只在一个域里传：发给本域的一个实体，由同一个 Actor 类型的 `[On<T>]` 处理。
- 声明在 Server 工程，带 `[NinoType]`：延后的事件随 Actor 迁移，也不能随 Logic 热更消失。
- 影响别的域用消息。

---

## 5. 逻辑

逻辑只有两种写法：
- 对消息和事件的反应：`[Handles<K>]` 的 `static partial class` 里的 `[On<T>]` 方法；
- 每 tick 的逻辑：`[UpdateInGroup<K>]` 的 EasyEcs 系统。

```csharp
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>] static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed) { … }
}
```

- `[Handles<K>]` 写一次，类里所有方法都属于 Actor 类型 K：客户端消息发给 K。
- 一个 Actor 类型可以有多个类，例如 `Maps`、`Moves`、`Combat`。

### `[On<T>]` 的 T

| T | 是什么 | 例子 |
|---|---|---|
| 服务器之间的消息 | 消息的 `IMessageTo<K>`、`IRequestTo<K, R>` 必须是类上的 K | `[On<GrantItem>]` |
| 客户端消息 | 发给类上的 K | `[On<EnterMap>]`、`[On<MoveTo>]` |
| `Incoming` | 只在 `[Handles<Client>]` 里：每条客户端消息转发前的过滤（第 7 节） | `[On<Incoming>]` |
| 生命周期消息 | 框架发的（第 6 节） | `[On<Activated>]`、`[On<Left>]`、`[On<PresenceLost>]`、`[On<Deactivating>]` |
| 回信 | 等别的 Actor 的结果（第 8 节） | `[On<Answer<GrantItem, Looting>>]` |
| 本地事件 | `IEvent`，由 `Send` 发起 | `[On<Strike>]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个处理 Actor 类型。

### 一个绑定规则：参数绑定到主体

每条消息、每个事件都有一个**主体**实体。`Entity` 参数就是主体，组件参数（`in`/`ref`）总是主体上的组件。

| 进来的是 | 主体 |
|---|---|
| 带 `[To]` 的客户端消息 | 目标 Actor 的根实体，不论这个客户端是否已经在上面有 Presence |
| 不带 `[To]` 的客户端消息，处理 Actor 类型不是 `Client` | 这个客户端在目标上的 Presence 实体，例如地图上它的 Avatar、Player 上它的 Presence |
| 客户端消息，`[Handles<Client>]` 处理 | `Client` 的根实体 |
| `Incoming` | `Client` 的根实体 |
| 本地事件 | 它被发给的实体：`caster.Send(new Strike(…), after: …)` 的 caster |
| 服务器之间的消息、`Activated`、`Deactivating` | 接收 Actor 的根实体，例如地图 |
| `Answer` | 发问的实体（第 8 节） |
| `Left` | 正在离开的 Presence 实体 |
| `PresenceLost` | 在 `[Handles<Client>]` 里是 `Client` 的根实体；在 `Via` 上是这个客户端在 `Via` 上的 Presence 实体（第 7 节） |

- **自己的键**：主体是根时，取 Actor 类型组件参数 `in Map map`。主体是 Presence 时，它一定是根的直接子实体（`Join()` 保证），取 `in ChildOf on` 参数，再 `world.GetEntity(on.Parent)` 读根，例如 `world.GetEntity(on.Parent).Map`。框架不另造 `Root` 成员：`ChildOf` 和生成的成员就够了。
- **例外：声明参数**：`[Claims<K>]` 的组件参数（例如 `in PlayerClaims who`）不绑定到主体，绑定到发这条客户端消息的客户端的声明。声明随信封带着（不超过 16 字节）。只能用在客户端消息的处理方法里（分析器检查）。

**别的实体在方法体里显式读写：**

```csharp
if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
ref var hp = ref target.Health;
if (me.IsStunned) return;
var grid = world.GetEntity(on.Parent).Grid;
```

- 首选的写法：发送方把接收方需要的数据放进消息，接收方少读别的实体。
- 用 EasyEcs 生成的成员：tag 是 `me.IsDead`，组件是 `target.Health`，可能没有的组件用 `TryGetX(out …)`。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Entity` | 主体 |
| 组件 `in`/`ref` | 主体上的组件。主体缺少任何一个时不调用处理方法，等同于 ECS 查询没匹配上；请求以 `Skipped` 失败。有索引的组件（包括 Actor 类型组件、`ChildOf`）只能是 `in` |
| 声明 `in TClaims` | 发消息的客户端的声明（上面的例外） |
| `ref DynamicBuffer<T>` | 主体上的缓冲 |
| `Sender<K>` | 发送方。消息有多个 `[From<…>]` 时写 `Sender<ActorId>` |
| `in ReplyTo<T>` | 请求的回复令牌，要晚些回复时取 |
| 单例 `in`/`ref` | 本 shard 的单例；`Time` 只能 `in` |
| `World` | ECS 操作 |

**引用不会失效**：处理方法里做了结构变更（建实体、`Ask`、`Send`、加 tag 等），参数仍然可用。
- 生成的跳板把组件参数复制成局部变量（`entity.TryGet<T>(out T)`）传入。
- `ref` 参数在方法**正常返回后**、**只在值变了时**用 `entity.TryGetRef<T>(out bool found)` 写回：没变就不写，追踪变更的组件不会被白白标成写过。组件如果已被处理方法删掉，就不写回，不会把它加回来。
- EasyEcs 如果提供单个实体的 job 执行（第 14 节 F1），跳板就不用复制了。
- `DynamicBuffer` 参数直接按 `ref` 传入：它是指向存储的句柄，跳板只在调用前用 `GetOrAdd<DynamicBuffer<T>>()` 确保存储已经建好，之后不写回。
- 方法体里自己取的 `ref`（`target.Health`）不能跨过这个实体的结构变更使用（DAS022）。
- **可能别名的两个可写引用，分析器报警**：例如取了 `ref Health` 参数，又写 `target.Health`，而 `target` 可能就是 `me`（打自己的技能）。跳板的写回会覆盖方法体里的那次写入。改成先判断 `target != me`，或者把参数改成 `in`。

### 操作

只有一个句柄：`Entity`。所有发送、推送、加入的操作都是 `Entity` 上的扩展方法（DaServer 提供，通过实体所在的 world 找到 shard），只写在这张表里：

| 写法 | 做什么 |
|---|---|
| `e.Send(ev)` | 本地事件，`ev` 是 `IEvent`，发给本域的实体 `e`；当前项返回后、下一项之前执行（即时链，最多 1,024 个）。返回 `Scheduled<T>` |
| `e.Send(ev, after: t)` | 同上，到期时刻 = 当前项的时间 + t（第 9 节）；`t` 为 `TimeSpan.Zero` 时同上一行 |
| `e.Send(to, msg)` | 服务器之间的单向消息，例如 `me.Send(new Player(id), new Defeated(map))`，进入对方的邮箱 |
| `e.Ask(to, req[, state])` | 请求，回信是 `Answer`，主体是 `e`（第 8 节） |
| `e.Reply(in token, value)` | 用回复令牌回复；令牌是 `default` 时什么也不做 |
| `e.Push(msg)` | `e` 是 Presence：推给它的客户端 |
| `e.Push(in group, msg)` | 推给一个 `ClientGroup`（第 9 节） |
| `root.Broadcast(msg)` | `root` 是 Actor 的根：推给这个 Actor 所有带 `Joined` 的 Presence |
| `e.SetGroups(groups)` | `e` 是 Presence：它订阅的 `ClientGroup` 换成这些 |
| `e.Join()` | 让 `e` 成为当前客户端在本 Actor 上的 Presence，返回 `PresenceId`（第 7 节） |
| `e.Leave()` | Presence `e` 离开（第 7 节） |
| `e.Deactivate()` | 停用 `e` 所属的 Actor（第 6 节） |
| `client.Disconnect()` | `client` 是 `Client` 的根：断开这个客户端 |

- **跨 Actor 的操作，发送方是 `e` 所属的 Actor**：处理方法里是正在执行的这一项的根；系统里，运行时沿 `ChildOf` 找到没有父的那个实体，就是根。
- `Send`/`Ask` 的方向检查见第 4 节。
- 延后的事件取消：`world.Cancel(in c.Strike)`；判断是否还在等：`world.IsAlive(c.Strike.Id)`（第 9 节）。
- 用户不判断"立即还是延后"：`after` 来自数据，0 也是合法值。
- 本地事件只能发给本域的实体，运行时断言。

### 每 tick：Actor 类型就是系统组

每 tick 的逻辑就是普通的 EasyEcs 系统，DaServer 不另造一套：

```csharp
// Game.Server: the actor type carries its tick rate and its group filter
[TickRate(30), With<In<Map>>] public readonly partial record struct Map(long Id) : IActor<Map>;

// Game.Server.Logic
[UpdateInGroup<Map>]
public class Steering : EcsSystem
{
    [ForEach(Parallel = true), With<Steered>] static void Steer(in Position p, in Destination d, in Speed s, ref Velocity v) { … }
}
```

- **Actor 类型就是它的系统组**：`IActor<T>` 继承 `ISystemGroup`（第 3 节），`[UpdateInGroup<Map>]` 就是"每个 Map tick 跑一次"。不另写组类型。
- **帧率写在 Actor 类型上**：`[TickRate(30)]`，不写为 20。没有系统的 Actor 类型没有 tick，`[TickRate]` 写了也报错。
  - 帧率写进 `wire.manifest`；改帧率就是改了 manifest，要滚动升级。
  - 一种服务器承载多个有 tick 的 Actor 类型时，各帧率必须整除其中最快的一个（例如 60、30、10），避免节拍抖动。
- **只处理自己的实体：组过滤 `[With<In<K>>]`**。
  - 写在 Actor 类型上的 `[With<T>]` 是 EasyEcs 的组过滤：加到这个组每个系统的每个 `[ForEach]` 方法上（EasyEcs `guide/systems.mdx` 的 "Group filters"）。
  - 查询缓存只存匹配的表，所以同一个 shard 上别的 Actor 类型的表，对这个组的系统不增加任何开销（`guide/systems.mdx` 的 "What it costs"，`why-fast.mdx` 的 "query cache"）。
  - `In<K>` 是框架的 tag（`public struct In<T> : IComponent where T : struct, IActor<T>;`），K 类型 Actor 的每个实体都带着：
    - 根：运行时激活时加上；
    - `[Prefab<K>]` 的 prefab：注册时加上，所以 `world.Instantiate(prefab)` 出来就带着；
    - `Join()`：没有时加上；
    - 用户自己 `CreateEntity` 的实体：自己加。
  - 有系统的 Actor 类型必须写 `[With<In<K>>]`，没写分析器报错（可一键修复）。框架的生成器替不了：EasyEcs 的生成器只读源码里的特性，看不到别的生成器补上的（同第 10 节的 `ITrackChanges`）。
- **怎么跑**：shard 在每个 tick 的有序阶段之后调用 `world.Update<Map>()`。一个 shard 上同一 Actor 类型的域共用 tick 边界，所以一次覆盖这个 shard 上这个 Actor 类型的所有域。
- **系统就是 EasyEcs 的系统**：`[ForEach]` 方法、批量方法（`Span<T>`）、`[Changed<T>]`、`[With<T>]`、`[UpdateAfter<T>]`、`UpdateInterval`，规则全按 EasyEcs 的文档。生成代码是 EasyEcs 自己的生成器写的。
- **默认单线程**。`[ForEach(Parallel = true)]` 只给重活用（EasyEcs 的建议）：shard 本来就已经占满各个核。
- **系统里的发送**：`Entity` 上的操作只能在非并行的系统里用，经 shard 发出；`Parallel = true` 的方法里用，分析器报错。
- `Time` 在系统里是这个 tick 的值，`Time.Now` 是 tick 时刻 t（第 9 节）。

### 其他

- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理；用户的系统用 `SystemErrorPolicy.Disable`（第 11 节）。
- **分发表、路由表和系统**在加载时只暂存，各个 shard 在两个 tick 之间换上（第 10 节）。表在暂存时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理方法抛出异常

**不回滚。** EasyEcs 的结构变更当场生效，没有命令缓冲（EasyEcs `decisions.mdx` D2），组件写入也是当场的。所以抛出时：
- 抛出之前做的修改都留着：建的实体、加删的组件和 tag、方法体里直接写的组件（`target.Health`）、已经发出的消息；
- `ref` 参数的改动不写回：跳板只在正常返回后写回（上面的"引用不会失效"）；
- 这条消息的请求以失败结束；它的即时链里还没执行的事件作废。

之后怎么办，看 Actor 类型上的 `[OnError]`：
- `Resume`（默认）：实体就是现在的样子，接着处理下一项；
- `Restart`：不写记录，不投递 `Left` 和 `Deactivating`，直接销毁整棵子树，之后从最后一次写成的记录重新激活。上次写记录之后的改动丢失；客户端那边按 Presence 丢失处理（第 7 节），加入消息自动重放。

- 分析器给建议：**先校验，最后做结构变更**。方法里结构变更之后还有 `throw` 或可能抛出的调用时，给出提示。第 12 节的 `Resolve` 就是这样写的。
- 一项的即时链超过 1,024 个，也按 `[OnError]` 处理。
- **`[On<Activated>]` 抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次。
  - 这样不会留下初始化了一半的 Actor。
- `Client` 抛出异常时，断开这个客户端。

---

## 6. 生命周期

生命周期事件是框架发的消息，用 `[On<T>]` 处理，和其他事件排在同一条 `(时间, 序号)` 线上。

| 消息 | 主体 | 何时 | 常见用途 |
|---|---|---|---|
| `Activated(Activation Origin)` | 根 | Actor 激活后的第一条邮件；`enum Activation { Created, Loaded, Migrated }` | 初始化 |
| `Left` | Presence 实体 | 在 `e.Leave()` 所在项的即时链里执行，同 `e.Send(ev)`；执行完框架销毁 Presence 实体 | 广播离开 |
| `PresenceLost(ActorId Target, Client Client)` | 见第 5 节的绑定表 | 一条路由失效（第 7 节） | 记录、提示；重新加入由框架做 |
| `Deactivating` | 根 | 停用前的最后一条邮件 | 收尾 |

`PresenceLost` 里的 `Client` 就是那个客户端的 Actor 类型值：Actor 类型就是它的地址。

**占位是 ECS 数据**：一个键在"还没有实体"或"正在写记录"时，它的根实体是一个占位：只有 Actor 类型组件、一个 tag 和等着的邮件（框架内部的 `DynamicBuffer<Mail>`）。占位上没有别的组件，也没有 `In<K>`，所以系统和处理方法都匹配不到它。

| 状态 | 根实体 | 怎么进入 | 发生什么 |
|---|---|---|---|
| 加载中 | 占位，带 `Loading` | 第一条消息到达 | 邮件留在占位上，异步读记录 |
| 激活 | 完整的根实体 | 记录到了，或者没有记录 | 按根 prefab 建根实体，接过 Actor 类型组件和等着的邮件，销毁占位，装上记录；邮箱里第一条是 `Activated`，后面才是等着的邮件 |
| 运行 | 完整的根实体 | — | 按 `(时间, 序号)` 执行消息和事件，再跑系统（第 1 节） |
| 停用 | 完整的根实体 → 占位，带 `Saving` | 空闲超过 `IdleTimeout`（有 Presence 时不算空闲），或者调用 `e.Deactivate()` | 依次：<br>1. 对每个 Presence `Leave`，各自的 `Left` 紧接着执行，通知对应的 `Client`，只发不等；<br>2. 投递 `Deactivating`，它之后不会再有别的邮件；<br>3. `SaveEntities` 写进缓冲，销毁整棵子树（待执行的延后事件随之消失），换成带 `Saving` 的占位，之后的邮件留在占位上；<br>4. 异步写记录；<br>5. 写成功后：占位上有邮件，就变成 `Loading`，读到的是刚写好的记录；没有就销毁占位 |

- 写记录失败就重试，绝不丢弃。
- `[OnError] Restart` 不走这张表的停用，见第 5 节。
- **激活的条件：**
  - 默认任何发给它的消息都能激活（Orleans 的虚拟 actor）；
  - Actor 类型可以写 `[ActivatedBy<EnterMap>]` 收紧，其他消息遇到不存在的 Actor，返回 `NoActor`；
  - 框架的控制消息（`Answer`、`JoinConfirmed` 等）不会激活 Actor，找不到就丢弃；
  - `Client` 只在客户端连接时激活。

---

## 7. 客户端与 Presence

### `Client` 实体

- **激活**：客户端连上一个承载 `Client` 的节点后，框架用令牌里的 `ClientId` 激活 `Client(ClientId)`。如果这个节点上已经有同一个 `ClientId` 的 `Client`，先停用旧的。
- **连接信息**：`(ClientId, 节点, 连接代数)`，框架内部使用。
  - 连接代数只用来判断是不是同一个连接：只比较相等，不比较大小。
- **声明**：令牌里的声明放进用户声明的组件，例如 `[Claims<Player>] PlayerClaims`。框架只负责解码令牌、把组件加到 `Client` 上，并随它转发的每条客户端消息带上（第 5 节的声明参数）。框架不认识任何业务字段。
- **位置固定**：`Client` 固定在连接所在的节点，不参与放置和重新平衡，也不迁移；连接断开，它就停用。
- **扩展**：用 `[Prefab<Client>(Root = true)]` 的 prefab 给它加组件，例如令牌桶；在 Logic 里写 `[Handles<Client>]` 类。
- **找不到它**：别的 Actor 不能用 `ClientId` 找到 `Client`。推送只经过 Presence，所以不需要位置目录。

### Presence

客户端在一个 Actor 上的存在，叫 **Presence**。

| 在哪 | 是什么 |
|---|---|
| 目标一侧 | 根的一个直接子实体，带 `Presence(PresenceId Id, 连接信息)` 和 `In<K>`，按 `PresenceId` 建值索引，例如地图上的 Avatar |
| `Client` 一侧 | 路由表 `DynamicBuffer<Route>` 里的一项：`Route(ActorId 目标, PresenceId, ActorId Via, 加入消息)`，框架内部使用 |

- **每个 Actor 类型最多一个 Presence**：一个客户端同一时刻最多在一张地图上、一个 Player 上。
- `Via` 是建立这条路由的一方：客户端消息里 `Join()` 时是 `Client` 自己；处理带着客户端上下文的服务器请求时 `Join()`，是发问的 Actor；挂接时是 `Client` 自己。
- 加入消息：`Via` 是 `Client` 时，路由项存下当初那条客户端消息（它的处理方法调用了 `Join()`）的字节，用来重放（见下面的"Presence 丢失"）。

**`PresenceId`** 是 64 位：高 32 位是创建它的节点 id，低 32 位是该节点上的计数。
- 节点 id 在每个进程启动时随机生成，所以全集群唯一。
- Presence 迁移后，`PresenceId` 照用，只是一个值，不代表它现在在哪个节点。

### 第一个 Presence：`[Claims<K>]`

```csharp
[Claims<Player>] public partial record struct PlayerClaims;   // the generator adds Player's key fields: (long Id)
```

- 声明组件写成 `partial`，不写字段：生成器按 K 的键补上同样的字段和 `IComponent`。每个 Actor 类型最多一个 `[Claims<K>]`。
- 框架做完整件事，用户不写代码：
  1. `Client` 激活后，向 `Player(Id)` 发框架内部的挂接请求；
  2. Player 一侧建这个客户端的 Presence 实体，**最新的连接胜出**：同一个 Player 上旧连接的 Presence 被 `Leave`，旧的 `Client` 断开；旧连接之后再来挂接（半开连接在重试），回复 `Superseded`，旧的 `Client` 直接断开；
  3. 失败（超时、`NoOwner`、`Busy`）时按带随机抖动的退避重试，超过次数就断开客户端。一个游戏节点宕机时，十万个客户端不会同时重试；
  4. Player 的 Presence 丢失（Player 重启）时重新挂接。
- 挂接好之前，客户端消息在 `Client` 上排队；挂接好后框架推一条 `Ready` 给客户端。
- Player 上的 Presence 和别的 Presence 一样：不带 `[To]`、由 Player 处理的客户端消息，主体就是它。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Client` 实体。
2. 先过过滤：`[Handles<Client>]` 类里的 `[On<Incoming>] static Verdict Allow(in Incoming m, ref RateLimit r)`。它逐条执行，返回放行或者拒绝的原因；没写就全部放行。
3. 按生成的路由表，得到处理 Actor 类型。
4. 得到目标：
   - 带 `[To]`：目标是这个 Actor 类型、键是这个字段的 Actor，没有就激活；
   - 不带 `[To]`：路由表里这个 Actor 类型的那一项；没有就拒绝，`NotJoined`。
5. 直接转发给目标，信封带着客户端上下文和声明；不带 `[To]` 的还带着 `PresenceId`，目标据此找到 Presence 实体，作为主体。
6. 被拒绝时，客户端收到原因：`NotJoined`、`Busy`、`NoActor`、`Filtered` 等。

### 加入：`e.Join()`

```csharp
[On<EnterMap>]                                                             // [To] Map: the subject is the map's root
static bool Enter(in EnterMap m, in PlayerClaims who, in Map map, World world)
{
    var key = new Avatar(map, new Player(who.Id));
    if (!world.AvatarIndex.TryFind(key, out var avatar))
        avatar = world.Instantiate(Prefabs.Avatar).SetAvatar(key);        // immediate (EasyEcs D2): an entity, or a throw
    avatar.Join();                                                         // this client's presence here, at once; the route follows
    return true;
}
```

**本地当场生效。** EasyEcs 的结构变更当场生效（EasyEcs `decisions.mdx` D2），所以 `world.Instantiate(…).SetAvatar(…)` 要么返回实体，要么抛出。`Join()` 也是当场的：
1. `e` 还不是根的子实体时，先成为根的直接子实体（`ChildOf`），没有 `In<K>` 时加上；
2. 加上 `Presence(新的 PresenceId, 连接信息)` 和确认期限 `Due`，返回 `PresenceId`。处理方法随后可以直接返回 `true`。
- 客户端来自正在执行的这一项的客户端上下文；没有上下文时，`Join()` 抛出 `NoClient`，请求失败。
- `e` 已经是别的连接的 Presence 时，新连接接管它（最新的胜出）：旧连接的 `Client` 删掉路由，实体退出旧连接的 `ClientGroup`、去掉 `Joined`，不触发 `Left`。

**在网关登记路由是异步的：**
1. 运行时把 `Route(目标, PresenceId, Via, 加入消息)` 发给上下文里的 `Client`，排在这条请求的回复前面。两者同一来源、同一目标，所以 `Client` 一定先写路由，再把回复交给客户端。
2. `Client` 写路由时，如果已经有同一个 Actor 类型、别的目标的路由，先删掉它，并通知旧目标：旧目标对那个 Presence 执行 `Leave`，收到 `Left`（**每个 Actor 类型一个 Presence**）。
3. `Client` 写好路由后，先向目标发 `JoinConfirmed(presenceId)`，之后才转发消息。两者同一来源、同一目标，所以确认一定先到。确认如果丢了，这个 Presence 上第一条转发来的消息也算确认。
4. 目标收到确认后：
   - 给 Presence 实体加 `Joined` 和 `NeedsSnapshot` 两个 tag，去掉确认期限；
   - 先给客户端推一条框架通知，带帧长、当前 tick 号和服务器时间（第 9 节），之后推送才开始。
   - 推送和 AOI 只认带 `Joined` 的 Presence；
   - `NeedsSnapshot` 表示"还没收到过完整快照"，推完快照后去掉它。迁移过来的 Presence（`Migrated`）和被新连接接管的，也重新带上 `NeedsSnapshot`，因为 `ClientGroup` 的订阅只在本节点。
5. **登记失败**（`Client` 已经不在了，路由没写成）：确认期限到了，框架对这个 Presence 执行 `Leave`，Actor 收到 `Left`。这和客户端加入后马上断线走的是同一条路。
6. **期限**：
   - 请求的期限取客户端请求剩下的时间；
   - 回复到达 `Client` 时，客户端的请求已经超时的，照样写路由（Presence 已经在目标上了），只丢弃回复。

**推送**：`e.Push(msg)`、`root.Broadcast(msg)`、`ClientGroup`（第 5 节的操作表）。都按 Presence 实体记下的节点直接发送。

**换地图**：客户端发 `EnterMap(8)`。地图 8 加入后，`Client` 写路由时删掉地图 7 的路由，地图 7 收到 `Left`（上面第 2 步）。两张地图是两个 Actor，它们之间没有先后：短时间内客户端可能同时出现在两张图上。需要严格先后的业务，用请求串起来。

### 离开

- **`e.Leave()`**：
  1. 当场退出所有 `ClientGroup`，通知 `Client` 删掉这条路由，客户端收到一条框架通知；
  2. 在当前项的即时链里执行 `[On<Left>]`，主体是这个 Presence 实体；
  3. 之后框架销毁 Presence 实体。
- **外部触发的离开**（`Client` 断线、被踢、被顶号、加入了同一 Actor 类型的另一个 Actor，或者节点离开视图）由框架作为邮件送到目标，在目标的处理过程中 `Leave`。
- **同一个键不会有两个 Presence 实体**：同一个 Player 再进同一张图时，`Enter` 用 `Join()` 接管已有的 Avatar。
- **通知丢失时的兜底：**
  - 两个节点之间的链路重连时，目标一侧按网关列出自己持有的 Presence，网关指出其中哪些已经不认识，目标对这些执行 `Leave`；
  - 某个节点离开成员视图时，各节点对记在该节点上的 Presence 执行 `Leave`；
  - 网络抖动、GC 停顿不会让 Presence 失效，只有节点离开视图才会。
- Presence 相关的控制消息不受 `Busy` 限制。

### Presence 丢失：自动重新加入

目标重启了（从记录恢复，或者 `[OnError] Restart`），旧的 Presence 实体已经没有了：
1. `Client` 在两种情况下删掉这条路由：转发时收到 `NotJoined`；或者看到目标所在的节点离开视图。即使客户端一直不发消息，也能发现。
2. 投递 `PresenceLost(目标, 客户端)`：
   - 给客户端 SDK；
   - 给 `[Handles<Client>]` 的 `[On<PresenceLost>]`，写了才投递；
   - `Via` 是别的 Actor 时，也给 `Via`（它当初替客户端发的请求，由它决定要不要再发）。
3. `Via` 是 `Client` 时，框架把存下的**加入消息重放**给目标，带着原来的声明。目标因此重新激活，加入消息的处理方法再调用一次 `Join()`，路由重新写好，客户端重新收到时间通知和完整快照。用户不写重新加入的代码。
   - 重放的回复丢弃：客户端没有在等它。
   - 目标暂时不可达（`NoOwner`、超时）时，按挂接同样的带抖动退避重试，超过次数放弃，只留下第 2 步的 `PresenceLost`。
- **限制**：
  - 只重放那一条加入消息，不重放之后的消息；之后的状态以目标的记录为准；
  - 加入消息的处理方法必须幂等：同一个客户端再来一次，结果一样（第 12 节的 `Enter` 先找已有的 Avatar）；
  - 挂接（`[Claims<K>]`）的 Presence 不靠重放，由框架重新挂接。

---

## 8. 等待别的 Actor 的结果

```csharp
// Game.Server
[NinoType, From<Map>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;
[NinoType] public record struct Looting(EntityId Drop, ReplyTo<bool> Reply) : IComponent;   // the Ask state

// Game.Server.Logic, in a [Handles<Map>] class
[On<PickUp>]                                                               // subject: the client's avatar
static void PickUp(in PickUp m, in PlayerClaims who, in ReplyTo<bool> reply, Entity me, World world)
{
    if (!Drops.TryFind(world, m.Drop, out var drop)) { me.Reply(in reply, false); return; }
    me.Ask(new Player(who.Id), new GrantItem(drop.Loot.Item), new Looting(drop.Id, reply));
}

[On<Answer<GrantItem, Looting>>]                                           // subject: the avatar that asked
static void Granted(in Answer<GrantItem, Looting> a, Entity me, World world)
{
    bool ok = a.Ok && a.Reply;
    if (ok && world.TryGetEntity(a.State.Drop, out var drop)) drop.Destroy();
    me.Reply(in a.State.Reply, ok);
}
```

- **`e.Ask(to, in message[, state])`** 创建一个等待实体，它是 `e` 的子实体，带：
  - `state`：一个普通组件，存在等待实体上，回信时作为 `a.State` 交回；不写时回信是 `Answer<TReq, None>`；
  - 调用 id：全集群唯一；
  - 期限：`Due`（第 9 节）；
  - 客户端上下文：处理客户端消息、`Left`、`PresenceLost` 时自动带上（第 4 节）。
  - 生成器为有状态和无状态各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(Actor 类型, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer<TRequest, TState>` 邮件**，按（请求类型, 状态类型）分发；主体是发问的实体 `e`。
  - `a.Ok`、`a.Status`、`a.Reply`（请求声明的回复类型）、`a.State`；
  - **等待实体在它的 `Answer` 处理方法返回后立即销毁。**
  - 发问的实体先被销毁了（例如 Avatar 离开），等待实体随它一起销毁，回信到达时丢弃。要在这种情况下也收尾，从根发问。
- **超时**：`Due` 到了，框架投递一个 `Ok = false` 的 `Answer`，和别的项一样按 `(时间, 序号)` 执行。
- **问多个目标**：对每个目标各 `Ask` 一次，每个目标一个 `Answer`。要汇总时，把计数放进发问实体上的组件。
- **`ReplyTo<T>` 是普通数据**：
  - 请求的处理方法取 `in ReplyTo<T> reply` 参数；`T` 就是请求声明的回复类型，`Reply` 在编译期检查类型；
  - 要先问别人再回复：把令牌放进 `Ask` 的状态（上例的 `Looting.Reply`），回信时 `me.Reply(in a.State.Reply, …)`；
  - 要晚些回复、不问别人：把令牌存进任何组件，之后 `Reply`；
  - `default` 的令牌，`Reply` 什么也不做（例如重放的加入消息，没有客户端在等）；
  - 同一个令牌只有第一次回复有效；一直没有回复，调用方按期限超时。
- 请求的处理方法必须返回回复，或者取 `ReplyTo<T>`（分析器检查）。
- 状态类型声明在 Server 工程，带 `[NinoType]`：等待实体随 Actor 迁移，Logic 热更时也不能被移除。

---

## 9. 时间与广播

### 时间

**一个时间类型：`TimeSpan`，从 UTC 纪元算起的领域时间。**
- 各节点用 NTP 对时，所以绝对的到期时刻在迁移和恢复之后仍然正确。
- `Time` 是 shard 的单例，由运行时写，用户只读：

  ```csharp
  public record struct Time(long Tick, TimeSpan DeltaTime, int TickRate, TimeSpan Now) : ISingletonComponent;
  ```

- **`Now` 是正在执行的那一项的时间**：处理消息时是它进入 shard 的时刻，执行延后事件时是它的到期时刻，系统里是 tick 时刻 t。
- 系统运行期间，`Tick`、`DeltaTime`、`TickRate` 是这个 Actor 类型的值；没有 tick 的 Actor 类型，它们是 0。
- **`after` 从当前项的时间算起**：到期时刻 = `Time.Now` + after，存精确值，不存 tick 号。它在 tick 窗口所在的那一帧执行（第 1 节），从不提前。
- 数据里写秒或毫秒，例如 `Skills.Windup(id)` 返回 `TimeSpan`。
- 为什么用时间不用 tick：改帧率不能改技能的手感。Minecraft 的 `/tick rate` 改了帧率，按帧写的一切都跟着变快变慢；Photon Quantum 的 `FrameTimer.FromSeconds` 让用户写秒，内部存帧。
- 没有 tick 的域：到了时间就执行。
- **帧率每个构建固定**：写在 Actor 类型上，进 `wire.manifest`（第 10 节）。
- **客户端的时间**：Presence 确认后，目标先推一条框架通知，带帧长、当前 tick 号和服务器时间（第 7 节）；之后的第一份快照对齐到这个 tick。加入另一个 Actor 类型（可能是另一个帧率）的 Actor 时，再推一次。

### 一个计时机制：`Due`

延后的事件和框架的期限（请求超时、加入确认期限、挂接和重放的退避）是同一种东西：**带 `Due` 的实体**。

```csharp
// DaServer: an ordered value index; Key => this, compared by (At, Seq)
public partial record struct Due(TimeSpan At, long Seq) : IComponent, IIndexed<Due>, IComparable<Due>
{
    public static readonly Due Min = new(TimeSpan.MinValue, long.MinValue);
}
```

| 谁带 `Due` | 是什么 |
|---|---|
| 延后的事件 | 发起实体的子实体：`ChildOf(发起者)`、`Due`、事件本身 |
| 请求的期限 | 等待实体 |
| 加入确认期限 | 确认前的 Presence 实体 |
| 挂接、重放的退避 | `Client` 的一个子实体 |

- 每个有序阶段读 `world.DueIndex.Range(Due.Min, new Due(t, long.MaxValue))`：EasyEcs 的有序值索引，已经按 `(时间, 序号)` 排好，再和邮箱快照按同一个顺序归并。
- 执行中销毁实体是允许的；执行中新加的、时间不晚于 t 的项，同一趟里按顺序取到。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- 周期性的逻辑写进系统；稀疏的周期（例如每秒跳一次伤害）在事件处理方法里再 `Send(…, after:)` 下一次。

**`Scheduled<T>` 是普通数据**：`record struct Scheduled<T>(EntityId Id) where T : struct, IEvent`，指向那个子实体，可以存进组件。
- 取消：`world.Cancel(in c.Strike)`，就是 `TryGetEntity` 加 `Destroy`；
- 还在等吗：`world.IsAlive(c.Strike.Id)`，执行或取消后变成 `false`。

### 广播

- **`ClientGroup`**：一个值（`ClientGroup(long Key)`），按键区分。
  - Presence 用 `e.SetGroups(groups)` 订阅；`Leave` 时自动退出；
  - `e.Push(in group, msg)` 一次序列化，每个有成员的网关节点收到一帧，由网关扇出；
  - 成员只有带 `Joined` 的 Presence。
- **整个 Actor**：`root.Broadcast(msg)`，推给它所有带 `Joined` 的 Presence。
- **小组**（公会、队伍）：一个固定键的 Actor，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热更、升级（EasyEcs 4.4）

组件类型的集合都用 EasyEcs 的 `ComponentSet`（`reference/componentset.mdx`）：注册时建好一次，存在 `static readonly` 字段里，框架按 `Type` 加入它在注册时才知道的类型。

### 记录

一份记录就是一次 `SaveEntities`，包括：
- 根实体，放在第一个；
- 带至少一个 `[Persist]` 组件的子孙；
- 这些子孙到根的路径上的实体。

保存用的组件集合：`persist = ComponentSet.Create().With(typeof(每个 [Persist] 组件)).With<ChildOf>()`。

### 恢复

- 记录头里存着实体数 n。
- 按根 prefab 建好根实体放在 `into[0]`，其余 n−1 个是空实体，然后一次 `LoadEntitiesInto(record, into)`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会改写成新的 id；指向这组以外的 `EntityId` 变成 `Null`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里，包括 `In<K>`（它是 tag，不占列）。所以 `persist` 集合也带上 `In<K>`。
- 加载完，`ChangeSystem` 把它的基准重置到这一刻。EasyEcs 会把加载的每一行都算作写入，不重置的话，每次激活都会白写一次（第 14 节 F3）。

### 只写改过的

- **`[Persist]` 组件自己声明 `ITrackChanges`**：`record struct Grid(…) : IComponent, ITrackChanges`。EasyEcs 的生成器只看源码里声明的接口，看不到我们的生成器补上的；不声明的话，生成的成员会把读当成写。分析器检查：`[Persist]` 必须声明 `ITrackChanges`。
- `ChangeSystem` 同时看 `[Changed<T>]` 和 `[Removed<T>]`：持久化组件被写或者被删，都给根实体标 `Unsaved`。
- 带持久化组件的子孙，在同一趟里加上框架的清理组件 `Persisted(EntityId Root)`。新建和 `LoadEntitiesInto` 都算写入，所以加载后它会自动补上。
- 子孙被销毁（例如物品用掉了）时，清理系统按 `Root` 给根实体标 `Unsaved`，然后移除自己。所以删除也会写进记录。

### 序列化规则（Nino）

| 数据 | 要求 |
|---|---|
| `[Persist]` 组件 | `[NinoType]`；改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式 |
| `Ask` 的状态类型、本地事件 | `[NinoType]` |
| 其他非托管组件 | 迁移时按原始字节搬运，不用标 |
| 托管组件 | 要有 Nino 格式化器（分析器检查） |

记录里的类型按 `TypeId` 存。

### 迁移

- 保存整棵子树，组件集合是注册时建好的 `migrate = ComponentSet.Create().With(typeof(每个不带 [Transient] 的组件))`：默认都带，标了 `[Transient]` 的除外。
- 等待中的请求、Presence 实体、待执行的延后事件随 Actor 一起迁移。
  - `Due` 是绝对时间，到达后按新 shard 的 tick 边界重新归入 tick 窗口；已经过期的在第一个 tick 执行。迁移本身花的时间不会被算进去两次。
- 停用写记录时不保存待执行的延后事件：需要跨停用保留的状态，写进 `[Persist]` 组件。
- 到达后的第一条邮件是 `Activated(Activation.Migrated)`。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。

### 热更

- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有所有 `[On<T>]` 方法、分发跳板、分发表、路由表和系统。
- 每个 shard 在两个 tick 之间：
  1. `world.RemoveTypes(旧上下文)`：连同旧的系统一起移除；
  2. 换上新的分发表和路由表，`world.AddSystem<T>()` 加上新的系统。
- 所有 shard 都换完之后，旧上下文才卸载。
- 新版本如果改了某条客户端消息的处理 Actor 类型，或者让某个 Actor 类型从没有 tick 变成有 tick（反过来也一样），`wire.manifest` 比对后拒绝这次热更。
- 组件、消息、事件、状态类型、Actor 类型（包括帧率和组过滤）不热更，新增或修改它们要走滚动升级。待执行的延后事件因此在热更前后都有处理方法。

### `wire.manifest`

- 由 Server 和 Logic 两个工程一起写出，Logic 的行在单独的一段。
- Server 的一段：Actor 类型和承载它的服务器类型、声明挂接的 Actor 类型、每条消息的接收方、发送方、回复类型、事件、状态类型、持久化组件、推送。
- Logic 的一段：客户端消息的路由（带 `[To]` 的写明字段）、`Answer` 的处理、有 tick 的 Actor 类型和帧率（帧率来自 Server 的 `[TickRate]`，有没有 tick 来自 Logic 里有没有系统）。
- AppHost 只读 `actor` 和 `hosted-by`。

### 滚动升级

- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点 id 在每个进程启动时随机生成，所以同名的节点重启后，不会被当成原来那个。
- 新旧版本的帧率可以不同：延后事件存的是绝对时间。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。Actor 在新节点从记录激活，最多丢一个检查点间隔的改动。`Client` 看到节点离开视图，投递 `PresenceLost`，重放加入消息（第 7 节） |
| 网关宕机 | 客户端断开，带令牌重连，得到新的 `Client`。各目标看到那个节点离开视图，执行 `Leave` |
| 半开连接时客户端已经连到别处 | 新连接的挂接胜出，旧的 `Client` 被断开；旧连接再来挂接会收到 `Superseded` |
| 挂接失败 | 框架带抖动退避重试，超过次数断开 |
| `Join()` 时没有客户端上下文 | 抛出 `NoClient`，请求失败 |
| 加入后路由没写成（客户端已断开） | 确认期限到，`Leave`，目标收到 `Left` |
| 重放加入消息失败 | 带抖动退避重试，超过次数放弃；客户端只收到 `PresenceLost` |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活 |
| 主体缺少组件参数 | 不调用处理方法；请求以 `Skipped` 失败 |
| 拿着回复令牌的 Actor 在回复前停用 | 调用方按期限超时；之后到达的回信丢弃 |
| 发问的实体在回信前被销毁 | 等待实体一起销毁，回信丢弃 |
| 停用时写记录失败 | 重试；这期间到达的消息留在 `Saving` 占位上，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；目标节点宕机时，在途的单向消息可能丢失 |
| 邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回绝；Presence 的控制消息不受限 |
| 客户端读得太慢 | 断开（ADR-035） |
| 离开通知丢失 | 链路重连时核对，或者节点离开视图时，补做 `Leave` |
| `[On<T>]` 抛出异常 | 不回滚，见第 5 节：请求失败，即时链里还没执行的事件作废；`Resume` 照现状继续，`Restart` 从最后的记录重新激活；`Client` 直接断开 |
| 一项的即时链超过 1,024 个 | 按 `[OnError]` 处理 |
| 系统抛出异常 | `SystemErrorPolicy.Disable`：这个系统停用，异常留在 `system.Error`，健康检查报告；下一次热更换上新的系统 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 处理方法重新校验后作废 |

---

## 12. 完整例子：连接、进地图、移动、放技能、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct EnterMap([To] long Map) : IFromClient<bool>;         // goes straight to that map
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took; to its current map
[NinoType] public record struct CastSkill(int Skill, long Target) : IFromClient;      // Target: an id from Nearby
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
[NinoType] public record struct NearbyEvent(long Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: a PresenceId or an npc's id
```

```csharp
// ── Game.Server: actor types, servers, events ─────────────────────────────────
public readonly partial record struct Player(long Id) : IActor<Player>;               // joined at login through [Claims<Player>]
[TickRate(30), With<In<Map>>]                                                          // 30 ticks a second; its systems see only map entities
public readonly partial record struct Map(long Id) : IActor<Map>;

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Client>] public struct GatewayServer : IServer;

[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;           // a cast's resolution, after the windup

// ── Game.Server: data ─────────────────────────────────────────────────────────
[Claims<Player>] public partial record struct PlayerClaims;                             // decoded from the token; fields from Player's key
[NinoType, Persist] public record struct Grid(int Size, int CellSize) : IComponent, ITrackChanges;   // on a map
public partial record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar>;            // Key => this is generated
public partial record struct Cell(EntityId Map, int X, int Y) : IComponent, IIndexed<Cell>, ITrackChanges;   // AOI cell, by the map's entity
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
public record struct Casting(Scheduled<Strike> Strike) : IComponent;                    // "is casting" while Strike is pending
public record struct Shout(Cell Cell, NearbyEvent Event);                               // a map's outbox element

public static class Prefabs
{
    [Prefab<Map>(Root = true)] public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetBuffer<Shout>();
    [Prefab<Map>] public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetVelocity(default)
        .SetNetSync(default).SetSpeed(6).SetHealth(100).SetVisible();      // a player's position comes from its steps
    [Prefab<Map>] public static readonly Prefab Npc = new Prefab().SetPosition(default).SetVelocity(default)
        .SetNetSync(default).SetDestination(default).SetSpeed(3).SetHealth(100).SetSteered().SetVisible();
}
```

```csharp
// ── Game.Server.Logic: a map's membership ─────────────────────────────────────
[Handles<Map>]
public static partial class Maps
{
    [On<EnterMap>]                                                         // [To] Map: the subject is the map's root
    static bool Enter(in EnterMap m, in PlayerClaims who, in Map map, World world)
    {
        var key = new Avatar(map, new Player(who.Id));
        if (!world.AvatarIndex.TryFind(key, out var avatar))              // idempotent: a reconnect or a replay finds it
            avatar = world.Instantiate(Prefabs.Avatar).SetAvatar(key);    // immediate (EasyEcs D2): an entity, or a throw
        avatar.Join();                                                     // a child of the map, this client's presence; the route follows
        return true;                                                       // the client's previous map, if any, gets Left
    }

    [On<Left>]                                                             // subject: the avatar; then the framework destroys it
    static void Gone(in Left l, in Presence presence, in Cell cell, in ChildOf on, World world) =>
        world.GetEntity(on.Parent).Get<DynamicBuffer<Shout>>()
            .Add(new Shout(cell, new NearbyEvent(presence.Id.Value, NearbyKind.Disappear, default, default)));
}

// ── Game.Server.Logic: steps a client took ────────────────────────────────────
[Handles<Map>]
public static partial class Moves
{
    [On<MoveTo>]                                                           // subject: this client's avatar; applied now, later items see it
    static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed)
    {
        if (me.IsDead) return;
        if (!Steps.IsValid(p.Value, m.At, speed.Value)) { me.Push(new SnapBack(p.Value)); return; }
        p.Value = m.At;
        v.Value = m.Velocity;
    }
}

// ── Game.Server.Logic: combat ─────────────────────────────────────────────────
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>]                                                        // subject: the caster's avatar
    static void Cast(in CastSkill m, Entity me, in ChildOf on, World world)
    {
        if (me.HasCasting || me.IsStunned || me.IsDead) return;           // one cast at a time
        if (!Targets.TryFind(world, on.Parent, m.Target, out var target)) return;   // a PresenceId or an npc id -> the entity on this map
        me.SetCasting(me.Send(new Strike(m.Skill, target.Id), after: Skills.Windup(m.Skill)));   // Zero resolves right after this handler
    }

    [On<Strike>]                                                           // subject: the caster; at its due time, reads the target as it is now
    static void Resolve(in Strike s, Entity me, in Position at, World world)
    {
        me.RemoveCasting();
        if (me.IsDead || me.IsStunned) return;                             // died or got stunned during the windup
        if (!world.TryGetEntity(s.Target, out var target) || target.IsDead) return;
        if (!Skills.InRange(s.Skill, at.Value, target.Position.Value)) return;   // out of range: dodged
        ref var hp = ref target.Health;
        hp.Value -= Skills.Damage(s.Skill);
        if (hp.Value <= 0) target.SetDead();                               // structural change last: hp is not used after it
    }
}

// ── Game.Server.Logic: every Map tick, after the ordered phase ───────────────
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
    [ForEach, Changed<Position>, With<Visible>]                            // moved since the last tick: which cell now
    static void Assign(Entity e, in Position p, in ChildOf on, World world)
    {
        var cell = Aoi.CellOf(on.Parent, p.Value, world.GetEntity(on.Parent).Grid.CellSize);
        if (!e.TryGetCell(out var old) || old != cell) e.Set(cell);       // only a crossing writes Cell
    }
}

[UpdateInGroup<Map>, UpdateAfter<CellAssign>]
public class Regroup : EcsSystem
{
    [ForEach, Changed<Cell>, With<Joined>]                                 // a joined avatar crossed into another cell
    static void Crossed(Entity avatar, in Cell cell) => avatar.SetGroups(Aoi.Around(in cell));   // its 3×3 ClientGroups

    [ForEach, With<NeedsSnapshot>]                                         // just joined, moved in or taken over: subscribe now
    static void Fresh(Entity avatar, in Cell cell) => avatar.SetGroups(Aoi.Around(in cell));
}

[UpdateInGroup<Map>, UpdateAfter<Regroup>]
public class Snapshot : EcsSystem
{
    [ForEach, With<NeedsSnapshot>]                                         // a presence not yet in sync
    static void Push(Entity avatar, in Cell cell, World world)
    {
        avatar.Push(Aoi.Snapshot(world, in cell));                         // a pooled Nearby of the 3×3 cells, from world.CellIndex
        avatar.RemoveNeedsSnapshot();
    }
}

[UpdateInGroup<Map>, UpdateAfter<Snapshot>]
public class Diff : EcsSystem
{
    [ForEach, Changed<Velocity>, With<Visible>]                            // velocity written: tell the cell if it differs from what was sent
    static void Moved(Entity e, in Velocity v, in Position p, in Cell cell, ref NetSync sync, in ChildOf on, World world)
    {
        if (v == sync.Sent) return;
        sync = new NetSync(v);
        world.GetEntity(on.Parent).Get<DynamicBuffer<Shout>>().Add(new Shout(cell, Aoi.Moved(e, in p, in v)));
    }
}

[UpdateInGroup<Map>, UpdateAfter<Diff>]
public class Flush : EcsSystem
{
    [ForEach]                                                              // once per map: only map roots have an outbox
    static void Publish(Entity map, ref DynamicBuffer<Shout> outbox)
    {
        Aoi.Publish(map, outbox.AsReadOnlySpan());                         // one Nearby per cell with news: map.Push(Aoi.Group(cell), nearby)
        outbox.Clear();
    }
}
```

被打断的前摇：眩晕的处理方法里 `if (me.TryGetCasting(out var c)) { world.Cancel(in c.Strike); me.RemoveCasting(); }`。

这个例子没有 Player 的逻辑：客户端在登录时由 `[Claims<Player>]` 加入 Player，进地图的 `EnterMap` 直接发给地图，不经过 Player。

AOI 的状态全在组件里：格子是 `Cell`，上次发出的速度是 `NetSync`，没收到过快照是 `NeedsSnapshot`，发件箱是地图上的 `DynamicBuffer<Shout>`。`Aoi` 是用户写的无状态帮助类。每个系统只做一件事：
1. `CellAssign`：位置被写过的实体，算出格子；跨格才写 `Cell`。
2. `Regroup`：`Cell` 变了的已加入的 Avatar，以及刚加入的 Avatar，换它订阅的 3×3 `ClientGroup`。
3. `Snapshot`：带 `NeedsSnapshot` 的 Avatar，推一份它看得到的完整快照，去掉 `NeedsSnapshot`。
4. `Diff`：速度被写过、并且和 `NetSync` 不同的实体，写一条 `Moved` 进地图的发件箱。
5. `Flush`：每张地图按格子合并发件箱，每个有内容的格子发一帧 `Nearby`，然后清空。

所有系统都只看带 `In<Map>` 的实体（`Map` 上的组过滤，第 5 节）。除了 `Steering`，这些系统都发消息或做结构变更，所以都是单线程的。

事件里的 `Id`：Avatar 用 `PresenceId`，NPC 用一个不会与之相撞的编号（例如最高位置 1）。`Targets.TryFind` 按同样的规则查回实体。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: Key => this for every partial IIndexed<T> record struct whose key is itself
partial record struct Map    { public Map Key => this; }
partial record struct Avatar { public Avatar Key => this; }
partial record struct Cell   { public Cell Key => this; }

// [Claims<Player>]: the fields of Player's key
partial record struct PlayerClaims(long Id) : IComponent;

file static class MapActor
{
    [ModuleInitializer]
    internal static void Register()
    {
        Prefabs.Avatar.Set(new In<Map>());                                 // [Prefab<Map>]: every map entity carries In<Map>
        Prefabs.Npc.Set(new In<Map>());
        Actors.Register<Map>(
            find: static (World w, in Map key, out Entity e) => w.MapIndex.TryFind(key, out e),   // EasyEcs's value index
            spawn: static (World w) => w.Instantiate(Prefabs.Map));       // the runtime sets Map and In<Map> on the root
    }
}

file static class Registration
{
    [ModuleInitializer]
    internal static void Register() =>
        Claims.Register<PlayerClaims, Player>(static (in PlayerClaims c) => new Player(c.Id));
}
```

```csharp
// <auto-generated/> Game.Server: typed sends on Entity, one per message and shape (§8's GrantItem; this example has none)
public static class GrantItemSends
{
    public static void Ask(this Entity from, Player to, in GrantItem message) =>              // answer: Answer<GrantItem, None>
        Runtime.Ask(from, in to, in message, None.Value);

    public static void Ask<TState>(this Entity from, Player to, in GrantItem message, in TState state)
        where TState : struct, IComponent =>
        Runtime.Ask(from, in to, in message, in state);                    // the client's context comes from the item being run
}
// The receiver is in the signature; the sender is checked where it is called: inside [Handles<K>] or [UpdateInGroup<K>],
// K must be in [From<…>] (here Map), else the analyzer reports it; elsewhere the runtime asserts the root's actor type.
// Entity.Send<T>(in T, TimeSpan after = default) where T : struct, IEvent is the framework's own generic method.
```

```csharp
// <auto-generated/> Game.Server.Logic: [On<T>] trampolines inside each partial class (they can reach private handlers)
// root = the domain's root; subject = the entity the binding rule picks
partial class Maps
{
    internal static void On_Enter(World world, Entity root, Entity subject, in Mail mail, ref Outcome outcome)
    {
        var m = mail.Read<EnterMap>();                                     // [To]: subject == root
        var who = mail.Claims<PlayerClaims>();                             // the sending client's claims, not the subject's
        if (!subject.TryGet<Map>(out var map)) { outcome.Skipped(); return; }
        outcome.Reply(Enter(in m, in who, in map, world));                 // Join() queued the Route ahead of this reply
    }

    internal static void On_Gone(World world, Entity root, Entity subject, in Mail mail, ref Outcome outcome)
    {
        var l = mail.Read<Left>();
        if (!subject.TryGet<Presence>(out var presence) || !subject.TryGet<Cell>(out var cell) || !subject.TryGet<ChildOf>(out var on))
        { outcome.Skipped(); return; }
        Gone(in l, in presence, in cell, in on, world);
    }
}

partial class Moves
{
    internal static void On_Move(World world, Entity root, Entity subject, in Mail mail, ref Outcome outcome)
    {
        var m = mail.Read<MoveTo>();
        if (!subject.TryGet<Position>(out var p) || !subject.TryGet<Velocity>(out var v) || !subject.TryGet<Speed>(out var speed))
        { outcome.Skipped(); return; }                                     // the subject lacks a component: not called
        var p0 = p;
        var v0 = v;                                                        // copies in: they survive structural changes
        Move(in m, subject, ref p, ref v, in speed);                       // a throw skips the write-back below
        if (p != p0) { ref var wp = ref subject.TryGetRef<Position>(out var hasP); if (hasP) wp = p; }   // a change only: no false stamp
        if (v != v0) { ref var wv = ref subject.TryGetRef<Velocity>(out var hasV); if (hasV) wv = v; }   // and only if it still exists
    }
}

partial class Combat
{
    internal static void On_Resolve(World world, Entity root, Entity subject, in Mail mail, ref Outcome outcome)
    {
        var s = mail.Read<Strike>();                                       // subject: the entity the event was sent to
        if (!subject.TryGet<Position>(out var at)) { outcome.Skipped(); return; }
        Resolve(in s, subject, in at, world);                              // RemoveCasting is a structural change: locals stay valid
    }
}
```

```csharp
// <auto-generated/> Game.Server.Logic: dispatch per actor type, client routes, systems; staged on load, adopted between ticks
file static class Dispatch_Map
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Map>(static table => table
        .Client<EnterMap>(Maps.On_Enter)                                   // [To]: subject is the root; may Join
        .Lifecycle<Left>(Maps.On_Gone)                                     // run in the immediate chain of the Leave
        .Client<MoveTo>(Moves.On_Move)                                     // subject: the presence
        .Client<CastSkill>(Combat.On_Cast)
        .Event<Strike>(Combat.On_Resolve)
        .Systems(static world =>                                           // the Map group; EasyEcs orders them by [UpdateAfter]
        {
            world.AddSystem<Steering>();
            world.AddSystem<Movement>();
            world.AddSystem<CellAssign>();
            world.AddSystem<Regroup>();
            world.AddSystem<Snapshot>();
            world.AddSystem<Diff>();
            world.AddSystem<Flush>();
        }));                                                               // the shard calls world.Update<Map>() at 30 Hz
}
// No Dispatch_Player: Player has no handlers in this example, so it has no ticks either.

file static class ClientRoutes
{
    [ModuleInitializer]
    internal static void Register() => Routes.Stage(static table => table
        .Route<EnterMap, Map>(to: static (in EnterMap m) => new Map(m.Map))    // [To] long Map -> Map(long Id)
        .Route<MoveTo, Map>()                                                   // no [To]: this client's map presence
        .Route<CastSkill, Map>());
}
```

```csharp
// <auto-generated/> AppHost, from the actor / hosted-by lines of Game.Server's wire.manifest
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
# ── Game.Server ──
actor   Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer
actor   Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer
actor   DaServer.Client     id 3E0F7A21  key int64   hosted-by GatewayServer
claims  Game.Server.PlayerClaims  -> Player
event   Game.Server.Strike
persist Game.Server.Grid
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
# ── Game.Server.Logic ──
client  Game.Protocol.EnterMap   -> Map  to Map  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
tick    Game.Server.Map  30Hz
```

### 运行时的路径

```
① 客户端连 gate-0      框架在 gate-0 激活 Client(c1)，加上 PlayerClaims；第一条邮件是 Activated
② 挂接                 [Claims<Player>]：框架向 Player 42@game-1 挂接；Player 建 c1 的 Presence（最新的胜出），回复 P1
                      Client 写路由 (Player 42, P1, Via 自己)，发 JoinConfirmed(P1)；推 Ready，放出排队的消息
③ EnterMap(7)         Client(c1)@gate-0：过滤 → 路由表：带 [To]，目标 Map 7@map-0；直接转发，信封带着 c1 的上下文和声明
④ Enter               Map 7 的有序阶段：主体是根；找不到 Avatar 就 Instantiate（当场）；Join() 当场得到 P2，返回 true
                      运行时先把 Route(Map 7, P2, Via c1, EnterMap(7)) 发给 Client(c1)，再发回复
⑤ Client(c1)          写路由；原来有 Map 3 的路由就删掉，Map 3 收到 Left；把 true 交给客户端；向 Map 7 发 JoinConfirmed(P2)
⑥ JoinConfirmed(P2)   Map 7：Avatar 带上 Joined 和 NeedsSnapshot；推时间通知（33 ms、tick 号、服务器时间）；下一个 tick 的 Regroup、Snapshot
⑦ MoveTo              不带 [To]：Client(c1) → c1 的 Map 路由 → Map 7 的邮箱；下一个 tick 的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Send(Strike, after: 前摇)，带着 Due 进有序索引；Casting 拒绝第二次施法
⑨ Strike 到期         tick 窗口所在那一帧的有序阶段，和消息按 (时间, 序号) 归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ 每 33 ms 一个 tick   有序阶段 → world.Update<Map>()：Steering（多线程）、Movement、CellAssign、Regroup、Snapshot、Diff、Flush
                      只遍历带 In<Map> 的表；Nearby → 有观察者的网关 → 各个连接
⑪ map-0 宕机          Map 7 在 map-1 从记录激活（Avatar 不在记录里）；Client(c1) 看到 map-0 离开视图：删路由，PresenceLost 给 SDK，
                      重放 EnterMap(7) → Enter 找不到 Avatar 就新建，Join → 同 ④–⑥
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：接收方不对没有对应的方法；`[Handles<K>]` 类或 `[UpdateInGroup<K>]` 系统里发送，K 不在消息的 `[From<…>]` 里。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个 Actor 类型处理。
- `[On<Incoming>]` 不在 `[Handles<Client>]` 里。
- 一个请求有两个处理方法；`Answer` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`；`Reply` 的值类型和令牌的 `T` 不同。
- `Sender<K>` 的 K 不是这条消息声明的发送方。
- 客户端消息有多个 `[To]` 字段；`[To]` 字段的类型和处理 Actor 类型的键形状不一致；`[Handles<Client>]` 处理的消息带 `[To]`。
- 声明参数（`[Claims<K>]` 的组件）用在客户端消息以外的处理方法里。
- 用户代码写 Actor 类型组件，或者把 Actor 类型组件放进 prefab、`CreateEntity`。
- 有索引的组件作为 `ref` 参数。
- 方法体里的 `ref` 跨过这个实体的结构变更使用（DAS022）。
- 两个可写引用可能指向同一个组件：`ref T` 参数加上对另一个实体的写，而那个实体可能就是主体。
- `Parallel = true` 的 `[ForEach]` 方法里调用 `Entity` 上的 DaServer 操作（第 5 节的操作表）。结构变更、并行、有索引的组件，EasyEcs 另有 EECS010、EECS013、EECS015。
- 有系统的 Actor 类型没有写 `[With<In<K>>]`（可一键修复）；没有系统的 Actor 类型写了 `[TickRate]`；一种服务器上各帧率不能都整除最快的那个。
- 把 `IEvent` 发给别的 Actor（`e.Send(to, ev)`），或者把服务器之间的消息当本地事件发（`e.Send(msg)`）。
- 事件类型不是 `[NinoType]`，或者没有实现 `IEvent`。
- `[Claims<K>]` 的组件不是 `partial`，或者自己写了字段；一个 Actor 类型有两个 `[Claims<K>]`。
- 一个 Actor 类型有两个 `[Prefab<K>(Root = true)]`。
- Protocol 里出现 Actor 类型或者服务器之间的消息；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- Actor 类型的键超过 16 字节，或者不是非托管类型。
- `[Persist]` 组件没有声明 `ITrackChanges`，或者不是 `[NinoType]`；`Ask` 的状态类型不是 `[NinoType]`。
- 组件、事件、状态类型、Actor 类型声明在 Logic。
- 要迁移的托管组件没有 Nino 格式化器。

**提示**（不报错）：处理方法里结构变更之后还有 `throw` 或可能抛出的调用：先校验，最后做结构变更（第 5 节）。

**启动时检查**：配置的服务器类型不存在，或者和 `wire.manifest` 对不上，节点启动失败。

**删掉的东西**：
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调（现在是 `Activated` 的 `Activation` 值）、`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`（并入 `[On<Incoming>]`）、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded、自研的记录格式、`ActorKey`；
- `[Receive]`、`[Receive<K>]`、用 `EcsSystem` 处理消息、`[Every]` 系统组、`self.After`、作为业务入口的 `TimerSystem`/`MailSystem`；
- `[Frame(ms)]`、`Raise`、`SendAfter`、`[On<T, K>]`、`[Link]`、`[Of(…)]`、`[Pending]`、`Maybe<T>`、`AskFor`、`world.Route`、`routeFrom:`、用户写的 `Attach`、空间区域；
- `[On<Tick>]`、`[On<Tick>(Serial)]`、`Tick` 事件、`[After<TClass>]`、生成的 job 结构、配置里的帧率和 `.WithTickRate`；
- 时间轮、`Due(long UnixMs)`、`Scheduled<T>.Cancel()`/`Pending`；
- `reply:`、`onBehalfOf:`、`a.ReplyTo`、`Hold`、`AskAll`/`Answers<,>`、`ReplyTo` 不能复制的规则、`[Claims<K>]` 的形状检查；
- `KindId`、`MessageId`、`EventId`（统一成 `TypeId`）、`Activated(bool Restored, bool MovedIn)`、`[Persist]` 隐含 `ITrackChanges`；
- v22 的名字：`IKind<T>` 和"种类"（→ `IActor<T>`、Actor 类型）、`KindKey`（→ `ActorId`）、链接、`Linked`、`LinkId`、`LinkFrom()`、`Unlink()`、`Unlinked`、`LinkLost`、`Confirmed`、`Unsynced`、`LinkConfirmed`、`NotLinked`（→ Presence、`Presence`、`PresenceId`、`Join()`、`Leave()`、`Left`、`PresenceLost`、`Joined`、`NeedsSnapshot`、`JoinConfirmed`、`NotJoined`）、`From<K>` 参数（→ `Sender<K>`）、`IAddressed`（→ `[To]`）、`IMessage<K>`、`IRequest<K, R>`（→ `IMessageTo<K>`、`IRequestTo<K, R>`）、`Envelope` 过滤参数（→ `Incoming`）、`Activation.Fresh`/`Restored`/`MovedIn`（→ `Created`/`Loaded`/`Migrated`）、`Unloading`（→ `Deactivating`）、`IdleAfter`（→ `IdleTimeout`）、`[OnError] Reload`（→ `Restart`）、`[Spawns<K>]`（→ `[Prefab<K>]`）、`Clock` 和 `Clock.Frame`（→ `Time`、`Time.Tick`）；
- v22 的 `Self<K>`（操作都到了 `Entity` 上；`self.Push` → `root.Broadcast`，`self.Unload()` → `e.Deactivate()`，`self.Disconnect()` → `client.Disconnect()`）、`e.Watch`（→ `e.SetGroups`）；
- v22 的 `IFrameGroup<K>`、`[Frame(Hz)]`、`MapFrame`（Actor 类型就是系统组，帧率是 `[TickRate]`）；
- v22 例子里经 Player 中转进图的写法：`TravelTo`、服务器之间的 `EnterMap`/`LeaveMap`、`Travelling`、`OnMap`、`Players`；
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
| v15 | 研究执行模型：消息、帧、并行之间的先后 |
| v16 | 一切都是对事件的反应：只有 `[On<T>]`，Tick 也是事件，由生成的 job 结构并行执行 |
| v17 | 一条消息是一个事务；Tick 按列并行 |
| v18 | 动作跨多帧：`Raise(e, after: n)`、`Scheduled<T>`，到期时重新校验 |
| v19 | n = 0 在当前处理方法之后紧接着执行，属于同一个事务 |
| v20 | 每个域是一个离散事件模拟：按 `(时间, 序号)` 逐个执行；并行来自空间区域的前瞻 |
| v21 | 帧率移到配置（Hz）；延后用时间；Send 统一；一个绑定规则；[Handles<K>]；Answer<TReq,TState>；链接自动；去掉空间分区 |
| v22 | 每帧逻辑用 EasyEcs 系统和帧组（帧率写在帧组上）；一个时间类型、一个计时机制（Due 有序索引）；Scheduled 是数据；[Persist] 自己声明 ITrackChanges；ReplyTo 放进状态；From 只表示发送方；实体操作在 Entity 上；[Filter] 并入 [On<Envelope>] |
| v23 | 改名（Actor、Presence/Join/Leave、Sender、IMessageTo/IRequestTo、TickRate、Time…）；客户端消息带 [To] 直达目标，每个 Actor 类型一个 Presence，掉线自动重放加入消息；只留 Entity；帧率写在 Actor 类型上、Actor 类型即系统组；异常不回滚如实说明 |

---

## 14. 待拍板

1. **采用 v23**：Actor 类型即地址、组件和系统组（`[TickRate]`、`[With<In<K>>]`）；客户端消息带 `[To]` 直达目标、不带 `[To]` 发给当前 Presence；每个 Actor 类型一个 Presence；Presence 丢失时框架重放加入消息；只有 `Entity` 一个句柄；异常不回滚，`Resume`/`Restart`。
2. **目标写在哪**：
   - A. 客户端消息的一个字段标 `[To]`（第 4 节目前按 A 写）。消息少，一条 `EnterMap` 同时是"加入"和"进哪张图"；
   - B. 单独的加入消息类型（例如 `IJoin<TKey>`），只有它能带目标、只有它的处理方法能 `Join()`。重放的范围一眼看清，但多一种消息。
3. **Presence 丢失后怎么重新加入**：
   - A. 框架重放加入消息（第 7 节目前按 A 写）。用户不写代码，但加入消息的处理方法必须幂等，重放的只有那一条；
   - B. 只投递 `PresenceLost`，由客户端 SDK 或 `[Handles<Client>]` 自己重发。逻辑看得见，但每个游戏都要写一遍。
4. **每个 Actor 类型只能有一个 Presence**：聊天室这类"同时在多个同类 Actor 里"的需求怎么办？
   - 把这类 Actor 类型标成允许多个 Presence（例如 `[Presences(Many)]`），不带 `[To]` 的消息对它报错，必须带 `[To]`；
   - 或者不支持：一个 `ChatHub` Actor 自己管成员，按 `ClientGroup` 扇出。
5. **加入消息存在路由项里**：大小要不要上限（例如 256 字节，超过的不重放，只投递 `PresenceLost`），还是限制在编译期（分析器要求加入消息是非托管的小结构）。
6. **根 prefab 怎么标**：`[Prefab<K>(Root = true)]`（目前写法），还是单独的 `[RootPrefab<K>]`。
7. **`[With<In<K>>]` 要用户写**：Map 的声明里写了两次 Map。可以向 EasyEcs 提 F5：组过滤可以写在接口上并代入类型参数（`[With<In<TSelf>>] interface IActor<TSelf> : ISystemGroup`），生成器对实现它的组生效，用户就不写了。
8. **向 EasyEcs 提的功能**：
   - F1：单个实体执行一个 job，`entity.Run<…, TJob>(ref job)`：跳板不用再复制组件参数；
   - F2：批量方法（`Span<T>`）也能 `Parallel = true`：现在只有逐个实体的方法能并行；
   - F3：加载时不打变更戳（`LoadEntitiesInto` 现在把每一行都算作写入），`ChangeSystem` 就不用重置基准；
   - F4：保存整棵子树（`Descendants`），迁移和记录不用自己收集子孙；
   - F5：见上一条。
