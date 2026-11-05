# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Starts the server when it is not installed yet: installs it and, while that lasts, answers the MCP client.

.DESCRIPTION
    Run by start.cmd, with Windows PowerShell 5.1, when the server of the plugin's version is not installed. The download
    (about 40 MB) can take longer than the client waits for a server to start (30 seconds in Claude Code, which then
    does not try again for 15 minutes), so:

    - It runs install.ps1 and waits up to 10 seconds without reading standard input. If the server is installed by then,
      it starts it on the same standard input and output, as start.cmd does.
    - Otherwise it answers the client itself, as a server with no tools yet: the server/discover probe of the 2026-07-28
      protocol gets "method not found", so the client falls back to the initialize handshake, which the server also
      supports. When the install ends, it starts the server, repeats the client's initialize to it, tells the client
      that the tool and prompt lists changed, and passes the messages both ways until either side closes.

    Standard output is the MCP channel; messages go to standard error. The server's own instructions reach the client
    only in the next session, because they are part of the initialize answer.
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ServerArguments = @()
)

$ErrorActionPreference = 'Stop'
$waitSeconds = 10
# The initialize versions of the bundled MCP SDK; it refuses any other.
$versions = @('2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05')

function Say([string]$message) {
    [Console]::Error.WriteLine("solid-edge-mcp: $message")
}

