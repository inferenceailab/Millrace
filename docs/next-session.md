# Picking this up again

Written 2026-07-26, at the end of the session that closed milestone 0.5.

**This file is not a backlog.** The work lives in [GitHub issues](https://github.com/inferenceailab/Millrace/issues)
and the [project board](https://github.com/users/inferenceailab/projects/1), mirrored for offline
reading in [`backlog.md`](backlog.md). What is here is the part that does *not* fit in an issue:
what needs a human, what will bite you, and what the last session learned the hard way.

Delete anything that stops being true.

## Two things waiting on a person

**`NUGET_API_KEY` is not configured.** `.github/workflows/release.yml` packs, smoke-tests and
publishes on a `v*` tag, and it has never run against a real tag. Without the secret it fails with a
clear message rather than silently skipping — deliberate — but that means the first release will
fail until the secret exists. Deferred on purpose; nothing else depends on it.

**The development machine's Node is below the build floor.** It was v24.14.1, one patch under the
24.15.0 the Angular CLI requires, so `dotnet build` cannot compile the Angular bundle there. Three
separate changes last session had to ship with "CI verifies this, not me", because the parity check
(§11.22) reads the built bundle and it was stale locally. Upgrading Node is a five-minute fix and
worth doing before [#47](https://github.com/inferenceailab/Millrace/issues/47) adds a third UI
bundle to the same build.

## What 1.0 still needs

Detail is in the issues; the ordering argument is not.

| | Why it is where it is |
|---|---|
| [#47](https://github.com/inferenceailab/Millrace/issues/47) Blazor UI | The biggest remaining feature. §11.23 settled that it is **native Blazor over the C# contract types**, not a web component — read that decision before starting, because the spike that produced it is in `spikes/ui-webcomponents/` and its README explains what was measured. |
| [#48](https://github.com/inferenceailab/Millrace/issues/48) Docs site | Now that packages can actually be installed, this is what makes them usable. |
| [#49](https://github.com/inferenceailab/Millrace/issues/49) Benchmarks | Positioning against Hangfire and WorkflowCore. Nothing depends on it. |
| [#77](https://github.com/inferenceailab/Millrace/issues/77) Nested sagas | **Design already settled** in §11.29 — inner unwinds first, then propagates outward. Implementation only. Two consequences recorded there: compensation becomes a *stack* rather than a list, and a failed inner compensation must suspend the whole instance. |
| [#99](https://github.com/inferenceailab/Millrace/issues/99) Documentation | 164 public members have no XML comment. `CS1591` is suppressed in `Directory.Build.props`; **that suppression is the deliverable to delete.** Do it per area, not in one pass — 164 comments written at once become 164 restatements of the method name. |

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

## What earned its keep

Worth knowing before deciding whether to keep paying for them.

- **The conformance kit** repeatedly caught provider divergence, and once caught a bug in its own
  spec when a second provider disagreed (§11 SQL Server byte ordering).
- **The contract parity check** (§11.22) found that the React UI had shipped without any management
  actions at all, and now fails the build when a contract endpoint no UI reaches.
- **The wire-format tests** (§11.24) found enum values serializing as integers — which had silently
  removed every status colour from the dashboard and inverted the Cancel/Requeue buttons.

Each of those was invisible to every other test in the repo.
