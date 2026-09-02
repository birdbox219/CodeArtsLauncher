using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Launcher.UI.Services;

/// <summary>
/// Minimal file logger.
///
/// A WinExe has no console, so console logging goes nowhere and the previous build's only
/// diagnostics were ad-hoc File.AppendAllText calls inside a catch block. Everything the launcher
/// logs now lands in launcher.log next to the executable, which is what makes a failed install or
/// a missing channel diagnosable after the fact.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 2048);
    private readonly LogLevel _minimum;

    public FileLoggerProvider(string path, LogLevel minimum = LogLevel.Information)
    {
        _path = path;
        _minimum = minimum;

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Keep one previous run for comparison instead of an ever-growing file.
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                File.Move(path, path + ".1", overwrite: true);
        }
        catch
        {
            // Logging must never be the reason the app fails to start.
        }

        var writer = new System.Threading.Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "launcher-log"
        };
        writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName, _minimum);

    private void Enqueue(string line)
    {
        try { _queue.TryAdd(line); }
        catch (InvalidOperationException) { /* completed */ }
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch { /* disk full, file locked: drop the line rather than crash */ }
        }
    }

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch { }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category, LogLevel minimum) : ILogger
    {
        private readonly string _shortCategory = category.Contains('.')
            ? category[(category.LastIndexOf('.') + 1)..]
            : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
              .Append(" [").Append(Abbrev(logLevel)).Append("] ")
              .Append(_shortCategory).Append(": ")
              .Append(formatter(state, exception))
              .AppendLine();

            if (exception is not null) sb.AppendLine(exception.ToString());

            provider.Enqueue(sb.ToString());
        }

        private static string Abbrev(LogLevel level) => level switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???"
        };
    }
}
