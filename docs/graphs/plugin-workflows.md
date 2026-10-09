# Plugin Workflows

Diagrams for how plugins are loaded, stored, and executed. Sections and edges
marked **implemented** reflect the current code; anything dotted or labelled
with a later phase is **planned**. See
[11-implementation-plan.md](../11-implementation-plan.md),
[02-plugin-package.md](../02-plugin-package.md),
[03-plugin-lifecycle.md](../03-plugin-lifecycle.md), and
[05-scheduling-execution.md](../05-scheduling-execution.md).

## Install → validate → store (Phase 2 — implemented)

```mermaid
flowchart TD
  OP0(["Operator uploads package<br/>multipart/form-data"]) --> API["Host: POST /api/plugins"]
  API --> PM["PluginManager.InstallAsync"]
  PM --> OPP[["operations: Pending (Install)"]]
  PM --> RUN[["operations: Running"]]
  RUN --> EX["ZipPackageArchiveReader.ExtractAsync"]
  EX --> SAFE{"Archive safety:<br/>traversal, absolute/drive,<br/>symlink/special, duplicate,<br/>entry count, size, ratio"}
  SAFE -- "unsafe" --> REJECT
  SAFE -- "ok" --> EXT["ExtractedPackage:<br/>entries+sha256, manifest bytes,<br/>signature bytes"]
  EXT --> VAL["PackageValidator.Validate"]
  VAL --> G1["manifest parse + required fields"]
  G1 --> G2["contractVersion compatibility"]
  G2 --> G3["canonical digest == artifactHash"]
  G3 --> G4["signature valid (RSA/ECDSA)"]
  G4 --> G5["entryAssembly present"]
  VAL --> GATE{"all gates pass?"}
  GATE -- "no" --> REJECT
  GATE -- "yes" --> PUB{"id+version already published?"}
  PUB -- "same artifactHash" --> IDEM["Succeeded (idempotent)"]
  PUB -- "different content" --> REJECT
  PUB -- "no" --> PERS["registry: Validating<br/>(manifest_json, staging_path)"]
  PERS --> PROM["FileSystemArtifactStore.PromoteAsync<br/>staging → artifacts/{pluginId}/{version}"]
  PROM --> STAGED["registry: Staged<br/>artifact_path set, staging cleared"]
  STAGED --> OK["operations: Succeeded"]
  REJECT["Reject: registry Rejected (new versions only;<br/>published versions never mutated),<br/>discard staging, operations: Failed"]
```

## Load & activate (Phase 3 — implemented)

`PluginLoadContext` loads the entry assembly from the retained artifact;
`IJobPlugin.GetJobs()` discovers definitions and `IJobHandlerFactory` resolves a
handler per job. Publication is one registry transaction: the single-row
`plugin_activation` write plus the version state and discovered job definitions.
The same transaction writes a `ScheduleChange` outbox record per job; the
reconciler applies them to Quartz (Phase 4).

```mermaid
flowchart TD
  REG[("registry: version = Staged")] --> ACTAPI["PluginManager.ActivateAsync / RollbackAsync"]
  ACTAPI --> LQ["verify Staged/Retired + artifact present"]
  LQ --> LC["PluginLoadContext (collectible ALC)<br/>entry assembly from artifact store"]
  LC --> DISC["IJobPlugin.GetJobs() +<br/>IJobHandlerFactory.CreateHandler(jobId)"]
  DISC --> DEF["PluginDefinitionValidator:<br/>shape, plugin identity, unique job ids"]
  DEF --> ACC{"all valid?"}
  ACC -- "no" --> FAIL["version = Failed (audited)<br/>previous active preserved"]
  ACC -- "yes" --> ACT[["PUBLISH (one transaction):<br/>plugin_activation row + version = Active<br/>+ discovered job definitions<br/>+ ScheduleChange outbox records"]]
  ACT --> RETIRE["previous active: Draining → drain policy →<br/>unload; Retired (clean) or unclean marker"]
  ACT --> AUD[("audit log")]
  ACT --> OUT["operations outbox: ScheduleChange"]
  OUT --> REC["Reconciler"]
  REC --> QZ[("Quartz ADO.NET store")]
```

