# Testing

xUnit v3 on the Microsoft Testing Platform (MTP), in `src/Scheduler.Tests`
(unit / integration / fault-injection folders).

## Strategy

- **Unit**: pure logic — contract validation (`ScheduleSpec`, `JobDefinition`, `RetryPolicy`),
  retry evaluation, state-machine transition rules, policy checks. Fast, deterministic.
- **Integration** (in-repo, SQLite file/memory): registry + artifact store + in-process runtime +
  Quartz (RAM store for trigger semantics, ADO SQLite store for persistence), the observability
  services behind the management API endpoints, CLI via its HTTP client seam, secret authorization.
  All SQLite tests use temp directories, cleaned up per test.
- **Fault injection**: crash points during activation (kill between registry publication and
  Quartz application), mid-drain kills, corrupted/tampered packages, unload failure (plugin that
  retains a reference), secret denial paths, replayed/duplicate operations, crash-left `Running`
  executions → `Interrupted`.
- **End-to-end**: `ExampleEndToEndTests` starts the real `Scheduler.Host` as a child process (temp
  data/keys/db, loopback port, bootstrap admin credential) and drives it through
  `Scheduler.Cli`/`HttpSchedulerApiClient`, asserting the joined acceptance flow (#1, #2, #8, #10,
  #11, #12, #16, #17, #19); `ExamplePluginTests` runs the same signed example package through the
  in-process runtime for the drain `Wait`/`Cancel` paths (#18). See [13-example.md](13-example.md).

## Acceptance criteria (each becomes an automated integration test)

| # | Scenario | Gate |
| --- | --- | --- |
| 1 | Runtime installation | Install + activate a signed plugin without host restart |
| 2 | Validation failure | Bad signature/hash/contract/manifest rejected; active version untouched |
| 3 | Runtime scheduling | Add/modify/disable/re-enable schedules without rebuilding the host |
| 4 | Version replacement | Activate a new version while an old-version execution runs to completion |
| 5 | Execution draining | No new executions on the retiring version; configured drain/cancel applied |
| 6 | Rollback | Restore a previously validated version without restart |
| 7 | Crash recovery | Kill host mid-activation; restart; registry/Quartz reconcile correctly |
| 8 | Secret authorization | Plugin cannot retrieve an ungranted secret |
| 9 | Worker compatibility | Same logical job via the worker backend, same scheduling contract |
| 10 | Auditability | Install/activate/remove/**manual-run**/**secret-access** events recorded |
| 11 | Work summary | `GET /api/executions/summary` returns durable done/not-done counts (outcomes, retries, rejections by reason, schedules fired/missed, lifecycle, reconciliation) |
| 12 | Execution logs | Per-execution log entries captured and returned by `GET /api/executions/{id}/logs`; no secret values |
| 13 | Recovery classification | Crash-left `Running` executions become `Interrupted` and are never auto-retried |
| 14 | Health detail | `GET /api/health` reports DB reachability, reconciler last success, and stuck executions |
| 15 | Secret rotation | Idle rotation is picked up by the next execution; rotation mid-flight does not change a running execution's held value |
| 16 | API authentication | Unauthenticated and under-permissioned requests are rejected (401/403); a granted scope succeeds; loopback by default |
| 17 | Operation idempotency | Replaying a lifecycle request's operation id returns the same result without re-executing |
| 18 | Shutdown drain | In-flight execution completes under `Wait`; `Cancel` cancels it; a `ShuttingDown` dispatch is rejected durably; interrupted lifecycle operations are recovered on restart |
| 19 | Backup | A consistent backup restores artifact hashes and preserves lifecycle/activation state |

## Fixture rules

No real secrets in tests — dummy references and a fake value store / fake provider. Deterministic
time via injected clocks for retry/misfire/rotation/drain assertions where needed. Observability
tests read the durable tables/logs through the repositories and the API/CLI seams, never from
in-process meters. Secret tests assert that no value appears in logs, audit entries, or execution
result summaries.
