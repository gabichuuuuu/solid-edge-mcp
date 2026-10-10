using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Operaciones de pieza en modelado ordenado. Cada operación se verifica: si Solid Edge la marca
/// como fallida o no cambia el volumen como se espera, se borra y se devuelve un error accionable.
/// Las operaciones por boceto usan una copia enlazada de su perfil (como «Seleccionar del boceto»):
/// si la operación se borra, el boceto se conserva para reintentar.
/// </summary>
public static partial class FeatureService
{
    /// <summary>Tolerancia de volumen (m³) para decidir si una operación cambió el sólido: 1e-6 mm³.</summary>
    private const double VolumeTolerance = 1e-15;

    /// <summary>Tolerancia de área (m²) para los cambios que no alteran el volumen: 1e-6 mm².</summary>
    private const double AreaTolerance = 1e-12;

    public static readonly IReadOnlyList<string> Directions = ["normal", "reverse", "symmetric"];

    public static object Extrude(SeContext context, string? documentHandle, string sketch, string operation, string extent,
        string direction, double? distance)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        bool cut = IsCut(operation);
        bool finite = extent switch
        {
            "finite" => true,
            "through_all" or "through_next" => false,
            _ => throw ToolException.InvalidArgument($"Unknown extent '{extent}'.", "Use finite, through_all or through_next."),
        };
        double depth = finite
            ? Units.MmToM(Units.RequirePositive(distance ?? throw ToolException.InvalidArgument("Extent 'finite' requires 'distance' (mm)."), "distance"))
            : 0;
        FeaturePropertyConstants side = Side(direction, finite);
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        Model? existing = model.TryModel();
        IncludedProfile included = Include(model, sketchProfile);
        Array profiles = new object[] { included.Profile };

        object feature = Create(cut ? "extruded cutout" : "extruded protrusion", included, () =>
        {
            if (cut)
            {
                ExtrudedCutouts cutouts = (existing ?? model.RequireModel()).ExtrudedCutouts;
                return extent switch
                {
                    "finite" => cutouts.AddFiniteMulti(1, ref profiles, side, depth),
                    "through_all" => cutouts.AddThroughAllMulti(1, ref profiles, side),
                    _ => cutouts.AddThroughNextMulti(1, ref profiles, side),
                };
            }

            if (existing is null)
            {
                if (!finite)
                {
                    throw ToolException.InvalidArgument("The first feature of a part needs a finite extent.", "Use extent 'finite' with a distance.");
                }

                Model created = model.Models.AddFiniteExtrudedProtrusion(1, ref profiles, side, depth);
                return created.ExtrudedProtrusions.Item(created.ExtrudedProtrusions.Count);
            }

            ExtrudedProtrusions protrusions = existing.ExtrudedProtrusions;
            return extent switch
            {
                "finite" => protrusions.AddFiniteMulti(1, ref profiles, side, depth),
                "through_all" => protrusions.AddThroughAllMulti(1, ref profiles, side),
                _ => protrusions.AddThroughNextMulti(1, ref profiles, side),
            };
        });

