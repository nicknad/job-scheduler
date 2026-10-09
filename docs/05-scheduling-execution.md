# Scheduling and Execution

## Model

- **Quartz decides when**; the **registry decides what and how**. Quartz's job store is a
  derived projection of the platform's job registry, kept in sync by reconciliation.
- Every trigger is derived from a stable platform `JobId` plus the job's current
  `configurationRevision`. Quartz job keys and job data are strings only (job id, plugin id,
  version, revision, execution id) — no binary-serialized .NET types.
- Schedule changes update triggers without touching plugin code. Parameter changes affect **new**
  executions by default. Plugin version changes change the dispatch target, never running
  executions.
- The dispatcher resolves the active version from the single-row activation record and pins it (with
  the configuration revision) on the execution. A job whose plugin has no active version does not
  dispatch; the failure is explicit.
- Plugins cannot modify scheduler state.
- Dispatch is invoked by `IJobManager.RunNowAsync` for manual runs (phase 3); Quartz scheduled
  fires enter the same dispatcher in phase 4. Running executions are cancelled cooperatively
  through the dispatcher (an explicit cancel request or the configured drain policy).

## Reconciliation (phase 4 — planned)

The reconciler (on startup, after lifecycle operations, and on a periodic sweep):

1. Resumes or rolls back incomplete lifecycle operations from durable operation records.
2. Applies pending registry → Quartz changes from the outbox (create/update/pause/delete triggers).
3. Detects drift (triggers without a registry basis, or enabled jobs without triggers) and repairs
  it toward the registry.
4. Records reconciliation results and failures as structured events.

Registry and Quartz updates are not one atomic transaction — that is exactly what the outbox
operation records exist for ([07-persistence.md](07-persistence.md)).

## Execution tracking

Every execution has a unique `executionId` and a durable record:

- Job id, plugin id/version, configuration revision (pinned at dispatch).
- Scheduled fire time, actual start time, end time, duration.
- Status and attempt number.
- Result summary or **sanitized** error (never secrets, never raw stack detail in API responses).
- Cancellation reason where applicable.

Status flow:

```
Pending → Running → Succeeded | Failed | Cancelled | TimedOut
                       └────────── Interrupted
```

`Interrupted` marks executions left in `Running` after a host crash; they require explicit
recovery classification before any retry — never auto-retry blindly.

## Retries and idempotency

- Bounded (`MaxAttempts`), configurable per job.
- Only retry failures classified `Retryable` (unknown/transient); known-permanent failures stop
  immediately. A thrown execution exception is retryable; a handler-reported `JobResult.Failed` is
  retryable only when the handler sets `JobResult.Retryable`.
- Exponential backoff with additive jitter, capped by `MaxDelay` (implemented in
  `Scheduler.Application` `RetryPolicyEvaluator`).
- No automatic retries for non-idempotent operations unless side effects are tracked.
- Assume a crash can occur **after an external side effect but before success is recorded**.
  The platform offers durable tracking and at-least-once recovery where configured; handlers must
  tolerate duplicate side effects.

## Concurrency

- Configurable **global execution limit** (bounded; default 8).
- Per-job limits; **no-overlap is the default**, parallel execution is an explicit opt-in.
- Cancellation and timeout propagate to handlers; timeouts record `TimedOut`.
- Fair capacity handling among jobs competing for execution slots (bounded queue, no starvation
  of one-shot/manual runs by recurring floods).
