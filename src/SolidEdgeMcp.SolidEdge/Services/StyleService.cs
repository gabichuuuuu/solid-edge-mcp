using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Styles of a document, as in its Styles dialog: dimension, text, character, line, fill, hatch, dash, drawing view, table,
/// smart frame, view and face styles. Each type is a collection of the document (DimensionStyles…) and each style is read
/// and changed by property name through its interop interface, in API units (metres, radians), with colours as #RRGGBB.
/// </summary>
public static class StyleService
{
    private sealed record Kind(string Key, string Collection, Type Interface, string Title, int? ImportType = null,
        bool HasActive = false, bool Removable = true, bool ParentInAdd = true);

    // ImportType: seStyleTypeConstants of Document.ImportStyles2 (igDimensionStyle 0 … igTextStyle 6).
    private static readonly Kind[] Kinds =
    [
        new("dimension", "DimensionStyles", typeof(SolidEdgeFrameworkSupport.DimensionStyle), "dimension style", 0, HasActive: true),
        new("text", "TextStyles", typeof(TextStyle), "text style", 6, HasActive: true),
        new("character", "TextCharStyles", typeof(TextCharStyle), "character style"),
        new("line", "LinearStyles", typeof(LinearStyle), "line style", 4, HasActive: true),
        new("fill", "FillStyles", typeof(FillStyle), "fill style", 2, HasActive: true),
        new("hatch", "HatchPatternStyles", typeof(HatchPatternStyle), "hatch pattern style", 3),
        new("dash", "DashStyles", typeof(DashStyle), "dash style", ParentInAdd: false),
        new("drawing_view", "DrawingViewStyles", typeof(SolidEdgeFrameworkSupport.DrawingViewStyle), "drawing view style", 1, HasActive: true),
        new("table", "TableStyles", typeof(SolidEdgeFrameworkSupport.TableStyle), "table style", 5),
        new("frame", "SmartFrame2dStyles", typeof(SolidEdgeFrameworkSupport.SmartFrame2dStyle), "smart frame style", Removable: false),
        new("view", "ViewStyles", typeof(ViewStyle), "view style", HasActive: true),
        new("face", "FaceStyles", typeof(FaceStyle), "face style"),
    ];

    public static IReadOnlyList<string> TypeNames { get; } = [.. Kinds.Select(k => k.Key)];

    private const string UnitsNote = "Values in API units: lengths in metres, angles in radians; colours as #RRGGBB; enumerations by name.";

    /// <summary>Style types this document has, with how many styles and the active one.</summary>
    public static object Types(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        var types = new List<object>();
        foreach (Kind kind in Kinds)
        {
            if (Late.TryGet(document, kind.Collection) is { } collection)
            {
                types.Add(new { type = kind.Key, count = Late.Count(collection), active = ActiveName(document, kind, collection) });
            }
        }

        return new { document = context.DocumentHandle(document), types };
    }

