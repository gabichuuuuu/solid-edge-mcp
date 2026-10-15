// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Alternate components of an occurrence (Define Alternate Components) and the Large Assembly Performance mode.</summary>
public static partial class AssemblyService
{
    /// <summary>
    /// The files an occurrence may be swapped for. Verified in Solid Edge 2026: Occurrence.AddAlternateComponent(file) makes
    /// IsAlternateComponent true; GetAllAlternateComponents gives E_POINTER through the interop types and answers through
    /// IDispatch when a string array is passed in; RemoveAlternateComponent(file) and RemoveAllAlternateComponents empty it
    /// (the latter gives E_INVALIDARG when there are none).
    /// </summary>
    public static object Alternates(SeContext context, string? handle)
    {
        Occurrence occurrence = Occurrence(context, handle);
        return DescribeAlternates(occurrence);
    }

    public static object AddAlternate(SeContext context, string? handle, string? path)
    {
        Occurrence occurrence = Occurrence(context, handle);
        string file = ExistingFile(context, path ?? throw ToolException.InvalidArgument("'path' is required: the file of the alternate component."));
        if (!string.Equals(Path.GetExtension(file), Path.GetExtension(occurrence.OccurrenceFileName), StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"An alternate of '{Path.GetFileName(occurrence.OccurrenceFileName)}' must be a file of the same kind.");
        }

        if (AlternateFiles(occurrence).Any(f => SamePath(f, file)))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is already an alternate of '{occurrence.Name}'.");
        }

        try
        {
            occurrence.AddAlternateComponent(file);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("alternate_failed", $"Solid Edge did not add '{Path.GetFileName(file)}' as an alternate: {ex.Message.Trim()}", null, ex);
        }

        if (!AlternateFiles(occurrence).Any(f => SamePath(f, file)))
        {
            throw new ToolException("alternate_failed", $"Solid Edge did not add '{Path.GetFileName(file)}' as an alternate of '{occurrence.Name}'.");
        }

        context.ModelChanged((SolidEdgeDocument)occurrence.TopLevelDocument, "an alternate component was added");
        return DescribeAlternates(occurrence);
    }

    /// <summary>Removes one alternate file of an occurrence, or all of them when <paramref name="path"/> is not given.</summary>
    public static object RemoveAlternate(SeContext context, string? handle, string? path)
    {
        Occurrence occurrence = Occurrence(context, handle);
        string[] current = AlternateFiles(occurrence);
        if (current.Length == 0)
        {
            throw new ToolException("no_alternates", $"'{occurrence.Name}' has no alternate components.");
        }

        if (path is null)
        {
            occurrence.RemoveAllAlternateComponents();
        }
        else
        {
            string file = current.FirstOrDefault(f => SamePath(f, path) || string.Equals(Path.GetFileName(f), path.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw ToolException.NotFound($"'{path}' is not an alternate of '{occurrence.Name}'.", $"Its alternates: {string.Join(", ", current)}.");
            occurrence.RemoveAlternateComponent(file);
        }

        string[] after = AlternateFiles(occurrence);
        if (after.Length != (path is null ? 0 : current.Length - 1))
        {
            throw new ToolException("alternate_failed", $"Solid Edge kept {after.Length} alternate(s) of '{occurrence.Name}'.");
        }

        context.ModelChanged((SolidEdgeDocument)occurrence.TopLevelDocument, "an alternate component was removed");
        return DescribeAlternates(occurrence);
    }

    /// <summary>
    /// Large Assembly Performance mode of an assembly, read and set. Verified in Solid Edge 2026: AssemblyDocument.PerformanceMode
    /// reads false and takes true and false.
    /// </summary>
    public static object Performance(SeContext context, string? documentHandle, bool? enabled)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        bool previous = assembly.PerformanceMode;
        if (enabled is { } wanted && wanted != previous)
        {
            assembly.PerformanceMode = wanted;
            if (assembly.PerformanceMode != wanted)
            {
                throw new ToolException("setting_rejected", "Solid Edge did not change the performance mode.");
            }
        }

        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), performanceMode = assembly.PerformanceMode, previous };
    }

    private static object DescribeAlternates(Occurrence occurrence)
    {
        string[] files = AlternateFiles(occurrence);
        return new { occurrence = occurrence.Name, file = occurrence.OccurrenceFileName, count = files.Length, alternates = files };
    }

    private static string[] AlternateFiles(Occurrence occurrence)
    {
        if (!occurrence.IsAlternateComponent())
        {
            return [];
        }

        object?[] args = Late.CallByRef(occurrence, "GetAllAlternateComponents", [0, Array.Empty<string>()], 0, 1);
        return args[1] is Array names ? [.. names.Cast<object?>().OfType<string>()] : [];
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
