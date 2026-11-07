// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeFramework;
using SolidEdgePart;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Save Simplified Model As: the simplified body of a part written as a Parasolid file.</summary>
public static partial class SimplifyService
{
    /// <summary>
    /// Verified in Solid Edge 2026: SimplifiedModel.SaveBody(file) writes Parasolid text (a .par name gets .x_t added);
    /// the document type 2 writes .x_t and 3 binary .x_b, while 0 and 1 open a new part or sheet metal document and then fail
    /// (E_NOINTERFACE), so only Parasolid files are written.
    /// </summary>
    private static object SavePart(SeContext context, ModelDocument model, string? path)
    {
        SimplifiedModel simplified = Find(model)
            ?? throw new ToolException("no_simplified_model", "The part has no simplified model.", "Make it first with action 'create'.");
        string file = ParasolidPath(context, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        simplified.SaveBody(file, ParasolidType(file));
        return File.Exists(file) && new FileInfo(file).Length > 0
            ? new { document = context.DocumentHandle(model.Document), path = file, bytes = new FileInfo(file).Length }
            : throw new ToolException("save_failed", $"Solid Edge did not write '{file}'.");
    }

    /// <summary>The checked path of a Parasolid file to write; an existing file is replaced.</summary>
    internal static string ParasolidPath(SeContext context, string? path)
    {
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("'path' is required: the Parasolid file to write (.x_t or .x_b)."), "path");
        _ = ParasolidType(file);
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        return file;
    }

    /// <summary>The document type SaveBody takes for a Parasolid file: 2 text (.x_t), 3 binary (.x_b).</summary>
    internal static int ParasolidType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".x_t" => 2,
        ".x_b" => 3,
        _ => throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' must be a Parasolid file (.x_t or .x_b).",
            "Bodies are written as Parasolid; a whole part is exported with se_export."),
    };
}
