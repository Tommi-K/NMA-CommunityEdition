using System.Text;
using JetBrains.Annotations;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// Reads and writes Mod Organizer 2's <c>plugins.txt</c>: the game's load order, one plugin file
/// name per line, the plugin that loads first at the top.
/// </summary>
/// <remarks>
/// Fallout New Vegas and Skyrim list plugin names bare, while Skyrim Special Edition and
/// Fallout 4 mark the enabled ones with a leading <c>*</c>. <see cref="Parse"/> accepts both and
/// <see cref="Write"/> emits the bare form, which every one of those games reads.
/// </remarks>
[PublicAPI]
public static class ModOrganizerPluginList
{
    /// <summary>
    /// The name Mod Organizer gives the file.
    /// </summary>
    public const string FileName = "plugins.txt";

    /// <summary>
    /// The comment Mod Organizer puts at the top of the file.
    /// </summary>
    public const string Header = ModOrganizerModList.Header;

    /// <summary>
    /// Parses the contents of a <c>plugins.txt</c> into plugin file names, in load order.
    /// </summary>
    public static List<string> Parse(string contents)
    {
        var plugins = new List<string>();

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // A leading `*` marks the plugin as enabled in the games that use that dialect. A
            // plugin absent from the file is the disabled state, so there is nothing to carry over.
            if (line[0] == '*') line = line[1..].Trim();
            if (line.Length == 0) continue;

            plugins.Add(line);
        }

        return plugins;
    }

    /// <summary>
    /// Writes the plugin file names as a <c>plugins.txt</c>, expecting them in load order.
    /// </summary>
    public static string Write(IEnumerable<string> pluginFileNames)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        foreach (var pluginFileName in pluginFileNames)
            sb.Append(pluginFileName).Append('\n');

        return sb.ToString();
    }
}
