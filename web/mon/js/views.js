/* Sensors, network, disks, services, startup, system info and settings views. */
'use strict';

/* ================= SENSORS ================= */
Views.sensors = (() => {
  let sig = '', rows = new Map(), pinCharts = [], pinSig = '', vis = new Set();
  const io = new IntersectionObserver(es => es.forEach(e => { const id = e.target.dataset.id; if (e.isIntersecting) vis.add(id); else vis.delete(id); }), { root: $('#view-sensors .vbody') });
  const typeMax = t => ({ Load: 100, Control: 100, Level: 100, Humidity: 100, Temperature: 110 }[t]);
  function visible() {
    const q = $('#se-filter').value.trim().toLowerCase(); const hide = new Set(S.hiddenSensorTypes);
    return St.tick.hw.sensors.filter(s => !hide.has(s.t) && (!q || s.n.toLowerCase().includes(q) || s.t.toLowerCase().includes(q) || ((St.hw.get(s.hw) || {}).name || '').toLowerCase().includes(q)));
  }
  function chips() {
    const types = [...new Set(St.tick.hw.sensors.map(s => s.t))].sort();
    $('#se-types').innerHTML = types.map(t => `<span class="chip ${S.hiddenSensorTypes.includes(t) ? '' : 'on'}" data-t="${t}">${t}</span>`).join('');
    $$('#se-types .chip').forEach(c => c.onclick = e => {
      const t = c.dataset.t;
      if (e.ctrlKey) S.hiddenSensorTypes = types.filter(x => x !== t);         // ctrl-click = solo
      else S.hiddenSensorTypes = S.hiddenSensorTypes.includes(t) ? S.hiddenSensorTypes.filter(x => x !== t) : [...S.hiddenSensorTypes, t];
      saveSettings(); sig = ''; chips(); tick();
    });
  }
  function build(list) {
    const tb = $('#se-table tbody'); tb.innerHTML = ''; rows.clear(); io.disconnect(); vis.clear();
    const byHw = new Map(); list.forEach(s => (byHw.get(s.hw) || byHw.set(s.hw, []).get(s.hw)).push(s));
    for (const [hwId, ss] of byHw) {
      const hw = St.hw.get(hwId) || { name: hwId, type: '' }; const par = hw.parent ? St.hw.get(hw.parent) : null;
      tb.appendChild(h(`<tr class="grp"><td></td><td colspan="6">${esc(hw.name)}<span class="gsub">${esc(hw.type)}${par ? ' · ' + esc(par.name) : ''} · ${esc(hwId)}</span></td></tr>`));
      for (const s of ss) {
        const tr = h(`<tr data-id="${esc(s.id)}"><td><span class="star ${S.pinned.includes(s.id) ? 'on' : ''}">${S.pinned.includes(s.id) ? '&#xE735;' : '&#xE734;'}</span></td><td>${esc(s.n)}</td><td class="r num v"></td><td class="r num mn muted"></td><td class="r num mx muted"></td><td><canvas class="spk"></canvas></td><td class="muted">${s.t}</td></tr>`);
        tr.querySelector('.star').onclick = () => togglePin(s.id);
        tr.oncontextmenu = e => ctxMenu(e.clientX, e.clientY, [{ label: S.pinned.includes(s.id) ? 'Unpin graph' : 'Pin graph', click: () => togglePin(s.id) }, { label: 'Copy identifier', click: () => navigator.clipboard.writeText(s.id) }, { label: 'Copy value', click: () => navigator.clipboard.writeText(F.sensor(s.t, St.sens.get(s.id).v)) }]);
        tb.appendChild(tr); rows.set(s.id, { tr, v: tr.querySelector('.v'), mn: tr.querySelector('.mn'), mx: tr.querySelector('.mx'), cv: tr.querySelector('canvas'), t: s.t });
        io.observe(tr);
      }
    }
  }
  function togglePin(id) { S.pinned = S.pinned.includes(id) ? S.pinned.filter(x => x !== id) : [...S.pinned, id]; saveSettings(); sig = ''; pinSig = ''; tick(); }
  function pins() {
    const ps = S.pinned.filter(id => St.sens.has(id)); const s2 = ps.join('|');
    if (s2 !== pinSig) {
      pinSig = s2; const el = $('#se-pinned');
      el.innerHTML = ps.map((id, i) => { const s = St.sens.get(id); const hw = St.hw.get(s.hw); return `<div class="card"><div class="ch">${esc(s.n)}<span class="sub">${esc(shortHw(hw))}</span><span class="r mono" id="pinv-${i}"></span><button class="tbtn" data-unpin="${esc(id)}" title="Unpin">&#xE711;</button></div><div class="chartbox short"><canvas></canvas></div></div>`; }).join('');
      el.style.marginBottom = ps.length ? '12px' : '0';
      $$('[data-unpin]', el).forEach(b => b.onclick = () => togglePin(b.dataset.unpin));
      pinCharts = ps.map((id, i) => { const s = St.sens.get(id); return new LineChart($$('#se-pinned canvas')[i], { series: [{ ring: ring(H.sens, id), color: s.t === 'Temperature' ? '--c-temp' : s.t === 'Fan' ? '--c-gpu' : s.t === 'Power' ? '--accent2' : '--c-cpu', label: s.n }], max: typeMax(s.t) || 'auto', fmt: v => F.sensor(s.t, v) }); });
    }
    ps.forEach((id, i) => { const s = St.sens.get(id); const e = $('#pinv-' + i); if (e) e.textContent = F.sensor(s.t, s.v); });
    pinCharts.forEach(c => c.draw());
  }
  function tick() {
    if (!St.tick.hw) { $('#se-table tbody').innerHTML = `<tr><td colspan="7" class="muted" style="padding:20px">Hardware sensors unavailable${St.lhm ? '' : ' — LibreHardwareMonitor failed to open (see xenomon.log). Admin + PawnIO driver are required for CPU/motherboard sensors.'}</td></tr>`; return; }
    if (!$('#se-types').children.length) chips();
    const s2 = St.hwVer + '|' + $('#se-filter').value + '|' + S.hiddenSensorTypes.join(',') + '|' + S.pinned.join(',');
    if (s2 !== sig) { build(visible()); sig = s2; }
    for (const [id, r] of rows) {
      const s = St.sens.get(id); if (!s) continue;
      const v = F.sensor(s.t, s.v); if (r.v.textContent !== v) { r.v.textContent = v; if (s.t === 'Temperature') r.v.style.color = tempColor(s.v); }
      const mn = F.sensor(s.t, s.mn), mx = F.sensor(s.t, s.mx); if (r.mn.textContent !== mn) r.mn.textContent = mn; if (r.mx.textContent !== mx) r.mx.textContent = mx;
      if (vis.has(id)) spark(r.cv, ring(H.sens, id), s.t === 'Temperature' ? '--c-temp' : '--c-cpu', typeMax(s.t), false);
    }
    pins();
  }
  $('#se-filter').addEventListener('input', debounce(() => { sig = ''; tick(); }, 150));
  $('#se-reset').onclick = () => Host.call('resetMinMax').then(() => toast('Min/max reset'));
  $('#se-report').onclick = () => Host.call('hwReport').then(f => toast('Report: ' + f));
  $('#se-log').onclick = toggleLog;
  return { tick, show() { sig = ''; pinSig = ''; } };
})();

