using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.PatchFinder;

[UsedImplicitly]
public partial class PatchFinderView : ReactiveUserControl<IPatchFinderViewModel>
{
    public PatchFinderView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.HeaderText, view => view.HeaderText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.DescriptionText, view => view.SubHeaderText.Text)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandExport, view => view.ExportButton)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.CanExport, view => view.ExportButton.IsEnabled)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.CommandRescan, view => view.RescanButton)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.IsScanning, view => view.RescanButton.IsEnabled, static isScanning => !isScanning)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.StatusText, view => view.StatusText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.IsScanning, view => view.ScanProgress.IsVisible)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Progress, view => view.ScanProgress.Value)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Patches, view => view.PatchItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.PatchesEmptyText, view => view.PatchesEmptyText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.PatchesEmptyText, view => view.PatchesEmptyText.IsVisible, static text => text is not null)
                .DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.ShowPatchesNeedingOtherMods, view => view.ShowAllPatchesCheckBox.IsChecked)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Translations, view => view.TranslationItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.TranslationsEmptyText, view => view.TranslationsEmptyText.Text)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.TranslationsEmptyText, view => view.TranslationsEmptyText.IsVisible, static text => text is not null)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Languages, view => view.LanguageSelector.ItemsSource)
                .DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SelectedLanguage, view => view.LanguageSelector.SelectedItem)
                .DisposeWith(disposables);
        });
    }
}
