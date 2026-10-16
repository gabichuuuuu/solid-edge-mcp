// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

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

/// <summary>Image returned by an operation (sent as MCP image content).</summary>
public sealed record ToolImage(byte[] Data, string MimeType, object? Details);

/// <summary>
/// Runs the tool operations: registers each one with an id, converts the result
/// to compact JSON and errors into actionable messages (no stack traces, which go only to the log).
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
    /// Operation against Solid Edge (on the STA thread). <paramref name="precheck"/> validates before
    /// connecting to or starting Solid Edge (e.g. paths that are not allowed).
    /// </summary>
    public Task<CallToolResult> RunAsync(string tool, string action, Func<SeContext, object?> work,
        CancellationToken cancellationToken, bool allowStart = true, TimeSpan? timeout = null, Action? precheck = null) =>
        ExecuteAsync(tool, action, () =>
        {
            precheck?.Invoke();
            return session.RunAsync($"{tool}.{action}", work, cancellationToken, allowStart, timeout);
        });

    /// <summary>Operation that does not need Solid Edge.</summary>
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
        catch (Exception ex) when (InteropBootstrap.IsMissing(ex))
        {
            logger.LogError(ex, "op#{Id} {Tool}.{Action} failed: the Solid Edge interop is missing", id, tool, action);
            throw new McpException(new ToolException(
                "interop_missing",
                "The Solid Edge interop is not available: the server generates it from the installed Solid Edge when it starts, and it could not (the reason is at the start of the server log).",
                "Check that Solid Edge is installed on this computer and that Windows PowerShell can run scripts, then restart the MCP server.").ToClientMessage());
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

    /// <summary>Requires a mandatory argument for a specific action.</summary>
    public static T Require<T>(T? value, string name, string action) where T : class =>
        value ?? throw ToolException.InvalidArgument($"Action '{action}' requires '{name}'.");

    public static T RequireValue<T>(T? value, string name, string action) where T : struct =>
        value ?? throw ToolException.InvalidArgument($"Action '{action}' requires '{name}'.");
}
