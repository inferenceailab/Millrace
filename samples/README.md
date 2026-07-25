# Millrace sample

A single minimal-API host showing the whole library: durable jobs, a workflow, and the ops
dashboard mounted as middleware in the same process.

## Run it

```bash
dotnet run --project Millrace.Sample.Api
```

Then open <http://localhost:5000> — it redirects to the dashboard.

That is the whole setup. No database, no broker, no sidecar: the bundled in-memory provider carries
everything. It is explicitly **not durable** — restart the host and the queue is empty, which is
exactly why it is only for development.

### With a real database

```bash
docker compose up -d

MILLRACE_POSTGRES="Host=localhost;Port=5433;Database=millrace;Username=millrace;Password=millrace" \
  dotnet run --project Millrace.Sample.Api
```

One connection string is the only difference. Now enqueue something, stop the host, start it again —
the job is still there and still runs.

## What to try

| Request | Shows |
|---|---|
| `POST /orders/A1/confirm` | Fire-and-forget: one line to enqueue |
| `POST /orders/A1/remind?seconds=30` | A delayed job — durable, so it survives a restart |
| `POST /orders/A1/settle` | A continuation: the notification runs only if the charge succeeds |
| `POST /reports/nightly` | A cron schedule, upserted idempotently by id |
| `POST /onboarding` with `{"customerId":"c1","needsApproval":true}` | A workflow that parks on a signal |
| `GET /log` | What actually ran |

For the workflow, the instance suspends waiting for approval and **holds no job at all** — it is a
row until you deliver the decision:

```bash
curl -X POST http://localhost:5000/millrace/api/v1/signals/approval/c1 \
  -H 'content-type: application/json' -d '{"IsApproved":true}'
```

## The dashboard

Mounted at `/millrace` in this same host — no extra process to deploy.

- UI: <http://localhost:5000/millrace/ui>
- API: `/millrace/api/v1/...`
- OpenAPI document: `/millrace/openapi/millrace-v1.json`

The API is the product; the React UI is one client of it. Everything the UI shows is available to
`curl`, and management actions (cancel, requeue, trigger, signal) are the same contract.

**Authorization.** This sample allows anonymous access in Development and otherwise requires an
`X-Millrace-Key` header matching `MILLRACE_DASHBOARD_KEY`. Mounting the dashboard with no
authorization hook at all is a **startup error** outside Development — the API exposes job arguments
and can cancel work, so it fails closed at deploy time rather than looking broken later.
