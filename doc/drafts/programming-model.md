# DaServer 新编程模型设计（草案 v20，未实现，迭代中）

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
| `Game.Server` | 种类、服务器之间的消息、延后的事件、组件、prefab、服务器类型，以及五行的 Program.cs | 服务端 |
| `Game.Server.Logic` | 所有逻辑：`[On<T>]` 方法，以及生成的分发表、Tick 的 job 结构、客户端路由表 | 服务端；放在可回收的 `AssemblyLoadContext` 里，可以热更 |

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
| 帧长 | 写在种类上：`[Frame(50)] partial record struct Map(long Id) : IKind<Map>;` | Server |
| 延后的事件 | `[NinoType] record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 处理消息、生命周期、回信、事件 | `static partial class` 里的 `[On<T>]` 方法 | Logic |
| 客户端消息的接收方 | `[On<MoveTo, Map>]`：第二个类型参数是接收它的种类 | Logic |
| 延后执行 | `self.Raise(e)`（当前处理方法之后，同一个事务）；`self.Raise(e, after: n)` 返回 `Scheduled<T>`（n 帧后） | Logic |
| 每帧的逻辑 | `[On<Tick>]`，参数是组件或列（`Span<T>`） | Logic |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `Client` | 内置的种类：每个连上来的客户端，在接受客户端的服务器上有一个实体，键是 `ClientId` |
| `Self<K>` | 正在处理消息的实体自己：强类型的键，以及发消息的入口 |
| `KindKey` | 任意种类的地址（种类 id 加最多 16 字节的键），用 `Is<T>(out T key)` 取出强类型的键 |
| `LinkId`、`Linked`、`Confirmed` | 客户端在某个目标上的链接；目标上的链接实体，例如地图上的 Avatar |
| `ReplyTo<T>`、`Answer<TReq, TRep>`、`[Pending]`、`[Link]` | 晚些回复；等别的实体的结果；参数绑定到哪个实体 |
| `Activated`、`Unlinked`、`LinkLost`、`Unloading` | 生命周期消息：框架发给实体自己，用 `[On<T>]` 处理 |
| `Tick` | 每一帧的事件（`Frame`、`DeltaTime`），用 `[On<Tick>]` 处理 |
| `Raise`、`Scheduled<T>` | 延后的事件：n = 0 紧接着执行，n > 0 在 n 帧后执行；`Scheduled<T>` 可以存进组件，用来取消或表示"正在进行" |
| `[Of(nameof(E.Field))]` | 参数绑定到事件或消息某个字段指向的实体，例如技能的目标 |
| 空间区域 | 每帧按位置把一张地图分成互不影响的区域，区域之间并行；用户不写 |
| `IdleAfter`、`Clock` | 空闲回收；shard 的时钟单例（`Now`、`Frame`、`DeltaTime`） |
| `ClientGroup` | 一次序列化、按网关扇出的广播组 |

### 谁做什么

| 机制 | EasyEcs 的功能 | 我们的特性和生成器 | 运行时 |
|---|---|---|---|
| 按键找实体 | 种类组件的值索引 | 为 `partial` 种类生成 `Key => this` 和种类 id 常量 | 收到消息时查索引 |
| 只处理一类实体 | `[With<Map>]` | — | — |
| 事件的顺序 | — | — | 每个域按 `(时间, 序号)` 逐个执行消息、Tick、延后的事件 |
| 消息处理 | — | `[On<T>]` 生成的分发跳板 | 逐个事件调用，逐个隔离异常 |
| 每帧的逻辑 | 并行 API、批量方法、`[Changed<T>]` | `[On<Tick>]` 生成 job 结构 | 帧开始时按列并行执行 |
| 帧长 | — | `[Frame(ms)]` | 同一帧长的域共用帧边界 |
| 延后的事件 | 有序值索引的 `Range`、`ChildOf` | `Raise`、`Scheduled<T>`、`IEvent` | 到期时按 `(时间, 序号)` 执行；发起者销毁时一起消失 |
| 空间区域 | 并行 API | — | 每帧按 AOI 网格分区，区域内串行、区域之间并行；调试模式核对全域逐个执行的结果 |
| 发送方向 | — | `[From<K>]` 生成 `Self<K>` 上的方法 | 发送、回复、超时 |
| 客户端消息的路由 | — | `[On<T, K>]` 生成路由表 | `Client` 实体转发 |
| 等待、回复 | `ChildOf`、`ICleanupComponent` | `[Pending]`、`ReplyTo<T>` | 调用 id、框架内部的毫秒期限、期限传递 |
| 链接 | 子实体、值索引 | `[Link]` 参数绑定 | 确认、核对、重连 |
| 生命周期 | — | 用户写 `[On<Activated>]` 等 | 投递生命周期消息 |
| 一张地图的实体 | `ChildOf`、`world.Children(map)` | — | — |
| 只写改过的 | `ITrackChanges`、`[Changed<T>]`、`[Removed<T>]`、`ICleanupComponent` | `[Persist]` | `ChangeSystem`、`CheckpointSystem` |
| 记录、迁移 | `ComponentSet`、`SaveEntities`、`LoadEntitiesInto` | `[Persist]`、`[Transient]` | 读写存储 |
| 版本兼容 | Nino 类型 id、`[NinoFormerName]` | `wire.manifest` | 滚动升级 |
| 热更 | `RemoveTypes`、可回收的上下文 | 分发表、job 结构、路由表在 Logic 程序集里 | 两帧之间切换 |
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

### 执行模型（v20）：每个域是一个离散事件模拟

**一个域（一张地图）里发生的一切，都是带时间戳的事件：**

| 事件 | 时间戳 | 例子 |
|---|---|---|
| 客户端或别的实体发来的消息 | 进入本域的时刻 | 移动一步、放技能、交易 |
| 延后的事件 `self.Raise(e, after: n)` | 发起时刻 + n × 帧长（精确时间，不取整） | 前摇 3 帧后的结算、5 秒后 buff 到期 |
| 帧 `Tick` | 每一帧的时刻 | 移动推进、buff 跳伤害、回复 |

**语义只有一条：所有事件按 `(时间, 序号)` 排成一条线，逐个"立即"执行。** 每个事件看到的，正好是它之前所有事件的全部效果。

- 序号是本域的计数器，在事件进入本域（消息出队、`Raise` 调用、帧开始）时分配，保证同一时刻的事件也有确定的先后。
- `Raise(e)`（n = 0）：在当前处理方法返回后、下一个事件之前执行，属于同一个事务。后面的消息一定看到它的结果。每条消息最多连锁 1,024 个，超出按 `[OnError]` 处理。
- `Raise(e, after: n)` 返回 `Scheduled<T>`，可以存进组件，用来取消（前摇被打断），也可以表示"正在施法"，用于拒绝第二次施法。发起者被销毁时，它的待执行事件一起消失。
- 到期时要重新校验：施法者还活着吗？被眩晕了吗？目标还在吗？

**两个例子：**
- **前摇和闪避**：A 在 t 时刻放前摇 3 帧的攻击，结算事件在 t + 3 帧执行，读的是那一刻 B 的位置。B 在前摇期间走开了，就是闪避。
- **同一帧互相攻击**：A 打 B 的结算在 t₁，B 打 A 的在 t₂。如果 t₁ < t₂，A 先结算，B 被打死；轮到 B 的结算时重新校验，发现施法者已死，作废。同一时刻的按序号裁决。

**并行从哪里来（来自并行离散事件模拟 PDES 的研究，见 `research-ordering.md` 第 8 节）：**

| 来源 | 条件 | 做法 |
|---|---|---|
| 帧推进 | 只读写实体自己的组件 | 帧开始时作为一个事件，按列并行、SIMD |
| 一张地图内的空间分区 | 两个区域相距超过"作用距离 + 2 × 最大速度 × 一帧"，这一帧内彼此不可能影响 | 每帧按位置把地图分成区域（AOI 网格本来就算好了）；在 [t, t + 一帧) 这个窗口里，各区域按 `(时间, 序号)` 串行处理自己的事件，区域之间并行；靠得太近的区域在这一帧合并 |
| 很多个域 | 天然独立 | 每核一个 shard，跑很多张地图 |

- **结果总是等于全域逐个执行**：区域之间在这一帧内不可能相互影响，这是 PDES 里"前瞻"（lookahead）的保证。
- **跨区域的效果至少要隔一帧**（发往别的地图的消息、飞行时间不少于一帧的弹道），在帧之间投递。这条设计规则由框架检查。
- **前摇不提供前瞻**：结算那一刻读目标的状态，延迟是 0。真正能让两件事并行的，是"它们在空间上离得够远"。之前"有前摇就能并行"的说法是错的，已更正。
- **代价**：每帧一次屏障；消息要等它所在的帧窗口结束才处理，延迟最多一帧（30 Hz 时最多约 33 ms）。只有一个区域、不需要并行的域，可以不开窗口，消息到了就处理。

**不采用的：**
- 乐观执行（Time Warp 回滚）：保存状态或写反向计算的开销和一个约 1 µs 的事件本身差不多，而且回滚前已经推给客户端的结果收不回来。
- 按消息类型分批：会打乱 `(时间, 序号)`。只有帧推进这种"只写自己"的才按列批量处理。
- 把命令排到下一帧统一执行：等于把效果挪后，会出现"发包时打得到、执行时人跑了"。

**用户写的只有三种，全部是 `[On<T>]`：**

```csharp
[On<CastSkill, Map>]                              // a message: validate, change instant state, schedule
static void Cast(in CastSkill m, [Link] Entity me, [Link] Maybe<Casting> casting, [Link] Maybe<Stunned> stunned)
{
    if (casting.Exists || stunned.Exists) return;
    me.SetCasting(me.Raise(new Strike(m.Skill, m.Target), after: Skills.Windup(m.Skill)));   // n can be 0
}

