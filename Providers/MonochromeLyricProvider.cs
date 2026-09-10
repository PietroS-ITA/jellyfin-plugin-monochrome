using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Providers;

/// <summary>
/// Native Jellyfin 12.0 Lyric Provider for Monochrome / TIDAL music.
/// </summary>
public class MonochromeLyricProvider : ILyricProvider
{
    private readonly MonochromeApiClient _apiClient;
    private readonly ILogger<MonochromeLyricProvider> _logger;

    public MonochromeLyricProvider(MonochromeApiClient apiClient, ILogger<MonochromeLyricProvider> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Monochrome Music Lyrics";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(LyricSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SongName))
        {
            return Array.Empty<RemoteLyricInfo>();
        }

        try
        {
            var artist = request.ArtistNames?.FirstOrDefault() ?? request.AlbumArtistsNames?.FirstOrDefault();
            long trackId = 0;
            if (request.ProviderIds != null)
            {
                if (request.ProviderIds.TryGetValue("TidalTrack", out var tStr) || request.ProviderIds.TryGetValue("MonochromeTrack", out tStr))
                {
                    long.TryParse(tStr, out trackId);
                }
            }

            if (trackId == 0 && !string.IsNullOrEmpty(request.MediaPath))
            {
                var fn = Path.GetFileNameWithoutExtension(request.MediaPath);
                long.TryParse(fn, out trackId);
            }

            var lyrics = await _apiClient.GetTrackLyricsAsync(trackId, request.SongName, artist, cancellationToken).ConfigureAwait(false);
            if (lyrics == null)
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            var rawContent = !string.IsNullOrEmpty(lyrics.RawLrc) ? lyrics.RawLrc : lyrics.PlainLyrics;
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return Array.Empty<RemoteLyricInfo>();
            }

            return new[]
            {
                new RemoteLyricInfo
                {
                    Id = $"{lyrics.TrackId}_{request.SongName}",
                    ProviderName = Name,
                    Metadata = new LyricMetadata
                    {
                        Artist = lyrics.Artist ?? artist,
                        Title = lyrics.Title ?? request.SongName,
                        IsSynced = lyrics.HasSynced
                    },
                    Lyrics = new LyricResponse
                    {
                        Stream = new MemoryStream(Encoding.UTF8.GetBytes(rawContent)),
                        Format = lyrics.HasSynced ? "lrc" : "txt"
                    }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome lyric search failed for '{Title}'", request.SongName);
            return Array.Empty<RemoteLyricInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<LyricResponse?> GetLyricsAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var parts = id.Split('_', 2);
            long.TryParse(parts[0], out var trackId);
            string? title = parts.Length > 1 ? parts[1] : null;

            var lyrics = await _apiClient.GetTrackLyricsAsync(trackId, title, null, cancellationToken).ConfigureAwait(false);
            if (lyrics == null)
            {
                return null;
            }

            var rawContent = !string.IsNullOrEmpty(lyrics.RawLrc) ? lyrics.RawLrc : lyrics.PlainLyrics;
            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return null;
            }

            return new LyricResponse
            {
                Stream = new MemoryStream(Encoding.UTF8.GetBytes(rawContent)),
                Format = lyrics.HasSynced ? "lrc" : "txt"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome GetLyricsAsync failed for ID {Id}", id);
            return null;
        }
    }
}
