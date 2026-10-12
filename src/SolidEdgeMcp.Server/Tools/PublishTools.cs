using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum PublishAction
{
    Templates,
    Pdf3d,
}

/// <summary>Publishing of saved models (Application menu > Publish).</summary>
[McpServerToolType]
public sealed class PublishTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_publish", Title = "Publish 3D PDF", Destructive = false, OpenWorld = false)]
    [Description("""
        Publishes saved models as 3D PDF documents with the 3D PDF templates of this installation (Application menu > Publish > 3D PDF): pages laid out by the template, with the model in 3D that the reader can turn.
        templates: the installed 3D PDF templates |
        pdf3d (source = a saved .par/.psm/.asm file or a folder of them, subfolders = with its subfolders; template = a name from 'templates' or a .dft path (default: the part or assembly landscape one); folder = output folder (default: where the source is); name = PDF name for a single file (default: the source name); attachStep, attachJt = embed STEP AP242 and JT files; html = also a self-contained .html page with the 3D model; overwrite).
        Solid Edge publishes only while no document is open: save and close the ones you opened (se_documents close) first. The result gives each PDF with its pages, embedded files and whether it holds 3D.
        Example: {"action":"pdf3d","source":"C:\\Work\\bracket.par","attachStep":true}.
        """)]
    public Task<CallToolResult> Publish(
        PublishAction action,
        [Description("pdf3d: absolute path of a saved part, sheet metal or assembly, or of a folder of them.")] string? source = null,
        [Description("pdf3d: 3D PDF template name (action 'templates') or absolute .dft path.")] string? template = null,
        [Description("pdf3d: output folder (absolute, in an allowed folder); default: the folder of the source.")] string? folder = null,
        [Description("pdf3d: file name of the PDF for a single source, without folder (default: the source name).")] string? name = null,
        [Description("pdf3d with a folder: also publish the models of its subfolders (the PDFs all go to 'folder').")] bool subfolders = false,
        [Description("pdf3d: embed a STEP AP242 file of the model in the PDF.")] bool attachStep = false,
        [Description("pdf3d: embed a JT file of the model in the PDF.")] bool attachJt = false,
        [Description("pdf3d: also write a self-contained .html page with the 3D model next to each PDF.")] bool html = false,
        [Description("pdf3d: replace PDF (and .html) files that already exist.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_publish";
        string act = Snake(action);
        return action switch
        {
            PublishAction.Templates => runner.RunAsync(tool, act, PublishService.Templates, cancellationToken),
            PublishAction.Pdf3d => runner.RunAsync(tool, act, c => PublishService.Pdf3d(c, source, template, folder, name, subfolders, attachStep,
                attachJt, html, overwrite), cancellationToken, timeout: TimeSpan.FromMinutes(10)),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
