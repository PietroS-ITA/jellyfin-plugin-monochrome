using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Monochrome.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Providers;

/// <summary>
/// Provides media sources dynamically for Monochrome music tracks.
/// Resolves direct HiFi/Lossless TIDAL streams on the fly when any Jellyfin client starts playback.
/// </summary>
public class MonochromeMediaSourceProvider : IMediaSourceProvider
{
    private readonly MonochromeApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly ILogger<MonochromeMediaSourceProvider> _logger;

    public MonochromeMediaSourceProvider(
        MonochromeApiClient apiClient,
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreamRepository,
        ILogger<MonochromeMediaSourceProvider> logger)
    {
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _mediaStreamRepository = mediaStreamRepository;
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Audio)
        {
            return [];
        }

        var trackIdStr = item.GetProviderId("MonochromeTrack") ?? item.GetProviderId("TidalTrack");
        if (string.IsNullOrEmpty(trackIdStr) || !long.TryParse(trackIdStr, out var trackId))
        {
            return [];
        }

        try
        {
            var localFile = await _apiClient.EnsureTrackCachedAsync(trackId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(localFile) || !File.Exists(localFile))
            {
                _logger.LogWarning("Monochrome cached file was empty or missing for track {TrackId}", trackId);
                return [];
            }

            var fileInfo = new FileInfo(localFile);
            var isFlac = localFile.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
            var isM4a = localFile.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase);
            var container = isFlac ? "flac" : (isM4a ? "m4a" : "flac");
            var codec = isFlac ? "flac" : "aac";

            // Update item in library so that its path points to the real cached file
            if (item is Audio audioItem && (audioItem.Path != localFile || audioItem.Container != container))
            {
                audioItem.Path = localFile;
                audioItem.Container = container;
                try
                {
                    var parent = audioItem.GetParent() ?? _libraryManager.GetUserRootFolder();
                    await _libraryManager.UpdateItemAsync(audioItem, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update track path in library for track {TrackId}", trackId);
                }
            }

            // Ensure media stream is saved in SQLite database so GetOptimalAudioStream never throws
            try
            {
                _mediaStreamRepository.SaveMediaStreams(item.Id,
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = codec,
                        Index = 0,
                        IsDefault = true,
                        Channels = 2,
                        SampleRate = 44100
                    }
                ], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not save media stream for track {TrackId}", trackId);
            }

            var mediaSource = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = localFile,
                Protocol = MediaProtocol.File,
                Container = container,
                Name = item.Name,
                Size = fileInfo.Length,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsRemote = false,
                RequiresOpening = false,
                RequiresClosing = false,
                MediaStreams =
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = codec,
                        Index = 0,
                        IsDefault = true,
                        Channels = 2,
                        SampleRate = 44100
                    }
                ]
            };

            return [mediaSource];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve media source for Monochrome track {TrackId}", trackId);
            return [];
        }
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        return Task.FromResult<ILiveStream>(null!);
    }
}
