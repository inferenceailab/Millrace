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

### Should the three UIs share one rendered implementation? — [#86](https://github.com/inferenceailab/Millrace/issues/86)

**Blocks the Blazor UI (1.0); does not block React or Angular, which now share a non-visual core.**

§11.4 chose "designed once in the contract, rendered three times". The alternative is to render
*once* — a web-component implementation (Stencil or Lit) that React, Angular and Blazor all host —
which would supersede §11.4 rather than implement it. Elsa v3 is the reference: its `elsa-studio`
designer is Stencil-compiled custom elements, and it **also** ships a separate Blazor-native Elsa
Studio, which is the empirical tell that the single-module approach breaks at Blazor.

What is already settled and not part of this question: the API client, contract types and
formatting live in `src/ui-shared/` and the stylesheet is shared, so the ~60% of a UI that is not
rendering is written once (§11.21). This question is only about the remaining rendering layer.

The three candidate shapes, and what each costs:

| | One implementation | An Angular shop can extend it | Blazor shares it | Cost to adopt |
|---|---|---|---|---|
| Web components | yes | **no** — still an island, just a standards-based one | hosts it, cannot extend in C# | stylesheet must be re-cut for shadow DOM |
| Shared core + thin renderers (**current**) | no | yes | no — shares the C# DTOs instead | already done for React/Angular |
| Status quo (per-UI everything) | no | yes | no | — |

Two things to weigh that are easy to get wrong:

- **The stylesheet does not survive shadow DOM as-is.** `millrace.css` is 28 custom properties and
  37 class selectors. The properties inherit through a shadow boundary; the class selectors do not.
  So "the tokens already work" is true and insufficient — the component styles would have to be
  re-cut per component or exposed via `::part`, which is a rewrite of the visual layer, not a port.
- **#46's motivation argues against web components.** It asks for an Angular dashboard "without a
  React island". A custom element is still an island: an Angular team cannot fork a page, add a
  column, or restyle it in their own idiom. Web components solve "one thing to maintain"; they do
  not solve "no island", which is what was asked for.

**Blazor is the deciding case, and it is asymmetric.** React and Angular can share a TypeScript
core because both consume ES modules. Blazor cannot — but it does not need to, because it already
has a *better* shared core available: the real C# contract types in `Millrace.Dashboard`, which the
TypeScript ones are a hand-written mirror of. A Blazor UI referencing those shares more of the
contract than a TypeScript core could ever give it. So "one common module for all three" is only
reachable via web components, and only by making the Blazor UI not really Blazor.

Answer before starting the Blazor UI. Answering it later means rewriting two implementations.

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
