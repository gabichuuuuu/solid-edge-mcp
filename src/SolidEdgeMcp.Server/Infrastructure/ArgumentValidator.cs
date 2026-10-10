using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>
/// Comprueba los argumentos de cada llamada contra el esquema de la herramienta antes de enlazarlos.
/// Si el enlace falla (enumeración desconocida, tipo erróneo), el SDK solo devuelve un error genérico
/// sin pistas; así el cliente recibe qué argumento está mal y qué valores admite.
/// </summary>
public static class ArgumentValidator
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            Tool? tool = (context.MatchedPrimitive as McpServerTool)?.ProtocolTool;
            if (tool is not null && Validate(tool, context.Params?.Arguments) is { } error)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] };
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    /// <summary>Mensaje de error para el cliente, o <c>null</c> si los argumentos encajan con el esquema.</summary>
    public static string? Validate(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        JsonElement schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out JsonElement properties))
        {
            return null;
        }

        string[] names = properties.EnumerateObject().Select(p => p.Name).ToArray();
        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement item in required.EnumerateArray())
            {
                string name = item.GetString()!;
                if (arguments is null || !arguments.ContainsKey(name))
                {
                    return $"[invalid_argument] {tool.Name} requires '{name}'.";
                }
            }
        }

        if (arguments is null)
        {
            return null;
        }

        foreach ((string name, JsonElement value) in arguments)
        {
            if (!properties.TryGetProperty(name, out JsonElement property))
            {
                return $"[invalid_argument] {tool.Name} has no argument '{name}'. Valid arguments: {string.Join(", ", names)}.";
            }

            if (Check(name, property, value) is { } problem)
            {
                return $"[invalid_argument] {tool.Name}: {problem}";
            }
        }

        return null;
    }

    private static string? Check(string name, JsonElement property, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.TryGetProperty("enum", out JsonElement allowed))
        {
            string[] options = allowed.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToArray();
            return value.ValueKind == JsonValueKind.String && options.Contains(value.GetString(), StringComparer.Ordinal)
                ? null
                : $"'{name}' must be one of: {string.Join(", ", options)}; got {value.GetRawText()}.";
        }

        string[] types = TypesOf(property);
        if (types.Length > 0 && !types.Any(t => Matches(t, value)))
        {
            return $"'{name}' must be {string.Join(" or ", types.Where(t => t != "null"))}; got {Describe(value)}.";
        }

        if (value.ValueKind == JsonValueKind.Array && property.TryGetProperty("items", out JsonElement items))
        {
            string[] itemTypes = TypesOf(items);
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (itemTypes.Length > 0 && !itemTypes.Any(t => Matches(t, item)))
                {
                    return $"'{name}[{index}]' must be {string.Join(" or ", itemTypes.Where(t => t != "null"))}; got {Describe(item)}.";
                }

                index++;
            }
        }

        return null;
    }

    private static string[] TypesOf(JsonElement schema) =>
        !schema.TryGetProperty("type", out JsonElement type) ? []
        : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
        : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray()
        : [];

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string {value.GetRawText()}",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };
}
