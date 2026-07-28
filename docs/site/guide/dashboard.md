---
title: Dashboard
description: Mount the Millrace operations dashboard — REST contract, OpenAPI, and the React, Angular and Blazor UIs.
---

# Dashboard

The dashboard is **middleware in your application**, not a separate process. No extra deployment, no
sidecar, no second connection string — it reads through the same storage provider your workers use.

It comes in two halves, and both are opt-in:

- **`Millrace.Dashboard`** — the versioned REST contract, the OpenAPI document, and the middleware.
  No UI of its own.
- **A UI package** — a prebuilt bundle served by that middleware. Three official ones.

Mounting the API without a UI is a supported choice: the contract is the product, and building your
own client against it is expected.

## Mounting it

```bash
dotnet add package Millrace.Dashboard --prerelease
dotnet add package Millrace.Dashboard.Ui.React --prerelease
```

```csharp
builder.Services.AddMillraceDashboard();
builder.Services.AddMillraceReactUi();

var app = builder.Build();

app.MapMillraceDashboard("/millrace");
```

| Path | What |
|---|---|
| `/millrace/ui` | The UI |
| `/millrace/api/v1/...` | The REST contract |
| `/millrace/openapi/...` | The OpenAPI document |

The prefix is yours to choose. UIs use hash routing, so one prebuilt bundle works at any mount point
without a rebuild.

## Authorization

> [!CAUTION]
> **Outside Development, `MapMillraceDashboard` throws at startup until an authorization hook is
> registered.**

This is deliberate and worth understanding rather than working around. An operations dashboard
exposes every job argument you have ever enqueued, plus buttons that cancel, requeue and re-run
work. A dashboard that defaults to open is one deploy away from being public, and nobody discovers
that from a log line. Failing closed at startup is the only default that cannot be forgotten.

Register an inline hook:

```csharp
builder.Services.AddMillraceDashboardAuthorization((context, ct) =>
    ValueTask.FromResult(context.User.IsInRole("Operations")));
```

…or a class, when the decision needs dependencies:

```csharp
builder.Services.AddMillraceDashboardAuthorization<MyDashboardAuthorization>();

public sealed class MyDashboardAuthorization(IUserService users) : IMillraceDashboardAuthorization
{
    public ValueTask<bool> AuthorizeAsync(HttpContext context, CancellationToken ct) => ...;
}
```

There is an explicit escape hatch, named so that it cannot be typed by accident or skimmed past in
review:

```csharp
builder.Services.AddMillraceDashboard(options =>
{
    options.AllowAnonymousAccessInsecure = true;
});
```

## What it shows

Six views, identical across all three UIs:

| View | Contents |
|---|---|
| **Overview** | Job counts by state, queue health, storage provider and contract version. |
| **Jobs** | Cursor-paged, filtered by state and queue. |
| **Job detail** | Arguments, attempt history, failures with stack traces, and the management actions. |
| **Recurring** | Cron definitions, next fire time, trigger-now. |
| **Instances** | Workflow instances, their state and cursor position. |
| **Signals** | Raise a signal by name and correlation id. |

## Management actions

Exposed on the relevant views, and identical to the [`IJobClient` methods](jobs.md#management):

- **Cancel** a job that has not finished.
- **Run now** a job waiting out its retry backoff — consumes no retry budget.
- **Requeue** a finished job, minting a new one linked back to the original.
- **Trigger** a recurring definition without disturbing its schedule.
- **Recover** a suspended compensation — retry, skip or abandon. See
  [Workflows](workflows.md#when-a-compensation-itself-fails).
- **Signal** a waiting workflow instance.

Buttons enable from the job's actual state, so a terminal job offers Requeue and not Cancel.

## The REST contract

Versioned in the URL. `v1` is stable at 1.0 and is what the UIs are built against.

```
GET  /millrace/api/v1/info
GET  /millrace/api/v1/statistics
GET  /millrace/api/v1/jobs
GET  /millrace/api/v1/jobs/{id}
GET  /millrace/api/v1/recurring
GET  /millrace/api/v1/instances

POST /millrace/api/v1/jobs/{id}/cancel
POST /millrace/api/v1/jobs/{id}/requeue
POST /millrace/api/v1/jobs/{id}/run-now
POST /millrace/api/v1/recurring/{id}/trigger
POST /millrace/api/v1/instances/{id}/compensation/{action}
POST /millrace/api/v1/signals/{name}/{correlationId}
```

That last one is how an external system — a webhook, a service in another language — resumes a
waiting workflow:

```bash
curl -X POST /millrace/api/v1/signals/approval/cust-1 \
     -H 'Content-Type: application/json' \
     -d '{"isApproved":true}'
```

### Pagination

Cursor-based, with an **opaque token** and **no total count**. Counting rows in a job table under
load is either expensive or wrong, and a UI that shows a stale number is worse than one that does
not show it.

### OpenAPI

`AddMillraceDashboard()` registers an OpenAPI document describing the contract. It includes **only**
dashboard endpoints — your application's own OpenAPI documents are untouched, and the dashboard's
never absorbs your endpoints. Point any generator at it to build a typed client.

No spec UI (Swagger UI, Scalar) is bundled. The document is served; rendering it is your choice.

## Choosing a UI

All three are functionally identical, built against the same contract, and checked by an automated
parity test that fails the build when a contract endpoint no UI reaches.

| Package | Bundle size | Choose it when |
|---|---|---|
| `Millrace.Dashboard.Ui.React` | ~150–210 KB | Default choice. |
| `Millrace.Dashboard.Ui.Angular` | ~150–210 KB | Your team's tooling is Angular. |
| `Millrace.Dashboard.Ui.Blazor` | **~6.4 MB** | You want a .NET dashboard end to end. |

```csharp
builder.Services.AddMillraceReactUi();
builder.Services.AddMillraceAngularUi();
builder.Services.AddMillraceBlazorUi();
```

> [!NOTE]
> The Blazor package is large because a Blazor WebAssembly application ships a .NET runtime —
> `dotnet.native.wasm` at 2.9 MB and `System.Private.CoreLib` at 1.6 MB. That is measured, not
> estimated, and no amount of care makes it not so. It is accepted rather than engineered around:
> the package is opt-in, and an operations dashboard is not a public page where first-paint bytes
> decide anything.

**Installing a UI package never requires Node, npm or a CDN.** The bundle ships prebuilt and embedded
in the assembly, and the packaging smoke test proves it by installing the packages into a project
that has no Node toolchain at all.
