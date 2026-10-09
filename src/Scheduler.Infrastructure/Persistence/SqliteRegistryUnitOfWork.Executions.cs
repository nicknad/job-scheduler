using Microsoft.Data.Sqlite;
using Scheduler.Application.Persistence;
using Scheduler.Contracts.Execution;

namespace Scheduler.Infrastructure.Persistence;

internal sealed partial class SqliteRegistryUnitOfWork
{
    private const string ExecutionColumns =
        "execution_id, job_id, plugin_id, plugin_version, config_revision, attempt, status, " +
        "scheduled_at, started_at, ended_at, result_summary, cancellation_reason";

    async Task<ExecutionRecord?> IExecutionRepository.GetAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = CreateCommand(
            "SELECT " + ExecutionColumns + " FROM executions WHERE execution_id = $executionId;");
        command.Parameters.AddWithValue("$executionId", executionId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadExecution(reader) : null;
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListByJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        await using SqliteCommand command = CreateCommand(
            "SELECT " + ExecutionColumns + " FROM executions WHERE job_id = $jobId ORDER BY scheduled_at;");
        command.Parameters.AddWithValue("$jobId", jobId);

        return await ReadExecutionsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionRecord>> ListByStatusAsync(
        JobExecutionStatus status,
        CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = CreateCommand(
            "SELECT " + ExecutionColumns + " FROM executions WHERE status = $status ORDER BY scheduled_at;");
        command.Parameters.AddWithValue("$status", status.ToString());

        return await ReadExecutionsAsync(command, cancellationToken);
    }

    public async Task CreateAsync(ExecutionRecord execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await using SqliteCommand command = CreateCommand(
            """
            INSERT INTO executions
                (execution_id, job_id, plugin_id, plugin_version, config_revision, attempt, status,
                 scheduled_at, started_at, ended_at, result_summary, cancellation_reason)
            VALUES
                ($executionId, $jobId, $pluginId, $pluginVersion, $configRevision, $attempt, $status,
                 $scheduledAt, $startedAt, $endedAt, $resultSummary, $cancellationReason);
            """);
        AddExecutionParameters(command, execution);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(ExecutionRecord execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        await using SqliteCommand command = CreateCommand(
            """
            UPDATE executions
               SET status = $status,
                   started_at = $startedAt,
                   ended_at = $endedAt,
                   result_summary = $resultSummary,
                   cancellation_reason = $cancellationReason
             WHERE execution_id = $executionId;
            """);
        command.Parameters.AddWithValue("$status", execution.Status.ToString());
        command.Parameters.AddWithValue(
            "$startedAt",
            execution.StartedAt.HasValue ? DbTimestamp.Format(execution.StartedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$endedAt",
            execution.EndedAt.HasValue ? DbTimestamp.Format(execution.EndedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$resultSummary", execution.ResultSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$cancellationReason",
            execution.CancellationReason ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString("D"));

        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Execution '{execution.ExecutionId}' was not found.");
        }
    }

    private static void AddExecutionParameters(SqliteCommand command, ExecutionRecord execution)
    {
        command.Parameters.AddWithValue("$executionId", execution.ExecutionId.ToString("D"));
        command.Parameters.AddWithValue("$jobId", execution.JobId);
        command.Parameters.AddWithValue("$pluginId", execution.PluginId);
        command.Parameters.AddWithValue("$pluginVersion", execution.PluginVersion.ToString());
        command.Parameters.AddWithValue("$configRevision", execution.ConfigurationRevision);
        command.Parameters.AddWithValue("$attempt", execution.Attempt);
        command.Parameters.AddWithValue("$status", execution.Status.ToString());
        command.Parameters.AddWithValue("$scheduledAt", DbTimestamp.Format(execution.ScheduledAt));
        command.Parameters.AddWithValue(
            "$startedAt",
            execution.StartedAt.HasValue ? DbTimestamp.Format(execution.StartedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$endedAt",
            execution.EndedAt.HasValue ? DbTimestamp.Format(execution.EndedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$resultSummary", execution.ResultSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$cancellationReason",
            execution.CancellationReason ?? (object)DBNull.Value);
    }

    private static async Task<IReadOnlyList<ExecutionRecord>> ReadExecutionsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<ExecutionRecord> executions = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            executions.Add(ReadExecution(reader));
        }

        return executions;
    }

    private static ExecutionRecord ReadExecution(SqliteDataReader reader) => new()
    {
        ExecutionId = Guid.Parse(reader.GetString(0)),
        JobId = reader.GetString(1),
        PluginId = reader.GetString(2),
        PluginVersion = Version.Parse(reader.GetString(3)),
        ConfigurationRevision = reader.GetInt32(4),
        Attempt = reader.GetInt32(5),
        Status = Enum.Parse<JobExecutionStatus>(reader.GetString(6)),
        ScheduledAt = DbTimestamp.Parse(reader.GetString(7)),
        StartedAt = reader.IsDBNull(8) ? null : DbTimestamp.Parse(reader.GetString(8)),
        EndedAt = reader.IsDBNull(9) ? null : DbTimestamp.Parse(reader.GetString(9)),
        ResultSummary = reader.IsDBNull(10) ? null : reader.GetString(10),
        CancellationReason = reader.IsDBNull(11) ? null : reader.GetString(11),
    };
}
