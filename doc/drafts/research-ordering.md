# 调研：有状态服务器的消息顺序、并行与缓存（草案 v15 的依据）

问题：单线程按到达顺序处理所有客户端的消息，顺序可控，但只有一个核；多线程按客户端分 actor，交互的两个客户端没有共同的顺序；按消息随便调度，状态会乱、缓存失效。

## 1. actor 运行时与分区系统：一致性域 + 单写者 + 成批执行

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
- 需要提的功能：按分组值并行 `[ForEach]`；运行时读取读写集；对给定实体列表并行执行；多个 world 共享并行度上限；读取单个实体组件的写入 tick。