/* ================= NETWORK ================= */
Views.network = (() => {
  let cards = new Map(), cardSig = '', conns = [], lastFetch = 0, fetching = false, sort = { k: 'proc', asc: true }, dns = new Map(), nf = { listen: true, loop: false, udp: true };
  let pingSig = '', pingCharts = [], connHtml = '', curList = [];
  const COLS = [['proc', 'Process'], ['pid', 'PID'], ['proto', 'Proto'], ['laddr', 'Local address'], ['lport', 'L-port'], ['raddr', 'Remote address'], ['rport', 'R-port'], ['state', 'State'], ['host', 'Remote host']];
  function head() {
    $('#nw-table thead').innerHTML = '<tr>' + COLS.map(([k, l]) => `<th data-k="${k}" class="${sort.k === k ? 'sorted' + (sort.asc ? ' asc' : '') : ''}">${l}</th>`).join('') + '</tr>';
    $$('#nw-table th').forEach(th => th.onclick = () => { sort.k === th.dataset.k ? sort.asc = !sort.asc : (sort = { k: th.dataset.k, asc: true }); head(); renderConns(); });
  }
  async function fetchConns() {
    if (fetching) return; fetching = true;
    try { conns = await Host.call('connections'); lastFetch = Date.now(); renderConns(); } catch (e) { toast(e.message, 'error'); }
    fetching = false;
  }
  const isLoop = a => a.startsWith('127.') || a === '::1';
  const isPriv = a => /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|169\.254\.|fe80|fc|fd|0\.0\.0\.0|::$|\*)/i.test(a) || isLoop(a);
  function renderConns() {
    const q = $('#nw-filter').value.trim().toLowerCase();
    let list = conns.map(c => Object.assign({}, c, { proc: (St.byPid.get(c.pid) || [0, 0, c.pid === 0 ? 'System Idle' : c.pid === 4 ? 'System' : '?'])[2], host: dns.get(c.raddr) || '' }));
    list = list.filter(c => (nf.listen || c.state !== 'LISTEN') && (nf.loop || !(isLoop(c.laddr) && (isLoop(c.raddr) || c.raddr === '*' || c.raddr === '0.0.0.0'))) && (nf.udp || !c.proto.startsWith('UDP')));
    if (q) list = list.filter(c => [c.proc, c.pid, c.laddr, c.lport, c.raddr, c.rport, c.state, c.host, c.proto].some(x => String(x).toLowerCase().includes(q)));
    const d = sort.asc ? 1 : -1; list.sort((a, b) => (a[sort.k] < b[sort.k] ? -1 : a[sort.k] > b[sort.k] ? 1 : 0) * d || a.pid - b.pid);
    $('#nw-count').textContent = `${list.length} / ${conns.length} sockets · ${conns.filter(c => c.state === 'ESTABLISHED').length} established`;
    const tb = $('#nw-table tbody'); curList = list;
    const html = list.slice(0, 3000).map((c, i) => `<tr data-i="${i}"><td><span class="nmc">${Icons.img(Meta.get(c.pid).path)}<span>${esc(c.proc)}</span></span></td><td class="num">${c.pid}</td><td>${c.proto}</td><td class="num">${esc(c.laddr)}</td><td class="num">${c.lport}</td><td class="num">${esc(c.raddr)}</td><td class="num">${c.rport || ''}</td>
      <td>${c.state ? `<span class="pill ${c.state === 'ESTABLISHED' ? 'ok' : c.state === 'LISTEN' ? 'acc' : c.state.includes('WAIT') ? 'warn' : ''}">${c.state}</span>` : ''}</td><td class="muted">${esc(c.host)}</td></tr>`).join('');
    if (html !== connHtml) { connHtml = html; tb.innerHTML = html; }     // most refreshes change nothing
    Meta.want([...new Set(list.map(c => c.pid))]);
    // auto-resolve a few public remotes
    let n = 0; for (const c of list) { if (n > 6) break; if (c.state === 'ESTABLISHED' && !isPriv(c.raddr) && !dns.has(c.raddr)) { resolve(c.raddr); n++; } }
  }
  function connMenu(e) {
    const tr = e.target.closest('tr'); if (!tr) return; const c = curList[+tr.dataset.i]; if (!c) return;
    ctxMenu(e.clientX, e.clientY, [
      { label: 'Go to process', click: () => { setView('procs'); Views.procs.select(c.pid); } },
      { label: 'End process', danger: 1, click: () => { setView('procs'); Views.procs.select(c.pid); Views.procs.key({ key: 'Delete', shiftKey: false, preventDefault() { } }); } }, '-',
      { label: 'Resolve remote host', disabled: c.raddr === '*', click: () => resolve(c.raddr, true) },
      { label: 'Look up IP (ipinfo.io)', disabled: isPriv(c.raddr), click: () => Host.call('openUrl', { url: 'https://ipinfo.io/' + c.raddr }) },
      { label: 'Copy remote endpoint', click: () => navigator.clipboard.writeText(`${c.raddr}:${c.rport}`) },
      { label: 'Copy row', click: () => navigator.clipboard.writeText(`${c.proc} ${c.pid} ${c.proto} ${c.laddr}:${c.lport} -> ${c.raddr}:${c.rport} ${c.state}`) },
      { label: 'Ping remote (add latency target)', disabled: c.raddr === '*' || isLoop(c.raddr), click: () => addPingTarget(c.raddr) }]);
  }
  function resolve(ip, loud) {
    if (dns.has(ip) && !loud) return; dns.set(ip, '…');
    Host.call('dnsLookup', { ip }).then(name => { dns.set(ip, name || '(no PTR)'); if (loud) toast(`${ip} → ${name || 'no PTR record'}`); if (St.view === 'network') renderConns(); });
  }
  function tickCards() {
    const t = St.tick; const sig = (t.net || []).map(n => n.id).join('|');
    if (sig !== cardSig) {
      cardSig = sig; cards.clear(); const el = $('#nw-cards');
      el.innerHTML = (t.net || []).map((n, i) => `<div class="card"><div class="ch">${esc(n.name)}<span class="sub">${esc(n.desc)}</span><span class="r mono" id="nwv-${i}"></span></div><div class="chartbox short"><canvas></canvas></div><div class="legend" id="nwl-${i}"></div></div>`).join('');
      (t.net || []).forEach((n, i) => { const x = H.nic.get(n.id); cards.set(n.id, { i, chart: new LineChart($$('#nw-cards canvas')[i], { series: [{ ring: x.down, color: '--c-down', label: 'recv', fmt: F.bits }, { ring: x.up, color: '--c-up', label: 'send', fill: false, fmt: F.bits }], max: 'auto', floor: 125000, fmt: v => F.bits(v).replace('/s', '') }) }); });
    }
    (t.net || []).forEach(n => {
      const c = cards.get(n.id); if (!c) return; c.chart.draw();
      setHTML($('#nwv-' + c.i), `<span style="color:${col('--c-down')}">↓ ${F.bits(n.down)}</span> <span style="color:${col('--c-up')}">↑ ${F.bits(n.up)}</span>`);
      setHTML($('#nwl-' + c.i), `<span>${esc(n.ips.filter(a => !a.includes(':')).join(', '))}</span><span>gw ${esc(n.gw.join(', ') || '—')}</span><span>${n.speed > 0 ? F.bits(n.speed / 8).replace('/s', 'ps') : ''}</span><span>Σ ↓${F.bytes(n.rx, 1)} ↑${F.bytes(n.tx, 1)}</span>`);
    });
    $('#nw-sub').textContent = `↓ ${F.bits(D.down)}  ↑ ${F.bits(D.up)}`;
  }
  function tickPing() {
    const ps = D.ping; const el = $('#nw-ping'); const sig = ps.map(p => p.host).join('|');
    if (sig !== pingSig) {
      pingSig = sig;
      el.innerHTML = ps.map((p, i) => `<div class="card"><div class="ch">${esc(p.auto ? 'Gateway' : p.label)}<span class="sub" id="png-s${i}">${esc(p.host)}</span><span class="r mono" id="png-v${i}"></span></div><div class="chartbox short"><canvas></canvas></div><div class="legend" id="png-l${i}"></div></div>`).join('')
        || '<div class="muted" style="padding:4px 2px">No latency targets — add your gateway, servers or a game host with <b>Latency targets…</b></div>';
      pingCharts = ps.map((p, i) => new LineChart($$('#nw-ping canvas')[i], { series: [{ ring: (H.ping.get(p.host) || { ring: new Ring(S.history) }).ring, color: '--c-down', label: 'rtt', fmt: v => v.toFixed(0) + ' ms' }], max: 'auto', floor: 10, fmt: v => v.toFixed(0) + 'ms' }));
    }
    ps.forEach((p, i) => {
      pingCharts[i] && pingCharts[i].draw();
      setHTML($('#png-s' + i), esc(p.host) + (p.addr ? ' → ' + esc(p.addr) : ''));
      setHTML($('#png-v' + i), p.n ? `<span style="color:${pingColor(p.ms)}">${p.ms >= 0 ? p.ms + ' ms' : 'timeout'}</span>` : '<span class="muted">…</span>');
      setHTML($('#png-l' + i), `<span>avg ${p.avg ?? '—'} ms</span><span>min ${p.min ?? '—'} · max ${p.max ?? '—'}</span><span>jitter ${p.jitter ?? '—'} ms</span><span style="color:${p.loss > 0 ? 'var(--err)' : 'inherit'}">loss ${p.loss}%</span><span class="muted">${p.n} pings</span>`);
    });
  }
  function tick() { tickPing(); tickCards(); if (Date.now() - lastFetch > 2500) fetchConns(); }
  head();
  $('#nw-table tbody').addEventListener('contextmenu', connMenu);
  $('#nw-ping-edit').onclick = () => editPingTargets();
  $('#nw-filter').addEventListener('input', debounce(renderConns, 150));
  $$('[data-nf]').forEach(c => c.onclick = () => { nf[c.dataset.nf] = !nf[c.dataset.nf]; c.classList.toggle('on', nf[c.dataset.nf]); renderConns(); });
  return { tick, refresh: fetchConns, show() { lastFetch = 0; } };
})();

