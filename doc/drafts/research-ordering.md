# 调研：有状态服务器的消息顺序、并行与缓存（编程模型草案的依据）

问题：单线程按到达顺序处理所有客户端的消息，顺序可控，但只有一个核；多线程按客户端分 actor，交互的两个客户端没有共同的顺序；按消息随便调度，状态会乱、缓存失效。

## 1. actor 运行时与分区系统：一致性域 + 单写者 + 成批执行

“一致性域”是文献里的说法：一个单写者负责的那组状态。本设计里它就是一个 Actor（根实体加子孙）。

| 系统 | 一致性域 | 单写者 | 成批 | 跨域 |
|---|---|---|---|---|
| Orleans | grain | 逐个 turn（默认不可重入） | `ActivationSchedulingQuantum`（按时间） | 可重入选项；Orleans Transactions（两阶段提交，提前释放锁） |
| Akka / Proto.Actor | 实体 | actor + Cluster Sharding | `throughput`（默认 5 / 300） | saga；只保证同一对发送方与接收方之间的顺序 |
| Kubernetes | API 对象 | 每个键一个 worker；租约选主 | 按键去重 | `resourceVersion` 乐观并发、冲突重试；按状态差反复调和 |
| LMAX | 整个交易所 | 一个线程（每秒 600 万笔） | smart batching | 不需要 |
| VoltDB / H-Store | 分区（每核一个） | 分区线程 | 每分区队列 | 协调者 |
| Calvin | 节点的锁管理器 | 按全局顺序授锁 | 10 ms 一批 | 先定序再执行，不需要两阶段提交 |
| Redis | 实例 | 主线程 | 管道 | 多开实例 |

为什么每分区单线程胜过加锁：没有锁、闩、CAS 的开销，没有上下文切换，数据留在一个核的缓存里，执行确定。H-Store 的测量：传统数据库的时间主要花在日志、闩、锁、缓冲管理上。

来源：
- https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-scheduling
- https://learn.microsoft.com/dotnet/api/orleans.configuration.schedulingoptions
- https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions
- https://vldb.org/pvldb/vol17/p3720-eldeeb.pdf
- https://raw.githubusercontent.com/akka/akka/main/akka-actor/src/main/resources/reference.conf
- https://doc.akka.io/libraries/akka-core/current/general/message-delivery-reliability.html
- https://doc.akka.io/libraries/akka-core/current/typed/cluster-sharding.html
- https://github.com/kubernetes/community/blob/master/contributors/devel/sig-api-machinery/controllers.md
- https://martinfowler.com/articles/lmax.html
- https://mechanical-sympathy.blogspot.com/2011/09/single-writer-principle.html
- https://cs.yale.edu/homes/thomson/publications/calvin-sigmod12.pdf
- https://redis.io/docs/latest/operate/oss_and_stack/management/optimization/latency/

## 2. 游戏服务器：每个区域一个串行权威，多核靠很多区域

- ET：每个 Scene（例如一张地图）是一个 Fiber，单线程；Fiber 跑在线程池上；有序邮箱用协程锁；换地图经 Location 加锁、序列化、转发、重新登记。https://github.com/egametang/ET
- EVE：每个星系单线程；热点星系独占节点；时间膨胀（TiDi）。https://www.eveonline.com/news/view/time-dilation-hows-that-going
- WoW：分片、跨服区域、分层、相位，都是靠拆人降低密度。https://warcraft.wiki.gg/wiki/Sharding_(term)
- New World：世界按格子分给 hub，人移动时交接，30 Hz。https://aws.amazon.com/blogs/gametech/the-unique-architecture-behind-amazon-games-seamless-mmo-new-world
- SpatialOS、Hadean：每个实体组件同时只有一个权威 worker，按空间交接。
- Valorant：一局一个串行循环，一个核跑三局以上，按 NUMA 固定。https://www.riotgames.com/en/news/valorants-128-tick-servers
- Source、Overwatch、Unity Netcode、Photon、帧同步：输入绑定到 tick，同一 tick 的输入视为同时，按固定顺序裁决，不按到达顺序。
- Unity、Bevy、Flecs：按读写集并行跑系统；结构变更先进命令缓冲，在同步点按确定顺序回放。

## 3. 按既定顺序并行执行、结果等于串行

