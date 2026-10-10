using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Tabla de variables (variables de usuario y cotas). Los valores se dan en las unidades de las
/// herramientas (mm, grados, mm², mm³); internamente Solid Edge usa metros y radianes.
/// </summary>
public static class VariableService
{
    public static readonly IReadOnlyList<string> Kinds = ["all", "user", "dimension"];
    public static readonly IReadOnlyList<string> UnitNames = ["distance", "angle", "area", "volume", "mass", "scalar"];

    // Valores de SolidEdgeConstants.VariableNameBy y VariableVarType (el API los recibe como VARIANT).
    private const int NamedByBoth = 2;
    private const int VarTypeDimension = 1;
    private const int VarTypeVariable = 2;
    private const int VarTypeBoth = 3;

    public static object List(SeContext context, string? documentHandle, string? filter, string kind, int offset, int limit)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        int varType = kind switch
        {
            "all" => VarTypeBoth,
            "user" => VarTypeVariable,
            "dimension" => VarTypeDimension,
            _ => throw ToolException.InvalidArgument($"Unknown kind '{kind}'.", $"Use one of: {string.Join(", ", Kinds)}."),
        };
        object list = Query(document, string.IsNullOrWhiteSpace(filter) ? "*" : filter, varType);
        int count = Late.Count(list);
        int start = Math.Max(offset, 0);
        int take = Math.Clamp(limit, 1, context.MaxListItems);
        var items = new List<object>();
        for (int i = start + 1; i <= count && items.Count < take; i++)
        {
            if (Late.Call(list, "Item", i) is { } item)
            {
                items.Add(Describe(context, document, item));
            }
        }

