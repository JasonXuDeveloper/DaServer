# Operating a cluster

How to run, watch and change a DaServer cluster declared in an Aspire AppHost (`AddDaServerCluster`). The commands
below are the Aspire CLI's (`dotnet tool install -g Aspire.Cli`); everything they do is also in the dashboard, and
through the Aspire MCP server to an AI agent.

## Run

```bash
aspire run   --apphost samples/mmo/Sample.AppHost             # in the foreground, with the dashboard
aspire start --apphost samples/mmo/Sample.AppHost -- Bots=10   # in the background; arguments after -- go to the AppHost
aspire ps                                                      # running AppHosts and their dashboard URLs
aspire stop                                                    # nodes stop first (leave, hand over, save), then the rest
```

## Watch

```bash
aspire describe                 # every resource: state, health, URLs. The cluster resource says "6/6 joined"
aspire wait map-0 --status healthy --timeout 120
aspire logs game-0 -f           # a node's console
aspire otel traces              # traces: an HTTP request and the handlers it causes, on any node, are one trace
aspire otel spans game-0
aspire otel logs map-0
```

The cluster resource (named as the cluster) is the parent of its nodes in the dashboard; its state counts the nodes whose
readiness passes (`/health`: joined and not leaving). A node's health is its own: ready (joined, not leaving, its store
reachable) and alive (no stalled shard, `/alive`; a shard that crashed, or failed to build its world, counts as
stalled). A handler that throws (`MessageFailed` when nobody waits for its reply, which gets the error otherwise;
`ActorFailed` for a `Created`, `Loaded` or `Unloading` handler) or a system that throws (`SystemFailed`, at most once a
second per shard) is logged and counted in `daserver.handler.failed`, and its shard goes on; so does a node whose feature throws
(`FeatureFailed`). Probes and the metrics scrape start no trace.

## Commands on a node

| Command | What it does |
|---|---|
| `aspire resource map-0 membership` | The cluster as that node sees it: members, status, incarnation, load, build, the slot coordinator (JSON) |
| `aspire resource map-0 stats` | Loaded actors, shards, queued and dropped messages (by reason), failed handlers, tick overruns, stalled shards (JSON) |
| `aspire resource game-1 drain` | The node leaves and stops gracefully, as on SIGTERM: clients move, actors hand over or are saved; asks for confirmation in the dashboard |
| `aspire resource map-0 unload --type Map` | Every loaded actor of that type on the node unloads (its `Unloading` mail, then saved as on any unload); its next message loads it again |
| `aspire resource map-0 start` / `stop` / `restart` | Aspire's own; stop is graceful too (SIGTERM) |

The commands call the node's admin endpoints on its HTTP port. Outside Aspire, call them yourself with the bearer token
the cluster's signing secret derives (HMAC-SHA256 of `daserver-admin`, hex); a node without a signing secret refuses
every admin request.

```bash
TOKEN=$(printf daserver-admin | openssl dgst -sha256 -hmac "$DaServer__SigningSecret" | awk '{print toupper($2)}')
curl -H "Authorization: Bearer $TOKEN" http://map-0:8080/admin/stats
curl -X POST -H "Authorization: Bearer $TOKEN" http://game-1:8080/admin/drain
```

`/metrics` (Prometheus) and `/health`, `/alive` need no token.

