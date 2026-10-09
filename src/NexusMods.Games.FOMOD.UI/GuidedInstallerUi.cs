using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.App.UI;
using NexusMods.Sdk.Jobs;
using ReactiveUI;

namespace NexusMods.Games.FOMOD.UI;

[UsedImplicitly]
public sealed class GuidedInstallerUi : IGuidedInstaller
{
    /// <summary>
    /// Only one installer window is on screen at a time.
    /// </summary>
    /// <remarks>
    /// Collections install their mods in parallel, so several installs can reach the point of asking the
    /// user for input at the same time. Each install has its own installer, so without this they'd all
    /// put a window on screen at once.
    /// </remarks>
    private static readonly SemaphoreSlim WindowGate = new(initialCount: 1, maxCount: 1);

    private readonly IServiceProvider _serviceProvider;
    private readonly CompositeDisposable _compositeDisposable;

    private IServiceScope? _currentScope;
    private GuidedInstallerWindow? _window;
    private string _windowName = string.Empty;
    private int _holdsWindowGate;

    /// <summary>
    /// The choice the window is currently waiting on, so closing the window always resolves the install
    /// instead of leaving it waiting for an answer that can no longer arrive.
    /// </summary>
    private TaskCompletionSource<UserChoice>? _pendingChoice;

    public GuidedInstallerUi(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _compositeDisposable = new CompositeDisposable();
    }

    public void SetupInstaller(string windowName)
    {
        // NOTE(sewer): The window is created on the first call to RequestUserChoice instead of here.
        // Scripts can finish without ever asking the user anything, and waiting our turn for the one
        // window we're allowed to show has to happen without blocking the install.
        _windowName = windowName;
    }

    public void CleanupInstaller()
    {
        var window = _window;
        _window = null;

        if (window is not null)
        {
            OnUi(window, static w =>
            {
                if (w.ViewModel is not null) w.ViewModel.ActiveStepViewModel = null;
                w.Close();
            });
        }

        _currentScope?.Dispose();
        _currentScope = null;

        ReleaseWindowGate();
    }

    public async Task<UserChoice> RequestUserChoice(
        GuidedInstallationStep installationStep,
        Percent progress,
        CancellationToken cancellationToken)
    {
        var window = await EnsureWindow(cancellationToken);
        var scope = _currentScope!;

        var tcs = new TaskCompletionSource<UserChoice>();
        Interlocked.Exchange(ref _pendingChoice, tcs);

        try
        {
            OnUi((scope, window, tcs, installationStep, progress), static tuple =>
            {
                SetupStep(tuple.scope, tuple.window, tuple.tcs, tuple.installationStep, tuple.progress);
            });

            return await tcs.Task;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingChoice, null, tcs);
        }
    }

    /// <summary>
    /// Waits for our turn to show a window, then creates it on the UI thread.
    /// </summary>
    private async Task<GuidedInstallerWindow> EnsureWindow(CancellationToken cancellationToken)
    {
        if (_window is not null) return _window;

        await WindowGate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _holdsWindowGate, 1);

        _currentScope = _serviceProvider.CreateScope();
        var windowViewModel = _currentScope.ServiceProvider.GetRequiredService<IGuidedInstallerWindowViewModel>();
        windowViewModel.WindowName = _windowName;

        // NOTE(erri120): The window has to exist before we can hand it the first step, so we wait for
        // the UI thread to create it.
        var created = new TaskCompletionSource<GuidedInstallerWindow>();
        OnUi((state: this, viewModel: windowViewModel, created), static tuple =>
        {
            try
            {
                tuple.created.SetResult(tuple.state.SetupWindow(tuple.viewModel));
            }
            catch (Exception e)
            {
                tuple.created.SetException(e);
            }
        });

        _window = await created.Task;
        return _window;
    }

    private GuidedInstallerWindow SetupWindow(IGuidedInstallerWindowViewModel windowViewModel)
    {
        var window = new GuidedInstallerWindow
        {
            ViewModel = windowViewModel,
        };

        window.Show();

        Observable
            .FromEventPattern(
                addHandler => window.Closed += addHandler,
                removeHandler => window.Closed -= removeHandler
            )
            .SubscribeWithErrorLogging(_ =>
            {
                // Closing the window is how the user aborts the install, and it may also happen while a
                // step is on screen for some other reason, so the pending choice always gets an answer.
                var tcs = Interlocked.Exchange(ref _pendingChoice, null);
                tcs?.TrySetResult(new UserChoice(new UserChoice.CancelInstallation()));
            })
            .DisposeWith(_compositeDisposable);

        return window;
    }

    private static void SetupStep(
        IServiceScope currentScope,
        IViewFor<IGuidedInstallerWindowViewModel> window,
        TaskCompletionSource<UserChoice> tcs,
        GuidedInstallationStep installationStep,
        Percent progress)
    {
        var viewModel = window.ViewModel!;
        viewModel.ActiveStepViewModel ??= new GuidedInstallerStepViewModel(currentScope.ServiceProvider);

        var activeStepViewModel = viewModel.ActiveStepViewModel;
        activeStepViewModel.ModName = viewModel.WindowName;
        activeStepViewModel.InstallationStep = installationStep;
        activeStepViewModel.TaskCompletionSource = tcs;
        activeStepViewModel.Progress = progress;
    }

    private void ReleaseWindowGate()
    {
        if (Interlocked.Exchange(ref _holdsWindowGate, 0) == 0) return;
        WindowGate.Release();
    }

    private static void OnUi<TState>(TState state, Action<TState> action)
    {
        // NOTE: AvaloniaScheduler has to be used to do work on the UI thread
        AvaloniaScheduler.Instance.Schedule(
            (action, state),
            AvaloniaScheduler.Instance.Now,
            (_, tuple) =>
            {
                var (innerAction, innerState) = tuple;
                innerAction(innerState);
                return Disposable.Empty;
            });
    }

    public void Dispose()
    {
        CleanupInstaller();

        // Nothing is going to answer a step that's still waiting at this point.
        Interlocked.Exchange(ref _pendingChoice, null)?.TrySetResult(new UserChoice(new UserChoice.CancelInstallation()));

        _compositeDisposable.Dispose();
    }
}
