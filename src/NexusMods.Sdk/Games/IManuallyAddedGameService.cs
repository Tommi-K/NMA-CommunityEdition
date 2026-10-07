using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Outcome of trying to manually register a game installation.
/// </summary>
[PublicAPI]
public enum ManualGameAddStatus
{
    /// <summary>
    /// The folder was recognised as a supported game and has been registered.
    /// </summary>
    Added,

    /// <summary>
    /// The selected folder doesn't exist.
    /// </summary>
    DirectoryNotFound,

    /// <summary>
    /// None of the supported games could be found in the selected folder.
    /// </summary>
    NotRecognized,

    /// <summary>
    /// The game in this folder is already known to the app, either because it was
    /// added manually before or because one of the store locators already found it.
    /// </summary>
    AlreadyKnown,
}

/// <summary>
/// Result of <see cref="IManuallyAddedGameService.AddGame"/>.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Game">The recognised game, when one was recognised.</param>
/// <param name="DetectedVersion">Version read off the game's primary file, if it carries one.</param>
[PublicAPI]
public readonly record struct ManualGameAddResult(
    ManualGameAddStatus Status,
    IGameData? Game = null,
    string? DetectedVersion = null)
{
    /// <summary>
    /// Whether a new installation was registered.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Game))]
    public bool IsSuccess => Status == ManualGameAddStatus.Added;
}

/// <summary>
/// Registers game installations that the store locators didn't find, by pointing
/// the app at the game folder directly.
/// </summary>
[PublicAPI]
public interface IManuallyAddedGameService
{
    /// <summary>
    /// Works out which supported game is installed in <paramref name="directory"/> and registers it.
    /// </summary>
    /// <remarks>
    /// The game is identified by looking for its primary (executable) file, so the caller
    /// doesn't have to know which game the folder holds.
    /// </remarks>
    Task<ManualGameAddResult> AddGame(AbsolutePath directory);
}