## Execute (Phase 3–5 — implemented)

Manual runs enter through `IJobManager.RunNowAsync`; scheduled runs enter from
Quartz's bridge job. The dispatcher resolves the active version, pins it with
the configuration revision, admits the execution under the concurrency gates,
and runs it to a terminal state. A refused dispatch is recorded as a rejection,
and a manual run is audited. Retries loop inside the runner against the
already-pinned handler. Worker execution is phase 8.

```mermaid
flowchart TD
  RUN["IJobManager.RunNowAsync(jobId)<br/>(manual run)"] --> DISP["Dispatcher.DispatchAsync(jobId)"]
  RUN -- "audit job.run" --> AUD[("audit log")]
  QZ[("Quartz fires trigger")] --> BRIDGE["QuartzBridgeJob:<br/>read jobId from job data"]
  BRIDGE --> DISP
  DISP -- "not admitted: reason" --> REJ[("execution_rejections")]
  DISP --> RES["resolve active version from plugin_activation"]
  RES --> PIN["pin plugin version + configRevision<br/>+ resolve handler"]
  PIN --> CONC{"ConcurrencyGate:<br/>global limit (default 8),<br/>per-job no-overlap default"}
  CONC -- "blocked" --> WAIT["wait for a free slot"]
  CONC -- "admitted" --> MODE{"executionMode"}
  MODE -- "in-process" --> IP["Runtime.InProcess:<br/>build JobExecutionContext scope"]
  MODE -. "worker (phase 8)" .-> WK["Runtime.Worker:<br/>authenticated local IPC"]
  IP --> H["IJobHandler.ExecuteAsync<br/>(timeout + cancellation token)"]
  WK -.-> H
  H --> EXEC[("execution store:<br/>Pending → Running → terminal<br/>+ correlation id")]
  EXEC --> RET{"failed &amp; retryable?<br/>RetryPolicyEvaluator"}
  RET -- "yes, attempts left" --> BACKOFF["backoff delay"]
  BACKOFF --> H
  RET -- "no" --> DONE["Succeeded / Failed / TimedOut / Cancelled"]
  DISP --> CANCEL["Dispatcher.CancelAsync(executionId)<br/>cooperative cancellation"]
  CANCEL --> H
```

## Reconcile & project to Quartz (Phase 4 — implemented)

The reconciler (`ScheduleReconciler`, in `Scheduler.Application`) is the only component that talks
to the scheduler, and it does so through the `IScheduleStore` port (`QuartzScheduleStore`, in
`Scheduler.Infrastructure`). It runs at startup, after each lifecycle/API mutation, and on the
periodic service; sweeps are serialized so they cannot race. Managers only publish durable outbox
records.

```mermaid
flowchart TD
  START["startup"] --> REC
  FILTER["Host ReconcileAfterLifecycleFilter<br/>(after activate/deactivate/rollback/remove/update)"] --> REC
  PERIODIC["ReconciliationService<br/>PeriodicTimer sweep"] --> REC
  PM["PluginManager / JobManager"] -- "one registry transaction:<br/>rows + ScheduleChange record" --> OUT[("operations outbox")]
  OUT --> REC["ScheduleReconciler.ReconcileAsync<br/>(serialized)"]

  REC --> S1["1. lifecycle: resolve non-terminal ops<br/>published → Succeeded, else RolledBack/Failed"]
  REC --> S2["2. outbox: apply Pending/Running ScheduleChange<br/>(apply-then-mark, idempotent)"]
  REC --> S3["3. drift repair: compare registry vs live triggers"]

  S1 --> REG[("registry (SQLite)")]
  S2 --> REG
  S3 --> REG
  REG --> DESIRED{"desired projections:<br/>enabled + plugin active +<br/>one-shot not consumed"}
  DESIRED --> STORE
  S2 --> STORE["IScheduleStore (QuartzScheduleStore)"]
  S3 --> STORE
  STORE -- "AddJob(Replacing) +<br/>ScheduleTrigger(Replace)" --> QZ[("Quartz ADO.NET store<br/>QRTZ_ schema")]
  STORE -- "DeleteJob (rogue/disabled/inactive)" --> QZ
  QZ -- "fire" --> BR["QuartzBridgeJob → IDispatcher.DispatchAsync"]
  REC --> AUD[("audit log: results + failures")]
```

