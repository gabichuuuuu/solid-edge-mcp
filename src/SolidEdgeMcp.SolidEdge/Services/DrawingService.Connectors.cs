// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Connector = SolidEdgeFrameworkSupport.Connector;
using Connectors = SolidEdgeFrameworkSupport.Connectors;
using ConnectorTypeConstants = SolidEdgeFrameworkSupport.ConnectorTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Connectors of a sheet (Connector command of the Annotation group): a line, jump, corner, step or gap line between two
/// points, free on the sheet or tied to two 2D elements. Verified in Solid Edge 2026: AddConnectorByType keeps the given
/// points and the key point index of each element; an end tied to the start key point of a line follows it when the line
/// moves. Each end is therefore tied at the key point of the element nearest the given point, with that key point's
/// coordinates. A connector is deleted with Delete.
/// </summary>
public static partial class DrawingService
{
    public static readonly IReadOnlyList<string> ConnectorTypes = ["line", "jump", "corner", "step", "gap"];

    /// <summary>Ends of a connector farther than this from the asked point (m) mean it was not placed as asked.</summary>
    private const double ConnectorTolerance = 1e-6;

    public static object AddConnector(SeContext context, string? documentHandle, string? sheetRef, string? type, double[]? points, string[]? targets)
    {
        if (points is not { Length: 4 })
        {
            throw ToolException.InvalidArgument("connector requires 'points': [x1, y1, x2, y2] in mm on the sheet, its start and end (with 'targets', near the points of the elements to tie it to).");
        }

        double[] at = [.. points.Select(v => Units.MmToM(Units.RequireFinite(v, "points")))];
        ConnectorTypeConstants kind = type switch
        {
            null or "line" => ConnectorTypeConstants.seLineConnector,
            "jump" => ConnectorTypeConstants.seJumpConnector,
            "corner" => ConnectorTypeConstants.seCornerConnector,
            "step" => ConnectorTypeConstants.seStepConnector,
            "gap" => ConnectorTypeConstants.seGapConnector,
            _ => throw ToolException.InvalidArgument($"Unknown connector type '{type}'.", $"Use one of: {string.Join(", ", ConnectorTypes)}."),
        };

        DraftDocument draft;
        Sheet sheet;
        Func<Connectors, Connector> add;
        (object Element, int Key, string Point)[] ties = [];
        if (targets is { Length: > 0 })
        {
            if (targets.Length != 2)
            {
                throw ToolException.InvalidArgument("'targets' takes two 2D elements of the sheet: the one the connector starts at and the one it ends at.");
            }

            object first = context.Handles.Resolve(targets[0], "targets");
            object second = context.Handles.Resolve(targets[1], "targets");
            sheet = Late.TryGet(first, "Parent") is Sheet parent && Late.TryGet(second, "Parent") is Sheet other && parent.Name == other.Name
                ? parent
                : throw ToolException.InvalidArgument("'targets' must be two 2D elements drawn on the same sheet (se_sketch with the sheet handle).");
            draft = DraftFor(context, documentHandle, targets[0]);
            ties = [NearestKeyPoint(first, at[0], at[1], targets[0]), NearestKeyPoint(second, at[2], at[3], targets[1])];
            (double x1, double y1) = KeyPointAt(ties[0].Element, ties[0].Key);
            (double x2, double y2) = KeyPointAt(ties[1].Element, ties[1].Key);
            at = [x1, y1, x2, y2];
            add = c => c.AddConnectorByType(first, x1, y1, 0, ties[0].Key, second, x2, y2, 0, ties[1].Key, kind);
        }
        else
        {
            draft = DraftFor(context, documentHandle, sheetRef);
            sheet = ResolveSheet(context, draft, sheetRef);
            add = c => c.AddFreeSpaceConnectorByType(at[0], at[1], 0, at[2], at[3], 0, kind);
        }

        var connectors = (Connectors)sheet.Connectors;
        int before = connectors.Count;
        Connector connector;
        try
        {
            connector = add(connectors);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("connector_failed", $"Solid Edge did not add the connector: {ex.Message.Trim()}",
                "Give two points apart; with 'targets', two 2D elements of the sheet.", ex);
        }

