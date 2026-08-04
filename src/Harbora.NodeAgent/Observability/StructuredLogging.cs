using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Harbora.NodeAgent.Security;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Observability;

/// <summary>
/// One JSON object per line on stdout, which is where systemd's journal collects it.
///
/// <para>
/// Redaction happens here rather than at each call site. A call site can be careful; every call
/// site being careful forever cannot be relied on, and the one that forgets is the one that logs
/// the connection string. Putting the scrub at the only exit makes it structural.
/// </para>
/// </summary>
public sealed class StructuredLoggerProvider(SecretRedactor redactor, LogLevel minimum) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider? _scopes;
    private readonly TextWriter _out = Console.Out;
    private readonly Lock _gate = new();
    private readonly SecretRedactor _redactor = redactor;
    private readonly LogLevel _minimum = minimum;

    public ILogger CreateLogger(string categoryName) => new StructuredLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    private void Write(string json)
    {
        lock (_gate)
        {
            _out.WriteLine(json);
            _out.Flush();
        }
    }

    private sealed class StructuredLogger(StructuredLoggerProvider provider, string category) : ILogger
    {
        private static readonly JsonWriterOptions WriterOptions = new()
        {
            // The log is read by humans in a terminal as often as by a collector; escaping every
            // non-ASCII character would make the Persian half of the messages unreadable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = provider._redactor.Redact(formatter(state, exception));

            using var buffer = new MemoryStream(256);
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
                writer.WriteString("level", Level(logLevel));
                writer.WriteString("logger", category);
                writer.WriteString("msg", message);

                if (eventId.Id != 0) writer.WriteNumber("eventId", eventId.Id);

                if (exception is not null)
                {
                    writer.WriteString("exception", exception.GetType().FullName);
                    // Exception messages routinely carry the argument that caused them, which for
                    // a database helper is the password.
                    writer.WriteString("exceptionMessage", provider._redactor.Redact(exception.Message));
                    if (exception.StackTrace is { } trace)
                        writer.WriteString("stack", provider._redactor.Redact(trace));
                }

                WriteStateFields(writer, state, provider._redactor);
                WriteScopeFields(writer, provider);

                writer.WriteEndObject();
            }

            provider.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        }

        private static void WriteStateFields<TState>(Utf8JsonWriter writer, TState state, SecretRedactor redactor)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> pairs) return;

            foreach (var (key, value) in pairs)
            {
                if (key == "{OriginalFormat}") continue;
                WriteField(writer, key, value, redactor);
            }
        }

        private static void WriteScopeFields(Utf8JsonWriter writer, StructuredLoggerProvider provider)
        {
            provider._scopes?.ForEachScope((scope, w) =>
            {
                if (scope is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                    foreach (var (key, value) in pairs)
                    {
                        if (key == "{OriginalFormat}") continue;
                        WriteField(w, key, value, provider._redactor);
                    }
            }, writer);
        }

        private static void WriteField(Utf8JsonWriter writer, string key, object? value, SecretRedactor redactor)
        {
            var name = JsonEncodedText.Encode(key, WriterOptions.Encoder);

            if (SecretRedactor.LooksSecret(key))
            {
                writer.WriteString(name, SecretRedactor.Mask);
                return;
            }

            switch (value)
            {
                case null: writer.WriteNull(name); break;
                case string s: writer.WriteString(name, redactor.Redact(s)); break;
                case bool b: writer.WriteBoolean(name, b); break;
                case int i: writer.WriteNumber(name, i); break;
                case long l: writer.WriteNumber(name, l); break;
                case double d: writer.WriteNumber(name, d); break;
                default: writer.WriteString(name, redactor.Redact(value.ToString())); break;
            }
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "none",
        };
    }

}
