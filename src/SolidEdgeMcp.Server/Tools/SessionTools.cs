using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

public enum SessionAction
{
    Status,
    Connect,
    Disconnect,
    Quit,
    Restart,
}

[McpServerToolType]
public sealed class SessionTools(ToolRunner runner)
{
    [McpServerTool(Name = "se_session", Title = "Solid Edge session", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Connection to Solid Edge. status: state without starting anything (busy operation, open documents, and any Solid Edge dialog that waits for the person: calls fail with dialog_open until it is answered). connect: attach to the running Solid Edge or start it (visible) if allowed. disconnect: release the connection without closing Solid Edge; all handles expire. quit: close Solid Edge (refused while a document has unsaved changes). restart: quit and start it again, e.g. when Solid Edge misbehaves (features failing that worked before).")]
    public Task<CallToolResult> Session(
        [Description("status | connect | disconnect | quit | restart")] SessionAction action,
        CancellationToken cancellationToken)
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
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }
}
