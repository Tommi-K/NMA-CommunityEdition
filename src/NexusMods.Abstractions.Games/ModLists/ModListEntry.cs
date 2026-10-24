using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// What a line in a <c>modlist.txt</c> refers to.
/// </summary>
[PublicAPI]
public enum ModListEntryKind
{
    /// <summary>
    /// A regular mod, written as <c>+Name</c> or <c>-Name</c>.
    /// </summary>
    Mod,

    /// <summary>
    /// A separator: a label the user dropped into the list to group the mods under it.
    /// Mod Organizer names these <c>&lt;label&gt;_separator</c>.
    /// </summary>
    Separator,

    /// <summary>
    /// Content Mod Organizer sees but does not manage, such as the game's own DLC, written as <c>*Name</c>.
    /// </summary>
    Unmanaged,
}

/// <summary>
/// A single line of a <c>modlist.txt</c>.
/// </summary>
[PublicAPI]
public readonly record struct ModListEntry(string Name, bool IsEnabled, ModListEntryKind Kind)
{
    /// <summary>
    /// Where on Nexus Mods the mod can be downloaded from, when the file says. Mod Organizer
    /// itself never writes this; see <see cref="ModListNexusSource"/>.
    /// </summary>
    public ModListNexusSource? Nexus { get; init; }

    /// <summary>
    /// Free text written as comment lines above the entry, for people reading the file. Not read back.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// A regular mod.
    /// </summary>
    public static ModListEntry Mod(string name, bool isEnabled) => new(name, isEnabled, ModListEntryKind.Mod);

    /// <summary>
    /// A separator label. Separators carry no enabled state of their own.
    /// </summary>
    public static ModListEntry Separator(string name) => new(name, IsEnabled: true, ModListEntryKind.Separator);
}

/// <summary>
/// The mod page, and possibly the exact file, a <c>modlist.txt</c> entry comes from.
/// </summary>
/// <remarks>
/// Written as a comment line just above the entry it belongs to, such as
/// <c>#nexusmods:skyrimspecialedition/12604/35407</c>. Mod Organizer skips comment lines, so a
/// file carrying these still opens there; the app reads them back to download mods the loadout
/// doesn't have yet.
/// </remarks>
/// <param name="GameDomain">The site's name for the game the page is under, such as <c>skyrimspecialedition</c>.</param>
/// <param name="ModId">The mod page.</param>
/// <param name="FileId">The file on it, or null to take the page's main file.</param>
[PublicAPI]
public readonly record struct ModListNexusSource(GameDomain GameDomain, ModId ModId, FileId? FileId)
{
    /// <summary>
    /// What the comment line starts with.
    /// </summary>
    public const string CommentPrefix = "#nexusmods:";

    /// <summary>
    /// The comment line for this source.
    /// </summary>
    public string ToCommentLine() => FileId is { } fileId
        ? $"{CommentPrefix}{GameDomain}/{ModId}/{fileId}"
        : $"{CommentPrefix}{GameDomain}/{ModId}";

    /// <summary>
    /// Reads a line written by <see cref="ToCommentLine"/>.
    /// </summary>
    public static bool TryParseCommentLine(string line, [NotNullWhen(true)] out ModListNexusSource? source)
    {
        source = null;
        if (!line.StartsWith(CommentPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var parts = line[CommentPrefix.Length..].Trim().Split('/');
        if (parts.Length is < 2 or > 3) return false;

        var domain = parts[0].Trim();
        if (domain.Length == 0) return false;
        if (!uint.TryParse(parts[1], out var modId) || modId == 0) return false;

        FileId? fileId = null;
        if (parts.Length == 3)
        {
            if (!uint.TryParse(parts[2], out var parsedFileId) || parsedFileId == 0) return false;
            fileId = NexusMods.Sdk.NexusModsApi.FileId.From(parsedFileId);
        }

        source = new ModListNexusSource(GameDomain.From(domain), NexusMods.Sdk.NexusModsApi.ModId.From(modId), fileId);
        return true;
    }
}
