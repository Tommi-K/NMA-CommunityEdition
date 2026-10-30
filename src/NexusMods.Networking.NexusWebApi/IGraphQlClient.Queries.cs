using JetBrains.Annotations;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi.Errors;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi;

[PublicAPI]
public partial interface IGraphQlClient
{
    /// <summary>
    /// Queries all categories, including global categories, for a game.
    /// </summary>
    ValueTask<GraphQlResult<ICategory[], NotFound>> QueryGameCategories(
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all global categories.
    /// </summary>
    ValueTask<GraphQlResult<ICategory[]>> QueryGlobalCategories(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a presigned upload URL for collection revisions.
    /// </summary>
    ValueTask<GraphQlResult<PresignedUploadUrl>> RequestCollectionRevisionUploadUrl(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a presigned upload URL for media files.
    /// </summary>
    ValueTask<GraphQlResult<PresignedUploadUrl>> RequestMediaUploadUrl(
        string mimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the domain of a game by id.
    /// </summary>
    ValueTask<GraphQlResult<GameDomain, NotFound>> QueryGameDomain(
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the id of a game by domain.
    /// </summary>
    ValueTask<GraphQlResult<NexusModsGameId, NotFound>> QueryGameId(
        GameDomain domain,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a mod.
    /// </summary>
    ValueTask<GraphQlResult<IMod, NotFound>> QueryMod(
        ModId modId,
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries each of <paramref name="mods"/> in one request. Mods that don't exist are left out
    /// of the result, which is otherwise in no particular order.
    /// </summary>
    ValueTask<GraphQlResult<IMod[]>> QueryMods(
        IReadOnlyList<ModUid> mods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a single file.
    /// </summary>
    ValueTask<GraphQlResult<IModFile, NotFound>> QueryModFile(
        FileId fileId,
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all files of a mod.
    /// </summary>
    ValueTask<GraphQlResult<IModFile[], NotFound>> QueryModFiles(
        ModId modId,
        NexusModsGameId nexusModsGameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all files of each of <paramref name="mods"/> in one request, of which there can be
    /// at most <see cref="MaxModsPerFilesRequest"/>.
    /// </summary>
    /// <returns>
    /// The files of each mod, in the order of <paramref name="mods"/>. A mod that doesn't exist
    /// has none.
    /// </returns>
    ValueTask<GraphQlResult<IModFile[][]>> QueryModFilesOfMods(
        IReadOnlyList<ModUid> mods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The most mods <see cref="QueryModFilesOfMods"/> takes.
    /// </summary>
    const int MaxModsPerFilesRequest = 10;

    /// <summary>
    /// Queries the ID of a collection by slug.
    /// </summary>
    ValueTask<GraphQlResult<CollectionId, NotFound>> QueryCollectionId(
        CollectionSlug slug,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Queries a collection revision.
    /// </summary>
    ValueTask<GraphQlResult<ICollectionRevision, NotFound>> QueryCollectionRevision(
        CollectionSlug collectionSlug,
        RevisionNumber revisionNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all revisions of a collection.
    /// </summary>
    ValueTask<GraphQlResult<(RevisionNumber Number, RevisionStatus Status)[], NotFound>> QueryCollectionRevisionNumbers(
        CollectionSlug collectionSlug,
        GameDomain gameDomain,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the download link for a revision.
    /// </summary>
    ValueTask<GraphQlResult<string, NotFound>> QueryCollectionRevisionDownloadLink(
        CollectionSlug collectionSlug,
        RevisionNumber revisionNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries one page of the "mods requiring this file" list of each of <paramref name="mods"/>.
    /// </summary>
    /// <remarks>
    /// The same page is read for every mod, and a mod the site doesn't return is left out
    /// of the result rather than reported as an error.
    /// </remarks>
    ValueTask<GraphQlResult<ModDependentsPage[]>> QueryModsRequiring(
        IReadOnlyList<ModUid> mods,
        int offset,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries which files a mod page has, and how each is filed.
    /// </summary>
    ValueTask<GraphQlResult<ModFileSummary[]>> QueryModFileSummaries(
        ModUid mod,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the category of each of <paramref name="mods"/>.
    /// </summary>
    ValueTask<GraphQlResult<ModCategoryAssignment[]>> QueryModCategories(
        IReadOnlyList<ModUid> mods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the name, status, category and Nexus Mods requirements of each of <paramref name="mods"/>.
    /// </summary>
    ValueTask<GraphQlResult<ModRequirementDetails[]>> QueryModRequirementDetails(
        IReadOnlyList<ModUid> mods,
        int requirementCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the name, status and whole "Requirements" list of each of <paramref name="mods"/>,
    /// including the requirements hosted elsewhere.
    /// </summary>
    ValueTask<GraphQlResult<ModRequirementList[]>> QueryModRequirements(
        IReadOnlyList<ModUid> mods,
        int requirementCount,
        CancellationToken cancellationToken = default);
}
