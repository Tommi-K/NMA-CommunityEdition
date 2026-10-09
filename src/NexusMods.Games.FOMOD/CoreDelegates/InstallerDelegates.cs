using FomodInstaller.Interface;
using FomodInstaller.Interface.ui;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;

namespace NexusMods.Games.FOMOD.CoreDelegates;

[UsedImplicitly]
public sealed class InstallerDelegates : ICoreDelegates, IDisposable
{
    public IContextDelegates context { get; }
    public IIniDelegates ini => throw new NotImplementedException();
    public IPluginDelegates plugin { get; }

    public IUIDelegates ui => UiDelegates;
    public UiDelegates UiDelegates;

    /// <summary>
    /// Tears down the UI delegates, which closes the installer window and frees its scope if one was opened.
    /// </summary>
    public void Dispose() => UiDelegates.Dispose();

    public InstallerDelegates(
        ILoggerFactory loggerFactory,
        IGuidedInstaller guidedInstaller)
    {
        context = new ContextDelegates(loggerFactory.CreateLogger<ContextDelegates>());
        plugin = new PluginDelegates(loggerFactory.CreateLogger<PluginDelegates>());
        UiDelegates = new UiDelegates(
            loggerFactory.CreateLogger<UiDelegates>(),
            guidedInstaller
        );
    }
}
