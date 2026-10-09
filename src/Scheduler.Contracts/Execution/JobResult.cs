namespace Scheduler.Contracts.Execution;

/// <summary>Outcome of one execution attempt.</summary>
public sealed record JobResult
{
    public required JobOutcome Outcome { get; init; }

    /// <summary>Short, sanitized summary of the result. Must never contain secret values.</summary>
    public string? Summary { get; init; }

    /// <summary>Optional structured result data (serializable values only).</summary>
    public IReadOnlyDictionary<string, string?> Data { get; init; } =
        new Dictionary<string, string?>();

    public static JobResult Succeeded(string? summary = null) => new() { Outcome = JobOutcome.Succeeded, Summary = summary };

    public static JobResult Failed(string summary) => new() { Outcome = JobOutcome.Failed, Summary = summary };
}

public enum JobOutcome
{
    Succeeded,
    Failed,
}
