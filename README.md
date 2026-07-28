# Millrace

> **Status: 1.0.** Jobs, workflows, sagas, both SQL providers, the dashboard and its three official
> UIs are built, tested and published. **The storage contract and the v1 REST contract are now
> stable**: they will not break within 1.x. That promise is the whole meaning of this version
> number, which stayed at `0.1.0-alpha` until it could be made honestly. Start with the
> [documentation](https://inferenceailab.github.io/Millrace/), or with
> [ARCHITECTURE.md](ARCHITECTURE.md) if you would rather read the design.

**Millrace** is a durable job and workflow orchestration library for .NET — in-process,
storage-agnostic, dashboard-included. The mental model: *Hangfire's substrate with a real
orchestration layer on top.* A millrace is the engineered channel that carries water to the wheel:
directed, sustained flow that does work.

- **Durable jobs** — persistent queues, retries with backoff, cron, delayed jobs, continuations. `jobs.EnqueueAsync<IEmailSender>(s => s.SendAsync(orderId))`.
- **Workflows** — code-first fluent graphs with parallel branches, sagas + compensation, strongly typed signals, durable timers. Every activity runs as a durable job and inherits retries, distribution, and observability.
- **Storage as a plugin** — the core has zero database dependencies; pick a provider package (PostgreSQL first) or write your own and verify it with the shipped conformance kit.
- **Ops dashboard** — mounted as middleware over a versioned REST + OpenAPI contract, with official React, Angular, and Blazor UIs.
- **Modern .NET only, zero framework lock-in** — `net10.0`, System.Text.Json, TimeProvider, OpenTelemetry-native. No application-framework dependencies (no ABP, no MassTransit): just the BCL and `Microsoft.Extensions.*`, with multi-tenancy and authorization hooks built in natively. MIT licensed.

## Where it stands

| Phase | Scope | State |
|---|---|---|
| 0.1 | Storage contract, InMemory provider, job substrate, conformance kit | **done** |
| 0.2 | PostgreSQL provider · dashboard REST API + OpenAPI · React UI (read-only) | **done** |
| 0.3 | Workflow engine core | **done** |
| 0.4 | Sagas, compensation, versioning, management actions | **done** |
| 0.5 | OpenTelemetry, SQL Server provider, batch enqueue, `Millrace.Testing`, Angular UI | **done** |
| 1.0 | Hardening, Blazor UI, docs site, benchmarks, stable contracts | **done** |

Full documentation — guides and the generated API reference — is at
**<https://inferenceailab.github.io/Millrace/>**; its source is [docs/site](docs/site).

Planned work lives on the [project board](https://github.com/users/inferenceailab/projects/1),
mirrored in [docs/backlog.md](docs/backlog.md). Unsettled design questions are tracked in
[docs/open-questions.md](docs/open-questions.md).

Measured against Hangfire and WorkflowCore in [docs/benchmarks.md](docs/benchmarks.md) — method,
caveats and a harness you can run yourself.

## Install

```bash
dotnet add package Millrace
dotnet add package Millrace.Storage.PostgreSql
```

```csharp
builder.Services.AddMillrace(millrace => millrace.UsePostgreSqlStorage(connectionString));
```

That is a working job substrate: enqueue, retries, cron, continuations, workflows. The dashboard is
opt-in, and a UI is a separate package again — mount the API without one if you would rather build
your own client against the REST contract.

```csharp
builder.Services.AddMillraceDashboard();
builder.Services.AddMillraceReactUi();   // or AddMillraceAngularUi() / AddMillraceBlazorUi()

app.MapMillraceDashboard("/millrace");
```

| Package | For |
|---|---|
| `Millrace` | The library. Jobs, workflows, the in-memory provider. No database dependencies. |
| `Millrace.Storage.PostgreSql` | PostgreSQL provider — `SKIP LOCKED` claims and `LISTEN/NOTIFY` wakeups. |
| `Millrace.Storage.SqlServer` | SQL Server provider. No push channel, so workers poll. |
| `Millrace.Dashboard` | The REST + OpenAPI contract and middleware. No UI of its own. |
| `Millrace.Dashboard.Ui.React` | Prebuilt React bundle, embedded. |
| `Millrace.Dashboard.Ui.Angular` | Prebuilt Angular bundle, embedded. |
| `Millrace.Dashboard.Ui.Blazor` | Blazor WebAssembly UI, embedded. Carries the .NET WASM runtime, so ~6.4 MB against ~150–210 KB for the other two. |
| `Millrace.Testing` | Deterministic test host — see below. |
| `Millrace.Storage.Verification` | Conformance kit, for writing your own provider. |

The UI packages ship their bundle prebuilt, so installing one never requires Node, npm or a CDN.

## Try it

```bash
dotnet run --project samples/Millrace.Sample.Api
```

Then open <http://localhost:5000>. No database, no broker, no sidecar — the bundled in-memory
provider carries jobs, a workflow and the dashboard in one process. Add one connection string to
run the same sample on PostgreSQL and watch jobs survive a restart. See
[samples/README.md](samples/README.md).

## Testing your jobs

`Millrace.Testing` runs work deterministically, so a test never polls or sleeps:

```csharp
await using var millrace = MillraceTestHost.Create(
    services => services.AddScoped<IEmailSender, EmailSender>());

await millrace.Jobs.EnqueueAsync<IEmailSender>(s => s.SendAsync(orderId));
await millrace.RunUntilIdleAsync();

Assert.True(sent);
```

Delays, retry backoff and signal timeouts are reached with `AdvanceTime` rather than waiting — a
seven-day timeout is one call.

## Repository layout

```
src/      library packages (Millrace, Millrace.Storage.*, Millrace.Dashboard*, ...)
test/     unit and integration tests
samples/  runnable examples
bench/    benchmarks vs Hangfire and WorkflowCore (docs/benchmarks.md)
docs/     documentation
```

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK, and **Node 22.22.3+ or 24.15.0+** (the Angular CLI's floor) — the React and Angular UI packages compile their embedded
bundle during the .NET build. Consumers of the published package never need Node; that is the point
of shipping the bundle prebuilt. To build the C# alone, pass `-p:SkipUiBuild=true` (the resulting
package serves no UI).

The PostgreSQL conformance run needs Docker (Testcontainers). It is strict in CI: an unreachable
database fails the run rather than skipping it.

The UI rendering tests need a browser, and follow the same rule. Install one once:

```
pwsh test/Millrace.Dashboard.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
```

Without it they skip locally and fail in CI. **Run them before touching a UI** — they are the only
check in the repository that executes a bundle rather than reading it.

## License

[MIT](LICENSE)
