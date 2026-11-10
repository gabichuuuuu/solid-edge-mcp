// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Components made from the assembly: a new empty part placed in it, adjustable parts, an occurrence replaced by a copy of
/// its file or by a new part, and an occurrence saved as a part file of its own.
/// </summary>
public static partial class AssemblyService
{
    /// <summary>
    /// Places a new, empty part, sheet metal part or subassembly made from a template (the start of Create Part In-Place).
    /// Verified in Solid Edge 2026: Occurrences.AddByTemplate(file, template) writes the file and adds it at the origin,
    /// without starting in-place editing; the part is modelled by opening its file.
    /// </summary>
    public static object InsertNew(SeContext context, string? documentHandle, string path, string? template, double[]? origin, double[]? angles, bool ground)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string file = NewFile(context, path);
        string templatePath = DocumentService.TemplateFor(context, ComponentType(file), template);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Occurrence occurrence;
        try
        {
            occurrence = assembly.Occurrences.AddByTemplate(file, templatePath);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("insert_failed", $"Solid Edge did not create '{Path.GetFileName(file)}': {ex.Message.Trim()}", "Check the template (se_documents templates).", ex);
        }

        if (!File.Exists(file))
        {
            throw new ToolException("insert_failed", $"Solid Edge did not write '{file}'.");
        }

