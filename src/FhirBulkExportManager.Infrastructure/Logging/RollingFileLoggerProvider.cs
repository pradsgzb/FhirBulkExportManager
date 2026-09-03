using FhirBulkExportManager.Shared;
using Microsoft.Extensions.Logging;

namespace FhirBulkExportManager.Infrastructure.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly object sync = new();
    private readonly string directory;
    private readonly long maximumBytes;
    private readonly TimeProvider timeProvider;
    private StreamWriter? writer;
    private string? currentPath;

    public RollingFileLoggerProvider(string directory, long maximumBytes = 20 * 1024 * 1024, TimeProvider? timeProvider = null)
    {
        this.directory = Path.GetFullPath(directory);
        this.maximumBytes = maximumBytes;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(this.directory);
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (sync)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (sync)
        {
            EnsureWriter();
            var sanitized = SecurityRedactor.Redact(message);
            var sanitizedException = exception is null ? string.Empty : " | " + SecurityRedactor.Redact(exception.GetType().Name + ": " + exception.Message);
            writer!.WriteLine($"{timeProvider.GetUtcNow():O} [{level}] {category} ({eventId.Id}) {sanitized}{sanitizedException}");
            writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        var date = timeProvider.GetUtcNow().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var basePath = Path.Combine(directory, $"fhir-export-{date}.log");
        var path = basePath;
        var suffix = 0;
        while (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
        {
            suffix++;
            path = Path.Combine(directory, $"fhir-export-{date}-{suffix:D2}.log");
        }

        if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) && writer is not null)
        {
            return;
        }

        writer?.Dispose();
        currentPath = path;
        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough))
        {
            AutoFlush = true,
        };
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
