# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Installs the server of the plugin's version, if needed, and prints the path of its executable.

.DESCRIPTION
    Run with Windows PowerShell 5.1: by start.cmd on every start with -IfInstalled, and by bridge.ps1 when that printed
    nothing. When %LocalAppData%\SolidEdgeMcp\server\<version> already holds the server it only prints its path; with
    -IfInstalled it stops there. Otherwise it downloads solid-edge-mcp-<version>-win-x64.zip and its .sha256 from the
    GitHub release v<version>, checks the hash, extracts the package beside the target folder and moves it into place,
    so an interrupted install leaves nothing half done. One install of a version runs at a time: another start waits for
    it and uses its result. The package carries no Solid Edge interop: the server generates it from the local
    installation on its first start.

    Standard output carries only the path; messages go to standard error, because the standard output of the server
    is the MCP channel. SOLID_EDGE_MCP_PACKAGE may point to a package zip, with its .sha256 beside it, to install it
    without a download (to try a build before its release): it goes to server\<version>-local-<start of its hash>.

.PARAMETER IfInstalled
    Prints the path only when the server is already installed, and never installs it.
#>
[CmdletBinding()]
param(
    [switch]$IfInstalled
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Say([string]$message) {
    [Console]::Error.WriteLine("solid-edge-mcp: $message")
}

$download = $null
$stage = $null
$lock = $null
try {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\.claude-plugin\plugin.json') -Raw | ConvertFrom-Json
    $version = [string]$manifest.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "The plugin version '$version' is not a release version."
    }

    $name = "solid-edge-mcp-$version-win-x64"
    $root = Join-Path $env:LOCALAPPDATA 'SolidEdgeMcp\server'
    $zip = $env:SOLID_EDGE_MCP_PACKAGE
    if ($zip) {
        # A local package gets a folder of its own, named after its hash: a rebuilt package is installed again, and the
        # release of the same version is not shadowed by a trial build.
        $packageHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $target = Join-Path $root "$version-local-$($packageHash.Substring(0, 12))"
    }
    else {
        $target = Join-Path $root $version
    }

    $exe = Join-Path $target 'SolidEdgeMcp.Server.exe'
    if ((Test-Path -LiteralPath $exe) -or $IfInstalled) {
        if (Test-Path -LiteralPath $exe) {
            Write-Output $exe
        }

        exit 0
    }

    # Two sessions starting at once share one download.
    $lock = New-Object Threading.Mutex($false, "Local\solid-edge-mcp-install-$(Split-Path -Leaf $target)")
    try {
        $owned = $lock.WaitOne([TimeSpan]::FromMinutes(15))
    }
    catch [Threading.AbandonedMutexException] {
        # The previous install was interrupted; this one owns the lock now.
        $owned = $true
    }

    if (-not $owned) {
        $lock = $null
        throw 'Another install of this version has been running for 15 minutes.'
    }

    if (Test-Path -LiteralPath $exe) {
        Write-Output $exe
        exit 0
    }

    New-Item -ItemType Directory -Path $root -Force | Out-Null
    if ($zip) {
        Say "Installing the server $version from '$zip'."
    }
    else {
        $download = Join-Path $root ".download-$PID"
        New-Item -ItemType Directory -Path $download -Force | Out-Null
        $zip = Join-Path $download "$name.zip"
        $url = "https://github.com/gabichuuuuu/solid-edge-mcp/releases/download/v$version/$name.zip"
        Say "Downloading the server $version (about 40 MB) from $url."
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        Invoke-WebRequest -Uri "$url.sha256" -OutFile "$zip.sha256" -UseBasicParsing
    }

    if (-not (Test-Path -LiteralPath "$zip.sha256")) {
        throw "'$zip.sha256' does not exist: the package cannot be checked."
    }

    $expected = ((Get-Content -LiteralPath "$zip.sha256" -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "The SHA-256 of '$zip' is ${actual}, not the ${expected} of its .sha256: the package is damaged."
    }

    $stage = Join-Path $root ".$version-$PID"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'SolidEdgeMcp.Server.exe'))) {
        throw "The package '$zip' does not contain SolidEdgeMcp.Server.exe."
    }

    if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $exe)) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    try {
        # Unlike Move-Item, which would move the folder into an existing one, this fails when the target exists.
        [IO.Directory]::Move($stage, $target)
    }
    catch {
        # Another start installed it at the same time; the stage is removed below.
        if (-not (Test-Path -LiteralPath $exe)) {
            throw
        }
    }

    Say "Installed the server $version in '$target'."
    Write-Output $exe
}
catch {
    Say "The server could not be installed: $($_.Exception.Message)"
    exit 1
}
finally {
    foreach ($temporary in @($download, $stage)) {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) {
            Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if ($lock) {
        $lock.ReleaseMutex()
    }
}
