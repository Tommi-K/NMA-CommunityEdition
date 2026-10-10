using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Sends each request directly, through the proxy a download leased, or through
/// <see cref="ProxyPool"/>'s current pick, and takes a failing proxy out of rotation so
/// the retry above us lands on another one.
/// </summary>
internal sealed class ProxyRoutingHandler : HttpMessageHandler
{
    private readonly ProxyPool _proxyPool;
    private readonly ILogger<ProxyRoutingHandler> _logger;
    private readonly HttpMessageInvoker _direct;
    private readonly HttpMessageInvoker _proxied;

    // NOTE(CE): a leased proxy needs its own handler, since a handler resolves its proxy
    // once per request from a single IWebProxy and pools connections per proxy. One entry
    // per proxy the user configured, so this is bounded by the length of their list.
    private readonly ConcurrentDictionary<Uri, HttpMessageInvoker> _leasedInvokers = new();

    public ProxyRoutingHandler(ProxyPool proxyPool, ILogger<ProxyRoutingHandler> logger)
    {
        _proxyPool = proxyPool;
        _logger = logger;

        // NOTE(CE): no proxy set, so this one still honours HTTP_PROXY and friends
        _direct = new HttpMessageInvoker(new SocketsHttpHandler(), disposeHandler: true);

        _proxied = new HttpMessageInvoker(new SocketsHttpHandler
        {
            Proxy = proxyPool,
            UseProxy = true,
        }, disposeHandler: true);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // NOTE(CE): the download holding the direct slot keeps the local connection even
        // though proxying is on
        if (ProxyRequestOptions.IsPinnedToDirectConnection(request)) return _direct.SendAsync(request, cancellationToken);
        if (!_proxyPool.ShouldProxy(request)) return _direct.SendAsync(request, cancellationToken);

        var leased = ProxyRequestOptions.GetLeasedProxy(request);
        if (leased is not null) return SendThroughAsync(GetLeasedInvoker(leased), leased, request, cancellationToken);

        var current = _proxyPool.CurrentProxy;
        if (current is null) return SendWithoutProxyAsync(request, cancellationToken);

        return SendThroughAsync(_proxied, current, request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ProxyRequestOptions.IsPinnedToDirectConnection(request)) return _direct.Send(request, cancellationToken);
        if (!_proxyPool.ShouldProxy(request)) return _direct.Send(request, cancellationToken);

        var leased = ProxyRequestOptions.GetLeasedProxy(request);
        var proxy = leased ?? _proxyPool.CurrentProxy;
        if (proxy is null)
        {
            EnsureDirectAllowed(request);
            return _direct.Send(request, cancellationToken);
        }

        var invoker = leased is not null ? GetLeasedInvoker(leased) : _proxied;

        try
        {
            var response = invoker.Send(request, cancellationToken);
            _proxyPool.ReportSuccess(proxy);
            return response;
        }
        catch (Exception e) when (IsProxyFailure(e, cancellationToken))
        {
            _proxyPool.ReportFailure(proxy, e);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendThroughAsync(
        HttpMessageInvoker invoker,
        Uri proxy,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _proxyPool.ReportSuccess(proxy);
            return response;
        }
        catch (Exception e) when (IsProxyFailure(e, cancellationToken))
        {
            _proxyPool.ReportFailure(proxy, e);
            throw;
        }
    }

    private HttpMessageInvoker GetLeasedInvoker(Uri address)
    {
        return _leasedInvokers.GetOrAdd(address, static (key, pool) => new HttpMessageInvoker(new SocketsHttpHandler
        {
            Proxy = new WebProxy(key)
            {
                Credentials = pool.GetCredential(key),
            },
            UseProxy = true,
        }, disposeHandler: true), _proxyPool);
    }

    private async Task<HttpResponseMessage> SendWithoutProxyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureDirectAllowed(request);
        return await _direct.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureDirectAllowed(HttpRequestMessage request)
    {
        if (!_proxyPool.AllowDirectFallback)
        {
            throw new HttpRequestException(
                "None of the configured proxies are working and falling back to a direct connection is turned off. " +
                "Check the proxy list in Settings, or allow direct connections."
            );
        }

        _logger.LogWarning("No working proxy for `{Uri}`, connecting directly", request.RequestUri);
    }

    private static bool IsProxyFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        return exception switch
        {
            HttpRequestException => true,
            HttpIOException => true,
            SocketException => true,
            IOException => true,
            // NOTE(CE): a connect timeout to the proxy surfaces as a cancellation
            OperationCanceledException => true,
            _ => false,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _direct.Dispose();
            _proxied.Dispose();

            foreach (var invoker in _leasedInvokers.Values) invoker.Dispose();
            _leasedInvokers.Clear();
        }

        base.Dispose(disposing);
    }
}
