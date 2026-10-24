using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Telemetry;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Errors;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi.Patches;

/// <summary>
/// Finds patches and translations for the mods in a loadout.
/// </summary>
[PublicAPI]
public interface IPatchFinder
{
    /// <summary>
    /// Reads the "mods requiring this file" list of every Nexus Mods mod in the loadout and
    /// picks out the patches and translations that aren't installed yet.
    /// </summary>
    /// <param name="loadoutId">The loadout whose mods count as installed.</param>
    /// <param name="onlyFor">
    /// Reads only this mod's list instead of every one. What is found is still checked against
    /// the whole loadout, so a patch for it and another installed mod is ready to use.
    /// </param>
    /// <param name="progress">Told how far the search has got.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    ValueTask<PatchFinderResult> Find(
        LoadoutId loadoutId,
        ModUid? onlyFor = null,
        IProgress<PatchFinderProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// How far a <see cref="IPatchFinder.Find"/> has got.
/// </summary>
/// <param name="Stage">What it is doing.</param>
/// <param name="Done">How many items of the stage are finished.</param>
/// <param name="Total">How many items the stage has.</param>
[PublicAPI]
public readonly record struct PatchFinderProgress(PatchFinderStage Stage, int Done, int Total);

/// <summary>
/// The stages of a <see cref="IPatchFinder.Find"/>.
/// </summary>
[PublicAPI]
public enum PatchFinderStage
{
    /// <summary>
    /// Reading the "mods requiring this file" lists, counted in pages of them. The total grows
    /// once the first page of each list has said how long it is.
    /// </summary>
    ReadingDependents,

    /// <summary>
    /// Reading the categories of mods that build on several installed mods, to find patches whose
    /// names don't say so. Counted in mods.
    /// </summary>
    CheckingCategories,

    /// <summary>
    /// Looking up the requirements of the patches and translations found, counted in candidates.
    /// </summary>
    CheckingCandidates,
}

/// <summary>
/// What a <see cref="IPatchFinder.Find"/> found.
/// </summary>
[PublicAPI]
public sealed record PatchFinderResult
{
    /// <summary>
    /// How many installed mods from Nexus Mods had their list read: every one of them, or the
    /// one asked for.
    /// </summary>
    public required int ModsChecked { get; init; }

    /// <summary>
    /// The patches and translations that are not installed, best first.
    /// </summary>
    public required PatchFinderMatch[] Matches { get; init; }

    /// <summary>
    /// How many patches and translations were found that are already installed.
    /// </summary>
    public required int AlreadyInstalled { get; init; }

    /// <summary>
    /// Installed mods whose lists were too long to read to the end.
    /// </summary>
    public required string[] Truncated { get; init; }

    /// <summary>
    /// How many requests to Nexus Mods failed. Anything they would have found is missing.
    /// </summary>
    public required int FailedRequests { get; init; }
}

/// <summary>
/// A patch or translation for one or more installed mods.
/// </summary>
[PublicAPI]
public sealed record PatchFinderMatch
{
    /// <summary>
    /// The mod page.
    /// </summary>
    public required ModUid Uid { get; init; }

    /// <summary>
    /// Its name on the site.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Its page on the site.
    /// </summary>
    public required Uri Uri { get; init; }

    /// <summary>
    /// The site's name for the game it is under, as in its address.
    /// </summary>
    public required GameDomain GameDomain { get; init; }

    /// <summary>
    /// Whether it is a patch or a translation.
    /// </summary>
    public required DependentKind Kind { get; init; }

    /// <summary>
    /// The language of a translation, when its name says.
    /// </summary>
    public ModLanguage? Language { get; init; }

    /// <summary>
    /// The installed mods it requires, which are what it patches or translates.
    /// </summary>
    public required string[] ForInstalledMods { get; init; }

    /// <summary>
    /// The mods on Nexus Mods it requires that are not installed.
    /// </summary>
    public required string[] MissingRequirements { get; init; }

    /// <summary>
    /// Whether everything it requires from Nexus Mods is installed: a patch that is ready to use,
    /// rather than one for a combination of mods the loadout doesn't have.
    /// </summary>
    public bool HasAllRequirements => MissingRequirements.Length == 0;
}

/// <inheritdoc/>
internal sealed class PatchFinder : IPatchFinder
{
    // The API caps a single page of a requirement list at 80 entries.
    private const int DependentsPerPage = 80;

    // The API rejects a query whose cost passes 10,000, and a page of dependents costs about 5
    // per entry, so 20 mods at a time is as many as fits.
    private const int ModsPerDependentsRequest = 20;

