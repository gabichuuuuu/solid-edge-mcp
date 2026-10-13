// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Thin wall of a protrusion (mm): Side = inside | outside | symmetric of the profile; EndCaps closes both ends.</summary>
public sealed record ThinWallSpec(double Thickness, string Side = "inside", bool EndCaps = false);

/// <summary>Extrusion limits and draft: From/To = plane index, plane handle or planar face; Draft in degrees, DraftSide inside | outside.</summary>
public sealed record ExtrudeLimits(string? From = null, string? To = null, double? Draft = null, string? DraftSide = null);

/// <summary>
/// Part operations in ordered modelling. Each operation is verified: if Solid Edge marks it
/// as failed or does not change the volume as expected, it is deleted and an actionable error is returned.
/// Sketch-based operations use a linked copy of their profile (the UI command "Select from Sketch"):
/// if the operation is deleted, the sketch is kept so it can be retried.
/// </summary>
public static partial class FeatureService
{
    /// <summary>Volume tolerance (m³) for deciding whether an operation changed the solid: 1e-6 mm³.</summary>
    private const double VolumeTolerance = 1e-15;

    /// <summary>Area tolerance (m²) for changes that do not alter the volume: 1e-6 mm².</summary>
    private const double AreaTolerance = 1e-12;

    public static readonly IReadOnlyList<string> Directions = ["normal", "reverse", "symmetric"];

    public static object Extrude(SeContext context, string? documentHandle, string sketch, string operation, string extent,
        string direction, double? distance, ThinWallSpec? thinWall = null, ExtrudeLimits? limits = null)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        bool cut = IsCut(operation);
        ThinWallSpec? wall = CheckThinWall(thinWall, cut);
        bool finite = extent switch
        {
            "finite" => true,
            "through_all" or "through_next" or "to" or "from_to" => false,
            _ => throw ToolException.InvalidArgument($"Unknown extent '{extent}'.", "Use finite, through_all, through_next, to or from_to."),
        };
        object? fromSource = null;
        object? toSource = null;
        if (extent is "to" or "from_to")
        {
            toSource = model.ResolvePlaneSource(context, limits?.To ?? throw ToolException.InvalidArgument($"Extent '{extent}' requires 'to': the plane or planar face the extrusion ends on."), "to");
            if (extent == "from_to")
            {
                fromSource = model.ResolvePlaneSource(context, limits.From ?? throw ToolException.InvalidArgument("Extent 'from_to' requires 'from': the plane or planar face the extrusion starts on."), "from");
            }
        }

        double? draft = limits?.Draft;
        if (draft is { } angle && !(angle > 0 && angle < 90))
        {
            throw ToolException.InvalidArgument("'draft' must be an angle between 0 and 90 degrees.");
        }

