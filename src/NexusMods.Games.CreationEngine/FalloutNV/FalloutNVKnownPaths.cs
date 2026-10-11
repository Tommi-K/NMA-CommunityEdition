
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.FalloutNV;

public static class FalloutNVKnownPaths
{
    /// <remarks>
    /// The launcher writes <c>plugins.txt</c> but the game reads <c>Plugins.txt</c>; on a
    /// case sensitive filesystem - which is where this app runs the game through Proton -
    /// only the capitalised name is picked up.
    /// </remarks>
    public static readonly GamePath PluginsFile = new(LocationId.AppData, "Plugins.txt");

    /// <summary>
    /// Epic ships the Epic Online Services SDK next to the executable and nothing else
    /// does, which is how the other mod managers tell that build apart. It matters because
    /// the Epic build keeps its settings and plugin list under <c>FalloutNV_Epic</c>.
    /// </summary>
    public static readonly RelativePath EpicMarkerFile = "EOSSDK-Win32-Shipping.dll";

    public const string AppDataFolderName = "FalloutNV";
    public const string EpicAppDataFolderName = "FalloutNV_Epic";

    /// <summary>
    /// Folders that belong directly under <c>Data</c>, so that an archive which starts at
    /// one of them is recognised as a loose-file mod rather than something that needs a
    /// <c>Data</c> wrapper. The shared Creation Engine list plus the ones specific to New
    /// Vegas and its script extender.
    /// </summary>
    public static readonly string[] TopLevelFolders =
    [
        ..KnownPaths.CommonTopLevelFolders,
        "config",
        "distantland",
        "dllplugins",
        "materials",
        "mcm",
        "mits",
        "nvse",
        "shadersfx",
    ];
}
