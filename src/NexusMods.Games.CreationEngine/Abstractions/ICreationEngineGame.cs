using NexusMods.Abstractions.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;


using NexusMods.Sdk.Games;

namespace NexusMods.Games.CreationEngine.Abstractions;

/// <summary>
/// How a game marks a plugin as active in its plugins file.
/// </summary>
public enum PluginsFileFormat
{
    /// <summary>
    /// The file lists every known plugin and an asterisk prefix marks the active ones; the
    /// order of the lines is the load order. Used by Skyrim SE and Fallout 4.
    /// </summary>
    Asterisk,

    /// <summary>
    /// The file lists only the active plugins, with no prefix. Used by the older Gamebryo
    /// titles (Oblivion, Fallout 3, Fallout: New Vegas), which take their load order from
    /// the plugins' file modification times rather than from this file.
    /// </summary>
    ActiveOnly,
}

/// <summary>
/// A common interface for all games that use the creation engine.
/// </summary>
public interface ICreationEngineGame : IGame
{
    public GamePath PluginsFile { get; }

    /// <summary>
    /// The format of <see cref="PluginsFile"/>. Defaults to the modern asterisk format.
    /// </summary>
    public PluginsFileFormat PluginsFileFormat => PluginsFileFormat.Asterisk;

    /// <summary>
    /// The file extensions this game loads as plugins. Defaults to all three Creation Engine
    /// extensions; the pre-Fallout 4 engines have no light plugins and override this.
    /// </summary>
    public Extension[] PluginExtensions => KnownCEExtensions.PluginFiles;

    /// <summary>
    /// Parse a plugin file. Currently, this loads only the header of the file, in the future
    /// we can add flags to load specific groups
    /// </summary>
    public ValueTask<PluginHeader?> ParsePlugin(Hash hash, RelativePath? name = null);
}
