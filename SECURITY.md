# Security policy

## Reporting a vulnerability

**Please do not open a public issue.** Report privately through
[GitHub Security Advisories](https://github.com/inferenceailab/Millrace/security/advisories/new),
which is enabled on this repository.

Expect an acknowledgement within a few days. If a report is valid you will be credited in the
advisory unless you would rather not be.

## Supported versions

| Version | Supported |
|---|---|
| 1.x | Yes |
| 0.x prereleases | No — upgrade to 1.x |

The storage contract and the `/api/v1` REST contract are stable within 1.x (ARCHITECTURE.md §11.41),
so upgrading inside that range is not a breaking change.

## What is in scope

Millrace is a library you host, not a service we operate, so the interesting boundaries are the ones
your application exposes:

- **The dashboard's REST contract and middleware** (`Millrace.Dashboard`) — anything reachable over
  HTTP, including authorization bypass, and anything that leaks job arguments across a tenant
  boundary.
- **The dashboard UIs** — XSS through job arguments, error messages, or workflow data rendered in a
  page.
- **The storage providers** — SQL injection, or a tenant filter that can be escaped.
- **Job invocation** — the expression capture and its deserialization, which turns stored data into
  a method call.

### Two things that are working as designed

Both look alarming and are documented behaviour rather than vulnerabilities. Report them if you can
show the guarantee is *broken*; not merely that it exists.

- **The dashboard exposes job arguments to anyone authorized to see it.** That is what an operations
  dashboard is for. It fails closed — outside `Development` it is a startup error until an
  authorization hook is registered (§11.13) — and `AllowAnonymousAccessInsecure` is named to be hard
  to enable by accident.
- **Jobs execute arbitrary serialized method calls.** Anyone who can write to the jobs table can run
  code in your worker process. Treat the storage connection as a trust boundary equal to the
  application itself.

## What is out of scope

- Vulnerabilities in the .NET runtime or in `Microsoft.Extensions.*`. Report those to their owners.
- Findings against build-time-only dependencies that never ship. The UI packages embed a **prebuilt**
  bundle, so npm packages used to build them are not present in what a consumer installs —
  `scripts/smoke-test-packages.ps1` proves the published packages need no Node at all.
- Anything requiring an attacker who already has write access to your database or your deployment.

## How releases are published

Worth knowing if you are assessing supply-chain risk:

- **No long-lived publishing credential exists.** nuget.org validates a GitHub OIDC token against a
  policy naming this repository and the `release.yml` workflow, then issues a key valid for one hour
  and usable once (§11.33). There is no stored secret to leak, and nothing to rotate.
- **The version comes from the git tag alone**, and release tags are immutable — a ruleset blocks
  deleting or moving `v*`, with no bypass.
- **The artifact is tested before it is pushed.** `release.yml` packs, then installs the built
  packages into a throwaway project and runs a job from them, and only then requests the publishing
  key.
- **Packages are built from a commit on `main`**, which requires a pull request with passing checks.
- **Workflows may only use an allowlist of actions** — GitHub-owned ones, plus `NuGet/login`. The
  verified-creator marketplace is deliberately *not* trusted wholesale, so a compromised third-party
  action cannot be introduced without a repository settings change.
