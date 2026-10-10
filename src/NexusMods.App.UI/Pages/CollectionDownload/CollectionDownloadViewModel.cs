using System.Reactive.Linq;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Media.Imaging;
using DynamicData;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Abstractions.Telemetry;
using NexusMods.App.UI.Controls;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Controls.Navigation;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Pages.LibraryPage;
using NexusMods.App.UI.Pages.LoadoutPage;
using NexusMods.App.UI.Pages.TextEdit;
using NexusMods.App.UI.Resources;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Collections;
using NexusMods.UI.Sdk.Icons;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Loadouts;
using NexusMods.UI.Sdk;
using OneOf;
using R3;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Observable = System.Reactive.Linq.Observable;
using ReactiveCommand = R3.ReactiveCommand;

namespace NexusMods.App.UI.Pages.CollectionDownload;

using CollectionDownloadEntity = NexusMods.Abstractions.NexusModsLibrary.Models.CollectionDownload;

public sealed class CollectionDownloadViewModel : APageViewModel<ICollectionDownloadViewModel>, ICollectionDownloadViewModel
{
    private readonly CollectionRevisionMetadata.ReadOnly _revision;
    private readonly CollectionMetadata.ReadOnly _collection;

    private readonly IServiceProvider _serviceProvider;
    private readonly IOverlayController _overlayController;
    private readonly IWindowNotificationService _notificationService;
    private readonly LoadoutId _targetLoadout;

    public CollectionDownloadTreeDataGridAdapter TreeDataGridAdapter { get; }

