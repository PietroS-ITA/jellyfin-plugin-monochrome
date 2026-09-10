using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Monochrome.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Monochrome;

/// <summary>
/// The main plugin class for Monochrome Music.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Monochrome Music";

    /// <inheritdoc />
    public override string Description => "Stream and browse lossless Hi-Res music from Monochrome and TIDAL in Jellyfin 12.0.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("0a9d15e2-6bf3-4674-8b1b-7a329d7831d1");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "MonochromeSearch",
                DisplayName = "Monochrome Music",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.searchPage.html",
                    GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "library",
                MenuIcon = "search"
            },
            new PluginPageInfo
            {
                Name = "Monochrome",
                DisplayName = "Monochrome Settings",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
                EnableInMainMenu = false,
                MenuIcon = "settings"
            }
        };
    }
}

