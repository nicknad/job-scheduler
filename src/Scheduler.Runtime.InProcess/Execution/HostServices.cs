using Scheduler.Contracts.Execution;
using Scheduler.Contracts.Secrets;

namespace Scheduler.Runtime.InProcess.Execution;

/// <summary>Discards plugin log output until host logging is wired in phase 6.</summary>
public sealed class NullJobExecutionLogger : IJobExecutionLogger
{
    public void Log(JobLogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>Discards progress reports until host observability is wired in phase 6.</summary>
public sealed class NullJobProgressReporter : IJobProgressReporter
{
    public void Report(int percent, string? stage = null)
    {
    }
}

/// <summary>
/// Denies every secret resolution until the per-plugin authorization policy
/// lands (phase 6). Jobs that declare no secret references never touch it.
/// </summary>
public sealed class DeniedSecretProvider : ISecretProvider
{
    public Task<string> ResolveAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        throw new SecretNotAuthorizedException(secretReference);
    }
}
