// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Internal components: parts, sheet metal parts and subassemblies kept inside the assembly file until they are published
/// as files of their own (Create Internal Component, Publish Internal Components). Verified in Solid Edge 2026:
/// InternalComponents.CreateInternalComponent(type, name, ground) adds an empty component named name.par, .psm or .asm
/// (an existing name gets _1), placed as an occurrence whose file is "assembly.asm\name.par" and grounded or not as asked;
/// DeleteInternalComponent(name with its extension) removes it and its occurrence; they stay after the assembly is saved and
/// reopened; PublishInternalComponents(folder) writes each one to the folder, points its occurrence at the new file and
/// empties the list. Their geometry is modelled in Solid Edge: building features through InternalComponent.ProfileSets
/// after EditStart gave RPC_E_SERVERFAULT, and the occurrence's document gives E_ACCESSDENIED.
/// </summary>
public static class InternalComponentService
{
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] components = [.. Components(assembly).Select(c => Describe(context, assembly, c))];
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), count = components.Length, components };
    }

    public static object Create(SeContext context, string? documentHandle, string? name, InternalComponentKind kind, bool ground)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string title = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the name of the new component, without extension.")
            : name.Trim();
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.HasExtension(title))
        {
            throw ToolException.InvalidArgument($"'{title}' is not a valid component name: give a file name without folder or extension.");
        }

        (InternalComponentTypeConstant type, string extension) = kind switch
        {
            InternalComponentKind.Part => (InternalComponentTypeConstant.InternalComponentTypeConstant_Part, ".par"),
            InternalComponentKind.SheetMetal => (InternalComponentTypeConstant.InternalComponentTypeConstant_SheetMetal, ".psm"),
            InternalComponentKind.Assembly => (InternalComponentTypeConstant.InternalComponentTypeConstant_Assembly, ".asm"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        string fullName = title + extension;
        // Solid Edge would add _1 to a name in use; refuse it so the component gets the name asked for.
        if (Components(assembly).Any(c => string.Equals(c.Name, fullName, StringComparison.OrdinalIgnoreCase))
            || Items<Occurrence>(assembly.Occurrences).Any(o => string.Equals(Path.GetFileName(o.OccurrenceFileName), fullName, StringComparison.OrdinalIgnoreCase)))
        {
            throw ToolException.InvalidArgument($"The assembly already has a component '{fullName}'.", "Choose another 'name'.");
        }

        int before = assembly.InternalComponents.Count;
        assembly.InternalComponents.CreateInternalComponent(type, title, ground, out InternalComponent created);
        if (assembly.InternalComponents.Count != before + 1 || created is null)
        {
            throw new ToolException("create_failed", $"Solid Edge did not create the internal component '{fullName}'.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "an internal component was created");
        return new { component = Describe(context, assembly, created), count = assembly.InternalComponents.Count };
    }

    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        InternalComponent component = Find(assembly, name);
        string fullName = component.Name;
        assembly.InternalComponents.DeleteInternalComponent(fullName);
        if (Components(assembly).Any(c => string.Equals(c.Name, fullName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("delete_failed", $"Solid Edge kept the internal component '{fullName}'.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "an internal component was deleted");
        return new { deleted = fullName, count = assembly.InternalComponents.Count };
    }

    /// <summary>Writes every internal component to <paramref name="folder"/> and points its occurrence at the new file.</summary>
    public static object Publish(SeContext context, string? documentHandle, string? folder, bool overwrite)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string target = context.Paths.Check(folder ?? throw ToolException.InvalidArgument("'folder' is required: where the component files are written."), "folder");
        string[] names = [.. Components(assembly).Select(c => c.Name)];
        if (names.Length == 0)
        {
            throw new ToolException("nothing_to_publish", "The assembly has no internal components.", "Create one with action 'create'.");
        }

        string[] files = [.. names.Select(n => Path.Combine(target, n))];
        string[] existing = [.. files.Where(File.Exists)];
        if (existing.Length > 0 && !overwrite)
        {
            throw new ToolException("file_exists", $"'{string.Join("', '", existing)}' already exist.", "Pass overwrite=true to replace them, or another folder.");
        }

        foreach (string file in existing)
        {
            if (DocumentService.FindOpen(context, file) is not null)
            {
                throw new ToolException("file_in_use", $"'{file}' is open in Solid Edge.", "Close it first, or choose another folder.");
            }

            File.Delete(file);
        }

        Directory.CreateDirectory(target);
        assembly.PublishInternalComponents(target);
        string[] missing = [.. files.Where(f => !File.Exists(f))];
        if (missing.Length > 0)
        {
            throw new ToolException("publish_failed", $"Solid Edge did not write '{string.Join("', '", missing)}'.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "internal components were published");
        HashSet<int> grounded = GroundedIds(assembly);
        object[] occurrences = [.. Items<Occurrence>(assembly.Occurrences)
            .Where(o => files.Any(f => string.Equals(Path.GetFullPath(o.OccurrenceFileName), f, StringComparison.OrdinalIgnoreCase)))
            .Select(o => AssemblyService.Describe(context, assembly, o, grounded))];
        return new { folder = target, files, occurrences, left = assembly.InternalComponents.Count };
    }

    private static IEnumerable<InternalComponent> Components(AssemblyDocument assembly) => Items<InternalComponent>(assembly.InternalComponents);

    private static InternalComponent Find(AssemblyDocument assembly, string? name)
    {
        string wanted = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: an internal component from action 'list'.")
            : name.Trim();
        InternalComponent[] all = [.. Components(assembly)];
        return all.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c.Name), wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw ToolException.NotFound($"The assembly has no internal component '{wanted}'.",
                all.Length == 0 ? "It has none." : $"It has: {string.Join(", ", all.Select(c => c.Name))}.");
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, InternalComponent component)
    {
        string name = component.Name;
        Occurrence? occurrence = Items<Occurrence>(assembly.Occurrences)
            .FirstOrDefault(o => string.Equals(Path.GetFileName(o.OccurrenceFileName), name, StringComparison.OrdinalIgnoreCase));
        return new
        {
            name,
            kind = Path.GetExtension(name).ToLowerInvariant() switch { ".psm" => "sheet_metal", ".asm" => "assembly", _ => "part" },
            occurrence = occurrence is null ? null : AssemblyService.Describe(context, assembly, occurrence, GroundedIds(assembly)),
        };
    }
}
