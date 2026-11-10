/* XenoDisk core: native bridge, formatting, modal/toast/ctxmenu, jobs. */
'use strict';
const Host = (() => {
  const wv = window.chrome && window.chrome.webview;
  let seq = 0; const pending = new Map(); const handlers = {};
  if (wv) wv.addEventListener('message', e => {
    const m = e.data;
    if (m && m.id) { const p = pending.get(m.id); if (!p) return; pending.delete(m.id); m.error ? p.rej(new Error(m.error)) : p.res(m.result); }
    else if (m && m.evt) (handlers[m.evt] || []).forEach(f => { try { f(m.data); } catch (err) { console.error(err); } });
  });
  return {
    available: !!wv,
    call(method, args) {
      if (!wv) return Promise.reject(new Error('Native host not available'));
      const id = ++seq;
      return new Promise((res, rej) => { pending.set(id, { res, rej }); wv.postMessage({ id, method, args: args || {} }); });
    },
    on(evt, fn) { (handlers[evt] = handlers[evt] || []).push(fn); },
  };
})();
function logToHost(level, text) { if (Host.available) Host.call('log', { level, text: String(text).slice(0, 4000) }).catch(() => { }); }
window.addEventListener('error', e => logToHost('error', `${e.message} @ ${e.filename}:${e.lineno}\n${e.error && e.error.stack || ''}`));
window.addEventListener('unhandledrejection', e => logToHost('error', 'Unhandled: ' + (e.reason && (e.reason.stack || e.reason.message) || e.reason)));

const $ = (s, r) => (r || document).querySelector(s);
const $$ = (s, r) => Array.from((r || document).querySelectorAll(s));
const esc = s => s == null ? '' : String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const h = html => { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstChild; };
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };

const F = {
  bytes(b, d) {
    if (b == null || isNaN(b)) return '—';
    const dec = (typeof S !== 'undefined' && S.sizeUnit === 'decimal');
    const base = dec ? 1000 : 1024;
    const u = dec ? ['B', 'KB', 'MB', 'GB', 'TB', 'PB'] : ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB']; let i = 0; b = +b;
    while (Math.abs(b) >= base && i < u.length - 1) { b /= base; i++; }
    return (i === 0 ? b.toFixed(0) : b.toFixed(d != null ? d : (b < 10 ? 2 : b < 100 ? 1 : 0))) + ' ' + u[i];
  },
  gb(b) { if (b == null) return '—'; const base = (typeof S !== 'undefined' && S.sizeUnit === 'decimal') ? 1e9 : 1073741824; return (b / base).toFixed(b < base * 10 ? 1 : 0) + (base === 1e9 ? ' GB' : ' GiB'); },
  temp(c) { if (c == null) return '—'; return (typeof S !== 'undefined' && S.tempUnit === 'F') ? Math.round(c * 9 / 5 + 32) + '°F' : c + '°C'; },
  pct(v, d = 0) { return v == null ? '—' : (+v).toFixed(d) + '%'; },
  num(v) { return v == null ? '—' : (+v).toLocaleString('en-US'); },
  date(s) { if (!s) return '—'; const d = new Date(s); return isNaN(d) ? s : d.toLocaleString(); },
  dur(h) { if (h == null) return '—'; const d = Math.floor(h / 24); return d > 0 ? `${d}d ${h % 24}h` : `${h}h`; },
};

/* ---------- settings ---------- */
const S = {
  theme: 'dark',
  threads: 0,                 // 0 = auto (all logical cores)
  sizeUnit: 'binary',         // binary (1024) | decimal (1000)
  tempUnit: 'C',              // C | F
  followReparse: false,       // follow junctions/symlinks while scanning
  includeHidden: true,
  includeSystem: false,
  skipDirs: ['$Recycle.Bin', 'System Volume Information', 'Windows\\WinSxS', 'Windows\\Temp'],
  dupeHash: 'sha256',         // sha256 | md5 | xxhash | quick
  confirmDestructive: true,   // require typed confirmation on destructive ops
  imageVerify: true,
  benchSizeMB: 1024,
  ioBufferMB: 4,              // imaging block size
  findLimit: 5000,
  autoRefresh: true,          // refresh disk list on USB plug/unplug
};
async function loadS() { try { Object.assign(S, JSON.parse(await Host.call('loadSettings'))); } catch { } if (!Array.isArray(S.skipDirs)) S.skipDirs = []; }
const saveS = debounce(() => Host.call('saveSettings', { json: JSON.stringify(S, null, 2) }).catch(() => { }), 300);
function scanOpts() { return { threads: S.threads | 0, followReparse: !!S.followReparse, includeHidden: !!S.includeHidden, includeSystem: !!S.includeSystem, skipDirs: S.skipDirs || [] }; }
function applyTheme() { document.body.classList.toggle('light', S.theme === 'light'); document.body.classList.toggle('dark', S.theme !== 'light'); Host.call('setTheme', { dark: S.theme !== 'light' }).catch(() => { }); }

