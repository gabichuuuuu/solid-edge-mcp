// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Text.Json;
using SolidEdgeDraft;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Display of a drawing view, as in its Properties dialog: the switches of the view itself (shading, captions, cropping,
/// quality…) and those of its model members (hidden and tangent edges, edge styles, display as reference, show or hide a
/// part). Verified in Solid Edge 2026: a model member setting applies, also to the members inside an assembly member, but
/// always reads back false or empty, so its effect is measured by the edges the view draws, counted by line style; the
/// view's Defaults_* settings do not change the members already shown.
/// </summary>
public static partial class DrawingService
{
    private static readonly (string Collection, string Name)[] GraphicCollections =
    [
        ("DVLines2d", "lines"), ("DVArcs2d", "arcs"), ("DVCircles2d", "circles"), ("DVEllipses2d", "ellipses"),
        ("DVEllipticalArcs2d", "ellipticalArcs"), ("DVBSplineCurves2d", "splines"), ("DVLineStrings2d", "lineStrings"),
        ("DVPoints2d", "points"),
    ];

    /// <summary>Graphics drawn in segments with a line style: their collection, segment count and style of a segment.</summary>
    private static readonly (Func<DrawingView, object> Items, Func<object, int> Count, Func<object, int, object?> Style)[] StyledGraphics =
    [
        (v => v.DVLines2d, g => ((DVLine2d)g).SegmentedStyleCount,
            (g, i) => { ((DVLine2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
        (v => v.DVArcs2d, g => ((DVArc2d)g).SegmentedStyleCount,
            (g, i) => { ((DVArc2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
        (v => v.DVCircles2d, g => ((DVCircle2d)g).SegmentedStyleCount,
            (g, i) => { ((DVCircle2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
        (v => v.DVEllipses2d, g => ((DVEllipse2d)g).SegmentedStyleCount,
            (g, i) => { ((DVEllipse2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
        (v => v.DVEllipticalArcs2d, g => ((DVEllipticalArc2d)g).SegmentedStyleCount,
            (g, i) => { ((DVEllipticalArc2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
        (v => v.DVBSplineCurves2d, g => ((DVBSplineCurve2d)g).SegmentedStyleCount,
            (g, i) => { ((DVBSplineCurve2d)g).GetSegmentedStyle(i, out _, out _, out _, out _, out object s); return s; }),
    ];

    private static readonly (string Collection, string Name)[] SheetCollections =
    [
        ("Lines2d", "lines"), ("Arcs2d", "arcs"), ("Circles2d", "circles"), ("Ellipses2d", "ellipses"),
        ("EllipticalArcs2d", "ellipticalArcs"), ("BSplineCurves2d", "splines"), ("LineStrings2d", "lineStrings"), ("Points2d", "points"),
    ];

    private const int MaxSegments = 5000;
    private const int MaxMembers = 2000;
    private const string NotShown = "not shown";

    public static object ViewDisplay(SeContext context, string? viewHandle, int offset, int limit)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        (Dictionary<string, object?> properties, List<string> readOnly, _) = ComProperties.ReadAll(view, typeof(DrawingView));
        List<(ModelMember Member, int Level)> members = MemberTree(view);
        int start = Math.Max(offset, 0);
        var listed = members.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems))
            .Select((m, i) => DescribeMember(m.Member, start + i + 1, m.Level)).ToList();
        (Dictionary<string, int> edges, bool complete) = EdgeStyles(view);
        return new
        {
            view = DescribeView(context, draft, view),
            properties,
            readOnly,
            memberCount = members.Count,
            members = listed,
            memberProperties = MemberProperties().Select(p => p.Name).ToList(),
            edges,
            edgesCounted = complete ? null : $"the first {MaxSegments} segments",
            graphics = GraphicCounts(view),
            note = "Change a view property, or a model member property (memberProperties: every top member, and so the parts "
                + "inside an assembly, unless 'member' names one), with action 'set_display'. Solid Edge does not report model "
                + "member values, so 'edges' (segments drawn, by line style) shows their effect. Lengths in metres; colours #RRGGBB.",
        };
    }

    public static object SetViewDisplay(SeContext context, string? viewHandle, string? property, JsonElement? value, string? member)
    {
        DrawingView view = ResolveView(context, viewHandle);
        string name = string.IsNullOrWhiteSpace(property)
            ? throw ToolException.InvalidArgument("'property' is required: a property listed by action 'display'.")
            : property.Trim();
        JsonElement given = value ?? throw ToolException.InvalidArgument("'value' is required.");
        PropertyInfo? onView = ComProperties.Scalars(typeof(DrawingView)).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        PropertyInfo? onMember = onView is null
            ? MemberProperties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        if (onView is null && onMember is null)
        {
            IEnumerable<string> all = ComProperties.Scalars(typeof(DrawingView)).Select(p => p.Name).Concat(MemberProperties().Select(p => p.Name));
            throw ToolException.NotFound($"A drawing view has no display property '{name}'.{NameMatch.Hint(all, name)}", "List them with action 'display'.");
        }

        if (onView is not null && !string.IsNullOrWhiteSpace(member))
        {
            throw ToolException.InvalidArgument($"'{onView.Name}' belongs to the view, not to a model member: leave out 'member'.");
        }

        string handle = context.Register(view, "drawing_view", Document(DraftFor(context, null, viewHandle)));
        (Dictionary<string, int> before, _) = EdgeStyles(view);
        if (onView is not null)
        {
            (object? previous, object? current) = ComProperties.Set(context, view, onView, given, $"the drawing view '{Late.TryGetString(view, "Name")}'");
            view.Update();
            (Dictionary<string, int> after, _) = EdgeStyles(view);
            string? memberName = onView.Name.StartsWith("Defaults_", StringComparison.Ordinal) ? onView.Name["Defaults_".Length..] : null;
            bool hasMemberTwin = memberName is not null && MemberProperties().Any(p => p.Name == memberName);
            return new
            {
                view = handle,
                property = onView.Name,
                on = "view",
                previous,
                value = current,
                edges = new { before, after },
                upToDate = view.IsUpToDate,
                note = hasMemberTwin && SameCounts(before, after)
                    ? $"Defaults_* settings only apply to model members added later: to change what the view shows now, set '{memberName}' (a model member property)."
                    : null,
                restore = $"Saved with the drawing. To undo, set it back to {JsonSerializer.Serialize(previous)}.",
            };
        }

        ModelMember[] targets = string.IsNullOrWhiteSpace(member) ? Members(view) : [FindMember(view, member)];
        if (targets.Length == 0)
        {
            throw new ToolException("not_supported", "The view shows no model (a 2D view), so it has no model members.");
        }

        object? written = null;
        var names = new List<string>();
        foreach (ModelMember target in targets)
        {
            written = ComProperties.Write(context, target, onMember!, given, $"the model member '{MemberName(target)}'");
            names.Add(MemberName(target));
        }

        view.Update();
        (Dictionary<string, int> changed, _) = EdgeStyles(view);
        bool visible = !SameCounts(before, changed);
        return new
        {
            view = handle,
            property = onMember!.Name,
            on = "model members",
            members = names,
            value = written,
            edges = new { before, after = changed },
            changed = visible,
            upToDate = view.IsUpToDate,
            note = visible
                ? null
                : "The edges drawn did not change: the members may already have had this value, or the view has no such edges "
                    + "(Solid Edge does not report model member values, so it cannot be read back).",
            restore = visible && written is bool flag
                ? $"Saved with the drawing. To undo, set it back to {(!flag ? "true" : "false")}."
                : "Saved with the drawing. Solid Edge does not report the previous value of a model member setting.",
        };
    }

    public static object Breaks(SeContext context, string? viewHandle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        BreakLinePair[] pairs = Pairs(view);
        return new
        {
            view = context.Register(view, "drawing_view", Document(DraftFor(context, null, viewHandle))),
            broken = view.IsBroken,
            size = SizeOf(view),
            count = pairs.Length,
            breaks = pairs.Select((p, i) => DescribeBreak(p, i + 1)).ToList(),
        };
    }

    /// <summary>
    /// Adds a pair of break lines and shows the view broken; checked by the view getting shorter. Verified in Solid Edge
    /// 2026: the positions are sheet coordinates, and the view shrinks by their distance minus the gap.
    /// </summary>
    /// <summary>
    /// Broken-out section of a view: the outline through model points (projected onto the view) cut to a depth (mm) from the
    /// front of the part, showing what is inside, hatched. Verified in Solid Edge 2026: the profile is drawn in sheet
    /// coordinates on the view's own BrokenOutSectionProfiles with the view as target (Validate reports lines without
    /// relations as invalid, yet the cut is made); the profile has no Delete, but it stays selected after it is made, so a
    /// failed one is deleted through the selection.
    /// </summary>
    public static object AddBrokenOut(SeContext context, string? viewHandle, double[]? points, double? depth)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        List<(double X, double Y, double Z)> model = Geo.Points3(points, "points", 3);
        if (model.Count < 3)
        {
            throw ToolException.InvalidArgument("A broken-out section needs at least three model points: the corners of its outline as seen in the view.");
        }

        double cut = Units.MmToM(Units.RequirePositive(depth ?? throw ToolException.InvalidArgument("Action 'broken_out' requires 'depth' (mm from the front of the part)."), "depth"));
        (double X, double Y)[] outline = [.. model.Select(p => ModelToSheet(view, p))];
        // A section that cuts nothing is still made, and it can be neither deleted nor undone through the API (verified):
        // an outline away from what the view draws is refused before anything is made.
        Box drawn = Outline(view, Graphics(view));
        if (outline.Max(p => p.X) <= drawn.X1 || outline.Min(p => p.X) >= drawn.X2 || outline.Max(p => p.Y) <= drawn.Y1 || outline.Min(p => p.Y) >= drawn.Y2)
        {
            throw ToolException.InvalidArgument("The outline does not overlap what the view shows of the part.",
                "Give the corners of the outline as model points seen in this view (se_geometry lists the model's edges).");
        }

        Dictionary<string, int> before = GraphicCounts(view);
        BrokenOutSectionProfiles profiles = view.BrokenOutSectionProfiles;
        int count = profiles.Count;
        int undoStart = UndoCount(draft);
        BrokenOutSectionProfile profile = profiles.Add(view);
        try
        {
            for (int i = 0; i < outline.Length; i++)
            {
                (double X, double Y) a = outline[i], b = outline[(i + 1) % outline.Length];
                profile.Profile.Lines2d.AddBy2Points(a.X, a.Y, b.X, b.Y);
            }

            profile.Profile.EndEdit();
            profile.Depth = cut;
            view.Update();
            Dictionary<string, int> after = GraphicCounts(view);
            if (profiles.Count != count + 1 || !view.IsBrokenOutSectionTarget || after.OrderBy(k => k.Key).SequenceEqual(before.OrderBy(k => k.Key)))
            {
                throw new ToolException("view_failed", "The broken-out section changed nothing in the view: the outline must enclose part of it and the depth reach inside.",
                    "Give the corners of the outline as model points of the view, and a depth that reaches what should be shown.");
            }

            // After the update Depth reads from the back of the part (15 from the front of a 20 mm part reads 5): the depth
            // asked for is reported, and the cut is checked by what the view shows.
            return new
            {
                view = DescribeView(context, draft, view),
                brokenOut = new { corners = outline.Length, depth = Geo.Mm(cut), sections = profiles.Count },
                graphics = new { before, after },
            };
        }
        catch (Exception ex) when (ex is ToolException || (ComErrors.Unwrap(ex) is System.Runtime.InteropServices.COMException or ArgumentException && !ComErrors.IsGone(ex)))
        {
            // A section that cuts nothing is still made and not left selected: then it is undone.
            bool removed = DeleteIfOnlySelected(draft, profile, profiles, count) || (UndoSince(draft, undoStart) && profiles.Count == count);
            ToolException error = ex as ToolException
                ?? new ToolException("view_failed", $"Solid Edge rejected the broken-out section: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
            if (!removed)
            {
                throw new ToolException(error.Code, error.Message + " The section could not be removed: undo it in Solid Edge.", error.NextStep, ex);
            }

            throw error;
        }
    }

    /// <summary>Undoable transactions of a document, or -1 if it does not report them.</summary>
    private static int UndoCount(object document)
    {
        try
        {
            return Convert.ToInt32(Late.Call(document, "UndoTransactionCount"), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return -1;
        }
    }

    /// <summary>Undoes only the transactions recorded after <paramref name="start"/>; returns whether it undid any.</summary>
    private static bool UndoSince(object document, int start)
    {
        int count = UndoCount(document);
        if (start < 0 || count <= start)
        {
            return false;
        }

        try
        {
            Late.Call(document, "Undo", count - start);
            return true;
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Deletes what is selected only when that is the new broken-out profile alone (it stays selected once made).</summary>
    private static bool DeleteIfOnlySelected(DraftDocument draft, BrokenOutSectionProfile profile, BrokenOutSectionProfiles profiles, int count)
    {
        try
        {
            object? selection = Late.TryGet(draft, "SelectSet");
            if (selection is null || Late.Count(selection) != 1)
            {
                return profiles.Count == count;
            }

            object? selected = Late.Call(selection, "Item", 1);
            if (selected is null || !(Late.SameObject(selected, profile) || Late.SameObject(selected, profile.Profile)))
            {
                return profiles.Count == count;
            }

            Late.Call(selection, "Delete");
            return profiles.Count == count;
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            return false;
        }
    }

    public static object AddBreak(SeContext context, string? viewHandle, string direction, double from, double to, string? shape, double? gap)
    {
        DrawingView view = ResolveView(context, viewHandle);
        bool vertical = direction == "vertical";
        double first = Units.MmToM(Units.RequireFinite(Math.Min(from, to), "from"));
        double second = Units.MmToM(Units.RequireFinite(Math.Max(from, to), "to"));
        if (second - first < 1e-6)
        {
            throw ToolException.InvalidArgument("'from' and 'to' must be two different positions.");
        }

        double[] before = SizeOf(view);
        string hint = $"Put both inside the view: they are {(vertical ? "x" : "y")} positions on the sheet in mm (se_drawing views gives the view centre and size).";
        // Verified in Solid Edge 2026: lines outside the view give no pair (null) rather than an error.
        BreakLinePair pair = view.BreakLinePairs.Add(
            vertical ? BreakLinePairDirConstants.igBreakLinePairDirConstants_Vertical : BreakLinePairDirConstants.igBreakLinePairDirConstants_Horizontal,
            first, second, false)
            ?? throw new ToolException("view_failed", $"Solid Edge did not add break lines at {Geo.Mm(first)} and {Geo.Mm(second)} mm.", hint);
        try
        {
            if (shape is not null)
            {
                pair.Type = BreakShape(shape);
            }

            if (gap is { } g)
            {
                pair.Gap = Units.MmToM(Units.RequireFinite(g, "gap"));
            }

            view.SetBrokenState();
            view.Update();
        }
        catch
        {
            TryDeleteBreak(view, pair);
            throw;
        }

        double[] after = SizeOf(view);
        int axis = vertical ? 0 : 1;
        if (before[axis] - after[axis] < 1e-3)
        {
            TryDeleteBreak(view, pair);
            throw new ToolException("view_failed",
                $"The break lines at {Geo.Mm(first)} and {Geo.Mm(second)} mm did not shorten the view, so they were removed.", hint);
        }

        BreakLinePair[] pairs = Pairs(view);
        return new
        {
            view = context.Register(view, "drawing_view", Document(DraftFor(context, null, viewHandle))),
            @break = DescribeBreak(pair, pairs.Length),
            size = new { before, after },
        };
    }

    public static object SetBroken(SeContext context, string? viewHandle, bool broken)
    {
        DrawingView view = ResolveView(context, viewHandle);
        if (broken && Pairs(view).Length == 0)
        {
            throw new ToolException("not_supported", "The view has no break lines.", "Add them with action 'add_break'.");
        }

        double[] before = SizeOf(view);
        if (broken)
        {
            view.SetBrokenState();
        }
        else
        {
            view.SetUnBrokenState();
        }

        view.Update();
        return new
        {
            view = context.Register(view, "drawing_view", Document(DraftFor(context, null, viewHandle))),
            broken = view.IsBroken,
            size = new { before, after = SizeOf(view) },
        };
    }

    public static object RemoveBreak(SeContext context, string? viewHandle, int index)
    {
        DrawingView view = ResolveView(context, viewHandle);
        BreakLinePair[] pairs = Pairs(view);
        if (index < 1 || index > pairs.Length)
        {
            throw ToolException.NotFound($"The view has {pairs.Length} break(s); there is no number {index}.", "Action 'breaks' lists them.");
        }

        double[] before = SizeOf(view);
        object removed = DescribeBreak(pairs[index - 1], index);
        pairs[index - 1].Delete();
        if (Pairs(view).Length == 0)
        {
            view.SetUnBrokenState();
        }

        view.Update();
        return new
        {
            view = context.Register(view, "drawing_view", Document(DraftFor(context, null, viewHandle))),
            removed,
            remaining = Pairs(view).Length,
            size = new { before, after = SizeOf(view) },
        };
    }

    /// <summary>
    /// Converts the view into 2D geometry (Drop): it keeps its look but loses its link to the model. Verified in Solid Edge
    /// 2026: the view becomes a 2D view (igUserView) whose lines and arcs are in its own sheet (DrawingView.Sheet).
    /// </summary>
    public static object To2d(SeContext context, string? viewHandle)
    {
        DrawingView view = ResolveView(context, viewHandle);
        DraftDocument draft = DraftFor(context, null, viewHandle);
        if (LinkOf(view) is null)
        {
            throw new ToolException("not_supported", "The view shows no model: it is already 2D.");
        }

        int graphics = GraphicCounts(view)["total"];
        view.Drop();
        view.Update();
        Dictionary<string, int> elements = view.Sheet is Sheet own ? SheetElements(own) : [];
        if (LinkOf(view) is not null || elements.GetValueOrDefault("total") == 0)
        {
            throw new ToolException("view_failed", "Solid Edge did not turn the view into 2D geometry.",
                "Check that the view is up to date (se_drawing update) and try again.");
        }

        return new
        {
            view = DescribeView(context, draft, view),
            graphics,
            elements,
            note = "The view now holds plain 2D elements: it no longer follows changes to the model.",
        };
    }

    private static BreakLinePair[] Pairs(DrawingView view) => [.. Late.Items(view.BreakLinePairs).OfType<BreakLinePair>()];

    private static object DescribeBreak(BreakLinePair pair, int index) => new
    {
        index,
        direction = pair.Direction == BreakLinePairDirConstants.igBreakLinePairDirConstants_Vertical ? "vertical" : "horizontal",
        from = Geo.Mm(pair.xy1),
        to = Geo.Mm(pair.xy2),
        shape = JsonNamingPolicy.SnakeCaseLower.ConvertName(pair.Type.ToString().Replace("igBreakLinePairTypeConstants_", "", StringComparison.Ordinal)),
        gap = Geo.Mm(pair.Gap),
    };

    private static BreakLinePairTypeConstants BreakShape(string shape) => shape switch
    {
        "straight" => BreakLinePairTypeConstants.igBreakLinePairTypeConstants_Straight,
        "cylindrical" => BreakLinePairTypeConstants.igBreakLinePairTypeConstants_Cylindrical,
        "short_break" => BreakLinePairTypeConstants.igBreakLinePairTypeConstants_ShortBreak,
        "long_break" => BreakLinePairTypeConstants.igBreakLinePairTypeConstants_LongBreak,
        "short_curved_break" => BreakLinePairTypeConstants.igBreakLinePairTypeConstants_ShortCurvedBreak,
        _ => throw ToolException.InvalidArgument($"Unknown break shape '{shape}'."),
    };

    private static void TryDeleteBreak(DrawingView view, BreakLinePair pair)
    {
        try
        {
            pair.Delete();
            if (Pairs(view).Length == 0)
            {
                view.SetUnBrokenState();
            }

            view.Update();
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            // The error that stopped the break is the one reported.
        }
    }

    private static double[] SizeOf(DrawingView view)
    {
        Box box = BoxOf(view);
        return [Geo.Mm(box.Width), Geo.Mm(box.Height)];
    }

    /// <summary>Model member properties that can be set (Solid Edge does not report their values).</summary>
    private static IEnumerable<PropertyInfo> MemberProperties() => ComProperties.Scalars(typeof(ModelMember)).Where(p => p.CanWrite);

    /// <summary>Top model members of the view: the part, or the assembly (its parts are inside it).</summary>
    private static ModelMember[] Members(DrawingView view)
    {
        try
        {
            return [.. Late.Items(view.ModelMembers).OfType<ModelMember>()];
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    /// <summary>Every model member, depth first: inside an assembly member its parts, subassemblies and reference planes.</summary>
    private static List<(ModelMember Member, int Level)> MemberTree(DrawingView view)
    {
        var tree = new List<(ModelMember, int)>();
        void Add(ModelMember member, int level)
        {
            if (tree.Count >= MaxMembers)
            {
                return;
            }

            tree.Add((member, level));
            if (MemberType(member) == "assembly" && Late.TryGet(member, "ModelMembers") is { } children)
            {
                foreach (ModelMember child in Late.Items(children).OfType<ModelMember>())
                {
                    Add(child, level + 1);
                }
            }
        }

        foreach (ModelMember member in Members(view))
        {
            Add(member, 0);
        }

        return tree;
    }

    /// <summary>Model member by its number in action 'display' or its component or file name.</summary>
    private static ModelMember FindMember(DrawingView view, string member)
    {
        List<(ModelMember Member, int Level)> tree = MemberTree(view);
        string wanted = member.Trim();
        if (int.TryParse(wanted, out int index))
        {
            return index >= 1 && index <= tree.Count
                ? tree[index - 1].Member
                : throw ToolException.NotFound($"The view has {tree.Count} model member(s); there is no number {index}.", "Action 'display' lists them.");
        }

        return tree.Select(m => m.Member).FirstOrDefault(m => string.Equals(MemberName(m), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(Late.TryGetString(m, "FileName") ?? ""), wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"The view has no model member '{member}'.{NameMatch.Hint(tree.Select(m => MemberName(m.Member)), wanted)}",
                "Action 'display' lists them with their number.");
    }

    private static string MemberName(ModelMember member) =>
        Late.TryGetString(member, "ComponentName") is { Length: > 0 } component
            ? component
            : Path.GetFileName(Late.TryGetString(member, "FileName") ?? "") is { Length: > 0 } file ? file : "model";

    private static string MemberType(ModelMember member)
    {
        try
        {
            return member.Type switch
            {
                ModelMemberTypeConstants.seAssemblyMember => "assembly",
                ModelMemberTypeConstants.sePartMember => "part",
                ModelMemberTypeConstants.seConstructionMember => "construction",
                ModelMemberTypeConstants other => other.ToString(),
            };
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            return "other";
        }
    }

    private static object DescribeMember(ModelMember member, int index, int level) => new
    {
        index,
        name = MemberName(member),
        type = MemberType(member),
        level,
        file = Path.GetFileName(Late.TryGetString(member, "FileName") ?? ""),
    };

    /// <summary>
    /// Segments the view draws, counted by line style (Visible, Hidden, Tangent…); a segment the view does not draw
    /// rejects GetSegmentedStyle (verified in Solid Edge 2026) and counts as "not shown".
    /// </summary>
    private static (Dictionary<string, int> Styles, bool Complete) EdgeStyles(DrawingView view)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        int segments = 0;
        foreach ((Func<DrawingView, object> items, Func<object, int> countOf, Func<object, int, object?> styleOf) in StyledGraphics)
        {
            object collection;
            try
            {
                collection = items(view);
            }
            catch (Exception ex) when (ComErrors.IsCallFailure(ex))
            {
                continue;
            }

            foreach (object graphic in Late.Items(collection))
            {
                int count = countOf(graphic);
                for (int i = 1; i <= count; i++)
                {
                    if (++segments > MaxSegments)
                    {
                        return (counts, false);
                    }

                    string style = SegmentStyle(styleOf, graphic, i, names);
                    counts[style] = counts.GetValueOrDefault(style) + 1;
                }
            }
        }

        return (counts, true);
    }

    private static string SegmentStyle(Func<object, int, object?> styleOf, object graphic, int index, Dictionary<object, string> names)
    {
        try
        {
            if (styleOf(graphic, index) is not { } style)
            {
                return "(no style)";
            }

            if (!names.TryGetValue(style, out string? name))
            {
                name = Late.TryGetString(style, "Name") ?? "(unnamed)";
                names[style] = name;
            }

            return name;
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            return NotShown;
        }
    }

    private static bool SameCounts(Dictionary<string, int> a, Dictionary<string, int> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out int other) && other == pair.Value);

    /// <summary>2D graphics the view shows, by kind, and their total.</summary>
    private static Dictionary<string, int> GraphicCounts(DrawingView view) => Counts(view, GraphicCollections);

    /// <summary>2D elements of a sheet (or of the sheet of a 2D view), by kind, and their total.</summary>
    internal static Dictionary<string, int> SheetElements(Sheet sheet) => Counts(sheet, SheetCollections);

    private static Dictionary<string, int> Counts(object owner, (string Collection, string Name)[] collections)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach ((string collection, string name) in collections)
        {
            int count = Late.TryGet(owner, collection) is { } items && Late.TryGet(items, "Count") is int n ? n : 0;
            if (count > 0)
            {
                counts[name] = count;
            }

            total += count;
        }

        counts["total"] = total;
        return counts;
    }
}
