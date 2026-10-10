using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using R3;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Holds the configured proxies, keeps track of which of them work, and hands the
/// current pick to <see cref="System.Net.Http.SocketsHttpHandler"/>.
/// </summary>
/// <remarks>
/// Health checking is what makes a list of proxies usable. An entry has to complete a
/// real HTTPS request with a certificate that validates, which rules out anything that
/// intercepts TLS, and the survivors then have their bandwidth measured, because a proxy
/// that answers a small request in 200ms can still deliver nothing afterwards. Entries
/// that fail mid-download are skipped and retried later.
/// </remarks>
[PublicAPI]
public sealed class ProxyPool : IWebProxy, IDisposable
{
    private static readonly Uri ProbeUri = new("https://www.nexusmods.com/favicon.ico");
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan MinimumReprobeInterval = TimeSpan.FromMinutes(2);
    private const int MaxFailuresBeforeSkip = 2;
    private const int MaxRemoteEntries = 200;
    private const int MaxProbeConcurrency = 8;

    // NOTE(CE): measuring bandwidth costs bandwidth, so only the entries that survive the
    // cheap reachability stage are measured, one at a time so they don't compete for the
    // local connection and understate each other
    private const int MaxThroughputProbes = 10;
    private const int ThroughputProbeBytes = 256 * 1024;
    private const long MinimumThroughputKbps = 24;
    private static readonly Uri ThroughputProbeUri = new($"https://speed.cloudflare.com/__down?bytes={ThroughputProbeBytes}");
    private static readonly TimeSpan ThroughputProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<ProxyPool> _logger;
    private readonly Lazy<ISettingsManager?> _settingsManager;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly object _initializationLock = new();
    private readonly object _leaseLock = new();

    private IDisposable? _settingsSubscription;
    private volatile bool _isInitialized;
    private volatile PoolState _state = PoolState.Disabled;
    private long _lastProbeTicks;
    private int _directLeases;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ProxyPool(ILogger<ProxyPool> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        Credentials = new ProxyCredentialProvider(this);

        // NOTE(CE): resolved lazily, this runs while the HttpClient singleton is being
        // built and the settings manager isn't necessarily ready that early
        _settingsManager = new Lazy<ISettingsManager?>(() =>
        {
            try
            {
                return serviceProvider.GetService(typeof(ISettingsManager)) as ISettingsManager;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to resolve the settings manager, proxy support stays off");
                return null;
            }
        });
    }

    /// <summary>
    /// Whether the user enabled proxy support.
    /// </summary>
    public bool IsEnabled => EnsureInitialized().IsEnabled;

    /// <summary>
    /// Whether requests may connect directly when no proxy works.
    /// </summary>
    public bool AllowDirectFallback => EnsureInitialized().AllowDirectFallback;

    /// <summary>
    /// The proxy that requests are currently sent through, or <c>null</c> when none is usable.
    /// </summary>
    public Uri? CurrentProxy
    {
        get
        {
            var state = EnsureInitialized();
            return state.IsEnabled ? SelectCandidate(state)?.Endpoint.Address : null;
        }
    }

    /// <summary>
    /// Whether this request is one we route through a proxy.
    /// </summary>
    public bool ShouldProxy(HttpRequestMessage request)
    {
        var state = EnsureInitialized();
        if (!state.IsEnabled) return false;
        return state.ProxyAllTraffic || ProxyRequestOptions.IsFileDownload(request);
    }

    /// <inheritdoc/>
    public ICredentials? Credentials { get; set; }

    /// <inheritdoc/>
    public Uri? GetProxy(Uri destination)
    {
        var state = EnsureInitialized();
        if (!state.IsEnabled) return null;
        return SelectCandidate(state)?.Endpoint.Address;
    }

    /// <inheritdoc/>
    public bool IsBypassed(Uri host)
    {
        var state = EnsureInitialized();
        if (!state.IsEnabled) return true;
        if (IsLocalDestination(host)) return true;
        return SelectCandidate(state) is null;
    }

