using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Avalonia.Platform.Storage;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.App.UI.Settings;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Patches;
using NexusMods.Sdk;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.Settings;
using NexusMods.UI.Sdk;
using NexusMods.UI.Sdk.Icons;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Unit = R3.Unit;

namespace NexusMods.App.UI.Pages.PatchFinder;

/// <summary>
/// Lists the patches and translations for the mods in a loadout, as found on the "mods
/// requiring this file" lists of their pages on Nexus Mods.
/// </summary>
/// <remarks>
/// This is the list a user would otherwise go through by hand on every mod page, or wait for
/// LOOT to point out one entry of at a time.
/// </remarks>
public sealed class PatchFinderViewModel : APageViewModel<IPatchFinderViewModel>, IPatchFinderViewModel
{
    internal const string Title = "Patches & translations";

    // How many installed mods a row names before summing up the rest.
    private const int MaxNamesPerRow = 4;

    private static readonly PatchFinderLanguageOption AllLanguages = new(Code: null, "All languages");

    private readonly ILogger<PatchFinderViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPatchFinder _patchFinder;
    private readonly LoadoutId _loadoutId;
    private readonly ModUid? _onlyFor;
    private readonly string? _onlyForName;
    private readonly string _loadoutTitle;
    private readonly PatchFinderLanguageOption _appLanguage;

    private PatchFinderResult? _result;
    private bool _hasScanned;

    [Reactive] public bool IsScanning { get; private set; }
    [Reactive] public string StatusText { get; private set; } = string.Empty;
    [Reactive] public double Progress { get; private set; }
    [Reactive] public IReadOnlyList<IPatchFinderItemViewModel> Patches { get; private set; } = [];
    [Reactive] public IReadOnlyList<IPatchFinderItemViewModel> Translations { get; private set; } = [];
    [Reactive] public string? PatchesEmptyText { get; private set; }
    [Reactive] public string? TranslationsEmptyText { get; private set; }
    [Reactive] public IReadOnlyList<PatchFinderLanguageOption> Languages { get; private set; }
    [Reactive] public PatchFinderLanguageOption? SelectedLanguage { get; set; }
    [Reactive] public bool ShowPatchesNeedingOtherMods { get; set; }
    [Reactive] public bool CanExport { get; private set; }

    public string HeaderText { get; }
    public string DescriptionText { get; }

    public R3.ReactiveCommand<Unit> CommandRescan { get; }
    public R3.ReactiveCommand<Unit> CommandExport { get; }

    /// <param name="onlyFor">The one mod to look for patches for, or null for every mod in the loadout.</param>
    public PatchFinderViewModel(IServiceProvider serviceProvider, IWindowManager windowManager, LoadoutId loadoutId, ModUid? onlyFor = null) : base(windowManager)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<PatchFinderViewModel>>();
        _patchFinder = serviceProvider.GetRequiredService<IPatchFinder>();
        _loadoutId = loadoutId;
        _onlyFor = onlyFor;

        var db = serviceProvider.GetRequiredService<IConnection>().Db;
        var loadout = Loadout.Load(db, loadoutId);
        _loadoutTitle = loadout.IsValid() ? $"{loadout.Name} ({loadout.InstallationInstance.Game.DisplayName})" : "this loadout";

        if (onlyFor is { } uid)
        {
            _onlyForName = NexusModsModPageMetadata.FindByUid(db, uid).Select(static page => page.Name).FirstOrDefault() ?? $"mod {uid.ModId}";
            HeaderText = $"Patches & translations for {_onlyForName}";
            DescriptionText = $"Read from the \"Mods requiring this file\" list on {_onlyForName}'s Nexus Mods page and checked against the rest of this loadout. " +
                              "A patch is ready to use when everything it requires is installed.";
            TabTitle = $"Patches: {_onlyForName}";
        }
        else
        {
            HeaderText = Title;
            DescriptionText = "Patches and translations for the mods in this loadout, read from the \"Mods requiring this file\" list on each mod's Nexus Mods page. " +
                              "A patch is ready to use when everything it requires is installed.";
            TabTitle = Title;
        }