        object result = Verify(context, model, feature, before, cut ? -1 : 1, switched, sketch, included);
        Hide(sketchProfile);
        Hide(included.Profile);
        return result;
    }

    public static object Revolve(SeContext context, string? documentHandle, string sketch, string? axis, string operation,
        double angle, string direction)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        RefAxis refAxis = context.Handles.Resolve<RefAxis>(
            axis ?? throw ToolException.InvalidArgument("se_revolve requires 'axis'.", "Create it with se_sketch action 'axis' on a line of the sketch, before closing the sketch."),
            "axis");
        bool cut = IsCut(operation);
        double radians = Units.DegToRad(Units.RequirePositive(angle, "angle"));
        if (radians > 2 * Math.PI + 1e-9)
        {
            throw ToolException.InvalidArgument("'angle' must be at most 360 degrees.");
        }

        FeaturePropertyConstants side = Side(direction, finite: true);
        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        Model? existing = model.TryModel();
        IncludedProfile included = Include(model, sketchProfile);
        Array profiles = new object[] { included.Profile };

        object feature = Create(cut ? "revolved cutout" : "revolved protrusion", included, () =>
        {
            if (cut)
            {
                return (existing ?? model.RequireModel()).RevolvedCutouts.AddFiniteMulti(1, ref profiles, refAxis, side, radians);
            }

            if (existing is null)
            {
                Model created = model.Models.AddFiniteRevolvedProtrusion(1, ref profiles, refAxis, side, radians);
                return created.RevolvedProtrusions.Item(created.RevolvedProtrusions.Count);
            }

            return existing.RevolvedProtrusions.AddFiniteMulti(1, ref profiles, refAxis, side, radians);
        });

        object result = Verify(context, model, feature, before, cut ? -1 : 1, switched, sketch, included);
        Hide(sketchProfile);
        Hide(included.Profile);
        return result;
    }

    /// <summary>Redondeo de radio constante en las aristas indicadas.</summary>
    public static object Round(SeContext context, string? documentHandle, string[]? edges, double? radius)
    {
        (ModelDocument model, object[] edgeObjects) = ResolveEdges(context, documentHandle, edges);
        double r = Units.MmToM(Units.RequirePositive(radius ?? throw ToolException.InvalidArgument("se_round requires 'radius' (mm)."), "radius"));
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        double[] radii = Enumerable.Repeat(r, edgeObjects.Length).ToArray();
        object feature = Create("round", () => target.Rounds.Add(edgeObjects.Length, edgeObjects, radii));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Chaflán de retranqueo igual en las aristas indicadas.</summary>
    public static object Chamfer(SeContext context, string? documentHandle, string[]? edges, double? distance)
    {
        (ModelDocument model, object[] edgeObjects) = ResolveEdges(context, documentHandle, edges);
        double d = Units.MmToM(Units.RequirePositive(distance ?? throw ToolException.InvalidArgument("se_chamfer requires 'distance' (mm)."), "distance"));
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("chamfer", () => target.Chamfers.AddEqualSetback(edgeObjects.Length, edgeObjects, d));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Suprimir, reactivar, borrar o renombrar una operación.</summary>
    public static object Edit(SeContext context, string? documentHandle, string featureHandle, string action, string? name)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, featureHandle);
        object feature = context.Handles.Resolve(featureHandle, "feature");
        string label = Late.TryGetString(feature, "Name") ?? featureHandle;
        double before = model.Volume();
        try
        {
            switch (action)
            {
                case "suppress":
                case "unsuppress":
                    Late.Set(feature, "Suppress", action == "suppress");
                    break;
                case "delete":
                    Late.Call(feature, "Delete");
                    context.Handles.Remove(featureHandle, "the feature was deleted");
                    break;
                case "rename":
                    Late.Set(feature, "Name", string.IsNullOrWhiteSpace(name)
                        ? throw ToolException.InvalidArgument("Action 'rename' requires 'name'.")
                        : name);
                    break;
                default:
                    throw ToolException.InvalidArgument($"Unknown action '{action}'.", "Use suppress, unsuppress, delete or rename.");
            }
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is MissingMemberException
            || ComErrors.Unwrap(ex) is COMException { HResult: ComErrors.DispEMemberNotFound or ComErrors.DispEUnknownName })
        {
            throw new ToolException("not_supported", $"'{label}' does not support '{action}'.", "Use se_com_describe on the handle to see what it supports.");
        }

        if (action != "rename")
        {
            context.ModelChanged(model.Document, $"the model changed ({action} {label})");
        }

        double after = model.Volume();
        return new
        {
            feature = action == "delete" ? null : Describe(context, model, feature),
            deleted = action == "delete" ? label : null,
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
        };
    }

    /// <summary>Árbol de operaciones del documento (barra de operaciones), en orden.</summary>
    public static object Tree(SeContext context, string? documentHandle, int offset, int limit)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        EdgebarFeatures features = model.EdgebarFeatures;
        int count = features.Count;
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object>();
        for (int i = Math.Max(offset, 0) + 1; i <= count && items.Count < take; i++)
        {
            if (features.Item(i) is { } item)
            {
                items.Add(Describe(context, model, item, i));
            }
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            modelingMode = model.ModelingMode,
            count,
            offset = Math.Max(offset, 0),
            features = items,
            volume = Geo.Mm3(model.Volume()),
        };
    }

    public static object Describe(SeContext context, ModelDocument model, object feature, int? index = null)
    {
        string type = InteropCatalog.GetTypeName(feature) ?? "object";
        string shortType = type[(type.LastIndexOf('.') + 1)..];
        string kind = shortType switch
        {
            "RefPlane" => "refplane",
            "Profile" or "ProfileSet" or "Sketch" => "sketch",
            _ => "feature",
        };
        return new
        {
            index,
            handle = context.Register(feature, kind, model.Document),
            name = Late.TryGetString(feature, "Name"),
            type = shortType,
            status = StatusOf(feature),
            suppressed = Late.TryGet(feature, "Suppress") as bool?,
        };
    }

    public static string? StatusOf(object feature)
    {
        object? value = Late.TryGet(feature, "Status");
        if (value is null)
        {
            return null;
        }

        int code = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return code switch
        {
            (int)FeatureStatusConstants.igFeatureOK => "ok",
            (int)FeatureStatusConstants.igFeatureFailed => "failed",
            (int)FeatureStatusConstants.igFeatureWarned => "warning",
            (int)FeatureStatusConstants.igFeatureSuppressed => "suppressed",
            (int)FeatureStatusConstants.igFeatureRolledBack => "rolled_back",
            _ => code.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static bool IsCut(string operation) => operation switch
    {
        "add" => false,
        "cut" => true,
        _ => throw ToolException.InvalidArgument($"Unknown operation '{operation}'.", "Use 'add' (protrusion) or 'cut' (cutout)."),
    };

    /// <summary>Lado respecto del plano del boceto: normal = sentido de la normal del boceto.</summary>
    private static FeaturePropertyConstants Side(string direction, bool finite) => direction switch
    {
        "normal" => FeaturePropertyConstants.igRight,
        "reverse" => FeaturePropertyConstants.igLeft,
        "symmetric" => finite ? FeaturePropertyConstants.igSymmetric : FeaturePropertyConstants.igBoth,
        _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", $"Use one of: {string.Join(", ", Directions)}."),
    };

    private static (ModelDocument Model, object[] Edges) ResolveEdges(SeContext context, string? documentHandle, string[]? edges)
    {
        if (edges is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'edges' must list at least one edge handle.", "Get edge handles with se_geometry action 'edges'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, edges[0]);
        object[] resolved = edges.Select(e => (object)context.Handles.Resolve<SolidEdgeGeometry.Edge>(e, "edges")).ToArray();
        return (model, resolved);
    }

    private static object Create(string what, Func<object> create) => Create(what, null, create);

    /// <summary>Crea la operación; si Solid Edge la rechaza, borra la copia del perfil y da un error accionable.</summary>
    private static object Create(string what, IncludedProfile? included, Func<object> create)
    {
        try
        {
            return create();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsDisconnected(com))
        {
            // Las llamadas por IDispatch (Late) llegan envueltas en TargetInvocationException.
            if (included is not null)
            {
                DeleteIncluded(included);
            }

            throw new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message}",
                "Check that the sketch is closed (se_sketch 'close'), the direction and extent, and that the geometry intersects the part. The sketch is kept: call the tool again with it.", ex);
        }
    }

    /// <summary>Copia enlazada del perfil del boceto para una operación (equivale a «Seleccionar del boceto»).</summary>
    private static IncludedProfile Include(ModelDocument model, Profile sketchProfile)
    {
        try
        {
            Profile profile = model.ProfileSets.CreateIncludedProfile(sketchProfile);
            return new IncludedProfile(profile, Late.TryGet(profile, "Parent"));
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("sketch_not_usable", $"The sketch cannot be used for a feature: {ex.Message}",
                "Close it first with se_sketch action 'close' (it must form closed loops).", ex);
        }
    }

    /// <summary>
    /// Borra el conjunto de la copia del perfil. Al borrar una operación fallida, Solid Edge borra el perfil
    /// pero deja su conjunto vacío; por eso se guarda la referencia al conjunto al crear la copia.
    /// </summary>
    private static void DeleteIncluded(IncludedProfile included)
    {
        try
        {
            Late.Call(included.Set ?? included.Profile, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Ya no existe.
        }
    }

    /// <summary>Copia enlazada del perfil de un boceto y el conjunto (ProfileSet) que la contiene.</summary>
    private sealed record IncludedProfile(Profile Profile, object? Set);

    /// <summary>Verifica la operación con <see cref="Check"/> y describe el resultado.</summary>
    private static object Verify(SeContext context, ModelDocument model, object feature, double before, int expectedSign, bool switched,
        string? sketch = null, IncludedProfile? included = null, string? direction = null, Func<bool>? changedOtherwise = null)
    {
        (double after, string? status) = Check(context, model, feature, before, expectedSign, sketch, included, changedOtherwise);
        return new
        {
            feature = Describe(context, model, feature),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            direction,
            warnings = Warnings(switched, status == "warning"),
        };
    }

    /// <summary>
    /// Comprueba el estado y el cambio de volumen; si la operación falló o no tuvo efecto, la borra y lanza
    /// <c>feature_failed</c>. <paramref name="expectedSign"/>: +1 añade material, -1 lo quita, 0 cualquier cambio;
    /// con 0, <paramref name="changedOtherwise"/> detecta cambios que no alteran el volumen (p. ej. un desmoldeo
    /// con el plano neutro en medio de la cara). Con <paramref name="undoneByCaller"/> no se borra: quien llama deshace
    /// (en síncrono, borrar una operación no devuelve las caras que recortó).
    /// </summary>
    private static (double After, string? Status) Check(SeContext context, ModelDocument model, object feature, double before, int expectedSign,
        string? sketch, IncludedProfile? included, Func<bool>? changedOtherwise = null, bool undoneByCaller = false)
    {
        context.ModelChanged(model.Document, "the model changed (new feature)");
        string name = Late.TryGetString(feature, "Name") ?? "The feature";
        string? status = StatusOf(feature);
        double after = model.Volume();
        double delta = after - before;
        string? problem = status == "failed"
            ? "Solid Edge reports it as failed"
            : expectedSign > 0 && delta <= VolumeTolerance ? "it did not add material"
            : expectedSign < 0 && delta >= -VolumeTolerance ? "it did not remove material"
            : expectedSign == 0 && Math.Abs(delta) <= VolumeTolerance && changedOtherwise?.Invoke() != true ? "it did not change the solid"
            : null;
        if (problem is not null)
        {
            if (!undoneByCaller)
            {
                TryDelete(feature);
                if (included is not null)
                {
                    DeleteIncluded(included);
                }
            }

            string kept = sketch is null ? "" : $" The sketch '{sketch}' is kept: call the tool again with it.";
            throw new ToolException("feature_failed", $"{name} was {(undoneByCaller ? "undone" : "deleted")} because {problem}.{kept}",
                "Check that the geometry meets the part (sketch x/y are measured from the frame origin returned by se_sketch 'create', which on a face is not the model origin), the direction ('normal' follows the sketch normal; try another one) and the extent.");
        }

        return (after, status);
    }

    private static List<string>? Warnings(bool switched, bool featureWarning)
    {
        var warnings = new List<string>();
        if (switched)
        {
            warnings.Add("The document was switched to ordered modeling.");
        }

        if (featureWarning)
        {
            warnings.Add("Solid Edge reports a warning on this feature.");
        }

        return warnings.Count == 0 ? null : warnings;
    }

    /// <summary>
    /// Oculta el boceto usado y la copia de su perfil, como hace la interfaz de Solid Edge. Solo tras una
    /// operación correcta: si falla, el boceto sigue visible para corregirlo o reutilizarlo.
    /// </summary>
    private static void Hide(Profile profile)
    {
        try
        {
            profile.Visible = false;
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Solo afecta a la presentación; la operación ya está creada y verificada.
        }
    }

    private static void TryDelete(object feature)
    {
        try
        {
            Late.Call(feature, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Si no se puede borrar, el error ya informa del fallo; la persona puede deshacer.
        }
    }
}
