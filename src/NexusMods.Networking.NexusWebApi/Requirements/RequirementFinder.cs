using System.Diagnostics;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Telemetry;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi.Patches;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Networking.NexusWebApi.Requirements;

/// <summary>
/// Works out which of the mods that some installed mods require are missing from the loadout.
/// </summary>
[PublicAPI]
public interface IRequirementFinder
{
    /// <summary>
    /// Reads the "Requirements" list on Nexus Mods of each of <paramref name="mods"/>, then the lists
    /// of the required mods that aren't installed, and so on, and sorts out what the loadout lacks.
    /// </summary>
    /// <param name="loadoutId">The loadout whose mods count as installed.</param>
    /// <param name="mods">The installed mods whose requirements to look up.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    ValueTask<RequirementFinderResult> Find(
        LoadoutId loadoutId,
        IReadOnlyCollection<ModUid> mods,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a <see cref="IRequirementFinder.Find"/> found.
/// </summary>
[PublicAPI]
public sealed record RequirementFinderResult
{
    /// <summary>
    /// The mods whose requirements were looked up.
    /// </summary>
    public required ModReference[] ModsChecked { get; init; }

    /// <summary>
    /// Every requirement that needs something done, installed or not. Those the app can see to come
    /// first (see <see cref="FoundRequirement.IsFixable"/>), each before the mods that require it,
    /// which is the order to stack them in from the lowest priority up. The rest follow by name.
    /// </summary>
    public required FoundRequirement[] Requirements { get; init; }

    /// <summary>
    /// How many of the required mods are installed and switched on already.
    /// </summary>
    public required int AlreadyInstalled { get; init; }

    /// <summary>
    /// How many requests to Nexus Mods failed. The requirements those would have found are missing.
    /// </summary>
    public required int FailedRequests { get; init; }

    /// <summary>
    /// Whether some optional requirements were left out, there being more of them than the search
    /// takes on at once.
    /// </summary>
    public required bool Truncated { get; init; }

    /// <summary>
    /// The requirements the app can see to, in the order to install them.
    /// </summary>
    public IEnumerable<FoundRequirement> Fixable => Requirements.Where(static requirement => requirement.IsFixable);
}

/// <summary>
/// What is the matter with a <see cref="FoundRequirement"/>.
/// </summary>
[PublicAPI]
public enum RequirementState
{
    /// <summary>
    /// On Nexus Mods and not in the loadout: it can be downloaded and installed.
    /// </summary>
    Missing,

    /// <summary>
    /// Hosted somewhere other than Nexus Mods, such as a game's own modding tools. Whether the
    /// user has it can't be told, and the app can't fetch it.
    /// </summary>
    OffSite,

    /// <summary>
    /// On Nexus Mods but not to be installed from there: taken down, or for another game.
    /// </summary>
    Unavailable,

    /// <summary>
    /// In the loadout, but switched off by the user. It can be switched back on.
    /// </summary>
    SwitchedOff,

    /// <summary>
    /// In the loadout, but only in collections that are switched off. A copy can be installed from
    /// the library, which stands in for it while those collections stay off.
    /// </summary>
    InDisabledCollection,
}

/// <summary>
/// One mod's "Requirements" list naming a <see cref="FoundRequirement"/>.
/// </summary>
/// <param name="Mod">The mod whose list it is.</param>
/// <param name="Notes">What its author wrote about the requirement, such as which version is needed.</param>
/// <param name="IsOptional">
/// Whether the mod can probably do without it: the notes say so, it is a translation, or the mod is
/// a hub such as a patch hub, which lists every mod it has a patch for and needs each of them only
/// for that patch.
/// </param>
[PublicAPI]
public sealed record RequirementLink(ModReference Mod, string? Notes, bool IsOptional);

/// <summary>
/// A mod that one or more of the mods looked up require, directly or through another requirement.
/// </summary>
[PublicAPI]
public sealed record FoundRequirement
{
    /// <summary>
    /// The mod page on Nexus Mods, or null for a requirement hosted elsewhere.
    /// </summary>
    public required ModUid? Uid { get; init; }