        TabIcon = IconValues.PuzzleCheckOutline;

        // Translations into the language the app is set to are the ones that matter, until the
        // user picks another; the others are a click away.
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        var culture = settingsManager.Get<LanguageSettings>().UICulture;
        _appLanguage = new PatchFinderLanguageOption(culture.TwoLetterISOLanguageName, LanguageName(culture.TwoLetterISOLanguageName, culture.EnglishName));

        var settings = settingsManager.Get<PatchFinderSettings>();
        var initialLanguage = settings.TranslationLanguage switch
        {
            null => _appLanguage,
            PatchFinderSettings.AllLanguages => AllLanguages,
            var code when code == _appLanguage.Code => _appLanguage,
            var code => new PatchFinderLanguageOption(code, LanguageName(code, code)),
        };

        Languages = new[] { AllLanguages, _appLanguage, initialLanguage }.Distinct().ToArray();
        SelectedLanguage = initialLanguage;
        ShowPatchesNeedingOtherMods = settings.ShowPatchesNeedingOtherMods;

        CommandRescan = new R3.ReactiveCommand(
            executeAsync: async (_, cancellationToken) => await Scan(cancellationToken),
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandExport = new R3.ReactiveCommand(
            executeAsync: async (_, cancellationToken) => await Export(cancellationToken),
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(vm => vm.SelectedLanguage, vm => vm.ShowPatchesNeedingOtherMods)
                .Subscribe(_ => UpdateLists())
                .DisposeWith(disposables);

            // Remembered for next time, once the user changes something: until then the filter
            // keeps following the app's language. The selection is also briefly null while the
            // language list is replaced, which is not a choice the user made.
            this.WhenAnyValue(vm => vm.SelectedLanguage, vm => vm.ShowPatchesNeedingOtherMods)
                .Skip(1)
                .Subscribe(tuple =>
                {
                    var (language, showAll) = tuple;
                    if (language is null) return;

                    settingsManager.Update<PatchFinderSettings>(current => current with
                    {
                        TranslationLanguage = language.Code ?? PatchFinderSettings.AllLanguages,
                        ShowPatchesNeedingOtherMods = showAll,
                    });
                })
                .DisposeWith(disposables);

            // Once per page, not every time the tab comes back into view: the lists change
            // slowly, and there is a button for when the user wants them read again.
            if (_hasScanned) return;
            _hasScanned = true;
            CommandRescan.Execute(Unit.Default);
        });
    }

    private async ValueTask Scan(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsScanning = true;
            CanExport = false;
            Progress = 0;
            StatusText = "Reading the \"Mods requiring this file\" lists on Nexus Mods…";
        });

        try
        {
            var progress = new DispatcherProgress(this);
            var result = await _patchFinder.Find(_loadoutId, _onlyFor, progress, cancellationToken);
            Dispatcher.UIThread.Post(() => ShowResult(result));
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() => StatusText = "Stopped.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to look for patches and translations for loadout `{Loadout}`", _loadoutId);
            Dispatcher.UIThread.Post(() => StatusText = $"Couldn't read the lists from Nexus Mods: {exception.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsScanning = false;
                CanExport = _result is not null;
            });
        }
    }

    private void ReportProgress(PatchFinderProgress progress)
    {
        Progress = progress.Total == 0 ? 100 : 100.0 * progress.Done / progress.Total;
        StatusText = progress.Stage switch
        {
            PatchFinderStage.ReadingDependents => $"Reading the \"Mods requiring this file\" lists on Nexus Mods… {Progress:0}%",
            PatchFinderStage.CheckingCategories => $"Checking which of {progress.Total} mods that build on several of yours are filed as patches: {progress.Done} of {progress.Total}",
            PatchFinderStage.CheckingCandidates => $"Checking what {progress.Total} possible patches and translations require: {progress.Done} of {progress.Total}",
            _ => StatusText,
        };
    }

    private void ShowResult(PatchFinderResult result)
    {
        _result = result;
        Progress = 100;

        // Replacing the list clears the selection on the way, so remember it first. It stays on
        // the list even when nothing was found in it, rather than quietly turning into another.
        var selected = SelectedLanguage;
        var selectedCode = selected?.Code;

        var foundLanguages = result.Matches
            .Select(static match => match.Language)
            .OfType<ModLanguage>()
            .Select(static language => new PatchFinderLanguageOption(language.Code, language.Name))
            .Append(_appLanguage)
            .Concat(selected is { Code: not null } ? [selected] : [])
            .DistinctBy(static option => option.Code)
            .OrderBy(static option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        Languages = [AllLanguages, ..foundLanguages];
        SelectedLanguage = Languages.FirstOrDefault(option => option.Code == selectedCode) ?? AllLanguages;

        UpdateLists();
        StatusText = Summarize(result);
    }

    private void UpdateLists()
    {
        if (_result is null)
        {
            Patches = [];
            Translations = [];
            PatchesEmptyText = null;
            TranslationsEmptyText = null;
            return;
        }

        var patches = _result.Matches.Where(static match => match.Kind == DependentKind.Patch).ToArray();
        var readyPatches = patches.Where(static match => match.HasAllRequirements).ToArray();
        var shownPatches = ShowPatchesNeedingOtherMods ? patches : readyPatches;

        Patches = shownPatches.Select(CreateItem).ToArray();
        PatchesEmptyText = shownPatches.Length > 0
            ? null
            : patches.Length > 0
                ? $"None of the patches found has all its requirements installed. {patches.Length} more need mods you don't have."
                : "No patches found for your mods.";

        // Null for a moment while the language list is being replaced.
        var language = SelectedLanguage ?? AllLanguages;

        var translations = _result.Matches.Where(static match => match.Kind == DependentKind.Translation).ToArray();
        var shownTranslations = language.Code is null
            ? translations
            : translations.Where(match => match.Language?.Code == language.Code).ToArray();

        Translations = shownTranslations.Select(CreateItem).ToArray();
        TranslationsEmptyText = shownTranslations.Length > 0
            ? null
            : translations.Length > 0
                ? $"No {language.DisplayName} translations found. Pick \"{AllLanguages.DisplayName}\" to see the other {translations.Length}."
                : "No translations found for your mods.";
    }

    private string Summarize(PatchFinderResult result)
    {
        if (result.ModsChecked == 0)
        {
            return _onlyFor is null
                ? "None of the mods in this loadout came from Nexus Mods, so there are no lists to read."
                : $"{_onlyForName} is no longer in this loadout.";
        }

        var patches = result.Matches.Count(static match => match.Kind == DependentKind.Patch);
        var readyPatches = result.Matches.Count(static match => match.Kind == DependentKind.Patch && match.HasAllRequirements);
        var translations = result.Matches.Count(static match => match.Kind == DependentKind.Translation);

        var summary = _onlyFor is null
            ? $"Checked {result.ModsChecked} mods from Nexus Mods: {Plural(readyPatches, "patch", "patches")} ready to use"
            : $"Checked {_onlyForName}: {Plural(readyPatches, "patch", "patches")} ready to use";
        if (patches > readyPatches) summary += $" ({patches - readyPatches} more need mods you don't have)";
        summary += $", {Plural(translations, "translation", "translations")}";
        if (result.AlreadyInstalled > 0) summary += $", and {result.AlreadyInstalled} you already have";
        summary += ".";

        if (result.FailedRequests > 0)
            summary += $" {Plural(result.FailedRequests, "request", "requests")} to Nexus Mods failed, so some may be missing.";

        if (result.Truncated.Length > 0)
            summary += $" Only part of the list was read for {JoinNames(result.Truncated)}, as thousands of mods require them.";

        return summary;
    }

    private PatchFinderItemViewModel CreateItem(PatchFinderMatch match)
    {
        return new PatchFinderItemViewModel(
            name: match.Name,
            forText: match.ForInstalledMods.Length > 0 ? $"For {JoinNames(match.ForInstalledMods)}" : string.Empty,
            missingText: match.HasAllRequirements ? null : $"Also needs {JoinNames(match.MissingRequirements)}",
            languageText: match.Kind == DependentKind.Translation ? match.Language?.Name ?? "Unknown language" : null,
            open: () => OpenPage(match)
        );
    }

    /// <summary>
    /// Saves what is on the page as a mod list the loadout's Import can download from, which also
    /// reads as a list of links.
    /// </summary>
    /// <remarks>
    /// Follows the page's filters, so the file holds what the user is looking at: the same
    /// language, and patches for mods they don't have only when those are shown.
    /// </remarks>
    private async ValueTask Export(CancellationToken cancellationToken)
    {
        var result = _result;
        if (result is null) return;

        var language = SelectedLanguage ?? AllLanguages;
        var includeAll = ShowPatchesNeedingOtherMods;
        var title = _onlyFor is null ? _loadoutTitle : $"{_onlyForName} in {_loadoutTitle}";

        var path = await _serviceProvider.GetRequiredService<IAvaloniaInterop>().SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the patches and translations as a mod list",
            SuggestedFileName = $"{ToFileName(_onlyFor is null ? _loadoutTitle : _onlyForName!)} - patches and translations.txt",
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Mod Organizer mod list") { Patterns = ["*.txt"] },
            ],
        });

        if (path is null) return;

        var notifications = _serviceProvider.GetRequiredService<IWindowNotificationService>();

        try
        {
            var report = PatchFinderModList.Write(result, new PatchFinderModListOptions
            {
                Title = title,
                IncludePatchesNeedingOtherMods = includeAll,
                TranslationLanguage = language.Code is null ? null : new ModLanguage(language.Code, language.DisplayName),
            });

            await path.Value.WriteAllTextAsync(report, cancellationToken);
            notifications.ShowToast($"Saved the list to {path.Value}. Import it from the loadout's Import button to download and install them.", ToastNotificationVariant.Success);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save the list of patches and translations to `{Path}`", path.Value);
            notifications.ShowToast($"Couldn't save the list: {exception.Message}", ToastNotificationVariant.Failure);
        }
    }

    /// <summary>
    /// Leaves out what a file name can't hold on any of the systems the app runs on.
    /// </summary>
    private static string ToFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
    }

    private void OpenPage(PatchFinderMatch match)
    {
        // In a tab, like every other mod page the app opens, so the user can read it and
        // download from it without leaving the list.
        var inAppBrowser = _serviceProvider.GetService<IInAppBrowser>();
        if (inAppBrowser?.TryOpen(match.Uri, match.Name) == true) return;

        _serviceProvider.GetRequiredService<IOSInterop>().OpenUri(match.Uri);
    }

    private static string LanguageName(string code, string fallback)
    {
        return DependentClassifier.KnownLanguages.FirstOrDefault(language => language.Code == code)?.Name ?? fallback;
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count <= MaxNamesPerRow) return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(MaxNamesPerRow))} and {names.Count - MaxNamesPerRow} more";
    }

    private static string Plural(int count, string singular, string plural) => $"{count} {(count == 1 ? singular : plural)}";

    /// <summary>
    /// Hands progress from the scan's threads to the UI thread.
    /// </summary>
    private sealed class DispatcherProgress(PatchFinderViewModel viewModel) : IProgress<PatchFinderProgress>
    {
        public void Report(PatchFinderProgress value) => Dispatcher.UIThread.Post(() => viewModel.ReportProgress(value));
    }
}

/// <summary>
/// One patch or translation on the page.
/// </summary>
public sealed class PatchFinderItemViewModel : IPatchFinderItemViewModel
{
    public string Name { get; }
    public string ForText { get; }
    public string? MissingText { get; }
    public string? LanguageText { get; }

    public R3.ReactiveCommand<Unit> CommandOpen { get; }

    public PatchFinderItemViewModel(string name, string forText, string? missingText, string? languageText, Action open)
    {
        Name = name;
        ForText = forText;
        MissingText = missingText;
        LanguageText = languageText;
        CommandOpen = new R3.ReactiveCommand(_ => open());
    }
}
