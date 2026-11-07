// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class FeatureService
{
    /// <summary>
    /// Multi-Body Publish: design bodies of a saved part written as part files of their own, next to the part and named after
    /// the bodies ("Design Body_2.par"), optionally gathered in a new assembly. Verified in Solid Edge 2026:
    /// SaveMultiBodyPublishMembers(n, ref models, createAssembly, assemblyFile, out status) with the Model objects of the bodies
    /// writes one part per body (and the assembly) with status up to date; GetMultiBodyPublishMembers fails (E_POINTER typed,
    /// DISP_E_TYPEMISMATCH late), so the bodies are given by the caller.
    /// </summary>
    private static object PublishBodies(SeContext context, string? documentHandle, string[]? bodies, string? assembly, bool overwrite)
    {
        ModelDocument model = bodies is [string first, ..] ? ModelDocument.For(context, documentHandle, first) : ModelDocument.Get(context, documentHandle);
        if (model.Document is not PartDocument part)
        {
            throw new ToolException("wrong_document_type", "Multi-body publish writes the design bodies of a part.");
        }

        string source = Late.TryGetString(model.Document, "FullName") is { Length: > 0 } fullName && Path.IsPathRooted(fullName)
            ? context.Paths.Check(fullName, "document path")
            : throw new ToolException("never_saved", $"'{model.Document.Name}' has never been saved.",
                "Save it in an allowed folder first (se_documents save_as): the body files are written next to it.");
        if (model.Document.Dirty)
        {
            throw new ToolException("unsaved_changes", $"'{model.Document.Name}' has unsaved changes.", "Save it first (se_documents save): the body files are copies of the saved part.");
        }

        IReadOnlyList<(Model Model, SolidEdgeGeometry.Body Body)> all = RequireBodies(model, 1);
        Model[] chosen = bodies is { Length: > 0 }
            ? [.. Owned(context, all, bodies).Bodies.Select(b => all.First(a => Late.SameObject(a.Body, b)).Model)]
            : [.. all.Select(a => a.Model)];
        string folder = Path.GetDirectoryName(source)!;
        string[] files = [.. chosen.Select(m => context.Paths.Check(Path.Combine(folder,
            (Late.TryGetString(m, "BodyName") is { Length: > 0 } name ? name : throw new ToolException("publish_failed", "A body has no name to write it under.")) + ".par"), "body file"))];
        string? target = assembly is null ? null : context.Paths.Check(assembly, "assembly");
        if (target is not null && !string.Equals(Path.GetExtension(target), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(target)}' must be an assembly file (.asm).");
        }

        string[] written = target is null ? files : [.. files, target];
        foreach (string file in written.Where(File.Exists))
        {
            if (!overwrite)
            {
                throw new ToolException("file_exists", $"'{file}' already exists.", "Pass overwrite=true to replace it.");
            }

            if (DocumentService.FindOpen(context, file) is not null)
            {
                throw new ToolException("file_in_use", $"'{file}' is open in Solid Edge.", "Close it first.");
            }

            File.Delete(file);
        }

        Array members = chosen;
        part.SaveMultiBodyPublishMembers(members.Length, ref members, target is not null, target ?? "", out MultiBodyPublishStatusConstants status);
        string[] missing = [.. written.Where(f => !File.Exists(f))];
        if (missing.Length > 0)
        {
            throw new ToolException("publish_failed", $"Solid Edge did not write '{string.Join("', '", missing)}' (status {status}).",
                "Check that the bodies are design bodies of the saved part (se_body list).");
        }

        return new
        {
            document = context.DocumentHandle(model.Document),
            files,
            assembly = target,
            status = status.ToString().Replace("seMBPStatus", "", StringComparison.Ordinal),
            note = "Each file is a part of its own; open it with se_documents to measure it. Solid Edge renews the bodies of the part: take their handles again from se_body list.",
        };
    }
}
