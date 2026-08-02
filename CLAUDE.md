# Millrace — repo conventions

## Commit messages

- Plain commit messages only: imperative summary line, optional wrapped body explaining what and why.
- **Never add AI attribution trailers** — no `Co-Authored-By: Claude ...`, no `Claude-Session: ...`, no "Generated with" lines.

## Working on this repo

- Read `ARCHITECTURE.md` first — it is the accepted design. Don't re-litigate decisions in §11 (decision log); append new decisions there instead.
- Core rule: `Millrace` (the core package) depends only on the BCL and `Microsoft.Extensions.*`. No database clients, no application frameworks (no ABP, MassTransit, MediatR).
- `net10.0` only, nullable enabled, warnings are errors, package versions live centrally in `Directory.Packages.props`.
- Build with `dotnet build`, test with `dotnet test`.
- Building needs **Node 22.22.3+ or 24.15.0+** as well as the .NET SDK — the floor the Angular CLI
  enforces, and it refuses to run below it with no npm output, so the failure reads as a bare exit
  code. Both `Millrace.Dashboard.Ui.React` and `Millrace.Dashboard.Ui.Angular` compile their
  embedded bundles during the .NET build. `-p:SkipUiBuild=true` skips it. Consumers of the published
  packages never need Node — keep it that way, and `scripts/smoke-test-packages.ps1` is what proves
  it.
- The UI bundle is generated, never committed. Change `ui/src`, not `ui/dist`.

## Planning

- Milestones, epics and user stories live in GitHub: Issues + Milestones + project board
  [#1](https://github.com/users/inferenceailab/projects/1). That is the source of truth.
- `docs/backlog.md` is a **generated mirror** for offline reading and diff review. Change the
  issues, then run `pwsh ./scripts/generate-backlog.ps1` — never hand-edit it. The docs workflow
  runs the same script with `-Check` and fails when the mirror is stale, so this is enforced rather
  than remembered. It reads GitHub, not the working tree, so closing an issue can turn an unrelated
  pull request red; regenerating is the fix and the failure output says so.
- Design questions that must be settled before code go in `docs/open-questions.md` and get a
  `type:spike` issue. Once answered, the answer moves into `ARCHITECTURE.md` §11 and the entry
  leaves the open-questions doc.
