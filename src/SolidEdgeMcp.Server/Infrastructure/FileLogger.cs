using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SolidEdgeMcp.Server.Infrastructure;

/// <summary>
/// Log diario en UTF-8 en <c>%LocalAppData%\SolidEdgeMcp\logs</c>. stdout queda reservado al
/// protocolo MCP; la consola de logs es stderr.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly LogLevel _minimum;

    public FileLoggerProvider(string directory, LogLevel minimum)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(level.ToString().ToUpperInvariant()[..4])
            .Append(' ').Append(category[(category.LastIndexOf('.') + 1)..])
            .Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        string path = Path.Combine(_directory, $"solid-edge-mcp-{DateTime.Now:yyyyMMdd}.log");
        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (IOException)
            {
                // Un fallo del log no debe romper una operación.
            }
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(_directory).EnumerateFiles("solid-edge-mcp-*.log"))
            {
                if (file.LastWriteTime < DateTime.Now.AddDays(-RetentionDays))
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
