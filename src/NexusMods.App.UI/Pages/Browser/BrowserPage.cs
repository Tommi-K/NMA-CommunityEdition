using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Context for a tab showing a web page inside the app.
/// </summary>
[JsonName("BrowserPageContext")]
public record BrowserPageContext : IPageFactoryContext
{
    /// <summary>
    /// The page to load when the tab opens.
    /// </summary>
    public required Uri Uri { get; init; }

    /// <summary>
    /// Title to show on the tab until the page reports its own.
    /// </summary>
    public string? InitialTitle { get; init; }

    /// <summary>
    /// Whether this tab should close itself once a download handoff has been taken.
    /// Only set for tabs opened purely to start a download; a tab the user is browsing
    /// in stays put. Defaults to false, which is also what older persisted workspaces
    /// deserialize to.
    /// </summary>
    public bool CloseAfterDownload { get; init; }

    /// <summary>
    /// Whether the tab should press the download button on the page itself rather than
    /// waiting for the user to do it. Set for tabs opened by a download action; defaults to
    /// false, which is also what older persisted workspaces deserialize to.
    /// </summary>
    public bool AutoStartDownload { get; init; }
}

[UsedImplicitly]
public class BrowserPageFactory : APageFactory<IBrowserPageViewModel, BrowserPageContext>
{
    public BrowserPageFactory(IServiceProvider serviceProvider) : base(serviceProvider) { }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("2d145046-00f8-4b06-955b-dd59fddf91e2"));
    public override PageFactoryId Id => StaticId;

    public override IBrowserPageViewModel CreateViewModel(BrowserPageContext context)
    {
        var vm = ServiceProvider.GetRequiredService<IBrowserPageViewModel>();
        vm.Context = context;
        return vm;
    }
}
