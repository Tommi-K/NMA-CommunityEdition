using System.Globalization;
using JetBrains.Annotations;
using NexusMods.Abstractions.Games;

namespace NexusMods.Networking.NexusWebApi.Patches;

/// <summary>
/// What a <see cref="PatchFinderModList"/> covers.
/// </summary>
[PublicAPI]
public sealed record PatchFinderModListOptions
{
    /// <summary>
    /// What the list is for, such as the loadout and game.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Whether to list patches that also need mods the loadout doesn't have.
    /// </summary>
    public bool IncludePatchesNeedingOtherMods { get; init; }

    /// <summary>
    /// The language to list translations for, or null for every language.
    /// </summary>
    public ModLanguage? TranslationLanguage { get; init; }

    /// <summary>
    /// When the lists were read.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Writes what a <see cref="IPatchFinder"/> found as a Mod Organizer <c>modlist.txt</c>, so that
/// importing it into a loadout downloads and installs the lot.
/// </summary>
/// <remarks>
/// Each entry carries its Nexus Mods page in the comment line the import downloads from (see
/// <see cref="ModListNexusSource"/>), with no file, so the page's main file is taken. Above that go
/// the installed mods it is for and its address, as plain comments, which keeps the file readable
/// as a list of links. The sections are Mod Organizer separators, which the import skips.
/// <para/>
/// Anything that needs mods the loadout doesn't have is written switched off: importing it
/// installs it without loading a plugin whose masters are missing.
/// </remarks>
[PublicAPI]
public static class PatchFinderModList
{
    public static string Write(PatchFinderResult result, PatchFinderModListOptions options)
    {
        var entries = new List<ModListEntry>();
        var patches = result.Matches.Where(static match => match.Kind == DependentKind.Patch).ToArray();

        AddSection(entries, "Patches ready to use", patches.Where(static match => match.HasAllRequirements));

        if (options.IncludePatchesNeedingOtherMods)
            AddSection(entries, "Patches that need mods you don't have", patches.Where(static match => !match.HasAllRequirements));

        var translations = result.Matches
            .Where(match => match.Kind == DependentKind.Translation)
            .Where(match => options.TranslationLanguage is null || match.Language?.Code == options.TranslationLanguage.Code);

        AddSection(entries, options.TranslationLanguage is null ? "Translations" : $"{options.TranslationLanguage.Name} translations", translations);

        var comment = $"""
                       Patches and translations for {options.Title}, from the "Mods requiring this file" lists of
                       {(result.ModsChecked == 1 ? "1 mod" : $"{result.ModsChecked} mods")} on Nexus Mods, {options.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}.
                       Import this file from a loadout's Import button to download and install them.
                       """;

        if (result.FailedRequests > 0)
            comment += $"\n{result.FailedRequests} requests to Nexus Mods failed while making it, so it may be missing some.";

        return ModOrganizerModList.Write(entries, comment);
    }

    private static void AddSection(List<ModListEntry> entries, string heading, IEnumerable<PatchFinderMatch> matches)
    {
        // Grouped by the installed mods each is for, which is how the user goes through them:
        // what there is for this mod of theirs, or for this pair of them.
        var ordered = matches
            .OrderBy(static match => string.Join(" + ", match.ForInstalledMods), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static match => match.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        if (ordered.Length == 0) return;

        entries.Add(ModListEntry.Separator($"{heading} ({ordered.Length}){ModOrganizerModList.SeparatorSuffix}"));

        foreach (var match in ordered)
        {
            var comment = new List<string>();
            if (match.ForInstalledMods.Length > 0) comment.Add($"For {string.Join(" + ", match.ForInstalledMods)}");
            if (match.Kind == DependentKind.Translation) comment.Add($"Language: {match.Language?.Name ?? "unknown"}");
            if (!match.HasAllRequirements) comment.Add($"Also needs: {string.Join(", ", match.MissingRequirements)}");

            // Without the app's tracking parameters: these are for people to read and share.
            comment.Add(match.Uri.GetLeftPart(UriPartial.Path));

            entries.Add(ModListEntry.Mod(match.Name, isEnabled: match.HasAllRequirements) with
            {
                Nexus = new ModListNexusSource(match.GameDomain, match.Uid.ModId, FileId: null),
                Comment = string.Join("\n", comment),
            });
        }
    }
}
