using JetBrains.Annotations;
using NexusMods.Sdk.Settings;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// Settings for routing downloads through a proxy.
/// </summary>
[PublicAPI]
public record ProxySettings : ISettings
{
    /// <summary>
    /// Whether downloads are routed through one of the proxies in <see cref="ProxyList"/>.
    /// </summary>
    public bool EnableProxy { get; [UsedImplicitly] set; }

    /// <summary>
    /// The user's proxy list, one proxy per line.
    /// </summary>
    public string ProxyList { get; [UsedImplicitly] set; } = DefaultProxyList;

    /// <summary>
    /// Optional URL of an additional proxy list, fetched directly (never through a proxy) on startup.
    /// </summary>
    public string ProxyListUrl { get; [UsedImplicitly] set; } = string.Empty;

    /// <summary>
    /// Whether API, login and image traffic is proxied as well, instead of file downloads only.
    /// </summary>
    public bool ProxyAllTraffic { get; [UsedImplicitly] set; }

    /// <summary>
    /// Whether downloads fall back to a direct connection when no proxy in the list works.
    /// </summary>
    public bool AllowDirectFallback { get; [UsedImplicitly] set; } = true;

    /// <summary>
    /// Whether one download at a time runs on the local connection instead of a proxy.
    /// </summary>
    public bool KeepOneDownloadDirect { get; [UsedImplicitly] set; } = true;

    /// <summary>
    /// The list shipped with the app.
    /// </summary>
    /// <remarks>
    /// Every default entry is a loopback address: the only machine you have to trust to run
    /// one of these is your own. Entries that aren't listening are dropped by the health
    /// check, so leaving an unused line in here costs nothing but one connection attempt.
    /// Remote proxies are deliberately absent - an open proxy that is alive, fast and not
    /// logging your traffic today is usually none of those things next week, so a list
    /// baked into the app would be worse than no list at all. Point
    /// <see cref="ProxyListUrl"/> at a source you trust, or paste entries below.
    /// </remarks>
    public const string DefaultProxyList =
        """
        # One proxy per line, "#" starts a comment. Supported:
        #   http://host:port            socks5://host:port
        #   https://host:port           socks4://host:port
        #   socks5://user:password@host:port
        #
        # Every entry is health checked before use: it has to complete a real HTTPS
        # request with a certificate that validates, and the best few then have their
        # bandwidth measured. The fastest one wins. Entries that fail, or that can't
        # manage 24 KB/s, are skipped and retried later.

        # Local proxies, tried in listed order.
        http://127.0.0.1:8118     # Privoxy, Squid, mitmproxy
        socks5://127.0.0.1:1080   # ssh -D 1080, sing-box, shadowsocks, v2ray
        socks5://127.0.0.1:40000  # Cloudflare WARP: warp-cli mode proxy

        # Tor reaches the CDN but exit nodes are slow, often blocked outright, and the
        # Tor Project asks you not to push bulk downloads through it. Uncomment to use:
        # socks5://127.0.0.1:9050
        """;

    /// <inheritdoc/>
    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.EnableProxy,
                new PropertyOptions<ProxySettings, bool>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Route downloads through a proxy",
                    DescriptionFactory = _ => "Sends mod downloads through one of the proxies below instead of connecting directly. " +
                                              "Their bandwidth is measured and the fastest one is used, and the app switches to another one if it fails mid-download. " +
                                              "One proxy carries the download, the rest are standby.",
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.ProxyList,
                new PropertyOptions<ProxySettings, string>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Proxy list",
                    DescriptionFactory = _ => "One proxy per line, as `http://host:port` or `socks5://host:port`, optionally with `user:password@`. " +
                                              "Lines starting with `#` are ignored. Entries that fail the health check are skipped.",
                    Validation = ValidateProxyList,
                },
                new TextEntryContainerOptions
                {
                    IsMultiLine = true,
                    ControlWidth = 420,
                    MaxControlHeight = 220,
                    Placeholder = "socks5://127.0.0.1:1080",
                }
            )
            .ConfigureProperty(
                x => x.ProxyListUrl,
                new PropertyOptions<ProxySettings, string>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Proxy list URL",
                    DescriptionFactory = _ => "Optional. A plain text list of proxies in the same format, fetched on startup and added to the list above. " +
                                              "Fetched directly, never through a proxy, so whoever hosts it sees your IP address.",
                    Validation = ValidateProxyListUrl,
                },
                new TextEntryContainerOptions
                {
                    ControlWidth = 420,
                    Placeholder = "https://example.com/proxies.txt",
                }
            )
            .ConfigureProperty(
                x => x.ProxyAllTraffic,
                new PropertyOptions<ProxySettings, bool>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Proxy all app traffic",
                    DescriptionFactory = _ => "Also routes API calls, login and images through the proxy. " +
                                              "Off by default: those requests carry your account token, and only the download itself needs the proxy.",
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.KeepOneDownloadDirect,
                new PropertyOptions<ProxySettings, bool>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Run one download without a proxy",
                    DescriptionFactory = _ => "Gives the first of your parallel downloads your own connection and a proxy each to the rest, since your own line is usually the fastest one available. " +
                                              "Note that downloading a single mod then uses no proxy at all. Turn this off to put every download on a proxy. " +
                                              "Ignored when direct connections are disallowed below.",
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.AllowDirectFallback,
                new PropertyOptions<ProxySettings, bool>
                {
                    Section = Sections.Advanced,
                    DisplayName = "Fall back to a direct connection",
                    DescriptionFactory = _ => "When no proxy in the list works, download directly instead of failing. " +
                                              "Turn this off if the proxy is there to hide your IP address.",
                },
                new BooleanContainerOptions()
            );
    }

    private static ValidationResult ValidateProxyList(string value)
    {
        ProxyListParser.Parse(value, out var errors);
        return errors.Length == 0
            ? ValidationResult.CreateSuccessful()
            : ValidationResult.CreateFailed(string.Join(Environment.NewLine, errors));
    }

    private static ValidationResult ValidateProxyListUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ValidationResult.CreateSuccessful();

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidationResult.CreateFailed("Has to be an http or https URL");
        }

        return ValidationResult.CreateSuccessful();
    }
}