    /// <summary>
    /// Claims a proxy for one download. The fastest proxy that nothing else is using
    /// is handed out, so concurrent downloads end up on different proxies.
    /// </summary>
    /// <remarks>
    /// When every usable proxy is already taken, the one with the fewest downloads on
    /// it is shared rather than making the caller wait, since a list can easily hold
    /// fewer proxies than there are parallel downloads. Check
    /// <see cref="ProxyLease.IsExclusive"/> to tell the two cases apart. Returns
    /// <c>null</c> when proxy support is off or nothing in the list works.
    /// </remarks>
    public ProxyLease? AcquireLease()
    {
        var state = EnsureInitialized();
        if (!state.IsEnabled) return null;

        lock (_leaseLock)
        {
            // NOTE(CE): the local connection is treated as the fastest entry in the pool
            // and handed to the first download that asks
            if (state.UseDirectSlot && _directLeases == 0)
            {
                _directLeases++;
                return new ProxyLease(this, candidate: null, isExclusive: true);
            }

            ProxyCandidate? free = null;
            ProxyCandidate? leastLoaded = null;

            // NOTE(CE): candidates are ordered fastest first, so the first unused one is
            // also the fastest unused one
            foreach (var candidate in state.Candidates)
            {
                if (!candidate.IsUsable) continue;
                if (candidate.ConsecutiveFailures >= MaxFailuresBeforeSkip) continue;

                if (candidate.ActiveLeases == 0)
                {
                    free = candidate;
                    break;
                }

                if (leastLoaded is null || candidate.ActiveLeases < leastLoaded.ActiveLeases) leastLoaded = candidate;
            }

            var chosen = free ?? leastLoaded;
            if (chosen is null)
            {
                if (state.Candidates.Length > 0) ScheduleProbe(state);
                return null;
            }

            chosen.ActiveLeases++;
            return new ProxyLease(this, chosen, isExclusive: free is not null);
        }
    }

    private void ReleaseLease(ProxyCandidate? candidate)
    {
        lock (_leaseLock)
        {
            if (candidate is null)
            {
                if (_directLeases > 0) _directLeases--;
                return;
            }

            if (candidate.ActiveLeases > 0) candidate.ActiveLeases--;
        }
    }

    /// <summary>
    /// Gets the credentials configured for the proxy at <paramref name="address"/>, if any.
    /// </summary>
    public NetworkCredential? GetCredential(Uri address) => Find(_state, address)?.Endpoint.Credential;

    /// <summary>
    /// Reports that a request through <paramref name="address"/> failed. Repeated
    /// failures take the proxy out of rotation.
    /// </summary>
    public void ReportFailure(Uri? address, Exception exception)
    {
        if (address is null) return;

        var candidate = Find(_state, address);
        if (candidate is null) return;

        candidate.LastError = exception.Message;
        var failures = Interlocked.Increment(ref candidate.ConsecutiveFailures);

        if (failures >= MaxFailuresBeforeSkip)
        {
            candidate.IsUsable = false;
            _logger.LogWarning("Proxy `{Proxy}` failed {Count} times in a row and is taken out of rotation: {Reason}", address, failures, exception.Message);
        }
        else
        {
            _logger.LogInformation("Proxy `{Proxy}` failed, trying the next one: {Reason}", address, exception.Message);
        }
    }

    /// <summary>
    /// Reports that a request through <paramref name="address"/> succeeded.
    /// </summary>
    public void ReportSuccess(Uri? address)
    {
        if (address is null) return;

        var candidate = Find(_state, address);
        if (candidate is null || candidate.ConsecutiveFailures == 0) return;
        Interlocked.Exchange(ref candidate.ConsecutiveFailures, 0);
    }

    private PoolState EnsureInitialized()
    {
        if (_isInitialized) return _state;

        lock (_initializationLock)
        {
            if (_isInitialized) return _state;
            _isInitialized = true;

            var settingsManager = _settingsManager.Value;
            if (settingsManager is null) return _state;

            try
            {
                _settingsSubscription = settingsManager
                    .GetChanges<ProxySettings>()
                    .Subscribe(settings => Apply(settings));

                Apply(settingsManager.Get<ProxySettings>());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to read the proxy settings, downloads will connect directly");
            }

            return _state;
        }
    }

