# Job Contract

The contract assembly (`Scheduler.Contracts`) is the **only** API plugins see. It is
dependency-free, versioned independently of the host, and loaded from the host's default context —
plugins must never bundle their own copy (the loader redirects contract assembly loads to the
host's instance).

## Interfaces

```csharp
public interface IJobPlugin
{
    string Id { get; }
    Version Version { get; }
    IReadOnlyCollection<JobDefinition> GetJobs();
}

public interface IJobHandler
{
    Task<JobResult> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}
```

`JobExecutionContext` is a platform type, **not** Quartz's `IJobExecutionContext`. It provides:

- Job, execution, and configuration-revision identifiers; correlation ID.
- The pinned plugin id/version for this execution.
- Validated parameters (serializable data only).
- A **restricted** secret provider (only granted references resolve).
- A minimal logger and a progress reporter.
- Deadline information; cancellation flows through the `CancellationToken`.

It does **not** provide: scheduler internals, arbitrary host services, global configuration, or a
way to modify scheduling state.

## Job definition (data only)

| Property | Purpose |
| --- | --- |
| `JobId` | Stable identifier within the platform |
| `PluginId` / `PluginVersion` | Implementation identity |
| `Enabled` | Whether scheduling is permitted |
| `Schedule` | Cron / fixed interval / one-shot trigger |
| `Parameters` | Versioned JSON configuration, stored as key/value data |
| `ConcurrencyPolicy` | `DisallowOverlap` (default) or `AllowParallel` (explicit opt-in) |
| `Timeout` | Maximum execution duration |
| `RetryPolicy` | Bounded retry configuration |
| `MisfirePolicy` | `FireOnce` / `Skip` / `RunImmediately` |
| `SecretReferences` | Allowed secret identifiers (references only, never values) |
| `ExecutionMode` | `InProcess` or `Worker` |

Rules:

- All job parameters must be **serializable data**. Never persist plugin object instances or rely
  on serializing arbitrary .NET types into the job store (Quartz's data map is used in string
  form only).
- Definitions returned by `GetJobs()` are *proposals*; the registry is authoritative for what is
  enabled and how it is scheduled.
- Contract evolution is additive and versioned; breaking changes follow the compatibility decision
  process in [12-decisions.md](12-decisions.md).
