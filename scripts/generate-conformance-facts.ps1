<#
.SYNOPSIS
  Regenerates docs/site/guide/conformance-facts.md from the conformance suites.

.DESCRIPTION
  The conformance kit is the definition of a supported provider (ARCHITECTURE.md §11.41), and its
  fact names are already written as sentences — Millrace.Storage.Verification suppresses CS1591 for
  exactly that reason: a <summary> above
  Awaiting_inserts_racing_parent_terminal_apply_never_strand_a_child would restate it, not explain
  it.

  What was missing was somewhere to *read* them. docs/site/guide/writing-a-provider.md tells an
  author what to implement and what catches people out, but never enumerates what the kit actually
  asserts — so "what does conformance require?" was answerable only by opening the source or running
  the suite. This renders those sentences into the guide.

  It restates nothing. Every line on the page is a method name with its underscores replaced, in
  source order, under the section comments the suites already group themselves by. If a fact is
  added, renamed or removed, this page changes with it or CI fails.

.PARAMETER Check
  Renders to memory and compares against the file on disk instead of writing it. Exits non-zero
  when they differ, naming the lines. This is what CI runs.

.EXAMPLE
  pwsh ./scripts/generate-conformance-facts.ps1

.EXAMPLE
  pwsh ./scripts/generate-conformance-facts.ps1 -Check
#>
[CmdletBinding()]
param(
  [string]$SourceDir = (Join-Path $PSScriptRoot '..' 'src' 'Millrace.Storage.Verification'),
  [string]$OutFile = (Join-Path $PSScriptRoot '..' 'docs' 'site' 'guide' 'conformance-facts.md'),
  [switch]$Check
)

$ErrorActionPreference = 'Stop'

# Suite order on the page, and the heading each gets. Declared rather than discovered so the page
# reads in the order a provider author meets these — jobs first, because a provider that implements
# only IJobStorage is already useful, and monitoring last because §11.14 made it a separate
# interface for that reason.
$suiteOrder = @(
  @{ Class = 'JobStorageConformanceSuite'; Heading = 'Job storage' },
  @{ Class = 'WorkflowStorageConformanceSuite'; Heading = 'Workflow storage' },
  @{ Class = 'MonitoringConformanceSuite'; Heading = 'Monitoring read model' }
)

$files = Get-ChildItem -LiteralPath $SourceDir -Filter '*ConformanceSuite*.cs' | Sort-Object Name
if (-not $files) {
  Write-Error "No conformance suite sources under $SourceDir"
}

# suite class -> ordered list of @{ Section; Facts }
$bySuite = [ordered]@{}

foreach ($file in $files) {
  $lines = Get-Content -LiteralPath $file.FullName

  $class = $null
  foreach ($line in $lines) {
    if ($line -match 'public\s+abstract\s+partial\s+class\s+(\w+)' -or $line -match 'public\s+abstract\s+class\s+(\w+)') {
      $class = $Matches[1]
      break
    }
  }
  if (-not $class) { continue }

  # A partial file with no section comments is its own section, named by the file suffix:
  # JobStorageConformanceSuite.Checkpoints.cs -> "checkpoints". The base file falls back to a
  # section that is dropped later if it stays empty.
  $suffix = ($file.BaseName -split '\.')[-1]
  $fallback = if ($suffix -eq $class) { '' } else { $suffix.ToLowerInvariant() }

  if (-not $bySuite.Contains($class)) { $bySuite[$class] = [System.Collections.Generic.List[object]]::new() }
  $sections = $bySuite[$class]

  $current = $fallback
  $pendingIsTheory = $false
  $pending = $false

  foreach ($line in $lines) {
    if ($line -match '^\s*//\s-{4,}\s*(.+?)\s*$') {
      $current = $Matches[1]
      continue
    }

    if ($line -match '^\s*\[(Fact|Theory)\]') {
      $pending = $true
      $pendingIsTheory = $Matches[1] -eq 'Theory'
      continue
    }

    if (-not $pending) { continue }

    # Attributes may stack ([Theory] then [InlineData]); keep waiting for the signature.
    if ($line -match '^\s*\[') { continue }

    if ($line -match '^\s*public\s+(?:async\s+)?(?:Task|void|ValueTask)\s+([A-Za-z0-9_]+)\s*\(') {
      $name = $Matches[1]

      $section = $sections | Where-Object { $_.Section -eq $current } | Select-Object -First 1
      if (-not $section) {
        $section = [pscustomobject]@{ Section = $current; Facts = [System.Collections.Generic.List[object]]::new() }
        $sections.Add($section)
      }
      $section.Facts.Add([pscustomobject]@{ Name = $name; IsTheory = $pendingIsTheory })
      $pending = $false
      $pendingIsTheory = $false
    }
  }
}

