# Benchmarks

Millrace against **Hangfire** for jobs and **WorkflowCore** for workflows: same PostgreSQL server,
same process, same work, same worker concurrency. Answers
[#49](https://github.com/inferenceailab/Millrace/issues/49).

The harness is in this repository under [`bench/`](../bench), and everything below regenerates with
two commands. That is the point of publishing it — a benchmark nobody else can run is a claim, and
the [comparison table in `ARCHITECTURE.md`](../ARCHITECTURE.md) has been making claims since before
there was anything to measure.

> **Read the [caveats](#what-this-does-not-show) before quoting any of this.** These numbers were
> produced on one developer machine with the database on loopback. They are a comparison between
> three libraries under identical conditions, not a capacity plan for anyone's production.

## Reproducing them

```
cd bench && docker compose up -d
dotnet run -c Release --project Millrace.Benchmarks -- --all --repeats 9 --json raw.json
```

**About 34 minutes**, which is what the tables below were produced with. Dropping `--repeats 9` uses
the harness default of three and takes roughly a third as long — right for iterating, not for
publishing. `--help` lists the knobs; [`bench/README.md`](../bench/README.md) covers running one cell
at a time.

Worth one sanity run first — `--scenario drain --system millrace --jobs 500 --repeats 1` finishes in
under a minute. The harness compiles in CI but never executes there, so it is the kind of code that
rots quietly, and a small run is the cheapest way to find out before committing half an hour.

## The numbers

Median of 9 runs per cell. Millrace 1.1.0, Hangfire 1.8.24 with Hangfire.PostgreSql 1.21.1,
WorkflowCore 3.18.0. Measured 2026-07-30.

```
Machine: Microsoft Windows 10.0.26200 · X64 · 28 logical cores
Runtime: .NET 10.0.9 · server GC · PostgreSQL 17.9
Settings: workers=20, producers=8, jobs=10000, instances=2000, rate=200/s for 15s, median of 9
```

### Enqueue throughput — client writes, nothing consuming

| System | Tuning | Throughput | spread |
|---|---|--:|--:|
| **Millrace** | matched | **1,763 jobs/s** | 21% |
| Hangfire | matched | 948 jobs/s | 16% |

### Drain throughput — a standing backlog, workers started

| System | Tuning | Throughput | Startup | spread |
|---|---|--:|--:|--:|
| **Millrace** | matched | **1,992 jobs/s** | 9.1 ms | 15% |
| Hangfire | matched | 616 jobs/s | 19 ms | 2% |
| **Millrace** | default | **1,991 jobs/s** | 9.1 ms | 7% |
| Hangfire | default | 640 jobs/s | 18 ms | 2% |

### Enqueue-to-execute latency — steady arrivals at 200/s, unsaturated

| System | Tuning | p50 | p95 | p99 | max | achieved rate |
|---|---|--:|--:|--:|--:|--:|
| **Millrace** | matched | **6.8 ms** | 11 ms | 13 ms | 32 ms | 200/s |
| Hangfire | matched | 22 ms | 31 ms | 38 ms | 51 ms | 200/s |
| **Millrace** | default | **6.7 ms** | 11 ms | 13 ms | 33 ms | 200/s |
| Hangfire | default | 13 ms | 18 ms | 22 ms | 37 ms | 200/s |

### Workflow throughput — three-step instances, drained from a backlog

| System | Tuning | Throughput | Startup | spread |
|---|---|--:|--:|--:|
| **Millrace** | matched | **450 inst/s** | 3,466 ms | 4% |
| WorkflowCore | matched | 70 inst/s | 10,517 ms | 3% |

### What to take from it

Millrace is **1.9× on enqueue**, **3.1–3.2× on drain**, **1.9–3.2× on median latency**, and **6.4× on
workflow instances**. The gaps are large enough to survive the caveats below; the exact multiples
are not, and nobody should quote them to two significant figures.

The drain gap is the one with an identifiable cause rather than a general "it is newer": Millrace
claims a batch of jobs per round trip (`ClaimBatchSize`, 16 by default) where Hangfire fetches one
at a time. At these rates the round trip *is* the cost, so batching it is most of the difference.

**Two results cut against the setup rather than for it, and are reported because they are true:**

- **Hangfire is still faster on its own defaults than on the tuning chosen to be fair to it** — 13 ms
  against 22 ms at p50. Enabling `EnableLongPolling`, the closest analogue to the LISTEN/NOTIFY wake
  Millrace uses, made it slower rather than faster. If the *matched* row were dropped, Hangfire
  would look worse than it is. This survived a re-measurement on a quieter machine, which is more
  than the original run could claim for it.
- **Millrace's workflow startup is 3.5 s.** An instance is three checkpointed steps and the pipeline
  has to fill before any instance completes, so a 2,000-instance backlog spends its first seconds
  producing nothing. WorkflowCore's is worse (10.5 s), but "less bad than WorkflowCore" is not a
  defence of a cold-start figure that will be visible to anyone starting a burst of workflows.

### Compared with the previous run

This is the first re-measurement, and it is the more useful half of the exercise: it says which parts
of the claim above are properties of Millrace and which were properties of a developer workstation.

**Every absolute number improved, on every system.** The comparands' versions did not change —
Hangfire 1.8.24 and WorkflowCore 3.18.0 in both runs — so their gains are not code:

| | previous | this run | change |
|---|--:|--:|--:|
| Millrace enqueue, *matched* | 1,474/s | 1,763/s | +20% |
| Hangfire enqueue, *matched* | 828/s | 948/s | +14% |
| Millrace drain, *matched* | 1,692/s | 1,992/s | +18% |
| Hangfire drain, *matched* | 503/s | 616/s | +22% |
| Millrace workflow | 352/s | 450/s | +28% |
| WorkflowCore workflow | 67/s | 70/s | +4% |

The previous session found "some unidentified process retrying a connection to the benchmark's port
every two seconds for the entire session". A uniform lift across three libraries, two of them
byte-identical, is what that looks like when it goes away. **Read none of the absolute gains as
Millrace getting faster.**

**The ratios are what held.** On the job scenarios they moved less than the harness's own noise floor:
enqueue 1.78× → 1.86×, drain *matched* 3.36× → 3.23×, drain *default* 3.19× → 3.11×. All under 10%,
which by the rule below means nothing happened.

**Two ratios did move, and both are worth naming rather than smoothing over.**

- **Latency narrowed, against Millrace.** The published claim was 2.5–5× on median latency; it is now
  1.9–3.2×. Millrace was flat (6.9 → 6.8 ms *matched*, 7.2 → 6.7 ms *default*) while Hangfire improved
  sharply (34 → 22 ms, 18 → 13 ms). The reading: Millrace's ~7 ms looks floor-bound — it is a
  notification round trip, and a quieter machine cannot make it shorter — whereas Hangfire's was
  polling-bound and noise-sensitive. **The original latency multiple was inflated by the noise, and
  the honest figure is the smaller one.**
- **Workflow widened, for Millrace** — 5.3× → 6.4×. Most of that is startup rather than steady state:
  Millrace's fell 4,435 → 3,466 ms while WorkflowCore's barely moved, and WorkflowCore's run is
  startup-dominated (10.5 s of roughly 29). Throughput here includes spin-up by design, so a startup
  improvement shows up as a throughput ratio. Do not read 6.4× as the engine executing steps 6.4×
  faster.



## What is measured

Four questions, each asked identically of every system.

| Scenario | The question | Metric |
|---|---|---|
| **enqueue** | How fast can a caller write jobs, with nothing consuming them? | jobs/s |
| **drain** | How fast does a standing backlog clear once workers start? | jobs/s |
| **latency** | With arrivals well below saturation, how long from enqueue to execution? | p50/p95/p99 ms |
| **workflow** | How fast does a backlog of three-step workflow instances complete? | instances/s |

**enqueue** never starts a worker, so it isolates the write path — expression capture, argument
serialization, insert. It is the cost paid on the caller's own thread, which is the one that matters
to whatever is serving a request while it enqueues.

**drain** seeds the whole backlog with workers stopped, so every system starts from the same
standing queue and none is measured while racing its own producer. Throughput is the backlog divided
by the time from starting the pool to the last completion — spin-up included, and reported
separately as *startup* so it can be seen rather than inferred.

**latency** is the scenario polling architecture shows up in. Arrivals are paced against the clock
in 25 ms batches rather than one at a time, because Windows timer granularity is ~15 ms and pacing
each job individually would produce a sawtooth that belongs to `Task.Delay` rather than to any
system here. Every system receives the identical arrival pattern.

**workflow** runs the same three-step linear definition on both engines, over the same data
document, seeded and drained exactly as the jobs are. An instance is three checkpointed steps, so an
instance is not comparable to a job — only to another engine's instance.

Neither backlog scenario reports latency percentiles. Everything in a seeded backlog is enqueued
before anything runs, so a job's "latency" is its queue position divided by the drain rate: the
throughput column restated in a way that reads like responsiveness and is not.

## How the systems are configured

The rules, stated so they can be argued with rather than taken on trust:

1. **One PostgreSQL server; one database per system, dropped and recreated before every measured
   run.** Same disk, same settings, same cache pressure — but no run inherits another's dead tuples
   or planner statistics. Without this the system measured second looks faster for it.
2. **Identical worker concurrency (20).** It is the one knob every deployment sets deliberately, and
   leaving three different defaults in place would make every other number a comparison of core
   counts.
3. **Identical job body: nothing at all.** A body with any weight makes every system converge on
   `parallelism ÷ body duration`, and the substrate — the thing under test — stops being visible.
   This measures the ceiling, not a prediction about any real workload.
4. **Where a knob had to move, it moved in the comparand's favour.** Millrace runs on its shipped
   defaults in every row of every table. Hangfire and WorkflowCore are the ones adjusted.
5. **Setup is never inside the measured window.** Schema DDL, EF Core migrations and JIT are all
   paid in a discarded warmup.
6. **Median of nine runs, with every repeat kept** in `--json` output and the spread published
   beside the median. Nine is what the published tables use (`--repeats 9`); the harness *default* is
   three, which is for iterating rather than publishing.

### Two tunings, both published

| Tuning | What it means | Why it is here |
|---|---|---|
| **matched** | Every system polls at the same 200 ms floor, and Hangfire's long polling is on | The only way a latency number says something about design rather than about a default someone chose in 2016 |
| **default** | Each system exactly as it ships, concurrency aside | What an evaluator actually gets on day one, which is also a real difference |

Publishing only *matched* would hide that Hangfire ships a 15-second poll interval. Publishing only
*default* would be winning an argument that was never had. So both.

The 200 ms floor is Millrace's own `MinPollDelay` default — moving Millrace instead would mean
tuning the system under test to beat the comparands.

### What was changed, per system

| System | Changed from its defaults | Why |
|---|---|---|
| **Millrace** | `MaxParallelism` only | Its shipped defaults already sit at the matched floor. Its two rows differ in nothing, which makes them a repeatability control — read the gap between them as this harness's noise floor |
| **Hangfire** | `WorkerCount`; under *matched* also `QueuePollInterval` 15 s → 200 ms and `EnableLongPolling` on | Long polling is the closest equivalent to the LISTEN/NOTIFY wake Millrace uses. Without it, a latency comparison measures a default rather than a design |
| **WorkflowCore** | `MaxConcurrentWorkflows` 4 → 20, `PollInterval` → 200 ms | Left alone it would be compared at a fifth of everything else's concurrency, which would say nothing about either engine |

## What this does not show

Every one of these would move the numbers, most of them toward each other:

- **The database is on loopback.** Real deployments have network round-trips between the worker and
  its storage, and that latency is added to every system equally — which compresses the *relative*
  differences here, because a fixed cost added to both sides of a ratio moves it toward 1.
- **One node.** Nothing here measures multi-node claim contention, which is where a storage
  contract's locking design earns or loses its keep, and it is the case Millrace's conformance kit
  cares most about.
- **The happy path only.** No failures, no retries, no dead-lettering, no compensation. A workload
  that fails 5% of the time exercises entirely different code.
- **A no-op job body.** See rule 3 — this is a ceiling. Any real job does work, and the closer that
  work gets to milliseconds the less any of this matters.
- **One shape of workload.** Uniform jobs on one queue. No priorities, no mixed durations, no
  starvation.
- **Nothing about durability, correctness or operability**, which is where most of the actual
  argument between these libraries lives. A system can be fast and lose jobs. These runs would not
  notice.

## Reading the spread

The **spread** column is `(max − min) ÷ median` across the nine repeats. It is deliberately the
crudest measure available: one slow run moves it a lot, which is what makes it useful as a warning
and useless as a summary.

**The machine was not isolated, and the numbers above were published anyway.** It is a developer
workstation with unrelated containers running throughout, and Docker was cold-started for this run.
The previous session additionally found some unidentified process retrying a connection to the
benchmark's port every two seconds throughout; that is gone, which is most of why every absolute
number rose.

The reason for publishing regardless is the repeatability control. Millrace's two drain rows are the
same configuration measured twice, nine times each, with nothing changed between them:

| | median | spread |
|---|--:|--:|
| Millrace drain, *matched* | 1,992 jobs/s | 15% |
| Millrace drain, *default* | 1,991 jobs/s | 7% |

**The medians agree to within 0.05% while individual runs vary by 15%.** That is the shape of noise
that a median absorbs and a mean would not, and it is why the medians are quoted and every repeat is
kept in `--json`. It also sets the floor for reading the rest of the table: a difference under about
10% between two rows here means nothing, which is why the results above are described as "3.1–3.2×"
rather than as a number.

The previous run's control agreed to 0.4%; this one to 0.05%. Two runs is not a trend, but it is at
least not evidence that the control is luck.

### The widest spread has a cause, and it is the harness

Millrace's enqueue spread is 21%, the widest cell in the table. It is not a wide distribution — it is
the first two repeats being cold:

```
1,541  1,439  1,777  1,739  1,789  1,756  1,763  1,807  1,792
```

Runs 3–9 sit in a 4% band; runs 1 and 2 are the two lowest of the nine. The warmup exercises the
*drain* scenario, which seeds a backlog and therefore only partly pays for the enqueue path — so the
enqueue scenario is still warming during its own first repeats.

**Two things follow, and the second is the point.** It is a real gap in the warmup, worth closing
before the next publication. And it barely moved the published figure: the median of runs 3–9 alone is
1,777/s against the published 1,763/s, a 0.8% difference across a 21% spread. That is the median doing
exactly the job §11.37 claims for it, demonstrated on an accident rather than asserted.

Hangfire's enqueue does not show the pattern — its low repeat is run 5, not runs 1–2 — so this is
specific to Millrace's write path rather than to the harness's ordering in general.

Reproducing this on a genuinely quiet machine should tighten the spreads further and should not move
the ratios. If it moves a *ratio* substantially, that is worth an issue; a moved absolute is worth a
shrug, as this run demonstrates.

## If a run stalls

The harness gives each run ten minutes and reports one that overruns as a stall, excluding it from
the median and printing the count beside the result. This exists because it happened: a Hangfire
drain stopped making progress once in nine runs during development, and the first version of the
harness took the whole suite down with it.

It has not recurred since the harness began settling between runs — recreating a database underneath
a worker pool that has not finished shutting down is the most plausible cause, since PostgreSQL's
`DROP DATABASE ... WITH (FORCE)` terminates whatever is still connected. It is recorded here rather
than omitted: the published tables have no stalls in them, and that is a statement about these runs
rather than a guarantee.

The 2026-07-30 re-measurement had **zero stalls across 108 measured runs**, which is the first
independent evidence that the settling fixed it rather than that the stall happened to not recur.
