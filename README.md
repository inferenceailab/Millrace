# Weft

> **Status: early design / pre-alpha.** Nothing here is usable yet — start with [ARCHITECTURE.md](ARCHITECTURE.md).

**Weft** is a durable job and workflow orchestration library for .NET — in-process, storage-agnostic, dashboard-included. The mental model: *Hangfire's substrate with a real orchestration layer on top.*

- **Durable jobs** — persistent queues, retries with backoff, cron, delayed jobs, continuations. `jobs.EnqueueAsync<IEmailSender>(s => s.SendAsync(orderId))`.
- **Workflows** — code-first fluent graphs with parallel branches, sagas + compensation, strongly typed signals, durable timers. Every activity runs as a durable job and inherits retries, distribution, and observability.
- **Storage as a plugin** — the core has zero database dependencies; pick a provider package (PostgreSQL first) or write your own and verify it with the shipped conformance kit.
- **Ops dashboard** — mounted as middleware over a versioned REST + OpenAPI contract, with official React, Angular, and Blazor UIs.
- **Modern .NET only, zero framework lock-in** — `net10.0`, System.Text.Json, TimeProvider, OpenTelemetry-native. No application-framework dependencies (no ABP, no MassTransit): just the BCL and `Microsoft.Extensions.*`, with multi-tenancy and authorization hooks built in natively. MIT licensed.

## Repository layout

```
src/      library packages (Weft, Weft.Storage.*, Weft.Dashboard*, ...)
test/     unit and integration tests
samples/  runnable examples
docs/     documentation
```

## Building

```
dotnet build
dotnet test
```

Requires the .NET 10 SDK.

## License

[MIT](LICENSE)
