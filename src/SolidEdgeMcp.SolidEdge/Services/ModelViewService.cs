// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// PMI model views of a part, sheet metal or assembly (PMI > Model Views): a named orientation with a render mode and the
/// section views, PMI dimensions and annotations it shows; drawings place them with se_drawing base_view modelView, and 3D PDF
/// publishing (se_publish modelViews) takes them all: the IncludeIn3dPdfPublish flag did not change what it takes. Verified: AddByStandardViewOrientation(front) is named "Front"; a section view
/// added with SectionView.AddToModelView counts in SectionCount and RemoveFromModelView takes it out.
/// </summary>
public static class ModelViewService
{
    private static readonly string[] Orientations = ["front", "back", "left", "right", "top", "bottom", "dimetric", "isometric", "trimetric"];
    private static readonly string[] RenderModes = ["none", "visible_edges", "visible_and_hidden_edges", "shaded", "shaded_with_visible_edges"];

    public static object List(SeContext context, string? documentHandle)
    {
        (SolidEdgeDocument document, PMIModelViews views) = Open(context, documentHandle);
        object[] items = Late.Items(views).OfType<PMIModelView>().Select(v => Describe(context, document, v)).ToArray();
        return new { document = context.DocumentHandle(document), count = items.Length, views = items };
    }

    /// <summary>A model view in a standard orientation (replacing one of the same orientation if asked), or of the current view.</summary>
    public static object Add(SeContext context, string? documentHandle, string? orientation, string? name, string? render, bool replace)
    {
        (SolidEdgeDocument document, PMIModelViews views) = Open(context, documentHandle);
        PMIRenderModeConstants mode = (PMIRenderModeConstants)Index(RenderModes, render ?? "shaded_with_visible_edges", "render");
        int before = views.Count;
        PMIModelView view;
        try
        {
            view = orientation is null
                ? views.Add(Name(name, "add without 'orientation' needs 'name': the view takes the current orientation of the window."), mode)
                : views.AddByStandardViewOrientation((PMIModelViewStandardOrientationConstants)Index(Orientations, orientation, "orientation"), mode, replace ? 1 : 0);
        }
        catch (COMException ex)
        {
            throw new ToolException("model_view_failed", $"Solid Edge did not add the model view: {ex.Message.Trim()}",
                "A view of that orientation may exist already: pass replace = true, or another orientation.", ex);
        }

        if (orientation is not null && !string.IsNullOrWhiteSpace(name))
        {
            view.Name = name.Trim();
        }

        return views.Count >= before && (views.Count > before || replace)
            ? Describe(context, document, view)
            : throw new ToolException("model_view_failed", "Solid Edge did not add the model view: the document has one of that orientation already.", "Pass replace = true, or another orientation.");
    }

    /// <summary>Renames a view, changes its render mode or its 3D PDF flag.</summary>
    public static object Set(SeContext context, string? documentHandle, string? viewName, string? name, string? render, bool? pdf)
    {
        (SolidEdgeDocument document, PMIModelView view) = Find(context, documentHandle, viewName);
        if (name is null && render is null && pdf is null)
        {
            throw ToolException.InvalidArgument("set needs 'name', 'render' or 'pdf'.");
        }

        if (render is not null)
        {
            view.RenderMode = (PMIRenderModeConstants)Index(RenderModes, render, "render");
        }

        if (pdf is { } include)
        {
            view.IncludeIn3dPdfPublish = include;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            view.Name = name.Trim();
        }

        view.Update();
        object result = Describe(context, document, view);
        bool ok = (render is null || RenderName(view) == render) && (pdf is null || view.IncludeIn3dPdfPublish == pdf)
            && (string.IsNullOrWhiteSpace(name) || view.Name == name.Trim());
        return ok ? result : throw new ToolException("model_view_failed", $"Solid Edge did not change the model view '{view.Name}' as asked.", "Read it with action 'list'.");
    }

    /// <summary>
    /// Adds section views (se_section_view) or PMI dimensions and annotations (se_pmi) to a model view, or takes them out; the
    /// counts of the view are read back.
    /// </summary>
    public static object Items(SeContext context, string? documentHandle, string? viewName, string[]? items, bool include)
    {
        (SolidEdgeDocument document, PMIModelView view) = Find(context, documentHandle, viewName);
        if (items is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"{(include ? "include" : "exclude")} needs 'items': section view handles (se_section_view) or PMI dimension and annotation handles (se_pmi).");
        }

