# Weft — repo conventions

## Commit messages

- Plain commit messages only: imperative summary line, optional wrapped body explaining what and why.
- **Never add AI attribution trailers** — no `Co-Authored-By: Claude ...`, no `Claude-Session: ...`, no "Generated with" lines.

## Working on this repo

- Read `ARCHITECTURE.md` first — it is the accepted design. Don't re-litigate decisions in §11 (decision log); append new decisions there instead.
- Core rule: `Weft` (the core package) depends only on the BCL and `Microsoft.Extensions.*`. No database clients, no application frameworks (no ABP, MassTransit, MediatR).
- `net10.0` only, nullable enabled, warnings are errors, package versions live centrally in `Directory.Packages.props`.
- Build with `dotnet build`, test with `dotnet test`.
