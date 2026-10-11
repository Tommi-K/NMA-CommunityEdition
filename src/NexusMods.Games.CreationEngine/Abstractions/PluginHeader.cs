using Mutagen.Bethesda.Plugins;

namespace NexusMods.Games.CreationEngine.Abstractions;

/// <summary>
/// The part of a plugin's header the app actually needs: what the plugin is called and
/// which other plugins it is built on top of.
/// </summary>
/// <remarks>
/// Deliberately not Mutagen's <c>IMod</c>. Mutagen only models the games it has record
/// definitions for - Oblivion, Skyrim, Fallout 4 and Starfield - and has no
/// <c>GameRelease</c> for Fallout 3 or New Vegas, so those games have to read their own
/// headers. <see cref="ModKey"/> itself is game-agnostic (it is a filename plus a type),
/// so it stays in use and the diagnostics keep rendering plugin names the same way.
/// </remarks>
public readonly record struct PluginHeader(ModKey ModKey, IReadOnlyList<ModKey> Masters);