[On<Strike>]                                      // the resolution, now or n frames later; re-validates
static void Resolve(in Strike s, Entity me, [Of(nameof(Strike.Target))] in Position target, in Position mine,
    [Of(nameof(Strike.Target))] ref Health hp)
{
    me.RemoveCasting();
    if (me.Has<Dead>() || me.Has<Stunned>()) return;
    if (Skills.InRange(s.Skill, mine.Value, target.Value)) hp.Value -= Skills.Damage(s.Skill);   // dodged otherwise
}

[On<Tick>]                                        // time passing: own columns only, parallel, SIMD
static void Advance(in Tick t, Span<Position> p, ReadOnlySpan<Velocity> v) { /* integrate */ }
```

用户不需要判断"立即还是延后"（由数据里的 n 决定），也不需要判断"串行还是并行"（由空间位置决定）。`[Of(…)]` 只是读写目标实体的写法，不影响正确性。

**用户能看到的反馈：**
- 每个域的指标：每帧区域数、最大区域占比（人群扎堆时会变大）、串行占比；
- 调试模式：把每帧改成全域逐个执行，核对结果是否完全一致。

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
- 只处理地图的 Tick 写 `[With<Map>]`；
- 处理方法里取 `in Map self`，就知道自己是谁。

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
  - 处理这条消息的 `[On<MoveTo, Map>]` 第二个类型参数写的是哪个种类，消息就转发给哪个种类；
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

逻辑只有一种写法：`static partial class` 里的 `[On<T>]` 方法。

### `[On<T>]` 的 T

| T | 是什么 | 例子 |
|---|---|---|
| 服务器之间的消息 | 接收方由消息的 `IMessage<K>`、`IRequest<K, R>` 决定 | `[On<EnterMap>]` |
| 客户端消息 | 第二个类型参数是接收它的种类，生成器据此写路由表 | `[On<MoveTo, Map>]` |
| 生命周期消息 | 框架发给实体自己 | `[On<Activated>]`、`[On<Unlinked>]`、`[On<LinkLost>]`、`[On<Unloading>]` |
| 回信 | 等别的实体的结果（第 8 节） | `[On<Answer<EnterMap, LinkId>>]` |
| 延后的事件 | `IEvent`，由 `Raise` 发起 | `[On<Strike>]` |
| 帧 | 每一帧一次（第 9 节） | `[On<Tick>]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个接收种类。
- 事件类型和消息一样声明在 Server 工程，带 `[NinoType]`：它们会随实例迁移，也不能随 Logic 热更消失。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Self<K>` | 目标实体自己（域的根，例如地图） |
| `Entity`、组件（`ref`/`in`） | **目标实体**（默认）；事件绑定到发起它的实体（`entity.Raise` 的那个） |
| `[Link] Entity`、`[Link]` 组件 | 这个客户端在目标上的链接实体，例如这个玩家的 Avatar。只用于客户端消息和 `Unlinked` |
| `[Of(nameof(E.Field))]` 组件 | 事件或消息里这个 `EntityId` 字段指向的实体，例如技能的目标 |
| `Maybe<T>` | 可能没有的组件 |
| `[Pending]` 组件 | 处理 `Answer` 时，等待实体上的数据 |
| `in ReplyTo<T>` | 要晚些回复时取；处理 `Answer` 时，取的是等待实体上存的令牌 |
| `From` | 发送方 |
| `Span<T>`、`ReadOnlySpan<T>` | 只用于 `[On<Tick>]`：一整列 |
| 单例 | 本 shard 的单例 |

- `[Of]` 指向的实体已经不在，或者缺少要求的组件：跳板不调用处理方法，等同于重新校验失败。

**引用不会失效**：处理方法里做了结构变更（建实体、`Ask`、`Link`、`Raise` 等），参数仍然可用。
- 生成的跳板把 `in` 参数复制成局部变量。
- `ref` 参数传入局部变量，调用结束后用 `TryGetRef` 写回。组件如果已被处理方法删掉，就不写回，不会把它加回来。
- `DynamicBuffer` 参数直接按 `ref` 传入：它是指向存储的句柄，跳板只在调用前确保存储已经建好，之后不写回。
- 分析器检查：取了 `ref T` 的处理方法，不能再用别的方式写同一个实体上的 `T`，否则写回会覆盖那次写入。

### 延后执行：`Raise`

- **`self.Raise(e)`、`entity.Raise(e)`**（n = 0）：当前处理方法返回后、下一个事件之前执行，属于同一个事务。每条消息最多连锁 1,024 个。
- **`Raise(e, after: n)`**：n 帧后执行，返回 `Scheduled<T>`。
  - 存进组件，可以 `Cancel()`（前摇被打断），也可以看 `Pending` 判断"正在施法"。
  - 发起者被销毁时，它的待执行事件一起消失。
- 只能 `Raise` 给本域的实体。影响别的域用消息。
- 到期时要重新校验：施法者还活着吗？目标还在、还够得着吗？
- 用户不判断"立即还是延后"：n 来自数据，0 也是合法值。

### 每帧：`[On<Tick>]`

- 参数只有组件或列的 Tick 处理方法，只读写实体自己的组件。生成器为它写 job 结构，运行时用 EasyEcs 的并行 API 按列执行，可以 SIMD。
  - 不能发消息、`Raise`、做结构变更（分析器检查；EasyEcs 另有 EECS013、EECS015）。
- 取了 `Entity`、`World` 或 `Self<K>` 的 Tick 处理方法串行执行：
  - 取 `Entity`：逐个实体；
  - 取 `Self<K>`：每个域一次，例如一张地图的 AOI。
- 先后：同一个类里按声明顺序；跨类用 `[After<TClass>]`。
- `[With<T>]` 收窄到一类实体。

### 一帧里的顺序

一个域里所有事件按 `(时间, 序号)` 逐个执行（第 1 节）。落到每一帧 [t, t + 帧长)：

1. **消息**：帧边界 t 之前进入本域的消息，时间戳就是 t，按序号逐条执行。每条之后，紧接着执行它连锁的 `Raise(e)`。
2. **Tick**：时刻 t 的 `Tick`，同一时刻里排第一。先跑并行的，再跑串行的。
3. **延后的事件**：时间落在 [t, t + 帧长) 的，按 `(时间, 序号)` 执行。

**空间区域**（第 1 节）：
- 帧开始时按位置把域分成区域：两个区域相距超过"作用距离 + 2 × 最大速度 × 一帧"，这一帧内互不影响；靠得太近的合并。
- 第 1 步和第 3 步：每个区域按 `(时间, 序号)` 串行处理自己的事件，区域之间并行。第 2 步的并行 Tick 本来就按列并行。
- 绑定不到位置的事件（发给地图本身的 `EnterMap`、`LeaveMap`）：在分区之前、帧开始时串行执行。它们和别的消息时间戳相同（都是 t），所以把它们的序号排在前面仍然符合 `(时间, 序号)`；新进来的实体随后参与分区。不能因为有人进图就让整帧不分区，否则人多的地图每帧都退回串行。
- 只有一个区域、不需要并行的域可以关掉窗口：消息到了就处理，Tick 和延后的事件到时间就执行，顺序规则不变。

**其他：**
- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理。
- 每帧之后，框架销毁已经回复的等待实体，以及已经写完记录的实例。
- **分发表、job 表和路由表**在加载时只暂存，各个 shard 在两帧之间换上。表在暂存时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。

### 处理方法抛出异常

- 运行时逐个事件捕获异常：这条消息的请求以失败结束，它连锁的、还没执行的 `Raise(e)` 作废。
- 之后怎么办，看种类上的 `[OnError]`：
  - `Resume`（默认）：实体保持原样，接着处理下一个事件；
  - `Reload`：照正常流程卸载（第 6 节），之后从记录重新激活。
- 一条消息连锁超过 1,024 个 `Raise(e)`，也按 `[OnError]` 处理。
- **`[On<Activated>]` 抛出异常，算作激活失败**：
  - 排队的请求都以失败结束；
  - 实体不写记录就销毁；
  - 下一条消息到来时，按退避再激活一次。
  - 这样不会留下初始化了一半的实体。
- `Client` 种类抛出异常时，断开这个客户端。

---

## 6. 生命周期

生命周期事件是框架发给实体自己的消息，用 `[On<T>]` 处理，和其他事件排在同一条 `(时间, 序号)` 线上。

| 消息 | 何时 | 常见用途 |
|---|---|---|
| `Activated(bool Restored, bool MovedIn)` | 实例激活后的第一条邮件 | 初始化 |
| `Unlinked` | 链接实体被断开时，由 `world.Unlink` 当场执行；`[Link]` 绑定到这个链接实体 | 广播离开 |
| `LinkLost(KindKey Target, From Client)` | 一条路由失效，发给当初建立它的实体（`Via`） | 重新建立链接 |
| `Unloading` | 卸载前的最后一条邮件 | 收尾 |

**占位**：一个键在"还没有实体"或"实体正在卸载"时，框架用一个占位接住发给它的邮件。

| 状态 | 怎么进入 | 发生什么 |
|---|---|---|
| 加载中 | 第一条消息到达 | 占位接住邮件，异步读记录。这时还没有实体，Tick 看不到它 |
| 激活 | 记录到了，或者没有记录 | 按 prefab 建实体，装上记录；邮箱里第一条是 `Activated`，后面才是占位接住的邮件 |
| 运行 | — | 消息、Tick、延后的事件按 `(时间, 序号)` 执行（第 5 节） |
| 卸载 | 空闲超过 `IdleAfter`（有链接实体时不算空闲），或者调用 `world.Unload(e)` | 依次：<br>1. 决定卸载的那一刻起，新到的邮件由占位接住，所以 `Unloading` 之后不会再有别的邮件；<br>2. 对每个链接实体执行 `world.Unlink`，通知对应的 `Client`，只发不等；<br>3. 投递 `Unloading`；之后待执行的延后事件随实体一起消失（第 9 节）；<br>4. 写记录；<br>5. 写成功后销毁实体。占位接住的邮件激活新实例，新实例读到的是刚写好的记录 |

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
- **扩展**：用 `[Spawns<Client>]` 的 prefab 给它加组件，例如令牌桶；在 Logic 里给它写 `[On<T>]` 方法。
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
- `Client` 的 `[On<Activated>]`，根据声明向 Player 发 `Attach`，等待数据是 `Attempt(1)`。
- 失败（超时、`NoOwner`、`Busy`）时，用 `self.SendAfter(退避, …)` 重试，`Attempt` 加一，退避带随机抖动；超过 N 次就断开客户端。
  - `SendAfter` 的延迟是毫秒，由框架内部的期限实现，到期时作为消息发出；它不是游戏逻辑的 `Raise`。
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
  1. 执行 `[On<Unlinked>]`；
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
  - 期限：框架内部的毫秒期限（第 9 节）；
  - 可选的 `ReplyTo<T>`；
  - `AskFor` 时，还带上客户端的连接信息。
  - 生成器为有没有 `pending`、有没有 `reply` 各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(种类, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Answer` 邮件**，用 `[On<Answer<TReq, TRep>>]` 处理，按（请求类型, 等待数据类型）分发：
  - 标了 `[Pending]` 的组件参数和 `in ReplyTo<T>` 绑定到等待实体，其余组件参数绑定到发问方；
  - 处理完，等待实体由框架删除。
