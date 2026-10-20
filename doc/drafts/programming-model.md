# DaServer 新编程模型设计（草案 v21，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。能用 EasyEcs 的功能就用 EasyEcs，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个实体发到哪个实体，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分；
- 高性能，高并发。

本稿是完整的一份。第 12 节是完整的例子，分别列出用户写的代码和生成的代码。第 13 节是检查和版本记录，第 14 节是待拍板的点。

---

## 0. 一页看懂

### 三个工程

| 工程 | 放什么 | 谁引用 |
|---|---|---|
| `Game.Protocol` | 客户端收发的消息。只有数据，不出现任何服务端的种类 | 客户端和服务端；编译成 netstandard2.1 的 DLL 交给 Unity |
| `Game.Server` | 种类、服务器之间的消息、本地事件、组件、prefab、服务器类型，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有逻辑：`[Handles<K>]` 类里的 `[On<T>]` 方法，以及生成的分发表、Tick 的 job 结构、客户端路由表 | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

### 用户写的

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 推给客户端的消息 | `partial class Nearby : IToClient`（含集合的消息是 `[NinoPooled]` 类） | Protocol |
| 种类（地址，也是实体上的组件） | `readonly partial record struct Map(long Id) : IKind<Map>;` | Server |
| 服务器之间的消息 | `[From<Player>] record struct EnterMap(…) : IRequest<Map, LinkId>;` | Server |
| 本地事件 | `[NinoType] record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；持久化的写成 `[Persist] partial record struct` | Server |
| prefab | `[Spawns<Map>] static readonly Prefab Map = …;` | Server |
| 令牌里的声明 | `[Claims<Player>] record struct PlayerClaims(long PlayerId) : IComponent;`：客户端连上后，框架把它挂到这个 Player | Server |
| 服务器类型 | `[Hosts<Map>] struct MapServer : IServer;` | Server |
| 帧率 | 配置，按服务器类型，单位 Hz：`"MapServer": { "TickRate": 30 }`；AppHost 可以写 `.WithTickRate(30)` | 配置 / AppHost |
| 逻辑 | `[Handles<Map>] static partial class Maps { [On<MoveTo>] … [On<Tick>] … }` | Logic |
| 现在或以后做一件事 | `Send`：`caster.Send(new Strike(…), after: Skills.Windup(skill))`，不写 `after` 就是紧接着 | Logic |
| 等别的实体的结果 | `self.Ask(map, new EnterMap(…), state: new Travelling(map))`，回信是 `Answer<EnterMap, Travelling>` | Logic |
| 每帧的逻辑 | `[On<Tick>]`，参数是组件或列（`Span<T>`），默认并行；要实体或域的写 `[On<Tick>(Serial)]` | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `Client` | 内置的种类：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Self<K>` | 域的根：强类型的键、根实体，以及所有消息操作（`Send`、`Ask`、`Reply`、`Push`、`Unlink`、`Disconnect`、`LinkFrom`） |
| `World` | 只做 ECS：找实体、建实体、读写组件 |
| `KindKey` | 任意种类的地址（种类 id 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键 |
| `LinkId`、`Linked`、`Confirmed` | 客户端在某个目标上的链接；目标上的链接实体，例如地图上的 Avatar |
| `ReplyTo<T>`、`Answer<TReq, TState>` | 晚些回复；等别的实体的结果，`TState` 是发问时存下的状态 |
| `Activated`、`Unlinked`、`LinkLost`、`Unloading` | 生命周期消息，用 `[On<T>]` 处理 |
| `Tick` | 每一帧的事件，用 `[On<Tick>]` 处理 |
| `Clock` | shard 的只读单例：`Frame`、`DeltaTime`、`TickRate`、`Now` |
| `Scheduled<T>` | 延后的事件的句柄：可以存进组件，用来取消或表示"正在进行" |
| `IdleAfter` | 空闲回收 |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 种类组件的值索引 | 为 `partial` 种类生成 `Key => this` 和种类 id 常量 | 收到消息时查索引 |
| 逻辑属于哪个种类 | — | `[Handles<K>]` | 分发表按种类建 |
| 事件的顺序 | — | — | 每个域按 `(时间, 序号)` 逐个执行消息和事件，再跑 Tick |
| 消息处理 | — | `[On<T>]` 生成的分发跳板 | 逐个调用，逐个隔离异常 |
| 参数绑定 | — | 一个规则：组件参数绑定到主体 | 跳板读主体的组件 |
| 每帧的逻辑 | 并行 API、批量方法 | `[On<Tick>]` 生成 job 结构 | 有序阶段之后按列并行 |
| 帧率 | — | — | 配置按服务器类型读入，节点运行期间不变 |
| 延后的事件 | `ChildOf` | `Send(e, after:)`、`Scheduled<T>`、`IEvent` | 存绝对到期时间，按所在的帧放进时间轮；发起者销毁时一起消失 |
| 发送方向 | — | `[From<K>]` 生成 `Self<K>` 上的方法 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[Handles<K>]` 生成路由表 | `Client` 实体转发 |
| 等待、回复 | `ChildOf`、`ICleanupComponent` | `Answer<TReq, TState>`、`ReplyTo<T>` | 调用 id、框架内部的毫秒期限、期限传递 |
| 链接 | 子实体、值索引 | `[Claims<K>]` | 挂接、重试、顶号、自动写路由、确认、核对 |
| 生命周期 | — | 用户写 `[On<Activated>]` 等 | 投递生命周期消息 |
| 一张地图的实体 | `ChildOf`、`world.Children(map)` | — | — |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ICleanupComponent` | `[Persist]` 补上 `ITrackChanges` | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收的上下文 | 分发表、job 结构、路由表在 Logic 程序集里 | 两帧之间切换 |
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

**域**是一个种类的实例和它的子孙，例如一张地图和地图上的所有实体。域里发生的一切，都是排好序的事件：

| 事件 | 时间 | 例子 |
|---|---|---|
| 客户端或别的实体发来的消息 | 进入本域（到达 shard）的时刻 | 移动一步、放技能、交易 |
| 本地事件 `e.Send(ev)` | 紧接着当前处理方法，同一个事务 | 立即结算的技能 |
| 延后的事件 `e.Send(ev, after: 时间)` | 发起时刻 + after，精确值，不取整 | 前摇 0.3 秒后的结算、5 秒后 buff 到期 |
| `Tick` | 每一帧，有序阶段之后 | 移动推进、buff 跳伤害、AOI |

**排序只有一条：所有事件按 `(时间, 序号)` 执行。**
- 时间：消息是进入本域的时刻，延后事件是精确的到期时刻。序号是本域的计数器，时间相同时按先后打破平局。
- 帧只决定"什么时候执行"，不改变"谁先谁后"：时间落在上一帧边界之后、这一帧边界之前的事件，在这一帧的有序阶段里按 `(时间, 序号)` 执行。
- 所以 0.29 秒到达的闪避排在 0.30 秒到期的结算前面，即使它们落在同一帧里。
- 每个事件看到的，正好是它之前所有事件的全部效果。

**一帧 [t, t + DeltaTime)：**

| 阶段 | 做什么 | 怎么跑 |
|---|---|---|
| a. 有序阶段 | 时间不晚于 t 的消息（已按到达时刻排好）和到期事件（时间轮这一格，按时间排序，一格通常只有几个），按 `(时间, 序号)` 归并 | 串行。每一项执行完，紧接着执行它的即时链（不带 `after` 的 `Send`），同一个事务；每项最多连锁 1,024 个 |
| b. Tick 阶段 | `[On<Tick>]` 处理方法 | 默认按列并行、SIMD，只读写组件；`[On<Tick>(Serial)]` 串行 |

- 有序阶段开始时取一次邮箱快照；这期间新到的消息进下一帧。
- 只有带 Tick 处理方法的种类按帧走（例如地图）。Player、Guild、`Client` 没有帧：消息到了就处理，延后事件到了时间就执行，顺序规则不变。
- 到期时要重新校验：施法者还活着吗？被眩晕了吗？目标还在、还够得着吗？

