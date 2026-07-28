---
title: Testing your jobs
description: Deterministic testing with Millrace.Testing — no polling, no sleeping, and a seven-day timeout in one call.
---

# Testing your jobs

Background work is notoriously awkward to test. The usual approach — enqueue, `Task.Delay(500)`,
assert, hope — produces tests that are slow, flaky, and quietly useless when a machine is loaded.

`Millrace.Testing` removes the waiting entirely.

```bash
dotnet add package Millrace.Testing --prerelease
```

## The shape of a test

```csharp
await using var millrace = MillraceTestHost.Create(
    services => services.AddScoped<IEmailSender, EmailSender>());

await millrace.Jobs.EnqueueAsync<IEmailSender>(s => s.SendAsync(orderId));
await millrace.RunUntilIdleAsync();

Assert.True(sent);
```

No polling and no sleeping — and no chance of the assertion running before the job does.

## How it works

The real worker pool and scheduler are **switched off**. Instead,
<xref:Millrace.Testing.MillraceTestHost.RunUntilIdleAsync*> drains the queue on the calling thread:
activate what is due, claim it, run it, apply the transition, repeat until nothing is left.

That is what makes the test deterministic. Leaving the real workers running would race every
assertion and put the sleeps straight back.

Storage is the bundled in-memory provider. This is for testing **your** jobs and workflows — the
storage contract itself is covered by the [conformance kit](writing-a-provider.md).

## Registering services

`Create` takes the services your jobs depend on, and optionally extra Millrace configuration:

```csharp
await using var millrace = MillraceTestHost.Create(
    configure: services =>
    {
        services.AddScoped<IEmailSender, FakeEmailSender>();
        services.AddSingleton(_clock);
    },
    millrace: builder => builder.AddWorkflow<OnboardingWorkflow>(),
    startingAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
```

`startingAt` defaults to 2026-01-01Z. Set it when a cron expression or a date-dependent assertion
needs a specific starting point.

## Travelling in time

The clock is a `FakeTimeProvider`, so delays, retry backoff and signal timeouts are **reached**
rather than waited out.

```csharp
await millrace.Jobs.ScheduleAsync<IReportService>(
    s => s.GenerateAsync("nightly"), TimeSpan.FromDays(7));

await millrace.RunUntilIdleAsync();
Assert.Equal(0, generated);          // nothing is due yet

await millrace.AdvanceTime(TimeSpan.FromDays(7));
await millrace.RunUntilIdleAsync();
Assert.Equal(1, generated);
```

A seven-day timeout is one call, and the test runs in milliseconds.

> [!NOTE]
> `AdvanceTime` moves the clock and activates what became due — it does **not** execute. That
> separation lets a test advance and assert that something is *ready* without running it. Call
> `RunUntilIdleAsync` when you want it to run.

## Testing failure

By default a job that exhausts its retries **rethrows** out of `RunUntilIdleAsync`, so a broken job
fails the test loudly rather than leaving an unexplained assertion failure three lines later.

When the failure is the thing under test, opt out:

```csharp
await millrace.RunUntilIdleAsync(throwOnFailure: false);

var job = await millrace.GetJobAsync(jobId);
Assert.Equal(JobState.Dead, job!.State);
```

`RunUntilIdleAsync` returns how many job **executions** ran, counting retries separately — which is
often the cleanest way to assert a retry policy did what you expected:

```csharp
await millrace.Jobs.EnqueueAsync<IFlaky>(f => f.WorkAsync(),
    new EnqueueOptions { Retry = Retry.Fixed(TimeSpan.Zero, 3) });

var executions = await millrace.RunUntilIdleAsync(throwOnFailure: false);
Assert.Equal(3, executions);
```

> [!TIP]
> `RunUntilIdleAsync` is bounded at 10,000 passes, so a job that endlessly re-enqueues itself fails
> the test instead of hanging the suite.

## Testing workflows

Workflows work the same way. Register the definition, start an instance, drain.

```csharp
await using var millrace = MillraceTestHost.Create(
    services => services.AddScoped<IAccounts, FakeAccounts>(),
    builder => builder.AddWorkflow<OnboardingWorkflow>());

var id = await millrace.Workflows.StartAsync("onboarding",
    new OnboardingData { CustomerId = "c-1", NeedsApproval = true });

await millrace.RunUntilIdleAsync();

// Parked on the signal — the branch was taken and nothing else can advance.
var state = await millrace.GetInstanceStateAsync(id);
Assert.Equal(WorkflowInstanceState.Suspended, state);

await millrace.Workflows.SignalAsync("approval", "c-1", new ApprovalDecision(true));
await millrace.RunUntilIdleAsync();

Assert.Equal(WorkflowInstanceState.Completed, await millrace.GetInstanceStateAsync(id));

var data = await millrace.GetDataAsync<OnboardingData>(id);
Assert.True(data!.Approved);
```

To test a **signal timeout**, advance past it instead of sending anything:

```csharp
await millrace.AdvanceTime(TimeSpan.FromDays(3));
await millrace.RunUntilIdleAsync();
Assert.Equal(WorkflowInstanceState.Completed, await millrace.GetInstanceStateAsync(id));
```

## Inspecting state

| Member | Returns |
|---|---|
| `Jobs` | `IJobClient`, as the application under test sees it. |
| `Workflows` | `IWorkflowClient`. |
| `Time` | The `FakeTimeProvider`. Prefer `AdvanceTime` for moving it. |
| `Services` | The service provider, for resolving your own types. |
| `GetJobAsync(id)` | The `JobRecord` — state, attempts, failures. |
| `GetInstanceAsync(id)` | The workflow instance record. |
| `GetInstanceStateAsync(id)` | Just the instance's state. |
| `GetDataAsync<TData>(id)` | The instance's data document. |

## Testing a saga's compensation

Compensation triggers on **exhausted retries**, so give the failing step a retry policy that
exhausts quickly — otherwise the test spends its time advancing through backoff:

```csharp
await using var millrace = MillraceTestHost.Create(
    services =>
    {
        services.AddScoped<IInventory, FakeInventory>();
        services.AddScoped<IPayments, AlwaysFailingPayments>();
    },
    builder => builder.AddWorkflow<CheckoutSaga>());

var id = await millrace.Workflows.StartAsync("checkout", new CheckoutData());
await millrace.RunUntilIdleAsync(throwOnFailure: false);

Assert.True(inventoryReleased);   // the compensation ran
```

## What this does not cover

Deliberately:

- **Storage behaviour.** The in-memory provider passes the conformance suite, but a test here tells
  you nothing about your production provider's SQL. That is what the conformance kit is for.
- **Concurrency.** The harness runs jobs on one thread, on purpose. Determinism is the feature; if
  you need to test genuine parallel claiming, you need a real provider and a real worker pool.
- **Wall-clock timing.** Everything is a fake clock. Nothing here measures how long anything takes.
