# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Keeps the generated parts of the documentation up to date: the progress section of docs/COVERAGE.md,
    the README progress badge, tool count and version, and the progress chart (docs/assets/progress.svg
    and progress-dark.svg).

.DESCRIPTION
    Counts the rows of every table in docs/COVERAGE.md by the first character of their last cell:
    ✔ works through the MCP, ✘ does not work in Solid Edge (documented, with an alternative when there
    is one), · available in the API but not tested yet. The progress is the share of rows that work
    (✔): ✘ and · rows count as not working yet, so 100 % means that every function in the tables
    works through the MCP.
    Tools declared in src (McpServerTool Name = "se_...") that docs/COVERAGE.md does not mention yet
    are listed in its "New tools" section as · rows, so a new tool counts as pending until its rows
    are written; the README tool count follows the code and its version follows the <Version> of
    src/SolidEdgeMcp.Server/SolidEdgeMcp.Server.csproj.
    The chart URLs in docs/COVERAGE.md and README.md carry a fingerprint of the chart (?v=...), so a
    changed chart gets a new URL and clients that cache images (such as the GitHub mobile app) do not
    keep showing an old one.
    Without switches it rewrites the files of the working tree. -Check changes nothing and fails when
    they are out of date. -Staged, meant for a local pre-commit hook, updates the staged versions
    from the staged tables and code (so every commit is consistent) and then the working tree.

.EXAMPLE
    .\scripts\update-progress.ps1
.EXAMPLE
    .\scripts\update-progress.ps1 -Check
.EXAMPLE
    pwsh -NoProfile -File scripts/update-progress.ps1 -Staged   # in .git/hooks/pre-commit
#>
[CmdletBinding()]
param([switch]$Check, [switch]$Staged)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$utf8 = [Text.UTF8Encoding]::new($false)
$startMarker = '<!-- progress:start -->'
$endMarker = '<!-- progress:end -->'
$newStart = '<!-- new-tools:start -->'
$newEnd = '<!-- new-tools:end -->'

function Read-Text([string]$path) {
    if (Test-Path $path) { [IO.File]::ReadAllText($path, $utf8) } else { '' }
}

function Same([string]$a, [string]$b) { ($a -replace "`r`n", "`n") -ceq ($b -replace "`r`n", "`n") }

function Percent([int]$working, [int]$total) {
    if ($total -eq 0) { return 0 }
    # Rounded down, so that 100 % only appears when every function works.
    return [math]::Floor(100 * $working / $total)
}

function Esc([string]$text) { [Security.SecurityElement]::Escape($text) }

# First 8 hex digits of the SHA-256 of a text: the version of a chart in its URL.
function Fingerprint([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($utf8.GetBytes($text)) } finally { $sha.Dispose() }
    -join ($hash[0..3] | ForEach-Object { $_.ToString('x2') })
}

# Progress chart: one square per function, one row per area, in a light and a dark version (the
# README picks one with prefers-color-scheme). Transparent background, GitHub's own colors; blue and
# orange stay apart for color-blind readers and the legend and the numbers carry the meaning too.
# Reads $areas, $done, $notViable, $pending, $total and $percent from the caller.
$themes = [ordered]@{
    light = @{ ink = '#1f2328'; muted = '#59636e'; done = '#0969da'; not_viable = '#bc4c00'; pending = '#d1d9e0' }
    dark = @{ ink = '#f0f6fc'; muted = '#9198a1'; done = '#4493f8'; not_viable = '#db6d28'; pending = '#3d444d' }
}

