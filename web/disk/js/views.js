/* XenoDisk views: health, volumes, finder, space, dupes, recover, usb, tools, settings. */
'use strict';
const Views = {};

// ---------------- VOLUMES ----------------
Views.volumes = async function () {
  const tb = $('#vl-table'); const vols = [];
  State.disks.forEach(d => (d.partitions || []).forEach(p => { if (p.letter || p.fs) vols.push({ d, p }); }));
  tb.querySelector('thead').innerHTML = '<tr><th>Vol</th><th>Label</th><th>Disk</th><th>FS</th><th class="r">Size</th><th class="r">Free</th><th class="r">Used</th><th>Type</th><th>Health</th></tr>';
  const body = tb.querySelector('tbody'); body.innerHTML = '';
  const flt = ($('#vl-filter').value || '').toLowerCase();
  vols.filter(v => !flt || (v.p.letter + ' ' + (v.p.label || '') + ' ' + (v.p.fs || '')).toLowerCase().includes(flt)).forEach(({ d, p }) => {
    const used = p.size && p.free != null ? Number(p.size) - Number(p.free) : null;
    const pct = used != null && p.size ? used / Number(p.size) * 100 : 0;
    const tr = h(`<tr>
      <td class="mono">${esc(p.letter || '—')}</td><td>${esc(p.label || '')}</td>
      <td class="mono">${d.number}</td><td class="mono">${esc(p.fs || '')}</td>
      <td class="r mono">${F.gb(p.size)}</td><td class="r mono">${p.free ? F.gb(p.free) : '—'}</td>
      <td class="r"><span class="meter"><i style="width:${pct}%;background:${pct > 90 ? 'var(--err)' : 'var(--accent)'}"></i></span> <span class="mono">${F.pct(pct)}</span></td>
      <td>${esc(p.typeName || '')}</td><td>${p.volHealth === 'Healthy' ? '<span class="pill ok">Healthy</span>' : '<span class="pill warn">' + esc(p.volHealth || '—') + '</span>'}</td></tr>`);
    tr.oncontextmenu = e => { e.preventDefault(); selectPart(d.number, p.number); partMenu(e.clientX, e.clientY, d, p); };
    body.appendChild(tr);
  });
  $('#vl-filter').oninput = debounce(Views.volumes, 150);
  $('#vl-refresh').onclick = loadDisks;
};

// ---------------- HEALTH ----------------
Views.health = async function () {
  const body = $('#hl-body'); body.innerHTML = '<div class="empty">Reading SMART…</div>';
  $('#hl-refresh').onclick = Views.health;
  try {
    const r = await Host.call('health');
    body.innerHTML = '<div class="g-tiles"></div>';
    const grid = body.querySelector('.g-tiles');
    r.list.forEach(x => {
      const rel = x.reliability || {};
      const bad = x.health !== 'Healthy' || x.smartPredictFail;
      grid.appendChild(h(`<div class="htile">
        <div class="hh"><span class="dot ${bad ? 'bad' : 'ok'}"></span><b>${x.disk != null ? 'Disk ' + x.disk : 'Storage device'}</b><span class="grow"></span>${x.smartPredictFail ? '<span class="pill bad">SMART FAIL</span>' : '<span class="pill ' + (bad ? 'warn' : 'ok') + '">' + esc(x.health) + '</span>'}</div>
        <div class="muted mono" style="margin-bottom:8px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${esc(x.model || '')}</div>
        <div class="kv">
          <span class="k">SMART prediction</span><span class="v">${x.smartPredictFail == null ? 'Unavailable' : x.smartPredictFail ? 'Failure predicted' : 'No failure predicted'}</span>
          ${rel.temp ? `<span class="k">Temp</span><span class="v">${F.temp(rel.temp)}${rel.tempMax ? ' (max ' + F.temp(rel.tempMax) + ')' : ''}</span>` : ''}
          ${rel.powerOnHours ? `<span class="k">Power-on</span><span class="v">${F.dur(rel.powerOnHours)}</span>` : ''}
          ${x.usage != null ? `<span class="k">Usage</span><span class="v">${['Unknown', 'Auto-select', 'Hot spare', 'Manual', 'Retired'][x.usage] || x.usage}</span>` : ''}
          ${rel.wear ? `<span class="k">Wear</span><span class="v">${rel.wear}%</span>` : ''}
          ${rel.readErrors != null ? `<span class="k">Read errors</span><span class="v">${F.num(rel.readErrors)}</span>` : ''}
          ${rel.writeErrors != null ? `<span class="k">Write errors</span><span class="v">${F.num(rel.writeErrors)}</span>` : ''}
        </div>
        ${x.disk != null ? `<div style="margin-top:10px"><button class="btn sm sec" data-scan="${x.disk}">Surface scan</button></div>` : '<div class="muted">Physical disk identity not confirmed; select the disk in Disks to scan it.</div>'}
      </div>`));
    });
    grid.querySelectorAll('[data-scan]').forEach(b => b.onclick = () => Jobs.run('surfaceScan', { disk: +b.dataset.scan }, j => j.result && toast('Disk ' + b.dataset.scan + ': ' + (j.result.bad ? j.result.bad + ' bad regions' : 'clean'), j.result.bad ? 'warn' : 'ok')));
    if (!r.list.length) body.innerHTML = '<div class="empty">No physical disks reported.</div>';
  } catch (e) { body.innerHTML = `<div class="empty">Health unavailable: ${esc(e.message)}</div>`; }
};

