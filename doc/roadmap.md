# Roadmap / status

Keep this file current: tick items when done, add items when scope changes.

## Development order (agreed: one item at a time, each proven before the next)

1. Z2 core hot paths (done): H3, H2, M1 status-only refusals, M6 completion queue, M4 actor pools, M7 per-link chunk
   reuse, local sends without a copy, core allocation tests. M2/M3/M5 move with client connections (G steps 6 and 8);
   H1 comes with N3b, H4 with N8.
2. G0-G2 (done, below): one message model (ADR-047), feature points and ingress, client connections as a
   built-in feature, Abstractions and Client packages (ADR-048).
3. T2 ECS bots and business isolation (done): shared rules, a real client, simulated users that run it.
4. N2 `SendAfter` (done) (the delayed send primitive, per-shard timer wheel), N3b no sync-over-async (done; the
   await-only `Reply<T>` for callers outside the shards).
5. N8 (done) collections in messages without garbage (H4), on Nino 4 (ADR-078).
6. D1-D4 (done) graceful shutdown without a store, A-items (Aspire and hosting; A6 uses the `Trace` header extension).
7. C1-C4 (done) cluster hardening (before persistence: fencing assumes correct downing).
8. N1 (done) Contracts / Model / Logic split.
9. N5 (done) persistence (PostgreSQL + in-memory store) with D5-D6 and P1 `DaServer.Testing`, N6 state handoff, N7
   placement affinity with K1-K2 placement strategies and the owner table.
