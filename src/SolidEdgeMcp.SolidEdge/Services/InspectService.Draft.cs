// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeGeometry;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class InspectService
{
    /// <summary>Samples per parameter of a curved face (planar faces have one normal).</summary>
    private const int DraftSamples = 9;

    /// <summary>
    /// Draft analysis along a pull direction, from the face normals (the Draft Face Analysis command waits for faces to be picked
    /// and ViewStyle.SetAnalysisParameters gives E_NOINTERFACE). The draft of a face at a point is the angle between the face
    /// and the pull direction, asin(n·d) in degrees: positive where the outward normal leans towards the pull direction, 0 on a
    /// wall parallel to it, 90 on a face square to it. Curved faces are sampled on a grid of their parameters, keeping the
    /// points on the face. Each face is positive (all of it at least the minimum draft), negative (all at most minus it),
    /// insufficient (all within it) or mixed.
    /// </summary>
    public static object Draft(SeContext context, string? documentHandle, string? source, double[]? direction, double minDraft, int offset, int limit)
    {
        double[] pull = CheckDirection(direction) ?? throw ToolException.InvalidArgument("draft needs 'direction': the pull direction [x, y, z] of the mould.");
        double length = Geo.Length(pull);
        pull = [.. pull.Select(v => v / length)];
        if (!(minDraft >= 0 && minDraft < 90))
        {
            throw ToolException.InvalidArgument("'minDraft' must be from 0 to less than 90 degrees.");
        }

        TopologySource topology = Topology(context, documentHandle, source, "Faces", FeatureTopologyQueryTypeConstants.igQueryAll);
        var rows = new List<(Face Face, double Min, double Max, string Class)>();
        foreach (Face face in Late.Items(topology.Collection).OfType<Face>())
        {
            (double min, double max) = DraftRange(face, topology.Placement, pull);
            string kind = min >= minDraft - 1e-9 ? "positive"
                : max <= -minDraft + 1e-9 ? "negative"
                : min > -minDraft && max < minDraft ? "insufficient"
                : "mixed";
            rows.Add((face, min, max, kind));
        }

        int skip = Math.Max(0, offset);
        int take = Math.Clamp(limit, 1, 500);
        return new
        {
            direction = pull.Select(Geo.R).ToArray(),
            minDraft,
            count = rows.Count,
            counts = new
            {
                positive = rows.Count(r => r.Class == "positive"),
                negative = rows.Count(r => r.Class == "negative"),
                insufficient = rows.Count(r => r.Class == "insufficient"),
                mixed = rows.Count(r => r.Class == "mixed"),
            },
            offset = skip,
            more = skip + take < rows.Count,
            faces = rows.Skip(skip).Take(take).Select(r => new
            {
                handle = context.Register(topology.Wrap(r.Face), "face", topology.Owner),
                type = SurfaceType(r.Face.Geometry),
                area = Geo.Mm2(r.Face.Area),
                draft = new[] { Math.Round(r.Min, 4), Math.Round(r.Max, 4) },
                @class = r.Class,
            }).ToArray(),
        };
    }

    /// <summary>Least and greatest draft (degrees) of a face along the unit pull direction.</summary>
    private static (double Min, double Max) DraftRange(Face face, Placement? placement, double[] pull)
    {
        if (face.Geometry is Plane)
        {
            double draft = DraftOf(Rotated(placement, FaceNormal(face)), pull);
            return (draft, draft);
        }

        Array lo = new double[2];
        Array hi = new double[2];
        face.GetParamRange(ref lo, ref hi);
        double[] a = [.. Geo.Doubles(lo)];
        double[] b = [.. Geo.Doubles(hi)];
        var grid = new List<double>();
        for (int i = 0; i < DraftSamples; i++)
        {
            for (int j = 0; j < DraftSamples; j++)
            {
                grid.Add(a[0] + (b[0] - a[0]) * i / (DraftSamples - 1));
                grid.Add(a[1] + (b[1] - a[1]) * j / (DraftSamples - 1));
            }
        }

        int n = grid.Count / 2;
        Array parameters = grid.ToArray();
        Array onFace = new bool[n];
        face.GetParamOnFace(n, ref parameters, ref onFace);
        Array normals = new double[3 * n];
        face.GetNormal(n, ref parameters, ref normals);
        double[] values = [.. Geo.Doubles(normals)];
        int[] inside = [.. onFace.Cast<object>().Select(Convert.ToInt32)];
        var drafts = Enumerable.Range(0, n).Where(k => inside[k] != 0)
            .Select(k => DraftOf(Rotated(placement, [values[3 * k], values[3 * k + 1], values[3 * k + 2]]), pull)).ToList();
        if (drafts.Count == 0)
        {
            double centre = DraftOf(Rotated(placement, FaceNormal(face)), pull);
            return (centre, centre);
        }

        return (drafts.Min(), drafts.Max());
    }

    private static double DraftOf(double[] normal, double[] pull)
    {
        double sine = Math.Clamp(normal.Zip(pull, (x, y) => x * y).Sum() / Geo.Length(normal), -1, 1);
        return Units.RadToDeg(Math.Asin(sine));
    }
}
