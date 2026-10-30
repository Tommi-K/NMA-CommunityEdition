using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.TestFramework;
using Xunit.Abstractions;

namespace NexusMods.Networking.NexusWebApi.Tests;

/// <summary>
/// How a run of downloads without premium holds off asking for files it can't use yet.
/// </summary>
public class DownloadQueueTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<DownloadQueueTests>(helper)
{
    private const int MaxRunning = 4;
    private const int MaxWaiting = 4;

    private NexusModsLibrary Library => ServiceProvider.GetRequiredService<NexusModsLibrary>();

    [Fact]
    public async Task RoomIsThere_UntilEnoughAreWaitingForTheirTurn()
    {
        var library = Library;
        var running = await Take(library, MaxRunning);

        // Running doesn't count: only those with nothing to do yet are of no use.
        await library.WaitForRoomInDownloadQueue(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var waiting = Enumerable.Range(0, MaxWaiting - 1).Select(_ => library.WaitForDownloadTurn(CancellationToken.None).AsTask()).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        await library.WaitForRoomInDownloadQueue(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Release(running);
        (await Task.WhenAll(waiting)).Should().HaveCount(MaxWaiting - 1);
    }

    [Fact]
    public async Task AFullQueue_HoldsTheRun_UntilADownloadGetsItsTurn()
    {
        var library = Library;
        var running = await Take(library, MaxRunning);
        var waiting = Enumerable.Range(0, MaxWaiting).Select(_ => library.WaitForDownloadTurn(CancellationToken.None).AsTask()).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var room = library.WaitForRoomInDownloadQueue(CancellationToken.None).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        room.IsCompleted.Should().BeFalse(because: "the downloads have plenty to be getting on with");

        // One finishing lets one of those waiting start, which makes room for another.
        running[0].Dispose();
        await room.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var turn in running.Skip(1)) turn.Dispose();
        foreach (var turn in await Task.WhenAll(waiting)) turn.Dispose();
    }

    [Fact]
    public async Task CancellingARun_EndsItsWaitForRoom()
    {
        var library = Library;
        var running = await Take(library, MaxRunning);
        var waiting = Enumerable.Range(0, MaxWaiting).Select(_ => library.WaitForDownloadTurn(CancellationToken.None).AsTask()).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        using var cancellation = new CancellationTokenSource();
        var room = library.WaitForRoomInDownloadQueue(cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        var waitingForRoom = () => room.WaitAsync(TimeSpan.FromSeconds(5));
        await waitingForRoom.Should().ThrowAsync<OperationCanceledException>();

        Release(running);
        foreach (var turn in await Task.WhenAll(waiting)) turn.Dispose();
    }

    [Fact]
    public async Task DroppingTheWaitingDownloads_MakesRoomAgain()
    {
        var library = Library;
        var running = await Take(library, MaxRunning);
        var waiting = Enumerable.Range(0, MaxWaiting).Select(_ => library.WaitForDownloadTurn(CancellationToken.None).AsTask()).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        library.DropWaitingDownloads();

        var dropped = () => Task.WhenAll(waiting);
        await dropped.Should().ThrowAsync<OperationCanceledException>();

        await library.WaitForRoomInDownloadQueue(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Release(running);
    }

    private static async Task<IDisposable[]> Take(NexusModsLibrary library, int count)
    {
        var turns = new IDisposable[count];
        for (var i = 0; i < count; i++) turns[i] = await library.WaitForDownloadTurn(CancellationToken.None);
        return turns;
    }

    private static void Release(IEnumerable<IDisposable> turns)
    {
        foreach (var turn in turns) turn.Dispose();
    }
}
