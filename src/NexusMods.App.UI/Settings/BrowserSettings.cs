using NexusMods.Sdk.Settings;

namespace NexusMods.App.UI.Settings;

public record BrowserSettings : ISettings
{
    /// <summary>
    /// Whether the in-app browser drops requests to known ad and tracker domains.
    /// </summary>
    public bool BlockAdsAndTrackers { get; set; } = true;

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.BlockAdsAndTrackers,
            new PropertyOptions<BrowserSettings, bool>
            {
                Section = Sections.Privacy,
                DisplayName = "Block ads and trackers in the in-app browser",
                DescriptionFactory = _ => "When enabled, pages opened inside the app don't load requests to known advertising and tracking domains. Turn this off if a page doesn't work correctly.",
            },
            new BooleanContainerOptions()
        );
    }
}
