<#
.SYNOPSIS
    Asserts that every internal link in the rendered documentation site resolves to a real file.

.DESCRIPTION
    docfx checks cross-references it owns (<xref:...> and API links) and fails the build on a broken
    one. It does not check ordinary markdown links between conceptual pages — a `[jobs](jobs.md)`
    pointing at a file that was renamed renders as a link to nowhere and builds clean.

    That is the same shape of gap ARCHITECTURE.md §11.38 records: every check was static and none of
    them looked at the artifact. This walks the rendered HTML and resolves each internal href
    against the output directory, which is the only place the answer actually lives.

    Written in PowerShell rather than Node so the docs workflow needs no Node toolchain — the UI
    bundles are skipped there, and adding one back for a link checker would give that claim away.

.PARAMETER Site
    The rendered site directory (docs/site/_site).

.EXAMPLE
    ./scripts/check-docs-links.ps1 -Site docs/site/_site
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Site
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Site)) {
    Write-Error "Site directory not found: $Site. Build it first with 'dotnet docfx docs/site/docfx.json'."
}

$root = (Resolve-Path -LiteralPath $Site).Path
$pages = Get-ChildItem -LiteralPath $root -Recurse -Filter *.html -File

$broken = [System.Collections.Generic.List[string]]::new()
$checked = 0

foreach ($page in $pages) {
    $html = Get-Content -LiteralPath $page.FullName -Raw

    foreach ($match in [regex]::Matches($html, '(?:href|src)="([^"]+)"')) {
        $href = $match.Groups[1].Value

        # External, in-page and inline targets are not ours to resolve.
        if ($href -match '^(https?:|mailto:|#|data:|javascript:)') { continue }

        $clean = ($href -split '#')[0]
        $clean = ($clean -split '\?')[0]
        if ([string]::IsNullOrWhiteSpace($clean)) { continue }

        $checked++

        $target = Join-Path $page.DirectoryName $clean
        # A directory link is served by its index.html, so it only counts as resolved if that exists.
        $ok = (Test-Path -LiteralPath $target -PathType Leaf) -or
              (Test-Path -LiteralPath (Join-Path $target 'index.html') -PathType Leaf)

        if (-not $ok) {
            $relative = $page.FullName.Substring($root.Length).TrimStart('\', '/')
            $broken.Add("$relative  ->  $href")
        }
    }
}

Write-Host "Checked $checked internal links across $($pages.Count) pages."

if ($broken.Count -gt 0) {
    # Templates repeat the same navigation on every page, so one broken nav link would otherwise be
    # reported hundreds of times and bury everything else.
    $unique = $broken | Sort-Object -Unique
    Write-Host ""
    Write-Host "Broken internal links ($($unique.Count) distinct, $($broken.Count) occurrences):"
    $unique | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" }
    if ($unique.Count -gt 40) {
        Write-Host "  ... and $($unique.Count - 40) more."
    }
    exit 1
}

Write-Host "All internal links resolve."
