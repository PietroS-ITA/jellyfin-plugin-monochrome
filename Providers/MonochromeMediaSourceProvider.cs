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
    private readonly ILogger<MonochromeMediaSourceProvider> _logger;

    public MonochromeMediaSourceProvider(
        MonochromeApiClient apiClient,
        ILogger<MonochromeMediaSourceProvider> logger)
    {
        _apiClient = apiClient;
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
            var isMp4 = localFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var container = isMp4 ? "mp4" : "flac";

            var mediaSource = new MediaSourceInfo
            {
                Id = item.Id.ToString("N"),
                Path = localFile,
                Protocol = MediaProtocol.File,
                Container = container,
                Name = item.Name,
                Size = fileInfo.Length,
                SupportsDirectPlay = false,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                IsRemote = false,
                MediaStreams =
                [
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = "flac",
                        Index = 0,
                        IsDefault = true
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
