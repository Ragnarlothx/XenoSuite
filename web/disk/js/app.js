/* XenoDisk app: boot, navigation, disks + partition operations. */
'use strict';
const State = { info: null, disks: [], sel: { disk: null, part: null }, view: 'disks' };

async function boot() {
  const bl = $('#boot-log');
  const log = t => { bl.appendChild(h(`<div>${esc(t)}</div>`)); };
  try {
    State.info = await Host.call('ready');
    await loadS(); applyTheme();
    State.cores = State.info.cores || navigator.hardwareConcurrency || 4;
    log('host ready · admin=' + State.info.admin + ' · cores=' + State.cores);
    $('#app').classList.remove('hidden'); $('#boot').remove();
    $('#mb-host').textContent = State.info.machine;
    $('#mb-admin').innerHTML = State.info.admin ? '<span class="pill ok">ADMIN</span>' : '<span class="pill warn">LIMITED</span>';
    $('#sb-admin').innerHTML = State.info.admin ? 'Administrator' : 'Not elevated — click to restart as admin';
    if (!State.info.admin) $('#sb-admin').onclick = () => Host.call('restartAdmin');
    setClock(); setInterval(setClock, 1000);
    wireChrome();
    Host.on('devchange', () => { if (S.autoRefresh && ['disks', 'volumes', 'usb', 'health'].includes(State.view)) refreshView(); });
    await loadDisks();
    show('disks');
  } catch (e) {
    log('FATAL: ' + e.message);
  }
}

function setClock() { $('#mb-clock').textContent = new Date().toLocaleTimeString(); }

function wireChrome() {
  $$('#activitybar .act').forEach(b => b.onclick = () => show(b.dataset.view));
  document.addEventListener('keydown', e => {
    if (e.target.matches('input,select,textarea')) return;
    const map = { '1': 'disks', '2': 'volumes', '3': 'health', '4': 'finder', '5': 'space', '6': 'dupes', '7': 'recover', '8': 'usb', '9': 'tools' };
    if (map[e.key]) show(map[e.key]);
    if (e.key === 'F5') { e.preventDefault(); refreshView(); }
  });
  // menubar
  const menus = {
    file: [
      { label: 'Refresh', key: 'F5', click: refreshView },
      { label: 'Open Disk Management', click: () => Host.call('openDiskMgmt') },
      '-',
      { label: 'Exit', click: () => Host.call('quit') },
    ],
    tools: [
      { label: 'Find old / large files', click: () => show('finder') },
      { label: 'Duplicate finder', click: () => show('dupes') },
      { label: 'Space analyzer', click: () => show('space') },
      { label: 'Clean temp files', click: () => Views.cleanTemp() },
      '-',
      { label: 'Benchmark a volume…', click: () => show('tools') },
      { label: 'Image a disk to file…', click: () => show('tools') },
      { label: 'Hash a file…', click: () => Views.hashFile() },
      '-',
      { label: 'DevTools', key: 'F12', click: () => Host.call('devTools') },
    ],
    view: [
      { label: 'Disks & partitions', click: () => show('disks') },
      { label: 'Volumes', click: () => show('volumes') },
      { label: 'Health & SMART', click: () => show('health') },
      { label: 'Recover / restore', click: () => show('recover') },
      { label: 'USB & removable', click: () => show('usb') },
      { label: 'Settings', click: () => show('settings') },
    ],
    help: [
      { label: 'About XenoDisk', click: about },
      { label: 'Readme', click: () => Host.call('openFile', { path: 'README.txt' }) },
    ],
  };
  $$('#menubar .mtop').forEach(m => m.onclick = e => {
    const open = m.classList.contains('open'); closeMenus(); if (open) return;
    m.classList.add('open');
    const r = m.getBoundingClientRect();
    ctxMenu(r.left, r.bottom + 2, menus[m.dataset.menu]);
  });
  // disks view buttons
  $('#dk-refresh').onclick = loadDisks;
  $('#dk-diskmgmt').onclick = () => Host.call('openDiskMgmt');
}

