# DaServer 新编程模型设计（草案 v62，未实现，迭代中）

原则：业务代码干净、解耦、简洁，不用 OOP。ECS 一路到底：能用 EasyEcs 的功能就用 EasyEcs，状态都是数据，其余交给我们的特性和生成器，不重复造轮子。

目标：
- 从类型就看得出每条消息从哪个 Actor 发到哪个 Actor，以及它们在哪种服务器上；
- 客户端只知道它收发的消息，不知道服务端怎么划分，也看不到节点和 Actor 的地址；
- 分布式下也正确：同一个 Actor 只有一个激活能写，连接有先后，先后和超时不依赖各节点的时钟一致；
- 高性能，高并发。

本稿是完整的一份。每条规则只在它所在的那一节写一次，别处只给链接。第 12 节是完整的例子，分别列出用户写的代码、生成的代码和测试。第 13 节是编译期检查，第 14 节是已定的取舍和对 EasyEcs 的需求，第 15 节是可选的扩展能力（各自一个包，不引用就没有）。框架内部的数据、机制和协议细节在 [architecture.md](architecture.md)；本稿先用后讲的词（主体、有序阶段、`Due`、`Outbox`、epoch、占位等）在它第 2 节的 [Terms](architecture.md#terms) 里各有一句话的定义。

文中引用的 EasyEcs 文档都在它仓库的 `docs/content/` 下。本稿基于 EasyEcs 4.7：`entity.Run(ref job, filter)`、接口带的组过滤（`ISystemGroupWith<T>`）、`world.Descendants(root)`、`LoadEntitiesInto(…, markWritten: false)`（以上自 4.5），`world.Load(bytes, markWritten: false)`、`AddSystem(…, changesFromNow: true)`（4.6），`Mut<T>` 参数和 `SetIfChanged`/`SetXIfChanged(…)`、带组件的 `world.Instantiate`、`[ChangedAny<…>]`/`[RemovedAny<…>]` 和存在字段里、用 `Since(tick)` 换起点的 `QueryFilter.Create().ChangedAny<…>(0)`、`IUniqueIndexed<T>`（`Get`/`TryGet`）、`world.TrimExcess()`、`[ForEach(Slices = N)]`、`SystemStats` 的分配字节和访问的实体数、`world.DescribeSchedule()`、`world.SaveWithout(output, in exclude)` 和 `world.LoadKeeping(input, in keep, markWritten)`、`world.Descendants(root).AddTo(ids, includeRoot: true)`、一次销毁一组 id 的 `world.DestroyEntities(ids)`、只读不打戳的遍历 `world.Read<…>(filter)`、O(1) 的匹配数（`index.Find(key).Count`、`world.Children(parent).Count`）和不同键数 `index.KeyCount`（4.7）。名字按 4.7 的：`ITracked`、`ISystemGroupWith<T>`、`Retired`、job 的 `WriteMask`、`SaveEntitiesWith`；4.7 的分析器规则 EECS037–052 对生成的代码和例子都不报（W1）。带追踪组件的程序 Native AOT 发布 0 警告（EasyEcs 的 CI 以警告为错误发布并运行）。要先核对的 EasyEcs 行为只列在[路线图的 W1](../roadmap.md)（第 14 节第 5 条）。

---

## 先看一个最小的例子：聊天室

完整的写法从下一节起逐条讲；第 12 节是一个大的例子（地图、技能、掉落）。最小的服务端是一个聊天室：

```csharp
// Game.Protocol: what the client sends and receives
[NinoType] public record struct JoinRoom([To] long Room) : IFromClient<bool>;
[NinoType] public record struct Say([To] long Room, string Text) : IFromClient;
[NinoType] public record struct LeaveRoom([To] long Room) : IFromClient;
[NinoType] public record struct Said(long From, string Text) : IToClient;

// Game.Server
public readonly partial record struct Account(long Id) : IClaims;                // who the token says the client is
[ActivatedBy<JoinRoom>, ActorOptions(PresencesPerClient = 16, DetachGrace = 0)]  // joining creates a room; 16 rooms a client
public readonly partial record struct Room(long Id) : IActor<Room>;
[Hosts<Room>, Hosts<Connection>] public readonly struct ChatServer : IServer;    // one server type: rooms and clients

[Handles<Room>]
static partial class Chat
{
    [On<JoinRoom>]
    static bool Join(in JoinRoom m, Entity room) => room.JoinClient(out _) == Status.Ok;   // find or spawn, then join

    [On<Say>]
    static void Talk(in Say m, in Account who, Entity room)
    {
        if (room.TryGetClient(out _)) room.Push(room.Everyone, new Said(who.Id, m.Text));    // members only
    }

    [On<LeaveRoom>]
    static void Quit(in LeaveRoom m, Entity room)
    {
        if (room.TryGetClient(out var me)) me.Leave();
    }
}
```

- `[To]` 选房间；`Say` 发给一个没有激活的房间号就丢弃并计数（它是单向消息）：只有 `JoinRoom` 能建出新的房间（第 4、6 节）。
- 房间不保存（没有 `RootPrefab`），它的激活不碰存储，所以这个样例不需要数据库；最后一个人离开、空闲过了 `IdleTimeout` 就停用。
- `root.Everyone` 是房间里所有在线的成员，一次序列化，每个网关一帧（第 9 节）。
- 测试：`await using var cluster = await TestCluster.Start(Registration.Create(), c => c.Nodes<ChatServer>(1));`，`var alice = await cluster.Connect(new Account(1));` 连上一个客户端（第 15 节的测试工具）。
- `dotnet new daserver` 生成的就是这个样例（`samples/starter`，ADR-091）。

## 0. 一页看懂

### 用户写的

用户一般只有两个工程：`Game.Protocol` 是客户端收发的消息，只有数据，编译成 net10.0 的 DLL 交给客户端（首先是 Godot 4 .NET，它的游戏工程是 net10.0；也编 netstandard2.1，给以后的 Unity 留着）；`Game.Server` 是其余的一切。客户端也要跑的规则（预测移动、本地校验，例如 mmo 样例的 `Steps.IsValid`）放进第三个工程 `Game.Shared`（netstandard2.1、net10.0，只有纯函数和数据，不含 Actor 类型和服务器之间的消息；Protocol 之外由分析器同样把关），Server 和客户端都引用它。`Game.Server` 加上五行的 Program.cs 和部署用的 `apphost.cs`（第 2 节）。换代码就是换二进制：节点原地热重启，JIT 和 Native AOT 一样（第 10 节）。

| 写什么 | 写法 | 工程 |
|---|---|---|
| 客户端消息 | `[NinoType] record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;` | Protocol |
| 发给指定 Actor 的客户端消息 | `[NinoType] record struct EnterMap([To] long Map) : IFromClient<bool>;`；只到已有记录的键，Actor 类型写了 `[ActivatedBy<EnterMap>]` 才能由它建出新的（第 6 节）；键的一部分来自身份时，消息只带其余的字段，路由由 Actor 类型上的静态 `Route` 方法补上（第 4 节） | Protocol |
| 贵重的客户端请求 | `[Once, NinoType] record struct BuyItem(int Slot) : IFromClient<BuyResult>;`：SDK 用同一个序号重发到有回复，处理方法只执行一次，重发拿到存着的回复；`[Once]` 隐含 `Commit`，回复在记录写成之后才发出（第 8 节）；珍贵道具、花钻石这类。价格不由客户端说，服务端查表 | Protocol |
| 只要最新的消息 | `[NinoType, Latest] record struct Aim(Vector2 Direction) : IFromClient;`：邮箱里同一个发送方的同类型的旧的一条还没处理时，被新的替换（第 4 节）；移动输入、视角这类状态 | Protocol |
| 推给客户端的消息 | `[NinoType] record struct SnapBack(Vector2 At) : IToClient;`；含集合的是 `[NinoType, NinoPooled] sealed partial class Nearby : IToClient` | Protocol |
| Actor 类型（地址、根、系统组） | `[TickRate(30)] readonly partial record struct Map(long Id) : IActor<Map>;`；根要带组件时在类型里写 `public static Prefab RootPrefab { get; } = …`，根要保存再加 `[Persist]`；改默认的设置写 `[ActorOptions(IdleTimeout = …, DetachGrace = …, MaxPending = …, MaxMail = …, MaxMailBytes = …, MaxWaitingMail = …, PresencesPerClient = …, Unacked = …, OnceWindow = …, OnceInFlight = …, OnceQueued = …)]`（第 3、8 节） | Server |
| 服务器之间的消息 | `[Once, NinoType, From<Loot>] record struct GrantItem(int Item) : IRequestTo<Player, bool>;`；把价值转进别的 Actor、不能执行两次的请求标 `[Once]`，框架给每次发出分配序号、按它去重，只能用 `AskUntilAnswered` 发出；经济状态推荐放在不按 tick 走的 Actor 上（第 8 节） | Server |
| 本地事件 | `record struct Strike(int Skill, EntityId Target) : IEvent;` | Server |
| 组件 | `record struct Health(int Value) : IComponent;`；不随迁移、不进记录、到达和加载时取 prefab 的默认值的，标 `[NotSaved]`（第 10 节） | Server |
| 子实体的键 | `partial record struct NetId(Map Map, int Value) : IComponent, IUniqueIndexed<NetId>;`（EasyEcs 的唯一值索引），第一个字段是 Actor 类型（第 3 节） | Server |
| prefab | `[Prefab<Map>] internal static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);`；要保存的 prefab 再写 `[Persist]`（第 10 节）；Actor 里的实体都用 `root.Spawn(prefab, …)` 建，按键找到或建出：`world.NetIdIndex.TryGet(key, out var e)`，没有再 `root.Spawn`；当前客户端的成员实体：`root.JoinClient(out var me, prefab)` 找到或建出并加入（第 7 节） | Server |
| 配置表 | `readonly record struct SkillTable(SkillRow[] Rows) : IConfigTable<SkillTable>`：静态的 `Name` 和 `TryLoad`，处理方法和系统取 `in SkillTable skills`（第 10 节） | Server |
| world 自己的缓存 | `struct PathCache : IWorldSingleton<PathCache>`：静态的 `Create()`，可以取 `ref`（第 5 节） | Server |
| 身份 | `readonly partial record struct Account(long Id) : IClaims;`：令牌里的声明，用户自己的类型（第 7 节） | Server |
| 连上就加入 | `[JoinOnConnect<Account>] readonly partial record struct Player(long Id) : IActor<Player> { [Persist] public static Prefab RootPrefab { get; } = …; }` | Server |
| 服务器类型 | `[Hosts<Map>] public readonly struct MapServer : IServer;` | Server |
| 对消息和事件的反应 | `[Handles<Map>] static partial class Combat { [On<CastSkill>, Without<Stunned>] … }` | Server |
| 每 tick 或对数据的反应 | `[UpdateInGroup<Map>] class Movement : EcsSystem { [ForEach] … }`；热重启前后的反应也是系统：`[ForEach, With<Restarting>]`、`[With<Resumed>]`（第 10 节） | Server |
| 本 Actor 里现在或以后做一件事 | `me.Schedule(new Strike(…), after: skills.Windup(skill))`，不写 `after` 就是紧接着（在项里）；要扛住停用和宕机的定时：`root.Remind<DailyReset>(at, every)`（`DaServer.Reminders`，第 15 节） | Server |
| 发给别的 Actor | `me.Send(new Player(id), new Defeated(map))` | Server |
| 等别的 Actor 的结果 | `root.Ask(new Loot(map.Id), new Claim(m.Drop, by), new Picking(reply))`（`deadline:` 可选），回信是 `Replied<Claim, Picking>`；不带状态时是 `Replied<Claim>` | Server |
| 贵重的改动 | 改完 `root.Commit()`：这个根在这个 tick 末就写，这一项发出的东西等写成才离开；把价值转进别的 Actor 的请求标 `[Once]`、用 `e.AskUntilAnswered(to, request)` 发（第 8 节） | Server |
| 测试一个 Actor | `new TestActor<Map>(new Map(7), Registration.Create())`；多个节点用 `TestCluster` | 测试 |
| 部署 | `cluster.AddNodes<MapServer>("map", replicas: 4)` | `Game.Server/apphost.cs`（第 2 节） |

### 框架给的

| 名字 | 是什么 |
|---|---|
| `IActor<T>` | Actor 类型的接口：组件、唯一值索引、`IEquatable<T>`、系统组，带组过滤 `In<T>` 和静态的 `RootPrefab`（第 3 节）；子实体的键直接用 EasyEcs 的 `IUniqueIndexed<T>` |
| `Entity`、`World` | 唯一的句柄和 ECS 本身。ECS 的成员来自 EasyEcs；发送、推送、加入、`Root` 等是 DaServer 加在 `Entity` 上的操作（第 5 节） |
| `ActorId`、`TypeId<T>.Value` | 任意 Actor 的地址（只在服务端）；类型的 id（Nino 类型 id），Actor 类型、消息、事件、组件都用它 |
| `Sender`、`ReplyTo<T>`、`Replied<TReq[, TState]>`、`CallId` | 发送方（`Id`，`TryGet(out K key)`）；回复令牌（普通数据）；回信（`TryGetValue` 取值，`Status` 说明原因，`State` 是发问时存下的状态）；调用 id（第 5、8 节） |
| `Connection`、`IClaims`、`Presence`、`Joined`、`Leaving`、`Detached` | 每个客户端连接一个 Actor；身份；客户端在一个 Actor 上的存在和它的几个状态（第 7 节） |
| `Activated`、`Deactivating` | 激活后的第一条邮件；停用前最后一个 tick 根上的 tag（第 6 节） |
| `Time`、`Pending<T>`、`ClientGroup` | 时间单例；延后事件和请求的同一种句柄；一次序列化、按网关扇出的广播组（第 9 节） |
| `Status` | 请求的结果和拒绝原因，分 `NotExecuted` 和 `Transient` 两类（第 11 节） |
| `[TickRate(n)]`、`[EverySeconds(n)]`、`[ActorOptions(…)]`、`[Latest]`、`[Persist]` | 帧率（第 5 节）；按时间跑的系统（第 5 节）；Actor 类型的设置（第 3 节）；只要最新的单向消息（第 4 节）；要保存的 prefab，根的和子实体的（第 10 节） |
| `Commit`、`[Once]`、`AskUntilAnswered` | 贵重的改动写成之后才放出这一项的输出；执行一次的请求（框架分配序号去重）；扛住宕机、一直问到有回答的问（第 8 节） |
| `[ActivatedBy<T>]`、`CanCreate` | 存储里还没有记录的键，只有 T 能激活；客户端的 `[To]` 消息不写它就只到已有记录的键；建键之前的纯函数门（第 4、6 节） |
| `IConfigTable<T>`、`IWorldSingleton<T>` | 用户单例的两种：配置表（节点读入、校验，只能取 `in`，第 10 节）；world 自己的缓存或配置（每个 world 用 `Create()` 建一份，可以取 `ref`，第 5 节） |
| `ICluster`、`Registration`、`TestActor<K>`、`TestCluster` | Actor 以外的入口（第 4 节）；生成的注册对象；测试台（第 12 节） |

进阶（用到时再看）：

| 名字 | 是什么 |
|---|---|
| `root.Delete()`、`Status.Outstanding` | 永久删掉一个 Actor 的记录（一条单向的路：之后它什么都不做，过了宽限才真正删）；根还有没问完的 `AskUntilAnswered`（排着的、已发出的、停住的都算）时回 `Outstanding`，什么都不删（第 5、8 节） |
| `[Stateless]` | 不保存、不迁移的 Actor 类型，只做当场回复（第 3 节） |
| `[NotSaved]` | 不随迁移、不进记录的组件：迁移到达和加载时取 prefab 副本的默认值（第 10 节） |
| `Incoming`、`Verdict` | `[Handles<Connection>]` 里每条客户端消息转发前的过滤（第 7 节） |
| `Takeover` | `[JoinOnConnect]` 上谁胜出：`NewestWins`（默认）或 `Reject`（第 7 节） |
| `PresenceLost` | `Connection` 上的通知：这个客户端的一个 Presence 没了（第 7 节） |
| `Restarting`、`Resumed`、`RestartingNodes` | 热重启：冻结前最后一个 tick 根上的 tag、加载后第一个 tick 的 tag；列出正在热重启的节点的节点单例（第 7、10 节） |

框架内部的数据、机制表（每个机制用了 EasyEcs 的什么）和诊断里看得到的内部名字，在 [architecture.md 第 2 节](architecture.md#2-runtime-structure)。

## 一段用户代码

第 12 节的完整例子里的几段：两个 Actor 类型、两条客户端消息的处理、一次 `AskUntilAnswered` 和它的回信、一个系统；最后是它们用到的声明。每一行都和第 12 节一字不差，只省略了这里用不到的方法和声明（例如 `Pickups.Redrop`、`Steering`）。两个词先说清：**主体**是一条消息或一个事件作用的实体，处理方法的组件参数都绑定到它（第 5 节）；**有序阶段**是每个 tick 开头，把这个 tick 窗口里的邮件和到期项按 `(时间, 序号)` 逐项执行的那一段（第 1 节）。捡东西分在两个 Actor 上：按 tick 走的地图只管看得见的掉落，不按 tick 走的 `Loot` 管发物品（第 8 节）。

```csharp
[TickRate(30), ActivatedBy<EnterMap>]                                                  // 30 ticks a second; only EnterMap creates a map
public readonly partial record struct Map(long Id) : IActor<Map>
{
    [Persist]                                                                          // the map's record: Grid and NetIds; drops are Loot's
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetVisitors(default).SetBuffer<Shout>();
    public static bool CanCreate(in Account who, in EnterMap m, in MapTable maps) => maps.Has(m.Map);   // before the claim:
}                                                                                      // a made-up map id writes nothing
public readonly partial record struct Loot(long Map) : IActor<Loot>                    // no [TickRate]: a map's drops as records; its
{                                                                                      // commits write Loot's record, not the map's (section 8)
    [Persist] public static Prefab RootPrefab { get; } = new Prefab();
}

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

[Handles<Map>]
public static partial class Pickups
{
    const float Reach = 2;

    [On<PickUp>]                                                           // subject: the client's avatar; replies later
    static void Take(in PickUp m, in Map map, in Account who, in Position at, in ReplyTo<bool> reply, Entity me, World world)
    {
        if (!world.NetIdIndex.TryGet(new NetId(map, m.Drop), out var drop) || !drop.HasPrize || !drop.IsVisible    // a client's
            || Vector2.DistanceSquared(at.Value, drop.Position.Value) > Reach * Reach)   // number: a drop, seen, in reach
        {
            me.Reply(in reply, false);
            return;
        }
        me.Root.Ask(new Loot(map.Id), new Claim(m.Drop, new Player(who.Id)), new Picking(reply));   // from the root: the drop
    }                                                                      // may be gone before the answer comes

    [On<Replied<Claim, Picking>>]                                          // subject: the map's root; past the client's deadline
    static void Picked(in Replied<Claim, Picking> a, Entity root)          // nothing is sent
    {
        if (a.TryGetValue(out var ok)) root.Reply(in a.State.Reply, ok);  // Loot's answer, ahead of Loot's Gone (section 1)
        else if (a.Status.NotExecuted) root.Reply(in a.State.Reply, false);   // Loot never ran it: not taken
    }                                                                      // TimedOut, Faulted: no reply, the client's call times
                                                                           // out: unknown (the item may be granted), never "false"
}

[Handles<Loot>]
public static partial class Stashes
{
    [On<Claim>]                                                            // subject: Loot's root; replies later
    static void Award(in Claim c, in Loot loot, in ReplyTo<bool> reply, Entity root, World world)
    {
        var key = new Stash(loot, c.Drop);
        if (!world.StashIndex.TryGet(key, out var stash))                  // unknown here (lost before a checkpoint):
        {
            root.Reply(in reply, false);
            root.Send(new Map(loot.Map), new Gone(c.Drop));                // the map stops showing it
            return;
        }
        if (stash.Granting.By == c.By) return;                             // this player's retry while its grant is in flight: no
                                                                           // answer, the call times out: unknown, never "false"
        if (stash.Granting.By != default || !stash.TryGetPrize(out var prize))   // already promised to someone else
        {
            root.Reply(in reply, false);
            return;
        }
        stash.SetGranting(c.By, prize.Item);                               // promised: written with the ask, which is
        stash.AskUntilAnswered(c.By, new GrantItem(prize.Item), new Looting(reply));   // asked again after a crash
    }

    [On<Replied<GrantItem, Looting>>]                                      // subject: the stash
    static void Granted(in Replied<GrantItem, Looting> a, in Loot loot, in Stash key, Entity stash)
    {                                                                      // comes once: the player's value or its definite
        var taken = a.TryGetValue(out var ok) && ok;                       // refusal, never a link outcome (section 8)
        stash.Reply(in a.State.Reply, taken);                              // answers the map's Claim, ahead of Gone (section 1);
        if (!taken)                                                        // the token's deadline may have passed: nothing sent
        {                                                                  // the bag was full, or the player refused for sure
            stash.Set(default(Granting));                                  // the drop stays for someone else
            return;                                                        // this item commits; the acknowledgement follows
        }
        stash.Send(new Map(loot.Map), new Gone(key.Drop));                 // granted: the drop goes once this is written
        stash.Destroy();
    }
}

[UpdateInGroup<Map>, UpdateAfter<Steering>]
public class Movement : EcsSystem
{
    [ForEach, With<Steered>]                                               // per row: a batch Span<Position> would stamp every row
    static void Integrate(Mut<Position> p, in Velocity v, in Time time) =>  // standing still: same value, no stamp
        p.SetIfChanged(new Position(p.Value.Value + v.Value * (float)time.DeltaTime.TotalSeconds));
}

// ── the declarations used above, line for line from section 12 ──
[NinoType] public record struct MoveTo(Vector2 At, Vector2 Velocity) : IFromClient;   // a step the client took; to its current map
[NinoType] public record struct PickUp(int Drop) : IFromClient<bool>;                 // Drop: a NetId number; to its current map
[NinoType] public record struct SnapBack(Vector2 At) : IToClient;                     // the step was refused
[NinoType] public record struct NearbyEvent(int Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: the entity's NetId number
public enum NearbyKind : byte { Appear, Moved, Disappear }
public readonly partial record struct Account(long Id) : IClaims;                      // decoded from the token
[JoinOnConnect<Account>]                                                               // joined at connect: Account(42) -> Player(42)
public readonly partial record struct Player(long Id) : IActor<Player>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(40).SetWallet(default).SetBuffer<Slot>();
}
[Once, NinoType, From<Loot>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // runs once on the player
[NinoType, From<Map>] public record struct Claim(int Drop, Player By) : IRequestTo<Loot, bool>;   // the map commits nothing: Loot does
[NinoType, From<Loot>] public record struct Gone(int Drop) : IMessageTo<Map>;          // no longer to be had: off the map
[NinoType] public record struct Grid(int Size, int CellSize) : IComponent, ITracked;   // on a map's root
[NinoType] public record struct Inventory(int Capacity) : IComponent, ITracked;    // on a player's root
[NinoType] public record struct Wallet(int Diamonds) : IComponent, ITracked;       // on a player's root: section 8's purchases
[NinoType] public record struct Slot(int Item, int Count);                            // a bag slot: a buffer element on the root
public record struct NetIds(int Next) : IComponent, ITracked;                     // on a map's root: the last drop number given out,
                                                                                        // saved at checkpoints (a crash may repeat one: Clash)
[NotSaved] public record struct Visitors(int Next) : IComponent;                        // on a map's root: avatars' and NPCs' numbers,
                                                                                        // negative, never saved (none outlives a crash)
public partial record struct NetId(Map Map, int Value) : IComponent, IUniqueIndexed<NetId>;   // what clients call an entity; Key => this is generated
public partial record struct Stash(Loot Loot, int Drop) : IComponent, IUniqueIndexed<Stash>, ITracked;  // a drop's record on Loot
public partial record struct Cell(Map Map, int X, int Y) : IComponent, IIndexed<Cell>, ITracked;   // AOI cell: many entities
                                                                                        // per cell (a non-unique index); Map keeps maps apart
public record struct Visible : IComponent;                                              // a tag: what the AOI tells about
public record struct Position(Vector2 Value) : IComponent, ITracked;               // tracked: CellAssign reacts to writes
public record struct Velocity(Vector2 Value) : IComponent, ITracked;               // tracked: Diff reacts to writes
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Dead : IComponent;
[NinoType] public record struct Prize(int Item) : IComponent, ITracked;           // what a drop holds: on the map and on Loot
public record struct Picking(ReplyTo<bool> Reply) : IComponent;                         // the map's plain ask's state: the client's token
[NinoType] public record struct Looting(ReplyTo<bool> Reply) : IComponent;              // the grant's ask state, kept in its ask row
                                                                                          // (so in Loot's record): the map's token
[NinoType] public record struct Granting(Player By, int Item) : IComponent, ITracked;   // promised to whom: By default = none
[NotSaved] public record struct Shout(Cell Cell, NearbyEvent Event);                    // news for one cell, on the map; empty between ticks
                                                                                        // (a buffer element: EasyEcs asks for no interface)
```

---
## 1. 保证

1. **每个 Actor 按 `(时间, 序号)` 逐条处理自己的消息和事件，Actor 之间并行。** `Activated` 也是邮件，排在同一个顺序里。
2. **一个发送方发给一个目标的一切，按发送顺序到达**：单向消息、请求、回答（`Replied`、`ReplyTo<T>` 的回复）、加入的路由和 Presence 的进出通知都算；回答算作回答它的那个 Actor 发出的消息（同 Erlang：回复就是一条普通的消息）。客户端发给一个 Actor 的、一个 Actor 推给一个客户端的也一样。发给不同目标的互不等待。用户只记这一条规则（ADR-096）。
   - 唯一的例外是迁移的那一两秒：只尽力保序，同 Orleans，在途的消息可能后发先到。
   - `[Latest]` 的消息被同一个发送方的同类型的新的一条替换时，被替换的那条不到达（第 4 节）；到达的仍按发送顺序。
   - 调用了 `Commit` 的那一项的输出等记录写成才离开，它挡住同一个根之后发给同一个目标的输出，后面的不超车（第 8 节）。
   - 另一个例外是组推送：调用了 `Commit` 的那一项推给一个 `ClientGroup` 的消息，写成时才追加进组帧，所以它和别的项推给同一个组的消息按放出的先后排，而不是按发出的先后（一次写不挡住整个组的广播，第 8 节）。
   - 用户看不到的框架流量（心跳、成员视图的交换、链路的确认、`Fence`、`Wake`、`HandoffQuery`、大消息的完成通知、`[Once]` 的确认、`AckQuery` 和它的回答、窗口更新，第 8 节）走自己的通道：它们和用户的消息之间没有可以观察的先后。
   - 需要跨两个目标的先后，用请求和回复串起来。
3. **单向消息最多投递一次；请求一定有结果**：回复、带原因的拒绝，或者超时。**超时不等于没执行**：结果未知时由发问方决定；`[Once]` 的请求从不执行两次，用 `AskUntilAnswered` 发出，框架一直问到有回答（第 8 节）。每条路径的投递、顺序、去重、超时和重试由谁负责，见第 11 节的表。
4. **同一个 Actor 任一时刻最多一个激活能写记录**：激活先占 epoch，每次写都按 epoch 比较并交换；比较失败的激活被隔离（第 10 节）。
5. **客户端重连后是一个新的连接**：断线前没送达的推送会丢失，重连前后也没有顺序关系。旧连接留下的 Presence 在宽限期内保留，新连接接管它（第 7 节；先后由连接戳决定，见 [architecture.md 第 3 节](architecture.md#connection-stamps)）。
6. **每种失败的结果都是确定的**，见第 11 节。

### 执行模型：每个 Actor 是一个离散事件模拟

**Actor** 是 Actor 类型的一个实例，例如 7 号地图：它的根实体（带 Actor 类型组件 `Map(7)`）加上所有子孙，例如一张地图和地图上的所有实体。Actor 里发生的一切，都是排好序的事件：

| 事件 | 时间 | 例子 |
|---|---|---|
| 客户端或别的 Actor 发来的消息 | 进入本 world 的时刻 | 移动一步、放技能、交易 |
| 本地事件 `e.Schedule(ev)` | 紧接着当前处理方法（在项里） | 立即结算的技能 |
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
| a. 有序阶段 | 运行时系统 `OrderedPhase`：tick 窗口里的消息和到期项按 `(时间, 序号)` 归并（第 9 节） | 串行。每一项执行完，紧接着执行它的即时链（项里不带 `after` 的 `Schedule`；系统里的见第 9 节），每项最多连锁 1,024 个；阶段结束时把 `Outbox` 交给链路 |
| b. 系统 | `world.Update<Map>()`：这个 Actor 类型的 EasyEcs 系统（第 5 节） | 单线程；`[ForEach(Parallel = true)]` 只在 world 比核少时分块多线程（第 5 节） |
| c. 收尾（`Settling`） | 框架的系统：`Depart` 销毁带 `Leaving` 的 Presence，`SyncSubscriptions` 同步订阅（第 7、9 节），去掉 `Restarting` 和 `Resumed`（第 10 节）；收尾里写出的帧随这一圈最后一次交给链路发出。框架组 `Cleanup` 不在这里：所有 Actor 类型的一趟都跑完后，它在 world 上跑一次，处理已销毁实体留下的清理组件（`ReleaseRoutes` 等，第 6、9 节） | 单线程 |

- 有序阶段的状态（tick 号、序号计数器、当前 tick 窗口、即时链预算）按 Actor 类型存在运行时的单例 `Ordered<K>` 里。有序阶段、收尾和清理都是 DaServer 自己的 `EcsSystem`，在框架的系统组里，每个只做一件事。
- 有序阶段开始时取一次邮箱快照；这期间新到的消息进下一帧。
- **一个 Actor 不能饿死同一 world 的邻居**：每批邮件里每个 Actor 最多执行一定数量的项（节点配置），其余留在它自己的邮箱里给下一批，仍按 `(时间, 序号)`。收也按 Actor 限：每个 Actor 的邮箱有自己的上限 `[ActorOptions(MaxMail = n, MaxMailBytes = b)]`（默认 4,096 条、4 MiB），满了只拒这一个 Actor 的新消息（请求回 `Busy`，单向消息丢弃并计数），同 world 的邻居照常收；world 的派发队列的上限只是节点内存的最后一道闸（Akka 的邮箱按 Actor 有界，Orleans 也按 grain 限排队的请求）。
- **先看冻结标志和租约**：每个 world 在取邮件和有序阶段之前读一次节点的冻结标志（`volatile`；节点被宣布 `Dead`、正在热重启时置上），再拿租约的截止时刻和时钟比（第 2 节）；冻结了就不执行，整个 world 冻结；除了热重启那一下短暂的冻结，也不取邮件。把 Outbox 交给链路之前再比一次。邮件的时间是它到达的时刻（链路读到帧时记下），所以冻结之后再取的邮件仍按到达的先后排，排在之后才到的回答和事件前面。
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
- 每个系统的耗时、分配和访问的实体数：EasyEcs 的 `MeasureSystems` 和 `system.Stats`（`LastAllocated`/`TotalAllocated`、`LastEntities`/`TotalEntities`），导出为指标。它按 world 统计每个系统，一次 `world.Update<Map>()` 覆盖这个 world 上所有的 Map，所以是这些 Actor 合在一起的值，不分 Actor；
- `world.Count<Retired>()` 导出为指标：已销毁、清理组件还没处理完的实体数。一直涨，说明有清理系统没跑或者漏了（第 6 节）；
- 只读的查看端点：一个 Actor 的子树、最忙的 Actor、每个 world 的表和系统、按组件计数（只按请求运行，会分配；[architecture.md 第 8 节](architecture.md#inspector-and-admin-clients)）；
- 并行和串行结果一致的核对，是一个测试（第 12 节的 `TestActor<K>`）。

---

## 2. 服务器与节点

```csharp
// Game.Server: section 12's servers
[Hosts<Player>, Hosts<Loot>, Hosts<Payment>] public readonly struct PlayerServer  : IServer;   // Payment: samples/payments
[Hosts<Map>]                 public readonly struct MapServer     : IServer;
[Hosts<Connection>]          public readonly struct GatewayServer : IServer;   // hosting Connection = accepting clients
```

第 3 节另外几种 Actor 类型（`Guild`、`Room`、`Pricing`）只示范写法；用到它们的工程同样把它们写进某个服务器的 `[Hosts<…>]`。

- **一个工程可以定义多种服务器类型；节点 = 一个进程 = 一种服务器。**
  - 拆服务器就是为了独立扩缩、独立发布、故障隔离，所以一个进程只跑一种服务器。
  - 本地开发由 Aspire 起多个进程；测试用 `TestCluster` 和 `TestActor<K>`。
  - 小规模部署可以写一个 `[Hosts<Player>, Hosts<Loot>, Hosts<Map>, Hosts<Connection>] AllInOne`。
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
- **成员表**：谁在集群里由存储里的成员表决定（Orleans 的成员协议，[Orleans：集群管理](https://learn.microsoft.com/dotnet/orleans/implementation/cluster-management)；功能点 `IMembershipTable`，有 PostgreSQL 和内存两种实现）。没有多数派、选票或集群大小要算，挂掉几个成员都行。
  - `daserver_members` 每个节点化身一行：集群、节点名、节点代数、地址、服务器类型、状态（`Joining`、`Active`、`ShuttingDown`、`Dead`）、对它的怀疑（谁、哪一代、存储的 `now()`）、`restart_until`（批准的热重启和它的期限）、`dead_seen_at`（协调者读到它 `Dead` 之后第一次写时存储的 `now()`，见下面的重新放置）、启动时间和 `i_am_alive`（存储的 `now()`，不改版本）；`daserver_membership` 每个集群一行：表版本、槽表和各 Actor 类型的放置版本。每次变化（加入、状态、怀疑、宣布死亡、热重启许可、槽移动）都是一个事务：改动的行，加上"版本还是读到的那个就把它加一"的比较并交换；冲突就重读重试。所以成员变化按表版本全序，读到同一版本的节点算出同样的成员、协调者和槽表。`i_am_alive` 每 30 秒写一次，不动版本。
  - 没有存储时只支持单个节点，而且要明说：节点配了 `DaServer:Membership:SingleNode = true` 才用内存表启动（本地运行、没有数据库的入门部署、`AllInOne` 部署）；没有成员存储、也没写 `SingleNode` 的节点拒绝启动，所以配错的副本不会自己组成一个单节点的集群；配了存储又写了 `SingleNode` 的也拒绝启动。单节点热重启时，交接的头带着自己那一行成员记录（节点名、代数、启动时间、`restart_until`），新进程先用它填好内存表再进 `Rejoining`，所以照样通过"还是 `Active`、同一代、自己的 `restart_until`"的检查，交接的 world 照样加载。AppHost 只有一个节点又没有存储时，`DaServer.Aspire` 替它设 `SingleNode`；多于一个节点又没有存储时，自动加一个 PostgreSQL。多于一个进程的集群把成员表放在存储里：默认是 Actor 存储的 PostgreSQL，也可以单独一个连接（`WithMembershipStore(db)`）。`TestCluster` 的节点共用一个内存表，每个节点经过一层能注入故障的 `IMembershipTable`（让某个节点的读写失败或变慢，第 15 节）。所有节点都要这个连接，网关也一样。
- **协调者**：不选举，由表算出：读到的表版本里，没有 `restart_until`（不管值是多少）的 `Active` 成员中启动时间最早（再按节点名）的那个。规则只看表、不看时钟，所以读到同一版本的节点算的一样；正在热重启的成员不是协调者，按这条规则算出的下一个照常盖 `dead_seen_at`、重新放置死节点，热重启超期时宣布它 `Dead`。每个读到同一版本的节点算出同一个协调者；过时的协调者什么也做不成，因为它的每个决定（槽移动、热重启许可和期限）都是带版本的写，比较会失败。它规划槽的移动（ADR-079）、写槽表，宣布热重启超期的成员 `Dead`（清理从没写成的记录行的不是它，第 10 节）。热重启保留节点的行和启动时间，所以热重启过的协调者回来、清掉 `restart_until` 之后仍是协调者。还有 `Dead` 成员等着重新放置时不批准热重启：两件事不重叠。
- **节点代数**：节点加入时自己取：代数 = max(当前的 Unix 毫秒数, 表里这个节点名最大的代数 + 1)，写进表里。所以整个集群冷启动之后代数也不会重复。新代数的行和"把同名的旧代数都标成 `Dead`"在同一个事务里写，还在跑的旧进程读到就自己停下。热重启不算重新加入：代数不变（第 10 节）。
- **加入**：节点读表，把自己写成 `Joining`，再确认和每个 `Active` 成员双向都连得通（`i_am_alive` 比表里最新的 `i_am_alive` 落后三个间隔以上的成员跳过，只比存储的时钟：整个集群重启时，一个没被宣布死亡的崩溃节点不挡路；跳过过成员的节点，在把每个成员都探测过一次之前不投怀疑票），然后把自己写成 `Active`；在那之前不承载槽也不接客户端。有成员连不通就不变成 `Active`，过一会儿重试，所以和集群一部分断开的节点进不来。第一个节点创建版本行。成员不需要种子：节点在表里找到彼此。
- **故障检测**：每个 `Active` 成员按成员哈希环探测后面最多 `ProbedNodes`（10）个成员，每 `ProbeInterval`（1 秒）一次，走节点链路；连续 `MissedProbes`（3）次没回，先请另一个成员代为探测（间接探测），还不通就在对方的行里写一条带时间的怀疑。一条怀疑让对方在 `SuspicionExpiry`（3 分钟）之内有了 `VotesForDeath`（2；剩下的其他 `Active` 成员不够时就是全部其他成员）条来自不同 `Active` 成员的怀疑时，写的人在同一个事务里把它标成 `Dead`。写的人在它写所依据的版本里必须是 `Active`，所以分区两边都连得到存储时，先写成的死亡宣告胜出，另一边的写比较失败：读到自己 `Dead` 的一边自己停下。两个成员的集群断成两半，恰好留下一个。
- **知道最新的表**：节点每 `TableRefresh`（5 秒）读一次表，一次读出两张表的同一个快照（一条 `UNION ALL` 语句）；自己写过之后把新快照发给其他成员，版本更新的就采用。帧里带发送方的表版本，版本新的一方用快照回答旧的一方。
- **被宣布死亡的节点自己停下**：读到自己 `Dead`（或者对方因为它这一代已经 `Dead` 而拒收它的帧）时，冻结所有 world、隔离所有 Actor，以非零的退出码退出；平台重新启动进程，以新的代数加入。其他节点一看到 `Dead` 就不再给它发。
- **隔离租约**：节点只在"发出最后一次读到自己 `Active` 的读表请求"之后的 `IsolationTimeout`（20 秒）之内运行 world。从**发出**算起：快照是在发出之后才取的，迟到的回答带着死亡之前的状态，也不能把租约延长到那个状态之后。别的成员推来的快照、对过时版本的回答只更新视图，不续租约：它有多旧不知道。租约用一个在机器挂起时也走的时钟（Linux 的 `CLOCK_BOOTTIME`、Windows 的 `QueryInterruptTime`、macOS 的 `mach_continuous_time`；`Stopwatch` 用的 `CLOCK_MONOTONIC` 挂起时和检查点恢复时不走），所以机器挂起过的进程醒来时租约已经过期。这个时钟只算客户机自己做的挂起：虚拟机被管理器停住（QEMU 的 kvmclock 只在虚拟机运行时走），或者进程在一个时间命名空间里从检查点恢复（CRIU），它也看不出。所以每次读表还带回存储的 `now()`（语句开始时取的，落在这次读的[发出, 回答]之间），节点记下每次读的这个区间：两次读之间存储的时间比两个区间允许的最大间隔（后一次的回答减前一次的发出）还多走了一秒以上，才当作停顿过（等着开始的读，例如故障转移后重连、存储前面的排队，不算），先立刻再读一次确认，按下面的停顿保护冻结到下一次读表确认自己还是 `Active`；醒来之后到下一次读表（最多一个 `TableRefresh`）之间还可能做的旧事，保存的写有 epoch 比较拦着，对端读到它 `Dead` 之后拒收它那一代的帧。成员循环把租约的截止时刻发布成一个值，**每个 shard 自己**在每个有序阶段之前、把 Outbox 交给链路之前拿它和时钟比：线程池饿着、成员循环还没跑，从停顿里醒来的 shard 也不会越过租约做事。
- **读不到表就不干活**：租约过了的节点冻结所有 world（在两个 tick 之间，同热重启的冻结），也不再从派发队列取邮件（队列有界，满了请求回 `Busy`，单向消息丢弃并计数，第 9 节），但不马上退出：连接留着。之后读到自己这一代还是 `Active` 就恢复，读到 `Dead` 就停下。冻结期间它**告诉集群自己被隔离了**：对直接和间接的探测都回答 `Isolated`，探测方自己的租约已经连续有效 `IsolationTimeout` 以上、而且自己不是刚从全集群的冻结里恢复（恢复之后还要再过一个 `IsolationTimeout`，并且自己积压的写已经写完）时，把它当作一次探测失败（不先间接探测），够票就宣布它 `Dead`（存储刚恢复时各节点第一次读表前后差几秒，积压的写还在排队，这两个条件让还没来得及读表的节点不被误杀，也不让恢复变成一连串的死亡宣告）；恢复时写记录和占 epoch 从每个 world 一条起步、每成功一轮加倍到上限，`DueScan` 带随机的延后，不一起压向刚回来的存储；至少有一个成员回答它的探测时不是 `Isolated`（别人连得到存储，只有它连不到：连接池耗尽、DNS 故障、网络策略），它就是**单独被隔离**：`/health` 就绪检查失败，这样冻结超过 `DaServer:Membership:IsolationExit`（1 分钟）就以退出码 75 退出，平台重启它，连得到存储后以新的代数加入。所以连得上别的节点、连不上存储的节点会让出它的槽，不会永远冻结着占住它们。它碰到的成员也都是 `Isolated` 时，是整个存储停了：就绪检查照常通过（存储故障不能让所有网关都退出负载均衡），谁也不退出。代价要说清楚：**存储停的时间超过 `IsolationTimeout`，整个集群都暂停**，直到存储回来、各自恢复（记录本来就在存储里，有状态的集群那段时间也存不了）。指标 `daserver.membership.frozen` 和日志 `MembershipFrozen` 报告它。
- **成员表有自己的连接**：读表和成员的写走一个单独的小连接池（同一个库也一样，两个连接，语句超时短），从不排在 Actor 的批量写后面：存储刚恢复、积压的写一起涌过去时，读表不会因此超时，节点不会因此被当成隔离。
- **停顿保护**：成员循环两次运行之间在租约时钟上隔了超过 `ProbeInterval`（GC 停顿、虚拟机被暂停），或者存储的时间比租约时钟多走了（上面）时，world 保持冻结，直到一次读表确认自己还是 `Active`；隔开的那段时间里没回的探测不算。安全靠的是 shard 自己查租约，停顿保护只是缩短一次短于租约的停顿之后还会做的旧事。
- **死节点在重新放置之前一直占着它的槽**：被写成 `Dead` 的成员留在每个节点的放置集合里（按会合哈希排序和槽表的覆盖都一样），直到协调者重新放置的那次写把它去掉；所以不管各节点先后什么时候知道它死了，那次写之前谁也不激活它的键。其他节点不再给 `Dead` 的那一代发，发往它的键的邮件由发送方所在的节点回答 `Unavailable(retryAfter)`，`retryAfter` 是视图里离重新放置的期限还剩的时间（最多大约 `IsolationTimeout + DeathMargin`，25 秒）：问按自己的规则重试（`AskUntilAnswered` 由框架接着问），客户端的调用收到 `Unavailable`，单向消息在发送方的窗口里等。同名的新一代在那次写之前也不承载旧一代的槽。
- **重新放置**：期限从 `dead_seen_at` 算，不从宣布死亡的那次写算。宣布死亡是一个多条语句的事务，它的 `now()` 是事务开始的时刻；写的人在 `COMMIT` 之前停住（GC、同步提交慢）时，提交可能晚得多，而那之前死节点读到的还是自己 `Active`，一直在续租约。所以协调者第一次读到某个成员 `Dead`、它还没有 `dead_seen_at` 时，用一次自己的带版本的写把存储的 `now()` 记进去：这个事务开始于一次已经看到死亡提交的读之后，所以 `dead_seen_at` 晚于提交，而死节点最后一次续租约始于提交之前。存储的 `now()` 过了 `dead_seen_at + IsolationTimeout + DaServer:Membership:DeathMargin`（5 秒，覆盖存储时钟和节点租约时钟走速的差）之后，协调者用一次带版本的写把它移出放置集合，这次写就是它承载的各 Actor 类型的放置版本的变化。到那时死节点要么读到了自己的死亡、停下了，要么租约过期、冻结了。两个时刻都按存储的时钟读，协调者自己的时钟、它什么时候知道这件事都不参与；保存的键还有 epoch 比较兜底（第 10 节）。
- **不对称的连通**：C 连不到 B、A 连得到时，C 先请 A 代为探测，A 探测得通，B 就不算失败，谁也不被宣布死亡；C 发给 B 的邮件等着、超时。Orleans 也有这个缺口，DaServer 报告它而不去猜：指标 `daserver.link.unreachable{from,to}` 报告单向不通的一对，Dashboard 列出它们。
- **world**：节点里有几个工作线程，每个线程跑一个 EasyEcs world；节点承载的所有 Actor 类型共用它，Actor 落在哪个 world 由键的哈希决定。world 的个数由节点按分到的 CPU 自己定（`DaServer:Worlds` 可以覆盖），用户代码和 AppHost 都不写它；框架内部把"一个线程加一个 world"叫 shard。
- **帧率**写在 Actor 类型上（第 5 节），不在配置里。
- **Program.cs**：大约五行，把生成的 `Registration.Create()`（第 12 节）交给节点，并显式声明 .NET 自带的两种服务管理器（systemd、Windows 服务），各自不在对应的服务管理器下就什么也不做；macOS 的 launchd 不需要代码，设置写在 plist 里。所以同一个二进制到处能跑（[deployment.md 第 0 节](deployment.md#0-launch-flows)）。两个 `using` 都写出来，生成器不补 `global using`：`Registration.Create()` 在工程的根命名空间里（第 5 节的"其他"），其余的命名空间由 `Microsoft.NET.Sdk.Web` 的 ImplicitUsings 带上：
  ```csharp
  using DaServer;                                                  // Registration, AddDaServer, MapDaServer
  using Game.Server;                                               // the generated Registration.Create()

  var builder = WebApplication.CreateSlimBuilder(args);
  builder.Host.UseSystemd().UseWindowsService();                  // launchd needs none: its plist sets the rest
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
  cluster.AddNodes<PlayerServer>("game", replicas: 3).WithActorStore(actors);
  cluster.AddNodes<MapServer>("map", replicas: 4).WithActorStore(actors);
  cluster.AddNodes<GatewayServer>("gate", replicas: 2).WithClientConnections(port: 7100);
  builder.Build().Run();
  ```
  - 运行：`dotnet run --file apphost.cs`（或 `aspire run`）；发布：`aspire publish`。在这个目录里直接 `dotnet run` 跑的是节点本身。本地、AOT、Compose、Kubernetes 和裸机的启动流程见 [deployment.md 第 0 节](deployment.md#0-launch-flows)。
  - `DaServer` 包的构建属性把 `apphost.cs` 移出服务端工程的编译（`<Compile Remove="apphost.cs" />`），用户不用写。
  - **AppHost 不引用服务端程序集**：`#:project` 只给 Aspire 工程元数据（`Projects.Game_Server`）。DaServer.Aspire 的生成器读这个工程提交的 `wire.manifest`，只读 `actor` 和 `hosted-by`（第 10 节），在 AppHost 里生成同名的服务器类型（同样是 `public readonly struct MapServer : IServer`），每个都绑定到这个工程：`AddNodes<MapServer>` 起的是 Game.Server 的二进制，节点跑 `MapServer`。
  - **只有一个 `IServer`**（DaServer.Core）：服务端的服务器类型和 AppHost 里生成的同名类型都实现它。`AddNodes<T>(string name, int replicas) where T : struct, IServer` 返回 `DaServerNodeGroup<T>`。
  - **两个名字各管一件事**：`name` 是 Aspire 的资源名，也是 Pod 名和节点名的前缀（`map-0`、`map-1`）；服务器类型的身份是它的 `FullName`（`Game.Server.MapServer`），节点启动时按它和 `wire.manifest` 对上。集群名和组名都是资源名，不能重名：集群叫 `mmo`，组叫 `game`、`map`、`gate`。
  - **记录存储**：`WithActorStore(actors)` 给承载有状态 Actor 的组接上 DaServer.Persistence.PostgreSql；只承载 `Connection` 和 `[Stateless]` 的组不用。签名密钥是集群的一个 Secret `{cluster}-signing`（键 `DaServer__SigningSecret` 和可选的 `DaServer__AcceptedSigningSecret`：按文件挂载，文件名就是配置项的名字），由 `AddDaServerCluster` 生成，所有节点组共用，用户不写（[deployment.md 第 4 节](deployment.md#4-secrets-and-configuration)）。
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
[JoinOnConnect<Account>] public readonly partial record struct Player(long Id) : IActor<Player>   // §7
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(40).SetWallet(default).SetBuffer<Slot>();   // saved: its inventory and wallet
}
public readonly partial record struct Guild(long Id) : IActor<Guild>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetQuests(default);   // saved: §15's reminders live under it
}
[TickRate(30), ActivatedBy<EnterMap>]                                             // 30 ticks a second; only EnterMap creates a map
public readonly partial record struct Map(long Id) : IActor<Map>
{
    [Persist]                                                                      // saved: the map has a record
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetVisitors(default).SetBuffer<Shout>();   // the root's own components
    public static bool CanCreate(in Account who, in EnterMap m, in MapTable maps) => maps.Has(m.Map);   // §4: before the claim
}
[ActivatedBy<JoinRoom>, ActorOptions(PresencesPerClient = 16, DetachGrace = 0)]   // the chat room at the top
public readonly partial record struct Room(long Id) : IActor<Room>;
[Stateless] public readonly partial record struct Pricing(int Region) : IActor<Pricing>;   // any node, never persisted
```

**地址**：`new Map(7)` 就是 7 号地图这个 Actor 的地址。消息和组件里存别的 Actor 的地址，直接用 `Map`、`Player` 类型的字段。要存"任意 Actor 的地址"时用 `ActorId`。地址只在服务端：客户端看不到 `ActorId`、`PresenceId`、`EntityId`，也看不到节点（第 4 节）。

**同一个值也是组件**：地图的根实体上带着 `Map(7)`。
- 按键找实体：`world.MapIndex.TryGet(new Map(7), out var e)`，用的是 EasyEcs 的值索引；
- 处理方法里知道自己是谁：取 Actor 类型参数 `in Map map`，它总是绑定到当前项所在 Actor 的根，不论主体是根还是子实体（第 5 节）。

**接口**：`IActor<T>` 继承 `IComponent`、`IUniqueIndexed<T>`、`IEquatable<T>` 和 `ISystemGroup`，并带着组过滤 `In<T>`，还有 `ISystemGroupWithout<Deleting>`（框架标签，打在整棵子树上，所以删除中的 Actor 的根和子孙都不跑这个类型的系统，第 5 节）；子实体的键直接实现 EasyEcs 的 `IComponent, IUniqueIndexed<T>`，没有框架自己的接口。键都是唯一值索引（EasyEcs 的 `IUniqueIndexed<T>`）：一个 world 里两个同键的根、两个同 `NetId` 的子实体都是错误，会让键重复的写入在改动任何东西之前抛出 `InvalidOperationException`（`guide/indexes-and-relations.mdx` 的 "Unique keys"）；查找用生成的 `world.NetIdIndex.TryGet`（`Get` 在没有时抛出）：

```csharp
public interface IActor<TSelf> : IComponent, IUniqueIndexed<TSelf>, IEquatable<TSelf>, ISystemGroup, ISystemGroupWith<In<TSelf>>,
                                  ISystemGroupWithout<Deleting>
    where TSelf : struct, IActor<TSelf>
{
    static virtual Prefab? RootPrefab => null;   // null: no root components; an actor type with root components declares its own
}
```

- `IEquatable<TSelf>` 是继承的接口，不是约束：值索引要求 `T : IEquatable<T>`，写成约束的话，每个用到 `IActor<T>` 的泛型都要重复这条约束，否则 CS0314。record struct 本来就实现它。
- **根的 prefab 是 Actor 类型上的静态属性** `public static Prefab RootPrefab { get; } = …`：`Registration` 按 `K.RootPrefab` 读它，建占位和根都用它的副本（第 6 节）。没有根组件、也不保存的 Actor 类型不写：默认是 `null`，`Registration` 当作"根上没有用户组件"，用一个只带框架组件的副本（EasyEcs 的 `Prefab` 没有共享的空实例，共享一个也会被 `SetX` 改掉）。要保存的 prefab 一律写 `[Persist]`（第 10 节）：根的写在 `RootPrefab` 上，属于哪个 Actor 类型由所在的类型推出，不写 `[Prefab<K>]`；子实体的 prefab 写 `[Prefab<K>, Persist]`，`[Prefab<K>]` 只说它属于哪个 Actor 类型。叫 `RootPrefab` 不叫 `Root`：`e.Root` 是实体所在 Actor 的根实体（第 5 节）。

- EasyEcs 认源码里声明的接口，包括继承来的：它的生成器和分析器都用 `AllInterfaces` 判断（`Symbols.Implements`）。所以：
  - `[UpdateInGroup<TGroup>]` 的约束是 `struct, ISystemGroup`，C# 认继承来的接口；
  - EasyEcs 的分析器判断"是不是系统组"时，EECS023 不会误报。
  - EasyEcs 的生成器看不到别的生成器补上的接口和特性（它的分析器看得到：分析器在所有生成器之后、对整个编译跑），所以要让它生成代码时看见的，都由用户写的接口或特性带着。要保存的组件类型的 `ITracked` 因此由用户写在源码里（第 10 节的"只写改过的"），生成器不补。
- **组过滤由接口带上（EasyEcs 的 `ISystemGroupWith<T>`，4.5 引入，4.7 起叫这个名字）**：EasyEcs 从组类型实现的接口上读组过滤，`IActor<TSelf>` 带着 `In<TSelf>`，所以 `[UpdateInGroup<Map>]` 的每个 `[ForEach]` 方法都只看 Map 的实体，用户不写 `[With<In<Map>>]`。不能写成接口上的特性 `[With<In<TSelf>>]`：C# 不允许特性的类型实参用类型形参（CS8968）。
- **`Key` 由生成器补上，写成显式接口实现** `Map IIndexed<Map>.Key => this;`（`IUniqueIndexed<TKey>` 继承 `IIndexed<TKey>`，自己不声明 `Key`，所以唯一键也是这一个成员，4.7 源码如此）：`IActor<T>` 和以自身作键的 `IUniqueIndexed<T>`、`IIndexed<T>` 的 `partial record struct` 都一样，Actor 类型、`Avatar`、`NetId` 和第 12 节的非唯一索引 `Cell` 都是。用户不写 `Key`。生成的那一半只重复 `readonly` 和 `record struct`，不重复位置参数表（否则 CS8863 或修饰符不一致）。这些类型没写 `partial` 时，分析器报错（DAS025）并给一键修复，而不是让 C# 报一个看不懂的 CS0535。不能写成公有属性 `public Map Key => this;`：record 合成的 `ToString()` 会打印 `Key`，`Key` 又是自己，无限递归到栈溢出。
- EasyEcs 生成的成员是属性，不带括号：组件是 `e.HasLoot`，tag 是 `e.IsDead`。
- **按值找实体只有一种写法**，就是 EasyEcs 生成的索引成员：唯一键 `world.NetIdIndex.TryGet(key, out var e)`（一定有时 `Get(key)`），非唯一键 `world.CellIndex.Find(key)`（枚举器，不分配）。
- Actor 类型的 id 是 `TypeId<Map>.Value`，没有单独的常量。
- `[TickRate]` 的规则在第 5 节。

**子实体的值索引，键里带上 Actor 的键**：一个 world 里有很多 Actor，所以子实体的键必须带着它所在的 Actor，否则两张地图的同名键会撞。规则只有一条：Actor 里建出的实体上，有值索引的组件（`IUniqueIndexed<T>` 或 `IIndexed<T>`）第一个字段是 Actor 类型（分析器检查），例如唯一的 `NetId(Map Map, int Value)`、`Avatar(Map Map, Player Player)`，非唯一的格子 `Cell(Map Map, int X, int Y)`。Actor 的键不随迁移和恢复改变，不用改写；不用根的 `EntityId` 作键。框架的 `PresenceId`（全 world 唯一的随机值）除外。

**只有运行时能写 Actor 类型组件：**
- 分析器禁止用户代码对 Actor 类型组件 `Set`、`Remove`、`Disable`、`SetAll`、`RemoveAll`，也禁止把它放进 `CreateEntity` 或 prefab。
- 运行时建占位时，唯一索引保证这个键在 world 里还不存在（重复就抛出，是运行时的错误）。占位就是以后的根（第 6 节），键从建占位起一直在它上面，不搬动。
- 运行时按键找根用同一个索引的泛型写法 `world.Index<K, K>().TryGet(key, out var root)`（`world.MapIndex` 就是它）：泛型的 `Actor<K>` 取一次、按 world 缓存，生成的注册代码里没有查找委托（第 12 节）。

**键**：
- Actor 类型的字段合计不超过 16 字节，并且是非托管类型；
- 字符串键用 64 位哈希代替，原文另存在组件里。

**其他：**
- **`[ActorOptions]`**：Actor 类型可调的设置都在这一个特性里（`IdleTimeout`、`DetachGrace`、`MaxPending`、`MaxMail`、`MaxMailBytes`、`MaxWaitingMail`、`PresencesPerClient`、`Unacked`、`OnceWindow`、`OnceInFlight`、`OnceQueued`），各自的规则在引用的那一节，不写就是默认值。`[TickRate]` 和 `[Stateless]` 不放进去：帧率定义了这个类型的系统组多久跑一次，`[Stateless]` 改变的是这个类型是什么（不保存、不迁移、每个节点都能有，分析器据此禁止一些操作），都不是调参。
- **`[Stateless]` Actor 类型**：不保存，不迁移；每个节点可以有自己的 Actor，键只用来选 Actor。它的地址不含节点，回信、确认和到期都找不回同一个实例，所以分析器禁止在它上面 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、建 `Due` 的 `Schedule`（带 `after`，或在系统里）。它只做"收到请求，当场回复"。
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
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient           // pooled: Pooled<Nearby>.Rent(), returned by Push
{
    public List<NearbyEvent> Events = new();
    private static partial IEnumerable<Nearby> OnNinoPoolWarmup() => null;   // Nino's two hooks (NINO019): start empty,
    private partial void OnNinoPoolReturn() => Events.Clear();               // and clear what the next use must not see
}
```

- `IFromClient` 是客户端发的单向消息，`IFromClient<TReply>` 是客户端发的请求，`IToClient` 是推给客户端的消息。
- **`[Latest]`：只要最新的**（移动输入、视角、拖动中的位置这类状态）：写在单向消息上（`IFromClient`、`IMessageTo<K>`）。同一个发送方发给同一个目标的这种消息，邮箱里还有一条没处理的旧的时，新的一条到达就替换它：
  - 旧的那条是这个发送方发给这个目标、排在最后的一项时（它后面没有这个发送方的别的消息），新的**就地替换**它，留在旧的位置、用旧的时间。几万个发送方一起高频发送、邮箱一直积压时，每个发送方的那一条照样往前走、轮得到处理；排到队尾的话，每一条都被下一条挤回队尾，一条也处理不了。
  - 旧的那条后面还有这个发送方的别的消息时，丢掉旧的、新的排在队尾：占旧的位置就会跑到它们前面。所以到达的总是发出的那一串里按原顺序的一部分，从不和别的消息换位置。
  - **发送方**：服务器之间是发送的 Actor；客户端是它的 `ClientId`（同一次登录换了连接、换了网关也是同一个发送方）。每条 `[Latest]` 消息带一对 `(发送方的代, 序号)`，按字典序比较：代是服务端发送方这次激活的认领 epoch（来自存储的序列，认领时就已落定、单调递增，第 10 节；不用另记一个激活计数：那个要等下一次写才保存，宕机在那之前就会重用同一代而计数又从头开始，新消息会被当成旧的一直丢掉）、客户端的是这条连接的连接戳的尝试号（Unix 毫秒的起点，取自己的加一与目标回报的加一中较大的，第 7 节、[architecture.md 第 3 节](architecture.md#connection-stamps)；会话随机数是随机的，排不出先后），序号在一代里按（发送方, 目标, 类型）数。接收方记着处理过和排着的最大的一对，更旧的直接丢掉：迁移时墓碑转来的旧的一条、重连前旧网关上还在途的一条，到得比新的晚，也盖不掉新的；发送方宕机后重新激活、应用重启，计数从头开始，但代更大，新的消息照样比旧的新。这张表按 Actor 记（每个发送方和类型一项，过了 `DaServer:Mailbox:LatestWindow`（10 秒）没有新消息就删），随迁移和交接走。邮箱满（`MaxMail`）时，原地替换的一条照样进来（它不占新的位置），追加的一条按满处理。
  - 替换在接收方的邮箱里做（占位上等着的邮件、`Commit` 留住的输出里也是），按（目标的根, 发送方, `TypeId`）记着最后一条没处理的，O(1)，不分配；被替换的、因为序号旧被丢掉的都计数（`daserver.messages.superseded`）。Actor 忙不过来时，这种消息自然只剩最新的一条，其余的消息一条不少。
  - 写在请求（`IFromClient<R>`、`IRequestTo<K, R>`）、推送或 Topic 的消息上，分析器报错（DAS030）：请求一定要有结果，推送和 Topic 的投递不经过 Actor 的邮箱。
- 含集合的消息是 `[NinoPooled]` 类（Nino 4 的规则），写上 Nino 要求的两个钩子（`OnNinoPoolWarmup`、`OnNinoPoolReturn`，缺了是 NINO019）。服务端用 `Pooled<T>.Rent()` 取一个（DaServer 的帮助方法，取 Nino 池里的实例，没有就新建），`Push` 序列化之后把它还回池里，所以推送不分配；客户端收到的由 Nino 反序列化时从池里取。
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
  - **键的一部分来自身份**：客户端说出的键不能信，例如每个用户一份的 `Order(long Sale, long User)`：让客户端写 `User` 就能替别人下单。这时消息只带客户端可以说的字段，Actor 类型写一个静态方法从身份补上其余的，生成的路由在 `Connection` 上调用它（身份就在那里）：
    ```csharp
    [NinoType] public record struct PlaceOrder([To] long Sale) : IFromClient<OrderStatus>;      // Protocol: no User field
    [ActivatedBy<PlaceOrder>]
    public readonly partial record struct Gate(long Sale, int Shard) : IActor<Gate>             // not saved: losers leave no row
    {
        public static Gate Route(in Account who, in PlaceOrder m) => new(m.Sale, (int)(who.Id % SaleShards.Count));
        public static bool CanCreate(in Account who, in PlaceOrder m, in SaleTable sales) => sales.Has(m.Sale);
    }
    public static class SaleShards { public const int Count = 16; }                            // a constant: Route reads no table
    [NinoType, From<Gate>] public record struct Place : IRequestTo<Order, OrderStatus>;        // Gate → Order(sale, user): no fields,
    ```                                                                                         // the key already says who
    有 `Route` 时 `[To]` 的类型不必和键形状一致；键里有消息没带的字段却找不到对应的 `Route`、或者 `Route` 的身份类型不是工程的 `IClaims`，分析器报错（`[To]` 和键形状一致的消息，例如 `JoinRoom`、`EnterMap`，不用 `Route`）。秒杀样例里只有不保存的 `Gate` 接 `PlaceOrder`（一条客户端消息只有一个处理 Actor 类型）：它知道哪一场已经卖完，就地回答卖完的人，其余的用服务器之间的 `Place` 问 `Order(m.Sale, who.Id)`。用户 id 来自身份，所以买家不能替别人下单；`Order` 由服务器之间的消息激活，不需要 `Route` 和 `[ActivatedBy]`；抢不到的人只碰 `Gate`，不留行（ADR-091）。`Route` 是纯函数（不读 world，不取配置表），在网关上逐条执行，不分配；所以分片数 `SaleShards.Count` 是常量，改它要滚动网关。
- **`[To]` 只到已有记录的键**：客户端能随便编键，任何消息都能激活的话，一个随机的房间号就往存储里插一行。所以带 `[To]` 的客户端消息只激活存储里已经有记录的键；要让某条客户端消息建出新的 Actor（开房间、建队伍），在 Actor 类型上写 `[ActivatedBy<这条消息>]`，处理方法负责校验它（第 6 节）。带 `[To]` 的客户端消息发给一个没写 `[ActivatedBy]` 的 Actor 类型时，分析器给警告（DAS024，说明它只到已有记录的键），确实如此的可以压掉。规则只有一条：没写 `[ActivatedBy]` 的类型，服务器之间的消息都能建键，客户端消息都不能；写了 `[ActivatedBy]` 的类型，只有列出的消息能建键，服务器之间的消息也一样（列表就是这个类型全部能建键的消息），只有 `Wake` 和 `[JoinOnConnect]` 的加入例外（第 6 节）。根不保存的 Actor 类型没有记录，所以没写 `[ActivatedBy]` 时客户端的 `[To]` 消息到不了它，写了时没列出的消息（服务器之间的也一样）都回 `NoActor`；它的激活不在存储里留行（第 10 节）。
- **建键之前的纯函数门**：`[ActivatedBy]` 旁边可以写 `static bool CanCreate(in TClaims who, in TMessage m)`（客户端消息；服务器之间的消息没有身份，写成 `static bool CanCreate(in TMessage m)`；形状不对分析器报错）（不读 world，不分配；除了身份和消息，最多再取一张 `in` 的配置表，它是节点上不可变的快照；取两张以上分析器报错，需要的数据放进同一张表），在占 epoch 之前、在主人节点上执行。返回 `false` 时不建键：保存的 Actor 类型用不插行的占法（只认已有记录的行）激活，键有记录就照常激活，没有记录才回 `NoActor`，存储里什么也不写。例如第 12 节的 `Map`：地图号要在地图配置表里；一张地图从表里下架之后，它已经写过的记录还在，发给它的消息照常激活它（处理方法自己决定下架的地图怎么回答），没写过记录的号才是 `NoActor`。根不保存的 Actor 类型没有记录可认，`CanCreate` 返回 `false` 就是 `NoActor`。写了 `CanCreate`、它的消息却不在 `[ActivatedBy]` 里，或者有 `CanCreate` 形状的静态方法名字不对，分析器报错。
- **客户端消息直接到目标**：网关上的 `Connection` 实体过滤后直接转发给目标 Actor，中间不经过 Player（第 7 节）。

### 服务器之间的消息（Server）

```csharp
// Game.Server: never shipped to clients
[Once, NinoType, From<Loot>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // runs once on the player
[NinoType, From<Map>] public record struct Defeated(Map Where) : IMessageTo<Player>;
```

- **接收方写在接口上**：`IMessageTo<TTo>` 是单向消息，`IRequestTo<TTo, TReply>` 是请求（它继承标记接口 `IRequest`，分析器据此认出请求，例如 `IsPending<T>` 的 T 必须是 `IEvent` 或 `IRequest`）。
- **发送方写在特性上**：`[From<K>]`，可以写多个。客户端只能发 `IFromClient`。
- **把价值转进别的 Actor、不能执行两次的请求标 `[Once]`**（发物品、入账），只能用 `AskUntilAnswered` 发出；规则全在第 8 节。订单本身是自己的 Actor（`Payment`、`Order`），重复由它的状态回答。框架不按调用 id 去重别的请求。
- **消息里不写发送方的键**：接收方取 `Sender sender` 参数：`sender.Id` 是发送方的 `ActorId`，`sender.TryGet(out Map map)` 在发送方是 Map 时取出它的键（`TryGet<K>(out K key) where K : struct, IActor<K>`）。只有这一个 `Sender` 类型，所以一条消息有一个还是多个 `[From<…>]` 写法都一样。从 `ICluster` 或客户端来的消息没有发送方 Actor：`sender.Id` 是 `default`，每个 `TryGet` 都返回 `false`。
- **方向由生成的方法和分析器保证**：
  - 每条消息生成 `Entity` 上的 `Send`/`Ask` 方法，参数类型是接收方的 Actor 类型，所以接收方写错编译不过；
  - `Entity` 不带 Actor 类型，所以发送方由调用处决定：`[Handles<K>]` 类和 `[UpdateInGroup<K>]` 系统里，K 不在消息的 `[From<…>]` 里，分析器报错（`DaServer.Topics` 包自己的 `Subscribe`、`Unsubscribe`、`Publish`、`NotSubscribed` 不写 `[From<…>]`，不受这条检查，第 15 节）；在别处（帮助类）调用时，Debug 构建沿 `ChildOf` 走到根、断言它的 Actor 类型，Release 不做这一步。
  - 这是帮助写对的手段，不是安全边界。安全边界在网关：只转发 `IFromClient`。
- **发送方是哪个 Actor**：发送实体所属的 Actor（`e.Root`），见第 5 节的操作表。
- **客户端上下文自动带上**：处理客户端消息、`PresenceLost` 时，发出的请求信封里带着这个客户端的上下文（`ClientId`、所在节点和 `ConnectionId`、连接戳、身份、客户端的调用 id），和期限一样。接收方在处理这个请求时可以 `Join()`（第 7 节）。
  - 用户不接触上下文本身；它在运行时的单例 `Current` 里（第 5 节）。系统里、`Activated` 里和定时事件里没有客户端上下文，从那里发出的请求也不带：接收方的 `Join()` 得到 `NoClient`（第 7 节），不是默认值。
- **`ICluster`**（HTTP、后台、测试）不属于任何 Actor，可以发任何服务器之间的消息，回复回到调用方：`await cluster.Ask(to, msg, deadline)`，期限也可以换成 `CancellationToken`。
  - 期限作为预算发出（第 9 节）；取消只停止等待，已经发出的请求照常执行，结果未知，同超时；
  - 外部的重试（例如支付平台重发同一个 webhook）问的是同一个 Actor（`Payment(单号)`），由它的状态回答（第 8 节的支付样例）；`ICluster` 不能发 `[Once]` 的请求（它不保存，没有序号可以接着数，DAS032）；
  - 节点热重启期间，它的 HTTP 平面不可用（进程已经退出、还没起来），外部调用方按自己的重试处理；
  - 踢人用 `cluster.Disconnect(clientId)`：发给所有承载 `Connection` 的节点，每个节点问它的每个 world，由连着它的那个处理。

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
| Topic 的投递 | `ITopicMessage`（`DaServer.Topics`，第 15 节）：写在订阅它的任何 Actor 类型的类里，主体是根 | `[On<PriceChanged>]` |

- 一个请求只能有一个处理方法；一条客户端消息只能有一个处理 Actor 类型。
- 离开、确认、断线、停用、热重启不是消息，是 tag 和组件：`Leaving`、`Joined`、`Detached`、`Deactivating`、`Restarting`、`Resumed` 由系统反应（第 6、7、10 节）。`[On<T>]` 写这些 tag，分析器报错，修复是"写一个 `[ForEach, With<T>]` 的系统"。

### 处理方法是 EasyEcs 的单实体 job

处理一个实体就用 EasyEcs 自己的做法：`entity.Run<T1..Tn, TJob>(ref job[, filter])`，job 是 `IForEachEntity<T1..Tn>`（EasyEcs `guide/queries.mdx` 的 "One entity: `entity.Run`"、`decisions.mdx` 的 D33，Actors 样例就是这样写的；基准是同样工作手写代码的 0.69–1.06 倍，0 分配）。DaServer 不另造执行的方式，生成器只替用户写那个本来要手写的 job 结构和分发表：
- job 的 `Execute` 调用 `[On<T>]` 方法；消息、发送方、回复令牌、身份、Actor 类型、单例是 job 的字段，由分发表在调用前填好。方法按 `ref` 收单例（`IWorldSingleton<T>` 可写）时，字段要是引用，所以生成的 job 是 `ref struct`，这个字段是 `ref` 字段（`entity.Run` 的约束带 `allows ref struct`）；复制一份当字段的话，对单例的写会悄悄丢掉（生成器测试 `RefSingletonWriteReachesWorld`）。按 `in` 收的消息和单例照常复制进字段（复制看不出差别），所以没有 `ref` 单例的 job 是普通的 struct（第 12 节的 `Move_Job`）。
- `WriteMask` 和 `SelfStamped` 按 EasyEcs 自己的约定写（它的 `SystemGenerator` 就这样生成，4.7 的迁移指南把两者并列）：`WriteMask` 是 `ref` 和 `Mut<T>` 参数的位，`SelfStamped` 是 `Mut<T>` 参数的位。`WriteMask` 还决定结构变更之后哪些引用被搬到新行，所以 `Mut<T>` 必须在 `WriteMask` 里，否则改了结构之后经 `Mut` 的写会丢；`SelfStamped` 让 `Mut<T>` 只在真写时打戳，`RunJob` 对其余的位照常打戳；`[With<T>]`、`[Without<T>]` 编成 job 上建一次的 `static readonly QueryFilter`；`Entity`、`World` 参数只交给不改结构的操作（`Send`、`Push`、`Reply`、`Root`、`Group`、`Everyone`、`SetGroupRate`、`SetGroups`、`TryGetClient`）时 job 带上 `INoStructuralChanges`，规则同 EasyEcs（`guide/structural-changes.mdx`）。`Commit` 置的标记（`WriteNow`）是根上一直在的框架组件 `RootState` 的字段，不是 tag，框架记进 `Unacked` 的是根副本带着的缓冲，所以它们不改结构。`Ask`、`Join`、`Leave`、`Spawn`、`Deactivate`、带 `after` 的 `Schedule`、系统里的 `Schedule`、`Set`/`Remove`/`Destroy` 算结构变更。
- 所以 `ref` 参数的语义就是 `entity.Run` 的：直接指向存储，方法体里改了结构照样可写，`in` 参数保持改之前的值，只有 `WriteMask` 打戳（`guide/refs-and-await.mdx`）。组件参数都是必有的（`IForEachEntity` 不收 `Maybe<T>`）：可能没有的数据用 `TryGetX(out …)` 读。主体缺组件或不满足过滤时 `Run` 返回 `false`，不调用：请求以 `Skipped` 失败，单向消息和事件跳过（计数）。
- **没有组件参数的处理方法**（第 12 节的 `Enter`、`Cast`）直接调用：`entity.Run` 至少要一个组件。分发表检查 `subject.IsAlive` 和过滤里的 `Has<T>()`，通过就调用。
- **检查只补 EasyEcs 看不到的**：EasyEcs 的分析器检查 `[ForEach]` 方法，不检查手写或生成的 job，所以 DaServer 的分析器对 `[On<T>]` 方法报同样的错。编号是 DaServer 自己的（DAS026，严重级别、`#pragma` 和帮助链接各管一份实现），说明里写出对应的 EasyEcs 规则（EECS002、EECS011、EECS012、EECS013、EECS015，以及下面 4.7 的几条，`guide/analyzer.mdx`；提示 EECS025、EECS026 照样给）和它的链接，不另写一套规则；等 EasyEcs 加上"把框架方法当作 job 的形状来检查"的特性（第 14 节的需求 (d)），这条就删掉，由 EasyEcs 自己报。它自己的规则只管 EasyEcs 不知道的东西（绑定、身份、`[Handles<K>]`，第 13 节）。EasyEcs 4.7 新加的、对方法体也成立的规则同样照报：EECS038（改了副本却没写回）、EECS040（对不能比较的类型 `SetIfChanged`）、EECS041（遍历时会被拒绝的调用）、EECS047（结构变更之后还在用的 `ref`：重新取）、EECS052（建出实体之后紧跟着 `Set`：把组件交给建它的调用）；所以原来的 DAS022（`ref` 跨过结构变更）退役，由 DAS026 照 EECS047 报。

### 一个绑定规则：参数绑定到主体

每条消息、每个事件都有一个**主体**实体。`Entity` 参数就是主体，组件参数（`in`/`ref`）和过滤总是针对主体。

| 进来的是 | 主体 |
|---|---|
| 带 `[To]` 的客户端消息 | 目标 Actor 的根实体，不论这个客户端是否已经在上面有 Presence |
| 不带 `[To]` 的客户端消息，处理 Actor 类型带 `[JoinOnConnect]` | 这个 Actor 的根实体（例如 Player 的根：背包、钱包这些每个玩家一份的状态都在根上）；这个客户端的 Presence 用 `root.TryGetClient(out var me)` 取 |
| 不带 `[To]` 的客户端消息，其余的处理 Actor 类型（不是 `Connection`） | 这个客户端在目标上的 Presence 实体，例如地图上它的 Avatar |
| 客户端消息，`[Handles<Connection>]` 处理 | `Connection` 的根实体 |
| `Incoming`、`PresenceLost` | `Connection` 的根实体 |
| 本地事件 | 它被发给的实体：`caster.Schedule(new Strike(…), after: …)` 的 caster |
| 服务器之间的消息、`Activated` | 接收 Actor 的根实体，例如地图 |
| `Replied` | 发问的实体（第 8 节） |

- **两个例外，都不看主体**：
  - **Actor 类型参数**（例如 `in Map map`）总是绑定到当前项所在 Actor 的根上的键，主体是子实体也一样。这是知道"自己是谁"的唯一写法；它不会因为主体是子实体而 `Skipped`。
  - **身份参数**：`IClaims` 类型的参数（例如 `in Account who`）绑定到发这条客户端消息的客户端的身份。身份随信封带着（不超过 16 字节）。只能用在客户端消息的处理方法里（分析器检查）。
- 系统没有当前项，所以 `[UpdateInGroup<K>]` 系统的 `[ForEach]` 方法不取 `IClaims` 参数（分析器报错）。Actor 类型参数 `in K` 可以取，它就是一个普通的组件参数，只有根带着它，所以这个方法只遍历这个类型的根（每个 Actor 每 tick 一次的逻辑，例如匹配队列的一趟、帧同步房间的一帧）；子实体的方法要所在 Actor 的键，读 `e.Root` 上的组件。系统方法取 `in K`、其余的组件参数（和 `[With<T>]`）却不全在 `K.RootPrefab` 上时，`Registration` 构造时抛出：那样的方法一行也匹配不到（同处理方法的检查，下面）。
- 主体在表里一定是根时（带 `[To]` 的客户端消息、`[JoinOnConnect]` 类型的客户端消息、服务器之间的消息、`Activated`），`Presence`、`Joined` 这类只在 Presence 上的组件参数和过滤永远不满足，分析器报错。
- **组件参数主体上没有，不等到运行时才发现**：主体是根时，组件参数（和 `[With<T>]`）要在 `K.RootPrefab` 上；主体是 Presence 时，要在建它的 prefab 上。哪个 prefab 带哪些组件，生成器看不到（prefab 是用 EasyEcs 生成的 `SetX` 成员搭的，生成器看不到别的生成器的输出），所以这条在 `Registration` 构造时检查：第一次跑测试或启动节点就抛出，指出处理方法和缺的组件，不会变成运行时一条条消息的 `Skipped`（`Skipped` 只留给运行时被删掉组件的实体）。

**别的实体在方法体里显式读写：**

```csharp
if (!world.TryGetEntity(s.Target, out var target) || target.IsDead || !target.HasHealth) return;
ref var hp = ref target.Health;
```

- 首选的写法：发送方把接收方需要的数据放进消息，接收方少读别的实体。
- **客户端说出的东西都要校验**：客户端消息里的编号可能指向这个 Actor 里的任何实体，用之前确认它是该有的那种（第 12 节的 `Cast` 查 `HasHealth`，`Take` 查 `HasPrize`、看得见、够得着）。
- 用 EasyEcs 生成的成员：tag 是 `me.IsDead`，组件是 `target.Health`，可能没有的组件用 `TryGetX(out …)`，主体上的也一样。
- 对主体的条件写成过滤（`[Without<Dead>]`），不写成方法体里的 `if (me.IsDead) return;`。

### 参数

| 参数 | 绑定到 |
|---|---|
| 消息或事件 `in T` | 这条消息、这个事件 |
| `Entity` | 主体 |
| 组件 `in`/`ref`/`Mut<T>` | 主体上的组件。缺少时不调用（上面的过滤）。`Mut<T>`（EasyEcs 4.7）读 `Value` 时不打戳，`Write()` 打戳，`SetIfChanged(v)` 值不同才写和打戳；生成的 job 同样处理它。有索引的组件（包括 `ChildOf`、`IUniqueIndexed<T>`）只能是 `in` |
| Actor 类型 `in K` | 当前项所在 Actor 的根上的键（上面的例外） |
| 身份 `in TClaims`（`IClaims` 类型） | 发消息的客户端的身份（上面的例外） |
| `in DynamicBuffer<T>`、`ref DynamicBuffer<T>` | 主体上的缓冲：只读取 `in`（不打戳，第 12 节的 `Items.Grant`），要改的取 `ref` 或经 `root.WriteX()`。EasyEcs 只在源码里出现过 `DynamicBuffer<T>`（参数、局部变量）时才生成 `WriteX()`，光有预制体上的 `SetBuffer<T>()` 不够：支付样例的 `Accept` 取 `in DynamicBuffer<Refund>`，所以 `root.WriteRefund()` 存在。缓冲的下标是只读的：改一个元素经 `AsSpan()`（`root.WriteRefund().AsSpan()[i].RefundedAt = …`），增删用 `Add`、`RemoveAtSwapBack` 等 |
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
  - `IConfigTable<T>`：调参的表，有记录存储时由集群统一读入、换版本（第 10 节的"不重启就能改的"）；处理方法和系统只能取 `in`，例如第 12 节的 `in SkillTable skills`。
  - `IWorldSingleton<T>`：world 自己的缓存或配置（例如寻路缓存），不是 Actor 的状态。生成的 `Registration` 列出处理方法和系统用到的每个这种类型，每个 world 建立时用 `T.Create()` 建一份。只有它能取 `ref`。
  - 两种都不迁移、不保存，也不随热重启交接（新进程自己读入或建，第 10 节），所以类型不用能序列化。框架的单例（`Time`、`Current`、`Outbox`、节点单例）只能 `in`。
- 每个 Actor 一份的状态放在它的根上（`K.RootPrefab` 的组件）。
- 违反时分析器报错：`A singleton is shared by every actor in this world: take it 'in'; only an IWorldSingleton<T> (a world-local cache or configuration) may be taken 'ref'; keep per-actor state on the actor root`。不实现这两个接口之一的用户单例类型也报错。
- 为什么：Overwatch 的单例是一局一份，因为一局有自己的 world（出自 [GDC 2017 的演讲](https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and)）；这里一个 world 有很多 Actor，可写的单例会让一个 Actor 的处理方法改到别的 Actor 看得见的数据，迁移时还丢掉它。

### 操作

只有一个句柄：`Entity`。所有发送、推送、加入的操作都是 `Entity` 上的扩展方法（DaServer 提供，通过实体所在的 world 找到 world），只写在这张表里：

| 写法 | 做什么 |
|---|---|
| `e.Schedule(ev)` | 本地事件，`ev` 是 `IEvent`，发给本 Actor 的实体 `e`。在项里：当前项返回后、下一项之前执行（即时链，最多 1,024 个），不建实体：事件是 world 的链栈里的一个值。在项外（系统里）：同 `e.Schedule(ev, after: TimeSpan.Zero)`（第 9 节）。返回 `Pending<T>` |
| `e.Schedule(ev, after: t)` | 同上，到期时刻 = `Time.Elapsed` + t（第 9 节）；建一个带 `Due` 的子实体。在项里 `t` 为 `TimeSpan.Zero` 时同上一行（即时链） |
| `e.IsPending<T>()`、`e.Cancel<T>()` | `e` 上还有没执行的 T 事件、或者还在等回信的 T 请求吗（链栈里的和子实体）；取消它们：事件不再执行，请求不再等，回信到了就丢弃（第 8、9 节）。一组扩展方法，`where T : struct`，分析器要求 T 是 `IEvent` 或 `IRequest` |
| `e.IsPending(p)`、`e.Cancel(p)` | `Schedule` 或 `Ask` 返回的那一个 `Pending<T>` 还在等吗；取消它 |
| `e.Send(to, msg)` | 服务器之间的单向消息，只发给别的 Actor，例如 `me.Send(new Player(id), new Defeated(map))` |
| `e.Ask(to, req[, state][, deadline: t])` | 请求，回信是 `Replied`，主体是 `e`；返回 `Pending<T>`，调用 id 是它的 `Call`。`deadline` 是这一问的超时，不写用 `DaServer:AskTimeout`（第 8 节） |
| `e.AskUntilAnswered(to, req[, state])` | `e` 是要保存的实体（根或要保存的子孙），`req` 是 `[Once]` 的请求：请求写成存储里的一行问（根的问的表，不在记录里），和这一项一起写成，轮到它发时才取序号发出，框架一直问到有回答，回答是 `Replied`，主体是 `e`（`e` 已经不在了就丢弃）；没有返回值，从不失败：根排着没发的问到了 `OnceQueued` 时，发给它的普通请求和客户端的 `[Once]` 请求在执行之前回 `Busy`，别的照常（第 8 节）；删除中的根上调用它是运行时错误（`root.Delete()`）（第 8 节） |
| `e.Reply(in token, value)` | 用回复令牌回复；令牌是 `default` 时什么也不做 |
| `root.Commit()` | `root` 是要保存的 Actor 的根：它在这个 tick 末就写记录，这一项发出的一切等写成才离开（第 8 节） |
| `e.Push(msg)` | `e` 是 Presence 或 `Connection` 的根：推给它的客户端 |
| `e.Push(in group, msg)` | 推给一个 `ClientGroup`（第 9 节） |
| `root.Everyone` | `root` 是 Actor 的根：这个 Actor 所有带 `Joined` 的 Presence 组成的 `ClientGroup`（保留的键 `ClientGroup.Everyone`）；推给整个 Actor 就是 `root.Push(root.Everyone, msg)`（第 9 节） |
| `root.Group(key)` | `root` 是 Actor 的根：本 Actor 的 `ClientGroup`，一个值，键带着 Actor 的 `ActorId`；不改任何数据（第 9 节） |
| `root.SetGroupRate(key, everyTicks: n)` | `root` 是 Actor 的根：这个组的帧每 n 个 tick 交出一次，写在根上（第 9 节） |
| `e.SetGroups(groups)` | `e` 是 Presence：把它的 `DynamicBuffer<Subscribed>` 换成 `ReadOnlySpan<ClientGroup>` 里的组（可以 `stackalloc`，第 9 节） |
| `root.JoinClient(out presence[, prefab])` | `root` 是 Actor 的根：当前客户端在这个 Actor 上的成员实体，按框架的索引 `(ActorId, ClientId)` 找到，没有就用 `prefab`（不写是空的）`Spawn` 一个，再 `Join()`。写法是 `root.JoinClient(out Entity presence[, prefab])`，返回 `Status`，和 `Join()` 一样。聊天室、匹配、队伍不用再各写一遍成员的键组件、`TryGet`、`Spawn` 和 `Join()`（第 7 节） |
| `root.TryGetClient(out Entity presence)` | `root` 是 Actor 的根：当前客户端在这个 Actor 上的 Presence（带不带 `Joined` 都算）；没有客户端上下文或不是成员时返回 `false` |
| `root.Spawn(prefab[, in a[, in b …]])` | 在 Actor 里建实体：用注册时的 prefab 副本，`world.Instantiate(副本, in new ChildOf(root), in a, in b …)`（EasyEcs 4.7，最多给 9 个组件：`ChildOf` 占一个，所以用户最多给 8 个，唯一键也算其中一个）直接写进最终的表，当场返回实体；唯一键在建实体之前核对，键已被占就抛出 |
| 按键找到或建出 | `if (!world.AvatarIndex.TryGet(key, out var e)) e = root.Spawn(prefab, key, …);`：就是 EasyEcs 的唯一索引加 `Spawn`，没有另外的操作。加入消息再来一次，找到的是同一个实体（第 7 节）；只在新建时才算的组件（花掉一个编号的）写在 `Spawn` 的参数里，找到时不求值 |
| `e.Join()` | 让 `e` 成为当前客户端在本 Actor 上的 Presence；返回 `Status`：`Ok`、`NoClient`（这一项没有客户端上下文）、`Superseded`（第 7 节） |
| `e.Leave()` | Presence `e` 离开：加上 `Leaving`（第 7 节） |
| `root.Deactivate()` | `root` 是 Actor 的根：停用这个 Actor（第 6 节）。不是根时运行时断言；分析器看得出来时报错 |
| `root.Delete()` | `root` 是要保存的 Actor 的根：删掉这个 Actor 的记录，**一条单向的路**。返回 `Status`：根还有问的行（排着的、已发出的、停住的都算）时回 `Outstanding`，什么都不删（不悄悄丢下一个还在问的意图；等它们的 `Replied` 之后再删）；否则 `Ok`，根进入"删除中"（框架标签 `Deleting`，随这一项的写落下，这一项留住的输出随这次写放出）。**删除中的根什么都不再做**：标签打在根和整棵子树上（之后在它下面生成的实体也带上），`IActor<T>` 声明了 `ISystemGroupWithout<Deleting>`，所以这个类型的任何系统和 `[ForEach]` 都不碰根和它的子孙，删除中的 Actor 不会从系统里发出任何东西；提醒和 `Schedule` 的事件不再投递（`NextDue` 只算删除用的那个唤醒），还在等的普通 `Ask` 取消（迟到的回答当作没有等它的实体丢弃），拿着它给的 `ReplyTo` 的调用方等到自己的超时（它们的请求执行过了，不回一个说没执行的状态），新的请求回 `Busy`（不存下，不是定论），单向消息丢弃、计数（`daserver.messages.dropped{reason=deleting}`），只有 `[Once]` 的重复照样回答 `Unacked` 里存着的回复。它也不能再问或发：`Delete()` 返回 `Ok` 之后，同一项里再 `Send`、`Ask`、`AskUntilAnswered`、`Push`、`Schedule`、`Remind` 是运行时错误，这一项按 `Reload` 丢掉（`Delete` 也随之撤销，什么都没发生；计数 `daserver.delete.send_after`，在一个回答的项里会随回答重新投递而重复，所以重复时告警），分析器看得到的 `Delete()` 返回 `Ok` 的路径上这是编译错误（DAS033，第 13 节），运行时的检查只兜住它看不到的路径。框架给它挂一个随记录保存的唤醒（框架的 `WakeAt`），所以宕机、停用之后由 `DueScan` 叫醒，加载时重新检查，接着删（测试 `DeletingRecordResumedAfterCrash`）。**真正删除**要三样都成立：`Unacked` 已空（每个存下的回复都被确认；探测限住等待，约一个 `ProbeAfter`；客户端会话的项到"可删于"）、没有问的行、并且从 `Deleting` 那次写落定和它最后一次收下 `[Once]` 请求两者中较晚的那一刻起过了宽限 `MaxAskTimeout + 2 × ClockSkew`（这段时间里迟到的尝试照样碰到这个根，得到存着的回复、`Stale` 或 `Busy`，不会激活一个新的；过了它，删除之前发出的尝试都已过时，接收方按发出时刻丢弃）；而且只在 `Deleting` 那次写落定之后。删除是一个函数（`daserver_delete_actor`，第 10 节）在一个事务里的四条语句：按 epoch 比较删记录、删掉它的问的行（`Outstanding` 保证本来就没有）、给它作为 `[Once]` 发送方的行写下"忘于"、给它作为接收方的行写下"孤于"（行留到那时，第 8 节）；之后销毁，不再写。重试时（上一次提交的确认丢了）函数回"没有记录"或"被围栏"都算已经删掉。删除中的根不迁出（再平衡跳过它），冻结写下它的标签，新进程接着删。之后到达的邮件按 `[ActivatedBy]` 照常可能激活一个新的 Actor（新记录，序号的起点更大；它认领之后读到旧的行，旧发送方迟到的已执行的尝试回 `Stale`）。根不保存的类型用 `Deactivate()`（DAS031）。样例：支付过了保留期（`Purge`）、秒杀结束后的订单 |
| `connection.Disconnect()` | `connection` 是 `Connection` 的根：断开这个客户端 |
| `root.IssueClientToken(in claims, lifetime, into)` | `root` 是 Actor 的根：签发一个客户端令牌（第 7 节）：新的 `ClientId`，签发序号是根上框架保存的计数器加一；写进 `into`（`Span<byte>`，至少 `SignedToken.MaxLength` 字节），返回写入的字节数。隐含 `root.Commit()`：令牌在签发序号写成之后才离开；只能在根要保存的 Actor 类型上调用（分析器） |
| `e.Root` | `e` 所在 Actor 的根：沿 `ChildOf` 往上走的循环（EasyEcs 文档的祖先遍历）。实体不挪到另一个 Actor 的根下：框架的标签、`Persisted.Root` 和记录都按子树算，记录收集时发现子树里 `Persisted.Root` 指向别的根的实体，这个根这次写不下（`RecordUnwritable`，记日志）；要换根就在新根下 `Spawn` 一个新的 |

- **跨 Actor 的操作，发送方是 `e.Root` 那个 Actor。**
- **发出的东西先是数据**：`Send`、`Ask`、`Reply`、`Push` 和 `Join()` 的路由，都追加到运行时的单例 `Outbox`：追加时就序列化进按目标节点分的池化 `FrameWriter`，组推送只序列化一次，每个网关一帧。有序阶段结束时（没有 tick 的 Actor 类型是每批邮件之后）和收尾之后，运行时把这些帧交给链路一次（调用了 `Commit` 的项发出的等记录写成，第 8 节）；同一个目标的帧按追加的顺序发出，等着写成的挡住同一对之后的。所以 `Join()` 的路由一定排在回复前面（第 7 节），`TestActor<K>` 把 `Outbox` 解码成数据断言（第 12 节）。
- **当前项的上下文也是数据**：运行时的单例 `Current` 里是当前项的时间、根、发送方和客户端上下文。操作从它取客户端上下文；没有时是明确的结果（`Join()` 返回 `NoClient`），不是默认值。
- `Send`/`Ask` 的方向检查见第 4 节。
- 本地事件只能发给本 Actor 的实体，运行时断言。

### 每 tick：Actor 类型就是系统组

每 tick 的逻辑就是普通的 EasyEcs 系统，DaServer 不另造一套（例子见第 12 节的 `Steering`、`Movement`）。

- **Actor 类型就是它的系统组**：`IActor<T>` 继承 `ISystemGroup`（第 3 节），`[UpdateInGroup<Map>]` 就是"每个 Map tick 跑一次"。不另写组类型。
- **帧率写在 Actor 类型上**：`[TickRate(30)]`。
  - 帧率写进 `wire.manifest` 的 Server 一段；改帧率就是改了 Server，要滚动升级。
  - 一种服务器承载多个有 tick 的 Actor 类型时，各帧率必须整除其中最快的一个（例如 60、30、10），避免节拍抖动。
  - 没有 `[TickRate]` 的 Actor 类型也可以有系统：每批邮件之后跑，world 空闲时每 `DaServer:QuietPass`（1 秒）也跑一趟（第 1 节）。所以这种组里的每个 `[ForEach]` 方法必须只访问有事的行：带 `[Changed<T>]`、`[Removed<T>]` 或过滤一个 tag 的 `[With<T>]`，或者系统写了 `[EverySeconds(n)]`；一样都没有就是每批邮件一趟全扫，分析器报错。要"每 N 秒一次"用 `[EverySeconds(n)]`：运行时等 `Time.Elapsed` 过了下一个时刻才跑它。不用 `UpdateInterval`：EasyEcs 的 `UpdateInterval` 数的是组的更新次数，不是时间（`update % interval`），而这种组每批邮件更新一次，次数不规则，空闲时还不更新；它只用于有 `[TickRate]` 的类型上"每 N 个 tick 一次"。`[EverySeconds]` 在两种类型上都能写：按时间就用它，按 tick 数才用 `UpdateInterval`。代价相差约 70 倍：EasyEcs 复制 10 万个实体里改过的 10 个，只走改过的行是 525 ns，全扫是 38.6 µs（[change tracking](https://ecs.xgamedev.net/en/docs/features.md#change-tracking)）。
- **只处理自己的实体**：组过滤 `In<K>` 由 `IActor<K>` 带上（`ISystemGroupWith<In<K>>`），加到这个组每个系统的每个 `[ForEach]` 方法上（EasyEcs `guide/systems.mdx` 的 "Group filters"）。
  - 查询缓存只存匹配的表，所以同一个 world 上别的 Actor 类型的表，对这个组的系统不增加任何开销（`guide/systems.mdx` 的 "What it costs"，`why-fast.mdx` 的 "query cache"）。
  - `In<K>` 是框架的 tag（`public struct In<T> : IComponent where T : struct, IActor<T>;`），K 类型 Actor 的每个实体都带着：
    - 根：运行时激活时加上；
    - `root.Spawn(prefab, in a, in b)`：`Registration` 注册 `[Prefab<K>]` 时做一份副本（EasyEcs 的 `new Prefab(basePrefab)`），带上 `In<K>` 和一列 `ChildOf(default)`，静态的 prefab 不被改动。`Spawn` 用 `world.Instantiate(副本, in new ChildOf(root), in a, in b)` 一次写进最终的表，不移动行（EasyEcs 4.7）；
    - Actor 里不用 `CreateEntity`（分析器报错），一律 `root.Spawn`，所以用户从不自己加 `In<K>`。
- **怎么跑**：每个 world 的线程按 Actor 类型依次调用这个类型的框架组 `Ordering<Map>`（`OrderedPhase`）、`world.Update<Map>()`、这个类型的框架组 `Settling<Map>`（`Depart`、`SyncSubscriptions`、去掉 `Restarting` 和 `Resumed`），最后是按 world 的框架组 `Cleanup`（第 6 节）；循环的写法见 [architecture.md 第 2 节](architecture.md#2-runtime-structure)。EasyEcs 只按 `[UpdateInGroup<T>]` 分组，而 C# 不许在泛型的 `OrderedPhase<K>` 上写 `[UpdateInGroup<Ordering<K>>]`（CS8968），所以生成器给每个 Actor 类型写封闭的子类（`[UpdateInGroup<Ordering<Map>>] sealed class OrderedPhase_Map : OrderedPhase<Map>;`，`Settling` 的两个系统同样），`DescribeSchedule()` 看得到每个类型自己的组。它们在 `OnUpdate` 里用按 Actor 类型建好的 `static readonly QueryFilter`（`.With<In<K>>()`）跑 job。一个 world 上同一 Actor 类型的 Actor 共用 tick 边界，所以一次覆盖这个 world 上这个 Actor 类型的所有 Actor。
- **分片访问的行，`DeltaTime` 不是它自己经过的时间**：`[ForEach(Slices = N)]`（例如 NPC 的 AI）每个 tick 只访问 1/N 的行，一行每 N 个 tick 才被访问一次，`Time.DeltaTime` 仍是一个 tick 的长度；要按经过的时间算的，自己记上次访问的 `Time.Elapsed`。
- **系统就是 EasyEcs 的系统**：`[ForEach]` 方法、批量方法（`Span<T>`）、`[Changed<T>]`、`[With<T>]`、`[UpdateAfter<T>]`、`UpdateInterval`（只给有 `[TickRate]` 的组，按 tick 数；按时间跑用 `[EverySeconds]`），规则全按 EasyEcs 的文档。生成代码是 EasyEcs 自己的生成器写的。
- **单线程**。每个 world 设 `WorldOptions.MaxDegreeOfParallelism` = 节点的核数 / 节点的 world 数，最少 1（EasyEcs D36：并行预算按 world 设，不跨 world 共享；`guide/threading.mdx`、`guide/hosting.mdx`）。默认每个核一个 world，所以默认就是 1：`[ForEach(Parallel = true)]` 不起作用，各个 world 已经占满各个核。
  - 只有一种服务器跑的 world 比核少时（`DaServer:Worlds`，例如一个节点只跑一张超大地图），`Parallel = true` 的方法才分块多线程；`TestActor<K>` 的 `AssertParallelMatchesSerial` 就是给这种部署用的（第 12 节）。
  - 并行方法按块切行，一块至少 1,024 行，写追踪组件的按 4,096 行的整数倍切（`guide/systems.mdx` 的 "Parallel methods"），所以实体少的 world 开了也不多花。
- **系统里的发送**：`Entity` 上的操作只能在非并行的系统里用，追加到 `Outbox`；`Parallel = true` 的方法里用，分析器报错，`Commit` 也一样（第 8 节）。
- 系统里 `Time.Elapsed` 是这一趟的时刻：有 `[TickRate]` 的是 tick 时刻 t，没有的是这一趟开始时读的时钟（第 9 节）。
- **只在值变了时写**：`[Changed<T>]` 看的是"写过"，不是"变了"，`ref` 参数和批量方法的 `Span<T>` 一取就算写入。所以每 tick 重算一个追踪组件的系统取 `Mut<T>` 参数，用 `SetIfChanged(v)` 写：值相同就不写、不打戳（EasyEcs 4.7，同 Bevy 的 `set_if_neq`；不是参数的组件用 `entity.SetIfChanged(v)` 或生成的 `entity.SetXIfChanged(…)`，有索引的组件只能这样写）。值的比较用 `IEquatable<T>`（record struct 自带），没有它的非托管类型比字节，带托管字段又没有 `IEquatable<T>` 的抛出 `NotSupportedException`。见第 12 节的 `Steering` 和 `Movement`：站着的 NPC 不打戳，`CellAssign` 和 `Diff` 不访问它。
- **几个组件合成一份输出时**，用 `[ChangedAny<A, B, C>]`（2 到 9 个追踪组件；`[RemovedAny<…>]`、`QueryFilter.Create().ChangedAny<…>(since)`，实例方法，EasyEcs 4.7）：其中任何一个写过，这一行只访问一次，不是每个组件一趟。一个过滤里的每次 `ChangedAny` 调用都并进同一组，所以超过 9 个类型就分几次调用。
  - 真的每 tick 都变的组件不要追踪：每次写都打戳，`[Changed<T>]` 每 tick 都是全部的行，比全扫还多一份开销（EasyEcs 的建议，[change tracking](https://ecs.xgamedev.net/en/docs/features.md#change-tracking)）。第 12 节追踪 `Position` 和 `Velocity`，因为多数实体多数 tick 不动。

### 其他

- 框架的系统自己处理异常，不受 EasyEcs 的 `SystemErrorPolicy` 管理；节点上用户的系统用 `SystemErrorPolicy.Disable`（第 11 节），`TestActor<K>` 用 `Throw`，让测试直接失败（第 12 节）。
- **`Registration`** 是生成的一个实例对象，不是静态表：Actor 类型、prefab 副本、身份、连上就加入、分发表、路由表、系统。节点、`TestCluster`、`TestActor<K>` 都接这个对象。表在构造时一次建好；方法组转换成的静态委托由 C# 缓存，不会分配。
  - `Registration.Create()` 是生成在服务端工程根命名空间里的 C# 14 静态扩展成员，容器类按程序集命名（`Game.Server.GameServerRegistration`），一个服务端工程一个，不按服务器类型分（`PlayerServer`、`MapServer`、`GatewayServer` 共用它）：`using Game.Server;` 之后写 `Registration.Create()`。一个测试工程引用两个服务端工程、两个命名空间都 `using` 时，这个调用有歧义，改写成容器类上的调用 `GameServerRegistration.Create()`。

### 处理方法抛出异常

**不回滚。** EasyEcs 的结构变更当场生效，没有命令缓冲（EasyEcs `decisions.mdx` D2），组件写入也是当场的。所以抛出时：
- 抛出之前做的修改都留着：建的实体、加删的组件和 tag、经 `ref` 参数和方法体写的组件；结构变更之后经 `ref` 参数的写入，EasyEcs 在 job 抛出时也搬到新行（4.5 的 `entity.Run` 保证）；
- 已经追加进 `Outbox` 的照样发出；`Commit` 留住的除外，`Reload` 按第 8 节放出或丢弃它们；
- 这条消息的请求以 `Faulted` 结束（终止的结果，不是 `Transient`：同样的输入会再抛一次）；它的即时链里还没执行的事件作废。

之后怎么办，只看根要不要保存，没有开关：**根要保存（`RootPrefab` 带 `[Persist]`）就重新加载（Reload），否则继续（Continue）**。原因是不回滚：改了一半的项留在内存里，下一个检查点就把它写进记录，宕机和迁移都去不掉它；重新加载丢掉内存，从上一份完整的记录接着来。没有记录的 Actor 只有内存这一份，丢掉它不比留着好。
- **重新加载**：不写记录，不进 `Leaving`，不加 `Deactivating`。
  1. 销毁根的所有子孙；
  2. 根回到注册时 `K.RootPrefab` 的副本：去掉副本以外的组件（tag `Unsaved` 也明确去掉），副本上有的组件改回副本的值；留着的只有键和排队的邮件（`Mail`），带上 `Loading`，就是占位；
  3. 这一批里这个根还没执行的邮件，按原来的 `(时间, 序号)` 移进 `Mail`；
  4. 等这个根正在进行的写记录有结果（超时算没写成；留住的输出按第 8 节放出或丢弃），再从最后一次写成的记录重新激活（第 6 节的表，同样经 `ActivationSlots` 和退避）。写记录的结果按发起它的激活的 epoch 认：旧激活的写成也好、失败也好，都不改新激活（不加回 `Unsaved`，不放出输出）；没等到的那次写落在新激活占 epoch 之前，新激活读到它，落在之后就比较失败。

  上次写记录之后的改动丢失，排队的邮件不丢。Presence 是清理组件，销毁时框架释放它们的路由，客户端 SDK 收到 `PresenceLost` 后重新发加入消息（第 7 节）。抛出的请求当场以 `Faulted` 回答：它的改动随重新加载丢掉，没有写下任何东西。同一个键连续 `Reload`（节点配置的次数）就进入下面激活失败的退避。
- **继续**：实体就是现在的样子，接着处理下一项。

- 分析器给建议：**先校验，最后做结构变更**。方法里结构变更之后还有 `throw` 或可能抛出的调用时，给出提示。第 12 节的 `Resolve` 就是这样写的。
- 一项的即时链超过 1,024 个，或者一个 Actor 待处理的项超过上限（第 9 节），也按这条规则处理（重新加载或继续）。
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
| `Deactivating` | 根上的 tag，声明为 `ITracked`（`[Removed<Deactivating>]` 要求它，EasyEcs 的 `RemovedAttribute<T>`；第 15 节），系统 `[ForEach, With<Deactivating>]` 反应 | 停用前的最后一个 tick（没有 tick 的 Actor 类型：最后一批邮件之后的那一轮系统） | 收尾 |

- **`Activated` 是消息，不是 tag**：初始化必须在排队的邮件之前执行，而系统在有序阶段之后才跑，所以它只能是第一条邮件。顺序固定：`Activated` 在占位上等着的邮件之前，等着的邮件在之后到达的邮件之前（运行时激活时按这个顺序重新编序号）。
- **`Deactivating` 是数据**：它之后不再有邮件，不需要排在邮件里，所以和 `Leaving`、`Detached`（第 7 节）、`Restarting`、`Resumed`（第 10 节）一样是 tag，由系统反应。
- `PresenceLost` 不在这张表里：它不是 Actor 的生命周期，是 `Connection` 上的通知（第 7 节）。

**占位是 ECS 数据，也是以后的根**（EasyEcs `guide/migration-and-unload.mdx` 的 "A record of what persists"：先有实体，记录后到，装到它上面）：一个键在"还没有激活"或"正在写记录"时，它的根实体是一个占位。占位按注册时 `K.RootPrefab` 的副本建，这份副本不带 `In<K>`；占位上还有 Actor 类型组件（键）、一个 tag（`Loading` 或 `Saving`）和等着的邮件（框架内部的 `DynamicBuffer<Mail>`）。没有 `In<K>`，所以系统和处理方法都匹配不到它。占位上等着的邮件有自己的上限 `[ActorOptions(MaxWaitingMail = n)]`（默认 65,536；`MaxPending` 只限待处理的延后事件和请求，第 9 节），全节点再有一个字节的上限 `DaServer:WaitingMailBytes`（256 MiB，按 world 分份，同激活名额）：存储慢、很多键同时在 `Loading` 时，等着的邮件加起来也有界。超过任何一个时请求回 `Busy`，单向消息丢弃并计数。

| 状态 | 根实体 | 怎么进入 | 发生什么 |
|---|---|---|---|
| 交接中 | 占位，带 `Arriving` | 放置刚把这个键所在的槽移到本节点（槽表的移动、节点加入或优雅离开），来源还没报告这个槽交接完，邮件到了而本节点上还没有它 | 不占 epoch：邮件停在占位上，本节点问来源（`HandoffQuery`）。来源回迁移的字节（它这时迁移这个 Actor：先邮件，后子树）就按迁移到达；回"不在这里"就照常占 epoch 加载；回"推迟"（例如存储不可用，来源写不了根）就把邮件转发回来源，退避后再问。来源转发来的邮件排在本节点停住的邮件之前，所以同一个发送方的顺序跨过迁移也不变。所以占 epoch 从不和一个还活着的来源竞争 |
| 加载中 | 占位，带 `Loading` | 第一条消息到达本节点，本节点按自己的视图是这个键的主人、键也不在交接中（不是主人就转发，不激活）；或者根要保存的 Actor 的处理方法抛出、重新加载（第 5 节） | 邮件留在占位上；拿到 `ActivationSlots` 的名额后，一条语句占 epoch 并读记录（第 10 节） |
| 激活 | 同一个实体，成为根 | epoch 占到了，记录读到了或者没有记录 | 有记录时 `LoadEntitiesInto(record, [占位, n−1 个空实体], markWritten: false)`（第 10 节）；然后加上 `In<K>`，去掉 `Loading`。键和邮件本来就在它上面，不搬动；邮箱里第一条是 `Activated`，后面才是等着的邮件 |
| 运行 | 完整的根实体 | — | 按 `(时间, 序号)` 执行消息和事件，再跑系统和收尾（第 1 节） |
| 停用 | 完整的根实体 → 占位，带 `Saving` | 空闲超过 `IdleTimeout`（不写用节点配置 `DaServer:IdleTimeout`；空闲检查是 `[ForEach(Slices = N)]` 的框架系统，每次看每张表的 1/N 行）：没有待执行的延后事件和等待中的请求，也没有不带 `Detached` 的 Presence；或者调用 `root.Deactivate()` | 依次：<br>1. 对每个 Presence `Leave()`；<br>2. 根加上 `Deactivating`，之后的邮件不再执行，留给占位（这一轮的系统 `Commit` 了：照常，这次停用的写就是它要等的写，第 8 节）；<br>3. 这一轮的系统和收尾跑完（用户对 `Leaving` 和 `Deactivating` 的反应、`Depart`）后，`SaveEntitiesWith` 写进缓冲（抛出时见第 10 节的"只写改过的"），销毁整棵子树（待执行的延后事件随之消失），换成带 `Saving` 的占位，邮件都留在占位上。所以 `[ForEach, With<Deactivating>]` 的系统里 `Schedule(ev)` 建的 `Due` 子实体也随子树消失，事件不会执行；<br>4. 异步写记录，按 epoch 比较并交换；<br>5. 写成功后：占位上有邮件，就变成 `Loading`，读到的是刚写好的记录；没有就销毁占位 |
| 删除中 | 完整的根实体，带 `Deleting` 和框架的唤醒（随写入的记录保存） | `root.Delete()` 返回 `Ok` | 根和整棵子树都不跑系统、不投提醒，新请求回 `Busy`，单向消息丢弃，只有 `[Once]` 的重复回答 `Unacked` 存着的回复，不能再问或发；`Unacked` 清空、没有问的行、过了宽限之后由 `daserver_delete_actor` 删掉记录、标好行，再销毁，不写；不迁出，冻结和宕机之后新的主人接着删（第 5 节） |
| 隔离 | 销毁 | 写记录时 epoch 比较失败；收到 `Fence(键, epoch)` 控制邮件（第 10 节）；或者本节点被宣布 `Dead` | 不写记录、不进 `Leaving`、不加 `Deactivating`，直接销毁整棵子树；Presence 的路由由清理组件释放（第 7 节）；排队的请求以 `NoOwner` 失败 |

- 写记录失败就重试，绝不丢弃；epoch 比较失败不是失败，是隔离。
- **框架在根上数着待处理的项**（内部的 `Pending`，就是 `MaxPending` 限的那个个数，第 9 节）：建延后事件、发出请求时加一，执行、取消、收到回信时减一。空闲的判断只看这个数为 0 的根，所以 Actor 不会在等回信或等定时事件时被停用。节点宕机时延后事件和请求的等待照样丢失（停用和记录本来就不保存它们）；要扛住宕机的，是 `AskUntilAnswered` 的请求和它的 `WakeAt`，`DueScan` 到时唤醒 Actor（第 8 节）。
- **存储慢或不可用**：
  - 框架限制本节点同时在占 epoch 的激活数（内部的 `ActivationSlots`）；拿不到名额的占位按带随机抖动的退避再试，存储恢复时不会一齐涌上去；
  - 存储的延迟或错误超过阈值时，节点进入存储降级（内部的 `StoreDegraded`）：这期间只停掉空闲停用（停用要写记录）；`root.Deactivate()` 照常，但带 `Saving` 的占位数有上限，超过的停用推迟到有名额；
  - 占位等过了期限（节点配置）还没激活，发来的请求立刻回 `Busy`，不再排队到超时；
  - 就绪检查不看存储：存储出问题时节点照样就绪，已经激活的 Actor 照常运行；`Commit` 留住的输出等到写成才发出（第 8 节）。
- 抛出后的重新加载不走这张表的停用，见第 5 节。
- **框架销毁整棵子树之前，先从子孙上移除不该触发清理的清理组件。** 销毁带清理组件（`ICleanupComponent`）的实体时，EasyEcs 不释放它：它失去其他所有组件（包括 `In<K>`），离开所有索引，带上 `Retired`，等清理系统移除最后一个清理组件（`guide/cleanup-and-disable.mdx`）。整棵子树用 `world.Descendants(root).AddTo(ids, includeRoot: true)` 收集，一次 `world.DestroyEntities(ids)` 销毁（4.7：只过一次结构闸门、只处理一次级联，失效和重复的 id 跳过）。框架的两个清理组件是 `Presence`（释放路由，第 7 节）和要保存的实体上的 `Persisted`（给根标 `Unsaved`，第 10 节）：

  | 销毁的原因 | 先移除 `Persisted` | 先移除 `Presence` |
  |---|---|---|
  | 停用（写完 `SaveEntitiesWith` 之后） | 是：记录已经写进缓冲，根也要销毁 | 不用：这时已经没有 Presence（第 1 步 `Leave()`，`Depart` 已经移除） |
  | 迁移（写完迁移字节之后） | 是 | 是：Presence 随字节到了新节点，路由不释放 |
  | 抛出后重新加载、隔离、激活失败 | 是：不写记录 | 不移除：路由要释放，客户端 SDK 据此重新加入 |

  所以只有用户自己的销毁、`Reload`、隔离和激活失败留下待清理的实体，没有白做的清理。
- **对 `Retired` 反应的框架系统不带 `In<K>` 过滤**：退役的实体已经没有 `In<K>`，带着它就永远匹配不到。所以 `ReleaseRoutes`（`[With<Retired>, With<Presence>]`）和 `Persisted` 的清理系统在框架组 `Cleanup` 里，按 world 跑，不按 Actor 类型；它们需要的信息（连接戳、根的 `EntityId`）都在清理组件自己里面。
- **激活的条件：**
  - 服务器之间的消息默认都能激活（Orleans 的虚拟 actor）；
  - **客户端带 `[To]` 的消息默认只激活存储里已经有记录的键**：键是客户端说的，不能让它随便建行（第 4 节）；没有记录时回 `NoActor`；
  - Actor 类型可以写 `[ActivatedBy<EnterMap>]`（可以写多个）指定哪些消息能建出新的键：存储里还没有这个键的记录时，只有这些消息能激活，其他消息（服务器之间的也一样）返回 `NoActor`；有记录的键什么消息都能激活；
  - 写了 `CanCreate` 的，能建键的消息先过它：返回 `false` 时只用不插行的占法激活，键有记录照常激活，没有才是 `NoActor`（第 4 节）；
  - **`[JoinOnConnect]` 的加入总能激活、总能建键**（新玩家第一次登录就建出 Player 的行）：键由身份按 `[JoinOnConnect<TClaims>]` 算出，不是客户端编的，所以 `[ActivatedBy]` 和 `CanCreate` 都不管它，写在同一个类型上也一样；
  - 框架的消息（`Replied`、`JoinConfirmed`、路由的释放、`Fence` 等）不会激活 Actor，找不到就丢弃；`DueScan` 发的 `Wake` 是例外，它就是来激活的（第 8 节）；
  - 只有按本节点的视图是这个键的主人时才激活；不是主人就转发给主人（跳数有上限），主人的激活再由存储里的放置版本把关（第 10 节）；
  - 本节点上还有这个键的墓碑（迁移走了，第 10 节）时不激活，邮件都转发到新节点；
  - `Connection` 只在客户端连接时激活。

---

## 7. 客户端与 Presence

本节是用户看得到的部分。连接戳、`TakeOver`、路由的登记和核对、`Retire` 和 `HandedOff` 栅栏、按负载的再平衡和接纳令牌桶是框架内部的协议，在 [architecture.md 第 3 节](architecture.md#3-messaging)。

### `Connection` 实体和令牌

- **令牌**：客户端连接时出示一个令牌：DaServer 的 `SignedToken`（集群的签名密钥签名，带签发和过期时刻），内容是 `(ClientId, 签发序号, 身份)`。
  - **签发**：`cluster.IssueClientToken(in claims, ClientId.New(), issueSeq, lifetime, into)`：`into` 是调用方给的 `Span<byte>`（至少 `SignedToken.MaxLength` 字节），返回写入的字节数，不分配。Actor 里用 `root.IssueClientToken(in claims, lifetime, into)`（第 5 节的操作表），签发序号是根上框架保存的计数器，每次加一。
  - **签发序号先落定，令牌才离开**：否则签发方在令牌发出之后、记录写成之前宕机，从旧记录恢复后同一个序号会再签给另一次登录。所以 `root.IssueClientToken` 隐含 `root.Commit()`：令牌就是回复，在签发序号写成之后才离开（第 8 节）；在根不保存的 Actor 类型上调用，分析器报错。mmo 样例的登录服务就这样问账号 Actor。
  - **刷新**：`cluster.RefreshClientToken(token, lifetime, into)` 换新的过期时刻，保留 `ClientId` 和签发序号。
  - **`ClientId`**：`readonly record struct ClientId(ulong Value)`，每次登录随机取一个，重连和刷新都不变；它是客户端调用 id 的前缀（第 8 节），也是 `cluster.Disconnect(clientId)` 的参数。
- **会话令牌**：令牌说明是谁登录；会话令牌说明 SDK 的这一次运行还在不在。SDK 第一次连上时网关签发 `Session(ClientId, 会话随机数, 有效至 T, 剩余 R, 调用的高水位)`（同一把签名密钥），每次回复和心跳把 `T` 推到"现在加 `DaServer:Clients:ResumeWindow`"（只在心跳、或 `T` 后移超过四分之一时新签一个）；重连时 SDK 交回它和有效的客户端令牌，任何网关过了 `T` 都不认，`T` 也从不超过客户端令牌的到期（第 8 节的 `[Once]` 靠它：结束了的会话不能从另一个网关回来重放旧的调用，偷到的会话令牌也不能一直续）。SDK 按 `R` 用单调时钟计时，不看设备时钟。应用重启是新的会话。
- **`ConnectionId`**：`readonly record struct ConnectionId(ulong Hi, ulong Lo)`，16 个随机字节，网关接受连接时生成，每个连接一个；它是 `Connection` 的键。键不用 `ClientId`：同一次登录在一个节点上可能同时有两个连接（`Retire` 后又连到同一个网关、旧连接半开时重连），而 Actor 的键是唯一索引，第二个连接的键会抛出。
- **激活**：客户端连上一个承载 `Connection` 的节点后，框架激活 `Connection(ConnectionId)`，和别的 Actor 一样按键的哈希落在一个 world 里；再加上 `ClientOf(ClientId)`（令牌里的 `ClientId`，非唯一的值索引，EasyEcs 的 `IIndexed<ClientId>`）。发给连接的一切（推送、`TakeOver`、`Lost`、`HandedOff`）都按 `ConnectionId` 寻址，Presence 记着它。只有必须按 `ClientId` 找连接的地方用 `ClientOf`：`cluster.Disconnect(clientId)` 发给每个承载 `Connection` 的节点，节点问它的每个 world（少见的管理操作）。同一次登录的几个连接之间不在网关上比较，在目标上按连接戳比较，同一个节点和不同节点一样（[architecture.md 第 3 节](architecture.md#connection-stamps)）。
- **身份**：令牌里的声明解码成用户写的 `IClaims` 类型，例如 `Account`，作为组件加到 `Connection` 上，并随它转发的每条客户端消息带上（第 5 节的身份参数）。框架不认识任何业务字段。
- **位置固定**：`Connection` 固定在连接所在的节点，不参与放置，也不迁移；连接断开，它就停用。
- **扩展**：写 `[Handles<Connection>]` 类；要给它加组件（例如令牌桶），在它的 `[On<Activated>]` 里当场 `Set` 上（`Connection` 是框架的类型，没有用户写的 `RootPrefab`）。
- **找不到它**：别的 Actor 不能用 `ClientId` 找到 `Connection`（`ClientOf` 的索引只在本节点的各个 world 里）。推送只经过 Presence（它记着节点和 `ConnectionId`），所以不需要位置目录。

### Presence

客户端在一个 Actor 上的存在，叫 **Presence**：目标 Actor 的根的一个直接子实体，例如地图上的 Avatar。框架给它加 `Presence`（清理组件，按 `PresenceId` 建值索引，另按 `(ActorId, ClientId)` 建唯一索引，`root.JoinClient` 和 `root.TryGetClient` 用它）；网关上有一条通往它的路由；客户端 SDK 只知道一个**槽号**（这个连接上每个加入过的 Actor 一个小整数，按 (Actor 类型, 键) 分配，不含 Actor 类型、节点和地址）。

- **每个 Actor 最多一个 Presence；每个 Actor 类型最多 `PresencesPerClient` 个**（`[ActorOptions(PresencesPerClient = n)]`，默认 1）：默认一个客户端同一时刻最多在一张地图上、一个 Player 上；聊天频道、队伍加公会、几个匹配队列这类写大一点（例如 16），同时在几个同类 Actor 上。超过时加入新的，最早加入的那个被离开（默认 1 时就是"换地图"）。不带 `[To]` 的客户端消息要知道发给哪一个，所以 `PresencesPerClient` 大于 1 的 Actor 类型只处理带 `[To]` 的客户端消息（分析器检查），处理方法用 `root.TryGetClient(out var me)` 认出成员。
- **`PresenceId`** 是 64 位随机数，只在服务端；接管时换新的。
- Presence 的状态都是数据：

  | 状态 | 是什么 | 谁反应 |
  |---|---|---|
  | `Joined(bool MissedPushes)` | 网关确认了路由；推送和 AOI 只认带它的 Presence。`MissedPushes` 为真时客户端可能漏过推送（第一次加入、断线后重连、接管、推送在链路或网关上被丢掉或合并，第 7 节的推送），只有在客户端因网关升级换连接、一个推送也没漏时为假 | 快照系统 `[ForEach, Changed<Joined>]`，`MissedPushes` 为假时跳过（第 12 节的 `SendFullState`） |
  | `Leaving` | 正在离开 | 用户的系统 `[ForEach, With<Leaving>]`；之后框架的 `Depart` 销毁它 |
  | `Detached` | 连接断了，宽限期内保留（下面） | 一般不用反应 |

### 连上就加入：`[JoinOnConnect<TClaims>]`

```csharp
public readonly partial record struct Account(long Id) : IClaims;                       // the token's claims: the user's own type
[JoinOnConnect<Account>]                                                                // joined at connect, keyed by the claims
public readonly partial record struct Player(long Id) : IActor<Player>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(40).SetWallet(default).SetBuffer<Slot>();
}
```

- **身份是用户自己的类型**，实现 `IClaims`（`public interface IClaims : IComponent`），不绑在 Player 上；非托管、不超过 16 字节，一个工程最多一个（分析器检查）。
- **想在连上时就加入的 Actor 类型，自己声明** `[JoinOnConnect<Account>]`（`where T : struct, IClaims`）。这个 Actor 类型的键和身份形状相同（同样类型、同样顺序的字段），生成器按字段一一对应，`Account(42)` → `Player(42)`；形状不同，分析器报错。可以有多个 Actor 类型挂在同一个身份上。
- **谁胜出是声明上的参数**：`[JoinOnConnect<Account>(Takeover = Takeover.NewestWins)]`，默认 `NewestWins`：更新的连接接管，旧连接被关闭；`Reject`：另一次登录（`ClientId` 不同）来加入、而已有的 Presence 带着 `Joined` 时，已有的连接留下，新的收到 `AlreadyConnected` 后断开（半开的旧连接要等它超时）。同一个 `ClientId` 总是按连接序号接管（重连、`Retire` 换网关都是同一次登录）；`Detached` 的 Presence（连接已经断了）从不拒绝。
- 框架做完整件事，用户不写代码：建或接管 Presence，失败时按 `RetryAfter` 或带抖动的退避重试，重试的预算按时间算（`DaServer:Clients:JoinRetryFor`，60 秒，长过一次重新放置：死节点的槽要等探测、`IsolationTimeout` 和 `DeathMargin`，约 30 秒），过了才以 `Unavailable(retryAfter)` 结束会话；Presence 丢失（Player 重启）时框架重新加入。
- 加入好之前，客户端消息在 `Connection` 上排队，有上限（节点配置），超过的回 `Busy`；加入好后框架推一条 `Ready` 给客户端。
- **客户端消息的主体是根**：不带 `[To]`、由 `[JoinOnConnect]` 的 Actor 类型（Player）处理的客户端消息，主体是这个 Actor 的根，不是 Presence：每个玩家一份的状态（背包、钱包）在根上，客户端改自己的状态是最常见的事（`[On<UseItem>] static bool Use(in UseItem m, ref DynamicBuffer<Slot> bag)`）。要 Presence 时用 `root.TryGetClient(out var me)`。参数里的组件根的 prefab 上没有，`Registration` 构造时就报错（第 5 节）。

### 客户端消息的转发

1. 客户端发来一条 `IFromClient` 消息，先到它的 `Connection` 实体。
2. 先过过滤：`[Handles<Connection>]` 类里的 `[On<Incoming>] static Verdict Allow(in Incoming m, ref RateLimit r)`。`Incoming` 是帧头的视图（`TypeId`、长度、身份），网关从不解码负载。它逐条执行，返回放行或者拒绝的原因；没写就全部放行。
3. 按生成的路由表，得到处理 Actor 类型和目标：带 `[To]` 的是这个 Actor 类型、键是这个参数的 Actor，没有就激活；不带 `[To]` 的是这个客户端在该 Actor 类型上的 Presence，没有就拒绝，`NotJoined`。
4. 直接转发给目标，信封带着客户端上下文。Presence 带着 `Leaving`，或者已经被接管，回 `NotJoined`。
5. 被拒绝时，客户端收到原因：`NotJoined`、`Busy`、`NoActor`、`Filtered`、`Superseded` 等。
6. **每个连接同时在途的请求有上限**：`DaServer:Clients:MaxInFlight`（64）个还没回答的请求，再来的网关当场回 `Busy`，不转发。一个客户端不能往一个热点 Actor 里无限地排请求，发往网关的回答所留的位置也因此有界（第 8 节）。

### 加入：`e.Join()`

例子是第 12 节的 `Maps.Enter`：按 `AvatarIndex` 找到或用 `root.Spawn` 建好 Avatar，然后 `return avatar.Join() == Status.Ok;`。成员只要按客户端认出的（聊天室、队伍、匹配队列），一行就够：`return root.JoinClient(out _, Prefabs.Member) == Status.Ok;`（第 5 节的操作表），不用自己写键组件。`JoinClient` 按 `ClientId` 认人，`ClientId` 是一次登录：重新登录是新的 `ClientId`，会建出第二个成员，前一个在宽限期里 `Detached`。要按账号认人的成员（一个账号在一个队列里只能排一次、断线重登还是同一张票），像 `Maps.Enter` 一样用身份做键：`Ticket(Queue Queue, long Account)` 加 `IUniqueIndexed`，`TryGet` 找到就 `Join()` 接管，找不到再 `Spawn`。

**本地当场生效**（EasyEcs 的结构变更当场生效，`decisions.mdx` D2），结果是一个 `Status` 值，不是异常：
1. `e` 必须是根的直接子实体，带 `In<K>`（`root.Spawn` 建的就是），否则抛出（这是代码错误）；
2. 当前项没有客户端上下文（系统里、`Activated` 里，或者请求是从没有客户端的地方发出的，第 4 节）：返回 `NoClient`，什么也不改；
3. `e` 已经是 Presence 时：同一个连接返回 `Ok`（幂等）；已有的连接更新，返回 `Superseded`，什么也不改；当前的连接更新，就接管：去掉 `Joined`、`Leaving` 和 `Detached`，换新的 `PresenceId`，旧连接的路由被释放，旧连接被关闭；
4. 加上 `Presence` 和确认期限，把路由追加到 `Outbox`，返回 `Ok`。路由一定排在这条请求的回复前面（第 5 节的 `Outbox`）。

- 分析器要求用到 `Join()` 的返回值。
- 网关写好路由后确认，Presence 带上 `Joined`；有 `[TickRate]` 的 Actor 类型先给客户端推一条框架通知，带帧长、当前 tick 号和服务器时间（第 9 节），之后推送才开始。确认期限到了还没确认（客户端已经断开），框架对它 `Leave()`。
- 客户端的请求已经超时的，Presence 照样建好，只是回复被丢弃。
- **换地图**：客户端发 `EnterMap(8)`。地图 8 加入后（`Map` 的 `PresencesPerClient` 是默认的 1），网关删掉地图 7 的路由，地图 7 对那个 Presence `Leave()`。两张地图是两个 Actor，它们之间没有先后：短时间内客户端可能同时出现在两张图上。需要严格先后的业务，用请求串起来。

**`Superseded` 的意思只有一个：有更新的连接或者更新的登录。** 它出现在三处：`Join()` 返回它（已有的连接更新）；客户端消息被拒绝时的原因（这个连接已被接管）；客户端 SDK 收到的终止事件（同一账号在别处登录了更新的令牌：SDK 报给应用，不自动重连）。同一次登录就是同一个 `ClientId`：它换了连接（重连、网关升级）不是 `Superseded`，旧连接安静地关闭；同一次登录的几个连接之间，按 SDK 每次连接加一的序号排先后，不看连到了哪个网关（[architecture.md 第 3 节](architecture.md#connection-stamps)）。只有 `ClientId` 和对方不同、签发序号更小的一方收到 `Superseded`；签发序号相等（同一个序号签了两次）时新的算更新的登录，旧连接以 `Superseded` 关闭。

### 推送

`e.Push(msg)`、`ClientGroup`（整个 Actor 是 `root.Everyone`；第 5 节的操作表、第 9 节）。只推给带 `Joined` 的 Presence。

- **推送丢了，就重新同步，不悄悄丢**：推送是单向的，可能在三处被丢掉：发往网关的链路窗口满了、网关的派发队列满了、一个客户端读得太慢。每一处丢掉时都给受影响的 Presence 记上 `Joined.MissedPushes = true`（写 `Joined`，所以快照系统 `[Changed<Joined>]` 照常重推一份，同断线重连）：链路满时由发送的 world 按 Presence 记着的网关和订阅找出它们；网关上丢掉时，网关给目标发一条 Presence 通知 `Missed(PresenceId)`（同 `Lost`，在这一对的顺序里、留着位置，不会被拒）。
  - **重新同步有预算**：每个 Actor 每个 tick 最多把 `DaServer:ResyncsPerTick`（64）个 Presence 记成 `MissedPushes`，其余的排着下一个 tick 再记；所以一个网关宕机、十万个客户端同时重连时，地图不会在一个 tick 里给几千个人各推一份完整状态（快照是最大的消息，一起推只会再把发送队列挤满）。
- **读得太慢的客户端先合并，久了才断**：一个连接还没写出去的字节超过它的上限 `MaxPendingSendBytes`（至少 `MaxMessageSize`，否则一份大快照永远发不出去，启动时检查）时，网关不再给它排新的帧：同一个组的帧只留最新的一帧，直接推送照常排在后面，被合并掉的组帧让这个 Presence 记上 `MissedPushes`（上面）。积压一直不下去，超过 `DaServer:Clients:SlowClientGrace`（10 秒）才断开，按断线处理（`Detached`）。所以手机卡了一秒不会被断开、重连、再拉一份全量快照、又被断开。

### 离开：`Leaving` 是数据

- **`e.Leave()`** 当场：加上 `Leaving`，去掉 `Joined`，把路由的释放追加到 `Outbox`；客户端 SDK 收到 `PresenceClosed(槽号)`，不重新加入这个槽。
- **用户的系统反应**：`[ForEach, With<Leaving>]`，例如广播"消失"（第 12 节的 `Vanish`）。可能已经不在的组件用 `TryGetX`。
- **收尾组的 `Depart`** 排在用户的系统之后：去掉 `Presence`，销毁实体。
- 带 `Leaving` 的 Presence 不再收客户端消息（`NotJoined`）。同一项或同一帧里再 `Join()` 它，就去掉 `Leaving`，算作接管。
- **外部触发的离开**（被 `Disconnect` 踢掉、加入同一 Actor 类型的另一个 Actor 超过了 `PresencesPerClient`）由框架作为邮件送到目标，在目标的处理过程中 `Leave()`。
- **同一个键不会有两个 Presence 实体**：同一个 Player 再进同一张图时，`AvatarIndex` 找到已有的 Avatar，`Join()` 接管它。

### 断线：宽限期内的 `Detached`

- 连接断开时（客户端断线、被接管后关闭、积压超过 `SlowClientGrace` 被断开），或者网关节点离开集群时（成员表里成了 `ShuttingDown` 或 `Dead`），目标不立刻 `Leave()` 这些 Presence：去掉 `Joined`，加上 tag `Detached` 和期限（现在 + 宽限期）。
- 宽限期默认 30 秒，在 Actor 类型上写 `[ActorOptions(DetachGrace = 秒)]` 改；0 就是立刻 `Leave()`。
- 带 `Detached` 的 Presence：不收推送，不让 Actor 保持活跃（第 6 节的空闲判断不算它），实体和它的组件都还在。
- 这期间客户端的消息：断开时还没送达的丢失；重连之后、这个槽重新加入之前，不带 `[To]` 的请求回 `NotJoined`，单向消息丢弃并计数（第 11 节的表）。
- 客户端重连，新连接接管它：去掉 `Detached`，照常确认，`Joined.MissedPushes` 为真，所以快照系统重推一份。`[JoinOnConnect]` 的 Presence 也一样。
- 期限到了还没被接管，框架对它 `Leave()`。
- `cluster.Disconnect(clientId)`、`connection.Disconnect()` 是有意的断开：Presence 立刻 `Leave()`，没有宽限。
- 一个地区的网络抖一下、或者一个网关宕机，客户端重连回来时 Presence 还在，Actor 不用重建它们。
- **房间类的 Actor 要想好宽限期**：匹配队列里断线的票在宽限期内还会被配上，帧同步房间会等一个不在的人。这种 Actor 写 `[ActorOptions(DetachGrace = 0)]`（断线就离开），或者写一个 `[ForEach, With<Detached>]` 的系统决定怎么办（例如让票在断线期间不参与匹配、留着座位）。arena 样例两种都用，各用在一个 Actor 上（W7d）：对局 `Match` 写 `DetachGrace = 0`，掉线就离开；匹配队列 `Queue` 用 `[ForEach, With<Detached>]` 的系统把断线的票标成不可匹配，宽限期内重连接回原来的票，票自己的 `Expire` 到期才移走它。

### Presence 丢失：`PresenceLost` 和 SDK 重新加入

目标重启了（从记录恢复、抛出后重新加载、隔离），或者 Presence 没经过 `Leave()` 就被销毁（父实体级联销毁、用户 `Destroy()`）：`Presence` 是清理组件，框架释放它的路由，网关投递 `PresenceLost`。迁移不是丢失：Presence 随 Actor 一起到新节点。
1. 投递：给客户端 SDK `PresenceLost(槽号)`；给 `[Handles<Connection>]` 的 `[On<PresenceLost>]`，写了才投递。
2. **SDK 重新发这个槽的加入消息**：SDK 为每个槽记着最后一条加入它的消息，收到 `PresenceLost(槽号)` 时按带随机抖动的退避重发它（收到 `Busy` 时按它的 `RetryAfter`），带着当前的身份；目标因此重新激活，加入消息的处理方法再调用一次 `Join()`。
   - 同一个槽后来加入的消息替换先前的；`PresenceClosed(槽号)` 清掉它；
   - 重连之后，`Ready` 一到，SDK 重发所有槽的加入消息，新连接接管目标上旧连接留下的 Presence；
   - 重发的回复由 SDK 自己收下，应用只看到 SDK 的事件。
- **限制**：
  - 只重发那一条加入消息，不重发之后的消息；之后的状态以目标的记录为准；
  - 加入消息的处理方法先按唯一索引找、没有再 `root.Spawn` 建 Presence 的实体，再来一次结果一样，不用另写幂等的逻辑（第 12 节的 `Enter`）；
  - 连上就加入（`[JoinOnConnect]`）的 Presence 不靠 SDK，由框架重新加入。

### 节点热重启、网关升级：客户端不断线

- **承载 Actor 的节点热重启**（第 10 节）：发给它的消息在网关和对端的重发窗口里等着，热备接过去之后送达。想让客户端看到提示（例如"地图重启中"）的，在网关上写一个 `[EverySeconds(1)]` 的系统（每秒一次，第 5 节）：节点单例 `RestartingNodes` 列出正在热重启的节点和各自的预计停顿；`nodes.TryGetPause(connection, out var pause)` 看这个连接的路由有没有指向其中一个，有就 `connection.Push(…)`。
- **网关升级、缩容、扩容后的再平衡**：框架让客户端先连上新网关、再关旧连接，每个 Actor 的推送保持顺序，`Joined.MissedPushes` 为假，快照系统不重推。用户不写代码，客户端不多知道任何拓扑；做法见 [architecture.md 第 3 节](architecture.md#retire-and-rebalancing)，各平台的部署见 [deployment.md 第 2 节](deployment.md#stateless-groups-and-gateways-deployment)。

---

## 8. 等待别的 Actor 的结果，贵重的改动

例子是第 12 节的 `Pickups` 和 `Stashes`：客户端要捡起地上的掉落，地图问 `Loot`；`Loot` 把"许给了谁"写进掉落的记录，同时 `AskUntilAnswered` Player 发放物品（`[Once] GrantItem`）；Player 执行一次，改动写成之后才回答。

- **`e.Ask(to, in message[, state][, deadline: t])`** 创建一个等待实体，它是 `e` 的子实体，带：
  - `state`：一个普通组件，存在等待实体上，回信时作为 `a.State` 交回；不写时回信是 `Replied<TReq>`，没有 `State`；
  - 调用 id：全集群唯一，`a.Call`；运行时按 world 维护"调用 id → 等待实体"的哈希索引，回信到达时一次查到；
  - 期限：`Due`（第 9 节），在线上是剩余时间。`deadline` 是这次问的超时，不写用节点配置 `DaServer:AskTimeout`，最多 `DaServer:MaxAskTimeout`（更长的按它截断；客户端请求的期限网关也这样截断，第 8 节的去重靠它）。在带请求上下文的项里（处理一个请求，或者它链上的回信），取 min(这个超时, 这个请求剩下的时间)：嵌套的问不会比外层的请求活得久。这一项调用了 `Commit`（下面）时，问在写成之后才离开，期限从离开时算起；
  - 客户端上下文：处理客户端消息、`PresenceLost` 时自动带上（第 4 节）。
  - 生成器为有状态和无状态各生成一个重载，不靠默认参数推断泛型。
- **回信按 `(Actor 类型, 键, 调用 id)` 寻址**，发问方迁移后，回信会跟过去。
- **回信是发给发问方的 `Replied<TRequest>` 或 `Replied<TRequest, TState>` 邮件**，按（请求类型, 状态类型）分发；主体是发问的实体 `e`。
  - `Replied` 是只读结构，字段都是 `readonly`：`Status`、`Call`、`RetryAfter`（`Busy` 时建议的等待，否则为 0），有状态时还有 `State`；
  - **回复的值只能经 `a.TryGetValue(out var v)` 取**：成功时返回 `true` 和值；`a.Status` 只说明失败的原因（`TimedOut`、`Busy`、`NoOwner`…），成功时是 `Ok`。没有"失败时也能读到的默认值"；
  - 值的类型来自请求：生成器为每个请求类型写一个 C# 14 的扩展块（`extension<TState>(Replied<GrantItem, TState> a)`，里面是 `bool TryGetValue(out bool value)`）；回复的字节留在这封邮件的帧里（收到它的池化接收缓冲），`TryGetValue` 当场反序列化，不另存一份；
  - **等待实体在它的 `Replied` 处理方法返回后立即销毁**，帧随之归还。
  - 发问的实体先被销毁了（例如 Avatar 离开），等待实体随它一起销毁，回信到达时丢弃。所以要收尾的，从根或者不会离开的子实体发问（在 Presence 上 `Ask`，分析器给提示）。
- **回信和别的消息一样按顺序到达**：接收方的回答排在它之前发给发问方的消息后面、之后发的前面（第 1 节的保证 2）。
- **超时**：`Due` 到了，框架投递一个 `Status = TimedOut` 的 `Replied`，和别的项一样按 `(时间, 序号)` 执行。
- **超时不等于没执行**：请求可能已经执行，只是回复没赶上。结果未知时由发问方决定：
  - `a.Status.Transient` 说明值得重试；要重试，延后 `a.RetryAfter`，再问一次。普通的问不去重：再问是一次新的执行，所以只对读、或者本身幂等的请求这样做；
  - 不能执行两次的请求标 `[Once]`，用 `AskUntilAnswered` 发出：框架用同一个序号一直问到有回答，结果未知不交给用户（下面）。
- **等待实体不进记录**：它随 Actor 迁移和热重启，但停用和宕机时随之消失（同延后的事件）。要一直问到有结果的用 `AskUntilAnswered`（下面）。

### 贵重的改动：`root.Commit()`

一般的改动随检查点成批写下（第 10 节的"默认"）：宕机最多丢一个检查点间隔（存储跟不上时是积压排空的时间，第 10 节），下线（停用）、停机、迁移和热重启都先写或者带过去。这就是游戏服务器的惯例：TrinityCore 每 90 秒、AzerothCore 每 15 分钟给所有玩家存一次，下线时再存一次。物品、货币、订单这类贵重的改动要在回答之前落定：处理方法改完调用 `root.Commit()`（TrinityCore 的交易、邮件也是当场写进一个事务；Orleans 的 `WriteStateAsync`、Akka Persistence 的 `persist(...).thenReply` 都是"写成再回复"）。

- **做什么**：根上置 `WriteNow`（框架组件 `RootState` 的字段，不是 tag，所以不改结构），这个根进这个 tick 末的批量写（没有 tick 的 Actor 类型：这批邮件之后），不等检查点间隔。同 world 这一刻要写的根合成一条语句（每 512 行、最多 `DaServer:Store:StatementBytes` 一条），所以提交多的时候一条语句落定更多的根：成组提交，吞吐不降。
- **这一项发出的东西等写成才离开**：调用 `Commit` 的那一项（一个处理方法，或者系统里的一行）发出的一切（回复、`Send`、`Ask`、`Push`）留在根上（池化），带着根的写代：包含这一项改动的那次写记录。那次写（或者之后的一次）写成，它们按追加的顺序离开；比较失败（这个激活已被隔离，第 6 节），丢弃。写出错时，临时的错误（超时、连接断开、序列化冲突、死锁）用同样的字节重试（同一条语句连续超时三次就对半拆开，免得一条永远写不完的大语句一直占着 world 的写名额；拆到一行还超时的，这一行移出写名额，单独按退避重试（1 秒起，翻倍到 1 分钟），不占别人的名额；之后连续十条语句不超时，语句的大小再翻倍回到上限）；存储每次都会拒绝的错误（例如一行太大），把这条语句对半拆开再写，直到找出那一行，按写不进去处理（第 10 节），其余的照常写成。
  - 推给 `ClientGroup` 的也留住：写成时才追加进这个组的帧，所以一次写不挡住整个组的广播；它和别的项推给同一个组的消息按放出的先后排（第 1 节保证 2 的例外）。
  - 放出时已经过了期限的回复不发出、计数（发问方已经按超时处理了）；留住的 `[Latest]` 消息照样被同一个发送方的新的一条替换（第 4 节），一个每秒 60 次的输入流不会在一次写后面越排越长。
- **处理方法不等**（ADR-081）：Actor 照常执行后面的项，它们读到还没写下的状态。
- **顺序不变**：留住的输出挡住同一个根之后发给同一个目标的输出（第 1 节的保证 2）：之后普通的项发给那个目标的也等这次写，发给别的目标的当场离开。代价只落在这一对上。
- **跨目标的先后不管**：之后一个普通的项读到还没写下的状态，把它告诉另一个目标（例如推给客户端"余额 90"），这条当场离开，可能比让余额变成 90 的那次写更早到；那次写失败、被隔离时，客户端看到的是一个回滚了的值。要"写成之后才告诉别人"的，那一项也调用 `Commit`（根上没有还没写下的改动时它当场放出，有就等那次写）。
- **没有要写的就不写**：根上没有还没写下的改动（上一次写已经写成，这一项也没改要保存的数据）时，`Commit` 什么也不写，输出当场离开。所以重复的请求回答得和普通请求一样快；前一次的改动还没写成时，这一次的回答也等它（重复的请求也调用 `Commit`，下面的充值）。
- **最坏的延迟**：这个 tick 剩下的时间，加上这个根已经在途的那次写（一个根同时最多一次），加上一条语句；这一刻要写的根多于一条语句时，再加后面的；world 在途的语句到了上限时，还要等一个名额（第 10 节的"默认"）。存储慢，回答跟着慢；存储不回答时，回答一直等到存储回来（超过 `IsolationTimeout` 整个集群冻结，第 2 节）：`AskUntilAnswered` 按自己的期限超时，用同一个序号再问（下面），框架回答第一次存下的回复。
- **留住的输出有上限**：每个根最多 `DaServer:Commit:HeldBytesPerRoot`（1 MiB），每个 world 最多 `DaServer:Commit:HeldBytesPerWorld`（64 MiB）。到了上限，发给这个根（world 到了上限：这个 world 上调用过 `Commit` 的根）的新请求在执行之前就回 `Busy`，`RetryAfter` 是测得的写记录延迟，单向消息丢弃并计数；存储慢的时候内存有界，压力推回给发送方（指标 `daserver.commits.held_bytes`）。
- **只能在根要保存的 Actor 类型上调用**（分析器报错，DAS031）。处理方法里、系统里都行（`Parallel = true` 的方法除外，同第 5 节的所有操作）；系统里的一次 `Commit` 管这一行的输出。
- **`root.IssueClientToken` 隐含 `Commit`**：令牌是回复，在签发序号写成之后才离开（第 7 节）。
- **停用、迁出、热重启的冻结和优雅停止**先等这个根在途的写和留住的输出放出：停用的最后一次写写成后放出（第 6 节）；迁出时源节点先写一次这个根，放出之后才搬字节，存储不回答就推迟这次迁移；冻结等它们，等过冻结的上限就放弃这次热重启（第 10 节）。**最后一次写发出之后，这个根的新邮件停在它上面不执行**（同 `Saving` 的占位），所以一个每 tick 都提交的根也等得完：不然每次写成之后又有新的留住的输出，迁移和冻结永远等不到头。这一次写的 `SaveEntitiesWith` 抛出、或者存储拒绝它的那一行的根不等：它留住的输出当场丢弃，迁出退回到强制停用，冻结从冻结的 world 里销毁它的子树、留在交接的字节外面（第 10 节的"只写改过的"）；优雅停止在停止预算内等，过了就丢弃。所以占位、迁移的字节和交接的字节里都没有留住的输出。
- **`Reload` 先等在途的写有结果**（第 5 节）：写成就放出这次写的写代的输出，失败或超时就丢弃；还没写的写代的输出丢弃。丢掉的回复对发问方是超时：结果未知，`AskUntilAnswered` 用同一个序号再问。
- **推荐把经济状态放在不按 tick 走的 Actor 上**（第 12 节的 `Loot`、Player）：否则地图每 `Commit` 一次，整张地图的记录就要在这个 tick 末写一次。这不是要求，分析器不管。

### 去重：订单是自己的 Actor，转移价值的那一步是 `[Once]`

框架不按调用 id 去重所有请求（同 Orleans：投递最多一次，"调用方负责端到端地重试"，[best practices](https://learn.microsoft.com/dotnet/orleans/resources/best-practices)）。要"做且只做一次"的分两层：

- **订单本身是自己的 Actor**：一笔支付是 `Payment(单号)`，一个秒杀订单是 `Order(场次, 买家)`，各自是一行记录（这就是"订单表"）。同一笔的重复 webhook、同一个买家再来，都由这个 Actor 的状态回答（`Charge.State`、订单的状态），不需要别的去重。
- **把价值转进另一个 Actor 的那一步**（`Payment` 给 Player 入账、客户端花钻石买东西、`Loot` 给 Player 发物品）是标了 `[Once]` 的请求，框架去重。做法照 TCP（[RFC 9293](https://www.rfc-editor.org/rfc/rfc9293)）：发送方给每次发出编一个序号，接收方按序号认出重复，发送方累计确认，接收方据此清理，并告诉发送方还能发几个。

**序号**：
- 发送方是一个要保存的 Actor（根要保存）或者一个客户端会话，别的都不能发 `[Once]` 的请求（根不保存的 Actor 类型：DAS031；`ICluster`、`[Stateless]`：DAS032）。
- 每个发送方一个计数器，单调递增，从不重复：Actor 的在根的框架组件 `RootState` 里（`OnceNext`），和发出它的那次写一起落定（发出 `[Once]` 的请求隐含 `Commit`，下面），所以宕机、迁移、热重启之后接着数；客户端的在 SDK 里，见下面的"客户端会话"。一个发送方的序号发给所有接收方，不按接收方分开：发送方只要一个计数器、一个"最小的未完成"，接收方看到的序号有空洞（发给别人的），不影响下面的判断。
- **序号是一对 `(起点, 计数)`，先比起点、再比计数**：起点是 `long`，计数是 `uint`；线上、`Unacked` 的项、`OnceSenders` 的行都带这一对。Actor 的起点是这个记录第一次被认领时的 epoch（存储的序列，从不重复，第 10 节），计数从 0 起；客户端会话的起点是 0，计数是 SDK 的调用 id（会话本身就是一个新的发送方，下面）。所以一个被销毁又重建的 Actor（地图重建后的 `Loot`、删掉又建的 Player、同一个场次和买家的新 `Order`）的序号都大于旧的那个用过的，接收方不会把新请求当成旧的拒掉，也不会拿旧的回复回答它；epoch 再大也不溢出（不做移位）。计数用到只剩 2^24 时报警（`daserver.once.counter_low`），用完时这个根不再发 `[Once]`（`AskUntilAnswered` 停在那里、报警，由对账处理）：一个记录要发约 40 亿个才碰得到。
- 重试沿用原来的序号。用户不写键：框架分配，所以不会有"键重复了，新操作被当成旧的吞掉"（地图宕机之后掉落的编号重用这类），也不需要一个请求里有业务的键字段。
- **每次尝试带着 UTC 发出时刻**（同一个序号的每次重试各带各的）：Actor 发送方的由发送方节点盖，客户端的由网关在收到这次尝试时盖（设备的时钟不可信）。接收方不执行发出时刻早于 `MaxAskTimeout` 加 `DaServer:ClockSkew` 之前的尝试（丢弃、计数 `daserver.once.too_old`，告警规则 `DaServerOnceTooOld`：节点的时钟漂得比 `ClockSkew` 还远时，每次尝试都会被悄悄丢掉）。期限在线上是剩余时间，一帧在满的窗口、分片重组、重连里等过多久，剩余时间并不知道；发出时刻是绝对的，所以下面"忘掉一个发送方"和"会话的行按时间删"只依赖两个绝对时刻（销毁的那一刻、一次调用的重发截止），不依赖链路上等了多久，也不依赖接收方见过哪次尝试。
- **每次尝试有自己的身份**：Actor 发送方每次尝试一个新的调用 id；客户端的调用 id 就是序号，所以 SDK 的信封里另带这一次的尝试次数（从 1 数），回答带回它。

**接收方**：
- `Unacked`（根上保存的框架缓冲，生成器给收 `[Once]` 请求的 Actor 类型的根副本带上空的，所以写它不是结构变更）：每一项是 `(发送方, 序号, 当时的回复, 写代)`：回复是不超过 32 字节的定长值（`bool`、枚举、小的结构体，DAS032），写代是包含它的那次写，重复的请求等它写成才回答。最多 `[ActorOptions(Unacked = n)]` 项，默认 1,024；每项约 84 字节（发送方的 id 约 32、序号 12、回复 32、写代 8），满了约 84 KiB，在记录的上限之内。它在根的记录里，所以每次这个根写记录都整个重写：一个满载的接收方每 tick 提交一次，约 84 KiB × 20 = 每秒约 1.7 MB 的记录加同样多的 WAL，这是 `Unacked` 上限的代价，调小 `Unacked` 就小（基准 `OnceReceiverWriteBytes`）。**大小要按"项活多久"来定**：一项从执行到被确认约是"回复的那次写 + 发送方处理回复的那次写 + 一次确认"，存储正常时约 0.1–0.2 秒；秒杀的一个库存分片每秒约 5,000 个 `Reserve` 时约 500–1,000 项，所以默认的 1,024 是刚好够，存储慢一倍就会回 `WindowFull`（发送方排队，不丢）：这样的接收方调大 `Unacked`（4,096 约 340 KiB）。
- **`OnceSenders` 是自己的存储行，不在根的记录里**：每个 `(接收方, 发送方)` 一行 `(已确认到 n)`，客户端会话的行另有 `可删于`，被忘掉的 Actor 发送方的行另有 `忘于`，被删掉的接收方的行另有 `孤于`（下面）：序号不大于 `n` 的都已完成并被确认，或者从来没发给这里。每行约 52 字节（发送方的 id 约 32、标记 12、时刻 8）。**Actor 发送方的行不按时间过期**，只在发送方或接收方被销毁之后、过了一段固定的时刻才删（下面的"忘掉一个发送方"），所以行数是"发过 `[Once]` 给它、还没被销毁的发送方"的个数。秒杀的一个库存分片：给它发过 `Reserve` 的只是走到它那里的订单（`Gate` 学到它卖完之后不再问它，第 12 节、ADR-091），一个分片几千到几万行、几百 KB 到几 MB；场次结束、订单被销毁之后，这些行跟着删。放进根的记录，一个热的接收方很快超过 `DaServer:Store:MaxRecordBytes`（4 MiB），每次提交还要重写全部。
  - **改了的行和记录同一个事务、同一条成批写的语句写下**（`n` 前进、`Unacked` 删掉项、新发送方的行必须一起落定，不然一个还在途的重复会再执行一次）。每个新发送方要两次行改动，在两次写里：建行，之后确认让 `n` 前进；每秒 5,000 个新发送方、每秒 20 次写，就是每次写约 500 行。**每个根每次写最多 `DaServer:Once:RowChangesPerWrite` 行改动**（默认 1,024，约 52 KB，一条语句放得下）：没写下的改动到了这个数，新发送方的请求回 `WindowFull`（`RetryAfter` 是下一次写的预计时刻），确认先记在内存里、下一次写再落下（确认晚一次写落下只是项多留一会儿）；写比一个 tick 慢时上限按每次写算，所以一个根每秒最多接 `RowChangesPerWrite / 2 × 每秒写的次数` 个新发送方（默认、每秒 20 次写约 10,000 个），超过的排队（测试 `OnceRowChangesBounded`，十万买家的 E2E 量出这个速率）。
  - **删行不在记录的语句里**：发送方被销毁（`daserver_delete_actor` 在删它的记录的事务里给它的行写下 `忘于`）、接收方被销毁（同一个事务给它作为接收方的行写下 `孤于`）、客户端会话过了 `可删于`（下面），都在记录的语句之外；到了时刻的行由 `daserver_once_expire` 分页删（每次最多几千行，第 10 节的清扫）。运行时的空闲扫描（`ShardIdleScan`）按同样的时刻丢掉内存里的副本，一个一直激活的接收方也不会攒满。
  - **激活时，认领成功之后**按接收方的键做一次范围读，把这些行读进 world（不是每条消息查一次库）：认领之前读，被围栏的旧主人迟到的一次写可能还落得进来，`n` 就回退了。行**不在迁移和交接的字节里**：源节点迁出之前、冻结时的那次写带上所有没写下的行改动（下面），目标认领之后自己读；交接时新进程照样读（共享同一个存储）。上限的 1,048,576 行约 55 MB，读一次约 1–3 秒；这段时间邮件停在占位上，受 `MaxWaitingMail` 和节点的 `WaitingMailBytes` 的上限（第 6 节），超过的回 `Busy`。
  - 每个接收方最多 `DaServer:Once:SendersPerReceiver` 行（默认 1,048,576）：满了，新发送方的请求回 `WindowFull`（`RetryAfter` 按 `Once:Retry`），从不挤掉一行；行只在发送方被销毁之后空出来（指标 `daserver.once.senders` 接近上限时报警，测试 `ShardRowsBounded`）。
- 一条 `[Once]` 请求到达，框架在执行之前查：
  - 发出时刻早于 `MaxAskTimeout + ClockSkew` 之前：**过时的尝试**，丢弃、计数（上面）。
  - 目标没有记录、这个请求又不能激活它（`[ActivatedBy]`，第 6 节）：回 `NoActor`，确定没执行。
  - 序号不大于这个发送方的 `n`：**过时的重复**（同 TCP 的序号检查），不执行，回 `Stale`（第 11 节；发送方已经确认过，没有谁在等它，只计数）。
  - 序号在 `Unacked` 里：**重复**，回答存着的回复（那一项还没写成时，等那次写，所以"已经做过"一定已经落定）。
  - 窗口用完、行改动到了上限、行数到了上限（上面、下面）：回 `WindowFull`，确定没执行。
  - 根在删除中（`root.Delete()` 之后，第 5 节）：新的回 `Busy`，不存下（不是定论）。
  - 否则是新的：处理方法执行一次，**隐含 `Commit`**，回复记进 `Unacked`，等记录写成才离开。宕机落在写成之前，改动和这一项一起丢掉，再问时当成第一次执行，不重复也不丢。
- 接收方的回答里，值和 `Stale`、`WindowFull` 是查过序号之后说的；`Faulted`（处理方法抛出，改动按 `Reload` 丢掉）和 `NotHandled` 也是，但它们**不存下**，所以只对这一次尝试说话：发送方只接受当前这次尝试的（Actor：同一个调用 id；客户端：同一个尝试次数）`Faulted`、`NotHandled`，更早的尝试迟到的这类回答丢弃、计数（不然一次迟到的 `Faulted` 会在重试已经执行之后被当成定论，触发补偿，东西却已经给了）。`NoActor` 是确定的（目标不存在，请求不能激活它）。只有链路上的结果（`TimedOut`、`ConnectionLost`、`Busy`、`NoOwner`、`Unavailable`）是未知。
- 接收方还有项时，框架给它的根挂一个唤醒（框架的 `WakeAt`，最老一项的时刻加 `ProbeAfter`）：停用了的接收方也会被叫醒去探测（下面），项不会因为没人激活它而一直留着。

**累计确认，丢了无害**（同 TCP 的 ACK）：
- 发送方发给这个接收方的每一封邮件（消息、请求、`[Once]` 请求的重试）都带着"比 `a` 小的序号我这边都已了结"。**`a` 取已经写下的状态，不取内存里的**：它是发送方最近一次写成的状态里、发给这个接收方、已发出还在等回答的问（"已发出"的行）的最小序号，没有就是那时的 `OnceNext`。排着还没发出的问没有序号（序号在把它改成"已发出"的那次写里才从 `OnceNext` 取），所以确认从不越过一个还没发出的问（测试 `AckNeverSkipsQueuedAsk`）。内存里刚删掉、还没写下的行（`Replied` 已经处理、那次写还在途）不算了结：不然那次写之前宕机，这一行从存储里回来再问，接收方已经确认、删了那一项，只能回 `Stale`。接收方删掉这个发送方序号小于 `a` 的项，`n` 前进到 `a` 之前（记下 `a`，比较时不含它）。
- 发送方处理完带回复的 `Replied`、那次写写成之后，框架再单独发一次确认（同 TCP 的延迟确认），走框架内部的通道（同心跳、成员表，第 1 节；在接收方收下时处理，不进邮箱，所以邮箱满、`Busy` 都丢不掉它；链路窗口里有一小块固定的预留给它和下面的探测、窗口更新，也给 `Busy` 的回答本身）。丢了（链路断开、接收方宕机在下一次写之前）也无害：下一封邮件、下一次确认、下面的探测都带着同样或更新的 `a`。
- 接收方清理 `Unacked` 不用 `Commit`，随下一次写落下（上面的行改动上限之内）；宕机后旧的项回来，再被下一次确认或探测清掉。
- **`Stale` 到不了 `AskUntilAnswered`**：确认只按写下的状态发，所以一行还在存储里的已发出的问，接收方从没被告知它了结了。万一收到（运行时的缺陷），计数、报警（`daserver.once.stale_asking`，告警规则），这一行停在那里不再问（状态改成"停住"，随这一项写下），由对账处理，从不交给用户。做清扫的节点每分钟按类型从问的表里数一次停住的行（部分索引），导出成整个集群的量（`daserver.once.stopped`，按 `cluster`、`hosting_type`（托管这个 Actor 类型的服务器类型，从成员表里各节点托管的类型解析出来；有几个托管它的服务器类型就各导出一点，没有活着的托管者时是 `none`）和 `actor_type`；只在它做着清扫、这次计数查成了时导出，不导出缓存的值，冻结时也不导出），所以 Actor 停用、节点重启、根迁走都不让它归零，告警一直响到对账处理掉（测试 `StoppedAskingCountedFromStore`）；计数器从节点启动起就以 0 导出，第一次出现也看得见。

**流量控制**（同 TCP 的接收窗口）：
- 接收方给每个发送方最多 `[ActorOptions(OnceWindow = n)]` 项未确认的（默认 16），整个根最多 `Unacked` 项。每个回答都带着这个发送方还能用的项数。
- 发送方据此在自己这边排队，不发出去被拒：窗口用完时，后面的 `[Once]` 请求留在发送方（它们本来就是写进记录的意图，下面），窗口打开再发。
- **窗口由接收方推开**（同 TCP 的窗口更新）：回了 `WindowFull` 的发送方按先后记在接收方的一个有界队列里（`DaServer:Once:RefusedQueue`，默认 4,096）；窗口打开时（项清掉；行数到了上限时是行被删时，不是项清掉时），接收方按这个顺序给它们发窗口更新（内部通道）。所以排着的发送方不靠退避去碰运气，先被拒的先拿到窗口，一个退避到一分钟的发送方也不会错过（秒杀）。更新丢了，队列里的发送方按固定的 `DaServer:Once:ZeroWindowProbe`（默认 1 秒，不翻倍）再问一次最早的那个（同 TCP 的零窗口探测）。没进队列的（队列满了）照 `RetryAfter`、再按 `DaServer:Once:Retry` 翻倍退避，所以十万个被拒的发送方不会每秒各来问一次。
- 一个发送方占不满一个接收方：别的发送方照样有自己的窗口，直到整个根的上限。
- **问全在一张表里，背压只在入口**：`AskUntilAnswered` 的每个问都是存储里单独的一行（`daserver_once_asks`：发问的根、调用的先后 `RootState.AskOrdinal`、状态"排着""已发出""停住"、序号（发出之前为空）、接收方、发问的实体、请求的字节、下一次再问的时刻、第几次），和发出它的那一项的记录在同一条语句里写下、随记录的比较落定（同 `OnceSenders` 的行，第 10 节），算在每次写的行改动上限里；记录里没有问，所以问再多也撑不爆记录（`RecordUnwritable`），一项扣了东西、又发出意图，宕机之后两者一起回来。一个请求最多 `DaServer:Once:MaxRequestBytes`（默认 1 MiB，远小于一条语句的上限），调用时检查，超了在发问的那一项里抛出。**发送**：按调用的先后、对每个接收方依次发（同一个目标保序，第 1 节），受接收方的窗口和 `[ActorOptions(OnceInFlight)]`（默认 1,024，一个根同时在发的个数）限制，这只限内存和发送的速度，从不拒绝什么；一行在把它改成"已发出"的那次写里才从 `OnceNext` 取序号，写成之后才离开，宕机之后用存下的序号再发（测试 `AskRowsOneTable`、`SeqAssignedAtSend`）。激活着的根把它的行留在内存里，认领之后和记录一起范围读回（同 `OnceSenders`；几千行是毫秒级，入口的背压让一个根排着的行停在上限附近）；行的"下一次再问"进根的 `NextDue`，所以根停用、节点宕机之后 `DueScan` 照样把它叫醒接着问（测试 `QueuedAsksWakeAfterCrash`）。**背压只在入口**：一个根排着没发的问超过 `[ActorOptions(OnceQueued)]`（默认 1,024 行）或 `DaServer:Once:QueuedBytes`（默认 64 MiB）时，发给它的普通请求和客户端的 `[Once]` 请求在执行之前回 `Busy`（给客户端会话宣告的窗口降到 0），别的都照常：系统、反应（`Leaving`、`Deactivating`、`Restarting`、`Resumed`、`[Changed<T>]`）、计时、别的 Actor 的 `[Once]` 请求和回答（`Replied`）。后两种每执行一个就清掉某个根的一个问，所以两个、三个互相问的根（玩家交易、公会银行）一直往前走，没有标签、没有停掉的系统、没有错过的反应（测试 `AdmissionBusyAtQueuedCap`、`ReactionsRunUnderBackPressure`、`MutualOnceRootsProgress`）。Actor 的 `[Once]` 请求也不受邮箱上限 `MaxMail` 限制：限制它们的是每个发送方的窗口和根的 `Unacked` 总数，邮箱满了也不拒掉清掉别人的问的那一个（测试 `OnceRequestsExemptFromMaxMail`）。上限之后的增长只来自这些照常执行的项，受存储限制，所以是告警，不是拒绝：`DaServerOnceAsksGrowing`（做清扫的节点每分钟按 Actor 类型从存储数一次问的行，`daserver.once.ask_rows`，标签同 `daserver.once.stopped`；一个类型所有根加起来超过 1,024 行、半小时里一直在涨，多半是一个卡住的下游，或者一条每个回答问两次以上的环）。`Replied` 或 `[Once]` 的处理方法在循环里调用 `AskUntilAnswered` 是警告 DAS034。指标 `daserver.once.asks{state}`、`daserver.once.queued_full`、`daserver.once.ask_rows`。

**探测**（同 TCP 的保活探测）：只为了清掉确认丢了的项。一个发送方的项在 `Unacked` 里放了 `DaServer:Once:ProbeAfter`（默认 30 秒）还没被确认，又没有新的邮件带来确认，接收方问一次发送方"你那边的 `a` 是多少"（框架邮件 `AckQuery`，内部通道）：
- Actor 发送方：发送方节点的运行时从写下的状态回答，不激活这个 Actor：激活着的从它最近写成的状态（上面的 `a`），没激活的读一次它的问的行和记录里的 `OnceNext`；同一个节点收到的探测成批读（一个节点宕机之后，几千个接收方同时来问，也只是几条成批的读，不是几千次激活）。记录不存在（发送方被删掉了）就回答"已忘掉"：接收方把这个发送方的行和项留到存储里记着的"忘于"，到时一起丢掉（下面）；在那之前还在途的重复照样认得出来。
- 客户端会话：问接收方最后一次看到这个会话时经过的网关，网关从会话表回答；网关不认识这个会话（客户端换了网关、这个网关重启过）就回答"不知道"：接收方不清理，这些项随这个会话的行在"可删于"一起删（下面）。
- 回答带着发送方的 `a`，接收方照普通确认清理、`n` 前进。回答也走内部通道，丢了就过一个 `ProbeAfter` 再问。所以确认丢了、接收方宕机回滚了清理，都不会让一项永远留着：要么发送方还在问（它在等，这一项该留），要么探测把它清掉。探测里没有时钟的推理：它只问"了结到哪里"。

**忘掉一个发送方**（代替 TCP 的 TIME-WAIT：不看接收方见过什么，只看发送方什么时候不在了）：
- **Actor 发送方的行不按时间过期**，只有两种情况删：发送方被销毁，或者接收方自己被销毁。
- **发送方被销毁**：删除它的记录的那个事务（`root.Delete()`，第 5 节；从没写成的空记录被清扫时，它从没发出过 `[Once]`，没有行）同时给所有接收方那里这个发送方的行写下 `忘于 = now() + MaxAskTimeout + 3 × ClockSkew`（`daserver_delete_actor`，按 `sender` 的索引；存储的时钟），到了这个时刻由 `daserver_once_expire` 分页删掉。记录在，行就在；记录删了，行一定会删，没有中间状态，也不需要发送方记着它问过谁、一个个去通知。激活着的接收方另外经内部通道收到一次"忘掉"（带着"忘于"和被删掉的那一个的起点，尽力而为，只在这个起点等于内存里这一行的起点时生效：内存里的起点更大，说明同一个键已经重建、新的那个还没写下，新的赢；没收到的，内存里的行照样在读到的"忘于"、或者探测得到"已忘掉"之后到时丢掉），行和项都留到"忘于"：在那之前到达的重复照样回答存着的回复。
- **为什么要等这一段，而不是当场删**：销毁之前已经发出、还在链路窗口、分片重组或冻结的 world 里的尝试，在行删掉之后到达，就会被当成新的再执行一次。每次尝试带着发出时刻（上面），接收方不执行发出得比 `MaxAskTimeout + ClockSkew` 更早的尝试；销毁之后发送方不再发出任何尝试（它的问的行随记录删掉，重建的新记录起点更大），所以等过 `MaxAskTimeout` 加三个 `ClockSkew`（发送方节点、存储、接收方节点三个时钟两两之差都在 `ClockSkew` 之内），销毁之前发出的每次尝试到这时都已经过时。这是销毁那一刻的一个绝对时刻，不随接收方见过哪次尝试变化。
- **接收方被销毁**：`root.Delete()` 等 `Unacked` 清空、没有问的行、过了宽限才真正删除（第 5 节）：它执行过的每个 `[Once]` 请求的回复都已被发送方确认，没有谁还会为它重试；在那之前的重复照样回答存着的回复。删除**不删**它作为接收方的行，而是在同一个事务里给它们写下 `孤于 = now() + MaxAskTimeout + 3 × ClockSkew`（取较晚的那个），到时由 `daserver_once_expire` 删掉。所以删除之后同一个键被激活出新的 Actor 时，它认领之后读到这些行：旧发送方迟到的、已经执行过的尝试，序号不大于 `n`，回 `Stale`，不会在新的 Actor 上再执行一次（测试 `DeletedReceiverRefusesLateAttempt`；支付的 `Open` 正是 `[Once]` 加 `[ActivatedBy<Open>]` 的形状）；新的 Actor 写这一行的改动清掉"孤于"（它又在收这个发送方的请求了）。没在这里执行过的尝试照常是新的：能激活这个类型的，在新的 Actor 上执行；不能激活的回 `NoActor`，确定没执行，`AskUntilAnswered` 把它作为确定的拒绝交给 `Replied`。不等 `Unacked` 清空就删，一个执行过、改动已经发给第三方（例如它 `AskUntilAnswered` 的入账）的请求，重试时会得到"确定没执行"，发送方据此补偿就重复了（测试 `DeleteWaitsForUnackedEmpty`）。
- **重建的发送方**：同一个键删掉又建出来，新的记录起点更大。接收方第一次看到一个发送方的起点大于它那一行的起点时，把 `n` 提到 `(新起点, 0)`，清掉那一行的"忘于"（内存里，写行改动的语句里也一样：起点更大的改动清掉"忘于"），旧起点的序号都属于被删掉的那一个，之后到达的回 `Stale`。不清的话，新的那个的行会在旧的"忘于"一到就被删，它在途的重试就被当成新的再执行一次（测试 `RecreatedSenderOutlivesOldForgetAfter`）。再删一次，"忘于"重新写成这一次的（取较晚的那个）。
- **接收方加载时**，`Unacked` 里没有对应行的项（发送方已经被忘掉、行已经删了）丢掉；运行时的空闲扫描在"忘于"或"孤于"再过一个 `ClockSkew` 时丢掉内存里的行和它的项，但从不丢还有项或者还有没写下的改动的行；内存里的行一有改动（收下一个请求、`n` 前移、加一项），它在内存里的"孤于"就清掉，和写下时存储里清掉的一样，所以同一个键重建出来的接收方不会在旧的"孤于"到时丢掉自己刚收下、还没被确认的项（测试 `RecreatedReceiverKeepsEntriesPastOrphanAfter`）。一直激活的接收方没收到"忘掉"、又没有项要探测时也不会一直留着这一行：空闲扫描每过 `ProbeAfter` 按自己的键读一次带"忘于"的行（`忘于 IS NOT NULL`，按接收方的索引的范围读），读到的行带着它的起点，同样只在起点和内存里的一致时才生效（测试 `StaleForgetNoticeIgnoredForRecreatedSender`）。
- **被忘掉的行不会被写回来**：接收方写行改动的那条语句只在发送方的记录还在时插入新行（对那条记录取 `FOR KEY SHARE`），删除先删记录、再在下一条语句里标它的行，所以一个和删除同时落下的行改动要么被删除等到、看见，要么看见记录已经不在、不插入（第 10 节的语句）。
- **迁出和冻结先写下没写的行改动**：迁出之前源节点的那次写、热重启冻结时的写，都带上这个根所有还没写下的行改动（最多 `RowChangesPerWrite` 行），所以迁移和交接的字节里从不需要它们，目标和新进程读到的行就是完整的。那次写之后再到的确认不再记（丢掉）：确认是累计的，下一封邮件、下一次确认或探测会带给新的主人（测试 `HandoverKeepsUnwrittenRowChanges`）。

**客户端会话**：
- **会话是网关签名的令牌**：SDK 第一次连上时，网关签发 `Session(ClientId, 会话随机数, 有效至 T, 剩余 R, 调用的高水位)`，用集群的签名密钥签名（同客户端令牌，第 7 节）。`T` 每次回复和心跳都往后推到"现在加 `DaServer:Clients:ResumeWindow`"（默认 2 分钟）；新签一个令牌只在心跳时、或者 `T` 比上一个令牌后移超过 `ResumeWindow` 的四分之一时（不是每个回复都多一次 HMAC 和约 80 字节）。SDK 重连时交回它，**任何网关**都只认还没过 `T` 的（网关之间的时钟差在 `ClockSkew` 之内）：过了就拒，SDK 换一个新的会话。
- **恢复会话要同时交回有效的客户端令牌**（第 7 节），会话的 `ClientId` 要和它一致，`T` 从不超过客户端令牌的到期：偷到的会话令牌不能靠心跳一直续下去。
- **一个 `[Once]` 调用最多重发 `ResumeWindow`，从它第一次发出算**，不是整个会话：会话可以靠心跳一直续，一次调用不行。SDK 从第一次发出起用单调时钟计时，满了就不再重发这次调用，结果未知，应用重新拉一次状态（背包、余额）就知道；设备的时钟快了几个小时也不影响。**每个网关都强制它**：令牌里带着每四分之一个 `ResumeWindow` 一个"到这时为止见过的最大调用 id"（最近五个，更早的合成一个），一个调用 id 第一次出现在哪一格就从哪一格算起，过了"那一格的末尾加 `ResumeWindow`"（它的"重发截止"）的尝试，任何网关都拒绝。拿着一个更早的令牌来的、调用 id 比令牌里的高水位都大的，按令牌签发的那一格算（调用一定是在那之后第一次发出的）：截止只会更早、不会更晚，换个网关、交回旧令牌都延不长一次调用的重发。网关把这次调用的重发截止盖在每次尝试上。所以不管客户端换了几个网关、接收方有多久收不到重发，一次调用过了重发截止就再也执行不了（测试 `ClientCallResendWindowEnforcedByGateway`）。
- **会话的行按时间删**（只有客户端的行有时间的规则，因为重发的期限由网关强制）：接收方的行记着它见过的这个会话的调用里最晚的重发截止，`可删于 = 那个截止 + MaxAskTimeout + ClockSkew`，到了就删掉这个会话的项和那一行（`daserver_once_expire`，接收方停用了也一样）。网关在重发截止之后不放行任何尝试，接收方不执行发出（网关盖的时刻）超过 `MaxAskTimeout + ClockSkew` 的尝试，所以到"可删于"时，它见过的每个调用都不会再有尝试被执行；它没见过的调用从没在这里执行过，之后到达是第一次。接收方有多久收不到这个会话的重发都不影响：截止是调用的，不是接收方看到的。
- SDK 在每个请求和心跳里带着这个会话的 `a`（最小的还没收到回复的 `[Once]` 调用 id），网关记下，并拒绝序号小于它的 `[Once]` 请求（过时的，或者有人重放旧的调用 id；普通请求的调用 id 不受这个限制，SDK 里排着的、id 更小的普通请求照常过）。网关记着这个会话有项留在哪些接收方，最多 `DaServer:Clients:OnceReceiversPerSession` 个（默认 64，满了这个会话新的 `[Once]` 请求回 `Busy`，SDK 照 `RetryAfter` 重发），`a` 前进时给它们各发一次确认（内部通道），探测也由它回答（上面）。
- `[Once]` 的请求由 SDK 自己用同一个序号重发，直到有回复或者到了这次调用的重发截止；应用只拿到一次结果，自己从不重发（`Busy` 也由 SDK 按 `RetryAfter` 重发）。SDK 收到 `Stale` 时按"结果未知"交给应用（这一次已经有过结论，只是这条连接没收到），应用重新拉一次状态。应用重启是新的会话；序号的计数就是 SDK 给 `[Once]` 请求的调用 id，在会话里单调递增。

**限制**：
- `[Once]` 请求的处理方法要当场返回回复，不能取 `ReplyTo<T>`（DAS032）：存下的回复要和改动在同一次写里落定。要先问别的 Actor 或者外面的服务才能回答的，把它拆成自己的订单 Actor 的一步（秒杀的 `Order`、共用的支付样例的 `Payment`）。
- `Unacked` 在根的记录里，`OnceSenders` 是自己的存储行（上面）：都是框架的持久化数据，在运行时自己的追踪过滤里，不在用户的追踪类型表、`wire.manifest` 的 `record` 行里；`wire.manifest` 给每个 `[Once]` 的请求类型记一个 `once` 标记（加上或去掉它会改变语义，滚动升级的比较要看得见，第 14 节）。
- 指标 `daserver.once.entries`、`daserver.once.senders`、`daserver.once.duplicates`、`daserver.once.stale`、`daserver.once.stale_asking`、`daserver.once.too_old`、`daserver.once.window_full`、`daserver.once.probes`、`daserver.once.forgotten`、`daserver.once.counter_low`、`daserver.once.asks`、`daserver.once.queued_full`、`daserver.once.ask_rows`、`daserver.once.stopped`、`daserver.delete.send_after`；`stale_asking`（和 `stopped`）、`counter_low`、`too_old`、`ask_rows` 和 `delete.send_after` 有告警规则（只在目标设计里有这些指标，规则标着落地的步骤）。

### 一直问到有回答：`AskUntilAnswered`

普通的问在发问方停用或宕机时随等待实体消失。要一直问到有回答的（入账、发物品、下单的每一步），写 `e.AskUntilAnswered(to, in request[, state])`，`e` 是根或者它要保存的子实体：

- **请求本身就是写下的意图**：框架为它写一行问（上面那张表：发问的实体、目标、请求的字节、`state`），和这一项一起写成（隐含 `Commit`），轮到它时才取序号、写成之后才发出（同事务性发件箱：状态和意图一起写下，转发方重发，接收方按 id 去重，[transactional outbox](https://microservices.io/patterns/data/transactional-outbox)）。所以"忘了先 `Commit` 意图就去问"不会凭空多出价值。问的行属于根，不属于发问的实体：发问的实体被别的路径销毁了（掉落过期、重新掉落），在途的问照样问到有回答，接收方的项照常被确认。
- **发问的实体按身份记，不按实体 id**：问的行记的是发问的实体在记录里的身份（根就是根；子实体是它的唯一键，`IUniqueIndexed<T>`，或者它在记录里的位置，加载时经 `LoadEntitiesInto` 换成新的 id），回答到了按它找到现在的实体；EasyEcs 的实体 id 会复用，所以从不直接存 id（测试 `AskingEntitySurvivesReload`）。
- **只用于 `[Once]` 的请求，`[Once]` 的请求也只能这样发**（分析器，DAS032）：不去重的请求一直重试会重复执行；去重的请求用普通的 `Ask` 发，结果未知时没有谁接着问。
- **框架负责重试**：链路上的结果（`TimedOut`、`Busy`、`NoOwner`、`Unavailable`、`ConnectionLost`）不交给用户，按 `RetryAfter`（没有就按 `DaServer:Once:Retry`，默认 5 秒起，翻倍到 1 分钟，带抖动）用同一个序号再问；节点宕机、Actor 停用之后，运行时按问的行的"下一次再问"（它进根的 `NextDue`）唤醒它接着问（第 10 节的唤醒）。窗口用完时按上面的流量控制排队。
- **回答照常是 `Replied<TRequest[, TState]>`**，只投递一次，主体是发问的实体：带值（`a.TryGetValue(out var v)`），或者接收方确定的拒绝（`Faulted`、`NotHandled`，只认当前这次尝试的：上面）。收到它的那一项隐含 `Commit`，这一行随之删掉，写成之后框架发确认。**补偿只在这两种回答上做**；链路上的结果永远不会到用户手里，所以不会出现"再问时收到 `NotExecuted` 就以为没做过"。
- 发问的实体已经不在了：回答照样删掉这一行、照样确认，回答本身丢弃、计数（`daserver.once.orphan_replies`）：价值已经在接收方落定，发问的实体由用户销毁，就是用户放弃了这个结果。删掉了的问迟到的回答也丢弃。
- 一直没有回答的（接收方的类型删了、一直 `WindowFull`）一直问下去，指标 `daserver.once.oldest_age` 报出最老的一个，样例的对账报出停在某一步太久的订单。
- 根还有问的行（任何状态）时 `root.Delete()` 回 `Outstanding`、什么都不删（第 5 节）：一个还在问的意图不会被悄悄丢下；等回答到了再删。删除中的根不能再问（运行时错误，第 5 节），所以删除时不会有新写的问被丢下。删掉之后，它在各接收方的行和项按上面的"忘掉一个发送方"删掉。

### 在两个 Actor 之间转移价值：一条规则

**扣的一方写下自己的状态，`AskUntilAnswered` 一个 `[Once]` 的请求；收的一方执行一次，回复存进 `Unacked`，写成之后才回答；扣的一方在 `Replied` 里记下完成。** 恰好一次，没有条件：不重复（序号由框架分配、从不重复；确认之前的重复拿到存着的回复，确认之后的旧尝试被序号检查拒绝），不丢（框架一直问到有回答，意图在记录里）。没有时间或容量的条件：窗口满了只让发送方排队，从不挤掉一项。TrinityCore 把交易的双方写进一个事务；这里两个 Actor 的记录不在一个事务里，所以拆成这条规则。没有按这条规则做的交互（例如捡一件普通的东西不 `Commit`）宕机时可能一方回滚、另一方没有：扣的一方先写，所以结果是丢了一件，不是多出一件，游戏宁可丢、不复制。**跨目标的因果不受保护**：一项读了还没写下的状态、发给另一个目标的普通输出不等那次写（`Commit` 的栅栏只按目标，第 8 节上面）；要跨目标都依赖已经落定的状态，就用这条规则。

- **支付是一个共用的样例**（`samples/payments`，mmo 的充值和秒杀都用它，W7）：照支付平台的正规流程，外面的 IO 都在 HTTP 平面，不在 shard 上（DAS015）。游戏提供五样：`PayFor`（这笔为谁付、付了之后给什么）、`Notify`（付了或过期之后怎么通知它）、它的 `[Once] Settled`、给 `Open` 加上的 `[From<…>]`（谁能开一笔），以及在它的某个服务器类型上 `[Hosts<Payment>]`（mmo 放在 `PlayerServer` 上，秒杀放在它的服务器上；没有服务器类型托管 `Payment`，AppHost 的构建检查会失败）。
  1. 发起方开一笔：mmo 的客户端经客户端协议发 `[Once] Recharge(Pack)` 给 Player，Player 查充值包的表，生成这笔的 id，`AskUntilAnswered(new Payment(id), new Open(id, new PayFor(me, 钻石数), 价格, 期限))`，回答客户端这笔的 id（重发拿到同一个 id：`[Once]` 存着这个回复）；秒杀是订单预留到库存之后自己开。要给什么都在 `PayFor` 里，`Settled` 原样带回，所以发起方不用另外记"等着哪一笔"。
  2. 客户端调用节点的 HTTP 端点 `POST /payments/{id}/checkout`（低频，HTTP 平面的本分）：端点经 `ICluster.Ask` 读出这笔的金额和状态，向支付平台创建收银（IO，在 shard 外），**以这笔的 id 作为平台的幂等键**，所以点两次"去付款"是同一个收银、不会扣两次；把平台的收银地址返回给客户端，客户端去平台付款。`Open` 还没到 `Payment`（客户端拿到 id 的时刻早于 `Open` 落定）时读到 `NoActor`：端点短暂重试（最多 2 秒），还没有就回 409，客户端稍后再点。
  3. 平台回调我们的 webhook（HTTP 平面），带着这笔的 id 和平台的令牌；端点先用平台的签名验回调，再拿令牌向平台服务器到服务器地查这笔（金额、平台的流水号），都对上才 `ICluster.Ask(new Payment(id), new Paid(amount, providerRef))`。**每个确定的结果都回 2xx**（接受了、重复的、要退款的）：非 2xx 只给链路上的失败，平台按自己的规则重发（几天），由 `Payment` 的状态回答。webhook 在 `Payment` 写下之后才回。
  4. `Payment` 写下 `Paid`，`Notify` 由游戏 `AskUntilAnswered` 发起方一个 `[Once] Settled(Id, For, Outcome)`：mmo 的 Player 入账钻石，秒杀的 `Order` 确认；`Settled` 的回答到了，这笔写成 `Settled`。过了收款期限没付的，`Payment` 写下 `Expired`，同样通知发起方（过期）。**状态只有两条路**：`Open → Paid → Settled`、`Open → Expired`。**不是被接受的那一笔的付款都进退款缓冲**（`Payment` 的 `DynamicBuffer<Refund>`，每个平台流水号一次）：过期之后才到的、金额对不上的、已经付过又来一笔流水号不同的（买家付了两次）；平台重发同一个流水号，回答之前的结果，不再记一次。都用 `Commit` 写下，对账报出，模拟的平台能退款。金额对不上的付款不改状态，所以收款期限一到照样通知发起方，订单不会一直等着。
  5. 退款：退款缓冲里的每一笔由平台退回（模拟的平台、或者运营在平台后台），退款完成的回调（同样验签、向平台核实）经 `ICluster.Ask(new Payment(id), new Refunded(providerRef))` 在那一项上记下退款时刻；重复的回调回答之前的结果。
  6. 每天和平台的报表对账（样例的一个后台任务，经 `ICluster`）：平台有、`Payment` 没有的单号，停在 `Open`/`Paid` 太久的，退款缓冲里还没退的，都报出来。对过账的每笔再收到一次 `Purge`：已经了结（`Settled` 或 `Expired`）、退款都已完成、并且现在晚于结算时刻（`SettledAt`，写下 `Settled` 或 `Expired` 的那一刻）加 `Payments.Retention`（样例里的一个常量，30 天，长过平台重发回调的几天；要改就改这一行，它不是框架的配置）的，`root.Delete()`；别的回 `false`，下一天再来。删除之后它在发起方那里的 `[Once]` 行随之忘掉（上面），所以 Player 上的行数跟着还留着的支付走，不跟着一辈子充过几次走；之后迟到的回调得到 `NoActor`，端点回 2xx 并记一条日志，对账报出。
  ```csharp
  // samples/payments, linked into each game's server. The game supplies PayFor (who is paid for, what they get),
  // its [Once] Settled, the [From<…>] on Open, [Hosts<Payment>] on one of its server types, and implements Notify.
  [NinoType] public readonly record struct PaymentId(Guid Value);
  [NinoType] public readonly record struct ProviderRef(Guid Value);                 // the provider's transaction id
  [ActivatedBy<Open>] public readonly partial record struct Payment(PaymentId Id) : IActor<Payment>
  {
      [Persist] public static Prefab RootPrefab { get; } = new Prefab().Set(default(Charge)).SetBuffer<Refund>();
  }
  public enum ChargeState : byte { None, Open, Paid, Settled, Expired }      // Open → Paid → Settled, or Open → Expired
  public enum PayOutcome : byte { Paid, Expired }
  public enum PaidResult : byte { Accepted, AlreadyPaid, Refunding }             // every one is a 2xx at the webhook
  [NinoType] public record struct Charge(PaymentId Id, PayFor For, long Amount, ChargeState State, DateTime Deadline,
                                         ProviderRef Ref, DateTime SettledAt) : IComponent, ITracked;   // Ref: the accepted payment
  [NinoType] public record struct Refund(ProviderRef Ref, long Amount, DateTime RefundedAt);   // default RefundedAt: not yet
  [Once, NinoType] public partial record struct Open(PaymentId Id, PayFor For, long Amount, DateTime Deadline)
      : IRequestTo<Payment, bool>;                                              // each game adds its [From<…>] (below)
  [NinoType] public record struct Paid(long Amount, ProviderRef Ref) : IRequestTo<Payment, PaidResult>;   // from ICluster
  [NinoType] public record struct Refunded(ProviderRef Ref) : IRequestTo<Payment, bool>;   // from ICluster: the provider refunded it
  [NinoType] public record struct Purge : IRequestTo<Payment, bool>;              // from the daily reconciliation
  [NinoType] public record struct Expire;                                          // the reminder's marker
  public static class Payments { public static readonly TimeSpan Retention = TimeSpan.FromDays(30); }   // the sample's constant

  [Handles<Payment>]
  public static partial class Charges
  {
      [On<Open>]                                                            // from the business owner, once per sequence number
      static bool Opened(in Open m, Mut<Charge> c, Entity root)
      {
          c.Write() = new Charge(m.Id, m.For, m.Amount, ChargeState.Open, m.Deadline, default, default);
          root.Remind<Expire>(m.Deadline);                                  // DaServer.Reminders: survives deactivation
          return true;
      }

      [On<Paid>]                                                            // the webhook, after verifying with the provider
      static PaidResult Accept(in Paid m, Mut<Charge> c, in DynamicBuffer<Refund> refunds, Entity root)
      {
          PaidResult result;
          if (c.Value.Ref == m.Ref && c.Value.State is ChargeState.Paid or ChargeState.Settled)
              result = PaidResult.AlreadyPaid;                              // the provider's retry of the accepted payment
          else if (Listed(in refunds, m.Ref))
              result = PaidResult.Refunding;                                // its retry of a payment already listed for refund
          else if (c.Value.State == ChargeState.Open && m.Amount == c.Value.Amount)
          {
              ref var w = ref c.Write();
              w.State = ChargeState.Paid; w.Ref = m.Ref;
              Notify(root, in w, PayOutcome.Paid);                          // the game's AskUntilAnswered of its [Once] Settled
              result = PaidResult.Accepted;
          }
          else
          {
              root.WriteRefund().Add(new Refund(m.Ref, m.Amount, default)); // late, the wrong amount, or a second payment
              result = PaidResult.Refunding;                                // the state is untouched: the deadline still notifies
          }
          root.Commit();                                                    // the 2xx leaves once this is written; a retry's too
          return result;
      }

      static bool Listed(in DynamicBuffer<Refund> refunds, ProviderRef r)
      {
          for (int i = 0; i < refunds.Count; i++) if (refunds[i].Ref == r) return true;
          return false;
      }

      [On<Replied<Settled>>]
      static void Done(in Replied<Settled> a, Mut<Charge> c, in Time time)
      {
          if (!a.TryGetValue(out _) || c.Value.State != ChargeState.Paid) return;
          ref var w = ref c.Write();
          w.State = ChargeState.Settled; w.SettledAt = time.Utc;
      }

      [On<Refunded>]                                                        // the provider's refund callback, verified
      static bool Refunded(in Refunded m, in DynamicBuffer<Refund> refunds, Entity root, in Time time)
      {
          for (int i = 0; i < refunds.Count; i++)
          {
              if (refunds[i].Ref != m.Ref) continue;
              if (refunds[i].RefundedAt == default) root.WriteRefund().AsSpan()[i].RefundedAt = time.Utc;   // the indexer is read-only
              root.Commit();
              return true;
          }
          return false;                                                     // not one we listed: reconciliation reports it
      }

      [On<Purge>]
      static bool Purge(in Purge m, in Charge c, in DynamicBuffer<Refund> refunds, Entity root, in Time time)
      {
          if (c.State is not (ChargeState.Settled or ChargeState.Expired)) return false;
          if (time.Utc < c.SettledAt + Payments.Retention) return false;
          for (int i = 0; i < refunds.Count; i++) if (refunds[i].RefundedAt == default) return false;
          return root.Delete() == Status.Ok;                                // Outstanding while a Settled is still asked
      }

      [On<Reminded<Expire>>]
      static void Expired(in Reminded<Expire> r, Mut<Charge> c, Entity root, in Time time)
      {
          if (c.Value.State != ChargeState.Open) return;
          ref var w = ref c.Write();
          w.State = ChargeState.Expired; w.SettledAt = time.Utc;
          Notify(root, in w, PayOutcome.Expired);
      }

      private static partial void Notify(Entity root, in Charge charge, PayOutcome outcome);   // the game implements it
  }

  // the mmo's side
  [NinoType] public readonly record struct PayFor(Player Who, int Diamonds);
  [From<Player>] public partial record struct Open;                     // Player opens payments (the flash sale: [From<Order>])
  [Once, NinoType, From<Payment>] public record struct Settled(PaymentId Id, PayFor For, PayOutcome Outcome)
      : IRequestTo<Player, bool>;
  public static partial class Charges                                 // the samples/payments sources are linked into the game
  {
      private static partial void Notify(Entity root, in Charge charge, PayOutcome outcome) =>   // same parameter names as the declaration (CS8826)
          root.AskUntilAnswered(charge.For.Who, new Settled(charge.Id, charge.For, outcome));
  }

  [On<Settled>]                                                          // subject: the player's root; runs once per sequence number
  static bool Credit(in Settled m, Mut<Wallet> wallet)
  {
      if (m.Outcome == PayOutcome.Paid) wallet.Write().Diamonds += m.For.Diamonds;
      return true;                                                       // [Once] commits: the answer leaves once this is written
  }
  ```
  处理方法里没有重试、意图组件和去重的代码：`Open`、`Settled` 由框架按序号去重，平台的重复回调由 `Charge.State` 和平台的流水号回答。`Paid` 不是 `[Once]`：它来自 `ICluster`（不能发 `[Once]`，第 8 节上面），重复由 `Charge` 和退款缓冲按平台的流水号回答。`Open` 在样例里声明成 `partial`，由每个游戏补上自己的 `[From<…>]`（mmo 是 `Player`，秒杀是 `Order`）：共用的样例不认识游戏的发送方，`[From<K>]` 又是必须的（第 4 节）。客户端只看到钻石变多（推送）。
- **玩家发起的贵重请求**（花钻石买东西、用珍贵道具）：
  ```csharp
  // Game.Protocol
  [Once, NinoType] public record struct BuyItem(int Slot) : IFromClient<BuyResult>;   // the SDK resends it until answered
  public enum BuyResult : byte { Ok, TooPoor, NoSuchItem, BagFull }

  // Game.Server: Wallet and Inventory are on Player.RootPrefab (section 12)
  public readonly record struct ShopTable(ShopRow[] Rows) : IConfigTable<ShopTable>   // Name and TryLoad as SkillTable's
  {
      public bool Has(int slot) => (uint)slot < (uint)Rows.Length;
      public int Price(int slot) => Rows[slot].Price;
      public int Item(int slot) => Rows[slot].Item;
  }

  [On<BuyItem>]                                                          // subject: the player's root; runs once per call:
  static BuyResult Buy(in BuyItem m, in ShopTable shop, Mut<Wallet> wallet, in Inventory inv,   // a resend gets the stored answer
                       in DynamicBuffer<Slot> bag, Entity root)
  {
      if (!shop.Has(m.Slot)) return BuyResult.NoSuchItem;                // the client's index is checked before it is used
      var price = shop.Price(m.Slot);                                    // the server decides the price
      if (wallet.Value.Diamonds < price) return BuyResult.TooPoor;
      if (bag.Count >= inv.Capacity) return BuyResult.BagFull;
      wallet.Write().Diamonds -= price;
      root.WriteSlot().Add(new Slot(shop.Item(m.Slot), 1));
      return BuyResult.Ok;                                               // [Once] commits: the reply leaves once this is written
  }
  ```
  处理方法里没有去重的代码：重发由 SDK 用同一个序号做，接收方回答存着的回复。应用只拿到一次结果；这次调用过了重发截止（从第一次发出算 `ResumeWindow`，每个网关都强制；应用被杀、断网太久）时结果未知，应用重新拉一次背包就知道。再点一次"购买"是一次新的购买。客户端给的下标先查再用：一个越界的下标抛出，会让 Player 按 `Reload` 回滚（第 5 节）。
- **秒杀的订单**（ADR-091）：`Order(Sale, User)` 是订单自己的 Actor，同一个买家再来，回答订单现在的状态。每一步是订单的状态加一次 `AskUntilAnswered`：写下 `Reserving`，问库存分片 `[Once] Reserve`（回答写进订单之后进下一步）。**分片回答卖完了，订单直接写下 `SoldOut`，结束**：什么也没预留，没有要释放的。预留到了，写下 `AwaitingPayment`，按上面共用的支付样例开一笔 `Payment`（`Open`，收款期限是这场秒杀的付款时限），买家拿着这笔的 id 去付款；`Payment` 的 `Settled(Paid)` 到了，订单写下 `Confirmed`。**只有预留到了之后的支付过期才释放**（`Settled` 说过期）：写下 `Releasing`，`AskUntilAnswered` 一个 `[Once] Release`，回答到了写下 `Released`。补偿只由确定的回答触发，链路上的结果不到用户手里，所以不会因为超时去补偿。库存分片是 `Reserve`、`Release` 的接收方，`[Once]` 替它去重（迟到的 `Reserve` 尝试是重复或过时的，不会落在已经释放的订单上）；它的预留集合是业务状态（还剩多少、谁预留了）。**释放的库存重新可卖**：分片把它加回剩余量；`Gate` 学到某个分片卖完之后不再问它，所以 `Gate` 每过 `SaleTable` 里的 `RecheckSoldOut`（默认 5 秒）用 `Describe` 重新问一次卖完了的分片，有剩余就重新放进轮转（测试 `ReleasedStockIsSoldAgain`）。**场次结束后删订单**：`Gate` 不保存（它是带键、不存盘的 Actor），所以"已结束"不写下来，也不用提醒：它每收到一个请求就比一次 `time.Utc` 和 `SaleTable` 里这一场的 `EndsAt`，过了就回 `SaleEnded`，不再新建订单（停用、重启之后也一样），所以删掉的订单不会被同一个买家再建出来（`SaleTable` 加载时校验付款时限不短于 `MaxAskTimeout + ClockSkew`：不然一个慢节点在 `EndsAt` 之前收下的建单，可能在它的订单删掉之后才到）；订单建出来时给自己挂一个提醒（`Remind<Close>`，在场次结束再过付款时限的时刻；提醒在订单自己的记录里，停用了也会被唤醒，`Gate` 不用记着一场里的上百万个订单），到时订单在了结的状态（`SoldOut`、`Released`、`Confirmed` 且已交给发货）里 `root.Delete()`，还没了结（或 `Outstanding`）的过一会再提醒自己。它在库存分片上的 `[Once]` 行随之忘掉，所以一个分片的行数是这一场走到它那里的订单数，场次之间不累积。

### 客户端的调用 id、`ReplyTo<T>`

- **客户端的调用 id**：SDK 一个计数器，在一个会话（`(ClientId, 会话随机数)`，上面）里单调递增，重连后接着数；网关在前面加上会话，所以处理方法读到的 `reply.Call` 是 `(ClientId, 会话, 客户端调用 id)`，全集群唯一。它用来对上回复；`[Once]` 的请求的序号就是它（上面）。连接断了还没收到回复的调用，SDK 以 `ConnectionLost` 结束：结果未知，同超时。单向的客户端消息不带调用 id：最多一次，SDK 不重发，不用去重。
- **问多个目标**：对每个目标各 `Ask` 一次，每个目标一个 `Replied`。要汇总时，把计数放进发问实体上的组件。
- **`ReplyTo<T>` 是普通数据**：
  - 请求的处理方法取 `in ReplyTo<T> reply` 参数；`T` 就是请求声明的回复类型，`Reply` 在编译期检查类型；
  - 要先问别人再回复：把令牌放进 `Ask` 的状态（第 12 节的 `Looting.Reply`），回信时 `me.Reply(in a.State.Reply, …)`；
  - 要晚些回复、不问别人：把令牌存进任何组件，之后 `Reply`；存在要保存的实体上的令牌跨过停用和宕机，期限内照样能回复：写进记录时它的期限按 UTC 时刻存（比较时多留 `DaServer:ClockSkew`），在内存里和迁移的字节里照常是剩余时间（第 9 节）；
  - `default` 的令牌，`Reply` 什么也不做；
  - 同一个令牌只有第一次回复有效；一直没有回复，调用方按期限超时；
  - 令牌带着请求的期限：过了期限的 `Reply` 不发出，计数（调用方已经按超时处理了）。
- 请求的处理方法要么返回回复，要么取 `ReplyTo<T>`，不能两样都做，也不能都不做（分析器检查）。
- 等待实体和它的状态随 Actor 迁移和热重启（序列化规则见第 10 节）。
- **回信不受 `Busy` 限制，它的位置在两头都留好**：回复的字节数按请求类型算：回复是定长的（`bool`、枚举、定长结构体）就是生成器算出的确切大小，变长的按 `DaServer:MaxReplyBytes`（64 KiB；更大的写 `[MaxReplySize(n)]`）。
  - **回信走的那一段，由接收方留**：接收方收下一个请求（进它的邮箱）时，在自己发往发问方节点的链路窗口里留这么多字节，回复发出或者请求过了期限就释放。所以一个往网关推广播推满了窗口的地图，照样能把回复发给通过这个网关问它的客户端：留的位置不和推送抢。留不下（窗口里留的已过一半）就不收这个请求，当场回 `Busy`：拒绝的是新的请求，从不是回信。这条 `Busy` 自己用窗口里一小块固定的预留（同内部通道的确认、探测、窗口更新），被推送占满的窗口也挤不掉它，所以发问方收到的是 `Busy`，不是超时。
  - **回信到达的那一段，由发问方留**：每次 `Ask` 在发问方这个 world 的派发队列里留同样多的字节，回信到达或者这次问到了期限就释放（存着的 `ReplyTo<T>` 过了期限也不再占位置）；留的已过队列的一半时，新的问当场以 `Busy` 回答，不发出：一个 Actor 一下子发出几万个问，占不满整个分片，别的 Actor 照常收消息。
  - 定长的回复按确切大小留，所以一个 `bool` 的问只留几个字节：链路上能同时在途的问不被 64 KiB 的估计压到几百个。比留的位置还大的回复不占预留，照普通消息排，可能被拒，发问方超时（计数 `daserver.replies.oversize`）（第 11 节）。

---

## 9. 时间与广播

### 时间

**一个时间类型：`TimeSpan`，本进程单调时钟上的时间，下文叫单调时间。**
- 单调时间不是 UTC，不依赖各节点对时。它只在一个进程里有意义。
- **跨进程只传剩余时间**：请求的期限在线上是"还剩多久"；迁移的字节里，延后事件和期限也存剩余时间。接收方用自己的单调时钟重新起算：到期时刻 = 收到时的 `Time.Elapsed` + 剩余时间。
- **停顿不延长请求的预算**（同 ADR-028）：请求在执行之前已经过了期限，就丢弃、计数，不执行，调用方已经按超时处理了。所以框架的期限都算上途中和停顿的时间：
  - 链路记下每个帧进重发窗口的时刻，重放时从请求的剩余时间里减去它在窗口里等的时间；
  - 迁移的字节和热重启交接的字节里带着"已经等了多久"：占位上和调度队列里的邮件、`ReplyTo` 的期限、等待中的请求的期限，都减去途中和停顿的时间；
  - 用户的延后事件不减：那是 Actor 自己的时间，迁移和热重启的停顿不计入（第 10 节）。
- `Time` 是 world 的单例，由运行时写，用户只读。同一个 world 上可以有帧率不同的 Actor 类型，所以运行时在每个 Actor 类型的一趟（`Ordering`、`Update<K>()`、`Settling`）开始时写它：`Tick` 和 `TickRate` 取自这个类型的 `Ordered<K>`（tick 号存在那里，按 Actor 类型数），`DeltaTime` 是这个类型的帧长；`Elapsed` 和 `Utc` 每一项开始时再写（下面）：

  ```csharp
  public record struct Time(long Tick, TimeSpan DeltaTime, int TickRate, TimeSpan Elapsed, DateTime Utc) : ISingletonComponent;
  ```

- **一条时间规则：Actor 里的代码只从 `Time` 读时间。** 先后、延后、超时用 `Elapsed`（单调）；要跨停用、跨进程留下来的绝对时刻（buff 在某日到期、`AskUntilAnswered` 的下一次再问、提醒）用 `Utc`，存进要保存的实体（第 10 节）。
  - `Utc` 来自 world 的 `TimeProvider`（测试里是 DaServer 自己的手动时钟，ADR-071），每一项开始时读一次，同一项里不变；
  - 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`，分析器给警告；
  - 各节点的 UTC 只差对时的误差，所以框架用到 UTC 的地方（存着的 `ReplyTo<T>` 的期限、`Unacked` 里确认之后的留存）多留一个余量 `DaServer:ClockSkew`，排序和期限从不比较两个节点的 UTC。

- **`Elapsed` 是正在执行的那一项的时间**：处理消息时是它进入 world 的时刻，执行延后事件时是它的到期时刻，系统里是这一趟的时刻：有 `[TickRate]` 的 Actor 类型是 tick 时刻 t，没有的是这一趟开始时读的时钟。
- **框架组 `Cleanup` 不属于任何 Actor 类型**：所有 Actor 类型的一趟跑完后，运行时再写一次 `Time` 的 `Utc` 和 `Elapsed`（这一刻的时间），`Tick`、`TickRate`、`DeltaTime` 留着最后一趟的值，清理系统不读它们。
- 所以系统和处理方法读到的 `Tick`、`DeltaTime`、`TickRate` 总是正在跑的这个 Actor 类型的；没有 tick 的 Actor 类型，它们是 0。
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
| 请求的期限 | 等待实体 |
| `WakeAt` 到时（激活着时） | 带 `WakeAt` 的要保存的实体（第 8、10 节） |
| 加入确认期限 | 确认前的 Presence 实体 |
| 断线的宽限期 | 带 `Detached` 的 Presence 实体（第 7 节） |
| 连上就加入的退避 | `Connection` 的一个子实体 |

- **`Due` 是组件，排序是运行时的索引**：每个 world 一个 4 叉最小堆，项是 `(At, Seq, EntityId 和版本)`。加 `Due` 时入堆；实体销毁或事件取消时不去堆里找，堆顶出堆时实体的版本对不上、或者 `(At, Seq)` 不等于实体现在的 `Due` 就跳过（惰性删除；同一个实体上重新挂的 `Due`，例如带 `WakeAt` 的提醒，版本不变，只看版本会提前触发）。多数请求几毫秒就有回信，它的超时项却要等到 `At` 才出堆，所以过期的项（堆的大小减去 `CountOf<Due>`）多过活着的项时，运行时就地压紧一次（去掉过期的，O(n) 重建），堆不超过活着的项的两倍（指标 `daserver.dueheap.entries`）。EasyEcs 的有序值索引是排好序的数组，每取走队首都要搬动整个数组，所以不用它。
- 堆不保存：`world.Load` 和迁移到达之后，运行时按带 `Due` 的实体重建一次。
- `OrderedPhase` 每一步比较三处的队首：即时链（当前项刚 `Schedule` 的，先执行）、到期堆里不晚于 t 的、邮箱快照，执行最早的那个。执行中新加的、时间不晚于 t 的项，下一步就按顺序取到。序号取自单例 `Ordered<K>`。
- **项里不带 `after` 的 `Schedule` 不建实体**：事件是 world 链栈里的一个值，当前项返回后依次执行，两个项之间链栈总是空的，所以它从不进交接或迁移的字节。
- **项外（系统里）的 `Schedule(ev)` 就是 `Schedule(ev, after: TimeSpan.Zero)`**：系统运行时没有正在执行的项，没有即时链可接。事件成为一个带 `Due` 的子实体，到期时刻是 `Time.Elapsed`（系统里是这一趟的时刻：有 tick 的是 tick 时刻 t），在下一个有序阶段执行（这一帧的有序阶段已经跑完），和别的 `Due` 一样进交接和迁移的字节。链栈只在项里用，所以两个项之间仍然是空的。
- 发起者被销毁时，子实体一起销毁，事件不再执行。
- 周期性的逻辑写进系统；稀疏的周期（例如每秒跳一次伤害）在事件处理方法里再 `Schedule(…, after:)` 下一次。
- **每个 Actor 待处理的项有一个上限**：待执行的延后事件和等待中的请求合计（框架在根上数的那个个数，第 6 节），默认 65,536，Actor 类型上可以写 `[ActorOptions(MaxPending = n)]`。超过时 `Ask` 不发出，当场给发问的实体投递一个 `Busy` 的 `Replied`（一个奖励十万个玩家的世界首领问到一半，不会因为抛出而重新加载、丢掉上一份记录之后的改动），`Schedule` 抛出，按抛出的规则处理（第 5 节）：一个失控的循环不会吃光内存。

**`Pending<T>` 是普通数据，事件和请求共用**：`record struct Pending<T>(EntityId Entity, long Seq, CallId Call) where T : struct`：延后的事件和等回信的请求指向那个子实体；即时链里的事件 `Entity` 是空的，按 `Seq` 认；`Call` 只有请求有。
- 还在等吗：`e.IsPending(p)`；取消：`e.Cancel(p)`（`e` 是发起者）。执行、收到回信或取消后 `IsPending` 就是 `false`。
- 存进组件的 `Pending<T>` 不需要清理，也不会卡住：`EntityId` 带版本，执行或取消后永远不再指向活着的实体。
- 多数时候不用存句柄：`e.IsPending<T>()` 问 `e` 上有没有没执行的 T（查 `e` 的子实体和链栈），`e.Cancel<T>()` 取消它们。"正在施法"就是 `me.IsPending<Strike>()`，不另设组件（第 12 节）。

### 广播

- **`ClientGroup`**：一个值，`root.Group(long key)` 得到：键前面带着这个 Actor 的 `ActorId`，所以两张地图的同号格子不会撞。取这个值不读也不写任何数据。
- **订阅是数据**：Presence 上的 `DynamicBuffer<Subscribed>`，`Subscribed(ClientGroup Group)`。
  - `e.SetGroups(groups)` 就是改写这个缓冲，`groups` 是 `ReadOnlySpan<ClientGroup>`（可以 `stackalloc`）；
  - 收尾组的 `SyncSubscriptions`（`[Changed<DynamicBuffer<Subscribed>>]`，以及刚加上 `Joined`、刚被接管的 Presence）把这些 Presence 的完整订阅集合发给网关，网关整个替换（幂等，不用记上次发了什么）。只有带 `Joined` 的 Presence 是成员。迁来的 Presence 也由它订阅（迁移到达算作写入，第 10 节）；
  - `e.Push(in group, msg)` 一次序列化，每个有成员的网关节点收到一帧，由网关扇出。
- **每个组的发送频率是数据**：`root.SetGroupRate(key, everyTicks: n)`，n 是 tick 数，写进根上框架的 `DynamicBuffer<GroupRate>`（`GroupRate(long Key, int Every)`；随迁移和热重启交接的字节走，不进记录）。有 `[TickRate]` 的 Actor 类型的根副本预先带着这个空缓冲，`Join()` 加上 `Presence` 时也带上空的 `DynamicBuffer<Subscribed>`，所以 `SetGroupRate` 和 `SetGroups` 只改写已有的缓冲，不是结构变更（第 5 节的 `INoStructuralChanges`）。一般在 `[On<Activated>]` 里给每个要低频的组设一次（每次激活都会跑，第 12 节的 `Maps.Start`）；最近一次设的生效（从下一个该发的 tick 起），`everyTicks: 1` 或者没设过就是每个 tick。推给这个组的消息照常一次序列化、追加进 `Outbox` 里这个组的池化帧，但这一帧到了组该发的那个 tick 才交给链路，期间的推送合在同一帧里，一条不少；帧到了 `DaServer:GroupFrameMaxBytes`（256 KiB）就提前交出，顺序不变，只是这一次快于设定的频率（十万人的聊天组每 30 tick 一帧也不会攒出一个 16 MB 的帧）。计数器就在这个组的帧缓冲上，不分配。
  - 频率不改变组的身份：订阅和推送都用 `root.Group(key)`，是同一个组。远处的格子、不重要的实体用低频，近处用高频，同 Unity Netcode 的 `MaxSendRate` 和按重要性排的快照（[optimizations](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/optimizations.html)）、SpatialOS 兴趣查询的 frequency。
  - 只对有 `[TickRate]` 的 Actor 类型有意义，别的 Actor 类型调用 `SetGroupRate` 分析器报错。推给同一个客户端的直接推送和低频组的推送之间不保持先后。
  - **成员变化前先交出**：一个组的成员变化（`SyncSubscriptions` 发给网关的订阅集合里加上或去掉这个组，Presence 离开）追加进 `Outbox` 之前，这个组没到点的帧先交给链路。所以网关总是先按旧的成员扇出旧的推送：刚离开的不会漏掉离开前的推送，刚加入的不会收到加入前的推送。
  - Actor 停用、迁出，或者节点热重启冻结之前的最后一个 tick，没到点的组帧当场交出，所以记录、迁移的字节和交接的字节里都没有它。被隔离的 Actor 的组帧直接丢弃：隔离不发出任何东西。
- **整个 Actor 也是一个组**：`root.Everyone`，即 `root.Group(ClientGroup.Everyone)`，键是保留的（`root.Group(ClientGroup.Everyone)` 以外用这个键，常量分析器报错，运行时断言）。网关确认路由、Presence 带上 `Joined` 时（`JoinConfirmed`，不是 `Join()` 当场）运行时把它写进 `Subscribed`，去掉 `Joined`（`Detached`、接管）时拿掉它，`SyncSubscriptions` 随之更新旧网关的订阅；`e.SetGroups` 只换用户的组、留着它，所以带 `Joined` 的 Presence 都是成员。推给整个 Actor 是 `root.Push(root.Everyone, msg)`，走的就是上面那一条路：一次序列化、每个网关一帧，`SetGroupRate(ClientGroup.Everyone, everyTicks: n)` 和"成员变化前先交出"照样适用，线上协议不变（订阅集合里多一项）。
- **小组**（公会、队伍）：一个固定键的 Actor，成员表是它的组件，由它转发。大规模扇出用 `ClientGroup`。

---

## 10. 持久化、迁移、热重启、升级

### 默认：异步、成批地写

处理方法和系统从不等存储：
- 改了要保存的数据，根上标 `Unsaved`（下面的"只写改过的"）。每个 tick 结束时（没有 tick 的 Actor 类型：每批邮件之后），world 把要写的根合成一批，按每 512 行一条语句异步地写下（PostgreSQL 上每条是按数组 `unnest` 的语句，今天的 `PostgreSqlActorStore` 已经这样写，ADR-068）：调用过 `Commit` 的根每次都进（第 8 节），只有普通改动的根最多每个检查点间隔（`DaServer:CheckpointInterval`，默认 30 秒，同今天 `SaveEverySeconds` 的默认）进一次。每一行各自按 epoch 比较并交换（下面的激活 epoch）；一个根同时最多一次写在途。
- **在途的语句有上限**：每个 world 同时最多 `DaServer:Store:WritesInFlight` 条语句在途（默认 2，所以一个节点最多是 world 数的两倍）。一条语句最多 512 行（同今天），也最多 `DaServer:Store:StatementBytes`（8 MiB）；一份记录最多 `DaServer:Store:MaxRecordBytes`（4 MiB），更大的按写不进去处理（下面的"只写改过的"）：PostgreSQL 一个值最多 1 GB，一条语句太大也会被拒，一行的大小不能拖住整批。行按 `(type_id, key)` 排好序：一个旧主人的写和新主人的写同时到，锁的顺序一样，不会互相死锁。名额满了，这一轮要写的根不取，留着 `Unsaved` 和 `WriteNow` 等下一轮。所以存储慢时内存和连接都有界，代价是留住的输出晚得更多（第 8 节的最坏延迟）；等着的根数是指标 `daserver.store.writes_waiting`。
- **每一轮怎么取**：`WriteNow` 的根先取，但每一轮至少留四分之一的行给到了检查点的根（两种都在等的时候），所以提交一直不断时，普通的改动也照样写下去，不会一直被挤在后面。到了检查点的根按"最早没写下的"先取。每个根的检查点时刻错开：第一次在激活后的 `CheckpointInterval × (键的哈希 / 2⁶⁴)` 处，之后每过一个间隔，所以一次大事件之后的百万个根不会挤在同一个 tick 里。最老的还没写下的改动有多久是指标 `daserver.store.oldest_unsaved_age`。
- **写不过来就少收**：一个 world 等着写的根超过 `DaServer:Store:BacklogPause`（4 条语句的行数）时，它暂停从派发队列取新邮件（队列有界，满了请求回 `Busy`），写下去了再接着取。存储慢时压力推回给发送方，而不是在内存里越积越多。
- 优雅地离开先写：停用和优雅停止（缩容、滚动升级、关机）先写记录（第 6 节），迁移带着整棵子树走，热重启把还没写的连同 `Unsaved` 交给新进程（下面）。例外是序列化不了的根：它只留着上一份记录（下面的"只写改过的"）。**优雅停止有预算**（ADR-063，默认 25 秒）：积压排不完的那部分改动，过了预算就丢掉并记日志，不假装"什么也不丢"；所以停止预算（Kubernetes 的 `terminationGracePeriodSeconds`）按 `daserver.store.oldest_unsaved_age` 和积压定，[deployment.md](deployment.md) 第 2 节。
- 宕机丢的是最后一个检查点间隔的普通改动，存储跟不上时是积压排空要的时间，两者取大（第 11 节）；Actor 照常执行，只有调用了 `Commit` 的项发出的东西等包含它们的那次写写成才离开（第 8 节）。贵重的改动用 `Commit`，不丢。

组件类型的集合都用 EasyEcs 的 `ComponentSet`（`reference/componentset.mdx`）：注册时建好一次，存在 `Registration` 里，框架按 `Type` 加入它在注册时才知道的类型（`ComponentSet.With(Type)`）。

### 要保存的是实体，不是组件类型

**保存按实体的种类决定**：从要保存的 prefab 建出的实体是要保存的实体，它带的组件除了 `[NotSaved]` 的都进记录；别的实体不进记录。根要保存，就在 `K.RootPrefab` 上写 `[Persist]`；子实体的 prefab 写 `[Prefab<K>, Persist]`（第 3 节）。

- 同一个组件类型可以既在要保存的实体上，也在不保存的实体上：第 12 节 `Loot` 的 `Stash` 带着 `Prize`、`Position` 保存，地图上看得见的掉落带着同样的 `Prize` 和 `Position` 不保存，Avatar 带着 `Position` 也不保存。按组件类型决定的话，要么 `Stash` 恢复后缺了 `Position`（地图重启后摆不回原处），要么 `Position` 把所有 Avatar 拖进记录。
- 注册时，每个 prefab 的副本（根的、要保存的和不保存的）都带上框架组件 `FromPrefab(int Prefab, int Composition)`：`Prefab` 是这个 prefab 的 id（它全名的 FNV-1a，同 `TypeId`），`Composition` 是它的组件集合的哈希，两个都写在 `wire.manifest` 的 `prefab` 行。要保存的 prefab 的副本还带清理组件 `Persisted(EntityId Root)`，`root.Spawn` 填上 `Root`；根的副本也带它（`Root` 是自己）。所以"这个实体要不要保存、从哪个 prefab、哪个版本来"是数据：系统按 `[With<Persisted>]` 过滤，加载时按 `FromPrefab` 补组件（下面的恢复）。
- **要保存的实体只能建在根或另一个要保存的实体下面**：记录是一棵完整的树。`Spawn` 时父实体不是，就抛出（分析器看得出时报错）；根不保存的 Actor 类型不能有要保存的 prefab（分析器报错）。
- 根上的 `Unacked`（第 8 节，收 `[Once]` 请求的 Actor 类型才有；它的 `OnceSenders` 是自己的存储行，不是组件）、`RootState` 里的 `OnceNext` 和 `AskOrdinal`、`NextDue` 和签发令牌的计数器（第 7 节）是框架的持久化组件；`AskUntilAnswered` 的问是存储里单独的行（第 8 节），不在记录里，它们"下一次再问"的时刻进 `NextDue`。

### 记录

一份记录就是一次 `SaveEntitiesWith`：根放在第一个，然后是带 `Persisted` 的子孙。收集用 `world.Descendants(root).AddTo(ids, includeRoot: true)`（父在子前，返回加进去的个数，列表复用时不分配），再就地去掉不带 `Persisted` 的：要保存的实体只建在要保存的实体下面，剩下的仍是一棵从根开始的树。然后 `world.SaveEntitiesWith(output, CollectionsMarshal.AsSpan(ids), in persist)`：列表按 span 传，不复制。迁移用同一种收集，不去掉（下面的迁移）。

**保存用的组件集合 `persist`** 在 `Registration` 构造时、运行时算一次，只有组件类型，没有单例（EasyEcs 的组保存给它带单例的集合就抛出 `ArgumentException`）：注册的要保存的 prefab（包括根的）上的组件类型，去掉 `[NotSaved]` 的。它只在运行时用（`SaveEntitiesWith` 的集合、加载时补组件、组成哈希），编译期的东西一样也不依赖它（追踪看源码里的声明，下面的"只写改过的"）。生成器算不出它：prefab 是用 EasyEcs 生成的 `SetX` 成员搭的，生成器看不到别的生成器的输出，`new Prefab(BodyPrefab)` 的链和按配置搭的 prefab 更是只有运行时才知道；所以 `Registration` 从 prefab 读它带的组件（EasyEcs 需求 (b)，第 14 节；在它之前，把每个 prefab 在一个临时 world 里实例化一次，读实例的 `Entity.Describe()` 的类型，构造时一次，冷路径），每个 prefab 的组成哈希也这样算。`root.Spawn` 给要保存的 prefab 传进去、prefab 上又没有的组件类型不保存：要存，就在 prefab 上带一个默认值（下面）；再加上框架的 `ChildOf`、`In<K>`、`FromPrefab`、`Persisted`、`Unacked`、`NextDue`、`WakeAt` 和签发令牌的计数器。等待实体和 `Ask` 的状态不在里面：它们不进记录（第 8 节）。之后才加到要保存的实体上的组件，类型在这个集合里才保存：要存，就在 prefab 上带一个默认值。框架只在内存里有意义的组件（`Due`、`Presence`、`Joined`、`Detached`、`Leaving`、`Subscribed`、`GroupRate` 等，[architecture.md 第 2 节](architecture.md#runtime-data)）不在里面。

### 激活 epoch：一个 Actor 只有一个激活能写

- **放置版本**：一个 Actor 类型的放置版本是它的放置最后一次变化时的成员表版本：承载它的成员变化，以及槽表的每次移动（按负载的迁移、粘性类型的钉住，ADR-079）都算；写在成员表的版本行里，所以每个节点算出同样的版本。只在成员变化时加一的话，槽表移走的键在新主人那里永远占不到（同一个版本，主人不同）。节点之间的每个帧捎带发送方见过的最高版本；一个节点从不按比它见过的更旧的版本激活。
- **一张表，一个 Actor 一行，主键是 `(type_id, key)`**：不同 Actor 类型的同号键（`Map(7)` 和 `Loot(7)`）是两行。
  表和索引（[architecture.md 第 4 节](architecture.md#store-statements)）。每一次占 epoch（新插入、从别的主人接过来、不插行的占法）都从一个单调的来源（存储的序列）取新的 epoch，不在旧值上加一，所以 epoch 在整个存储里只增不减，一行被过期清理删掉之后再建，也不会重用任何旧激活拿过的 epoch：一条迟到的旧语句、旧的 `Fence(键, epoch)` 都对不上新行。
  键用 `bytea` 而不是两个 `bigint`：键是不超过 16 字节的任意非托管结构，一列就装得下每种形状，主键和比较都不用补零的第二列；每个字段按大端写，所以一个 `long` 键就是 `int8send(key)`。重构建一张新表 `actor_records`，今天的 `daserver_actors` 表不读也不转换（1.0 之前，破坏性）。槽号由节点按键的哈希算好写进来（和放置同一个哈希），SQL 里不算哈希；`DueScan` 按 `(type_id, slot, next_due)` 的索引只扫自己的 `(类型, 槽号)`。表和索引只由建表的一次性任务建（节点镜像的 `--DaServer:Migrate`，手写的 SQL，在 advisory lock 下，[deployment.md 第 3 节](deployment.md#3-jobs)），节点从不建表：启动时表还不答复，节点就不就绪，按一个退避重试，不设期限（启动探针把等存储算作进展，存储宕机不会让节点循环重启，[deployment.md 第 2 节](deployment.md#probes-both-kinds)）。
- **激活先占 epoch，占和读是一条语句**（键还没有记录时插入一行占位；有行而条件不成立时语句不改也不返回任何行，就是没占到）：
  占 epoch 和读记录的那一条语句见 [architecture.md 第 4 节](architecture.md#store-statements)。
  存储里记着 `(epoch, 写代, 主人, 放置版本)`；每次占 epoch 都把写代归零，新激活的写代从 0 数起（`:gen` 是节点代数，不是写代）。从别的主人手里拿走一个键，必须带严格更新的放置版本：同一个版本里第二个来占的节点被拒绝，按过时视图做出的放置也抢不走新主人；自己在同一个版本里重试（语句超时、连接断开后）照样成功。占不到就按退避重来。
- **被挤掉的主人马上隔离**：占的语句返回了被挤掉的主人（`old.owner` 是另一个还活着的节点代数），本节点给它发控制邮件 `Fence(键, epoch)`；那边的框架系统隔离这个根（第 6 节的表），不用等它下一次写记录失败才发现。
- 只有按自己的视图是主人的节点才去占（第 6 节）；不是主人的转发。
- **每次写记录都按 epoch 和写代比较并交换**（写代是根上数着的、取走的记录数，第 8 节的留住的输出也按它）：
  批量写的语句见 [architecture.md 第 4 节](architecture.md#store-statements)。
  存储里的 epoch 不是自己的，写失败。写代让一条超时之后才落地的旧语句永远盖不掉更新的状态：同一个 epoch 里写代只增不减，热重启原地时也一样（写代在根上，随交接的字节过去，新进程接着数）。
- **同一个根的写从不重叠**：写在途期间（根上框架组件 `RootState` 的 `Writing` 字段）`CheckpointSystem` 不取它的记录；在途期间的改动留着或重新标上 `Unsaved`（下面的"只写改过的"），进下一次写，它带着最新的状态，后写的盖掉先写的。**写出错先分清是哪种**：临时的错误（语句超时、连接断开、`40001` 序列化冲突、`40P01` 死锁、`53xxx` 资源不足、`57P0x` 关停）用同样的字节和写代重试，超时的那条先取消（Npgsql 的取消发 PostgreSQL 的取消请求，等语句结束；同占 epoch 超时）再重试，不让一条迟到的语句占着池里的连接；存储每次都会拒绝的错误（值太大、约束、数据错误）重试也没用，这条语句对半拆开再写，直到只剩出错的那一行，那一行按下面的"写不进去"处理，其余的照常写成，所以一个坏的根拖不住整个 world 的写。重试时，没更新的行由语句的 `SELECT` 返回存储里的 `(epoch, 写代)`（它读的是语句开始时的快照，这些行本来就没被这条语句改）：epoch 是自己的、写代正是这一次的，说明先前超时的那次已经写成，算成功（同一个 epoch 里一个写代只用于一份字节）；epoch 不是自己的，比较失败。
- **比较失败就隔离**（epoch 不是自己的，第 6 节的表）：不写、不进 `Leaving`，销毁整棵子树。Presence 的路由由清理组件释放，客户端 SDK 重新加入，到达真正的主人那里。
- **节点被宣布 `Dead` 时**，它读到就隔离自己所有的 Actor 并退出，新进程以新的节点代数加入（第 2 节）。重新放置至少晚一个 `TableRefresh`，所以连得到存储的死节点那时已经停下（第 2 节）；分区或长停顿之后醒来、还没读到表的节点，epoch 比较拦住它的写。
- `[Stateless]`、`Connection` 和根不保存的 Actor 类型不占 epoch：它们不写记录，存储里也没有它们的行。一个键只有一个激活由视图保证：只有按自己读到的成员表是主人、键也不在交接中的节点才激活，别的转发；分区里被宣布 `Dead` 的一侧读到就停下，`IsolationTimeout` 之内读不成表的节点冻结，重新放置要等存储的时钟过了 `dead_seen_at + IsolationTimeout + DeathMargin`。两个视图短暂不一致（槽表的变化还在传播）时，同一个键可能短暂有两个激活：都不写任何东西，代价是邮件被分到两边，帧带着见过的最高放置版本，一次视图交换之内就收敛（[architecture.md 第 4 节](architecture.md#4-placement-activation-persistence)）。
- **从没写成的行会过期**：记录还是空的行，最后一次占 epoch 之后过了 `DaServer:EmptyRecordTtl`（默认 1 天）就删掉。要保存的 Actor 激活之后一个检查点间隔之内就写下第一份记录（新键标了 `Unsaved`），所以这样的行属于被拒绝或者写之前就宕了的激活。承载要保存的 Actor 类型的 `Active` 成员里最老的那个，它的第一个 world 每 `DaServer:EmptyRecordSweepInterval`（1 小时）分批清一次（不交给协调者：协调者可能是一个不承载要保存类型的网关），经一个只删这种行的 `SECURITY DEFINER` 函数，运行时的数据库角色没有 `DELETE` 权限（[architecture.md 第 4 节](architecture.md#store-statements)）。

### 恢复

- 记录头里存着实体数 n。
- `into[0]` 是占位，它就是根（第 6 节）；其余 n−1 个是空实体。然后一次 `LoadEntitiesInto(record, into, markWritten: false)`，再给根加上 `In<K>`、去掉 `Loading`。
- 同一次加载里，`ChildOf` 和 `EntityId` 会改写成新的 id；指向这组以外的 `EntityId` 变成 `Null`。
- 子孙没有 prefab 默认值，需要的组件都要在记录里，包括 `In<K>`（它是 tag，不占列）。所以 `persist` 集合带上 `In<K>`。
- **恢复用 `markWritten: false`，迁移到达不用**，只在这里写一次：
  - 恢复：读进来的值不算变更，被它覆盖的 prefab 默认值也不算（EasyEcs 4.5），所以激活不会白写一次记录，`ChangeSystem` 不用重置任何基准；
  - 迁移到达用默认的 `LoadEntities`（有占位时 `LoadEntitiesInto`），`markWritten` 为 true：追踪变更的组件都算写入。所以 `[Changed<T>]` 的系统都把迁来的实体当作刚写过：`SyncSubscriptions` 按 `Subscribed` 重新订阅网关（第 9 节），`CellAssign` 重算格子，到达后的第一个检查点在新的 epoch 下写一次记录。
- **加载时按 prefab 补上，一条规则管记录、迁移的字节和热重启交接的字节**：加载之后，运行时按每个实体的 `FromPrefab` 找到注册的 prefab 副本，补上两种它没有的组件，值是副本的默认值：
  - 副本上的 `[NotSaved]` 组件：`[NotSaved]` 的意思就是"到达时取 prefab 的默认值"（记录和迁移的字节里本来没有它，热重启交接的字节里有，就不补）；
  - 这个 prefab 在实体的 `Composition` 之后新加的组件（新版本给 prefab 加的）。运行时在 Actor 里删掉的组件不算，不补回来：`wire.manifest` 留着每个 prefab 以前的组成（哈希和组件列表），`Registration` 据此算出"新加的"是哪些。

  然后把实体的 `Composition` 改成现在的。补了要保存的组件时给根加上 `Unsaved`：这些默认值是新数据，下一次检查点存下来。不认识的 prefab id 或者组成哈希不补，计数。没有记录（新键）时也给根加上 `Unsaved`：占位上的默认值是建占位时写的，那时没有系统看得到它，`[Changed<T>]` 不会替它补上。
- 记录里有、这个版本不认识的 `TypeId`（例如回滚到旧版本之后）跳过并计数，加载照常完成；下一次写记录就不再有它。所以回滚会丢掉新版本的字段和类型，滚动升级的检查拦着这种发布（下面的滚动升级）。

### 只写改过的

- **要保存的组件类型在源码里写 `ITracked`**：EasyEcs 按组件类型追踪写入（每行一个 8 字节的变更戳列，写时一次存储），不按实体，而它的变更过滤 `ChangedAny<T1..Tn>` 要在编译期写出类型（`where T : ITracked`，4.7 没有非泛型的形式，运行时拼要 `MakeGenericMethod`，过不了 Native AOT）。所以追踪只有一个来源，就是源码里的声明：
  - 用户在要保存的组件类型的声明上写 `ITracked`（第 12 节的 `Grid`、`NetIds`、`Inventory`、`Wallet`、`Stash`、`Prize`、`Position`），不用 `partial`；系统要对写入反应的类型也写（第 12 节的 `Cell`、`Velocity`：不保存，只为 `[Changed<T>]`）。`DynamicBuffer<T>` 本身就是 `ITracked`（EasyEcs 4.7），所以背包的 `Slot` 这样的缓冲元素什么也不用写；服务端工程的生成器把编译里出现的、元素类型声明在服务端工程里的每个 `DynamicBuffer<T>`（参数、`SetBuffer<T>` 调用）放进追踪类型表，元素标了 `[NotSaved]` 的除外；元素类型是 DaServer 包的缓冲由那个包自己的钩子放进去（下面；例如 `Subscribed` 不进记录，它的包就不放），服务端工程的生成器不重复放；元素类型声明在别的程序集里的缓冲不进表（要保存时同 DAS029 的道理：把元素类型挪进服务端工程）。生成器不补这个接口。
  - 生成器从服务端工程的编译里所有写了 `ITracked` 的组件类型，生成 `ChangeSystem` 的 `ChangedAny`/`RemovedAny` 过滤和 `Registration` 里的追踪类型表（下面）。DaServer 的包各自的生成器写出自己的那一份（`DaServer.Topics` 的 `DynamicBuffer<Subscriber>`、`DynamicBuffer<Subscription>`，`DaServer.Reminders` 的 `Reminder<T>`），接法是固定的：每个包的生成器在服务端工程的编译里写出一个固定名字的生成成员，`internal static Registration <包的命名空间>.Generated.PackageHooks.Register(Registration registration)`（追踪类型、prefab、系统都在里面，和生成的 `Registration` 一样是在同一个实例上的链式调用，返回它），服务端工程的生成器看到引用了哪些 DaServer 包，就在生成的 `Registration.Create()` 的链里逐个调用它们。包的生成器**总是**写出这个成员，没什么要注册时也写一个直接返回的空成员，所以引用了包就一定调用得到（测试 `PackageHookEmittedWhenEmpty`）；每次 `ChangedAny` 调用都并进同一组，所以几份过滤拼起来就是一个过滤。泛型的 `Reminder<T>` 写不进 `ChangedAny<…>`，所以 Reminders 的生成器按编译里每个 `Remind<T>`/`[On<Reminded<T>>]` 的 T 写出封闭的 `Reminder<T>`。收要去重的请求的 Actor 类型（第 8 节），服务端工程的生成器把框架的 `DynamicBuffer<Unacked>` 放进自己的追踪类型表。**过滤只放会进记录的类型**：标了 `[NotSaved]` 的类型，和元素标了 `[NotSaved]` 的 `DynamicBuffer<T>`（例如第 12 节地图根上每 tick 都写、tick 之间是空的 `Shout`），不放进去；否则它们每 tick 的写会给每个根标 `Unsaved`，每张地图每个检查点都重写一次（测试 `NotSavedBufferWriteDoesNotTagUnsaved`）。写了却从不出现在要保存的实体上的类型（例如只在 Avatar 上的组件）只多付变更戳，`ChangeSystem` 按 `With<Persisted>` 不访问它们。组件都写在服务端工程里（DAS013 已退役）：别的、不是 DaServer 包的引用程序集里写了 `ITracked` 的组件类型不进任何一份追踪表，分析器报错（DAS029）；生成器的增量管线按引用的元数据身份缓存，改一行代码不重新扫描引用。
  - **`Registration` 构造时检查**：`persist` 里的每个用户组件类型和 `DynamicBuffer<T>`（`[NotSaved]` 的除外）都在追踪类型表里，否则抛出（只在按配置搭的 prefab 上出现的缓冲，生成器看不到，就在这里发现），异常列出缺 `ITracked` 的类型和带着它的 prefab。这是启动时的错误，不是第一次写记录时才发现（测试 `PersistTypeWithoutTrackedFailsRegistration`）。分析器看得出的地方（`[Persist]` 的 prefab 用字面的 `SetX`、`SetBuffer<T>` 链搭在同一个编译里）提前报（DAS027，组件和缓冲都报：缓冲元素在别的、不是 DaServer 包的程序集里时，生成器看不到它）。
  - 追踪写在源码里，EasyEcs 的生成器和分析器看到的都一样：`Maybe<T>` 列的写入照常打戳，生成的 `entity.SetXIfChanged(…)`/`WriteX` 也照常有。
  - **读和写各有各的写法**：对追踪的类型，EasyEcs 生成的 `entity.X` 属性是 `ref readonly`（它的 `ComponentGenerator`：被 world 关注的类型，即追踪、有索引或分组的，给只读的 `Read`），经它读从不算写入。写走 `SetX`、`WriteX()`（追踪、不带索引也不分组的类型才有）、`SetXIfChanged`、`Mut<T>` 或 `ref` 参数，每种都打戳。生成的 `TryGetX(out copy)` 和 `TryGet<T>(out copy)` 按值复制，从不算写入（EasyEcs `api.mdx` 的 entity 表），所以可能没有的组件照常用它读（第 12 节的 `stash.TryGetPrize`、`e.TryGetCell`）。
- **结构变更也是变更**：加上一个被追踪的组件算一次写入，去掉它记下一次移除（EasyEcs `reference/itrackchanges.mdx`、`guide/change-tracking.mdx`），所以下面的 `ChangedAny`/`RemovedAny` 看得到它们。写了 `ITracked` 的 tag 也这样，由 W1 核对；核对通过，tag 的增删由 `ChangeSystem` 照常看到，什么也不用另做。核对之前（核对不过就一直这样），限制按实体，不按类型：对 `persist` 里的 tag `Set`/`Remove`，只在分析器看得出这个实体要保存时报错（例如 `root.Spawn` 一个要保存的 prefab 返回的实体、带 `[With<Persisted>]` 的方法）；看不出的地方照常写，运行时在要保存的实体上发现 tag 变了就给根标 `Unsaved`（做法在 W1）。同一个 tag 在不保存的实体上随便增删（第 12 节的 `Visible`）。
- **`root.Spawn` 建出要保存的实体时，当场给根标 `Unsaved`**，不靠变更戳。
- `ChangeSystem` 用注册时建好的两个过滤：生成器写出的追踪类型的 `ChangedAny` 加 `With<Persisted>()`，和对应的 `RemovedAny`。EasyEcs 的 `ChangedAny<T1..Tn>(since)`（`QueryFilter` 的实例方法）一次最多 9 个类型，一个过滤里的每次调用并进同一组，所以生成的 `Registration` 把追踪类型分成每组 2 到 9 个、大小均衡的几组，每组调用一次（12 个分成 6 + 6：均衡只是为了每次调用的开销差不多）。只有一个类型的一份（一个包只追踪一个类型，或者用户的工程只有一个）写成 `ChangedAny<T, T>` 和 `RemovedAny<T, T>`，同 EasyEcs 自己的生成器，从不写 `Changed<T>`/`Removed<T>`：过滤里的 `Changed<T>` 是"并且"，和别的份并在一起时会要求这个类型也写过，`ChangedAny` 的各次调用才是"任何一个"（测试 `SingleTrackedTypeComposesWithPackageSets`）。第 12 节的样例有十一个用户的追踪类型，生成两次调用，六个和五个：`QueryFilter.Create().ChangedAny<Grid, NetIds, Inventory, Wallet, Stash, Prize>(0).ChangedAny<Position, Cell, Velocity, Granting, DynamicBuffer<Slot>>(0).With<Persisted>()`（`Shout` 是 `[NotSaved]`，不在里面）。框架自己的持久化类型（`RootState`、`DynamicBuffer<Unacked>`、`WakeAt`）在运行时自己的过滤里，不在这张表里；`OnceSenders` 是自己的存储行（第 8 节），不经这个过滤，每次运行用 `Since(上次运行的 tick)` 换起点。要保存的实体上任何一个组件被写或者被删，这一行访问一次，按 `Persisted.Root` 给根标 `Unsaved`。一行改了几个组件也只访问一次；不保存的实体上同类型的写只多付变更戳，`ChangeSystem` 不访问它们。不分配。
- **占位不写**：根带着 `Loading`、`Saving` 或 `ActivationFailed` 时，`ChangeSystem` 不给它标 `Unsaved`，`CheckpointSystem` 也不写它（`[Without<Loading>, Without<Saving>, Without<ActivationFailed>]`）。否则 `Reload` 把根改回 prefab 默认值的那些写，会被当成改动写进记录，盖掉最后一份好记录（第 5 节）。
- **记录在 pass 外面取**：EasyEcs 的 `World.Save`（`SaveEntitiesWith` 经它走）在 pass 进行中抛出（`ThrowIfIterating`），而 `[ForEach]` 方法和不带 `INoStructuralChanges` 的 `entity.Run` 都在 pass 里；带 `INoStructuralChanges` 的 job 冻住了结构闸门，`Save` 要进结构区，同样抛出。所以任何 job 和任何 `[ForEach]` 方法里都不调用 `SaveEntitiesWith`。所以 `CheckpointSystem` 在 pass 里只收集要写的根的 id、改根上的标记，取记录（`Descendants`、`SaveEntitiesWith`）由每个 world 的 `StoreWrite` 步骤在 `world.Update` 之后做（[architecture.md 第 2 节](architecture.md#the-shard-loop)的循环）；停用、停止和迁移的那次 `SaveEntitiesWith` 也一样，框架的 job 只做标记（W1 测试 `SaveEntitiesOutsidePassOnly`）。
- `CheckpointSystem` 在每个 tick 末（没有 tick 的 Actor 类型：每批邮件之后）挑出要写的根，`StoreWrite` 把它们各 `SaveEntitiesWith` 一次，合成这个 world 的一次批量写（上面的"默认"），按 epoch 比较并交换：调用过 `Commit` 的根（`RootState` 的 `WriteNow` 字段，由 `Commit` 置上，第 8 节）每次都取，其余带 `Unsaved` 的根离上次写过了检查点间隔才取；在途的语句满了就都等下一轮（上面的"默认"）。**取记录时去掉 `Unsaved` 和 `WriteNow`**，写出错就加回（用同样的字节重试）；写在途期间的改动重新标上它，进下一次写。根上的 `NextDue` 同时写进记录的 `next_due` 列（下面的唤醒）。
- **一个根的状态写不进去**（`SaveEntitiesWith` 抛出：环、没注册的子类型；记录超过 `MaxRecordBytes`；或者存储每次都拒绝它那一行，上面）：跳过这一行，记日志 `RecordUnwritable`（按次数限流），这一批的其余行照常写。存储里留着它上一份记录；根重新标上 `Unsaved`（不标 `WriteNow`），过一个检查点间隔再试，它留住的输出等到某一次写成才放出（迁出和热重启的冻结不等，下一条）。
- **停用、停止和迁移也是先 `SaveEntitiesWith`，再销毁子树**。它抛出时：空闲停用取消，去掉 `Deactivating`，Actor 照常运行（已经离开的 Presence 不回来），根重新标上 `Unsaved`，按上一条再试；强制停用（`root.Deactivate()`、经记录的热重启、迁移退回的停用）和优雅停止放弃这次写，存储里留着上一份记录，记日志 `RecordUnwritable`，照常销毁，留住的输出随之丢弃；迁移退回到强制停用（放置已经指向目标，Actor 不能留在源节点上），同样放弃这次写、留着上一份记录，目标按需从它激活。原地热重启的冻结也一样不因此放弃：交接时 `world.Save` 抛出，框架逐根 `SaveEntitiesWith` 找出抛出的根，像隔离一样从冻结的 world 里销毁它们的子树（不写记录；Presence 留下的清理组件随交接的字节过去，新进程释放它们的路由），记日志 `RecordUnwritable`，再 `world.Save` 一次；这些 Actor 在新进程里按需从上一份记录激活，没写下的改动丢掉。迁出和冻结为放出留住的输出先写的那一次（第 8 节）也一样：`SaveEntitiesWith` 抛出的根当场丢弃留住的输出，不等一次永远写不成的写，迁出退回到强制停用，冻结从冻结的 world 里销毁它的子树、留在交接的字节外面。
- **`Unsaved` 是根上的 tag，这是有意的**，虽然 EasyEcs 的 `ecs-principles.mdx` 第 5 条说变更检测用变更戳和 `[Changed<T>]`，不用加了又删的标记：
  - 要回答的问题是"这个 Actor 的记录要不要写"，答案属于根：子孙的变更（写、删、销毁）都要汇总到根上，记录也是按根整份写的。`[Changed<T>]` 只能回答"这一行这一趟写过没有"，回答不了"这棵树上次写成之后改过没有"；
  - 它要活过多个 tick：写记录出错时加回，直到某一次写成功，所以不能每 tick 从变更戳重新推出来；
  - `CheckpointSystem` 用 `[With<Unsaved>]` 只看这些根，没改过的 Actor 不花任何开销。
- 要保存的子孙被销毁（例如 `Stash` 被领走）时，EasyEcs 的移除检测不报告销毁的实体，所以靠清理组件：框架组 `Cleanup` 里的清理系统（`[With<Retired>, With<Persisted>]`，不带 `In<K>`，第 6 节）按 `Root` 给根标 `Unsaved`，然后移除自己。所以删除也会写进记录。框架自己销毁整棵子树时先移除 `Persisted`，不触发它（第 6 节的表）。
- 加载的实体不算写入，`Persisted` 在 `persist` 集合里，随记录一起恢复，`Root` 由加载改写成新的 id。

### 唤醒：`WakeAt`、`NextDue`、`DueScan`

没激活的 Actor 也要在某个 UTC 时刻醒来：`AskUntilAnswered` 的请求要再问（第 8 节），`DaServer.Reminders` 的提醒要触发（第 15 节）。这是运行时的事，扩展能力只写数据（ADR-045），用户不直接写它：
- 要叫醒 Actor 的实体带持久化的框架组件 `WakeAt(DateTime Utc)`：提醒上是它下一次触发的时刻（第 15 节），删除中的根上是删除的检查（第 5 节）。它们只写 `WakeAt`；`AskUntilAnswered` 的问不是实体，它们的"下一次再问"直接进 `NextDue`，到时由运行时再问（第 8 节）。
- 运行时的 `WakeSystem` 按 `[Changed<WakeAt>]`、`[Removed<WakeAt>]`（两个方法；`ChangedAny` 要 2 到 9 个类型，一个类型就是 `Changed`；只按 `[Removed<T>]` 过滤的方法取一个用不到的组件参数 `in Persisted`，因为 EasyEcs 4.7 的 EECS003 只接受只带 `[With<T>]` 或 `[Changed<T>]` 的零组件 `[ForEach]`，EasyEcs 需求 (e)；Topics 包对 `[Removed<Deactivating>]` 的反应同样）和 `Persisted` 的清理找出受影响的根，只对这些根沿 `world.Descendants(root)` 重算最小的 `WakeAt`，写进根上持久化的 `NextDue`；值变了就给根标 `Unsaved`（`NextDue` 是运行时的，不在生成的 `ChangeSystem` 过滤里），所以前移的时刻一定随下一个检查点写下。只有运行时算它，用户和扩展能力不写。
- **激活着时，`WakeAt` 由一个系统换成 `Due`**：运行时的 `ArmSystem` 有三个方法：`[ForEach, With<WakeAt>, Without<Due>]` 给刚从记录加载、刚迁来的实体挂上 `Due = Time.Elapsed + (WakeAt.Utc − Time.Utc)`（已过的就是 0）；`[ForEach, Changed<WakeAt>]` 给改写了 `WakeAt` 的实体重挂（改写 `Due` 的 `(At, Seq)`，堆里旧的那一项按惰性删除跳过，第 9 节），所以把下一次再问挪后或挪前都按新的时刻；`[ForEach, Removed<WakeAt>]`（取 `in Due` 参数：EECS003 不接受只按 `[Removed<T>]` 过滤的零组件方法）摘掉它的 `Due`，所以取消了的提醒不会再被叫醒。之后这件事只看单调的 `Due`，NTP 的跳变不再挪动它。带 `WakeAt` 的实体上的 `Due` 只属于它：同一个实体上不会有别的 `Due`（延后事件和等待都是子实体）。
- **到时**：这个 `Due` 是有序阶段里的一项，交给登记了这个组件的唤醒处理，在这一项里直接做事：`DaServer.Reminders` 投递提醒、前进周期（第 15 节）；删除中的根重新检查能不能删（第 5 节）。问的行到了"下一次再问"时，由运行时再问一次、写下下一次的时刻（第 8 节）；宕机落在两次写之间时，存储里还是旧的时刻，下次激活又到时：再问是同一个序号，无害。所以再问和提醒没有各自的挂载系统，重新激活之后也不另扫一轮，用户也不写叫醒之后的系统。
- **先算 `NextDue`，再取记录**：`WakeSystem` 在框架组 `Cleanup` 里排在 `CheckpointSystem` 之前，所以写下的 `next_due` 总算上了这个 tick 新写的 `WakeAt`。
- 检查点把 `NextDue` 写进记录的 `next_due` 列（没有就是空）。每个节点的 `DueScan` 每隔 `DaServer:DueScanInterval` 向存储查自己是主人的槽里 `next_due` 已到的键（`IActorStore.DueKeys`，按 `(type_id, slot, next_due)` 的索引分页；分页的游标和语句见 [architecture.md 第 4 节](architecture.md#store-statements)），给还没激活的发控制邮件 `Wake`，Actor 因此激活。
  注册里有写 `WakeAt` 的 Actor 类型（生成器看得出：`AskUntilAnswered` 的调用，或者 `DaServer.Reminders`）时才查。查询不排除激活着的键（存储不知道哪些在内存里）：激活着的 Actor 的 `WakeAt` 在将来或刚过（它的时刻一到就被去掉或前移），只在刚过、改它的那次写还没写成时被查到，`Wake` 到了 world 就丢弃。所以一次扫描多出的行不超过最近一个检查点间隔里到时的项。
- **由 `Wake` 激活的 Actor 总是重写 `next_due`**：激活给根标 `Unsaved`，下一个检查点写下现在的 `NextDue`。否则一个过时的 `next_due` 会让 `DueScan` 每一轮都叫醒它。

### 序列化规则（Nino）

| 数据 | 要求 |
|---|---|
| 进记录的组件（`persist` 里的用户组件类型） | `[NinoType]`：改名用 `[NinoFormerName]`，加字段用 `VersionTolerant` 或 `Keyed` 格式；或者非托管、布局冻结：按原始字节写，布局哈希记在 `wire.manifest`，改了布局构建报错（DAS017），要改先改成 `[NinoType]`。布局冻结的是类型，所以它用到的每个地方都一样冻结（第 12 节 Avatar 上的 `Position` 也是）。托管的必须是 `[NinoType]`（分析器检查） |
| 其他非托管的组件、本地事件、`Ask` 的状态 | 迁移和热重启时按原始字节搬运，不用标：`wire.manifest` 里每个这样的类型都有布局哈希，两边对不上就不搬字节（下面的迁移、热重启的路径） |
| 托管的组件、事件、状态 | 要有 Nino 格式化器（`[NinoType]`，分析器检查） |

这是唯一的一条序列化规则，别处只引用它。记录里的类型按 `TypeId` 存。

### 迁移

- 保存整棵子树（`world.Descendants(root).AddTo(ids, includeRoot: true)`，同上面的记录），组件集合是注册时建好的 `migrate = ComponentSet.Create().With(typeof(每个不带 [NotSaved] 的组件))`：默认都带，标了 `[NotSaved]` 的除外；只有组件类型（`With(Type)` 也收单例，组保存遇到单例就抛出，所以生成器只传组件）；到达时按 prefab 补上它们（上面的恢复）。源节点先等这个根在途的写和 `Commit` 留住的输出放出（第 8 节；最后一次写发出之后，这个根的新邮件停在它上面，墓碑建好后随字节转过去）；这个根的 `SaveEntitiesWith` 抛出时不等，丢弃留住的输出，退回到强制停用（上面的"只写改过的"）。
- 等待中的请求、Presence 实体（连同 `Subscribed`）、待执行的延后事件随 Actor 一起迁移。
  - `Due` 在字节里是剩余时间，到达后按新 world 的单调时钟重新起算，再按它的 tick 边界归入 tick 窗口；已经过期的在第一个 tick 执行（第 9 节）。`Due.Seq` 到达时用新 world 的序号重新编号，保持它们之间原来的先后，然后重建到期堆。
- **到达时占一个新的 epoch**：从字节里带来的 epoch `e` 比较并加一（同一条 `UPDATE … RETURNING`，不读记录），主人改成本节点，放置版本写成 `GREATEST(存储里的, 本节点见过的这个类型的版本)`（放置版本只增不减，一个落后的视图不会把它改小），写代归零（根上的写代也从 0 数起）；条件是 `epoch = e OR (epoch = e + 1 AND owner = (本节点名, 代数))`，所以这条语句重试时、或者到达后本节点热重启再来一次时，照样成功。占到之前，到达的子树像占位一样不运行，邮件排队；比较失败（别人已经占了）就隔离这份到达。所以迁移之后，源节点的任何迟到的写都会失败。
- **迁移头里带着每个组件的布局哈希和每个 prefab 的组成哈希**（来自 `wire.manifest`）。组成哈希不同照样搬字节，到达时按 prefab 补上（上面的恢复）；目标节点的布局有一个对不上（滚动升级时新旧版本混跑），就不搬字节：源节点改为停用、写记录，目标节点从版本兼容的记录加载。这时只有记录里的留下；Presence 丢失，由 SDK 重新加入。
- 源节点销毁子树前先移除 `Presence` 和 `Persisted`：路由不释放，根也不标 `Unsaved`（第 6 节的表）。
- **墓碑**：源节点在迁移后保留一段时间的 `(键 → 新节点)`，这个键的所有邮件都按原来的顺序转过去（回信和普通消息都是）；墓碑还在时本节点不激活这个键。墓碑指向的节点已经离开集群时，按当前的放置转发。
- 停用写记录时不保存待执行的延后事件：需要跨停用保留的状态，放在要保存的实体上（`DaServer.Reminders` 的提醒就是）。
- 到达后的第一条邮件是 `Activated(Activation.Migrated)`。
- 跨 world 也走字节，因为 `MoveEntities` 要求两个 world 在同一个线程。
- 迁移期间尽力保序，不设栅栏。
- 重新平衡引起的迁移限速：每个节点同时迁出的 Actor 有上限，节点增减时不会一下子搬空。按负载的迁移不选邮箱正热的 Actor（最近一秒的邮件超过 `MaxMail` 的一半）：每秒十万条消息的 Actor 一迁移，`Arriving` 的占位马上就满，迁移本身成了丢消息的原因；它留在原处，等热度下去再挪。

### Native AOT

节点可以用 `PublishAot=true` 发布成原生程序（ADR-083），也可以照常 JIT 运行。两者行为相同：都不在运行时加载程序集，改代码都走热重启或滚动升级（下面）。

- **生成的注册是封闭类型，不用反射**：`Registration.Create()`（C# 14 的静态扩展成员，第 12 节）在用户的程序集里写出每个封闭的类型（`.Actor<Map>()`、分发表、job、`world.AddSystem<…>()`），框架按 Actor 类型的系统也由生成的代码写出封闭的实例。没有反射、`MakeGenericType`、`Activator`、程序集扫描，也没有模块初始化器。
- 框架的泛型系统不写 `[ForEach]` 方法，在 `OnUpdate` 里用 EasyEcs 的泛型入口跑 job，所以 EasyEcs 不需要在运行时用 `MakeGenericType` 封闭它们的代码（EasyEcs D20：只由别的程序集封闭的泛型系统才走这条路，AOT 下不行）。用户的泛型 `[ForEach]` 系统只在别的程序集里封闭时，DaServer 的分析器报错（第 13 节）：EasyEcs 对这条路不报警告，错了只会在运行时失败。
- 序列化都是 Nino 生成的格式化器；HTTP 平面和它的 JSON 规则见 [architecture.md 第 8 节](architecture.md#8-native-aot)（ADR-083）。
- 发布要求 0 警告，不接受压掉任何警告；哪些已经核实、哪些还要验证，只写在 [architecture.md 第 8 节](architecture.md#8-native-aot)。

### `wire.manifest`

- 每个工程写出自己的一段。写它的是进程外的 `DaServer.WireTool`（随生成器的包一起发，构建时 `dotnet exec`；不做成进程内的 MSBuild 任务：Visual Studio 的 MSBuild 是 .NET Framework，任务要另编一份，还会把 `Microsoft.CodeAnalysis` 装进 MSBuild 的节点，和它自己的版本冲突）。它读构建出的程序集；prefab 的组成只有运行时才知道（上面的"记录"），所以它在自己的进程里建一次 `Registration`，从里面读出每个 prefab 的组件。
- Server 的一段：Actor 类型、承载它的服务器类型、帧率、身份、连上就加入的 Actor 类型、每条消息的接收方、发送方、回复类型、是否 `latest`、事件、状态类型、配置表（表名和表类型的格式哈希）、prefab（它的 id、组成哈希和要不要保存；组成变过的，以前的组成也留着：哈希和组件列表，加载时据此补组件）、进记录的组件类型（`persist` 的全部用户类型，写法 `nino`、`raw` 或 `tag`）、每个组件的布局哈希、推送；客户端消息的路由（带 `[To]` 的写明参数）、事件和 `Replied` 的处理、`Activated` 的处理、对 tag（`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）的消费。
- 热重启按它比对新旧版本，选走哪条路（下面）；prefab 的组成和表的格式也在比对里。
- `actor` 行还记着哪些客户端消息能建键（`activated-by`）、有没有 `CanCreate`：去掉或加上它们改变了客户端能激活什么，比对把它当协议的改变。
- AppHost 只读 `actor` 和 `hosted-by`。

### 滚动升级

- 旧版本节点收到不认识的消息，返回 `NotHandled`。调用方在重新解析放置之后可以重试一次：放置可能已经换到新版本的节点。
- 旧版本的网关收到不认识 `TypeId` 的客户端消息，回 `NotHandled`；SDK 在握手里带上自己的 `wire.manifest` 哈希，收到 `NotHandled` 时向应用报告版本不一致，而不是一个普通的失败。
- **回滚要安全**：`{cluster}-upgrade-check`（[deployment.md 第 3 节](deployment.md#3-jobs)）还检查回滚：新版本会往记录里写旧版本保留不了的字段，或者新的记录类型（旧版本读进来时跳过，再写回就丢掉，上面的恢复），就拒绝，除非强制。
- 有状态 Actor 的节点组（例如地图）先滚完，协调者才恢复按负载的迁移：混跑期间的迁移只能退回到停用加记录。
- `[Hosts]` 写在成员卡片里，新旧版本混跑时，放置结果一致。
- 节点代数在加入时取、写进成员表（第 2 节），所以同名的节点重启后，不会被当成原来那个。
- 新旧版本的帧率可以不同：延后事件跨进程时存的是剩余时间。
- 组件布局不同的节点之间，迁移退回到停用加记录（上面的迁移）。

### 热重启

改代码就换二进制：节点在原地换成新版本的进程，Actor 不重新激活，客户端不断线。JIT 和 Native AOT、每个平台都只有一个做法：**新进程先起好，旧进程把它的 world 经本机的管道流过去，连同监听的套接字，然后退出**（ADR-084、ADR-098）。没有快照文件，也不在进程里重新加载程序集。热重启的做法只写在这一节。

- **每个节点是一对实例**：两个都装好、都启用的实例共用节点名和一个本机目录（`DaServer:HandoverPath`，只放一把锁、一个管道和一个武装文件，几 KB）；每个实例从 `DaServer:Instance` 知道自己的实例名（systemd 的 `%i`，其余平台由 `DaServer.Aspire` 或安装的定义设上）。**谁是节点在运行时决定，不靠配置**：拿到 `<目录>/node.lock` 排他锁（Unix 上 `flock`，Windows 上独占的文件句柄；进程不管怎么退出，内核都会放掉它）的进程就是节点，照常运行。拿不到锁的实例看目录里的武装文件：节点武装着、武装的 `wire.manifest` 哈希等于自己的，它就是**热备**；否则它**停着**：放掉打开的一切，只留下进程（几十 MB），等锁；它照样在管道上监听，拒绝每一次握手，只接受"退出"这一句（Kubernetes 上的同镜像滚动用它）。节点进程死了（崩溃、被杀），锁被内核放掉，停着的实例拿到它，走普通的启动（从记录激活），以新的代数加入。
  - 平时没有热备：热备只在热重启时由触发器启动（下面）。**交接之后旧进程不退出**：它放掉锁、交出套接字、销毁它的 world，做一次压缩的完整回收（`GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)`，把释放的内存还给系统），然后原地停着，就是这一对里停着的那个；下一次热重启时，触发器重启的就是它。所以交接从不靠监管者把退出的进程拉起来：Kubernetes 上容器一直在跑，Pod 不因为它掉出就绪，也碰不到 kubelet 的重启退避。
- **武装只做准备**：运维命令 `{cluster}-restart`（第 2 节）带上新版本的 `wire.manifest`，经节点的管理端点武装节点。节点比对新旧两份（唯一的一份比对，[architecture.md 第 5 节](architecture.md#5-migration-upgrades-hot-restart)），选一条路，在命令的结果里报告；带 `dryRun=true` 只回答路径、不武装，升级检查用的就是它；节点把这次武装（随机数、武装的 `wire.manifest` 哈希、期限）写进目录里的武装文件。武装之后没有热备来，到期限就静默作废，什么也不发生。同镜像滚动（改配置，二进制不变）是同一条路：武装的哈希就是现在的，热备以同样的二进制起来，读入新的配置。换签名密钥不用重启：节点热加载密钥文件。

  | 比对结果 | 路径 | 停顿 |
  |---|---|---|
  | 每个组件的布局哈希相同；旧版本有的事件、`Replied` 组合、`Activated` 的处理方法和对 tag 的消费，新版本都还有；客户端路由不变。prefab 的组成变了照样原地：加载时按 prefab 补上（上面的恢复） | **原地**：交接带上所有的 world | 目标约 0.1–0.3 秒，100 万个 Actor 约 0.3–0.7 秒（W6 实测） |
  | 布局变了，但只变在新版本读得了记录的组件上（`VersionTolerant`、`Keyed` 的 `[NinoType]`，或者不进记录的组件），或者去掉了上一行的处理方法 | **经记录**：旧进程先停用所有 Actor、写记录，交接的 world 里没有 Actor；新进程按需从记录激活（`/admin/restart?mode=unload` 强制走这条） | 更长；不持久化的状态按停用的规则丢掉，Presence 由 SDK 重新加入 |
  | 承载关系（`[Hosts]`）或客户端路由变了 | 拒绝；走**滚动升级**（上面），路由变了要连网关一起升级 | — |

  - 去掉的处理方法可能正有数据在等：待执行的事件、等待实体、正在离开的 Presence，所以原地不接受它。
  - **拒绝武装**：有成员在 `Joining` 或 `ShuttingDown`（放置正在变）；本节点作为来源或目标还有槽在交接中（第 6 节的交接：暂停期间目标会把邮件停在 `Arriving` 上，请求超时）；成员表里已经有别的节点的行带着 `restart_until`（全集群同时只有一个热重启，第 2 节）；热备放不下：交接时机器上有两个进程，热备要大约节点的工作集那么多内存，它的内存上限或者机器的可用内存不够就拒绝（Kubernetes 上先把热备容器的内存请求原地调到这个数，节点放不下就拒绝，见平台表）；存储测得的写记录延迟（p99）超过冻结上限 `DaServer:HotRestart:FreezeBound`（2 秒）的四分之一：冻结要等在途的写和留住输出的根再写一次，存储慢时冻结注定过上限、就地恢复，白停一次。持有热重启许可的节点，协调者不给它规划迁入或迁出的槽移动；武装之后、交接开始之前有成员变成 `Joining` 或 `ShuttingDown`，或者有任何涉及本节点的槽交接开始，武装作废，热备回到停着。
- **武装时先写一轮**：武装被接受后，节点把所有带 `Unsaved` 的根写一次（一轮检查点，Actor 照常运行）。交接成功时这一轮不是必需的，它管的是之后的坏情况：热备接过去之后又崩溃，从记录恢复只丢武装之后的改动。冻结里不再写剩下的：停顿短更要紧。武装的回答等这一轮写完才发出；这一轮写失败了，武装以失败回答并报告原因，不开始。
  - 一个节点组逐个节点热重启，协调者排在最后；期间新旧版本混跑，规则同滚动升级。
- **步骤**：
  1. **热备起来**：触发器重启这一对里不是节点的那个实例（平台表）。它以新的二进制启动，看到武装文件、哈希对得上，就当热备：完成 .NET 的启动、预热代码（AOT 没有要 JIT 的；JIT 下是 ReadyToRun 加预热路径）、打开存储的连接池，不加入集群、不服务，在本机管道上监听：`NamedPipeServerStream` 带 `PipeOptions.CurrentUserOnly`，Linux 和 macOS 上是目录里的 Unix 域套接字（权限 0600，残留的旧套接字先删掉；路径超过平台上限，Linux 108 字节、macOS 104 字节时，启动时拒绝、发布时 `DaServer.Aspire` 就报错；不退到 `/tmp`：Compose 每个容器各有自己的 `/tmp`，systemd 的 `PrivateTmp=` 也一样，而且别的用户能抢先占用那个名字），Windows 上是命名管道；AOT 可用。热备是管道的服务端，节点是客户端。不用匿名管道：热备由监管者启动，不是节点的子进程，继承不到句柄。
  2. **握手**：武装过的节点从武装起就去连管道，连不上或握手不过就退避重试，直到有热备通过握手（武装的随机数、武装的 `wire.manifest` 哈希必须等于热备自己的、期限、热备的进程 id，都在任何字节之前），或者武装过期。还没换掉的旧版本实例握手不过（哈希不对），节点接着重试。到期限还没有热备通过握手，节点从不冻结，武装过期，滚动流程报告失败。
  3. **取得许可**：节点在成员表里自己的行写上 `restart_until`（期限，按存储的时钟 `now()` 加上上限；一次带版本的写，别的行已经有 `restart_until` 时比较失败，热重启不开始），其他成员读到就把它当作 `NodeRestarting`。这次写提交之后节点才冻结。期限只按存储的时钟判定。
  4. **冻结前的最后一个 tick**：框架给每个 Actor 的根加上 `Restarting(TimeSpan ExpectedPause)`，用户的系统按需要反应（下面）；同一个 tick 的 `Settling` 去掉它，所以交接的字节里没有 `Restarting`。
  5. **冻结**：每个 world 在两个 tick 之间冻结：不再执行任何项，等在途的存储操作（写记录、占 epoch）有结果：写成的就此落定，`Commit` 留住的输出随之放出，在切换点之前交给链路；没写成的检查点给根加回 `Unsaved`，随字节过去，新进程的下一个检查点接着写；还有留住输出的根先写一次，等它们放出（第 8 节），所以交接的字节里没有留住的输出；这一次 `SaveEntitiesWith` 抛出的根当场丢弃留住的输出，从冻结的 world 里销毁子树，留在字节外面（第 10 节的"只写改过的"）。冻结里不执行任何项，所以只要等一轮写。这个等待有上限（`FreezeBound`），过了就放弃（下面）。**冻结只停 world 的线程**：IO 线程照常收帧，放进调度队列，冻结期间发来的消息不被拒绝。
  6. **流过去**：节点停止读链路，这一刻就是切换点。它把字节从池化的缓冲区直接流进管道，不落中间文件：
     - 头：节点名、节点代数、武装的随机数、旧版本和武装的 `wire.manifest` 哈希、每个组件的布局哈希、world 的个数、冻结时的 `Environment.TickCount64`；单节点（`SingleNode`）的还带着自己那一行成员记录（第 2 节）；
     - 每个 world：`world.SaveWithout(output, in exclude)` 的字节（实体连同 id 和版本、组件、`DynamicBuffer`（包括占位上等着的 `Mail`）、单例；不含系统、索引、写入戳，EasyEcs `guide/saves.mdx`；`exclude` 是用户的单例和框架运行时的单例，下面）、调度队列里还没进 world 的邮件、`Time.Elapsed`；每个 world 在自己的线程上序列化、边写边算校验和，各 world 的字节在管道上按块交错，所以停顿大约是最慢的那个 world 的序列化，加上热备那边的加载；即时链在两个 tick 之间是空的，`Outbox` 在冻结前的最后一个 tick 交出了所有的帧（包括没到点的组帧，第 9 节）；
     - 节点的运行时状态：调用 id 的序号、墓碑；每条链路收到的字节数，发出的偏移和还没确认的尾巴；邮件和期限已经等了多久（第 9 节）；
     - **监听的套接字**：Unix 上经 Unix 域套接字的 `SCM_RIGHTS` 交过去（P/Invoke 的 `sendmsg`，AOT 可用），热备用 `new Socket(new SafeSocketHandle(fd, ownsHandle: true))` 接上；Windows 上节点对每个监听的套接字调用 `Socket.DuplicateAndClose(热备的进程 id)`，把 `SocketInformation` 的字节经管道交过去，热备用 `new Socket(SocketInformation)` 接上。所以端口一刻也不空着，排队等着被接受的连接也不丢。HTTP 平面（Kestrel）的端口用 Kestrel 的 `ListenHandle` 接上；接不上的平台（W6 逐个核实）重新绑定它，只影响健康检查和管理端点一个很短的间隙。
  7. **检查、加载、确认**：热备边收边检查（校验和、随机数、`wire.manifest` 哈希、期限），按头里的个数建 world，不管这次分到多少 CPU（键按哈希落在哪个 world，个数不同就对不上；只有配置 `DaServer:Worlds` 钉了另一个数时才拒绝）；先读入集群当前版本的配置表、建好用户的单例和框架的单例，再流式加载（`world.LoadKeeping(bytes, in keep, markWritten: false)`，`keep` 就是它们），按 prefab 补上组件（上面的恢复），接上时钟、加上系统、重建到期堆和调用 id 索引，然后回答确认；字节不对就回答拒绝，丢掉读到的，回到停着。节点收到确认才放掉锁、交出套接字，然后原地停着（上面）。**收到拒绝、流断了或者超时没有回答，节点就地恢复**：它什么也没放掉（锁、套接字、成员表的行都还在，`restart_until` 由它清掉），根带一个 tick 的 `Resumed(Pause, Aborted: true)`，什么也不丢；它向滚动流程报告失败，流程在这个节点停下，同任何一步失败一样。
  8. **`Rejoining`**：热备从节点在管道上的最后一句（"交出了"）和拿到锁知道节点走了，接管套接字，读成员表里自己的行；它的 world 保持冻结，直到读到结果：
     - 还是 `Active`、代数还是自己的、`restart_until` 还是旧进程写的那个：用一次带版本的写清掉 `restart_until`（期限已过时这次写的条件不成立，按下一条处理），以同一个身份（节点名，代数）重连。先核对一遍：根所在的槽现在的主人已经不是自己的（期间放置变了），隔离或者迁出这些根；然后各 world 开始跑。其他节点认出同一个身份，按交接的字节数续上各条流，重放各自的重发窗口：就是链路断了重连时的续流，不重复也不丢。对端收到这个身份的 Hello 时清掉对它的重连退避，马上连回来。
     - 已经被写成 `Dead`（期限过了，协调者宣布了它）、期限已过，或者已经有更大的代数：丢掉加载的 world，不写，以新的代数加入，从记录激活。
  9. 第一个 tick，每个根带着 `Resumed(TimeSpan Pause, bool Aborted)`，`Pause` 是实测的停顿；有 `[TickRate]` 的 Actor 给带 `Joined` 的 Presence 重推时间通知（第 7 节）；收尾组 `Settling` 去掉 `Resumed`。系统在有序阶段之后跑，所以看到 `Resumed` 时，停顿期间到的消息已经执行过了。
- **放弃**：冻结等过上限（例如存储挂住），节点清掉自己的 `restart_until`，告诉热备作废，就地恢复：每个根在下一个 tick 带着 `Resumed(Pause, Aborted: true)`，用户的系统可以撤销对 `Restarting` 的反应；滚动流程报告失败。热重启从不因为失败而退出进程：没有谁要它停下。
- **SIGTERM 永远是优雅停止**（所有 Actor 停用、写记录，ADR-063）：缩容、关机、平台换掉 Pod，都不是热重启。
- **热备拿到锁、却没有经过交接**（节点崩溃、被杀，确认之前或者没有武装）：它丢掉没确认的字节，走普通的启动，从记录激活。`Rejoining` 对 `restart_until` 的检查排除了加载两次。
- **就绪**：`/health` 报告进程的实例 id（启动时随机生成）、实例名（`DaServer:Instance`）和正在运行的 `wire.manifest` 哈希。滚动流程等到节点报出一个新的实例 id、而且哈希等于武装的哈希才往下走：监听的端口随套接字交过去，旧进程交出之前也还在回答；交接失败、就地恢复的节点报的是旧的实例 id，流程不会当它成功了。
- **用户的反应是数据**：
  ```csharp
  [UpdateInGroup<Map>]
  public class RestartNotice : EcsSystem
  {
      [ForEach, With<Restarting>]                                          // the last tick before the freeze
      static void Warn(Entity map, in Restarting r) => map.Push(map.Everyone, new MapRestarting(r.ExpectedPause));
  }
  ```
  - 不写系统时什么也不做：停顿期间的消息在调度队列和对方的重发窗口里等着，之后照常执行（过了期限的请求按第 9 节丢弃）。秒杀这类就是这样。
  - 例如 MMO 推一条通知，再让 Presence 离开（`Leave()`），客户端按通知里的停顿，大约一秒后重新进图；
  - 要在重启后全量走一遍的系统，过滤 `[With<Resumed>]`；`Resumed.Aborted` 为真时，是撤销的时候。
  - 网关上的系统读节点单例 `RestartingNodes`，见第 7 节。
- **用户的单例和框架运行时的单例不交接**：配置表（`IConfigTable<T>`）和 world 自己的缓存（`IWorldSingleton<T>`）是新进程自己的数据（集群当前版本的表、重新建的缓存），类型也不用能序列化；框架的 `Outbox`（池化的写入器，冻结前已经交出所有的帧）、`Current`、`Time` 和节点单例的副本是运行时的状态，没有 Nino 格式化器，带过去也是过时的。旧进程用 `world.SaveWithout(output, in exclude)` 保存，`exclude` 是这些单例（`ComponentSet.Create().WithSingleton<T>()`，注册时建一次）；新进程用 `world.LoadKeeping(input, in keep, markWritten: false)` 加载，`keep` 是同一个集合：加载跳过字节里的值，保留 world 现在的（`guide/saves.mdx` 的 "Restart"）。`keep` 只能有单例（有组件就抛出 `ArgumentException`），所以这个集合只放单例。
- **时间**：新进程的 world 时钟从保存时的 `Time.Elapsed` 接着走，所以用户的延后事件不用改写，剩下的时间不变，停顿不计入；框架的期限减去停顿（第 9 节）。
- **所有权**：
  - 不重新占 epoch，不隔离（留在字节外面的根除外，第 10 节的"只写改过的"）：每个 Actor 的 epoch 在它的根上，随字节过去；存储里的 epoch 没变，新进程写记录照样比较成功。
  - 任一时刻只有一个进程跑 world：旧进程在切换点停下，收到确认才放锁；热备拿到锁、读到自己的行之后才运行 world。
  - `NodeRestarting` 期间其他节点不把它当作失效：不怀疑它，也不宣布它 `Dead`；协调者钉住它所有的槽，不规划迁入或迁出它的移动，不重新激活。发给它的消息留在各自链路的有界重发窗口里（网关上的客户端消息也是）。窗口满了：单向消息丢弃并计数，请求立刻以 `Busy` 失败（`RetryAfter` 是预计停顿），重试由发问方按第 11 节决定；请求的期限照常走，可能超时。
  - 有成员在 `NodeRestarting` 时，别的节点的离开在自己的停止预算之内等它回来再走：两件事同时改放置，第 8 步的核对就对不上了。
  - 期限过了，协调者把它写成 `Dead`。Actor 在别处占新的 epoch，从记录激活；迟到的热备读到自己 `Dead`，以新的代数加入。
  - 热重启的节点不是协调者：协调者是没有 `restart_until`（不管值是多少）的 `Active` 成员里启动最早的（第 2 节），规则只看表、不看时钟，所以读到同一个版本的节点都同意；协调者自己武装热重启时，下一个按规则就接手，期限过了由它把这个节点写成 `Dead`，不会出现谁都不认为自己是协调者的情况。
  - 放弃了的热重启（存储卡住、交接失败）由这个节点自己清掉 `restart_until`：它的行还是 `Active`、还是它自己的代数时，这次带版本的写就成立，期限过没过都一样；所以存储晚回来时，一个健康的节点不会因为期限已过被写成 `Dead`、丢掉没写下的状态。
- **平台**：每个平台都是这一对实例，只换"重启不是节点的那个实例"这一步；逐个节点"预拉镜像、武装、重启那个实例、等就绪"的循环只有一份（[deployment.md 第 5 节](deployment.md#5-rbac-and-who-runs-the-hot-restart)）。DaServer 不另起管理进程。

  | 平台 | 这一对实例 | 触发器 |
  |---|---|---|
  | Kubernetes | 一个 Pod 两个容器 `node-a`、`node-b`：同一个镜像、同一份配置，共用 Pod 的网络命名空间和一个 `medium: Memory` 的小 `emptyDir`（锁、管道、武装文件）；每个容器有自己的探针端口（`DaServer:ProbePort`），停着的和热备的容器报告就绪（它们不服务），所以 Pod 的就绪只取决于节点。内存：两个容器的上限相同；请求跟着角色走：节点的容器带节点的大小（CPU 和内存），停着的带空闲的大小，所以整个 Pod 按一个节点调度；chart 把 `node-a` 渲染成节点的大小，冷启动时它成为节点（空闲大小的实例从不先拿锁）；进程的处理器数是 chart 里节点大小的字面值，不跟着请求变，所以调整请求从不改变进程的并行度。崩溃接管（停着的实例在空闲大小的容器里成了节点）后请求是反的：Pod 的总量对，节点的 CPU 权重却是空闲的，节点从自己的 cgroup 看出这一点，报告 `daserver.node.requests_low`，并自己经部署流水线请求只调请求的那次运行（带退避重试，直到 cgroup 显示节点大小），两边的请求原地对调，不重启任何东西。自动扩缩：忙碌比只由节点进程导出，按组平均的 KEDA、HPA 数的就是节点，不管它在哪个容器里；VPA 在这些 Pod 上是 `Off`，只给 Job 读推荐 | rollout Job 先把停着的容器的 CPU 和内存请求原地调到节点的大小（内存取 `nodeSize.memory` 和 VPA 对两个容器推荐的较大值；Pod 的 `resize` 子资源，Kubernetes 1.33 起默认开启的 beta、1.35 GA，`resizePolicy` 不重启；Windows 的 Pod 和用了静态 CPU 或内存管理器的机器不支持，那里走经记录的删除重建）。调整的结果是 `Infeasible`，或者等过期限还是 `Deferred`（可能之后才生效），Job 都把请求调回去、拒绝武装，这个 Pod 走经记录的删除重建，不留一个以后才生效的大请求。调成了再只改它的镜像（一次强制的服务器端应用，ADR-085）：kubelet 只重启这个容器，它以新镜像起来当热备；交接之后旧进程原地停着（上面），Job 把它的请求调到空闲的大小。同镜像滚动没有镜像可改：Job 经节点的管理端点让节点在管道上告诉停着的实例退出，kubelet 重启它（十分钟内退出过的容器有重启退避，10 秒起，只推迟它回来，不影响停顿），它重新读 env 和 `envFrom`。**就绪的窗口**：Pod 的就绪要求每个容器都就绪，所以停着的容器被改镜像、重启的那几秒，Pod 不就绪；承载热重启的节点组不承载 `Connection`（网关不热重启），没有客户端的 Service 依赖它，PDB 把它算作一个不可用的 Pod，这一刻挡住同组别的 Pod 被驱逐，正是逐个节点的意思 |
  | Docker Compose | 两个服务 `map-0-a`、`map-0-b`，`network_mode: service:map-0-net` 共用一个占着网络命名空间和主机名的小容器（固定的镜像，发布不重建它），共用一个命名卷（锁、管道、武装文件）；三者都是 `restart: unless-stopped`（`on-failure` 在 Docker 守护进程重启、机器重启后不拉起） | 部署机器上 `docker compose up -d --no-deps --force-recreate map-0-b`（`/health` 说 `map-0-a` 是节点时）；不带 `--force-recreate` 的 `up` 只在镜像或配置变了时才重建，同镜像滚动就什么也不重启。交接之后旧容器原地停着 |
  | systemd | 单元模板 `daserver@.service` 的两个实例 `daserver@map-0-a`、`daserver@map-0-b`，都启用；模板设 `DaServer__Instance=%i`，节点名和目录写在每个实例的 `/etc/daserver/%i.env` 里（没有能去掉 `-a`/`-b` 后缀的说明符）；目录 `/run/daserver/<节点名>` 由 `tmpfiles.d` 的一行在开机时建好，不用 `RuntimeDirectory=`（单元停下时 systemd 删掉它的运行目录，而两个实例共用它）；`Restart=on-failure` | `systemctl restart daserver@<不是节点的那个>`；交接之后旧实例原地停着 |
  | launchd | 两个 job，`KeepAlive`（`SuccessfulExit = false`） | `launchctl kickstart -k system/<不是节点的那个>`：不管它在不在跑都启动它，在跑的停着的实例被 SIGKILL，它什么也不持有，无害 |
  | Windows 服务 | 两个服务，同一个服务账户（专用用户或 gMSA），或者管道带显式的 `PipeSecurity` ACL 允许另一个服务的 SID（两个默认虚拟账户下 `CurrentUserOnly` 会拒绝节点，每次交接都失败）；`sc failure` 和 `sc failureflag 1` 让服务管理器拉起以非零退出码停下的服务（隔离退出，第 2 节） | `Restart-Service <不是节点的那个>` |
  | 本地 Aspire | 每个节点资源两个副本，各自的实例名，共用一个临时目录 | 仪表盘里的 `{cluster}-restart`：重新编译，逐个节点武装，再用 Aspire 资源的重启命令重启不是节点的那个副本 |

- **写入戳**：新进程用 `world.LoadKeeping(input, in keep, markWritten: false)` 加载（`guide/saves.mdx` 的 "Restart"，EasyEcs D39）：加载的行没有写入戳（tick 0），也不算移除过；系统加在加载之后，第一次运行看的是 tick 1 起的写入（`guide/change-tracking.mdx` 的 "Ticks"）。
  - 所以 `ChangeSystem` 不会给每个根标 `Unsaved`，Actor 不会把记录重写一遍；真正没写的记录由带过来的 `Unsaved` 负责。
  - **用户看得到的后果**：热重启之后，所有 `[Changed<T>]` / `[Removed<T>]` 方法（包括用户的，例如 AOI、`SyncSubscriptions`、`CellAssign`）都不把加载的行当作变更。由追踪组件算出来的组件本来就随字节过去，一般不用重算；确实要全量走一遍的，用 `[With<Resumed>]`。
  - `AddSystem(system, changesFromNow: true)` 只给运行中途才加入的系统，例如功能开关打开时才加上的。框架注册时加的系统都用默认值。
  - `world.Restore(snapshot)`（`Capture` / `Restore`）总是把恢复的行算作写入：只用于测试，不用于热重启。
- **网关不热重启**：`Connection` 钉在套接字上，换进程连接就断。网关先扩后缩、分批 `Retire`，客户端不断线（第 7 节，[deployment.md 第 2 节](deployment.md#stateless-groups-and-gateways-deployment)）。
- **`[Stateless]` 的 Actor 类型**没有要交接的状态：可以单独部署成节点组，先起新节点、再停旧节点，零停顿。

### 不重启就能改的

两种改动都只是换数据，在两个 tick 之间生效：
- **配置表**（调参的表）：实现 `IConfigTable<T>` 的用户单例（第 5 节），`T.Name` 是表名，`T.TryLoad` 解码并校验；处理方法和系统取 `in`（第 12 节的 `SkillTable`）。
  - **整个集群一个当前版本，要有记录存储**：存储里每张表一行 `(名字, 版本, 哈希, 字节)`，协调者把 `(名字, 版本, 哈希)` 写进成员表的版本行。每个启动的、热重启的节点都读这个版本（按哈希核对字节），所以新节点和老节点用的是同一份表。存储里还没有这张表的版本时（新集群，或者一次发布加了一种表），第一个读它的节点用自己的 `DaServer:Tables:{名字}` 写入第一版（插入只成功一次）。
  - **没有存储的节点**（只承载 `Connection`、`[Stateless]` 和根不保存的 Actor 类型，而且是单个节点，配了 `DaServer:Membership:SingleNode = true`：多于一个节点就要有存储放成员表，第 2 节）从 `DaServer:Tables:{名字}` 读表，`POST` 只换收到它的那个节点的表：文档里写明这种表是每个节点各自的。
  - **读不了当前版本的节点不就绪**：节点的 `TryLoad` 拒绝存储里的当前版本（例如新版本改了表的格式）时，就绪检查失败并报告这个错误；存储里没有、`DaServer:Tables:{名字}` 也没有的表同样让读它的节点不就绪，报告缺的表名。`wire.manifest` 的 `table` 行带着表类型的格式哈希，`{cluster}-upgrade-check` 看到格式变了，就用新版本的 `TryLoad` 试读当前版本，读不了就事先拒绝这次发布。
  - **换版本**：管理端点 `POST /admin/tables/{名字}`（也是 Aspire 命令）把新字节交给协调者（没有存储时见上面）：先 `TryLoad` 校验，失败就不换，命令报告原因；通过就写进存储、版本加一，成员表的版本行传开后每个节点读入一次，每个 world 在两个 tick 之间换上。
  - **回滚**就是把旧版本的字节再 `POST` 一次：它成为新的一版。
- **功能开关**：二进制里编译好的几种做法，由一个单例或 tag 选（处理方法和系统按它过滤）；开关就是改这份数据。

---

## 11. 失败时的结果

| 情况 | 结果 |
|---|---|
| 目标节点宕机 | 请求超时或者收到 `NoOwner`。Actor 在新节点占到新的 epoch，从记录激活，最多丢一个检查点间隔的改动（`Commit` 之后回答过的不丢，第 8 节）。`Connection` 看到节点离开集群，删路由，`PresenceLost(槽号)` 给 SDK，SDK 重发加入消息（第 7 节） |
| 两个节点都以为自己有同一个 Actor（分区、长停顿之后） | 只有占到最新 epoch 的能写；被挤掉的收到 `Fence` 或者写记录时比较失败（批量写逐行比较，只隔离这一行的 Actor），被隔离：不写、不进 `Leaving`，路由由清理组件释放，SDK 重新加入到真正的主人 |
| 节点被其他成员探测不到（宕机、单向或对称分区） | 探测不到它的成员写怀疑，凑够 `VotesForDeath` 条就把它写成 `Dead`；它读到就冻结、隔离、退出；`IsolationTimeout` 之内读不成表的节点冻结，读到自己还是 `Active` 再恢复；冻结的节点回答探测 `Isolated`、就绪检查失败，连得上别人时 `IsolationExit` 之后退出；协调者等存储的 `now()` 过了 `dead_seen_at + IsolationTimeout + DeathMargin` 才把它移出放置集合，在那之前发给它的键的回 `Unavailable(retryAfter)`（第 2 节）。单向不通的一对由 `daserver.link.unreachable` 报告 |
| 存储不可用 | 不到 `IsolationTimeout` 时照常运行（谁也宣布不了死亡、加入不了，槽不移动，要写记录的照第 10 节等着）；超过了，**整个集群暂停**：每个节点冻结 world，连接留着、邮件在有界窗口里排队，存储回来、读到自己还是 `Active` 就恢复（第 2 节）。恢复时不一起压上去：成员表走自己的小连接池，不排在积压的写后面；写记录和占 epoch 从每个 world 一条起步、每成功一轮加倍到上限，`DueScan` 带随机的延后；刚从全集群冻结里恢复的节点，在一个 `IsolationTimeout` 加上自己的积压写完之前，不把别人的 `Isolated` 当作探测失败，所以存储恢复时不会连锁地把节点宣布死亡 |
| 节点被虚拟机管理器暂停、或者从检查点恢复（CRIU 的时间命名空间），自己的时钟看不出 | 每次读表都带回存储的 `now()`：两次读之间存储的时间比两次读的[发出, 回答]区间允许的最大间隔多走了一秒以上（等着开始的读不算），再读一次确认后当作停顿过，冻结到下一次读表确认自己还是 `Active`；所以醒来之后最多一个 `TableRefresh`（5 秒）里可能还有过时的工作，保存的写有 epoch 比较拦着，对端拒收 `Dead` 那一代的帧（第 2 节） |
| 成员表写冲突（两个节点同时改） | 比较失败的一方重读、重试；先写成的生效，所以分区两边的死亡宣告只有一个成立 |
| 协调者宕机 | 它被宣布 `Dead` 之后，启动时间次早的 `Active` 成员就是协调者（每个节点从同一份表算出来）；热重启的期限存在表里，不用从头算（第 2 节） |
| 对称的网络分区 | 存储是仲裁者：先写成的死亡宣告胜出，被宣布的一边读到就停下；两个成员的集群也恰好留下一个。被宣布的一边人数再多也一样，不按人数算；连不到存储的一边租约过期自己冻结，它的槽要等 `dead_seen_at + IsolationTimeout + DeathMargin` 才移走（[architecture.md 第 4 节](architecture.md#4-placement-activation-persistence)） |
| 按槽表或节点进出迁移一个键 | 新主人先向旧主人要（邮件先到，字节随后），旧主人回答"不在"才去占 epoch；旧主人写不了记录（存储挂住）时这次迁移推迟，邮件转发回旧主人，Actor 留在那里照常运行 |
| 客户端带 `[To]` 的消息指向一个没有记录的键 | 没写 `[ActivatedBy]` 的 Actor 类型回 `NoActor`，存储里什么也不写（第 4、6 节） |
| 被宣布 `Dead` 的节点又连上了 | 读到自己 `Dead` 就隔离所有的 Actor、退出；新进程以新的节点代数加入。对端拒收 `Dead` 那一代的帧 |
| 按过时的视图去激活 | 节点不按比见过的更旧的放置版本激活；存储里的放置版本更新、或者同一版本已经有别的主人，占 epoch 被拒绝；不是主人的节点本来就转发，不激活 |
| 整个集群冷启动、几个节点同时启动 | 代数取 max(Unix 毫秒, 表里见过的 + 1)，不重复；第一个写成的创建版本行，其他的按表加入；没被宣布死亡的旧行 `i_am_alive` 过期，不挡加入（第 2 节） |
| 网关滚动升级或缩容 | 不断线：先扩后缩，分批 `Retire`，SDK 先连上新网关、接管各个 Presence，`HandedOff` 栅栏保持每个 Actor 的推送顺序，再关旧连接（[architecture.md 第 3 节](architecture.md#retire-and-rebalancing)） |
| 网关宕机、客户端断线 | 客户端带令牌重连，得到新的 `Connection` 和更大的连接戳；目标上的 Presence 带着 `Detached` 等着，SDK 重发各槽的加入消息，新连接接管它们，快照系统重推一份；宽限期过了没人接管才 `Leave()`（第 7 节） |
| 半开连接时客户端已经连到别处 | 连接戳更大的胜出（`Takeover.NewestWins`），目标发 `TakeOver`，旧的 `Connection` 安静地关闭；旧连接迟到的加入戳更小，`Join()` 返回 `Superseded`（同一次登录，SDK 不当作终止）；同一个连接重试，戳相等，幂等再回复 |
| 另一台设备登录了同一个账号 | 签发序号更大的接管；旧设备的连接以 `Superseded` 关闭，SDK 报给应用，不自动重连；只是刷新过令牌的旧设备排不到新登录前面（第 7 节） |
| 连上就加入失败 | 框架按 `RetryAfter` 或带抖动的退避重试，`JoinRetryFor`（60 秒，长过一次重新放置）过了才以 `Unavailable(retryAfter)` 结束会话 |
| `Join()` 时没有客户端上下文 | 返回 `NoClient`，处理方法决定怎么回复 |
| `Join()` 时已有的 Presence 连接戳更大 | 返回 `Superseded`，处理方法决定怎么回复 |
| 加入后路由没写成（客户端已断开） | 确认期限到，`Leave()` |
| SDK 重发加入消息失败 | SDK 带抖动退避重试，同样按时间算预算（长过一次重新放置），过了才放弃；应用只看到 `PresenceLost` |
| `Activated` 抛出异常 | 激活失败：排队的请求失败，实体不写记录就销毁，按退避再激活；连续失败若干次后留下激活失败的占位，到期前立刻回 `Busy`（第 5 节） |
| 存储慢或不可用 | 存储降级：不做空闲停用，`Saving` 的占位有上限（超过的 `Deactivate()` 推迟）；同时占 epoch 的激活有上限，带抖动退避；等过期限的占位立刻回 `Busy`；节点照样就绪，已激活的 Actor 照常运行（第 6 节）；`Commit` 留住的输出等到写成才发出，发问方按自己的期限超时、用同一个键再问；留住的输出到了每个根、每个 world 的上限，发给这些根的新请求回 `Busy(RetryAfter = 写延迟)`；等着写的根超过 `BacklogPause`，world 暂停取新邮件（第 8、10 节） |
| 存储每次都拒绝一行（值太大、约束、数据错误） | 这条语句对半拆开再写，直到找出那一行：它按 `RecordUnwritable` 处理（存储里留着上一份记录，它留住的输出丢弃），其余的照常写成；只有临时的错误才用同样的字节重试（第 10 节） |
| 一次大事件之后很多根同时要写（检查点风暴） | 检查点按"最早没写下的"先取、按键的哈希错开，每一轮给检查点留四分之一的行；宕机丢的是检查点间隔和积压排空时间两者里大的那个；优雅停止过了预算还没写下的改动丢掉并记日志（第 10 节） |
| 主体缺少组件参数，或者不满足过滤 | 不调用处理方法；请求以 `Skipped` 失败，单向消息和事件跳过（`TestActor<K>` 里缺少组件参数抛出，那是代码错误；过滤不满足照常跳过） |
| 请求超时、`ConnectionLost` | 不等于没执行：普通的请求由发问方决定，再问是一次新的执行（只对读和幂等的请求再问）；`[Once]` 的由 `AskUntilAnswered`、SDK 用同一个序号再问，接收方回答第一次存下的回复（第 8 节） |
| 请求在执行前已经过了期限（排队、重发窗口、热重启、迁移途中） | 丢弃并计数，不执行（第 9 节） |
| 接收方 `Commit` 之后、写成之前宕机或被隔离 | 回复不发，改动和 `Unacked` 里的那一项也没写进存储；`AskUntilAnswered` 超时，用同一个序号再问，真正的主人执行一次（第 8 节） |
| 接收方写成之后、回复到达之前宕机 | `AskUntilAnswered` 超时再问，接收方从 `Unacked` 里认出这个序号，回答存下的回复，不再执行 |
| 发问方在回信前宕机 | 普通的问随之消失；`AskUntilAnswered` 的请求在记录里，`DueScan` 按它的 `WakeAt` 唤醒 Actor，框架接着问，接收方回答存下的回复（第 8 节） |
| 发送方确认之后，一次更早的尝试才到 | 发出时刻早于 `MaxAskTimeout + ClockSkew` 之前的丢弃；其余的序号不大于接收方记的"已确认到 n"：过时的重复，回 `Stale`，不执行，计数（同 TCP 的序号检查）；`AskUntilAnswered` 不会收到 `Stale`（确认只按写下的状态发），SDK 收到时按"结果未知"交给应用（第 8 节） |
| 确认丢了（链路断开、接收方在下一次写之前宕机） | 无害：确认走内部通道，邮箱满、`Busy` 丢不掉它；之后的每封邮件、下一次确认都带着同样或更新的累计确认；发送方安静了，接收方过 `Once:ProbeAfter` 探测一次（第 8 节） |
| 发送方给一个接收方的窗口用完 | 后面的 `[Once]` 请求在发送方排队，接收方按先后推窗口更新，更新丢了按 `Once:ZeroWindowProbe` 再问；发送方的根排着没发的问到了 `OnceQueued`，发给它的普通请求和客户端的 `[Once]` 请求在执行之前回 `Busy`（客户端会话的窗口降到 0），系统、反应、计时、Actor 的 `[Once]` 请求和回答照常执行，问都在存储里单独的行上，从不撑爆记录（第 8 节）；接收方的发送方行到了 `Once:SendersPerReceiver`，新发送方回 `WindowFull`，行只在发送方被销毁之后空出来；从不挤掉一项（第 8 节） |
| 发问的根被销毁 | 根还有问的行（任何状态）时 `root.Delete()` 回 `Outstanding`，不删；删除时，删它的记录的事务给它在各接收方的行写下"忘于 = 现在 + `MaxAskTimeout` + 3 × `ClockSkew`"，到时删掉；之前发出的尝试到那时都已过时（接收方按发出时刻丢弃），所以不会被当成新的；激活着的接收方另外收到一次"忘掉"，探测得到"已忘掉"（第 8 节） |
| 接收方被销毁 | 等 `Unacked` 清空、没有问的行、过了宽限才删；它的发送方行不删，写下"孤于"留到那时，所以同一个键重建出来的 Actor 读到它们，迟到的已执行的尝试回 `Stale`；没执行过的能激活就在新的 Actor 上执行，不能激活就回 `NoActor`（确定没执行）（第 5、8 节） |
| 客户端一直重发、接收方一直收不到（节点宕机、放置还没稳定） | 一次调用最多重发 `ResumeWindow`，从第一次发出算，每个网关按会话令牌里的高水位强制；接收方的会话行在它见过的最晚的重发截止再过 `MaxAskTimeout` 加 `ClockSkew` 才删，所以删了之后这些调用的尝试都进不来（第 8 节） |
| 拿着回复令牌的 Actor 在回复前停用 | 令牌在要保存的实体上，重新激活后期限内照样回复；否则调用方按期限超时 |
| 过了期限才 `Reply` | 不发出，计数 |
| 发问的实体在回信前被销毁 | 等待实体一起销毁，回信丢弃 |
| 停用时写记录失败 | 重试；这期间到达的消息留在 `Saving` 占位上，不会读到旧记录 |
| 迁移期间的消息 | 不丢，尽力保序；晚到的邮件由墓碑转发（墓碑指向的节点已经离开时按当前放置）；目标节点宕机时，在途的单向消息可能丢失 |
| 迁移两端的组件布局不同 | 不搬字节：停用、写记录，目标从记录加载 |
| 记录里有这个版本不认识的类型（回滚之后） | 跳过并计数，加载照常；新版本 prefab 上多出的组件按 prefab 的默认值补上（第 10 节） |
| 新版本去掉了还在用的处理方法 | 热重启不走原地，改走经记录（第 10 节）；分发时仍然找不到就是 `NotHandled` |
| 武装之后到期限也没有热备通过握手 | 武装静默作废，节点从不冻结，滚动流程报告失败（第 10 节） |
| 热重启冻结时存储挂住、热备拒绝、流断了、热备不回答 | 节点就地恢复：撤回 `NodeRestarting`，根带 `Resumed(Aborted: true)`，什么也不丢；滚动流程在这个节点停下（第 10 节） |
| 热备确认之后、接管之前崩溃 | 节点已经退出、热备也没了：这一对里剩下的实例（或者平台拉起来的）走普通的启动，以新的代数加入，Actor 从记录激活，丢掉武装那一轮写之后的改动和冻结时调度队列里已确认的单向消息（请求照宕机的规则超时） |
| Retire 时旧连接或它的网关在 `HandedOff` 之前断了 | SDK 等过调用超时，不带退役标记重新加入这个槽，`Joined.MissedPushes` 为真，快照系统重推（[architecture.md 第 3 节](architecture.md#retire-and-rebalancing)） |
| 设备时钟往回调，新的连接序号比目标上记的小 | `Detached` 的 Presence 不拒绝同一个 `ClientId` 的新连接；被拒绝时目标回报它记的序号，SDK 从它之上接着数（[architecture.md 第 3 节](architecture.md#connection-stamps)） |
| 热重启接管之后又崩溃 | 同普通的宕机：经记录 |
| 热重启超过 `restart_until` 的期限还没回来 | 协调者把它写成 `Dead`；迟到的新进程读到自己 `Dead`，丢掉加载的 world，以新的代数加入 |
| 热重启期间放置变了 | 协调者本来不动它的槽；回来时的核对隔离或迁出主人已经不是自己的根（第 10 节） |
| 滚动升级中收到 `NotHandled` | 重新解析放置后可以重试一次；客户端消息到了旧网关，SDK 报告版本不一致 |
| 一个 Actor 的邮箱满（`MaxMail`、`MaxMailBytes`）、占位上等着的邮件满（每个键 `MaxWaitingMail`，全节点 `DaServer:WaitingMailBytes`）、加入好之前 `Connection` 上排队的满 | 只拒这一个 Actor（或键、连接）：请求回 `Busy`；单向消息丢弃并计数；客户端的消息由网关回绝；同 world 的别的 Actor 照常收（第 1 节）。world 的派发队列满了是节点内存的最后一道闸，同样处理 |
| 发往一个节点的链路窗口满（例如它在热重启） | 单向消息丢弃并计数，推送丢掉时给受影响的 Presence 记 `MissedPushes`（第 7 节）；请求立刻以 `Busy` 失败（发问方收到 `Status = Busy` 的 `Replied`）；链路从不因为满而断开 |
| 一条链路上同时在发的大消息、在收的分片太多 | 每条链路最多 `DaServer:Links:LargeInFlight`（4）条大消息同时在发，别的在发送方排着；接收方每条链路的重组缓冲有上限 `DaServer:Links:ReassemblyBytes`（64 MiB），收到第一片时就按整条的大小做准入，进不来的整条拒绝（请求回 `Busy`），不白白重组 |
| 目标节点变慢 | 它的自适应在途上限到了，新请求立刻 `Busy`（下面） |
| 一个 Actor 收到的消息远多于邻居 | 每批里它的项数有上限，其余留到下一批；同 world 的其他 Actor 照常推进（第 1 节） |
| 新连接一下子太多（网关或节点宕机后） | 网关的接纳令牌桶回 `Busy(retryAfter)`，SDK 指数退避加完全随机抖动（下面） |
| 回信和 Presence 的进出通知 | 不受 `Busy` 限制，但有界：回信的位置两头都留好，接收方收下请求时在自己的出向窗口里留、发问方问的时候在自己的派发队列里留，按回复的确切大小（定长的）或最大字节数，回信到达或期限到了释放，留的超过一半时新的请求当场 `Busy`（第 8 节）；Presence 的通知不超过加入的速度乘上延迟（每次加入最多几条：路由、释放、`JoinConfirmed`、`Missed`）；它们照常按同一对的顺序排（第 1 节） |
| 一个 Actor 待处理的项超过 `MaxPending` | `Ask` 不发出，当场回一个 `Busy` 的 `Replied`；`Schedule` 抛出，按抛出的规则处理 |
| 客户端读得太慢 | 积压过了上限：同一个组只留最新的一帧，受影响的 Presence 记 `MissedPushes`、按预算重新同步；积压超过 `SlowClientGrace`（10 秒）才断开，按断线处理（第 7 节） |
| 离开通知丢失 | 链路重连时核对，或者网关离开集群时，按断线处理 |
| Presence 没经过 `Leave()` 就销毁 | 清理组件释放路由，SDK 收到 `PresenceLost` 后重新加入 |
| `[On<T>]` 抛出异常 | 不回滚，见第 5 节：请求以 `Faulted` 失败，即时链里还没执行的事件作废；根要保存的 Actor 重新加载（`Reload`）：把根恢复成 prefab 的副本，保留排队的邮件，从最后的记录重新激活，改了一半的项不会进检查点；连续 `Reload` 进入激活失败的退避；其余的继续（`Continue`），照现状处理下一项；`Connection` 直接断开 |
| 一项的即时链超过 1,024 个 | 按抛出的规则处理 |
| 系统抛出异常 | `SystemErrorPolicy.Disable`：这个系统停用，异常留在 `system.Error`，健康检查报告；下一次热重启或升级换上新的系统。这一趟里已经 `Commit` 过的行照常写成、放出 |
| 延后事件到期前发起者被销毁 | 事件随发起者消失，不执行 |
| 延后事件到期时条件已不成立（施法者死了、目标不在或走开了） | 过滤不满足就不调用；方法体里重新校验目标后作废 |

### 每条路径保证什么

| 路径 | 会丢吗 | 会执行两次吗 | 去重 | 谁重试 |
|---|---|---|---|---|
| `Send`（Actor 之间的单向消息） | 会：最多一次。节点宕机、链路窗口满、迁移途中目标宕机时丢失 | 不会 | — | 没人 |
| `Ask` | 请求会丢，但一定有结果：回复、拒绝或者 `TimedOut`；发问方停用、宕机时等待随之消失 | 会：结果未知后再问时 | —（不去重：只对读和幂等的请求再问） | 发问方的代码（`Transient` 时延后 `RetryAfter`）；框架只重试转发一跳上的（下面） |
| `AskUntilAnswered`（`[Once]` 的请求） | 不会：请求在记录里，宕机、停用后被唤醒接着问，一直问到有回答 | 不会：框架分配的序号，接收方执行一次、存下回复，写成之后才回答；确认过的序号被拒为过时 | 框架的序号，累计确认、探测清理（第 8 节） | 框架：链路上的结果不交给用户 |
| `Schedule(ev, after:)` | 会：停用、节点宕机时丢失（不在记录里）；迁移、热重启不丢 | 不会 | — | 没人；要扛住宕机的定时用 `DaServer.Reminders`（第 15 节） |
| 组件的状态 | 会：宕机时丢最多一个检查点间隔的改动；`Commit` 之后的不丢 | — | — | — |
| `Push` | 会：断线、`Detached` 期间丢失；链路或网关满了、客户端读得太慢时被丢掉或合并 | 不会；例外同 `Send` | — | 没人；丢掉、合并的都给 Presence 记 `MissedPushes`，快照系统按预算补全，重新加入后也是（第 7 节） |
| `ClientGroup`（含 `root.Everyone`） | 同 `Push`；只发给带 `Joined` 的 Presence | 不会 | — | 同 `Push` |
| 客户端请求（`IFromClient<R>`） | 一定有结果：回复、拒绝、`TimedOut` 或者 `ConnectionLost` | 会：应用再发时 | `[Once]`：会话里的调用 id 就是序号，框架存下回复，请求和心跳带着的累计确认后删 | SDK：`[Once]` 的用同一个序号重发到有回复，或者到这次调用的重发截止（从第一次发出算 `ResumeWindow`，每个网关都强制，第 8 节）；别的由应用决定 |
| 客户端单向消息（`IFromClient`） | 会：最多一次。断开时没送达的丢失；断线后到这个槽重新加入之前，不带 `[To]` 的被网关丢弃并计数（`NotJoined`，第 7 节）；`[Latest]` 的被同类型的新的一条替换 | 不会：SDK 不重发 | — | 没人 |
| 加入（`[JoinOnConnect]`、SDK 重发的加入消息） | 不会：失败就重试，直到成功或者 `Unavailable` | 无害：`Join()` 按连接戳幂等，唯一索引找到已有的实体 | 连接戳 | 框架（`[JoinOnConnect]`）和 SDK（各槽的加入消息），按 `RetryAfter` 和抖动 |
| 转发的一跳 | 同它转发的消息 | 不会：只重试确定没执行的拒绝（`NoOwner`、`NotHandled`） | — | 框架，计入重试预算 |
| `ICluster` 的 `Ask`、`Send` | 同 `Ask`、`Send`：调用方在 Actor 外面，不持久化 | 同 `Ask` | 订单自己的 Actor（例如支付单号就是 `Payment` 的键），由它的状态回答 | 外部调用方（支付平台的重试） |
| 存着的 `ReplyTo<T>` 跨过停用 | 令牌在要保存的实体上不丢，重新激活后期限内照样回复；在别的组件里就丢了，调用方超时 | 不会：同一个令牌只有第一次回复有效 | — | 发问方 |

先后的保证在第 1 节（一个发送方发给一个目标的一切按发送顺序到达，`Commit` 留住的挡住之后的）；期限怎么传、停顿不延长预算在第 9 节。

### 重试、预算与过载

- **`Status` 的两个分类**是 `Status` 上的扩展属性 `s.NotExecuted` 和 `s.Transient`：

  | `Status` | `NotExecuted`：确定没执行，再发一次不会执行两次 | `Transient`：过一会儿可能成功 |
  |---|---|---|
  | `Ok` | | |
  | `Busy`（带 `RetryAfter`）、`NoOwner`、`Unavailable`（带 `RetryAfter`：到重新放置还剩的时间） | 是 | 是 |
  | `NotHandled`、`NoActor`、`Filtered`、`NotJoined`、`Skipped` | 是 | |
  | `WindowFull`、`Stale`（`[Once]` 的，第 8 节） | 是：接收方查过序号之后说的 | `WindowFull` 是；`Stale` 对 SDK 是结果未知 |
  | `TimedOut`、`ConnectionLost` | 结果未知 | 是：普通的请求再问是一次新的执行；`[Once]` 的由 `AskUntilAnswered`、SDK 用同一个序号再问 |
  | `Faulted` | 否：处理方法抛出了，根不保存（`Continue`）的 Actor 可能改了一半（第 5 节）；根保存的按 `Reload` 丢掉改动，所以 `[Once]` 的请求收到它就是确定没执行。结果不存，再问会再执行一次；所以发送方只认答这一次尝试（调用 id 相同）的 `Faulted`、`NotHandled`，更早的尝试迟到的丢弃 | |
  | `NoClient`、`Superseded`、`AlreadyConnected` | 加入和连接的结果（第 7 节） | |
  | `Outstanding` | `root.Delete()` 的结果：根还有问的行（排着的、已发出的、停住的），什么都没删（第 5 节） | 等它们的 `Replied` 之后再删 |

- **框架自己重试的只有**：转发的一跳上确定没执行的拒绝（例如按过时视图转发后收到 `NoOwner`，滚动升级中的 `NotHandled`）和加入（`[JoinOnConnect]`，第 7 节）。转发的重试用每个节点一个重试预算：令牌桶，约为请求数的 10%（同 gRPC 的重试节流）；桶空了，转发把拒绝原样交回调用方。请求的重试是发问方代码的事（`AskUntilAnswered` 由框架接着问，第 8 节）：`Replied` 处理方法按 `Status` 决定，要重试的用 `Schedule(…, after: a.RetryAfter)` 延后再问（第 13 节的提示）。
- **`Busy` 带 `RetryAfter`**：SDK、`[JoinOnConnect]`、SDK 的重新加入都按它等，而且等的是 `[RetryAfter, 2 × RetryAfter]` 里的一个随机时长（完全抖动）：十万个客户端收到同一个 `RetryAfter`，不会在同一刻一起再来；没有时按指数退避加完全随机抖动。`Unavailable(retryAfter)` 带的是到重新放置还剩的真实时间（死节点的 `dead_seen_at + IsolationTimeout + DeathMargin` 减去现在，按视图），不是一个固定值。
- **接纳新连接**：网关按令牌桶接纳新连接，桶空了回 `Busy(retryAfter)`；SDK 按指数退避加完全随机抖动重连。迁移引起的重新加入也走这个桶。
- **慢的目标节点**：框架按目标节点记着在途请求数和延迟，算出一个自适应的在途上限（内部的 `PeerHealth`）；超过的新请求立刻以 `Busy` 失败，不在一个慢节点上堆到超时。
- **热点 Actor**：每批邮件里每个 Actor 的项数有上限（第 1 节）。

### 定时的默认值

| 定时 | 默认 | 说明 |
|---|---|---|
| `DaServer:Membership:ProbeInterval` | 1 秒 | 每个成员探测环上后面的成员的间隔（第 2 节） |
| `DaServer:Membership:ProbedNodes` | 10 | 每个成员探测几个成员 |
| `DaServer:Membership:MissedProbes` | 3 | 连续这么多次没回，先间接探测，再写怀疑 |
| `DaServer:Membership:VotesForDeath` | 2 | 这么多条来自不同 `Active` 成员的怀疑宣布 `Dead`；其他 `Active` 成员不够时就是全部其他成员 |
| `DaServer:Membership:SuspicionExpiry` | 3 分钟 | 比这更旧的怀疑不算票 |
| `DaServer:Membership:TableRefresh` | 5 秒 | 多久读一次成员表 |
| `DaServer:Membership:IAmAliveInterval` | 30 秒 | 多久写一次 `i_am_alive`（存储的 `now()`，不动版本）；比表里最新的落后三个间隔的成员加入时跳过 |
| `DaServer:Membership:IsolationTimeout` | 20 秒 | 隔离租约：从发出最后一次读到自己 `Active` 的读表请求算起，过了就冻结 world，读到自己 `Active` 再恢复 |
| `DaServer:Membership:IsolationExit` | 1 分钟 | 冻结这么久、而且有成员不是 `Isolated`（只有自己连不到存储），以退出码 75 退出 |
| `DaServer:Membership:DeathMargin` | 5 秒 | 死节点在存储的 `now()` 过了 `dead_seen_at + IsolationTimeout` 再加这么久才移出放置集合 |
| `DaServer:Membership:SingleNode` | false | 没有成员存储的节点要它为 true 才启动（第 2 节）；`DaServer.Aspire` 给只有一个节点、没有存储的 AppHost 设上 |
| `DaServer:Membership:DefunctRowAge` | 7 天 | `Dead` 的行留这么久（诊断用），之后由协调者删掉 |
| 节点链路的静默关闭 | 3 秒 | `MissedProbes` × `ProbeInterval`；之后重连、重放重发窗口（ADR-053） |
| 断线的宽限期 | 30 秒 | `[ActorOptions(DetachGrace = …)]`（第 7 节） |
| `DaServer:AskTimeout` | 5 秒 | 不写 `deadline:` 时一次问的超时（第 8 节） |
| `DaServer:DueScanInterval` | 1 秒 | `DueScan` 查到时的键的间隔（第 10 节） |
| `DaServer:Links:FragmentSize` | 64 KiB | 比这大的消息切成这么大的片，和别的发送方、目标之间的帧交错发出（[architecture.md 第 3 节](architecture.md#links-one-ordered-stream-fragments-and-batches)） |
| `DaServer:CheckpointInterval` | 30 秒 | 只有普通改动的根多久写一次记录；宕机最多丢这么久的普通改动（第 10 节） |
| `DaServer:Topics:RenewInterval` | 60 秒 | 订阅方多久重发一次 `Subscribe`；丢了或乱了序的订阅消息最多让订阅者这么久收不到发布（第 15 节） |
| `DaServer:ClockSkew` | 1 分钟 | 存着的 `ReplyTo<T>` 按 UTC 比较期限、`[Once]` 的尝试按发出时刻丢弃、被忘掉的发送方和客户端会话的行多留时多加的余量（第 8、9 节） |
| `DaServer:MaxAskTimeout` | 1 分钟 | 任何请求（服务端之间、客户端）的期限的上限；`[Once]` 的尝试发出超过这么久加 `ClockSkew` 就不执行，所以被销毁的发送方的行在销毁之后这么久加三个 `ClockSkew` 删，客户端会话的行在重发截止之后这么久加 `ClockSkew` 删（第 8 节） |
| `[ActorOptions(Unacked = n)]`、`OnceWindow`、`OnceInFlight`、`OnceQueued` | 1,024、16、1,024、1,024 | 接收方的根最多几项没确认的 `[Once]`；每个发送方的窗口；一个根同时在发的问（只限内存和速度，不拒绝）；一个根排着没发的问超过它，普通请求和客户端的 `[Once]` 请求回 `Busy`（第 8 节） |
| `DaServer:Once:ProbeAfter` | 30 秒 | 一项没确认、发送方又没有新邮件这么久，接收方探测一次，只为清掉确认丢了的项（第 8 节） |
| `DaServer:Once:Retry` | 5 秒，翻倍到 1 分钟 | `AskUntilAnswered` 没有 `RetryAfter` 时的重试间隔，带抖动（第 8 节） |
| `DaServer:Once:ZeroWindowProbe` | 1 秒，不翻倍 | 窗口为零、窗口更新又没到时，发送方再问一次最早的那个（第 8 节） |
| `DaServer:Once:SendersPerReceiver` | 1,048,576 | 一个接收方最多几行 `OnceSenders`（约 55 MB），满了新发送方回 `WindowFull`；行只在发送方被销毁后空出来（第 8 节） |
| `DaServer:Once:RowChangesPerWrite` | 1,024 | 一个根每次写最多几行 `OnceSenders` 的改动（约 52 KB）；没写下的到了这个数，新发送方回 `WindowFull`；每个新发送方两次改动，所以每秒最多约 `RowChangesPerWrite / 2 × 每秒写的次数` 个新发送方（第 8 节） |
| `DaServer:Once:RefusedQueue` | 4,096 | 接收方记着几个回了 `WindowFull` 的发送方，窗口打开时按先后给它们发窗口更新（第 8 节） |
| `DaServer:Once:QueuedBytes`、`DaServer:Once:MaxRequestBytes` | 64 MiB、1 MiB | 一个根排着没发的问的字节上限（同 `OnceQueued`，入口的背压）；一个 `AskUntilAnswered` 请求的最大字节，调用时检查（第 8 节） |
| `DaServer:Clients:ResumeWindow` | 2 分钟 | 会话令牌的有效期 `T` 每次回复和心跳后推到"现在加这么久"（新签令牌只在心跳或 `T` 后移超过四分之一时），不超过客户端令牌的到期；过了 `T` 任何网关都不认这个会话；一个 `[Once]` 调用从第一次发出算最多重发这么久，SDK 用单调时钟计、每个网关按令牌里的调用高水位强制；`[Once]` 的序号在会话里有效（第 8 节） |
| `DaServer:Clients:OnceReceiversPerSession` | 64 | 网关给一个会话最多记几个有 `[Once]` 项的接收方，满了这个会话新的 `[Once]` 请求回 `Busy`（第 8 节） |
| `DaServer:IdleTimeout` | 15 分钟 | `[ActorOptions(IdleTimeout = …)]` 不写时，空闲多久停用（同 Orleans 默认的激活回收时长，第 6 节） |
| `MaxPending`、`MaxWaitingMail` | 65,536、65,536 | `[ActorOptions]`：一个根待处理的项（延后事件、在途的问）；一个键的占位上等着的邮件（第 5、6 节） |
| `PresencesPerClient` | 1 | `[ActorOptions]`：一个客户端在这种 Actor 上最多几个 Presence（第 7 节） |
| `DaServer:Store:WritesInFlight` | 2 | 每个 world 同时在途的写语句（第 10 节） |
| `DaServer:DispatchQueueCapacity` | 65,536 项 | 每个分片派发队列的上限：节点内存的最后一道闸（第 1 节）；回信的预留按字节记在它旁边（第 8 节） |
| `DaServer:LinkResendWindow`、`MaxMessageSize` | 32 MB、16 MB | 每条节点链路的重发窗口（至少 `MaxMessageSize` 加回信的预留）、一条消息的上限（[architecture.md 第 3 节](architecture.md#links-one-ordered-stream-fragments-and-batches)） |
| `DaServer:Commit:HeldBytesPerRoot`、`HeldBytesPerWorld` | 1 MiB、64 MiB | `Commit` 留住的输出的上限，到了就对新请求回 `Busy`（第 8 节） |
| `DaServer:MaxReplyBytes` | 64 KiB | 一次问给回信预留的字节数，请求类型上 `[MaxReplySize(n)]` 改（第 8 节） |
| `DaServer:Store:StatementBytes`、`MaxRecordBytes` | 8 MiB、4 MiB | 一条批量写的语句、一份记录的上限（第 10 节） |
| `DaServer:Store:BacklogPause` | 2,048 个根 | world 等着写的根超过这么多就暂停取新邮件（第 10 节） |
| `DaServer:HotRestart:FreezeBound` | 2 秒 | 热重启冻结的上限；写延迟的 p99 超过它的四分之一时拒绝武装（第 10 节） |
| `DaServer:Links:LargeInFlight`、`ReassemblyBytes` | 4、64 MiB | 每条链路同时在发的大消息数、接收方的重组缓冲（第 11 节） |
| `DaServer:Mailbox:LatestWindow` | 10 秒 | `[Latest]` 的序号表里一个发送方多久没新消息就删（第 4 节） |
| `DaServer:GroupFrameMaxBytes` | 256 KiB | 低频组的帧攒到这么大就提前交出（第 9 节） |
| `DaServer:ResyncsPerTick` | 64 | 每个 Actor 每个 tick 最多记几个 `MissedPushes`（第 7 节） |
| `DaServer:WaitingMailBytes` | 256 MiB | 全节点占位上等着的邮件的字节上限，按 world 分份（第 6 节） |
| `DaServer:Clients:MaxInFlight` | 64 | 每个客户端连接同时在途的请求数（第 7 节） |
| `DaServer:Clients:SlowClientGrace` | 10 秒 | 慢的客户端积压持续这么久才断开（第 7 节） |
| `DaServer:Clients:JoinRetryFor` | 60 秒 | 连上就加入、SDK 重新加入的重试预算（第 7 节） |
| `DaServer:Clients:RebalanceTolerance` | 10% | 网关的连接数超过组平均这么多才按速率退掉多出的（[architecture.md 第 3 节](architecture.md#retire-and-rebalancing)） |
| `DaServer:Clients:MaxConnectionAge` | 关 | 可选：连接活过这么久就 `Retire`（第 7 节；默认按负载重新平衡，不按年龄） |
| `MaxMail`、`MaxMailBytes` | 4,096、4 MiB | `[ActorOptions]`：每个 Actor 邮箱的上限（第 1 节） |
| `DaServer:EmptyRecordTtl` | 1 天 | 记录从没写成的行，最后一次占 epoch 之后过了这么久就删掉（第 10 节） |
| `DaServer:EmptyRecordSweepInterval` | 1 小时 | 承载要保存类型的最老 `Active` 成员清这种行的间隔（第 10 节） |
| `DaServer:QuietPass` | 1 秒 | 空闲的 world 也给没有 tick 的 Actor 类型跑一趟系统，`[EverySeconds]` 的系统据此按时运行（第 5 节） |
| `RetireGrace` | 10 秒 | 退役的连接多久不走就断开（ADR-026） |
| SDK 的心跳、静默断开 | 15 秒、30 秒 | ADR-053 |
| 网关对静默客户端的断开 | 45 秒 | ADR-053；之后才开始宽限期 |

`TableRefresh` 小于 `IsolationTimeout`：连得到存储的死节点在重新放置之前就读到了自己 `Dead`；连不到存储的，最后一次续租约始于死亡提交之前，租约过期就冻结，而它的槽要等 `dead_seen_at + IsolationTimeout + DeathMargin`（存储的时钟，`dead_seen_at` 晚于提交）才移走，所以别的节点激活它的键时它已经冻结；保存的键还有 epoch 比较兜底。

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
[NinoType, NinoPooled] public sealed partial class Nearby : IToClient           // pooled: Pooled<Nearby>.Rent(), returned by Push
{
    public List<NearbyEvent> Events = new();
    private static partial IEnumerable<Nearby> OnNinoPoolWarmup() => null;   // Nino's two hooks (NINO019): start empty,
    private partial void OnNinoPoolReturn() => Events.Clear();               // and clear what the next use must not see
}
[NinoType] public record struct NearbyEvent(int Id, NearbyKind Kind, Vector2 At, Vector2 Velocity);   // Id: the entity's NetId number
public enum NearbyKind : byte { Appear, Moved, Disappear }
```

```csharp
// ── Game.Server: identity, actor types, servers, messages, events ─────────────
public readonly partial record struct Account(long Id) : IClaims;                      // decoded from the token
[JoinOnConnect<Account>]                                                               // joined at connect: Account(42) -> Player(42)
public readonly partial record struct Player(long Id) : IActor<Player>
{
    [Persist] public static Prefab RootPrefab { get; } = new Prefab().SetInventory(40).SetWallet(default).SetBuffer<Slot>();
}
[TickRate(30), ActivatedBy<EnterMap>]                                                  // 30 ticks a second; only EnterMap creates a map
public readonly partial record struct Map(long Id) : IActor<Map>
{
    [Persist]                                                                          // the map's record: Grid and NetIds; drops are Loot's
    public static Prefab RootPrefab { get; } = new Prefab().SetGrid(512, 16).SetNetIds(default).SetVisitors(default).SetBuffer<Shout>();
    public static bool CanCreate(in Account who, in EnterMap m, in MapTable maps) => maps.Has(m.Map);   // before the claim:
}                                                                                      // a made-up map id writes nothing
public readonly partial record struct Loot(long Map) : IActor<Loot>                    // no [TickRate]: a map's drops as records; its
{                                                                                      // commits write Loot's record, not the map's (section 8)
    [Persist] public static Prefab RootPrefab { get; } = new Prefab();
}

[Hosts<Player>, Hosts<Loot>, Hosts<Payment>] public readonly struct PlayerServer  : IServer;   // Payment: samples/payments
[Hosts<Map>]                 public readonly struct MapServer     : IServer;
[Hosts<Connection>]          public readonly struct GatewayServer : IServer;

[Once, NinoType, From<Loot>] public record struct GrantItem(int Item) : IRequestTo<Player, bool>;   // runs once on the player
[NinoType, From<Map>] public record struct Claim(int Drop, Player By) : IRequestTo<Loot, bool>;   // the map commits nothing: Loot does
[NinoType, From<Map>] public record struct Dropped(int Drop, int Item, Vector2 At) : IMessageTo<Loot>;   // Drop: a NetId number
[NinoType, From<Map>] public record struct Restock : IMessageTo<Loot>;                  // the map came back without its drops
[NinoType, From<Loot>] public record struct Show(int Drop, int Item, Vector2 At) : IMessageTo<Map>;   // a drop Loot holds
[NinoType, From<Loot>] public record struct Gone(int Drop) : IMessageTo<Map>;          // no longer to be had: off the map
[NinoType, From<Loot>] public record struct Clash(int Item, Vector2 At) : IMessageTo<Map>;   // Loot holds that number: renumber
public record struct Strike(int Skill, EntityId Target) : IEvent;                      // a cast's resolution, after the windup

// ── Game.Server: tuning data, swapped without a restart (section 10) ─────────
[NinoType] public record struct SkillRow(TimeSpan Windup, float Reach, int Damage);
public readonly record struct SkillTable(SkillRow[] Rows) : IConfigTable<SkillTable>   // indexed by skill id
{
    public static string Name => "skills";                                             // DaServer:Tables:skills, POST /admin/tables/skills
    public static bool TryLoad(ReadOnlySpan<byte> bytes, out SkillTable table, out string? error) =>
        SkillRows.TryDecode(bytes, out table, out error);                              // the game's own decode and checks (cold path)
    public bool Has(int skill) => (uint)skill < (uint)Rows.Length;                 // a client's index is checked before use
    public TimeSpan Windup(int skill) => Rows[skill].Windup;
    public bool InRange(int skill, Vector2 from, Vector2 to) =>
        Vector2.DistanceSquared(from, to) <= Rows[skill].Reach * Rows[skill].Reach;
    public int Damage(int skill) => Rows[skill].Damage;
}

public readonly record struct MapTable(long[] Ids) : IConfigTable<MapTable>          // the maps that exist, sorted
{
    public static string Name => "maps";
    public static bool TryLoad(ReadOnlySpan<byte> bytes, out MapTable table, out string? error) =>
        MapRows.TryDecode(bytes, out table, out error);
    public bool Has(long id) => Array.BinarySearch(Ids, id) >= 0;
}

// ── Game.Server: data ─────────────────────────────────────────────────────────
// a component on a [Persist] prefab declares ITracked (section 10); Registration checks it at start
[NinoType] public record struct Grid(int Size, int CellSize) : IComponent, ITracked;   // on a map's root
[NinoType] public record struct Inventory(int Capacity) : IComponent, ITracked;    // on a player's root
[NinoType] public record struct Wallet(int Diamonds) : IComponent, ITracked;       // on a player's root: section 8's purchases
[NinoType] public record struct Slot(int Item, int Count);                            // a bag slot: a buffer element on the root
public record struct NetIds(int Next) : IComponent, ITracked;                     // on a map's root: the last drop number given out,
                                                                                        // saved at checkpoints (a crash may repeat one: Clash)
[NotSaved] public record struct Visitors(int Next) : IComponent;                        // on a map's root: avatars' and NPCs' numbers,
                                                                                        // negative, never saved (none outlives a crash)
public partial record struct NetId(Map Map, int Value) : IComponent, IUniqueIndexed<NetId>;   // what clients call an entity; Key => this is generated
public partial record struct Avatar(Map Map, Player Player) : IComponent, IUniqueIndexed<Avatar>;
public partial record struct Stash(Loot Loot, int Drop) : IComponent, IUniqueIndexed<Stash>, ITracked;  // a drop's record on Loot
public partial record struct Cell(Map Map, int X, int Y) : IComponent, IIndexed<Cell>, ITracked;   // AOI cell: many entities
                                                                                        // per cell (a non-unique index); Map keeps maps apart
public record struct Visible : IComponent;                                              // a tag: what the AOI tells about
public record struct Resnap : IComponent;                                               // a tag: crossed cells unconfirmed, owed a snapshot
public record struct Position(Vector2 Value) : IComponent, ITracked;               // tracked: CellAssign reacts to writes
public record struct Velocity(Vector2 Value) : IComponent, ITracked;               // tracked: Diff reacts to writes
public record struct NetSync(Velocity Sent) : IComponent;                               // the velocity last told to observers
public record struct Destination(Vector2 Target) : IComponent;                          // server-driven movers (npcs)
public record struct Steered : IComponent;                                              // moved by time, not by messages
public record struct Speed(float Value) : IComponent;
public record struct Carries(int Item) : IComponent;                                   // what an npc drops when it dies
public record struct Health(int Value) : IComponent;
public record struct Dead : IComponent;
public record struct Stunned : IComponent;
[NinoType] public record struct Prize(int Item) : IComponent, ITracked;           // what a drop holds: on the map and on Loot
public record struct Picking(ReplyTo<bool> Reply) : IComponent;                         // the map's plain ask's state: the client's token
[NinoType] public record struct Looting(ReplyTo<bool> Reply) : IComponent;              // the grant's ask state, kept in its ask row
                                                                                          // (so in Loot's record): the map's token
[NinoType] public record struct Granting(Player By, int Item) : IComponent, ITracked;   // promised to whom: By default = none
[NotSaved] public record struct Shout(Cell Cell, NearbyEvent Event);                    // news for one cell, on the map; empty between ticks
                                                                                        // (a buffer element: EasyEcs asks for no interface)

internal static class Prefabs                                                           // the generator reads them: at least internal
{
    static readonly Prefab BodyPrefab = new Prefab().SetPosition(default).SetVelocity(default).SetNetSync(default)
        .SetHealth(100).SetVisible();                                      // what everything walking on a map has; only a base
    [Prefab<Map>] internal static readonly Prefab AvatarPrefab = new Prefab(BodyPrefab).SetSpeed(6);   // not saved: rejoins after a restart
    [Prefab<Map>] internal static readonly Prefab NpcPrefab = new Prefab(BodyPrefab).SetSpeed(3).SetCarries(default).SetDestination(default).SetSteered();
    [Prefab<Map>] internal static readonly Prefab DropPrefab = new Prefab().SetVisible();   // what clients see: not saved, Loot shows it again
    [Prefab<Loot>, Persist]                                                // saved: Stash, Prize, Position and Granting (its AskUntilAnswered's ask is a store row of Loot's root);
    internal static readonly Prefab StashPrefab = new Prefab()             // the defaults put the four in the composition and the
        .Set(default(Stash)).SetPrize(default).SetPosition(default)        // persist set (Spawn overwrites three, test StashPrefabRoundTrip);
        .Set(default(Granting));                                           // Granting's By is default until someone is promised the drop
                                                                           // (multi-field components: EasyEcs generates SetX(fields);
                                                                           // a whole value goes through the generic Set)
}
```

```csharp
// ── Game.Server: a map's membership ───────────────────────────────────────────
[Handles<Map>]
public static partial class Maps
{
    [On<EnterMap>]                                                         // [To] Map: the subject is the map's root
    static bool Enter(in EnterMap m, in Account who, in Map map, Entity root, World world)
    {
        var key = new Avatar(map, new Player(who.Id));
        if (!world.AvatarIndex.TryGet(key, out var avatar))                // a re-sent join finds it
            avatar = root.Spawn(Prefabs.AvatarPrefab, key, new NetId(map, -(++root.Visitors.Next)));   // a number only when it spawns;
                                                                           // negative: never a drop's number
        return avatar.Join() == Status.Ok;                                 // a newer connection takes over; the previous map leaves it
    }

    [On<Activated>]                                                        // every activation: the rates are the root's live data
    static void Start(in Activated a, in Map map, Entity root, in Grid grid, World world)
    {
        Span<long> far = stackalloc long[Aoi.MaxFarKeys];
        foreach (var key in Aoi.FarKeys(in grid, far)) root.SetGroupRate(key, everyTicks: 3);   // far cells: one frame every 3 ticks
        if (a.Origin != Activation.Migrated)                               // from its record, or Created again by a crash before
        {                                                                  // the first record: no drops here, Loot shows its own
            root.Send(new Loot(map.Id), new Restock());                    // a map's first Created: its Loot holds
            return;                                                        // nothing, so the Restock shows nothing
        }
        foreach (var e in world.Descendants(root))                         // migrated: [NotSaved] Visitors arrives as 0, its avatars
            if (e.TryGetNetId(out var id) && id.Value < 0)                 // and NPCs with their numbers: go on below the lowest
                root.Visitors.Next = Math.Max(root.Visitors.Next, -id.Value);
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
        if (me.IsPending<Strike>() || !skills.Has(m.Skill)) return;      // one cast at a time; an unknown skill index
                                                                           // would throw and reload the map (section 5)
        if (!world.NetIdIndex.TryGet(new NetId(map, m.Target), out var target) || !target.HasHealth) return;    // a client's number:
                                                                           // any entity on this map, so only one that can be hurt
        me.Schedule(new Strike(m.Skill, target.Id), after: skills.Windup(m.Skill));   // Zero resolves right after this handler
    }

    [On<Strike>, Without<Dead>, Without<Stunned>]                          // died or got stunned during the windup: not called
    static void Resolve(in Strike s, in Position at, in SkillTable skills, World world)
    {
        if (!skills.Has(s.Skill) || !world.TryGetEntity(s.Target, out var target) || target.IsDead || !target.HasHealth)
            return;                                                          // the table may have swapped during the windup
        if (!skills.InRange(s.Skill, at.Value, target.Position.Value)) return;   // out of range: dodged
        ref var hp = ref target.Health;
        hp.Value -= skills.Damage(s.Skill);
        if (hp.Value <= 0) target.SetDead();                               // structural change last: hp is not used after it
    }
}

// ── Game.Server: drops on the map, what clients see (not saved) ───────────────
[Handles<Map>]
public static partial class Pickups
{
    const float Reach = 2;

    [On<Clash>]                                                            // subject: the map's root; a number repeated after a
    static void Redrop(in Clash c, in Map map, Entity root, ref NetIds ids) =>   // crash is Loot's already: the next one
        root.Send(new Loot(map.Id), new Dropped(++ids.Next, c.Item, c.At));

    [On<Show>]                                                             // subject: the map's root; a re-shown drop is found again
    static void Place(in Show s, in Map map, Entity root, World world)
    {
        var id = new NetId(map, s.Drop);
        if (!world.NetIdIndex.TryGet(id, out _)) root.Spawn(Prefabs.DropPrefab, id, new Prize(s.Item), new Position(s.At));
    }

    [On<PickUp>]                                                           // subject: the client's avatar; replies later
    static void Take(in PickUp m, in Map map, in Account who, in Position at, in ReplyTo<bool> reply, Entity me, World world)
    {
        if (!world.NetIdIndex.TryGet(new NetId(map, m.Drop), out var drop) || !drop.HasPrize || !drop.IsVisible    // a client's
            || Vector2.DistanceSquared(at.Value, drop.Position.Value) > Reach * Reach)   // number: a drop, seen, in reach
        {
            me.Reply(in reply, false);
            return;
        }
        me.Root.Ask(new Loot(map.Id), new Claim(m.Drop, new Player(who.Id)), new Picking(reply));   // from the root: the drop
    }                                                                      // may be gone before the answer comes

    [On<Replied<Claim, Picking>>]                                          // subject: the map's root; past the client's deadline
    static void Picked(in Replied<Claim, Picking> a, Entity root)          // nothing is sent
    {
        if (a.TryGetValue(out var ok)) root.Reply(in a.State.Reply, ok);  // Loot's answer, ahead of Loot's Gone (section 1)
        else if (a.Status.NotExecuted) root.Reply(in a.State.Reply, false);   // Loot never ran it: not taken
    }                                                                      // TimedOut, Faulted: no reply, the client's call times
                                                                           // out: unknown (the item may be granted), never "false"

    [On<Gone>]                                                             // subject: the map's root; it comes after Loot's
    static void Remove(in Gone g, in Map map, World world)                 // answer to that Claim (section 1)
    {
        var id = new NetId(map, g.Drop);
        if (!world.NetIdIndex.TryGet(id, out var drop)) return;
        Aoi.Vanish(drop, in id);                                           // its cell hears Disappear, then it goes
        drop.Destroy();
    }
}

// ── Game.Server: drops as records, what pays out (section 8) ─────────────────
[Handles<Loot>]
public static partial class Stashes
{
    [On<Dropped>]                                                          // subject: Loot's root
    static void Add(in Dropped d, in Loot loot, Entity root, World world)
    {
        if (world.StashIndex.TryGet(new Stash(loot, d.Drop), out _))       // the map repeated a number after its crash:
        {                                                                  // Loot keeps the drop it holds under it
            root.Send(new Map(loot.Map), new Clash(d.Item, d.At));
            return;
        }
        root.Spawn(Prefabs.StashPrefab, new Stash(loot, d.Drop), new Prize(d.Item), new Position(d.At));
        root.Send(new Map(loot.Map), new Show(d.Drop, d.Item, d.At));        // the map shows only what Loot holds
    }

    [On<Restock>]                                                          // subject: Loot's root; the map lost its drops
    static void Reshow(in Restock r, in Loot loot, Entity root, World world)
    {
        foreach (var e in world.Descendants(root))
            if (e.TryGetStash(out var key) && e.TryGetPrize(out var prize))
                root.Send(new Map(loot.Map), new Show(key.Drop, prize.Item, e.Position.Value));
    }

    [On<Claim>]                                                            // subject: Loot's root; replies later
    static void Award(in Claim c, in Loot loot, in ReplyTo<bool> reply, Entity root, World world)
    {
        var key = new Stash(loot, c.Drop);
        if (!world.StashIndex.TryGet(key, out var stash))                  // unknown here (lost before a checkpoint):
        {
            root.Reply(in reply, false);
            root.Send(new Map(loot.Map), new Gone(c.Drop));                // the map stops showing it
            return;
        }
        if (stash.Granting.By == c.By) return;                             // this player's retry while its grant is in flight: no
                                                                           // answer, the call times out: unknown, never "false"
        if (stash.Granting.By != default || !stash.TryGetPrize(out var prize))   // already promised to someone else
        {
            root.Reply(in reply, false);
            return;
        }
        stash.SetGranting(c.By, prize.Item);                               // promised: written with the ask, which is
        stash.AskUntilAnswered(c.By, new GrantItem(prize.Item), new Looting(reply));   // asked again after a crash
    }

    [On<Replied<GrantItem, Looting>>]                                      // subject: the stash
    static void Granted(in Replied<GrantItem, Looting> a, in Loot loot, in Stash key, Entity stash)
    {                                                                      // comes once: the player's value or its definite
        var taken = a.TryGetValue(out var ok) && ok;                       // refusal, never a link outcome (section 8)
        stash.Reply(in a.State.Reply, taken);                              // answers the map's Claim, ahead of Gone (section 1);
        if (!taken)                                                        // the token's deadline may have passed: nothing sent
        {                                                                  // the bag was full, or the player refused for sure
            stash.Set(default(Granting));                                  // the drop stays for someone else
            return;                                                        // this item commits; the acknowledgement follows
        }
        stash.Send(new Map(loot.Map), new Gone(key.Drop));                 // granted: the drop goes once this is written
        stash.Destroy();
    }
}

// ── Game.Server: a player's side ──────────────────────────────────────────────
[Handles<Player>]
public static partial class Items
{
    [On<GrantItem>]                                                        // subject: the player's root; [Once]: runs once per
    static bool Grant(in GrantItem m, in Inventory inv, in DynamicBuffer<Slot> bag, Entity root)   // sequence number, a re-ask
    {                                                                      // gets the stored answer, after the write
        if (bag.Count >= inv.Capacity) return false;                       // full: a re-ask gets false too
        root.WriteSlot().Add(new Slot(m.Item, 1));
        return true;                                                       // [Once] commits: the answer leaves once written
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
        var cell = Aoi.CellOf(map.Map, p.Value, map.Grid.CellSize);       // the generated accessor of a tracked type: ref readonly
        e.SetIfChanged(cell);                                              // only a crossing writes Cell (adds it the first time)
    }
}

[UpdateInGroup<Map>, UpdateAfter<CellAssign>]
public class Arrive : EcsSystem
{
    [ForEach, Changed<Cell>, With<Visible>]                                // spawned, shown or crossed into a cell: its cell hears it
    static void Appear(Entity e, in Cell cell, in NetId id, in Position p) =>
        e.Root.WriteShout().Add(new Shout(cell, Aoi.Appeared(e, in id, in p)));   // with its Velocity if it has one (a drop has none)
}

[UpdateInGroup<Map>, UpdateAfter<Arrive>]
public class Regroup : EcsSystem
{
    [ForEach, Changed<Cell>, With<Presence>]                               // a presence's first cell or a crossing: the near
    static void Crossed(Entity avatar, in Cell cell, in DynamicBuffer<Subscribed> before, World world)   // groups of the 3×3
    {                                                                      // cells, the far ones of the ring
        Span<ClientGroup> around = stackalloc ClientGroup[Aoi.MaxAround];
        var now = Aoi.Around(avatar.Root, in cell, around);                // root.Group(key) per cell
        var joined = avatar.Has<Joined>();
        if (joined)                                                        // a crossing: what the cells new to it hold (a join's
            avatar.Push(Aoi.Snapshot(world, now, before.AsReadOnlySpan()));   // full state is SendFullState's)
        avatar.SetGroups(now);                                             // the framework syncs the gateway
        if (!joined) avatar.SetResnap();                                   // unconfirmed (a join, a handover): SendFullState
    }                                                                      // sends it at confirmation; structural change last
}

[UpdateInGroup<Map>, UpdateAfter<Regroup>]
public class SendFullState : EcsSystem
{
    [ForEach, Changed<Joined>]                                             // joined, reconnected or taken over: confirmed this tick
    static void Send(Entity avatar, in Joined joined, in Cell cell, World world)
    {
        var resnap = avatar.IsResnap;                                      // crossed cells while unconfirmed
        if (!joined.MissedPushes && !resnap) return;                       // handed over by a gateway upgrade: nothing missed
        Span<ClientGroup> around = stackalloc ClientGroup[Aoi.MaxAround];
        var now = Aoi.Around(avatar.Root, in cell, around);                // near and far, as Regroup subscribes them
        avatar.Push(Aoi.Snapshot(world, now, default));                    // a pooled Nearby of every subscribed cell (nothing
                                                                           // before), from world.CellIndex.Find; Find(c).Count
                                                                           // (O(1)) skips the empty ones
        if (resnap) avatar.RemoveResnap();                                 // structural change last
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
    // removals happen only in items or here, so Aoi.Snapshot (earlier this tick) skips exactly what this removes
    [ForEach, With<Leaving>]                                               // leaving now; the framework destroys it after the user's systems
    static void Disappear(Entity avatar, in NetId id) => Aoi.Vanish(avatar, in id);

    [ForEach, With<Dead>, With<Steered>]                                   // an npc killed this tick: its drop, then off the map
    static void Fall(Entity npc, in NetId id, in Position p, in Carries loot)
    {
        Aoi.Vanish(npc, in id);
        var root = npc.Root;
        root.Send(new Loot(id.Map.Id), new Dropped(++root.WriteNetIds().Next, loot.Item, p.Value));   // NetIds is tracked: written through WriteNetIds
        npc.Destroy();                                                     // structural change last
    }
}

[UpdateInGroup<Map>, UpdateAfter<Vanish>]
public class Flush : EcsSystem
{
    [ForEach, Changed<DynamicBuffer<Shout>>]                               // only maps whose shouts were written this tick
    static void Publish(Entity map, ref DynamicBuffer<Shout> shouts)       // the clear is this system's own write: not a change
    {                                                                      // for its next run, so the map does not come back
        Aoi.Publish(map, shouts.AsReadOnlySpan());                         // one Nearby per cell with news, pushed to the cell's
                                                                           // near group and to its far group (every 3 ticks)
        shouts.Clear();
    }
}
```

NPC 死亡由 `Vanish.Fall` 处理：它的格子听到 `Disappear`，地图从根上的 `NetIds` 取一个编号，直接发 `Dropped` 告诉 `Loot`，然后销毁 NPC。别的原因让 NPC 离开地图时（例子里没写出），也一样先 `Aoi.Vanish` 再 `Destroy()`。被打断的前摇：眩晕的处理方法里 `me.Cancel<Strike>()`。

客户端连上时由 `[JoinOnConnect<Account>]` 加入 Player；进地图的 `EnterMap` 直接发给地图，不经过 Player。Player 只回答 `Loot` 的 `GrantItem`。

**保存的是什么**：地图的根（`Grid`、`NetIds`）在地图的记录里，地图上的掉落、Avatar 和 NPC 都不在。掉落的经济状态在 `Loot(地图的键)` 的记录里：每个掉落一个 `Stash`（`Prize`、`Position`，以及许给谁的 `Granting` 和发物品的那次 `AskUntilAnswered`）。地图每次不是迁移来的激活（从记录激活：宕机、`Reload`、停用之后；或者宕机在第一份记录之前，地图以 `Created` 回来）都向 `Loot` 发 `Restock`，`Loot` 把还有的掉落再 `Show` 一遍（新地图的 `Loot` 什么也没有，什么也不发）；迁移和热重启带着地图的全部实体，不用这一步（热重启不再激活）。地图的根仍然要保存，只为 `NetIds`：它只给掉落编号，从最后一份记录接着往下发，所以宕机只会重复上一个检查点之后发出的编号。重复的编号撞上 `Loot` 还记着的掉落时，`Stashes.Add` 留下 `Loot` 已有的，回 `Clash`，地图用下一个编号再报一次（`Redrop`），所以每个重复都由它补上，掉落都会再显示出来。Avatar 和 NPC 不保存，编号取自另一段：根上 `[NotSaved]` 的 `Visitors` 发的负数，从不和掉落的编号相撞，也不进记录（Avatar 进出地图不让根多写一次记录）。宕机之后它们都不在了，`Visitors` 从 0 重新数也不会重复；热重启交接的字节带着它；迁移的字节里没有它，`Maps.Start` 在 `Activated(Migrated)` 时从迁来的最小负编号接着数。客户端重新加入，Avatar 重新建出来。Player 的根（`Inventory`）在 Player 的记录里。`Shout` 在两个 tick 之间是空的，标了 `[NotSaved]`。所以 `persist` 里的用户类型是 `Grid`、`NetIds`（`Visitors` 是 `[NotSaved]`，不在里面）、`Inventory`、`Wallet` 和背包的 `DynamicBuffer<Slot>`（Player 的根）、`Stash`、`Prize`、`Position`、`Granting`（`Loot` 的掉落）；它们都在声明上写了 `ITracked`（`DynamicBuffer<Slot>` 本身就是）。发物品的问的状态 `Looting` 存在它的问的行里（第 8 节），和记录一样要读回来：所以它照记录的规则写成 `[NinoType]`，里面 `ReplyTo<bool>` 的期限在记录里按 UTC 时刻存，不按内存里的剩余时间（第 8 节的 `ReplyTo<T>`），原样拷一份原始布局不够。`Enter` 先按 `AvatarIndex` 找，找不到才 `Spawn`，编号只在新建时取（它是 `Spawn` 的参数），所以重发的加入找到已有的 Avatar，不白花编号。

AOI 的状态全在组件里：客户端看到的编号是 `NetId`，格子是 `Cell`（非唯一值索引，`world.CellIndex.Find(cell)` 列出格子里的实体），上次发出的速度是 `NetSync`，确认和有没有漏过推送是框架的 `Joined(MissedPushes)`，订阅是 `Subscribed`，消息是地图上的 `DynamicBuffer<Shout>`。`Aoi` 是用户写的无状态帮助类；`Aoi.Vanish(e, in id)` 在 `e` 有 `Cell` 时往那个格子写一条 `Disappear`（没有格子就没人见过它）。客户端把 `Appear` 和快照当作写入（已经知道的实体就更新），所以重复的不出错。`Aoi.Snapshot` 跳过 `Vanish` 这个 tick 要移走的实体：带 `Leaving` 的，和同时带 `Dead`、`Steered` 的（被打死的 NPC；被打死的 Avatar 不离开地图，照样在快照里）。`Regroup` 和 `SendFullState` 跑在 `Vanish` 之前，这些实体这个 tick 的 `Disappear` 送到格子的组时，新订阅者还不是组员（订阅在收尾时才同步），快照带上它们，客户端就留下一个幽灵。每个系统只做一件事：
1. `CellAssign`：位置被写过的实体，算出格子；跨格才写 `Cell`。
2. `Arrive`：`Cell` 被写过的可见实体（新建的、`Show` 出来的掉落、跨了格的），往新格子写一条 `Appear`，带位置，有 `Velocity` 的再带速度。所以先加入的客户端也看得到之后才出现的掉落和 NPC、走进视野的实体。
3. `Regroup`：`Cell` 变了的 Presence，改写它订阅的 `ClientGroup`：3×3 格子的近处组，外面一圈格子的远处组（`root.Group(格子的键)`，键带着这张地图）。已经确认的 Avatar 跨格时，先推一份新订阅的格子（不在原来的 `Subscribed` 里的）的 `Aoi.Snapshot`：那些格子里早就在的实体不会再喊 `Appear`。还没确认的 Avatar（刚加入，或者 `Joined` 在交接中被去掉）跨格时推不了，`Regroup` 给它标 `Resnap`。框架的 `SyncSubscriptions` 按这份数据同步网关（第 9 节），所以用户只写一处。远处组的频率是 `Maps.Start` 在每次激活时写的数据（`root.SetGroupRate(key, everyTicks: 3)`，第 9 节）：它们的推送攒 3 个 tick 交出一帧。离开视野不用消息：客户端按位置和速度外推，实体走出自己订阅的格子就丢掉。
4. `SendFullState`：这个 tick 确认的 Avatar（`Joined` 被写过：第一次加入、断线后重连、被别的连接接管），`MissedPushes` 为真或者带着 `Resnap` 才推一份完整的状态：它订阅的全部格子，3×3 的近处和外面一圈的远处（`Aoi.Around`，和 `Regroup` 一样，只是没有原来的订阅），所以远处一直没动的实体也看得到；推完去掉 `Resnap`；因网关升级换连接、其间也没跨格的，一个推送也没漏，不推。
5. `Diff`：速度被写过、并且和 `NetSync` 不同的实体，写一条 `Moved` 进地图的 `Shout`。
6. `Vanish`：带 `Leaving` 的 Avatar 和这个 tick 被打死的 NPC，往它的格子写一条 `Disappear`；NPC 还留下掉落、被销毁。`Pickups.Remove` 销毁掉落之前也这样写。
7. `Flush`：`Shout` 被写过的地图（`[Changed<DynamicBuffer<Shout>>]`，没消息的地图不访问），按格子合并，每个有内容的格子给它的近处组和远处组各推一份 `Nearby`，然后经 `ref` 清空。清空是 `Flush` 自己这一趟的写入，EasyEcs 不把它算作 `Flush` 下一趟的变更（`guide/change-tracking.mdx` 的 "Ticks"），所以没有新消息的地图下一个 tick 不再被访问。

所有系统都只看带 `In<Map>` 的实体（`IActor<Map>` 带着的组过滤，第 5 节）。`Steering`、`Movement` 和 `Moves.Move` 用 `Mut<T>`（第 5 节的"只在值变了时写"）：站着的 NPC 和被拒绝的一步都不打戳，`CellAssign` 和 `Diff` 就不访问它们。除了 `Steering` 都是单线程：`Movement` 每行只是一次加法和一次比较，其余的发消息、写别的实体或做结构变更。`Steering` 也只在节点的 world 比核少时才多线程（第 5 节）。

**捡东西分在两个 Actor 上**：按 tick 走的地图只管看得见的掉落：`Take` 校验客户端给的编号（是掉落、看得见、够得着），再从地图的根普通地问 `Loot`（`Claim`），地图从不 `Commit`。不按 tick 走的 `Loot` 管经济状态（第 8 节推荐的写法），照第 8 节转移价值的规则：`Award` 把 `Granting`（许给谁、什么）写进 `Stash`，同时 `AskUntilAnswered` Player 的 `[Once] GrantItem`（两者一起写成，问才离开）；`Granting` 的 `By` 不是默认值的掉落就是"已经许给了别人"（`Granting` 在 prefab 上带着默认值，所以它进记录，第 10 节）。Player 的 `Grant` 按框架的序号执行一次、`Commit` 之后才回答。`Loot` 在回答之前宕机：`Stash`、`Granting` 和那次问从记录恢复，`DueScan` 唤醒它，框架用同一个序号接着问；Player 认出这个序号，回答存下的回复，不发第二次。`Granted` 只在有回答时被调用（值，或者 Player 确定的拒绝），所以重问时链路上的结果永远不会让 `Loot` 把已经发出去的掉落放给别人。`Stash` 没了，`Loot` 发 `Gone`，地图删掉看得见的掉落；`Loot` 不认识的编号（`Dropped` 在 `Loot` 的检查点之前随它宕机丢了）也回 `Gone`。`Loot` 对 `Claim` 的回答和之后的 `Gone` 是同一个发送方发给同一个目标的，按发送顺序到达（第 1 节的保证 2），所以客户端先拿到回答，地图再删掉掉落（测试 `ClaimAnswerArrivesBeforeGone`）；问是从地图的根发的，掉落先没了也不带走等待实体。

NPC 也由用户建：`root.Spawn(Prefabs.NpcPrefab, new NetId(map, -(++root.Visitors.Next)), new Carries(item))`，和 Avatar 一样从 `Visitors` 取一个负的编号；`Carries` 是它死时留下的东西。

### 生成的代码

```csharp
// <auto-generated/> Game.Server: Key => this for every partial IActor<T> / IUniqueIndexed<T>- or IIndexed<T>-of-itself record struct
partial record struct Map    { Map IIndexed<Map>.Key => this; }        // explicit: a public Key would recurse in ToString()
partial record struct Loot   { Loot IIndexed<Loot>.Key => this; }
partial record struct Player { Player IIndexed<Player>.Key => this; }
partial record struct Avatar { Avatar IIndexed<Avatar>.Key => this; }
partial record struct Cell   { Cell IIndexed<Cell>.Key => this; }      // non-unique: world.CellIndex.Find(cell)
partial record struct NetId  { NetId IIndexed<NetId>.Key => this; }
partial record struct Stash  { Stash IIndexed<Stash>.Key => this; }
```

```csharp
// <auto-generated/> Game.Server: typed sends on Entity and the typed reply value, one class per message
public static class GrantItemSends
{
    public static Pending<GrantItem> Ask(this Entity from, Player to, in GrantItem message,
        TimeSpan deadline = default) =>                                    // answer: Replied<GrantItem>; deadline default: AskTimeout
        Runtime.Ask(from, in to, in message, deadline);                    // into Outbox (held there if this item commits)

    public static Pending<GrantItem> Ask<TState>(this Entity from, Player to, in GrantItem message, in TState state,
        TimeSpan deadline = default) where TState : struct, IComponent =>  // answer: Replied<GrantItem, TState>
        Runtime.Ask(from, in to, in message, in state, deadline);

    extension(Replied<GrantItem> a)                                        // C# 14 extension block: the reply's type comes from the request
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);   // reads the reply bytes in the mail's frame
    }

    extension<TState>(Replied<GrantItem, TState> a) where TState : struct, IComponent
    {
        public bool TryGetValue(out bool value) => Runtime.TryRead(in a, out value);
    }
}
// The receiver is in the signature; the sender is checked where it is called: inside [Handles<K>] or [UpdateInGroup<K>],
// K must be in [From<…>] (here Loot), else the analyzer reports it; elsewhere a Debug build asserts the root's actor type.
// Schedule<T> and one IsPending<T>()/Cancel<T>() group (T : struct; the analyzer requires an IEvent or an IRequest, the
// marker every IRequestTo<,> inherits) are the framework's own extension methods; Schedule and Ask both return Pending<T>.
```

```csharp
// <auto-generated/> Game.Server: each [On<T>] method with component parameters as an EasyEcs per-entity job, inside its
// partial class; one without them is called directly
partial class Maps
{
    // Enter has no component parameter: no job (EasyEcs's Run takes one to nine components). The dispatch tests what
    // Run would: the subject is alive, and a [With<T>]/[Without<T>] on the method would add subject.Has<T>() tests here.
    internal static void On_Enter(Entity subject, in Mail mail, ref Outcome outcome)
    {
        if (!subject.IsAlive) { outcome.Skipped(); return; }
        var message = mail.Read<EnterMap>();
        var who = mail.Claims<Account>();                                  // the sending client's claims, not the subject's
        var map = mail.Target<Map>();                                      // the item's actor key, not a component of the subject
        outcome.Reply(Enter(in message, in who, in map, subject));        // Join() put the Route in Outbox ahead of this reply
    }
}

partial class Moves
{
    internal struct Move_Job : IForEachEntity<Position, Velocity, Speed>, INoStructuralChanges   // Entity only pushed to
    {
        public static readonly QueryFilter Filter = QueryFilter.Create().Without<Dead>();   // [Without<Dead>], built once
        public MoveTo Message;
        public static int WriteMask => 0b011;                                 // Position, Velocity: Mut<T> refs move with the row
        public static int SelfStamped => 0b011;                            // Mut<T> stamps on SetIfChanged, not on the call
        public void Execute(Entity e, ref Position p, ref Velocity v, ref Speed speed) =>
            Move(in Message, e, new Mut<Position>(ref p, e), new Mut<Velocity>(ref v, e), in speed);   // Mut(ref value, entity): a write stamps e's row
    }

    internal static void On_Move(Entity subject, in Mail mail, ref Outcome outcome)
    {
        var job = new Move_Job { Message = mail.Read<MoveTo>() };
        if (!subject.Run<Position, Velocity, Speed, Move_Job>(ref job, Move_Job.Filter)) outcome.Skipped();   // no copy, no write-back
    }
}
// Combat.Cast, Pickups.Place and Remove, Stashes.Add, Reshow and Award have no component parameter either (keys,
// claims and singletons such as SkillTable are arguments): called directly, as Enter. Of the jobs, Resolve adds Dead to
// another entity, Take asks (a waiting child of the root), Granted destroys: none gets INoStructuralChanges; Picked has no
// component parameter and only replies. Items.Grant_Job
// reads Inventory and the bag (in) and appends through root.WriteSlot(), then returns its reply: no structural change; Maps.Start_Job (SetGroupRate on the buffer the root copy carries,
// section 9, and a Send) and Pickups.Redrop_Job only send: they get it.
// GrantItem is [Once]: the dispatch checks the sender's sequence number against the player's OnceSenders rows and Unacked
// buffer first (stale, duplicate with its stored reply, or new); the first run commits implicitly (the root's WriteNow,
// the tick-end write) and its reply, stored in Unacked on the root copy (no structural change), is held until that row's
// CAS. Award's AskUntilAnswered writes a queued ask row of Loot's root (it records the stash) with the item and commits; the row takes
// the next number from Loot's RootState in the write that sends it, and the GrantItem ask leaves once that write lands.
// Map commits nothing: only its checkpoints write its record.
```

```csharp
// <auto-generated/> Game.Server: a C# 14 static extension member in the project's root namespace, so the call is
// Registration.Create() (GameServerRegistration.Create() where two server projects' namespaces are imported, section 5);
// one container per assembly (named after Game.Server, not after a server type); no static registries, no module initializers
namespace Game.Server;

public static class GameServerRegistration
{
    extension(Registration)
    {
        public static Registration Create() => new Registration()
            .Actor<Map>(static a => a                                      // root prefab: Map.RootPrefab, persistent; the runtime finds a Map
                .ActivatedBy<EnterMap, Account, MapTable>(Map.CanCreate))  // [ActivatedBy<EnterMap>] and its CanCreate (message, claims, table),
                                                                           // run before the claim; ActivatedBy<TMessage, TClaims> without a table
                                                                           // by world.Index<Map, Map>(); also adds the framework's closed
                                                                           // OrderedPhase<Map>, Depart<Map>, SyncSubscriptions<Map>
            .Prefab<Map>(Prefabs.AvatarPrefab)                             // a registered copy with In<Map> and a ChildOf column
            .Prefab<Map>(Prefabs.NpcPrefab)                                // the static prefabs stay as written
            .Prefab<Map>(Prefabs.DropPrefab)
            .Actor<Loot>()                                                 // root prefab: Loot.RootPrefab, persistent; no tick
            .Prefab<Loot>(Prefabs.StashPrefab, persist: true)              // the copy also carries Persisted
            .Actor<Player>(static a => a                                   // root prefab: Player.RootPrefab, persistent
                .Once())                                                   // it receives a [Once] request: its root copy carries an empty
                                                                           // Unacked buffer (1,024 entries, window 16); OnceSenders are store rows
            .Tracked(static set => set                                     // every component type of this compilation declaring ITracked
                .With<Grid>().With<NetIds>().With<Inventory>().With<Wallet>()   // (section 10), plus the buffers it uses; the
                .With<Stash>().With<Prize>().With<Position>().With<Cell>() // framework's persisted types (RootState, Unacked,
                .With<Velocity>().With<Granting>().With<DynamicBuffer<Slot>>(),   // WakeAt) are in the runtime's own filter;
                changed: static f => f                                     // Registration checks the run-time persist set; packages add theirs
                    .ChangedAny<Grid, NetIds, Inventory, Wallet, Stash, Prize>(0)   // eleven in two balanced calls (six, five);
                    .ChangedAny<Position, Cell, Velocity, Granting, DynamicBuffer<Slot>>(0)   // none is [NotSaved]
                    .With<Persisted>(),                                    // only rows of persistent entities tag their root
                removed: static f => f                                     // a removal of any of them tags the root Unsaved too
                    .RemovedAny<Grid, NetIds, Inventory, Wallet, Stash, Prize>(0)
                    .RemovedAny<Position, Cell, Velocity, Granting, DynamicBuffer<Slot>>(0)
                    .With<Persisted>())
                                                                           // DynamicBuffer<Shout> is [NotSaved]: tracked by EasyEcs, not here
            .ConfigTable<SkillTable>()                                     // loaded from the cluster's current version, swapped live
            .ConfigTable<MapTable>()                                       // Map.CanCreate reads it
            .Claims<Account>()
            .JoinOnConnect<Player, Account>(static (in Account c) => new Player(c.Id), Takeover.NewestWins)
            .Dispatch<Map>(static table => table
                .Activated(Maps.On_Start)
                .Client<EnterMap>(Maps.On_Enter)                           // [To]: subject is the root; may Join
                .Client<MoveTo>(Moves.On_Move)                             // subject: the presence
                .Client<CastSkill>(Combat.On_Cast)
                .Client<PickUp>(Pickups.On_Take)
                .Message<Show>(Pickups.On_Place)
                .Message<Gone>(Pickups.On_Remove)
                .Message<Clash>(Pickups.On_Redrop)
                .Replied<Claim, Picking>(Pickups.On_Picked)
                .Event<Strike>(Combat.On_Resolve))
            .Dispatch<Loot>(static table => table
                .Message<Dropped>(Stashes.On_Add)
                .Message<Restock>(Stashes.On_Reshow)
                .Request<Claim>(Stashes.On_Award)
                .Replied<GrantItem, Looting>(Stashes.On_Granted))
            .Dispatch<Player>(static table => table
                .Once<GrantItem>(Items.On_Grant))                          // [Once]: the sequence number checked first
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
                world.AddSystem<Arrive>();
                world.AddSystem<Regroup>();
                world.AddSystem<SendFullState>();
                world.AddSystem<Diff>();
                world.AddSystem<Vanish>();
                world.AddSystem<Flush>();
            })                                                             // the node runs Ordering, Update<Map>(), Settling at 30 Hz; Cleanup per world
    }
}
```

```csharp
// <auto-generated/> AppHost, from the actor / hosted-by lines of Game.Server's wire.manifest
namespace Game.Server
{
    public readonly struct PlayerServer  : IServer;
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
actor   Game.Server.Map     id 5A1C93E1  key long    hosted-by MapServer   tick 30Hz  activated-by EnterMap can-create
actor   Game.Server.Loot    id 1D84B6A9  key long    hosted-by PlayerServer
actor   Game.Server.Player  id 0B77D012  key long    hosted-by PlayerServer  join-on-connect Account newest-wins
actor   DaServer.Connection id 3E0F7A21  key DaServer.ConnectionId raw(16){Hi:ulong@0:raw(8),Lo:ulong@8:raw(8)}  hosted-by GatewayServer
claims  Game.Server.Account  long
request Game.Server.GrantItem  Loot -> Player  reply bool  once      # [Once]: adding or removing it changes semantics
request Game.Server.Claim      Map -> Loot  reply bool
message Game.Server.Dropped    Map -> Loot
message Game.Server.Restock    Map -> Loot
message Game.Server.Show       Loot -> Map
message Game.Server.Gone       Loot -> Map
message Game.Server.Clash      Loot -> Map
event   Game.Server.Strike   layout 7C21A0F3
state   Game.Server.Looting  for GrantItem  layout 2C8D41F6
state   Game.Server.Picking  for Claim  layout 9B3E52C0
table   Game.Server.SkillTable  skills  format 6A3E0D71   # the table type's layout: the diff sees a format change
table   Game.Server.MapTable    maps    format 3B90C4E2
prefab  Map     Game.Server.Map.RootPrefab          id 2F61A0C7  composition 0C5B7A12  persist
prefab  Map     Game.Server.Prefabs.DropPrefab      id 7D2E10A4  composition 3A6C81F2
prefab  Map     Game.Server.Prefabs.AvatarPrefab    id 51B3E9F0  composition 2D70C6E5
prefab  Map     Game.Server.Prefabs.NpcPrefab       id C40A6B12  composition 5E9D27B4
prefab  Loot    Game.Server.Loot.RootPrefab         id 64C2F9A1  composition 1F4D8B63  persist
prefab  Loot    Game.Server.Prefabs.StashPrefab     id B5E0A37C  composition E27A5C09  persist
prefab  Player  Game.Server.Player.RootPrefab       id 9E07D355  composition 6B19E4F3  persist
record  Game.Server.Grid       nino  layout 19E4B2D8      # the persist set, one line per user type (the framework's own
                                                           # persisted types are fixed by the DaServer version, not listed)
record  Game.Server.NetIds     raw   layout 3D5A0B19      # raw: a layout change fails the build (DAS017), wherever the type is used
record  Game.Server.Inventory  nino  layout 82C7F015
record  Game.Server.Wallet     nino  layout 5D3A90E7
record  Game.Server.Slot       nino  layout 0E6A93D4      # DynamicBuffer<Slot> on the player's root
record  Game.Server.Stash      raw   layout A04C7E52
record  Game.Server.Prize      nino  layout 6E12C4A7
record  Game.Server.Position   raw   layout 5C19F2B0      # also on avatars and drops, not saved: the layout is frozen there too
record  Game.Server.Granting   nino  layout 4F0B6C2D      # the intent on a stash
push    Game.Protocol.Nearby    pooled
push    Game.Protocol.SnapBack
client  Game.Protocol.EnterMap   -> Map  to Map  reply bool
client  Game.Protocol.MoveTo     -> Map
client  Game.Protocol.CastSkill  -> Map
client  Game.Protocol.PickUp     -> Map  reply bool
on      Game.Server.GrantItem    Player
on      Game.Server.Claim        Loot
on      Game.Server.Dropped      Loot
on      Game.Server.Restock      Loot
on      Game.Server.Show         Map
on      Game.Server.Gone         Map
on      Game.Server.Clash        Map
on      Game.Server.Strike       Map
replied Game.Server.GrantItem Looting  Loot
replied Game.Server.Claim Picking  Map
activated Map
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
    Assert.That(map.PresenceOf(a).IsPending<Strike>(), Is.True);                // Deliver ran the cast at once: its Strike waits
    map.Deliver(new MoveTo(new Vector2(5, 0), default), client: b);             // one step within Speed 6, beyond Slash's reach of 2
    map.Advance(Skills.Table.Windup(Skills.Slash));
    map.Tick();

    Assert.That(map.PresenceOf(b).Position.Value, Is.EqualTo(new Vector2(5, 0)));   // the step was taken, not snapped back
    Assert.That(map.PresenceOf(b).Health.Value, Is.EqualTo(100));
    Assert.That(map.PresenceOf(a).IsPending<Strike>(), Is.False);               // the Strike ran (and missed), nothing left pending
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

[Test]
public void A_grant_is_asked_again_after_a_crash()
{
    using var loot = new TestActor<Loot>(new Loot(7), Registration.Create());  // no tick rate: each Tick runs one batch
    loot.Deliver(new Dropped(9, Catalog.Sword, default), from: new Map(7));
    loot.Deliver(new Claim(9, new Player(42)), from: new Map(7));
    Assert.That(loot.Outbox.Asks<GrantItem>(), Is.Empty);                      // held: Award committed
    loot.Tick();                                                                // the tick-end write lands, the ask leaves

    var first = loot.Outbox.Asks<GrantItem>(to: new Player(42)).Single();
    loot.Crash();                                                               // memory gone: back to the last record
    loot.Advance(DaServerDefaults.OnceRetry);                                   // the ask row's next try: the framework asks again
    loot.Tick();
    var again = loot.Outbox.Asks<GrantItem>(to: new Player(42)).Last();
    Assert.That(again.Sequence, Is.EqualTo(first.Sequence));                    // same number: the player runs it once

    loot.Answer<GrantItem>(Status.TimedOut);                                    // a link outcome: never reaches Granted,
    loot.Tick();                                                                // the drop stays promised
    Assert.That(loot.Outbox.Sends<Gone>(), Is.Empty);
    loot.Answer<GrantItem>(true);                                               // the player: granted (its Unacked entry)
    loot.Tick();                                                                // Granted commits: Gone leaves after the write
    Assert.That(loot.Outbox.Sends<Gone>(to: new Map(7)), Has.Count.EqualTo(1)); // the Claim's token outlived its deadline
}                                                                               // during the crash: Gone alone
```

- **`TestActor<K>` 只给通用的东西**：`Root`、`SetTable`（换上一张配置表）、`Connect`（一个测试客户端：连接和身份）、`Deliver`（当场执行这一项：处理方法和它触发的即时链跑完才返回，同有序阶段执行一项；系统要等 `Tick`；服务器之间的消息带 `from:`）、`Tick`、`Advance`、`Outbox`、`PresenceOf(client)`（返回这个客户端在本 Actor 上的 Presence 实体，`Entity`）、`Answer<TReq>`、`FailNextWrite`、`Crash`（丢掉内存，像宕机一样从最后一份记录重新激活）、`AllocatedBytes`、`AssertParallelMatchesSerial`。游戏自己的帮助方法（例如 `SpawnNpcs`、"某个客户端的 NetId"）是测试工程里对 `TestActor<Map>` 的扩展方法，测试用的数据（`Skills.Table`、技能编号 `Skills.Slash`、`Catalog.Sword`）也在测试工程里。
- **路由在测试台里当场确认**：`Connect` 和 `Deliver` 像网关一样确认 `Join()` 追加的路由，所以加入的 Presence 当场带上 `Joined(MissedPushes: true)`，快照系统照常反应；`Outbox` 里照样看得到路由。
- **`Commit` 在测试台里**：`Tick` 结束时做这个 tick 的写记录（测试台的记录在内存里，写当场成功），留住的输出随后进 `Outbox`；`FailNextWrite()` 让下一次写出错，重试和 `Reload` 照第 8、10 节走；`Crash()` 测宕机之后的 `AskUntilAnswered`；`Answer<TReq>(value)` 或 `Answer<TReq>(status)` 给还在等的 `TReq` 投递一个回答：回复的值，或者 `TimedOut`、`Faulted` 这样的结果，不用另起一个接收方（`AskUntilAnswered` 的请求照第 8 节：链路上的结果让框架再问，不到用户的 `Replied`）。
- **`Skipped` 在测试台里分两种**：主体缺少组件参数是代码错误，`Deliver` 抛出；过滤不满足（`[Without<Dead>]`）是有意的跳过，照常记成 `Skipped`。
- **`map.AllocatedBytes(action)`**：执行一次 `action`（静态 lambda，不捕获），返回分配的字节数。Actor 里有 `Parallel = true` 的方法时用 `GC.GetTotalAllocatedBytes(precise: true)`，否则用 `GC.GetAllocatedBytesForCurrentThread()`（EasyEcs `guide/testing.mdx`）；所以测试按 fixture 串行跑。
- **`map.AssertParallelMatchesSerial(ticks)`**：给一种服务器跑的 world 比核少的部署（第 5 节）。测试台的 world 按这种部署建（`MaxDegreeOfParallelism` 用全部核）。`MaxDegreeOfParallelism` 在建 world 时定下（init-only），`Restore` 只收同一个 world 的快照（别的 world 的抛 `ArgumentException`），所以测试台自己带一个串行的孪生 world（`MaxDegreeOfParallelism = 1`），收到同样的输入：
  1. 主 world `Capture()` 存下起点，跑 `ticks` 帧，记下 `Outbox` 和整棵子树 `SaveEntitiesWith` 的字节；
  2. `Restore(起点)`，再跑 `ticks` 帧，和第 1 步比较：并行方法有数据竞争时，两次多线程的结果就可能不同；
  3. 孪生 world 跑同样的 `ticks` 帧，和第 1 步比较：串行和并行一致。

### 运行时的路径

每一跳经过的队列、索引和帧见 [architecture.md 第 3 节](architecture.md#3-messaging)。

```
① 客户端连 gate-0      框架在 gate-0 激活 Connection(n1)（n1 是这个连接的 ConnectionId，c1 是它的 ClientId），加上 Account(42) 和连接戳 S1；第一条邮件是 Activated
② 连上就加入           [JoinOnConnect<Account>]：向 Player 42@game-1 发加入请求（带 S1）；Player 建 c1 的 Presence（戳更大的胜出），回复 P1
                      Connection 写路由 (Player 42, P1, 槽 0)，发 JoinConfirmed(P1)；推 Ready，放出排队的消息
③ EnterMap(7)         Connection(n1)@gate-0：过滤（帧头）→ 路由表：带 [To]，目标 Map 7@map-0；直接转发，信封带着 c1 的上下文（身份、S1、调用 id）
④ Enter               Map 7 的有序阶段：主体是根；AvatarIndex 没找到，Spawn 建出 Avatar（新建的带 NetId）；Join() 当场返回 Ok（P2），Enter 返回 true
                      Outbox 里给 gate-0 的帧先是 Route(Map 7, P2)，再是回复；阶段结束时交给链路
⑤ Connection(n1)      写路由，槽 1；原来有 Map 3 的路由就删掉，Map 3 对旧 Presence Leave()；向 Map 7 发 JoinConfirmed(P2)；
                      给 SDK 发 JoinConfirmed(槽 1, 调用 id)，再交回 true：SDK 记下 EnterMap(7) 是槽 1 的加入消息
⑥ JoinConfirmed(P2)   Map 7：Avatar 带上 Joined(MissedPushes: true)；推时间通知（33 ms、tick 号、服务器时间）；
                      Regroup 已写好 Subscribed，SendFullState 按 Changed<Joined> 推完整状态，收尾的 SyncSubscriptions 把订阅集合发给网关
⑦ MoveTo              不带 [To]：Connection(n1) → c1 的 Map 路由 → Map 7 的邮箱；下一个 tick 的有序阶段取出：主体是 Avatar，校验这一步，当场改位置
⑧ CastSkill           同上；Cast 校验后 caster.Schedule(Strike, after: 前摇)，带着 Due 进到期堆；IsPending<Strike> 拒绝第二次施法
⑨ Strike 到期         tick 窗口所在那一帧的有序阶段，和消息按 (时间, 序号) 归并：读目标此刻的位置，够得着才扣血；走开了就是闪避
⑩ PickUp(9)           主体是 Avatar：Take 校验 9 号是看得见、够得着的掉落，地图的根向 Loot 7@game-0 普通地 Ask Claim（状态里是客户端的令牌）；
                      Loot 7：Award 找到 Stash 9，写上 Granting(Player 42)，AskUntilAnswered GrantItem（框架写一行问，发出的那次写给它 Loot 7 的下一个序号 s，
                      隐含 Commit：这一批之后的写写成，问才离开）；Player 42：s 不在 Unacked、大于 OnceSenders 里 Loot 7 的 n，Grant 改背包，回复记进 Unacked，
                      隐含 Commit，写成后回答 true；Loot 7 的 Granted：回复 Claim，向 Map 7 发 Gone(9)，销毁 Stash（问的行属于 Loot 的根，已随这个回答删掉）；回复和 Gone 是 Loot 7
                      发给 Map 7 的，按这个顺序到达：Map 7 先回复客户端 true，再删掉看得见的掉落。写成后框架发累计确认，Player 删掉 s 这一项。
                      game-0 在回信前宕机：DueScan 按 next_due 唤醒 Loot 7，Stash 和 Granting 从记录、问的行从存储恢复，到了再问的时刻框架用 s 再问；
                      Player 在 Unacked 里认出 s，回答存下的 true，不发第二次
⑪ 每 33 ms 一个 tick   Ordering（OrderedPhase）→ Update<Map>()：Steering、Movement、CellAssign、Arrive、Regroup、SendFullState、Diff、
                      Vanish、Flush → Settling：Depart、SyncSubscriptions、去掉 Restarting/Resumed；只遍历带 In<Map> 的表 → Cleanup：ReleaseRoutes 等（按 world）；
                      Nearby 序列化一次 → 有观察者的网关各一帧 → 各个连接
⑫ map-0 宕机          map-1、gate-0 探测不到它，两条怀疑把它写成 Dead（还活着的话读到就自己停下）；一个 TableRefresh 后槽表重新放置；Map 7 在 map-1 建占位（键和等着的邮件都在它上面），占到新的 epoch，记录装进占位，它就是根：
                      Grid 和 NetIds 恢复，掉落、Avatar 和 NPC 不在记录里；Activated(Loaded)（没写过记录时是 Created）向 Loot 7 发 Restock，Loot 7 把还有的掉落 Show 回来；
                      Connection(n1) 看到 map-0 成了 Dead：删路由，PresenceLost(槽 1) 给 SDK；SDK 重发 EnterMap(7) → Enter 新建 Avatar，Join → 同 ④–⑥
⑬ map-0 热重启        武装（写武装文件）；触发器重启这一对里停着的实例，它以新版本起来当热备、在管道上监听；节点握手通过，在自己的行写
                      restart_until（其他成员看到 NodeRestarting），最后一个 tick 根上带 Restarting；冻结，每个 world 在自己的线程上流进管道，
                      监听的套接字一起交过去；gate-0 把发给 map-0 的帧留在重发窗口；热备加载、确认，旧进程交出套接字、原地停着；热备拿到锁，读到自己的行
                      还是 Active，清掉 restart_until，以同一个身份重连、续上流；第一个 tick 带 Resumed；Avatar、Presence、路由都没变，
                      客户端只看到一次零点几秒的停顿
⑭ c1 与 gate-0 的连接半开（gate-0 还没发现断线），c1 换到 gate-1 重连
                      新的 Connection(n2)（同一个 ClientId c1）带更大的戳 S2（签发序号相同，SDK 的连接序号更大）：连上就加入，接管 Player 上的 Presence；Ready 之后 SDK 重发 EnterMap(7)：
                      AvatarIndex 找到原来的 Avatar，Join() 按戳接管（新的 PresenceId，去掉 Joined 直到确认）；gate-0 上的旧路由按旧的 PresenceId 释放；
                      目标向 gate-0 发 TakeOver(n1, S2)（Presence 记着 gate-0 和 n1，gate-0 按 n1 的哈希找到它的 world）：签发序号相同，旧的 Connection(n1) 安静地关闭；确认后 Joined.MissedPushes 为真，SendFullState 重推
⑮ gate-1 宕机          Player 42 和 Map 7 上 c1 的 Presence 加上 Detached（宽限 30 秒），不推送；c1 经负载均衡器连到 gate-0，
                      戳更大，接管两个 Presence，去掉 Detached；没回来的，宽限期过后 Leave()
```

---

## 13. 编译期检查

诊断编号沿用今天的 DAS001–022 里还适用的那些（检查的规则不变，套在新的写法上），退役的编号不再发，新的检查用新编号（从 DAS023 起；逐个编号见路线图 W3）。

**编译期检查**（下面任何一种情况都会报错）：
- 发送方向写错：接收方不对没有对应的方法；`[Handles<K>]` 类或 `[UpdateInGroup<K>]` 系统里发送，K 不在消息的 `[From<…>]` 里（`DaServer.Topics` 的四条消息除外，第 4 节）。
- 含 `[On<T>]` 的类没有 `[Handles<K>]`，或者不是 `static partial class`（可一键修复）。
- 服务器之间的消息的接收方不是类上的 K；客户端消息没有处理方法，或者被两个 Actor 类型处理。
- 消息（`IFromClient`、`IToClient`、`IMessageTo<K>`、`IRequestTo<K, R>`、`ITopicMessage`）没有 `[NinoType]`。
- `[On<Incoming>]` 不在 `[Handles<Connection>]` 里。
- 一个请求有两个处理方法；`Replied` 的（请求类型, 状态类型）组合重复。
- 请求的处理方法既不返回回复，也不取 `ReplyTo<T>`；或者既返回回复，又取 `ReplyTo<T>`；`Reply` 的值类型和令牌的 `T` 不同。
- `[On<T>]` 的 T 是框架的 tag 或组件（`Joined`、`Leaving`、`Detached`、`Deactivating`、`Restarting`、`Resumed`）：它们不是消息，修复是"写一个 `[ForEach, With<T>]` 的系统"。
- 主体一定是根的处理方法（带 `[To]` 的客户端消息、`[JoinOnConnect]` 类型的客户端消息、服务器之间的消息、`Activated`）取 `Presence`、`Joined` 这类只在 Presence 上的组件参数或过滤。组件参数在主体的 prefab 上没有，由 `Registration` 构造时报错（第 5 节）。
- 带 `[To]` 的客户端消息找不到对应的 `Route`，或者 `Route` 的身份类型不是工程的 `IClaims`（第 4 节）；`PresencesPerClient` 大于 1 的 Actor 类型处理不带 `[To]` 的客户端消息（第 7 节）；`ClientGroup.Everyone` 这个保留键用在 `root.Group(…)` 里（第 9 节）。
- `IActor<T>`、以自身作键的 `IUniqueIndexed<T>`、`IIndexed<T>` 不是 `partial`（DAS025，可一键修复，第 3 节）。
- 分析器看得出是要保存的 prefab（同一个编译里用字面的 `SetX` 链搭的 `[Persist]` prefab）上有没写 `ITracked` 的用户组件类型，或者元素类型声明在服务端工程和 DaServer 包以外的 `DynamicBuffer<T>`（DAS027，第 10 节；看不出的由 `Registration` 在启动时抛出）。
- 该用要保存的根、用了不保存的（DAS031，第 8 节）：`root.Commit()`、`root.Delete()`、`root.IssueClientToken`、`AskUntilAnswered` 用在根不保存的 Actor 类型上，或者在看得出不保存的实体上调用；根不保存的 Actor 类型处理或发出 `[Once]` 的请求（`[From<K>]` 的 K 根不保存也算）。凡是"该存的根没存"都只报 DAS031，不同时报 DAS032。
- `[Latest]` 写在请求、推送或 Topic 的消息上（DAS030，第 4 节）。
- `[Once]` 的写法和发法（DAS032，第 8 节）：写在不是请求（`IFromClient<R>`、`IRequestTo<K, R>`）的类型上；回复不是不超过 32 字节的定长值（存不进 `Unacked`）；处理方法取 `ReplyTo<T>`（存下的回复要和改动一起写成）；`[Once]` 的服务器请求用 `Ask` 发出，或者从 `ICluster`、`[Stateless]` 发出；`AskUntilAnswered` 发的不是 `[Once]` 的请求。
- `Join()` 的返回值没有用到。
- 客户端消息有多个 `[To]` 参数；`[To]` 参数的类型和处理 Actor 类型的键形状不一致；`[Handles<Connection>]` 处理的消息带 `[To]`。
- 身份参数（`IClaims` 类型）用在客户端消息以外的处理方法里；`[UpdateInGroup<K>]` 系统的方法取 `IClaims` 参数（第 5 节；Actor 类型参数 `in K` 可以取，它只匹配根）。
- `[On<T>]` 方法违反 EasyEcs `[ForEach]` 的规则：DAS026，说明里写出对应的 EasyEcs 规则和链接（第 5 节；包括方法体里跨过结构变更的 `ref`，EECS047，原来的 DAS022），外加 `Maybe<T>` 参数（job 只收必有的组件）。
- 用户代码写 Actor 类型组件，或者把 Actor 类型组件放进 prefab、`CreateEntity`。
- Actor 里调用 `CreateEntity`（用 `root.Spawn`）；对看得出不是根的实体调用 `Deactivate()`。
- 承载 `Connection` 的服务器类型在 AppHost 里没有 `WithClientConnections`；消息引用的 Actor 类型没有服务器类型承载（AppHost 构建时检查，第 2 节）。
- Actor 里建出的实体上，有值索引的组件（`IUniqueIndexed<T>`、`IIndexed<T>`）第一个字段不是 Actor 类型（第 3 节）。
- 有索引的组件作为 `ref` 或 `Mut<T>` 参数。
- `Parallel = true` 的 `[ForEach]` 方法里调用 `Entity` 上的 DaServer 操作（第 5 节的操作表）。结构变更、并行、有索引的组件，EasyEcs 另有 EECS010、EECS013、EECS015。
- `[On<T>]` 方法或 Actor 类型组里的系统以 `ref` 取 `IWorldSingleton<T>` 以外的单例；用户的单例类型既不实现 `IConfigTable<T>` 也不实现 `IWorldSingleton<T>`（第 5 节）。
- 没有 `[TickRate]` 的 Actor 类型组里，`[ForEach]` 方法既没有 `[Changed<T>]`、`[Removed<T>]`、过滤 tag 的 `[With<T>]`，系统也没有 `[EverySeconds(n)]`：每批邮件一趟全扫（第 5 节）；这种组里的系统设 `UpdateInterval`（它数的是更新次数，不是时间，改用 `[EverySeconds]`）。
- 系统方法取 Actor 类型参数 `in K`，其余组件参数不全在 `K.RootPrefab` 上（`Registration` 构造时抛出，第 5 节；分析器看得出时提前报）。
- `CanCreate` 的消息不在同一个类型的 `[ActivatedBy]` 里；`CanCreate` 除了身份和消息取了不止一张配置表，或者取了配置表以外的参数；有 `CanCreate` 形状（`static bool`，取身份和一条 `[ActivatedBy]` 的消息）的静态方法名字不是 `CanCreate`（第 4 节）。
- `Schedule(new T())` 的 T 在任何 `[Handles<K>]` 里都没有 `[On<T>]`；`Remind<T>` 的 T 没有 `[On<Reminded<T>>]`，或者调用所在的类不是 `[Handles<K>]`/`[UpdateInGroup<K>]`、T 也没有 `[On<Reminded<T>>]` 可推出 K，或者推出的 K 的根不保存（`DaServer.Reminders` 自己的分析器，第 15 节）。
- 服务端工程引用的、不是 DaServer 包的程序集里有写了 `ITracked` 的组件类型：它不进任何一份追踪表（DAS029，第 10 节）。
- 没有 `[TickRate]` 的 Actor 类型调用 `root.SetGroupRate(key, everyTicks: n)`（第 9 节）。
- 一种服务器上各帧率不能都整除最快的那个。
- `IClaims` 类型超过 16 字节或不是非托管类型；一个工程有两个 `IClaims` 类型。
- `[JoinOnConnect<C>]` 的 Actor 类型的键和 C 的形状不同；一个 Actor 类型写了两个 `[JoinOnConnect]`。
- Protocol 里出现 Actor 类型、服务器之间的消息、`ActorId`、`PresenceId` 或 `EntityId`；Protocol 里含集合的消息不是 `[NinoPooled]` 类。
- Actor 类型的键超过 16 字节，或者不是非托管类型。
- 生成器要读的 prefab（`[Prefab<K>]` 字段、`RootPrefab`）不是至少 `internal`；`[Persist]` 写在既不是 `RootPrefab` 也没有 `[Prefab<K>]` 的字段上，或者 `RootPrefab` 上写了 `[Prefab<K>]`（第 3 节）。
- 根不保存的 Actor 类型有带 `[Persist]` 的子实体 prefab；看得出要保存的 prefab 建在不保存的实体下面（第 10 节）。
- 进记录的组件类型是托管的却没有 `[NinoType]`；按原始字节进记录的组件改了布局（DAS017，第 10 节的序列化规则）。
- 对看得出要保存的实体 `Set` 或 `Remove` `persist` 里的 tag（W1 核对 tag 的增删会打戳之前；看不出的地方不报，DAS028，第 10 节）。
- `root.Delete()` 返回 `Ok` 的路径上（比较了 `== Status.Ok`）还有 `Send`、`Ask`、`AskUntilAnswered`、`Push`、`Schedule`、`Remind`：删除中的根不能再问或发（DAS033，第 5 节）；只在 `Outstanding` 的分支里重新 `Remind` 的（秒杀订单的 `Close`）不报。
- `[Stateless]` 的 Actor 类型里 `Ask`、把 `ReplyTo<T>` 存进组件、`Join()`、建 `Due` 的 `Schedule`（带 `after`，或在系统里）。
- `[Stateless]` 的 Actor 类型、`Connection` 或根不保存的 Actor 类型上 `Subscribe`：它们不占 epoch，Topic 认不出它们，订阅也存不下（DAS023，第 15 节的 `DaServer.Topics`）。
- `[ActorOptions]` 的值是负数。
- 要迁移的托管组件、托管的事件或 `Ask` 状态没有 Nino 格式化器（第 10 节的序列化规则）。
- 用户的泛型 `[ForEach]` 系统只在别的程序集里封闭（EasyEcs 要在运行时 `MakeGenericType`，Native AOT 下不行，第 10 节）。

**警告和提示**（不报错）：
- 处理方法和系统里读 `DateTime.UtcNow`、`DateTime.Now`：改读 `Time.Utc`（警告，第 9 节）；
- 带 `[To]` 的客户端消息发给一条 `[ActivatedBy]` 都没写的 Actor 类型：它只到已有记录的键，没有任何消息能建出这个类型的键（DAS024，警告，第 4 节；写了 `[ActivatedBy]` 的类型，别的 `[To]` 消息只到已有记录的键是有意的，不报）；
- `Schedule(…, after:)` 的常量比 Actor 类型的 `IdleTimeout` 长：它让 Actor 一直激活着，跨停用的定时用 `DaServer.Reminders`（提示，第 15 节）；
- 处理方法里结构变更之后还有 `throw` 或可能抛出的调用：先校验，最后做结构变更（第 5 节）；
- 追踪变更的组件作为 `ref` 参数，却不是每条路径都写它：改用 `in` 或 `Mut<T>`，不写就不打戳（第 5 节；第 12 节的 `Moves.Move`）；
- `Replied` 处理方法里没有延后就重新 `Ask`（普通请求）：改成 `Schedule(…, after: a.RetryAfter)` 再问，不在对方忙的时候立刻压上去（第 11 节）；
- `Replied` 或 `[Once]` 的处理方法在循环里调用 `AskUntilAnswered`：一次回答问不定多少次，一个卡住的下游会让排着的问越积越多，改成有限次的调用（DAS034，警告，第 8 节）；
- 在 Presence 实体上 `Ask`：Presence 离开时等待实体随它销毁，回信丢弃；要收尾的从根或者不会离开的子实体问（第 8 节）；
- 看不清 `root.Delete()` 是否返回了 `Ok` 的路径上（没有看返回值，或者比较藏在调用里）还有 `Send`、`Ask`、`AskUntilAnswered`、`Push`、`Schedule`、`Remind`：真是 `Ok` 时运行时会让这一项按 `Reload` 丢掉（DAS033 的提示，第 5 节；看得清的路径上是错误，上面）；

**启动时检查**：配置的服务器类型不存在，或者和 `wire.manifest` 对不上，节点启动失败。热备收到的字节不合格时拒绝它，节点就地恢复（第 10 节）。

---

## 14. 已定的取舍，以及待办

本稿没有需要拍板的项。下面是起草时比较过的做法，以及按需求（通用、ECS、解耦、DRY、易维护、分布式正确）选定的理由。

1. **身份用用户自己的类型 `Account : IClaims` 加 `[JoinOnConnect<Account>]`**，不用框架包装 `Claim<Player>`。框架是通用的，不是每个应用都有 Player（例如秒杀的买家）；身份绑在某个 Actor 的键上，令牌格式会随那个键变。身份是一个接口（`IClaims : IComponent`），不是特性：EasyEcs 和 C# 的泛型约束都看得见它（`[JoinOnConnect<T>] where T : struct, IClaims`），生成器不用补任何东西。代价是 `Account` 和 `Player` 的字段写两遍，由分析器保证形状相同。
2. **客户端路由由 `[On<T>]` 所在的 `[Handles<K>]` 类推出**，不另写路由表。同一件事只写一处；改动路由的版本要连网关一起滚动升级，热重启由 `wire.manifest` 比对拦住（第 10 节）。
3. **`[From<K>]` 保持必写**（唯一的例外是 `DaServer.Topics` 包自己的四条消息，第 15 节）。"从类型就看得出谁发给谁"是目标之一；新的发送方要改消息声明，这正是想要的显式。
4. **消息不做成实体**。邮箱、到期堆和即时链在有序阶段归并三个来源。把每条消息做成带 `Due` 的实体能统一表示，但每条消息多一次建实体和销毁；到期堆和链栈已经给出同样的顺序，而延后事件本来就是实体（要保存、迁移）。所以不做。
5. **EasyEcs 4.7 提供本稿依赖的功能**（清单在开头）。EasyEcs 评估后放弃的：并行批量方法、`LastWriteTick`、公开读写集、多 world 共享并行预算（D36）。重构的第一步是 [W1](../roadmap.md)：`EasyEcsVersion` 升到 4.7；本稿依赖、要先核对的 EasyEcs 编译和行为（每一项一个测试）只列在那里。
6. **改代码只有热重启，只有一个做法：热备交接**：不用 `AssemblyLoadContext`（AOT 加载不了程序集，卸载不干净会悄悄泄漏，只换得了逻辑），不用 `MetadataUpdater`（开发工具，Release 构建关着），不写快照文件（进程启动的 0.3–2 秒都落在停顿里）；理由见 ADR-084 和 ADR-098，做法见第 10 节；Kubernetes 上的形态见 ADR-085。
7. **能是数据的都是数据**（ECS 优先于 OOP）：停用、热重启前后、离开、断线、确认都是根或 Presence 上的 tag 或组件（`Deactivating`、`Restarting`、`Resumed`、`Leaving`、`Detached`、`Joined`），由系统反应，不是回调或虚方法；要不要保存是实体上的 `Persisted`；待处理的个数、`WriteNow`、`OnceNext`、`Unacked`、`NextDue`、组的发送频率在根上；一直问到有回答的请求是存储里根的问的行；节点状态（`RestartingNodes`，以及内部的存储降级、激活名额、各目标节点的在途上限）是节点单例；`Join()` 的结果是值。只有 `Activated` 是邮件：它必须排在占位上等着的邮件之前。保留的：
   - 单例 `Current`（当前项的上下文）：它是 world 的数据，由运行时在每项之前写；改成每个操作显式传参，每个 `Send`/`Ask`/`Join` 都要多一个参数，换不来别的；
   - `Registration` 的构建写法：它是生成的，只在启动时跑一次，写出封闭类型给 AOT；
   - `ICluster` 和 `TestActor<K>`：它们在 world 外面（HTTP、测试），不是 Actor 里的逻辑。
8. **对 EasyEcs 的需求**（EasyEcs 仓库的 issue；提出了、还没转交给 EasyEcs，由用户转交）：
   - (b) 公开读一个 prefab 带的组件（`prefab.Components`，一个 `ComponentSet` 或类型列表）：`Registration` 据此在运行时算 `persist` 和组成哈希（第 10 节）。
   - (c) 不靠特性把系统放进组（`AddSystem(system, group)` 或 `AddSystem<TSystem, TGroup>()`）：框架的泛型系统按 Actor 类型分组就不用生成封闭的子类（第 5 节）。
   - (d) 一个特性，把框架的方法标成"job 形状"（参数按 `IForEachEntity` 绑定），让 EasyEcs 的分析器自己对 `[On<T>]` 方法报 EECS002/011/012/013/015（和提示 025/026）：DAS026 就删掉（第 5 节）。

   需求和证据另成一份，交用户转交；DaServer 从不自己改 EasyEcs。追踪写在源码里（第 10 节），所以运行时按类型打开追踪（#27）和在运行时决定打戳（(a)）这两条不再需要，撤回。

   本稿依赖的已有行为：加上一个被追踪的组件算一次写入，去掉它记下一次移除（EasyEcs `reference/itrackchanges.mdx`）；写了 `ITracked` 的 tag 是不是一样，W1 核对（第 10 节）。

   EasyEcs 文档里的模式照着用，不另造：祖先遍历（`e.Root`），固定步长和追帧上限（EasyEcs `guide/hosting.mdx` 的 "A fixed step that catches up"，按 Actor 类型套用，[architecture.md 第 2 节](architecture.md#the-shard-loop)的循环）。延迟补偿的历史环（样例要做命中判定时用）是 DaServer 自己的做法。
9. **存盘是游戏的惯例：定时成批地写，下线、停机、迁移和热重启之前必写；贵重的改动 `Commit`；订单是自己的 Actor，转移价值的那一步是 `[Once]`，照 TCP 去重和确认**（ADR-097、ADR-099）。框架不对所有请求做按调用 id 的恰好一次：Orleans、Akka、Dapr 都没有，支付平台（Stripe、Google Play、PlayFab、Nakama）都按订单去重再对账。要去重的只是 `[Once]` 的那一步：序号由框架分配，接收方存下回复，累计确认就删，窗口限住每个发送方，所以占用只和在途的操作有关，用户不写键。`Commit` 搭在 tick 末本来就有的那次批量写上，不多一次存储往返；要扛住宕机一直问下去的，是 `AskUntilAnswered`：请求本身就是写进记录的数据。
10. **宕机后的唤醒（`WakeAt`、`NextDue`、`DueScan`、`IActorStore.DueKeys`）在运行时，不在 `DaServer.Reminders`**：`AskUntilAnswered` 也要它，而运行时不能依赖扩展能力（ADR-045）。`NextDue` 只由运行时按 `WakeAt` 算；`AskUntilAnswered` 和 `DaServer.Reminders` 只写 `WakeAt`、登记到时的处理（第 10、15 节）。
11. **保存按实体的种类，不按组件类型**（第 10 节，ADR-082）：同一个组件类型（`Prize`、`Position`）既在要保存的 `Stash` 上，也在不保存的掉落和 Avatar 上；按类型保存，要么 `Stash` 恢复后缺数据，要么 Avatar 被拖进记录。
12. **对照别的 ECS 之后保留的**（flecs、Bevy、Unity Netcode for Entities、Photon Quantum、Overwatch、SpatialOS）：
    - 一个 world 里有很多 Actor：系统按表（archetype）一趟走完这个 world 上同类型的所有 Actor，不是一个 Actor 一个 world（Overwatch 一局一个 world，所以单例能一局一份；这里的单例规则见第 5 节）；
    - 对数据的反应是对 tag 反应的系统（`Leaving`、`Deactivating`、`Restarting`），不是 observer：flecs 自己的建议是"能用系统解决的，就该用系统"（[Observers manual](https://www.flecs.dev/flecs/ObserversManual.html)），系统成批、有固定的位置和顺序；
    - 邮件和事件只有一个按 `(时间, 序号)` 的全序（第 1 节），比 Bevy 按帧缓冲的 message 更严：同一帧里的先后不会因为读的系统排在哪而变；
    - 格子是一个组件的非唯一值索引（`Cell`），不是每个格子一个 tag 或关系：那样每个格子一种表，实体跨格就换表，表越分越碎（flecs [relationships 手册](https://www.flecs.dev/flecs/Relationships.html)讲的碎片问题）；
    - 换代码是热备交接，不是动态加载：Bevy 因为不健全弃用了动态插件（[bevy_dynamic_plugin](https://docs.rs/bevy_dynamic_plugin)），理由同 ADR-084。

---

## 15. 扩展能力（可选包）

每一项是一个独立的包、自己的命名空间，不引用就没有（ADR-045、ADR-090）；运行时不依赖它们。写法和框架本身一样：数据是组件、tag 和单例，逻辑是系统，热路径不分配。业务功能（匹配、组队、排行榜、下单和支付）不在这里：它们是样例，也是 `dotnet new` 模板（ADR-091）。

**一对多发送只有三种，各管一件事**（选哪一个只看发给谁）：
- **Actor 发给很多 Actor**：`DaServer.Topics`（下面），例如价格变动通知各个城市。
- **Actor 发给很多客户端**：`ClientGroup`（第 9 节；整个 Actor 是 `root.Everyone`），例如格子里的移动、频道里的聊天推给在线的人。
- **成员表本身有规则**（公会、队伍的加入、权限、上限）：一个固定键的 Actor，成员表是它的组件，由它转发（第 9 节的"小组"）；它推给成员的客户端时照样用 `ClientGroup`。

### `DaServer.Reminders`：扛得住宕机的提醒

```csharp
public record struct DailyReset;                                            // a marker type: which reminder
root.Remind<DailyReset>(Quests.NextMidnightUtc(time.Utc), every: TimeSpan.FromDays(1));   // in a Guild handler ([Persist] root, §3); key: 0
[On<Reminded<DailyReset>>] static void Reset(in Reminded<DailyReset> r, ref Quests q) => q = default;   // r.UtcAt: which firing
```

- 提醒是 Actor 里的一个子实体，带持久化的 `Reminder<T>(ActorId Actor, long Key, TimeSpan Period)`，下一次触发的时刻就是它的 `WakeAt`；T 是用户的标记类型，`Key` 区分同一种的几个提醒（"十分钟后提醒我"可以有两个；不写是 0）。唯一索引的键是 `(Actor, Key)`，不含 `Period`（`Reminder<T>` 的 `Key` 属性返回 `new ReminderKey(Actor, Key)`，不是 `this`：否则换了周期就找不到原来的提醒），以所在 Actor 的 `ActorId` 开头（同第 3 节"子实体的键以 Actor 类型开头"：包里的泛型类型写不出用户的 Actor 类型，`ActorId` 起同样的作用），所以同一个 world 里两个 `Guild` 各有自己的 `DailyReset`。每个 Actor 每个 (T, 键) 一个。完整的签名是 `root.Remind<T>(DateTime at, TimeSpan every = default, long key = 0, bool move = false)`：`every` 不写就是只提醒一次，`key` 和 `move` 都可以不写；它按唯一索引找，没有再 `Spawn`；已经有、周期也一样时不动它（不挪时刻，所以在 `[On<Activated>]` 里每次都调用也不会跳过一次已经到期、还没投递的提醒），周期不同或者写了 `move: true` 才改成新的时刻和周期；`root.CancelReminder<T>(key: 0)` 销毁它（取消统一叫 `Cancel`：`Cancel<T>()`、`CancelReminder<T>()`）。处理方法收到的 `Reminded<T>(long Key, DateTime UtcAt)` 带着键。
- 提醒的实体是要保存的实体：Reminders 的生成器按编译里每一对 (Actor 类型 K, T) 写一个要保存的 prefab（K 从 `Remind<T>` 调用所在的 `[Handles<K>]` 或 `[UpdateInGroup<K>]` 类推出，或者从 `[On<Reminded<T>>]` 处理方法所在的 `[Handles<K>]` 推出；哪个都推不出时分析器报错，第 13 节）（`[Prefab<K>, Persist]`，带封闭的 `Reminder<T>` 和 `WakeAt`），所以 `persist` 集合、组成哈希和 `wire.manifest` 的 `prefab` 行里每一对各有一个（测试 `ReminderClosedPerRemindT`）。只有根要保存的 Actor 类型能 `Remind`（第 10 节：要保存的实体只能建在要保存的实体下面），例子里的 `Guild` 就是（第 3 节）。
- **激活着的时候**：运行时按 `WakeAt` 挂的 `Due`（第 10 节的唤醒）到期时，这一项交给包登记的唤醒处理：它按 `(时间, 序号)` 给根投递 `Reminded<T>(Key, UtcAt)`（`UtcAt` 是这一次的 `WakeAt`）；处理方法返回后，同一项里 `WakeAt` 前进到第一个还没到的时刻（`Period` 为 0 就销毁这个子实体），所以前进和处理方法的改动总在同一份记录里。包没有自己的挂载系统；从记录加载时已过的时刻在第一个 tick 执行。
- **停机之后只补一次**：Actor 停了好几个周期（宕机、停用、整个集群停机）之后，过了时刻的提醒只投递一次，`WakeAt` 直接前进到第一个将来的时刻，不按错过的周期一个个补。
- **没激活的时候**：运行时按 `WakeAt` 算出根上的 `NextDue`，检查点把它写进记录的 `next_due` 列，`DueScan` 到时激活这个 Actor（第 10 节的唤醒）。所以提醒能叫醒一个停用了的 Actor，节点宕机也不丢。包不写 `NextDue`。
- **至少一次，按 (提醒, `UtcAt`) 去重**：`WakeAt` 的前进和处理方法的改动在同一份记录里落定。宕机落在两次写记录之间时，重新激活后同一个 `UtcAt` 再执行一次；同一个激活里不会。
- 和 `Schedule(after)`、`WakeAt` 的分工：持久的时间只有一个机制，`WakeAt`（第 8、10 节）；提醒是建在它上面的一层，把"到时"变成一封邮件、自己前进周期，所以不另有一套时间：

  | | `e.Schedule(ev, after:)` | `AskUntilAnswered`（运行时，第 8 节） | `DaServer.Reminders` |
  |---|---|---|---|
  | 写法 | 一个事件，`[On<T>]` 处理 | `e.AskUntilAnswered(to, request)`，回答在 `[On<Replied<T>>]` | `root.Remind<T>(at, every)`，`[On<Reminded<T>>]` 处理 |
  | 时间 | 单调时间，精确到项 | 重试按 `RetryAfter` 和退避；宕机后精度是 `DueScan` 的间隔（秒级） | UTC，同 `WakeAt` |
  | 停用、宕机 | 丢失（迁移、热重启不丢） | 留在记录里，到时激活 Actor 接着问 | 同 `WakeAt` |
  | 投递 | 最多一次 | 请求在接收方恰好一次（`[Once]`），回答投递一次 | 至少一次，按 (提醒, `UtcAt`) 去重，周期自己前进 |
  | 用途 | 技能前摇、buff、普通请求的延后重问 | 发物品、入账、下单的每一步 | 每日重置、拍卖结束、订阅续费 |

### `DaServer.Topics`：Actor 之间的发布订阅

```csharp
[NinoType] public record struct PriceChanged(int Item, long Price) : ITopicMessage;   // handled by any subscribing actor type
me.Subscribe(new Topic(Feeds.Prices));                                     // me: an entity of the subscribing actor
me.Publish(new Topic(Feeds.Prices), new PriceChanged(item, price));
[On<PriceChanged>] static void Reprice(in PriceChanged m, ref Shop shop) { … }   // in the subscriber's [Handles<K>] class
// the package's own messages, all one-way to the Topic: Subscribe, Unsubscribe, Publish and
[NinoType] public record struct NotSubscribed(ActorId Actor, long Epoch) : IMessageTo<Topic>;   // sent by the subscriber's shard
```

- `Topic(long Id)` 是包里内置的 Actor 类型，根要保存，根上是订阅者表 `DynamicBuffer<Subscriber>`（`Subscriber(ActorId Actor, long Epoch)`，`Epoch` 是订阅者那次激活的 epoch）。`e.Subscribe(topic)`、`e.Unsubscribe(topic)`、`e.Publish(topic, msg)` 各给它发一条包里的单向消息（`Subscribe`、`Unsubscribe`、`Publish`），订阅的是 `e.Root` 那个 Actor；包里还有一条发给它的单向消息 `NotSubscribed(ActorId, Epoch)`（下面）。它们不写 `[From<…>]`：任何 Actor 类型都可能发送它们，所以第 4 节的方向规则不适用于它们（分析器也不检查）。这些都是包里普通的消息，不是控制邮件：控制邮件是运行时自己的几种（第 10、11 节），运行时不依赖包。`Feeds.Prices` 是用户自己的常量。
- **只有根要保存的 Actor 类型能订阅**：`[Stateless]`、`Connection` 和根不保存的 Actor 类型不占 epoch（第 10 节），Topic 按 epoch 认订阅者、删过期的订阅，记不下它们；订阅还要在订阅方的根上留一份持久化的 `Subscription`，而不保存的根下面不能有要保存的数据。在它们上面 `Subscribe` 是分析器错误（DAS023，第 13 节）。生成器给每个调用 `Subscribe` 的 Actor 类型注册的根 prefab 副本加上 `DynamicBuffer<Subscription>`，所以它在 `persist` 里，随根保存。
- **`Subscribe(epoch)` 是幂等的单向消息**，带着订阅方这次激活的 epoch（框架填上）：Topic 给每个订阅者只记最大的 epoch；epoch 更大就更新，相同的什么也不改，更小的（旧激活迟到的）不理会。表的改动是普通的改动，随下一个检查点写下（第 10 节）；宕机丢掉的由续订补上（下面）。
- **承载 Topic 的服务器类型写 `[Hosts<Topic>]`**（第 2 节），Topic 的根要保存，所以这个工程要配存储。
- **订阅也是订阅方的数据，订阅方定期续订**：订阅方的根上有持久化的 `DynamicBuffer<Subscription>`。包里的系统在 `Activated` 之后订阅；之后只要根活着、不带 `Deactivating`，每隔 `DaServer:Topics:RenewInterval`（默认 60 秒）把每个 `Subscription` 再发一次 `Subscribe`（`[ForEach(Slices = N)]` 分片扫，同空闲检查；一行在一轮里被搬到别的表时可能漏扫一次，所以准确地说是最多两个续订间隔）；活着的根去掉 `Deactivating` 时（空闲停用被取消，`[Removed<Deactivating>]`）马上再发一次，因为那一轮已经退订过了（这要 W1 核对过 tag 的移除会记下，第 10 节；核对不过，这次重新订阅就退回到下一次续订）。所以丢了或乱了序的订阅消息最多两个续订间隔就补上（下面的"最多一次"）。
- **退订**：`e.Unsubscribe(topic)` 先从订阅方自己的 `Subscription` 缓冲里删掉这一项，再给 Topic 发单向的 `Unsubscribe`。包里的系统在 `Deactivating` 那一轮也发 `Unsubscribe`（缓冲不动，下次激活照它再订阅），所以 Topic 的表里只有激活着的订阅者，投递从不激活 Actor。
- **接收的节点按订阅方的数据过滤**：每个节点的投递帧里，每个订阅者都带着 Topic 表里的 `(ActorId, Epoch)`。投递在订阅者的 shard 上派发时（不是进队列时）才查缓冲：topic 不在订阅者根的 `Subscription` 缓冲里（退订了），或者订阅者已经不在，就丢掉它，从订阅者的 shard 给 Topic 发一条单向消息 `NotSubscribed(ActorId, Epoch)`，两种情况都原样带回投递时的那个 epoch；Topic 收到时，带回的 epoch 和表里的相同才删这一项。所以在 `Unsubscribe` 之后派发的投递不运行处理方法；`NotSubscribed` 是普通的单向消息，在 `Busy` 下可能被丢掉，丢了也无妨：下一次投递又被丢掉，再发一次；丢了的 `Unsubscribe` 也无妨：下一次发布就把这一项删掉，哪怕订阅者在那之后已经迁移、换了新的激活（不在的订阅者下次激活时自己再订阅）。
- **删除只认同一个激活**：退订带着发出它的激活的 epoch，`NotSubscribed` 带着投递时表里的 epoch，都只有和表里的相同才删。重新订阅也不会被误删：同一个激活里的退订、`NotSubscribed` 和再次 `Subscribe` 都是普通的单向消息，按先后从同一个 shard 发给同一个 Topic，按第 1 节的保证 2 按顺序到达，新激活的 `Subscribe` 又先把表里的 epoch 抬高。所以旧激活迟到的退订或通知，删不掉新激活刚做的订阅；旧激活迟到的 `Subscribe` 比表里的小，也盖不掉它。
- **`e.Publish(topic, msg)`**：把 `msg` 序列化一次，装进包里的单向消息 `Publish`（负载的类型号和字节）发给 Topic；Topic 不解码负载，按订阅者所在的节点分组，每个节点一帧（只序列化一次，池化的帧，帧里每个订阅者带着它的 `(ActorId, Epoch)`），那个节点再放进各订阅者的邮箱。`ICluster` 也能发布。
- **最多一次，两个方向都有界**：Topic 宕机时在途的发布丢掉。丢了或乱了序的订阅消息（Topic 在检查点之前宕机，丢了没写下的表；一条 `Subscribe` 在路上丢了；Topic 迁移时 `Unsubscribe` 和之后的 `Subscribe` 乱了序）最多让订阅者两个续订间隔收不到发布。退订之后，在 `Unsubscribe` 之后派发的投递不再运行处理方法（接收的节点按 `Subscription` 缓冲丢掉），`Unsubscribe` 丢了的话 Topic 也在下一次发布时停发（那次投递换回一条 `NotSubscribed`）。同一个发布者经同一个 Topic 发给同一个订阅者的按发送顺序到达。没有回放，没有持久的偏移：要持久的流（回放、消费组）用 Kafka 或 NATS JetStream，从 Actor 里调用。
- 一个 Topic 的订阅者数有上限（节点配置）；更大的扇出分到多个 Topic 键，推给客户端用 `ClientGroup`（第 9 节）。
- Actor 之间的扇出由它做；框架本身没有别的发布。

### 非 .NET 的客户端

- **客户端的线协议写成公开的规范**：帧格式、握手（令牌、`wire.manifest` 哈希）、`Ready`、重连和重新加入（槽号、`JoinConfirmed`、`PresenceLost`、`PresenceClosed`）、`Retire` 和 `HandedOff`、`Superseded`、`Unavailable`、`Busy` 的 `RetryAfter`、心跳和静默断开。C# SDK 是参考实现，一套一致性测试同时跑 C# 和其他实现。
- **TypeScript 客户端**（npm 包，经浏览器的 WebSocket 连 `/clients/ws`）：消息的类和编解码由 Nino.TypeScript（预发布的包）在每次构建 `Game.Protocol` 之后从它的程序集生成，结构体和版本兼容的类型都支持，DaServer 不另写 TypeScript 生成器；浏览器拿到和 C# 客户端一样的类型；槽、重新加入、`Retire`、退避按规范实现。
- Unreal / C++ 以后按同一份规范做。

### `DaServer.Replication`：组件同步到客户端

```csharp
[Replicated] public record struct Health(int Value) : IComponent, ITracked;   // changed fields reach the clients
```

- `[Replicated]` 写在追踪变更的组件上（分析器检查 `ITracked`，以及非托管或 `[NinoType]`）。实体同步给谁，由用户写的 `ReplicatedTo(ClientGroup Group)` 决定，例如 AOI 系统写它所在格子的组。客户端看到的实体编号是包分配的 `ReplicaId`（每个 Actor 一个计数器），不是 `EntityId`。
- **状态是 DaServer 自己的数据**，不依赖 EasyEcs 记住发过什么：
  - 每个 `[Replicated]` 组件旁边有内部组件 `Replica<T>`：上次发出的值和发出它的戳，也就是同步那一趟开始时读的 world 的 `ChangeTick`（EasyEcs 的变更戳，`ulong`；不是 `Time.Tick`，那是按 Actor 类型数的 tick 号，和写入戳比不了）；
  - 每个组有一个有界的环，记着离开这个组的实体（换组或销毁）和离开时的 `ChangeTick`。不用 EasyEcs 的销毁日志（`WorldOptions.DestroyLogCapacity`、`world.DestroyedSince`）代替它：那个日志按 world 记被销毁的 `EntityId`，不分组、不带 `ReplicaId`，换组的离开它也不记；环把两种离开放在一处，按组回答；
  - 每个 Actor 有一个**化身号**（随机 64 位，在根上）：从记录激活、迁移到达、热重启加载、抛出后重新加载时都换新的。`ChangeTick` 是一个 world 的计数，所以戳只在同一个化身里可比：Actor 每次换到另一个 world（迁移、热重启的新进程、换节点的激活），化身号都换了。换化身号时框架清空这个 Actor 的组的离开环，把 `Replica<T>` 的戳清零：旧 world 的戳和离开不再算数，环覆盖的范围从这个化身的第一趟同步算起。
- **每个 tick**：每个 Actor 类型一个包里的系统，`[ForEach, ChangedAny<所有 [Replicated] 类型>, With<ReplicatedTo>]`（每个属性 2 到 9 个类型，多的分几个属性，并进同一组；只有一个 `[Replicated]` 类型时是 `[Changed<T>]`，因为 `ChangedAny` 要 2 到 9 个类型）：一个实体改了几个组件也只出一份补丁。生成器给每个类型生成按字段的位掩码，和 `Replica<T>` 比较，按 `ClientGroup` 把改了的字段写进池化的帧，一个组一帧（序列化一次，网关扇出，第 9 节），再更新 `Replica<T>` 和它的戳。离开组的实体写成移除。每份补丁带着这一趟的戳，客户端只原样存下、加入时交回，不解释它。
- **全量**：实体换组（`[Changed<ReplicatedTo>]`）时，新组收到它的全量、旧组收到移除；Presence 新订阅一个组时收到它订阅的组里所有实体的全量。
- **重新加入只补差**：`[Changed<Joined>]` 并且 `MissedPushes` 为真时，SDK 在加入时带着它最后应用的（化身号, 戳）。化身号相同、组的离开环还够得着那个戳，就只发从它起的：离开环里的移除；`ReplicatedTo` 的写入戳不早于它的实体发全量（离线期间才进组、之后没再写过的实体，`Replica<T>` 的戳不说明它，靠的是这一条）；其余实体发 `Replica<T>` 的戳不早于它的组件。查询用一个存在字段里的过滤 `QueryFilter.Create().ChangedAny<所有 [Replicated] 类型, ReplicatedTo>(0)`（`ReplicatedTo` 是追踪变更的组件），每个客户端用 `filter.Since(ack)` 换成它确认的起点，不分配（只是更快的查找，不靠它判断正确性）。`Since` 是"在它或它之后写过"，所以确认的那一趟里写过的也再发一次：多发，不漏。化身号不同（Actor 重新加载、迁移、热重启、`Reload`），或者环已经被覆盖，就发全量。
- **重发全量就是 `Reset(化身号)`**：先发一条 `Reset(化身号)`，客户端清掉这个 Actor 的所有副本，再应用全量。所以离线期间被移除的实体不会留在客户端上，客户端也从此按新的化身号报告（化身号, 戳）。
- 客户端 SDK 应用补丁：C# 的 `DaServer.Replication.Client`（net10.0，因为 EasyEcs 只发 net10.0：Godot 4 .NET 游戏和 .NET 机器人）写进客户端的 EasyEcs world，TypeScript 的写进按 `ReplicaId` 索引的实体表。Unity 要等 EasyEcs 出 netstandard2.1 的版本（路线图 W7）。
- 同步的组件用写入方法改：`[Replicated]` 组件都追踪变更，生成的 `entity.Health` 是 `ref readonly`，所以服务器上写它用 `e.WriteHealth()`、`Mut<Health>` 或 `SetHealth`，不能像第 12 节没同步的 `Health` 那样 `ref var hp = ref target.Health`。
- 最多一次，和推送一样；漏了的由全量补上。用一个单独的基准项目证明：同一个 mmo 的 AOI 场景跑两遍，同步组件对比样例手写的 `Nearby`（每个客户端的字节数、广播 p99、每 tick 的分配）；mmo 样例本身保留手写的 AOI（ADR-091）。

### 测试和管理的小工具

- **`TestCluster`**（`DaServer.Testing`）：进程内的多节点集群，和 `TestActor<K>` 一样接 `Registration`：
  ```csharp
  await using var cluster = await TestCluster.Start(Registration.Create(), c => c
      .Nodes<PlayerServer>(2).Nodes<MapServer>(2).Nodes<GatewayServer>(1)   // which server type each node runs
      .Table(new MapTable([7]))                                             // the cluster's current version of a config table
      .Table(Skills.Table)                                                  // every table the server types read: the maps read SkillTable
      .ManualClock());                                                      // step ticks and time from the test
  var alice = await cluster.Connect(new Account(1));                        // a client with claims, through a gateway
  Assert.That(await alice.Ask(new EnterMap(7)), Is.True);
  cluster.Step(TimeSpan.FromSeconds(1));                                    // every node's worlds advance one second of ticks
  Assert.That(alice.Pushes<Nearby>(), Is.Not.Empty);
  ```
  `Connect(claims)` 签一个令牌、经网关连上，返回测试用的客户端（`Ask`、`Send`、收到的推送、`PresenceLost` 等 SDK 事件）；`Nodes<TServer>(n)` 选每个节点跑哪种服务器；`Table(value)` 给集群一个配置表的当前版本（不写就按 `DaServer:Tables:{名字}` 的配置；都没有时读这张表的节点不就绪，`Start` 等不到就绪就失败并报告缺的表，第 10 节），`Map.CanCreate` 这样读表的门因此在测试里有数据；`ManualClock()` 让 `Step` 推进所有节点的时间和 tick，不用等真实时间。没有存储时用内存里的 `IActorStore` 和内存里的成员表（进程内的节点共用一个实例，第 2 节），也能接 PostgreSQL（`Schema` 先建表，第 10 节）。
- **`TestCluster` 的故障注入**：`cluster.Partition(a, b)`（双向或单向）、`cluster.Kill(node)`（不经过优雅停止）、`cluster.DelayLinks(a, b, delay)`。用户用它测自己的逻辑在分区、宕机、慢链路下的结果，和框架自己的测试是同一套。
- **查看端点**（只读；本地是 Aspire 命令，生产是 Dashboard）：一个 Actor 的子树、最忙的 Actor、每个 world 的系统、按组件计数；端点和规则见 [architecture.md 第 8 节](architecture.md#inspector-and-admin-clients)。
