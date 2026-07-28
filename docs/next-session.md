# Picking this up again

Written 2026-07-26 at the end of the session that closed milestone 0.5, and rewritten on 2026-07-28
when the 1.0 milestone emptied.

**This file is not a backlog.** The work lives in [GitHub issues](https://github.com/inferenceailab/Millrace/issues)
and the [project board](https://github.com/users/inferenceailab/projects/1), mirrored for offline
reading in [`backlog.md`](backlog.md). What is here is the part that does *not* fit in an issue:
what needs a human, what will bite you, and what the last session learned the hard way.

Delete anything that stops being true.

## Start here

**Millrace 1.0.0 is released.** Nine packages on nuget.org, the milestone is closed, and the
storage and v1 REST contracts are now stable promises (§11.41) rather than working agreements.
`v1.0.0-rc.1` went out first to exercise the publishing path; `v1.0.0` followed the same day.

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

`main` builds clean, the whole suite passes, the docs deploy on every push, and **no non-epic issue
is open**. There is no backlog: the next session picks what 1.1 or 2.0 should be, rather than
finishing something.

## Nothing is blocking on a person

Publishing credentials were the last blocker and they are done, proven by three real releases rather
than by configuration looking right. GitHub Pages needed enabling once and now is. Nothing waits on
anyone.

What *does* want a person is direction: with 1.0 out and no backlog, the next decision is what 1.x
is for. The obvious candidate is the SQLite provider, because it is the cheapest test of whether the
contract just frozen is actually finished — but that is a judgement about priorities, not a task
waiting to be picked up.

## Releasing

**It works, and it has now run twice.** `v0.1.0-alpha.1` put eight packages on nuget.org on
2026-07-26; `v1.0.0-rc.1` put **nine** there on 2026-07-28, first attempt again. Tag `vX.Y.Z` and
the rest is automatic: pack, smoke-test the built artifacts, exchange a GitHub OIDC token for a
one-hour nuget.org key (§11.33), push, create the GitHub release. No secret is stored, so nothing
expires and nothing needs rotating.

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

**Verify the published packages, not just the green job.** After both tags all nine were confirmed
indexed on nuget.org and then installed into a throwaway console app that enqueued a job — about two
minutes, and the only thing that actually proves a consumer can use what shipped. `dotnet nuget
push` reporting success means the bytes were accepted, not that they work.

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
has no delete. Any future version bump needs the same ordering: docs merged, then tag.

**The trust policy is pinned to the repository and to the file name `release.yml`.** Renaming or
moving that file breaks publishing, which is the trade §11.33 made deliberately: authority belongs
to the thing that runs.

## What 1.0 was shipped knowing

Two things were weighed and accepted rather than overlooked. Both are more expensive to change today
than they were yesterday, which is exactly why they are written down.

- **The storage contract may not be finished being learned from.** It grew clause 7 in §11.16 and
  the compensation-recovery surface in §11.30, both discovered by *building on it*. Only two
  providers plus the in-memory one have ever exercised it. **A third is the cheapest way to find
  out**, and SQLite was already next on the roadmap. If it finds a gap, that gap is now a 2.0
  conversation or a new interface (§11.14's shape), not an edit.
- **`Millrace.Storage.Verification` still suppresses `CS1591`** (§11.34) for its ~110 `[Fact]`
  methods. It is the package a community provider author reads, and it is the one with undocumented
  public members. 1.0 does not make that worse — it makes it visible to more people.

Neither is a defect. Both are the kind of thing that is obvious in hindsight and invisible in a
changelog.

## Things that will bite you

**Check CI before merging.** `main` was red for four commits last session while PRs were merged into
it, and the cause — the Angular UI had not built since #88 — was invisible because the build failed
before the tests ran. Two test failures were hiding behind it. `gh pr checks <n>` costs seconds.

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

**Tests that drive the real worker share its scheduler — this has now bitten three times.** The
newest: `An_instance_pinned_to_an_unregistered_version_fails_loudly` reads an instance and writes it
back under optimistic concurrency, while its host runs a worker polling every 5ms that checkpoints
the same instance and bumps `Revision`. Green on the pull request, green locally, red on Linux
`main`. The fix was to stop running a worker the test never needed. **Diagnose these by reproducing
them**: enabling the worker and inserting a 750ms delay between the read and the write reproduced
CI's exact exception locally, and disabling the worker with the same delay passed — which pins the
cause instead of correlating with it.

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

**Tests that drive the real worker share its scheduler.** Two separate fixes to the same test taught
this twice: polling too tightly starves the worker through the storage lock, and polling without
advancing the fake clock means the worker never wakes at all. `Eventually.ObservedAsync` in
`test/Millrace.Tests/Workflows/` exists for this — advance the clock inside the predicate.

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

**`gh pr edit` does not work on this repository.** It fails with a Projects-classic GraphQL error
because it fetches project cards on the way past. `gh pr create` and `gh pr merge` are fine; editing
a title or body needs `gh api repos/inferenceailab/Millrace/pulls/<n> -X PATCH --input <file>`. And
`jq` is not installed on the development machine, so build the payload with `node -e` instead.

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

## What earned its keep

Worth knowing before deciding whether to keep paying for them.

- **The conformance kit** repeatedly caught provider divergence, and once caught a bug in its own
  spec when a second provider disagreed (§11 SQL Server byte ordering).
- **The contract parity check** (§11.22) found that the React UI had shipped without any management
  actions at all, and now fails the build when a contract endpoint no UI reaches.
- **The wire-format tests** (§11.24) found enum values serializing as integers — which had silently
  removed every status colour from the dashboard and inverted the Cancel/Requeue buttons.
- **Refusing to suppress `CS1574`** (§11.31) has now caught four broken crefs — three when
  documentation was first turned on, and one while writing the rest of it: a reference to a type
  from a package the project does not depend on. It compiled, and it read as correct.

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
in the repository that executes a UI rather than reading it. Two things to watch. It asserts on what
the three UIs *share* — hash routes, button labels, a Queue column — so the question in a few months
is whether that shared surface held or whether the tests quietly became React-shaped. And it covers
Chromium only, with no visual regression, so "it renders" still does not mean "it looks right".

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

One that has not earned anything yet: **the benchmark harness only runs when someone runs it.** It
compiles in CI and never executes there, so it will rot the way any unexecuted code does. The
question in a few months is whether the numbers in `benchmarks.md` still reproduce, and the cheapest
way to find out is to run it before believing the table.
