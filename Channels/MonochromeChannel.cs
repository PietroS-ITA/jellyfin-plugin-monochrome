using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Channels;

/// <summary>
/// Channel provider enabling music browsing and streaming from Monochrome / TIDAL inside Jellyfin.
/// </summary>
public class MonochromeChannel : IChannel, IRequiresMediaInfoCallback, ISupportsMediaProbe
{
    private readonly MonochromeApiClient _apiClient;
    private readonly ILogger<MonochromeChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonochromeChannel"/> class.
    /// </summary>
    /// <param name="apiClient">The API client instance.</param>
    /// <param name="logger">The logger instance.</param>
    public MonochromeChannel(MonochromeApiClient apiClient, ILogger<MonochromeChannel> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Monochrome Music";

    /// <inheritdoc />
    public string Description => "Browse and stream high-resolution lossless music from Monochrome and TIDAL.";

    /// <inheritdoc />
    public string DataVersion => "1.0.0";

    /// <inheritdoc />
    public string HomePageUrl => "https://monochrome.tf";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType>
            {
                ChannelMediaContentType.Song
            },
            MediaTypes = new List<ChannelMediaType>
            {
                ChannelMediaType.Audio
            },
            SupportsSortOrderToggle = true,
            DefaultSortFields = new List<ChannelItemSortField>
            {
                ChannelItemSortField.Name,
                ChannelItemSortField.DateCreated
            }
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId)
    {
        return Plugin.Instance?.Configuration.EnableChannel ?? true;
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var folderId = query.FolderId;

        // 1. Root level navigation
        if (string.IsNullOrEmpty(folderId))
        {
            return GetRootFolders();
        }

        // 2. Top Hits / Trending
        if (folderId.Equals("trending", StringComparison.OrdinalIgnoreCase))
        {
            return await GetTrendingItemsAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Recent / Saved Searches
        if (folderId.Equals("recent_searches", StringComparison.OrdinalIgnoreCase))
        {
            return GetRecentSearchesFolder();
        }

        // 4. Browse Genres
        if (folderId.Equals("genres", StringComparison.OrdinalIgnoreCase))
        {
            return GetGenreFolders();
        }

        // 5. Specific Search Query
        if (folderId.StartsWith("search_q_", StringComparison.OrdinalIgnoreCase))
        {
            var searchQuery = folderId.Substring("search_q_".Length);
            return await ExecuteSearchItemsAsync(searchQuery, cancellationToken).ConfigureAwait(false);
        }

        // 6. Album tracks view
        if (folderId.StartsWith("album_", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(folderId.Substring("album_".Length), out var albumId))
            {
                return await GetAlbumChannelItemsAsync(albumId, cancellationToken).ConfigureAwait(false);
            }
        }

        // 7. Artist view (Top tracks and Albums)
        if (folderId.StartsWith("artist_", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(folderId.Substring("artist_".Length), out var artistId))
            {
                return await GetArtistSubfoldersAsync(artistId, cancellationToken).ConfigureAwait(false);
            }
        }

        // 8. Artist Albums
        if (folderId.StartsWith("art_albums_", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(folderId.Substring("art_albums_".Length), out var artistId))
            {
                return await GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
            }
        }

        // 9. Artist Top Tracks
        if (folderId.StartsWith("art_tracks_", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(folderId.Substring("art_tracks_".Length), out var artistId))
            {
                return await GetArtistTopTracksAsync(artistId, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ChannelItemResult();
    }

    private ChannelItemResult GetRootFolders()
    {
        var items = new List<ChannelItemInfo>
        {
            new ChannelItemInfo
            {
                Id = "trending",
                Name = "Top Hits & Featured Tracks",
                Overview = "Explore trending top songs across the music catalogue.",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = "https://resources.tidal.com/images/153a5c0e/a879/4cba/9b7e/343c16260a92/640x640.jpg"
            },
            new ChannelItemInfo
            {
                Id = "recent_searches",
                Name = "Recent & Custom Searches",
                Overview = "Browse your recent free searches and custom queries (synced with the Monochrome Search page).",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = "https://resources.tidal.com/images/7376c221/ca36/4134/9605/65c829e0839f/640x640.jpg"
            },
            new ChannelItemInfo
            {
                Id = "genres",
                Name = "Browse by Genre & Style",
                Overview = "Explore Pop, Rock, Hip-Hop, Electronic, Jazz, Classical, and more.",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = "https://resources.tidal.com/images/a8323a6f/b9ad/448d/9b7e/241d7a8d5df1/640x640.jpg"
            }
        };

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private ChannelItemResult GetRecentSearchesFolder()
    {
        var queries = Plugin.Instance?.Configuration.RecentSearches ?? new List<string>();
        var items = queries.Select(q => new ChannelItemInfo
        {
            Id = $"search_q_{Uri.EscapeDataString(q)}",
            Name = $"Search: {q}",
            Overview = $"Browse results for '{q}' in Monochrome / TIDAL HiFi.",
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container
        }).ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private ChannelItemResult GetGenreFolders()
    {
        var genres = new[]
        {
            "Pop", "Rock", "Hip-Hop", "Electronic", "Jazz", "Classical",
            "Metal", "Indie", "R&B", "Dance", "Reggae", "Blues", "Ambient", "Soundtrack"
        };

        var items = genres.Select(g => new ChannelItemInfo
        {
            Id = $"search_q_{Uri.EscapeDataString(g)}",
            Name = $"Genre: {g}",
            Overview = $"Top tracks and albums in {g}.",
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container
        }).ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private async Task<ChannelItemResult> GetTrendingItemsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Search top hits to populate trending view
            var search = await _apiClient.SearchAsync("top hits", cancellationToken).ConfigureAwait(false);
            var items = new List<ChannelItemInfo>();

            if (search.Tracks?.Items != null)
            {
                foreach (var track in search.Tracks.Items)
                {
                    items.Add(MapTrackToChannelItem(track));
                }
            }

            if (search.Albums?.Items != null)
            {
                foreach (var album in search.Albums.Items.Take(10))
                {
                    items.Add(MapAlbumToChannelFolder(album));
                }
            }

            return new ChannelItemResult
            {
                Items = items,
                TotalRecordCount = items.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching trending items in MonochromeChannel.");
            return new ChannelItemResult();
        }
    }

    private async Task<ChannelItemResult> ExecuteSearchItemsAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var search = await _apiClient.SearchAsync(Uri.UnescapeDataString(query), cancellationToken).ConfigureAwait(false);
            var items = new List<ChannelItemInfo>();

            // Add Artists
            if (search.Artists?.Items != null)
            {
                foreach (var artist in search.Artists.Items.Take(5))
                {
                    items.Add(new ChannelItemInfo
                    {
                        Id = $"artist_{artist.Id}",
                        Name = $"Artist: {artist.Name}",
                        Type = ChannelItemType.Folder,
                        FolderType = ChannelFolderType.Container,
                        ImageUrl = MonochromeApiClient.GetArtistPictureUrl(artist.Picture)
                    });
                }
            }

            // Add Albums
            if (search.Albums?.Items != null)
            {
                foreach (var album in search.Albums.Items.Take(10))
                {
                    items.Add(MapAlbumToChannelFolder(album));
                }
            }

            // Add Tracks
            if (search.Tracks?.Items != null)
            {
                foreach (var track in search.Tracks.Items)
                {
                    items.Add(MapTrackToChannelItem(track));
                }
            }

            return new ChannelItemResult
            {
                Items = items,
                TotalRecordCount = items.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing search in MonochromeChannel for {Query}.", query);
            return new ChannelItemResult();
        }
    }

    private async Task<ChannelItemResult> GetAlbumChannelItemsAsync(long albumId, CancellationToken cancellationToken)
    {
        try
        {
            var tracks = await _apiClient.GetAlbumTracksAsync(albumId, cancellationToken).ConfigureAwait(false);
            var items = tracks.Select(MapTrackToChannelItem).ToList();

            return new ChannelItemResult
            {
                Items = items,
                TotalRecordCount = items.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tracks for album {AlbumId}.", albumId);
            return new ChannelItemResult();
        }
    }

    private async Task<ChannelItemResult> GetArtistSubfoldersAsync(long artistId, CancellationToken cancellationToken)
    {
        var artist = await _apiClient.GetArtistAsync(artistId, cancellationToken).ConfigureAwait(false);
        var artistName = artist?.Name ?? "Artist";
        var pictureUrl = MonochromeApiClient.GetArtistPictureUrl(artist?.Picture);

        var items = new List<ChannelItemInfo>
        {
            new ChannelItemInfo
            {
                Id = $"art_tracks_{artistId}",
                Name = $"{artistName} - Top Tracks",
                Overview = $"Top popular tracks by {artistName}.",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = pictureUrl
            },
            new ChannelItemInfo
            {
                Id = $"art_albums_{artistId}",
                Name = $"{artistName} - Albums & Discography",
                Overview = $"Full album discography for {artistName}.",
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                ImageUrl = pictureUrl
            }
        };

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private async Task<ChannelItemResult> GetArtistAlbumsAsync(long artistId, CancellationToken cancellationToken)
    {
        var albums = await _apiClient.GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
        var items = albums.Select(MapAlbumToChannelFolder).ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private async Task<ChannelItemResult> GetArtistTopTracksAsync(long artistId, CancellationToken cancellationToken)
    {
        var tracks = await _apiClient.GetArtistTopTracksAsync(artistId, cancellationToken).ConfigureAwait(false);
        var items = tracks.Select(MapTrackToChannelItem).ToList();

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count
        };
    }

    private static ChannelItemInfo MapTrackToChannelItem(TidalTrackItem track)
    {
        var artistName = track.Artist?.Name ?? track.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist";
        var coverUrl = MonochromeApiClient.GetCoverUrl(track.Album?.Cover);

        return new ChannelItemInfo
        {
            Id = $"track_{track.Id}",
            Name = track.Title,
            Artists = new List<string> { artistName },
            AlbumArtists = new List<string> { artistName },
            SeriesName = track.Album?.Title,
            RunTimeTicks = TimeSpan.FromSeconds(track.Duration).Ticks,
            IndexNumber = track.TrackNumber,
            ParentIndexNumber = track.VolumeNumber,
            ImageUrl = coverUrl,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Song,
            MediaType = ChannelMediaType.Audio
        };
    }

    private static ChannelItemInfo MapAlbumToChannelFolder(TidalAlbumRef album)
    {
        var artistName = album.Artist?.Name ?? album.Artists?.FirstOrDefault()?.Name ?? string.Empty;
        var displayName = string.IsNullOrEmpty(artistName) ? album.Title : $"{album.Title} - {artistName}";

        return new ChannelItemInfo
        {
            Id = $"album_{album.Id}",
            Name = displayName,
            Overview = album.ReleaseDate != null ? $"Release: {album.ReleaseDate}" : null,
            ImageUrl = MonochromeApiClient.GetCoverUrl(album.Cover),
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        var cleanId = id.StartsWith("track_", StringComparison.OrdinalIgnoreCase)
            ? id.Substring("track_".Length)
            : id;

        if (!long.TryParse(cleanId, out var trackId))
        {
            throw new ArgumentException($"Invalid track ID: {id}", nameof(id));
        }

        _logger.LogInformation("Resolving cached audio stream for channel track ID: {TrackId}", trackId);
        var localFile = await _apiClient.EnsureTrackCachedAsync(trackId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var isMp4 = localFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        var mediaSource = new MediaSourceInfo
        {
            Id = id,
            Path = localFile,
            Protocol = MediaProtocol.File,
            Container = isMp4 ? "mp4" : "flac",
            IsRemote = false,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 0,
                    IsDefault = true,
                    Codec = "flac"
                }
            }
        };

        return new[] { mediaSource };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages()
    {
        return new[]
        {
            ImageType.Primary,
            ImageType.Thumb
        };
    }
}
