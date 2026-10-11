using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using NexusMods.App.UI.Resources;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Dlc;

[UsedImplicitly]
public partial class DlcPageView : ReactiveUserControl<IDlcPageViewModel>
{
    public DlcPageView()
    {
        InitializeComponent();

        HeaderText.Text = Language.DlcPage_Title;
        SubHeaderText.Text = Language.DlcPage_Description;
        EmptyText.Text = Language.DlcPage_Empty;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Items, view => view.DlcItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.HasItems, view => view.DlcItems.IsVisible)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.HasItems, view => view.EmptyText.IsVisible, hasItems => !hasItems)
                .DisposeWith(disposables);
        });
    }
}
