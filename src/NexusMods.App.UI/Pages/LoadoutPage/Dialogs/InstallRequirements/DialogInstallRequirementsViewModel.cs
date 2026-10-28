using System.Reactive.Linq;
using NexusMods.Networking.NexusWebApi.Requirements;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.UI.Sdk;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Unit = R3.Unit;

namespace NexusMods.App.UI.Pages.LoadoutPage.Dialogs.InstallRequirements;

public interface IDialogInstallRequirementsViewModel : IViewModelInterface
{
    /// <summary>
    /// What was found, in a sentence or two.
    /// </summary>
    string SummaryText { get; }

    /// <summary>
    /// The requirements the app can see to - download, copy out of a switched-off collection, or
    /// switch back on - each with a box to tick.
    /// </summary>
    IReadOnlyList<RequirementItemViewModel> ToInstall { get; }

    /// <summary>
    /// The requirements the app can't install, for the user to see to.
    /// </summary>
    IReadOnlyList<RequirementItemViewModel> ToCheck { get; }

    /// <summary>
    /// What happens to the ticked mods.
    /// </summary>
    string FootnoteText { get; }

    /// <summary>
    /// How many mods are ticked and needed, which is how many an install fetches.
    /// </summary>
    int SelectedCount { get; }
}

/// <summary>
/// Lists what some mods require from Nexus Mods and lets the user pick which of the missing ones to
/// install.
/// </summary>
/// <remarks>
/// Authors put optional mods on their "Requirements" lists as well as the ones their mod can't do
/// without, and patch hubs list every mod they have a patch for. So the list starts out with only
/// what is firmly needed ticked, ticking a mod ticks what it firmly needs in turn, and a mod that
/// only unticked mods need is greyed out and left alone.
/// </remarks>
public sealed class DialogInstallRequirementsViewModel : AViewModel<IDialogInstallRequirementsViewModel>, IDialogInstallRequirementsViewModel
{
    // How many mods a row names before summing up the rest.
    private const int MaxNamesPerRow = 3;

    private readonly HashSet<ModUid> _roots;
    private readonly bool _targetIsOn;
    private bool _isUpdating;

    public string SummaryText { get; }
    public IReadOnlyList<RequirementItemViewModel> ToInstall { get; }
    public IReadOnlyList<RequirementItemViewModel> ToCheck { get; }
    public string FootnoteText { get; }

    [Reactive] public int SelectedCount { get; private set; }

