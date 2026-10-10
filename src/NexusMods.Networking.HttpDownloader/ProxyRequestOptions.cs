using JetBrains.Annotations;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Marks the requests that <see cref="ProxyPool"/> routes through a proxy.
/// </summary>
/// <remarks>
/// A proxy is selected per request instead of per client, so that the default
/// setting can proxy the file transfer itself without sending the account token
/// that the API calls carry through somebody else's server.
/// </remarks>
[PublicAPI]
public static class ProxyRequestOptions
{
    private static readonly HttpRequestOptionsKey<bool> FileDownloadKey = new("NexusMods.IsFileDownload");
    private static readonly HttpRequestOptionsKey<Uri> LeasedProxyKey = new("NexusMods.LeasedProxy");
    private static readonly HttpRequestOptionsKey<bool> PinnedDirectKey = new("NexusMods.PinnedDirect");

    /// <summary>
    /// Marks <paramref name="request"/> as part of a file download.
    /// </summary>
    public static void MarkAsFileDownload(HttpRequestMessage request) => request.Options.Set(FileDownloadKey, true);

    /// <summary>
    /// Whether <paramref name="request"/> was marked by <see cref="MarkAsFileDownload"/>.
    /// </summary>
    public static bool IsFileDownload(HttpRequestMessage request)
        => request.Options.TryGetValue(FileDownloadKey, out var isFileDownload) && isFileDownload;

    /// <summary>
    /// Pins <paramref name="request"/> to the proxy this download leased, instead of
    /// letting it take whichever proxy is currently the best one.
    /// </summary>
    public static void SetLeasedProxy(HttpRequestMessage request, Uri proxyAddress)
        => request.Options.Set(LeasedProxyKey, proxyAddress);

    /// <summary>
    /// The proxy <paramref name="request"/> was pinned to, if any.
    /// </summary>
    public static Uri? GetLeasedProxy(HttpRequestMessage request)
        => request.Options.TryGetValue(LeasedProxyKey, out var proxyAddress) ? proxyAddress : null;

    /// <summary>
    /// Pins <paramref name="request"/> to the local connection, for the one download that
    /// holds the direct slot while the others run on proxies.
    /// </summary>
    public static void PinToDirectConnection(HttpRequestMessage request) => request.Options.Set(PinnedDirectKey, true);

    /// <summary>
    /// Whether <paramref name="request"/> was pinned by <see cref="PinToDirectConnection"/>.
    /// </summary>
    public static bool IsPinnedToDirectConnection(HttpRequestMessage request)
        => request.Options.TryGetValue(PinnedDirectKey, out var isPinned) && isPinned;
}