/* ================= DISKS ================= */
Views.disks = (() => {
  let sig = '', charts = [];
  function tick() {
    const t = St.tick; const drives = t.drives || [];
    const s2 = drives.map(d => d.name).join() + '|' + D.disks.map(d => d.id).join();
    if (s2 !== sig) {
      sig = s2; charts = [];
      $('#dk-body').innerHTML = `<div class="card"><div class="ch">All disks<span class="sub">latency · queue · IOPS (PhysicalDisk _Total)</span></div><div class="stats" id="dk-lat" style="grid-template-columns:repeat(5,minmax(0,1fr));margin-top:0"></div></div>
        <div class="card sec-gap"><div class="ch">Volumes</div><div id="dk-vols"></div></div>
        <div class="grid sec-gap" style="grid-template-columns:repeat(auto-fill,minmax(420px,1fr))">${D.disks.map((d, i) => `<div class="card"><div class="ch">${esc(d.name)}<span class="r mono" id="dkh-${i}"></span></div>
          <div class="chartbox short"><canvas id="dkc-${i}"></canvas></div><div class="stats" id="dks-${i}" style="grid-template-columns:repeat(4,minmax(0,1fr))"></div></div>`).join('')}</div>
        ${D.disks.length ? '' : `<div class="card sec-gap"><div class="ch">All physical disks<span class="sub">perf counters — per-disk SMART/temps need admin</span><span class="r mono" id="dk-allv"></span></div><div class="chartbox"><canvas id="dk-all"></canvas></div><div class="legend"><span><i style="background:${col('--c-disk')}"></i>read</span><span><i style="background:${col('--c-up')}"></i>write</span></div></div>`}`;
      if (!D.disks.length) charts.push(new LineChart($('#dk-all'), { series: [{ ring: H.dr, color: '--c-disk', label: 'read', fmt: F.rate }, { ring: H.dw, color: '--c-up', label: 'write', fill: false, fmt: F.rate }], max: 'auto', floor: 1048576, fmt: v => F.bytes(v, 0) }));
      D.disks.forEach((d, i) => {
        const R = (type, ...n) => ring(H.sens, (pick(d.sensors, type, ...n) || {}).id || 'none');
        charts.push(new LineChart($('#dkc-' + i), { series: [{ ring: R('Throughput', 'Read Rate', /read/i), color: '--c-disk', label: 'read', fmt: F.rate }, { ring: R('Throughput', 'Write Rate', /write/i), color: '--c-up', label: 'write', fill: false, fmt: F.rate }], max: 'auto', floor: 1048576, fmt: v => F.bytes(v, 0) }));
      });
    }
    const y = D.sys;
    setHTML($('#dk-lat'), [['Read latency', y.dlr != null ? y.dlr.toFixed(2) + ' ms' : '—'], ['Write latency', y.dlw != null ? y.dlw.toFixed(2) + ' ms' : '—'], ['Queue depth', y.dq ?? '—'], ['IOPS', y.diops != null ? F.num(Math.round(y.diops)) : '—'], ['Active', D.diskAct != null ? D.diskAct.toFixed(0) + '%' : '—']]
      .map(([k, v]) => `<div class="stat"><div class="sk">${k}</div><div class="sv s">${v}</div></div>`).join(''));
    setHTML($('#dk-vols'), drives.map(d => {
      const used = d.total - d.free, p = used / d.total * 100;
      return `<div class="mrow" style="grid-template-columns:260px minmax(0,1fr) 260px"><span class="mn"><b style="color:var(--fg)">${esc(d.name)}</b> ${esc(d.label || '')} <em>${esc(d.fmt)} · ${esc(d.type)}</em></span>
        <span class="meter" style="height:10px"><i style="width:${p}%;background:${p > 90 ? '#f14c4c' : p > 75 ? '#cca700' : col('--c-disk')}"></i></span><span class="mv">${F.bytes(d.free, 1)} free of ${F.bytes(d.total, 1)} (${p.toFixed(0)}%)</span></div>`;
    }).join(''));
    if (!D.disks.length) { charts.forEach(c => c.draw()); const e = $('#dk-allv'); if (e) e.textContent = `R ${F.rate(D.diskR)} · W ${F.rate(D.diskW)}${D.diskAct != null ? ' · ' + D.diskAct.toFixed(0) + '% active' : ''}`; }
    D.disks.forEach((d, i) => {
      charts[i] && charts[i].draw();
      setHTML($('#dkh-' + i), `${d.act != null ? d.act.toFixed(0) + '% active · ' : ''}<span style="color:${tempColor(d.temp)}">${F.temp(d.temp)}</span>`);
      setHTML($('#dks-' + i), [['Read', F.rate(d.r)], ['Write', F.rate(d.w)], ['Life', d.life != null ? d.life.toFixed(0) + '%' : '—'], ['Used', F.pct(d.used, 0)], ['Data read', d.readTot != null ? F.sensor('Data', d.readTot) : '—'], ['Data written', d.writeTot != null ? F.sensor('Data', d.writeTot) : '—'], ['Spare', d.spare != null ? d.spare.toFixed(0) + '%' : '—'], ['Temp', F.temp(d.temp)]]
        .map(([k, v]) => `<div class="stat"><div class="sk">${k}</div><div class="sv s">${v}</div></div>`).join(''));
    });
  }
  return { tick, show() { sig = ''; } };
})();

