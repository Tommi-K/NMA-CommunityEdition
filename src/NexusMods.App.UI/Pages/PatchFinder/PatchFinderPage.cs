using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Serialization.Attributes;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.UI.Sdk.Icons;

namespace NexusMods.App.UI.Pages.PatchFinder;

[JsonName("NexusMods.App.UI.Pages.PatchFinder.PatchFinderPageContext")]
public record PatchFinderPageContext : IPageFactoryContext
{
    public required LoadoutId LoadoutId { get; init; }

    /// <summary>
    /// The one mod to look for patches for, as <see cref="ModUid.AsUlong"/>, or null for every
    /// mod in the loadout. Kept as a number so the page survives being saved with the workspace.
    /// </summary>
    public ulong? ModPageUid { get; init; }
}

[UsedImplicitly]
public class PatchFinderPageFactory : APageFactory<IPatchFinderViewModel, PatchFinderPageContext>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _conn;

    public PatchFinderPageFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _conn = serviceProvider.GetRequiredService<IConnection>();
    }

    public static readonly PageFactoryId StaticId = PageFactoryId.From(Guid.Parse("4c0b8f0e-6a3e-4f37-9d2b-5be7d1e3a9c4"));
    public override PageFactoryId Id => StaticId;

    public override IPatchFinderViewModel CreateViewModel(PatchFinderPageContext context)
    {
        var onlyFor = context.ModPageUid is { } uid ? ModUid.FromUlong(uid) : (ModUid?)null;
        return new PatchFinderViewModel(_serviceProvider, WindowManager, context.LoadoutId, onlyFor);
    }

    public override IEnumerable<PageDiscoveryDetails?> GetDiscoveryDetails(IWorkspaceContext workspaceContext)
    {
        if (workspaceContext is not LoadoutContext loadoutContext) yield break;

        var loadout = Loadout.Load(_conn.Db, loadoutContext.LoadoutId);
        if (!loadout.IsValid()) yield break;

        yield return new PageDiscoveryDetails
        {
            SectionName = "Utilities",
            ItemName = PatchFinderViewModel.Title,
            Icon = IconValues.PuzzleCheckOutline,
            PageData = new PageData
            {
                FactoryId = StaticId,
                Context = new PatchFinderPageContext
                {
                    LoadoutId = loadout.LoadoutId,
                },
            },
        };
    }
}
