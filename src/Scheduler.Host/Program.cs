using System.IO.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Scheduler.Application.Execution;
using Scheduler.Application.JobManagement;
using Scheduler.Application.Packaging;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Contracts;
using Scheduler.Contracts.Execution;
using Scheduler.Contracts.Jobs;
using Scheduler.Contracts.Secrets;
using Scheduler.Infrastructure.Packaging;
using Scheduler.Infrastructure.Persistence;
using Scheduler.Infrastructure.Scheduling;
using Scheduler.Runtime.InProcess.AssemblyLoading;
using Scheduler.Runtime.InProcess.Execution;
using Scheduler.Host;

const long MultipartBodyLengthLimit = 512L * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);

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

IFileSystem fileSystem = new FileSystem();
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
builder.Services.AddSingleton<IJobExecutionLogger, NullJobExecutionLogger>();
builder.Services.AddSingleton<IJobProgressReporter, NullJobProgressReporter>();
builder.Services.AddSingleton<ISecretProvider, DeniedSecretProvider>();
builder.Services.AddSingleton<IExecutionBackend, InProcessExecutionBackend>();
builder.Services.AddSingleton<ExecutionRunner>();
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddSingleton<IJobManager, JobManager>();
builder.Services.AddSingleton<IPluginManager, PluginManager>();

builder.Services.AddSchedulerScheduling(connectionFactory.DatabasePath, scheduleOptions);

var app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    IDatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync();

    SigningKeyPathGuard.EnsureOutsideWritableRoots(packagingOptions, scope.ServiceProvider.GetRequiredService<IFileSystem>());

    IArtifactStore artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
    await artifactStore.ClearStagingAsync();
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
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status409Conflict);
    }
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

app.MapPost("/api/executions/{id:guid}/cancel", async (
    Guid id,
    IDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    bool cancelled = await dispatcher.CancelAsync(id, "Requested via API", cancellationToken);
    return cancelled ? Results.Ok(new { executionId = id, status = "cancelling" }) : Results.NotFound(new { id });
});

app.MapGet("/api/audit", () => Results.Ok(Array.Empty<object>()));

app.Run();

static IResult LifecycleResult(PluginOperation operation) =>
    operation.Status == PluginOperationStatus.Succeeded
        ? Results.Ok(operation)
        : Results.Json(operation, statusCode: StatusCodes.Status422UnprocessableEntity);

public partial class Program;
