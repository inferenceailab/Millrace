# Picking this up again

Written 2026-07-26, at the end of the session that closed milestone 0.5, and revised later the same
day — after `0.1.0-alpha.1` was published and [#99](https://github.com/inferenceailab/Millrace/issues/99)
closed.

**This file is not a backlog.** The work lives in [GitHub issues](https://github.com/inferenceailab/Millrace/issues)
and the [project board](https://github.com/users/inferenceailab/projects/1), mirrored for offline
reading in [`backlog.md`](backlog.md). What is here is the part that does *not* fit in an issue:
what needs a human, what will bite you, and what the last session learned the hard way.

Delete anything that stops being true.

## Start here

Nested sagas ([#119](https://github.com/inferenceailab/Millrace/pull/119), §11.35) and the Blazor UI
([#120](https://github.com/inferenceailab/Millrace/pull/120), §11.36) are both merged. `main` builds
clean and the whole suite passes.

**[#49](https://github.com/inferenceailab/Millrace/issues/49) is done** ([#124](https://github.com/inferenceailab/Millrace/pull/124),
§11.37): `bench/` holds a harness, [`benchmarks.md`](benchmarks.md) holds the numbers and the
caveats. Millrace is 1.8× Hangfire enqueueing, 3.2–3.4× draining, 2.5–5× on median latency, and 5.3×
WorkflowCore on workflow instances.

**Two of 1.0's three remaining issues are now [#48](https://github.com/inferenceailab/Millrace/issues/48)
and [#122](https://github.com/inferenceailab/Millrace/issues/122)** — see the table below. #47 was
closed on its own terms and the layout work it never asked for became #122; what that issue records
and the parity check does not is that `SignalAsync` and `GetInfoAsync` are on the Blazor client but
called from no page, so an operator cannot raise a signal from that dashboard at all. §11.36's
parity claim is one-directional — a bundle never mentioning an endpoint does not call it — and both
literals are present, so it passes.

## Nothing is blocking on a person

Publishing credentials were the last blocker and they are done, proven by a real release rather than
by configuration looking right. Nothing waits on anyone now — 1.0 is two issues, and only
[#48](https://github.com/inferenceailab/Millrace/issues/48) wants a decision before it wants code.

## Releasing

**It works, and it has run for real.** `v0.1.0-alpha.1` put all eight packages on nuget.org on
2026-07-26, first attempt. Tag `vX.Y.Z` and the rest is automatic: pack, smoke-test the built
artifacts, exchange a GitHub OIDC token for a one-hour nuget.org key (§11.33), push, create the
GitHub release. No secret is stored, so nothing expires and nothing needs rotating.

Three things to know before the next tag.

**The tag is the version, and nuget.org has no delete** — only unlist. A prerelease tag is the cheap
way to exercise a change to the release path, and the workflow infers prerelease status from the
hyphen, so `v1.2.3-rc.1` marks itself correctly without anyone remembering to.

**`release.yml`'s actions only run on a tag**, so CI never exercises them. They went from v4 to
`checkout@v7`, `setup-dotnet@v6`, `setup-node@v7` and `upload-artifact@v7` on 2026-07-26 and have
not run since — the next release is their first real test. This is the same blind spot §11.31
identified when it moved packing onto every push, and it has not been closed for this half.

**The trust policy is pinned to the repository and to the file name `release.yml`.** Renaming or
moving that file breaks publishing, which is the trade §11.33 made deliberately: authority belongs
to the thing that runs.

## What 1.0 still needs

Detail is in the issues; the ordering argument is not.

| | Why it is where it is |
|---|---|
| [#122](https://github.com/inferenceailab/Millrace/issues/122) Blazor layout | The Blazor UI shipped in [#120](https://github.com/inferenceailab/Millrace/pull/120) as **one page**, where the other two have six views. Codeable today, and the issue names the two client methods no page reaches. |
| [#48](https://github.com/inferenceailab/Millrace/issues/48) Docs site | Now that packages can actually be installed, this is what makes them usable. **Still not codeable** — nobody has chosen a generator (docfx, Statiq, something else), and that is a §11 decision rather than a task. It is now the only thing in 1.0 that cannot be started cold. |

## Things that will bite you

**Check CI before merging.** `main` was red for four commits last session while PRs were merged into
it, and the cause — the Angular UI had not built since #88 — was invisible because the build failed
before the tests ran. Two test failures were hiding behind it. `gh pr checks <n>` costs seconds.

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

One that has not earned anything yet: **the benchmark harness only runs when someone runs it.** It
compiles in CI and never executes there, so it will rot the way any unexecuted code does. The
question in a few months is whether the numbers in `benchmarks.md` still reproduce, and the cheapest
way to find out is to run it before believing the table.
