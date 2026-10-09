using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Contracts.Plugins;

namespace Scheduler.Application.Execution;

/// <summary>
/// Resolves the active plugin version for a job, admits the execution under the
/// concurrency policy, and runs it through the configured backend. Existing
/// executions keep their pinned version and revision; only new dispatches follow
/// a version change.
/// </summary>
public sealed class Dispatcher : IDispatcher
{
    private readonly IRegistryUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IPluginRuntime _pluginRuntime;
    private readonly IExecutionBackend _backend;
    private readonly ExecutionRunner _runner;
    private readonly ConcurrencyGate _gate;
    private readonly IRunningExecutionRegistry _running;
    private readonly TimeProvider _timeProvider;

    public Dispatcher(
        IRegistryUnitOfWorkFactory unitOfWorkFactory,
        IPluginRuntime pluginRuntime,
        IExecutionBackend backend,
        ExecutionRunner runner,
        ConcurrencyGate gate,
        IRunningExecutionRegistry running,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkFactory);
        ArgumentNullException.ThrowIfNull(pluginRuntime);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(running);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _unitOfWorkFactory = unitOfWorkFactory;
        _pluginRuntime = pluginRuntime;
        _backend = backend;
        _runner = runner;
        _gate = gate;
        _running = running;
        _timeProvider = timeProvider;
    }

    public async Task<Guid> DispatchAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        JobRecord job = await GetJobAsync(jobId, cancellationToken)
            ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");

        if (!job.Definition.Enabled)
        {
            throw new InvalidOperationException($"Job '{jobId}' is disabled.");
        }

        if (job.Definition.ExecutionMode != _backend.Mode)
        {
            throw new NotSupportedException(
                $"Execution mode '{job.Definition.ExecutionMode}' is not available on this host.");
        }

        Version activeVersion = await ResolveActiveVersionAsync(job.Definition.PluginId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Plugin '{job.Definition.PluginId}' has no active version; activate it before running jobs.");

        IJobHandler handler = _pluginRuntime.ResolveHandler(jobId, job.Definition.PluginId, activeVersion)
            ?? throw new InvalidOperationException(
                $"No handler for job '{jobId}' in active plugin '{job.Definition.PluginId}' '{activeVersion}'.");

        Guid executionId = Guid.NewGuid();
        using IRunningExecution running = _running.Register(
            executionId,
            job.Definition.PluginId,
            activeVersion,
            CancellationToken.None);
        using IDisposable slot = await _gate.AcquireAsync(
            jobId,
            job.Definition.ConcurrencyPolicy,
            cancellationToken);

        ExecutionRequest request = new(
            jobId,
            job.Definition.PluginId,
            activeVersion,
            job.ConfigurationRevision,
            executionId,
            _timeProvider.GetUtcNow(),
            job.Definition.Parameters,
            job.Definition.Timeout,
            job.Definition.RetryPolicy,
            handler);

        await _runner.RunAsync(request, running);
        return executionId;
    }

    public Task<bool> CancelAsync(Guid executionId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Task.FromResult(_running.TryCancel(executionId, reason));
    }

    private async Task<JobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        return await unitOfWork.Jobs.GetAsync(jobId, cancellationToken);
    }

    private async Task<Version?> ResolveActiveVersionAsync(string pluginId, CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        PluginActivationRecord? activation = await unitOfWork.Plugins.GetActivationAsync(pluginId, cancellationToken);
        return activation?.Version;
    }
}
