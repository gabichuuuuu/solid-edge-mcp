// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeMcp.Core;
using SolidEdgeFramework;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>
/// Context of an operation on the STA thread: the connected application, the handles, the policy and the
/// path guard. It is only valid within the operation that receives it.
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

    /// <summary>Document of the handle, or the active one if none is given.</summary>
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
            // With no active document, Solid Edge returns an error instead of null.
        }

        return active as SolidEdgeDocument ?? throw new ToolException(
            "no_active_document",
            "There is no active document in Solid Edge.",
            "Open or create a document with se_documents, or pass a document handle.");
    }

    /// <summary>Document of the handle converted to the requested type (part, assembly, drawing...).</summary>
    public T GetDocument<T>(string? handle, string expected) where T : class
    {
        SolidEdgeDocument document = GetDocument(handle);
        return document as T ?? throw new ToolException(
            "wrong_document_type",
            $"The document '{SafeName(document)}' is a {DocumentKinds.Describe(document)}; this action needs a {expected}.",
            "Activate or pass a document of the right type (se_documents list).");
    }

    /// <summary>Stable handle of a document (scope of the handles of its objects).</summary>
    public string DocumentHandle(SolidEdgeDocument document) => Handles.Register(document, "doc");

    /// <summary>Registers an object of a document: it expires when the document is closed.</summary>
    public string Register(object value, string kind, SolidEdgeDocument? owner) =>
        Handles.Register(value, kind, owner is null ? null : DocumentHandle(owner));

    /// <summary>Registers an object in the same scope (document) as another handle that is already registered.</summary>
    public string RegisterBeside(object value, string kind, string relatedHandle) =>
        Handles.Register(value, kind, Handles.GetScope(relatedHandle));

    /// <summary>Document a handle belongs to; without a scope, the active document.</summary>
    public SolidEdgeDocument DocumentOf(string handle) =>
        Handles.GetScope(handle) is { } scope ? Handles.Resolve<SolidEdgeDocument>(scope, "document") : GetDocument(null);

    /// <summary>Topology handles: they stop being reliable as soon as the model changes.</summary>
    private static readonly string[] TopologyKinds = ["face", "faces", "edge", "edges", "vertex", "vertices", "body", "shell", "shells", "loop", "loops"];

    /// <summary>Expires the faces, edges and vertices of the document after an operation that changes the model.</summary>
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