    private void Apply(ProxySettings settings)
    {
        var endpoints = ProxyListParser.Parse(settings.ProxyList, out var errors);
        foreach (var error in errors)
        {
            _logger.LogWarning("Ignoring an entry in the proxy list. {Error}", error);
        }

        var state = new PoolState
        {
            IsEnabled = settings.EnableProxy,
            ProxyAllTraffic = settings.ProxyAllTraffic,
            AllowDirectFallback = settings.AllowDirectFallback,
            // NOTE(CE): handing a download the local connection is still a direct
            // connection, so it is off when those are disallowed
            UseDirectSlot = settings.KeepOneDownloadDirect && settings.AllowDirectFallback,
            Candidates = endpoints.Select(static endpoint => new ProxyCandidate(endpoint)).ToArray(),
        };

        _state = state;

        if (!state.IsEnabled)
        {
            _logger.LogInformation("Proxy support is off, downloads connect directly");
            return;
        }

        _logger.LogInformation("Proxy support is on with {Count} proxies configured, checking which of them work", state.Candidates.Length);
        _ = RefreshAsync(state, settings.ProxyListUrl?.Trim() ?? string.Empty);
    }

    private async Task RefreshAsync(PoolState state, string listUrl)
    {
        var cancellationToken = _cancellationTokenSource.Token;

        try
        {
            if (listUrl.Length > 0)
            {
                var remote = await FetchRemoteListAsync(listUrl, cancellationToken).ConfigureAwait(false);
                if (remote.Length > 0 && ReferenceEquals(_state, state))
                {
                    var known = state.Candidates.Select(static x => x.Endpoint.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var additional = remote
                        .Where(endpoint => known.Add(endpoint.ToString()))
                        .Select(static endpoint => new ProxyCandidate(endpoint))
                        .ToArray();

                    state = state with { Candidates = [..state.Candidates, ..additional] };
                    _state = state;

                    _logger.LogInformation("Added {Count} proxies from `{Url}`", additional.Length, listUrl);
                }
            }

            await ProbeAllAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to check the configured proxies");
        }
    }

    private ProxyCandidate? SelectCandidate(PoolState state)
    {
        foreach (var candidate in state.Candidates)
        {
            if (!candidate.IsUsable) continue;
            if (candidate.ConsecutiveFailures >= MaxFailuresBeforeSkip) continue;
            return candidate;
        }

        if (state.Candidates.Length > 0) ScheduleProbe(state);
        return null;
    }

    private void ScheduleProbe(PoolState state)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastProbeTicks);

        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < MinimumReprobeInterval) return;
        if (Interlocked.CompareExchange(ref _lastProbeTicks, now, last) != last) return;

        _logger.LogInformation("No usable proxy left, re-checking all {Count} of them", state.Candidates.Length);
        _ = ProbeAllAsync(state, _cancellationTokenSource.Token);
    }

    private async Task ProbeAllAsync(PoolState state, CancellationToken cancellationToken)
    {
        if (state.Candidates.Length == 0) return;
        Interlocked.Exchange(ref _lastProbeTicks, Stopwatch.GetTimestamp());

        using var semaphore = new SemaphoreSlim(initialCount: MaxProbeConcurrency, maxCount: MaxProbeConcurrency);

        await Task.WhenAll(state.Candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        })).ConfigureAwait(false);

        if (!ReferenceEquals(_state, state)) return;

        // NOTE(CE): latency says nothing about bandwidth, a proxy can answer a HEAD request
        // in 200ms and then deliver nothing, so the survivors are measured before one is picked
        var finalists = state.Candidates
            .Where(static candidate => candidate.IsUsable)
            .OrderBy(static candidate => candidate.LatencyMs)
            .Take(MaxThroughputProbes)
            .ToArray();

        foreach (var candidate in finalists)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await MeasureThroughputAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        if (!ReferenceEquals(_state, state)) return;

        var ordered = state.Candidates
            .OrderByDescending(static candidate => candidate.IsUsable)
            .ThenByDescending(static candidate => candidate.ThroughputKbps)
            .ThenBy(static candidate => candidate.LatencyMs)
            .ToArray();

        _state = state with { Candidates = ordered };

        foreach (var candidate in ordered)
        {
            if (!candidate.IsUsable)
            {
                _logger.LogDebug("Proxy `{Proxy}` is unusable: {Reason}", candidate.Endpoint, candidate.LastError);
            }
            else if (candidate.ThroughputKbps >= 0)
            {
                _logger.LogDebug("Proxy `{Proxy}`: {Throughput} KB/s, {Latency} ms", candidate.Endpoint, candidate.ThroughputKbps, candidate.LatencyMs);
            }
            else
            {
                _logger.LogDebug("Proxy `{Proxy}`: reachable in {Latency} ms, bandwidth not measured", candidate.Endpoint, candidate.LatencyMs);
            }
        }

        var usable = ordered.Where(static candidate => candidate.IsUsable).ToArray();
        if (usable.Length == 0)
        {
            _logger.LogWarning(
                "None of the {Count} configured proxies passed the health check, downloads will {Behaviour}",
                ordered.Length,
                _state.AllowDirectFallback ? "connect directly" : "fail until one works"
            );
        }
        else
        {
            var best = usable[0];
            _logger.LogInformation(
                "{Usable} of {Total} proxies passed the health check, downloads go through `{Proxy}` ({Speed})",
                usable.Length, ordered.Length, best.Endpoint,
                best.ThroughputKbps >= 0 ? $"{best.ThroughputKbps} KB/s" : $"{best.LatencyMs} ms, bandwidth not measured"
            );
        }
    }

    private async Task ProbeAsync(ProxyCandidate candidate, CancellationToken cancellationToken)
    {
        var isTlsIntercepted = false;

        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(candidate.Endpoint.Address)
            {
                Credentials = candidate.Endpoint.Credential,
            },
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = ProbeTimeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                // NOTE(CE): the default callback would reject this too, this one only exists
                // so we can tell the user why the proxy was rejected
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    if (errors == SslPolicyErrors.None) return true;
                    isTlsIntercepted = true;
                    return false;
                },
            },
        };

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ProbeTimeout,
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ProbeUri);
            request.Headers.UserAgent.Add(ApplicationConstants.UserAgent);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var elapsed = stopwatch.ElapsedMilliseconds;

            // NOTE(CE): the probe is HTTPS, so the proxy has to open a CONNECT tunnel and
            // can't answer on the origin's behalf without failing the certificate check
            // above. Any response we can read is therefore proof that the tunnel works,
            // whatever the origin thinks of the request itself.
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                MarkUnusable(candidate, "it needs credentials, add them as `scheme://user:password@host:port`");
                return;
            }

            // NOTE(CE): the status itself is not judged. A direct request gets the same 403
            // from the bot filter in front of the site, so anything other than a proxy
            // demanding authentication tells us nothing about the proxy.
            candidate.LatencyMs = elapsed;
            candidate.IsUsable = true;
            candidate.LastError = null;
            Interlocked.Exchange(ref candidate.ConsecutiveFailures, 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var reason = isTlsIntercepted
                ? "it intercepts TLS and its certificate isn't trusted"
                : e is OperationCanceledException
                    ? $"it didn't respond within {ProbeTimeout.TotalSeconds:0} seconds"
                    : e.InnerException?.Message ?? e.Message;

            MarkUnusable(candidate, reason);
        }
    }

    private async Task MeasureThroughputAsync(ProxyCandidate candidate, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(candidate.Endpoint.Address)
            {
                Credentials = candidate.Endpoint.Credential,
            },
            UseProxy = true,
            ConnectTimeout = ProbeTimeout,
        };

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = ThroughputProbeTimeout,
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();

            using var response = await client.GetAsync(ThroughputProbeUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = stopwatch.Elapsed;

            var kilobytesPerSecond = elapsed.TotalSeconds > 0
                ? (long)(body.Length / 1024d / elapsed.TotalSeconds)
                : 0;

            if (body.Length < ThroughputProbeBytes)
            {
                MarkUnusable(candidate, $"it delivered only {body.Length / 1024} of {ThroughputProbeBytes / 1024} KB");
                return;
            }

            if (kilobytesPerSecond < MinimumThroughputKbps)
            {
                MarkUnusable(candidate, $"it only manages {kilobytesPerSecond} KB/s");
                return;
            }

            candidate.ThroughputKbps = kilobytesPerSecond;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var reason = e is OperationCanceledException
                ? $"it couldn't deliver {ThroughputProbeBytes / 1024} KB within {ThroughputProbeTimeout.TotalSeconds:0} seconds"
                : e.InnerException?.Message ?? e.Message;

            MarkUnusable(candidate, reason);
        }
    }

    private async Task<ProxyEndpoint[]> FetchRemoteListAsync(string listUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(listUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _logger.LogWarning("`{Url}` isn't a valid proxy list URL", listUrl);
            return [];
        }

        // NOTE(CE): deliberately unproxied, this is how the list is bootstrapped
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 1024 * 1024,
        };

        var text = await client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        var endpoints = ProxyListParser.Parse(text, out var errors, isRemote: true);

        if (errors.Length > 0) _logger.LogInformation("Skipped {Count} entries from `{Url}` that couldn't be parsed", errors.Length, uri);

        if (endpoints.Length > MaxRemoteEntries)
        {
            _logger.LogWarning("`{Url}` listed {Count} proxies, only the first {Max} are used", uri, endpoints.Length, MaxRemoteEntries);
            endpoints = endpoints[..MaxRemoteEntries];
        }

        return endpoints;
    }

    private static void MarkUnusable(ProxyCandidate candidate, string reason)
    {
        candidate.IsUsable = false;
        candidate.LastError = reason;
        candidate.LatencyMs = long.MaxValue;
        candidate.ThroughputKbps = -1;
    }

    private static ProxyCandidate? Find(PoolState state, Uri address)
    {
        foreach (var candidate in state.Candidates)
        {
            if (candidate.Endpoint.Address.Equals(address)) return candidate;
        }

        return null;
    }

    private static bool IsLocalDestination(Uri destination)
    {
        var host = destination.Host;
        if (host.Length == 0) return true;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;

        if (!IPAddress.TryParse(host, out var address))
        {
            // NOTE(CE): single label host names can only be on the local network
            return !host.Contains('.');
        }

        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _settingsSubscription?.Dispose();

        try
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
        }
        catch (Exception)
        {
            // nothing useful to do while shutting down
        }
    }

    private sealed record PoolState
    {
        public static readonly PoolState Disabled = new()
        {
            IsEnabled = false,
            ProxyAllTraffic = false,
            AllowDirectFallback = true,
            UseDirectSlot = false,
            Candidates = [],
        };

        public required bool IsEnabled { get; init; }
        public required bool ProxyAllTraffic { get; init; }
        public required bool AllowDirectFallback { get; init; }
        public required bool UseDirectSlot { get; init; }
        public required ProxyCandidate[] Candidates { get; init; }
    }

    internal sealed class ProxyCandidate(ProxyEndpoint endpoint)
    {
        public ProxyEndpoint Endpoint { get; } = endpoint;

        public long LatencyMs = long.MaxValue;
        public long ThroughputKbps = -1;
        public int ConsecutiveFailures;
        public int ActiveLeases;
        public volatile bool IsUsable = true;
        public volatile string? LastError;
    }

    /// <summary>
    /// A claim on one proxy, held for as long as a download runs.
    /// </summary>
    [PublicAPI]
    public sealed class ProxyLease : IDisposable
    {
        private readonly ProxyPool _pool;
        private readonly ProxyCandidate? _candidate;
        private int _isReleased;

        internal ProxyLease(ProxyPool pool, ProxyCandidate? candidate, bool isExclusive)
        {
            _pool = pool;
            _candidate = candidate;
            IsExclusive = isExclusive;
        }

        /// <summary>
        /// Whether this lease is the local connection rather than a proxy.
        /// </summary>
        public bool IsDirect => _candidate is null;

        /// <summary>
        /// The leased proxy, or <c>null</c> for the direct slot.
        /// </summary>
        public ProxyEndpoint? Endpoint => _candidate?.Endpoint;

        /// <summary>
        /// Address of the leased proxy, or <c>null</c> for the direct slot.
        /// </summary>
        public Uri? Address => _candidate?.Endpoint.Address;

        /// <summary>
        /// Measured throughput of the leased proxy in KB/s, or <c>-1</c> if it wasn't
        /// measured or this is the direct slot.
        /// </summary>
        public long ThroughputKbps => _candidate?.ThroughputKbps ?? -1;

        /// <summary>
        /// Whether this download has the proxy to itself.
        /// </summary>
        public bool IsExclusive { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isReleased, 1) != 0) return;
            _pool.ReleaseLease(_candidate);
        }
    }

    private sealed class ProxyCredentialProvider(ProxyPool pool) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            foreach (var candidate in pool._state.Candidates)
            {
                var address = candidate.Endpoint.Address;
                if (address.Port != uri.Port) continue;
                if (!string.Equals(address.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate.Endpoint.Credential;
            }

            return null;
        }
    }
}
