using Scheduler.Contracts.Execution;

namespace Scheduler.Application.Execution;

/// <summary>
/// Resolves the active plugin version for a fire time and selects the
/// execution backend. Quartz decides when jobs are due; the dispatcher
/// decides where and how they run.
/// </summary>
public interface IDispatcher
{
    /// <summary>Starts an execution for the given job at the current fire time.</summary>
    Task<Guid> DispatchAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Requests cooperative cancellation of a running execution.</summary>
    Task<bool> CancelAsync(Guid executionId, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// A backend that executes jobs. In-process and worker backends implement the
/// same logical execution contract.
/// </summary>
public interface IExecutionBackend
{
    ExecutionMode Mode { get; }
}

/// <summary>How a retiring plugin version's running executions are treated.</summary>
public enum DrainPolicy
{
    /// <summary>Wait for running executions to finish within the drain timeout.</summary>
    Wait,

    /// <summary>Cooperatively cancel running executions.</summary>
    Cancel,
}
