/* XenoMon core: native bridge, formatting, ring buffers, menus, modal, toasts, settings. */
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
(() => { const ce = console.error.bind(console); console.error = (...a) => { ce(...a); logToHost('console', a.map(x => x && x.stack || x).join(' ')); }; })();
window.addEventListener('unhandledrejection', e => logToHost('error', 'Unhandled: ' + (e.reason && (e.reason.stack || e.reason.message) || e.reason)));

const $ = (s, r) => (r || document).querySelector(s);
const $$ = (s, r) => Array.from((r || document).querySelectorAll(s));
const esc = s => s == null ? '' : String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const h = (html) => { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstChild; };
const css = n => getComputedStyle(document.body).getPropertyValue(n).trim();
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
/* write innerHTML only when it actually changed — skips re-parse + re-layout for static rows every tick */
const setHTML = (el, html) => { if (el && el._h !== html) { el._h = html; el.innerHTML = html; } };

/* ---------- formatting ---------- */
const F = {
  bytes(b, d) {
    if (b == null || isNaN(b)) return '—';
    const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']; let i = 0; b = +b;
    while (Math.abs(b) >= 1024 && i < u.length - 1) { b /= 1024; i++; }
    return (i === 0 ? b.toFixed(0) : b.toFixed(d != null ? d : (b < 10 ? 2 : b < 100 ? 1 : 0))) + ' ' + u[i];
  },
  rate(b) { return b == null ? '—' : F.bytes(b) + '/s'; },
  bits(B) {
    if (B == null) return '—'; let b = B * 8; const u = ['b/s', 'Kb/s', 'Mb/s', 'Gb/s']; let i = 0;
    while (b >= 1000 && i < u.length - 1) { b /= 1000; i++; }
    return (i === 0 ? b.toFixed(0) : b.toFixed(b < 10 ? 2 : b < 100 ? 1 : 0)) + ' ' + u[i];
  },
  pct(v, d = 1) { return v == null ? '—' : (+v).toFixed(d) + '%'; },
  num(v) { return v == null ? '—' : (+v).toLocaleString('en-US'); },
  dur(ms) {
    let s = Math.floor(ms / 1000); const d = Math.floor(s / 86400); s %= 86400;
    const hh = Math.floor(s / 3600), mm = Math.floor(s % 3600 / 60), ss = s % 60;
    return (d ? d + 'd ' : '') + String(hh).padStart(2, '0') + ':' + String(mm).padStart(2, '0') + ':' + String(ss).padStart(2, '0');
  },
  temp(c, d = 1) {
    if (c == null) return '—';
    return S.tempUnit === 'F' ? (c * 9 / 5 + 32).toFixed(d) + '°F' : (+c).toFixed(d) + '°C';
  },
  mhz(v) { return v == null ? '—' : v >= 1000 ? (v / 1000).toFixed(2) + ' GHz' : v.toFixed(0) + ' MHz'; },
  // LHM sensor type → display
  sensor(t, v) {
    if (v == null) return '—';
    switch (t) {
      case 'Temperature': return F.temp(v);
      case 'Load': case 'Control': case 'Level': case 'Humidity': return v.toFixed(1) + ' %';
      case 'Voltage': return v.toFixed(3) + ' V';
      case 'Current': return v.toFixed(3) + ' A';
      case 'Power': return v.toFixed(1) + ' W';
      case 'Clock': return v.toFixed(0) + ' MHz';
      case 'Frequency': return v.toFixed(0) + ' Hz';
      case 'Fan': return v.toFixed(0) + ' RPM';
      case 'Flow': return v.toFixed(1) + ' L/h';
      case 'Factor': return v.toFixed(3);
      case 'Data': return v.toFixed(v < 100 ? 2 : 1) + ' GB';
      case 'SmallData': return v >= 1024 ? (v / 1024).toFixed(2) + ' GB' : v.toFixed(0) + ' MB';
      case 'Throughput': return F.rate(v);
      case 'TimeSpan': return F.dur(v * 1000);
      case 'Energy': return v.toFixed(0) + ' mWh';
      case 'Noise': return v.toFixed(1) + ' dBA';
      case 'Conductivity': return v.toFixed(1) + ' µS/cm';
      default: return v.toFixed(2);
    }
  },
};

/* fixed-size ring buffer of numbers (null = gap) */
class Ring {
  constructor(n) { this.n = n; this.a = new Array(n).fill(null); this.i = 0; this.count = 0; }
  push(v) { this.a[this.i] = v; this.i = (this.i + 1) % this.n; if (this.count < this.n) this.count++; }
  last() { return this.a[(this.i - 1 + this.n) % this.n]; }
  get(k) { return this.a[(this.i - this.n + k + this.n * 2) % this.n]; }     // 0 = oldest
  max() { let m = 0; for (const v of this.a) if (v != null && v > m) m = v; return m; }
  resize(n) { const vals = []; for (let k = 0; k < this.n; k++) vals.push(this.get(k)); const r = new Ring(n); vals.slice(-n).forEach(v => r.push(v)); Object.assign(this, r); }
}

