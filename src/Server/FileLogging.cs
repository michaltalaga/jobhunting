namespace JobHunting.Server;

/// <summary>
/// Writes the log to data/logs/server-yyyy-MM-dd.log as well as the console, so a run can be analysed afterwards.
/// The usual Logging:LogLevel settings apply to it too.
/// </summary>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() { }

    private void Write(string text)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"server-{DateTime.Now:yyyy-MM-dd}.log"), text);
            }
            catch (IOException)
            {
                // Logging must never take the server down.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            };
            var text = $"{DateTime.Now:HH:mm:ss.fff} {level} {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception is not null) text += exception + Environment.NewLine;
            provider.Write(text);
        }
    }
}
