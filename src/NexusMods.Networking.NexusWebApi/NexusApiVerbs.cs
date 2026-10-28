using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.Patches;
using NexusMods.Networking.NexusWebApi.Requirements;
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
            .AddVerb(() => NexusFindPatches)
            .AddVerb(() => NexusFindRequirements);


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

    [Verb("nexus find-requirements", "Lists what the loadout's mods require from their \"Requirements\" lists on Nexus Mods and is missing or switched off")]
    private static async Task<int> NexusFindRequirements([Injected] IRenderer renderer,
        [Option("l", "loadout", "Loadout to check")] Loadout.ReadOnly loadout,
        [Option("m", "mod", "Nexus Mods id of one installed mod to check, instead of all of them", true)] string? modId,
        [Injected] IRequirementFinder requirementFinder,
        [Injected] CancellationToken token)
    {
        ModUid[] mods;
        if (modId is not null)
        {
            if (!uint.TryParse(modId, out var parsed)) return await renderer.InputError("{0} is not a mod id", modId);
            mods = [new ModUid(ModId.From(parsed), loadout.Installation.GameId)];
        }
        else
        {
            mods = LoadoutItem.FindByLoadout(loadout.Db, loadout.LoadoutId)
                .OfTypeLoadoutItemGroup()
                .OfTypeLibraryLinkedLoadoutItem()
                .Select(static linked => linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var item) ? item.ModPageMetadata.Uid : (ModUid?)null)
                .OfType<ModUid>()
                .Distinct()
                .ToArray();
        }

        var result = await requirementFinder.Find(loadout.LoadoutId, mods, token);

        await renderer.TextLine("Checked {0} mods, {1} requirements already installed, {2} failed requests{3}",
            result.ModsChecked.Length, result.AlreadyInstalled, result.FailedRequests, result.Truncated ? ", stopped early" : string.Empty);

        // A line each, like find-patches: the names are long.
        foreach (var requirement in result.Requirements)
        {
            var notes = requirement.RequiredBy.Select(static link => link.Notes).OfType<string>().Distinct().ToArray();

            await renderer.TextLine("{0}\t{1}\t{2}\t{3}\t{4}",
                requirement.State + (requirement.IsOptional ? " (optional)" : string.Empty),
                requirement.Name,
                "for " + string.Join(", ", requirement.RequiredBy.Select(static link => link.Mod.Name + (link.IsOptional ? "?" : string.Empty))),
                requirement.Reason ??
                (requirement.CollectionName is not null ? $"in \"{requirement.CollectionName}\" ({requirement.FileName})" : null) ??
                (notes.Length > 0 ? string.Join(" / ", notes) : "-"),
                requirement.Uri?.GetLeftPart(UriPartial.Path) ?? "-");
        }

        return 0;
    }
}
