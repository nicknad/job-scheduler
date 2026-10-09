# Implementation Plan

Phased, vertical-slice plan. Each phase ends with a green build, passing tests for the phase's
gate, updated docs, and no regressions. Phases 1–6 deliver the initial release against the
acceptance checklist in [10-testing.md](10-testing.md); phase 7+ harden and extend.

## Engineering principles

1. **Registry is truth; everything else is a projection.** Quartz, caches, and the loaded-assembly
   table are rebuilt from durable state.
2. **Every lifecycle operation is durable before it acts.** Operation record written first,
   updated at each phase boundary, terminal states only on success or explicit failure/rollback.
3. **Idempotent apply, not distributed transactions.** Registry + outbox in one SQLite
   transaction; Quartz changes applied idempotently from operation records; replay is safe.
4. **Executions pin their world.** Plugin version + configuration revision are fixed at dispatch;
   updates/drains never mutate a running execution's behavior.
5. **Failure paths are explicit.** No catch-and-continue on lifecycle operations: failures are
   logged, audited, and leave durable state consistent.
6. **Contract frozen at 1.0**; additive evolution only, breaking changes via ADR
   ([12-decisions.md](12-decisions.md)).
7. **Module discipline.** Dependency direction fixed ([01-architecture.md](01-architecture.md));
   no Quartz types above `Infrastructure`; nothing reaches plugins but `Scheduler.Contracts`.

## Phase 0 — Scaffold (done)

Repo, solution (`slnx`), central package management + lock files, analyzers
(`TreatWarningsAsErrors`, `latest-recommended`), `.editorconfig`, `dotnet format` gate, git
hooks, PackageGuard allowlist, xUnit v3 + MTP, Apache-2.0, contract assembly v0 skeletons,
`PluginLoadContext` skeleton, SQLite schema DDL, IPC message contracts, host/API + CLI skeletons,
docs-as-spec.

## Phase 1 — Registry core (done)

**Goal:** authoritative state on disk.
Deliverables: SQLite connection management (WAL, explicit path config), idempotent forward-only
migrations applied at startup, repositories for the six data areas, durable operation records
(create → run → succeed/fail/rollback), audit log writer.
Tests: repository round-trips, migration idempotency, operation-record lifecycle.
Exit gate: all lifecycle state transitions persist and reload.

## Phase 2 — Packages, validation, artifacts