**结果：** 在第 k 帧执行的结算，看到的是第 k−1 帧结束时的位置（第 k−1 帧的 Tick 已经推进过），再加上时间比它早的所有消息和事件，包括同一帧里先到的移动。所以前摇期间走开，就是闪避。

**例子：同一帧里依次到达 [A 打 B，B 移动，A 移动，B 打 A]**

前摇为 0（`after` 是 `TimeSpan.Zero`）：
1. A 的 `Cast` 返回后，紧接着执行 `Strike`：读 B 还没动的位置，够得着就扣血；B 被打死则带上 `Dead`。
2. B 移动：B 已死的话，`Move` 直接返回。
3. A 移动。
4. B 的 `Cast`：B 已死则拒绝；否则紧接着结算，读 A 移动后的位置。

前摇 0.3 秒，30 Hz（9 帧）：
1. 第 k 帧：A 的 `Strike` 到期时刻 t_A + 0.3 秒；B 移动；A 移动；B 的 `Strike` 到期时刻 t_B + 0.3 秒，比 A 的晚。两个都进它们所在的那一帧的格子。
2. 第 k+1 到 k+8 帧：B 继续发 `MoveTo` 走开，Tick 推进 NPC。
3. 到期的那一帧，有序阶段按时间执行：A 的 `Strike`、B 的 `Strike`，以及这一帧里时间夹在它们之间的消息（例如 B 在 A 到期前一刻发来的移动）。A 的结算读 B 此刻的位置：已经走出范围，就是闪避。如果 A 打死了 B，B 的结算发现施法者已死，作废。

**并行从哪里来：**

| 来源 | 做法 |
|---|---|
| 很多个域 | 一个 shard 是一个线程，跑很多张地图；节点有很多 shard，集群有很多节点。TrinityCore 的 MapUpdater、EVE 的星系、LoL 和 Valorant 的对局都是这样 |
| Tick 阶段 | 只读写组件的 `[On<Tick>]` 按列并行、SIMD，覆盖这个 shard 上这个种类的所有域 |

- 域内的有序阶段永远串行。没有上市的游戏把一个模拟步按空间切开并行：那需要框架懂位置、作用距离、最大速度这些游戏概念（违反 ADR-045），还会加一帧延迟，语义也脆弱。
- 以后再考虑：
  - 一个超大的域，只有在基准测试显示它的有序阶段超出帧预算时，才提供用户自己给出分区的可选方案；
  - 无缝大世界用 New World 的做法：世界按格子分给进程，人移动时交接。

**不采用的：**
- 乐观执行（Time Warp 回滚）：保存状态或写反向计算的开销和一个约 1 µs 的事件本身差不多，而且回滚前已经推给客户端的结果收不回来。
- 按消息类型分批：会打乱 `(时间, 序号)`。只有 Tick 这种"只写组件"的才按列批量处理。
- 把命令排到下一帧统一执行：等于把效果挪后，会出现"发包时打得到、执行时人跑了"。

**用户写的只有三种，全部是 `[On<T>]`：**

```csharp
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>]                                   // a client message: subject = the caster's avatar
    static void Cast(in CastSkill m, Entity me, Self<Map> map, World world)
    {
        if (me.Has<Casting>() || me.Has<Stunned>() || me.Has<Dead>()) return;
        if (!Targets.TryFind(world, map, m.Target, out var target)) return;
        me.SetCasting(me.Send(new Strike(m.Skill, target.Id), after: Skills.Windup(m.Skill)));   // TimeSpan.Zero: right after this handler
    }

    [On<Strike>]                                      // a local event: subject = the caster; reads the target as it is now
    static void Resolve(in Strike s, Entity me, in Position at, World world)
    {
        me.RemoveCasting();
        if (me.Has<Dead>() || !world.TryGet(s.Target, out var target) || target.Has<Dead>()) return;
        if (!Skills.InRange(s.Skill, at.Value, target.Get<Position>().Value)) return;          // out of range: dodged
        target.Get<Health>().Value -= Skills.Damage(s.Skill);
    }
}

[Handles<Map>]
public static partial class Simulation
{
    [On<Tick>]                                        // columns only: parallel, SIMD
    static void Integrate(Span<Position> p, ReadOnlySpan<Velocity> v, in Clock clock) { /* integrate */ }
}
```

用户不判断"立即还是延后"（`after` 来自数据，0 也合法），也不判断"串行还是并行"（有序阶段串行，只写组件的 Tick 并行）。

**用户能看到的反馈：**
- 每个域的指标：每帧有序阶段的耗时、Tick 的耗时；
- 调试模式：把并行的 Tick 改成串行执行，核对结果是否完全一致。

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
- **帧率**是服务器类型的配置（第 9 节）：
  ```json
  { "DaServer": { "MapServer": { "TickRate": 30 } } }
  ```
- **AppHost：**
  ```csharp
  var cluster = builder.AddDaServerCluster("game");
  cluster.AddNodes<GameServer>("game", replicas: 3);
  cluster.AddNodes<MapServer>("map", replicas: 4, shards: 8).WithTickRate(30);   // written into the nodes' configuration
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientEndpoints();
  ```
  - AppHost 不引用服务端程序集。DaServer.Aspire 的生成器读服务端工程提交的 `wire.manifest`，在 AppHost 里生成同名的服务器类型。
  - `AddNodes<T>` 返回 `NodeGroup<T>`。承载 `Client` 的服务器，生成的类型实现 `IAcceptsClients`；`WithClientEndpoints()` 是 `NodeGroup<T>` 上 `where T : IAcceptsClients` 的扩展方法，所以给别的服务器开客户端端口编译不过。
  - `WithTickRate(hz)` 把 `DaServer:<服务器类型>:TickRate` 写进这组节点的配置；配置里另写的值优先。
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
- 处理方法里用 `self.Key` 知道自己是谁。

**接口**：`IKind<T>` 继承 `IComponent` 和 `IIndexed<T>`。
- EasyEcs 的生成器会读继承来的接口，所以 EasyEcs 不用改。
- 种类必须是 `partial`，我们的生成器给它补上 `public T Key => this;` 和种类 id 常量 `public const uint KindId`（Nino 类型 id）。
- 种类上不写帧率：帧率是部署的事，见第 9 节。

**只有运行时能写种类组件：**
- 分析器禁止用户代码对种类组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时创建实例时，断言这个键在索引里还不存在。

**键**：
- 种类的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[Stateless]` 种类**：不保存，不迁移；每个节点可以有自己的实例，键只用来选实例。
- **子实体不可寻址**，例如 Avatar、等待中的请求、延后的事件。它们跟父实体一起保存、一起迁移。
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
- **目标由服务端决定**：处理它的 `[On<T>]` 在哪个 `[Handles<K>]` 类里，消息就转发给哪个种类。
  - 生成器据此在 Logic 里生成客户端路由表；
  - 一条客户端消息只能有一个处理种类。
- `IAddressed` 的消息带一个 `To` 字段，用来在同种类的多个链接里选一个。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[NinoType, From<Player>] public record struct EnterMap(Player Player) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player) : IMessage<Map>;
```

- **接收方写在接口上**：`IMessage<TTo>` 是单向消息，`IRequest<TTo, TReply>` 是请求。
- **发送方写在特性上**：`[From<K>]`，可以写多个。
  - `[From<Client>]` 指服务端的 `Client` 实体，不是远端的客户端。
  - 远端的客户端只能发 `IFromClient`。
- **方向由生成的方法保证**：每条消息只在它的发送方的 `Self<K>` 上生成发送方法，方向写错就没有这个方法，编译不过。
  - 这是帮助写对的手段，不是安全边界。
  - 安全边界在网关：只转发 `IFromClient`。
- **客户端上下文自动带上**：处理客户端消息时发出的请求，信封里带着这个客户端的连接信息（`ClientId`、所在节点、连接代数），和期限一样。目标建链接时用它，回信时用它写路由（第 7 节）。
  - 不在处理客户端消息时，显式写 `self.Ask(…, onBehalfOf: lost.Client)`。
  - 用户不接触连接信息本身。