/* ---------- toasts / modal ---------- */
function toast(msg, kind, ms) {
  const t = h(`<div class="toast ${kind || ''}">${esc(msg)}</div>`);
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), ms || (kind === 'error' ? 7000 : 3500));
}
function modal({ title, bodyHtml, buttons, onOpen }) {
  return new Promise(res => {
    const m = $('#modal'); $('#m-title').textContent = title || '';
    $('#m-body').innerHTML = bodyHtml || '';
    const bb = $('#m-buttons'); bb.innerHTML = '';
    const done = v => { m.classList.add('hidden'); document.removeEventListener('keydown', key, true); res(v); };
    (buttons || [{ label: 'OK', value: true }]).forEach((b, i) => {
      const el = h(`<button class="btn ${b.cls || (i === 0 && (buttons || []).length > 1 ? 'sec' : '')}">${esc(b.label)}</button>`);
      el.onclick = () => { if (b.collect) { const v = b.collect(); if (v === false) return; done(v); } else done(b.value); };
      bb.appendChild(el);
    });
    const key = e => { if (e.key === 'Escape') { e.preventDefault(); done(null); } };
    document.addEventListener('keydown', key, true);
    m.classList.remove('hidden');
    if (onOpen) setTimeout(() => onOpen($('#m-body')), 10);
  });
}
const confirmDanger = (title, bodyHtml, okLabel) => (typeof S !== 'undefined' && S.confirmDestructive === false) ? Promise.resolve(true) : modal({ title, bodyHtml, buttons: [{ label: 'Cancel', value: false }, { label: okLabel || 'Confirm', value: true, cls: 'danger' }] });

/* ---------- context menu ---------- */
function ctxMenu(x, y, items) {
  closeMenus();
  const cm = $('#ctxmenu'); cm.innerHTML = '';
  items.forEach(it => {
    if (it === '-') { cm.appendChild(h('<div class="msep"></div>')); return; }
    const mi = h(`<div class="mi ${it.danger ? 'danger' : ''} ${it.disabled ? 'disabled' : ''}"><span>${esc(it.label)}</span><span class="mk">${esc(it.key || '')}</span></div>`);
    mi.onclick = () => { closeMenus(); it.click && it.click(); };
    cm.appendChild(mi);
  });
  cm.classList.remove('hidden');
  cm.style.left = '0px'; cm.style.top = '0px';
  const r = cm.getBoundingClientRect();
  cm.style.left = Math.min(x, innerWidth - r.width - 6) + 'px';
  cm.style.top = Math.min(y, innerHeight - r.height - 6) + 'px';
}
function closeMenus() { $('#ctxmenu').classList.add('hidden'); $$('.menu').forEach(m => m.remove()); $$('.mtop.open').forEach(m => m.classList.remove('open')); }
document.addEventListener('mousedown', e => { if (!e.target.closest('#ctxmenu, .menu, .mtop')) closeMenus(); });

/* ---------- jobs ---------- */
const Jobs = (() => {
  const box = () => $('#jobs'); const els = new Map(); const onDone = new Map(); const earlyDone = new Map();
  Host.on('job', j => render(j));
  Host.on('jobdone', j => {
    render(j, true);
    const cb = onDone.get(j.id);
    if (cb) { onDone.delete(j.id); cb(j); }
    else {
      // A fast worker may finish before the bridge reply supplies its job ID.
      earlyDone.set(j.id, j);
      if (earlyDone.size > 50) earlyDone.delete(earlyDone.keys().next().value);
    }
    const el = els.get(j.id);
    if (el) setTimeout(() => { el.remove(); els.delete(j.id); updateCount(); }, j.state === 'done' ? 3500 : 8000);
    if (j.state === 'error') toast((j.title || 'Job') + ' failed: ' + j.error, 'error');
  });
  function render(j, fin) {
    let el = els.get(j.id);
    if (!el) { el = h(`<div class="job"><div class="jt"><b class="jtitle"></b><span class="grow"></span><span class="pct mono"></span><span class="x" title="Cancel">&#xE711;</span></div><div class="bar"><i></i></div><div class="jm"></div></div>`); el.querySelector('.x').onclick = () => Host.call('jobCancel', { id: j.id }); box().appendChild(el); els.set(j.id, el); }
    el.className = 'job' + (fin ? ' ' + j.state : '');
    el.querySelector('.jtitle').textContent = j.title || j.kind;
    const pct = j.pct < 0 ? null : j.pct;
    el.querySelector('.pct').textContent = fin ? (j.state === 'done' ? 'done' : j.state) : (pct == null ? '' : Math.round(pct) + '%');
    el.querySelector('.bar > i').style.width = (fin && j.state === 'done' ? 100 : (pct == null ? 100 : pct)) + '%';
    el.querySelector('.jm').textContent = fin ? (j.error || '') : (j.msg || '');
    el.querySelector('.x').style.display = fin ? 'none' : '';
    updateCount();
  }
  function updateCount() { const n = els.size; $('#sb-jobs').textContent = n ? `${n} job${n > 1 ? 's' : ''} running` : ''; }
  return {
    run(method, args, onComplete) {
      return Host.call(method, args).then(r => {
        if (r && r.job != null) {
          const completed = earlyDone.get(r.job);
          earlyDone.delete(r.job);
          if (onComplete) { if (completed) onComplete(completed); else onDone.set(r.job, onComplete); }
        }
        return r;
      });
    },
  };
})();
