---
title: Jobs
description: Enqueue, schedule, chain, batch and manage durable background jobs.
---

# Jobs

A job is a serialized method call that survives a restart. Millrace captures the declared service
type, the method and the arguments; a worker resolves the service from DI and invokes it later,
possibly on another node.

Everything on this page is <xref:Millrace.IJobClient>, injected wherever you need it.

## The five shapes

### Fire-and-forget

Claimable immediately by any node listening to the queue.

```csharp
var jobId = await jobs.EnqueueAsync<IEmailSender>(s => s.SendConfirmationAsync(orderId));
```

### Delayed and scheduled

Durable by construction: the delay is a row with a due time, not a timer in memory, so a seven-day
delay fires seven days later even across deploys.

```csharp
await jobs.ScheduleAsync<IEmailSender>(s => s.SendReminderAsync(orderId), TimeSpan.FromHours(2));

await jobs.ScheduleAsync<IReportService>(s => s.GenerateAsync("march"), new DateTimeOffset(...));
```

### Recurring (cron)

Five-field vixie cron, evaluated in **UTC**. The recurring id is the definition's identity, so
calling this twice with the same id updates rather than duplicates — upserting is idempotent, which
makes it safe to run at startup.

```csharp
await jobs.UpsertRecurringAsync<IReportService>(
    "nightly-report", "0 3 * * *", s => s.GenerateAsync("nightly"));

await jobs.RemoveRecurringAsync("nightly-report");
```

Removing a definition stops future occurrences. Occurrences it already fired are ordinary jobs by
then and run to completion, keeping their link back to the id that produced them. That link is
**provenance, not a live reference** — so removing a schedule leaves its history readable rather
than dangling or cascading.

> [!NOTE]
> A due occurrence fires on exactly one node, fenced by compare-and-set. No leader election, and no
> duplicate occurrence when two nodes wake in the same second.

### Continuations

Runs only if the parent **succeeds**. If the parent dies or is cancelled, the continuation is
cancelled too — transitively, through the whole chain.

```csharp
var charge = await jobs.EnqueueAsync<IPayments>(p => p.CaptureAsync(orderId));
var notify = await jobs.ContinueWithAsync<INotifier>(charge, n => n.NotifySettledAsync(orderId));
```

Continuations are the right tool for a short linear chain. Once you need a branch, a parallel fan-out
or compensation, reach for a [workflow](workflows.md) instead.

### Batches

One round trip and one transaction, with ids returned positionally.

```csharp
var batch = new JobBatch();
foreach (var id in orderIds)
{
    batch.Enqueue<IEmailSender>(s => s.SendAsync(id));
}

IReadOnlyList<JobId> ids = await jobs.EnqueueBatchAsync(batch);
```

**All-or-nothing**: every job lands or none does. A partially-enqueued fan-out is worse than no
fan-out, because the caller cannot tell which half landed and retrying duplicates the rest.

## Options

<xref:Millrace.EnqueueOptions> is accepted by every enqueue method.

```csharp
await jobs.EnqueueAsync<IPayments>(p => p.CaptureAsync(paymentId),
    new EnqueueOptions
    {
        Queue = "payments",
        Priority = 10,
        Retry = Retry.Exponential(5),
        IdempotencyKey = $"capture:{paymentId}",
    });
```

