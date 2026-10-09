# Persistence

## Local storage

SQLite (via `Microsoft.Data.Sqlite`) for the initial single-machine deployment; Quartz uses the
ADO.NET job store against the same database engine (separate schema, created from Quartz's
upstream SQLite script). All storage roots are **explicitly configured**, never the process
working directory: database, artifacts, staging, and logs.

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
forward-only, versioned, and applied idempotently at startup.

## Atomicity and the outbox

The registry and the Quartz store **cannot** participate in one transaction. Therefore:

1. Lifecycle and schedule changes write registry rows and an operation record in one SQLite
   transaction.
2. Operation records drive the application of changes to Quartz.
3. The reconciler processes pending operations idempotently (apply-then-mark), and on startup
   converges Quartz to the registry state.

**Activation is a single-row publication** (`plugin_activation`), which makes the desired
active version unambiguous at all times.

## Backups

Back up the registry database and the artifact store consistently (checkpoint database, snapshot
artifacts, verify artifact hashes against the registry after restore).
