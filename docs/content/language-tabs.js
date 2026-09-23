// Pair the F# and C# examples marked with .cs-alt, including shell command pairs.
// Both examples remain readable if JavaScript or localStorage is unavailable.
(() => {
  const key = 'fcqrs-lang';
  let language = 'fsharp';
  try { language = localStorage.getItem(key) === 'csharp' ? 'csharp' : 'fsharp'; } catch (_) {}

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
      button.addEventListener('click', () => apply(lang));
      button.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        const next = event.key === 'Home' ? 'fsharp' : event.key === 'End' ? 'csharp' :
          lang === 'fsharp' ? 'csharp' : 'fsharp';
        apply(next);
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
  // FsLiveDocs ships a Prism F# grammar. Use the existing vendored highlighter
  // for C# after its DOMContentLoaded formatting has finished.
  window.addEventListener('DOMContentLoaded', () => {
    if (!window.hljs) return;
    document.querySelectorAll('code.language-csharp').forEach(code => {
      code.querySelectorAll('br').forEach(br => br.replaceWith('\n'));
      code.textContent = code.textContent;
      window.hljs.highlightElement(code);
    });
  });
})();
