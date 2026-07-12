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

    /// <summary>Direct .ts URL for the recorder (docs/05 §5.4, D3) — fetched straight from the provider.</summary>
    public string StreamTsUrl(ProviderSource s, int streamId)
        => $"{BaseUrl(s)}/live/{Uri.EscapeDataString(s.Username)}/{Uri.EscapeDataString(s.Password)}/{streamId}.ts";

    public Task<XtreamAuthResponse?> AuthAsync(ProviderSource s, CancellationToken ct = default)
        => GetAsync<XtreamAuthResponse>(Api(s, ""), ct);

    public async Task<List<XtreamLiveStream>> GetLiveStreamsAsync(ProviderSource s, CancellationToken ct = default)
        => await GetAsync<List<XtreamLiveStream>>(Api(s, "&action=get_live_streams"), ct) ?? new();

    public async Task<List<XtreamCategory>> GetLiveCategoriesAsync(ProviderSource s, CancellationToken ct = default)
        => await GetAsync<List<XtreamCategory>>(Api(s, "&action=get_live_categories"), ct) ?? new();

    public Task<XtreamShortEpgResponse?> GetShortEpgAsync(ProviderSource s, int streamId, int limit, CancellationToken ct = default)
        => GetAsync<XtreamShortEpgResponse>(Api(s, $"&action=get_short_epg&stream_id={streamId}&limit={limit}"), ct);

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(DefaultUserAgent);
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