- **`ICluster`**（HTTP、后台、测试）不属于任何种类，可以发任何服务器之间的消息，回复回到调用方。
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Client` 的节点，由连着它的那个处理。

### 本地事件（Server）

```csharp
[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;
```

- 本地事件只在一个域里传：发给本域的一个实体，由同一个种类的 `[On<T>]` 处理。
- 声明在 Server 工程，带 `[NinoType]`：延后的事件随实例迁移，也不能随 Logic 热更消失。
- 影响别的域用消息。

---

## 5. 逻辑

逻辑只有一种写法：`[Handles<K>]` 的 `static partial class` 里的 `[On<T>]` 方法。

```csharp
[Handles<Map>]
public static partial class Movement
{
    [On<MoveTo>] static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed, Self<Map> map) { … }
}
```

- `[Handles<K>]` 写一次，类里所有方法都属于种类 K：客户端消息转发给 K，Tick 跑在 K 的域上。
- 一个种类可以有多个类，例如 `Maps`、`Movement`、`Combat`、`Simulation`。

### `[On<T>]` 的 T

| T | 是什么 | 例子 |
|---|---|---|
| 服务器之间的消息 | 消息的 `IMessage<K>`、`IRequest<K, R>` 必须是类上的 K | `[On<EnterMap>]` |
| 客户端消息 | 转发给类上的 K | `[On<MoveTo>]` |
| 生命周期消息 | 框架发的 | `[On<Activated>]`、`[On<Unlinked>]`、`[On<LinkLost>]`、`[On<Unloading>]` |
| 回信 | 等别的实体的结果（第 8 节） | `[On<Answer<EnterMap, Travelling>>]` |
| 本地事件 | `IEvent`，由 `Send` 发起 | `[On<Strike>]` |
| 帧 | 每一帧一次（第 9 节） | `[On<Tick>]`、`[On<Tick>(Serial)]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个处理种类。

### 一个绑定规则：参数绑定到主体

每条消息、每个事件都有一个**主体**实体。`Entity` 参数就是主体，组件参数（`in`/`ref`）是主体上的组件。

| 进来的是 | 主体 |
|---|---|
| 客户端消息 | 这个客户端在目标上的链接实体，例如地图上它的 Avatar |
| 本地事件 | 它被发给的实体：`caster.Send(new Strike(…), after: …)` 的 caster |
| 服务器之间的消息、`Activated`、`Unloading`、`Answer` | 接收种类的根实体，例如地图 |
| `Unlinked` | 正在断开的链接实体 |
| `LinkLost` | `Via` 上这个客户端的链接实体，例如 Player 上它的链接实体 |
| `Tick` | 并行：匹配的每个实体；取 `Self<K>`：每个域的根 |

**别的实体在方法体里显式读写：**

```csharp
if (!world.TryGet(s.Target, out var target) || target.Has<Dead>()) return;
ref var hp = ref target.Get<Health>();
if (me.Has<Stunned>()) return;
if (self.Entity.TryGet<OnMap>(out var on)) …
```

- 首选的写法：发送方把接收方需要的数据放进消息，接收方少读别的实体。
- tag 用 `me.Has<Stunned>()` 判断。
- 可能没有的组件用 `TryGet`。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Entity` | 主体 |
| 组件 `in`/`ref` | 主体上的组件。主体缺少任何一个时不调用处理方法，等同于 ECS 查询没匹配上；请求以 `Skipped` 失败 |
| `ref DynamicBuffer<T>` | 主体上的缓冲 |
| `Self<K>` | 域的根：`Key`、根实体 `Entity`，以及所有消息操作 |
| `in ReplyTo<T>` | 请求的回复令牌，要晚些回复时取 |
| `From` | 发送方 |
| 单例 `in`/`ref` | 本 shard 的单例；`Clock` 只读 |
| `World` | ECS 操作 |
| `Span<T>`、`ReadOnlySpan<T>` | 只用于并行的 `[On<Tick>]`：一整列 |

**引用不会失效**：处理方法里做了结构变更（建实体、`Ask`、`Send`、加 tag 等），参数仍然可用。
- 生成的跳板把 `in` 参数复制成局部变量。
- `ref` 参数传入局部变量，调用结束后用 `TryGetRef` 写回。组件如果已被处理方法删掉，就不写回，不会把它加回来。
- `DynamicBuffer` 参数直接按 `ref` 传入：它是指向存储的句柄，跳板只在调用前确保存储已经建好，之后不写回。
- 方法体里自己取的 `ref`（`target.Get<Health>()`）不能跨过这个实体的结构变更使用（DAS022）。
- **可能别名的两个可写引用，分析器报警**：例如取了 `ref Health` 参数，又写 `target.Get<Health>()`，而 `target` 可能就是 `me`（打自己的技能）。跳板的写回会覆盖方法体里的那次写入。改成先判断 `target != me`，或者把参数改成 `in`。

### 现在或以后：`Send`

一个动词：`Send`。

| 写法 | 发给谁 | 什么时候执行 |
|---|---|---|
| `e.Send(ev)` | 本域的实体 `e`，`ev` 是 `IEvent` | 当前处理方法返回后、下一项之前，同一个事务（即时链，最多 1,024 个） |
| `e.Send(ev, after: t)`，返回 `Scheduled<T>` | 同上 | 到期时刻 = 现在 + t，在它所在那一帧的有序阶段按时间执行，从不提前；`t` 为 `TimeSpan.Zero` 时同上一行 |
| `self.Send(to, msg)` | 别的实例，`msg` 是服务器之间的消息 | 进入对方的邮箱 |

- `Scheduled<T>` 存进组件，可以 `Cancel()`（前摇被打断），也可以看 `Pending` 判断"正在施法"。
- 发起者被销毁时，它的待执行事件一起消失。
- 用户不判断"立即还是延后"：`after` 来自数据，0 也是合法值。
- 本地事件只能发给本域的实体，运行时断言。

### 每帧：`[On<Tick>]`

| 写法 | 参数 | 怎么跑 |
|---|---|---|
| `[On<Tick>]` | 只有组件、列（`Span<T>`）、只读单例 | 生成 job 结构，用 EasyEcs 的并行 API 按列跑，可以 SIMD。不能发消息、发事件、做结构变更 |
| `[On<Tick>(Serial)]` | 可以取 `Entity`、`Self<K>`、`World` | 串行。取 `Entity`：逐个实体；取 `Self<K>`：每个域一次，例如一张地图的 AOI |

- 取了 `Entity`、`Self<K>` 或 `World` 却没写 `Serial`，分析器报错：热循环不会悄悄变成串行。
- 匹配的实体：这个种类的域里、带着所有组件参数的实体；`[With<T>]` 再收窄。
- `Tick` 参数可以不写，`Clock` 里有 `DeltaTime`。
- 先后：同一个类里按声明顺序；跨类用 `[After<TClass>]`。

### 其他

- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理。
- 每帧之后，框架销毁已经回复的等待实体，以及已经写完记录的实例。
- **分发表、job 表和路由表**在加载时只暂存，各个 shard 在两帧之间换上。表在暂存时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理方法抛出异常

- 运行时逐项捕获异常：这条消息的请求以失败结束，它的即时链里还没执行的事件作废。
- 之后怎么办，看种类上的 `[OnError]`：
  - `Resume`（默认）：实体保持原样，接着处理下一项；
  - `Reload`：照正常流程卸载（第 6 节），之后从记录重新激活。
- 一项的即时链超过 1,024 个，也按 `[OnError]` 处理。
- **`[On<Activated>]` 抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次。
  - 这样不会留下初始化了一半的实体。
- `Client` 种类抛出异常时，断开这个客户端。

---

## 6. 生命周期

生命周期事件是框架发的消息，用 `[On<T>]` 处理，和其他事件排在同一条 `(时间, 序号)` 线上。

| 消息 | 主体 | 何时 | 常见用途 |
|---|---|---|---|
| `Activated(bool Restored, bool MovedIn)` | 根 | 实例激活后的第一条邮件 | 初始化 |
| `Unlinked` | 链接实体 | `self.Unlink(e)` 所在的处理方法返回后紧接着执行，同 `e.Send(ev)`；执行完框架销毁链接实体 | 广播离开 |
| `LinkLost(KindKey Target, From Client)` | `Via` 上这个客户端的链接实体 | 一条路由失效，发给当初建立它的实体（`Via`）；这个客户端和 `Via` 之间已经没有链接时不投递 | 重新建立链接 |
| `Unloading` | 根 | 卸载前的最后一条邮件 | 收尾 |

**占位**：一个键在"还没有实体"或"实体正在卸载"时，框架用一个占位接住发给它的邮件。

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达 | 占位接住邮件，异步读记录。这时还没有实体，Tick 看不到它 |
| 激活 | 记录到了，或者没有记录 | 按 prefab 建实体，装上记录；邮箱里第一条是 `Activated`，后面才是占位接住的邮件 |
| 运行 | — | 按 `(时间, 序号)` 执行消息和事件，再跑 Tick（第 1 节） |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或者调用 `self.Unload()` | 依次：<br>1. 决定卸载的那一刻起，新到的邮件由占位接住，所以 `Unloading` 之后不会再有别的邮件；<br>2. 对每个链接实体 `Unlink`，各自的 `Unlinked` 紧接着执行，通知对应的 `Client`，只发不等；<br>3. 投递 `Unloading`；之后待执行的延后事件随实体一起消失（第 9 节）；<br>4. 写记录；<br>5. 写成功后销毁实体。占位接住的邮件激活新实例，新实例读到的是刚写好的记录 |

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
- **声明**：令牌里的声明放进用户声明的组件，例如 `[Claims<Player>] PlayerClaims(long PlayerId)`。框架只负责解码令牌、把组件加到 `Client` 上，不认识任何业务字段。
- **位置固定**：`Client` 固定在连接所在的节点，不参与放置和重新平衡，也不迁移；连接断开，它就卸载。
- **扩展**：用 `[Spawns<Client>]` 的 prefab 给它加组件，例如令牌桶；在 Logic 里写 `[Handles<Client>]` 类。
- **找不到它**：别的实体不能用 `ClientId` 找到 `Client`。推送只经过链接，所以不需要位置目录。

### 第一条链接：`[Claims<K>]`

```csharp
[Claims<Player>] public record struct PlayerClaims(long PlayerId) : IComponent;   // fields = Player's key
```

- 声明组件的字段按顺序就是 K 的键（分析器检查形状一致）。每个种类最多一个 `[Claims<K>]`。
- 框架做完整件事，用户不写代码：
  1. `Client` 激活后，向 `Player(PlayerId)` 发框架内部的挂接请求；
  2. Player 一侧建这个客户端的链接实体，**最新的连接胜出**：同一个 Player 上旧连接的链接实体被 `Unlink`，旧的 `Client` 断开；旧连接之后再来挂接（半开连接在重试），回复 `Superseded`，旧的 `Client` 直接断开；
  3. 失败（超时、`NoOwner`、`Busy`）时按带随机抖动的退避重试，超过次数就断开客户端。一个游戏节点宕机时，十万个客户端不会同时重试；
  4. 收到 Player 的 `LinkLost`（Player 重启）时重新挂接。
- 挂接好之前，客户端消息在 `Client` 上排队；挂接好后框架推一条 `Ready` 给客户端。
- Player 上的链接实体和别的链接实体一样：Player 的处理方法收到这个客户端的消息时，主体就是它。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Client` 实体。
2. 先过 `Client` 的过滤方法：`[Handles<Client>]` 类里的 `[Filter] static Verdict Allow(in Envelope e, ref RateLimit r)`。它逐条执行，返回放行或者拒绝的原因。
3. 按生成的路由表，得到目标种类。
4. 在路由表 `DynamicBuffer<Route>` 里找这个种类的链接：`IAddressed` 的消息用 `To` 选具体的一项，其他消息用最近建立的那一项。
5. 转发给目标。转发的消息带着 `LinkId`，目标据此找到链接实体，作为主体。
6. 被拒绝时，客户端收到原因：`NotLinked`、`Busy`、`NoActor`、`Filtered` 等。

