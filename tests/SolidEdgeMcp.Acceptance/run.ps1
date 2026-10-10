# Ejecuta los guiones de aceptación contra el servidor compilado y Solid Edge real, uno detrás de otro.
# Uso (desde la raíz, tras dotnet build SolidEdgeMcp.slnx): .\tests\SolidEdgeMcp.Acceptance\run.ps1 [A01 A03 ...]
# Crea y cierra sin guardar sus propios documentos; los archivos van a la carpeta de trabajo
# (%LocalAppData%\SolidEdgeMcp\work\acceptance o SE_MCP_WORK), y el registro de cada guion a logs\ dentro de ella.
param(
    [string[]]$Names = @('A'),
    [string]$Configuration = 'Debug',
    [string]$Policy = (Join-Path $env:LOCALAPPDATA 'SolidEdgeMcp\policy.json'),
    # Otro ejecutable del servidor, p. ej. publish\SolidEdgeMcp.Server.exe para validar la publicación.
    [string]$Server
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$server = if ($Server) { (Resolve-Path $Server).Path } else { Join-Path $root "src\SolidEdgeMcp.Server\bin\$Configuration\net10.0-windows\SolidEdgeMcp.Server.exe" }
# La solución compila los tests para x64 (bin\x64\...); compilando solo el proyecto, la salida va a bin\...
$runner = "bin\x64\$Configuration", "bin\$Configuration" |
    ForEach-Object { Join-Path $PSScriptRoot "$_\net10.0-windows\SolidEdgeMcp.Acceptance.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $runner) { $runner = Join-Path $PSScriptRoot "bin\x64\$Configuration\net10.0-windows\SolidEdgeMcp.Acceptance.exe" }
foreach ($exe in $server, $runner) {
    if (-not (Test-Path $exe)) { throw "No existe $exe; compila antes con: dotnet build SolidEdgeMcp.slnx" }
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

Write-Output "$($scripts.Count) guiones, $failed con fallos. Registros: $logs"
exit $(if ($failed -eq 0) { 0 } else { 1 })