        connector.GetConnectorType(out ConnectorTypeConstants made);
        connector.GetStartPoint(out double sx, out double sy, out _);
        connector.GetEndPoint(out double ex2, out double ey, out _);
        bool tied = ties.Length == 0 || Tied(connector, start: true) is not null && Tied(connector, start: false) is not null;
        if (connectors.Count != before + 1 || made != kind || !tied
            || new[] { sx - at[0], sy - at[1], ex2 - at[2], ey - at[3] }.Any(d => Math.Abs(d) > ConnectorTolerance))
        {
            connector.Delete();
            throw new ToolException("connector_failed", "Solid Edge did not place the connector as asked; it was removed.",
                "Check the points and the elements in 'targets'.");
        }

        object result = DescribeConnector(context, draft, connector);
        return ties.Length == 0 ? new { connector = result } : new { connector = result, tiedAt = ties.Select(t => t.Point).ToArray() };
    }

    private static object DescribeConnector(SeContext context, DraftDocument draft, Connector connector)
    {
        connector.GetConnectorType(out ConnectorTypeConstants type);
        connector.GetStartPoint(out double x1, out double y1, out _);
        connector.GetEndPoint(out double x2, out double y2, out _);
        string handle = context.Register(connector, "connector", Document(draft));
        return new
        {
            handle,
            type = type switch
            {
                ConnectorTypeConstants.seLineConnector => "line",
                ConnectorTypeConstants.seJumpConnector => "jump",
                ConnectorTypeConstants.seCornerConnector => "corner",
                ConnectorTypeConstants.seStepConnector => "step",
                ConnectorTypeConstants.seGapConnector => "gap",
                _ => type.ToString(),
            },
            start = new[] { Geo.Mm(x1), Geo.Mm(y1) },
            end = new[] { Geo.Mm(x2), Geo.Mm(y2) },
            startElement = Tied(connector, start: true) is { } s ? context.RegisterBeside(s, SketchService.ElementKind(s), handle) : null,
            endElement = Tied(connector, start: false) is { } e ? context.RegisterBeside(e, SketchService.ElementKind(e), handle) : null,
        };
    }

    private static object? Tied(Connector connector, bool start)
    {
        object element;
        if (start)
        {
            connector.GetStartElement(out element, out _, out _, out _, out _);
        }
        else
        {
            connector.GetEndElement(out element, out _, out _, out _, out _);
        }

        return element;
    }

    /// <summary>The key point of a 2D element nearest (x, y) in metres: its index and its name (start, end, middle, center…).</summary>
    private static (object Element, int Key, string Point) NearestKeyPoint(object element, double x, double y, string handle)
    {
        int count = Late.TryGet(element, "KeyPointCount") is { } n ? Convert.ToInt32(n) : 0;
        if (count == 0)
        {
            throw ToolException.InvalidArgument($"'{handle}' has no key points to tie a connector to.", "Pass lines, arcs, circles or other 2D elements of the sheet.");
        }

        int best = 0;
        double nearest = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            (double kx, double ky) = KeyPointAt(element, i);
            double distance = Math.Sqrt((kx - x) * (kx - x) + (ky - y) * (ky - y));
            if (distance < nearest)
            {
                (best, nearest) = (i, distance);
            }
        }

        object?[] key = Late.CallByRef(element, "GetKeyPoint", [best, 0.0, 0.0, 0.0, 0, 0], 1, 2, 3, 4, 5);
        string name = ((SolidEdgeFramework.KeyPointType)Convert.ToInt32(key[4])).ToString().Replace("igKeyPoint", "", StringComparison.Ordinal);
        return (element, best, $"{handle}: {JsonNamingPolicy.SnakeCaseLower.ConvertName(name)}");
    }

    private static (double X, double Y) KeyPointAt(object element, int index)
    {
        object?[] key = Late.CallByRef(element, "GetKeyPoint", [index, 0.0, 0.0, 0.0, 0, 0], 1, 2, 3, 4, 5);
        return (Convert.ToDouble(key[1]), Convert.ToDouble(key[2]));
    }
}
