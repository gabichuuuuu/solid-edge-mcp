// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The simplified model of an assembly: the assembly without chosen components, or without those smaller than a share of
/// its size, kept in the assembly and written as a part. Verified in Solid Edge 2026: CreateOrEditSimplifiedAssembly with
/// the components to leave out makes it (IsAssemblySimplified); GetSimplifiedAssemblyInputs reads back what it left out;
/// SaveSimplifiedAssemblyAs writes a part of what is left (a block and a pin, the pin left out: the part holds the block's
/// 120000 mm3); DeleteSimplifiedAssembly removes it.
/// </summary>
public static partial class SimplifyService
{
    private static AssemblyDocument? AssemblyTarget(SeContext context, string? documentHandle, string[]? faces, string? feature) =>
        faces is { Length: > 0 } || feature is not null ? null : context.GetDocument(documentHandle) as AssemblyDocument;

    private static object RunAssembly(SeContext context, AssemblyDocument assembly, string action, string[]? occurrences, double? ratio, string? path) => action switch
    {
        "status" => AssemblyStatus(context, assembly),
        "create" => CreateAssembly(context, assembly, occurrences, ratio),
        "delete" => DeleteAssembly(context, assembly),
        "save_as" => SaveAssembly(context, assembly, path),
        _ => throw ToolException.InvalidArgument($"An assembly is simplified with status, create, delete and save_as, not '{action}'.",
            "Simplify a part (holes, rounds, faces) in the part document."),
    };

    private static object AssemblyStatus(SeContext context, AssemblyDocument assembly)
    {
        if (!assembly.IsAssemblySimplified)
        {
            return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), simplified = false };
        }

        Array excluded = Array.Empty<object>(), included = Array.Empty<object>();
        assembly.GetSimplifiedAssemblyInputs(out int excludedCount, ref excluded, out double ratio, out int includedCount, ref included);
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            simplified = true,
            excluded = Names(excluded, excludedCount),
            ratio,
            included = Names(included, includedCount),
        };
    }

    /// <summary>Makes (or remakes) the simplified assembly without the given components and, with a ratio, without the smaller ones.</summary>
    private static object CreateAssembly(SeContext context, AssemblyDocument assembly, string[]? occurrences, double? ratio)
    {
        Occurrence[] excluded = [.. (occurrences ?? []).Select(h => AssemblyService.Occurrence(context, h, "occurrences"))];
        if (excluded.Length == 0 && ratio is null)
        {
            throw ToolException.InvalidArgument("Give 'occurrences' to leave out, or 'ratio' to leave out the components smaller than that share of the assembly (0-1).");
        }

        if (ratio is { } r && (!double.IsFinite(r) || r < 0 || r >= 1))
        {
            throw ToolException.InvalidArgument("'ratio' is a share of the assembly size between 0 and 1, e.g. 0.1.");
        }

        Array exclude = excluded.Cast<object>().ToArray(), include = Array.Empty<object>();
        assembly.CreateOrEditSimplifiedAssembly(excluded.Length, ref exclude, ratio ?? 0, 0, ref include);
        Array readExcluded = Array.Empty<object>(), readIncluded = Array.Empty<object>();
        assembly.GetSimplifiedAssemblyInputs(out int count, ref readExcluded, out _, out _, ref readIncluded);
        if (!assembly.IsAssemblySimplified || count != excluded.Length)
        {
            throw new ToolException("simplify_failed", $"Solid Edge left out {count} of the {excluded.Length} components.");
        }

        return AssemblyStatus(context, assembly);
    }

    private static object DeleteAssembly(SeContext context, AssemblyDocument assembly)
    {
        if (!assembly.IsAssemblySimplified)
        {
            throw new ToolException("no_simplified_model", "The assembly has no simplified model.");
        }

        assembly.DeleteSimplifiedAssembly();
        return assembly.IsAssemblySimplified
            ? throw new ToolException("simplify_failed", "Solid Edge kept the simplified model.")
            : AssemblyStatus(context, assembly);
    }

    /// <summary>Writes the simplified assembly as a part file (allowed folders).</summary>
    private static object SaveAssembly(SeContext context, AssemblyDocument assembly, string? path)
    {
        if (!assembly.IsAssemblySimplified)
        {
            throw new ToolException("no_simplified_model", "The assembly has no simplified model.", "Make it first with action 'create'.");
        }

        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("Action 'save_as' requires 'path': the .par file to write."), "path");
        if (!string.Equals(System.IO.Path.GetExtension(file), ".par", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("A simplified assembly is written as a part (.par).");
        }

        DateTime before = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        assembly.SaveSimplifiedAssemblyAs(file);
        return File.Exists(file) && File.GetLastWriteTimeUtc(file) != before
            ? new { path = file, note = "Open it with se_documents to measure it (se_physical)." }
            : throw new ToolException("save_failed", $"Solid Edge did not write '{file}'.");
    }

    private static string[] Names(Array items, int count) =>
        count == 0 || items is null ? [] : [.. items.Cast<object?>().OfType<Occurrence>().Select(o => o.Name)];
}