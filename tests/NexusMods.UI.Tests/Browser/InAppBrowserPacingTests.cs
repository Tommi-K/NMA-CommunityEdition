using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NexusMods.App.UI.Pages.Browser;
using NexusMods.App.UI.Windows;
using NexusMods.Sdk;
using NSubstitute;

namespace NexusMods.UI.Tests.Browser;

public class InAppBrowserPacingTests
{
    private static readonly Uri FirstPage = new("https://www.nexusmods.com/game/mods/1?tab=files&file_id=1");
    private static readonly Uri SecondPage = new("https://www.nexusmods.com/game/mods/2?tab=files&file_id=2");

    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(100);

    private readonly FakeTimeProvider _time = new();
    private readonly BrowserDownloadTracker _tracker = new();
    private readonly FakeDriver _driver;
    private readonly InAppBrowser _browser;

    public InAppBrowserPacingTests()
    {
        _driver = new FakeDriver(_tracker, _time);
        _tracker.RegisterDriver(_driver);
        _browser = new InAppBrowser(NullLogger<InAppBrowser>.Instance, Substitute.For<IWindowManager>(), _tracker, _time);
    }

    [Fact]
    public async Task Pages_AreAskedForNoFasterThanTheInterval()
    {
        using var session = _browser.BeginDownloadSession();

        await _browser.StartDownload(FirstPage);

        var second = _browser.StartDownload(SecondPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(2));
        await second;

        var third = _browser.StartDownload(FirstPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(2));
        await third;
    }

