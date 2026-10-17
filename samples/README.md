# Samples

One directory per application scenario. Each is a set of ordinary projects built on DaServer; nothing in `src/`
knows about them.

| Scenario | What it shows |
|---|---|
| [mmo/](mmo) | An MMO cluster: login server, client-facing gateway nodes, game server (players, realm), map servers (maps, movement, area of interest), 10k bots, Aspire orchestration. |
| [flashsale/](flashsale) | A flash sale: a million buyers for 100 units over the client protocol, sharded stock, a waiting room, idempotent orders in PostgreSQL, an autoscaled sale group on Kubernetes. |
