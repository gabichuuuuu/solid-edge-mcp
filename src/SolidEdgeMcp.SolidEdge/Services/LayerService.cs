// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Layers of a part, sheet metal or assembly document, or of a drawing. They hold 2D elements: the geometry of sketches
/// in models; drawing views, dimensions, annotations and sheet geometry in drawings. New elements go to the active layer.
/// </summary>
/// <remarks>
/// Verified in drawings: the layer names are shared by all the sheets (Sheet.Layers returns the same collection) and
/// IsEmpty counts every sheet, while the active layer, Show and Locatable are kept per sheet; Show and Locatable are read
/// and written on the active sheet whatever sheet the collection came from. The requested sheet is therefore activated
/// for the operation and the previous one restored afterwards.
/// </remarks>
public static class LayerService
{
    public static object List(SeContext context, string? documentHandle, string? sheet)
    {
        using LayerScope scope = Open(context, documentHandle, sheet);
        string active = scope.Layers.ActiveLayer.Name;
        var rows = Late.Items(scope.Layers).Cast<Layer>().Select(l => Describe(l, active)).ToList();
        return new { owner = scope.Owner, count = rows.Count, active, layers = rows };
    }

    public static object Create(SeContext context, string? documentHandle, string? sheet, string? name, bool activate)
    {
        string layerName = RequireName(name);
        using LayerScope scope = Open(context, documentHandle, sheet);
        if (Find(scope.Layers, layerName) is not null)
        {
            // Verified: Add with an existing name gives E_INVALIDARG.
            throw ToolException.InvalidArgument($"There is already a layer '{layerName}' in {scope.Owner}.", "Choose another name.");
        }

        Layer layer = scope.Layers.Add(layerName);
        if (activate)
        {
            layer.Activate();
        }

        return new { owner = scope.Owner, created = Describe(layer, scope.Layers.ActiveLayer.Name) };
    }

    /// <summary>Makes a layer the active one (and shows it): the elements created next go to it.</summary>
    public static object Activate(SeContext context, string? documentHandle, string? sheet, string? name)
    {
        using LayerScope scope = Open(context, documentHandle, sheet);
        Layer layer = Resolve(scope, name);

        // Verified: Solid Edge activates a hidden layer and keeps Show = false, while it never hides the active one.
        if (!layer.Show)
        {
            layer.Show = true;
        }

        layer.Activate();
        if (!string.Equals(scope.Layers.ActiveLayer.Name, layer.Name, StringComparison.Ordinal) || !layer.Show)
        {
            throw new ToolException("layer_failed", $"Solid Edge did not make '{layer.Name}' the active layer.");
        }

        return new { owner = scope.Owner, active = Describe(layer, layer.Name) };
    }

    /// <summary>Shows or hides a layer, or makes its elements selectable or not; the result is read back.</summary>
    public static object Set(SeContext context, string? documentHandle, string? sheet, string? name, bool? shown, bool? locatable)
    {
        if (shown is null && locatable is null)
        {
            throw ToolException.InvalidArgument("'shown' or 'locatable' is required.");
        }

        using LayerScope scope = Open(context, documentHandle, sheet);
        Layer layer = Resolve(scope, name);
        string active = scope.Layers.ActiveLayer.Name;
        if (shown == false && layer.Name == active)
        {
            // Verified: Solid Edge ignores Show = false on the active layer.
            throw new ToolException("not_supported", $"'{layer.Name}' is the active layer and Solid Edge keeps it shown.", "Activate another layer first (action activate).");
        }

        if (shown is { } show)
        {
            layer.Show = show;
            if (layer.Show != show)
            {
                throw new ToolException("layer_failed", $"Solid Edge did not {(show ? "show" : "hide")} the layer '{layer.Name}'.");
            }
        }

        if (locatable is { } value)
        {
            layer.Locatable = value;
            if (layer.Locatable != value)
            {
                throw new ToolException("layer_failed", $"Solid Edge did not change whether the layer '{layer.Name}' can be selected.");
            }
        }

        return new { owner = scope.Owner, layer = Describe(layer, active) };
    }