function Quote([string[]]$values) {
    ($values | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
}

function Get-ServerPath($install, $output) {
    if ($install.ExitCode -ne 0) {
        return $null
    }

    $lines = @($output.Result -split "`r?`n" | Where-Object { $_.Trim() })
    if ($lines.Count -eq 0) {
        return $null
    }

    return $lines[-1].Trim()
}

function Send($writer, [string]$id, [string]$body) {
    $writer.WriteLine('{"jsonrpc":"2.0","id":' + $id + ',' + $body + '}')
}

try {
    $setup = New-Object Diagnostics.ProcessStartInfo
    $setup.FileName = Join-Path $PSHOME 'powershell.exe'
    $setup.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote @(Join-Path $PSScriptRoot 'install.ps1'))
    $setup.UseShellExecute = $false
    $setup.RedirectStandardInput = $true
    $setup.RedirectStandardOutput = $true
    $install = [Diagnostics.Process]::Start($setup)
    $install.StandardInput.Close()
    $installOutput = $install.StandardOutput.ReadToEndAsync()

    if ($install.WaitForExit($waitSeconds * 1000)) {
        $install.WaitForExit()
        $exe = Get-ServerPath $install $installOutput
        if (-not $exe) {
            exit 1
        }

        # Nothing has been read from standard input: the server inherits it and the client's first message is still there.
        $start = New-Object Diagnostics.ProcessStartInfo $exe, (Quote $ServerArguments)
        $start.UseShellExecute = $false
        $server = [Diagnostics.Process]::Start($start)
        $server.WaitForExit()
        exit $server.ExitCode
    }

    Say 'Answering the client until the server is installed; its tools will appear then.'
    $utf8 = New-Object Text.UTF8Encoding $false
    $clientIn = New-Object IO.StreamReader ([Console]::OpenStandardInput()), $utf8
    $clientOut = New-Object IO.StreamWriter ([Console]::OpenStandardOutput()), $utf8
    $clientOut.AutoFlush = $true
    $clientOut.NewLine = "`n"

    $initialize = $null
    $initializeId = $null
    $initialized = $false
    $read = $clientIn.ReadLineAsync()
    while (-not $install.HasExited) {
        if (-not $read.Wait(250)) {
            continue
        }

        $line = $read.Result
        if ($null -eq $line) {
            Say 'The client closed the connection; the install goes on.'
            exit 0
        }

        $read = $clientIn.ReadLineAsync()
        try {
            $message = $line | ConvertFrom-Json
        }
        catch {
            continue
        }

        $method = [string]$message.method
        if (-not $method) {
            continue
        }

        if ($message.PSObject.Properties.Name -notcontains 'id') {
            if ($method -eq 'notifications/initialized') {
                $initialized = $true
            }

            continue
        }

        $id = ConvertTo-Json $message.id -Compress
        switch ($method) {
            'initialize' {
                $requested = [string]$message.params.protocolVersion
                if ($versions -notcontains $requested) {
                    $refusal = "Unsupported protocol version '$requested'; supported: $($versions -join ', ')."
                    Send $clientOut $id ('"error":{"code":-32602,"message":' + (ConvertTo-Json $refusal -Compress) + '}')
                    break
                }

                $initialize = $line
                $initializeId = $id
                $instructions = 'The solid-edge-mcp server is being installed (the first start of this plugin version downloads about 40 MB). Its tools appear as soon as it is ready; until then there are none. Its own usage instructions arrive in the next session.'
                Send $clientOut $id ('"result":{"protocolVersion":"' + $requested + '","capabilities":{"logging":{},"prompts":{"listChanged":true},"tools":{"listChanged":true}},"serverInfo":{"name":"solid-edge","version":"installing"},"instructions":' + (ConvertTo-Json $instructions -Compress) + '}')
            }
            'ping' { Send $clientOut $id '"result":{}' }
            'tools/list' { Send $clientOut $id '"result":{"tools":[]}' }
            'prompts/list' { Send $clientOut $id '"result":{"prompts":[]}' }
            'logging/setLevel' { Send $clientOut $id '"result":{}' }
            default { Send $clientOut $id '"error":{"code":-32601,"message":"Method not found: the solid-edge-mcp server is still being installed."}' }
        }
    }

    $install.WaitForExit()
    $exe = Get-ServerPath $install $installOutput
    if (-not $exe) {
        Say 'The server could not be installed: its tools will not appear. Start a new session to try again.'
        exit 1
    }

    $start = New-Object Diagnostics.ProcessStartInfo $exe, (Quote $ServerArguments)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = $utf8
    $server = [Diagnostics.Process]::Start($start)
    # Process.StandardInput would encode with the console code page.
    $serverIn = New-Object IO.StreamWriter $server.StandardInput.BaseStream, $utf8
    $serverIn.AutoFlush = $true
    $serverIn.NewLine = "`n"
    $serverOut = $server.StandardOutput

    if ($initialize) {
        # The client already has an answer to its initialize: the server's goes no further.
        $serverIn.WriteLine($initialize)
        while ($true) {
            $line = $serverOut.ReadLine()
            if ($null -eq $line) {
                Say 'The server closed before answering initialize.'
                exit 1
            }

            $answer = $null
            try {
                $answer = $line | ConvertFrom-Json
            }
            catch {
            }

            if ($answer -and -not $answer.method -and $answer.PSObject.Properties.Name -contains 'id' -and (ConvertTo-Json $answer.id -Compress) -eq $initializeId) {
                if ($answer.error) {
                    Say "The server refused initialize: $($answer.error.message)"
                    exit 1
                }

                break
            }

            $clientOut.WriteLine($line)
        }

        if ($initialized) {
            $serverIn.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        }

        $clientOut.WriteLine('{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}')
        $clientOut.WriteLine('{"jsonrpc":"2.0","method":"notifications/prompts/list_changed"}')
        Say 'The server is installed and running; its tools are available.'
    }

    $fromServer = $serverOut.ReadLineAsync()
    while ($true) {
        [void][Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]@($read, $fromServer))
        if ($read.IsCompleted) {
            $line = $read.Result
            if ($null -eq $line) {
                $serverIn.Close()
                if (-not $server.WaitForExit(10000)) {
                    $server.Kill()
                }

                exit 0
            }

            $serverIn.WriteLine($line)
            $read = $clientIn.ReadLineAsync()
        }

        if ($fromServer.IsCompleted) {
            $line = $fromServer.Result
            if ($null -eq $line) {
                $server.WaitForExit()
                exit $server.ExitCode
            }

            $clientOut.WriteLine($line)
            $fromServer = $serverOut.ReadLineAsync()
        }
    }
}
catch {
    Say "The server could not be started: $($_.Exception.Message)"
    exit 1
}
