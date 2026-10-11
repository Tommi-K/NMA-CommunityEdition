using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Unit = R3.Unit;

namespace NexusMods.App.UI.Pages.Dlc;

/// <summary>
/// Lists the game's own optional content and lets the user switch each piece on or off.
/// </summary>
/// <remarks>
/// Switching a piece off does not touch the files - they belong to the game installation. It
/// records the choice in the data model, and the next apply leaves that content's plugin out of
/// the plugins file the app generates, which is what stops the game loading it.
/// </remarks>
public sealed class DlcPageViewModel : APageViewModel<IDlcPageViewModel>, IDlcPageViewModel
{
    public ReadOnlyObservableCollection<IDlcItemViewModel> Items { get; }
    public bool HasItems { get; }

    public DlcPageViewModel(IServiceProvider serviceProvider, IWindowManager windowManager, LoadoutId loadoutId) : base(windowManager)
    {
        TabIcon = IconValues.Package;
        TabTitle = Language.DlcPage_Title;

        var connection = serviceProvider.GetRequiredService<IConnection>();
        var logger = serviceProvider.GetRequiredService<ILogger<DlcPageViewModel>>();

        var loadout = Loadout.Load(connection.Db, loadoutId);
        var locations = loadout.InstallationInstance.Locations;

        var disabledIds = DisabledDlcIds(connection.Db, loadoutId);

        var items = loadout.InstallationInstance.Game is IGameWithDlc gameWithDlc
            ? gameWithDlc.Dlc
                // The list says what the game can have; only offer what this copy actually has
                // installed, so a base-game install doesn't show six switches that do nothing.
                .Where(dlc => locations.ToAbsolutePath(dlc.Plugin).FileExists)
                .Select(dlc => (IDlcItemViewModel)new DlcItemViewModel(
                    connection,
                    logger,
                    loadoutId,
                    dlc,
                    isEnabled: !disabledIds.Contains(dlc.Id)))
                .ToArray()
            : [];

        Items = new ReadOnlyObservableCollection<IDlcItemViewModel>(new ObservableCollection<IDlcItemViewModel>(items));
        HasItems = items.Length > 0;

        this.WhenActivated(disposables =>
        {
            // The data model is the authority: the switches follow it, whoever wrote it. This is
            // also what puts a row back after a failed write, since nothing else moves the switch.
            DisabledGameDlc.ObserveAll(connection)
                .FilterImmutable(entry => entry.LoadoutId == loadoutId)
                .QueryWhenChanged(query => query.Items.Select(static entry => entry.DlcId).ToHashSet(StringComparer.OrdinalIgnoreCase))
                .OnUI()
                .SubscribeWithErrorLogging(disabled =>
                {
                    foreach (var item in Items.OfType<DlcItemViewModel>())
                    {
                        item.IsEnabled = !disabled.Contains(item.Id);
                    }
                })
                .DisposeWith(disposables);
        });
    }

    private static HashSet<string> DisabledDlcIds(IDb db, LoadoutId loadoutId) => DisabledGameDlc
        .FindByLoadout(db, loadoutId)
        .Select(static entry => entry.DlcId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One row on the DLC page.
/// </summary>
public sealed class DlcItemViewModel : ReactiveObject, IDlcItemViewModel
{
    public string Id { get; }
    public string Name { get; }
    public string PluginName { get; }

    [Reactive] public bool IsEnabled { get; set; }

    public R3.ReactiveCommand<Unit> CommandToggle { get; }

    public DlcItemViewModel(
        IConnection connection,
        ILogger logger,
        LoadoutId loadoutId,
        GameDlc dlc,
        bool isEnabled)
    {
        Id = dlc.Id;
        Name = dlc.Name;
        PluginName = dlc.Plugin.FileName.ToString();
        IsEnabled = isEnabled;

        CommandToggle = new R3.ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var shouldEnable = !IsEnabled;

                try
                {
                    using var tx = connection.BeginTransaction();

                    if (shouldEnable)
                    {
                        // Several entries for one id shouldn't happen, but retract whatever is
                        // there rather than leaving the content stuck off.
                        foreach (var entry in DisabledGameDlc.FindByLoadout(connection.Db, loadoutId))
                        {
                            if (!string.Equals(entry.DlcId, Id, StringComparison.OrdinalIgnoreCase)) continue;
                            tx.Delete(entry.Id, recursive: false);
                        }
                    }
                    else
                    {
                        _ = new DisabledGameDlc.New(tx)
                        {
                            LoadoutId = loadoutId,
                            DlcId = Id,
                        };
                    }

                    await tx.Commit();
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to switch DLC `{Dlc}` {State} for loadout `{Loadout}`", Id, shouldEnable ? "on" : "off", loadoutId);
                }
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );
    }
}
