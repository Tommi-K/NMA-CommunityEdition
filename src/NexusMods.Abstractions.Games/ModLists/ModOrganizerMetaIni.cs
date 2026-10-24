using JetBrains.Annotations;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Paths;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Reads where a mod came from out of the <c>meta.ini</c> Mod Organizer 2 keeps in each mod's
/// folder.
/// </summary>
/// <remarks>
/// A <c>modlist.txt</c> holds only folder names, but Mod Organizer records the Nexus Mods page and
/// file every mod was installed from next to it:
/// <code>
/// [General]
/// gameName=SkyrimSE
/// modid=12604
///
/// [installedFiles]
/// 1\modid=12604
/// 1\fileid=35407
/// </code>
/// The profile's list sits at <c>&lt;instance&gt;/profiles/&lt;profile&gt;/modlist.txt</c> and the
/// mods at <c>&lt;instance&gt;/mods/&lt;name&gt;/meta.ini</c>, so the one can be found from the other.
/// </remarks>
[PublicAPI]
public static class ModOrganizerMetaIni
{
    /// <summary>
    /// The name Mod Organizer gives the file.
    /// </summary>
    public const string FileName = "meta.ini";

    /// <summary>
    /// Mod Organizer's names for its games, mapped to the site's. Only the ones that differ, or
    /// that one game shares with another, need to be here; any other name is tried as it is.
    /// </summary>
    private static readonly Dictionary<string, string> GameDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SkyrimSE"] = "skyrimspecialedition",
        ["SkyrimVR"] = "skyrimspecialedition",
        ["Fallout4VR"] = "fallout4",
        ["FalloutNV"] = "newvegas",
        ["TTW"] = "newvegas",
        ["EnderalSE"] = "enderalspecialedition",
    };

    /// <summary>
    /// The folder holding the mods of the Mod Organizer instance a profile's <c>modlist.txt</c>
    /// belongs to, or null when the file doesn't sit in one.
    /// </summary>
    public static AbsolutePath? FindModsFolder(AbsolutePath modListPath)
    {
        // <instance>/profiles/<profile>/modlist.txt
        var profiles = modListPath.Parent.Parent;
        if (!profiles.FileName.ToString().Equals("profiles", StringComparison.OrdinalIgnoreCase)) return null;

        var mods = profiles.Parent / "mods";
        return mods.DirectoryExists() ? mods : null;
    }

    /// <summary>
    /// Reads the Nexus Mods page and file a mod came from, or null when it didn't come from there.
    /// </summary>
    /// <param name="contents">The contents of the mod's <c>meta.ini</c>.</param>
    /// <param name="fallbackDomain">The game to assume when the file doesn't name one this knows.</param>
    public static ModListNexusSource? ReadNexusSource(string contents, GameDomain fallbackDomain)
    {
        var values = Parse(contents);

        var gameName = values.GetValueOrDefault("General/gameName");
        var domain = string.IsNullOrWhiteSpace(gameName)
            ? fallbackDomain
            : GameDomain.From(GameDomains.GetValueOrDefault(gameName, gameName.ToLowerInvariant()));

        // The installed file says which mod and which file in one place, so it wins. Mod Organizer
        // writes -1 or 0 for anything it doesn't know.
        for (var index = 1; values.ContainsKey($"installedFiles/{index}\\modid"); index++)
        {
            if (!TryParseId(values.GetValueOrDefault($"installedFiles/{index}\\modid"), out var modId)) continue;

            var fileId = TryParseId(values.GetValueOrDefault($"installedFiles/{index}\\fileid"), out var parsedFileId)
                ? FileId.From(parsedFileId)
                : (FileId?)null;

            return new ModListNexusSource(domain, ModId.From(modId), fileId);
        }

        // Installed by hand from a download: the page is known, the file isn't.
        if (TryParseId(values.GetValueOrDefault("General/modid"), out var generalModId))
            return new ModListNexusSource(domain, ModId.From(generalModId), FileId: null);

        return null;
    }

    private static bool TryParseId(string? value, out uint id)
    {
        id = 0;
        return value is not null && uint.TryParse(value.Trim().Trim('"'), out id) && id > 0;
    }

    /// <summary>
    /// The file's values, keyed by <c>section/key</c>.
    /// </summary>
    private static Dictionary<string, string> Parse(string contents)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            values[$"{section}/{line[..separator].Trim()}"] = line[(separator + 1)..].Trim();
        }

        return values;
    }
}
