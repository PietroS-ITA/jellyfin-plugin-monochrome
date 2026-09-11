using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private readonly ConcurrentDictionary<Guid, List<Guid>> _userRadioQueues = new();

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

        try
        {
            RegisterWithJavaScriptInjector();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JavaScript Injector registration encountered an issue");
        }

        // Schedule periodic registration retries in background in case JavaScript Injector starts after Monochrome
        _ = Task.Run(async () =>
        {
            for (int i = 1; i <= 4; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * i)).ConfigureAwait(false);
                try
                {
                    RegisterWithJavaScriptInjector();
                }
                catch { }
            }
        });

        try
        {
            InjectWebClientScript();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Web script injection encountered an issue");
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

            // 2. Repair legacy monochrome://track/ and .mp4 paths to local cache .flac path
            var cacheDir = Path.Combine(_applicationPaths.CachePath, "monochrome");
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
                    UPDATE BaseItems
                    SET Path = CASE 
                            WHEN Path LIKE 'monochrome://track/%' THEN @cacheDir || '/' || SUBSTR(Path, 20) || '.flac'
                            WHEN Path LIKE '%.mp4' THEN REPLACE(Path, '.mp4', '.flac')
                            ELSE Path
                        END
                    WHERE Type = 'MediaBrowser.Controller.Entities.Audio.Audio'
                      AND (Path LIKE 'monochrome://track/%' OR Path LIKE '%.mp4' OR ExternalId LIKE 'track_%');";
                var p1 = cmd2.CreateParameter();
                p1.ParameterName = "@cacheDir";
                p1.Value = cacheDir;
                cmd2.Parameters.Add(p1);
                var affected = await cmd2.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected > 0)
                {
                    _logger.LogInformation("Monochrome DB Repair: Repaired {Count} items with native FLAC container and paths.", affected);
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

            using (var cmd3b = conn.CreateCommand())
            {
                cmd3b.CommandText = @"
                    UPDATE MediaStreamInfos
                    SET Codec = 'flac'
                    WHERE Codec != 'flac' AND ItemId IN (
                        SELECT Id FROM BaseItems
                        WHERE Type = 'MediaBrowser.Controller.Entities.Audio.Audio'
                          AND (Path LIKE '%.flac' OR Path LIKE '%monochrome%')
                    );";
                await cmd3b.ExecuteNonQueryAsync().ConfigureAwait(false);
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

            // 5. Purge any short preview cache files (< 4MB) and remux existing .mp4 files to native .flac
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
                        else
                        {
                            var targetFlac = Path.ChangeExtension(file, ".flac");
                            if (!File.Exists(targetFlac) || new FileInfo(targetFlac).Length < 1024)
                            {
                                if (await _apiClient.RemuxToNativeAudioAsync(file, targetFlac, "flac", CancellationToken.None).ConfigureAwait(false))
                                {
                                    try { File.Delete(file); } catch { }
                                }
                            }
                            else
                            {
                                try { File.Delete(file); } catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monochrome DB Repair: Preview cache purge and remux encountered an issue.");
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
                    // Store the upcoming radio queue in memory for this session and user
                    _sessionRadioQueues[sessionId] = new List<Guid>(queuedGuids);
                    if (e.Session.UserId != Guid.Empty)
                    {
                        _userRadioQueues[e.Session.UserId] = new List<Guid>(queuedGuids);
                    }

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

                    _logger.LogInformation("Autoplay Radio: Prepared {Count} similar tracks for '{Title}' in radio queue", queuedGuids.Count, audio.Name);
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

        // Check if the stopped item was a Monochrome track
        bool isMonochrome = (audio.Path != null && audio.Path.Contains("monochrome"))
                            || (audio.ExternalId != null && audio.ExternalId.StartsWith("track_"))
                            || (audio.ProviderIds != null && (audio.ProviderIds.ContainsKey("MonochromeTrack") || audio.ProviderIds.ContainsKey("TidalTrack")));

        if (!isMonochrome)
        {
            return;
        }

        // Check if the track reached true physical completion (within 2.5s of the end of the song).
        // In Jellyfin, PlayedToCompletion is marked true when position >= 90%, so an explicit STOP
        // anywhere between 90% and the end would otherwise falsely trigger autoplay!
        bool naturalCompletion = false;
        if (e.PlayedToCompletion && audio.RunTimeTicks.HasValue && audio.RunTimeTicks.Value > 0)
        {
            if (e.PlaybackPositionTicks.HasValue && e.PlaybackPositionTicks.Value > 0)
            {
                var diffTicks = audio.RunTimeTicks.Value - e.PlaybackPositionTicks.Value;
                if (diffTicks <= TimeSpan.FromSeconds(2.5).Ticks && diffTicks >= -TimeSpan.FromSeconds(5.0).Ticks)
                {
                    naturalCompletion = true;
                }
            }
        }

        // If it was NOT a natural track completion (user clicked STOP, paused, or skipped early):
        // ALWAYS and IMMEDIATELY clear the radio queue so nothing will ever autoplay!
        if (!naturalCompletion)
        {
            _logger.LogInformation("Monochrome Autoplay: Track '{Title}' stopped before true completion (pos: {Pos}ms, runtime: {Run}ms). Clearing radio queue.",
                audio.Name,
                e.PlaybackPositionTicks.HasValue ? e.PlaybackPositionTicks.Value / 10000 : 0,
                audio.RunTimeTicks.HasValue ? audio.RunTimeTicks.Value / 10000 : 0);

            ClearRadioQueue(sessionId, e.Session.UserId);
            return;
        }

        // If the session has multiple items in its native queue (e.g. user is playing an Album or Playlist):
        // Yield to Jellyfin's native queue playback!
        if (e.Session.NowPlayingQueue != null && e.Session.NowPlayingQueue.Count > 1)
        {
            _logger.LogInformation("Monochrome Autoplay: Session has {Count} items in native queue. Yielding to native playlist.", e.Session.NowPlayingQueue.Count);
            ClearRadioQueue(sessionId, e.Session.UserId);
            return;
        }

        // Retrieve queued radio tracks
        List<Guid>? queue = null;
        if (!_sessionRadioQueues.TryGetValue(sessionId, out queue) || queue.Count == 0)
        {
            if (e.Session.UserId == Guid.Empty || !_userRadioQueues.TryGetValue(e.Session.UserId, out queue) || queue.Count == 0)
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Brief pause to ensure client didn't immediately start something else or user didn't stop
                await Task.Delay(1000).ConfigureAwait(false);

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

                _logger.LogInformation("Autoplay Radio: Track '{Title}' reached natural completion. Advancing to radio track {NextId} on session {SessionId}...", audio.Name, nextTrackGuid, sessionId);

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

    /// <summary>
    /// Forcibly plays the next radio track for a session or user.
    /// </summary>
    public async Task<Guid?> PlayNextRadioTrackAsync(string? sessionId, Guid? userId, CancellationToken cancellationToken = default)
    {
        List<Guid>? queue = null;
        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessionRadioQueues.TryGetValue(sessionId, out queue);
        }

        if ((queue == null || queue.Count == 0) && userId.HasValue && userId.Value != Guid.Empty)
        {
            _userRadioQueues.TryGetValue(userId.Value, out queue);
        }

        if (queue == null || queue.Count == 0)
        {
            var anyQueue = _sessionRadioQueues.Values.FirstOrDefault(q => q.Count > 0)
                           ?? _userRadioQueues.Values.FirstOrDefault(q => q.Count > 0);
            queue = anyQueue;
        }

        if (queue == null || queue.Count == 0)
        {
            return null;
        }

        Guid nextTrackGuid;
        lock (queue)
        {
            if (queue.Count == 0) return null;
            nextTrackGuid = queue[0];
            queue.RemoveAt(0);
        }

        var session = !string.IsNullOrEmpty(sessionId)
            ? _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId)
            : _sessionManager.Sessions.FirstOrDefault(s => userId.HasValue && s.UserId == userId.Value);

        if (session != null)
        {
            if (session.Capabilities != null)
            {
                session.Capabilities.SupportsMediaControl = true;
            }

            var playNow = new PlayRequest
            {
                ItemIds = new[] { nextTrackGuid },
                PlayCommand = PlayCommand.PlayNow
            };

            await _sessionManager.SendPlayCommand(string.Empty, session.Id, playNow, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Autoplay Radio: Client requested Next track {NextId} on session {SessionId}", nextTrackGuid, session.Id);
        }

        return nextTrackGuid;
    }

    /// <summary>
    /// Clears the radio autoplay queue for a session and/or user.
    /// If both sessionId and userId are null or empty, clears all active queues.
    /// </summary>
    public void ClearRadioQueue(string? sessionId = null, Guid? userId = null)
    {
        if (string.IsNullOrEmpty(sessionId) && (!userId.HasValue || userId.Value == Guid.Empty))
        {
            _sessionRadioQueues.Clear();
            _userRadioQueues.Clear();
            _logger.LogInformation("Autoplay Radio: Cleared all radio queues across all sessions.");
            return;
        }

        if (!string.IsNullOrEmpty(sessionId) && _sessionRadioQueues.TryGetValue(sessionId, out var sQueue))
        {
            lock (sQueue)
            {
                sQueue.Clear();
            }
            _sessionRadioQueues.TryRemove(sessionId, out _);
        }

        if (userId.HasValue && userId.Value != Guid.Empty && _userRadioQueues.TryGetValue(userId.Value, out var uQueue))
        {
            lock (uQueue)
            {
                uQueue.Clear();
            }
            _userRadioQueues.TryRemove(userId.Value, out _);
        }

        _logger.LogInformation("Autoplay Radio: Cleared queue for session '{SessionId}', user '{UserId}'", sessionId, userId);
    }

    private void RegisterWithJavaScriptInjector()
    {
        try
        {
            var injectorAsm = System.Runtime.Loader.AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(a => a.GetName().Name?.Equals("Jellyfin.Plugin.JavaScriptInjector", StringComparison.OrdinalIgnoreCase) == true)
                ?? AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name?.Equals("Jellyfin.Plugin.JavaScriptInjector", StringComparison.OrdinalIgnoreCase) == true);

            if (injectorAsm == null)
            {
                return;
            }

            var pluginInterfaceType = injectorAsm.GetType("Jellyfin.Plugin.JavaScriptInjector.PluginInterface");
            if (pluginInterfaceType == null)
            {
                return;
            }

            var registerMethod = pluginInterfaceType.GetMethod("RegisterScript", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod == null)
            {
                return;
            }

            var paramType = registerMethod.GetParameters().FirstOrDefault()?.ParameterType;
            if (paramType == null)
            {
                return;
            }

            var version = Plugin.Instance?.Version.ToString() ?? "1.3.9.6";
            var scriptJs = $"(function(){{if(!document.getElementById('monochrome-karaoke-loader')){{var s=document.createElement('script');s.id='monochrome-karaoke-loader';s.src='/Monochrome/karaoke.js?v={version}';s.defer=true;document.body.appendChild(s);var l=document.createElement('link');l.rel='stylesheet';l.href='/Monochrome/karaoke.css?v={version}';document.head.appendChild(l);}}}})();";

            var parseMethod = paramType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            object? payload = null;

            if (parseMethod != null)
            {
                var payloadJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = "monochrome-karaoke-ui",
                    name = "Monochrome Apple Music Karaoke & Lyrics",
                    script = scriptJs,
                    enabled = true,
                    requiresAuthentication = false,
                    pluginId = Plugin.Instance?.Id.ToString() ?? Guid.Empty.ToString(),
                    pluginName = "Monochrome",
                    pluginVersion = version
                });
                payload = parseMethod.Invoke(null, new object[] { payloadJson });
            }

            if (payload != null)
            {
                var result = registerMethod.Invoke(null, new object[] { payload });
                _logger.LogInformation("Monochrome Web: Programmatically registered Karaoke UI v{Version} with JavaScript Injector plugin. Result: {Result}", version, result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome Web: Could not register script with JavaScript Injector.");
        }
    }

    private void InjectWebClientScript()
    {
        var candidatePaths = new List<string>();
        try
        {
            if (!string.IsNullOrEmpty(_applicationPaths.WebPath))
            {
                candidatePaths.Add(Path.Combine(_applicationPaths.WebPath, "index.html"));
            }
        }
        catch { }

        candidatePaths.Add("/jellyfin/jellyfin-web/index.html");
        candidatePaths.Add("/usr/share/jellyfin/web/index.html");
        candidatePaths.Add("/usr/lib/jellyfin/web/index.html");
        candidatePaths.Add("/usr/lib/jellyfin/bin/jellyfin-web/index.html");
        candidatePaths.Add("/var/lib/jellyfin/web/index.html");
        candidatePaths.Add("/app/jellyfin/jellyfin-web/index.html");
        candidatePaths.Add("/jellyfin-web/index.html");
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "jellyfin-web", "index.html"));

        var indexPath = candidatePaths.FirstOrDefault(File.Exists);
        if (string.IsNullOrEmpty(indexPath))
        {
            _logger.LogInformation("Monochrome Web: index.html not found in candidate paths.");
            return;
        }

        try
        {
            var content = File.ReadAllText(indexPath);
            var version = Plugin.Instance?.Version.ToString() ?? "1.3.9.6";
            var scriptTag = $"<script plugin=\"Monochrome\" src=\"/Monochrome/karaoke.js?v={version}\" defer></script>";
            var styleTag = $"<link plugin=\"Monochrome\" rel=\"stylesheet\" href=\"/Monochrome/karaoke.css?v={version}\">";

            // If index.html already has the exact versioned tags, nothing to do
            if (content.Contains(scriptTag, StringComparison.OrdinalIgnoreCase) &&
                content.Contains(styleTag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Clean up any legacy or previous version Monochrome tags
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<script[^>]*plugin=""Monochrome""[^>]*></script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<script[^>]*src=""/Monochrome/karaoke\.js[^""]*""[^>]*></script>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<link[^>]*plugin=""Monochrome""[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<link[^>]*href=""/Monochrome/karaoke\.css[^""]*""[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var headEnd = content.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (headEnd != -1)
            {
                content = content.Insert(headEnd, styleTag);
            }

            var bodyEnd = content.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd != -1)
            {
                content = content.Insert(bodyEnd, scriptTag);
            }

            File.WriteAllText(indexPath, content);
            _logger.LogInformation("Monochrome Web: Successfully injected Karaoke UI v{Version} into {Path}", version, indexPath);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Monochrome Web: index.html found at '{Path}' but is read-only (permission denied). Install 'JavaScript Injector' plugin from the Jellyfin Catalog or configure Custom CSS.", indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Monochrome Web: Could not modify index.html for Karaoke UI.");
        }
    }

    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }
}
