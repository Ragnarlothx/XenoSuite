/* XenoMon app shell: state, history, derived metrics, view switching, menus, status bar, overview, alerts, mini mode. */
'use strict';
const St = { tick: null, hw: new Map(), sens: new Map(), procs: [], byPid: new Map(), cores: 0, admin: false, view: 'overview', paused: false, lhm: true, info: {}, hwVer: 0, hwVerSeen: -1 };
const H = {};
const D = {};                 // derived metrics for the current tick
const Views = {};

function initHistory() {
  const n = S.history;
  H.cpu = new Ring(n); H.kernel = new Ring(n); H.core = []; H.mem = new Ring(n); H.commit = new Ring(n);
  H.down = new Ring(n); H.up = new Ring(n); H.dr = new Ring(n); H.dw = new Ring(n);
  H.nic = new Map(); H.sens = new Map(); H.proc = new Map(); H.ping = new Map(); H.cpuTemp = new Ring(n); H.cpuClock = new Ring(n); H.cpuPower = new Ring(n); H.memPct = new Ring(n); H.dact = new Ring(n);
}
function resizeHistory(n) {
  S.history = n;
  const all = [H.cpu, H.kernel, H.mem, H.memPct, H.dact, H.commit, H.down, H.up, H.dr, H.dw, H.cpuTemp, H.cpuClock, H.cpuPower, ...H.core];
  H.nic.forEach(x => { all.push(x.down, x.up); }); H.sens.forEach(r => all.push(r)); H.ping.forEach(x => all.push(x.ring));
  all.forEach(r => r.resize(n)); saveSettings();
}
const ring = (map, key, n) => { let r = map.get(key); if (!r) { r = new Ring(n || S.history); map.set(key, r); } return r; };

/* ---------- sensor lookup helpers ---------- */
function hwOf(type) { return [...St.hw.values()].filter(x => x.type === type || (Array.isArray(type) && type.includes(x.type))); }
let sensByHw = new Map(), sensByHwVer = -1;
function sensorsOf(hwId) {
  if (!St.tick || !St.tick.hw) return [];
  if (sensByHwVer !== St.hwVer) {            // sensor set only changes with a new meta version — index it once
    sensByHwVer = St.hwVer; sensByHw = new Map();
    const add = (k, s) => { let a = sensByHw.get(k); if (!a) sensByHw.set(k, a = []); a.push(s); };
    for (const s of St.tick.hw.sensors) { add(s.hw, s); const par = (St.hw.get(s.hw) || {}).parent; if (par) add(par, s); }
  }
  return sensByHw.get(hwId) || [];
}

/* The host sends the sensor table (ids/names/types) only when it changes, then a flat value array per tick.
   Sensor objects are reused and updated in place. */
const HwMeta = { ver: -1, hw: [], sensors: [], asked: 0 };
function expandHw(t) {
  const x = t.hw; if (!x) return;
  if (x.meta) {
    HwMeta.ver = x.m;
    HwMeta.hw = x.meta.hw.map(a => ({ id: a[0], name: a[1], type: a[2], parent: a[3] }));
    HwMeta.sensors = x.meta.s.map(a => ({ id: a[0], hw: a[1], t: a[2], n: a[3], ix: a[4], v: null, mn: null, mx: null }));
    St.hwVer++;
  } else if (x.m !== HwMeta.ver) {
    if (Date.now() - HwMeta.asked > 2000) { HwMeta.asked = Date.now(); Host.call('hwMeta').catch(() => { }); }
    t.hw = null; return;
  }
  const v = x.v, ss = HwMeta.sensors;
  for (let i = 0, j = 0; i < ss.length; i++, j += 3) { const s = ss[i]; s.v = v[j]; s.mn = v[j + 1]; s.mx = v[j + 2]; }
  t.hw = { hw: HwMeta.hw, sensors: ss };
}
function pick(list, type, ...names) {
  for (const n of names) {
    const s = list.find(x => x.t === type && (n instanceof RegExp ? n.test(x.n) : x.n === n));
    if (s) return s;
  }
  return null;
}
const val = s => s ? s.v : null;

