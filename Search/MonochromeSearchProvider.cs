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
            foreach (var track in catalog.Tracks.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trackGuid = GetDeterministicGuid($"monochrome_track_{track.Id}");
                if (!seen.Add(trackGuid))
                {
                    continue;
                }

                await EnsureTrackItemAsync(trackGuid, track, parentFolder, cancellationToken).ConfigureAwait(false);
                var score = CalculateScore(track.Title, searchTerm, 100f, 96f, 92f, 80f);
                yield return new SearchResult(trackGuid, score);
            }
        }

        // 2. Albums (MusicAlbum items)
        if (catalog.Albums?.Items != null && AllowsType(query, BaseItemKind.MusicAlbum))
        {
            foreach (var album in catalog.Albums.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var albumGuid = GetDeterministicGuid($"monochrome_album_{album.Id}");
                if (!seen.Add(albumGuid))
                {
                    continue;
                }

                await EnsureAlbumItemAsync(albumGuid, album, parentFolder, cancellationToken).ConfigureAwait(false);
                var score = CalculateScore(album.Title, searchTerm, 98f, 94f, 88f, 70f);
                yield return new SearchResult(albumGuid, score);
            }
        }

        // 3. Artists (MusicArtist items)
        if (catalog.Artists?.Items != null && AllowsType(query, BaseItemKind.MusicArtist))
        {
            foreach (var artist in catalog.Artists.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var artistGuid = GetDeterministicGuid($"monochrome_artist_{artist.Id}");
                if (!seen.Add(artistGuid))
                {
                    continue;
                }

                await EnsureArtistItemAsync(artistGuid, artist, parentFolder, cancellationToken).ConfigureAwait(false);
                var score = CalculateScore(artist.Name, searchTerm, 97f, 93f, 85f, 65f);
                yield return new SearchResult(artistGuid, score);
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

    public async Task<Audio?> EnsureTrackItemAsync(Guid trackGuid, TidalTrackItem track, Folder? parentFolder, CancellationToken cancellationToken)
    {
        var artistName = track.Artists?.FirstOrDefault()?.Name ?? track.Artist?.Name ?? "Unknown Artist";
        var existing = _libraryManager.GetItemById(trackGuid);
        if (existing != null)
        {
            bool needsUpdate = false;
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty || existing.ChannelId != Guid.Empty))
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                existing.ChannelId = Guid.Empty;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                try
                {
                    await _libraryManager.UpdateItemAsync(existing, parentFolder!, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Updated ParentId for track {TrackId}", track.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for track {TrackId}", track.Id);
                }
            }

            return existing as Audio;
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
            IndexNumber = track.TrackNumber,
            ExternalId = $"track_{track.Id}"
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
            audio.ChannelId = Guid.Empty;
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
            _logger.LogInformation("Indexed Monochrome track: '{Title}' by '{Artist}' ({TrackId}) under '{ParentName}'", track.Title, artistName, track.Id, parentFolder?.Name ?? "Root");
            return audio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome track {TrackId} in library", track.Id);
            return null;
        }
    }

    public async Task<MusicAlbum?> EnsureAlbumItemAsync(Guid albumGuid, TidalAlbumRef album, Folder? parentFolder, CancellationToken cancellationToken)
    {
        var existing = _libraryManager.GetItemById(albumGuid);
        if (existing != null)
        {
            bool needsUpdate = false;
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty || existing.ChannelId != Guid.Empty))
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                existing.ChannelId = Guid.Empty;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                try
                {
                    await _libraryManager.UpdateItemAsync(existing, parentFolder!, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Updated ParentId for album {AlbumId}", album.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for album {AlbumId}", album.Id);
                }
            }

            return existing as MusicAlbum;
        }

        var artistName = album.Artist?.Name ?? album.Artists?.FirstOrDefault()?.Name ?? "Unknown Artist";
        var musicAlbum = new MusicAlbum
        {
            Id = albumGuid,
            Name = album.Title,
            Artists = [ artistName ],
            AlbumArtists = [ artistName ],
            ProductionYear = album.ReleaseDate != null && DateTime.TryParse(album.ReleaseDate, out var dt) ? dt.Year : null,
            ExternalId = $"album_{album.Id}"
        };

        if (parentFolder != null)
        {
            musicAlbum.SetParent(parentFolder);
            musicAlbum.ParentId = parentFolder.Id;
            musicAlbum.ChannelId = Guid.Empty;
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
            _logger.LogInformation("Indexed Monochrome album: '{Title}' ({AlbumId}) under '{ParentName}'", album.Title, album.Id, parentFolder?.Name ?? "Root");

            // Background populate album tracks so opening the album shows all tracks
            _ = Task.Run(async () =>
            {
                try
                {
                    var tracks = await _apiClient.GetAlbumTracksAsync(album.Id, CancellationToken.None).ConfigureAwait(false);
                    foreach (var t in tracks)
                    {
                        var tGuid = GetDeterministicGuid($"monochrome_track_{t.Id}");
                        await EnsureTrackItemAsync(tGuid, t, musicAlbum, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not pre-fetch tracks for album {AlbumId}", album.Id);
                }
            });

            return musicAlbum;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome album {AlbumId} in library", album.Id);
            return null;
        }
    }

    public async Task<MusicArtist?> EnsureArtistItemAsync(Guid artistGuid, TidalArtistRef artist, Folder? parentFolder, CancellationToken cancellationToken)
    {
        var existing = _libraryManager.GetItemById(artistGuid);
        if (existing != null)
        {
            bool needsUpdate = false;
            if (parentFolder != null && (existing.ParentId != parentFolder.Id || existing.ParentId == Guid.Empty || existing.ChannelId != Guid.Empty))
            {
                existing.SetParent(parentFolder);
                existing.ParentId = parentFolder.Id;
                existing.ChannelId = Guid.Empty;
                needsUpdate = true;
            }

            if (needsUpdate)
            {
                try
                {
                    await _libraryManager.UpdateItemAsync(existing, parentFolder!, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("Updated ParentId for artist {ArtistId}", artist.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not update ParentId for artist {ArtistId}", artist.Id);
                }
            }

            return existing as MusicArtist;
        }

        var musicArtist = new MusicArtist
        {
            Id = artistGuid,
            Name = artist.Name,
            ExternalId = $"artist_{artist.Id}"
        };

        if (parentFolder != null)
        {
            musicArtist.SetParent(parentFolder);
            musicArtist.ParentId = parentFolder.Id;
            musicArtist.ChannelId = Guid.Empty;
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
            _logger.LogInformation("Indexed Monochrome artist: '{Name}' ({ArtistId}) under '{ParentName}'", artist.Name, artist.Id, parentFolder?.Name ?? "Root");

            // Background pre-fetch artist top tracks and albums so navigating to artist shows tracks & albums
            _ = Task.Run(async () =>
            {
                try
                {
                    var topTracks = await _apiClient.GetArtistTopTracksAsync(artist.Id, CancellationToken.None).ConfigureAwait(false);
                    foreach (var t in topTracks)
                    {
                        var tGuid = GetDeterministicGuid($"monochrome_track_{t.Id}");
                        await EnsureTrackItemAsync(tGuid, t, parentFolder, CancellationToken.None).ConfigureAwait(false);
                    }

                    var albums = await _apiClient.GetArtistAlbumsAsync(artist.Id, CancellationToken.None).ConfigureAwait(false);
                    foreach (var alb in albums)
                    {
                        var albGuid = GetDeterministicGuid($"monochrome_album_{alb.Id}");
                        await EnsureAlbumItemAsync(albGuid, alb, parentFolder, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not pre-fetch items for artist {ArtistId}", artist.Id);
                }
            });

            return musicArtist;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register Monochrome artist {ArtistId} in library", artist.Id);
            return null;
        }
    }

    public Folder? GetMusicParentFolder(Guid? userId)
    {
        try
        {
            var user = userId.HasValue && userId.Value != Guid.Empty ? _userManager.GetUserById(userId.Value) : null;
            user ??= _userManager.GetUsers().FirstOrDefault();

            // 1. Look through the user's accessible views (the exact same views used by SearchManager's access filtering)
            if (user != null)
            {
                var userViews = _userViewManager.GetUserViews(new UserViewQuery
                {
                    User = user,
                    IncludeHidden = true,
                    IncludeExternalContent = true
                });

                // Priority 1: User has a dedicated music library / view
                var musicView = userViews.FirstOrDefault(v =>
                    (v is CollectionFolder cf && (cf.CollectionType == CollectionType.music || string.Equals(cf.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)))
                    || (v is UserView uv && (uv.ViewType == CollectionType.music || string.Equals(uv.ViewType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)))
                    || v.Name.Contains("music", StringComparison.OrdinalIgnoreCase)
                    || v.Name.Contains("musica", StringComparison.OrdinalIgnoreCase));

                if (musicView != null)
                {
                    var phys = ResolvePhysicalFolderFromView(musicView);
                    if (phys != null)
                    {
                        _logger.LogInformation("Resolved music parent physical folder from music view '{Name}' -> '{PhysName}' ({PhysId})", musicView.Name, phys.Name, phys.Id);
                        return phys;
                    }
                }

                // Priority 2: Any accessible user library folder (e.g. Movies, Series, etc.)
                // Placing music under a real library physical folder ensures TopParentId is valid and passes SearchManager's user access filter!
                foreach (var view in userViews)
                {
                    var phys = ResolvePhysicalFolderFromView(view);
                    if (phys != null)
                    {
                        _logger.LogInformation("Resolved music parent physical folder from accessible view '{Name}' -> '{PhysName}' ({PhysId})", view.Name, phys.Name, phys.Id);
                        return phys;
                    }
                }
            }

            // 2. Look in GetUserRootFolder().Children (which holds CollectionFolders on the server)
            var userRoot = _libraryManager.GetUserRootFolder();
            if (userRoot?.Children != null)
            {
                var rootFolders = userRoot.Children.OfType<Folder>().ToList();
                var musicCf = rootFolders.OfType<CollectionFolder>().FirstOrDefault(cf =>
                    cf.CollectionType == CollectionType.music
                    || string.Equals(cf.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)
                    || cf.Name.Contains("music", StringComparison.OrdinalIgnoreCase)
                    || cf.Name.Contains("musica", StringComparison.OrdinalIgnoreCase));

                if (musicCf != null)
                {
                    var phys = ResolvePhysicalFolderFromView(musicCf);
                    if (phys != null)
                    {
                        return phys;
                    }
                }

                foreach (var folder in rootFolders)
                {
                    var phys = ResolvePhysicalFolderFromView(folder);
                    if (phys != null)
                    {
                        return phys;
                    }
                }

                if (rootFolders.Count > 0)
                {
                    return rootFolders[0];
                }
            }

            // 3. Look in _libraryManager.GetVirtualFolders()
            try
            {
                var virtualFolders = _libraryManager.GetVirtualFolders();
                if (virtualFolders != null)
                {
                    var musicVf = virtualFolders.FirstOrDefault(vf =>
                        string.Equals(vf.CollectionType?.ToString(), "music", StringComparison.OrdinalIgnoreCase)
                        || vf.Name.Contains("music", StringComparison.OrdinalIgnoreCase)
                        || vf.Name.Contains("musica", StringComparison.OrdinalIgnoreCase));

                    if (musicVf != null && Guid.TryParse(musicVf.ItemId, out var musicFolderGuid))
                    {
                        var folder = _libraryManager.GetItemById(musicFolderGuid);
                        if (folder != null)
                        {
                            var phys = ResolvePhysicalFolderFromView(folder);
                            if (phys != null)
                            {
                                _logger.LogInformation("Resolved music parent physical folder from virtual folder '{Name}' -> '{PhysName}' ({PhysId})", musicVf.Name, phys.Name, phys.Id);
                                return phys;
                            }
                        }
                    }

                    foreach (var vf in virtualFolders)
                    {
                        if (Guid.TryParse(vf.ItemId, out var vfGuid))
                        {
                            var folder = _libraryManager.GetItemById(vfGuid);
                            if (folder != null)
                            {
                                var phys = ResolvePhysicalFolderFromView(folder);
                                if (phys != null)
                                {
                                    _logger.LogInformation("Resolved music parent physical folder from virtual folder '{Name}' -> '{PhysName}' ({PhysId})", vf.Name, phys.Name, phys.Id);
                                    return phys;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not check GetVirtualFolders()");
            }

            // 4. Look in _libraryManager.RootFolder
            try
            {
                var root = _libraryManager.RootFolder;
                if (root?.Children != null)
                {
                    foreach (var child in root.Children.OfType<Folder>())
                    {
                        var phys = ResolvePhysicalFolderFromView(child);
                        if (phys != null)
                        {
                            return phys;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not check RootFolder children");
            }

            return _libraryManager.GetUserRootFolder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resolve physical music parent folder");
            return _libraryManager.GetUserRootFolder();
        }
    }

    private Folder? ResolvePhysicalFolderFromView(BaseItem view)
    {
        if (view is UserView uv)
        {
            if (uv.DisplayParentId != Guid.Empty)
            {
                var parent = _libraryManager.GetItemById(uv.DisplayParentId);
                if (parent is CollectionFolder cf && cf.PhysicalFolderIds.Length > 0)
                {
                    if (_libraryManager.GetItemById(cf.PhysicalFolderIds[0]) is Folder phys)
                    {
                        return phys;
                    }
                }

                if (parent is Folder pf)
                {
                    return pf;
                }
            }

            if (uv.ParentId != Guid.Empty)
            {
                var parent = _libraryManager.GetItemById(uv.ParentId);
                if (parent is CollectionFolder cf && cf.PhysicalFolderIds.Length > 0)
                {
                    if (_libraryManager.GetItemById(cf.PhysicalFolderIds[0]) is Folder phys)
                    {
                        return phys;
                    }
                }

                if (parent is Folder pf)
                {
                    return pf;
                }
            }
        }

        if (view is CollectionFolder colf)
        {
            if (colf.PhysicalFolderIds.Length > 0 && _libraryManager.GetItemById(colf.PhysicalFolderIds[0]) is Folder phys)
            {
                return phys;
            }

            return colf;
        }

        if (view is Folder folder)
        {
            return folder;
        }

        return null;
    }

    private static float CalculateScore(string? title, string searchTerm, float exactScore, float startsWithScore, float containsScore, float baseScore)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(searchTerm))
        {
            return baseScore;
        }

        var t = title.Trim();
        var s = searchTerm.Trim();

        if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase))
        {
            return exactScore;
        }

        if (t.StartsWith(s, StringComparison.OrdinalIgnoreCase))
        {
            return startsWithScore;
        }

        if (t.Contains(s, StringComparison.OrdinalIgnoreCase))
        {
            return containsScore;
        }

        return baseScore;
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

    public static Guid GetDeterministicGuid(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
}
