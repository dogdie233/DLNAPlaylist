using Microsoft.Extensions.Logging;

namespace DLNAPlaylist.Util;

/// <summary>
///     把 Microsoft.Extensions.Logging 桥接到 <see cref="LogSink"/>。
///     这样 Kestrel / ASP.NET Core / Hosting 内部的日志会一并出现在我们的 TUI 日志面板里。
///
///     注意：避免反向递归 —— LogSink.Append 内部不要再走 ILogger，否则会无限循环。
///     当前 LogSink 实现没有这个隐患（它直接 publish 到 EventBus）。
/// </summary>
public sealed class LogSinkLoggerProvider(LogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BridgeLogger(sink, ShortenCategory(categoryName));

    public void Dispose() { }

    /// <summary>
    /// "Microsoft.AspNetCore.Hosting.Diagnostics" → "Hosting.Diagnostics"
    /// "Microsoft.AspNetCore.Server.Kestrel" → "Kestrel"
    /// 其他保留最后两段。
    /// </summary>
    static string ShortenCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return "log";
        if (category.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal))
            return "Kestrel";
        if (category.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal))
            return category["Microsoft.AspNetCore.".Length..];
        if (category.StartsWith("Microsoft.Extensions.Hosting.", StringComparison.Ordinal))
            return category["Microsoft.Extensions.Hosting.".Length..];
        // 取最后一段做精简
        var last = category.LastIndexOf('.');
        return last >= 0 ? category[(last + 1)..] : category;
    }

    sealed class BridgeLogger : ILogger
    {
        readonly LogSink _sink;
        readonly string _category;

        public BridgeLogger(LogSink sink, string category)
        {
            _sink = sink;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} :: {exception.GetType().Name}: {exception.Message}";
            _sink.Log(logLevel, _category, message);
        }
    }
}
