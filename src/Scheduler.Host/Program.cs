using System.IO.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Scheduler.Application.Execution;
using Scheduler.Application.JobManagement;
using Scheduler.Application.Observability;
using Scheduler.Application.Packaging;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Application.Reconciliation;
using Scheduler.Contracts;
using Scheduler.Contracts.Execution;
using Scheduler.Contracts.Jobs;
using Scheduler.Contracts.Secrets;
using Scheduler.Infrastructure.Observability;
using Scheduler.Infrastructure.Packaging;
using Scheduler.Infrastructure.Persistence;
using Scheduler.Infrastructure.Scheduling;
using Scheduler.Runtime.InProcess.AssemblyLoading;
using Scheduler.Runtime.InProcess.Execution;
using Scheduler.Host;

const long MultipartBodyLengthLimit = 512L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

IFileSystem fileSystem = new FileSystem();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddProvider(new SimpleFileLoggerProvider(
    ResolveLogsRoot(packagingLogsRoot: builder.Configuration["JobScheduler:LogsRoot"] ?? "logs", builder.Environment.ContentRootPath),
    fileSystem));

builder.Services.AddHealthChecks();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MultipartBodyLengthLimit);

PersistenceOptions persistenceOptions = new()
{
    DatabasePath = builder.Configuration["JobScheduler:DatabasePath"] ?? "data/jobscheduler.db",
    BaseDirectory = builder.Environment.ContentRootPath,
};
PackagingOptions packagingOptions = new()
{
    ArtifactsRoot = builder.Configuration["JobScheduler:ArtifactsRoot"] ?? "data/artifacts",
    StagingRoot = builder.Configuration["JobScheduler:StagingRoot"] ?? "data/staging",
    PublicKeyPath = builder.Configuration["JobScheduler:PackagePublicKeyPath"] ?? "keys/package-signing.pub.pem",
    DataRoot = builder.Configuration["JobScheduler:DataRoot"] ?? "data",
    LogsRoot = builder.Configuration["JobScheduler:LogsRoot"] ?? "logs",
    BaseDirectory = builder.Environment.ContentRootPath,
};
ExecutionOptions executionOptions = new()
{
    GlobalConcurrencyLimit = builder.Configuration.GetValue("JobScheduler:GlobalConcurrencyLimit", 8),
};
ScheduleOptions scheduleOptions = new()
{
    SchedulerName = builder.Configuration["JobScheduler:SchedulerName"] ?? "scheduler",
    UsePersistentStore = builder.Configuration.GetValue("JobScheduler:UsePersistentStore", true),
};
ObservabilityOptions observabilityOptions = new()
{
    RetainedLogs = builder.Configuration.GetValue("JobScheduler:RetainedLogs", 500),
};

SqliteConnectionFactory connectionFactory = new(persistenceOptions, fileSystem);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(fileSystem);
builder.Services.AddSingleton(persistenceOptions);
builder.Services.AddSingleton(packagingOptions);
builder.Services.AddSingleton(packagingOptions.ToLimits());
builder.Services.AddSingleton(SchedulerContract.CurrentVersion);
builder.Services.AddSingleton(executionOptions);
builder.Services.AddSingleton<ISqliteConnectionFactory>(connectionFactory);
builder.Services.AddSingleton<IDatabaseInitializer>(services =>
    new SqliteDatabaseInitializer(
        services.GetRequiredService<ISqliteConnectionFactory>(),
        SchemaMigrations.All));
builder.Services.AddSingleton<IRegistryUnitOfWorkFactory, SqliteRegistryUnitOfWorkFactory>();
builder.Services.AddSingleton<IAuditWriter, AuditWriter>();
builder.Services.AddSingleton<IPackageArchiveReader, ZipPackageArchiveReader>();
builder.Services.AddSingleton<IPackageSignatureVerifier, PackageSignatureVerifier>();
builder.Services.AddSingleton<IArtifactStore, FileSystemArtifactStore>();
builder.Services.AddSingleton<PackageValidator>();

builder.Services.AddSingleton<IPluginRuntime, InProcessPluginRuntime>();
builder.Services.AddSingleton<IRunningExecutionRegistry, RunningExecutionRegistry>();
builder.Services.AddSingleton<ConcurrencyGate>();
builder.Services.AddSingleton<RetryPolicyEvaluator>();
builder.Services.AddSingleton<ISecretProvider, DeniedSecretProvider>();
builder.Services.AddSingleton<IExecutionBackend, InProcessExecutionBackend>();
builder.Services.AddSingleton<ExecutionRunner>();
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddSingleton<IJobManager, JobManager>();
builder.Services.AddSingleton<IPluginManager, PluginManager>();

builder.Services.AddSchedulerScheduling(connectionFactory.DatabasePath, scheduleOptions);
builder.Services.AddSchedulerObservability(observabilityOptions);

