# The documentation site

Source for <https://inferenceailab.github.io/Millrace/>, built with [docfx](https://dotnet.github.io/docfx/).
Published by [`.github/workflows/docs.yml`](../../.github/workflows/docs.yml) on every push to
`main`, and built (without publishing) on every pull request.

## Building it locally

```bash
dotnet tool restore                        # once — docfx is pinned in .config/dotnet-tools.json
dotnet docfx docs/site/docfx.json --serve
```

Then open <http://localhost:8080>. Drop `--serve` to build without serving.

`dotnet docfx` with no verb does metadata *and* build. `dotnet docfx metadata docs/site/docfx.json`
regenerates only the API reference, which is the slow half.

## What is generated

- **`api/`** — the API reference, extracted from the XML documentation comments on every public
  member. Generated on each build and **git-ignored**; never edit it, and never commit it.
- **`_site/`** — the rendered site. Also git-ignored.

Everything else here is written by hand.

Metadata generation passes `-p:SkipUiBuild=true`, so building the docs needs the .NET SDK and
**no Node** — the UI projects contribute their C# without compiling their bundles. Keep it that way:
the docs workflow deliberately installs no Node toolchain, so anything that reintroduces the
dependency fails there rather than silently growing one.

## Adding a page

1. Write the markdown under `guide/`.
2. Add it to [`guide/toc.yml`](guide/toc.yml) — a page absent from the table of contents is
   reachable only by direct link.
3. Build and **open it in a browser**. ARCHITECTURE.md §11.38 is about exactly this: every check in
   this repository is static, and the logo on this site rendered at 69px over the brand text through
   a build that reported zero warnings.

## What the build checks

- **`--warningsAsErrors`** — a broken `<xref:>` or an unresolvable link fails the build instead of
  shipping as a dead link. Note that docfx still prints *"Build succeeded with warning"* in that
  case; the non-zero exit code is what CI keys on.
- **[`scripts/check-docs-links.ps1`](../../scripts/check-docs-links.ps1)** — walks the rendered HTML
  and resolves every internal `href`. docfx validates the cross-references it owns, but an ordinary
  markdown link to a renamed page renders as a link to nowhere and builds clean.

## Conventions

- Link between guide pages with ordinary relative markdown links (`[jobs](jobs.md)`).
- Link into the API reference with `<xref:Millrace.IJobClient>`, which resolves to the generated
  page and fails the build when the member stops existing. Prefer it over a hand-written path.
- Every code sample should be real. Each one here was checked against the source it documents —
  `WorkflowInstanceState.Waiting` got into a draft and does not exist.
