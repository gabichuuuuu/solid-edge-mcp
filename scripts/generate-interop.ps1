# Copyright (c) 2026 Gabriel Spoleto Couto
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

<#
.SYNOPSIS
    Generates the Solid Edge interop assemblies from the installed type libraries.

.DESCRIPTION
    Uses System.Runtime.InteropServices.TypeLibConverter (the same as tlbimp), which only exists in
    .NET Framework; that is why it runs with Windows PowerShell 5.1. If it is launched from PowerShell 7,
    it relaunches itself with powershell.exe.

    SAFEARRAYs are imported as System.Array and the [out, retval] of dispatch interfaces are
    turned into return values, as the community Solid Edge interop does.

.PARAMETER ProgramDir
    Solid Edge Program folder. By default it is read from the COM registry of SolidEdge.Application.

.PARAMETER OutputDir
    Output folder. By default lib\interop of the repository.
#>
[CmdletBinding()]
param(
    [string]$ProgramDir,
    [string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\interop')
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    $argsList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-OutputDir', $OutputDir)
    if ($ProgramDir) { $argsList += @('-ProgramDir', $ProgramDir) }
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @argsList
    exit $LASTEXITCODE
}

if (-not [Environment]::Is64BitProcess) {
    throw 'Run the script in a 64-bit process.'
}

if (-not $ProgramDir) {
    $clsid = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\SolidEdge.Application\CLSID').'(default)'
    $server = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\LocalServer32").'(default)'
    $exe = ($server -replace '\s+/.*$', '').Trim('"')
    $ProgramDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($exe))
}
$ProgramDir = (Resolve-Path -LiteralPath $ProgramDir).Path

# Libraries used by the server. References between them are resolved and converted automatically.
$typeLibs = @('constant.tlb', 'framewrk.tlb', 'fwksupp.tlb', 'geometry.tlb', 'Part.tlb', 'assembly.tlb', 'draft.tlb')

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class SeInteropGenerator
{
    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string file, int regKind, out ITypeLib typeLib);

    private const int RegKindNone = 2;

    private static readonly Dictionary<Guid, Assembly> Cache = new Dictionary<Guid, Assembly>();
    private static readonly List<string> Generated = new List<string>();
    private static readonly List<string> Messages = new List<string>();
    private static string outputDir;

    private sealed class Sink : ITypeLibImporterNotifySink
    {
        public void ReportEvent(ImporterEventKind eventKind, int eventCode, string eventMsg)
        {
            if (eventKind != ImporterEventKind.NOTIF_TYPECONVERTED)
            {
                Messages.Add(eventKind + ": " + eventMsg);
            }
        }

        public Assembly ResolveRef(object typeLib)
        {
            return Convert((ITypeLib)typeLib);
        }
    }

    public static string[] Run(string[] files, string outDir)
    {
        outputDir = outDir;
        foreach (string file in files)
        {
            ITypeLib typeLib;
            LoadTypeLibEx(file, RegKindNone, out typeLib);
            Convert(typeLib);
        }
        var result = new List<string>(Generated);
        foreach (string message in Messages)
        {
            result.Add("  " + message);
        }
        return result.ToArray();
    }

    private static Assembly Convert(ITypeLib typeLib)
    {
        IntPtr attrPtr;
        typeLib.GetLibAttr(out attrPtr);
        var attr = (System.Runtime.InteropServices.ComTypes.TYPELIBATTR)Marshal.PtrToStructure(
            attrPtr, typeof(System.Runtime.InteropServices.ComTypes.TYPELIBATTR));
        typeLib.ReleaseTLibAttr(attrPtr);

        Assembly existing;
        if (Cache.TryGetValue(attr.guid, out existing))
        {
            return existing;
        }

        string libName = Marshal.GetTypeLibName(typeLib);
        string fileName = "Interop." + libName + ".dll";
        string fullPath = Path.Combine(outputDir, fileName);

        var converter = new TypeLibConverter();
        AssemblyBuilder builder = converter.ConvertTypeLibToAssembly(
            typeLib,
            fullPath,
            TypeLibImporterFlags.SafeArrayAsSystemArray | TypeLibImporterFlags.TransformDispRetVals,
            new Sink(),
            null,
            null,
            libName,
            null);

        Cache[attr.guid] = builder;
        builder.Save(fileName);
        Generated.Add(fileName + "  (" + libName + " " + attr.wMajorVerNum + "." + attr.wMinorVerNum + ", " + attr.guid + ")");
        return builder;
    }
}
'@

$files = foreach ($name in $typeLibs) {
    $path = Join-Path $ProgramDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The type library $path does not exist"
    }
    $path
}

# AssemblyBuilder.Save writes to the current directory of the application domain.
[Environment]::CurrentDirectory = $OutputDir
$report = [SeInteropGenerator]::Run([string[]]$files, $OutputDir)

$version = (Get-Item -LiteralPath (Join-Path $ProgramDir 'Edge.exe')).VersionInfo.ProductVersion
Set-Content -LiteralPath (Join-Path $OutputDir 'SOURCE.txt') -Encoding UTF8 -Value @(
    "Generated: $(Get-Date -Format s)",
    "Solid Edge: $version",
    "Program: $ProgramDir"
)

"Interop generated in $OutputDir (Solid Edge $version):"
$report
