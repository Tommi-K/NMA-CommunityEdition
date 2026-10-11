using JetBrains.Annotations;

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
    /// A regular mod.
    /// </summary>
    public static ModListEntry Mod(string name, bool isEnabled) => new(name, isEnabled, ModListEntryKind.Mod);

    /// <summary>
    /// A separator label. Separators carry no enabled state of their own.
    /// </summary>
    public static ModListEntry Separator(string name) => new(name, IsEnabled: true, ModListEntryKind.Separator);
}
