using Microsoft.Extensions.Logging;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaximumFileBytes = 1024 * 1024;
    private const int MaximumFiles = 5;
    private readonly string _directory;
    private readonly object _gate = new();

    public RollingFileLoggerProvider(string directory)
    {
        _directory = Path.GetFullPath(directory);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            RotateIfNeeded();
            var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category}: {message}";
            if (exception is not null)
            {
                line += $" ({SensitiveDataSanitizer.ExceptionType(exception)})";
            }

            File.AppendAllText(
                Path.Combine(_directory, "desktop-host.log"),
                SensitiveDataRedactor.Redact(line) + Environment.NewLine);
        }
    }

    private void RotateIfNeeded()
    {
        var active = Path.Combine(_directory, "desktop-host.log");
        if (!File.Exists(active) || new FileInfo(active).Length < MaximumFileBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"desktop-host.{MaximumFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaximumFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"desktop-host.{index}.log");
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(_directory, $"desktop-host.{index + 1}.log"));
            }
        }

        File.Move(active, Path.Combine(_directory, "desktop-host.1.log"));
    }

    private sealed class Logger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
