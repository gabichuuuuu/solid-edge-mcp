# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Keeps the generated parts of the documentation up to date: the tool count and version of the README,
    the "New tools" section of docs/COVERAGE.md and the version of the Claude Code plugin.

.DESCRIPTION
    Checks the last cell of every table row in docs/COVERAGE.md: ✔ works through the MCP, ✘ does not work in
    Solid Edge, followed by its reason in bold, · available in the API but not tested yet.
    Tools declared in src (McpServerTool Name = "se_...") that docs/COVERAGE.md does not mention yet are listed
    in its "New tools" section as · rows; the README tool count follows the code and its version follows the
    <Version> of src/SolidEdgeMcp.Server/SolidEdgeMcp.Server.csproj.
    Without switches it rewrites the files of the working tree. -Check changes nothing and fails when
    they are out of date. -Staged, meant for a local pre-commit hook, updates the staged versions
    from the staged tables and code (so every commit is consistent) and then the working tree.

.EXAMPLE
    .\scripts\update-docs.ps1
.EXAMPLE
    .\scripts\update-docs.ps1 -Check
.EXAMPLE
    pwsh -NoProfile -File scripts/update-docs.ps1 -Staged   # in .git/hooks/pre-commit
#>
[CmdletBinding()]
param([switch]$Check, [switch]$Staged)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$utf8 = [Text.UTF8Encoding]::new($false)
$newStart = '<!-- new-tools:start -->'
$newEnd = '<!-- new-tools:end -->'

function Read-Text([string]$path) {
    if (Test-Path $path) { [IO.File]::ReadAllText($path, $utf8) } else { '' }
}

function Same([string]$a, [string]$b) { ($a -replace "`r`n", "`n") -ceq ($b -replace "`r`n", "`n") }

# Computes every generated file of the checkout in $root: returns the new contents by relative path,
# the numbers and the tools that docs/TOOLS.md does not describe.
function Get-Docs([string]$root) {
    $coverage = Read-Text (Join-Path $root 'docs/COVERAGE.md')
    $readme = Read-Text (Join-Path $root 'README.md')
    $toolsDoc = Read-Text (Join-Path $root 'docs/TOOLS.md')
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
            'Declared in the code but not yet described in the tables above. `scripts/update-docs.ps1` lists'
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

    # Rows by status, for the maintainer's summary; header and separator rows are skipped.
    $counts = @{ done = 0; not_viable = 0; pending = 0 }
    # Why each ✘ does not work: the word in bold right after it.
    $reasons = [ordered]@{ API = 0; Unsafe = 0; Installation = 0; Interactive = 0; External = 0 }
    $inSection = $false
    foreach ($line in ($coverage -split "`n")) {
        if ($line -match '^## ') {
            $inSection = $true
            continue
        }

        if (-not $inSection -or $line -notmatch '^\|' -or $line -match '^\|\s*(Function|-)') {
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

        $counts[$kind]++
    }

    # Tool count: the tools badge and every "N tools" / "N se_* tools" of the README.
    $newReadme = [regex]::Replace($readme, '(?<=img\.shields\.io/badge/tools-)\d+(?=-)', "$($tools.Count)")
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
        'docs/COVERAGE.md' = $coverage
        'README.md' = $newReadme
    }
    # Version of the Claude Code plugin: its launcher installs the release package of that version.
    $pluginFile = 'plugin/.claude-plugin/plugin.json'
    $plugin = Read-Text (Join-Path $root $pluginFile)
    $pluginVersion = [regex]'(?<="version"\s*:\s*")[^"]+(?=")'
    if (-not $pluginVersion.IsMatch($plugin)) {
        throw "$pluginFile has no version."
    }

    $files[$pluginFile] = $pluginVersion.Replace($plugin, $version, 1)

    [pscustomobject]@{
        Files = $files
        Summary = "$($counts.done) ✔, $($counts.not_viable) ✘ ($($reasons.API) API, $($reasons.Unsafe) unsafe, $($reasons.Installation) installation, $($reasons.Interactive) interactive, $($reasons.External) external), $($counts.pending) ·; $($tools.Count) tools; version $version"
        Undescribed = @($tools | Where-Object { $toolsDoc -notmatch "``$([regex]::Escape($_))``" })
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
    $result = Get-Docs $repo
    $stale = @($result.Files.Keys | Where-Object { -not (Same (Read-Text (Join-Path $repo $_)) $result.Files[$_]) })
    if ($stale.Count -gt 0) {
        Write-Error "The generated documentation is out of date ($($stale -join ', ')): run scripts/update-docs.ps1."
        exit 1
    }

    Write-Output "Documentation up to date: $($result.Summary)."
    exit 0
}

if ($Staged) {
    # The staged files go to a temporary folder, are regenerated there and staged again, so the commit
    # holds documentation that matches its own tables and code, whatever else is unstaged.
    $temp = Join-Path ([IO.Path]::GetTempPath()) "se-docs-$([guid]::NewGuid().ToString('N'))"
    try {
        $paths = @(Invoke-Git ls-files -- README.md docs/COVERAGE.md docs/TOOLS.md plugin/.claude-plugin src | Where-Object {
                $_ -in @('README.md', 'docs/COVERAGE.md', 'docs/TOOLS.md', 'plugin/.claude-plugin/plugin.json') -or ($_ -like 'src/*' -and ($_ -like '*.cs' -or $_ -like '*.csproj'))
            })
        Invoke-Git checkout-index "--prefix=$(($temp -replace '\\', '/'))/" -- @paths | Out-Null
        New-Item -ItemType Directory -Force (Join-Path $temp 'plugin/.claude-plugin') | Out-Null
        $result = Get-Docs $temp
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

$result = Get-Docs $repo
Write-Files $repo $result.Files | ForEach-Object { Write-Output "Updated $_." }
foreach ($tool in $result.Unlisted) { Write-Warning "$tool is not described in docs/COVERAGE.md yet: listed under 'New tools' as not tested." }
foreach ($tool in $result.Undescribed) { Write-Warning "$tool is not described in docs/TOOLS.md." }
Write-Output "$($result.Summary)."
