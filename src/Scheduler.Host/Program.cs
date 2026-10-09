using System.IO.Abstractions;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

PersistenceOptions persistenceOptions = new()
{
    DatabasePath = builder.Configuration["JobScheduler:DatabasePath"] ?? "data/jobscheduler.db",
    BaseDirectory = builder.Environment.ContentRootPath,
};
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFileSystem>(new FileSystem());
builder.Services.AddSingleton(persistenceOptions);
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

var app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    IDatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync();
}

app.MapHealthChecks("/healthz");

app.MapGet("/api/plugins", () => Results.Ok(Array.Empty<PluginDescriptor>()));
app.MapPost("/api/plugins", () => NotImplemented("install"));
app.MapPost("/api/plugins/{id}/{version}/validate", (string id, string version) => NotImplemented("validate", id, version));
app.MapPost("/api/plugins/{id}/{version}/activate", (string id, string version) => NotImplemented("activate", id, version));
app.MapPost("/api/plugins/{id}/deactivate", (string id) => NotImplemented("deactivate", id));
app.MapPost("/api/plugins/{id}/{version}/rollback", (string id, string version) => NotImplemented("rollback", id, version));
app.MapGet("/api/plugins/{id}/versions", (string id) => NotImplemented("listVersions", id));

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
