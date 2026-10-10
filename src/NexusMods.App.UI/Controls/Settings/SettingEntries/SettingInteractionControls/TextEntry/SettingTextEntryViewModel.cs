using System.Reactive.Disposables;
using JetBrains.Annotations;
using NexusMods.Sdk.Settings;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Settings;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.App.UI.Controls.Settings.SettingEntries.TextEntry;

public class SettingTextEntryViewModel : AViewModel<ISettingTextEntryViewModel>, ISettingTextEntryViewModel
{
    public StringContainer StringContainer { get; }

    public IPropertyValueContainer ValueContainer => StringContainer;

    public string? Placeholder { get; }

    public bool IsMultiLine { get; }

    public double ControlWidth { get; }

    public double MaxControlHeight { get; }

    [Reactive] public bool HasChanged { get; private set; }

    public SettingTextEntryViewModel(StringContainer stringContainer, TextEntryContainerOptions containerOptions)
    {
        StringContainer = stringContainer;

        Placeholder = containerOptions.Placeholder;
        IsMultiLine = containerOptions.IsMultiLine;
        ControlWidth = containerOptions.ControlWidth;
        MaxControlHeight = containerOptions.MaxControlHeight;

        this.WhenActivated(disposables =>
        {
            ValueContainer.WhenAnyValue(x => x.HasChanged)
                .BindToVM(this, vm => vm.HasChanged)
                .DisposeWith(disposables);
        });
    }
}

[UsedImplicitly]
public class SettingTextEntryFactory : IInteractionControlFactory<TextEntryContainerOptions>
{
    public IInteractionControl Create(
        IServiceProvider serviceProvider,
        ISettingsManager settingsManager,
        TextEntryContainerOptions containerOptions,
        PropertyConfig propertyConfig)
    {
        return new SettingTextEntryViewModel(
            new StringContainer(
                value: propertyConfig.GetValueCasted<string>(settingsManager),
                defaultValue: propertyConfig.GetDefaultValueCasted<string>(settingsManager),
                config: propertyConfig
            ),
            containerOptions
        );
    }
}
