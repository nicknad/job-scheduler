using System.Text;
using System.Text.Json;
using Scheduler.Application.Packaging;
using Scheduler.Application.Persistence;

namespace Scheduler.Application.PluginManagement;

/// <summary>
/// Authoritative lifecycle use cases. Phase 2 implements install (stage →
/// validate → promote) and validation; activation and later transitions are
/// added in phase 3 and are not yet available.
/// </summary>
public sealed class PluginManager : IPluginManager
{
    private const string Actor = "local";

    private readonly IRegistryUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IPackageArchiveReader _archiveReader;
    private readonly IArtifactStore _artifactStore;
    private readonly PackageValidator _validator;
    private readonly IAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;
    private readonly PackagingLimits _limits;
    private readonly Version _hostContractVersion;

    public PluginManager(
        IRegistryUnitOfWorkFactory unitOfWorkFactory,
        IPackageArchiveReader archiveReader,
        IArtifactStore artifactStore,
        PackageValidator validator,
        IAuditWriter auditWriter,
        TimeProvider timeProvider,
        PackagingLimits limits,
        Version hostContractVersion)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkFactory);
        ArgumentNullException.ThrowIfNull(archiveReader);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(hostContractVersion);

        _unitOfWorkFactory = unitOfWorkFactory;
        _archiveReader = archiveReader;
        _artifactStore = artifactStore;
        _validator = validator;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider;
        _limits = limits;
        _hostContractVersion = hostContractVersion;
    }

    public async Task<PluginOperation> InstallAsync(Stream package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        Guid operationId = Guid.NewGuid();
        await CreateOperationAsync(operationId, OperationKind.Install, cancellationToken);
        string? stagingRoot = null;

        try
        {
            stagingRoot = await _artifactStore.CreateStagingDirectoryAsync(operationId, cancellationToken);
            await SetOperationStateAsync(
                operationId,
                OperationState.Running,
                Serialize(new InstallOperationPayload(stagingRoot, null, null)),
                cancellationToken);

            ExtractedPackage extracted =
                await _archiveReader.ExtractAsync(package, stagingRoot, _limits, cancellationToken);
            PackageValidationResult result = _validator.Validate(extracted, _hostContractVersion);

            // A published version is immutable: never mutate its record, even on a
            // rejected re-upload. Resolve it once for both the invalid and valid paths.
            PluginVersionRecord? existing = result.Manifest is null
                ? null
                : await GetExistingAsync(result.Manifest.Id, result.Manifest.Version, cancellationToken);
            bool alreadyPublished = existing is not null && IsPublished(existing.State);

            if (!result.IsValid || result.Manifest is null)
            {
                string reason = string.Join("; ", result.Errors);
                if (!alreadyPublished)
                {
                    await PersistRejectedAsync(result.Manifest, result.Errors, CancellationToken.None);
                }

                await FailOperationAsync(operationId, reason, CancellationToken.None);
                await DiscardStagingAsync(stagingRoot, CancellationToken.None);
                await AuditAsync("plugin.install.rejected", result.Manifest, reason, CancellationToken.None);
                return new PluginOperation(
                    operationId,
                    result.Manifest?.Id ?? string.Empty,
                    result.Manifest?.Version,
                    PluginOperationStatus.Failed,
                    reason);
            }

            PluginManifest manifest = result.Manifest;

            if (alreadyPublished)
            {
                string? conflict = string.Equals(existing!.ArtifactHash, manifest.ArtifactHash, StringComparison.Ordinal)
                    ? null
                    : $"Plugin version '{manifest.Id}' '{manifest.Version}' is already published and immutable.";
                await DiscardStagingAsync(stagingRoot, CancellationToken.None);
                stagingRoot = null;
                await CompleteConflictAsync(operationId, manifest, conflict, CancellationToken.None);
                return new PluginOperation(
                    operationId,
                    manifest.Id,
                    manifest.Version,
                    conflict is null ? PluginOperationStatus.Succeeded : PluginOperationStatus.Failed,
                    conflict);
            }

            DateTimeOffset installedAt = _timeProvider.GetUtcNow();
            await PersistValidatingAsync(manifest, stagingRoot, installedAt, cancellationToken);

            StagedArtifact staged = await _artifactStore.PromoteAsync(
                manifest.Id,
                manifest.Version,
                stagingRoot,
                manifest.ArtifactHash,
                cancellationToken);
            stagingRoot = null;

            await SetStagedAsync(manifest.Id, manifest.Version, staged.ArtifactPath, cancellationToken);
            await SetOperationStateAsync(
                operationId,
                OperationState.Succeeded,
                Serialize(new InstallOperationPayload(null, manifest.Id, manifest.Version.ToString())),
                cancellationToken);
            await AuditAsync("plugin.install", manifest, null, cancellationToken);

            return new PluginOperation(
                operationId,
                manifest.Id,
                manifest.Version,
                PluginOperationStatus.Succeeded);
        }
        catch (PackageValidationException exception)
        {
            await FailOperationAsync(operationId, exception.Message, CancellationToken.None);
            await DiscardStagingAsync(stagingRoot, CancellationToken.None);
            await _auditWriter.RecordAsync(
                Actor,
                "plugin.install.rejected",
                "package",
                exception.Message,
                CancellationToken.None);
            return new PluginOperation(
                operationId,
                string.Empty,
                null,
                PluginOperationStatus.Failed,
                exception.Message);
        }
        catch
        {
            await FailOperationAsync(operationId, "Install failed", CancellationToken.None);
            await DiscardStagingAsync(stagingRoot, CancellationToken.None);
            throw;
        }
    }

    public async Task<ValidationReport> ValidateAsync(
        string pluginId,
        Version version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(version);

        await using (IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken))
        {
            _ = await unitOfWork.Plugins.GetVersionAsync(pluginId, version, cancellationToken)
                ?? throw new KeyNotFoundException($"Plugin version '{pluginId}' '{version}' was not found.");
        }

        ExtractedPackage? package = await _artifactStore.OpenAsync(pluginId, version, cancellationToken);
        if (package is null)
        {
            return ValidationReport.Invalid(pluginId, version, ["No retained artifact available to validate."]);
        }

        PackageValidationResult result = _validator.Validate(package, _hostContractVersion);
        await PersistRevalidationAsync(pluginId, version, result, cancellationToken);
        await _auditWriter.RecordAsync(
            Actor,
            "plugin.validate",
            $"{pluginId}:{version}",
            result.IsValid ? null : string.Join("; ", result.Errors),
            cancellationToken);

        return result.IsValid
            ? ValidationReport.Valid(pluginId, version, result.Warnings)
            : ValidationReport.Invalid(pluginId, version, result.Errors);
    }

    public async Task<IReadOnlyList<PluginDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        IReadOnlyList<string> pluginIds = await unitOfWork.Plugins.ListPluginIdsAsync(cancellationToken);

        List<PluginDescriptor> descriptors = [];
        foreach (string pluginId in pluginIds)
        {
            IReadOnlyList<PluginVersionRecord> versions =
                await unitOfWork.Plugins.ListVersionsAsync(pluginId, cancellationToken);
            PluginActivationRecord? activation =
                await unitOfWork.Plugins.GetActivationAsync(pluginId, cancellationToken);
            descriptors.Add(new PluginDescriptor(pluginId, activation?.Version, ResolveState(versions, activation)));
        }

        return descriptors;
    }

    public async Task<IReadOnlyList<PluginVersionDescriptor>> ListVersionsAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        IReadOnlyList<PluginVersionRecord> versions =
            await unitOfWork.Plugins.ListVersionsAsync(pluginId, cancellationToken);

        return versions
            .Select(version => new PluginVersionDescriptor(
                version.PluginId,
                version.Version,
                version.State,
                version.InstalledAt,
                version.ValidatedAt))
            .ToList();
    }

    // Lifecycle transitions beyond install/validate belong to phase 3.
    public Task<PluginOperation> ActivateAsync(string pluginId, Version version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Activation is implemented in phase 3.");

    public Task<PluginOperation> DeactivateAsync(string pluginId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Deactivation is implemented in phase 3.");

    public Task<PluginOperation> RollbackAsync(string pluginId, Version targetVersion, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Rollback is implemented in phase 3.");

    public Task<PluginOperation> RemoveAsync(string pluginId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Removal is implemented in phase 3.");

    private static PluginLifecycleState ResolveState(
        IReadOnlyList<PluginVersionRecord> versions,
        PluginActivationRecord? activation)
    {
        if (activation is not null)
        {
            return versions.FirstOrDefault(version => version.Version == activation.Version)?.State
                ?? PluginLifecycleState.Failed;
        }

        return versions.Count > 0 ? versions[^1].State : PluginLifecycleState.Uploaded;
    }

    private static bool IsPublished(PluginLifecycleState state) =>
        state is PluginLifecycleState.Staged
            or PluginLifecycleState.Activating
            or PluginLifecycleState.Active
            or PluginLifecycleState.Draining
            or PluginLifecycleState.Retired;

    private async Task<PluginVersionRecord?> GetExistingAsync(
        string pluginId,
        Version version,
        CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        return await unitOfWork.Plugins.GetVersionAsync(pluginId, version, cancellationToken);
    }

    private async Task CompleteConflictAsync(
        Guid operationId,
        PluginManifest manifest,
        string? conflict,
        CancellationToken cancellationToken)
    {
        if (conflict is null)
        {
            await SetOperationStateAsync(
                operationId,
                OperationState.Succeeded,
                Serialize(new InstallOperationPayload(null, manifest.Id, manifest.Version.ToString())),
                cancellationToken);
            await AuditAsync("plugin.install", manifest, "already published", cancellationToken);
            return;
        }

        await FailOperationAsync(operationId, conflict, cancellationToken);
        await _auditWriter.RecordAsync(
            Actor,
            "plugin.install.rejected",
            $"{manifest.Id}:{manifest.Version}",
            conflict,
            cancellationToken);
    }

    private async Task CreateOperationAsync(Guid operationId, OperationKind kind, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        await unitOfWork.Operations.CreateAsync(
            new OperationRecord
            {
                OperationId = operationId,
                Kind = kind,
                Payload = Serialize(new InstallOperationPayload(null, null, null)),
                State = OperationState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task SetOperationStateAsync(
        Guid operationId,
        OperationState state,
        string? payload,
        CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        await unitOfWork.Operations.UpdateStateAsync(
            operationId,
            state,
            payload,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task FailOperationAsync(Guid operationId, string error, CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        OperationRecord? operation = await unitOfWork.Operations.GetAsync(operationId, cancellationToken);
        if (operation is null || OperationTransitions.IsTerminal(operation.State))
        {
            return;
        }

        await unitOfWork.Operations.UpdateStateAsync(
            operationId,
            OperationState.Failed,
            Serialize(new InstallOperationPayload(null, null, null) with { Error = error }),
            _timeProvider.GetUtcNow(),
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task PersistValidatingAsync(
        PluginManifest manifest,
        string stagingRoot,
        DateTimeOffset installedAt,
        CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        await unitOfWork.Plugins.UpsertVersionAsync(
            BuildRecord(manifest, stagingRoot, installedAt) with
            {
                State = PluginLifecycleState.Validating,
                ValidatedAt = null,
            },
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task PersistRejectedAsync(
        PluginManifest? manifest,
        IReadOnlyList<string> errors,
        CancellationToken cancellationToken)
    {
        if (manifest is null)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        PluginVersionRecord? existing = await unitOfWork.Plugins.GetVersionAsync(
            manifest.Id, manifest.Version, cancellationToken);
        if (existing is not null && IsPublished(existing.State))
        {
            return;
        }

        await unitOfWork.Plugins.UpsertVersionAsync(
            BuildRecord(manifest, stagingRoot: null, now) with
            {
                State = PluginLifecycleState.Rejected,
                ValidatedAt = now,
                ValidationError = string.Join("; ", errors),
            },
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task PersistRevalidationAsync(
        string pluginId,
        Version version,
        PackageValidationResult result,
        CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        PluginVersionRecord? record = await unitOfWork.Plugins.GetVersionAsync(pluginId, version, cancellationToken);
        if (record is null || record.State is PluginLifecycleState.Active or PluginLifecycleState.Draining or PluginLifecycleState.Retired)
        {
            return;
        }

        await unitOfWork.Plugins.SetVersionStateAsync(
            pluginId,
            version,
            result.IsValid ? PluginLifecycleState.Staged : PluginLifecycleState.Rejected,
            result.IsValid ? null : string.Join("; ", result.Errors),
            _timeProvider.GetUtcNow(),
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task SetStagedAsync(
        string pluginId,
        Version version,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        await using IRegistryUnitOfWork unitOfWork = await _unitOfWorkFactory.BeginAsync(cancellationToken);
        await unitOfWork.Plugins.SetStagedAsync(pluginId, version, artifactPath, _timeProvider.GetUtcNow(), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
    }

    private async Task DiscardStagingAsync(string? stagingRoot, CancellationToken cancellationToken)
    {
        if (stagingRoot is null)
        {
            return;
        }

        await _artifactStore.DiscardStagingAsync(stagingRoot, cancellationToken);
    }

    private Task AuditAsync(string action, PluginManifest? manifest, string? details, CancellationToken cancellationToken)
    {
        string target = manifest is null ? "package" : $"{manifest.Id}:{manifest.Version}";
        return _auditWriter.RecordAsync(Actor, action, target, details, cancellationToken);
    }

    private static PluginVersionRecord BuildRecord(
        PluginManifest manifest,
        string? stagingRoot,
        DateTimeOffset installedAt) => new()
        {
            PluginId = manifest.Id,
            Version = manifest.Version,
            ContractVersion = manifest.ContractVersion.ToString(),
            EntryAssembly = manifest.EntryAssembly,
            EntryType = manifest.EntryType,
            ExecutionMode = manifest.ExecutionMode,
            ArtifactHash = manifest.ArtifactHash,
            State = PluginLifecycleState.Validating,
            InstalledAt = installedAt,
            ManifestJson = Encoding.UTF8.GetString(PluginManifestJson.ToCanonicalUtf8(manifest)),
            StagingPath = stagingRoot,
        };

    private static string Serialize(InstallOperationPayload payload) => JsonSerializer.Serialize(payload);

    private sealed record InstallOperationPayload(string? StagingRoot, string? PluginId, string? Version)
    {
        public string? Error { get; init; }
    }
}
