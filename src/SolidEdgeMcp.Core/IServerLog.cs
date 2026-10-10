namespace SolidEdgeMcp.Core;

/// <summary>
/// Registro mínimo para las capas sin dependencia de Microsoft.Extensions.Logging. El servidor lo
/// adapta a <c>ILogger</c> (stderr y archivo).
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
