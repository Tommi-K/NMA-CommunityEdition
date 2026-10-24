using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.Patches;
using NexusMods.Paths;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Networking.NexusWebApi;

internal static class NexusApiVerbs
{
    internal static IServiceCollection AddNexusApiVerbs(this IServiceCollection collection) =>
        collection
            .AddModule("nexus", "Commands for interacting with the Nexus Mods API")
            .AddVerb(() => NexusApiVerify)
            .AddVerb(() => NexusDownloadLinks)
            .AddVerb(() => NexusFindPatches);


    [Verb("nexus verify", "Verifies the logged in account via the Nexus API")]
    private static async Task<int> NexusApiVerify([Injected] IRenderer renderer,
        [Injected] NexusApiClient nexusApiClient,
        [Injected] IAuthenticatingMessageFactory messageFactory,
        [Injected] CancellationToken token)
    {
        var userInfo = await messageFactory.Verify(nexusApiClient, token);
        
        await renderer.Table(["Name", "Premium"],
        [
            [
                userInfo?.Name ?? "<Not logged in>",
                    userInfo?.UserRole == UserRole.Premium,
            ],
        ]);

        return 0;
    }

    [Verb("nexus download-links", "Generates download links for a given file")]
    private static async Task<int> NexusDownloadLinks([Injected] IRenderer renderer,
        [Option("g", "gameDomain", "Game domain")] string gameDomain,
        [Option("m", "modId", "Mod ID")] ModId modId,
        [Option("f", "fileId", "File ID")] FileId fileId,
        [Injected] NexusApiClient nexusApiClient,
        [Injected] CancellationToken token)
    {
        var links = await nexusApiClient.DownloadLinksAsync(gameDomain, modId, fileId, token);

        await renderer.Table(["Source", "Link"],
            links.Data.Select(x => new object[] { x.ShortName, x.Uri }));
        return 0;
    }

    [Verb("nexus find-patches", "Lists patches and translations for the loadout's mods, from their \"mods requiring this file\" lists")]
    private static async Task<int> NexusFindPatches([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to look for patches for")] Loadout.ReadOnly loadout,
        [Option("m", "mod", "Nexus Mods id of one installed mod to look for patches for, instead of all of them", true)] string? modId,
        [Option("a", "all", "Also list patches that need mods the loadout doesn't have", true)] bool all,
        [Option("o", "output", "Write the list to this file as a Mod Organizer modlist.txt the loadout Import can download from, instead of printing it", true)] string? output,
        [Injected] IPatchFinder patchFinder,
        [Injected] IFileSystem fileSystem,
        [Injected] CancellationToken token)
    {
        ModUid? onlyFor = null;
        if (modId is not null)
        {
            if (!uint.TryParse(modId, out var parsed)) return await renderer.InputError("{0} is not a mod id", modId);
            onlyFor = new ModUid(ModId.From(parsed), loadout.Installation.GameId);
        }

        var result = await patchFinder.Find(loadout.LoadoutId, onlyFor, cancellationToken: token);

        await renderer.TextLine("Checked {0} mods from Nexus Mods, {1} patches and translations already installed, {2} failed requests",
            result.ModsChecked, result.AlreadyInstalled, result.FailedRequests);

        if (result.Truncated.Length > 0)
            await renderer.TextLine("Too many dependents to read in full: {0}", string.Join(", ", result.Truncated));

        if (output is not null)
        {
            var path = fileSystem.FromUnsanitizedFullPath(output);
            var report = PatchFinderModList.Write(result, new PatchFinderModListOptions
            {
                Title = $"{loadout.Name} ({loadout.Installation.Name})",
                IncludePatchesNeedingOtherMods = all,
            });

            await path.WriteAllTextAsync(report, token);
            await renderer.TextLine("Wrote {0}", path);
            return 0;
        }

        // A line each rather than a table: the names are long, and a table squeezed to the width
        // of a terminal is unreadable.
        foreach (var match in result.Matches.Where(match => all || match.Kind == DependentKind.Translation || match.HasAllRequirements))
        {
            await renderer.TextLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                match.Kind,
                match.Language?.Code ?? "-",
                match.Name,
                string.Join(", ", match.ForInstalledMods),
                match.HasAllRequirements ? "-" : "needs " + string.Join(", ", match.MissingRequirements),
                match.Uri);
        }

        return 0;
    }
}
