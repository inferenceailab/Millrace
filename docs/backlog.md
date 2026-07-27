# Backlog

> **Generated file — do not edit.** Rendered from GitHub issues on 2026-07-27 by `scripts/generate-backlog.ps1`.
> The source of truth is the [project board](https://github.com/users/inferenceailab/projects/1) and the repository issues.

**60 done · 7 open** (of which 0 blocked on an unresolved spike).

## Epics

- [#1](https://github.com/inferenceailab/Millrace/issues/1) Layer 0 — storage contract and provider model
- [#2](https://github.com/inferenceailab/Millrace/issues/2) Layer 1 — durable job substrate
- [#3](https://github.com/inferenceailab/Millrace/issues/3) Layer 2 — workflow engine
- [#4](https://github.com/inferenceailab/Millrace/issues/4) Layer 3 — dashboard
- [#5](https://github.com/inferenceailab/Millrace/issues/5) Cross-cutting — observability, tenancy and consumer testing

## 0.1 — Storage contract & job substrate — **complete**

Core abstractions, InMemory provider, substrate (enqueue/delayed/cron/continuations/retries/leases), conformance kit. Proves: the storage contract is right. Delivered in the Weft prototype and imported; see docs/provenance.md.

| # | Story | Area | State |
|---|---|---|---|
| [#6](https://github.com/inferenceailab/Millrace/issues/6) | Storage contract v1: IJobStorage and IWorkflowStorage | storage | done |
| [#7](https://github.com/inferenceailab/Millrace/issues/7) | Bundled InMemory storage provider | storage | done |
| [#8](https://github.com/inferenceailab/Millrace/issues/8) | Storage conformance kit (TCK) for provider authors | storage | done |
| [#9](https://github.com/inferenceailab/Millrace/issues/9) | Expression-based invocation capture and execution | substrate | done |
| [#10](https://github.com/inferenceailab/Millrace/issues/10) | IJobClient: enqueue, schedule, continue-with and recurring | substrate | done |
| [#11](https://github.com/inferenceailab/Millrace/issues/11) | Worker pool with leases, heartbeat and two-phase shutdown | substrate | done |
| [#12](https://github.com/inferenceailab/Millrace/issues/12) | Opportunistic scheduler: due-job activation and recurring fire | substrate | done |
| [#13](https://github.com/inferenceailab/Millrace/issues/13) | UTC cron parser (five-field vixie dialect) | substrate | done |
| [#14](https://github.com/inferenceailab/Millrace/issues/14) | Retry policy with the attempt/failure split | substrate | done |
| [#15](https://github.com/inferenceailab/Millrace/issues/15) | Native multi-tenancy via ITenantContextAccessor | ops | done |

## 0.2 — PostgreSQL provider & dashboard contract — **complete**

PostgreSQL provider; dashboard REST API + OpenAPI contract; React UI (read-only). Proves: real-DB queue semantics and the API-first ops story. Blocked on the four design questions in docs/open-questions.md.

| # | Story | Area | State |
|---|---|---|---|
| [#16](https://github.com/inferenceailab/Millrace/issues/16) | PostgreSQL storage provider | storage | done |
| [#17](https://github.com/inferenceailab/Millrace/issues/17) | Spike: API versioning scheme and OpenAPI generation | dashboard | done |
| [#18](https://github.com/inferenceailab/Millrace/issues/18) | Spike: freeze pagination and filter DTOs for job and instance queries | dashboard | done |
| [#19](https://github.com/inferenceailab/Millrace/issues/19) | Spike: default authorization posture for the dashboard | dashboard | done |
| [#20](https://github.com/inferenceailab/Millrace/issues/20) | Spike: decide where IMonitoringStorage lives | storage | done |
| [#21](https://github.com/inferenceailab/Millrace/issues/21) | IMonitoringStorage read model contract | storage | done |
| [#22](https://github.com/inferenceailab/Millrace/issues/22) | Implement IMonitoringStorage in the InMemory provider | storage | done |
| [#23](https://github.com/inferenceailab/Millrace/issues/23) | Implement IMonitoringStorage in the PostgreSQL provider | storage | done |
| [#24](https://github.com/inferenceailab/Millrace/issues/24) | Conformance facts for the monitoring read model | storage | done |
| [#25](https://github.com/inferenceailab/Millrace/issues/25) | Millrace.Dashboard backend: mount, versioned REST and OpenAPI document | dashboard | done |
| [#26](https://github.com/inferenceailab/Millrace/issues/26) | Dashboard: statistics overview endpoint | dashboard | done |
| [#27](https://github.com/inferenceailab/Millrace/issues/27) | Dashboard: job list by state with filtering and pagination | dashboard | done |
| [#28](https://github.com/inferenceailab/Millrace/issues/28) | Dashboard: job detail with exception and attempt history | dashboard | done |
| [#29](https://github.com/inferenceailab/Millrace/issues/29) | Dashboard: recurring schedule view | dashboard | done |
| [#30](https://github.com/inferenceailab/Millrace/issues/30) | Dashboard: workflow instance list | dashboard | done |
| [#31](https://github.com/inferenceailab/Millrace/issues/31) | IMillraceDashboardAuthorization hook | dashboard | done |
| [#32](https://github.com/inferenceailab/Millrace/issues/32) | Millrace.Dashboard.Ui.React: embedded read-only reference UI | dashboard | done |
| [#33](https://github.com/inferenceailab/Millrace/issues/33) | Client-facing job cancellation API | substrate | done |
| [#34](https://github.com/inferenceailab/Millrace/issues/34) | Sample: minimal API host running jobs on PostgreSQL | docs | done |
| [#50](https://github.com/inferenceailab/Millrace/issues/50) | Import the prototype source tree and apply the Millrace rename | build | done |
| [#53](https://github.com/inferenceailab/Millrace/issues/53) | CI reports success when the PostgreSQL conformance suite silently skips | build | done |

## 0.3 — Workflow engine core — **complete**

Sequence/if/foreach/parallel, typed signals + bookmarks, durable timers. Proves: the jobs-as-activities bet (ARCHITECTURE.md 6.2).

| # | Story | Area | State |
|---|---|---|---|
| [#35](https://github.com/inferenceailab/Millrace/issues/35) | Workflow definition builder and exported graph shape | workflow | done |
| [#36](https://github.com/inferenceailab/Millrace/issues/36) | Checkpointed execution: instances, cursors and activities as jobs | workflow | done |
| [#37](https://github.com/inferenceailab/Millrace/issues/37) | Typed signals and bookmarks | workflow | done |
| [#38](https://github.com/inferenceailab/Millrace/issues/38) | Durable timers and delays | workflow | done |
| [#64](https://github.com/inferenceailab/Millrace/issues/64) | Workflow checkpoints cannot be atomic with the job transition | storage | done |
| [#67](https://github.com/inferenceailab/Millrace/issues/67) | A checkpoint conflict retries the activity, not just the merge | workflow | done |

## 0.4 — Sagas, versioning & management actions — **complete**

Sagas/compensation, definition versioning, dashboard management actions (contract + React). Proves: production orchestration.

| # | Story | Area | State |
|---|---|---|---|
| [#39](https://github.com/inferenceailab/Millrace/issues/39) | Sagas and compensation | workflow | done |
| [#40](https://github.com/inferenceailab/Millrace/issues/40) | Definition versioning with in-flight drain | workflow | done |
| [#41](https://github.com/inferenceailab/Millrace/issues/41) | Dashboard management actions | dashboard | done |
| [#73](https://github.com/inferenceailab/Millrace/issues/73) | Requeue and retry-now have no path through the job contract | storage | done |

## 0.5 — Breadth: OTel, SQL Server, testing, Angular — **complete**

OpenTelemetry, SQL Server provider, batch enqueue, Millrace.Testing, Angular UI. Proves: breadth and the multi-UI model.

| # | Story | Area | State |
|---|---|---|---|
| [#42](https://github.com/inferenceailab/Millrace/issues/42) | OpenTelemetry traces and metrics | ops | done |
| [#43](https://github.com/inferenceailab/Millrace/issues/43) | SQL Server storage provider | storage | done |
| [#44](https://github.com/inferenceailab/Millrace/issues/44) | Millrace.Testing consumer harness | ops | done |
| [#45](https://github.com/inferenceailab/Millrace/issues/45) | Batch enqueue | substrate | done |
| [#46](https://github.com/inferenceailab/Millrace/issues/46) | Angular UI over the frozen contract | dashboard | done |
| [#57](https://github.com/inferenceailab/Millrace/issues/57) | Per-attempt job history is a timeline the 0.1 schema cannot provide | storage | done |
| [#61](https://github.com/inferenceailab/Millrace/issues/61) | Recurring last outcome is not derivable: fired jobs are not linked to their definition | storage | done |
| [#78](https://github.com/inferenceailab/Millrace/issues/78) | Sagas: per-step error policies (Suspend, Terminate) | workflow | done |
| [#79](https://github.com/inferenceailab/Millrace/issues/79) | Sagas: what happens when a compensation itself fails | workflow | done |
| [#83](https://github.com/inferenceailab/Millrace/issues/83) | The test harness reimplements worker logic and can silently diverge from it | ops | done |

## 1.0 — Hardening & credibility

Hardening, Blazor UI, docs site, benchmarks vs Hangfire/WorkflowCore. Proves: credibility.

| # | Story | Area | State |
|---|---|---|---|
| [#47](https://github.com/inferenceailab/Millrace/issues/47) | Blazor UI over the frozen contract | dashboard | done |
| [#48](https://github.com/inferenceailab/Millrace/issues/48) | Documentation site | docs | open |
| [#49](https://github.com/inferenceailab/Millrace/issues/49) | Benchmarks against Hangfire and WorkflowCore | docs | done |
| [#77](https://github.com/inferenceailab/Millrace/issues/77) | Sagas: nested sagas | workflow | done |
| [#97](https://github.com/inferenceailab/Millrace/issues/97) | Run a failed job now, without waiting out its backoff |  | done |
| [#98](https://github.com/inferenceailab/Millrace/issues/98) | Packaging and release: nothing publishes today |  | done |
| [#99](https://github.com/inferenceailab/Millrace/issues/99) | Document the 164 undocumented public members |  | done |
| [#122](https://github.com/inferenceailab/Millrace/issues/122) | Blazor UI: multi-view layout to match React and Angular | dashboard | open |

## Not yet scheduled

| # | Story | Area | State |
|---|---|---|---|
| [#86](https://github.com/inferenceailab/Millrace/issues/86) | Spike: should the three UIs share one rendered implementation (web components) or one non-visual core? |  | done |
| [#87](https://github.com/inferenceailab/Millrace/issues/87) | Flaky: A_delay_defers_the_rest_of_the_flow_until_it_comes_due fails under parallel suite load |  | done |
| [#89](https://github.com/inferenceailab/Millrace/issues/89) | Enum values serialize as integers; every UI declares them as strings |  | done |
