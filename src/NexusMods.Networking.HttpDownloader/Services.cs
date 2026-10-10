using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;
using Polly;

namespace NexusMods.Networking.HttpDownloader;

public static class Services
{
    /// <summary>
    /// Add the default HTTP downloader services
    /// </summary>
    public static IServiceCollection AddHttpDownloader(this IServiceCollection services)
    {
        return services
            .AddSettings<ProxySettings>()
            .AddSingleton<ProxyPool>()
            .AddSingleton<HttpClient>(BuildClient);
    }

    private static HttpClient BuildClient(IServiceProvider serviceProvider)
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(3),
                UseJitter = true,
            })
            .Build();

        // NOTE(CE): the routing handler sits inside the retry, so a retry after a proxy
        // failure gets the next proxy from the pool
        HttpMessageHandler handler = new ResilienceHandler(pipeline)
        {
            InnerHandler = new ProxyRoutingHandler(
                proxyPool: serviceProvider.GetRequiredService<ProxyPool>(),
                logger: serviceProvider.GetRequiredService<ILogger<ProxyRoutingHandler>>()
            ),
        };

        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

        client.DefaultRequestHeaders.UserAgent.Add(ApplicationConstants.UserAgent);

        return client;
    }
}
