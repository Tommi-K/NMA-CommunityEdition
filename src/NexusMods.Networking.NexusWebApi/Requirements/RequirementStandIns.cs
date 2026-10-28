using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Extensions;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Abstractions.ElementComparers;
using NexusMods.MnemonicDB.Abstractions.Query;
using NexusMods.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Networking.NexusWebApi.Requirements;

/// <summary>
/// Keeps the copies installed for requirements that are only in switched-off collections - the
/// <see cref="RequirementStandIn"/>s - from doubling up with the originals.
/// </summary>
/// <remarks>
/// A stand-in is only wanted while no other copy of its file is on. So it goes off when one is
/// switched on - its collection switched back on, or the mod installed again - and back on when
/// that copy goes off or is removed. The user switching a stand-in on or off by hand settles it: the
/// app never switches back on one it didn't switch off, and one the user switches back on while
/// another copy is on becomes an ordinary mod.
/// <para/>
/// This runs by itself whenever something is switched on or off, installed or removed.
/// </remarks>
[PublicAPI]
public interface IRequirementStandIns
{
    /// <summary>
    /// What it switched, as it happens, for telling the user.
    /// </summary>
    IObservable<StandInChange> Changes { get; }

    /// <summary>
    /// Brings every stand-in in line with the other copies of its file now, rather than once the
    /// changes that call for it have settled.
    /// </summary>
    /// <returns>What it switched.</returns>
    ValueTask<StandInChange[]> Reconcile(CancellationToken cancellationToken = default);
}

/// <summary>
/// What <see cref="IRequirementStandIns"/> does to one stand-in.
/// </summary>
[PublicAPI]
public enum StandInAction
{
    /// <summary>
    /// Nothing: it is on with no other copy on, off with one on, or off because the user said so.
    /// </summary>
    None,

    /// <summary>
    /// Another copy of the file is on, so this one goes off until that one does.
    /// </summary>
    StandDown,

    /// <summary>
    /// It was switched off for another copy that is no longer on, so it comes back on.
    /// </summary>
    ComeBack,

    /// <summary>
    /// The user switched it back on while another copy was on: they want both, so the app stops
    /// managing it.
    /// </summary>
    StopTracking,
}

/// <summary>
/// One stand-in that <see cref="IRequirementStandIns"/> switched.
/// </summary>
/// <param name="StandIn">The stand-in.</param>
/// <param name="Name">Its name.</param>
/// <param name="Action">What was done.</param>
/// <param name="OtherCopyPlace">For <see cref="StandInAction.StandDown"/>, the collection the copy that is on is in, if it is in one.</param>
[PublicAPI]
public sealed record StandInChange(LoadoutItemGroupId StandIn, string Name, StandInAction Action, string? OtherCopyPlace);

/// <inheritdoc cref="IRequirementStandIns"/>
internal sealed class RequirementStandIns : IRequirementStandIns, IHostedService, IDisposable
{
    // Collections switch dozens of mods at once; one look once they are done is enough.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(200);

    private readonly ILogger<RequirementStandIns> _logger;
    private readonly IConnection _connection;
    private readonly Subject<StandInChange> _changes = new();

    private IDisposable? _subscription;

    public RequirementStandIns(ILogger<RequirementStandIns> logger, IConnection connection)
    {
        _logger = logger;
        _connection = connection;
    }

    public IObservable<StandInChange> Changes => _changes;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Only what can change the answer: something switched on or off, a mod installed or
        // removed, or a stand-in added.
        var changes = Observable.Merge(
            _connection.ObserveDatoms(LoadoutItem.Disabled).Select(static _ => System.Reactive.Unit.Default),
            _connection.ObserveDatoms(LibraryLinkedLoadoutItem.LibraryItem).Select(static _ => System.Reactive.Unit.Default),
            _connection.ObserveDatoms(RequirementStandIn.StandIn).Select(static _ => System.Reactive.Unit.Default)
        );

