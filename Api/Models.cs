using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Monochrome.Api;

/// <summary>
/// OAuth token response from TIDAL auth endpoint.
/// </summary>
public class TidalTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

/// <summary>
/// Artist reference.
/// </summary>
public class TidalArtistRef
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("picture")]
    public string? Picture { get; set; }
}

/// <summary>
/// Album reference embedded in tracks or search results.
/// </summary>
public class TidalAlbumRef
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("numberOfTracks")]
    public int? NumberOfTracks { get; set; }

    [JsonPropertyName("artists")]
    public List<TidalArtistRef>? Artists { get; set; }

    [JsonPropertyName("artist")]
    public TidalArtistRef? Artist { get; set; }
}

/// <summary>
/// Track item.
/// </summary>
public class TidalTrackItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("trackNumber")]
    public int TrackNumber { get; set; }

    [JsonPropertyName("volumeNumber")]
    public int VolumeNumber { get; set; } = 1;

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("artist")]
    public TidalArtistRef? Artist { get; set; }

    [JsonPropertyName("artists")]
    public List<TidalArtistRef>? Artists { get; set; }

    [JsonPropertyName("album")]
    public TidalAlbumRef? Album { get; set; }

    [JsonPropertyName("isrc")]
    public string? Isrc { get; set; }

    [JsonPropertyName("allowStreaming")]
    public bool AllowStreaming { get; set; } = true;
}

/// <summary>
/// Generic paginated response from v1 endpoints.
/// </summary>
public class TidalPagedList<T>
{
    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("totalNumberOfItems")]
    public int TotalNumberOfItems { get; set; }

    [JsonPropertyName("items")]
    public List<T> Items { get; set; } = new();
}

/// <summary>
/// Search response from v1 search endpoint.
/// </summary>
public class TidalSearchResponse
{
    [JsonPropertyName("tracks")]
    public TidalPagedList<TidalTrackItem>? Tracks { get; set; }

    [JsonPropertyName("albums")]
    public TidalPagedList<TidalAlbumRef>? Albums { get; set; }

    [JsonPropertyName("artists")]
    public TidalPagedList<TidalArtistRef>? Artists { get; set; }
}

/// <summary>
/// Playback information for a track.
/// </summary>
public class TidalPlaybackInfo
{
    [JsonPropertyName("trackId")]
    public long TrackId { get; set; }

    [JsonPropertyName("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonPropertyName("manifestMimeType")]
    public string? ManifestMimeType { get; set; }

    [JsonPropertyName("manifest")]
    public string? Manifest { get; set; }

    [JsonPropertyName("bitDepth")]
    public int? BitDepth { get; set; }

    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }
}

/// <summary>
/// Decoded BTS manifest.
/// </summary>
public class TidalBtsManifest
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("codecs")]
    public string? Codecs { get; set; }

    [JsonPropertyName("encryptionType")]
    public string? EncryptionType { get; set; }

    [JsonPropertyName("urls")]
    public List<string>? Urls { get; set; }
}

/// <summary>
/// Resolved stream details.
/// </summary>
public class ResolvedStream
{
    public string Url { get; set; } = string.Empty;
    public string Container { get; set; } = "flac";
    public string Codec { get; set; } = "flac";
    public int? BitDepth { get; set; }
    public int? SampleRate { get; set; }
    public string Quality { get; set; } = "LOSSLESS";
    public bool IsDash { get; set; }
    public string? DashInitUrl { get; set; }
    public List<string> DashSegmentUrls { get; set; } = new();
}

/// <summary>
/// A single line of synchronized lyrics.
/// </summary>
public class SyncedLyricLine
{
    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("ticks")]
    public long Ticks { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Parsed lyrics result for a track.
/// </summary>
public class TrackLyricsResult
{
    [JsonPropertyName("trackId")]
    public long TrackId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("hasSynced")]
    public bool HasSynced { get; set; }

    [JsonPropertyName("rawLrc")]
    public string RawLrc { get; set; } = string.Empty;

    [JsonPropertyName("plainLyrics")]
    public string PlainLyrics { get; set; } = string.Empty;

    [JsonPropertyName("lines")]
    public List<SyncedLyricLine> Lines { get; set; } = new();
}

