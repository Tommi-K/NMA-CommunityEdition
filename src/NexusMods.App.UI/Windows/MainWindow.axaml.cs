using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Platform;
using NexusMods.App.UI.Extensions;
using NexusMods.App.UI.WorkspaceSystem;
using ReactiveUI;

namespace NexusMods.App.UI.Windows;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {        
        ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 44; // 44 pixels drag height to match the top bar
        
        InitializeComponent();

        #if DEBUG
        this.AttachDevTools();
        #endif

        this.WhenActivated(disposables =>
        {
            var storageProvider = GetTopLevel(this)?.StorageProvider;
            var clipboard = GetTopLevel(this)?.Clipboard;
            
            if (storageProvider is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterStorageProvider.Execute(storageProvider).Subscribe();
                    })
                    .DisposeWith(disposables);
            }
            
            if (clipboard is not null)
            {
                this.WhenAnyValue(view => view.ViewModel)
                    .WhereNotNull()
                    .SubscribeWithErrorLogging(vm =>
                    {
                        using var _ = vm.RegisterClipboard.Execute(clipboard).Subscribe();
                    })
                    .DisposeWith(disposables);
            }

            this.OneWayBind(ViewModel, vm => vm.TopBar, v => v.TopBarViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Spine, v => v.SpineViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.DevelopmentBuildBanner, v => v.DevelopmentBuildBanner.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.WorkspaceController.ActiveWorkspace, view => view.WorkspaceView.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.LeftMenu)
                .BindTo(this, view => view.LeftMenuViewModelHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .BindTo(this, view => view.OverlayViewHost.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.CurrentOverlay)
                .Select(content => content != null)
                .BindTo(this, view => view.OverlayBorder.IsVisible)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.IsActive)
                .BindTo(this, view => view.ViewModel!.IsActive)
                .DisposeWith(disposables);

            // the mouse's back and forward buttons. Tunnel, so nothing under the pointer
            // takes the release for a click of its own first.
            this.AddDisposableHandler(PointerReleasedEvent, OnPointerReleasedForHistory, RoutingStrategies.Tunnel)
                .DisposeWith(disposables);

            // nothing nearer the pointer took it: step the selected tab, as the top bar's buttons do
            this.AddDisposableHandler(HistoryNavigation.RequestedEvent, (_, e) =>
            {
                e.Handled = true;
                if (ViewModel?.WorkspaceController.ActiveWorkspace?.SelectedTab is { } tab)
                    HistoryNavigation.Step(tab, e.IsForward);
            }).DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.BringWindowToFront)
                .OnUI()
                .Where(static shouldBringToFront => shouldBringToFront)
                .Subscribe(_ => {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;

                    Activate();
                }).DisposeWith(disposables);
        });
    }

    private void OnPointerReleasedForHistory(object? sender, PointerReleasedEventArgs e)
    {
        if (HistoryNavigation.IsForward(e.InitialPressMouseButton) is not { } forward) return;
        e.Handled = true;

        // A dialog covers the workspace, and stepping the page behind it isn't what anyone means.
        if (ViewModel?.CurrentOverlay is not null) return;
        if (e.Source is not Interactive source) return;

        source.RaiseEvent(new HistoryNavigationEventArgs(forward));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ViewModel?.OnClose();
        base.OnClosing(e);
    }
    
    
}
