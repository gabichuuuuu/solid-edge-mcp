namespace SolidEdgeMcp.Core;

/// <summary>
/// Minimal log for the layers that do not depend on Microsoft.Extensions.Logging. The server
/// adapts it to <c>ILogger</c> (stderr and file).
/// </summary>
public interface IServerLog
{
    void Info(string message);

    void Warning(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

public sealed class NullServerLog : IServerLog
{
    public static NullServerLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warning(string message, Exception? exception = null)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
