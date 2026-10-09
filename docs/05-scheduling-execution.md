# Scheduling and Execution

## Model

- **Quartz decides when**; the **registry decides what and how**. Quartz's job store is a
  derived projection of the platform's job registry, kept in sync by reconciliation.
- Every trigger is derived from a stable platform `JobId` plus the job's current
  `configurationRevision`. Quartz job keys and job data are strings only (job id, plugin id,
  version, revision) — no binary-serialized .NET types.
- Schedule changes update triggers without touching plugin code. Parameter changes affect **new**
  executions by default. Plugin version changes change the dispatch target, never running
  executions.
- The dispatcher resolves the active version from the single-row activation record and pins it (with
  the configuration revision) on the execution. A job whose plugin has no active version does not
  dispatch; the failure is explicit.
- Plugins cannot modify scheduler state.
- Dispatch is invoked by `IJobManager.RunNowAsync` for manual runs and by the Quartz bridge job
  for scheduled fires; both enter the same dispatcher. Running executions are cancelled
  cooperatively through the dispatcher (an explicit cancel request or the configured drain policy).

## Trigger derivation

Every trigger is derived from the stable platform `JobId` plus the job's current
`configurationRevision`:

- The Quartz `JobKey` and `TriggerKey` are both the `JobId` inside the single group `jobs`, so a
  job has exactly one durable job detail and one trigger.
- Job and trigger data are **strings only**: `jobId`, `pluginId`, `version`, `revision`. Nothing
  is binary-serialized into the store; `StoreJobDataAsStrings` is enforced.
- Cron and fixed-interval schedules start now and repeat; one-shot schedules start at their
  `oneShotAt`. The `revision` stored on the trigger lets the reconciler refresh a stale trigger
  when the job's configuration changes.
- A one-shot is a single firing: once its `oneShotAt` has passed it is treated as consumed, so a
  completed trigger is never recreated (and re-fired) by a later sweep.
- Applying a projection is idempotent: `AddJob(Replacing)` then `ScheduleTrigger(Replace)`.

## Quartz → dispatcher path (phase 4 — implemented)

The single Quartz job type (`QuartzBridgeJob`) is registered by Quartz through DI. When a trigger
fires it reads `jobId` from the merged job data and calls `IDispatcher.DispatchAsync(jobId)`. The
bridge never reads or writes scheduling state and never resolves plugin or contract types; the
dispatcher resolves the active plugin version and pins it (with the configuration revision) on the
execution. Since one `IJob` type is used for every job, per-job no-overlap stays with the
`ConcurrencyGate` rather than the static `DisallowConcurrentExecution` attribute, so
`AllowParallel` jobs are unaffected.

## Reconciliation (phase 4 — implemented)

The reconciler (`ScheduleReconciler`, invoked on startup, after lifecycle operations, and on a
periodic sweep) converges Quartz toward the registry. Sweeps are serialized, so the periodic loop
and the post-lifecycle trigger cannot race each other:

1. **Lifecycle operations.** Every non-terminal operation record (including one stranded `Pending`
   by a crash before its first `Running` write) is resolved against the registry and driven to a
   terminal state: an activation whose version is now published is resumed (`Succeeded`); one that
   never published is rolled back (`RolledBack`); an install that never promoted fails;
   deactivation/removal that already applied succeeds. Publication is atomic (ADR-003), so "did it
   publish?" is a registry read.
2. **Outbox apply-then-mark.** Every non-terminal `ScheduleChange` record — `Pending` (never
   applied) or `Running` (crash between apply and mark) — is applied to Quartz first and only then
   marked `Succeeded`, so replay is safe and no record is stranded. The reconciler re-reads the job
   from the registry before applying; the record's payload is intent, the registry is truth.
3. **Drift repair.** The reconciler lists the live triggers and repairs toward the registry: a
   trigger with no registry basis (unknown job, disabled job, or a plugin with no active version)
   is removed; an enabled job missing a trigger is scheduled; a trigger whose revision/version is
   stale is refreshed.
4. **Structured events.** Each lifecycle resolution and the overall outcome are written to the
   audit log; per-job drift/outbox failures are collected and surfaced in
   `ReconciliationResult.Errors` while the remaining jobs still converge.

Registry and Quartz updates are not one atomic transaction — that is exactly what the outbox
operation records exist for ([07-persistence.md](07-persistence.md)).

## Misfire mapping and disabled jobs

`MisfirePolicy` maps to the Quartz instruction for the trigger family:

| `MisfirePolicy` | Cron trigger | Simple trigger |
| --- | --- | --- |
| `FireOnce` | `FireAndProceed` | `FireNow` |
| `Skip` | `DoNothing` | `NextWithRemainingCount` |
| `RunImmediately` | `FireAndProceed` | `NowWithRemainingCount` |

Cron triggers cannot express "abandon the scheduled time", so `RunImmediately` collapses to
`FireAndProceed` there. A **disabled job has no active trigger**: the reconciler removes it, and the
dispatcher independently rejects a dispatch of a disabled job. A job whose plugin has no active
version is likewise treated as unschedulable and has its trigger removed.

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