### 链接

| 在哪 | 是什么 |
|---|---|
| 目标一侧 | 一个子实体，带 `Linked(LinkId, 连接信息)`，按 `LinkId` 建值索引。连接信息来自请求信封里的客户端上下文 |
| `Client` 一侧 | 路由表里的一项：`Route(KindKey 目标, LinkId, KindKey Via)`。`Via` 是建立这条路由的实体，例如 Player；挂接建立的路由，`Via` 是 `Client` 本身 |

**`LinkId`** 是 64 位：高 32 位是创建它的节点 id，低 32 位是该节点上的计数。
- 节点 id 在每个进程启动时随机生成，所以全集群唯一。
- 链接迁移后，`LinkId` 照用，只是一个值，不代表它现在在哪个节点。

**建立链接：**
1. 目标在处理请求时调用 `self.LinkFrom(entity)`，回复 `LinkId`。客户端来自请求的客户端上下文；没有上下文时请求以 `NoClient` 失败。
   - `entity` 已经链接着别的连接时，新连接接管它（最新的胜出）：旧连接的 `Client` 删掉路由，实体退出旧连接的 `ClientGroup`、去掉 `Confirmed`，不触发 `Unlinked`。
2. **路由自动写好**：回复类型是 `LinkId` 的请求成功回信时，框架先把 `Route(目标, LinkId, Via = 发问方)` 发给上下文里的 `Client`，再调用 `Answer` 的处理方法。
   - 处理方法随后回复客户端的请求时，回复和路由同一来源、同一目标，所以 `Client` 一定先写路由，再把回复交给客户端。
3. `Client` 写好路由后，先向目标发 `LinkConfirmed(linkId)`，之后才转发消息。两者同一来源、同一目标，所以确认一定先到。确认如果丢了，这条链接上第一条转发来的消息也算确认。
4. 目标收到确认后，给链接实体加 `Confirmed` tag。
   - 推送和 AOI 只认带 `Confirmed` 的链接实体；
   - AOI 第一次看到 `Confirmed` 时推一份完整的快照。迁移过来的链接实体（`MovedIn`）也按第一次看到处理，因为 `ClientGroup` 的订阅只在本节点。
5. **期限**：
   - 请求的期限取客户端请求剩下的时间；
   - 回信到达时，客户端的请求已经超时的，不写路由；
   - 目标的链接实体过了期限仍未确认，就删除。

**推送：**
- `self.Push(linkEntity, in msg)`：推给某个链接实体对应的客户端；
- `self.Push(in msg)`：推给根的所有已确认的链接；
- `ClientGroup` 广播（第 9 节）。
- 都按链接实体记下的节点直接发送。

### 断开

- **`self.Unlink(linkEntity)`**：
  1. 当场退出所有 `ClientGroup`，通知 `Client` 删掉这条路由，客户端收到一条框架通知；
  2. 当前处理方法返回后紧接着执行 `[On<Unlinked>]`，主体是这个链接实体，规则同 `e.Send(ev)`；
  3. 之后框架销毁链接实体。
- **外部触发的断开**（`Client` 断线、被踢、被顶号，或者节点离开视图）由框架作为邮件送到目标，在目标的处理过程中 `Unlink`。
- **同一个键不会有两个链接实体**：同一个 Player 再进同一张图时，`Enter` 用 `LinkFrom` 接管已有的 Avatar（第 12 节）。
- **通知丢失时的兜底：**
  - 两个节点之间的链路重连时，目标一侧按网关列出自己持有的链接实体，网关指出其中哪些已经不认识，目标对这些执行断开；
  - 某个节点离开成员视图时，各节点对记在该节点上的链接实体执行断开；
  - 网络抖动、GC 停顿不会让链接失效，只有节点离开视图才会。
- **目标重启**（实例从记录恢复，旧的链接实体已经没有了）：
  - `Client` 在两种情况下删掉这条路由：转发时收到 `NotLinked`；或者看到目标所在的节点离开视图。即使客户端一直不发消息，也能发现。
  - 然后给 `Via` 发 `LinkLost(目标, 客户端)`。
  - 例如地图重启后，Player 收到 `LinkLost`，重新 `EnterMap`；Player 重启后，`Client` 收到自己的 `LinkLost`，框架重新挂接。
- 链接相关的控制消息不受 `Busy` 限制。

