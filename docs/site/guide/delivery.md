---
title: Delivery guarantees
description: At-least-once execution, exactly-once state transitions, and how to write handlers that can survive both.
---

# Delivery guarantees

Millrace makes two promises, and the difference between them is the most important thing to
understand about it.

> **Execution is at-least-once. State transitions are exactly-once.**

Everything on this page follows from that sentence.

## Why execution is at-least-once

A worker claims a job, runs your code, and records the outcome. Those are two separate things, and a
process can die between them.

Consider a worker that charges a credit card and then loses power before it can write `Succeeded`.
The charge happened. Millrace has no record that it happened. The lease expires, another worker
claims the job, and it charges the card again.

There is no way to make this not happen. Committing "the work is done" and doing the work are
transactions in two different systems — your payment provider and your database — and no amount of
engineering inside Millrace can make an arbitrary side effect atomic with a row update. Systems that
claim exactly-once execution either control both sides, or are quietly describing something narrower.

So Millrace states the guarantee it can actually keep, and gives you the tools to work with it.

> [!IMPORTANT]
> **Your job handlers must be idempotent.** Running one twice with the same arguments must be safe.
> This is not defensive style — it is a correctness requirement of the system you are using.

## What *is* exactly-once

The job's **lifecycle** is linear, even with dozens of workers on many nodes racing for the same
queue:

- **A claim is exclusive.** Two concurrent claims never return the same job. Relational providers use
  native queue semantics (`FOR UPDATE SKIP LOCKED`, `READPAST`) rather than advisory locking.
- **A transition is all-or-nothing.** The state change and its side effects — enqueuing a
  continuation, checkpointing a workflow instance — commit together or not at all.
- **A job never moves backwards.** `Succeeded` is terminal. There is no path back to `Processing`.
- **A signal resumes exactly one instance**, under arbitrary concurrency.
- **A due recurring job fires on exactly one node**, fenced by compare-and-set on its next fire time —
  so no leader election is needed, and two nodes waking at the same instant produce one occurrence.

These are not implementation details of one provider. They are the storage contract, and the
[conformance kit](writing-a-provider.md) enforces every one of them executably.

## Writing an idempotent handler

Three approaches, in rough order of preference.

### 1. Make the operation naturally idempotent

The best option, when the shape of the work allows it. Setting a value is idempotent; incrementing
one is not.

```csharp
// Idempotent: running twice leaves the same state.
order.Status = OrderStatus.Confirmed;

// Not idempotent: running twice charges twice.
account.Balance -= amount;
```

### 2. Use the downstream system's idempotency

Most payment providers, mail services and message APIs accept an idempotency key of their own. Pass
one derived from something stable in the job's arguments — never from `Guid.NewGuid()` or the
current time, which differ between the first attempt and the retry.

```csharp
public async Task CaptureAsync(string paymentId)
{
    await _payments.CaptureAsync(paymentId, idempotencyKey: $"capture:{paymentId}");
}
```

### 3. Record what you did, and check first

When neither of the above is available, make the check and the work one transaction in your own
database.

```csharp
public async Task SendInvoiceAsync(string orderId)
{
    if (await _db.InvoicesSent.AnyAsync(x => x.OrderId == orderId))
    {
        return;
    }

    await _mail.SendAsync(orderId);
    _db.InvoicesSent.Add(new InvoiceSent(orderId));
    await _db.SaveChangesAsync();
}
```

Note that this narrows the window rather than closing it — a crash between `SendAsync` and
`SaveChangesAsync` still sends twice. That is the residual risk of a side effect outside your
transaction, and naming it is more useful than pretending the guard removed it.

## Enqueue-time idempotency keys

The problem above is *execution* running twice. A different problem is *enqueue* being called twice —
a retried HTTP request, a webhook delivered again, a user double-clicking.

<xref:Millrace.EnqueueOptions.IdempotencyKey> handles that one:

```csharp
await jobs.EnqueueAsync<IPayments>(p => p.CaptureAsync(paymentId),
    new EnqueueOptions { IdempotencyKey = $"capture:{paymentId}" });
```

An enqueue whose key matches an **active** job is a no-op that returns the existing job's id. The
second caller gets the first caller's job back, and one job runs.

Three details that matter:

- **The scope is `(TenantId, IdempotencyKey)` among active jobs.** A null tenant is its own scope, so
  two tenants can use the same key without colliding.
- **The key is released on a terminal transition** — it stops constraining uniqueness once the job
  reaches `Succeeded`, `Dead` or `Cancelled`. The field itself is never cleared, so the record still
  shows what key the job carried. This is what makes "capture payment 123" enqueueable again next
  month.
- **Not supported on recurring jobs** in the current version.

> [!NOTE]
> An idempotency key does **not** make execution exactly-once. It deduplicates *enqueues*. A single
> job admitted by a key can still run twice if a worker crashes mid-flight — which is why the
> handler still has to be idempotent.

## Retries are the other source of duplicates

A job that throws is retried according to its <xref:Millrace.Retry> policy — five attempts with
exponential backoff, by default. Every retry is another execution of your handler, so the same
idempotency requirement applies to a handler that failed *partway through*.

This is the case people forget: the job did not merely run twice, it ran once *incompletely* and
then again from the top. A handler that writes three records and throws on the fourth will, on
retry, try to write the first three again.

When retries are exhausted the job is **dead-lettered** rather than deleted. It sits in `Dead`
where the [dashboard](dashboard.md) can show it, and an operator can requeue it once the cause is
fixed.

## Cancellation is cooperative and best-effort

Every job receives a `CancellationToken`. It fires when the node is shutting down, when the job is
cancelled through the dashboard, and — importantly — when the **lease is lost**.

Losing a lease means another worker may already have claimed the job. Millrace signals the token as
a best-effort attempt to stop the first execution before it duplicates work, but it cannot guarantee
your code notices in time. Honour the token where you can; do not rely on it for correctness.

## Poison jobs

A job that crashes the *worker process* — rather than throwing an exception your handler could
record — would otherwise retry forever, taking down a node each time.

Millrace detects this by comparing attempts against recorded failures. When a claim finds that a job
has been attempted far more often than it has failed, the job has been repeatedly interrupted
without ever reaching a failure handler, and it is dead-lettered **without executing**. The threshold
is <xref:Millrace.MillraceOptions.InterruptionLimit>, default 10.

## Workflows: the same rules, one level up

A [workflow](workflows.md) does not change the guarantee — it decomposes it.

- **The activity is the unit of at-least-once.** Each activity runs as an ordinary durable job, so an
  individual activity can execute twice for exactly the reasons above. Activities must be
  idempotent.
- **The checkpoint is the unit of exactly-once progress.** The instance's cursor advances in the
  *same atom* as the activity job's state transition. So a crash cannot advance the workflow past
  work that did not commit, and cannot lose progress that did.

The practical consequence: a crash mid-workflow re-runs at most one activity, never the whole
graph. Millrace checkpoints rather than replays — your activity code is invoked once per attempt,
not repeatedly re-executed to rebuild state, and it is free to be non-deterministic.

## Summary

| | Guarantee |
|---|---|
| Job execution | **At least once.** Make handlers idempotent. |
| Job state transitions | Exactly once, linear, under any concurrency. |
| Enqueue with an idempotency key | At most one active job per key, per tenant. |
| Signal delivery | Resumes exactly one waiting instance. |
| Recurring fire | Exactly one node per due occurrence. |
| Workflow progress | Checkpointed exactly once; activities at least once. |
