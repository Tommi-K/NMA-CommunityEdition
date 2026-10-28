using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.Networking.NexusWebApi.Errors;
using NexusMods.Networking.NexusWebApi.Requirements;
using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;
using Xunit.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Tests;

/// <summary>
/// A requirement whose only copy is in a switched-off collection: the finder spotting it, and the
/// copy installed to stand in for it following that collection on and off.
/// </summary>
public class RequirementStandInTests : ACyberpunkIsolatedGameTest<RequirementStandInTests>
{
    private readonly IGraphQlClient _graphQlClient = Substitute.For<IGraphQlClient>();

    public RequirementStandInTests(ITestOutputHelper helper) : base(helper) { }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // The requirement lists come from here instead of the site.
        return base.AddServices(services).AddSingleton(_graphQlClient);
    }

    [Theory]
    [InlineData(true, false, false, StandInAction.None)]
    [InlineData(true, false, true, StandInAction.StandDown)]
    [InlineData(false, true, true, StandInAction.None)]
    [InlineData(false, true, false, StandInAction.ComeBack)]
    [InlineData(false, false, true, StandInAction.None)]
    [InlineData(false, false, false, StandInAction.None)]
    [InlineData(true, true, true, StandInAction.StopTracking)]
    [InlineData(true, true, false, StandInAction.StopTracking)]
    public void Decide_FollowsTheOtherCopy_AndLeavesTheUsersChoiceAlone(bool isOn, bool isStoodDown, bool otherCopyIsOn, StandInAction expected)
    {
        RequirementStandIns.Decide(isOn, isStoodDown, otherCopyIsOn).Should().Be(expected);
    }

    [Fact]
    public async Task StandIn_FollowsItsCollection_UntilTheUserTakesOver()
    {
        var (loadout, collection, original, root) = await SetUpRequirementInSwitchedOffCollection();
        var finder = ServiceProvider.GetRequiredService<IRequirementFinder>();
        var standIns = ServiceProvider.GetRequiredService<IRequirementStandIns>();

        // Only a switched-off collection has it, so a copy is what's offered.
        var found = (await finder.Find(loadout, [root])).Requirements.Should().ContainSingle().Subject;
        found.State.Should().Be(RequirementState.InDisabledCollection);
        found.InstalledCopy.Should().Be(original);
        found.CollectionName.Should().Be("Pack");
        found.IsFixable.Should().BeTrue();

        var standIn = await InstallStandIn(loadout, original);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((true, false, true));
        (await finder.Find(loadout, [root])).Requirements.Should().BeEmpty("the stand-in provides it");

        // The collection back on: the copy steps aside, and the requirement is still met.
        await SwitchOn(collection);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((false, true, true));
        (await finder.Find(loadout, [root])).Requirements.Should().BeEmpty("the collection's copy provides it");

        // And off again: the copy comes back.
        await SwitchOff(collection);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((true, false, true));

        // Switched off by hand, it stays off whatever the collection does.
        await SwitchOff(standIn);
        await SwitchOn(collection);
        await standIns.Reconcile();
        await SwitchOff(collection);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((false, false, true));

        // So the finder offers to switch it back on, rather than a third copy.
        found = (await finder.Find(loadout, [root])).Requirements.Should().ContainSingle().Subject;
        found.State.Should().Be(RequirementState.SwitchedOff);
        found.InstalledCopy.Should().Be(standIn);

        // Switched back on by hand next to the collection's copy, it is no longer managed.
        await SwitchOn(standIn);
        await SwitchOn(collection);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((false, true, true));

        await SwitchOn(standIn);
        await standIns.Reconcile();
        StateOf(standIn).Should().Be((true, false, false));
    }

    [Fact]
    public async Task StandIn_SwitchesByItself_AndComesBackWhenTheCollectionIsRemoved()
    {
        var (loadout, collection, original, _) = await SetUpRequirementInSwitchedOffCollection();
        var standIn = await InstallStandIn(loadout, original);

        // Nothing calls for it here: switching the collection on is enough.
        await SwitchOn(collection);
        await WaitFor(() => StateOf(standIn) == (false, true, true));

        await LoadoutManager.RemoveCollection(CollectionGroupId.From(collection.Value));
        await WaitFor(() => StateOf(standIn) == (true, false, true));
    }

    /// <summary>
    /// A loadout whose collection "Pack" holds a mod and is switched off, and a mod on the site
    /// that requires that mod.
    /// </summary>
    private async Task<(LoadoutId Loadout, LoadoutItemGroupId Collection, LoadoutItemGroupId Original, ModUid Root)> SetUpRequirementInSwitchedOffCollection()
    {
        var loadout = await CreateLoadout();
        var archive = await RegisterLocalArchive(CreateModArchive("Shared Framework"));

        var collection = await CreateCollection(loadout, "Pack");
        var collectionGroup = collection.AsLoadoutItemGroup().LoadoutItemGroupId;

        var installed = await LoadoutManager.InstallItem(archive.AsLibraryFile().AsLibraryItem(), loadout, parent: collectionGroup);
        var original = installed.LoadoutItemGroup!.Value;
        await DisableItem(collection.Id);

        // A mod added from a file is matched to a requirement by its name.
        var gameId = Cyberpunk2077Game.NexusModsGameId.Value;
        var root = new ModUid(ModId.From(1), gameId);
        var requirement = new ModUid(ModId.From(2), gameId);
        var requirementName = LoadoutItem.Load(Connection.Db, original.Id).Name;

        _graphQlClient
            .QueryModRequirements(Arg.Any<IReadOnlyList<ModUid>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var lists = call.Arg<IReadOnlyList<ModUid>>()
                    .Where(uid => uid == root)
                    .Select(uid => new ModRequirementList(uid, "Root Mod", "published", [new ModRequirementEntry(requirement, requirementName, IsExternal: false, Url: null, Notes: null)]))
                    .ToArray();

                return new ValueTask<GraphQlResult<ModRequirementList[]>>(new GraphQlResult<ModRequirementList[]>(lists));
            });

        return (loadout, collectionGroup, original.LoadoutItemGroupId, root);
    }

    /// <summary>
    /// What the install-requirements button does for a requirement only in a switched-off
    /// collection: a copy of the same library file, marked as a stand-in.
    /// </summary>
    private async Task<LoadoutItemGroupId> InstallStandIn(LoadoutId loadout, LoadoutItemGroupId original)
    {
        var libraryItem = LibraryLinkedLoadoutItem.Load(Connection.Db, original).LibraryItem;
        var copy = (await LoadoutManager.InstallItem(libraryItem, loadout)).LoadoutItemGroup!.Value;

        using var tx = Connection.BeginTransaction();
        tx.Add(copy.Id, RequirementStandIn.StandIn, Null.Instance);
        await tx.Commit();

        return copy.LoadoutItemGroupId;
    }

    private (bool IsOn, bool IsStoodDown, bool IsStandIn) StateOf(LoadoutItemGroupId id)
    {
        var db = Connection.Db;
        var standIn = RequirementStandIn.Load(db, id);
        return (!LoadoutItem.Load(db, id).IsDisabled, standIn.IsStoodDown, standIn.IsValid());
    }

    private Task SwitchOn(LoadoutItemGroupId id) => EnableItem(id.Value).AsTask();

    private Task SwitchOff(LoadoutItemGroupId id) => DisableItem(id.Value).AsTask();

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The stand-in didn't follow its collection in time");
            await Task.Delay(50);
        }
    }

    private AbsolutePath CreateModArchive(string name)
    {
        var directory = FileSystem.GetKnownPath(KnownPath.EntryDirectory) / $"RequirementStandInTests-{Guid.NewGuid()}";
        directory.CreateDirectory();

        var path = directory / $"{name}.zip";
        using (var zip = ZipFile.Open(path.ToString(), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"bin/x64/{name}.txt").Open());
            writer.Write(name);
        }

        return path;
    }
}