**换地图**：由业务决定先后，Player 先发 `LeaveMap`，再请求 `EnterMap`。框架不会自动断开旧链接。一个客户端可以同时链接同一种类的多个目标，例如多个聊天室。

---

## 8. 等待别的实体的结果

```csharp
self.Ask(map, new EnterMap(self.Key), state: new Travelling(map), reply: reply);

[On<Answer<EnterMap, Travelling>>]
static void Entered(in Answer<EnterMap, Travelling> a, Entity me, Self<Player> self) { … a.Ok, a.Reply, a.State, a.ReplyTo … }
```

- **`self.Ask(to, in message[, state:][, reply:][, onBehalfOf:])`** 创建一个等待子实体，带：
  - `state`：一个普通组件，存在等待实体上，回信时作为 `a.State` 交回；不写时是 `None`；
  - 调用 id：全集群唯一；
  - 期限：框架内部的毫秒期限（第 9 节）；
  - `reply`：交出的 `ReplyTo<T>`，回信时作为 `a.ReplyTo` 交回；
  - 客户端上下文：处理客户端消息时自动带上，或者 `onBehalfOf:` 显式给出。
  - 生成器为每种参数形态各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer<TRequest, TState>` 邮件**，按（请求类型, 状态类型）分发；主体是发问的根。
  - `a.Ok`、`a.Status`、`a.Reply`（请求声明的回复类型）、`a.State`、`a.ReplyTo`；
  - 回复类型是 `LinkId` 时，调用处理方法之前路由已经发出（第 7 节）；
  - 处理完，等待实体由框架删除。
- **超时**：期限到了，框架投递一个 `Ok = false` 的 `Answer`。它和别的消息一样进入本域，排进 `(时间, 序号)`。
- **`ReplyTo<T>` 是带类型的清理组件**，由框架搬运，用户不能复制（分析器检查）：
  - `T` 就是请求声明的回复类型，`self.Reply` 在编译期检查类型；
  - `a.ReplyTo` 的 `T` 由分析器核对：同一个（请求, 状态）的所有 `Ask` 交出的 `ReplyTo<T>` 必须是同一个 `T`；
  - 没有交出令牌时，`self.Reply(a.ReplyTo, …)` 什么也不做；
  - `self.Reply` 先读出令牌，再移除组件，然后发出回复；
  - 等待实体在回复之前被销毁时，清理系统恰好执行一次，给调用方发一个失败回复；
  - 迁移时，旧的那一份在销毁前先移除 `ReplyTo`，不会误发；到了新节点，它作为普通组件装上，清理规则照常生效。
- 请求的处理方法必须返回回复，或者取 `ReplyTo<T>`（分析器检查）。
- **晚些回复，不问别人**：`self.Hold(in reply, state: …)` 建一个等待实体，之后对它 `self.Reply`。
- **一对多**：`self.AskAll(keys, in message, state: …)` 只建一个等待实体，回信累积在它的缓冲里。全部到齐或者超时后，投递一个 `Answers<TRequest, TState>`，里面的集合是池化的。
- 状态类型必须声明在 Server 工程，因为 Logic 热更时，它里面的类型会被移除。

---

## 9. 时间与广播

**帧率：**
- 帧率是服务器类型的配置，单位 Hz：`DaServer:MapServer:TickRate`，不写默认 20。AppHost 的 `.WithTickRate(hz)` 写进同一个配置项。
- 节点运行期间不变；改帧率就是重新部署。
- `Clock` 是 shard 的只读单例：

  ```csharp
  public readonly struct Clock { public long Frame; public TimeSpan DeltaTime; public int TickRate; public long Now; }   // Now: UTC ms at the frame's start
  ```

- 只有带 `[On<Tick>]` 的种类跑 Tick；Player、Guild、`Client` 不跑。
- 一种服务器承载多个要 Tick 的种类时，默认同一个帧率。要不同帧率，按种类配置（`"MapServer": { "TickRate": 30, "Room": { "TickRate": 10 } }`），所有帧率都必须整除其中最快的一个（例如 60、30、10），避免节拍抖动；否则启动失败。Tick 执行期间 `Clock` 是当前种类的值。
- 一个 shard 上同一帧率的域共用帧边界，所以 Tick 能一次按列跑完这个 shard 上这个种类的所有域。

**延后的事件用时间，不用帧：**
- `e.Send(ev, after: TimeSpan)`。数据里写秒或毫秒，例如 `Skills.Windup(id)` 返回 `TimeSpan`。
- 到期时刻 = 发起时刻 + after，存精确的绝对时间（UTC），不存帧号。它在所在那一帧的有序阶段里按时间执行，从不提前。`TimeSpan.Zero` 或不写就是即时链。
- 为什么：改帧率不能改技能的手感。Minecraft 的 `/tick rate` 改了帧率，按帧写的一切都跟着变快变慢；Photon Quantum 的 `FrameTimer.FromSeconds` 让用户写秒，内部存帧。
- 不 Tick 的域没有帧：到了时间就执行。

**存放：**
- 待执行的事件是发起实体的子实体，挂在一个按到期帧分格的时间轮里。
- 发出时追加到所在那一格，插入 O(1)。一格通常只有几个事件，取出时按 `(时间, 序号)` 排一次，再和这一帧的消息归并。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- `Scheduled<T>` 是这个子实体的句柄：`Cancel()` 销毁它；`Pending` 在执行或取消后变成 `false`。
- 周期性的逻辑写 `[On<Tick>]`；稀疏的周期（例如每秒跳一次伤害）在事件处理方法里再 `Send(…, after:)` 下一次。

**框架内部的期限：**
- 请求超时、链接确认期限、挂接重试的退避，用的是框架内部的 `Due(long UnixMs)`：绝对的 UTC 毫秒数，带有序值索引。迁移和恢复之后仍然正确，前提是各节点用 NTP 对时。
- 它们不属于游戏逻辑，用户也不能用：到期时作为消息进入本域，再按 `(时间, 序号)` 执行。

**广播：**
- **`ClientGroup`**：
  - 一次序列化，每个有成员的网关节点收到一帧，由网关扇出；
  - 成员是已确认的链接实体，`Unlink` 时自动退出。
- **小组**（公会、队伍）：一个固定键的实例，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

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

- **`[Persist]` 就意味着追踪变更**：组件写成 `partial`，生成器补上 `ITrackChanges`，用户代码里不出现它。
- `ChangeSystem` 同时看 `[Changed<T>]` 和 `[Removed<T>]`：持久化组件被写或者被删，都给根实体标 `Unsaved`。
- 带持久化组件的子孙，在同一趟里加上框架的清理组件 `Persisted(EntityId Root)`。新建和 `LoadEntitiesInto` 都算写入，所以加载后它会自动补上。
- 子孙被销毁（例如物品用掉了）时，清理系统按 `Root` 给根实体标 `Unsaved`，然后移除自己。所以删除也会写进记录。
- 记录里的类型按 Nino 类型 id 存。改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式。

### 迁移

- 保存整棵子树的所有组件，标了 `[Transient]` 的除外，默认都带。
- 托管类型的组件必须有 Nino 格式化器（分析器检查）。
- 等待中的请求、链接实体、待执行的延后事件随实例一起迁移。
  - 延后事件带着绝对到期时间，到达后按新节点的帧边界重新归格；已经过期的在第一帧执行。帧率不同时偏差也小于一帧；迁移本身花的时间不会被算进去两次。
- 卸载写记录时不保存待执行的延后事件：需要跨卸载保留的状态，写进 `[Persist]` 组件。
- 到达后的第一条邮件是 `Activated(MovedIn: true)`。
- 跨 shard 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。

### 热更

- Logic 工程放在可回收的 `AssemblyLoadContext` 里，里面有所有逻辑、分发跳板、Tick 的 job 结构、分发表和路由表。
- 每个 shard 在两帧之间：
  1. `world.RemoveTypes(旧上下文)`；
  2. 换上新的分发表、job 表和路由表。
- 所有 shard 都换完之后，旧上下文才卸载。
- 新版本如果改了某条客户端消息的处理种类，`wire.manifest` 比对后拒绝这次热更。
- 组件、消息、事件、状态类型、种类不热更，新增它们要走滚动升级。待执行的延后事件因此在热更前后都有处理方法。

### 滚动升级

- `wire.manifest` 记着每条消息的接收方、发送方、回复类型，客户端消息的路由，以及声明挂接的种类。
- 旧版本节点收到不认识的消息，返回 `NotHandled`。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点 id 在每个进程启动时随机生成，所以同名的节点重启后，不会被当成原来那个。
- 新旧版本的帧率可以不同：延后事件迁移时按时间换算（见上）。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。实例在新节点从记录激活，最多丢一个检查点间隔的改动。`Client` 看到节点离开视图，给 `Via` 发 `LinkLost`，链接重新建立 |
| 网关宕机 | 客户端断开，带令牌重连，得到新的 `Client`。各目标看到那个节点离开视图，执行断开 |
| 半开连接时客户端已经连到别处 | 新连接的挂接胜出，旧的 `Client` 被断开；旧连接再来挂接会收到 `Superseded` |
| 挂接失败 | 框架带抖动退避重试，超过次数断开 |
| `LinkFrom` 时没有客户端上下文 | 请求以 `NoClient` 失败 |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活 |
| 主体缺少组件参数 | 不调用处理方法；请求以 `Skipped` 失败 |
| 发问方在回信之前被卸载 | `ReplyTo` 的清理立即给调用方发失败回复；之后到达的回信丢弃 |
| 卸载时写记录失败 | 重试；这期间到达的消息由占位接住，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；目标节点宕机时，在途的单向消息可能丢失 |
| 邮箱满 | 发送方收到 `Busy`；客户端的消息由网关回绝；链接控制消息不受限 |
| 客户端读得太慢 | 断开（ADR-035） |
| 断开通知丢失 | 链路重连时核对，或者节点离开视图时，补做断开 |
| `[On<T>]` 抛出异常 | 请求失败，即时链里还没执行的事件作废。默认 `Resume`；`Reload` 时断开所有链接后从记录重新激活；`Client` 直接断开 |
| 一项的即时链超过 1,024 个 | 按 `[OnError]` 处理 |
| `[On<Tick>]` 抛出异常 | 这一帧跳过它剩下的部分，按种类的 `[OnError]` 处理，并在健康检查里报告 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 处理方法重新校验后作废 |

---

## 12. 完整例子：连接、进地图、移动、放技能、看见别人

### 用户写的

```csharp
// ── Game.Protocol ─────────────────────────────────────────────────────────────
[NinoType] public record struct TravelTo(long MapId)   : IFromClient<bool>;
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took
[NinoType] public record struct CastSkill(int Skill, long Target) : IFromClient;      // Target: an id from Nearby
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient { public List<NearbyEvent> Events = new(); }
[NinoType] public record struct NearbyEvent(long Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: a LinkId or an npc's id
```

```csharp
// ── Game.Server: kinds, servers, messages and events ─────────────────────────
public readonly partial record struct Player(long Id) : IKind<Player>;
public readonly partial record struct Map(long Id)    : IKind<Map>;                   // its tick rate is configuration

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Client>] public struct GatewayServer : IServer;

