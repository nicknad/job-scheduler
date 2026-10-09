using Scheduler.Application.Execution;
using Scheduler.Application.Observability;
using Scheduler.Contracts.Execution;

namespace Scheduler.Runtime.InProcess.Execution;

/// <summary>
/// Builds the execution scope from a platform invocation and calls the handler
/// directly. It never exposes scheduler internals; the handler sees only
/// <see cref="JobExecutionContext" />, which is a contract type. The execution
/// logger and progress reporter are created per execution from the factory so the
/// singleton backend never carries per-execution state.
/// </summary>
public sealed class InProcessExecutionBackend : IExecutionBackend
{
    private readonly IExecutionLoggerFactory _loggerFactory;
    private readonly Contracts.Secrets.ISecretProvider _secrets;

    public InProcessExecutionBackend(
        IExecutionLoggerFactory loggerFactory,
        Contracts.Secrets.ISecretProvider secrets)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(secrets);

        _loggerFactory = loggerFactory;
        _secrets = secrets;
    }

    public ExecutionMode Mode => ExecutionMode.InProcess;

    public Task<JobResult> ExecuteAsync(ExecutionInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        ExecutionIdentity identity = new(
            invocation.ExecutionId,
            invocation.CorrelationId,
            invocation.JobId,
            invocation.PluginId,
            invocation.PluginVersion);

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
            _loggerFactory.CreateLogger(identity),
            _loggerFactory.CreateProgressReporter(identity),
            _secrets);

        return invocation.Handler.ExecuteAsync(context, cancellationToken);
    }
}