        object[] resolved = items.Select(i => context.Handles.Resolve(i, "items")).ToArray();
        int before = Count(view);
        foreach (object item in resolved)
        {
            try
            {
                if (item is SectionView section)
                {
                    if (include)
                    {
                        section.AddToModelView(view);
                    }
                    else
                    {
                        section.RemoveFromModelView(view);
                    }
                }
                else if (include)
                {
                    view.AddDimAnnotOrSectionToView(item);
                }
                else
                {
                    view.RemoveDimAnnotOrSectionFromView(item);
                }
            }
            catch (COMException ex)
            {
                throw new ToolException("model_view_failed", $"Solid Edge did not {(include ? "add" : "remove")} an item: {ex.Message.Trim()}",
                    "Pass section views of this document (se_section_view list) or its PMI dimensions and annotations (se_pmi list).", ex);
            }
        }

        view.Update();
        int after = Count(view);
        return (include ? after > before : after < before)
            ? Describe(context, document, view)
            : throw new ToolException("model_view_failed", $"The model view '{view.Name}' still shows {after} items (before: {before}).",
                include ? "The items may be in it already: check with action 'list'." : "The items may not be in it: check with action 'list'.");
    }

    /// <summary>Shows a model view in the window (its orientation, render mode, sections and PMI).</summary>
    public static object Apply(SeContext context, string? documentHandle, string? viewName)
    {
        (SolidEdgeDocument document, PMIModelView view) = Find(context, documentHandle, viewName);
        view.Apply();
        return new { applied = view.Name, view = Describe(context, document, view), note = "See it with se_view capture." };
    }

    public static object Delete(SeContext context, string? documentHandle, string? viewName)
    {
        (SolidEdgeDocument document, PMIModelView view) = Find(context, documentHandle, viewName);
        (_, PMIModelViews views) = Open(context, context.DocumentHandle(document));
        int before = views.Count;
        string name = view.Name;
        view.Delete();
        return views.Count < before
            ? new { deleted = name, count = views.Count }
            : throw new ToolException("model_view_failed", $"Solid Edge did not delete the model view '{name}'.");
    }

    private static object Describe(SeContext context, SolidEdgeDocument document, PMIModelView view) => new
    {
        handle = context.Register(view, "model_view", document),
        name = view.Name,
        render = RenderName(view),
        pdf = view.IncludeIn3dPdfPublish,
        sections = Enumerable.Range(1, view.SectionCount).Select(i => Late.TryGetString(view.SectionItem[i], "Caption") ?? Late.TryGetString(view.SectionItem[i], "Name")).ToArray(),
        dimensions = view.DimensionCount,
        annotations = view.AnnotationCount,
    };

    private static int Count(PMIModelView view) => view.SectionCount + view.DimensionCount + view.AnnotationCount;

    private static string RenderName(PMIModelView view)
    {
        int code = (int)view.RenderMode;
        return code >= 0 && code < RenderModes.Length ? RenderModes[code] : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Index(string[] names, string value, string argument)
    {
        int index = Array.IndexOf(names, value.Trim().ToLowerInvariant());
        return index >= 0 ? index : throw ToolException.InvalidArgument($"Unknown {argument} '{value}'.", $"Use one of: {string.Join(", ", names)}.");
    }

    private static string Name(string? name, string missing) => string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument(missing) : name.Trim();

    private static (SolidEdgeDocument Document, PMIModelViews Views) Open(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object pmi = document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument or SolidEdgeAssembly.AssemblyDocument
            ? Late.Get(document, "PMI")!
            : throw new ToolException("not_supported", $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}: model views belong to parts, sheet metal and assemblies.");
        return (document, (PMIModelViews)Late.Get(pmi, "PMIModelViews")!);
    }

    private static (SolidEdgeDocument Document, PMIModelView View) Find(SeContext context, string? documentHandle, string? view)
    {
        string wanted = Name(view, "'view' is required: a model view handle or name (action 'list').");
        if (wanted.Contains(':', StringComparison.Ordinal))
        {
            return context.Handles.Resolve(wanted, "view") is PMIModelView byHandle
                ? (context.DocumentOf(wanted), byHandle)
                : throw new ToolException("handle_wrong_type", $"'{wanted}' is not a model view.", "Pass a handle or name from action 'list'.");
        }

        (SolidEdgeDocument document, PMIModelViews views) = Open(context, documentHandle);
        PMIModelView? found = Late.Items(views).OfType<PMIModelView>().FirstOrDefault(v => string.Equals(v.Name, wanted, StringComparison.OrdinalIgnoreCase));
        return found is null
            ? throw ToolException.NotFound($"The document has no model view '{wanted}'.", "List them with action 'list' or add one with 'add'.")
            : (document, found);
    }
}
