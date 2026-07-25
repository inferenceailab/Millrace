<#
.SYNOPSIS
  Regenerates docs/backlog.md from the GitHub issues and milestones.

.DESCRIPTION
  GitHub is the source of truth for planned work (see CLAUDE.md). This script renders a
  read-only markdown mirror so the plan is reviewable offline and in diffs.

  Never hand-edit docs/backlog.md — change the issues, then run this.

.EXAMPLE
  pwsh ./scripts/generate-backlog.ps1
#>
[CmdletBinding()]
param(
  [string]$Repo = 'inferenceailab/Millrace',
  [string]$OutFile = (Join-Path $PSScriptRoot '..' 'docs' 'backlog.md')
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
$null = $sb.AppendLine("> **Generated file — do not edit.** Rendered from GitHub issues on $(Get-Date -Format 'yyyy-MM-dd') by ``scripts/generate-backlog.ps1``.")
$null = $sb.AppendLine("> The source of truth is the [project board](https://github.com/users/inferenceailab/projects/1) and the repository issues.")
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

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
Set-Content -Path $OutFile -Value $sb.ToString().TrimEnd() -Encoding utf8
Write-Host "wrote $OutFile ($done done, $open open)"
