// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_pmi does: the action and its inputs. Lengths in mm, angles in degrees.</summary>
public sealed record PmiSpec(string Action, string[]? Dimensions = null, double? Value = null, string Direction = "measure", bool? Locked = null,
    bool? ShowDimensions = null, bool? ShowAnnotations = null);

/// <summary>
/// PMI (3D) dimensions of a part or sheet metal document and the PMI display. Verified in Solid Edge 2026 on se_primitive
/// solids:
/// <list type="bullet">
/// <item>A box carries three linear PMI dimensions (height, width and length: Linear_1 to Linear_3) and a cylinder its
/// height (linear) and diameter (radial diameter). They are driven (seDimStatusDriven, Constraint false): setting Value
/// then changes nothing and Value reads the old value back.</item>
/// <item>Constraint = true locks a dimension (seDimStatusDriving); then SetPMIEditDirection and Value move the faces: the
/// width of a 100 x 60 x 20 box set to 80 gives 160000 mm³; its length set to 120 with seMoveOriginParent moved the face at
/// x = 0 to -20, and set to 140 with seMoveParentsSymmetrically moved both ends by 10. It works in either modelling mode
/// and the change stays when the document goes back to ordered. Unlocked again, the dimension keeps the new value.</item>
/// <item>A locked dimension holds its value in later synchronous edits: a coplanar relation that would have stretched a
/// locked length moved the target face instead.</item>
/// <item>The variable table does not list PMI dimensions: se_variables cannot reach them.</item>
/// <item>New PMI dimensions cannot be placed: Dimensions.AddDistanceBetweenObjects (faces or edges), AddLength and
/// AddDistanceBetweenObjectsEX return nothing, also with PMI.ActivePlane set, and setting Dimensions.Plane fails with
/// E_FAIL.</item>
/// <item>Delete removes a dimension and leaves the model as it is.</item>
/// <item>PMI.ShowDimensions = false hides the dimensions in the window (checked on a capture); PMI.Show reads false while
/// they are shown and setting it to true is ignored, so it is not offered.</item>
/// </list>
/// </summary>
public static class PmiService
{
    public static readonly IReadOnlyList<string> Actions = ["list", "set", "lock", "unlock", "delete", "display", "set_display"];

    /// <summary>Metres (0.0001 mm).</summary>
    private const double LengthTolerance = 1e-7;

    /// <summary>Radians (about 0.00001 degrees).</summary>
    private const double AngleTolerance = 2e-7;

    /// <summary>Cubic metres (0.001 mm³).</summary>
    private const double VolumeTolerance = 1e-12;

