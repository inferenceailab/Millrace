using Millrace.Storage;
using Millrace.Storage.InMemory;
using Millrace.Storage.Verification;

namespace Millrace.Tests.Storage;

internal sealed class InMemoryHarness(TimeProvider time) : IStorageHarness
{
    private readonly InMemoryStorage _storage = new(time);

    public IJobStorage Jobs => _storage;

    public IWorkflowStorage Workflows => _storage;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The bundled InMemory provider must pass its own conformance kit.</summary>
public sealed class InMemoryJobStorageConformanceTests : JobStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => ValueTask.FromResult<IStorageHarness>(new InMemoryHarness(time));
}

public sealed class InMemoryWorkflowStorageConformanceTests : WorkflowStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => ValueTask.FromResult<IStorageHarness>(new InMemoryHarness(time));
}
