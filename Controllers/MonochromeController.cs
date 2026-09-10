using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Controllers;

/// <summary>
/// API Controller exposing Monochrome Music actions, stream resolution, search, and STRM export.
/// </summary>
[ApiController]
[Route("Monochrome")]
public class MonochromeController : ControllerBase
{
    private readonly MonochromeApiClient _apiClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MonochromeController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonochromeController"/> class.
    /// </summary>
    /// <param name="apiClient">The API client.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public MonochromeController(
        MonochromeApiClient apiClient,
        IHttpClientFactory httpClientFactory,
        ILogger<MonochromeController> logger)
    {
        _apiClient = apiClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stream resolver: redirects to or proxies the direct audio stream for a track.
    /// </summary>
    /// <param name="trackId">The numeric TIDAL track ID.</param>
    /// <param name="quality">Optional audio quality override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("Stream")]
    public async Task<IActionResult> GetStream(
        [FromQuery] long trackId,
        [FromQuery] string? quality = null,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { error = "Invalid trackId" });
        }

        try
        {
            var cachedFile = await _apiClient.EnsureTrackCachedAsync(trackId, cancellationToken).ConfigureAwait(false);
            var isMp4 = cachedFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var contentType = isMp4 ? "audio/mp4" : "audio/flac";

            return PhysicalFile(cachedFile, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve stream for track {TrackId}", trackId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Searches for music across tracks, albums, and artists.
    /// </summary>
    [HttpGet("Search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return BadRequest(new { error = "Query parameter 'q' is required." });
        }

        try
        {
            var results = await _apiClient.SearchAsync(q, cancellationToken).ConfigureAwait(false);
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query '{Query}'", q);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets album information and tracks.
    /// </summary>
    [HttpGet("Album")]
    public async Task<IActionResult> GetAlbum([FromQuery] long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest(new { error = "Invalid album ID." });
        }

        try
        {
            var album = await _apiClient.GetAlbumAsync(id, cancellationToken).ConfigureAwait(false);
            if (album == null)
            {
                return NotFound(new { error = "Album not found." });
            }

            var tracks = await _apiClient.GetAlbumTracksAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                Album = album,
                Tracks = tracks
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get album {AlbumId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets artist information and top tracks.
    /// </summary>
    [HttpGet("Artist")]
    public async Task<IActionResult> GetArtist([FromQuery] long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest(new { error = "Invalid artist ID." });
        }

        try
        {
            var artist = await _apiClient.GetArtistAsync(id, cancellationToken).ConfigureAwait(false);
            if (artist == null)
            {
                return NotFound(new { error = "Artist not found." });
            }

            var topTracks = await _apiClient.GetArtistTopTracksAsync(id, cancellationToken).ConfigureAwait(false);
            var albums = await _apiClient.GetArtistAlbumsAsync(id, cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                Artist = artist,
                TopTracks = topTracks,
                Albums = albums
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artist {ArtistId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Exports an album as .strm files and artwork into a specified or configured Jellyfin music library folder.
    /// </summary>
    [HttpPost("ExportStrm")]
    public async Task<IActionResult> ExportStrm(
        [FromQuery] long albumId,
        [FromQuery] string? targetDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (albumId <= 0)
        {
            return BadRequest(new { error = "Invalid albumId." });
        }

        var baseDir = !string.IsNullOrWhiteSpace(targetDirectory)
            ? targetDirectory
            : Plugin.Instance?.Configuration.StrmLibraryPath;

        if (string.IsNullOrWhiteSpace(baseDir))
        {
            return BadRequest(new { error = "No STRM library target directory specified or configured in plugin settings." });
        }

        try
        {
            var album = await _apiClient.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false);
            if (album == null)
            {
                return NotFound(new { error = "Album not found." });
            }

            var tracks = await _apiClient.GetAlbumTracksAsync(albumId, cancellationToken).ConfigureAwait(false);
            if (tracks.Count == 0)
            {
                return BadRequest(new { error = "Album contains no tracks." });
            }

            var artistName = SanitizePath(album.Artist?.Name ?? album.Artists?[0].Name ?? "Unknown Artist");
            var albumTitle = SanitizePath(album.Title);
            var albumDir = Path.Combine(baseDir, artistName, albumTitle);

            Directory.CreateDirectory(albumDir);

            // Determine server URL for .strm file stream endpoint
            var scheme = Request.Scheme;
            var host = Request.Host.Value;
            var baseUrl = $"{scheme}://{host}";

            // Write .strm file for each track
            var count = 0;
            foreach (var track in tracks)
            {
                var trackName = SanitizePath(track.Title);
                var fileName = $"{track.TrackNumber:D2} - {trackName}.strm";
                var filePath = Path.Combine(albumDir, fileName);

                var streamEndpoint = $"{baseUrl}/Monochrome/Stream?trackId={track.Id}";
                await System.IO.File.WriteAllTextAsync(filePath, streamEndpoint, cancellationToken).ConfigureAwait(false);
                count++;
            }

            // Download cover artwork if available
            var coverUrl = MonochromeApiClient.GetCoverUrl(album.Cover, 1280);
            if (!string.IsNullOrEmpty(coverUrl))
            {
                try
                {
                    var httpClient = _httpClientFactory.CreateClient();
                    var coverBytes = await httpClient.GetByteArrayAsync(coverUrl, cancellationToken).ConfigureAwait(false);
                    var coverPath = Path.Combine(albumDir, "cover.jpg");
                    await System.IO.File.WriteAllBytesAsync(coverPath, coverBytes, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download cover art for album {AlbumTitle}", albumTitle);
                }
            }

            return Ok(new
            {
                success = true,
                message = $"Successfully exported {count} tracks to {albumDir}",
                album = albumTitle,
                artist = artistName,
                path = albumDir,
                trackCount = count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting STRM for album {AlbumId}", albumId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the list of recent or saved searches.
    /// </summary>
    [HttpGet("RecentSearches")]
    public IActionResult GetRecentSearches()
    {
        var searches = Plugin.Instance?.Configuration.RecentSearches ?? new List<string>();
        return Ok(searches);
    }

    /// <summary>
    /// Adds a query to recent searches so it appears in the Search UI chips and in the Channel folder.
    /// </summary>
    [HttpPost("RecentSearch")]
    public IActionResult AddRecentSearch([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "Query cannot be empty" });
        }

        query = query.Trim();
        var config = Plugin.Instance?.Configuration;
        if (config != null)
        {
            config.RecentSearches ??= new List<string>();
            config.RecentSearches.RemoveAll(q => q.Equals(query, StringComparison.OrdinalIgnoreCase));
            config.RecentSearches.Insert(0, query);

            // Limit to 25 items
            if (config.RecentSearches.Count > 25)
            {
                config.RecentSearches.RemoveRange(25, config.RecentSearches.Count - 25);
            }

            Plugin.Instance?.SaveConfiguration();
        }

        return Ok(new { success = true, query });
    }

    /// <summary>
    /// Tests connectivity with TIDAL / Monochrome APIs.
    /// </summary>
    [HttpGet("TestConnection")]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            var token = await _apiClient.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                return Ok(new { success = false, message = "Could not obtain an access token." });
            }

            var search = await _apiClient.SearchAsync("test", cancellationToken).ConfigureAwait(false);
            var trackCount = search.Tracks?.Items.Count ?? 0;

            return Ok(new
            {
                success = true,
                message = $"Connected successfully! Token valid. Catalogue returned {trackCount} test results."
            });
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                message = ex.Message
            });
        }
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString().Trim();
    }
}

