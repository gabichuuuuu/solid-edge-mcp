using Microsoft.Extensions.Logging;
using SolidEdgeMcp.Core;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>Adapts <see cref="IServerLog"/> from the internal layers to <see cref="ILogger"/>.</summary>
public sealed class LoggerServerLog(ILogger<LoggerServerLog> logger) : IServerLog
{
    public void Info(string message) => logger.LogInformation("{Message}", message);

    public void Warning(string message, Exception? exception = null) => logger.LogWarning(exception, "{Message}", message);

    public void Error(string message, Exception? exception = null) => logger.LogError(exception, "{Message}", message);
}
