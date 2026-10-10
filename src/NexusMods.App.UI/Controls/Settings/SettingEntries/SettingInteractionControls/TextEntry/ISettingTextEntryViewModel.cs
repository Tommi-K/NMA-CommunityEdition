using NexusMods.UI.Sdk.Settings;

namespace NexusMods.App.UI.Controls.Settings.SettingEntries.TextEntry;

public interface ISettingTextEntryViewModel : IInteractionControl
{
    StringContainer StringContainer { get; }

    string? Placeholder { get; }

    bool IsMultiLine { get; }

    double ControlWidth { get; }

    double MaxControlHeight { get; }
}
