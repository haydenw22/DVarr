using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DVarr.Infrastructure;

/// <summary>One captured log line for the in-app Logs viewer.</summary>
public readonly record struct LogEntry(long TsUtc, string Level, string Category, string Message);

/// <summary>
/// A bounded ring buffer of the most recent log lines, so the Logs page can show what's happening without SSH into
/// the container. Singleton; thread-safe; capped at <see cref="Capacity"/> entries (oldest dropped). With
/// <see cref="EnablePersistence"/> the same (redacted) entries are also appended to a rotating file under /config and
/// reloaded on boot, so a restart or a container update no longer throws away the evidence of what went wrong just
/// before it. The endpoint that exposes it is behind the app's auth like every other API.
/// </summary>
public sealed class LogRingBuffer
{
    public const int Capacity = 2000;
    private readonly ConcurrentQueue<LogEntry> _q = new();

    // ---- durable log file (opt-in via EnablePersistence) --------------------------------------------------------
    // Two generations of MaxFileBytes each, so the on-disk cost is bounded (~16 MB) and never needs pruning by age.
    // Writes are batched off the logging path — a log call only ever touches an in-memory queue.
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxPending = 20_000;         // backstop if the writer ever wedges: drop oldest, never grow forever
    private string? _file, _prevFile;
    private readonly ConcurrentQueue<LogEntry> _pending = new();

    public void Add(in LogEntry e)
    {
        // Defence-in-depth (audit LOG-01): a provider URL carries the IPTV login in its query or path, and ANY
        // logger category can end up quoting one (an exception message, a misjudged Information line). Nothing
        // secret-shaped is allowed into the buffer this page displays — or, now, onto disk.
        var redacted = e with { Message = Redact(e.Message) };
        _q.Enqueue(redacted);
        while (_q.Count > Capacity && _q.TryDequeue(out _)) { }
        if (_file is null) return;
        _pending.Enqueue(redacted);
        while (_pending.Count > MaxPending && _pending.TryDequeue(out _)) { }
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

    /// <summary>Start persisting log lines under <paramref name="dir"/> and preload the ring with what earlier runs
    /// left behind, so the Logs page survives a restart or a container update. Best-effort by design: ANY failure
    /// leaves the buffer memory-only rather than breaking logging (or startup).</summary>
    public void EnablePersistence(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "dvarr.log");
            var prev = Path.Combine(dir, "dvarr.log.1");
            LoadTail(prev, file);       // preload BEFORE _file is set, so Add() doesn't re-persist what we just read
            _prevFile = prev;
            _file = file;
            _ = Task.Run(FlushLoopAsync);
        }
        catch { _file = null; }
    }

    /// <summary>Write any queued entries to disk now (called on a batch tick, and on shutdown so the last lines —
    /// usually the most interesting ones — aren't lost).</summary>
    public void Flush()
    {
        if (_file is null || _pending.IsEmpty) return;
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out var e))
            sb.Append(JsonSerializer.Serialize(new { t = e.TsUtc, l = e.Level, c = e.Category, m = e.Message })).Append('\n');
        if (sb.Length == 0) return;
        try { File.AppendAllText(_file, sb.ToString()); Rotate(); } catch { /* disk full / read-only → keep serving from memory */ }
    }

    private async Task FlushLoopAsync()
    {
        while (true)
        {
            try { await Task.Delay(2000); Flush(); }
            catch { /* the writer must never die — a bad batch is dropped, logging continues */ }
        }
    }

    /// <summary>Roll dvarr.log → dvarr.log.1 once it passes the size cap, keeping exactly one previous generation.</summary>
    private void Rotate()
    {
        try
        {
            if (_file is null || _prevFile is null) return;
            var fi = new FileInfo(_file);
            if (!fi.Exists || fi.Length < MaxFileBytes) return;
            if (File.Exists(_prevFile)) File.Delete(_prevFile);
            File.Move(_file, _prevFile);
        }
        catch { }
    }

    /// <summary>Seed the ring from the previous run's files (oldest generation first), keeping only the newest
    /// <see cref="Capacity"/> lines. Unparseable lines (a torn write at a crash) are skipped.</summary>
    private void LoadTail(string prev, string file)
    {
        var lines = new List<string>();
        foreach (var f in new[] { prev, file })
        {
            try { if (File.Exists(f)) lines.AddRange(File.ReadLines(f)); }
            catch { }
        }
        foreach (var line in lines.Count > Capacity ? lines.Skip(lines.Count - Capacity) : lines)
            if (TryParse(line, out var e)) _q.Enqueue(e);
        while (_q.Count > Capacity && _q.TryDequeue(out _)) { }
    }

    private static bool TryParse(string line, out LogEntry e)
    {
        e = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            e = new LogEntry(
                r.TryGetProperty("t", out var t) && t.TryGetInt64(out var ts) ? ts : 0,
                r.TryGetProperty("l", out var l) ? l.GetString() ?? "" : "",
                r.TryGetProperty("c", out var c) ? c.GetString() ?? "" : "",
                r.TryGetProperty("m", out var m) ? m.GetString() ?? "" : "");
            return true;
        }
        catch { return false; }
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
