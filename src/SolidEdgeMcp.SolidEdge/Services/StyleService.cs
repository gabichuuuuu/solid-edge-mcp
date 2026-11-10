// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
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
    private sealed record Kind(StyleType Key, string Collection, Type Interface, string Title, int? ImportType = null,
        bool HasActive = false, bool Removable = true, bool ParentInAdd = true);

    // ImportType: seStyleTypeConstants of Document.ImportStyles2 (igDimensionStyle 0 … igTextStyle 6).
    private static readonly Kind[] Kinds =
    [
        new(StyleType.Dimension, "DimensionStyles", typeof(SolidEdgeFrameworkSupport.DimensionStyle), "dimension style", 0, HasActive: true),
        new(StyleType.Text, "TextStyles", typeof(TextStyle), "text style", 6, HasActive: true),
        new(StyleType.Character, "TextCharStyles", typeof(TextCharStyle), "character style"),
        new(StyleType.Line, "LinearStyles", typeof(LinearStyle), "line style", 4, HasActive: true),
        new(StyleType.Fill, "FillStyles", typeof(FillStyle), "fill style", 2, HasActive: true),
        new(StyleType.Hatch, "HatchPatternStyles", typeof(HatchPatternStyle), "hatch pattern style", 3),
        new(StyleType.Dash, "DashStyles", typeof(DashStyle), "dash style", ParentInAdd: false),
        new(StyleType.DrawingView, "DrawingViewStyles", typeof(SolidEdgeFrameworkSupport.DrawingViewStyle), "drawing view style", 1, HasActive: true),
        new(StyleType.Table, "TableStyles", typeof(SolidEdgeFrameworkSupport.TableStyle), "table style", 5),
        new(StyleType.Frame, "SmartFrame2dStyles", typeof(SolidEdgeFrameworkSupport.SmartFrame2dStyle), "smart frame style", Removable: false),
        new(StyleType.View, "ViewStyles", typeof(ViewStyle), "view style", HasActive: true),
        new(StyleType.Face, "FaceStyles", typeof(FaceStyle), "face style"),
    ];

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
                types.Add(new { type = ArgumentNames.Snake(kind.Key), count = Late.Count(collection), active = ActiveName(document, kind, collection) });
            }
        }

        return new { document = context.DocumentHandle(document), types };
    }

    public static object List(SeContext context, string? documentHandle, StyleType type, string? filter, int offset, int limit)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        string[] names = [.. Names(collection).Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        int start = Math.Max(offset, 0);
        string[] page = [.. names.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems))];
        return new
        {
            document = context.DocumentHandle(document),
            type = ArgumentNames.Snake(kind.Key),
            active = ActiveName(document, kind, collection),
            count = names.Length,
            offset = start,
            returned = page.Length,
            more = start + page.Length < names.Length,
            styles = page,
        };
    }

    /// <summary>Every property of a style with its value.</summary>
    public static object Get(SeContext context, string? documentHandle, StyleType type, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        (object style, string styleName) = Find(document, kind, collection, name);
        (Dictionary<string, object?> values, List<string> readOnly, List<string> unreadable) = ComProperties.ReadAll(style, kind.Interface);
        return new
        {
            document = context.DocumentHandle(document),
            type = ArgumentNames.Snake(kind.Key),
            name = styleName,
            active = string.Equals(ActiveName(document, kind, collection), styleName, StringComparison.OrdinalIgnoreCase),
            properties = values,
            readOnly,
            unreadable = unreadable.Count > 0 ? unreadable : null,
            units = UnitsNote,
        };
    }

    /// <summary>Changes one property of a style and reads it back.</summary>
    public static object Set(SeContext context, string? documentHandle, StyleType type, string name, string property, JsonElement value)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        (object style, string styleName) = Find(document, kind, collection, name);
        string owner = $"the {kind.Title} '{styleName}'";
        PropertyInfo target = ComProperties.Find(kind.Interface, property, owner);
        (object? previous, object? current) = ComProperties.Set(context, style, target, value, owner);
        if (kind.Key == StyleType.View)
        {
            ViewService.ModelView(document)?.Update();
        }

        return new
        {
            document = context.DocumentHandle(document),
            type = ArgumentNames.Snake(kind.Key),
            name = target.Name is "Name" or "StyleName" && current is string renamed ? renamed : styleName,
            property = target.Name,
            previous,
            value = current,
            restore = $"To undo, set it back to {JsonSerializer.Serialize(previous)}.",
        };
    }

    /// <summary>
    /// The 8 lights of a view style (numbered 1-8 here, 0-7 in the API): colour, intensity (its brightest channel) and direction
    /// (theta, phi in degrees). Verified: SetLight with (1, 0.5, 0.25) reads back #FF8040, and SetLightIntensity(0.8) turns the
    /// light grey at 0.8, so a colour and an intensity are not set together.
    /// </summary>
    public static object Lights(SeContext context, string? documentHandle, string name)
    {
        (SolidEdgeDocument document, ViewStyle style, string styleName) = OpenView(context, documentHandle, name);
        object[] lights = Enumerable.Range(1, style.NumLights).Select(n => DescribeLight(style, n)).ToArray();
        return new { document = context.DocumentHandle(document), name = styleName, count = lights.Length, lights };
    }

    /// <summary>Changes one light of a view style: colour '#RRGGBB' or grey intensity 0-1, and direction [theta, phi] in degrees.</summary>
    public static object SetLight(SeContext context, string? documentHandle, string name, int light, string? color, double? intensity, double[]? direction)
    {
        (SolidEdgeDocument document, ViewStyle style, string styleName) = OpenView(context, documentHandle, name);
        if (light < 1 || light > style.NumLights)
        {
            throw ToolException.InvalidArgument($"'light' must be 1 to {style.NumLights}.");
        }

        if (color is not null && intensity is not null)
        {
            throw ToolException.InvalidArgument("Give 'color' or 'intensity', not both: Solid Edge sets the intensity as a grey light.");
        }

        if (color is null && intensity is null && direction is null)
        {
            throw ToolException.InvalidArgument("set_light needs 'color', 'intensity' or 'direction'.");
        }

        int n = light - 1;
        object previous = DescribeLight(style, light);
        style.GetLight(n, out float red, out float green, out float blue, out float theta, out float phi);
        if (color is not null)
        {
            (red, green, blue) = Rgb.Channels(color);
        }
        else if (intensity is { } level)
        {
            if (!double.IsFinite(level) || level < 0 || level > 1)
            {
                throw ToolException.InvalidArgument("'intensity' must be between 0 and 1.");
            }

            red = green = blue = (float)level;
        }

        if (direction is not null)
        {
            if (direction is not [double t, double p] || !double.IsFinite(t) || !double.IsFinite(p))
            {
                throw ToolException.InvalidArgument("'direction' must be [theta, phi] in degrees.");
            }

            (theta, phi) = ((float)Units.DegToRad(t), (float)Units.DegToRad(p));
        }

        style.SetLight(n, red, green, blue, theta, phi);
        style.GetLight(n, out float r2, out float g2, out float b2, out float t2, out float p2);
        if (Math.Abs(r2 - red) > 0.005 || Math.Abs(g2 - green) > 0.005 || Math.Abs(b2 - blue) > 0.005
            || Math.Abs(t2 - theta) > 1e-3 || Math.Abs(p2 - phi) > 1e-3)
        {
            throw new ToolException("style_not_changed", $"Solid Edge did not set light {light} of the view style '{styleName}' as asked.",
                "Read it with action 'lights'.");
        }

        ViewService.ModelView(document)?.Update();
        return new { document = context.DocumentHandle(document), name = styleName, previous, light = DescribeLight(style, light) };
    }

    private static (SolidEdgeDocument Document, ViewStyle Style, string Name) OpenView(SeContext context, string? documentHandle, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, StyleType.View);
        (object style, string styleName) = Find(document, kind, collection, name);
        return (document, (ViewStyle)style, styleName);
    }

    private static object DescribeLight(ViewStyle style, int light)
    {
        style.GetLight(light - 1, out float red, out float green, out float blue, out float theta, out float phi);
        return new
        {
            light,
            color = Rgb.Hex(red, green, blue),
            intensity = Math.Round(Math.Max(red, Math.Max(green, blue)), 4),
            direction = new[] { Math.Round(Units.RadToDeg(theta), 3), Math.Round(Units.RadToDeg(phi), 3) },
        };
    }

    /// <summary>Makes a style the active one of its type (for view styles: the style of the document's window).</summary>
    public static object Activate(SeContext context, string? documentHandle, StyleType type, string name)
    {
        (SolidEdgeDocument document, Kind kind, object collection) = Open(context, documentHandle, type);
        if (!kind.HasActive)
        {
            throw new ToolException("not_supported", $"Solid Edge has no active {kind.Title}: styles of this type are chosen where they are used.");
        }

        (_, string styleName) = Find(document, kind, collection, name);
        string? previous = ActiveName(document, kind, collection);
        if (kind.Key == StyleType.View)
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

        return new { document = context.DocumentHandle(document), type = ArgumentNames.Snake(kind.Key), active, previous };
    }

    /// <summary>New style copied from another one (by default the active one, or the first).</summary>
    public static object Add(SeContext context, string? documentHandle, StyleType type, string name, string? basedOn)
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
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            throw new ToolException("style_failed", $"Solid Edge did not create the {kind.Title} '{wanted}': {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        _ = TryFind(collection, wanted) ?? throw new ToolException("style_failed", $"Solid Edge did not create the {kind.Title} '{wanted}'.");
        return new { document = context.DocumentHandle(document), type = ArgumentNames.Snake(kind.Key), created = wanted, basedOn = kind.ParentInAdd ? parent : null };
    }

    public static object Remove(SeContext context, string? documentHandle, StyleType type, string name)
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
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            throw new ToolException("style_in_use", $"Solid Edge did not delete the {kind.Title} '{styleName}': {ComErrors.Unwrap(ex).Message.Trim()}",
                "It may be in use or be one of the styles Solid Edge needs.", ex);
        }

        return TryFind(collection, styleName) is null
            ? new { document = context.DocumentHandle(document), type = ArgumentNames.Snake(kind.Key), removed = styleName }
            : throw new ToolException("style_in_use", $"The {kind.Title} '{styleName}' is still in the document.", "It may be in use or be one of the styles Solid Edge needs.");
    }

    /// <summary>Copies the styles of one type from another open document (Solid Edge's ImportStyles2).</summary>
    public static object Import(SeContext context, string? documentHandle, StyleType type, string from, bool replace)
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
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
        {
            throw new ToolException("style_failed", $"Solid Edge did not import the {kind.Title}s: {ComErrors.Unwrap(ex).Message.Trim()}", null, ex);
        }

        string[] after = Names(Late.Get(document, kind.Collection)!);
        return new
        {
            document = context.DocumentHandle(document),
            from = context.DocumentHandle(source),
            type = ArgumentNames.Snake(kind.Key),
            replace,
            imported = after.Where(n => !before.Contains(n)).ToArray(),
            count = after.Length,
        };
    }

    private static (SolidEdgeDocument Document, Kind Kind, object Collection) Open(SeContext context, string? documentHandle, StyleType type)
    {
        Kind kind = Kinds.FirstOrDefault(k => k.Key == type)
            ?? throw new ArgumentOutOfRangeException(nameof(type));
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

        if (kind.Key == StyleType.View)
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
