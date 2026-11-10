// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>What se_sensor does: the action and its inputs, in the units of the tools (mm, mm², degrees).</summary>
public sealed record SensorSpec(SensorAction Action, string? Sensor = null, string? Name = null, string? Description = null, string? Variable = null,
    string[]? Elements = null, string[]? Faces = null, SensorOperator? Operator = null, double? Threshold = null, double[]? Range = null,
    double[]? DisplayRange = null, SensorDisplay? Display = null, SensorUpdate? Update = null, string[]? Features = null);

/// <summary>
/// Sensors of a part, sheet metal or assembly document: they watch a variable, the minimum distance between two elements or
/// the area of faces, and flag when the value leaves the condition set on them. Verified in Solid Edge 2026:
/// <list type="bullet">
/// <item>Sensors.AddVariableSensor and AddMinimumDistanceSensor (faces of a part; occurrences or faces of occurrences in an
/// assembly) work; CurrentValue is in metres (or the database units of the variable).</item>
/// <item>IsInRange is the alarm: it is true when the value does not meet the operator and its threshold (a value of 30 mm
/// with "between 20 and 50" reads false; 60 mm reads true). The operators with one threshold use MinimumThreshold.</item>
/// <item>Automatic sensors follow the model (a moved occurrence: 30 mm to 10 mm); manual ones read OutOfDate until
/// Update.</item>
/// <item>Sensor.Delete breaks the sensors of the document: every later call on Sensors fails with 0x80040225, also after
/// saving and opening the file again. So sensors are not deleted here.</item>
/// <item>A sensor with an empty description makes the document fail to save (SaveAs: DISP_E_EXCEPTION), so the
/// description defaults to the name.</item>
/// <item>AddSurfaceAreaSensor takes one face (a list: type mismatch), makes the sensor and then fails with DISP_E_BADCALLEE
/// handing the face back; its SensorType is 4, which SensorTypeConstants lacks.</item>
/// <item>Sheet metal parts have SheetMetalSensors (Item typed as object): variable and area sensors work there, but
/// AddMinimumDistanceSensor fails with E_INVALIDARG (faces or edges) after leaving an empty sensor that makes SaveAs fail, so
/// it is refused before the call.</item>
/// <item>A sensor does not tell which variable it watches, so the variable and its units are kept in an attribute set of
/// the sensor; sensors made elsewhere report a variable value in database units.</item>
/// </list>
/// </summary>
public static class SensorService
{

    public static readonly IReadOnlyList<string> CheckerFeatures =
        ["exterior_edges", "interior_edges", "cutouts", "holes", "dimples", "louvers", "drawn_cutouts", "beads"];
    public static readonly IReadOnlyList<string> Operators = ["greater_than", "less_than", "equal_to", "not_equal_to", "between", "not_between"];

    private const string AttributeSetName = "SolidEdgeMcp";
    private const string UnitsAttribute = "Units";
    private const string VariableAttribute = "Variable";

    /// <summary>HRESULT of every call on Sensors once a sensor of the document was deleted through the API.</summary>
    private const int BrokenSensors = unchecked((int)0x80040225);

    /// <summary>DISP_E_BADCALLEE, which AddSurfaceAreaSensor returns after making the sensor.</summary>
    private const int BadCallee = unchecked((int)0x80020010);

    /// <summary>SensorType of a surface area sensor (missing from SensorTypeConstants).</summary>
    private const int SurfaceAreaType = 4;

    /// <summary>SensorType of a sheet metal checker sensor (missing from SensorTypeConstants).</summary>
    private const int CheckerType = 8;