    /// <param name="result">What the finder found.</param>
    /// <param name="subject">The mods looked up, as the start of a sentence.</param>
    /// <param name="targetName">The collection new mods go into, when known.</param>
    /// <param name="targetIsOn">Whether that collection is on, and so whether new mods in it do anything.</param>
    /// <param name="openUri">Opens a requirement's page.</param>
    public DialogInstallRequirementsViewModel(RequirementFinderResult result, string subject, string? targetName, bool targetIsOn, Action<Uri> openUri)
    {
        _roots = result.ModsChecked.Select(static mod => mod.Uid).ToHashSet();
        _targetIsOn = targetIsOn;

        // Kept in the finder's order, which is the order to install in, and shown the other way
        // round: a mod first, then what it needs, which reads like the chain it is.
        var toInstall = result.Fixable.Select(requirement => new RequirementItemViewModel(requirement, openUri)).ToArray();
        ToInstall = toInstall.Reverse().ToArray();

        ToCheck = result.Requirements
            .Where(static requirement => !requirement.IsFixable)
            .Select(requirement => new RequirementItemViewModel(requirement, openUri))
            .ToArray();

        SelectDefaults();
        UpdateNeeded();

        foreach (var item in ToInstall)
        {
            item.WhenAnyValue(static x => x.IsSelected)
                .Skip(1)
                .Subscribe(isSelected => OnSelectionChanged(item, isSelected));
        }

        // Left unticked for looking optional, or for being needed only by mods that do; the copies
        // left unticked for want of a collection that is on are explained on their own.
        var optionalCount = ToInstall.Count(item => !item.IsSelected && MayTickByItself(item));
        var missingCount = ToInstall.Count(static item => item.Requirement.State == RequirementState.Missing);
        var offCount = ToInstall.Count - missingCount;
        var verb = result.ModsChecked.Length == 1 ? "requires" : "require";

        var summary = (missingCount, offCount) switch
        {
            (0, 0) => $"Everything {(result.ModsChecked.Length == 1 ? $"{subject} requires" : "these mods require")} from Nexus Mods is installed, but some requirements need your attention.",
            (_, 0) => $"{subject} {verb} {Plural(missingCount, "mod")} from Nexus Mods that {(missingCount == 1 ? "isn't" : "aren't")} in this loadout.",
            (0, _) => $"{subject} {verb} {Plural(offCount, "mod")} that this loadout has but {(offCount == 1 ? "is" : "are")} switched off.",
            _ => $"{subject} {verb} {Plural(ToInstall.Count, "mod")} this loadout lacks: {missingCount} not installed and {offCount} switched off.",
        };

        if (optionalCount > 0)
            summary += $" {(optionalCount == 1 ? "One is" : $"{optionalCount} are")} left unticked: optional from what the authors wrote, or only needed by optional ones.";

        SummaryText = summary;

        var footnotes = new List<string>();
        if (ToInstall.Count > 0)
        {
            var into = targetName is null ? string.Empty : $" into “{targetName}”";
            footnotes.Add($"New mods are downloaded, or copied from your library when a switched-off collection has them, and go{into} below your other mods so those keep winning file conflicts. " +
                          "Mods with installer options will ask for them. A mod that only unticked mods need is greyed out and skipped.");
        }

        if (!targetIsOn && ToInstall.Any(static item => item.Requirement.State != RequirementState.SwitchedOff))
        {
            footnotes.Add($"“{targetName}” is switched off, like all your own collections, so the new mods stay off until you switch it on. " +
                          "For a mod that only a switched-off collection has, switching that collection back on is the better fix, so those are left unticked.");
        }

        if (ToInstall.Any(static item => item.Requirement.State == RequirementState.InDisabledCollection))
        {
            footnotes.Add("A copy taken from a switched-off collection switches itself off whenever another copy of the same file is on, such as when you switch the collection back on, " +
                          "and back on when that one goes off, so you don't end up with both.");
        }

        if (result.FailedRequests > 0)
            footnotes.Add($"{Plural(result.FailedRequests, "request")} to Nexus Mods failed, so some requirements may be missing from this list.");

        if (result.Truncated)
            footnotes.Add("Some optional requirements were left out, as there were too many to list.");

        FootnoteText = string.Join("\n\n", footnotes);
    }

    /// <summary>
    /// The ticked mods that something still needs, in the order to install them.
    /// </summary>
    public FoundRequirement[] GetSelected()
    {
        return ToInstall
            .Where(static item => item is { IsSelected: true, IsNeeded: true })
            .Select(static item => item.Requirement)
            .Reverse()
            .ToArray();
    }

    /// <summary>
    /// Ticks what the mods looked up firmly need, then what those firmly need, and so on.
    /// </summary>
    private void SelectDefaults()
    {
        var active = new HashSet<ModUid>(_roots);
        bool changed;

        do
        {
            changed = false;

            foreach (var item in ToInstall)
            {
                if (item.IsSelected || !MayTickByItself(item)) continue;
                if (!item.Requirement.RequiredBy.Any(link => !link.IsOptional && active.Contains(link.Mod.Uid))) continue;

                // One without a page on Nexus Mods has no list of its own to need anything through.
                item.IsSelected = true;
                if (item.Requirement.Uid is { } uid) active.Add(uid);
                changed = true;
            }
        } while (changed);
    }