        return Placed(context, assembly, occurrence, origin, angles, ground);
    }

    /// <summary>
    /// Places a part as an adjustable part, whose adjustable variables (se_variables adjustable) each occurrence may change.
    /// Verified in Solid Edge 2026: Occurrences.AddAsAdjustablePart(file) gives an occurrence with IsAdjustablePart true.
    /// </summary>
    public static object InsertAdjustable(SeContext context, string? documentHandle, string path, double[]? origin, double[]? angles, bool ground)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string file = ExistingFile(context, path);
        Occurrence occurrence;
        try
        {
            occurrence = assembly.Occurrences.AddAsAdjustablePart(file);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            // Verified: a part without adjustable variables gives E_INVALIDARG.
            throw new ToolException("not_adjustable", $"'{Path.GetFileName(file)}' is not an adjustable part; it was not inserted.",
                "Choose its adjustable variables first (se_variables adjustable, in the part) and save it.", ex);
        }

        if (!occurrence.IsAdjustablePart)
        {
            occurrence.Delete();
            throw new ToolException("not_adjustable", $"'{Path.GetFileName(file)}' is not an adjustable part; it was not inserted.",
                "Choose its adjustable variables first (se_variables adjustable, in the part) and save it.");
        }

        return Placed(context, assembly, occurrence, origin, angles, ground);
    }

    /// <summary>
    /// Makes an occurrence of an adjustable part adjustable in this assembly. Verified in Solid Edge 2026:
    /// Occurrence.MakeAdjustablePart() on an occurrence of a part with adjustable variables sets IsAdjustablePart. The values
    /// of each occurrence are set in Solid Edge: AdjustablePart.GetAdjustables and SetAdjustables fail with E_POINTER.
    /// </summary>
    public static object MakeAdjustable(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        if (!occurrence.IsAdjustablePart)
        {
            try
            {
                occurrence.MakeAdjustablePart();
            }
            catch (Exception ex) when (ComErrors.IsCallFailure(ex))
            {
                // Verified: an occurrence of a part without adjustable variables gives E_POINTER.
                throw new ToolException("not_adjustable", $"Solid Edge did not make '{occurrence.Name}' adjustable: {ex.Message.Trim()}",
                    "Its part needs adjustable variables (se_variables adjustable, in the part).", ex);
            }

            if (!occurrence.IsAdjustablePart)
            {
                throw new ToolException("not_adjustable", $"'{occurrence.Name}' did not become adjustable.",
                    "Its part needs adjustable variables (se_variables adjustable, in the part, then save it).");
            }

            context.ModelChanged((SolidEdgeDocument)assembly, "an occurrence was made adjustable");
        }

        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    /// <summary>
    /// Replace Part with Copy / with New Part: <paramref name="path"/> gets a copy of the occurrence's file, or a new empty
    /// part from the template, and the occurrence (or every occurrence of its file) is replaced by it. Verified in Solid Edge
    /// 2026: Occurrence.Replace(file, false) takes the copy, and an empty new part too (its range is then empty).
    /// </summary>
    public static object ReplaceWith(SeContext context, string? handle, string path, ReplaceSource source, bool all, string? template)
    {
        if (source == ReplaceSource.File)
        {
            return Replace(context, handle, path, all);
        }

        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        string file = NewFile(context, path);
        string current = occurrence.OccurrenceFileName;
        switch (source)
        {
            case ReplaceSource.Copy:
                if (!string.Equals(Path.GetExtension(file), Path.GetExtension(current), StringComparison.OrdinalIgnoreCase))
                {
                    throw ToolException.InvalidArgument($"The copy must keep the extension of '{Path.GetFileName(current)}'.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.Copy(current, file);
                break;
            case ReplaceSource.New:
                DocumentTypeConstants type = ComponentType(file);
                string templatePath = DocumentService.TemplateFor(context, type, template);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var created = (SolidEdgeDocument)context.Application.Documents.Add(DocumentKinds.ForNew(DocumentKinds.ToKind(type)).ProgId, templatePath);
                try
                {
                    created.SaveAs(file);
                }
                finally
                {
                    created.Close(false);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        occurrence.Replace(file, all);
        if (!string.Equals(Path.GetFullPath(occurrence.OccurrenceFileName), file, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("replace_failed", $"'{occurrence.Name}' still uses '{Path.GetFileName(occurrence.OccurrenceFileName)}'; '{file}' was left on disk.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "an occurrence was replaced");
        return new { occurrence = Describe(context, assembly, occurrence, GroundedIds(assembly)), created = file, from = current };
    }

    /// <summary>
    /// Save Model As: a new part file holding a linked copy of an occurrence's part, in its assembly position. Verified in
    /// Solid Edge 2026: AssemblyDocument.SaveModelAs(occurrence, file) on a saved assembly wrote a part with CopiedPart_1, the
    /// 100 x 60 x 20 block moved 200 mm in x spanning x 200..300 with its volume; on an assembly never saved the part has no body.
    /// </summary>
    public static object SaveModel(SeContext context, string? handle, string path, bool overwrite)
    {
        Occurrence occurrence = Occurrence(context, handle);
        AssemblyDocument assembly = occurrence.TopLevelDocument;
        var document = (SolidEdgeDocument)assembly;
        if (!Path.IsPathRooted(document.FullName) || document.Dirty)
        {
            throw new ToolException("unsaved_changes", "Save the assembly first: the new part links to the saved assembly.", "se_documents save or save_as.");
        }

        if (occurrence.Subassembly)
        {
            throw ToolException.InvalidArgument($"'{occurrence.Name}' is a subassembly; save_model takes a part occurrence.");
        }

        string file = context.Paths.Check(path, "path");
        if (!string.Equals(Path.GetExtension(file), ".par", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("The model is saved as a part file (.par).");
        }

        if (DocumentService.FindOpen(context, file) is not null)
        {
            throw new ToolException("file_in_use", $"'{file}' is open in Solid Edge.", "Close it first, or choose another path.");
        }

        if (File.Exists(file))
        {
            if (!overwrite)
            {
                throw new ToolException("file_exists", $"'{file}' already exists.", "Pass overwrite=true to replace it.");
            }

            File.Delete(file);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        assembly.SaveModelAs(occurrence, file);
        if (!File.Exists(file))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{file}'.");
        }

        var saved = (SolidEdgeDocument)context.Application.Documents.Open(file);
        double volume;
        try
        {
            volume = ModelDocument.From(saved).TryBodies()?.Sum(b => b.Body.Volume) ?? 0;
        }
        finally
        {
            saved.Close(false);
        }

        if (volume <= 0)
        {
            throw new ToolException("save_failed", $"'{file}' was written without a body.", "Check the occurrence's part.");
        }

        return new { occurrence = occurrence.Name, path = file, volume = Geo.Mm3(volume) };
    }

    private static object Placed(SeContext context, AssemblyDocument assembly, Occurrence occurrence, double[]? origin, double[]? angles, bool ground)
    {
        if (origin is not null || angles is not null)
        {
            (double x, double y, double z) = Vector3(origin, "origin");
            (double ax, double ay, double az) = Vector3(angles, "angles");
            occurrence.PutTransform(Units.MmToM(x), Units.MmToM(y), Units.MmToM(z), Units.DegToRad(ax), Units.DegToRad(ay), Units.DegToRad(az));
        }

        bool grounded = GroundedIds(assembly).Contains(occurrence.OccurrenceID);
        if (ground && !grounded)
        {
            assembly.Relations3d.AddGround(occurrence);
        }
        else if (!ground && grounded)
        {
            Unground(assembly, occurrence);
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "an occurrence was added");
        return Describe(context, assembly, occurrence, GroundedIds(assembly));
    }

    private static string ExistingFile(SeContext context, string path)
    {
        string file = context.Paths.Check(path, "path");
        return File.Exists(file) ? file : throw ToolException.NotFound($"The file '{file}' does not exist.", "Save the part first (se_documents save_as) or check the path.");
    }

    private static string NewFile(SeContext context, string path)
    {
        string file = context.Paths.Check(path, "path");
        return File.Exists(file)
            ? throw new ToolException("file_exists", $"'{file}' already exists.", "Give the path of a new file.")
            : file;
    }

    private static DocumentTypeConstants ComponentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".par" => DocumentTypeConstants.igPartDocument,
        ".psm" => DocumentTypeConstants.igSheetMetalDocument,
        ".asm" => DocumentTypeConstants.igAssemblyDocument,
        _ => throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' must be a .par, .psm or .asm file."),
    };
}
