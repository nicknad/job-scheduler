# Plugin Lifecycle

## State machine (per plugin *version*)

```
Uploaded → Validating → Staged → Activating → Active
   │           │           │          │          │
   └──────────►└─► Rejected   └─► Failed ───────┤
                                               │ update / deactivate
                                               ▼
                                            Draining → Retired ──(retention)──► Removed
```

Transitions are driven exclusively by the plugin manager and persisted durably. Plugin lifecycle
is independent from job-definition state: an **active** plugin can have **disabled** schedules.

| State | Meaning |
| --- | --- |
| Uploaded | Package received into staging |
| Validating | Signature/hash/manifest/contract validation in progress |
| Rejected | Validation failed; result persisted |
| Staged | Verified artifact promoted to immutable storage |
| Activating | Load + job discovery + definition validation + atomic publication + trigger reconciliation |
| Active | The published desired version; receives new executions |
| Draining | No new executions; running executions finish or are cancelled per drain policy |
| Retired | No executions reference this version; package retained for rollback |
| Removed | Physical deletion allowed after the retention/audit period |
| Failed | Activation failed; previous active version (if any) is preserved |

## Install

1. Receive the package (upload or artifact reference) into the staging root.
2. Validate: archive safety → manifest → digest → signature → assembly-level checks
   ([02-plugin-package.md](02-plugin-package.md)).
3. Persist the validation result.
4. Promote the verified artifact to immutable, content-addressed storage.
5. Nothing here affects the active version.

## Activate

1. Confirm the target version is `Staged` and valid.
2. Load the plugin into its own collectible `AssemblyLoadContext`; discover job definitions.
3. Validate definitions: identifier uniqueness, schedule validity, parameter schema, required
   capabilities vs. granted permissions.
4. **Atomically publish** the new active version in the registry (single-row activation record +
   the version's state → `Active`). "Active" is only claimed after this success point.
5. Reconcile its job definitions and triggers with Quartz (via durable operation records).
6. Record the outcome in the audit log.

If the process dies mid-activation, the reconciler finishes or rolls back the operation from the
operation record; the previous active version keeps serving until publication succeeds.

## Update (version replacement)

1. Install and validate the new version with **zero impact** on the active version.
2. Stop dispatching new executions to the old version (`Draining`).
3. Atomically publish the new version; new executions target it.
4. Apply the configured drain policy to old-version executions:
   - `Wait` — drain within the configured drain timeout;
   - `Cancel` — cooperative cancellation.
5. Unregister old definitions no longer present; attempt to unload the old assembly context.
6. Retain the previous package for rollback (`Retired`, not deleted).

**Invariant:** updating a plugin never changes the behavior of already-running executions. Each
execution pins its plugin version and configuration revision at dispatch time.

## Rollback

1. Target version must be previously validated and still retained.
2. Stop new executions on the current version, activate the rollback target (same path as
   activation), drain per policy, retain the current version.
3. No host restart; the operation is a normal activation of an older version.

## Remove

1. Disable scheduling first; reject new executions once deactivation starts.
2. Apply the drain/cancel policy.
3. Release registrations, handlers, tasks, and references.
4. Attempt assembly unload; **treat unload failure as an operational condition** (alert, mark
   unclean) — never as proof of removal. Unloading is cooperative: lingering references or
   long-running work can keep an `AssemblyLoadContext` alive.
5. Retain the artifact for the configured rollback/audit period, then delete physically.

## Crash-safety

Every lifecycle operation is persisted as a durable operation record *before* work begins and
updated at each phase boundary. On startup, the reconciler finishes or rolls back every
non-terminal operation, then converges Quartz to the registry. See
[07-persistence.md](07-persistence.md) and [11-implementation-plan.md](11-implementation-plan.md).