    public static object Run(SeContext context, string? documentHandle, SensorSpec spec) => spec.Action switch
    {
        SensorAction.List => List(context, Document(context, documentHandle, spec)),
        SensorAction.AddVariable => AddVariable(context, Document(context, documentHandle, spec), spec),
        SensorAction.AddDistance => AddDistance(context, Document(context, documentHandle, spec), spec),
        SensorAction.AddArea => AddArea(context, Document(context, documentHandle, spec), spec),
        SensorAction.AddChecker => AddChecker(context, Document(context, documentHandle, spec), spec),
        SensorAction.Update => Update(context, Document(context, documentHandle, spec), spec),
        SensorAction.Set => Set(context, Document(context, documentHandle, spec), spec),
        _ => throw new ArgumentOutOfRangeException(nameof(spec)),
    };

    private static SolidEdgeDocument Document(SeContext context, string? documentHandle, SensorSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(documentHandle))
        {
            return context.GetDocument(documentHandle);
        }

        string? related = spec.Elements?.FirstOrDefault() ?? spec.Faces?.FirstOrDefault();
        if (related is null && spec.Sensor is { } key && key.Contains(':', StringComparison.Ordinal))
        {
            related = key;
        }

        return related is null ? context.GetDocument(null) : context.DocumentOf(related);
    }

    /// <summary>
    /// The sensors of a part or an assembly (Sensors) or of a sheet metal part (SheetMetalSensors, a separate interface with the
    /// same methods and Item typed as object).
    /// </summary>
    private sealed class SensorSet(Sensors? sensors, SheetMetalSensors? sheetMetal)
    {
        public bool IsSheetMetal => sheetMetal is not null;

        public int Count => sensors?.Count ?? sheetMetal!.Count;

        public object Item(int index) => sensors is not null ? sensors.Item(index) : sheetMetal!.Item(index);

        public object AddVariableSensor(object variable, string name, string description, double lower, double upper, double minimum, double maximum,
            SensorOperatorConstants op, SensorDisplayTypeConstants display, SensorUpdateMechanismConstants update) => sensors is not null
            ? sensors.AddVariableSensor(variable, name, description, lower, upper, minimum, maximum, op, display, update)
            : sheetMetal!.AddVariableSensor(variable, name, description, lower, upper, minimum, maximum, op, display, update);

        public object AddMinimumDistanceSensor(object first, object second, string name, string description, double lower, double upper, double minimum,
            double maximum, SensorOperatorConstants op, SensorDisplayTypeConstants display, SensorUpdateMechanismConstants update) => sensors is not null
            ? sensors.AddMinimumDistanceSensor(first, second, name, description, lower, upper, minimum, maximum, op, display, update)
            : sheetMetal!.AddMinimumDistanceSensor(first, second, name, description, lower, upper, minimum, maximum, op, display, update);

        public object AddSurfaceAreaSensor(SurfaceAreaSensorAreaTypeConstants areaType, SurfaceAreaSensorSelectionTypeConstants selection, string name,
            string description, double lower, double upper, double minimum, double maximum, SensorOperatorConstants op, SensorDisplayTypeConstants display,
            SensorUpdateMechanismConstants update, ref object element) => sensors is not null
            ? sensors.AddSurfaceAreaSensor(areaType, selection, name, description, lower, upper, minimum, maximum, op, display, update, ref element)
            : sheetMetal!.AddSurfaceAreaSensor(areaType, selection, name, description, lower, upper, minimum, maximum, op, display, update, ref element);

        public object AddCheckerSensor(SheetMetalSensorFeatureTypeConstants left, SheetMetalSensorFeatureTypeConstants right, string name, string description,
            double threshold, SensorUpdateMechanismConstants update, object on)
        {
            object element = on;
            return sheetMetal!.AddSheetMetalCheckerSensor(left, right, name, description, threshold, update, ref element);
        }
    }

    private static SensorSet SensorsOf(SolidEdgeDocument document)
    {
        object? sensors;
        try
        {
            sensors = Late.Get(document, "Sensors");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            sensors = null;
        }

        return sensors switch
        {
            Sensors part => new SensorSet(part, null),
            SheetMetalSensors sheetMetal => new SensorSet(null, sheetMetal),
            _ => throw new ToolException("wrong_document_type",
            $"'{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}: sensors belong to part, sheet metal and assembly documents.",
            "Activate or pass a part, sheet metal or assembly document."),
        };
    }

    /// <summary>The sensors of the document, read once (Count fails if a sensor was ever deleted through the API).</summary>
    private static List<Sensor> Read(SolidEdgeDocument document)
    {
        SensorSet sensors = SensorsOf(document);
        try
        {
            int count = sensors.Count;
            var list = new List<Sensor>(count);
            for (int i = 1; i <= count; i++)
            {
                list.Add((Sensor)sensors.Item(i));
            }

            return list;
        }
        catch (COMException ex) when (ex.HResult == BrokenSensors)
        {
            throw Broken(document, ex);
        }
    }

    private static ToolException Broken(SolidEdgeDocument document, Exception ex) => new("sensors_unavailable",
        $"Solid Edge cannot read the sensors of '{SeContext.SafeName(document)}' (0x80040225). This happens once a sensor of the document was deleted through the API, and it stays in the saved file.",
        "Sensors of this document cannot be used through the API any more: add them in a copy saved before the deletion or in another document.", ex);

    private static object List(SeContext context, SolidEdgeDocument document)
    {
        List<Sensor> sensors = Read(document);
        return new
        {
            document = context.DocumentHandle(document),
            count = sensors.Count,
            sensors = sensors.Select((s, i) => Describe(context, document, s, i + 1)).ToArray(),
        };
    }

    private static object AddVariable(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Variable))
        {
            throw ToolException.InvalidArgument("add_variable requires 'variable': the name of a variable or dimension (se_variables list).");
        }

        object variable = VariableService.Find(document, spec.Variable);
        string variableName = VariableService.NameOf(variable) ?? spec.Variable;
        int units = VariableService.UnitsOf(variable);
        Condition condition = NewCondition(spec, units);
        (double lower, double upper) = DisplayRange(spec, units, condition);
        SensorSet sensors = SensorsOf(document);
        string name = NameFor(document, spec.Name, variableName);
        object created = Add(document, "variable", () => sensors.AddVariableSensor(variable, name, DescriptionOf(spec, name), lower, upper,
            condition.Minimum, condition.Maximum, condition.Operator, DisplayOf(spec.Display), UpdateOf(spec.Update)));
        return Created(context, document, (Sensor)created, units, variableName);
    }

    private static object AddDistance(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        if (spec.Elements is not { Length: 2 })
        {
            throw ToolException.InvalidArgument("add_distance requires 'elements': two handles (faces, edges or vertices; in an assembly also occurrences).",
                "Get faces with se_geometry faces (in an assembly with source = an occurrence) and occurrences with se_assembly list.");
        }

        if (SensorsOf(document).IsSheetMetal)
        {
            throw new ToolException("not_supported",
                $"Solid Edge 2026 does not make minimum distance sensors in a sheet metal part ('{SeContext.SafeName(document)}'): the call fails and leaves an empty sensor that stops the document from saving, and sensors cannot be deleted.",
                "Watch the distance with a variable sensor (se_variables add a formula or a dimension, then add_variable), or in an assembly that holds the part.");
        }

        object first = context.Handles.Resolve(spec.Elements[0], "elements");
        object second = context.Handles.Resolve(spec.Elements[1], "elements");
        int units = (int)UnitTypeConstants.igUnitDistance;
        Condition condition = NewCondition(spec, units);
        (double lower, double upper) = DisplayRange(spec, units, condition);
        SensorSet sensors = SensorsOf(document);
        string name = NameFor(document, spec.Name, "Distance");
        object created = Add(document, "minimum distance", () => sensors.AddMinimumDistanceSensor(first, second, name, DescriptionOf(spec, name), lower, upper,
            condition.Minimum, condition.Maximum, condition.Operator, DisplayOf(spec.Display), UpdateOf(spec.Update)));
        return Created(context, document, (Sensor)created, units, null);
    }

    private static object AddArea(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        if (spec.Faces is not { Length: 1 })
        {
            throw ToolException.InvalidArgument("add_area requires 'faces': one face whose area is watched (se_geometry faces).",
                "Make one sensor per face: Solid Edge refuses a list of faces (type mismatch).");
        }

        object element = context.Handles.Resolve(spec.Faces[0], "faces");
        int units = (int)UnitTypeConstants.igUnitArea;
        Condition condition = NewCondition(spec, units);
        (double lower, double upper) = DisplayRange(spec, units, condition);
        SensorSet sensors = SensorsOf(document);
        string name = NameFor(document, spec.Name, "Area");
        object created = Add(document, "surface area", () =>
        {
            try
            {
                return sensors.AddSurfaceAreaSensor(SurfaceAreaSensorAreaTypeConstants.seSurfaceAreaSensorAreaTypePos,
                    SurfaceAreaSensorSelectionTypeConstants.seSurfaceAreaSensorSelectFace, name, DescriptionOf(spec, name), lower, upper,
                    condition.Minimum, condition.Maximum, condition.Operator, DisplayOf(spec.Display), UpdateOf(spec.Update), ref element);
            }
            catch (COMException ex) when (ex.HResult == BadCallee)
            {
                // Verified: Solid Edge makes the sensor and then fails handing the face back (DISP_E_BADCALLEE).
                return Read(document).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                    ?? throw new ToolException("feature_failed", $"Solid Edge did not make the surface area sensor: {ex.Message}", "Pass one face of this document.", ex);
            }
        });
        return Created(context, document, (Sensor)created, units, null);
    }

    /// <summary>
    /// A sheet metal checker sensor: the smallest distance between two kinds of features (edges, cutouts, holes, dimples,
    /// louvers, beads) against a threshold in mm.
    /// </summary>
    private static object AddChecker(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        if (spec.Features is not [string left, string right])
        {
            throw ToolException.InvalidArgument("add_checker requires 'features': the two kinds of features to keep apart.", $"Use two of: {string.Join(", ", CheckerFeatures)}.");
        }

        SensorSet sensors = SensorsOf(document);
        if (!sensors.IsSheetMetal)
        {
            throw new ToolException("not_supported", $"Checker sensors belong to sheet metal parts; '{SeContext.SafeName(document)}' is a {DocumentKinds.Describe(document)}.",
                "Use add_distance between faces or edges.");
        }

        if (spec.Faces is not [string face])
        {
            throw ToolException.InvalidArgument("add_checker requires 'faces': one face of the sheet (se_geometry faces); without it Solid Edge leaves the sensor in error.");
        }

        object element = context.Handles.Resolve(face, "faces");
        int first = Feature(left);
        int second = Feature(right);
        double threshold = Units.MmToM(Units.RequirePositive(spec.Threshold ?? throw ToolException.InvalidArgument("add_checker requires 'threshold': the smallest distance allowed, in mm."), "threshold"));
        string name = NameFor(document, spec.Name, "Checker");
        object created = Add(document, "sheet metal checker", () =>
        {
            try
            {
                return sensors.AddCheckerSensor((SheetMetalSensorFeatureTypeConstants)first, (SheetMetalSensorFeatureTypeConstants)second, name,
                    DescriptionOf(spec, name), threshold, UpdateOf(spec.Update), element);
            }
            catch (COMException ex) when (ex.HResult == BadCallee)
            {
                // As with area sensors, Solid Edge makes the sensor and then fails handing the face back.
                return Read(document).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
                    ?? throw new ToolException("feature_failed", $"Solid Edge did not make the checker sensor: {ex.Message}", "Pass one face of the sheet.", ex);
            }
        });
        return Created(context, document, (Sensor)created, (int)UnitTypeConstants.igUnitDistance, null);
    }

    private static int Feature(string feature)
    {
        int index = CheckerFeatures.ToList().IndexOf(feature.Trim().ToLowerInvariant());
        return index >= 0 ? index : throw ToolException.InvalidArgument($"Unknown feature kind '{feature}'.", $"Use one of: {string.Join(", ", CheckerFeatures)}.");
    }

    private static object Add(SolidEdgeDocument document, string what, Func<object> add)
    {
        try
        {
            return add() ?? throw new ToolException("feature_failed", $"Solid Edge did not make the {what} sensor.");
        }
        catch (COMException ex) when (ex.HResult == BrokenSensors)
        {
            throw Broken(document, ex);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            throw new ToolException("feature_failed", $"Solid Edge did not make the {what} sensor: {ex.Message}",
                "Check the elements (faces, edges, occurrences of this document) and the threshold.", ex);
        }
    }

    /// <summary>Marks the sensor with the units of its value, reads it back and widens the display range to show the value.</summary>
    private static object Created(SeContext context, SolidEdgeDocument document, Sensor sensor, int units, string? variable)
    {
        Remember(sensor, units, variable);
        sensor.Update();
        double value = sensor.CurrentValue;
        if (value > sensor.UpperRange)
        {
            sensor.UpperRange = value * 1.5;
        }

        var warnings = new List<string>();
        if (sensor.Status == SensorStatusConstants.seSensorStatusInError)
        {
            warnings.Add("Solid Edge reports the sensor in error. It was kept, because deleting sensors through the API breaks the sensors of the document: fix it with se_sensor set.");
        }

        int index = Read(document).FindIndex(s => Late.SameObject(s, sensor)) + 1;
        return new { sensor = Describe(context, document, sensor, index), warnings = warnings.Count > 0 ? warnings : null };
    }

    private static object Update(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        List<Sensor> sensors = Read(document);
        IEnumerable<(Sensor Sensor, int Index)> targets = string.IsNullOrWhiteSpace(spec.Sensor)
            ? sensors.Select((s, i) => (s, i + 1))
            : [Find(context, sensors, spec.Sensor)];
        var result = new List<object>();
        foreach ((Sensor sensor, int index) in targets)
        {
            sensor.Update();
            result.Add(Describe(context, document, sensor, index));
        }

        return new { document = context.DocumentHandle(document), count = result.Count, sensors = result };
    }

    private static object Set(SeContext context, SolidEdgeDocument document, SensorSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Sensor))
        {
            throw ToolException.InvalidArgument("set requires 'sensor': its name, index or handle (se_sensor list).");
        }

        (Sensor sensor, int index) = Find(context, Read(document), spec.Sensor);
        int? units = UnitsOf(sensor);
        bool changed = false;
        if (spec.Operator is not null || spec.Threshold is not null || spec.Range is not null)
        {
            if (units is null)
            {
                throw ToolException.InvalidArgument($"The units of sensor '{sensor.Name}' are not known (it was made outside this server), so its thresholds cannot be set in mm.",
                    "Change its thresholds in Solid Edge (Sensors tab of PathFinder).");
            }

            Condition condition = ChangedCondition(sensor, spec, units.Value);
            sensor.Operator = condition.Operator;
            sensor.MinimumThreshold = condition.Minimum;
            sensor.MaximumThreshold = condition.Maximum;
            changed = true;
        }

        if (spec.DisplayRange is not null)
        {
            (double lower, double upper) = DisplayRange(spec, units ?? (int)UnitTypeConstants.igUnitScalar, null);
            sensor.LowerRange = lower;
            sensor.UpperRange = upper;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(spec.Name))
        {
            if (!string.Equals(spec.Name, sensor.Name, StringComparison.Ordinal) && Read(document).Any(s => string.Equals(s.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw ToolException.InvalidArgument($"There is already a sensor named '{spec.Name}'.");
            }

            sensor.Name = spec.Name;
            changed = true;
        }

        if (spec.Description is not null)
        {
            sensor.Description = DescriptionOf(spec, sensor.Name);
            changed = true;
        }

        if (spec.Display is not null)
        {
            sensor.DisplayType = DisplayOf(spec.Display);
            changed = true;
        }

        if (spec.Update is not null)
        {
            sensor.UpdateMechanism = UpdateOf(spec.Update);
            changed = true;
        }

        if (!changed)
        {
            throw ToolException.InvalidArgument("set needs something to change: operator, threshold, range, displayRange, name, description, display or update.");
        }

        sensor.Update();
        return Describe(context, document, sensor, index);
    }

    private static (Sensor Sensor, int Index) Find(SeContext context, List<Sensor> sensors, string key)
    {
        string text = key.Trim();
        if (text.Contains(':', StringComparison.Ordinal))
        {
            Sensor wanted = context.Handles.Resolve<Sensor>(text, "sensor");
            int at = sensors.FindIndex(s => Late.SameObject(s, wanted));
            return at >= 0 ? (sensors[at], at + 1) : (wanted, 0);
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return index >= 1 && index <= sensors.Count
                ? (sensors[index - 1], index)
                : throw ToolException.NotFound($"There is no sensor {index}: the document has {sensors.Count}.", "List them with se_sensor list.");
        }

        int found = sensors.FindIndex(s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase));
        return found >= 0
            ? (sensors[found], found + 1)
            : throw ToolException.NotFound($"There is no sensor named '{text}'.", "List them with se_sensor list.");
    }

    private sealed record Condition(SensorOperatorConstants Operator, double Minimum, double Maximum);

    private static Condition NewCondition(SensorSpec spec, int units)
    {
        if (spec.Operator is not { } op)
        {
            throw ToolException.InvalidArgument("Adding a sensor requires 'operator' and its threshold: greater_than, less_than, equal_to or not_equal_to with 'threshold'; between or not_between with 'range' [min, max].");
        }

        return Parse(ArgumentNames.Snake(op), spec.Threshold, spec.Range, units);
    }

    private static Condition ChangedCondition(Sensor sensor, SensorSpec spec, int units)
    {
        string op = spec.Operator is { } given ? ArgumentNames.Snake(given) : OperatorName(sensor.Operator);
        bool range = op is "between" or "not_between";
        double? threshold = spec.Threshold ?? (range || spec.Operator is not null ? null : VariableService.FromDatabase(sensor.MinimumThreshold, units));
        double[]? limits = spec.Range ?? (!range || spec.Operator is not null ? null
            : [VariableService.FromDatabase(sensor.MinimumThreshold, units), VariableService.FromDatabase(sensor.MaximumThreshold, units)]);
        return Parse(op, threshold, limits, units);
    }

    private static Condition Parse(string op, double? threshold, double[]? range, int units)
    {
        SensorOperatorConstants code = op switch
        {
            "greater_than" => SensorOperatorConstants.seSensorOperatorGreaterThan,
            "less_than" => SensorOperatorConstants.seSensorOperatorLessThan,
            "equal_to" => SensorOperatorConstants.seSensorOperatorEqualTo,
            "not_equal_to" => SensorOperatorConstants.seSensorOperatorNotEqualTo,
            "between" => SensorOperatorConstants.seSensorOperatorBetween,
            "not_between" => SensorOperatorConstants.seSensorOperatorNotBetween,
            _ => throw ToolException.InvalidArgument($"Unknown operator '{op}'.", $"Use one of: {string.Join(", ", Operators)}."),
        };

        if (code is SensorOperatorConstants.seSensorOperatorBetween or SensorOperatorConstants.seSensorOperatorNotBetween)
        {
            if (threshold is not null || range is not { Length: 2 } || !double.IsFinite(range[0]) || !double.IsFinite(range[1]) || range[0] > range[1])
            {
                throw ToolException.InvalidArgument($"'{op}' takes 'range': [min, max] with min <= max, and no 'threshold'.");
            }

            return new Condition(code, VariableService.ToDatabase(range[0], units), VariableService.ToDatabase(range[1], units));
        }

        if (range is not null || threshold is not { } value || !double.IsFinite(value))
        {
            throw ToolException.InvalidArgument($"'{op}' takes one 'threshold' and no 'range'.");
        }

        // The operators with one threshold compare with MinimumThreshold; both are set so the sensor reads the same either way.
        double si = VariableService.ToDatabase(value, units);
        return new Condition(code, si, si);
    }

    private static (double Lower, double Upper) DisplayRange(SensorSpec spec, int units, Condition? condition)
    {
        if (spec.DisplayRange is { } given)
        {
            if (given.Length != 2 || !double.IsFinite(given[0]) || !double.IsFinite(given[1]) || given[0] >= given[1])
            {
                throw ToolException.InvalidArgument("'displayRange' takes [lower, upper] with lower < upper: the scale of the sensor's range bar.");
            }

            return (VariableService.ToDatabase(given[0], units), VariableService.ToDatabase(given[1], units));
        }

        double top = Math.Max(Math.Abs(condition?.Maximum ?? 0), Math.Abs(condition?.Minimum ?? 0));
        double lower = Math.Min(0, Math.Min(condition?.Minimum ?? 0, condition?.Maximum ?? 0) * 2);
        return (lower, top > 0 ? top * 2 : 1);
    }

    private static string NameFor(SolidEdgeDocument document, string? name, string basis)
    {
        HashSet<string> taken = Read(document).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return taken.Contains(name)
                ? throw ToolException.InvalidArgument($"There is already a sensor named '{name}'.", "Pick another name or change that sensor with se_sensor set.")
                : name;
        }

        for (int i = 1; ; i++)
        {
            string candidate = $"{basis} sensor {i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The description, never empty: verified in Solid Edge 2026, a sensor with an empty description makes the document fail
    /// to save (SaveAs: DISP_E_EXCEPTION).
    /// </summary>
    private static string DescriptionOf(SensorSpec spec, string name)
    {
        if (spec.Description is null)
        {
            return name;
        }

        return !string.IsNullOrWhiteSpace(spec.Description)
            ? spec.Description
            : throw ToolException.InvalidArgument("'description' cannot be empty: Solid Edge cannot save a document with a sensor without a description.",
                "Leave it out (the sensor name is used) or give some text.");
    }

    private static SensorDisplayTypeConstants DisplayOf(SensorDisplay? display) => display switch
    {
        null or SensorDisplay.Range => SensorDisplayTypeConstants.seSensorDisplayTypeHorizontalRange,
        SensorDisplay.TrueFalse => SensorDisplayTypeConstants.seSensorDisplayTypeTrueFalse,
        _ => throw new ArgumentOutOfRangeException(nameof(display)),
    };

    private static SensorUpdateMechanismConstants UpdateOf(SensorUpdate? update) => update switch
    {
        null or SensorUpdate.Automatic => SensorUpdateMechanismConstants.seSensorUpdateMechanismAutomatic,
        SensorUpdate.Manual => SensorUpdateMechanismConstants.seSensorUpdateMechanismManual,
        _ => throw new ArgumentOutOfRangeException(nameof(update)),
    };

    private static void Remember(Sensor sensor, int units, string? variable)
    {
        try
        {
            var sets = (AttributeSets)sensor.AttributeSets;
            AttributeSet set = sets.Add(AttributeSetName);
            set.Add(UnitsAttribute, AttributeTypeConstants.seLong).Value = units;
            if (variable is not null)
            {
                set.Add(VariableAttribute, AttributeTypeConstants.seStringUnicode).Value = variable;
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Without the attribute set the value of a variable sensor is reported in database units.
        }
    }

    private static object? Remembered(Sensor sensor, string attribute)
    {
        try
        {
            if (Late.Get(sensor, "IsAttributeSetPresent", AttributeSetName) is not true)
            {
                return null;
            }

            var set = (AttributeSet)((AttributeSets)sensor.AttributeSets).Item(AttributeSetName);
            foreach (SolidEdgeFramework.Attribute item in set)
            {
                if (string.Equals(item.Name, attribute, StringComparison.Ordinal))
                {
                    return item.Value;
                }
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Read like a sensor made outside this server.
        }

        return null;
    }

    /// <summary>Units of the sensor's value (UnitTypeConstants), or null when unknown (database units).</summary>
    private static int? UnitsOf(Sensor sensor)
    {
        if (Remembered(sensor, UnitsAttribute) is { } units)
        {
            return Convert.ToInt32(units, CultureInfo.InvariantCulture);
        }

        return (int)sensor.SensorType switch
        {
            (int)SensorTypeConstants.seSensorTypeMinimumDistance => (int)UnitTypeConstants.igUnitDistance,
            SurfaceAreaType => (int)UnitTypeConstants.igUnitArea,
            _ => null,
        };
    }

    private static object Describe(SeContext context, SolidEdgeDocument document, Sensor sensor, int index)
    {
        int? units = UnitsOf(sensor);
        SensorOperatorConstants op = sensor.Operator;
        bool range = op is SensorOperatorConstants.seSensorOperatorBetween or SensorOperatorConstants.seSensorOperatorNotBetween;
        // A checker sensor reads IsInRange true while the distance is at least its threshold, the opposite of the other sensors.
        bool checker = (int)sensor.SensorType == CheckerType;
        bool alert = checker ? !sensor.IsInRange : sensor.IsInRange;
        return new
        {
            index = index > 0 ? index : (int?)null,
            name = sensor.Name,
            description = sensor.Description is { Length: > 0 } d ? d : null,
            type = TypeName(sensor.SensorType),
            variable = Remembered(sensor, VariableAttribute) as string,
            value = Value(sensor.CurrentValue, units),
            unit = units is { } u ? VariableService.UnitLabel(u) : "database units (m, rad, m2)",
            @operator = checker ? "not_less_than" : OperatorName(op),
            threshold = range ? null : Value(sensor.MinimumThreshold, units),
            range = range ? new[] { Value(sensor.MinimumThreshold, units), Value(sensor.MaximumThreshold, units) } : null,
            met = !alert,
            alert,
            status = sensor.Status switch
            {
                SensorStatusConstants.seSensorStatusUpToDate => "up_to_date",
                SensorStatusConstants.seSensorStatusOutOfDate => "out_of_date",
                SensorStatusConstants.seSensorStatusInError => "error",
                _ => "unknown",
            },
            display = sensor.DisplayType == SensorDisplayTypeConstants.seSensorDisplayTypeTrueFalse ? "true_false" : "range",
            displayRange = new[] { Value(sensor.LowerRange, units), Value(sensor.UpperRange, units) },
            update = sensor.UpdateMechanism == SensorUpdateMechanismConstants.seSensorUpdateMechanismManual ? "manual" : "automatic",
            handle = context.Register(sensor, "sensor", document),
        };
    }

    private static double? Value(double si, int? units) =>
        double.IsFinite(si) ? Math.Round(units is { } u ? VariableService.FromDatabase(si, u) : si, units is null ? 12 : 6) : null;

    private static string TypeName(SensorTypeConstants type) => type switch
    {
        SensorTypeConstants.seSensorTypeVariable => "variable",
        SensorTypeConstants.seSensorTypeMinimumDistance => "minimum_distance",
        (SensorTypeConstants)SurfaceAreaType => "surface_area",
        (SensorTypeConstants)CheckerType => "sheet_metal_checker",
        SensorTypeConstants.seSensorTypeUser => "user",
        _ => type.ToString(),
    };

    private static string OperatorName(SensorOperatorConstants op) => op switch
    {
        SensorOperatorConstants.seSensorOperatorGreaterThan => "greater_than",
        SensorOperatorConstants.seSensorOperatorLessThan => "less_than",
        SensorOperatorConstants.seSensorOperatorEqualTo => "equal_to",
        SensorOperatorConstants.seSensorOperatorNotEqualTo => "not_equal_to",
        SensorOperatorConstants.seSensorOperatorBetween => "between",
        SensorOperatorConstants.seSensorOperatorNotBetween => "not_between",
        _ => "none",
    };
}
