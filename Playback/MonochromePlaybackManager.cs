using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using Jellyfin.Plugin.Monochrome.Search;
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
    private readonly ILogger<MonochromePlaybackManager> _logger;
    private readonly ConcurrentDictionary<string, string> _lastQueuedRadioTrack = new();

    public MonochromePlaybackManager(
        ISessionManager sessionManager,
        MonochromeApiClient apiClient,
        MonochromeSearchProvider searchProvider,
        ILogger<MonochromePlaybackManager> logger)
    {
        _sessionManager = sessionManager;
        _apiClient = apiClient;
        _searchProvider = searchProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _logger.LogInformation("Monochrome Autoplay Radio manager initialized.");
        try
        {
            await RepairPresentationUniqueKeysAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RepairPresentationUniqueKeysAsync top-level exception");
        }
    }

    private async Task RepairPresentationUniqueKeysAsync()
    {
        await Task.Yield();
        try
        {
            var candidatePaths = new[]
            {
                "/config/data/jellyfin.db",
                "/config/jellyfin.db"
            };

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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE BaseItems SET PresentationUniqueKey = LOWER(REPLACE(Id, '-', '')) WHERE PresentationUniqueKey IS NULL;";
            var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _logger.LogInformation("Monochrome DB Repair: Successfully repaired {Count} items with NULL PresentationUniqueKey in {DbFile}.", affected, dbFile);
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
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Item is not Audio audio || e.Session == null)
        {
            return;
        }

        // Only trigger autoplay if the session queue has at most 1 item (the single track played)
        // If the user queued an album or playlist, do not interfere with their explicit queue!
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

        if (string.IsNullOrEmpty(trackIdStr) && audio.Path != null && audio.Path.StartsWith("monochrome://track/", StringComparison.OrdinalIgnoreCase))
        {
            trackIdStr = audio.Path.Substring("monochrome://track/".Length);
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
                await Task.Delay(1500).ConfigureAwait(false);

                _logger.LogInformation("Autoplay Radio: Fetching similar tracks for '{Title}' ({TrackId}) in session {SessionId}...", audio.Name, trackId, sessionId);
                var radioTracks = await _apiClient.GetTrackRadioAsync(trackId, 15, CancellationToken.None).ConfigureAwait(false);
                if (radioTracks.Count == 0)
                {
                    return;
                }

                var parentFolder = _searchProvider.GetMusicParentFolder(e.Session.UserId);
                var queuedGuids = new List<Guid>();

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
                    }
                }

                if (queuedGuids.Count > 0)
                {
                    var playRequest = new PlayRequest
                    {
                        ItemIds = queuedGuids.ToArray(),
                        PlayCommand = PlayCommand.PlayLast
                    };

                    await _sessionManager.SendPlayCommand(sessionId, sessionId, playRequest, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("Autoplay Radio: Enqueued {Count} similar tracks for '{Title}'", queuedGuids.Count, audio.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Autoplay radio queueing failed for track {TrackId}", trackId);
            }
        });
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
    }
}
