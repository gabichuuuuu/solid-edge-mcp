using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Vista común de los documentos con modelo 3D por operaciones (pieza y chapa), que tienen los
/// mismos miembros en interfaces distintas.
/// </summary>
public sealed class ModelDocument
{
    private ModelDocument(SolidEdgeDocument document, string kind)
    {
        Document = document;
        Kind = kind;
    }

    public SolidEdgeDocument Document { get; }

    public string Kind { get; }

    public Models Models => Document is PartDocument part ? part.Models : ((SheetMetalDocument)Document).Models;

    public RefPlanes RefPlanes => Document is PartDocument part ? part.RefPlanes : ((SheetMetalDocument)Document).RefPlanes;

    public ProfileSets ProfileSets => Document is PartDocument part ? part.ProfileSets : ((SheetMetalDocument)Document).ProfileSets;

    public Sketchs Sketches => Document is PartDocument part ? part.Sketches : ((SheetMetalDocument)Document).Sketches;

    public HoleDataCollection HoleData => Document is PartDocument part ? part.HoleDataCollection : ((SheetMetalDocument)Document).HoleDataCollection;

    public EdgebarFeatures EdgebarFeatures =>
        Document is PartDocument part ? part.DesignEdgebarFeatures : ((SheetMetalDocument)Document).DesignEdgebarFeatures;

    public static ModelDocument Get(SeContext context, string? handle) => From(context.GetDocument(handle));

    public static ModelDocument From(SolidEdgeDocument document) => document switch
    {
        PartDocument => new ModelDocument(document, "part"),
        SheetMetalDocument => new ModelDocument(document, "sheet_metal"),
        _ => throw new ToolException("wrong_document_type",
            $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}; this action needs a part or sheet metal document.",
            "Activate or pass a part (.par) or sheet metal (.psm) document."),
    };

    /// <summary>Documento del handle indicado o, si no se indica, el del objeto relacionado (boceto, operación...).</summary>
    public static ModelDocument For(SeContext context, string? documentHandle, string relatedHandle) =>
        From(string.IsNullOrWhiteSpace(documentHandle) ? context.DocumentOf(relatedHandle) : context.GetDocument(documentHandle));

    /// <summary>Modelo (cuerpo de diseño) existente, o error si aún no hay ninguna operación sólida.</summary>
    public Model RequireModel() => TryModel() ?? throw new ToolException("no_model", $"'{Document.Name}' has no solid yet.",
        "Create a base feature first (se_extrude or se_revolve with operation 'add').");

    public Model? TryModel() => Models.Count == 0 ? null : Models.Item(1);

    /// <summary>Volumen del cuerpo en m³, o 0 si no hay sólido.</summary>
    public double Volume() => TryModel()?.Body is SolidEdgeGeometry.Body body ? body.Volume : 0;

    /// <summary>Área exterior del cuerpo en m² (suma de sus caras), o 0 si no hay sólido.</summary>
    public double SurfaceArea() => TryModel()?.Body is SolidEdgeGeometry.Body body
        ? Late.Items(body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]).Sum(f => ((SolidEdgeGeometry.Face)f).Area)
        : 0;

    public string ModelingMode
    {
        get
        {
            ModelingModeConstants mode = Document is PartDocument part ? part.ModelingMode : ((SheetMetalDocument)Document).ModelingMode;
            return mode == ModelingModeConstants.seModelingModeOrdered ? "ordered" : "synchronous";
        }
    }

    /// <summary>
    /// Las operaciones de este servidor son de modelado ordenado: si el documento está en síncrono,
    /// cambia a ordenado (como haría una persona). Devuelve si hubo cambio.
    /// </summary>
    public bool EnsureOrdered() => SetMode(ModelingModeConstants.seModelingModeOrdered);

    /// <summary>
    /// Cambia a síncrono: en Solid Edge 2026 las operaciones de chapa sobre aristas (brida, dobladillo, rotura de
    /// esquina) solo se resuelven así. Devuelve si hubo cambio, para volver después con <see cref="EnsureOrdered"/>.
    /// </summary>
    public bool EnsureSynchronous() => SetMode(ModelingModeConstants.seModelingModeSynchronous);

    private bool SetMode(ModelingModeConstants mode)
    {
        if (ModelingMode == (mode == ModelingModeConstants.seModelingModeOrdered ? "ordered" : "synchronous"))
        {
            return false;
        }

        if (Document is PartDocument part)
        {
            part.ModelingMode = mode;
        }
        else
        {
            ((SheetMetalDocument)Document).ModelingMode = mode;
        }

        return true;
    }

    public void MinimumDistance(object first, object second, out double distance, ref Array point1, ref Array point2)
    {
        if (Document is PartDocument part)
        {
            part.MinimumDistance(first, second, out distance, ref point1, ref point2);
        }
        else
        {
            ((SheetMetalDocument)Document).MinimumDistance(first, second, out distance, ref point1, ref point2);
        }
    }

    /// <summary>Plano: índice de un plano del documento (1 = primer plano base), handle de plano o de cara plana.</summary>
    public RefPlane ResolvePlane(SeContext context, string plane, string argumentName = "plane") =>
        PlaneFrom(ResolvePlaneSource(context, plane, argumentName));

    /// <summary>
    /// Plano de referencia o cara plana sin crear todavía el plano. Sobre una cara se crea un plano local,
    /// que pertenece a la operación que lo usa y desaparece con ella: cada intento necesita el suyo.
    /// </summary>
    public object ResolvePlaneSource(SeContext context, string plane, string argumentName = "plane")
    {
        if (int.TryParse(plane, out int index))
        {
            int count = RefPlanes.Count;
            if (index < 1 || index > count)
            {
                throw ToolException.InvalidArgument($"Reference plane index {index} is out of range (1–{count}).",
                    "List the planes and their normals with se_ref_plane action 'list'.");
            }

            return RefPlanes.Item(index);
        }

        object value = context.Handles.Resolve(plane, argumentName);
        return value switch
        {
            RefPlane or SolidEdgeGeometry.Face { Geometry: SolidEdgeGeometry.Plane } => value,
            SolidEdgeGeometry.Face => throw ToolException.InvalidArgument($"'{plane}' is not a planar face.",
                "Pass a planar face (se_geometry faces with type 'plane') or a reference plane."),
            _ => throw new ToolException("handle_wrong_type", $"'{plane}' in '{argumentName}' is not a reference plane or a planar face."),
        };
    }

    /// <summary>Plano de trabajo de una fuente de <see cref="ResolvePlaneSource"/> (plano local si es una cara).</summary>
    public RefPlane PlaneFrom(object source) => source switch
    {
        RefPlane refPlane => refPlane,
        SolidEdgeGeometry.Face face => RefPlanes.AddParallelByDistance(face, 0.0, ReferenceElementConstants.igNormalSide, Local: true),
        _ => throw new ArgumentException("Not a plane source.", nameof(source)),
    };
}
