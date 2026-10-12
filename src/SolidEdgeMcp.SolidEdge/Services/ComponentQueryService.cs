using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>A query criterion: property (snake_case), condition (contains | is | is_not), value and custom property name.</summary>
public sealed record ComponentCriterion(string Property, string Condition, string? Value, string? Custom);

/// <summary>
/// Component queries and zones of an assembly, as the Select Tools of PathFinder use them. Verified in Solid Edge 2026:
/// each change of a query (criteria, scope) evaluates it and replaces the selection with its matches (none: an empty
/// selection), so the matches are read from the selection; the criteria are combined with AND, 'contains' ignores case and
/// 'Name' is the occurrence name ('bolt.par:2'). Parts inside subassemblies are selected as references (path: top-level
/// occurrence and the subassembly occurrences down to the part). Removing a query keeps the selection. <c>Query.Criteria</c>
/// cannot be read and the Status criterion is refused (E_INVALIDARG) or matches nothing. A zone selects the parts inside
/// a box (<c>Overlap</c>: also those partly inside) and replaces the selection. Saved queries and zones are kept in the
/// assembly file; Solid Edge's own quick query is "__SolidEdgeQuickQuery__" and is not listed.
/// </summary>
public static class ComponentQueryService
{
    private const string QuickQuery = "__SolidEdgeQuickQuery__";

