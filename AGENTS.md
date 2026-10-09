# AGENTS.md

## Mission

Build secure, resilient, error-free, readable, and maintainable software.

## Ground rules

- **Docs are the spec.** `docs/` is authoritative; if code and docs disagree, fix one of them in
  the same change. Never implement against an undocumented behavior change — update the doc first.
- **The registry is the truth.** Quartz and all other stores are reconcileable projections.
- **No secrets in code, logs, tests, or docs.** Secret references only, never values.
- **Contract stability.** `src/Scheduler.Contracts` stays dependency-free, exposes no scheduler
  internals, and changes only additively (breaking changes require a decision record in
  `docs/12-decisions.md`).
- **Dependencies are gated.** Only permissively licensed packages from the allowlist
  (`packageguard.config.json`); versions pinned centrally in `Directory.Packages.props`. Run
  `packageguard` after changing dependencies.

## Before you say "done"

Run all of these from the repository root; every one must pass:

```sh
dotnet format JobScheduler.slnx --verify-no-changes
dotnet build JobScheduler.slnx -c Release
dotnet test  JobScheduler.slnx -c Release --no-build
packageguard
```

## Code conventions

- Follow `.editorconfig` exactly (file-scoped namespaces, LF endings, final newline).
- `TreatWarningsAsErrors` is on; treat analyzer findings as errors, not noise. Do not suppress
  rules — restructure the code instead.
- No `catch`-and-continue on lifecycle operations: every failure path must be explicit, logged,
  and leave durable state consistent (operation records, [03-plugin-lifecycle.md](docs/03-plugin-lifecycle.md)).
- Tests: xUnit v3 (MTP). New units of behavior get tests; new features reference the acceptance
  criteria in [10-testing.md](docs/10-testing.md).
- No gratuitous comments; name things so comments are unnecessary. Doc comments (`///`) are for
  contract surface only.

## Commits

- Conventional, small, single-purpose commits; never mix spec changes with unrelated refactors.
- Never commit generated build output, lock-file-adjacent noise, or secrets.
