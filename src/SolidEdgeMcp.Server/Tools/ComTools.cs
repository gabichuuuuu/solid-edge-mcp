using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.Server.Tools;

/// <summary>
/// Capa genérica: cualquier objeto, propiedad o método del API de Solid Edge. Unidades del API
/// (metros y radianes). Las rutas de archivo pasan por las carpetas permitidas.
/// </summary>
[McpServerToolType]
public sealed class ComTools(ToolRunner runner)
{
    private const string TargetDescription = "Object: a handle (e.g. 'feature:7'), 'app' (Application) or 'active_document'.";

    [McpServerTool(Name = "se_com_describe", Title = "Describe API object", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the properties and methods (with parameter types) of a Solid Edge API object, or of an interface by full name (e.g. 'SolidEdgePart.Model'). Use it before se_com_get/set/invoke. API units: metres and radians.")]
    public Task<CallToolResult> Describe(
        [Description(TargetDescription + " Omit when 'type' is given.")] string? target = null,
        [Description("Interface full name instead of an object, e.g. 'SolidEdgePart.ExtrudedProtrusions'.")] string? type = null,
        [Description("Only members whose name contains this text.")] string? filter = null,
        int offset = 0,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (target is null && type is not null)
        {
            return runner.RunLocalAsync("se_com_describe", "describe", () => Task.FromResult<object?>(
                GenericCom.Describe(null, null, type, filter, offset, Math.Min(limit, 2000))));
        }

        return runner.RunAsync("se_com_describe", "describe", c => GenericCom.Describe(c, target, type, filter, offset, limit), cancellationToken);
    }

    [McpServerTool(Name = "se_com_get", Title = "Read API property", ReadOnly = true, OpenWorld = false)]
    [Description("Reads a property (or calls a parameterless getter) of a Solid Edge API object. Objects come back as handles. Example: {\"target\":\"active_document\",\"member\":\"Models\"}.")]
    public Task<CallToolResult> Get(
        [Description(TargetDescription)] string target,
        [Description("Property name.")] string member,
        [Description("Index arguments for indexed properties, as a JSON array.")] JsonElement? index = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_com_get", member, c => new { value = GenericCom.Get(c, target, member, index) }, cancellationToken);

    [McpServerTool(Name = "se_com_set", Title = "Write API property", Destructive = true, OpenWorld = false)]
    [Description("Sets a property of a Solid Edge API object and returns the new value. API units (metres, radians). Pass objects as {\"handle\":\"...\"}.")]
    public Task<CallToolResult> Set(
        [Description(TargetDescription)] string target,
        [Description("Property name.")] string member,
        [Description("New value (JSON).")] JsonElement value,
        [Description("Index arguments for indexed properties, as a JSON array.")] JsonElement? index = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_com_set", member, c => new { value = GenericCom.Set(c, target, member, value, index) }, cancellationToken);

    [McpServerTool(Name = "se_com_invoke", Title = "Call API method", Destructive = true, OpenWorld = false)]
    [Description("""
        Calls any method of a Solid Edge API object with positional arguments (JSON array). API units: metres and radians; collections start at 1.
        Objects: {"handle":"..."} (also {"handle":"app"} or {"handle":"active_document"}). Typed values when needed: {"type":"double[]","value":[0,0,0]} (also int, short, bool, string, int[], object[]). Omit or null trailing optional arguments.
        Returns the result and any ref/out arguments. File paths must be inside the allowed folders.
        Example: {"target":"refplanes:4","method":"Item","arguments":[1]}.
        """)]
    public Task<CallToolResult> Invoke(
        [Description(TargetDescription)] string target,
        [Description("Method name.")] string method,
        [Description("Positional arguments as a JSON array.")] JsonElement? arguments = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_com_invoke", method, c => GenericCom.Invoke(c, target, method, arguments), cancellationToken);

    [McpServerTool(Name = "se_com_items", Title = "List API collection", ReadOnly = true, OpenWorld = false)]
    [Description("Lists the items of a Solid Edge collection (Count/Item, 1-based) with their names and handles. Optionally first reads a property that returns the collection, e.g. {\"target\":\"active_document\",\"member\":\"RefPlanes\"}.")]
    public Task<CallToolResult> Items(
        [Description(TargetDescription)] string target,
        [Description("Optional property of the target that returns the collection.")] string? member = null,
        int offset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("se_com_items", member ?? "items", c => GenericCom.Items(c, target, member, offset, limit), cancellationToken);

    [McpServerTool(Name = "se_com_constants", Title = "API constants", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the Solid Edge API enumerations by enum name or value name, e.g. 'FeaturePropertyConstants' or 'igRight'. Does not need Solid Edge.")]
    public Task<CallToolResult> Constants(
        [Description("Text contained in the enum or value name.")] string query,
        int offset = 0,
        [Description("Maximum number of values (default 100).")] int limit = 100) =>
        runner.RunLocalAsync("se_com_constants", "search", () => Task.FromResult<object?>(GenericCom.Constants(query, offset, limit)));
}
