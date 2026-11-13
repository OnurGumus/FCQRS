// Pair the F# and C# examples marked with .cs-alt, including shell command pairs.
// Both examples remain readable if JavaScript or localStorage is unavailable.
(() => {
  const key = 'fcqrs-lang';
  let language = 'fsharp';
  try { language = localStorage.getItem(key) === 'csharp' ? 'csharp' : 'fsharp'; } catch (_) {}
  // ?lang=csharp (or cs) and ?lang=fsharp (or fs) choose the language, so a link can open a page
  // in C#. Choosing a tab writes the choice into the address, ready to copy.
  const names = { csharp: 'csharp', cs: 'csharp', 'c#': 'csharp', fsharp: 'fsharp', fs: 'fsharp', 'f#': 'fsharp' };
  try {
    const requested = names[(new URLSearchParams(location.search).get('lang') || '').toLowerCase()];
    if (requested) language = requested;
  } catch (_) {}
  function choose(value) {
    apply(value);
    try {
      const url = new URL(location.href);
      url.searchParams.set('lang', value);
      history.replaceState(history.state, '', url);
    } catch (_) {}
  }

  function apply(value) {
    language = value;
    try { localStorage.setItem(key, value); } catch (_) {}
    document.querySelectorAll('.lang-tab').forEach(tab => {
      tab.setAttribute('aria-selected', String(tab.dataset.lang === value));
      tab.tabIndex = tab.dataset.lang === value ? 0 : -1;
    });
    document.querySelectorAll('.lang-pane').forEach(pane => {
      pane.hidden = pane.dataset.lang !== value;
    });
  }

  const blocks = [...document.querySelectorAll('pre code')]
    .filter(code => !code.closest('.livedocs-shared-setup'));
  const matches = (code, lang) => code.classList.contains(`language-${lang}`);
  // FsLiveDocs renders an example's compiler setup as collapsed sibling <details>.
  // Keep that setup in the example's pane so it is hidden with its language.
  const isSetup = el => el?.matches('details.livedocs-shared-setup');
  function withSetup(example) {
    const members = [example];
    for (let el = example.previousElementSibling; isSetup(el); el = el.previousElementSibling) members.unshift(el);
    for (let el = example.nextElementSibling; isSetup(el); el = el.nextElementSibling) members.push(el);
    if (members.length === 1) return example;
    const pane = document.createElement('div');
    members[0].before(pane);
    pane.append(...members);
    return pane;
  }
  document.querySelectorAll('.cs-alt').forEach((marker, index) => {
    const fs = [...blocks].reverse().find(code => matches(code, marker.dataset.fs || 'fsharp') &&
      marker.compareDocumentPosition(code) & Node.DOCUMENT_POSITION_PRECEDING);
    const cs = blocks.find(code => matches(code, marker.dataset.cs || 'csharp') &&
      marker.compareDocumentPosition(code) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (!fs || !cs) return;
    const panes = [fs, cs].map(code => {
      const paired = code.closest('.lang-pane');
      if (paired) return paired;
      const managed = code.closest('.livedocs-code');
      if (managed) return withSetup(managed);
      // Prism replaces the class list on pre elements. Keep tab state on a wrapper.
      const pre = code.closest('pre');
      const pane = document.createElement('div');
      pre.before(pane);
      pane.appendChild(pre);
      return pane;
    });
    if (panes.some(pane => pane.classList.contains('lang-pane'))) return;
    const bar = document.createElement('div');
    bar.className = 'lang-tabbar';
    bar.setAttribute('role', 'tablist');
    bar.setAttribute('aria-label', 'Example language');
    ['fsharp', 'csharp'].forEach((lang, side) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'lang-tab';
      button.dataset.lang = lang;
      button.textContent = side === 0 ? 'F#' : 'C#';
      button.id = `example-${index}-${lang}-tab`;
      button.setAttribute('role', 'tab');
      button.setAttribute('aria-controls', `example-${index}-${lang}`);
      button.addEventListener('click', () => choose(lang));
      button.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const next = event.key === 'Home' ? 'fsharp' : event.key === 'End' ? 'csharp' :
          lang === 'fsharp' ? 'csharp' : 'fsharp';
        choose(next);
        bar.querySelector(`[data-lang="${next}"]`).focus();
      });
      bar.appendChild(button);
      const pane = panes[side];
      pane.classList.add('lang-pane');
      pane.dataset.lang = lang;
      pane.id = `example-${index}-${lang}`;
      pane.setAttribute('role', 'tabpanel');
      pane.setAttribute('aria-labelledby', button.id);
    });
    panes[0].before(bar);
    marker.remove();
  });
  apply(language);
  // highlight.js colours a C# type name only where it is declared. Find the other places a type
  // name stands, from the text around it: before a variable or member name, with type arguments,
  // after new/is/as/or/and/not/case, in a pattern, in a base list, or before a static member.
  // `kindAt` gives the highlight.js class at an offset, or '' for unhighlighted code.
  // Returns [start, end) offsets in order.
  function csharpTypeRanges(text, kindAt) {
    const word = /[A-Za-z0-9_]/;
    const plain = i => kindAt(i) === '';
    const generic = [];
    for (let i = 1; i < text.length; i++) {
      if (text[i] !== '<' || !word.test(text[i - 1]) || !plain(i)) continue;
      let depth = 0, j = i;
      for (; j < text.length; j++) {
        if (text[j] === '<') depth++;
        else if (text[j] === '>') { if (--depth === 0) break; }
        else if (!/[\w\s,.?\[\]()]/.test(text[j])) break;
      }
      if (depth === 0 && j < text.length) { generic.push([i, j]); }
    }
    const genericEnd = i => generic.find(range => range[0] === i)?.[1];
    const inGeneric = i => generic.some(range => range[0] < i && i < range[1]);
    const lineOf = i => text.slice(text.lastIndexOf('\n', i - 1) + 1, (text.indexOf('\n', i) + 1 || text.length + 1) - 1);
    // A C# 15 union names its type and then lists its case types.
    const unions = [...text.matchAll(/\bunion\s+\w+\s*(\([^)]*\))?/g)]
      .map(found => [found.index, found.index + found[0].length]);
    const inUnion = i => unions.some(range => range[0] < i && i < range[1]);
    const typeWords = ['new', 'is', 'as', 'or', 'and', 'not', 'case'];
    const ranges = [];
    const name = /(?<![\w@])[A-Z][A-Za-z0-9_]*/g;
    for (let match; (match = name.exec(text));) {
      const start = match.index, end = start + match[0].length;
      if (!plain(start)) continue;
      const line = lineOf(start);
      if (/^\s*(global\s+)?using\s+(static\s+)?[\w.]+\s*;/.test(line)) continue;
      let p = start - 1;
      while (p >= 0 && (/\s/.test(text[p]) || kindAt(p) === 'comment')) p--;
      const before = p >= 0 ? text[p] : '';
      if (before === '.') {
        // new Npgsql.NpgsqlConnection(...): the last name of the path is the type.
        const path = /\bnew\s+[\w.]+\.$/.test(text.slice(Math.max(0, start - 200), start));
        if (path && /[({]/.test(text[end] || '')) ranges.push([start, end]);
        continue;
      }
      let previous = '';
      if (word.test(before)) {
        let q = p;
        while (q >= 0 && word.test(text[q])) q--;
        previous = text.slice(q + 1, p + 1);
        // A name that follows a type is the member or variable being declared.
        const kind = kindAt(p);
        if (kind === '' || kind === 'built_in' || kind === 'title') continue;
      }
      const afterArrow = before === '>' && text[p - 1] === '=';
      if ((before === '>' && !afterArrow) || before === ']') continue;
      if (before === '?' && /[\w>\]]/.test(text[p - 1] || '')) continue;
      let n = end;
      const close = text[n] === '<' ? genericEnd(n) : undefined;
      if (close !== undefined) n = close + 1;
      if (text[n] === '?') n++;
      while (text[n] === '[' && text[n + 1] === ']') n += 2;
      const attached = text[n] || '';
      let m = n;
      while (m < text.length && /\s/.test(text[m])) m++;
      const next = text[m] || '';
      const isNew = previous === 'new';
      let type = false;
      const lineStart = text.slice(text.lastIndexOf('\n', start - 1) + 1, start);
      if (inGeneric(start)) type = true;
      else if (inUnion(start)) type = true;
      else if (isNew && next === '.') type = false;
      else if (next === '(' && /^\(\s*var\b/.test(text.slice(m))) type = true;
      else if (/^(or|and)\b/.test(text.slice(m)) && kindAt(m) === 'keyword') type = true;
      else if (next === '(') type = isNew;
      else if (close !== undefined) type = true;
      else if (attached === ':' && previous !== 'case') type = false;
      else if (typeWords.includes(previous) || /\b(typeof|default|nameof)\($/.test(text.slice(0, start))) type = true;
      else if (m > n && /[A-Za-z_@]/.test(next) && ['', 'title'].includes(kindAt(m))) type = true;
      else if (next === '.' && /[A-Za-z]/.test(text[m + 1] || '')) type = true;
      else if (next === '{') type = true;
      else if (next === '=' && text[m + 1] === '>') type = true;
      else if (before === '(' && (next === ',' || next === ')') && !/[\w>]/.test(text[p - 1] || '')) type = true;
      else if (/\b(class|record|struct|interface)\b.*\s:\s/.test(lineStart)) type = true;
      if (type) ranges.push([start, end]);
    }
    return ranges;
  }
  function colourCsharpTypes(code) {
    // highlight.js gives a declared method the class it gives a declared type. Mark the methods,
    // so that they keep the function colour and differ from the type names around them.
    code.querySelectorAll('.hljs-function > .hljs-title').forEach(title => {
      const gap = title.previousSibling;
      const keyword = gap?.nodeType === Node.TEXT_NODE && !gap.data.trim() ? gap.previousSibling : null;
      const declaresType = keyword?.nodeType === Node.ELEMENT_NODE &&
        ['record', 'class', 'struct', 'interface', 'union', 'enum'].includes(keyword.textContent);
      if (!declaresType) title.classList.add('function_');
    });
    const nodes = [];
    const walker = document.createTreeWalker(code, NodeFilter.SHOW_TEXT);
    let offset = 0;
    for (let node; (node = walker.nextNode());) {
      // highlight.js wraps a declaration and its parameters in spans that leave their code plain.
      const scope = [...node.parentElement.classList].find(name => name.startsWith('hljs-')) || '';
      const kind = ['hljs-function', 'hljs-params', 'hljs-subst'].includes(scope) ? '' : scope.slice(5);
      nodes.push({ node, start: offset, end: offset + node.data.length, kind: node.parentElement === code ? '' : kind });
      offset += node.data.length;
    }
    const at = i => nodes.find(entry => entry.start <= i && i < entry.end);
    const ranges = csharpTypeRanges(code.textContent, i => at(i)?.kind ?? '');
    // Wrap from the end so earlier offsets in the same text node stay valid.
    for (const [start, end] of ranges.reverse()) {
      const entry = at(start);
      if (!entry || end > entry.end) continue;
      const range = document.createRange();
      range.setStart(entry.node, start - entry.start);
      range.setEnd(entry.node, end - entry.start);
      const span = document.createElement('span');
      span.className = 'hljs-type';
      range.surroundContents(span);
    }
  }
  // FsLiveDocs ships a Prism F# grammar. Use the existing vendored highlighter
  // for C# and SQL after its DOMContentLoaded formatting has finished.
  window.addEventListener('DOMContentLoaded', () => {
    if (!window.hljs) return;
    // highlight.js 11.9 predates C# 15. Mark its new type modifiers as keywords before the
    // grammar compiles on first use.
    const csharp = window.hljs.getLanguage('csharp');
    if (Array.isArray(csharp?.keywords?.keyword)) csharp.keywords.keyword.push('closed', 'union');
    document.querySelectorAll('code.language-csharp, code.language-sql').forEach(code => {
      code.querySelectorAll('br').forEach(br => br.replaceWith('\n'));
      code.textContent = code.textContent;
      window.hljs.highlightElement(code);
      if (code.classList.contains('language-csharp')) colourCsharpTypes(code);
    });
  });
})();