- **超时**：期限到了，框架投递一个 `Ok = false` 的 `Answer`。它和别的消息一样进入本域，排进 `(时间, 序号)`。
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

**帧：**
- 每个域按帧走。帧长写在种类上：`[Frame(50)]`，不写默认 50 ms。
- 一个 shard 上帧长相同的域共用帧边界，所以 Tick 能一次按列跑完这个 shard 上所有的域。
- `Tick` 带 `Frame`（帧号）和 `DeltaTime`（帧长，秒）。
- `Clock` 是框架给每个 shard 的单例：`Now` 是当前事件的时间，`Frame`、`DeltaTime` 同 `Tick`。

**延后的事件：**
- `Raise(e, after: n)` 的时间 = 发起时刻 + n × 帧长，是精确时间，不取整到帧。
- 待执行的事件是发起实体的子实体，按 `(时间, 序号)` 建有序值索引；每帧用 `Range` 取出这一帧到期的。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- `Scheduled<T>` 是这个子实体的句柄：`Cancel()` 销毁它；`Pending` 在执行或取消后变成 `false`。
- 周期性的逻辑写 `[On<Tick>]`；稀疏的周期（例如每 20 帧跳一次伤害）在事件处理方法里再 `Raise` 下一次。

**框架内部的期限：**
- 请求超时、链接确认期限、`SendAfter` 的退避，用的是框架内部的 `Due(long UnixMs)`：绝对的 UTC 毫秒数，带有序值索引。迁移和恢复之后仍然正确，前提是各节点用 NTP 对时。
- 它们不属于游戏逻辑：到期时作为消息进入本域，再按 `(时间, 序号)` 执行。

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
- 等待中的请求、链接实体、待执行的延后事件随实例一起迁移。延后事件保留剩下的时间，到达后照常执行。
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
- 新版本如果改了某条客户端消息的目标种类，`wire.manifest` 比对后拒绝这次热更。
- 组件、消息、事件、种类不热更，新增它们要走滚动升级。待执行的延后事件因此在热更前后都有处理方法。

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
| `[On<T>]` 抛出异常 | 请求失败，连锁的 `Raise(e)` 作废。默认 `Resume`；`Reload` 时断开所有链接后从记录重新激活；`Client` 直接断开 |
| 一条消息连锁超过 1,024 个 `Raise(e)` | 按 `[OnError]` 处理 |
| `[On<Tick>]` 抛出异常 | 这一帧跳过它剩下的部分，按种类的 `[OnError]` 处理，并在健康检查里报告 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标走开了） | 处理方法重新校验后作废；`[Of]` 指向的实体不在时，跳板不调用处理方法 |

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
[Frame(50)] public readonly partial record struct Map(long Id) : IKind<Map>;          // 20 frames a second