$total = 0
foreach ($class in $bySuite.Keys) {
  foreach ($section in $bySuite[$class]) { $total += $section.Facts.Count }
}
$theories = 0
foreach ($class in $bySuite.Keys) {
  foreach ($section in $bySuite[$class]) { $theories += ($section.Facts | Where-Object IsTheory).Count }
}

function Format-Sentence($name) {
  # A pure substitution, which is the point: the name is the specification, and anything beyond
  # swapping underscores for spaces would be this script having an opinion about it.
  $name -replace '_', ' '
}

function Format-Heading($section) {
  if ([string]::IsNullOrWhiteSpace($section)) { return $null }
  return $section.Substring(0, 1).ToUpperInvariant() + $section.Substring(1)
}

$sb = [System.Text.StringBuilder]::new()
$null = $sb.AppendLine('# Conformance facts')
$null = $sb.AppendLine()
$null = $sb.AppendLine('> **Generated file — do not edit.** Rendered from the conformance suites by')
$null = $sb.AppendLine('> `scripts/generate-conformance-facts.ps1`. Change the suites, then run it.')
$null = $sb.AppendLine()
$null = $sb.AppendLine("A provider that passes these is a supported provider. There are **$total** of them, and they are")
$null = $sb.AppendLine('the definition rather than a description of it — [Writing a provider](writing-a-provider.md)')
$null = $sb.AppendLine('explains what you implement, and this is what gets checked.')
$null = $sb.AppendLine()
$null = $sb.AppendLine('Each line is a test method name with its underscores replaced by spaces, in source order. Put')
$null = $sb.AppendLine('the underscores back to find one in `Millrace.Storage.Verification`, or to run it on its own.')
$null = $sb.AppendLine("$theories of them are theories, so a run reports more cases than there are lines here.")
$null = $sb.AppendLine()
$null = $sb.AppendLine('Nothing on this page was written for it. If a sentence below is unclear, the fix belongs in the')
$null = $sb.AppendLine('method name it came from.')
$null = $sb.AppendLine()

foreach ($entry in $suiteOrder) {
  if (-not $bySuite.Contains($entry.Class)) { continue }
  $sections = $bySuite[$entry.Class]
  $count = 0
  foreach ($s in $sections) { $count += $s.Facts.Count }

  $null = $sb.AppendLine("## $($entry.Heading) — ``$($entry.Class)`` ($count facts)")
  $null = $sb.AppendLine()

  $named = @($sections | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Section) })
  $useHeadings = $named.Count -gt 1

  foreach ($section in $sections) {
    if ($section.Facts.Count -eq 0) { continue }
    if ($useHeadings) {
      $heading = Format-Heading $section.Section
      if ($heading) {
        $null = $sb.AppendLine("### $heading")
        $null = $sb.AppendLine()
      }
    }
    foreach ($fact in $section.Facts) {
      $suffix = if ($fact.IsTheory) { ' *(theory)*' } else { '' }
      $null = $sb.AppendLine("- $(Format-Sentence $fact.Name)$suffix")
    }
    $null = $sb.AppendLine()
  }
}

$rendered = $sb.ToString().TrimEnd()

# Same reasoning as generate-backlog.ps1: no .gitattributes, core.autocrlf on, AppendLine follows
# Environment.NewLine — so a raw comparison fails on the Linux runner for a reason invisible in the
# diff.
function ConvertTo-ComparableText($text) { ($text -replace "`r`n", "`n").TrimEnd() }

if ($Check) {
  if (-not (Test-Path -LiteralPath $OutFile)) {
    Write-Error "$OutFile does not exist. Run: pwsh ./scripts/generate-conformance-facts.ps1"
  }

  $expected = ConvertTo-ComparableText $rendered
  $actual = ConvertTo-ComparableText (Get-Content -LiteralPath $OutFile -Raw)

  if ($expected -eq $actual) {
    Write-Host "conformance-facts.md is current ($total facts)."
    exit 0
  }

  $expectedLines = $expected -split "`n"
  $actualLines = $actual -split "`n"
  Write-Host 'conformance-facts.md is out of date. Run: pwsh ./scripts/generate-conformance-facts.ps1'
  Write-Host ''

  $shown = 0
  for ($i = 0; $i -lt [Math]::Max($expectedLines.Count, $actualLines.Count) -and $shown -lt 20; $i++) {
    $e = if ($i -lt $expectedLines.Count) { $expectedLines[$i] } else { '<missing>' }
    $a = if ($i -lt $actualLines.Count) { $actualLines[$i] } else { '<missing>' }
    if ($e -ne $a) {
      Write-Host ("  line {0}" -f ($i + 1))
      Write-Host ("    on disk:   {0}" -f $a)
      Write-Host ("    should be: {0}" -f $e)
      $shown++
    }
  }

  exit 1
}

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
Set-Content -Path $OutFile -Value $rendered -Encoding utf8
Write-Host "wrote $OutFile ($total facts, $theories theories)"
