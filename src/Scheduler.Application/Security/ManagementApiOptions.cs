namespace Scheduler.Application.Security;

/// <summary>
/// Management-API authentication configuration. Credentials are named by a
/// secret reference (never a literal value); the referenced value lives in the
/// encrypted store and is loaded at startup. Authentication is on by default.
/// </summary>
public sealed class ManagementApiOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>When false (default), the API is intended to bind loopback only.</summary>
    public bool AllowRemoteAccess { get; init; }

    public IReadOnlyList<ApiCredentialOptions> Credentials { get; init; } = [];
}

/// <summary>One configured API credential: a secret reference and its scopes.</summary>
public sealed class ApiCredentialOptions
{
    public required string Reference { get; init; }

    public IReadOnlyList<AuthorizationScope> Scopes { get; init; } = [];
}

/// <summary>The authenticated caller: a credential name and its granted scopes.</summary>
public sealed record ApiPrincipal(string Name, AuthorizationScope Scopes)
{
    /// <summary>Whether the principal holds every scope in <paramref name="required" />.</summary>
    public bool Has(AuthorizationScope required) => (Scopes & required) == required;
}