// ---------------- FINDER ----------------
Views._found = [];
Views.finder = function () {
  $('#fd-pick').onclick = async () => { const p = await Host.call('pickFolder', { title: 'Folder to search' }); if (p) $('#fd-root').value = p; };
  $('#fd-go').onclick = Views.runFind;
  $('#fd-recycle').onclick = () => Views.actSelected('#fd-table', 'recycle');
  $('#fd-move').onclick = () => Views.actSelected('#fd-table', 'move');
};
Views.runFind = function () {
  const root = $('#fd-root').value.trim(); if (!root) return toast('Choose a folder', 'warn');
  const args = {
    root, pattern: $('#fd-pattern').value.trim() || '*', minKB: (+$('#fd-minmb').value || 0) * 1024,
    olderDays: +$('#fd-older').value || 0, sort: $('#fd-sort').value, limit: S.findLimit || 5000, ...scanOpts(),
  };
  const body = $('#fd-table').querySelector('tbody'); body.innerHTML = '';
  $('#fd-count').textContent = 'scanning…';
  Jobs.run('findFiles', args, j => {
    if (j.state !== 'done') { $('#fd-count').textContent = j.state; return; }
    Views._found = j.result.files || [];
    $('#fd-count').textContent = `${j.result.count} matches (scanned ${F.num(j.result.scanned)})`;
    Views.renderFiles();
  });
};
Views.renderFiles = function () {
  const tb = $('#fd-table');
  tb.querySelector('thead').innerHTML = '<tr><th class="chk"><input type="checkbox" id="fd-all"></th><th>Name</th><th class="r">Size</th><th>Modified</th><th>Last used</th><th>Path</th></tr>';
  const body = tb.querySelector('tbody'); body.innerHTML = '';
  Views._found.forEach((f, i) => {
    const tr = h(`<tr data-i="${i}"><td class="chk"><input type="checkbox"></td><td>${esc(f.name)}</td><td class="r mono">${F.bytes(f.size)}</td><td class="mono">${F.date(f.modified)}</td><td class="mono">${F.date(f.accessed)}</td><td class="mono muted">${esc(f.path)}</td></tr>`);
    tr.oncontextmenu = e => { e.preventDefault(); ctxMenu(e.clientX, e.clientY, [{ label: 'Open', click: () => Host.call('openFile', { path: f.path }) }, { label: 'Show in Explorer', click: () => Host.call('reveal', { path: f.path }) }, '-', { label: 'Recycle', danger: true, click: () => Views.doRecycle([f.path]) }]); };
    body.appendChild(tr);
  });
  $('#fd-all').onchange = e => body.querySelectorAll('input[type=checkbox]').forEach(c => c.checked = e.target.checked);
};
Views.selectedPaths = function (table, arr) {
  const out = [];
  $$(table + ' tbody tr').forEach(tr => { const c = tr.querySelector('input[type=checkbox]'); if (c && c.checked) out.push(arr[+tr.dataset.i].path || arr[+tr.dataset.i]); });
  return out;
};
Views.actSelected = async function (table, kind) {
  const paths = Views.selectedPaths(table, Views._found);
  if (!paths.length) return toast('Nothing selected', 'warn');
  if (kind === 'recycle') return Views.doRecycle(paths);
  const dest = await Host.call('pickFolder', { title: 'Move files to…' }); if (!dest) return;
  Jobs.run('moveFiles', { paths, dest, copy: false }, j => j.state === 'done' && toast(`Moved ${j.result.done} file(s)`, 'ok'));
};
Views.doRecycle = async function (paths) {
  if (!await confirmDanger('Recycle files', `<div class="warnbox">Send ${paths.length} item(s) to the Recycle Bin?</div>`, 'Recycle')) return;
  const r = await Host.call('recycleFiles', { paths });
  toast(`Recycled ${r.ok}` + (r.errors.length ? `, ${r.errors.length} failed` : ''), r.errors.length ? 'warn' : 'ok');
  if (State.view === 'finder') { Views._found = Views._found.filter(f => !paths.includes(f.path)); Views.renderFiles(); }
};

