/* XenoSuite Audit tab — drives the VulnAudit engine and renders its report. */
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
    call(method, args) { if (!wv) return Promise.reject(new Error('no host')); const id = ++seq; return new Promise((res, rej) => { pending.set(id, { res, rej }); wv.postMessage({ id, method, args: args || {} }); }); },
    on(e, f) { (handlers[e] = handlers[e] || []).push(f); },
  };
})();
const $ = s => document.querySelector(s);
const esc = s => s == null ? '' : String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
function toast(msg, kind) { const t = document.createElement('div'); t.className = 'toast ' + (kind || ''); t.textContent = msg; $('#toasts').appendChild(t); setTimeout(() => t.remove(), kind === 'error' ? 7000 : 3500); }

const SEV = ['high', 'medium', 'low', 'info'];
let STATUS = {};

async function init() {
  try {
    STATUS = await Host.call('auditStatus');
    $('#mb-admin').innerHTML = '';
    render();
    if (STATUS.hasReport) loadReport();
  } catch (e) { $('#a-body').innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
  $('#a-scan').onclick = scan;
  $('#a-gui').onclick = () => Host.call('auditRunGui').catch(e => toast(e.message, 'error'));
  $('#a-html').onclick = () => Host.call('auditOpenHtml').catch(e => toast(e.message, 'error'));
  $('#a-path').onclick = setPath;
}

function render() {
  if (!STATUS.found) {
    $('#a-body').innerHTML = `<div class="empty">VulnAudit engine not found.<br><br>Click <b>Set VulnAudit.exe…</b> and point to <span class="mono">Project 4\\dist\\VulnAudit.exe</span>.</div>`;
    $('#a-scan').disabled = true; $('#a-gui').disabled = true; $('#a-html').disabled = true;
    return;
  }
  $('#a-when').textContent = STATUS.reportWhen ? 'Last scan: ' + STATUS.reportWhen : 'No scan yet';
}

async function setPath() {
  const p = await Host.call('pickFile', { title: 'Locate VulnAudit.exe', filter: 'VulnAudit|VulnAudit.exe|Executables|*.exe' });
  if (!p) return;
  const r = await Host.call('auditSetPath', { path: p });
  if (r.ok) { toast('Path set', 'ok'); STATUS = await Host.call('auditStatus'); render(); } else toast('Not a valid file', 'warn');
}

async function scan() {
  $('#a-body').innerHTML = `<div class="empty">Running full security + health audit… this can take a minute.</div>`;
  $('#a-scan').disabled = true;
  try { const r = await Host.call('auditScan'); STATUS = await Host.call('auditStatus'); render(); draw(r.report, r.when); toast('Scan complete', 'ok'); }
  catch (e) { toast(e.message, 'error'); $('#a-body').innerHTML = `<div class="empty">${esc(e.message)}</div>`; }
  finally { $('#a-scan').disabled = false; }
}

async function loadReport() {
  try { const r = await Host.call('auditReport'); if (r.report) draw(r.report, r.when); } catch (e) { }
}

function gradeColor(g) { g = (g || '').toUpperCase(); return g.startsWith('A') ? '#2f9e62' : g.startsWith('B') ? '#4f9cf9' : g.startsWith('C') ? '#cca700' : g.startsWith('D') ? '#e0772f' : '#b3261e'; }

function draw(rep, when) {
  if (!rep) { $('#a-body').innerHTML = '<div class="empty">No report. Run a scan.</div>'; return; }
  const findings = (rep.findings || []).filter(f => !f.ignored);
  const counts = { high: 0, medium: 0, low: 0, info: 0 };
  findings.forEach(f => { counts[f.severity] = (counts[f.severity] || 0) + 1; });
  const body = $('#a-body');
  body.innerHTML = '';
  const head = document.createElement('div'); head.id = 'audit-head';
  head.innerHTML = `<div class="grade" style="background:${gradeColor(rep.grade)}">${esc(rep.grade || '—')}</div>
    <div><div style="font-size:13px;color:var(--fg2)">Security score</div><div style="font:700 26px var(--mono)">${rep.score != null ? esc(rep.score) : '—'}<span style="font-size:14px;color:var(--fg3)"> / 100</span></div></div>
    <div class="sevrow">${SEV.map(s => `<span class="sevpill sv-${s}">${counts[s] || 0} ${s}</span>`).join('')}</div>
    <span class="grow"></span>
    <div class="muted mono">${esc(rep.os || '')}${when ? ' · ' + esc(when) : ''}</div>`;
  body.appendChild(head);

  // group by category, severity order within
  const cats = {};
  findings.forEach(f => (cats[f.category || 'Other'] = cats[f.category || 'Other'] || []).push(f));
  const catNames = Object.keys(cats).sort((a, b) => worst(cats[b]) - worst(cats[a]));
  catNames.forEach(cat => {
    const split = document.createElement('div'); split.className = 'catsplit'; split.textContent = cat + ' (' + cats[cat].length + ')'; body.appendChild(split);
    cats[cat].sort((a, b) => SEV.indexOf(a.severity) - SEV.indexOf(b.severity)).forEach(f => body.appendChild(card(f)));
  });
  if (!findings.length) body.appendChild(h('<div class="empty">No findings — clean.</div>'));
}
function worst(arr) { return Math.max(...arr.map(f => 3 - SEV.indexOf(f.severity))); }
function h(html) { const t = document.createElement('template'); t.innerHTML = html.trim(); return t.content.firstChild; }

function card(f) {
  const el = document.createElement('div'); el.className = 'finding ' + f.severity;
  el.innerHTML = `<div class="ft"><span class="sevpill sv-${f.severity}" style="padding:1px 8px;font-size:11px">${esc(f.severity)}</span><span class="ftitle">${esc(f.title)}</span></div>
    ${f.detail ? `<div class="fdetail">${esc(f.detail)}</div>` : ''}
    ${f.fix && f.fix !== 'No action needed.' ? `<div class="ffix"><b>Fix:</b> ${esc(f.fix)}</div>` : ''}
    <div style="margin-top:8px;display:flex;gap:6px">
      ${f.cmd ? `<button class="btn sm sec cpy">Copy command</button>` : ''}
      ${f.link ? `<button class="btn sm sec lnk">Open link</button>` : ''}
    </div>`;
  const cpy = el.querySelector('.cpy'); if (cpy) cpy.onclick = () => { navigator.clipboard?.writeText(f.cmd); toast('Copied', 'ok'); };
  const lnk = el.querySelector('.lnk'); if (lnk) lnk.onclick = () => Host.call('openFile', { path: f.link });
  return el;
}

init();