    /// <summary>
    /// Its name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Where to see it or get it, when known.
    /// </summary>
    public required Uri? Uri { get; init; }

    /// <summary>
    /// What is the matter with it.
    /// </summary>
    public required RequirementState State { get; init; }

    /// <summary>
    /// Why it can't be installed, for an <see cref="RequirementState.Unavailable"/> one.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The copy in the loadout to act on: for <see cref="RequirementState.SwitchedOff"/> the one to
    /// switch back on, for <see cref="RequirementState.InDisabledCollection"/> the one whose library
    /// file to install a copy of.
    /// </summary>
    public LoadoutItemGroupId? InstalledCopy { get; init; }

    /// <summary>
    /// For <see cref="RequirementState.InDisabledCollection"/>, the switched-off collection that
    /// <see cref="InstalledCopy"/> is in.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// For <see cref="RequirementState.InDisabledCollection"/>, the name of the file a copy is made of.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// The lists that name it: those of some of the mods looked up, or of other requirements.
    /// </summary>
    public required RequirementLink[] RequiredBy { get; init; }

    /// <summary>
    /// Whether every list that names it can do without it.
    /// </summary>
    public bool IsOptional => RequiredBy.All(static link => link.IsOptional);

    /// <summary>
    /// Whether the app can see to it: download it, switch it back on, or copy it out of a
    /// switched-off collection.
    /// </summary>
    public bool IsFixable => RequirementFinder.IsFixable(State);
}

/// <inheritdoc/>
internal sealed class RequirementFinder : IRequirementFinder
{
    // The API caps a page of a requirement list at 80 entries. Only patch hubs list more, and what
    // they list is optional anyway.
    private const int RequirementsPerMod = 80;

    // The API rejects a query whose cost passes 10,000, and every requirement asked for counts
    // whether a mod has that many or not.
    private const int ModsPerRequest = 10;

    private const int MaxParallelRequests = 6;

    // A patch hub lists a hundred mods, every one of them optional. Past this many optional ones the
    // rest are left out; the ones that are needed always go in.
    private const int MaxOptional = 200;

    // A patch that names this many mods is a hub of patches for each of them, rather than one patch
    // that needs them all.
    private const int PatchHubSize = 5;

    // Any mod that names this many is a hub of some kind, such as a pack of outfit conversions each
    // needing its own original. No mod genuinely needs a dozen others.
    private const int HubSize = 12;

    /// <summary>
    /// What authors write in a requirement's notes when the mod works without it. Matched anywhere
    /// in the notes, ignoring case.
    /// </summary>
    private static readonly string[] OptionalPhrases =
    [
        "optional", "not required", "not a hard requirement", "not needed", "not necessary", "not mandatory",
        "not actually", "isn't required", "isn't needed", "soft requirement", "run without", "work without",
        "works without", "only if", "only needed", "only required", "only for", "only users", "if you use",
        "if you want", "if using", "if you have", "either", "recommended", "supported", "compatib", "credit",
    ];

    private readonly ILogger<RequirementFinder> _logger;
    private readonly IGraphQlClient _graphQlClient;
    private readonly IConnection _connection;
    private readonly IGameDomainToGameIdMappingCache _mappingCache;

