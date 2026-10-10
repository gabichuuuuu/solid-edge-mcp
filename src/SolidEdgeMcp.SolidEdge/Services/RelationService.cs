using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Relación de conjunto: <paramref name="Occurrence1"/> se mueve hasta <paramref name="Occurrence2"/> (o hasta un
/// plano del conjunto si se omite). Elementos: caras (se_geometry con la ocurrencia como 'source') o planos de la pieza.
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
    bool Flip = false);

/// <summary>
/// Relaciones de conjunto. El sentido de <c>NormalsAligned</c> de Solid Edge no corresponde siempre a «alinear» o
/// «enfrentar» las caras, así que cada relación se comprueba con la geometría resultante en coordenadas del conjunto
/// (normales exteriores y distancia con signo) y se invierte si hace falta, como haría una persona al verla.
/// </summary>
public static class RelationService
{
    public static readonly IReadOnlyList<string> Types = ["planar", "axial", "insert"];

    /// <summary>Tolerancias del resultado: 0,001 mm y coseno de unos 0,08°.</summary>
    private const double DistanceTolerance = 1e-6;
    private const double ParallelCosine = 0.999999;

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
                $"Use one of: {string.Join(", ", Types)}. Other relations (angle, tangent...) are available with se_com_invoke on the document's Relations3d.");
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

        if (spec.Type == "axial" && offset != 0)
        {
            throw ToolException.InvalidArgument("'offset' applies to planar and insert relations, not to axial ones.");
        }

        // Validación previa de la geometría: el error sale antes de tocar el conjunto.
        if (spec.Type == "axial")
        {
            AxisOf(first, "element1");
            AxisOf(second, "element2");
        }
        else
        {
            PlaneOf(first, "element1");
            PlaneOf(second, "element2");
        }

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
                Array none1 = new double[3];
                Array none2 = new double[3];
                planar = relations.AddPlanar(first.Reference, second.Reference, spec.Align, ref none1, ref none2);
                created.Add(planar);
                SetPlanar((PlanarRelation3d)planar, first, second, offset, spec.Align);
            }

            if (spec.Type == "axial")
            {
                AddAxial(relations, first, second, spec.Flip, "element", created);
            }
            else if (spec.Type == "insert")
            {
                // Como el comando Insertar de Solid Edge: plana enfrentada + axial con el giro bloqueado.
                AddAxial(relations, firstAxis!, secondAxis!, spec.Flip, "axis", created).FixedRotate = true;
                if (planar is not null)
                {
                    // El eje puede haber girado la pieza: se vuelve a comprobar la cara.
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
        catch (Exception ex) when (ex is ToolException or COMException && !ComErrors.IsDisconnected(ex))
        {
            foreach (object relation in created)
            {
                TryDelete(relation);
            }

            moving.PutTransform(x, y, z, ax, ay, az);
            if (ungrounded)
            {
                assembly.Relations3d.AddGround(moving);
            }

            // Mover una ocurrencia no cambia sus caras: los handles siguen valiendo (las coordenadas se vuelven a consultar).
            if (ex is ToolException)
            {
                throw;
            }

            throw new ToolException("relation_failed", $"Solid Edge rejected the {spec.Type} relation: {ex.Message}",
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

    /// <summary>Ajusta orientación (enfrentadas o alineadas) y signo del desplazamiento comprobando el resultado.</summary>
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
    /// Relación axial conservando la orientación actual de la pieza (o la contraria con <paramref name="flip"/>). La
    /// relación se añade a <paramref name="created"/> en cuanto existe, para deshacerla si algo falla después.
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

    /// <summary>Elemento de una ocurrencia (con su referencia en el conjunto) o, sin ocurrencia, plano del conjunto.</summary>
    /// <summary>Ocurrencia indicada o, si falta, la del handle del elemento (caras y aristas leídas desde una ocurrencia).</summary>
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

    /// <summary>Punto y normal exterior de un plano (m, coordenadas del conjunto).</summary>
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

    /// <summary>Punto y dirección del eje de un cilindro, un cono, una arista recta o circular (m, conjunto).</summary>
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

        if (Late.TryGet(relation, "Suppress") is true)
        {
            info["suppressed"] = true;
        }

        return info;
    }

    /// <summary>Ocurrencias de la relación; las suprimidas no se pueden leer y se omiten.</summary>
    private static Occurrence[] Members(object relation) => (relation is GroundRelation3d
            ? new[] { Late.TryGet(relation, "Occurrence") }
            : new[] { Late.TryGet(relation, "Occurrence1"), Late.TryGet(relation, "Occurrence2") })
        .OfType<Occurrence>().Where(o => Late.TryGet(o, "OccurrenceID") is not null).ToArray();

    private static string TypeName(object relation) => relation switch
    {
        GroundRelation3d => "ground",
        PlanarRelation3d => "planar",
        AxialRelation3d => "axial",
        AngularRelation3d => "angle",
        TangentRelation3d => "tangent",
        PointRelation3d => "point",
        RigidSetRelation3d => "rigid_set",
        _ => (InteropCatalog.GetTypeName(relation) ?? "relation").Split('.')[^1],
    };

    /// <summary>Estado de la relación. Por IDispatch llega como entero, no como la enumeración.</summary>
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
            // Ya no existe.
        }
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Minus(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Unit(double[] v)
    {
        double length = Geo.Length(v);
        return length < 1e-15 ? v : [v[0] / length, v[1] / length, v[2] / length];
    }

    /// <summary>Elemento resuelto: el objeto de la pieza (o del conjunto), lo que se pasa a la relación y su ocurrencia.</summary>
    private sealed record Side(object Element, object Reference, Occurrence? Occurrence);
}
