using System.Collections.Concurrent;
using System.Text;

namespace LiveSubtitles.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warning, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warning, message, ex);
    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? exception = null) { }
}

/// <summary>
/// Appends log lines to a daily rolling file (errors, connection events, warnings). Never logs audio or API keys;
/// transcript text is not written either, so the log is safe to share when reporting a problem.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(4096);
    private readonly Thread _writer;
    private readonly LogLevel _minimum;

    public FileLog(string directory, LogLevel minimum = LogLevel.Info)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        CleanupOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "LogWriter" };
        _writer.Start();
    }

    public string LogDirectory => _directory;
    public string CurrentFile => Path.Combine(_directory, $"livesubtitles-{DateTime.Now:yyyyMMdd}.log");

    public event Action<LogLevel, string>? Logged;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(7)).Append(' ')
            .Append(message);
        if (exception != null) line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
        var text = line.ToString();
        Logged?.Invoke(level, text);
        if (level < _minimum) return;
        if (exception != null) text += Environment.NewLine + exception;
        _queue.TryAdd(text);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine); }
            catch { /* logging must never crash the app */ }
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_directory, "livesubtitles-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(1000);
    }
}
