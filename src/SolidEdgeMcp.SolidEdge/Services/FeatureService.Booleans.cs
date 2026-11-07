// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Body = SolidEdgeGeometry.Body;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_body does: the action and its inputs. Lengths in mm.</summary>
public sealed record BodySpec(string Action, string[]? Targets = null, string[]? Tools = null, string[]? Bodies = null,
    string Side = "auto", double? Thickness = null, double Clearance = 0, bool Thicken = true, string[]? Paths = null, string? Report = null,
    string? Assembly = null, bool Overwrite = false, bool FullBody = false);

/// <summary>
/// Operations between bodies (ordered modelling): Boolean subtract, intersect and unite, emboss, and design bodies turned
/// into construction bodies and back. A closed construction body turned into a design body is how a part gets two design
/// bodies that overlap, which a split cannot give.
/// </summary>
public static partial class FeatureService
{
    public static readonly IReadOnlyList<string> BodyActions = ["list", "subtract", "intersect", "unite", "emboss", "to_construction", "to_design", "divide", "heal", "publish"];

    public static object BodyOperation(SeContext context, string? documentHandle, BodySpec spec) => spec.Action switch
    {
        "list" => ListBodies(context, documentHandle),
        "subtract" => SubtractBodies(context, documentHandle, spec),
        "intersect" => IntersectBodies(context, documentHandle, spec),
        "unite" => UniteBodies(context, documentHandle, spec),
        "emboss" => Emboss(context, documentHandle, spec),
        "to_construction" => ToConstruction(context, documentHandle, spec.Bodies),
        "to_design" => ToDesign(context, documentHandle, spec.Bodies),
        "divide" => Divide(context, documentHandle, spec.Tools, spec.Paths),
        "heal" => Heal(context, documentHandle, spec.Report, spec.FullBody),
        "publish" => PublishBodies(context, documentHandle, spec.Bodies, spec.Assembly, spec.Overwrite),
        _ => throw ToolException.InvalidArgument($"Unknown action '{spec.Action}'.", $"Use one of: {string.Join(", ", BodyActions)}."),
    };

    /// <summary>
    /// Heals and optimizes the design bodies, as the Optimize command (useful on imported bodies): Clean body always, then
    /// Simplify body only, or Full body optimization with <paramref name="fullBody"/>. Verified in Solid Edge 2026:
    /// Model.HealAndOptimizeBodyEx(heal, optimize, clean, fullBody, report, file) on a body imported from STEP kept it
    /// valid and wrote the report, face and edge counts by geometry type before and after. The Geometry Inspector queries
    /// (Model.GetFaultsOfBody, GetSmallFacesOfBody, GetSliverFacesOfBody) fail through the API: E_POINTER with the
    /// interop, DISP_E_TYPEMISMATCH through IDispatch whatever the output arrays.
    /// </summary>
    private static object Heal(SeContext context, string? documentHandle, string? report, bool fullBody)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        string? kept = report is null ? null : context.Paths.Check(report, "report");
        if (kept is not null && File.Exists(kept))
        {
            throw new ToolException("file_exists", $"'{kept}' already exists.", "Give a new file for the report.");
        }

        IReadOnlyList<Model> models = model.MeasuredModels();
        if (model.TryBodies() is not { Count: > 0 })
        {
            throw new ToolException("no_body", "The part has no design body to heal.");
        }

        double before = model.Volume();
        var faces = new Dictionary<string, (int Before, int After)>();
        var edges = new Dictionary<string, (int Before, int After)>();
        var text = new List<string>();
        foreach (Model owner in models)
        {
            string file = Path.Combine(context.Paths.ExchangeFolder, $"heal_{Guid.NewGuid():N}.txt");
            try
            {
                owner.HealAndOptimizeBodyEx(true, true, true, fullBody, true, file);
                if (File.Exists(file))
                {
                    string[] lines = File.ReadAllLines(file);
                    text.AddRange(lines);
                    HealCounts(lines, faces, edges);
                }
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                throw new ToolException("feature_failed", $"Solid Edge could not heal the body: {ex.Message.Trim()}", "Check the body with se_physical; a mesh body cannot be healed.", ex);
            }
            finally
            {
                File.Delete(file);
            }
        }

