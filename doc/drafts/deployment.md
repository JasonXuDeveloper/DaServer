# Target deployment shape (draft, not implemented)

What `DaServer.Aspire` and the samples should publish for Kubernetes and Docker Compose, after an audit of what they
publish today (`aspire publish` of both samples, Aspire 13.6.1, Kubernetes publisher 13.6.1-preview, run on
2026-10-20). Decided items join ADR-085 and ADR-087 when W10 promotes the drafts, after W12 and N9; this file is deleted then.

The step column follows the roadmap: **W7** samples, **W8** Native AOT (images, the Compose health check), **W11**
Kubernetes shape and the hot-restart rollout (ADR-085), **W12** deployment corrections, **W6** the manifest diff, the
`{cluster}-restart` command outside Kubernetes and the Compose shape it needs (a pair of services per stateful node and a
holder, a named handover volume each), **N9** the upgrade check (roadmap development order, item 14). "Hook" means Aspire's `PublishAsKubernetesService` /
`PublishAsDockerComposeService`; "DA" means code in `DaServer.Aspire`. Ports per group are
[draft §2](programming-model.md#2-服务器与节点); how a hot restart works is [draft §10](programming-model.md#热重启).

## 0. Launch flows

One node binary, five ways to run it. Everything below uses an official package or tool; DaServer adds only what
follows from DaServer (ports, probes, the handover directory, the pair of instances per stateful node, the membership
store when a cluster has more than one node).

| Where | Build | Start | Code change |
|---|---|---|---|
| Local, JIT (daily work) | `dotnet run --file Game.Server/apphost.cs` (or `aspire run`): Aspire builds the server once | Each node is an `AddDotnetProject` resource (`Aspire.Hosting.Dotnet`, pinned; prerelease): Aspire runs the built apphost executable, one process per replica, no `dotnet run` CLI process beside it (no page says so: it rests on the implementation, which launches the project's `RunCommand` computed by `ComputeRunArguments`); debugger, dashboard, OTLP as with `AddProject` (W8 verifies that and OTLP, endpoints, replicas and attach, and falls back to `AddProject` if one fails) | Rebuild in the dashboard, or the dashboard's `{cluster}-restart`: rebuild, then the one rollout loop (section 5) restarting the other replica of the node's pair; the handover directory a per-node temp directory set by DA |
| Local, Native AOT (checking AOT) | `aspire publish` with `PublishAot=true` writes AOT images and a Compose file | `docker compose up` on that output: the same images as production; not for the edit-run loop | Publish again |
| Docker Compose | `aspire publish`: JIT images on `aspnet:11.0-resolute-chiseled`, AOT images on `runtime-deps:11.0-resolute-chiseled` (no .NET runtime in the image; the SDK picks it for `PublishAot`) | First the database, then the schema one-shot on the new image: `docker compose up -d --wait postgres` (when the database is in the Compose file; `--wait` waits for its `pg_isready` health check), then `docker compose up --no-deps --force-recreate --exit-code-from schema schema`, which runs in the foreground and returns the schema container's exit code (`--exit-code-from` implies `--abort-on-container-exit`; `--no-deps` keeps it from starting nodes); a non-zero code stops the deploy; then `docker compose up -d --no-recreate` (Aspire's `aspire deploy` for Compose; DA adds both): new services start, existing containers are left alone, and the rollout loop then recreates each node one at a time. `--no-recreate` alone would start the exited schema container again on its old image, so the release's migration would never run. A plain `up -d` would recreate every changed service at once, a whole-cluster outage with no arm (E2E `ComposeDeployRecreatesNoNode`) | The rollout loop (section 5): stateful groups hot restart (an a/b pair of services per node sharing a named volume, section 8); gateways and stateless groups a plain rolling restart, one container at a time (section 8) |
| Kubernetes | `aspire publish -- Publish=k8s`: the same images, a Helm chart | `helm install` / `helm upgrade` | Stateful groups: hot restart (StatefulSet `OnDelete`, two containers per pod, the parked one's image applied, ADR-085); gateways and stateless groups: Deployment rollout (section 2) |
| Bare metal / VM | `dotnet publish -c Release -r linux-x64` (JIT: framework-dependent, needs the .NET runtime) or `-p:PublishAot=true -r <rid>` (one native executable, depends only on libc; verified: a 9.6 MB ASP.NET Core AOT node starts in an empty environment) | The platform's service manager, from the unit, plist or service below | Install the new build beside the old one (versioned directory, a `current` link), then `{cluster}-restart`: the rollout loop (section 5) with the service manager's restart as its adapter |

- **A node without a store** starts only with `DaServer:Membership:SingleNode = true` (one node, the in-memory
  membership table; [architecture.md section 4](architecture.md#4-placement-activation-persistence)); any other node
  without a membership store refuses to start, and so does a node with `SingleNode` and a store. DA sets `DaServer__Membership__SingleNode=true` on the node of an
  AppHost with one node and no store, in run mode and in what it publishes, and adds a PostgreSQL resource to an
  AppHost with more than one node and no store.
- **Program.cs** declares .NET's two service managers, each a no-op off its platform, so one binary runs everywhere:
  `builder.Host.UseSystemd().UseWindowsService();` ([draft §2](programming-model.md#2-服务器与节点)). launchd needs
  no code (below). `AddDaServer` calls none of them.
- **systemd**: `Type=notify` (`UseSystemd()` sends readiness and stopping). A stateful node is a pair of instances of
  one template, `daserver@.service`, with `Environment=DaServer__Instance=%i` and `EnvironmentFile=/etc/daserver/%i.env`
  (one file per instance, `map-0-a.env` and `map-0-b.env`, both naming `DaServer__Name=map-0` and
  `DaServer__HandoverPath=/run/daserver/map-0`, since no specifier strips the `-a`/`-b` suffix from `%i`); both
  instances are enabled. The handover directory is created at boot by a `tmpfiles.d` entry
  (`d /run/daserver/<node> 0700 daserver daserver -`,
  [tmpfiles.d(5)](https://www.freedesktop.org/software/systemd/man/latest/tmpfiles.d.html)), not by
  `RuntimeDirectory=`: systemd removes a unit's runtime directory when that unit stops, and the two instances share
  it. `TimeoutStopSec` at least the node's stop budget; `Restart=on-failure` with `RestartSec=1` (the isolation exit's
  code 75 and a crash start it again; an instance that handed over never exits: it parks in place). A hot restart restarts the instance
  that is not the node: `systemctl restart daserver@<other>`. A gateway or stateless group needs no pair.
- **launchd** (macOS): two jobs per stateful node, each a plist with `Label`, `ProgramArguments`, `KeepAlive`
  (`SuccessfulExit = false`, so the isolation exit's 75 and a crash restart it; a handed-over instance parks and never exits), `ExitTimeOut`
  (launchd sends SIGTERM, then SIGKILL after it; launchd.plist(5) calls the default system-defined, so set it
  explicitly, at least the stop budget), `ThrottleInterval` 1 (launchd starts a job at most once per
  `ThrottleInterval`, 10 s by default), `WorkingDirectory` (the install directory, the content root; launchd starts in
  `/`), `EnvironmentVariables` with `DaServer__Instance`, `DaServer__Name`, `DaServer__HandoverPath` (a local
  directory), `Logging__Console__FormatterOptions__ColorBehavior=Disabled` and a
  `Logging__Console__FormatterOptions__TimestampFormat`, and `StandardOutPath`/`StandardErrorPath` (logs go to
  stdout). A hot restart is `launchctl kickstart -k system/<other label>`: it starts the job whether or not it runs,
  and kills a running parked instance with SIGKILL, harmless since it holds nothing (`launchctl kill SIGTERM` would
  leave a cleanly stopped job not restarted by `KeepAlive`, and fails on one that already exited). DaServer has no
  launchd code; the Generic Host's console lifetime already stops on SIGTERM.
- **Windows services**: two services per stateful node, `sc create`, each with `DaServer:Instance`, `DaServer:Name`
  and `DaServer:HandoverPath` (a local directory) in its configuration, under one service account (a dedicated user
  or gMSA), or with a `PipeSecurity` ACL on the pipe granting the other service's SID, since `CurrentUserOnly` refuses
  a different account. A hot restart is `Restart-Service <other>`. The isolation exit (architecture.md section 4)
  cannot rely on a clean exit there: a Generic Host service that stops cleanly reports `SERVICE_STOPPED`, and the
  service manager's recovery actions never fire. So the node ends it with exit code 75
  (`DaServer.ExitCodes.RestartRequested`, the same code on every platform), set on the `WindowsServiceLifetime` (a
  `ServiceBase`) as `ExitCode` before `StopApplication`: the service manager reads only that, not the process's exit
  code, and the lifetime never sets it itself. The documented install sets `sc failure <svc> reset= 0 actions=
  restart/1000` and `sc failureflag <svc> 1`, which makes the service manager restart a service that stops with a
  non-zero code.
- **AOT images from macOS or Windows**: Native AOT does not compile across operating systems
  ([cross-compile](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile)), so a Linux AOT image
  cannot come from the SDK container publish on those hosts. There DA publishes AOT nodes through
  `PublishAsDockerFile` with a generated two-stage Dockerfile: build in `mcr.microsoft.com/dotnet/sdk:11.0-resolute-aot`
  (the SDK with the native toolchain), run on `runtime-deps:11.0-resolute-chiseled`. The host needs only Docker. On
  Linux (CI included) the SDK container publish builds the image directly. W8. The images are Ubuntu 26.04 (`resolute`)
  variants; DA sets `ContainerFamily=resolute-chiseled` explicitly rather than relying on the SDK's default family
  ([.NET on Ubuntu 26.04](https://devblogs.microsoft.com/dotnet/whats-new-for-dotnet-in-ubuntu-2604/)).
- **Health checks** use the node's own executable in probe mode on every target (section 7), so neither image needs
  `dotnet`, curl or a shell.
- **Why not `AddExecutable` for local AOT**: Aspire has no API to publish and then launch a native binary; an
  executable resource loses the .NET project defaults (OTLP, endpoints, Rebuild, debugging) that DA would have to
  rebuild by hand. The Compose run of the real images checks AOT with official tools only.
- **Why the dashboard command locally**: `aspire do` runs its steps in a fresh process that cannot reach the running
  AppHost's orchestrator; the resource command runs inside it. Published targets use `aspire do {cluster}-restart`.
- **Who starts a hot restart, per environment** (one loop, section 5; the trigger is the platform's adapter):

  | Environment | Started by | The loop runs in | Restart trigger |
  |---|---|---|---|
  | Local run | the `{cluster}-restart` resource command in the Aspire dashboard | the AppHost process | the restart of the pair's other replica |
  | Compose | `aspire do {cluster}-restart` | the deploying machine drives it; the admin calls (arm, dry run, wait for ready) run inside the internal network as `docker compose run --rm --no-deps rollout …` (section 8) | `docker compose up -d --no-deps --force-recreate <other instance>` |
  | Kubernetes | `aspire do {cluster}-restart` | the `{cluster}-rollout-{n}` Job | the parked container's image, applied as `daserver-rollout` (its memory request resized first; for a same-image roll the node tells the parked instance to exit) |
  | systemd, launchd, Windows service | the documented script | the operator's machine | the other instance: `systemctl restart`, `launchctl kickstart -k`, `Restart-Service` |
  | Any, the Dashboard's "Hot restart" of a type | the Dashboard calls the deploy pipeline's webhook (`DaServer:Dashboard:DeployWebhook`, carrying the action id and the build expected), which runs `aspire do {cluster}-restart`; the pipeline's concurrency group per type queues and never cancels (`cancel-in-progress: false`), and the webhook can name a completion callback carrying the action id; without a webhook it shows that command | wherever the row above for that environment says | as above |
  | Any, one node (the Dashboard's node "Restart") | the Dashboard calls the deploy pipeline's webhook for that node, carrying the action id and the node's incarnation as seen (the loop refuses an incarnation the node no longer is; one action per type at a time) | wherever the row above for that environment says | a same-image handover of that one node |

- **The pair of instances** ([architecture.md section 5](architecture.md#5-migration-upgrades-hot-restart), ADR-098):
  every stateful node is two installed instances that share its node name and handover directory, and no
  configuration says which one is live: the process holding the exclusive lock on `<HandoverPath>/node.lock` is the
  node; an instance without it is the standby while the node is armed with its manifest hash, and parks otherwise (a
  few tens of MB, holding nothing). The trigger restarts the instance that is not the node, so it comes up on the new
  build as the standby; the node hands over and parks in place (it never exits, so no handover waits on a supervisor); `/health` reports the instance id, `DaServer:Instance` and the
  running manifest hash, and the rollout waits for a new instance id with the armed hash. Each instance learns its own
  name from `DaServer:Instance`: the systemd template's `%i`; launchd jobs and Windows services carry it in their own
  definitions; on Compose and Kubernetes DA sets it per service or container (containers that share a network
  namespace cannot learn their names). On a nack, a broken stream or no answer in time the node resumes in place and
  nothing is lost, and with no standby passing the handshake by the arm's timeout it never freezes; either way the
  roll stops for that node and reports it. A same-image roll (configuration only) is the same handover with the
  running build. SIGTERM is always a graceful stop.

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
| Kind | `StatefulSet` named `{group}` (not `{group}-statefulset`), so pods are `game-0`, `game-1`: the same names as the run-mode resources and the node names | Hot restart keeps the pod, its IP and its `emptyDir` (ADR-085); stable names match the dashboard and logs | DA: replace `resource.Workload` in the hook | W11 |
| `serviceName` | `{group}-peers` (the existing headless Service, `publishNotReadyAddresses: true`); per-pod DNS `game-0.game-peers` | StatefulSets require a governing headless Service ([StatefulSet](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/)) | DA | W11 |
| `podManagementPolicy` | `Parallel` | Nodes join by gossip and need no order; `OrderedReady` serializes scale-ups (a flash-sale burst waits pod by pod) and can wedge on one broken pod ("forced rollback"). Rolling updates stay one pod at a time | DA | W11 |
| `updateStrategy` | `OnDelete` for groups without client connections; `RollingUpdate` (its default, one pod at a time; `maxUnavailable` is beta and on by default since 1.35 but not used, section 10) for stateful groups that also accept clients (flash sale `sale`) | `OnDelete`: a template change replaces no pod; DaServer's rollout decides per pod (hot restart or delete-and-recreate). A group hosting `Connection` cannot hot restart ([draft §10](programming-model.md#热重启)). Such a group has no surge either: the clients of a terminating pod are retired to the other pods, old ones included, so during its roll a client may move up to once per remaining old pod. That is the documented limit of hosting clients on a stateful group; groups that want one move per client keep gateways separate | DA | W11 |
| Template image vs. pods | `helm upgrade` (or `aspire deploy`) sets the template image; the rollout patches only pods, never the template, so the live object never drifts from the Helm release (ADR-085) | The next `helm upgrade` would otherwise write the old tag back | Rollout Job | W11 |
| Replicas | `spec.replicas` from `AddNodes(replicas)`, **not rendered** when the application adds an autoscaler for the group (the HPA then owns it through the `scale` subresource, which works on StatefulSets; section 11) | Scale-in removes the highest ordinals, all at once under `Parallel`, each leaving gracefully; the autoscaler's scale-in policy limits it to one pod per period (section 10) | DA, sample hook (clears it beside the autoscaler) | W11 |
| Grace period | `terminationGracePeriodSeconds` = stop budget + margin + preStop, as today; the stop budget sized to drain the expected write backlog (`daserver.store.oldest_unsaved_age` under peak load), since changes still unwritten when it runs out are lost and logged | Unchanged ([pod termination](https://kubernetes.io/docs/concepts/workloads/pods/pod-lifecycle/#pod-termination)); a graceful stop writes everything it can within the budget ([draft §10](programming-model.md#默认异步成批地写)) | DA (exists) | - |
| Containers | Two node containers, `node-a` and `node-b`: the same image and configuration, `DaServer__Instance` set per container, each serving its probes on its own port (`DaServer:ProbePort`), the same memory limit on both; the parked one's memory request small, the node's the working set (the rollout resizes them, section 3) | A hot restart is a handover between the two (ADR-098): the pod keeps its IP and network namespace, the listening sockets pass from one container to the other, and only the parked container is restarted by an image apply. A parked or standby instance reports ready (it serves nothing), so the pod's readiness depends only on the node, except for the seconds the parked container restarts (an image apply, a same-image roll's exit): a pod is Ready only when every container is, so it is briefly not Ready then. Hot-restart groups host no `Connection`, so no client Service depends on it; the PDB counts it as one unavailable pod, which holds off another eviction in the group meanwhile | DA | W11 |
| preStop | No `sleep` on hot-restart groups. The kubelet runs preStop whatever the reason a container is killed, the image apply on the parked container included, and a `sleep` would only delay the standby | [Lifecycle hooks](https://kubernetes.io/docs/concepts/containers/container-lifecycle-hooks/) | DA | W11 |
| Handover volume | `emptyDir: {medium: Memory, sizeLimit: 1Mi}` at `/var/run/daserver/handover` (`DaServer__HandoverPath`): the lock, the pipe's Unix domain socket and the arm file, shared by both containers | Both containers must see one inode for `flock` and one socket path; nothing large is written there, the worlds stream over the socket ([emptyDir](https://kubernetes.io/docs/concepts/storage/volumes/#emptydir)) | DA | W11 |
| Persistent volume | **None** (no `volumeClaimTemplates`) | State lives in the actor store and moves between the pod's containers by the handover. A PV would be zone-bound and slow | - | W11 |
| PDB | `maxUnavailable: 1`, `unhealthyPodEvictionPolicy: AlwaysAllow`; stays an application choice (CLAUDE.md), the samples keep adding it on the StatefulSet's pods | With the default policy, an outage that makes every pod unready blocks every node drain ([PDB](https://kubernetes.io/docs/tasks/run-application/configure-pdb/#unhealthy-pod-eviction-policy)) | Sample hook | W11 |
| Topology spread | Soft spread over `kubernetes.io/hostname` (`whenUnsatisfiable: ScheduleAnyway`); zone spread left to the app | One machine's loss should not take several nodes' actors at once ([topology spread](https://kubernetes.io/docs/concepts/scheduling-eviction/topology-spread-constraints/)) | DA (hostname), sample (zones) | W12 |
| CPU count | `env DOTNET_PROCESSOR_COUNT` from `resourceFieldRef: requests.cpu` (rounded up) when no CPU limit is set | The world count defaults to `ProcessorCount`, which ignores requests: today a pod on a 64-core host runs 64 worlds ([downward API](https://kubernetes.io/docs/tasks/inject-data-application/environment-variable-expose-pod-information/)) | DA | W12 |
| Image pre-pull | Before patching a pod, the rollout makes the node pull the new image: a pod with `nodeName` set to that node (it skips the scheduler, so it carries what admission needs), the new image's node binary in a no-op mode (`--DaServer:Exit`; a chiseled image has no `true`), `restartPolicy: Never`, `activeDeadlineSeconds`, the restricted `securityContext` of section 7, minimal explicit requests and limits (1m CPU, 16Mi memory: a `ResourceQuota` rejects a pod without them, and `nodeName` skips the scheduler but not the kubelet's resource admission, so a pod with real requests would be refused `OutOfcpu`/`OutOfmemory` on a full node; when it is refused anyway the rollout reports a warning and the pull happens inside the pause), the workload's `imagePullSecrets`, and its `tolerations` and `nodeSelector` (setting `nodeName` skips the scheduler but not the kubelet's taint checks, and the mmo's stateful groups run on a tainted node pool) | The kubelet kills the old container and only then pulls the image; a pull inside the pause defeats "hundreds of milliseconds" | Rollout Job | W11 |
| Image reference | The rollout applies the digest-pinned reference (`image@sha256:…`, resolved once when the roll starts) and compares `status.containerStatuses[].imageID` before and after each pod | With a mutable tag (`latest`) and `IfNotPresent`, an unchanged reference restarts nothing or runs a stale cached image | Rollout Job | W11 |
| Template changes beyond the image | Before patching a pod, the rollout compares two revisions, never the live pod: the template data of the `ControllerRevision` named by the pod's own `controller-revision-hash` label against the StatefulSet's `updateRevision`, images ignored. A live pod always differs from its template (the VPA sets and resizes `resources`, the controller sets `hostname` and `subdomain`, admission adds tolerations and webhook fields), so comparing it would recreate every pod and no hot restart would ever happen. An in-place patch can change only the image; probes, env literals, `DOTNET_PROCESSOR_COUNT`, volumes and `sizeLimit`, `securityContext` and the grace period in the template cannot change on a running pod. When the two templates differ beyond the image, the rollout takes the delete-and-recreate path for that pod (the rolling path through records) and says so; when only the image differs, it applies the parked container's image and sets the pod's `controller-revision-hash` label to the update revision in one forced server-side apply (two applies under the same field manager would let the second one drop the image the first one owns), so the next comparison starts from the right revision | Otherwise a release that changes, say, probe timeouts and the image runs the new build with the old spec until the pod happens to be deleted | Rollout Job, E2E `TemplateChangeBeyondImageRecreatesPod` (with a VPA-resized pod that must still patch in place) | W11 |
| Immutable fields | `serviceName`, `podManagementPolicy`, `selector` and `volumeClaimTemplates` are frozen from the first release: DA never changes them, and a publish test compares them with the previous chart. Kubernetes accepts updates to `replicas`, `template`, `updateStrategy`, `minReadySeconds`, `ordinals` and `persistentVolumeClaimRetentionPolicy` only, so any other change makes `aspire deploy` fail with `Forbidden` | [StatefulSet spec](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/) | DA, publish test `StatefulSetImmutableFieldsUnchanged` | W11 |

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
| HPA | Allowed (flash sale uses one); `targetRef` names the right kind; no rendered `replicas` (section 11); scale-in rate and PDB per section 10 | `Publishing.DeploymentOf` assumes Deployment | Sample | W11 |
| Probes, grace, preStop, spread, CPU count | As for stateful groups; gateways keep a preStop delay for the endpoint to leave the load balancer | | DA | W11 / W12 |

Bare metal: start the new gateway processes (another port or host behind the load balancer) before draining the old
ones; the operator's load balancer configuration decides. Compose: section 8.

### Probes (both kinds)

| Probe | Target | Why |
|---|---|---|
| startupProbe | `/alive`, period 2 s, failureThreshold 45 (90 s) | Kestrel starts first (the node's long start runs after `ApplicationStarted`, not inside a hosted service's `StartAsync`), and during the start `/alive` answers 200 while the start makes progress: waiting for the store counts as progress (no deadline: a store outage must not restart nodes in a loop; the node stays not ready and retries with one backoff), and so does each step of a handover's load and the `Rejoining` read of its own membership row, which have deadlines of their own (the restart permit's from the coordinator). `/alive` answers 503 only when the start stalls: no step finished within 90 s while the store answers. So the startup probe catches a hung start, not a slow store, and liveness then needs no `initialDelaySeconds` ([probes](https://kubernetes.io/docs/concepts/configuration/liveness-readiness-startup-probes/)) |
| readinessProbe | Per container on its own probe port: the node's container answers `/health` (joined, not leaving, not isolated alone: frozen past its lease while a peer still reaches the store, [architecture.md section 4](architecture.md#4-placement-activation-persistence)), timeout 2 s; once the node has started, the store is not part of it: the Npgsql data source is registered with `DisableHealthChecks`, or `/health` maps only the checks tagged `daserver` (today `AddKeyedNpgsqlDataSource` adds its check to `/health`) | A store outage must not take every node out of service: active actors keep running and activations back off ([draft §6](programming-model.md#6-生命周期)); 1 s timeouts flap under GC or load. The other container (parked or standby) answers ready while it runs: it serves nothing, and the pod must not go unready because of it |
| livenessProbe | `/alive` (no stalled world), timeout 2 s | Unchanged meaning |

How: DA (`WithHttpProbe(ProbeType.Startup, ...)`), W12.

### Kubernetes Services (both kinds)

How ports are declared per group is [draft §2](programming-model.md#2-服务器与节点); these are the Services that follow.

| Service | Target | Why | How | Step |
|---|---|---|---|---|
| `{group}-peers` (headless) | The cluster port (7000) only, as today | Node links go pod to pod (the address each node writes into the membership table) | DA (exists) | - |
| `{group}-service` (ClusterIP) | The HTTP port (health, admin) only, reachable inside the cluster; today it also carries the cluster and client ports | Smaller surface; a load-balanced cluster port is never right | DA | W11 |
| Client Service | Only for a group with `WithClientConnections(port)`: the declared port (TCP and WebSocket), one address balanced over the replicas. Its `type` and annotations (`LoadBalancer`, `externalTrafficPolicy`) come from the application's hook, as the mmo sample does today | CLAUDE.md: the load balancer's kind is the platform's and the application's | DA (Service), sample hook (kind) | W11 |
| Public HTTP | Only for a group with Aspire's `WithExternalHttpEndpoints()` (webhooks) | Admin and probes stay internal | Aspire (exists) | - |
| Per-replica public addresses | None: direct connect to one pod (Agones-style) is a non-goal; a later direct-connect feature would add it | | - | - |

## 3. Jobs

| Job | What it runs | When | Why not today's way | How | Step |
|---|---|---|---|---|---|
| `{cluster}-schema` | The node image in schema mode (`--DaServer:Migrate`, added by W6): `PostgreSqlActorStore.Schema` under the advisory lock, with a DDL role | Always a plain Job named with the release revision (`ttlSecondsAfterFinished`); no Helm hook variant (a pre-install hook would run before an in-chart database exists, and one shape is enough). Nodes never create the table: until it answers they are not ready and retry with one backoff, with no deadline of its own (the startup probe counts the wait as progress, section 2). Aspire run mode runs the same mode as a one-shot resource; in-process tests (`TestCluster`) call `Schema` once before their nodes start | Creation at node start gives the runtime role DDL rights, and every node start, scale-up and hot restart runs `CREATE INDEX IF NOT EXISTS`, which takes a `SHARE` lock on the live table before it checks whether the index exists ([CREATE INDEX](https://www.postgresql.org/docs/current/sql-createindex.html)): every save of the cluster waits for it, and it queues behind any long transaction. Hand-written SQL and the advisory lock stay: a migrations library is not worth one table and one index | DA (`WithActorStore(..., schema: SchemaMode.Job)`), DaServer.Persistence.PostgreSql (missing table: not ready, retried); the mode itself is W6 | W12 |
| `{cluster}-upgrade-check` | The new image in check mode: sends its own `wire.manifest` to a running node's dry-run arm (`/admin/restart?dryRun=true`, signed with the admin key), where the one manifest diff runs ([architecture.md section 5](architecture.md#5-migration-upgrades-hot-restart)) and answers the hot-restart path (in place, through records, rolling); the check prints it and fails on an incompatible change, and on a release that would write record fields or record types the running build cannot keep (no safe rollback) unless forced | A plain Job started by the deploy step (`aspire deploy`) before the template changes; Compose: a step of `aspire deploy` | Today only the route check runs, on the publishing machine, against the build alone; nothing compares with what is running | DA (step + Job), DaServer (check mode, dry-run arm) | N9 |
| `{cluster}-rollout-{n}` | The new image in rollout mode, ServiceAccount `{cluster}-rollout`: per pod of each stateful group, one at a time and the coordinator last: pre-pull, resize the parked container's memory request up to the node's working set in place (the pod's `resize` subresource, beta and on by default from 1.33, GA in 1.35; not for Windows pods or machines with the static CPU or memory manager, which take the delete-and-recreate path; a resize that reports `Infeasible`, or is still `Deferred` at its deadline, is set back before the arm is refused, so no large request applies later, and that pod takes the delete-and-recreate path through records), arm the node (`/admin/restart`; refused while a member joins or leaves, another node holds the restart permit, or the standby would not fit), apply the parked container's image (the kubelet restarts only that container, which comes up as the standby and takes over), wait for the node's new instance id with the armed hash, resize the old container's request back down (the old process parked in place: its container never restarts); or delete the pod when the path is rolling. A **same-image roll** (a config change) has no image to apply: the Job arms the node with its running manifest and asks it to tell the parked instance over the pipe to exit; the kubelet restarts that container in the same pod (`restartPolicy: Always`), which re-reads env and `envFrom` and takes over the same way. The kubelet backs off restarts of a container that exited within 10 minutes of its last start (10 s, then doubling), whatever the exit code; that only delays a same-image roll's standby (a handed-over instance parks without exiting), never the pause, so a second roll of a pod within 10 minutes waits for the back-off before its handover (E2E `SameImageRollTwiceWithin10Min`). Secret rotation needs no roll at all (section 4) | Started by `aspire do {cluster}-restart` or `kubectl create job` | See section 5 for why in-cluster | DA (command creates the Job), DaServer (mode) | W11 |
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
| Signing secret | **One** cluster Secret `{cluster}-signing` whose keys are the configuration names themselves, `DaServer__SigningSecret` and the optional `DaServer__AcceptedSigningSecret`; every node mounts it whole (no `items`: a listed key missing from the Secret would block the volume, and `optional` exists only for the whole volume) as files at `/run/secrets/daserver` and reads them through .NET's key-per-file configuration provider (`AddKeyPerFile("/run/secrets/daserver", optional: true)`, `__` becomes `:`). Files, not environment variables: the value stays out of `kubectl describe`, `docker inspect` and process dumps, and Compose mounts its `secrets:` at the same path, so one mechanism serves both | Today each group has its own copy in the values (four `--set`s in the mmo chart that must agree; one typo splits the cluster: its Hellos are refused). One object is what the rotation steps change | DA: in publish mode, leave the variable out of the generated per-group Secret, add the Secret and the volume mount in the hook; DaServer.Hosting: the key-per-file source | W12 |
| Missing secret | Published nodes refuse to start without a signing secret (`DaServer__RequireSigningSecret=true`, set by DA in publish mode only; run mode and tests stay open) | Today an empty value silently yields an unauthenticated cluster; Helm templates from Aspire cannot use `required` | Runtime option + DA | W12 |
| Existing Secret | `WithSigningSecret(existingSecretName)`: reference a Secret made by External Secrets, Sealed Secrets or the CSI driver instead of a Helm value | Secret values in Helm values end up in release history and CI logs ([secrets good practices](https://kubernetes.io/docs/concepts/security/secrets-good-practices/)) | DA | W12 |
| Rotation | Change `accepted`; swap; drop, without restarting anything: nodes load the key files with `reloadOnChange` through a `PhysicalFileProvider` built with `UsePollingFileWatcher = true`, `UseActivePolling = true` and `ExclusionFilters.None`, polling every 4 s (the provider's fixed internal interval, not configurable; file events miss the swap of a Secret volume's `..data` symlink, and the default filter skips dot-prefixed names), and replace their signing keys between two batches. A reload that reads an empty or too short current key (`DaServer__SigningSecret` under 32 bytes: a Compose file rewritten by truncate-then-write can be read between the two) is refused: the node keeps its current keys, logs it and reads again at the next poll; `RequireSigningSecret` applies to reloads as it does at start. The accepted key is optional: an empty `DaServer__AcceptedSigningSecret` file means "no accepted key", which is how Compose drops it (a bind-mounted file cannot disappear from a running container). Every node reports the fingerprint of the keys it holds in `/admin/membership`: `HMAC-SHA256(key, "daserver-key-id")`, first 8 bytes in hex, one for `current` and one for `accepted` (empty when absent), never the key; the rotation step (a command of DA, `{cluster}-rotate-secret`, and the operations page) waits until every node reports the new fingerprint before the next step, since the kubelet updates mounted Secret files eventually (its sync period plus cache, often 60 to 90 s) | A roll per step would restart gateways and stateless groups too (their template does not change, so Helm would not roll them) and run into the kubelet's restart back-off; reading the files live needs neither | DaServer.Hosting (reload, fingerprint), DA (command), docs | W12 |
| Admin key | `{cluster}-admin`, a Secret of its own (key-per-file `current` and `accepted`, generated by DA like the signing secret), mounted by the Dashboard, the rollout Job and the upgrade-check Job, and read-only by nodes, which verify admin requests with it; nobody else mounts it. A second Secret, `{cluster}-alertmanager`, holds the Dashboard's token for Alertmanager's silences API, mounted key-per-file the same way and present only when `DaServer:Dashboard:Alertmanager` is set. Requests carry an HMAC with a scope, `view` or `act`; nodes refuse an `act` request signed for `view`, re-read the files when they change and accept `current`, then `accepted`, during a rotation, so a long-lived holder (the Dashboard) survives a rotation without a restart (E2E `DashboardSurvivesRotation`). The signing secret is mounted by nodes only. No admin key in Helm values or release history | A compromised Dashboard cannot pose as a node or sign a resume ticket; a view-scoped request cannot act | DA (Secret, mounts), DaServer (scope check) | W11 |
| Database | Two roles: owner (schema Job; it needs `CREATE` on the schema, which PostgreSQL 15 and later no longer grant on `public` to everyone) and runtime (`SELECT, INSERT, UPDATE` on `actor_records`, `daserver_members` and `daserver_membership`, `USAGE` on the epoch sequence, `EXECUTE` on `daserver_sweep_empty` and `daserver_members_cleanup`, no `DELETE`; [architecture.md section 4](architecture.md#store-statements)). The schema Job creates the sequence, the tables, the functions and the grants, every object qualified with the configured schema (no reliance on either role's `search_path`), granting to the role named by the runtime connection string's `Username` (the names are the user's, not fixed). It does not create roles: the samples' AppHosts add an init step (`{cluster}-roles`, the database's superuser; Aspire's PostgreSQL resource has only `postgres`) that converges the roles and the schema on every run (architecture.md section 4): creates the owner role (it owns the schema's objects), the runtime role and, with `AddDaServerDashboard`, the Dashboard's role (`<cluster>_dashboard`: insert-only on `daserver_audit`, `SELECT, INSERT, UPDATE, DELETE` on `daserver_actions`, read and write on `daserver_dataprotection`, nothing on the actor tables), none a superuser, if missing, and sets both passwords every run (`ALTER ROLE … PASSWORD`, built with `format('%L')`, so a changed password parameter reaches the database); runs `CREATE SCHEMA IF NOT EXISTS … AUTHORIZATION <owner>` (or grants the owner `CREATE` on `public`); and hands every existing DaServer object to the owner (`ALTER … OWNER TO`, for tables that earlier nodes created as `postgres`); the schema Job runs after it (Compose `depends_on: service_completed_successfully`; on Kubernetes the schema Job retries with backoff until the role exists, since Jobs have no ordering); with an external database the operator provisions both roles. Every node needs the runtime connection, gateways and stateless groups included: the membership table is in the store (a cluster of more than one node has no membership without it). Connection strings stay in Secrets (Aspire already puts `ConnectionStrings__actors` there); `AddConnectionString` parameters are secret | Least privilege; no DDL from nodes | Samples (two connection strings), DA (`WithActorStore(runtime, owner)`) | W12 |
| OTLP | In publish mode DA points `OTEL_EXPORTER_OTLP_ENDPOINT` at the OpenTelemetry Collector (a plain parameter), `OTEL_EXPORTER_OTLP_HEADERS` (API keys) a `secret: true` parameter; the samples do not publish the Aspire dashboard | Aspire wires OTLP to its in-cluster dashboard: telemetry would have two destinations; the Collector fans out to Prometheus, Loki and Tempo ([architecture.md section 8](architecture.md#telemetry)) | DA, samples | W12 |
| Non-secret config | Stays in Aspire's per-group ConfigMap (`envFrom`): cluster name, ports, flush interval | Correct today. A ConfigMap change restarts nothing; Deployments get a `checksum/config` pod annotation so `helm upgrade` rolls them; `OnDelete` StatefulSets replace no pod on a template change, so their config change is applied by the rollout Job's same-image roll (section 3) | DA (annotation) | W12 |

Compose: Aspire writes secret parameters as `${VAR}` in `docker-compose.yaml` and empty keys in `.env`. Target: the
same files as on Kubernetes. A top-level `secrets:` entry per key (`daserver-signing-current` from
`file: ./secrets/signing-current`, or `environment: SAMPLE_SECRET` for CI) is granted to every node and the schema
service with `target: /run/secrets/daserver/DaServer__SigningSecret` (and the accepted key the
same way, at `DaServer__AcceptedSigningSecret`: DA always declares it, from an initially empty file, because adding a
secret to a service later changes the service and forces a recreate); the node reads them through the same key-per-file source, and `DaServer__RequireSigningSecret` makes a
missing file a refused start. The admin key (`{cluster}-admin`) is a secret file the same way (`daserver-admin-current`
and `daserver-admin-accepted`, at `/run/secrets/daserver-admin/`), granted to the nodes, the Dashboard and the
`rollout` tools service; the Dashboard and `rollout` never receive the signing secret. `./secrets/` is never committed. A `file:` secret is a bind mount, and Compose ignores
`uid`, `gid` and `mode` for it ([Compose secrets](https://docs.docker.com/reference/compose-file/services/)): the host
file must be readable by uid 1654 (mode 0444, or 0440 with group 1654), which the operations page and DA's publish
output state; an `environment:` source honours `mode`. Rotation is the same three steps without a restart: write the
new content into the existing file (`cat new > ./secrets/signing-current` keeps its inode; a tool that replaces the
file makes a new inode the bind mount never sees, and then only `up --force-recreate` picks it up).

## 5. RBAC and who runs the hot restart

| Identity | Permissions | Why |
|---|---|---|
| Nodes: ServiceAccount `{cluster}-node`, `automountServiceAccountToken: false` | None | Discovery is DNS (ADR-030); a node with an API token is a foothold for nothing it needs ([opt out of automounting](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/#opt-out-of-api-credential-automounting)) |
| Schema and upgrade-check Jobs | None (automount off) | They talk to the database and the nodes' admin plane only |
| Rollout Job: ServiceAccount `{cluster}-rollout` + Role + RoleBinding in the release namespace | `pods`: get, list, watch, patch (the parked container's image, and the `controller-revision-hash` label after a patch), delete; `pods/resize`: patch (the parked container's memory request); `pods` create (the pre-pull pods); `statefulsets`: get, list, watch (read the template image and replicas); `controllerrevisions`: get, list (compare a pod's spec with the update revision); `horizontalpodautoscalers` (or `scaledobjects.keda.sh`): get, list, patch (finding the one that targets the group and pausing scale-in during the roll, section 10). No `statefulsets` patch (Helm owns the template). Writes go through Server-Side Apply as field manager `daserver-rollout` (section 11) | The writes the rollout makes; namespaced, nothing cluster-wide ([RBAC](https://kubernetes.io/docs/reference/access-authn-authz/rbac/)) |
| The operator or CI | `create` on `jobs`, `get` on `pods/log` in the namespace | Starting and watching the rollout |

**One rollout loop, one adapter per platform.** The loop is the node image's rollout mode: for each stateful group,
one node at a time and the coordinator last: pre-pull the new image, arm (`/admin/restart`), trigger the restart of
the pair's instance that is not the node, wait for a new instance id with the armed manifest hash in `/health`, next;
a rolling path or a group without hot restart (gateways, stateless groups) skips the arm. Only "trigger the restart"
differs per platform: Aspire run mode (the restart command of the pair's other replica, inside the AppHost), Compose
(`docker compose up -d --no-deps --force-recreate <other instance>`), Kubernetes (resize and apply the parked
container's image, or delete the pod on the rolling path) and bare metal (the service manager's restart of the other
instance; section 0). There is no second implementation.

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
| `{cluster}-http-plane` | Ingress on the HTTP port from the release's Jobs, from the Dashboard's pods (`daserver.io/dashboard={cluster}`, when `AddDaServerDashboard` is used) from the pods of every AppHost resource that references the group (`WithReference`: an internal REST caller or back-office service using `MapAsk`/`MapSend`; Aspire knows those references, and `/admin` still needs the admin key) and from a namespace selector given as a parameter (`metrics-namespace`, Prometheus or the Collector when it scrapes); kubelet probes come from the node and most CNIs allow them. From anywhere only for groups with `WithExternalHttpEndpoints()` | `/admin` (restart, unload) sits on this port, today open to the whole cluster network through `{group}-service` |
| `{cluster}-clients` (groups with `WithClientConnections(port)`) | Ingress on the declared client port from anywhere | The load balancer's traffic |
| Store | Ingress to the in-chart PostgreSQL only from node pods (every group: membership is in the store), the schema Job, the role-init Job (`{cluster}-roles`, sample) and the Dashboard's pods; node pods get no egress policy (an egress rule isolates them, cutting DNS, the cluster port to other nodes and OTLP to the Collector). A platform that must restrict egress also allows kube-dns on 53 UDP/TCP, the cluster port to pods labelled `daserver.io/cluster`, the Collector and the database | Samples (the database is the application's) |
| `{cluster}-dashboard` (with `AddDaServerDashboard`) | Ingress to the Dashboard only from the Ingress or Gateway controller; egress to Alertmanager (silences, alert groups), the Prometheus API, the Loki API, every node's HTTP port, the store and the deploy pipeline's webhook host | The Dashboard holds the admin key and acts on the cluster; nothing else should reach it |

Default deny for the namespace is the platform's call. Source: [network policies](https://kubernetes.io/docs/concepts/services-networking/network-policies/).

## 7. Pod security and images

| Setting | Target | Why |
|---|---|---|
| Pod `securityContext` | `runAsNonRoot: true`, `runAsUser/runAsGroup: 1654` (the .NET images' `app` user, `APP_UID`), `seccompProfile: RuntimeDefault` | Pod Security Standard "restricted" ([PSS](https://kubernetes.io/docs/concepts/security/pod-security-standards/)) |
| Container `securityContext` | `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `readOnlyRootFilesystem: true` | Ports are above 1024 (8080, 7000, 7100): no capability needed ([security context](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/)) |
| Writable paths | `/tmp` (`emptyDir`, `sizeLimit: 64Mi`: .NET's diagnostics IPC socket and temp files) and the handover directory (stateful groups). Nothing else: logs go to stdout, the HTTP plane keeps no Data Protection keys (no cookies) | Verify in W12 by running the E2E with a read-only root |
| Resources | Requests are the application's (the samples' VPAs); DA sets none, but derives `DOTNET_PROCESSOR_COUNT` from them (section 2) | CLAUDE.md: sizing is the platform's |
| Image (Native AOT, ADR-083) | Base `mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled` (the SDK's AOT default is a chiseled runtime-deps variant; `-extra` only with `InvariantGlobalization=false`, which the samples do not use); JIT nodes `aspnet:11.0-resolute-chiseled`. Set `ContainerFamily` in `ServerNode.props` / the flash-sale project | No shell, no package manager, non-root by default ([container publish](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration), [container images](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images)); debugging is `kubectl debug` with an ephemeral container ([debug a pod](https://kubernetes.io/docs/tasks/debug/debug-application/debug-running-pod/#ephemeral-container)) |
| Compose health check | One form for JIT and AOT images: `["CMD", "/app/{assembly}", "--DaServer:Probe=/health"]`, the node's own executable in probe mode (an AOT image is that executable; a JIT image gets it as the apphost, which publish emits by default: DA makes sure `UseAppHost` stays on). Kubernetes uses `httpGet` probes on the same endpoints, so one probe implementation serves both | An AOT image has no `dotnet` (and distroless images have no curl), so today's `dotnet /app/{assembly}.dll` check fails every AOT node; Compose then never reports it healthy and `service_healthy` dependants never start |
| One image per node project | Schema, upgrade-check and rollout Jobs run the node's own image with a mode flag (like the `Probe` mode) | No extra image to build, version or scan |

Steps: securityContext, `/tmp` and service account W12 (the handover volume W11); images and the Compose health check W8.

## 8. Docker Compose equivalents (where they differ)

| Kubernetes | Compose |
|---|---|
| StatefulSet | Stateful groups: a pair of services per node, `map-0-a` and `map-0-b`, with `network_mode: service:map-0-net` and one named volume for the handover directory, the same node name and `DaServer__Instance` set per service; `map-0-net` is a holder (a pinned image that does not change per release, so no deploy recreates it under a live node; it only waits, the role of Kubernetes' pause container) that owns the network namespace, the fixed `hostname` (the node name) and the network aliases, so the node's address survives either process exiting (a namespace joined through `service:map-0-a` itself would end with that container). An empty named volume mounted at a path the image lacks is created `root:root 0755`, which uid 1654 cannot write: a one-shot service per stateful node, `map-0-handover-init`, runs the node image as `user: "0"` in `--DaServer:PrepareHandoverDir` mode (libc `chown`/`chmod` of the mounted directory to 1654; the image has no shell), with `cap_drop: [ALL]`, `cap_add: [CHOWN, FOWNER]` (root without `CAP_CHOWN` cannot chown) and `no-new-privileges`, and both instances `depends_on` its completion (E2E `ComposeHandoverVolumeWritable`). Both services run at once; the lock on `node.lock` in the shared volume makes one the node and the other parks. Hot restart: `{cluster}-restart` runs the rollout loop (section 5) with `docker compose up -d --no-deps --force-recreate map-0-b` as its trigger when `/health` names `map-0-a` as the node (without `--force-recreate`, `up` recreates a container only when its image or service configuration changed, so a same-image roll would restart nothing). The pair and the holder carry `restart: unless-stopped`, so all three come back after a reboot or a daemon restart (`on-failure` does not), and a container that handed over is started again and parks. E2E `ComposeStandbyHandover`, `ComposePairBackAfterDaemonRestart` |
| Deployment (gateways) | One service per gateway (`gate-0`, `gate-1`, …), as for stateful nodes, because Compose has no per-replica `up`: `up --force-recreate <svc>` recreates every replica of a service at once. A plain rolling restart in the same loop, one service at a time: the stopping gateway retires its connections (section 2) and its clients resume on another gateway. A client may move twice during a gateway roll, which is acceptable at Compose's single-host scale; no surge (E2E `ComposeGatewayRollKeepsClients`) |
| Deployment (stateless groups) | One service per replica as well (`api-0`, `api-1`, …), restarted one at a time by the same loop; `deploy.replicas` is not used, for the same reason |
| Jobs | One-shot services (`restart: "no"`): `roles` (sample, the database's superuser) with `schema` `depends_on: {roles: {condition: service_completed_successfully}}`, and nodes `depends_on: {schema: {condition: service_completed_successfully}}`; the deploy step runs them in that order (section 0); load generators under `profiles: [loadtest]` |
| Upgrade check, rollout | Steps of `aspire deploy` / `{cluster}-restart`. The nodes' HTTP plane is only on the internal network, which publishes no host port, so every admin call (the dry-run arm, arming, waiting for ready) runs in a container on that network: `docker compose run --rm --no-deps rollout <step> <node>`, the new image in rollout mode, the admin key read from the mounted secret file. The `rollout` service carries `profiles: [tools]`, so `docker compose up -d` never starts it. The deploying machine drives the loop and runs only the trigger (`up --force-recreate`) itself. The deploy step itself runs `docker compose up -d --no-recreate`, so a release creates new services and recreates no existing node; the loop then applies the new image and configuration one node at a time |
| Secrets | Compose `secrets:` mounted at `/run/secrets/daserver` (section 4), the same files as on Kubernetes; `./secrets/` and `.env` never committed |
| securityContext | `read_only: true`, `tmpfs: [/tmp]`, `user: "1654"`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]` |
| NetworkPolicy | Three networks: an `internal: true` network for the cluster and HTTP planes; an outbound network (not internal, no published ports) on every node and the schema service so they reach an external database and an external OTLP endpoint; and the default one only for client-facing services and the dashboard |
| RBAC | None (no API) |
| Health check | The node executable in probe mode (section 7); `postgres` gets `pg_isready` so `game` can wait for `service_healthy` instead of `service_started` |

## 9. Steps

The work is the roadmap's (W6, W7, W8, W11, W12, N9); the Step column of each table above says which step
builds that row. Docs to follow when merged are listed under W10 in the roadmap.

## 10. Kubernetes resources and controllers

Which Kubernetes object each part of a DaServer deployment uses, which controllers may act on it, and who writes it.
Checked against Kubernetes 1.37 (current when written), VPA 1.7, KEDA 2.21, Gateway API 1.6, Helm 4 and Aspire 13.6.1.
The rule from CLAUDE.md decides the "How" column: **DA** publishes what follows from DaServer itself (kinds, ports,
probes, grace, the membership store, the rollout's own objects); the **sample's hook** (the application's AppHost through
`PublishAsKubernetesService`) adds scaling and disruption policy (HPA or `ScaledObject`, VPA, PDB, the load balancer's
kind); the **platform** owns cluster-scoped and shared machinery (the controllers themselves, PriorityClasses, the
Collector, node autoscaling, admission policy). DaServer exports the signals and makes joining and leaving safe; it
does not decide when to scale.

### Workload kind per component

| Component | Kind | Why not the others | How | Step |
|---|---|---|---|---|
| Stateful node groups | `StatefulSet`, `OnDelete` (`RollingUpdate` when the group accepts clients itself), `Parallel` (section 2) | Stable pod names and a pod whose two containers hand over in place (ADR-085). A Deployment renames pods and empties `emptyDir` on every template change; a DaemonSet ties the count to machines (below) | DA | W11 |
| Gateways | `Deployment`, surge then drain (section 2) | No identity, no state to hand over; surge needs a second ReplicaSet | DA | W11 |
| Stateless groups | `Deployment`, `maxUnavailable: 0` | No identity, no state to hand over | DA | W11 |
| `DaServer.Dashboard` | `Deployment`, 1 replica (2 for availability only with a store; without one exactly 1, since the type lock is in the store), no autoscaler | Stateless (ADR-093; its action records and Data Protection keys live in the store); its cost grows with nodes, not viewers, so there is nothing for an HPA to follow | DA (`AddDaServerDashboard`) | W7e |
| OpenTelemetry Collector | A gateway `Deployment` (an HPA on its own CPU is fine); an agent `DaemonSet` only when the platform also collects host or kubelet metrics or container log files | Nodes push OTLP to one address ([architecture.md section 8](architecture.md#telemetry)) and log over OTLP, so they need no per-machine agent ([Collector deployment: agent and gateway](https://opentelemetry.io/docs/collector/deploy/)) | Platform (sample `AddHelmChart` when published) | W7e / W12 |
| Schema, upgrade check, rollout, load generators | `Job` (section 3) | Run to completion; a Deployment restarts them forever | DA (sample for load generators) | W11 / W12 / N9 |
| Image pre-pull | Short bare pods created by the rollout Job, pinned to each machine that hosts a pod of the group | A pre-pull `DaemonSet` would pull on every machine, including those without the group, and would have to be deleted after | Rollout Job | W11 |
| Scheduled work | No `CronJob` (section 3) | | - | - |

ReplicaSets, ReplicationControllers and bare pods are never published directly.

### DaemonSet: not for DaServer nodes

A DaemonSet runs one pod per eligible machine. DaServer nodes do not use one:

- **The count follows machines, not load.** Capacity would change when the node pool changes, and an HPA cannot
  scale it ("HorizontalPodAutoscaling does not apply to objects that can't be scaled (for example: a DaemonSet)",
  [HPA](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/)). DaServer scales by
  adding nodes to a group, and placement (ADR-079) spreads slots over them.
- **A machine drain does not stop them.** `kubectl drain` does not evict DaemonSet pods (they would be recreated on
  the cordoned machine at once), so a node would not leave, hand over its actors or drain its clients before the
  machine goes; only the kubelet's graceful node shutdown, if it is configured, would send SIGTERM, with its own
  budget. A PodDisruptionBudget does not apply either
  ([safely drain a node](https://kubernetes.io/docs/tasks/administer-cluster/safely-drain-node/)).
- **No stable identity.** DaemonSet pod names carry a random suffix and change when a machine is replaced; ADR-085
  needs `{group}-{ordinal}`.
- **What a DaemonSet would give is reachable otherwise.** One node per machine is topology spread with
  `whenUnsatisfiable: DoNotSchedule` plus a dedicated node pool (Aspire's `AddNodePool`, as the mmo sample does);
  host networking for direct client connections is a non-goal (section 2, "Per-replica public addresses").

DaemonSets stay for the platform's per-machine software ([DaemonSet](https://kubernetes.io/docs/concepts/workloads/controllers/daemonset/)):
a Collector agent, node-exporter, the CNI and CSI drivers, log shippers. DaServer publishes none.

### Autoscaling per group

| Group | Horizontal | Vertical (VPA) | PDB | Produced by |
|---|---|---|---|---|
| Stateful | KEDA `ScaledObject` (or an HPA through the Prometheus adapter) on `avg(daserver_shard_busy_ratio)` of the group, `minReplicaCount` at least the group's floor (never 0), scale-in one pod at a time (below) | Memory: `InPlaceOrRecreate` (or `Off`); CPU: `Off` or `Initial`, never an in-place mode (below) | `maxUnavailable: 1`, `unhealthyPodEvictionPolicy: AlwaysAllow` | Sample hook |
| Gateways | HPA on CPU or on client sessions per pod (`daserver_clients_sessions`, through KEDA); scale-in limited so drain waves do not overlap | `Off` (recommendations only) | `maxUnavailable` as a percentage (10–25%): one at a time would make a machine drain of a large gateway group take hours | Sample hook |
| Stateless | KEDA or HPA on the busy ratio, or CPU (flash sale); scale-to-zero only for a group nobody calls while idle | `Off` or `Initial` | Optional, percentage | Sample hook |
| Dashboard | None | None (fixed small requests) | None: a PDB on one replica blocks every machine drain | DA |
| Collector | Its own HPA on CPU | Platform's | Platform's | Platform |

DA's only part in autoscaling is a helper that names the right scale target (`StatefulSet` or `Deployment` and its
name), since DA chooses the kind; today's `Publishing.DeploymentOf` hard-codes `Deployment` (section 1). The
`Manifest` type for objects Aspire has no class for (VPA, `ScaledObject`, PDB) becomes public in DA (W11).

#### HPA or KEDA on the busy ratio

| | KEDA `ScaledObject` | Plain HPA |
|---|---|---|
| Metric path | KEDA's Prometheus scaler queries Prometheus; KEDA creates and owns the HPA (`keda-hpa-{name}`) and serves the value as an external metric | Needs the Prometheus adapter registered as `custom.metrics.k8s.io` or `external.metrics.k8s.io` |
| Target | Any kind with a `/scale` subresource, StatefulSets included ([ScaledObject spec](https://keda.sh/docs/latest/reference/scaledobject-spec/)) | Same |
| Behavior | `advanced.horizontalPodAutoscalerConfig.behavior` (passed to the HPA) | `spec.behavior` |
| Pitfall | `minReplicaCount` defaults to **0**: a stateful or gateway group must set it, or an idle cluster scales its nodes away | None specific |
| Pausing | `autoscaling.keda.sh/paused` annotation | No pause field; `behavior.scaleDown.selectPolicy: Disabled` stops scale-in only |

Recommendation: KEDA where it is installed (operations.md already says so), the adapter otherwise; the busy ratio
remains the default signal (design.md, metrics). Use `avg`, not `max`: one hot actor is a placement problem that more
nodes cannot solve. HPA tolerance is per direction and stable since 1.37 (`behavior.scaleUp.tolerance`,
`behavior.scaleDown.tolerance`, default 10%,
[HPA tolerance](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/#tolerance)); a
wider scale-down tolerance (0.2) keeps a group from shrinking on noise. Scale-to-zero (`HPAScaleToZero`, beta in 1.37,
object and external metrics only) suits only a stateless group whose callers tolerate the cold start.

#### Scale-in, draining and StatefulSet ordinals

What happens when a controller removes a pod, per kind:

| Kind | Which pods go | What DaServer does | What to set |
|---|---|---|---|
| StatefulSet, `Parallel` | The highest ordinals, **all at once** ("all Pods are created or terminated simultaneously", [StatefulSet: parallel pod management](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/#parallel-pod-management)) | Each leaves gracefully: hands its actors over or saves them (ADR-063), within the stop budget | `behavior.scaleDown.policies: [{type: Pods, value: 1, periodSeconds: P}]` with P at least the grace period (stop budget + margin, 40 s by default), and `stabilizationWindowSeconds` 600 or more: a scale-in moves every actor of the leaving node and a later scale-out moves slots back, so churn is expensive. Several simultaneous leaves would also hit the store and the coordinator at once |
| Deployment (gateways) | The ReplicaSet's choice: unready and newer pods first, then by `controller.kubernetes.io/pod-deletion-cost` | The gateway fails readiness, waits the deregistration delay, retires its connections in waves over `DrainSpread`; its clients resume on the others | A scale-in policy of a few pods per period longer than `DrainSpread`, so retire waves from two gateways do not pile onto the same survivors |
| Deployment (stateless) | As above | Leaves; nothing to hand over | Defaults are fine |

- **A PDB does not slow a scale-in.** It guards the eviction API (machine drains, the VPA updater, node autoscalers);
  a controller lowering `replicas` deletes pods directly ("deleting deployments or pods bypasses Pod Disruption
  Budgets", [disruptions](https://kubernetes.io/docs/concepts/workloads/pods/disruptions/)). The scale-in policy
  above is the only rate limit.
- **No `pod-deletion-cost`.** Choosing the gateway with the fewest connections would need someone with API access to
  annotate pods before each scale-in; the docs warn against updating it from a metric
  ([ReplicaSet: pod deletion cost](https://kubernetes.io/docs/concepts/workloads/controllers/replicaset/#pod-deletion-cost)),
  and connection rebalancing (ADR-089) already keeps gateways even, so every choice costs the same.
- **A scale-in during a hot-restart rollout** would delete a pod the rollout may have armed or be handing over: its
  node then stops gracefully (SIGTERM is never a hot restart, so nothing is lost), but the roll for that pod fails and
  its actors move to other nodes a moment before the roll would have restarted them in place. Scale-out during a roll is harmless (new pods start from the
  new template image; arming waits while a member joins). So the rollout Job **pauses scale-in of the group it
  rolls** and releases it at the end: on a `ScaledObject` the `autoscaling.keda.sh/paused` annotation is too coarse
  (it also stops scale-out), so it sets `advanced.horizontalPodAutoscalerConfig.behavior.scaleDown.selectPolicy:
  Disabled`; on a plain HPA `behavior.scaleDown.selectPolicy: Disabled`; both with Server-Side Apply under its own
  field manager (section 11), so the release does not own the field and the Job's apply never conflicts with it. A
  Job killed mid-roll leaves scale-in paused: the next rollout clears the field first, and the Dashboard shows a
  group whose scale-in `daserver-rollout` holds. Evictions in that window still follow the PDB (one pod at a time);
  the arm-to-handover window is seconds.

#### VPA modes against the busy-ratio HPA

| Mode | What it does ([VPA](https://kubernetes.io/docs/concepts/workloads/autoscaling/vertical-pod-autoscale/)) | For DaServer |
|---|---|---|
| `Off` | Recommendations in `.status` only | Every group: sizing input for the application's requests |
| `Initial` | Sets requests when a pod is created, never after | Good for CPU: `DOTNET_PROCESSOR_COUNT` comes from `requests.cpu` through the downward API (section 2), so a new pod gets as many worlds as the CPU it was given |
| `Recreate` (`Auto` is its deprecated alias) | Evicts pods whose requests differ | Each eviction is a graceful leave and an actor move; prefer `InPlaceOrRecreate` |
| `InPlaceOrRecreate` (beta) | Resizes running pods through the `resize` subresource, evicts when it cannot | Memory of stateful groups (below); it falls back to eviction, which honours the PDB |
| `InPlace` (alpha, VPA 1.7, feature gate) | Resizes in place, never evicts; waits when the machine has no room | Not yet: alpha |

- **CPU never in place.** A running node's parallelism is fixed at start: `Environment.ProcessorCount` "is fixed at
  .NET runtime startup for the process lifetime"
  ([Environment.ProcessorCount](https://learn.microsoft.com/dotnet/api/system.environment.processorcount)), the
  downward-API variable is resolved when the container starts, and a hot restart keeps the handed-over world count
  (architecture.md section 5). More CPU in place only raises the cgroup weight of worlds that already exist; the
  group's CPU grows horizontally. A group that pins `DaServer:Shards` (the mmo `map`) is the same. Whether
  `DOTNET_PROCESSOR_COUNT` (from `requests.cpu`) is honoured by a Native AOT node as by a JIT one, and whether a
  container restart after a resize picks up the new request, is not documented upstream: W8 and W12 test both
  (`CpuCountFromRequestsUnderAot`, `CpuResizeKeepsWorldCount`).
- **Not both autoscalers on one resource.** VPA and HPA must not both act on CPU (or memory) of the same workload.
  The busy ratio is a custom metric, so the combination is allowed, but VPA in an active mode on CPU would change
  what the ratio means under the HPA. Hence: HPA on the busy ratio for CPU, VPA on memory only
  (`resourcePolicy.containerPolicies[].controlledResources: [memory]`) or `Off`.
- `controlledValues: RequestsOnly` when the application sets a memory limit by hand; the default
  (`RequestsAndLimits`) scales the limit with the request.

#### Garbage collector settings

Nodes run Server GC with concurrent collection, as the ASP.NET Core default, and turn DATAS off
(`GarbageCollectionAdaptationMode=0`): DATAS (on by default since .NET 9) changes the number of GC heaps with the
allocation rate to save memory, but a node's worlds are sized to its cores and its hot paths allocate nothing in
steady state, so a fixed heap per core gives steadier tick latency for no memory cost worth having. Native AOT has no
runtimeconfig.json: the setting is baked in at publish (the `GarbageCollectionAdaptationMode` MSBuild property) or
set as `DOTNET_GCDynamicAdaptationMode=0`, and W9 verifies the AOT image honours it. W9 measures both
with the 10,000-bot E2E (p99 broadcast, working set) and keeps DATAS off only if it wins. The GC's hard limit stays
at its default, 75% of the container's memory limit; the standby runs in its own container (Kubernetes) or process, so
the arm checks its room, not the node's (section 3).

#### In-place resize and the .NET runtime

In-place pod resize is GA since 1.35 ([resize CPU and memory](https://kubernetes.io/docs/tasks/configure-pod-container/resize-container-resources/),
[GA announcement](https://kubernetes.io/blog/2025/12/19/kubernetes-v1-35-in-place-pod-resize-ga/)): requests and
limits are mutable through the `resize` subresource, each container's `resizePolicy` per resource says
`NotRequired` (default) or `RestartContainer`, memory limit decreases are allowed but best-effort (skipped while usage
is above the new limit), the QoS class cannot change, and resizing a memory-backed `emptyDir`'s `sizeLimit` is alpha
in 1.37 (cgroup v2 only).

| Resource | What .NET does on a live change | Target |
|---|---|---|
| CPU limit or request | Nothing: `ProcessorCount` and the world count are fixed (above) | No CPU limit on node pods (throttling stalls ticks); `resizePolicy cpu: NotRequired` so a resize never restarts a container |
| Memory limit | The GC's hard limit (by default 75% of the container limit) is computed at start, but `GC.RefreshMemoryLimit()` (.NET 8+) "reconfigure[s] itself by detecting the various memory limits on the system", container limits included; it throws when the new limit is below what is already committed ([GC.RefreshMemoryLimit](https://learn.microsoft.com/dotnet/api/system.gc.refreshmemorylimit)) | **DaServer** watches the cgroup's `memory.max` off the shard threads (every few seconds, cgroup v2) and calls `GC.RefreshMemoryLimit()` when it changes, logging a refusal; the arm's memory check reads the limits live (section 3). Then `resizePolicy memory: NotRequired`. Native AOT is the same path: its `GC.RefreshMemoryLimit` ([GC.NativeAot.cs](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/System.Private.CoreLib/src/System/GC.NativeAot.cs)) calls `RhRefreshMemoryLimit`, which asks the same GC to refresh ([GCHelpers.cpp](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/Runtime/GCHelpers.cpp)), and the AOT runtime links the GC's own cgroup reader (`gc/unix/cgroup.cpp`), which reads the limit again on every call (`GCToOSInterface::GetPhysicalMemoryLimit`); W12 proves it on kind with both the JIT and the AOT image (`MemoryLimitResizeRefreshesGcLimit`, `MemoryLimitResizeRefreshesGcLimitUnderAot`) |

Today the mmo sample's `ResizeInPlace` uses `memory: RestartContainer`, which on a stateful pod is a graceful stop
and a rejoin in the same pod (every actor of the node moves twice). With the refresh above it becomes `NotRequired`.
Stateful groups should set a memory limit (equal to the request) so that the GC limit and the arm check have one
bound; CPU stays request-only.

#### Other objects and controllers

| Object or feature | Use | Why | How |
|---|---|---|---|
| PodDisruptionBudget | Per group as in the table above; `unhealthyPodEvictionPolicy: AlwaysAllow` everywhere | With the default `IfHealthyBudget`, a group whose pods are all unready (a store outage, a crash loop) blocks every machine drain ([PDB](https://kubernetes.io/docs/tasks/run-application/configure-pdb/#unhealthy-pod-eviction-policy)) | Sample hook |
| PriorityClass | The application may give stateful groups a higher class than load generators and batch work; DA sets none | Preemption deletes a pod (a graceful leave, but an actor move); deferred in-place resizes are retried by priority first. Classes are cluster-scoped | Platform defines, sample references (`priorityClassName`) |
| Topology spread | Section 2 (hostname soft, zones by the application) | | DA, sample |
| Node pools / affinity | Optional, by the application (mmo: `AddNodePool("stateful")`) | | Sample |
| Readiness gates | None of DaServer's: `/health` already says joined and not leaving. A cloud load balancer controller may inject its own gate on gateway pods (AWS Load Balancer Controller), which the surge-then-drain roll then waits for: wanted | | Platform |
| Native sidecar containers (GA 1.33) | None on node pods | Telemetry goes over OTLP to the Collector; node links authenticate themselves (ADR-065). A service mesh is the platform's; if one is present, the cluster port (7000) should bypass its proxy (pod to pod, latency-bound) | - |
| Init containers | None | The schema is a Job; a node waits for the store itself (section 3) | - |
| StatefulSet `maxUnavailable` (beta, 1.35) | Not used | `OnDelete` groups never roll through the controller; client-facing stateful groups roll one pod at a time on purpose | - |
| StatefulSet `Recreate` strategy (alpha, 1.37) | Not used | Stops every node of the group at once | - |
| `minReadySeconds` | Not used | Readiness already waits for the join | - |
| Service `LoadBalancer` or Gateway API | Client ports: a `LoadBalancer` Service (today's sample), or a Gateway with a `TCPRoute` (Standard since Gateway API 1.6, [release](https://kubernetes.io/blog/2026/08/03/gateway-api-v1-6-release/)) for TCP and an `HTTPRoute` for `/clients/ws`; the Dashboard and webhooks through an `HTTPRoute` (Aspire has typed `Gateway` and `HTTPRoute`, no `TCPRoute`) | The kind of load balancer is the application's (CLAUDE.md) | Sample hook |
| Node autoscaling (Cluster Autoscaler, Karpenter) | Platform's | Its consolidation evicts through the eviction API, so PDBs and graceful leave apply; DA sets no `safe-to-evict` or `do-not-disrupt` annotations | Platform |
| LimitRange, ResourceQuota | Platform's | VPA clamps its recommendations to LimitRanges | Platform |
| Lease, CRDs, operators | None | No Kubernetes API from nodes (section 5); no DaServer operator (ADR-085) | - |

## 11. Server-Side Apply

**What it is.** Server-Side Apply (SSA, GA since 1.22) moves `apply` into the API server: a client sends a partial
object holding only the fields it has an opinion on (`PATCH` with `application/apply-patch+yaml`), and the server
merges it and records, per field, which **field manager** owns it in `metadata.managedFields`. Changing a field
another manager owns is a **conflict**; the applier can force it (taking ownership), drop the field from its
configuration (giving it up), or set the same value (sharing ownership)
([Server-Side Apply](https://kubernetes.io/docs/reference/using-api/server-side-apply/)). Client-Side Apply (CSA)
is the original `kubectl apply`: kubectl computes a three-way patch from the last configuration it stored in the
`kubectl.kubernetes.io/last-applied-configuration` annotation, the live object and the new file.

**Not control-plane only.** SSA is an API-server feature that any client uses: `kubectl apply --server-side`, Helm 4,
Argo CD (`ServerSideApply=true`, manager `argocd-controller`), Flux (always SSA), and controllers through client-go's
apply configurations. Built-in controllers mostly still write with `Update`/`Patch` (the HPA through the `scale`
subresource, the VPA updater through `resize`), and those writes are recorded in `managedFields` too, so SSA appliers
see them as owners and conflict with them. CSA is purely client-side (kubectl); Helm 3 had its own three-way merge
against the stored release manifest instead of the annotation.

**CSA's pitfalls, all of which DaServer would hit:**

- No conflict detection: an apply silently overwrites what another actor changed. Helm 3 and CSA reset an
  HPA-managed `spec.replicas` to the chart's value on every upgrade.
- The annotation holds the whole object; annotations are capped at 262,144 bytes in total ("Resource is too big to
  fit in 262144 bytes allowed annotation size", [Argo CD sync options](https://argo-cd.readthedocs.io/en/stable/user-guide/sync-options/)),
  which large ConfigMaps (configuration tables) and CRDs reach.
- It is stale state: values others overwrote stay in the applier's annotation until it touches that field again.
- Custom resources (`ScaledObject`, VPA) get a JSON merge patch: lists are replaced whole.
- Removing a field from the file deletes it from the live object, which for `replicas` means the default of 1
  ([HPA: migrating to autoscaling](https://kubernetes.io/docs/concepts/workloads/autoscaling/horizontal-pod-autoscale/#migrating-deployments-and-statefulsets-to-horizontal-autoscaling)).
  For a stateful group that is a mass leave.

**What applies DaServer's objects.** `aspire deploy` for Kubernetes runs `helm upgrade --install ...
--server-side=true --force-conflicts` (checked in the `Aspire.Hosting.Kubernetes` 13.6.1 assembly), and Aspire uses
field manager `helm` for its own follow-up patches. Helm 4 applies with field manager `helm`, defaults to SSA for new
installs and keeps an existing release's method on upgrade (`--server-side=auto`, recorded as `apply_method`)
([HIP-0023](https://helm.sh/community/hips/hip-0023)). An operator running `helm upgrade` without
`--force-conflicts` gets an error on a conflicting field instead. Because `aspire deploy` forces, **any field the chart
renders is taken back on every deploy**, whoever changed it in between. The ownership plan below follows from that.

### Who owns which field

| Field | Owner (field manager) | Rule |
|---|---|---|
| Workload template, selector, `serviceName`, `updateStrategy`, Services, ConfigMaps, Secrets, PDB, autoscaler objects | `helm` (the chart DA and the sample produce) | Everything the AppHost declares |
| `spec.replicas` of an autoscaled group | The HPA controller (KEDA's HPA included), through `scale` | **The chart does not render `replicas` for a group with an autoscaler.** The sample's helper that adds the HPA or `ScaledObject` sets the workload's `Spec.Replicas = null` in the same hook (Aspire's `Replicas` is nullable: "If null, the default value defined by the server will be used"), so one place says who owns the count. Otherwise every `aspire deploy` forces the rendered count back (a stateful group scaled to 8 drops to 2: six nodes leave at once) |
| `spec.replicas` of a group without an autoscaler | `helm` | Rendered from `AddNodes(replicas)`; `kubectl scale` is undone by the next deploy, as intended |
| Pod `spec.containers[].resources` | VPA admission (at creation) and updater (`resize`); `daserver-rollout` for the parked container's memory request during a roll | Pods are not in the chart; no conflict with `helm`. How a VPA and the rollout's resize of the pair combine is an open point settled on kind in W11 (architecture.md section 10) |
| Pod `spec.containers[node-a or node-b].image` after a hot restart | `daserver-rollout` | The rollout Job applies only `{metadata.name, metadata.labels[controller-revision-hash], spec.containers: [{name, image}]}` for the parked container, always together in one apply, with SSA and `force=true` (a later apply that omitted the image would remove it from the manager's set) (the image field belongs to the StatefulSet controller from pod creation; the docs advise controllers to force on objects they manage). `managedFields` then records which pods were hot-restarted and when; the template stays `helm`'s |
| `behavior.scaleDown.selectPolicy` during a roll | `daserver-rollout` | Applied without force: if the chart sets that field, the roll refuses to start and says so. Released by applying an empty configuration under the same manager, which removes the fields it owns |
| Pre-pull pods | `daserver-rollout` | Created and deleted by the Job |
| Anything on Kubernetes from nodes or the Dashboard | Nobody | Nodes have no token (section 5); the Dashboard acts through the nodes' admin plane (ADR-093) and the deploy webhook, and needs no Kubernetes API; it holds the admin key, never the signing secret |

The rollout Role (section 5) gains `patch` on `horizontalpodautoscalers` (and on `scaledobjects.keda.sh` when the
group uses KEDA) for the scale-in pause; nothing else changes. Using SSA for the Job's writes instead of
read-modify-write patches needs no `resourceVersion`, cannot clobber a field it did not name, and leaves an audit trail
in `managedFields` (`kubectl get pod game-0 --show-managed-fields`).

### Moving an existing release

- **From Helm 3 (CSA) to Helm 4.** `aspire deploy` already uses SSA. A release installed by Helm 3 and upgraded by
  hand stays on CSA under `--server-side=auto`; pass `--server-side=true` once.
- **Taking `replicas` out of the chart** (adding an autoscaler to a running group). If the HPA has never changed the
  count, `helm` still owns it alone and the upgrade that drops the field resets it to 1. Before that upgrade, hand
  the field over as the SSA page shows: `kubectl apply --server-side --field-manager=handover-to-hpa` a manifest with
  only `spec.replicas` at the current value; the temporary manager disappears once the HPA writes the field
  ([transferring ownership](https://kubernetes.io/docs/reference/using-api/server-side-apply/#transferring-ownership)).
  The upgrade note for W11 says so.
- **Argo CD or Flux instead of `aspire deploy`.** Both apply the chart with SSA; the same rule (no rendered
  `replicas` for autoscaled groups) avoids their need for `ignoreDifferences` on `/spec/replicas`.
- **Mixing tools.** Never `kubectl apply` (CSA) the rendered chart over a Helm 4 release: it adds the annotation and
  its stale copy. Diagnose with `kubectl get <kind> <name> --show-managed-fields -o yaml`.
