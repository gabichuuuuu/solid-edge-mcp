// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Body = SolidEdgeGeometry.Body;
using Face = SolidEdgeGeometry.Face;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Colours of parts, sheet metal and assemblies through the face styles of the document: list, create and delete styles,
/// and apply them to the body, faces, features or assembly components (or remove those overrides). A colour is the diffuse
/// colour of the style as #RRGGBB, with an opacity from 0 (transparent) to 1.
/// </summary>
public static class AppearanceService
{
    private sealed record Target(string Handle, string Kind, object Value, SolidEdgeDocument Owner);

    public static object List(SeContext context, string? documentHandle, string? filter, int offset, int limit)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        FaceStyle[] styles = Styles(document)
            .Where(s => filter is null || s.StyleName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        int start = Math.Max(offset, 0);
        object[] items = styles.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems)).Select(Describe).ToArray();
        return new
        {
            document = context.DocumentHandle(document),
            count = styles.Length,
            offset = start,
            returned = items.Length,
            more = start + items.Length < styles.Length,
            styles = items,
        };
    }

    /// <summary>New style with a diffuse colour and an opacity; an existing name is an error (styles are not overwritten).</summary>
    public static object Create(SeContext context, string? documentHandle, string? name, string? color, double? opacity)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("Action 'create' requires 'name'.");
        }

        (float r, float g, float b) = ParseColor(color ?? throw ToolException.InvalidArgument("Action 'create' requires 'color' as '#RRGGBB'."));
        float alpha = Opacity(opacity);
        if (Find(document, name) is not null)
        {
            throw new ToolException("style_exists", $"'{SeContext.SafeName(document)}' already has a style named '{name}'.",
                "Pick another name, or apply the existing style with action 'apply' and 'style'.");
        }

        FaceStyle style = NewStyle(document, name, r, g, b, alpha);
        return new { document = context.DocumentHandle(document), created = Describe(style) };
    }

    /// <summary>Deletes a style of the document. Solid Edge refuses to delete a style that is in use.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        string styleName = string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("Action 'delete' requires 'name'.") : name;
        styleName = (Find(document, styleName) ?? throw NotFound(document, styleName)).StyleName;
        try
        {
            FacesStyles(document).Remove(styleName);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("style_in_use", $"Solid Edge did not delete the style '{styleName}': it is in use.",
                "Reset (action 'reset') or re-colour the body, faces, features or components that use it, then delete it.");
        }

        return Find(document, styleName) is null
            ? new { document = context.DocumentHandle(document), deleted = styleName }
            : throw new ToolException("style_in_use", $"The style '{styleName}' is still in the document.", "Check that it is not in use.");
    }

    /// <summary>Style assigned to each target (null: it has none of its own and shows the one of its feature, body or part).</summary>
    public static object Get(SeContext context, string? documentHandle, string[]? targets) =>
        new { items = Resolve(context, documentHandle, targets).Select(t => Report(t, Assigned(t))).ToArray() };

    /// <summary>
    /// Applies a style (by name) or a colour (a style named after it, created if needed) to the targets, and reads it back.
    /// </summary>
    public static object Apply(SeContext context, string? documentHandle, string[]? targets, string? styleName, string? color, double? opacity)
    {
        if ((styleName is null) == (color is null))
        {
            throw ToolException.InvalidArgument("Action 'apply' requires either 'style' (a style name, see action 'list') or 'color' ('#RRGGBB').");
        }

        Target[] resolved = Resolve(context, documentHandle, targets);
        var styles = new Dictionary<SolidEdgeDocument, FaceStyle>(ReferenceEqualityComparer.Instance);
        var done = new List<(Target Target, object? Previous)>();
        try
        {
            foreach (Target target in resolved)
            {
                if (!styles.TryGetValue(target.Owner, out FaceStyle? style))
                {
                    style = styleName is not null
                        ? Find(target.Owner, styleName) ?? throw NotFound(target.Owner, styleName)
                        : ColorStyle(target.Owner, color!, opacity);
                    styles[target.Owner] = style;
                }

                object? previous = Assigned(target);
                Assign(target, style);
                done.Add((target, previous));
                if (!string.Equals(Assigned(target)?.StyleName, style.StyleName, StringComparison.Ordinal))
                {
                    throw new ToolException("appearance_failed", $"Solid Edge did not keep the style '{style.StyleName}' on {target.Handle}.",
                        "Check the target with action 'get'; for a component, the style must exist in the assembly (action 'list').");
                }
            }
        }
        catch
        {
            // The targets already changed get their previous style back.
            foreach ((Target target, object? previous) in Enumerable.Reverse(done))
            {
                TryRestore(target, previous as FaceStyle);
            }

            throw;
        }

        return new { items = resolved.Select(t => Report(t, Assigned(t))).ToArray() };
    }

    /// <summary>Removes the style of the targets, so that they show again the one of their feature, body or part.</summary>
    public static object Reset(SeContext context, string? documentHandle, string[]? targets)
    {
        Target[] resolved = Resolve(context, documentHandle, targets);
        foreach (Target target in resolved)
        {
            Assign(target, null);
            if (Assigned(target) is { } left)
            {
                throw target.Kind == "feature"
                    ? new ToolException("not_supported", $"Solid Edge does not remove the style '{left.StyleName}' of a feature through the API.",
                        "Apply another style to the feature (action 'apply'), e.g. the one of the body.")
                    : new ToolException("appearance_failed", $"{target.Handle} still has the style '{left.StyleName}'.", "Check the target with action 'get'.");
            }
        }

        return new { items = resolved.Select(t => Report(t, null)).ToArray() };
    }

    private static Target[] Resolve(SeContext context, string? documentHandle, string[]? targets)
    {
        if (targets is not { Length: > 0 })
        {
            // Without targets: the body of the part (the colour of the whole part).
            SolidEdgeDocument document = context.GetDocument(documentHandle);
            return [Whole(context.DocumentHandle(document), document)];
        }

        return targets.Select(handle =>
        {
            object value = context.Handles.Resolve(handle, "targets");
            return value switch
            {
                SolidEdgeDocument document => Whole(handle, document),
                Body body => new Target(handle, "body", body, context.DocumentOf(handle)),
                Face face => PartTarget(context, handle, "face", face),
                Occurrence occurrence => new Target(handle, "occurrence", occurrence, context.DocumentOf(handle)),
                _ when handle.StartsWith("feature:", StringComparison.Ordinal) => PartTarget(context, handle, "feature", value),
                _ => throw ToolException.InvalidArgument($"'{handle}' cannot be coloured.",
                    "Pass a part or sheet metal document, a body, faces (se_geometry faces), features (se_feature_tree) or assembly occurrences (se_assembly list)."),
            };
        }).ToArray();
    }

    private static Target Whole(string handle, SolidEdgeDocument document)
    {
        if (ModelDocument.TryGet(document) is not { } model)
        {
            throw new ToolException("wrong_document_type", $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}; only parts and sheet metal have a body to colour.",
                "In an assembly, pass the occurrences to colour as 'targets' (se_assembly list).");
        }

        return model.TryModel()?.Body is Body body
            ? new Target(handle, "body", body, document)
            : throw new ToolException("no_model", $"'{SeContext.SafeName(document)}' has no solid yet.", "Create a feature first (se_extrude...).");
    }

    /// <summary>Faces and features belong to a part or sheet metal document (faces of a component come from se_geometry with a part).</summary>
    private static Target PartTarget(SeContext context, string handle, string kind, object value)
    {
        SolidEdgeDocument owner = context.DocumentOf(handle);
        return ModelDocument.TryGet(owner) is not null
            ? new Target(handle, kind, value, owner)
            : throw new ToolException("wrong_document_type", $"'{handle}' does not belong to a part or sheet metal document.",
                "Colour a whole component with its occurrence handle, or open its part to colour faces or features.");
    }

    private static FaceStyle? Assigned(Target target)
    {
        try
        {
            return target.Value switch
            {
                Body body => body.Style,
                Face face => face.Style,
                // Style (the name) fails with E_INVALIDARG after PutStyleUsePartStyle: there is no style of its own then.
                Occurrence occurrence => occurrence.FaceStyle as FaceStyle,
                _ => Late.Call(target.Value, "GetStyle") as FaceStyle,
            };
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    private static void Assign(Target target, FaceStyle? style)
    {
        switch (target.Value)
        {
            case Body body:
                body.Style = style;
                break;
            case Face face:
                face.Style = style;
                break;
            case Occurrence occurrence when style is null:
                occurrence.PutStyleNone();
                break;
            case Occurrence occurrence:
                occurrence.Style = style.StyleName;
                break;
            default:
                // A null style is passed as an empty IDispatch (a plain null is a type mismatch for late binding).
                Late.Call(target.Value, "SetStyle", style is null ? new DispatchWrapper(null) : style);
                break;
        }
    }

    private static void TryRestore(Target target, FaceStyle? previous)
    {
        try
        {
            if (previous is not null || target.Kind != "feature")
            {
                Assign(target, previous);
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Best effort: the error being reported is the original one.
        }
    }

    private static object Report(Target target, FaceStyle? style) => new
    {
        target = target.Handle,
        kind = target.Kind,
        hasOwnStyle = style is not null,
        style = style is null ? null : Describe(style),
    };

    /// <summary>Style named after the colour ('#RRGGBB', plus the opacity in % if below 1), created if the document lacks it.</summary>
    private static FaceStyle ColorStyle(SolidEdgeDocument document, string color, double? opacity)
    {
        (float r, float g, float b) = ParseColor(color);
        float alpha = Opacity(opacity);
        string name = Hex(r, g, b) + (alpha < 1 ? $" {Math.Round(alpha * 100).ToString(CultureInfo.InvariantCulture)}%" : "");
        return Find(document, name) ?? NewStyle(document, name, r, g, b, alpha);
    }

    private static FaceStyle NewStyle(SolidEdgeDocument document, string name, float r, float g, float b, float alpha)
    {
        FaceStyle style = FacesStyles(document).Add(name, "");
        style.SetDiffuse(r, g, b);
        style.Opacity = alpha;
        return style;
    }

    private static FaceStyles FacesStyles(SolidEdgeDocument document) =>
        Late.TryGet(document, "FaceStyles") as FaceStyles
        ?? throw new ToolException("wrong_document_type", $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}, which has no face styles.",
            "Use a part, sheet metal, weldment or assembly document.");

    private static IEnumerable<FaceStyle> Styles(SolidEdgeDocument document) => Late.Items(FacesStyles(document)).OfType<FaceStyle>();

    /// <summary>Style by its name, or by the only one that differs just in case (style names vary between templates and languages).</summary>
    private static FaceStyle? Find(SolidEdgeDocument document, string name)
    {
        FaceStyle[] styles = [.. Styles(document)];
        string? found = NameMatch.Resolve(styles.Select(s => s.StyleName), name);
        return found is null ? null : styles.First(s => s.StyleName == found);
    }

    private static ToolException NotFound(SolidEdgeDocument document, string name) =>
        new("style_not_found", $"'{SeContext.SafeName(document)}' has no style named '{name}'.{NameMatch.Hint(Styles(document).Select(s => s.StyleName), name)}",
            "List the styles with action 'list', or apply a colour with 'color' ('#RRGGBB').");

    private static object Describe(FaceStyle style)
    {
        style.GetDiffuse(out float r, out float g, out float b);
        return new { name = style.StyleName, color = Hex(r, g, b), opacity = Math.Round(style.Opacity, 3) };
    }

    private static string Hex(float r, float g, float b) =>
        $"#{Channel(r):X2}{Channel(g):X2}{Channel(b):X2}";

    private static int Channel(float value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private static (float R, float G, float B) ParseColor(string color)
    {
        if (color.Length != 7 || color[0] != '#'
            || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            throw ToolException.InvalidArgument($"'{color}' is not a #RRGGBB colour.", "Example: '#FF8000' (orange).");
        }

        return (((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
    }

    private static float Opacity(double? opacity) => opacity switch
    {
        null => 1f,
        >= 0 and <= 1 => (float)opacity.Value,
        _ => throw ToolException.InvalidArgument("'opacity' must be between 0 (transparent) and 1 (opaque)."),
    };
}
