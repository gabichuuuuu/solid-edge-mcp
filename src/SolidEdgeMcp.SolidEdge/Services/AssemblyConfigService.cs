// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Display configurations of an assembly: named states of which components are shown, as the "Configurations" command
/// saves and applies them. Verified: Solid Edge keeps them in a .cfg file next to the assembly, so an assembly that was
/// never saved refuses them (STG_E_ACCESSDENIED).
/// </summary>
public static class AssemblyConfigService
{
    private const int StorageAccessDenied = unchecked((int)0x80030005);

    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Saved(context, documentHandle);
        object[] configurations = Items<Configuration>(assembly.Configurations).Select(Describe).ToArray();
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            active = assembly.ActiveConfiguration,
            count = configurations.Length,
            configurations,
            hidden = Hidden(assembly),
        };
    }

    /// <summary>Saves the current display (shown and hidden components) under a new name.</summary>
    public static object Save(SeContext context, string? documentHandle, string? name)
    {
        string title = RequireName(name);
        AssemblyDocument assembly = Saved(context, documentHandle);
        if (Find(assembly, title) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a configuration '{title}'.", "Pass another 'name', or delete it first.");
        }

        try
        {
            assembly.Configurations.Add(title);
        }
        catch (COMException ex) when (ex.HResult == StorageAccessDenied)
        {
            throw new ToolException("not_supported", $"Solid Edge could not write the configuration file of the assembly: {ex.Message}",
                "Check that the assembly and its .cfg file are not read-only, or save the assembly in another folder (se_documents save_as).", ex);
        }

        Configuration saved = Find(assembly, title)
            ?? throw new ToolException("configuration_failed", $"Solid Edge did not save the configuration '{title}'.");
        string? note = AssemblyExplodeService.IsExploded(assembly)
            ? $"The assembly is exploded, and this display configuration does not keep the explode. {AssemblyExplodeService.Keep}"
            : null;
        return new { saved = Describe(saved), hidden = Hidden(assembly), note };
    }

    /// <summary>Applies a configuration: the components it hides are hidden and the others shown.</summary>
    public static object Apply(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Saved(context, documentHandle);
        Configuration configuration = Require(assembly, name);
        assembly.Configurations.Apply(configuration.Name);
        if (!string.Equals(assembly.ActiveConfiguration, configuration.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("configuration_failed", $"Solid Edge did not apply the configuration '{configuration.Name}'.");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "a configuration was applied");
        string? note = configuration.ConfigurationType == ConfigurationTypeConstants.seConfigurationType_Explode
            ? "An explode configuration shows in drawing views (se_drawing base_view with 'configuration'); applied to the assembly, its components do not read as exploded. se_assembly_config explode explodes them again."
            : null;
        return new { applied = configuration.Name, hidden = Hidden(assembly), note };
    }

    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Saved(context, documentHandle);
        Configuration configuration = Require(assembly, name);
        string title = configuration.Name;
        configuration.Delete();
        if (Find(assembly, title) is not null)
        {
            throw new ToolException("configuration_failed", $"Solid Edge did not delete the configuration '{title}'.");
        }

        return new { deleted = title, count = assembly.Configurations.Count };
    }

    private static AssemblyDocument Saved(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        // A new assembly reports its bare name (Asm1) as FullName, without a folder.
        if (!System.IO.Path.IsPathRooted(((SolidEdgeDocument)assembly).FullName))
        {
            throw new ToolException("not_supported", "Solid Edge keeps the configurations in a .cfg file next to the assembly, and this one was never saved.",
                "Save it first (se_documents save_as).");
        }

        return assembly;
    }

    private static Configuration? Find(AssemblyDocument assembly, string name) =>
        Items<Configuration>(assembly.Configurations).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static Configuration Require(AssemblyDocument assembly, string? name)
    {
        string title = RequireName(name);
        return Find(assembly, title) ?? throw new ToolException("not_found", $"The assembly has no configuration '{title}'.",
            "List them with se_assembly_config action list.");
    }

    private static object Describe(Configuration configuration) => new
    {
        name = configuration.Name,
        type = configuration.ConfigurationType == ConfigurationTypeConstants.seConfigurationType_Explode ? "explode" : "display",
    };

    /// <summary>Names of the top-level components that are hidden now.</summary>
    private static string[] Hidden(AssemblyDocument assembly) =>
        Items<Occurrence>(assembly.Occurrences).Where(o => !o.Visible).Select(o => o.Name).ToArray();

    private static string RequireName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? throw ToolException.InvalidArgument("'name' (the configuration) is required.") : name.Trim();
}
