using SolidEdgeFramework;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>Tipos de documento: nombres estables para el cliente, ProgID y extensión.</summary>
public static class DocumentKinds
{
    public static readonly IReadOnlyList<string> CreatableKinds = ["part", "sheet_metal", "assembly", "draft", "weldment"];

    public static string ToKind(DocumentTypeConstants type) => type switch
    {
        DocumentTypeConstants.igPartDocument or DocumentTypeConstants.igSyncPartDocument => "part",
        DocumentTypeConstants.igSheetMetalDocument or DocumentTypeConstants.igSyncSheetMetalDocument => "sheet_metal",
        DocumentTypeConstants.igAssemblyDocument or DocumentTypeConstants.igSyncAssemblyDocument => "assembly",
        DocumentTypeConstants.igDraftDocument => "draft",
        DocumentTypeConstants.igWeldmentDocument => "weldment",
        DocumentTypeConstants.igWeldmentAssemblyDocument => "weldment_assembly",
        DocumentTypeConstants.igAssemblyViewerDocument or DocumentTypeConstants.igPartViewerDocument
            or DocumentTypeConstants.igDraftViewerDocument => "viewer",
        _ => "unknown",
    };

    public static string Describe(SolidEdgeDocument document)
    {
        try
        {
            return ToKind(document.Type).Replace('_', ' ');
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return "document of unknown type";
        }
    }

    public static (string ProgId, DocumentTypeConstants Type) ForNew(string kind) => kind switch
    {
        "part" => ("SolidEdge.PartDocument", DocumentTypeConstants.igPartDocument),
        "sheet_metal" => ("SolidEdge.SheetMetalDocument", DocumentTypeConstants.igSheetMetalDocument),
        "assembly" => ("SolidEdge.AssemblyDocument", DocumentTypeConstants.igAssemblyDocument),
        "draft" => ("SolidEdge.DraftDocument", DocumentTypeConstants.igDraftDocument),
        "weldment" => ("SolidEdge.WeldmentDocument", DocumentTypeConstants.igWeldmentDocument),
        _ => throw ToolException.InvalidArgument(
            $"Unknown document kind '{kind}'.", $"Use one of: {string.Join(", ", CreatableKinds)}."),
    };
}
