using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Edge = SolidEdgeGeometry.Edge;
using Face = SolidEdgeGeometry.Face;
using Line = SolidEdgeGeometry.Line;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Brida sobre aristas de la chapa (mm y grados).</summary>
public sealed record FlangeSpec(string[]? Edges, double? Length, double Angle = 90, string Side = "inside", double? Radius = null);

/// <summary>Dobladillo sobre aristas de la chapa (mm).</summary>
public sealed record HemSpec(string[]? Edges, string Type, double? Length, double? Radius = null, double? Length2 = null,
    double? Radius2 = null, string Side = "inside");

/// <summary>Valores globales de la chapa (mm); <c>null</c> = sin cambio.</summary>
public sealed record SheetMetalValues(double? Thickness, double? BendRadius, double? ReliefWidth, double? ReliefDepth, double? NeutralFactor);

/// <summary>Operación de chapa por boceto (mm y grados).</summary>
public sealed record SheetFormSpec(
    string Sketch,
    string Action,
    double? Depth = null,
    double? Height = null,
    double? Width = null,
    double? Angle = null,
    string Direction = "auto",
    string MovingSide = "right",
    string Position = "inside",
    string Side = "inside",
    string BeadType = "u",
    string Method = "thickness");

/// <summary>
/// Chapa. Comprobado en Solid Edge 2026: por API, la brida, el dobladillo y la rotura de esquina solo se resuelven en
/// modelado síncrono (en ordenado Flanges.Add crea una brida que nunca se resuelve) y solo sobre operaciones síncronas;
/// por eso la pestaña, la brida de contorno base y esas operaciones se crean en síncrono y después se vuelve al modo
/// anterior. Las operaciones por boceto (embutido, recortes, desvío, pliegue...) son ordenadas, como el resto de
/// herramientas: en ordenado el cuerpo incluye las operaciones de los dos modos; en síncrono faltan las ordenadas.
/// </summary>
public static partial class FeatureService
{
    public static readonly IReadOnlyList<string> HemTypes = ["closed", "open", "s_flange", "closed_loop"];

    private const string SyncNextStep =
        "Check the edges (se_geometry 'edges'): use straight edges of the sheet faces of synchronous features (tabs, flanges), not thickness edges or edges made by ordered features, and smaller sizes.";

    public static object SheetSettings(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        return DescribeSettings(context, model, Sheet(model));
    }

    /// <summary>Cambia los valores globales de la chapa; el modelo se actualiza con ellos.</summary>
    public static object SetSheetSettings(SeContext context, string? documentHandle, SheetMetalValues values)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        SheetMetalDocument sheet = Sheet(model);
        var changes = new List<(SheetMetalGlobalConstants Parameter, object Value)>();
        if (values.Thickness is { } thickness)
        {
            changes.Add((SheetMetalGlobalConstants.seSheetMetalGlobalMaterialThickness, Units.MmToM(Units.RequirePositive(thickness, "thickness"))));
        }

        if (values.BendRadius is { } bendRadius)
        {
            changes.Add((SheetMetalGlobalConstants.seSheetMetalGlobalBendRadius, Units.MmToM(NonNegative(bendRadius, "bendRadius"))));
        }

        if (values.ReliefWidth is { } reliefWidth)
        {
            changes.Add((SheetMetalGlobalConstants.seSheetMetalGlobalReliefWidth, Units.MmToM(NonNegative(reliefWidth, "reliefWidth"))));
        }

        if (values.ReliefDepth is { } reliefDepth)
        {
            changes.Add((SheetMetalGlobalConstants.seSheetMetalGlobalReliefLength, Units.MmToM(NonNegative(reliefDepth, "reliefDepth"))));
        }

        if (values.NeutralFactor is { } neutralFactor)
        {
            if (!double.IsFinite(neutralFactor) || neutralFactor < 0 || neutralFactor > 1)
            {
                throw ToolException.InvalidArgument("'neutralFactor' must be between 0 and 1 (typically 0.3–0.5).");
            }

            changes.Add((SheetMetalGlobalConstants.seSheetMetalGlobalNeutralFactor, neutralFactor));
        }

        if (changes.Count == 0)
        {
            throw ToolException.InvalidArgument("Action 'set_settings' needs at least one value.",
                "Pass thickness, bendRadius, reliefWidth, reliefDepth or neutralFactor.");
        }

        double before = model.Volume();
        foreach ((SheetMetalGlobalConstants parameter, object value) in changes)
        {
            sheet.SetGlobalParameter(parameter, value);
        }

