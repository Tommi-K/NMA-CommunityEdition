using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.Dlc;

[JsonName("NexusMods.App.UI.Pages.Dlc.DlcPageContext")]
public record DlcPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }
}

[UsedImplicitly]
public class DlcPageFactory : APageFactory<IDlcPageViewModel, DlcPageContext>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _conn;

    public DlcPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("8f1bb5a4-4e1e-4b8f-bb4a-9a5a2f0f6d21"));
    public override PageFactoryId Id => StaticId;

    public override IDlcPageViewModel CreateViewModel(DlcPageContext context)
    {
        return new DlcPageViewModel(_serviceProvider, WindowManager, context.LoadoutId);
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);
        if (!loadout.IsValid()) yield break;

        // Only games that ship switchable content of their own get the page.
        if (loadout.InstallationInstance.Game is not IGameWithDlc) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = Language.DlcPage_Title,
            Icon = IconValues.Package,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new DlcPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
