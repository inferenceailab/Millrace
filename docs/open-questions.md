# Open questions

Design calls that must be settled **before** the code they govern is written. Each has a
`type:spike` issue. When one is answered, the answer moves into `ARCHITECTURE.md` §11 (decision
log) and the entry is deleted from here.

Anything already decided is *not* here — see §11. Don't re-litigate those.

## Currently blocking

### Workflow checkpoints cannot be atomic with the job transition — [#64](https://github.com/inferenceailab/Millrace/issues/64)

**Blocks the rest of 0.3** (#36, #37, #38). §6.2 requires the instance checkpoint, the next-activity
enqueue and the activity job's transition to commit as one atom — "the checkpoint is the unit of
exactly-once progress". `JobTransition` can carry the enqueue but not the checkpoint, and
`UpdateInstanceAsync` is a separate call on a separate interface, so the coupling is inexpressible.

Either ordering of two calls loses: checkpoint-then-transition re-runs an activity from a cursor
that already moved past it; transition-then-checkpoint stalls the instance forever with no failure
recorded. Recommended fix is an optional checkpoint on `JobTransition`, the direct analogue of the
existing `Enqueue`.

The definition builder (#35) is unaffected and is done — it produces the graph shape and touches no
storage.

## Settled

The four questions that gated the 0.2 dashboard slice were settled on 2026-07-25 and now live in
`ARCHITECTURE.md` §11.11–§11.14:

| Was | Settled as | Decision |
|---|---|---|
| API versioning and OpenAPI generation | URL segment; built-in `Microsoft.AspNetCore.OpenApi`; no bundled spec UI | §11.11 |
| Pagination and filter DTOs | Cursor with an opaque token, no total count; `JobQuery`/`Page<T>` frozen in §4.1 | §11.12 |
| Default authorization posture | Startup error outside Development | §11.13 |
| Where `IMonitoringStorage` lives | Separate interface, required of a supported provider | §11.14 |

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
