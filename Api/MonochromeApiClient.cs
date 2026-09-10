using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.Monochrome.Configuration;
using MediaBrowser.Common.Configuration;
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
    public MonochromeApiClient(
        HttpClient httpClient,
        IApplicationPaths applicationPaths,
        ILogger<MonochromeApiClient> logger)
    {
        _httpClient = httpClient;
        _applicationPaths = applicationPaths;
        _logger = logger;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) Jellyfin-Monochrome/1.0");
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

        var url = $"{TidalApiBase}/search?query={Uri.EscapeDataString(query)}&limit={limit}&countryCode={countryCode}";

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

        // Try Monochrome / HiFi instance if specified and not empty
        if (!string.IsNullOrWhiteSpace(Config.ApiBaseUrl) && !Config.UseDirectTidalApi)
        {
            try
            {
                var hifiUrl = $"{Config.ApiBaseUrl.TrimEnd('/')}/track?id={trackId}&quality={quality}";
                using var hifiReq = new HttpRequestMessage(HttpMethod.Get, hifiUrl);
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
                        if (stream != null)
                        {
                            return stream;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve stream via Monochrome instance {Url}, falling back to direct API.", Config.ApiBaseUrl);
            }
        }

        // Direct TIDAL API playbackinfo resolution
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
    /// Returns the absolute path to the local audio file (.mp4 or .flac).
    /// </summary>
    public async Task<string> EnsureTrackCachedAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var cacheDir = Path.Combine(_applicationPaths.CachePath, "monochrome");
        Directory.CreateDirectory(cacheDir);

        var cacheFile = Path.Combine(cacheDir, $"{trackId}.mp4");
        var flacFile = Path.Combine(cacheDir, $"{trackId}.flac");

        if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 1024)
        {
            return cacheFile;
        }

        if (File.Exists(flacFile) && new FileInfo(flacFile).Length > 1024)
        {
            return flacFile;
        }

        var trackLock = _trackDownloadLocks.GetOrAdd(trackId, _ => new SemaphoreSlim(1, 1));
        await trackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 1024)
            {
                return cacheFile;
            }

            if (File.Exists(flacFile) && new FileInfo(flacFile).Length > 1024)
            {
                return flacFile;
            }

            _logger.LogInformation("Resolving stream for track {TrackId} to cache locally...", trackId);
            var resolved = await ResolveTrackStreamAsync(trackId, cancellationToken: cancellationToken).ConfigureAwait(false);

            var isMp4 = resolved.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase) || resolved.IsDash;
            var targetFile = isMp4 ? cacheFile : Path.Combine(cacheDir, $"{trackId}.{resolved.Container}");
            var tempFile = $"{targetFile}.tmp.{Guid.NewGuid():N}";

            if (resolved.IsDash && !string.IsNullOrEmpty(resolved.DashInitUrl) && resolved.DashSegmentUrls.Count > 0)
            {
                _logger.LogInformation("Downloading {Count} DASH segments for track {TrackId}...", resolved.DashSegmentUrls.Count, trackId);
                using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    // 1. Write initialization segment
                    using var initReq = new HttpRequestMessage(HttpMethod.Get, resolved.DashInitUrl);
                    using var initRes = await _httpClient.SendAsync(initReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    initRes.EnsureSuccessStatusCode();
                    await initRes.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

                    // 2. Write media segments sequentially
                    foreach (var segUrl in resolved.DashSegmentUrls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var segReq = new HttpRequestMessage(HttpMethod.Get, segUrl);
                        using var segRes = await _httpClient.SendAsync(segReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        segRes.EnsureSuccessStatusCode();
                        await segRes.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // Direct file download
                _logger.LogInformation("Downloading direct stream for track {TrackId}...", trackId);
                using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                using var streamReq = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
                using var streamRes = await _httpClient.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                streamRes.EnsureSuccessStatusCode();
                await streamRes.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempFile, targetFile, true);
            _logger.LogInformation("Track {TrackId} successfully cached at {Path} ({Bytes} bytes)", trackId, targetFile, new FileInfo(targetFile).Length);

            // Clean older cached tracks in background if cache exceeds limit
            _ = Task.Run(() => CleanCacheIfNecessary(cacheDir));

            return targetFile;
        }
        finally
        {
            trackLock.Release();
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
}