    public RequirementFinder(
        ILogger<RequirementFinder> logger,
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
    /// A requirement as the search builds it up.
    /// </summary>
    private sealed class Entry(ModUid? uid, string name, Uri? uri, RequirementState state)
    {
        public ModUid? Uid { get; } = uid;
        public string Name { get; set; } = name;
        public Uri? Uri { get; } = uri;
        public RequirementState State { get; set; } = state;
        public string? Reason { get; set; }
        public LoadoutItemGroupId? InstalledCopy { get; init; }
        public string? CollectionName { get; init; }
        public string? FileName { get; init; }
        public List<RequirementLink> RequiredBy { get; } = [];

        /// <summary>
        /// Whether its own list has been asked for, which only happens once something firmly needs
        /// it. An optional one is only checked for still being on the site.
        /// </summary>
        public bool IsWalked { get; set; }

        /// <summary>
        /// The keys of the fixable requirements this one requires in turn.
        /// </summary>
        public List<string> Requires { get; } = [];

        public void AddLink(RequirementLink link)
        {
            var index = RequiredBy.FindIndex(existing => existing.Mod.Uid == link.Mod.Uid);
            if (index == -1)
            {
                RequiredBy.Add(link);
                return;
            }

            // A list naming the same mod twice, say as two versions: needed if either entry says so.
            var existing = RequiredBy[index];
            RequiredBy[index] = existing with
            {
                Notes = existing.Notes is null ? link.Notes : link.Notes is null ? existing.Notes : $"{existing.Notes} / {link.Notes}",
                IsOptional = existing.IsOptional && link.IsOptional,
            };
        }

        public FoundRequirement ToResult() => new()
        {
            Uid = Uid,
            Name = Name,
            Uri = Uri,
            State = State,
            Reason = Reason,
            InstalledCopy = InstalledCopy,
            CollectionName = CollectionName,
            FileName = FileName,
            RequiredBy = RequiredBy.ToArray(),
        };
    }

    internal static bool IsFixable(RequirementState state) =>
        state is RequirementState.Missing or RequirementState.SwitchedOff or RequirementState.InDisabledCollection;

    public async ValueTask<RequirementFinderResult> Find(LoadoutId loadoutId, IReadOnlyCollection<ModUid> mods, CancellationToken cancellationToken)
    {
        var db = _connection.Db;
        var loadout = Loadout.Load(db, loadoutId);
        if (!loadout.IsValid()) throw new ArgumentException($"Loadout {loadoutId} does not exist", nameof(loadoutId));

        var gameId = loadout.Installation.GameId;
        var installed = InstalledMods.Read(db, loadoutId);
        var addresses = new SiteAddresses(_mappingCache, _logger);
        var stopwatch = Stopwatch.StartNew();

        var roots = mods.Distinct().ToArray();
        var rootNames = roots.ToDictionary(static uid => uid, uid => installed.PageNames.GetValueOrDefault(uid) ?? $"mod {uid.ModId}");

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var alreadyInstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedRequests = 0;
        var optionalCount = 0;
        var truncated = false;

        IReadOnlyList<ModUid> level = roots;
        var isFirstLevel = true;

        while (level.Count > 0)
        {
            var (lists, failedUids, failed) = await QueryAll(level, cancellationToken);
            failedRequests += failed;

            var byUid = new Dictionary<ModUid, ModRequirementList>();
            foreach (var list in lists) byUid.TryAdd(list.Uid, list);

            if (!isFirstLevel) ApplyStatus(level, byUid, failedUids);

            var next = new List<ModUid>();

            foreach (var list in byUid.Values.OrderBy(static list => list.Name, StringComparer.OrdinalIgnoreCase))
            {
                Entry? requirerEntry = null;
                ModReference requirer;

                if (isFirstLevel)
                {
                    if (!rootNames.ContainsKey(list.Uid)) continue;

                    // A mod the loadout doesn't have is only known by the site's name for it.
                    if (!installed.PageNames.ContainsKey(list.Uid) && !string.IsNullOrWhiteSpace(list.Name)) rootNames[list.Uid] = list.Name;
                    requirer = new ModReference(list.Uid, rootNames[list.Uid]);
                }
                else
                {
                    // A page that was taken down won't be installed, so what it needs doesn't matter.
                    if (!entries.TryGetValue(Key(list.Uid), out requirerEntry) || !IsFixable(requirerEntry.State)) continue;
                    requirer = new ModReference(list.Uid, requirerEntry.Name);
                }

                // Authors often enter a mod on the site as an off-site link to its page.
                var entriesOfList = list.Requirements
                    .Select(requirement => (Requirement: requirement, Uid: requirement.Uid ?? addresses.TryParseModPageUrl(requirement.Url)))
                    .ToArray();

                var modCount = entriesOfList.Count(static pair => pair.Uid is not null);
                var isHub = modCount >= HubSize ||
                            (modCount >= PatchHubSize && DependentClassifier.Classify(list.Name).Kind == DependentKind.Patch);

                foreach (var (requirement, parsedUid) in entriesOfList)
                {
                    // Some authors list the translations of their mod; it doesn't need any of them.
                    var isOptional = isHub ||
                                     ReadsAsOptional(requirement.Notes) ||
                                     DependentClassifier.Classify(requirement.Name).Kind == DependentKind.Translation;

                    var link = new RequirementLink(requirer, requirement.Notes, isOptional);

                    if (parsedUid is { } uid)
                    {
                        var key = Key(uid);

                        if (!entries.TryGetValue(key, out var entry))
                        {
                            var copies = installed.CopiesOf(uid, requirement.Name);
                            if (copies.Count > 0)
                            {
                                var name = installed.PageNames.GetValueOrDefault(uid) ?? requirement.Name;
                                entry = CreateInstalledEntry(uid, name, addresses.TryGetModUri(uid), copies);
                                if (entry is null)
                                {
                                    alreadyInstalled.Add(key);
                                    continue;
                                }

                                LogCopies(entry, copies);
                            }
                            // One of the mods looked up that the loadout doesn't have after all; it
                            // was asked about, so it isn't something to add.
                            else if (rootNames.ContainsKey(uid))
                            {
                                continue;
                            }
                            else if (uid.GameId != gameId)
                            {
                                entry = new Entry(uid, requirement.Name, addresses.TryGetModUri(uid), RequirementState.Unavailable)
                                {
                                    Reason = "for another game on Nexus Mods",
                                };
                            }
                            else
                            {
                                if (link.IsOptional && optionalCount >= MaxOptional)
                                {
                                    truncated = true;
                                    continue;
                                }

                                entry = new Entry(uid, requirement.Name, addresses.TryGetModUri(uid), RequirementState.Missing);
                                if (link.IsOptional) optionalCount++;
                            }

                            entries[key] = entry;
                        }

                        entry.AddLink(link);
                        if (!IsFixable(entry.State)) continue;

                        requirerEntry?.Requires.Add(key);

                        // What it requires in turn matters too, whether it is missing or only switched
                        // off: a switched-off collection usually holds a requirement's own requirements
                        // as well.
                        if (!link.IsOptional && !entry.IsWalked)
                        {
                            entry.IsWalked = true;
                            next.Add(uid);
                        }
                    }
                    else if (requirement.IsExternal || requirement.Url is not null)
                    {
                        var key = "url:" + (requirement.Url ?? requirement.Name);

                        if (!entries.TryGetValue(key, out var entry))
                        {
                            var uri = Uri.TryCreate(requirement.Url, UriKind.Absolute, out var parsed) ? parsed : null;

                            // Whatever is hosted elsewhere can only be recognised by its name, when
                            // the user added it from a file.
                            var copies = installed.CopiesOf(uid: null, requirement.Name);
                            if (copies.Count > 0)
                            {
                                entry = CreateInstalledEntry(uid: null, requirement.Name, uri, copies);
                                if (entry is null)
                                {
                                    alreadyInstalled.Add(key);
                                    continue;
                                }

                                LogCopies(entry, copies);
                            }
                            else
                            {
                                entry = new Entry(uid: null, requirement.Name, uri, RequirementState.OffSite);
                            }

                            entries[key] = entry;
                        }

                        entry.AddLink(link);
                        if (IsFixable(entry.State)) requirerEntry?.Requires.Add(key);
                    }
                }
            }

            level = next;
            isFirstLevel = false;
        }

        // The optional ones nothing firmly needs were never looked up, and some will have been taken
        // down since the lists naming them were written.
        var unwalked = entries.Values
            .Where(static entry => entry is { State: RequirementState.Missing, IsWalked: false, Uid: not null })
            .Select(static entry => entry.Uid!.Value)
            .ToArray();

        if (unwalked.Length > 0)
        {
            var (lists, failedUids, failed) = await QueryAll(unwalked, cancellationToken);
            failedRequests += failed;

            var byUid = new Dictionary<ModUid, ModRequirementList>();
            foreach (var list in lists) byUid.TryAdd(list.Uid, list);

            ApplyStatus(unwalked, byUid, failedUids);
        }

        var requirements = OrderForInstall(entries)
            .Concat(entries.Values
                .Where(static entry => !IsFixable(entry.State))
                .OrderBy(static entry => entry.State)
                .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            .Select(static entry => entry.ToResult())
            .ToArray();

        _logger.LogInformation(
            "Looked up the requirements of {Count} mods in {Elapsed}: {Missing} to install ({Optional} optional), {SwitchedOff} switched off, {InDisabledCollection} only in switched-off collections, {OffSite} hosted elsewhere, {Unavailable} unavailable, {Installed} installed ({Failed} failed requests)",
            roots.Length,
            stopwatch.Elapsed,
            requirements.Count(static requirement => requirement.State == RequirementState.Missing),
            requirements.Count(static requirement => requirement is { State: RequirementState.Missing, IsOptional: true }),
            requirements.Count(static requirement => requirement.State == RequirementState.SwitchedOff),
            requirements.Count(static requirement => requirement.State == RequirementState.InDisabledCollection),
            requirements.Count(static requirement => requirement.State == RequirementState.OffSite),
            requirements.Count(static requirement => requirement.State == RequirementState.Unavailable),
            alreadyInstalled.Count,
            failedRequests
        );

        return new RequirementFinderResult
        {
            ModsChecked = roots
                .Select(uid => new ModReference(uid, rootNames[uid]))
                .OrderBy(static mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            Requirements = requirements,
            AlreadyInstalled = alreadyInstalled.Count,
            FailedRequests = failedRequests,
            Truncated = truncated,
        };

        // Why a requirement the loadout has counts as off, for when the verdict looks wrong.
        void LogCopies(Entry entry, IReadOnlyList<InstalledCopy> copies)
        {
            _logger.LogDebug("Requirement `{Name}` is {State}: {Copies}", entry.Name, entry.State, string.Join("; ", copies));
        }

        void ApplyStatus(IReadOnlyList<ModUid> asked, Dictionary<ModUid, ModRequirementList> byUid, HashSet<ModUid> failedUids)
        {
            foreach (var uid in asked)
            {
                // One the loadout has keeps its name, and works whatever became of its page.
                var entry = entries[Key(uid)];
                if (entry.State != RequirementState.Missing) continue;

                if (byUid.TryGetValue(uid, out var list))
                {
                    // The site's own name for the page beats whatever the list that named it wrote.
                    if (!string.IsNullOrWhiteSpace(list.Name)) entry.Name = list.Name;
                    if (list.Status.Equals("published", StringComparison.OrdinalIgnoreCase)) continue;

                    entry.State = RequirementState.Unavailable;
                    entry.Reason = DescribeStatus(list.Status);
                }
                else if (!failedUids.Contains(uid))
                {
                    entry.State = RequirementState.Unavailable;
                    entry.Reason = "not found on Nexus Mods";
                }

                // When the request failed, it stays missing: the page most likely exists, and the
                // download says so if it doesn't. Only what it requires in turn is unknown.
            }
        }
    }

    private static string Key(ModUid uid) => $"mod:{uid.GameId}/{uid.ModId}";

    /// <summary>
    /// What to do about a requirement the loadout has <paramref name="copies"/> of, or null when one
    /// of them is on and there is nothing to do.
    /// </summary>
    /// <remarks>
    /// A copy the user switched off is switched back on rather than copied: that leaves the loadout
    /// with no more copies than it had. Only when every copy is in a switched-off collection does a
    /// new one have to be made.
    /// </remarks>
    private static Entry? CreateInstalledEntry(ModUid? uid, string name, Uri? uri, IReadOnlyList<InstalledCopy> copies)
    {
        if (copies.Any(static copy => copy.IsEnabled)) return null;

        var switchedOff = copies.FirstOrDefault(static copy => copy.DisabledCollection is null);
        if (switchedOff is not null)
        {
            return new Entry(uid, name, uri, RequirementState.SwitchedOff)
            {
                InstalledCopy = switchedOff.Group,
            };
        }

        // A copy the collection kept on is the one it meant to use; past that, the newest file. Which
        // file it is goes in the dialog, for the rare page whose collection copy is an extra file.
        var best = copies
            .OrderBy(static copy => copy.IsSwitchedOffItself)
            .ThenByDescending(static copy => copy.UploadedAt)
            .First();

        return new Entry(uid, name, uri, RequirementState.InDisabledCollection)
        {
            InstalledCopy = best.Group,
            CollectionName = best.DisabledCollection,
            FileName = best.FileName,
        };
    }

    private static bool ReadsAsOptional(string? notes)
    {
        if (notes is null) return false;
        return OptionalPhrases.Any(phrase => notes.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Why a page whose status isn't <c>published</c> can't be downloaded from.
    /// </summary>
    private static string DescribeStatus(string status) => status.ToLowerInvariant() switch
    {
        "removed" or "wastebinned" => "taken down from Nexus Mods",
        "hidden" => "hidden by its author on Nexus Mods",
        "under_moderation" => "under moderation on Nexus Mods",
        "not_published" => "not published on Nexus Mods yet",
        _ => $"not available on Nexus Mods ({status})",
    };

    /// <summary>
    /// The fixable requirements, each after the ones it requires.
    /// </summary>
    /// <remarks>
    /// Authors do list one another's mods as requirements both ways round, so a cycle just stops
    /// where it closes rather than being an error.
    /// </remarks>
    private static List<Entry> OrderForInstall(Dictionary<string, Entry> entries)
    {
        var ordered = new List<Entry>();
        var visited = new HashSet<Entry>();

        foreach (var entry in entries.Values
                     .Where(static entry => IsFixable(entry.State))
                     .OrderBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Visit(entry);
        }

        return ordered;

        void Visit(Entry entry)
        {
            if (!visited.Add(entry)) return;

            foreach (var key in entry.Requires)
            {
                if (entries.TryGetValue(key, out var requirement) && IsFixable(requirement.State)) Visit(requirement);
            }

            ordered.Add(entry);
        }
    }

    /// <summary>
    /// Reads the lists of <paramref name="mods"/> a chunk at a time, several chunks at once.
    /// </summary>
    /// <returns>What came back, the mods of the chunks that failed, and how many chunks failed.</returns>
    private async ValueTask<(List<ModRequirementList> Lists, HashSet<ModUid> FailedUids, int FailedRequests)> QueryAll(
        IReadOnlyList<ModUid> mods,
        CancellationToken cancellationToken)
    {
        var lists = new List<ModRequirementList>();
        var failedUids = new HashSet<ModUid>();
        var failedRequests = 0;
        var gate = new Lock();

        await Parallel.ForEachAsync(
            mods.Chunk(ModsPerRequest),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelRequests, CancellationToken = cancellationToken },
            async (chunk, token) =>
            {
                var result = await TryQuery(chunk, token);

                lock (gate)
                {
                    if (result is not null)
                    {
                        lists.AddRange(result);
                        return;
                    }

                    failedRequests++;
                    failedUids.UnionWith(chunk);
                }
            }
        );

        return (lists, failedUids, failedRequests);
    }

    /// <summary>
    /// Runs one request, turning a failure into null so that one bad batch doesn't throw away
    /// everything the others found.
    /// </summary>
    private async ValueTask<ModRequirementList[]?> TryQuery(ModUid[] mods, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _graphQlClient.QueryModRequirements(mods, RequirementsPerMod, cancellationToken);
            if (result.TryGetData(out var data)) return data;

            _logger.LogWarning(
                "Nexus Mods rejected a request for the requirements of {Count} mods: {Errors}",
                mods.Length,
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
            _logger.LogWarning(exception, "Request for the requirements of {Count} mods failed", mods.Length);
            return null;
        }
    }

    /// <summary>
    /// Translates between the site's game names and ids for one search, remembering every answer.
    /// </summary>
    /// <remarks>
    /// The cache behind it asks the API, and waits for it, about a game it hasn't seen, and doesn't
    /// remember a game it couldn't find. Lists name the same few games over and over, so one
    /// question per game is all a search should cost.
    /// </remarks>
    private sealed class SiteAddresses(IGameDomainToGameIdMappingCache mappingCache, ILogger logger)
    {
        private readonly Dictionary<string, NexusModsGameId?> _gameIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<NexusModsGameId, GameDomain?> _domains = [];

        /// <summary>
        /// The mod a link to a mod page on Nexus Mods is for, such as
        /// <c>https://www.nexusmods.com/cyberpunk2077/mods/107?tab=files</c>.
        /// </summary>
        public ModUid? TryParseModPageUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (!uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase)) return null;

            // Either /<game>/mods/<id> or, on the newer site, /games/<game>/mods/<id>.
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var modsIndex = Array.FindLastIndex(segments, static segment => segment.Equals("mods", StringComparison.OrdinalIgnoreCase));
            if (modsIndex < 1 || modsIndex + 1 >= segments.Length) return null;
            if (!uint.TryParse(segments[modsIndex + 1], out var modId) || modId == 0) return null;

            var domain = segments[modsIndex - 1].ToLowerInvariant();
            if (!_gameIds.TryGetValue(domain, out var gameId))
            {
                try
                {
                    gameId = mappingCache[GameDomain.From(domain)];
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "No game on Nexus Mods for the links to `{Domain}`, such as `{Url}`", domain, url);
                    gameId = null;
                }

                _gameIds[domain] = gameId;
            }

            return gameId is { } id ? new ModUid(ModId.From(modId), id) : null;
        }

        public Uri? TryGetModUri(ModUid uid)
        {
            if (!_domains.TryGetValue(uid.GameId, out var domain))
            {
                try
                {
                    domain = mappingCache[uid.GameId];
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "No site address for game {GameId}, so its mods get no links", uid.GameId);
                    domain = null;
                }

                _domains[uid.GameId] = domain;
            }

            return domain is { } known ? NexusModsUrlBuilder.GetModUri(known, uid.ModId) : null;
        }
    }