    public static object List(SeContext context, string? documentHandle, string type, string? filter, int offset, int limit)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        string[] names = [.. Names(collection).Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        int start = Math.Max(offset, 0);
        string[] page = [.. names.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems))];
        return new
        {
            document = context.DocumentHandle(document),
            type = kind.Key,
            active = ActiveName(document, kind, collection),
            count = names.Length,
            offset = start,
            returned = page.Length,
            more = start + page.Length < names.Length,
            styles = page,
        };
    }

    /// <summary>Every property of a style with its value.</summary>
    public static object Get(SeContext context, string? documentHandle, string type, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        (object style, string styleName) = Find(document, kind, collection, name);
        (Dictionary<string, object?> values, List<string> readOnly, List<string> unreadable) = ComProperties.ReadAll(style, kind.Interface);
        return new
        {
            document = context.DocumentHandle(document),
            type = kind.Key,
            name = styleName,
            active = string.Equals(ActiveName(document, kind, collection), styleName, StringComparison.OrdinalIgnoreCase),
            properties = values,
            readOnly,
            unreadable = unreadable.Count > 0 ? unreadable : null,
            units = UnitsNote,
        };
    }

    /// <summary>Changes one property of a style and reads it back.</summary>
    public static object Set(SeContext context, string? documentHandle, string type, string name, string property, JsonElement value)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        (object style, string styleName) = Find(document, kind, collection, name);
        string owner = $"the {kind.Title} '{styleName}'";
        PropertyInfo target = ComProperties.Find(kind.Interface, property, owner);
        (object? previous, object? current) = ComProperties.Set(context, style, target, value, owner);
        if (kind.Key == "view")
        {
            ViewService.ModelView(document)?.Update();
        }

        return new
        {
            document = context.DocumentHandle(document),
            type = kind.Key,
            name = target.Name is "Name" or "StyleName" && current is string renamed ? renamed : styleName,
            property = target.Name,
            previous,
            value = current,
            restore = $"To undo, set it back to {JsonSerializer.Serialize(previous)}.",
        };
    }

    /// <summary>Makes a style the active one of its type (for view styles: the style of the document's window).</summary>
    public static object Activate(SeContext context, string? documentHandle, string type, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        if (!kind.HasActive)
        {
            throw new ToolException("not_supported", $"Solid Edge has no active {kind.Title}: styles of this type are chosen where they are used.");
        }

        (_, string styleName) = Find(document, kind, collection, name);
        string? previous = ActiveName(document, kind, collection);
        if (kind.Key == "view")
        {
            SolidEdgeFramework.View view = ViewService.ModelView(document)
                ?? throw new ToolException("no_window", $"'{SeContext.SafeName(document)}' has no model window.", "Activate the document (se_documents activate).");
            view.Style = styleName;
            view.Update();
        }
        else
        {
            Late.Set(collection, "Active", styleName);
        }

        string? active = ActiveName(document, kind, collection);
        if (!string.Equals(active, styleName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("value_rejected", $"Solid Edge kept '{active}' as the active {kind.Title}.");
        }

        return new { document = context.DocumentHandle(document), type = kind.Key, active, previous };
    }

    /// <summary>New style copied from another one (by default the active one, or the first).</summary>
    public static object Add(SeContext context, string? documentHandle, string type, string name, string? basedOn)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        string wanted = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("Action 'add' requires 'name'.") : name.Trim();
        if (TryFind(collection, wanted) is not null)
        {
            throw new ToolException("style_exists", $"'{SeContext.SafeName(document)}' already has a {kind.Title} named '{wanted}'.", "Choose another name.");
        }

        string? parent = basedOn is not null
            ? Find(document, kind, collection, basedOn).Name
            : ActiveName(document, kind, collection) ?? Names(collection).FirstOrDefault();
        try
        {
            _ = kind.ParentInAdd ? Late.Call(collection, "Add", wanted, parent ?? "") : Late.Call(collection, "Add", wanted);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("style_failed", $"Solid Edge did not create the {kind.Title} '{wanted}': {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        _ = TryFind(collection, wanted) ?? throw new ToolException("style_failed", $"Solid Edge did not create the {kind.Title} '{wanted}'.");
        return new { document = context.DocumentHandle(document), type = kind.Key, created = wanted, basedOn = kind.ParentInAdd ? parent : null };
    }

    public static object Remove(SeContext context, string? documentHandle, string type, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        if (!kind.Removable)
        {
            throw new ToolException("not_supported", $"Solid Edge does not delete {kind.Title}s through the API.");
        }

        (_, string styleName) = Find(document, kind, collection, name);
        if (string.Equals(ActiveName(document, kind, collection), styleName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("style_in_use", $"'{styleName}' is the active {kind.Title}.", "Activate another one first (action 'activate').");
        }

        try
        {
            Late.Call(collection, "Remove", styleName);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("style_in_use", $"Solid Edge did not delete the {kind.Title} '{styleName}': {ComErrors.Unwrap(ex).Message.Trim()}",
                "It may be in use or be one of the styles Solid Edge needs.", ex);
        }

        return TryFind(collection, styleName) is null
            ? new { document = context.DocumentHandle(document), type = kind.Key, removed = styleName }
            : throw new ToolException("style_in_use", $"The {kind.Title} '{styleName}' is still in the document.", "It may be in use or be one of the styles Solid Edge needs.");
    }

    /// <summary>Copies the styles of one type from another open document (Solid Edge's ImportStyles2).</summary>
    public static object Import(SeContext context, string? documentHandle, string type, string from, bool replace)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        if (kind.ImportType is not { } importType)
        {
            throw new ToolException("not_supported", $"Solid Edge does not import {kind.Title}s between documents.",
                "It imports dimension, text, line, fill, hatch, drawing view and table styles.");
        }

        SolidEdgeDocument source = context.GetDocument(from);
        if (ReferenceEquals(source, document) || string.Equals(source.FullName, document.FullName, StringComparison.OrdinalIgnoreCase) && source.FullName.Length > 0)
        {
            throw ToolException.InvalidArgument("'from' must be another open document.");
        }

        HashSet<string> before = new(Names(collection), StringComparer.OrdinalIgnoreCase);
        try
        {
            Late.Call(document, "ImportStyles2", importType, replace, source);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsDisconnected(ex))
        {
            throw new ToolException("style_failed", $"Solid Edge did not import the {kind.Title}s: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        string[] after = Names(Late.Get(document, kind.Collection)!);
        return new
        {
            document = context.DocumentHandle(document),
            from = context.DocumentHandle(source),
            type = kind.Key,
            replace,
            imported = after.Where(n => !before.Contains(n)).ToArray(),
            count = after.Length,
        };
    }

    private static (SolidEdgeDocument Document, Kind Kind, object Collection) Open(SeContext context, string? documentHandle, string type)
    {
        string key = (type ?? "").Trim().ToLowerInvariant();
        Kind kind = Kinds.FirstOrDefault(k => k.Key == key)
            ?? throw ToolException.InvalidArgument($"Unknown style type '{type}'.", $"Use one of: {string.Join(", ", TypeNames)}.");
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object collection = Late.TryGet(document, kind.Collection)
            ?? throw new ToolException("not_supported", $"'{SeContext.SafeName(document)}' has no {kind.Title}s.", "List the style types of the document with action 'types'.");
        return (document, kind, collection);
    }

    private static (object Style, string Name) Find(SolidEdgeDocument document, Kind kind, object collection, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument($"'name' (the {kind.Title}) is required.");
        }

        return TryFind(collection, name.Trim()) is { } found
            ? found
            : throw ToolException.NotFound($"'{SeContext.SafeName(document)}' has no {kind.Title} '{name}'.{NameMatch.Hint(Names(collection), name)}",
                "List them with action 'list'.");
    }

    private static (object Style, string Name)? TryFind(object collection, string name)
    {
        foreach (object style in Late.Items(collection))
        {
            if (NameOf(style) is { } styleName && string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase))
            {
                return (style, styleName);
            }
        }

        return null;
    }

    private static string[] Names(object collection) => [.. Late.Items(collection).Select(NameOf).OfType<string>()];

    /// <summary>View and face styles call it StyleName; the others, Name.</summary>
    private static string? NameOf(object style) => Late.TryGetString(style, "Name") ?? Late.TryGetString(style, "StyleName");

    private static string? ActiveName(SolidEdgeDocument document, Kind kind, object collection)
    {
        if (!kind.HasActive)
        {
            return null;
        }

        if (kind.Key == "view")
        {
            try
            {
                return ViewService.ModelView(document)?.Style;
            }
            catch (COMException)
            {
                return null;
            }
        }

        return Late.TryGetString(collection, "Active");
    }
}