/* ================= SERVICES ================= */
Views.services = (() => {
  let data = [], sf = 'all', sort = { k: 'display', asc: true }, loaded = false;
  const COLS = [['display', 'Display name'], ['name', 'Name'], ['state', 'Status'], ['start', 'Startup'], ['pid', 'PID'], ['account', 'Log on as'], ['desc', 'Description']];
  async function load() {
    $('#sv-count').textContent = 'loading…';
    try { data = await Host.call('services'); loaded = true; render(); } catch (e) { toast(e.message, 'error'); }
  }
  function render() {
    $('#sv-table thead').innerHTML = '<tr>' + COLS.map(([k, l]) => `<th data-k="${k}" class="${sort.k === k ? 'sorted' + (sort.asc ? ' asc' : '') : ''}">${l}</th>`).join('') + '</tr>';
    $$('#sv-table th').forEach(th => th.onclick = () => { sort.k === th.dataset.k ? sort.asc = !sort.asc : (sort = { k: th.dataset.k, asc: true }); render(); });
    const q = $('#sv-filter').value.trim().toLowerCase();
    let list = data.filter(s => (sf === 'all' || s.state === sf) && (!q || [s.name, s.display, s.desc, s.path, s.pid].some(x => String(x || '').toLowerCase().includes(q))));
    const d = sort.asc ? 1 : -1; list.sort((a, b) => (String(a[sort.k]).toLowerCase() < String(b[sort.k]).toLowerCase() ? -1 : 1) * d);
    $('#sv-count').textContent = `${list.length} services · ${data.filter(s => s.state === 'Running').length} running`;
    $('#sv-table tbody').innerHTML = list.map((s, i) => `<tr data-i="${i}"><td>${esc(s.display)}</td><td class="mono">${esc(s.name)}</td><td><span class="pill ${s.state === 'Running' ? 'ok' : s.state === 'Stopped' ? '' : 'warn'}">${esc(s.state)}</span></td>
      <td>${esc(s.start)}</td><td class="num">${s.pid || ''}</td><td class="muted">${esc(s.account || '')}</td><td class="muted" title="${esc(s.desc)}">${esc(s.desc || '')}</td></tr>`).join('');
    $$('#sv-table tbody tr').forEach(tr => {
      const s = list[+tr.dataset.i];
      tr.oncontextmenu = e => ctxMenu(e.clientX, e.clientY, [
        { label: 'Start', disabled: s.state === 'Running', click: () => act(s, 'start') },
        { label: 'Stop', disabled: s.state === 'Stopped', danger: 1, click: () => act(s, 'stop') },
        { label: 'Restart', disabled: s.state !== 'Running', click: () => act(s, 'restart') }, '-',
        { label: 'Go to process', disabled: !s.pid, click: () => { setView('procs'); Views.procs.select(s.pid); } },
        { label: 'Open file location', disabled: !s.path, click: () => { const m = /^"([^"]+)"|^(\S+)/.exec(s.path); Host.call('openLocation', { path: m[1] || m[2] }); } },
        { label: 'Search online', click: () => Host.call('openUrl', { url: 'https://www.google.com/search?q=' + encodeURIComponent(s.name + ' service ' + s.display) }) },
        { label: 'Copy service name', click: () => navigator.clipboard.writeText(s.name) },
        { label: 'Copy image path', disabled: !s.path, click: () => navigator.clipboard.writeText(s.path) }]);
      tr.ondblclick = () => modal({ title: s.display, body: `Name: ${s.name}\nStatus: ${s.state}\nStartup: ${s.start}\nPID: ${s.pid || '-'}\nLog on as: ${s.account || '-'}\nType: ${s.type}\n\nPath:\n${s.path || '-'}\n\n${s.desc || ''}` });
    });
  }
  async function act(s, a) {
    if (a === 'stop' && !(await confirmBox(`Stop ${s.display}?`, 'Dependent services may also stop.', 'Stop', 'danger'))) return;
    toast(`${a} ${s.name}…`);
    try { const r = await Host.call('serviceAction', { name: s.name, action: a }); toast(`${s.display}: ${r}`); load(); } catch (e) { toast(`${s.name}: ${e.message}`, 'error'); }
  }
  $('#sv-filter').addEventListener('input', debounce(render, 120));
  $('#sv-refresh').onclick = load;
  $$('[data-sf]').forEach(c => c.onclick = () => { sf = c.dataset.sf; $$('[data-sf]').forEach(x => x.classList.toggle('on', x === c)); render(); });
  return { show() { if (!loaded) load(); }, refresh: load };
})();

