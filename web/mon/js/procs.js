/* Processes view: live keyed table (no full re-render), heat-mapped cells, tree mode, details pane, full context menu. */
'use strict';
Views.procs = (() => {
  // p = [pid, ppid, name, cpu, privWS, commit, threads, handles, ioR, ioW, gpu, vram, session, basePrio, createMs, ws]
  const PRIO = new Proxy({}, { get: (_, v) => { v = +v; return !v ? '—' : v <= 4 ? 'Idle' : v <= 6 ? 'Below normal' : v <= 8 ? 'Normal' : v <= 10 ? 'Above normal' : v <= 15 ? 'High' : 'Realtime'; } });
  const CRITICAL = /^(system|smss\.exe|csrss\.exe|wininit\.exe|winlogon\.exe|services\.exe|lsass\.exe|svchost\.exe|dwm\.exe|registry|memory compression|secure system|system idle process)$/i;
  const heatMem = () => St.tick ? St.tick.mem.total * 0.08 : 1e9;
  const COLS = [
    { k: 'name', l: 'Name', v: p => p[2].toLowerCase(), w: 260 },
    { k: 'pid', l: 'PID', v: p => p[0], f: v => v, r: 1, num: 1 },
    { k: 'user', l: 'User', v: p => (Meta.get(p[0]).user || '').toLowerCase(), f: (v, p) => esc(Meta.get(p[0]).user || '') },
    { k: 'cpu', l: 'CPU', v: p => p[3], f: v => v < 0.05 ? '0' : v.toFixed(v < 10 ? 1 : 0) + '%', r: 1, num: 1, heat: v => v / 60, tot: () => F.pct(St.tick.cpu.total, 0) },
    { k: 'mem', l: 'Memory', v: p => p[4], f: v => F.bytes(v, 1), r: 1, num: 1, heat: v => v / heatMem(), tot: () => D.memPct.toFixed(0) + '%', tip: 'Private working set' },
    { k: 'commit', l: 'Commit', v: p => p[5], f: v => F.bytes(v, 1), r: 1, num: 1 },
    { k: 'gpu', l: 'GPU', v: p => p[10], f: v => v < 0.05 ? '0' : v.toFixed(1) + '%', r: 1, num: 1, heat: v => v / 60, tot: () => D.gpus.length ? F.pct(Math.max(...D.gpus.map(g => g.load || 0)), 0) : '' },
    { k: 'vram', l: 'VRAM', v: p => p[11], f: v => v ? F.bytes(v, 0) : '', r: 1, num: 1 },
    { k: 'dr', l: 'Disk read', v: p => p[8], f: v => v ? F.rate(v) : '0', r: 1, num: 1, heat: v => v / 20e6 },
    { k: 'dw', l: 'Disk write', v: p => p[9], f: v => v ? F.rate(v) : '0', r: 1, num: 1, heat: v => v / 20e6 },
    { k: 'thr', l: 'Threads', v: p => p[6], f: v => v, r: 1, num: 1 },
    { k: 'hnd', l: 'Handles', v: p => p[7], f: v => F.num(v), r: 1, num: 1 },
    { k: 'prio', l: 'Priority', v: p => p[13], f: v => PRIO[v] || v },
    { k: 'ses', l: 'Session', v: p => p[12], f: v => v, r: 1, num: 1 },
    { k: 'start', l: 'Started', v: p => p[14], f: v => v ? new Date(v).toLocaleString('en-US', { hour12: false, month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '', num: 1 },
    { k: 'desc', l: 'Description', v: p => (Meta.get(p[0]).desc || '').toLowerCase(), f: (v, p) => esc(Meta.get(p[0]).desc || '') },
    { k: 'path', l: 'Path', v: p => (Meta.get(p[0]).path || '').toLowerCase(), f: (v, p) => esc(Meta.get(p[0]).path || '') },
  ];
  const rows = new Map(); let order = [], selPid = null, built = false, frozen = false, frozenOrder = null;
  const collapsed = new Set(); const seenPids = new Set(); let firstTick = true;
  let detailBuilt = null, detailCharts = [];

  function head() {
    const sk = S.procSort.key;
    $('#pr-table thead').innerHTML = '<tr>' + COLS.map(c => `<th data-k="${c.k}" class="${c.r ? 'r' : ''} ${c.k === sk ? 'sorted' + (S.procSort.asc ? ' asc' : '') : ''}" title="${esc(c.tip || '')}" style="${c.w ? 'min-width:' + c.w + 'px' : ''}">${c.tot ? `<span class="tot" id="tot-${c.k}"></span>` : (COLS.some(x => x.tot) ? '<span class="tot">&nbsp;</span>' : '')}${c.l}</th>`).join('') + '</tr>';
    $$('#pr-table th').forEach(th => th.onclick = () => {
      const k = th.dataset.k; if (S.procSort.key === k) S.procSort.asc = !S.procSort.asc; else { S.procSort.key = k; S.procSort.asc = ['name', 'user', 'desc', 'path', 'pid', 'start'].includes(k); }
      frozenOrder = null; saveSettings(); head(); tick();
    });
  }
  function rowEl(p) {
    const tr = document.createElement('tr'); tr.dataset.pid = p[0];
    COLS.forEach(c => { const td = document.createElement('td'); if (c.r) td.className = 'r'; if (c.num) td.classList.add('num'); tr.appendChild(td); });
    tr.cells[0].innerHTML = '<span class="nmc"><span class="tw"></span><span class="ic"></span><span class="nm"></span></span>';
    tr.onclick = () => select(p[0]);
    tr.ondblclick = () => select(p[0], true);
    tr.oncontextmenu = e => { select(p[0]); menu(e.clientX, e.clientY, p[0]); };
    tr.cells[0].querySelector('.tw').onclick = e => { e.stopPropagation(); const pid = +tr.dataset.pid; collapsed.has(pid) ? collapsed.delete(pid) : collapsed.add(pid); tick(); };
    return { tr, cache: [], icon: null, key: p[0] + ':' + p[14] };
  }

  function computeOrder(list) {
    const c = COLS.find(x => x.k === S.procSort.key) || COLS[3]; const dir = S.procSort.asc ? 1 : -1;
    const key = new Map(); for (const p of list) key.set(p, c.v(p));     // sort key computed once per row, not per comparison
    const cmp = (a, b) => { const va = key.get(a), vb = key.get(b); return (va < vb ? -1 : va > vb ? 1 : 0) * dir || a[0] - b[0]; };
    if (!S.tree) return { rows: [...list].sort(cmp).map(p => [p, 0]) };
    const by = new Map(list.map(p => [p[0], p])); const kids = new Map();
    const roots = [];
    for (const p of list) {
      const par = by.get(p[1]);
      if (par && p[1] !== p[0] && par[14] <= p[14]) { (kids.get(p[1]) || kids.set(p[1], []).get(p[1])).push(p); } else roots.push(p);
    }
    const out = [];
    const walk = (p, d) => { out.push([p, d, kids.has(p[0])]); if (kids.has(p[0]) && !collapsed.has(p[0])) kids.get(p[0]).sort(cmp).forEach(k => walk(k, d + 1)); };
    roots.sort(cmp).forEach(r => walk(r, 0));
    return { rows: out };
  }

  function filterList() {
    const q = $('#pr-filter').value.trim().toLowerCase();
    if (!q) return St.procs;
    return St.procs.filter(p => { const m = Meta.get(p[0]); return p[2].toLowerCase().includes(q) || String(p[0]) === q || (m.path || '').toLowerCase().includes(q) || (m.user || '').toLowerCase().includes(q) || (m.desc || '').toLowerCase().includes(q); });
  }

  function tick() {
    if (!built) { head(); built = true; }
    const list = filterList();
    Meta.want(list.map(p => p[0]));
    let { rows: ord } = computeOrder(list);
    if (frozen && frozenOrder && !S.tree) {
      const pos = new Map(frozenOrder.map((pid, i) => [pid, i]));
      ord.sort((a, b) => (pos.has(a[0][0]) ? pos.get(a[0][0]) : 1e9) - (pos.has(b[0][0]) ? pos.get(b[0][0]) : 1e9));
    }
    if (frozen) frozenOrder = ord.map(r => r[0][0]);
    const tb = $('#pr-table tbody'); const live = new Set();
    for (const [p, depth, hasKids] of ord) {
      const key = p[0] + ':' + p[14]; live.add(p[0]);
      let r = rows.get(p[0]);
      if (r && r.key !== key) { r.tr.remove(); rows.delete(p[0]); r = null; }
      if (!r) { r = rowEl(p); rows.set(p[0], r); if (!firstTick && !seenPids.has(p[0])) r.tr.classList.add('newp'); seenPids.add(p[0]); }
      const m = Meta.get(p[0]);
      // name cell
      const nm = `${p[2]}${m.x86 ? ' (32 bit)' : ''}`;
      if (r.cache[0] !== nm + depth + hasKids + collapsed.has(p[0])) {
        r.cache[0] = nm + depth + hasKids + collapsed.has(p[0]);
        const c0 = r.tr.cells[0]; c0.querySelector('.nm').textContent = nm;
        c0.querySelector('.nmc').style.paddingLeft = (depth * 16) + 'px';
        c0.querySelector('.tw').innerHTML = S.tree && hasKids ? (collapsed.has(p[0]) ? '&#xE76C;' : '&#xE70D;') : '';
      }
      if (r.icon !== (m.path || '') || (m.path && !r.iconOk)) { const u = Icons.get(m.path); r.icon = m.path || ''; r.iconOk = !!u; r.tr.cells[0].querySelector('.ic').innerHTML = `<img class="picon${u ? '' : ' noico'}" src="${u || BLANK}">`; }
      r.tr.classList.toggle('susp', !!m.susp);
      for (let i = 1; i < COLS.length; i++) {
        const c = COLS[i]; const v = c.v(p); const txt = c.f ? c.f(c.k === 'user' || c.k === 'desc' || c.k === 'path' ? null : v, p) : v;
        if (r.cache[i] !== txt) { r.cache[i] = txt; r.tr.cells[i].innerHTML = txt; if (c.k === 'path') r.tr.cells[i].title = m.path || ''; }
        if (c.heat) {
          const a = p[0] === 0 ? 0 : clamp(c.heat(v), 0, 1); const bg = a > 0.01 ? `rgba(${S.theme === 'light' ? '10,102,216' : '79,156,249'},${(0.06 + a * 0.5).toFixed(2)})` : '';
          if (r.tr.cells[i].style.background !== bg && r['h' + i] !== bg) { r['h' + i] = bg; r.tr.cells[i].style.background = bg; }
        }
      }
    }
    // remove dead
    for (const [pid, r] of rows) if (!live.has(pid)) { r.tr.remove(); rows.delete(pid); }
    // reorder only if needed
    const want = ord.map(o => o[0][0]);
    if (want.length !== order.length || want.some((x, i) => x !== order[i])) {
      const frag = document.createDocumentFragment(); want.forEach(pid => frag.appendChild(rows.get(pid).tr)); tb.appendChild(frag); order = want;
    }
    $$('#pr-table tbody tr.sel').forEach(t => { if (+t.dataset.pid !== selPid) t.classList.remove('sel'); });
    if (selPid != null && rows.get(selPid)) rows.get(selPid).tr.classList.add('sel');
    // totals
    COLS.forEach(c => { if (c.tot) { const e = $('#tot-' + c.k); if (e) e.textContent = c.tot(); } });
    const io = St.procs.reduce((a, p) => a + p[8] + p[9], 0);
    const ed = $('#tot-dr'); if (ed) ed.textContent = F.rate(io);
    $('#pr-count').textContent = `${list.length}${list.length !== St.procs.length ? '/' + St.procs.length : ''} processes`;
    firstTick = false;
    detail();
  }

  /* ---------- detail pane ---------- */
  function select(pid, open) {
    selPid = pid; detailBuilt = null;
    $$('#pr-table tbody tr.sel').forEach(t => t.classList.remove('sel'));
    const r = rows.get(pid); if (r) { r.tr.classList.add('sel'); if (open !== false) r.tr.scrollIntoView({ block: 'nearest' }); }
    $('#proc-detail').classList.remove('hidden'); $('#proc-detail').style.height = (S.detailH || 240) + 'px';
    detail();
  }
  function detail() {
    const pane = $('#proc-detail'); if (selPid == null || pane.classList.contains('hidden')) return;
    const p = St.byPid.get(selPid);
    if (!p) { pane.querySelector('h3') && (pane.querySelector('h3').style.textDecoration = 'line-through'); return; }
    const m = Meta.get(p[0]); const key = p[0] + ':' + p[14];
    if (detailBuilt !== key) {
      detailBuilt = key;
      pane.innerHTML = `<div class="pdl"><h3>${Icons.img(m.path)}<span>${esc(p[2])}</span><span class="pill acc">PID ${p[0]}</span><span class="pill" id="pd-state"></span><span class="grow"></span>
          <button class="btn sm sec" id="pd-mods">Modules &amp; command line</button><button class="btn sm danger" id="pd-kill">End task</button><button class="tbtn" id="pd-close" title="Close (Esc)">&#xE8BB;</button></h3>
        <div class="kv" id="pd-kv"></div></div>
        <div class="pdr"><div class="legend" style="margin:0 0 4px"><span><i style="background:${col('--c-cpu')}"></i>CPU <b id="pd-cpu"></b></span><span><i style="background:${col('--c-mem')}"></i>Memory <b id="pd-mem"></b></span></div>
          <div class="chartbox short" style="flex:1"><canvas id="pd-c1"></canvas></div><div class="chartbox short" style="flex:1;margin-top:6px"><canvas id="pd-c2"></canvas></div></div>`;
      const hx = H.proc.get(key) || { cpu: new Ring(60), mem: new Ring(60) };
      detailCharts = [new LineChart($('#pd-c1'), { series: [{ ring: hx.cpu, color: '--c-cpu', label: 'CPU' }], max: 'auto', floor: 5, fmt: v => v.toFixed(1) + '%' }),
        new LineChart($('#pd-c2'), { series: [{ ring: hx.mem, color: '--c-mem', label: 'Memory' }], max: 'auto', floor: 1048576, fmt: v => F.bytes(v, 0) })];
      $('#pd-close').onclick = () => { pane.classList.add('hidden'); };
      $('#pd-kill').onclick = () => kill(p[0], false);
      $('#pd-mods').onclick = () => modules(p[0]);
    }
    const par = St.byPid.get(p[1]);
    const kv = [['Description', m.desc], ['Company', m.company], ['Version', m.ver], ['Path', m.path], ['User', m.user], ['Parent', par ? `${par[2]} (${p[1]})` : `${p[1]} (exited)`],
      ['Started', p[14] ? new Date(p[14]).toLocaleString() + ' · ' + F.dur(Date.now() - p[14]) + ' ago' : ''], ['Priority', PRIO[p[13]] || p[13]], ['Threads / handles', `${p[6]} / ${F.num(p[7])}`],
      ['Working set (total / private)', `${F.bytes(p[15], 1)} / ${F.bytes(p[4], 1)}`], ['Commit', F.bytes(p[5], 1)], ['GPU / VRAM', `${p[10].toFixed(1)}% / ${F.bytes(p[11], 0)}`], ['Disk I/O', `R ${F.rate(p[8])} · W ${F.rate(p[9])}`], ['Session', p[12]]];
    $('#pd-kv').innerHTML = kv.map(r => `<span class="k">${r[0]}</span><span class="v" title="${esc(r[1])}">${esc(r[1] == null || r[1] === '' ? '—' : r[1])}</span>`).join('');
    $('#pd-cpu').textContent = p[3].toFixed(1) + '%'; $('#pd-mem').textContent = F.bytes(p[4], 1);
    const st = $('#pd-state'); st.textContent = m.susp ? 'SUSPENDED' : 'running'; st.className = 'pill ' + (m.susp ? 'warn' : 'ok');
    detailCharts.forEach(c => c.draw());
  }
  async function modules(pid) {
    toast('Reading modules…');
    try {
      const d = await Host.call('procDetails', { pid });
      const html = `<div class="kv" style="margin-bottom:10px"><span class="k">Command line</span><span class="v" style="white-space:pre-wrap;word-break:break-all">${esc(d.cmd || '—')}</span>
        <span class="k">Path</span><span class="v">${esc(d.path || '—')}</span><span class="k">Started</span><span class="v">${esc(d.start || '—')}</span>
        <span class="k">Priority class</span><span class="v">${esc(d.prio || '—')}</span><span class="k">Affinity</span><span class="v">0x${esc(d.affinity || '?')}</span><span class="k">Threads</span><span class="v">${d.threads}</span></div>
        <div class="muted" style="margin-bottom:4px">${d.modules.length} modules${d.modErr ? ' — ' + esc(d.modErr) : ''}</div>
        <table class="t"><thead><tr><th>Module</th><th class="r">Size</th><th>Version</th><th>Path</th></tr></thead><tbody>${d.modules.map(x => `<tr><td>${esc(x.name)}</td><td class="r num">${F.bytes(x.size, 0)}</td><td class="num">${esc(x.ver || '')}</td><td title="${esc(x.path)}">${esc(x.path)}</td></tr>`).join('')}</tbody></table>`;
      modal({ title: `${(St.byPid.get(pid) || [0, 0, '?'])[2]} — PID ${pid}`, html, buttons: [{ label: 'Close', value: null }] });
    } catch (e) { toast(e.message, 'error'); }
  }

  /* ---------- actions ---------- */
  async function kill(pid, tree) {
    const p = St.byPid.get(pid); if (!p) return;
    const crit = CRITICAL.test(p[2]);
    if (crit || tree) {
      const ok = await confirmBox(`End ${tree ? 'process tree' : 'process'}: ${p[2]} (${pid})?`, crit ? '⚠ This looks like a critical Windows process. Killing it can crash or log off the system.' : 'All child processes will be terminated too.', tree ? 'End tree' : 'End process', 'danger');
      if (!ok) return;
    }
    try { await Host.call('procKill', { pid, tree }); toast(`Ended ${p[2]} (${pid})`); } catch (e) { toast(`Couldn't end ${p[2]}: ${e.message}`, 'error'); }
  }
  async function affinity(pid) {
    const cur = parseInt(await Host.call('procAffinity', { pid, mask: '' }).catch(() => '0'), 16) || 0;
    const boxes = Array.from({ length: St.cores }, (_, i) => `<label style="display:inline-flex;gap:4px;width:84px;font-family:var(--mono)"><input type="checkbox" data-c="${i}" ${(cur / Math.pow(2, i)) % 2 >= 1 ? 'checked' : ''}>CPU ${i}</label>`).join('');
    const ok = await modal({ title: 'Processor affinity — PID ' + pid, html: `<div>${boxes}</div>`, buttons: [{ label: 'Cancel', value: false }, { label: 'Apply', value: true }] });
    if (!ok) return;
    let mask = 0n; $$('#m-body input[data-c]').forEach(c => { if (c.checked) mask |= 1n << BigInt(c.dataset.c); });
    if (!mask) return toast('Pick at least one CPU', 'error');
    Host.call('procAffinity', { pid, mask: mask.toString(16) }).then(v => toast('Affinity set: 0x' + v)).catch(e => toast(e.message, 'error'));
  }
  function menu(x, y, pid) {
    const p = St.byPid.get(pid); if (!p) return; const m = Meta.get(pid);
    const prio = l => () => Host.call('procPriority', { pid, level: l }).then(v => toast(`${p[2]} → ${v}`)).catch(e => toast(e.message, 'error'));
    ctxMenu(x, y, [
      { label: 'End task', key: 'Del', danger: 1, click: () => kill(pid, false) },
      { label: 'End process tree', key: 'Shift+Del', danger: 1, click: () => kill(pid, true) }, '-',
      m.susp ? { label: 'Resume', click: () => Host.call('procResume', { pid }).then(() => { Meta.refresh(pid); toast('Resumed ' + p[2]); }).catch(e => toast(e.message, 'error')) }
        : { label: 'Suspend', click: () => Host.call('procSuspend', { pid }).then(() => { Meta.refresh(pid); toast('Suspended ' + p[2]); }).catch(e => toast(e.message, 'error')) },
      { label: 'Set priority', sub: [['Realtime', 'RealTime'], ['High', 'High'], ['Above normal', 'AboveNormal'], ['Normal', 'Normal'], ['Below normal', 'BelowNormal'], ['Idle', 'Idle']].map(([l, v]) => ({ label: l, check: PRIO[p[13]] && PRIO[p[13]].replace(' ', '').toLowerCase() === v.toLowerCase(), click: prio(v) })) },
      { label: 'Set affinity…', click: () => affinity(pid) }, '-',
      { label: 'Details (modules, command line)', click: () => modules(pid) },
      { label: 'Open file location', disabled: !m.path, click: () => Host.call('openLocation', { path: m.path }) },
      { label: 'Search online', click: () => Host.call('openUrl', { url: 'https://www.google.com/search?q=' + encodeURIComponent(p[2] + ' ' + (m.desc || '')) }) },
      { label: 'Show network connections', click: () => { setView('network'); $('#nw-filter').value = String(pid); Views.network.tick(); } },
      { label: 'Go to parent', disabled: !St.byPid.get(p[1]), click: () => select(p[1]) }, '-',
      { label: 'Copy name', click: () => navigator.clipboard.writeText(p[2]) },
      { label: 'Copy path', disabled: !m.path, click: () => navigator.clipboard.writeText(m.path) },
      { label: 'Copy row', click: () => navigator.clipboard.writeText(COLS.map(c => c.l + ': ' + String(c.f ? c.f(c.v(p), p) : c.v(p)).replace(/<[^>]+>/g, '')).join('\n')) },
    ]);
  }

  /* ---------- wiring ---------- */
  $('#pr-filter').addEventListener('input', debounce(() => { frozenOrder = null; tick(); }, 120));
  $('#pr-tree').onclick = () => { S.tree = !S.tree; $('#pr-tree').classList.toggle('on', S.tree); saveSettings(); tick(); };
  $('#pr-tree').classList.toggle('on', !!S.tree);
  $('#pr-freeze').onclick = () => { frozen = !frozen; frozenOrder = null; $('#pr-freeze').classList.toggle('on', frozen); };
  $('#pr-run').onclick = runTask;
  $('#pr-end').onclick = () => selPid != null ? kill(selPid, false) : toast('Select a process first');
  document.addEventListener('meta', () => { if (St.view === 'procs' && St.tick) tick(); });
  document.addEventListener('icons', () => { if (St.view === 'procs' && St.tick) tick(); });
  // detail pane resize
  (() => {
    const sash = $('#pd-sash'); let y0, h0;
    sash.onmousedown = e => {
      const pane = $('#proc-detail'); if (pane.classList.contains('hidden')) return;
      y0 = e.clientY; h0 = pane.offsetHeight; sash.classList.add('drag');
      const mv = ev => { pane.style.height = clamp(h0 - (ev.clientY - y0), 120, innerHeight - 200) + 'px'; detailCharts.forEach(c => c.draw()); };
      const up = () => { sash.classList.remove('drag'); S.detailH = pane.offsetHeight; saveSettings(); removeEventListener('mousemove', mv); removeEventListener('mouseup', up); };
      addEventListener('mousemove', mv); addEventListener('mouseup', up);
    };
  })();
  return {
    tick, select(pid) { select(pid); },
    show() { /* S.tree may be loaded after module init */ $('#pr-tree').classList.toggle('on', !!S.tree); },
    esc() { $('#proc-detail').classList.add('hidden'); },
    key(e) {
      if (e.key === 'Delete' && selPid != null) { kill(selPid, e.shiftKey); e.preventDefault(); }
      if ((e.key === 'ArrowDown' || e.key === 'ArrowUp') && order.length) {
        e.preventDefault(); let i = order.indexOf(selPid); i = clamp(i + (e.key === 'ArrowDown' ? 1 : -1), 0, order.length - 1); select(order[i]);
      }
      if (e.key === 'Enter' && selPid != null) modules(selPid);
    },
  };
})();
