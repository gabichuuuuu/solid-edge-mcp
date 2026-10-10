using System.Collections.Concurrent;
using System.Diagnostics;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.SolidEdge.Com;

/// <summary>
/// Único hilo STA por el que pasan todas las llamadas COM, de una en una. Registra el filtro de
/// mensajes OLE. Una llamada COM en curso no se puede interrumpir: si vence el tiempo, se informa al
/// cliente y la operación sigue hasta terminar; mientras tanto el dispatcher figura como ocupado.
/// </summary>
public sealed class StaComDispatcher : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private readonly IServerLog _log;
    private readonly int _retryWindowMilliseconds;
    private volatile WorkItem? _current;
    private bool _disposed;

    public StaComDispatcher(IServerLog log, TimeSpan messageFilterRetryWindow)
    {
        _log = log;
        _retryWindowMilliseconds = (int)Math.Clamp(messageFilterRetryWindow.TotalMilliseconds, 1_000, int.MaxValue);
        _thread = new Thread(Run) { IsBackground = true, Name = "SolidEdge COM (STA)" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Operación en curso y desde cuándo, o <c>null</c> si está libre.</summary>
    public (string Name, TimeSpan Elapsed)? Current
    {
        get
        {
            WorkItem? item = _current;
            return item is null ? null : (item.Name, item.Stopwatch.Elapsed);
        }
    }

    public int Pending => _queue.Count;

    public bool IsOnDispatcherThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Ejecuta <paramref name="work"/> en el hilo STA y espera su resultado como mucho <paramref name="timeout"/>.</summary>
    public async Task<T> InvokeAsync<T>(string name, Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOnDispatcherThread)
        {
            return work();
        }

        WorkItem? current = _current;
        if (current is not null && current.Stopwatch.Elapsed > current.Timeout)
        {
            throw new ToolException(
                "solid_edge_busy",
                $"Solid Edge is still busy with '{current.Name}' ({current.Stopwatch.Elapsed.TotalSeconds:F0} s, over its time limit).",
                "Check Solid Edge for an open dialog or a long computation, close it, and retry. 'se_session' with action 'status' shows when it is free.");
        }

        var item = new WorkItem<T>(name, work, timeout, cancellationToken);
        _queue.Add(item, CancellationToken.None);
        try
        {
            return await item.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            item.Abandon();
            throw new ToolException(
                "timeout",
                $"'{name}' did not finish within {timeout.TotalSeconds:F0} s. It may still be running in Solid Edge.",
                "Check Solid Edge for an open dialog. Increase operationTimeoutSeconds in policy.json for long operations.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            item.Abandon();
            throw;
        }
    }

    public Task InvokeAsync(string name, Action work, TimeSpan timeout, CancellationToken cancellationToken) =>
        InvokeAsync(name, () =>
        {
            work();
            return true;
        }, timeout, cancellationToken);

    private void Run()
    {
        var filter = new OleMessageFilter(_retryWindowMilliseconds);
        int hr = NativeMethods.CoRegisterMessageFilter(filter, out _);
        if (hr < 0)
        {
            _log.Warning($"CoRegisterMessageFilter failed (0x{hr:X8}); calls rejected by a busy Solid Edge will fail immediately.");
        }

        foreach (WorkItem item in _queue.GetConsumingEnumerable())
        {
            if (item.ShouldSkip)
            {
                item.Cancel();
                continue;
            }

            _current = item;
            try
            {
                item.Execute();
            }
            catch (Exception ex)
            {
                // Execute ya entrega la excepción al llamante; esto solo cubre fallos imprevistos.
                _log.Error($"Unexpected failure in COM operation '{item.Name}'.", ex);
            }
            finally
            {
                _current = null;
                if (item.Abandoned)
                {
                    _log.Warning($"Abandoned COM operation '{item.Name}' finished after {item.Stopwatch.Elapsed.TotalSeconds:F1} s.");
                }
            }
        }

        NativeMethods.CoRegisterMessageFilter(null, out _);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _log.Warning("The COM thread did not stop within 5 s.");
        }
    }

    private abstract class WorkItem(string name, TimeSpan timeout, CancellationToken cancellationToken)
    {
        public string Name { get; } = name;

        public TimeSpan Timeout { get; } = timeout;

        public Stopwatch Stopwatch { get; } = new();

        public volatile bool Abandoned;

        public bool ShouldSkip => Abandoned || cancellationToken.IsCancellationRequested;

        public void Abandon() => Abandoned = true;

        public abstract void Execute();

        public abstract void Cancel();
    }

    private sealed class WorkItem<T>(string name, Func<T> work, TimeSpan timeout, CancellationToken cancellationToken)
        : WorkItem(name, timeout, cancellationToken)
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute()
        {
            Stopwatch.Start();
            try
            {
                Completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
            finally
            {
                Stopwatch.Stop();
            }
        }

        public override void Cancel() => Completion.TrySetCanceled();
    }
}