/* ================= STARTUP ================= */
Views.startup = (() => {
  let data = [], loaded = false;
  async function load() {
    try { data = await Host.call('startup'); loaded = true; render(); } catch (e) { toast(e.message, 'error'); }
  }
  function render() {
    $('#su-table thead').innerHTML = '<tr><th>Enabled</th><th>Name</th><th>Scope</th><th>Command</th><th>Source</th></tr>';
    $('#su-count').textContent = `${data.length} items · ${data.filter(d => d.enabled).length} enabled`;
    $('#su-table tbody').innerHTML = data.map((d, i) => `<tr data-i="${i}"><td><span class="switch ${d.enabled ? 'on' : ''}"></span></td><td><span class="nmc">${Icons.img(d.exe)}<span>${esc(d.name)}</span></span></td><td>${esc(d.scope)}</td><td class="mono" title="${esc(d.cmd)}">${esc(d.cmd)}</td><td class="muted">${esc(d.source)}</td></tr>`).join('');
    $$('#su-table tbody tr').forEach(tr => {
      const d = data[+tr.dataset.i];
      tr.querySelector('.switch').onclick = () => toggle(d);
      tr.oncontextmenu = e => ctxMenu(e.clientX, e.clientY, [
        { label: d.enabled ? 'Disable' : 'Enable', click: () => toggle(d) },
        { label: 'Open file location', disabled: !d.exe, click: () => Host.call('openLocation', { path: d.exe }) },
        { label: 'Search online', click: () => Host.call('openUrl', { url: 'https://www.google.com/search?q=' + encodeURIComponent(d.name + ' startup') }) },
        { label: 'Copy command', click: () => navigator.clipboard.writeText(d.cmd || '') }]);
    });
  }
  async function toggle(d) {
    try { const r = await Host.call('startupToggle', Object.assign({}, d, { enable: !d.enabled })); d.enabled = r.enabled; toast(`${d.name} ${r.enabled ? 'enabled' : 'disabled'}${r.backup ? ' · backup saved' : ''}`); render(); }
    catch (e) { toast(e.message, 'error'); }
  }
  document.addEventListener('icons', () => { if (St.view === 'startup' && loaded) render(); });
  $('#su-refresh').onclick = load;
  return { show() { if (!loaded) load(); }, refresh: load };
})();

/* ================= SYSTEM ================= */
Views.system = (() => {
  let info = null, text = '';
  async function load() {
    try { info = await Host.call('sysInfo'); render(); } catch (e) { $('#sy-body').innerHTML = `<div class="muted">${esc(e.message)}</div>`; }
  }
  const dt = s => { const m = /^(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})/.exec(s || ''); return m ? `${m[1]}-${m[2]}-${m[3]} ${m[4]}:${m[5]}` : (s || ''); };
  function render() {
    const o = info.os[0] || {}, cs = info.cs[0] || {}, b = info.board[0] || {}, bi = info.bios[0] || {}, c = info.cpu[0] || {};
    const sections = [
      ['Operating system', [['Edition', o.Caption], ['Version', `${o.Version} (build ${o.BuildNumber})`], ['Architecture', o.OSArchitecture], ['Installed', dt(o.InstallDate)], ['Last boot', dt(o.LastBootUpTime)], ['Computer name', info.machine], ['User', cs.UserName], ['Domain / workgroup', cs.Domain], ['.NET runtime', info.dotnet]]],
      ['Processor', [['Model', (c.Name || '').trim()], ['Cores / threads', `${c.NumberOfCores} / ${c.NumberOfLogicalProcessors}`], ['Base clock', c.MaxClockSpeed + ' MHz'], ['L2 / L3', `${F.bytes(c.L2CacheSize * 1024, 0)} / ${F.bytes(c.L3CacheSize * 1024, 0)}`], ['Socket', c.SocketDesignation], ['Virtualization (FW)', c.VirtualizationFirmwareEnabled ? 'enabled' : 'disabled']]],
      ['Motherboard & BIOS', [['Board', `${b.Manufacturer || ''} ${b.Product || ''}`], ['Revision', b.Version], ['System', `${cs.Manufacturer || ''} ${cs.Model || ''}`], ['BIOS', `${bi.Manufacturer || ''} ${bi.SMBIOSBIOSVersion || ''}`], ['BIOS date', dt(bi.ReleaseDate)]]],
      ['Memory', [['Installed', F.bytes(+cs.TotalPhysicalMemory, 1)], ...info.ram.map(m => [m.DeviceLocator || m.BankLabel, `${F.bytes(+m.Capacity, 0)} · ${m.ConfiguredClockSpeed || m.Speed} MT/s · ${(m.Manufacturer || '').trim()} ${(m.PartNumber || '').trim()}`])]],
      ['Graphics', info.gpu.flatMap((g, i) => [[`GPU ${i}`, g.Name], ['  driver', `${g.DriverVersion} (${dt(g.DriverDate).slice(0, 10)})`], ['  mode', `${g.VideoModeDescription || '—'} @ ${g.CurrentRefreshRate || '?'} Hz`]])],
      ['Storage', info.disk.map(d => [d.Model, `${F.bytes(+d.Size, 0)} · ${d.InterfaceType || ''} · ${d.MediaType || ''} · fw ${(d.FirmwareRevision || '').trim()}`])],
      ['Network adapters', info.net.map(n => [n.NetConnectionID || n.Name, `${n.Name} · ${n.MACAddress || ''}${n.Speed ? ' · ' + F.bits(n.Speed / 8).replace('/s', 'ps') : ''}`])],
      ['Audio', info.sound.map(s => [s.Name, s.Manufacturer])],
      ['Sensor hardware (LibreHardwareMonitor)', [...St.hw.values()].map(x => [x.type, x.name])],
    ];
    text = sections.map(([t, kv]) => `== ${t} ==\n` + kv.map(([k, v]) => `${k}: ${v ?? ''}`).join('\n')).join('\n\n');
    $('#sy-body').innerHTML = `<div class="grid" style="grid-template-columns:repeat(auto-fill,minmax(480px,1fr))">` + sections.map(([t, kv]) => `<div class="card"><div class="ch">${esc(t)}</div><div class="kv">${kv.map(([k, v]) => `<span class="k">${esc(k)}</span><span class="v" title="${esc(v)}">${esc(v ?? '—')}</span>`).join('')}</div></div>`).join('') + '</div>';
  }
  $('#sy-copy').onclick = () => { navigator.clipboard.writeText(text); toast('System info copied'); };
  return { show() { if (!info) load(); }, refresh: load };
})();

