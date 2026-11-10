// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Variable table (user variables and dimensions). Values are given in the units of the
/// tools (mm, degrees, mm², mm³); internally Solid Edge uses metres and radians.
/// </summary>
public static partial class VariableService
{
    // Values of SolidEdgeConstants.VariableNameBy and VariableVarType (the API receives them as VARIANT).
    private const int NamedByBoth = 2;
    private const int VarTypeDimension = 1;
    private const int VarTypeVariable = 2;
    private const int VarTypeBoth = 3;

    // Bits of a variable's range condition (SetValueRangeValues), measured in Solid Edge 2026.
    private const int RangeMinExclusive = 1;
    private const int RangeMinInclusive = 2;
    private const int RangeMaxExclusive = 4;
    private const int RangeMaxInclusive = 8;

    public static object List(SeContext context, string? documentHandle, string? filter, VariableKind kind, int offset, int limit)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        int varType = kind switch
        {
            VariableKind.All => VarTypeBoth,
            VariableKind.User => VarTypeVariable,
            VariableKind.Dimension => VarTypeDimension,
            VariableKind.Physical => VarTypePhysical,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (varType == VarTypePhysical)
        {
            RefreshPhysical(context, document);
        }

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

        string? note = varType == VarTypePhysical && count == 0
            ? "The physical property variables appear once the part has a density: apply a material (se_material). se_physical measures the model without them."
            : null;
        return new { document = context.DocumentHandle(document), count, offset = start, returned = items.Count, more = start + items.Count < count, variables = items, note };
    }

    public static object Get(SeContext context, string? documentHandle, string name)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (IsPhysical(document, name))
        {
            RefreshPhysical(context, document);
        }

