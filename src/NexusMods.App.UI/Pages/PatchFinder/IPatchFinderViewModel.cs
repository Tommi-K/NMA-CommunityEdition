using NexusMods.App.UI.WorkspaceSystem;

namespace NexusMods.App.UI.Pages.PatchFinder;

public interface IPatchFinderViewModel : IPageViewModelInterface
{
    /// <summary>
    /// The page heading, naming the mod when the page is about just one.
    /// </summary>
    string HeaderText { get; }

    /// <summary>
    /// What the page lists and where it comes from.
    /// </summary>
    string DescriptionText { get; }

    /// <summary>
    /// Whether the lists are being read right now.
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// What the scan is doing while it runs, and what it found once it is done.
    /// </summary>
    string StatusText { get; }

    /// <summary>
    /// How far the current stage of the scan has got, from 0 to 100.
    /// </summary>
    double Progress { get; }

    /// <summary>
    /// Patches not yet installed, filtered by <see cref="ShowPatchesNeedingOtherMods"/>.
    /// </summary>
    IReadOnlyList<IPatchFinderItemViewModel> Patches { get; }

    /// <summary>
    /// Translations not yet installed, filtered by <see cref="SelectedLanguage"/>.
    /// </summary>
    IReadOnlyList<IPatchFinderItemViewModel> Translations { get; }

    /// <summary>
    /// Shown in place of <see cref="Patches"/> when it is empty after a scan, otherwise null.
    /// </summary>
    string? PatchesEmptyText { get; }

    /// <summary>
    /// Shown in place of <see cref="Translations"/> when it is empty after a scan, otherwise null.
    /// </summary>
    string? TranslationsEmptyText { get; }

    /// <summary>
    /// The languages translations can be filtered by: "All languages" first, then every
    /// language a translation was found in, plus the app's own.
    /// </summary>
    IReadOnlyList<PatchFinderLanguageOption> Languages { get; }

    /// <summary>
    /// The language translations are shown for. Starts as the app's language.
    /// </summary>
    PatchFinderLanguageOption? SelectedLanguage { get; set; }

    /// <summary>
    /// Whether to list patches that also require mods the loadout doesn't have. Those are
    /// patches for combinations the user isn't running, so they are hidden by default.
    /// </summary>
    bool ShowPatchesNeedingOtherMods { get; set; }

    /// <summary>
    /// Reads the lists again.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandRescan { get; }

    /// <summary>
    /// Whether there is a finished scan to save.
    /// </summary>
    bool CanExport { get; }

    /// <summary>
    /// Saves what the page shows as a mod list the loadout's Import can download and install from.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandExport { get; }
}

public interface IPatchFinderItemViewModel
{
    /// <summary>
    /// The name of the patch or translation.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The installed mods it is for.
    /// </summary>
    string ForText { get; }

    /// <summary>
    /// The mods it also requires that are not installed, or null when there are none.
    /// </summary>
    string? MissingText { get; }

    /// <summary>
    /// The language of a translation, or null for a patch.
    /// </summary>
    string? LanguageText { get; }

    /// <summary>
    /// Opens its page on Nexus Mods.
    /// </summary>
    R3.ReactiveCommand<R3.Unit> CommandOpen { get; }
}

/// <summary>
/// An entry of the translation language filter.
/// </summary>
/// <param name="Code">The ISO 639-1 code, or null for every language.</param>
/// <param name="DisplayName">What the filter shows.</param>
public sealed record PatchFinderLanguageOption(string? Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}
