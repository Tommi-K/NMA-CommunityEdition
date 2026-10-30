using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.ModUpdates;
using NexusMods.Networking.ModUpdates.Mixins;
using NexusMods.Networking.NexusWebApi.Errors;
using NexusMods.Networking.NexusWebApi.Extensions;
using NexusMods.Sdk.NexusModsApi;
using NSubstitute;
using R3;
using Xunit.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Tests;

/// <summary>
/// How the first check after adding a collection reads its hundreds of mod pages.
/// </summary>
public class UpdateModPagesTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<UpdateModPagesTests>(helper)
{
    private static readonly NexusModsGameId GameId = NexusModsGameId.From(3333u);

    private readonly IGraphQlClient _graphQl = Substitute.For<IGraphQlClient>();
    private readonly HashSet<ModUid> _removedMods = [];
    private int _modsRequests;
    private int _filesRequests;

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(10, 1, 1)]
    [InlineData(11, 1, 2)]
    [InlineData(60, 1, 6)]
    [InlineData(61, 2, 7)]
    [InlineData(319, 6, 32)]
    public async Task Pages_ShareTheirRequests(int pageCount, int expectedModsRequests, int expectedFilesRequests)
    {
        AnswerRequests();
        var pages = await CreatePages(pageCount);

        await Update(undetermined: pages);

        _modsRequests.Should().Be(expectedModsRequests);
        _filesRequests.Should().Be(expectedFilesRequests);
        await _graphQl.DidNotReceiveWithAnyArgs().QueryMod(default, default, default);
        await _graphQl.DidNotReceiveWithAnyArgs().QueryModFiles(default, default, default);
    }

    [Fact]
    public async Task EveryPage_GetsItsOwnFilesAndATimestamp()
    {
        AnswerRequests();
        var pages = await CreatePages(25);

        await Update(outOfDate: pages);

        foreach (var page in pages)
        {
            var updated = NexusModsModPageMetadata.Load(Connection.Db, page.Id);
            updated.Name.Should().Be($"Fresh name of {page.Uid.ModId}");
            HasTimestamp(updated).Should().BeTrue();

            updated.Files.Select(static file => file.Name).Should().Equal($"File of {page.Uid.ModId}");
        }
    }

    [Fact]
    public async Task ARemovedMod_IsLeftAlone_AndDoesntHoldUpTheOthers()
    {
        AnswerRequests();
        var pages = await CreatePages(15);
        var removed = pages[7];
        _removedMods.Add(removed.Uid);

        // Out of date is the strict case: a failure there fails the whole check.
        await Update(outOfDate: pages);

        var left = NexusModsModPageMetadata.Load(Connection.Db, removed.Id);
        left.Name.Should().Be(removed.Name);
        HasTimestamp(left).Should().BeFalse(because: "it should be looked at again next time");

        foreach (var page in pages.Where(page => page.Id != removed.Id))
        {
            HasTimestamp(NexusModsModPageMetadata.Load(Connection.Db, page.Id)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task AFailedBatchOfFiles_IsReadOneModAtATime()
    {
        AnswerRequests(filesInOneRequestFail: true);
        _graphQl.QueryModFiles(default, default, default).ReturnsForAnyArgs(call => new GraphQlResult<IModFile[], NotFound>([CreateFile(new ModUid(call.Arg<ModId>(), GameId))]));
        var pages = await CreatePages(12);

        await Update(undetermined: pages);

        await _graphQl.ReceivedWithAnyArgs(12).QueryModFiles(default, default, default);

        foreach (var page in pages)
        {
            NexusModsModPageMetadata.Load(Connection.Db, page.Id).Files.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task APageWhoseFilesCantBeRead_IsNotMarkedAsUpToDate()
    {
        AnswerRequests(filesInOneRequestFail: true);
        _graphQl.QueryModFiles(default, default, default).ReturnsForAnyArgs(new GraphQlResult<IModFile[], NotFound>(new NotFound { Message = "gone" }));
        var pages = await CreatePages(3);

        await Update(undetermined: pages);

        foreach (var page in pages)
        {
            HasTimestamp(NexusModsModPageMetadata.Load(Connection.Db, page.Id)).Should().BeFalse();
        }
    }

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task TheRealServer_AnswersTheBatchedQueries()
    {
        var graphQl = ServiceProvider.GetRequiredService<IGraphQlClient>();

        // More than the twenty the server stops at unless told how many are wanted.
        uint[] modIds = [6837, 7616, 15229, 20827, 24295, 20073, 19234, 906, 13657, 26222, 16092, 16236, 24853, 14730, 16272, 10556, 18209, 10886, 8340, 10038, 35285, 16921, 23129, 13260, 10393];
        var uids = modIds.Select(id => new ModUid(ModId.From(id), GameId)).ToArray();

        var mods = (await graphQl.QueryMods(uids, CancellationToken.None)).AssertHasData();

        mods.Should().HaveCountGreaterThan(20);
        mods.Select(static mod => ModUid.FromV2Api(mod.Uid)).Should().BeSubsetOf(uids);

        // Fewer than it has slots for, and one that doesn't exist, which mustn't sink the others.
        var missing = new ModUid(ModId.From(99999999u), GameId);
        var files = (await graphQl.QueryModFilesOfMods([uids[0], missing, uids[2]], CancellationToken.None)).AssertHasData();

        files.Should().HaveCount(3);
        files[0].Should().NotBeEmpty().And.OnlyContain(file => file.ModId == (int)modIds[0]);
        files[1].Should().BeEmpty();
        files[2].Should().NotBeEmpty().And.OnlyContain(file => file.ModId == (int)modIds[2]);
    }

    [Fact]
    public void FocusRefreshes_ComeAtOnce_ThenNoMoreOftenThanTheInterval()
    {
        var time = new FakeTimeProvider();
        var isActive = new Subject<bool>();
        var refreshes = new List<TimeSpan>();
        var start = time.GetUtcNow();

        using var subscription = LoginManager.ThrottleFocusRefreshes(isActive, time).Subscribe(_ => refreshes.Add(time.GetUtcNow() - start));

        void GainFocus()
        {
            isActive.OnNext(false);
            isActive.OnNext(true);
        }

        // Coming back after a while is seen right away: this is the one that has to be.
        GainFocus();
        refreshes.Should().Equal(TimeSpan.Zero);

        // A burst of focus changes doesn't add up to a burst of requests.
        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            GainFocus();
        }

        refreshes.Should().HaveCount(1);

        // The last of them still gets its refresh, once the interval is up. One second at a time,
        // because the fake clock jumps straight to the end of a longer step before it fires.
        while (refreshes.Count < 2 && time.GetUtcNow() - start < LoginManager.FocusRefreshInterval * 2)
        {
            time.Advance(TimeSpan.FromSeconds(1));
        }

        refreshes.Should().HaveCount(2);
        refreshes[1].Should().Be(LoginManager.FocusRefreshInterval);

        // Nothing more without another change of focus.
        time.Advance(LoginManager.FocusRefreshInterval * 3);
        refreshes.Should().HaveCount(2);

        GainFocus();
        refreshes.Should().HaveCount(3);
    }

    [Fact]
    public void FocusChangingNonStop_CantPutTheRefreshOffForever()
    {
        var time = new FakeTimeProvider();
        var isActive = new Subject<bool>();
        var refreshes = 0;

        using var subscription = LoginManager.ThrottleFocusRefreshes(isActive, time).Subscribe(_ => refreshes++);

        // Half an hour of the focus changing every few seconds, which is six intervals.
        for (var second = 0; second < 30 * 60; second += 5)
        {
            isActive.OnNext(false);
            isActive.OnNext(true);
            time.Advance(TimeSpan.FromSeconds(5));
        }

        refreshes.Should().BeInRange(6, 12);
    }

    [Fact]
    public void FocusLost_IsNoReasonToRefresh()
    {
        var time = new FakeTimeProvider();
        var isActive = new Subject<bool>();
        var refreshes = 0;

        using var subscription = LoginManager.ThrottleFocusRefreshes(isActive, time).Subscribe(_ => refreshes++);

        isActive.OnNext(false);
        time.Advance(LoginManager.FocusRefreshInterval * 2);

        refreshes.Should().Be(0);
    }

    private void AnswerRequests(bool filesInOneRequestFail = false)
    {
        _graphQl.QueryMods(default!, default).ReturnsForAnyArgs(call =>
        {
            Interlocked.Increment(ref _modsRequests);

            return new GraphQlResult<IMod[]>(call.Arg<IReadOnlyList<ModUid>>()
                .Where(uid => !_removedMods.Contains(uid))
                .Select(uid => CreateMod(uid))
                .ToArray());
        });

        if (filesInOneRequestFail)
        {
            _graphQl.QueryModFilesOfMods(default!, default).ReturnsForAnyArgs(new GraphQlResult<IModFile[][]>([new KeyValuePair<ErrorCode, IGraphQlError>(default, new TestError())]));
            return;
        }

        _graphQl.QueryModFilesOfMods(default!, default).ReturnsForAnyArgs(call =>
        {
            Interlocked.Increment(ref _filesRequests);

            var mods = call.Arg<IReadOnlyList<ModUid>>();
            mods.Count.Should().BeLessThanOrEqualTo(IGraphQlClient.MaxModsPerFilesRequest);

            return new GraphQlResult<IModFile[][]>(mods.Select(uid => new[] { CreateFile(uid) }).ToArray());
        });
    }

    private async Task Update(NexusModsModPageMetadata.ReadOnly[]? undetermined = null, NexusModsModPageMetadata.ReadOnly[]? outOfDate = null)
    {
        var result = new PerFeedCacheUpdaterResult<PageMetadataMixin>
        {
            UndeterminedItems = (undetermined ?? []).Select(static page => (PageMetadataMixin)page).ToList(),
            OutOfDateItems = (outOfDate ?? []).Select(static page => (PageMetadataMixin)page).ToList(),
        };

        using var tx = Connection.BeginTransaction();
        await RunUpdateCheck.UpdateModFilesForOutdatedPages(Connection.Db, tx, Logger, _graphQl, result, CancellationToken.None);
        await tx.Commit();
    }

    /// <summary>
    /// Creates mod pages the way adding a collection does, which leaves them with no timestamp.
    /// </summary>
    private async Task<NexusModsModPageMetadata.ReadOnly[]> CreatePages(int count)
    {
        using var tx = Connection.BeginTransaction();

        var ids = Enumerable.Range(1, count)
            .Select(id => CreateMod(new ModUid(ModId.From((uint)id), GameId), name: $"Old name of {id}").Resolve(Connection.Db, tx, setFilesTimestamp: false))
            .ToArray();

        var result = await tx.Commit();
        return ids.Select(id => NexusModsModPageMetadata.Load(result.Db, result[id])).ToArray();
    }

    private static bool HasTimestamp(NexusModsModPageMetadata.ReadOnly page) => NexusModsModPageMetadata.DataUpdatedAt.TryGetValue(page, out _);

    private static IMod CreateMod(ModUid uid, string? name = null)
    {
        var mod = Substitute.For<IMod>();
        mod.Uid.Returns(uid.ToV2Api());
        mod.Name.Returns(name ?? $"Fresh name of {uid.ModId}");
        mod.Game.DomainName.Returns("cyberpunk2077");
        mod.UpdatedAt.Returns(DateTimeOffset.UnixEpoch);
        mod.PictureUrl.Returns((string?)null);
        mod.ThumbnailUrl.Returns((string?)null);
        return mod;
    }

    private static IModFile CreateFile(ModUid uid)
    {
        var file = Substitute.For<IModFile>();
        file.Uid.Returns(new FileUid(FileId.From(uid.ModId.Value), uid.GameId).ToV2Api());
        file.Name.Returns($"File of {uid.ModId}");
        file.Version.Returns("1.0");
        file.SizeInBytes.Returns("1");
        return file;
    }

    private sealed class TestError : IGraphQlError
    {
        public string Message => "test";
        public ErrorCode Code => default;
    }
}