        return Describe(context, document, Find(document, name));
    }

    /// <summary>Changes the value (tool units) or the formula of a variable or dimension.</summary>
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
            int units = UnitsOf(item);
            double target = ToDatabase(v, units);
            Late.Set(item, "Value", target);

            // Outside its limits (range or list of values) Solid Edge keeps the previous value without raising an error.
            double now = Convert.ToDouble(Late.Get(item, "Value"), CultureInfo.InvariantCulture);
            if (Math.Abs(now - target) > 1e-9 * Math.Max(1, Math.Abs(target)))
            {
                throw new ToolException("value_rejected",
                    $"Solid Edge kept '{name}' at {Math.Round(FromDatabase(now, units), 9)} {UnitLabel(units)} instead of {v} {UnitLabel(units)}.",
                    LimitsOf(item, units) is { } limits
                        ? $"The value must respect the variable's limits ({JsonText(limits)}); change them with action 'limits'."
                        : "The value may not produce a valid model; try another one.");
            }
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

    /// <summary>Creates a user variable with a value (tool units) or a formula.</summary>
    public static object Add(SeContext context, string? documentHandle, string name, double? value, string? formula, VariableUnits units)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        int unitsType = units switch
        {
            VariableUnits.Distance => (int)UnitTypeConstants.igUnitDistance,
            VariableUnits.Angle => (int)UnitTypeConstants.igUnitAngle,
            VariableUnits.Area => (int)UnitTypeConstants.igUnitArea,
            VariableUnits.Volume => (int)UnitTypeConstants.igUnitVolume,
            VariableUnits.Mass => (int)UnitTypeConstants.igUnitMass,
            VariableUnits.Scalar => (int)UnitTypeConstants.igUnitScalar,
            _ => throw new ArgumentOutOfRangeException(nameof(units)),
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

            // The numeric value is set in internal units: it does not depend on how the formula is interpreted.
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

    /// <summary>Renames a variable or dimension; Solid Edge updates the formulas that use it.</summary>
    public static object Rename(SeContext context, string? documentHandle, string name, string newName)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw ToolException.InvalidArgument("Action 'rename' requires 'newName'.");
        }

        try
        {
            ((Variables)document.Variables).PutName(item, newName);
        }
        catch (COMException ex)
        {
            throw new ToolException("variable_failed", $"Solid Edge rejected the name '{newName}': {ex.Message}",
                "Use a unique name that starts with a letter and has no spaces or operators.", ex);
        }

        if (!string.Equals(NameOf(item), newName, StringComparison.Ordinal))
        {
            throw new ToolException("variable_failed", $"Solid Edge kept the name '{NameOf(item)}'.", "Use a unique name that starts with a letter.");
        }

        return Describe(context, document, item);
    }

    /// <summary>Comment of the variable (the "Comment" column of the variable table).</summary>
    public static object Comment(SeContext context, string? documentHandle, string name, string comment)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        try
        {
            Late.Call(item, "SetComment", comment);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw new ToolException("not_supported", $"'{name}' does not accept a comment: {ComErrors.Unwrap(ex).Message}", null, ex);
        }

        return Describe(context, document, item);
    }

    /// <summary>Publishes the variable under a name to use it from assemblies or part families (Expose).</summary>
    public static object Expose(SeContext context, string? documentHandle, string name, bool expose, string? exposeName)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        try
        {
            Late.Set(item, "Expose", expose ? 1 : 0);
            if (expose && !string.IsNullOrWhiteSpace(exposeName))
            {
                Late.Set(item, "ExposeName", exposeName);
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw new ToolException("not_supported", $"'{name}' cannot be exposed: {ComErrors.Unwrap(ex).Message}", null, ex);
        }

        return Describe(context, document, item);
    }

    /// <summary>
    /// Limits of the variable: range (min and max) or list of allowed values; with neither, they are removed. Range conditions
    /// are a bit mask (RangeMin*/RangeMax*); a range may have only min or only max.
    /// </summary>
    public static object Limits(SeContext context, string? documentHandle, string name, double? min, double? max, double[]? values,
        bool minExclusive, bool maxExclusive)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        object item = Find(document, name);
        int units = UnitsOf(item);
        try
        {
            if (values is { Length: > 0 })
            {
                if (min is not null || max is not null)
                {
                    throw ToolException.InvalidArgument("Give either 'values' (allowed values) or 'min' and 'max' (range), not both.");
                }

                Array list = values.Select(v => ToDatabase(Units.RequireFinite(v, "values"), units)).ToArray();
                Late.CallByRef(item, "SetDiscreteValues", [list], 0);
            }
            else if (min is not null || max is not null)
            {
                if (min is not null && max is not null && min >= max)
                {
                    throw ToolException.InvalidArgument("The range needs min < max.");
                }

                int condition = (min is null ? 0 : minExclusive ? RangeMinExclusive : RangeMinInclusive)
                    | (max is null ? 0 : maxExclusive ? RangeMaxExclusive : RangeMaxInclusive);
                Late.Call(item, "SetValueRangeValues", ToDatabase(min ?? 0, units), condition, ToDatabase(max ?? 0, units));
            }
            else
            {
                Late.Call(item, "ClearLimitsOrDiscreteValues");
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            throw new ToolException("not_supported", $"Solid Edge did not accept limits on '{name}': {ComErrors.Unwrap(ex).Message}",
                "Limits apply to user variables and driving dimensions.", ex);
        }

        bool wanted = values is { Length: > 0 } || min is not null || max is not null;
        if ((LimitsOf(item, units) is not null) != wanted)
        {
            throw new ToolException("variable_failed", $"Solid Edge did not {(wanted ? "set" : "clear")} the limits of '{name}'.");
        }

        return Describe(context, document, item);
    }

    /// <summary>
    /// Goal Seek: Solid Edge changes one variable until another reaches a value. Verified in Solid Edge 2026 on a part:
    /// GA 10 → 25 so that GB = GA*2 = 50 (44 iterations); a distance GD → 50 mm so that an area GE = GD² = 2500 mm². An
    /// unknown name fails with E_POINTER and an unreachable value with E_FAIL. Mass, Volume and Surface_Area of a part are
    /// searched by the server (<see cref="MeasuredGoalSeek"/>).
    /// </summary>
    public static object GoalSeek(SeContext context, string? documentHandle, string name, string change, double? value, double? timeLimit,
        int? iterations)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        string? physical = PhysicalTarget(document, name);
        object? target = physical is null ? Find(document, name) : null;
        object changed = Find(document, change);
        if (Late.TryGet(changed, "IsReadOnly") is true)
        {
            throw new ToolException("read_only_variable", $"'{change}' is read-only (driven or external), so Goal Seek cannot change it.",
                "Name the variable or dimension that drives it in 'change'.");
        }

        double wanted = Units.RequireFinite(value ?? throw ToolException.InvalidArgument("goal_seek needs 'value': the value 'name' must reach."), "value");
        double seconds = Units.RequirePositive(timeLimit ?? 10, "timeLimit");
        int limit = iterations ?? 100;
        if (limit < 1)
        {
            throw ToolException.InvalidArgument("'iterations' must be at least 1.");
        }

        if (target is null)
        {
            return MeasuredGoalSeek(context, document, physical!, changed, wanted, seconds, limit);
        }

        int units = UnitsOf(target);
        double goal = ToDatabase(wanted, units);

        string targetName = NameOf(target)!;
        string changeName = NameOf(changed)!;
        double start = Convert.ToDouble(Late.Get(changed, "Value"), CultureInfo.InvariantCulture);
        double? volumeBefore = TryVolume(document);
        double elapsed;
        int steps;
        bool timeOut;
        bool iterationsOut;
        try
        {
            switch (document)
            {
                case SolidEdgePart.PartDocument part:
                    part.GoalSeek(targetName, changeName, goal, seconds, limit, out elapsed, out steps, out timeOut, out iterationsOut);
                    break;
                case SolidEdgePart.SheetMetalDocument sheetMetal:
                    sheetMetal.GoalSeek(targetName, changeName, goal, seconds, limit, out elapsed, out steps, out timeOut, out iterationsOut);
                    break;
                case SolidEdgeAssembly.AssemblyDocument assembly:
                    assembly.GoalSeek(targetName, changeName, goal, seconds, limit, out elapsed, out steps, out timeOut, out iterationsOut);
                    break;
                case SolidEdgeDraft.DraftDocument draft:
                    draft.GoalSeek(targetName, changeName, goal, seconds, limit, out elapsed, out steps, out timeOut, out iterationsOut);
                    break;
                default:
                    throw new ToolException("not_supported", "Goal Seek works in part, sheet metal, assembly and drawing documents.");
            }
        }
        catch (Exception ex) when (ex is COMException or NullReferenceException && !ComErrors.IsGone(ex))
        {
            PutBack(changed, start);
            throw new ToolException("goal_not_reached", $"Solid Edge could not bring '{targetName}' to the value by changing '{changeName}': {ComErrors.Unwrap(ex).Message.Trim()}",
                "'name' must depend on 'change' through formulas or the model, and the value must be reachable.", ex);
        }

        double reached = Convert.ToDouble(Late.Get(target, "Value"), CultureInfo.InvariantCulture);
        if (Math.Abs(reached - goal) > 1e-6 * Math.Max(1, Math.Abs(goal)))
        {
            PutBack(changed, start);
            throw new ToolException("goal_not_reached",
                $"Goal Seek stopped with '{targetName}' at {Math.Round(FromDatabase(reached, units), 9)} {UnitLabel(units)} after {steps} iterations "
                + $"({(timeOut ? "time limit reached" : iterationsOut ? "iteration limit reached" : "no closer value found")}); '{changeName}' was put back.",
                "Raise timeLimit or iterations, or check that the value can be reached.");
        }

        context.ModelChanged(document, $"Goal Seek changed '{changeName}'");
        double? volumeAfter = TryVolume(document);
        return new
        {
            target = Describe(context, document, target),
            changed = Describe(context, document, changed),
            iterations = steps,
            seconds = Math.Round(elapsed, 3),
            volumeChange = volumeAfter is null || volumeBefore is null ? (double?)null : Geo.Mm3(volumeAfter.Value - volumeBefore.Value),
        };
    }

    private static void PutBack(object variable, double value)
    {
        try
        {
            Late.Set(variable, "Value", value);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The variable keeps what Goal Seek left; the error reported names it.
        }
    }

    /// <summary>
    /// Adjustable part: the variables an assembly may adjust in each occurrence (Adjustable Part command). Verified in Solid
    /// Edge 2026: AdjustableDefinition.SetAdjustables takes the variable objects (names fail) and makes IsAdjustablePart true;
    /// GetAdjustables returns them with their notes; RemoveAllAdjustables makes the part ordinary again.
    /// </summary>
    public static object Adjustable(SeContext context, string? documentHandle, string[]? names, string[]? notes)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        SolidEdgePart.AdjustableDefinition definition = document switch
        {
            SolidEdgePart.PartDocument part => part.AdjustableDefinition,
            SolidEdgePart.SheetMetalDocument sheetMetal => sheetMetal.AdjustableDefinition,
            _ => throw new ToolException("not_supported", "Adjustable variables belong to part and sheet metal documents.",
                "In an assembly, place the part with se_assembly insert adjustable=true or make an occurrence adjustable."),
        };

        if (names is not null)
        {
            if (names.Length == 0)
            {
                definition.RemoveAllAdjustables();
            }
            else
            {
                if (notes is not null && notes.Length != names.Length)
                {
                    throw ToolException.InvalidArgument($"'notes' has {notes.Length} entries for {names.Length} names.", "Give one note per name, or omit notes.");
                }

                Array variables = names.Select(n => Find(document, n)).ToArray();
                Array texts = names.Select((n, i) => notes?[i] ?? "").ToArray<object>();
                definition.SetAdjustables(variables.Length, ref variables, ref texts);
            }

            context.ModelChanged(document, "the adjustable variables changed");
        }

        var listed = AdjustablesOf(definition);
        bool adjustable = document is SolidEdgePart.PartDocument p ? p.IsAdjustablePart : ((SolidEdgePart.SheetMetalDocument)document).IsAdjustablePart;
        if (names is { Length: > 0 } && (!adjustable || listed.Count != names.Length))
        {
            throw new ToolException("setting_rejected", $"Solid Edge kept {listed.Count} adjustable variable(s) instead of {names.Length}.",
                "Use variables or dimensions of this document (se_variables list).");
        }

        return new
        {
            document = context.DocumentHandle(document),
            adjustable,
            variables = listed.Select(a => new { name = NameOf(a.Variable), note = a.Note }).ToArray(),
        };
    }

    private static List<(object Variable, string Note)> AdjustablesOf(SolidEdgePart.AdjustableDefinition definition)
    {
        Array variables = new object[1];
        Array notes = new object[1];
        definition.GetAdjustables(out int count, ref variables, ref notes);
        if (count > 1 && variables.Length < count)
        {
            variables = new object[count];
            notes = new object[count];
            definition.GetAdjustables(out count, ref variables, ref notes);
        }

        var result = new List<(object, string)>();
        for (int i = 0; i < Math.Min(count, variables.Length); i++)
        {
            if (variables.GetValue(i) is { } variable)
            {
                result.Add((variable, notes.GetValue(i)?.ToString() ?? ""));
            }
        }

        return result;
    }

    private static object Query(SolidEdgeDocument document, string criterion, int varType)
    {
        var variables = (Variables)document.Variables;
        return variables.Query(criterion, NamedByBoth, varType, true);
    }

    /// <summary>Whether a variable or dimension of the document has the name (ignoring case); true when Solid Edge cannot tell.</summary>
    internal static bool InUse(SolidEdgeDocument document, string name)
    {
        try
        {
            return Late.Items(Query(document, name, VarTypeBoth)).Any(item => string.Equals(NameOf(item), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return true;
        }
    }

    internal static object Find(SolidEdgeDocument document, string name)
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

    internal static string? NameOf(object item) =>
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
            comment = TryCall(item, "GetComment") as string is { Length: > 0 } c ? c : null,
            exposedAs = Late.TryGet(item, "Expose") is int e && e != 0 ? Late.TryGetString(item, "ExposeName") ?? NameOf(item) : null,
            limits = LimitsOf(item, units),
            handle = context.Register(item, item is Dimension ? "dimension" : "variable", document),
        };
    }

    /// <summary>Limits of the variable (range or list of values) in tool units, or <c>null</c>.</summary>
    private static object? LimitsOf(object item, int units)
    {
        object?[] has;
        try
        {
            has = Late.CallByRef(item, "HasVariableLimit", [false, 0], 0, 1);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }

        if (has[0] is not true)
        {
            return null;
        }

        int kind = Convert.ToInt32(has[1], CultureInfo.InvariantCulture);
        if (kind == (int)VariableLimitValueConstant.igMinMaxLimit)
        {
            object?[] range = Late.CallByRef(item, "GetValueRangeValues", [0.0, 0, 0.0], 0, 1, 2);
            int condition = Convert.ToInt32(range[1], CultureInfo.InvariantCulture);
            bool hasMin = (condition & (RangeMinExclusive | RangeMinInclusive)) != 0;
            bool hasMax = (condition & (RangeMaxExclusive | RangeMaxInclusive)) != 0;
            return new
            {
                type = "range",
                min = hasMin ? Math.Round(FromDatabase(Convert.ToDouble(range[0], CultureInfo.InvariantCulture), units), 9) : (double?)null,
                max = hasMax ? Math.Round(FromDatabase(Convert.ToDouble(range[2], CultureInfo.InvariantCulture), units), 9) : (double?)null,
                minExclusive = (condition & RangeMinInclusive) == 0 && (condition & RangeMinExclusive) != 0 ? true : (bool?)null,
                maxExclusive = (condition & RangeMaxInclusive) == 0 && (condition & RangeMaxExclusive) != 0 ? true : (bool?)null,
                unit = UnitLabel(units),
            };
        }

        double[]? values = null;
        try
        {
            // Through IDispatch the output parameter must arrive as a double array (with null it gives DISP_E_TYPEMISMATCH).
            if (Late.CallByRef(item, "GetDiscreteValues", [new double[] { 0.0 }], 0)[0] is Array list)
            {
                values = list.Cast<object>().Select(v => Math.Round(FromDatabase(Convert.ToDouble(v, CultureInfo.InvariantCulture), units), 9)).ToArray();
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Without a readable list: only the limit type is reported.
        }

        return new { type = "values", values, unit = UnitLabel(units) };
    }

    private static object? TryCall(object target, string method)
    {
        try
        {
            return Late.Call(target, method);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            return null;
        }
    }

    private static string JsonText(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Unit type (<see cref="UnitTypeConstants"/>): of the variable or, for dimensions, according to their type.</summary>
    internal static int UnitsOf(object item)
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

    internal static double FromDatabase(double value, int units) => units switch
    {
        (int)UnitTypeConstants.igUnitDistance => Units.MToMm(value),
        (int)UnitTypeConstants.igUnitAngle => Units.RadToDeg(value),
        (int)UnitTypeConstants.igUnitArea => Units.M2ToMm2(value),
        (int)UnitTypeConstants.igUnitVolume => Units.M3ToMm3(value),
        _ => value,
    };

    internal static double ToDatabase(double value, int units) => units switch
    {
        (int)UnitTypeConstants.igUnitDistance => Units.MmToM(value),
        (int)UnitTypeConstants.igUnitAngle => Units.DegToRad(value),
        (int)UnitTypeConstants.igUnitArea => Units.Mm2ToM2(value),
        (int)UnitTypeConstants.igUnitVolume => Units.Mm3ToM3(value),
        _ => value,
    };

    internal static string UnitLabel(int units) => units switch
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
