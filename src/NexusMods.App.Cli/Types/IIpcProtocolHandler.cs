namespace NexusMods.CLI.Types;

/// <summary>
/// Defines a protocol handler for protocols which need passing messages to the main application.
/// </summary>
public interface IIpcProtocolHandler
{
    /// <summary>
    /// The protocol to handle, e.g. 'nxm'
    /// </summary>
    public string Protocol { get; }
    
    /// <summary>
    /// Handles the given URL.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="token">Allows to cancel the operation.</param>
    /// <param name="source">
    /// Where the link came from. Defaults to <see cref="ProtocolLinkSource.External"/>, which
    /// is what a link handed over by the OS is; code inside the app that makes its own links
    /// says so, and see <see cref="ProtocolLinkSource"/> for what that changes.
    /// </param>
    public Task Handle(string url, CancellationToken token, ProtocolLinkSource source = ProtocolLinkSource.External);
}