function derive() {
  const t = St.tick;
  // CPU
  const cpuHw = hwOf('Cpu')[0];
  D.cpuName = cpuHw ? cpuHw.name : (St.info.cpuName || 'CPU');
  if (cpuHw) {
    const ss = sensorsOf(cpuHw.id);
    D.cpuTempS = pick(ss, 'Temperature', 'Core (Tctl/Tdie)', 'CPU Package', 'Core (Tdie)', 'Core (Tctl)', /package/i, /tdie|tctl/i, /core/i);
    D.cpuPowerS = pick(ss, 'Power', 'Package', 'CPU Package', /package/i);
    const clocks = ss.filter(s => s.t === 'Clock' && /^(Core|CPU Core) #\d+/.test(s.n) && s.v != null);
    D.cpuClock = clocks.length ? clocks.reduce((a, s) => a + s.v, 0) / clocks.length : null;
    D.cpuClockMax = clocks.length ? Math.max(...clocks.map(s => s.v)) : null;
    D.cpuVolt = val(pick(ss, 'Voltage', /core.*(svi|vid)/i, /^core$/i, /vcore/i));
    D.cpuCoreClocks = clocks;
  }
  D.cpuTemp = val(D.cpuTempS) || null; D.cpuPower = val(D.cpuPowerS) || null;   // 0 = sensor not readable (no admin / no PawnIO)
  if (D.cpuCoreClocks) D.cpuCoreClocks = D.cpuCoreClocks.filter(s => s.v > 0);
  if (!D.cpuClock) D.cpuClock = null; if (!D.cpuClockMax) D.cpuClockMax = null;
  // GPUs
  D.gpus = hwOf(['GpuNvidia', 'GpuAmd', 'GpuIntel']).map((g, i) => {
    const ss = sensorsOf(g.id);
    const vu = val(pick(ss, 'SmallData', 'GPU Memory Used', 'D3D Dedicated Memory Used', /memory used/i));
    const vt = val(pick(ss, 'SmallData', 'GPU Memory Total', /memory total/i));
    return {
      id: g.id, name: g.name, type: g.type, color: i ? '--c-gpu2' : '--c-gpu', idx: i,
      loadS: pick(ss, 'Load', 'GPU Core', 'D3D 3D', /core/i),
      temp: val(pick(ss, 'Temperature', 'GPU Core', /core|gpu/i)), hot: val(pick(ss, 'Temperature', 'GPU Hot Spot', /hot ?spot|junction/i)),
      memTemp: val(pick(ss, 'Temperature', 'GPU Memory Junction', 'GPU Memory', /memory/i)),
      vramUsed: vu, vramTotal: vt, clock: val(pick(ss, 'Clock', 'GPU Core', /core|shader/i)), memClock: val(pick(ss, 'Clock', 'GPU Memory', /memory/i)),
      power: val(pick(ss, 'Power', 'GPU Package', 'GPU Power', 'GPU PPT', 'GPU Core', /package|board|ppt|power/i)),
      fan: val(pick(ss, 'Fan', /fan/i)), fanPct: val(pick(ss, 'Control', /fan/i)),
      memLoad: val(pick(ss, 'Load', 'GPU Memory Controller', /memory controller/i)),
      video: val(pick(ss, 'Load', 'GPU Video Engine', /video/i)), bus: val(pick(ss, 'Load', 'GPU Bus', /bus/i)),
      sensors: ss,
    };
  });
  D.gpus.forEach(g => g.load = val(g.loadS));
  // physical disks
  D.disks = hwOf('Storage').map(d => {
    const ss = sensorsOf(d.id);
    return {
      id: d.id, name: d.name, sensors: ss,
      temp: val(pick(ss, 'Temperature', 'Temperature', 'Composite Temperature', /temp/i)),
      act: val(pick(ss, 'Load', 'Total Activity', /activity/i)), used: val(pick(ss, 'Load', 'Used Space', /used/i)),
      r: val(pick(ss, 'Throughput', 'Read Rate', /read/i)), w: val(pick(ss, 'Throughput', 'Write Rate', /write/i)),
      life: val(pick(ss, 'Level', 'Remaining Life', /life/i)), spare: val(pick(ss, 'Level', 'Available Spare', /spare/i)),
      readTot: val(pick(ss, 'Data', 'Data Read', 'Host Reads', /read/i)), writeTot: val(pick(ss, 'Data', 'Data Written', 'Host Writes', /writ/i)),
    };
  });
  D.diskR = D.disks.reduce((a, d) => a + (d.r || 0), 0); D.diskW = D.disks.reduce((a, d) => a + (d.w || 0), 0);
  if (t.disk && !D.disks.some(d => d.r != null)) { D.diskR = t.disk.r; D.diskW = t.disk.w; }
  D.diskAct = t.disk ? t.disk.act : null;
  // kernel counters (ctx switches, DPC, standby list, disk latency…) + Task-Manager-style effective clock
  D.sys = t.sys || {};
  D.cpuEff = D.sys.freq && D.sys.perf ? D.sys.freq * D.sys.perf / 100 : null;
  if (D.cpuClock == null && D.cpuEff) D.cpuClock = D.cpuEff;          // no admin / no PawnIO → still show a speed
  D.diskLat = D.sys.dlr != null ? Math.max(D.sys.dlr, D.sys.dlw || 0) : null;
  D.ping = t.ping || []; D.gwPing = D.ping.find(p => p.auto) || null;
  D.down = (t.net || []).reduce((a, n) => a + n.down, 0); D.up = (t.net || []).reduce((a, n) => a + n.up, 0);
  D.memPct = t.mem ? t.mem.used / t.mem.total * 100 : 0;
}

/* ---------- tick ---------- */
function onTick(t) {
  expandHw(t);
  St.tick = t; Charts.tick++;
  if (t.hw) {
    if (St.hwVerSeen !== St.hwVer) {
      St.hwVerSeen = St.hwVer;
      for (const x of t.hw.hw) St.hw.set(x.id, x);
      St.sens.clear(); for (const s of t.hw.sensors) St.sens.set(s.id, s);
    }
    for (const s of t.hw.sensors) ring(H.sens, s.id).push(s.v);
  }
  for (const p of t.ping || []) {
    let x = H.ping.get(p.host); if (!x) H.ping.set(p.host, x = { ring: new Ring(S.history), seq: -1 });
    if (p.seq !== x.seq) { x.seq = p.seq; x.ring.push(p.ms >= 0 ? p.ms : null); }
  }
  St.procs = t.procs || []; St.byPid.clear(); for (const p of St.procs) St.byPid.set(p[0], p);
  derive();
  // history
  if (t.cpu) {
    H.cpu.push(t.cpu.total); H.kernel.push(t.cpu.kernel);
    t.cpu.cores.forEach((v, i) => { (H.core[i] = H.core[i] || new Ring(S.history)).push(v); });
  }
  H.mem.push(t.mem.used); H.memPct.push(D.memPct); H.commit.push(t.mem.commit);
  H.down.push(D.down); H.up.push(D.up); H.dr.push(D.diskR); H.dw.push(D.diskW); H.dact.push(D.diskAct);
  H.cpuTemp.push(D.cpuTemp); H.cpuClock.push(D.cpuClock); H.cpuPower.push(D.cpuPower);
  for (const n of t.net || []) { let x = H.nic.get(n.id); if (!x) H.nic.set(n.id, x = { down: new Ring(S.history), up: new Ring(S.history) }); x.down.push(n.down); x.up.push(n.up); }
  const live = new Set();
  for (const p of St.procs) {
    const key = p[0] + ':' + p[14]; live.add(key);
    let x = H.proc.get(key); if (!x) H.proc.set(key, x = { cpu: new Ring(60), mem: new Ring(60) });
    x.cpu.push(p[3]); x.mem.push(p[4]);
  }
  if (H.proc.size > live.size + 50) for (const k of H.proc.keys()) if (!live.has(k)) H.proc.delete(k);
  // render
  statusbar(); alerts();
  if (document.body.classList.contains('mini')) renderMini();
  else { const v = Views[St.view]; if (v && v.tick) try { v.tick(); } catch (e) { console.error(e); } }
}

/* ---------- views ---------- */
function setView(v) {
  if (v === 'mini') return setMini(true);
  St.view = v;
  $$('.act[data-view]').forEach(b => b.classList.toggle('active', b.dataset.view === v));
  $$('.view').forEach(s => s.classList.toggle('active', s.id === 'view-' + v));
  const vw = Views[v]; if (vw && vw.show) vw.show(); if (vw && vw.tick && St.tick) vw.tick();
  if (v !== 'mini' && v !== 'settings') { S.startView = v; saveSettings(); }
}

/* ---------- status bar ---------- */
function statusbar() {
  const t = St.tick;
  $('#sb-cpu').innerHTML = `CPU <b>${F.pct(t.cpu.total)}</b>`;
  $('#sb-mem').innerHTML = `RAM <b>${F.bytes(t.mem.used, 1)}</b> / ${F.bytes(t.mem.total, 0)}`;
  $('#sb-gpu').innerHTML = D.gpus.map((g, i) => `GPU${i} <b>${F.pct(g.load, 0)}</b>`).join(' · ');
  const hottest = Math.max(D.cpuTemp || 0, ...D.gpus.map(g => g.temp || 0));
  $('#sb-temp').innerHTML = `<span style="color:${tempColor(hottest)}">▲</span>` + (D.cpuTemp != null ? ` CPU <b>${F.temp(D.cpuTemp)}</b>` : '') + D.gpus.map((g, i) => ` GPU${i} <b>${F.temp(g.temp, 0)}</b>`).join('');
  $('#sb-net').innerHTML = `↓ <b>${F.bits(D.down)}</b> ↑ <b>${F.bits(D.up)}</b>`;
  $('#sb-procs').innerHTML = `procs <b>${t.mem.procs}</b> thr <b>${F.num(t.mem.threads)}</b> hnd <b>${F.num(t.mem.handles)}</b>`;
  $('#sb-up').innerHTML = `up <b>${F.dur(t.up)}</b>`;
  $('#sb-log').innerHTML = t.logging ? '<span class="dot rec"></span> REC' : '<span class="dot"></span> log';
  $('#sb-int').innerHTML = `${S.interval}ms`;
  setHTML($('#sb-cost'), `<span class="muted">${t.cost}ms</span>`);
  if (t.prof) $('#sb-cost').title = `Sampling cost per tick (host side)\ncpu+mem ${t.prof.cpu} ms\nprocesses ${t.prof.procs} ms (${St.procs.length})\nnet/drives ${t.prof.net} ms\nsensors snapshot ${t.prof.hw} ms (LHM pass ${t.prof.sensorBg} ms on its own thread)\nkernel counters + ping ${t.prof.ctr} ms\nGPU-per-process (background) ${t.prof.gpuBg} ms\npayload ${(t.prof.bytes / 1024).toFixed(1)} KB · dropped frames ${t.prof.dropped}`;
  $('#mb-clock').textContent = new Date(t.t).toLocaleTimeString('en-US', { hour12: false });
  $('#se-log').textContent = t.logging ? 'Stop CSV log' : 'Start CSV log';
  $('#se-log').classList.toggle('danger', !!t.logging);
}

/* ---------- alerts ---------- */
const lastAlert = {};
function alerts() {
  const a = S.alerts; if (!a.on) { $('#sb-alert').className = 'st'; $('#sb-alert').textContent = ''; return; }
  const hits = [];
  if (a.cpuTemp && D.cpuTemp > a.cpuTemp) hits.push(`CPU ${F.temp(D.cpuTemp)}`);
  D.gpus.forEach((g, i) => { const tt = Math.max(g.temp || 0, g.hot ? g.hot - 10 : 0); if (a.gpuTemp && tt > a.gpuTemp) hits.push(`GPU${i} ${F.temp(tt === g.temp ? g.temp : g.hot)}${tt === g.temp ? '' : ' hot spot'}`); });
  if (a.ram && D.memPct > a.ram) hits.push(`RAM ${D.memPct.toFixed(0)}%`);
  if (a.cpuLoad && H.cpu.count > 10) { let s = 0; for (let k = H.cpu.n - 10; k < H.cpu.n; k++) s += H.cpu.get(k) || 0; if (s / 10 > a.cpuLoad) hits.push(`CPU load ${(s / 10).toFixed(0)}% (10s)`); }
  const sb = $('#sb-alert');
  sb.className = 'st' + (hits.length ? ' alert' : ''); sb.textContent = hits.length ? '⚠ ' + hits.join(' · ') : '';
  const now = Date.now();
  hits.forEach(m => { const k = m.split(' ')[0]; if (!lastAlert[k] || now - lastAlert[k] > 60000) { lastAlert[k] = now; toast('⚠ Threshold: ' + m, 'warn', 6000); } });
}

/* ---------- overview ---------- */
Views.overview = (() => {
  let built = false, charts = [], gpuSig = '';
  const tile = (id, label, color) => `<div class="card tile" data-go="${id}"><div class="tl"><span style="color:${col(color)}">■</span>${label}<span class="tn" id="tn-${id}"></span></div>
    <div class="tv" id="tv-${id}">—</div><div class="ts" id="ts-${id}"></div><canvas id="tc-${id}"></canvas></div>`;
  function build() {
    const sig = D.gpus.map(g => g.id).join('|'); gpuSig = sig;
    const b = $('#ov-body');
    b.innerHTML = `<div class="grid g-tiles" id="ov-tiles">
        ${tile('cpu', 'CPU', '--c-cpu')}${tile('mem', 'Memory', '--c-mem')}
        ${D.gpus.map((g, i) => tile('gpu' + i, 'GPU ' + i, g.color)).join('')}
        ${tile('net', 'Network', '--c-down')}${tile('disk', 'Disk I/O', '--c-disk')}</div>
      <div class="grid g-2 sec-gap">
        <div class="card"><div class="ch">CPU <span class="sub" id="ov-cpuname"></span><span class="r mono muted" id="ov-cpustat"></span></div>
          <div class="chartbox"><canvas id="ov-cpuchart"></canvas></div>
          <div class="legend"><span><i style="background:${col('--c-cpu')}"></i>total</span><span><i style="background:${col('--fg3')}"></i>kernel</span><span><i style="background:${col('--c-mem')}"></i>memory %</span></div>
          <div class="cores sec-gap" id="ov-cores"></div></div>
        <div class="card"><div class="ch">Thermals<span class="r mono muted" id="ov-hot"></span></div><div id="ov-temps"></div>
          <div class="ch sec-gap">Fans &amp; pumps</div><div id="ov-fans"></div>
          <div class="ch sec-gap">Power draw</div><div id="ov-power"></div></div>
      </div>
      <div class="grid g-3 sec-gap">
        <div class="card"><div class="ch">Top CPU</div><div class="tlist" id="ov-tcpu"></div></div>
        <div class="card"><div class="ch">Top memory</div><div class="tlist" id="ov-tmem"></div></div>
        <div class="card"><div class="ch">Top GPU / disk</div><div class="tlist" id="ov-tio"></div></div>
      </div>`;
    charts = [new LineChart($('#ov-cpuchart'), { series: [{ ring: H.cpu, color: '--c-cpu', label: 'CPU' }, { ring: H.kernel, color: '--fg3', label: 'kernel', fill: false, width: 1 }, { ring: H.memPct, color: '--c-mem', label: 'RAM', fill: false, width: 1.2 }], max: 100, fmt: v => v.toFixed(0) + '%' })];
    $$('#ov-tiles .tile').forEach(t => t.onclick = () => { setView('perf'); Views.perf.select(t.dataset.go === 'disk' ? (D.disks[0] ? 'disk:' + D.disks[0].id : 'diskall') : t.dataset.go === 'net' ? 'nic:' + ((St.tick.net || [])[0] || {}).id : t.dataset.go); });
    const cores = $('#ov-cores'); cores.innerHTML = '';
    (St.tick.cpu.cores || []).forEach((_, i) => cores.appendChild(h(`<div class="core"><div class="cf"></div><div class="cl"><span>T${String(i).padStart(2, '0')}</span><span class="ck"></span></div><div class="cv">0%</div><canvas></canvas></div>`)));
    built = true;
  }
  function meterRows(el, list, fmt, maxOf, colorOf) {
    setHTML(el, list.length ? list.map(s => {
      const hw = St.hw.get(s.hw); const m = maxOf(s);
      return `<div class="mrow"><span class="mn">${esc(s.n)} <em>${esc(shortHw(hw))}</em></span><span class="meter"><i style="width:${clamp((s.v || 0) / m * 100, 0, 100)}%;background:${colorOf(s)}"></i></span><span class="mv">${fmt(s)}</span></div>`;
    }).join('') : '<div class="muted s">none reported</div>');
  }
  function topList(el, rows, valFn, fmt) {
    setHTML(el, rows.map(p => `<div class="tr" data-pid="${p[0]}">${Icons.img(Meta.get(p[0]).path)}<span class="tn2">${esc(p[2])} <span class="muted mono">${p[0]}</span></span><span class="tv2">${fmt(valFn(p))}</span></div>`).join(''));
    el.onclick = e => { const r = e.target.closest('.tr'); if (r) { setView('procs'); Views.procs.select(+r.dataset.pid); } };
  }
  return {
    tick() {
      const t = St.tick;
      if (!built || gpuSig !== D.gpus.map(g => g.id).join('|')) build();
      $('#ov-sub').textContent = `${St.info.machine || ''} · ${St.cores} threads · ${F.bytes(t.mem.total, 0)} RAM · ${D.gpus.length} GPU`;
      // tiles
      const set = (id, v, sub, name) => { $('#tv-' + id).innerHTML = v; $('#ts-' + id).innerHTML = sub; if (name != null) $('#tn-' + id).textContent = name; };
      set('cpu', `${t.cpu.total.toFixed(1)}<small>%</small>`, `<span>${D.cpuClock ? F.mhz(D.cpuClock) : ''}</span><span style="color:${tempColor(D.cpuTemp)}">${D.cpuTemp != null ? F.temp(D.cpuTemp) : ''}</span><span>${D.cpuPower != null ? D.cpuPower.toFixed(0) + ' W' : ''}</span>`, D.cpuName);
      spark($('#tc-cpu'), H.cpu, '--c-cpu', 100);
      set('mem', `${F.bytes(t.mem.used, 1).replace(' ', '<small>')}</small>`, `<span>${D.memPct.toFixed(0)}% of ${F.bytes(t.mem.total, 0)}</span><span>commit ${F.bytes(t.mem.commit, 1)}</span>`, '');
      spark($('#tc-mem'), H.mem, '--c-mem', t.mem.total);
      D.gpus.forEach((g, i) => {
        const vr = g.vramUsed != null ? `${(g.vramUsed / 1024).toFixed(1)}${g.vramTotal ? '/' + (g.vramTotal / 1024).toFixed(0) : ''} GB` : '';
        set('gpu' + i, `${(g.load || 0).toFixed(0)}<small>%</small>`, `<span style="color:${tempColor(g.temp)}">${F.temp(g.temp, 0)}</span><span>${vr}</span><span>${g.power != null ? g.power.toFixed(0) + ' W' : ''}</span>`, g.name);
        spark($('#tc-gpu' + i), ring(H.sens, g.loadS ? g.loadS.id : 'x'), g.color, 100);
      });
      set('net', `${F.bits(D.down).replace(' ', '<small>')}</small>`, `<span>↑ ${F.bits(D.up)}</span>${D.gwPing ? `<span style="color:${pingColor(D.gwPing.ms)}">gw ${D.gwPing.ms >= 0 ? D.gwPing.ms + 'ms' : 'timeout'}</span>` : `<span>${(t.net || []).length} if</span>`}`, '');
      spark($('#tc-net'), H.down, '--c-down', Math.max(H.down.max(), H.up.max()));
      set('disk', `${F.rate(D.diskR + D.diskW).replace(' ', '<small>')}</small>`, `<span>R ${F.rate(D.diskR)}</span><span>W ${F.rate(D.diskW)}</span>${D.diskAct != null ? `<span>${D.diskAct.toFixed(0)}% busy</span>` : ''}${D.diskLat != null ? `<span>${D.diskLat.toFixed(1)}ms</span>` : ''}`, '');
      spark($('#tc-disk'), H.dr, '--c-disk', Math.max(H.dr.max(), H.dw.max()));
      // cpu card
      $('#ov-cpuname').textContent = D.cpuName;
      $('#ov-cpustat').textContent = `${D.cpuClock ? F.mhz(D.cpuClock) + ' avg' : ''}${D.cpuClockMax ? ' · ' + F.mhz(D.cpuClockMax) + ' max' : ''}${D.cpuVolt ? ' · ' + D.cpuVolt.toFixed(3) + ' V' : ''}${D.sys.ctx != null ? ' · ' + F.num(Math.round(D.sys.ctx)) + ' ctx/s' : ''}`;
      charts.forEach(c => c.draw());
      const cc = $$('#ov-cores .core');
      t.cpu.cores.forEach((v, i) => {
        const c = cc[i]; if (!c) return;
        c.querySelector('.cv').textContent = v.toFixed(0) + '%';
        const f = c.querySelector('.cf'); f.style.height = v + '%'; f.style.background = loadColor(v);
        const ck = D.cpuCoreClocks && D.cpuCoreClocks[Math.floor(i / Math.max(1, St.cores / Math.max(1, D.cpuCoreClocks.length)))];
        c.querySelector('.ck').textContent = ck ? (ck.v / 1000).toFixed(2) + 'G' : '';
        spark(c.querySelector('canvas'), H.core[i], '--c-cpu', 100, false);
      });
      // thermals / fans / power
      const sensors = t.hw ? t.hw.sensors : [];
      const temps = sensors.filter(s => s.t === 'Temperature' && s.v != null && s.v > 0 && s.v < 150 && !/distance|limit|critical|warning|resolution/i.test(s.n));
      const prio = s => { const hw = St.hw.get(s.hw) || {}; return ({ Cpu: 0, GpuNvidia: 1, GpuAmd: 1, GpuIntel: 1, Storage: 3, Memory: 4 }[hw.type] ?? 2); };
      temps.sort((a, b) => prio(a) - prio(b) || b.v - a.v);
      const hot = temps.reduce((m, s) => s.v > (m ? m.v : -1) ? s : m, null);
      $('#ov-hot').innerHTML = hot ? `hottest <span style="color:${tempColor(hot.v)}">${F.temp(hot.v)}</span> ${esc(hot.n)}` : '';
      meterRows($('#ov-temps'), temps.slice(0, 16), s => `<span style="color:${tempColor(s.v)}">${F.temp(s.v)}</span>`, () => 105, s => tempColor(s.v));
      const fans = sensors.filter(s => s.t === 'Fan' && s.v != null && (s.v > 0 || s.mx > 0));
      meterRows($('#ov-fans'), fans, s => s.v.toFixed(0) + ' rpm', s => Math.max(2000, s.mx || 0), () => col('--c-cpu'));
      const pw = sensors.filter(s => s.t === 'Power' && s.v != null && s.v > 0.5 && /package|ppt|gpu power|board|total|core|cpu/i.test(s.n));
      const seenPw = new Set(); const pw2 = pw.filter(s => { const k = s.hw + s.n; if (seenPw.has(k)) return false; seenPw.add(k); return true; }).slice(0, 8);
      meterRows($('#ov-power'), pw2, s => s.v.toFixed(1) + ' W', s => Math.max(100, s.mx || 0), () => col('--accent2'));
      // top lists
      const ps = St.procs.filter(p => p[0] !== 0);
      topList($('#ov-tcpu'), [...ps].sort((a, b) => b[3] - a[3]).slice(0, 8), p => p[3], v => v.toFixed(1) + '%');
      topList($('#ov-tmem'), [...ps].sort((a, b) => b[4] - a[4]).slice(0, 8), p => p[4], v => F.bytes(v, 1));
      const io = [...ps].sort((a, b) => (b[10] * 1e9 + b[8] + b[9]) - (a[10] * 1e9 + a[8] + a[9])).slice(0, 8);
      topList($('#ov-tio'), io, p => p, p => p[10] > 0 ? p[10].toFixed(1) + '% gpu' : F.rate(p[8] + p[9]));
      Meta.want(ps.map(p => p[0]));
    },
  };
})();
function shortHw(hw) { if (!hw) return ''; return hw.name.replace(/(AMD|Intel|NVIDIA|Radeon|GeForce|\(R\)|\(TM\)|Processor|Core\b)/gi, '').replace(/\s+/g, ' ').trim().slice(0, 22); }

/* process metadata cache (path/user/description) — fetched lazily for new PIDs */
const Meta = (() => {
  const m = new Map(); const want = new Set(); let busy = false;
  const flush = async () => {
    if (busy || !want.size) return; busy = true;
    const pids = [...want].slice(0, 400); pids.forEach(p => want.delete(p));
    try { const r = await Host.call('procMeta', { pids }); for (const k in r) m.set(+k, r[k]); document.dispatchEvent(new Event('meta')); } catch { }
    busy = false; if (want.size) setTimeout(flush, 100);
  };
  return {
    get(pid) { return m.get(pid) || {}; },
    has(pid) { return m.has(pid); },
    want(pids) { let n = 0; for (const p of pids) if (!m.has(p)) { want.add(p); n++; } if (n) setTimeout(flush, 0); },
    refresh(pid) { m.delete(pid); Host.call('procMeta', { pids: [pid], fresh: true }).then(r => { for (const k in r) m.set(+k, r[k]); }); },
  };
})();

/* ---------- mini overlay ---------- */
function setMini(on) {
  Host.call('setMini', { on, topmost: S.topmost });
  document.body.classList.toggle('mini', on);
  if (on) {
    $('#mini').innerHTML = `<div class="mh">XENO<span class="muted">MON</span><span class="grow"></span><button class="tbtn" id="mini-exit" title="Back to full view (Ctrl+M)">&#xE740;</button></div><div id="mini-rows"></div><div class="ch muted" style="font-size:10px;margin-top:4px">TOP CPU</div><div class="tlist" id="mini-top"></div>`;
    $('#mini-exit').onclick = () => setMini(false);
    if (St.tick) renderMini();
  } else if (St.tick) setView(St.view);
}
function renderMini() {
  const t = St.tick; const rows = [
    ['CPU', H.cpu, '--c-cpu', 100, `${t.cpu.total.toFixed(0)}% ${D.cpuTemp != null ? F.temp(D.cpuTemp, 0) : ''}`],
    ['RAM', H.mem, '--c-mem', t.mem.total, `${D.memPct.toFixed(0)}% ${F.bytes(t.mem.used, 0)}`],
    ...D.gpus.map((g, i) => ['GPU' + i, ring(H.sens, g.loadS ? g.loadS.id : 'x'), g.color, 100, `${(g.load || 0).toFixed(0)}% ${F.temp(g.temp, 0)}`]),
    ['NET↓', H.down, '--c-down', 0, F.bits(D.down)], ['NET↑', H.up, '--c-up', 0, F.bits(D.up)],
    ['DISK', H.dr, '--c-disk', 0, F.rate(D.diskR + D.diskW)],
  ];
  const el = $('#mini-rows');
  if (el.children.length !== rows.length) el.innerHTML = rows.map(() => `<div class="mrow2"><span class="k"></span><canvas></canvas><span class="v"></span></div>`).join('');
  rows.forEach((r, i) => { const row = el.children[i]; row.children[0].textContent = r[0]; row.children[2].textContent = r[4]; spark(row.children[1], r[1], r[2], r[3] || null); });
  const top = St.procs.filter(p => p[0]).sort((a, b) => b[3] - a[3]).slice(0, 6);
  $('#mini-top').innerHTML = top.map(p => `<div class="tr">${Icons.img(Meta.get(p[0]).path)}<span class="tn2">${esc(p[2])}</span><span class="tv2">${p[3].toFixed(1)}%</span></div>`).join('');
  Meta.want(top.map(p => p[0]));
}

/* ---------- menus ---------- */
const VIEW_KEYS = ['overview', 'perf', 'procs', 'sensors', 'network', 'disks', 'services', 'startup', 'system', 'health'];   // keys 1-9, 0
function pingColor(ms) { return ms == null || ms < 0 ? 'var(--err)' : ms < 20 ? 'var(--ok)' : ms < 80 ? 'var(--warn)' : 'var(--err)'; }
function intervalMenu() { return [250, 500, 1000, 2000, 5000].map(ms => ({ label: ms < 1000 ? ms + ' ms' : ms / 1000 + ' s', check: S.interval === ms, click: () => setInterval_(ms) })); }
function setInterval_(ms) { S.interval = ms; Host.call('setInterval', { ms }); saveSettings(); toast('Sample interval: ' + ms + ' ms'); }
function historyMenu() { return [60, 120, 300, 600, 1800].map(n => ({ label: n + ' samples', check: S.history === n, click: () => resizeHistory(n) })); }
async function toggleLog() {
  if (St.tick && St.tick.logging) { const f = await Host.call('logStop'); toast('Log saved: ' + f); }
  else { const f = await Host.call('logStart'); toast('Logging every sample to ' + f); }
}
async function exportSnapshot() {
  const snap = { time: new Date().toISOString(), derived: D, tick: St.tick, meta: Object.fromEntries(St.procs.map(p => [p[0], Meta.get(p[0])])) };
  const f = await Host.call('writeFile', { name: `snapshot-${Date.now()}.json`, text: JSON.stringify(snap, null, 1) });
  toast('Snapshot written: ' + f);
}
function setTheme(t) { S.theme = t; document.body.classList.toggle('light', t === 'light'); refreshColors(); Host.call('setDarkTitle', { on: t !== 'light' }); saveSettings(); }
function setTopmost(on) { S.topmost = on; Host.call('setTopmost', { on }); saveSettings(); }
function togglePause() { St.paused = !St.paused; Host.call('pause', { on: St.paused }); toast(St.paused ? 'Updates paused (Space to resume)' : 'Updates resumed'); }
async function runTask() {
  const cmd = await modal({ title: 'Run new task', body: 'Program, document, folder or URL. Runs elevated (XenoMon is admin).', input: 'e.g. cmd, notepad, regedit, https://…', buttons: [{ label: 'Cancel', value: null }, { label: 'Run', value: true }] });
  if (cmd) Host.call('runTask', { cmd }).catch(e => toast(e.message, 'error'));
}
const tool = c => Host.call('runTask', { cmd: c }).catch(e => toast(e.message, 'error'));
const MENUS = {
  file: () => [
    { label: (St.tick && St.tick.logging) ? 'Stop CSV log' : 'Start CSV log', key: 'Ctrl+L', click: toggleLog },
    { label: 'Export snapshot (JSON)', key: 'Ctrl+S', click: exportSnapshot },
    { label: 'Open data folder', click: () => Host.call('openDataDir') }, '-',
    { label: 'Exit', key: 'Alt+F4', click: () => Host.call('quit') }],
  view: () => [
    ...VIEW_KEYS.map((v, i) => ({ label: $(`.act[data-view=${v}]`).title.replace(/ \(.*/, ''), key: String((i + 1) % 10), check: St.view === v, click: () => setView(v) })), '-',
    { label: 'Mini overlay', key: 'Ctrl+M', click: () => setMini(true) },
    { label: 'Always on top', check: S.topmost, click: () => setTopmost(!S.topmost) },
    { label: 'Pause updates', key: 'Space', check: St.paused, click: togglePause },
    { label: 'Sample interval', sub: intervalMenu() }, { label: 'History length', sub: historyMenu() },
    { label: 'Theme', sub: [{ label: 'Dark', check: S.theme !== 'light', click: () => setTheme('dark') }, { label: 'Light', check: S.theme === 'light', click: () => setTheme('light') }] },
    { label: 'Temperature unit', sub: [{ label: '°C', check: S.tempUnit === 'C', click: () => { S.tempUnit = 'C'; saveSettings(); } }, { label: '°F', check: S.tempUnit === 'F', click: () => { S.tempUnit = 'F'; saveSettings(); } }] }],
  tools: () => [
    { label: 'Run new task…', key: 'Ctrl+R', click: runTask },
    { label: 'Reset sensor min/max', click: () => Host.call('resetMinMax').then(() => toast('Min/max reset')) },
    { label: 'Latency targets…', click: () => editPingTargets() },
    { label: 'System health scan', click: () => setView('health') },
    { label: 'Full hardware report', click: () => Host.call('hwReport') }, '-',
    { label: 'Command prompt (admin)', click: () => tool('cmd.exe') },
    { label: 'PowerShell (admin)', click: () => tool('powershell.exe') },
    { label: 'Resource Monitor', click: () => tool('resmon.exe') },
    { label: 'Device Manager', click: () => tool('devmgmt.msc') },
    { label: 'Event Viewer', click: () => tool('eventvwr.msc') },
    { label: 'Services console', click: () => tool('services.msc') },
    { label: 'Windows Task Manager', click: () => tool('taskmgr.exe') }, '-',
    { label: 'Developer tools', key: 'F12', click: () => Host.call('devTools') }],
  help: () => [
    { label: 'Keyboard shortcuts', click: shortcuts },
    { label: 'About XenoMon', click: () => modal({ title: 'XenoMon ' + (St.info.version || ''), body: 'Xeno\'s hardware monitor + task manager.\nSensors: LibreHardwareMonitor (PawnIO driver, NVAPI, ADL).\nProcesses: NtQuerySystemInformation.\nGPU per-process: GPU Engine perf counters.' }) }],
};
function shortcuts() {
  modal({ title: 'Keyboard shortcuts', body: '1-9, 0   switch view (0 = Health)\nCtrl+F   filter (processes / sensors / …)\nDel      end selected process\nShift+Del end process tree\nSpace    pause / resume updates\nCtrl+M   mini overlay\nCtrl+L   start / stop CSV log\nCtrl+S   export snapshot\nCtrl+R   run new task\nCtrl+,   settings\nF5       refresh current list\nEsc      close menus / detail pane' });
}

/* ---------- boot ---------- */
async function boot() {
  const bl = (m) => $('#boot-log').appendChild(h(`<div>${esc(m)}</div>`));
  await loadSettings();
  if (S.theme === 'light') document.body.classList.add('light');
  refreshColors(); initHistory();
  Host.on('status', s => {
    if (s.stage === 'sensors') bl(s.msg);
    if (s.stage === 'ready') {
      St.lhm = s.lhm; St.info.machine = s.machine;
      bl(s.lhm ? 'sensors online' : 'sensors FAILED: ' + (s.error || '?'));
      bl('sampling @ ' + S.interval + 'ms'); $('#mb-host').textContent = s.machine;
    }
  });
  let first = true;
  Host.on('tick', t => {
    if (first) { first = false; setTimeout(() => $('#boot').remove(), 150); setView(S.startView || 'overview'); }
    onTick(t);
    if (!St.diagDone && St.tick.hw) { St.diagDone = true; logToHost('diag', 'hw: ' + [...St.hw.values()].map(x => x.type + '=' + x.name).join(' | ') + ' || gpus: ' + D.gpus.map(g => g.name + ' load=' + g.load + ' sensors=' + g.sensors.length).join(' | ')); }
  });
  $$('.act[data-view]').forEach(b => b.onclick = () => setView(b.dataset.view));
  $$('.mtop').forEach(m => m.onmousedown = e => {
    e.stopPropagation(); const open = m.classList.contains('open'); closeMenus(); if (open) return;
    m.classList.add('open'); const r = m.getBoundingClientRect(); ctxMenu(r.left, r.bottom + 2, MENUS[m.dataset.menu]()); m.classList.add('open');
  });
  $('#sb-cpu').onclick = () => { setView('perf'); Views.perf.select('cpu'); };
  $('#sb-mem').onclick = () => { setView('perf'); Views.perf.select('mem'); };
  $('#sb-gpu').onclick = () => { setView('perf'); Views.perf.select('gpu0'); };
  $('#sb-temp').onclick = () => setView('sensors');
  $('#sb-log').onclick = toggleLog;
  $('#sb-int').onclick = e => ctxMenu(e.clientX, e.clientY - 160, intervalMenu());
  window.addEventListener('resize', () => { if (St.tick) { const v = Views[St.view]; v && v.tick && v.tick(); } });
  document.addEventListener('keydown', e => {
    const inInput = e.target.matches('input, select, textarea');
    if (!$('#modal').classList.contains('hidden')) return;
    if (e.key === 'Escape') { closeMenus(); if (Views[St.view] && Views[St.view].esc) Views[St.view].esc(); return; }
    if (e.ctrlKey && e.key.toLowerCase() === 'm') { e.preventDefault(); setMini(!document.body.classList.contains('mini')); return; }
    if (e.ctrlKey && e.key.toLowerCase() === 'l') { e.preventDefault(); toggleLog(); return; }
    if (e.ctrlKey && e.key.toLowerCase() === 's') { e.preventDefault(); exportSnapshot(); return; }
    if (e.ctrlKey && e.key.toLowerCase() === 'r') { e.preventDefault(); runTask(); return; }
    if (e.ctrlKey && e.key === ',') { e.preventDefault(); setView('settings'); return; }
    if (e.ctrlKey && e.key.toLowerCase() === 'f') { e.preventDefault(); const f = $(`#view-${St.view} input[type=search]`); if (f) { f.focus(); f.select(); } return; }
    if (e.key === 'F12') { Host.call('devTools'); return; }
    if (inInput) { if (e.key === 'Escape') e.target.blur(); return; }
    if (e.key === ' ') { e.preventDefault(); togglePause(); return; }
    if (e.key === 'F5') { e.preventDefault(); const v = Views[St.view]; v && v.refresh && v.refresh(); return; }
    if (/^[0-9]$/.test(e.key) && !e.ctrlKey && !e.altKey) { setView(VIEW_KEYS[(+e.key + 9) % 10]); return; }
    const v = Views[St.view]; if (v && v.key) v.key(e);
  });
  document.addEventListener('contextmenu', e => { if (!e.target.closest('input')) e.preventDefault(); });
  const r = await Host.call('ready');
  Object.assign(St.info, r); St.admin = r.admin; St.cores = r.cores;
  $('#sb-admin').innerHTML = r.admin ? '<span class="pill ok">ADMIN</span>' : '<span class="pill warn" title="CPU/motherboard sensors need admin — click to restart elevated" style="cursor:pointer">USER · elevate</span>';
  if (!r.admin) $('#sb-admin').onclick = () => Host.call('restartAdmin');
  bl(`host ok · ${r.cores} logical cpus · ${r.admin ? 'elevated' : 'NOT elevated'}`);
  if (S.interval !== 1000) Host.call('setInterval', { ms: S.interval });
  if (S.topmost) Host.call('setTopmost', { on: true });
  if (S.theme === 'light') Host.call('setDarkTitle', { on: false });
}
