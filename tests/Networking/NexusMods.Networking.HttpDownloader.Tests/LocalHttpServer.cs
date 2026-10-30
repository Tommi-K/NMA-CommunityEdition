using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Networking.HttpDownloader.Tests;

public class LocalHttpServer : IDisposable
{
    private readonly ILogger<LocalHttpServer> _logger;
    private readonly HttpListener _listener;
    private readonly string _prefix;

    public byte[] LargeData { get; set; }
    public Hash LargeDataHash { get; set; }

    /// <summary>
    /// What <c>/file</c> and the <c>/stall</c> paths serve.
    /// </summary>
    public ReadOnlyMemory<byte> StallData { get; }
    public Hash StallDataHash { get; }

    public LocalHttpServer(ILogger<LocalHttpServer> logger)
    {
        _logger = logger;
        (_listener, _prefix) = CreateNewListener();
        _listener.Start();

        LargeData = GenerateLargeData();
        LargeDataHash = LargeData.AsSpan().xxHash3();

        StallData = LargeData.AsMemory(start: 0, length: 8 * MB);
        StallDataHash = LargeData.AsSpan(start: 0, length: 8 * MB).xxHash3();

        StartLoop();
    }



    private byte[] GenerateLargeData()
    {
        var data = new byte[512 * 1024 * 1024];
        // Bit of a hack because .NextBytes is *extremely* slow
        var seed = Random.Shared.Next(0, 255);
        for (var offset = 0; offset < data.Length; offset++)
            data[offset] = (byte)((offset % 255) ^ seed);

        return data;
    }

