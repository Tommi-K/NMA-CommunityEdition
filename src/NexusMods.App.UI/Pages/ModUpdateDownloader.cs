using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.Telemetry;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Library;

namespace NexusMods.App.UI.Pages;

/// <summary>
/// Downloads the newest file of a mod into the library.
/// </summary>
/// <remarks>
/// Shared by everywhere that offers "update this mod": the library's update commands and the
/// collection download page. The two accounts take different routes to the same place -- a
/// premium account is handed the file by the API, and everyone else has the mod's download page
/// driven for them in the app's own browser tab -- and neither of them belongs in a view model.
/// <para/>
/// Downloading only ever adds to the library. What is already installed is left alone, so this
/// is safe to offer for a mod that came from a read-only collection: the newer file sits in the
/// library until the user installs it themselves.
/// </remarks>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class ModUpdateDownloader
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoginManager _loginManager;
    private readonly NexusModsLibrary _nexusModsLibrary;
    private readonly ILibraryService _libraryService;
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IOSInterop _osInterop;

    public ModUpdateDownloader(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        _nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        _libraryService = serviceProvider.GetRequiredService<ILibraryService>();
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _osInterop = serviceProvider.GetRequiredService<IOSInterop>();
    }

    /// <summary>
    /// Downloads each of <paramref name="newestFiles"/> into the library.
    /// </summary>
    /// <param name="newestFiles">The files to fetch. Duplicates are downloaded once.</param>
    /// <param name="cancellationToken">Stops the run; whatever already downloaded is kept.</param>
    public async ValueTask DownloadAll(IEnumerable<NexusModsFileMetadata.ReadOnly> newestFiles, CancellationToken cancellationToken)
    {
        var files = newestFiles.DistinctBy(static file => file.Id).ToArray();
        if (files.Length == 0) return;

        if (_loginManager.IsPremium)
        {
            // Note(sewer): There's usually just 1 file in like 99% of the cases here
            //              so no need to optimize around file reuse and TemporaryFileManager.
            foreach (var newestFile in files)
            {
                await using var tempPath = _temporaryFileManager.CreateFile();
                var job = await _nexusModsLibrary.CreateDownloadJob(tempPath, newestFile, cancellationToken: cancellationToken);
                await _libraryService.AddDownload(job);
            }

            return;
        }

        var inAppBrowser = _serviceProvider.GetService<IInAppBrowser>();

        // One tab for the whole run, brought to the front once and then left where it is.
        using var downloadSession = inAppBrowser?.BeginDownloadSession();

        foreach (var newestFile in files)
        {
            var uri = NexusModsUrlBuilder.GetFileDownloadUri(
                newestFile.ModPage.GameDomain,
                newestFile.ModPage.Uid.ModId,
                newestFile.Uid.FileId,
                useNxmLink: true,
                campaign: NexusModsUrlBuilder.CampaignUpdates
            );

            if (inAppBrowser is not null)
            {
                // Awaited: the browser drives one download page at a time, so awaiting here is
                // what turns a list of updates into one download after another. A page that
                // hands nothing over leaves its tab up, where the user can press the button
                // themselves, and the run moves on.
                var outcome = await inAppBrowser.StartDownload(uri, newestFile.Name, cancellationToken);
                if (outcome is not InAppDownloadOutcome.Unavailable) continue;
            }

            // No tab to drive, so the page goes to the system browser as it always used to.
            _osInterop.OpenUri(uri);
        }
    }
}
