using Scheduler.Application.Observability;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Cli;
using Scheduler.Contracts.Execution;
using Scheduler.Contracts.Jobs;

namespace Scheduler.Tests.Unit.Cli;

public sealed class SchedulerCliTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StatusPrintsTheSummaryAndExitsDone()
    {
        FakeClient client = new();
        (int exitCode, string output, _) = await RunAsync(client, "status");

        Assert.Equal(SchedulerCli.ExitDone, exitCode);
        Assert.Contains("succeeded    7", output, StringComparison.Ordinal);
        Assert.Contains("rejections:    1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedJobRunExitsNotDoneAndPrintsTheReason()
    {
        FakeClient client = new()
        {
            RunJobException = new SchedulerApiException(409, "The job was not admitted.", "Disabled"),
        };

        (int exitCode, _, string error) = await RunAsync(client, "job", "run", "job-1");

        Assert.Equal(SchedulerCli.ExitNotDone, exitCode);
        Assert.Contains("(reason: Disabled)", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulJobRunExitsDoneAndPrintsTheExecutionId()
    {
        FakeClient client = new();
        (int exitCode, string output, _) = await RunAsync(client, "job", "run", "job-1");

        Assert.Equal(SchedulerCli.ExitDone, exitCode);
        Assert.Contains("started execution", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithAnInvalidWindowExitsError()
    {
        FakeClient client = new();
        (int exitCode, _, string error) = await RunAsync(client, "status", "--window", "bogus");

        Assert.Equal(SchedulerCli.ExitError, exitCode);
        Assert.Contains("not a valid window", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithAValidWindowExitsDone()
    {
        FakeClient client = new();
        (int exitCode, _, _) = await RunAsync(client, "status", "--window", "90s");

        Assert.Equal(SchedulerCli.ExitDone, exitCode);
    }

    [Fact]
    public async Task UnhealthyHealthExitsNotDone()
    {
        FakeClient client = new()
        {
            Health = new HealthReport(
                Healthy: false,
                DateTimeOffset.UnixEpoch,
                new HealthComponent(true, "reachable"),
                new HealthComponent(false, "last failure"),
                [Guid.NewGuid()]),
        };

        (int exitCode, string output, _) = await RunAsync(client, "health");

        Assert.Equal(SchedulerCli.ExitNotDone, exitCode);
        Assert.Contains("stuck        1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowOfAFailedExecutionExitsNotDone()
    {
        FakeClient client = new()
        {
            Execution = new ExecutionRecord
            {
                ExecutionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                JobId = "job-1",
                PluginId = "plugin-1",
                PluginVersion = new Version(1, 0, 0),
                ConfigurationRevision = 1,
                Attempt = 1,
                Status = JobExecutionStatus.Failed,
                CorrelationId = "corr",
                ScheduledAt = DateTimeOffset.UnixEpoch,
                ResultSummary = "boom",
            },
        };

        (int exitCode, string output, _) = await RunAsync(client, "execution", "show", "11111111-1111-1111-1111-111111111111");

        Assert.Equal(SchedulerCli.ExitNotDone, exitCode);
        Assert.Contains("boom", output, StringComparison.Ordinal);
        Assert.Contains("corr", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommandExitsError()
    {
        FakeClient client = new();
        (int exitCode, _, string error) = await RunAsync(client, "bogus");

        Assert.Equal(SchedulerCli.ExitError, exitCode);
        Assert.Contains("unknown", error, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ISchedulerApiClient client,
        params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await SchedulerCli.RunAsync(args, client, output, error, CancellationToken);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class FakeClient : ISchedulerApiClient
    {
        public ExecutionSummary Summary { get; set; } = new(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            new ExecutionOutcomeCounts(10, 0, 7, 2, 1, 0, 0, 3),
            new RejectionCounts(1, new Dictionary<string, int> { ["disabled"] = 1 }),
            new ScheduleEventCounts(5, 0, 0),
            new LifecycleCounts(new Dictionary<string, int>(), new Dictionary<string, int>()),
            new ReconciliationCounts(2, 0, 3, DateTimeOffset.UnixEpoch));

        public SchedulerApiException? RunJobException { get; set; }

        public HealthReport Health { get; set; } = new(
            true,
            DateTimeOffset.UnixEpoch,
            new HealthComponent(true, "reachable"),
            new HealthComponent(true, "ok"),
            []);

        public ExecutionRecord? Execution { get; set; }

        public Task<ExecutionSummary> GetSummaryAsync(TimeSpan? window, CancellationToken cancellationToken = default) =>
            Task.FromResult(Summary);

        public Task<IReadOnlyList<JobDefinition>> ListJobsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobDefinition>>([]);

        public Task<Guid> RunJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            RunJobException is null
                ? Task.FromResult(Guid.NewGuid())
                : Task.FromException<Guid>(RunJobException);

        public Task<IReadOnlyList<ExecutionRecord>> ListExecutionsAsync(ExecutionFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExecutionRecord>>([]);

        public Task<ExecutionRecord?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Execution);

        public Task<IReadOnlyList<ExecutionLogEntry>> GetExecutionLogsAsync(Guid executionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExecutionLogEntry>>([]);

        public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(AuditFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>([]);

        public Task<HealthReport> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Health);

        public Task<IReadOnlyList<PluginDescriptor>> ListPluginsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginDescriptor>>([]);

        public Task<PluginOperation> InstallPluginAsync(string packagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginOperation(Guid.NewGuid(), "plugin-1", new Version(1, 0, 0), PluginOperationStatus.Succeeded));

        public Task<ValidationReport> ValidatePluginAsync(string pluginId, Version version, CancellationToken cancellationToken = default) =>
            Task.FromResult(ValidationReport.Valid(pluginId, version, []));

        public Task<PluginOperation> ActivatePluginAsync(string pluginId, Version version, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginOperation(Guid.NewGuid(), pluginId, version, PluginOperationStatus.Succeeded));

        public Task<PluginOperation> DeactivatePluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginOperation(Guid.NewGuid(), pluginId, null, PluginOperationStatus.Succeeded));

        public Task<PluginOperation> RollbackPluginAsync(string pluginId, Version version, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginOperation(Guid.NewGuid(), pluginId, version, PluginOperationStatus.Succeeded));

        public Task<PluginOperation> RemovePluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginOperation(Guid.NewGuid(), pluginId, null, PluginOperationStatus.Succeeded));
    }
}
