# Decisions

## Locked decisions

| Decision | Initial choice |
| --- | --- |
| Host lifetime | Long-running .NET Worker Service (web host for the management API) |
| Runtime | .NET 10 (LTS) |
| Plugin activation | Explicit API/CLI operation — never filesystem-triggered |
| Default execution | In-process, trusted (platform-owner-signed) plugins |
| Plugin isolation | Optional worker-process backend, same execution contract |
| Scheduling | Quartz.NET (ADO.NET SQLite job store) |
| Local persistence | SQLite with explicit paths and backups |
| Package format | Versioned archive, signed manifest, artifact hash |
| Secret management | Central provider with per-plugin authorization (references only) |
| Update policy | New executions switch to the new version; old executions drain per policy |
| Crash recovery | Durable operation records + startup reconciliation |
| Execution guarantees | At-least-once where configured; handlers tolerate duplicate side effects |
| Test framework | xUnit v3 on Microsoft Testing Platform |
| Dependency policy | Central package management, lock files, PackageGuard allowlist (permissive licenses only) |
| License | Apache-2.0 |

Additional hard constraint: **a plugin update must never change the behavior of already-running
executions.** Every execution pins its plugin version and configuration revision; the host enforces
the configured drain or cancellation policy.

## Lightweight ADRs

Format: *Context → Decision → Consequences*. Append-only; new decisions that break earlier ones
supersede with a link. Current ADRs:

- **ADR-001 Contract assembly is dependency-free and shared.** Plugins must not bundle
  `Scheduler.Contracts`; the load context redirects to the host's copy. Consequence: plugin/host
  version skew is impossible for the contract; contract evolution is additive only.
- **ADR-002 Registry is authoritative; Quartz is a projection.** Consequence: no atomic
  registry+Quartz transactions; outbox operation records + reconciliation instead.
- **ADR-003 Activation is a single-row publication.** Consequence: "active" is claimable only
  after one atomic registry write; everything before publication is recoverable without impact.
- **ADR-004 In-process execution is for trusted code.** Consequence: signature verification is
  supply-chain control, not a sandbox; the worker backend is the isolation escalation path.
- **ADR-005 Secrets resolve at the execution boundary.** Consequence: restricted per-execution
  providers, audit of decisions (not values), rotation semantics pinned at dispatch.