[Hosts<Player>] public struct GameServer    : IServer;
[Hosts<Map>]    public struct MapServer     : IServer;
[Hosts<Client>] public struct GatewayServer : IServer;

[NinoType, From<Client>] public record struct Attach                  : IRequest<Player, LinkId>;
[NinoType, From<Player>] public record struct EnterMap(Player Player) : IRequest<Map, LinkId>;
[NinoType, From<Player>] public record struct LeaveMap(Player Player) : IMessage<Map>;

[NinoType] public record struct Strike(int Skill, EntityId Target) : IEvent;           // a cast's resolution, after the windup

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
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Health(int Value) : IComponent;
public record struct Casting(Scheduled<Strike> Strike) : IComponent;                    // "is casting" while Strike is pending
public record struct Shout(NearbyEvent Event);                                          // a map's outbox element

public static class Prefabs
{
    [Spawns<Map>] public static readonly Prefab Map = new Prefab().SetGrid(512, 16).SetBuffer<Shout>();
    public static readonly Prefab Avatar = new Prefab().SetPosition(default).SetVelocity(default)
        .SetHealth(100).SetVisible();                                     // a player's position comes from its steps
    public static readonly Prefab Npc = new Prefab().SetPosition(default).SetHeading(default).SetVelocity(default)
        .SetDestination(default).SetSpeed(3).SetHealth(100).SetSteered().SetVisible();
}
```

```csharp
// ── Game.Server.Logic: connecting ─────────────────────────────────────────────
public static partial class Connecting
{
    [On<Activated>]                                                        // the client connected: link it to its player
    static void Connected(in Activated a, in Self<Client> self, in PlayerClaims claims) =>
        self.Ask(new Player(claims.PlayerId), new Attach(), new Attempt(1));

