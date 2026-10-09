# Testing

xUnit v3 on the Microsoft Testing Platform (MTP), in `src/Scheduler.Tests`
(unit / integration / fault-injection folders).

## Strategy

- **Unit**: pure logic — contract validation (`ScheduleSpec`, `JobDefinition`, `RetryPolicy`),
  retry evaluation, state-machine transition rules, policy checks. Fast, deterministic.
- **Integration** (in-repo, SQLite file/memory): registry + artifact store + in-process runtime +
  Quartz (RAM store for trigger semantics, ADO SQLite store for persistence), management API via
  `WebApplicationFactory`, secret authorization. All SQLite tests use temp directories, cleaned up
  per test.
- **Fault injection**: crash points during activation (kill between registry publication and
  Quartz application), mid-drain kills, corrupted/tampered packages, unload failure (plugin that
  retains a reference), secret denial paths, replayed/duplicate operations.

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
| 10 | Auditability | Install/activate/remove/secret-access/manual-run events recorded |

## Fixture rules

No real secrets in tests — dummy references and a fake provider. Deterministic time via injected
clocks for retry/misfire assertions where needed.