/* ================= SETTINGS ================= */
Views.settings = (() => {
  function render() {
    const sel = (id, opts, cur) => `<select id="${id}">${opts.map(([v, l]) => `<option value="${v}" ${String(v) === String(cur) ? 'selected' : ''}>${l}</option>`).join('')}</select>`;
    const sw = (id, on) => `<span class="switch ${on ? 'on' : ''}" id="${id}"></span>`;
    $('#st-body').innerHTML = `<div class="set">
      <h4>Sampling</h4>
      <span class="sl">Sample interval<small>How often everything is polled</small></span>${sel('set-int', [[250, '250 ms'], [500, '500 ms'], [1000, '1 s'], [2000, '2 s'], [5000, '5 s']], S.interval)}
      <span class="sl">History length<small>Samples kept per graph</small></span>${sel('set-hist', [[60, '60'], [120, '120'], [300, '300'], [600, '600'], [1800, '1800']], S.history)}
      <h4>Display</h4>
      <span class="sl">Theme</span>${sel('set-theme', [['dark', 'Dark'], ['light', 'Light']], S.theme)}
      <span class="sl">Temperature unit</span>${sel('set-unit', [['C', '°C'], ['F', '°F']], S.tempUnit)}
      <span class="sl">Always on top</span>${sw('set-top', S.topmost)}
      <span class="sl">Start on view</span>${sel('set-start', VIEW_KEYS.map(v => [v, v]), S.startView)}
      <span class="sl">Latency targets<small>Pinged every 2 s while Monitor is showing (gateway is automatic)</small></span><span><button class="btn sm sec" id="set-ping">Edit…</button></span>
      <h4>Alerts</h4>
      <span class="sl">Threshold alerts<small>Toast + red status bar</small></span>${sw('set-al', S.alerts.on)}
      <span class="sl">CPU temperature above (°C)</span><input type="number" id="al-ct" value="${S.alerts.cpuTemp}">
      <span class="sl">GPU temperature above (°C)</span><input type="number" id="al-gt" value="${S.alerts.gpuTemp}">
      <span class="sl">RAM usage above (%)</span><input type="number" id="al-ram" value="${S.alerts.ram}">
      <span class="sl">CPU load above, 10 s avg (%)<small>0 = off</small></span><input type="number" id="al-cl" value="${S.alerts.cpuLoad}">
      <h4>Environment</h4>
      <span class="sl">Privileges</span><span>${St.admin ? '<span class="pill ok">Administrator</span>' : '<span class="pill warn">Standard user — limited sensors</span>'}</span>
      <span class="sl">Hardware sensors</span><span>${St.lhm ? '<span class="pill ok">LibreHardwareMonitor online</span>' : '<span class="pill bad">offline</span>'} <span class="muted mono">${St.hw.size} devices · ${St.sens.size} sensors</span></span>
      <span class="sl">Data folder<small>logs, snapshots, reports, registry backups</small></span><span><button class="btn sm sec" id="set-open">Open data folder</button></span>
      <span class="sl">Reset</span><span><button class="btn sm sec" id="set-reset">Reset all settings</button></span>
    </div>`;
    $('#set-int').onchange = e => setInterval_(+e.target.value);
    $('#set-hist').onchange = e => resizeHistory(+e.target.value);
    $('#set-theme').onchange = e => setTheme(e.target.value);
    $('#set-unit').onchange = e => { S.tempUnit = e.target.value; saveSettings(); };
    $('#set-start').onchange = e => { S.startView = e.target.value; saveSettings(); };
    $('#set-top').onclick = () => { setTopmost(!S.topmost); render(); };
    $('#set-al').onclick = () => { S.alerts.on = !S.alerts.on; saveSettings(); render(); };
    [['al-ct', 'cpuTemp'], ['al-gt', 'gpuTemp'], ['al-ram', 'ram'], ['al-cl', 'cpuLoad']].forEach(([id, k]) => $('#' + id).onchange = e => { S.alerts[k] = +e.target.value; saveSettings(); });
    $('#set-open').onclick = () => Host.call('openDataDir');
    $('#set-ping').onclick = () => editPingTargets();
    $('#set-reset').onclick = async () => { if (await confirmBox('Reset all settings?', 'Pinned sensors, filters, thresholds and layout go back to defaults.', 'Reset', 'danger')) { await Host.call('saveSettings', { json: '{}' }); location.reload(); } };
  }
  return { show: render };
})();

/* ================= LATENCY TARGETS ================= */
async function editPingTargets() {
  let cur = []; try { cur = await Host.call('pingTargets'); } catch { }
  const v = await modal({ title: 'Latency targets', body: 'Hosts or IPs to ping every 2 s, comma separated (max 8). The default gateway is always included.\nGood picks: your server rack boxes, NAS, router, 1.1.1.1, a game server.', input: 'e.g. 1.1.1.1, 192.168.1.10, nas.local', value: (cur || []).join(', '), buttons: [{ label: 'Cancel', value: null }, { label: 'Save', value: true }] });
  if (v == null) return;
  try { const r = await Host.call('setPingTargets', { hosts: String(v).split(/[,\s]+/).filter(Boolean) }); toast('Latency targets: ' + (r.join(', ') || 'gateway only')); } catch (e) { toast(e.message, 'error'); }
}
async function addPingTarget(host) {
  let cur = []; try { cur = await Host.call('pingTargets'); } catch { }
  if (cur.includes(host)) return toast(host + ' is already a latency target');
  try { await Host.call('setPingTargets', { hosts: [...cur, host] }); toast('Pinging ' + host + ' — see Network'); } catch (e) { toast(e.message, 'error'); }
}

/* ================= HEALTH ================= */
/* One on-demand scan: stability events, driver problems, pending restart, OS/boot facts, security at a glance,
   signed-in users and SMB shares/clients. Heavy (event logs + WMI), so it only runs when asked. */
