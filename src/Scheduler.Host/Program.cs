using System.IO.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Scheduler.Application.Packaging;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Contracts;
using Scheduler.Infrastructure.Packaging;
using Scheduler.Infrastructure.Persistence;

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFileSystem>(new FileSystem());
builder.Services.AddSingleton(persistenceOptions);
builder.Services.AddSingleton(packagingOptions);
builder.Services.AddSingleton(packagingOptions.ToLimits());
builder.Services.AddSingleton(SchedulerContract.CurrentVersion);
builder.Services.AddSingleton<ISqliteConnectionFactory>(services =>
    new SqliteConnectionFactory(
        services.GetRequiredService<PersistenceOptions>(),
        services.GetRequiredService<IFileSystem>()));
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
builder.Services.AddSingleton<IPluginManager, PluginManager>();

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

app.MapPost("/api/plugins/{id}/{version}/activate", (string id, string version) => NotImplemented("activate", id, version));
app.MapPost("/api/plugins/{id}/deactivate", (string id) => NotImplemented("deactivate", id));
app.MapPost("/api/plugins/{id}/{version}/rollback", (string id, string version) => NotImplemented("rollback", id, version));

app.MapGet("/api/jobs", () => Results.Ok(Array.Empty<object>()));
app.MapPut("/api/jobs/{id}", (string id) => NotImplemented("updateJob", id));
app.MapPost("/api/jobs/{id}/run", (string id) => NotImplemented("runJob", id));

app.MapPost("/api/executions/{id}/cancel", (Guid id) => NotImplemented("cancelExecution", id));
app.MapGet("/api/executions/{id}", (Guid id) => Results.NotFound(new { id }));

app.MapGet("/api/audit", () => Results.Ok(Array.Empty<object>()));

app.Run();

static IResult NotImplemented(string operation, params object?[] targets) =>
    Results.Json(new { operation, targets }, statusCode: StatusCodes.Status501NotImplemented);

public partial class Program;
