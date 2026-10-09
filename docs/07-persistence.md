# Persistence

## Local storage

SQLite (via `Microsoft.Data.Sqlite`) for the initial single-machine deployment; Quartz uses the
ADO.NET job store against the same database engine (separate schema, created from Quartz's
upstream SQLite script). All storage roots are **explicitly configured**, never the process
working directory: database, artifacts, staging, and logs. A relative database path resolves
against the host content root.

Every connection opens with WAL journaling, foreign-key enforcement, and a bounded busy timeout
(`SqliteConnectionFactory`). Quartz opens the same database file through a provider
(`PragmaApplyingDbProvider`) that applies the same pragmas to every connection it creates, so the
scheduler store shares the registry's journal mode, foreign-key and busy-timeout behavior. Access
goes through `IRegistryUnitOfWork`, a single-connection, single-transaction scope exposing the
data-area repositories. A registry write and its operation record commit together; disposing the
scope without committing rolls both back.

File-system access goes through `System.IO.Abstractions.IFileSystem`, and timestamps come from an
injected `System.TimeProvider` (e.g. the audit writer). Both are injectable so storage-provisioning
and clock failures can be exercised in tests.

## Logical data areas (kept distinct even in one database file)

| Area | Contents |
| --- | --- |
| Plugin registry | Plugin identities, versions, artifact hashes, lifecycle state, activation records |
| Job registry | Job definitions, schedules, parameters, revisions |
| Execution store | Run status, attempts, timestamps, results |
| Audit log | Who performed which action and when |
| Operations (outbox) | Durable, resumable lifecycle and scheduler-change records |
| Secret grants | Which plugin/job may resolve a secret reference (references only, never values) |
| Idempotency | Operation id → recorded lifecycle result, for replay-safe mutations |
| Quartz store | Triggers, job details, scheduler-specific persistence |

The schema is owned by `Scheduler.Infrastructure` (`SchemaDefinitions`); migrations are
forward-only, versioned, and applied idempotently at startup. Applied versions are recorded in
`PRAGMA user_version`; each pending migration runs in its own transaction, so a partially applied
run resumes cleanly on the next start.

Repository ports (`IPluginRepository`, `IJobRepository`, `IExecutionRepository`,
`IOperationRepository`, `IAuditLogRepository`, `ISecretGrantRepository`, `IIdempotencyStore`) live
in `Scheduler.Application.Persistence`/`Scheduler.Application.Secrets`/`Scheduler.Application.Security`;
SQLite implementations live in `Scheduler.Infrastructure.Persistence`.

`plugin_versions` additionally stores the canonical manifest JSON and the staging/artifact paths
plus the validation report (schema migration v2), so an install can be validated, promoted, and
later activated from durable state alone. Migration v5 adds `secret_grants` and `idempotency`;
both are created idempotently (`CREATE TABLE IF NOT EXISTS`).

## Atomicity and the outbox

The registry and the Quartz store **cannot** participate in one transaction. Therefore:

1. Lifecycle and schedule changes write registry rows and an operation record in one SQLite
   transaction.
2. Operation records drive the application of changes to Quartz.
3. The reconciler processes pending operations idempotently (apply-then-mark), and on startup
   converges Quartz to the registry state.

Operation records follow a single state machine: `Pending → Running → Succeeded | Failed |
RolledBack`. Transitions are validated on write (`OperationTransitions`); terminal states are
final. Startup reconciliation reads non-terminal operations to resume or roll them back.

**Activation is a single-row publication** (`plugin_activation`), which makes the desired
active version unambiguous at all times.

## Backup and restore

`IBackupService` (implemented by `FileSystemBackupService`) takes a consistent backup: a SQLite
`VACUUM INTO` checkpoint of the registry (lifecycle and activation rows included), a snapshot of the
artifact store, and a hash manifest. The destination is confined to the configured backup root
(`PackagingOptions.BackupRoot`, default `data/backups`); a destination outside it is rejected. A
reused destination is cleared of stale artifacts first. Verification re-hashes every recorded
artifact and requires the registry database to be present.

## Quartz store

The Quartz ADO.NET SQLite job store persists scheduling data (job details, triggers, scheduler
state) in the **same database file** as the registry but in its own `QRTZ_` schema. That schema is
created from Quartz's upstream SQLite script at startup (`ProvisionSchema`, which provisions only
missing objects) and is therefore **not** a projection of the platform schema. The registry remains
the source of truth; Quartz is reconciled toward it ([05-scheduling-execution.md](05-scheduling-execution.md)).

## Operation payloads

Operation payloads are serialized JSON with an explicit version (`PayloadVersion`); a host refuses
(rather than misreads) a payload written by a newer build. Two shapes exist:

- **Lifecycle** (`Install`, `Activate`, `Deactivate`, `Rollback`, `Remove`): the plugin id and
  version needed to resume or roll the operation back.
- **ScheduleChange**: the desired scheduling state — `action` (`Create`/`Update`/`Pause`/`Delete`),
  `jobId`, desired `schedule`, `configurationRevision`, `enabled`, plugin id/version, and
  `misfirePolicy`. It is intent only; the reconciler re-reads the registry before applying.

## Outbox application (apply-then-mark)

1. A manager writes the registry rows **and** the `ScheduleChange` operation record (state
   `Pending`) in one SQLite transaction, then returns; the manager never touches Quartz.
2. The reconciler applies the operation idempotently to Quartz, then transitions it
   `Pending → Running → Succeeded`. A crash after apply but before the mark leaves the record
   `Running` (non-terminal), so the next sweep re-applies a no-op and marks it `Succeeded` — no
   record is ever stranded.
3. On startup the reconciler resolves every non-terminal record: stranded `Pending`/`Running`
   `ScheduleChange`s are re-applied and marked, published activations resume, unpublished ones
   roll back, un-promoted installs fail, and applied deactivations/removals succeed. Sweeps are
   serialized so the periodic loop and the post-lifecycle trigger cannot race each other.
4. When a plugin update drops definitions the new version no longer provides, the publication
   transaction deletes those job rows and writes a `Delete` change for each, so their triggers
   converge away toward the registry.
