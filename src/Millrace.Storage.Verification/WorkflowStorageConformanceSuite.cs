using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Millrace.Storage;
using Xunit;

namespace Millrace.Storage.Verification;

/// <summary>
/// The workflow-storage conformance suite: optimistic concurrency on instances and at-most-once
/// bookmark consumption (ARCHITECTURE.md §4.2.4). The engine that drives this contract lands in
/// 0.3; the contract freezes now.
/// </summary>
public abstract partial class WorkflowStorageConformanceSuite
{
    /// <inheritdoc cref="JobStorageConformanceSuite.Epoch"/>
    protected static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a fresh, empty store bound to <paramref name="time"/>.</summary>
    protected abstract ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time);

    /// <inheritdoc cref="JobStorageConformanceSuite.NewTime"/>
    protected static FakeTimeProvider NewTime() => new(Epoch);

    /// <summary>
    /// Data/cursor documents are JSON, not opaque strings: providers may normalize lexical
    /// formatting (jsonb whitespace/key order), so fidelity is asserted semantically.
    /// </summary>
    protected static void AssertJsonEqual(string expected, string? actual) => JsonAssert.Equal(expected, actual);

    /// <summary>Builds a running instance at revision 1, the shape a create should store.</summary>
    protected static WorkflowInstanceRecord Instance(TimeProvider time) => new()
    {
        Id = WorkflowInstanceId.New(time),
        DefinitionId = "conformance-flow",
        DefinitionVersion = 1,
        State = WorkflowInstanceState.Running,
        DataJson = """{"value":1}""",
        Revision = 1,
        CreatedAt = time.GetUtcNow(),
        UpdatedAt = time.GetUtcNow(),
    };

    /// <summary>Builds a bookmark waiting on a named signal for one correlation id.</summary>
    /// <remarks>
    /// The defaults collide on purpose: two bookmarks built without overriding them match the same
    /// signal, which is what the at-most-once facts need in order to prove that delivering it
    /// resumes exactly one instance.
    /// </remarks>
    protected static BookmarkRecord Bookmark(
        TimeProvider time, WorkflowInstanceId instanceId,
        string signalName = "approval", string correlationId = "order-1") => new()
    {
        Id = Guid.CreateVersion7(time.GetUtcNow()),
        InstanceId = instanceId,
        SignalName = signalName,
        CorrelationId = correlationId,
        CreatedAt = time.GetUtcNow(),
    };

    [Fact]
    public async Task Create_and_get_roundtrip_with_revision_one()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);

        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);
        var stored = await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(instance.Id, stored.Id);
        Assert.Equal(1, stored.Revision);
        AssertJsonEqual(instance.DataJson, stored.DataJson);
    }

    [Fact]
    public async Task Create_normalizes_revision_to_one_regardless_of_input()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time) with { Revision = 42 };

        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        Assert.Equal(1, (await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task Duplicate_create_throws_concurrency_exception()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        await Assert.ThrowsAsync<MillraceConcurrencyException>(async () =>
            await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None));
    }

    [Fact]
    public async Task Update_with_matching_revision_increments_it()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        await harness.Workflows.UpdateInstanceAsync(
            instance with { DataJson = """{"value":2}""" }, expectedRevision: 1, CancellationToken.None);

        var stored = (await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None))!;
        Assert.Equal(2, stored.Revision);
        AssertJsonEqual("""{"value":2}""", stored.DataJson);
    }

    [Fact]
    public async Task Update_with_stale_revision_throws_and_changes_nothing()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);
        await harness.Workflows.UpdateInstanceAsync(
            instance with { DataJson = """{"value":2}""" }, expectedRevision: 1, CancellationToken.None);

        await Assert.ThrowsAsync<MillraceConcurrencyException>(async () =>
            await harness.Workflows.UpdateInstanceAsync(
                instance with { DataJson = """{"value":99}""" }, expectedRevision: 1, CancellationToken.None));

        var stored = (await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None))!;
        Assert.Equal(2, stored.Revision);
        AssertJsonEqual("""{"value":2}""", stored.DataJson);
    }

    [Fact]
    public async Task Update_of_missing_instance_throws_concurrency_exception()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);

        await Assert.ThrowsAsync<MillraceConcurrencyException>(async () =>
            await harness.Workflows.UpdateInstanceAsync(Instance(time), expectedRevision: 1, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_updates_have_exactly_one_winner()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await harness.Workflows.UpdateInstanceAsync(
                    instance with { DataJson = $$"""{"writer":{{i}}}""" },
                    expectedRevision: 1, CancellationToken.None);
                return true;
            }
            catch (MillraceConcurrencyException)
            {
                return false;
            }
        })));

        Assert.Equal(1, outcomes.Count(won => won));
        Assert.Equal(2, (await harness.Workflows.GetInstanceAsync(instance.Id, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task Consume_with_no_match_returns_null()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);
        await harness.Workflows.AddBookmarkAsync(Bookmark(time, instance.Id), CancellationToken.None);

        Assert.Null(await harness.Workflows.ConsumeBookmarkAsync("approval", "other-order", CancellationToken.None));
        Assert.Null(await harness.Workflows.ConsumeBookmarkAsync("rejection", "order-1", CancellationToken.None));
    }

    [Fact]
    public async Task Consume_is_at_most_once_under_contention()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);
        await harness.Workflows.AddBookmarkAsync(Bookmark(time, instance.Id), CancellationToken.None);

        var consumed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None))));

        Assert.Equal(1, consumed.Count(b => b is not null));
    }

    [Fact]
    public async Task Consume_returns_the_oldest_matching_bookmark()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance1 = Instance(time);
        var instance2 = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance1, CancellationToken.None);
        await harness.Workflows.CreateInstanceAsync(instance2, CancellationToken.None);

        var older = Bookmark(time, instance1.Id);
        await harness.Workflows.AddBookmarkAsync(older, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var newer = Bookmark(time, instance2.Id);
        await harness.Workflows.AddBookmarkAsync(newer, CancellationToken.None);

        var first = await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None);
        var second = await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None);
        var third = await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None);

        Assert.Equal(older.Id, first!.Id);
        Assert.Equal(newer.Id, second!.Id);
        Assert.Null(third);
    }

    [Fact]
    public async Task Consume_breaks_created_at_ties_by_id()
    {
        var time = NewTime();
        await using var harness = await CreateHarnessAsync(time);
        var instance = Instance(time);
        await harness.Workflows.CreateInstanceAsync(instance, CancellationToken.None);

        // Same CreatedAt (time never advances) — the contract's tie-break is the bookmark Id, in
        // byte order.
        //
        // Not Guid.CompareTo: that compares the leading fields in native endianness, which matches
        // no database's ordering, so an expectation built on it is satisfied or not depending on
        // which random bits a UUIDv7 happened to get. This fact passed on PostgreSQL for months by
        // luck, and only failed once a second provider existed to disagree with it.
        var a = Bookmark(time, instance.Id);
        var b = Bookmark(time, instance.Id);
        await harness.Workflows.AddBookmarkAsync(a, CancellationToken.None);
        await harness.Workflows.AddBookmarkAsync(b, CancellationToken.None);
        var expectedFirst = Millrace.Storage.Monitoring.MonitoringCursor.CompareIds(a.Id, b.Id) < 0 ? a.Id : b.Id;
        var expectedSecond = expectedFirst == a.Id ? b.Id : a.Id;

        var first = await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None);
        var second = await harness.Workflows.ConsumeBookmarkAsync("approval", "order-1", CancellationToken.None);

        Assert.Equal(expectedFirst, first!.Id);
        Assert.Equal(expectedSecond, second!.Id);
    }
}
