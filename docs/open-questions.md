# Open questions

Design calls that must be settled **before** the code they govern is written. Each has a
`type:spike` issue. When one is answered, the answer moves into `ARCHITECTURE.md` §11 (decision
log) and the entry is deleted from here.

Anything already decided is *not* here — see §11. Don't re-litigate those.

## Currently blocking

**Nothing.** No design question is holding up code right now.

The last one was the generator for the documentation site, settled on 2026-07-28 as docfx (§11.39,
closing [#48](https://github.com/inferenceailab/Millrace/issues/48)) — which was also the last issue
in the 1.0 milestone.

## Settled

The four questions that gated the 0.2 dashboard slice were settled on 2026-07-25 and now live in
`ARCHITECTURE.md` §11.11–§11.14:

| Was | Settled as | Decision |
|---|---|---|
| API versioning and OpenAPI generation | URL segment; built-in `Microsoft.AspNetCore.OpenApi`; no bundled spec UI | §11.11 |
| Pagination and filter DTOs | Cursor with an opaque token, no total count; `JobQuery`/`Page<T>` frozen in §4.1 | §11.12 |
| Default authorization posture | Startup error outside Development | §11.13 |
| Where `IMonitoringStorage` lives | Separate interface, required of a supported provider | §11.14 |
| Whether the three UIs share one rendered implementation | No — shared non-visual core, per-framework rendering, native Blazor | §11.23 |

Settled since, each once it stopped being answerable on paper:

| Was | Settled as | Decision |
|---|---|---|
| Workflow checkpoints cannot be atomic with the job transition ([#64](https://github.com/inferenceailab/Millrace/issues/64)) | An optional checkpoint on `JobTransition`, committing in the same atom | §11.16 |
| Which generator the documentation site uses ([#48](https://github.com/inferenceailab/Millrace/issues/48)) | docfx — the only one that publishes the XML comments §11.34 made mandatory | §11.39 |

Two consequences are worth knowing before writing UI code, because they are easy to violate by
habit:

- **No list view may show a total or a page number** — cursor paging deliberately does not carry
  one (§11.12). Next/previous only. Aggregate counts come from `GetStatisticsAsync`.
- **The cursor is opaque.** Clients must round-trip it untouched, never parse or construct one.

## Accepted gaps — carried forward deliberately

These are known and consciously not being fixed. They are not spikes.

- **Worker self-reclaim has no end-to-end test.** Impossible with real time, because startup
  validation enforces `LeaseDuration > HeartbeatInterval`. Semantics are covered at the conformance
  and unit level instead (`Expired_lease_is_reclaimable_and_increments_attempt`,
  `Renewal_racing_reclaim_has_exactly_one_owner`).
- **The multi-node clock-skew envelope is not conformance-testable.**
  `LeaseDuration > HeartbeatInterval + skew` is validated at startup and documented as an operating
  requirement (§11.8), not enforced by the kit.
- **The PostgreSQL conformance suite skips on Windows CI.** Windows runners cannot run Linux
  containers. The skip is deliberate and explicit; the Linux job is strict and provides the real
  coverage, and any job that sets nothing inherits strictness (§53).
