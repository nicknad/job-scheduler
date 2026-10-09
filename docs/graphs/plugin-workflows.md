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
Applying schedules to Quartz (the outbox → reconciler step) is phase 4.

```mermaid
flowchart TD
  REG[("registry: version = Staged")] --> ACTAPI["PluginManager.ActivateAsync / RollbackAsync"]
  ACTAPI --> LQ["verify Staged/Retired + artifact present"]
  LQ --> LC["PluginLoadContext (collectible ALC)<br/>entry assembly from artifact store"]
  LC --> DISC["IJobPlugin.GetJobs() +<br/>IJobHandlerFactory.CreateHandler(jobId)"]
  DISC --> DEF["PluginDefinitionValidator:<br/>shape, plugin identity, unique job ids"]
  DEF --> ACC{"all valid?"}
  ACC -- "no" --> FAIL["version = Failed (audited)<br/>previous active preserved"]
  ACC -- "yes" --> ACT[["PUBLISH (one transaction):<br/>plugin_activation row + version = Active<br/>+ discovered job definitions"]]
  ACT --> RETIRE["previous active: Draining → drain policy →<br/>unload; Retired (clean) or unclean marker"]
  ACT --> AUD[("audit log")]
  ACT -. "phase 4" .-> OUT["operations outbox: ScheduleChange"]
  OUT -.-> REC["Reconciler"]
  REC -.-> QZ[("Quartz ADO.NET store")]
```

## Execute (Phase 3 — implemented)

Manual runs enter through `IJobManager.RunNowAsync`; scheduled runs enter from
Quartz once phase 4 lands. The dispatcher resolves the active version, pins it
with the configuration revision, admits the execution under the concurrency
gates, and runs it to a terminal state. Retries loop inside the runner against
the already-pinned handler. Worker execution is phase 7.

```mermaid
flowchart TD
  RUN["IJobManager.RunNowAsync(jobId)<br/>(manual run)"] --> DISP["Dispatcher.DispatchAsync(jobId)"]
  QZ[("Quartz fires trigger")] -. "phase 4" .-> DISP
  DISP --> RES["resolve active version from plugin_activation"]
  RES --> PIN["pin plugin version + configRevision<br/>+ resolve handler"]
  PIN --> CONC{"ConcurrencyGate:<br/>global limit (default 8),<br/>per-job no-overlap default"}
  CONC -- "blocked" --> WAIT["wait for a free slot"]
  CONC -- "admitted" --> MODE{"executionMode"}
  MODE -- "in-process" --> IP["Runtime.InProcess:<br/>build JobExecutionContext scope"]
  MODE -. "worker (phase 7)" .-> WK["Runtime.Worker:<br/>authenticated local IPC"]
  IP --> H["IJobHandler.ExecuteAsync<br/>(timeout + cancellation token)"]
  WK -.-> H
  H --> EXEC[("execution store:<br/>Pending → Running → terminal")]
  EXEC --> RET{"failed &amp; retryable?<br/>RetryPolicyEvaluator"}
  RET -- "yes, attempts left" --> BACKOFF["backoff delay"]
  BACKOFF --> H
  RET -- "no" --> DONE["Succeeded / Failed / TimedOut / Cancelled"]
  DISP --> CANCEL["Dispatcher.CancelAsync(executionId)<br/>cooperative cancellation"]
  CANCEL --> H
```

## Modules & layering

```mermaid
flowchart LR
  subgraph Ctrl["Control plane (implemented)"]
    CLI["Scheduler.Cli"] --> API["Management API (Host)"]
    API --> PM["Plugin Manager"]
    PM --> REG[("Registry: SQLite")]
    PM --> ART[("Artifact store")]
    RECON["Reconciler"] --> REG
    RECON --> QZ[("Quartz store")]
  end
  subgraph Exe["Execution plane (in-process implemented)"]
    QZ --> DISP["Dispatcher"]
    RUN["Job Manager (manual run)"] --> DISP
    DISP --> RT["Runtime.InProcess"]
    DISP -. "phase 7" .-> RWK["Runtime.Worker"]
    RT --> EXC[("Execution store")]
  end
  PM -. "operation outbox (phase 4)" .-> RECON
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

Planned in later phases: the reconciler and Quartz trigger application (phase 4),
the management API/CLI lifecycle surface (phase 5), secrets/observability
(phase 6), and the worker backend (phase 7).