[NinoType, From<Player>] public record struct EnterMap(Player Player) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player) : IMessage<Map>;

[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;           // a cast's resolution, after the windup

// ── Game.Server: data ─────────────────────────────────────────────────────────
[Claims<Player>] public record struct PlayerClaims(long PlayerId) : IComponent;        // decoded from the token; the framework attaches
public record struct OnMap(Map Map) : IComponent;                                       // on a player that is on a map
public record struct Travelling(Map To) : IComponent;                                   // state kept while EnterMap is asked
[NinoType, Persist] public partial record struct Grid(int Size, int CellSize) : IComponent;   // on a map; tracked because persisted
public record struct Avatar(Map Map, Player Player) : IComponent, IIndexed<Avatar> { public Avatar Key => this; }
public record struct Visible : IComponent;
public record struct Position(Vector2 Value) : IComponent;
public record struct Velocity(Vector2 Value) : IComponent;
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Dead : IComponent;
public record struct Stunned : IComponent;
public record struct Casting(Scheduled<Strike> Strike) : IComponent;                    // "is casting" while Strike is pending
public record struct Shout(NearbyEvent Event);                                          // a map's outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetBuffer<Shout>();
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetVelocity(default)
        .SetSpeed(6).SetHealth(100).SetVisible();                         // a player's position comes from its steps
    public static readonly Prefab Npc = new Prefab().SetPosition(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(3).SetHealth(100).SetSteered().SetVisible();
}
```

```json
// configuration of the map nodes (or .WithTickRate(30) in the AppHost)
{ "DaServer": { "MapServer": { "TickRate": 30 } } }
```

```csharp
// ── Game.Server.Logic: players (attaching a client is the framework's, from [Claims<Player>]) ──
[Handles<Player>]
public static partial class Players
{
    [On<TravelTo>]                                                         // subject: this client's link on the player
    static void Travel(in TravelTo m, Self<Player> self, in ReplyTo<bool> reply)
    {
        if (self.Entity.TryGet<OnMap>(out var on)) self.Send(on.Map, new LeaveMap(self.Key));   // leave first: no double presence
        var map = new Map(m.MapId);
        self.Ask(map, new EnterMap(self.Key), state: new Travelling(map), reply: reply);        // the client's context goes along
    }

    [On<Answer<EnterMap, Travelling>>]                                     // subject: the player; the route is already on its way
    static void Entered(in Answer<EnterMap, Travelling> a, Entity me, Self<Player> self)
    {
        if (a.Ok) me.SetOnMap(a.State.To); else me.RemoveOnMap();
        self.Reply(in a.ReplyTo, a.Ok);                                    // nothing held after a LinkLost re-entry: no-op
    }

    [On<LinkLost>]                                                         // the map restarted: enter it again for the same client
    static void Lost(in LinkLost lost, Self<Player> self)
    {
        if (lost.Target.Is<Map>(out var map) && self.Entity.TryGet<OnMap>(out var on) && on.Map == map)
            self.Ask(map, new EnterMap(self.Key), state: new Travelling(map), onBehalfOf: lost.Client);
    }
}

// ── Game.Server.Logic: a map's membership ─────────────────────────────────────
[Handles<Map>]
public static partial class Maps
{
    [On<EnterMap>]                                                         // subject: the map
    static LinkId Enter(in EnterMap m, Self<Map> map, World world)
    {
        var key = new Avatar(map.Key, m.Player);
        if (world.AvatarIndex.TryFind(key, out var avatar)) return map.LinkFrom(avatar);   // same player again: the newest connection takes it over
        return map.LinkFrom(world.Instantiate(Prefabs.Avatar).Set(key));   // the client from the request's context; sets ChildOf(map)
    }

    [On<LeaveMap>]
    static void Leave(in LeaveMap m, Self<Map> map, World world)
    {
        if (world.AvatarIndex.TryFind(new Avatar(map.Key, m.Player), out var avatar)) map.Unlink(avatar);
    }

    [On<Unlinked>]                                                         // subject: the avatar; runs right after Leave, then it is destroyed
    static void Gone(in Unlinked u, in Linked link, Self<Map> map) =>
        map.Entity.Get<DynamicBuffer<Shout>>().Add(new Shout(new NearbyEvent(link.Id.Value, NearbyKind.Disappear, default, default)));
}

// ── Game.Server.Logic: movement ───────────────────────────────────────────────
[Handles<Map>]
public static partial class Movement
{
    [On<MoveTo>]                                                           // subject: this client's avatar; applied now, later items see it
    static void Move(in MoveTo m, Entity me, ref Position p, ref Velocity v, in Speed speed, Self<Map> map)
    {
        if (me.Has<Dead>()) return;
        if (!Steps.IsValid(p.Value, m.At, speed.Value)) { map.Push(me, new SnapBack(p.Value)); return; }
        p.Value = m.At;
        v.Value = m.Velocity;
    }
}

// ── Game.Server.Logic: combat ─────────────────────────────────────────────────
[Handles<Map>]
public static partial class Combat
{
    [On<CastSkill>]                                                        // subject: the caster's avatar
    static void Cast(in CastSkill m, Entity me, Self<Map> map, World world)
    {
        if (me.Has<Casting>() || me.Has<Stunned>() || me.Has<Dead>()) return;   // one cast at a time
        if (!Targets.TryFind(world, map, m.Target, out var target)) return;      // a LinkId or an npc id -> the entity on this map
        me.SetCasting(me.Send(new Strike(m.Skill, target.Id), after: Skills.Windup(m.Skill)));   // Zero resolves right after this handler
    }

    [On<Strike>]                                                           // subject: the caster; at its due frame, reads the target as it is now
    static void Resolve(in Strike s, Entity me, in Position at, World world)
    {
        me.RemoveCasting();
        if (me.Has<Dead>() || me.Has<Stunned>()) return;                  // died or got stunned during the windup
        if (!world.TryGet(s.Target, out var target) || target.Has<Dead>()) return;
        if (!Skills.InRange(s.Skill, at.Value, target.Get<Position>().Value)) return;   // out of range: dodged
        ref var hp = ref target.Get<Health>();
        hp.Value -= Skills.Damage(s.Skill);
        if (hp.Value <= 0) target.SetDead();                               // structural change last: hp is not used after it
    }
}

// ── Game.Server.Logic: every frame, after the ordered phase, in declaration order ──
[Handles<Map>]
public static partial class Simulation
{
    [On<Tick>, With<Steered>]                                              // components only: a parallel job
    static void Steer(in Position p, in Destination d, in Speed s, ref Velocity v) =>
        v.Value = Steering.Toward(p.Value, d.Target, s.Value);

    [On<Tick>, With<Steered>]                                              // whole columns: SIMD; Vectors is a user helper
    static void Integrate(Span<Position> positions, ReadOnlySpan<Velocity> velocities, in Clock clock) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities),
            (float)clock.DeltaTime.TotalSeconds);

    [On<Tick>(Serial)]                                                     // Self<Map>: once per map; components bind to the map
    static void Broadcast(Self<Map> map, in Grid grid, ref DynamicBuffer<Shout> outbox, World world) =>
        Aoi.Run(world, map, in grid, ref outbox);
}
```

被打断的前摇：眩晕的处理方法里 `if (me.TryGet<Casting>(out var c)) { c.Strike.Cancel(); me.RemoveCasting(); }`。

`Aoi.Run` 处理一张地图：
1. 遍历 `world.Children(map.Entity)`，每帧按位置把实体分进格子；格子不是组件，所以跨格不需要结构变更。
2. 只看带 `Confirmed` 的 Avatar。
3. 一个客户端跨了格，或者它的 Avatar 第一次带上 `Confirmed`（包括迁移来的、被新连接接管的）：
   - 更新它订阅的 3×3 `ClientGroup`；
   - `map.Push` 一份它看得到的人的完整快照。
4. 速度和上次推送的不同的实体写成 `Moved` 事件（上次的速度存在 AOI 的格子里），和发件箱里已有的事件一起按格子合并。
5. 每个有内容的格子发一帧 `Nearby`，然后清空发件箱。

事件里的 `Id`：Avatar 用 `LinkId`，NPC 用一个不会与之相撞的编号（例如最高位置 1）。`Targets.TryFind` 按同样的规则查回实体。

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

// [Persist] implies change tracking
partial record struct Grid : ITrackChanges;

// [Claims<Player>]: which player a client attaches to
file static class PlayerClaimsRegistration
{
    [ModuleInitializer]
    internal static void Register() =>
        Claims.Register<PlayerClaims, Player>(static (in PlayerClaims c) => new Player(c.PlayerId));
}
```