        DraftSideConstants draftSide = (limits?.DraftSide ?? "inside") switch
        {
            "inside" => DraftSideConstants.seDraftInside,
            "outside" => DraftSideConstants.seDraftOutside,
            string other => throw ToolException.InvalidArgument($"Unknown draft side '{other}'.", "Use inside or outside."),
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
                    "to" or "from_to" => cutouts.AddFromToMulti(1, ref profiles, fromSource ?? included.Profile.Plane, toSource),
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
                "to" or "from_to" => protrusions.AddFromToMulti(1, ref profiles, fromSource ?? included.Profile.Plane, toSource),
                _ => protrusions.AddThroughNextMulti(1, ref profiles, side),
            };
        });

        ApplyThinWall(model, feature, wall, included);
        if (draft is { } degrees)
        {
            ApplyDraft(model, feature, Units.DegToRad(degrees), draftSide, side == FeaturePropertyConstants.igSymmetric, included);
        }

        object result = Verify(context, model, feature, before, cut ? -1 : 1, switched, sketch, included);
        Hide(sketchProfile);
        Hide(included.Profile);
        return result;
    }

    /// <summary>
    /// Draft (taper) of the side faces of an extrusion (ApplyDirection1Treatment, and Direction2 when symmetric). Verified with a
    /// 40 x 40 square extruded 30 mm: 5° inside 41976.37 mm3 (a frustum 40 -> 34.75), outside 54574.74 (40 -> 45.25).
    /// A draft the solid cannot take leaves the volume as it was: the feature is then deleted.
    /// </summary>
    private static void ApplyDraft(ModelDocument model, object feature, double radians, DraftSideConstants side, bool bothDirections, IncludedProfile included)
    {
        if (StatusOf(feature) == "failed")
        {
            // Verify reports the failed feature itself.
            return;
        }

        double before = model.Volume();
        string? problem = null;
        try
        {
            // Verified: the same side on Direction2 tapers that half the other way (symmetric 30 mm, 5° inside on both gave
            // 48068.89 mm3 = an inside half plus an outside half), so the second direction takes the opposite side.
            DraftSideConstants opposite = side == DraftSideConstants.seDraftInside ? DraftSideConstants.seDraftOutside : DraftSideConstants.seDraftInside;
            foreach ((string method, DraftSideConstants s) in bothDirections ? [("ApplyDirection1Treatment", side), ("ApplyDirection2Treatment", opposite)] : new[] { ("ApplyDirection1Treatment", side) })
            {
                Late.Call(feature, method, (int)TreatmentTypeConstants.seTreatmentDraft, (int)s, radians, (int)TreatmentCrownTypeConstants.seTreatmentCrownNone,
                    (int)TreatmentCrownSideConstants.seTreatmentCrownSideNone, (int)TreatmentCrownCurvatureSideConstants.seTreatmentCrownCurvatureNone, 0.0, 0.0);
            }

            if (StatusOf(feature) == "failed" || Math.Abs(model.Volume() - before) <= VolumeTolerance)
            {
                problem = "the draft did not change the solid";
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            problem = $"Solid Edge did not take the draft ({ComErrors.Unwrap(ex).Message.Trim()})";
        }

        if (problem is not null)
        {
            string name = Late.TryGetString(feature, "Name") ?? "The feature";
            TryDelete(feature);
            DeleteIncluded(included);
            throw new ToolException("feature_failed", $"{name} was deleted because {problem}. The sketch is kept: call the tool again with it.",
                "Use a smaller draft angle, or the other draftSide, so that the side faces still meet the end.");
        }
    }

    /// <summary>
    /// The thin wall option of a protrusion (SetThinWallOptions on the new feature, as the Thin Wall step of the command).
    /// Verified with a 40 x 40 square extruded 30 mm (48000 mm3) and a 2 mm wall: inside 9120 (40² - 36²)·30, outside 10080,
    /// symmetric 9600, inside with end caps 14304 (48000 - 36²·26); a 10 x 30 rectangle 10 mm off the axis revolved 360°:
    /// inside 13571.68, outside 16587.61. An open profile gives no body.
    /// </summary>
    private static ThinWallSpec? CheckThinWall(ThinWallSpec? wall, bool cut)
    {
        if (wall is null)
        {
            return null;
        }

        if (cut)
        {
            throw ToolException.InvalidArgument("'wall' applies to protrusions (operation add), not to cutouts.");
        }

        Units.RequirePositive(wall.Thickness, "wall");
        _ = ThinWallSide(wall.Side);
        return wall;
    }

    private static FeaturePropertyConstants ThinWallSide(string side) => side switch
    {
        "inside" => FeaturePropertyConstants.igInside,
        "outside" => FeaturePropertyConstants.igOutside,
        "symmetric" => FeaturePropertyConstants.igSymmetric,
        _ => throw ToolException.InvalidArgument($"Unknown wall side '{side}'.", "Use inside, outside or symmetric."),
    };

    /// <summary>
    /// Sets the thin wall on the new feature. Verified: a wall thicker than half the profile leaves the solid as it was with a
    /// warning, so a volume that does not change is taken as a failure and the feature is deleted.
    /// </summary>
    private static void ApplyThinWall(ModelDocument model, object feature, ThinWallSpec? wall, IncludedProfile? included)
    {
        if (wall is null || StatusOf(feature) == "failed")
        {
            return;
        }

        const string hint = "Use a closed sketch and a wall thinner than half the narrowest part of the profile.";
        double solid = model.Volume();
        string? problem = null;
        try
        {
            Late.Call(feature, "SetThinWallOptions", true, wall.EndCaps, false, Units.MmToM(wall.Thickness), (int)ThinWallSide(wall.Side));
            if (StatusOf(feature) == "failed" || Math.Abs(model.Volume() - solid) <= VolumeTolerance)
            {
                problem = "the solid did not become a thin wall";
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            problem = $"Solid Edge did not take the thin wall ({ComErrors.Unwrap(ex).Message.Trim()})";
        }

        if (problem is not null)
        {
            string name = Late.TryGetString(feature, "Name") ?? "The feature";
            TryDelete(feature);
            if (included is not null)
            {
                DeleteIncluded(included);
            }

            throw new ToolException("feature_failed", $"{name} was deleted because {problem}. The sketches are kept: call the tool again with them.", hint);
        }
    }

    public static object Revolve(SeContext context, string? documentHandle, string sketch, string? axis, string operation,
        double angle, string direction, ThinWallSpec? thinWall = null)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, sketch);
        Profile sketchProfile = SketchService.Resolve(context, sketch);
        RefAxis refAxis = context.Handles.Resolve<RefAxis>(
            axis ?? throw ToolException.InvalidArgument("se_revolve requires 'axis'.", "Create it with se_sketch action 'axis' on a line of the sketch, before closing the sketch."),
            "axis");
        bool cut = IsCut(operation);
        ThinWallSpec? wall = CheckThinWall(thinWall, cut);
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

        ApplyThinWall(model, feature, wall, included);
        object result = Verify(context, model, feature, before, cut ? -1 : 1, switched, sketch, included);
        Hide(sketchProfile);
        Hide(included.Profile);
        return result;
    }

    /// <summary>
    /// Fillet on the given edges: a constant radius, or a variable one given at the start and end of each edge.
    /// Verified: Rounds.AddVariable with the edges, their end vertices and a radius per vertex; the radius changes
    /// linearly along the edge (2 to 8 mm along a 100 mm edge removes 601.2 mm3; circular sections would give 600.9).
    /// </summary>
    public static object Round(SeContext context, string? documentHandle, string[]? edges, double? radius, double[]? radii = null)
    {
        (ModelDocument model, object[] edgeObjects) = ResolveEdges(context, documentHandle, edges);
        if (radius is not null == radii is not null)
        {
            throw ToolException.InvalidArgument("se_round takes either 'radius' (constant) or 'radii' (variable), not both and not neither.");
        }

        if (radii is not null)
        {
            return VariableRound(context, model, edgeObjects, radii);
        }

        double r = Units.MmToM(Units.RequirePositive(radius!.Value, "radius"));
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        double[] constant = Enumerable.Repeat(r, edgeObjects.Length).ToArray();
        object feature = Create("round", () => target.Rounds.Add(edgeObjects.Length, edgeObjects, constant));
        return Verify(context, model, feature, before, 0, switched);
    }

    private static object VariableRound(SeContext context, ModelDocument model, object[] edgeObjects, double[] radii)
    {
        if (radii.Length != 2 * edgeObjects.Length)
        {
            throw ToolException.InvalidArgument($"'radii' needs two values per edge (radius at its start and at its end, mm): {2 * edgeObjects.Length} for {edgeObjects.Length} edge(s).",
                "The start and end of each edge are those given by se_geometry 'edges'.");
        }

        // One radius per vertex: a vertex shared by two edges of a chain must get the same radius from both.
        var vertices = new List<object>();
        var vertexRadii = new List<double>();
        for (int i = 0; i < edgeObjects.Length; i++)
        {
            var edge = (SolidEdgeGeometry.Edge)edgeObjects[i];
            foreach ((object vertex, double value) in new[] { ((object)edge.StartVertex, radii[2 * i]), (edge.EndVertex, radii[2 * i + 1]) })
            {
                double r = Units.MmToM(Units.RequirePositive(value, "radii"));
                int known = vertices.FindIndex(v => Late.SameObject(v, vertex));
                if (known < 0)
                {
                    vertices.Add(vertex);
                    vertexRadii.Add(r);
                }
                else if (Math.Abs(vertexRadii[known] - r) > 1e-9)
                {
                    throw ToolException.InvalidArgument("Two edges that meet give different radii for the vertex they share.",
                        "Give the same radius at the end of one edge and the start of the next.");
                }
            }
        }

        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        Array edgeArray = edgeObjects;
        Array vertexArray = vertices.ToArray();
        Array radiusArray = vertexRadii.ToArray();
        object feature = Create("variable round", () => target.Rounds.AddVariable(edgeObjects.Length, ref edgeArray, vertices.Count, ref vertexArray, ref radiusArray));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Equal-setback chamfer on the given edges.</summary>
    /// <summary>
    /// Chamfer with an equal setback, two setbacks or a setback and an angle; the last two measure 'distance' on the reference
    /// face. Verified on an edge of a 40 x 40 x 30 block with the top face as reference: 3 and 5 remove 0.5·3·5·40 = 300 mm3,
    /// 3 and 30° remove 0.5·3·(3·tan 30°)·40 = 103.92 mm3.
    /// </summary>
    public static object Chamfer(SeContext context, string? documentHandle, string[]? edges, double? distance, double? distance2 = null,
        double? angle = null, string? face = null)
    {
        (ModelDocument model, object[] edgeObjects) = ResolveEdges(context, documentHandle, edges);
        double d = Units.MmToM(Units.RequirePositive(distance ?? throw ToolException.InvalidArgument("se_chamfer requires 'distance' (mm)."), "distance"));
        if (distance2 is not null && angle is not null)
        {
            throw ToolException.InvalidArgument("Give either 'distance2' (two setbacks) or 'angle' (a setback and an angle), not both.");
        }

        if (angle is { } a && !(a > 0 && a < 90))
        {
            throw ToolException.InvalidArgument("'angle' must be between 0 and 90 degrees.");
        }

        object? reference = null;
        if (distance2 is not null || angle is not null)
        {
            reference = context.Handles.Resolve<SolidEdgeGeometry.Face>(face ?? throw ToolException.InvalidArgument(
                "Two setbacks or an angle need 'face': the face 'distance' is measured on, one of the two faces that meet at the edges.",
                "Get it with se_geometry 'faces'."), "face");
        }

        double d2 = distance2 is { } second ? Units.MmToM(Units.RequirePositive(second, "distance2")) : 0;
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("chamfer", () => distance2 is not null
            ? target.Chamfers.AddUnequalSetback(reference, edgeObjects.Length, edgeObjects, d, d2)
            : angle is { } degrees
                ? target.Chamfers.AddSetbackAngle(reference, edgeObjects.Length, edgeObjects, d, Units.DegToRad(degrees))
                : target.Chamfers.AddEqualSetback(edgeObjects.Length, edgeObjects, d));
        return Verify(context, model, feature, before, 0, switched);
    }

    public static readonly IReadOnlyList<string> FeatureEditActions = ["suppress", "unsuppress", "delete", "rename", "group", "ungroup", "reorder", "roll_to", "roll_to_end"];

    /// <summary>
    /// Suppress, unsuppress, delete or rename a feature; group features and dissolve a group; move a feature next to another
    /// one; roll the model back to a feature and forward to its end.
    /// </summary>
    public static object Edit(SeContext context, string? documentHandle, string? featureHandle, string action, string? name,
        string[]? features = null, string? target = null, bool insertBefore = true)
    {
        switch (action)
        {
            case "group":
                return Group(context, documentHandle, features, name);
            case "roll_to_end":
                return RollToEnd(context, documentHandle);
            case var known when !FeatureEditActions.Contains(known):
                throw ToolException.InvalidArgument($"Unknown action '{action}'.", $"Use one of: {string.Join(", ", FeatureEditActions)}.");
        }

        if (string.IsNullOrWhiteSpace(featureHandle))
        {
            throw ToolException.InvalidArgument($"Action '{action}' requires 'feature': a handle from se_feature_tree.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, featureHandle);
        object feature = context.Handles.Resolve(featureHandle, "feature");
        string label = Late.TryGetString(feature, "Name") ?? featureHandle;
        switch (action)
        {
            case "ungroup":
                return Ungroup(context, model, featureHandle, feature, label);
            case "reorder":
                return Reorder(context, model, feature, label, target, insertBefore);
            case "roll_to":
                return RollTo(context, model, feature, label);
        }

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
                    throw ToolException.InvalidArgument($"Unknown action '{action}'.", $"Use one of: {string.Join(", ", FeatureEditActions)}.");
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

    /// <summary>Feature tree of the document (feature bar), in order.</summary>
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

        double? volume = model.TryVolume();
        return new
        {
            document = context.DocumentHandle(model.Document),
            modelingMode = model.ModelingMode,
            count,
            offset = Math.Max(offset, 0),
            features = items,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
            note = volume is null ? "Solid Edge cannot read the body (e.g. converted to a mesh): use se_physical for its volume." : null,
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
            members = feature is FeatureGroup ? Late.TryGet(feature, "Count") as int? : null,
        };
    }

    public static string? StatusOf(object feature)
    {
        object? value = Late.TryGet(feature, "Status");
        return value is null ? null : StatusName(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }

    internal static string StatusName(int code) => code switch
    {
        (int)FeatureStatusConstants.igFeatureOK => "ok",
        (int)FeatureStatusConstants.igFeatureFailed => "failed",
        (int)FeatureStatusConstants.igFeatureWarned => "warning",
        (int)FeatureStatusConstants.igFeatureSuppressed => "suppressed",
        (int)FeatureStatusConstants.igFeatureRolledBack => "rolled_back",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };

    private static bool IsCut(string operation) => operation switch
    {
        "add" => false,
        "cut" => true,
        _ => throw ToolException.InvalidArgument($"Unknown operation '{operation}'.", "Use 'add' (protrusion) or 'cut' (cutout)."),
    };

    /// <summary>Side relative to the sketch plane: normal = the direction of the sketch normal.</summary>
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

    private static object Create(string what, Func<object> create) => Create(what, null, create, sketch: false);

    /// <summary>
    /// Creates the feature; if Solid Edge rejects it, deletes the profile copy and returns an actionable error.
    /// <paramref name="sketch"/>: the profile comes from a sketch of the person, which is kept for another try.
    /// </summary>
    private static object Create(string what, IncludedProfile? included, Func<object> create, bool sketch = true)
    {
        try
        {
            return create();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            // Calls through IDispatch (Late) arrive wrapped in TargetInvocationException.
            if (included is not null)
            {
                DeleteIncluded(included);
            }

            string kept = sketch ? " The sketch is kept: call the tool again with it." : "";
            throw ComErrors.IsObjectRemoved(com)
                ? new ToolException("feature_failed", $"Solid Edge could not build the {what} and removed it.", NotBuiltHint + kept, ex)
                : new ToolException("feature_failed", $"Solid Edge rejected the {what}: {com.Message}",
                    (sketch ? "Check that the sketch is closed (se_sketch 'close'), the direction and extent, and that the geometry intersects the part."
                        : "Check the elements, sizes and direction against the part (se_geometry).") + kept, ex);
        }
    }

    /// <summary>What to check when Solid Edge removes an operation it cannot build while creating it.</summary>
    internal const string NotBuiltHint = "Check the sizes against the part: Solid Edge cannot build an operation that would leave walls of zero thickness or meet the body only along a line (for example a hole exactly as wide as the bar).";

    /// <summary>Linked copy of the sketch profile for an operation (equivalent to the UI command "Select from Sketch").</summary>
    private static IncludedProfile Include(ModelDocument model, Profile sketchProfile)
    {
        try
        {
            Profile profile = model.ProfileSets.CreateIncludedProfile(sketchProfile);
            return new IncludedProfile(profile, Late.TryGet(profile, "Parent"));
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("sketch_not_usable", $"The sketch cannot be used for a feature: {ex.Message}",
                "Close it first with se_sketch action 'close' (it must form closed loops).", ex);
        }
    }

    /// <summary>
    /// Deletes the profile set of the copy. When a failed operation is deleted, Solid Edge deletes the profile
    /// but leaves its set empty; that is why the reference to the set is stored when the copy is created.
    /// </summary>
    private static void DeleteIncluded(IncludedProfile included)
    {
        try
        {
            Late.Call(included.Set ?? included.Profile, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // No longer exists.
        }
    }

    /// <summary>Linked copy of a sketch profile and the set (ProfileSet) that contains it.</summary>
    private sealed record IncludedProfile(Profile Profile, object? Set);

    /// <summary>Verifies the feature with <see cref="Check"/> and describes the result.</summary>
    private static object Verify(SeContext context, ModelDocument model, object feature, double before, int expectedSign, bool switched,
        string? sketch = null, IncludedProfile? included = null, string? direction = null, Func<bool>? changedOtherwise = null, string? hint = null)
    {
        (double after, string? status) = Check(context, model, feature, before, expectedSign, sketch, included, changedOtherwise, hint: hint);
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
    /// Checks the status and the volume change; if the feature failed or had no effect, it deletes it and throws
    /// <c>feature_failed</c>. <paramref name="expectedSign"/>: +1 adds material, -1 removes it, 0 any change;
    /// with 0, <paramref name="changedOtherwise"/> detects changes that do not alter the volume (e.g. a draft
    /// with the neutral plane in the middle of the face). With <paramref name="undoneByCaller"/> it is not deleted: the caller undoes
    /// it (in synchronous mode, deleting a feature does not return the faces it trimmed). <paramref name="hint"/> replaces the
    /// next step about sketches for features made from faces or edges.
    /// </summary>
    private static (double After, string? Status) Check(SeContext context, ModelDocument model, object feature, double before, int expectedSign,
        string? sketch, IncludedProfile? included, Func<bool>? changedOtherwise = null, bool undoneByCaller = false, string? hint = null)
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

            string kept = sketch is null ? ""
                : sketch.Contains(',', StringComparison.Ordinal) ? $" The sketches ({sketch}) are kept: call the tool again with them."
                : $" The sketch '{sketch}' is kept: call the tool again with it.";
            throw new ToolException("feature_failed", $"{name} was {(undoneByCaller ? "undone" : "deleted")} because {problem}.{kept}",
                hint ?? "Check that the geometry meets the part (sketch x/y are measured from the frame origin returned by se_sketch 'create', which on a face is not the model origin), the direction ('normal' follows the sketch normal; try another one) and the extent.");
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
    /// Hides the sketch used and the copy of its profile, as the Solid Edge UI does. Only after a
    /// successful operation: if it fails, the sketch stays visible so it can be corrected or reused.
    /// </summary>
    private static void Hide(Profile profile)
    {
        try
        {
            profile.Visible = false;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Affects only the presentation; the feature is already created and verified.
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
            // If it cannot be deleted, the error already reports the failure; the person can undo it.
        }
    }
}
