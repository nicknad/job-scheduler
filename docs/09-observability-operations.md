# Observability and Operations

Observability exists to answer three operator questions **without reading source or tailing raw
logs**:

1. **What work was done?** — executions, schedules fired, lifecycle operations, reconciliation
   repairs.
2. **What work was not done, and why?** — rejected, skipped, missed/misfire, failed, timed out,
   cancelled, interrupted.
3. **What is written down?** — durable execution history, audit trail, per-execution logs, all
   reachable through the CLI.

The surface is **CLI-first**: `Scheduler.Cli` is the primary operator tool and a thin client of the
management API; `Scheduler.Host` exposes the HTTP endpoints that serve it.

## Only work-done / not-done signals

Every number means "work happened" or "work did not happen". Infrastructure telemetry (CPU, memory,
GC, HTTP latency, thread-pool, connection-pool) is explicitly out of scope. In-process
`System.Diagnostics.Metrics` counters are **deferred** and must not be added until scraping exists:
the durable numbers are SQL aggregates over `executions`, `execution_rejections`, `schedule_events`,
`reconciliation_runs`, and `operations`, so they survive restarts.

| Signal | Source of truth | Meaning |
| --- | --- | --- |
| Executions started / completed by outcome (`succeeded`\|`failed`\|`timedout`\|`cancelled`\|`interrupted`) | `executions` | work done + result |
| Executions rejected by reason | `execution_rejections` | work not done + why |
| Retries | `executions.attempt` | work repeated |
| Active executions | `executions` where `status = Running` | work in progress |
| Duration | `executions.started_at`/`ended_at` | how much work/time |
| Schedules fired / missed / skipped | `schedule_events` (Quartz trigger listener) | scheduled work done / not run |
| Lifecycle operations by action / outcome | `operations` | lifecycle work done / not done |
| Reconciliation repaired / failed | `operations` + `reconciliation_runs` | repair work done / not done |
| Audit trail | `audit_log` | who did what, when |

### Rejection reasons

Work that is not admitted is recorded durably with a structured reason (the `ExecutionRejectionReason`
token) rather than only thrown:

| Reason | When |
| --- | --- |
| `NotFound` | no job with that id |
| `Disabled` | job definition is disabled |
| `ModeUnavailable` | job's `executionMode` is not served by this host |
| `NoActiveVersion` | the job's plugin has no active version |
| `NoHandler` | active plugin version resolves no handler for the job |
| `Concurrency` | admission was abandoned while waiting for a concurrency slot; the default gate queues rather than rejecting, so this is recorded only when a waiter is cancelled |

## Correlation identifiers

A correlation id is generated at dispatch and persisted on the execution row
(`executions.correlation_id`). It links an execution's history with its per-execution log (the log
file is keyed by execution id) and is carried in the host structured-log scope (`ExecutionId`,
`CorrelationId`). Audit entries are not correlated. This is the only "tracing": no distributed
tracing beyond it.

## Execution history

- `executions` records job id, plugin id/version, pinned configuration revision, attempt, status,
  timestamps, sanitized result, cancellation reason, and correlation id.
- Status flow: `Pending → Running → Succeeded | Failed | Cancelled | TimedOut`, plus
  `Running → Interrupted` for crash recovery.
- `Interrupted` marks executions left `Running` after a host crash. On startup the host transitions
  every `Running` row to `Interrupted`; recovery is explicit (retry / mark failed / manual) and
  **never auto-retried**.

## Per-execution logs

Plugin log output is **persisted per execution** and exposed for retrieval, not merely forwarded to
the console:

- Port `IExecutionLogStore` with a file-backed JSONL implementation at
  `{LogsRoot}/{executionId}.log`, one JSON object per line (`timestamp`, `level`, `message`,
  `exception`). File-per-execution is chosen over a DB table because logs are large, append-heavy,
  and not queried in aggregate; the DB keeps only the indexed history and the log path is derived
  from the execution id.
- Plugins receive an execution-scoped `IJobExecutionLogger`/`IJobProgressReporter` created per
  execution by `IExecutionLoggerFactory.CreateLogger(ExecutionIdentity)` /
  `CreateProgressReporter(ExecutionIdentity)`. This replaces the process-wide
  `NullJobExecutionLogger`/`NullJobProgressReporter` singletons, because the backend is a singleton
  and cannot carry per-execution state.
- Retention trims the oldest log files beyond a configured count at startup.
- `GET /api/executions/{id}/logs` returns the captured entries. Captured plugin log text is
  plugin-authored and stored verbatim; the platform never writes secret values it resolves, and
  execution result summaries are sanitized.

## Host structured logging

The host logs through `Microsoft.Extensions.Logging` with a JSON console sink and a rolling
newline-delimited JSON file under the configured `LogsRoot` (`host-{yyyy-MM-dd}.log`). Execution and
lifecycle scopes carry `ExecutionId`/`CorrelationId`/`JobId`/`PluginId`/`PluginVersion` where
applicable. No third-party logging dependency is used.

## API surface

| Endpoint | Purpose |
| --- | --- |
| `GET /api/executions?jobId=&status=&since=&until=&limit=` | execution history with filters |
| `GET /api/jobs/{id}/executions` | one job's executions |
| `GET /api/executions/summary?window=` | **centerpiece**: done/not-done counts + reasons |
| `GET /api/executions/{id}` | one execution with its correlation id, result summary, and cancellation reason |
| `GET /api/executions/{id}/logs` | captured per-execution log entries |
| `GET /api/audit?actor=&action=&target=&since=&limit=` | real, filterable audit trail |
| `GET /api/health` | detail: DB reachable, reconciler last success, stuck executions |
| `GET /healthz` | liveness only (unchanged) |

## CLI surface

`Scheduler.Cli` is a thin HTTP client (base URL from `--api` argument or `SCHEDULER_API_URL`,
default `http://localhost:5000`). It re-implements no logic and returns meaningful exit codes:
`0` = done, non-zero = failed/not-done.

| Command | Behavior |
| --- | --- |
| `status` | summary (done/not-done) |
| `job list` | list jobs |
| `job run <id>` | manual run; exit non-zero when rejected (prints the reason) |
| `job history <id>` | one job's executions |
| `execution list --status failed --since 24h` | filtered executions |
| `execution show <id>` | status, why-not, correlation id, logs |
| `audit tail` | recent audit entries |
| `health` | health detail |
| `plugin list` / `plugin install/validate/activate/deactivate/rollback/remove` | lifecycle |

## Health

Liveness stays at `/healthz`. Operational health (`GET /api/health`) reports:

- **database reachable** — a trivial query succeeds;
- **reconciler** — last successful sweep time and last error count (in-process status; the durable
  run history is `reconciliation_runs`);
- **stuck executions** — count of `Running` executions whose `started_at` is older than the
  configured heartbeat threshold, and the affected ids.

A stuck execution is surfaced, not auto-killed; heartbeat monitoring and drain-timeout shutdown
land with secrets/hardening. `/healthz` stays a liveness probe; the operational checks above are
computed by `IHealthReportService` and exposed only at `GET /api/health`.

## Recovery and resilience

- Startup: apply pending migrations → **classify `Interrupted`** (before the host serves requests) →
  the reconciler runs its startup sweep in the hosted service before new fire times are accepted.
- Preserve active plugin versions when a new version fails validation or activation.
- Graceful shutdown: stop accepting fire times, apply the drain timeout, then exit.
- Backups: consistent database + artifacts ([07-persistence.md](07-persistence.md)).
