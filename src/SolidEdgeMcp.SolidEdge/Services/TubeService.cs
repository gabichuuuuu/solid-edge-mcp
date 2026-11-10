// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Tubes of an assembly routed along a path of straight segments (XpresRoute, Occurrences.AddTube). Verified in Solid Edge
/// 2026 Community Edition: AddTube builds a tube part from line segments of the assembly (LineSegments.Add, in metres), with
/// a bend of the given radius at every corner, solid or hollow (WallThickness); it gives E_FAIL while the assembly has never
/// been saved. An L of 100 + 80 mm, outer diameter 12, wall 1 and bend radius 20 gives a flat length of 171.42 mm
/// (80 + 60 + π/2·20) and 5923.71 mm³ (π/4·(12² − 10²)·171.42); solid, 19386.68 mm³. Occurrence.GetTube reads its data.
/// </summary>
public static class TubeService
{
    private const TubePropertyPidConstants BendRadiusPid = TubePropertyPidConstants.seTubePropertyPid_TubeBendRadius;
    private const TubePropertyPidConstants OuterDiameterPid = TubePropertyPidConstants.seTubePropertyPid_TubeOuterDiameter;
    private const TubePropertyPidConstants WallPid = TubePropertyPidConstants.seTubePropertyPid_TubeWallThickness;
    private const TubePropertyPidConstants FlatLengthPid = TubePropertyPidConstants.seTubePropertyPid_TubeFlatLength;

    /// <summary>Routes a tube along a polyline (mm) and checks the new occurrence: a tube with volume.</summary>
    public static object Add(SeContext context, string? documentHandle, double[]? points, double? outerDiameter, double? wall,
        double? bendRadius, string? material, string? path, bool overwrite)
    {
        AssemblyDocument assembly = AssemblyService.Assembly(context, documentHandle);
        if (points is null || points.Length < 6 || points.Length % 3 != 0)
        {
            throw ToolException.InvalidArgument("'points' takes the path of the tube as [x1, y1, z1, x2, y2, z2, ...] in mm: two points or more.");
        }

        if (outerDiameter is not > 0)
        {
            throw ToolException.InvalidArgument("'outerDiameter' (mm) is required and must be positive.");
        }

        double thickness = wall ?? 0;
        if (thickness < 0 || thickness * 2 >= outerDiameter)
        {
            throw ToolException.InvalidArgument($"'wall' must be between 0 (a solid rod) and less than half the outer diameter ({outerDiameter / 2} mm).");
        }

        double radius = bendRadius ?? 2 * outerDiameter.Value;
        if (radius <= outerDiameter / 2)
        {
            throw ToolException.InvalidArgument($"'bendRadius' must be larger than half the outer diameter ({outerDiameter / 2} mm).");
        }

        var corners = Enumerable.Range(0, points.Length / 3).Select(i => new[] { points[3 * i], points[(3 * i) + 1], points[(3 * i) + 2] }).ToList();
        for (int i = 1; i < corners.Count; i++)
        {
            if (Geo.Distance(corners[i - 1], corners[i]) < 1e-6)
            {
                throw ToolException.InvalidArgument($"Points {i} and {i + 1} of the path are the same.");
            }
        }

        string? folder = DocumentService.PathOf((SolidEdgeDocument)assembly) is { } saved ? Path.GetDirectoryName(saved) : null;
        if (folder is null)
        {
            throw new ToolException("not_saved", "Solid Edge routes tubes only in an assembly that has been saved (AddTube gives E_FAIL before).",
                "Save the assembly first (se_documents save_as), then add the tube.");
        }

        // Verified with Solid Edge 2026 Foundation: a material the libraries do not have gives only E_INVALIDARG.
        string? tubeMaterial = string.IsNullOrWhiteSpace(material) ? null : MaterialService.RequireMaterial(context, material.Trim());
        string file = context.Paths.Check(path ?? NextTubeFile(folder, ((SolidEdgeDocument)assembly).Name), "path");
        if (!string.Equals(Path.GetExtension(file), ".par", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("A tube is a part: 'path' must be a .par file.");
        }

        if (File.Exists(file))
        {
            if (!overwrite)
            {
                throw new ToolException("file_exists", $"'{file}' already exists.", "Pass overwrite=true to replace it, or another 'path'.");
            }

            File.Delete(file);
        }

        string template = context.Application.GetDefaultTemplatePath(DocumentTypeConstants.igPartDocument);
        if (!File.Exists(template))
        {
            throw new ToolException("no_template", "This installation has no default part template for the tube.",
                "Set a default part template in Solid Edge (se_app templates).");
        }

        var segments = new List<object>();
        for (int i = 1; i < corners.Count; i++)
        {
            Array start = corners[i - 1].Select(Units.MmToM).ToArray();
            Array end = corners[i].Select(Units.MmToM).ToArray();
            segments.Add(assembly.LineSegments.Add(ref start, ref end));
        }

        int before = assembly.Occurrences.Count;
        Occurrence occurrence;
        try
        {
            Array path3d = segments.ToArray();
            string name = file;
            occurrence = assembly.Occurrences.AddTube(ref path3d, ref name, template, thickness == 0,
                tubeMaterial ?? Type.Missing, Units.MmToM(radius), Units.MmToM(outerDiameter.Value),
                Type.Missing, thickness > 0 ? Units.MmToM(thickness) : Type.Missing);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is COMException com && !ComErrors.IsGone(com))
        {
            DeleteSegments(segments);
            throw new ToolException("tube_failed", $"Solid Edge could not route the tube: {ex.Message.Trim()}",
                "Check that consecutive segments are not parallel back on themselves and that the bend radius fits the shortest segment.", ex);
        }

        if (assembly.Occurrences.Count != before + 1 || !occurrence.IsTube() || AssemblyService.PartOf(occurrence).Volume() <= 0)
        {
            try
            {
                occurrence.Delete();
            }
            catch (COMException)
            {
                // Removed with the segments below or left for the person to see.
            }

            DeleteSegments(segments);
            throw new ToolException("tube_failed", "Solid Edge made no tube solid along the path.",
                "Check the bend radius against the segment lengths and the outer diameter.");
        }

        return new { tube = Describe(context, assembly, occurrence) };
    }

    /// <summary>The tubes of an assembly with their sizes, flat length and path.</summary>
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = AssemblyService.Assembly(context, documentHandle);
        var tubes = Late.Items(assembly.Occurrences).OfType<Occurrence>().Where(IsTube).Select(o => Describe(context, assembly, o)).ToList();
        return new { document = ((SolidEdgeDocument)assembly).Name, count = tubes.Count, tubes };
    }

