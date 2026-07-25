# Millrace — repo conventions

## Commit messages

- Plain commit messages only: imperative summary line, optional wrapped body explaining what and why.
- **Never add AI attribution trailers** — no `Co-Authored-By: Claude ...`, no `Claude-Session: ...`, no "Generated with" lines.

## Working on this repo

- Read `ARCHITECTURE.md` first — it is the accepted design. Don't re-litigate decisions in §11 (decision log); append new decisions there instead.
- Core rule: `Millrace` (the core package) depends only on the BCL and `Microsoft.Extensions.*`. No database clients, no application frameworks (no ABP, MassTransit, MediatR).
- `net10.0` only, nullable enabled, warnings are errors, package versions live centrally in `Directory.Packages.props`.
- Build with `dotnet build`, test with `dotnet test`.

## Planning

- Milestones, epics and user stories live in GitHub: Issues + Milestones + project board
  [#1](https://github.com/users/inferenceailab/projects/1). That is the source of truth.
- `docs/backlog.md` is a **generated mirror** for offline reading and diff review. Change the
  issues, then regenerate the mirror — never hand-edit it.
- Design questions that must be settled before code go in `docs/open-questions.md` and get a
  `type:spike` issue. Once answered, the answer moves into `ARCHITECTURE.md` §11 and the entry
  leaves the open-questions doc.
