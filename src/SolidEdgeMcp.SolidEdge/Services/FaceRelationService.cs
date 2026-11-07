// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Cone = SolidEdgeGeometry.Cone;
using Cylinder = SolidEdgeGeometry.Cylinder;
using Face = SolidEdgeGeometry.Face;
using Plane = SolidEdgeGeometry.Plane;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_face_relation does: the action and its inputs. Lengths in mm.</summary>
public sealed record FaceRelationSpec(string Action, string[]? Faces = null, string? Target = null, double? Distance = null,
    string? Relation = null, string[]? Rules = null, bool Maintain = true, IReadOnlyDictionary<string, bool>? Values = null);

/// <summary>
/// 3D relations between faces of a part (the Relate commands of synchronous modelling) and the live rules.
/// Verified in Solid Edge 2026 on synchronous boxes and cylinders:
/// <list type="bullet">
/// <item>Constraints.AddOffset(n, faces, target, metres, igSimple) moves the faces along their normal to that distance from
/// the target, on the side they are on (0 makes them coplanar); EditOffsetValueByFace(metres, face) moves them again.</item>
/// <item>Constraints.Add(n, faces, target, type, igSimple, igSeedAll) moves faces to meet a parallel, perpendicular,
/// tangent or concentric relation, but the solver may move the target instead (it moved the face of the newest box,
/// whichever was the target): a fixed relation on the target during the call (Add with the target as its own target)
/// makes the faces move. With igSeedSingle the relation is only recorded: nothing moves and later edits ignore it.</item>
/// <item>A relation Solid Edge cannot meet (a boss concentric with another) stays unsolved and blocks later ones, so it is
/// deleted. Relations stay and later solves keep them (a face offset from a moved face follows it).</item>
/// <item>Faces of ordered features cannot take relations: Add fails with E_FAIL and nothing changes. In synchronous mode
/// the ordered features are not computed, so volumes are compared in the mode the document had.</item>
/// <item>Solid Edge clears the undo list when a relation is added (Undo then fails with E_UNEXPECTED), so nothing here can
/// be undone: the faces are checked before anything is added.</item>
/// <item>Constraints.Item returns a new object on every call: relations are matched by Constraint.index, and new ones are
/// appended at the end of the collection.</item>
/// <item>Live rules belong to the Solid Edge session, not to the document: a rule set through one part or sheet metal
/// document applies to every open and new one. RestoreLiveRules brings back the defaults of most rules but leaves the offset
/// rule as it is.</item>
/// </list>
/// </summary>
public static class FaceRelationService
{
    public static readonly IReadOnlyList<string> Actions =
        ["list", "coplanar", "parallel", "perpendicular", "tangent", "concentric", "offset", "fix", "edit", "delete", "live_rules", "set_live_rules", "restore_live_rules"];

    /// <summary>The live rules in the order of LiveRulesConstants (1 = concentric … 18 = offset).</summary>
    public static readonly IReadOnlyList<string> LiveRules =
        ["concentric", "coplanar", "tangent_edge", "tangent_touching", "parallel", "perpendicular", "symmetric", "symmetric_xy", "symmetric_yz",
         "symmetric_zx", "maintain_radius", "ortho_locking", "coplanar_axes", "coplanar_axes_x", "coplanar_axes_y", "coplanar_axes_z", "thickness_chain", "offset"];

    /// <summary>Lengths in metres.</summary>
    private const double Tolerance = 1e-6;

    /// <summary>On the dot product of unit vectors (1e-6 is about 0.08 degrees).</summary>
    private const double DotTolerance = 1e-6;

    private const string SynchronousHint =
        "Relations move synchronous geometry: faces of se_primitive solids, of synchronous parts and of imported bodies; faces of ordered features cannot take them.";

    private const string LiveRulesNote =
        "Live rules belong to the Solid Edge session: they apply to every part and sheet metal document. Pass 'previous' as 'values' to put them back.";

