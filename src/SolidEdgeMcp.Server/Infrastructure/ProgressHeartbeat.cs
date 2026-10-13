// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>
/// While a tool call runs, sends a progress notification every few seconds when the client asked for them (a progress token
/// in the request). Solid Edge reports no progress of its own, so the notifications only say that the call is still
/// running and for how long: clients that limit each request in time and reset the limit on progress then wait for long
/// operations (3D PDF publishing, imports, printing, large assemblies) instead of giving up.
/// </summary>
public static class ProgressHeartbeat
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.Params?.ProgressToken is not { } token)
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task beat = BeatAsync(context.Server, token, context.Params.Name, stop.Token);
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await beat.ConfigureAwait(false);
            }
        };

    private static async Task BeatAsync(McpServer server, ProgressToken token, string tool, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int seconds = (int)watch.Elapsed.TotalSeconds;
                await server.NotifyProgressAsync(token,
                    new ProgressNotificationValue { Progress = seconds, Message = $"{tool}: Solid Edge is still working ({seconds} s)." },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The call ended.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or McpException)
        {
            // The notifications are a courtesy: a client that went away must not make the call fail.
        }
    }
}