## Observe & operate (Phase 5 — implemented)

The operator surface is CLI-first: `Scheduler.Cli` is a thin HTTP client of the management API and
re-implements no logic. Every number is a durable SQL aggregate, so the done/not-done picture
survives restarts. Rejections, schedule fires/misses, and reconciliation runs are recorded durably
at the point they happen.

```mermaid
flowchart TD
  OP(["Operator"]) --> CLI["Scheduler.Cli<br/>(thin HTTP client)"]
  CLI --> API["Management API (Host)"]

  API --> SUM["GET /api/executions/summary"]
  API --> HIST["GET /api/executions, /api/jobs/{id}/executions, /api/executions/{id}"]
  API --> LOGS["GET /api/executions/{id}/logs"]
  API --> AUD["GET /api/audit"]
  API --> HEALTH["GET /api/health  (DB, reconciler, stuck)"]

  SUM --> DB[("SQLite aggregates")]
  HIST --> DB
  AUD --> DB
  HEALTH --> DB
  HEALTH --> RSTAT["IReconciliationStatus"]

  LOGS --> LOGFILES[("LogsRoot/{executionId}.log<br/>JSONL per execution")]

  DISP["Dispatcher"] -- "not admitted + reason" --> REJ[("execution_rejections")]
  QZ["Quartz trigger listener"] -- "fired / missed / skipped" --> SEV[("schedule_events")]
  RECON["ScheduleReconciler"] -- "each sweep" --> RUNS[("reconciliation_runs")]
  HOST["Host startup"] -- "Running → Interrupted" --> DB

  DB --> SUM
  REJ --> SUM
  SEV --> SUM
  RUNS --> SUM
  EXEC["InProcessExecutionBackend"] -- "execution-scoped logger factory" --> LOGFILES
  HOSTLOG["Host logging: JSON console + rolling file"] --> LOGROOT[("LogsRoot/host-{date}.log")]
```

## Modules & layering

```mermaid
flowchart LR
  subgraph Ctrl["Control plane (implemented)"]
    CLI["Scheduler.Cli"] --> API["Management API (Host)"]
    API --> PM["Plugin Manager"]
    API --> JM["Job Manager"]
    PM --> REG[("Registry: SQLite")]
    JM --> REG
    PM --> ART[("Artifact store")]
    FILTER["Host post-lifecycle filter"] --> RECON
    PERIODIC["ReconciliationService"] --> RECON
    RECON["ScheduleReconciler (Application)"] --> REG
    RECON --> STORE["IScheduleStore"]
    STORE["QuartzScheduleStore (Infrastructure)"] --> QZ[("Quartz store")]
    API --> OBS["Observability: summary / history / logs / audit / health"]
    OBS --> REG
    OBS --> LOGS[("LogsRoot files")]
  end
  subgraph Exe["Execution plane (in-process implemented)"]
    QZ --> BRIDGE["QuartzBridgeJob"]
    BRIDGE --> DISP["Dispatcher"]
    RUN["Job Manager (manual run)"] --> DISP
    DISP --> RT["Runtime.InProcess"]
    DISP -. "phase 8" .-> RWK["Runtime.Worker"]
    RT --> EXC[("Execution store")]
  end
  PM -- "operation outbox" --> RECON
  CON["Contracts (no deps)"] --> APP["Application"]
  APP --> INF["Infrastructure"]
  CON --> INF
  APP --> RIP["Runtime.InProcess"]
  CON --> RWK
  APP --> HOST["Host"]
  INF --> HOST
  RIP --> HOST
  CON --> HOST
```

Quartz types stay inside `Scheduler.Infrastructure` (and the Host composition root); the reconciler
reaches Quartz only through the application-owned `IScheduleStore` port, so `Scheduler.Application`
and `Scheduler.Contracts` remain Quartz-free.

Planned in later phases: authenticated management-API surface and secret authorization (phase 6),
and the worker backend (phase 8).