var app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    IDatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync();

    SigningKeyPathGuard.EnsureOutsideWritableRoots(packagingOptions, scope.ServiceProvider.GetRequiredService<IFileSystem>());

    IArtifactStore artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
    await artifactStore.ClearStagingAsync();

    IRegistryUnitOfWorkFactory unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IRegistryUnitOfWorkFactory>();
    DateTimeOffset startupNow = TimeProvider.System.GetUtcNow();
    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync();
    int interrupted = await unitOfWork.Executions.MarkRunningAsInterruptedAsync(startupNow);
    if (interrupted > 0)
    {
        await unitOfWork.Audit.WriteAsync(new AuditEntry
        {
            Timestamp = startupNow,
            Actor = "host",
            Action = "execution.recovered",
            Target = "executions",
            Details = $"interrupted={interrupted}",
        });
    }

    await unitOfWork.CommitAsync();

    IExecutionLogStore logStore = scope.ServiceProvider.GetRequiredService<IExecutionLogStore>();
    await logStore.TrimAsync(observabilityOptions.RetainedLogs);
}

app.MapHealthChecks("/healthz");

app.MapGet("/api/plugins", async (IPluginManager manager, CancellationToken cancellationToken) =>
    Results.Ok(await manager.ListAsync(cancellationToken)));

app.MapPost("/api/plugins", async (HttpRequest request, IPluginManager manager, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "A multipart/form-data package upload is required." });
    }

    IFormCollection form = await request.ReadFormAsync(cancellationToken);
    IFormFile? file = form.Files.Count > 0 ? form.Files[0] : null;
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "A non-empty package file is required." });
    }

    await using Stream stream = file.OpenReadStream();
    PluginOperation operation = await manager.InstallAsync(stream, cancellationToken);
    return operation.Status == PluginOperationStatus.Succeeded
        ? Results.Ok(operation)
        : Results.Json(operation, statusCode: StatusCodes.Status422UnprocessableEntity);
});

app.MapPost("/api/plugins/{id}/{version}/validate", async (
    string id,
    string version,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
{
    if (!Version.TryParse(version, out Version? parsed))
    {
        return Results.BadRequest(new { error = $"'{version}' is not a valid version." });
    }

    try
    {
        ValidationReport report = await manager.ValidateAsync(id, parsed, cancellationToken);
        return Results.Ok(report);
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
});

app.MapGet("/api/plugins/{id}/versions", async (
    string id,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
    Results.Ok(await manager.ListVersionsAsync(id, cancellationToken)));

app.MapPost("/api/plugins/{id}/{version}/activate", async (
    string id,
    string version,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
{
    if (!Version.TryParse(version, out Version? parsed))
    {
        return Results.BadRequest(new { error = $"'{version}' is not a valid version." });
    }

    PluginOperation operation = await manager.ActivateAsync(id, parsed, cancellationToken);
    return LifecycleResult(operation);
}).AddEndpointFilter<ReconcileAfterLifecycleFilter>();

app.MapPost("/api/plugins/{id}/deactivate", async (
    string id,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
    LifecycleResult(await manager.DeactivateAsync(id, cancellationToken)))
    .AddEndpointFilter<ReconcileAfterLifecycleFilter>();

app.MapPost("/api/plugins/{id}/{version}/rollback", async (
    string id,
    string version,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
{
    if (!Version.TryParse(version, out Version? parsed))
    {
        return Results.BadRequest(new { error = $"'{version}' is not a valid version." });
    }

    return LifecycleResult(await manager.RollbackAsync(id, parsed, cancellationToken));
}).AddEndpointFilter<ReconcileAfterLifecycleFilter>();

app.MapPost("/api/plugins/{id}/remove", async (
    string id,
    IPluginManager manager,
    CancellationToken cancellationToken) =>
    LifecycleResult(await manager.RemoveAsync(id, cancellationToken)))
    .AddEndpointFilter<ReconcileAfterLifecycleFilter>();

app.MapGet("/api/jobs", async (IJobManager jobs, CancellationToken cancellationToken) =>
    Results.Ok(await jobs.ListAsync(cancellationToken)));

app.MapGet("/api/jobs/{id}", async (string id, IJobManager jobs, CancellationToken cancellationToken) =>
{
    JobDefinition? definition = await jobs.GetAsync(id, cancellationToken);
    return definition is null ? Results.NotFound(new { id }) : Results.Ok(definition);
});

app.MapPut("/api/jobs/{id}", async (
    string id,
    JobDefinition definition,
    IJobManager jobs,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(id, definition.JobId, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { error = "The route id and definition jobId must match." });
    }

    try
    {
        return Results.Ok(await jobs.UpdateAsync(definition, cancellationToken));
    }
    catch (JobValidationException exception)
    {
        return Results.BadRequest(new { error = exception.Message, exception.Errors });
    }
}).AddEndpointFilter<ReconcileAfterLifecycleFilter>();

app.MapPost("/api/jobs/{id}/run", async (
    string id,
    IJobManager jobs,
    CancellationToken cancellationToken) =>
{
    try
    {
        Guid executionId = await jobs.RunNowAsync(id, cancellationToken);
        return Results.Ok(new { executionId });
    }
    catch (DispatchRejectedException exception)
    {
        return Results.Json(
            new { error = exception.Message, reason = exception.Reason.ToString() },
            statusCode: StatusCodes.Status409Conflict);
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message, reason = nameof(ExecutionRejectionReason.NotFound) });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict);
    }
});

app.MapGet("/api/executions", async (
    string? jobId,
    string? status,
    DateTimeOffset? since,
    DateTimeOffset? until,
    int? limit,
    IRegistryUnitOfWorkFactory unitOfWorkFactory,
    ObservabilityOptions observability,
    CancellationToken cancellationToken) =>
{
    JobExecutionStatus? parsedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse(status, ignoreCase: true, out JobExecutionStatus value) || !Enum.IsDefined(value))
        {
            return Results.BadRequest(new { error = $"'{status}' is not a valid execution status." });
        }

        parsedStatus = value;
    }

    ExecutionFilter filter = new()
    {
        JobId = jobId,
        Status = parsedStatus,
        Since = since,
        Until = until,
        Limit = Math.Clamp(limit ?? observability.ExecutionListLimit, 1, observability.ExecutionListLimit),
    };

    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
    return Results.Ok(await unitOfWork.Executions.ListAsync(filter, cancellationToken));
});