    public CollectionDownloadViewModel(
        IWindowManager windowManager,
        IServiceProvider serviceProvider,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        LoadoutId targetLoadout) : base(windowManager)
    {
        _serviceProvider = serviceProvider;
        _overlayController = serviceProvider.GetRequiredService<IOverlayController>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();

        var connection = serviceProvider.GetRequiredService<IConnection>();
        var mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        var osInterop = serviceProvider.GetRequiredService<IOSInterop>();
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        var loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        var jobMonitor = serviceProvider.GetRequiredService<IJobMonitor>();
        var modUpdateDownloader = serviceProvider.GetRequiredService<ModUpdateDownloader>();

        var tileImagePipeline = ImagePipelines.GetCollectionTileImagePipeline(serviceProvider);
        var backgroundImagePipeline = ImagePipelines.GetCollectionBackgroundImagePipeline(serviceProvider);
        var userAvatarPipeline = ImagePipelines.GetUserAvatarPipeline(serviceProvider);

        _revision = revisionMetadata;
        _collection = revisionMetadata.Collection;
        _targetLoadout = targetLoadout;

        var libraryFile = collectionDownloader.GetLibraryFile(revisionMetadata);
        var collectionJsonFile = nexusModsLibrary.GetCollectionJsonFile(libraryFile);

        TabTitle = _collection.Name;
        TabIcon = IconValues.CollectionsOutline;

        TreeDataGridAdapter = new CollectionDownloadTreeDataGridAdapter(serviceProvider, revisionMetadata, targetLoadout);
        TreeDataGridAdapter.ViewHierarchical.Value = false;

        RequiredDownloadsCount = CollectionDownloader.CountItems(_revision, CollectionDownloader.ItemType.Required);
        OptionalDownloadsCount = CollectionDownloader.CountItems(_revision, CollectionDownloader.ItemType.Optional);

        // True whatever the account: premium downloads through the API, and everyone else
        // has each mod's download page driven for them in a tab. Neither button carries the
        // premium badge any more because neither needs premium.
        CanDownloadAutomatically = true;


        CommandDownloadRequiredItems = _isDownloadingRequiredItems.CombineLatest(_canDownloadRequiredItems, static (isDownloading, canDownload) => !isDownloading && canDownload)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, cancellationToken) =>
                {
                    if (!await loginManager.EnsureLoggedIn("Download Collection", cancellationToken)) return;

                    // No premium check: a free account gets each mod's download page opened
                    // and started for it, one mod after another, rather than being sent off
                    // to upgrade.
                    await collectionDownloader.DownloadItems(_revision, itemType: CollectionDownloader.ItemType.Required, db: connection.Db,
                        cancellationToken: cancellationToken
                    );
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandDownloadOptionalItems = _isDownloadingOptionalItems.CombineLatest(_canDownloadOptionalItems, static (isDownloading, canDownload) => !isDownloading && canDownload)
            .ToReactiveCommand<Unit>(
                executeAsync: async (_, cancellationToken) =>
                {
                    if (!await loginManager.EnsureLoggedIn("Download Collection", cancellationToken)) return;

                    await collectionDownloader.DownloadItems(_revision, itemType: CollectionDownloader.ItemType.Optional, db: connection.Db,
                        cancellationToken: cancellationToken
                    );
                },
                awaitOperation: AwaitOperation.Drop,
                configureAwait: false
            );

        CommandInstallOptionalItems = IsInstalling.CombineLatest(_canInstallOptionalItems, static (isInstalling, canInstall) => !isInstalling && canInstall).ToReactiveCommand<Unit>(
            executeAsync: async (_, _) =>
            {
                await InstallCollectionJob.Create(
                    serviceProvider,
                    targetLoadout,
                    source: libraryFile,
                    revisionMetadata,
                    items: CollectionDownloader.GetItems(revisionMetadata, CollectionDownloader.ItemType.Optional)
                );
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandInstallRequiredItems = IsInstalling.CombineLatest(_canInstallRequiredItems, static (isInstalling, canInstall) => !isInstalling && canInstall).ToReactiveCommand<Unit>(
            executeAsync: async (_, _) =>
            {
                var items = CollectionDownloader.GetItems(revisionMetadata, CollectionDownloader.ItemType.Required);
                var group = await InstallCollectionJob.Create(
                    serviceProvider,
                    targetLoadout,
                    source: libraryFile,
                    revisionMetadata,
                    items: items
                );

                if (CollectionDownloader.IsFullyInstalled(items, group.AsCollectionGroup(), connection.Db))
                    _notificationService.ShowToast(Language.ToastNotification_Collection_installed, ToastNotificationVariant.Success);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandDeleteCollectionRevision = new ReactiveCommand(
            executeAsync: async (_, _) =>
            {
                var pageData = new PageData
                {
                    FactoryId = LibraryPageFactory.StaticId,
                    Context = new LibraryPageContext()
                    {
                        LoadoutId = targetLoadout,
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = new OpenPageBehavior.ReplaceTab(PanelId, TabId);
                workspaceController.OpenPage(WorkspaceId, pageData, behavior,
                    checkOtherPanels: false
                );

                await collectionDownloader.DeleteCollectionLoadoutGroup(_revision, cancellationToken: CancellationToken.None);
                await collectionDownloader.DeleteRevision(_revision);
                
                _notificationService.ShowToast(Language.ToastNotification_Collection_deleted);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false,
            cancelOnCompleted: false
        );

        CommandDeleteAllDownloads = new ReactiveCommand(canExecuteSource: R3.Observable.Return(false), initialCanExecute: false);

        CommandViewOnNexusMods = new ReactiveCommand(execute: _ =>
        {
            var gameDomain = mappingCache[_collection.GameId];
            var uri = NexusModsUrlBuilder.GetCollectionUri(gameDomain, _collection.Slug, revisionMetadata.RevisionNumber, campaign: NexusModsUrlBuilder.CampaignCollections);
            osInterop.OpenUri(uri);
        });

        CommandOpenJsonFile = new ReactiveCommand(
            execute: _ =>
            {
                var pageData = new PageData
                {
                    FactoryId = TextEditorPageFactory.StaticId,
                    Context = new TextEditorPageContext
                    {
                        FileId = collectionJsonFile.AsLibraryFile().LibraryFileId,
                        FilePath = collectionJsonFile.AsLibraryFile().FileName,
                        IsReadOnly = true,
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = new OpenPageBehavior.NewTab(PanelId);
                workspaceController.OpenPage(WorkspaceId, pageData, behavior);
            }
        );

        CommandDownloadModUpdates = new ReactiveCommand<Unit>(
            executeAsync: async (_, cancellationToken) =>
            {
                if (!await loginManager.EnsureLoggedIn("Download mod updates", cancellationToken)) return;

                await modUpdateDownloader.DownloadAll(GetNewestFilesForCollectionMods(), cancellationToken);
            },
            awaitOperation: AwaitOperation.Drop,
            configureAwait: false
        );

        CommandViewCollection = IsInstalled.ToReactiveCommand<NavigationInformation>(info =>
            {
                var group = CollectionDownloader.GetCollectionGroup(_revision, _targetLoadout, connection.Db).Value;

                var pageData = new PageData
                {
                    FactoryId = CollectionLoadoutPageFactory.StaticId,
                    Context = new CollectionLoadoutPageContext
                    {
                        LoadoutId = _targetLoadout,
                        GroupId = group.AsCollectionGroup(),
                    },
                };

                var workspaceController = GetWorkspaceController();
                var behavior = workspaceController.GetOpenPageBehavior(pageData, info);
                workspaceController.OpenPage(WorkspaceId, pageData, behavior);
            }
        );

        IsDownloading = _isDownloadingRequiredItems.CombineLatest(_isDownloadingOptionalItems, static (a, b) => a || b).ToBindableReactiveProperty();
        IsUpdateAvailable = NewestRevisionNumber.Select(static optional => optional.HasValue).ToBindableReactiveProperty();

        // Cancelling the job is enough to stop the run: the wait on each mod's download page
        // takes the job's token, so the mod in progress gives up its place in the queue, and
        // the browser session the run holds closes the tab it was driving on its way out.
        CommandCancelDownload = IsDownloading.ToReactiveCommand<Unit>(
            execute: _ =>
            {
                foreach (var job in jobMonitor.Jobs.ToArray())
                {
                    if (job.Definition is not DownloadCollectionJob definition) continue;
                    if (definition.RevisionMetadata.Id != _revision.Id) continue;
                    if (!job.Status.IsActive()) continue;

                    jobMonitor.Cancel(job.Id);
                }
            }
        );

        CommandUpdateCollection = IsUpdateAvailable.ToReactiveCommand<Unit>(
            executeAsync: async (_, cancellationToken) =>
            {
                var newestRevisionNumber = NewestRevisionNumber.Value.Value;
                var revision = await collectionDownloader.GetOrAddRevision(_collection.Slug, newestRevisionNumber, cancellationToken);

                var pageData = new PageData
                {
                    FactoryId = CollectionDownloadPageFactory.StaticId,
                    Context = new CollectionDownloadPageContext
                    {
                        TargetLoadout = targetLoadout,
                        CollectionRevisionMetadataId = revision,
                    },
                };

                var workspaceController = GetWorkspaceController();
                workspaceController.OpenPage(WorkspaceId, pageData, new OpenPageBehavior.ReplaceTab(PanelId, TabId));
            }, awaitOperation: AwaitOperation.Drop, configureAwait: false
        );

        this.WhenActivated(disposables =>
            {
                TreeDataGridAdapter.Activate().AddTo(disposables);

                jobMonitor
                    .HasActiveJob<InstallCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id)
                    .OnUI()
                    .Subscribe(isInstalling => IsInstalling.Value = isInstalling)
                    .AddTo(disposables);

                jobMonitor
                    .HasActiveJob<DownloadCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id && job.ItemType == CollectionDownloader.ItemType.Required)
                    .OnUI()
                    .Subscribe(isDownloading => _isDownloadingRequiredItems.OnNext(isDownloading))
                    .AddTo(disposables);

                jobMonitor
                    .HasActiveJob<DownloadCollectionJob>(job => job.RevisionMetadata.Id == _revision.Id && job.ItemType == CollectionDownloader.ItemType.Optional)
                    .OnUI()
                    .Subscribe(isDownloading => _isDownloadingOptionalItems.OnNext(isDownloading))
                    .AddTo(disposables);

                var numDownloadedRequiredItemsObservable = Observable
                    .Return(_revision)
                    .OffUi()
                    .SelectMany(revision => collectionDownloader.DownloadedItemCountObservable(revision, itemType: CollectionDownloader.ItemType.Required));

                var numDownloadedOptionalItemsObservable = Observable
                    .Return(_revision)
                    .OffUi()
                    .SelectMany(revision => collectionDownloader.DownloadedItemCountObservable(revision, itemType: CollectionDownloader.ItemType.Optional));

                var collectionGroupObservable = collectionDownloader.GetCollectionGroupObservable(_revision, _targetLoadout);
                var isCollectionInstalledObservable = collectionDownloader
                    .IsCollectionInstalledObservable(_revision, collectionGroupObservable)
                    .Prepend(false);
                var hasInstalledAllOptionalItems = collectionDownloader
                    .IsCollectionInstalledObservable(_revision, collectionGroupObservable, CollectionDownloader.ItemType.Optional)
                    .Prepend(false);

                numDownloadedRequiredItemsObservable.CombineLatest(isCollectionInstalledObservable)
                    .OnUI()
                    .Subscribe(tuple =>
                        {
                            var (numDownloadedRequiredItems, isCollectionInstalled) = tuple;
                            var hasDownloadedAllRequiredItems = numDownloadedRequiredItems == RequiredDownloadsCount;

                            CountDownloadedRequiredItems = numDownloadedRequiredItems;
                            _canInstallRequiredItems.OnNext(!isCollectionInstalled && hasDownloadedAllRequiredItems);
                            _canDownloadRequiredItems.OnNext(!hasDownloadedAllRequiredItems);

                            if (hasDownloadedAllRequiredItems)
                            {
                                if (isCollectionInstalled)
                                {
                                    if (!IsInstalled.Value)
                                    {
                                        IsInstalled.Value = true;
                                    }
                                    
                                    CollectionStatusText = Language.CollectionDownloadViewModel_CollectionDownloadViewModel_Ready_to_play___All_required_mods_installed;
                                }
                                else
                                {
                                    IsInstalled.Value = false;
                                    CollectionStatusText = Language.CollectionDownloadViewModel_Ready_to_install;
                                }
                            }
                            else
                            {
                                IsInstalled.Value = false;
                                CollectionStatusText = string.Format(Language.CollectionDownloadViewModel_Num_required_mods_downloaded, numDownloadedRequiredItems, RequiredDownloadsCount);
                            }
                        }
                    ).AddTo(disposables);

                numDownloadedOptionalItemsObservable
                    .CombineLatest(hasInstalledAllOptionalItems)
                    .OnUI()
                    .Subscribe(tuple =>
                        {
                            var (numDownloadedOptionalItems, hasInstalledAllOptionals) = tuple;
                            var hasDownloadedAllOptionalItems = numDownloadedOptionalItems == OptionalDownloadsCount;

                            CountDownloadedOptionalItems = numDownloadedOptionalItems;
                            HasInstalledAllOptionalItems.Value = hasInstalledAllOptionals;
                            _canInstallOptionalItems.OnNext(hasDownloadedAllOptionalItems && !hasInstalledAllOptionals);
                            _canDownloadOptionalItems.OnNext(!hasDownloadedAllOptionalItems);
                        }
                    ).AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Id, tileImagePipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.TileImage = bitmap)
                    .AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Id, backgroundImagePipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.BackgroundImage = bitmap)
                    .AddTo(disposables);

                ImagePipelines.CreateObservable(_collection.Author.Id, userAvatarPipeline)
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (bitmap, self) => self.AuthorAvatar = bitmap)
                    .AddTo(disposables);

                TreeDataGridAdapter.MessageSubject.SubscribeAwait(
                    onNextAsync: (message, cancellationToken) =>
                    {
                        return message.Match<ValueTask>(
                            f0: installMessage => InstallItem(installMessage.DownloadEntity, cancellationToken),
                            // Whether the download got started is only of interest to a
                            // caller working through a list of them; here the one row the
                            // user pressed reports for itself.
                            f1: async downloadNexusMods => await collectionDownloader.Download(downloadNexusMods.DownloadEntity, cancellationToken),
                            f2: downloadExternal => collectionDownloader.Download(downloadExternal.DownloadEntity, cancellationToken),
                            f3: manualDownloadOpenModal => OpenManualDownloadModal(manualDownloadOpenModal.DownloadEntity)
                        );
                    },
                    awaitOperation: AwaitOperation.Parallel,
                    configureAwait: false
                ).AddTo(disposables);

                R3.Observable.Return(_revision)
                    .ObserveOnThreadPool()
                    .SelectAwait((revision, cancellationToken) => nexusModsLibrary.GetLastPublishedRevisionNumber(revision.Collection, cancellationToken))
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe(this, static (graphQlResult, self) =>
                        {
                            // TODO: handle errors
                            var lastPublishedRevisionNumber = graphQlResult.AssertHasData();
                            if (!lastPublishedRevisionNumber.HasValue)
                            {
                                self.IsUpdateAvailable.Value = false;
                                self.NewestRevisionNumber.Value = Optional<RevisionNumber>.None;
                            }
                            else
                            {
                                var isUpdateAvailable = lastPublishedRevisionNumber.Value > self._revision.RevisionNumber;

                                self.IsUpdateAvailable.Value = isUpdateAvailable;
                                self.NewestRevisionNumber.Value = isUpdateAvailable ? lastPublishedRevisionNumber : Optional<RevisionNumber>.None;
                            }
                        }
                    ).AddTo(disposables);

                R3.Observable.Return(collectionJsonFile)
                    .ObserveOnThreadPool()
                    .SelectAwait((jsonFile, cancellationToken) => nexusModsLibrary.ParseCollectionJsonFile(jsonFile, cancellationToken))
                    .ObserveOnUIThreadDispatcher()
                    .Subscribe((this, serviceProvider), static (collectionRoot, state) =>
                        {
                            var (self, serviceProvider) = state;

                            var collectionInstructionsText = collectionRoot.Info.InstallInstructions;

                            var modsInstructions = collectionRoot.Mods
                                .Select(static mod => (mod.Name, mod.Instructions, mod.Optional))
                                .Where(static tuple => !string.IsNullOrWhiteSpace(tuple.Instructions))
                                .Select(static tuple =>
                                    new ModInstructions(tuple.Name, tuple.Instructions, tuple.Optional ? CollectionDownloader.ItemType.Optional : CollectionDownloader.ItemType.Required)
                                )
                                .ToArray();

                            var optionalModsInstructions = modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Optional).ToArray();
                            var requiredModsInstructions = modsInstructions.Where(static x => x.ItemType == CollectionDownloader.ItemType.Required).ToArray();

                            if (!string.IsNullOrWhiteSpace(collectionInstructionsText))
                            {
                                var markdownRendererViewModel = serviceProvider.GetRequiredService<IMarkdownRendererViewModel>();
                                markdownRendererViewModel.Contents = collectionInstructionsText;
                                self.InstructionsRenderer = markdownRendererViewModel;
                            }

                            self.RequiredModsInstructions = requiredModsInstructions;
                            self.OptionalModsInstructions = optionalModsInstructions;
                        }
                    ).AddTo(disposables);
            }
        );
    }

    /// <summary>
    /// The newest file of every mod this collection pins that has a newer one available.
    /// </summary>
    /// <remarks>
    /// Restricted to the files the collection actually uses. A mod page can hold files this
    /// collection never asked for, and an update to one of those is nothing to do with it.
    /// </remarks>
    private NexusModsFileMetadata.ReadOnly[] GetNewestFilesForCollectionMods()
    {
        var modUpdateService = _serviceProvider.GetRequiredService<IModUpdateService>();

        var pinnedFileIds = new HashSet<EntityId>();
        var modPages = new Dictionary<EntityId, NexusModsModPageMetadata.ReadOnly>();

        foreach (var download in _revision.Downloads)
        {
            if (!download.TryGetAsCollectionDownloadNexusMods(out var nexusModsDownload)) continue;

            var file = nexusModsDownload.FileMetadata;
            pinnedFileIds.Add(file.Id);
            modPages[file.ModPage.Id] = file.ModPage;
        }

        var newestFiles = new List<NexusModsFileMetadata.ReadOnly>();
        foreach (var modPage in modPages.Values)
        {
            var updates = modUpdateService.HasModPageUpdatesAvailable(modPage);
            if (!updates.HasValue) continue;

            foreach (var mapping in updates.Value.FileMappings)
            {
                if (!pinnedFileIds.Contains(mapping.File.Id)) continue;
                newestFiles.Add(mapping.NewestFile);
            }
        }

        return newestFiles.DistinctBy(static file => file.Id).ToArray();
    }

    private ValueTask OpenManualDownloadModal(CollectionDownloadExternal.ReadOnly downloadEntity)
    {
        _overlayController.Enqueue(new ManualDownloadRequiredOverlayViewModel(_serviceProvider, downloadEntity));
        return ValueTask.CompletedTask;
    }

    private async ValueTask InstallItem(CollectionDownloadEntity.ReadOnly download, CancellationToken cancellationToken)
    {
        var monitor = _serviceProvider.GetRequiredService<IJobMonitor>();

        var job = await InstallCollectionDownloadJob.Create(
            serviceProvider: _serviceProvider,
            targetLoadout: _targetLoadout,
            download: download,
            cancellationToken: cancellationToken
        );

        await monitor.Begin<InstallCollectionDownloadJob, LoadoutItemGroup.ReadOnly>(job);
    }

    public BindableReactiveProperty<bool> IsInstalled { get; } = new(value: false);

    public BindableReactiveProperty<bool> HasInstalledAllOptionalItems { get; } = new(value: false);

    private readonly BehaviorSubject<bool> _canDownloadRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _canDownloadOptionalItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _isDownloadingRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _isDownloadingOptionalItems = new(initialValue: false);
    public BindableReactiveProperty<bool> IsDownloading { get; }

    private readonly BehaviorSubject<bool> _canInstallRequiredItems = new(initialValue: false);
    private readonly BehaviorSubject<bool> _canInstallOptionalItems = new(initialValue: false);
    public BindableReactiveProperty<bool> IsInstalling { get; } = new(value: false);

    public BindableReactiveProperty<bool> IsUpdateAvailable { get; }
    public BindableReactiveProperty<Optional<RevisionNumber>> NewestRevisionNumber { get; } = new();

    public string Name => _collection.Name;
    public string Summary => _collection.Summary.ValueOr(string.Empty);
    public ulong EndorsementCount => _collection.Endorsements.ValueOr(0ul);
    public ulong TotalDownloads => _collection.TotalDownloads.ValueOr(0ul);
    public string Category => _collection.Category.Name;
    public Size TotalSize => _revision.TotalSize.ValueOr(Size.Zero);
    public Percent OverallRating => Percent.Create(_revision.Collection.RecentRating.ValueOr(0), maximum: 100);

    public string AuthorName => _collection.Author.Name;
    public bool IsAdult => _revision.IsAdult.ValueOr(false);
    public CollectionSlug Slug => _collection.Slug;
    public RevisionNumber RevisionNumber => _revision.RevisionNumber;

    [Reactive] public IMarkdownRendererViewModel? InstructionsRenderer { get; set; }
    [Reactive] public ModInstructions[] RequiredModsInstructions { get; set; } = [];
    [Reactive] public ModInstructions[] OptionalModsInstructions { get; set; } = [];

    public int RequiredDownloadsCount { get; }
    public int OptionalDownloadsCount { get; }
    [Reactive] public int CountDownloadedOptionalItems { get; private set; }
    [Reactive] public int CountDownloadedRequiredItems { get; private set; }

    [Reactive] public Bitmap? TileImage { get; private set; }
    [Reactive] public Bitmap? BackgroundImage { get; private set; }
    [Reactive] public Bitmap? AuthorAvatar { get; private set; }
    [Reactive] public string CollectionStatusText { get; private set; } = "";

    [Reactive] public bool CanDownloadAutomatically { get; private set; }

    public ReactiveCommand<NavigationInformation> CommandViewCollection { get; }
    public ReactiveCommand<Unit> CommandDownloadRequiredItems { get; }
    public ReactiveCommand<Unit> CommandInstallRequiredItems { get; }
    public ReactiveCommand<Unit> CommandDownloadOptionalItems { get; }
    public ReactiveCommand<Unit> CommandInstallOptionalItems { get; }
    public ReactiveCommand<Unit> CommandCancelDownload { get; }
    public ReactiveCommand<Unit> CommandUpdateCollection { get; }

    public ReactiveCommand<Unit> CommandViewOnNexusMods { get; }
    public ReactiveCommand<Unit> CommandOpenJsonFile { get; }
    public ReactiveCommand<Unit> CommandDownloadModUpdates { get; }
    public ReactiveCommand<Unit> CommandDeleteAllDownloads { get; }
    public ReactiveCommand<Unit> CommandDeleteCollectionRevision { get; }
}

public readonly record struct InstallMessage(CollectionDownloadEntity.ReadOnly DownloadEntity);

public readonly record struct DownloadNexusModsMessage(CollectionDownloadNexusMods.ReadOnly DownloadEntity);

public readonly record struct DownloadExternalMessage(CollectionDownloadExternal.ReadOnly DownloadEntity);

public readonly record struct ManualDownloadOpenModal(CollectionDownloadExternal.ReadOnly DownloadEntity);

public class CollectionDownloadTreeDataGridAdapter :
    TreeDataGridAdapter<CompositeItemModel<EntityId>, EntityId>,
    ITreeDataGirdMessageAdapter<OneOf<InstallMessage, DownloadNexusModsMessage, DownloadExternalMessage, ManualDownloadOpenModal>>
{
    private readonly CollectionRevisionMetadata.ReadOnly _revisionMetadata;
    private readonly LoadoutId _targetLoadout;
    private readonly CollectionDataProvider _collectionDataProvider;

    public R3.ReactiveProperty<CollectionDownloadsFilter> Filter { get; } = new(value: CollectionDownloadsFilter.OnlyRequired);

    public Subject<OneOf<InstallMessage, DownloadNexusModsMessage, DownloadExternalMessage, ManualDownloadOpenModal>> MessageSubject { get; } = new();

    public CollectionDownloadTreeDataGridAdapter(
        IServiceProvider serviceProvider,
        CollectionRevisionMetadata.ReadOnly revisionMetadata,
        LoadoutId targetLoadout) : base(serviceProvider)
    {
        _revisionMetadata = revisionMetadata;
        _targetLoadout = targetLoadout;
        _collectionDataProvider = serviceProvider.GetRequiredService<CollectionDataProvider>();
    }

    protected override IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> GetRootsObservable(bool viewHierarchical)
    {
        return _collectionDataProvider.ObserveCollectionItems(_revisionMetadata, Filter.AsSystemObservable(), _targetLoadout);
    }

    protected override void BeforeModelActivationHook(CompositeItemModel<EntityId> model)
    {
        base.BeforeModelActivationHook(model);

        model.SubscribeToComponentAndTrack<CollectionComponents.InstallAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.InstallComponentKey,
            state: this,
            factory: static (self, _, component) => component.CommandInstall.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new InstallMessage(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.NexusModsDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.NexusModsDownloadComponentKey,
            state: this,
            factory: static (self, _, component) =>
                component.CommandDownload.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new DownloadNexusModsMessage(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.ExternalDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.ExternalDownloadComponentKey,
            state: this,
            factory: static (self, _, component) =>
                component.CommandDownload.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new DownloadExternalMessage(downloadEntity)); })
        );

        model.SubscribeToComponentAndTrack<CollectionComponents.ManualDownloadAction, CollectionDownloadTreeDataGridAdapter>(
            key: CollectionColumns.Actions.ManualDownloadComponentKey,
            state: this,
            factory: static (self, _, component) =>
                component.CommandOpenModal.Subscribe(self, static (downloadEntity, self) => { self.MessageSubject.OnNext(new ManualDownloadOpenModal(downloadEntity)); })
        );
    }

    protected override IColumn<CompositeItemModel<EntityId>>[] CreateColumns(bool viewHierarchical)
    {
        var nameColumn = ColumnCreator.Create<EntityId, SharedColumns.Name>();

        return
        [
            viewHierarchical ? ITreeDataGridItemModel<CompositeItemModel<EntityId>, EntityId>.CreateExpanderColumn(nameColumn) : nameColumn,
            ColumnCreator.Create<EntityId, LibraryColumns.ItemVersion>(),
            ColumnCreator.Create<EntityId, SharedColumns.ItemSize>(),
            ColumnCreator.Create<EntityId, CollectionColumns.Actions>(),
        ];
    }

    private bool _isDisposed;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            MessageSubject.Dispose();
            _isDisposed = true;
        }

        base.Dispose(disposing);
    }
}
