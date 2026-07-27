using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DVarr.Data.Entities;

namespace DVarr.Services.Ingest;

/// <summary>
/// Talks to a single provider credential's Xtream Codes API (player_api.php). Each
/// credential = exactly one concurrent stream, so the recorder pulls the direct .ts
/// URL via the tuner pool (D3); this client is only for discovery/auth/EPG.
/// </summary>
public sealed class XtreamClient
{
    private readonly HttpClient _http;
    private readonly ILogger<XtreamClient> _log;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public XtreamClient(HttpClient http, ILogger<XtreamClient> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>The player User-Agent sent whenever a source has none configured. Providers routinely reject
    /// requests without a recognisable player UA, so EVERY provider-facing call (discovery, EPG, recorder,
    /// preview) must fall back to this same value — never send a blank/default-client UA.</summary>
    public const string DefaultUserAgent = "VLC/3.0.18 LibVLC/3.0.18";

    public string BaseUrl(ProviderSource s)
    {
        var proto = string.IsNullOrWhiteSpace(s.ServerProtocol) ? "http" : s.ServerProtocol;
        var host = s.BaseUrl.Trim().TrimEnd('/');
        host = host.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("http://", "", StringComparison.OrdinalIgnoreCase);
        var port = proto.Equals("https", StringComparison.OrdinalIgnoreCase) && s.HttpsPort is > 0
            ? s.HttpsPort!.Value
            : s.Port;
        var portPart = port > 0 ? $":{port}" : "";
        return $"{proto}://{host}{portPart}";
    }

    private string Api(ProviderSource s, string query)
        => $"{BaseUrl(s)}/player_api.php?username={Uri.EscapeDataString(s.Username)}&password={Uri.EscapeDataString(s.Password)}{query}";

    /// <summary>Direct .ts URL for the recorder (docs/05 §5.4, D3) — fetched straight from the provider.
    /// Kept for callers that explicitly want MPEG-TS; format-aware callers use <see cref="StreamUrl"/>.</summary>
    public string StreamTsUrl(ProviderSource s, int streamId)
        => $"{BaseUrl(s)}/live/{Uri.EscapeDataString(s.Username)}/{Uri.EscapeDataString(s.Password)}/{streamId}.ts";

    /// <summary>The container the live URL should ask for on this source: "ts" or "m3u8". Honours the source's
    /// StreamFormat ("ts"/"hls" force it); "auto" prefers .ts when the provider's allowed_output_formats include it
    /// (the historical default), else HLS. A proxy that serves HLS-only (community case: recording died because
    /// DVarr always built .ts URLs) either advertises m3u8-only or is forced with StreamFormat=hls.</summary>
    public static string EffectiveStreamExt(ProviderSource s)
    {
        var fmt = (s.StreamFormat ?? "auto").Trim().ToLowerInvariant();
        if (fmt == "ts") return "ts";
        if (fmt is "hls" or "m3u8") return "m3u8";
        // Exact token match, not substring — "rtsp".Contains("ts") is true and would wrongly keep a
        // genuinely m3u8-only lineup on .ts.
        var allowed = (s.AllowedOutputFormats ?? "").ToLowerInvariant()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Length > 0 && !allowed.Contains("ts") && allowed.Contains("m3u8")) return "m3u8";
        return "ts";
    }

    /// <summary>Live stream URL in the source's effective container (.ts or .m3u8).</summary>
    public string StreamUrl(ProviderSource s, int streamId)
        => $"{BaseUrl(s)}/live/{Uri.EscapeDataString(s.Username)}/{Uri.EscapeDataString(s.Password)}/{streamId}.{EffectiveStreamExt(s)}";

    // ---- Catch-up (tv_archive) ----
    // Xtream providers answer one of two archive URL shapes; DVarr tries the preferred/probed one first and the
    // recorder's ladder falls back to the other. The `start` stamp is in the PROVIDER's own timezone
    // (server_info.timezone, stored on the source at auth) — not UTC.

    public const string ShapeTimeshiftPhp = "timeshift_php";
    public const string ShapeTimeshiftPath = "timeshift_path";

