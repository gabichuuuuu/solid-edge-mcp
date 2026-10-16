@echo off
rem Copyright (c) 2026 Gabriel Spoleto Couto
rem SPDX-License-Identifier: AGPL-3.0-only
rem
rem Starts the solid-edge-mcp server of this plugin's version, installing it first when needed (install.ps1).
rem Standard output is the MCP channel: only the server writes to it. install.ps1 prints the path of the
rem server on standard output and its messages on standard error, and its standard input is NUL so that it
rem cannot read the client's first messages.
setlocal
set "SERVER="
for /f "usebackq delims=" %%p in (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1" ^<NUL`) do set "SERVER=%%p"
if not defined SERVER exit /b 1
"%SERVER%" %*