        // Con operaciones ordenadas el cambio no se aplica hasta recalcular (comprobado: sin Recompute el volumen no varía).
        sheet.Recompute();
        context.ModelChanged(model.Document, "the sheet metal settings changed");
        double after = model.Volume();
        return new { settings = DescribeSettings(context, model, sheet), volume = Geo.Mm3(after), volumeChange = Geo.Mm3(after - before) };
    }

    /// <summary>
    /// Pestaña desde un boceto cerrado de un solo lazo: la primera es la pestaña base (crea la chapa con el espesor del
    /// documento); las siguientes añaden material en el plano de la chapa. En síncrono, AddBaseTab con lazos anidados
    /// toma solo la región interior, de ahí la comprobación del número de lazos.
    /// </summary>
    public static object Tab(SeContext context, string? documentHandle, string sketch, string direction)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Sheet(model);
        Profile profile = SketchService.Resolve(context, sketch);
        FeaturePropertyConstants side = direction switch
        {
            "normal" => FeaturePropertyConstants.igRight,
            "reverse" => FeaturePropertyConstants.igLeft,
            _ => throw ToolException.InvalidArgument($"Direction '{direction}' is not valid for a tab.", "Use normal (thickness along the sketch normal) or reverse."),
        };
        if (SketchService.LoopCount(profile) is int loops && loops > 1)
        {
            throw ToolException.InvalidArgument($"The sketch has {loops} loops; a tab is made from a single closed outline.",
                "Draw only the outline in the sketch; cut holes and openings afterwards with se_sheetmetal_form 'normal_cutout' or se_hole.");
        }

        bool first = model.TryModel() is null;
        object result = Synchronous(context, model, first ? "base tab" : "tab", 1, sketch, [() =>
        {
            if (first)
            {
                return LastFeature(model, model.Models.AddBaseTab(profile, side).Tabs);
            }

            Tabs tabs = model.RequireModel().Tabs;
            tabs.AddTab(profile, side);
            return LastFeature(model, tabs);
        }],
        first
            ? "Check that the sketch is one closed outline (se_sketch 'close')."
            : "Draw the tab on the plane of a sheet face (se_sketch 'create' with that face), touching or overlapping the sheet, and try direction 'reverse'.");
        TryHide(profile);
        return result;
    }

    /// <summary>Brida de contorno base desde un boceto abierto (crea la chapa).</summary>
    public static object ContourFlange(SeContext context, string? documentHandle, string sketch, double? width, string direction,
        string materialSide, double? radius)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        SheetMetalDocument sheet = Sheet(model);
        if (model.TryModel() is not null)
        {
            throw new ToolException("not_supported", $"'{model.Document.Name}' already has a sheet; contour_flange creates the base of a new one.",
                "Add flanges on edges with action 'flange'; a contour flange on an existing edge is available through se_com (Model.ContourFlanges.AddSync).");
        }

        Profile profile = SketchService.Resolve(context, sketch);
        double extent = Units.MmToM(Units.RequirePositive(width ?? throw ToolException.InvalidArgument("contour_flange requires 'width' (mm): the extent along the sketch normal."), "width"));
        // Solid Edge 2026 ignora el lado de proyección (1, 2, 3, 7 y 8 dan el mismo sólido): el ancho crece siempre hacia la
        // normal del boceto.
        if (direction != "normal")
        {
            throw ToolException.InvalidArgument("Solid Edge always extends a base contour flange along the sketch normal.",
                "Use direction 'normal' and draw the sketch on a plane whose normal points the way the width should grow (se_refplane, or the opposite face).");
        }

        // Comprobado con una U recorrida desde su primer punto y vista desde el lado de la normal del boceto: igRight pone
        // el espesor a la izquierda del recorrido (al contrario que el lado móvil de desvíos y pliegues).
        FeaturePropertyConstants thicknessSide = materialSide switch
        {
            "right" => FeaturePropertyConstants.igLeft,
            "left" => FeaturePropertyConstants.igRight,
            _ => throw ToolException.InvalidArgument($"Unknown materialSide '{materialSide}'.", "Use left or right."),
        };
        double bend = BendRadius(sheet, radius);
        // En síncrono la brida de contorno base queda como una pestaña y una brida por cada tramo más (no en ContourFlanges).
        HashSet<string> before = FeatureNames(model);
        List<object> parts = [];
        object result = Synchronous(context, model, "contour flange", 1, sketch, [() =>
        {
            Model created = model.Models.AddBaseContourFlange(profile, (int)thicknessSide, (int)FeaturePropertyConstants.igFinite,
                (int)FeaturePropertyConstants.igRight, extent, bend);
            parts = NewFeatures(model, before);
            return parts.FirstOrDefault() ?? LastFeature(model, created.ContourFlanges);
        }],
        "Check that the sketch is one open chain (se_sketch 'close' with open=true) and that the bend radius fits its corners.",
        () => parts);
        TryHide(profile);
        return result;
    }

    /// <summary>
    /// Una brida por arista. Comprobado: sube de la cara a la que pertenece la arista, alejándose del material; la
    /// longitud se mide desde esa cara; BendAngle es el ángulo entre la brida y la chapa (90 = perpendicular).
    /// ThicknessSide: material dentro (cara exterior de la brida enrasada con la arista), fuera o pliegue fuera.
    /// DimSide no admite ningún valor (E_INVALIDARG con 4, 5, 97 y 98), así que no se ofrece.
    /// </summary>
    public static object Flange(SeContext context, string? documentHandle, FlangeSpec spec)
    {
        (ModelDocument model, object[] edges) = ResolveEdges(context, documentHandle, spec.Edges);
        RequireSheet(model);
        double length = Units.MmToM(Units.RequirePositive(spec.Length ?? throw ToolException.InvalidArgument("A flange requires 'length' (mm)."), "length"));
        double angle = Units.RequireFinite(spec.Angle, "angle");
        if (angle <= 0 || angle >= 180)
        {
            throw ToolException.InvalidArgument("'angle' must be greater than 0 and less than 180 degrees (90 = perpendicular).");
        }

        FlangeFeatureConstants side = spec.Side switch
        {
            "inside" => FlangeFeatureConstants.seFlangeMaterialInside,
            "outside" => FlangeFeatureConstants.seFlangeMaterialOutside,
            "bend_outside" => FlangeFeatureConstants.seFlangeBendOutside,
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}'.", "Use inside, outside or bend_outside."),
        };
        object radius = spec.Radius is { } r ? Units.MmToM(Units.RequirePositive(r, "radius")) : Type.Missing;
        double bendAngle = Units.DegToRad(angle);
        return Synchronous(context, model, "flange", 1, null,
            PerEdge(model, edges, edge => model.RequireModel().Flanges.AddSync(edge, length,
                ThicknessSide: (int)side, InsideRadius: radius, BendAngle: bendAngle)),
            SyncNextStep);
    }

    /// <summary>
    /// Un dobladillo por arista. Los tipos rizo (curl) y bucle centrado provocan RPC_E_SERVERFAULT en Solid Edge 2026 y el
    /// bucle abierto falla, así que no se ofrecen. Los opcionales con tipo se omiten por IDispatch (la interop no admite
    /// omitirlos) para que Solid Edge aplique sus valores por defecto.
    /// </summary>
    public static object Hem(SeContext context, string? documentHandle, HemSpec spec)
    {
        (ModelDocument model, object[] edges) = ResolveEdges(context, documentHandle, spec.Edges);
        SheetMetalDocument sheet = RequireSheet(model);
        HemFeatureConstants type = spec.Type switch
        {
            "closed" => HemFeatureConstants.seHemTypeClosed,
            "open" => HemFeatureConstants.seHemTypeOpen,
            "s_flange" => HemFeatureConstants.seHemTypeSFlange,
            "closed_loop" => HemFeatureConstants.seHemTypeClosedLoop,
            _ => throw ToolException.InvalidArgument($"Unknown hem type '{spec.Type}'.", $"Use one of: {string.Join(", ", HemTypes)}."),
        };
        HemFeatureConstants side = spec.Side switch
        {
            "inside" => HemFeatureConstants.seHemSideMaterialInside,
            "outside" => HemFeatureConstants.seHemSideMaterialOutside,
            "bend_outside" => HemFeatureConstants.seHemBendOutside,
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}'.", "Use inside, outside or bend_outside."),
        };
        double length = Units.MmToM(Units.RequirePositive(spec.Length ?? throw ToolException.InvalidArgument("A hem requires 'length' (mm): the length of the folded flange."), "length"));
        double radius = BendRadius(sheet, spec.Radius);
        bool sFlange = type == HemFeatureConstants.seHemTypeSFlange;
        object radius2 = sFlange ? spec.Radius2 is { } r2 ? Units.MmToM(Units.RequirePositive(r2, "radius2")) : radius : Type.Missing;
        object length2 = sFlange ? spec.Length2 is { } l2 ? Units.MmToM(Units.RequirePositive(l2, "length2")) : length : Type.Missing;
        return Synchronous(context, model, "hem", 1, null,
            PerEdge(model, edges, edge => Late.Call(model.RequireModel().Hems, "Add", edge, (int)type, radius, length,
                (int)side, Type.Missing, Type.Missing, radius2, length2)),
            SyncNextStep);
    }

    /// <summary>Redondeo o chaflán (45°) de esquinas en aristas que atraviesan el espesor.</summary>
    public static object BreakCorner(SeContext context, string? documentHandle, string[]? edges, string type, double? size)
    {
        (ModelDocument model, object[] edgeObjects) = ResolveEdges(context, documentHandle, edges);
        RequireSheet(model);
        FeaturePropertyConstants kind = type switch
        {
            "round" => FeaturePropertyConstants.igConstRadiusRound,
            "chamfer" => FeaturePropertyConstants.igChamfer45degSetback,
            _ => throw ToolException.InvalidArgument($"Unknown corner type '{type}'.", "Use round or chamfer."),
        };
        double setback = Units.MmToM(Units.RequirePositive(size ?? throw ToolException.InvalidArgument("break_corner requires 'size' (mm): radius or chamfer setback."), "size"));
        return Synchronous(context, model, "break corner", 0, null,
            [() => model.RequireModel().BreakCorners.Add(kind, edgeObjects.Length, edgeObjects, setback)],
            "Use the corner edges that go through the thickness (se_geometry 'edges' with direction = the sheet normal) of synchronous features, and a smaller size.");
    }

    /// <summary>
    /// Crea o actualiza el desplegado y devuelve su tamaño de corte. FlatPatterns.Add exige caras, aristas y vértices del
    /// cuerpo del modelo de desplegado (con los del diseño da E_INVALIDARG): se toma su cara plana mayor y su arista
    /// recta más larga.
    /// </summary>
    public static object Flatten(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        SheetMetalDocument sheet = RequireSheet(model);
        bool switched = model.EnsureOrdered();
        Model design = model.RequireModel();
        FlatPatternModels flatModels = sheet.FlatPatternModels;
        FlatPatternModel flat = flatModels.Count > 0 ? flatModels.Item(1) : flatModels.Add(design);
        FlatPatterns patterns = flat.FlatPatterns;
        bool created = patterns.Count == 0;
        try
        {
            if (created)
            {
                var body = (Body)flat.Body;
                Face face = Late.Items(body.Faces[TopologyQuery.igQueryPlane]).Cast<Face>().MaxBy(f => f.Area)
                    ?? throw new ToolException("flatten_failed", "The sheet has no planar face to lay flat.");
                Edge edge = Late.Items(face.Edges).Cast<Edge>().Where(e => e.Geometry is Line).MaxBy(EdgeLength)
                    ?? throw new ToolException("flatten_failed", "The base face has no straight edge to align the flat pattern.");
                patterns.Add(edge, face, edge.StartVertex, 0);
            }
            else if (!flat.IsUpToDate)
            {
                flat.Update();
            }

            flat.UpdateCutSize();
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("flatten_failed", $"Solid Edge could not flatten the sheet: {ex.Message}",
                "Check the feature tree (se_feature_tree) for failed features; formed features that cannot unfold make the flat pattern fail.", ex);
        }

        flat.GetCutSize(out double x, out double y);
        return new
        {
            document = context.DocumentHandle(model.Document),
            created,
            upToDate = flat.IsUpToDate,
            cutSize = new { x = Geo.Mm(x), y = Geo.Mm(y) },
            note = "The flat pattern follows the design. Export it with se_export flatPattern=true (.dxf or .dwg).",
            warnings = Warnings(switched, false),
        };
    }

    /// <summary>Operaciones por boceto de chapa, en modelado ordenado con copia enlazada del perfil.</summary>
    public static object SheetForm(SeContext context, string? documentHandle, SheetFormSpec spec)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Sketch);
        RequireSheet(model);
        Profile sketchProfile = SketchService.Resolve(context, spec.Sketch);
        bool normal = spec.Direction switch
        {
            "auto" or "normal" => true,
            "reverse" => false,
            _ => throw ToolException.InvalidArgument($"Unknown direction '{spec.Direction}'.", "Use auto, normal or reverse."),
        };
        string direction = normal ? "normal" : "reverse";
        switch (spec.Action)
        {
            case "dimple":
            {
                double depth = Required(spec.Depth, "depth", "dimple");
                return Form(context, model, sketchProfile, spec.Sketch, "dimple", 0, direction, (target, profile) => target.Dimples.Add(profile, depth,
                    DimpleFeatureConstants.seDimpleProfileLeft, normal ? DimpleFeatureConstants.seDimpleDepthRight : DimpleFeatureConstants.seDimpleDepthLeft));
            }

            case "drawn_cutout":
            {
                double depth = Required(spec.Depth, "depth", "drawn_cutout");
                return Form(context, model, sketchProfile, spec.Sketch, "drawn cutout", 0, direction, (target, profile) => target.DrawnCutouts.Add(profile, depth,
                    DrawnCutoutFeatureConstants.seDrawnCutoutProfileLeft,
                    normal ? DrawnCutoutFeatureConstants.seDrawnCutoutDepthRight : DrawnCutoutFeatureConstants.seDrawnCutoutDepthLeft,
                    DrawnCutoutFeatureConstants.seDrawnCutoutMaterialInside));
            }

            case "normal_cutout":
                return NormalCutout(context, model, sketchProfile, spec.Sketch, spec.Direction, spec.Method);
            case "etch":
                // Un grabado no cambia el volumen: basta con que Solid Edge no lo marque como fallido.
                return Form(context, model, sketchProfile, spec.Sketch, "etch", 0, null, (target, profile) => target.Etches.Add(profile),
                    changedOtherwise: () => true);
            case "jog":
            {
                double height = Required(spec.Height, "height", "jog");
                JogFeatureConstants material = spec.Side switch
                {
                    "inside" => JogFeatureConstants.seJogMaterialInside,
                    "outside" => JogFeatureConstants.seJogMaterialOutside,
                    "bend_outside" => JogFeatureConstants.seJogMaterialBendOutside,
                    _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}'.", "Use inside, outside or bend_outside."),
                };
                JogFeatureConstants moving = MovingSide(spec.MovingSide) ? JogFeatureConstants.seJogMoveRight : JogFeatureConstants.seJogMoveLeft;
                return Form(context, model, sketchProfile, spec.Sketch, "jog", 0, direction, (target, profile) => target.Jogs.AddFinite(profile, height,
                    material, moving, normal ? JogFeatureConstants.seJogNormal : JogFeatureConstants.seJogReverseNormal));
            }

            case "bend":
            {
                double angle = Units.RequirePositive(spec.Angle ?? throw ToolException.InvalidArgument("bend requires 'angle' (degrees)."), "angle");
                if (angle >= 180)
                {
                    throw ToolException.InvalidArgument("'angle' must be less than 180 degrees.");
                }

                BendFeatureConstants position = spec.Position switch
                {
                    "inside" => BendFeatureConstants.seBendPZLInside,
                    "outside" => BendFeatureConstants.seBendPZLOutside,
                    "symmetric" => BendFeatureConstants.seBendPZLSymmetric,
                    _ => throw ToolException.InvalidArgument($"Unknown position '{spec.Position}'.", "Use inside, outside or symmetric."),
                };
                BendFeatureConstants moving = MovingSide(spec.MovingSide) ? BendFeatureConstants.seBendMoveRight : BendFeatureConstants.seBendMoveLeft;
                return Form(context, model, sketchProfile, spec.Sketch, "bend", 0, direction, (target, profile) => target.Bends.Add(profile,
                    Units.DegToRad(angle), position, moving, normal ? BendFeatureConstants.seBendNormal : BendFeatureConstants.seBendReverseNormal));
            }

            case "bead":
            {
                double height = Required(spec.Height, "height", "bead");
                double width = Required(spec.Width, "width", "bead");
                FeaturePropertyConstants shape = spec.BeadType switch
                {
                    "u" => FeaturePropertyConstants.igUShaped,
                    "v" => FeaturePropertyConstants.igVShaped,
                    _ => throw ToolException.InvalidArgument($"Unknown bead type '{spec.BeadType}'.", "Use u or v (the circular bead has no effect in Solid Edge 2026)."),
                };
                // Combinación comprobada (altura 3, anchura 8): radio de forma = anchura/4, punzón y matriz = min/3,
                // conicidad 0,1 rad, sin redondeo, extremo con lanceta (el extremo punzonado da E_FAIL).
                double formRadius = width / 4;
                double toolRadius = Math.Min(width, height) / 3;
                return Form(context, model, sketchProfile, spec.Sketch, "bead", 0, direction, (target, profile) =>
                {
                    Array profiles = new object[] { profile };
                    return target.Beads.Add(1, ref profiles, shape, height, width, 0.1, formRadius, toolRadius, toolRadius,
                        FeaturePropertyConstants.igNoRound,
                        normal ? FeaturePropertyConstants.igNormalSideDummy : FeaturePropertyConstants.igReverseNormalSideDummy,
                        FeaturePropertyConstants.igLancedEnd, 0.0);
                });
            }

            default:
                throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.",
                    "Use dimple, drawn_cutout, normal_cutout, etch, jog, bend or bead.");
        }
    }

    /// <summary>
    /// Recorte normal a la chapa en todo su espesor. Con dirección «auto» corta hacia el material si el boceto está sobre
    /// una cara de la chapa; si no, prueba hacia la normal del boceto y, si no quita material, hacia el otro lado (el lado
    /// sin material no da error ni efecto). No se usa igBoth: deja el cuerpo ilegible (E_FAIL) en Solid Edge 2026.
    /// </summary>
    private static object NormalCutout(SeContext context, ModelDocument model, Profile sketchProfile, string sketch, string direction, string method)
    {
        FeaturePropertyConstants how = method switch
        {
            "thickness" => FeaturePropertyConstants.igThickness,
            "mid_plane" => FeaturePropertyConstants.igSMMidPlaneCutout,
            "clearance" => FeaturePropertyConstants.igSMClearanceCutout,
            _ => throw ToolException.InvalidArgument($"Unknown method '{method}'.", "Use thickness, mid_plane or clearance."),
        };
        FeaturePropertyConstants[] sides = direction switch
        {
            "auto" => TowardsMaterial(model, sketchProfile) is { } side ? [side] : [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            "normal" => [FeaturePropertyConstants.igRight],
            "reverse" => [FeaturePropertyConstants.igLeft],
            _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", "Use auto, normal or reverse."),
        };
        for (int i = 0; ; i++)
        {
            FeaturePropertyConstants side = sides[i];
            try
            {
                return Form(context, model, sketchProfile, sketch, "normal cutout", -1, side == FeaturePropertyConstants.igRight ? "normal" : "reverse",
                    (target, profile) =>
                    {
                        Array profiles = new object[] { profile };
                        return target.NormalCutouts.AddThroughAllMulti(1, ref profiles, side, how);
                    });
            }
            catch (ToolException ex) when (ex.Code == "feature_failed" && i + 1 < sides.Length)
            {
                // «auto»: el material está al otro lado del plano del boceto.
            }
        }
    }

    /// <summary>
    /// Lado del plano del boceto hacia el material cuando el plano coincide con una cara plana del cuerpo (el material queda
    /// al lado contrario de su normal exterior); <c>null</c> si no coincide con ninguna. Evita un intento fallido, que
    /// cuesta una operación creada y borrada.
    /// </summary>
    private static FeaturePropertyConstants? TowardsMaterial(ModelDocument model, Profile profile)
    {
        double[] origin = To3d(profile, 0, 0);
        double[] normal = Geo.Cross(Minus(To3d(profile, 1, 0), origin), Minus(To3d(profile, 0, 1), origin));
        double size = Geo.Length(normal);
        normal = normal.Select(c => c / size).ToArray();
        var body = (Body)model.RequireModel().Body;
        foreach (Face face in Late.Items(body.Faces[TopologyQuery.igQueryPlane]).Cast<Face>())
        {
            if (face.Geometry is not SolidEdgeGeometry.Plane plane)
            {
                continue;
            }

            Array root = new double[3];
            Array planeNormal = new double[3];
            plane.GetPlaneData(ref root, ref planeNormal);
            double cosine = Dot(InspectService.FaceNormal(face), normal);
            if (Math.Abs(Math.Abs(cosine) - 1) < 1e-6 && Math.Abs(Dot(Minus(Geo.Doubles(root).ToArray(), origin), normal)) < 1e-7)
            {
                return cosine > 0 ? FeaturePropertyConstants.igLeft : FeaturePropertyConstants.igRight;
            }
        }

        return null;
    }

    private static double[] To3d(Profile profile, double x, double y)
    {
        profile.Convert2DCoordinate(x, y, out double x3, out double y3, out double z3);
        return [x3, y3, z3];
    }

    private static double[] Minus(double[] a, double[] b) => a.Zip(b, (p, q) => p - q).ToArray();

    private static double Dot(double[] a, double[] b) => a.Zip(b, (p, q) => p * q).Sum();

    /// <summary>Operación ordenada por boceto: copia enlazada del perfil, verificación y ocultación del boceto si va bien.</summary>
    private static object Form(SeContext context, ModelDocument model, Profile sketchProfile, string sketch, string what, int expectedSign,
        string? direction, Func<Model, Profile, object> create, Func<bool>? changedOtherwise = null)
    {
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        IncludedProfile included = Include(model, sketchProfile);
        object feature = Create(what, included, () => create(target, included.Profile));
        object result = Verify(context, model, feature, before, expectedSign, switched, sketch, included, direction, changedOtherwise);
        Hide(sketchProfile);
        Hide(included.Profile);
        return result;
    }

    /// <summary>
    /// Una operación por arista. Cada operación síncrona regenera las aristas del cuerpo (las anteriores dan E_FAIL):
    /// desde la segunda, la arista se vuelve a buscar por su recta, guardada antes de cambiar nada.
    /// </summary>
    private static List<Func<object?>> PerEdge(ModelDocument model, object[] edges, Func<object, object?> create)
    {
        EdgeLine?[] lines = edges.Select(LineOf).ToArray();
        return edges.Select((edge, i) => (Func<object?>)(() =>
            create(i == 0 || lines[i] is not { } line ? edge : FindEdge(model, line)))).ToList();
    }

    /// <summary>Extremos (m) de una arista recta.</summary>
    private sealed record EdgeLine(double[] Start, double[] End);

    private static EdgeLine? LineOf(object edge)
    {
        if (edge is not Edge { Geometry: Line } straight)
        {
            return null;
        }

        Array start = new double[3];
        Array end = new double[3];
        straight.GetEndPoints(ref start, ref end);
        return new EdgeLine(Geo.Doubles(start).ToArray(), Geo.Doubles(end).ToArray());
    }

    /// <summary>Arista recta del cuerpo actual sobre la misma recta y con el mayor solape (la operación anterior pudo acortarla).</summary>
    private static object FindEdge(ModelDocument model, EdgeLine line)
    {
        double[] axis = line.End.Zip(line.Start, (a, b) => a - b).ToArray();
        double length = Geo.Length(axis);
        double[] unit = axis.Select(c => c / length).ToArray();
        double Along(double[] p) => p.Zip(line.Start, (a, b) => a - b).Zip(unit, (a, b) => a * b).Sum();
        bool OnLine(double[] p)
        {
            double t = Along(p);
            return Geo.Length(p.Select((c, k) => c - line.Start[k] - t * unit[k]).ToArray()) < 1e-7;
        }

        object? best = null;
        double bestOverlap = 1e-6;
        var body = (Body)model.RequireModel().Body;
        foreach (Edge edge in Late.Items(body.Edges[TopologyQuery.igQueryAll]).Cast<Edge>())
        {
            if (LineOf(edge) is { } other && OnLine(other.Start) && OnLine(other.End))
            {
                double a = Along(other.Start);
                double b = Along(other.End);
                double overlap = Math.Min(Math.Max(a, b), length) - Math.Max(Math.Min(a, b), 0);
                if (overlap > bestOverlap)
                {
                    best = edge;
                    bestOverlap = overlap;
                }
            }
        }

        return best ?? throw new ToolException("feature_failed", "One of the edges no longer exists after the previous features of this call.",
            "Call the tool again with fresh edge handles from se_geometry 'edges'.");
    }

    /// <summary>
    /// Crea operaciones en síncrono, cada una verificada con el volumen medido en síncrono antes y después, y vuelve a
    /// ordenado si el documento lo estaba. Si una falla, se deshace todo lo hecho en la llamada con Undo (cada cambio de
    /// modo y cada operación es una transacción): en síncrono, borrar una brida no devuelve la placa que recortó.
    /// </summary>
    private static object Synchronous(SeContext context, ModelDocument model, string what, int expectedSign, string? sketch,
        IReadOnlyList<Func<object?>> steps, string nextStep, Func<IReadOnlyList<object>>? parts = null)
    {
        int undoStart = UndoCount(model);
        bool switched = model.EnsureSynchronous();
        var created = new List<object>();
        double start;
        double current;
        bool warning = false;
        int index = 0;
        try
        {
            start = current = model.Volume();
            for (; index < steps.Count; index++)
            {
                Func<object?> step = steps[index];
                object feature = Create(what, () => step() ?? throw new ToolException("feature_failed", $"Solid Edge did not create the {what}.", nextStep));
                created.Add(feature);
                (current, string? status) = Check(context, model, feature, current, expectedSign, sketch, null, undoneByCaller: true);
                warning |= status == "warning";
            }
        }
        catch (ToolException ex) when (ex.Code == "feature_failed")
        {
            if (!UndoSince(model, undoStart))
            {
                foreach (object feature in created)
                {
                    TryDelete(feature);
                }
            }

            context.ModelChanged(model.Document, "the model changed (call undone)");
            string undone = steps.Count > 1
                ? $"{Capitalize(what)} {index + 1} of {steps.Count} failed; everything made in this call was undone. "
                : "";
            throw new ToolException(ex.Code, undone + ex.Message, nextStep, ex);
        }
        finally
        {
            if (switched)
            {
                RestoreOrdered(model);
            }
        }

        double final = model.Volume();
        var warnings = new List<string>();
        if (warning)
        {
            warnings.Add("Solid Edge reports a warning on this feature.");
        }

        if (switched && model.ModelingMode != "ordered")
        {
            warnings.Add("The document could not return to ordered modeling.");
        }

        IReadOnlyList<object> all = parts?.Invoke() is { Count: > 0 } split ? split : created;
        return new
        {
            feature = Describe(context, model, created[0]),
            features = all.Count > 1 ? all.Select(f => Describe(context, model, f)).ToList() : null,
            volume = Geo.Mm3(final),
            volumeChange = Geo.Mm3(current - start),
            modelingMode = model.ModelingMode,
            warnings = warnings.Count == 0 ? null : warnings,
        };
    }

    /// <summary>Transacciones que se pueden deshacer, o -1 si el documento no lo indica.</summary>
    private static int UndoCount(ModelDocument model)
    {
        try
        {
            return Convert.ToInt32(Late.Call(model.Document, "UndoTransactionCount"), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return -1;
        }
    }

    /// <summary>
    /// Deshace las transacciones posteriores a <paramref name="start"/>. Solo cuenta las añadidas desde entonces, así que
    /// nunca deshace trabajo anterior (con la pila llena puede quedarse corto). Devuelve si deshizo algo.
    /// </summary>
    private static bool UndoSince(ModelDocument model, int start)
    {
        int count = UndoCount(model);
        if (start < 0 || count <= start)
        {
            return false;
        }

        try
        {
            Late.Call(model.Document, "Undo", count - start);
            return true;
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return false;
        }
    }

    private static void RestoreOrdered(ModelDocument model)
    {
        try
        {
            model.EnsureOrdered();
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // El resultado informa del modo en que quedó el documento.
        }
    }

    /// <summary>
    /// Última operación de la colección o, si no la recoge (la brida de contorno base no aparece en ContourFlanges), la
    /// última de la barra de operaciones que no sea un boceto ni un plano.
    /// </summary>
    private static object? LastFeature(ModelDocument model, object collection)
    {
        int count = Late.Count(collection);
        return count > 0
            ? Late.Call(collection, "Item", count)
            : Late.Items(model.EdgebarFeatures).Reverse().FirstOrDefault(IsSolidFeature);
    }

    /// <summary>Nombres de las operaciones de la barra, para localizar después las que añade una llamada.</summary>
    private static HashSet<string> FeatureNames(ModelDocument model) =>
        Late.Items(model.EdgebarFeatures).Select(f => Late.TryGetString(f, "Name")).OfType<string>().ToHashSet(StringComparer.Ordinal);

    /// <summary>Operaciones de la barra (sin bocetos ni planos) que no estaban en <paramref name="before"/>.</summary>
    private static List<object> NewFeatures(ModelDocument model, HashSet<string> before) =>
        Late.Items(model.EdgebarFeatures).Where(f => IsSolidFeature(f) && Late.TryGetString(f, "Name") is { } name && !before.Contains(name)).ToList();

    private static bool IsSolidFeature(object feature) => InteropCatalog.GetTypeName(feature) is not { } type
        || !(type.EndsWith(".Sketch", StringComparison.Ordinal) || type.EndsWith(".Profile", StringComparison.Ordinal)
            || type.EndsWith(".ProfileSet", StringComparison.Ordinal) || type.EndsWith(".RefPlane", StringComparison.Ordinal));

    private static SheetMetalDocument Sheet(ModelDocument model) => model.Document as SheetMetalDocument
        ?? throw new ToolException("wrong_document_type", $"'{model.Document.Name}' is not a sheet metal document.",
            "Create one with se_documents action 'new' and kind 'sheet_metal', or open a .psm file.");

    /// <summary>Documento de chapa que ya tiene la chapa creada.</summary>
    private static SheetMetalDocument RequireSheet(ModelDocument model)
    {
        SheetMetalDocument sheet = Sheet(model);
        if (model.TryModel() is null)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no sheet yet.",
                "Create it first with se_sheetmetal action 'tab' (closed sketch) or 'contour_flange' (open sketch).");
        }

        return sheet;
    }

    private static object DescribeSettings(SeContext context, ModelDocument model, SheetMetalDocument sheet) => new
    {
        document = context.DocumentHandle(model.Document),
        thickness = Geo.Mm(GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalMaterialThickness)),
        bendRadius = Geo.Mm(GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalBendRadius)),
        reliefWidth = Geo.Mm(GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalReliefWidth)),
        reliefDepth = Geo.Mm(GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalReliefLength)),
        neutralFactor = Geo.R(GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalNeutralFactor)),
        material = TryGlobal(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalMaterial) as string is { Length: > 0 } name ? name : null,
        modelingMode = model.ModelingMode,
    };

    private static double GlobalNumber(SheetMetalDocument sheet, SheetMetalGlobalConstants parameter)
    {
        sheet.GetGlobalParameter(parameter, out object value);
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static object? TryGlobal(SheetMetalDocument sheet, SheetMetalGlobalConstants parameter)
    {
        try
        {
            sheet.GetGlobalParameter(parameter, out object value);
            return value;
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return null;
        }
    }

    /// <summary>Radio de plegado en metros: el indicado (mm) o el del documento.</summary>
    private static double BendRadius(SheetMetalDocument sheet, double? radius) => radius is { } r
        ? Units.MmToM(Units.RequirePositive(r, "radius"))
        : GlobalNumber(sheet, SheetMetalGlobalConstants.seSheetMetalGlobalBendRadius);

    /// <summary>Lado móvil: <c>true</c> = a la derecha de la línea (dirección de la línea × normal del boceto).</summary>
    private static bool MovingSide(string side) => side switch
    {
        "right" => true,
        "left" => false,
        _ => throw ToolException.InvalidArgument($"Unknown movingSide '{side}'.", "Use right or left."),
    };

    private static double Required(double? value, string name, string action) =>
        Units.MmToM(Units.RequirePositive(value ?? throw ToolException.InvalidArgument($"{action} requires '{name}' (mm)."), name));

    private static double NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw ToolException.InvalidArgument($"'{name}' must be zero or positive (mm).");
        }

        return value;
    }

    private static double EdgeLength(Edge edge)
    {
        edge.GetParamExtents(out double p0, out double p1);
        edge.GetLengthAtParam(p0, p1, out double length);
        return length;
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Oculta el boceto usado; en síncrono Solid Edge puede haberlo absorbido en la operación.</summary>
    private static void TryHide(Profile profile)
    {
        try
        {
            Hide(profile);
        }
        catch (Exception ex) when (ex is InvalidComObjectException || Late.IsMemberFailure(ex))
        {
            // Solo afecta a la presentación.
        }
    }
}
