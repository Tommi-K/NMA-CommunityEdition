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

    private const string PreferFastPlaceholder = "__PREFER_FAST__";

    /// <summary>
    /// Builds the script. <paramref name="preferFast"/> picks the premium button.
    /// </summary>
    public static string Build(bool preferFast) => Template
        .Replace(PreferFastPlaceholder, preferFast ? "true" : "false");

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

            var snapshots = [
                { at: started + 6000, note: '[6s]', done: false },
                { at: started + 15000, note: '[15s]', done: false }
            ];

            var ticks = 0;
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
                var wanted = (location.search.match(/[?&]file_id=(\d+)/) || [])[1] || null;
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

            // Polled rather than pressed once: the button is commonly disabled behind a
            // countdown, and waiting for it to become usable is the point.
            var timer = setInterval(function () {
                ticks++;

                // Preferred over pressing anything: no countdown to sit through, and
                // nothing that breaks when the page is redesigned.
                var link = handoffLink();
                if (link) {
                    clearInterval(timer);
                    say('handoff ' + link);
                    return;
                }

                var button = findById();

                // Walking every root is far heavier than checking a handful of ids, so the
                // broader searches run about once a second rather than on every tick.
                if (!button && ticks % 4 === 0) {
                    nearMisses = [];
                    button = findByLabel() || findInDownloadComponent();
                }

                if (button) {
                    clearInterval(timer);

                    var target = actionable(button);
                    say('pressing ' + describe(target) +
                        (target === button ? '' : ' (via label ' + describe(button) + ')'));
                    target.click();
                    return;
                }

                for (var sn = 0; sn < snapshots.length; sn++) {
                    if (!snapshots[sn].done && Date.now() > snapshots[sn].at) {
                        snapshots[sn].done = true;
                        inventory(snapshots[sn].note);
                    }
                }

                if (Date.now() > deadline) {
                    clearInterval(timer);
                    inventory('[gave up]');
                }
            }, 250);
        })();
        """;
}
