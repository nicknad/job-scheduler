# Plugin Workflows

Diagrams for how plugins are loaded, stored, and executed. Sections marked
**implemented** reflect the current code; sections marked **planned** are the
Phase 3 targets the registry and artifact store already feed. See
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

## Load & activate (Phase 3 — planned)

`PluginLoadContext` exists as a skeleton today; the rest is the Phase 3 target.

```mermaid
flowchart TD
  REG[("registry: version = Staged")] --> ACTAPI["PluginManager.ActivateAsync"]
  ACTAPI --> LQ["verify Staged + valid"]
  LQ --> LC["PluginLoadContext (collectible ALC)<br/>entry assembly from artifact store"]
  LC --> DISC["IJobPlugin.GetJobs()"]
  DISC --> DEF["validate definitions:<br/>unique ids, schedule, params,<br/>capabilities vs grants"]
  DEF --> ACC{"all valid?"}
  ACC -- "no" --> FAIL["version = Failed<br/>previous active preserved"]
  ACC -- "yes" --> ACT[["PUBLISH: single-row plugin_activation<br/>+ version = Active"]]
  ACT --> OUT["operations outbox: schedule changes"]
  OUT --> REC["Reconciler"]
  REC --> QZ[("Quartz ADO.NET store:<br/>triggers / job details")]
  ACT --> AUD[("audit log")]
```

## Execute (Phase 3 — planned)

```mermaid
flowchart TD
  QZ[("Quartz fires trigger")] --> DISP["Dispatcher.DispatchAsync(jobId)"]
  DISP --> RES["resolve active plugin id+version"]
  RES --> PIN["pin version + configRevision"]
  PIN --> CONC{"concurrency gates:<br/>global limit, per-job, no-overlap"}
  CONC -- "blocked" --> WAIT["bounded queue (fair)"]
  CONC -- "admitted" --> MODE{"executionMode"}
  MODE -- "in-process" --> IP["Runtime.InProcess:<br/>JobExecutionContext scope"]
  MODE -- "worker" --> WK["Runtime.Worker:<br/>authenticated local IPC"]
  IP --> H["IJobHandler.ExecuteAsync"]
  WK --> H
  H --> EXEC[("execution store:<br/>Pending → Running → terminal")]
  EXEC --> RET{"failed &amp; retryable?"}
  RET -- "yes" --> BACKOFF["RetryPolicyEvaluator backoff"]
  BACKOFF --> DISP
  RET -- "no" --> DONE["Succeeded / Failed / TimedOut / Cancelled"]
```

## Modules & layering

```mermaid
flowchart LR
  subgraph Ctrl["Control plane"]
    CLI["Scheduler.Cli"] --> API["Management API (Host)"]
    API --> PM["Plugin Manager"]
    PM --> REG[("Registry: SQLite")]
    PM --> ART[("Artifact store")]
    RECON["Reconciler"] --> REG
    RECON --> QZ[("Quartz store")]
  end
  subgraph Exe["Execution plane"]
    QZ --> DISP["Dispatcher"]
    DISP --> RT["Runtime.InProcess / Runtime.Worker"]
    RT --> EXC[("Execution store")]
  end
  PM -. "operation outbox" .-> RECON
  CON["Contracts (no deps)"] --> APP["Application"]
  APP --> INF["Infrastructure"]
  CON --> INF
  APP --> RIP["Runtime.InProcess"]
  CON --> RWK["Runtime.Worker"]
  INF --> HOST["Host"]
```
