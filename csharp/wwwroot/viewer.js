/* Shared file viewer used by the in-page preview overlay and the standalone /preview page.
   Builds elements with the DOM API only (never innerHTML) so file names can't inject markup. */
(function () {
  const css = `
.dsm-viewer { width: 100%; height: 100%; display: flex; align-items: center; justify-content: center; min-height: 0; }
.dsm-viewer img { max-width: 100%; max-height: 100%; object-fit: contain; cursor: zoom-in; }
.dsm-viewer img.actual { max-width: none; max-height: none; object-fit: none; cursor: zoom-out; }
.dsm-viewer.scroll { overflow: auto; display: block; }
.dsm-viewer.scroll img.actual { display: block; margin: auto; }
.dsm-viewer video { max-width: 100%; max-height: 100%; background: #000; }
.dsm-viewer audio { width: min(560px, 92%); }
.dsm-viewer iframe { width: 100%; height: 100%; border: 0; background: #fff; }
.dsm-viewer .dsm-msg { max-width: 460px; text-align: center; padding: 24px; opacity: .85; }
`;
  function ensureStyle() {
    if (document.getElementById('dsm-viewer-style')) return;
    const s = document.createElement('style');
    s.id = 'dsm-viewer-style';
    s.textContent = css;
    document.head.append(s);
  }
  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function fileUrl(scan, path) {
    return '/api/file?' + (scan ? 'scan=' + encodeURIComponent(scan) + '&' : '') + 'path=' + encodeURIComponent(path);
  }

  /* opts: { scan, path, kind: 'image'|'video'|'audio'|'pdf' } -> element. Call dispose(el) when it is removed. */
  function create(opts) {
    ensureStyle();
    const box = el('div', 'dsm-viewer');
    const url = fileUrl(opts.scan, opts.path);
    const fail = (msg) => box.replaceChildren(el('div', 'dsm-msg', msg || "This browser can't show this file. Use Open to view it in its default app."));
    // A failed <img>/<video> doesn't say why. Ask the server again so the message can name the real cause.
    const explain = async () => {
      try {
        const r = await fetch(url, { headers: { Range: 'bytes=0-0' } });
        if (!r.ok) {
          const j = await r.json().catch(() => ({}));
          return fail("Can't preview this file: " + (j.error || 'the server refused it (HTTP ' + r.status + ').'));
        }
        fail(opts.kind === 'image'
          ? "The browser couldn't decode this image. It may be damaged or in a format Chrome can't show. Use Open to view it in its default app."
          : undefined);
      } catch (e) { fail("Can't preview this file: couldn't reach the server."); }
    };
    let media;
    if (opts.kind === 'image') {
      media = el('img');
      const wait = el('div', 'dsm-msg', 'Loading\u2026 (HEIC photos are converted the first time you open them)');
      if (/\.hei[cf]$/i.test(opts.path)) { box.append(wait); media.style.display = 'none'; media.onload = () => { wait.remove(); media.style.display = ''; }; }
      media.alt = opts.path.split(/[\\/]/).pop();
      media.onerror = explain;
      media.onclick = () => {
        const actual = media.classList.toggle('actual');
        box.classList.toggle('scroll', actual);
      };
    } else if (opts.kind === 'video' || opts.kind === 'audio') {
      media = el(opts.kind);
      media.controls = true;
      media.autoplay = true;
      media.preload = 'metadata';
      media.onerror = explain;
    } else if (opts.kind === 'pdf') {
      media = el('iframe');
      media.title = opts.path.split(/[\\/]/).pop();
    } else { fail(); return box; }
    media.src = url;
    box.append(media);
    return box;
  }
  // Stops playback and releases the file so it can be deleted or moved while the page stays open.
  function dispose(box) {
    for (const m of box.querySelectorAll('video, audio')) { m.pause(); m.removeAttribute('src'); m.load(); }
    for (const m of box.querySelectorAll('img, iframe')) m.removeAttribute('src');
  }
  window.DsmViewer = { create, dispose, fileUrl };
})();
