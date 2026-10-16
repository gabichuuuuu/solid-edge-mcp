# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: AGPL-3.0-only

<#
.SYNOPSIS
    Dumps the interfaces, members and enumerations of the generated interop as text.

.DESCRIPTION
    Creates one file per assembly in reference\api (not versioned). It is used to look up signatures
    and real constants of the Solid Edge API instead of guessing them.

.PARAMETER InteropDir
    Folder with the generated interop assemblies. By default lib\interop of the repository.

.PARAMETER OutputDir
    Output folder. By default reference\api of the repository.
#>
[CmdletBinding()]
param(
    [string]$InteropDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\interop'),
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'reference\api')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

function Format-TypeName([Type]$type) {
    if ($type.IsByRef) { return (Format-TypeName $type.GetElementType()) + '&' }
    if ($type.IsArray) { return (Format-TypeName $type.GetElementType()) + '[]' }
    if ($type.IsGenericType) {
        $name = $type.Name.Substring(0, $type.Name.IndexOf('`'))
        return $name + '<' + (($type.GetGenericArguments() | ForEach-Object { Format-TypeName $_ }) -join ', ') + '>'
    }
    switch ($type.FullName) {
        'System.Object' { 'object' } 'System.String' { 'string' } 'System.Int32' { 'int' }
        'System.Int16' { 'short' } 'System.Double' { 'double' } 'System.Single' { 'float' }
        'System.Boolean' { 'bool' } 'System.Void' { 'void' } 'System.Array' { 'Array' }
        'System.Int64' { 'long' } 'System.Byte' { 'byte' } 'System.UInt32' { 'uint' }
        default { if ($type.Namespace -like 'SolidEdge*') { $type.Namespace + '.' + $type.Name } else { $type.Name } }
    }
}

function Format-Parameter([System.Reflection.ParameterInfo]$p) {
    # An [in, out] by reference is passed with ref in C#; only [out] is passed with out.
    $prefix = if (-not $p.ParameterType.IsByRef) { '' } elseif ($p.IsOut -and -not $p.IsIn) { 'out ' } else { 'ref ' }
    $type = Format-TypeName ($(if ($p.ParameterType.IsByRef) { $p.ParameterType.GetElementType() } else { $p.ParameterType }))
    $text = "$prefix$type $($p.Name)"
    if ($p.IsOptional) {
        $default = if ($p.HasDefaultValue -and $null -ne $p.DefaultValue -and $p.DefaultValue -isnot [System.Reflection.Missing]) { " = $($p.DefaultValue)" } else { '' }
        $text = "[opt] $text$default"
    }
    $text
}

foreach ($file in Get-ChildItem -LiteralPath $InteropDir -Filter 'Interop.*.dll') {
    $assembly = [System.Reflection.Assembly]::LoadFrom($file.FullName)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("// $($file.Name)")
    foreach ($type in ($assembly.GetExportedTypes() | Sort-Object FullName)) {
        if ($type.IsEnum) {
            $lines.Add("enum $($type.FullName)")
            foreach ($name in [Enum]::GetNames($type)) {
                $value = [Convert]::ToInt64([Enum]::Parse($type, $name))
                $lines.Add("  $name = $value")
            }
        }
        elseif ($type.IsInterface) {
            if ($type.Name.StartsWith('_') -or $type.Name -like '*_Event' -or $type.Name -like '*_SinkHelper') { continue }
            $guid = ($type.GetCustomAttributes([System.Runtime.InteropServices.GuidAttribute], $false) | Select-Object -First 1).Value
            $bases = ($type.GetInterfaces() | Where-Object { $_.Namespace -like 'SolidEdge*' } | ForEach-Object { Format-TypeName $_ }) -join ', '
            $lines.Add("interface $($type.FullName)$(if ($bases) { " : $bases" }) {$guid}")
            foreach ($prop in ($type.GetProperties() | Sort-Object Name)) {
                $index = $prop.GetIndexParameters()
                $indexText = if ($index.Count) { '[' + (($index | ForEach-Object { Format-Parameter $_ }) -join ', ') + ']' } else { '' }
                $access = @(if ($prop.CanRead) { 'get' }; if ($prop.CanWrite) { 'set' }) -join '/'
                $lines.Add("  P $(Format-TypeName $prop.PropertyType) $($prop.Name)$indexText { $access }")
            }
            foreach ($method in ($type.GetMethods() | Where-Object { -not $_.IsSpecialName } | Sort-Object Name)) {
                $params = ($method.GetParameters() | ForEach-Object { Format-Parameter $_ }) -join ', '
                $lines.Add("  M $(Format-TypeName $method.ReturnType) $($method.Name)($params)")
            }
        }
        elseif ($type.IsClass -and $type.Name -like '*Class') {
            continue
        }
    }
    $target = Join-Path $OutputDir (($file.BaseName -replace '^Interop\.', '') + '.txt')
    [System.IO.File]::WriteAllLines($target, $lines, [System.Text.UTF8Encoding]::new($false))
    '{0,-32} {1,7} lines' -f (Split-Path -Leaf $target), $lines.Count
}
