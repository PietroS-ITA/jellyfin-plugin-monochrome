using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using Jellyfin.Plugin.Monochrome.Search;
using Jellyfin.Plugin.Monochrome.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Controllers;

/// <summary>
/// API Controller exposing Monochrome Music actions, stream resolution, search, radio, and STRM export.
/// </summary>
[ApiController]
[Route("Monochrome")]
public class MonochromeController : ControllerBase
{
    private readonly MonochromeApiClient _apiClient;
    private readonly MonochromeSearchProvider _searchProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly MonochromePlaybackManager? _playbackManager;
    private readonly ILogger<MonochromeController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonochromeController"/> class.
    /// </summary>
    public MonochromeController(
        MonochromeApiClient apiClient,
        MonochromeSearchProvider searchProvider,
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<MonochromeController> logger,
        MonochromePlaybackManager? playbackManager = null)
    {
        _apiClient = apiClient;
        _searchProvider = searchProvider;
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _playbackManager = playbackManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    [HttpGet("Configuration")]
    public IActionResult GetConfiguration()
    {
        if (Plugin.Instance == null)
        {
            return NotFound(new { error = "Plugin instance not initialized" });
        }

        return Ok(Plugin.Instance.Configuration);
    }

    /// <summary>
    /// Updates the plugin configuration and saves it to disk.
    /// </summary>
    [HttpPost("Configuration")]
    public IActionResult UpdateConfiguration([FromBody] Jellyfin.Plugin.Monochrome.Configuration.PluginConfiguration newConfig)
    {
        if (Plugin.Instance == null)
        {
            return NotFound(new { error = "Plugin instance not initialized" });
        }

        if (newConfig == null)
        {
            return BadRequest(new { error = "Configuration body cannot be null" });
        }

        Plugin.Instance.UpdateConfiguration(newConfig);
        _logger.LogInformation("Monochrome configuration successfully updated and saved to disk.");
        return Ok(new { success = true, configuration = Plugin.Instance.Configuration });
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
            var isFlac = cachedFile.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
            var isM4a = cachedFile.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
            var contentType = isFlac ? "audio/flac" : (isM4a ? "audio/mp4" : "audio/flac");

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

    /// <summary>
    /// Gets track radio / similar tracks in the same style for a given track.
    /// </summary>
    [HttpGet("Radio")]
    public async Task<IActionResult> GetRadio([FromQuery] long trackId, [FromQuery] int limit = 30, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { error = "Invalid trackId" });
        }

        try
        {
            var tracks = await _apiClient.GetTrackRadioAsync(trackId, limit, cancellationToken).ConfigureAwait(false);
            return Ok(tracks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get radio for track {TrackId}", trackId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Saves a track into the user's favorites (Spotify style, without saving physical files).
    /// </summary>
    [HttpPost("Favorite")]
    public async Task<IActionResult> SetFavorite(
        [FromQuery] long trackId,
        [FromQuery] Guid userId,
        [FromQuery] bool isFavorite = true,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { error = "Invalid trackId" });
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return NotFound(new { error = "User not found." });
        }

        try
        {
            var trackGuid = MonochromeSearchProvider.GetDeterministicGuid($"monochrome_track_{trackId}");
            var item = _libraryManager.GetItemById(trackGuid);
            if (item == null)
            {
                var track = await _apiClient.GetTrackAsync(trackId, cancellationToken).ConfigureAwait(false);
                if (track == null)
                {
                    return NotFound(new { error = "Track not found on TIDAL/Monochrome." });
                }

                var parentFolder = _searchProvider.GetMusicParentFolder(userId);
                item = await _searchProvider.EnsureTrackItemAsync(trackGuid, track, parentFolder, cancellationToken).ConfigureAwait(false);
            }

            if (item != null)
            {
                var dataDto = new UpdateUserItemDataDto
                {
                    IsFavorite = isFavorite
                };
                _userDataManager.SaveUserData(user, item, dataDto, UserDataSaveReason.UpdateUserData);
                return Ok(new { success = true, trackId, isFavorite });
            }

            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Could not index track for favoriting." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to favorite track {TrackId}", trackId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets synchronized or plain lyrics for a track ID.
    /// </summary>
    [HttpGet("Tracks/{trackId}/Lyrics")]
    public async Task<IActionResult> GetLyrics(long trackId, CancellationToken cancellationToken)
    {
        if (trackId <= 0)
        {
            return BadRequest(new { error = "Invalid trackId" });
        }

        var lyrics = await _apiClient.GetTrackLyricsAsync(trackId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (lyrics == null)
        {
            return NotFound(new { error = "No lyrics found for track " + trackId });
        }

        return Ok(lyrics);
    }

    /// <summary>
    /// Gets synchronized or plain lyrics for a track GUID in the library.
    /// </summary>
    [HttpGet("Tracks/ByGuid/{guid}/Lyrics")]
    public async Task<IActionResult> GetLyricsByGuid(Guid guid, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(guid);
        if (item is Audio audio)
        {
            long trackId = 0;
            if (audio.ProviderIds != null)
            {
                if (audio.ProviderIds.TryGetValue("TidalTrack", out var tStr) || audio.ProviderIds.TryGetValue("MonochromeTrack", out tStr))
                {
                    long.TryParse(tStr, out trackId);
                }
            }

            if (trackId == 0 && audio.ExternalId != null && audio.ExternalId.StartsWith("track_"))
            {
                long.TryParse(audio.ExternalId.Substring(6), out trackId);
            }

            var artist = audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault();
            var lyrics = await _apiClient.GetTrackLyricsAsync(trackId, audio.Name, artist, cancellationToken).ConfigureAwait(false);
            if (lyrics != null)
            {
                return Ok(lyrics);
            }
        }

        return NotFound(new { error = "No lyrics found" });
    }

    /// <summary>
    /// Searches or retrieves lyrics by title and artist, guid, or trackId.
    /// </summary>
    [HttpGet("Lyrics/Search")]
    public async Task<IActionResult> SearchLyrics(
        [FromQuery] string? title = null,
        [FromQuery] string? artist = null,
        [FromQuery] long trackId = 0,
        [FromQuery] Guid? guid = null,
        CancellationToken cancellationToken = default)
    {
        if (guid.HasValue && guid.Value != Guid.Empty)
        {
            var item = _libraryManager.GetItemById(guid.Value);
            if (item is Audio audio)
            {
                if (trackId == 0 && audio.ProviderIds != null)
                {
                    if (audio.ProviderIds.TryGetValue("TidalTrack", out var tStr) || audio.ProviderIds.TryGetValue("MonochromeTrack", out tStr))
                    {
                        long.TryParse(tStr, out trackId);
                    }
                }

                if (trackId == 0 && audio.ExternalId != null && audio.ExternalId.StartsWith("track_"))
                {
                    long.TryParse(audio.ExternalId.Substring(6), out trackId);
                }

                title ??= audio.Name;
                artist ??= audio.Artists?.FirstOrDefault() ?? audio.AlbumArtists?.FirstOrDefault();
            }
        }

        if (trackId > 0 || !string.IsNullOrWhiteSpace(title))
        {
            var lyrics = await _apiClient.GetTrackLyricsAsync(trackId, title, artist, cancellationToken).ConfigureAwait(false);
            if (lyrics != null)
            {
                return Ok(lyrics);
            }
        }

        return NotFound(new { error = "No lyrics found" });
    }

    /// <summary>
    /// Stops playback and clears the radio autoplay queue for a session.
    /// </summary>
    [HttpGet("Playback/Stop")]
    [HttpPost("Playback/Stop")]
    public IActionResult TriggerStopRadio([FromQuery] string? sessionId = null, [FromQuery] Guid? userId = null)
    {
        _playbackManager?.ClearRadioQueue(sessionId, userId);
        return Ok(new { success = true, stopped = true });
    }

    /// <summary>
    /// Advances playback to the next song in the radio queue.
    /// </summary>
    [HttpGet("Playback/Next")]
    public async Task<IActionResult> TriggerNextRadioTrack([FromQuery] string? sessionId = null, [FromQuery] Guid? userId = null, CancellationToken cancellationToken = default)
    {
        if (_playbackManager != null)
        {
            var nextGuid = await _playbackManager.PlayNextRadioTrackAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
            if (nextGuid.HasValue)
            {
                return Ok(new { success = true, nextTrackId = nextGuid.Value });
            }
        }

        return Ok(new { success = false, message = "No tracks in radio queue." });
    }

    /// <summary>
    /// Serves the Apple Music style karaoke script for Jellyfin Web.
    /// </summary>
    [HttpGet("karaoke.js")]
    public IActionResult GetKaraokeJs()
    {
        var asm = typeof(MonochromeController).Assembly;
        var resourceName = "Jellyfin.Plugin.Monochrome.Web.karaoke.js";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "application/javascript");
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, "Web", "karaoke.js");
        if (System.IO.File.Exists(localPath))
        {
            return PhysicalFile(localPath, "application/javascript");
        }

        return NotFound();
    }

    /// <summary>
    /// Serves the Apple Music style karaoke styles for Jellyfin Web.
    /// </summary>
    [HttpGet("karaoke.css")]
    public IActionResult GetKaraokeCss()
    {
        var asm = typeof(MonochromeController).Assembly;
        var resourceName = "Jellyfin.Plugin.Monochrome.Web.karaoke.css";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return Content(reader.ReadToEnd(), "text/css");
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, "Web", "karaoke.css");
        if (System.IO.File.Exists(localPath))
        {
            return PhysicalFile(localPath, "text/css");
        }

        return NotFound();
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

