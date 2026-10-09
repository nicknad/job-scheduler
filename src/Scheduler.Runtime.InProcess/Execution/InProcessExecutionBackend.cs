using Scheduler.Application.Execution;
using Scheduler.Contracts.Execution;

namespace Scheduler.Runtime.InProcess.Execution;

/// <summary>
/// Builds the execution scope from a platform invocation and calls the handler
/// directly. It never exposes scheduler internals; the handler sees only
/// <see cref="JobExecutionContext" />, which is a contract type.
/// </summary>
public sealed class InProcessExecutionBackend : IExecutionBackend
{
    private readonly IJobExecutionLogger _logger;
    private readonly IJobProgressReporter _progress;
    private readonly Contracts.Secrets.ISecretProvider _secrets;

    public InProcessExecutionBackend(
        IJobExecutionLogger logger,
        IJobProgressReporter progress,
        Contracts.Secrets.ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(secrets);

        _logger = logger;
        _progress = progress;
        _secrets = secrets;
    }

    public ExecutionMode Mode => ExecutionMode.InProcess;

    public Task<JobResult> ExecuteAsync(ExecutionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        JobExecutionContext context = new(
            invocation.ExecutionId,
            invocation.JobId,
            invocation.PluginId,
            invocation.PluginVersion,
            invocation.ConfigurationRevision,
            invocation.ScheduledAt,
            invocation.Deadline,
            invocation.CorrelationId,
            invocation.Parameters,
            _logger,
            _progress,
            _secrets);

        return invocation.Handler.ExecuteAsync(context, cancellationToken);
    }
}
