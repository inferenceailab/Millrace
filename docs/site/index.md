---
title: Millrace — durable jobs and workflows for .NET
description: In-process, storage-agnostic durable job and workflow orchestration for .NET, with a dashboard included.
---

# Millrace

**Durable job and workflow orchestration for .NET** — in-process, storage-agnostic,
dashboard-included. The mental model: *Hangfire's substrate with a real orchestration layer on top.*

A millrace is the engineered channel that carries water to the wheel: directed, sustained flow that
does work.

> [!NOTE]
> **Millrace is 1.0.** The storage contract and the v1 REST contract are **stable** — they will not
> break within 1.x. That promise is the entire meaning of this version number, which stayed at
> `0.1.0-alpha` until it could be made honestly.

```bash
dotnet add package Millrace
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UseInMemoryStorage());

// ...then, anywhere you have an IJobClient:
await jobs.EnqueueAsync<IEmailSender>(s => s.SendConfirmationAsync(orderId));
```

That is a working durable job substrate. [**Getting started**](guide/getting-started.md) takes it
from there.

## The one thing to read before you ship

Millrace executes jobs **at least once**. A job can run twice — a worker that finishes the work and
crashes before recording the outcome has done the work, and the lease expires and it runs again.
This is not a defect to be fixed later; it is the honest consequence of running arbitrary code
against storage that cannot enlist in your side effects.

So **your handlers must be idempotent**, and Millrace gives you the tools to make them so:

```csharp
await jobs.EnqueueAsync<IPayments>(p => p.CaptureAsync(paymentId),
    new EnqueueOptions { IdempotencyKey = $"capture:{paymentId}" });
```

What Millrace *does* guarantee exactly once is the **state transition**: a job's lifecycle is
linear even under concurrent workers on many nodes, because the claim and the transition are one
atom that storage enforces. Two workers never run the same claim; a job never goes from `Succeeded`
back to `Processing`.

That distinction — at-least-once *execution*, exactly-once *transitions* — governs how you write
every handler you will ever give it. It has [its own page](guide/delivery.md), and it is worth the
ten minutes.

## What it does

| | |
|---|---|
| **[Durable jobs](guide/jobs.md)** | Persistent queues, retries with backoff, cron schedules, delayed jobs, continuations, batches. `jobs.EnqueueAsync<IEmailSender>(s => s.SendAsync(orderId))`. |
| **[Workflows](guide/workflows.md)** | Code-first fluent graphs — parallel branches, sagas with compensation, strongly typed signals, durable timers. Every activity runs as a durable job and inherits retries, distribution and observability. |
| **[Storage as a plugin](guide/providers.md)** | The core has zero database dependencies. Pick a provider package, or [write your own](guide/writing-a-provider.md) and verify it against the shipped conformance kit. |
| **[Ops dashboard](guide/dashboard.md)** | Middleware over a versioned REST + OpenAPI contract, with official React, Angular and Blazor UIs. |
| **[Deterministic testing](guide/testing.md)** | `Millrace.Testing` runs work to completion without polling or sleeping. A seven-day timeout is one call. |

## Design commitments

These are the constraints the library is built under, and they are the reason to choose it or not.

- **`net10.0` only.** `System.Text.Json`, `TimeProvider`, OpenTelemetry-native. No polyfills, no
  legacy target matrix.
- **No application-framework lock-in.** The core package depends on the BCL and
  `Microsoft.Extensions.*`. Nothing else — no ABP, no MassTransit, no MediatR, and no database
  client.
- **In-process.** Millrace is a library your application hosts, not a server you operate. There is
  no broker, no sidecar and no separate deployment.
- **Storage-agnostic by contract, not by adapter.** What a provider must guarantee is written down
  as an executable conformance kit rather than prose, so "supported" means "passes the suite".
- **Multi-tenancy and authorization are built in**, not bolted on. Tenant scoping reaches the
  storage contract itself.
- **MIT licensed.**

## Where it stands

| Phase | Scope | State |
|---|---|---|
| 0.1 | Storage contract, in-memory provider, job substrate, conformance kit | **done** |
| 0.2 | PostgreSQL provider · dashboard REST API + OpenAPI · React UI | **done** |
| 0.3 | Workflow engine core | **done** |
| 0.4 | Sagas, compensation, versioning, management actions | **done** |
| 0.5 | OpenTelemetry, SQL Server provider, batch enqueue, `Millrace.Testing`, Angular UI | **done** |
| 1.0 | Hardening, Blazor UI, docs site, benchmarks, stable contracts | **done** |

[**ARCHITECTURE.md**](https://github.com/inferenceailab/Millrace/blob/main/ARCHITECTURE.md) is the
accepted design, and its §11 decision log records why each significant choice was made — including
the ones that were rejected. If you are evaluating Millrace seriously, that document is more
useful than this site.

[**Benchmarks**](https://github.com/inferenceailab/Millrace/blob/main/docs/benchmarks.md) measure
Millrace against Hangfire and WorkflowCore, with the method, the caveats, and a harness you can run
yourself. The short version: 1.8× Hangfire on enqueue, 3.2–3.4× on draining, and 5.3× WorkflowCore
on workflow instances — measured on one machine, which is the first caveat of several.