    private static bool IsTube(Occurrence occurrence)
    {
        try
        {
            return occurrence.IsTube();
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, Occurrence occurrence)
    {
        Tube tube = occurrence.GetTube();
        tube.Path(out int count, out object segments, out _);
        var points = new List<double[]>();
        foreach (object segment in segments as object[] ?? (segments as Array)?.Cast<object>().ToArray() ?? [])
        {
            if (segment is LineSegment line)
            {
                line.GetStartPoint(out double x0, out double y0, out double z0);
                line.GetEndPoint(out double x1, out double y1, out double z1);
                double[] start = [Geo.Mm(x0), Geo.Mm(y0), Geo.Mm(z0)];
                if (points.Count == 0 || Geo.Distance(points[^1], start) > 1e-6)
                {
                    points.Add(start);
                }

                points.Add([Geo.Mm(x1), Geo.Mm(y1), Geo.Mm(z1)]);
            }
        }

        ModelDocument part = AssemblyService.PartOf(occurrence);
        occurrence.Range(out double ax, out double ay, out double az, out double bx, out double by, out double bz);
        double wall = Property(assembly, tube, WallPid) ?? 0;
        return new
        {
            handle = context.Register(occurrence, "occurrence", (SolidEdgeDocument)assembly),
            name = occurrence.Name,
            file = occurrence.OccurrenceFileName,
            outerDiameter = Property(assembly, tube, OuterDiameterPid),
            wall = wall > 0 ? wall : (double?)null,
            solid = wall <= 0,
            bendRadius = Property(assembly, tube, BendRadiusPid),
            flatLength = Property(assembly, tube, FlatLengthPid),
            material = Late.TryGetString(tube, "TubeMaterial") is { } m && m != "(None)" ? m : null,
            volume = Geo.Mm3(part.Volume()),
            boundingBox = new { min = new[] { Geo.Mm(ax), Geo.Mm(ay), Geo.Mm(az) }, max = new[] { Geo.Mm(bx), Geo.Mm(by), Geo.Mm(bz) } },
            segments = count,
            points,
        };
    }

    /// <summary>
    /// A tube property in mm. Solid Edge formats it in the distance unit of the assembly, with the unit ("12.00 mm") or
    /// without it (verified with Solid Edge 2026 Foundation: "12.00", and "0.472" once the assembly is in inches), so it is
    /// read back with the assembly's ParseUnit.
    /// </summary>
    private static double? Property(AssemblyDocument assembly, Tube tube, TubePropertyPidConstants pid)
    {
        try
        {
            tube.GetTubePropertyNameAndValueByPid(pid, out _, out string value);
            object metres = ((SolidEdgeDocument)assembly).UnitsOfMeasure.ParseUnit((int)UnitTypeConstants.igUnitDistance, value.Trim());
            return Geo.Mm(Convert.ToDouble(metres, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            return null;
        }
    }

    private static void DeleteSegments(IEnumerable<object> segments)
    {
        foreach (object segment in segments)
        {
            try
            {
                Late.Call(segment, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                // A segment Solid Edge does not let go stays in the assembly; it does not change any part.
            }
        }
    }

    private static string NextTubeFile(string folder, string assemblyName)
    {
        string stem = Path.GetFileNameWithoutExtension(assemblyName);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{stem}_tube{i}.par");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
