using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public static class SessionService
{
    /// <summary>Conecta (o arranca) Solid Edge y lo deja visible.</summary>
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
