using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using Jellyfin.Plugin.Monochrome.Search;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Playback;

/// <summary>
/// Background hosted service that monitors playback events to automatically queue similar tracks
/// (Spotify-style autoplay radio) when a single song is played.
/// </summary>
public sealed class MonochromePlaybackManager : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly MonochromeApiClient _apiClient;
    private readonly MonochromeSearchProvider _searchProvider;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<MonochromePlaybackManager> _logger;
    private readonly ConcurrentDictionary<string, string> _lastQueuedRadioTrack = new();
    private readonly ConcurrentDictionary<string, List<Guid>> _sessionRadioQueues = new();

    public MonochromePlaybackManager(
        ISessionManager sessionManager,
        MonochromeApiClient apiClient,
        MonochromeSearchProvider searchProvider,
        IApplicationPaths applicationPaths,
        ILogger<MonochromePlaybackManager> logger)
    {
        _sessionManager = sessionManager;
        _apiClient = apiClient;
        _searchProvider = searchProvider;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Monochrome Autoplay Radio manager initialized.");
        try
        {
            await RepairDatabaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RepairDatabaseAsync top-level exception");
        }
    }

    private async Task RepairDatabaseAsync()
    {
        await Task.Yield();
        try
        {
            var candidatePaths = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(_applicationPaths.DataPath))
                {
                    candidatePaths.Add(Path.Combine(_applicationPaths.DataPath, "jellyfin.db"));
                }

                if (!string.IsNullOrEmpty(_applicationPaths.ConfigurationDirectoryPath))
                {
                    candidatePaths.Add(Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "data", "jellyfin.db"));
                    candidatePaths.Add(Path.Combine(_applicationPaths.ConfigurationDirectoryPath, "jellyfin.db"));
                }
            }
            catch
            {
                // Fallback to standard defaults
            }

            candidatePaths.Add("/config/data/jellyfin.db");
            candidatePaths.Add("/config/jellyfin.db");

            var dbFile = candidatePaths.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(dbFile))
            {
                _logger.LogInformation("Monochrome DB Repair: No jellyfin.db found in candidate paths.");
                return;
            }

            var sqliteAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase));
            var connType = sqliteAsm?.GetType("Microsoft.Data.Sqlite.SqliteConnection");

            if (connType == null)
            {
                _logger.LogWarning("Monochrome DB Repair: Could not find SqliteConnection in loaded assemblies.");
                return;
            }

            using var conn = (System.Data.Common.DbConnection)Activator.CreateInstance(connType, $"Data Source={dbFile};Mode=ReadWrite")!;
            await conn.OpenAsync().ConfigureAwait(false);

            // 1. Repair NULL PresentationUniqueKey
            using (var cmd1 = conn.CreateCommand())
            {
                cmd1.CommandText = "UPDATE BaseItems SET PresentationUniqueKey = LOWER(REPLACE(Id, '-', '')) WHERE PresentationUniqueKey IS NULL;";
                var affected = await cmd1.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                {
                    _logger.LogInformation("Monochrome DB Repair: Repaired {Count} items with NULL PresentationUniqueKey.", affected);
                }
            }

            // 2. Repair legacy monochrome://track/ paths to local cache path
            var cacheDir = Path.Combine(_applicationPaths.CachePath, "monochrome");
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "UPDATE BaseItems SET Path = @cacheDir || '/' || SUBSTR(Path, 20) || '.mp4' WHERE Path LIKE 'monochrome://track/%';";
                var p1 = cmd2.CreateParameter();
                p1.ParameterName = "@cacheDir";
                p1.Value = cacheDir;
                cmd2.Parameters.Add(p1);
                var affected = await cmd2.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                {
                    _logger.LogInformation("Monochrome DB Repair: Repaired {Count} items with legacy monochrome:// paths.", affected);
                }
            }

            // 3. Ensure MediaStreamInfos exists for all Audio BaseItems
            using (var cmd3 = conn.CreateCommand())
            {
                cmd3.CommandText = @"
                    INSERT INTO MediaStreamInfos (ItemId, StreamIndex, StreamType, Codec, Channels, SampleRate, IsDefault, IsExternal, IsForced, IsOriginal)
                    SELECT b.Id, 0, 0, 'flac', 2, 44100, 1, 0, 0, 0
                    FROM BaseItems b
                    LEFT JOIN MediaStreamInfos m ON b.Id = m.ItemId AND m.StreamIndex = 0
                    WHERE b.Type = 'MediaBrowser.Controller.Entities.Audio.Audio'
                      AND m.ItemId IS NULL;";
                var affected = await cmd3.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                {
                    _logger.LogInformation("Monochrome DB Repair: Inserted missing default media streams for {Count} audio items.", affected);
                }
            }

            // 4. Clean up single-track Album titles that match the track title (so artist name displays on subtitle)
            using (var cmd4 = conn.CreateCommand())
            {
                cmd4.CommandText = @"
                    UPDATE BaseItems
                    SET Album = ''
                    WHERE Type = 'MediaBrowser.Controller.Entities.Audio.Audio'
                      AND (Path LIKE '%monochrome%' OR ExternalId LIKE 'track_%')
                      AND (Album = Name OR Album LIKE Name || ' / %' OR Album LIKE Name || ' - %');";
                var affected = await cmd4.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                {
                    _logger.LogInformation("Monochrome DB Repair: Cleared redundant single-song Album name for {Count} tracks.", affected);
                }
            }

            // 5. Purge any short preview cache files (< 4MB) from previous versions
            try
            {
                if (Directory.Exists(cacheDir))
                {
                    foreach (var file in Directory.GetFiles(cacheDir, "*.mp4"))
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length < 4L * 1024L * 1024L)
                        {
                            File.Delete(file);
                            _logger.LogInformation("Monochrome DB Repair: Purged short preview cache file {File} ({Bytes} bytes)", file, fi.Length);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monochrome DB Repair: Preview cache purge encountered an issue.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monochrome DB Repair failed");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Audio audio || e.Session == null)
        {
            return;
        }

        // Enable media control capabilities on the session so remote play commands are accepted
        if (e.Session.Capabilities != null)
        {
            e.Session.Capabilities.SupportsMediaControl = true;
        }

        // Only trigger autoplay radio if the session queue has at most 1 item (the single track played)
        // If the user explicitly queued an album or playlist, do not interfere with their explicit queue!
        var queue = e.Session.NowPlayingQueue;
        if (queue != null && queue.Count > 1)
        {
            return;
        }

        var sessionId = e.Session.Id;
        string? trackIdStr = null;
        if (audio.ProviderIds != null)
        {
            if (!audio.ProviderIds.TryGetValue("TidalTrack", out trackIdStr))
            {
                audio.ProviderIds.TryGetValue("MonochromeTrack", out trackIdStr);
            }
        }

        if (string.IsNullOrEmpty(trackIdStr) && audio.Path != null)
        {
            if (audio.Path.StartsWith("monochrome://track/", StringComparison.OrdinalIgnoreCase))
            {
                trackIdStr = audio.Path.Substring("monochrome://track/".Length);
            }
            else if (audio.Path.Contains("/monochrome/"))
            {
                var fileName = Path.GetFileNameWithoutExtension(audio.Path);
                trackIdStr = fileName;
            }
        }

        if (string.IsNullOrEmpty(trackIdStr) && !string.IsNullOrEmpty(audio.ExternalId) && audio.ExternalId.StartsWith("track_", StringComparison.OrdinalIgnoreCase))
        {
            trackIdStr = audio.ExternalId.Substring("track_".Length);
        }

        if (string.IsNullOrEmpty(trackIdStr) || !long.TryParse(trackIdStr, out var trackId))
        {
            return;
        }

        // Avoid triggering duplicate radio queries for the same track on the same session
        var dedupeKey = $"{sessionId}_{trackId}";
        if (!_lastQueuedRadioTrack.TryAdd(dedupeKey, trackIdStr))
        {
            return;
        }

        // Keep cache manageable
        if (_lastQueuedRadioTrack.Count > 50)
        {
            _lastQueuedRadioTrack.Clear();
            _lastQueuedRadioTrack.TryAdd(dedupeKey, trackIdStr);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Small delay to let the client player queue settle
                await Task.Delay(1200).ConfigureAwait(false);

                _logger.LogInformation("Autoplay Radio: Fetching similar tracks for '{Title}' ({TrackId}) in session {SessionId}...", audio.Name, trackId, sessionId);
                var radioTracks = await _apiClient.GetTrackRadioAsync(trackId, 15, CancellationToken.None).ConfigureAwait(false);
                if (radioTracks.Count == 0)
                {
                    return;
                }

                var parentFolder = _searchProvider.GetMusicParentFolder(e.Session.UserId);
                var queuedGuids = new List<Guid>();
                var upcomingTracks = new List<TidalTrackItem>();

                foreach (var rTrack in radioTracks)
                {
                    if (rTrack.Id == trackId)
                    {
                        continue; // Skip the currently playing track itself
                    }

                    var tGuid = MonochromeSearchProvider.GetDeterministicGuid($"monochrome_track_{rTrack.Id}");
                    var item = await _searchProvider.EnsureTrackItemAsync(tGuid, rTrack, parentFolder, CancellationToken.None).ConfigureAwait(false);
                    if (item != null)
                    {
                        queuedGuids.Add(item.Id);
                        upcomingTracks.Add(rTrack);
                    }
                }

                if (queuedGuids.Count > 0)
                {
                    // Store the upcoming radio queue in memory for this session
                    _sessionRadioQueues[sessionId] = new List<Guid>(queuedGuids);

                    // Pre-cache the first 2 upcoming radio tracks in the background so skip forward is instantaneous
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < Math.Min(2, upcomingTracks.Count); i++)
                        {
                            try
                            {
                                await _apiClient.EnsureTrackCachedAsync(upcomingTracks[i].Id, CancellationToken.None).ConfigureAwait(false);
                                _logger.LogDebug("Autoplay Radio: Pre-cached upcoming track '{Title}' ({TrackId})", upcomingTracks[i].Title, upcomingTracks[i].Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to pre-cache upcoming track {TrackId}", upcomingTracks[i].Id);
                            }
                        }
                    });

                    // Send PlayCommand.PlayLast to client player so client queue is populated
                    var playRequest = new PlayRequest
                    {
                        ItemIds = queuedGuids.ToArray(),
                        PlayCommand = PlayCommand.PlayLast
                    };

                    await _sessionManager.SendPlayCommand(string.Empty, sessionId, playRequest, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("Autoplay Radio: Enqueued {Count} similar tracks for '{Title}'", queuedGuids.Count, audio.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Autoplay radio queueing failed for track {TrackId}", trackId);
            }
        });
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session == null || e.Item is not Audio audio)
        {
            return;
        }

        var sessionId = e.Session.Id;
        if (!_sessionRadioQueues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
        {
            return;
        }

        // Check if the stopped item was a Monochrome track
        bool isMonochrome = (audio.Path != null && audio.Path.Contains("monochrome"))
                            || (audio.ExternalId != null && audio.ExternalId.StartsWith("track_"))
                            || (audio.ProviderIds != null && (audio.ProviderIds.ContainsKey("MonochromeTrack") || audio.ProviderIds.ContainsKey("TidalTrack")));

        if (!isMonochrome)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Brief pause to check if the client already advanced automatically
                await Task.Delay(800).ConfigureAwait(false);

                var activeSession = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
                if (activeSession == null)
                {
                    return;
                }

                // If the client is already playing something else, do not interfere
                if (activeSession.NowPlayingItem != null)
                {
                    return;
                }

                Guid nextTrackGuid;
                lock (queue)
                {
                    if (queue.Count == 0)
                    {
                        return;
                    }
                    nextTrackGuid = queue[0];
                    queue.RemoveAt(0);
                }

                _logger.LogInformation("Autoplay Radio: Client finished '{Title}'. Automatically triggering next track {NextId} on session {SessionId}...", audio.Name, nextTrackGuid, sessionId);

                if (activeSession.Capabilities != null)
                {
                    activeSession.Capabilities.SupportsMediaControl = true;
                }

                var playNow = new PlayRequest
                {
                    ItemIds = new[] { nextTrackGuid },
                    PlayCommand = PlayCommand.PlayNow
                };

                await _sessionManager.SendPlayCommand(string.Empty, sessionId, playNow, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Autoplay transition failed on session {SessionId}", sessionId);
            }
        });
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }
}