| 做法 | 读写集 | 结果等于到达顺序 | 适用 |
|---|---|---|---|
| Calvin | 预先声明，或先探查一次 | 是 | 依赖数据的事务要执行两次 |
| BOHM | 预先声明写集 | 是 | 多版本占位，读不阻塞写 |
| PWV | 读写集 + 拆分成片 | 是 | 写尽早可见，高冲突下快 |
| Aria | 运行时 | 否（重排、推迟到下一批） | — |
| Block-STM | 运行时，校验 | 是 | 交易约 100–200 µs；全冲突时最多慢 30% |
| Solana、Sui | 预先声明（Sui 写进类型） | 不冲突的可交换 | 热点账户串行 |
| Project Darkstar | 运行时，冲突重试 | 否 | 热点对象重试风暴 |

关键差别：数据库和区块链的事务每笔 100 µs 级，游戏消息约 0.05–2 µs，所以每次访问的调度开销必须是几次数组读写，而不是并发数据结构。

可行的做法（按推荐排序）：
1. 孤岛并行：一批消息按写到的（实体, 组件）做并查集，每个孤岛按到达顺序串行，孤岛之间并行；宽消息（访问集未知）作为串行屏障；只做累加的热点组件用按顺序的前缀和。
2. 分波次并行：按依赖层级分层，每层按消息类型成批，可 SIMD；冲突链长时退化。
3. 乐观并行（Block-STM 风格）：只对很重、访问集不可预知的处理方法。

来源：
- https://www.cs.princeton.edu/courses/archive/fall19/cos418/papers/calvin.pdf
- https://arxiv.org/pdf/1412.2324
- https://par.nsf.gov/servlets/purl/10156624
- https://www2.cs.arizona.edu/~caolei/papers/Aria.pdf
- https://www.eecg.utoronto.ca/~ashvin/publications/caracal.pdf
- https://arxiv.org/abs/2203.06871
- https://solana.com/news/sealevel---parallel-processing-thousands-of-smart-contracts
- https://move-book.com/object/fast-path-and-consensus.md
- https://static.usenix.org/events/usenix08/tech/slides/waldo.pdf
- https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-entity-command-buffer-playback.html

## 4. EasyEcs 4.4

