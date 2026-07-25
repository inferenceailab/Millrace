# Next steps — rest of phase 0.2 (as of 2026-07-25)

Done so far: phase 0.1 complete (contract, InMemory, engine, TCK — commits `4ab0d7c`..`15a9480`)
and the PostgreSQL provider (`fbdc256`) passing all 70 conformance facts against postgres:17.
State of play details: ARCHITECTURE.md §11 (decisions 8–9 are the ones added since bootstrap).

## Up next: the dashboard slice of 0.2 (§7, §11.4 — design input needed before coding)

1. **`IMonitoringStorage` read model** (§4.1 sketch): `GetStatisticsAsync`, `QueryJobsAsync`,
   `QueryInstancesAsync`, `GetJobAsync` details. Needs concrete `JobQuery`/`InstanceQuery`/
   `Page<T>` DTO shapes — this freezes with the API contract, so decide deliberately.
   Implement in InMemory + PostgreSQL, add TCK facts.
2. **`Weft.Dashboard` backend**: `app.MapWeftDashboard("/weft")`, versioned REST API with a
   published OpenAPI document, `IWeftDashboardAuthorization` hook. Read-only endpoints first
   (stats, job lists by state, job detail, recurring view, instance list); management actions
   (requeue, retry-now, cancel, trigger-recurring) after the read contract settles.
3. **`Weft.Dashboard.Ui.React`**: embedded prebuilt bundle, reference client for the contract.

## Open questions to settle first (the contract is rendered three times — decide once)

- API versioning scheme (URL segment vs header) and OpenAPI generation approach
  (built-in ASP.NET Core OpenAPI vs Swashbuckle).
- Pagination/filter shape for job and instance queries (cursor vs offset; freeze in DTOs).
- Default authorization posture when `IWeftDashboardAuthorization` isn't configured
  (deny-by-default outside Development?).
- Whether `IMonitoringStorage` lands in the existing provider packages now (schema already
  supports it) or ships as optional capability.

## Carry-overs / accepted gaps

- Worker self-reclaim has no e2e test (impossible with real time — validation enforces
  lease > heartbeat); semantics covered at TCK/unit level.
- SqlServer provider and `Weft.Testing` are 0.5 (§10) — nothing pending there.
