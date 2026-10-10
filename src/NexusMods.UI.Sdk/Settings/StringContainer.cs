using JetBrains.Annotations;
using NexusMods.Sdk.Settings;

namespace NexusMods.UI.Sdk.Settings;

[PublicAPI]
public class StringContainer : APropertyValueContainer<string, TextEntryContainerOptions>
{
    public StringContainer(
        string value,
        string defaultValue,
        PropertyConfig config) : base(value, defaultValue, config) { }
}