function Chart($c) {
    $square = 12
    $pitch = 16
    $labelWidth = 270
    # Squares per line, so that they never reach the numbers on the right; longer areas wrap.
    $perLine = 30
    $rowGap = 12
    $rowTop = 84
    $rowsHeight = ($areas.Values | ForEach-Object {
            [math]::Ceiling(($_.done + $_.not_viable + $_.pending) / $perLine) * $pitch + $rowGap
        } | Measure-Object -Sum).Sum
    $legendY = $rowTop + $rowsHeight + 26
    $height = $legendY + 12
    $byArea = ($areas.Keys | ForEach-Object { "$_ $($areas[$_].done) of $($areas[$_].done + $areas[$_].not_viable + $areas[$_].pending)" }) -join '; '
    # DIN 1451 lettering where the system has it (Windows, macOS), as in docs/assets/banner.svg.
    $font = "Bahnschrift, 'DIN Alternate', 'DIN 1451', D-DIN, Barlow, 'Roboto Condensed', 'Arial Narrow', sans-serif"
    $svg = @(
        "<svg xmlns=""http://www.w3.org/2000/svg"" width=""870"" height=""$height"" viewBox=""0 0 870 $height"" role=""img"" aria-labelledby=""title desc"">"
        "  <title id=""title"">Progress: $percent % of the functions work through the MCP</title>"
        "  <desc id=""desc"">$(Esc "One square per function. $done of $total Solid Edge functions in the coverage map work through the MCP, $notViable fail in Solid Edge 2026 and $pending are not tested yet. By area: $byArea.")</desc>"
        "  <g font-family=""$font"">"
        "    <text x=""0"" y=""40"" font-size=""36"" font-weight=""600"" fill=""$($c.ink)"">$percent %</text>"
        "    <text x=""$(if ($percent -ge 100) { 112 } else { 92 })"" y=""40"" font-size=""16"" fill=""$($c.muted)"">of the $total Solid Edge functions in the coverage map work through the MCP</text>"
    )
    $y = $rowTop
    foreach ($name in $areas.Keys) {
        $a = $areas[$name]
        $count = $a.done + $a.not_viable + $a.pending
        $svg += "    <text x=""0"" y=""$($y + 11)"" font-size=""14"" fill=""$($c.ink)"">$(Esc $name)</text>"
        $kinds = @(@('done') * $a.done) + @(@('not_viable') * $a.not_viable) + @(@('pending') * $a.pending)
        for ($i = 0; $i -lt $kinds.Count; $i++) {
            $x = $labelWidth + ($i % $perLine) * $pitch
            $top = $y + [math]::Floor($i / $perLine) * $pitch
            if ($kinds[$i] -eq 'pending') {
                $svg += "    <rect x=""$($x + 0.5)"" y=""$($top + 0.5)"" width=""$($square - 1)"" height=""$($square - 1)"" rx=""2"" fill=""none"" stroke=""$($c.pending)""/>"
            }
            else {
                $svg += "    <rect x=""$x"" y=""$top"" width=""$square"" height=""$square"" rx=""2"" fill=""$($c[$kinds[$i]])""/>"
            }
        }

        $svg += "    <text x=""812"" y=""$($y + 11)"" font-size=""13"" text-anchor=""end"" fill=""$($c.muted)"">$($a.done) / $count</text>"
        $svg += "    <text x=""870"" y=""$($y + 11)"" font-size=""14"" font-weight=""600"" text-anchor=""end"" fill=""$($c.ink)"">$(Percent $a.done $count) %</text>"
        $y += [math]::Ceiling($count / $perLine) * $pitch + $rowGap
    }

    $legend = @(
        @{ x = 0; kind = 'done'; text = "Works through the MCP ($done)" }
        @{ x = 250; kind = 'not_viable'; text = "Fails in Solid Edge 2026, documented ($notViable)" }
        @{ x = 590; kind = 'pending'; text = "Not tested yet ($pending)" }
    )
    foreach ($item in $legend) {
        if ($item.kind -eq 'pending') {
            $svg += "    <rect x=""$($item.x + 0.5)"" y=""$($legendY - 10.5)"" width=""$($square - 1)"" height=""$($square - 1)"" rx=""2"" fill=""none"" stroke=""$($c.pending)""/>"
        }
        else {
            $svg += "    <rect x=""$($item.x)"" y=""$($legendY - 11)"" width=""$square"" height=""$square"" rx=""2"" fill=""$($c[$item.kind])""/>"
        }

        $svg += "    <text x=""$($item.x + 20)"" y=""$legendY"" font-size=""13"" fill=""$($c.muted)"">$($item.text)</text>"
    }

    $svg += '  </g>'
    $svg += '</svg>'
    ($svg -join "`n") + "`n"
}

