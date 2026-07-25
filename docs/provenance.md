# Provenance

Millrace did not start here. It was designed and partly built as a local prototype under the
working name **Weft**, then imported into this repository on **2026-07-25**.

## Source

| | |
|---|---|
| Prototype path | `C:\development\EXPERIMENTS\abp.wexflow\weft` (local only — **no git remote**) |
| Imported from commit | `f2a2311` — *Note the remaining phase 0.2 work and its open design questions* |
| Commits in prototype | 11, from `baf506b` (rule out ABP) to `f2a2311` |
| Verified at import | `dotnet build` clean (SDK 10.0.301, 0 warnings); `Millrace.Tests` 181/181 passing; PostgreSQL conformance 70/70 against `postgres:17` |

The prototype history exists only on this machine. The code import preserves those 11 commits by
fetching directly from the prototype working copy rather than squashing them into a single
snapshot commit, so authorship and the reasoning trail survive.

## What changed on import

- **Renamed Weft → Millrace** throughout: namespaces, package ids, type names
  (`WeftStorageException` → `MillraceStorageException`), DI entry point (`AddWeft` → `AddMillrace`),
  dashboard mount (`MapWeftDashboard("/weft")` → `MapMillraceDashboard("/millrace")`).
  Rationale and evidence: `ARCHITECTURE.md` §11.10.
- **`.gitignore` replaced.** This repo was scaffolded with GitHub's Python template; the project is
  .NET, so the prototype's Visual Studio template replaced it.
- **`docs/next-steps.md` retired.** Its content became GitHub milestones, epics and user stories
  (mirrored in `docs/backlog.md`), plus `docs/open-questions.md` for the unsettled design calls.
  It was a single-file plan; keeping it alongside an issue tracker would have created two
  competing sources of truth.
- **`ARCHITECTURE.md` §11.1 struck through** rather than deleted — the original name decision and
  the reason it failed are both part of the record.

## Naming history

The prototype was called Weft because a weft is the thread woven across the warp — threads (jobs)
becoming fabric (workflows). That name was dropped at import. `Weft` the bare NuGet id was free,
but the `Weft.*` namespace was not: two unrelated .NET projects already ship under it
(`Weft.Core`/`Weft.Server`/`Weft.Loro` by StrangeDaysTech, a CRDT library whose
`Weft.Server.Persistence.<Provider>` layout is near-identical in shape to our planned
`Weft.Storage.<Provider>`; and `WeftDotNet.*` by AboimPinto). `Batchflow` was considered and
rejected for the same class of reason — `BatchFlow` is taken on NuGet by a dormant 2016 .NET
batch-processing library, an adjacent enough domain to confuse.

`Millrace` was verified on 2026-07-25 to have **zero** packages on NuGet, leaving the whole
`Millrace.*` prefix reservable, which the multi-package layout in §9 requires.
