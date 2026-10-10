<#
.SYNOPSIS
    Genera los ensamblados de interop de Solid Edge a partir de las type libraries instaladas.

.DESCRIPTION
    Usa System.Runtime.InteropServices.TypeLibConverter (lo mismo que tlbimp), que solo existe en
    .NET Framework; por eso se ejecuta con Windows PowerShell 5.1. Si se lanza desde PowerShell 7,
    se relanza a sí mismo con powershell.exe.

    Las SAFEARRAY se importan como System.Array y los [out, retval] de interfaces dispatch se
    transforman en valores de retorno, igual que la interop de la comunidad Solid Edge.

.PARAMETER ProgramDir
    Carpeta Program de Solid Edge. Por defecto se obtiene del registro COM de SolidEdge.Application.

.PARAMETER OutputDir
    Carpeta de salida. Por defecto lib\interop del repositorio.
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
    throw 'Ejecuta el script en un proceso de 64 bits.'
}

if (-not $ProgramDir) {
    $clsid = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\SolidEdge.Application\CLSID').'(default)'
    $server = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\$clsid\LocalServer32").'(default)'
    $exe = ($server -replace '\s+/.*$', '').Trim('"')
    $ProgramDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($exe))
}
$ProgramDir = (Resolve-Path -LiteralPath $ProgramDir).Path

# Bibliotecas que usa el servidor. Las referencias entre ellas se resuelven y convierten solas.
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
        throw "No existe la type library $path"
    }
    $path
}

# AssemblyBuilder.Save escribe en el directorio actual del dominio de aplicación.
[Environment]::CurrentDirectory = $OutputDir
$report = [SeInteropGenerator]::Run([string[]]$files, $OutputDir)

$version = (Get-Item -LiteralPath (Join-Path $ProgramDir 'Edge.exe')).VersionInfo.ProductVersion
Set-Content -LiteralPath (Join-Path $OutputDir 'SOURCE.txt') -Encoding UTF8 -Value @(
    "Generado: $(Get-Date -Format s)",
    "Solid Edge: $version",
    "Program: $ProgramDir"
)

"Interop generada en $OutputDir (Solid Edge $version):"
$report
