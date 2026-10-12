// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Common view of the 3D model documents for operations (part and sheet metal), which have the
/// same members in different interfaces.
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

    /// <summary>Model view if the document is a part or a sheet metal part; otherwise <c>null</c>.</summary>
    public static ModelDocument? TryGet(SolidEdgeDocument document) =>
        document is PartDocument or SheetMetalDocument ? From(document) : null;

    /// <summary>Document of the given handle or, if none is given, the one of the related object (sketch, feature...).</summary>
    public static ModelDocument For(SeContext context, string? documentHandle, string relatedHandle) =>
        From(string.IsNullOrWhiteSpace(documentHandle) ? context.DocumentOf(relatedHandle) : context.GetDocument(documentHandle));

    /// <summary>Existing model (design body), or an error if there is no solid feature yet.</summary>
    public Model RequireModel() => TryModel() ?? throw new ToolException("no_model", $"'{Document.Name}' has no solid yet.",
        "Create a base feature first (se_extrude or se_revolve with operation 'add').");

    public Model? TryModel() => Models.Count == 0 ? null : Models.Item(1);

    /// <summary>
    /// Body of the existing model. Solid Edge fails reading it (E_FAIL) once the body was converted to a mesh
    /// (Constructions.ConvertToMeshes, verified), so that case gets an error that says why.
    /// </summary>
    public object? RequireBody()
    {
        Model model = RequireModel();
        try
        {
            return model.Body;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            throw new ToolException("no_brep_body", $"Solid Edge cannot read the body of '{Document.Name}' as faces and edges (it fails this way after the body is converted to a mesh).",
                "Use se_physical for its mass properties. Solid Edge does not undo, suppress or delete the conversion through the API (verified): convert a copy of the part.");
        }
    }

    /// <summary>Volume of the body in m³, or null when Solid Edge cannot read the body (e.g. a mesh).</summary>
    public double? TryVolume()
    {
        try
        {
            return Volume();
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            return null;
        }
    }

    /// <summary>Volume of all the design bodies in m³, or 0 if there is no solid.</summary>
    public double Volume() => Bodies().Sum(b => b.Body.Volume);

    /// <summary>Outer area of all the design bodies in m² (sum of their faces), or 0 if there is no solid.</summary>
    public double SurfaceArea() => Bodies().Sum(b =>
        Late.Items(b.Body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]).Sum(f => ((SolidEdgeGeometry.Face)f).Area));

    /// <summary>
    /// Design bodies of the document with their model, in order. A split makes one model per body; a body united into
    /// another keeps its model, whose Body then fails with E_FAIL (verified): it is skipped. On the first model the error
    /// is passed on (a body converted to a mesh fails the same way and the callers report it).
    /// </summary>
    public IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> Bodies() =>
        MeasuredModels().Select(m => (m, m.Body as SolidEdgeGeometry.Body)).Where(b => b.Item2 is not null)
            .Select(b => (b.m, b.Item2!)).ToList();

    /// <summary>Design bodies, or null when the first one cannot be read (a body converted to a mesh).</summary>
    public IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)>? TryBodies()
    {
        try
        {
            return Bodies();
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            return null;
        }
    }

    /// <summary>
    /// Models that hold a body: the first one always (also when its body is a mesh, which only physical properties can
    /// measure) and the others while their body can be read (one united into another fails with E_FAIL, verified).
    /// </summary>
    public IReadOnlyList<Model> MeasuredModels()
    {
        var models = new List<Model>();
        int count = Models.Count;
        for (int i = 1; i <= count; i++)
        {
            Model model = Models.Item(i);
            if (i > 1)
            {
                try
                {
                    _ = model.Body;
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
                {
                    continue;
                }
            }

            models.Add(model);
        }

        return models;
    }

    public string ModelingMode
    {
        get
        {
            ModelingModeConstants mode = Document is PartDocument part ? part.ModelingMode : ((SheetMetalDocument)Document).ModelingMode;
            return mode == ModelingModeConstants.seModelingModeOrdered ? "ordered" : "synchronous";
        }
    }

    /// <summary>
    /// The operations of this server are ordered-modelling operations: if the document is in synchronous mode,
    /// it switches to ordered (as a person would). Returns whether it changed.
    /// </summary>
    public bool EnsureOrdered() => SetMode(ModelingModeConstants.seModelingModeOrdered);

    /// <summary>
    /// Switches to synchronous: in Solid Edge 2026 the sheet metal operations on edges (flange, hem, corner
    /// break) are only resolved this way. Returns whether it changed, to switch back later with <see cref="EnsureOrdered"/>.
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

    /// <summary>Plane: index of a plane of the document (1 = first base plane), plane handle or planar face handle.</summary>
    public RefPlane ResolvePlane(SeContext context, string plane, string argumentName = "plane") =>
        PlaneFrom(ResolvePlaneSource(context, plane, argumentName));

    /// <summary>
    /// Reference plane or planar face, without creating the plane yet. On a face a local plane is created,
    /// which belongs to the operation that uses it and disappears with it: each attempt needs its own.
    /// </summary>
    public object ResolvePlaneSource(SeContext context, string plane, string argumentName = "plane")
    {
        if (OrientedPlanes.TryAxis(plane, out int axis))
        {
            return OrientedPlanes.Find(Late.Items(RefPlanes), axis, plane);
        }

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

    /// <summary>Working plane of a source from <see cref="ResolvePlaneSource"/> (local plane if it is a face).</summary>
    public RefPlane PlaneFrom(object source) => source switch
    {
        RefPlane refPlane => refPlane,
        SolidEdgeGeometry.Face face => RefPlanes.AddParallelByDistance(face, 0.0, ReferenceElementConstants.igNormalSide, Local: true),
        _ => throw new ArgumentException("Not a plane source.", nameof(source)),
    };
}