```csharp
// <auto-generated/> Game.Server: typed sends, only on the declared senders; one overload per shape, no default-argument inference
public static class PlayerSends
{
    public static void Ask<TState>(this in Self<Player> self, Map to, in EnterMap message, in TState state)
        where TState : struct, IComponent =>
        self.Runtime.Ask(in to, EnterMap.MessageId, in message, in state, reply: default, ClientContext.Current);

    public static void Ask<TState>(this in Self<Player> self, Map to, in EnterMap message, in TState state, in ReplyTo<bool> reply)
        where TState : struct, IComponent =>
        self.Runtime.Ask(in to, EnterMap.MessageId, in message, in state, in reply, ClientContext.Current);

    public static void Ask<TState>(this in Self<Player> self, Map to, in EnterMap message, in TState state, in From onBehalfOf)
        where TState : struct, IComponent =>
        self.Runtime.Ask(in to, EnterMap.MessageId, in message, in state, reply: default, ClientContext.Of(in onBehalfOf));

    public static void Send(this in Self<Player> self, Map to, in LeaveMap message) =>
        self.Runtime.Send(in to, LeaveMap.MessageId, in message);
}
// There is no Self<Map>.Ask(…, EnterMap): a map cannot send it, so such a call does not compile.
// Entity.Send<T>(in T, TimeSpan after) where T : struct, IEvent is the framework's own generic method.
```

```csharp
// <auto-generated/> Game.Server.Logic: [On<T>] trampolines inside each partial class (they can reach private handlers)
// root = the domain's root; subject = the entity the binding rule picks
partial class Maps
{
    internal static void On_Enter(World world, Entity root, Entity subject, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<EnterMap>();
        outcome.Reply(Enter(in m, Self<Map>.Of(root), world));
    }

    internal static void On_Gone(World world, Entity root, Entity subject, in Envelope envelope, ref Outcome outcome)
    {
        var u = envelope.Read<Unlinked>();
        if (!subject.TryRead<Linked>(out var link)) { outcome.Skipped(); return; }
        Gone(in u, in link, Self<Map>.Of(root));
    }
}

partial class Movement
{
    internal static void On_Move(World world, Entity root, Entity subject, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<MoveTo>();
        if (!subject.TryRead<Position>(out var p) || !subject.TryRead<Velocity>(out var v) || !subject.TryRead<Speed>(out var speed))
        { outcome.Skipped(); return; }                                     // the subject lacks a component: not called
        Move(in m, subject, ref p, ref v, in speed, Self<Map>.Of(root));
        ref var ps = ref subject.TryGetRef<Position>(out var hasP); if (hasP) ps = p;   // written back only if it still exists
        ref var vs = ref subject.TryGetRef<Velocity>(out var hasV); if (hasV) vs = v;
    }
}

partial class Combat
{
    internal static void On_Resolve(World world, Entity root, Entity subject, in Envelope envelope, ref Outcome outcome)
    {
        var s = envelope.Read<Strike>();                                   // subject: the entity the event was sent to
        if (!subject.TryRead<Position>(out var at)) { outcome.Skipped(); return; }
        Resolve(in s, subject, in at, world);                              // RemoveCasting is a structural change: locals stay valid
    }
}

partial class Players
{
    internal static void On_Entered(World world, Entity root, Entity subject, in Envelope envelope, ref Outcome outcome)
    {
        var a = envelope.ReadAnswer<EnterMap, Travelling>();               // reply, status, state and held token from the waiting entity
        Entered(in a, subject, Self<Player>.Of(root));                     // the runtime sent the Route before this call
    }
}

partial class Simulation
{
    // components only: a job struct run by EasyEcs's parallel API, no closure, no allocation
    internal struct Steer_Job : IForEachJob<Position, Destination, Speed, Velocity>
    {
        public void Execute(in Position p, in Destination d, in Speed s, ref Velocity v) => Steer(in p, in d, in s, ref v);
    }

    internal static void On_Steer(World world) =>
        world.Query<Steer_Query>().ParallelForEach(new Steer_Job());       // Steer_Query: in a Map domain, With<Steered>, the four components

    internal static void On_Integrate(World world)
    {
        var clock = world.Singleton<Clock>();
        world.Query<Integrate_Query>().ParallelForEachChunk(new Integrate_Job { Clock = clock });   // whole columns, one call per chunk
    }

    internal struct Integrate_Job : IChunkJob
    {
        public Clock Clock;
        public void Execute(in Chunk chunk) => Integrate(chunk.Span<Position>(), chunk.ReadOnlySpan<Velocity>(), in Clock);
    }

    internal static void On_Broadcast(World world, Entity root)            // [On<Tick>(Serial)] with Self<Map>: once per map
    {
        var grid = root.Read<Grid>();
        root.EnsureBuffer<Shout>();
        Broadcast(Self<Map>.Of(root), in grid, ref root.Get<DynamicBuffer<Shout>>(), world);
    }
}
```