    /// <summary>
    /// One copy of a mod in a loadout.
    /// </summary>
    /// <param name="Group">The mod.</param>
    /// <param name="IsEnabled">Whether it is on, collection and all.</param>
    /// <param name="IsSwitchedOffItself">Whether it is switched off itself, rather than only through its collection.</param>
    /// <param name="DisabledCollection">The name of the switched-off collection it is in, if it is in one.</param>
    /// <param name="FileName">The name of the file it was installed from.</param>
    /// <param name="UploadedAt">When that file was uploaded, for one from Nexus Mods.</param>
    private sealed record InstalledCopy(
        LoadoutItemGroupId Group,
        bool IsEnabled,
        bool IsSwitchedOffItself,
        string? DisabledCollection,
        string FileName,
        DateTimeOffset UploadedAt);

    /// <summary>
    /// The mods of a loadout, enabled or not, as a requirement can be matched against them.
    /// </summary>
    private sealed class InstalledMods
    {
        /// <summary>
        /// The copies of each mod page the loadout's mods came from.
        /// </summary>
        private Dictionary<ModUid, List<InstalledCopy>> ByPage { get; } = [];

        /// <summary>
        /// The copies of the mods added from a file on disk, which are only known by their names.
        /// </summary>
        private Dictionary<string, List<InstalledCopy>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The copies of every mod, by name.
        /// </summary>
        private Dictionary<string, List<InstalledCopy>> ByAnyName { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<ModUid, string> PageNames { get; } = [];

        /// <summary>
        /// The copies of a requirement. For one on Nexus Mods, those from its page and those of its
        /// name from a file on disk: a mod from another page that happens to share the name is
        /// another mod. For one hosted elsewhere, any mod of its name, as such a link often points
        /// to a mod that has a page on Nexus Mods as well.
        /// </summary>
        public IReadOnlyList<InstalledCopy> CopiesOf(ModUid? uid, string name)
        {
            if (uid is not { } page) return ByAnyName.TryGetValue(name, out var anyName) ? anyName : [];

            var copies = new List<InstalledCopy>();
            if (ByPage.TryGetValue(page, out var fromPage)) copies.AddRange(fromPage);
            if (ByName.TryGetValue(name, out var byName)) copies.AddRange(byName);
            return copies;
        }

        public static InstalledMods Read(IDb db, LoadoutId loadoutId)
        {
            var mods = new InstalledMods();

            foreach (var linked in LoadoutItem.FindByLoadout(db, loadoutId).OfTypeLoadoutItemGroup().OfTypeLibraryLinkedLoadoutItem())
            {
                var item = linked.AsLoadoutItemGroup().AsLoadoutItem();
                var isNexus = linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexusModsLibraryItem);

                // One walk up the parents answers both: on is on itself with nothing above it off.
                var disabledParent = FindDisabledParent(item);

                var copy = new InstalledCopy(
                    Group: linked.AsLoadoutItemGroup().LoadoutItemGroupId,
                    IsEnabled: !item.IsDisabled && disabledParent is null,
                    IsSwitchedOffItself: item.IsDisabled,
                    DisabledCollection: disabledParent,
                    FileName: isNexus ? nexusModsLibraryItem.FileMetadata.Name : linked.LibraryItem.Name,
                    UploadedAt: isNexus ? nexusModsLibraryItem.FileMetadata.UploadedAt : DateTimeOffset.MinValue
                );

                Add(mods.ByAnyName, item.Name, copy);

                if (!isNexus)
                {
                    Add(mods.ByName, item.Name, copy);
                    continue;
                }

                var page = nexusModsLibraryItem.ModPageMetadata;
                Add(mods.ByPage, page.Uid, copy);
                mods.PageNames.TryAdd(page.Uid, page.Name);
            }

            return mods;
        }

        /// <summary>
        /// The name of the nearest group above <paramref name="item"/> that is switched off, which is
        /// what keeps it off even when it is on itself.
        /// </summary>
        private static string? FindDisabledParent(LoadoutItem.ReadOnly item)
        {
            var current = item;
            while (LoadoutItem.Parent.TryGetValue(current, out var parentId))
            {
                current = LoadoutItem.Load(current.Db, parentId);
                if (current.IsDisabled) return current.Name;
            }

            return null;
        }

        private static void Add<TKey>(Dictionary<TKey, List<InstalledCopy>> index, TKey key, InstalledCopy copy) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var copies))
            {
                copies = [];
                index[key] = copies;
            }

            copies.Add(copy);
        }
    }
}