        return new { document = context.DocumentHandle(document), count, offset = start, returned = items.Count, more = start + items.Count < count, variables = items };
    }

    public static object Get(SeContext context, string? documentHandle, string name)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        return Describe(context, document, Find(document, name));
    }

    /// <summary>Cambia el valor (unidades de las herramientas) o la fórmula de una variable o cota.</summary>
    public static object Set(SeContext context, string? documentHandle, string name, double? value, string? formula)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        if (Late.TryGet(item, "IsReadOnly") is true)
        {
            throw new ToolException("read_only_variable", $"'{name}' is read-only (driven or external).", "Change the variable or dimension that drives it.");
        }

        double? volumeBefore = TryVolume(document);
        if (!string.IsNullOrWhiteSpace(formula))
        {
            Late.Set(item, "Formula", formula);
        }
        else
        {
            double v = Units.RequireFinite(value ?? throw ToolException.InvalidArgument("Action 'set' requires 'value' or 'formula'."), "value");
            Late.Set(item, "Value", ToDatabase(v, UnitsOf(item)));
        }

        context.ModelChanged(document, $"the variable '{name}' changed");
        double? volumeAfter = TryVolume(document);
        return new
        {
            variable = Describe(context, document, item),
            volume = volumeAfter is null ? (double?)null : Geo.Mm3(volumeAfter.Value),
            volumeChange = volumeAfter is null || volumeBefore is null ? (double?)null : Geo.Mm3(volumeAfter.Value - volumeBefore.Value),
        };
    }

    /// <summary>Crea una variable de usuario con un valor (unidades de las herramientas) o una fórmula.</summary>
    public static object Add(SeContext context, string? documentHandle, string name, double? value, string? formula, string units)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        int unitsType = units switch
        {
            "distance" => (int)UnitTypeConstants.igUnitDistance,
            "angle" => (int)UnitTypeConstants.igUnitAngle,
            "area" => (int)UnitTypeConstants.igUnitArea,
            "volume" => (int)UnitTypeConstants.igUnitVolume,
            "mass" => (int)UnitTypeConstants.igUnitMass,
            "scalar" => (int)UnitTypeConstants.igUnitScalar,
            _ => throw ToolException.InvalidArgument($"Unknown units '{units}'.", $"Use one of: {string.Join(", ", UnitNames)}."),
        };
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("Action 'add' requires 'name'.");
        }

        var variables = (Variables)document.Variables;
        object created;
        if (!string.IsNullOrWhiteSpace(formula))
        {
            created = variables.Add(name, formula, unitsType);
        }
        else
        {
            double database = ToDatabase(Units.RequireFinite(value ?? throw ToolException.InvalidArgument("Action 'add' requires 'value' or 'formula'."), "value"), unitsType);
            created = variables.Add(name, database.ToString("R", CultureInfo.InvariantCulture), unitsType);

            // El valor numérico se fija en unidades internas: no depende de cómo interprete la fórmula.
            Late.Set(created, "Value", database);
        }

        return Describe(context, document, created);
    }

    public static object Delete(SeContext context, string? documentHandle, string name)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        if (item is Dimension)
        {
            throw new ToolException("not_supported", $"'{name}' is a dimension; delete the dimension or its feature instead.");
        }

        Late.Call(item, "Delete");
        return new { deleted = name };
    }

    private static object Query(SolidEdgeDocument document, string criterion, int varType)
    {
        var variables = (Variables)document.Variables;
        return variables.Query(criterion, NamedByBoth, varType, true);
    }

    private static object Find(SolidEdgeDocument document, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ToolException.InvalidArgument("'name' is required.");
        }

        object list;
        try
        {
            list = Query(document, name, VarTypeBoth);
        }
        catch (COMException)
        {
            list = Query(document, "*", VarTypeBoth);
        }

        object? caseInsensitive = null;
        foreach (object item in Late.Items(list))
        {
            string? itemName = NameOf(item);
            if (string.Equals(itemName, name, StringComparison.Ordinal))
            {
                return item;
            }

            if (caseInsensitive is null && string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = item;
            }
        }

        return caseInsensitive ?? throw ToolException.NotFound($"There is no variable or dimension named '{name}'.",
            "List them with se_variables action 'list' (names are case-sensitive).");
    }

    private static string? NameOf(object item) =>
        Late.TryGetString(item, "DisplayName") is { Length: > 0 } display ? display : Late.TryGetString(item, "Name");

    private static object Describe(SeContext context, SolidEdgeDocument document, object item)
    {
        int units = UnitsOf(item);
        object? raw = Late.TryGet(item, "Value");
        double? value = raw is null ? null : FromDatabase(Convert.ToDouble(raw, CultureInfo.InvariantCulture), units);
        return new
        {
            name = NameOf(item),
            systemName = Late.TryGetString(item, "SystemName"),
            kind = item is Dimension ? "dimension" : "variable",
            value = value is null ? (double?)null : Math.Round(value.Value, 9),
            unit = UnitLabel(units),
            formula = Late.TryGetString(item, "Formula") is { Length: > 0 } f ? f : null,
            readOnly = Late.TryGet(item, "IsReadOnly") as bool?,
            handle = context.Register(item, item is Dimension ? "dimension" : "variable", document),
        };
    }

    /// <summary>Tipo de unidad (<see cref="UnitTypeConstants"/>): de la variable o, en cotas, según su tipo.</summary>
    private static int UnitsOf(object item)
    {
        if (item is Dimension dimension)
        {
            DimTypeConstants type;
            try
            {
                type = dimension.DimensionType;
            }
            catch (COMException)
            {
                return (int)UnitTypeConstants.igUnitDistance;
            }

            return type is DimTypeConstants.igDimTypeAngular or DimTypeConstants.igDimTypeArcAngle or DimTypeConstants.igDimTypeAngularCoordinate
                ? (int)UnitTypeConstants.igUnitAngle
                : (int)UnitTypeConstants.igUnitDistance;
        }

        object? units = Late.TryGet(item, "UnitsType");
        return units is null ? (int)UnitTypeConstants.igUnitScalar : Convert.ToInt32(units, CultureInfo.InvariantCulture);
    }

    private static double FromDatabase(double value, int units) => units switch
    {
        (int)UnitTypeConstants.igUnitDistance => Units.MToMm(value),
        (int)UnitTypeConstants.igUnitAngle => Units.RadToDeg(value),
        (int)UnitTypeConstants.igUnitArea => Units.M2ToMm2(value),
        (int)UnitTypeConstants.igUnitVolume => Units.M3ToMm3(value),
        _ => value,
    };

    private static double ToDatabase(double value, int units) => units switch
    {
        (int)UnitTypeConstants.igUnitDistance => Units.MmToM(value),
        (int)UnitTypeConstants.igUnitAngle => Units.DegToRad(value),
        (int)UnitTypeConstants.igUnitArea => Units.Mm2ToM2(value),
        (int)UnitTypeConstants.igUnitVolume => Units.Mm3ToM3(value),
        _ => value,
    };

    private static string UnitLabel(int units) => units switch
    {
        (int)UnitTypeConstants.igUnitDistance => "mm",
        (int)UnitTypeConstants.igUnitAngle => "deg",
        (int)UnitTypeConstants.igUnitArea => "mm2",
        (int)UnitTypeConstants.igUnitVolume => "mm3",
        (int)UnitTypeConstants.igUnitMass => "kg",
        (int)UnitTypeConstants.igUnitDensity => "kg/m3",
        (int)UnitTypeConstants.igUnitScalar => "",
        _ => Enum.IsDefined(typeof(UnitTypeConstants), units)
            ? $"SI ({((UnitTypeConstants)units).ToString().Replace("igUnit", "", StringComparison.Ordinal)})"
            : "SI",
    };

    private static double? TryVolume(SolidEdgeDocument document) =>
        document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument ? ModelDocument.From(document).Volume() : null;
}
