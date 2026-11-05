// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.Server.Infrastructure;
using SolidEdgeMcp.SolidEdge.Services;

namespace SolidEdgeMcp.Server.Tools;

[McpServerToolType]
public sealed class ServerTools(ToolRunner runner, ServerPolicy policy, PathGuard guard, AppPaths paths)
{
    [McpServerTool(Name = "se_server_info", Title = "Server information", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Server version, allowed folders for files, work folder, limits, configuration warnings and the Solid Edge installation (version, folders, language, Parasolid version). Does not need Solid Edge running.")]
    public Task<CallToolResult> ServerInfo() =>
        runner.RunLocalAsync("se_server_info", "info", () => Task.FromResult<object?>(new
        {
            server = "solid-edge-mcp",
            version = Program.Version,
            policyFile = policy.SourceFile,
            allowedFolders = guard.AllowedRoots,
            workFolder = guard.WorkFolder,
            exchangeFolder = guard.ExchangeFolder,
            logFolder = paths.Logs,
            startSolidEdgeIfNotRunning = policy.StartSolidEdgeIfNotRunning,
            operationTimeoutSeconds = policy.OperationTimeoutSeconds,
            limits = policy.Limits,
            warnings = guard.Warnings,
            solidEdge = InstallationService.Read() ?? new { installed = false },
            units = "Tools use millimetres and degrees; se_com_* use metres and radians.",
        }));
}
