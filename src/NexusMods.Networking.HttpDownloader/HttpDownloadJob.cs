using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Downloads;
using NexusMods.Abstractions.HttpDownloads;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using System.ComponentModel;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Download job.
/// </summary>
[PublicAPI]
public record HttpDownloadJob : IJobDefinitionWithStart<HttpDownloadJob, AbsolutePath>, IHttpDownloadJob
{

    /// <summary>
    /// Client.
    /// </summary>
    public required HttpClient Client { get; init; }

    /// <summary>
    /// Logger.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// Proxy pool, used to lease one proxy for the duration of this download so that
    /// parallel downloads don't share a proxy. Null when proxy support isn't available.
    /// </summary>
    public ProxyPool? ProxyPool { get; init; }

    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri Uri { get; init; }
    
    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri DownloadPageUri { get; init; }
    
    /// <summary>
    /// The destination of the download.
    /// </summary>
    public required AbsolutePath Destination { get; init; }

    /// <summary>
    /// How long the download may go without receiving a single byte before the attempt is
    /// abandoned and retried from where it stopped.
    /// </summary>
    /// <remarks>
    /// The client's own timeout ends once the response headers are in, so nothing else notices
    /// a connection that stays open and delivers nothing: the download would sit there, holding
    /// its place in a collection's queue, until the OS gives up on the socket hours later.
    /// </remarks>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether to go straight to fetching the file, without first asking the server about it
    /// (a HEAD request), which saves a round trip per download.
    /// </summary>
    /// <remarks>
    /// What that request would have said -- the size, whether the download can be resumed --
    /// then comes from the answer to the download itself.
    /// </remarks>
    public bool SkipMetadataRequest { get; init; }

    /// <inheritdoc/>
    public DownloadedFileHashes? KnownHashes => _state.Hashes;

