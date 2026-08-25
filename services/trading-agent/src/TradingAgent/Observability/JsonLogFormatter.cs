using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ClaudeTradingAgent.TradingAgent.Observability;

/// <summary>
/// One JSON object per line, with the same field names the other services in
/// this platform use — the log collector lifts `severity` into a label, and
/// mixed formats break parsing at the collector rather than at the app.
/// </summary>
public sealed class JsonLogFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "platform-json";

    private static readonly string Hostname = Environment.MachineName;

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null) return;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", DateTimeOffset.UtcNow.ToString("o"));
            writer.WriteString("severity", Severity(logEntry.LogLevel));
            writer.WriteString("service", "trading-agent");
            writer.WriteString("message", message ?? string.Empty);
            writer.WriteString("hostname", Hostname);
            writer.WriteString("logger", logEntry.Category);

            if (logEntry.Exception is not null)
                writer.WriteString("error", logEntry.Exception.ToString());

            writer.WriteEndObject();
        }

        textWriter.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string Severity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "INFO",
    };
}
