// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Materials from the Solid Edge libraries (MatTable). The API gives the values in SI; they are returned with the unit in
/// the name. GetMaterialList and GetMatLibFileName give E_NOTIMPL in Solid Edge 2026: each library is walked instead.
/// </summary>
public static class MaterialService
{
    private static readonly (MatTablePropIndex Index, string Name, double Scale)[] Numeric =
    [
        (MatTablePropIndex.seDensity, "densityKgM3", 1),
        (MatTablePropIndex.seModulusElasticity, "elasticModulusMPa", 1e-6),
        (MatTablePropIndex.sePoissonRatio, "poissonRatio", 1),
        (MatTablePropIndex.seYieldStress, "yieldStressMPa", 1e-6),
        (MatTablePropIndex.seUltimateStress, "ultimateStressMPa", 1e-6),
        (MatTablePropIndex.seElongation, "elongation", 1),
        (MatTablePropIndex.seCoefOfThermalExpansion, "thermalExpansionPerK", 1),
        (MatTablePropIndex.seThermalConductivity, "thermalConductivityWPerMK", 1),
        (MatTablePropIndex.seSpecificHeat, "specificHeatJPerKgK", 1),
    ];

    public static object Libraries(SeContext context) => new { libraries = LibraryNames(Table(context)) };

    public static object List(SeContext context, string? library, string? filter)
    {
        MatTable table = Table(context);
        int budget = context.MaxListItems;
        var result = new List<object>();
        foreach (string name in library is null ? LibraryNames(table) : [RequireLibrary(table, library)])
        {
            string[] materials = Materials(table, name)
                .Where(m => filter is null || m.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(budget, 0)).ToArray();
            budget -= materials.Length;
            result.Add(new { library = name, count = materials.Length, materials });
        }

        return new { libraries = result, truncated = budget <= 0 ? true : (bool?)null };
    }

    public static object Info(SeContext context, string name, string? library)
    {
        MatTable table = Table(context);
        (string lib, name) = FindLibrary(table, name, library);
        return new { material = name, library = lib, properties = Properties(index =>
            Late.CallByRef(table, "GetMaterialPropValueFromLibrary", [name, lib, (int)index, null], 3)[3]) };
    }

