using System.Collections.Frozen;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.App.UI.Settings;
using NexusMods.Sdk.Settings;
using R3;
using Xilium.CefGlue;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// Decides which requests the in-app browser is allowed to make, from the domain rules
/// in <c>AdBlockList.txt</c>.
/// </summary>
/// <remarks>
/// Registered as a singleton: the rules are immutable once loaded, so one instance is
/// shared by every browser tab. <see cref="ShouldBlock"/> is called from Chromium's IO
/// thread, which is why nothing here mutates state beyond the one volatile flag.
/// </remarks>
[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal sealed class AdBlocker : IDisposable
{
    private const string RulesFileName = "AdBlockList.txt";

    private readonly FrozenSet<string> _blocked;
    private readonly FrozenSet<string> _allowed;
    private readonly IDisposable _settingsSubscription;

    // Written on the UI thread when the setting changes, read on Chromium's IO thread.
    private volatile bool _enabled;

    public AdBlocker(ILogger<AdBlocker> logger, ISettingsManager settingsManager)
    {
        (_blocked, _allowed) = LoadRules(logger);

        _enabled = settingsManager.Get<BrowserSettings>().BlockAdsAndTrackers;
        _settingsSubscription = settingsManager
            .GetChanges<BrowserSettings>()
            .Subscribe(settings => _enabled = settings.BlockAdsAndTrackers);
    }

    /// <summary>
    /// Whether the request for <paramref name="url"/> should be dropped.
    /// </summary>
    /// <remarks>
    /// Never throws, and answers false whenever it isn't sure: showing an ad is a much
    /// better failure than breaking a page the user is trying to read.
    /// </remarks>
    public bool ShouldBlock(string? url, CefResourceType resourceType)
    {
        if (!_enabled) return false;

        // The page the user navigated to is always allowed, so a bad rule can never
        // strand them on an error page. Everything the page then pulls in is fair game,
        // including sub-frames, which is how most ads arrive.
        if (resourceType is CefResourceType.MainFrame) return false;

        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        // Leave our own schemes (notably `nxm://`) alone; the download handoff depends
        // on them reaching the navigation handler untouched.
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = uri.Host;
        if (host.Length == 0) return false;

        if (Matches(_allowed, host)) return false;
        return Matches(_blocked, host);
    }

    /// <summary>
    /// Whether <paramref name="host"/> or any of its parent domains is in <paramref name="rules"/>.
    /// </summary>
    private static bool Matches(FrozenSet<string> rules, string host)
    {
        if (rules.Count == 0) return false;
        if (rules.Contains(host)) return true;

        // Walk up one domain label at a time, so a rule for `doubleclick.net` catches
        // `ads.g.doubleclick.net` while a rule can never match a bare TLD.
        var start = 0;
        while (true)
        {
            var dot = host.IndexOf('.', start);
            if (dot < 0 || dot + 1 >= host.Length) return false;

            var parent = host[(dot + 1)..];
            if (!parent.Contains('.')) return false;
            if (rules.Contains(parent)) return true;

            start = dot + 1;
        }
    }

    private static (FrozenSet<string> Blocked, FrozenSet<string> Allowed) LoadRules(ILogger logger)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var assembly = typeof(AdBlocker).Assembly;

            // Matched on the file name so moving the rules file doesn't silently turn
            // filtering off.
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(RulesFileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                logger.LogWarning("`{FileName}` isn't embedded in the assembly; the in-app browser won't filter anything", RulesFileName);
                return (FrozenSet<string>.Empty, FrozenSet<string>.Empty);
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                logger.LogWarning("Unable to read `{ResourceName}`; the in-app browser won't filter anything", resourceName);
                return (FrozenSet<string>.Empty, FrozenSet<string>.Empty);
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var rule = line.AsSpan().Trim();
                if (rule.IsEmpty || rule[0] == '#') continue;

                if (rule.StartsWith("@@"))
                {
                    var host = rule[2..].Trim();
                    if (!host.IsEmpty) allowed.Add(host.ToString());
                }
                else
                {
                    blocked.Add(rule.ToString());
                }
            }

            logger.LogDebug("In-app browser filter: {BlockedCount} block rules, {AllowedCount} allow rules", blocked.Count, allowed.Count);
        }
        catch (Exception e)
        {
            // Fail open. A browser that shows ads is better than one that won't start.
            logger.LogWarning(e, "Unable to load the in-app browser filter rules");
            return (FrozenSet<string>.Empty, FrozenSet<string>.Empty);
        }

        return (
            blocked.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            allowed.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        );
    }

    public void Dispose() => _settingsSubscription.Dispose();
}
