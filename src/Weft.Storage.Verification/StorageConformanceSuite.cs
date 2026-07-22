namespace Weft.Storage.Verification;

/// <summary>
/// Base class for storage provider conformance suites. Provider test projects inherit this and
/// supply a factory for their <c>IJobStorage</c>; the suite verifies the atomicity contract
/// (ARCHITECTURE.md §4.2): exclusive claims, lease semantics, atomic transitions, at-most-once
/// bookmark consumption, fenced recurring firing, and idempotency-key uniqueness.
/// </summary>
public abstract class StorageConformanceSuite
{
    // Suites land in phase 0.1 alongside the IJobStorage abstraction.
}
