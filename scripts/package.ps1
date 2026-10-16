# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Builds the distributable package of the server: win-x64, self-contained, without the Solid Edge interop.

.DESCRIPTION
    The interop derives from the Siemens type libraries, so it is not distributed: the package is compiled against it
    but does not carry it, and the server generates it from the Solid Edge installed on the machine on its first start
    (scripts\generate-interop.ps1, bundled; cached in %LocalAppData%\SolidEdgeMcp\interop). The script needs the interop
    in lib\interop to compile, fails if any interop file or reference ends up in the package, and writes
    artifacts\solid-edge-mcp-<version>-win-x64.zip with its SHA-256.

.PARAMETER OutputDir
    Output folder. By default artifacts\ of the repository.
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SolidEdgeMcp.Server\SolidEdgeMcp.Server.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw "No <Version> in $project."
}

$name = "solid-edge-mcp-$version-win-x64"
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path
$stage = Join-Path $OutputDir $name
$zip = Join-Path $OutputDir "$name.zip"
foreach ($old in @($stage, $zip, "$zip.sha256")) {
    if (Test-Path -LiteralPath $old) {
        Remove-Item -LiteralPath $old -Recurse -Force
    }
}

& dotnet publish $project -c Release -r win-x64 --self-contained -o $stage -p:SolidEdgeInteropCopyLocal=false -nologo -v:q
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)."
}

# Nothing derived from Siemens files may be in the package.
$interop = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -like 'Interop.SolidEdge*' -or $_.Name -eq 'SOURCE.txt' }
if ($interop) {
    throw "The package contains interop files: $($interop.Name -join ', ')."
}
if ((Get-Content -LiteralPath (Join-Path $stage 'SolidEdgeMcp.Server.deps.json') -Raw) -match 'Interop\.SolidEdge') {
    throw 'SolidEdgeMcp.Server.deps.json still lists the interop.'
}
if (-not (Test-Path -LiteralPath (Join-Path $stage 'scripts\generate-interop.ps1'))) {
    throw 'The package does not carry scripts\generate-interop.ps1.'
}

Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'config\policy.example.json') -Destination (Join-Path $stage 'policy.example.json')

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline

$size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
"Package: $zip ($size MB)"
"SHA-256: $hash"
