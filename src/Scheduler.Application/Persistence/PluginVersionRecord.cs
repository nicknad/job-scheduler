using Scheduler.Application.PluginManagement;
using Scheduler.Contracts.Execution;

namespace Scheduler.Application.Persistence;

/// <summary>
/// Durable projection of one plugin version row. State transitions belong to the
/// plugin manager; this type carries only the persisted shape.
/// </summary>
public sealed record PluginVersionRecord
{
    public required string PluginId { get; init; }

    public required Version Version { get; init; }

    public required string ContractVersion { get; init; }

    public required string EntryAssembly { get; init; }

    public required string EntryType { get; init; }

    public required ExecutionMode ExecutionMode { get; init; }

    public required string ArtifactHash { get; init; }

    public required PluginLifecycleState State { get; init; }

    public required DateTimeOffset InstalledAt { get; init; }

    public DateTimeOffset? ValidatedAt { get; init; }

    public string? ValidationError { get; init; }
}
