using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>A private bounded log for launches without a journal.</summary>
internal sealed class FlatpakLog(string path) : ILoggerProvider
{
    private readonly object _gate = new();
    internal const int Limit = 1024 * 1024;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                OpenXlrPaths.EnsurePrivateDir(Path.GetDirectoryName(path)!);
                if (new FileInfo(path).LinkTarget is not null || new FileInfo(path + ".old").LinkTarget is not null)
                    return;
                if (File.Exists(path) && new FileInfo(path).Length >= Limit) File.Move(path, path + ".old", true);
                // Bound one pathological message as well as the file count.
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read,
                                    };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(path, options);
                using var writer = new StreamWriter(file);
                writer.WriteLine(line.Length > 8192 ? line[..8192] : line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Cannot write daemon log: " + ex.Message); }
        }
    }

    private sealed class FileLogger(FlatpakLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) owner.Write($"{DateTimeOffset.UtcNow:O} {level} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
