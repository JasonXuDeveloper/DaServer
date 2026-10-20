# Target deployment shape (draft, not implemented)

What `DaServer.Aspire` and the samples should publish for Kubernetes and Docker Compose, after an audit of what they
publish today (`aspire publish` of both samples, Aspire 13.6.1, Kubernetes publisher 13.6.1-preview, run on
2026-10-20). Decided items join ADR-085 / roadmap R11 when merged; this file is deleted then (R10).

Steps named below: **R6** runtime (hot restart, persistence), **R7** samples, **R8** Native AOT, **R11** Kubernetes
shape (ADR-085), **N9** hot-restart operator side. "Hook" means Aspire's `PublishAsKubernetesService` /
`PublishAsDockerComposeService`; "DA" means code in `DaServer.Aspire`.

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

How DA tells the kinds apart: before R, a group with `WithActorStore` or an explicit `.Stateful()`; after R3, from the
committed `wire.manifest` (`actor` / `hosted-by`): a group hosting any actor type that is not `[Stateless]` is
stateful; a group hosting only `Connection` and `[Stateless]` types is not.

### Stateful node groups (host actors): StatefulSet

| Setting | Target | Why | How | Step |
|---|---|---|---|---|
| Kind | `StatefulSet` named `{group}` (not `{group}-statefulset`), so pods are `game-0`, `game-1`: the same names as the run-mode resources and the node names | Hot restart keeps the pod and its `emptyDir` (ADR-085); stable names match the dashboard and logs | DA: replace `resource.Workload` in the hook | R11 |
| `serviceName` | `{group}-peers` (the existing headless Service, `publishNotReadyAddresses: true`); per-pod DNS `game-0.game-peers` | StatefulSets require a governing headless Service ([StatefulSet](https://kubernetes.io/docs/concepts/workloads/controllers/statefulset/)) | DA | R11 |
| `podManagementPolicy` | `Parallel` | Nodes join by gossip and need no order; `OrderedReady` serializes scale-ups (a flash-sale burst waits pod by pod) and can wedge on one broken pod ("forced rollback"). With `maxUnavailable` 1 rolling updates are unchanged | DA | R11 |
| `updateStrategy` | `OnDelete` for groups without client connections; `RollingUpdate` (maxUnavailable 1) for stateful groups that also accept clients (flash sale `sale`) | `OnDelete`: a template change replaces no pod; DaServer's rollout decides per pod (hot restart or delete-and-recreate). A group hosting `Connection` cannot hot restart (architecture draft §5) | DA | R11 |
| Template image vs. pods | `helm upgrade` (or `aspire deploy`) sets the template image; the rollout only patches pods | **Correction to ADR-085**: if the command patches the StatefulSet template, the live object drifts from the Helm release and the next `helm upgrade` writes the old tag back | N9 | N9 |
| Replicas | `spec.replicas` from `AddNodes(replicas)`; an HPA may own it (`scale` subresource works on StatefulSets, [HPA](https://kubernetes.io/docs/tasks/run-application/horizontal-pod-autoscale/)) | Scale-in removes the highest ordinal, which leaves gracefully | DA | R11 |
| Grace period | `terminationGracePeriodSeconds` = stop budget + margin + preStop, as today | Unchanged ([pod termination](https://kubernetes.io/docs/concepts/workloads/pods/pod-lifecycle/#pod-termination)) | DA (exists) | - |
| preStop | `sleep 5`, as today. Note: the kubelet's container kill runs preStop whatever the reason (to confirm on kind in R11), so each hot restart likely waits 5 s before SIGTERM (the node keeps serving, so no pause is added; a roll of N pods takes 5·N s longer) | [Lifecycle hooks](https://kubernetes.io/docs/concepts/containers/container-lifecycle-hooks/) | DA (exists) | N9 |
| Snapshot volume | `emptyDir: {medium: Memory, sizeLimit: <2 x expected world bytes>}` at `/var/run/daserver/snapshot` (`DaServer__SnapshotPath`) | Survives container restarts, not pod deletion; tmpfs counts against the memory limit ([emptyDir](https://kubernetes.io/docs/concepts/storage/volumes/#emptydir)) | DA | R11 / R6 |
| Persistent volume | **None** (no `volumeClaimTemplates`) | State lives in the actor store; a snapshot outlives one restart only. A PV would be zone-bound and slow | - | R11 |
| PDB | `maxUnavailable: 1`, `unhealthyPodEvictionPolicy: AlwaysAllow`; stays an application choice (CLAUDE.md), the samples keep adding it | With the default policy, a store outage that makes every pod unready blocks every node drain ([PDB](https://kubernetes.io/docs/tasks/run-application/configure-pdb/#unhealthy-pod-eviction-policy)) | Sample hook | R7 |
| Topology spread | Soft spread over `kubernetes.io/hostname` (`whenUnsatisfiable: ScheduleAnyway`); zone spread left to the app | One machine's loss should not take several nodes' actors at once ([topology spread](https://kubernetes.io/docs/concepts/scheduling-eviction/topology-spread-constraints/)) | DA (hostname), sample (zones) | R11 |
| CPU count | `env DOTNET_PROCESSOR_COUNT` from `resourceFieldRef: requests.cpu` (rounded up) when no CPU limit is set | The world count defaults to `ProcessorCount`, which ignores requests: today a pod on a 64-core host runs 64 shards ([downward API](https://kubernetes.io/docs/tasks/inject-data-application/environment-variable-expose-pod-information/)) | DA | R11 |
| Image pre-pull | Before patching a pod, the rollout makes the node pull the new image (a short pod pinned to that node with the new image, exiting at once) | The kubelet kills the old container and only then pulls the image; a pull inside the pause defeats "hundreds of milliseconds" | N9 rollout Job | N9 |

Moving an existing release from Deployment to StatefulSet deletes the Deployment: every pod of the group stops at
once (each leaves gracefully and saves). The upgrade note says so; scale the group down first or accept the pause.

### Stateless groups and gateways: Deployment

| Setting | Target | Why | How | Step |
|---|---|---|---|---|
| Kind | `Deployment`, `RollingUpdate` maxSurge 1, maxUnavailable 0 (today 1) | Start new, then stop old (ADR-083, architecture §5); with 0 unavailable a gateway roll never drops capacity | DA | R11 |
| Services | As for stateful groups (below) | | DA | R11 |
| HPA | Allowed (flash sale uses one); `targetRef` names the right kind | `Publishing.DeploymentOf` assumes Deployment | Sample | R7 |
| Probes, grace, preStop, spread, CPU count | As for stateful groups | | DA | R11 |

### Probes (both kinds)

| Probe | Target | Why |
|---|---|---|
| startupProbe | `/alive`, period 2 s, failureThreshold 45 (90 s) | Covers the store's 60 s `InitializeTimeout` and a snapshot or records load; liveness then needs no `initialDelaySeconds` ([probes](https://kubernetes.io/docs/concepts/configuration/liveness-readiness-startup-probes/)) |
| readinessProbe | `/health` (joined, not leaving, store reachable), timeout 2 s | Unchanged meaning; 1 s timeouts flap under GC or load |
| livenessProbe | `/alive` (no stalled world), timeout 2 s | Unchanged meaning |

How: DA (`WithHttpProbe(ProbeType.Startup, ...)`), R11.

### Services and ports (both kinds)

Ports are declared per node group in `apphost.cs` and are the same on every replica (one pod template, an IP per pod;
[draft §2](programming-model.md#2-服务器与节点)). Only what the AppHost writes is exposed.

| Service | Target | Why | How | Step |
|---|---|---|---|---|
| `{group}-peers` (headless) | The cluster port (7000) only, as today | Node links go pod to pod; seeds | DA (exists) | - |
| `{group}-service` (ClusterIP) | The HTTP port (health, admin) only, reachable inside the cluster; today it also carries the cluster and client ports | Smaller surface; a load-balanced cluster port is never right | DA | R11 |
| Client Service | Only for a group with `WithClientConnections(port)`: the declared port (TCP and WebSocket), one address balanced over the replicas. Its `type` and annotations (`LoadBalancer`, `externalTrafficPolicy`) come from the application's hook, as the mmo sample does today | CLAUDE.md: the load balancer's kind is the platform's and the application's | DA (Service), sample hook (kind) | R11 |
| Public HTTP | Only for a group with Aspire's `WithExternalHttpEndpoints()` (webhooks) | Admin and probes stay internal | Aspire (exists) | - |
| Per-replica public addresses | None: direct connect to one pod (Agones-style) is a non-goal; a later direct-connect feature would add it | | - | - |

A group hosting `Connection` without `WithClientConnections`, or the reverse, fails the AppHost build.

## 3. Jobs

| Job | What it runs | When | Why not today's way | How | Step |
|---|---|---|---|---|---|
| `{cluster}-schema` | The node image in schema mode (`--DaServer:Migrate`): `PostgreSqlActorStore.Schema` under the advisory lock, with a DDL role | Before nodes need the table: a Helm `pre-install,pre-upgrade` hook with an external database; a plain Job (named with the release revision, `ttlSecondsAfterFinished`) when the chart also runs PostgreSQL, since a pre-install hook would run before the database exists | Creation at node start does not race (advisory lock), but: every node start, scale-up and hot restart runs `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, which takes an `ACCESS EXCLUSIVE` lock on the live table before it checks anything ([ALTER TABLE](https://www.postgresql.org/docs/current/sql-altertable.html)): every claim and save of the cluster waits, and queues behind any long transaction. It also gives the runtime role DDL rights. Nodes then run `createTable: false` with a DML-only role and treat a missing table as "not ready yet" (retry within the startup probe) instead of exiting | DA (`WithActorStore(..., schema: SchemaMode.Job)`), DaServer (the mode), DaServer.Persistence.PostgreSql (missing table retried) | R6 / R11 |
| `{cluster}-upgrade-check` | The new image in check mode: reads the running cluster's manifests (a node's `/admin/manifest`, admin token) and diffs them with its own `wire.manifest`; prints the hot-restart path (in place, through records, rolling) and fails on an incompatible change | Helm `pre-upgrade` hook; `aspire deploy` step for Compose | Today only the route check runs, on the publishing machine, against the build alone (`RouteCheckStep.cs`); nothing compares with what is running | DA (step + Job), DaServer (mode), runtime (`/admin/manifest`) | N9 |
| `{cluster}-rollout-{n}` | The new image in rollout mode, ServiceAccount `{cluster}-rollout`: per pod of each stateful group, one at a time: pre-pull, arm the node (`/admin/restart`), patch the pod's image, wait for ready; or delete the pod when the path is rolling | Started by the AppHost command (`aspire do {cluster}-restart`) or `kubectl create job` | See section 5 for why in-cluster | DA (command creates the Job), DaServer (mode) | N9 |
| Load generators | `bots`, `crowd`: **a Job** (`backoffLimit: 0`, `ttlSecondsAfterFinished`) behind `-- LoadTest=true`, otherwise excluded from publish | They run to completion; as Deployments they restart forever (a 10,000-bot storm every few minutes against the deployed cluster) | Sample: `ExcludeFromManifest()` in publish mode unless asked, the Job as an `AdditionalResource`; Compose: `profiles: [loadtest]` | R7 |

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
| Signing secret | **One** cluster Secret `{cluster}-signing` with keys `current` and optional `accepted`; every node reads them through `env.valueFrom.secretKeyRef` (`accepted` with `optional: true`) as `DaServer__SigningSecret` / `DaServer__AcceptedSigningSecret` | Today each group has its own copy in the values (four `--set`s in the mmo chart that must agree; one typo splits the cluster: its Hellos are refused). One object is what the rotation steps change | DA: in publish mode, leave the variable out of the generated per-group Secret, add the Secret and the `valueFrom` env in the hook | R11 |
| Missing secret | Published nodes refuse to start without a signing secret (`DaServer__RequireSigningSecret=true`, set by DA in publish mode only; run mode and tests stay open) | Today an empty value silently yields an unauthenticated cluster; Helm templates from Aspire cannot use `required` | Runtime option (R6) + DA | R11 |
| Existing Secret | `WithSigningSecret(existingSecretName)`: reference a Secret made by External Secrets, Sealed Secrets or the CSI driver instead of a Helm value | Secret values in Helm values end up in release history and CI logs ([secrets good practices](https://kubernetes.io/docs/concepts/security/secrets-good-practices/)) | DA | R11 |
| Rotation | Change `accepted`, roll; swap; roll; drop. On Kubernetes "roll" for a stateful `OnDelete` group is a hot restart per pod: a container restart re-reads `secretKeyRef` env (env is resolved when the container starts) | The existing three-step rotation (operations.md) needs no new mechanism, only the key in the Secret | Docs | R11 |
| Admin token | Operators, CI and the rollout Job get the **derived** token (`AdminToken.For`, HMAC of `daserver-admin`) in a Secret `{cluster}-admin`, never the signing secret | The token cannot sign links or tickets; the signing secret can | DA (renders it at publish from the parameter) | N9 |
| Database | Two roles: owner (schema Job) and runtime (`SELECT, INSERT, UPDATE` on the table). Connection strings stay in Secrets (Aspire already puts `ConnectionStrings__actors` there); `AddConnectionString` parameters are secret | Least privilege; no DDL from nodes | Samples (two connection strings), DA (`WithActorStore(runtime, owner)`) | R7 / R11 |
| OTLP | `OTEL_EXPORTER_OTLP_ENDPOINT` a plain parameter, `OTEL_EXPORTER_OTLP_HEADERS` (API keys) a `secret: true` parameter; both through `DaServerNodeGroup<T>.WithEnvironment` | Aspire wires OTLP to its in-cluster dashboard; a real collector usually needs a key | Already expressible; document in operations.md | R11 |
| Non-secret config | Stays in Aspire's per-group ConfigMap (`envFrom`): cluster name, ports, seeds, flush interval | Correct today. A ConfigMap change restarts nothing; Deployments get a `checksum/config` pod annotation so `helm upgrade` rolls them | DA (annotation) | R11 |

Compose: Aspire writes secret parameters as `${VAR}` in `docker-compose.yaml` and empty keys in `.env`. Target:
`${SAMPLE_SECRET:?set the cluster signing secret}` (Compose refuses to start without it), plus
`DaServer__RequireSigningSecret`. File-based Compose `secrets:` (mounted at `/run/secrets`) need the node to read a
key-per-file source; worth it only together with reload-on-change rotation (later, not R11).

## 5. RBAC and who runs the hot restart

| Identity | Permissions | Why |
|---|---|---|
| Nodes: ServiceAccount `{cluster}-node`, `automountServiceAccountToken: false` | None | Discovery is DNS (ADR-030); a node with an API token is a foothold for nothing it needs ([opt out of automounting](https://kubernetes.io/docs/tasks/configure-pod-container/configure-service-account/#opt-out-of-api-credential-automounting)) |
| Schema and upgrade-check Jobs | None (automount off) | They talk to the database and the nodes' admin plane only |
| Rollout Job: ServiceAccount `{cluster}-rollout` + Role + RoleBinding in the release namespace | `pods`: get, list, watch, patch, delete; `pods` create (the pre-pull pods); `statefulsets`: get, list, watch (read the template image and replicas). No `statefulsets` patch (Helm owns the template) | The two writes the rollout makes; namespaced, nothing cluster-wide ([RBAC](https://kubernetes.io/docs/reference/access-authn-authz/rbac/)) |
| The operator or CI | `create` on `jobs`, `get` on `pods/log` in the namespace | Starting and watching the rollout |

**Who runs it: an in-cluster Job, started by the AppHost's `{cluster}-restart` command (`aspire do mmo-restart`).** The alternative, the operator's CLI with a
kubeconfig, would need `pods/proxy` or port-forwards to reach each pod's admin plane, would stop half-way when a
laptop sleeps during a minutes-long one-pod-at-a-time roll, and would carry the operator's broad credentials. The Job
runs the new image itself (so it has the new `wire.manifest`), reaches pods by IP inside the NetworkPolicy, and its
token can do only the patches above. Aspire has typed `Role`, `RoleBinding` and `ServiceAccountV1`: DA adds them in
the hook of the first stateful group. Compose has no API: there `{cluster}-restart` does the roll locally (see 8).

## 6. Network policy

DA emits these (the ports and groups are DaServer's knowledge; CNIs without NetworkPolicy support ignore them):

| Policy | Allows | Why |
|---|---|---|
| `{cluster}-cluster-plane` (all node pods) | Ingress on the cluster port (7000) only from pods labelled `daserver.io/cluster={cluster}` | Node links; defence in depth beside link authentication (ADR-065) |
| `{cluster}-http-plane` | Ingress on the HTTP port from the release's Jobs and from a namespace selector given as a parameter (`metrics-namespace`, Prometheus); kubelet probes come from the node and most CNIs allow them. From anywhere only for groups with `WithExternalHttpEndpoints()` | `/admin` (drain, unload) sits on this port, today open to the whole cluster network through `{group}-service` |
| `{cluster}-clients` (groups with `WithClientConnections(port)`) | Ingress on the declared client port from anywhere | The load balancer's traffic |
| Store | Egress from node pods and the schema Job to the database port; ingress to the in-chart PostgreSQL only from those | Samples (the database is the application's) |

Default deny for the namespace is the platform's call. Source: [network policies](https://kubernetes.io/docs/concepts/services-networking/network-policies/).

## 7. Pod security and images

| Setting | Target | Why |
|---|---|---|
| Pod `securityContext` | `runAsNonRoot: true`, `runAsUser/runAsGroup: 1654` (the .NET images' `app` user, `APP_UID`), `seccompProfile: RuntimeDefault` | Pod Security Standard "restricted" ([PSS](https://kubernetes.io/docs/concepts/security/pod-security-standards/)) |
| Container `securityContext` | `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, `readOnlyRootFilesystem: true` | Ports are above 1024 (8080, 7000, 7100): no capability needed ([security context](https://kubernetes.io/docs/tasks/configure-pod-container/security-context/)) |
| Writable paths | `/tmp` (`emptyDir`, `sizeLimit: 64Mi`: .NET's diagnostics IPC socket and temp files) and the snapshot directory (stateful groups). Nothing else: logs go to stdout, the HTTP plane keeps no Data Protection keys (no cookies), the route check's temp file is written on the publishing machine | Verify in R11 by running the E2E with a read-only root |
| Resources | Requests are the application's (the samples' VPAs); DA sets none, but derives `DOTNET_PROCESSOR_COUNT` from them (section 2) | CLAUDE.md: sizing is the platform's |
| Image (Native AOT, ADR-083) | Base `mcr.microsoft.com/dotnet/runtime-deps:11.0-noble-chiseled` (the SDK's AOT default is a chiseled runtime-deps variant; `-extra` only with `InvariantGlobalization=false`, which the samples do not use); JIT nodes `aspnet:11.0-noble-chiseled`. Set `ContainerFamily` in `ServerNode.props` / the flash-sale project | No shell, no package manager, non-root by default ([container publish](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration), [container images](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images)); debugging is `kubectl debug` with an ephemeral container ([debug a pod](https://kubernetes.io/docs/tasks/debug/debug-application/debug-running-pod/#ephemeral-container)) |
| Compose health check | One form for JIT and AOT images: `["CMD", "/app/{assembly}", "--DaServer:Probe=/health"]`, the node's own executable in probe mode (an AOT image is that executable; a JIT image gets it as the apphost, which publish emits by default: DA makes sure `UseAppHost` stays on). Kubernetes uses `httpGet` probes on the same endpoints, so one probe implementation serves both | An AOT image has no `dotnet` (and distroless images have no curl), so today's `dotnet /app/{assembly}.dll` check fails every AOT node; Compose then never reports it healthy and `service_healthy` dependants never start |
| One image per node project | Schema, upgrade-check and rollout Jobs run the node's own image with a mode flag (like today's `Probe` and `ListRoutes` modes) | No extra image to build, version or scan |

Steps: securityContext, volumes and service account R11; images and the Compose health check R8.

## 8. Docker Compose equivalents (where they differ)

| Kubernetes | Compose |
|---|---|
| StatefulSet / Deployment | One service per group, `deploy.replicas` (as today). Hot restart: the image change recreates the container, losing `tmpfs` and changing the hostname (the node name). Compose groups take rolling upgrades only; a single-replica service with a fixed `hostname` and a named volume could hot restart later |
| Jobs | One-shot services (`restart: "no"`): `schema` with nodes `depends_on: {schema: {condition: service_completed_successfully}}`; load generators under `profiles: [loadtest]` |
| Upgrade check, rollout | Steps of `aspire deploy` / `{cluster}-restart`, run on the deploying machine |
| Secrets | `${VAR:?message}` (required) for the signing secret; `.env` never committed |
| securityContext | `read_only: true`, `tmpfs: [/tmp]`, `user: "1654"`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]` |
| NetworkPolicy | Two networks: an `internal: true` network for the cluster and HTTP planes, and the default one only for client-facing services and the dashboard |
| RBAC | None (no API) |
| Health check | The node executable in probe mode (section 7); `postgres` gets `pg_isready` so `game` can wait for `service_healthy` instead of `service_started` |

## 9. What changes in DaServer.Aspire and the samples

`DaServer.Aspire` (R11 unless noted):
1. Stateful groups (manifest-derived after R3; `WithActorStore` or `.Stateful()` before): `StatefulSet` named after
   the group, `serviceName: {group}-peers`, `podManagementPolicy: Parallel`, `OnDelete` (or `RollingUpdate` when the
   group accepts clients), snapshot `emptyDir` (medium Memory), no volume claims.
2. Stateless groups and gateways: `maxUnavailable: 0`; `checksum/config` annotation.
3. Every node pod: `startupProbe`, 2 s probe timeouts, ServiceAccount `{cluster}-node` without a token, restricted
   securityContext, `/tmp` `emptyDir`, soft hostname spread, `DOTNET_PROCESSOR_COUNT` from `requests.cpu`;
   `{group}-service` with the HTTP port only, a client Service only for `WithClientConnections(port)`.
4. One `{cluster}-signing` Secret (`current`, optional `accepted`) through `secretKeyRef`, or an existing Secret
   (`WithSigningSecret`); `DaServer__RequireSigningSecret=true` in publish mode; `{cluster}-admin` token Secret (N9).
5. `WithActorStore(runtime, owner, schema: Job)`: the schema Job (hook or revision-named) and Compose one-shot service.
6. NetworkPolicies for the cluster, HTTP and client planes; Compose internal network.
7. N9: the upgrade-check hook Job, the rollout Job with its ServiceAccount, Role and RoleBinding, the `{cluster}-restart`
   command that starts it, image pre-pull.
8. R8: the Compose health check runs `/app/{assembly}`; chiseled `ContainerFamily` documented for node projects.
9. A public `Manifest` type (today `samples/Shared/Publishing.cs`) for the objects Aspire has no type for (Job,
   PodDisruptionBudget, NetworkPolicy, VerticalPodAutoscaler).

Samples (R7):
1. `Publishing.DeploymentOf` becomes `WorkloadOf` (kind from the workload: `StatefulSet` for game, map and sale).
2. PDBs get `unhealthyPodEvictionPolicy: AlwaysAllow`; the flash-sale HPA gets `behavior.scaleDown` limits (with
   `Parallel`, a drop from 10 to 2 would otherwise remove 8 pods at once).
3. `bots` and `crowd`: excluded from publish unless `-- LoadTest=true`, then a Job (Kubernetes) or `profiles:
   [loadtest]` (Compose).
4. Two database roles (owner for the schema Job, runtime for the nodes); `ContainerFamily` chiseled in
   `samples/mmo/ServerNode.props` and `FlashSale.Server.csproj`.
5. `Sample.AppHost` and `FlashSale.AppHost` become an `apphost.cs` in each sample's server project (file-based
   AppHost, [draft §2](programming-model.md#2-服务器与节点)); client ports with `WithClientConnections(port)`.
5. `tests/DaServer.E2E/PublishTests.cs`: assert `templates/{group}/statefulset.yaml` for game, map and sale (with
   `serviceName`, `OnDelete`/`Parallel`, the snapshot `emptyDir`), `deployment.yaml` for login and gateway, a
   startupProbe, one signing Secret, the schema Job, no bots/crowd workload by default, the NetworkPolicies, and in
   Compose `service_completed_successfully` and the `/app/{assembly}` health check.

Docs to follow when merged: ADR-030 rewritten (already planned by ADR-085) including "no RBAC for nodes; one
namespaced Role for the rollout Job"; CLAUDE.md's orchestration rule lists what DaServer.Aspire adds (it names only
probes, grace periods, preStop, workloads with seeds and client ports today: add StatefulSets for stateful groups, the
signing Secret, schema/upgrade/rollout Jobs, the rollout Role, NetworkPolicies for its planes, pod security);
operations.md "Publish and deploy" and "Rotating the signing secret" (Secret keys, hot restart as the roll).
