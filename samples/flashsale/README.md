# Flash sale sample

A million buyers for 100 units. Buyers connect to any sale node (the platform's load balancer spreads them) over the
client protocol (TCP, or WebSocket at `/clients/ws` for browsers, wss behind the platform's TLS), never over the HTTP
plane, which is for low-frequency traffic (ADR-046). On every node a stateless `Gate` service turns their messages into
asks of the sale's actors.

## How it works

- **Stock**: 16 `StockShard` actors per sale. A turn is the atomic decrement (no lock, no Redis counter); shards let
  buyers decrement in parallel. A buyer starts at its own shard and moves on when it is empty.
- **Orders**: one `Order` actor per (user, sale). Its turn spans the purchase, so a retry waits for it and gets the
  same answer: idempotent without request ids.
- **Sold out**: each shard tells the `Sale` actor when it runs out and who bought its units. Once all are empty, each
  shard's gate asks the sale once (`Describe`) and keeps the answer in its world (`KnownSales`): from then on it
  answers the crowd itself, "confirmed" for the buyers, "sold out" for everyone else, without touching any actor. The
  late crowd spreads over every shard of every node and creates no order actors.
- **Queue (optional)**: a sale opened with `PerSecond` above 0 queues its buyers: the `Sale` actor hands out positions
  and `AdmissionSystem` admits them at that pace every tick; a buyer buys once admitted (the gate checks with the sale).

## Compared with the usual designs

| Usual design | Here |
|---|---|
| Load balancer + stateless edge servers | Clients connect to any node (the platform's LB); the `Gate` service is stateless, nodes are added to scale |
| Stock pre-deducted in Redis (`DECR`, Lua) | `StockShard` actors: the turn is atomic, the state is the actor's, written with `SaveNow`; no extra hop, no single counter |
| Deduplicated, idempotent order requests (request ids, unique keys) | An `Order` actor per (user, sale): retries wait for the running purchase and read its outcome |
| Message queue between the edge and order creation (peak shaving) | Not needed: the actors' mailboxes are the queue, bounded and backpressured (`Overloaded` makes clients retry); a real shop sends confirmed orders on to payment and fulfilment as messages |
| "Sold out" cached at the edge (CDN, gateway) | Each shard's gate knows the sale is over and answers locally |
| Waiting room / virtual queue (ticketing, Queue-it, Cloudflare Waiting Room) | Optional `PerSecond` queue: worth it when stock is large and checkout (payment, a database) takes only so many orders a second, or when buyers must be served first come, first served. Few units and a crowd (this sample's default) sell out at once: buying directly is one request per buyer and has no single hot actor |

## Projects

| Project | What |
|---|---|
| `FlashSale.Contracts` | ids (`SaleId`, `StockShardId`, `OrderId`) and messages; the `[FromClient]` ones for the gate |
| `FlashSale.Components` | `[Persist]` stock, purchase and sale state; the gate's `KnownSales` singleton |
| `FlashSale.Server` | the node: `StockShard`, `Order`, `Sale` actors, `AdmissionSystem`, the `Gate` service |
| `FlashSale.Crowd` | the buyers as a `DaServer.Simulation` swarm; checks exactly the stock sold, nothing left, retries answered alike, orders readable (`--per-second` for a queued sale, `--web-socket` for the browser path) |
| `FlashSale.AppHost` | two sale nodes taking clients, PostgreSQL and the crowd (`dotnet run --project samples/flashsale/FlashSale.AppHost`) |
| `FlashSale.Tests` | the guarantees in an in-process cluster (`DaServer.Testing`), the queue on a manual clock |

## Numbers

One 4-core VM with the crowd on it (it takes about half the machine; the machine is the limit, not a node), 100 units:

| Run | Requests | Time | Steady rate | Node CPU |
|---|---:|---:|---:|---:|
| 1,000,000 buyers, 2 nodes, direct (buy + one retry each) | 2,000,101 | 8.6 s | | 1.8 cores |
| 3,000,000 buyers, 1 node, direct | 6,000,101 | 19.4 s | 335,000-386,000/s | 1.7 cores |
| 1,000,000 buyers, 1 node, queued (2,000 admitted a second) | 1,036,163 | 5.5 s | | 1.6 cores |

Exactly 100 sold every time, no retry answered differently, every confirmed order read back.