    /// <summary>Convert a UTC epoch to the provider's local wall clock for a timeshift `start` stamp.
    /// Unknown/invalid zone → UTC (the most common server default).</summary>
    public static DateTime ToProviderLocal(ProviderSource s, long utcEpoch)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(utcEpoch).UtcDateTime;
        var tz = (s.Timezone ?? "").Trim();
        if (tz.Length == 0) return utc;
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(tz)); }
        catch { return utc; }
    }

    /// <summary>Archive pull URL for one shape. Duration is rounded UP to whole minutes (a truncated request
    /// would clip the end of the pull).</summary>
    public string TimeshiftUrl(ProviderSource s, int streamId, long startUtc, int durationS, string shape)
        => TimeshiftUrlLocal(s, streamId, ToProviderLocal(s, startUtc), durationS, shape);

    /// <summary>Archive pull URL from an ALREADY provider-local start. Chunked pulls convert the pull's base
    /// start once and add plain minute offsets per chunk — converting each chunk independently would collide
    /// two chunks onto the same wall-clock stamp across a DST fall-back hour (archive indexes are wall-clock
    /// linear, so linear offsets from one converted base are the faithful mapping).</summary>
    public string TimeshiftUrlLocal(ProviderSource s, int streamId, DateTime providerLocalStart, int durationS, string shape)
    {
        var stamp = providerLocalStart.ToString("yyyy-MM-dd:HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        var minutes = Math.Max(1, (durationS + 59) / 60);
        return shape == ShapeTimeshiftPath
            ? $"{BaseUrl(s)}/timeshift/{Uri.EscapeDataString(s.Username)}/{Uri.EscapeDataString(s.Password)}/{minutes}/{stamp}/{streamId}.ts"
            : $"{BaseUrl(s)}/streaming/timeshift.php?username={Uri.EscapeDataString(s.Username)}&password={Uri.EscapeDataString(s.Password)}&stream={streamId}&start={stamp}&duration={minutes}";
    }

    /// <summary>Both archive shapes for this pull, the source's probed/known shape first — the recorder's
    /// fallback ladder walks to the alternate shape if the first can't be opened.</summary>
    public List<string> TimeshiftUrlCandidates(ProviderSource s, int streamId, long startUtc, int durationS)
    {
        var first = s.CatchupShape == ShapeTimeshiftPath ? ShapeTimeshiftPath : ShapeTimeshiftPhp;
        var second = first == ShapeTimeshiftPath ? ShapeTimeshiftPhp : ShapeTimeshiftPath;
        var urls = new List<string> { TimeshiftUrl(s, streamId, startUtc, durationS, first) };
        if (string.IsNullOrEmpty(s.CatchupShape)) urls.Add(TimeshiftUrl(s, streamId, startUtc, durationS, second));
        return urls;
    }

    public Task<XtreamAuthResponse?> AuthAsync(ProviderSource s, CancellationToken ct = default)
        => GetAsync<XtreamAuthResponse>(Api(s, ""), s.UserAgent, ct);

    public async Task<List<XtreamLiveStream>> GetLiveStreamsAsync(ProviderSource s, CancellationToken ct = default)
        => await GetAsync<List<XtreamLiveStream>>(Api(s, "&action=get_live_streams"), s.UserAgent, ct) ?? new();

    public async Task<List<XtreamCategory>> GetLiveCategoriesAsync(ProviderSource s, CancellationToken ct = default)
        => await GetAsync<List<XtreamCategory>>(Api(s, "&action=get_live_categories"), s.UserAgent, ct) ?? new();

    public Task<XtreamShortEpgResponse?> GetShortEpgAsync(ProviderSource s, int streamId, int limit, CancellationToken ct = default)
        => GetAsync<XtreamShortEpgResponse>(Api(s, $"&action=get_short_epg&stream_id={streamId}&limit={limit}"), s.UserAgent, ct);

    private async Task<T?> GetAsync<T>(string url, string? userAgent, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Honour the source's configured UA (some providers gate EVERY call on it, not just streams); VLC default otherwise.
        req.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
    }

    /// <summary>Open the EPG XMLTV stream — the external override URL if set, else the provider's xmltv.php.</summary>
    public Task<Stream> OpenEpgAsync(ProviderSource s, CancellationToken ct = default)
    {
        var url = (s.EpgOverride && !string.IsNullOrWhiteSpace(s.EpgUrl))
            ? s.EpgUrl!
            : $"{BaseUrl(s)}/xmltv.php?username={Uri.EscapeDataString(s.Username)}&password={Uri.EscapeDataString(s.Password)}";
        return OpenUrlAsync(url, s.UserAgent, ct);
    }

    /// <summary>
    /// GET a URL and return a readable, STREAMING stream — gunzipped if the body is gzip. Never buffers
    /// the whole response (provider XMLTV for a large lineup can be hundreds of MB). Content-Encoding gzip
    /// is handled by the handler's AutomaticDecompression; body-gzip (.xml.gz) is detected by sniffing the
    /// first two bytes via a tiny pushback stream.
    /// </summary>
    public async Task<Stream> OpenUrlAsync(string url, string? userAgent, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var net = await resp.Content.ReadAsStreamAsync(ct);

        var lead = new byte[2];
        var got = 0;
        while (got < 2)
        {
            var n = await net.ReadAsync(lead.AsMemory(got, 2 - got), ct);
            if (n == 0) break;
            got += n;
        }
        Stream stream = new LeadingBytesStream(lead, got, net);
        return got == 2 && lead[0] == 0x1f && lead[1] == 0x8b
            ? new GZipStream(stream, CompressionMode.Decompress)
            : stream;
    }

    /// <summary>Read-only stream that serves a few peeked bytes first, then the underlying stream — lets us
    /// sniff the gzip magic without consuming or buffering the response.</summary>
    private sealed class LeadingBytesStream : Stream
    {
        private readonly byte[] _lead;
        private readonly int _len;
        private readonly Stream _inner;
        private int _pos;

        public LeadingBytesStream(byte[] lead, int len, Stream inner) { _lead = lead; _len = len; _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c)
        {
            if (_pos < _len) { var n = Math.Min(c, _len - _pos); Array.Copy(_lead, _pos, b, o, n); _pos += n; return n; }
            return _inner.Read(b, o, c);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < _len) { var n = Math.Min(buffer.Length, _len - _pos); _lead.AsSpan(_pos, n).CopyTo(buffer.Span); _pos += n; return n; }
            return await _inner.ReadAsync(buffer, ct);
        }
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    public static string DecodeBase64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return s; }
    }
}