Views.health = (() => {
  let data = null, loading = false, days = 7;
  const sevPill = c => !c.count ? 'ok' : c.sev === 'bad' ? 'bad' : c.sev === 'warn' ? 'warn' : 'acc';
  const sevColor = c => !c.count ? 'var(--ok)' : c.sev === 'bad' ? 'var(--err)' : c.sev === 'warn' ? 'var(--warn)' : 'var(--fg)';
  const ago = d => { const ms = Date.now() - new Date(d).getTime(); if (!(ms >= 0)) return ''; const m = ms / 60000; return m < 60 ? Math.round(m) + 'm ago' : m < 1440 ? Math.round(m / 60) + 'h ago' : Math.round(m / 1440) + 'd ago'; };
  const when = d => d ? new Date(d).toLocaleString('en-US', { hour12: false, month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';
  const pill = (cls, txt) => `<span class="pill ${cls}">${esc(txt)}</span>`;
  const onOff = (v, goodWhenOn = true) => v == null ? pill('', 'unknown') : pill(v === goodWhenOn ? 'ok' : 'bad', v ? 'on' : 'off');
  const err = o => o && !Array.isArray(o) && o.error ? `<div class="hrow">${pill('', '?')} <span class="muted">${esc(o.error)}</span></div>` : null;
  const kv = rows => rows.filter(Boolean).map(([k, v]) => `<span class="k">${esc(k)}</span><span class="v">${v}</span>`).join('');

  async function load() {
    if (loading) return; loading = true;
    $('#hl-sub').textContent = `scanning event logs, devices and WMI — last ${days} days…`; $('#hl-refresh').disabled = true;
    try { data = await Host.call('sysHealth', { days }); render(); }
    catch (e) { toast('Health scan failed: ' + e.message, 'error'); $('#hl-sub').textContent = 'scan failed'; }
    loading = false; $('#hl-refresh').disabled = false;
  }

  function render() {
    const d = data, ev = Array.isArray(d.events) ? d.events : [], E = k => ev.find(x => x.key === k) || { count: 0, latest: [] };
    const devs = Array.isArray(d.devices) ? d.devices.filter(x => x.code !== 22) : [], disabled = Array.isArray(d.devices) ? d.devices.filter(x => x.code === 22) : [];
    const rb = d.reboot || {}, os = d.os || {}, sec = d.security || {};
    $('#hl-sub').textContent = `last ${d.days} days · scanned in ${(d.ms / 1000).toFixed(1)} s${d.admin ? '' : ' · not elevated — some logs are hidden'}`;
    const tile = (label, val, sub, color, go, id) => `<div class="card tile" ${go ? `data-go="${go}"` : ''}><div class="tl">${label}</div><div class="tv" ${id ? `id="${id}"` : ''} style="color:${color}">${val}</div><div class="ts">${sub || ''}</div></div>`;
    const evTile = (k, label) => { const c = E(k); return tile(label, c.error ? '?' : c.count + (c.capped ? '+' : ''), c.count ? 'latest ' + ago(c.latest[0] && c.latest[0].time) : 'none in ' + d.days + 'd', sevColor(c), 'ev-' + k); };
    const app = E('app');
    $('#hl-tiles').innerHTML =
      tile('Uptime', F.dur(St.tick ? St.tick.up : os.uptimeMs || 0).replace(/:\d\d$/, ''), os.fastStartup ? '<span style="color:var(--warn)">fast startup on</span>' : 'since ' + when(os.boot), 'var(--fg)', 'hl-sys', 'hl-upv')
      + tile('Restart pending', rb.pending ? 'YES' : 'no', rb.pending ? esc(rb.reasons[0] || '') : 'nothing queued', rb.pending ? 'var(--warn)' : 'var(--ok)', 'hl-sys')
      + evTile('shutdown', 'Unexpected shutdowns') + evTile('bsod', 'Blue screens') + evTile('whea', 'WHEA hw errors') + evTile('disk', 'Disk / FS errors') + evTile('gpu', 'GPU driver resets')
      + tile('App crashes', app.count || 0, app.apps && app.apps[0] ? 'top: ' + esc(app.apps[0].name) : 'none in ' + d.days + 'd', app.count ? 'var(--fg)' : 'var(--ok)', 'hl-apps-card')
      + tile('Device problems', devs.length, devs.length ? esc(devs[0].name) : disabled.length ? disabled.length + ' disabled by you' : 'all drivers OK', devs.length ? 'var(--err)' : 'var(--ok)', 'hl-dev-card');
    $$('#hl-tiles .tile[data-go]').forEach(t => t.onclick = () => { const el = document.getElementById(t.dataset.go); if (!el) return; if (el.tagName === 'DETAILS') el.open = true; el.scrollIntoView({ behavior: 'smooth', block: 'center' }); });

    // stability events
    const wu = E('wu');
    $('#hl-events').innerHTML = (err(d.events) || '') + ev.filter(c => c.key !== 'app' && c.key !== 'wu').map(c => {
      if (c.error) return `<div class="hrow">${pill('', '?')} ${esc(c.title)} <span class="muted">— ${esc(c.error)}</span></div>`;
      if (!c.count) return `<div class="hrow">${pill('ok', '0')} ${esc(c.title)}</div>`;
      return `<details class="hev" id="ev-${c.key}"><summary>${pill(sevPill(c), c.count + (c.capped ? '+' : ''))} ${esc(c.title)} <span class="muted">· latest ${when(c.latest[0] && c.latest[0].time)}</span></summary>
        <div class="hint">${esc(c.hint)}</div>${c.latest.map(e => `<div class="hev-row"><span class="mono muted">${when(e.time)}</span><span class="mono">${esc(e.provider)} · ${e.id}</span><span class="hev-msg" title="${esc(e.msg)}">${esc(e.msg)}</span></div>`).join('')}</details>`;
    }).join('') + (wu.error ? '' : `<div class="hrow">${pill(wu.wuFailed ? 'warn' : 'acc', 'WU')} Windows Update: ${wu.wuLastOk ? 'last install ' + when(wu.wuLastOk) : 'no installs in ' + d.days + ' days'}${wu.wuFailed ? ` · <span style="color:var(--warn)">${wu.wuFailed} failed</span>` : ''}</div>`);

    // system + reboot + security
    $('#hl-sys').innerHTML = err(os) || kv([
      ['OS', esc(`${os.product || ''} ${os.version || ''}`) + ` <span class="muted">build ${esc(os.build || '')}</span>`],
      ['Installed', when(os.installed)], ['Last boot', when(os.boot) + (os.bootMs ? ` <span class="muted">· boot took ${(os.bootMs / 1000).toFixed(1)} s</span>` : '')],
      ['Fast startup', os.fastStartup == null ? '—' : os.fastStartup ? pill('warn', 'on') + ' <span class="muted">shutdown ≠ fresh boot; uptime keeps counting</span>' : pill('ok', 'off')],
      ['Power plan', esc(os.plan || '—')], ['Machine', esc(os.machine || '') + (os.domain ? ` <span class="muted">· ${esc(os.domain)}</span>` : '')],
    ]);
    $('#hl-reboot').innerHTML = err(rb) || (rb.pending ? rb.reasons.map(r => `<div class="hrow">${pill('warn', 'restart')} ${esc(r)}</div>`).join('') : `<div class="hrow">${pill('ok', 'OK')} No restart pending</div>`);
    const df = sec.defender, fw = sec.firewall || {}, otherAv = (sec.avs || []).some(a => a.on && !/defender/i.test(a.name));
    $('#hl-sec').innerHTML = err(sec) || kv([
      ['Firewall', `domain ${onOff(fw.domain)} private ${onOff(fw.priv)} public ${onOff(fw.pub)}`],
      df && (df.av || !otherAv) ? ['Defender', `real-time ${onOff(df.rtp)} · signatures ${df.sigAge > 3 ? pill('warn', df.sigAge + ' days old') : pill('ok', df.sigAge === 0 ? 'today' : df.sigAge + 'd old')} · tamper ${onOff(df.tamper)}`]
        : df ? ['Defender', `${pill('', 'passive')} <span class="muted">another antivirus is active</span>`] : null,
      ...(sec.avs || []).filter(a => !/defender/i.test(a.name)).map(a => ['Antivirus', `${esc(a.name)} ${onOff(a.on)} ${a.upToDate ? '' : pill('warn', 'out of date')}`]),
      ['Secure Boot', onOff(sec.secureBoot)], ['TPM', sec.tpm ? (sec.tpm.present ? `${onOff(sec.tpm.enabled)} <span class="muted">${esc(sec.tpm.spec || '')}</span>` : pill('warn', 'not found')) : '—'],
      ['UAC', onOff(sec.uac)], ['Remote Desktop', sec.rdp ? `${pill('acc', 'enabled')} NLA ${onOff(sec.rdpNla)}` : pill('ok', 'off')],
      sec.smb ? ['SMB server', `SMBv1 ${sec.smb.smb1 ? pill('bad', 'ON') : pill('ok', 'off')} · signing required ${onOff(sec.smb.signing)} · encryption ${onOff(sec.smb.encrypt)}`] : null,
    ]) + '<div class="hint" style="margin-top:6px">Full scan with fixes → <a href="#" id="hl-audit">Audit tab</a></div>';
    const au = $('#hl-audit'); if (au) au.onclick = e => { e.preventDefault(); try { chrome.webview.postMessage({ id: 0, method: 'suiteNav', args: { to: 'audit' } }); } catch { } };

    // app crashes
    const apps = app.apps || [];
    $('#hl-apps').innerHTML = app.error ? `<tbody><tr><td class="muted">${esc(app.error)}</td></tr></tbody>` : apps.length
      ? `<thead><tr><th>Program</th><th class="r">Crashes</th><th class="r">Hangs</th><th>Last</th></tr></thead><tbody>${apps.map(a => `<tr title="${esc(a.path || '')}"><td>${esc(a.name)}</td><td class="r num">${a.count - a.hangs}</td><td class="r num">${a.hangs}</td><td class="num muted">${when(a.last)}</td></tr>`).join('')}</tbody>`
      : `<tbody><tr><td class="muted">No app crashes or hangs in the last ${d.days} days.</td></tr></tbody>`;

    // devices
    $('#hl-dev').innerHTML = err(d.devices) || (devs.length ? devs.map(x => `<div class="hrow">${pill('bad', 'code ' + x.code)} <b>${esc(x.name)}</b> <span class="muted">${esc(x.cls || '')} · ${esc(x.meaning)}</span></div>`).join('') : `<div class="hrow">${pill('ok', 'OK')} Every present device reports a working driver.</div>`)
      + (disabled.length ? `<div class="hrow muted">${pill('', disabled.length)} disabled: ${disabled.map(x => esc(x.name)).join(', ')}</div>` : '');

    // sessions
    const ss = Array.isArray(d.sessions) ? d.sessions : [];
    $('#hl-sess').innerHTML = d.sessions && d.sessions.error ? `<tbody><tr><td class="muted">${esc(d.sessions.error)}</td></tr></tbody>` : ss.length
      ? `<thead><tr><th>User</th><th>State</th><th>Session</th><th>From</th><th>Logon</th><th class="r">Idle</th></tr></thead><tbody>${ss.map(x => `<tr><td>${esc(x.user)}</td><td>${pill(x.state === 'Active' ? 'ok' : x.state === 'Disconnected' ? 'warn' : '', x.state)}</td><td class="mono">${esc(x.station || '')} #${x.id}</td><td class="mono">${esc(x.client || (x.remote ? '?' : 'console'))}</td><td class="num muted">${when(x.logon)}</td><td class="r num">${x.idleMin != null ? x.idleMin + ' min' : ''}</td></tr>`).join('')}</tbody>`
      : '<tbody><tr><td class="muted">No interactive sessions.</td></tr></tbody>';

    // SMB
    const smb = d.smb || {};
    if (smb.error) $('#hl-smb').innerHTML = err(smb);
    else {
      const shares = (smb.shares || []).sort((a, b) => a.special - b.special);
      const conns = smb.conns || [];
      $('#hl-smb').innerHTML = `<div class="hsub">Shared from this PC</div>` + (shares.length ? shares.map(x => {
        const users = conns.filter(c => c.share === x.name);
        return `<div class="hrow ${x.special ? 'muted' : ''}">${pill(x.special ? '' : 'acc', x.kind)} <b>${esc(x.name)}</b> <span class="mono muted">${esc(x.path || '')}</span>${users.length ? ` · ${users.map(u => `${esc(u.user || '?')}@${esc(u.computer || '?')} (${u.files} files)`).join(', ')}` : ''}</div>`;
      }).join('') : '<div class="hrow muted">none</div>')
        + `<div class="hsub">Clients connected to this PC</div>` + ((smb.sessions || []).length ? smb.sessions.map(x => `<div class="hrow">${pill('ok', 'SMB')} <b>${esc(x.user || '?')}</b> from <span class="mono">${esc(x.computer || '?')}</span> <span class="muted">· ${x.open} open · active ${F.dur(x.active * 1000)} · idle ${F.dur(x.idle * 1000)}</span></div>`).join('') : '<div class="hrow muted">nobody</div>')
        + `<div class="hsub">Mapped drives (outgoing)</div>` + ((smb.mapped || []).length ? smb.mapped.map(x => `<div class="hrow">${pill(x.status === 'OK' ? 'ok' : 'warn', x.local || '—')} <span class="mono">${esc(x.remote)}</span> <span class="muted">${esc(x.state || '')}</span></div>`).join('') : '<div class="hrow muted">none</div>');
    }
  }

  function tick() { const e = $('#hl-upv'); if (e && St.tick) e.textContent = F.dur(St.tick.up).replace(/:\d\d$/, ''); }
  $('#hl-refresh').onclick = load;
  $$('[data-hd]').forEach(c => c.onclick = () => { days = +c.dataset.hd; $$('[data-hd]').forEach(x => x.classList.toggle('on', x === c)); load(); });
  return { show() { if (!data) load(); }, tick, refresh: load };
})();
