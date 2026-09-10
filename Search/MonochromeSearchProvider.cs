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

        RecordSearchQuery(searchTerm);

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
        var artistName = track.Artists?.FirstOrDefault()?.Name ?? track.Artist?.Name ?? "Unknown Artist";
        var existing = _libraryManager.GetItemById(trackGuid);
        if (existing != null)
        {
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty))
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
            Artists = track.Artists?.Select(a => a.Name).ToList() ?? [ artistName ],
            AlbumArtists = [ artistName ],
            Album = track.Album?.Title ?? "",
            RunTimeTicks = track.Duration * TimeSpan.TicksPerSecond,
            Path = $"monochrome://track/{track.Id}",
            Container = "mp4",
            IndexNumber = track.TrackNumber
        };

        if (track.Album?.ReleaseDate != null && DateTime.TryParse(track.Album.ReleaseDate, out var dt))
        {
            audio.ProductionYear = dt.Year;
            audio.PremiereDate = dt;
        }

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
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty))
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
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty))
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
            // 1. Get all root collection folders in Jellyfin
            var allCollectionFolders = _libraryManager.RootFolder.Children.OfType<CollectionFolder>().ToList();

            // 2. If a specific user is performing the search, find their accessible collection folders
            var accessibleFolders = new List<CollectionFolder>();
            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                var user = _userManager.GetUserById(userId.Value);
                if (user != null)
                {
                    var userRoot = _libraryManager.GetUserRootFolder();
                    var userViews = userRoot.GetChildren(user, true).OfType<Folder>().ToList();

                    foreach (var view in userViews)
                    {
                        if (view is UserView uv)
                        {
                            if (uv.DisplayParent is CollectionFolder dpcf && !accessibleFolders.Contains(dpcf))
                            {
                                accessibleFolders.Add(dpcf);
                            }
                            else if (uv.DisplayParentId != Guid.Empty && _libraryManager.GetItemById(uv.DisplayParentId) is CollectionFolder cf1 && !accessibleFolders.Contains(cf1))
                            {
                                accessibleFolders.Add(cf1);
                            }
                            else if (uv.ParentId != Guid.Empty && _libraryManager.GetItemById(uv.ParentId) is CollectionFolder cf2 && !accessibleFolders.Contains(cf2))
                            {
                                accessibleFolders.Add(cf2);
                            }
                            else
                            {
                                var matching = allCollectionFolders.FirstOrDefault(cf => cf.Id == uv.Id || string.Equals(cf.Name, uv.Name, StringComparison.OrdinalIgnoreCase));
                                if (matching != null && !accessibleFolders.Contains(matching))
                                {
                                    accessibleFolders.Add(matching);
                                }
                            }
                        }
                        else if (view is CollectionFolder cf && !accessibleFolders.Contains(cf))
                        {
                            accessibleFolders.Add(cf);
                        }
                    }
                }
            }

            // Fall back to all server collection folders if none resolved specifically for the user
            if (accessibleFolders.Count == 0)
            {
                accessibleFolders = allCollectionFolders;
            }

            // 3. Look for a Music library among accessible folders
            var musicFolder = accessibleFolders.FirstOrDefault(f =>
                f.CollectionType == CollectionType.music
                || string.Equals(f.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("music", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("musica", StringComparison.OrdinalIgnoreCase));

            if (musicFolder != null)
            {
                return musicFolder;
            }

            // 4. Look for ANY Music library on the entire server
            var anyMusicFolder = allCollectionFolders.FirstOrDefault(f =>
                f.CollectionType == CollectionType.music
                || string.Equals(f.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("music", StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains("musica", StringComparison.OrdinalIgnoreCase));

            if (anyMusicFolder != null)
            {
                return anyMusicFolder;
            }

            // 5. If no music folder exists, use the first accessible collection folder (e.g. Movies / Series)
            // Giving the item a real CollectionFolder guarantees TopParentId is non-null and belongs to user's TopParentIds.
            if (accessibleFolders.Count > 0)
            {
                return accessibleFolders[0];
            }

            if (allCollectionFolders.Count > 0)
            {
                return allCollectionFolders[0];
            }

            // 6. If server has NO collection folders at all, create a virtual music library!
            try
            {
                _logger.LogInformation("No collection folders found in Jellyfin. Auto-creating 'Monochrome Music' virtual library...");
                _libraryManager.AddVirtualFolder("Monochrome Music", CollectionTypeOptions.music, new LibraryOptions(), false)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                var newFolder = _libraryManager.RootFolder.Children.OfType<CollectionFolder>()
                    .FirstOrDefault(f => f.Name == "Monochrome Music" || f.CollectionType == CollectionType.music);
                if (newFolder != null)
                {
                    return newFolder;
                }
            }
            catch (Exception createEx)
            {
                _logger.LogWarning(createEx, "Could not auto-create virtual music folder");
            }

            return _libraryManager.RootFolder;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not locate specific music library folder, using root folder fallback");
            return _libraryManager.RootFolder;
        }
    }

    private void RecordSearchQuery(string query)
    {
        try
        {
            if (Plugin.Instance == null || string.IsNullOrWhiteSpace(query) || query.Length < 2)
            {
                return;
            }

            var config = Plugin.Instance.Configuration;
            lock (config.RecentSearches)
            {
                if (!config.RecentSearches.Contains(query, StringComparer.OrdinalIgnoreCase))
                {
                    config.RecentSearches.Insert(0, query);
                    if (config.RecentSearches.Count > 20)
                    {
                        config.RecentSearches.RemoveAt(config.RecentSearches.Count - 1);
                    }

                    Plugin.Instance.SaveConfiguration();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not record recent search query");
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
