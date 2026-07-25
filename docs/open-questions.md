# Open questions

Design calls that must be settled **before** the code they govern is written. Each has a
`type:spike` issue. When one is answered, the answer moves into `ARCHITECTURE.md` §11 (decision
log) and the entry is deleted from here.

Anything already decided is *not* here — see §11. Don't re-litigate those.

## Blocking phase 0.2 — the dashboard slice

The dashboard contract is rendered three times (React 0.2, Angular 0.5, Blazor 1.0) against a
single versioned REST + OpenAPI contract (§7, §11.4). That is the whole reason these are decided
once, up front, rather than discovered during implementation: a DTO shape that leaks into three
UIs and a published OpenAPI document is expensive to change afterwards.

### Q1. API versioning scheme and OpenAPI generation

URL segment (`/millrace/api/v1/...`) or header-based negotiation? And generate the OpenAPI
document with built-in ASP.NET Core OpenAPI or Swashbuckle?

*Why it blocks:* the version lands in every client's base path and in the contract-parity tracking
between the three UIs.

### Q2. Pagination and filter shape for job and instance queries

Cursor-based or offset-based? This freezes the `JobQuery` / `InstanceQuery` / `Page<T>` DTOs
sketched in §4.1.

*Why it blocks:* `IMonitoringStorage` cannot be implemented in either provider until the query DTOs
exist, and every list view in every UI binds to `Page<T>`. Note the job substrate already claims
order by `Priority DESC` then FIFO (§11.8) — cursor pagination must be stable under that ordering
while jobs change state underneath it.

### Q3. Default authorization posture for `IMillraceDashboardAuthorization`

What happens when a consumer mounts the dashboard without configuring authorization? Proposed:
deny by default outside Development. The alternative is allow-by-default with a startup warning.

*Why it blocks:* it is a security default, and defaults are breaking changes to tighten later.

### Q4. Where `IMonitoringStorage` lives

Does it land in the existing `Millrace.Storage.*` provider packages now — the schema already
supports it — or ship as an optional capability a provider may decline, in the style of
`IStorageNotifier` (§4.1 P3)?

*Why it blocks:* it decides whether a third-party provider is obliged to implement the read model
to be "supported", which changes both the conformance kit and the provider-authoring story.

## Accepted gaps — carried forward deliberately

These are known and consciously not being fixed. They are not spikes.

- **Worker self-reclaim has no end-to-end test.** Impossible with real time, because startup
  validation enforces `LeaseDuration > HeartbeatInterval`. Semantics are covered at the conformance
  and unit level instead (`Expired_lease_is_reclaimable_and_increments_attempt`,
  `Renewal_racing_reclaim_has_exactly_one_owner`).
- **The multi-node clock-skew envelope is not conformance-testable.**
  `LeaseDuration > HeartbeatInterval + skew` is validated at startup and documented as an operating
  requirement (§11.8), not enforced by the kit.
