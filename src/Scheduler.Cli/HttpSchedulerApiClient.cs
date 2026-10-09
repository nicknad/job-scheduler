using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Scheduler.Application.Observability;
using Scheduler.Application.Persistence;
using Scheduler.Application.PluginManagement;
using Scheduler.Contracts.Jobs;

namespace Scheduler.Cli;

/// <summary>
/// The thin HTTP client for the management API. Every command maps to one
/// endpoint; no scheduling or lifecycle logic lives here.
/// </summary>
public sealed class HttpSchedulerApiClient : ISchedulerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpSchedulerApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public Task<ExecutionSummary> GetSummaryAsync(TimeSpan? window, CancellationToken cancellationToken = default)
    {
        string query = window is null ? string.Empty : "?window=" + DurationParser.ToQueryValue(window.Value);
        return GetAsync<ExecutionSummary>("api/executions/summary" + query, cancellationToken);
    }

    public Task<IReadOnlyList<JobDefinition>> ListJobsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<JobDefinition>>("api/jobs", cancellationToken);

    public async Task<Guid> RunJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.PostAsync(
            $"api/jobs/{Uri.EscapeDataString(jobId)}/run",
            content: null,
            cancellationToken);
        RunResponse run = await ReadAsync<RunResponse>(response, cancellationToken);
        return run.ExecutionId;
    }

    public Task<IReadOnlyList<ExecutionRecord>> ListExecutionsAsync(
        ExecutionFilter filter,
        CancellationToken cancellationToken = default)
    {
        List<string> query = [];
        Add(query, "jobId", filter.JobId);
        Add(query, "status", filter.Status?.ToString());
        Add(query, "since", Format(filter.Since));
        Add(query, "until", Format(filter.Until));
        Add(query, "limit", filter.Limit.ToString(CultureInfo.InvariantCulture));
        return GetAsync<IReadOnlyList<ExecutionRecord>>("api/executions" + Query(query), cancellationToken);
    }

    public async Task<ExecutionRecord?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync($"api/executions/{executionId:D}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync<ExecutionRecord>(response, cancellationToken);
    }

    public Task<IReadOnlyList<ExecutionLogEntry>> GetExecutionLogsAsync(
        Guid executionId,
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<ExecutionLogEntry>>($"api/executions/{executionId:D}/logs", cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(
        AuditFilter filter,
        CancellationToken cancellationToken = default)
    {
        List<string> query = [];
        Add(query, "actor", filter.Actor);
        Add(query, "action", filter.Action);
        Add(query, "target", filter.Target);
        Add(query, "since", Format(filter.Since));
        Add(query, "limit", filter.Limit.ToString(CultureInfo.InvariantCulture));
        return GetAsync<IReadOnlyList<AuditEntry>>("api/audit" + Query(query), cancellationToken);
    }

    public async Task<HealthReport> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        // Health returns 503 when degraded but still carries the report body.
        using HttpResponseMessage response = await _http.GetAsync("api/health", cancellationToken);
        HealthReport? report = await response.Content.ReadFromJsonAsync<HealthReport>(JsonOptions, cancellationToken);
        return report ?? throw new SchedulerApiException((int)response.StatusCode, "The management API returned an empty body.");
    }

    public Task<IReadOnlyList<PluginDescriptor>> ListPluginsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PluginDescriptor>>("api/plugins", cancellationToken);

    public async Task<PluginOperation> InstallPluginAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        await using Stream stream = File.OpenRead(packagePath);
        using MultipartFormDataContent content = new();
        using StreamContent streamContent = new(stream);
        content.Add(streamContent, "file", Path.GetFileName(packagePath));

        using HttpResponseMessage response = await _http.PostAsync("api/plugins", content, cancellationToken);
        return await ReadAsync<PluginOperation>(response, cancellationToken);
    }

    public Task<ValidationReport> ValidatePluginAsync(
        string pluginId,
        Version version,
        CancellationToken cancellationToken = default) =>
        PostAsync<ValidationReport>($"api/plugins/{Uri.EscapeDataString(pluginId)}/{version}/validate", cancellationToken);

    public Task<PluginOperation> ActivatePluginAsync(
        string pluginId,
        Version version,
        CancellationToken cancellationToken = default) =>
        PostAsync<PluginOperation>($"api/plugins/{Uri.EscapeDataString(pluginId)}/{version}/activate", cancellationToken);

    public Task<PluginOperation> DeactivatePluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        PostAsync<PluginOperation>($"api/plugins/{Uri.EscapeDataString(pluginId)}/deactivate", cancellationToken);

    public Task<PluginOperation> RollbackPluginAsync(
        string pluginId,
        Version version,
        CancellationToken cancellationToken = default) =>
        PostAsync<PluginOperation>($"api/plugins/{Uri.EscapeDataString(pluginId)}/{version}/rollback", cancellationToken);

    public Task<PluginOperation> RemovePluginAsync(string pluginId, CancellationToken cancellationToken = default) =>
        PostAsync<PluginOperation>($"api/plugins/{Uri.EscapeDataString(pluginId)}/remove", cancellationToken);

    private async Task<T> GetAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(uri, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsync(uri, content: null, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorAsync(response, cancellationToken);
        }

        T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new SchedulerApiException((int)response.StatusCode, "The management API returned an empty body.");
    }

    private static async Task<SchedulerApiException> ErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string message = response.ReasonPhrase ?? "The management API request failed.";
        string? reason = null;

        try
        {
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                {
                    message = error.GetString() ?? message;
                }

                if (body.TryGetProperty("reason", out JsonElement reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
                {
                    reason = reasonElement.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body; keep the status phrase.
        }

        return new SchedulerApiException((int)response.StatusCode, message, reason);
    }

    private static void Add(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static string? Format(DateTimeOffset? value) => value?.ToString("o", CultureInfo.InvariantCulture);

    private static string Query(List<string> parts) => parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);

    private sealed record RunResponse(Guid ExecutionId);
}
