---
title: Getting started
description: Install Millrace, enqueue your first durable job, and mount the dashboard.
---

# Getting started

This page goes from an empty ASP.NET Core application to a durable job that survives a restart, plus
a dashboard to watch it. It takes about five minutes and needs no database until the last section.

## Requirements

- **.NET 10 SDK.** Millrace targets `net10.0` and nothing else.
- A database only when you want durability — the bundled in-memory provider needs nothing at all.

Consumers never need Node. The dashboard UI packages ship their bundle prebuilt; Node is only a
requirement for building *this repository*.

## Install

Everything published so far is a prerelease, so `--prerelease` is required until 1.0.

```bash
dotnet add package Millrace --prerelease
```

## Register the services

`AddMillrace` takes a builder. The only thing it strictly requires is a storage provider.

```csharp
using Millrace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMillrace(millrace => millrace.UseInMemoryStorage());

// The service your job will call — ordinary DI, registered the way you always would.
builder.Services.AddScoped<IEmailSender, EmailSender>();

var app = builder.Build();
app.Run();
```

Registering Millrace also starts a **worker pool** and a **scheduler** in this process. Both are
opt-out (`MillraceOptions.WorkerEnabled`, `MillraceOptions.SchedulerEnabled`) for the deployment
where some nodes should enqueue but never execute.

> [!NOTE]
> The in-memory provider is a real implementation of the storage contract — it passes the same
> conformance suite the SQL providers do. It is not durable across a restart, which makes it right
> for development, samples and tests, and wrong for anything else.

## Enqueue a job

Inject <xref:Millrace.IJobClient> and enqueue against an *interface*, not a concrete class:

```csharp
app.MapPost("/orders/{id}/confirm", async (string id, IJobClient jobs) =>
{
    var jobId = await jobs.EnqueueAsync<IEmailSender>(s => s.SendConfirmationAsync(id));
    return Results.Ok(new { enqueued = jobId });
});
```

What happens to that expression is worth understanding, because it explains most of the rules that
follow. Millrace captures the **declared type**, the **method**, and the **serialized arguments** —
then throws the expression away. At execution time, possibly minutes later and possibly on a
different machine, it resolves `IEmailSender` from a fresh DI scope and invokes the method with the
deserialized arguments.

Two consequences:

- **Pass ids, not entities.** The arguments are serialized as JSON. A whole `Order` object is a
  snapshot that will be stale by the time it runs — and may not round-trip at all.
- **Keep job signatures stable.** Methods are matched by name and parameter types. Renaming a method
  that has jobs in the queue orphans them.

Because the declared type is what gets recorded, the implementation behind `IEmailSender` can change
freely without touching jobs already enqueued.

## See it run

Start the application and post to the endpoint. The job is written to storage, a worker claims it,
and your `EmailSender` runs — in a DI scope of its own, with a `CancellationToken` that fires if the
node is shutting down or the lease is lost.

## Add the dashboard

The dashboard is two packages: the API contract, and a UI to render it. Both are opt-in.

```bash
dotnet add package Millrace.Dashboard --prerelease
dotnet add package Millrace.Dashboard.Ui.React --prerelease
```

```csharp
builder.Services.AddMillraceDashboard();
builder.Services.AddMillraceReactUi();   // or AddMillraceAngularUi() / AddMillraceBlazorUi()

var app = builder.Build();

app.MapMillraceDashboard("/millrace");
```

Then open `/millrace/ui`.

> [!WARNING]
> **Outside Development, this is a startup error until you register an authorization hook.** That is
> deliberate: an operations dashboard exposes every job argument you have ever enqueued, and failing
> closed at startup is the only default that cannot be forgotten into production. See
> [Dashboard](dashboard.md#authorization).

## Make it durable

One connection string is the entire difference between the in-memory story and the durable one:

```bash
dotnet add package Millrace.Storage.PostgreSql --prerelease
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UsePostgreSqlStorage(connectionString));
```

The schema is created and upgraded on startup. Now stop the process mid-run and start it again: work
that had not finished is still there, and gets claimed and executed. Nothing else in your code
changes.

## What to read next

- **[Delivery guarantees](delivery.md)** — at-least-once execution and what it demands of your
  handlers. Read this before you ship anything.
- **[Jobs](jobs.md)** — scheduling, retries, cron, continuations, batches, queues and priority.
- **[Workflows](workflows.md)** — when a chain of jobs stops being enough.
- **[Testing your jobs](testing.md)** — how to assert on all of this without sleeping in a test.

## A complete example

The repository ships a runnable sample covering every job shape, a workflow with a durable signal
wait, and the dashboard — in one process with no external dependencies:

```bash
git clone https://github.com/inferenceailab/Millrace.git
cd Millrace
dotnet run --project samples/Millrace.Sample.Api
```

Then open <http://localhost:5000>. Set `MILLRACE_POSTGRES` to run the same sample durably.
