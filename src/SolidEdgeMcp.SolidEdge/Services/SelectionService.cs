// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Active Solid Edge selection: reads what the person selected and selects objects by handle.</summary>
public static class SelectionService
{
    public static object Get(SeContext context, int offset, int limit)
    {
        SelectSet set = context.Application.ActiveSelectSet;
        SolidEdgeDocument? owner = ActiveDocument(context);
        int count = set.Count;
        int start = Math.Max(0, offset);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object?>();
        for (int i = start + 1; i <= count && items.Count < take; i++)
        {
            object item = set.Item(i);
            items.Add(new
            {
                index = i,
                name = Late.TryGetString(item, "Name"),
                value = ComValues.ToJson(context, item, owner),
            });
        }

        return new { count, offset = start, returned = items.Count, more = start + items.Count < count, items };
    }

    /// <summary>Selects the given objects (replacing the selection or adding to it).</summary>
    public static object Set(SeContext context, string[]? handles, bool append)
    {
        if (handles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'handles' must list at least one object handle (face, edge, feature, occurrence...).");
        }

        object[] objects = handles.Select(h => context.Handles.Resolve(h, "handles")).ToArray();
        SelectSet set = context.Application.ActiveSelectSet;
        if (!append)
        {
            set.RemoveAll();
        }

        foreach (object value in objects)
        {
            // Faces and edges of an occurrence are selected through their reference in the assembly.
            set.Add(value is OccurrenceElement element
                ? element.Reference(context.GetDocument<SolidEdgeAssembly.AssemblyDocument>(null, "assembly"))
                : value);
        }

        set.RefreshDisplay();
        return Get(context, 0, context.MaxListItems);
    }

    public static object Clear(SeContext context)
    {
        SelectSet set = context.Application.ActiveSelectSet;
        set.RemoveAll();
        set.RefreshDisplay();
        return new { count = 0 };
    }

    private static SolidEdgeDocument? ActiveDocument(SeContext context)
    {
        try
        {
            return context.Application.Documents.Count == 0 ? null : context.Application.ActiveDocument as SolidEdgeDocument;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