    [On<Answer<Attach, LinkId>>]
    static void Attached(in Answer<Attach, LinkId> answer, [Pending] in Attempt attempt, in Self<Client> self, in PlayerClaims claims, World world)
    {
        if (answer.Ok) world.Route(in answer);                             // Via = the Client itself
        else if (answer.Status == RpcStatus.Superseded || attempt.N >= 5) self.Disconnect();
        else self.SendAfter(Backoff.Jittered(attempt.N), new Player(claims.PlayerId), new Attach(), new Attempt(attempt.N + 1));
    }

    [On<LinkLost>]                                                         // the player restarted: attach again
    static void Lost(in LinkLost lost, in Self<Client> self, in PlayerClaims claims)
    {
        if (lost.Target.Is<Player>(out _)) self.Ask(new Player(claims.PlayerId), new Attach(), new Attempt(1));
    }
}

// ── Game.Server.Logic: players ────────────────────────────────────────────────
public static partial class Players
{
    [On<Attach>]                                                           // newest connection wins; an old one's retry gets Superseded
    static LinkId Attach(in Attach m, in Self<Player> self) => self.LinkFrom(supersede: true);

    [On<TravelTo, Player>]                                                 // TravelTo goes to the player
    static void Travel(in TravelTo m, in Self<Player> self, in From from, in ReplyTo<bool> reply, Maybe<OnMap> onMap)
    {
        if (onMap.Exists) self.Send(onMap.Value.Map, new LeaveMap(self.Key));   // leave first: no double presence
        var map = new Map(m.MapId);
        self.AskFor(from, map, new EnterMap(self.Key), new Travelling(map), reply);
    }

