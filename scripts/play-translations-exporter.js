// Exports Play Console's machine translations for scripts/import_play_translations.py (RemEx-fwhqw).
// Open Play Console > Translations > App strings (after a bundle is in a draft release), set
// "Show rows" to 500, then paste this into the DevTools console. It scrolls every page of the
// virtualized grid, then downloads play-translations.json. Plurals are not in Play's grid.
(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const rows = {};
  const headers = [...document.querySelectorAll('[role=columnheader], th')].map((h) => h.innerText.replace(/\s+/g, ' ').trim());
  const scroller = () => {
    let el = document.querySelector('[role=row]');
    while (el && !(el.scrollHeight > el.clientHeight + 20 && /(auto|scroll)/.test(getComputedStyle(el).overflowY))) el = el.parentElement;
    return el || document.scrollingElement;
  };
  const grab = () => {
    for (const r of document.querySelectorAll('[role=row], tr')) {
      const c = [...r.querySelectorAll('[role=cell],[role=gridcell],td')];
      if (c.length < 3) continue;
      const k = c[0].innerText.trim();
      if (k) rows[k] = c.map((x) => x.innerText);
    }
  };
  // The paginator buttons are <material-button>, not <button>.
  const nextBtn = () => document.querySelector('material-button[aria-label="Go to the next page"]');
  for (let page = 0; page < 20; page++) {
    const s = scroller();
    s.scrollTop = 0;
    await sleep(800);
    let last = -1;
    while (true) {
      grab();
      if (s.scrollTop === last) break;
      last = s.scrollTop;
      s.scrollTop += Math.max(200, s.clientHeight * 0.7);
      await sleep(350);
    }
    grab();
    console.log(`page ${page + 1}: ${Object.keys(rows).length} rows`);
    const nx = nextBtn();
    if (!nx || nx.getAttribute('aria-disabled') === 'true') break;
    nx.click();
    await sleep(4000);
  }
  const codes = headers.slice(2).map((h) => (h.split('–').pop() || '').trim());
  const locales = Object.fromEntries(codes.map((c) => [c, {}]));
  for (const [k, cells] of Object.entries(rows)) {
    codes.forEach((c, i) => {
      const v = cells[i + 2];
      if (v !== undefined && v.trim() !== '') locales[c][k] = v.replace(/\n$/, '');
    });
  }
  const out = { source: 'play-console-app-translations', exportedAt: new Date().toISOString(), locales };
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([JSON.stringify(out)], { type: 'application/json' }));
  a.download = 'play-translations.json';
  document.body.appendChild(a);
  a.click();
  a.remove();
  console.log(`exported ${Object.keys(rows).length} keys x ${codes.length} languages`);
})();