        // One pass at a time.
        _subscription = changes
            .Throttle(SettleTime)
            .Select(_ => Observable.FromAsync(ReconcileQuietly))
            .Concat()
            .Subscribe();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _changes.Dispose();
    }

    public async ValueTask<StandInChange[]> Reconcile(CancellationToken cancellationToken)
    {
        // Nearly always there is nothing to do, which is worth knowing before a transaction.
        var db = _connection.Db;
        if (!RequirementStandIn.All(db).Any(standIn => Assess(db, standIn).Action != StandInAction.None)) return [];

        cancellationToken.ThrowIfCancellationRequested();

        // Decided again inside the transaction, against the database as it stands then, so that a
        // switch the user makes in the meantime is never undone by a stale decision.
        var txFunction = new ReconcileTxFunction();
        using (var tx = _connection.BeginTransaction())
        {
            tx.Add(txFunction);
            await tx.Commit();
        }

        var changes = txFunction.Changes.ToArray();
        foreach (var change in changes)
        {
            _logger.LogInformation("Requirement stand-in `{Name}` ({Id}): {Action}{Other}",
                change.Name, change.StandIn, change.Action, change.OtherCopyPlace is null ? string.Empty : $", the copy in `{change.OtherCopyPlace}` being on");
            _changes.OnNext(change);
        }

        return changes;
    }

    private async Task ReconcileQuietly()
    {
        try
        {
            await Reconcile(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to bring the requirement stand-ins in line with the other copies of their files");
        }
    }

    /// <summary>
    /// The rule, apart from the database: what to do with a stand-in given whether it is on, whether
    /// the app switched it off, and whether another copy of its file is on.
    /// </summary>
    internal static StandInAction Decide(bool isOn, bool isStoodDown, bool otherCopyIsOn)
    {
        // The app always clears the mark when it switches one back on, so on and marked means
        // somebody else switched it on.
        if (isOn && isStoodDown) return StandInAction.StopTracking;
        if (isOn && otherCopyIsOn) return StandInAction.StandDown;
        if (isStoodDown && !otherCopyIsOn) return StandInAction.ComeBack;
        return StandInAction.None;
    }

    private readonly record struct Assessment(StandInAction Action, LoadoutItem.ReadOnly? OtherCopy);

    private static Assessment Assess(IDb db, RequirementStandIn.ReadOnly standIn)
    {
        if (!LibraryLinkedLoadoutItem.TryGet(db, standIn.Id, out var linked)) return default;

        var item = standIn.AsLoadoutItemGroup().AsLoadoutItem();
        var otherCopy = FindOtherCopyThatIsOn(db, linked.Value, item.LoadoutId);

        return new Assessment(Decide(isOn: !item.IsDisabled, standIn.IsStoodDown, otherCopy is not null), otherCopy);
    }

    /// <summary>
    /// A copy of the stand-in's file elsewhere in its loadout that is on, collection and all.
    /// </summary>
    /// <remarks>
    /// Another stand-in doesn't count: it provides nothing the first one doesn't, and counting it
    /// would have the two switch each other off.
    /// </remarks>
    private static LoadoutItem.ReadOnly? FindOtherCopyThatIsOn(IDb db, LibraryLinkedLoadoutItem.ReadOnly standIn, LoadoutId loadoutId)
    {
        foreach (var libraryItem in SameFile(db, standIn.LibraryItem))
        {
            foreach (var other in LibraryLinkedLoadoutItem.FindByLibraryItem(db, libraryItem))
            {
                if (other.Id == standIn.Id) continue;

                var otherItem = other.AsLoadoutItem();
                if (otherItem.LoadoutId != loadoutId) continue;
                if (RequirementStandIn.Load(db, other.Id).IsValid()) continue;
                if (otherItem.IsEnabled()) return otherItem;
            }
        }

        return null;
    }

    /// <summary>
    /// The library items holding the file <paramref name="libraryItem"/> does: itself, another
    /// download of it, or another version of it, which on Nexus Mods keeps the file's name.
    /// </summary>
    /// <remarks>
    /// Other files of the same page don't count. A page's optional extras are often on it too, and
    /// one of those being on is no reason to switch off the main file a mod needs.
    /// </remarks>
    private static IEnumerable<LibraryItem.ReadOnly> SameFile(IDb db, LibraryItem.ReadOnly libraryItem)
    {
        yield return libraryItem;

        if (!libraryItem.TryGetAsNexusModsLibraryItem(out var nexusModsLibraryItem)) yield break;
        var file = nexusModsLibraryItem.FileMetadata;

        foreach (var other in NexusModsLibraryItem.FindByModPageMetadata(db, nexusModsLibraryItem.ModPageMetadata))
        {
            if (other.Id == nexusModsLibraryItem.Id) continue;

            var otherFile = other.FileMetadata;
            if (otherFile.Id == file.Id || string.Equals(otherFile.Name, file.Name, StringComparison.OrdinalIgnoreCase))
                yield return other.AsLibraryItem();
        }
    }

    /// <summary>
    /// Switches the stand-ins on and off as <see cref="Decide"/> says, deciding against the database
    /// the transaction is applied to.
    /// </summary>
    private sealed class ReconcileTxFunction : ITxFunction
    {
        /// <summary>
        /// What it did, for telling the user once the transaction is in.
        /// </summary>
        public List<StandInChange> Changes { get; } = [];

        public bool Equals(ITxFunction? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

        public void Apply(ITransaction tx, IDb basis)
        {
            Changes.Clear();

            foreach (var standIn in RequirementStandIn.All(basis))
            {
                var (action, otherCopy) = Assess(basis, standIn);
                if (action == StandInAction.None) continue;

                var id = standIn.Id;
                switch (action)
                {
                    case StandInAction.StandDown:
                        tx.Add(id, LoadoutItem.Disabled, Null.Instance);
                        tx.Add(id, RequirementStandIn.StoodDown, Null.Instance);
                        break;
                    case StandInAction.ComeBack:
                        tx.Retract(id, LoadoutItem.Disabled, Null.Instance);
                        tx.Retract(id, RequirementStandIn.StoodDown, Null.Instance);
                        break;
                    case StandInAction.StopTracking:
                        tx.Retract(id, RequirementStandIn.StoodDown, Null.Instance);
                        tx.Retract(id, RequirementStandIn.StandIn, Null.Instance);
                        break;
                }

                var otherPlace = otherCopy is { } copy && copy.HasParent() ? copy.Parent.AsLoadoutItem().Name : null;
                Changes.Add(new StandInChange(standIn.AsLoadoutItemGroup().LoadoutItemGroupId, standIn.AsLoadoutItemGroup().AsLoadoutItem().Name, action, otherPlace));
            }
        }
    }
}