    [On<Answer<EnterMap, LinkId>>]                                         // the map answered: route the client, then reply to it
    static void Entered(in Answer<EnterMap, LinkId> answer, [Pending] in Travelling trip, in ReplyTo<bool> reply, Entity player, World world)
    {
        if (answer.Ok) player.SetOnMap(trip.To);
        world.Reply(in reply, answer.Ok, routeFrom: in answer);           // the Client writes the route, then hands over the reply
    }

    [On<LinkLost>]                                                         // the map restarted: enter it again for the same client
    static void Lost(in LinkLost lost, in Self<Player> self, Maybe<OnMap> onMap)
    {
        if (lost.Target.Is<Map>(out var map) && onMap.Exists && onMap.Value.Map == map)
            self.AskFor(lost.Client, map, new EnterMap(self.Key), new Rejoining());
    }

    [On<Answer<EnterMap, LinkId>>]                                         // re-entered: write the route; no client request to reply to
    static void Rejoined(in Answer<EnterMap, LinkId> answer, [Pending] in Rejoining r, World world)
    {
        if (answer.Ok) world.Route(in answer);
    }
}

// ── Game.Server.Logic: a map's messages ───────────────────────────────────────
public static partial class Maps
{
    [On<EnterMap>]                                                         // EnterMap is IRequest<Map, …>
    static LinkId Enter(in EnterMap m, in Self<Map> map, World world)
    {
        var key = new Avatar(map.Key, m.Player);
        if (world.AvatarIndex.TryFind(key, out var old)) world.Unlink(old);   // gone at once: never two avatars per key
        return map.LinkFrom(world.Instantiate(Prefabs.Avatar).Set(key));  // the client from the envelope; sets ChildOf(map)
    }

    [On<LeaveMap>]
    static void Leave(in LeaveMap m, in Self<Map> map, World world)
    {
        if (world.AvatarIndex.TryFind(new Avatar(map.Key, m.Player), out var avatar)) world.Unlink(avatar);
    }

    [On<Unlinked>]                                                         // run inside Unlink: [Link] is the avatar, the outbox the map's
    static void Gone(in Unlinked u, [Link] in Linked link, ref DynamicBuffer<Shout> outbox) =>
        outbox.Add(new Shout(new NearbyEvent(link.Id.Value, NearbyKind.Disappear, default, default)));

    [On<MoveTo, Map>]                                                      // a step: applied now, so later events see it
    static void Move(in MoveTo m, [Link] Entity avatar, [Link] ref Position position, in Grid grid, World world)
    {
        if (!Movement.IsValidStep(in position, in m, in grid)) { world.Push(avatar, new SnapBack(position.Value)); return; }
        position.Value = m.At;
        avatar.SetVelocity(m.Velocity);                                    // tracked: AOI broadcasts it this frame
    }
}

// ── Game.Server.Logic: combat ─────────────────────────────────────────────────
public static partial class Combat
{
    [On<CastSkill, Map>]                                                   // validate, then schedule the resolution
    static void Cast(in CastSkill m, [Link] Entity avatar, [Link] Maybe<Casting> casting, World world)
    {
        if (casting.Exists && casting.Value.Strike.Pending) return;        // one cast at a time
        if (!Targets.TryFind(world, m.Target, out var target)) return;     // a LinkId or an npc id -> the entity on this map
        avatar.SetCasting(avatar.Raise(new Strike(m.Skill, target), after: Skills.Windup(m.Skill)));   // 0 resolves right after this handler
    }

    [On<Strike>]                                                           // n frames later; reads the target as it is now
    static void Resolve(in Strike s, Entity me, in Health mine, in Position at,
        [Of(nameof(Strike.Target))] in Position target, [Of(nameof(Strike.Target))] ref Health hp)
    {
        me.RemoveCasting();
        if (mine.Value <= 0 || hp.Value <= 0) return;                      // the caster died during the windup, or the target already did
        if (Skills.InRange(s.Skill, at.Value, target.Value)) hp.Value -= Skills.Damage(s.Skill);   // out of range: dodged
    }
}

// ── Game.Server.Logic: every frame, in declaration order ─────────────────────
public static partial class Simulation
{
    [On<Tick>, With<Steered>]                                              // components only: a parallel job; into an untracked component
    static void Steer(in Tick t, in Position p, in Destination d, in Speed s, ref Heading h) =>
        h.Value = Steering.Toward(p.Value, d.Target, s.Value);