app.MapGet("/api/jobs/{id}/executions", async (
    string id,
    IRegistryUnitOfWorkFactory unitOfWorkFactory,
    CancellationToken cancellationToken) =>
{
    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
    return Results.Ok(await unitOfWork.Executions.ListByJobAsync(id, cancellationToken));
});

app.MapGet("/api/executions/summary", async (
    string? window,
    IExecutionSummaryService summary,
    CancellationToken cancellationToken) =>
{
    TimeSpan? parsedWindow = null;
    if (!string.IsNullOrWhiteSpace(window))
    {
        if (!DurationParser.TryParse(window, out TimeSpan value))
        {
            return Results.BadRequest(new { error = $"'{window}' is not a valid window (e.g. 90s, 30m, 24h, 7d)." });
        }

        parsedWindow = value;
    }

    return Results.Ok(await summary.GetSummaryAsync(parsedWindow, cancellationToken));
});

app.MapGet("/api/executions/{id:guid}", async (
    Guid id,
    IRegistryUnitOfWorkFactory unitOfWorkFactory,
    CancellationToken cancellationToken) =>
{
    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
    ExecutionRecord? execution = await unitOfWork.Executions.GetAsync(id, cancellationToken);
    return execution is null ? Results.NotFound(new { id }) : Results.Ok(execution);
});

app.MapGet("/api/executions/{id:guid}/logs", async (
    Guid id,
    IExecutionLogStore logs,
    IRegistryUnitOfWorkFactory unitOfWorkFactory,
    CancellationToken cancellationToken) =>
{
    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
    ExecutionRecord? execution = await unitOfWork.Executions.GetAsync(id, cancellationToken);
    if (execution is null)
    {
        return Results.NotFound(new { id });
    }

    return Results.Ok(await logs.ReadAsync(id, cancellationToken));
});

app.MapPost("/api/executions/{id:guid}/cancel", async (
    Guid id,
    IDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    bool cancelled = await dispatcher.CancelAsync(id, "Requested via API", cancellationToken);
    return cancelled ? Results.Ok(new { executionId = id, status = "cancelling" }) : Results.NotFound(new { id });
});

app.MapGet("/api/audit", async (
    string? actor,
    string? action,
    string? target,
    DateTimeOffset? since,
    int? limit,
    IRegistryUnitOfWorkFactory unitOfWorkFactory,
    ObservabilityOptions observability,
    CancellationToken cancellationToken) =>
{
    AuditFilter filter = new()
    {
        Actor = actor,
        Action = action,
        Target = target,
        Since = since,
        Limit = Math.Clamp(limit ?? 100, 1, observability.ExecutionListLimit),
    };

    await using IRegistryUnitOfWork unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
    return Results.Ok(await unitOfWork.Audit.ListAsync(filter, cancellationToken));
});

app.MapGet("/api/health", async (IHealthReportService health, CancellationToken cancellationToken) =>
{
    HealthReport report = await health.GetHealthAsync(cancellationToken);
    return report.Healthy
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static IResult LifecycleResult(PluginOperation operation) =>
    operation.Status == PluginOperationStatus.Succeeded
        ? Results.Ok(operation)
        : Results.Json(operation, statusCode: StatusCodes.Status422UnprocessableEntity);

static string ResolveLogsRoot(string packagingLogsRoot, string contentRoot) =>
    Path.IsPathRooted(packagingLogsRoot)
        ? packagingLogsRoot
        : Path.GetFullPath(Path.Combine(contentRoot, packagingLogsRoot));

public partial class Program;
