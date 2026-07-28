---
title: Workflows
description: Code-first durable workflows — branches, parallel fan-out, sagas with compensation, signals and timers.
---

# Workflows

A workflow is a graph of activities that survives restarts, deploys and machine failures. Each
activity runs as an ordinary [durable job](jobs.md), so it inherits retries, backoff, leases,
distribution and dashboard visibility rather than reimplementing them.

Reach for a workflow when a chain of continuations stops being enough — when you need a branch, a
parallel fan-out, a wait for something external, or the ability to undo committed work.

## Checkpointed, not replayed

This is the design decision that shapes everything else.

Some workflow engines re-execute your workflow function from the top after every step, relying on a
cache to skip work already done. That approach demands **determinism**: no `DateTime.Now`, no
`Guid.NewGuid()`, no random ordering, no calling anything that might answer differently.

Millrace does not do that. It persists a **cursor** — where the instance is in the graph — and
advances it one checkpoint at a time. Your activity code runs once per attempt.

> [!NOTE]
> **There are no determinism constraints on activity code.** Use the clock, generate ids, read
> whatever you like. Activities are ordinary classes with ordinary constructor DI.

The tradeoff is that state must live in the workflow's **data document** rather than in local
variables, because local variables belong to a job that has ended by the time the next activity
starts.

## A first workflow

Three pieces: a data document, some activities, and a definition.

```csharp
using Millrace.Workflows;

// 1. The document. Plain, serializable, mutable.
public sealed class OnboardingData
{
    public string CustomerId { get; set; } = "";
    public bool NeedsApproval { get; set; }
    public bool Approved { get; set; }
}

// 2. An activity. Ordinary code, constructor DI.
public sealed class CreateAccount(IAccounts accounts) : IActivity<OnboardingData>
{
    public async Task ExecuteAsync(ActivityContext<OnboardingData> context, CancellationToken ct)
    {
        await accounts.CreateAsync(context.Data.CustomerId, ct);
    }
}

// 3. The definition.
public sealed class OnboardingWorkflow : IWorkflow<OnboardingData>
{
    public string Id => "onboarding";
    public int Version => 1;

    public void Build(IWorkflowBuilder<OnboardingData> flow) => flow
        .StartWith<CreateAccount>()
        .If(
            d => d.NeedsApproval,
            approval => approval.WaitForSignal<ApprovalDecision>(
                "approval",
                d => d.CustomerId,
                (d, decision) => d.Approved = decision.IsApproved,
                timeout: TimeSpan.FromDays(3)))
        .Then<SendWelcome>();
}
```

Register it, then start instances:

```csharp
builder.Services.AddMillrace(millrace =>
{
    millrace.UsePostgreSqlStorage(connectionString);
    millrace.AddWorkflow<OnboardingWorkflow>();
});
```

```csharp
var instanceId = await workflows.StartAsync("onboarding", new OnboardingData { CustomerId = "c-1" });
```

The instance record and its first activity job are created **together**, so an instance can never
exist with nothing scheduled to advance it.

## Recording results

An activity has no return value. It records what it did by mutating
<xref:Millrace.Workflows.ActivityContext`1.Data>, because the document is what gets checkpointed —
anything an activity keeps elsewhere is gone when the job ends.

```csharp
public Task ExecuteAsync(ActivityContext<OrderData> context, CancellationToken ct)
{
    context.Data.InvoiceId = invoice.Id;   // persisted with the checkpoint
    return Task.CompletedTask;
}
```

The context also carries `InstanceId`, `NodeId`, `DefinitionId`, `Version` and — inside a `ForEach` —
`LoopIndex`. Logging `InstanceId` is what connects an activity's own diagnostics to the instance an
operator is looking at in the dashboard.

## Building the graph

Every builder method appends to the current sequence and returns the same builder, so a definition
reads top to bottom.

### Sequence

```csharp
flow.StartWith<ValidateOrder>()
    .Then<ChargeCard>()
    .Then<ShipOrder>();
