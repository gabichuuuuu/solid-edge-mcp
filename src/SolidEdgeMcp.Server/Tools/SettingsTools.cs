using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;
using static SolidEdgeMcp.Server.Tools.FeatureTools;

namespace SolidEdgeMcp.Server.Tools;

public enum OptionAction
{
    List,
    Get,
    Set,
    Units,
    SetUnits,
}

public enum OptionScope
{
    Application,
    Document,
}

/// <summary>Opciones de Solid Edge y del documento, y unidades.</summary>
[McpServerToolType]
public sealed class SettingsTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_options", Title = "Solid Edge options and units", Destructive = true, OpenWorld = false)]
    [Description("""
        Solid Edge options (the Solid Edge Options dialog and global settings) and document units.
        list (scope application|document, filter = text in the name) | get (name) | set (name, value of the same type as the current one; colours as '#RRGGBB'; returns the previous value to restore it) |
        units (display units of the document: type, unit, precision) | set_units (unitType e.g. distance|angle|mass|area|volume, unit e.g. millimeter|inch|degree|kilogram, precision index).
        Application options belong to the person and persist between sessions: change them only when asked. Document options (scope document) depend on the type (part, sheet metal, assembly, draft, weldment) and are in SI units (m, rad, kg/m3).
        Units only change what Solid Edge displays: the se_* tools always use mm, degrees and kg.
        Example: {"action":"get","name":"PartandAsmUndoSteps"}.
        """)]
    public Task<CallToolResult> Options(
        OptionAction action,
        [Description("application (default) or document.")] OptionScope scope = OptionScope.Application,
        [Description("Option name as listed (prefix such as seApplicationGlobal optional).")] string? name = null,
        [Description("For set: new value (bool, number, text; colours '#RRGGBB').")] JsonElement? value = null,
        [Description("For list: text contained in the option name.")] string? filter = null,
        [Description("For set_units: unit type, e.g. distance, angle, mass, area, volume.")] string? unitType = null,
        [Description("For set_units: unit name (millimeter, inch, meter, degree, radian, kilogram...) or its numeric value.")] string? unit = null,
        [Description("For set_units: precision index (distance: 0 ones, 1 tenths, 2 hundredths, 3 thousandths...).")] int? precision = null,
        [Description("Document handle; default: active document.")] string? document = null,
        int offset = 0,
        [Description("For list: maximum number of options (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        const string tool = "se_options";
        string actionName = Snake(action);
        string where = Snake(scope);
        return action switch
        {
            OptionAction.List => runner.RunAsync(tool, actionName, c => OptionService.List(c, where, document, filter, offset, limit), cancellationToken),
            OptionAction.Get => runner.RunAsync(tool, actionName, c => OptionService.Get(c, where, document,
                ToolRunner.Require(name, "name", "get")), cancellationToken),
            OptionAction.Set => runner.RunAsync(tool, actionName, c => OptionService.Set(c, where, document,
                ToolRunner.Require(name, "name", "set"), ToolRunner.RequireValue(value, "value", "set")), cancellationToken),
            OptionAction.Units => runner.RunAsync(tool, actionName, c => OptionService.Units(c, document), cancellationToken),
            OptionAction.SetUnits => runner.RunAsync(tool, actionName, c => OptionService.SetUnits(c, document,
                ToolRunner.Require(unitType, "unitType", "set_units"), unit, precision), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