- 现成可用：立即生效的结构变更、并行 `[ForEach]`、批量方法、`ParallelSystems`、变更追踪、值索引、`IGroup`、`DynamicBuffer`、`ICleanupComponent`、`MoveEntities`/`SaveEntities`、`MeasureSystems`。
- 和"依赖图并行""乐观并行"冲突的决策：结构变更立即生效、不提供命令缓冲（D2、D3），世界只有一个所有者线程（D13）。
- 需要提的功能：按分组值并行 `[ForEach]`；运行时读取读写集；对给定实体列表并行执行；多个 world 共享并行度上限；读取单个实体组件的写入 tick。后来哪些提供了、哪些放弃了，见编程模型草案[第 14 节](programming-model.md#14-已定的取舍以及待办)第 5 条。

## 5. 网易：倩女幽魂、天谕手游、梦幻西游

没有找到覆盖倩女幽魂整体服务端架构的公开演讲；以下来自雷火的官方知乎文章和云风的公开分享提纲。

- **一个副本在一个游戏进程里**：Master 进程管理场景分配和负载均衡，游戏进程（Gas）承载场景和其中的角色；"副本的所有逻辑都在单个游戏进程中完成"。跨服玩法是把玩家传送到另一组服务器。https://zhuanlan.zhihu.com/p/431133807
- **倩女的 Avatar 系统（热点场景拆到多个进程）**：一张地图拆成多个镜像场景，每个角色在一个进程上是"主控角色"、在其他进程上是"化身"；技能按目标拆成单元，转发到目标主控所在的进程结算，结算是异步的，竞争要逐个技能手工处理。效果：帮会赛 84 对 84 无延迟，上限约 500 人同屏；瓶颈变成进程间通信和 AOI。改造前，技能结算占游戏进程 90% 以上的开销。https://zhuanlan.zhihu.com/p/648987879
- **天谕手游选了相反的路**：不按玩家拆进程（密集时每个进程几乎都要镜像所有人，异步编程降低效率），而是"纵向拆分"，"尽量保持上层业务逻辑是单线程"，把视野序列化、广播列表、广播挪到网络线程。单图 1150+ 人，主线程 40–50%，网络线程 20–30%。https://zhuanlan.zhihu.com/p/700231330
- **梦幻西游、大话西游（云风 2007）**：多进程、单线程；跨组消息避免交互式（请求-回复）协议。https://blog.csdn.net/zhongguoren666/article/details/8018290
- 都没有提到 ECS、job 系统或面向数据的布局。

启示：
- 业务逻辑单线程、一个场景一个串行权威，是网易各产品的共同基础，与本设计的"Actor 内串行"一致。
- 把一个热点场景拆到多个进程（Avatar）能扩到约 500 人，但代价是异步结算和手工处理竞争；天谕认为不值得，改为把非业务的部分（序列化、广播、AOI、存盘）移出主线程。
- 对本设计：Actor 内的业务处理保持串行等价；把序列化、广播合并、AOI、写记录这些"输出"放到并行的 tick 系统和网络线程（框架已经这样做发送合并），是同一个方向。

## 6. 让游戏逻辑像顺序代码、并行交给运行时

- Sweeney，POPL 2006：游戏逻辑约 1 万个对象，每次更新碰 5–10 个；提议用事务，以任意顺序并发更新；数值计算用隐式并行的函数式，约占 80% 的 CPU。https://www.cs.princeton.edu/~dpw/popl/06/Tim-POPL.ppt
- Verse：函数默认 `<transacts>`；`<computes>`/`<reads>`/`<writes>` 写在签名里；"没有 `<writes>` 的函数可以无锁并行执行"；失败会回滚效果。目前 Fortnite 的事务是单线程的（AutoRTFM），用于回滚。https://verselang.github.io/book/13_effects/ ，https://www.unrealengine.com/tech-blog/bringing-verse-transactional-memory-semantics-to-c
- Roblox Parallel Luau：同一个 Actor 的脚本串行；`task.desynchronize`/`synchronize` 切换阶段；并行阶段基本只读；小任务的开销大于收益。https://create.roblox.com/docs/scripting/multithreading
- SpacetimeDB：每个 reducer 一个事务；每个数据库单线程执行；BitCraft 按区域分模块，周期性的 agent reducer 批量处理。https://spacetimedb.com/docs/functions/reducers ，https://github.com/clockworklabs/BitCraftPublic
- Unreal Mass：处理器声明 fragment 访问；结构变更走命令缓冲，批次结束时生效；信号双缓冲。https://dev.epicgames.com/documentation/en-us/unreal-engine/overview-of-mass-entity-in-unreal-engine
- Bevy：observer 立即执行但串行、独占世界；系统并行但命令要等同步点；用户常遇到"差一帧"和顺序歧义的问题。https://bevy.org/news/bevy-0-14/ ，https://github.com/bevyengine/bevy/pull/7383
- Unity DOTS：结构变更走命令缓冲，在同步点回放；频繁的状态用可开关的组件。https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/performance-sync-points.html

## 7. 数据流、确定性模拟、SIMD

- Differential Dataflow：按键分区、键内按时间顺序；单次更新约 230 µs，大批量时约 1–5 µs；适合大批量同一时间戳的数据，不适合顺序相关的小操作。https://github.com/TimelyDataflow/differential-dataflow
- Factorio：并行电网 0.5 ms → 0.39 ms，但 CPU 用量从 0.5% 涨到 15%，整体没有变快（内存带宽受限）；只读的读取器并行效果好。https://www.factorio.com/blog/post/fff-421
- Paradox CK3：每个系统先并行读（只写私有数据），再按确定顺序写；"最有效、最好教"。https://accu.org/conf-docs/PDFs_2023/XMultiThreadingModelinParadoxGamesPastPresentandFuture.pdf
- Stellaris：伤害先记录，后续阶段再统一施加；按线程分缓冲，最后合并。
- SIMD（PVLDB 2018）：连续数组上的筛选快 8.4 倍；分散访问时最多快 2.7 倍；gather 约 1.1 倍；连接查询几乎没有收益。https://ir.cwi.nl/pub/28470/28470.pdf
- 顺序与公平：先到先得适合请求、回复类的交互；"同一 tick 视为同时"适合随时间推进的模拟。

## 8. 并行离散事件模拟（PDES）

- 前瞻（lookahead）：一个逻辑进程在时刻 T 发出的消息，时间戳至少是 T + L。若所有跨进程的影响都至少延迟 L，那么 [t, t + L) 窗口里不同进程的事件可以并行，结果与逐个执行一致（同时刻的事件要有确定的次序）。https://sigsim.acm.org/conf/mskr/Courseware/Fujimoto/Slides/FujimotoSlides-07-Lookahead.pdf
- 窗口式同步（YAWNS 一类）：算出安全下界，处理所有不超过它的事件，屏障，再下一轮；"按时间步推进的模拟隐含了一个步长的前瞻"。https://sigsim.acm.org/conf/mskr/Courseware/Fujimoto/Slides/FujimotoSlides-09-SynchronousAlgorithms1.pdf
- 前瞻的来源：一个实体影响另一个实体所需的最短时间，例如"武器飞到另一辆坦克需要 L"、"坦克的最大速度"。保守算法"成败取决于前瞻"：前瞻与步长之比从 0.9 降到 0.1，加速比从约 10 倍降到约 1 倍。
- 前摇不是前瞻：结算时读目标当时的状态，延迟为 0。可用的前瞻来自空间距离与最大速度。
- 乐观同步（Time Warp）：投机执行、回滚、反消息；反向计算比保存状态快约 3 倍。ROSS 的 PHOLD 约每核每秒 25 万个事件（合成基准）。https://www.llnl.gov/news/lawrence-livermore-and-rensselaer-polytechnic-institute-scientists-set-new-simulation-speed
- 细粒度事件：平均每个事件 3.7 µs 时，原子操作、锁就已经是主要开销（Wilsey，WSC 2019）。约 1 µs 的事件只有在一次同步覆盖很多事件（按窗口、按批）时才值得并行。https://www.informs-sim.org/wsc19papers/150.pdf
- 游戏：Darkstar 用事务加冲突重试，不保证时间顺序；SpatialOS 没有前瞻或时间窗口的一致性保证；并行 Quake 服务器的瓶颈是锁和全局同步点的负载不均；人群密集的区域无法再细分（LEARS）。
- 事件表：日历队列、时间轮的插入和取出平均 O(1)。https://blog.acolyer.org/2015/11/23/hashed-and-hierarchical-timing-wheels/

## 9. 业界做法（v21 依据）

- **帧率是部署参数，延后用时间**：
  - Minecraft 的 `/tick rate` 改的是整个游戏的帧率，按 tick 写的一切（作物、红石、冷却）都跟着变快变慢；默认 20，重启后复原。https://minecraft.wiki/w/Commands/tick
  - Photon Quantum 的 `FrameTimer.FromSeconds(frame, 秒)`：用户写秒，内部存帧；另有 `FromFrames`。https://doc.photonengine.com/quantum/v3/concepts-and-patterns/frame-timer
  - 结论：数据里写时间，发出时换算成到期帧一次（向上取整，从不提前）；改帧率不改手感。
- **并行来自很多个 Actor，不来自切开一个模拟步**：
  - TrinityCore：`MapUpdater` 是一个线程池，每张地图的更新是一个任务，地图内部单线程；一张地图卡住，这一轮的其他更新都要等它。https://github.com/TrinityCore/TrinityCore/blob/master/src/server/game/Maps/MapUpdater.cpp
  - EVE：每个星系单线程，热点星系独占节点，过载时时间膨胀（第 2 节）。
  - LoL、Valorant：一局一个串行的服务器循环，一台机器跑很多局。https://technology.riotgames.com/news/determinism-league-legends-introduction ，https://www.riotgames.com/en/news/valorants-128-tick-servers
  - New World：无缝世界按格子分给进程，人移动时交接；每个格子内部仍是一个串行权威（第 2 节）。
  - 没有找到上市游戏在一个模拟步里按空间切分并行。空间前瞻需要位置、作用距离、最大速度这些游戏概念，和框架的通用性（ADR-045）冲突，还多一帧延迟。
- **一个 Actor 内**：有序的部分串行；只写自己组件的部分按列并行（第 7 节的 CK3、Factorio，第 2 节的 ECS 引擎）。