    public static object Run(SeContext context, string? documentHandle, PmiSpec spec) => spec.Action switch
    {
        "list" => List(context, documentHandle),
        "set" => Set(context, documentHandle, spec),
        "lock" => Lock(context, documentHandle, spec.Dimensions, locked: true),
        "unlock" => Lock(context, documentHandle, spec.Dimensions, locked: false),
        "delete" => Delete(context, documentHandle, spec.Dimensions),
        "display" => Display(context, documentHandle),
        "set_display" => SetDisplay(context, documentHandle, spec),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", Actions)}."),
    };

    private static object List(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Dimensions dimensions = DimensionsOf(model);
        int count = dimensions.Count;
        return new
        {
            document = context.DocumentHandle(model.Document),
            count,
            dimensions = Enumerable.Range(1, count).Select(i => Describe(context, model, dimensions.Item(i), i)).ToArray(),
        };
    }

    /// <summary>
    /// Drives the model with one dimension: it is locked for the change, then unlocked to read what the model measures
    /// (an unlocked dimension is driven by the geometry); if that is not the value asked for, the old value is put back.
    /// </summary>
    private static object Set(SeContext context, string? documentHandle, PmiSpec spec)
    {
        if (spec.Dimensions is not [string key])
        {
            throw ToolException.InvalidArgument("set changes one dimension: pass 'dimensions' with one index, handle or name from list.");
        }

        double requested = Units.RequireFinite(spec.Value ?? throw ToolException.InvalidArgument("set needs 'value': the new value in mm (degrees for an angle)."), "value");
        PMIEditDirectionConstants direction = spec.Direction switch
        {
            "measure" => PMIEditDirectionConstants.seMoveMeasureParent,
            "origin" => PMIEditDirectionConstants.seMoveOriginParent,
            "symmetric" => PMIEditDirectionConstants.seMoveParentsSymmetrically,
            _ => throw ToolException.InvalidArgument($"Unknown direction '{spec.Direction}'.", "Use measure, origin or symmetric."),
        };
        ModelDocument model = DocumentOf(context, documentHandle, spec.Dimensions);
        Dimensions dimensions = DimensionsOf(model);
        (Dimension dimension, int index) = Find(context, dimensions, key);
        RequireDrivable(dimension, key);
        bool angle = IsAngle(dimension);
        if (!angle && requested <= 0)
        {
            throw ToolException.InvalidArgument($"'value' must be greater than 0 mm; got {requested.ToString(CultureInfo.InvariantCulture)}.");
        }

        double target = angle ? Units.DegToRad(requested) : Units.MmToM(requested);
        double tolerance = angle ? AngleTolerance : LengthTolerance;
        double old = dimension.Value;
        bool wasLocked = dimension.Constraint;
        bool keepLocked = spec.Locked ?? wasLocked;
        double before = model.Volume();
        if (Math.Abs(old - target) <= tolerance)
        {
            SetLocked(dimension, keepLocked);
            return Result(context, model, dimension, index, old, before, model.Volume(), "The dimension already had that value: the model did not change.");
        }

        string? problem = null;
        Exception? inner = null;
        bool restored = false;
        bool switched = model.EnsureSynchronous();
        try
        {
            try
            {
                SetLocked(dimension, true);
                dimension.SetPMIEditDirection(direction);
                dimension.Value = target;
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
            {
                (problem, inner) = ($"Solid Edge rejected it ({com.Message.Trim()})", ex);
            }

            SetLocked(dimension, false);
            double measured = dimension.Value;
            if (problem is null && Math.Abs(measured - target) > tolerance)
            {
                problem = $"the model measures {Text(measured, angle)}";
            }

            if (problem is not null && Math.Abs(measured - old) > tolerance)
            {
                restored = PutBack(dimension, old, tolerance);
            }

            SetLocked(dimension, problem is null ? keepLocked : wasLocked);
        }
        finally
        {
            Restore(model, switched);
        }

        double after = model.Volume();
        context.ModelChanged(model.Document, problem is null ? "a PMI dimension changed" : "a PMI dimension edit failed");
        if (problem is not null)
        {
            string state = restored ? " The old value was put back." : "";
            string changed = Math.Abs(after - before) > VolumeTolerance
                ? $" The volume went from {Geo.Mm3(before)} to {Geo.Mm3(after)} mm³: check the model (se_geometry, se_view)."
                : "";
            throw new ToolException("value_rejected", $"'{DisplayName(dimension)}' did not reach {Text(target, angle)}: {problem}.{state}{changed}",
                "The faces the dimension moves may be held by locked dimensions or relations (se_pmi list, se_face_relation list), or the value may not fit the part; try another value or direction.",
                inner);
        }

        return Result(context, model, dimension, index, old, before, after, null);
    }

    /// <summary>
    /// Locks or unlocks a dimension. Unlocked, a diameter reads its radius until the dimension is updated (verified on a
    /// cylinder: 25 after setting 50, 50 again after UpdateStatus or a recompute), so it is updated every time.
    /// </summary>
    private static void SetLocked(Dimension dimension, bool locked)
    {
        dimension.Constraint = locked;
        dimension.UpdateStatus();
    }

    /// <summary>Sets the old value again (locked for the change); true when the model measures it again.</summary>
    private static bool PutBack(Dimension dimension, double old, double tolerance)
    {
        try
        {
            SetLocked(dimension, true);
            dimension.Value = old;
            SetLocked(dimension, false);
            return Math.Abs(dimension.Value - old) <= tolerance;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            return false;
        }
    }

    private static object Result(SeContext context, ModelDocument model, Dimension dimension, int index, double old, double before, double after,
        string? note) => new
    {
        dimension = Describe(context, model, dimension, index),
        previous = Value(old, IsAngle(dimension)),
        volume = Geo.Mm3(after),
        volumeChange = Geo.Mm3(after - before),
        bodies = InspectService.DescribeBodies(context, model),
        note,
    };

    /// <summary>
    /// Locks or unlocks dimensions. A locked dimension drives the model (seDimStatusDriving) and keeps its value in later
    /// synchronous edits; an unlocked one follows the geometry. On a failure the dimensions changed so far are put back.
    /// </summary>
    private static object Lock(SeContext context, string? documentHandle, string[]? keys, bool locked)
    {
        string action = locked ? "lock" : "unlock";
        if (keys is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} needs 'dimensions': indexes, handles or names from list.");
        }

        ModelDocument model = DocumentOf(context, documentHandle, keys);
        Dimensions dimensions = DimensionsOf(model);
        (Dimension Dimension, int Index)[] found = keys.Select(k => Find(context, dimensions, k)).ToArray();
        if (locked)
        {
            for (int i = 0; i < found.Length; i++)
            {
                RequireDrivable(found[i].Dimension, keys[i]);
            }
        }

        var changed = new List<Dimension>();
        for (int i = 0; i < found.Length; i++)
        {
            Dimension dimension = found[i].Dimension;
            if (dimension.Constraint == locked)
            {
                continue;
            }

            string? problem;
            try
            {
                SetLocked(dimension, locked);
                problem = dimension.Constraint != locked || locked != (Status(dimension) == "driving")
                    ? $"it is still {Status(dimension)}"
                    : null;
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
            {
                problem = $"Solid Edge rejected it ({com.Message.Trim()})";
            }

            if (problem is not null)
            {
                foreach (Dimension done in changed)
                {
                    SetLocked(done, !locked);
                }

                throw new ToolException("dimension_failed", $"'{keys[i]}' could not be {action}ed: {problem}." +
                    (changed.Count > 0 ? " The dimensions changed before it were put back." : ""),
                    locked ? "A dimension whose faces are already held by other locked dimensions or relations cannot be locked as well." : "Check it with se_pmi list.");
            }

            changed.Add(dimension);
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            dimensions = found.Select(f => Describe(context, model, f.Dimension, f.Index)).ToArray(),
        };
    }

    private static object Delete(SeContext context, string? documentHandle, string[]? keys)
    {
        if (keys is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("delete needs 'dimensions': indexes, handles or names from list.");
        }

        ModelDocument model = DocumentOf(context, documentHandle, keys);
        Dimensions dimensions = DimensionsOf(model);
        (Dimension Dimension, int Index)[] found = keys.Select(k => Find(context, dimensions, k)).ToArray();
        if (found.Select(f => f.Index).Distinct().Count() != found.Length)
        {
            throw ToolException.InvalidArgument("'dimensions' names the same dimension twice.");
        }

        string[] names = found.Select(f => DisplayName(f.Dimension)).ToArray();
        int count = dimensions.Count;
        double before = model.Volume();
        foreach ((Dimension dimension, _) in found)
        {
            dimension.Delete();
        }

        context.Handles.InvalidateScope(context.DocumentHandle(model.Document), "PMI dimensions were deleted", "dimension");
        int left = dimensions.Count;
        double after = model.Volume();
        return left != count - found.Length
            ? throw new ToolException("dimension_failed", $"Solid Edge kept {left - (count - found.Length)} of the dimensions to delete.", "Check them with se_pmi list.")
            : new
            {
                deleted = names,
                count = left,
                volume = Geo.Mm3(after),
                volumeChange = Geo.Mm3(after - before),
            };
    }

    private static object Display(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiOf(model);
        return new
        {
            document = context.DocumentHandle(model.Document),
            showDimensions = pmi.ShowDimensions,
            showAnnotations = pmi.ShowAnnotations,
            dimensions = DimensionsOf(model).Count,
        };
    }

    private static object SetDisplay(SeContext context, string? documentHandle, PmiSpec spec)
    {
        if (spec.ShowDimensions is null && spec.ShowAnnotations is null)
        {
            throw ToolException.InvalidArgument("set_display needs 'showDimensions' or 'showAnnotations'.");
        }

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        PMI pmi = PmiOf(model);
        var previous = new { showDimensions = pmi.ShowDimensions, showAnnotations = pmi.ShowAnnotations };
        if (spec.ShowDimensions is bool dimensions)
        {
            pmi.ShowDimensions = dimensions;
        }

        if (spec.ShowAnnotations is bool annotations)
        {
            pmi.ShowAnnotations = annotations;
        }

        string[] ignored = [.. new (string Name, bool? Wanted, bool Now)[]
            {
                ("showDimensions", spec.ShowDimensions, pmi.ShowDimensions),
                ("showAnnotations", spec.ShowAnnotations, pmi.ShowAnnotations),
            }.Where(s => s.Wanted is bool wanted && wanted != s.Now).Select(s => s.Name)];
        return ignored.Length > 0
            ? throw new ToolException("setting_rejected", $"Solid Edge left {string.Join(" and ", ignored)} unchanged.", "Check the document with se_pmi display.")
            : new
            {
                document = context.DocumentHandle(model.Document),
                showDimensions = pmi.ShowDimensions,
                showAnnotations = pmi.ShowAnnotations,
                previous,
            };
    }

    /// <summary>The document of the first dimension handle, or the one given, or the active one.</summary>
    private static ModelDocument DocumentOf(SeContext context, string? documentHandle, string[] keys) =>
        documentHandle is null && keys is [string first, ..] && first.Contains(':', StringComparison.Ordinal)
            ? ModelDocument.For(context, null, first)
            : ModelDocument.Get(context, documentHandle);

    private static PMI PmiOf(ModelDocument model) => Late.Get(model.Document, "PMI") as PMI
        ?? throw new ToolException("not_supported", $"'{model.Document.Name}' has no PMI.", "PMI belongs to part and sheet metal documents.");

    private static Dimensions DimensionsOf(ModelDocument model) => (Dimensions)PmiOf(model).Dimensions;

    /// <summary>A dimension by index (from 1), handle or name (DisplayName, as list shows it).</summary>
    private static (Dimension Dimension, int Index) Find(SeContext context, Dimensions dimensions, string key)
    {
        int count = dimensions.Count;
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return index >= 1 && index <= count
                ? (dimensions.Item(index), index)
                : throw new ToolException("not_found", $"The document has {count} PMI dimension(s), so there is no dimension {index}.", "Indexes start at 1 (se_pmi list).");
        }

        if (key.Contains(':', StringComparison.Ordinal))
        {
            Dimension wanted = context.Handles.Resolve<Dimension>(key, "dimensions");
            for (int i = 1; i <= count; i++)
            {
                if (Late.SameObject(dimensions.Item(i), wanted))
                {
                    return (wanted, i);
                }
            }

            throw new ToolException("not_found", $"'{key}' is not a PMI dimension of this document.", "Use a handle from se_pmi list (dimensions of sketches: se_variables).");
        }

        for (int i = 1; i <= count; i++)
        {
            Dimension dimension = dimensions.Item(i);
            if (string.Equals(DisplayName(dimension), key, StringComparison.Ordinal))
            {
                return (dimension, i);
            }
        }

        throw new ToolException("not_found", $"No PMI dimension is called '{key}'.", "Indexes, handles and names come from se_pmi list (names are case-sensitive).");
    }

    private static void RequireDrivable(Dimension dimension, string key)
    {
        string status = Status(dimension);
        if (status is not ("driven" or "driving"))
        {
            throw new ToolException("not_drivable", $"'{key}' cannot drive the model: its status is {status}.",
                "Pick a dimension whose status is driven or driving (se_pmi list).");
        }
    }

    private static object Describe(SeContext context, ModelDocument model, Dimension dimension, int index)
    {
        bool angle = IsAngle(dimension);
        return new
        {
            index,
            handle = context.Register(dimension, "dimension", model.Document),
            name = DisplayName(dimension),
            type = TypeName(dimension),
            value = Value(dimension.Value, angle),
            units = angle ? "deg" : "mm",
            locked = dimension.Constraint,
            status = Status(dimension),
            faces = RelatedFaces(context, model, dimension),
        };
    }

    /// <summary>The faces Solid Edge reports for the dimension (it may report only one end, or none).</summary>
    private static string[] RelatedFaces(SeContext context, ModelDocument model, Dimension dimension)
    {
        var faces = new List<string>();
        try
        {
            dimension.GetRelatedCount(out int count);
            for (int i = 1; i <= count; i++)
            {
                dimension.GetRelated(i, out object graphic, out _, out _, out _, out _);
                if (graphic is Face face)
                {
                    faces.Add(context.Register(face, "face", model.Document));
                }
            }
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            // No related faces to show.
        }

        return [.. faces];
    }

    private static string DisplayName(Dimension dimension) => Late.TryGetString(dimension, "DisplayName") ?? "";

    private static bool IsAngle(Dimension dimension) => DimensionType(dimension)
        is DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle or DimTypeConstants.igDimTypeAngularCoordinate;

    private static DimTypeConstants? DimensionType(Dimension dimension)
    {
        try
        {
            return dimension.DimensionType;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string TypeName(Dimension dimension) => DimensionType(dimension) switch
    {
        DimTypeConstants.igDimTypeLinear => "linear",
        DimTypeConstants.igDimTypeRadial => "radius",
        DimTypeConstants.igDimTypeAngular => "angle",
        DimTypeConstants.igDimTypeRDiameter or DimTypeConstants.igDimTypeCDiameter => "diameter",
        DimTypeConstants.igDimTypeSDiameter => "symmetric_diameter",
        DimTypeConstants.igDimTypeArcLength => "arc_length",
        DimTypeConstants.igDimTypeArcAngle => "arc_angle",
        DimTypeConstants.igDimTypeCoordinate => "coordinate",
        DimTypeConstants.igDimTypeChamfer => "chamfer",
        DimTypeConstants.igDimTypeAngularCoordinate => "angular_coordinate",
        DimTypeConstants.igDimTypeCurveLength => "curve_length",
        _ => "other",
    };

    private static string Status(Dimension dimension)
    {
        try
        {
            return dimension.StatusOfDimension switch
            {
                DimStatusConstants.seDimStatusDriving => "driving",
                DimStatusConstants.seDimStatusDriven => "driven",
                DimStatusConstants.seDimStatusDetached => "detached",
                DimStatusConstants.seOneEndDetached => "one_end_detached",
                DimStatusConstants.seDimStatusError => "error",
                DimStatusConstants.seNonDrivablePMI => "not_drivable",
                _ => "unknown",
            };
        }
        catch (COMException)
        {
            return "unknown";
        }
    }

    private static double Value(double apiValue, bool angle) => angle ? Geo.R(Units.RadToDeg(apiValue)) : Geo.Mm(apiValue);

    private static string Text(double apiValue, bool angle) =>
        Value(apiValue, angle).ToString(CultureInfo.InvariantCulture) + (angle ? " degrees" : " mm");

    private static void Restore(ModelDocument model, bool switched)
    {
        if (!switched)
        {
            return;
        }

        try
        {
            model.EnsureOrdered();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The document stays in synchronous modelling; the next ordered operation switches it.
        }
    }
}