```csharp
// <auto-generated/> Game.Server.Logic: dispatch per kind, tick order, client routes; staged on load, adopted between frames
file static class Dispatch_Map
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Map>(static table => table
        .Request(EnterMap.MessageId, Maps.On_Enter)
        .Message(LeaveMap.MessageId, Maps.On_Leave)
        .Message(Unlinked.MessageId, Maps.On_Gone)
        .Client(MoveTo.MessageId, Movement.On_Move)                        // subject: the link entity
        .Client(CastSkill.MessageId, Combat.On_Cast)
        .Event(Strike.EventId, Combat.On_Resolve)
        .Tick(Simulation.On_Steer)                                         // parallel, declaration order
        .Tick(Simulation.On_Integrate)
        .TickSerialPerDomain(Simulation.On_Broadcast));                    // built once when staged
}

file static class Dispatch_Player
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Player>(static table => table
        .Client(TravelTo.MessageId, Players.On_Travel)
        .Answer(EnterMap.MessageId, Travelling.TypeId, Players.On_Entered)    // keyed by (request, state)
        .Message(LinkLost.MessageId, Players.On_Lost));                    // no Tick: Player has no frames
}

file static class ClientRoutes
{
    [ModuleInitializer]
    internal static void Register() => Routes.Stage(static table => table
        .Route(TravelTo.MessageId, Player.KindId)                         // [Handles<Player>] Players.Travel
        .Route(MoveTo.MessageId, Map.KindId)                              // [Handles<Map>] Movement.Move
        .Route(CastSkill.MessageId, Map.KindId));                         // [Handles<Map>] Combat.Cast
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
// IServerType declares `static abstract string Name { get; }`; AddNodes<T>() where T : IServerType returns NodeGroup<T>;
// WithTickRate(hz) writes DaServer:<Name>:TickRate into the group's configuration.
```

```text
# wire.manifest (excerpt, committed)
kind    Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer
kind    Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer
claims  Game.Server.PlayerClaims  -> Player
client  Game.Protocol.TravelTo   -> Player  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
server  Game.Server.EnterMap    Player -> Map     reply LinkId
server  Game.Server.LeaveMap    Player -> Map
event   Game.Server.Strike      Map
state   Game.Server.Travelling  Player  answers EnterMap
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
```

### 运行时的路径

```
① 客户端连 gate-0      框架在 gate-0 激活 Client(c1)，加上 PlayerClaims；第一条邮件是 Activated
② 挂接                 [Claims<Player>]：框架向 Player 42@game-1 挂接；Player 建 c1 的链接实体（最新的胜出），回复 L1
                      Client 写路由 (Player 42, L1, Via 自己)，发 LinkConfirmed(L1)；推 Ready，放出排队的消息
③ TravelTo(7)         Client(c1)@gate-0 → 路由 → Player 42：主体是 c1 在 Player 上的链接实体；Player 不按帧走，到了就处理
④ Ask                 Player 42 → Map 7@map-0：EnterMap，信封带着 c1 的上下文；Map 7 建 Avatar，LinkFrom，回复 L2
⑤ Answer              回复类型是 LinkId：框架先把 Route(Map 7, L2, Via Player 42) 发给 Client(c1)，再调用 Entered
                      Entered：SetOnMap，Reply(true) → Client(c1)：路由已经写好，再把回复交给客户端
⑥ LinkConfirmed(L2)   Client → Map 7：Avatar 带上 Confirmed；下一帧的 Broadcast 推完整快照
⑦ MoveTo              Client(c1) → 路由 → Map 7 的邮箱；下一帧的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Send(Strike, after: 前摇)，进到期帧那一格；Casting 拒绝第二次施法
⑨ Strike 到期         所在那一帧的有序阶段，和消息按时间归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ 每 33 ms 一帧（30 Hz） 有序阶段（消息和到期事件按 (时间, 序号) 归并）→ Tick（Steer、Integrate 按列并行；Broadcast 每张地图串行）
                      Nearby → 有观察者的网关 → 各个连接
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：没有对应的方法。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个种类处理。
- 一个请求有两个处理方法；`Answer` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`。
- 同一个（请求, 状态）的 `Ask` 交出不同类型的 `ReplyTo<T>`，或者 `self.Reply(a.ReplyTo, …)` 的值类型不对。
- 复制 `ReplyTo<T>`。
- 用户代码写种类组件，或者把种类组件放进 prefab、`CreateEntity`。
- 方法体里的 `ref` 跨过这个实体的结构变更使用（DAS022）。
- 两个可写引用可能指向同一个组件：`ref T` 参数加上对另一个实体的 `Get<T>()`，而那个实体可能就是主体。
- 并行的 `[On<Tick>]` 取了 `Entity`、`Self<K>` 或 `World`（要写 `[On<Tick>(Serial)]`）；在里面发消息、发事件、做结构变更。结构变更和写有索引的组件，EasyEcs 另有 EECS013、EECS015。
- `[After<TClass>]` 成环。
- 把 `IEvent` 发给 `Self<K>`（本地事件发给实体），或者把服务器之间的消息发给实体。
- 事件类型不是 `[NinoType]`，或者没有实现 `IEvent`。
- `[Claims<K>]` 的字段和 K 的键形状不同；一个种类有两个 `[Claims<K>]`。
- Protocol 里出现种类或者服务器之间的消息；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- 种类的键超过 16 字节，或者不是非托管类型。
- `[Persist]` 组件不是 `partial`。
- 组件、事件、状态类型声明在 Logic。
- 要迁移的托管组件没有 Nino 格式化器。

**启动时检查**：配置的服务器类型不存在；同一种服务器上各种类的帧率不能都整除最快的那个。

**删掉的东西**：
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调、`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded、自研的记录格式、`ActorKey`；
- `[Receive]`、`[Receive<K>]`、用 `EcsSystem` 和 `[ForEach]` 写逻辑、`[Every]` 系统组、`self.After`、作为业务入口的 `TimerSystem`/`MailSystem`；
- `[Frame(ms)]`、`Raise`、`SendAfter`、`[On<T, K>]`、`[Link]`、`[Of(…)]`、`[Pending]`、`Maybe<T>`、`AskFor`、`world.Route`、`routeFrom:`、用户写的 `Attach`、用户代码里的 `ITrackChanges`、空间区域；
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

---

## 14. 待拍板

1. **采用 v21**：每个域按 `(时间, 序号)` 执行有序阶段，再跑 Tick；`Send` 一个动词；参数绑定到主体；`[Handles<K>]`；`Answer<TReq, TState>`；`[Claims<K>]` 自动挂接、自动写路由；并行来自很多域和并行的 Tick，不做空间分区。
2. **帧率写在哪**（第 9 节目前按 B 写）：
   - A. 写在种类上，只此一处：`[Frame(Hz = 30)] partial record struct Map(long Id) : IKind<Map>;`，不写为 20 Hz。帧率是玩法参数：跟着种类走，测试和线上一致，改动出现在 `wire.manifest` 的差异里，只能滚动升级生效（推荐）；
   - B. 配置，按服务器类型（`DaServer:MapServer:TickRate`），AppHost 的 `.WithTickRate` 写默认值。运维可调，但同一种服务器的副本可能被改成不同的值，`TestCluster` 也看不到 AppHost 的值。
   - 两种都一样的：延后事件存绝对时间；客户端从链接确认和第一份快照拿到帧长、帧号和服务器时间，换图时重新推送；节点在成员信息里公布帧率，同一版本不一致时报警。
3. **本地事件**：
   - 保留单独的 `IEvent` 标记，本地事件和服务器之间的消息一眼分开，分析器能查发错的地方（当前写法）；
   - 任何消息类型都可以在本域发给实体。
4. **`[On<Tick>(Serial)]` 的写法**，备选：`[On<Tick>(Serial)]`（`TickMode.Serial` 经 `global using static`）、`[On<Tick>, Serial]`、`[OnSerial<Tick>]`。
