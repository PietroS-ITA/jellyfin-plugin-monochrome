using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Monochrome.Configuration;

/// <summary>
/// Plugin configuration for Monochrome Music.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ApiBaseUrl = "https://monochrome.tf";
        AudioQuality = "LOSSLESS"; // LOSSLESS, HI_RES_LOSSLESS, HIGH, LOW
        CountryCode = "IT";
        UseDirectTidalApi = true;
        CustomToken = string.Empty;
        EnableChannel = true;
        EnableStreamProxy = false;
        StrmLibraryPath = string.Empty;
        SearchLimit = 25;
    }

    /// <summary>
    /// Gets or sets the Monochrome / HiFi API base URL.
    /// </summary>
    public string ApiBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the preferred audio quality.
    /// Options: HI_RES_LOSSLESS, LOSSLESS, HIGH, LOW.
    /// </summary>
    public string AudioQuality { get; set; }

    /// <summary>
    /// Gets or sets the country code for catalogue availability (ISO 3166-1 alpha-2, e.g. IT, US, GB).
    /// </summary>
    public string CountryCode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use direct TIDAL API authentication.
    /// </summary>
    public bool UseDirectTidalApi { get; set; }

    /// <summary>
    /// Gets or sets an optional custom OAuth/API token or refresh token.
    /// </summary>
    public string CustomToken { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Monochrome Channel is enabled in Jellyfin.
    /// </summary>
    public bool EnableChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to proxy audio streams through Jellyfin or redirect directly (HTTP 302).
    /// </summary>
    public bool EnableStreamProxy { get; set; }

    /// <summary>
    /// Gets or sets the default path on the server filesystem where .strm files are generated.
    /// </summary>
    public string StrmLibraryPath { get; set; }

    /// <summary>
    /// Gets or sets the search limit.
    /// </summary>
    public int SearchLimit { get; set; }
}