    /// <summary>
    /// Only exists for extension by derived classes.
    /// </summary>
    public virtual ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile)
    {
        return ValueTask.CompletedTask;
    }

    private readonly HttpDownloadState _state = new();
    
    public bool SupportsPausing => true;
    /// <summary>
    /// Implementation for external job state data access
    /// </summary>
    public IPublicJobStateData? GetJobStateData() => _state;
    
    /// <summary>
    /// Constructor for the job
    /// </summary>
    public static IJobTask<HttpDownloadJob, AbsolutePath> Create(
        IServiceProvider provider,
        Uri uri,
        Uri downloadPage,
        AbsolutePath destination,
        bool skipMetadataRequest = false)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new HttpDownloadJob
        {
            Uri = uri,
            DownloadPageUri = downloadPage,
            Destination = destination,
            Logger = provider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            Client = provider.GetRequiredService<HttpClient>(),
            ProxyPool = provider.GetService<ProxyPool>(),
            SkipMetadataRequest = skipMetadataRequest,
        };

        return monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
    }

    /// <inheritdoc/>
    public async ValueTask<AbsolutePath> StartAsync(IJobContext<HttpDownloadJob> context)
    {
        var stopwatch = Stopwatch.StartNew();
        var failuresWithoutProgress = 0;

        while (true)
        {
            var downloadedBefore = (long)_state.TotalBytesDownloaded.Value;

            try
            {
                var destination = await StartAsyncImpl(context);

                // NOTE(CE): what a run's actual speed can be read back from afterwards
                var size = _state.TotalBytesDownloaded;
                var bytesPerSecond = size.Value / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                Logger.LogInformation("Downloaded {Size} from `{PageUri}` in {Seconds:0.0} s ({Speed}/s)", size, DownloadPageUri, stopwatch.Elapsed.TotalSeconds, Size.FromLong((long)bytesPerSecond));

                return destination;
            }
            catch (Exception e) when (NetworkExceptions.Contains(e.GetType()) && !context.CancellationToken.IsCancellationRequested)
            {
                // NOTE(CE): an attempt that got somewhere doesn't count against the limit. A large
                // file on a connection that keeps going quiet would otherwise run out of tries while
                // still getting there. Only where the download carries on from where it stopped,
                // though: one that starts over each time is no further along for it.
                var progress = (long)_state.TotalBytesDownloaded.Value - downloadedBefore;
                if (_state.AcceptRanges.ValueOrDefault() && progress >= MeaningfulProgress) failuresWithoutProgress = 0;
                if (++failuresWithoutProgress > MaxRetriesWithoutProgress) throw;

                var delay = RetryDelay * Math.Pow(2, failuresWithoutProgress - 1) * (0.75 + Random.Shared.NextDouble() / 2);
                Logger.LogDebug(e, "Download from `{PageUri}` failed, trying again in {Delay}", DownloadPageUri, delay);
                await Task.Delay(delay, context.CancellationToken);
            }
        }
    }

    private async ValueTask<AbsolutePath> StartAsyncImpl(IJobContext<HttpDownloadJob> context)
    {
        await context.YieldAsync();
        if (_state.TotalBytesDownloaded > Size.Zero)
            Logger.LogInformation("Resuming download from {Bytes} bytes", _state.TotalBytesDownloaded);

        // NOTE(CE): held for this attempt only, a retry leases again and so moves to
        // another proxy if this one just failed
        using var lease = ProxyPool?.AcquireLease();
        if (lease is { IsDirect: true })
        {
            Logger.LogInformation("Downloading `{Uri}` on the local connection, the other downloads take the proxies", Uri);
        }
        else if (lease is not null)
        {
            Logger.LogInformation(
                "Downloading `{Uri}` through proxy `{Proxy}`{Shared}",
                Uri, lease.Address, lease.IsExclusive ? string.Empty : " (shared with another download)"
            );
        }

        if (!SkipMetadataRequest) await FetchMetadata(context, lease);

        await context.YieldAsync();
        await using var fileStream = Destination.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        fileStream.Position = (long)_state.TotalBytesDownloaded.Value;

        // Declared before the stream below so that it outlives the stream's progress timer,
        // which is what cancels it.
        using var stallCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var lastBytesWritten = _state.TotalBytesDownloaded;

        // Zero until the response is in: waiting for that is the client's own timeout's job.
        long lastProgress = 0;

        await using var outputStream = new StreamProgressWrapper<IJobContext<HttpDownloadJob>>(
            new HashingWriteStream(fileStream, _state.Hasher),
            context,
            (state, tuple) =>
            {
                var (bytesWritten, speed) = tuple;

                _state.TotalBytesDownloaded = bytesWritten;

                state.SetPercent(bytesWritten, _state.ContentLength.ValueOr(static () => Size.One));
                state.SetRateOfProgress(speed);

                var progressSince = Volatile.Read(ref lastProgress);
                if (progressSince == 0) return;

                if (bytesWritten != lastBytesWritten)
                {
                    lastBytesWritten = bytesWritten;
                    Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());
                }
                else if (Stopwatch.GetElapsedTime(progressSince) >= StallTimeout)
                {
                    TryCancel(stallCancellation);
                }
            }
        );

        if (_state.ContentLength.HasValue)
        {
            var contentLength = (long)_state.ContentLength.Value.Value;
            if (outputStream.Length != contentLength) outputStream.SetLength(contentLength);
        }

        await context.YieldAsync();
        using var request = PrepareRequest(lease, out var isRangeRequest);
        using var response = await Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        );

        response.EnsureSuccessStatusCode();

        // NOTE(CE): without the request for the file's details beforehand, this answer is
        // where they come from
        if (!_state.AcceptRanges.HasValue)
        {
            _state.AcceptRanges = response.Headers.AcceptRanges.Contains("bytes");
            _state.ETag = response.Headers.ETag;
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // NOTE(erri120): We might've not gotten the content length in the initial HEAD request.
            if (!_state.ContentLength.HasValue)
            {
                // For full requests only, the response should contain the content length at this point.
                // For range requests, the content length equals the length of the range, so we can't use that here.
                var newContentLength = response.Content.Headers.ContentLength;
                if (newContentLength is not null)
                {
                    _state.ContentLength = Size.FromLong(newContentLength.Value);
                    outputStream.SetLength(newContentLength.Value);
                }
            }
        } else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (!_state.ContentLength.HasValue)
            {
                // Responses to range requests should have this header
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Range
                var contentRange = response.Content.Headers.ContentRange;
                var length = contentRange?.Length;
                if (length is not null)
                {
                    _state.ContentLength = Size.FromLong(length.Value);
                    outputStream.SetLength(length.Value);
                }
            }
        }

        if (isRangeRequest && response.StatusCode == HttpStatusCode.OK)
        {
            // NOTE(erri120): We asked the server whether it supports range requests, the server responded with yes,
            // then we do a range request, and suddenly the server changed its mind and says no...
            Logger.LogWarning("Server `{ServerName}` responded with 200 to a valid range request for download from `{PageUri}`. The download will be reset", response.Headers.Server.ToString(), DownloadPageUri);

            // NOTE(erri120): The only thing we can do here is to reset everything and start from scratch.
            _state.TotalBytesDownloaded = Size.Zero;
            outputStream.Position = 0;
            _state.AcceptRanges = false;
        }
        else if (!isRangeRequest && outputStream.Position != 0)
        {
            // NOTE(CE): a retry from a server without range support asks for the whole file
            // again, which has to go from the start rather than after the part already written
            Logger.LogInformation("`{PageUri}` can't be resumed, downloading it again from the start", DownloadPageUri);
            _state.TotalBytesDownloaded = Size.Zero;
            outputStream.Position = 0;
        }

        Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());
        _state.Hasher.PrepareForWriteAt(outputStream.Position);

        try
        {
            await response.Content.CopyToAsync(outputStream, stallCancellation.Token);
        }
        catch (Exception) when (stallCancellation.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("Nothing arrived from `{PageUri}` for {Timeout}, at `{DownloadedBytes}` of `{TotalBytes}` bytes; trying again from there", DownloadPageUri, StallTimeout, outputStream.Position, outputStream.Length);

            // Exactly this type: the retry around this method matches on it.
            throw new HttpRequestException($"The download from `{DownloadPageUri}` stalled");
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Exception while downloading from `{PageUri}`, downloaded `{DownloadedBytes}` from `{TotalBytes}` bytes", DownloadPageUri, outputStream.Position, outputStream.Length);
            throw;
        }
        finally
        {
            _state.TotalBytesDownloaded = Size.FromLong(outputStream.Position);
        }

        // Verify the download is complete: the server can return 200 with an
        // empty/truncated body (rate-limit, expired token, mid-stream disconnect
        // that flushed an EOF cleanly) and HttpClient won't throw. Without this
        // guard, the file is silently saved at 0 bytes / partial, then NMA
        // archives the bad payload and extracts garbage on install. Throwing
        // HttpRequestException lets the resilience pipeline retry it.
        if (_state.ContentLength.HasValue)
        {
            var expected = _state.ContentLength.Value;
            var actual = _state.TotalBytesDownloaded;
            if (actual < expected)
                throw new HttpRequestException($"Download truncated: got {actual.Value} bytes, expected {expected.Value} from `{DownloadPageUri}`");
        }

        _state.Hashes = _state.Hasher.Finish(_state.TotalBytesDownloaded);

        // Ensure progress is set to 100% when download completes
        if (_state.ContentLength.HasValue)
            context.SetPercent(_state.ContentLength.Value, _state.ContentLength.Value);
        else
            context.SetPercent(Size.One, Size.One);

        return Destination;
    }
    
    /// <summary>
    /// Cancels <paramref name="cancellationTokenSource"/>, from a timer callback, where an
    /// escaping exception would take the process down.
    /// </summary>
    private static void TryCancel(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the attempt is already over
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, ProxyPool.ProxyLease? lease)
    {
        var request = new HttpRequestMessage(method, uri);

        ProxyRequestOptions.MarkAsFileDownload(request);

        if (lease is { IsDirect: true }) ProxyRequestOptions.PinToDirectConnection(request);
        else if (lease?.Address is not null) ProxyRequestOptions.SetLeasedProxy(request, lease.Address);

        return request;
    }

    private HttpRequestMessage PrepareRequest(ProxyPool.ProxyLease? lease, out bool isRangeRequest)
    {
        var request = CreateRequest(HttpMethod.Get, Uri, lease);

        // NOTE(erri120): use a normal GET request for the entire file
        // NOTE(CE): not known yet when the HEAD request was skipped and no answer has come in
        if (!_state.AcceptRanges.ValueOrDefault() || _state.TotalBytesDownloaded == Size.Zero)
        {
            // NOTE(erri120): Using If-Match to ensure that what we're downloading didn't suddenly change
            if (_state.ETag.HasValue)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Match
                request.Headers.IfMatch.Add(_state.ETag.Value);
            }

            isRangeRequest = false;
        }
        else
        {
            // NOTE(erri120): Using If-Range for range requests instead of If-Match
            if (_state.ETag.HasValue)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Range
                request.Headers.IfRange = new RangeConditionHeaderValue(_state.ETag.Value);
            }

            // A server MAY send a Content-Length header field in a response to a HEAD request
            // https://httpwg.org/specs/rfc9110.html#rfc.section.8.6

            // NOTE(erri120): As such, we might not know the content length, but since we download
            // in serial, we can omit the end position to request all remaining bytes
            var range = _state.ContentLength.HasValue
                ? new RangeHeaderValue(
                    from: (long)_state.TotalBytesDownloaded.Value,
                    to: (long)_state.ContentLength.Value.Value
                )
                : new RangeHeaderValue(
                    from: (long)_state.TotalBytesDownloaded.Value,
                    to: null
                );

            // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Range
            request.Headers.Range = range;
            isRangeRequest = true;
        }

        return request;
    }

    private async ValueTask FetchMetadata(IJobContext context, ProxyPool.ProxyLease? lease)
    {
        using var request = CreateRequest(HttpMethod.Head, Uri, lease);

        using var response = await Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        ).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // https://datatracker.ietf.org/doc/html/rfc7233#section-5.1.2
        // "bytes" and "none" are the only range units we care about,
        // "none" meaning the server doesn't support ranges
        _state.AcceptRanges = response.Headers.AcceptRanges.Contains("bytes");
        _state.ETag = response.Headers.ETag;

        var contentLength = response.Content.Headers.ContentLength;
        _state.ContentLength = contentLength is not null ? Size.FromLong(contentLength.Value) : Optional<Size>.None;
    }

    /// <summary>
    /// The failures a download is tried again after, matched on the exact type.
    /// </summary>
    private static readonly ImmutableArray<Type> NetworkExceptions =
    [
        typeof(HttpIOException),
        typeof(HttpRequestException),
        typeof(SocketException),
    ];

    /// <summary>
    /// How many attempts in a row may fail without getting anywhere before the download is given up on.
    /// </summary>
    private const int MaxRetriesWithoutProgress = 3;

    /// <summary>
    /// How far an attempt has to get for its failure not to count against <see cref="MaxRetriesWithoutProgress"/>.
    /// </summary>
    private const long MeaningfulProgress = 256 * 1024;

    /// <summary>
    /// The wait before the first retry, doubled for each one after it that got nowhere.
    /// </summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Public interface for external access to download information
/// </summary>
[PublicAPI]
public interface IHttpDownloadState : IPublicJobStateData, INotifyPropertyChanged
{
    /// <summary>
    /// Content length from HTTP headers
    /// </summary>
    Optional<Size> ContentLength { get; }
    
    /// <summary>
    /// Total bytes downloaded so far
    /// </summary>
    Size TotalBytesDownloaded { get; }
}

/// <summary>
/// Internal mutable state that persists across pause/resume cycles
/// </summary>
internal sealed class HttpDownloadState : ReactiveObject, IHttpDownloadState
{
    [Reactive] public Optional<Size> ContentLength { get; set; }
    [Reactive] public Size TotalBytesDownloaded { get; set; }
    public Optional<EntityTagHeaderValue> ETag { get; set; }
    public Optional<bool> AcceptRanges { get; set; }

    /// <summary>
    /// Hashes the file as it is written, across every attempt.
    /// </summary>
    public DownloadHasher Hasher { get; } = new();

    /// <summary>
    /// The finished file's hashes, when <see cref="Hasher"/> saw all of it.
    /// </summary>
    public DownloadedFileHashes? Hashes { get; set; }
}