    // Frameworks such as an unofficial patch or a UI library are required by thousands of mods.
    // Past this many the rest are skipped: a patch for two mods is on both of their lists, so it
    // is still found through the shorter one.
    private const int MaxDependentsPerMod = 6000;

    // A page of mods is capped at 80, and a category costs next to nothing.
    private const int ModsPerCategoryRequest = 80;

    // Kept under the cost limit too: about 6 per requirement plus a little per mod.
    private const int ModsPerDetailsRequest = 60;
    private const int RequirementsPerMod = 15;

    private const int MaxParallelRequests = 6;

    private readonly ILogger<PatchFinder> _logger;
    private readonly IGraphQlClient _graphQlClient;
    private readonly IConnection _connection;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;

    public PatchFinder(
        ILogger<PatchFinder> logger,
        IGraphQlClient graphQlClient,
        IConnection connection,
        IGameDomainToGameIdMappingCache mappingCache)
    {
        _logger = logger;
        _graphQlClient = graphQlClient;
        _connection = connection;
        _mappingCache = mappingCache;
    }

    /// <summary>
    /// A mod found on the list of one or more installed mods.
    /// </summary>
    private sealed class Dependent(string name)
    {
        public string Name { get; } = name;

        /// <summary>
        /// The installed mods whose lists it is on, which is to say the installed mods it requires.
        /// </summary>
        public HashSet<ModUid> RequiredInstalledMods { get; } = [];
    }

