using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Pages.LoadoutPage.Dialogs.InstallRequirements;
using NexusMods.App.UI.Windows;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Networking.NexusWebApi.Requirements;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Dialog;
using NexusMods.UI.Sdk.Dialog.Enums;

namespace NexusMods.App.UI.Pages.LoadoutPage;

/// <summary>
/// Finds what some mods of a loadout require from Nexus Mods and lacks, and installs the ones the
/// user picks.
/// </summary>
/// <remarks>
/// The downloads go through <see cref="ModListDownloader"/>, the same as a mod list's missing
/// mods: the API for a premium account, the mod's download page in the app's browser otherwise.
/// </remarks>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class RequirementInstaller
{
    private static readonly ButtonDefinitionId InstallButtonId = ButtonDefinitionId.From("install-requirements");

    // One run at a time, across every page. A second one started while the first is still
    // downloading would find the same requirements missing and install them twice.
    private readonly SemaphoreSlim _runGate = new(initialCount: 1, maxCount: 1);

    private readonly ILogger<RequirementInstaller> _logger;
    private readonly IConnection _connection;
    private readonly IRequirementFinder _requirementFinder;
    private readonly ModListDownloader _modListDownloader;
    private readonly ILoadoutManager _loadoutManager;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;
    private readonly IWindowNotificationService _notificationService;
    private readonly IOSInterop _osInterop;

    public RequirementInstaller(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<RequirementInstaller>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _requirementFinder = serviceProvider.GetRequiredService<IRequirementFinder>();
        _modListDownloader = serviceProvider.GetRequiredService<ModListDownloader>();
        _loadoutManager = serviceProvider.GetRequiredService<ILoadoutManager>();
        _mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        _osInterop = serviceProvider.GetRequiredService<IOSInterop>();
    }

    /// <summary>
    /// Looks up what the mods <paramref name="ids"/> stand for require, asks the user which of the
    /// missing or switched-off ones to see to, and sees to them.
    /// </summary>
    /// <param name="windowManager">The window to ask in.</param>
    /// <param name="loadoutId">The loadout the mods are in.</param>
    /// <param name="ids">The mods, as the rows of a loadout page hold them.</param>
    /// <param name="parent">The collection to install into, or none for the loadout's default.</param>
    /// <param name="cancellationToken">Stops the run; whatever was installed stays installed.</param>
    public async Task Run(
        IWindowManager windowManager,
        LoadoutId loadoutId,
        IReadOnlyCollection<LoadoutItemId> ids,
        Optional<LoadoutItemGroupId> parent,
        CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            _notificationService.ShowToast("Requirements are still being installed. Try again once that's done.");
            return;
        }

        try
        {
            await RunExclusive(windowManager, loadoutId, ids, parent, cancellationToken);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task RunExclusive(
        IWindowManager windowManager,
        LoadoutId loadoutId,
        IReadOnlyCollection<LoadoutItemId> ids,
        Optional<LoadoutItemGroupId> parent,
        CancellationToken cancellationToken)
    {
        var mods = GetModPages(ids);
        if (mods.Length == 0)
        {
            _notificationService.ShowToast("None of these mods came from Nexus Mods, so there is no list of requirements to read.");
            return;
        }

        var subject = mods.Length == 1 ? $"“{mods[0].Name}”" : $"The {mods.Length} selected mods";
        _notificationService.ShowToast($"Checking what {(mods.Length == 1 ? subject : $"the {mods.Length} selected mods")} {(mods.Length == 1 ? "requires" : "require")} on Nexus Mods…");

        RequirementFinderResult result;
        try
        {
            result = await _requirementFinder.Find(loadoutId, mods.Select(static mod => mod.Uid).ToArray(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to look up the requirements of {Count} mods in loadout `{Loadout}`", mods.Length, loadoutId);
            _notificationService.ShowToast($"Couldn't read the requirements from Nexus Mods: {exception.Message}", ToastNotificationVariant.Failure);
            return;
        }

        if (result.Requirements.Length == 0)
        {
            var message = result.FailedRequests > 0
                ? $"Couldn't read the requirements from Nexus Mods: {result.FailedRequests} requests failed."
                : $"Everything {(mods.Length == 1 ? subject : "these mods")} {(mods.Length == 1 ? "requires" : "require")} from Nexus Mods is installed.";

            _notificationService.ShowToast(message, result.FailedRequests > 0 ? ToastNotificationVariant.Failure : ToastNotificationVariant.Success);
            return;
        }

        var target = ResolveTarget(loadoutId, parent);
        var viewModel = new DialogInstallRequirementsViewModel(result, subject, target.Name, target.IsOn, uri => _osInterop.OpenUri(uri));
        var hasFixable = viewModel.ToInstall.Count > 0;

        // When all there is to do is switch mods back on, "install" would promise a download.
        var installLabel = viewModel.ToInstall.All(static item => item.Requirement.State == RequirementState.SwitchedOff)
            ? "Switch ticked mods on"
            : "Install ticked mods";

        DialogButtonDefinition[] buttons = hasFixable
            ?
            [
                DialogStandardButtons.Cancel,
                new DialogButtonDefinition(installLabel, InstallButtonId, ButtonAction.Accept, ButtonStyling.Primary),
            ]
            :
            [
                new DialogButtonDefinition("Close", ButtonDefinitionId.Close, ButtonAction.Reject),
            ];

        var title = mods.Length == 1 ? $"Requirements of {mods[0].Name}" : $"Requirements of {mods.Length} mods";
        var answer = await windowManager.ShowDialog(DialogFactory.CreateDialog(title, buttons, viewModel), DialogWindowType.Modal);
        if (answer.ButtonId != InstallButtonId) return;

        var selected = viewModel.GetSelected();
        if (selected.Length == 0) return;

        await Install(windowManager, loadoutId, selected, target.Id, cancellationToken);
    }

    /// <summary>
    /// Where the new mods go: <paramref name="parent"/> when it is on, otherwise the first of the
    /// loadout's own collections that is on.
    /// </summary>
    /// <remarks>
    /// Left to itself, an install goes into the first of the user's collections whether it is on or
    /// not. A requirement installed into a collection that is switched off does nothing, and a copy
    /// standing in for one in a switched-off collection would land right next to it. When every
    /// collection is off there is no better place, and the dialog says so.
    /// </remarks>
    private (Optional<LoadoutItemGroupId> Id, string? Name, bool IsOn) ResolveTarget(LoadoutId loadoutId, Optional<LoadoutItemGroupId> parent)
    {
        var db = _connection.Db;
        var candidates = new List<LoadoutItemGroupId>();
        if (parent.HasValue) candidates.Add(parent.Value);
        candidates.AddRange(Loadout.Load(db, loadoutId).MutableCollections().Select(static collection => LoadoutItemGroupId.From(collection.CollectionId)));

        (Optional<LoadoutItemGroupId> Id, string? Name, bool IsOn)? first = null;
        foreach (var id in candidates)
        {
            var item = LoadoutItem.Load(db, id);
            if (!item.IsValid()) continue;

            if (item.IsEnabled()) return (id, item.Name, true);
            first ??= (id, item.Name, false);
        }

        return first ?? (parent, null, true);
    }

    private async Task Install(
        IWindowManager windowManager,
        LoadoutId loadoutId,
        FoundRequirement[] requirements,
        Optional<LoadoutItemGroupId> parent,
        CancellationToken cancellationToken)
    {
        var failed = new List<string>();

        // One the user switched off goes back on where it is, keeping its place in the order.
        var switchedOn = await SwitchBackOn(requirements, failed);

        // One only in switched-off collections gets a copy from the library, which
        // IRequirementStandIns keeps from doubling up with the original.
        var standIns = await InstallStandIns(loadoutId, requirements, parent, failed, cancellationToken);

        var downloaded = 0;
        var missing = requirements.Where(static requirement => requirement.State == RequirementState.Missing).ToArray();
        if (missing.Length > 0)
        {
            _notificationService.ShowToast($"Downloading {Plural(missing.Length, "requirement")}…");

            // Highest priority first, the way a mod list has them: the reverse of the order to install in.
            var mods = new List<ModListMissingMod>(capacity: missing.Length);
            foreach (var requirement in missing.Reverse())
            {
                if (requirement.Uid is not { } uid) continue;

                mods.Add(new ModListMissingMod(
                    requirement.Name,
                    IsEnabled: true,
                    new ModListNexusSource(_mappingCache[uid.GameId], uid.ModId, FileId: null)
                ));
            }

            var downloads = await _modListDownloader.DownloadAndInstall(loadoutId, mods, parent, cancellationToken);
            downloaded = downloads.Installed;
            failed.AddRange(downloads.Failed);
        }

        try
        {
            await MoveBelowOtherMods(loadoutId, requirements, standIns);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The mods are installed either way; only their place in the conflict order is off.
            _logger.LogError(exception, "Failed to move the installed requirements below the other mods of loadout `{Loadout}`", loadoutId);
        }

        var installed = downloaded + standIns.Count;
        _logger.LogInformation("Saw to {Count} requirements of loadout `{Loadout}`: downloaded {Downloaded}, copied {Copied} out of switched-off collections, switched {SwitchedOn} back on, {Failed} failed",
            requirements.Length, loadoutId, downloaded, standIns.Count, switchedOn, failed.Count);

        if (failed.Count == 0)
        {
            _notificationService.ShowToast(LoadoutDialogs.DescribeRequirementsInstalled(installed, switchedOn), ToastNotificationVariant.Success);
            return;
        }

        await windowManager.ShowDialog(LoadoutDialogs.RequirementsInstalled(installed, switchedOn, failed), DialogWindowType.Modal);
    }

    /// <summary>
    /// Switches back on the requirements the user switched off.
    /// </summary>
    /// <returns>How many were switched on.</returns>
    private async ValueTask<int> SwitchBackOn(FoundRequirement[] requirements, List<string> failed)
    {
        var db = _connection.Db;
        using var tx = _connection.BeginTransaction();
        var count = 0;

        foreach (var requirement in requirements)
        {
            if (requirement.State != RequirementState.SwitchedOff || requirement.InstalledCopy is not { } group) continue;

            var item = LoadoutItem.Load(db, group);
            if (!item.IsValid())
            {
                failed.Add($"{requirement.Name}: it is no longer in the loadout");
                continue;
            }

            if (!item.IsDisabled) continue;

            tx.Retract(group.Value, LoadoutItem.Disabled, Null.Instance);

            // A stand-in the app had switched off goes back to being managed as one: left marked, it
            // would read as the user switching it on by hand.
            if (RequirementStandIn.Load(db, group).IsStoodDown) tx.Retract(group.Value, RequirementStandIn.StoodDown, Null.Instance);

            count++;
        }

        if (count > 0) await tx.Commit();
        return count;
    }

    /// <summary>
    /// Installs a copy of each requirement that is only in switched-off collections, from the library
    /// file the collection installed it from, and marks it as a stand-in.
    /// </summary>
    /// <returns>The copies, by the index of their requirement in <paramref name="requirements"/>.</returns>
    private async ValueTask<Dictionary<int, LoadoutItemGroupId>> InstallStandIns(
        LoadoutId loadoutId,
        FoundRequirement[] requirements,
        Optional<LoadoutItemGroupId> parent,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        var standIns = new Dictionary<int, LoadoutItemGroupId>();

        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            if (requirement.State != RequirementState.InDisabledCollection || requirement.InstalledCopy is not { } original) continue;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!LibraryLinkedLoadoutItem.TryGet(_connection.Db, original.Value, out var linked))
                {
                    failed.Add($"{requirement.Name}: it is no longer in the loadout");
                    continue;
                }

                // Awaited one at a time: an installer with options asks the user, and two of those
                // at once would be one too many.
                var result = await _loadoutManager.InstallItem(linked.Value.LibraryItem, loadoutId, parent);
                if (result.LoadoutItemGroup is not { } group)
                {
                    failed.Add($"{requirement.Name}: couldn't be installed from your library");
                    continue;
                }

                using var tx = _connection.BeginTransaction();
                tx.Add(group.Id, RequirementStandIn.StandIn, Null.Instance);
                await tx.Commit();

                standIns[index] = group.LoadoutItemGroupId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Failed to install a copy of `{Name}` from the library", requirement.Name);
                failed.Add($"{requirement.Name}: couldn't be installed from your library ({exception.Message})");
            }
        }

        return standIns;
    }

    /// <summary>
    /// Puts the newly installed requirements at the bottom of the conflict order, each below the ones
    /// that need it, so that every other mod - the ones that required them included - goes on
    /// winning the file conflicts it wins now.
    /// </summary>
    /// <remarks>
    /// A new mod goes in at the top. That suits a mod the user picked, but a requirement is a base
    /// to build on: a framework whose files the mods using it replace, as with a load order's masters.
    /// One switched back on keeps the place the user gave it.
    /// </remarks>
    private async ValueTask MoveBelowOtherMods(LoadoutId loadoutId, FoundRequirement[] requirements, Dictionary<int, LoadoutItemGroupId> standIns)
    {
        var db = _connection.Db;
        var placed = standIns.Select(static pair => (Index: pair.Key, Group: pair.Value)).ToList();

        // A downloaded one had no copy in the loadout before, so every copy of its page is new.
        var downloadedPages = new Dictionary<ModUid, int>();
        for (var index = 0; index < requirements.Length; index++)
        {
            if (requirements[index] is { State: RequirementState.Missing, Uid: { } uid }) downloadedPages.TryAdd(uid, index);
        }

        if (downloadedPages.Count > 0)
        {
            foreach (var linked in LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemGroup().OfTypeLibraryLinkedLoadoutItem())
            {
                if (!linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var item)) continue;
                if (!downloadedPages.TryGetValue(item.ModPageMetadata.Uid, out var index)) continue;

                placed.Add((index, linked.AsLoadoutItemGroup().LoadoutItemGroupId));
            }
        }

        var groups = placed
            .OrderBy(static pair => pair.Index)
            .Select(static pair => pair.Group)
            .Distinct()
            .ToArray();

        if (groups.Length == 0) return;

        var groupSet = groups.ToHashSet();
        var priorities = LoadoutItemGroupPriority.FindByLoadout(db, loadoutId)
            .Where(priority => groupSet.Contains(priority.TargetId))
            .Select(static priority => priority.LoadoutItemGroupPriorityId)
            .ToArray();

        if (priorities.Length == 0) return;

        // Down to the bottom together, keeping the order they arrived in, then dealt out between
        // those bottom places in the order to install them.
        await _loadoutManager.LoseAllFileConflicts(priorities);
        await _loadoutManager.ReorderMods(loadoutId, groups);
    }

    /// <summary>
    /// The Nexus Mods pages that <paramref name="ids"/> were installed from.
    /// </summary>
    private ModReference[] GetModPages(IReadOnlyCollection<LoadoutItemId> ids)
    {
        var db = _connection.Db;
        var pages = new Dictionary<ModUid, ModReference>();

        foreach (var id in ids)
        {
            if (!LibraryLinkedLoadoutItem.TryGet(db, id.Value, out var linkedItem)) continue;
            if (!NexusModsLibraryItem.TryGet(db, linkedItem.Value.LibraryItemId.Value, out var nexusModsLibraryItem)) continue;

            var page = nexusModsLibraryItem.Value.ModPageMetadata;
            pages.TryAdd(page.Uid, new ModReference(page.Uid, page.Name));
        }

        return pages.Values.ToArray();
    }

    private static string Plural(int count, string noun) => $"{count} {(count == 1 ? noun : noun + "s")}";
}
