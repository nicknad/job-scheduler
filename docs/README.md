# Documentation Index

Condensed, authoritative specifications and the implementation plan for the
runtime-extensible .NET job platform. If code and docs disagree, the docs win;
fix the code or fix the docs in the same change.

| Document | Contents |
| --- | --- |
| [01-architecture.md](01-architecture.md) | Objective, components, planes, architectural rules, non-goals |
| [02-plugin-package.md](02-plugin-package.md) | Package layout, manifest, signatures, validation gates |
| [03-plugin-lifecycle.md](03-plugin-lifecycle.md) | State machine; install/activate/update/rollback/remove semantics |
| [04-job-contract.md](04-job-contract.md) | The stable contract: `IJobPlugin`, `IJobHandler`, execution context |
| [05-scheduling-execution.md](05-scheduling-execution.md) | Quartz integration, reconciliation, execution tracking, retries, concurrency |
| [06-secrets.md](06-secrets.md) | Secret references, authorization, rotation, process boundaries |
| [07-persistence.md](07-persistence.md) | Data areas, SQLite, outbox, storage roots, backups |
| [08-security.md](08-security.md) | API authentication, signing keys, least privilege, audit |
| [09-observability-operations.md](09-observability-operations.md) | Structured logs, metrics, health checks, recovery, shutdown |
| [10-testing.md](10-testing.md) | Test strategy mapped to the acceptance criteria |
| [11-implementation-plan.md](11-implementation-plan.md) | The phased, resilience- and maintainability-first implementation plan |
| [12-decisions.md](12-decisions.md) | Locked decisions and lightweight ADRs |
| [graphs/plugin-workflows.md](graphs/plugin-workflows.md) | Diagrams: install/store, load/activate, execute, modules |