    public static object Current(SeContext context, string? documentHandle)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is AssemblyDocument assembly)
        {
            return assembly.WeldmentAssembly ? Beads(context, assembly, null)
                : new
                {
                    document = context.DocumentHandle(document),
                    note = "An assembly has no material of its own: each part has its own (se_physical lists them with their density).",
                };
        }

        MatTable table = Table(context);
        table.GetCurrentMaterialName(document, out string name);
        return Describe(context, table, document, name);
    }

    /// <summary>The density of the document's material in kg/m³, or null (no material, or not readable).</summary>
    internal static double? Density(SeContext context, SolidEdgeDocument document)
    {
        if (MaterialName(context, document) is null)
        {
            return null;
        }

        try
        {
            object? value = Late.CallByRef(Table(context), "GetMaterialPropValueFromDoc", [document, (int)MatTablePropIndex.seDensity, null], 2)[2];
            return value is null ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The material assigned to a part or sheet metal document, or null (none, or not readable).</summary>
    internal static string? MaterialName(SeContext context, SolidEdgeDocument document)
    {
        try
        {
            Table(context).GetCurrentMaterialName(document, out string name);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Applies a library material to the document and checks that it was assigned.</summary>
    public static object Apply(SeContext context, string? documentHandle, string name, string? library)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        MatTable table = Table(context);
        (string lib, name) = FindLibrary(table, name, library);
        if (document is AssemblyDocument assembly)
        {
            return ApplyToBeads(context, table, assembly, name, lib);
        }

        // The material changes density and appearance, not the topology: faces and edges stay valid (verified).
        table.ApplyMaterialToDoc(document, name, lib);
        table.GetCurrentMaterialName(document, out string applied);
        if (!string.Equals(applied, name, StringComparison.Ordinal))
        {
            throw new ToolException("material_not_applied", $"Solid Edge did not assign '{name}' (the document material is '{applied}').",
                "Check that the document is a part or sheet metal document and that it is not read-only.");
        }

        return Describe(context, table, document, applied, lib);
    }

    /// <summary>
    /// The material of a weldment's weld beads: its name and the density of the library material, which the mass of the
    /// beads uses. Verified: a new weldment has no bead material and a density of 0, so se_physical gives no mass once it has
    /// a bead; ApplyMaterialToDoc gives E_INVALIDARG on an assembly.
    /// </summary>
    private static object ApplyToBeads(SeContext context, MatTable table, AssemblyDocument assembly, string name, string library)
    {
        if (!assembly.WeldmentAssembly)
        {
            throw new ToolException("wrong_document_type", "An assembly has no material of its own: each part has its own, and only the weld beads of a weldment take one.",
                "Apply the material to the part documents (se_material apply with document = the part).");
        }

        object? value = Late.CallByRef(table, "GetMaterialPropValueFromLibrary", [name, library, (int)MatTablePropIndex.seDensity, null], 3)[3];
        double density = value is null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!(density > 0))
        {
            throw new ToolException("material_not_applied", $"The material '{name}' has no density in '{library}', so the beads would have no mass.",
                "Pick a material with a density (se_material info).");
        }

        assembly.WeldmentAssemblyBeadMaterial = name;
        assembly.WeldmentAssemblyBeadDensity = density;
        if (!string.Equals(assembly.WeldmentAssemblyBeadMaterial, name, StringComparison.Ordinal)
            || Math.Abs(assembly.WeldmentAssemblyBeadDensity - density) > 1e-9 * density)
        {
            throw new ToolException("material_not_applied", $"Solid Edge did not keep '{name}' as the material of the weld beads.",
                "Check that the weldment is not read-only.");
        }

        return Beads(context, assembly, library);
    }

    private static object Beads(SeContext context, AssemblyDocument assembly, string? library)
    {
        string material = assembly.WeldmentAssemblyBeadMaterial;
        double density = assembly.WeldmentAssemblyBeadDensity;
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            material = string.IsNullOrEmpty(material) ? null : material,
            library,
            appliesTo = "weld_beads",
            beadDensityKgM3 = density > 0 ? Geo.R(density) : (double?)null,
            note = density > 0 ? "The material of the weld beads; each part keeps its own (se_physical gives the mass)."
                : "The weld beads have no material, so the weldment has no mass: use action 'apply' on this document.",
        };
    }

    /// <summary>Sheet metal gauge of the document, or <c>null</c> when it has none.</summary>
    public static string? GaugeName(SeContext context, SolidEdgeDocument document)
    {
        try
        {
            Table(context).GetCurrentGageName(document, out string name);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Applies a sheet metal gauge of a material library (thickness, bend radius, relief) and returns the library. Verified:
    /// ApplyGageFromLibraryToDoc(doc, "10 gage", "Materials") sets thickness and bend radius to 3.581 mm. Without a library,
    /// each one is tried until the document reports the gauge.
    /// </summary>
    public static string ApplyGauge(SeContext context, SolidEdgeDocument document, string gauge, string? library)
    {
        MatTable table = Table(context);
        foreach (string lib in library is null ? LibraryNames(table) : [RequireLibrary(table, library)])
        {
            try
            {
                table.ApplyGageFromLibraryToDoc(document, gauge, lib);
            }
            catch (Exception ex) when (ex is NullReferenceException || ex is COMException com && !ComErrors.IsGone(com))
            {
                // A gauge the library does not have gives E_POINTER (NullReferenceException) and leaves the document unchanged.
                continue;
            }

            table.GetCurrentGageName(document, out string applied);
            if (string.Equals(applied, gauge, StringComparison.OrdinalIgnoreCase))
            {
                return lib;
            }
        }

        throw ToolException.NotFound($"There is no sheet metal gauge '{gauge}'{(library is null ? " in the material libraries" : $" in '{library}'")}.",
            "Pass the gauge name as the library writes it, e.g. '16 gage' (se_sheetmetal 'settings' shows the current one).");
    }

    private static object Describe(SeContext context, MatTable table, SolidEdgeDocument document, string name, string? library = null)
    {
        object? properties = string.IsNullOrEmpty(name) ? null : Properties(index =>
            Late.CallByRef(table, "GetMaterialPropValueFromDoc", [document, (int)index, null], 2)[2]);
        double? density = null;
        double? mass = null;
        if (ModelDocument.TryGet(document) is { } model && model.TryModel() is { } solid)
        {
            double stored = InspectService.Density(context, model, solid).Density;
            density = stored > 0 ? stored : null;
            double volume = model.Volume();
            mass = density is { } d && volume > 0 ? Math.Round(d * volume, 9) : null;
        }

        return new
        {
            document = context.DocumentHandle(document),
            material = string.IsNullOrEmpty(name) ? null : name,
            library,
            properties,
            documentDensityKgM3 = density,
            massKg = mass,
            note = string.IsNullOrEmpty(name) ? "No material assigned: use action 'apply' (see 'list')." : null,
        };
    }

    /// <summary>Properties through IDispatch: with the typed enumeration of the interop, Solid Edge gives DISP_E_BADCALLEE.</summary>
    private static Dictionary<string, object?> Properties(Func<MatTablePropIndex, object?> read)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["faceStyle"] = read(MatTablePropIndex.seFaceStyle) as string,
        };
        foreach ((MatTablePropIndex index, string key, double scale) in Numeric)
        {
            object? value = read(index);
            values[key] = value is null ? null : Geo.R(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) * scale);
        }

        return values;
    }

    private static MatTable Table(SeContext context) => context.Application.GetMaterialTable();

    private static string[] LibraryNames(MatTable table)
    {
        table.GetMaterialLibraryList(out object list, out _);
        return Strings(list);
    }

    private static string[] Materials(MatTable table, string library)
    {
        table.GetMaterialListFromLibrary(library, out _, out object list);
        return Strings(list);
    }

    private static string[] Strings(object? list) => list is Array array ? array.Cast<object?>().OfType<string>().ToArray() : [];

    private static string RequireLibrary(MatTable table, string library) =>
        NameMatch.Resolve(LibraryNames(table), library)
        ?? throw ToolException.NotFound($"There is no material library '{library}'.{NameMatch.Hint(LibraryNames(table), library)}", "Use action 'libraries' to list them.");

    /// <summary>
    /// Given library (which must contain the material) or the first one that contains it, with the material name as the
    /// library writes it. Libraries differ between installations (and languages): a name that is not found is answered with
    /// the closest ones there are.
    /// </summary>
    private static (string Library, string Name) FindLibrary(MatTable table, string name, string? library)
    {
        string[] libraries = library is null ? LibraryNames(table) : [RequireLibrary(table, library)];
        var all = new List<string>();
        foreach (string lib in libraries)
        {
            string[] materials = Materials(table, lib);
            if (NameMatch.Resolve(materials, name) is { } found)
            {
                return (lib, found);
            }

            all.AddRange(materials);
        }

        throw ToolException.NotFound($"There is no material '{name}'{(library is null ? "" : $" in '{library}'")}.{NameMatch.Hint(all, name)}",
            "Use action 'list' (optionally with 'filter') and pass the name as the library writes it.");
    }
}
