using FluentAssertions;
using NexusMods.App.UI.Pages.Browser;
using Xilium.CefGlue;

namespace NexusMods.UI.Tests.Browser;

public class InAppBrowserRequestHandlerTests
{
    [Theory]
    [InlineData(CefResourceType.Image)]
    [InlineData(CefResourceType.Media)]
    [InlineData(CefResourceType.Favicon)]
    [InlineData(CefResourceType.Prefetch)]
    [InlineData(CefResourceType.Ping)]
    public void WhatADownloadPageDoesntNeed_IsSkipped(CefResourceType resourceType)
    {
        InAppBrowserRequestHandler.IsNeedlessForDownload(resourceType, isMainFrame: true).Should().BeTrue();
    }

    // The page itself, and what the download button and a Cloudflare check rely on.
    [Theory]
    [InlineData(CefResourceType.MainFrame)]
    [InlineData(CefResourceType.SubFrame)]
    [InlineData(CefResourceType.Script)]
    [InlineData(CefResourceType.Stylesheet)]
    [InlineData(CefResourceType.Xhr)]
    [InlineData(CefResourceType.SubResource)]
    public void WhatADownloadPageMightNeed_IsLeftAlone(CefResourceType resourceType)
    {
        InAppBrowserRequestHandler.IsNeedlessForDownload(resourceType, isMainFrame: true).Should().BeFalse();
    }

    // A Cloudflare check runs in a frame of its own, images and all.
    [Theory]
    [InlineData(CefResourceType.Image)]
    [InlineData(CefResourceType.Media)]
    [InlineData(CefResourceType.Favicon)]
    [InlineData(CefResourceType.Prefetch)]
    [InlineData(CefResourceType.Ping)]
    public void OtherFrames_AreLeftAlone(CefResourceType resourceType)
    {
        InAppBrowserRequestHandler.IsNeedlessForDownload(resourceType, isMainFrame: false).Should().BeFalse();
    }
}
