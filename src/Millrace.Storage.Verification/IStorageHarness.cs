using Millrace.Storage;

namespace Millrace.Storage.Verification;

/// <summary>
/// One isolated storage instance under test. Provider test projects create a <b>fresh, empty</b>
/// store per call (a new schema, database, or container) bound to the suite-supplied
/// <see cref="TimeProvider"/> — every lease and due-time comparison must go through it, which is
/// what makes the suite's time-travel assertions deterministic. Dispose tears the store down.
/// </summary>
public interface IStorageHarness : IAsyncDisposable
{
    IJobStorage Jobs { get; }

    IWorkflowStorage Workflows { get; }

    /// <summary>
    /// The dashboard read model. Required, not optional: §11.14 makes implementing
    /// <see cref="Monitoring.IMonitoringStorage"/> part of the bar for a supported provider, so a
    /// harness cannot opt out of the monitoring facts.
    /// </summary>
    Monitoring.IMonitoringStorage Monitoring { get; }
}
