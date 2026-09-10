using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Monochrome.Api;
using Jellyfin.Plugin.Monochrome.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Monochrome.Similar;

/// <summary>
/// Provides similar track recommendations and autoplay radio for Monochrome / TIDAL music in Jellyfin.
/// Intercepts Instant Mix, radio, and playback queue recommendations so the next song in the same style
/// plays automatically.
/// </summary>
public class MonochromeSimilarItemsProvider : ILocalSimilarItemsProvider<Audio>, ISimilarItemsProvider
{
    private readonly MonochromeApiClient _apiClient;
    private readonly MonochromeSearchProvider _searchProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MonochromeSimilarItemsProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonochromeSimilarItemsProvider"/> class.
    /// </summary>
    public MonochromeSimilarItemsProvider(
        MonochromeApiClient apiClient,
        MonochromeSearchProvider searchProvider,
        ILibraryManager libraryManager,
        ILogger<MonochromeSimilarItemsProvider> logger)
    {
        _apiClient = apiClient;
        _searchProvider = searchProvider;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Monochrome Similar Tracks";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.LocalMetadataProvider;

    /// <inheritdoc />
    public TimeSpan? CacheDuration => TimeSpan.FromHours(2);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseItem>> GetSimilarItemsAsync(
        Audio item,
        SimilarItemsQuery query,
        CancellationToken cancellationToken)
    {
        var trackIdStr = item.GetProviderId("TidalTrack") ?? item.GetProviderId("MonochromeTrack");
        if (string.IsNullOrEmpty(trackIdStr) && item.Path != null && item.Path.StartsWith("monochrome://track/", StringComparison.OrdinalIgnoreCase))
        {
            trackIdStr = item.Path.Substring("monochrome://track/".Length);
        }

        if (string.IsNullOrEmpty(trackIdStr) || !long.TryParse(trackIdStr, out var trackId))
        {
            return [];
        }

        try
        {
            _logger.LogInformation("Fetching radio recommendations for track '{Title}' (ID: {TrackId})...", item.Name, trackId);
            var radioTracks = await _apiClient.GetTrackRadioAsync(trackId, 30, cancellationToken).ConfigureAwait(false);
            if (radioTracks.Count == 0)
            {
                return [];
            }

            var parentFolder = _searchProvider.GetMusicParentFolder(query.User?.Id);
            var results = new List<BaseItem>();

            foreach (var rTrack in radioTracks)
            {
                if (rTrack.Id == trackId)
                {
                    continue; // Skip the currently playing track itself
                }

                var trackGuid = MonochromeSearchProvider.GetDeterministicGuid($"monochrome_track_{rTrack.Id}");
                var trackItem = await _searchProvider.EnsureTrackItemAsync(trackGuid, rTrack, parentFolder, cancellationToken).ConfigureAwait(false);
                if (trackItem != null)
                {
                    results.Add(trackItem);
                }
            }

            _logger.LogInformation("Monochrome radio returned {Count} similar tracks for '{Title}'.", results.Count, item.Name);
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get similar items for track {TrackId}", trackId);
            return [];
        }
    }
}