    private void OnSelectionChanged(RequirementItemViewModel changedItem, bool isSelected)
    {
        if (_isUpdating) return;
        _isUpdating = true;

        try
        {
            // Ticking a mod brings along what it can't do without, and what those can't do without.
            if (isSelected && changedItem.Requirement.Uid is { } uid)
            {
                var pending = new Queue<ModUid>([uid]);
                while (pending.TryDequeue(out var requirer))
                {
                    foreach (var item in ToInstall)
                    {
                        if (item.IsSelected || !MayTickByItself(item)) continue;
                        if (!item.Requirement.RequiredBy.Any(link => !link.IsOptional && link.Mod.Uid == requirer)) continue;

                        item.IsSelected = true;
                        if (item.Requirement.Uid is { } itemUid) pending.Enqueue(itemUid);
                    }
                }
            }

            UpdateNeeded();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Whether the dialog may tick a mod for the user, as something a ticked mod needs. A copy that
    /// would go into a switched-off collection does nothing, so that one is the user's call.
    /// </summary>
    private bool MayTickByItself(RequirementItemViewModel item)
    {
        return _targetIsOn || item.Requirement.State != RequirementState.InDisabledCollection;
    }

    /// <summary>
    /// Greys out the mods that only unticked mods need.
    /// </summary>
    private void UpdateNeeded()
    {
        var active = new HashSet<ModUid>(_roots);
        var needed = new HashSet<RequirementItemViewModel>();
        bool changed;

        do
        {
            changed = false;

            foreach (var item in ToInstall)
            {
                if (needed.Contains(item)) continue;
                if (!item.Requirement.RequiredBy.Any(link => active.Contains(link.Mod.Uid))) continue;

                needed.Add(item);
                if (item.IsSelected && item.Requirement.Uid is { } uid) active.Add(uid);
                changed = true;
            }
        } while (changed);

        foreach (var item in ToInstall) item.IsNeeded = needed.Contains(item);
        SelectedCount = ToInstall.Count(static item => item is { IsSelected: true, IsNeeded: true });
    }

    internal static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count <= MaxNamesPerRow) return string.Join(", ", names);
        return $"{string.Join(", ", names.Take(MaxNamesPerRow))} and {names.Count - MaxNamesPerRow} more";
    }

    private static string Plural(int count, string noun) => $"{count} {(count == 1 ? noun : noun + "s")}";
}

/// <summary>
/// One requirement on the list.
/// </summary>
public sealed class RequirementItemViewModel : ReactiveObject
{
    public FoundRequirement Requirement { get; }

    public string Name { get; }

    /// <summary>
    /// Which mods need it, and what their authors wrote about it.
    /// </summary>
    public string DetailText { get; }

    /// <summary>
    /// What ticking it does, for one that is installed, or what the user has to do about one the app
    /// can't see to.
    /// </summary>
    public string? StatusText { get; }

    public bool HasUri { get; }

    [Reactive] public bool IsSelected { get; set; }

    /// <summary>
    /// False when only mods the user unticked need it, so it won't be installed.
    /// </summary>
    [Reactive] public bool IsNeeded { get; set; } = true;

    public R3.ReactiveCommand<Unit> CommandOpen { get; }

    public RequirementItemViewModel(FoundRequirement requirement, Action<Uri> openUri)
    {
        Requirement = requirement;
        Name = requirement.Name;
        HasUri = requirement.Uri is not null;

        var requirers = requirement.RequiredBy.Select(static link => link.Mod.Name).ToArray();
        var notes = requirement.RequiredBy
            .Select(static link => link.Notes?.Trim())
            .Where(static notes => !string.IsNullOrEmpty(notes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DetailText = (requirement.IsOptional ? "Optional for " : "Needed by ") + DialogInstallRequirementsViewModel.JoinNames(requirers)
                     + (notes.Length > 0 ? $" · “{string.Join("” · “", notes)}”" : string.Empty);

        StatusText = requirement.State switch
        {
            RequirementState.OffSite => "Not on Nexus Mods: make sure you have it.",
            RequirementState.Unavailable => $"Can't be installed: {requirement.Reason}.",
            RequirementState.SwitchedOff => "Installed, but switched off. Ticking switches it back on.",
            RequirementState.InDisabledCollection =>
                $"Only in “{requirement.CollectionName}”, which is switched off. Ticking installs a copy of “{requirement.FileName}” from your library, without downloading it again.",
            _ => null,
        };

        CommandOpen = new R3.ReactiveCommand<Unit>(_ =>
        {
            if (requirement.Uri is { } uri) openUri(uri);
        });
    }
}
