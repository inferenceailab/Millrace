# Benchmarks

The harness behind [`docs/benchmarks.md`](../docs/benchmarks.md), which is where the numbers, the
method and the caveats live. This file is just how to run it.

```
cd bench && docker compose up -d
dotnet run -c Release --project Millrace.Benchmarks -- --all
```

The first command starts a PostgreSQL on port 5434 — not 5432, and not the sample's 5433, so
nothing you already have running is disturbed. The second prints a markdown table and takes roughly
twenty minutes at the published defaults.

`--help` lists every knob. The useful ones while iterating:

```
--scenario drain --system millrace,hangfire --jobs 2000 --repeats 1   # one cell, quickly
--json results.json                                                   # every repeat, not just medians
```

Nothing here ships. It is not referenced by any package, it is excluded from `dotnet test`, and
`Hangfire` and `WorkflowCore` appear in `Directory.Packages.props` under a label saying so.

## Before you trust a number you produced

Close everything else first. The measurement is wall-clock throughput on one machine, so another
build, a test run or a browser will move it — this is why the harness publishes the spread across
repeats, and why a spread above about 10% means the run should be repeated rather than reported.
