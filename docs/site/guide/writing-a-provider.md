---
title: Writing a provider
description: Implement the Millrace storage contract and verify it against the shipped conformance kit.
---

# Writing a storage provider

Millrace's storage contract is deliberately small and deliberately strict. If your database can do
an exclusive claim and a multi-statement transaction, it can back Millrace.

The important thing about this page: **you do not have to read the contract carefully and hope.**
The requirements ship as an executable conformance kit. Reference the package, implement one
interface, and run the suite — what "supported" means is "passes the suite".

## Start with the kit, not the implementation

```bash
dotnet add package Millrace.Storage.Verification
```

Wire it up before you have written anything. A wall of red is a specification you can work against;
prose is not.

You implement one small interface, <xref:Millrace.Storage.Verification.IStorageHarness>, which hands
the suite an **isolated, empty** store:

```csharp
public sealed class MyStorageHarness : IStorageHarness
{
    public IJobStorage Jobs { get; }
    public IWorkflowStorage Workflows { get; }
    public IMonitoringStorage Monitoring { get; }

    public static async ValueTask<IStorageHarness> CreateAsync(TimeProvider time)
    {
        // A fresh schema, database or container per call.
        var storage = new MyStorage(connectionString, time, new MyStorageOptions { Schema = unique });
        await storage.InitializeAsync(CancellationToken.None);
        return new MyStorageHarness(storage);
    }

    public ValueTask DisposeAsync() => /* tear the store down */;
}
```

Then derive the three suites:

```csharp
public sealed class MyJobStorageConformanceTests : JobStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => MyStorageHarness.CreateAsync(time);
}

public sealed class MyWorkflowStorageConformanceTests : WorkflowStorageConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => MyStorageHarness.CreateAsync(time);
}

public sealed class MyMonitoringConformanceTests : MonitoringConformanceSuite
{
    protected override ValueTask<IStorageHarness> CreateHarnessAsync(TimeProvider time)
        => MyStorageHarness.CreateAsync(time);
}
```

That is the whole integration. `dotnet test` now tells you exactly what you have left to do.

> [!IMPORTANT]
> **Every lease and due-time comparison must go through the suite-supplied `TimeProvider`.** That is
> what makes the suite's time-travel assertions deterministic — it advances a fake clock to expire a
> lease rather than sleeping. A provider that reads `DateTime.UtcNow` internally will fail in ways
> that look like flakiness.

`Monitoring` is **required, not optional**. Implementing the dashboard read model is part of the bar
for a supported provider, so the harness cannot opt out of the monitoring facts.

## What you implement

Three interfaces, plus one optional one.

### `IJobStorage`

The job substrate: enqueue, claim, lease renewal, transitions, cancellation, due-job activation and
the recurring-job surface. The methods that carry the interesting guarantees are `ClaimAsync`,
`ApplyAsync` and `TryFireRecurringAsync`.

### `IWorkflowStorage`

Workflow instances, cursors and bookmarks. `ConsumeBookmarkAsync` is the one with a hard concurrency
requirement.

### `IMonitoringStorage`

The dashboard's read model: paged, filtered queries over jobs, recurring definitions and workflow
instances, plus statistics. Cursor-paged with an opaque token and **no total count** — the contract
does not ask you for one, because on most stores it is either expensive or a lie.

### `IStorageNotifier` (optional)

Push wakeups. Implement it when your store can tell a worker that work arrived — PostgreSQL's
`LISTEN/NOTIFY` is the reference case. Advertise it through `StorageCapabilities`.

Without it, workers fall back to adaptive polling. That is a **latency** difference, not a
correctness one: the SQL Server provider ships without a notifier and is fully supported.

> [!NOTE]
> Signals are droppable by design. A missed notification costs latency, never correctness — the poll
> ceiling is the backstop. Do not build an at-least-once delivery mechanism for these; it is not
> needed, and the contract does not assume one.

## The seven guarantees

These are what the suite actually checks.

1. **Claim is exclusive.** Two concurrent `ClaimAsync` calls never return the same job. Use native
   queue semantics where you have them — `FOR UPDATE SKIP LOCKED`, `READPAST` — and compare-and-set
   where you do not.
2. **Claim sets a lease.** A claimed job is invisible to other workers until `LeaseUntil`, and an
   expired lease makes it claimable again. This is worker-crash recovery, and it is the whole of it.
3. **`ApplyAsync` is all-or-nothing.** The state change and its side effects — continuation enqueues,
   checkpoint writes — commit together or not at all.
4. **`ConsumeBookmarkAsync` consumes at most once** under arbitrary concurrency, so a signal resumes
   exactly one waiting instance.
5. **`ClaimDueRecurringAsync` fences.** A due recurring definition fires on exactly one node —
   compare-and-set on `NextFireTime`. This is what removes the need for leader election.
6. **Idempotency keys are unique** among *active* jobs, scoped `(TenantId, IdempotencyKey)`. A null
   tenant is its own scope. Enqueue batches are all-or-nothing and must linearize against concurrent
   terminal key release.
7. **A workflow checkpoint commits with its transition.** `JobTransition.Checkpoint` updates the
   instance in the same atom as the state change and the enqueue inserts. The fence is evaluated
   first — a rejected fence returns `false` and never touches the instance; a stale checkpoint
   revision rolls the whole transition back and throws `MillraceConcurrencyException`.

## Things that catch provider authors out

Collected from writing the three official providers.

- **Byte ordering in cursor tokens.** Opaque does not mean arbitrary: pagination has to be stable and
  total, and two providers disagreeing about the ordering of a composite key is how the kit once
  caught a bug in **its own** specification.
- **Terminal key release is a uniqueness-scope rule, not a field update.** The idempotency key is
  never cleared from the record — it stops constraining uniqueness once the job is terminal. Clearing
  it destroys the provenance the dashboard shows.
- **`GetJobAsync` returns an unfenced snapshot.** It is a read, not a claim, and nothing about it
  reserves the job.
- **Batch enqueue must linearize against concurrent terminal transitions.** The interesting race is a
  batch admitting a key at the same moment another job holding it goes terminal.
- **Don't cache the clock.** See the `TimeProvider` note above.

## Registering it

```csharp
public static MillraceBuilder UseMyStorage(
    this MillraceBuilder builder, string connectionString, Action<MyStorageOptions>? configure = null)
{
    var options = new MyStorageOptions();
    configure?.Invoke(options);

    builder.Services.Replace(ServiceDescriptor.Singleton(sp => new MyStorage(
        connectionString, sp.GetRequiredService<TimeProvider>(), options)));

    return builder.UseStorage(
        sp => sp.GetRequiredService<MyStorage>(),
        sp => sp.GetRequiredService<MyStorage>(),
        sp => sp.GetRequiredService<MyStorage>());
}
```

Use `Replace` rather than `Add` so registration is **last-wins** — that is the convention the
official providers follow, and it is what lets a test host override the composition root.

## Read the official providers

`Millrace.Storage.PostgreSql` and `Millrace.Storage.SqlServer` are the worked examples, and they are
deliberately small. The PostgreSQL one is the better first read if your store has native queue
semantics; the SQL Server one shows what a provider looks like **without** a push channel.
