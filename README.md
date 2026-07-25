# Millrace

> **Status: pre-alpha.** The storage contract, job substrate and PostgreSQL provider are built and
> green; the workflow engine and dashboard are not. Start with [ARCHITECTURE.md](ARCHITECTURE.md).

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
| 0.3 | Workflow engine core | not started |
| 0.4 | Sagas, compensation, versioning, management actions | not started |
| 0.5 | OpenTelemetry, SQL Server provider, batch enqueue, `Millrace.Testing`, Angular UI | not started |
| 1.0 | Hardening, Blazor UI, docs site, benchmarks | not started |

Planned work lives on the [project board](https://github.com/users/inferenceailab/projects/1),
mirrored in [docs/backlog.md](docs/backlog.md). Unsettled design questions are tracked in
[docs/open-questions.md](docs/open-questions.md).

## Repository layout

```
src/      library packages (Millrace, Millrace.Storage.*, Millrace.Dashboard*, ...)
test/     unit and integration tests
samples/  runnable examples
docs/     documentation
```

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK, and **Node 20+** — `Millrace.Dashboard.Ui.React` compiles its embedded
bundle during the .NET build. Consumers of the published package never need Node; that is the point
of shipping the bundle prebuilt. To build the C# alone, pass `-p:SkipUiBuild=true` (the resulting
package serves no UI).

The PostgreSQL conformance run needs Docker (Testcontainers). It is strict in CI: an unreachable
database fails the run rather than skipping it.

## License

[MIT](LICENSE)
