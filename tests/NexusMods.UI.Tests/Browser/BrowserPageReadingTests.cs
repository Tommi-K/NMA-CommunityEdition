using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.App.UI.Pages.Browser;
using NexusMods.App.UI.Windows;
using NexusMods.CLI.Types;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using NSubstitute;

namespace NexusMods.UI.Tests.Browser;

/// <summary>
/// How the tab a run of downloads goes through gets from one download page to the next: by
/// reading it once it has a page of the site open, and by loading it when that doesn't work.
/// </summary>
public class BrowserPageReadingTests(IServiceProvider provider) : AUiTest(provider)
{
    private static Uri PageFor(int mod, string host = "www.nexusmods.com") => new($"https://{host}/game/mods/{mod}?tab=files&file_id={mod}&nmm=1");

    private static string LinkFor(int mod) => $"nxm://game/mods/{mod}/files/{mod}?key=k&expires=1&user_id=1";

    private sealed class Tab
    {
        public required BrowserPageViewModel VM { get; init; }
        public required BrowserDownloadTracker Tracker { get; init; }
        public List<string> Scripts { get; } = [];
    }

    private async Task<Tab> OpenTabOnFirstPage()
    {
        Tab tab = null!;

        await OnUi(() =>
        {
            var tracker = new BrowserDownloadTracker();
            var handler = Substitute.For<IIpcProtocolHandler>();
            handler.Protocol.Returns("nxm");

            var services = new ServiceCollection()
                .AddSingleton(tracker)
                .AddSingleton(handler)
                .AddSingleton(Substitute.For<ILoginManager>())
                .AddSingleton(new AdBlocker(NullLogger<AdBlocker>.Instance, Provider.GetRequiredService<ISettingsManager>()))
                .BuildServiceProvider();

            var vm = new BrowserPageViewModel(NullLogger<BrowserPageViewModel>.Instance, Substitute.For<IWindowManager>(), Substitute.For<IOSInterop>(), services);
            tab = new Tab { VM = vm, Tracker = tracker };

            vm.Activator.Activate();
            vm.PageScriptRunner = tab.Scripts.Add;
            vm.Context = new BrowserPageContext
            {
                Uri = PageFor(1),
                AutoStartDownload = true,
                IsDownloadDriver = true,
                DownloadRequestId = tracker.Add(),
            };
        });

        return tab;
    }

    /// <summary>
    /// Sends the tab to a page and returns what it was asked for under, once that has landed.
    /// </summary>
    private async Task<Guid> Drive(Tab tab, Uri page)
    {
        var requestId = tab.Tracker.Add();
        tab.VM.DriveDownload(page, "title", requestId);
        await OnUi(() => { });
        return requestId;
    }

    private async Task HandOff(Tab tab, int mod) => await OnUi(() => tab.VM.TryHandleAppUri(LinkFor(mod)).Should().BeTrue());

    private async Task FailToRead(Tab tab, int mod) => await OnUi(() => tab.VM.ReportMustLoadPage(mod.ToString()));

    [Fact]
    public async Task TheFirstPage_IsLoaded()
    {
        var tab = await OpenTabOnFirstPage();

        tab.VM.Address.Should().Be(PageFor(1).ToString());
        tab.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task APageBeforeAnythingWasHandedOver_IsLoaded()
    {
        var tab = await OpenTabOnFirstPage();

        await Drive(tab, PageFor(2));

        tab.VM.Address.Should().Be(PageFor(2).ToString());
        tab.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePageAfterAHandoff_IsReadWithoutGoingThere()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);

        var requestId = await Drive(tab, PageFor(2));

        tab.VM.Address.Should().Be(PageFor(1).ToString(), because: "the page it is read from has to stay open");
        tab.Scripts.Should().ContainSingle().Which.Should().Contain("\"2\"");
        tab.VM.Context!.Uri.Should().Be(PageFor(2));

        // What the run is waiting on works the same as for a loaded page.
        var handoff = tab.Tracker.WaitForHandoff(requestId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), CancellationToken.None);
        await HandOff(tab, 2);
        (await handoff).Should().Be(HandoffResult.HandedOff);
    }

    [Fact]
    public async Task APageThatCantBeRead_IsLoaded()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);
        await Drive(tab, PageFor(2));

        await FailToRead(tab, 2);

        tab.VM.Address.Should().Be(PageFor(2).ToString());
    }

    [Fact]
    public async Task AnAnswerAboutAnotherFile_IsIgnored()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);
        await Drive(tab, PageFor(2));

        await FailToRead(tab, 99);

        tab.VM.Address.Should().Be(PageFor(1).ToString());
    }

    [Fact]
    public async Task ThreeFailuresInARow_GoBackToLoadingPages()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);

        for (var mod = 2; mod <= 4; mod++)
        {
            await Drive(tab, PageFor(mod));
            tab.Scripts.Should().HaveCount(mod - 1);

            await FailToRead(tab, mod);
            await HandOff(tab, mod);
        }

        await Drive(tab, PageFor(5));

        tab.Scripts.Should().HaveCount(3);
        tab.VM.Address.Should().Be(PageFor(5).ToString());
    }

    [Fact]
    public async Task AFailureNowAndThen_DoesntAddUp()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);

        for (var mod = 2; mod <= 10; mod += 3)
        {
            await Drive(tab, PageFor(mod));
            await FailToRead(tab, mod);
            await HandOff(tab, mod);

            await Drive(tab, PageFor(mod + 1));
            await FailToRead(tab, mod + 1);
            await HandOff(tab, mod + 1);

            // Read, and a success, which is what clears the count.
            await Drive(tab, PageFor(mod + 2));
            await HandOff(tab, mod + 2);
        }

        var before = tab.Scripts.Count;
        await Drive(tab, PageFor(50));

        tab.Scripts.Should().HaveCount(before + 1);
    }

    [Fact]
    public async Task ATabWithoutAView_LoadsPages()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);

        await OnUi(() => tab.VM.PageScriptRunner = null);
        await Drive(tab, PageFor(2));

        tab.VM.Address.Should().Be(PageFor(2).ToString());
    }

    [Fact]
    public async Task APageOnAnotherSite_IsLoaded()
    {
        var tab = await OpenTabOnFirstPage();
        await HandOff(tab, 1);

        await Drive(tab, PageFor(2, host: "next.nexusmods.com"));

        tab.Scripts.Should().BeEmpty();
        tab.VM.Address.Should().Be(PageFor(2, host: "next.nexusmods.com").ToString());
    }

    [Fact]
    public void TheReader_IsBuiltForItsPage()
    {
        var script = AutoDownloadScript.BuildPageReader(PageFor(7), "7");

        script.Should().NotContain("__PAGE__").And.NotContain("__FILE_ID__").And.NotContain("__SUSPENSION__");
        script.Should().Contain("function suspensionIn(text)").And.Contain("file_id=7").And.Contain("var wanted = \"7\";");
    }

    [Fact]
    public void ThePageScript_HasTheSameNoticeCheck()
    {
        var script = AutoDownloadScript.Build(preferFast: false);

        script.Should().NotContain("__SUSPENSION__").And.NotContain("__PREFER_FAST__");
        script.Should().Contain("function suspensionIn(text)").And.Contain("suspensionIn(pageText())");
    }
}
