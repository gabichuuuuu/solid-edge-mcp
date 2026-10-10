using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>Estado de la conexión, sin efectos secundarios.</summary>
public sealed record SessionStatus(
    bool Connected,
    bool SolidEdgeRunning,
    string? Version,
    int? ProcessId,
    bool? Visible,
    int? OpenDocuments,
    string? ActiveDocument,
    string? ActiveEnvironment,
    string? BusyWith,
    double? BusySeconds,
    int QueuedOperations,
    int LiveHandles);

/// <summary>
/// Conexión con Solid Edge. Todo acceso COM pasa por <see cref="RunAsync{T}"/>, que lo ejecuta en
/// el hilo STA, conecta o arranca Solid Edge si hace falta, desactiva los diálogos de alerta durante
/// la operación y traduce los errores COM.
/// </summary>
public sealed class SolidEdgeSession : IDisposable
{
    private readonly StaComDispatcher _dispatcher;
    private readonly ServerPolicy _policy;
    private readonly PathGuard _paths;
    private readonly AppPaths _appPaths;
    private readonly IServerLog _log;
    private Application? _application;
    private volatile bool _connected;

    public SolidEdgeSession(ServerPolicy policy, PathGuard paths, AppPaths appPaths, IServerLog log)
    {
        _policy = policy;
        _paths = paths;
        _appPaths = appPaths;
        _log = log;
        Handles = new HandleRegistry(policy.Limits.MaxHandles);
        _dispatcher = new StaComDispatcher(log, OperationTimeout);
    }

    public HandleRegistry Handles { get; }

    public TimeSpan OperationTimeout => TimeSpan.FromSeconds(_policy.OperationTimeoutSeconds);

    private TimeSpan StartupTimeout => TimeSpan.FromSeconds(_policy.StartupTimeoutSeconds);

    /// <summary>
    /// Ejecuta una operación con Solid Edge. Si no hay conexión, se engancha a la instancia abierta o
    /// (si <paramref name="allowStart"/> y la política lo permiten) arranca una.
    /// </summary>
    public Task<T> RunAsync<T>(string operation, Func<SeContext, T> work, CancellationToken cancellationToken,
        bool allowStart = true, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? OperationTimeout;
        if (!_connected && allowStart && _policy.StartSolidEdgeIfNotRunning)
        {
            limit += StartupTimeout;
        }

        return _dispatcher.InvokeAsync(operation, () => Execute(operation, work, allowStart), limit, cancellationToken);
    }

    /// <summary>Estado sin conectar ni arrancar nada.</summary>
    public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        (string Name, TimeSpan Elapsed)? busy = _dispatcher.Current;
        if (busy is not null)
        {
            return new SessionStatus(_connected, true, null, null, null, null, null, null,
                busy.Value.Name, Math.Round(busy.Value.Elapsed.TotalSeconds, 1), _dispatcher.Pending, Handles.Count);
        }

        return await _dispatcher.InvokeAsync("status", () =>
        {
            Application? app = TryGetApplication(allowStart: false);
            if (app is null)
            {
                return new SessionStatus(false, false, null, null, null, null, null, null, null, null, 0, Handles.Count);
            }

            string? activeDocument = null;
            try
            {
                if (app.Documents.Count > 0 && app.ActiveDocument is SolidEdgeDocument doc)
                {
                    activeDocument = doc.Name;
                }
            }
            catch (COMException)
            {
            }

            return new SessionStatus(true, true, app.Version, app.ProcessID, app.Visible, app.Documents.Count,
                activeDocument, SafeEnvironment(app), null, null, _dispatcher.Pending, Handles.Count);
        }, OperationTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Suelta la conexión sin cerrar Solid Edge. Los handles dejan de ser válidos.</summary>
    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync("disconnect", () => Reset("the server disconnected from Solid Edge"), OperationTimeout, cancellationToken);

    private T Execute<T>(string operation, Func<SeContext, T> work, bool allowStart)
    {
        Application app = TryGetApplication(allowStart) ?? throw new ToolException(
            "solid_edge_not_running",
            "Solid Edge is not running.",
            _policy.StartSolidEdgeIfNotRunning
                ? "Call se_session with action 'connect' to start it, or start Solid Edge manually."
                : "Start Solid Edge manually (startSolidEdgeIfNotRunning is false in policy.json).");

        bool? previousAlerts = null;
        try
        {
            previousAlerts = app.DisplayAlerts;
            if (previousAlerts == true)
            {
                app.DisplayAlerts = false;
            }

            return work(new SeContext(app, Handles, _paths, _policy, _appPaths));
        }
        catch (Exception ex)
        {
            if (ComErrors.IsDisconnected(ex))
            {
                // Solid Edge desconecta los objetos que borra (p. ej. el boceto de una operación borrada):
                // solo se reinicia la sesión si la aplicación tampoco responde.
                if (IsAlive(app))
                {
                    throw ComErrors.ObjectGone(ex, operation);
                }

                Reset("Solid Edge closed or restarted");
            }

            Exception translated = ComErrors.Translate(ex, operation);
            if (translated is ToolException)
            {
                throw translated;
            }

            throw;
        }
        finally
        {
            if (previousAlerts == true && _application is not null)
            {
                try
                {
                    _application.DisplayAlerts = true;
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                }
            }
        }
    }

    private static bool IsAlive(Application app)
    {
        try
        {
            _ = app.ProcessID;
            return true;
        }
        catch (Exception ex) when (ComErrors.IsDisconnected(ex))
        {
            return false;
        }
    }

    /// <summary>Aplicación conectada y viva; reconecta si Solid Edge se cerró o reinició.</summary>
    private Application? TryGetApplication(bool allowStart)
    {
        if (_application is not null)
        {
            try
            {
                _ = _application.ProcessID;
                return _application;
            }
            catch (Exception ex) when (ComErrors.IsDisconnected(ex))
            {
                _log.Warning("The connection to Solid Edge was lost; reconnecting.");
                Reset("Solid Edge closed or restarted");
            }
        }

        object? running = NativeMethods.TryGetActiveObject(SolidEdgeLauncher.ProgId);
        bool started = false;
        if (running is null && allowStart && _policy.StartSolidEdgeIfNotRunning)
        {
            running = SolidEdgeLauncher.Start(StartupTimeout, _log);
            started = true;
        }

        if (running is null)
        {
            return null;
        }

        _application = (Application)running;
        _connected = true;
        WaitUntilResponsive(_application);
        if (started)
        {
            // Arrancado por COM, Solid Edge empieza oculto: visible, la persona lo ve y lo controla,
            // y sigue abierto cuando el servidor lo suelta.
            _application.Visible = true;
        }

        _log.Info($"Connected to Solid Edge {_application.Version} (pid {_application.ProcessID}).");
        return _application;
    }

    /// <summary>Tras arrancar, Solid Edge se registra antes de estar listo: espera a que conteste.</summary>
    private void WaitUntilResponsive(Application app)
    {
        DateTime deadline = DateTime.UtcNow + StartupTimeout;
        while (true)
        {
            try
            {
                _ = app.Documents.Count;
                return;
            }
            catch (COMException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
            }
        }
    }

    private void Reset(string reason)
    {
        Handles.Clear(reason);
        _application = null;
        _connected = false;
    }

    private static string? SafeEnvironment(Application app)
    {
        try
        {
            return app.ActiveEnvironment;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
    }
}