**Goal:** nothing unverified ever reaches the artifact store.
Deliverables: archive extraction with traversal/size/entry safety; canonical manifest parsing;
digest + signature verification (RSA/ECDSA over canonical manifest + package digest, public key
config, key outside writable roots); content-addressed immutable artifact store; staging →
promotion; `contractVersion` compatibility check.
Tests: tampered/corrupt/traversal packages rejected (acceptance #2); valid packages promote; the
active version is untouched by failed installs.
Exit gate: acceptance #2 green.

## Phase 3 — In-process runtime and dispatcher (done)

**Goal:** trusted execution with clean lifecycle edges.
Deliverables: plugin activation (load via `PluginLoadContext`, `GetJobs()` discovery, handler
resolution via `IJobHandlerFactory`, definition validation), activation publication (single-row
`plugin_activation` + version state + discovered job definitions), dispatcher (resolves the active
version, global + per-job concurrency gates, no-overlap default), per-run execution scope, timeout +
cancellation propagation, execution store writes (Pending → Running → terminal), sanitized errors,
retry engine wiring (`RetryPolicyEvaluator`), cooperative unload with unclean-unload surfacing
(`plugin_versions.unclean_unload_reason`). Quartz trigger application remains phase 4.
Tests: execute a compiled test plugin; timeout/cancel paths; parallel vs no-overlap; unload
success and cooperative-unload-failure paths.
Exit gate: single-job runtime install → activate → run → drain works end-to-end.

## Phase 4 — Quartz integration and reconciliation (done)

**Goal:** one scheduling authority, converged state.
Deliverables: Quartz hosted scheduler with ADO.NET SQLite job store; trigger derivation
(job id + schedule revision, string-only job data); reconciler (startup, post-operation, periodic
sweep) consuming the outbox; drift repair toward the registry; misfire mapping to `MisfirePolicy`;
disabled job handling.
Tests: schedule add/modify/disable/re-enable without restart (acceptance #3); crash mid-operation
→ restart → converges (acceptance #7); drift (rogue trigger) removed.
Exit gate: acceptance #3 and #7 green. Per-step gate — each reconciliation step is independently
idempotent and re-runnable: lifecycle resolution is a registry read plus a terminal transition,
outbox application is apply-then-mark, and drift repair is a full registry-versus-Quartz diff, so a
sweep interrupted at any point converges on the next sweep. Quartz hosting is configured from the
`Quartz` package (the `Quartz.Extensions.Hosting` package is an empty 4.x meta-package); Quartz
types stay in `Scheduler.Infrastructure` and the host composition root, and the reconciler reaches
Quartz only through `IScheduleStore`.

## Phase 5 — Observability and operations (done)

**Goal:** the operator can answer what work was done, what was not and why, and what is written
down — from the CLI, without reading source or tailing raw logs.
Deliverables: durable done/not-done SQL aggregates over the registry tables (no in-process meters);
JSON summary endpoint `GET /api/executions/summary` (the centerpiece; no Prometheus/OTel scrape
endpoint); execution history, filterable audit read, per-execution log capture and retrieval, and
health detail endpoints; a finished CLI-first operator surface (`Scheduler.Cli`) as a thin HTTP
client; execution-scoped logger/reporter factory; durable rejection reasons; correlation id on every
execution; startup `Interrupted` classification. See
[09-observability-operations.md](09-observability-operations.md).
Tests: summary aggregates (acceptance #11); log capture and retrieval (acceptance #12); recovery
classification (acceptance #13); health detail (acceptance #14); extended audit coverage (#10).
Exit gate: acceptance #10–#14 green.
Per-step gate — each step ships independently and idempotently:
1. Spec docs updated (gates green).
2. Persistence: `correlation_id`, rejection records, startup `Interrupted` classification, and the
   per-execution log store (migration + repositories + tests) — all forward-only and idempotent.
3. Execution-scoped logger/reporter factory replacing the process-wide null singletons.
4. API endpoints (list / history / summary / show / logs / audit / health) with tests.
5. CLI commands (thin client) with tests.
6. Host structured logging scopes + rolling file capture under `LogsRoot`.
7. Docs finalized; this phase marked done.
The summary aggregates are idempotent reads; `Interrupted` classification runs once per startup and
is idempotent (only `Running` rows transition); log retention only trims files beyond the configured
count.

## Phase 6 — Secrets and release hardening

**Goal:** least-privilege secrets, authenticated release surface, resilient shutdown.
Deliverables: DPAPI/OS-backed secret store + policy registry; restricted per-execution
`ISecretProvider`; rotation semantics (latest-at-dispatch, retain-until-complete); management-API
authentication (loopback default, separate permissions per action) and operation-scoped idempotency;
graceful shutdown with drain timeout and heartbeat-based stuck-execution monitoring; consistent
backup tooling.
Tests: secret authorization negative/positive (acceptance #8); rotation during idle + running
executions; crash/drain/shutdown fault-injection sweeps.
Exit gate: full acceptance checklist (#1–#8, #10) green.

## Phase 7 — E2E Tests

Genearte an in process example job, and show case how the implementation is workign in detail, how the job is run and so on.

## Phase 8 — Worker backend

Same logical execution contract over authenticated local IPC (`Scheduler.Runtime.Worker`):
supervision (start/monitor/replace unhealthy workers), structured messages (existing IPC
contracts), worker identity authorization, resource limits, terminate-and-replace without host
restart. Exit gate: acceptance #9 green.

## Crash-consistency matrix (fault-injection targets)

| Crash point                                      | On-restart behavior                                                 |
| ------------------------------------------------ | ------------------------------------------------------------------- |
| After staging, before validation                 | Staged package unvalidated; ignored or re-validated                 |
| Mid-validation                                   | Re-run validation; no promotion occurred                            |
| After promotion, before activation request       | Version `Staged`; no dispatch impact                                |
| Mid-activation (load, before publication)        | Operation rolled back; previous active version keeps serving        |
| After publication, before Quartz apply           | Outbox replay applies triggers; activation already atomic           |
| Mid-drain                                        | Drain re-enters from operation record; old executions re-classified |
| Mid-execution                                    | Execution → `Interrupted`; explicit recovery classification         |
| After external side effect, before success write | At-least-once: retry possible; handlers tolerate duplicates         |

## Maintainability practices

- Docs-as-spec with ADRs; every behavior change updates its doc in the same commit.
- Vertical slices with the acceptance checklist as the definition of done.
- Test pyramid: fast unit gates on pure logic (contracts, retry, state machine), SQLite-backed
  integration for persistence/lifecycle, targeted fault injection for crash points.
- CPM + committed lock files + PackageGuard allowlist + format/analyzer gates in pre-commit and
  CI keep the codebase boring to change.
- Small, conventional, single-purpose commits.

## Risk register

| Risk                                                 | Mitigation                                                                             |
| ---------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Cooperative unload fails (plugin retains references) | Treat as operational condition: mark unclean, alert, keep serving; never claim removed |
| SQLite contention under load                         | WAL mode, short transactions, single writer path for registry; outbox batched apply    |
| Quartz/registry drift                                | Outbox + periodic reconcile sweep; drift repair tests                                  |
| Tampered packages                                    | Defense-in-depth gates in phase 2 + key hygiene                                        |
| Secrets leakage                                      | Restricted provider, audit metadata only, rotation tests, no values in logs/tests      |
| Plugin destabilizes host (in-process)                | Trusted-signing prerequisite, timeouts, worker backend as escalation                   |
| Contract breaking drift                              | Frozen contract + compat check at validation + ADR process                             |
