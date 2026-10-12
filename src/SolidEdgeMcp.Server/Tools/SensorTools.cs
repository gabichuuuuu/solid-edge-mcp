// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum SensorAction
{
    List,
    AddVariable,
    AddDistance,
    AddArea,
    Update,
    Set,
}

public enum SensorOperator
{
    GreaterThan,
    LessThan,
    EqualTo,
    NotEqualTo,
    Between,
    NotBetween,
}

public enum SensorDisplay
{
    Range,
    TrueFalse,
}

public enum SensorUpdate
{
    Automatic,
    Manual,
}

/// <summary>Sensors of parts, sheet metal and assemblies: watched values with a condition (Sensors tab of PathFinder).</summary>
[McpServerToolType]
public sealed class SensorTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_sensor", Title = "Sensors", Destructive = false, OpenWorld = false)]
    [Description("""
        Sensors of a part, sheet metal or assembly document: a watched value and a condition; Solid Edge flags the sensor when the value leaves it. Lengths in mm, areas in mm2, a variable in its own units.
        list (index, name, type, value, unit, operator, threshold or range, met, alert, status, display, update, handle) |
        add_variable (variable = name from se_variables) | add_distance (elements = two faces, edges or vertices; in an assembly also occurrences or faces from se_geometry with source = an occurrence) | add_area (faces) |
        update (manual sensors: read the value again; one 'sensor' or all) | set (sensor: change operator, threshold, range, displayRange, name, description, display, update).
        Condition: operator greater_than | less_than | equal_to | not_equal_to with 'threshold', or between | not_between with 'range' [min, max]. met = the value meets it; alert = Solid Edge flags the sensor (met false).
        Sensors are not deleted here: deleting one through the API breaks every sensor of the document, also in the saved file.
        Example: {"action":"add_distance","elements":["occurrence:3","occurrence:4"],"operator":"greater_than","threshold":5}; {"action":"add_variable","variable":"width","operator":"between","range":[40,80]}.
        """)]
    public Task<CallToolResult> Sensor(
        SensorAction action,
        [Description("update, set: the sensor (name, index or handle from list).")] string? sensor = null,
        [Description("add_*: the variable or dimension to watch (add_variable).")] string? variable = null,
        [Description("add_distance: two handles (faces, edges, vertices; occurrences in an assembly).")] string[]? elements = null,
        [Description("add_area: face handles whose area is watched.")] string[]? faces = null,
        [Description("add_*, set: the condition the value should meet.")] SensorOperator? @operator = null,
        [Description("greater_than, less_than, equal_to, not_equal_to: the threshold (mm, mm2 or the variable's units).")] double? threshold = null,
        [Description("between, not_between: [min, max].")] double[]? range = null,
        [Description("add_*, set: [lower, upper] scale of the range bar; default 0 to twice the threshold.")] double[]? displayRange = null,
        [Description("add_*, set: the sensor name; default '<variable|Distance|Area> sensor N'.")] string? name = null,
        [Description("add_*, set: a description (not empty); default: the sensor name.")] string? description = null,
        [Description("add_*, set: range (a bar, default) or true_false.")] SensorDisplay? display = null,
        [Description("add_*, set: automatic (default: follows the model) or manual (read with update).")] SensorUpdate? update = null,
        [Description("Part, sheet metal or assembly document handle; default: the document of the elements or the active one.")] string? document = null,
        CancellationToken cancellationToken = default)
    {
        string act = Snake(action);
        var spec = new SensorSpec(act, sensor, name, description, variable, elements, faces, @operator is { } o ? Snake(o) : null, threshold, range,
            displayRange, display is { } d ? Snake(d) : null, update is { } u ? Snake(u) : null);
        return runner.RunAsync("se_sensor", act, c => SensorService.Run(c, document, spec), cancellationToken);
    }
}