    public async ValueTask<PatchFinderResult> Find(LoadoutId loadoutId, ModUid? onlyFor, IProgress<PatchFinderProgress>? progress, CancellationToken cancellationToken)
    {
        var installed = GetInstalledModPages(_connection.Db, loadoutId);
        var failedRequests = 0;

        ModUid[] listsToRead = onlyFor is { } only
            ? installed.ContainsKey(only) ? [only] : []
            : installed.Keys.ToArray();

        _logger.LogInformation("Looking for patches and translations for {Count} of {Installed} mods from Nexus Mods", listsToRead.Length, installed.Count);

        var (dependents, truncated, failedDependentRequests) = await ReadDependents(listsToRead, progress, cancellationToken);
        failedRequests += failedDependentRequests;

        var alreadyInstalled = 0;
        var candidates = new List<ModUid>();
        var unnamedCandidates = new List<ModUid>();

        foreach (var (uid, dependent) in dependents)
        {
            var kind = DependentClassifier.Classify(dependent.Name).Kind;

            if (installed.ContainsKey(uid))
            {
                if (kind != DependentKind.None) alreadyInstalled++;
                continue;
            }

            if (kind != DependentKind.None) candidates.Add(uid);
            // With only one list read, every mod on it is on just that one, so the category is
            // all there is to go on - and one list is short enough to check all of.
            else if (onlyFor is not null || dependent.RequiredInstalledMods.Count >= 2) unnamedCandidates.Add(uid);
        }

        // A mod that builds on two installed mods may be a patch for them filed under its game's
        // patch category without saying so in its name. A large loadout has thousands of mods that
        // merely build on two of its frameworks, so only their categories are read for that, which
        // is a fraction of the cost of the full look-up below.
        var (categories, failedCategoryRequests) = await QueryInChunks(
            unnamedCandidates, ModsPerCategoryRequest, PatchFinderStage.CheckingCategories, progress,
            (chunk, token) => _graphQlClient.QueryModCategories(chunk, token), cancellationToken
        );
        failedRequests += failedCategoryRequests;

        candidates.AddRange(categories
            .Where(static category => DependentClassifier.Classify(string.Empty, category.CategoryName).Kind != DependentKind.None)
            .Select(static category => category.Uid)
        );

        var (details, failedDetailRequests) = await QueryInChunks(
            candidates, ModsPerDetailsRequest, PatchFinderStage.CheckingCandidates, progress,
            (chunk, token) => _graphQlClient.QueryModRequirementDetails(chunk, RequirementsPerMod, token), cancellationToken
        );
        failedRequests += failedDetailRequests;

        var domains = installed.Values
            .GroupBy(static page => page.Uid.GameId)
            .ToDictionary(static group => group.Key, static group => group.First().GameDomain);

        var matches = new List<PatchFinderMatch>();

        foreach (var detail in details)
        {
            // Pages that were taken down are still listed, but there is nothing to download.
            if (!detail.Status.Equals("published", StringComparison.OrdinalIgnoreCase)) continue;

            var classification = DependentClassifier.Classify(detail.Name, detail.CategoryName);
            if (classification.Kind == DependentKind.None) continue;

            if (!dependents.TryGetValue(detail.Uid, out var dependent)) continue;

            var requiredInstalled = new HashSet<ModUid>(dependent.RequiredInstalledMods);
            var missing = new List<string>();

            foreach (var requirement in detail.NexusRequirements)
            {
                if (installed.ContainsKey(requirement.Uid)) requiredInstalled.Add(requirement.Uid);
                else missing.Add(requirement.Name);
            }

            if (!TryGetModUri(detail.Uid, domains, out var uri, out var domain)) continue;

            matches.Add(new PatchFinderMatch
            {
                Uid = detail.Uid,
                Name = detail.Name,
                Uri = uri,
                GameDomain = domain,
                Kind = classification.Kind,
                Language = classification.Language,
                ForInstalledMods = requiredInstalled
                    .Select(required => installed[required].Name)
                    .Order(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                MissingRequirements = missing.Distinct().ToArray(),
            });
        }

        var ordered = matches
            .OrderBy(static match => match.Kind)
            .ThenByDescending(static match => match.HasAllRequirements)
            .ThenByDescending(static match => match.ForInstalledMods.Length)
            .ThenBy(static match => match.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        _logger.LogInformation(
            "Found {Patches} patches and {Translations} translations not yet installed ({Installed} already installed, {Failed} failed requests)",
            ordered.Count(static match => match.Kind == DependentKind.Patch),
            ordered.Count(static match => match.Kind == DependentKind.Translation),
            alreadyInstalled,
            failedRequests
        );

        return new PatchFinderResult
        {
            ModsChecked = listsToRead.Length,
            Matches = ordered,
            AlreadyInstalled = alreadyInstalled,
            Truncated = truncated.Select(uid => installed[uid].Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray(),
            FailedRequests = failedRequests,
        };
    }

    private sealed record InstalledModPage(ModUid Uid, string Name, GameDomain GameDomain);

    /// <summary>
    /// The mod pages the loadout's mods were downloaded from, enabled or not: a patch for a
    /// mod that is switched off is still a patch for a mod the user has.
    /// </summary>
    private static Dictionary<ModUid, InstalledModPage> GetInstalledModPages(IDb db, LoadoutId loadoutId)
    {
        var pages = new Dictionary<ModUid, InstalledModPage>();

        foreach (var item in LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemGroup().OfTypeLibraryLinkedLoadoutItem())
        {
            if (!item.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusModsLibraryItem)) continue;

            var page = nexusModsLibraryItem.ModPageMetadata;
            pages.TryAdd(page.Uid, new InstalledModPage(page.Uid, page.Name, page.GameDomain));
        }

        return pages;
    }

    /// <summary>
    /// Reads the "mods requiring this file" list of each of <paramref name="lists"/>.
    /// </summary>
    /// <remarks>
    /// The first page of every list comes first, since that is all most mods have and it says how
    /// long each list is. The pages after it are then all known up front and read side by side:
    /// the server takes about as long per entry however the entries are batched, so it is how
    /// many requests are in flight that decides how long a framework with thousands of
    /// dependents holds things up.
    /// </remarks>
    private async ValueTask<(Dictionary<ModUid, Dependent> Dependents, ModUid[] Truncated, int FailedRequests)> ReadDependents(
        ModUid[] lists,
        IProgress<PatchFinderProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dependents = new Dictionary<ModUid, Dependent>();
        var gate = new Lock();
        var failedRequests = 0;
        var requests = 0;
        var stopwatch = Stopwatch.StartNew();

        // Counted in pages: one per installed mod to begin with, and more once the first pages
        // say how long the lists are.
        var pagesDone = 0;
        var pagesTotal = lists.Length;

        var furtherPages = new Dictionary<int, List<ModUid>>();
        var truncated = new List<ModUid>();

        progress?.Report(new PatchFinderProgress(PatchFinderStage.ReadingDependents, 0, pagesTotal));

        async ValueTask ReadPage(ModUid[] mods, int offset, CancellationToken token)
        {
            var pages = await TryQuery(() => _graphQlClient.QueryModsRequiring(mods, offset, DependentsPerPage, token), mods.Length, offset);

            lock (gate)
            {
                requests++;
                pagesDone += mods.Length;

                if (pages is null)
                {
                    failedRequests++;
                }
                else
                {
                    foreach (var page in pages)
                    {
                        foreach (var reference in page.Dependents)
                        {
                            if (!dependents.TryGetValue(reference.Uid, out var dependent))
                            {
                                dependent = new Dependent(reference.Name);
                                dependents[reference.Uid] = dependent;
                            }

                            dependent.RequiredInstalledMods.Add(page.Uid);
                        }

                        if (offset != 0) continue;

                        var end = Math.Min(page.TotalCount, MaxDependentsPerMod);
                        if (page.TotalCount > MaxDependentsPerMod) truncated.Add(page.Uid);

                        for (var next = DependentsPerPage; next < end; next += DependentsPerPage)
                        {
                            if (!furtherPages.TryGetValue(next, out var atOffset))
                            {
                                atOffset = [];
                                furtherPages[next] = atOffset;
                            }

                            atOffset.Add(page.Uid);
                            pagesTotal++;
                        }
                    }
                }

                progress?.Report(new PatchFinderProgress(PatchFinderStage.ReadingDependents, pagesDone, pagesTotal));
            }
        }

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(
            lists.Chunk(ModsPerDependentsRequest),
            parallelOptions,
            (chunk, token) => ReadPage(chunk, offset: 0, token)
        );

        // Deepest pages first: only the longest lists go that far, and starting them early keeps
        // them from being the last thing left running.
        var work = furtherPages
            .OrderByDescending(static pair => pair.Key)
            .SelectMany(static pair => pair.Value.Chunk(ModsPerDependentsRequest).Select(chunk => (Offset: pair.Key, Mods: chunk)))
            .ToArray();

        await Parallel.ForEachAsync(
            work,
            parallelOptions,
            (item, token) => ReadPage(item.Mods, item.Offset, token)
        );

        if (truncated.Count > 0)
            _logger.LogInformation("Stopped reading {Count} lists of mods requiring a mod after {Max} entries", truncated.Count, MaxDependentsPerMod);

        _logger.LogInformation("Read {Count} mods requiring the installed ones in {Requests} requests and {Elapsed}", dependents.Count, requests, stopwatch.Elapsed);
        return (dependents, truncated.ToArray(), failedRequests);
    }

    /// <summary>
    /// Runs <paramref name="query"/> over <paramref name="mods"/> a chunk at a time, several
    /// chunks at once, and collects what comes back.
    /// </summary>
    private async ValueTask<(List<T> Results, int FailedRequests)> QueryInChunks<T>(
        List<ModUid> mods,
        int chunkSize,
        PatchFinderStage stage,
        IProgress<PatchFinderProgress>? progress,
        Func<ModUid[], CancellationToken, ValueTask<GraphQlResult<T[]>>> query,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var gate = new Lock();
        var failedRequests = 0;
        var finished = 0;
        var stopwatch = Stopwatch.StartNew();

        progress?.Report(new PatchFinderProgress(stage, 0, mods.Count));

        await Parallel.ForEachAsync(
            mods.Chunk(chunkSize),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = cancellationToken },
            async (chunk, token) =>
            {
                var result = await TryQuery(() => query(chunk, token), chunk.Length, offset: 0);

                lock (gate)
                {
                    if (result is null) failedRequests++;
                    else results.AddRange(result);

                    finished += chunk.Length;
                    progress?.Report(new PatchFinderProgress(stage, finished, mods.Count));
                }
            }
        );

        _logger.LogInformation("{Stage}: looked up {Count} mods in {Elapsed}", stage, mods.Count, stopwatch.Elapsed);
        return (results, failedRequests);
    }

    /// <summary>
    /// Runs one request, turning a failure into null so that one bad batch doesn't throw away
    /// everything the others found.
    /// </summary>
    private async ValueTask<T[]?> TryQuery<T>(Func<ValueTask<GraphQlResult<T[]>>> query, int modCount, int offset)
    {
        try
        {
            var result = await query();
            if (result.TryGetData(out var data)) return data;

            _logger.LogWarning(
                "Nexus Mods rejected a request for {Count} mods at offset {Offset}: {Errors}",
                modCount,
                offset,
                string.Join("; ", result.Errors.Select(static error => error.Value.Message))
            );
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Request for {Count} mods at offset {Offset} failed", modCount, offset);
            return null;
        }
    }

    private bool TryGetModUri(ModUid uid, Dictionary<NexusModsGameId, GameDomain> domains, [NotNullWhen(true)] out Uri? uri, out GameDomain domain)
    {
        try
        {
            domain = domains.TryGetValue(uid.GameId, out var known) ? known : _mappingCache[uid.GameId];
            uri = NexusModsUrlBuilder.GetModUri(domain, uid.ModId);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No site address for game {GameId}, skipping mod {ModId}", uid.GameId, uid.ModId);
            uri = null;
            domain = default;
            return false;
        }
    }
}
