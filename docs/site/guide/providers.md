---
title: Storage providers
description: Choosing and configuring a Millrace storage provider — in-memory, SQLite, PostgreSQL, SQL Server.
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
| SQLite | `Millrace.Storage.Sqlite` | Durability without a server: single-node deployments, development, tests that outlive a restart. |
| PostgreSQL | `Millrace.Storage.PostgreSql` | Production. The best queue semantics of the four. |
| SQL Server | `Millrace.Storage.SqlServer` | Production, where SQL Server is what you already run. |

All four pass the same conformance suite. The differences below are about performance and
operational shape, not correctness.

## In-memory

```csharp
builder.Services.AddMillrace(millrace => millrace.UseInMemoryStorage());
```

Included in the core package. It is a real implementation — it passes the same conformance suite the
SQL providers do — and it holds everything in process memory, so a restart loses the lot.

Right for: development, the sample, unit and integration tests (though
[`Millrace.Testing`](testing.md) is better for those). Wrong for anything you care about.

## SQLite

```bash
dotnet add package Millrace.Storage.Sqlite
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UseSqliteStorage("Data Source=millrace.db"));
```

The gap between in-memory and running a database server. Jobs survive a restart, there is nothing to
deploy or operate, and the whole store is one file you can copy, back up or delete.

**The trade is concurrency.** SQLite has one writer at a time and no row locks, so claims cannot step
over each other the way `SKIP LOCKED` and `READPAST` do — instead every write path takes the writer
lock up front and the second claimer *waits*. That is still exclusive, which is all the contract asks,
and it is why the SQLite provider passes the same suite. But throughput does not improve by adding
workers the way it does on PostgreSQL, and past a certain `MaxParallelism` they simply queue.

Right for: single-node deployments, desktop and edge applications, CI, and development where you want
a restart to keep its jobs. Wrong for: several application nodes sharing one store, or any workload
where write contention is the bottleneck. That is the point to move to PostgreSQL — the storage
contract is the same, so it is a registration change.

### Options

```csharp
millrace.UseSqliteStorage("Data Source=millrace.db", options =>
{
    options.AutoCreateSchema = true;                    // default
    options.UseWriteAheadLog = true;                    // default
    options.BusyTimeout = TimeSpan.FromSeconds(30);     // default
});
```

`UseWriteAheadLog` lets reads run alongside the single writer, which is what keeps the dashboard from
queueing behind a claim. Turn it off only where WAL cannot work — it needs shared memory, so some
network file systems refuse it.

`BusyTimeout` is how long a connection waits for the writer lock before failing. It is a contention
budget rather than a statement timeout: every write here is a short transaction, so waiting is almost
always better than surfacing an error to a worker that would just retry. If claims start timing out,
that is the signal to move to a server-backed provider rather than to raise it further.

### In-memory databases

`Data Source=:memory:` works, and the provider holds one connection open for its lifetime so the
database survives between operations. It is durable in the sense that nothing is lost while the
process lives, and lost entirely when it exits — so it is a curiosity next to
[`Millrace.Testing`](testing.md), which is what you actually want for tests.

### Wakeups are in-process

The provider advertises the notification capability, but the channel is in-process: SQLite has no
cross-process notification mechanism. One application sees pushed wakeups; a second process sharing
the same file falls back to its poll interval. That is a **latency** difference and nothing more —
notifications are best-effort by contract, and a worker's liveness rests on the poll ceiling rather
than on any signal arriving.

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

All three durable providers create and upgrade their schema on startup when `AutoCreateSchema` is
left on. Upgrades are **idempotent** — safe to run from every node in a deployment simultaneously, and
safe to run against a database that is already current.

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

Four providers ship, and the fourth was the interesting one: SQLite has no `SKIP LOCKED`, one writer
and no server, so it was the cheapest test of whether the storage contract frozen in 1.0 could still
be implemented by something shaped differently. It could, unchanged.

Community providers — Mongo, Redis, others — became viable the day the conformance kit shipped: the
bar is public, executable, and the same one the official providers clear.
