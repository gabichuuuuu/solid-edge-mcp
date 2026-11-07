// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Body = SolidEdgeGeometry.Body;
using Circle = SolidEdgeGeometry.Circle;
using Edge = SolidEdgeGeometry.Edge;
using Line = SolidEdgeGeometry.Line;
using RefPlane = SolidEdgePart.RefPlane;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_pmi_annotation does. Model points and positions in mm.</summary>
public sealed record PmiAnnotationSpec(string Action, string? Plane = null, double[]? Points = null, double[]? Position = null, string? Text = null,
    string Finish = "machined", string? Characteristic = null, string? Tolerance = null, string[]? Datums = null, string[]? Rows = null,
    bool Composite = false, string Weld = "fillet", string WeldSide = "arrow", bool AllAround = false, bool Field = false,
    string? Annotation = null, string? Kind = null);

/// <summary>
/// PMI (3D) annotations of a part or sheet metal document, attached to the edges of its bodies. Verified in Solid Edge 2026 on
/// blocks of 100 x 60 x 20 mm with holes:
/// <list type="bullet">
/// <item>The Add methods of the PMI collections take the collection's AnnotInitData: ClearCreationData, SetPlane (a reference
/// plane), then SetTerminatorElementEx(edge, keypoint, x, y, z) for the annotations on a leader, or SetFirstElement,
/// SetSecondElement and SetThirdElement for centre marks, centre lines, bolt hole circles and datum points. Every collection
/// placed its annotation that way (balloons, leaders, feature control frames, datum frames, datum targets, datum points,
/// surface finish symbols, weld and GOST weld symbols, corner annotations, centre marks, centre lines between two straight
/// edges or two circles, bolt hole circles through three holes), attached to the edge.</item>
/// <item>The annotation is drawn in the plane parallel to the given one through the terminator. Leader vertices (AddVertex,
/// and the end of AddLeader) are 2D offsets from the terminator in that plane, in metres: x along the plane's reference
/// direction, y along normal × x (checked on captures of the front and top planes). A balloon starts with one vertex 3 mm
/// along x and 10 mm along y from the terminator, so that vertex is moved instead of adding a bend.</item>
/// <item>TextBoxes.AddPMITextBox(plane, x, y, z) takes model coordinates.</item>
/// <item>GetTerminator reads (0, 0, 0) and Range is 2D, so the result reports the terminator and the position from the
/// inputs; the leader's last vertex is read back.</item>
/// <item>Moving: TransformToModelCoordinates(u, v, 0) gives the model point of 2D offsets in the annotation's plane, (0, 0)
/// being the terminator. SetVertex moves the leader end of balloons, feature control frames, weld and surface finish
/// symbols; a leader keeps its vertex for SetVertex and SetVertices and moves by Move(u0, v0, u, v). A datum frame's leader
/// stays square to its edge with the length asked; a datum frame placed without a leader has no vertex. A text box keeps
/// its origin in 2D coordinates of its plane from the plane's root (GetOrigin, SetOrigin) and does not give its plane.</item>
/// </list>
/// </summary>
public static class PmiAnnotationService
{
    public static readonly IReadOnlyList<string> Actions = ["list", "text", "balloon", "callout", "leader", "datum", "datum_target", "datum_point",
        "surface_finish", "feature_control_frame", "weld", "gost_weld", "corner", "center_mark", "centerline", "bolt_circle", "move", "delete"];

    public static readonly IReadOnlyList<string> Kinds = ["text", "balloon", "leader", "datum", "datum_target", "datum_point", "surface_finish",
        "feature_control_frame", "weld", "gost_weld", "corner", "center_mark", "centerline", "bolt_circle"];

    /// <summary>Metres (0.01 mm): how far a model point may be from the edge it picks.</summary>
    internal const double PickTolerance = 1e-5;

    /// <summary>Metres: how far a leader end read back may be from the one set.</summary>
    private const double VertexTolerance = 1e-7;