    [Fact]
    public async Task Suspension_HoldsTheRunForAsLongAsAskedThenTriesThePageAgain()
    {
        using var session = _browser.BeginDownloadSession();
        _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromMinutes(2)));

        var run = _browser.StartDownload(FirstPage).AsTask();
        await WaitFor(() => _driver.Pages.Count == 1);

        // The two minutes asked for, and the margin on top.
        await ExpectNextPageAfter(TimeSpan.FromSeconds(150));

        (await run).Should().Be(InAppDownloadOutcome.Started);
        _driver.Pages.Should().Equal(FirstPage, FirstPage);
    }

    [Fact]
    public async Task Suspension_WithoutADuration_IsTakenToBeTenMinutes()
    {
        using var session = _browser.BeginDownloadSession();
        _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, retryAfter: null));

        var run = _browser.StartDownload(FirstPage).AsTask();
        await WaitFor(() => _driver.Pages.Count == 1);

        await ExpectNextPageAfter(TimeSpan.FromSeconds(630));
        await run;
    }

    [Fact]
    public async Task Suspension_SaysSoOnTheTab()
    {
        using var session = _browser.BeginDownloadSession();
        _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromMinutes(1)));

        var start = _time.GetUtcNow();
        var run = _browser.StartDownload(FirstPage).AsTask();
        await WaitFor(() => _driver.PausedUntil is not null);

        _driver.PausedUntil.Should().Be(start + TimeSpan.FromSeconds(90));

        await ExpectNextPageAfter(TimeSpan.FromSeconds(90));
        await run;
    }

    [Fact]
    public async Task Suspension_DoublesTheInterval()
    {
        using var session = _browser.BeginDownloadSession();
        _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromSeconds(1)));

        var run = _browser.StartDownload(FirstPage).AsTask();
        await WaitFor(() => _driver.Pages.Count == 1);
        await ExpectNextPageAfter(TimeSpan.FromSeconds(31));
        await run;

        var next = _browser.StartDownload(SecondPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(4));
        await next;
    }

    [Fact]
    public async Task Suspension_DoublesTheInterval_OnlyUpToTheMaximum()
    {
        using var session = _browser.BeginDownloadSession();

        for (var i = 0; i < 5; i++)
        {
            _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromSeconds(1)));

            // Well past the interval, so the page is asked for straight away.
            _time.Advance(TimeSpan.FromMinutes(1));

            var pages = _driver.Pages.Count;
            var run = _browser.StartDownload(FirstPage).AsTask();
            await WaitFor(() => _driver.Pages.Count > pages);

            await ExpectNextPageAfter(TimeSpan.FromSeconds(31));
            await run;
        }

        var next = _browser.StartDownload(SecondPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(16));
        await next;
    }

    [Fact]
    public async Task Suspension_OutlivesTheRun()
    {
        using (_browser.BeginDownloadSession())
        {
            _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromMinutes(5)));

            using var cancellation = new CancellationTokenSource();
            var run = _browser.StartDownload(FirstPage, cancellationToken: cancellation.Token).AsTask();
            await WaitFor(() => _driver.PausedUntil is not null);
            await Settle();

            await cancellation.CancelAsync();
            var waiting = () => run;
            await waiting.Should().ThrowAsync<OperationCanceledException>();
        }

        // Pressing Download again straight away doesn't get a page out of it.
        using var secondRun = _browser.BeginDownloadSession();

        var second = _browser.StartDownload(SecondPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(330));
        await second;
    }

    [Fact]
    public async Task Cancelling_WhileHeld_LetsTheNextDownloadThrough()
    {
        using var session = _browser.BeginDownloadSession();
        _driver.Answer(FirstPage, id => _tracker.ReportSuspended(id, TimeSpan.FromSeconds(1)));

        using var cancellation = new CancellationTokenSource();
        var run = _browser.StartDownload(FirstPage, cancellationToken: cancellation.Token).AsTask();
        await WaitFor(() => _driver.PausedUntil is not null);
        await Settle();

        await cancellation.CancelAsync();
        var waiting = () => run;
        await waiting.Should().ThrowAsync<OperationCanceledException>();

        var next = _browser.StartDownload(SecondPage).AsTask();
        await ExpectNextPageAfter(TimeSpan.FromSeconds(31));

        (await next).Should().Be(InAppDownloadOutcome.Started);
    }

    [Theory]
    [InlineData("suspended 600 134069", "134069", 600)]
    [InlineData("suspended 3600 7", "7", 3600)]
    [InlineData("suspended 0 134069", "134069", null)]
    public void Report_IsReadWithItsFileAndDuration(string report, string expectedFileId, int? expectedSeconds)
    {
        AutoDownloadScript.TryParseSuspended(report, out var fileId, out var retryAfter).Should().BeTrue();

        fileId.Should().Be(expectedFileId);
        retryAfter.Should().Be(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    [Theory]
    [InlineData("handoff nxm://game/mods/1/files/1")]
    [InlineData("gave-up 134069")]
    [InlineData("suspended 600")]
    public void Report_OfSomethingElse_IsNotASuspension(string report)
    {
        AutoDownloadScript.TryParseSuspended(report, out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// Checks that the next page comes exactly <paramref name="gap"/> after the clock's current
    /// time: not while the clock is short of that, and with it moved no further.
    /// </summary>
    /// <remarks>
    /// The wait is worked out from absolute times, so it doesn't matter whether what is being
    /// waited on has got as far as its timer yet when the clock moves. Landing on the very tick
    /// also means the clock reads the same whenever the page gets round to looking at it, so
    /// each call can start from where the last one stopped, however slow the machine.
    /// </remarks>
    private async Task ExpectNextPageAfter(TimeSpan gap)
    {
        var pages = _driver.Pages.Count;

        await Settle();
        _time.Advance(gap - Tolerance);
        await Settle();

        _driver.Pages.Count.Should().Be(pages, because: $"the next page shouldn't come before {gap}");

        _time.Advance(Tolerance);
        await WaitFor(() => _driver.Pages.Count > pages);
    }

    private static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(50));

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, because: "it should have happened by now");
            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }
    }

    /// <summary>
    /// Stands in for the tab: answers every page with its handoff, unless told otherwise.
    /// </summary>
    private sealed class FakeDriver(BrowserDownloadTracker tracker, TimeProvider time) : IBrowserDownloadDriver
    {
        private readonly ConcurrentDictionary<Uri, ConcurrentQueue<Action<Guid>>> _answers = new();
        private readonly ConcurrentQueue<Uri> _pages = new();

        public IReadOnlyList<Uri> Pages => _pages.ToArray();

        public DateTimeOffset? PausedUntil { get; private set; }

        /// <summary>
        /// The next time <paramref name="uri"/> is loaded, it is answered this way instead.
        /// </summary>
        public void Answer(Uri uri, Action<Guid> answer) => _answers.GetOrAdd(uri, _ => new ConcurrentQueue<Action<Guid>>()).Enqueue(answer);

        public void DriveDownload(Uri uri, string? title, Guid requestId)
        {
            _pages.Enqueue(uri);

            tracker.ReportArmed(requestId);

            if (_answers.TryGetValue(uri, out var answers) && answers.TryDequeue(out var answer)) answer(requestId);
            else tracker.ReportHandoff(requestId);
        }

        public void CloseDriverTab() { }

        public void BringToFront() { }

        public void ShowPausedUntil(DateTimeOffset until) => PausedUntil = until;
    }
}