```

`StartWith` and `Then` are identical — the first exists so a definition can open with the word that
reads correctly.

### Branch

```csharp
flow.If(
    d => d.Total > 1000,
    then => then.Then<RequireManagerApproval>(),
    otherwise => otherwise.Then<AutoApprove>());
```

> [!WARNING]
> **The predicate must be pure.** It is an `Expression`, evaluated at execution time against the
> persisted document, and may be re-evaluated after a rehydration. It must depend on nothing but the
> document. Taking an expression rather than a delegate is also what lets the exported graph shape
> show the condition instead of an opaque box.

### Parallel

```csharp
flow.Parallel(
    branch => branch.Then<ReserveInventory>(),
    branch => branch.Then<ChargeCard>(),
    branch => branch.Then<NotifyWarehouse>());
```

Branches run concurrently, each as its own chain of jobs, and the workflow continues once all
complete.

> [!IMPORTANT]
> **Branches should write disjoint regions of the document.** Each branch checkpoint merges under
> optimistic concurrency; a conflict retries the *merge*, not the activity. Two branches writing the
> same field is a lost update waiting to happen.

### Loop

```csharp
flow.ForEach(d => d.LineItems, body => body.Then<ShipLineItem>());
```

The body sees `context.LoopIndex` rather than the item, because the item is not separate state — it
lives in the document, which is the only thing checkpointed. Index the same collection the loop
selected.

### Durable delay

```csharp
flow.Delay(TimeSpan.FromDays(7));
```

A scheduled job carrying a resume token, not a timer in memory. Seven days later means seven days
later, across any number of deploys.

## Signals

A workflow can park until something outside it happens — an approval, a webhook, a human.

```csharp
flow.WaitForSignal<ApprovalDecision>(
    name: "approval",
    correlate: d => d.CustomerId,
    bind: (d, decision) => d.Approved = decision.IsApproved,
    timeout: TimeSpan.FromDays(3));
```

Send one from anywhere in your application:

```csharp
await workflows.SignalAsync("approval", customerId, new ApprovalDecision(IsApproved: true));
```

…or from outside the process entirely, over the dashboard's REST contract:

```
POST /millrace/api/v1/signals/approval/{customerId}
```

Four things worth knowing:

- **A waiting instance holds no job at all** — only a bookmark. A workflow parked for a month costs
  one row and nothing else. There is no thread, no timer and no polling.
- **Delivery is at-most-once.** The bookmark is consumed atomically, so two concurrent senders cannot
  both resume the same wait — the second gets `false` back.
- **The payload type is declared by the definition**, so a shape mismatch is a compile-time error for
  in-process senders. The wire format is plain JSON, which keeps webhooks and cross-language senders
  possible.
- **`timeout` is optional.** When it elapses, the wait gives up and the sequence continues — so
  design the following step to cope with the signal never having arrived.

## Sagas and compensation

A saga is a sequence whose completed steps are **undone in reverse order** if a later one fails.

```csharp
flow.Saga(saga => saga
    .Then<ReserveInventory>().CompensateWith<ReleaseInventory>()
    .Then<ChargeCard>().CompensateWith<RefundCard>()
    .Then<BookCourier>().CompensateWith<CancelCourier>());
```

If `BookCourier` fails past its retries, `RefundCard` runs and then `ReleaseInventory` runs.

> [!IMPORTANT]
> **Compensation is triggered by exhausted retries, not by the first exception.** A step that fails
> transiently and then succeeds has not failed, and unwinding it would be wrong.

Each compensation runs as its own durable job with its own retry policy.

### When a step fails, and unwinding is not what you want

`OnFailure` annotates the step just appended.

```csharp
flow.Saga(saga => saga
    .Then<ReserveInventory>().CompensateWith<ReleaseInventory>()
    .Then<ChargeCard>().CompensateWith<RefundCard>()
    .Then<NotifyCustomer>().OnFailure(StepFailurePolicy.Terminate));