// ---------------- SPACE ANALYZER ----------------
Views._spaceRoot = null;
Views.space = function () {
  $('#sp-pick').onclick = async () => { const p = await Host.call('pickFolder', { title: 'Folder to analyze' }); if (p) $('#sp-root').value = p; };
  $('#sp-go').onclick = () => {
    const root = $('#sp-root').value.trim(); if (!root) return toast('Choose a folder', 'warn');
    $('#sp-tree').innerHTML = '<div class="empty">Scanning…</div>';
    Jobs.run('spaceScan', { root, ...scanOpts() }, j => { if (j.state === 'done') { Views._spaceRoot = j.result; Views.renderSpace(j.result, [j.result]); } });
  };
};
Views.renderSpace = function (node, path) {
  $('#sp-sub').textContent = `${F.bytes(node.size)} · ${F.num(node.files)} files`;
  const cr = $('#sp-crumbs'); cr.innerHTML = '';
  path.forEach((n, i) => { const s = h(`<span>${esc(n.name || n.path)}</span>`); s.onclick = () => Views.openSpace(n, path.slice(0, i + 1)); cr.appendChild(s); if (i < path.length - 1) cr.appendChild(h('<span class="sep">/</span>')); });
  const tree = $('#sp-tree'); tree.innerHTML = '';
  const kids = (node.children || []).slice();
  const max = kids.reduce((a, k) => Math.max(a, k.size), 1);
  kids.forEach(k => {
    const row = h(`<div class="trow"><div class="tn"><span class="tw">${k.children && k.children.length ? '▸' : ''}</span><span class="nm">${esc(k.name)}</span></div><div class="bar"><i style="width:${k.size / max * 100}%"></i></div><div class="sz mono">${F.bytes(k.size)}</div></div>`);
    row.onclick = () => Views.openSpace(k, path.concat(k));
    row.oncontextmenu = e => { e.preventDefault(); ctxMenu(e.clientX, e.clientY, [{ label: 'Open in Explorer', click: () => Host.call('reveal', { path: k.path }) }, { label: 'File types here…', click: () => Views.spaceTypes(k) }, '-', { label: 'Recycle folder', danger: true, click: () => Views.doRecycle([k.path]) }]); };
    tree.appendChild(row);
  });
  if (!kids.length) tree.appendChild(h('<div class="empty">No subfolders — leaf folder.</div>'));
};
Views.openSpace = async function (node, path) {
  if (!node.children) { try { const r = await Host.call('spaceChildren', { path: node.path }); node.children = r.children; } catch { node.children = []; } }
  Views.renderSpace(node, path);
};
Views.spaceTypes = async function (node) {
  const r = await Host.call('spaceTypes', { path: node.path });
  const rows = (r.types || []).map(t => `<tr><td class="mono">${esc(t.ext)}</td><td class="r mono">${F.bytes(t.size)}</td><td class="r mono">${F.num(t.count)}</td></tr>`).join('');
  modal({ title: 'File types · ' + node.name, bodyHtml: `<table class="t"><thead><tr><th>Ext</th><th class="r">Size</th><th class="r">Count</th></tr></thead><tbody>${rows}</tbody></table>`, buttons: [{ label: 'Close', value: true }] });
};

// ---------------- DUPLICATES ----------------
Views._dupes = [];
Views.dupes = function () {
  $('#dp-pick').onclick = async () => { const p = await Host.call('pickFolder', { title: 'Folder to scan' }); if (p) $('#dp-root').value = p; };
  $('#dp-go').onclick = () => {
    const root = $('#dp-root').value.trim(); if (!root) return toast('Choose a folder', 'warn');
    $('#dp-body').innerHTML = '<div class="empty">Scanning…</div>';
    Jobs.run('findDupes', { root, minKB: +$('#dp-minkb').value || 1, hashAlgo: S.dupeHash, ...scanOpts() }, j => { if (j.state === 'done') { Views._dupes = j.result.groups || []; Views.renderDupes(j.result); } });
  };
  $('#dp-recycle').onclick = () => {
    const paths = $$('#dp-body input[type=checkbox]:checked').map(c => c.dataset.path);
    if (!paths.length) return toast('Nothing selected', 'warn');
    Views.doRecycle(paths).then(() => Views.dupes());
  };
};
Views.renderDupes = function (res) {
  $('#dp-sub').textContent = `${res.groups.length} groups · ${F.bytes(res.wasted)} reclaimable`;
  const body = $('#dp-body'); body.innerHTML = '';
  if (!res.groups.length) { body.appendChild(h('<div class="empty">No duplicates found.</div>')); return; }
  res.groups.forEach(g => {
    const card = h(`<div class="card"><div class="ch">${F.bytes(g.size)} each · ${g.files.length} copies<span class="r muted mono">${esc(g.hash.slice(0, 12))}</span></div></div>`);
    g.files.forEach((f, i) => {
      const row = h(`<div class="trow"><div class="tn"><input type="checkbox" data-path="${esc(f)}" ${i === 0 ? '' : 'checked'} style="margin-right:6px"><span class="nm mono">${esc(f)}</span></div><div></div><div class="sz"><span class="btn sm sec rev">Open</span></div></div>`);
      row.querySelector('.rev').onclick = () => Host.call('reveal', { path: f });
      card.appendChild(row);
    });
    body.appendChild(card);
  });
  const note = h('<div class="muted" style="font-size:11px;margin-top:6px">First copy in each group is left unchecked by default (kept).</div>');
  body.appendChild(note);
};

