<#
.SYNOPSIS
    Fails when a transitive pin in Directory.Packages.props is no longer needed.

.DESCRIPTION
    Some entries in Directory.Packages.props exist only to force a transitive dependency past a
    security advisory, using CentralPackageTransitivePinningEnabled. Each is a version floor this
    repository chose on someone else's behalf, and each is supposed to be removed once upstream
    resolves the patched version by itself.

    "Supposed to" was the problem. The removal condition lived in a code comment, which made it a
    thing to remember during a dependency wave rather than a thing that announces itself — and a
    pin that outlives its advisory is a floor nobody chose, silently constraining every consumer of
    the packages this repository ships.

    The condition is mechanical, which is why this can be a check at all: walk the dependency chain
    the pin overrides, starting from a package this repository references directly, and ask what
    version the chain demands on its own. If that is already at or above the pin, the pin is doing
    nothing and should go.

    Two simplifications, both deliberate and both conservative:

    - A nuspec declares dependencies per target framework. This takes the highest version declared
      for the target across every group rather than resolving the graph for net10.0 specifically.
      For these chains the groups agree; where they would not, taking the highest is the reading
      that is least likely to claim a still-needed pin is retirable.
    - A dependency version in a nuspec is a minimum, not an exact version, and NuGet resolves the
      highest demanded across the whole graph. So what this computes is a lower bound on what
      would be resolved without the pin. A lower bound at or above the pin proves the pin is
      redundant; below it does not prove the pin is needed, only that this chain alone does not
      supply it. That asymmetry is the right way round for a check that blocks a build.

.PARAMETER PackagesProps
    Path to Directory.Packages.props. Defaults to the one in this repository.

.EXAMPLE
    pwsh ./scripts/check-transitive-pins.ps1
#>
[CmdletBinding()]
param(
    [string]$PackagesProps = (Join-Path $PSScriptRoot '..' 'Directory.Packages.props')
)

$ErrorActionPreference = 'Stop'

# Each pin, and the chain it exists to override. The chain starts at a package this repository
# references directly and ends at the pinned package. Only the relationship is declared here —
# every version is read from Directory.Packages.props or from nuget.org, so a Dependabot bump of a
# root package is picked up without anyone editing this table. A table that had to be maintained by
# hand would rot exactly the way the comments it replaces did.
$pins = @(
    @{
        Pin      = 'Microsoft.OpenApi'
        Advisory = 'CVE-2026-49451 (GHSA-v5pm-xwqc-g5wc)'
        Chain    = @('Microsoft.AspNetCore.OpenApi', 'Microsoft.OpenApi')
    },
    @{
        Pin      = 'SQLitePCLRaw.lib.e_sqlite3'
        Advisory = 'GHSA-2m69-gcr7-jv3q'
        Chain    = @('Microsoft.Data.Sqlite', 'SQLitePCLRaw.bundle_e_sqlite3', 'SQLitePCLRaw.lib.e_sqlite3')
    },
    @{
        Pin      = 'OpenTelemetry.Api'
        Advisory = 'GHSA-g94r-2vxg-569j'
        Chain    = @('WorkflowCore', 'OpenTelemetry.Api')
    }
)

if (-not (Test-Path -LiteralPath $PackagesProps)) {
    Write-Error "Not found: $PackagesProps"
}

$props = [xml](Get-Content -LiteralPath $PackagesProps -Raw)
$declared = @{}
foreach ($pv in $props.SelectNodes('//PackageVersion')) {
    $declared[$pv.Include] = $pv.Version
}

function Get-LowerBound {
    <#
        A nuspec dependency version is a range. "2.1.11" means >= 2.1.11; "[2.1.11]" means exactly
        that; "[1.0,2.0)" means >= 1.0. All three have the same lower bound, which is the only part
        this check needs.
    #>
    param([string]$Range)

    if ([string]::IsNullOrWhiteSpace($Range)) { return $null }
    $trimmed = $Range.Trim().TrimStart('[', '(').TrimEnd(']', ')')
    $lower = ($trimmed -split ',')[0].Trim()
    if ([string]::IsNullOrWhiteSpace($lower)) { return $null }
    return $lower
}

function ConvertTo-ComparableVersion {
    # Prerelease suffixes do not order under [version]; the release part is enough to compare
    # against a pin, which is always a stable version.
    param([string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version)) { return $null }
    $release = ($Version -split '-')[0]
    [version]$parsed = $null
    if ([version]::TryParse($release, [ref]$parsed)) { return $parsed }
    return $null
}

