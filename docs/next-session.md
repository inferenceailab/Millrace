# Picking this up again

Written 2026-07-26 at the end of the session that closed milestone 0.5, rewritten on 2026-07-28 when
the 1.0 milestone emptied, extended on 2026-07-29 after the repository was hardened and the first
dependency wave came through, again the same day after the SQLite provider shipped, on
2026-07-30 after the benchmarks were re-measured against 1.1.0, and on 2026-08-02 after August's
dependency wave was cleared, the benchmark section's explanation of its own widest spread turned out
to be wrong, and the board emptied.

**This file is not a backlog.** The work lives in [GitHub issues](https://github.com/inferenceailab/Millrace/issues)
and the [project board](https://github.com/users/inferenceailab/projects/1), mirrored for offline
reading in [`backlog.md`](backlog.md). What is here is the part that does *not* fit in an issue:
what needs a human, what will bite you, and what the last session learned the hard way.

Delete anything that stops being true.

## Start here

**Millrace 1.1.0 is released** — **ten** packages on nuget.org, the tenth being
`Millrace.Storage.Sqlite`. `v1.1.0-rc.1` went out first and `v1.1.0` followed, both from the same
commit.

1.0.0 remains the version that matters: it is where the storage and v1 REST contracts became stable
promises (§11.41) rather than working agreements. 1.1.0 is purely additive — no contract moved, which
is why it is a minor.

**That changes how this repository works, and it is the one thing to internalise before touching
anything.** Until today, a bad decision could be corrected in the next commit. Now
`IJobStorage`, `IWorkflowStorage`, `IMonitoringStorage`, the records they exchange and the
`/api/v1` surface cannot break within 1.x. Adding a member to any of those interfaces breaks every
implementor, so it waits for 2.0 or arrives as a separate interface the way §11.14 put monitoring
in its own. What is *not* frozen — the engine, the workers, the providers' SQL, the UIs, and the
conformance kit, which is a test suite and expected to grow — is where the freedom now lives.

### How it got here

The 1.0 milestone closed with eight issues, the last three on 2026-07-28:

- **[#122](https://github.com/inferenceailab/Millrace/issues/122)** ([#127](https://github.com/inferenceailab/Millrace/pull/127),
  §11.38) gave the Blazor dashboard the six views the other two have. It also found that the UI
  shipped in #120 had *never been run* — it rendered nothing on first load, for three independent
  reasons no static check could see.
- **[#48](https://github.com/inferenceailab/Millrace/issues/48)** ([#128](https://github.com/inferenceailab/Millrace/pull/128),
  §11.39) is the documentation site: docfx, live at
  <https://inferenceailab.github.io/Millrace/>, guide by hand and API reference generated from the
  XML comments. GitHub Pages is enabled and the first deploy landed and was verified in a browser.

Then **[#126](https://github.com/inferenceailab/Millrace/issues/126)** ([#131](https://github.com/inferenceailab/Millrace/pull/131),
§11.40) closed the gap §11.38 named: a `browser` CI job now serves each of the three UIs from a real
Kestrel host and drives it with Chromium, so a UI that renders nothing fails the build instead of
shipping.

Since then the repository itself was hardened (§11.42) and the first Dependabot wave was worked
through — nine dependency and security pull requests, all closed or merged. Both are covered below.

**Then the SQLite provider was built** ([#151](https://github.com/inferenceailab/Millrace/issues/151),
four pull requests, #152–#154) and a flaky workflow test was fixed (#150). That is the section below
worth reading first, because its result is a fact about the frozen contract rather than about SQLite.

**Then the benchmarks were re-measured against 1.1.0** ([#156](https://github.com/inferenceailab/Millrace/pull/156)),
which is the first time the harness has run since #49 published the table. It has its own section
below, and the short version is that it reproduced — but the useful half was learning which parts of
the old claim were about Millrace and which were about a developer workstation.

`main` builds clean, the whole suite passes, the docs deploy on every push, and **no pull request is
open** — August's dependency wave arrived on 2026-08-01 and was merged the next day, which cost ten
minutes rather than July's session tail (see below). **No issue is open either, which has not been
true before.** [#157](https://github.com/inferenceailab/Millrace/issues/157) started as a gap in the
benchmark harness's warmup, turned out to be a wrong explanation published in `benchmarks.md`, and
was corrected and closed on 2026-08-02; the five layer epics closed the same day. So the board is
empty, and there is nothing left to pick up instead of deciding what 1.x or 2.0 is for.

## Nothing is blocking on a person

Publishing credentials were the last blocker and they are done, proven by three real releases rather
than by configuration looking right. GitHub Pages needed enabling once and now is. Nothing waits on
anyone.

What *does* want a person is direction. The SQLite provider was the obvious candidate and it is done,
so that answer is spent.

**The board is now empty too.** The five layer epics ([#1](https://github.com/inferenceailab/Millrace/issues/1)–[#5](https://github.com/inferenceailab/Millrace/issues/5))
were closed on 2026-08-02: every story under all five had shipped, and they were open by neglect
rather than by design — created 2026-07-25 and never edited since, no checklists, no sub-issues. Each
carries a closing comment mapping what it covered to the milestone that delivered it, so the trail
survives the closure rather than living in a table nobody kept.

That removes the last thing that could be picked up *instead* of choosing. The nearest thing to a
named candidate is the one §11.41 names against itself: three relational providers is still one shape
of provider, and a document or key-value store would stress the clauses none of them do — the fenced
compare-and-set and the recursive cancel cascade assume a lot. That is a suggestion, not a decision,
and it wants a fresh epic argued on its own terms rather than #1 reopened.

The one standing commitment is **monthly dependency pull requests**, which are not a backlog but are
not nothing either; the first wave took a session's tail to work through. See below.

## The SQLite provider, and what it proved

Four pull requests, sliced schema+jobs+workflows → monitoring → packaging. Worth reading even if you
never touch SQLite, because the reason it was built was to interrogate §11.41.

**The frozen contract holds, unchanged.** All 126 conformance facts pass against a provider with no
server, one writer and no `SKIP LOCKED` — no suppressions, no provider-specific skips, nothing that
needed a 2.0 conversation or a §11.14-shaped side interface. §11.41 shipped 1.0 knowing the storage
contract "may not be finished being learned from" and named a third provider as the cheapest way to
find out. It was, and the answer was yes.

**Claims serialise instead of skipping locks.** Every mutating path opens `BEGIN IMMEDIATE` and takes
SQLite's single writer lock up front, so a second claimer waits rather than stepping over. The
contract only asks that two claims never return the same job, so this conforms — what it costs is
throughput under a high `MaxParallelism`, which is the point to move to PostgreSQL. That line is
load-bearing and was measured, not assumed: flipping to `deferred: true` fails exactly the seven
concurrency facts, each burning the full `busy_timeout` on a lock-upgrade deadlock, while the other
75 stay green.

**The same lock deleted work.** The PostgreSQL provider resolves an idempotency conflict by
inserting, losing, looking up the holder, and retrying when the holder went terminal in the
read-committed window between the two. Under a writer lock a plain look-then-insert is already
atomic, so neither that retry loop nor the parent row lock exists in the SQLite provider. Worth
remembering when reading the two side by side: the difference is not sloppiness.

**Three things SQLite could not copy**, all documented in the code where they happen: no
`NULLS NOT DISTINCT` (the idempotency index leads with `tenant_id IS NULL` so a null tenant collides
with another null tenant but not with a tenant literally named `''`); no data-modifying CTEs (the
claim decomposes into several statements inside the transaction); and no `LATERAL` (the recurring
last-outcome lookup became two correlated subqueries, safe only because the ordering key ends in a
unique id and so names exactly one row).

**The smoke test now runs a durable provider.** It opens a store, enqueues, closes the provider,
reopens over the same file and claims the job back. That is the only check anywhere exercising a
durable provider from a *package* rather than the source tree — the SQL providers need a server, so
their suites cannot — and it covers a class nothing else could see: `SQLitePCLRaw` ships a native
library, and a package that failed to declare it would restore cleanly and throw on the first query.
Verified by pointing the reopen at a different file and watching it fail.

**Adding SQLite surfaced a security advisory for free.** `Microsoft.Data.Sqlite` 10.0.10 resolves
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries GHSA-2m69-gcr7-jv3q (high). Restore *failed
outright* rather than warning, so it was impossible to miss; pinned to 2.1.12 via the transitive
pinning already enabled for the `Microsoft.OpenApi` case. There is now a second such pin to retire
when upstream catches up — see the dependency section.

**There is no strictness policy for SQLite and there cannot be.** The other two providers fail rather
than skip when no database is reachable, and §11.42 credits that for making the `Microsoft.Data.SqlClient`
7.0 bump safe to accept on evidence. SQLite is a file, always reachable, so the policy would assert
nothing. **A green SQLite run therefore carries less weight than a green PostgreSQL run** — for the
others, whether the suite ran at all was the part in doubt. This is written into the harness doc
comment so the two are not read as equivalent.

## The benchmarks, re-measured

The harness had not run since #49 published the table, and this file's closing line said the cheapest
way to find out whether the numbers still held was to run it before believing them. That has now
happened, so here is what it cost and what it changed.

**It reproduced, and the ratios are the part that reproduced.** On the job scenarios every ratio
landed inside the harness's own 10% noise floor: enqueue 1.78× → 1.86×, drain 3.36× → 3.23× matched
and 3.19× → 3.11× default. Nothing in the published claim had to be withdrawn.

**Every absolute number improved on every system, and none of it is code.** Two of the three
comparands did not change version — Hangfire 1.8.24 gained 14–22% and WorkflowCore 3.18.0 gained 4%
on byte-identical code. The previous session had found some process retrying a connection to the
benchmark port every two seconds throughout, and a uniform lift across three libraries is what that
looks like when it stops. **So the absolutes measure the machine, and only the ratios survive a change
of machine.** Read the table that way, and do not quote a jobs/s figure without the box it came from.

**Two ratios moved, and both moved toward honesty.** Latency *narrowed*: the published 2.5–5× is
really 1.9–3.2×, because Millrace sat flat at ~6.8 ms while Hangfire improved from 34 ms to 22 ms.
Millrace's figure looks floor-bound on a notification round trip; Hangfire's was polling-bound and
therefore noise-sensitive, so the original multiple was inflated by the noise the re-run removed and
the smaller number is the one to quote. Workflow *widened* to 6.4×, but mostly through startup falling
4,435 ms to 3,466 ms against a comparand whose run is startup-dominated — not the engine executing
steps 6.4× faster. A widening ratio deserves the same suspicion as a flattering absolute.

**The re-run found a 21% cell and invented a cause for it — and the invented cause is the more
useful finding.** Millrace's enqueue spread was 21%, the widest cell in the table, with runs 1 and 2
the two lowest of nine. That was written up, in `benchmarks.md` and here, as a gap in the warmup:
the warmup exercises *drain*, which seeds a backlog and therefore only partly pays for the enqueue
path. It reads well. Nobody tested it.

**Tested on 2026-08-02, it is wrong** ([#157](https://github.com/inferenceailab/Millrace/issues/157),
now re-scoped to correcting the claim). Building the warmup it implies changes nothing: on a settled
machine the *unmodified* harness already produces an 8% enqueue spread with runs 1 and 2 mid-band,
and adding the warmup leaves it at 8%. The low repeats also refuse to stay at the front — across four
runs they landed on 2 and 8, then 6–9, then nowhere — and a warmup gap is front-loaded every time by
construction. The premise misread the harness besides: `DrainThroughputAsync` seeds through the same
`EnqueueAsync` call at full size with workers stopped, so the enqueue path was already warm.

So the 21% is what the document's own "Reading the spread" section says a spread is: an un-isolated
machine, Docker cold-started for that run, one draw where the low pair happened to land first. The
0.8% median shift stands and is still §11.37's claim demonstrated on an accident — it just was not
the accident anyone named.

**Read this as a warning about the shape of the mistake, not about benchmarks.** The number was
measured and the explanation beside it was not, and in the finished prose the two were
indistinguishable. That is the same failure as the false encoding comment recorded further down, and
the same fix applies: go break it.

**Practicalities for whoever runs it next.** It is **34 minutes**, not the twenty the method section
used to claim, and there were zero stalls across 108 runs. Method rule 6 said three runs while the
tables said nine; the rule was wrong and `--repeats 9` is now in the reproduction command so it
produces what is published. Do a one-minute sanity run first — the harness never executes in CI, so
the first thing to establish is that it still starts.

## Releasing

**It works, and it has now run four times.** `v0.1.0-alpha.1` put eight packages on nuget.org on
2026-07-26; `v1.0.0-rc.1` and `v1.0.0` put **nine** there on 2026-07-28; `v1.1.0-rc.1` and `v1.1.0`
put **ten** there on 2026-07-29, first attempt each time. Tag `vX.Y.Z` and the rest is automatic:
pack, smoke-test the built artifacts, exchange a GitHub OIDC token for a one-hour nuget.org key
(§11.33), push, create the GitHub release. No secret is stored, so nothing expires and nothing needs
rotating.

**A new package needs no workflow change.** 1.1.0 was the first release to publish a package that had
never existed, and it required editing nothing: `release.yml` packs the solution and pushes
`artifacts/*.nupkg`, so the tenth arrived through the glob. What a new package *does* need is
`<IsPackable>true</IsPackable>` — it was deliberately left false while the provider was incomplete,
precisely so an unplanned tag could not ship it early.

Three things to know before the next tag.

**The tag is the version, and nuget.org has no delete** — only unlist. A prerelease tag is the cheap
way to exercise a change to the release path, and the workflow infers prerelease status from the
hyphen: `v1.0.0-rc.1` marked itself correctly without anyone remembering to.

**The upgraded actions have now been exercised.** `release.yml`'s steps only run on a tag, so CI
never touches them; they went to `checkout@v7`, `setup-dotnet@v6`, `setup-node@v7` and
`upload-artifact@v7` on 2026-07-26 and had not run since. `v1.0.0-rc.1` was tagged partly to find
out, and they work — as does the OIDC exchange, which was the step with no fallback. **The blind
spot itself is not closed**: the next change to that file will again be unexercised until someone
tags. `docs.yml` (§11.39) is the shape of the fix — build on pull requests, deploy only from `main`
— and `release.yml` cannot copy it exactly, because it publishes somewhere with no delete. A
prerelease tag is the same idea at the cost of a version number, and it is the habit to keep.

**Verify the published packages, not just the green job.** After every tag so far, all packages were
confirmed indexed on nuget.org and then installed into a throwaway console app that ran real work —
about two minutes, and the only thing that actually proves a consumer can use what shipped. `dotnet
nuget push` reporting success means the bytes were accepted, not that they work. For 1.1.0 that meant
enqueueing a job through `Millrace.Storage.Sqlite`, closing the provider, reopening it and claiming
the job back, which also exercises the native `SQLitePCLRaw` dependency resolving from a package
rather than a project reference.

**The indexing lag is real and it is not uniform.** Minutes after the 1.1.0 push, the flat container
listed 1.1.0 for two of the ten packages and not the other eight; a few minutes later, all ten. So a
partial answer is a *timing* observation, not a failed publish — do not start diagnosing until the
same check has been repeated.

Two traps in that verification, both hit on 1.0.0. **nuget.org has two indexes**: the flat container
(package content) publishes within a few minutes, and the registration index that `dotnet add
package` resolves against lags behind it — so "indexed" by one measure is still unusable by the
other. And **your own machine caches the failure**: the first `dotnet add package Millrace` after
1.0.0 went live said *"there are no stable versions available"* long after there were, because the
NuGet HTTP cache had kept the earlier answer. `dotnet nuget locals http-cache --clear` before
concluding anything.

**Documentation ships inside the packages, so it lands *before* the tag.**
`Directory.Build.targets` packs the repository root `README.md` into every package. Tagging 1.0.0
with "Status: alpha" still in it would have embedded that in nine packages permanently — nuget.org
has no delete. Any future version bump needs the same ordering: docs merged, then tag. 1.1.0 followed
it: the README's package table and status note were merged in the packaging pull request, before the
tag existed.

That ordering has a sharp edge worth naming. Between merging the README and cutting the tag, the
README on `main` describes a package nobody can install yet. The 1.1.0 wording handled it by saying
the provider "ships with the next release" **without naming a version** — accurate in both windows,
and it makes no promise about which release, which is not a README's decision to make.

**The trust policy is pinned to the repository and to the file name `release.yml`.** Renaming or
moving that file breaks publishing, which is the trade §11.33 made deliberately: authority belongs
to the thing that runs.

## The repository's settings are part of the supply chain

Hardened on 2026-07-29 (§11.42), and worth knowing before you change anything about how work reaches
`main` — or before wondering why a push was refused.

`main` requires a pull request with four passing checks, and blocks force-push and deletion.
**Release tags are immutable with no bypass at all** — `v*` cannot be deleted or moved, because the
tag is the version and nuget.org has no delete, which makes it the one git operation that cannot be
undone anywhere. `main` *does* keep an admin bypass, and that is not an inconsistency: required
checks with no bypass deadlock, because a broken CI configuration cannot be fixed if the fix cannot
merge. Tags have no such deadlock.

Pull requests require **zero approvals**. A single maintainer cannot approve their own, so requiring
a review would block every merge; what the rule buys is that the branch goes through CI and leaves a
diff.

**Actions are an allowlist and every one is pinned to a commit SHA.** GitHub-owned actions plus
`NuGet/login`; the verified-creator marketplace is deliberately *not* trusted wholesale. Adding a
third-party action now needs a settings change, and a version bump arrives as a reviewable
Dependabot pull request rather than silently through a moved tag.

**The lesson worth carrying: a control can be active and enforce nothing.** `main` had a ruleset
named "main", marked active, carrying the right rules — and an empty ref-name condition, so it
matched no branches and `main` was wide open. The settings UI showed a green, plausible rule.
`GET /repos/{owner}/{repo}/rules/branches/main` returned `[]`, and that endpoint is the only one
that answers the question. **Check what applies to the branch, not what the rule says.**

## What 1.0 was shipped knowing

Two things were weighed and accepted rather than overlooked. Both are more expensive to change today
than they were yesterday, which is exactly why they are written down.

- **The storage contract may not be finished being learned from.** ~~Only two providers plus the
  in-memory one have ever exercised it. A third is the cheapest way to find out.~~ **Settled, and the
  answer was yes.** SQLite implements it unchanged: 126 facts, no suppressions, no skips, no gap that
  needed a 2.0 conversation or a §11.14-shaped side interface. That is the strongest evidence the
  frozen contract is actually finished, because SQLite is the provider least like the two it was
  designed against — no server, one writer, no `SKIP LOCKED`.

  Two caveats on how far that generalises. It is still three *relational* providers; a document or
  key-value store would test different clauses (the fenced compare-and-set and the recursive cancel
  cascade are the two that assume a lot). And "no gap" means the conformance kit found none — the kit
  is the definition of supported, but it is also the thing a gap would have to be visible to.
- **`Millrace.Storage.Verification` still suppresses `CS1591`** (§11.34) for its ~110 `[Fact]`
  methods. It is the package a community provider author reads, and it is the one with undocumented
  public members. 1.0 does not make that worse — it makes it visible to more people.

Neither is a defect. Both are the kind of thing that is obvious in hindsight and invisible in a
changelog.

## Things that will bite you

**Check CI before merging.** `main` was red for four commits last session while PRs were merged into
it, and the cause — the Angular UI had not built since #88 — was invisible because the build failed
before the tests ran. Two test failures were hiding behind it. `gh pr checks <n>` costs seconds.

**Read that output carefully, though.** Summarising six pull requests at once by piping
`gh pr checks` through `awk` produced a tidy table that said one of them was green when it was
failing on both build jobs, because the check *names* contain spaces and shifted the columns. It was
caught only by trying to merge and being refused. Prefer `--json name,state` when you are counting
rather than reading.

**"It builds clean" still means nothing about whether it renders.** §11.38 learned this from the
Blazor UI and then the docs site collected it again the same week: docfx reported zero warnings, 741
API items, every cross-reference resolving and all 8,850 internal links valid — and the logo was
rendering at 69 pixels square on top of the brand text. The first fix (explicit `width`/`height` on
the SVG) did nothing, because the template injects it client-side as `<img id="logo">` and sizes it
from that id, so a class selector loses on specificity. One screenshot found both facts.

**Open what you built.** [#126](https://github.com/inferenceailab/Millrace/issues/126) has since made
that a check rather than a habit — but only for the dashboard UIs. The docs site has no equivalent,
which is why its logo bug was found by hand.

**Measure the exit code of the thing you are measuring.** Checking whether docfx's
`--warningsAsErrors` actually fails a build, the first attempt read the exit code of `tail` through
a pipe, got 0, and would have concluded the gate does not work — shipping a CI check that never
fires. docfx really exits **−1** while printing *"Build succeeded with warning"*, so its text and
its exit code disagree and only one of them is load-bearing. This is the same failure as the
benchmark numbers in #49: the measurement was wrong in a way the output looked fine about.

**A checker that has never failed is not a checker.** `scripts/check-docs-links.ps1` was verified by
breaking a link on purpose and watching it exit 1. Worth the sixty seconds every time — the test
that asserted a 200 on `{prefix}/ui` passed for months while the page was blank (§11.38).

**A confident comment is not a checked claim, and they are hard to tell apart.** Building the SQLite
provider produced two comments of identical shape, each asserting that a text encoding was
load-bearing for ordering. One was true and covered by a real test:
`Consume_breaks_created_at_ties_by_id` asserts the bookmark tie-break in RFC 4122 byte order, which
canonical lowercase uuid text sorts in, and it is the fact that caught the PostgreSQL/SQL Server
divergence. The other was **false**: fixed-width timestamps were said to be necessary because
trailing-zero stripping would sort `…00.5` after `…00.4999999`. It does not — stripping trailing zeros
is prefix-preserving on a fixed-position fraction, so the variable-width form sorts correctly too, and
swapping the constant leaves all 126 facts green. It was found by trying to falsify the claim, and
only because the sibling claim invited the same test. Fixed width is still right, for the smaller
reason that the stored form should be unambiguous to anything not sharing the formatter. **The habit
worth keeping: when you write "otherwise X breaks", go break it.**

**Tests that drive the real worker share its scheduler — this has now bitten four times**, in four
different ways:

- Polling too tightly starves the worker through the storage lock.
- Polling without advancing the fake clock means the worker never wakes at all.
  `Eventually.ObservedAsync` in `test/Millrace.Tests/Workflows/` exists for both — advance the clock
  inside the predicate.
- A test that reads an instance and writes it back under optimistic concurrency races the worker,
  which checkpoints the same instance and bumps `Revision`.
  `An_instance_pinned_to_an_unregistered_version_fails_loudly` did exactly that and was green on the
  pull request, green locally, and red on Linux `main`. The fix was to stop running a worker the
  test never needed.
- **A worker parked on a wakeup signal never looks again on a frozen fake clock.** `WaitForWorkAsync`
  waits on two things — a storage signal and `Task.Delay(pollDelay, time)` as the ceiling — and
  `IStorageNotifier` documents the signal as droppable, so the delay is the only *guaranteed*
  re-look. A clock nothing advances switches that guarantee off, and any missed signal hangs the test
  to the deadline. `A_delay_defers_the_rest_of_the_flow_until_it_comes_due` did this on a docs-only
  commit (#150). Three of five `Eventually.ObservedAsync` call sites advanced the clock by hand and
  two did not, so the clock is now a **required parameter** the helper advances — the gap is
  unrepresentable rather than remembered.

**Diagnose these by reproducing them.** Enabling the worker and inserting a 750ms delay between the
read and the write reproduced CI's exact exception locally; disabling the worker with the same delay
passed. That pins the cause instead of correlating with it.

**But say so when you could not reproduce it.** #150's diagnosis rests on the notifier contract and
the worker's wait path, *not* on a caught failure: 40 runs of the test alone and 5 of the full suite
were clean on a fast Windows box against a two-core Linux runner with four suites in parallel. The
first repro attempt also inserted its delay on the wrong side of the race, widening the window that
made the bug *less* likely and passing for the wrong reason. Later, running the whole solution's
suites together on a pre-#150 branch did produce one intermittent failure in `Millrace.Tests` — clean
on four subsequent runs, and the test name was not captured, so it is consistent with the flake and
not proof of it. **The condition that matters is whole-solution load, not a loop over one suite**; if
this recurs on `main`, that is where to start.

**"Works on my machine" caused every real defect last session**, without exception:
- `@angular/cli` was never declared as a dependency; `npx` had fetched it locally, so it built here
  and nowhere else.
- Tests that read the repo's own source located it with `[CallerFilePath]`, which
  `ContinuousIntegrationBuild` remaps to `/_/…` — so enabling reproducible builds for packaging
  broke them, and only under `GITHUB_ACTIONS`.
- A workflow test waited on a `FakeTimeProvider` the test never advanced. The worker's poll loop
  sleeps through that clock, so it passed locally on timing luck and hung on a slower machine.

`GITHUB_ACTIONS=true dotnet build` reproduces the CI-only build behaviour locally. Use it before
blaming CI.

**Adding a project to the solution can break a build that has nothing to do with it.** The Blazor
package shells out to `dotnet publish`, and that app references `Millrace.csproj` — so it was a
second MSBuild process writing the same `obj` as the outer solution build. Latent since #120,
because it only fails when the timing lines up; adding `bench/` widened the graph enough that
Windows CI failed with `CS2012` on `Millrace.dll` while Linux passed the same commit. Fixed by
giving the inner publish its own `artifacts-path`. If a UI package ever gains another nested build,
it needs the same treatment.

**A benchmark measures the machine as much as the code.** Three separate measurement bugs in #49
each produced a number that looked entirely plausible — an arrival-rate producer that throttled
itself to 118/s for one system and 200/s for another, a throughput window that opened two-thirds of
the way through the work, and a warmup small enough that every first run looked 3× faster than
steady state. None of them looked wrong in the output; all three were found by asking why a number
was *better* than expected. Suspect the flattering result first.

The 2026-07-30 re-run is the same lesson from the other side: every absolute improved, including for
two comparands whose code had not changed a byte. Nothing was wrong with the code and nothing was
wrong with the harness — the machine was quieter. See the benchmark section above before quoting any
absolute figure from `benchmarks.md`.

**`gh pr edit` does not work on this repository.** It fails with a Projects-classic GraphQL error
because it fetches project cards on the way past. `gh pr create` and `gh pr merge` are fine; editing
a title or body needs `gh api repos/inferenceailab/Millrace/pulls/<n> -X PATCH --input <file>`. And
`jq` is not installed on the development machine, so build the payload with `node -e` instead.

**`gh` has two accounts logged in and the wrong one is active by default.** Work on this repository
belongs to `inferenceailab`; the corporate `u297954_lhgroup` is an Enterprise Managed User and cannot
write here. The trap is that **it reads fine** — `gh pr list`, `gh pr view` and `gh pr checks` all
work under it, so nothing warns you until a mutation is refused with
`GraphQL: Unauthorized: As an Enterprise Managed User, you cannot access this content
(mergePullRequest)`. That message names the PR and means the account. `git push` fails separately and
differently, with `Permission to inferenceailab/Millrace.git denied to u297954_lhgroup` — same cause.

`gh auth switch --user inferenceailab` fixes both, but **it does not stay fixed**: on 2026-07-30 it
reverted twice inside a single session, roughly fifteen minutes apart, with nothing in between that
touched authentication. So this is not a once-per-session check. Treat any 403 or `Unauthorized` from
git or `gh` as this first, before believing anything about repository permissions.

**`docs/backlog.md` was generated with nothing generating it — that is now a check.** It went two
days stale without anything noticing: #151 closed on 2026-07-29 and the mirror still listed it open
until it was regenerated on 2026-07-30 (#158). CLAUDE.md called it a generated file, which read like
a guarantee and was an instruction to a person.

**Closed on 2026-08-02.** The docs job runs `generate-backlog.ps1 -Check`, which renders to memory
and compares against the file, failing with the offending line numbers and the command that fixes
it. Two things had to be dealt with first, and between them they are why it had not been done
already:

- **The generator stamped the generation date into the header**, so regenerating an *unchanged*
  backlog still produced a one-line diff, and a fail-on-any-diff job would have gone red on every
  unrelated push. The stamp is gone. The header now points at
  `git log -1 --format=%cd docs/backlog.md`, which cannot be wrong the way a written date can.
- **Line endings would have failed it on the runner and nowhere else.** There is no `.gitattributes`
  and `core.autocrlf` is on, so the working tree is CRLF here and LF on Linux, while
  `StringBuilder.AppendLine` follows `Environment.NewLine`. The check normalises both sides before
  comparing. A checker that fails for a reason invisible in its own output is worse than no checker.

**It was verified by breaking it**, per the rule further down this section: a row flipped from `done`
to `open` fails with that line quoted against what it should say, a truncated file fails naming the
first missing line, and the restored file passes.

**Know what it couples.** The mirror tracks GitHub issues rather than the working tree, so closing an
issue can turn red a pull request that never touched a file. That is intended — whoever changes
issues is who should re-render — but it is the failure mode to watch, because a check that fails for
something you did not do is the kind that gets switched off. If that starts happening, moving it to a
`main`-only push is the retreat; deleting it is not.

**A suppression hides more than what it was added for.** `CS1591` was suppressed so packaging could
proceed, and it swallowed 52 warnings that were not about missing documentation at all: the SDK's
default `**/*.cs` glob had been reaching into `node_modules`, compiling node-gyp's
`Find-VisualStudio.cs`, and shipping eleven public COM interop types in the Angular package. It
built cleanly, the bundle worked, and nothing else could see it. Both UI projects had already
excluded `node_modules` from `None` items, which is what made the gap look closed.

Two habits fall out. Before trusting a count that justifies a suppression, re-measure it — the
number in #99 was the core package only, and the real one was two and a half times larger. And when
a suppression finally comes out, read what it was hiding rather than assuming it was all the thing
named on the tin.

## Dependency updates, monthly from now on

`.github/dependabot.yml` groups GitHub Actions, NuGet and both npm trees on a monthly schedule. The
first wave landed on 2026-07-29 — six pull requests — and cost more than expected. August's landed
on 2026-08-01 and cost nothing. Both are worth knowing about, because the difference between them is
the thing to budget for.

**The August wave was two patch bumps and no code change**: `Microsoft.Extensions.Hosting`
10.0.0 → 10.0.10 ([#161](https://github.com/inferenceailab/Millrace/pull/161)) and
`@angular/build` + `@angular/cli` 22.0.8 → 22.0.9
([#160](https://github.com/inferenceailab/Millrace/pull/160)), both green on every check, both merged
on 2026-08-02. So July's cost was about **grouped majors**, not about the monthly cadence — a wave
with no major in it is a ten-minute job. Budget a session's tail for the wave that contains a major,
not for every wave.

**One of the two read more alarming than it was, and the reason generalises.**
`Microsoft.Extensions.Hosting` 10.0.0 → 10.0.10 sits in the same file as the comment arguing to hold
`Microsoft.Extensions.*` at 10.0.0 — but that argument is about the *shipping* floor
(`Microsoft.AspNetCore.Components.WebAssembly`, and what raising it would do to consumers of the core
package), while this entry is in the `Testing` ItemGroup and is referenced only by
`test/Millrace.Tests` and `bench/Millrace.Benchmarks`. Same version number, same file, opposite
consequences. **Check which ItemGroup a bumped version lives in before reading it against a comment
about a different one** — `Directory.Packages.props` is labelled by group precisely so this is
answerable, and the answer took one `grep` of the `.csproj` references.

**Grouping `patterns: ['*']` bundles unrelated majors, and one broken member holds the rest
hostage.** The React group arrived as vite 8 + `@vitejs/plugin-react` 6 + TypeScript 7 in a single
pull request that failed. Only TypeScript was at fault, and only for one line: TS 7 raises `TS2882`
for a side-effect import of a non-code asset, so `main.tsx` importing the shared stylesheet no longer
typechecks. `declare module '*.css'` fixed it and the other two majors were fine all along. **When a
grouped bump fails, find out which member did it before rejecting the group.**

**Angular pins the TypeScript it accepts.** `@angular/compiler-cli@22` declares
`peerDependencies.typescript` as `>=6.0 <6.1`, so a TypeScript major is unbuildable there until
Angular widens it. There is now an `ignore` for TypeScript *majors* in the Angular UI only, with a
comment saying to delete it when Angular moves. Check that before assuming the pin is stale.

**`TreatWarningsAsErrors` turns a deprecation into a broken build.** Testcontainers 4.13 obsoleted
the parameterless `PostgreSqlBuilder()`/`MsSqlBuilder()`, and the bump would not compile. Both now
name their image in the constructor — which the SQL Server suite never did, so it had been inheriting
whatever the package defaulted to. Expect any library that deprecates on a minor to do this here.

**Dependabot re-runs and produces a follow-up.** Merging the `microsoft-extensions` group
immediately produced another pull request for two packages the first one missed. Do not assume the
queue is empty because you emptied it. August produced no follow-up — but that was checked minutes
after the merge, which is not long enough to mean much, so the habit stands.

**Three transitive pins are waiting on upstream** — the previous version of this line said two, and
missed the benchmark-only one. Each is in `Directory.Packages.props` with a comment saying when to
remove it: `Microsoft.OpenApi` 2.7.5 (CVE-2026-49451, waiting on ASP.NET Core),
`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 (GHSA-2m69-gcr7-jv3q, waiting on `Microsoft.Data.Sqlite` to
resolve it itself) and `OpenTelemetry.Api` 1.17.0 (GHSA-g94r-2vxg-569j, waiting on WorkflowCore,
under `Benchmark comparands`). A pin that outlives its advisory is a version floor nobody chose.

**You no longer have to remember to check them.** `scripts/check-transitive-pins.ps1` runs in the
`pack` job and fails the build once a pin stops doing anything. It walks the chain each pin
overrides — `Microsoft.Data.Sqlite` → `SQLitePCLRaw.bundle_e_sqlite3` → `SQLitePCLRaw.lib.e_sqlite3`,
and the two shorter ones — asking each link's own nuspec what it declares, which is the question the
absence of a Dependabot pull request does not answer. Only the *relationships* are written down;
every version comes from `Directory.Packages.props` or nuget.org, so a bump of a root package is
picked up without editing the script. It was verified by lowering a pin below what its chain
supplies and watching it fail.

Two things it makes visible that are worth not "tidying". **The pins sit at the lowest fixed version,
not the latest** — `Microsoft.OpenApi` is at 3.9.0 upstream against a pin of 2.7.5, and
`SQLitePCLRaw.lib.e_sqlite3` at 3.53.3 against 2.1.12. That is deliberate: the pin exists to clear an
advisory, and raising it further is a floor nobody chose in the other direction. And **the check is
deliberately asymmetric** — it computes a *lower bound* on what would resolve without the pin, so it
can prove a pin is redundant but never that one is required. That is the right way round for
something that blocks a build.

**A shipping major deserves more than a green tick — but the strictness policy supplies it.**
`Microsoft.Data.SqlClient` went 6.1.4 → 7.0.2, and that one *ships*, inside
`Millrace.Storage.SqlServer`. Its breaking changes turned out to be .NET Framework assembly-identity
ones, so `net10.0` is unaffected. The evidence that mattered: the Linux CI job runs with
`MILLRACE_REQUIRE_SQLSERVER=true`, which **fails rather than skips** when no database is reachable —
so a green Test step is proof the conformance suite really ran against the new driver.

## What earned its keep

Worth knowing before deciding whether to keep paying for them.

- **The conformance kit** repeatedly caught provider divergence, and once caught a bug in its own
  spec when a second provider disagreed (§11 SQL Server byte ordering). It has now done the larger
  job it was built for: a third provider, shaped unlike the other two, was declared supported on
  evidence rather than on review. **Nobody read the SQLite provider's SQL** — 126 executable facts
  did, and they were shown to discriminate rather than merely pass (see `BeginImmediate`). That is
  the difference between a suite and a rubber stamp, and it is why a community provider is a
  realistic proposition.
- **The contract parity check** (§11.22) found that the React UI had shipped without any management
  actions at all, and now fails the build when a contract endpoint no UI reaches.
- **The wire-format tests** (§11.24) found enum values serializing as integers — which had silently
  removed every status colour from the dashboard and inverted the Cancel/Requeue buttons.
- **Refusing to suppress `CS1574`** (§11.31) has now caught four broken crefs — three when
  documentation was first turned on, and one while writing the rest of it: a reference to a type
  from a package the project does not depend on. It compiled, and it read as correct.
- **The provider strictness policy** — an unreachable database fails a CI run rather than skipping it
  — turned out to do a second job nobody designed it for. It is what made a major bump of the SQL
  Server driver safe to accept on evidence rather than on optimism, because a green run *cannot* mean
  the suite was skipped.
- **Warnings-as-errors on restore, specifically.** Adding one package reference surfaced a
  high-severity advisory in a *native* transitive dependency and refused to restore at all. A warning
  would have scrolled past in a session that was reading test output, not restore output.
- **The package smoke test**, which now runs a durable provider end to end out of a `.nupkg`. Every
  other check in the repository verifies the source tree; this one verifies the artifact, and SQLite
  is the first provider it could actually run.

Each of those was invisible to every other test in the repo.

Newly true, and worth the same scrutiny in a few months:

- **`CS1591` is a build error** (§11.34). Every public member outside the conformance kit is
  documented, and a new undocumented one now fails the build rather than joining a backlog. The
  question to ask later is whether that produced comments worth reading or comments written to
  satisfy a compiler.
- **`UiPackagingTests`** asserts each UI assembly exports only its own types, after node-gyp's
  `Find-VisualStudio.cs` shipped eleven public COM interop types inside the Angular package.
- **The parity check caught its author.** Adding the Blazor UI, it failed on the first run because
  the hand-written client was missing `/info` — the same "endpoint added, UI forgotten" failure it
  was built for, this time against the person building it.
- **Publishing the spread, not just the median** (§11.37). Millrace is measured twice per benchmark
  scenario with nothing changed between the two rows, which turns a repeatability control into
  something a reader can check: the medians agree to within 0.4% while individual runs vary by 15%.
  That pair is also the only honest answer to "is this difference real?" — under about 10%, it is
  not.

**The browser suite** (§11.40) is the newest, and the one with the clearest job: it is the only check
in the repository that executes a UI rather than reading it. It has already earned its keep on
something that was not a UI change at all — forcing a transitive dependency to a patched major
(`@hono/node-server`, §11.42) rebuilt the Angular bundle, and the suite is what confirmed the bundle
still rendered rather than merely still compiling.

Two things to watch. It asserts on what the three UIs *share* — hash routes, button labels, a Queue
column — so the question in a few months is whether that shared surface held or whether the tests
quietly became React-shaped. And it covers Chromium only, with no visual regression, so "it renders"
still does not mean "it looks right".

And from the docs site (§11.39), too new to have earned anything but worth watching:

- **`CS1591` finally paid out.** §11.34 made 649 public members documented and nothing published
  them; the question it left open was whether that produced comments worth reading or comments
  written to satisfy a compiler. The generated reference is the answer, and it is now readable in
  public — which is also the forcing function that keeps it honest.
- **The link checker exists because docfx does not check the links it does not own.** It validates
  cross-references and API links; an ordinary `[jobs](jobs.md)` at a renamed page renders as a link
  to nowhere and builds clean.
- **The docs build proves the no-Node claim rather than asserting it.** `-p:SkipUiBuild=true` plus a
  workflow that installs no Node means anything reintroducing the dependency fails the job. It
  passed on a real runner, which is a stronger statement than it passing here.

**The benchmark harness used to run only when someone ran it** — and on 2026-07-30 someone did,
which was the first evidence either way. It had not executed since #49. It still ran, 108 runs with
zero stalls, and it reproduced every job-scenario ratio inside its own noise floor.

**Since 2026-08-02 a CI job runs it on every push**, so that gap cannot reopen. All four scenarios at
the smallest sizes that exercise each — about half a minute, Millrace only, against a PostgreSQL
service container. Drain alone would have been cheaper and was rejected: the latency scenario has a
`PeriodicTimer` and percentile code drain never touches, and workflow has its own engine path, so a
drain-only job would have left three quarters of the harness in exactly the state this was meant to
end. **Its numbers are not measurements** and must not reach `benchmarks.md` — 500 jobs on a shared
runner measures the runner.

What it *earned* is more interesting than that it still works. It separated the numbers that were
about Millrace from the numbers that were about a quiet machine — a distinction no amount of
re-reading the table could have produced, and one that required running it.

It was also credited here with catching "a real defect in itself" — the 21% enqueue cell, #157.
**That credit has been withdrawn.** The harness surfaced a number; the defect was a story told about
the number, and testing it on 2026-08-02 found nothing there (see the benchmark section above). The
spread column did its job, which is to flag a run worth looking at. Reading a cause out of it was
the part nobody checked.

The standing question has narrowed rather than gone. CI now proves the harness *runs*; it says
nothing about whether the published table is still true, because a 500-job run on a shared runner
cannot. So: run it properly before believing the table, and expect the absolutes to have moved even
when nothing did.
