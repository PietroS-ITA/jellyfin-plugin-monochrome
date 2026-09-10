using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Monochrome.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Search;

/// <summary>
/// Native Jellyfin 12.0 Search Provider.
/// Intercepts global search queries from any user (base users and admins)
/// and seamlessly contributes Monochrome/TIDAL music results to Jellyfin's global search.
/// </summary>
public sealed class MonochromeSearchProvider : IExternalSearchProvider
{
    private readonly MonochromeApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly ILogger<MonochromeSearchProvider> _logger;

    public MonochromeSearchProvider(
        MonochromeApiClient apiClient,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserViewManager userViewManager,
        ILogger<MonochromeSearchProvider> logger)
    {
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userViewManager = userViewManager;
        _logger = logger;
    }

    public string Name => "Monochrome Music Search";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    public int Priority => 50;

    public bool CanSearch(SearchProviderQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            return false;
        }

        var term = query.SearchTerm.Trim();
        if (term.Length == 0)
        {
            return false;
        }

        if (query.IncludeItemTypes.Length > 0)
        {
            var allowsMusic = query.IncludeItemTypes.Contains(BaseItemKind.Audio)
                || query.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum)
                || query.IncludeItemTypes.Contains(BaseItemKind.MusicArtist);

            if (!allowsMusic)
            {
                return false;
            }
        }

        if (query.ExcludeItemTypes.Length > 0)
        {
            if (query.ExcludeItemTypes.Contains(BaseItemKind.Audio)
                && query.ExcludeItemTypes.Contains(BaseItemKind.MusicAlbum)
                && query.ExcludeItemTypes.Contains(BaseItemKind.MusicArtist))
            {
                return false;
            }
        }

        if (query.MediaTypes.Length > 0 && !query.MediaTypes.Contains(MediaType.Audio))
        {
            return false;
        }

        return true;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!CanSearch(query))
        {
            yield break;
        }

        var searchTerm = query.SearchTerm.Trim();

        var parentFolder = GetMusicParentFolder(query.UserId);

        TidalSearchResponse? catalog = null;
        try
        {
            catalog = await _apiClient.SearchAsync(searchTerm, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing global search in Monochrome for '{SearchTerm}'", searchTerm);
            yield break;
        }

        if (catalog == null)
        {
            yield break;
        }

        var seen = new HashSet<Guid>();

        // 1. Tracks (Audio items)
        if (catalog.Tracks?.Items != null && AllowsType(query, BaseItemKind.Audio))
        {
            foreach (var track in catalog.Tracks.Items.Take(15))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackGuid = GetDeterministicGuid($"monochrome_track_{track.Id}");
                if (!seen.Add(trackGuid))
                {
                    continue;
                }

                EnsureTrackItem(trackGuid, track, parentFolder);
                yield return new SearchResult(trackGuid, 0.95f);
            }
        }

        // 2. Albums (MusicAlbum items)
        if (catalog.Albums?.Items != null && AllowsType(query, BaseItemKind.MusicAlbum))
        {
            foreach (var album in catalog.Albums.Items.Take(8))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var albumGuid = GetDeterministicGuid($"monochrome_album_{album.Id}");
                if (!seen.Add(albumGuid))
                {
                    continue;
                }

                EnsureAlbumItem(albumGuid, album, parentFolder);
                yield return new SearchResult(albumGuid, 0.90f);
            }
        }

        // 3. Artists (MusicArtist items)
        if (catalog.Artists?.Items != null && AllowsType(query, BaseItemKind.MusicArtist))
        {
            foreach (var artist in catalog.Artists.Items.Take(5))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var artistGuid = GetDeterministicGuid($"monochrome_artist_{artist.Id}");
                if (!seen.Add(artistGuid))
                {
                    continue;
                }

                EnsureArtistItem(artistGuid, artist, parentFolder);
                yield return new SearchResult(artistGuid, 0.85f);
            }
        }
    }

    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await foreach (var item in SearchAsync(query, cancellationToken).ConfigureAwait(false))
        {
            results.Add(item);
        }

        return results;
    }

    private void EnsureTrackItem(Guid trackGuid, TidalTrackItem track, Folder? parentFolder)
    {
        var existing = _libraryManager.GetItemById(trackGuid);
        if (existing != null)
        {
            if (existing.ParentId == Guid.Empty && parentFolder != null)
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                try
                {
                    _libraryManager.UpdateItemAsync(existing, parentFolder, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for track {TrackId}", track.Id);
                }
            }

            return;
        }

        var audio = new Audio
        {
            Id = trackGuid,
            Name = track.Title,
            Artists = track.Artists?.Select(a => a.Name).ToList() ?? [ track.Artist?.Name ?? "Unknown Artist" ],
            Album = track.Album?.Title ?? "",
            RunTimeTicks = track.Duration * TimeSpan.TicksPerSecond,
            Path = $"monochrome://track/{track.Id}",
            Container = "mp4",
            IndexNumber = track.TrackNumber
        };

        if (parentFolder != null)
        {
            audio.SetParent(parentFolder);
            audio.ParentId = parentFolder.Id;
        }

        audio.SetProviderId("MonochromeTrack", track.Id.ToString(CultureInfo.InvariantCulture));
        audio.SetProviderId("TidalTrack", track.Id.ToString(CultureInfo.InvariantCulture));

        var coverUrl = track.Album?.Cover;
        if (!string.IsNullOrEmpty(coverUrl))
        {
            var resolvedCover = MonochromeApiClient.GetCoverUrl(coverUrl, 640);
            if (!string.IsNullOrEmpty(resolvedCover))
            {
                audio.SetImage(new ItemImageInfo
                {
                    Path = resolvedCover,
                    Type = ImageType.Primary
                }, 0);
            }
        }

        try
        {
            _libraryManager.CreateItem(audio, parentFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome track {TrackId} in library", track.Id);
        }
    }

    private void EnsureAlbumItem(Guid albumGuid, TidalAlbumRef album, Folder? parentFolder)
    {
        var existing = _libraryManager.GetItemById(albumGuid);
        if (existing != null)
        {
            if (existing.ParentId == Guid.Empty && parentFolder != null)
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                try
                {
                    _libraryManager.UpdateItemAsync(existing, parentFolder, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for album {AlbumId}", album.Id);
                }
            }

            return;
        }

        var artistName = album.Artist?.Name ?? "Unknown Artist";
        var musicAlbum = new MusicAlbum
        {
            Id = albumGuid,
            Name = album.Title,
            Artists = [ artistName ],
            AlbumArtists = [ artistName ],
            ProductionYear = album.ReleaseDate != null && DateTime.TryParse(album.ReleaseDate, out var dt) ? dt.Year : null
        };

        if (parentFolder != null)
        {
            musicAlbum.SetParent(parentFolder);
            musicAlbum.ParentId = parentFolder.Id;
        }

        musicAlbum.SetProviderId("MonochromeAlbum", album.Id.ToString(CultureInfo.InvariantCulture));
        musicAlbum.SetProviderId("TidalAlbum", album.Id.ToString(CultureInfo.InvariantCulture));

        var coverUrl = album.Cover;
        if (!string.IsNullOrEmpty(coverUrl))
        {
            var resolvedCover = MonochromeApiClient.GetCoverUrl(coverUrl, 640);
            if (!string.IsNullOrEmpty(resolvedCover))
            {
                musicAlbum.SetImage(new ItemImageInfo
                {
                    Path = resolvedCover,
                    Type = ImageType.Primary
                }, 0);
            }
        }

        try
        {
            _libraryManager.CreateItem(musicAlbum, parentFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome album {AlbumId} in library", album.Id);
        }
    }

    private void EnsureArtistItem(Guid artistGuid, TidalArtistRef artist, Folder? parentFolder)
    {
        var existing = _libraryManager.GetItemById(artistGuid);
        if (existing != null)
        {
            if (existing.ParentId == Guid.Empty && parentFolder != null)
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                try
                {
                    _libraryManager.UpdateItemAsync(existing, parentFolder, ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for artist {ArtistId}", artist.Id);
                }
            }

            return;
        }

        var musicArtist = new MusicArtist
        {
            Id = artistGuid,
            Name = artist.Name
        };

        if (parentFolder != null)
        {
            musicArtist.SetParent(parentFolder);
            musicArtist.ParentId = parentFolder.Id;
        }

        musicArtist.SetProviderId("MonochromeArtist", artist.Id.ToString(CultureInfo.InvariantCulture));
        musicArtist.SetProviderId("TidalArtist", artist.Id.ToString(CultureInfo.InvariantCulture));

        var picUrl = artist.Picture;
        if (!string.IsNullOrEmpty(picUrl))
        {
            var resolvedPic = MonochromeApiClient.GetArtistPictureUrl(picUrl, 640);
            if (!string.IsNullOrEmpty(resolvedPic))
            {
                musicArtist.SetImage(new ItemImageInfo
                {
                    Path = resolvedPic,
                    Type = ImageType.Primary
                }, 0);
            }
        }

        try
        {
            _libraryManager.CreateItem(musicArtist, parentFolder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome artist {ArtistId} in library", artist.Id);
        }
    }

    private Folder? GetMusicParentFolder(Guid? userId)
    {
        try
        {
            // 1. Try to find a music library from user's accessible library folders
            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user != null)
                {
                    var userRoot = _libraryManager.GetUserRootFolder();
                    var userFolders = userRoot.GetChildren(user, true).OfType<Folder>().ToList();

                    var musicFolder = userFolders.FirstOrDefault(f =>
                        f is CollectionFolder cf && cf.CollectionType == CollectionType.music);
                    if (musicFolder != null)
                    {
                        return musicFolder;
                    }

                    if (userFolders.Count > 0)
                    {
                        return userFolders[0];
                    }
                }
            }

            // 2. Try global virtual folders
            var virtualFolders = _libraryManager.GetVirtualFolders();
            var musicVirtualFolder = virtualFolders.FirstOrDefault(v => v.CollectionType == CollectionTypeOptions.music)
                ?? virtualFolders.FirstOrDefault();
            if (musicVirtualFolder != null && Guid.TryParse(musicVirtualFolder.ItemId, out var folderId))
            {
                if (_libraryManager.GetItemById(folderId) is Folder folder)
                {
                    return folder;
                }
            }

            // 3. Fallback to RootFolder children
            var rootFolders = _libraryManager.RootFolder.Children.OfType<Folder>().ToList();
            var rootMusic = rootFolders.FirstOrDefault(f => f is CollectionFolder cf && cf.CollectionType == CollectionType.music)
                ?? rootFolders.FirstOrDefault();
            if (rootMusic != null)
            {
                return rootMusic;
            }

            return _libraryManager.RootFolder;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not locate specific music library folder, using root folder fallback");
            return _libraryManager.RootFolder;
        }
    }

    private static bool AllowsType(SearchProviderQuery query, BaseItemKind kind)
    {
        if (query.IncludeItemTypes.Length > 0)
        {
            return query.IncludeItemTypes.Contains(kind);
        }

        if (query.ExcludeItemTypes.Length > 0)
        {
            return !query.ExcludeItemTypes.Contains(kind);
        }

        return true;
    }

    private static Guid GetDeterministicGuid(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
}
