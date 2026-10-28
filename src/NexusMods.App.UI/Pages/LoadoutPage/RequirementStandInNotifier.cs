using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Networking.NexusWebApi.Requirements;
using NexusMods.UI.Sdk;

namespace NexusMods.App.UI.Pages.LoadoutPage;

/// <summary>
/// Tells the user when a copy installed for a requirement switches itself off or back on, which
/// would otherwise look like a mod changing by itself.
/// </summary>
[UsedImplicitly]
internal sealed class RequirementStandInNotifier : IHostedService
{
    private readonly ILogger<RequirementStandInNotifier> _logger;
    private readonly IRequirementStandIns _standIns;
    private readonly IWindowNotificationService _notificationService;

    private IDisposable? _subscription;

    public RequirementStandInNotifier(
        ILogger<RequirementStandInNotifier> logger,
        IRequirementStandIns standIns,
        IWindowNotificationService notificationService)
    {
        _logger = logger;
        _standIns = standIns;
        _notificationService = notificationService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _standIns.Changes.Subscribe(change =>
        {
            var message = change.Action switch
            {
                StandInAction.StandDown when change.OtherCopyPlace is not null =>
                    $"Switched off the copy of “{change.Name}” installed for a requirement: the one in “{change.OtherCopyPlace}” is on.",
                StandInAction.StandDown =>
                    $"Switched off the copy of “{change.Name}” installed for a requirement: another copy of it is on.",
                StandInAction.ComeBack =>
                    $"Switched “{change.Name}” back on: it was installed for a requirement, and no other copy of it is on now.",
                _ => null,
            };

            if (message is null) return;

            // Without a window, as when the app only runs commands, there is nobody to tell.
            try
            {
                _notificationService.ShowToast(message);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Couldn't show a toast");
            }
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