The endpoints require the authorization policy `DaServerAdmin.Policy`; its default accepts the token above. An
application that authorizes operators its own way (JWT, mTLS, an API gateway's header) redefines the policy after
`AddDaServer`: `builder.Services.AddAuthorization(o => o.AddPolicy(DaServerAdmin.Policy, p => ...))`.

## Seeds

A node joins through its seeds (`DaServer__Seeds__0`, ...): "host:port", a DNS name resolving to every replica (what
`AddDaServerCluster` passes: a Compose service, a headless Kubernetes Service), or a service name such as
`tcp://peers`, resolved by .NET service discovery from configuration (`services__peers__tcp__0=tcp://10.0.0.5:7000`)
or, once the node adds `AddDnsSrvServiceEndpointProvider()`, from DNS SRV records. Seeds are resolved in the
background every 5 s while a node is alone.

## Rotating the signing secret

The signing secret proves nodes to each other, signs clients' resume tickets and derives the admin token. Rotate it
without a restart of the cluster and without losing a ticket, in three rolling deployments:

1. Every node: `DaServer__SigningSecret=<old>`, `DaServer__AcceptedSigningSecret=<new>`.
2. Every node: `DaServer__SigningSecret=<new>`, `DaServer__AcceptedSigningSecret=<old>`.
3. Every node: `DaServer__SigningSecret=<new>`, no accepted secret.

Each step accepts what the previous one signs, so old and new nodes link and resume each other's clients throughout;
finish a step on every node before starting the next.

## Production views

The Aspire dashboard of a deployment is for diagnosis; the durable views are the platform's monitoring. In
production the nodes send everything over OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`) to one OpenTelemetry Collector, which
fans out: metrics to Prometheus's OTLP endpoint (the Collector's `otlphttp` exporter to `/api/v1/otlp`; start
Prometheus with `--web.enable-otlp-receiver` and set `otlp: { promote_resource_attributes: [daserver.cluster] }` in
its configuration; leave `service.namespace` unset, or `job` reads "ns/name"),
logs to Grafana Loki (which ingests OTLP itself at `/otlp/v1/logs`; `service.name` becomes a label, the other
attributes structured metadata) and traces to Tempo. One exit per node, and the backends change in the Collector's
configuration, not in the nodes. Every node also serves its metrics at `/metrics` on the HTTP port, always, for an
environment without a Collector, which scrapes it instead. Use one path per environment: with a Collector, do not also
let Prometheus scrape the nodes, or every series arrives twice under two identities (`instance` from the scrape,
`service.instance.id` through the Collector). KEDA and the Prometheus adapter query Prometheus, not the pod, so either
path feeds autoscaling. Set the metric export interval to 15 s (`OTEL_METRIC_EXPORT_INTERVAL=15000`; the SDK's
default is 60 s): the rules read pushed gauges over two-minute windows (`last_over_time(...[2m])`, since pushed series
get no staleness markers) and rates over windows holding at least four pushes.
The repository ships two starting points, checked against a running node:

- `observability/grafana-dashboard.json`: a Grafana dashboard (import it, pick the Prometheus data source): members
  each node sees, shard busy ratio, loaded actors, client sessions, queued, dropped (by reason) and failed messages,
  late ticks and stalled shards, GC pauses and allocation, CPU and working set.
- `observability/prometheus-rules.yaml`: alert rules: a stalled shard, dropped messages, failing handlers,
  saturated shards, late ticks, GC pauses, a silent node, and target-only rules
  that never fire on today's code (`DaServerMembersDisagree` reads `daserver_cluster_view_version`,
  `DaServerNodeRequestsLow`, `DaServerAskLatencyHigh`, `DaServerOnceTooOld`, `DaServerOnceStaleAsking`,
  `DaServerOnceCounterLow`, `DaServerOnceAskOld`, `DaServerOnceQueuedFull`, `DaServerOnceUnknownState`, `DaServerOnceSendersNearCap`, `DaServerStoreWritesBehind` (its limit is four of the node group's `DaServer:CheckpointInterval`: a group configured with a longer interval needs its own copy of the rule; when it fires, check the store's replicas first, on PostgreSQL the synchronous standbys' `pg_stat_replication.sync_state`), `DaServerRecordUnwritable`, `DaServerStoreRolledBack` and `DaServerStoreDurabilityDropped` (a store below `Strict` lost writes in a failover, or its self-check now finds a weaker class than at start; ADR-104), `DaServerMembershipDocumentLarge` (the membership document past half the store's largest value), `DaServerDeleteSendAfter`, and the ask recording rules: none of
  their metrics exist yet). Check it with `promtool check rules` before loading it. Load it with `rule_files:`,
  or wrap the groups in a `PrometheusRule` for the Prometheus Operator. A rule that compares a reading against a number carries that
  number as the annotation `daserver_limit` (and its unit as `daserver_unit`): change both together when you tune it.
  Every rule about nodes groups by `cluster`, `type` and `node` (`DaServerAskLatencyHigh`, a type's p99, by `cluster`
  and `type`; `DaServerOnceStaleAsking`, whose stopped-ask gauge is counted from the store by whichever node sweeps and maps the
  hosting server type onto `type`, by `cluster` and `type`; both it and `DaServerOnceAskOld` keep `actor_type` as a label, StaleAsking naming it in its description and AskOld in its summary; `DaServerMembersDisagree` also keeps `cluster`, `type` and `node`: one alert per lagging node), so Alertmanager groups an incident per rule and server type; a
  label a node does not send yet is empty, so the rules work on one cluster today. `DaServerNodeSilent` fires for a
  node seen in the last 15 minutes that has pushed nothing for two minutes, whose series would otherwise vanish and
  resolve its alerts; a node whose final push carries the `close` stop phase stopped gracefully and is not reported (a
  node that crashes mid-drain is). It needs the OTLP path through the Collector: a scrape carries no final push, so
  where nodes are scraped every graceful stop would fire it, and it should be left out there.

Autoscaling is the platform's. The default signal is `daserver_shard_busy_ratio` (the share of time the shards work):
with KEDA in the cluster (`AddHelmChart` in the AppHost installs it), a `ScaledObject` with a Prometheus trigger on
`avg(daserver_shard_busy_ratio{node=~"game-.*"})` scales a group's Deployment or StatefulSet; the Prometheus adapter
does the same for a plain HPA. Without either, an HPA on CPU (the flash sale sample's) is the closest built-in signal.

- Use `avg`, not `max`: one hot actor is a placement problem that more nodes do not solve.
- Set `minReplicaCount` on a `ScaledObject`: KEDA's default is 0.
- Scale in one pod per period, with the period at least the group's graceful stop and a stabilization window of
  600 s or more (`behavior.scaleDown`). A PodDisruptionBudget does not limit a scale-in, and a StatefulSet with
  `Parallel` pod management deletes the highest ordinals at once; each leaving node needs the time to hand its actors
  over.
- A VerticalPodAutoscaler manages memory only; CPU scales horizontally (a node fixes its worlds from its processor
  count at start).
- `aspire deploy` runs `helm upgrade --server-side=true --force-conflicts`, so it takes back every field the chart
  renders, `replicas` included. For a group an autoscaler manages, remove `replicas` from the published workload (or
  move its ownership to the autoscaler: `kubectl apply --server-side --field-manager=handover-to-hpa` of a manifest
  carrying only `replicas`), or every deploy resets the group's size. `kubectl get <kind> <name> --show-managed-fields`
  shows which manager owns a field. Do not `kubectl apply` (client-side) over a Helm release.

The cluster as one node sees it is `aspire resource <node> membership` (or `/admin/membership`); orchestration state is
the platform's own (kubectl, k9s, Helm, the cloud console). Graceful stop phases go out over OTLP (the histogram
`daserver.stop.phase.duration`): a scrape rarely catches a node that is stopping.

## AI agents

`aspire agent init` configures the agents it detects to use `aspire agent mcp`, the Aspire MCP server: it lists resources, reads their logs and traces, and executes resource commands, the node commands above
included. `aspire mcp tools` and `aspire mcp call <resource> <tool>` reach MCP tools that resources themselves expose.

## Publish and deploy

```bash
aspire publish -o out          # Docker Compose files (or a Helm chart with -- Publish=k8s in the samples)
aspire deploy                  # to the environments the AppHost declares
aspire do sample-routes        # only the cluster's route check
```

Publishing and deploying first run the cluster's route check (`{cluster}-routes`): each node project lists what its
contracts declare and its actors and services handle, and a message no node handles, or a request two service types
answer, stops the pipeline before anything is written. The Compose services get a health check (the node in probe mode),
a stop grace period from the stop budget and a restart policy; Kubernetes workloads get readiness and liveness probes,
a preStop pause and a headless `{group}-peers` Service for seeds.
