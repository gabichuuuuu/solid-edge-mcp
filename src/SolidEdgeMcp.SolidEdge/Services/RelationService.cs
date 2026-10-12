using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Assembly relation: <paramref name="Occurrence1"/> is moved until it meets <paramref name="Occurrence2"/> (or until an
/// assembly plane when omitted). Elements: faces (se_geometry with the occurrence as 'source') or planes of the part.
/// </summary>
public sealed record RelationSpec(
    string Type,
    string? Occurrence1,
    string? Element1,
    string? Occurrence2,
    string? Element2,
    string? Axis1 = null,
    string? Axis2 = null,
    double Offset = 0,
    bool Align = false,
    bool Flip = false,
    double? Angle = null,
    string[]? Occurrences = null,
    string? Plane = null,
    string[]? Centered = null,
    string[]? Between = null,
    string? Point1 = null,
    string? Point2 = null,
    double[]? Ratio = null,
    string? RatioBy = null,
    bool? LockRotation = null);

/// <summary>
/// Assembly relations. The direction of Solid Edge's <c>NormalsAligned</c> does not always correspond to "align" or
/// "face against" the faces, so each relation is checked against the resulting geometry in assembly coordinates
/// (outward normals and signed distance) and is inverted if needed, as a person would do when looking at it.
/// </summary>
public static partial class RelationService
{
    public static readonly IReadOnlyList<string> Types =
        ["planar", "axial", "insert", "parallel", "tangent", "angle", "rigid_set", "center_plane", "connect", "coordinate_system", "gear"];

    /// <summary>Result tolerances: 0.001 mm and a cosine of about 0.08°.</summary>
    private const double DistanceTolerance = 1e-6;
    private const double ParallelCosine = 0.999999;
    private const double PerpendicularCosine = 0.0014;
    private const double AngleTolerance = 0.0014;

    public static object List(SeContext context, string? documentHandle, string? occurrence, int offset, int limit)
    {
        AssemblyDocument assembly = string.IsNullOrWhiteSpace(occurrence)
            ? AssemblyService.Assembly(context, documentHandle)
            : AssemblyService.Occurrence(context, occurrence).TopLevelDocument;
        int? id = string.IsNullOrWhiteSpace(occurrence) ? null : AssemblyService.Occurrence(context, occurrence).OccurrenceID;
        var rows = new List<object>();
        foreach (object relation in AssemblyService.Items<object>(assembly.Relations3d))
        {
            Occurrence[] members = Members(relation);
            if (id is { } wanted && !members.Any(m => m.OccurrenceID == wanted))
            {
                continue;
            }

            rows.Add(Describe(context, assembly, relation, members));
        }

        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        List<object> page = rows.Skip(start).Take(take).ToList();
        return new { count = rows.Count, offset = start, returned = page.Count, more = start + page.Count < rows.Count, relations = page };
    }

    public static object Delete(SeContext context, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw ToolException.InvalidArgument("'relation' is required: a handle from se_relations 'list' or 'add'.");
        }

        object relation = context.Handles.Resolve(handle, "relation");
        string type = TypeName(relation);
        Late.Call(relation, "Delete");
        context.Handles.Remove(handle, "the relation was deleted");
        return new { deleted = handle, type };
    }

    public static object Add(SeContext context, string? documentHandle, RelationSpec spec)
    {
        if (!Types.Contains(spec.Type))
        {
            throw ToolException.InvalidArgument($"Unknown relation type '{spec.Type}'.",
                $"Use one of: {string.Join(", ", Types)}. Cam and path relations leave Solid Edge 2026 unstable through the API: add them with the Solid Edge commands.");
        }

        if (spec.LockRotation is not null && spec.Type is not ("axial" or "insert"))
        {
            throw ToolException.InvalidArgument("'lockRotation' applies to axial and insert relations.");
        }

        switch (spec.Type)
        {
            case "rigid_set":
                return RigidSet(context, spec.Occurrences);
            case "center_plane":
                return CenterPlane(context, spec);
            case "connect":
                return Connect(context, spec);
            case "coordinate_system":
                return CoordinateSystems(context, spec);
            case "gear":
                return Gear(context, spec);
        }

        Occurrence moving = OccurrenceOf(context, spec.Occurrence1, spec.Element1, "occurrence1", "element1")
            ?? throw ToolException.InvalidArgument("'occurrence1' is required (the occurrence that moves), unless 'element1' is a face or edge handle from se_geometry with 'source' = that occurrence.");
        AssemblyDocument assembly = moving.TopLevelDocument;
        Occurrence? target = OccurrenceOf(context, spec.Occurrence2, spec.Element2, "occurrence2", "element2");
        if (target is not null && target.OccurrenceID == moving.OccurrenceID)
        {
            throw ToolException.InvalidArgument("'occurrence1' and 'occurrence2' must be different occurrences.");
        }

        Side first = Resolve(context, assembly, moving, spec.Element1, "element1");
        Side second = Resolve(context, assembly, target, spec.Element2, "element2");
        Side? firstAxis = spec.Type == "insert" ? Resolve(context, assembly, moving, spec.Axis1, "axis1") : null;
        Side? secondAxis = spec.Type == "insert" ? Resolve(context, assembly, target, spec.Axis2, "axis2") : null;
        double offset = Units.MmToM(spec.Offset);
        if (!double.IsFinite(offset))
        {
            throw ToolException.InvalidArgument("'offset' must be a finite number (mm).");
        }

        if (spec.Type is "axial" or "angle" && offset != 0)
        {
            throw ToolException.InvalidArgument($"'offset' does not apply to {spec.Type} relations.");
        }

        if (spec.Type == "parallel" && offset < 0)
        {
            throw ToolException.InvalidArgument("For parallel axes 'offset' is the distance between them (mm, >= 0; 0 = free distance).");
        }

        double? angle = null;
        if (spec.Type == "angle")
        {
            double degrees = spec.Angle ?? throw ToolException.InvalidArgument("An angle relation requires 'angle' (degrees between the faces, 0-180).");
            if (!double.IsFinite(degrees) || degrees < 0 || degrees > 180)
            {
                throw ToolException.InvalidArgument("'angle' must be between 0 and 180 degrees.");
            }

            angle = Units.DegToRad(degrees);
        }

        // Geometry validation first: the error is raised before touching the assembly.
        switch (spec.Type)
        {
            case "axial" or "parallel":
                AxisOf(first, "element1");
                AxisOf(second, "element2");
                break;
            case "tangent":
                CylinderOf(first, "element1");
                if (second.Element is not Face { Geometry: SolidEdgeGeometry.Cylinder })
                {
                    PlaneOf(second, "element2");
                }

                break;
            default:
                PlaneOf(first, "element1");
                PlaneOf(second, "element2");
                break;
        }

        object? measure = spec.Type == "angle" ? MeasurePlane(context, assembly, first, second, spec.Plane) : null;
        if (firstAxis is not null)
        {
            AxisOf(firstAxis, "axis1");
            AxisOf(secondAxis!, "axis2");
        }

        moving.GetTransform(out double x, out double y, out double z, out double ax, out double ay, out double az);
        bool ungrounded = AssemblyService.Unground(assembly, moving);
        var created = new List<object>();
        try
        {
            Relations3d relations = assembly.Relations3d;
            object? planar = null;
            if (spec.Type is "planar" or "insert")
            {
                planar = AddPlanar(relations, first, second, spec.Align, created);
                SetPlanar((PlanarRelation3d)planar, first, second, offset, spec.Align);
            }

            if (spec.Type == "axial")
            {
                AxialRelation3d axial = AddAxial(relations, first, second, spec.Flip, "element", created);
                if (spec.LockRotation is bool locked)
                {
                    LockRotation(axial, locked);
                }
            }
            else if (spec.Type == "parallel")
            {
                AddParallel(relations, first, second, offset, created);
            }
            else if (spec.Type == "tangent")
            {
                AddTangent(relations, first, second, offset, created);
            }
            else if (spec.Type == "angle")
            {
                AddAngle(relations, first, second, angle!.Value, measure!, created);
            }
            else if (spec.Type == "insert")
            {
                // Like the Solid Edge Insert command: planar mate + axial with the rotation locked.
                LockRotation(AddAxial(relations, firstAxis!, secondAxis!, spec.Flip, "axis", created), spec.LockRotation ?? true);
                if (planar is not null)
                {
                    // The axis may have rotated the part: the face is checked again.
                    SetPlanar((PlanarRelation3d)planar, first, second, offset, spec.Align);
                }
            }

            foreach (object relation in created)
            {
                string status = DetailedStatus(relation);
                if (status != "solved")
                {
                    throw new ToolException("relation_failed", $"The {TypeName(relation)} relation is {status}.",
                        "Check that the elements can be related (parallel planes, coaxial cylinders) and that the occurrence is not fixed by other relations.");
                }
            }
        }
        catch (Exception ex) when (Rejected(ex))
        {
            foreach (object relation in created)
            {
                TryDelete(relation);
            }

            PutBack(assembly, moving, [x, y, z, ax, ay, az], ungrounded);

            // Moving an occurrence does not change its faces: the handles stay valid (the coordinates are queried again).
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("relation_failed", $"Solid Edge rejected the {spec.Type} relation: {ComErrors.Unwrap(ex).Message}",
                "Use planar faces or reference planes for planar relations and cylindrical faces or straight edges for axial ones; the occurrence was put back where it was.", ex);
        }

        Occurrence[] none = [];
        return new
        {
            relations = created.Select(r => Describe(context, assembly, r, none)).ToList(),
            occurrence = AssemblyService.Describe(context, assembly, moving, AssemblyService.GroundedIds(assembly)),
            ungrounded,
        };
    }

    /// <summary>
    /// Creates the planar relation. If other relations already prevent rotating the part and Solid Edge interprets
    /// <c>NormalsAligned</c> in the direction that a symmetry would require, the relation is left unsatisfied and changing it afterwards
    /// <c>NormalsAligned</c> does not recover it (verified in Solid Edge 2026): it is deleted and created with the opposite value.
    /// </summary>
    private static PlanarRelation3d AddPlanar(Relations3d relations, Side first, Side second, bool align, List<object> created)
    {
        PlanarRelation3d relation = Create(align);
        if (DetailedStatus(relation) != "unsatisfied")
        {
            return relation;
        }

        created.Remove(relation);
        TryDelete(relation);
        relation = Create(!align);
        if (DetailedStatus(relation) == "unsatisfied")
        {
            throw new ToolException("relation_failed", "The planar relation cannot be satisfied together with the occurrence's other relations.",
                "Check the relations that hold the occurrence (se_relations list with 'occurrence') and that this one does not contradict them.");
        }

        return relation;

        PlanarRelation3d Create(bool normalsAligned)
        {
            Array none1 = new double[3];
            Array none2 = new double[3];
            PlanarRelation3d added = relations.AddPlanar(first.Reference, second.Reference, normalsAligned, ref none1, ref none2);
            created.Add(added);
            return added;
        }
    }

    /// <summary>Adjusts orientation (facing or aligned) and the offset sign, checking the result.</summary>
    private static void SetPlanar(PlanarRelation3d relation, Side first, Side second, double offset, bool align)
    {
        if (offset != 0)
        {
            relation.Offset = offset;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            (double[] p1, double[] n1) = PlaneOf(first, "element1");
            (double[] p2, double[] n2) = PlaneOf(second, "element2");
            double cosine = Dot(n1, n2);
            if (align ? cosine < ParallelCosine : cosine > -ParallelCosine)
            {
                relation.NormalsAligned = !relation.NormalsAligned;
                continue;
            }

            double distance = Dot(Minus(p1, p2), n2);
            if (Math.Abs(distance - offset) <= DistanceTolerance)
            {
                return;
            }

            if (offset != 0 && Math.Abs(distance + offset) <= DistanceTolerance)
            {
                relation.Offset = -relation.Offset;
                continue;
            }

            throw new ToolException("relation_failed",
                $"After the planar relation the faces are {Geo.Mm(distance)} mm apart instead of {Geo.Mm(offset)} mm.",
                "Check that the occurrence is not held by other relations (se_relations list) and that the faces are planar.");
        }

        throw new ToolException("relation_failed", $"The planar relation could not {(align ? "align" : "face")} the two faces.",
            "Check the faces with se_geometry (source = the occurrence) and try 'align' the other way.");
    }

    /// <summary>
    /// Axial relation keeping the current orientation of the part (or the opposite one with <paramref name="flip"/>). The
    /// relation is added to <paramref name="created"/> as soon as it exists, so it can be undone if something fails later.
    /// </summary>
    private static AxialRelation3d AddAxial(Relations3d relations, Side first, Side second, bool flip, string argument, List<object> created)
    {
        (_, double[] d1) = AxisOf(first, argument + "1");
        (_, double[] d2) = AxisOf(second, argument + "2");
        bool aligned = (Dot(d1, d2) >= 0) ^ flip;
        AxialRelation3d relation = relations.AddAxial(first.Reference, second.Reference, aligned);
        created.Add(relation);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (double[] a1, double[] e1) = AxisOf(first, argument + "1");
            (double[] a2, double[] e2) = AxisOf(second, argument + "2");
            double cosine = Dot(e1, e2);
            double gap = Geo.Length(Geo.Cross(Minus(a1, a2), e2));
            if (Math.Abs(cosine) < ParallelCosine || gap > DistanceTolerance)
            {
                throw new ToolException("relation_failed", $"After the axial relation the axes are {Geo.Mm(gap)} mm apart"
                    + (Math.Abs(cosine) < ParallelCosine ? " and not parallel." : "."),
                    "Check that the occurrence is not held by other relations (se_relations list).");
            }

            if ((cosine > 0) == aligned)
            {
                return relation;
            }

            relation.Flip();
        }

        throw new ToolException("relation_failed", "The axial relation could not keep the requested orientation.",
            "Try again with 'flip' the other way.");
    }

    /// <summary>Parallel axes (Solid Edge: axial with parallel offset), at the given distance or free if it is 0.</summary>
    private static void AddParallel(Relations3d relations, Side first, Side second, double offset, List<object> created)
    {
        AxialRelation3d relation = relations.AddAxialWithParallelOffset(first.Reference, second.Reference, offset > 0, offset);
        created.Add(relation);
        (double[] a1, double[] e1) = AxisOf(first, "element1");
        (double[] a2, double[] e2) = AxisOf(second, "element2");
        double gap = Geo.Length(Geo.Cross(Minus(a1, a2), e2));
        if (Math.Abs(Dot(e1, e2)) < ParallelCosine || (offset > 0 && Math.Abs(gap - offset) > DistanceTolerance))
        {
            throw new ToolException("relation_failed", $"After the parallel relation the axes are {Geo.Mm(gap)} mm apart"
                + (Math.Abs(Dot(e1, e2)) < ParallelCosine ? " and not parallel." : $" instead of {Geo.Mm(offset)} mm."),
                "Check that the occurrence is not held by other relations (se_relations list).");
        }
    }

    /// <summary>
    /// Tangency of a cylinder with a plane (on the outer side of the face) or with another cylinder (external). Solid
    /// Edge chooses the half-space with <c>IsHalfSpacePositive</c>, which cannot be changed later: if the cylinder ends up
    /// on the other side the relation is deleted and created with the opposite value.
    /// </summary>
    private static void AddTangent(Relations3d relations, Side first, Side second, double offset, List<object> created)
    {
        string found = "";
        foreach (bool positive in new[] { true, false })
        {
            Array point1 = new double[3];
            Array point2 = new double[3];
            TangentRelation3d relation = relations.AddTangent(first.Reference, second.Reference, ref point1, ref point2, offset, positive);
            created.Add(relation);
            (bool tangent, found) = TangentResult(first, second, offset);
            if (tangent && DetailedStatus(relation) == "solved")
            {
                return;
            }

            created.Remove(relation);
            TryDelete(relation);
        }

        throw new ToolException("relation_failed", $"The tangent relation did not leave the cylinder outside the other element ({found}).",
            "Check that the cylinder's axis can lie parallel to the plane or to the other cylinder and that the occurrence is not held by other relations.");
    }

    /// <summary>Checks the external tangency with the requested offset; also returns the measured values for the error.</summary>
    private static (bool Tangent, string Found) TangentResult(Side first, Side second, double offset)
    {
        (double[] a1, double[] d1, double r1) = CylinderOf(first, "element1");
        if (second.Element is Face { Geometry: SolidEdgeGeometry.Cylinder })
        {
            (double[] a2, double[] d2, double r2) = CylinderOf(second, "element2");
            double between = Geo.Length(Geo.Cross(Minus(a1, a2), d2));
            double expected = r1 + r2 + offset;
            return (Math.Abs(Dot(d1, d2)) >= ParallelCosine && Math.Abs(between - expected) <= DistanceTolerance,
                $"axes {Geo.Mm(between)} mm apart, expected {Geo.Mm(expected)} mm");
        }

        (double[] p2, double[] n2) = PlaneOf(second, "element2");
        double height = Dot(Minus(a1, p2), n2);
        return (Math.Abs(Dot(d1, n2)) <= PerpendicularCosine && Math.Abs(height - (r1 + offset)) <= DistanceTolerance,
            $"axis {Geo.Mm(height)} mm from the face, expected {Geo.Mm(r1 + offset)} mm");
    }

    /// <summary>
    /// Angle between the outward normals of two planar faces (0° = same direction, 180° = facing each other), measured in
    /// <paramref name="measure"/>, an assembly plane perpendicular to the rotation axis. Without a measurement plane, Solid Edge
    /// leaves the relation without geometry (missing_geometry) and does not move the part (verified in Solid Edge 2026). The
    /// Solid Edge angle is measured with its own directions: the result is checked and the equivalent ones are tried.
    /// </summary>
    private static void AddAngle(Relations3d relations, Side first, Side second, double angle, object measure, List<object> created)
    {
        AngularRelation3d relation = relations.AddAngular(first.Reference, second.Reference, false, false,
            measure, measure, angle, true, true, true);
        created.Add(relation);
        double measured = 0;
        foreach (double candidate in new[] { angle, (2 * Math.PI) - angle, Math.PI - angle, Math.PI + angle })
        {
            if (candidate != angle)
            {
                relation.Angle = candidate;
            }

            (_, double[] n1) = PlaneOf(first, "element1");
            (_, double[] n2) = PlaneOf(second, "element2");
            measured = Math.Acos(Math.Clamp(Dot(n1, n2), -1, 1));
            if (Math.Abs(measured - angle) <= AngleTolerance && DetailedStatus(relation) == "solved")
            {
                return;
            }
        }

        throw new ToolException("relation_failed",
            $"After the angle relation the faces' normals are {Units.RadToDeg(measured):0.###}° apart instead of {Units.RadToDeg(angle):0.###}° ({DetailedStatus(relation)}).",
            "Use a measurement 'plane' perpendicular to the rotation axis and check that the occurrence is free to rotate about it (se_relations list with 'occurrence').");
    }

    /// <summary>
    /// Measurement plane of the angle: the given one ('1'-'3' or an assembly plane) or the base plane perpendicular to both
    /// faces (its normal, parallel to the rotation axis). With parallel faces the axis is not defined and must be given.
    /// </summary>
    private static object MeasurePlane(SeContext context, AssemblyDocument assembly, Side first, Side second, string? plane)
    {
        (_, double[] n1) = PlaneOf(first, "element1");
        (_, double[] n2) = PlaneOf(second, "element2");
        if (!string.IsNullOrWhiteSpace(plane))
        {
            Side measure = Resolve(context, assembly, null, plane, "plane");
            (_, double[] normal) = PlaneOf(measure, "plane");
            if (Math.Abs(Dot(normal, n1)) > PerpendicularCosine || Math.Abs(Dot(normal, n2)) > PerpendicularCosine)
            {
                throw ToolException.InvalidArgument("'plane' must be perpendicular to both faces (its normal is the rotation axis).",
                    "Use the assembly base plane whose normal is the axis the occurrence turns about, or create one with se_ref_plane.");
            }

            return measure.Reference;
        }

        double[] axis = Geo.Cross(n1, n2);
        if (Geo.Length(axis) < 1e-6)
        {
            throw ToolException.InvalidArgument("The faces are parallel, so the rotation axis is not defined: give the measurement 'plane'.",
                "'plane' = 'xy', 'yz' or 'xz' assembly base plane ('1'-'3' in the template's order), or an assembly plane handle, perpendicular to the rotation axis.");
        }

        axis = Unit(axis);
        foreach (object basePlane in OrientedPlanes.Bases(Late.Items(assembly.AsmRefPlanes)))
        {
            (_, double[] normal) = PlaneOf(new Side(basePlane, basePlane, null), "plane");
            if (Math.Abs(Dot(normal, axis)) >= ParallelCosine)
            {
                return basePlane;
            }
        }

        throw ToolException.InvalidArgument("No assembly base plane is perpendicular to both faces: give the measurement 'plane'.",
            "Create an assembly plane perpendicular to the rotation axis (se_ref_plane) and pass its handle as 'plane'.");
    }

    /// <summary>Rigid set: the occurrences move together, as one.</summary>
    private static object RigidSet(SeContext context, string[]? occurrences)
    {
        if (occurrences is not { Length: >= 2 })
        {
            throw ToolException.InvalidArgument("A rigid set needs 'occurrences': two or more occurrence handles.");
        }

        Occurrence[] members = occurrences.Select((handle, i) => AssemblyService.Occurrence(context, handle, $"occurrences[{i}]")).ToArray();
        if (members.Select(m => m.OccurrenceID).Distinct().Count() != members.Length)
        {
            throw ToolException.InvalidArgument("'occurrences' repeats an occurrence.");
        }

        AssemblyDocument assembly = members[0].TopLevelDocument;
        Array array = members.Cast<object>().ToArray();
        RigidSetRelation3d relation = assembly.Relations3d.AddRigidSet(members.Length, ref array);
        if (relation.OccurrenceCount != members.Length)
        {
            TryDelete(relation);
            throw new ToolException("relation_failed", $"The rigid set has {relation.OccurrenceCount} occurrences instead of {members.Length}.",
                "Check that the occurrences belong to the same assembly and are not already in another rigid set.");
        }

        return new { relations = new[] { Describe(context, assembly, relation, []) } };
    }

    /// <summary>
    /// Adds relations that move <paramref name="moving"/>, ungrounded first as for the other positioning relations. If Solid
    /// Edge rejects one, leaves it unsolved or <paramref name="add"/> finds the result wrong, the relations are deleted and
    /// the occurrence is put back where it was.
    /// </summary>
    private static object Position(SeContext context, AssemblyDocument assembly, Occurrence moving, string type, Action<Relations3d, List<object>> add,
        params Occurrence?[] others)
    {
        moving.GetTransform(out double x, out double y, out double z, out double ax, out double ay, out double az);
        // Solid Edge may move an occurrence the relation goes to when it is free: it is put back too if the relation fails.
        List<(Occurrence Occurrence, double[] Transform)> before = [.. others.OfType<Occurrence>()
            .Where(o => o.OccurrenceID != moving.OccurrenceID).DistinctBy(o => o.OccurrenceID).Select(o => (o, TransformOf(o)))];
        bool ungrounded = AssemblyService.Unground(assembly, moving);
        var created = new List<object>();
        try
        {
            add(assembly.Relations3d, created);
            RequireSolved(created);
        }
        catch (Exception ex) when (Rejected(ex))
        {
            foreach (object relation in created)
            {
                TryDelete(relation);
            }

            PutBack(assembly, moving, [x, y, z, ax, ay, az], ungrounded);
            foreach ((Occurrence other, double[] transform) in before)
            {
                PutBack(assembly, other, transform, false);
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("relation_failed", $"Solid Edge rejected the {type} relation: {ComErrors.Unwrap(ex).Message}",
                "Check the elements with se_geometry ('source' = the occurrence); the occurrence was put back where it was.", ex);
        }

        Occurrence[] none = [];
        return new
        {
            relations = created.Select(r => Describe(context, assembly, r, none)).ToList(),
            occurrence = AssemblyService.Describe(context, assembly, moving, AssemblyService.GroundedIds(assembly)),
            ungrounded,
        };
    }

    /// <summary>
    /// An error after which the relations are undone: a tool error or a call Solid Edge rejected (.NET raises E_INVALIDARG,
    /// E_NOINTERFACE and E_POINTER as ArgumentException, InvalidCastException and NullReferenceException), unless Solid
    /// Edge itself is gone.
    /// </summary>
    private static bool Rejected(Exception ex) => ex is ToolException
        || (ComErrors.Unwrap(ex) is COMException or ArgumentException or InvalidCastException or NullReferenceException && !ComErrors.IsGone(ex));

    private static double[] TransformOf(Occurrence occurrence)
    {
        occurrence.GetTransform(out double x, out double y, out double z, out double ax, out double ay, out double az);
        return [x, y, z, ax, ay, az];
    }

    /// <summary>Puts an occurrence back where it was (and grounds it again); best effort, so the original error is reported.</summary>
    private static void PutBack(AssemblyDocument assembly, Occurrence occurrence, double[] t, bool ground)
    {
        try
        {
            occurrence.PutTransform(t[0], t[1], t[2], t[3], t[4], t[5]);
            if (ground)
            {
                assembly.Relations3d.AddGround(occurrence);
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }

    private static void RequireSolved(IEnumerable<object> created)
    {
        foreach (object relation in created)
        {
            string status = DetailedStatus(relation);
            if (status != "solved")
            {
                throw new ToolException("relation_failed", $"The {TypeName(relation)} relation is {status}.",
                    "Check that the elements can be related and that the occurrence is not held by other relations (se_relations list with 'occurrence').");
            }
        }
    }

    /// <summary>
    /// Center-plane relation: one face or plane of the moving occurrence, or the plane midway between two of its faces, ends
    /// midway between two faces or planes of other occurrences or of the assembly. Verified in Solid Edge 2026: the single
    /// form takes the same element twice (Nothing as the second one fails with E_NOINTERFACE).
    /// </summary>
    private static object CenterPlane(SeContext context, RelationSpec spec)
    {
        if (spec.Centered is not { Length: 1 or 2 } centered)
        {
            throw ToolException.InvalidArgument("A center_plane relation needs 'centered': one face or plane of the occurrence that moves (that face is centred) or two (the plane midway between them is centred).",
                "Faces: se_geometry action 'faces' with 'source' = the occurrence.");
        }

        if (spec.Between is not { Length: 2 } between)
        {
            throw ToolException.InvalidArgument("A center_plane relation needs 'between': the two faces or planes it is centred between (of other occurrences, or assembly planes '1'-'3').");
        }

        Occurrence moving = OccurrenceOf(context, spec.Occurrence1, centered[0], "occurrence1", "centered[0]")
            ?? throw ToolException.InvalidArgument("'occurrence1' is required (the occurrence that moves), unless 'centered' holds face handles from se_geometry with 'source' = that occurrence.");
        AssemblyDocument assembly = moving.TopLevelDocument;
        Side[] mine = [.. centered.Select((handle, i) => Resolve(context, assembly, moving, handle, $"centered[{i}]"))];
        Side[] targets = [.. between.Select((handle, i) =>
        {
            Occurrence? owner = OccurrenceOf(context, spec.Occurrence2, handle, "occurrence2", $"between[{i}]");
            return owner is not null && owner.OccurrenceID == moving.OccurrenceID
                ? throw ToolException.InvalidArgument($"'between[{i}]' belongs to the occurrence that moves: use faces of other occurrences or assembly planes.")
                : Resolve(context, assembly, owner, handle, $"between[{i}]");
        })];

        // The faces must be parallel planes: checked before touching the assembly.
        (_, double[] normal) = PlaneOf(targets[0], "between[0]");
        foreach ((Side side, string name) in mine.Select((s, i) => (s, $"centered[{i}]")).Append((targets[1], "between[1]")))
        {
            if (Math.Abs(Dot(PlaneOf(side, name).Normal, normal)) < ParallelCosine)
            {
                throw ToolException.InvalidArgument($"'{name}' is not parallel to 'between[0]': a center-plane relation relates parallel planar faces or planes.");
            }
        }

        double Along(Side side, string name) => Dot(PlaneOf(side, name).Point, normal);
        Occurrence?[] owners = [.. targets.Select(t => t.Occurrence)];
        return Position(context, assembly, moving, "center_plane", (relations, created) =>
        {
            Array point1 = new double[3];
            Array point2 = new double[3];
            Array point3 = new double[3];
            Array point4 = new double[3];
            created.Add(relations.AddCenterPlane(mine[0].Reference, mine[^1].Reference, targets[0].Reference, targets[1].Reference,
                ref point1, ref point2, ref point3, ref point4));
            RequireSolved(created);
            double goal = (Along(targets[0], "between[0]") + Along(targets[1], "between[1]")) / 2;
            double middle = mine.Select((s, i) => Along(s, $"centered[{i}]")).Average();
            if (Math.Abs(middle - goal) > DistanceTolerance)
            {
                throw new ToolException("relation_failed",
                    $"After the center-plane relation the centred {(mine.Length == 2 ? "mid-plane" : "face")} is {Geo.Mm(middle - goal)} mm from the middle of the two faces.",
                    "Check that the occurrence is not held by other relations (se_relations list with 'occurrence').");
            }
        }, owners);
    }

    /// <summary>
    /// Connect relation (PointRelation3d): a key point of an edge of the moving occurrence (start, end, middle or centre)
    /// onto a key point of another edge, or onto a planar face, a plane or a straight edge. The start and end are those of
    /// the edge as se_geometry lists them (verified in Solid Edge 2026).
    /// </summary>
    private static object Connect(SeContext context, RelationSpec spec)
    {
        string key1 = spec.Point1 ?? throw ToolException.InvalidArgument("A connect relation needs 'point1': start, end, mid or center of the edge 'element1'.");
        Occurrence moving = OccurrenceOf(context, spec.Occurrence1, spec.Element1, "occurrence1", "element1")
            ?? throw ToolException.InvalidArgument("'occurrence1' is required (the occurrence that moves), unless 'element1' is an edge handle from se_geometry with 'source' = that occurrence.");
        AssemblyDocument assembly = moving.TopLevelDocument;
        Occurrence? target = OccurrenceOf(context, spec.Occurrence2, spec.Element2, "occurrence2", "element2");
        if (target is not null && target.OccurrenceID == moving.OccurrenceID)
        {
            throw ToolException.InvalidArgument("'occurrence1' and 'occurrence2' must be different occurrences.");
        }

        Side first = Resolve(context, assembly, moving, spec.Element1, "element1");
        Side second = Resolve(context, assembly, target, spec.Element2, "element2");
        double Gap()
        {
            double[] point = KeyPointOf(first, key1, "element1");
            if (spec.Point2 is { } key2)
            {
                return Geo.Length(Minus(point, KeyPointOf(second, key2, "element2")));
            }

            if (second.Element is SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Line })
            {
                (double[] start, double[] direction) = AxisOf(second, "element2");
                return Geo.Length(Geo.Cross(Minus(point, start), direction));
            }

            if (second.Element is SolidEdgeGeometry.Edge)
            {
                throw ToolException.InvalidArgument("Without 'point2', 'element2' must be a planar face, a plane or a straight edge.",
                    "To meet a point of a curved edge, give 'point2' (start, end, mid or center).");
            }

            (double[] root, double[] normal) = PlaneOf(second, "element2");
            return Math.Abs(Dot(Minus(point, root), normal));
        }

        Gap(); // checks both elements before touching the assembly
        return Position(context, assembly, moving, "connect", (relations, created) =>
        {
            created.Add(spec.Point2 is { } key2
                ? relations.AddPoint(first.Reference, KeyPoint(key1), second.Reference, KeyPoint(key2))
                : relations.AddPoint(first.Reference, KeyPoint(key1), second.Reference));
            RequireSolved(created);
            double gap = Gap();
            if (gap > DistanceTolerance)
            {
                throw new ToolException("relation_failed", $"After the connect relation the point is {Geo.Mm(gap)} mm away from 'element2'.",
                    "Check that the occurrence is not held by other relations (se_relations list with 'occurrence').");
            }
        }, target);
    }

    private static Relation3dGeometryConstants KeyPoint(string key) => key.ToLowerInvariant() switch
    {
        "start" => Relation3dGeometryConstants.igRelation3dStartPoint,
        "end" => Relation3dGeometryConstants.igRelation3dEndPoint,
        "mid" => Relation3dGeometryConstants.igRelation3dMidPoint,
        "center" => Relation3dGeometryConstants.igRelation3dCenterPoint,
        _ => throw ToolException.InvalidArgument($"Unknown key point '{key}'.", "Use start, end, mid or center."),
    };

    /// <summary>Start, end, middle or centre of an edge (m, assembly coordinates).</summary>
    private static double[] KeyPointOf(Side side, string key, string argumentName)
    {
        if (side.Element is not SolidEdgeGeometry.Edge edge)
        {
            throw ToolException.InvalidArgument($"'{argumentName}' must be an edge to take its {key} point.",
                "Edges: se_geometry action 'edges' with 'source' = the occurrence.");
        }

        double[] point;
        switch (KeyPoint(key))
        {
            case Relation3dGeometryConstants.igRelation3dStartPoint or Relation3dGeometryConstants.igRelation3dEndPoint:
                Array start = new double[3];
                Array end = new double[3];
                edge.GetEndPoints(ref start, ref end);
                point = [.. Geo.Doubles(KeyPoint(key) == Relation3dGeometryConstants.igRelation3dStartPoint ? start : end)];
                break;
            case Relation3dGeometryConstants.igRelation3dMidPoint:
                if (edge.Geometry is not (SolidEdgeGeometry.Line or SolidEdgeGeometry.Circle))
                {
                    throw ToolException.InvalidArgument($"'{argumentName}': 'mid' needs a straight or circular edge.", "Use start or end for other curves.");
                }

                edge.GetParamExtents(out double min, out double max);
                Array parameters = new[] { (min + max) / 2 };
                Array points = new double[3];
                edge.GetPointAtParam(1, ref parameters, ref points);
                point = [.. Geo.Doubles(points)];
                break;
            default:
                if (edge.Geometry is not SolidEdgeGeometry.Circle circle)
                {
                    throw ToolException.InvalidArgument($"'{argumentName}' has no centre: 'center' needs a circular edge.");
                }

                Array center = new double[3];
                Array axis = new double[3];
                circle.GetCircleData(ref center, ref axis, out _);
                point = [.. Geo.Doubles(center)];
                break;
        }

        Placement? placement = side.Occurrence is null ? null : Placement.Of(side.Occurrence);
        return placement?.Point(point) ?? point;
    }

    /// <summary>
    /// Coordinate system relation: a coordinate system of the moving occurrence's part ends on one of another occurrence's
    /// part or of the assembly, origins and axes together (verified in Solid Edge 2026).
    /// </summary>
    private static object CoordinateSystems(SeContext context, RelationSpec spec)
    {
        Occurrence moving = AssemblyService.Occurrence(context, spec.Occurrence1, "occurrence1");
        AssemblyDocument assembly = moving.TopLevelDocument;
        Occurrence? target = string.IsNullOrWhiteSpace(spec.Occurrence2) ? null : AssemblyService.Occurrence(context, spec.Occurrence2, "occurrence2");
        if (target is not null && target.OccurrenceID == moving.OccurrenceID)
        {
            throw ToolException.InvalidArgument("'occurrence1' and 'occurrence2' must be different occurrences.");
        }

        SolidEdgePart.CoordinateSystem mine = SystemOf(context, moving.OccurrenceDocument, spec.Element1, "element1", $"'{moving.Name}'");
        SolidEdgePart.CoordinateSystem theirs = target is null
            ? SystemOf(context, assembly, spec.Element2, "element2", "the assembly")
            : SystemOf(context, target.OccurrenceDocument, spec.Element2, "element2", $"'{target.Name}'");
        return Position(context, assembly, moving, "coordinate_system", (relations, created) =>
        {
            object second = target is null ? theirs : assembly.CreateReference(target, theirs);
            created.Add(relations.AddCoordinateSystem(assembly.CreateReference(moving, mine), second));
            RequireSolved(created);
            (double[] o1, double[] x1, double[] z1) = FrameOf(mine, moving);
            (double[] o2, double[] x2, double[] z2) = FrameOf(theirs, target);
            double gap = Geo.Length(Minus(o1, o2));
            bool turned = Dot(x1, x2) < ParallelCosine || Dot(z1, z2) < ParallelCosine;
            if (gap > DistanceTolerance || turned)
            {
                throw new ToolException("relation_failed",
                    $"After the coordinate system relation the origins are {Geo.Mm(gap)} mm apart{(turned ? " and the axes differ" : "")}.",
                    "Check that the occurrence is not held by other relations (se_relations list with 'occurrence').");
            }
        }, target);
    }

    /// <summary>Origin, X and Z axes of a coordinate system in assembly coordinates (m).</summary>
    private static (double[] Origin, double[] X, double[] Z) FrameOf(SolidEdgePart.CoordinateSystem system, Occurrence? owner)
    {
        (double[] origin, double[] x, _, double[] z) = RefPlaneService.Axes(system);
        if (owner is null)
        {
            return (origin, Unit(x), Unit(z));
        }

        Placement placement = Placement.Of(owner);
        return (placement.Point(origin), Unit(placement.Vector(x)), Unit(placement.Vector(z)));
    }

    /// <summary>A coordinate system of a document by name (any case) or handle; the error lists the names there are.</summary>
    private static SolidEdgePart.CoordinateSystem SystemOf(SeContext context, object document, string? element, string argumentName, string owner)
    {
        List<SolidEdgePart.CoordinateSystem> systems = Late.TryGet(document, "CoordinateSystems") is { } collection
            ? [.. Late.Items(collection).OfType<SolidEdgePart.CoordinateSystem>()]
            : [];
        string names = systems.Count == 0 ? "none" : string.Join(", ", systems.Select(s => s.Name));
        if (string.IsNullOrWhiteSpace(element))
        {
            throw ToolException.InvalidArgument($"'{argumentName}' is required: a coordinate system of {owner} ({names}).");
        }

        SolidEdgePart.CoordinateSystem? found = HandlePattern().IsMatch(element.Trim())
            ? context.Handles.Resolve<SolidEdgePart.CoordinateSystem>(element.Trim(), argumentName)
            : systems.FirstOrDefault(s => string.Equals(s.Name, element.Trim(), StringComparison.OrdinalIgnoreCase));
        return found is not null && systems.Any(s => Late.SameObject(s, found))
            ? found
            : throw ToolException.NotFound($"{owner} has no coordinate system '{element}'.", $"Its coordinate systems: {names}.");
    }

    /// <summary>
    /// Gear relation: element2 turns, or moves along a straight edge (rack and pinion), as element1 turns. Verified in Solid
    /// Edge 2026: nothing moves when it is added; both turn the same way unless flip is set; the distance of a rack is
    /// given in metres per the turns of element1; and an occurrence held by an axial relation with its rotation locked does
    /// not turn (lockRotation false on that relation frees it).
    /// </summary>
    private static object Gear(SeContext context, RelationSpec spec)
    {
        if (spec.Ratio is not { Length: 2 } ratio || ratio.Any(v => !double.IsFinite(v) || v <= 0))
        {
            throw ToolException.InvalidArgument("A gear relation needs 'ratio': two numbers > 0, the teeth of element1 and element2, or with ratioBy 'turns' the turns of element1 and the turns of element2 (or the mm a rack moves).");
        }

        string by = (spec.RatioBy ?? "teeth").ToLowerInvariant();
        if (by is not ("teeth" or "turns"))
        {
            throw ToolException.InvalidArgument($"Unknown 'ratioBy' '{spec.RatioBy}'.", "Use teeth or turns.");
        }

        Occurrence first = OccurrenceOf(context, spec.Occurrence1, spec.Element1, "occurrence1", "element1")
            ?? throw ToolException.InvalidArgument("'occurrence1' is required (the gear that drives), unless 'element1' is a face or edge handle from se_geometry with 'source' = that occurrence.");
        AssemblyDocument assembly = first.TopLevelDocument;
        Occurrence second = OccurrenceOf(context, spec.Occurrence2, spec.Element2, "occurrence2", "element2")
            ?? throw ToolException.InvalidArgument("'occurrence2' is required (the gear or rack that is driven), unless 'element2' is a face or edge handle read from it.");
        if (second.OccurrenceID == first.OccurrenceID)
        {
            throw ToolException.InvalidArgument("'occurrence1' and 'occurrence2' must be different occurrences.");
        }

        Side driver = Resolve(context, assembly, first, spec.Element1, "element1");
        Side driven = Resolve(context, assembly, second, spec.Element2, "element2");
        if (!Turns(driver))
        {
            throw ToolException.InvalidArgument("'element1' must be what turns: a cylindrical or conical face or a circular edge of the driving gear.",
                "For a rack and pinion, element1 is the pinion and element2 a straight edge of the rack along its travel.");
        }

        bool rack = !Turns(driven);
        if (rack && driven.Element is not SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Line })
        {
            throw ToolException.InvalidArgument("'element2' must be a cylindrical or conical face or a circular edge (a gear), or a straight edge of a rack along its travel.");
        }

        if (rack && by != "turns")
        {
            throw ToolException.InvalidArgument("For a rack (a straight edge in 'element2') use ratioBy 'turns': ratio = [turns of element1, mm the rack moves].");
        }

        double value2 = rack ? Units.MmToM(ratio[1]) : ratio[1];
        string[] held = [.. new[] { first, second }.Where(o => AssemblyService.StatusName(o) is "fully_constrained" or "fixed").Select(o => o.Name)];
        GearRelation3d? relation = null;
        try
        {
            relation = assembly.Relations3d.AddGear(driver.Reference, driven.Reference,
                rack ? Relation3dGearTypeConstants.igRelation3dGearTypeRotaryLinear : Relation3dGearTypeConstants.igRelation3dGearTypeRotaryRotary,
                by == "turns" ? Relation3dGearRatioTypeConstants.igRelation3dGearRatioTypeNumberOfTurns : Relation3dGearRatioTypeConstants.igRelation3dGearRatioTypeNumberOfTeeth,
                ratio[0], value2, spec.Flip);
            RequireSolved([relation]);
            if (Math.Abs(relation.RatioValue1 - ratio[0]) > 1e-9 || Math.Abs(relation.RatioValue2 - value2) > 1e-9)
            {
                throw new ToolException("relation_failed", string.Create(CultureInfo.InvariantCulture,
                    $"Solid Edge kept the ratio {relation.RatioValue1}:{relation.RatioValue2} instead of {ratio[0]}:{value2}."),
                    "Check the ratio and the elements.");
            }
        }
        catch (Exception ex) when (Rejected(ex))
        {
            if (relation is not null)
            {
                TryDelete(relation);
            }

            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("relation_failed", $"Solid Edge rejected the gear relation: {ComErrors.Unwrap(ex).Message}",
                "Use cylindrical or conical faces or circular edges for gears, and a straight edge for a rack.", ex);
        }

        HashSet<int> grounded = AssemblyService.GroundedIds(assembly);
        return new
        {
            relations = new[] { Describe(context, assembly, relation, []) },
            occurrences = new[] { AssemblyService.Describe(context, assembly, first, grounded), AssemblyService.Describe(context, assembly, second, grounded) },
            note = held.Length > 0
                ? $"{string.Join(" and ", held)} could not move before the gear relation, so the gears do not turn: free the rotation (lockRotation false on the axial relation) or remove what holds it."
                : null,
        };
    }

    /// <summary>A cylindrical or conical face or a circular edge: something that turns about an axis.</summary>
    private static bool Turns(Side side) => side.Element is Face { Geometry: SolidEdgeGeometry.Cylinder or SolidEdgeGeometry.Cone }
        or SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Circle };

    [GeneratedRegex(@"^[a-z_0-9]+:\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    /// <summary>
    /// Locks or frees the rotation of an axial relation about its axis. Verified in Solid Edge 2026: AddAxial locks it, so a
    /// part held by an axial and a planar relation cannot turn (a gear relation then does not move it).
    /// </summary>
    private static void LockRotation(AxialRelation3d relation, bool locked)
    {
        relation.FixedRotate = locked;
        if (relation.FixedRotate != locked)
        {
            throw new ToolException("relation_failed", $"Solid Edge kept the rotation of the axial relation {(locked ? "free" : "locked")}.",
                "Check the relations that hold the occurrence (se_relations list with 'occurrence').");
        }
    }

    /// <summary>Axis, direction and radius of a cylindrical face (m, assembly coordinates).</summary>
    private static (double[] Point, double[] Direction, double Radius) CylinderOf(Side side, string argumentName)
    {
        if (side.Element is not Face { Geometry: SolidEdgeGeometry.Cylinder cylinder })
        {
            throw ToolException.InvalidArgument($"'{argumentName}' must be a cylindrical face for a tangent relation.");
        }

        Array point = new double[3];
        Array direction = new double[3];
        cylinder.GetCylinderData(ref point, ref direction, out double radius);
        double[] p = Geo.Doubles(point).ToArray();
        double[] d = Geo.Doubles(direction).ToArray();
        Placement? placement = side.Occurrence is null ? null : Placement.Of(side.Occurrence);
        return (placement?.Point(p) ?? p, Unit(placement?.Vector(d) ?? d), radius);
    }

    /// <summary>Given occurrence or, if missing, the one of the element's handle (faces and edges read from an occurrence).</summary>
    private static Occurrence? OccurrenceOf(SeContext context, string? occurrence, string? element, string occurrenceName, string elementName)
    {
        if (!string.IsNullOrWhiteSpace(occurrence))
        {
            return AssemblyService.Occurrence(context, occurrence, occurrenceName);
        }

        return !string.IsNullOrWhiteSpace(element) && !int.TryParse(element, out _)
            && context.Handles.Resolve(element, elementName) is OccurrenceElement qualified
            ? qualified.Occurrence
            : null;
    }

    private static Side Resolve(SeContext context, AssemblyDocument assembly, Occurrence? occurrence, string? element, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(element))
        {
            throw ToolException.InvalidArgument($"'{argumentName}' is required: a face or reference plane handle"
                + (argumentName.StartsWith("element", StringComparison.Ordinal) && occurrence is null ? ", or '1'-'3' for the assembly base planes." : "."),
                "Get faces with se_geometry action 'faces' and 'source' = the occurrence handle (coordinates are assembly coordinates).");
        }

        if (occurrence is null)
        {
            object plane = int.TryParse(element, out int index)
                ? Late.Call(assembly.AsmRefPlanes, "Item", index) ?? throw ToolException.NotFound($"The assembly has no plane {index}.")
                : context.Handles.Resolve(element, argumentName);
            return plane is AsmRefPlane
                ? new Side(plane, plane, null)
                : throw ToolException.InvalidArgument($"Without its occurrence, '{argumentName}' must be an assembly reference plane ('1'-'3' or a handle).");
        }

        object value = context.Handles.Resolve(element, argumentName);
        if (value is OccurrenceElement qualified)
        {
            if (qualified.Occurrence.OccurrenceID != occurrence.OccurrenceID)
            {
                throw ToolException.InvalidArgument($"'{argumentName}' is a face or edge of another occurrence ('{qualified.Occurrence.Name}', not '{occurrence.Name}').",
                    "Get the faces with se_geometry 'source' = the occurrence you relate, or omit the occurrence to use the element's own.");
            }

            value = qualified.Element;
        }

        if (value is not (Face or RefPlane or SolidEdgeGeometry.Edge))
        {
            throw ToolException.InvalidArgument($"'{argumentName}' must be a face, an edge or a reference plane of the occurrence's part.");
        }

        object reference = assembly.CreateReference(occurrence, value);
        return new Side(value, reference, occurrence);
    }

    /// <summary>Point and outward normal of a plane (m, assembly coordinates).</summary>
    private static (double[] Point, double[] Normal) PlaneOf(Side side, string argumentName)
    {
        Array root = new double[3];
        Array normal = new double[3];
        double[] n;
        switch (side.Element)
        {
            case Face { Geometry: SolidEdgeGeometry.Plane plane } face:
                plane.GetRootPoint(ref root);
                n = InspectService.FaceNormal(face);
                break;
            case RefPlane refPlane:
                refPlane.GetRootPoint(ref root);
                refPlane.GetNormal(ref normal);
                n = Geo.Doubles(normal).ToArray();
                break;
            case AsmRefPlane asmPlane:
                asmPlane.GetRootPoint(ref root);
                asmPlane.GetNormal(ref normal);
                n = Geo.Doubles(normal).ToArray();
                break;
            default:
                throw ToolException.InvalidArgument($"'{argumentName}' must be a planar face or a reference plane for this relation.");
        }

        double[] point = Geo.Doubles(root).ToArray();
        Placement? placement = side.Occurrence is null ? null : Placement.Of(side.Occurrence);
        return (placement?.Point(point) ?? point, Unit(placement?.Vector(n) ?? n));
    }

    /// <summary>Point and direction of the axis of a cylinder, a cone, a straight or circular edge (m, assembly).</summary>
    private static (double[] Point, double[] Direction) AxisOf(Side side, string argumentName)
    {
        Array point = new double[3];
        Array direction = new double[3];
        switch (side.Element)
        {
            case Face { Geometry: SolidEdgeGeometry.Cylinder cylinder }:
                cylinder.GetCylinderData(ref point, ref direction, out _);
                break;
            case Face { Geometry: SolidEdgeGeometry.Cone cone }:
                cone.GetConeData(ref point, ref direction, out _, out _, out _);
                break;
            case SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Line line }:
                line.GetLineData(ref point, ref direction);
                break;
            case SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Circle circle }:
                circle.GetCircleData(ref point, ref direction, out _);
                break;
            default:
                throw ToolException.InvalidArgument($"'{argumentName}' must be a cylindrical or conical face, or a straight or circular edge, for this relation.");
        }

        double[] p = Geo.Doubles(point).ToArray();
        double[] d = Geo.Doubles(direction).ToArray();
        Placement? placement = side.Occurrence is null ? null : Placement.Of(side.Occurrence);
        return (placement?.Point(p) ?? p, Unit(placement?.Vector(d) ?? d));
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, object relation, Occurrence[] members)
    {
        if (members.Length == 0)
        {
            members = Members(relation);
        }

        var info = new Dictionary<string, object?>
        {
            ["handle"] = context.Register(relation, "relation", (SolidEdgeDocument)assembly),
            ["type"] = TypeName(relation),
            ["status"] = DetailedStatus(relation),
            ["occurrences"] = members.Select(m => m.Name).ToArray(),
        };
        if (Late.TryGet(relation, "Offset") is double offset)
        {
            info["offset"] = Geo.Mm(offset);
        }

        if (relation is AxialRelation3d axial)
        {
            if (axial.ParallelOffset)
            {
                info["parallel"] = true;
                info["fixedOffset"] = axial.FixedParallelOffset;
            }
            else
            {
                info["rotationLocked"] = axial.FixedRotate;
            }
        }

        if (relation is GearRelation3d gear)
        {
            try
            {
                // A rack (a straight edge) moves a distance, kept in metres by Solid Edge: shown in mm.
                bool rack = Late.TryGet(gear.GetElement2(out _), "Object") is SolidEdgeGeometry.Edge { Geometry: SolidEdgeGeometry.Line };
                info["ratio"] = new[] { gear.RatioValue1, rack ? Geo.Mm(gear.RatioValue2) : gear.RatioValue2 };
                if (rack)
                {
                    info["rack"] = true;
                }
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
            {
                // A suppressed gear relation: its ratio cannot be read.
            }
        }

        if (Late.TryGet(relation, "Angle") is double angle)
        {
            info["angle"] = Math.Round(Units.RadToDeg(angle), 6);
        }

        if (Late.TryGet(relation, "Suppress") is true)
        {
            info["suppressed"] = true;
        }

        return info;
    }

    /// <summary>
    /// Occurrences of the relation; suppressed ones cannot be read and are skipped. Center-plane and coordinate system
    /// relations have no Occurrence1/2: their elements are references whose Parent is the occurrence (verified).
    /// </summary>
    private static Occurrence[] Members(object relation) => (relation switch
        {
            GroundRelation3d => new[] { Late.TryGet(relation, "Occurrence") },
            RigidSetRelation3d => (Late.TryGet(relation, "Occurrences") as Array)?.Cast<object?>().ToArray() ?? [],
            CenterPlaneRelation3d plane => Parents(() => [plane.GetElement1(out _), plane.GetElement2(out _), plane.GetElement3(out _), plane.GetElement4(out _)]),
            CoordinateSystemRelation3d systems => Parents(() => [systems.GetElement1(out _), systems.GetElement2(out _)]),
            _ => new[] { Late.TryGet(relation, "Occurrence1"), Late.TryGet(relation, "Occurrence2") },
        })
        .OfType<Occurrence>().Where(o => Late.TryGet(o, "OccurrenceID") is not null)
        .DistinctBy(o => o.OccurrenceID).ToArray();

    private static object? ParentOf(object? element) => element is null ? null : Late.TryGet(element, "Parent");

    /// <summary>Occurrences of the elements of a relation; none when they cannot be read (a suppressed relation).</summary>
    private static object?[] Parents(Func<object?[]> elements)
    {
        try
        {
            return [.. elements().Select(ParentOf)];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    private static string TypeName(object relation) => relation switch
    {
        GroundRelation3d => "ground",
        PlanarRelation3d => "planar",
        AxialRelation3d => "axial",
        AngularRelation3d => "angle",
        TangentRelation3d => "tangent",
        PointRelation3d => "connect",
        RigidSetRelation3d => "rigid_set",
        CenterPlaneRelation3d => "center_plane",
        CoordinateSystemRelation3d => "coordinate_system",
        GearRelation3d => "gear",
        CamFollowerRelation3d => "cam",
        PathRelation3d => "path",
        _ => (InteropCatalog.GetTypeName(relation) ?? "relation").Split('.')[^1],
    };

    /// <summary>Status of the relation. Through IDispatch it arrives as an integer, not as the enumeration.</summary>
    private static string DetailedStatus(object relation) => (Late.TryGet(relation, "DetailedStatus") is { } value
        ? (Relation3dDetailedStatusConstants)Convert.ToInt32(value, CultureInfo.InvariantCulture)
        : Relation3dDetailedStatusConstants.igRelation3dDetailedStatusUnknown) switch
    {
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusSolved => "solved",
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusSuppressed => "suppressed",
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusBetweenSetMembers => "between_rigid_set_members",
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusBetweenFixed => "between_fixed_occurrences",
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusUnsatisfied => "unsatisfied",
        Relation3dDetailedStatusConstants.igRelation3dDetailedStatusMissingGeometry => "missing_geometry",
        _ => "unknown",
    };

    private static void TryDelete(object relation)
    {
        try
        {
            Late.Call(relation, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // No longer exists.
        }
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Minus(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Unit(double[] v)
    {
        double length = Geo.Length(v);
        return length < 1e-15 ? v : [v[0] / length, v[1] / length, v[2] / length];
    }

    /// <summary>Resolved element: the part (or assembly) object, what is passed to the relation, and its occurrence.</summary>
    private sealed record Side(object Element, object Reference, Occurrence? Occurrence);
}
