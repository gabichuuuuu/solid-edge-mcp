// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>The mesh of the 3D Print command (Application menu > Print > 3D Print) of a part, sheet metal or assembly.</summary>
public static class Print3dService
{
    /// <summary>
    /// Mesh information as the 3D Print command computes it. Verified (block 100 x 60 x 20 with two 10 mm holes,
    /// 116858.41 mm³, 19342.48 mm²): Get3dPrintInfo gives 132 triangles, mesh area 19342.38 mm² and volume 116972.35 mm³ for
    /// both file types. The estimated file size and export tolerance it also returns look swapped between the types (the
    /// 3MF value is exactly a binary STL of the mesh, 84 + 50 bytes per triangle, less 4), so they are left out. Its
    /// tolerances are those of the 3D Print command, not those of se_export (the STL export of a similar block had 120
    /// triangles where the command counted 72).
    /// </summary>
    public static object Mesh(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        const Print3DFileType type = Print3DFileType.e3DPrint_STL;
        int triangles, points, edges;
        double area, volume, angle;
        switch (document)
        {
            case PartDocument part:
                part.Get3dPrintInfo(out triangles, out points, out edges, out area, out volume, out _, out _, out angle, type);
                break;
            case SheetMetalDocument sheet:
                sheet.Get3dPrintInfo(out triangles, out points, out edges, out area, out volume, out _, out _, out angle, type);
                break;
            case WeldmentDocument weldment:
                weldment.Get3dPrintInfo(out triangles, out points, out edges, out area, out volume, out _, out _, out angle, type);
                break;
            case AssemblyDocument assembly:
                assembly.Get3dPrintInfo(out triangles, out points, out edges, out area, out volume, out _, out _, out angle, type);
                break;
            default:
                throw ToolException.InvalidArgument($"'{document.Name}' is not a part, sheet metal, weldment or assembly: it has no 3D print mesh.");
        }

        if (triangles <= 0)
        {
            throw new ToolException("no_mesh", $"Solid Edge made no mesh of '{document.Name}'.", "Check that the document has a solid (se_geometry summary).");
        }

        double? modelVolume = document is PartDocument or SheetMetalDocument && ModelDocument.Get(context, documentHandle).TryBodies() is { Count: > 0 } bodies
            ? bodies.Sum(b => b.Body.Volume) : null;
        return new
        {
            document = document.Name,
            triangles,
            points,
            edges,
            meshArea = Geo.Mm2(area),
            meshVolume = Geo.Mm3(volume),
            modelVolume = modelVolume is { } v ? Geo.Mm3(v) : (double?)null,
            volumeDeviationPercent = modelVolume is { } m && m > 0 ? Math.Round((volume - m) / m * 100, 4) : (double?)null,
            angleTolerance = Math.Round(Units.RadToDeg(angle), 6),
            note = "The mesh of Solid Edge's 3D Print command, with its own tolerances; se_export .stl or .3mf writes the file with the export options.",
        };
    }
}
