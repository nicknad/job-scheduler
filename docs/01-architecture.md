# Architecture

## Objective

A single-machine .NET 10 job execution platform that installs, validates, activates, updates,
disables, removes, and rolls back job plugin implementations **without restarting the scheduler
host**. Operational simplicity first; a clean path to separate worker processes and stronger
isolation later.

Locked decisions: [12-decisions.md](12-decisions.md).

## Components

### Control plane

| Component | Responsibility |
| --- | --- |
| Management API / CLI | Authenticated installation, activation, configuration, and removal commands. The CLI calls the same API/application services — never separate installation logic. |
| Plugin manager | Package validation, version management, lifecycle coordination, rollback. Sole owner of lifecycle transitions. |
| Registry | Authoritative desired state: plugin metadata, versions, activation records, job definitions, permissions. |
| Reconciler | Detects and repairs incomplete lifecycle operations after failures or crashes. |

### Execution plane

| Component | Responsibility |
| --- | --- |
| Quartz.NET | Decides *when* jobs are due. Trigger management, recurring schedules, misfire handling, durable ADO.NET (SQLite) job store. |
| Dispatcher | Resolves the active plugin version for a fire time and selects the execution backend. |
| Plugin runtime (in-process) | Loads assemblies into collectible `AssemblyLoadContext`s, tracks executions, manages dependency scopes, cooperative unloading. |
| Worker supervisor (later) | Starts, monitors, and stops external worker processes over authenticated local IPC. |

### Shared services

Secret provider (restricted, reference-based), artifact store (immutable, versioned), execution
store (durable run records), observability (structured logs, metrics, health, audit).

## Architectural rules

1. **The registry is the truth.** Quartz is a derived, reconcileable projection of desired state.
   Never the other way around.
2. The plugin manager must not manipulate Quartz internals directly; it publishes changes and the
   reconciler applies them.
3. Plugin code never controls its own registration, activation, or scheduling.
4. The contract assembly (`Scheduler.Contracts`) exposes only stable abstractions. **No Quartz
   types reach plugins.** It stays dependency-free.
5. Filesystem changes never activate plugins. Only explicit API/CLI operations do.
6. One scheduling system only. The dispatcher picks the execution backend; Quartz stays the
   scheduling authority for both in-process and worker execution.
7. Quartz's job store persists scheduling data, not the platform's execution history. The
   platform maintains its own execution store.
8. Module dependency direction (enforced by project references):

```
Contracts        (no dependencies)
   ↑
Application      (→ Contracts)
   ↑
Infrastructure   (→ Application, Contracts; packages: Quartz, Microsoft.Data.Sqlite)
Runtime.InProcess(→ Application)
Runtime.Worker   (→ Contracts; worker process owns its own runtime)
Host             (→ all; hosts API + Quartz)
Cli              (→ Application; thin client of the management API)
Tests            (→ all)
```

## Non-goals (initial release)

Arbitrary third-party code; multi-machine clustering; general workflow engine; hot replacement of
code inside a running execution; exactly-once guarantees for external side effects; a web dashboard
beyond the management API and basic operational visibility.