    [On<Tick>, With<Steered>]                                              // takes Entity: sequential; Velocity changes only when it differs
    static void Commit(in Tick t, Entity e, in Heading h, in Velocity v) { if (h.Value != v.Value) e.SetVelocity(h.Value); }

    [On<Tick>, With<Steered>]                                              // whole columns; Vectors is a user helper
    static void Integrate(in Tick t, Span<Position> positions, ReadOnlySpan<Velocity> velocities) =>
        Vectors.AddScaled(MemoryMarshal.Cast<Position, float>(positions), MemoryMarshal.Cast<Velocity, float>(velocities), t.DeltaTime);

    [On<Tick>]                                                             // takes Self<Map>: once per map, sequential: cells, subscriptions, sends
    static void Broadcast(in Tick t, in Self<Map> map, in Grid grid, ref DynamicBuffer<Shout> outbox, World world) =>
        Aoi.Run(world, map, grid, ref outbox);
}
```

`Aoi.Run` 处理一张地图：
1. 遍历 `world.Children(map)`，每帧按位置把实体分进格子；格子不是组件，所以跨格不需要结构变更。同一张网格也给运行时用来划分空间区域。
2. 只看带 `Confirmed` 的 Avatar。
3. 一个客户端跨了格，或者它的 Avatar 第一次带上 `Confirmed`（包括迁移来的）：
   - 更新它订阅的 3×3 `ClientGroup`；
   - 推一份它看得到的人的完整快照。
4. `Changed<Velocity>` 的实体写成 `Moved` 事件，和发件箱里已有的事件一起按格子合并。
5. 每个有内容的格子发一帧 `Nearby`，然后清空发件箱。

事件里的 `Id`：Avatar 用 `LinkId`，NPC 用一个不会与之相撞的编号（例如最高位置 1）。`Targets.TryFind` 按同样的规则查回实体。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: the kind's key, id, frame and registration
partial record struct Map
{
    public Map Key => this;
    public const uint KindId = 0x5A1C_93E1;                               // NinoTypeId of Game.Server.Map
}

file static class MapKind
{
    [ModuleInitializer]
    internal static void Register() => Kinds.Register<Map>(Map.KindId, frameMs: 50,
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
// <auto-generated/> Game.Server.Logic: [On<T>] trampolines inside each partial class (they can reach private handlers)
partial class Maps
{
    internal static void On_Enter(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<EnterMap>();
        outcome.Reply(Enter(in m, Self<Map>.Of(target), world));
    }

    internal static void On_Gone(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var u = envelope.Read<Unlinked>();
        var linked = link.Read<Linked>();                                  // in: copied
        target.EnsureBuffer<Shout>();                                      // the store exists before the handle is used
        Gone(in u, in linked, ref target.Get<DynamicBuffer<Shout>>());     // a buffer is a handle: passed by ref, no write-back
    }

    internal static void On_Move(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var m = envelope.Read<MoveTo>();
        var position = link.Read<Position>();                              // [Link] ref: a local in
        var grid = target.Read<Grid>();                                    // in, on the map: copied
        Move(in m, link, ref position, in grid, world);
        if (link.TryGetRef<Position>(out var slot)) slot = position;       // written back only if it still exists
    }
}

partial class Combat
{
    // an event's target is the entity that raised it
    internal static void On_Resolve(World world, Entity target, Entity link, in Envelope envelope, ref Outcome outcome)
    {
        var s = envelope.Read<Strike>();
        var other = world.Resolve(s.Target);                               // [Of(nameof(Strike.Target))]
        if (!other.TryRead<Position>(out var targetAt) || !other.TryRead<Health>(out var hp)) return;   // gone: not called
        var mine = target.Read<Health>();
        var at = target.Read<Position>();
        Resolve(in s, target, in mine, in at, in targetAt, ref hp);        // RemoveCasting is a structural change: locals stay valid
        if (other.TryGetRef<Health>(out var slot)) slot = hp;
    }
}

partial class Simulation
{
    // components only: a job struct run by EasyEcs's parallel API, no closure, no allocation
    internal struct Steer_Job : IForEachJob<Position, Destination, Speed, Heading>
    {
        public Tick Tick;
        public void Execute(in Position p, in Destination d, in Speed s, ref Heading h) => Steer(in Tick, in p, in d, in s, ref h);
    }

    internal static void On_Steer(World world, in Tick tick) =>
        world.Query<Steered_Query>().ParallelForEach(new Steer_Job { Tick = tick });

    internal static void On_Integrate(World world, in Tick tick)
    {
        foreach (var chunk in world.Query<Steered_Query>().Chunks())               // whole columns, one call per chunk
            Integrate(in tick, chunk.Span<Position>(), chunk.ReadOnlySpan<Velocity>());
    }

    internal static void On_Broadcast(World world, Entity map, in Tick tick)    // once per map
    {
        var grid = map.Read<Grid>();
        map.EnsureBuffer<Shout>();
        Broadcast(in tick, Self<Map>.Of(map), in grid, ref map.Get<DynamicBuffer<Shout>>(), world);
    }
}
```

