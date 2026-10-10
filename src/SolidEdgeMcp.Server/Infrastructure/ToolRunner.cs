using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>Imagen devuelta por una operación (se envía como contenido de imagen MCP).</summary>
public sealed record ToolImage(byte[] Data, string MimeType, object? Details);

/// <summary>
/// Ejecuta las operaciones de las herramientas: registra cada una con un id, convierte el resultado
/// en JSON compacto y los errores en mensajes accionables (sin trazas, que van solo al log).
/// </summary>
public sealed class ToolRunner(SolidEdgeSession session, ILogger<ToolRunner> logger)
{
    private static long _operationCounter;

    public static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public SolidEdgeSession Session => session;

    /// <summary>
    /// Operación con Solid Edge (en el hilo STA). <paramref name="precheck"/> valida antes de
    /// conectar o arrancar Solid Edge (p. ej. rutas no permitidas).
    /// </summary>
    public Task<CallToolResult> RunAsync(string tool, string action, Func<SeContext, object?> work,
        CancellationToken cancellationToken, bool allowStart = true, TimeSpan? timeout = null, Action? precheck = null) =>
        ExecuteAsync(tool, action, () =>
        {
            precheck?.Invoke();
            return session.RunAsync($"{tool}.{action}", work, cancellationToken, allowStart, timeout);
        });

    /// <summary>Operación que no necesita Solid Edge.</summary>
    public Task<CallToolResult> RunLocalAsync(string tool, string action, Func<Task<object?>> work) =>
        ExecuteAsync(tool, action, work);

    private async Task<CallToolResult> ExecuteAsync(string tool, string action, Func<Task<object?>> work)
    {
        long id = Interlocked.Increment(ref _operationCounter);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("op#{Id} {Tool}.{Action} started", id, tool, action);
        try
        {
            object? result = await work().ConfigureAwait(false);
            logger.LogInformation("op#{Id} {Tool}.{Action} ok in {Ms} ms", id, tool, action, stopwatch.ElapsedMilliseconds);
            return ToResult(result);
        }
        catch (ToolException ex)
        {
            logger.LogWarning(ex.InnerException, "op#{Id} {Tool}.{Action} failed in {Ms} ms: {Code} {Message}", id, tool, action,
                stopwatch.ElapsedMilliseconds, ex.Code, ex.Message);
            throw new McpException(ex.ToClientMessage());
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("op#{Id} {Tool}.{Action} cancelled", id, tool, action);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "op#{Id} {Tool}.{Action} failed unexpectedly", id, tool, action);
            throw new McpException($"[internal_error] {tool} '{action}' failed: {ex.GetType().Name}: {ex.Message} (details in the server log, op#{id}).");
        }
    }

    public static CallToolResult ToResult(object? result)
    {
        if (result is CallToolResult ready)
        {
            return ready;
        }

        if (result is ToolImage image)
        {
            var content = new List<ContentBlock> { ImageContentBlock.FromBytes(image.Data, image.MimeType) };
            if (image.Details is not null)
            {
                content.Add(new TextContentBlock { Text = JsonSerializer.Serialize(image.Details, ResultJson) });
            }

            return new CallToolResult { Content = content };
        }

        string text = result switch
        {
            null => "{\"ok\":true}",
            string s => s,
            _ => JsonSerializer.Serialize(result, ResultJson),
        };
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    /// <summary>Exige un argumento obligatorio para una acción concreta.</summary>
    public static T Require<T>(T? value, string name, string action) where T : class =>
        value ?? throw ToolException.InvalidArgument($"Action '{action}' requires '{name}'.");

    public static T RequireValue<T>(T? value, string name, string action) where T : struct =>
        value ?? throw ToolException.InvalidArgument($"Action '{action}' requires '{name}'.");
}
