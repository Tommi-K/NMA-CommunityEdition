using System.Reactive.Disposables;
using Avalonia.Data.Converters;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.LoadoutPage.Dialogs.InstallRequirements;

[UsedImplicitly]
public partial class DialogInstallRequirementsView : ReactiveUserControl<IDialogInstallRequirementsViewModel>
{
    /// <summary>
    /// Fades a row that won't be installed because only unticked mods need it.
    /// </summary>
    public static readonly IValueConverter NeededToOpacity = new FuncValueConverter<bool, double>(static isNeeded => isNeeded ? 1.0 : 0.5);

    public DialogInstallRequirementsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.SummaryText, view => view.SummaryText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ToInstall, view => view.ToInstallItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ToInstall, view => view.ToInstallSection.IsVisible, static items => items.Count > 0)
                .DisposeWith(disposables);

            // Clicking install with nothing ticked does nothing, which this makes plain.
            this.OneWayBind(ViewModel, vm => vm.SelectedCount, view => view.SelectedCountText.Text, static count => $"{count} ticked")
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ToCheck, view => view.ToCheckItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ToCheck, view => view.ToCheckSection.IsVisible, static items => items.Count > 0)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.FootnoteText, view => view.FootnoteText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.FootnoteText, view => view.FootnoteText.IsVisible, static text => !string.IsNullOrEmpty(text))
                .DisposeWith(disposables);
        });
    }
}
