using System.Security.Cryptography;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using PhoenicxTorBox.Configuration;

namespace PhoenicxTorBox;

/// <summary>
/// Phoenicx TorBox: TorBox movies and series as Jellyfin libraries, streamed straight from TorBox.
/// Made by Hanson — https://phoenicx.org
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin id, also in build.yaml.</summary>
    public static readonly Guid PluginId = Guid.Parse("d793e3f9-364f-4640-9ebb-7c807bfcb90e");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataPath = applicationPaths.DataPath;

        if (string.IsNullOrWhiteSpace(Configuration.SigningSecret))
        {
            Configuration.SigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
        }
    }

    /// <summary>Gets the running plugin.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets Jellyfin's data folder.</summary>
    public string DataPath { get; }

    /// <inheritdoc />
    public override string Name => "Phoenicx TorBox";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Your TorBox movies and series as Jellyfin libraries, played straight from TorBox. By Hanson — phoenicx.org";

    /// <summary>Gets the folder the .strm files go to.</summary>
    public string LibraryRoot =>
        string.IsNullOrWhiteSpace(Configuration.LibraryPath)
            ? Path.Combine(DataPath, "phoenicx-torbox")
            : Configuration.LibraryPath.Trim();

    /// <summary>Gets the movies folder to add as a Movies library.</summary>
    public string MoviesPath => Path.Combine(LibraryRoot, "Movies");

    /// <summary>Gets the shows folder to add as a Shows library.</summary>
    public string ShowsPath => Path.Combine(LibraryRoot, "Shows");

    /// <summary>Gets the folder holding one folder per TorBox tag.</summary>
    public string TagsPath => Path.Combine(LibraryRoot, "Tags");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "PhoenicxTorBox",
            EnableInMainMenu = true,
            MenuIcon = "cloud",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }
}
