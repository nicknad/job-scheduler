# Persistence

## Local storage

SQLite (via `Microsoft.Data.Sqlite`) for the initial single-machine deployment; Quartz uses the
ADO.NET job store against the same database engine (separate schema, created from Quartz's
upstream SQLite script). All storage roots are **explicitly configured**, never the process
working directory: database, artifacts, staging, and logs. A relative database path resolves
against the host content root.

Every connection opens with WAL journaling, foreign-key enforcement, and a bounded busy timeout
(`SqliteConnectionFactory`). Access goes through `IRegistryUnitOfWork`, a single-connection,
single-transaction scope exposing the data-area repositories. A registry write and its operation
record commit together; disposing the scope without committing rolls both back.

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
| Quartz store | Triggers, job details, scheduler-specific persistence |

The schema is owned by `Scheduler.Infrastructure` (`SchemaDefinitions`); migrations are
forward-only, versioned, and applied idempotently at startup. Applied versions are recorded in
`PRAGMA user_version`; each pending migration runs in its own transaction, so a partially applied
run resumes cleanly on the next start.

Repository ports (`IPluginRepository`, `IJobRepository`, `IExecutionRepository`,
`IOperationRepository`, `IAuditLogRepository`) live in `Scheduler.Application.Persistence`;
SQLite implementations live in `Scheduler.Infrastructure.Persistence`.

`plugin_versions` additionally stores the canonical manifest JSON and the staging/artifact paths
plus the validation report (schema migration v2), so an install can be validated, promoted, and
later activated from durable state alone.

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

## Backups

Back up the registry database and the artifact store consistently (checkpoint database, snapshot
artifacts, verify artifact hashes against the registry after restore).
