# Target deployment shape (draft, not implemented)

What `DaServer.Aspire` and the samples should publish for Kubernetes and Docker Compose, after an audit of what they
publish today (`aspire publish` of both samples, Aspire 13.6.1, Kubernetes publisher 13.6.1-preview, run on
2026-10-20). Decided items join ADR-085 and ADR-087 when W10 promotes the drafts, after W12 and N9; this file is deleted then.

The step column follows the roadmap: **W7** samples, **W8** Native AOT (images, the Compose health check), **W11**
Kubernetes shape and the hot-restart rollout (ADR-085), **W12** deployment corrections, **W6** the manifest diff, the
`{cluster}-restart` command outside Kubernetes and the Compose shape it needs (one service per stateful node, a named
snapshot volume each), **N9** the upgrade check (roadmap development order, item 14). "Hook" means Aspire's `PublishAsKubernetesService` /
`PublishAsDockerComposeService`; "DA" means code in `DaServer.Aspire`. Ports per group are
[draft §2](programming-model.md#2-服务器与节点); how a hot restart works is [draft §10](programming-model.md#热重启).

## 0. Launch flows

One node binary, five ways to run it. Everything below uses an official package or tool; DaServer adds only what
follows from DaServer (ports, seeds, probes, the snapshot directory).

| Where | Build | Start | Code change |
|---|---|---|---|
| Local, JIT (daily work) | `dotnet run --file Game.Server/apphost.cs` (or `aspire run`): Aspire builds the server once | Each node is an `AddDotnetProject` resource (`Aspire.Hosting.Dotnet`, pinned; prerelease): Aspire runs the built apphost executable, one process per replica, no `dotnet run` CLI process beside it (no page says so: it rests on the implementation, which launches the project's `RunCommand` computed by `ComputeRunArguments`); debugger, dashboard, OTLP as with `AddProject` (W8 verifies that and OTLP, endpoints, replicas and attach, and falls back to `AddProject` if one fails) | Rebuild in the dashboard, or the dashboard's `{cluster}-restart`: rebuild, then the one rollout loop (section 5) with Aspire's resource restart as its adapter; snapshot in a per-node temp directory set by DA |
| Local, Native AOT (checking AOT) | `aspire publish` with `PublishAot=true` writes AOT images and a Compose file | `docker compose up` on that output: the same images as production; not for the edit-run loop | Publish again |
| Docker Compose | `aspire publish`: JIT images on `aspnet:11.0-resolute-chiseled`, AOT images on `runtime-deps:11.0-resolute-chiseled` (no .NET runtime in the image; the SDK picks it for `PublishAot`) | `docker compose up -d` (Aspire's `aspire deploy` for Compose) | The rollout loop (section 5): stateful groups hot restart (one service per node, snapshot on its named volume, section 8); gateways and stateless groups a plain rolling restart, one container at a time (section 8) |
| Kubernetes | `aspire publish -- Publish=k8s`: the same images, a Helm chart | `helm install` / `helm upgrade` | Stateful groups: hot restart (StatefulSet `OnDelete`, pod image patch, ADR-085); gateways and stateless groups: Deployment rollout (section 2) |
| Bare metal / VM | `dotnet publish -c Release -r linux-x64` (JIT: framework-dependent, needs the .NET runtime) or `-p:PublishAot=true -r <rid>` (one native executable, depends only on libc; verified: a 9.6 MB ASP.NET Core AOT node starts in an empty environment) | The platform's service manager, from the unit, plist or service below | Install the new build beside the old one (versioned directory, a `current` link), then `{cluster}-restart`: the rollout loop (section 5) with the service manager's restart as its adapter |

- **Program.cs** declares .NET's two service managers, each a no-op off its platform, so one binary runs everywhere:
  `builder.Host.UseSystemd().UseWindowsService();` ([draft §2](programming-model.md#2-服务器与节点)). launchd needs
  no code (below). `AddDaServer` calls none of them.
- **systemd**: `Type=notify` (`UseSystemd()` sends readiness and stopping), `RuntimeDirectory=daserver-<node>` with
  `RuntimeDirectoryPreserve=restart` (a tmpfs under `/run` that `systemctl restart` keeps,
  [systemd.exec(5)](https://www.freedesktop.org/software/systemd/man/latest/systemd.exec.html#RuntimeDirectory=)), and
  `Environment=DaServer__SnapshotPath=/run/daserver-<node>`; `TimeoutStopSec` at least the node's stop budget. A hot
  restart is `systemctl restart`.
- **launchd** (macOS): a plist with `Label`, `ProgramArguments`, `KeepAlive`, `ExitTimeOut` (launchd sends SIGTERM,
  then SIGKILL after it; launchd.plist(5) calls the default system-defined, so set it explicitly, at least the stop
  budget), `WorkingDirectory`, `EnvironmentVariables` with `DaServer__SnapshotPath` (a local directory),
  `StandardOutPath`/`StandardErrorPath` (logs go to stdout). A hot restart is `launchctl kickstart -k system/<label>`.
  The plist carries what a launchd process needs, so DaServer has no launchd code: `WorkingDirectory` set to the
  install directory (the content root; launchd starts in `/`), and in `EnvironmentVariables`
  `Logging__Console__FormatterOptions__ColorBehavior=Disabled` and a `Logging__Console__FormatterOptions__TimestampFormat`.
  The Generic Host's console lifetime already stops on SIGTERM.
- **Windows services**: `sc create`; `DaServer:SnapshotPath` (a local directory) in the node's configuration. A hot
  restart is `Restart-Service`.
- **AOT images from macOS or Windows**: Native AOT does not compile across operating systems
  ([cross-compile](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile)), so a Linux AOT image
  cannot come from the SDK container publish on those hosts. There DA publishes AOT nodes through
  `PublishAsDockerFile` with a generated two-stage Dockerfile: build in `mcr.microsoft.com/dotnet/sdk:11.0-resolute-aot`
  (the SDK with the native toolchain), run on `runtime-deps:11.0-resolute-chiseled`. The host needs only Docker. On
  Linux (CI included) the SDK container publish builds the image directly. W8. (.NET 11 images are on Ubuntu 26.04,
  `resolute`; there are no `noble` tags for it.)
- **Health checks** use the node's own executable in probe mode on every target (section 7), so neither image needs
  `dotnet`, curl or a shell.
- **Why not `AddExecutable` for local AOT**: Aspire has no API to publish and then launch a native binary; an
  executable resource loses the .NET project defaults (OTLP, endpoints, Rebuild, debugging) that DA would have to
  rebuild by hand. The Compose run of the real images checks AOT with official tools only.
- **Why the dashboard command locally**: `aspire do` runs its steps in a fresh process that cannot reach the running
  AppHost's orchestrator; the resource command runs inside it. Published targets use `aspire do {cluster}-restart`.

## 1. What is published today (audit)

| Area | Today | Where |
|---|---|---|
| Workload | Every group, stateful or not, is a `Deployment` (`RollingUpdate`, maxSurge 1, maxUnavailable 1), named `{group}-deployment` | `src/DaServer.Aspire/DaServerClusterExtensions.cs:133,252` |
| Seeds | Headless `{group}-peers` Service, `publishNotReadyAddresses: true`, port 7000 only | `DaServerClusterExtensions.cs:258-265` |
| Shutdown | `terminationGracePeriodSeconds` = budget + 10 s margin + 5 s preStop `sleep` (40 s default) | `DaServerClusterExtensions.cs:254-257` |
| Probes | readiness `/health` (delay 5, timeout 1), liveness `/alive` (delay 10, timeout 1); **no startupProbe** | `DaServerClusterExtensions.cs:199-201` |
| Ordinary Service | `{group}-service` ClusterIP with **http (admin), cluster and client ports** | Aspire default |
| Signing secret | One Aspire parameter `{cluster}-secret` (`secret: true`), but rendered **once per group** in the Helm values (`secrets.game.sample_secret`, `secrets.map.sample_secret`, ...), each defaulting to `""` | `DaServerClusterExtensions.cs:76-77,196`; generated `values.yaml` |
| Empty secret | Helm install without `--set` gives every node an empty secret: unauthenticated links (ADR-065), no resume tickets, every admin request refused. Nothing fails | `src/DaServer/Runtime/NodeOptions.cs:111`; `Cluster/SigningKeys.cs:21` |
| Rotation | `DaServer__AcceptedSigningSecret` exists (operations.md "Rotating the signing secret") but no AppHost parameter or Secret key carries it | `doc/operations.md:69-79` |
| DB credentials | `ConnectionStrings__actors` in the group's Secret (correct), but the role is the `postgres` superuser | generated `templates/game/secrets.yaml` |
| Schema | Each node creates the table at start under an advisory lock (`CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`) | `src/DaServer.Persistence.PostgreSql/PostgreSqlActorStore.cs:35-38,59-62,103-110` |
| Load generators | `bots` (10,000 bots) and `crowd` (1,000,000 buyers) are published as **Deployments**: they exit when done and Kubernetes restarts them, again and again | `samples/mmo/Sample.AppHost/AppHost.cs:63`, `samples/flashsale/FlashSale.AppHost/AppHost.cs:28` |
| Autoscalers | HPA and VPA `targetRef` hard-coded `kind: Deployment` | `samples/Shared/Publishing.cs:29,38`; `samples/flashsale/FlashSale.AppHost/Platform.cs:11` |
| PDB | Sample-level `maxUnavailable: 1` for game and map | `samples/mmo/Sample.AppHost/Platform.cs:20` |
| Compose health check | `dotnet /app/{assembly}.dll --DaServer:Probe=/health`: **fails on a Native AOT image** (no `dotnet` in it) | `DaServerClusterExtensions.cs:221` |
| Compose secrets | `${SAMPLE_SECRET}` from `.env`, empty if unset | generated `.env` |
| Not published at all | ServiceAccount (pods get the namespace default and an automounted token), securityContext, resources, topology spread, NetworkPolicy, Jobs, CPU count for shards | - |
| Shard count | `NodeOptions.Shards = Environment.ProcessorCount`, which follows a CPU **limit**; the samples set only requests (VPA), so a pod gets one shard per **host** core | `src/DaServer/Runtime/NodeOptions.cs:43` |
| OTLP | Points at the in-cluster Aspire dashboard (`k8s-dashboard-service:18889`); no headers parameter | Aspire default |

Aspire facts that the target below relies on (checked on the built assembly): `KubernetesResource.Workload`,
`.Secret`, `.ConfigMap` and `.Service` are settable; typed `StatefulSet`, `Role`, `RoleBinding`, `ServiceAccountV1`,
`Secret`, `HorizontalPodAutoscalerV2` exist; **no typed Job, CronJob, PodDisruptionBudget or NetworkPolicy** (a
plain `BaseKubernetesResource` subclass, like the samples' `Manifest`, writes them). Aspire renders a StatefulSet
on its own only for a workload bound to a persistent volume
([aspire.dev: persistent volumes](https://aspire.dev/deployment/kubernetes/persistent-volumes/)); DA replaces the
`Deployment` with a `StatefulSet` built from the same pod template. Secret parameters go to `secrets.<resource>` in
`values.yaml` and to the resource's Secret ([aspire.dev: clusters](https://aspire.dev/deployment/kubernetes/clusters/)).
The Compose model has `Secrets`, `ReadOnly`, `Tmpfs`, `CapDrop`, `SecurityOpt`, `User` and `Profiles`.

## 2. Workloads

How DA tells the kinds apart: from the committed `wire.manifest` (`actor` / `hosted-by`): a group hosting any actor
type that is not `[Stateless]` is stateful; a group hosting only `Connection` and `[Stateless]` types is not.

### Stateful node groups (host actors): StatefulSet

| Setting | Target | Why | How | Step |
|---|---|---|---|---|
| Kind | `StatefulSet` named `{group}` (not `{group}-statefulset`), so pods are `game-0`, `game-1`: the same names as the run-mode resources and the node names | Hot restart keeps the pod and its `emptyDir` (ADR-085); stable names match the dashboard and logs | DA: replace `resource.Workload` in the hook | W11 |
| `serviceName` | `{group}-peers` (the existing headless Service, `publishNotReadyAddresses: true`); per-pod DNS `game-0.game-peers` | StatefulSets require a governing headless Service ([StatefulSet](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/)) | DA | W11 |
| `podManagementPolicy` | `Parallel` | Nodes join by gossip and need no order; `OrderedReady` serializes scale-ups (a flash-sale burst waits pod by pod) and can wedge on one broken pod ("forced rollback"). Rolling updates stay one pod at a time | DA | W11 |
| `updateStrategy` | `OnDelete` for groups without client connections; `RollingUpdate` (its default, one pod at a time; no `maxUnavailable`, which is feature-gated for StatefulSets) for stateful groups that also accept clients (flash sale `sale`) | `OnDelete`: a template change replaces no pod; DaServer's rollout decides per pod (hot restart or delete-and-recreate). A group hosting `Connection` cannot hot restart ([draft §10](programming-model.md#热重启)) | DA | W11 |
| Template image vs. pods | `helm upgrade` (or `aspire deploy`) sets the template image; the rollout patches only pods, never the template, so the live object never drifts from the Helm release (ADR-085) | The next `helm upgrade` would otherwise write the old tag back | Rollout Job | W11 |
| Replicas | `spec.replicas` from `AddNodes(replicas)`; an HPA may own it (`scale` subresource works on StatefulSets, [HPA](https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/)) | Scale-in removes the highest ordinal, which leaves gracefully | DA | W11 |
| Grace period | `terminationGracePeriodSeconds` = stop budget + margin + preStop, as today | Unchanged ([pod termination](https://kubernetes.io/docs/concepts/workloads/pods/pod-lifecycle/#pod-termination)) | DA (exists) | - |
| preStop | No `sleep` on hot-restart groups. The kubelet's container kill runs preStop whatever the reason, an image patch included (to prove on kind in W11), so a `sleep` would add its seconds to every hot restart's pause | [Lifecycle hooks](https://kubernetes.io/docs/concepts/containers/container-lifecycle-hooks/) | DA | W11 |
| Snapshot volume | `emptyDir: {medium: Memory, sizeLimit: <2 x expected world bytes>}` at `/var/run/daserver/snapshot` (`DaServer__SnapshotPath`) | Survives container restarts, not pod deletion; tmpfs counts against the memory limit ([emptyDir](https://kubernetes.io/docs/concepts/storage/volumes/#emptydir)), so a node refuses to arm when its working set plus the snapshot estimate passes about 80% of the cgroup limit ([draft §10](programming-model.md#热重启)) | DA | W11 |
| Persistent volume | **None** (no `volumeClaimTemplates`) | State lives in the actor store; a snapshot outlives one restart only. A PV would be zone-bound and slow | - | W11 |
| PDB | `maxUnavailable: 1`, `unhealthyPodEvictionPolicy: AlwaysAllow`; stays an application choice (CLAUDE.md), the samples keep adding it on the StatefulSet's pods | With the default policy, an outage that makes every pod unready blocks every node drain ([PDB](https://kubernetes.io/docs/tasks/run-application/configure-pdb/#unhealthy-pod-eviction-policy)) | Sample hook | W11 |
| Topology spread | Soft spread over `kubernetes.io/hostname` (`whenUnsatisfiable: ScheduleAnyway`); zone spread left to the app | One machine's loss should not take several nodes' actors at once ([topology spread](https://kubernetes.io/docs/concepts/scheduling-eviction/topology-spread-constraints/)) | DA (hostname), sample (zones) | W12 |
| CPU count | `env DOTNET_PROCESSOR_COUNT` from `resourceFieldRef: requests.cpu` (rounded up) when no CPU limit is set | The world count defaults to `ProcessorCount`, which ignores requests: today a pod on a 64-core host runs 64 worlds ([downward API](https://kubernetes.io/docs/tasks/inject-data-application/environment-variable-expose-pod-information/)) | DA | W12 |
| Image pre-pull | Before patching a pod, the rollout makes the node pull the new image (a short pod pinned to that node with the new image, exiting at once) | The kubelet kills the old container and only then pulls the image; a pull inside the pause defeats "hundreds of milliseconds" | Rollout Job | W11 |

Moving an existing release from Deployment to StatefulSet deletes the Deployment: every pod of the group stops at
once (each leaves gracefully and saves). The upgrade note says so; scale the group down first or accept the pause.

### Stateless groups and gateways: Deployment

On Kubernetes gateways roll surge then drain, so every client moves exactly once and only the load balancer chooses where (Compose rolls them plainly, section 8).

| Setting | Target | Why | How | Step |
|---|---|---|---|---|
| Kind (gateways) | `Deployment`, `RollingUpdate` with `maxSurge: 100%`, `maxUnavailable: 0`: every new pod starts and is Ready before any old pod stops; the Deployment then scales the old ReplicaSet down | A client retired from an old gateway must not land on another old one and be retired again; with every new pod ready first, the load balancer reaches only new gateways. Cost: twice the gateway pods for the length of the roll (gateways are stateless and small) | DA | W11 |
| Draining a gateway | On SIGTERM it fails readiness first (its endpoint goes not-ready/`terminating`, so the load balancer sends it no new connections), waits a short deregistration delay, then retires its connections in waves spread over `DrainSpread`: `ControlOp.Retire`, make before break (wave bounds and the push-order fence: [architecture.md section 3](architecture.md#retire-and-rebalancing)) | The client learns nothing new about the topology: it only receives `Retire` and reconnects to the same address; waves keep a mass retire from becoming a reconnect storm | DaServer (exists, ADR-026), DA (grace period covers delay + waves) | W11 |
| Connection rebalancing | By load (ADR-089; rule and defaults in [architecture.md section 3](architecture.md#retire-and-rebalancing)): a gateway sheds only its excess above the group average, rate-limited, so a balanced group retires nobody. DA passes `DaServer:Clients:RebalanceTolerance` and the optional, off-by-default `maxConnectionAge` from `WithClientConnections` | After a scale-out, long-lived connections stay where they are; an age would also move clients of a balanced group | DaServer, DA (the parameters) | W5 / W11 |
| Kind (stateless groups) | `Deployment`, `RollingUpdate` maxSurge 1, maxUnavailable 0 (today 1) | Start new, then stop old (ADR-085); no capacity dropped | DA | W11 |
| Services | As for stateful groups (below) | | DA | W11 |
| HPA | Allowed (flash sale uses one); `targetRef` names the right kind | `Publishing.DeploymentOf` assumes Deployment | Sample | W11 |
| Probes, grace, preStop, spread, CPU count | As for stateful groups; gateways keep a preStop delay for the endpoint to leave the load balancer | | DA | W11 / W12 |

Bare metal: start the new gateway processes (another port or host behind the load balancer) before draining the old
ones; the operator's load balancer configuration decides. Compose: section 8.

### Probes (both kinds)

| Probe | Target | Why |
|---|---|---|
| startupProbe | `/alive`, period 2 s, failureThreshold 45 (90 s) | Covers waiting for the store at start (until the table answers, the node is not ready and retries with one backoff; there is no separate initialize deadline) and a snapshot or records load; liveness then needs no `initialDelaySeconds` ([probes](https://kubernetes.io/docs/concepts/configuration/liveness-readiness-startup-probes/)) |
| readinessProbe | `/health` (joined, not leaving), timeout 2 s; once the node has started, the store is not part of it: the Npgsql data source is registered with `DisableHealthChecks`, or `/health` maps only the checks tagged `daserver` (today `AddKeyedNpgsqlDataSource` adds its check to `/health`) | A store outage must not take every node out of service: active actors keep running and activations back off ([draft §6](programming-model.md#6-生命周期)); 1 s timeouts flap under GC or load |
| livenessProbe | `/alive` (no stalled world), timeout 2 s | Unchanged meaning |

How: DA (`WithHttpProbe(ProbeType.Startup, ...)`), W12.

### Kubernetes Services (both kinds)

How ports are declared per group is [draft §2](programming-model.md#2-服务器与节点); these are the Services that follow.

| Service | Target | Why | How | Step |
|---|---|---|---|---|
| `{group}-peers` (headless) | The cluster port (7000) only, as today | Node links go pod to pod; seeds | DA (exists) | - |
| `{group}-service` (ClusterIP) | The HTTP port (health, admin) only, reachable inside the cluster; today it also carries the cluster and client ports | Smaller surface; a load-balanced cluster port is never right | DA | W11 |
| Client Service | Only for a group with `WithClientConnections(port)`: the declared port (TCP and WebSocket), one address balanced over the replicas. Its `type` and annotations (`LoadBalancer`, `externalTrafficPolicy`) come from the application's hook, as the mmo sample does today | CLAUDE.md: the load balancer's kind is the platform's and the application's | DA (Service), sample hook (kind) | W11 |
| Public HTTP | Only for a group with Aspire's `WithExternalHttpEndpoints()` (webhooks) | Admin and probes stay internal | Aspire (exists) | - |
| Per-replica public addresses | None: direct connect to one pod (Agones-style) is a non-goal; a later direct-connect feature would add it | | - | - |

## 3. Jobs

| Job | What it runs | When | Why not today's way | How | Step |
|---|---|---|---|---|---|
| `{cluster}-schema` | The node image in schema mode (`--DaServer:Migrate`, added by W6): `PostgreSqlActorStore.Schema` under the advisory lock, with a DDL role | Always a plain Job named with the release revision (`ttlSecondsAfterFinished`); no Helm hook variant (a pre-install hook would run before an in-chart database exists, and one shape is enough). Nodes never create the table: until it answers they are not ready and retry with one backoff, bounded by the startup probe. Aspire run mode runs the same mode as a one-shot resource; in-process tests (`TestCluster`) call `Schema` once before their nodes start | Creation at node start gives the runtime role DDL rights, and every node start, scale-up and hot restart runs `CREATE INDEX IF NOT EXISTS`, which takes a `SHARE` lock on the live table before it checks whether the index exists ([CREATE INDEX](https://www.postgresql.org/docs/current/sql-createindex.html)): every save of the cluster waits for it, and it queues behind any long transaction. Hand-written SQL and the advisory lock stay: a migrations library is not worth one table and one index | DA (`WithActorStore(..., schema: SchemaMode.Job)`), DaServer.Persistence.PostgreSql (missing table: not ready, retried); the mode itself is W6 | W12 |
| `{cluster}-upgrade-check` | The new image in check mode: sends its own `wire.manifest` to a running node's dry-run arm (`/admin/restart?dryRun=true`, admin token), where the one manifest diff runs ([architecture.md section 5](architecture.md#5-migration-upgrades-hot-restart)) and answers the hot-restart path (in place, through records, rolling); the check prints it and fails on an incompatible change, and on a release that would write record fields or record types the running build cannot keep (no safe rollback) unless forced | A plain Job started by the deploy step (`aspire deploy`) before the template changes; Compose: a step of `aspire deploy` | Today only the route check runs, on the publishing machine, against the build alone; nothing compares with what is running | DA (step + Job), DaServer (check mode, dry-run arm) | N9 |
| `{cluster}-rollout-{n}` | The new image in rollout mode, ServiceAccount `{cluster}-rollout`: per pod of each stateful group, one at a time and the coordinator last: pre-pull, arm the node (`/admin/restart`; refused while a member joins or leaves, another node holds the restart permit, or memory is short), patch the pod's image (the kubelet's SIGTERM starts the restart; an arm with no patch after it expires), wait for ready; or delete the pod when the path is rolling. A **same-image roll** (secret rotation, a config change) has no image to patch: the Job arms with `/admin/restart?exit=true` (the same admin verb; `mode=unload` instead unloads every actor first, the full-unload restart), the node runs hot-restart step 1 itself and exits, and the kubelet restarts the container in the same pod (`restartPolicy: Always`), which re-reads env and `envFrom` | Started by `aspire do {cluster}-restart` or `kubectl create job` | See section 5 for why in-cluster | DA (command creates the Job), DaServer (mode) | W11 |
| Load generators | `bots`, `crowd`: **a Job** (`backoffLimit: 0`, `ttlSecondsAfterFinished`) behind `-- LoadTest=true`, otherwise excluded from publish | They run to completion; as Deployments they restart forever (a 10,000-bot storm every few minutes against the deployed cluster) | Sample: `ExcludeFromManifest()` in publish mode unless asked, the Job as an `AdditionalResource`; Compose: `profiles: [loadtest]` | W12 |

Not cluster workloads: the **E2E tests** run in CI as the driver (they start the cluster through Aspire locally or
on kind, launch the load-generator Job and assert on its result and the nodes' metrics), and **BenchmarkDotNet**
runs on the developer's machine and in CI (it measures in-process code). Neither is published.

CronJobs: **none**. Considered: record pruning (orders and other finished actors: application data, the
application's job), backups (the database operator's), secret rotation (a three-step roll, not a schedule),
certificate renewal (the platform's, cert-manager). Sources: [Job](https://kubernetes.io/docs/concepts/workloads/controllers/job/),
[TTL after finished](https://kubernetes.io/docs/concepts/workloads/controllers/ttlafterfinished/),
[Helm hooks](https://helm.sh/docs/topics/charts_hooks/).

## 4. Secrets and configuration

| Item | Target | Why | How | Step |
|---|---|---|---|---|
| Signing secret | **One** cluster Secret `{cluster}-signing` with keys `current` and optional `accepted`; every node mounts it as files at `/run/secrets/daserver` (`items`: `current` → `DaServer__SigningSecret`, `accepted` → `DaServer__AcceptedSigningSecret`, `optional: true` for `accepted`) and reads them through .NET's key-per-file configuration provider (`AddKeyPerFile("/run/secrets/daserver", optional: true)`, `__` becomes `:`). Files, not environment variables: the value stays out of `kubectl describe`, `docker inspect` and process dumps, and Compose mounts its `secrets:` at the same path, so one mechanism serves both | Today each group has its own copy in the values (four `--set`s in the mmo chart that must agree; one typo splits the cluster: its Hellos are refused). One object is what the rotation steps change | DA: in publish mode, leave the variable out of the generated per-group Secret, add the Secret and the volume mount in the hook; DaServer.Hosting: the key-per-file source | W12 |
| Missing secret | Published nodes refuse to start without a signing secret (`DaServer__RequireSigningSecret=true`, set by DA in publish mode only; run mode and tests stay open) | Today an empty value silently yields an unauthenticated cluster; Helm templates from Aspire cannot use `required` | Runtime option + DA | W12 |
| Existing Secret | `WithSigningSecret(existingSecretName)`: reference a Secret made by External Secrets, Sealed Secrets or the CSI driver instead of a Helm value | Secret values in Helm values end up in release history and CI logs ([secrets good practices](https://kubernetes.io/docs/concepts/security/secrets-good-practices/)) | DA | W12 |
| Rotation | Change `accepted`, roll; swap; roll; drop. On Kubernetes "roll" for a stateful `OnDelete` group is the rollout Job's same-image roll (section 3): each node exits after its snapshot and reads the files again when its container restarts (the node reads them at start; the kubelet refreshes mounted Secret files in place) | The existing three-step rotation (operations.md) needs no new mechanism, only the key in the Secret | Docs | W12 |
| Admin token | The rollout, upgrade-check and Dashboard workloads mount `{cluster}-signing` and derive the token in memory at start (`AdminToken.For`, HMAC of `daserver-admin`); they send only the token. No `{cluster}-admin` Secret and no token in Helm values or release history | The token cannot sign links or tickets; the signing secret never leaves the cluster's Secret | DA (mounts), DaServer (derivation in those modes) | W11 |
| Database | Two roles: owner (schema Job) and runtime (`SELECT, INSERT, UPDATE` on the table). Connection strings stay in Secrets (Aspire already puts `ConnectionStrings__actors` there); `AddConnectionString` parameters are secret | Least privilege; no DDL from nodes | Samples (two connection strings), DA (`WithActorStore(runtime, owner)`) | W12 |
| OTLP | In publish mode DA points `OTEL_EXPORTER_OTLP_ENDPOINT` at the OpenTelemetry Collector (a plain parameter), `OTEL_EXPORTER_OTLP_HEADERS` (API keys) a `secret: true` parameter; the samples do not publish the Aspire dashboard | Aspire wires OTLP to its in-cluster dashboard: telemetry would have two destinations; the Collector fans out to Prometheus, Loki and Tempo ([architecture.md section 8](architecture.md#telemetry)) | DA, samples | W12 |
| Non-secret config | Stays in Aspire's per-group ConfigMap (`envFrom`): cluster name, ports, seeds, flush interval | Correct today. A ConfigMap change restarts nothing; Deployments get a `checksum/config` pod annotation so `helm upgrade` rolls them; `OnDelete` StatefulSets replace no pod on a template change, so their config change is applied by the rollout Job's same-image roll (section 3) | DA (annotation) | W12 |

Compose: Aspire writes secret parameters as `${VAR}` in `docker-compose.yaml` and empty keys in `.env`. Target: the
same files as on Kubernetes. A top-level `secrets:` entry per key (`daserver-signing-current` from
`file: ./secrets/signing-current`, or `environment: SAMPLE_SECRET` for CI) is granted to every node, the schema
service and the rollout step with `target: /run/secrets/daserver/DaServer__SigningSecret` (and the accepted key the
same way); the node reads them through the same key-per-file source, and `DaServer__RequireSigningSecret` makes a
missing file a refused start. `./secrets/` is never committed. Rotation is the same three steps: change the files,
roll.

## 5. RBAC and who runs the hot restart

| Identity | Permissions | Why |
|---|---|---|
| Nodes: ServiceAccount `{cluster}-node`, `automountServiceAccountToken: false` | None | Discovery is DNS (ADR-030); a node with an API token is a foothold for nothing it needs ([opt out of automounting](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/#opt-out-of-api-credential-automounting)) |
| Schema and upgrade-check Jobs | None (automount off) | They talk to the database and the nodes' admin plane only |
| Rollout Job: ServiceAccount `{cluster}-rollout` + Role + RoleBinding in the release namespace | `pods`: get, list, watch, patch, delete; `pods` create (the pre-pull pods); `statefulsets`: get, list, watch (read the template image and replicas). No `statefulsets` patch (Helm owns the template) | The two writes the rollout makes; namespaced, nothing cluster-wide ([RBAC](https://kubernetes.io/docs/reference/access-authn-authz/rbac/)) |
| The operator or CI | `create` on `jobs`, `get` on `pods/log` in the namespace | Starting and watching the rollout |

**One rollout loop, one adapter per platform.** The loop is the node image's rollout mode: for each stateful group,
one node at a time and the coordinator last: pre-pull the new image, arm (`/admin/restart`), trigger the restart,
wait for ready, next; a rolling path or a group without hot restart (gateways, stateless groups) skips the arm. Only
"trigger the restart" differs per platform: Aspire run mode (the resource's restart command, inside the AppHost),
Compose (`docker compose up -d --no-deps <node>`), Kubernetes (patch the pod's image, or delete the pod on the rolling
path) and bare metal (the service manager's restart). There is no second implementation.

**Who runs it on Kubernetes: an in-cluster Job, started by `aspire do {cluster}-restart` (W11).** The alternative, the operator's CLI with a
kubeconfig, would need `pods/proxy` or port-forwards to reach each pod's admin plane, would stop half-way when a
laptop sleeps during a minutes-long one-pod-at-a-time roll, and would carry the operator's broad credentials. The Job
runs the new image itself (so it has the new `wire.manifest`), reaches pods by IP inside the NetworkPolicy, and its
token can do only the patches above. Aspire has typed `Role`, `RoleBinding` and `ServiceAccountV1`: DA adds them in
the hook of the first stateful group. Compose has no API: there `{cluster}-restart` runs the same loop on the deploying machine (see 8).

## 6. Network policy

DA emits these (the ports and groups are DaServer's knowledge; CNIs without NetworkPolicy support ignore them):

| Policy | Allows | Why |
|---|---|---|
| `{cluster}-cluster-plane` (all node pods) | Ingress on the cluster port (7000) only from pods labelled `daserver.io/cluster={cluster}` | Node links; defence in depth beside link authentication (ADR-065) |
| `{cluster}-http-plane` | Ingress on the HTTP port from the release's Jobs and from a namespace selector given as a parameter (`metrics-namespace`, Prometheus); kubelet probes come from the node and most CNIs allow them. From anywhere only for groups with `WithExternalHttpEndpoints()` | `/admin` (restart, unload) sits on this port, today open to the whole cluster network through `{group}-service` |
| `{cluster}-clients` (groups with `WithClientConnections(port)`) | Ingress on the declared client port from anywhere | The load balancer's traffic |
| Store | Egress from node pods and the schema Job to the database port; ingress to the in-chart PostgreSQL only from those | Samples (the database is the application's) |

Default deny for the namespace is the platform's call. Source: [network policies](https://kubernetes.io/docs/concepts/services-networking/network-policies/).

## 7. Pod security and images

| Setting | Target | Why |
|---|---|---|
| Pod `securityContext` | `runAsNonRoot: true`, `runAsUser/runAsGroup: 1654` (the .NET images' `app` user, `APP_UID`), `seccompProfile: RuntimeDefault` | Pod Security Standard "restricted" ([PSS](https://kubernetes.io/docs/concepts/security/pod-security-standards/)) |
| Container `securityContext` | `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `readOnlyRootFilesystem: true` | Ports are above 1024 (8080, 7000, 7100): no capability needed ([security context](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/)) |
| Writable paths | `/tmp` (`emptyDir`, `sizeLimit: 64Mi`: .NET's diagnostics IPC socket and temp files) and the snapshot directory (stateful groups). Nothing else: logs go to stdout, the HTTP plane keeps no Data Protection keys (no cookies) | Verify in W12 by running the E2E with a read-only root |
| Resources | Requests are the application's (the samples' VPAs); DA sets none, but derives `DOTNET_PROCESSOR_COUNT` from them (section 2) | CLAUDE.md: sizing is the platform's |
| Image (Native AOT, ADR-083) | Base `mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled` (the SDK's AOT default is a chiseled runtime-deps variant; `-extra` only with `InvariantGlobalization=false`, which the samples do not use); JIT nodes `aspnet:11.0-resolute-chiseled`. Set `ContainerFamily` in `ServerNode.props` / the flash-sale project | No shell, no package manager, non-root by default ([container publish](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration), [container images](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images)); debugging is `kubectl debug` with an ephemeral container ([debug a pod](https://kubernetes.io/docs/tasks/debug/debug-application/debug-running-pod/#ephemeral-container)) |
| Compose health check | One form for JIT and AOT images: `["CMD", "/app/{assembly}", "--DaServer:Probe=/health"]`, the node's own executable in probe mode (an AOT image is that executable; a JIT image gets it as the apphost, which publish emits by default: DA makes sure `UseAppHost` stays on). Kubernetes uses `httpGet` probes on the same endpoints, so one probe implementation serves both | An AOT image has no `dotnet` (and distroless images have no curl), so today's `dotnet /app/{assembly}.dll` check fails every AOT node; Compose then never reports it healthy and `service_healthy` dependants never start |
| One image per node project | Schema, upgrade-check and rollout Jobs run the node's own image with a mode flag (like the `Probe` mode) | No extra image to build, version or scan |

Steps: securityContext, `/tmp` and service account W12 (the snapshot volume W11); images and the Compose health check W8.

## 8. Docker Compose equivalents (where they differ)

| Kubernetes | Compose |
|---|---|
| StatefulSet | Stateful groups: one service per node (`map-0`, `map-1`, …) with a fixed `hostname` (the node name) and a named volume of its own for the snapshot, since recreating a container (any image change) loses `tmpfs` and a replica's generated hostname. Hot restart: `{cluster}-restart` on the deploying machine runs the rollout loop (section 5) with `docker compose up -d --no-deps map-0` as its trigger |
| Deployment (gateways) | A plain rolling restart in the same loop, one container at a time: the stopping gateway retires its connections (section 2) and its clients resume on another gateway. A client may move twice during a gateway roll, which is acceptable at Compose's single-host scale; no surge |
| Deployment (stateless groups) | One service per group with `deploy.replicas`, rolling |
| Jobs | One-shot services (`restart: "no"`): `schema` with nodes `depends_on: {schema: {condition: service_completed_successfully}}`; load generators under `profiles: [loadtest]` |
| Upgrade check, rollout | Steps of `aspire deploy` / `{cluster}-restart`, run on the deploying machine: the check calls a node's dry-run arm, the rollout is the same loop |
| Secrets | Compose `secrets:` mounted at `/run/secrets/daserver` (section 4), the same files as on Kubernetes; `./secrets/` and `.env` never committed |
| securityContext | `read_only: true`, `tmpfs: [/tmp]`, `user: "1654"`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]` |
| NetworkPolicy | Two networks: an `internal: true` network for the cluster and HTTP planes, and the default one only for client-facing services and the dashboard |
| RBAC | None (no API) |
| Health check | The node executable in probe mode (section 7); `postgres` gets `pg_isready` so `game` can wait for `service_healthy` instead of `service_started` |

## 9. Steps

The work is the roadmap's (W6, W7, W8, W11, W12, N9); the Step column of each table above says which step
builds that row. Docs to follow when merged are listed under W10 in the roadmap.