/* ---------- settings ---------- */
const S = {
  interval: 1000, history: 120, theme: 'dark', tempUnit: 'C', topmost: false, startView: 'overview',
  pinned: [], hiddenSensorTypes: [], tree: false, procSort: { key: 'cpu', asc: false }, detailH: 240,
  alerts: { on: true, cpuTemp: 90, gpuTemp: 85, ram: 92, cpuLoad: 0 },
};
async function loadSettings() {
  try { const j = JSON.parse(await Host.call('loadSettings')); Object.assign(S, j, { alerts: Object.assign({}, S.alerts, j.alerts || {}) }); } catch { }
}
const saveSettings = debounce(() => Host.call('saveSettings', { json: JSON.stringify(S, null, 2) }).catch(() => { }), 400);

/* ---------- toasts / modal ---------- */
function toast(msg, kind, ms) {
  const t = h(`<div class="toast ${kind || ''}">${esc(msg)}</div>`);
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), ms || (kind === 'error' ? 6000 : 3200));
}
function modal({ title, body, html, input, value, buttons }) {
  return new Promise(res => {
    const m = $('#modal'); $('#m-title').textContent = title || '';
    if (html) $('#m-body').innerHTML = html; else $('#m-body').textContent = body || '';
    const inp = $('#m-input'); inp.classList.toggle('hidden', !input); inp.value = value || ''; inp.placeholder = input || '';
    const bb = $('#m-buttons'); bb.innerHTML = '';
    const done = v => { m.classList.add('hidden'); document.removeEventListener('keydown', key, true); res(v); };
    (buttons || [{ label: 'OK', value: true }]).forEach((b, i) => {
      const el = h(`<button class="btn ${b.cls || (i ? '' : 'sec')}">${esc(b.label)}</button>`);
      el.onclick = () => done(b.value === undefined ? b.label : (input && b.value === true ? inp.value : b.value)); bb.appendChild(el);
    });
    const key = e => {
      if (e.key === 'Escape') { e.preventDefault(); done(null); }
      if (e.key === 'Enter' && input) { e.preventDefault(); done(inp.value); }
    };
    document.addEventListener('keydown', key, true);
    m.classList.remove('hidden'); if (input) setTimeout(() => { inp.focus(); inp.select(); }, 10);
  });
}
const confirmBox = (title, body, okLabel, cls) => modal({ title, body, buttons: [{ label: 'Cancel', value: false }, { label: okLabel || 'OK', value: true, cls: cls || '' }] });

/* ---------- context menu: items = [{label, key, click, check, danger, disabled, sub:[...]}, '-'] ---------- */
function ctxMenu(x, y, items) {
  closeMenus();
  const build = (items, el) => {
    el.innerHTML = '';
    items.forEach(it => {
      if (it === '-') { el.appendChild(h('<div class="msep"></div>')); return; }
      const mi = h(`<div class="mi ${it.danger ? 'danger' : ''} ${it.disabled ? 'disabled' : ''}"><span><span class="chk">${it.check ? '✓' : ''}</span>${esc(it.label)}</span><span class="mk">${it.sub ? '›' : esc(it.key || '')}</span></div>`);
      if (it.sub) {
        mi.onmouseenter = () => {
          $$('.submenu').forEach(s => s.remove());
          const sm = h('<div class="submenu"></div>'); build(it.sub, sm); document.body.appendChild(sm);
          const r = mi.getBoundingClientRect(); place(sm, r.right + 2, r.top - 4);
        };
      } else {
        mi.onmouseenter = () => { if (el.id === 'ctxmenu') $$('.submenu').forEach(s => s.remove()); };
        mi.onclick = () => { closeMenus(); it.click && it.click(); };
      }
      el.appendChild(mi);
    });
  };
  const cm = $('#ctxmenu'); build(items, cm); cm.classList.remove('hidden'); place(cm, x, y);
}
function place(el, x, y) {
  el.style.left = '0px'; el.style.top = '0px';
  const r = el.getBoundingClientRect();
  el.style.left = Math.max(2, Math.min(x, innerWidth - r.width - 4)) + 'px';
  el.style.top = Math.max(2, Math.min(y, innerHeight - r.height - 4)) + 'px';
}
function closeMenus() { $('#ctxmenu').classList.add('hidden'); $$('.submenu').forEach(s => s.remove()); $$('.mtop.open').forEach(m => m.classList.remove('open')); }
document.addEventListener('mousedown', e => { if (!e.target.closest('#ctxmenu, .submenu, .mtop')) closeMenus(); });
window.addEventListener('blur', closeMenus);

/* process icon cache (path → data uri) */
const BLANK = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';
const Icons = (() => {
  const cache = new Map(); const want = new Set(); let busy = false;
  const flush = async () => {
    if (busy || !want.size) return; busy = true;
    const paths = [...want].slice(0, 80); paths.forEach(p => want.delete(p));
    try { const r = await Host.call('icons', { paths }); for (const p of paths) cache.set(p, r[p] || ''); document.dispatchEvent(new Event('icons')); } catch { }
    busy = false; if (want.size) setTimeout(flush, 50);
  };
  return {
    get(path) { if (!path) return ''; if (cache.has(path)) return cache.get(path); want.add(path); setTimeout(flush, 30); cache.set(path, ''); return ''; },
    img(path) { const u = Icons.get(path); return `<img class="picon${u ? '' : ' noico'}" src="${u || BLANK}">`; },
  };
})();