    private void StartLoop()
    {
        Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                _logger.LogInformation("Got connection");

                // Answered off the loop: the first answer stalls, and the retry has to get
                // through while it does.
                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (path == "/file" || path.StartsWith("/stall", StringComparison.Ordinal))
                {
                    _ = Task.Run(() => HandleSmallFile(context, path));
                    continue;
                }

                using var resp = context.Response;

                if (context.Request.Url?.PathAndQuery.StartsWith("/Resources") ?? false)
                {
                    await ServeResource(resp, context.Request);
                }
                else
                {

                    switch (context.Request.Url?.PathAndQuery)
                    {
                        case "/hello":
                        {
                            var responseString = Encoding.UTF8.GetBytes("Hello World!");
                            resp.StatusCode = 200;
                            resp.StatusDescription = "OK";
                            resp.ProtocolVersion = HttpVersion.Version11;
                            resp.ContentLength64 = responseString.Length;
                            if (context.Request.HttpMethod != "HEAD")
                            {
                                await using var ros = resp.OutputStream;
                                await ros.WriteAsync(responseString);
                            }

                            break;
                        }
                        case "/reliable":
                            await HandleUnreliable(resp, context.Request, false);
                            break;
                        case "/unreliable":
                            await HandleUnreliable(resp, context.Request, true);
                            break;
                        default:
                        {
                            resp.StatusCode = 404;
                            resp.StatusDescription = "Not Found";
                            break;
                        }
                    }
                }
            }
        });
    }

    private async Task ServeResource(HttpListenerResponse resp, HttpListenerRequest request)
    {
        var filePath = Uri.UnescapeDataString(request.Url!.AbsolutePath);
        var fullPath = Path.GetFullPath("."+filePath);

        await using var stream = FileSystem.Shared.FromUnsanitizedFullPath(fullPath).Read();

        if (request.HttpMethod == "HEAD")
        {
            resp.StatusCode = (int)HttpStatusCode.OK;
            resp.StatusDescription = "OK";
            resp.ProtocolVersion = HttpVersion.Version11;
            resp.ContentLength64 = stream.Length;
            resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
            resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");
            resp.Headers.Add(HttpResponseHeader.KeepAlive, "true");
            await using var _ = resp.OutputStream;
            return;
        }

        var rangeString = request.Headers.Get("Range");


        if (rangeString == null)
        {
            resp.StatusCode = (int)HttpStatusCode.OK;
            resp.StatusDescription = "OK";
            resp.ProtocolVersion = HttpVersion.Version11;
            resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
            resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");
            resp.Headers.Add(HttpResponseHeader.KeepAlive, "true");
            resp.ContentLength64 = stream.Length;
            await using var ros = resp.OutputStream;
            await stream.CopyToAsync(ros);

        }
        else
        {
            var rangeValue = RangeHeaderValue.Parse(rangeString);
            var range = rangeValue.Ranges.First();
            resp.StatusCode = (int)HttpStatusCode.PartialContent;
            resp.StatusDescription = "Partial Content";
            resp.ProtocolVersion = HttpVersion.Version11;
            resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
            resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");
            resp.Headers.Add(HttpResponseHeader.KeepAlive, "true");
            resp.Headers.Add(HttpResponseHeader.ContentRange, range.ToString());

            await SendContent(resp, stream, range);
        }
    }

    private const int MB = 1024 * 1024;
    private async Task HandleUnreliable(HttpListenerResponse resp, HttpListenerRequest request, bool truncate)
    {
        if (request.HttpMethod == "HEAD")
        {
            resp.StatusCode = (int)HttpStatusCode.OK;
            resp.StatusDescription = "OK";
            resp.ProtocolVersion = HttpVersion.Version11;
            resp.ContentLength64 = LargeData.Length;
            resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
            resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");
            resp.Headers.Add(HttpResponseHeader.KeepAlive, "true");
            await using var _ = resp.OutputStream;
            return;
        }

        var rangeString = request.Headers.Get("Range");
        var rangeValue = RangeHeaderValue.Parse(rangeString);
        var range = rangeValue.Ranges.First();

        resp.StatusCode = (int)HttpStatusCode.PartialContent;
        resp.StatusDescription = "Partial Content";
        resp.ProtocolVersion = HttpVersion.Version11;
        resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
        resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");
        resp.Headers.Add(HttpResponseHeader.KeepAlive, "true");
        resp.Headers.Add(HttpResponseHeader.ContentRange, range.ToString());
        await SendContent(resp, new MemoryStream(LargeData), range, truncate);

    }

    /// <summary>
    /// Full requests made so far to each <c>/stall-no-ranges</c> path.
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _requestsWithoutRanges = new();

    /// <summary>
    /// How much a <c>/stall-often</c> answer sends before it goes quiet.
    /// </summary>
    private const int StallOftenChunk = 3 * MB / 2;

    /// <summary>
    /// Serves <see cref="StallData"/>. <c>/file</c> answers every request in full. A path
    /// starting <c>/stall</c> answers a request for the whole file with its first half and then
    /// nothing more, keeping the connection open the way a dead route does, and a request for
    /// a range in full. One starting <c>/stall-often</c> goes quiet after every
    /// <see cref="StallOftenChunk"/> of any answer, range or not. One starting
    /// <c>/stall-no-ranges</c> says nothing about ranges, stalls only the first request made
    /// to it, and answers every later one with the whole file.
    /// </summary>
    private async Task HandleSmallFile(HttpListenerContext context, string path)
    {
        var supportsRanges = !path.StartsWith("/stall-no-ranges", StringComparison.Ordinal);
        var stallsOften = path.StartsWith("/stall-often", StringComparison.Ordinal);

        try
        {
            using var resp = context.Response;
            resp.ProtocolVersion = HttpVersion.Version11;
            resp.Headers.Add(HttpResponseHeader.ContentType, "application/octet-stream");
            if (supportsRanges) resp.Headers.Add(HttpResponseHeader.AcceptRanges, "bytes");

            if (context.Request.HttpMethod == "HEAD")
            {
                resp.StatusCode = (int)HttpStatusCode.OK;
                resp.ContentLength64 = StallData.Length;
                await using var _ = resp.OutputStream;
                return;
            }

            var rangeString = supportsRanges ? context.Request.Headers.Get("Range") : null;
            var stalls = path.StartsWith("/stall", StringComparison.Ordinal) &&
                         (supportsRanges || _requestsWithoutRanges.AddOrUpdate(path, 1, static (_, count) => count + 1) == 1);

            if (rangeString is null && !stalls)
            {
                resp.StatusCode = (int)HttpStatusCode.OK;
                resp.ContentLength64 = StallData.Length;

                await using var full = resp.OutputStream;
                await full.WriteAsync(StallData);
                return;
            }

            if (rangeString is null)
            {
                resp.StatusCode = (int)HttpStatusCode.OK;
                resp.ContentLength64 = StallData.Length;

                var output = resp.OutputStream;
                await output.WriteAsync(StallData[..(stallsOften ? StallOftenChunk : StallData.Length / 2)]);
                await output.FlushAsync();

                await Task.Delay(TimeSpan.FromMinutes(2));
                return;
            }

            var range = RangeHeaderValue.Parse(rangeString).Ranges.First();
            var from = (int)(range.From ?? 0);
            var to = (int)Math.Min(range.To ?? StallData.Length - 1, StallData.Length - 1);

            resp.StatusCode = (int)HttpStatusCode.PartialContent;
            resp.Headers.Add(HttpResponseHeader.ContentRange, $"bytes {from}-{to}/{StallData.Length}");
            resp.ContentLength64 = to - from + 1;

            if (stallsOften && to - from + 1 > StallOftenChunk)
            {
                var partial = resp.OutputStream;
                await partial.WriteAsync(StallData[from..(from + StallOftenChunk)]);
                await partial.FlushAsync();

                await Task.Delay(TimeSpan.FromMinutes(2));
                return;
            }

            await using var ros = resp.OutputStream;
            await ros.WriteAsync(StallData[from..(to + 1)]);
        }
        catch (Exception e)
        {
            // The stalled answer's client has long gone by the time it is closed.
            _logger.LogInformation(e, "Stalled response ended");
        }
    }

    private async Task SendContent(HttpListenerResponse resp, Stream src, RangeItemHeaderValue range, bool truncate = false)
    {
        var from = range.From ?? 0;
        var to = range.To ?? src.Length;
        await using var ros = resp.OutputStream;
        src.Position = from;

        var count = to - from + 1;

        if (truncate && count > MB * 2)
            count = Random.Shared.Next(MB, MB * 2);

        var buffer = new byte[count];
        await src.ReadExactlyAsync(buffer);
        await ros.WriteAsync(buffer);
    }

    public Uri Uri => new(_prefix);

    private (HttpListener Listener, string Prefix) CreateNewListener()
    {
        HttpListener mListener;
        while (true)
        {
            mListener = new HttpListener();
            var newPort = Random.Shared.Next(49152, 65535);
            mListener.Prefixes.Add($"http://127.0.0.1:{newPort}/");
            try
            {
                mListener.Start();
            }
            catch
            {
                continue;
            }
            break;
        }

        return (mListener, mListener.Prefixes.First());
    }

    public void Dispose()
    {
        _listener.Stop();
    }
}
