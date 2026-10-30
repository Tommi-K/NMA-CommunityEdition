using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NexusMods.App.UI.Pages.Browser;

/// <summary>
/// The script that presses the download button on a Nexus Mods file page, for a tab that
/// was opened to start a download rather than to browse.
/// </summary>
/// <remarks>
/// The button is inside the shadow DOM of Nexus Mods' <c>&lt;mod-file-download&gt;</c>
/// custom element, so the search has to walk shadow roots as well as the page and its
/// same-origin frames. An ordinary <c>querySelectorAll</c> does not cross a shadow
/// boundary and finds nothing at all here, however good the selector.
///
/// Only a confident match is pressed: for a free account the fast button is a purchase
/// page rather than a download, and pressing the real button leaves Nexus Mods' own
/// countdown and handoff to run exactly as they would for a person clicking it.
///
/// When nothing matches, the script writes an inventory of the page -- shadow roots
/// included -- to the console rather than failing quietly. Those lines reach the app log
/// and are the only view of the real markup available from outside.
/// </remarks>
internal static class AutoDownloadScript
{
    /// <summary>
    /// Marks the script's own console output, so it can be told apart from whatever else a
    /// page logs.
    /// </summary>
    public const string LogPrefix = "[nma-autodownload] ";

    /// <summary>
    /// Prefix the script uses to report the <c>nxm://</c> handoff link it found on the
    /// page, after <see cref="LogPrefix"/>.
    /// </summary>
    /// <remarks>
    /// The link comes back over the console rather than as the result of an evaluation.
    /// That reads as an odd channel, and it is, but it is the one that demonstrably works
    /// here: the script's console output reaches the app reliably, while
    /// <c>EvaluateJavaScript</c> returned nothing on this page for reasons it would not
    /// report.
    /// </remarks>
    public const string HandoffPrefix = "handoff ";

    /// <summary>
    /// Prefix the script uses to report that it has given up on the page, after
    /// <see cref="LogPrefix"/>, followed by the id of the file the page is for.
    /// </summary>
    /// <remarks>
    /// The file id is what lets the app tell this apart from a late report about a page the
    /// tab has already moved on from.
    /// </remarks>
    public const string GaveUpPrefix = "gave-up ";

    /// <summary>
    /// Prefix the script uses to report that Nexus Mods answered with its notice that this
    /// address has been suspended for sending too many requests, after <see cref="LogPrefix"/>.
    /// Followed by how many seconds the notice asks to stay away for, zero when it doesn't
    /// say, and the id of the file the page is for.
    /// </summary>
    public const string SuspendedPrefix = "suspended ";

    /// <summary>
    /// Prefix <see cref="BuildPageReader"/>'s script uses to report that reading the page got
    /// nowhere and it has to be loaded after all, after <see cref="LogPrefix"/>, followed by the
    /// id of the file the page is for.
    /// </summary>
    public const string LoadPagePrefix = "load-page ";

    private const string PreferFastPlaceholder = "__PREFER_FAST__";
    private const string SuspensionPlaceholder = "__SUSPENSION__";
    private const string PagePlaceholder = "__PAGE__";
    private const string FileIdPlaceholder = "__FILE_ID__";

    /// <summary>
    /// Builds the script. <paramref name="preferFast"/> picks the premium button.
    /// </summary>
    public static string Build(bool preferFast) => Template
        .Replace(SuspensionPlaceholder, SuspensionFunction)
        .Replace(PreferFastPlaceholder, preferFast ? "true" : "false");

    /// <summary>
    /// Builds a script that gets the download out of another Nexus Mods page without loading it:
    /// run in a page that is already open, it asks for the other page's HTML and reads the link
    /// out of that.
    /// </summary>
    /// <remarks>
    /// Compared with loading the page this is exactly one request: nothing is rendered, none of
    /// its scripts run and nothing it refers to is fetched. It reports like the page's own
    /// script does, with <see cref="HandoffPrefix"/>, <see cref="SuspendedPrefix"/> or
    /// <see cref="GaveUpPrefix"/>, and with <see cref="LoadPagePrefix"/> for anything else.
    /// </remarks>
    /// <param name="page">The page, on the same site as the one this runs in.</param>
    /// <param name="fileId">The file the page is for.</param>
    public static string BuildPageReader(Uri page, string fileId) => PageReaderTemplate
        .Replace(SuspensionPlaceholder, SuspensionFunction)
        .Replace(PagePlaceholder, JsonSerializer.Serialize(page.ToString()))
        .Replace(FileIdPlaceholder, JsonSerializer.Serialize(fileId));

