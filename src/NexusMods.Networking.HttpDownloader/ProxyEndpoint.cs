using System.Diagnostics.CodeAnalysis;
using System.Net;
using JetBrains.Annotations;

namespace NexusMods.Networking.HttpDownloader;

/// <summary>
/// A single proxy server, parsed from one line of the user's proxy list.
/// </summary>
[PublicAPI]
public sealed record ProxyEndpoint
{
    /// <summary>
    /// Address of the proxy, without any credentials.
    /// </summary>
    public required Uri Address { get; init; }

    /// <summary>
    /// Credentials for the proxy, if the entry had any.
    /// </summary>
    public NetworkCredential? Credential { get; init; }

    /// <summary>
    /// Whether this entry came from <see cref="ProxySettings.ProxyListUrl"/> instead of the local list.
    /// </summary>
    public bool IsRemote { get; init; }

    /// <inheritdoc/>
    public override string ToString() => Address.AbsoluteUri.TrimEnd('/');
}

/// <summary>
/// Parses the proxy list format: one proxy per line, <c>#</c> starts a comment.
/// </summary>
[PublicAPI]
public static class ProxyListParser
{
    private const int DefaultSocksPort = 1080;

    /// <summary>
    /// Schemes we hand to <see cref="System.Net.Http.SocketsHttpHandler"/>.
    /// </summary>
    private static readonly string[] SupportedSchemes = ["http", "https", "socks4", "socks4a", "socks5"];

    /// <summary>
    /// Parses <paramref name="text"/> into endpoints, collecting a message for every line
    /// that couldn't be parsed.
    /// </summary>
    public static ProxyEndpoint[] Parse(string? text, out string[] errors, bool isRemote = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors = [];
            return [];
        }

        var results = new List<ProxyEndpoint>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]);
            if (line.Length == 0) continue;

            if (!TryParseEntry(line, isRemote, out var endpoint, out var error))
            {
                problems.Add($"Line {i + 1}: {error}");
                continue;
            }

            // NOTE(CE): keep the first occurrence, a duplicate would only be probed twice
            if (!seen.Add(endpoint.ToString())) continue;
            results.Add(endpoint);
        }

        errors = problems.ToArray();
        return results.ToArray();
    }

    private static string StripComment(string line)
    {
        var commentStart = line.IndexOf('#');
        if (commentStart >= 0) line = line[..commentStart];
        return line.Trim();
    }

    private static bool TryParseEntry(
        string line,
        bool isRemote,
        [NotNullWhen(true)] out ProxyEndpoint? endpoint,
        [NotNullWhen(false)] out string? error)
    {
        endpoint = null;

        // NOTE(CE): bare "host:port" entries are the most common copy-paste format
        var withScheme = line.Contains("://", StringComparison.Ordinal) ? line : "http://" + line;

        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
        {
            error = $"`{line}` is not a valid proxy address";
            return false;
        }

        if (!SupportedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = $"`{uri.Scheme}` proxies aren't supported, use one of: {string.Join(", ", SupportedSchemes)}";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = $"`{line}` is missing a host";
            return false;
        }

        var isSocks = uri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
        var port = uri.Port;
        if (port <= 0)
        {
            if (!isSocks)
            {
                error = $"`{line}` is missing a port";
                return false;
            }

            port = DefaultSocksPort;
        }

        NetworkCredential? credential = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':');
            var user = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            var password = separator < 0 ? string.Empty : uri.UserInfo[(separator + 1)..];
            credential = new NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(password));
        }

        // NOTE(CE): credentials are passed separately, a proxy URI carrying them is rejected
        // by SocketsHttpHandler
        if (!Uri.TryCreate($"{uri.Scheme.ToLowerInvariant()}://{uri.Host}:{port}", UriKind.Absolute, out var address))
        {
            error = $"`{line}` is not a valid proxy address";
            return false;
        }

        endpoint = new ProxyEndpoint
        {
            Address = address,
            Credential = credential,
            IsRemote = isRemote,
        };

        error = null;
        return true;
    }
}
