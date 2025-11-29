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

    // Color F# keywords (case sensitive) - type, interface, member, override, module
    // Use negative lookbehind to avoid matching inside class names like "fsdocs-type"
    html = html.replace(/(?<![-"'])\b(type|interface|member|override|module)\b(?![-"'])/g, '<span class="fsdocs-keyword">$1</span>');

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
