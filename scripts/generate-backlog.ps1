<#
.SYNOPSIS
  Regenerates docs/backlog.md from the GitHub issues and milestones.

.DESCRIPTION
  GitHub is the source of truth for planned work (see CLAUDE.md). This script renders a
  read-only markdown mirror so the plan is reviewable offline and in diffs.

  Never hand-edit docs/backlog.md — change the issues, then run this.

  The rendered file carries no generation date, deliberately. It used to, and that made the
  obvious staleness check impossible: regenerating an unchanged backlog still produced a one-line
  diff, so a job that failed on any diff would have gone red on every unrelated push and been
  switched off within a week. Git already records when the file last changed, and unlike a stamp
  written into the file it cannot be wrong.

.PARAMETER Check
  Renders to memory and compares against the file on disk instead of writing it. Exits non-zero
  when they differ, naming the lines. This is what CI runs, so the "generated file" claim in
  CLAUDE.md is enforced rather than asserted.

.EXAMPLE
  pwsh ./scripts/generate-backlog.ps1

.EXAMPLE
  pwsh ./scripts/generate-backlog.ps1 -Check
#>
[CmdletBinding()]
param(
  [string]$Repo = 'inferenceailab/Millrace',
  [string]$OutFile = (Join-Path $PSScriptRoot '..' 'docs' 'backlog.md'),
  [switch]$Check
)

$ErrorActionPreference = 'Stop'

$issues = gh api "repos/$Repo/issues?state=all&per_page=100" --paginate | ConvertFrom-Json |
          Where-Object { -not $_.pull_request }
$milestones = gh api "repos/$Repo/milestones?state=all&per_page=100" | ConvertFrom-Json |
              Sort-Object number

function Get-Label($issue, $prefix) {
  ($issue.labels.name | Where-Object { $_ -like "$prefix*" } | ForEach-Object { $_ -replace "^$prefix", '' }) -join ', '
}
function Format-Row($i) {
  $state = if ($i.state -eq 'closed') { 'done' } elseif ($i.labels.name -contains 'blocked') { 'blocked' } else { 'open' }
  $area  = Get-Label $i 'area:'
  "| [#$($i.number)]($($i.html_url)) | $($i.title) | $area | $state |"
}

$sb = [System.Text.StringBuilder]::new()
$null = $sb.AppendLine('# Backlog')
$null = $sb.AppendLine()
$null = $sb.AppendLine("> **Generated file — do not edit.** Rendered from GitHub issues by ``scripts/generate-backlog.ps1``.")
$null = $sb.AppendLine("> The source of truth is the [project board](https://github.com/users/inferenceailab/projects/1) and the repository issues.")
$null = $sb.AppendLine("> For when it was last rendered, ask git: ``git log -1 --format=%cd docs/backlog.md``.")
$null = $sb.AppendLine()

$open = ($issues | Where-Object state -eq 'open').Count
$done = ($issues | Where-Object state -eq 'closed').Count
$blocked = ($issues | Where-Object { $_.state -eq 'open' -and $_.labels.name -contains 'blocked' }).Count
$null = $sb.AppendLine("**$done done · $open open** (of which $blocked blocked on an unresolved spike).")
$null = $sb.AppendLine()

# epics
$epics = $issues | Where-Object { $_.labels.name -contains 'type:epic' } | Sort-Object number
if ($epics) {
  $null = $sb.AppendLine('## Epics')
  $null = $sb.AppendLine()
  foreach ($e in $epics) { $null = $sb.AppendLine("- [#$($e.number)]($($e.html_url)) $($e.title -replace '^Epic: ','')") }
  $null = $sb.AppendLine()
}

# spikes first — they gate everything else
$spikes = $issues | Where-Object { $_.labels.name -contains 'type:spike' -and $_.state -eq 'open' } | Sort-Object number
if ($spikes) {
  $null = $sb.AppendLine('## Open design questions (spikes)')
  $null = $sb.AppendLine()
  $null = $sb.AppendLine('These gate the work below them. See [`docs/open-questions.md`](open-questions.md) for the full statement of each.')
  $null = $sb.AppendLine()
  $null = $sb.AppendLine('| # | Question | Area |')
  $null = $sb.AppendLine('|---|---|---|')
  foreach ($s in $spikes) {
    $null = $sb.AppendLine("| [#$($s.number)]($($s.html_url)) | $($s.title -replace '^Spike: ','') | $(Get-Label $s 'area:') |")
  }
  $null = $sb.AppendLine()
}

# by milestone
foreach ($m in $milestones) {
  $items = $issues | Where-Object { $_.milestone -and $_.milestone.number -eq $m.number } | Sort-Object number
  if (-not $items) { continue }
  $tag = if ($m.state -eq 'closed') { ' — **complete**' } else { '' }
  $null = $sb.AppendLine("## $($m.title)$tag")
  $null = $sb.AppendLine()
  if ($m.description) { $null = $sb.AppendLine("$($m.description)"); $null = $sb.AppendLine() }
  $null = $sb.AppendLine('| # | Story | Area | State |')
  $null = $sb.AppendLine('|---|---|---|---|')
  foreach ($i in $items) { $null = $sb.AppendLine((Format-Row $i)) }
  $null = $sb.AppendLine()
}

$unassigned = $issues | Where-Object { -not $_.milestone -and $_.labels.name -notcontains 'type:epic' } | Sort-Object number
if ($unassigned) {
  $null = $sb.AppendLine('## Not yet scheduled')
  $null = $sb.AppendLine()
  $null = $sb.AppendLine('| # | Story | Area | State |')
  $null = $sb.AppendLine('|---|---|---|---|')
  foreach ($i in $unassigned) { $null = $sb.AppendLine((Format-Row $i)) }
  $null = $sb.AppendLine()
}

$rendered = $sb.ToString().TrimEnd()

# Line endings are normalised on both sides before comparing. There is no .gitattributes here and
# core.autocrlf is on, so the working tree is CRLF on Windows and LF on the Linux runner while
# StringBuilder.AppendLine follows Environment.NewLine — comparing raw text would fail on the
# runner for a reason invisible in the diff, which is how a check earns a reputation for crying
# wolf and then gets deleted.
function ConvertTo-ComparableText($text) { ($text -replace "`r`n", "`n").TrimEnd() }

if ($Check) {
  if (-not (Test-Path -LiteralPath $OutFile)) {
    Write-Error "$OutFile does not exist. Run: pwsh ./scripts/generate-backlog.ps1"
  }

  $expected = ConvertTo-ComparableText $rendered
  $actual = ConvertTo-ComparableText (Get-Content -LiteralPath $OutFile -Raw)

  if ($expected -eq $actual) {
    Write-Host "docs/backlog.md is current ($done done, $open open)."
    exit 0
  }

  $expectedLines = $expected -split "`n"
  $actualLines = $actual -split "`n"
  Write-Host "docs/backlog.md is out of date. Run: pwsh ./scripts/generate-backlog.ps1"
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

  # An issue closed on GitHub makes this fail on a pull request that never touched it — the mirror
  # tracks external state, not the diff. That is the intended behaviour and the fix is thirty
  # seconds, but it is also the reason to keep this message actionable rather than merely correct.
  exit 1
}

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
Set-Content -Path $OutFile -Value $rendered -Encoding utf8
Write-Host "wrote $OutFile ($done done, $open open)"