    public static object Run(SeContext context, string? documentHandle, FaceRelationSpec spec) => spec.Action switch
    {
        "list" => List(context, documentHandle),
        "coplanar" => Offset(context, documentHandle, spec, spec.Distance is null or 0
            ? 0
            : throw ToolException.InvalidArgument("coplanar has no distance.", "Use offset to keep the faces at a distance.")),
        "offset" => Offset(context, documentHandle, spec, RequireDistance(spec.Distance, "offset")),
        "parallel" => Relate(context, documentHandle, spec, ConstraintTypeConstants.igRelationParallel),
        "perpendicular" => Relate(context, documentHandle, spec, ConstraintTypeConstants.igRelationPerpendicular),
        "tangent" => Relate(context, documentHandle, spec, ConstraintTypeConstants.igRelationTangent),
        "concentric" => Relate(context, documentHandle, spec, ConstraintTypeConstants.igRelationConcentric),
        "fix" => Fix(context, documentHandle, spec),
        "edit" => Edit(context, documentHandle, spec),
        "delete" => Delete(context, documentHandle, spec),
        "live_rules" => ReadLiveRules(context, documentHandle),
        "set_live_rules" => SetLiveRules(context, documentHandle, spec),
        "restore_live_rules" => RestoreLiveRules(context, documentHandle),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", Actions)}."),
    };

    private static object List(SeContext context, string? documentHandle)
    {
        (ModelDocument model, PartDocument part) = Part(context, documentHandle, null);
        Constraints constraints = part.Constraints;
        int count = constraints.Count;
        return new
        {
            document = context.DocumentHandle(model.Document),
            count,
            relations = Enumerable.Range(1, count).Select(i => Describe(context, model, constraints.Item(i), i)).ToArray(),
        };
    }

    /// <summary>Offset (and coplanar, an offset of 0): the faces move along their normal, on the side of the target they are on.</summary>
    private static object Offset(SeContext context, string? documentHandle, FaceRelationSpec spec, double distance)
    {
        string action = spec.Action;
        (ModelDocument model, PartDocument part, Face[] faces, Face target) = Operands(context, documentHandle, spec, action);
        Shape goal = Require(ShapeOf(target), "plane", spec.Target!, action);
        for (int i = 0; i < faces.Length; i++)
        {
            Shape shape = Require(ShapeOf(faces[i]), "plane", spec.Faces![i], action);
            if (!Parallel(shape.Direction, goal.Direction))
            {
                throw ToolException.InvalidArgument($"'{spec.Faces[i]}' is not parallel to the target: {action} moves a face along its normal.",
                    "Make the faces parallel first (action parallel).");
            }
        }

        double metres = Units.MmToM(distance);
        return Apply(context, model, part, action, faces, target, fixTarget: false,
            (constraints, face, stay) =>
            {
                Array one = new object[] { face };
                return constraints.AddOffset(1, ref one, stay, metres, SolveTypeConstants.igSimple);
            },
            (face, stay) => Math.Abs(PlaneDistance(stay, face) - metres) <= Tolerance && Parallel(face.Direction, stay.Direction)
                ? null
                : $"it is {Geo.Mm(PlaneDistance(stay, face))} mm from the target instead of {distance}");
    }

    /// <summary>Parallel, perpendicular, tangent and concentric: Add with every face that can move (igSeedAll) while the target is fixed.</summary>
    private static object Relate(SeContext context, string? documentHandle, FaceRelationSpec spec, ConstraintTypeConstants type)
    {
        string action = spec.Action;
        (ModelDocument model, PartDocument part, Face[] faces, Face target) = Operands(context, documentHandle, spec, action);
        Shape goal = ShapeOf(target);
        for (int i = 0; i < faces.Length; i++)
        {
            Shape shape = ShapeOf(faces[i]);
            if (Unsupported(type, shape, goal) is { } why)
            {
                throw ToolException.InvalidArgument($"{action} between '{spec.Faces![i]}' ({shape.Kind}) and '{spec.Target}' ({goal.Kind}): {why}.",
                    "parallel and perpendicular take planes, cylinders and cones; tangent a cylinder and a plane (or two cylinders); concentric cylinders or cones.");
            }
        }

        return Apply(context, model, part, action, faces, target, fixTarget: true,
            (constraints, face, stay) =>
            {
                Array one = new object[] { face };
                return constraints.Add(1, ref one, stay, type, SolveTypeConstants.igSimple, SeedOptionConstants.igSeedAll);
            },
            (face, stay) => Unmet(type, face, stay));
    }

    /// <summary>
    /// Adds one relation per face in synchronous modelling (with several sources, AddOffset only relates the first one), then
    /// checks on the geometry that the target stayed and every face meets its relation. When one is not met, the relations of
    /// this call are deleted and the failure reported: it cannot be undone (see the class remarks).
    /// </summary>
    private static object Apply(SeContext context, ModelDocument model, PartDocument part, string action, Face[] faces, Face target,
        bool fixTarget, Func<Constraints, Face, Face, Constraint> add, Func<Shape, Shape, string?> unmet)
    {
        Shape targetBefore = ShapeOf(target);
        double volumeBefore = model.Volume();
        Constraints constraints = part.Constraints;
        var relations = new List<Constraint>();
        string? problem = null;
        Exception? inner = null;
        bool switched = model.EnsureSynchronous();
        try
        {
            int existing = constraints.Count;
            Mark[] marks = faces.Select(MarkOf).ToArray();
            Constraint? hold = fixTarget ? TryFix(constraints, target) : null;
            for (int i = 0; i < faces.Length && problem is null; i++)
            {
                try
                {
                    // A solve can leave the other face objects stale (Add then fails with E_FAIL): find them again.
                    Face face = i == 0 ? faces[i] : Again(model, faces[i], marks[i]);
                    Face stay = i == 0 ? target : Geometries(relations[^1]) is [Face fresh, ..] ? fresh : target;
                    relations.Add(add(constraints, face, stay));
                }
                catch (Exception ex) when (Rejected(ex))
                {
                    (problem, inner) = ($"Solid Edge rejected {Which(i, faces.Length)} ({ComErrors.Unwrap(ex).Message.Trim()})", ex);
                }
            }

            // A later relation can disturb an earlier one, so all of them are checked once every face is related.
            for (int i = 0; problem is null && i < relations.Count; i++)
            {
                Face[] now = Geometries(relations[i]);
                problem = now.Length < 2 ? $"Solid Edge did not keep {Which(i, faces.Length)}"
                    : Moved(targetBefore, ShapeOf(now[0])) ? "the target moved instead of the faces"
                    : unmet(ShapeOf(now[1]), ShapeOf(now[0])) is { } why ? $"{Which(i, faces.Length)} does not meet it: {why}"
                    : null;
            }

            TryDelete(hold);
            if (problem is not null)
            {
                DeleteNew(constraints, existing);
            }
        }
        finally
        {
            Restore(model, switched);
        }

        double after = model.Volume();
        context.ModelChanged(model.Document, problem is null ? $"a {action} relation moved faces" : $"a {action} relation failed");
        if (problem is not null)
        {
            throw Failed(action, volumeBefore, after, problem, inner);
        }

        return new
        {
            relations = relations.Select(r => Describe(context, model, r, r.index)).ToArray(),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - volumeBefore),
            note = "Solid Edge clears the undo list when a relation is added: edit or delete the relation to change it.",
        };
    }

    private static string Which(int index, int count) => count == 1 ? "the face" : $"face {index + 1}";

    /// <summary>Holds faces in place: a fixed relation each (the face is its own target), which later relations and edits keep.</summary>
    private static object Fix(SeContext context, string? documentHandle, FaceRelationSpec spec)
    {
        if (spec.Faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("fix needs 'faces': the faces to hold in place (se_geometry faces).");
        }

        (ModelDocument model, PartDocument part) = Part(context, documentHandle, spec.Faces[0]);
        Face[] faces = spec.Faces.Select(h => FaceOf(context, model, h, "faces")).ToArray();
        Constraints constraints = part.Constraints;
        int existing = constraints.Count;
        var made = new List<Constraint>();
        Mark[] marks = faces.Select(MarkOf).ToArray();
        bool switched = model.EnsureSynchronous();
        try
        {
            for (int i = 0; i < faces.Length; i++)
            {
                Face face = i == 0 ? faces[i] : Again(model, faces[i], marks[i]);
                Array one = new object[] { face };
                try
                {
                    made.Add(constraints.Add(1, ref one, face, ConstraintTypeConstants.igRelationFixed, SolveTypeConstants.igSimple, SeedOptionConstants.igSeedSingle));
                }
                catch (Exception ex) when (Rejected(ex))
                {
                    DeleteNew(constraints, existing);
                    throw new ToolException("feature_failed", $"Solid Edge could not fix '{spec.Faces[i]}' ({ComErrors.Unwrap(ex).Message.Trim()}); no face was fixed.",
                        "A face that is already fixed cannot be fixed again (list shows the fixed relations). " + SynchronousHint, ex);
                }
            }
        }
        finally
        {
            Restore(model, switched);
        }

        return new
        {
            relations = made.Select(r => Describe(context, model, r, r.index)).ToArray(),
            count = constraints.Count,
        };
    }

    /// <summary>A new distance for an offset (or coplanar) relation: its face moves again.</summary>
    private static object Edit(SeContext context, string? documentHandle, FaceRelationSpec spec)
    {
        double distance = RequireDistance(spec.Distance, "edit");
        double metres = Units.MmToM(distance);
        (ModelDocument model, PartDocument part) = Part(context, documentHandle, spec.Relation);
        Constraints constraints = part.Constraints;
        (Constraint relation, int index) = Find(context, constraints, spec.Relation);
        if (relation.Type != ConstraintTypeConstants.igRelationOffset)
        {
            throw ToolException.InvalidArgument($"Relation {index} is {TypeName(relation)}; only offset and coplanar relations have a distance.",
                "Delete it and add the relation you need.");
        }

        double volumeBefore = model.Volume();
        string? problem = null;
        Exception? inner = null;
        bool switched = model.EnsureSynchronous();
        try
        {
            Face[] faces = Geometries(relation);
            if (faces.Length < 2)
            {
                throw new ToolException("feature_failed", $"Relation {index} has no face to move.", "Delete it and add it again.");
            }

            Shape targetBefore = ShapeOf(faces[0]);
            try
            {
                relation.EditOffsetValueByFace(metres, faces[1]);
            }
            catch (Exception ex) when (Rejected(ex))
            {
                (problem, inner) = ($"Solid Edge rejected the distance ({ComErrors.Unwrap(ex).Message.Trim()})", ex);
            }

            if (problem is null)
            {
                Face[] now = Geometries(relation);
                double reached = now.Length < 2 ? double.NaN : PlaneDistance(ShapeOf(now[0]), ShapeOf(now[1]));
                problem = double.IsNaN(reached) ? "its faces are gone"
                    : Moved(targetBefore, ShapeOf(now[0])) ? "the target moved instead of the face"
                    : Math.Abs(reached - metres) > Tolerance ? $"the face is {Geo.Mm(reached)} mm from the target instead of {distance}"
                    : null;
            }
        }
        finally
        {
            Restore(model, switched);
        }

        double after = model.Volume();
        context.ModelChanged(model.Document, "a face relation was edited");
        if (problem is not null)
        {
            throw Failed("edit", volumeBefore, after, problem, inner, $"Relation {index} stays; edit it again or delete it.");
        }

        return new
        {
            relation = Describe(context, model, relation, index),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - volumeBefore),
        };
    }

    /// <summary>Deletes a relation; its faces stay where they are.</summary>
    private static object Delete(SeContext context, string? documentHandle, FaceRelationSpec spec)
    {
        (ModelDocument model, PartDocument part) = Part(context, documentHandle, spec.Relation);
        Constraints constraints = part.Constraints;
        (Constraint relation, int index) = Find(context, constraints, spec.Relation);
        string type = TypeName(relation);
        int before = constraints.Count;
        bool switched = model.EnsureSynchronous();
        try
        {
            relation.Delete();
        }
        finally
        {
            Restore(model, switched);
        }

        return constraints.Count == before - 1
            ? new { deleted = index, type, count = constraints.Count, volume = Geo.Mm3(model.Volume()) }
            : throw new ToolException("feature_failed", $"Relation {index} is still there after deleting it.", "List the relations and try again.");
    }

    private static object ReadLiveRules(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        return new { document = context.DocumentHandle(model.Document), rules = ReadRules(model), note = LiveRulesNote };
    }

    /// <summary>Sets live rules from 'rules' and 'maintain', or from 'values' (a rules object that live_rules returned).</summary>
    private static object SetLiveRules(SeContext context, string? documentHandle, FaceRelationSpec spec)
    {
        (int Index, bool Maintain)[] wanted = (spec.Rules, spec.Values) switch
        {
            ({ Length: > 0 }, null or { Count: 0 }) => spec.Rules.Select(r => (RuleIndex(r), spec.Maintain)).ToArray(),
            (null or { Length: 0 }, { Count: > 0 }) => spec.Values.Select(v => (RuleIndex(v.Key), v.Value)).ToArray(),
            ({ Length: > 0 }, { Count: > 0 }) => throw ToolException.InvalidArgument("Pass either 'rules' with 'maintain' or 'values', not both."),
            _ => throw ToolException.InvalidArgument("set_live_rules needs 'rules' (with 'maintain') or 'values' (rule: true|false).",
                $"Rules: {string.Join(", ", LiveRules)}."),
        };

        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Dictionary<string, bool> previous = ReadRules(model);
        foreach ((int index, bool maintain) in wanted)
        {
            WriteRule(model, index, maintain);
        }

        Dictionary<string, bool> now = ReadRules(model);
        string[] ignored = wanted.Where(w => now[LiveRules[w.Index - 1]] != w.Maintain).Select(w => LiveRules[w.Index - 1]).Distinct().ToArray();
        if (ignored.Length > 0)
        {
            throw new ToolException("not_applied", $"Solid Edge did not change {string.Join(", ", ignored)}.",
                "Check the Live Rules panel of synchronous modelling.");
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            changed = Changes(previous, now),
            previous,
            rules = now,
            note = LiveRulesNote,
        };
    }

    private static object RestoreLiveRules(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        Dictionary<string, bool> previous = ReadRules(model);
        switch (model.Document)
        {
            case PartDocument part:
                part.RestoreLiveRules();
                break;
            case SheetMetalDocument sheet:
                sheet.RestoreLiveRules();
                break;
        }

        Dictionary<string, bool> now = ReadRules(model);
        return new
        {
            document = context.DocumentHandle(model.Document),
            changed = Changes(previous, now),
            previous,
            rules = now,
            note = "Solid Edge restores the defaults of the live rules except offset, which stays as it is. " + LiveRulesNote,
        };
    }

    private static object[] Changes(Dictionary<string, bool> previous, Dictionary<string, bool> now) =>
        LiveRules.Where(r => previous[r] != now[r]).Select(r => (object)new { rule = r, previous = previous[r], value = now[r] }).ToArray();

    private static Dictionary<string, bool> ReadRules(ModelDocument model)
    {
        var rules = new Dictionary<string, bool>();
        for (int i = 1; i <= LiveRules.Count; i++)
        {
            bool maintain;
            switch (model.Document)
            {
                case PartDocument part:
                    part.GetLiveRules((LiveRulesConstants)i, out maintain);
                    break;
                default:
                    ((SheetMetalDocument)model.Document).GetLiveRules((LiveRulesConstants)i, out maintain);
                    break;
            }

            rules[LiveRules[i - 1]] = maintain;
        }

        return rules;
    }

    private static void WriteRule(ModelDocument model, int index, bool maintain)
    {
        switch (model.Document)
        {
            case PartDocument part:
                part.SetLiveRules((LiveRulesConstants)index, maintain);
                break;
            default:
                ((SheetMetalDocument)model.Document).SetLiveRules((LiveRulesConstants)index, maintain);
                break;
        }
    }

    private static int RuleIndex(string name)
    {
        int index = LiveRules.ToList().IndexOf(name);
        return index >= 0
            ? index + 1
            : throw ToolException.InvalidArgument($"Unknown live rule '{name}'.", $"Use: {string.Join(", ", LiveRules)}.");
    }

    private static double RequireDistance(double? distance, string action)
    {
        double value = Units.RequireFinite(distance ?? throw ToolException.InvalidArgument($"{action} needs 'distance' in mm (0 = coplanar)."), "distance");
        return value >= 0 ? value : throw ToolException.InvalidArgument("'distance' cannot be negative: the face stays on the side of the target it is on.");
    }

    private static (ModelDocument Model, PartDocument Part) Part(SeContext context, string? documentHandle, string? related)
    {
        ModelDocument model = related is null || int.TryParse(related, out _)
            ? ModelDocument.Get(context, documentHandle)
            : ModelDocument.For(context, documentHandle, related);
        return model.Document is PartDocument part
            ? (model, part)
            : throw new ToolException("wrong_document_type", "Face relations exist in parts only (the sheet metal API has none).",
                "Open or create a part; the live rule actions work in sheet metal too.");
    }

    private static (ModelDocument Model, PartDocument Part, Face[] Faces, Face Target) Operands(SeContext context, string? documentHandle,
        FaceRelationSpec spec, string action)
    {
        if (spec.Faces is not { Length: > 0 } || spec.Target is null)
        {
            throw ToolException.InvalidArgument($"{action} needs 'faces' (the faces that move) and 'target' (the face that stays).",
                "Take face handles from se_geometry faces.");
        }

        (ModelDocument model, PartDocument part) = Part(context, documentHandle, spec.Target);
        Face target = FaceOf(context, model, spec.Target, "target");
        Face[] faces = spec.Faces.Select(h => FaceOf(context, model, h, "faces")).ToArray();
        if (faces.Any(f => Late.SameObject(f, target)))
        {
            throw ToolException.InvalidArgument("The target is also in 'faces'.", "The target is the face that stays; 'faces' are the ones that move.");
        }

        return (model, part, faces, target);
    }

    private static Face FaceOf(SeContext context, ModelDocument model, string handle, string argument)
    {
        object value = context.Handles.Resolve(handle, argument);
        if (value is not Face face)
        {
            throw ToolException.InvalidArgument($"'{handle}' in '{argument}' is not a face of a part.", "Take face handles from se_geometry faces.");
        }

        if (!Late.SameObject(context.DocumentOf(handle), model.Document))
        {
            throw ToolException.InvalidArgument($"'{handle}' belongs to another document.", "All faces of a relation are in the same part.");
        }

        return face;
    }

    private static (Constraint Relation, int Index) Find(SeContext context, Constraints constraints, string? relation)
    {
        int count = constraints.Count;
        if (string.IsNullOrWhiteSpace(relation))
        {
            throw ToolException.InvalidArgument("'relation' is required: a handle or index from list.");
        }

        if (int.TryParse(relation, out int index))
        {
            return index >= 1 && index <= count
                ? (constraints.Item(index), index)
                : throw ToolException.InvalidArgument($"There is no relation {index}: the part has {count}.", "List the relations first.");
        }

        // Constraints.Item returns a new object on every call, so a handle is matched by the index it reports now
        // (it follows deletions of earlier relations); a deleted relation's object fails with RPC_E_DISCONNECTED.
        if (context.Handles.Resolve(relation, "relation") is not Constraint held)
        {
            throw ToolException.InvalidArgument($"'{relation}' is not a face relation.", "Use a handle or index from list.");
        }

        int current;
        try
        {
            current = held.index;
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or InvalidComObjectException)
        {
            current = 0;
        }

        return current >= 1 && current <= constraints.Count
            ? (constraints.Item(current), current)
            : throw new ToolException("handle_expired", $"'{relation}' is no longer a relation of the part (deleted).",
                "List the relations again and use the new handle or index.");
    }

    /// <summary>A single COM call that Solid Edge rejected, or whose object it removed, while it keeps running.</summary>
    private static bool Rejected(Exception ex) => ComErrors.IsCallFailure(ComErrors.Unwrap(ex)) || ComErrors.IsObjectRemoved(ex);

    private static object Describe(SeContext context, ModelDocument model, Constraint relation, int index)
    {
        Face[] faces = Geometries(relation);
        bool offset = relation.Type == ConstraintTypeConstants.igRelationOffset;
        double? distance = offset ? Geo.Mm(relation.OffsetValue) : null;
        return new
        {
            index,
            handle = context.Register(relation, "face_relation", model.Document),
            type = TypeName(relation),
            distance,
            target = faces.Length > 0 ? context.Register(faces[0], "face", model.Document) : null,
            faces = faces.Skip(1).Where(f => !Late.SameObject(f, faces[0])).Select(f => context.Register(f, "face", model.Document)).ToArray(),
        };
    }

    /// <summary>The relation type as the Relate commands name it: an offset of 0 is coplanar.</summary>
    private static string TypeName(Constraint relation) => relation.Type switch
    {
        ConstraintTypeConstants.igRelationOffset when Math.Abs(relation.OffsetValue) < 1e-12 => "coplanar",
        ConstraintTypeConstants.igRelationConcentric => "concentric",
        ConstraintTypeConstants.igRelationCoincident => "coincident",
        ConstraintTypeConstants.igRelationParallel => "parallel",
        ConstraintTypeConstants.igRelationPerpendicular => "perpendicular",
        ConstraintTypeConstants.igRelationTangent => "tangent",
        ConstraintTypeConstants.igRelationRigid => "rigid",
        ConstraintTypeConstants.igRelationFixed => "fixed",
        ConstraintTypeConstants.igRelationOffset => "offset",
        _ => relation.Type.ToString(),
    };

    /// <summary>The faces of a relation: the target first, then the faces that move (fresh objects after a solve).</summary>
    private static Face[] Geometries(Constraint relation)
    {
        Array geometries = new object[relation.GeometryCount + 1];
        relation.GetGeometries(ref geometries);
        return geometries.Cast<object?>().OfType<Face>().ToArray();
    }

    private static Constraint? TryFix(Constraints constraints, Face face)
    {
        Array one = new object[] { face };
        try
        {
            return constraints.Add(1, ref one, face, ConstraintTypeConstants.igRelationFixed, SolveTypeConstants.igSimple, SeedOptionConstants.igSeedSingle);
        }
        catch (Exception ex) when (Rejected(ex))
        {
            // Already fixed by a relation of the part: it holds the target anyway.
            return null;
        }
    }

    /// <summary>Deletes a relation made by this call; one that went with its faces fails with RPC_E_DISCONNECTED.</summary>
    private static void TryDelete(Constraint? relation)
    {
        try
        {
            relation?.Delete();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or InvalidComObjectException)
        {
            // Already gone.
        }
    }

    /// <summary>Deletes the relations made by this call (Solid Edge appends them), newest first.</summary>
    private static void DeleteNew(Constraints constraints, int existing)
    {
        for (int i = constraints.Count; i > existing; i--)
        {
            TryDelete(constraints.Item(i));
        }
    }

    private static ToolException Failed(string action, double volumeBefore, double after, string problem, Exception? inner, string? next = null)
    {
        string state = Math.Abs(after - volumeBefore) > 1e-12
            ? $" Solid Edge changed the model before failing (volume {Geo.Mm3(volumeBefore)} -> {Geo.Mm3(after)} mm³) and it cannot be undone after a relation: check it with se_geometry or se_view."
            : " The model did not change.";
        return new ToolException("feature_failed", $"The {action} relation was not made: {problem}.{state}",
            next ?? "Faces that would merge with others or swallow a feature cannot keep a relation. " + SynchronousHint, inner);
    }

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

    /// <summary>What finds a face again after a solve: its surface and the middle of its range.</summary>
    private sealed record Mark(Shape Shape, double[] Middle);

    private static Mark MarkOf(Face face) => new(ShapeOf(face), Middle(face));

    private static double[] Middle(Face face)
    {
        Array min = new double[3];
        Array max = new double[3];
        face.GetRange(ref min, ref max);
        double[] a = Vector(min);
        double[] b = Vector(max);
        return [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2];
    }

    /// <summary>The face of the model now on the same surface, facing the same way, nearest to where it was; the old object if none.</summary>
    private static Face Again(ModelDocument model, Face face, Mark mark)
    {
        Face? best = null;
        double nearest = double.MaxValue;
        foreach ((_, SolidEdgeGeometry.Body body) in model.Bodies())
        {
            foreach (Face candidate in Late.Items(body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]).OfType<Face>())
            {
                Shape shape = ShapeOf(candidate);
                if (shape.Kind != mark.Shape.Kind || Dot(shape.Direction, mark.Shape.Direction) < 1 - DotTolerance || Moved(mark.Shape, shape))
                {
                    continue;
                }

                double distance = Length(Minus(Middle(candidate), mark.Middle));
                if (distance < nearest)
                {
                    (best, nearest) = (candidate, distance);
                }
            }
        }

        return best ?? face;
    }

    /// <summary>A face's surface: plane (a point and the normal), cylinder or cone (a point on the axis, the axis and the radius).</summary>
    private sealed record Shape(string Kind, double[] Point, double[] Direction, double Radius);

    private static Shape ShapeOf(Face face)
    {
        Array point = new double[3];
        Array direction = new double[3];
        switch (face.Geometry)
        {
            case Plane plane:
                plane.GetPlaneData(ref point, ref direction);
                return new Shape("plane", Vector(point), Unit(Vector(direction)), 0);
            case Cylinder cylinder:
                cylinder.GetCylinderData(ref point, ref direction, out double radius);
                return new Shape("cylinder", Vector(point), Unit(Vector(direction)), radius);
            case Cone cone:
                cone.GetConeData(ref point, ref direction, out double coneRadius, out _, out _);
                return new Shape("cone", Vector(point), Unit(Vector(direction)), coneRadius);
            default:
                return new Shape("other", [0, 0, 0], [0, 0, 0], 0);
        }
    }

    private static Shape Require(Shape shape, string kind, string handle, string action) => shape.Kind == kind
        ? shape
        : throw ToolException.InvalidArgument($"{action} takes planar faces; '{handle}' is a {shape.Kind}.");

    private static string? Unsupported(ConstraintTypeConstants type, Shape face, Shape target)
    {
        bool Axial(Shape s) => s.Kind is "cylinder" or "cone";
        bool Known(Shape s) => s.Kind == "plane" || Axial(s);
        return type switch
        {
            ConstraintTypeConstants.igRelationParallel or ConstraintTypeConstants.igRelationPerpendicular when !Known(face) || !Known(target) =>
                "only planes, cylinders and cones have a direction",
            ConstraintTypeConstants.igRelationTangent when !(face.Kind == "cylinder" && target.Kind is "plane" or "cylinder"
                || face.Kind == "plane" && target.Kind == "cylinder") => "tangent takes a cylinder and a plane, or two cylinders",
            ConstraintTypeConstants.igRelationTangent when face.Kind == "cylinder" && target.Kind == "cylinder" && !Parallel(face.Direction, target.Direction) =>
                "the cylinders' axes are not parallel",
            ConstraintTypeConstants.igRelationConcentric when !Axial(face) || !Axial(target) => "concentric takes cylinders or cones",
            _ => null,
        };
    }

    /// <summary>Why a face does not meet the relation with the target, or null when it does.</summary>
    private static string? Unmet(ConstraintTypeConstants type, Shape face, Shape target)
    {
        double dot = Math.Abs(Dot(face.Direction, target.Direction));
        bool crossed = face.Kind == "plane" != (target.Kind == "plane");
        switch (type)
        {
            case ConstraintTypeConstants.igRelationParallel:
                return (crossed ? dot <= DotTolerance : dot >= 1 - DotTolerance) ? null : $"the angle is {Angle(face, target):0.###} degrees";
            case ConstraintTypeConstants.igRelationPerpendicular:
                return (crossed ? dot >= 1 - DotTolerance : dot <= DotTolerance) ? null : $"the angle is {Angle(face, target):0.###} degrees";
            case ConstraintTypeConstants.igRelationConcentric:
                double offAxis = LineDistance(face.Point, target);
                return dot >= 1 - DotTolerance && offAxis <= Tolerance ? null : $"its axis is {Geo.Mm(offAxis)} mm from the target's";
            case ConstraintTypeConstants.igRelationTangent:
                if (face.Kind == "cylinder" && target.Kind == "cylinder")
                {
                    double apart = LineDistance(face.Point, target);
                    return Math.Abs(apart - (face.Radius + target.Radius)) <= Tolerance || Math.Abs(apart - Math.Abs(face.Radius - target.Radius)) <= Tolerance
                        ? null
                        : $"the axes are {Geo.Mm(apart)} mm apart";
                }

                (Shape round, Shape flat) = face.Kind == "cylinder" ? (face, target) : (target, face);
                double gap = Math.Abs(Dot(flat.Direction, Minus(round.Point, flat.Point)));
                return Math.Abs(Dot(round.Direction, flat.Direction)) <= DotTolerance && Math.Abs(gap - round.Radius) <= Tolerance
                    ? null
                    : $"the axis is {Geo.Mm(gap)} mm from the plane, the radius {Geo.Mm(round.Radius)}";
            default:
                return null;
        }
    }

    /// <summary>Whether the target moved: another plane, axis or radius.</summary>
    private static bool Moved(Shape before, Shape after)
    {
        if (before.Kind != after.Kind || !Parallel(before.Direction, after.Direction))
        {
            return true;
        }

        return before.Kind == "plane"
            ? PlaneDistance(after, before) > Tolerance
            : LineDistance(before.Point, after) > Tolerance || Math.Abs(before.Radius - after.Radius) > Tolerance;
    }

    private static double PlaneDistance(Shape plane, Shape other) => Math.Abs(Dot(plane.Direction, Minus(other.Point, plane.Point)));

    private static double LineDistance(double[] point, Shape axis) => Length(Cross(Minus(point, axis.Point), axis.Direction));

    private static double Angle(Shape a, Shape b) => Units.RadToDeg(Math.Acos(Math.Clamp(Math.Abs(Dot(a.Direction, b.Direction)), 0, 1)));

    private static bool Parallel(double[] a, double[] b) => Math.Abs(Dot(a, b)) >= 1 - DotTolerance;

    private static double[] Vector(Array values) => [.. Geo.Doubles(values)];

    private static double[] Unit(double[] v)
    {
        double length = Length(v);
        return length > 0 ? v.Select(c => c / length).ToArray() : v;
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Minus(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Cross(double[] a, double[] b) => [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double Length(double[] v) => Math.Sqrt(Dot(v, v));
}
