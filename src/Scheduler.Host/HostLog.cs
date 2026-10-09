namespace Scheduler.Host;

/// <summary>Source-generated log messages for the host composition root.</summary>
internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Post-lifecycle reconciliation failed; the periodic sweep will retry.")]
    public static partial void PostLifecycleReconciliationFailed(ILogger logger, Exception exception);
}
