using System.Text.Json.Serialization;

namespace DVarr.Services.Ingest;

// Xtream Codes player_api.php response shapes. Numeric fields frequently arrive as
// strings, so the client's JsonSerializerOptions sets AllowReadingFromString.

public sealed class XtreamAuthResponse
{
    [JsonPropertyName("user_info")] public XtreamUserInfo? UserInfo { get; set; }
    [JsonPropertyName("server_info")] public XtreamServerInfo? ServerInfo { get; set; }
}

public sealed class XtreamUserInfo
{
    [JsonPropertyName("auth")] public int Auth { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("exp_date")] public string? ExpDate { get; set; }
    [JsonPropertyName("is_trial")] public string? IsTrial { get; set; }
    [JsonPropertyName("active_cons")] public int? ActiveCons { get; set; }
    [JsonPropertyName("max_connections")] public int? MaxConnections { get; set; }
    [JsonPropertyName("allowed_output_formats")] public List<string>? AllowedOutputFormats { get; set; }
}

public sealed class XtreamServerInfo
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("port")] public string? Port { get; set; }
    [JsonPropertyName("https_port")] public string? HttpsPort { get; set; }
    [JsonPropertyName("server_protocol")] public string? ServerProtocol { get; set; }
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
}

public sealed class XtreamLiveStream
{
    [JsonPropertyName("num")] public int? Num { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("stream_id")] public int StreamId { get; set; }
    [JsonPropertyName("stream_icon")] public string? StreamIcon { get; set; }
    [JsonPropertyName("epg_channel_id")] public string? EpgChannelId { get; set; }
    [JsonPropertyName("category_id")] public string? CategoryId { get; set; }
    [JsonPropertyName("tv_archive")] public int TvArchive { get; set; }
    [JsonPropertyName("tv_archive_duration")] public int TvArchiveDuration { get; set; }
}

public sealed class XtreamCategory
{
    [JsonPropertyName("category_id")] public string? CategoryId { get; set; }
    [JsonPropertyName("category_name")] public string? CategoryName { get; set; }
}

public sealed class XtreamShortEpgResponse
{
    [JsonPropertyName("epg_listings")] public List<XtreamEpgListing>? EpgListings { get; set; }
}

public sealed class XtreamEpgListing
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }          // base64
    [JsonPropertyName("description")] public string? Description { get; set; } // base64
    [JsonPropertyName("start_timestamp")] public long? StartTimestamp { get; set; }
    [JsonPropertyName("stop_timestamp")] public long? StopTimestamp { get; set; }
}
