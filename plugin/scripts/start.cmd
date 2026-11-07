@echo off
rem Copyright (c) 2026 Gabriel Spoleto Couto
rem SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
rem
rem Starts the solid-edge-mcp server of this plugin's version. Standard output is the MCP channel: only the server
rem writes to it. install.ps1 -IfInstalled prints the path of the server when it is installed, with its standard input
rem set to NUL so that it cannot read the client's first messages. When it is not installed, bridge.ps1 installs it and
rem answers the client meanwhile, because the download can take longer than the client waits for a server to start.
setlocal
rem A client started from PowerShell 7 passes on its PSModulePath, whose modules Windows PowerShell 5.1 cannot load.
set "PSModulePath="
set "SERVER="
for /f "usebackq delims=" %%p in (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1" -IfInstalled ^<NUL`) do set "SERVER=%%p"
if not defined SERVER goto install
"%SERVER%" %*
exit /b
:install
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0bridge.ps1" %*
