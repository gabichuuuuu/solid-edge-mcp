// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum SessionAction
{
    Status,
    Connect,
    Disconnect,
    Quit,
    Restart,
    Answer,
}

[McpServerToolType]
public sealed class SessionTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_session", Title = "Solid Edge session", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Connection to Solid Edge. status: state without starting anything (busy operation, open documents, and any Solid Edge dialog that waits for the person, with its buttons and options: calls fail with dialog_open until it is answered). connect: attach to the running Solid Edge or start it (visible) if allowed. disconnect: release the connection without closing Solid Edge; all handles expire. quit: close Solid Edge (refused while a document has unsaved changes). restart: quit and start it again, e.g. when Solid Edge misbehaves (features failing that worked before). answer: answer the dialog that waits, pressing 'button' after choosing 'option' (a radio button or check box), by the text status shows; the call it blocked then finishes. Answer as the person would: an option such as \"don't prompt me again\" changes Solid Edge's options, so choose it only when the person asks.")]
    public Task<CallToolResult> Session(
        [Description("status | connect | disconnect | quit | restart | answer")] SessionAction action,
        CancellationToken cancellationToken,
        [Description("answer: the button to press, as status lists it (e.g. OK).")] string? button = null,
        [Description("answer: the radio button or check box to choose first, as status lists it.")] string? option = null,
        [Description("answer: part of the title of the dialog, when several wait.")] string? dialog = null)
    {
        return action switch
        {
            SessionAction.Status => runner.RunLocalAsync("se_session", "status",
                async () => await runner.Session.GetStatusAsync(cancellationToken).ConfigureAwait(false)),
            SessionAction.Connect => runner.RunAsync("se_session", "connect", SessionService.Connect, cancellationToken),
            SessionAction.Disconnect => runner.RunLocalAsync("se_session", "disconnect", async () =>
            {
                await runner.Session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                return new { connected = false };
            }),
            SessionAction.Quit => runner.RunLocalAsync("se_session", "quit",
                async () => await runner.Session.QuitAsync(cancellationToken).ConfigureAwait(false)),
            SessionAction.Restart => runner.RunLocalAsync("se_session", "restart", async () =>
            {
                object quit = await runner.Session.QuitAsync(cancellationToken).ConfigureAwait(false);
                object connect = await runner.Session.RunAsync("connect", SessionService.Connect, cancellationToken).ConfigureAwait(false);
                return new { quit, connect };
            }),
            // Not through Solid Edge's thread, which the dialog keeps waiting: the dialog is answered through its window.
            SessionAction.Answer => runner.RunLocalAsync("se_session", "answer", async () =>
            {
                object answer = SolidEdgeDialogs.Answer(ToolRunner.Require(button, "button", "answer"), option, dialog);
                bool free = await runner.Session.WaitIdleAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return new
                {
                    answer,
                    free,
                    note = free ? null : "Solid Edge is still running the call the dialog blocked (or shows another dialog): se_session status shows it.",
                };
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