    /// <summary>
    /// Reads a report that starts with <see cref="SuspendedPrefix"/>.
    /// </summary>
    /// <param name="report">What the script said, after <see cref="LogPrefix"/>.</param>
    /// <param name="fileId">The file the page is for.</param>
    /// <param name="retryAfter">How long the notice asks to stay away for, null when it doesn't say.</param>
    public static bool TryParseSuspended(string report, [NotNullWhen(true)] out string? fileId, out TimeSpan? retryAfter)
    {
        fileId = null;
        retryAfter = null;

        if (!report.StartsWith(SuspendedPrefix, StringComparison.Ordinal)) return false;

        var parts = report[SuspendedPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;

        fileId = parts[1];
        if (int.TryParse(parts[0], out var seconds) && seconds > 0) retryAfter = TimeSpan.FromSeconds(seconds);

        return true;
    }

    /// <summary>
    /// While this address is suspended for sending too many requests, Nexus Mods answers every
    /// page with a short notice saying for how long. Only a page that short counts, so a mod
    /// page quoting the same words isn't taken for it. Gives the seconds to stay away for, 0
    /// when the notice doesn't say, and null when this isn't the notice.
    /// </summary>
    private const string SuspensionFunction =
        """
        function suspensionIn(text) {
            if (text.length > 2000) return null;
            if (!/temporarily suspended/i.test(text) || !/number of requests/i.test(text)) return null;

            var wait = text.match(/try again in (\d+)\s*(second|minute|hour)/i);
            if (!wait) return 0;

            return parseInt(wait[1], 10) * { second: 1, minute: 60, hour: 3600 }[wait[2].toLowerCase()];
        }
        """;

    private const string PageReaderTemplate =
        """
        (function () {
            var MARK = '[nma-autodownload] ';
            var page = __PAGE__;
            var wanted = __FILE_ID__;

            __SUSPENSION__

            // A parsed copy has no window to run in: its scripts don't run and nothing it refers
            // to gets loaded.
            function read(html) {
                var doc = new DOMParser().parseFromString(html, 'text/html');
                var nodes = doc.querySelectorAll('mod-file-download[download-url]');

                for (var i = 0; i < nodes.length; i++) {
                    var link = nodes[i].getAttribute('download-url') || '';
                    if (link.indexOf('nxm://') === 0 && nodes[i].getAttribute('file-id') === wanted) return 'handoff ' + link;
                }

                var notWords = doc.querySelectorAll('script, style, noscript');
                for (var n = 0; n < notWords.length; n++) notWords[n].remove();

                var text = ((doc.body && doc.body.textContent) || '').replace(/\s+/g, ' ').trim();
                var suspended = suspensionIn(text);

                return suspended === null ? 'load-page ' + wanted : 'suspended ' + suspended + ' ' + wanted;
            }

            var abort = new AbortController();
            var timeout = setTimeout(function () { abort.abort(); }, 15000);

            // Not kept in the cache: this page is never asked for again, and it is the same size
            // again on disk each time.
            fetch(page, { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'text/html' }, signal: abort.signal })
                .then(function (response) {
                    if (response.status === 404 || response.status === 410) return 'gave-up ' + wanted;
                    if (response.status === 429) return 'suspended 0 ' + wanted;
                    return response.text().then(read);
                })
                .catch(function () { return 'load-page ' + wanted; })
                .then(function (report) {
                    clearTimeout(timeout);
                    console.log(MARK + report);
                });
        })();
        """;

    private const string Template =
        """
        (function () {
            var MARK = '[nma-autodownload] ';

            // One press per document. A fresh navigation gets a fresh window, so this does
            // not stop the next page being handled.
            if (window.__nmaAutoDownloadArmed) return;
            window.__nmaAutoDownloadArmed = true;

            var preferFast = __PREFER_FAST__;
            var started = Date.now();
            var deadline = started + 30000;
            var wanted = (location.search.match(/[?&]file_id=(\d+)/) || [])[1] || null;

            var snapshots = [
                { at: started + 6000, note: '[6s]', done: false },
                { at: started + 15000, note: '[15s]', done: false }
            ];

            var timer = null;
            var observer = null;
            var finished = false;
            var nextIdSearch = 0;
            var nextLabelSearch = 0;
            var nextSuspensionCheck = 0;
            var nearMisses = [];

            function say(text) {
                console.log(MARK + text);
            }

            // Same-origin frames only; a cross-origin one throws on access and is skipped.
            function documents() {
                var list = [document];
                var frames = document.querySelectorAll('iframe, frame');

                for (var i = 0; i < frames.length; i++) {
                    try {
                        var doc = frames[i].contentDocument;
                        if (doc && doc.querySelectorAll) list.push(doc);
                    } catch (e) {
                        // cross-origin, nothing to look at
                    }
                }

                return list;
            }

            // Every place elements can hide: the documents, plus the shadow root of every
            // custom element in them, recursively. Nexus Mods' download button lives in
            // one of these, which is why searching the documents alone never finds it.
            function roots() {
                var list = [];
                var docs = documents();

                for (var i = 0; i < docs.length; i++) collectRoots(docs[i], list);
                return list;
            }

            function collectRoots(root, list) {
                if (list.length > 60) return;   // a pathological page shouldn't hang the tab
                list.push(root);

                var nodes;
                try {
                    nodes = root.querySelectorAll('*');
                } catch (e) {
                    return;
                }

                for (var i = 0; i < nodes.length; i++) {
                    if (nodes[i].shadowRoot) collectRoots(nodes[i].shadowRoot, list);
                }
            }

            function label(el) {
                return ((el && el.textContent) || '').replace(/\s+/g, ' ').trim();
            }

            function pageText() {
                return ((document.body && document.body.innerText) || '').replace(/\s+/g, ' ').trim();
            }

            __SUSPENSION__

            function clickable(el) {
                if (!el) return false;
                if (el.disabled) return false;
                if (el.getAttribute && el.getAttribute('aria-disabled') === 'true') return false;

                var rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            }

            function describe(el) {
                var cls = typeof el.className === 'string' ? el.className.split(/\s+/).slice(0, 2).join('.') : '';
                return (el.id ? '#' + el.id : '') + (cls ? '.' + cls : '') +
                       '<' + el.tagName.toLowerCase() + '>"' + label(el).slice(0, 30) + '"';
            }

            // The page states the link its own button would use, so it can be taken
            // directly. Matched on the file in the query string: a page can carry more
            // than one download element, and starting the wrong file would be worse than
            // not starting one at all.
            function handoffLink() {
                var nodes = document.querySelectorAll('mod-file-download[download-url]');
                var sole = null;

                for (var i = 0; i < nodes.length; i++) {
                    var link = nodes[i].getAttribute('download-url') || '';
                    if (link.indexOf('nxm://') !== 0) continue;

                    if (wanted) {
                        if (nodes[i].getAttribute('file-id') === wanted) return link;
                        continue;
                    }

                    if (sole) return null;   // ambiguous, leave it to the button
                    sole = link;
                }

                return sole;
            }

            // The label that matches is often a span or div inside the real control, and
            // clicking it is not always the same as clicking the control, so this climbs
            // to the nearest thing that behaves like a button. Crossing out of a shadow
            // root needs the host, which parentElement does not give.
            function actionable(el) {
                var node = el;

                for (var depth = 0; node && depth < 6; depth++) {
                    var tag = node.tagName ? node.tagName.toLowerCase() : '';
                    if (tag === 'button' || tag === 'a') return node;
                    if (node.getAttribute && node.getAttribute('role') === 'button') return node;

                    node = node.parentElement || (node.parentNode && node.parentNode.host) || null;
                }

                return el;   // nothing better; a click here still bubbles
            }

            function controlsIn(root) {
                try {
                    return root.querySelectorAll('a, button, [role="button"], input[type="submit"], input[type="button"]');
                } catch (e) {
                    return [];
                }
            }

            var idSelectors = preferFast
                ? ['#fastDownloadButton', '#slowDownloadButton',
                   '[id*="fast-download" i]', '[id*="slow-download" i]',
                   '[id*="fastdownload" i]', '[id*="slowdownload" i]']
                : ['#slowDownloadButton', '[id*="slow-download" i]', '[id*="slowdownload" i]'];

            // Deliberately narrow. A plain "Download" is not enough to act on here.
            var labelPatterns = preferFast
                ? [/^fast download$/i, /fast download/i, /^slow download$/i, /slow download/i]
                : [/^slow download$/i, /slow download/i, /download \(slow\)/i];

            function findById() {
                var all = roots();

                for (var r = 0; r < all.length; r++) {
                    for (var i = 0; i < idSelectors.length; i++) {
                        var hit;
                        try {
                            hit = all[r].querySelector(idSelectors[i]);
                        } catch (e) {
                            continue;   // selector unsupported, try the next
                        }
                        if (clickable(hit)) return hit;
                    }
                }

                return null;
            }

            // Every element in every root, not just the obvious tags: the button may be a
            // div or a span carrying a click handler. The most specific match wins, so a
            // wrapper whose text merely contains the label is not pressed in its place.
            function findByLabel() {
                var all = roots();
                var best = null;
                var bestDescendants = 1e9;

                for (var r = 0; r < all.length; r++) {
                    var nodes;
                    try {
                        nodes = all[r].querySelectorAll('*');
                    } catch (e) {
                        continue;
                    }

                    for (var i = 0; i < nodes.length; i++) {
                        var el = nodes[i];
                        var text = label(el);

                        // A long label means a container that happens to include the button.
                        if (!text || text.length > 60) continue;

                        var matched = false;
                        for (var p = 0; p < labelPatterns.length; p++) {
                            if (labelPatterns[p].test(text)) { matched = true; break; }
                        }
                        if (!matched) continue;

                        // "Slow download. Wait more." is the button counting down. Pressing
                        // it then does nothing, so this waits for the wording to settle
                        // rather than spending the one press on a button that isn't ready.
                        if (/\bwait\b/i.test(text)) {
                            if (nearMisses.length < 8) nearMisses.push(describe(el) + ' counting down');
                            continue;
                        }

                        if (!clickable(el)) {
                            if (nearMisses.length < 8) {
                                var rect = el.getBoundingClientRect();
                                nearMisses.push(describe(el) + ' rect=' + Math.round(rect.width) + 'x' +
                                    Math.round(rect.height) + (el.disabled ? ' disabled' : ''));
                            }
                            continue;
                        }

                        var descendants = el.querySelectorAll('*').length;
                        if (descendants < bestDescendants) {
                            best = el;
                            bestDescendants = descendants;
                        }
                    }
                }

                return best;
            }

            // Last resort, scoped to the element that exists to download this one file: if
            // it offers exactly one download control, that control is unambiguous even
            // when its wording isn't one we recognise. Anything that reads as the premium
            // upsell is excluded, since for a free account that is a purchase page.
            function findInDownloadComponent() {
                var docs = documents();

                for (var d = 0; d < docs.length; d++) {
                    var hosts;
                    try {
                        hosts = docs[d].querySelectorAll('mod-file-download');
                    } catch (e) {
                        continue;
                    }

                    for (var h = 0; h < hosts.length; h++) {
                        if (!hosts[h].shadowRoot) continue;

                        var nodes = controlsIn(hosts[h].shadowRoot);
                        var candidates = [];

                        for (var i = 0; i < nodes.length; i++) {
                            if (!clickable(nodes[i])) continue;

                            var text = label(nodes[i]);
                            if (!/download/i.test(text)) continue;
                            if (!preferFast && /premium|upgrade|trial|fast/i.test(text)) continue;

                            candidates.push(nodes[i]);
                        }

                        if (candidates.length === 1) return candidates[0];
                    }
                }

                return null;
            }

            // Site navigation swamps any listing of this page -- the header alone runs to
            // dozens of links -- so it is left out to leave room for the page body.
            function isChrome(el) {
                try {
                    return !!(el.closest && el.closest(
                        'header, nav, footer, [role="navigation"], [role="banner"], [role="contentinfo"]'));
                } catch (e) {
                    return false;
                }
            }

            function emit(note, name, entries, perLine) {
                if (!entries.length) {
                    say(note + ' ' + name + ': none');
                    return;
                }

                // Chunked: one very long console line risks being truncated.
                for (var k = 0; k < entries.length; k += perLine) {
                    say(note + ' ' + name + '[' + k + ']: ' + entries.slice(k, k + perLine).join(' | '));
                }
            }

            // The contents of every shadow root, which is where the button actually is and
            // what every earlier inventory was blind to.
            function reportShadowHosts(note) {
                var docs = documents();
                var found = [];

                for (var d = 0; d < docs.length; d++) {
                    var nodes;
                    try {
                        nodes = docs[d].querySelectorAll('*');
                    } catch (e) {
                        continue;
                    }

                    for (var i = 0; i < nodes.length && found.length < 10; i++) {
                        if (!nodes[i].shadowRoot) continue;

                        var ctrls = controlsIn(nodes[i].shadowRoot);
                        var inner = [];
                        for (var c = 0; c < ctrls.length && c < 10; c++) inner.push(describe(ctrls[c]));

                        found.push(nodes[i].tagName.toLowerCase() + ' => ' + (inner.join(' | ') || 'no controls'));
                    }
                }

                emit(note, 'shadow-hosts', found, 2);
            }

            function inventory(note) {
                var docs = documents();
                var frames = document.querySelectorAll('iframe, frame');
                var all = roots();

                say(note + ' url=' + location.href);
                say(note + ' title="' + (document.title || '').slice(0, 70) + '" frames=' +
                    frames.length + ' reachable=' + (docs.length - 1) + ' roots=' + all.length);

                // A page this short is a notice of some kind rather than a mod page, and what
                // it says is the whole explanation.
                var text = pageText();
                if (text.length <= 2000) say(note + ' body="' + text.slice(0, 500) + '"');

                reportShadowHosts(note);
                emit(note, 'near-miss', nearMisses, 4);

                var ids = [];
                for (var r = 0; r < all.length; r++) {
                    var withId;
                    try {
                        withId = all[r].querySelectorAll('[id*="ownload"], [class*="ownload"]');
                    } catch (e) {
                        continue;
                    }
                    for (var i = 0; i < withId.length && ids.length < 15; i++) ids.push(describe(withId[i]));
                }
                emit(note, 'download-ish', ids, 5);

                var texts = [];
                for (var r2 = 0; r2 < all.length; r2++) {
                    var nodes;
                    try {
                        nodes = all[r2].querySelectorAll('*');
                    } catch (e) {
                        continue;
                    }

                    for (var m = 0; m < nodes.length && texts.length < 20; m++) {
                        var el = nodes[m];
                        if (el.children && el.children.length > 2) continue;   // containers, not labels
                        var t = label(el);
                        if (!t || t.length > 40) continue;
                        if (!/download|slow|fast|premium/i.test(t)) continue;
                        if (isChrome(el)) continue;
                        texts.push(describe(el));
                    }
                }
                emit(note, 'download-text', texts, 5);
            }

            function finish() {
                finished = true;
                if (timer !== null) clearInterval(timer);
                if (observer !== null) observer.disconnect();
            }

            function handOff(link) {
                finish();
                say('handoff ' + link);

                // The link was all this page was loaded for. The images and scripts still to
                // come would only be more requests counted against this address.
                window.stop();
            }

            // Polled rather than pressed once: the button is commonly disabled behind a
            // countdown, and waiting for it to become usable is the point. The script goes in
            // as soon as the page starts loading, so the polling starts while it is still
            // arriving and the link below is taken the moment it is parsed.
            function tick() {
                if (finished) return;

                // Preferred over pressing anything: no countdown to sit through, and
                // nothing that breaks when the page is redesigned.
                var link = handoffLink();
                if (link) {
                    handOff(link);
                    return;
                }

                var now = Date.now();

                // Looked for first, and not until the page has been parsed: half a notice may
                // not say how long for yet.
                if (document.readyState !== 'loading' && now >= nextSuspensionCheck) {
                    nextSuspensionCheck = now + 250;

                    var suspended = suspensionIn(pageText());
                    if (suspended !== null) {
                        finish();
                        say('suspended ' + suspended + ' ' + (wanted || ''));
                        return;
                    }
                }

                // Nothing is pressed before the page has finished loading: a control can be in
                // the markup before the page's scripts have wired it up, and the one press
                // would be spent on a button that does nothing.
                if (document.readyState === 'complete' && now >= nextIdSearch) {
                    nextIdSearch = now + 250;
                    var button = findById();

                    // Walking every root is far heavier than checking a handful of ids, so
                    // the broader searches run about once a second.
                    if (!button && now >= nextLabelSearch) {
                        nextLabelSearch = now + 1000;
                        nearMisses = [];
                        button = findByLabel() || findInDownloadComponent();
                    }

                    if (button) {
                        finish();

                        var target = actionable(button);
                        say('pressing ' + describe(target) +
                            (target === button ? '' : ' (via label ' + describe(button) + ')'));
                        target.click();
                        return;
                    }
                }

                for (var sn = 0; sn < snapshots.length; sn++) {
                    if (!snapshots[sn].done && now > snapshots[sn].at) {
                        snapshots[sn].done = true;
                        inventory(snapshots[sn].note);
                    }
                }

                if (now > deadline) {
                    finish();
                    inventory('[gave up]');

                    // Said outright, so the app moves on now rather than when its own wait
                    // runs out.
                    say('gave-up ' + (wanted || ''));
                }
            }

            tick();

            if (!finished) {
                timer = setInterval(tick, 50);

                // The link is near the end of the page, so the parser can reach the end in the
                // time between two ticks, and the page's own scripts then start up and make
                // requests of their own. This acts on the link as soon as it has been parsed.
                if (window.MutationObserver) {
                    observer = new window.MutationObserver(function () {
                        if (finished) return;

                        var link = handoffLink();
                        if (link) handOff(link);
                    });

                    observer.observe(document, { childList: true, subtree: true });
                }
            }
        })();
        """;
}
