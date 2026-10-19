# Flash sale sample

A million buyers for 100 units. Buyers connect to any sale node (the platform's load balancer spreads them) over the
client protocol (TCP, or WebSocket at `/clients/ws` for browsers, wss behind the platform's TLS), never over the HTTP
plane, which is for low-frequency traffic (ADR-046). On every node a stateless `Gate` service turns their messages into
asks of the sale's actors.

## How it works

Actors and the gate are static classes of stateless `[Handle]` methods over components (ADR-080): a handler runs to
completion, asks come back as `Answered<,>` messages, and what a later step needs is data on an entity.

- **Stock**: 16 `StockShard` actors per sale. A handler is the atomic decrement (no lock, no Redis counter); shards let
  buyers decrement in parallel. A buyer starts at its own shard and moves on when it is empty.
- **Orders**: one `Order` actor per (user, sale). A purchase in progress is its `Buying` component; a retry arriving
  meanwhile keeps its reply token with the order's and hears the same answer, as does every retry after it
  (`Purchase`): idempotent without request ids.
- **Sold out**: each shard tells the `Sale` actor when it runs out and who bought its units. Once all are empty, each
  shard's gate asks the sale once (`Describe`) and keeps the answer in its world (a `KnownSale` entity, found by an
  EasyEcs index): from then on it answers the crowd itself, "confirmed" for the buyers, "sold out" for everyone else,
  without touching any actor. The late crowd spreads over every shard of every node and creates no order actors.
- **Queue (optional)**: a sale opened with `PerSecond` above 0 has a `WaitingRoom`: the `Sale` actor hands out
  positions and `AdmissionSystem` admits them at that pace every tick; a buyer buys once admitted (the gate checks with
  the sale).
- **Gate**: a client request it serves in steps (describe the sale, check the queue, buy) is an entity while its caller
  waits, the one its reply token names; the gate's components on it (`Placing`, `QueueingUp`, ...) carry what the
  next step needs and go with it when it is answered.

## Compared with the usual designs

| Usual design | Here |
|---|---|
| Load balancer + stateless edge servers | Clients connect to any node (the platform's LB); the `Gate` service is stateless, nodes are added to scale |
| Stock pre-deducted in Redis (`DECR`, Lua) | `StockShard` actors: a handler is atomic, the state is the actor's components, written with `SaveNow`; no extra hop, no single counter |
| Deduplicated, idempotent order requests (request ids, unique keys) | An `Order` actor per (user, sale): retries join the running purchase and read its outcome |
| Message queue between the edge and order creation (peak shaving) | Not needed: the actors' mailboxes are the queue, bounded and backpressured (`Overloaded` makes clients retry); a real shop sends confirmed orders on to payment and fulfilment as messages |
| "Sold out" cached at the edge (CDN, gateway) | Each shard's gate knows the sale is over and answers locally |
| Waiting room / virtual queue (ticketing, Queue-it, Cloudflare Waiting Room) | Optional `PerSecond` queue: worth it when stock is large and checkout (payment, a database) takes only so many orders a second, or when buyers must be served first come, first served. Few units and a crowd (this sample's default) sell out at once: buying directly is one request per buyer and has no single hot actor |

## Projects

| Project | What |
|---|---|
| `FlashSale.Contracts` | ids (`SaleId`, `StockShardId`, `OrderId`) and messages; the `[FromClient]` ones for the gate |
| `FlashSale.Components` | `[Persist]` stock, purchase, sold units and waiting room; the purchase in progress (`[Handoff]`); the gate's known sales and requests in progress |
| `FlashSale.Server` | the node: `StockShard`, `Order`, `Sale` actors, `AdmissionSystem`, the `Gate` service |
| `FlashSale.Crowd` | the buyers as a `DaServer.Simulation` swarm; checks exactly the stock sold, nothing left, retries answered alike, orders readable (`--per-second` for a queued sale, `--web-socket` for the browser path) |
| `FlashSale.AppHost` | two sale nodes taking clients, PostgreSQL and the crowd (`dotnet run --project samples/flashsale/FlashSale.AppHost`); `aspire publish` writes Docker Compose files, `-- Publish=k8s` a Helm chart with an HPA on the sale nodes' CPU and a VPA in recommendation mode (`Platform.cs`: nothing restarts during a sale) |
| `FlashSale.Tests` | the guarantees in an in-process cluster (`DaServer.Testing`): every unit sold once, retries (also during a purchase) answered alike, the queue on a manual clock |

## Numbers

One 4-core VM with the crowd on it (it takes about half the machine; the machine is the limit, not a node), 100 units:

| Run | Requests | Time | Steady rate | Node CPU |
|---|---:|---:|---:|---:|
| 1,000,000 buyers, 2 nodes, direct (buy + one retry each) | 2,000,101 | 8.6 s | | 1.8 cores |
| 3,000,000 buyers, 1 node, direct (requests on a 1 ms beat, the crowd's default; ADR-070) | 6,000,101 | 13.2 s | 548,000/s | 1.5 cores |
| 1,000,000 buyers, 1 node, queued (2,000 admitted a second) | 1,036,163 | 5.5 s | | 1.6 cores |

Exactly 100 sold every time, no retry answered differently, every confirmed order read back.
