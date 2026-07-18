using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DVarr.Infrastructure;

/// <summary>One captured log line for the in-app Logs viewer.</summary>
public readonly record struct LogEntry(long TsUtc, string Level, string Category, string Message);

/// <summary>
/// A bounded, in-memory ring buffer of the most recent log lines, so the Logs page can show what's happening without
/// SSH into the container. Singleton; thread-safe; capped at <see cref="Capacity"/> entries (oldest dropped). Purely
/// in-memory — nothing is written to disk, and it's gone on restart. The endpoint that exposes it is behind the app's
/// auth like every other API.
/// </summary>
public sealed class LogRingBuffer
{
    public const int Capacity = 2000;
    private readonly ConcurrentQueue<LogEntry> _q = new();

    public void Add(in LogEntry e)
    {
        // Defence-in-depth (audit LOG-01): a provider URL carries the IPTV login in its query or path, and ANY
        // logger category can end up quoting one (an exception message, a misjudged Information line). Nothing
        // secret-shaped is allowed into the buffer this page displays.
        _q.Enqueue(e with { Message = Redact(e.Message) });
        while (_q.Count > Capacity && _q.TryDequeue(out _)) { }
    }

    // Xtream-style creds travel two ways: query pairs (?username=u&password=p / &token= / &apikey=) and path
    // segments (http://host/live/USER/PASS/123.ts). Replace the sensitive values with a fixed marker.
    private static readonly Regex QuerySecret =
        new(@"((?:^|[?&\s])(?:username|password|pass|token|apikey|api_key|secret)=)[^&\s""'<]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XtreamPathSecret =
        new(@"(https?://[^\s/]+/(?:live|movie|series)/)[^/\s]+/[^/\s]+(/)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var m = QuerySecret.Replace(message, "$1[redacted]");
        return XtreamPathSecret.Replace(m, "$1[redacted]/[redacted]$2");
    }

    /// <summary>Recent entries (newest first), optionally filtered by minimum level and a case-insensitive substring.</summary>
    public IReadOnlyList<LogEntry> Recent(int take = 500, string? minLevel = null, string? contains = null)
    {
        var min = LevelRank(minLevel);
        IEnumerable<LogEntry> q = _q;
        if (min > 0) q = q.Where(e => LevelRank(e.Level) >= min);
        if (!string.IsNullOrWhiteSpace(contains))
            q = q.Where(e => e.Message.Contains(contains!, StringComparison.OrdinalIgnoreCase)
                          || e.Category.Contains(contains!, StringComparison.OrdinalIgnoreCase));
        return q.Reverse().Take(Math.Clamp(take, 1, Capacity)).ToList();
    }

    internal static int LevelRank(string? level) => (level ?? "").ToLowerInvariant() switch
    {
        "trace" => 1, "debug" => 2, "information" or "info" => 3, "warning" or "warn" => 4,
        "error" => 5, "critical" => 6, _ => 0,
    };
}

/// <summary>ILoggerProvider that funnels every log message into the shared <see cref="LogRingBuffer"/>.</summary>
public sealed class RingBufferLoggerProvider : ILoggerProvider
{
    private readonly LogRingBuffer _buffer;
    public RingBufferLoggerProvider(LogRingBuffer buffer) => _buffer = buffer;

    // Framework HttpClient categories log full request URLs at Information — for provider calls that's the IPTV
    // login verbatim (audit LOG-01). They're also pure noise for this viewer, so they never enter the ring at all
    // (a global filter in Program.cs quiets them for the console too; this stands even if that filter changes).
    public ILogger CreateLogger(string categoryName)
        => categoryName.StartsWith("System.Net.Http.", StringComparison.Ordinal)
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            : new RingBufferLogger(_buffer, categoryName);

    public void Dispose() { }

    private sealed class RingBufferLogger : ILogger
    {
        private readonly LogRingBuffer _buffer;
        private readonly string _category;
        public RingBufferLogger(LogRingBuffer buffer, string category) { _buffer = buffer; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // Capture Information and above — Debug/Trace are far too chatty for an in-app viewer (and cheap to skip).
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            // Last dotted segment of the category ("DVarr.Services.Recording.RecorderService" → "RecorderService").
            var dot = _category.LastIndexOf('.');
            var shortCat = dot >= 0 && dot < _category.Length - 1 ? _category[(dot + 1)..] : _category;
            _buffer.Add(new LogEntry(EpochTime.Now(), logLevel.ToString(), shortCat, msg));
        }
    }
}
