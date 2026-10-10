using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.Settings.SettingEntries.TextEntry;

[UsedImplicitly]
public partial class SettingTextEntryControl : ReactiveUserControl<ISettingTextEntryViewModel>
{
    public SettingTextEntryControl()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not null)
            {
                TextBox.Watermark = ViewModel.Placeholder;
                TextBox.AcceptsReturn = ViewModel.IsMultiLine;
                TextBox.Width = ViewModel.ControlWidth;
                if (ViewModel.IsMultiLine) TextBox.MaxHeight = ViewModel.MaxControlHeight;
            }

            // NOTE(CE): the container value is non-nullable, an emptied text box hands us null
            this.Bind(ViewModel,
                    vm => vm.StringContainer.CurrentValue,
                    view => view.TextBox.Text)
                .DisposeWith(disposables);
        });
    }
}
