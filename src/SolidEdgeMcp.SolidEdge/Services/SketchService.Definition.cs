// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using ObjectType = SolidEdgeFramework.ObjectType;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class SketchService
{
    /// <summary>Key points that place an element: the rest (middles, silhouettes) follow from them.</summary>
    private static readonly HashSet<SolidEdgeFramework.KeyPointType> Placing =
    [
        SolidEdgeFramework.KeyPointType.igKeyPointStart, SolidEdgeFramework.KeyPointType.igKeyPointEnd, SolidEdgeFramework.KeyPointType.igKeyPointCenter, SolidEdgeFramework.KeyPointType.igKeyPointInteriorNode,
        SolidEdgeFramework.KeyPointType.igKeyPointInteriorPole, SolidEdgeFramework.KeyPointType.igKeyPointPointOnly,
    ];

    /// <summary>Relations, construction axes and driving dimensions that <see cref="Define"/> added to a sketch.</summary>
    internal sealed class Definition
    {
        public int Relations { get; set; }

        public int ConstructionLines { get; set; }

        public List<Dimension> Dimensions { get; } = [];

        /// <summary>What each added dimension sizes (Width, Diameter, X…), which its name says.</summary>
        public Dictionary<object, string> Roles { get; } = new(ReferenceEqualityComparer.Instance);

        public bool Empty => Relations == 0 && ConstructionLines == 0 && Dimensions.Count == 0;

        public object? Describe(SeContext context, string sketch) => Empty ? null : new
        {
            relations = Relations,
            constructionLines = ConstructionLines == 0 ? (int?)null : ConstructionLines,
            dimensions = Dimensions.Select(d => new
            {
                dimension = context.RegisterBeside(d, "dimension", sketch),
                variable = d.DisplayName,
                value = d.DimensionType == DimTypeConstants.igDimTypeAngular ? Geo.R(Units.RadToDeg(d.Value)) : Geo.Mm(d.Value),
            }).ToArray(),
        };
    }

    /// <summary>
    /// Whether Solid Edge sees the sketch as under-defined (Sketch.IsUnderDefined or ProfileSet.IsUnderDefined); null when
    /// the profile has neither.
    /// </summary>
    internal static bool? IsUnderDefined(Profile profile) => profile.Parent switch
    {
        Sketch sketch => sketch.IsUnderDefined,
        ProfileSet set => set.IsUnderDefined,
        _ => null,
    };

    /// <summary>
    /// The sketch of an operation, fully defined (every sketch an operation uses is): one closed by se_sketch already is; any
    /// other (drawn in Solid Edge, edited since) is defined here or refused.
    /// </summary>
    internal static Profile ResolveDefined(SeContext context, string sketch)
    {
        Profile profile = Resolve(context, sketch);
        Define(profile);
        return profile;
    }

    /// <summary>
    /// Makes the sketch fully defined where it lies, as a designer dimensions it (by position relations and dimensions,
    /// coherently, never forced): first the relations the drawing already shows (connected ends, horizontal and vertical lines,
    /// ends on other elements, tangent, concentric, perpendicular and parallel), then the sizes (line lengths, circle
    /// diameters, arc radii; an ellipse gets construction axes from its centre, whose lengths are its semi-axes), then the angle
    /// of each inclined line from the line it meets or from a base plane, and last the position (<see cref="Positions"/>: the
    /// largest contour from the base planes, the others from the contours already placed). No drawn element is fixed: only
    /// the base plane traces drawn where Solid Edge projects none (<see cref="BasePlaneLines"/>), which are references. Model
    /// edges brought into the sketch (<see cref="Linked"/>) are references too: their ends that meet are connected, and the
    /// contours are placed from them before the base planes, as a hole is placed from the edges of its face. The lines in
    /// <paramref name="from"/> are tried first of all. Each dimension takes the value it measures and is kept only if Solid
    /// Edge makes it driving, so none is redundant and nothing moves, and is named after what it sizes (<see cref="Name"/>);
    /// the pass stops as soon as Solid Edge no longer sees the sketch as under-defined. If it still is, everything added is
    /// deleted and <c>sketch_under_defined</c> is thrown. Assembly layouts, which no operation uses, are left as they are.
    /// </summary>
    /// <remarks>
    /// Verified in Solid Edge 2026: a driving dimension that would over-constrain the sketch stays driven when Constraint is
    /// set, without an error, and a redundant relation is refused (0x80040210); Relations2d.AddAutoConstraints adds relations but
    /// never dimensions; Profile.ProjectRefPlane gives a line for a base plane that crosses a sketch plane parallel or square
    /// to the base planes (null for a parallel one, and for every one on a turned plane), and a distance from it to a key
    /// point may be measured obliquely, so the measurement axis is reversed when the value is not the perpendicular distance;
    /// a zero distance is placed as a "point on" relation instead; an ellipse takes a radial dimension (its major semi-axis)
    /// but no minor or rotation one, while construction lines tied to its centre and axis key points take length and angle
    /// dimensions that drive it. A model edge brought in (Profile.IncludeEdge) is a line tied by an offset relation to a hidden
    /// copy of the edge: it follows the model, but its ends stay free along the edge, so Solid Edge sees the sketch as
    /// under-defined until they meet other ends (the four edges of a face connected at its corners are defined); there, and
    /// with Foundation after any relation, only Profile.End or a recompute refreshes IsUnderDefined. A length on it holds it when the model changes (a 40 mm edge
    /// driven to 60 kept a 40 mm line), and AddFix cuts it from the model.
    /// </remarks>
    internal static Definition Define(Profile profile, IReadOnlyList<Line2d>? from = null)
    {
        var definition = new Definition();
        if (IsUnderDefined(profile) != true || LayoutOf(profile) is not null)
        {
            return definition;
        }

        var relations = (Relations2d)profile.Relations2d;
        var dimensions = (Dimensions)profile.Dimensions;
        var added = new List<object>();
        var references = new List<Line2d>();
        List<object> linked = Linked(relations, [.. PlacedElements(profile)]);
        bool IsLinked(object element) => linked.Any(l => ReferenceEquals(l, element));
        bool Done()
        {
            // Verified with Solid Edge 2026 Foundation: IsUnderDefined can keep its value after the relations that define the
            // sketch until Profile.End (a rectangle with all its relations and sizes, a circle on both base planes).
            if (IsUnderDefined(profile) == true)
            {
                profile.End(ProfileValidationType.igProfileAllowNested);
            }

            return IsUnderDefined(profile) == false;
        }

        bool Keep(Dimension dimension, string role)
        {
            dimension.Constraint = true;
            if (dimension.Constraint)
            {
                added.Add(dimension);
                definition.Dimensions.Add(dimension);
                definition.Roles[dimension] = role;
                return true;
            }

            Late.Call(dimension, "Delete");
            return false;
        }

        try
        {
            var axes = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
            definition.Relations += EllipseAxes(profile, relations, axes, added, linked);
            definition.ConstructionLines += axes.Count;
            object[] elements = [.. PlacedElements(profile)];
            object[] drawn = [.. elements.Where(e => !axes.ContainsKey(e))];
            object[] free = [.. elements.Where(e => !IsLinked(e))];
            definition.Relations += ConnectEnds(relations, drawn, added);
            definition.Relations += AlignLines(relations, free, added);
            definition.Relations += GeometricRelations(relations, drawn, added, IsLinked);
            if (!Done())
            {
                foreach (object element in free)
                {
                    Dimension? size = Try(() => element switch
                    {
                        Line2d line => dimensions.AddLength(line),
                        Circle2d circle => dimensions.AddCircularDiameter(circle),
                        Arc2d arc => dimensions.AddRadius(arc),
                        _ => null,
                    });
                    if (size is not null && Keep(size, axes.TryGetValue(element, out string? axis) ? axis : SizeRole(element)) && Done())
                    {
                        break;
                    }
                }
            }

            if (!Done())
            {
                references.AddRange(BasePlaneLines(profile, relations));
                bool[] used = new bool[references.Count];
                List<Line2d> preferred = [.. from ?? []];
                bool done = Angles(free, drawn, references, used, preferred, dimensions, Keep, Done);
                if (!done)
                {
                    // Placed from outlines only, not from construction lines (an ellipse's axes, kept from an earlier pass).
                    object[] outlines = [.. drawn.Where(e => !profile.IsConstructionElement(e) && !IsLinked(e))];
                    Positions(free, outlines, references, used, [.. linked.OfType<Line2d>()], preferred, dimensions, relations, Keep, Done, added, definition);
                    if (!Done())
                    {
                        PlaceBlocks(profile, references, used, dimensions, relations, Keep, Done, added, definition);
                    }
                }

                for (int i = references.Count - 1; i >= 0; i--)
                {
                    if (!used[i])
                    {
                        Late.Call(references[i], "Delete");
                        references.RemoveAt(i);
                    }
                }
            }

            if (!Done())
            {
                throw new ToolException("sketch_under_defined",
                    $"Solid Edge still sees the sketch '{NameOf(profile)}' as under-defined after relating and dimensioning its elements where they lie.",
                    linked.Count > 0
                        ? "The model edges brought in with 'project' keep their ends free unless they meet: project every edge of the outline (of a face, for example), so their ends meet at its corners, or relate those ends with se_sketch_constraint; then call 'close' again."
                        : "Relate or dimension the free elements with se_sketch_constraint (to each other or to model edges brought in with se_sketch 'project'), then call 'close' again.");
            }

            Name(profile, definition);
            return definition;
        }
        catch (ToolException)
        {
            Undo(added, references);
            throw;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            Undo(added, references);
            throw new ToolException("sketch_under_defined",
                $"Solid Edge failed while defining the sketch '{NameOf(profile)}': {ComErrors.Unwrap(ex).Message.Trim()}",
                "Relate and dimension the sketch with se_sketch_constraint, then call 'close' again.", ex);
        }
    }

    /// <summary>
    /// The angle of each inclined line, from a line in <paramref name="preferred"/>, a drawn line it meets (corner by corner, as
    /// a chain is dimensioned) or else a base plane line; a line whose angle is already set by its relations and sizes gets
    /// none. True when the sketch is done.
    /// </summary>
    private static bool Angles(object[] elements, object[] drawn, List<Line2d> references, bool[] used, List<Line2d> preferred,
        Dimensions dimensions, Func<Dimension, string, bool> keep, Func<bool> done)
    {
        Line2d[] lines = [.. drawn.OfType<Line2d>()];
        foreach (Line2d line in elements.OfType<Line2d>())
        {
            if (AxisAligned(line))
            {
                continue;
            }

            bool isDrawn = lines.Any(l => ReferenceEquals(l, line));
            IEnumerable<(Line2d Other, int Reference)> neighbours = isDrawn
                ? lines.Where(l => !ReferenceEquals(l, line) && Meet(l, line) && !Parallel(l, line)).Select(l => (l, -1))
                : [];
            // The base plane line nearest in direction first: 30° from the horizontal rather than 60° from the vertical.
            (double X, double Y) along = Direction(line, Ends(line).Start);
            IEnumerable<(Line2d Other, int Reference)> candidates = preferred.Where(p => !ReferenceEquals(p, line) && !Parallel(p, line))
                .Select(p => (p, -1)).Concat(neighbours).Concat(references.Select((r, i) => (r, i))
                .Where(r => !Parallel(r.r, line)).OrderByDescending(r => Dot(Direction(r.r, Ends(r.r).Start), along)));
            foreach ((Line2d other, int reference) in candidates)
            {
                (double X, double Y, bool _) a = DefaultPick(other);
                (double X, double Y, bool _) b = DefaultPick(line);
                Dimension? angle = Try(() => dimensions.AddAngleBetweenObjects(other, a.X, a.Y, 0, false, line, b.X, b.Y, 0, false));
                if (angle is not null && angle.Value > Math.PI + AngleTolerance)
                {
                    // The angle is the counter-clockwise turn from the first line to the second: the other order gives it under 180°.
                    Late.Call(angle, "Delete");
                    angle = Try(() => dimensions.AddAngleBetweenObjects(line, b.X, b.Y, 0, false, other, a.X, a.Y, 0, false));
                }

                if (angle is not null && keep(angle, "Angle"))
                {
                    if (reference >= 0)
                    {
                        used[reference] = true;
                    }

                    if (done())
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The position of the sketch, as a designer places it (a point of the sketch tied to another point): the largest contour
    /// first, from the base planes (on them when it touches them, symmetric about them when a base plane crosses one of its
    /// lines in the middle, else by distances); each other contour from the lines of those already placed (a hole from the
    /// edges of its plate), or from the base planes when it lies on them or no line serves. Model edges brought in
    /// (<paramref name="linked"/>) count as placed from the start, and the <paramref name="preferred"/> lines go before
    /// anything. Each point once (connected ends share it), then any skipped.
    /// </summary>
    private static void Positions(object[] elements, object[] drawn, List<Line2d> references, bool[] used, List<Line2d> linked,
        List<Line2d> preferred, Dimensions dimensions, Relations2d relations, Func<Dimension, string, bool> keep, Func<bool> done,
        List<object> added, Definition definition)
    {
        List<Line2d> placedLines = [.. linked.Where(l => !preferred.Any(p => ReferenceEquals(p, l)))];
        List<object[]> groups = Contours(elements);
        for (int g = 0; g < groups.Count; g++)
        {
            object[] group = groups[g];
            if (g == 0 && Centre(group, drawn, references, used, relations, added, definition) && done())
            {
                return;
            }

            bool Place(object element, int key, double x, double y)
            {
                foreach ((Line2d reference, int index) in Candidates((x, y), references, placedLines, preferred).Where(c => !ReferenceEquals(c.Line, element)))
                {
                    if (PlaceFrom(reference, element, key, x, y, dimensions, relations, keep, added, definition))
                    {
                        if (index >= 0)
                        {
                            used[index] = true;
                        }

                        if (done())
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            var placed = new List<(double X, double Y)>();
            var skipped = new List<(object Element, int Key, double X, double Y)>();
            foreach (object element in group)
            {
                foreach ((int key, double x, double y) in PlacingKeyPoints(element))
                {
                    if (placed.Any(p => Same(p, (x, y))))
                    {
                        skipped.Add((element, key, x, y));
                        continue;
                    }

                    placed.Add((x, y));
                    if (Place(element, key, x, y))
                    {
                        return;
                    }
                }
            }

            foreach ((object element, int key, double x, double y) in skipped)
            {
                if (Place(element, key, x, y))
                {
                    return;
                }
            }

            placedLines.AddRange(group.OfType<Line2d>().Where(l => drawn.Any(d => ReferenceEquals(d, l)) && !preferred.Any(p => ReferenceEquals(p, l))));
        }
    }

    /// <summary>
    /// The block occurrences of the sketch, each a piece that moves and turns as a whole: its origin is placed from the base
    /// planes (<see cref="PlaceFrom"/> with its origin reference) and its turn by the angle between one of its lines (its
    /// member reference) and a base plane line that is not parallel to it. True when the sketch is done.
    /// </summary>
    private static bool PlaceBlocks(Profile profile, List<Line2d> references, bool[] used, Dimensions dimensions, Relations2d relations,
        Func<Dimension, string, bool> keep, Func<bool> done, List<object> added, Definition definition)
    {
        if (Late.TryGet(profile, "BlockOccurrences") is not { } occurrences)
        {
            return false;
        }

        foreach (object occurrence in Late.Items(occurrences))
        {
            object origin = Late.Get(occurrence, "OriginReference")!;
            object?[] at = Late.CallByRef(occurrence, "GetOrigin", [0.0, 0.0], 0, 1);
            double x = Convert.ToDouble(at[0], CultureInfo.InvariantCulture);
            double y = Convert.ToDouble(at[1], CultureInfo.InvariantCulture);
            for (int i = 0; i < references.Count; i++)
            {
                if (PlaceFrom(references[i], origin, 0, x, y, dimensions, relations, keep, added, definition))
                {
                    used[i] = true;
                    if (done())
                    {
                        return true;
                    }
                }
            }

            if (Late.Get(occurrence, "BlockView") is not { } view || Late.Items(Late.Get(view, "Lines2d")!).OfType<Line2d>().FirstOrDefault() is not { } member)
            {
                continue;
            }

            ((double X, double Y) start, (double X, double Y) end) = Ends(member);
            (double X, double Y) a = InSketch(occurrence, start);
            (double X, double Y) b = InSketch(occurrence, end);
            (double X, double Y) middle = ((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            object turned = Late.Get(occurrence, "MemberReference", member)!;
            for (int i = 0; i < references.Count; i++)
            {
                Line2d reference = references[i];
                ((double X, double Y) r1, (double X, double Y) r2) = Ends(reference);
                if (Cross(Unit(b.X - a.X, b.Y - a.Y), Unit(r2.X - r1.X, r2.Y - r1.Y)) < AngleTolerance)
                {
                    continue;
                }

                (double X, double Y, bool _) pick = DefaultPick(reference);
                Dimension? angle = Try(() => dimensions.AddAngleBetweenObjects(reference, pick.X, pick.Y, 0, false, turned, middle.X, middle.Y, 0, false));
                if (angle is not null && angle.Value > Math.PI + AngleTolerance)
                {
                    Late.Call(angle, "Delete");
                    angle = Try(() => dimensions.AddAngleBetweenObjects(turned, middle.X, middle.Y, 0, false, reference, pick.X, pick.Y, 0, false));
                }

                if (angle is not null && keep(angle, "Angle"))
                {
                    used[i] = true;
                    if (done())
                    {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>A point of a block's view in the sketch, where the occurrence puts it (moved, turned and scaled).</summary>
    private static (double X, double Y) InSketch(object occurrence, (double X, double Y) point)
    {
        object?[] r = Late.CallByRef(occurrence, "BlockViewToOccurrence", [point.X, point.Y, 0.0, 0.0], 2, 3);
        return (Convert.ToDouble(r[2], CultureInfo.InvariantCulture), Convert.ToDouble(r[3], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The lines a point is placed from: the <paramref name="preferred"/> ones (nearest first, index -1), a base plane line it
    /// lies on, then the nearest lines of the contours already placed (index -1), then the other base plane lines (index =
    /// position in <paramref name="references"/>).
    /// </summary>
    private static IEnumerable<(Line2d Line, int Index)> Candidates((double X, double Y) point, List<Line2d> references, List<Line2d> placedLines,
        List<Line2d> preferred)
    {
        foreach (Line2d line in preferred.OrderBy(l => PointToLine(point, l)))
        {
            yield return (line, -1);
        }

        var bases = references.Select((r, i) => (Line: r, Index: i, Distance: PointToLine(point, r))).ToList();
        foreach (var on in bases.Where(b => b.Distance < Coincident))
        {
            yield return (on.Line, on.Index);
        }

        foreach (Line2d line in placedLines.OrderBy(l => PointToLine(point, l)).Take(NearestLines))
        {
            yield return (line, -1);
        }

        foreach (var off in bases.Where(b => b.Distance >= Coincident))
        {
            yield return (off.Line, off.Index);
        }
    }

    /// <summary>Lines of the placed contours tried for each point, nearest first.</summary>
    private const int NearestLines = 4;

    /// <summary>
    /// "Point on" relations of the middles of the contour's lines that a base plane crosses there, so a contour centred on the
    /// origin is symmetric about it (a 40 x 20 rectangle from (-20,-10) needs only its two sizes). True if any was added.
    /// </summary>
    private static bool Centre(object[] group, object[] drawn, List<Line2d> references, bool[] used, Relations2d relations, List<object> added,
        Definition definition)
    {
        bool any = false;
        foreach (Line2d line in group.OfType<Line2d>().Where(l => drawn.Any(d => ReferenceEquals(d, l))))
        {
            int middle = -1;
            (double X, double Y) at = default;
            foreach ((int key, SolidEdgeFramework.KeyPointType type, double x, double y) in KeyPoints(line))
            {
                if (type == SolidEdgeFramework.KeyPointType.igKeyPointMiddle)
                {
                    (middle, at) = (key, (x, y));
                    break;
                }
            }

            for (int i = 0; middle >= 0 && i < references.Count; i++)
            {
                Line2d reference = references[i];
                if (!Parallel(reference, line) && PointToLine(at, reference) < Coincident
                    && TryRelate(() => relations.AddPointOn(line, middle, reference), added))
                {
                    used[i] = true;
                    definition.Relations++;
                    any = true;
                }
            }
        }

        return any;
    }

    /// <summary>The elements grouped into contours (joined by shared ends or centres), the largest first.</summary>
    private static List<object[]> Contours(object[] elements)
    {
        List<(double X, double Y)>[] points = [.. elements.Select(e => PlacingKeyPoints(e).Select(k => (k.X, k.Y)).ToList())];
        int[] root = [.. Enumerable.Range(0, elements.Length)];
        int Root(int i) => root[i] == i ? i : root[i] = Root(root[i]);
        for (int i = 0; i < elements.Length; i++)
        {
            for (int j = i + 1; j < elements.Length; j++)
            {
                if (points[i].Any(p => points[j].Any(q => Same(p, q))))
                {
                    root[Root(i)] = Root(j);
                }
            }
        }

        return [.. elements.Select((e, i) => (Element: e, Root: Root(i))).GroupBy(t => t.Root)
            .Select(g => g.Select(t => t.Element).ToArray()).OrderByDescending(Extent)];
    }

    /// <summary>
    /// Diagonal of the box round the contour: the range of each element (an arc's bulge, not its centre; an ellipse whole), or
    /// its key points for an element without one (a point).
    /// </summary>
    private static double Extent(object[] group)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (object element in group)
        {
            try
            {
                object?[] r = Late.CallByRef(element, "Range", [0.0, 0.0, 0.0, 0.0], 0, 1, 2, 3);
                xs.AddRange([Convert.ToDouble(r[0], CultureInfo.InvariantCulture), Convert.ToDouble(r[2], CultureInfo.InvariantCulture)]);
                ys.AddRange([Convert.ToDouble(r[1], CultureInfo.InvariantCulture), Convert.ToDouble(r[3], CultureInfo.InvariantCulture)]);
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                foreach ((int _, SolidEdgeFramework.KeyPointType _, double x, double y) in KeyPoints(element))
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        return xs.Count == 0 ? 0 : Math.Sqrt(Math.Pow(xs.Max() - xs.Min(), 2) + Math.Pow(ys.Max() - ys.Min(), 2));
    }

    /// <summary>Distance from a reference line to the key point, kept if it is driving; a zero distance becomes "point on".</summary>
    private static bool PlaceFrom(Line2d reference, object element, int key, double x, double y, Dimensions dimensions, Relations2d relations,
        Func<Dimension, string, bool> keep, List<object> added, Definition definition)
    {
        double expected = PointToLine((x, y), reference);
        ((double X, double Y) start, (double X, double Y) end) = Ends(reference);
        // From a vertical line the distance runs along x, from a horizontal one along y.
        string role = Math.Abs(start.X - end.X) < Coincident ? "X" : Math.Abs(start.Y - end.Y) < Coincident ? "Y" : "Distance";
        // A point on the line is related directly, which Solid Edge refuses when redundant: a zero distance is measured first
        // to the end of the line, and as 0 once the measurement axis is reversed, which can make Solid Edge delete the dimension
        // (verified with Solid Edge 2026 Foundation).
        if (expected < Coincident && TryRelate(() => relations.AddPointOn(element, key, reference), added))
        {
            definition.Relations++;
            return true;
        }

        Dimension? dimension = Try(() => dimensions.AddDistanceBetweenObjects(reference, x, y, 0, false, element, x, y, 0, true));
        if (dimension is null)
        {
            return false;
        }

        try
        {
            if (Math.Abs(dimension.Value - expected) > DistanceTolerance)
            {
                dimension.MeasurementAxisDirection = !dimension.MeasurementAxisDirection;
            }

            if (Math.Abs(dimension.Value - expected) > DistanceTolerance)
            {
                Late.Call(dimension, "Delete");
                return false;
            }
        }
        catch (COMException ex) when (ex.HResult is ComErrors.RpcEDisconnected or ComErrors.CoEObjNotConnected && Reachable(dimensions))
        {
            // Verified with Solid Edge 2026 Foundation: reversing the measurement axis can make Solid Edge delete the dimension.
            return false;
        }

        if (!keep(dimension, role))
        {
            return false;
        }

        if (expected < Coincident)
        {
            // The dimension goes first: "point on" next to it would be redundant and fail.
            added.Remove(dimension);
            definition.Dimensions.Remove(dimension);
            Late.Call(dimension, "Delete");
            try
            {
                added.Add(relations.AddPointOn(element, key, reference));
                definition.Relations++;
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                // The zero dimension again: it defines the same freedom.
                return keep(dimensions.AddDistanceBetweenObjects(reference, x, y, 0, false, element, x, y, 0, true), role);
            }
        }

        return true;
    }

    private const double Coincident = 1e-9;

    /// <summary>Sine of the largest angle between directions taken as parallel (or cosine, as perpendicular).</summary>
    private const double AngleTolerance = 1e-7;

    /// <summary>Whether Solid Edge still answers through the sketch (a single object it deleted gives the same error as Solid Edge gone).</summary>
    private static bool Reachable(Dimensions dimensions)
    {
        try
        {
            _ = dimensions.Count;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>The dimension, or null when Solid Edge refuses it (it measures nothing there or would add nothing).</summary>
    private static Dimension? Try(Func<Dimension?> add)
    {
        try
        {
            return add();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Adds the relation; false when Solid Edge refuses it as redundant (0x80040210) or not applicable.</summary>
    private static bool TryRelate(Func<Relation2d> add, List<object> added)
    {
        try
        {
            added.Add(add());
            return true;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// The elements tied to model edges (se_sketch 'project', the Include command). Verified: Solid Edge ties each to a hidden
    /// copy of the edge (a Line2d in none of the profile's collections) with an offset relation, whose second related object
    /// is the element.
    /// </summary>
    private static List<object> Linked(Relations2d relations, object[] elements)
    {
        var linked = new List<object>();
        foreach (Relation2d relation in Late.Items(relations).OfType<Relation2d>().Where(r => (ObjectType)r.Type == ObjectType.igOffsetRelation2d))
        {
            relation.GetRelatedObjectCount(out int count);
            if (count != 2)
            {
                continue;
            }

            relation.GetRelatedObject(1, out object a, out _);
            relation.GetRelatedObject(2, out object b, out _);
            object[] own = [.. new[] { a, b }.Where(o => elements.Any(e => ReferenceEquals(e, o)))];
            if (own.Length == 1)
            {
                linked.Add(own[0]);
            }
        }

        return linked;
    }

    /// <summary>What the size of the element is: the width or height of a horizontal or vertical line, else its length.</summary>
    private static string SizeRole(object element) => element switch
    {
        Line2d line when Ends(line) is var (s, e) && Math.Abs(s.Y - e.Y) < Coincident => "Width",
        Line2d line when Ends(line) is var (s, e) && Math.Abs(s.X - e.X) < Coincident => "Height",
        Line2d => "Length",
        Circle2d => "Diameter",
        _ => "Radius",
    };

    /// <summary>
    /// Names each dimension the definition added after what it sizes, under the sketch's name (Sketch_1_Width,
    /// Sketch_1_Diameter2), as a designer names the variables that drive a design. Verified: Variables.PutName renames a sketch
    /// dimension, and a name in use or not valid fails with RPC_E_CANTCALLOUT_INEXTERNALCALL or _INASYNCCALL, so only a plain
    /// name that no variable or dimension has is passed; a dimension that still cannot take it keeps its system name (V356).
    /// </summary>
    private static void Name(Profile profile, Definition definition)
    {
        if (definition.Dimensions.Count == 0 || DocumentOf(profile) is not SolidEdgeFramework.SolidEdgeDocument document)
        {
            return;
        }

        string prefix = new([.. NameOf(profile).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        prefix = (prefix.Length > 0 && char.IsAsciiLetter(prefix[0]) ? prefix : "Sketch_" + prefix).TrimEnd('_');
        prefix = prefix[..Math.Min(prefix.Length, MaxPrefix)];
        var variables = (SolidEdgeFramework.Variables)document.Variables;
        foreach (Dimension dimension in definition.Dimensions)
        {
            string role = definition.Roles.TryGetValue(dimension, out string? r) ? r : "Size";
            string? name = Enumerable.Range(1, MaxNameTries).Select(n => n == 1 ? $"{prefix}_{role}" : $"{prefix}_{role}{n}")
                .FirstOrDefault(n => !VariableService.InUse(document, n));
            if (name is null)
            {
                continue;
            }

            try
            {
                variables.PutName(dimension, name);
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                // The name only helps to read the variable table: the dimension keeps its system name.
            }
        }
    }

    private const int MaxPrefix = 40;

    private const int MaxNameTries = 50;

    /// <summary>Elements whose place the sketch must define (construction ones included).</summary>
    private static IEnumerable<object> PlacedElements(Profile profile) =>
        new object[]
        {
            profile.Lines2d, profile.Circles2d, profile.Arcs2d, profile.Ellipses2d, profile.EllipticalArcs2d, profile.BSplineCurves2d,
            profile.Points2d, profile.Holes2d, profile.MountingBoss2dCollection,
        }.SelectMany(collection => Late.Items(collection));

    /// <summary>Key points that place the element; circles and holes only by their centre (the diameter gives the rest).</summary>
    private static IEnumerable<(int Key, double X, double Y)> PlacingKeyPoints(object element)
    {
        foreach ((int key, SolidEdgeFramework.KeyPointType type, double x, double y) in KeyPoints(element))
        {
            if (Placing.Contains(type))
            {
                yield return (key, x, y);
            }
        }
    }

    private static IEnumerable<(int Key, SolidEdgeFramework.KeyPointType Type, double X, double Y)> KeyPoints(object element)
    {
        int count = element is Circle2d or Hole2d ? 1 : Convert.ToInt32(Late.Get(element, "KeyPointCount"), CultureInfo.InvariantCulture);
        for (int key = 0; key < count; key++)
        {
            object?[] r = Late.CallByRef(element, "GetKeyPoint", [key, 0.0, 0.0, 0.0, 0, 0], 1, 2, 3, 4, 5);
            yield return (key, (SolidEdgeFramework.KeyPointType)Convert.ToInt32(r[4], CultureInfo.InvariantCulture),
                Convert.ToDouble(r[1], CultureInfo.InvariantCulture), Convert.ToDouble(r[2], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Two base planes of the document seen edge-on in the sketch, crossing each other. Where Solid Edge does not project one
    /// (a sketch plane turned off the principal directions), its trace on the sketch plane is drawn as a fixed construction
    /// line instead: a reference like the projected ones, not a fix of the drawn geometry.
    /// </summary>
    private static List<Line2d> BasePlaneLines(Profile profile, Relations2d relations)
    {
        object document = DocumentOf(profile);
        object planes = document is SolidEdgeAssembly.AssemblyDocument assembly ? assembly.AsmRefPlanes : Late.Get(document, "RefPlanes")!;
        IReadOnlyList<object> bases = OrientedPlanes.Bases(Late.Items(planes));
        var lines = new List<Line2d>();
        bool Take(Line2d line)
        {
            if (lines.Count == 1 && Parallel(lines[0], line))
            {
                Late.Call(line, "Delete");
                return false;
            }

            lines.Add(line);
            return lines.Count == 2;
        }

        foreach (object plane in bases)
        {
            object? seen;
            try
            {
                seen = profile.ProjectRefPlane(plane);
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                continue;
            }

            if (seen is Line2d line && Take(line))
            {
                return lines;
            }
        }

        foreach (object plane in bases)
        {
            if (Trace(profile, relations, plane) is { } trace && Take(trace))
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>
    /// The line where the base plane cuts the sketch plane, drawn as construction and fixed; null when the planes are
    /// parallel or Solid Edge refuses it. Verified: Profile.ProjectRefPlane gives null for every base plane on a plane turned
    /// 30° or 45° from xy about yz (also for xz, square to it), and lines on xy, its offsets and the planes square to it.
    /// </summary>
    private static Line2d? Trace(Profile profile, Relations2d relations, object plane)
    {
        (double[] root, double[] normal) = RefPlaneService.PlaneData(plane);
        profile.Convert2DCoordinate(0, 0, out double ox, out double oy, out double oz);
        profile.Convert2DCoordinate(1, 0, out double xx, out double xy, out double xz);
        profile.Convert2DCoordinate(0, 1, out double yx, out double yy, out double yz);
        // The trace is a*u + b*v + c = 0 in the sketch's coordinates (u, v).
        double a = normal[0] * (xx - ox) + normal[1] * (xy - oy) + normal[2] * (xz - oz);
        double b = normal[0] * (yx - ox) + normal[1] * (yy - oy) + normal[2] * (yz - oz);
        double c = normal[0] * (ox - root[0]) + normal[1] * (oy - root[1]) + normal[2] * (oz - root[2]);
        double length = Math.Sqrt(a * a + b * b);
        if (length < AngleTolerance)
        {
            return null;
        }

        (double u, double v) = (-c * a / (length * length), -c * b / (length * length));
        (double du, double dv) = (-b / length * TraceHalfLength, a / length * TraceHalfLength);
        Line2d line = profile.Lines2d.AddBy2Points(u - du, v - dv, u + du, v + dv);
        try
        {
            profile.ToggleConstruction(line);
            relations.AddFix(line);
            return line;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            Late.Call(line, "Delete");
            return null;
        }
    }

    /// <summary>Half length of a drawn base plane trace (m), about that of the lines Solid Edge projects.</summary>
    private const double TraceHalfLength = 0.06;

    /// <summary>The part, sheet metal or assembly document that holds the profile.</summary>
    private static object DocumentOf(Profile profile)
    {
        object? node = profile.Parent;
        for (int i = 0; i < 4 && node is not null; i++)
        {
            if (node is SolidEdgeFramework.SolidEdgeDocument)
            {
                return node;
            }

            node = Late.TryGet(node, "Parent");
        }

        throw new ToolException("internal_error", "The document of the sketch was not found.", "Report this with the sketch handle.");
    }

    /// <summary>
    /// A construction line from the centre to the major and to the minor axis key point of each ellipse and elliptical arc that
    /// has none, tied to both, so their lengths and the major one's angle size and turn it (verified to drive it). A fixed
    /// curve or one brought in from a model edge gets none. Each axis is kept with the radius its length is.
    /// </summary>
    private static int EllipseAxes(Profile profile, Relations2d relations, Dictionary<object, string> axes, List<object> added, List<object> linked)
    {
        object[] curves = [.. Late.Items(profile.Ellipses2d).Concat(Late.Items(profile.EllipticalArcs2d))];
        if (curves.Length == 0)
        {
            return 0;
        }

        // Key points of the curves tied to each construction line (an axis is a line tied to the centre and to an axis key).
        var fixedCurves = new List<object>();
        var ties = new List<(Line2d Line, object Curve, int Key)>();
        foreach (Relation2d relation in Late.Items(relations).OfType<Relation2d>())
        {
            var type = (ObjectType)relation.Type;
            if (type is not (ObjectType.igKeyPointRelation2d or ObjectType.igFixRelation2d))
            {
                continue;
            }

            relation.GetRelatedObject(1, out object a, out int ka);
            if (type == ObjectType.igFixRelation2d)
            {
                fixedCurves.Add(a);
                continue;
            }

            relation.GetRelatedObject(2, out object b, out int kb);
            if (a is Line2d line && profile.IsConstructionElement(line))
            {
                ties.Add((line, b, kb));
            }
            else if (b is Line2d other && profile.IsConstructionElement(other))
            {
                ties.Add((other, a, ka));
            }
        }

        bool HasAxis(object curve, int center, int end) => ties.Any(t => ReferenceEquals(t.Curve, curve) && t.Key == end
            && ties.Any(u => ReferenceEquals(u.Line, t.Line) && ReferenceEquals(u.Curve, curve) && u.Key == center));
        int count = 0;
        foreach (object curve in curves.Where(c => !fixedCurves.Concat(linked).Any(f => ReferenceEquals(f, c))))
        {
            var keys = KeyPoints(curve).ToList();
            int Key(SolidEdgeFramework.KeyPointType type) => keys.FindIndex(k => k.Type == type);
            int center = Key(SolidEdgeFramework.KeyPointType.igKeyPointCenter);
            int major = Key(SolidEdgeFramework.KeyPointType.igKeyPointMajorAxis);
            int minor = Key(SolidEdgeFramework.KeyPointType.igKeyPointMinorAxis);
            if (center < 0 || major < 0 || minor < 0)
            {
                continue;
            }

            foreach (int end in new[] { major, minor }.Where(end => !HasAxis(curve, keys[center].Key, keys[end].Key)))
            {
                Line2d axis = profile.Lines2d.AddBy2Points(keys[center].X, keys[center].Y, keys[end].X, keys[end].Y);
                added.Add(axis);
                axes[axis] = end == major ? "MajorRadius" : "MinorRadius";
                profile.ToggleConstruction(axis);
                added.Add(relations.AddKeypoint(axis, LineStart, curve, keys[center].Key));
                added.Add(relations.AddKeypoint(axis, LineEnd, curve, keys[end].Key));
                count += 2;
            }
        }

        return count;
    }

    /// <summary>Connect relations between coincident ends of lines and arcs that no relation joins yet.</summary>
    private static int ConnectEnds(Relations2d relations, object[] elements, List<object> added)
    {
        List<(object Element, int Key, double X, double Y)> ends = Ends(elements);
        int[] group = [.. Enumerable.Range(0, ends.Count)];
        int Root(int i) => group[i] == i ? i : group[i] = Root(group[i]);
        int Find(object element, int key) => ends.FindIndex(e => ReferenceEquals(e.Element, element) && e.Key == key);
        foreach (Relation2d relation in Late.Items(relations).OfType<Relation2d>())
        {
            if ((ObjectType)relation.Type != ObjectType.igKeyPointRelation2d)
            {
                continue;
            }

            relation.GetRelatedObject(1, out object a, out int ka);
            relation.GetRelatedObject(2, out object b, out int kb);
            int ia = Find(a, ka);
            int ib = Find(b, kb);
            if (ia >= 0 && ib >= 0)
            {
                group[Root(ia)] = Root(ib);
            }
        }

        int count = 0;
        for (int i = 0; i < ends.Count; i++)
        {
            for (int j = i + 1; j < ends.Count; j++)
            {
                if (Same((ends[i].X, ends[i].Y), (ends[j].X, ends[j].Y)) && Root(i) != Root(j))
                {
                    // Verified: the ends that a fillet or chamfer trims are already tied, and Solid Edge refuses another (0x80040210).
                    (object Element, int Key, double X, double Y) a = ends[i], b = ends[j];
                    count += TryRelate(() => relations.AddKeypoint(a.Element, a.Key, b.Element, b.Key), added) ? 1 : 0;
                    group[Root(i)] = Root(j);
                }
            }
        }

        return count;
    }

    /// <summary>Horizontal or vertical relation on lines that lie along an axis and have neither.</summary>
    private static int AlignLines(Relations2d relations, object[] elements, List<object> added)
    {
        var aligned = new List<object>();
        foreach (Relation2d relation in Late.Items(relations).OfType<Relation2d>())
        {
            if ((ObjectType)relation.Type is ObjectType.igHorizontalRelation2d or ObjectType.igVerticalRelation2d)
            {
                relation.GetRelatedObject(1, out object line, out _);
                aligned.Add(line);
            }
        }

        int count = 0;
        foreach (Line2d line in elements.OfType<Line2d>())
        {
            if (aligned.Any(a => ReferenceEquals(a, line)))
            {
                continue;
            }

            ((double X, double Y) start, (double X, double Y) end) = Ends(line);
            if (Math.Abs(start.Y - end.Y) < Coincident)
            {
                count += TryRelate(() => relations.AddHorizontal(line), added) ? 1 : 0;
            }
            else if (Math.Abs(start.X - end.X) < Coincident)
            {
                count += TryRelate(() => relations.AddVertical(line), added) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>
    /// The relations the drawing shows between its elements: an end on another line, circle or arc (not at its ends), tangent
    /// where a line and an arc or two arcs meet with the same direction, concentric circles and arcs, and perpendicular and
    /// parallel inclined lines (horizontal and vertical ones already have their relation). Each is added once; one that Solid
    /// Edge refuses as redundant is left out, and none between two model edges (the model relates them).
    /// </summary>
    private static int GeometricRelations(Relations2d relations, object[] elements, List<object> added, Func<object, bool> isLinked)
    {
        var existing = new List<(ObjectType Type, object A, int KeyA, object B, int KeyB)>();
        foreach (Relation2d relation in Late.Items(relations).OfType<Relation2d>())
        {
            relation.GetRelatedObjectCount(out int related);
            if (related < 2)
            {
                continue;
            }

            relation.GetRelatedObject(1, out object a, out int ka);
            relation.GetRelatedObject(2, out object b, out int kb);
            existing.Add(((ObjectType)relation.Type, a, ka, b, kb));
        }

        // A "point on" relation is per key point: each end of a line may lie on the same circle.
        bool Has(ObjectType type, object a, object b, int key) => existing.Any(r => r.Type == type
            && ((ReferenceEquals(r.A, a) && ReferenceEquals(r.B, b) && (key < 0 || r.KeyA == key))
                || (ReferenceEquals(r.A, b) && ReferenceEquals(r.B, a) && (key < 0 || r.KeyB == key))));
        int count = 0;
        void Relate(ObjectType type, object a, object b, Func<Relation2d> add, int key = -1)
        {
            if (!(isLinked(a) && isLinked(b)) && !Has(type, a, b, key) && TryRelate(add, added))
            {
                existing.Add((type, a, key, b, -1));
                count++;
            }
        }

        List<(object Element, int Key, double X, double Y)> ends = Ends(elements);
        foreach ((object element, int key, double x, double y) in ends)
        {
            foreach (object target in elements.Where(t => !ReferenceEquals(t, element) && t is Line2d or Circle2d or Arc2d))
            {
                if (OnInterior(target, (x, y)) && !ends.Any(e => ReferenceEquals(e.Element, target) && Same((e.X, e.Y), (x, y))))
                {
                    Relate(ObjectType.igPointOnRelation2d, element, target, () => relations.AddPointOn(element, key, target), key);
                }
            }
        }

        for (int i = 0; i < ends.Count; i++)
        {
            for (int j = i + 1; j < ends.Count; j++)
            {
                (object Element, int Key, double X, double Y) a = ends[i], b = ends[j];
                // Elliptical arcs are left out: their tangent is not square to the radius, so it is not judged here.
                if (ReferenceEquals(a.Element, b.Element) || !Same((a.X, a.Y), (b.X, b.Y)) || (a.Element is Line2d && b.Element is Line2d)
                    || a.Element is EllipticalArc2d || b.Element is EllipticalArc2d)
                {
                    continue;
                }

                if (Cross(Direction(a.Element, (a.X, a.Y)), Direction(b.Element, (b.X, b.Y))) < AngleTolerance)
                {
                    Relate(ObjectType.igTangentRelation2d, a.Element, b.Element, () => relations.AddTangent(a.Element, b.Element));
                }
            }
        }

        object[] round = [.. elements.Where(e => e is Circle2d or Arc2d)];
        for (int i = 0; i < round.Length; i++)
        {
            for (int j = i + 1; j < round.Length; j++)
            {
                object a = round[i], b = round[j];
                if (Same(KeyPoint(a, 0), KeyPoint(b, 0)))
                {
                    Relate(ObjectType.igConcentricRelation2d, a, b, () => relations.AddConcentric(a, b));
                }
            }
        }

        Line2d[] inclined = [.. elements.OfType<Line2d>().Where(l => !AxisAligned(l))];
        for (int i = 0; i < inclined.Length; i++)
        {
            for (int j = i + 1; j < inclined.Length; j++)
            {
                Line2d a = inclined[i], b = inclined[j];
                (double X, double Y) u = Direction(a, Ends(a).Start), v = Direction(b, Ends(b).Start);
                if (Meet(a, b) && Math.Abs(u.X * v.X + u.Y * v.Y) < AngleTolerance)
                {
                    Relate(ObjectType.igPerpendicularRelation2d, a, b, () => relations.AddPerpendicular(a, b));
                }
                else if (Cross(u, v) < AngleTolerance && PointToLine(Ends(b).Start, a) > Coincident)
                {
                    Relate(ObjectType.igParallelRelation2d, a, b, () => relations.AddParallel(a, b));
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Whether the normal (not construction) elements of the profile form closed loops: every end of a line, arc or curve meets
    /// another end (circles, ellipses and closed curves have none to meet).
    /// </summary>
    internal static bool IsClosed(Profile profile)
    {
        object[] elements = [.. new object[]
        {
            profile.Lines2d, profile.Arcs2d, profile.Circles2d, profile.Ellipses2d, profile.EllipticalArcs2d, profile.BSplineCurves2d,
        }.SelectMany(collection => Late.Items(collection)).Where(e => !profile.IsConstructionElement(e))];
        List<(double X, double Y)> ends = [.. Ends(elements).Select(e => (e.X, e.Y))];
        foreach (BSplineCurve2d curve in elements.OfType<BSplineCurve2d>())
        {
            ends.AddRange(KeyPoints(curve)
                .Where(k => k.Type is SolidEdgeFramework.KeyPointType.igKeyPointStart or SolidEdgeFramework.KeyPointType.igKeyPointEnd)
                .Select(k => (k.X, k.Y)));
        }

        return ends.All(end => ends.Count(other => Same(other, end)) > 1);
    }

    /// <summary>Ends of the lines and arcs (key points 0/1 of a line, 1/2 of an arc).</summary>
    private static List<(object Element, int Key, double X, double Y)> Ends(object[] elements)
    {
        var ends = new List<(object Element, int Key, double X, double Y)>();
        foreach (object element in elements)
        {
            int[] keys = element switch
            {
                Line2d => [LineStart, LineEnd],
                Arc2d or EllipticalArc2d => [1, 2],
                _ => [],
            };
            foreach (int key in keys)
            {
                (double x, double y) = KeyPoint(element, key);
                ends.Add((element, key, x, y));
            }
        }

        return ends;
    }

    /// <summary>Unit tangent of a line or an arc at a point of it (the sense does not matter).</summary>
    private static (double X, double Y) Direction(object element, (double X, double Y) at)
    {
        if (element is Line2d line)
        {
            ((double X, double Y) s, (double X, double Y) e) = Ends(line);
            return Unit(e.X - s.X, e.Y - s.Y);
        }

        (double X, double Y) c = KeyPoint(element, 0);
        return Unit(-(at.Y - c.Y), at.X - c.X);
    }

    /// <summary>Whether the point lies on the line segment, circle or arc away from its ends.</summary>
    private static bool OnInterior(object target, (double X, double Y) p)
    {
        switch (target)
        {
            case Line2d line:
                ((double X, double Y) a, (double X, double Y) b) = Ends(line);
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
                return t > 0 && t < 1 && PointToLine(p, line) < Coincident;
            case Circle2d circle:
                (double X, double Y) c = KeyPoint(circle, 0);
                return Math.Abs(Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y)) - circle.Radius) < Coincident;
            case Arc2d arc:
                (double X, double Y) o = KeyPoint(arc, 0);
                if (Math.Abs(Math.Sqrt((p.X - o.X) * (p.X - o.X) + (p.Y - o.Y) * (p.Y - o.Y)) - arc.Radius) >= Coincident)
                {
                    return false;
                }

                double turn = Math.Atan2(p.Y - o.Y, p.X - o.X) - arc.StartAngle;
                double sweep = arc.SweepAngle;
                double along = ((Math.Sign(sweep) * turn % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
                return along > AngleTolerance && along < Math.Abs(sweep) - AngleTolerance;
            default:
                return false;
        }
    }

    /// <summary>Whether two lines share an end.</summary>
    private static bool Meet(Line2d a, Line2d b)
    {
        ((double X, double Y) a1, (double X, double Y) a2) = Ends(a);
        ((double X, double Y) b1, (double X, double Y) b2) = Ends(b);
        return Same(a1, b1) || Same(a1, b2) || Same(a2, b1) || Same(a2, b2);
    }

    private static bool AxisAligned(Line2d line)
    {
        ((double X, double Y) start, (double X, double Y) end) = Ends(line);
        return Math.Abs(start.Y - end.Y) < Coincident || Math.Abs(start.X - end.X) < Coincident;
    }

    private static bool Same((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) < Coincident && Math.Abs(a.Y - b.Y) < Coincident;

    private static double Cross((double X, double Y) u, (double X, double Y) v) => Math.Abs(u.X * v.Y - u.Y * v.X);

    private static double Dot((double X, double Y) u, (double X, double Y) v) => Math.Abs(u.X * v.X + u.Y * v.Y);

    /// <summary>Deletes what a failed definition added, newest first.</summary>
    private static void Undo(List<object> added, List<Line2d> references)
    {
        foreach (object item in Enumerable.Reverse(added).Concat(references))
        {
            try
            {
                Late.Call(item, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // Deleted with something else (a relation goes with its element or reference line).
            }
        }
    }
}
