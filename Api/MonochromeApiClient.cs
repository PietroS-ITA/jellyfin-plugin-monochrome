using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Monochrome.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Api;

/// <summary>
/// Client for interacting with Monochrome and TIDAL APIs to retrieve metadata and stream audio.
/// </summary>
public class MonochromeApiClient
{
    private const string BrowserClientId = "txNoH4kkV41MfH25";
    private const string BrowserClientSecret = "dQjy0MinCEvxi1O4UmxvxWnDjt4cgHBPw8ll6nYBk98=";
    private const string AuthTokenUrl = "https://auth.tidal.com/v1/oauth2/token";
    private const string TidalApiBase = "https://api.tidal.com/v1";

    private readonly HttpClient _httpClient;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<MonochromeApiClient> _logger;
    private readonly IMediaEncoder? _mediaEncoder;
    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _trackDownloadLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MonochromeApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client instance.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="mediaEncoder">The media encoder instance.</param>
    public MonochromeApiClient(
        HttpClient httpClient,
        IApplicationPaths applicationPaths,
        ILogger<MonochromeApiClient> logger,
        IMediaEncoder? mediaEncoder = null)
    {
        _httpClient = httpClient;
        _applicationPaths = applicationPaths;
        _logger = logger;
        _mediaEncoder = mediaEncoder;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        }
    }

    private PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Obtains a valid access token, utilizing caching and refreshing when near expiration.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(Config.CustomToken))
        {
            return Config.CustomToken.Trim();
        }

        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _cachedToken;
            }

            _logger.LogInformation("Requesting fresh TIDAL client credentials token...");

            using var request = new HttpRequestMessage(HttpMethod.Post, AuthTokenUrl);
            var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BrowserClientId}:{BrowserClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var tokenData = JsonSerializer.Deserialize<TidalTokenResponse>(json);

            if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
            {
                throw new InvalidOperationException("Received empty access token from TIDAL auth server.");
            }

            _cachedToken = tokenData.AccessToken;
            var expiresInSeconds = tokenData.ExpiresIn > 60 ? tokenData.ExpiresIn - 60 : tokenData.ExpiresIn;
            _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresInSeconds);

            _logger.LogInformation("TIDAL access token acquired successfully, valid for {Seconds} seconds.", tokenData.ExpiresIn);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Searches for tracks, albums, and artists matching a query.
    /// </summary>
    public async Task<TidalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var limit = Config.SearchLimit > 0 ? Config.SearchLimit : 25;

        var normalizedQuery = query.Replace('+', ' ').Trim();
        while (normalizedQuery.Contains("  "))
        {
            normalizedQuery = normalizedQuery.Replace("  ", " ");
        }

        var url = $"{TidalApiBase}/search?query={Uri.EscapeDataString(normalizedQuery)}&limit={limit}&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TidalSearchResponse>(json);

        return result ?? new TidalSearchResponse();
    }

    /// <summary>
    /// Gets track details by ID.
    /// </summary>
    public async Task<TidalTrackItem?> GetTrackAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/tracks/{trackId}?countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TidalTrackItem>(json);
    }

    /// <summary>
    /// Gets album details by ID.
    /// </summary>
    public async Task<TidalAlbumRef?> GetAlbumAsync(long albumId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/albums/{albumId}?countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TidalAlbumRef>(json);
    }

    /// <summary>
    /// Gets album items (tracks) by album ID.
    /// </summary>
    public async Task<List<TidalTrackItem>> GetAlbumTracksAsync(long albumId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/albums/{albumId}/items?limit=100&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var list = new List<TidalTrackItem>();

        if (doc.RootElement.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElem.EnumerateArray())
            {
                if (item.TryGetProperty("item", out var trackElem))
                {
                    var track = JsonSerializer.Deserialize<TidalTrackItem>(trackElem.GetRawText());
                    if (track != null)
                    {
                        list.Add(track);
                    }
                }
                else
                {
                    var track = JsonSerializer.Deserialize<TidalTrackItem>(item.GetRawText());
                    if (track != null)
                    {
                        list.Add(track);
                    }
                }
            }
        }

        return list;
    }

    /// <summary>
    /// Gets artist details by ID.
    /// </summary>
    public async Task<TidalArtistRef?> GetArtistAsync(long artistId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/artists/{artistId}?countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TidalArtistRef>(json);
    }

    /// <summary>
    /// Gets top tracks for an artist.
    /// </summary>
    public async Task<List<TidalTrackItem>> GetArtistTopTracksAsync(long artistId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/artists/{artistId}/toptracks?limit=25&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TidalPagedList<TidalTrackItem>>(json);

        return result?.Items ?? new List<TidalTrackItem>();
    }

    /// <summary>
    /// Gets albums for an artist.
    /// </summary>
    public async Task<List<TidalAlbumRef>> GetArtistAlbumsAsync(long artistId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/artists/{artistId}/albums?limit=50&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TidalPagedList<TidalAlbumRef>>(json);

        return result?.Items ?? new List<TidalAlbumRef>();
    }

    /// <summary>
    /// Gets track radio (recommended similar tracks) by track ID.
    /// </summary>
    public async Task<List<TidalTrackItem>> GetTrackRadioAsync(long trackId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/tracks/{trackId}/radio?limit={limit}&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TidalTrackItem>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TidalPagedList<TidalTrackItem>>(json);

        return result?.Items ?? new List<TidalTrackItem>();
    }

    /// <summary>
    /// Gets artist radio (recommended tracks for an artist) by artist ID.
    /// </summary>
    public async Task<List<TidalTrackItem>> GetArtistRadioAsync(long artistId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var url = $"{TidalApiBase}/artists/{artistId}/radio?limit={limit}&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new List<TidalTrackItem>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TidalPagedList<TidalTrackItem>>(json);

        return result?.Items ?? new List<TidalTrackItem>();
    }

    /// <summary>
    /// Resolves playable audio stream URL for a given track.
    /// </summary>
    public async Task<ResolvedStream> ResolveTrackStreamAsync(long trackId, string? qualityOverride = null, CancellationToken cancellationToken = default)
    {
        var quality = qualityOverride ?? Config.AudioQuality ?? "LOSSLESS";

        // Candidate HiFi / Monochrome instances (full stream resolvers)
        var candidateEndpoints = new List<string>();
        const string DedicatedWorkerInstance = "https://hifi-api-workers.orbmusic.workers.dev";
        candidateEndpoints.Add(DedicatedWorkerInstance);

        if (!string.IsNullOrWhiteSpace(Config.ApiBaseUrl) && !candidateEndpoints.Contains(Config.ApiBaseUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
        {
            candidateEndpoints.Add(Config.ApiBaseUrl.TrimEnd('/'));
        }

        // 1. Try HiFi / Monochrome instances first (gives full lossless FLAC/AAC streams)
        if (!Config.UseDirectTidalApi || string.IsNullOrWhiteSpace(Config.CustomToken))
        {
            foreach (var baseUrl in candidateEndpoints)
            {
                try
                {
                    var hifiUrl = $"{baseUrl}/track?id={trackId}&quality={quality}";
                    using var hifiReq = new HttpRequestMessage(HttpMethod.Get, hifiUrl);
                    hifiReq.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                    using var hifiRes = await _httpClient.SendAsync(hifiReq, cancellationToken).ConfigureAwait(false);
                    if (hifiRes.IsSuccessStatusCode)
                    {
                        var hifiJson = await hifiRes.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(hifiJson);
                        JsonElement dataElem = doc.RootElement;
                        if (doc.RootElement.TryGetProperty("data", out var subData))
                        {
                            dataElem = subData;
                        }

                        var playback = JsonSerializer.Deserialize<TidalPlaybackInfo>(dataElem.GetRawText());
                        if (playback != null && !string.IsNullOrEmpty(playback.Manifest))
                        {
                            var stream = ParseManifest(playback);
                            if (stream != null && (!stream.IsDash || stream.DashSegmentUrls.Count > 8))
                            {
                                _logger.LogInformation("Successfully resolved full audio stream for track {TrackId} via {Instance} ({Quality}, {Segments} segments)",
                                    trackId, baseUrl, stream.Quality, stream.DashSegmentUrls.Count);
                                return stream;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve stream via instance {Url}, trying next...", baseUrl);
                }
            }
        }

        // 2. Direct TIDAL API playbackinfo resolution (fallback or when explicitly configured)
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var countryCode = string.IsNullOrWhiteSpace(Config.CountryCode) ? "IT" : Config.CountryCode.ToUpperInvariant();
        var pbUrl = $"{TidalApiBase}/tracks/{trackId}/playbackinfo?audioquality={quality}&playbackmode=STREAM&assetpresentation=FULL&countryCode={countryCode}";

        using var request = new HttpRequestMessage(HttpMethod.Get, pbUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var playbackInfo = JsonSerializer.Deserialize<TidalPlaybackInfo>(json);

        if (playbackInfo == null || string.IsNullOrEmpty(playbackInfo.Manifest))
        {
            throw new InvalidOperationException($"No playback manifest returned for track {trackId}.");
        }

        var resolved = ParseManifest(playbackInfo);
        if (resolved == null || string.IsNullOrEmpty(resolved.Url))
        {
            throw new InvalidOperationException($"Could not extract valid audio stream URL from track {trackId} manifest.");
        }

        return resolved;
    }

    private ResolvedStream? ParseManifest(TidalPlaybackInfo playbackInfo)
    {
        if (string.IsNullOrEmpty(playbackInfo.Manifest))
        {
            return null;
        }

        string decodedText;
        try
        {
            var bytes = Convert.FromBase64String(playbackInfo.Manifest);
            decodedText = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            decodedText = playbackInfo.Manifest;
        }

        // 1. Check for BTS JSON manifest (standard direct streaming format for lossless FLAC / AAC)
        try
        {
            var bts = JsonSerializer.Deserialize<TidalBtsManifest>(decodedText);
            if (bts?.Urls != null && bts.Urls.Count > 0)
            {
                var isFlac = (bts.MimeType?.Contains("flac", StringComparison.OrdinalIgnoreCase) ?? false)
                             || (bts.Codecs?.Contains("flac", StringComparison.OrdinalIgnoreCase) ?? false);

                return new ResolvedStream
                {
                    Url = bts.Urls[0],
                    Container = isFlac ? "flac" : "m4a",
                    Codec = isFlac ? "flac" : "aac",
                    BitDepth = playbackInfo.BitDepth,
                    SampleRate = playbackInfo.SampleRate,
                    Quality = playbackInfo.AudioQuality ?? "LOSSLESS"
                };
            }
        }
        catch
        {
            // Not JSON BTS manifest, check for DASH or direct URL below
        }

        // 2. Check for MPEG-DASH XML
        if (decodedText.Contains("<MPD", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var doc = XDocument.Parse(decodedText);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                // Check for SegmentTemplate (TIDAL fMP4 DASH stream)
                var segTemplate = doc.Descendants(ns + "SegmentTemplate").FirstOrDefault();
                if (segTemplate != null)
                {
                    var initAttr = segTemplate.Attribute("initialization")?.Value;
                    var mediaAttr = segTemplate.Attribute("media")?.Value;
                    var startNumberStr = segTemplate.Attribute("startNumber")?.Value ?? "1";
                    int.TryParse(startNumberStr, out var startNumber);
                    if (startNumber <= 0)
                    {
                        startNumber = 1;
                    }

                    var sElements = doc.Descendants(ns + "S").ToList();
                    int totalSegments = 0;
                    foreach (var s in sElements)
                    {
                        var rAttr = s.Attribute("r")?.Value;
                        int repeat = 0;
                        if (!string.IsNullOrEmpty(rAttr) && int.TryParse(rAttr, out var rVal))
                        {
                            repeat = rVal;
                        }

                        totalSegments += 1 + repeat;
                    }

                    if (!string.IsNullOrEmpty(initAttr) && !string.IsNullOrEmpty(mediaAttr) && totalSegments > 0)
                    {
                        var segmentUrls = new List<string>(totalSegments);
                        for (int i = startNumber; i < startNumber + totalSegments; i++)
                        {
                            segmentUrls.Add(mediaAttr.Replace("$Number$", i.ToString()));
                        }

                        var rep = doc.Descendants(ns + "Representation").FirstOrDefault();
                        var codecs = rep?.Attribute("codecs")?.Value ?? "flac";
                        var isFlac = codecs.Contains("flac", StringComparison.OrdinalIgnoreCase);

                        return new ResolvedStream
                        {
                            Url = segmentUrls[0],
                            Container = "mp4",
                            Codec = isFlac ? "flac" : "aac",
                            BitDepth = playbackInfo.BitDepth,
                            SampleRate = playbackInfo.SampleRate,
                            Quality = playbackInfo.AudioQuality ?? "LOSSLESS",
                            IsDash = true,
                            DashInitUrl = initAttr,
                            DashSegmentUrls = segmentUrls
                        };
                    }
                }

                // Look for BaseURL inside Representation or AdaptationSet
                var baseUrlElem = doc.Descendants(ns + "BaseURL").FirstOrDefault();
                if (baseUrlElem != null && !string.IsNullOrWhiteSpace(baseUrlElem.Value))
                {
                    var dashUrl = baseUrlElem.Value.Trim();
                    return new ResolvedStream
                    {
                        Url = dashUrl,
                        Container = "mp4",
                        Codec = "flac",
                        BitDepth = playbackInfo.BitDepth,
                        SampleRate = playbackInfo.SampleRate,
                        Quality = playbackInfo.AudioQuality ?? "LOSSLESS"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse XML MPD manifest.");
            }
        }

        // 3. Fallback regex search for direct HTTP/HTTPS URL
        var match = System.Text.RegularExpressions.Regex.Match(decodedText, @"https?://[^\s""'<>\\]+");
        if (match.Success)
        {
            var matchedUrl = match.Value;
            var isFlac = matchedUrl.Contains(".flac", StringComparison.OrdinalIgnoreCase);
            return new ResolvedStream
            {
                Url = matchedUrl,
                Container = isFlac ? "flac" : "m4a",
                Codec = isFlac ? "flac" : "aac",
                BitDepth = playbackInfo.BitDepth,
                SampleRate = playbackInfo.SampleRate,
                Quality = playbackInfo.AudioQuality ?? "LOSSLESS"
            };
        }

        return null;
    }

    /// <summary>
    /// Ensures that the audio track is downloaded and cached locally as a complete, playable audio file on the Jellyfin server.
    /// Returns the absolute path to the local audio file (.flac or .m4a).
    /// </summary>
    public async Task<string> EnsureTrackCachedAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var cacheDir = Path.Combine(_applicationPaths.CachePath, "monochrome");
        Directory.CreateDirectory(cacheDir);

        var flacFile = Path.Combine(cacheDir, $"{trackId}.flac");
        var m4aFile = Path.Combine(cacheDir, $"{trackId}.m4a");
        var legacyMp4File = Path.Combine(cacheDir, $"{trackId}.mp4");

        // 1. Check if already cached as native FLAC (> 4MB)
        if (File.Exists(flacFile))
        {
            var len = new FileInfo(flacFile).Length;
            if (len > 4L * 1024L * 1024L)
            {
                return flacFile;
            }

            _logger.LogInformation("Cached FLAC for track {TrackId} is small ({Bytes} bytes), deleting to re-cache full stream...", trackId, len);
            try { File.Delete(flacFile); } catch { }
        }

        // 2. Check if already cached as M4A (> 2MB)
        if (File.Exists(m4aFile))
        {
            var len = new FileInfo(m4aFile).Length;
            if (len > 2L * 1024L * 1024L)
            {
                return m4aFile;
            }

            try { File.Delete(m4aFile); } catch { }
        }

        // 3. If legacy MP4 exists (> 4MB), attempt fast remux to FLAC right away
        if (File.Exists(legacyMp4File))
        {
            var len = new FileInfo(legacyMp4File).Length;
            if (len > 4L * 1024L * 1024L)
            {
                if (await RemuxToNativeAudioAsync(legacyMp4File, flacFile, "flac", cancellationToken).ConfigureAwait(false))
                {
                    try { File.Delete(legacyMp4File); } catch { }
                    return flacFile;
                }
            }
            else
            {
                try { File.Delete(legacyMp4File); } catch { }
            }
        }

        var trackLock = _trackDownloadLocks.GetOrAdd(trackId, _ => new SemaphoreSlim(1, 1));
        await trackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(flacFile) && new FileInfo(flacFile).Length > 4L * 1024L * 1024L)
            {
                return flacFile;
            }

            if (File.Exists(m4aFile) && new FileInfo(m4aFile).Length > 2L * 1024L * 1024L)
            {
                return m4aFile;
            }

            _logger.LogInformation("Resolving stream for track {TrackId} to cache locally...", trackId);
            var resolved = await ResolveTrackStreamAsync(trackId, cancellationToken: cancellationToken).ConfigureAwait(false);

            var isFlac = resolved.Codec.Contains("flac", StringComparison.OrdinalIgnoreCase)
                         || resolved.Quality.Equals("LOSSLESS", StringComparison.OrdinalIgnoreCase)
                         || resolved.Quality.Equals("HI_RES", StringComparison.OrdinalIgnoreCase);
            var targetExt = isFlac ? "flac" : "m4a";
            var targetFile = isFlac ? flacFile : m4aFile;
            var tempRawFile = Path.Combine(cacheDir, $"{trackId}.raw.tmp.{Guid.NewGuid():N}");

            if (resolved.IsDash && !string.IsNullOrEmpty(resolved.DashInitUrl) && resolved.DashSegmentUrls.Count > 0)
            {
                _logger.LogInformation("Downloading {Count} DASH segments for track {TrackId}...", resolved.DashSegmentUrls.Count, trackId);
                using (var fileStream = new FileStream(tempRawFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    // 1. Write initialization segment
                    await DownloadSegmentWithRetryAsync(resolved.DashInitUrl, fileStream, cancellationToken).ConfigureAwait(false);

                    // 2. Write media segments sequentially
                    foreach (var segUrl in resolved.DashSegmentUrls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await DownloadSegmentWithRetryAsync(segUrl, fileStream, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // Direct file download
                _logger.LogInformation("Downloading direct stream for track {TrackId}...", trackId);
                using (var fileStream = new FileStream(tempRawFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await DownloadSegmentWithRetryAsync(resolved.Url, fileStream, cancellationToken).ConfigureAwait(false);
                }
            }

            // Remux raw DASH fMP4 container to native FLAC / M4A using FFmpeg so HTML5 <audio> can direct-play it
            bool remuxed = await RemuxToNativeAudioAsync(tempRawFile, targetFile, targetExt, cancellationToken).ConfigureAwait(false);
            if (remuxed && File.Exists(targetFile))
            {
                try { File.Delete(tempRawFile); } catch { }
            }
            else
            {
                // Fallback: move raw file directly
                File.Move(tempRawFile, targetFile, true);
            }

            _logger.LogInformation("Track {TrackId} successfully cached as {TargetExt} at {Path} ({Bytes} bytes)", trackId, targetExt, targetFile, new FileInfo(targetFile).Length);

            // Clean older cached tracks in background if cache exceeds limit
            _ = Task.Run(() => CleanCacheIfNecessary(cacheDir));

            // Pre-fetch lyrics in background and cache .lrc
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetTrackLyricsAsync(trackId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            });

            return targetFile;
        }
        finally
        {
            trackLock.Release();
        }
    }

    /// <summary>
    /// Remuxes an audio container (e.g. fragmented MP4) to a standard native audio file (.flac or .m4a) using FFmpeg.
    /// </summary>
    public async Task<bool> RemuxToNativeAudioAsync(string inputFile, string outputFile, string targetFormat, CancellationToken cancellationToken = default)
    {
        var ffmpeg = ResolveFfmpegPath();
        var tempOut = outputFile + $".remux.{Guid.NewGuid():N}";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -i \"{inputFile}\" -c copy \"{tempOut}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode == 0 && File.Exists(tempOut) && new FileInfo(tempOut).Length > 1024)
            {
                File.Move(tempOut, outputFile, true);
                _logger.LogInformation("Remuxed {Input} to native {Ext} via {Ffmpeg} ({Bytes} bytes)", Path.GetFileName(inputFile), targetFormat, ffmpeg, new FileInfo(outputFile).Length);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remuxing to native {Ext} via {Ffmpeg} failed", targetFormat, ffmpeg);
        }
        finally
        {
            try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch { }
        }

        return false;
    }

    private string ResolveFfmpegPath()
    {
        if (!string.IsNullOrEmpty(_mediaEncoder?.EncoderPath) && File.Exists(_mediaEncoder.EncoderPath))
        {
            return _mediaEncoder.EncoderPath;
        }

        var candidates = new[]
        {
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "ffmpeg"
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return "ffmpeg";
    }

    private async Task DownloadSegmentWithRetryAsync(string url, Stream destination, CancellationToken cancellationToken, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                await res.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Transient error downloading media segment (attempt {Attempt}/{Max}), retrying...", attempt, maxRetries);
                await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void CleanCacheIfNecessary(string cacheDir)
    {
        try
        {
            var dir = new DirectoryInfo(cacheDir);
            if (!dir.Exists)
            {
                return;
            }

            var files = dir.GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
            long totalSize = files.Sum(f => f.Length);
            const long maxCacheBytes = 1500L * 1024L * 1024L; // 1.5 GB limit

            if (totalSize > maxCacheBytes)
            {
                foreach (var f in files)
                {
                    if (totalSize <= maxCacheBytes * 0.8)
                    {
                        break;
                    }

                    try
                    {
                        var len = f.Length;
                        f.Delete();
                        totalSize -= len;
                    }
                    catch
                    {
                        // Ignore individual file deletion errors
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache cleanup encountered an issue.");
        }
    }

    /// <summary>
    /// Gets cover artwork URL for an album or track.
    /// </summary>
    public static string? GetCoverUrl(string? coverId, int size = 640)
    {
        if (string.IsNullOrWhiteSpace(coverId))
        {
            return null;
        }

        // TIDAL cover IDs are formatted as uuid where '-' becomes '/' in the URL
        var path = coverId.Replace("-", "/", StringComparison.Ordinal);
        return $"https://resources.tidal.com/images/{path}/{size}x{size}.jpg";
    }

    /// <summary>
    /// Gets artist profile picture URL.
    /// </summary>
    public static string? GetArtistPictureUrl(string? pictureId, int size = 640)
    {
        if (string.IsNullOrWhiteSpace(pictureId))
        {
            return null;
        }

        var path = pictureId.Replace("-", "/", StringComparison.Ordinal);
        return $"https://resources.tidal.com/images/{path}/{size}x{size}.jpg";
    }

    /// <summary>
    /// Retrieves synchronized (or plain) lyrics for a track, caching the .lrc file locally.
    /// </summary>
    public async Task<TrackLyricsResult?> GetTrackLyricsAsync(long trackId, string? title = null, string? artist = null, CancellationToken cancellationToken = default)
    {
        var cacheDir = Path.Combine(_applicationPaths.CachePath, "monochrome");
        var lrcFile = Path.Combine(cacheDir, $"{trackId}.lrc");

        // 1. Check if cached .lrc file already exists
        if (File.Exists(lrcFile))
        {
            try
            {
                var cachedLrc = await File.ReadAllTextAsync(lrcFile, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cachedLrc))
                {
                    var cachedResult = ParseLrc(cachedLrc, trackId, title, artist);
                    if (cachedResult.Lines.Count > 0)
                    {
                        return cachedResult;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed reading cached lyrics for track {TrackId}", trackId);
            }
        }

        // 2. Query TIDAL / Monochrome HiFi API for lyrics
        try
        {
            var hifiUrl = $"https://hifi-api-workers.orbmusic.workers.dev/lyrics?id={trackId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, hifiUrl);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            using var res = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("lyrics", out var lyricsObj))
                {
                    string? subtitles = lyricsObj.TryGetProperty("subtitles", out var subProp) ? subProp.GetString() : null;
                    string? plain = lyricsObj.TryGetProperty("lyrics", out var lyrProp) ? lyrProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(subtitles))
                    {
                        var result = ParseLrc(subtitles, trackId, title, artist);
                        result.PlainLyrics = plain ?? result.PlainLyrics;

                        // Save cache file
                        try
                        {
                            Directory.CreateDirectory(cacheDir);
                            await File.WriteAllTextAsync(lrcFile, subtitles, cancellationToken).ConfigureAwait(false);
                        }
                        catch { }

                        return result;
                    }
                    else if (!string.IsNullOrWhiteSpace(plain))
                    {
                        return new TrackLyricsResult
                        {
                            TrackId = trackId,
                            Title = title ?? string.Empty,
                            Artist = artist ?? string.Empty,
                            HasSynced = false,
                            PlainLyrics = plain
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HiFi lyrics resolution failed for track {TrackId}", trackId);
        }

        // 3. Fallback: Query LRCLIB.net for synchronized lyrics
        if (!string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var queryArtist = !string.IsNullOrWhiteSpace(artist) ? $"&artist_name={Uri.EscapeDataString(artist)}" : string.Empty;
                var lrclibUrl = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}{queryArtist}";
                using var req = new HttpRequestMessage(HttpMethod.Get, lrclibUrl);
                req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
                using var res = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    string? synced = doc.RootElement.TryGetProperty("syncedLyrics", out var sProp) ? sProp.GetString() : null;
                    string? plain = doc.RootElement.TryGetProperty("plainLyrics", out var pProp) ? pProp.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(synced))
                    {
                        var result = ParseLrc(synced, trackId, title, artist);
                        result.PlainLyrics = plain ?? result.PlainLyrics;

                        try
                        {
                            Directory.CreateDirectory(cacheDir);
                            await File.WriteAllTextAsync(lrcFile, synced, cancellationToken).ConfigureAwait(false);
                        }
                        catch { }

                        return result;
                    }
                    else if (!string.IsNullOrWhiteSpace(plain))
                    {
                        return new TrackLyricsResult
                        {
                            TrackId = trackId,
                            Title = title ?? string.Empty,
                            Artist = artist ?? string.Empty,
                            HasSynced = false,
                            PlainLyrics = plain
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LRCLIB lyrics resolution failed for track {TrackId}", trackId);
            }
        }

        return null;
    }

    /// <summary>
    /// Parses LRC content into a structured TrackLyricsResult.
    /// </summary>
    public static TrackLyricsResult ParseLrc(string lrcContent, long trackId = 0, string? title = null, string? artist = null)
    {
        var result = new TrackLyricsResult
        {
            TrackId = trackId,
            Title = title ?? string.Empty,
            Artist = artist ?? string.Empty,
            RawLrc = lrcContent
        };

        var lrcRegex = new Regex(@"\[(\d{1,2}):(\d{2})(?:\.(\d{2,3}))?\](.*)", RegexOptions.Compiled);
        var lines = new List<SyncedLyricLine>();
        var plainBuilder = new StringBuilder();

        using var reader = new StringReader(lrcContent);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var match = lrcRegex.Match(line);
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out var mins);
                int.TryParse(match.Groups[2].Value, out var secs);
                int ms = 0;
                if (match.Groups[3].Success)
                {
                    var msStr = match.Groups[3].Value;
                    if (msStr.Length == 2) msStr += "0";
                    int.TryParse(msStr, out ms);
                }

                double totalSeconds = (mins * 60) + secs + (ms / 1000.0);
                long ticks = (long)(totalSeconds * TimeSpan.TicksPerSecond);
                var text = match.Groups[4].Value.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    lines.Add(new SyncedLyricLine
                    {
                        Time = Math.Round(totalSeconds, 2),
                        Ticks = ticks,
                        Text = text
                    });
                    plainBuilder.AppendLine(text);
                }
            }
            else if (!line.StartsWith('[') && !string.IsNullOrWhiteSpace(line))
            {
                plainBuilder.AppendLine(line.Trim());
            }
        }

        result.Lines = lines.OrderBy(l => l.Time).ToList();
        result.HasSynced = result.Lines.Count > 0;
        result.PlainLyrics = plainBuilder.ToString().TrimEnd();

        return result;
    }
}