function show(view) {
  State.view = view;
  $$('#activitybar .act').forEach(b => b.classList.toggle('active', b.dataset.view === view));
  $$('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + view));
  refreshView();
}
function refreshView() {
  switch (State.view) {
    case 'disks': renderDisks(); break;
    case 'volumes': Views.volumes(); break;
    case 'health': Views.health(); break;
    case 'finder': Views.finder(); break;
    case 'space': Views.space(); break;
    case 'dupes': Views.dupes(); break;
    case 'recover': Views.recover(); break;
    case 'usb': Views.usb(); break;
    case 'tools': Views.tools(); break;
    case 'settings': Views.settings(); break;
  }
}

async function loadDisks() {
  try {
    const r = await Host.call('disks');
    State.disks = r.disks || [];
    $('#dk-count').textContent = State.disks.length + ' disk' + (State.disks.length !== 1 ? 's' : '');
    $('#sb-disks').textContent = State.disks.length + ' disks · ' +
      State.disks.reduce((a, d) => a + (d.partitions ? d.partitions.length : 0), 0) + ' partitions';
    if (State.view === 'disks') renderDisks();
  } catch (e) { toast('Failed to list disks: ' + e.message, 'error'); }
}

const SEGCLASS = p => p.isSystem || p.isBoot ? 'seg-sys' : /recovery/i.test(p.typeName || '') ? 'seg-rec' : /efi/i.test(p.typeName || '') ? 'seg-efi' : /reserved/i.test(p.typeName || '') ? 'seg-msr' : 'seg-data';

function renderDisks() {
  const body = $('#dk-body'); body.innerHTML = '';
  if (!State.disks.length) { body.appendChild(h('<div class="empty">No disks found.</div>')); return; }
  for (const d of State.disks) body.appendChild(diskCard(d));
}

function diskCard(d) {
  const el = h(`<div class="disk" data-disk="${d.number}"></div>`);
  if (State.sel.disk === d.number) el.classList.add('sel');
  const badges = [];
  if (d.offline) badges.push('<span class="pill warn">OFFLINE</span>');
  if (d.readOnly) badges.push('<span class="pill warn">READ-ONLY</span>');
  if (d.isSystem) badges.push('<span class="pill acc">SYSTEM</span>');
  if (d.usb) badges.push('<span class="pill">USB</span>');
  if (d.health && d.health !== 'Healthy') badges.push(`<span class="pill bad">${esc(d.health)}</span>`);
  el.innerHTML = `
    <div class="dh">
      <span class="dnum">DISK ${d.number}</span>
      <span class="dn">${esc(d.model || 'Disk ' + d.number)}</span>
      ${badges.join(' ')}
      <span class="grow"></span>
      <span class="model">${esc(d.style)} · ${F.gb(d.size)}</span>
    </div>
    <div class="dmeta">
      <span>Bus <b>${esc(d.bus || '—')}</b></span>
      <span>Media <b>${esc(d.media || '—')}</b></span>
      ${d.serial ? `<span>S/N <b>${esc(d.serial)}</b></span>` : ''}
      ${d.spindle > 0 ? `<span>${d.spindle} RPM</span>` : ''}
      <span>Health <b>${esc(d.health)}</b></span>
    </div>
    <div class="pillbar"></div>
    <div class="dacts"></div>`;
  const bar = el.querySelector('.pillbar');
  // build segments: partitions + free regions, by offset
  const segs = [];
  (d.partitions || []).forEach(p => segs.push({ kind: 'part', off: Number(p.offset), size: Number(p.size), p }));
  (d.free || []).forEach(f => segs.push({ kind: 'free', off: Number(f.offset), size: Number(f.size) }));
  segs.sort((a, b) => a.off - b.off);
  const total = Number(d.size) || segs.reduce((a, s) => a + s.size, 0) || 1;
  if (!segs.length) bar.appendChild(h(`<div class="seg-unalloc" style="flex:1"><div class="pl1">Uninitialized</div><div class="pl2">${esc(d.style)}</div></div>`));
  for (const s of segs) {
    const pct = Math.max(0.6, s.size / total * 100);
    if (s.kind === 'free') {
      const fe = h(`<div class="seg-unalloc" style="flex:${pct}"><div class="pl1">Unallocated</div><div class="pl2">${F.gb(s.size)}</div></div>`);
      fe.onclick = () => newPartition(d, s);
      fe.title = 'Unallocated ' + F.gb(s.size) + ' — click to create a partition';
      bar.appendChild(fe);
    } else {
      const p = s.p, usedPct = p.free && p.size ? (1 - Number(p.free) / Number(p.size)) * 100 : 0;
      const seg = h(`<div class="${SEGCLASS(p)} pfree" style="flex:${pct}" data-part="${p.number}">
          <i style="width:${usedPct}%"></i>
          <div class="pl1">${esc(p.letter || p.typeName || ('Part ' + p.number))}${p.label ? ' · ' + esc(p.label) : ''}</div>
          <div class="pl2">${esc(p.fs || p.typeName || '')} · ${F.gb(p.size)}${p.free ? ' · ' + F.gb(p.free) + ' free' : ''}</div>
      </div>`);
      if (State.sel.disk === d.number && State.sel.part === p.number) seg.classList.add('psel');
      seg.onclick = () => { selectPart(d.number, p.number); };
      seg.oncontextmenu = e => { e.preventDefault(); selectPart(d.number, p.number); partMenu(e.clientX, e.clientY, d, p); };
      seg.title = `${p.typeName || ''} ${p.letter || ''} ${F.gb(p.size)}`;
      bar.appendChild(seg);
    }
  }
  // disk actions
  const acts = el.querySelector('.dacts');
  const add = (label, cls, fn, title) => { const b = h(`<button class="btn sm ${cls || 'sec'}" title="${esc(title || '')}">${label}</button>`); b.onclick = fn; acts.appendChild(b); };
  if (d.style === 'RAW') add('Initialize…', '', () => initDisk(d), 'Create a partition table');
  if (d.offline) add('Bring online', '', () => op('diskOnline', { disk: d.number, on: true }));
  else add('Take offline', 'sec', () => op('diskOnline', { disk: d.number, on: false }));
  add(d.readOnly ? 'Clear read-only' : 'Set read-only', 'sec', () => op('diskReadOnly', { disk: d.number, on: !d.readOnly }));
  if (!d.isSystem) add('Convert ' + (d.style === 'GPT' ? 'to MBR' : 'to GPT'), 'sec', () => convertDisk(d));
  add('Image to file…', 'sec', () => Views.imageBackup(d));
  add('Surface scan', 'sec', () => Jobs.run('surfaceScan', { disk: d.number }, j => j.result && toast('Surface scan: ' + (j.result.bad ? j.result.bad + ' bad regions' : 'no bad regions'), j.result.bad ? 'warn' : 'ok')));
  if (!d.isSystem) add('Write image…', 'warn', () => Views.imageWrite(d));
  if (!d.isSystem) add('Erase disk…', 'danger', () => clearDisk(d));
  return el;
}

function selectPart(disk, part) {
  State.sel = { disk, part };
  const d = State.disks.find(x => x.number === disk);
  const p = d && d.partitions.find(x => x.number === part);
  $('#sb-sel').textContent = p ? `Disk ${disk} · ${p.letter || 'Part ' + part} · ${p.typeName || ''} · ${F.gb(p.size)}` : '';
  renderDisks();
}

/* ---------------- partition operations ---------------- */
function partMenu(x, y, d, p) {
  const sys = p.isSystem || p.isBoot;
  ctxMenu(x, y, [
    { label: 'Format…', click: () => formatPart(d, p), disabled: sys },
    { label: 'Resize / extend…', click: () => resizePart(d, p) },
    { label: p.letter ? 'Change drive letter…' : 'Assign drive letter…', click: () => changeLetter(d, p) },
    { label: p.letter ? 'Remove drive letter' : '', click: () => op('removeLetter', { disk: d.number, part: p.number }), disabled: !p.letter || sys },
    { label: 'Change label…', click: () => changeLabel(d, p) },
    '-',
    { label: p.isActive ? 'Mark inactive' : 'Mark active (MBR boot)', click: () => op('setPartAttr', { disk: d.number, part: p.number, active: !p.isActive }), disabled: d.style !== 'MBR' },
    { label: p.isHidden ? 'Unhide' : 'Hide', click: () => op('setPartAttr', { disk: d.number, part: p.number, hidden: !p.isHidden }) },
    { label: p.isReadOnly ? 'Clear read-only' : 'Set read-only', click: () => op('setPartAttr', { disk: d.number, part: p.number, readOnly: !p.isReadOnly }) },
    '-',
    p.letter ? { label: 'Check & repair (chkdsk)…', click: () => Views.chkdsk(p.letter) } : '-',
    p.letter ? { label: 'Optimize / defrag…', click: () => Views.optimize(p.letter) } : '-',
    p.letter ? { label: 'Benchmark…', click: () => Views.benchmark(p.letter) } : '-',
    '-',
    { label: 'Delete partition', danger: true, click: () => deletePart(d, p), disabled: sys },
  ].filter(x => x !== '-' || true));
}

async function op(method, args, msg) {
  try { await Host.call(method, args); toast(msg || 'Done', 'ok'); await loadDisks(); }
  catch (e) { toast(e.message, 'error'); }
}

function fsOptions(sel) { return ['NTFS', 'exFAT', 'FAT32', 'ReFS', 'FAT'].map(f => `<option ${f === sel ? 'selected' : ''}>${f}</option>`).join(''); }

async function newPartition(d, free) {
  const letters = await Host.call('freeLetters');
  const maxMB = Math.floor(free.size / 1048576);
  const r = await modal({
    title: `New partition on Disk ${d.number}`,
    bodyHtml: `<div class="field"><label>Size (MB)</label><input id="np-size" type="number" value="${maxMB}" max="${maxMB}"></div>
      <div class="field"><label>Max available</label><span class="mono">${F.gb(free.size)}</span></div>
      <div class="field"><label>File system</label><select id="np-fs">${fsOptions('NTFS')}<option value="none">(leave raw)</option></select></div>
      <div class="field"><label>Label</label><input id="np-label" placeholder="New Volume"></div>
      <div class="field"><label>Drive letter</label><select id="np-letter"><option value="auto">Auto</option>${letters.map(l => `<option>${l}</option>`).join('')}</select></div>
      <div class="field"><label>Quick format</label><input id="np-quick" type="checkbox" checked style="width:auto"></div>`,
    buttons: [{ label: 'Cancel', value: false }, {
      label: 'Create', cls: '', collect: () => ({
        sizeMB: clamp(+$('#np-size').value, 1, maxMB), fs: $('#np-fs').value, label: $('#np-label').value,
        letter: $('#np-letter').value, quick: $('#np-quick').checked,
      })
    }],
  });
  if (!r) return;
  try { await Host.call('createPartition', { disk: d.number, ...r }); toast('Partition created', 'ok'); loadDisks(); }
  catch (e) { toast(e.message, 'error'); }
}

async function formatPart(d, p) {
  const r = await modal({
    title: `Format ${p.letter || 'partition ' + p.number}`,
    bodyHtml: `<div class="warnbox">This erases everything on ${esc(p.letter || p.typeName || 'this partition')} (${F.gb(p.size)}${p.label ? ', “' + esc(p.label) + '”' : ''}).</div>
      <div class="field"><label>File system</label><select id="ff-fs">${fsOptions(p.fs || 'NTFS')}</select></div>
      <div class="field"><label>Label</label><input id="ff-label" value="${esc(p.label || '')}"></div>
      <div class="field"><label>Allocation unit</label><select id="ff-cluster"><option value="0">Default</option><option>4096</option><option>8192</option><option>16384</option><option>32768</option><option>65536</option></select></div>
      <div class="field"><label>Quick format</label><input id="ff-quick" type="checkbox" checked style="width:auto"></div>
      <div class="field"><label>Type to confirm</label><input id="ff-confirm" placeholder="FORMAT"></div>`,
    buttons: [{ label: 'Cancel', value: false }, {
      label: 'Format', cls: 'danger', collect: () => {
        if ($('#ff-confirm').value.trim().toUpperCase() !== 'FORMAT') { toast('Type FORMAT to confirm', 'warn'); return false; }
        return { fs: $('#ff-fs').value, label: $('#ff-label').value, cluster: +$('#ff-cluster').value, quick: $('#ff-quick').checked };
      }
    }],
  });
  if (!r) return;
  try { await Host.call('formatVolume', { disk: d.number, part: p.number, ...r }); toast('Formatted', 'ok'); loadDisks(); }
  catch (e) { toast(e.message, 'error'); }
}

async function resizePart(d, p) {
  let supp; try { supp = await Host.call('supportedSize', { disk: d.number, part: p.number }); } catch (e) { supp = { min: 0, max: Number(p.size) }; }
  const minMB = Math.ceil(Number(supp.min) / 1048576), maxMB = Math.floor(Number(supp.max) / 1048576), curMB = Math.round(Number(p.size) / 1048576);
  const r = await modal({
    title: `Resize ${p.letter || 'partition ' + p.number}`,
    bodyHtml: `<div class="field"><label>Current</label><span class="mono">${curMB.toLocaleString()} MB</span></div>
      <div class="field"><label>Allowed range</label><span class="mono">${minMB.toLocaleString()} – ${maxMB.toLocaleString()} MB</span></div>
      <div class="field"><label>New size (MB)</label><input id="rs-size" type="number" value="${curMB}" min="${minMB}" max="${maxMB}"></div>
      <div class="muted" style="font-size:11px">Shrinking is limited by unmovable files near the end of the volume. Extending needs unallocated space immediately after it.</div>`,
    buttons: [{ label: 'Cancel', value: false }, { label: 'Resize', collect: () => ({ sizeMB: clamp(+$('#rs-size').value, minMB, maxMB) }) }],
  });
  if (!r) return;
  try { await Host.call('resizePartition', { disk: d.number, part: p.number, ...r }); toast('Resized', 'ok'); loadDisks(); }
  catch (e) { toast(e.message, 'error'); }
}

async function changeLetter(d, p) {
  const letters = await Host.call('freeLetters');
  const r = await modal({
    title: 'Drive letter', bodyHtml: `<div class="field"><label>Assign letter</label><select id="cl-letter">${(p.letter ? [p.letter] : []).concat(letters).map(l => `<option>${l}</option>`).join('')}</select></div>`,
    buttons: [{ label: 'Cancel', value: false }, { label: 'Assign', collect: () => $('#cl-letter').value }],
  });
  if (!r) return;
  try { if (p.letter) await Host.call('removeLetter', { disk: d.number, part: p.number }); await Host.call('setLetter', { disk: d.number, part: p.number, letter: r }); toast('Letter set', 'ok'); loadDisks(); }
  catch (e) { toast(e.message, 'error'); }
}
async function changeLabel(d, p) {
  const r = await modal({ title: 'Volume label', bodyHtml: `<div class="field"><label>Label</label><input id="lb" value="${esc(p.label || '')}"></div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Set', collect: () => $('#lb').value }] });
  if (r === null) return;
  op('setLabel', { disk: d.number, part: p.number, label: r }, 'Label set');
}
async function deletePart(d, p) {
  if (!await confirmDanger('Delete partition', `<div class="warnbox">Delete ${esc(p.letter || 'partition ' + p.number)} (${F.gb(p.size)}${p.label ? ', “' + esc(p.label) + '”' : ''})? All data on it is lost.</div>`, 'Delete')) return;
  op('deletePartition', { disk: d.number, part: p.number }, 'Partition deleted');
}
async function initDisk(d) {
  const r = await modal({ title: `Initialize Disk ${d.number}`, bodyHtml: `<div class="field"><label>Partition style</label><select id="in-style"><option>GPT</option><option>MBR</option></select></div><div class="muted" style="font-size:11px">GPT for drives over 2 TB and modern systems; MBR for legacy/BIOS boot.</div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Initialize', collect: () => $('#in-style').value }] });
  if (!r) return; op('initDisk', { disk: d.number, style: r }, 'Disk initialized');
}
async function convertDisk(d) {
  const to = d.style === 'GPT' ? 'MBR' : 'GPT';
  if (!await confirmDanger('Convert disk', `<div class="warnbox">Convert Disk ${d.number} from ${d.style} to ${to}? The disk must have no partitions.</div>`, 'Convert')) return;
  op('convertDisk', { disk: d.number, style: to }, 'Converted to ' + to);
}
async function clearDisk(d) {
  const r = await modal({ title: `Erase Disk ${d.number}`, bodyHtml: `<div class="warnbox">This deletes ALL partitions and data on ${esc(d.model || 'Disk ' + d.number)} (${F.gb(d.size)}).</div><div class="field"><label>Type to confirm</label><input id="er" placeholder="ERASE"></div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Erase', cls: 'danger', collect: () => $('#er').value.trim().toUpperCase() === 'ERASE' ? true : (toast('Type ERASE', 'warn'), false) }] });
  if (!r) return; op('clearDisk', { disk: d.number }, 'Disk erased');
}

function about() {
  modal({ title: 'XenoDisk', bodyHtml: `<div style="font:12px var(--mono);line-height:1.7">
    <b>XenoDisk</b> v${State.info.version} — Xeno's disk & storage toolkit (Project 12).<br>
    Disk / partition / volume management, SMART health, file finder, duplicate finder,<br>
    space analyzer, Recycle Bin & snapshot recovery, USB tools, imaging, benchmark.<br><br>
    Backend uses the Windows Storage WMI provider plus diskpart / chkdsk / defrag / vssadmin.</div>`, buttons: [{ label: 'Close', value: true }] });
}
