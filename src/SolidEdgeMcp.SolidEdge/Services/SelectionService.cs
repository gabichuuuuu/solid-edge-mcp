using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Selección activa de Solid Edge: leer lo que la persona seleccionó y seleccionar objetos por handle.</summary>
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

    /// <summary>Selecciona los objetos indicados (sustituyendo la selección o añadiéndolos).</summary>
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
            // Las caras y aristas de una ocurrencia se seleccionan por su referencia en el conjunto.
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