```

| Policy | What exhausting retries means |
|---|---|
| `Retry` | The retry policy was the whole answer; exhausting it unwinds the saga. **Default.** |
| `Compensate` | Unwind immediately — identical to `Retry` once retries are spent. |
| `Suspend` | Park the instance for an operator, undoing nothing. For a step where unwinding is the wrong reflex — a partial refund, an external system that cannot be un-called. |
| `Terminate` | Fail the instance, skipping the unwind. For a step whose failure means the earlier work should **stand**. |

These decide what *exhaustion* means, not whether to retry — by the time any of them is consulted,
the job has already spent its retry budget.

### When a compensation itself fails

The instance is left **`Suspended`**, not forced to a terminal state. A half-undone saga is exactly
the case where an operator should look before anything else happens.

The way out is <xref:Millrace.Workflows.IWorkflowClient.RecoverCompensationAsync*>, also exposed as a
dashboard button:

| Action | Meaning |
|---|---|
| `Retry` | Run the failed compensation again. The answer when the cause was transient. |
| `Skip` | Treat this step as undone and carry on unwinding. Records a *decision*, not a fact — which is why it is never automatic. |
| `Abandon` | Stop unwinding and fail the instance, leaving the remaining steps done. Terminal, deliberately. |

The engine cannot choose between these, because which is right depends on what the compensation was
undoing and whether it is now safe to try again — neither of which the engine knows.

### Nested sagas

A saga can contain another saga, and the nesting requires you to answer a question that has no safe
default:

```csharp
flow.Saga(outer => outer
    .Then<ReserveInventory>().CompensateWith<ReleaseInventory>()
    .Saga(inner => inner
        .Then<ChargePartner>().CompensateWith<RefundPartner>(),
        NestedSagaPolicy.Unwind)
    .Then<BookCourier>().CompensateWith<CancelCourier>());
```

A failure *inside* the nested saga unwinds the nested saga first, completely, and only then reports
outward — so the outer saga's compensations run against the state its own steps actually left
behind.

`policy` answers the other direction: the inner saga **committed**, the outer failed later, and
either the inner work is undone with it or it stands.

| Policy | Meaning |
|---|---|
| `Unwind` | An outer unwind undoes this saga too, innermost step first. |
| `Keep` | Once this saga commits it stands; an outer unwind undoes only its own direct steps. |

There is no default, on purpose. `Keep` means the outer saga can no longer promise all-or-nothing
across the nested part — a real cost, and sometimes the correct one. An inner saga that took payment
through a provider with no reversal, or that notified a third party, is *final*, and replaying its
compensations would be a second wrong rather than an undo.

## Versioning

`(Id, Version)` is the key. **In-flight instances always finish on the version they started with**,
so old versions stay registered until their instances drain.

```csharp
public string Id => "onboarding";
public int Version => 2;      // bump whenever the graph's shape changes
```

> [!WARNING]
> Editing a graph in place under the same version leaves in-flight instances resuming into a shape
> their stored cursor no longer describes — and that failure surfaces long after the deploy that
> caused it.

To pin a new instance to a specific version during a rollout:

```csharp
await workflows.StartAsync("onboarding", version: 1, data);
```

`Build` is called once at registration, not per instance, and the graph is compiled and validated
there — so a malformed graph fails at **startup** rather than when an instance first reaches the bad
part.

## The queue activities run on

Workflow activity jobs are enqueued to <xref:Millrace.MillraceOptions.WorkflowQueue>, which defaults
to `"default"`.

> [!WARNING]
> Every node in a deployment must agree on this value, and **at least one must claim from it**. It is
> explicit rather than derived from a node's own queue list precisely because a node configured to
> claim only `reports` would otherwise enqueue activities somewhere nothing claims from — and the
> instance would hang with no error anywhere.

## Failure and idempotency

Activities inherit [at-least-once execution](delivery.md). An activity may run twice if a worker dies
after doing the work and before the checkpoint commits, so **side effects should be idempotent** —
the same requirement as any job, and the reason compensations should be idempotent too.

What the checkpoint guarantees is that progress is not lost or double-counted: a crash mid-workflow
re-runs at most one activity, never the whole graph.
