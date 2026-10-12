# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

# Runs the acceptance scripts against the built server and real Solid Edge, one after another.
# Usage (from the root, after dotnet build SolidEdgeMcp.slnx): .\tests\SolidEdgeMcp.Acceptance\run.ps1 [A01 A03 ...]
# Creates and closes its own documents without saving; the files go to the work folder
# (%LocalAppData%\SolidEdgeMcp\work\acceptance or SE_MCP_WORK), and the log of each script goes to logs\ inside it.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Script prefixes (all loose arguments): run.ps1 A03 A09
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Names = @('A'),
    [string]$Configuration = 'Debug',
    [string]$Policy = (Join-Path $env:LOCALAPPDATA 'SolidEdgeMcp\policy.json'),
    # Another server executable, e.g. publish\SolidEdgeMcp.Server.exe to validate the publication.
    [string]$Server
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$server = if ($Server) { (Resolve-Path $Server).Path } else { Join-Path $root "src\SolidEdgeMcp.Server\bin\$Configuration\net10.0-windows\SolidEdgeMcp.Server.exe" }
# The solution builds the tests for x64 (bin\x64\...); when only the project is built, the output goes to bin\...
$runner = "bin\x64\$Configuration", "bin\$Configuration" |
    ForEach-Object { Join-Path $PSScriptRoot "$_\net10.0-windows\SolidEdgeMcp.Acceptance.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $runner) { $runner = Join-Path $PSScriptRoot "bin\x64\$Configuration\net10.0-windows\SolidEdgeMcp.Acceptance.exe" }
foreach ($exe in $server, $runner) {
    if (-not (Test-Path $exe)) { throw "$exe does not exist; build it first with: dotnet build SolidEdgeMcp.slnx" }
}

$work = if ($env:SE_MCP_WORK) { $env:SE_MCP_WORK } else { Join-Path $env:LOCALAPPDATA 'SolidEdgeMcp\work\acceptance' }
$logs = Join-Path $work 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$serverArgs = if (Test-Path $Policy) { @('--policy', $Policy) } else { @() }

$failed = 0
$scripts = $Names | ForEach-Object { Get-ChildItem (Join-Path $PSScriptRoot 'scripts') -Filter "$_*.mcp" } | Sort-Object Name -Unique
foreach ($script in $scripts) {
    $log = Join-Path $logs "$($script.BaseName).log"
    & $runner $server $script.FullName @serverArgs *> $log
    $code = $LASTEXITCODE
    if ($code -ne 0) { $failed++ }
    Select-String -Path $log -Pattern '^RESULT|^  - ' | ForEach-Object { $_.Line }
}

Write-Output "$($scripts.Count) scripts, $failed with failures. Logs: $logs"
exit $(if ($failed -eq 0) { 0 } else { 1 })