// ---------------- RECOVER ----------------
Views._rtab = 'recycle'; Views._recycle = []; Views._shadows = []; Views._shadowStack = [];
Views.recover = function () {
  $$('#view-recover .chip').forEach(c => c.onclick = () => { $$('#view-recover .chip').forEach(x => x.classList.toggle('on', x === c)); Views._rtab = c.dataset.rtab; Views.loadRecover(); });
  $('#rc-refresh').onclick = Views.loadRecover;
  $('#rc-restore').onclick = Views.restoreSelected;
  Views.loadRecover();
};
Views.loadRecover = async function () {
  const body = $('#rc-body'); body.innerHTML = '<div class="empty">Loading…</div>';
  if (Views._rtab === 'recycle') {
    const r = await Host.call('recycleList');
    Views._recycle = r.items || [];
    body.innerHTML = `<table class="t"><thead><tr><th class="chk"><input type="checkbox" id="rc-all"></th><th>Name</th><th>Original location</th><th>Deleted</th><th class="r">Size</th></tr></thead><tbody></tbody></table>`;
    const tb = body.querySelector('tbody');
    Views._recycle.forEach((it, i) => tb.appendChild(h(`<tr data-i="${i}"><td class="chk"><input type="checkbox" data-id="${esc(it.id)}"></td><td>${esc(it.name)}</td><td class="mono muted">${esc(it.originalPath)}</td><td class="mono">${esc(it.deleted)}</td><td class="r mono">${F.bytes(it.size)}</td></tr>`)));
    const all = $('#rc-all'); if (all) all.onchange = e => tb.querySelectorAll('input[type=checkbox]').forEach(c => c.checked = e.target.checked);
    if (!Views._recycle.length) body.innerHTML = '<div class="empty">Recycle Bin is empty.</div>' + (r.error ? `<div class="empty">${esc(r.error)}</div>` : '');
  } else {
    const r = await Host.call('shadowList');
    Views._shadows = r.list || [];
    if (!Views._shadows.length) { body.innerHTML = '<div class="empty">No restore points / shadow copies found.<br><br>Create one below, or enable System Protection in Windows.</div><div style="text-align:center"><button class="btn sm" id="sh-create">Create snapshot of a volume…</button></div>'; bindCreate(); return; }
    body.innerHTML = '<div class="card"><div class="ch">Snapshots<span class="r"><button class="btn sm" id="sh-create">New snapshot…</button></span></div><div id="sh-list"></div></div><div id="sh-browser"></div>';
    const list = $('#sh-list');
    Views._shadows.forEach(s => { const row = h(`<div class="trow"><div class="tn mono">${esc(s.created)} — vol ${esc(s.volume || '')}</div><div></div><div class="sz"><span class="btn sm sec">Browse</span></div></div>`); row.querySelector('.btn').onclick = () => Views.browseShadow(s, ''); list.appendChild(row); });
    bindCreate();
  }
  function bindCreate() { const b = $('#sh-create'); if (b) b.onclick = Views.createSnapshot; }
};
Views.browseShadow = async function (s, path) {
  Views._shadowCur = s; Views._shadowPath = path;
  const r = await Host.call('shadowBrowse', { device: s.device, path });
  const br = $('#sh-browser'); br.innerHTML = `<div class="card"><div class="ch">Snapshot ${esc(s.created)} <span class="r"><span class="crumbs" id="sh-crumbs"></span></span></div><div id="sh-files"></div></div>`;
  const cr = $('#sh-crumbs'); const parts = path ? path.split('\\').filter(Boolean) : [];
  const root = h('<span>\\</span>'); root.onclick = () => Views.browseShadow(s, ''); cr.appendChild(root);
  parts.forEach((p, i) => { cr.appendChild(h('<span class="sep">\\</span>')); const sp = h(`<span>${esc(p)}</span>`); sp.onclick = () => Views.browseShadow(s, parts.slice(0, i + 1).join('\\')); cr.appendChild(sp); });
  const fl = $('#sh-files');
  if (r.error) { fl.innerHTML = `<div class="empty">${esc(r.error)}</div>`; return; }
  (r.dirs || []).forEach(d => { const row = h(`<div class="trow"><div class="tn"><span class="tw">▸</span><span class="nm">${esc(d.name)}</span></div><div></div><div></div></div>`); row.onclick = () => Views.browseShadow(s, d.rel); fl.appendChild(row); });
  (r.files || []).forEach(f => { const row = h(`<div class="trow"><div class="tn"><input type="checkbox" data-rel="${esc(f.rel)}" style="margin-right:6px"><span class="nm mono">${esc(f.name)}</span></div><div class="mono muted">${F.date(f.modified)}</div><div class="sz mono">${F.bytes(f.size)}</div></div>`); fl.appendChild(row); });
  if (!r.dirs.length && !r.files.length) fl.appendChild(h('<div class="empty">Empty.</div>'));
};
Views.restoreSelected = async function () {
  if (Views._rtab === 'recycle') {
    const ids = $$('#rc-body input[type=checkbox]:checked').map(c => c.dataset.id).filter(Boolean);
    if (!ids.length) return toast('Nothing selected', 'warn');
    const where = await modal({ title: 'Restore from Recycle Bin', bodyHtml: '<div>Restore to original locations, or pick a folder?</div>', buttons: [{ label: 'Cancel', value: null }, { label: 'Pick folder', value: 'pick', cls: 'sec' }, { label: 'Original location', value: 'orig' }] });
    if (!where) return;
    let dest = '';
    if (where === 'pick') { dest = await Host.call('pickFolder', { title: 'Restore to…' }); if (!dest) return; }
    const r = await Host.call('recycleRestore', { ids, dest });
    toast(`Restored ${r.ok}` + (r.errors && r.errors.length ? `, ${r.errors.length} failed` : ''), r.errors && r.errors.length ? 'warn' : 'ok');
    Views.loadRecover();
  } else {
    if (!Views._shadowCur) return toast('Browse a snapshot first', 'warn');
    const rels = $$('#sh-files input[type=checkbox]:checked').map(c => c.dataset.rel);
    if (!rels.length) return toast('Select files to restore', 'warn');
    const dest = await Host.call('pickFolder', { title: 'Restore snapshot files to…' }); if (!dest) return;
    Jobs.run('shadowRestore', { device: Views._shadowCur.device, paths: rels, dest }, j => j.state === 'done' && toast(`Restored ${j.result.ok} file(s)`, 'ok'));
  }
};
Views.createSnapshot = async function () {
  const letters = State.disks.flatMap(d => (d.partitions || []).filter(p => p.letter && /NTFS|ReFS/i.test(p.fs || '')).map(p => p.letter));
  if (!letters.length) return toast('No NTFS volume with a letter found', 'warn');
  const r = await modal({ title: 'Create snapshot', bodyHtml: `<div class="field"><label>Volume</label><select id="sn-vol">${letters.map(l => `<option>${l}</option>`).join('')}</select></div><div class="muted" style="font-size:11px">Creates a Volume Shadow Copy you can restore files from.</div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Create', collect: () => $('#sn-vol').value }] });
  if (!r) return;
  try { await Host.call('shadowCreate', { letter: r }); toast('Snapshot created', 'ok'); Views.loadRecover(); }
  catch (e) { toast(e.message, 'error'); }
};

// ---------------- USB ----------------
Views.usb = async function () {
  $('#ub-refresh').onclick = loadDisks;
  const wp = await Host.call('usbWriteProtect'); const sw = $('#ub-wp');
  sw.classList.toggle('on', !!wp.on);
  sw.onclick = async () => { const on = !sw.classList.contains('on'); try { await Host.call('setUsbWriteProtect', { on }); sw.classList.toggle('on', on); toast('USB write-protect ' + (on ? 'ON' : 'OFF') + ' (replug to apply)', 'ok'); } catch (e) { toast(e.message, 'error'); } };
  const body = $('#ub-body'); body.innerHTML = '';
  const usb = State.disks.filter(d => d.usb || d.removable);
  if (!usb.length) { body.appendChild(h('<div class="empty">No USB / removable drives connected.</div>')); return; }
  usb.forEach(d => {
    const letters = (d.partitions || []).filter(p => p.letter).map(p => p.letter);
    const card = h(`<div class="card"><div class="ch">${esc(d.model || 'USB disk')} — Disk ${d.number} <span class="r muted mono">${F.gb(d.size)} · ${esc(d.bus)}</span></div>
      <div class="dmeta">${letters.length ? 'Volumes: ' + letters.join(', ') : 'No mounted volume'}</div>
      <div class="dacts"></div></div>`);
    const acts = card.querySelector('.dacts');
    const b1 = h('<button class="btn sm">Safely eject</button>'); b1.onclick = async () => { try { const r = await Host.call('eject', { disk: d.number }); toast(r.ok ? 'Ejected — safe to remove' : (r.note || 'Dismounted'), r.ok ? 'ok' : 'warn'); loadDisks(); } catch (e) { toast(e.message, 'error'); } };
    acts.appendChild(b1);
    if (letters.length) { const b2 = h('<button class="btn sm sec">Capacity / fake-flash test…</button>'); b2.onclick = () => Views.capTest(letters[0]); acts.appendChild(b2); }
    const b3 = h('<button class="btn sm sec">Benchmark…</button>'); if (letters.length) { b3.onclick = () => Views.benchmark(letters[0], S.benchSizeMB); acts.appendChild(b3); }
    const b4 = h('<button class="btn sm warn">Image to file…</button>'); b4.onclick = () => Views.imageBackup(d); acts.appendChild(b4);
    body.appendChild(card);
  });
};
Views.capTest = async function (letter) {
  if (!await confirmDanger('Capacity test', `<div class="warnbox">Writes test data into free space on ${esc(letter)}, then removes its own test files. Use a backed-up device: faulty flash can corrupt existing files during a write test. Don't unplug during the test.</div>`, 'Start test')) return;
  Jobs.run('capacityTest', { letter, maxMB: 0 }, j => { if (j.state === 'done') { const r = j.result; modal({ title: 'Capacity test · ' + letter, bodyHtml: `<div class="kv"><span class="k">Verdict</span><span class="v">${esc(r.verdict)}</span><span class="k">Verified test data</span><span class="v">${F.bytes(r.verifiedMB * 1048576)}</span><span class="k">Reported free space</span><span class="v">${F.bytes(r.claimedFreeMB * 1048576)}</span></div>`, buttons: [{ label: 'Close', value: true }] }); } });
};

// ---------------- TOOLS ----------------
Views.tools = function () {
  const body = $('#tl-body'); body.innerHTML = '';
  const volumes = State.disks.flatMap(d => (d.partitions || []).filter(p => p.letter).map(p => p.letter));
  body.appendChild(h(`<div class="card"><div class="ch">Check &amp; repair (chkdsk)</div>
    <div class="field" style="display:flex;gap:8px;align-items:center"><select id="tl-chk-vol">${volumes.map(v => `<option>${v}</option>`).join('')}</select>
    <select id="tl-chk-mode"><option value="scan">Online scan (may perform online repairs)</option><option value="fix">/F fix errors</option><option value="rebuild">/F /R fix + recover bad sectors</option></select>
    <button class="btn sm" id="tl-chk-go">Run chkdsk</button></div></div>`));
  body.appendChild(h(`<div class="card"><div class="ch">Optimize / defrag</div>
    <div style="display:flex;gap:8px;align-items:center"><select id="tl-opt-vol">${volumes.map(v => `<option>${v}</option>`).join('')}</select>
    <button class="btn sm sec" id="tl-opt-an">Analyze</button><button class="btn sm" id="tl-opt-go">Optimize</button>
    <span class="muted" style="font-size:11px">SSD = TRIM/retrim, HDD = defrag (auto-detected by Windows)</span></div></div>`));
  body.appendChild(h(`<div class="card"><div class="ch">Benchmark</div>
    <div style="display:flex;gap:8px;align-items:center"><select id="tl-bn-vol">${volumes.map(v => `<option>${v}</option>`).join('')}</select>
    <label>Test size <select id="tl-bn-size"><option value="512">512 MB</option><option value="1024" selected>1 GB</option><option value="2048">2 GB</option></select></label>
    <button class="btn sm" id="tl-bn-go">Run benchmark</button></div><div id="tl-bn-out" class="mono muted" style="margin-top:8px"></div></div>`));
  body.appendChild(h(`<div class="card"><div class="ch">Disk imaging (raw .img)</div>
    <div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center">
      <select id="tl-im-disk">${State.disks.map(d => `<option value="${d.number}">Disk ${d.number} — ${esc(d.model || '')} (${F.gb(d.size)})</option>`).join('')}</select>
      <button class="btn sm" id="tl-im-backup">Backup disk → file…</button>
      <button class="btn sm warn" id="tl-im-write">Write file → disk…</button>
    </div><div class="muted" style="font-size:11px;margin-top:4px">Sector-by-sector. Writing overwrites the whole target disk.</div></div>`));
  body.appendChild(h(`<div class="card"><div class="ch">Utilities</div>
    <div style="display:flex;gap:8px;flex-wrap:wrap">
      <button class="btn sm sec" id="tl-hash">Hash a file (MD5/SHA)…</button>
      <button class="btn sm sec" id="tl-clean-dry">Analyze temp files</button>
      <button class="btn sm" id="tl-clean">Clean temp files</button>
      <button class="btn sm sec" id="tl-dm">Open Disk Management</button>
    </div></div>`));

  $('#tl-chk-go').onclick = () => Views.chkdsk($('#tl-chk-vol').value, $('#tl-chk-mode').value);
  $('#tl-opt-an').onclick = () => Jobs.run('optimizeVolume', { letter: $('#tl-opt-vol').value, analyzeOnly: true });
  $('#tl-opt-go').onclick = () => Views.optimize($('#tl-opt-vol').value);
  $('#tl-bn-go').onclick = () => Views.benchmark($('#tl-bn-vol').value, +$('#tl-bn-size').value);
  $('#tl-im-backup').onclick = () => Views.imageBackup(State.disks.find(d => d.number == $('#tl-im-disk').value));
  $('#tl-im-write').onclick = () => Views.imageWrite(State.disks.find(d => d.number == $('#tl-im-disk').value));
  $('#tl-hash').onclick = Views.hashFile;
  $('#tl-clean-dry').onclick = () => Views.cleanTemp(true);
  $('#tl-clean').onclick = () => Views.cleanTemp(false);
  $('#tl-dm').onclick = () => Host.call('openDiskMgmt');
};
Views.chkdsk = async function (letter, mode) {
  if (!mode) { const r = await modal({ title: 'chkdsk ' + letter, bodyHtml: `<div class="field"><label>Mode</label><select id="ck"><option value="scan">Online scan (may perform online repairs)</option><option value="fix">/F fix errors</option><option value="rebuild">/F /R fix + bad sectors</option></select></div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Run', collect: () => $('#ck').value }] }); if (!r) return; mode = r; }
  Jobs.run('chkdsk', { letter, mode }, j => j.state === 'done' && toast('chkdsk ' + letter + ' finished' + (j.result.note ? ' — ' + j.result.note : ''), 'ok'));
};
Views.optimize = function (letter) { Jobs.run('optimizeVolume', { letter, analyzeOnly: false }, j => j.state === 'done' && toast('Optimize ' + letter + ' done', 'ok')); };
Views.benchmark = function (letter, sizeMB) {
  Jobs.run('benchmark', { letter, sizeMB: sizeMB || 1024 }, j => { if (j.state === 'done') { const r = j.result; const out = `SEQ read ${r.seqReadMBps} MB/s · write ${r.seqWriteMBps} MB/s | 4K read ${F.num(r.randReadIOPS)} IOPS (${r.randReadMBps} MB/s) · write ${F.num(r.randWriteIOPS)} IOPS (${r.randWriteMBps} MB/s)`; const el = $('#tl-bn-out'); if (el) el.textContent = letter + ': ' + out; modal({ title: 'Benchmark · ' + letter, bodyHtml: `<div class="kv"><span class="k">Seq read</span><span class="v">${r.seqReadMBps} MB/s</span><span class="k">Seq write</span><span class="v">${r.seqWriteMBps} MB/s</span><span class="k">4K rand read</span><span class="v">${F.num(r.randReadIOPS)} IOPS · ${r.randReadMBps} MB/s</span><span class="k">4K rand write</span><span class="v">${F.num(r.randWriteIOPS)} IOPS · ${r.randWriteMBps} MB/s</span></div>`, buttons: [{ label: 'Close', value: true }] }); } });
};
Views.imageBackup = async function (d) {
  if (!d) { toast('Select a disk first', 'warn'); return; }
  const file = await Host.call('saveFile', { title: 'Save disk image', filter: 'Disk image (*.img)|*.img|All files|*.*', name: `disk${d.number}.img` }); if (!file) return;
  Jobs.run('imageBackup', { disk: d.number, file, verify: S.imageVerify, bufferMB: S.ioBufferMB }, j => j.state === 'done' && toast('Image written: ' + F.bytes(j.result.bytes), 'ok'));
};
Views.imageWrite = async function (d) {
  if (!d) { toast('Select a disk first', 'warn'); return; }
  if (d.isSystem || d.isBoot) { toast('Cannot write over a system/boot disk', 'error'); return; }
  if (!d.offline) { toast('Take the target disk offline before writing an image', 'warn'); return; }
  const file = await Host.call('pickFile', { title: 'Image file to write', filter: 'Disk image (*.img)|*.img|All files|*.*' }); if (!file) return;
  if (!await modal({ title: 'Write image to disk', bodyHtml: `<div class="warnbox">Overwrite the start of Disk ${d.number} — ${esc(d.model || '')} (${F.gb(d.size)}) — with this image? Existing partitions and files can become inaccessible. Bytes beyond the image length are not erased.</div><div class="field"><label>Type to confirm</label><input id="iw" placeholder="WRITE"></div>`, buttons: [
    { label: 'Cancel', value: false },
    { label: 'Write', cls: 'danger', collect: () => { if ($('#iw').value.trim().toUpperCase() !== 'WRITE') { toast('Type WRITE to confirm', 'warn'); return false; } return true; } }
  ] })) return;
  Jobs.run('imageWrite', { file, disk: d.number, verify: S.imageVerify, bufferMB: S.ioBufferMB }, j => j.state === 'done' && toast('Image written: ' + F.bytes(j.result.bytes), 'ok'));
};
Views.hashFile = async function () {
  const file = await Host.call('pickFile', { title: 'File to hash' }); if (!file) return;
  Jobs.run('hashFile', { file }, j => { if (j.state === 'done') { const r = j.result; modal({ title: 'Hashes', bodyHtml: `<div class="kv"><span class="k">Size</span><span class="v">${F.bytes(r.size)}</span><span class="k">MD5</span><span class="v">${r.md5}</span><span class="k">SHA-1</span><span class="v">${r.sha1}</span><span class="k">SHA-256</span><span class="v" style="word-break:break-all">${r.sha256}</span></div>`, buttons: [{ label: 'Close', value: true }] }); } });
};
Views.cleanTemp = async function (dry) {
  if (dry === false && !await confirmDanger('Clean temp files', '<div>Delete accessible files from the user and Windows temporary/cache folders? Files in use may be skipped. This cannot be undone.</div>', 'Clean')) return;
  Jobs.run('cleanupTemp', { dryRun: dry !== false }, j => { if (j.state === 'done') { const r = j.result; toast(`${r.dryRun ? 'Found' : 'Cleaned'} ${F.num(r.files)} temp files · ${F.bytes(r.bytes)}${r.failed ? ' · ' + F.num(r.failed) + ' failed' : ''}`, r.failed ? 'warn' : 'ok'); } });
};

// ---------------- SETTINGS ----------------
Views.settings = function () {
  const body = $('#st-body');
  const cores = State.cores || 4;
  const row = (label, hint, control) => `<div class="set"><div class="sl">${label}${hint ? `<small>${hint}</small>` : ''}</div><div>${control}</div></div>`;
  const sw = (id, on) => `<span class="switch ${on ? 'on' : ''}" data-sw="${id}"></span>`;
  body.innerHTML = `
    <div class="card"><div class="ch">Performance</div><div class="set-grid">
      ${row('Scan threads', `How many cores file scans use. Auto = all ${cores} logical cores.`,
        `<select data-set="threads">${['0:Auto (' + cores + ' cores)', '1:1', '2:2', '4:4', '8:8', '16:16'].map(o => { const [v, t] = o.split(':'); return `<option value="${v}" ${+S.threads === +v ? 'selected' : ''}>${t}</option>`; }).join('')}</select>`)}
      ${row('I/O buffer (imaging)', 'Larger = faster sequential transfer, more RAM.', `<select data-set="ioBufferMB">${[1, 2, 4, 8, 16, 32, 64].map(v => `<option value="${v}" ${+S.ioBufferMB === v ? 'selected' : ''}>${v} MB</option>`).join('')}</select>`)}
      ${row('Benchmark size', '', `<select data-set="benchSizeMB">${[512, 1024, 2048, 4096].map(v => `<option value="${v}" ${+S.benchSizeMB === v ? 'selected' : ''}>${v >= 1024 ? v / 1024 + ' GB' : v + ' MB'}</option>`).join('')}</select>`)}
      ${row('Find result cap', 'Max files returned by a search.', `<select data-set="findLimit">${[1000, 5000, 10000, 50000].map(v => `<option value="${v}" ${+S.findLimit === v ? 'selected' : ''}>${v.toLocaleString()}</option>`).join('')}</select>`)}
    </div></div>

    <div class="card"><div class="ch">Scanning</div><div class="set-grid">
      ${row('Follow junctions / symlinks', 'Off avoids loops and double-counting.', sw('followReparse', S.followReparse))}
      ${row('Include hidden files', '', sw('includeHidden', S.includeHidden))}
      ${row('Include system files', '', sw('includeSystem', S.includeSystem))}
      ${row('Duplicate hashing', 'Quick = size + head/tail (fast). xxHash = full fast. SHA-256 = safest.',
        `<select data-set="dupeHash">${['sha256:SHA-256', 'md5:MD5', 'xxhash:xxHash (fast)', 'quick:Quick (size+ends)'].map(o => { const [v, t] = o.split(':'); return `<option value="${v}" ${S.dupeHash === v ? 'selected' : ''}>${t}</option>`; }).join('')}</select>`)}
      ${row('Skip folders', 'One per line. Name or path-suffix (e.g. Windows\\WinSxS).', `<textarea data-set="skipDirs" style="width:100%;height:70px;font-family:var(--mono);font-size:12px">${esc((S.skipDirs || []).join('\n'))}</textarea>`)}
    </div></div>

    <div class="card"><div class="ch">Display</div><div class="set-grid">
      ${row('Theme', '', `<select data-set="theme"><option value="dark" ${S.theme !== 'light' ? 'selected' : ''}>Dark</option><option value="light" ${S.theme === 'light' ? 'selected' : ''}>Light</option></select>`)}
      ${row('Size units', '', `<select data-set="sizeUnit"><option value="binary" ${S.sizeUnit !== 'decimal' ? 'selected' : ''}>Binary (GiB, 1024)</option><option value="decimal" ${S.sizeUnit === 'decimal' ? 'selected' : ''}>Decimal (GB, 1000)</option></select>`)}
      ${row('Temperature', '', `<select data-set="tempUnit"><option value="C" ${S.tempUnit !== 'F' ? 'selected' : ''}>Celsius</option><option value="F" ${S.tempUnit === 'F' ? 'selected' : ''}>Fahrenheit</option></select>`)}
      ${row('Auto-refresh on USB plug', '', sw('autoRefresh', S.autoRefresh))}
    </div></div>

    <div class="card"><div class="ch">Safety</div><div class="set-grid">
      ${row('Confirm destructive actions', 'Prompt before delete / recycle / erase. Format & image-write always ask with a typed word.', sw('confirmDestructive', S.confirmDestructive))}
      ${row('Verify after imaging', 'Read back and compare after a disk image.', sw('imageVerify', S.imageVerify))}
    </div></div>

    <div class="card"><div class="ch">Elevation</div>
      <div>${State.info.admin ? '<span class="pill ok">Running as Administrator</span> — all operations available.' : '<span class="pill warn">Not elevated</span> — partitioning, SMART, imaging and snapshots need admin. <button class="btn sm" id="st-elevate">Restart as admin</button>'}</div></div>

    <div class="card"><div class="ch">About</div><div class="muted mono" style="font-size:12px">XenoDisk v${State.info.version} · ${esc(State.info.machine)} · ${cores} cores</div>
      <div style="margin-top:8px"><button class="btn sm sec" id="st-log">Open log folder</button> <button class="btn sm sec" id="st-dev">DevTools</button> <button class="btn sm sec" id="st-reset">Reset to defaults</button></div></div>`;

  // wire controls
  body.querySelectorAll('[data-set]').forEach(el => {
    const key = el.dataset.set;
    const handler = () => {
      if (el.tagName === 'TEXTAREA') S[key] = el.value.split('\n').map(x => x.trim()).filter(Boolean);
      else if (el.tagName === 'SELECT') S[key] = /^\d+$/.test(el.value) ? +el.value : el.value;
      saveS();
      if (key === 'theme') applyTheme();
      if (['sizeUnit', 'tempUnit'].includes(key)) { if (State.view === 'settings') { } loadDisks(); }
    };
    el.addEventListener(el.tagName === 'TEXTAREA' ? 'input' : 'change', handler);
  });
  body.querySelectorAll('[data-sw]').forEach(el => el.onclick = () => { const k = el.dataset.sw; S[k] = !S[k]; el.classList.toggle('on', S[k]); saveS(); });
  const el = $('#st-elevate'); if (el) el.onclick = () => Host.call('restartAdmin');
  $('#st-log').onclick = () => Host.call('reveal', { path: State.info.data });
  $('#st-dev').onclick = () => Host.call('devTools');
  $('#st-reset').onclick = async () => { if (await confirmDanger('Reset settings', '<div>Restore all settings to defaults?</div>', 'Reset')) { await Host.call('saveSettings', { json: '{}' }); await loadS(); applyTheme(); Views.settings(); toast('Settings reset', 'ok'); } };
};
