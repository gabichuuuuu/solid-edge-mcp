// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Organising the feature tree as in PathFinder: feature groups, moving a feature, and rolling the model back to a feature
/// (Go To) and forward again.
/// </summary>
public static partial class FeatureService
{
    /// <summary>
    /// Groups features. Verified: FeatureGroups.AddFeatureGroupsBySet takes features that are next to each other in the tree
    /// (any other set gives E_INVALIDARG, which .NET raises as ArgumentException, and a feature already in a group E_FAIL),
    /// also a single one; the group goes into the tree before its first member, and suppressing it suppresses them. The
    /// group does not keep its members in tree order, so they are reported in that order.
    /// </summary>
    private static object Group(SeContext context, string? documentHandle, string[]? features, string? name)
    {
        if (features is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("Action 'group' requires 'features': the features to group, next to each other in se_feature_tree.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, features[0]);
        Model target = model.RequireModel();
        object[] items = features.Select(h => context.Handles.Resolve(h, "features")).ToArray();
        List<object> tree = TreeItems(model);
        int[] positions = items.Select(f => tree.FindIndex(t => Late.SameObject(t, f))).ToArray();
        if (Array.IndexOf(positions, -1) is int missing and >= 0)
        {
            throw ToolException.InvalidArgument($"'{features[missing]}' is not in the feature tree of the document.", "Use handles from se_feature_tree.");
        }

        if (positions.Distinct().Count() != positions.Length)
        {
            throw ToolException.InvalidArgument("'features' lists the same feature twice.");
        }

        // In tree order, whatever the order given.
        Array ordered = items.Zip(positions).OrderBy(p => p.Second).Select(p => p.First).ToArray();
        FeatureGroup group;
        try
        {
            group = target.FeatureGroups.AddFeatureGroupsBySet(ordered.Length, ref ordered);
        }
        catch (Exception ex) when (ex is ArgumentException { HResult: ComErrors.EInvalidArg } or COMException { HResult: ComErrors.EFail or ComErrors.EInvalidArg })
        {
            throw new ToolException("invalid_argument", "Solid Edge groups only features that are next to each other in the feature tree and not in another group.",
                "Group a run of consecutive features (se_feature_tree shows the order, including sketches and planes between them) or move them together first with action 'reorder'.", ex);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            group.Name = name;
        }

        return new
        {
            group = Describe(context, model, group, IndexOf(model, group)),
            members = Members(model, group),
        };
    }

    /// <summary>Dissolves a feature group; its features stay where they are.</summary>
    private static object Ungroup(SeContext context, ModelDocument model, string featureHandle, object feature, string label)
    {
        if (feature is not FeatureGroup group)
        {
            throw ToolException.InvalidArgument($"'{label}' is not a feature group.", "Use the handle of a FeatureGroup from se_feature_tree.");
        }

        string?[] members = Members(model, group);
        group.Ungroup();
        context.Handles.Remove(featureHandle, "the feature group was dissolved");
        return new { ungrouped = label, members };
    }

    /// <summary>
    /// Moves a feature before or after another one. If Solid Edge refuses, or a feature fails after the move (it went before
    /// something it is built on), the feature is moved back.
    /// </summary>
    private static object Reorder(SeContext context, ModelDocument model, object feature, string label, string? targetHandle, bool before)
    {
        if (string.IsNullOrWhiteSpace(targetHandle))
        {
            throw ToolException.InvalidArgument("Action 'reorder' requires 'target': the feature to move it next to.");
        }

        object targetFeature = context.Handles.Resolve(targetHandle, "target");
        if (Late.SameObject(feature, targetFeature))
        {
            throw ToolException.InvalidArgument("'target' must be another feature.");
        }

        List<object> tree = TreeItems(model);
        int from = tree.FindIndex(t => Late.SameObject(t, feature));
        object? next = from >= 0 && from + 1 < tree.Count ? tree[from + 1] : null;
        object? previous = from > 0 ? tree[from - 1] : null;
        HashSet<string> failedBefore = FailedNames(tree);
        double volumeBefore = model.Volume();
        try
        {
            Late.Call(feature, "Reorder", targetFeature, before);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
        {
            string where = $"{(before ? "before" : "after")} '{Late.TryGetString(targetFeature, "Name") ?? targetHandle}'";
            throw new ToolException("invalid_argument", $"Solid Edge did not move '{label}' {where}: {com.Message}",
                "A feature cannot go before what it is built on (its sketch, plane, or the faces and edges it uses), nor among the synchronous features at the top of the tree.", ex);
        }

        context.ModelChanged(model.Document, $"the model changed (moved {label})");
        string[] failed = FailedNames(TreeItems(model)).Except(failedBefore).ToArray();
        if (failed.Length > 0)
        {
            try
            {
                if (next is not null)
                {
                    Late.Call(feature, "Reorder", next, true);
                }
                else if (previous is not null)
                {
                    Late.Call(feature, "Reorder", previous, false);
                }
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException com && !ComErrors.IsGone(com))
            {
                throw new ToolException("feature_failed", $"Moving '{label}' made {string.Join(", ", failed)} fail and it could not be moved back: {com.Message}",
                    "Undo it in Solid Edge (Ctrl+Z) or move it back with action 'reorder'.", ex);
            }

            context.ModelChanged(model.Document, $"the model changed (moved {label} back)");
            throw new ToolException("feature_failed", $"Moving '{label}' made {string.Join(", ", failed)} fail, so it was moved back.",
                "A feature cannot go before what it is built on (its sketch, plane, or the faces and edges it uses).");
        }

        double after = model.Volume();
        return new
        {
            feature = Describe(context, model, feature, IndexOf(model, feature)),
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - volumeBefore),
        };
    }

    /// <summary>
    /// Rolls the model back to just after a feature (PathFinder "Go To"): the features after it are rolled back and new
    /// features go after it. Verified: Feature.RollToFeature; the rolled-back features read igFeatureRolledBack.
    /// </summary>
    private static object RollTo(SeContext context, ModelDocument model, object feature, string label)
    {
        try
        {
            Late.Call(feature, "RollToFeature");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw new ToolException("not_supported", $"The model cannot be rolled back to '{label}'.", "Use a feature from se_feature_tree.", ex);
        }

        context.ModelChanged(model.Document, $"the model was rolled back to {label}");
        return Rolled(context, model, feature);
    }

    /// <summary>Rolls the model forward to its last feature, bringing back every rolled-back feature.</summary>
    private static object RollToEnd(SeContext context, string? documentHandle)
    {
        ModelDocument model = ModelDocument.Get(context, documentHandle);
        List<object> tree = TreeItems(model);
        for (int i = tree.Count - 1; i >= 0; i--)
        {
            try
            {
                Late.Call(tree[i], "RollToFeature");
            }
            catch (Exception ex) when (Late.IsMemberFailure(ex))
            {
                continue;
            }

            context.ModelChanged(model.Document, "the model was rolled forward to its end");
            return Rolled(context, model, tree[i]);
        }

        throw new ToolException("not_supported", "The feature tree has nothing to roll forward to.");
    }

    private static object Rolled(SeContext context, ModelDocument model, object feature)
    {
        string?[] rolledBack = TreeItems(model).Where(f => StatusOf(f) == "rolled_back").Select(f => Late.TryGetString(f, "Name")).ToArray();
        double? volume = model.TryVolume();
        return new
        {
            rolledTo = Describe(context, model, feature, IndexOf(model, feature)),
            rolledBack,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
            note = rolledBack.Length > 0
                ? "New features go after this one. Bring the rest back with action 'roll_to_end'."
                : null,
        };
    }

    /// <summary>Names of the features of a group, in tree order.</summary>
    private static string?[] Members(ModelDocument model, FeatureGroup group)
    {
        List<object> tree = TreeItems(model);
        return Late.Items(group)
            .OrderBy(m => tree.FindIndex(t => Late.SameObject(t, m)))
            .Select(m => Late.TryGetString(m, "Name"))
            .ToArray();
    }

    private static List<object> TreeItems(ModelDocument model)
    {
        EdgebarFeatures features = model.EdgebarFeatures;
        var items = new List<object>();
        for (int i = 1; i <= features.Count; i++)
        {
            if (features.Item(i) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>1-based position in the feature tree, as se_feature_tree reports it.</summary>
    private static int? IndexOf(ModelDocument model, object feature) =>
        TreeItems(model).FindIndex(t => Late.SameObject(t, feature)) is int i and >= 0 ? i + 1 : null;

    private static HashSet<string> FailedNames(List<object> tree) =>
        [.. tree.Where(f => StatusOf(f) == "failed").Select(f => Late.TryGetString(f, "Name") ?? "")];

    /// <summary>
    /// Moves an ordered feature (and what depends on it) to synchronous, as the Move to Synchronous command of PathFinder.
    /// Verified in Solid Edge 2026: PartDocument.MoveToSynchronous(feature, ignore warnings, false, ...) moved the body
    /// feature of an imported STEP part (no errors, volume difference 0); its out arrays must be passed initialised (a null
    /// array fails before the call). The document stays in ordered mode, so it is switched to synchronous, where the moved
    /// features are edited and recognised (se_recognize). The volume before is read in ordered mode: in synchronous mode the
    /// body leaves the ordered features out.
    /// Verified: after the synchronous body was edited (a face moved by a face relation, a recognised hole resized), moving
    /// an ordered round made later failed with E_FAIL, with or without ignoring warnings, and left the body unreadable with
    /// nothing to undo; once the part had been saved (SaveCopyAs to another file is enough) the same move worked, with no
    /// volume change. So a copy of the part is saved first in the work folder: it makes the move work and, if Solid Edge
    /// still breaks the body, it is kept and named in the error. Saving clears Solid Edge's undo list, as saving does.
    /// </summary>
    private static object MoveToSynchronous(SeContext context, ModelDocument model, object feature, string label)
    {
        model.EnsureOrdered();
        double before = model.Volume();
        string? backup = Backup(context, model);
        int errors = 0, warnings = 0;
        Array errorMessages = Array.Empty<string>(), warningMessages = Array.Empty<string>();
        try
        {
            if (model.Document is PartDocument part)
            {
                part.MoveToSynchronous(feature, true, false, out errors, out errorMessages, out warnings, out warningMessages, out _);
            }
            else
            {
                ((SheetMetalDocument)model.Document).MoveToSynchronous(feature, true, false, out errors, out errorMessages, out warnings,
                    out warningMessages);
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            context.ModelChanged(model.Document, $"the model changed (move {label} to synchronous failed)");
            if (model.TryModel() is not null && model.TryVolume() is null)
            {
                throw new ToolException("body_broken", $"Solid Edge could not move '{label}' to synchronous ({ex.Message.Trim()}) and left the body unreadable; it does not undo this."
                    + (backup is null ? "" : $" The part as it was is saved in '{backup}'."),
                    backup is null
                        ? "Close the document without saving (se_documents close, saveChanges false) and open the saved file again."
                        : "Close this document without saving (se_documents close, saveChanges false), open the copy (se_documents open) and save it where the part belongs (save_as). Make the feature again in synchronous mode instead (se_documents modeling_mode synchronous, then the tool).",
                    ex);
            }

            DeleteBackup(backup);
            throw new ToolException("feature_failed", $"Solid Edge could not move '{label}' to synchronous: {ex.Message.Trim()}",
                "Only ordered features move (se_feature_tree); the base feature of a part moves with everything after it.", ex);
        }

        DeleteBackup(backup);
        context.ModelChanged(model.Document, $"the model changed (move {label} to synchronous)");
        string[] Messages(Array? values) => values is null ? [] : [.. values.Cast<object?>().Select(v => v?.ToString() ?? "").Where(v => v.Length > 0)];
        if (errors > 0)
        {
            throw new ToolException("feature_failed", $"Solid Edge did not move '{label}' to synchronous ({errors} feature(s) with errors): "
                + string.Join("; ", Messages(errorMessages)), "Fix or delete the features named, then try again.");
        }

        if (Late.TryGet(feature, "ModelingModeType") is { } mode && Convert.ToInt32(mode, CultureInfo.InvariantCulture) == (int)ModelingModeConstants.seModelingModeOrdered)
        {
            throw new ToolException("feature_failed", $"'{label}' is still an ordered feature after Move to Synchronous.",
                "Check the feature in PathFinder; features that synchronous modelling cannot hold stay ordered.");
        }

        bool switched = model.EnsureSynchronous();
        double after = model.Volume();
        return new
        {
            moved = label,
            modelingMode = model.ModelingMode,
            volume = Geo.Mm3(after),
            volumeChange = Geo.Mm3(after - before),
            warnings = Messages(warningMessages) is { Length: > 0 } list ? list : null,
            note = string.Join(" ", new[]
            {
                switched ? "The document was switched to synchronous modelling, where the moved features are." : null,
                backup is null ? null : "A copy of the part was saved first (Solid Edge needs it for this move after synchronous edits), which cleared the undo list.",
            }.OfType<string>()) is { Length: > 0 } text ? text : null,
        };
    }

    /// <summary>Copy of the part in the work folder (backups), or null if Solid Edge does not save it.</summary>
    private static string? Backup(SeContext context, ModelDocument model)
    {
        string folder = Path.Combine(context.Paths.ExchangeFolder, "backups");
        string name = Path.GetFileNameWithoutExtension(model.Document.Name);
        string file = Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}{(model.Document is PartDocument ? ".par" : ".psm")}");
        try
        {
            Directory.CreateDirectory(folder);
            model.Document.SaveCopyAs(file);
            return File.Exists(file) ? file : null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DeleteBackup(string? file)
    {
        try
        {
            if (file is not null)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy left in the work folder does no harm.
        }
    }
}
