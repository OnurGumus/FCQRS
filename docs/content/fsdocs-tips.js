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
