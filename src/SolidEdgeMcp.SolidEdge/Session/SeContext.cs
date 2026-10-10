using SolidEdgeMcp.Core;
using SolidEdgeFramework;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>
/// Contexto de una operación en el hilo STA: la aplicación conectada, los handles, la política y la
/// guarda de rutas. Solo es válido dentro de la operación que lo recibe.
/// </summary>
public sealed class SeContext
{
    internal SeContext(Application application, HandleRegistry handles, PathGuard paths, ServerPolicy policy, AppPaths appPaths)
    {
        Application = application;
        Handles = handles;
        Paths = paths;
        Policy = policy;
        AppPaths = appPaths;
    }

    public Application Application { get; }

    public HandleRegistry Handles { get; }

    public PathGuard Paths { get; }

    public ServerPolicy Policy { get; }

    public AppPaths AppPaths { get; }

    public int MaxListItems => Policy.Limits.MaxListItems;

    /// <summary>Documento del handle, o el activo si no se indica.</summary>
    public SolidEdgeDocument GetDocument(string? handle)
    {
        if (!string.IsNullOrWhiteSpace(handle))
        {
            return Handles.Resolve<SolidEdgeDocument>(handle, "document");
        }

        object? active = null;
        try
        {
            active = Application.ActiveDocument;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Sin documento activo Solid Edge devuelve error en lugar de null.
        }

        return active as SolidEdgeDocument ?? throw new ToolException(
            "no_active_document",
            "There is no active document in Solid Edge.",
            "Open or create a document with se_documents, or pass a document handle.");
    }

    /// <summary>Documento del handle convertido al tipo pedido (pieza, conjunto, plano...).</summary>
    public T GetDocument<T>(string? handle, string expected) where T : class
    {
        SolidEdgeDocument document = GetDocument(handle);
        return document as T ?? throw new ToolException(
            "wrong_document_type",
            $"The document '{SafeName(document)}' is a {DocumentKinds.Describe(document)}; this action needs a {expected}.",
            "Activate or pass a document of the right type (se_documents list).");
    }

    /// <summary>Handle estable de un documento (ámbito de los handles de sus objetos).</summary>
    public string DocumentHandle(SolidEdgeDocument document) => Handles.Register(document, "doc");

    /// <summary>Registra un objeto de un documento: caduca cuando se cierra el documento.</summary>
    public string Register(object value, string kind, SolidEdgeDocument? owner) =>
        Handles.Register(value, kind, owner is null ? null : DocumentHandle(owner));

    /// <summary>Registra un objeto en el mismo ámbito (documento) que otro handle ya registrado.</summary>
    public string RegisterBeside(object value, string kind, string relatedHandle) =>
        Handles.Register(value, kind, Handles.GetScope(relatedHandle));

    /// <summary>Documento al que pertenece un handle; sin ámbito, el documento activo.</summary>
    public SolidEdgeDocument DocumentOf(string handle) =>
        Handles.GetScope(handle) is { } scope ? Handles.Resolve<SolidEdgeDocument>(scope, "document") : GetDocument(null);

    /// <summary>Handles de topología: dejan de ser fiables en cuanto cambia el modelo.</summary>
    private static readonly string[] TopologyKinds = ["face", "faces", "edge", "edges", "vertex", "vertices", "body", "shell", "shells", "loop", "loops"];

    /// <summary>Caduca caras, aristas y vértices del documento tras una operación que cambia el modelo.</summary>
    public void ModelChanged(SolidEdgeDocument document, string reason) =>
        Handles.InvalidateScope(DocumentHandle(document), reason, TopologyKinds);

    public static string SafeName(SolidEdgeDocument document)
    {
        try
        {
            return document.Name;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return "?";
        }
    }
}