    /// <summary>Deletes an empty layer that is not the active one; Solid Edge refuses a layer with elements (E_FAIL).</summary>
    public static object Delete(SeContext context, string? documentHandle, string? sheet, string? name)
    {
        using LayerScope scope = Open(context, documentHandle, sheet);
        Layer layer = Resolve(scope, name);
        string layerName = layer.Name;
        if (layerName == scope.Layers.ActiveLayer.Name)
        {
            throw new ToolException("not_supported", $"'{layerName}' is the active layer.", "Activate another layer first (action activate).");
        }

        if (!layer.IsEmpty)
        {
            throw new ToolException("not_supported", $"The layer '{layerName}' has elements and is not deleted.",
                "Move them to another layer first (action assign); the elements are never deleted with the layer.");
        }

        layer.Delete();
        if (Find(scope.Layers, layerName) is not null)
        {
            throw new ToolException("layer_failed", $"Solid Edge did not delete the layer '{layerName}'.");
        }

        return new { owner = scope.Owner, deleted = layerName };
    }

    /// <summary>Moves elements (sketch geometry, drawing views, dimensions, annotations) to a layer of their document.</summary>
    public static object Assign(SeContext context, string? documentHandle, string? sheet, string[]? objects, string? name)
    {
        if (objects is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'objects' is required: handles of 2D elements, drawing views, dimensions or annotations.");
        }

        string? document = documentHandle;
        if (string.IsNullOrWhiteSpace(document) && context.Handles.GetScope(objects[0]) is not null)
        {
            document = context.DocumentHandle(context.DocumentOf(objects[0]));
        }

        using LayerScope scope = Open(context, document, sheet);
        string layerName = Resolve(scope, name).Name;
        var moved = new List<object>();
        foreach (string handle in objects)
        {
            object item = context.Handles.Resolve(handle, "objects");
            if (Late.TryGetString(item, "Layer") is not { } before)
            {
                throw ToolException.InvalidArgument($"'{handle}' has no layer.", "Layers hold 2D elements, drawing views, dimensions and annotations.");
            }

            Late.Set(item, "Layer", layerName);
            if (Late.TryGetString(item, "Layer") != layerName)
            {
                throw new ToolException("layer_failed", $"Solid Edge did not move '{handle}' to the layer '{layerName}'.");
            }

            moved.Add(new { handle, from = before });
        }

        return new { owner = scope.Owner, layer = layerName, moved };
    }

    /// <summary>Layers of a model, or of a drawing with the requested sheet active until the scope is disposed.</summary>
    private static LayerScope Open(SeContext context, string? documentHandle, string? sheet)
    {
        SolidEdgeDocument document = string.IsNullOrWhiteSpace(documentHandle) && !string.IsNullOrWhiteSpace(sheet) && context.Handles.GetScope(sheet) is not null
            ? context.DocumentOf(sheet)
            : context.GetDocument(documentHandle);
        if (document is DraftDocument draft)
        {
            Sheet target = DrawingService.SheetFor(context, context.DocumentHandle(document), sheet);
            Sheet active = draft.ActiveSheet;
            Sheet? restore = null;
            if (!string.Equals(target.Name, active.Name, StringComparison.Ordinal))
            {
                target.Activate();
                restore = active;
            }

            return new LayerScope(target.Layers, $"sheet '{target.Name}' of '{document.Name}'", restore);
        }

        if (!string.IsNullOrWhiteSpace(sheet))
        {
            throw ToolException.InvalidArgument("'sheet' applies only to drawings.");
        }

        return new LayerScope(Late.TryGet(document, "Layers") as Layers
            ?? throw new ToolException("not_supported", $"'{document.Name}' has no layers."), $"'{document.Name}'", null);
    }

    private static Layer Resolve(LayerScope scope, string? name)
    {
        string layerName = RequireName(name);
        return Find(scope.Layers, layerName) ?? throw new ToolException("not_found", $"There is no layer '{layerName}' in {scope.Owner}.",
            "List them with se_layer action list, or create it (action create).");
    }

    private static Layer? Find(Layers layers, string name) =>
        Late.Items(layers).Cast<Layer>().FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    private static object Describe(Layer layer, string active) => new
    {
        name = layer.Name,
        active = layer.Name == active,
        shown = layer.Show,
        locatable = layer.Locatable,
        empty = layer.IsEmpty,
    };

    private static string RequireName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("'name' (the layer) is required.") : name.Trim();

    private sealed record LayerScope(Layers Layers, string Owner, Sheet? Restore) : IDisposable
    {
        public void Dispose() => Restore?.Activate();
    }
}
