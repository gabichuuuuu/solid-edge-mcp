// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public static class SessionService
{
    /// <summary>Connects to (or starts) Solid Edge and leaves it visible.</summary>
    public static object Connect(SeContext context)
    {
        SolidEdgeFramework.Application app = context.Application;
        if (!app.Visible)
        {
            app.Visible = true;
        }

        return new
        {
            connected = true,
            version = app.Version,
            processId = app.ProcessID,
            openDocuments = app.Documents.Count,
            environment = app.ActiveEnvironment,
        };
    }
}
