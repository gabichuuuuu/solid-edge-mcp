using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;

namespace SolidEdgeMcp.SolidEdge.Session;

/// <summary>Connection state, with no side effects.</summary>
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
    int LiveHandles,
    IReadOnlyList<DialogInfo>? Dialogs = null);

/// <summary>
/// Connection to Solid Edge. Every COM access goes through <see cref="RunAsync{T}"/>, which runs it on
/// the STA thread, connects to or starts Solid Edge if needed, disables alert dialogs during
/// the operation and translates COM errors.
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
        _dispatcher = new StaComDispatcher(log, OperationTimeout, SolidEdgeDialogs.FindOpen);
    }

    public HandleRegistry Handles { get; }

    public TimeSpan OperationTimeout => TimeSpan.FromSeconds(_policy.OperationTimeoutSeconds);

    private TimeSpan StartupTimeout => TimeSpan.FromSeconds(_policy.StartupTimeoutSeconds);

    /// <summary>
    /// Runs an operation with Solid Edge. If there is no connection, it attaches to the open instance or
    /// (if <paramref name="allowStart"/> and the policy allow it) starts one.
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

    /// <summary>State without connecting to or starting anything.</summary>
    public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // A modal dialog (e.g. a first-use tip) blocks Solid Edge until the person answers it: it is reported
        // without calling Solid Edge, which would wait for the dialog too.
        IReadOnlyList<DialogInfo> dialogs = _dispatcher.OpenDialogs();
        IReadOnlyList<DialogInfo>? shown = dialogs.Count > 0 ? dialogs : null;
        (string Name, TimeSpan Elapsed)? busy = _dispatcher.Current;
        if (busy is not null || dialogs.Any(d => d.WaitsForAnswer))
        {
            return new SessionStatus(_connected, true, null, null, null, null, null, null,
                busy?.Name, busy is { } b ? Math.Round(b.Elapsed.TotalSeconds, 1) : null, _dispatcher.Pending, Handles.Count, shown);
        }

        return await _dispatcher.InvokeAsync("status", () =>
        {
            // Without a connection (at startup or after 'disconnect') the open instance is checked without attaching to it;
            // when connected, TryGetApplication also reconnects if Solid Edge was restarted.
            Application? app = _application is not null ? TryGetApplication(allowStart: false) : null;
            bool connected = app is not null;
            app ??= NativeMethods.TryGetActiveObject(SolidEdgeLauncher.ProgId) as Application;
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

            return new SessionStatus(connected, true, app.Version, app.ProcessID, app.Visible, app.Documents.Count,
                activeDocument, SafeEnvironment(app), null, null, _dispatcher.Pending, Handles.Count, shown);
        }, OperationTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the connection without closing Solid Edge. Handles stop being valid.</summary>
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
                // Solid Edge disconnects the objects it deletes (e.g. the sketch of a deleted feature):
                // the session is only restarted if the application does not respond either.
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

    /// <summary>Connected, live application; reconnects if Solid Edge was closed or restarted.</summary>
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
            // Started through COM, Solid Edge starts hidden: visible, so the person can see and control it,
            // and it stays open when the server releases it.
            _application.Visible = true;
        }

        _log.Info($"Connected to Solid Edge {_application.Version} (pid {_application.ProcessID}).");
        return _application;
    }

    /// <summary>After starting, Solid Edge registers before it is ready: waits until it responds.</summary>
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
