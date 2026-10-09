# Observability and Operations

## Structured logs and metrics

| Signal | Coverage |
| --- | --- |
| Lifecycle events | Installs, validations (pass/fail + reasons), activations, deactivations, rollbacks, removals |
| Plugin runtime | Load duration, validation failures, unload success/failure (unclean unloads are alerts) |
| Executions | Active count, duration histograms, retries, timeouts, cancellations, failures |
| Secrets | Access decisions (reference, principal, allowed/denied) — never values |
| Reconciliation | Runs, pending operation counts, repairs applied, errors |

Structured logging with correlation IDs from `JobExecutionContext.CorrelationId`. Health checks:
liveness (`/healthz`), plus operational health (database reachable, reconciler idle, no stuck
`Running` executions without heartbeat).

## Recovery and resilience

- Startup: apply pending migrations, then run the reconciler (finish or roll back incomplete
  operations, converge Quartz to registry), then classify `Interrupted` executions — all **before**
  accepting new fire times from Quartz.
- Executions left in `Running` across a crash become `Interrupted`; explicit recovery
  classification (retry / mark failed / manual) — never auto-retry.
- Preserve active plugin versions when a new version fails validation or activation.
- **Graceful shutdown**: stop accepting new fire times, apply the configured drain timeout to
  running executions, checkpoint reconciliation state, then exit.
- Monitor: stuck executions (heartbeat timeouts), plugin load failures, unsuccessful unloads,
  reconciliation errors.

## Backup

Consistent backup of database + artifacts ([07-persistence.md](07-persistence.md)); verify
artifact hashes against the registry after restore.