| Option | Meaning |
|---|---|
| `Queue` | Which queue to write to. Defaults to `"default"`. |
| `Priority` | Higher is claimed first; FIFO within equal priority. Default 0. |
| `Retry` | Overrides the node's `DefaultRetry`. |
| `IdempotencyKey` | At most one active job per key per tenant — see [Delivery guarantees](delivery.md#enqueue-time-idempotency-keys). |

## Retries

<xref:Millrace.Retry> is plain data serialized onto the job record. The **engine** evaluates it, not
the storage provider, so retry behaviour is identical on every provider.

```csharp
Retry.None                                   // first failure is final
Retry.Fixed(TimeSpan.FromSeconds(30), 3)     // 30s between every attempt, 3 attempts total
Retry.Exponential(5)                         // 5s, 10s, 20s, 40s — capped at 1 hour
Retry.Exponential(8, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10))
```

> [!IMPORTANT]
> `MaxAttempts` counts **total** attempts including the first. `Retry.Exponential(5)` means at most
> five executions, not one plus five.

The default is `Retry.Exponential(5)` — a 5 second base doubling to a 1 hour ceiling. Quick enough to
ride out a blip, slow enough that a sustained outage is not hammered.

When attempts are exhausted the job moves to **`Dead`**. It is kept, not deleted, so the
[dashboard](dashboard.md) can show it and an operator can requeue it once the cause is fixed.

## Queues and priority

A node claims from the queues it is configured for:

```csharp
builder.Services.AddMillrace(millrace =>
{
    millrace.UsePostgreSqlStorage(connectionString);
    millrace.Configure(options =>
    {
        options.Queues.Clear();
        options.Queues.Add("payments");
        options.MaxParallelism = 32;
    });
});
```

Queues are **unordered** — there is no queue precedence. A node claiming from `payments` and
`default` treats them as one pool, and priority orders within the pool. If you need payments to
starve everything else, give them their own node rather than expecting an ordering that does not
exist.

> [!WARNING]
> A job enqueued to a queue that **no node claims from** sits there forever, with no error anywhere.
> This is the most common misconfiguration. The same trap applies to
> <xref:Millrace.MillraceOptions.WorkflowQueue>, which is why it is explicit rather than derived.

## Management

These are the operations the [dashboard](dashboard.md) exposes, available in code too.

```csharp
await jobs.CancelAsync(jobId);
await jobs.RunNowAsync(jobId);
await jobs.TriggerRecurringAsync("nightly-report");
JobId? replacement = await jobs.RequeueAsync(deadJobId);
```

**`CancelAsync`** cancels a pre-active job outright, along with its transitive continuation closure.
A job already *running* is asked to stop cooperatively — the flag reaches it through lease renewal —
so a worker about to finish may still succeed. `true` means something was cancelled, not that the
work did not happen.

**`RunNowAsync`** is for one specific situation: a job is waiting out its retry backoff, the cause
was fixed and deployed at 09:00, and the next attempt is not due until 09:40. It shortens the wait
and nothing else — **no retry budget is consumed**, because nothing was attempted. It returns false
for anything not awaiting a retry.

**`RequeueAsync`** mints a **new** job carrying a link back to the original, rather than reviving it.
Terminal records are immutable everywhere else in the contract, and rewriting one for a single
operator action would break that. Three things follow rather than being chosen:

- the retry budget starts fresh, because the job has never failed;
- the idempotency key is carried, so if another active job already holds it you get *that* job's id
  back — the right answer to "run it again" when an equivalent run is already in flight;
- the original's continuations stay cancelled, because nothing about a new job resurrects them.

Requeueing a job that is still running is refused; use `CancelAsync` first.

**`TriggerRecurringAsync`** fires an extra occurrence without disturbing the schedule — `NextFireTime`
is untouched, so the normal cadence continues.

## Job lifecycle

```
        ┌────────── retry (delay elapsed) ──────────┐
        ▼                                           │
Scheduled ──due──► Enqueued ──claim──► Processing ──┼──► Succeeded
                      ▲                    │        │
                      └── lease expired ───┤        └──► Failed ──► Dead (retries exhausted)
                                           └──────────► Cancelled
```

A claim takes a **lease** (default 5 minutes), renewed by a heartbeat while the job runs. If the node
dies, the lease expires and the job becomes claimable again — which is the recovery mechanism, and
also the reason execution is [at-least-once](delivery.md).

## Writing good job methods

- **Pass ids, not entities.** Arguments are serialized as JSON when enqueued, so an entity is a
  snapshot that is already stale when it runs.
- **Enqueue against interfaces.** The *declared* type is recorded, so implementations can change
  without touching queued jobs.
- **Keep signatures stable.** Methods match by name and parameter types; renaming orphans jobs
  already in the queue.
- **Instance methods returning `Task`** are what the current version supports.
- **Honour the `CancellationToken`.** It fires on shutdown, on cancellation, and on lease loss.
- **Be idempotent.** See [Delivery guarantees](delivery.md) — this is a requirement, not a
  suggestion.
