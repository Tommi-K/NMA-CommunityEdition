using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.DataModel;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.FileExtractor;
using NexusMods.Sdk.Settings;

namespace NexusMods.App;

/// <summary>
/// Keeps temporary files on the same drive as the mod archives.
/// </summary>
/// <remarks>
/// A download lands in a temporary file before it is archived, so a temporary folder on
/// the default drive fills that drive up during a collection install even though the user
/// pointed their storage location somewhere else. Keeping the two together also turns
/// archiving into a rename instead of a copy between file systems.
/// <br/><br/>
/// The folder is deliberately a sibling of the archive folder rather than a folder inside
/// it: <see cref="NxFileStore"/> sweeps `.tmp` files out of the archive locations when it
/// starts, and a download in progress looks exactly like one of those.
/// </remarks>
internal static class TempFolderLocation
{
    private const string FolderName = "NMAcommunity.App.Temp";

    /// <summary>
    /// Points <see cref="FileExtractorSettings.TempFolderLocation"/> at a folder next to
    /// the configured archive location, leaving it alone if that isn't usable.
    /// </summary>
    /// <remarks>
    /// Has to run before anything resolves <see cref="NexusMods.Paths.TemporaryFileManager"/>,
    /// which reads the setting once when it is constructed.
    /// </remarks>
    public static void FollowStorageLocation(IServiceProvider services, ILogger logger)
    {
        try
        {
            var settingsManager = services.GetRequiredService<ISettingsManager>();
            var fileSystem = services.GetRequiredService<IFileSystem>();

            // NOTE(CE): the CLI process runs on a smaller set of settings that doesn't
            // include the data model, and it forwards its work to the main process anyway
            if (!settingsManager.Configs.ContainsKey(typeof(DataModelSettings))) return;

            var archiveLocations = settingsManager.Get<DataModelSettings>().ArchiveLocations;
            if (archiveLocations.Length == 0) return;

            var archiveLocation = archiveLocations[0].ToPath(fileSystem);
            var parent = archiveLocation.Parent;
            if (parent == archiveLocation) return;

            var tempFolder = parent.Combine(FolderName);
            if (!TryCreate(tempFolder))
            {
                var current = settingsManager.Get<FileExtractorSettings>().TempFolderLocation.ToPath(fileSystem);
                logger.LogWarning(
                    "Can't use `{Path}` for temporary files, they stay at `{Fallback}`. Downloads will take up space on that drive while they run",
                    tempFolder, current
                );

                return;
            }

            settingsManager.Update<FileExtractorSettings>(current => current with
            {
                TempFolderLocation = new ConfigurablePath(baseDirectory: null, tempFolder.ToString()),
            });

            logger.LogInformation("Temporary files are stored at `{Path}`, next to the mod archives", tempFolder);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to put temporary files next to the mod archives, using the default location");
        }
    }

    private static bool TryCreate(AbsolutePath path)
    {
        try
        {
            if (!path.DirectoryExists()) path.CreateDirectory();
            return path.DirectoryExists();
        }
        catch (Exception)
        {
            // an unmounted or read only drive, the caller falls back to the default
            return false;
        }
    }
}
