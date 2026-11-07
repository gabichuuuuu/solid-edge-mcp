// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Structure of an assembly: active and inactive components, new subassemblies made from occurrences, occurrences moved
/// into a subassembly and subassemblies dispersed into their parent.
/// </summary>
public static partial class AssemblyService
{
    /// <summary>
    /// Activates (loads for editing) or inactivates a part occurrence; with <paramref name="all"/>, activates every component.
    /// Verified in Solid Edge 2026: the faces of an inactive occurrence can still be read; Activate=false on a subassembly
    /// occurrence is ignored.
    /// </summary>
    public static object Activate(SeContext context, string? documentHandle, string? handle, bool activate, bool all)
    {
        if (all)
        {
            if (!activate)
            {
                throw ToolException.InvalidArgument("'all' only goes with 'activate'; inactivate the occurrences one by one.");
            }

            AssemblyDocument assembly = handle is null ? Assembly(context, documentHandle) : Occurrence(context, handle).TopLevelDocument;
            assembly.ActivateAll();
            List<Occurrence> occurrences = Items<Occurrence>(assembly.Occurrences).ToList();
            return new
            {
                document = context.DocumentHandle((SolidEdgeDocument)assembly),
                activated = occurrences.Count,
                inactive = occurrences.Where(o => IsActive(o) == false).Select(o => o.Name).ToList(),
            };
        }

        Occurrence occurrence = Occurrence(context, handle);
        if (!activate && occurrence.Subassembly)
        {
            throw new ToolException("not_supported", $"'{occurrence.Name}' is a subassembly: Solid Edge ignores inactivating a subassembly occurrence.",
                "Inactivate its parts in the subassembly (se_documents open, then se_assembly inactivate).");
        }

        AssemblyDocument owner = occurrence.TopLevelDocument;
        occurrence.Activate = activate;
        if (IsActive(occurrence) is { } state && state != activate)
        {
            // Verified in Solid Edge 2026: Activate=false is ignored until the assembly has been saved.
            throw new ToolException("activate_failed", $"Solid Edge left '{occurrence.Name}' {(state ? "active" : "inactive")}.",
                string.IsNullOrEmpty(((SolidEdgeDocument)owner).FullName)
                    ? "Solid Edge inactivates components of a saved assembly only: save it first (se_documents save_as)."
                    : "Check the occurrence with se_assembly 'list'.");
        }

        return Describe(context, owner, occurrence, GroundedIds(owner));
    }

    /// <summary>
    /// New subassembly from occurrences of the assembly, as Transfer to a new subassembly does: an empty assembly is saved to
    /// <paramref name="path"/> (from <paramref name="template"/> or the default assembly template), inserted at the origin
    /// and the occurrences moved into it, keeping their positions. BundleOccurrences is not used: in Solid Edge 2026 it
    /// leaves the assembly's Occurrences failing with RPC_E_SERVERFAULT.
    /// </summary>
    public static object Bundle(SeContext context, string[]? occurrences, string? path, string? template)
    {
        List<Occurrence> moving = Occurrences(context, occurrences);
        AssemblyDocument assembly = SameAssembly(moving);
        string file = context.Paths.Check(path, "path");
        if (!System.IO.Path.GetExtension(file).Equals(".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("'path' must be a new .asm file.");
        }

        if (File.Exists(file))
        {
            throw new ToolException("file_exists", $"'{file}' already exists.",
                "Choose a new file name; to move occurrences into an existing subassembly, insert it and use action 'transfer'.");
        }

        string? templatePath = string.IsNullOrWhiteSpace(template) ? null : DocumentService.CheckTemplate(context, template, DocumentTypeConstants.igAssemblyDocument);
        var empty = (SolidEdgeDocument)(templatePath is null
            ? context.Application.Documents.Add("SolidEdge.AssemblyDocument")
            : context.Application.Documents.Add("SolidEdge.AssemblyDocument", templatePath));
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            empty.SaveAs(file);
        }
        finally
        {
            empty.Close(false);
        }

        if (!File.Exists(file))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{file}'.");
        }

        Occurrence subassembly;
        try
        {
            subassembly = assembly.Occurrences.AddWithTransform(file, 0, 0, 0, 0, 0, 0);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            TryDeleteFile(file);
            throw;
        }

        object result;
        try
        {
            result = MoveInto(context, assembly, moving, occurrences!, subassembly);
        }
        catch (Exception ex) when (ex is ToolException or COMException && SubCount(subassembly) == 0)
        {
            subassembly.Delete();
            TryDeleteFile(file);
            throw;
        }

