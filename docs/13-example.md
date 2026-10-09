# Example: an end-to-end walkthrough

A first-class, runnable example that exercises the whole platform the way an operator would: a real
plugin declares a secret **reference**, is packaged through the canonical manifest + signature
pipeline, installed, validated, activated, granted a secret, run, observed, drained, and backed up —
all over the real management API. The same flow is asserted by the automated end-to-end test
(`src/Scheduler.Tests/Integration/EndToEnd/ExampleEndToEndTests.cs`).

Nothing here is test-only: the example reaches the platform only through install → validate →
activate and the reconciler; it never bypasses the package pipeline.

## What the example is

| Piece | Value |
| --- | --- |
| Plugin project | `examples/Scheduler.Example.Plugin` (references only `Scheduler.Contracts`) |
| Plugin id / version | `example-reporting` / `1.0.0` |
| Job id | `example-report` (cron `0 6 * * *`, `ExecutionMode.InProcess`) |
| Secret reference | `example/report-api-key` (a **reference only** — never a value) |
| Deterministic work | resolves the granted reference, derives `SHA-256`-based `ReportDigest` of the value, reports progress, writes a small report file, and returns a summary containing only the digest and length |

The handler **never logs, persists, or returns the secret value**; only its 12-hex-character digest
and length appear in logs, the result summary, and the report. The plugin holds no references past
its collectible load context, so deactivation unloads cleanly.

## 1. Build and produce the signed package

The package is produced by the same canonical digest/signature pipeline the host validates
(`CanonicalPackageDigest` + `PluginManifestJson`), via a small repeatable tool. The tool generates an
RSA key pair on first run (private key + public key), signs the canonical manifest over the package
digest, and writes the ZIP.

```sh
dotnet build JobScheduler.slnx -c Release
dotnet run --project examples/Scheduler.Example.PackageBuilder -c Release --no-build -- \
  --output ./example-package/Scheduler.Example.Plugin.1.0.0.zip \
  --keys ./keys
```

Expected output (paths vary by machine):

```
package  /…/example-package/Scheduler.Example.Plugin.1.0.0.zip
public   /…/keys/package-signing.pub.pem
plugin   example-reporting 1.0.0 (Scheduler.Example.Plugin.ReportingPlugin)
```

`keys/package-signing.key` (private) and `keys/package-signing.pub.pem` (public) are written outside
the host's writable roots. The private key is never committed; `keys/` and `data/` are gitignored.

## 2. Start the host

The management API is authenticated and bound to loopback. The admin bearer token is provisioned
once from `SCHEDULER_BOOTSTRAP_MANAGEMENT_TOKEN` (never committed) and thereafter resolved from the
encrypted secret store.

```powershell
$env:SCHEDULER_BOOTSTRAP_MANAGEMENT_TOKEN = "local-admin-token-change-me"
$env:JobScheduler__PackagePublicKeyPath   = "$PWD/keys/package-signing.pub.pem"
$env:JobScheduler__SecretStore__KeyPath   = "$PWD/keys/secret-store.key"
dotnet run --project src/Scheduler.Host -c Release --no-build
```

The host logs `Now listening on: http://localhost:5080`. Runtime data (`data/`, `logs/`) defaults
under the host content root and is gitignored. Leave it running and use a second terminal.

## 3. Drive the platform with the CLI

`Scheduler.Cli` is a thin HTTP client; set the base URL and the admin token once:

```powershell
$env:SCHEDULER_API_URL   = "http://localhost:5080"
$env:SCHEDULER_API_TOKEN = "local-admin-token-change-me"
$cli = "dotnet"; $cliArgs = @("run","--project","src/Scheduler.Cli","-c","Release","--no-build","--")
```

Install → validate → activate (no host restart anywhere):

```powershell
& $cli @cliArgs plugin install ./example-package/Scheduler.Example.Plugin.1.0.0.zip
# example-reporting: Succeeded
& $cli @cliArgs plugin validate example-reporting 1.0.0
# example-reporting 1.0.0: valid
& $cli @cliArgs plugin activate example-reporting 1.0.0
# example-reporting: Succeeded
& $cli @cliArgs job list
# example-report        example-reporting    enabled  cron 0 6 * * *
```

Provision the secret value and grant the **reference** to the job (the value is read from stdin and
never appears in output):

```powershell
"example-api-key-value" | & $cli @cliArgs secret set example/report-api-key
# secret example/report-api-key: set
& $cli @cliArgs secret grant example-reporting example/report-api-key --job example-report
# grant example-reporting/example-report example/report-api-key: granted
```

Run it and observe the work:

```powershell
& $cli @cliArgs job run example-report
# started execution <execution-id>
& $cli @cliArgs execution list --job example-report --status Succeeded
# <execution-id> example-report Succeeded <time> <duration>
& $cli @cliArgs execution show <execution-id>
# status       Succeeded
# summary      report digest=<12-hex> length=20 path=…
& $cli @cliArgs audit tail --action secret.access.allowed
# <time> execution secret.access.allowed example/report-api-key — plugin=example-reporting;job=example-report;execution=<id>
```

The per-execution log (`GET /api/executions/{id}/logs`) and the report file contain the digest and
length only — **never the value**. The done/not-done picture comes from the summary:

```powershell
& $cli @cliArgs status
# executions:
#   succeeded    1
```

## 4. Drain and back up

```powershell
& $cli @cliArgs plugin deactivate example-reporting
# example-reporting: Succeeded   (in-flight executions complete under the Wait drain policy)
& $cli @cliArgs backup --to data/backups/walkthrough
# backup …/data/backups/walkthrough: 2 artifact(s), verified=True
```

Deactivation drains running executions first; the plugin unloads cleanly (a cooperative-unload
failure would surface as `Failed`, never a silent success). The backup takes a `VACUUM INTO`
checkpoint of the registry plus an artifact snapshot and verifies every artifact hash.

## Rejecting tampering

Re-uploading a package whose archive content does not match its signed manifest digest is rejected
(`422`), and the active version is untouched:

```powershell
# mutate a byte in the ZIP payload, then:
& $cli @cliArgs plugin install ./example-package/tampered.zip
# error: Package digest 'sha256:…' does not match manifest artifactHash 'sha256:…'
& $cli @cliArgs plugin list
# example-reporting Active 1.0.0     (unchanged)
```

## Automated coverage

`ExampleEndToEndTests` starts the real `Scheduler.Host` as a child process (temp data/keys/db,
loopback port, bootstrap admin credential) and asserts: install + validate + activate without
restart; the secret is actually used and never leaked; audit and the summary reflect the work; the
per-execution logs are retrievable; a tampered package is rejected with the active version untouched;
401/403 for under-permissioned calls; `X-Operation-Id` replay returns the same result; and backup
verification preserves lifecycle/activation state. `ExamplePluginTests` runs the same signed plugin
through the in-process runtime for the drain/`Wait` and `Cancel` paths. See
[10-testing.md](10-testing.md).
