<#
.SYNOPSIS
    Installs the built packages into a throwaway project and runs a job through them.

.DESCRIPTION
    Everything else verifies the source tree. This verifies the artifact, which is what consumers
    actually get, and it is the only check that can catch the failures packaging introduces: a
    missing dependency that the solution supplied by project reference, an embedded resource that
    did not survive `dotnet pack`, or a package that restores but cannot run.

    It also proves the promise the UI packages rest on — that a consumer never needs Node. The
    project built here has no Node toolchain involvement at all, and it serves the dashboard UI out
    of the package's embedded bundle.

.PARAMETER Packages
    Folder holding the .nupkg files, as produced by `dotnet pack -o`.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Packages,
    [string] $Version = '0.1.0-alpha'
)

$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) "millrace-smoke-$([guid]::NewGuid().ToString('n'))"
New-Item -ItemType Directory -Path $work | Out-Null

try {
    Write-Host "Consuming packages from $Packages in $work"

    # A feed containing ONLY the built packages plus nuget.org: if a package forgot to declare a
    # dependency, restore fails here rather than in a consumer's build six months from now.
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="built" value="$Packages" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content (Join-Path $work 'NuGet.config')

    Push-Location $work
    dotnet new console --framework net10.0 --name Consumer | Out-Null
    Push-Location (Join-Path $work 'Consumer')

    foreach ($package in @('Millrace', 'Millrace.Dashboard', 'Millrace.Dashboard.Ui.React', 'Millrace.Testing')) {
        dotnet add package $package --version $Version --no-restore | Out-Null
    }

    # Deliberately the harness rather than a bare host: it is the package a consumer's tests take,
    # so exercising it here checks the same path their CI will.
    @'
using Microsoft.Extensions.DependencyInjection;
using Millrace;
using Millrace.Testing;

var ran = false;

await using var host = MillraceTestHost.Create(services =>
    services.AddSingleton<IWork>(new Work(() => ran = true)));

await host.Jobs.EnqueueAsync<IWork>(w => w.RunAsync());
await host.RunUntilIdleAsync();

if (!ran)
{
    Console.Error.WriteLine("FAIL: the job never ran.");
    return 1;
}

// The embedded UI bundle has to survive packing: an empty glob produces a package that installs
// and then serves nothing, which no compile-time check would catch.
var ui = typeof(MillraceReactUiServiceCollectionExtensions).Assembly
    .GetManifestResourceNames()
    .Where(n => n.StartsWith("ui/", StringComparison.Ordinal))
    .ToList();

if (ui.Count == 0)
{
    Console.Error.WriteLine("FAIL: the React package shipped without its bundle.");
    return 1;
}

Console.WriteLine($"OK: job ran, and the UI package carries {ui.Count} embedded assets.");
return 0;

public interface IWork
{
    Task RunAsync();
}

public sealed class Work(Action onRun) : IWork
{
    public Task RunAsync()
    {
        onRun();
        return Task.CompletedTask;
    }
}
'@ | Set-Content 'Program.cs'

    dotnet run --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke test failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Package smoke test passed.'
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    Pop-Location -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
