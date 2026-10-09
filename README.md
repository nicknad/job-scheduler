# JobScheduler

A single-machine .NET 10 job execution platform that supports installing, validating, activating,
updating, disabling, removing, and rolling back job plugin implementations **without restarting
the scheduler host**.

- **Scheduler:** Quartz.NET (ADO.NET job store on SQLite)
- **Plugin model:** versioned, signed .NET assemblies behind a stable contract
- **Execution:** in-process (`AssemblyLoadContext`) first, worker-process backend later
- **State:** durable SQLite-backed plugin registry, job registry, execution store, and audit log
- **License:** Apache-2.0

The full specification and implementation plan live in [`docs/`](docs/README.md). A runnable
end-to-end example lives in [`examples/`](examples/), with an operator walkthrough in
[docs/13-example.md](docs/13-example.md).

## Repository layout

```
docs/                     Condensed specifications and the implementation plan
src/
  Scheduler.Contracts/    Stable, dependency-free plugin/job contract (the only API plugins see)
  Scheduler.Application/  Plugin manager, job manager, dispatcher, reconciler (use cases)
  Scheduler.Infrastructure/  SQLite persistence, artifact store, secret provider, observability
  Scheduler.Runtime.InProcess/  AssemblyLoadContext loading, execution scopes, unloading
  Scheduler.Runtime.Worker/    Worker-process execution backend (contract + IPC messages)
  Scheduler.Host/         Long-running host (management API + Quartz hosting)
  Scheduler.Cli/          CLI that calls the same management API
  Scheduler.Tests/        xUnit v3 tests on the Microsoft Testing Platform
  Scheduler.Tests.TestPlugins/  Test-only plugin assemblies loaded by runtime tests
examples/
  Scheduler.Example.Plugin/         Runnable example plugin (declares a secret reference)
  Scheduler.Example.PackageBuilder/ Builds the signed example package via the canonical pipeline
```

## Prerequisites

- .NET 10 SDK (pinned in `global.json`)
- Git
- [PackageGuard](https://github.com/dennisdoomen/packageguard) .NET tool (optional, for dependency audits)

## Build, test, verify

```sh
dotnet restore
dotnet build JobScheduler.slnx -c Release
dotnet test  JobScheduler.slnx -c Release

# Formatting (enforced by the pre-commit hook)
dotnet format JobScheduler.slnx --verify-no-changes

# Dependency license/package audit (config: packageguard.config.json)
dotnet tool install --global PackageGuard
packageguard
```

## Git hooks

This repository keeps its hooks in `.githooks/` (format verification + build-as-gate). Enable them once per clone:

```sh
git config core.hooksPath .githooks
```

## Conventions

- Central package management: all package versions are pinned in `Directory.Packages.props`;
  lock files (`packages.lock.json`) are committed for reproducible restores.
- `TreatWarningsAsErrors` is on for all projects; analyzers run at `latest-recommended`.
- Only permissively licensed packages (Apache-2.0/MIT/BSD-class) are allowed; this is enforced by
  `packageguard.config.json`.
- Text files use LF line endings (`.gitattributes`); `.editorconfig` governs style.
