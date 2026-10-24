using JetBrains.Annotations;
using NexusMods.Sdk.Settings;

namespace NexusMods.App.UI.Settings;

/// <summary>
/// What the patches and translations page was last left showing.
/// </summary>
public record PatchFinderSettings : ISettings
{
    /// <summary>
    /// Stands for every language in <see cref="TranslationLanguage"/>.
    /// </summary>
    public const string AllLanguages = "*";

    /// <summary>
    /// The language translations were last filtered by: an ISO 639-1 code, <see cref="AllLanguages"/>,
    /// or null to follow the app's own language.
    /// </summary>
    public string? TranslationLanguage { get; [UsedImplicitly] set; }

    /// <summary>
    /// Whether patches that need mods the loadout doesn't have were last shown.
    /// </summary>
    public bool ShowPatchesNeedingOtherMods { get; [UsedImplicitly] set; }

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder) => settingsBuilder;
}