# Computes every generated file of the checkout in $root: returns the new contents by relative path,
# the numbers and the tools that README.md does not describe.
function Get-Progress([string]$root) {
    $coverage = Read-Text (Join-Path $root 'docs/COVERAGE.md')
    $readme = Read-Text (Join-Path $root 'README.md')
    # Keeps the line endings of the checkout (CRLF with core.autocrlf on Windows).
    $newline = if ($coverage.Contains("`r`n")) { "`r`n" } else { "`n" }

    $tools = @(Get-ChildItem (Join-Path $root 'src') -Recurse -Filter '*.cs' -File |
            Select-String -Pattern 'McpServerTool\(\s*Name\s*=\s*"(se_[a-z0-9_]+)"' -AllMatches |
            ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($tools.Count -eq 0) {
        throw 'No [McpServerTool(Name = "se_...")] found under src.'
    }

    # "New tools" section, always at the end: the tools that the rest of the file does not mention.
    $base = $coverage
    $s = $coverage.IndexOf($newStart)
    if ($s -ge 0) {
        $base = $coverage.Substring(0, $s).TrimEnd() + $newline
    }

    $unlisted = @($tools | Where-Object { $base -notmatch "(?<![A-Za-z0-9_])$([regex]::Escape($_))(?![A-Za-z0-9_])" })
    if ($unlisted.Count -gt 0) {
        $section = @(
            $newStart
            '## New tools'
            ''
            'Declared in the code but not yet described in the tables above. `scripts/update-progress.ps1` lists'
            'them here, as not tested, until their rows are written.'
            ''
            '| Function | Route | Status |'
            '|---|---|---|'
        )
        $section += $unlisted | ForEach-Object { "| ``$_``, to be described | ``$_`` | · |" }
        $section += $newEnd
        $coverage = $base.TrimEnd() + $newline + $newline + ($section -join $newline) + $newline
    }
    elseif ($s -ge 0) {
        $coverage = $base
    }

    $start = $coverage.IndexOf($startMarker)
    $end = $coverage.IndexOf($endMarker)
    if ($start -lt 0 -or $end -lt $start) {
        throw "docs/COVERAGE.md has no '$startMarker' ... '$endMarker' block."
    }

    # Rows by section ("## " headers) outside the progress block; header and separator rows are skipped.
    $areas = [ordered]@{}
    # Why each ✘ does not work: the word in bold right after it.
    $reasons = [ordered]@{ API = 0; Unsafe = 0; Installation = 0; Interactive = 0; External = 0 }
    $area = $null
    $tables = $coverage.Substring(0, $start) + $coverage.Substring($end + $endMarker.Length)
    foreach ($line in ($tables -split "`n")) {
        if ($line -match '^## (.+)$') {
            $area = $Matches[1].Trim()
            continue
        }

        if ($null -eq $area -or $line -notmatch '^\|' -or $line -match '^\|\s*(Function|-)') {
            continue
        }

        $status = (($line.TrimEnd().TrimEnd('|')) -split '\|')[-1].Trim()
        $kind = switch -Regex ($status) {
            '^✔' { 'done'; break }
            '^✘' { 'not_viable'; break }
            '^·' { 'pending'; break }
            default { throw "Unknown status '$status' in: $line" }
        }

        if ($kind -eq 'not_viable') {
            if ($status -notmatch '^✘\s*\*\*(API|Unsafe|Installation|Interactive|External)\*\*') {
                throw "A ✘ row has no reason (**API**, **Unsafe**, **Installation**, **Interactive** or **External**) after the ✘: $line"
            }

            $reasons[$Matches[1]]++
        }

        if (-not $areas.Contains($area)) {
            $areas[$area] = @{ done = 0; not_viable = 0; pending = 0 }
        }

        $areas[$area][$kind]++
    }

    $done = ($areas.Values | ForEach-Object { $_.done } | Measure-Object -Sum).Sum
    $notViable = ($areas.Values | ForEach-Object { $_.not_viable } | Measure-Object -Sum).Sum
    $pending = ($areas.Values | ForEach-Object { $_.pending } | Measure-Object -Sum).Sum
    $total = $done + $notViable + $pending
    $percent = Percent $done $total
    # Highest reachable on the tested installation: what works plus the separate programs not yet evaluated.
    $reachable = Percent ($done + $reasons.External) $total

    # The charts come first: their URLs carry their fingerprint.
    $charts = [ordered]@{}
    foreach ($theme in $themes.Keys) {
        $file = if ($theme -eq 'light') { 'docs/assets/progress.svg' } else { "docs/assets/progress-$theme.svg" }
        $charts[$file] = Chart $themes[$theme]
    }

    $lightChart = "progress.svg?v=$(Fingerprint $charts['docs/assets/progress.svg'])"
    $darkChart = "progress-dark.svg?v=$(Fingerprint $charts['docs/assets/progress-dark.svg'])"

    $lines = @(
        $startMarker
        '## Progress'
        ''
        '<picture>'
        "  <source media=""(prefers-color-scheme: dark)"" srcset=""assets/$darkChart"">"
        "  <img src=""assets/$lightChart"" alt=""Progress by area: one square per function"" width=""100%"">"
        '</picture>'
        ''
        "**$percent % working** ($done of $total functions work through the MCP, ✔): $notViable do not work in Solid Edge 2026"
        "and are documented, with an alternative when there is one (✘), and $pending are still to be tested (·)."
        'Every function, tested or not, can also be reached through the generic COM layer and `se_command`.'
        ''
        "Why the $notViable do not work: **$($reasons.API) API** (Solid Edge's API fails and no other route gives the same result),"
        "**$($reasons.Unsafe) unsafe** (they close or damage Solid Edge, so the MCP refuses them), **$($reasons.Installation) installation**"
        "(they need a licence, add-on, program, template, mail profile or server that the tested installation lacks)$(if ($reasons.External -gt 0) { ',' } else { ' and' })"
        if ($reasons.External -gt 0) {
            "**$($reasons.Interactive) interactive** (commands and programs that wait for the person) and **$($reasons.External) external** (separate programs"
            'that Solid Edge starts, not yet evaluated).'
            "**Highest reachable on the tested installation: $reachable %**, the working functions plus those separate programs; the"
            'rest depends on Siemens fixing its API, on what is installed and licensed, or on the person working in Solid Edge.'
        }
        else {
            "**$($reasons.Interactive) interactive** (commands and programs that wait for the person)."
            "**Highest reachable on the tested installation: $reachable %**: the rest depends on Siemens fixing its API, on what is"
            'installed and licensed, or on the person working in Solid Edge.'
        }
        ''
        '| Area | ✔ | ✘ | · | Working |'
        '|---|---|---|---|---|'
    )
    foreach ($name in $areas.Keys) {
        $a = $areas[$name]
        $lines += "| $name | $($a.done) | $($a.not_viable) | $($a.pending) | $(Percent $a.done ($a.done + $a.not_viable + $a.pending)) % |"
    }

    $lines += "| **Total** | **$done** | **$notViable** | **$pending** | **$percent %** |"
    $lines += ''
    $lines += 'Generated by `scripts/update-progress.ps1` from the tables below.'
    $lines += $endMarker
    $newCoverage = $coverage.Substring(0, $start) + ($lines -join $newline) + $coverage.Substring($end + $endMarker.Length)

    $color = if ($percent -ge 100) { 'brightgreen' } elseif ($percent -ge 80) { 'yellowgreen' } else { 'yellow' }
    $badge = "[![Progress: $percent % of functions work through the MCP](https://img.shields.io/badge/progress-$percent%25-$color)](docs/COVERAGE.md#progress)"
    # The shields.io badge line that links to the progress section, whatever its text (LF or CRLF checkout).
    $badgePattern = '(?m)^\[!\[[^\]]*\]\(https://img\.shields\.io/badge/[^)]*\)\]\(docs/COVERAGE\.md#progress\)(?=\r?$)'
    if ($readme -notmatch $badgePattern) {
        throw 'README.md has no progress badge line (linked to docs/COVERAGE.md#progress) to update.'
    }

    $newReadme = [regex]::Replace($readme, $badgePattern, $badge)
    # The README's line with the progress and the highest reachable.
    $reachPattern = '(?m)^\*\*\d+ % of the functions work today\*\*[^\r\n]*'
    if ($newReadme -notmatch $reachPattern) {
        throw 'README.md has no "**N % of the functions work today**" line to update.'
    }

    $newReadme = [regex]::Replace($newReadme, $reachPattern,
        "**$percent % of the functions work today**; the highest reachable on the tested installation is **$reachable %** (why the rest do not: [progress](docs/COVERAGE.md#progress)).")
    # Progress chart of the README (light and dark): the same fingerprinted URLs.
    $chartPattern = '(?<=(?:src|srcset)="docs/assets/)progress(-dark)?\.svg(?:\?v=[0-9a-f]+)?(?=")'
    if ($newReadme -notmatch $chartPattern) {
        throw 'README.md has no progress chart (docs/assets/progress.svg) to update.'
    }

    $newReadme = [regex]::Replace($newReadme, $chartPattern, {
            param($match)
            if ($match.Groups[1].Success) { $darkChart } else { $lightChart }
        })
    # Tool count: the tools badge and every "N tools" / "N se_* tools" of the README.
    $newReadme = [regex]::Replace($newReadme, '(?<=img\.shields\.io/badge/tools-)\d+(?=-)', "$($tools.Count)")
    $newReadme = [regex]::Replace($newReadme, '(?<![\w.,])\d+(?= (se_\* )?tools\b)', "$($tools.Count)")
    # Version: the <Version> of the server project, in the version badge and in every "version (x.y.z)"
    # of the README.
    $project = Read-Text (Join-Path $root 'src/SolidEdgeMcp.Server/SolidEdgeMcp.Server.csproj')
    if ($project -notmatch '<Version>([^<]+)</Version>') {
        throw 'src/SolidEdgeMcp.Server/SolidEdgeMcp.Server.csproj has no <Version>.'
    }

    $version = $Matches[1].Trim()
    $newReadme = [regex]::Replace($newReadme, '(?<=!\[Version )[^\]]+(?=\])', $version)
    $newReadme = [regex]::Replace($newReadme, '(?<=img\.shields\.io/badge/version-)[^-]+(?:--[^-]+)*(?=-)', $version.Replace('-', '--'))
    $newReadme = [regex]::Replace($newReadme, '(?<=\bversion \()\d+\.\d+\.\d+[^)]*(?=\))', $version)

    $files = [ordered]@{
        'docs/COVERAGE.md' = $newCoverage
        'README.md' = $newReadme
    }
    foreach ($file in $charts.Keys) {
        $files[$file] = $charts[$file]
    }

    [pscustomobject]@{
        Files = $files
        Summary = "$percent % working (highest reachable $reachable %): $done ✔, $notViable ✘ ($($reasons.API) API, $($reasons.Unsafe) unsafe, $($reasons.Installation) installation, $($reasons.Interactive) interactive, $($reasons.External) external), $pending · of $total functions; $($tools.Count) tools; version $version"
        Undescribed = @($tools | Where-Object { $readme -notmatch "``$([regex]::Escape($_))``" })
        Unlisted = $unlisted
    }
}

# Writes the files whose content changed; returns their relative paths.
function Write-Files([string]$root, $files) {
    foreach ($rel in $files.Keys) {
        $path = Join-Path $root $rel
        if (-not (Same (Read-Text $path) $files[$rel])) {
            [IO.File]::WriteAllText($path, $files[$rel], $utf8)
            $rel
        }
    }
}

function Invoke-Git {
    $output = & git -C $repo @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed ($LASTEXITCODE)." }
    $output
}

if ($Check) {
    $result = Get-Progress $repo
    $stale = @($result.Files.Keys | Where-Object { -not (Same (Read-Text (Join-Path $repo $_)) $result.Files[$_]) })
    if ($stale.Count -gt 0) {
        Write-Error "The progress is out of date ($($stale -join ', ')): run scripts/update-progress.ps1."
        exit 1
    }

    Write-Output "Progress up to date: $($result.Summary)."
    exit 0
}

if ($Staged) {
    # The staged files go to a temporary folder, are regenerated there and staged again, so the commit
    # holds a progress that matches its own tables and code, whatever else is unstaged.
    $temp = Join-Path ([IO.Path]::GetTempPath()) "se-progress-$([guid]::NewGuid().ToString('N'))"
    try {
        $paths = @(Invoke-Git ls-files -- README.md docs/COVERAGE.md docs/assets src | Where-Object {
                $_ -in @('README.md', 'docs/COVERAGE.md') -or $_ -like 'docs/assets/progress*.svg' -or ($_ -like 'src/*' -and ($_ -like '*.cs' -or $_ -like '*.csproj'))
            })
        Invoke-Git checkout-index "--prefix=$(($temp -replace '\\', '/'))/" -- @paths | Out-Null
        New-Item -ItemType Directory -Force (Join-Path $temp 'docs/assets') | Out-Null
        $result = Get-Progress $temp
        foreach ($rel in @(Write-Files $temp $result.Files)) {
            $sha = Invoke-Git hash-object -w "--path=$rel" -- (Join-Path $temp $rel)
            Invoke-Git update-index --add --cacheinfo "100644,$sha,$rel" | Out-Null
            Write-Output "Staged $rel."
        }
    }
    finally {
        if (Test-Path $temp) { Remove-Item -Recurse -Force $temp }
    }
}

$result = Get-Progress $repo
Write-Files $repo $result.Files | ForEach-Object { Write-Output "Updated $_." }
foreach ($tool in $result.Unlisted) { Write-Warning "$tool is not described in docs/COVERAGE.md yet: listed under 'New tools' as not tested." }
foreach ($tool in $result.Undescribed) { Write-Warning "$tool is not described in README.md." }
Write-Output "$($result.Summary)."
