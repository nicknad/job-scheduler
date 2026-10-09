using Scheduler.Application.PluginManagement;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

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