    private static readonly Dictionary<string, QueryPropertyConstants> Properties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = QueryPropertyConstants.seQueryPropertyName,
        ["title"] = QueryPropertyConstants.seQueryPropertyTitle,
        ["subject"] = QueryPropertyConstants.seQueryPropertySubject,
        ["author"] = QueryPropertyConstants.seQueryPropertyAuthor,
        ["manager"] = QueryPropertyConstants.seQueryPropertyManager,
        ["company"] = QueryPropertyConstants.seQueryPropertyCompany,
        ["category"] = QueryPropertyConstants.seQueryPropertyCategory,
        ["keywords"] = QueryPropertyConstants.seQueryPropertyKeywords,
        ["comments"] = QueryPropertyConstants.seQueryPropertyComments,
        ["document_number"] = QueryPropertyConstants.seQueryPropertyDocumentNumber,
        ["revision"] = QueryPropertyConstants.seQueryPropertyRevisionNumber,
        ["project"] = QueryPropertyConstants.seQueryPropertyProject,
        ["material"] = QueryPropertyConstants.seQueryPropertyMaterial,
        ["custom"] = QueryPropertyConstants.seQueryPropertyCustom,
    };

    private static readonly Dictionary<string, QueryConditionConstants> Conditions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["contains"] = QueryConditionConstants.seQueryConditionContains,
        ["is"] = QueryConditionConstants.seQueryConditionIs,
        ["is_not"] = QueryConditionConstants.seQueryConditionIsNot,
    };

    private static readonly Dictionary<string, QueryScopeConstants> Scopes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = QueryScopeConstants.seQueryScopeAllParts,
        ["shown"] = QueryScopeConstants.seQueryScopeShownParts,
        ["hidden"] = QueryScopeConstants.seQueryScopeHiddenParts,
        ["selected"] = QueryScopeConstants.seQueryScopeSelectedParts,
    };

    /// <summary>A matched component: top-level occurrence and, for a part inside a subassembly, the names down to it.</summary>
    private sealed record Match(Occurrence Top, string[] Chain, string File);

    public static object Find(SeContext context, string? documentHandle, IReadOnlyList<ComponentCriterion>? criteria, string scope,
        bool subassemblies, string? name, string display, int limit)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        (QueryPropertyConstants Property, string Custom, QueryConditionConstants Condition, string Value)[] parsed = Parse(criteria);
        QueryScopeConstants scopeValue = ScopeOf(scope);
        string? title = Clean(name);
        if (title is not null)
        {
            RequireFreeName(assembly, title, null);
        }

        SelectSet set = assembly.SelectSet;
        // The caller's selection: a query with the scope 'selected' is evaluated on it, and a failed find puts it back.
        object[] selection = Late.Items(set).ToArray();
        bool onSelection = scopeValue == QueryScopeConstants.seQueryScopeSelectedParts;
        string queryName = title ?? "mcp_find_" + Guid.NewGuid().ToString("N")[..12];
        Queries queries = assembly.Queries;
        Query query;
        try
        {
            query = queries.Add(queryName);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("query_failed", $"Solid Edge did not create the query '{queryName}': {ex.Message}", "Pass another name.", ex);
        }

        List<Match> matches;
        try
        {
            query.SearchSubassemblies = subassemblies;
            foreach ((QueryPropertyConstants property, string custom, QueryConditionConstants condition, string value) in parsed)
            {
                try
                {
                    query.AddCriteria(property, custom, condition, value);
                }
                catch (COMException ex) when (!ComErrors.IsGone(ex))
                {
                    throw new ToolException("query_failed", $"Solid Edge refused the criterion on '{property}': {ex.Message}",
                        "Check the property and the value (se_properties lists the properties of a component file).", ex);
                }
            }

            // Each criterion evaluated the query and replaced the selection: with 'selected', put the caller's back first.
            if (onSelection)
            {
                Restore(set, selection);
            }

            matches = Evaluate(assembly, query, scopeValue, onSelection ? selection : null);
        }
        catch
        {
            TryRemove(queries, queryName);
            TryRestore(set, selection);
            throw;
        }
        finally
        {
            if (title is null)
            {
                TryRemove(queries, queryName);
            }
        }

        return Result(context, assembly, matches, display, limit, title is null ? null : ("query", title));
    }

    public static object Zone(SeContext context, string? documentHandle, double[]? box, bool overlap, string? name, string display, int limit)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        double[] corners = Box(box);
        string? title = Clean(name);
        if (title is not null)
        {
            RequireFreeName(assembly, title, null);
        }

        Zones zones = assembly.Zones;
        int before = zones.Count;
        SolidEdgeAssembly.Zone zone;
        try
        {
            zone = zones.Add(Units.MmToM(corners[0]), Units.MmToM(corners[1]), Units.MmToM(corners[2]),
                Units.MmToM(corners[3]), Units.MmToM(corners[4]), Units.MmToM(corners[5]));
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("query_failed", $"Solid Edge did not create the zone: {ex.Message}", "Check the box corners (mm).", ex);
        }

        string zoneName = zone.Name;
        List<Match> matches;
        try
        {
            zone.Overlap = overlap;
            if (title is not null)
            {
                zone.Name = title;
                if (!string.Equals(zone.Name, title, StringComparison.Ordinal))
                {
                    throw new ToolException("query_failed", $"Solid Edge kept the zone name '{zone.Name}' instead of '{title}'.", "Pass another name.");
                }
            }

            matches = ZoneMatches(assembly, zone);
        }
        catch
        {
            TryDeleteZone(zones, zone);
            throw;
        }
        finally
        {
            if (title is null)
            {
                TryDeleteZone(zones, zone);
            }
        }

        if (title is null && zones.Count != before)
        {
            throw new ToolException("query_failed", $"Solid Edge kept the temporary zone '{zoneName}' in the assembly.",
                $"Delete it with se_component_query delete, name '{zoneName}'.");
        }

        return Result(context, assembly, matches, display, limit, title is null ? null : ("zone", title));
    }

    /// <summary>Runs a saved query (on the current selection when its scope is 'selected') or a saved zone.</summary>
    public static object Run(SeContext context, string? documentHandle, string? name, string display, int limit)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string title = RequireName(name, "run");
        if (FindQuery(assembly, title) is { } query)
        {
            QueryScopeConstants scope = query.Scope;
            object[]? selection = scope == QueryScopeConstants.seQueryScopeSelectedParts ? Late.Items(assembly.SelectSet).ToArray() : null;
            List<Match> matches = Evaluate(assembly, query, scope, selection);
            return Result(context, assembly, matches, display, limit, ("query", assembly.Queries.get_QueryName(query)));
        }

        if (FindZone(assembly, title) is { } zone)
        {
            return Result(context, assembly, ZoneMatches(assembly, zone), display, limit, ("zone", zone.Name));
        }

        throw NotFound(title);
    }

    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        Queries queries = assembly.Queries;
        object[] savedQueries = Items<Query>(queries)
            .Select(q => (Query: q, Name: queries.get_QueryName(q)))
            .Where(q => q.Name != QuickQuery)
            .Select(q => (object)new
            {
                name = q.Name,
                criteria = q.Query.CriteriaCount,
                scope = ScopeName(q.Query.Scope),
                subassemblies = q.Query.SearchSubassemblies,
            })
            .ToArray();
        object[] zones = Items<SolidEdgeAssembly.Zone>(assembly.Zones).Select(z => (object)new
        {
            name = z.Name,
            box = BoxOf(z),
            overlap = z.Overlap,
        }).ToArray();
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            queries = savedQueries,
            zones,
            note = savedQueries.Length > 0 ? "Solid Edge does not give back the criteria of a saved query; run it to see what it finds." : null,
        };
    }

    public static object Rename(SeContext context, string? documentHandle, string? name, string? newName)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string title = RequireName(name, "rename");
        string target = Clean(newName) ?? throw ToolException.InvalidArgument("'newName' is required for rename.");
        if (FindQuery(assembly, title) is { } query)
        {
            string old = assembly.Queries.get_QueryName(query);
            RequireFreeName(assembly, target, old);
            assembly.Queries.Rename(old, target);
            string now = assembly.Queries.get_QueryName(query);
            return now == target
                ? new { renamed = old, query = now }
                : throw new ToolException("query_failed", $"Solid Edge kept the query name '{now}'.", "Pass another name.");
        }

        if (FindZone(assembly, title) is { } zone)
        {
            string old = zone.Name;
            RequireFreeName(assembly, target, old);
            zone.Name = target;
            return zone.Name == target
                ? new { renamed = old, zone = zone.Name }
                : throw new ToolException("query_failed", $"Solid Edge kept the zone name '{zone.Name}'.", "Pass another name.");
        }

        throw NotFound(title);
    }

    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string title = RequireName(name, "delete");
        if (FindQuery(assembly, title) is { } query)
        {
            string old = assembly.Queries.get_QueryName(query);
            assembly.Queries.Remove(old);
            return FindQuery(assembly, old) is null
                ? new { deleted = old, kind = "query" }
                : throw new ToolException("query_failed", $"Solid Edge did not delete the query '{old}'.");
        }

        if (FindZone(assembly, title) is { } zone)
        {
            string old = zone.Name;
            assembly.Zones.Delete(old);
            return FindZone(assembly, old) is null
                ? new { deleted = old, kind = "zone" }
                : throw new ToolException("query_failed", $"Solid Edge did not delete the zone '{old}'.");
        }

        throw NotFound(title);
    }

    /// <summary>
    /// Evaluates a query with the given scope and reads its matches from the selection. Setting the scope evaluates it; if the
    /// selection does not hold the matches yet, the scope is switched away and back. <paramref name="selection"/>: the
    /// selection to evaluate a 'selected' scope on (null for the other scopes).
    /// </summary>
    private static List<Match> Evaluate(AssemblyDocument assembly, Query query, QueryScopeConstants scope, object[]? selection)
    {
        SelectSet set = assembly.SelectSet;
        query.Scope = scope;
        int count = query.MatchesCount;
        if (set.Count != count)
        {
            query.Scope = scope == QueryScopeConstants.seQueryScopeAllParts ? QueryScopeConstants.seQueryScopeShownParts : QueryScopeConstants.seQueryScopeAllParts;
            if (selection is not null)
            {
                Restore(set, selection);
            }

            query.Scope = scope;
            count = query.MatchesCount;
        }

        List<Match> matches = Read(set);
        if (matches.Count != count)
        {
            throw new ToolException("query_failed", $"The query found {count} components but {matches.Count} are selected.",
                "Run it again; if the person is selecting in Solid Edge at the same time, wait until they finish.");
        }

        return matches;
    }

    private static List<Match> ZoneMatches(AssemblyDocument assembly, SolidEdgeAssembly.Zone zone)
    {
        // AddPartsToSelectSet replaces the selection with the parts in the zone.
        zone.AddPartsToSelectSet();
        return Read(assembly.SelectSet);
    }

    /// <summary>Top-level occurrences and references to parts inside subassemblies, as the query or zone selected them.</summary>
    private static List<Match> Read(SelectSet set)
    {
        var matches = new List<Match>();
        foreach (object item in Late.Items(set))
        {
            switch (item)
            {
                case Occurrence occurrence:
                    matches.Add(new Match(occurrence, [], occurrence.OccurrenceFileName));
                    break;
                case Reference reference:
                    matches.Add(Nested(reference));
                    break;
            }
        }

        return matches;
    }

    private static Match Nested(Reference reference)
    {
        // Solid Edge gives back an array sized to the chain (verified three levels down), whatever size is passed.
        Array bound = new object[1];
        reference.GetOccurrencesInPath(out object top, out int _, out int boundCount, ref bound);
        string[] chain = bound.Cast<object?>().Take(boundCount).OfType<SubOccurrence>().Select(s => s.Name).ToArray();
        var leaf = reference.Object as Occurrence;
        if (chain.Length == 0 && leaf is not null)
        {
            chain = [leaf.Name];
        }

        return new Match((Occurrence)top, chain, leaf?.OccurrenceFileName ?? "");
    }

    private static object Result(SeContext context, AssemblyDocument assembly, List<Match> matches, string display, int limit, (string Kind, string Name)? saved)
    {
        var document = (SolidEdgeDocument)assembly;
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var result = new Dictionary<string, object?>
        {
            ["document"] = context.DocumentHandle(document),
            ["count"] = matches.Count,
            ["components"] = matches.Take(take).Select(m => m.Chain.Length == 0
                ? (object)new { handle = context.Register(m.Top, "occurrence", document), name = m.Top.Name, file = m.File }
                : new { path = Path(m), name = m.Chain[^1], file = m.File, nested = true, occurrence = context.Register(m.Top, "occurrence", document) })
                .ToArray(),
            ["more"] = matches.Count > take,
            ["selected"] = assembly.SelectSet.Count,
        };
        if (saved is { } kept)
        {
            result[kept.Kind] = kept.Name;
        }

        if (!string.Equals(display, "select", StringComparison.OrdinalIgnoreCase))
        {
            if (matches.Count == 0)
            {
                result["note"] = "Nothing matched, so the display was not changed.";
            }
            else
            {
                try
                {
                    result["display"] = Display(assembly, matches, display, take);
                }
                finally
                {
                    context.ModelChanged(document, "components were shown or hidden");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Shows or hides the matches. show_only shows the matches with the levels above them (a matched subassembly with
    /// everything in it) and hides every other component, down into the subassemblies. Lists the components whose
    /// visibility changed; if Solid Edge refuses one, the changes made before it are undone.
    /// </summary>
    private static object Display(AssemblyDocument assembly, List<Match> matches, string display, int limit)
    {
        var changes = new List<(string Path, bool Visible, Action<bool> Write)>();
        int unchanged = 0;
        void Set(string path, bool visible, Func<bool> read, Action<bool> write)
        {
            if (read() == visible)
            {
                unchanged++;
                return;
            }

            write(visible);
            changes.Add((path, visible, write));
            if (read() != visible)
            {
                throw new ToolException("query_failed", $"Solid Edge left '{path}' {(visible ? "hidden" : "shown")}; the changes made before were undone.",
                    "Show or hide it with se_assembly show/hide.");
            }
        }

        void SetTop(Occurrence occurrence, bool visible) =>
            Set(occurrence.Name, visible, () => occurrence.Visible, v => occurrence.Visible = v);

        void SetChild(SubOccurrence child, string path, bool visible) =>
            Set(path, visible, () => child.Visible, v => child.Visible = v);

        // An empty subassembly has no SubOccurrences collection (null).
        IEnumerable<SubOccurrence> ChildrenOf(SubOccurrences? children) => children is null ? [] : Items<SubOccurrence>(children);

        void ShowAll(SubOccurrences? children, string parent)
        {
            foreach (SubOccurrence child in ChildrenOf(children))
            {
                string path = $"{parent}/{child.Name}";
                SetChild(child, path, true);
                if (child.Subassembly)
                {
                    ShowAll(child.SubOccurrences, path);
                }
            }
        }

        void ShowOnlyIn(SubOccurrences? children, List<string[]> chains, string parent)
        {
            foreach (SubOccurrence child in ChildrenOf(children))
            {
                string path = $"{parent}/{child.Name}";
                List<string[]> mine = chains.Where(c => c[0] == child.Name).ToList();
                SetChild(child, path, mine.Count > 0);
                if (mine.Any(c => c.Length == 1))
                {
                    if (child.Subassembly)
                    {
                        ShowAll(child.SubOccurrences, path);
                    }
                }
                else if (mine.Count > 0)
                {
                    ShowOnlyIn(child.SubOccurrences, mine.Select(c => c[1..]).ToList(), path);
                }
            }
        }

        string mode = display.ToLowerInvariant();
        try
        {
            switch (mode)
            {
                case "hide":
                case "show":
                    bool visible = mode == "show";
                    foreach (Match match in matches)
                    {
                        if (match.Chain.Length == 0)
                        {
                            SetTop(match.Top, visible);
                            continue;
                        }

                        // A part inside a subassembly is shown with every level above it.
                        if (visible)
                        {
                            SetTop(match.Top, true);
                        }

                        SubOccurrences? children = match.Top.SubOccurrences;
                        string path = match.Top.Name;
                        for (int i = 0; i < match.Chain.Length; i++)
                        {
                            SubOccurrence child = Child(children, match.Chain[i], path);
                            path += "/" + child.Name;
                            if (visible || i == match.Chain.Length - 1)
                            {
                                SetChild(child, path, visible);
                            }

                            children = child.SubOccurrences;
                        }
                    }

                    break;
                case "show_only":
                    HashSet<string> topMatches = matches.Where(m => m.Chain.Length == 0).Select(m => m.Top.Name).ToHashSet(StringComparer.Ordinal);
                    Dictionary<string, List<string[]>> nested = matches.Where(m => m.Chain.Length > 0)
                        .GroupBy(m => m.Top.Name, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Select(m => m.Chain).ToList(), StringComparer.Ordinal);
                    foreach (Occurrence occurrence in Items<Occurrence>(assembly.Occurrences))
                    {
                        bool whole = topMatches.Contains(occurrence.Name);
                        bool inside = nested.TryGetValue(occurrence.Name, out List<string[]>? chains);
                        SetTop(occurrence, whole || inside);
                        if (whole && occurrence.Subassembly)
                        {
                            ShowAll(occurrence.SubOccurrences, occurrence.Name);
                        }
                        else if (inside)
                        {
                            ShowOnlyIn(occurrence.SubOccurrences, chains!, occurrence.Name);
                        }
                    }

                    break;
                default:
                    throw ToolException.InvalidArgument($"Unknown display '{display}'.", "Use select, show_only, hide or show.");
            }
        }
        catch
        {
            // Undo what was changed before the failure, newest first.
            for (int i = changes.Count - 1; i >= 0; i--)
            {
                try
                {
                    changes[i].Write(!changes[i].Visible);
                }
                catch (COMException)
                {
                    // Best effort: the caller reports the original failure.
                }
            }

            throw;
        }

        string[] shown = changes.Where(c => c.Visible).Select(c => c.Path).ToArray();
        string[] hidden = changes.Where(c => !c.Visible).Select(c => c.Path).ToArray();
        return new
        {
            mode,
            shown = shown.Take(limit).ToArray(),
            hidden = hidden.Take(limit).ToArray(),
            unchanged,
            more = shown.Length > limit || hidden.Length > limit,
        };
    }

    private static SubOccurrence Child(SubOccurrences? children, string name, string parent) =>
        (children is null ? null : Items<SubOccurrence>(children).FirstOrDefault(c => c.Name == name))
        ?? throw new ToolException("query_failed", $"'{parent}' has no component '{name}' any more.", "Run the query again.");

    private static (QueryPropertyConstants, string, QueryConditionConstants, string)[] Parse(IReadOnlyList<ComponentCriterion>? criteria)
    {
        if (criteria is not { Count: > 0 })
        {
            throw ToolException.InvalidArgument("'criteria' must have at least one criterion, e.g. {\"property\":\"material\",\"condition\":\"is\",\"value\":\"Steel\"}.");
        }

        return criteria.Select((c, i) =>
        {
            QueryPropertyConstants property = Properties.TryGetValue(c.Property ?? "", out QueryPropertyConstants p) ? p
                : throw ToolException.InvalidArgument($"criteria[{i}]: unknown property '{c.Property}'.", $"Use one of: {string.Join(", ", Properties.Keys)}.");
            QueryConditionConstants condition = Conditions.TryGetValue(c.Condition ?? "contains", out QueryConditionConstants k) ? k
                : throw ToolException.InvalidArgument($"criteria[{i}]: unknown condition '{c.Condition}'.", "Use contains, is or is_not.");
            string custom = "";
            if (property == QueryPropertyConstants.seQueryPropertyCustom)
            {
                custom = Clean(c.Custom) ?? throw ToolException.InvalidArgument($"criteria[{i}]: a custom criterion needs 'custom', the custom property name.");
            }

            return (property, custom, condition, c.Value ?? "");
        }).ToArray();
    }

    private static QueryScopeConstants ScopeOf(string scope) =>
        Scopes.TryGetValue(scope, out QueryScopeConstants value) ? value
            : throw ToolException.InvalidArgument($"Unknown scope '{scope}'.", "Use all, shown, hidden or selected.");

    private static string ScopeName(QueryScopeConstants scope) => Scopes.FirstOrDefault(s => s.Value == scope).Key ?? scope.ToString();

    private static double[] Box(double[]? box)
    {
        if (box is not { Length: 6 } || box.Any(v => !double.IsFinite(v)))
        {
            throw ToolException.InvalidArgument("'box' must be [x1,y1,z1, x2,y2,z2] in mm: two opposite corners of the region.");
        }

        double[] corners =
        [
            Math.Min(box[0], box[3]), Math.Min(box[1], box[4]), Math.Min(box[2], box[5]),
            Math.Max(box[0], box[3]), Math.Max(box[1], box[4]), Math.Max(box[2], box[5]),
        ];
        return corners[3] > corners[0] && corners[4] > corners[1] && corners[5] > corners[2]
            ? corners
            : throw ToolException.InvalidArgument("The box has no volume: its corners must differ in x, y and z.");
    }

    private static double[] BoxOf(SolidEdgeAssembly.Zone zone)
    {
        zone.GetPoints(out double x1, out double y1, out double z1, out double x2, out double y2, out double z2);
        return [Geo.Mm(x1), Geo.Mm(y1), Geo.Mm(z1), Geo.Mm(x2), Geo.Mm(y2), Geo.Mm(z2)];
    }

    private static Query? FindQuery(AssemblyDocument assembly, string name)
    {
        Queries queries = assembly.Queries;
        return Items<Query>(queries).FirstOrDefault(q =>
        {
            string title = queries.get_QueryName(q);
            return title != QuickQuery && string.Equals(title, name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static SolidEdgeAssembly.Zone? FindZone(AssemblyDocument assembly, string name) =>
        Items<SolidEdgeAssembly.Zone>(assembly.Zones).FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Queries and zones share the names: 'run' and 'delete' find either by name.</summary>
    private static void RequireFreeName(AssemblyDocument assembly, string name, string? self)
    {
        bool same = self is not null && string.Equals(self, name, StringComparison.OrdinalIgnoreCase);
        if (!same && (string.Equals(name, QuickQuery, StringComparison.OrdinalIgnoreCase) || FindQuery(assembly, name) is not null || FindZone(assembly, name) is not null))
        {
            throw ToolException.InvalidArgument($"The assembly already has a query or zone named '{name}'.", "Pass another name.");
        }
    }

    private static void Restore(SelectSet set, object[] selection)
    {
        set.RemoveAll();
        foreach (object item in selection)
        {
            set.Add(item);
        }
    }

    private static void TryRestore(SelectSet set, object[] selection)
    {
        try
        {
            Restore(set, selection);
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original failure.
        }
    }

    private static void TryRemove(Queries queries, string name)
    {
        try
        {
            queries.Remove(name);
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original result or failure.
        }
    }

    private static void TryDeleteZone(Zones zones, SolidEdgeAssembly.Zone zone)
    {
        try
        {
            zones.Delete(zone.Name);
        }
        catch (COMException)
        {
            // Best effort: the caller reports the original result or failure.
        }
    }

    private static string Path(Match match) => match.Top.Name + "/" + string.Join("/", match.Chain);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireName(string? name, string action) =>
        Clean(name) ?? throw ToolException.InvalidArgument($"'name' (a saved query or zone) is required for {action}.", "List them with se_component_query list.");

    private static ToolException NotFound(string name) =>
        new("not_found", $"The assembly has no query or zone named '{name}'.", "List them with se_component_query list.");
}