10. V1-V3 (done) versioning and rolling upgrades (before scaling: rolling deploys depend on them).
11. S1-S6 (done) elastic scaling.
12. The ECS rewrite (ADR-081, the "ECS rewrite" entry under "Open cleanup" below; done where W does not replace it).
13. W — refactor to the programming model ([drafts/programming-model.md](drafts/programming-model.md),
    [drafts/architecture.md](drafts/architecture.md)) on EasyEcs 4.7 (ADR-082 to ADR-098). Breaking; each step
    proven before the next (W, not R: the requirement ids R0–R13 are taken).
    W10 runs last, after W12 and N9. Each step updates design.md and README for what it lands and deletes its rows
    from [drafts/architecture.md](drafts/architecture.md) section 9, so the docs stay true between steps; W10 moves
    what is left.
    - [ ] W1 EasyEcs 4.7 (`EasyEcsVersion`). The one home of the EasyEcs compile and behaviour checks the drafts rely on,
      each with its test: the generated explicit `Key` (`Map IIndexed<Map>.Key => this`) is accepted by value indexes,
      `IUniqueIndexed<T>` included, and its `Get`/`TryGet` find the entity (`GeneratedKeyAcceptedByValueIndex`);
      `IActor<TSelf>` (inheriting `IUniqueIndexed<TSelf>`, `IEquatable<TSelf>`, `ISystemGroup`,
      `ISystemGroupWith<In<TSelf>>`, with the static virtual `RootPrefab`) compiles in the framework assembly and in a consumer
      without repeating constraints and without EECS036 (`ActorInterfaceCompilesInConsumer`);
      `entity.Run` takes one to nine components (no zero-component form), so a handler without component parameters is
      no job: the dispatch tests the filter itself (`IsAlive`, `Has<T>` per `[With<T>]`/`[Without<T>]`) and calls the
      method directly (`ZeroComponentHandlerCalledDirectly`, a filtered one skipped as `Skipped`); a `Mut<T>` built by a generated
      `[On<T>]` job (`new Mut<T>(ref value, entity)`; the job's `WriteMask` includes the `Mut<T>` bits, which are also its
      `SelfStamped`, so a move carries the ref and `RunJob` leaves the stamping to `Mut`) stamps only on `Write()` and on a
      `SetIfChanged` that changed the value (`GeneratedJobMutStampsOnlyChanges`); a generated job holding a `ref`
      singleton is a `ref struct` and the write reaches the world (`RefSingletonWriteReachesWorld`); `World.Save` and
      `SaveEntitiesWith` throw inside a pass, so records are taken after `world.Update` (`SaveEntitiesOutsidePassOnly`); a `ChangedAny` filter built from
      balanced calls of 2 to 9 types each, as the generated tracked-type list builds it (a single type as
      `ChangedAny<T, T>`, never `Changed<T>`, which a filter ANDs), selects a row written in any of them
      (`BalancedChangedAnyChunks`), and a package's one-type set composed with the user's set still selects a row
      written only in the user's types (`SingleTrackedTypeComposesWithPackageSets`, `RemovedAny` alike); `root.Spawn` through `Instantiate` with given components
      refuses a taken `IUniqueIndexed<T>` key before creating anything (`SpawnOfTakenUniqueKeyChangesNothing`); a read through
      EasyEcs's generated accessor of an `ITracked` type is `ref readonly` and stamps nothing
      (`GeneratedAccessorOfTrackedTypeIsReadOnly`); a `[NotSaved]` buffer written every tick tags no root `Unsaved`
      (`NotSavedBufferWriteDoesNotTagUnsaved`); adding a
      tracked component stamps a write and removing it records a removal (documented for `ITracked`), for tags
      declaring it too (`StructuralChangeOfTrackedTagDetected`: a persist-set tag, and `[Removed<T>]` on a non-persisted tag, `Deactivating`; if it fails, the Topics' cancelled-deactivation resubscribe falls back to the next renewal, and `Set` and `Remove` of a
      persist-set tag are an analyzer error only on an entity the analyzer sees is persistent, and elsewhere the runtime
      finds the change by a sliced pass over persistent entities each checkpoint interval, comparing their persist-set
      tags with a mask kept per persistent entity at its last write); no framework or generated system declares a
      member named `RunCount` (`EcsSystem.RunCount`, CS0108; `NoSystemMemberHidesRunCount`); a write through `Mut<T>`
      after a structural change of its entity in the same job is kept and stamped (`MutWriteAfterStructuralChangeKept`);
      the generated code, the framework and the samples build with EasyEcs 4.7's analyzer rules EECS037 to EECS052 as
      errors: components given to `CreateEntity`/`Instantiate` instead of a `Set` right after (EECS052), read-only
      passes over tracked components through `world.Read` (EECS046), no `ref` kept across a structural change (EECS047)
      (`GeneratedCodeCleanUnderEasyEcs47Analyzers`); the handover's `SaveWithout`/`LoadKeeping` keep the new process's
      singletons (`HandoverKeepsNewProcessSingletons`); a subtree collected with `Descendants(root).AddTo` is destroyed by
      one `DestroyEntities(ids)` (`SubtreeDestroyedInOneCall`); `AddSingleton` throws on a present singleton, so the
      framework uses `GetOrAddX`/`SetX` wherever a load may have brought one back (`NoAddSingletonAfterLoad`); the
      inspector's and the admin plane's reads go through `world.Read` and stamp nothing (`InspectorReadStampsNothing`); the
      generated `SetXIfChanged` of an indexed tracked component adds it when the entity lacks it and notifies the index
      (`SetIfChangedAddsMissingIndexedComponent`; EasyEcs 4.7's XML documents it: "as Set does: added if missing,
      stamped, indexed"); `world.TrimExcess` runs from the world loop between `world.Update` calls, never inside a pass
      or a system (its XML: "owner thread only, not during a pass") (`TrimExcessBetweenUpdatesOnly`); every framework
      reactor whose filter is only `[Removed<T>]`, `[ChangedAny]` or `[RemovedAny]` takes a component parameter or a
      `[With<…>]`, since EECS003 accepts a zero-component `[ForEach]` only with `[With<T>]` or `[Changed<T>]`
      (`RemovedReactorsTakeAComponent`).
      EasyEcs requests (draft §14 item 8; proposed, not yet forwarded, none in 4.7; #27 and (a) withdrawn, since
      persisted types declare `ITracked` in source): (b) a public
      read of a prefab's components (until then `Registration` instantiates each prefab once into a scratch world at
      construction and reads the instance's `Entity.Describe()` types; `PrefabCompositionReadWithoutRequestB`), (c) adding a system to a group without an attribute (until then the
      generator emits a closed subclass per actor type of each generic framework system), (d) a "job-shaped" attribute
      so EasyEcs's analyzer checks `[On<T>]` methods itself (until then DAS026 repeats its rules), (e) EECS003 accepting
      `[Removed<T>]`, `[ChangedAny]` and `[RemovedAny]` as the filter of a zero-component `[ForEach]`, as it accepts
      `[Changed<T>]` (until then those reactors take a component, above). Still open after checking the 4.7.0 package:
      `Prefab` exposes only `Has<T>`/`Read<T>`/`Id`, `AddSystem<T>(bool changesFromNow)` has no group, and the
      generator has no job-shaped attribute.
    - [ ] W2 Packages and attributes (ADR-086, ADR-087): Abstractions merged into Core, Hosting into `DaServer`,
      Simulation into Testing, WireTool shipped inside Core beside the generator and run out of process (`dotnet exec`
      after the build, `UpdateWireManifest=true`); `DaServer:Shards` and the AppHost's shard
      parameter replaced by sizing from the node's CPUs (`DaServer:Worlds` overrides); `UseSystemd()` and
      `UseWindowsService()` declared in `Program.cs` (launchd configured by its plist, drafts/deployment.md section 0);
      `IFromClient`/`IToClient`/`[To]` (on parameters)/`IServer`/`[Once]`/`[Latest]` in Core (both are written on
      `Game.Protocol`'s client messages); `IActor<T>`,
      `[Handles<K>]`, `[On<T>]`, `IMessageTo<K>`, `IRequestTo<K,R>` with the `IRequest` marker, `[From<K>]`, `IEvent`,
      `IClaims`, `IConfigTable<T>`, `IWorldSingleton<T>`, `[Persist]` (on any registered prefab), `[Prefab<K>]`,
      `[JoinOnConnect<T>]`, `[Hosts<K>]`, `[ActivatedBy<T>]`, `[TickRate]`, `[EverySeconds(n)]`, `[Stateless]`, `[NotSaved]`,
      `[ActorOptions]` (`IdleTimeout`, `DetachGrace`, `MaxPending`, `MaxMail`, `MaxMailBytes`, `MaxWaitingMail`,
      `PresencesPerClient`, `Unacked`, `OnceWindow`, `OnceInFlight`, `OnceQueued`; no error setting: a persisted root reloads, others continue),
      `[MaxReplySize(n)]` (Core, so a Protocol request can carry it), `Pending<T>`,
      `Sender`, `Time.Utc` (from the world's `TimeProvider`) in the runtime; node configuration bound by the
      configuration binding source generator (enabled in the library, a `NodeSettings` POCO, no reflection binding).
      Targets (ADR-086): code both sides load (Core, Client, the templates' `Game.Protocol`/`Game.Shared`) builds for
      `net10.0` and netstandard2.1 (today netstandard2.1 and net11.0; `#if NET` paths now compile against .NET 10, so
      anything .NET 11-only moves to the server core), `DaServer.Replication.Client` for `net10.0` only (W7a), the server
      core (`DaServer`, Persistence, Testing, Aspire) for `net11.0` only; a Godot 4 .NET game (`net10.0`) loads the
      .NET build with its fast paths. When this lands, CLAUDE.md's layout section (which today lists Abstractions, Core
      and Client as "netstandard2.1, net11.0") changes with it.
      Tests: `PackagesDependOnlyDownwards` (rewritten for six packages and the optional ones), `WorldCountFollowsCpuRequest`,
      `ClientPackagesLoadOnNet10` (a net10.0 console references Core and Client and runs a connect, the CI's stand-in for
      a Godot game),
      `WireToolRunsOutOfProcess` (the manifest written by `dotnet exec`, no task assembly loaded into MSBuild).
    - [ ] W3 Generator and analyzers: explicit `Key`, typed sends (asks with an
      optional `deadline`), typed `Replied` accessors (C# 14 extension blocks), `[On<T>]` jobs run through EasyEcs's
      `entity.Run` (only the job struct and the dispatch table generated; the analyzer reports EasyEcs's EECS ids for
      what they cover; a handler without component parameters called directly after the dispatch's filter test; `INoStructuralChanges` inferred from how `Entity` and `World` are used; `Mut<T>` parameters),
      `Registration.Create()` as a static extension member in the server project's root namespace reading `K.RootPrefab`
      and the persistent prefabs (the persist set, computed at run time; the tracked-type list and the
      `ChangedAny`/`RemovedAny` filters emitted from the source's `ITracked` declarations, and `Registration`
      throwing when a type on a `[Persist]` prefab lacks it; each referenced DaServer package's
      `PackageHooks.Register(Registration)` called in the chain, emitted even when empty: `PackageHookEmittedWhenEmpty`,
      `PackageBufferNotInServerTrackedList`), configuration tables and
      world singletons, `wire.manifest` (one section per project; `table` with the table type's format hash, `prefab`
      with its id and composition hash (earlier compositions kept), and `record` lines for the whole persist set,
      `latest` on `[Latest]` messages; user projects are Protocol and Server), `INoStructuralChanges` for jobs that only call
      `SetGroupRate`/`SetGroups`, the draft's §13 checks and warnings. Today's diagnostic ids under the new model (an id
      keeps its meaning or is retired; a retired id is never issued again, and every other §13 check takes a new id
      from DAS023 on):
      - Kept, same meaning: DAS003 (a type that cannot cross the wire), DAS004 (type id collision), DAS009 (a
        synchronous wait on a world thread), DAS015 (work started off the world thread), DAS017 (a layout change the
        manifest does not tolerate), DAS018 (manifest out of date).
      - Kept, applied to the new API: DAS001 (an invalid handler: now an `[On<T>]` method's shape), DAS002 (an invalid
        message type: a non-generic `[NinoType]` struct or `[NinoPooled]` class implementing one marker, `IFromClient`,
        `IFromClient<R>`, `IToClient`, `IMessageTo<K>`, `IRequestTo<K,R>` or `ITopicMessage`), DAS006 (a message used against its direction:
        `[From<K>]` and the Protocol markers instead of `[FromClient]`/`[ToClient]`; the `DaServer.Topics` package's
        `Subscribe`, `Unsubscribe`, `Publish` and `NotSubscribed` declare no `[From<…>]`, since any actor type sends them, and are exempt), DAS007 (fields in `[Handles<K>]`
        classes and systems), DAS014 (an invalid persisted component: the persist set's types), DAS019 (a collection
        held directly by a client message; an error, as §13 lists it).
      - New: DAS023 (`Subscribe` on a `[Stateless]` actor type, on `Connection` or on an actor type whose root is not saved:
        they claim no epoch and their subscriptions cannot be stored), DAS024 (a client `[To]` message to an actor type with no `[ActivatedBy]` at all: a warning,
        since no message can create its keys), DAS025 (`IActor<T>`, or an `IUniqueIndexed<T>`/`IIndexed<T>` keyed
        by itself, not `partial`; with a code fix), DAS026 (an `[On<T>]` method breaks an EasyEcs `[ForEach]` rule; the
        message names the EECS rule, EECS002/011/012/013/015 and EasyEcs 4.7's EECS038/040/041/047/052, and links it), DAS027 (a user component type without `ITracked`, or a `DynamicBuffer<T>`
        whose element type is declared outside the server project and the DaServer packages, on a `[Persist]` prefab the analyzer can see, built with a literal `SetX` chain in
        the same compilation; elsewhere `Registration` throws at start), DAS028 (a `Set`/`Remove` of a persist-set tag on
        an entity the analyzer sees is persistent), DAS029 (a tracked component type in a referenced assembly that is not a DaServer package: it reaches no tracked
        table), DAS030 (`[Latest]` on a request, a push or a topic message), DAS031 (an unsaved root used where
        a saved one is needed: `Commit`, `root.Delete()`, `root.IssueClientToken` or `AskUntilAnswered` on an actor type whose root is not
        saved or on an entity the analyzer sees is not persistent, or such a type handling or sending a `[Once]` request,
        `[From<K>]` whose `K`'s root is not saved included; every unsaved-root case is DAS031 only), DAS032
        (every `[Once]` shape and sending rule: on a type that is not a request, a reply that is not a fixed-size value
        of at most 32 bytes, a handler taking `ReplyTo<T>`, a `[Once]` server request sent by `Ask` or from `ICluster` or a
        `[Stateless]` type, or
        `AskUntilAnswered` of a request that is not `[Once]`), DAS033 (`Send`, `Ask`, `AskUntilAnswered`, `Push`, `Schedule` or
        `Remind` on a path where `root.Delete()` returned `Ok`: an error where the analyzer sees the path, a hint where it
        cannot tell; the `Outstanding` branch is not reported), DAS034 (a warning: `AskUntilAnswered` inside a loop of a
        `Replied` or `[Once]` handler, whose asks pile up queued behind a stuck downstream). Ids are assigned here for every new §13 check as W3
        implements it, warnings and hints included. Generated partials (`Key`) repeat `readonly` and `record struct` but not the positional
        parameter list.
      - Retired: DAS005 and DAS011 (already unused), DAS008 (sessions and groups as ask targets are gone; typed sends
        make a wrong receiver a compile error), DAS010 (no shared-logic project: user projects are Protocol and
        Server), DAS012 ("a system asks": systems may `Ask`), DAS013 (components live in the Server project; no logic
        reload to outlive), DAS016 (`IPlaced` is gone), DAS020 (`DisconnectSession` is gone), DAS021 (`[Filter]` is
        replaced by `[On<Incoming>]`), DAS022 (a `ref` used after a structural change: DAS026 reports it as EasyEcs 4.7's
        EECS047).
      Tests, one
      generator case per diagnostic, among them: `MessageWithoutNinoTypeReported`, `LatestOnRequestIsDAS030`, `CommitOnUnsavedRootIsDAS031`,
      `IssueClientTokenOnUnsavedRootReported`, `IssueClientTokenCommitsImplicitly`,
      `ClaimsParameterInSystemReported`, `PrefabNotInternalReported`,
      `PersistOnUnregisteredPrefabReported`, `IndexedChildKeyFirstFieldNotActorReported`,
      `PendingOfNonEventOrRequestReported`, `PersistentPrefabOnUnsavedActorReported`, `RefAfterStructuralChangeIsDAS026` (EECS047), `OnceOnMessageIsDAS032`,
      `LargeOnceReplyIsDAS032`, `OnceWithReplyToIsDAS032`, `PlainAskOfOnceIsDAS032`, `AskUntilAnsweredOfPlainRequestIsDAS032`,
      `OnceFromClusterIsDAS032`, `OnceFromStatelessIsDAS032`, `OnceFromUnsavedFromKIsDAS031`, `OnceHandledOnUnsavedRootIsDAS031`,
      `AskUntilAnsweredOnVisiblyUnsavedEntityIsDAS031`, `OnceBuffersInRuntimePersistSet` (the framework's `Unacked`
      in the runtime's persist set, `OnceSenders` as its own rows, neither in the manifest's `record` lines), `OnceFlaggedInManifest`,
      `AskStateNotInPersistSet`, `TagSetOnVisiblyPersistentEntityIsDAS028`, `RegistrationRegistersCanCreateTables`, `ManifestRecordsActivatedBy`, `TagSetElsewhereNotReported`,
      `TrackedTypeInForeignReferenceIsDAS029`, `SubscribeFromUnsavedRootIsDAS023`, `PackageTrackedSetsComposeInRegistration`,
      `ReminderClosedPerRemindT` (one persistent prefab per (actor type, T), in the persist set and the manifest's
      `prefab` lines), `RegistrationIncludesRemovedAnyFilter`, `ActivatedByRegistersClaimsAndTable` (the three-type and the
      two-type overloads), `ManifestRecordsBufferElements` (`Slot`), `NotSavedBufferNotInTrackedTable`, `SubscribingTypeRootCarriesSubscriptionBuffer`, `InMessageJobIsPlainStruct`, `SystemInKWithChildComponentsRejected`,
      `CanCreateWithoutActivatedByReported`, `ScheduleOrRemindWithoutHandlerReported`, `RemindOnUnsavedRootReported`,
      `PrefabCompositionHistoryKeptInManifest`, `GroupRateOnlyJobGetsNoStructuralChanges`, `ScheduleInSystemIsStructuralChange`, `ScheduleInStatelessSystemReported`, `RawRecordLayoutChangeReported`, `TrackedRefNotAlwaysWrittenHinted`, `SendAfterDeleteIsDAS033`, `SendAfterDeleteUnseenPathIsDAS033Hint`, `AskInLoopIsDAS034Warning`, `EntityMovedAcrossRootsFaultsItsRecord`, `RemindOnOutstandingBranchNotHinted`,
      `ConfigTableTakenByRefReported`, `SingletonWithoutInterfaceReported`, `UnfilteredForEachWithoutTickRateReported`,
      `SetGroupRateWithoutTickRateReported`, `UtcNowInHandlerWarned`, `RootCopyCarriesUnackedWhenOnceHandled`,
      `RegistrationCallableWithTwoServerProjects`, `TopicPackageMessagesExemptFromDirection`,
      `ClientToWithoutActivatedByWarned` (DAS024), `MissingPartialIsDAS025`, `OnMethodBreakingEcsRuleIsDAS026`,
      `PersistTypeWithoutTrackedIsDAS027` (a component without `ITracked` and a buffer whose element is declared elsewhere, on a visible prefab; elsewhere
      `Registration` throws at start, buffers included),
      `RouteMissingForKeyFieldsReported` (not for `[To]` messages whose key the message carries),
      `ActivatedByUnhandledMessageReported`, `RouteOnUnhandledMessageReported`, `JoinClientStatusUnusedReported`,
      `CanCreateTakesClaimsMessageAndAtMostOneTable`, `ServerMessageCanCreateTakesNoClaims`,
      `UntargetedClientMessageOnMultiPresenceTypeReported`, `ManifestRecordsLatest`,
      `EveryOrFilterRequiredWithoutTickRate`; `UnrelatedEditsHitTheCache` over the new stages.
    - [ ] W4 Runtime core: shards (one world each, `MaxDegreeOfParallelism` per world), the shard loop and the ordered
      phase per actor type (`Ordered<K>`, holding the type's tick number; `Time` written at the start of each actor type's pass), the due heap (4-ary, lazy deletion), the chain stack, the call index,
      `Outbox` serializing on append into per-destination frames, `Current`, the framework groups (Ordering, Settling
      with `Restarting` and `Resumed` removal, Cleanup), a full link refusing at once (never an answer or a
      presence notice: room reserved for them), one ordered stream per link for everything a user observes and an
      internal lane for heartbeats, membership, acks, `Fence`, `Wake`, `HandoffQuery`, large-message completion and `[Once]`'s acknowledgements, `AckQuery` and window updates (ADR-096, ADR-099), `[Latest]`
      replacing a sender's unhandled older message of the type in the target's mailbox (in place when it is the sender's
      last item to that target, else the old one dropped and the new one appended; a sequence per (sender, target, type),
      the client's sender its `ClientId`, so an older one arriving late is dropped), per-actor mailbox bounds
      (`MaxMail`, `MaxMailBytes`) behind the dispatch queue, answer room reserved in bytes when an ask is made (refused
      locally past half; `MaxPending` answers `Busy`), one pair per (sender actor, gateway node), links that stop
      admitting but never disconnect when full, the window at least `MaxMessageSize` plus the reservations,
      `LargeInFlight` and `ReassemblyBytes` with admission on the first fragment, partial buffers kept across a reconnect
      and dropped on a generation change, pushes dropped at a full link marking `MissedPushes`, rate-limited group frames
      flushed early at `GroupFrameMaxBytes`, `RetryAfter` waited with full jitter over `[RetryAfter, 2 × RetryAfter]`,
      large messages in `DaServer:Links:FragmentSize` fragments interleaved round robin per pair and handed to the
      socket as gather writes, acks riding on data frames, the per-actor item budget per
      batch, `PeerHealth`, request budgets (nested asks, `ReplyTo` deadlines), the retry budget, `ICluster.Ask` with a
      deadline or `CancellationToken`, `root.Spawn` through `world.Instantiate` with components,
      find-or-create through the unique key index (`TryGet`, then `root.Spawn`), `Pending<T>` from `Schedule` and `Ask`
      with one `IsPending`/`Cancel` group, the fixed step with a catch-up cap (EasyEcs's hosting pattern), `TrimExcess`
      by a slow Cleanup system and an admin command, a throw in an actor without a record continuing, `Faulted`,
      `Status.NotExecuted`/`Transient`, `[Stateless]` actor types (any node, the key picks the local actor, no claim, no
      record), world singletons created per world by `Create()` and configuration tables read from
      `DaServer:Tables:{name}` (the cluster-wide version is N9), `TestActor<K>` without presences (one world, the root,
      DaServer's manual clock of ADR-071, `SetTable`), `TestCluster` rebuilt on `Registration` (its nodes take the generated
      object; no node-wide `TickRate` or `Shards` option: a node's worlds as in production, `Worlds` to override) with
      `TestCluster.Start(Registration.Create(), c => c.Nodes<TServer>(n).ManualClock())`, `Connect(claims)` and `Step(time)`
      stepping the manual clock so each actor type ticks at its own `[TickRate]`; `[EverySeconds(n)]` systems and the
      world's quiet pass (`DaServer:QuietPass`); `RootState` (`Writing`, `WriteNow`, write generation as fields);
      `ActivationSlots` and `PeerHealth` as per-world shares; the due heap compacted in place; systems taking the
      actor key `in K` bound to the item's root. Tests: `NestedAskInheritsRemainingBudget`,
      `RetryBudgetCapsForwardRetries`, `HotActorDoesNotStarveShardNeighbors`, `SlowPeerFailsFastAfterLimit`,
      `ClusterAskHonoursCancellationBudget`, `ThrowingHandlerAnswersFaulted`, `AnswerThenMessageArriveInSendOrder`,
      `PresenceNoticeOrderedWithMessages`, `AnswerAdmittedWhenQueueFull`, `LatestReplacesUnhandledOlder`,
      `LatestNeverOvertakesOtherMessages`, `HeartbeatNotBehindLargeMessage`, `SmallAnswerInterleavesWithFragments`,
      `FragmentsReassembleInPairOrder`, `AckRidesOnData`, `LatestUnderBacklogStillDelivered` (100,000 senders at 30 Hz
      behind a backlog: each sender's message is handled), `StaleLatestAfterMigrationDropped` (an older one forwarded by a
      forwarding marker after a newer one), `LatestSequenceContinuesAcrossReconnect`, `StatelessLatestSendersIndependent`, `HotActorRefusesOnlyItsOwnMail` (its shard's
      neighbours keep receiving), `AskReservationRefusesLocally` (65,536 asks from one actor), `MaxPendingAnswersBusy`,
      `ReservationReleasedAtDeadline`, `OversizeReplyTravelsUnreserved`, `FenceHoldsBehindFragmentedGroupFrame`,
      `PushOrderToClientKeptAcrossFragments`, `FullLinkNeverDisconnects`, `ReassemblyBudgetBounded`,
      `LargeMessageRefusedOnFirstFragment`, `PartialMessageKeptAcrossReconnect`, `PartialMessageDroppedOnNewGeneration`,
      `WindowAtLeastMaxMessageSize` (checked at start), `DroppedPushAtLinkMarksMissedPushes`,
      `RatedGroupFrameFlushesAtByteCap`, `RetryAfterJitteredOverWindow`,
      `CatchUpCapSkipsAndCountsTicks`, `TrimExcessAfterPeakShrinksWorld`, `ReSentJoinFindsSameEntityByIndex`,
      `PendingHandleCoversScheduleAndAsk`, `ThrowInActorWithoutRecordContinues`, `StatelessTypeAnswersOnAnyNodeWithoutClaim`, `TestActorTicksWithManualClock`,
      `TwoTickRatesInOneWorldSeeOwnTime`, `TestClusterStepTicksEachTypeAtItsRate`, `EveryRunsInIdleWorld`,
      `ReplyIsNotStructural`, `ActivationSharesPerWorld`, `DueHeapCompacts`, `SystemInKMatchesRoots`,
      `RegistrationRejectsComponentMissingFromPrefab`, `SystemsSeePassTimeNotLastItemTime`, `UntickedPassSeesZeroDeltaTime`, `ScheduleFromSystemBecomesDue` (`Schedule(ev)` from a system is a `Due` child at
      `Time.Elapsed`, run in the next ordered phase, kept by a handover taken in between). BDN (`LinkBenchmark`, against
      the pre-W baseline): system calls per message at 10,000 connections under a broadcast and a request load, and the
      latency of a small answer behind a 4 MB message on one link; `HotActorAdmission` (one actor flooded by 100,000
      senders, its neighbours' latency and 0 B per refusal).
    - [ ] W5 Presences, the gateway (`Connection` on `[Hosts<Connection>]`, keyed by a `ConnectionId` made at accept and
      placed on a world by that key's hash like every actor, every frame to a connection routed by the `ConnectionId`,
      `ClientOf(ClientId)` a non-unique index that `cluster.Disconnect` asks each world for, `Incoming` as a header view,
      forwarding as a batch pass), client tokens (`cluster.IssueClientToken`, `RefreshClientToken`, `ClientId`; one login is one
      `ClientId`), `[To]` routing (only to keys with a record unless `[ActivatedBy]`; key fields from the claims
      through the actor type's `Route(in TClaims, in TMessage)`), `[JoinOnConnect]` (client messages without `[To]`
      bound to the root), `PresencesPerClient` (the oldest left past the limit), `root.JoinClient(out presence, prefab?)` (a `Status`) and
      `root.TryGetClient`, `Join()` returning `Status`, SDK re-join, `Retire` with presences
      (the new connection's stamps take over, the `HandedOff` fence, `Joined(MissedPushes)`, the retire flag in the
      handshake passed on by `[JoinOnConnect]` joins; one login's connections settled by stamps at the targets, on one node as across nodes), `Detached` with
      `DetachGrace`, `cluster.Disconnect` and `connection.Disconnect` (no grace), `TakeOver` and the SDK's terminal
      `Superseded` (a newer sequence, or an equal one under another `ClientId`), one login's connections ordered by the
      SDK's attempt counter (from the Unix milliseconds at SDK start, one per attempt), `AlreadyConnected` (`Takeover.Reject`
      refuses only another `ClientId` on a `Joined` presence), client call ids
      across reconnects from a random 63-bit start (`ConnectionLost`), `Busy` retry-after in the SDK and
      `[JoinOnConnect]` (`Unavailable`), the admission bucket, load-based connection rebalancing (ADR-089; the average over gateways Up and ready),
      `ClientGroup` subscriptions (full sets, keys scoped by `root.Group`; a push goes only to the gateways with
      members, which replaces ADR-029; `root.Everyone`, the reserved actor-wide group that `Join()` subscribes and
      `SetGroups` keeps, replaces a separate broadcast path), the send rate per group as data
      (`root.SetGroupRate(key, everyTicks: n)` into the `GroupRate` buffer the ticking root's copy carries, the latest wins; the
      group's frame handed over on its due tick and before any membership change of the group, a counter in its
      buffer, 0 B; `Join()` adds an empty `Subscribed` buffer, so neither call is a structural change),
      `TestActor<K>.Connect` and `PresenceOf` (`Connect`/`Deliver` confirm routes as a gateway would), at most
      `DaServer:Clients:MaxInFlight` requests per connection, a gateway's dropped or conflated push sent back as
      `Missed(PresenceId)`, slow clients conflated (the newest frame per group) and disconnected only after
      `SlowClientGrace`, `MissedPushes` resyncs limited to `DaServer:ResyncsPerTick` per actor and tick, join retry budgets
      by time (`DaServer:Clients:JoinRetryFor`), `Unavailable` carrying the time left to re-placement. Tests: `GatewayCrashPresenceSurvivesGrace`, `NewestWinsClosesOldConnectionAcrossGateways`,
      `SupersededClientDoesNotReconnect`, `EqualSequenceOtherClientIdSuperseded`, `RefreshedTokenDoesNotOutrankNewerLogin`,
      `ReconnectThroughOlderGatewayTakesOver`, `StaleAttemptClosesQuietly`, `RestartedAppOutranksHalfOpenConnection`,
      `DisconnectLeavesWithoutGrace`, `BalancedGroupRetiresNobody`, `ScaleOutShedsOnlyExcess`,
      `JoinOnConnectOutageDoesNotAmplify`, `ClientRetryAfterReconnectKeepsCallId`, `SdkCallIdsStartRandom`,
      `ForeignCallIdCannotCollide`, `RetireKeepsPushOrderPerActor`, `ReconnectAfterLossGetsFullSnapshot`, `GatewayWritesOncePerBatch`,
      `LowRateGroupCoalescesUntilDue`, `GroupFrameFlushedBeforeMembershipChange`, `LatestGroupRateWins`,
      `BroadcastIsEveryoneGroup` (subscribed on `Join`, kept by `SetGroups`, rate-limited by `SetGroupRate`),
      `PushSkipsGatewaysWithoutMembers`,
      `RetireRacingConnectionLoss`, `UnknownClientMessageAtOldGateway`, `RetireToSameGatewayKeepsBothUntilRoutesMove`,
      `HalfOpenReconnectToSameGateway`, `RetireKeepsJoinOnConnectFence`, `JoiningGatewayLeftOutOfAverage`,
      `TakeOverReachesOldConnectionInAnotherWorld`, `DisconnectFindsConnectionsInEveryWorld`,
      `RejectRefusesOnlyOtherClientWhileJoined` (the same `ClientId` takes over by attempt, a `Detached` presence never
      rejects), `ClientToUnknownKeyIsNoActorAndWritesNothing`, `ActivatedByClientMessageCreatesKey`,
      `RouteFromClaimsIgnoresClientUser`, `ClientPurchaseRetrySameKeyAnsweredOnce`,
      `JoinOnConnectClientMessageBindsRoot`, `PresencesPerClientEvictsOldest`, `JoinClientFindsOrSpawns`,
      `TryGetClientFalseForNonMember`, `RetireFlushesRatedGroupBeforeHandedOff`, `PresenceNoticesNeverBusyNorDropped`, `SdkDropsPushAfterHandedOff`,
      `FenceTimeoutRejoinsWithMissedPushes`, `ConnectionInFlightCapAnswersBusy`, `GatewayDropSendsMissed`,
      `SlowClientConflatedNotDisconnected`, `SlowClientDisconnectedAfterGrace`, `ResyncBudgetSpreadsReconnectStorm` (a
      gateway's 100,000 clients reconnect: at most `ResyncsPerTick` full snapshots per map tick),
      `JoinRetryOutlastsReplacement` (a player node dies: `[JoinOnConnect]` keeps retrying past the ~30 s re-placement),
      `UnavailableCarriesTimeLeft`, `SendBoundAtLeastMaxMessageSize`.
    - [ ] W6 Persistence, activation, migration and hot restart (draft §6, §8, §10 and §11; architecture.md sections 4
      and 5; ADR-068, ADR-082, ADR-084, ADR-097, ADR-098, ADR-101 to ADR-105):
      - the store contract (ADR-102; architecture.md section 4): `IStore` (ordered byte keys with writer-chosen
        versions, `Get` and `Scan` with `Now`, conditional `Put`/`Delete`, `GuardedWrite`, ordered batches, `Confirm`,
        `Durability`, `SelfCheck`, `MaxValueBytes`, `Migrate`; `Get` and `Scan` reading headers only,
        `Scan` bounded by `ScanBytes`; unconditional puts) and its outcome model, the runtime acting on outcome
        classes only; the key layout (records, ask rows, the queued-ask index, `OnceSenders` rows under their root; the due
        and ask indexes as hints written ahead of their record, kept with it when a batch is halved, naming the record
        version they precede and cleaned compared on their versions only once the header has reached it:
        `DueEntryNeverLostToCleanup`); the activation stamp and the activation count in the record's version; `InMemoryStore` (`Strict` as a single node's membership; records and the Dashboard's keys only in development and `TestCluster`, refused for them in production with no opt-in: `InMemoryStoreRefusedForRecordsInProduction`); the durability classes (ADR-104): the
        effective class the lower of the declaration and the self-check, `DaServer:Store:AcceptDurability` and
        `DaServer:Dashboard:AcceptDurability` (membership always `Strict`, no opt-in), `daserver.store.durability_class`,
        the started class carried over a hot restart and `daserver.store.unsafe_exempt` on development and `TestCluster` nodes,
        below `Strict` fencing as the only guarantee (no loss detection; the class logged at start), an own-token older
        version in `NotApplied` or `Confirm` fenced, reloaded and counted in `daserver.store.regressions`, any other
        stored version (another token, the key absent) fenced, and an unsafe setting making the class `Unsafe`: a fresh
        start waits not ready before its membership join, a serving node or a hot restart's standby goes on under the
        `Local` rules (`NonStrictStoreRefusedWithoutOptIn`, `MembershipRequiresStrict`,
        `DashboardStoreBelowStrictRefusedWithoutOptIn`,
        `SelfCheckLowersClass`, `ProductionIsAnyNonDevelopmentEnvironment`, `OwnOlderVersionFencedAndCounted`, `RolledBackEpochNeverMatchesOtherOwner`,
        `ActivationStampOrdersUnsavedReactivation`); the conformance kit in
        `DaServer.Testing` (contract, per-key linearizability with a shipped checker, fault injection through a kit TCP
        proxy, failover per class, self-check), run against `InMemoryStore` and PostgreSQL in CI (layer 4 nightly);
      - the PostgreSQL provider (`DaServer.Persistence.PostgreSql`, `PostgreSqlStore`, `AddPostgreSqlStore`; PostgreSQL
        16 or later): the `daserver_kv`, `daserver_membership_kv`, `daserver_dashboard_kv` and `daserver_confirm` tables created only by the
        schema mode under a polled advisory lock, ending in a cleanly committed `COMMENT ON TABLE` (`SchemaJobRerunsIdempotently`,
        `SchemaQualifiesEveryObject`, `SchemaGrantsRuntimeRoleFromConnectionString`); `Get`, `Scan`, the guarded-write
        batch over `unnest` and `Confirm` by a marker write (architecture.md section 4's statements:
        `LargeRootWriteStaysAtomic`, `ExpiryNeverDeletesNewerVersion`, `NotAppliedRaceReread`); SQLSTATE to outcomes, a
        failed batch split between roots to the one `RowDeterministic` root (`PoisonRowBisectedRestLands`), a batch timed
        out three times halved (`TimedOutBatchHalved`, `HalvedBatchGrowsBack`); the commit handling of ADR-101 (`BEGIN`
        and data statements as one pipelined batch, `COMMIT` on its own with no client deadline and never cancelled:
        `ManyStatementWriteNotCancelled`, `TimedOutWriteNeverCancelled`, `BatchNeverCommitsWithoutCommit`,
        `HalfOpenWriteEndsAtScaledDeadline`, `UnansweredCommitLookedUp`, `AbandonedBackendTerminatedBeforeRetry`,
        `HalfOpenBeforeCommitReleasesLocks`, `DataSourcesRequireReadWrite`; a membership write's wait never cancelled
        nor its backend terminated, settled once that backend is gone by a `Confirm` on the lookup connection and the
        entry's `write_seq` (the conditioned-on value plus one; a join starts at 1), one pending membership item per
        process (a repeated `Confirm` or an `Unknown` heartbeat waits for the earlier backend to be gone:
        `RepeatedConfirmWaitsForEarlierBackend`), a process waiting before its first membership write while the role's
        waits in its database past `StatementTimeout` make no progress (a slow standby lets it through, a stalled one
        holds it; at most one stuck backend per live process plus those of processes that died while stuck):
        `MembershipWaitNeverTerminated`, `MembershipLeftoverBoundedPerRole`; a join retry stopping when its own
        generation's dead key exists or a higher generation of its name is recorded: `JoinRetryStopsWhenMarkedDead`; a handover starting only with no pending
        membership item, the wait counting against the arm: `HandoverStartsWithNoPendingMembershipItem`; the role's connections, across every
        database, against its limit (not exported when unlimited) exported and alerted: `MembershipConnectionsLowAlerted`); the roles step (roles named by cluster and schema, or `RolePrefix`, shortened past 52 bytes with a SHA-256 suffix: `RolesStepIdempotent`,
        `RoleNamesFitAndRepeatForLongClusterNames` (a 63-byte cluster name and a long multibyte prefix),
        `RoleNamesMatchBetweenAspireAndSql` (`DaServer.Aspire`'s C# names equal the SQL's),
        `RolesStepCreatesConfiguredSchema`, `RolesStepTakesOverExistingTables` (the schema, when not `public`, then the tables and the function), `RolesStepUpdatesPassword`,
        `ConnectionLimitsSizedFromAutoscalerMax`, `MembershipRoleConnectionsReserved`, `RolesSeeStandbySyncState` (runtime, membership and Dashboard roles through `daserver_replication()`, only the owner
        holding `pg_read_all_stats`; the function's `search_path` is fixed, so a caller's objects never run with the
        owner's rights, and `PUBLIC` cannot execute it), `RolePrefixUniquePerServer` (roles marked in the transaction that creates them, by `COMMENT ON ROLE`, with the cluster's membership store server (`system_identifier`), database and schema: one cluster's second store database converges, another cluster's membership database on this server or another, another schema or an unmarked role is refused, naming `RolePrefix`; after a membership move, roles re-marked as operations.md says converge)); `PublishRefusesStorelessRecords` (`aspire publish` fails when any node's records or Dashboard
        would use the in-memory store); `AspireRunModeIsDevelopment` (run mode sets `DOTNET_ENVIRONMENT=Development`
        unless the AppHost sets one; publish never does); the
        self-check declaring `Strict` only with a synchronous standby and `DaServer:Store:PostgreSql:SafeFailover`;
        in every class `fsync` or `full_page_writes` `off` making the class `Unsafe`, read on every new connection and
        every 10 s: a fresh start on it stays not ready before the membership join (`UnsafeAtStartStaysNotReady`), one
        finding it while serving, or a hot restart's standby, refuses nothing, goes on under the `Local` rules and raises
        `DaServerStoreUnsafe` (`UnsafeStoreDropsClassAndAlerts`, `UnsafeDuringHotRestartTakesOver`; development and `TestCluster` exempt with a warning:
        they start, export the class they found and never alert), `synchronous_commit` `off` or `local` declared `Local`
        (`UnsafeDurabilitySettingsDetected`); the BenchmarkDotNet gate
        `GenericTableCommitThroughput` (the generic table kept only within 10% of typed columns at 1 and 5 ms of injected
        latency, else typed columns behind the same primitives); the wire family's variants (Aurora, AlloyDB, Neon,
        PolarDB for PostgreSQL, CockroachDB, YugabyteDB) named supported only once the kit passes against each;
      - membership and fencing: the membership table (Orleans' protocol, ADR-103, architecture.md section 4) as one
        versioned document through `IStore` (a conditional put per change, one `Get` per read: `MembershipViewReadAtomic`;
        heartbeats as keys of their own, written only by their member; an `Unknown` write pending until its never-terminated backend is gone, then settled by a `Confirm` on the same session and the entry's `write_seq` (`MembershipWaitNeverTerminated`), the lease resting only on plain reads (`UnknownMembershipWriteNeverHoldsLease`); a new document's version from the store's `Now` in ms or `DaServer:Membership:InitialVersion` when higher (`NewMembershipDocumentStartsAboveStamps`); a claim reading a higher
        stored placement version re-reading the document, a stale view only forwarding (`StaleViewClaimRereadsAndForwards`);
        a version below one the node held, or a stored placement still above a fresh document, a store fault: logged,
        counted, `DaServerMembershipRegressed`, the node freezing and fencing without writing and exiting with code 75
        (`MembershipRegressionStopsTheNode`), the highest version held carried by a hot restart
        (`RegressionDetectedAcrossHotRestart`); the
        document re-read after a reconnect; `Dead` entries moved to keys of their own at re-placement and the document's size alerted
        (`MembershipDocumentBoundedUnderChurn`), a node whose entry left the document stopping, peers refusing a
        generation their view does not list (`ReplacedNodeAbsentFromDocumentStops`); one membership write in
        flight, heartbeats skipped while one is pending and its age alerted (`daserver.membership.write_pending_age`,
        `DaServerMembershipWritePending`: `MembershipWritePendingAlerted`; nothing steps down), its own store client with a connection kept
        for reads: `MembershipReadsAnswerWhileCommitWaits`) and the in-memory store (one node, `TestCluster`); joining (generation
        max(Unix ms · 2^16 + 16 random bits, recorded max + 1), older generations of the name written `Dead`, connectivity checked before
        `Active`), probes on a ring, indirect probes, suspicion votes and death declarations, the table refresh and
        snapshot push, a node reading itself `Dead` stopping, the isolation lease (from the read's issue, on the boot-time clock, checked by every shard before
        each ordered phase and Outbox hand-off; pushed snapshots never renew it), the freeze past it (no drain, resume on
        `Active`, `Isolated` probe answers, failed readiness, exit 75 after `IsolationExit` when a peer is not isolated,
        `daserver.membership.frozen`), the stall guard, dead members kept in the placement set until re-placement after
        `dead_seen_at + IsolationTimeout + DeathMargin` on the store's clock (`dead_seen_at` the `Now` of the coordinator's first read
        showing the death), suspicions stamped with the store's `Now`, `daserver.link.unreachable`, the coordinator derived from the table, `restart_until` as the
        restart permit, `WithMembershipStore`, a storeless node starting only with `DaServer:Membership:SingleNode`,
        `DaServer.Aspire` setting `SingleNode` for one node without a store and adding PostgreSQL to an AppHost with more
        than one; the fault-injecting per-node `IStore` in `TestCluster`; no seeds; the claim as a `Get` then a put
        compared on the stored version (the epoch per key, continued through a tombstone: `EpochContinuedThroughTombstone`;
        the owner's name and generation and the placement version in the header; the displaced owner from the `Get`),
        strict placement versions with `Fence` (bumped on every placement change, a migration's arrival compared on the
        source's last version and never lowering placement), a `NotApplied` claim decided again from the stored value, an
        `Unknown` one acting only once `Confirm` shows it and still notifying the displaced owner
        (`TimedOutClaimConfirmedByRerun`), `StoreDegraded`, `ActivationSlots` (`ActivationSlotsConfigurable`), `ActivationFailed`;
      - activation: placeholder roots with their mail bounded by `MaxWaitingMail`, `[ActivatedBy<T>]` (a key's first
        activation only; `Wake` and the `[JoinOnConnect]` join always activate; mail that may not create a key claims requiring
        a record), the handoff before a claim (`Arriving`, `HandoffQuery`) for keys of a slot just received, `Pending` with `MaxPending`, the idle scan as
        `[ForEach(Slices = N)]`, deactivation with the `Deactivating` tag;
      - records: persistence per entity kind (`FromPrefab`, `Persisted(Root)`, the persist set and its change system,
        `Unsaved`), every load completing entities from their prefab, the tick-end batch of guarded writes (one per root,
        the record compared on the root's last landed version), `WriteNow` and `DaServer:CheckpointInterval` (staggered by
        the key's hash, oldest unsaved first, a quarter of each round kept for checkpoints), `DaServer:Store:WritesInFlight`,
        batches capped by `Store:BatchBytes` of records and records by `Store:MaxRecordBytes` (or the provider's
        `MaxValueBytes`), keys sorted, outcomes per root by class: `StoreLevel` retried with the same bytes and a backoff,
        `Unknown` and `AlreadyLanded` released only once `Confirm` shows the write (another token fences, a lower
        generation resends write g): `UnknownReleasesOnlyAfterConfirm`, `RetriedWriteConfirmedBeforeRelease`,
        `ConfirmFindsWriteAbsentResends`, and nothing above g released: `ConfirmNeverReleasesLaterItems`;
        `RowDeterministic` ruled unwritable and then retried alone outside the slots (solo retries at most `WritesInFlight`
        per world: `SoloRetriesCappedPerWorld`), more than a quarter of a batch of at least 8 roots failing that way treated
        as a store failure (`StoreLevelErrorsNeverRuleUnwritable`, `UnwritableRootRetriedAlone`,
        `SingleRootWriteRuledUnwritable`, `UnwritableAloneRetryStaysUnwritable`), a world pausing its intake past
        `Store:BacklogPause`, `daserver.store.oldest_unsaved_age`, a root whose state does not serialize or whose row the
        store refuses handled as `RecordUnwritable`;
      - the reload after a throw in a persisted actor (a type without a record continues);
      - `Commit` (ADR-097): `WriteNow` set by `root.Commit()`, the item's output held per root with its write generation
        and released in order when the write lands (dropped on a fence), a barrier per (root, destination actor), nothing
        written when nothing is unwritten, a group push held until release and then appended to its group's frame,
        a reply past its deadline dropped on release, held `[Latest]` mail replaced, held output bounded per root and
        world (`Busy` beyond), mail parked on a root once its final write before a migration is issued;
        a write timed out three times halved; `root.IssueClientToken` with `IssueSeq`, committing implicitly;
        `TestActor<K>` with `FailNextWrite()`, `Crash()` and `Answer<TReq>`;
      - `[Once]` (ADR-099): `RootState.OnceNext` a sequence pair `(seed, counter)` compared seed first, the seed set by
        the claim creating the incarnation, the larger of its `Now` in milliseconds and the tombstone's seed + 1 (no
        shift, `SequenceSeedNeverOverflows`), an alert with 2^24 counters left and no
        `[Once]` past the end (`CounterExhaustionStopsOnce`), taken and committed by `AskUntilAnswered`; each attempt
        with its own identity (a call id per attempt for actors, an attempt number in the client envelope:
        `ClientLateFaultOfEarlierAttemptIgnored`) and a UTC sent-at stamped by the sender's node or the gateway
        (`ClientSentAtStampedByGateway`), attempts older than `MaxAskTimeout + ClockSkew` dropped and alerted
        (`DaServerOnceTooOld`; also `DaServerOnceStaleAsking` and `DaServerOnceCounterLow`, target-only rules in
        `observability/prometheus-rules.yaml`); the receiver's `OnceSenders` as keys of their own under the receiver (ADR-105: written only by
        the receiver, as dependents of its guarded write, however many: `LargeRootWriteStaysAtomic`, two changes per new
        sender; a row written back after the expiry deleted it costs storage only: `ForgottenRowWrittenBackExpiresAgain`),
        never in migration or handover bytes: the move's and the freeze's last write carries the unwritten row changes
        (`HandoverKeepsUnwrittenRowChanges`) and the target reads them after its claim lands (`OnceRowsReadAfterClaim`,
        `OnceRowsNotInMigrationBytes`), bounded by `Once:SendersPerReceiver`; per sender the mark, and `orphan_after` or a
        client session's `delete_after`; an actor sender's rows forgotten only after its record is deleted (`root.Delete()`
        writing a tombstone with `forgetUntil = Now + MaxAskTimeout + 3 × ClockSkew` on its own key: `ForgetOnSenderDestroy`,
        `TombstoneCarriesForget`; the expiry deleting the receivers' rows once the sender's key shows it forgotten, an
        in-flight duplicate before then still answered; a sender re-created under the same key raises the row to its
        higher seed: `RecreatedSenderRowKept`) or the receiver's own record is (its rows as a receiver marked
        `orphan_after` in its tombstone's write, kept so a re-activation reads the old
        marks: `DeletedReceiverRefusesLateAttempt`; a change to the row clears its in-memory mark and the idle scan keeps rows
        with entries: `RecreatedReceiverKeepsEntriesPastOrphanAfter`), its `Unacked` sized by how long an entry lives (BenchmarkDotNet `OnceReceiverWriteBytes`, the bytes a receiver's write adds per entry), a receiver deleted only once its `Unacked` is empty, it owns no
        ask row and `MaxAskTimeout + 2 × ClockSkew` has passed since the later of the `Deleting` write and its last
        `[Once]` admission (`DeleteWaitsForUnackedEmpty`; `root.Delete()` answers `Outstanding` while the root owns
        any ask row, in any state: `DeleteRefusedWhileAsking`; deletion a one-way passage: no system, reminder or
        `[ForEach]` on a Deleting root, new requests `Busy`, one-way mail dropped, only `[Once]` repeats answered:
        `DeletingRootRunsNothing`, the tag on the whole subtree: `DeletingTagCoversSubtree`; asking or sending after `Delete()` in the same item reloads it:
        `SendAfterDeleteReloadsItem`, analyzer error DAS033 (`SendAfterDeleteIsDAS033`); its framework `WakeAt` resumes it after a
        crash or idle unload: `DeletingRecordResumedAfterCrash`; a Deleting root not migrated, continued after a freeze;
        a retried delete that finds its tombstone or a newer activation's claim counts as done: `RetriedDeleteCountsAsDone`); a
        forget check applies to a row only for the seed it holds (`ForgetCheckIgnoresRecreatedSender`); a receiver with
        entries woken to probe by a framework `WakeAt` (`DeactivatedReceiverWakesToProbe`); in-memory rows of an
        always-active receiver dropped after their sender's `forgetUntil`, learned from an `AckQuery` answer or the idle
        scan's bounded walk (`Once:ForgetCheckBatch` rows per `ProbeAfter`): `IdleScanForgetsTombstonedSender`)
        and `Unacked` in the record (sender, sequence, stored reply of at most 32 bytes, write generation); the check before
        the handler (too old, stale at or below `n`, a repeat answered with the stored reply after its write,
        `WindowFull` past `OnceWindow`, `Unacked` or the rows (with `RetryAfter`), `NoActor` for a missing
        receiver the request may not activate, an unstored `Busy` while the receiver is being deleted, else
        run once with an implicit commit); cumulative acknowledgements
        taken from the sender's last written record on every mail of the pair, and one on the internal lane after the
        answer's item is written; window updates pushed in order to refused senders from a bounded FIFO (`Once:RefusedQueue`), sent when entries clear
        or, for the row limit, when rows are deleted (`RowLimitWindowUpdateOnDelete`), zero-window probes every
        `Once:ZeroWindowProbe` for queued senders and backoff for the rest (`UnqueuedRefusedSendersBackOff`); `AckQuery` probes after `Once:ProbeAfter` on the internal lane, answered from the record
        without activating (batched per node), "forgotten" with `forgetUntil` for a tombstone or a missing record (rows kept until then), a
        session's gateway answering from its table or "unknown", only to clear entries whose acknowledgement was lost
        (`ProbeClearsLostAck`); requests never run past their
        deadline, deadlines capped at `MaxAskTimeout`; client sessions as gateway-signed tokens `Session(ClientId, nonce,
        T, R, call-id high-water marks)` slid on replies and heartbeats by `Clients:ResumeWindow`, re-signed at heartbeats or when `T` moved a
        quarter of the window (`SessionResignedSparingly`), resumed only with a valid client token and never past its
        expiry (`StolenSessionCannotOutliveClientToken`), refused by every gateway past `T`, the SDK counting `R` on a
        monotonic clock (`SdkIgnoresDeviceClock`), a single `[Once]` call resent for at most `ResumeWindow` from its first
        attempt, by the SDK and by every gateway through the token's high-water marks (an id above an older token's marks
        counted from that token's quarter: `OlderTokenCannotExtendDeadline`) (`ClientCallResendWindowEnforcedByGateway`:
        a client kept alive by heartbeats while the receiver is unreachable for minutes never runs a call twice), the
        session's rows deleted at the latest resend deadline seen plus `MaxAskTimeout + ClockSkew`
        (`ClientRowsDeletedAfterLastResendDeadline`), stale `[Once]` call
        ids refused and plain requests not (`PlainRequestBelowAckPasses`), at most `Clients:OnceReceiversPerSession`
        receivers tracked, acknowledgements forwarded, probes answered;
      - `AskUntilAnswered` (ADR-100): every ask one key under its root (phases queued, sent,
        stopped, the call's state in `ask_state` with its type id in `state_type`: `AskStateKeptInRow`, an unknown type left sent
        and unanswered for a build that knows it, alerted by `DaServerOnceUnknownState`, and the upgrade check comparing
        `state` lines in both directions, no type an older build cannot read and a type removed in two releases (stop
        creating it, then remove it once no rows remain):
        `UnknownStateTypeWaitsForBuild`, `UpgradeCheckComparesStateLines`), rows written in the item's own record write
        joined to its compare, none in the record (`AskRowsOneTable`), inserted already sent when the window and
        `OnceInFlight` allow and no earlier row to that target is queued, in memory or paged out (`AskSentInItsOwnWrite`,
        `SentInsertNeverOvertakesQueued`); no write budget: a root's rows are dependents of its record's guarded write,
        applied only with it however many (`LargeRootWriteStaysAtomic`, `FencedRootRowsUnchanged`), so every item, system
        and reaction runs; a started item's cap `[ActorOptions(AsksPerItem)]` (16) and
        `Once:BytesPerItem` (1 MiB), the call past it throwing deterministically (`AsksPerItemThrowsDeterministically`);
        retry updates going with the next write; a row answered in a write sent only as a deletion, its pending change dropped
        (`AnsweredRowWithPendingRetryDeleted`); writes split between roots at 512 roots,
        never splitting a root; a request and its state at most `Once:MaxRequestBytes`, checked at
        the call; the sequence taken from `OnceNext` only in the write that sends a row, re-sent after a crash under the
        stored one (`SeqAssignedAtSend`), so the cumulative acknowledgement, from written sent rows only, never passes a
        queued ask (`AckNeverSkipsQueuedAsk`); sends per destination in call order, retries per destination on one timer in order
        (`AskRetriesPerDestinationInOrder`; asks ordered only among first sends, the §1 exception), at most
        `OnceInFlight` sent rows per root (memory and rate only, nothing refused); every sent row held in memory while active
        (`LoweringOnceInFlightNeverAcksEarly`) and a bounded queued head per destination (`Once:QueuedHeadPerTarget` 64,
        `Once:TargetsInMemory` 256, round-robin), the rest paged per destination by ordinal (`AskRowsPagedPerTarget`,
        `SlowTargetNeverHoldsOthers`), a held destination giving up its head when others wait once it has nothing sendable
        (backoff, zero window, only `OnceInFlight` left) or has used its quantum of `QueuedHeadPerTarget` sends, and
        destinations not held paged in round-robin by a target cursor over the root's queued-index keys
        (`ManyTargetsPastMemoryAllProgress`), a stuck destination holding at most `OnceWindow` in-flight slots so
        healthy ones send while fewer than `OnceInFlight / OnceWindow` are stuck (`StuckTargetsBoundedByWindow`), queued
        totals kept in `RootState` and written with the row changes, a held destination's count its total queued rows
        counted at page-in, the page-in taking the root's `Writing` slot, its unlanded queued rows to that target added (`PagedInCountIsTotal`, including the insert-in-flight race; `PageInProgressesUnderCommitEveryTick`), a destination not held counting as having queued
        rows only while `QueuedRows` exceeds the held counts (`QueuedTotalsSurviveReload`), `NextDue` from held rows and
        the next rotation so a root with only stuck work is not woken at once, request
        bytes kept until the insert lands and read ahead for the next sends, a sent row keeping metadata only and its
        bytes re-read for each re-ask round in reads bounded by `BatchBytes`, unwritten rows during a store outage
        growing in memory and landing in one write (`OutageBacklogLandsInOneWrite`), an outage of data writes only (a locked table, a full disk) or of the synchronous standbys alerted by `DaServerStoreWritesBehind`
        (`StoreWritesBehindAlerts`; an unwritable root counted in `daserver.store.unwritable_roots`, alerted by
        `DaServerRecordUnwritable` and left out of the gauge: `UnwritableRootNotStoreBehind`), the next try set by phase
        and none once stopped; a probe of an inactive sender answered from its record and then its ask rows, in that order
        (`ProbeAnswerRecordFirst`); read after the claim, their
        held rows' next try and the next cursor rotation feeding `NextDue` so a crash or unload always wakes the root (`QueuedAsksWakeAfterCrash`); pending row
        changes written in the last write before a move or freeze (`MigrationCarriesPendingAskRows`); link outcomes
        retried by the runtime (`RetryAfter`, `Once:Retry` with backoff and jitter), windows waited out; only a value or a
        definite refusal delivered as `Replied`, in an item that commits implicitly and deletes the row (the row belongs to
        the root, the asking entity recorded; an answer for a destroyed asking entity dropped and counted);
        `Faulted`/`NotHandled` accepted only from the current attempt's call id; a `Stale` answer counted and alerted,
        never delivered, the row moved to `stopped`, counted by the sweeping node's ask census (the ask index and the
        roots' headers) as the cluster gauge
        `daserver.once.stopped` (labelled by `hosting_type` and `actor_type`, exported only while sweeping and counting
        succeeds: `StoppedGaugeOnlyFromLiveSweeper`; `StoppedAskingCountedFromStore`: the alert holds across idle unloads
        and node restarts; the counters an alert reads exported at 0 from start); back-pressure at admission only: past
        `OnceQueued`/`Once:QueuedBytes` queued rows, new ordinary requests and client `[Once]` requests get `Busy` and
        client sessions see a window of 0 (`AdmissionBusyAtQueuedCap`), while systems, reactions, timers, actors'
        `[Once]` requests and answers always run (`ReactionsRunUnderBackPressure`, `MutualOnceRootsProgress`); actors'
        `[Once]` requests exempt from `MaxMail` (`OnceRequestsExemptFromMaxMail`) but counted toward `Unacked` at
        admission (`QueuedOnceCountsTowardUnacked`); `AskInLoopIsDAS034Warning`; alerts `DaServerOnceAskOld` on the
        sweeping node's `daserver.once.ask_age` (from the ask census) and
        `DaServerOnceQueuedFull` on `daserver.once.queued_full`, `daserver.once.ask_rows` a trend only (promtool tests:
        a stuck plateau fires, a healthy ramp does not, a drain resolves), `DaServerOnceUnknownState` on
        `daserver.once.unknown_state` and `DaServerOnceSendersNearCap` on `daserver.once.senders` against its cap
        (promtool tests: an unknown state fires after 15 minutes, a receiver at 90% of the cap fires); the asking entity found again by its identity
        after a reload or migration, never by a raw entity id (`AskingEntitySurvivesReload`);
      - the wake: `WakeAt` on persistent framework entities (reminders, the delete check; ask rows carry their own next try), the `ArmSystem` arming a `Due` from it
        on load and activation, re-arming it on `[Changed<WakeAt>]` and disarming it on `[Removed<WakeAt>]`, the firing
        item running the wake handler registered for the entity's component (the delete check, a package's reminder
        delivery), `NextDue` in the record's header, the due index (`DaServer:Store:DueBucket`) and `DueScan`;
      - the store's schema created only by the schema mode (`--DaServer:Migrate`, the provider's `Migrate`); nodes not
        ready until it answers, one backoff without a deadline (`/alive` treats the wait as progress), and the store not part of readiness once a
        node has started; today's `daserver_actors` table neither read nor converted;
      - migration bytes with layout hashes and a new epoch on arrival, forwarding markers passing mail on, group frames flushed
        before deactivation and migration and discarded on fencing;
      - hot restart's runtime side (draft §10, ADR-098): the pair of instances and the role by the lock on
        `<DaServer:HandoverPath>/node.lock` (node, standby while an arm with its manifest hash is in the arm file, parked
        otherwise; a parked instance takes the lock when the node dies), the arm (nonce, armed hash, deadline in the arm
        file; the write round first; refused while placement changes, while another node holds the permit, or when the
        standby would not fit), the standby listening on a `NamedPipeServerStream` with `CurrentUserOnly` (a socket path
        past the limit refused at start and at publish; a parked instance refuses every handshake and accepts only
        "exit"), the armed node connecting and retrying until a standby passes the handshake (nonce, manifest hash,
        deadline, process id) or the arm expires, the restart permit (`restart_until`), `Restarting`, the freeze, each
        world streamed on its own thread with an incremental checksum, user and framework singletons left out, the dispatch
        queues and link state streamed, the listening sockets passed over (`SCM_RIGHTS` on Unix, `DuplicateAndClose` on
        Windows, Kestrel's `ListenHandle` where it takes one), the standby's check, load and ack, the node parking in place after
        the ack (worlds destroyed, memory returned, never exiting) and its resume in place on a nack, a broken stream, a timeout or a freeze past its bound, `Rejoining`
        (reading the node's own membership entry), pinned slots and the reconcile, `Resumed`, `RestartingNodes`, `NodeRestarting`, a
        lock taken without a completed handover running the normal start, the instance id, `DaServer:Instance` and the
        manifest hash in `/health`; `/admin/restart` as the one admin verb (`mode=unload` for the path through records,
        `dryRun=true` reporting the path `ManifestDiff` picks); the rollout loop with its triggers for local Aspire (the
        dashboard's `{cluster}-restart` restarting the other replica), Compose (the `map-0-net` holder on a pinned image and
        an a/b pair sharing a named volume made writable by a `handover-init` service, all three `restart: unless-stopped`,
        `DaServer__Instance` per service, `up -d --no-deps --force-recreate` of the other instance, admin calls from
        `docker compose run --rm --no-deps rollout` on the internal network), systemd (the `daserver@.service` template with
        `DaServer__Instance=%i` and `/etc/daserver/%i.env`, both instances enabled, the directory from `tmpfiles.d`,
        `Restart=on-failure`), launchd (`kickstart -k` of the other job, `KeepAlive` with `SuccessfulExit = false`) and
        Windows services (one service account or a `PipeSecurity` ACL; `sc failure` and `sc failureflag` for the isolation
        exit's code 75) (the Kubernetes pod of two containers is W11); the retire fence and
        clock rules: a retiring node excluded from rebalancing, a member whose clock moved back accepted again;
      - docs: operations.md and README lose `/admin/drain` and its command.
      Tests: `ShardChecksFrozenFlagBeforeTick`, `StashRestoredWithItsKey`, `AvatarNotInRecord`, `NetIdsCounterSurvivesCrash`,
      `PersistentSpawnUnderUnsavedParentThrows`, `SpawnOfPersistentPrefabTagsRootUnsaved`,
      `DestroyedPersistentChildTagsRootUnsaved`, `PlaceholderNeverCheckpointed`, `RecordMissingComponentsFilledFromPrefab`,
      `UnknownRecordTypeSkippedAndCounted`, `NotSavedComponentsReaddedOnArrivalAndLoad`,
      `PrefabGainedComponentAddedOnHandoverLoad`, `ComponentRemovedAtRunTimeNotReadded`,
      `NonPersistentEntityCompletedAfterMigration`,
      `TimedOutWriteRetriedWithSameBytes`, `LateTimedOutWriteNeverOverwritesNewer`, `ActivatedByRefusesOtherMessages`,
      `MaxPendingExceededHandledLikeAThrow`,
      `PlaceholderMailFullAnswersBusy`, `ReloadKeepsQueuedMail`, `ReloadResetsRootToPrefab`, `ReloadClearsUnsaved`,
      `ReloadMovesBatchRemainderIntoMail`, `ReloadDiscardsUnwrittenHeldOutput`, `ReloadWaitsForInFlightWrite`, `StaleWriteCompletionIgnoredAfterReload`,
      `HalfAppliedItemNeverReachesCheckpoint`, `RepeatedReloadBacksOff`, `GrantSurvivesCalleeCrashAfterReply`,
      `WriteGenerationContinuesAcrossInPlaceHotRestart`, `CommitReplyHeldUntilWrite`, `OutputToOtherTargetsNotHeldBehindCommit`, `OneWriteInFlightPerRootLaterOverwrites`,
      `CommitsShareTickStatement`, `StoreWritesInFlightBounded`, `CommitFencedDropsOutput`,
      `DeactivationWaitsForHeldOutput`, `MigrationWaitsForHeldOutput`, `FreezeWritesRootsWithHeldOutput`,
      `GracefulStopDiscardsHeldOutputPastBudget`, `RepliedHandlerRerunRepeatsPlainOutput`,
      `SystemCommitRidesTickEndWrite`, `PersistedReplyToDeadlineIsUtc`, `IssueSeqWrittenBeforeTokenLeaves`, `NextDueFollowsWakeAt`, `NextDueChangeTagsUnsaved` (a reminder firing on an always-active actor moves `next_due` at the next checkpoint, so `DueScan` stops returning it),
      `WakeActivationRewritesNextDue`, `CrashAfterHandoverRejoinsFromRecords`,
      `HandoverWorldCountKeptUnlessConfigPinsAnother`, `ConfigTablesAndWorldSingletonsNotHandedOver`,
      `LateProcessReadsItsRowBeforeRunning`, `NewGenerationSupersedesRestartingOne`, `JoinDuringRestartMovesNoSlotOfRestartingNode`, `ScaleInWaitsForRestart`, `ResumeIsolatesRootsPlacedElsewhere`,
      `AsymmetricPartitionUnheardNodeDeclaredDead`, `CoordinatorCrashNextOldestTakesOver`, `SameVersionSecondClaimRefused`, `SlotMoveBumpsPlacementSoClaimSucceeds`, `DisplacedOwnerFencedWithoutWrite`, `GroupFramesFlushedBeforeDeactivationAndMigration`,
      `FencedActorDiscardsGroupFrames`, `MigrationArrivalClaimSurvivesRestart`,
      `MigrationArrivalNeverLowersPlacement`, `MapAndLootSameIdDoNotShareRecord`, `DueScanFiltersBySlot`, `DueScanPagesPastLimit`,
      `DueScanUntilFixedAcrossPages`, `DueScanWakesOnlyDueHeaders`,
      `UnwritableRootSkippedRestOfBatchWritten`, `UnwritableRootCancelsIdleDeactivation`,
      `UnwritableRootOnForcedDeactivationKeepsLastRecord`, `UnwritableRootOnStopKeepsLastRecord`,
      `UnwritableRootMigrationFallsBackToDeactivation`, `UnwritableRootLeftOutOfHandover`, `UnwritableRootWithHeldOutputDoesNotStallMigrationOrFreeze`, `MigrateModeCreatesTableAndExits` (and ignores today's table), `NodeNotReadyUntilTableExists`,
      `ShortStoreOutageKeepsNodeReady` (shorter than `IsolationTimeout`), `ArmSystemArmsDueFromWakeAt`, `IntentReaskedAfterCrashViaWakeAt`,
      `DryRunArmReportsPathWithoutArming`, `UnloadModeHandsOverEmptyWorlds`,
      `CoordinatorHotRestartStaysCoordinator`, `ExpiredRestartWrittenDead`,
      `ColdClusterRestartNeverReusesGeneration`, `ParallelFirstNodesFormOneCluster`, `ArmWithoutStandbyExpires`,
      `FreezeAbortsWhenStoreHangsNodeResumes`, `TornStreamNackedNodeResumes`, `RestartingTagNotHandedOver`, `AbortedRestartRaisesResumed`, `PeerBackoffResetOnRestartedHello`,
      `ForwardingMarkerToLeftNodeFallsBack`, `ForwardingMarkerDroppedWhenKeyReturns`, `RequestHeldThroughHotRestartExpires`, `MigrationDoesNotExtendRequestBudget`,
      `RepeatAfterCrashGetsStoredReply`, `IdleDeactivationWaitsForPendingDueAndAsks`, `StoreOutageBoundsMemoryAndRecoversWithoutHerd`,
      `PoisonActorBacksOffClaims`, `RebalancePausesDuringDrainAndRestart`,
      `TimeNoticeRepushedOnResume`, `ManifestDiffPicksPath`, `ComposeRestartOneNodeAtATime`,
      `SlotMoveHandsOffBeforeClaim`, `HandoffPostponedForwardsMail`, `NonCreatingMailClaimsWithoutInsert`,
      `JoinWritesJoiningThenActive` (no slots, no clients before `Active`), `JoinWaitsForConnectivityToActiveMembers`,
      `JoinSkipMeasuredAgainstNewestAlive` (and the skipping member casts no vote until it has probed every member), `SuspicionVotesDeclareDeath`, `IndirectProbeBeforeSuspicion`, `ExpiredSuspicionsDoNotCount`,
      `DeclaredDeadNodeStopsItself`, `PartitionMinoritySideDeclaredDead` (and a 2|3 split of five where the 2 side
      writes first keeps the 2 side), `TwoNodeSplitKeepsExactlyOne`, `TableCasConflictRetries`,
      `StoreOutageFreezesAndResumes` (the whole cluster pauses, connections kept), `PartitionMinorityWithoutStoreFreezes`,
      `StallGuardKeepsWorldsFrozen`, `ReplacementWaitsDeadSeenAtPlusIsolation`, `DeadSeenAtLaterThanStalledDeathCommit` (a
      death writer paused before `COMMIT`: the re-placement deadline still follows the commit), `DeadMemberKeepsSlotsUntilReplaced`
      (no node activates its keys before the coordinator's write, senders get `Unavailable(retryAfter)`; a new generation
      of the name takes none of them early), `LeaseCountsFromReadIssue` (a delayed reply does not extend it),
      `PushedSnapshotDoesNotRenewLease`, `ShardChecksLeaseWithoutMembershipLoop` (thread pool starved: the shard stops at
      the deadline and keeps its Outbox), `SuspendedProcessWakesWithLeaseExpired` (the boot-time clock),
      `IsolatedNodeAnswersProbesIsolatedAndFailsReadiness`, `IsolatedNodeReachingPeersExitsAfterIsolationExit`,
      `StoreOutageNobodyExitsNobodyVoted` (after the outage no node is voted dead for reading the table a few seconds
      late), `FrozenShardDrainsNothingAndQueueStaysBounded`, `MailKeepsArrivalTimeAcrossFreeze`, `MembershipViewReadAtomic`,
      `AsymmetricLinkReported`, `TestClusterFaultInjectingStore`,
      `NewerGenerationMarksOlderDead`, `OneStatefulOneGatewaySurvivesGatewayRestart`,
      `CoordinatorDerivedFromTable` (every node at a version names the same coordinator; a stale coordinator's write
      fails), `PlacementVersionIsTableVersion`, `StorelessWithoutSingleNodeRefusesStart`, `SingleNodeWithStoreRefusesStart`, `SingleNodeHandoverSeedsMemberEntry` (the header's member entry seeds the in-memory store),
      `InMemoryStoreSharedByTestCluster`, `AspireAddsPostgresForMultiNodeWithoutStore`, `AspireSetsSingleNodeForOneNodeWithoutStore`, `DefunctEntriesDropped`, `ChainedSlotMoveWaitsForHandoff`, `ForwardedMailOrderedBeforeParked`,
      `ClaimNotTakenReturnsStoredOwner` (forward, not `NoActor`), `CanCreateFalseWritesNothing`,
      `NonPersistedTypeClaimsNothing`, `NeverWrittenPlaceholderExpiresAfterTtl` (compared on its version: a first write
      landing meanwhile is kept), `RootDeleteWritesTombstone` (`root.Delete()` as one guarded write compared on the root's version, so a fenced activation writes nothing: the tombstone with `forgetUntil`, its receiver rows marked `orphan_after`, no ask rows by `Outstanding`; `DeleteOnUnsavedRootIsDAS031`), `ExpiryRunsOnOldestPersistedHost`, `ExpiryWalkResumesFromCursor`, `RolesStepIdempotent`, `RolesStepCreatesConfiguredSchema` (owned by the owner role, the runtime role's `USAGE` effective), `RolesStepTakesOverExistingTables`, `RolesStepUpdatesPassword`
      (and creates a non-superuser owner role), `VersionedWriteNeverTouchesHeartbeat`, `CanCreateFalseStillActivatesExistingRecord` (a map removed from the table), `CanCreateFalseOnUnsavedTypeIsNoActor`, `JoinOnConnectCreatesKeyDespiteActivatedBy` (a first login creates the `Player` row; `[ActivatedBy]` and `CanCreate` do not apply to the join),
      `HandoffQueryAdoptedPerSlotOnly`, `HandoffQueryVersionStopsSourceActivation`,
      `ConcurrentNewKeyClaimOneTaker`,
      `WriteFindingNothingStoredFences`, `ExpiryWalksKeysOnce`, `ArmVoidedByHandoff`,
      `ProviderStatementsRunUnderAot`, `NotAppliedClaimDecidedAgain`,
      `RecordRequiringClaimNoActorOnlyWithoutBody`, `TimedOutClaimConfirmedByRerun`,
      `NotAppliedOwnTokenLowerGenerationRewritten`, `AlreadyLandedConfirmedBeforeRelease`,
      `StandbyHandoverPauseUnder300ms`, `ListeningSocketsPassedOverNoUnboundGap` (backlog kept; Kestrel re-bound only where
      `ListenHandle` refuses), `NodeLockDecidesRole`, `ParkedInstanceTakesLockWhenNodeDies`, `ParkedInstanceRefusesHandshake`,
      `ParkedInstanceExitsOnRequest`, `WorldsStreamedInParallel`,
      `StandbyAckBeforeOldExits`, `StandbyNackResumesInPlace` (nothing lost, the roll stops for that node),
      `NoStandbyNeverFreezes` (the arm expires, the loop reports it), `ArmedClientRetriesPastOldBuildStandby`,
      `StandbyHandshakeStartsFreeze`, `HealthReportsInstanceAndManifestHash` (the loop waits for the armed hash),
      `StandbySocketPathTooLongRefused` (at start and at publish), `StandbyTakingLockWithoutAckRunsNormalStart`,
      `LaunchdStandbyKickstarted`, `SystemdTemplateInstanceEnvironment`,
      `ArmRefusedWhenStandbyDoesNotFit`, `StandbyPipeOwnerOnly`, `HandedOverProcessParksInPlace` (it never exits: no
      supervisor restart, a Kubernetes pod stays Ready; its memory returned), `ComposeStandbyHandover` (the holder keeps the address), `ComposePairBackAfterDaemonRestart`,
      `DueHeapDefersUnclaimedArrival`, `DueHeapSkipsRearmedEntry`, `SharesNeverExceedNodeLimit` (more
      worlds than the limit), `ArmAnswersAfterWriteRound`, `ArmRefusedDuringHandoff`,
      `FenceMissingRejoinSetsMissedPushes`, `FlushedFramesAppliedBeforeTakeOver`, `SaveEntitiesOutsidePassOnly`,
      `RebalanceExcludesRetiring`, `ClockBackRejoinAccepted`, `ArmPreFlushesUnsaved`, `HandoverExcludesFrameworkSingletons`, `ComposeSameImageRollRecreates`, `ComposeDeployRecreatesNoNode`, `ComposeDeployRunsNewSchemaFirst`, `ComposeDropsAcceptedKeyByEmptyFile`,
      `ComposeNodeReachesExternalDatabase`, `NodePodsKeepDnsAndClusterEgress`, `ImageAndRevisionLabelInOneApply`, `ComposeHandoverVolumeWritable`,
      `WindowsIsolationExitCode` (`ServiceBase.ExitCode` 75 seen by the service manager, on a Windows runner). New for the
      redesigns: `CommitBarrierKeepsPairOrder`, `CommitWithNothingUnwrittenReleasesAtOnce`, `RepeatKeyAnswerWaitsForFirstWrite`,
      `OnceRunsOnceSameSequence`, `StaleSequenceRefused` (an attempt sent before the acknowledgement and delivered late,
      past a migration, never runs), `CumulativeAckPrunes`, `LostAckHarmless`, `ProbeClearsLostAck`,
      `ProbeDeletedSenderForgotten`, `WindowHoldsAtSender`, `ZeroWindowProbeOpens`, `ForgetOnSenderDestroy`,
      `TombstoneCarriesForget`, `DeletedReceiverRefusesLateAttempt`, `DeleteWaitsForUnackedEmpty`, `DeleteRefusedWhileAsking`,
      `DeletingRootRunsNothing`, `SendAfterDeleteReloadsItem`, `DeletingRecordResumedAfterCrash`,
      `ForgetCheckIgnoresRecreatedSender`, `RecreatedSenderRowKept`, `IdleScanForgetsTombstonedSender`,
      `RetriedDeleteCountsAsDone`, `HandoverKeepsUnwrittenRowChanges`, `AskRowsOneTable`, `SeqAssignedAtSend`,
      `AckNeverSkipsQueuedAsk`, `QueuedAsksWakeAfterCrash`, `AdmissionBusyAtQueuedCap`, `OnceRequestsExemptFromMaxMail`,
      `ReactionsRunUnderBackPressure`, `MutualOnceRootsProgress`, `MigrationCarriesPendingAskRows`,
      `DeletingTagCoversSubtree`, `StoppedAskingCountedFromStore`,
      `OlderTokenCannotExtendDeadline`, `ClientCallResendWindowEnforcedByGateway`,
      `OnceSequenceSurvivesCrashAndHandover`, `ClientSessionAckPrunes`, `GatewayRefusesStaleCallId`,
      `ClientRowsDeletedAfterLastResendDeadline`, `OnceResentUnderSameSequence`, `OnceClientPurchaseResentRunsOnce`,
      `AppRestartIsNewSession`,
      `AskDeadlineCappedAtMaxAskTimeout`, `AskUntilAnsweredCommitsIntent`, `LinkOutcomeNeverReachesReplied`,
      `ReaskAfterCrashSameSequence`, `RefusalDeliveredOnce`, `AskRowDeletedOnAnswer`,
      `AckFromWrittenStateOnly` (a `Send` after an in-memory answer, then a crash before its write: the restored
      ask row re-asks and gets the stored reply, never `Stale`), `StaleToAskingAlertedNeverDelivered`,
      `RecreatedSenderNumbersAboveOld` (a `Loot` deleted and recreated before its old rows are forgotten is not refused
      `Stale`), `ShardRowsBounded` (a stock shard taking `Reserve` from 5,000 distinct orders a second keeps its record
      under `MaxRecordBytes` and its rows under `Once:SendersPerReceiver`, and its rows go when the sale's orders are
      deleted), `LateFaultedFromEarlierAttemptIgnored`,
      `TooOldAttemptDropped` (a frame that waited in a full window past `MaxAskTimeout + ClockSkew`),
      `DelayedAckSurvivesFullMailbox`, `WindowUpdatePushedInOrder`, `ZeroWindowProbeFixedInterval`,
      `AskRowSurvivesStashDestroy`, `OrphanReplyDroppedAckSent`, `ProbeAnsweredWithoutActivation`,
      `ProbesBatchedPerNode`, `UnknownSessionProbeWaitsForRowDeletion`, `SdkMapsStaleToUnknown`,
      `SessionTokenRefusedPastTByAnyGateway`, `SdkStopsResendingPastCallDeadline`, `FragmentAckedOnReceipt` (two 16 MB messages in
      a 32 MB window both finish), `IncompleteMessageResentFromFragmentZero`, `LargeMessageReservesWholeSize`,
      `BusyRefusalHasReservedRoom`, `AbortedHotRestartClearsOwnRestartUntil`, `SlowSingleRowRotatedOut`,
      `ConnectionLimitsSizedFromAutoscalerMax`,
      `ReaskNotExecutedAfterUnknownKeepsGrant` (a re-ask's link outcome after an unknown first attempt never releases
      the drop), `CrashBetweenReaskAndNewWakeAtFiresAgain`, `RewrittenWakeAtRearms`, `RemovedWakeAtDisarms`,
      `AnswerRoomReservedByReceiver` (a map whose window to a gateway is full of pushes still answers that gateway's
      clients), `FixedReplyReservesExactSize`, `StallNeedsBracketGap` (a read that waited to
      start is no stall), `CoordinatorNeverRestarting`, `ArmRefusedWhileReplacementPending`,
      `MembershipRoleConnectionsReserved`, `ResizeBackThroughDashboard`, `RequestsReadFromCgroup`, `VpaTargetCappedAtLimit`,
      `PartialReassemblyResentAfterHandover`, `ActivationStampOrdersUnsavedReactivation` (an unsaved sender re-activated after
      a crash, its counter restarted, still wins: its activation stamp is larger), `HotRestartKeepsClientsConnected`, `FailedHandoverResumesInPlace`, `HeldGroupPushJoinsFrameOnRelease`, `HeldGroupPushDoesNotStallGroup`,
      `HeldOutputCapAnswersBusy`, `ExpiredHeldReplyDropped`, `HeldLatestReplaced`, `RepeatCommitWaitsForEarlierWrite`,
      `MigrationConvergesWhileRootCommitsEveryTick`, `OversizeRecordUnwritable`,
      `BatchBytesCapped`, `KeysSortedNoDeadlockWithStaleOwner`,
      `CheckpointStormDrainsOldestFirst`, `CheckpointsKeepShareUnderCommitFlood`, `BacklogPausesIntake`,
      `GracefulStopPastBudgetLogsUnwritten`, `MembershipOnOwnStoreClient`, `RecoveryRampsStoreTraffic`,
      `RecoveringNodeIgnoresIsolatedVotes` (no cascade of deaths after a two-minute outage),
      `StoreClockJumpTreatedAsStall` (a VM paused 30 s with its boot clock stopped), `CoordinatorExcludesRestarting`,
      `ArmRefusedWhenWritesSlow`, `ResizeDeferredRevertedArmRefused`, `RestartPodReadinessWindowOnlyParkedContainer`,
      `RebalanceSkipsHotActor`, `PlaceholderMailNodeBudget`. BenchmarkDotNet
      `CommitThroughputPerWorld` (1, 10, 100 and 1,000 commits per world per tick at a store latency of 1 and 5 ms, per
      provider; for PostgreSQL also the generic table against typed columns, `GenericTableCommitThroughput`, the gate
      above), `ClaimThroughput` (activation batches through `Get` then a conditional put at 1 and 5 ms),
      `OnceCreditThroughput` (`[Once]` credits into 10,000 players with windows of 16 at 1 and 5 ms: the sequence check,
      stored reply, cumulative acknowledgement and pruning, against the same credits as plain asks),
      `CommitReplyLatency`, `CheckpointStorm` (1,000,000 roots written at once: drain time and memory against
      `Store:WritesInFlight`), and `HandoverPause` (JIT and AOT, 100,000 and 1,000,000 actors of about 200 bytes: the
      freeze, the parallel stream and the standby's load).
    - [ ] W6a `DaServer.Reminders` (ADR-090, draft §15): `Reminder<T>(ActorId Actor, Key, Period)` persistent children whose next firing is
      their `WakeAt`, armed by the runtime like every wake (the package has no arming system), one per (T, key):
      `root.Remind<T>(DateTime at, TimeSpan every = default, long key = 0, bool move = false)` finds or spawns it, `root.CancelReminder<T>(key: 0)`, `Reminded<T>(Key, UtcAt)`.
      Tests: `RemindKeyedTwice`, `RemindSamePeriodKeepsTime`, `RemindMoveOrNewPeriodReschedules` (the unique index key is `(Actor, Key)`, so a new period finds the existing reminder), `CancelReminderDestroys`, `ReminderActivatesUnloadedActor`, `ReminderSurvivesNodeCrash`,
      `ReminderFiresOncePerUtcAtWithinActivation`, `RemindersArmedOnlyByRuntime`,
      `PeriodicReminderAdvancesInSameRecord`, `ReminderAfterOutageFiresOnce`; BenchmarkDotNet `DueScan` over 1M
      records, 0 B per firing.
    - [ ] W6b `DaServer.Topics` (ADR-090, draft §15): the `Topic` actor type with its subscriber table keyed by activation stamps, the
      one-way `Subscribe`/`Unsubscribe`/`Publish`/`NotSubscribed` package messages, `[Hosts<Topic>]` with a store, the
      subscriber's `Subscription` buffer and its renewal, the check at dispatch, one frame per subscriber node; deletes
      the service `Publish`. Tests: `PublishReachesSubscribersOnEveryNode`, `DeliveryNeverActivates`,
      `ResubscribesAfterActivation`, `GoneSubscriberPruned`, `StaleUnsubscribeKeepsNewSubscription` (an older stamp's
      unsubscribe is ignored), `LowerStampSubscribeIgnored`, `SubscriptionRenewsAfterTopicCrash`,
      `CancelledDeactivationResubscribes`, `ReorderedUnsubscribeHealsWithinTwoRenewIntervals` (same stamp, reordered
      during a Topic migration: a renewal restores it within two intervals), `LostUnsubscribeStopsDeliveryOnNextPublish`, `LostUnsubscribeThenMigrationStillPruned`, `ResubscribeInSameActivationSurvivesNotice`, `TopicDeliveryHandledOnSubscriberRoot`;
      BenchmarkDotNet fan-out to 1k/10k subscribers on 1/3 nodes, 0 B per delivery.
    - [ ] W7 Samples (mmo, flashsale) on the new model (their systems use `Mut<T>`), the mmo's drops split
      between a non-ticking `Loot` actor (their records, persistent, writing `Granting` with an `AskUntilAnswered` of `[Once] GrantItem`,
      `Granted` called only on a value or a definite refusal) and the map (what
      clients see, not saved, shown again after every activation that is not a migration), its avatars not saved, its AOI telling entries and exits (`Appear` into a cell newly written, `Disappear` before every destroy, a crossing's snapshot of the cells newly subscribed); the login sample issuing client tokens (`IssueClientToken` commits: the issue sequence
      written before the token leaves); the mmo's inventory as slots on the `Player` root (the client's `UseItem` bound
      to the root), precious-item use and premium purchases as `[Once]` client requests (run once, the SDK resending under the same sequence number), a
      recharge through the shared payment sample (below: `Player` opens a `Payment` on the client's `[Once] Recharge`,
      `Payment`'s `[Once] Settled` credits once; the client claims nothing), a guild's daily quest reset on a `DaServer.Reminders` reminder;
      `samples/payments`, the shared payment sample both games use (ADR-091): `Payment(id)` opened by the business owner
      with `AskUntilAnswered` of `[Once] Open`, a node HTTP endpoint creating the checkout at the provider, the webhook
      checking the callback's signature and verifying the payment server to server before `ICluster.Ask(Payment, Paid)`,
      `PayFor` carrying what the owner gets and `Settled` echoing it (the owner keeps no list of payments it waits for),
      checkout idempotent per payment id (the provider's idempotency key) and retrying `NoActor` while `Open` lands,
      `Payment` committing every outcome and the webhook answering 2xx for each definite one, settling the owner with
      `[Once] Settled`, expiry on a reminder, late, mismatched and second payments recorded as refunds due, a daily reconciliation, and a provider simulator (checkout, signed callback, verify API,
      refunds, daily report) that the bots pay through; it is source-linked into each game's server, which supplies
      `PayFor`, `Notify`, its `[Once] Settled`, `[From<…>]` on `Open` and `[Hosts<Payment>]` on one of its server types (when it lands, CLAUDE.md's layout section lists `samples/payments`); the flash sale's
      `PlaceOrder` carrying only the sale, handled only by a keyed, non-persisted `Gate(Sale, Shard)` actor
      (`[ActivatedBy<PlaceOrder>]`, `CanCreate` over the sale table) that asks `Order(sale, user)` with the server
      message `Place` (ADR-091, ADR-095); each AppHost becomes an `apphost.cs` (file-based) in the sample's server
      project, with client ports declared by `WithClientConnections(port)`; `DaServer.Aspire`'s generator writing the
      server types from the committed `wire.manifest` (`actor` and `hosted-by` lines; `IAcceptsClients` on those hosting
      `Connection`) and the build check, and the route check reading the committed manifests through `ManifestDiff`
      (no `ListRoutes` mode, no process per node project). The mmo keeps its hand-written AOI (ADR-091). Tests:
      `Sample.Tests` on `TestActor<Map>`
      (`Walking_out_of_range_during_the_windup_dodges`, `A_busy_tick_allocates_nothing_and_threads_change_nothing`,
      `A_grant_is_asked_again_after_a_crash`, `ReaskLinkOutcomeNeverReleasesDrop`, `DropsReshownAfterMapCrash`, `DropsReshownAfterCrashBeforeFirstRecord`, `LaterDropAppearsToEarlierClients`, `TakenDropDisappears`, `ClaimAnswerArrivesBeforeGone` (Loot's answer and its `Gone` arrive in send order: the client gets its answer, then the drop disappears), `KilledNpcDisappearsAndDrops`, `CrossingPushesNewCellsSnapshot`, `CrossingDuringHandoverSnapshotsAtConfirmation` (an avatar crossing cells while its `Joined` is dropped is tagged `Resnap` and gets a full snapshot when confirmed), `SnapshotSkipsWhatVanishRemoves` (a `Leaving` avatar and a dead NPC are left out; a dead avatar, which stays on the map, is in), `MapCrashReusedNumberDoesNotBreakLoot` (a drop number repeated after a map crash ends in `Clash`/`Redrop` and the drop
      is shown again), `VisitorNumbersNeverMeetDropNumbers`, `VisitorNumbersContinueAfterMigration`, `TimedOutClaimLeavesPickUpUnknown`), `LoginIssuesIncreasingSequence`, `LoginTokenLeavesAfterSequenceWritten`,
      `UseItemBindsToPlayerRoot`, `PreciousPurchaseRunsOnceAcrossRetry`, `RechargeCreditsOnce` (through the shared
      payment sample), `PaymentWebhookRepeatedAfterDaysCreditsOnce`, `PlayerSettledOncePerSequence` (a re-ask after
      `Payment` crashed carries the same sequence number and is answered from the player's `Unacked` entry),
      `WebhookAnsweredOnlyAfterPaidWritten`, `RetriedWebhookWaitsForFirstWrite`, `SettleReaskedAfterPaymentCrash`
      (through the ask row's next try and `NextDue`), `WebhookWithBadSignatureRefused`, `WebhookVerifiedWithProviderBeforeAsk`
      (a forged callback with a valid id but no provider payment credits nothing), `AmountMismatchListedForRefund` (the state stays `Open`, the deadline still notifies),
      `UnpaidPaymentExpiresAndSettlesExpired`, `LatePaymentDuringExpiredSettleIsRefunded` (a payment landing while
      `Settled(Expired)` is in flight is listed for refund and the answer leaves the charge `Expired`),
      `RepeatedSecondPaymentRefundedOnce`, `TwoLatePaymentsBothRefunded`, `PaymentDeletedAfterRetention` (`Purge` deletes only a
      settled or expired charge whose refunds are all done and whose `SettledAt` plus `Payments.Retention` (a constant of the sample) has passed;
      its rows at the player are forgotten), `RefundCallbackMarksRefundDone` (a repeated refund callback answers the
      earlier result), `PurgeKeepsChargeWithPendingRefund`,
      `WebhookAnswers2xxForEveryDefiniteOutcome`, `CheckoutTwiceIsOneProviderCheckout`, `CheckoutBeforeOpenRetriesThen409`, `CheckoutEndpointReadsAmountFromPayment`,
      `ProviderSimulatorRetriesCallback`, `ReconciliationReportsMissingStuckAndLate`, `StashPrefabRoundTrip` (Stash, Prize, Position and Granting
      in the record and back), `UnansweredGrantRepliesNothing` (the map's `Claim` times out, never a definite `false`),
      `SamePlayerRetryDuringGrantGetsNoFalse`, `GrantedCommitsBothOutcomes`, `BuyItemRetryAddsNothing`,
      `BuyItemUnknownSlotIsNoSuchItem`, `BuyItemFullBagIsBagFull`, `CastUnknownSkillIgnored` (a client index never throws
      a map into `Reload`),
      `OrderCompensatesOnlyOnDefiniteRefusal` (a timeout keeps asking; the payment of a reserved order settled as
      expired writes `Releasing`, which survives a crash), `SoldOutEndsOrderWithoutRelease`,
      `ReleasedStockIsSoldAgain` (`Gate` re-learns a shard through the shard's `Describe`), `OrdersDeletedAfterSaleForgetRows` (each order's own reminder at the sale's end plus the payment window),
      `EndedSaleRefusesNewOrders` (`Gate` compares `time.Utc` with the sale table's `EndsAt` on every request, so it holds after `Gate` restarts), `OrderPaidThroughSharedPaymentSample` (Reserving,
      AwaitingPayment, Confirmed), `OrderPaymentExpiryReleasesStock`, `GuildDailyResetFiresAfterCrash`, `BuyerCannotOrderForAnother`,
      `AppHostServerTypesGeneratedFromManifest`, `ClientPortWithoutConnectionHostFailsBuild`,
      `RouteCheckReadsCommittedManifests`; the 1,000-bot and
      100,000-buyer E2E on the new model.
    - [ ] W7a `DaServer.Replication` and `DaServer.Replication.Client` (ADR-090, draft §15, architecture.md section 10):
      `[Replicated]`, `ReplicatedTo`, generated field bitmasks, `Replica<T>` with its sent stamp, the per-group ring of
      exits, the per-actor incarnation (renewed whenever the actor is rebuilt, a reload after a throw included),
      patches per `ClientGroup` in pooled frames from a `ChangedAny` system, full state with `Reset` on a mismatch,
      deltas on rejoin; proven by a benchmark project running the mmo's AOI scenario both ways (replicated components
      against the sample's hand-written `Nearby`): bytes per client, broadcast p99, 0 B per tick (ADR-091). Tests:
      `RejoinSameIncarnationGetsDelta`, `IncarnationChangeForcesFullResend`, `IncarnationRenewalClearsExitRingsAndStamps`, `ReloadRenewsIncarnation`, `OverwrittenExitRingForcesFullResend`, `RejoinDeltaSendsFullStateOfEntityThatJoinedGroup`, `RejoinDeltaAckIsWorldChangeTick`,
      `FullResendClearsClientReplicasFirst`, `ReplicationClientRunsOnNet10` (`DaServer.Replication.Client` targets `net10.0`
      only, the target EasyEcs ships, so Godot 4 .NET games and .NET bots load it).
    - [ ] W7b Non-.NET clients (ADR-090): the client protocol specification, the conformance suite
      (run against the C# SDK first), the TypeScript client (`@daserver/client`) with the codecs Nino.TypeScript (prerelease,
      pinned) generates from `Game.Protocol`'s build; proven by the flash sale from a browser page and the suite in CI;
      the first C# client engine is Godot 4 .NET (`net10.0`); Unity (netstandard2.1, IL2CPP, WebGL over a jslib
      WebSocket transport) is future work, and its replication waits for an EasyEcs netstandard2.1 build. Tests:
      `ConformanceSuitePassesOnCSharpSdk`, `ConformanceSuitePassesOnTypeScriptClient`, `FlashSaleBuysFromBrowserPage`.
    - [ ] W7c Tools (ADR-090): `TestCluster.Partition`/`Kill`/`DelayLinks` and `Table(value)` for users' tests (the framework's own
      partition and crash tests move onto them); the read-only inspector's endpoints (drafts/architecture.md section 8,
      "Inspector and admin clients"), with Aspire commands as thin clients for local runs (the Dashboard is W7e's).
      Tests:
      `UserTestPartitionIsolatesNodes`, `UserTestKillSkipsGracefulStop`, `UserTestDelayLinksDelaysFrames`,
      `TestClusterTableSeedsCurrentVersion`,
      `InspectorDescribesActorSubtree`, `InspectorCountsByComponentWithoutReflection`.
    - [ ] W7d Samples as templates (ADR-091): `samples/starter` (the chat room of draft §0: `[ActivatedBy<JoinRoom>]`,
      `PresencesPerClient = 16`, `JoinClient`/`TryGetClient`, no record; the `daserver` template), `samples/arena`
      (matchmaking queues per region and mode, ranked and casual, `Match` and `Party` actors, a sharded `Leaderboard`
      merged on read; a detached ticket marked not matchable by a `[With<Detached>]` system (a reconnect within the grace takes it over; its `Expire` removes it), the `Match` with `DetachGrace = 0`, tickets keyed by account and expiring once, `Setup` asked before `Matched` is pushed), `samples/relay` (a `[TickRate(30)]`
      `Room` gathering inputs and broadcasting one frame per tick to its `ClientGroup`; simulation on the clients), the
      mmo's input ticks (`MoveTo.InputTick`, `LastInput` echoed in `Nearby` and snapshots) and its `Resync` keyframe
      system, NPC AI as `[ForEach(Slices = N)]`, the flash sale's saga (the order as a process manager: each step a
      state of the order with an `AskUntilAnswered` of a `[Once]` request (`Reserve`, a `Payment` of the shared sample,
      `Release`), compensation only on a definite answer, reconciliation of what stays unanswered; the sagas-versus-ACID page), each
      with BenchmarkDotNet numbers and a bot E2E; `DaServer.Templates` (`daserver`, `daserver-mmo`,
      `daserver-flashsale`, `daserver-arena`, `daserver-relay`) generated from the samples, instantiated, built and
      tested in CI; the README's getting started becomes `dotnet new daserver`. Tests: `StarterChatJoinSayLeave`,
      `StarterRoomGoesIdleWhenEmpty`, `ArenaMatchesQueuedPlayersE2E`, `DetachedTicketNotMatchable`, `TicketExpires`,
      `PlayerDetachingWhileMatched`, `ReloginTakesTicketOver`, `ExpiryScheduledOnceAcrossRejoins`,
      `JoinMatchNeverBeforeSetup`, `RelayRoomSendsOneFramePerTick`, `InputTickEchoedInNearby`,
      `SagaCompensatesRefusedStep`, `SagaStepReaskedAfterCrash`, `LateReserveAfterReleaseRefused`, `TemplatesInstantiateBuildAndTest`.
    - [ ] W7e `DaServer.Dashboard` (ADR-093, doc/dashboard/design.md): the stateless console (its own Deployment and
      Service, `AddDaServerDashboard`, its own admin key (`{cluster}-admin`, key-per-file, current and accepted) with scopes view
      and act, no Kubernetes API), the
      screens of the design's jobs table over one snapshot per refresh (Prometheus's fixed queries and range queries,
      or the nodes' current readings polled at a fixed cadence into the snapshot and a bounded ring), triage from
      firing Prometheus alerts (the outlier rule only naming culprits and the rows that stand out), distributions,
      outliers and the slowest actors computed on the server, logs from Loki (LogQL
      `query_range`, the patterns API, one query per type, cached in the snapshot), the audit in its durable sink
      (entries in the Dashboard's `IStore`; in development without a database, the in-memory store, for the life of the process, which a production node refuses), history links to Grafana at the same time range, OIDC with view and act roles, actions confirmed
      by typing and audited (with a reason for type- and cluster-wide actions, preconditions, idempotency keys, one
      action per type); the node's OpenTelemetry resource (`service.name` the group, `service.instance.id` the
      node) and framework logs with `daserver.actor.type`, `daserver.actor.key` and the trace id; the ask-latency histogram (`daserver.ask.duration`, per
      server type, in seconds, with explicit bucket boundaries from 0.0005 to 10 s including 0.025 through
      `InstrumentAdvice<double>.HistogramBucketBoundaries`: `AskDurationBucketsExplicit`, `HealthyClusterAskP99BelowLimit`) with its recording rules (`cluster_type:daserver_ask_duration_seconds_bucket:rate2m` by
      cluster, type and `le`, `cluster_type:daserver_ask_duration_seconds:p99` and
      `cluster_type_node:daserver_ask_duration_seconds:p99`); the OTLP metric export interval pinned to 15 s in
      `AddDaServerTelemetry` (`OTEL_METRIC_EXPORT_INTERVAL` overrides it); every DaServer metric
      carrying `cluster` and `type` labels beside `node` and the rule
      `DaServerAskLatencyHigh` carrying `daserver_limit` in `observability/prometheus-rules.yaml`; one reading table
      generating the instruments, `/admin/stats` and the stats line (off when OTLP is set); the samples' AppHosts
      adding the Collector, Prometheus, Loki, Tempo and Grafana when published, Prometheus fed by the Collector through
      its OTLP endpoint (`--web.enable-otlp-receiver`, `daserver.cluster` promoted) and scraping no node; the
      `{cluster}-deploy` Secret (webhook, lookup, and the callback's `current`/`accepted` HMAC keys, mounted into the
      Dashboard only; nodes hold no pipeline credential) and the HMAC-signed completion callback, treated as a hint
      confirmed by a run lookup; a node's resize request (its `/health` reporting `requests_low`, seen by the Dashboard's poll) checked and
      dispatched by the Dashboard once per (node, incarnation) plus one retry, outside the type's lock
      (`ResizeDispatchedOncePlusOneRetry`, `ResizeOutsideTypeLock`, `ResizeStalledAfterTenMinutes`); `DaServerNodeRequestsLow`; a node's restart (for a node hosting actors, a same-image handover of that one node through the deploy pipeline's
      webhook; retire and exit for a gateway or stateless node, which the platform replaces), the admin key re-read when its
      files change (current, then accepted), membership from up to three ready nodes (highest version, view hashes from
      the pulses), a type's hot restart and a roll back
      through the deploy pipeline's webhook (`DaServer:Dashboard:DeployWebhook`; the Dashboard never loops over
      nodes itself), the command shown when none is set; the front end in vanilla TypeScript built by esbuild, assets embedded. Tests:
      `RollbackCallsDeployWebhookAndAudits`, `RollbackWithoutWebhookShowsCommand`, `TypeHotRestartCallsDeployWebhook`, `DashboardShowsIncidentFromFiringAlerts`, `TriageWithoutPrometheusShowsThresholdFreeStatesOnly`,
      `ChartsFromPollRingWithoutPrometheus`, `DashboardLogsComeFromLoki`, `DashboardWithoutLokiSaysSo`,
      `ActorKeyIsNotALokiLabel`, `ActionNeedsTypedTargetAndIsAudited`, `ViewScopeCannotAct` (a node refuses an act request signed for
      view), `DashboardHoldsNoState` (two replicas answer alike: action records and the Data Protection key ring in the
      store; two replicas only with a store), `ServerTypeScreenFixedAt400Nodes` (the screen's rows and bins do not grow with
      nodes), `DashboardPollsNodesAtFixedCadence` (ten viewers add no node requests; concurrency sized from the node
      count, failed nodes backed off),
      `DashboardReadsAggregatesFromPrometheusWhenPresent`, `OutliersComputedOnServer`,
      `SlowestActorsMergedFromNodeRankings`, `ActorDetailAsksOwningNodeOnly`, `FindCapsResultsPerGroup`,
      `ResourceSetsServiceInstanceId`, `OneReadingTableFeedsMetricsStatsAndLine`, `StatsLineOffWhenOtlpSet`, `DashboardSurvivesRotation`, `MembershipCrossCheckedAcrossReadyNodes`,
      `GatewayRestartRetiresThenExits`, `StatelessRestartStopsGracefully`, `AlertLimitComesFromRuleAnnotation` (no rule,
      no limit line), `IncidentsGroupAlertsByRuleAndType` (400 per-node alerts are one incident; ranked by severity,
      then nodes; silences honoured), `AlertsUnreadableIsNotAllClear` (Prometheus down or stale shows "Alerts cannot be
      read" and the threshold-free checks), `NodeReadingsCarryAge` (2 s timeout; not answering, isolated and stale are
      states and stay out of distributions; "N of M read fresh"), `RefreshPatchesInPlace` (focus, typed input and list
      order survive a refresh; Find and confirm are not re-rendered), `RestartRefusesStaleIncarnation`,
      `OneActionPerTypeAtATime`, `RepeatedActionIsIdempotent` (a double press, a retry, a second operator; the key excludes the operator; a
      finished action no longer dedupes),
      `DeployWebhookCarriesExpectedBuild`, `ActionOutlivesBrowser`, `WebhookTimeoutIsUnknownNotRetried`,
      `TypeWideActionNeedsReason`, `AuditWritesIntentAndOutcome` (same action id, durable sink, entries never rewritten, an `Unknown` write confirmed before the call),
      `ClusterAndTypeLocksNeverBothHeld` (a type's action and a config push racing: both may be refused, never both run),
      `LockWithoutActionIsFree` (a replica crashing between the action's key and the lock leaves no type locked),
      `DashboardHoldsNoSigningSecret` (only `{cluster}-admin` mounted), `ActionsNeedAntiforgeryToken`,
      `DataRenderedAsTextUnderCsp` (a log line holding markup shows as text), `MixedBuildsCountedAndComparedPerBuild`,
      `OutlierRuleTwoSidedWithNoRequests`, `TypeP99FromMergedBuckets`, `LokiQueriesOnePerTypeAndCached`,
      `ConfigPushTwoPhaseAbortsOnTimeout`, `FindNeverActivatesActor` (a key parsed by its registered key type; lookup on
      Enter or a pause), `AlertRulesGroupByClusterTypeNode` (every rule keeps the three labels; the GC rule renames job
      and instance), `SilentNodeAlertFires` (a node that stops pushing fires `DaServerNodeSilent` instead of resolving
      its alerts), `MembersDisagreeOnStuckViewVersion` (a node behind the cluster's newest view version whose own has not moved;
      member counts differing during a rollout do not fire), `NodeRestartTakesTypeLock` (a node restart waits while its type rolls
      back), `TypeLockIsUniqueIndexWithLease`, `ExpiredLeaseReconcilesFromPipelineRun`, `ActionFollowsRunToTerminalState`
      (Requested, Accepted, Running, then Done or Failed; Stalled and Unknown hold the lock until one of them), `StalledWhenMembershipShowsNoProgress`,
      `LockHeldUntilTerminalState` (a 400-node roll and a same-image hot restart keep it), `FinishedActionNoLongerDedupes`,
      `StoreDownSendsNothingAndShowsCommand`, `OutcomeSpillsAndReplaysInOrder`, `StoreDownIsFirstIncident`,
      `StatusCountsFreshReadings`, `LastMembershipKeptWithAge`, `MembershipTargetsRechosenEachRefresh`,
      `PollAlwaysRunsWithPrometheus`, `FailedNodesBackedOff`, `SmallBuildComparedWithMajority` (a one-node canary
      flags), `OutlierAbsoluteFloor`, `RowChangeStillPatchesValues` (values update and the status says rows changed; rows
      change after 30 s at the latest), `AlertsUnreadableHasHysteresis`, `RecordingRulesNamed`, `LokiPatternsBudgeted`
      (the incident's type each cadence, the others every five minutes, incremental windows),
      `CancelledRunIsFailed` (a newer pending run cancels ours: the action is Failed), `SameSiteLaxWithAntiforgery`, `ExpiredSessionIs401AndKeepsInputs`,
      `AssetsSelfHostedUnderCsp`, `StylesViaCssom`, `SilenceNeedsActScopeAndIsAudited`, `SilenceCarriesIncidentMatchers`,
      `RefusalReenablesConfirm`, `TimesShowZoneOffset`, `AuditRowsMergedByActionId`, `NamesFitAt1024`,
      `LockHeldWhileUnknown`, `LockHeldWhileStalled`, `MarkFailedReleasesLockAndIsAudited`,
      `MarkFailedRefusedWhileStoreDown`, `ClusterActionsLockSentinel` (two config pushes never run at once),
      `ExpiredLeaseClaimedByOneReplica` (compare-and-set on the store's clock), `RunLookupGraceBeforeFailed`,
      `RunLookupFailureKeepsLock`, `CallbackWithoutValidSignatureRefused`, `SwappedRequestsShownWithResize`,
      `ResizeOutsideTypeLock` (idempotent, per-node group), `ResizeDispatchedOncePlusOneRetry`, `ResizeStalledAfterTenMinutes`, `LeaseRenewedOnOwnTimer` (a slow refresh never lets it lapse), `ClaimOnlyAfterLeaseExpiredALeaseAgo`,
      `LateDoneNeverOverwritesFailed`, `CallbackConfirmedByRunLookup` (a replayed callback is harmless),
      `RunFoundWithinAnHourReattached`, `PipelineCheckFailsClosed` (no answer for five minutes refuses, whatever the run's age), `PipelineCheckRunsOnStalled` (a run whose Job sat Pending past the Stalled bound still runs; only Done, Failed or an unknown id refuse), `DashboardAnswers503WithoutStore` (never "unknown id" when it cannot read the store), `QueuedRunBehindLongRolloutRuns` (an answering Dashboard decides), `PipelineCheckRetriesBeforeRefusing` (a Dashboard restarting for a minute fails no run), `RolloutJobReachesDashboardPort` (the network policy admits the Job's pods, labelled `daserver.io/rollout={cluster}`, to the Dashboard's port; Compose's `rollout` on the internal network), `AdminKeyOnlyOnActionsRoute` (the Dashboard accepts the admin key on `GET /admin/actions/{id}` only; a NetworkPolicy cannot restrict a path), `RolloutJobPodsCarryRolloutLabel`, `LateRunRefusedAfterMarkFailed` (the step asks `GET /admin/actions/{id}`), `ResizeRowsTakeNoTypeLock`, `OperatorResizeDedupedByIdempotencyKey` (no attempt number), `MarkFailedOnResizeWritesResizeRow`, `ResizeDispatchedOncePerIncarnationAcrossReplicas`, `StalledResizeHoldsOnlyItsNode`, `MarkFailedOfferedForLongRequestedOrUnknown`,
      `MarkFailedInOneTransaction`, `MarkFailedOnlyFromStateSeen`, `ClusterLockExcludesTypeLocks` (a config push is refused
      while a type rolls and the reverse, under concurrent inserts from two replicas: the serialising advisory lock),
      `PushRefusedBehindStalledTypeNamesIt`, `StagingAcksKeyedByIncarnation`, `RenewalZeroRowsStopsStoreErrorRetries`, `HandoverCarriesConfigVersion`, `ResizeProgressIsGaugeClearing`, `DashboardDispatchesResizeFromHealth`, `ParkedInstanceExportsNoMetrics`, `ParkedInstanceNeverShownAsNode`, `FocusReturnsToOpener`,
      `MetricExportIntervalIs15s`, the rules' unit tests under `promtool test rules` (`NodeSilentIgnoresGracefulStop`,
      `NodeSilentFiresAfterTwoMinutes`, `NodeSilentFiresOnCrashMidDrain` (stop phases before `close` do not exclude),
      `NodeSilentForgetsAfter15Minutes`, `MembersDisagreeIgnoresDepartedNode`, `MembersDisagreeQuietDuringRollout`,
      `NodeRequestsLowFires`, `AskLatencyHighFromRecordingRule`, `OnceTooOldFires`), the `daserver.cluster.view_version` gauge, and the
      prototype's layout, control and wheel checks against the built pages.
    - [ ] W8 Native AOT publish of the samples' nodes with 0 warnings (what holds and what to verify:
      drafts/architecture.md section 8); the AOT E2E run. Local runs: nodes as
      `AddDotnetProject` (verified: no `dotnet run` process, OTLP, endpoints, replicas, attach; `AddProject` as the
      fallback); AOT checked locally through the AOT images in Compose; images on `resolute` chiseled, built in a
      container on macOS and Windows; the Compose health check by the node's own executable (drafts/deployment.md
      section 0). Admin endpoints, the inspector included, as Nino through Nino.AspNetCore's Minimal API results (or plain text
      for `Accept: text/plain`), `MapAsk`/`MapSend` Nino bodies (`NinoBody<T>`) by default with per-endpoint opt-in JSON through the application's source-generated
      context (ADR-083); Nino.AspNetCore verified in a published AOT node (drafts/architecture.md section 8). Tests:
      `SampleNodesPublishAotWithoutWarnings`, `E2EPassesAgainstAotBinaries`,
      `LocalRunStartsNoDotnetCli`, `ComposeHealthCheckRunsNodeExecutable`, `AdminAnswersNinoOrPlainText`,
      `ConfigBindingNoAotWarnings`, `CpuCountFromRequestsUnderAot`, `HandoverPassesSocketsUnderAot` (the `sendmsg`
      P/Invoke and `SafeSocketHandle` adoption in an AOT node).
    - [ ] W9 BenchmarkDotNet against the pre-W baseline, allocation tests, the 10k-bot E2E, review rounds; the hot
      restart pause measured end to end through W6's `{cluster}-restart` (BenchmarkDotNet `HandoverPause`, E2E with
      clients connected; targets in ADR-098: 0.1–0.3 s, 0.3–0.7 s at 1,000,000 actors); the link's system calls per
      message and fragment interleaving against the pre-W baseline (W4's `LinkBenchmark`); E2E `RegionBlipReconnectStorm` (every client of a region drops and reconnects
      within the grace: presences survive, admission and back-off keep the reconnects bounded); the GC settings
      (Server GC with DATAS off against the default, drafts/deployment.md "Garbage collector settings") measured with
      the 10,000-bot E2E (broadcast p99, working set), DATAS kept off only if it wins; the AOT image is checked to
      honour it (no runtimeconfig.json there: `GcAdaptationModeHonouredUnderAot`).
    - [ ] W10 (last, after W12 and N9, which still point into the drafts) Promote what is left of the drafts into
      design.md and decisions.md and delete them (each step before already moved what it landed, rule above). The
      records that describe today's model and are rewritten or removed with them, from architecture.md section 9:
      ADR-011 (shard and world: the shard internal, "world" public, ADR-086), ADR-012 (registration), ADR-017 (one
      project per server), ADR-019 and ADR-047 (`Publish` deleted), ADR-023 (gossip membership and seeds replaced by the membership table), ADR-024,
      ADR-036, ADR-050 and ADR-051 (sessions, tickets and grants become presences and client tokens), ADR-025 and
      ADR-029 (client groups: full subscription sets, pushes only to gateways with members), ADR-026 (the connection
      age optional), ADR-030 (gateways and stateless groups only: no RBAC for nodes, one namespaced Role for the
      rollout Job; one step only; no seeds), ADR-031 (pulses no longer the failure detector: probes and suspicion
      votes), ADR-033 (`IdleTimeout`), ADR-037 (lifecycle tags), ADR-038 (`[Message]`), ADR-043
      (typed ids become actor types), ADR-052 (packages), ADR-055 and ADR-056 (the Simulation package and DAS010
      retired), ADR-058 (`SendAfter` becomes `Due`), ADR-061 (systems may ask), ADR-064 (keep-majority replaced by suspicion votes
      and death declarations in the membership table), ADR-065 (rotation without restarts: nodes reload the key
      files live), ADR-066 (no error setting: a persisted root reloads, others continue), ADR-067 (no reloadable logic
      project), ADR-068 (its "Today" bullet; the claim without its `INSERT` branch), ADR-072 (handoff becomes migration
      bytes, asked for by `HandoffQuery` before the target claims), ADR-073 (`IPlaced` and DAS016 gone), ADR-079 (the
      coordinator and the slot table derived from the membership table), ADR-081 (handlers, `Context`, sessions, lifecycle); CLAUDE.md (Layout: one Server project with several
      server types; the orchestration rule: what DaServer.Aspire adds now, StatefulSets, the signing Secret mounted as
      files, the Jobs, the rollout Role, NetworkPolicies, pod security; `replicas` owned by the autoscaler and the
      rollout's field manager, ADR-094) and operations.md ("Publish and deploy", "Rotating the signing secret").
    - [ ] W11 Kubernetes shape and the rollout (ADR-085, ADR-094, [drafts/deployment.md](drafts/deployment.md)
      sections 2, 5, 10 and 11): `DaServer.Aspire` publishes actor-hosting groups as StatefulSets with
      `updateStrategy: OnDelete` (today a Deployment: `DaServerClusterExtensions` `AddNodes` and its `Kubernetes` hook
      set `Deployment.Spec.Replicas`), `serviceName` the headless `{group}-peers` Service, `podManagementPolicy:
      Parallel`, every pod two containers `node-a` and `node-b` (the same image and configuration, a probe port each, the
      idle one ready while it runs, the same memory limit, the parked one's request small) sharing a small `medium:
      Memory` `emptyDir` for the handover directory (ADR-098), no preStop `sleep`; gateways as Deployments with
      `maxSurge: 100%` and `maxUnavailable: 0` (surge then drain), the rebalancing parameters on
      `WithClientConnections` (ADR-089; the optional `maxConnectionAge` off by default); stateless groups with
      `maxUnavailable: 0`; `{group}-service` with the HTTP port only; a public `Manifest` type for objects Aspire has
      no type for (Job, PodDisruptionBudget, NetworkPolicy, VPA, `ScaledObject`) and a helper naming each group's scale
      target (StatefulSet or Deployment), replacing the samples' `Publishing.DeploymentOf`. The samples' autoscaling
      through their hooks: the helper that adds an HPA or `ScaledObject` also clears the workload's `replicas`; the
      mmo's stateful groups on KEDA over the average busy ratio with `minReplicaCount` at least 1, the flash sale's HPA
      with `behavior.scaleDown` of one pod per 40 s or more and a 600 s window, VPAs on memory only for gateways and
      stateless groups and `Off` on hot-restart groups (the rollout reads their recommendation), PDBs on the
      StatefulSet's pods (gateways by percentage) with `unhealthyPodEvictionPolicy: AlwaysAllow`. Compose gateways roll
      plainly in the same loop (no surge). `aspire do {cluster}-restart` starts DaServer's rollout Job, the Kubernetes
      adapter of W6's loop (pre-pull, resize the parked container's CPU and memory requests up to the node size in place
      through the pod's `resize` subresource, memory from the larger of the chart's node size and the VPA's
      recommendation, and refuse the arm when it does not fit, arm, apply the parked container's image one pod at a
      time with Server-Side Apply as `daserver-rollout`, never the template, wait for the handover, resize the old
      container's requests down to the idle size; for a same-image roll ask the node to have the parked instance exit;
      a resize-only run (`--type --node --incarnation --resize-only`) that sets both containers' requests by role after a
      crash takeover (the `node.lock` holder node-sized, the other idle-sized, the holder re-read before each patch,
      `ResizeRereadsLockHolder`), in a per-node concurrency group; every rollout re-asserts the sizes by role after
      each handover (`RolloutReassertsSizesByRole`); scale-in of the group paused while it runs and
      released at the end), with its ServiceAccount and Role (pods, `pods/resize`, `get` on VPAs, and `patch` on HPAs
      and `ScaledObject`s); hot-restart pods render `node-a` node-sized and `node-b` idle-sized, with the processor count
      a literal of the node size; it mounts the `{cluster}-admin` Secret and signs with the `act` scope, never the
      signing secret; the rollout restarts the coordinator last. The
      upgrade note tells how to hand `replicas` to the autoscaler on a running group. E2E on kind: same pod UID and IP,
      only the parked container restarted by the image apply, the handover over the `emptyDir`'s socket, clients
      connected, the rollout order (coordinator last), `PodReadinessIgnoresIdleContainer`, `ParkedContainerRestartsAfterHandover`,
      `JobKilledBetweenArmAndPatch`, `SameImageRollRereadsConfig` (the parked instance told to exit restarts and reads the
      new `envFrom`), `SameImageRollTwiceWithin10Min` (the kubelet's back-off delays only the parked instance's return),
      `PrePullRefusedForRoomWarns`, `RolloutPatchesDigestAndChecksImageId`,
      `StatefulSetImmutableFieldsUnchanged` (publish test against the previous chart), `ParkedResizeRefusedFallsBackToRecreate`,
      `ParkedRequestResizedBackAfterHandover` (CPU and memory to the idle size), `StandbySizedFromVpaRecommendation`
      (the larger target of the two containers, never below the node size), `VpaOffOnHotRestartPods`,
      `ColdStartNodeIsNodeSizedContainer`, `ProcessorCountIgnoresResizedRequest`, `ParkedInstanceExportsNoBusyRatio`
      (the HPA's average counts nodes only), `CpuUtilizationHpaUnskewedByParked`, `CrashTakeoverReportsRequestsLow`,
      `ResizeOnlyRunSetsRequestsByRole` (a repeated or late run changes nothing), `ResizeRunPerNodeGroupNeverCancelsRollout`,
      `NodeAsksDashboardForResize` (no pipeline credential on nodes; the Dashboard checks `/health` first),
      `AutoscaledGroupRendersNoReplicas`, `AspireDeployKeepsHpaReplicas`, `RolloutPausesScaleInAndReleasesIt`,
      `RolloutClearsStalePause`, `ScaleInOnePodPerPeriodHandsOverActors`, `RolloutLoopSameAcrossAdapters`, the
      rebalancing tests of W5 on real gateways after a scale-out; `aspire publish` output (Helm lint and template,
      Compose) checked for both workload kinds.
    - [ ] W12 Deployment corrections ([drafts/deployment.md](drafts/deployment.md) sections 3, 4, 6, 7 and 10): one
      `{cluster}-signing` Secret (keys `DaServer__SigningSecret`, optional `DaServer__AcceptedSigningSecret` for
      rotation) mounted whole as files at `/run/secrets/daserver` and read through the key-per-file configuration
      source over a polling file provider (keys
      replaced between two batches, the key fingerprint in `/admin/membership`, the `{cluster}-rotate-secret` command
      waiting for every node's fingerprint), or an existing one through `WithSigningSecret(existingSecretName)`, and
      published nodes refuse to start without it; Compose secret files readable by the node's uid (0444, or 0440 with
      group 1654); Compose `secrets:`
      mounted at the same path and an `internal: true` network for the cluster and HTTP planes; the schema Job (W6's
      `--DaServer:Migrate`, Compose's one-shot service) with an owner role (`CREATE` on the schema, every object
      schema-qualified), nodes on a DML-only role (the record and membership tables), every node group given the store's
      connection (gateways included: membership is there), the samples' role-init step before the schema Job (on
      Kubernetes the schema Job retries until the role exists), the Compose deploy order (`up -d --wait postgres`, then
      `up --no-deps --force-recreate --exit-code-from schema schema`), no Helm hook; the published PostgreSQL (16 or later) `Strict` within its failure domain (ADR-101, `SafeFailover` and
      `DaServer:Store:FailureDomain` set by DA): on Kubernetes a primary and two standbys under `ANY 1` in three zones with
      quorum-aware failover (`zones`; one standby's maintenance pauses nothing, the samples adding a PodDisruptionBudget
      through the publisher hook; writes pause without a standby), on Compose one crash-safe instance with no
      replication or failover (`host`; the disk's or host's loss not covered, backups the operator's); the local
      `aspire run` single node exempt, the roles step setting `client_connection_check_interval`, `idle_in_transaction_session_timeout`, server keepalive
      and `tcp_user_timeout` (`HalfOpenBeforeCommitReleasesLocks`) and,
      on 17 and later only (`server_version_num >= 170000`), `transaction_timeout = 0` (a run on PostgreSQL 16 passes),
      the schema Job ending with a cleanly committed schema comment, the Dashboard's audit intent committed before its
      action row, `Target Session Attributes=read-write` on every data source DA wires;
      the upgrade check a plain Job started by the deploy step (N9); load generators (`bots`, `crowd`) as Jobs or left
      out of publish (Compose `profiles`); `DOTNET_PROCESSOR_COUNT` from `requests.cpu`; startup probes and 2 s probe
      timeouts; no service-account token on node pods; non-root, read-only root filesystem (writable `/tmp` and the
      handover directory); NetworkPolicy (admin plane closed to the cluster network, open to the Dashboard and the
      rollout Job); topology spread; Server GC with DATAS off in the published images (W9 decides); published OTLP
      pointed at the Collector (the samples publish no Aspire dashboard); the `checksum/config` annotation; the memory
      limit followed at run time: a node reads its cgroup `memory.max` and calls `GC.RefreshMemoryLimit()` when it
      changes (a refused refresh logged), the samples' in-place resize set to `memory: NotRequired` with VPAs on memory
      and a memory limit on stateful groups (ADR-094). Tests: `tests/DaServer.E2E/PublishTests.cs` over the published
      Helm chart and Compose file, `MemoryLimitResizeRefreshesGcLimit` and `MemoryLimitResizeRefreshesGcLimitUnderAot`
      (kind, in-place resize, JIT and AOT images), `CpuResizeKeepsWorldCount`, `DashboardSurvivesRotation`,
      `TemplateChangeBeyondImageRecreatesPod` (a VPA-resized pod still patches in place), `ComposeGatewayRollKeepsClients`, `ComposeHandoverVolumeWritable`, `NodeRefusesStartWithoutSigningSecret`, `SecretRotationWithoutRestart`,
      `RotationWaitsForFingerprintOnEveryNode`, `RejectEmptyKeyOnReload`, `NetworkPolicyAdmitsDashboard`,
      `NetworkPolicyAdmitsReferencingResources`, `NetworkPolicyAdmitsRoleInitToStore`, `ComposeDeployWaitsForPostgresThenSchemaExitCode`
      (a failing schema stops the deploy), `SchemaJobRetriesUntilRoleExists`, `GatewayPodsReachStore`,
      `ComposeSecretReadableByNode`, `LaunchdThrottleIntervalOne`, `PublishedStoreReplicatesSynchronously`, `ComposePublishesStrictDatabase`
      (the Compose file's one `postgres` runs with `fsync`, `full_page_writes` and `synchronous_commit` on and no
      standby, and the nodes start `Strict` with failure domain `host`), `SingleInstanceWithoutSafeFailoverIsLocal`
      (an undeclared single instance never claims `Strict`), `StartupLogNamesFailureDomain`.
14. N9 hot restart's remaining operator side, on W's model (draft §10; ADR-084, ADR-098), after W12 and before W10: the
    `{cluster}-upgrade-check` Job and `aspire deploy` step, a plain Job that calls a running node's dry-run arm
    (`/admin/restart?dryRun=true` with the new manifest, `ManifestDiff`), including the rollback-safety check (a release whose new
    fields or new record types a rollback would drop is refused unless forced) and the table check (a table whose
    format hash changed must load its current version with the new build's `TryLoad`); configuration tables with one
    cluster-wide current version (name, version, hash, bytes) in the store, loaded by every starting or hot-restarted
    node, seeded from `DaServer:Tables:{name}` by the first reader when the store has no version (a new cluster or a
    release adding a table type), replaced by `POST /admin/tables/{name}` (a rollback posts the old version), a node
    whose `TryLoad` rejects the current version failing readiness with the error; without a store, tables per node
    from configuration and a `POST` changing only that node (documented); and feature flags, data swaps between two
    ticks. The manifest diff, the dry-run arm and the rollout loop are W6, the Kubernetes adapter W11. Gateways take
    rolling upgrades; handing their listening sockets to the new process (`SCM_RIGHTS`, Unix only) stays a possible
    later optimization. (in-house game servers, ET, Envoy hot restart) Tests:
    `UpgradeCheckRefusesUnkeepableFieldsUnlessForced`, `UpgradeCheckRefusesNewRecordTypeUnlessForced`,
    `RollbackKeepsNewFieldsOrRefuses`, `UpgradeCheckRefusesUnloadableTableFormat`, `TableUpdateReachesEveryNode`,
    `NewTableTypeSeededFromConfiguration`, `RejectedStoredTableFailsReadiness`, `TablesPerNodeWithoutStore`, `StartingAndRestartedNodesLoadCurrentTable`, `InvalidTableVersionRejected`,
    `TableRollbackPostsOldVersion`, `FeatureFlagSwitchesBetweenTicks`.
15. N4 route completeness, N11 generated filters (on the G filter point), K3-K5, open cleanup, quality items. Later
    optional module: leases for strict singletons.
16. After the self-review converged (agreed): Z3 and Z4; P2 and P3, the Redis/Valkey and MongoDB store providers (the
    items under "One message model" below; ADR-102).
EasyEcs in parallel when DaServer waits on benchmarks: the Friflo gap, comparison tables in the docs.

## Runtime (`src/DaServer`)
- [x] Protocol: 24-byte frame header, frame kinds, RPC status codes
- [x] Buffers: POH `BufferPool`, pooled `FrameWriter` (over Nino's `NinoArrayBufferWriter`, ADR-078)
- [x] Concurrency: padded MPMC queue, object pool, allocation-free `AsyncSignal`
- [x] Net: `Connection` (batched send, zero-byte reads, backlog bound), `Listener`
- [x] RPC core: messages (`IRequest<TReply>`, `HandlerRegistry`), `CallContext` replies, pending-call table, pooled awaitables
- [x] Shard runtime as ECS (ADR-013, ADR-081): actor entities (`ActorKey`, lifecycle tags, a `DynamicBuffer<Envelope>`
      mailbox used while the actor is not live), runtime systems, system groups; dispatch queue (`DispatchSystem`,
      `DispatchQueueCapacity`) (ADR-040)
- [x] Actors as static `[Actor<TId>]` classes of `[Handle]` methods over components, one message at a time, handlers
      that never wait (answers as `Answered` mail); stateless `[Service]` classes (ADR-081)
- [x] Typed actor ids (ADR-043): `[Actor<TId>]`, `Send`/`Ask(id, m)` route by the id type, `Publish` to services only
- [x] Fence a node declared dead: its actors are removed without their `Unloading` mail (ADR-042, from Orleans)
- [x] Node: shards, local/remote routing, forwarding/relay, reply sinks
- [x] Cluster: push-pull gossip of pulses (cards once, ADR-031), seeds (static + DNS), failure detection, `ClusterView`, rendezvous placement,
      graceful leave + rebalancing
- [x] Gateway: sessions, per-handler-type bindings, per-message `[FromClient]` access, push, `ClientGroup` broadcast fan-out
      (O(1) membership, `All` is a group, no allocation per broadcast), `SessionClosed` to bound actors, client frame
      limit (ADR-025)
- [x] Runtime-owned sessions: Bind joins the actor's group, an actor's known sessions are child entities, a crashed
      gateway's sessions close (ADR-024)
- [x] Clients connect through the platform's load balancer and stay; `MaxConnectionAge` moves old connections to new
      nodes, the clients resuming their sessions (ADR-026)
- [x] One ordered link per node direction: replies travel with binds, pushes and events (ADR-022)
- [x] Placement: keys stay with suspect members, a dead node's keys move, unloading actors keep their mail (ADR-023)
- [x] Client: `ClusterClient` (the same messages as servers, session-bound actor messages without an id, DNS
      endpoints over every resolved address, entry addresses, callbacks
      inline or queued)
- [x] Topology without roles (ADR-017), `ICluster` for business code (ADR-020), actor `Group`
- [x] One-way and published messages: `Send`, `Publish` to every handling service, gossiped handlers, mailbox
      delivery (ADR-019)
- [x] Backpressure: bounded dispatch queues and actor mailboxes refuse with `Overloaded`; links are never paused
      (ADR-027)
- [x] Deadlines: the caller's remaining time travels with each request; expired work is dropped (ADR-028)
- [x] Resumable node links: a dropped connection replays what the peer did not process (ADR-008)
- [x] Actors unload when idle (`UnloadAfterIdleSeconds`, ADR-033)

## Now: zero GC (agreed; Z2 is step 1 of the development order)

Steady state allocates nothing: per message, call, reply, tick, broadcast, connection, gossip round and timer.

- [x] Z1 Audit every runtime path (src/DaServer, Hosting, generated dispatchers and `Ask` overloads, what samples
      teach): `TaskCompletionSource`/`Task`/timers/`CancellationTokenSource` per operation, unpooled awaitables, async
      state machines, closures and per-call delegates, LINQ, boxed enumerators, `params`, string formatting and
      logging without guards, boxing (structs in object fields), per-operation arrays/lists/records, Nino collection
      and string deserialization per message (N8), gossip/membership rebuilds, resume tickets, metrics.
      Findings (audit of this branch, fix order below):
      - H1 user `async ValueTask` handlers used the default builder: one state-machine box per suspended call. Fixed:
        handlers never await (ADR-081); code outside the shards awaits the pooled `Reply<T>` (ADR-062).
      - H2 `CallContext.AwaitReply` (pooled builder hits its per-thread cache cap; `ConfigureAwait(true)` allocates a
        context continuation per await). Fix: pooled `ReplyWatcher<T>`.
      - H3 pool caps below in-flight bounds (PendingCall<T> 1024, RelayCall 1024, FrameWriter 4096): bursts allocate
        and drop. Fix: per-shard-thread stacks for PendingCall, caps 64k (configurable) for frames and relays.
      - H4 Nino collections/strings per message (`Motion` list per client, strings in Say/Chat/replies). Fixed by N8
        for collections (`[NinoPooled]` holders the handler returns); a string is a new string per message (low
        volume: chat and logins; Nino writes ASCII text one byte a char).
      - M1 error paths allocate exactly under overload (interpolated NotHosted/Overloaded strings, `RpcException` per
        timeout). Fix: status-only frames, lazy exception message, non-throwing status results (N3b).
      - M2 `GroupMembers` created/removed per bind and AOI join/leave. Fix: pool + lazy removal from the maintain timer.
      - M3 `Task.Delay` + async per lost connection (grace) and boxed tuples per shard on gateway loss. Fix: timer heap
        drained by the maintain timer, cached state objects.
      - M4 per actor load: actor object, `Queue<Envelope>`, two HashSets. Fix: per-shard pools refilled on removal.
      - M5 `ClusterClient` pending table 2^14 (≈917 KB LOH per client). Fix: 256 by default, configurable.
      - M6 dispatch queue overflow `ConcurrentQueue` segments. Fix: second bounded ring for completions/callbacks.
      - M7 resend window chunks exceed the 64 KB pool budget across backlogged links. Fix: per-window free list.
      - M8 string concatenation on hostile client rejects. Fix: constant/precomputed messages.
      - L1-L7 rare (view rebuilds, binding arrays, client sweeper enumerator, metrics tags, reconnects, foreign awaits,
        local Send copy): fix where trivial.
      Allocation tests still missing (they come with the items that change those paths): grant churn, the client
      callback queue, collection messages (N8).
- [x] Z2 Core fixes: H3 (per-shard call stacks, 64k frame/relay pools), H2 (pooled `ReplyWatcher<T>`; tests build
      optimized so async state machines are structs as users ship them), M1 (status-only refusals, ADR-049), M6
      (completions and callbacks in their own queue, ADR-027), M4 (actor churn allocates nothing: an actor is an
      entity, ADR-044), M7 (a resend window recycles up to 16 released chunks), L7 (a local `Send` hands its frame over
      instead of copying it). New allocation tests: calls to another node, service Send/Ask/Publish (local and
      remote), refusals, relayed timeouts, actor churn, resend window reuse, idle nodes. Kept: L1 view rebuilds and
      L5 reconnects (per membership change), L4 metric sampling (a few hundred bytes per collection, only with a
      listener), L6 foreign awaits (a closure per await in a foreign UI-style context). H1 comes with N3b, H4 with N8,
      M2/M3/L2 with G step 6, M5/L3 with G step 8.
- [ ] Z3 Prove it: an allocation test per hot path the audit finds uncovered (0 B per operation after warm-up); the
      BenchmarkDotNet A/B against the baseline is done (G step 12). Done: calls from outside the cluster (thread-pool
      callers rent a call on one thread and return it on another; the shared pool held 1,024, so bursts above it
      allocated: 29 -> under 1 B per call, `BurstsOfCallsFromOutsideTheClusterDoNotAllocate`); the benchmarks' own
      harness allocates nothing (pooled `ValueTask` workloads) and their actors are `[Sticky]` (load balancing moved
      "remote" actors onto the caller mid-run). Every call, hot-actor and timer benchmark now reads 0-3 B per
      operation; what remains in the longest ones is the process background (about 1 KB/s). Scenario benchmarks to add: a hot
      actor (many concurrent callers on one key: the flash-sale shape), a burst beyond the mailbox and dispatch
      bounds (refusal cost and time to drain), a dense area-of-interest cell (broadcast to one crowded cell). Surges
      over time (p99, refusals, recovery) are load tests: `Sample.Bot` for crowded maps, `FlashSale.Crowd` (client
      protocol) for flash sales.
- [x] Z5 Client writes on a beat (ADR-070): `ClientConnectionOptions.FlushInterval` (off by default; the sample uses
      2 ms) writes each session once per beat from one flush thread, directly, without waking send loops; a full socket
      hands the rest to the connection's carry loop. Clients keep their receive buffers (no zero-byte read per push);
      node-side sessions still release idle ones. The 10k-in-one-map scenario on one 4-core VM shared with the bots:
      p99 1,010 ms with drops -> 432 ms (client buffers) -> 163 ms with a 2 ms beat, no drop or gap; through Aspire
      (`tests/DaServer.E2E`, 10,000 bots, one map, 60 s): E2E OK, p50 75 ms, p99 199 ms, six nodes exit 0. Rejected on the
      way, with numbers in ADR-070: writing at the end of a batch on the calling thread, per-core resume queues
      (Kestrel's IOQueue), io_uring (not in .NET 11), a beat split across the thread pool (faster on idle cores,
      p99 up to 795 ms on saturated ones).
- [ ] Z4 Keep it: CLAUDE.md rule plus analyzer coverage where a misuse is detectable in user code (e.g.
      `TaskCompletionSource`/`Task.Delay` in handlers and systems: use pooled framework awaitables and `SendAfter`).

## One message model, built-in ingress and client connections (G0-G2 and G5 done; browser clients in W7b)

One framework (ADR-045, ADR-048): ingress (`DaServer.Ingress`) and client connections (`DaServer.Clients`) are
built-in capabilities on public feature points, off unless configured; business domains live in samples. Load
balancing, TLS, CDN/WAF, Gateway API routes and autoscaling stay with the platform.

- [x] G0 One message model (ADR-047), steps 1-4: `Send`/`Ask`/`Publish` plus addresses (`IActorId` keyed,
      `INodeAddress` pinned, `IGroupAddress` fan-out; scope = `TypeId<TAddr>`, kind a JIT constant per address type);
      `Push`/`Broadcast` are sends to session / group addresses, `JoinGroup`/`LeaveGroup`/`Disconnect` messages to
      them, `BindSession` an `Ask` of `SessionAccess` (removed verbs, no aliases). Wire: `Message`/`Reply`/`Control`, `Target`
      always set, trailers by flag (`Origin` 16 B, call id; `Trace` with A6). Wire break, no rolling
      upgrade across it (pre-1.0). A/B of the G work against the baseline: G step 9 below and the README.
- [x] G1 Feature points and ingress, step 5, proven by two consumers (client connections and the HTTP plane).
- [x] G2 Client connections as a built-in feature, grants, packages, keep-alive, .NET 11, tests, docs: steps 6-10.
- [x] G5 `samples/flashsale`: buyers over the client protocol (TCP or WebSocket) to a stateless `Gate` service on
      every node; 16 `StockShard` actors per sale (a handler is the atomic decrement), `Order` actors keyed (user,
      sale) so retries get the same answer, the `Sale` actor collecting the shards' buyers so each shard's gate answers
      the late crowd itself; an optional queue (`PerSecond`, `AdmissionSystem`). `FlashSale.Crowd` is a
      `DaServer.Simulation` swarm (`MeasureUntilAsync`); AppHost, E2E (100,000 buyers in CI); the README compares it
      with the usual designs. One 4-core VM with the crowd on it: 3,000,000 buyers on one node, 6,000,101 requests in
      13.2 s with requests on a 1 ms beat (548,000/s steady, 1.51 node cores; ADR-070); the machine is the limit.
- [ ] P2 `DaServer.Persistence.Redis`, the Redis and Valkey provider of `IStore` (ADR-102, architecture.md section 4),
      declared `Replicated` (ADR-104): commands common to Redis 7.2+ and Valkey 7.2+, a Functions library loaded by
      `Migrate`, a root's keys hash-tagged into one slot and a guarded write always one function call however large
      (never `MULTI`), ordered scans through per-prefix sorted sets, the `Replicated` preset (`appendfsync always`,
      `WAITAOF 1 1 0`, `min-replicas-to-write 1`, `noeviction`, two replicas), `MaxValueBytes` 512 KiB; Sentinel first,
      certified by the kit (layers 1 to 3 and 5, layer 4 for `Replicated`); Cluster mode only once the kit passes on it
      (the expiry's ordered scan across slots and the indexes in other slots are open, architecture.md section 4).
- [ ] P3 `DaServer.Persistence.MongoDb`, the MongoDB provider of `IStore`, declared `Strict` with majority writes on
      a replica set (no `wtimeout`): one collection keyed by the key bytes, a guarded write as one document update or a
      transaction, `bulkWrite` batches, `_id` range scans, `$$NOW`, `Confirm` by a majority read,
      `UnknownTransactionCommitResult` as `Unknown`; certified by the kit for `Strict`.
      MySQL and its family stay with the community through the kit.
- Not in the framework: waiting rooms (virtual queues for human surges). They are policy for one kind of traffic,
  belong at the very edge (CDN/SaaS) to protect origins, and compose from ingress primitives (admission filters,
  token bucket, load-aware refusal) plus a queue actor; `samples/flashsale` shows the pattern.

What G delivered (each step kept every test green):

0. Baseline BenchmarkDotNet: Rpc, Message, Broadcast (1k/5k x 1/9), Scenario (3k/9k) on d676d2a.
1. Wire: `Target` always set, call-id trailer rewritten per hop, 3 frame kinds, receiver dispatch by one `Target`
   lookup (handler type -> shard, session/group scope -> client connections).
2. Addresses: `IAddress`/`INodeAddress`/`IGroupAddress`, `Send(addr, m)`, address-generic generated `Ask` overloads;
   DAS006 checks `[ToClient]` by address kind; asking a session or group and a client addressing anything but actors
   are DAS008.
3. `Origin(ulong Node, long Id)`: raw 16-byte trailer, `CallContext(ReplyTo, CallId, Origin)`, `ctx.Caller`;
   the origin is an `INodeAddress` (`ctx.Send(ctx.Caller, m)`) and `ClientSession` converts implicitly from it.
4. Session operations are messages to `ClientSession` (`JoinGroup`/`LeaveGroup`/`Disconnect` to a session or a group;
   `BindSession` asks a `SessionAccess`, so ADR-022 ordering holds).
5. Feature points and ingress (`DaServer.Features`, `DaServer.Ingress`): `NodeFeature` hooks, options bound from
   `DaServer:{name}` and enabled by configuration (`AddDaServerFeature`), `INodeContext.AddGauge`, pulse slots and
   card entries, `HandleAddress<TAddr>` with scopes in the card and group fan-out by scope, `IIngress` (raw and
   typed, origin, capped deadline, `IngressLoadLimit` early refusal), admission (`IIngressFilter`, `RateLimited` with
   a retry hint, `TokenBucket`, `SignedToken`), `TimerWheel<T>` (hashed wheel, one owner, O(1),
   allocation-free), dispatch-stage message filters (`MessageFilters.Register<T>` / `Register(IMessageFilter)`, run
   by generated dispatchers; N11 generates into them), the HTTP plane minimum (nodes on
   `WebApplication`, Kestrel on `DaServer:HttpPort`, `MapAsk`/`MapSend` through the ingress, statuses to
   429/503/504/403 with `Retry-After`, `DaServer-Budget` header; one origin id space per node for sessions and HTTP
   requests).
6. Client connections as a feature (`DaServer.Clients`, enabled by `DaServer:Clients`): generic card entries and
   pulse slots (`ClusterMember` keeps no client fields), the actor side in `ActorSessions` (ADR-050), client entry
   addresses (ADR-026), M2 (group objects pooled, empty
   groups removed lazily: group churn allocates nothing), M3 (resume graces on a `TimerWheel`), a per-session rate
   limit (`ClientConnectionOptions.MessagesPerSecond`/`Burst`), WebSocket transport (`WebSocketChannel` behind `IFrameChannel`,
   `/clients/ws`, `ClusterClient.ConnectAsync(Uri)`). L2 kept: a grant change copies the session's small grants
   array (a login or map change, not per message). Moving clients off a draining node is D3 (retired in waves, make
   before break).
7. `[FromClient]` / `[ToClient]` on `[Message]`; grants (ADR-051): `GrantSession`/`RevokeSession` admit a session to an
   actor, the edge refuses any other named actor; resuming asks the granted actors (ADR-036).
8. Packages (ADR-052): `DaServer.Abstractions` (contracts) <- `DaServer.Core` (shared wire and transport) <-
   `DaServer.Client` / `DaServer` (server runtime); the first three build for netstandard2.1 and net11.0, and
   `samples/mmo/Sample.Contracts` builds for both. Generators split by output (ADR-012): `AskGenerator`,
   `RegistrationGenerator` (static-constructor registration, IL2CPP-safe), `PolyfillGenerator`. M5: call tables allocate slots in doubling chunks as calls in flight reach them
   (`ClusterClient(maxPendingCalls: 4096)` is only a bound), connections with `ReleaseIdleBuffers` hold no send
   buffer when idle, clients share handler tables: 10k sample bots use 182 MB resident / 80 MB managed. Aspire:
   `AddNodes(...).WithClientConnections()` and `WithClientEndpoints(group)` (`DaServer__ClientEndpoints__{i}`).
   Names: `ClientSession(Node, Id)`, `NodeOptions.SigningSecret`, contract types in the root `DaServer` namespace,
   `HandlerRegistry`, `ClusterClient`.
9. .NET 11 (ADR-054): the server runtime targets net11.0 (local calls 2.3-2.7x faster under concurrency, remote
   1.1-1.3x; messages 1.6-3x).
10. Generator cost (`GeneratorBenchmark`: 1,000 messages, 250 handler types, 5,000 call sites): symbol checks compare
    names in place, requests and handlers are found through the compiler's attribute index
    (`ForAttributeWithMetadataName`), a handler type is read once: full run 36.0 ms (13.0 MB), an unrelated edit
    9.8 ms (2.4 MB). The analyzer costs what an empty analyzer watching the same symbols and invocations costs
    (720-750 ms, the compiler's binding).
11. Keep-alive (ADR-053): `Ping`/`Pong` when a direction sent or received nothing for an interval, silence closes
    client sessions (45 s), client connections (30 s, `CheckConnection` probes) and node link connections;
    `KeepAliveTests`.
12. Tests: `ArchitectureTests` (package references point down only; the runtime's folders use no client-connection
    type); allocation tests (< 1 B per delivery hold); A/B against step 0, d676d2a on .NET 10 vs the G head on
    .NET 11, back to back on one machine (BenchmarkDotNet default job):

    | Case | d676d2a | G head | |
    |---|---:|---:|---:|
    | 1 call local / remote | 31.5 / 121 us | 20.2 / 101 us | -36% / -17% |
    | 64 calls local / remote | 480 / 377 us | 218 / 335 us | -55% / -11% |
    | 1,024 calls local / remote | 4.16 / 2.22 ms | 1.88 / 1.67 ms | -55% / -25% |
    | messages local / remote | 5.10 / 1.78 us | 620 / 981 ns | -88% / -45% |
    | broadcast 1k x 1 / 1k x 9 | 5.37 / 5.81 ms | 5.35 / 5.86 ms | 0 / +1% |
    | broadcast 5k x 1 / 5k x 9 | 25.1 / 28.2 ms | 26.2 / 28.9 ms | +5% / +2% |
    | scenario 3k / 9k | 14.9 / 44.0 ms | 13.2 / 47.2 ms | -12% / +7% |

    Allocated per operation: equal or lower everywhere (messages 0 on both).

Open after G:

- [x] Trim `BufferPool` buckets back after a peak (41 MB of a 10k-bot process's 80 MB managed heap were 4 KB buffers
      kept from the all-sessions broadcast peak): every 60 s each bucket releases half of the buffers it held through the
      whole period without lending them. A bucket is a FIFO queue, so those are exactly the ones queued before the last
      trim that its renters' cursor has not reached since (no bookkeeping on rent: a first try that tracked a low-water
      mark on every rent cost 26% on four threads); buffers in use, even in rare bursts, never go
      (`APeaksBuffersGoBackHalfPerTrimWhileUsedOnesStay`; `PoolBenchmark.Buffers` unchanged).
- [x] Group broadcasts on .NET 11 against d676d2a on .NET 10: -9% to +8% (the release checklist's run), inside
      the runs' spread; nothing to profile further.
- [x] Release checklist recorded on 7c5b50b (October 2026, one 4-core VM): build 0 warnings; 395 tests pass (32
      skipped: PostgreSQL ones without `DASERVER_TEST_POSTGRES`, which CI sets); topology strings only in AppHosts and
      doc comments; the 10,000-bot E2E (one map, 60 s, lean bots): E2E OK, 2.4M broadcast samples, p50 75.8 ms, p99
      208.9 ms, p99.9 278.5 ms, no gap or disconnect, six nodes exit 0. BenchmarkDotNet default job, d676d2a on
      .NET 10 then the head, back to back:

      | Case | d676d2a | head | | Allocated d676d2a -> head |
      |---|---:|---:|---:|---:|
      | 1 call local / remote | 30.3 / 132 us | 5.17 / 85.9 us | -83% / -35% | 120 / 175 B -> 1 / 0 B |
      | 64 calls local / remote | 403 / 357 us | 169 / 315 us | -58% / -12% | 174 / 159 B -> 0 / 0 B |
      | 1,024 calls local / remote | 3.98 / 1.91 ms | 0.78 / 1.05 ms | -81% / -45% | 124 / 174 B -> 0 / 1 B |
      | messages local / remote | 4.62 / 1.58 us | 454 / 730 ns | -90% / -54% | 0 / 0 B -> 0 / 0 B |
      | broadcast 1k x 1 / 1k x 9 | 5.35 / 6.33 ms | 5.21 / 5.78 ms | -3% / -9% | 277 / 277 B -> 7 / 7 B |
      | broadcast 5k x 1 / 5k x 9 | 29.0 / 30.1 ms | 26.5 / 32.5 ms | -9% / +8% | 280 / 280 B -> 50 / 46 B |
      | scenario 3k / 9k | 15.3 / 48.4 ms | 15.5 / 52.0 ms | +2% / +8% | 142 / 152 B -> 16 / 50 B |

      Allocated per operation: calls 120-175 B -> 0-1 B, broadcasts 277-280 B -> 7-50 B, scenario 142-152 B ->
      16-50 B. Every case is within the 10% bound; the two +8% cases are inside one standard deviation of either side
      (2.6 and 4.2-5.3 ms). The 2 ms flush beat (head only, off by default) trades latency for CPU on idle cores, as
      ADR-070 records.

## Next: graceful shutdown and no data loss

Detection: the Generic Host turns SIGTERM, SIGINT (Ctrl+C) and SIGQUIT into `IHostApplicationLifetime.ApplicationStopping`
and calls `IHostedService.StopAsync` with a token that fires at `HostOptions.ShutdownTimeout` (30 s by default);
after it the process exits whatever is still running. Platforms send SIGTERM and kill later: Kubernetes after
`terminationGracePeriodSeconds` (30 s default), Docker Compose after `stop_grace_period` (10 s default). SIGKILL,
OOM kills, crashes and power loss give no signal at all, so no shutdown code can be the only protection.
- [x] D1 One shutdown budget (ADR-063): `NodeOptions.ShutdownTimeout` (25 s) bounds `StopAsync`, which honors the
      host's token; the host waits 5 s longer, Compose `stop_grace_period` is the budget + 10 s
      (`DaServerNodeGroup.WithShutdownTimeout`). Past the deadline the node logs, per shard, the actors not unloaded and
      saved yet, and exits instead of hanging.
- [x] D2 Ordered stop: readiness off (`/health` fails once Leaving) -> Leaving (callers reroute) -> drain features
      (client connections) -> unload every actor -> close links. Each phase is logged with its duration and recorded
      as `daserver.stop.phase.duration` (phase tag). Store flush joins with N5 (D6).
- [x] D3 Client-facing nodes drain: the listener closes, clients are retired in waves over
      `ClientConnectionOptions.DrainSpread` (3 s) and move to other nodes, make before break (ADR-026; readiness turned
      the balancer away first); the stop waits up to `RetireGrace` for them. `ClientsOfALeavingGatewayMoveAndResumeOnAnother`.
- [x] D4 Tests: a stop that exceeds its budget reports the unsaved actors and returns
      (`AStopThatExceedsItsBudgetReportsAndReturns`); a leaving gateway's clients keep their sessions; a stop under load
      loses no acknowledged deposit (`AStopUnderLoadLosesNoAcknowledgedDeposit`, 32 callers through the node that stays);
      a kill loses at most the last checkpoint interval (`AKillLosesAtMostTheLastCheckpointInterval`); both stores.
- [x] D5 Persistence that survives a crash (ADR-068): on unload always, a checkpoint every `SaveEverySeconds` when the
      record changed, `SaveNow` for critical changes; versioned saves reject a stale owner's late write.
- [x] D6 Store flush and backpressure: one save batch (at most 512 records) in flight per shard; the stop's unload phase
      waits for every final write (actors are removed only once written); a failing store is retried every second; the
      store's health joins readiness (the Aspire.Npgsql data source's own health check).

## Next: Aspire and hosting best practices (A-items, with D1-D4; checked against Aspire 13.6)

Aspire generates manifests and runs the local cluster; it never autoscales and publishes no grace period, no
Compose health check and no probe except HTTP probes declared with `WithHttpProbe`. What a node needs from it:

- [x] A1 HTTP plane on ASP.NET Core (ADR-046): every node runs Kestrel on a separate HTTP port (`DaServer__HttpPort`,
      Aspire endpoint `http`) next to its own TCP data plane. Official health checks: `daserver-ready` (joined, not
      leaving/draining, store healthy) mapped to `/health`, `daserver-live` (shard stall watchdog, N3b) tagged `live`
      and mapped to `/alive`, always on (not Development-only like the template). `DaServer.Aspire` declares the
      endpoint and readiness / liveness probes (`WithHttpProbe`, ASPIREPROBES001: Kubernetes probes, and the HTTP health
      check local `WaitFor` and the dashboard use). Users map REST endpoints in their node project (admin,
      webhooks, payment callbacks, HTTP login); an endpoint reaches actors only through `ICluster` (Ask/Send post to
      the shard), never shard state.
- [x] A2 Drain budget published (D1): `WithShutdownTimeout` sets Compose `stop_grace_period` and Kubernetes
      `terminationGracePeriodSeconds` (budget + 10 s: the host's 5 s margin and 5 s more) for whichever environment the model publishes to, and on Kubernetes a 5 s
      preStop sleep (counted in the grace period) so endpoint removal propagates before the node leaves.
- [x] A3 Compose: `stop_grace_period`, `restart: unless-stopped` and a `healthcheck` per node. The .NET images ship
      no curl or wget, so the check runs the node itself in probe mode (`dotnet /app/{assembly}.dll
      --DaServer:Probe=/health`: it asks `/health` on its own HTTP port and exits 0 or 1 before anything starts, about
      0.2 s); services waiting for a node group (`WaitFor`) depend on it `service_healthy`, so they start once a node
      joined. Checked in a container built by `PublishContainer`: healthy once joined, 1 against a closed port.
- [x] A4 Publish mode = one workload per node group (ADR-030): a Compose service with `deploy.replicas` or a
      Deployment with `replicas`; a headless `{group}-peers` Service (`clusterIP: None`, `publishNotReadyAddresses` so
      joining pods gossip); a node in a container advertises its own address; run mode keeps one resource per replica.
- [x] A5 Dashboard and admin: nodes map `/admin/membership`, `/admin/stats`, `/admin/drain` and `/admin/unload/{type}` on
      their HTTP plane, authorized by a bearer token the signing secret derives (HMAC, so the token gives no ticket
      away); `DaServer.Aspire` gives every node the commands Membership, Stats, Drain and Unload actors (dashboard,
      `aspire resource`, MCP) and, run locally, a cluster resource parenting its nodes whose state counts the joined ones
      ("6/6 joined"). A node's own state is Aspire's health (ready = joined and not leaving), so no custom node states
      (`AdminEndpointTests`, the E2E runs).
- [x] A6 Tracing (ADR-080): a handler whose message carries a trace context runs in a span of the `DaServer`
      activity source, child of the sender's on any node; asks, sends and publishes carry the running handler's
      context (or an ASP.NET Core request's), an ask's `Answered` mail continues the asking handler's trace, in the frame's extension block, the first entry of a block readers skip unknown entries
      of (new metadata needs no new link protocol). `AddDaServer` exports the traces with the ASP.NET Core and
      HttpClient instrumentation when OTLP is configured, so a REST call and the actor calls it causes are one trace.
      Traces start where the application starts them, never per message (`TracingTests`).
- [x] A7 Store wiring: `group.WithActorStore(postgres.AddDatabase("actors"))` (`WithReference` on the group's nodes,
      `WaitFor` when published, ADR-069), `builder.AddPostgreSqlActorStore()` in the node's Program.cs; the sample keeps player profiles in
      PostgreSQL on a data volume. Clients wait for every node of the cluster (`WithClientEndpoints`).
- [x] A8 Parameters and environments: `DaServerNodeGroup.WithEnvironment(name, parameter)` sets a node option on every
      node of a group from an AppHost parameter, published as a Helm value (`{{ .Values.config.map.map_shards }}`, its
      default with `publishValueAsDefault`) or a Compose variable (`.env`, filled by `aspire deploy` like every other);
      the MMO sample sets its map nodes' shards so, and points at an existing database with `AddConnectionString`
      (`-- ExistingDatabase=true`). Replica counts stay ints of the model (Aspire publishes no parameter there); the
      platform's autoscaler owns them in production.
- [x] A9 Tests through Aspire: the E2E runs wait for the cluster resource to count every node joined and read a node's
      Membership command; the scale-in under load drains the node through its command and starts it again.
- [x] A10 Kubernetes packaging through Aspire's own API, nothing in the framework: the samples' Kubernetes environment
      names its chart, release and namespace (`WithHelm`), and the MMO sample's stateful groups run on a node pool of
      their own (`AddNodePool`, `WithNodePool`: `nodeSelector: agentpool: stateful`). Installing KEDA or the Prometheus
      adapter is `AddHelmChart`'s, for an application that autoscales on custom metrics.
- [x] A11 Pipeline step: `aspire publish`/`deploy` first run the cluster's route check (`{cluster}-routes`, required by
      the publish and deploy prerequisites): each node project starts in check mode (`DaServer__ListRoutes`: it writes
      the messages its contracts declare and its actors and services handle, then exits before anything starts), and
      the step fails with the N4 problems of their union (`RouteRules`, the file `RouteCheck` uses). The listing is the
      node's own build, so it checks what the containers run; the analyzers already run in that build.
- [x] A12 Operator docs (`doc/operations.md`): run, watch (`aspire describe/logs/otel/wait`), the node commands, the
      admin endpoints with the derived token, the MCP server for agents, publish and deploy; every command checked
      against a running sample.
- Not for node membership: `WithReference` service discovery (`services__{group}__{endpoint}__N`: one load-balanced
  address per group; a Kubernetes Service lists ready pods only, and a joining node is not ready until it joined), and
  persistent project lifetimes (no replicas, fixed ports); fine for databases and user HTTP services. Seeds are the
  platform's DNS for each group instead (Compose service names, a headless `{group}-peers` Service listing every pod),
  as in Akka.NET's DNS discovery; Orleans keeps membership in a store instead (`WithClustering`), which DaServer's
  gossip does not need.
- [x] A14 Production views (`doc/operations.md`): a Grafana dashboard (`observability/grafana-dashboard.json`,
      imported into Grafana 12 and every query answered by Prometheus 3.5 scraping a node) and alert rules
      (`observability/prometheus-rules.yaml`, `promtool check rules`: 7 rules, all evaluating) over the metrics the
      nodes export; the cluster view from the admin `membership` command; orchestration state from the platform. Call
      latency has no metric: a histogram per call costs every call, and traces (A6) carry it where it is needed.
- Notes: the Aspire dashboard in deployments is diagnostic only (production observability is OTLP); Azure Container
  Apps exposes ingress, not per-replica addresses, so it is a later target than Kubernetes.

## Next: cluster hardening (C, before N5)
- [x] C1 Failure detection robust to pauses (ADR-064): heartbeats travel by gossip through any member (indirect by
      nature, suspicion before death); a round that runs late moves every member's progress clock forward by this
      process's pause, so its own GC or CPU stall declares nobody dead (`APauseOfThisProcessDeclaresNobodyDead`).
- [x] C2 Split brain (ADR-064): keep-majority downing in core, on by default (`NodeOptions.KeepMajority`): the
      minority side stops without saving and exits for a restart; ties keep the lowest node id; clusters under three
      members never down. Open: an optional authoritative lease store (Kubernetes Lease, PostgreSQL) as a module for
      strict ownership, with N5.
- [x] C3 Node authentication (ADR-065): Hello carries its link protocol levels (ADR-077), the cluster name and an HMAC of
      the signing secret over the acceptor's challenge and its endpoint; an accepted connection is read only after it
      (`LinkAuthTests`). Links are authenticated, not encrypted: TLS stays the platform's (service mesh, private
      network).
- [x] C4 Supervision (ADR-066): `[Actor<TId>(OnError = ...)]` is `ErrorAction.Resume` (default) or `Reload` (removed
      without saving, the next message loads it again); every failed handler, and every system pass that throws, is
      counted (`Node.Failed`, `daserver.handler.failed`).

## Next: versioning and rolling upgrades (V, before S)
- [x] V1 Wire manifest (ADR-076): each project commits `wire.manifest` (its types, their encodings and formats, the
      members their bytes hold, replies); DAS017 fails the build on a change the type's format does not tolerate
      (positional: any; version tolerant: anything but members appended at the end; keyed: a key's type), DAS018 asks
      to record the rest (`dotnet build -p:UpdateWireManifest=true`, `=force` for a deliberate break). Messages are
      positional, persisted components version tolerant or keyed (DAS014). The samples commit theirs.
- Nino 4.0.0-rc.3 resolved everything DaServer asked for (#197 layout metadata, now what the manifest reads; #198 a
  pool for `NinoArrayBufferWriter`, frames rent from the pinned `BufferPool`; #199 `NinoRegistry.Remove` for collectible
  contexts, unused since code changes ship by hot restart (ADR-084); #200 another assembly's auto-property struct raw in every build; #201 NINO026; #202 raw memory
  recorded field by field with offsets, `Pack`/`Size`/`[FieldOffset]` included, so the manifest adds no fields of its
  own).
- [x] V2 Build on the card (ADR-077): `ClusterMember.Build` (framework and application versions); a node logs while
      its view holds several builds.
- [x] V3 Messages only where the build has them (ADR-077): services (sent or forwarded) among nodes whose build
      handles the message; an actor's owner stays its type's, and a message its build lacks for that type is refused
      (`NotHosted`) instead of sent; a client message unknown to its node's build is `NotHosted` too.

## Next: packages and other core gaps (P, K)
- [x] P1 `DaServer.Testing` (ADR-071): `TestCluster` starts real nodes in the test process; `NodeOptions.TimeProvider`
      is the shards' clock (ticks, `SendAfter`, idle unloads, checkpoints, `world.Time` in handlers and systems);
      with `ManualTime` the test steps ticks (`TickAsync`, `AdvanceAsync`), each run once on every shard. The
      framework's `ClusterTest` builds on its node options; `Sample.Tests` tests the sample's servers with it.
- [x] K2 Slot table (ADR-074): actor keys (placements for `IPlaced` ids) hash into 4,096 slots; each view gives every
      slot of an actor type an owner among the type's nodes by rendezvous hashing, so finding a key's node is one
      table read; a node joining or leaving moves only the slots it wins or held. BDN `PlacementBenchmark` (one owner
      lookup): 3 nodes 7.98 -> 6.44 ns, 30 nodes 60.9 -> 6.45 ns, 300 nodes 558 -> 6.36 ns, 0 B. With N7, against
      N6 (three trees back to back): local calls 13.3 / 174 / 1,317 µs -> 11.1 / 171 / 1,316 µs (1 / 64 / 1,024 in
      flight), one-way messages 428 / 748 ns -> 426 / 769 ns (local / remote), allocations unchanged. Two earlier
      runs that ran the new tree second showed local calls 10-15% slower; run order on this VM, not the change.
- K1 placement strategies (agreed: Akka sharding style, ADR-074): no per-actor directory and no "prefer the caller's
  node" (`IPlaced` co-locates what talks); load-aware and sticky placement move whole slots and are S3.
- [x] K3 Load-aware service choice: a caller not hosting the service sends to the less loaded of two hosting nodes
      (power of two choices over the queue length each member gossips), the first of the two in turn, so equal loads
      stay evenly spread and a node with a backlog gets nothing while another is idler (`ServiceCallsGoToTheLessLoadedOfTwoNodes`).
- [x] K4 `CallOptions` (agreed: a struct overload): every generated `Ask` has an overload taking `in CallOptions`, whose
      `Timeout` bounds that call (zero: the sender's `CallTimeout`), on nodes and clients; the deadline travels as before
      (ADR-028). Handlers read when their caller stops waiting as `ctx.Deadline`, without a `CancellationTokenSource`
      (`CallOptionsTests`).
- [x] K5 `[Actor<TId>(MailboxCapacity = n)]` (an actor type's own bound; zero: the node's `ActorMailboxCapacity`,
      read from the type's settings when mail is queued) and `daserver.messages.dropped{reason}` (`queue_full`, `mailbox_full`,
      `expired`; `Node.Dropped(DropReason)`; the target adds `deleting`, one-way mail to a root being deleted, W6), replacing `daserver.shard.rejected` and `daserver.shard.expired`
      (`AnActorTypeSetsItsOwnMailboxCapacity`; replies and completions have their own queue, ADR-027).
- Out of scope in core: durable streams (use Kafka/NATS; `DaServer.Topics` is at-most-once pub/sub between actors,
  ADR-090), transactions (a handler is atomic; sagas are order actors whose steps are `AskUntilAnswered`s of `[Once]` requests, plus compensation on definite answers, the flash sale's saga
  sample, ADR-091), distributed locks (an actor is the lock), multi-tenancy, event sourcing (documented pattern only).

## Next: elastic scaling (after N5/N6: moving stateful actors needs persistence and handoff)

DaServer does not write an autoscaler. The platform decides replica counts (Kubernetes HPA, KEDA, Azure Container
Apps scale rules); Aspire only generates the manifests. The framework's job is that adding or removing a node of any
kind is safe and cheap, that it exports the signals to scale on, and that the generated manifests are correct.

What is stateful is decided by code, not by a role: a node hosting actors is stateful, a node hosting only services is
stateless. In the MMO sample the login server is stateless; gateways hold connections (soft state:
clients resume on another gateway with their ticket); the game server (players, realm) and the map servers are
stateful, maps being heavy actors.

- [x] S1 Scale signals: per-node OpenTelemetry metrics fit for HPA/KEDA (dispatch queue depth, mailbox backlog, loaded
      actors, connections, `daserver.shard.busy_ratio` from the shards' idle ticks, `daserver.shard.tick_overruns`),
      scraped from a Prometheus `/metrics` endpoint on the HTTP plane by default (OTLP when configured); the default
      scaling metric is the busy ratio (design.md, metrics). `HttpPlaneTests.MetricsAreScrapedFromTheHttpPlane`.
- [x] S2 Scale-in is a drain: SIGTERM -> readiness off -> graceful Leave -> actors hand off (N6) or unload and save
      (N5) -> exit within the termination grace period (D1-D3, the preStop pause first); gateways retire their clients
      in waves (make before break, ADR-026). `HandoffTests`, `ClientsOfALeavingGatewayMoveAndResumeOnAnother`.
- [x] S3 Slot allocation (ADR-079): the coordinator (oldest Up member by the card's `UpSince`) keeps a versioned slot
      table it sends to every member and on every new link; nodes report their loaded slots per actor type when they
      change; `[Sticky]` types keep loaded slots where they are (a join moves only empty slots: new keys go to the new
      node, a map with players stays); other types are evened out by load, up to two slots per round from the most to
      the least loaded node (handoff). A new coordinator drops its earlier term's reports and plans once every member
      reported (a stale report of its own once unpinned loaded slots for a round). Steady rounds allocate nothing
      (`AllocationTests` caught 16 KB of counts per type per round). `SlotAllocationTests`: sticky across a join, an
      imbalance evened out, a coordinator replaced with its pins.
- [x] S4 Generated manifests. Done: `DaServer.Aspire` adds only what follows from DaServer (probes, grace period, the
      preStop wait, one workload per group with its peers Service, client ports as external endpoints); scaling policy
      is each application's, in its AppHost (`Platform.cs`): the MMO sample's gateways behind a LoadBalancer, PDBs and
      in-place VPAs for game and map; the flash sale's sale nodes under an HPA on CPU and a VPA in recommendation
      mode. Plain Deployments: identity comes from gossip incarnations and state from the store, no StatefulSet.
      Publish tests (`PublishTests`, E2E): both samples in both environments, the Compose services' health checks,
      grace periods and healthy dependencies, the chart's probes, preStop and peers Services, `helm lint` and
      `helm template` on the chart. An HPA on the shards' busy ratio (exported, S1) needs KEDA or the Prometheus
      adapter in the cluster, so it is the application's (`doc/operations.md`); the flash sale scales on CPU.
- [x] S5 Vertical scaling: shards follow the CPU limit (`Environment.ProcessorCount` honors the cgroup quota and
      `DOTNET_PROCESSOR_COUNT`; documented on `NodeOptions.Shards`). A VPA is the
      application's: in place where it can (the mmo sample: CPU resized in place, a memory change restarts the
      container so the GC heap limit follows; an eviction is a drain and a rejoin), recommendations only where a
      restart must wait (the flash sale). The node reads its limits at start.
- [x] S6 Prove it: `SampleClusterTests.AStatefulGroupScalesInAndOutUnderLoad` runs the mmo AppHost with two game
      nodes and 1,000 bots; one game node leaves while they play and joins again: no gap, no failed map change, no
      disconnect, p99 31 ms. It found what the in-process tests could not: the sample's player and realm state did not
      move with their actors (now `[Handoff]`), and the realm's status sequence was the hosting shard's tick (now the
      realm's own). The 10k-bot run with a scale step belongs to the release checklist.

## Next: architecture borrowed from in-house game servers, ET and Orleans (agreed scope; order: the development order above)

Patterns only, never business code. Each item: tests, BenchmarkDotNet where a hot path changes, an ADR, logic and docs
in separate commits.

- [x] T2 ECS bots and business isolation (ADR-055, ADR-056): `Sample.Shared` (rules both sides run; DAS010 keeps the
      server and the transport out), `Sample.Client` (the game client as ECS data and systems), `Sample.Bot` (that
      client as a `DaServer.Simulation` swarm with an HTN behavior: change maps, chat, wander), `Sample.Tests`; the
      e2e test changes maps every 5 s. `DaServer.Simulation`: `ClientLoop`, HTN planning as ECS data, `Swarm`/`SwarmMeter`.
- [x] N1 Components apart from logic (ADR-067): an application project on the server runtime declares no component
      or singleton (DAS013) and its systems hold no fields (DAS007); components live in `X.Components` projects
      (contracts and EasyEcs only), which a reload never replaces. Shared logic is exempt (clients run it, it never
      reloads); the node project is the reloadable logic itself (no separate host project). Samples, tests and
      benchmarks follow it. (game servers' server/hot-reload split, ET Model/Hotfix)
- [x] N2 `SendAfter(delay, address, message)` / `CancelSend(timer)` (ADR-058): per-shard timer wheel of serialized
      frames (`ShardTimers`, `TimerSystem`), delivered as ordinary mail when due (follows moved keys), 5 ms resolution,
      zero allocation (`DelayedSendsDoNotAllocate`, `TimerBenchmark`); DAS009 of N3b points `await Task.Delay` to it.
      (ET timers as data, Orleans timers)
- [x] N3 Call cycles: handlers never wait (ADR-081), so actors asking each other in a chain or a cycle (A asks B asks
      A, an actor asking itself, through a service, across nodes) complete (`CallChainTests`).
- [x] N3b No sync-over-async on shard threads (agreed: all four layers):
      1. Done: DAS009 (error) in actors, services and systems: `.Result`, `.Wait()`, `GetAwaiter().GetResult()`,
         `AsTask().Wait*`, `Thread.Sleep`, `Task.Delay`, `SemaphoreSlim.Wait`, `Monitor.Enter`/`lock` waits; the
         message names the replacement for the context.
      2. Done (ADR-061, ADR-081): handlers never wait (an ask's answer is mail); delays use `SendAfter` (N2); systems
         never wait for replies (DAS012): in their own world they query and `ref` components directly, other entities'
         through `Entity` handles (N7 placement affinity puts what is read together in one world); data in another
         world gets an intent, validated by its owner, whose outcome comes back as a message.
      3. Done: reading a pooled call's result before completion throws; a stall watchdog reports a shard pass longer
         than `NodeOptions.StallAfter` (1 s) once per pass, in the log and as `Node.StalledShards`
         (`daserver.shard.stalled`); the `daserver-live` health check (A-items) reads it. A managed thread cannot
         read another thread's stack, so the log names the shard, not the blocking frame.
      4. Done (ADR-062): `Ask` from outside the shards returns `Reply<T>`, await-only (no `.Result`/`.Wait`;
         `AsTask()` for combinators); an `async Reply<T>` method keeps its state machine in a box pooled per thread
         and method, so awaiting allocates nothing.
- [x] N4 Route completeness (`DaServer.Testing.RouteCheck`): given a cluster's node projects, every message their
      contracts declare has an actor or service handling it (`[ToClient]`-only messages excepted) and no request is
      answered by two service types; both samples check theirs in a test (`EveryMessageHasItsHandler`,
      `RouteCheckTests`). Running it before an AppHost publishes is A11. (Orleans finds these at compile time.)
- [x] N7 Placement affinity (ADR-073): an actor id type implementing `IPlaced` is hashed by its `Placement` (a pure
      function of its value) instead of its value, in group 0: actors of any type with the same placement share a
      node and a shard. The wire is unchanged; the registration of the assembly declaring the id type registers it;
      DAS016 reports `IPlaced` on anything but an actor id type.
- [x] N5 Persistence as ECS (ADR-068): `[Persist]` components of an actor form one record (a section per component
      type id); `LoadSystem` batches the loads of a wake and restores components before the actor's `Loaded` mail;
      `CheckpointSystem` writes changed records every `SaveEverySeconds` (30 s; change-tracked, so only actors whose
      stored components were written are visited), `PersistSystem` writes
      `SaveNow` and unloaded actors (removed once written); `StoreSystem` keeps one load and one save batch in flight
      per shard; a rejected versioned write fences the local copy; `IActorStore`, `InMemoryActorStore` and
      `PostgreSqlActorStore` (`DaServer.Persistence.PostgreSql`: a row per actor, one round trip per batch); DAS014;
      `PersistenceBenchmark`. Relational business
      data (accounts, payments, mail, auctions) stays in the user's ORM inside services. (game-server write-behind, ET
      serialized components, Orleans IPersistentState + ETags)
- [x] Z7 A/B against d676d2a on one VM (default job, both built here; the VM is slower than when README numbers were
      taken, so only same-machine pairs compare): calls 26-74% faster (1 local call 60.1 -> 16.5 us, 1,024 local
      5,435 -> 1,391 us), one-way messages 54-88% faster (local 5.26 us -> 0.64 us), allocations equal or lower. Group
      broadcasts measured +1% to +32% and the broadcast scenario +7% / +13%, but repeated runs of one build vary by
      +-25% on this VM (35-46 ms for the same 5,000 x 9 case); a bisection on single runs pointed at a commit that two
      interleaved runs then cleared. Broadcast work goes to Z5.
- [ ] Z6 .NET 11 / C# 15 (audit of October 2026, probes on RC2, Roslyn 5.12): done: runtime async on for net11.0
      builds, net10.0 builds dropped (the shared code's net10.0 build returns in W2, ADR-086), `Reply<T>` kept as the awaitable of callers outside the shards (as fast as
      runtime async, 0 B suspended; ADR-054),
      C# 14 extension members for the server-only `World` API (`world.Tick`, `world.Cluster`). Open: Zstandard span
      codecs (`ZstandardEncoder`) for large actor records, measured with BDN. Not worth it now: interceptors (the generated
      `Ask` overloads already avoid the generic hop), unions (they box struct cases), closed hierarchies (net11-only
      attributes), a generated `AddSystems` (the order of systems is the user's to choose). Already in effect without
      changes: JIT bounds-check and devirtualization work, continuations that skip `ExecutionContext` restore when
      nothing flows.
- [ ] N12 Nino (audit of October 2026; Nino's reads check every length, bound depth and counts): done: enum members in
      `wire.manifest` (a member removed, renamed or renumbered is DAS017), pinned bytes for raw structs, strings,
      arrays and the framework's version-tolerant frames (`WireBytesTests`), DAS019 for collections held directly in
      `[FromClient]`/`[ToClient]` messages, `Registrations.Run` clean under AOT analysis. On 4.0.0-rc.6: Latin-1 strings
      one byte a char (link level 3, client level 5: a full restart), the manifest in Nino's byte descriptions with its
      layout text named in its header and keyed members matched by key, padded wire structs packed, `[NinoUtf8]` kept
      for chat only, the registration's module initializer only where one is usable (Nino's own Unity fix), `System.Type`
      refused as a wire type, Nino.AspNetCore bodies on the HTTP plane (ADR-046). Next: `GetFixedSize` for fixed-size frames (only if BDN
      says so), `IsSupported` checked at start. Open: release on Nino 4.0.0, not an RC (drop the NU5104 suppression then).
- [x] N6 State handoff on moves (ADR-072): an actor that changes node (scaling out or in, a node stopping) sends its
      [Persist] and [Handoff] components, version and last-written hash to the new owner on the link its forwarded
      mail takes next; the new owner restores them without reading the store, the old one is removed once accepted
      without writing. A new owner that gets mail first asks the previous owner and waits; refusals, silence and gone
      nodes fall back to the store. Scaling a 40-actor node out and in: 0 store reads, 0 writes. Also fixed: an actor
      unloaded and waiting for its final write no longer runs mail (it waits and moves with the actor).
- [x] N8 Collections in messages without garbage (ADR-078): a message that holds a collection holds it in a
      `[NinoPooled]` class its handlers return (`Moves` in `Motion`); each handler deserializes its own copy, so
      fan-out to several handlers and bursts held at once (200 per thread) allocate 0 B per push (Sample.Tests
      `ContractTests`). Nino's default pool (64 per thread, 256 shared) needs no sizing for a bot loop. Text the
      clients send and receive takes Nino's default (one byte a char for ASCII names); chat (`Say`, `Chat`) is
      `[NinoUtf8]`, as chat mixes ASCII with emoji and other scripts.
- [x] N11 Declared filters: `[Filter]` on a static `RpcStatus M(in Context context, in TMessage message)` (that
      message type) or on an `IMessageFilter` class (every message) is registered by the assembly's
      generated registration: rate limits, authorization and auditing without startup code; DAS021 for a wrong shape
      (`FiltersAreRegisteredWithTheAssembly`, `AFilterAttributeRegistersItself`). Registered rather than inlined into
      the dispatchers: a filter in another assembly (shared rules) applies too, and a message without filters still
      costs two length checks. (Orleans call filters)
- Not adopted: Orleans interface proxies, per-activation Task scheduling, grain directory / repartitioning,
  transactions; ET location server, coroutine locks, class entity trees, global singletons; game-server stop-the-world
  reload with forced GC, manual dirty marking. GM commands (N10) are out of scope for now.

## Edge cases (audit of October 2026: each found case gets a deterministic test before its fix)

Four read-only audits (moves and placement, membership and links, client sessions, persistence and the actor
lifecycle) listed the
interleavings a real network leaves to chance. Each is forced in a test (a gated store, a scripted node, crafted views
and cards, a manual clock), fails before its fix, and passes after it.

- [x] Client moves (`ClientMoveTests`, `ScriptedNode`): a move between a sender's pick and its send; calls of a left
      connection fail when its node dies; a refused resume; a second Retire; the old connection kept until its calls
      are answered; eight senders across twenty moves.
- [x] Slot planner (`SlotPlannerTests`): takeover without reports, a left node, sticky pins, a slot reported twice,
      moves until rendezvous agrees, the threshold and the move limit, determinism.
- [x] PE1 A checkpoint in flight as the actor unloads was taken for its final save (later changes lost).
- [x] PE2 A failed handler's SaveNow (and checkpoints of fenced actors) wrote half-changed state; a fenced unload handed
      off or wrote its stale copy.
- [x] PE3 A fenced actor dropped while its write was in flight (the reload could read before it): `PersistenceEdgeTests`.
- [x] PE4 A stale owner writing after the new owner loaded fenced the new owner: loading claims the record (its version
      goes up), so the stale copy is refused (`ANewOwnerClaimsTheRecordSoTheStaleOneCannotWriteOverIt`,
      `AClaimFencesWhoeverLoadedTheRecordBefore`).
- [x] PE5 A failed write waited for the next checkpoint: it is retried after the delay (`AFailedCriticalSaveIsRetriedSoon`);
      PE7 loads go in batches of at most 512 (`LoadsWaitingForASlowStoreGoInBoundedBatches`).
- [x] PE6 A failed load failed the batch's callers at once: retried after 200 ms until the call timeout
      (`ALoadThatFailsIsRetriedWithinTheCallTimeout`).
- [x] PE8 An actor's lifecycle handler asking back the actor whose request loads it waited for the call timeout: with
      answers as mail (ADR-081) it is answered (`ACreatedHandlerAskingTheActorWhoseRequestLoadsItIsAnswered`).
- [x] PE9 A store call that never finished held its shard's loads or writes for good (one batch in flight each): store calls
      are cancelled after `NodeOptions.StoreTimeout` (30 s) and retried like any failure, with one reusable token source
      per batch (`AHungLoadIsCancelledAfterTheStoreTimeoutAndRetried`, `AHungWriteIsCancelledAfterTheStoreTimeoutAndRetried`:
      held for good before).
- [x] H1 The new owner stopped waiting for the record after the call timeout while the old owner still ran the actor
      (duplicate activation): it asks again while the previous owner is alive and loads only once it is dead
      (`ANewOwnerWaitsForTheRecordWhileThePreviousOneHoldsTheActor`).
- [x] H2 "None" from a node that still owned the key in its own view, which then loaded it (two copies while views
      converge): it keeps the request until ownership changes (`ANodeThatStillOwnsAKeyDoesNotAnswerNone`). H12 a node
      loading right after another joined asked the newcomer, which never held the key, and waited on it
      (`ANodeLoadingRightAfterAnotherJoinedDoesNotWaitForIt`).
- [x] H3/H7/H8 A node kept one previous view, and a record refused for a key that moved on was lost without a store:
      nodes keep the views they replaced within the move window; a node that took a key moments ago defers "none"
      and asks the node it took it from; a record whose key moved on goes on once, to the asker or the owner
      (`ARecordThatArrivesAfterItsKeyMovedOnFollowsIt`, `ANodeThatHeldAKeyBrieflyWaitsForItsPreviousOwner`).
- [x] H4 The old owner timed out and wrote, then the new owner's late accept arrived: it waits while the new owner
      is alive (`ALateAnswerFromALiveNewOwnerIsWaitedFor`).
- [x] H5 A pending SaveNow is written after the record was sent (`Without<SaveNow>` on the handoff).
- [x] H6 User `[ForEach]` systems ran on actors that are not live (the mmo realm broadcast one status twice from
      both copies): an actor's state exists only while it is live; a copy that sent its record gives it up (restored
      if the handoff falls back), a new copy gets it when its record is restored (`NoSystemSeesACopyThatHandedItsStateOff`).
- [x] Handoff answers were dropped by a node that just joined (its view did not list the sender yet): sends use the
      link a node introduced itself on (the flake of `ScalingOutAndInMovesRecordsWithoutTheStore`, traced).
- [x] H11 Mail admitted to a stopped shard waited until it timed out: it goes on to the key's owner, and handoff frames
      are answered (none, refused) so the other side falls back at once (`MailForAStoppedShardGoesOnToTheKeysOwner`).
- [x] H9 A delayed-send handle kept across a move cancelled another timer on the new node (every wheel started at
      generation zero): handles carry a random salt per wheel (`AHandleCancelsOnlyOnTheWheelThatMadeIt`).
- [x] M1 A node restarting on the same address with a new id: the older of two live members at one address is dead at
      once, a node's own address holds only itself, a dead id resets a link only when the link's stream is its own
      (`ANodeRestartedAtTheSameAddressLosesNoCallWhenItsOldIdIsDeclaredDead`: a call timed out on every run before).
- [x] M2 Forgotten members came back through later nodes' gossip for good: forgotten ids are kept for ten DeadAfter,
      unknown Dead or Leaving members are not learned (`AForgottenMemberDoesNotComeBack`).
- [x] M3 A pause past DeadAfter fences the node (its actors go without a write, a new incarnation)
      (`APauseLongerThanDeadAfterFencesThisNode`).
- [x] M4/H10 Two coordinators wrote one table version: the view's coordinator's wins, a coordinator publishes above
      every version it saw (`TablesOfOneVersionFromTwoCoordinatorsConverge`).
- [x] M5 A client-facing node falsely declared dead then refuting had its sessions closed after the grace: skipped when
      it is back (`AClientFacingNodeFalselyDeclaredDeadKeepsItsSessions`).
- [x] M6 Actors already asked to leave, unloading or holding a final save were not fenced by a later Dead verdict (they
      unloaded as planned and wrote): every copy not yet removed is fenced (`AnActorAskedToLeaveIsFencedWhenItsNodeIsDeclaredDead`).
- [x] M7 A process late on every tick credited every gap to the others' clocks (a dead member kept its keys): the next
      pause is excused no sooner than DeadAfter and twice this one later, so the others age at half speed or more
      however late the ticks are (with DeadAfter alone, ticks late by nearly DeadAfter under a loaded machine excused
      nearly everything and the test failed in a full run; it now pauses 1.35 s of 1.5) (`ANodeLateOnEveryTickStillDeclaresTheDeadDead`). M10 a worse status at an older heartbeat
      lowered the heartbeat, so stale pulses counted as progress: the newest is kept (`AStalePulseDoesNotLowerAHeartbeat`).
      M11 introductions were kept for nodes that left: dropped once the view lists them (`ANodeThatLeftIsForgottenEverywhere`).
- [x] M9 A seed spelled otherwise than the member's card opened a second link to the node, and the two took its inbound
      slot from each other: the link reaching a member serves its card's address too (`ASeedSpelledOtherwiseIsOneLink`).
- [x] M8 A recorded Hello could be replayed to another node within its minute, and its endpoint was not signed: the
      acceptor challenges, the Hello proves the challenge and its endpoint (`AHelloProvesItsChallengeEndpointStreamAndCluster`).
- [x] S1 An actor that moved forgot its client sessions (resume refused, the mmo's map changes Forbidden): sessions
      travel in the handoff record, never stored (`AClientResumesWithAnActorThatMovedToAnotherNode`).
- [x] S2 A resume cut off after the actors accepted it (the client gave up waiting behind a backlog, a drain closed the
      new connection) left the session unrecoverable: the client-facing node reverts it (a resume back to the previous
      session, which the actors restore without telling their handler), a client retries on `Superseded`, and drains and
      connection ages skip sessions mid-resume (`AResumeCutOffAfterTheActorsAcceptedItIsRetried`).
- [x] S3 Calls on the old connection are failed when it dies while the move is still resuming; S4 a dispose during a
      reconnect or move says goodbye and keeps the client out of the sweep; S5 a probe only closes the connection it
      pinged; S8 a reconnect attempt ends with ReconnectTimeout (`ClientMoveTests`, all failing before).
- [x] S6 A move's two connections dispatched pushes from two loops (callbacks overlapped, a new session's push could
      come before an older one of the session left), and the actor's group broadcasts reached both sessions until the
      goodbye: pushes are one connection at a time and the new one's wait until the old drained
      (`PushesOfTheConnectionLeftComeBeforeThoseOfTheNewOne`); an actor taking a resumed session makes the previous one
      leave its group, ahead of its later broadcasts (`AResumedSessionTakesTheActorsGroupFromThePreviousOne`).
- [x] S7 A kick was evaded by a resume waiting behind the kicking handler (accepted, and the close then filtered as
      superseded): actors kick with `DisconnectSession`, which withdraws the grant at once, so the resume is refused
      (DAS020 for `Send(session, new Disconnect())` in actors; `AKickIsNotEvadedByAResumeWaitingBehindIt`). S9 One ticket
      presented on two gateways at once could resume the player on one and the room on the other: the first actor that
      takes the session decides, asked alone, and the other resume stops at its `Superseded`
      (`ATicketPresentedOnTwoGatewaysAtOnceResumesOnOne`). S10 A close or resume for a moving actor was refused: one
      sent on a stale view is forwarded, one reaching the new owner before the handoff record waits behind it, and an
      actor without a `SessionClosed` handler still forgets the session (`ACloseSentOnAStaleViewIsForwardedToTheOwner`,
      `ACloseThatReachesTheNewOwnerBeforeTheRecordIsNotLost`, `AResumeThatReachesTheNewOwnerBeforeTheRecordKeepsTheGrant`,
      `AnActorThatHandlesNoCloseStillForgetsAClosedSession`).
- [x] Fail fast: a test waiting on a condition stops at the first fault the cluster logs (a crashed or stalled shard, a
      failed maintenance tick or gossip round, a refused link, a stop past its budget, anything critical), naming it,
      instead of at its time limit (`FaultLog`); the E2E runs stop at the first resource that fails to start, exits or
      turns unhealthy, with its last lines (`Watchdog`). It found a refused node reconnecting thousands of times a second:
      a closed handshake now backs off (100 ms to 5 s).
- [x] PE11 The PostgreSQL table was created by a store's first call (its readiness said nothing of it, and a failed create
      failed the first loads): the node initializes its store before it starts, and `createTable: false` leaves the
      schema to a migration job, checked at start (`AStoreThatDoesNotCreateItsTableRefusesToStartWithoutIt`).
- [x] N12a A payload that is not its message is refused (`Malformed`) before the handler runs: no failure counted, no
      `OnError` action (`AMessageThatCannotBeReadIsRefusedWithoutFailingTheActor`).
- [x] PE10 Nodes starting together raced on `CREATE TABLE IF NOT EXISTS` (23505, the first loads failed): an advisory
      lock serializes the create (`NodesStartingTogetherCreateTheTableOnce`).
- [x] CQ1 `MpmcQueue` reported full (and empty) while a consumer (a producer) was between taking a position and
      handing its slot over: shard dispatch refused as `Overloaded`, pools dropping and allocating buffers. A slot one
      lap behind is full only when every position is still taken, empty only when no producer took it; otherwise the
      caller spins and yields, as `ConcurrentQueue`'s segments and crossbeam's `ArrayQueue` do
      (`MpmcQueueNeverFalselyFullOrEmpty`; 5.3M false fulls in 8M operations at 4 threads before, none after). A lost
      exchange of a cursor then backs off 2, 4, ... 64 spins and retries from the position it returned, so the losers
      stop bouncing the cursor's line. BenchmarkDotNet on one machine (ns per operation):

      | Benchmark | Before the fix | Fix | Fix and back-off |
      |---|---|---|---|
      | `PoolBenchmark.Queue`, 4 / 16 threads | 306.9 / 173.5 (failed operations counted) | 292.8 / 352.4 | 151.6 / 144.0 |
      | `PoolBenchmark.QueueBcl` (`ConcurrentQueue`), 4 / 16 threads | | 295.1 / 280.9 | 310.2 / 325.4 |
      | `PoolBenchmark.Buffers` / `Objects`, 16 threads | 11.5 / 7.0 | 9.8 / 6.8 | 9.7 / 6.9 |
      | `MessageBenchmark` Local / Remote | 586.1 / 844.6 | 500.7 / 800.7 | 468.6 / 752.3 |
      | `RpcBenchmark.Calls` | | | within 3.5% of the fix in every case, allocations unchanged |

      The pool rows ran on one machine before a container restart; the `MessageBenchmark` and `RpcBenchmark` fix and
      back-off columns ran back to back after it, on a slower machine, so its before column does not compare with them.
- [x] CQ2 A shard stopping and an admit racing it, and a flushed connection's carry loop and a `Wake`, each stored a
      flag and then read what the other thread stored (store buffering, allowed on x64): a frame stranded in a stopped
      shard, a connection stalled until closed as a slow consumer. Full fences (`Interlocked.Exchange`) on both stores;
      stress repros outside the suite: 1,304 and 51 in 8M trials before, none in 32M and 16M after.
- [x] CQ3 `PendingCallTable`: a sweep that read an expired call's tag and lost the core while the slot went round
      until a later call wrapped to the same id could fail that call; tag and the whole generation are one 64-bit state
      exchanged together (an entry stays 32 bytes; not reproduced, found by reading).

## Open cleanup (review rounds)
- Audit round 2 (agreed; each change with tests and BenchmarkDotNet where a hot path changes):
  - [x] Cache lines: a shard's per-pass counters and its sleeping flag on lines of their own, its own counters
        without `Interlocked`, the actor count written when it changes; channels touch their activity stamps at most
        every 100 ms; `DeferredReply.Used` written once.
  - [x] Pools and the call table: per-thread magazines in `ObjectPool` (the depot's cursors touched once per 32
        objects); call table entries 32 B (int tag, the deadline as the low 32 bits of `Platform.Milliseconds`, passed
        in absolute: one clock read less per call). `PoolBenchmark` (1/4/16 concurrent threads) before: frames 33 /
        189 / 198 ns, calls 41 / 230 / 232 ns; after: frames 24 / 12 / 11 ns, calls 43 / 173 / 162 ns, nothing
        allocated.
  - [x] The call table's free stack, the one CAS every call and reply contended on, in stripes (a node: one per core
        up to 16; a client: one): a thread frees onto its own stripe and takes from it first, then from the others, so
        the table grows no more than with one stack. `PoolBenchmark.Calls` (1/4/16 threads): 41 / 260 / 241 ns before,
        42 / 36 / 33 ns after; end to end (`RpcBenchmark`) within run-to-run noise, nothing allocated.
  - [x] Group fan-out: a channel array next to the members; `FrameChannel`, the shell (send, close, tag, stamps)
        shared by TCP and WebSocket.
  - [x] Routing: one route per handler type (nodes, kind, slot owners) found by one lookup.
  - [x] Small: the flusher's registrations in the thread's send batch (one lock per flusher and batch); the swarm's
        latency histogram striped by thread with log-linear buckets (1,024, at most 3% wide); the link's received
        counter on a line of its own.
  - [x] Clocks: resume graces on `NodeOptions.TimeProvider` (steppable in `TestCluster`); call deadlines, membership,
        keep-alives and rate limits on `Platform.Milliseconds` (real time, one clock).
  - [x] One backoff with full jitter (client reconnect, node links, swarm retries).
  - [x] Generated registration uses `TypeId<T>` (no literal ids); a test pins the analyzer's ids to Nino's (nested,
        namespaced, `[NinoFormerName]`).
  - [x] One host:port parser (`HostPort`, IPv6 in brackets); `[LoggerMessage]` on message and actor failures, the
        dropped-backlog warning at most every 10 s with a count.
- Audit round 3 (agreed):
  - [x] Planned reconnects make before they break (`ControlOp.Retire`, ADR-026): at
        `MaxConnectionAge` or a drain the node asks the client to move; it resumes on a new connection while the old one
        keeps working, then leaves the old one once its calls are answered; the node closes it after `RetireGrace`
        whatever the client does. `ResumeTests.AConnectionRetiredForItsAgeLosesNoPush`: every call answered and every
        push received across several moves.
  - [x] mmo: a VPA in `InPlaceOrRecreate` for game and map nodes, CPU resized in place, a memory change restarting
        the container (.NET reads its limits at start: the GC heap limit follows), one at a time under their PDBs;
        flashsale keeps recommendations only (nothing restarts during a sale).
  - [x] The actors' side of sessions stays in the runtime (decided against moving it into `Clients/`): nodes that only
        host actors run no client feature yet must close the sessions of a gone client-facing node, so that tracking
        is the runtime's (`SessionNodes`, out of `Node`); `ResumeGrace` and the signing secret serve both sides.
  - [x] One clock name: `Platform.Milliseconds` in the runtime, `nowMs` passed down from `Maintain`; one deadline
        helper (`PendingCallTable.DeadlineAfter`); every runtime log a `[LoggerMessage]` in `Log`; the stats line
        reads the node's gauges.
  - [x] Gossip rounds on the node's maintenance tick (one timer per node, one pause rule: the silence clock).
  - [x] PostgreSQL store: a pooled connection per batch (Npgsql keeps prepared statements per physical connection,
        prunes and replaces connections); only the parameter lists are reused.
  - [x] Samples share `samples/Shared/Publishing.cs` (the Compose/Kubernetes switch, `Manifest`, the VPA). The HPA
        stays plain data: Aspire's typed HPA writes empty metric sources Kubernetes rejects.
  - [x] Node options validated at construction (every problem in one message); one polling helper (`Poll`).
  - Declined: constants shared between `DaServer.Aspire` and the node (it would make the AppHost package
        depend on the node's, Nino included); the stop budget's defaults are documented on both sides instead.
  - Declined: client entry addresses through `IConfiguration`. Aspire passes them as `DaServer__ClientEndpoints__{i}`,
        which is the configuration key `DaServer:ClientEndpoints` already: a hosted caller binds it with its own
        configuration, the swarms read it with `Endpoints.FromEnvironment` (no configuration dependency in the
        simulation), and game clients (Unity) take addresses from the application.
  - [x] Files over ~400 lines split by concern: `ClientConnections` (+ Sessions, Groups, Resume), `ClusterClient` (+
        Connection, Resume), `Membership` (+ Gossip), `PendingCallTable` / `PendingCall`, `Connection` (+ Flushed),
        `Node` (+ Stop, Maintenance).
  - [x] The pools documented by who returns: `PerThread` (the renting thread), `ObjectPool` (another thread), a shard's
        `AnswerCalls` singleton (per-shard state, as ECS wants: not moved to `PerThread`).
  - [x] Docs: the stale statements (client redirect remnants, analyzer range DAS001-018, ADR-015/036, design §11
        publishing, READMEs on `Publish=k8s`, flashsale in samples/README, R11's bot options, CLAUDE.md layout,
        roadmap states).
- [x] Deployment: one AppHost publishes to Docker Compose or Kubernetes (`Publish=compose|k8s`); in publish
  mode a node group is one workload with replicas (A4): no container names, no fixed host ports per replica, seeds
  through the group's DNS name (Compose service DNS, Kubernetes headless Service), each replica advertising its own
  address (ADR-030); both samples, each adding its own platform objects (mmo: a load balancer for the gateways,
  disruption budgets and in-place VPAs for game and map; flashsale: an HPA, a VPA recommending requests). No StatefulSet (identity from gossip incarnations, state in the store), no etcd or
  Kubernetes API access (DNS seeds only); secrets are Aspire secret parameters (a Kubernetes Secret, a Compose
  `.env`); a migration Job is the application's.
- [x] Nino rc.4: `FrameWriter.Write<T>(in T)` over `NinoSerializer.Serialize<T>(in T, IBufferWriter<byte>)` (no copy of
      a large struct per write). BDN, rc.3 -> rc.4 on one machine: Message local 624 -> 601 ns, remote 960 -> 910 ns
      (0 B both); Broadcast within noise (1,000 clients 5.96 -> 6.00 ms, 5,000 clients 33.2 -> 34.0 ms); Persistence
      within noise, two interleaved runs each (Load memory 2.01/2.71 -> 1.91/2.14 ms, SaveNow PostgreSQL
      9.9/12.3 -> 10.5/10.7 ms). Load allocates per actor it activates (its record, index growth): activation is the
      cold path, not counted against the 0-GC thresholds.
- [x] Kestrel's raw TCP evaluated, not adopted: a `PipeChannel` over a Kestrel connection (`ListenOptions.Run`)
      feeding the same client connections (`TransportBenchmark`, kept to measure again). Broadcast to 1,000 clients
      equal (5.86 / 5.84 ms); to 5,000 clients 26.2 ms and 55 KB against 43.7 ms and 14.8 MB with Gen2 collections
      (each connection's pipe grows blocks, each batch awaits a flush); 64 concurrent client requests 29-30 against
      34 us. Both time and allocation regress, so the own `Connection`/`Listener` stay; TLS stays the platform's.
- [x] B4 one serialize-into-pooled-frame helper (`MessageWriter.Write`, replies included); B8 one pending-table scan
      (timeouts, a link's calls, all calls); B9 one add-unless-registered for handler types, systems and persisted
      components; B10 one mix (`ClusterView.Weight`) for slots, rendezvous and shards. BDN `RpcBenchmark` (short job,
      three interleaved runs each): one local call 4.25-4.62 us before, 4.67-5.19 us after, a run without B4 5.30 us
      (the machine's drift); 0 B both. Kept: B5, the local branches deliver to different places (an actor's shard, an
      address's feature) and the group fan-out holds the member already, so one helper saves no line.
- [x] B11 sample review: a queued flash sale admitted position 0 (never issued) and so let any buyer skip the queue
      (`AQueuedSaleAdmitsBuyersAtItsPace` now asks for it); a bot whose map change outlasted `TravelEvery` started a second one
      (the task carries its destination: started is data); maps are `[Sticky]` as ADR-079 describes; a late buyer's
      answer is a binary search over the sorted buyers; stale comments (the realm's tick count, the map's size, the
      tests that run a cluster), unused usings, one Deployment reference for both samples' autoscalers. Kept: the mmo
      sample's `Sample.*` names (renaming touches every project, the E2E tests and the published charts, for no
      behavior).
- [x] Split `Node` into links and router; membership talks to the node through an interface: links (handshake, one
      link per node) in `Node.Links.cs`, routing in `Node.Routing.cs`; `Membership` sees only `IMembershipHost`
      (identity, options, links, what to advertise and pulse, view changed, fenced, downed).
- [x] A handoff answer was dropped when the sender joined moments before: the shard read a view not listing it yet,
      gossip then published one that did and dropped its introduction, and the second read found neither. The old
      owner kept waiting on its copy; when the key came back it refused the record, and both sides fell back to the
      store (`ScalingOutAndInMovesRecordsWithoutTheStore`), or a stopping node waited out its budget for the answer
      (`WithoutAStoreStateStillMoves`). `LinkTo` reads the view again after a missed introduction (one lookup for
      handoffs and session operations). Under four parallel test runs on four cores: 2-6 failures per 48 runs before,
      0 in 192 after.
- [x] Test clusters had one name (`daserver`): a node taking a port an earlier test's node just released could be
      gossiped with by that test's stopping nodes and route keys to them (the suspect of a NotHosted in
      `AReloadWaitsForTheWriteInFlight`, CI run 39099216040). `TestNodes` gives each cluster a name of its own.
- [x] `ResendWindowsRecycleTheirOwnChunks` read the process-wide pool counter, which parallel tests and the pool's
      trimming move: it counts the window's own rents from the shared pool.
- Kept on purpose: B6 (local replies hand the frame over without a copy), B12 (the shard checks runnable actors after
  tick systems sent local mail)
- [x] Self-review round 1 after the release checklist (three independent read-only reviews: distributed correctness,
      hot paths, code and docs; each finding checked against the code before acting):
  - A handoff could stall until a node died when its record or answer was lost on a live link (a link that restarted
    its stream, a backlog past its bound): the previous owner sends the record again when the new owner asks or the
    answer is late while it lives, and the new owner answers "accepted" again for a record it already took
    (`HandedFrom`). `ALostRecordGoesAgainAndTheStoreStaysOut` (fails without the resend: the asks time out).
  - Call ids keep 32 - `PendingCallBits` bits of generation (14 on a node) and the free stacks reuse the last freed
    slot, so a late reply could, rarely, match a later call: a failed call's slot cools for 30 s (longer than any
    budget) before reuse, unless the table is full (`AFailedCallsSlotCoolsSoItsLateReplyFindsNoOtherCall`).
  - A seed resolver that never answers stopped the refreshes for good: each resolution is bounded by the refresh
    period (`AResolverThatNeverAnswersDoesNotStopTheRefreshes`). A Hello racing a new view could leave an
    introduction behind; a shard answering a given-up slot round counted into the next one (rounds are numbered).
  - A relayed reply's frame is sized for its call id; a handler's frame header is read only to log. Measured and not
    kept: node links keeping their written buffer as a spare (instead of renting 256 KB from the pool per burst after
    idle): `RpcBenchmark` default job, interleaved off/on/off/on, 1 remote call 95.2 / 96.2 / 89.4 / 82.2 us, 1,024
    remote calls 1,057 / 1,041 / 1,039 / 1,052 us, 0 B either way: no gain beyond the noise, for 256 KB more per link.
  - `LoadBatch` and `SaveBatch` share one `StoreCall`; `AsyncSignal`, `Listener` and `NodeMetrics` are internal;
    `SwarmResult.Failures` removed (unused).
  - Docs: hot reload described as planned (ADR-067, ADR-078, the mmo README), analyzer ranges, R11 as measured, the
    flash sale's numbers as ADR-070 measured them, history wording.
  - Kept, with the reason: the trace check reads `Activity.Current` on every send (an `AsyncLocal` read, a few ns;
    skipping it would drop spans users start themselves); the header decoded per stage and the
    placement computed per stage (cheap while no placed types are registered; a carried handler needs `Envelope` to
    grow); shard queues' CAS dequeue (the drain after close is a second consumer); the timeout sweep scanning the table
    every 100 ms (sequential, bounded by the peak); a group's lock held across its fan-out (orders broadcasts and
    joins); `ClusterClient.cs` at 454 lines (already split in three by concern).
- [x] Self-review round 2 (independent reviews of round 1's changes, the client edge, the public API and the docs):
  - Round 1's duplicate check (sender, version, hash) could match a newer record (an actor moved back and forth
    without a checkpoint carries the same header), and a resend arriving after the actor moved on could be adopted or
    refused into a stale write. Each handoff now carries a random id that resends keep; resends are their own frame,
    taken only by a copy waiting for the key's record, never adopted or passed on fresh; each shard remembers the
    handoffs it took or passed on for two handoff timeouts (`HandoffsSeen`, bounded), so a resend is answered
    "accepted" wherever the actor went, or follows the first send to the node it was passed to. A resend reaching a
    stopping shard goes unanswered (it may have been taken there); its sender falls back once that node left.
  - Tickets and resumes draw on the session's message rate (each costs a signature;
    `ResumesAndTicketsDrawOnTheSessionsRate`). `Take` refuses ids that are no call id (0 and the cooling tags).
  - `NodeFeature.IsReady` removed (nothing read it). Generated-code entry points hidden from IntelliSense
    (`ServerRegistry`, `HandlerTypeInfo`, `SystemInfo`, `MessageHandler(s)`, `MessageFilters<T>`); the transport types
    users never touch are internal (`Connection`, `ConnectionOptions`, `FrameChannel`, `KeepAlive`, `SendFlusher`,
    `WebSocketChannel`, `MpmcQueue`, `BufferPool`, `FrameExtensions`, `ClientConnections.Open`/`Channels`, the hosting
    margins and status mapping); `ClusterMember`'s fields stay public (the card is a Nino wire type), its `Clone` and
    `Supersedes` are internal.
  - Docs: the README's benchmark table and one-map latencies from the release checklist's run, its realm example as
    the sample writes it, design's shutdown order and `ICluster` scope, the roadmap's development order and Z6 state.
  - Kept, with the reason: `ClientConnections` configured under `DaServer:Clients` (the section names what is
    configured, the clients; the feature type names what runs); `ResumeGrace` on `NodeOptions` (both sides of a
    resume read it, the actor nodes without client connections included).
  - E2E: the first sample run after round 1 failed once with game-0 not exiting 0 (its log was not kept); three
    reruns of the three sample tests passed. Watched in the next rounds.
- [x] Self-review round 3 (the handoff protocol attacked on its own; the generator, Aspire, hosting, the PostgreSQL store,
  the testing package and the client SDK with fresh eyes):
  - A copy declared dead while past its `Unloading` mail (its final save waiting for the write in flight, its record about
    to go or gone) was never removed, so its shard never drained and a downed node never exited: fencing asks every
    copy to leave and the drop takes `HandingOff` copies too
    (`ACopyDeclaredDeadWhileItsFinalSaveWaitsIsStillDropped`, fails without the fix).
  - Handoffs: the restored copy keeps its handoff id (`TakenHandoff`, no bound), the shard's memory evicts oldest first
    and knows when it forgot early (65,536 entries); a resend is taken only from the sender the copy waits for, or by the
    owner holding no copy while nothing was forgotten (as a first send would be); one following a pass-on to a node
    that is gone is refused instead of unanswered.
  - Generator and analyzer: a nested or non-public `[Persist]`/`[Handoff]` component is DAS014 (it was never
    registered, so never saved); `[Handle]` on an abstract or generic class is DAS001 (nothing was generated and derived
    types do not inherit it); generated registrations sort ordinally on every machine.
  - Client: a push handler that throws no longer closes the connection (a reconnect loop when the resent state throws
    again): `ClusterClient.HandlerFailed` reports it, the other handlers still run
    (`AHandlerThatThrowsOnAPushIsReportedAndTheConnectionStays`); a client whose first connect fails leaves the
    sweeper's list.
  - Hosting and Aspire: the admin bearer scheme is case-insensitive (the token still compared in constant time); a
    budget header past `int.MaxValue` ms is clamped instead of a 500; probe mode without `HttpPort` says so; the
    route check lists node projects one after another (their builds share outputs) and kills a listing on
    cancellation.
  - Kept: a private nested `EcsSystem` fails the build in generated code (CS0122) rather than with a DAS rule: an
    error either way, and DAS001 names handlers, not systems.
- [x] Self-review round 4 (every lifecycle tag combination against every system's filter; every change since the
  release checklist):
  - Two copies each waiting for the other's record (ownership flapping X -> N -> X with the key loaded nowhere) waited
    for as long as both nodes lived: a copy still waiting for its record when it is asked to leave goes at once
    (`UnloadSystem.Abandon`; `ACopyStillWaitingForItsRecordGoesWhenItsNodeStops`, a stop that ran out its budget
    without it).
  - `ErrorAction.Reload` after an unload was asked for (a rebalance, a stop, an operator) saved or handed on the
    half-changed state: the copy is fenced whatever was asked before; a refused checkpoint fences a copy already
    leaving too.
  - A late "none" from a node other than the one asked no longer starts a store load; a fenced copy refuses a record
    reaching it in the same wake (its sender keeps it and writes); a failed non-final write no longer holds a copy
    leaving by handoff (its record carries the last written hash).
  - A given-up slot round no longer clears the flag of the round counting after it; a full call table takes cooling
    slots back from a rotating start; the README's per-case allocations are in the release record's table; the flash
    sale's G5 numbers are ADR-070's; `HandlerFailed` says failures go unobserved without a subscriber.
  - Kept: answers carry no handoff id (a stale "accepted" needs the key to move M -> N -> M within one answer's
    flight); `Observe` allocates a state machine per push whose handler completes asynchronously (synchronous handlers
    pay nothing, and the task is now observed instead of dropped).
- [x] Self-review round 5 (ownership across nodes; membership, links and sessions):
  - A link that closed while its backlog was being flushed after a resume dropped the rest of the backlog (frames the
    connection refused reach no resend window): the flush stops at the first refusal and keeps the rest for the next
    connection. Not covered by a deterministic test (it needs the socket to fail between the replay and the flush).
  - A minority node that heard of the deaths from its own side's gossip before its detector declared them never
    counted the sides (its detector skips members already dead), so it ran on until the other minority node went
    silent: every member turning Dead, detected or merged, triggers the count (`BothNodesOfATwoNodeMinorityDownPromptly`;
    the race itself needs both sides cut within one gossip round, so the test pins the behaviour, not the race).
  - A copy that handed its record to a node other than the one that asked told the asker "none", which loaded a second
    copy from the store: it tells the asker where the record went (`MovedOn`), and the asker waits for that node.
  - A node remembered previous owners for one call timeout, but a leaving copy can hold its key through a handoff and
    a write in flight: the move window is 2 x (call timeout + store timeout), over up to 16 recent views. A copy held
    longer meets a new owner that loaded from the store; versions decide and its changes since its last write are lost
    (ADR-072).
  - Fixed after the round: a `Loaded` handler failing on a copy restored from a handoff writes the moved record before
    the copy goes (`AMovedRecordWhoseLoadedHandlerFailsIsWrittenNotLost`, a zero balance without it); a critical write that failed on a
    copy leaving by handoff travels in the record, and the new owner writes it at once
    (`ACriticalChangeWhoseWriteFailedIsWrittenAtOnceByItsNewOwner`, lost on the new owner's crash without it); a
    restart of the link's stream closes a handshake that said Hello with the old stream id (no deterministic test:
    it needs the peer to restart while the handshake is in flight).
  - Planned client moves (agreed: a move marker): each actor that takes the new session sends `SessionMoved` to the
    previous one behind its last push there, and the resume's reply says how many actors took it; the client goes on
    with the new connection's pushes once that many markers came on the old one (or the call timeout), then says
    goodbye; the old node takes the session out of every group before answering the goodbye
    (`APushStillOnItsWayToTheOldSessionComesBeforeTheNewOnes`, the push arrived after the new one without it). A group
    broadcast fanned out between the new session's join and the old one's goodbye still reaches both: duplicates in
    that window need per-group sequence numbers, not adopted.
  - Kept, with the reason (each needs rare events together, and its fix costs more than it saves):
    - A resume reverted after the previous session's late close was already consumed leaves that session granted
      until the actor unloads (the close of a superseded session is consumed on every resume; keeping it for a revert
      would grow per resume). It needs the previous session's grace to end during the new connection's resume and
      that connection to die before the resume completes.
    - A resume whose reply is lost cannot be retried with the previous ticket (Superseded): the client reports
      `ReconnectFailed` and logs in again; the new session closes after its grace.
    - A stopped shard's handoff memory is gone, so a resend reaching it goes unanswered and its sender falls back to
      the store once that node left (versions decide). It needs a lost answer and the receiving node scaling in within
      one handoff timeout.
    - Sessions of a drained node reach `SessionClosed` one view removal after the grace (the drained node counts as
      present while Leaving).
- [x] Self-review round 6 (ownership and handoff; links, sessions and the client):
  - A copy abandoned while waiting for its record told its asker "none" while the real holder still had the state
    (two quick joins): it sends the asker to the node it waited for. A shard remembers for the move window where it
    handed each key's record, so a later ask for a key gone from it is sent there instead of hearing "none". A copy
    adopted from a sender ahead of this node's view, for a key its asker owns here, leaves when asked. Recent views
    are bounded by the move window (up to 256), not cut at 16. A failed `Loaded` handler writes the record as it came.
  - A resume answered after its asker timed out took the session too late (the actor then belonged to neither
    session): a late resume is dropped, and the client-facing node reverts the grants that timed out or disconnected.
    The new session joins the actor's group before the old one leaves it, so a group push in between reaches one of
    them. A link resumed while the view thread found it down is no longer reset under it; a flush cut short by a
    closing connection does not publish it (a later send would overtake the rest). A session joins no group after its
    goodbye. The resume reply's format changed, so the client protocol is 4.
  - No deterministic tests for these interleavings (each needs messages to cross at one point); the existing handoff,
    resume and move tests pass, and the sample E2E runs.
- [x] Self-review round 7 (the whole distributed runtime under the round-7 criterion; every change since the release
  checklist against CLAUDE.md):
  - Must-fix: a resume whose session closed while the actors were asked did not revert the actors whose answer timed
    out, so an actor that took it before its deadline never heard `SessionClosed`: every timed-out or disconnected
    grant is reverted, closed or not.
  - A revert puts the actor's group back on the previous session; a shard forgets where it handed a key once the key
    is here again (an asker no longer bounces between two nodes); `Handoff.cs` and `ClusterClient.cs` split by concern
    (`Handoff.Memory.cs`, `ClusterClient.Pushes.cs`); ADR-026 and ADR-072 updated; the failed-`Loaded` test now
    changes the state before throwing, so it fails without the as-received write.
  - Also from round 7 (should-fix): a flush cut by a closing connection reconnects only after that connection's bytes
    reached the resend window; a node owning a key in its own view defers an ask (`MustWait`) instead of sending the
    asker where it handed the key earlier. Kept: a revert routed by a newer view can overtake the late resume it undoes
    (two faults: a view change and a timeout on one resume).
  - Kept: no deterministic tests for "no group join after a goodbye" and "a late resume is dropped" (the first is
    indistinguishable from a closed session through the client API, the second needs a resume queued past its deadline
    behind other mail); an actor reverted after a timeout may have sent a `SessionMoved` that a moving client counts (its
    pushes then keep their order but may overtake a real taker's last push in that one case).
- [x] Self-review round 8 (the distributed runtime as a whole; rounds 5 to 7 against CLAUDE.md):
  - Must-fix: an exception from any ECS system (a user `[ForEach]` system's bug) ended its shard's loop for good: the
    shard's actors were dropped unsaved, their keys forwarded in a circle, and `/alive` stayed healthy. A system that
    throws is caught per system now (round 9: `SystemFailed`); a shard whose loop still crashes keeps its pass start, so
    the watchdog reports it stalled and `/alive` fails.
  - Must-fix (test): the critical-write handoff test counted every store write, so a's own retries could satisfy it
    before b wrote; it now waits for each moved key's landed write.
  - Also from round 8 (should-fix): a timer wheel scheduled after a long idle period no longer walks every idle tick;
    a moving client waits for its `SessionMoved` markers by a completion instead of polling, and `SessionMoved` is
    internal; a real-cluster test checks that the node's count of actors taking a session equals the markers it sends;
    `SendAfter` and ADR-058 say that delayed sends do not outlive their node; stale move-window comments.
  - Kept: delayed sends are not handed over on a graceful stop (sending them early would break "never earlier", and
    a deadline that must survive is state); `Handoff.cs` and `Membership.cs` are at the size limit (the next change
    to either splits it); the two eviction loops of `HandoffsSeen` stay apart (one also records how far it forgot).
- [x] Self-review round 9 (the distributed runtime; the round-8 changes against CLAUDE.md):
  - Must-fix: round 8 guarded each group's pass, not each system: a user system throwing on every pass in
    `ShardMessages` skipped the runtime's mail, load, save and unload systems after it. EasyEcs now catches per
    system (`SystemErrorPolicy.Disable`); the shard reports the error (`SystemFailed`, at most once a second per shard)
    and enables the system again for its next pass.
  - Must-fix: one actor whose state does not serialize threw on every pass of the persistence system, starving the
    shard's other writes, its handoffs and its stop; its write is now given up (the store keeps the last record, a
    leaving copy goes) and so is a handoff of it (it falls back).
  - Must-fix: without a store, an actor whose first handler threw left the key loading for good; a `Created` or
    `Loaded` handler that throws now fails the load, and the message that loaded it is answered with `Error` instead of
    a timeout.
  - Must-fix: a shard that crashed while building its world (a system's `OnCreate`) read as
    healthy; it reads as stalled from before its world is built, so `/alive` fails.
  - Also (should-fix): a test for the idle timer wheel's skip; the move's timeout delay is cancelled; stale
    "handoff timeout" comments; the throwing-system test waits by condition and resets its flag.
  - Kept: a delayed send whose delivery throws part-way through a wheel slot leaves that slot's later entries for the
    next round of the wheel (never early; delivery only enqueues); two moves overlapping (the new connection retired
    while the previous move still drains) wait the call timeout before the first goodbye, as before; a node whose shard
    fails to start still joins and is restarted by `/alive` rather than refusing to start.
- [x] Self-review round 10 (every place the runtime calls user code or reads user data; the round-9 changes):
  - Must-fix: a record whose receiving actor threw while being set up (its `Loaded` handler) was never answered: the
    sender resent it while the receiver lived, or (the handler throwing after the record was taken)
    the receiver kept a copy that never ran while the sender dropped its own. Now refused, so the sender writes it.
  - Must-fix: a reclaimed copy (a refused or unanswered handoff) whose `Loaded` handler threw kept unloading for
    good; it falls back to its final write either way.
  - Must-fix: a feature's `DrainAsync` that threw (or ran out of budget) skipped the actors' unload; every feature hook
    (drain, stop, member dead, view changed) is guarded (`FeatureFailed`) and the node's own steps run.
  - Must-fix (tests): the stalled-shard test could reset its flag before the shard built its world; the idle timer
    test's 100 ms bound included NUnit's work. A stale round-8 line here.
  - Also (should-fix): system failures count in `daserver.handler.failed` (and its alert); repeating errors share a
    once-a-second log limit per shard; a client event subscriber that throws is reported through `HandlerFailed`
    instead of stopping the reconnect; a test that an unserializable state does not move.
  - Kept: an unreadable record refused by a copy that has mail loads from the store at once, possibly before its
    sender's fallback write lands (versions then fence it; needs a record one build cannot read); a load or save
    batch's `Apply` has no `finally` around its loop (nothing reachable throws there outside the per-actor guards).
- [x] Self-review round 11 (the whole distributed runtime; the round-10 changes against CLAUDE.md):
  - Must-fix: a write that landed but whose answer was lost (a reset after the commit, a failover) made the retry
    expect the old version, so the store refused the owner as a stale one and the changes it acknowledged since were
    lost. Writes carry their hash, the store keeps it, and a retry names its lost write: a store that took it writes
    after it (ADR-068; `IActorStore` and the PostgreSQL table changed, decided with the owner).
  - Must-fix: a feature's pulse source that threw ended every gossip round before failure detection: a crashed peer
    was never declared dead. Each source is guarded.
  - Must-fix: a reconnected connection that closed before the reconnect flag cleared was never reported or replaced.
  - Must-fix (docs): ADR-072 and `Handoff.Restore` did not list the refusals added in rounds 9 and 10.
  - Also (should-fix): `Maintain` is guarded per feature (one helper for every feature hook, logged at most once a
    second per node); a drain that ignores its token ends with the stop budget; a copy with mail that refuses a record
    asks its sender again after the refusal, so it reads the sender's write instead of racing it; a reclaim that
    cannot read its own record writes nothing; every subscriber of a client event runs; docs of the failure metric.
  - Kept: only the latest lost write is remembered (two lost answers in a row can fence the owner); a drain that
    ignores its token takes the whole stop budget, leaving the unload none (the shards still stop and write while the
    process lives); a reclaim whose initializer throws has no deterministic test (it needs a refused handoff and a
    state not in the record).
- [x] Nino 4.0.0-rc.6 and the reviews of duplication and coupling (October 2026):
  - BenchmarkDotNet, rc.4 (3997eb8) against rc.6 (a794210), each class twice in both orders: calls 1 local/remote
    4.45, 4.48 / 131, 129 us -> 4.16, 4.50 / 129, 134 us; 1024 remote 1.17, 1.17 -> 1.17, 1.18 ms; messages
    local/remote 479, 475 / 843, 803 ns -> 506, 470 / 835, 816 ns; broadcast 1,000 x 9 7.86 -> 7.36, 7.77 ms, 5,000 x 9
    42.9 -> 41.2 ms; scenario 3,000 / 9,000 22.2, 21.2 / 66.5, 67.0 ms -> 22.3, 22.6 / 65.8, 65.4 ms; allocations
    unchanged (an outlier of 16-20 KB in one run each side: pool growth). Even: the benchmarks' messages are raw
    ints, which both copy as memory; rc.6 pays in bytes (Latin-1 strings, packed structs) and in Unity's lists.
  - Duplication removed: one test store (`TestStore`, `StoreTest`), shared test helpers, test files split by
    section; one `LogThrottle`, one `SigningKeys`, `Platform.After`, one handoff `Notify`; `Handoff`, `Node` split.
    Coupling: closes handled by the runtime, not a filter; Hosting takes `INodeContext` and lets `ClientConnections`
    accept WebSockets.
  - Kept: the frame channels' send and receive loops (`Connection`, `WebSocketChannel`, the benchmark's pipe channel)
    stay separate until a transport benchmark proves one shared loop costs nothing; `ClientConnections` still uses
    the node's internals (a capability of the runtime's own assembly, ADR-048) beyond `INodeContext`; configuration
    keys spelled in the Aspire integration (`DaServer__...`) are not shared constants yet.
- [x] ECS rewrite (agreed October 2026, ADR-081): everything is components, no inheritance, no polymorphism, no
  objects with state; only the DI shell (Generic Host, ASP.NET Core, Aspire) stays object-oriented.
  - [x] EC0 EasyEcs (4.2.0, now 4.3.0): ordering by `[UpdateBefore]`/`[UpdateAfter]` along the data flow (no priorities),
    failed systems reported and enabled again, `Set`/`GetOrAdd` for `Add`, `EntityId` in components.
  - [x] EC1 The design as an ADR (ADR-081): an actor is an entity whose identity is a component (`ActorKey`); handlers
    are stateless static functions of message, components and a context value; an answer to an ask is a message (no
    await in handlers); settings are attribute constants, the lifecycle is mail.
  - [x] EC2 The runtime on it: `ActorKey` indexed, the mailbox a `DynamicBuffer<Envelope>` used only while the actor is
    not live, generated dispatch of static handlers (components passed `ref`/`in`, added with their default), asks
    answered as `Answered` mail, replies kept as `PendingReply` entities (`ReplyTo`, `ReplyAfter`); the turn machinery,
    call chains and cycle detection, the long-turn watcher, `ReplyWhen`, the service-call entities, the actor directory
    and pools, and the `Actor` and `Service` base classes deleted.
  - [x] EC3 Sessions and persistence as data: an actor's known sessions are `KnownSession` child entities (tags `Known`,
    `Granted`, `Superseded`, indexed by session); `Created`/`Loaded`/`Unloading` mail; the generator declares
    `ITracked` for `[Persist]` components, `ChangeSystem` tags written actors `Unsaved` and `CheckpointSystem`
    writes only those.
  - [ ] EC4 The transport and the capabilities as ECS. Superseded by W5 (`Connection` actor entities and presences).
  - [x] EC5 Samples, tests and benchmarks on static handlers; the analyzers updated (async handlers rejected by DAS001,
    the rules of the removed model gone, DAS022: a component parameter used after a structural change of its entity).
  - [ ] EC6 Records as EasyEcs saves (needs component sets, Nino encoding and stable type ids in EasyEcs). Superseded by
    W6.
  - [ ] EC7 BenchmarkDotNet against the EC0 baseline (the README's numbers are from before the rewrite), the 10k-bot
    run and the flash sale, review rounds. Superseded by W9.
- [x] Self-review rounds 12-15 (the distributed runtime and the rules, tests and docs, each round two reviewers):
  - Must-fix: a state that changed back after a lost write answer was never written again (a write in doubt is now
    written whatever its hash, and a claim clears the stored hash); a link whose handshake closed before its connect
    returned stayed down until the peer was declared dead; closes and resumes for a moving actor were refused (S10);
    stale docs after the protocol levels (ADR-065, ADR-076, design, C3); claims of the round-13 fixes without tests.
  - Also (should-fix): the manifest compares a keyed member of another assembly's type again and asks to record a
    keyed rename; `MapSend` refuses a Nino body that is not the message; the HTTP plane resolves the node internally;
    unreadable closes and resumes are refused, not thrown out of the mailbox; allocation tests check pool growth;
    `ShardDispatch.Sessions.cs`.
  - Kept: a close or resume reaching a key moved within the move window loads its actor from the store when its
    previous owner held nothing (it forgets nothing, and stays loaded like any actor: until it idles out when `UnloadAfterIdleSeconds` is set); a close that bounces between
    two nodes with lasting different views reaches `MaxHops` and is dropped, so that actor keeps the closed session
    until its client-facing node leaves; the link test cannot force the early-close window every run.
- Review criterion from round 7 on (agreed): a finding is must-fix when a single fault (one view change, one link
  restart, one node crash, one slow store call) leads to lost acknowledged state, a second live copy or a wait that
  never ends; one that needs two or more rare events together is should-fix and is listed here. Convergence is two
  rounds in a row without a must-fix.


## Generator / analyzer (`src/DaServer.Generator`)
- [x] Messages instead of contracts (ADR-035): typed `Ask` overloads per request, one dispatcher per `[Handle]`
      method, client message registration, message ids
- [x] Hosting: actor and service types with their generated dispatchers, client handler classes, EasyEcs system registration
- [x] A node's `Program.cs` is five lines: `AddDaServer`, `MapDaServer` (ADR-021)
- [x] Incremental rules: syntax predicates, equatable models, tracking names, cache test (ADR-016)
- [x] `DaServerAnalyzer`: the DAS rules (handlers, messages, wire types, id collisions, message flow
      (ADR-038), actors, services and systems without fields (ADR-044), address kinds (ADR-047), shared logic
      (ADR-055), static non-`async` handlers (ADR-081), a component used after a structural change (DAS022))

## Hosting / orchestration
- [x] `DaServer.Hosting`: Generic Host, configuration, OpenTelemetry (OTLP), periodic stats line, graceful shutdown
- [x] `DaServer.Aspire`: AddDaServerCluster / AddNodes / WithClientConnections / WithClientEndpoints; Compose and
      Kubernetes publishing (ADR-015, ADR-030); no configuration files in node projects
- [x] 10k-player scenario: login, game, map, 3 gateways, 10k bots (Sample.Bot fails on any gap, disconnect or
      p99 over the limit); passes through Aspire (README "Performance")
- [x] E2E in CI: `tests/DaServer.E2E` runs the AppHost with `Aspire.Hosting.Testing` (1000 bots in CI) and
      fails unless the bot exits 0
- [x] Aspire: run, one resource per replica (members are addressed individually); published, one workload per group
      seeded through its DNS name (ADR-030)

## Release review
- [x] The release checklist: its current record is in "Open after G" (7c5b50b); R1-R11 and E1-E10 hold, open items
      are the unchecked roadmap entries
- [x] Naming review (ADR-032, ADR-040): `Loaded`/`Unloading`, `Caller`, one noun per concept; components start
      as their defaults, `ActorKey` for systems
- [x] Moving scenario: 10,000 bots in one map (AOI and dead reckoning, ~2.5M positions/s); servers 0–5 KB/s, no GC;
      scale-out: a gateway started mid-run
      receives the new clients (test + process run)
- [x] One write per connection per batch (ADR-034)
- [x] Message-first model (ADR-035); client send backlog bound 256 KB (a slow client is disconnected and
      resynchronizes when it resumes)
- [x] Auto-reconnect (ADR-036): signed resume tickets, `SessionClosed` after a grace period, `SessionResumed`,
      Aspire-generated signing secret
- [x] Lifecycle as tags (ADR-037): `Ready`/`Loading`/`UnloadRequested`/`Leaving`/`Removing` with one system each
      (no polling); sample grid as data + systems (`DespawnSystem`, `PlacementSystem`)
- [x] `[Message]` (ADR-038): `[NinoType, Message]` is a message, plain `[NinoType]` is data; `[FromClient]`/
      `[ToClient]` checked at build time (DAS006)
- [x] EasyEcs from NuGet, used as its docs teach (ADR-039): cadence as system groups (`ShardRebalance`,
      `ShardIdleScan`), `UpdateInterval` in samples, `ActorKey` as the only identity, timer queue removed

## Quality
- [x] Unit tests (queue, pool, frames, pending table, membership, placement)
- [x] Generator/analyzer tests including incremental caching
- [x] In-process cluster integration tests (`ClusterTest` fixture)
- [x] Allocation tests: cross-shard and cross-node actor calls, replies of suspended handlers, call bursts, one-way
      messages, service messages, refusals and relayed timeouts, actor churn, client fan-out, client messages through
      a client-facing node to another node, gossip rounds, resend windows and idle nodes allocate nothing per operation
- [x] BenchmarkDotNet suite: RPC (1/64/1024, local/remote), one-way messages (local/remote), group broadcast (1k/5k
      sockets), broadcast to everyone through 3 client-facing nodes (3k/9k sockets)
- [x] CI workflow (build, test, end to end; EasyEcs from NuGet)
- [x] Requirements and release checklist ([requirements.md](requirements.md))
- [x] Record benchmark numbers of this release in the README
- [x] Test: calls in flight to a crashed node fail when it is declared dead
- [x] Tests: hostile client frames beyond the size check (trailers or an extension block longer than the frame close
      the connection; an extension entry running past its block or a payload that does not read is refused as
      malformed; the node serves everyone else: `HostileFramesHurtOnlyTheirConnection`), suspect flapping (a member
      whose links drop for 0.9 s five times is suspected, never declared dead, and keeps its keys and actors:
      `AFlappingLinkMakesAMemberSuspectNeverDead`).