    public static object Run(SeContext context, string? documentHandle, PmiAnnotationSpec spec) => spec.Action switch
    {
        "list" => List(context, documentHandle, spec.Kind),
        "delete" => Delete(context, spec.Annotation),
        "move" => Move(context, documentHandle, spec),
        "text" => AddText(context, documentHandle, spec),
        "center_mark" or "centerline" or "bolt_circle" or "datum_point" => AddOnElements(context, documentHandle, spec),
        _ when Actions.Contains(spec.Action) => AddOnLeader(context, documentHandle, spec),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", Actions)}."),
    };

    private static object List(SeContext context, string? documentHandle, string? kind)
    {
        if (kind is not null && !Kinds.Contains(kind))
        {
            throw ToolException.InvalidArgument($"Unknown annotation kind '{kind}'.", $"Use one of: {string.Join(", ", Kinds)}.");
        }

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiService.PmiOf(model);
        var rows = new List<object>();
        foreach (string name in Kinds.Where(k => kind is null || k == kind))
        {
            if (CollectionOf(pmi, name) is { } collection)
            {
                rows.AddRange(Late.Items(collection).Select(item => Describe(context, model, item, null)));
            }
        }

        return new { document = context.DocumentHandle(model.Document), count = rows.Count, annotations = rows };
    }

    private static object Delete(SeContext context, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw ToolException.InvalidArgument("'annotation' is required: a handle from list or from the action that created it.");
        }

        object annotation = context.Handles.Resolve(handle, "annotation");
        string kind = DrawingService.KindOf(annotation) ?? throw ToolException.InvalidArgument($"'{handle}' is not a PMI annotation.");
        Late.Call(annotation, "Delete");
        context.Handles.Remove(handle, "the annotation was deleted");
        return new { deleted = handle, kind };
    }

    /// <summary>Moves an annotation (Move, PMI tab): the end of its leader, or a text, to the position projected onto its plane.</summary>
    private static object Move(SeContext context, string? documentHandle, PmiAnnotationSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Annotation))
        {
            throw ToolException.InvalidArgument("'annotation' is required: a handle from list or from the action that created it.");
        }

        object annotation = context.Handles.Resolve(spec.Annotation, "annotation");
        string kind = DrawingService.KindOf(annotation) ?? throw ToolException.InvalidArgument($"'{spec.Annotation}' is not a PMI annotation.");
        double[] target = ModelPoints(spec.Position, "position", 1, "where the annotation goes")[0];
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Annotation);
        string what = Name(kind);
        if (kind is "center_mark" or "centerline" or "bolt_circle" or "datum_point")
        {
            throw ToolException.InvalidArgument($"A {what} stays on the edges it marks: it cannot be moved.", "Delete it and place it on other edges.");
        }

        if (annotation is TextBox text)
        {
            Frame frame = FrameOf(context, model, PmiService.PmiOf(model), spec.Plane);
            double[] relative = Minus(target, RefPlaneService.PlaneData(frame.Plane).Root);
            (double tx, double ty) = (Dot(relative, frame.X), Dot(relative, frame.Y));
            text.GetOrigin(out double x0, out double y0, out double z0);
            text.SetOrigin(tx, ty, z0);
            text.GetOrigin(out double x1, out double y1, out _);
            if (Math.Abs(x1 - tx) > VertexTolerance || Math.Abs(y1 - ty) > VertexTolerance)
            {
                text.SetOrigin(x0, y0, z0);
                throw new ToolException("annotation_failed", "Solid Edge did not move the text; it was put back.", "Give another 'position'.");
            }

            return new { annotation = Describe(context, model, text, new Placed(frame.Name, Position(frame, RefPlaneService.PlaneData(frame.Plane).Root, (tx, ty)), null)) };
        }

        int count = VertexCount(annotation);
        if (count == 0)
        {
            throw new ToolException("not_supported", $"The {what} has no leader: it stays on its edge.", "Delete it and place it again with 'position' to give it a leader.");
        }

        double[] terminator = ToModel(annotation, 0, 0);
        double[] x = Unit(Minus(ToModel(annotation, 1e-3, 0), terminator));
        double[] y = Unit(Minus(ToModel(annotation, 0, 1e-3), terminator));
        double[] offset = Minus(target, terminator);
        (double U, double V) end = (Dot(offset, x), Dot(offset, y));
        if (Math.Sqrt((end.U * end.U) + (end.V * end.V)) < PickTolerance)
        {
            throw ToolException.InvalidArgument("'position' is on the annotation's edge point (once projected onto its plane): the leader would have no length.",
                "Give a position away from the edge, in the plane.");
        }

        (double U, double V) start = Vertex(annotation, count);
        try
        {
            Late.Call(annotation, "SetVertex", count, end.U, end.V, 0.0);
            if (Same(Vertex(annotation, count), start) && !Same(end, start))
            {
                // A leader keeps its vertex for SetVertex and moves by the difference.
                Late.Call(annotation, "Move", start.U, start.V, end.U, end.V);
            }

            end = RequireEnd(annotation, end, what, lengthOnly: kind == "datum");
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
        {
            (double U, double V) now = Vertex(annotation, count);
            if (!Same(now, start))
            {
                Late.Call(annotation, "SetVertex", count, start.U, start.V, 0.0);
                if (Same(Vertex(annotation, count), now))
                {
                    Late.Call(annotation, "Move", now.U, now.V, start.U, start.V);
                }
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("annotation_failed", $"Solid Edge could not move the {what}: {ComErrors.Unwrap(ex).Message.Trim()}", "Give another 'position'.", ex);
        }

        return new { annotation = Describe(context, model, annotation, new Placed(null, terminator, ToModel(annotation, end.U, end.V))) };
    }

    /// <summary>The model point (m) of 2D offsets (m) in the annotation's plane from its terminator.</summary>
    private static double[] ToModel(object annotation, double u, double v)
    {
        object?[] values = Late.CallByRef(annotation, "TransformToModelCoordinates", [u, v, 0.0, 0.0, 0.0, 0.0], 3, 4, 5);
        return [.. values.Skip(3).Take(3).Select(c => Convert.ToDouble(c, CultureInfo.InvariantCulture))];
    }

    private static (double U, double V) Vertex(object annotation, int index)
    {
        object?[] values = Late.CallByRef(annotation, "GetVertex", [index, 0.0, 0.0, 0.0], 1, 2, 3);
        return (Convert.ToDouble(values[1], CultureInfo.InvariantCulture), Convert.ToDouble(values[2], CultureInfo.InvariantCulture));
    }

    private static bool Same((double U, double V) a, (double U, double V) b) =>
        Math.Abs(a.U - b.U) <= VertexTolerance && Math.Abs(a.V - b.V) <= VertexTolerance;

    private static object AddText(SeContext context, string? documentHandle, PmiAnnotationSpec spec)
    {
        // As in drawings, a text box keeps its lines only when they are separated by '\r'.
        string content = DrawingService.RequireText(spec.Text, "text").Replace("\r\n", "\r").Replace('\n', '\r');
        double[] at = ModelPoints(spec.Position, "position", 1, "where the text goes")[0];
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiService.PmiOf(model);
        Frame frame = FrameOf(context, model, pmi, spec.Plane);
        var boxes = (TextBoxes)pmi.TextBoxes;
        int before = boxes.Count;
        object box = Create(boxes, before, "text", () => boxes.AddPMITextBox(frame.Plane, at[0], at[1], at[2]));
        return Checked(context, model, boxes, before, box, "text", () =>
        {
            var text = (TextBox)box;
            text.Text = content;
            if (text.Text != content)
            {
                throw new ToolException("annotation_failed", "Solid Edge did not keep the text.", "Avoid control characters in 'text'.");
            }
        }, () => new Placed(frame.Name, at, null));
    }

    /// <summary>
    /// Annotations on a leader from an edge: the terminator is the picked point (a vertex when it is an end of the edge), the
    /// leader ends at the position projected onto the plane.
    /// </summary>
    private static object AddOnLeader(SeContext context, string? documentHandle, PmiAnnotationSpec spec)
    {
        string kind = spec.Action == "callout" ? "balloon" : spec.Action;
        Action<object> content = Content(spec, kind);
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiService.PmiOf(model);
        Frame frame = FrameOf(context, model, pmi, spec.Plane);
        EdgePick pick = PickEdge(model, ModelPoints(spec.Points, "points", 1, "on the edge or vertex the annotation points at")[0], "edge", _ => true);
        bool leader = spec.Position is not null || kind is "balloon" or "leader" or "feature_control_frame" or "weld" or "gost_weld";
        (double U, double V) end = Offsets(frame, pick.Point, spec.Position);
        object collection = CollectionOf(pmi, kind)!;
        int before = Late.Count(collection);
        AnnotInitData data = Init(collection, frame);
        data.SetTerminatorElementEx(pick.Edge, pick.KeyPoint, pick.Point[0], pick.Point[1], pick.Point[2]);
        object annotation = Create(collection, before, kind, () => kind switch
        {
            "balloon" => ((Balloons)collection).AddBalloon(data),
            "leader" => ((Leaders)collection).AddLeader(data, end.U, end.V, 0),
            "datum" => ((DatumFrames)collection).AddDatumFrame(data),
            "datum_target" => ((DatumTargets)collection).AddDatumTarget(data),
            "surface_finish" => ((SurfaceFinishSymbols)collection).AddSurfaceFinishSymbol(data),
            "feature_control_frame" => ((FeatureControlFrames)collection).AddFeatureControlFrame(data),
            "weld" => ((WeldSymbols)collection).AddWeldSymbol(data),
            "gost_weld" => ((GostWeldSymbols)collection).AddGostWeldSymbol(data),
            "corner" => ((CornerAnnotations)collection).AddCornerAnnotation(data),
            _ => throw new InvalidOperationException(kind),
        });
        string what = Name(kind);
        (double U, double V) drawn = end;
        return Checked(context, model, collection, before, annotation, kind, () =>
        {
            // Verified (as in drawings): a weld or GOST weld symbol takes its leader before its content.
            bool leaderFirst = kind is "weld" or "gost_weld";
            if (leader && leaderFirst)
            {
                PutOnLeader(annotation, end);
            }

            content(annotation);
            if (annotation is GostWeldSymbol gost)
            {
                DrawingService.SetShelf(gost, DrawingService.GostNotes(spec.Text), left: end.U < 0);
            }

            if (leader && !leaderFirst && kind != "leader")
            {
                PutOnLeader(annotation, end);
            }

            if (!Convert.ToBoolean(Late.Get(annotation, "IsTerminatorAttachedToEntity"), CultureInfo.InvariantCulture))
            {
                throw new ToolException("annotation_failed", $"The {what} is not attached to the edge.", "Give a model point on an edge of the body (se_geometry edges).");
            }

            if (leader)
            {
                drawn = RequireEnd(annotation, end, what, lengthOnly: kind == "datum");
            }
        }, () => new Placed(frame.Name, pick.Point, leader ? Position(frame, pick.Point, drawn) : null));
    }

    /// <summary>The content of an annotation on a leader, checked on the inputs before anything is placed.</summary>
    private static Action<object> Content(PmiAnnotationSpec spec, string kind)
    {
        switch (kind)
        {
            case "balloon":
                string label = DrawingService.RequireText(spec.Text, "text");
                bool callout = spec.Action == "callout";
                return a =>
                {
                    var balloon = (Balloon)a;
                    if (callout)
                    {
                        balloon.Callout = 1;
                    }

                    balloon.BalloonText = label;
                    if (balloon.BalloonText != label)
                    {
                        throw new ToolException("annotation_failed", "Solid Edge did not keep the text of the balloon.", "Avoid control characters in 'text'.");
                    }
                };
            case "datum":
                string letter = DrawingService.RequireText(spec.Text, "text");
                return a => DrawingService.SetDatum((DatumFrame)a, letter);
            case "datum_target":
                (string Letter, string Number) target = DrawingService.DatumTargetLabel(spec.Text);
                return a => DrawingService.SetLabel((DatumTarget)a, target);
            case "surface_finish":
                DimSurfTextureSymTypeConstants finish = DrawingService.ParseFinish(spec.Finish);
                return a => DrawingService.SetFinish((SurfaceFinishSymbol)a, finish, spec.Text);
            case "feature_control_frame":
                string frame = string.IsNullOrEmpty(spec.Text) ? DrawingService.FrameText(spec.Characteristic, spec.Tolerance, spec.Datums) : spec.Text;
                string[] rows = DrawingService.RowTexts(spec.Rows, spec.Composite);
                return a =>
                {
                    var control = (FeatureControlFrame)a;
                    DrawingService.SetFrame(control, frame);
                    SetRows(control, rows, spec.Composite);
                };
            case "weld":
                (DimWeldTypeConstants, bool, bool) weld = DrawingService.ParseWeld(spec.Weld, spec.WeldSide);
                return a => DrawingService.SetWeld((WeldSymbol)a, weld, spec.Text, spec.AllAround, spec.Field);
            case "gost_weld":
                _ = DrawingService.GostNotes(spec.Text);
                return a =>
                {
                    var symbol = (GostWeldSymbol)a;
                    symbol.WeldAllAround = spec.AllAround;
                    symbol.WeldInField = spec.Field;
                };
            case "corner":
                string[] deviations = DrawingService.CornerDeviations(spec.Text);
                return a => DrawingService.SetDeviations((CornerAnnotation)a, deviations);
            default:
                return _ => { };
        }
    }

    /// <summary>The lower rows of a feature control frame, checked by reading them back (a PMI frame has no sheet extent).</summary>
    private static void SetRows(FeatureControlFrame frame, string[] rows, bool composite)
    {
        if (rows.Length == 0)
        {
            return;
        }

        frame.CompositeFrame = composite;
        frame.SecondaryFrame = rows[0];
        if (rows.Length > 1)
        {
            frame.TertiaryFrame = rows[1];
        }

        if (rows.Length > 2)
        {
            frame.QuaternaryFrame = rows[2];
        }

        if (!DrawingService.RowsOf(frame).SequenceEqual(rows) || frame.CompositeFrame != composite)
        {
            throw new ToolException("annotation_failed", "Solid Edge did not keep the rows of the feature control frame.", "Check the codes in 'rows'.");
        }
    }

    /// <summary>Centre marks, centre lines, bolt hole circles and datum points: placed on the picked edges themselves.</summary>
    private static object AddOnElements(SeContext context, string? documentHandle, PmiAnnotationSpec spec)
    {
        string kind = spec.Action;
        (int count, string expected) = kind switch
        {
            "center_mark" => (1, "on the circular edge"),
            "centerline" => (2, "on two straight edges (their mid line) or two circular edges (centre to centre)"),
            "bolt_circle" => (3, "on the circular edges of three holes"),
            _ => (1, "a vertex or a point on an edge"),
        };
        List<double[]> points = ModelPoints(spec.Points, "points", count, expected);
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiService.PmiOf(model);
        Frame frame = FrameOf(context, model, pmi, spec.Plane);
        EdgePick[] picks = kind switch
        {
            "center_mark" or "bolt_circle" => [.. points.Select(p => PickEdge(model, p, "circular edge", IsCircle))],
            "centerline" => [.. points.Select(p => PickEdge(model, p, "straight or circular edge", e => IsLine(e) || IsCircle(e)))],
            _ => [PickEdge(model, points[0], "edge", _ => true)],
        };
        if (picks.Select(p => p.Edge).Distinct(SameEdge.Instance).Count() != picks.Length)
        {
            throw ToolException.InvalidArgument("Two points pick the same edge.", $"Give one point {expected}.");
        }

        bool circles = picks.All(p => IsCircle(p.Edge));
        if (kind == "centerline" && !circles && !picks.All(p => IsLine(p.Edge)))
        {
            throw ToolException.InvalidArgument("A centre line joins two straight edges or two circular edges, not one of each.");
        }

        double[] at;
        double? radius = null;
        if (kind == "bolt_circle")
        {
            (at, double r) = Circumcircle([.. picks.Select(p => CircleOf(p.Edge).Centre)]);
            radius = r;
        }
        else
        {
            at = kind switch
            {
                "center_mark" => CircleOf(picks[0].Edge).Centre,
                "centerline" when circles => Mid(CircleOf(picks[0].Edge).Centre, CircleOf(picks[1].Edge).Centre),
                "centerline" => Mid(Mid(Ends(picks[0].Edge)), Mid(Ends(picks[1].Edge))),
                _ => picks[0].Point,
            };
        }

        object collection = CollectionOf(pmi, kind)!;
        int before = Late.Count(collection);
        AnnotInitData data = Init(collection, frame);
        for (int i = 0; i < picks.Length; i++)
        {
            (Edge edge, double[] p, bool keyPoint) = picks[i];
            Action<object, bool, double, double, double> set = i switch
            {
                0 => data.SetFirstElement,
                1 => data.SetSecondElement,
                _ => data.SetThirdElement,
            };
            set(edge, keyPoint, p[0], p[1], p[2]);
        }

        object annotation = Create(collection, before, kind, () => kind switch
        {
            "center_mark" => ((CenterMarks)collection).AddCenterMark(data),
            // bIsBy2Lines: 1 = between two straight edges, 0 = between two circles.
            "centerline" => ((CenterLines)collection).AddCenterLine(data, circles ? 0 : 1),
            // bIsByCenterRadius 0: through the centres of the three holes.
            "bolt_circle" => ((BoltHoleCircles)collection).AddBoltHoleCircle(data, 0),
            _ => ((DatumPoints)collection).AddDatumPoint(data),
        });
        return Checked(context, model, collection, before, annotation, kind, () =>
        {
            if (annotation is BoltHoleCircle circle)
            {
                circle.Circle2d(out _, out _, out double drawn);
                if (Math.Abs(drawn - radius!.Value) > VertexTolerance)
                {
                    throw new ToolException("annotation_failed", $"The bolt hole circle has a radius of {Geo.Mm(drawn)} mm, not {Geo.Mm(radius.Value)} mm through the three hole centres.",
                        "Pick three holes of the same circle.");
                }
            }
        }, () => new Placed(frame.Name, at, null, radius));
    }

    /// <summary>
    /// The plane an annotation is drawn in, with its axes: X its reference direction, Y = normal × X (the 2D axes of
    /// leader vertices).
    /// </summary>
    internal sealed record Frame(RefPlane Plane, double[] X, double[] Y, string Name);

    internal static Frame FrameOf(SeContext context, ModelDocument model, PMI pmi, string? plane)
    {
        object source = plane is null
            ? Late.TryGet(pmi, "ActivePlane") ?? throw ToolException.InvalidArgument(
                "'plane' is required: '1'-'3' or a reference plane handle, the plane the PMI is drawn in.", "Or lock a PMI plane first (se_pmi plane).")
            : model.ResolvePlaneSource(context, plane);
        var refPlane = source as RefPlane ?? throw ToolException.InvalidArgument($"'{plane}' is not a reference plane.",
            "PMI is drawn in reference planes: pass '1'-'3' or a plane handle (se_ref_plane list).");
        double[] normal = RefPlaneService.PlaneData(refPlane).Normal;
        double[] x = RefPlaneService.XAxis(refPlane) is { } axis && Geo.Length(axis) > 0
            ? Unit(axis)
            : throw new ToolException("plane_failed", "Solid Edge gives no reference direction for the plane.", "Use another reference plane.");
        return new Frame(refPlane, x, Unit(Geo.Cross(normal, x)), plane ?? "active");
    }

    /// <summary>An edge picked by a model point (m): the point, moved onto the vertex when it is at an end of the edge.</summary>
    internal sealed record EdgePick(Edge Edge, double[] Point, bool KeyPoint);

    /// <summary>The edge of the model's bodies through the point (within 0.01 mm) among those accepted.</summary>
    internal static EdgePick PickEdge(ModelDocument model, double[] point, string what, Func<Edge, bool> accept)
    {
        Edge? best = null;
        double nearest = double.MaxValue;
        foreach ((_, Body body) in model.Bodies())
        {
            foreach (Edge edge in Late.Items(body.Edges[TopologyQuery.igQueryAll]).Cast<Edge>())
            {
                if (!accept(edge) || BoxDistance(edge, point) >= nearest)
                {
                    continue;
                }

                double distance = FeatureService.DistanceToEdge(edge, point);
                if (distance < nearest)
                {
                    (best, nearest) = (edge, distance);
                }
            }
        }

        if (best is null || nearest > PickTolerance)
        {
            throw ToolException.NotFound(best is null
                    ? $"The model has no {what}."
                    : $"No {what} passes through ({string.Join(", ", point.Select(Geo.Mm))}) mm; the nearest is {Geo.Mm(nearest)} mm away.",
                "Give a model point on the edge (se_geometry edges lists the edges and their ends).");
        }

        double[]? end = EndAt(best, point);
        return new EdgePick(best, end ?? point, end is not null);
    }

    /// <summary>
    /// Every edge with an end at the vertex (m), as picks of that vertex: straight edges first, then those that start there,
    /// then the rest (Solid Edge may take another key point of a B-spline edge for a vertex).
    /// </summary>
    internal static List<EdgePick> VertexEdges(ModelDocument model, double[] point)
    {
        var found = new List<(EdgePick Pick, int Rank)>();
        foreach ((_, Body body) in model.Bodies())
        {
            foreach (Edge edge in Late.Items(body.Edges[TopologyQuery.igQueryAll]).Cast<Edge>())
            {
                if (BoxDistance(edge, point) > PickTolerance || EndAt(edge, point) is not { } end)
                {
                    continue;
                }

                int rank = IsLine(edge) ? 0 : Geo.Distance(Ends(edge).Start, point) <= PickTolerance ? 1 : 2;
                found.Add((new EdgePick(edge, end, true), rank));
            }
        }

        return [.. found.OrderBy(f => f.Rank).Select(f => f.Pick)];
    }

    internal static bool IsLine(Edge edge) => edge.Geometry is Line;

    internal static bool IsCircle(Edge edge) => edge.Geometry is Circle;

    internal static (double[] Start, double[] End) Ends(Edge edge)
    {
        Array start = new double[3];
        Array end = new double[3];
        edge.GetEndPoints(ref start, ref end);
        return ([.. Geo.Doubles(start)], [.. Geo.Doubles(end)]);
    }

    internal static (double[] Centre, double Radius) CircleOf(Edge edge)
    {
        var circle = (Circle)edge.Geometry;
        Array centre = new double[3];
        Array axis = new double[3];
        circle.GetCircleData(ref centre, ref axis, out double radius);
        return ([.. Geo.Doubles(centre)], radius);
    }

    /// <summary>Flat list of model points in mm → points in metres, exactly <paramref name="count"/> of them.</summary>
    internal static List<double[]> ModelPoints(double[]? values, string name, int count, string what)
    {
        List<(double X, double Y, double Z)> points = Geo.Points3(values, name, count);
        return points.Count != count
            ? throw ToolException.InvalidArgument($"'{name}' must be {count} model point(s) [x, y, z] in mm: {what}.")
            : [.. points.Select(p => new[] { p.X, p.Y, p.Z })];
    }

    /// <summary>The end of an open edge at the point (within the pick tolerance), or null.</summary>
    private static double[]? EndAt(Edge edge, double[] point)
    {
        (double[] start, double[] end) = Ends(edge);
        if (Geo.Distance(start, end) <= PickTolerance)
        {
            return null;
        }

        return Geo.Distance(start, point) <= PickTolerance ? start : Geo.Distance(end, point) <= PickTolerance ? end : null;
    }

    /// <summary>Distance (m) from the point to the edge's bounding box: no edge in that box is nearer.</summary>
    private static double BoxDistance(Edge edge, double[] point)
    {
        Array min = new double[3];
        Array max = new double[3];
        edge.GetRange(ref min, ref max);
        double[] low = [.. Geo.Doubles(min)];
        double[] high = [.. Geo.Doubles(max)];
        return Math.Sqrt(Enumerable.Range(0, 3).Sum(k =>
        {
            double outside = Math.Max(Math.Max(low[k] - point[k], point[k] - high[k]), 0);
            return outside * outside;
        }));
    }

    private static AnnotInitData Init(object collection, Frame frame)
    {
        var data = (AnnotInitData)Late.Get(collection, "AnnotInitData")!;
        data.ClearCreationData();
        data.SetPlane(frame.Plane);
        return data;
    }

    /// <summary>Runs the Add method; on an error, whatever it left in the collection is removed.</summary>
    private static object Create(object collection, int before, string kind, Func<object?> add)
    {
        try
        {
            return add() ?? throw new ToolException("annotation_failed", $"Solid Edge did not place the {Name(kind)}.", "Check the plane and the picked edges.");
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
        {
            foreach (object extra in Late.Items(collection).Skip(before).ToList())
            {
                TryDelete(extra);
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("annotation_failed", $"Solid Edge could not place the {Name(kind)}: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the plane and the picked edges.", ex);
        }
    }

    /// <summary>Where an annotation was placed (m): the plane, the picked point, the leader end and a bolt circle's radius.</summary>
    private sealed record Placed(string? Plane, double[] At, double[]? Position, double? Radius = null);

    /// <summary>Runs the checks of a new annotation and deletes it if they fail, so a failed call leaves nothing behind.</summary>
    private static object Checked(SeContext context, ModelDocument model, object collection, int before, object annotation, string kind, Action check, Func<Placed> placed)
    {
        try
        {
            if (Late.Count(collection) != before + 1)
            {
                throw new ToolException("annotation_failed", $"Solid Edge did not add the {Name(kind)} to the PMI.", "Check the plane and the picked edges.");
            }

            check();
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com)))
        {
            TryDelete(annotation);
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("annotation_failed", $"Solid Edge rejected the {Name(kind)}: {ComErrors.Unwrap(ex).Message.Trim()}",
                "Check the plane, the picked edges and the position.", ex);
        }

        return new { annotation = Describe(context, model, annotation, placed()) };
    }

    private static Dictionary<string, object?> Describe(SeContext context, ModelDocument model, object annotation, Placed? placed)
    {
        string kind = DrawingService.KindOf(annotation) ?? "annotation";
        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(annotation, kind, model.Document),
            ["kind"] = kind,
        };
        DrawingService.DescribeContent(info, annotation);
        switch (annotation)
        {
            case GostWeldSymbol gost:
                info["text"] = string.IsNullOrEmpty(gost.BelowBreakLineText) ? gost.AboveBreakLineText : gost.AboveBreakLineText + "|" + gost.BelowBreakLineText;
                info["attached"] = gost.IsTerminatorAttachedToEntity;
                break;
            case CornerAnnotation corner:
                string? lower = Late.TryGetString(corner, "LowerToleranceForEnhancedSymbol");
                info["text"] = Late.TryGetString(corner, "UpperToleranceForEnhancedSymbol") + (string.IsNullOrEmpty(lower) ? "" : "|" + lower);
                info["attached"] = corner.IsTerminatorAttachedToEntity;
                break;
        }

        if (placed is not null)
        {
            if (placed.Plane is not null)
            {
                info["plane"] = placed.Plane;
            }

            info["at"] = placed.At.Select(Geo.Mm).ToArray();
            if (placed.Position is not null)
            {
                info["position"] = placed.Position.Select(Geo.Mm).ToArray();
            }

            if (placed.Radius is { } radius)
            {
                info["radius"] = Geo.Mm(radius);
            }
        }

        return info;
    }

    private static object? CollectionOf(PMI pmi, string kind) => kind switch
    {
        "text" => pmi.TextBoxes,
        "balloon" => pmi.Balloons,
        "leader" => pmi.Leaders,
        "datum" => pmi.DatumFrames,
        "datum_target" => pmi.DatumTargets,
        "datum_point" => pmi.DatumPoints,
        "surface_finish" => pmi.SurfaceFinishSymbols,
        "feature_control_frame" => pmi.FeatureControlFrames,
        "weld" => pmi.WeldSymbols,
        "gost_weld" => pmi.GostWeldSymbols,
        "corner" => pmi.CornerAnnotations,
        "center_mark" => pmi.CenterMarks,
        "centerline" => pmi.CenterLines,
        "bolt_circle" => pmi.BoltHoleCircles,
        _ => null,
    };

    private static string Name(string kind) => kind.Replace('_', ' ');

    /// <summary>
    /// The leader of the annotation, ending at the offsets in the plane. A balloon already has a vertex: it is moved there
    /// instead of adding a second bend.
    /// </summary>
    private static void PutOnLeader(object annotation, (double U, double V) end)
    {
        Late.Set(annotation, "Leader", true);
        int count = VertexCount(annotation);
        if (count > 0)
        {
            Late.Call(annotation, "SetVertex", count, end.U, end.V, 0.0);
        }
        else
        {
            Late.Call(annotation, "AddVertex", end.U, end.V, 0.0);
        }
    }

    private static int VertexCount(object annotation) => Convert.ToInt32(Late.Get(annotation, "VertexCount"), CultureInfo.InvariantCulture);

    /// <summary>
    /// The leader's last vertex must be the offsets set; for a datum frame only its length, since Solid Edge draws a datum
    /// leader square to the edge (verified: asked 10 mm along x and -15 along y from a vertical edge, it ended 18.03 mm along x).
    /// Returns the end as drawn.
    /// </summary>
    private static (double U, double V) RequireEnd(object annotation, (double U, double V) end, string what, bool lengthOnly)
    {
        int count = VertexCount(annotation);
        object?[] values = count > 0 ? Late.CallByRef(annotation, "GetVertex", [count, 0.0, 0.0, 0.0], 1, 2, 3) : [];
        double u = count > 0 ? Convert.ToDouble(values[1], CultureInfo.InvariantCulture) : double.NaN;
        double v = count > 0 ? Convert.ToDouble(values[2], CultureInfo.InvariantCulture) : double.NaN;
        bool same = lengthOnly
            ? Math.Abs(Math.Sqrt((u * u) + (v * v)) - Math.Sqrt((end.U * end.U) + (end.V * end.V))) <= VertexTolerance
            : Math.Abs(u - end.U) <= VertexTolerance && Math.Abs(v - end.V) <= VertexTolerance;
        if (!same)
        {
            throw new ToolException("annotation_failed",
                $"The leader of the {what} ends {(count == 0 ? "nowhere" : $"at ({Geo.Mm(u)}, {Geo.Mm(v)}) mm")} from its edge in the plane instead of ({Geo.Mm(end.U)}, {Geo.Mm(end.V)}) mm.",
                "Give another 'position'.");
        }

        return (u, v);
    }

    /// <summary>The position (m) relative to the terminator in the plane: default 15 mm along x and y.</summary>
    private static (double U, double V) Offsets(Frame frame, double[] terminator, double[]? position)
    {
        if (position is null)
        {
            return (DrawingService.AnnotationOffset, DrawingService.AnnotationOffset);
        }

        double[] relative = Minus(ModelPoints(position, "position", 1, "the end of the leader")[0], terminator);
        (double u, double v) = (Dot(relative, frame.X), Dot(relative, frame.Y));
        return Math.Sqrt((u * u) + (v * v)) < PickTolerance
            ? throw ToolException.InvalidArgument("'position' is on the picked point (once projected onto the plane): the leader would have no length.",
                "Give a position away from the edge, in the plane.")
            : (u, v);
    }

    private static double[] Position(Frame frame, double[] terminator, (double U, double V) end) =>
        [.. terminator.Select((c, k) => c + (end.U * frame.X[k]) + (end.V * frame.Y[k]))];

    /// <summary>Centre and radius (m) of the circle through three points.</summary>
    private static (double[] Centre, double Radius) Circumcircle(double[][] p)
    {
        double[] a = Minus(p[0], p[2]);
        double[] b = Minus(p[1], p[2]);
        double[] axb = Geo.Cross(a, b);
        double area = Dot(axb, axb);
        if (area < 1e-18)
        {
            throw ToolException.InvalidArgument("The three hole centres are in a line: they do not make a circle.");
        }

        double[] offset = Geo.Cross(Minus(Scale(b, Dot(a, a)), Scale(a, Dot(b, b))), axb);
        double[] centre = [.. p[2].Select((c, k) => c + (offset[k] / (2 * area)))];
        return (centre, Geo.Distance(centre, p[0]));
    }

    private static void TryDelete(object annotation)
    {
        try
        {
            Late.Call(annotation, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) || ex is COMException)
        {
            // Already gone.
        }
    }

    private static double[] Mid((double[] Start, double[] End) ends) => Mid(ends.Start, ends.End);

    private static double[] Mid(double[] a, double[] b) => [.. a.Select((c, k) => (c + b[k]) / 2)];

    private static double[] Minus(double[] a, double[] b) => [.. a.Select((c, k) => c - b[k])];

    private static double[] Scale(double[] a, double factor) => [.. a.Select(c => c * factor)];

    private static double Dot(double[] a, double[] b) => a.Select((c, k) => c * b[k]).Sum();

    private static double[] Unit(double[] v)
    {
        double length = Geo.Length(v);
        return [.. v.Select(c => c / length)];
    }

    /// <summary>Edges are the same when Solid Edge says so (two wrappers can stand for one edge).</summary>
    private sealed class SameEdge : IEqualityComparer<Edge>
    {
        public static readonly SameEdge Instance = new();

        public bool Equals(Edge? x, Edge? y) => x is not null && y is not null && Late.SameObject(x, y);

        public int GetHashCode(Edge obj) => 0;
    }
}