        if (kept is not null)
        {
            File.WriteAllLines(kept, text);
        }

        context.ModelChanged(model.Document, "the bodies were healed and optimized");
        double after = model.Volume();
        static object Rows(Dictionary<string, (int Before, int After)> counts) =>
            counts.Where(c => c.Value.Before != 0 || c.Value.After != 0).Select(c => new { type = c.Key, before = c.Value.Before, after = c.Value.After }).ToArray();
        return new
        {
            document = context.DocumentHandle(model.Document),
            bodies = models.Count,
            fullBody,
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            faces = new { before = faces.Values.Sum(v => v.Before), after = faces.Values.Sum(v => v.After), types = Rows(faces) },
            edges = new { before = edges.Values.Sum(v => v.Before), after = edges.Values.Sum(v => v.After), types = Rows(edges) },
            report = kept,
        };
    }

    /// <summary>Adds the counts of a heal report (rows "Plane   8   8" under "Face Count" and "Edge Count") to the totals.</summary>
    internal static void HealCounts(IEnumerable<string> lines, Dictionary<string, (int Before, int After)> faces, Dictionary<string, (int Before, int After)> edges)
    {
        Dictionary<string, (int Before, int After)>? section = null;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("Face Count", StringComparison.OrdinalIgnoreCase))
            {
                section = faces;
                continue;
            }

            if (trimmed.StartsWith("Edge Count", StringComparison.OrdinalIgnoreCase))
            {
                section = edges;
                continue;
            }

            System.Text.RegularExpressions.Match row = System.Text.RegularExpressions.Regex.Match(trimmed, @"^([A-Za-z][A-Za-z\- ]*?)\s{2,}(\d+)\s+(\d+)$");
            if (section is null || !row.Success || row.Groups[1].Value.Equals("Total", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string type = row.Groups[1].Value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            (int b, int a) = section.GetValueOrDefault(type);
            section[type] = (b + int.Parse(row.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                a + int.Parse(row.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The design bodies and the construction bodies (closed solids, surfaces and curves) of a part.</summary>
    private static object ListBodies(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        return new
        {
            document = context.DocumentHandle(model.Document),
            design = model.TryModel() is null ? [] : InspectService.DescribeBodies(context, model),
            construction = ConstructionBodies(model).Select(c => DescribeConstruction(context, model, c.Name, c.Body)).ToArray(),
        };
    }

    /// <summary>
    /// Removes the tools from the targets. Verified: Subtracts.Add(targets, tools, directions, 0, 0) on the model of the
    /// target; a closed tool (direction 0) removes what it overlaps and a tool design body stays; an open surface removes
    /// one side of it: 2 (igNormalSide) keeps the side its face normal points to, 1 the other one, 0 fails.
    /// </summary>
    private static object SubtractBodies(SeContext context, string? documentHandle, BodySpec spec)
    {
        (ModelDocument model, Model owner, object[] targets, Body[] tools, bool switched) = Operands(context, documentHandle, spec, "subtract");
        int[] directions = tools.Select(t => t.IsSolid ? 0 : spec.Side switch
        {
            "normal" => (int)ReferenceElementConstants.igReverseNormalSide,
            "reverse" => (int)ReferenceElementConstants.igNormalSide,
            _ => throw ToolException.InvalidArgument("A tool of 'subtract' is an open surface: 'side' must say which side of it loses its material (normal or reverse).",
                "normal = the side its normal points to (se_geometry faces of the surface give the normal); reverse = the other side."),
        }).ToArray();
        double before = model.Volume();
        Array targetArray = targets;
        Array toolArray = tools;
        Array directionArray = directions;
        object feature = Create("subtract", () => owner.Subtracts.Add(targetArray.Length, ref targetArray, toolArray.Length, ref toolArray, ref directionArray,
            SETargetDesignBodyOption.igCreateMultipleDesignBodiesOnNonManifoldOption, SETargetConstructionBodyOption.igCreateMultipleConstructionBodiesOnNonManifoldOption));
        (double after, string? status) = Check(context, model, feature, before, -1, null, null,
            hint: "A closed tool must overlap the target; an open surface must cross it (try the other 'side').");
        return BodyResult(context, model, feature, before, after, switched, status);
    }

    /// <summary>
    /// Keeps only what the targets share with the tools. Verified: Intersects.Add(targets, tools, 0, 0) on the model of the
    /// target; the tools are used up (a 100 x 60 x 20 block and a 40 x 20 x 30 box that overlap by 20 x 20 x 20 leave 8000 mm³).
    /// </summary>
    private static object IntersectBodies(SeContext context, string? documentHandle, BodySpec spec)
    {
        (ModelDocument model, Model owner, object[] targets, Body[] tools, bool switched) = Operands(context, documentHandle, spec, "intersect");
        RequireClosed(context, spec.Tools!, tools, "intersect");
        double before = model.Volume();
        Array targetArray = targets;
        Array toolArray = tools;
        object feature = Create("intersect", () => owner.Intersects.Add(targetArray.Length, ref targetArray, toolArray.Length, ref toolArray,
            SETargetDesignBodyOption.igCreateMultipleDesignBodiesOnNonManifoldOption, SETargetConstructionBodyOption.igCreateMultipleConstructionBodiesOnNonManifoldOption));
        const string Hint = "The tools must overlap the targets.";
        (double after, string? status) = Check(context, model, feature, before, -1, null, null, hint: Hint);
        if (after <= VolumeTolerance)
        {
            TryDelete(feature);
            throw new ToolException("feature_failed", "The intersection was deleted because it left no material: the bodies do not overlap.", Hint);
        }

        return BodyResult(context, model, feature, before, after, switched, status);
    }

    /// <summary>
    /// Adds closed construction bodies to one design body; they stay as construction bodies (the Boolean feature of the
    /// Surfacing tab). Verified: BooleanFeatures.Add(n, tools, seBooleanUnite) on the model of the target, with the body of a
    /// closed surface or the surface itself: a 100 x 60 x 20 block and a 40 x 20 x 30 closed surface that overlaps it by
    /// 20 x 20 x 20 give 136000 mm³. Its subtract and intersect give what Subtracts and Intersects give; a reference plane as
    /// the tool (seBooleanPlaneFront or Back) is refused with E_INVALIDARG whatever the PlaneSide.
    /// </summary>
    private static object UniteBodies(SeContext context, string? documentHandle, BodySpec spec)
    {
        (ModelDocument model, Model owner, object[] targets, Body[] tools, bool switched) = Operands(context, documentHandle, spec, "unite");
        if (targets.Length != 1)
        {
            throw ToolException.InvalidArgument("unite adds the tools to one design body.", "Pass one design body in 'targets' (default: the first).");
        }

        IReadOnlyList<(Model Model, Body Body)> design = model.Bodies();
        for (int i = 0; i < tools.Length; i++)
        {
            if (design.Any(d => Late.SameObject(d.Body, tools[i])))
            {
                throw ToolException.InvalidArgument($"'{spec.Tools![i]}' is a design body; unite takes construction bodies.",
                    "To join design bodies into one, use se_part_feature unite.");
            }
        }

        RequireClosed(context, spec.Tools!, tools, "unite");
        double before = model.Volume();
        object[] toolArray = [.. tools];
        object feature = Create("unite", () => owner.BooleanFeatures.Add(toolArray.Length, toolArray, BooleanFeatureConstants.seBooleanUnite));
        (double after, string? status) = Check(context, model, feature, before, 1, null, null,
            hint: "The tools must be closed construction bodies (se_body list, kind 'solid') that reach outside the target.");
        return BodyResult(context, model, feature, before, after, switched, status, construction: true);
    }

    /// <summary>
    /// Presses closed tools into one thin target. Verified on a 100 x 60 x 2 plate with a cylinder of radius 10, 10 high,
    /// standing on it: thickened, a cup with walls of the thickness around the tool (clearance 0: + pi x 440 = 1382.30 mm³;
    /// clearance 0.5: 1517.39); on the other side it goes to the plate's other face (+ pi x 88); not thickened, the tool's
    /// shape is only cut out (- pi x 200).
    /// </summary>
    private static object Emboss(SeContext context, string? documentHandle, BodySpec spec)
    {
        (ModelDocument model, Model owner, object[] targets, Body[] tools, bool switched) = Operands(context, documentHandle, spec, "emboss");
        if (targets.Length != 1)
        {
            throw ToolException.InvalidArgument("emboss works on one target body.", "Pass one design body in 'targets' (default: the first).");
        }

        RequireClosed(context, spec.Tools!, tools, "emboss");
        double thickness = spec.Thicken
            ? Units.RequirePositive(spec.Thickness ?? throw ToolException.InvalidArgument("emboss needs 'thickness': the wall in mm (or thicken: false to only cut the tools' shape)."), "thickness")
            : spec.Thickness ?? 0;
        double clearance = spec.Clearance >= 0 ? spec.Clearance : throw ToolException.InvalidArgument("'clearance' cannot be negative.");
        bool defaultSide = spec.Side switch
        {
            "auto" or "normal" => true,
            "reverse" => false,
            _ => throw ToolException.InvalidArgument($"Unknown side '{spec.Side}'.", "Use auto, normal or reverse."),
        };
        double before = model.Volume();
        Array toolArray = tools;
        object feature = Create("emboss", () => owner.EmbossFeatures.Add(targets[0], toolArray.Length, ref toolArray, spec.Thicken, defaultSide,
            Units.MmToM(clearance), Units.MmToM(thickness)));
        (double after, string? status) = Check(context, model, feature, before, 0, null, null,
            hint: "The tools must be closed bodies that cross the target; with thicken, the target must be thin enough for the wall.");
        return BodyResult(context, model, feature, before, after, switched, status);
    }

    /// <summary>
    /// Turns design bodies into construction bodies, which mass properties and drawings leave out. Verified:
    /// Constructions.ToggleToConstructions.Add(n, bodies); the body moves to Constructions (a ConstructionModel).
    /// </summary>
    private static object ToConstruction(SeContext context, string? documentHandle, string[]? bodies)
    {
        if (bodies is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("to_construction needs 'bodies': design body handles (se_body list).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, bodies[0]);
        bool switched = model.EnsureOrdered();
        IReadOnlyList<(Model Model, Body Body)> all = RequireBodies(model, 1);
        (_, object[] chosen) = Owned(context, all, bodies);
        if (chosen.Length == all.Count)
        {
            throw ToolException.InvalidArgument("to_construction would leave the part without a design body.",
                "Keep at least one design body: turn a closed construction body into one first (to_design).");
        }

        double before = model.Volume();
        Array array = chosen;
        object feature = Create("toggle to construction", () => ConstructionsOf(model).ToggleToConstructions.Add(array.Length, ref array));
        (double after, string? status) = Check(context, model, feature, before, -1, null, null, hint: "Pass design bodies from se_body list.");
        return BodyResult(context, model, feature, before, after, switched, status, construction: true);
    }

    /// <summary>
    /// Turns closed construction bodies into design bodies. Verified: Models.ToggleToDesigns.Add(n, bodies) with the Body
    /// of a construction (a closed extruded surface with end caps, or a body made a construction); it can overlap the other
    /// design bodies. An open surface gives a failed feature, so it is refused first.
    /// </summary>
    private static object ToDesign(SeContext context, string? documentHandle, string[]? bodies)
    {
        if (bodies is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("to_design needs 'bodies': closed construction bodies (se_body list) or closed surfaces (se_surface).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, bodies[0]);
        bool switched = model.EnsureOrdered();
        Body[] chosen = bodies.Select(h => BodyOf(context, h, "bodies")).ToArray();
        IReadOnlyList<(Model Model, Body Body)> design = model.TryModel() is null ? [] : model.Bodies();
        for (int i = 0; i < chosen.Length; i++)
        {
            if (design.Any(d => Late.SameObject(d.Body, chosen[i])))
            {
                throw ToolException.InvalidArgument($"'{bodies[i]}' is already a design body.");
            }
        }

        RequireClosed(context, bodies, chosen, "to_design");
        double before = model.Volume();
        Array array = chosen;
        object feature = Create("toggle to design", () => model.Models.ToggleToDesigns.Add(array.Length, ref array));
        (double after, string? status) = Check(context, model, feature, before, 1, null, null, hint: "Pass closed construction bodies (se_body list, kind 'solid').");
        return BodyResult(context, model, feature, before, after, switched, status, construction: true);
    }

    /// <summary>
    /// Divides a saved part into part files, one per region that a reference plane or a construction surface cuts it into.
    /// Verified: DividedParts.Add(cutter, seDividedPartCutNormal, path of the first piece) makes one DividedPart per region
    /// (a plane across a block gives 2), each named by DocumentName; Populate writes each file, a part with a linked copy of
    /// its region (CopiedPart); saving the part afterwards leaves the pieces up to date (otherwise they read out of date).
    /// So the part must be saved and unchanged before: the save at the end then stores only the division.
    /// </summary>
    private static object Divide(SeContext context, string? documentHandle, string[]? tools, string[]? paths)
    {
        if (tools is not { Length: 1 })
        {
            throw ToolException.InvalidArgument("divide needs 'tools': one reference plane ('1'-'3' or refplane handle) or construction surface (se_surface) that cuts the part.");
        }

        string handle = tools[0];
        bool planeIndex = int.TryParse(handle, out _);
        ModelDocument model = planeIndex ? ModelDocument.Get(context, documentHandle) : ModelDocument.For(context, documentHandle, handle);
        object cutter = planeIndex ? model.ResolvePlaneSource(context, handle, "tools") : context.Handles.Resolve(handle, "tools");
        if (cutter is not RefPlane && (cutter is Face || FacesOf(cutter).Length == 0))
        {
            throw ToolException.InvalidArgument($"'{handle}' is not a reference plane or a construction surface.",
                "Pass a reference plane ('1'-'3', se_ref_plane) or a surface (se_surface); for a face, make a plane on it with se_ref_plane offset 0.");
        }

        string source = Late.TryGetString(model.Document, "FullName") is { Length: > 0 } fullName && Path.IsPathRooted(fullName)
            ? context.Paths.Check(fullName, "document path")
            : throw new ToolException("never_saved", $"'{model.Document.Name}' has never been saved.",
                "Save it in an allowed folder first (se_documents save_as): the pieces are linked to the saved part.");
        if (model.Document.ReadOnly)
        {
            throw new ToolException("read_only", $"'{model.Document.Name}' is open read-only.", "Dividing saves the part: open a copy that can be written.");
        }

        if (model.Document.Dirty)
        {
            throw new ToolException("unsaved_changes", $"'{model.Document.Name}' has unsaved changes.",
                "Save it first (se_documents save): dividing saves the part, and only the division should be saved then.");
        }

        object parts = Late.Get(model.Document, "DividedParts")!;
        if (Late.Count(parts) > 0)
        {
            throw ToolException.InvalidArgument($"'{model.Document.Name}' is already divided.", "Divide a copy of the part (se_documents save_as with copy).");
        }

        RequireBodies(model, 1);
        string extension = Path.GetExtension(source);
        string PieceFile(int index)
        {
            string path = context.Paths.Check(paths is null
                ? Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_{index + 1}{extension}")
                : paths[index], "paths");
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidArgument($"The pieces of '{model.Document.Name}' are {extension} files; got '{path}'.");
            }

            return File.Exists(path)
                ? throw new ToolException("file_exists", $"'{path}' already exists.", "Pass other 'paths' (one per piece) or remove the files.")
                : path;
        }

        if (paths is { Length: 0 })
        {
            throw ToolException.InvalidArgument("'paths' must list one file per piece.");
        }

        string first = PieceFile(0);
        CreateDivision(model, parts, cutter, first);
        int count = Late.Count(parts);
        var files = new List<string> { first };
        try
        {
            if (paths is not null && paths.Length != count)
            {
                throw ToolException.InvalidArgument($"The cut makes {count} pieces, but 'paths' lists {paths.Length}.", $"Pass {count} paths, or none for <part>_1 to _{count}.");
            }

            for (int i = 1; i < count; i++)
            {
                string file = PieceFile(i);
                if (files.Contains(file, StringComparer.OrdinalIgnoreCase))
                {
                    throw ToolException.InvalidArgument($"'paths' lists '{file}' twice.");
                }

                Late.Set(Late.Call(parts, "Item", i + 1)!, "DocumentName", file);
                files.Add(file);
            }
        }
        catch (ToolException)
        {
            Undivide(model, parts);
            throw;
        }

        for (int i = 1; i <= count; i++)
        {
            Late.Call(Late.Call(parts, "Item", i)!, "Populate");
        }

        model.Document.Save();
        context.ModelChanged(model.Document, "the part was divided");
        object[] pieces = [.. files.Select((file, i) => (object)new
        {
            index = i + 1,
            path = file,
            written = File.Exists(file),
            status = DivisionStatus(Late.Call(parts, "Item", i + 1)!),
        })];
        string[] missing = [.. files.Where(f => !File.Exists(f))];
        return missing.Length > 0
            ? throw new ToolException("save_failed", $"Solid Edge did not write {string.Join(", ", missing)}.", "Check the pieces with se_com_items on the part's DividedParts.")
            : new
            {
                document = context.DocumentHandle(model.Document),
                cutter = Late.TryGetString(cutter, "Name"),
                pieces,
                saved = source,
                note = "Each piece is a part with a linked copy of its region of this part; open it with se_documents to work on it.",
            };
    }

    private static void CreateDivision(ModelDocument model, object parts, object cutter, string first)
    {
        try
        {
            Late.Call(parts, "Add", cutter, (int)DividedPartCutDirectionConstants.seDividedPartCutNormal, first);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            Undivide(model, parts);
            throw new ToolException("feature_failed", $"Solid Edge did not divide '{model.Document.Name}': {com.Message}",
                "The plane or surface must cut the body into separate regions.", ex);
        }
    }

    /// <summary>
    /// Removes a division that is not going ahead. The part was saved and unchanged before, so saving it again stores that
    /// same state and leaves it ready for another try (a failed Add can still leave regions: Count 2 after E_FAIL, verified).
    /// </summary>
    private static void Undivide(ModelDocument model, object parts)
    {
        DeleteDivision(parts);
        if (Late.Count(parts) == 0 && model.Document.Dirty)
        {
            model.Document.Save();
        }
    }

    private static void DeleteDivision(object parts)
    {
        for (int guard = Late.Count(parts); guard > 0 && Late.Count(parts) > 0; guard--)
        {
            try
            {
                Late.Call(Late.Call(parts, "Item", Late.Count(parts))!, "Delete");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                return;
            }
        }
    }

    private static string DivisionStatus(object piece) => Late.TryGet(piece, "Status") is { } value
        ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) switch
        {
            (int)DividedPartStatusConstants.seDividedStatusUpToDate => "up_to_date",
            (int)DividedPartStatusConstants.seDividedStatusOutOfDate => "out_of_date",
            (int)DividedPartStatusConstants.seDividedStatusLinkBroken => "link_broken",
            _ => "not_created",
        }
        : "unknown";

    /// <summary>Targets (design bodies; default the first) with the model that owns the feature, and the tool bodies.</summary>
    private static (ModelDocument Model, Model Owner, object[] Targets, Body[] Tools, bool Switched) Operands(SeContext context, string? documentHandle,
        BodySpec spec, string action)
    {
        if (spec.Tools is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{action} needs 'tools': body handles (se_body list), surfaces (se_surface) or faces.");
        }

        ModelDocument model = spec.Targets is { Length: > 0 } t
            ? ModelDocument.For(context, documentHandle, t[0])
            : ModelDocument.For(context, documentHandle, spec.Tools[0]);
        bool switched = model.EnsureOrdered();
        IReadOnlyList<(Model Model, Body Body)> bodies = RequireBodies(model, 1);
        (Model owner, object[] targets) = spec.Targets is { Length: > 0 } handles ? Owned(context, bodies, handles) : (bodies[0].Model, [bodies[0].Body]);
        Body[] tools = spec.Tools.Select(h => BodyOf(context, h, "tools")).ToArray();
        for (int i = 0; i < tools.Length; i++)
        {
            if (targets.Any(x => Late.SameObject(x, tools[i])))
            {
                throw ToolException.InvalidArgument($"'{spec.Tools[i]}' is a target too.", "A body cannot be both a target and a tool.");
            }
        }

        return (model, owner, targets, tools, switched);
    }

    /// <summary>The body a handle stands for: a body; a face or surface gives the body it belongs to; a model its body.</summary>
    private static Body BodyOf(SeContext context, string handle, string argument)
    {
        object value = context.Handles.Resolve(handle, argument);
        object? body = value switch
        {
            Body => value,
            Face face => Late.TryGet(face, "Body"),
            _ => Late.TryGet(value, "Body") ?? (FacesOf(value) is { Length: > 0 } faces ? Late.TryGet(faces[0], "Body") : null),
        };
        return body as Body ?? throw ToolException.InvalidArgument($"'{handle}' in '{argument}' is not a body, a surface or a face.",
            "Use body handles from se_body list, surface handles from se_surface or face handles from se_geometry.");
    }

    private static void RequireClosed(SeContext context, string[] handles, Body[] bodies, string action)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            if (!bodies[i].IsSolid)
            {
                throw ToolException.InvalidArgument($"'{handles[i]}' is an open surface; {action} needs closed bodies.",
                    "Close it first: se_surface with endCaps, or stitch surfaces that enclose a volume (se_surface stitch).");
            }
        }
    }

    private static object BodyResult(SeContext context, ModelDocument model, object feature, double before, double after, bool switched, string? status,
        bool construction = false) => new
    {
        feature = Describe(context, model, feature),
        volume = Geo.Mm3(after),
        volumeChange = Geo.Mm3(after - before),
        bodies = InspectService.DescribeBodies(context, model),
        construction = construction ? ConstructionBodies(model).Select(c => DescribeConstruction(context, model, c.Name, c.Body)).ToArray() : null,
        warnings = Warnings(switched, status == "warning"),
    };

    /// <summary>
    /// Construction bodies: each item of Constructions is a ConstructionModel with a Body (surfaces, closed surfaces,
    /// bodies made constructions, curves). One whose body cannot be read (used up by a later feature) is skipped.
    /// </summary>
    private static List<(string? Name, Body Body)> ConstructionBodies(ModelDocument model)
    {
        Constructions constructions = ConstructionsOf(model);
        var list = new List<(string?, Body)>();
        int count = constructions.Count;
        for (int i = 1; i <= count; i++)
        {
            object item = constructions.Item(i);
            try
            {
                if (Late.Get(item, "Body") is Body body)
                {
                    list.Add((Late.TryGetString(item, "BodyName"), body));
                }
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
            {
                // No body to show.
            }
        }

        return list;
    }

    private static object DescribeConstruction(SeContext context, ModelDocument model, string? name, Body body)
    {
        bool solid = body.IsSolid;
        int faces = Late.Count(body.Faces[SolidEdgeGeometry.FeatureTopologyQueryTypeConstants.igQueryAll]);
        (double[] lo, double[] hi) = InspectService.Range([body]);
        return new
        {
            handle = context.Register(body, "body", model.Document),
            name,
            kind = solid ? "solid" : faces > 0 ? "surface" : "curve",
            volume = solid ? Geo.Mm3(body.Volume) : (double?)null,
            faces,
            boundingBox = new { min = lo, max = hi },
        };
    }
}
