---
title: Storage providers
description: Choosing and configuring a Millrace storage provider — in-memory, PostgreSQL, SQL Server.
---

# Storage providers

Millrace's core package has **no database dependencies at all**. Storage is a contract, and a
provider is a package that implements it. Choosing one is the only decision Millrace forces on you
at registration.

If you want to implement the contract yourself, see [Writing a provider](writing-a-provider.md).

## What ships today

| Provider | Package | Use it for |
|---|---|---|
| In-memory | `Millrace` (built in) | Development, samples, tests. Not durable. |
| PostgreSQL | `Millrace.Storage.PostgreSql` | Production. The best queue semantics of the three. |
| SQL Server | `Millrace.Storage.SqlServer` | Production, where SQL Server is what you already run. |

All three pass the same conformance suite. The differences below are about performance and
operational shape, not correctness.

## In-memory

```csharp
builder.Services.AddMillrace(millrace => millrace.UseInMemoryStorage());
```

Included in the core package. It is a real implementation — it passes the same conformance suite the
SQL providers do — and it holds everything in process memory, so a restart loses the lot.

Right for: development, the sample, unit and integration tests (though
[`Millrace.Testing`](testing.md) is better for those). Wrong for anything you care about.

## PostgreSQL

```bash
dotnet add package Millrace.Storage.PostgreSql
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UsePostgreSqlStorage(connectionString));
```

The recommended provider, for two concrete reasons:

- **`FOR UPDATE SKIP LOCKED`** gives an exclusive claim without contention. Workers step over each
  other's locked rows instead of queueing behind them, so adding workers adds throughput.
- **`LISTEN/NOTIFY`** lets storage *push* a wakeup when work arrives. Workers do not have to poll to
  discover a new job, which is most of the latency difference between the two SQL providers.

### Options

```csharp
millrace.UsePostgreSqlStorage(connectionString, options =>
{
    options.Schema = "millrace";        // default
    options.AutoCreateSchema = true;    // default
});
```

### Bringing your own data source

The single-argument overload creates and owns an `NpgsqlDataSource`. An application that already
builds its own — to configure logging, type mappings or pooling — should use the factory overload,
so there is one data source rather than two:

```csharp
millrace.UsePostgreSqlStorage(sp => sp.GetRequiredService<NpgsqlDataSource>());
```

## SQL Server

```bash
dotnet add package Millrace.Storage.SqlServer
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UseSqlServerStorage(connectionString));
```

Claims use `READPAST` for the same non-blocking exclusivity as PostgreSQL's `SKIP LOCKED`.

The difference is wakeups: **SQL Server has no `LISTEN/NOTIFY`**, so the provider advertises no
notification capability and workers fall back to adaptive polling — fast while busy, backing off to
a ceiling when idle. What differs from PostgreSQL is **wakeup latency, not correctness**. Tune it
with `MinPollDelay` and `MaxPollDelay` if the default idle ceiling of 5 seconds is too slow for you.

### Options

```csharp
millrace.UseSqlServerStorage(connectionString, options =>
{
    options.Schema = "millrace";        // default
    options.AutoCreateSchema = true;    // default
});
```

## Schema management

Both SQL providers create and upgrade their schema on startup when `AutoCreateSchema` is left on.
Upgrades are **idempotent** — safe to run from every node in a deployment simultaneously, and safe to
run against a database that is already current.

Set `AutoCreateSchema = false` where your organisation requires migrations to be applied by a
separate process with elevated rights. You are then responsible for the schema being present and
current before the application starts.

## Registration is last-wins

Calling a `Use...Storage` method after another **replaces** it rather than conflicting. That is what
lets a test host override whatever the composition root configured:

```csharp
builder.Services.AddMillrace(millrace =>
{
    millrace.UsePostgreSqlStorage(connectionString);
    millrace.UseInMemoryStorage();       // wins
});
```

## What a provider has to guarantee

Worth knowing even if you never write one, because it is what makes any of the above safe:

1. **Claim is exclusive.** Two concurrent claims never return the same job.
2. **Claim sets a lease.** A claimed job is invisible to others until it expires; expired leases make
   it claimable again, which is how a crashed worker's jobs come back.
3. **Transitions are all-or-nothing.** State change plus side effects commit together or not at all.
4. **A bookmark is consumed at most once**, so a signal resumes exactly one waiting instance.
5. **Due recurring jobs are fenced**, so an occurrence fires on exactly one node.
6. **Idempotency keys are unique** among active jobs, scoped by tenant.
7. **A workflow checkpoint commits with its transition**, in the same atom.

None of this is prose the provider author is trusted to have read. It is an executable
[conformance kit](writing-a-provider.md), and "supported" means "passes the suite".

## Roadmap

SQLite is the next reference provider, aimed at development and small single-node deployments.
Community providers — Mongo, Redis, others — became viable the day the conformance kit shipped: the
bar is public, executable, and the same one the official providers clear.