function Get-Nuspec {
    param([string]$Id, [string]$Version)

    $lower = $Id.ToLowerInvariant()
    $uri = "https://api.nuget.org/v3-flatcontainer/$lower/$Version/$lower.nuspec"
    try {
        return [xml](Invoke-RestMethod -Uri $uri -MaximumRetryCount 3 -RetryIntervalSec 2)
    }
    catch {
        throw "Could not read the nuspec for $Id $Version from nuget.org: $($_.Exception.Message)"
    }
}

function Get-DeclaredDependency {
    # The highest lower bound declared for $Id across every dependency group. See the two
    # simplifications in the description.
    param([xml]$Nuspec, [string]$Id)

    $versions = @()
    foreach ($dep in $Nuspec.SelectNodes('//*[local-name()="dependency"]')) {
        if ($dep.id -ne $Id) { continue }
        $bound = Get-LowerBound $dep.version
        if ($bound) { $versions += $bound }
    }

    if ($versions.Count -eq 0) { return $null }
    return ($versions | Sort-Object -Property @{ Expression = { ConvertTo-ComparableVersion $_ } } | Select-Object -Last 1)
}

$retirable = @()
$unresolved = @()

Write-Host "Transitive pins declared in $([IO.Path]::GetFileName($PackagesProps)):"
Write-Host ''

foreach ($pin in $pins) {
    $pinnedVersion = $declared[$pin.Pin]
    if (-not $pinnedVersion) {
        # The pin was removed but this table still lists it — that is a stale table, and saying so
        # is more useful than passing quietly.
        $unresolved += "$($pin.Pin) is listed here but has no PackageVersion entry. Remove it from `$pins."
        continue
    }

    $root = $pin.Chain[0]
    $rootVersion = $declared[$root]
    if (-not $rootVersion) {
        $unresolved += "$($pin.Pin): chain starts at $root, which has no PackageVersion entry."
        continue
    }

    # Walk the chain, asking each link what it declares for the next.
    $currentId = $root
    $currentVersion = $rootVersion
    $steps = @()
    $broken = $null

    for ($i = 1; $i -lt $pin.Chain.Count; $i++) {
        $nextId = $pin.Chain[$i]
        $nuspec = Get-Nuspec -Id $currentId -Version $currentVersion
        $nextVersion = Get-DeclaredDependency -Nuspec $nuspec -Id $nextId

        if (-not $nextVersion) {
            # The shape of the graph changed. That is not "the pin is fine" — it means this table
            # no longer describes reality, and the pin needs looking at by a person.
            $broken = "$currentId $currentVersion no longer declares a dependency on $nextId. The chain in this script is out of date."
            break
        }

        $steps += "$currentId $currentVersion -> $nextId $nextVersion"
        $currentId = $nextId
        $currentVersion = $nextVersion
    }

    if ($broken) {
        $unresolved += "$($pin.Pin): $broken"
        continue
    }

    $pinned = ConvertTo-ComparableVersion $pinnedVersion
    $resolved = ConvertTo-ComparableVersion $currentVersion

    Write-Host "  $($pin.Pin) pinned to $pinnedVersion — $($pin.Advisory)"
    foreach ($step in $steps) { Write-Host "      $step" }

    if ($null -eq $pinned -or $null -eq $resolved) {
        $unresolved += "$($pin.Pin): could not compare '$pinnedVersion' with '$currentVersion'."
        continue
    }

    if ($resolved -ge $pinned) {
        Write-Host "      RETIRABLE: the chain now supplies $currentVersion on its own." -ForegroundColor Yellow
        $retirable += "$($pin.Pin): chain supplies $currentVersion, pin holds $pinnedVersion. Remove the pin and its comment from Directory.Packages.props."
    }
    else {
        Write-Host "      still needed: the chain supplies only $currentVersion."
    }
    Write-Host ''
}

if ($unresolved) {
    Write-Host ''
    Write-Host 'This check could not answer the question for:' -ForegroundColor Red
    foreach ($u in $unresolved) { Write-Host "  - $u" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Fix the table in scripts/check-transitive-pins.ps1 rather than deleting the check.'
    exit 1
}

if ($retirable) {
    Write-Host ''
    Write-Host 'One or more pins are no longer doing anything:' -ForegroundColor Yellow
    foreach ($r in $retirable) { Write-Host "  - $r" -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'A pin that outlives its advisory is a version floor nobody chose. Remove it, and'
    Write-Host 'remove its entry from the table in this script.'
    exit 1
}

Write-Host "All $($pins.Count) pins are still doing work."
exit 0
