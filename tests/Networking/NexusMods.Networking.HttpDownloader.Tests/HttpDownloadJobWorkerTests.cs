using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;

namespace NexusMods.Networking.HttpDownloader.Tests;

public class HttpDownloadJobWorkerTests
{
    private readonly TemporaryFileManager _temporaryFileManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly LocalHttpServer _localHttpServer;

    public HttpDownloadJobWorkerTests(IServiceProvider serviceProvider)
    {
        _temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        _serviceProvider = serviceProvider;
        _localHttpServer = serviceProvider.GetRequiredService<LocalHttpServer>();
    }

    /// <summary>
    /// How the library hashes a file it adds; what a download works out on the way has to match.
    /// </summary>
    private static readonly MultiHasherBuilder<Hash, XxHash3, Xx3Hasher, Md5Value, MD5, Md5Hasher> LibraryHasher = MultiHasherBuilder.Start()
        .AddHasher<Hash, XxHash3, Xx3Hasher>()
        .AddHasher<Md5Value, MD5, Md5Hasher>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_DownloadIsHashedOnTheWay(bool skipMetadataRequest)
    {
        await using var outputPath = _temporaryFileManager.CreateFile();
        var job = CreateJob(new Uri(_localHttpServer.Uri, "file"), outputPath.Path, skipMetadataRequest);

        await _serviceProvider.GetRequiredService<IJobMonitor>().Begin<HttpDownloadJob, AbsolutePath>(job);

        (await outputPath.Path.XxHash3Async()).Should().Be(_localHttpServer.StallDataHash);
        await ShouldMatchLibraryHashes(job, outputPath.Path);
    }

    [Theory]
    [InlineData("stall", false)]
    [InlineData("stall", true)]
    [InlineData("stall-no-ranges", false)]
    [InlineData("stall-no-ranges", true)]
    public async Task Test_StalledDownloadRecovers(string path, bool skipMetadataRequest)
    {
        await using var outputPath = _temporaryFileManager.CreateFile();
        var job = CreateJob(new Uri(_localHttpServer.Uri, $"{path}/{skipMetadataRequest}"), outputPath.Path, skipMetadataRequest);

        // The first answer stops halfway and stays open; without the stall timeout this would
        // wait for the server's two minutes. With it, the rest comes from a range request, or,
        // from a server without ranges, the whole file again -- written over the half already
        // there rather than after it. Either way the hashes worked out on the way still hold.
        var stopwatch = Stopwatch.StartNew();
        await _serviceProvider.GetRequiredService<IJobMonitor>().Begin<HttpDownloadJob, AbsolutePath>(job);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));

        outputPath.Path.FileInfo.Size.Should().Be(Size.FromLong(_localHttpServer.StallData.Length));
        (await outputPath.Path.XxHash3Async()).Should().Be(_localHttpServer.StallDataHash);
        await ShouldMatchLibraryHashes(job, outputPath.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_DownloadThatKeepsStallingStillFinishes(bool skipMetadataRequest)
    {
        await using var outputPath = _temporaryFileManager.CreateFile();
        var job = CreateJob(new Uri(_localHttpServer.Uri, $"stall-often/{skipMetadataRequest}"), outputPath.Path, skipMetadataRequest) with
        {
            StallTimeout = TimeSpan.FromSeconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(100),
        };

        // Five stalls for an 8 MB file, each one after another 1.5 MB: more than the three retries
        // a download gets, but every attempt got somewhere, so none of them counts.
        await _serviceProvider.GetRequiredService<IJobMonitor>().Begin<HttpDownloadJob, AbsolutePath>(job);

        (await outputPath.Path.XxHash3Async()).Should().Be(_localHttpServer.StallDataHash);
        await ShouldMatchLibraryHashes(job, outputPath.Path);
    }

    private HttpDownloadJob CreateJob(Uri uri, AbsolutePath destination, bool skipMetadataRequest) => new()
    {
        Uri = uri,
        DownloadPageUri = uri,
        Destination = destination,
        Logger = _serviceProvider.GetRequiredService<ILogger<HttpDownloadJob>>(),
        Client = _serviceProvider.GetRequiredService<HttpClient>(),
        StallTimeout = TimeSpan.FromSeconds(2),
        SkipMetadataRequest = skipMetadataRequest,
    };

    private static async Task ShouldMatchLibraryHashes(HttpDownloadJob job, AbsolutePath file)
    {
        job.KnownHashes.Should().NotBeNull();
        var known = job.KnownHashes!.Value;

        await using var stream = file.Read();
        var expected = await LibraryHasher.HashAsync(stream);

        known.Hash.Should().Be(expected.Hash1);
        known.Md5.Should().Be(expected.Hash2);
        known.Size.Should().Be(file.FileInfo.Size);
    }

    [Fact]
    [Trait("RequiresNetworking", "True")]
    public async Task Test_NexusModsCDN100MFile()
    {
        const string url = "https://paris.nexus-cdn.com/100M";

        await using var outputPath = _temporaryFileManager.CreateFile();
        _ = await HttpDownloadJob.Create(_serviceProvider, new Uri(url), new Uri(url), outputPath.Path);;

        outputPath.Path.FileExists.Should().BeTrue();
        outputPath.Path.FileInfo.Size.Should().Be(Size.MB * 100);

        var hash = await outputPath.Path.XxHash3Async();
        hash.Should().Be(Hash.From(0x2330001AD4114867));
    }
}