```csharp
// <auto-generated/> Game.Server.Logic: dispatch per kind, frame order, client routes; staged on load, adopted between frames
file static class Dispatch_Map
{
    [ModuleInitializer]
    internal static void Register() => Dispatch.Stage<Map>(static table => table
        .Request(EnterMap.MessageId, Maps.On_Enter)
        .Message(LeaveMap.MessageId, Maps.On_Leave)
        .Message(Unlinked.MessageId, Maps.On_Gone)
        .Message(MoveTo.MessageId, Maps.On_Move)
        .Message(CastSkill.MessageId, Combat.On_Cast)
        .Event(Strike.EventId, Combat.On_Resolve)
        .Tick(Simulation.On_Steer, parallel: true)                         // declaration order
        .Tick(Simulation.On_Commit)
        .Tick(Simulation.On_Integrate, parallel: true)
        .TickPerDomain(Simulation.On_Broadcast));                          // built once when staged
}

file static class ClientRoutes
{
    [ModuleInitializer]
    internal static void Register() => Routes.Stage(static table => table
        .Route(TravelTo.MessageId, Player.KindId)                         // [On<TravelTo, Player>] Travel
        .Route(MoveTo.MessageId, Map.KindId)                              // [On<MoveTo, Map>] Move
        .Route(CastSkill.MessageId, Map.KindId));                         // [On<CastSkill, Map>] Cast
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
kind    Game.Server.Map     id 5A1C93E1  key int64   hosted-by MapServer  frame 50ms
kind    Game.Server.Player  id 0B77D012  key int64   hosted-by GameServer
client  Game.Protocol.TravelTo   -> Player  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
server  Game.Server.EnterMap    Player -> Map     reply LinkId
server  Game.Server.LeaveMap    Player -> Map
server  Game.Server.Attach      Client -> Player  reply LinkId
event   Game.Server.Strike      Map
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
⑦ LinkConfirmed(L2)   Client → Map 7：Avatar 带上 Confirmed；下一帧的 Broadcast 推完整快照
⑧ MoveTo              Client(c1) → 路由 → Map 7：[Link] 绑定到这个 Avatar；校验这一步，当场改位置，后面的事件都看得到
⑨ CastSkill           同上进入 Map 7；Cast 校验后 Raise(Strike, after: 前摇)；前摇期间 Casting 拒绝第二次施法
⑩ Strike 到期         在它的 (时间, 序号) 执行：读目标此刻的位置，够得着才扣血；目标走开了就是闪避
⑪ 每 50 ms 一帧       消息 → Tick（Steer、Integrate 按列并行；Commit、Broadcast 串行）→ 到期的事件；离得够远的区域并行
                      Nearby → 有观察者的网关 → 各个连接
```

---

## 13. 编译期检查、删掉的东西、版本记录

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：没有对应的方法。
- 客户端消息没有 `[On<T, K>]`，或者被两个种类接收；服务器之间的消息写了第二个类型参数。
- 一个请求有两个处理方法；`Answer` 的（请求类型, 等待数据类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`。
- 含 `[On<T>]` 的类不是 `static partial class`（可一键修复）。
- 用户代码写种类组件，或者把种类组件放进 prefab、`CreateEntity`。
- 取了 `ref T` 的处理方法，又用别的方式写同一个实体上的 `T`。
- 只取组件或列的 `[On<Tick>]` 里发消息、`Raise`、做结构变更。结构变更和写有索引的组件，EasyEcs 已经报 EECS013、EECS015。
- `Raise` 给本域以外的实体；跨域的效果要用消息，至少隔一帧。
- `[Of(nameof(E.Field))]` 的字段不存在，或者不是 `EntityId`。
- 事件类型不是 `[NinoType]`，或者没有实现 `IEvent`。
- 复制 `ReplyTo<T>`。
- Protocol 里出现种类或者服务器之间的消息；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- 种类的键超过 16 字节，或者不是非托管类型。
- `[Persist]` 组件没有实现 `ITrackChanges`。
- 组件、事件、等待数据、`Answer` 的类型参数声明在 Logic。
- 要迁移的托管组件没有 Nino 格式化器。

**删掉的东西**：
- `[Actor]`、`[Handle]`、`[Service]`、`Context`、`Answered`、`ReplyLater`；
- `Created`/`Loaded`/`Unloading` 回调、`UnloadAfterIdleSeconds`；
- 旧的 `[Filter]`、`[FromClient]`/`[ToClient]`、`Publish`；
- Known/Granted/Superseded、自研的记录格式、`ActorKey`；
- `[Receive]`、`[Receive<K>]`、用 `EcsSystem` 和 `[ForEach]` 写逻辑、`[Every]` 系统组、`self.After`、作为业务入口的 `TimerSystem`/`MailSystem`；
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

---

## 14. 待拍板

1. **采用 v20 的执行模型**：每个域按 `(时间, 序号)` 逐个执行消息、Tick、延后的事件；`[On<T>]` 是唯一的处理方法特性。
2. **空间窗口默认开启，还是按域开启**：默认开启时，每条消息最多多等一帧（30 Hz 约 33 ms）；按域开启时，种类上写一个特性，不写就是消息到了就处理。
3. **`[Of(nameof(Strike.Target))]` 的写法**，备选：
   - 在事件字段上标 `[Target]`，参数写 `[Target] in Position`；
   - 包装类型 `Of<Position>`，按字段名对应参数名；
   - 保持 `[Of(nameof(…))]`。
4. **`Self<K>` 这个名字**，也可以叫 `Me<K>`。