        return new
        {
            created = file,
            result,
            note = "The new subassembly is saved when the assembly is (se_documents save or save_as).",
        };
    }

    /// <summary>Moves occurrences into a subassembly of the same assembly (TransferOccurrences), keeping their positions.</summary>
    public static object Transfer(SeContext context, string[]? occurrences, string? subassemblyHandle)
    {
        List<Occurrence> moving = Occurrences(context, occurrences);
        Occurrence target = Occurrence(context, subassemblyHandle, "subassembly");
        if (!target.Subassembly)
        {
            throw new ToolException("wrong_occurrence", $"'{target.Name}' is not a subassembly.", "Pass a subassembly occurrence in 'subassembly', or use action 'bundle' to make a new one.");
        }

        AssemblyDocument assembly = SameAssembly([.. moving, target]);
        if (moving.Any(o => o.OccurrenceID == target.OccurrenceID))
        {
            throw ToolException.InvalidArgument("The subassembly cannot be moved into itself.");
        }

        if (target.OccurrenceDocument is SolidEdgeDocument document && document.FullName is { Length: > 0 } subFile)
        {
            // Its file changes: the policy must allow writing it.
            context.Paths.Check(subFile, "subassembly file");
        }

        return MoveInto(context, assembly, moving, occurrences!, target);
    }

    /// <summary>
    /// Disperses a subassembly: its components move up into the assembly at the same positions and the subassembly
    /// occurrence is removed (the subassembly file is not changed). <paramref name="all"/>: every occurrence of that subassembly.
    /// </summary>
    public static object Disperse(SeContext context, string? handle, bool all)
    {
        Occurrence subassembly = Occurrence(context, handle);
        if (!subassembly.Subassembly)
        {
            throw new ToolException("wrong_occurrence", $"'{subassembly.Name}' is not a subassembly.");
        }

        AssemblyDocument assembly = subassembly.TopLevelDocument;
        string name = subassembly.Name;
        string file = subassembly.OccurrenceFileName;
        List<Occurrence> before = Items<Occurrence>(assembly.Occurrences).ToList();
        HashSet<int> ids = before.Select(o => o.OccurrenceID).ToHashSet();
        List<Occurrence> dispersing = all
            ? before.Where(o => o.Subassembly && string.Equals(o.OccurrenceFileName, file, StringComparison.OrdinalIgnoreCase)).ToList()
            : [subassembly];
        HashSet<int> dispersingIds = dispersing.Select(o => o.OccurrenceID).ToHashSet();
        List<string> dispersedHandles = dispersing.Select(o => context.Register(o, "occurrence", (SolidEdgeDocument)assembly)).ToList();
        int expected = dispersing.Sum(SubCount);

        assembly.DisperseSubassembly(subassembly, all);
        foreach (string dispersed in dispersedHandles)
        {
            context.Handles.Remove(dispersed, "the subassembly was dispersed");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "a subassembly was dispersed");
        List<Occurrence> after = Items<Occurrence>(assembly.Occurrences).ToList();
        List<Occurrence> added = after.Where(o => !ids.Contains(o.OccurrenceID)).ToList();
        if (added.Count != expected || after.Any(o => dispersingIds.Contains(o.OccurrenceID)))
        {
            throw new ToolException("disperse_failed",
                $"Dispersing '{name}' brought up {added.Count} of {expected} components.", "Check the assembly with se_assembly 'list' (recursive).");
        }

        HashSet<int> grounded = GroundedIds(assembly);
        return new
        {
            dispersed = name,
            subassemblies = dispersing.Count,
            occurrences = added.Select(o => Describe(context, assembly, o, grounded)).ToList(),
            count = after.Count,
        };
    }

    /// <summary>
    /// Copies the components of another saved assembly into this one, associatively (an assembly copy that follows its
    /// source), optionally mirrored about an assembly plane. Verified: AssemblyCopies.Add places the source's top-level
    /// components at their positions in the source's active member (or mirrored, the type then reads Mirror); they come in
    /// ungrounded.
    /// </summary>
    public static object CopyAssembly(SeContext context, string? documentHandle, string path, string? plane)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string file = context.Paths.Check(path, "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"There is no file '{file}'.");
        }

        if (!string.Equals(System.IO.Path.GetExtension(file), ".asm", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("An assembly copy is made from another saved assembly (.asm).", "Insert a part with action 'insert'.");
        }

        if (string.Equals(file, DocumentService.PathOf((SolidEdgeDocument)assembly), StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("An assembly cannot copy itself.");
        }

        if (Items<AssemblyCopy>(assembly.AssemblyCopies).Any(c => string.Equals(c.SourceFilename, file, StringComparison.OrdinalIgnoreCase)))
        {
            // Verified: with two copies of the same assembly, plain or mirrored, SaveAs fails with DISP_E_EXCEPTION.
            throw new ToolException("copy_exists", $"The assembly already has a copy of '{System.IO.Path.GetFileName(file)}', and Solid Edge cannot save an assembly with two.",
                "Copy another assembly, or save the copy of this one under another name and copy that.");
        }

        object? mirror = string.IsNullOrWhiteSpace(plane) ? null : SketchService.AssemblyPlane(context, assembly, plane);
        HashSet<int> ids = Items<Occurrence>(assembly.Occurrences).Select(o => o.OccurrenceID).ToHashSet();
        AssemblyCopy copy;
        try
        {
            copy = mirror is null
                ? assembly.AssemblyCopies.Add(file, AssemblyCopyComponentConstants.seAssemblyCopyComponentsIncludeAll)
                : assembly.AssemblyCopies.Add(file, AssemblyCopyComponentConstants.seAssemblyCopyComponentsIncludeAll, mirror);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            // Verified with an assembly that has alternate positions.
            throw new ToolException("copy_failed", $"Solid Edge could not copy '{System.IO.Path.GetFileName(file)}' (E_FAIL): an assembly with alternate positions or a family is not copied.",
                "Write one member as a plain assembly (se_assembly_family save_member) and copy that.");
        }
        context.ModelChanged((SolidEdgeDocument)assembly, "an assembly copy was added");
        List<Occurrence> added = Items<Occurrence>(assembly.Occurrences).Where(o => !ids.Contains(o.OccurrenceID)).ToList();
        if (copy.Status != AssemblyCopyStatusConstants.seAssemblyCopyStatusOK || added.Count == 0)
        {
            foreach (Occurrence occurrence in added)
            {
                occurrence.Delete();
            }

            throw new ToolException("copy_failed", $"The assembly copy of '{System.IO.Path.GetFileName(file)}' is {copy.Status} with {added.Count} components.",
                "Check that the source assembly opens and has components.");
        }

        HashSet<int> grounded = GroundedIds(assembly);
        return new
        {
            copy = copy.Name,
            source = copy.SourceFilename,
            mirrored = copy.AssemblyCopyType == AssemblyCopyTypeConstants.seAssemblyCopyTypeMirror,
            occurrences = added.Select(o => Describe(context, assembly, o, grounded)).ToList(),
        };
    }

    /// <summary>TransferOccurrences into <paramref name="target"/>, checked by the number of components it then has.</summary>
    private static object MoveInto(SeContext context, AssemblyDocument assembly, List<Occurrence> moving, string[] handles, Occurrence target)
    {
        int before = SubCount(target);
        int topBefore = Items<Occurrence>(assembly.Occurrences).Count();
        List<string> names = moving.Select(o => o.Name).ToList();
        Array transfer = moving.Cast<object>().ToArray();
        assembly.TransferOccurrences(ref transfer, target);
        foreach (string moved in handles)
        {
            context.Handles.Remove(moved, "the occurrence was moved into a subassembly");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "occurrences were moved into a subassembly");
        int after = SubCount(target);
        int topAfter = Items<Occurrence>(assembly.Occurrences).Count();
        if (after != before + moving.Count || topAfter != topBefore - moving.Count)
        {
            throw new ToolException("transfer_failed",
                $"The subassembly has {after - before} of the {moving.Count} occurrences moved into it.", "Check the assembly with se_assembly 'list' (recursive).");
        }

        return new
        {
            moved = names,
            subassembly = Describe(context, assembly, target, GroundedIds(assembly)),
            components = (target.SubOccurrences is { } children ? Items<SubOccurrence>(children) : [])
                .Select(s => new { name = s.Name, file = s.SubOccurrenceFileName }).ToList(),
            count = topAfter,
        };
    }

    private static List<Occurrence> Occurrences(SeContext context, string[]? handles)
    {
        if (handles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' is required: occurrence handles of the assembly (se_assembly 'list').");
        }

        if (handles.Distinct(StringComparer.Ordinal).Count() != handles.Length)
        {
            throw ToolException.InvalidArgument("'occurrences' has the same handle more than once.");
        }

        return handles.Select(h => Occurrence(context, h, "occurrences")).ToList();
    }

    private static AssemblyDocument SameAssembly(List<Occurrence> occurrences)
    {
        AssemblyDocument assembly = occurrences[0].TopLevelDocument;
        if (occurrences.Any(o => !ReferenceEquals(o.TopLevelDocument, assembly)))
        {
            throw ToolException.InvalidArgument("All the occurrences must belong to the same assembly.");
        }

        return assembly;
    }

    /// <summary>Components of a subassembly occurrence (its SubOccurrences; an empty subassembly has none, null).</summary>
    private static int SubCount(Occurrence subassembly) => subassembly.SubOccurrences is { } children ? Late.Count(children) : 0;

    private static bool? IsActive(Occurrence occurrence)
    {
        try
        {
            return occurrence.Activate;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }
}
