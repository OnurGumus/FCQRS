let currentTip = null;
let currentTipElement = null;

function hideTip(evt, name, unique) {
    const el = document.getElementById(name);
    el.style.display = "none";
    currentTip = null;
}

function hideUsingEsc(e) {
    hideTip(e, currentTipElement, currentTip);
}

// Custom formatting: add line breaks before F# keywords and pipes
function formatTooltipContent(el) {
    if (el.dataset.formatted) return;
    el.dataset.formatted = 'true';

    let html = el.innerHTML;

    // Add line break before 'override' (with leading spaces)
    html = html.replace(/(\s{2,})(override\s)/g, '<br>$1$2');

    // Add line break before 'member' (with leading spaces)
    html = html.replace(/(\s{2,})(member\s)/g, '<br>$1$2');

    // Add line break before 'abstract' (with leading spaces)
    html = html.replace(/(\s{2,})(abstract\s)/g, '<br>$1$2');

    // Add line break before 'static' (with leading spaces)
    html = html.replace(/(\s{2,})(static\s)/g, '<br>$1$2');

    // Add line break before 'interface' (with leading spaces)
    html = html.replace(/(\s{2,})(interface\s)/g, '<br>$1$2');

    // Add line break before 'new' for constructors (with leading spaces)
    html = html.replace(/(\s{2,})(new\s*:)/g, '<br>$1$2');

    // Add line break before pipe '|' for discriminated unions (with leading spaces)
    html = html.replace(/(\s{2,})(\|)/g, '<br>$1$2');

    // Add line break before words ending with ':' (member names, with leading spaces and 4 nbsp indent)
    html = html.replace(/(\s{2,})(\w+:)/g, '<br>&nbsp;&nbsp;&nbsp;&nbsp;$2');

    // Add line break before '}' closing braces
    html = html.replace(/(\s*)(\})/g, '<br>$1$2');

    // Color type names after ':' (multiple words like 'string option', generics, and 'T style params)
    // Match sequences of type-like words until we hit -> or <br> or other delimiters
    html = html.replace(/(:\s*)((?:['A-Za-z_][\w]*(?:&lt;[^&]*&gt;)?\s*)+)(?=-&gt;|<br>|$|\))/g, '$1<span class="fsdocs-type">$2</span>');

    // Color types after 'of' in discriminated union cases (lines starting with | or = |)
    // Match: "| CaseName of TypeExpression" and color everything after "of"
    html = html.replace(/((?:<br>\s*|=\s*)\|[^<]*?\bof\s+)((?:['A-Za-z_][\w]*(?:&lt;[^&]*&gt;)?\s*)+)/g, '$1<span class="fsdocs-type">$2</span>');

    // Color 'of' keyword in discriminated union cases (lines starting with | or = |)
    html = html.replace(/((?:<br>\s*|=\s*)\|[^<]*?)\bof\b/g, '$1<span class="fsdocs-keyword">of</span>');

    // Color F# keywords (case sensitive) - type, interface, member, override, module, abstract, val
    // Use negative lookbehind to avoid matching inside class names like "fsdocs-type"
    html = html.replace(/(?<![-"'])\b(type|interface|member|override|module|abstract|val)\b(?![-"'])/g, '<span class="fsdocs-keyword">$1</span>');

    // Color multi-word keywords
    html = html.replace(/\bunion case\b/g, '<span class="fsdocs-keyword">union case</span>');

    // Color first word after 'type', 'interface', and 'module' keywords as type
    html = html.replace(/(<span class="fsdocs-keyword">(?:type|interface|module)<\/span>\s+)(['A-Za-z_][\w]*)/g, '$1<span class="fsdocs-type">$2</span>');

    // Color words before and after '->' (function arrow, encoded as -&gt;)
    html = html.replace(/(['A-Za-z_][\w]*)(\s*-&gt;)/g, '<span class="fsdocs-type">$1</span>$2');
    html = html.replace(/(-&gt;\s*)(['A-Za-z_][\w]*)/g, '$1<span class="fsdocs-type">$2</span>');

    // Color words before and after '*' (tuple)
    html = html.replace(/(['A-Za-z_][\w]*)(\s*\*)/g, '<span class="fsdocs-type">$1</span>$2');
    html = html.replace(/(\*\s*)(['A-Za-z_][\w]*)/g, '$1<span class="fsdocs-type">$2</span>');

    // Color all words starting with apostrophe as type (generic type params like 'T, 'TEvent)
    // Avoid matching inside already-processed spans or HTML tags
    html = html.replace(/(?<![<>\w"])('[A-Za-z_][\w]*)/g, '<span class="fsdocs-type">$1</span>');

    // Color words ending with < (generic types like List<, Option<, IEnumerable<)
    html = html.replace(/([A-Za-z_][\w]*)(&lt;)/g, '<span class="fsdocs-type">$1</span>$2');

    // Replace typeparam tags with just the type name in type color
    // &amp;lt;typeparam name="'EventDetails"&amp;gt; becomes <EventDetails> in aqua
    // Double-encoded: &amp;lt; and &amp;gt;
    html = html.replace(/&amp;lt;typeparam\s+name=["']?'?(\w+)["']?&amp;gt;/g, '<span class="fsdocs-type">&lt;$1&gt;</span>');

    // Also handle closing typeparam tags - just remove them
    html = html.replace(/&amp;lt;\/typeparam&amp;gt;/g, '');

    // Color summary content green and hide the summary tags
    html = html.replace(/&lt;summary&gt;([\s\S]*?)&lt;\/summary&gt;/g, '<span class="fsdocs-summary">$1</span>');

    el.innerHTML = html;
}

function showTip(evt, name, unique, owner) {
    document.onkeydown = hideUsingEsc;
    if (currentTip === unique) return;
    currentTip = unique;
    currentTipElement = name;

    const offset = 20;
    let x = evt.clientX;
    let y = evt.clientY + offset;

    const el = document.getElementById(name);

    // Format the tooltip content before showing
    formatTooltipContent(el);

    el.style.position = "absolute";
    el.style.display = "block";
    el.style.left = `${x}px`;
    el.style.top = `${y}px`;
    const maxWidth = document.documentElement.clientWidth - x - 16;
    el.style.maxWidth = `${maxWidth}px`;

    const rect = el.getBoundingClientRect();
    // Move tooltip if it is out of sight
    if (rect.bottom > window.innerHeight) {
        y = y - el.clientHeight - offset;
        el.style.top = `${y}px`;
    }

    if (rect.right > window.innerWidth) {
        x = y - el.clientWidth - offset;
        el.style.left = `${x}px`;
        const maxWidth = document.documentElement.clientWidth - x - 16;
        el.style.maxWidth = `${maxWidth}px`;
    }
}

function Clipboard_CopyTo(value) {
    const tempInput = document.createElement("input");
    tempInput.value = value;
    document.body.appendChild(tempInput);
    tempInput.select();
    document.execCommand("copy");
    document.body.removeChild(tempInput);
}

window.showTip = showTip;
window.hideTip = hideTip;
// Used by API documentation
window.Clipboard_CopyTo = Clipboard_CopyTo;

// ---------------------------------------------------------------------------
// F# / C# language tabs.
//
// Authoring: put an empty <div class="cs-alt"></div> immediately before a C#
// code block that is the twin of the F# block just above it. This script pairs
// the F# block (preceding the marker) with the C# block (following it) into a
// tabbed widget. The choice is sticky and global — pick C# once and every
// tabbed block on the site shows C# (remembered via localStorage).
// ---------------------------------------------------------------------------
(function () {
    const STORAGE_KEY = "fcqrs-lang";
    const preferred = () => localStorage.getItem(STORAGE_KEY) || "fsharp";

    // The outermost element of a rendered code block: the table.pre wrapper if
    // present (C# fences), otherwise the bare pre (F# .fsx script snippets).
    function blockRoot(codeEl) {
        return codeEl.closest("table.pre") || codeEl.closest("pre");
    }

    function nearest(codeEls, marker, lang, following) {
        const want = following
            ? Node.DOCUMENT_POSITION_FOLLOWING
            : Node.DOCUMENT_POSITION_PRECEDING;
        const order = following ? codeEls : codeEls.slice().reverse();
        for (const c of order) {
            if (c.getAttribute("lang") !== lang) continue;
            if (marker.compareDocumentPosition(c) & want) return c;
        }
        return null;
    }

    function setLang(lang) {
        localStorage.setItem(STORAGE_KEY, lang);
        applyLang(lang);
    }

    // Global toggle: every pane of the chosen language shows, the rest hide.
    function applyLang(lang) {
        document.querySelectorAll(".lang-tab").forEach(function (b) {
            b.classList.toggle("active", b.dataset.lang === lang);
        });
        document.querySelectorAll(".lang-pane").forEach(function (p) {
            p.style.display = p.dataset.lang === lang ? "" : "none";
        });
    }

    // Pair each marker's preceding F# block with its following C# block. The
    // blocks are NOT reparented (so every fsdocs style, incl. the #content >
    // pre.fssnip border, keeps applying); we only insert a tab bar before the F#
    // block and toggle display on the two blocks in place.
    function buildPairs(root) {
        const markers = Array.prototype.slice.call(root.querySelectorAll(".cs-alt"));
        const codeEls = Array.prototype.slice.call(root.querySelectorAll("code[lang]"));

        markers.forEach(function (marker) {
            const cs = nearest(codeEls, marker, "csharp", true);
            const fs = nearest(codeEls, marker, "fsharp", false);
            if (!cs || !fs) return;
            const fsRoot = blockRoot(fs);
            const csRoot = blockRoot(cs);
            if (!fsRoot || !csRoot || fsRoot === csRoot) return;

            const bar = document.createElement("div");
            bar.className = "lang-tabbar";
            [["fsharp", "F#"], ["csharp", "C#"]].forEach(function (pair) {
                const btn = document.createElement("button");
                btn.type = "button";
                btn.className = "lang-tab";
                btn.dataset.lang = pair[0];
                btn.textContent = pair[1];
                btn.addEventListener("click", function () { setLang(pair[0]); });
                bar.appendChild(btn);
            });

            fsRoot.parentNode.insertBefore(bar, fsRoot);
            fsRoot.classList.add("lang-pane");
            fsRoot.dataset.lang = "fsharp";
            csRoot.classList.add("lang-pane");
            csRoot.dataset.lang = "csharp";

            if (marker.parentNode) marker.parentNode.removeChild(marker);
        });
    }

    // Resolve a sibling asset (vendored next to this script in /content/), so the
    // highlighter loads with no CDN — works offline and under a strict CSP.
    function assetUrl(name) {
        const tag = document.querySelector('script[src*="fsdocs-tips.js"]');
        if (tag && tag.src) return tag.src.replace(/fsdocs-tips\.js.*$/, name);
        return "content/" + name;
    }

    // Re-highlight ONLY C# blocks: fsdocs colours C# with its F# lexer, so C#
    // type/method names fall back to the plain identifier colour. F# blocks are
    // left exactly as fsdocs rendered them, preserving their hover tooltips.
    function highlightCode() {
        if (!window.hljs) return;
        document.querySelectorAll('code[lang="csharp"]').forEach(function (el) {
            if (el.dataset.hljs) return;
            el.dataset.hljs = "1";
            el.textContent = el.textContent; // drop fsdocs' weak C# spans → raw text
            el.classList.add("language-csharp");
            try { window.hljs.highlightElement(el); } catch (e) { /* ignore */ }
        });
    }

    // Load highlight.js core (vendored next to this script), then highlight C#.
    function loadHighlighter() {
        if (!document.querySelector('code[lang="csharp"]')) return;
        if (window.hljs) { highlightCode(); return; }
        const core = document.createElement("script");
        core.src = assetUrl("highlight.min.js");
        core.onload = highlightCode;
        document.head.appendChild(core);
    }

    function init() {
        const root = document.getElementById("content") || document.body;
        buildPairs(root);
        applyLang(preferred());
        loadHighlighter();
    }

    // The script is loaded as a deferred module, so the DOM may already be ready.
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();

// ---------------------------------------------------------------------------
// Course navigation and learning aids. This runs on authored and generated API
// pages, keeping the whole documentation site consistent without duplicating
// presentation markup in every source page.
// ---------------------------------------------------------------------------
(function () {
    const lessons = {
        "overview.html": ["See why command decisions and screen queries need different models", "Place aggregates, projections, sagas, and correlation ids in one system", "Decide whether FCQRS fits the rules and failure modes of your application"],
        "get-started.html": ["Send a command to one aggregate and persist its event", "Project that event into query-shaped data", "Wait for the projection before reading the result"],
        "tutorial/1-the-aggregate.html": ["Model validated domain values, commands, events, and state", "Write pure decision and fold functions", "Explain which consistency boundary the aggregate protects"],
        "tutorial/2-running-it.html": ["Host an aggregate and projection in one actor system", "Follow one correlation id through write and read paths", "Run the application twice and observe recovery"],
        "tutorial/3-adding-a-saga.html": ["Split rules between two independent aggregates", "Coordinate them with a durable saga", "Make repeated commands safe during recovery"],
        "tutorial/4-testing-and-evolution.html": ["Test decisions, folds, histories, and saga states", "Change persisted contracts without losing old events", "Rebuild derived data deliberately"],
        "tutorial/5-production.html": ["Choose durable storage and restart-safe projections", "Prepare diagnostics, backups, and failure rehearsals", "Know when a single node is ready to become a cluster"],
        "concepts/cqrs-and-event-sourcing.html": ["Separate the model that decides from the model that answers queries", "Explain why events, rather than current state, are the source of truth", "Trace what crosses the write and read boundary"],
        "concepts/aggregates.html": ["Explain how an aggregate serializes decisions for one entity", "Distinguish persisted, deferred, and ignored outcomes", "Keep event replay deterministic"],
        "concepts/read-models.html": ["Turn journal events into query-shaped data", "Keep projection data and offsets in one transaction", "Explain why read models are disposable"],
        "concepts/sagas.html": ["Recognize work that crosses aggregate boundaries", "Model a saga as a durable state machine", "Design repeated commands and recovery safely"],
        "concepts/consistency-and-recovery.html": ["Use correlation ids for read-your-writes coordination", "Distinguish journal versions, offsets, and snapshots", "Reason about restarts and eventual consistency"],
        "concepts/csharp-interop.html": ["Represent FCQRS messages with C# union types", "Understand how F# and C# messages are serialized", "Choose the language boundary deliberately"],
        "how-to/define-an-aggregate.html": ["Choose an aggregate boundary and its domain types", "Implement the decision and fold functions", "Select the correct event action and snapshot policy"],
        "how-to/test-your-domain.html": ["Test a decision table without Akka.NET", "Verify folds against complete histories", "Cover replay and retry behaviour"],
        "how-to/evolve-events.html": ["Keep existing journal entries readable", "Choose an event-change strategy", "Prove compatibility before deployment"],
        "how-to/add-a-projection.html": ["Apply events and advance the offset atomically", "Resume from the last committed position", "Expose projection failures instead of hiding them"],
        "how-to/read-your-writes.html": ["Subscribe before sending a command", "Wait for the projection that serves the next query", "Avoid treating notification delivery as durable messaging"],
        "how-to/rebuild-a-read-model.html": ["Prepare the journal and schema for replay", "Choose an in-place or side-by-side rebuild", "Detect and recover from replay failure"],
        "how-to/write-a-saga.html": ["Define saga state, reactions, and command targets", "Register a durable cross-aggregate workflow", "Make recovery commands retry-safe"],
        "how-to/dispatch-async-effects.html": ["Dispatch best-effort work outside event replay", "Register and test the effect runner", "Choose this only when work may safely be lost"],
        "how-to/configure-the-database.html": ["Select a supported persistence provider", "Configure journal, query journal, and snapshots together", "Verify the connection before handling traffic"],
        "how-to/observability.html": ["Follow a correlation id through logs and traces", "Keep span names low-cardinality and payloads private", "Add alerts for the failure boundaries FCQRS cannot hide"],
        "how-to/use-from-csharp.html": ["Define aggregates with C# union types", "Register FCQRS through the hosting APIs", "Send commands and test the domain without starting the host"],
        "configuration.html": ["Understand what the embedded defaults configure", "Override persistence, diagnostics, and snapshot settings", "Prepare consistent configuration for multiple nodes"]
    };

    const tutorialSteps = [
        ["1-the-aggregate.html", "Model"],
        ["2-running-it.html", "Run"],
        ["3-adding-a-saga.html", "Coordinate"],
        ["4-testing-and-evolution.html", "Test"],
        ["5-production.html", "Operate"]
    ];

    function pageKey() {
        let path = location.pathname.replace(/^.*?\/FCQRS\//, "").replace(/^\//, "");
        return path || "index.html";
    }

    function pageKind(key) {
        if (/^tutorial\/\d-/.test(key)) return "Tutorial chapter";
        if (key === "tutorial/index.html") return "Guided course";
        if (/^concepts\//.test(key)) return key.endsWith("index.html") ? "Concept map" : "Concept";
        if (/^how-to\//.test(key)) return key.endsWith("index.html") ? "Task library" : "How-to guide";
        if (key === "get-started.html") return "Start here";
        if (key === "overview.html") return "Orientation";
        if (key === "configuration.html") return "Reference";
        if (/reference\//.test(key)) return "API reference";
        return "Documentation";
    }

    function readingMinutes(root) {
        const copy = root.cloneNode(true);
        copy.querySelectorAll("pre, table.pre, script, .fsdocs-tip, .learning-outcomes").forEach(function (el) { el.remove(); });
        const words = (copy.textContent || "").trim().split(/\s+/).filter(Boolean).length;
        return Math.max(2, Math.ceil(words / 220));
    }

    function addMeta(root, key) {
        const isApi = key.indexOf("reference/") >= 0;
        const h1 = root.querySelector("h1") || (isApi ? root.querySelector("h2") : null);
        if (!h1) return;
        if (isApi && h1.tagName === "H2") h1.classList.add("api-page-title");
        const meta = document.createElement("div");
        meta.className = "learning-meta";
        meta.innerHTML = '<span class="page-kind">' + pageKind(key) + '</span><span>' + readingMinutes(root) + ' min read</span>';
        h1.parentNode.insertBefore(meta, h1);

        let lead = h1.nextElementSibling;
        while (lead && lead.tagName !== "P" && lead.tagName !== "H2") lead = lead.nextElementSibling;
        if (lead && lead.tagName === "P") lead.classList.add("page-lead");
    }

    function addOutcomes(root, key) {
        const items = lessons[key];
        if (!items) return;
        const h1 = root.querySelector("h1");
        if (!h1) return;
        const panel = document.createElement("section");
        panel.className = "learning-outcomes";
        panel.setAttribute("aria-labelledby", "learning-outcomes-title");
        const verb = key.indexOf("how-to/") === 0 ? "You will be able to" : key.indexOf("concepts/") === 0 ? "You will understand" : "By the end";
        panel.innerHTML = '<h2 id="learning-outcomes-title">' + verb + '</h2><ul>' + items.map(function (item) { return "<li>" + item + "</li>"; }).join("") + "</ul>";

        const firstH2 = root.querySelector("h2");
        if (firstH2) root.insertBefore(panel, firstH2);
        else root.appendChild(panel);
    }

    function addTutorialProgress(root, key) {
        const match = key.match(/^tutorial\/(\d)-/);
        if (!match) return;
        const current = Number(match[1]);
        const nav = document.createElement("nav");
        nav.className = "tutorial-progress";
        nav.setAttribute("aria-label", "Tutorial progress");
        tutorialSteps.forEach(function (step, index) {
            const a = document.createElement("a");
            a.href = step[0];
            a.dataset.step = String(index + 1);
            a.textContent = step[1];
            if (index + 1 === current) a.setAttribute("aria-current", "step");
            nav.appendChild(a);
        });
        const panel = root.querySelector(".learning-outcomes");
        if (panel) panel.parentNode.insertBefore(nav, panel);
    }

    function turnListsIntoCards(root, key) {
        const headings = key === "overview.html"
            ? ["Find your path"]
            : key === "tutorial/index.html"
                ? ["The five stages"]
                : key === "concepts/index.html"
                    ? ["Read in this order"]
                    : key === "how-to/index.html"
                        ? ["Model the write side", "Build the read side", "Coordinate work", "Configure and operate"]
                        : [];
        if (!headings.length) return;
        root.classList.add("learning-index");
        root.querySelectorAll("h2").forEach(function (h2) {
            if (headings.indexOf(h2.textContent.trim()) < 0) return;
            let list = h2.nextElementSibling;
            while (list && list.tagName !== "UL" && list.tagName !== "OL" && list.tagName !== "H2") list = list.nextElementSibling;
            if (list && (list.tagName === "UL" || list.tagName === "OL")) list.classList.add("learning-card-list");
        });
    }

    function addCodeCopy(root) {
        root.querySelectorAll("pre.fssnip, table.pre").forEach(function (block) {
            if (block.querySelector(":scope > .copy-code")) return;
            const code = block.querySelector("code") || block;
            const button = document.createElement("button");
            button.type = "button";
            button.className = "copy-code";
            button.textContent = "Copy";
            button.setAttribute("aria-label", "Copy code");
            button.addEventListener("click", function () {
                const value = code.textContent || "";
                const done = function () {
                    button.textContent = "Copied";
                    button.classList.add("copied");
                    window.setTimeout(function () { button.textContent = "Copy"; button.classList.remove("copied"); }, 1600);
                };
                if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(value).then(done);
                else { Clipboard_CopyTo(value); done(); }
            });
            block.appendChild(button);
        });
    }

    function addLessonNav(root) {
        const links = Array.prototype.slice.call(document.querySelectorAll("#fsdocs-main-menu a[href]"));
        const unique = [];
        links.forEach(function (a) {
            const url = new URL(a.href, location.href);
            if (url.origin !== location.origin) return;
            const path = url.pathname;
            if (!unique.some(function (item) { return item.path === path; })) unique.push({ path: path, link: a });
        });
        const at = unique.findIndex(function (item) { return item.path === location.pathname; });
        if (at < 0) return;
        const previous = unique[at - 1];
        const next = unique[at + 1];
        if (!previous && !next) return;
        const nav = document.createElement("nav");
        nav.className = "lesson-nav";
        nav.setAttribute("aria-label", "Continue learning");
        function link(item, label) {
            if (!item) return;
            const a = document.createElement("a");
            a.href = item.link.href;
            a.innerHTML = "<small>" + label + "</small><strong>" + item.link.textContent.trim() + "</strong>";
            nav.appendChild(a);
        }
        link(previous, "Previous");
        link(next, "Continue");
        root.appendChild(nav);
    }

    function addReadingProgress() {
        const bar = document.createElement("div");
        bar.className = "reading-progress";
        bar.setAttribute("aria-hidden", "true");
        document.body.appendChild(bar);
        function update() {
            const range = document.documentElement.scrollHeight - window.innerHeight;
            const value = range > 0 ? Math.min(100, Math.max(0, window.scrollY / range * 100)) : 100;
            bar.style.setProperty("--reading-progress", value + "%");
        }
        update();
        window.addEventListener("scroll", update, { passive: true });
        window.addEventListener("resize", update);
    }

    function initLearningExperience() {
        const root = document.getElementById("content");
        if (!root) return;
        const key = pageKey();
        document.body.classList.add("page-" + pageKind(key).toLowerCase().replace(/[^a-z0-9]+/g, "-"));
        addMeta(root, key);
        addOutcomes(root, key);
        addTutorialProgress(root, key);
        turnListsIntoCards(root, key);
        addCodeCopy(root);
        addLessonNav(root);
        addReadingProgress();

        // The additions above change the page height before deep-linked
        // sections. Restore the requested anchor after the layout settles.
        if (location.hash) {
            window.requestAnimationFrame(function () {
                const name = decodeURIComponent(location.hash.slice(1));
                const target = document.getElementById(name) || document.getElementsByName(name)[0];
                if (target) target.scrollIntoView();
            });
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", initLearningExperience);
    else initLearningExperience();
})();
