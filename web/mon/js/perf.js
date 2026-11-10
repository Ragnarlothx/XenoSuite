/* Performance view: Task-Manager-style device list on the left, deep graphs + stats on the right. */
'use strict';
Views.perf = (() => {
  let sel = 'cpu', builtFor = null, charts = [], perThread = false, navSig = '', ramInfo = null;
  const navItems = () => {
    const t = St.tick; const items = [
      { key: 'cpu', title: 'CPU', ring: H.cpu, color: '--c-cpu', max: 100, l2: () => `${t.cpu.total.toFixed(0)}%  ${D.cpuClock ? F.mhz(D.cpuClock) : ''}`, l3: () => D.cpuTemp != null ? F.temp(D.cpuTemp) : '' },
      { key: 'mem', title: 'Memory', ring: H.mem, color: '--c-mem', max: t.mem.total, l2: () => `${F.bytes(t.mem.used, 1)}/${F.bytes(t.mem.total, 0)} (${D.memPct.toFixed(0)}%)`, l3: () => '' },
    ];
    D.gpus.forEach((g, i) => items.push({ key: 'gpu' + i, title: 'GPU ' + i, ring: ring(H.sens, g.loadS ? g.loadS.id : 'x'), color: g.color, max: 100, l2: () => g.name, l3: () => `${(g.load || 0).toFixed(0)}%  ${F.temp(g.temp, 0)}` }));
    D.disks.forEach((d, i) => items.push({ key: 'disk:' + d.id, title: 'Disk ' + i, ring: ring(H.sens, (d.sensors.find(s => s.n === 'Total Activity') || {}).id || 'x'), color: '--c-disk', max: 100, l2: () => d.name, l3: () => `${d.act != null ? d.act.toFixed(0) + '%' : ''} ${d.temp != null ? F.temp(d.temp, 0) : ''}` }));
    if (!D.disks.length && t.disk) items.push({ key: 'diskall', title: 'Disk (all)', ring: H.dact, color: '--c-disk', max: 100, l2: () => `R ${F.rate(D.diskR)} W ${F.rate(D.diskW)}`, l3: () => D.diskAct != null ? D.diskAct.toFixed(0) + '% active' : '' });
    (t.net || []).forEach(n => { const x = H.nic.get(n.id); items.push({ key: 'nic:' + n.id, title: n.type.replace('Ethernet', 'Ethernet').replace('Wireless80211', 'Wi-Fi'), ring: x.down, color: '--c-down', max: 0, l2: () => n.name, l3: () => `↓ ${F.bits(n.down)} ↑ ${F.bits(n.up)}` }); });
    return items;
  };
  function buildNav(items) {
    const nav = $('#perf-nav');
    nav.innerHTML = items.map(it => `<div class="pnav" data-k="${esc(it.key)}"><canvas></canvas><div style="min-width:0"><div class="pn1">${esc(it.title)}</div><div class="pn2"></div><div class="pn3"></div></div></div>`).join('');
    $$('.pnav', nav).forEach(n => n.onclick = () => select(n.dataset.k));
  }
  function select(k) { sel = k; builtFor = null; if (St.tick) tick(); }
  const stat = (k, id, small) => `<div class="stat"><div class="sk">${k}</div><div class="sv ${small ? 's' : ''}" id="${id}">—</div></div>`;
  const setT = (id, v) => setHTML(document.getElementById(id), String(v));
  const chartCard = (id, title, legend, cls) => `<div class="card"><div class="ch">${title}<span class="r mono muted" id="${id}-v"></span></div><div class="chartbox ${cls || ''}"><canvas id="${id}"></canvas></div>${legend ? `<div class="legend">${legend}</div>` : ''}</div>`;
  const lg = (c, l) => `<span><i style="background:${col(c)}"></i>${l}</span>`;

  function build() {
    charts = []; const el = $('#perf-detail'); const t = St.tick;
    $('#pf-cores-toggle').classList.toggle('hidden', sel !== 'cpu');
    $('#pf-cores-toggle').classList.toggle('on', perThread);
    if (sel === 'cpu') {
      el.innerHTML = `<div class="ph"><h2>CPU</h2><span class="model">${esc(D.cpuName)}</span></div>
        ${perThread ? `<div class="card"><div class="ch">Per logical processor<span class="r mono muted">% utilization, ${S.history} samples</span></div><div class="minis" id="pf-minis"></div></div>`
          : chartCard('pc-main', '% Utilization', lg('--c-cpu', 'total') + lg('--fg3', 'kernel'), 'tall')}
        <div class="grid g-3 sec-gap">${chartCard('pc-clk', 'Clock (avg core)', '', 'short')}${chartCard('pc-tmp', 'Temperature', '', 'short')}${chartCard('pc-pwr', 'Package power', '', 'short')}</div>
        <div class="stats">${stat('Utilization', 'ps-util')}${stat('Speed (avg)', 'ps-spd')}${stat('Fastest core', 'ps-max')}${stat('Temperature', 'ps-tmp')}${stat('Power', 'ps-pwr')}${stat('Core voltage', 'ps-v')}
          ${stat('Processes', 'ps-proc')}${stat('Threads', 'ps-thr')}${stat('Handles', 'ps-hnd')}${stat('Up time', 'ps-up')}${stat('Logical processors', 'ps-lp')}${stat('Kernel time', 'ps-k')}
          ${stat('Effective speed', 'ps-eff')}${stat('Context switches/s', 'ps-ctx')}${stat('Interrupts/s', 'ps-int')}${stat('System calls/s', 'ps-sysc')}${stat('DPC / interrupt time', 'ps-dpc')}${stat('Run queue (threads)', 'ps-q')}</div>
        <div class="card sec-gap"><div class="ch">All CPU sensors</div><div class="kv" id="pc-sens"></div></div>`;
      if (perThread) {
        const m = $('#pf-minis'); m.innerHTML = t.cpu.cores.map((_, i) => `<div class="mini"><div class="ml"><span>CPU ${i}</span><span id="pm-${i}"></span></div><canvas></canvas></div>`).join('');
        $$('.mini canvas', m).forEach((c, i) => charts.push(new LineChart(c, { series: [{ ring: H.core[i], color: '--c-cpu', label: 'CPU ' + i }], max: 100, yLabels: false, fmt: v => v.toFixed(0) + '%' })));
      } else charts.push(new LineChart($('#pc-main'), { series: [{ ring: H.cpu, color: '--c-cpu', label: 'total' }, { ring: H.kernel, color: '--fg3', label: 'kernel', fill: false, width: 1 }], max: 100, fmt: v => v.toFixed(0) + '%' }));
      charts.push(new LineChart($('#pc-clk'), { series: [{ ring: H.cpuClock, color: '--c-cpu', label: 'clock' }], max: 'auto', floor: 1000, fmt: v => (v / 1000).toFixed(1) + 'G' }));
      charts.push(new LineChart($('#pc-tmp'), { series: [{ ring: H.cpuTemp, color: '--c-temp', label: 'temp' }], max: 100, fmt: v => F.temp(v, 0) }));
      charts.push(new LineChart($('#pc-pwr'), { series: [{ ring: H.cpuPower, color: '--accent2', label: 'power' }], max: 'auto', floor: 50, fmt: v => v.toFixed(0) + 'W' }));
    } else if (sel === 'mem') {
      el.innerHTML = `<div class="ph"><h2>Memory</h2><span class="model" id="pm-model">${F.bytes(t.mem.total, 1)}</span></div>
        ${chartCard('pm-main', 'Memory usage', lg('--c-mem', 'in use'), 'tall')}
        <div class="card sec-gap"><div class="ch">Composition</div><div class="bigbar" id="pm-bar"></div><div class="legend" id="pm-leg"></div></div>
        <div class="grid g-2 sec-gap">${chartCard('pm-commit', 'Committed', lg('--c-commit', 'commit charge'), 'short')}<div class="card"><div class="ch">Modules</div><div class="kv" id="pm-mods"><span class="k">…</span><span class="v"></span></div></div></div>
        <div class="stats">${stat('In use', 'ms-use')}${stat('Available', 'ms-av')}${stat('Load', 'ms-load')}${stat('Committed', 'ms-com')}${stat('Commit limit', 'ms-lim')}${stat('Commit peak', 'ms-peak')}
          ${stat('Cached', 'ms-cache')}${stat('Paged pool', 'ms-pp')}${stat('Non-paged pool', 'ms-np')}${stat('Page file size', 'ms-pf')}
          ${stat('Standby', 'ms-sb')}${stat('Modified', 'ms-mod')}${stat('Free', 'ms-free')}${stat('Hard faults/s', 'ms-hf')}${stat('Page faults/s', 'ms-flt')}${stat('Pages in / out /s', 'ms-pio')}</div>`;
      charts.push(new LineChart($('#pm-main'), { series: [{ ring: H.mem, color: '--c-mem', label: 'in use' }], max: t.mem.total, fmt: v => F.bytes(v, 0) }));
      charts.push(new LineChart($('#pm-commit'), { series: [{ ring: H.commit, color: '--c-commit', label: 'commit' }], max: t.mem.commitLimit, fmt: v => F.bytes(v, 0) }));
      loadRam();
    } else if (sel.startsWith('gpu')) {
      const g = D.gpus[+sel.slice(3)]; if (!g) { sel = 'cpu'; return build(); }
      const ss = g.sensors; const id = n => (pick(ss, ...n) || {}).id || 'none';
      el.innerHTML = `<div class="ph"><h2>GPU ${g.idx}</h2><span class="model">${esc(g.name)}</span></div>
        ${chartCard('pg-load', 'Engines', lg(g.color, 'core') + lg('--c-mem', 'memory ctrl') + lg('--accent2', 'video') + lg('--fg3', 'bus'), 'tall')}
        <div class="grid g-3 sec-gap">${chartCard('pg-vram', 'Dedicated memory', '', 'short')}${chartCard('pg-temp', 'Temperatures', lg('--c-temp', 'core') + lg('--err', 'hot spot') + lg('--c-mem', 'memory'), 'short')}${chartCard('pg-pwr', 'Power / clock', lg('--accent2', 'W') + lg(g.color, 'MHz ÷10'), 'short')}</div>
        <div class="stats">${stat('Utilization', 'gs-load')}${stat('Dedicated memory', 'gs-vram')}${stat('Core temp', 'gs-t')}${stat('Hot spot', 'gs-hot')}${stat('Memory temp', 'gs-mt')}${stat('Power', 'gs-p')}${stat('Core clock', 'gs-c')}${stat('Memory clock', 'gs-mc')}${stat('Fan', 'gs-f')}${stat('Video engine', 'gs-vid')}</div>
        <div class="grid g-2 sec-gap"><div class="card"><div class="ch">All sensors</div><div class="kv" id="pg-sens"></div></div><div class="card"><div class="ch">Processes on any GPU<span class="r muted">engine % · VRAM</span></div><div class="tlist" id="pg-procs"></div></div></div>`;
      const R = n => ring(H.sens, id(n));
      charts.push(new LineChart($('#pg-load'), { series: [{ ring: R(['Load', 'GPU Core', 'D3D 3D', /core/i]), color: g.color, label: 'core' }, { ring: R(['Load', 'GPU Memory Controller', /memory controller/i]), color: '--c-mem', label: 'mem ctrl', fill: false }, { ring: R(['Load', 'GPU Video Engine', /video/i]), color: '--accent2', label: 'video', fill: false }, { ring: R(['Load', 'GPU Bus', /bus/i]), color: '--fg3', label: 'bus', fill: false, width: 1 }], max: 100, fmt: v => v.toFixed(0) + '%' }));
      charts.push(new LineChart($('#pg-vram'), { series: [{ ring: R(['SmallData', 'GPU Memory Used', 'D3D Dedicated Memory Used', /memory used/i]), color: g.color, label: 'used' }], max: g.vramTotal || 'auto', fmt: v => (v / 1024).toFixed(1) + 'G' }));
      charts.push(new LineChart($('#pg-temp'), { series: [{ ring: R(['Temperature', 'GPU Core', /core|gpu/i]), color: '--c-temp', label: 'core', fill: false }, { ring: R(['Temperature', 'GPU Hot Spot', /hot ?spot|junction/i]), color: '--err', label: 'hot spot', fill: false }, { ring: R(['Temperature', 'GPU Memory Junction', 'GPU Memory', /memory/i]), color: '--c-mem', label: 'memory', fill: false }], max: 110, fmt: v => F.temp(v, 0) }));
      const clkR = R(['Clock', 'GPU Core', /core|shader/i]); const clk10 = { get: k => { const v = clkR.get(k); return v == null ? null : v / 10; }, max: () => clkR.max() / 10, n: clkR.n };
      charts.push(new LineChart($('#pg-pwr'), { series: [{ ring: R(['Power', 'GPU Package', 'GPU Power', 'GPU PPT', 'GPU Core', /package|board|ppt|power/i]), color: '--accent2', label: 'W' }, { ring: clk10, color: g.color, label: 'MHz/10', fill: false, fmt: v => (v * 10).toFixed(0) + ' MHz' }], max: 'auto', floor: 100, fmt: v => v.toFixed(0) }));
    } else if (sel.startsWith('disk:')) {
      const d = D.disks.find(x => x.id === sel.slice(5)); if (!d) { sel = 'cpu'; return build(); }
      const R = (type, ...n) => ring(H.sens, (pick(d.sensors, type, ...n) || {}).id || 'none');
      el.innerHTML = `<div class="ph"><h2>Disk</h2><span class="model">${esc(d.name)}</span></div>
        ${chartCard('pd-act', 'Active time', lg('--c-disk', 'activity %'), '')}
        <div class="grid g-2 sec-gap">${chartCard('pd-rw', 'Transfer rate', lg('--c-disk', 'read') + lg('--c-up', 'write'), 'short')}${chartCard('pd-tmp', 'Temperature', '', 'short')}</div>
        <div class="stats">${stat('Active time', 'ds-act')}${stat('Read', 'ds-r')}${stat('Write', 'ds-w')}${stat('Temperature', 'ds-t')}${stat('Remaining life', 'ds-life')}${stat('Used space', 'ds-used')}${stat('Data read', 'ds-rt')}${stat('Data written', 'ds-wt')}
          ${stat('Latency R / W <span class="muted">(all disks)</span>', 'ds-lat')}${stat('Queue <span class="muted">(all)</span>', 'ds-q')}${stat('IOPS <span class="muted">(all)</span>', 'ds-iops')}</div>
        <div class="card sec-gap"><div class="ch">SMART / all sensors</div><div class="kv" id="pd-sens"></div></div>`;
      charts.push(new LineChart($('#pd-act'), { series: [{ ring: R('Load', 'Total Activity', /activity/i), color: '--c-disk', label: 'active' }], max: 100, fmt: v => v.toFixed(0) + '%' }));
      charts.push(new LineChart($('#pd-rw'), { series: [{ ring: R('Throughput', 'Read Rate', /read/i), color: '--c-disk', label: 'read', fmt: F.rate }, { ring: R('Throughput', 'Write Rate', /write/i), color: '--c-up', label: 'write', fill: false, fmt: F.rate }], max: 'auto', floor: 1048576, fmt: v => F.bytes(v, 0) }));
      charts.push(new LineChart($('#pd-tmp'), { series: [{ ring: R('Temperature', 'Temperature', 'Composite Temperature', /temp/i), color: '--c-temp', label: 'temp' }], max: 90, fmt: v => F.temp(v, 0) }));
    } else if (sel === 'diskall') {
      el.innerHTML = `<div class="ph"><h2>Disk</h2><span class="model">all physical disks · run as admin for per-drive SMART</span></div>
        ${chartCard('pa-act', 'Active time', lg('--c-disk', 'activity %'), '')}<div class="sec-gap"></div>${chartCard('pa-rw', 'Transfer rate', lg('--c-disk', 'read') + lg('--c-up', 'write'), '')}
        <div class="stats">${stat('Latency R / W', 'ds-lat')}${stat('Queue', 'ds-q')}${stat('IOPS', 'ds-iops')}</div>`;
      charts.push(new LineChart($('#pa-act'), { series: [{ ring: H.dact, color: '--c-disk', label: 'active' }], max: 100, fmt: v => v.toFixed(0) + '%' }));
      charts.push(new LineChart($('#pa-rw'), { series: [{ ring: H.dr, color: '--c-disk', label: 'read', fmt: F.rate }, { ring: H.dw, color: '--c-up', label: 'write', fill: false, fmt: F.rate }], max: 'auto', floor: 1048576, fmt: v => F.bytes(v, 0) }));
    } else if (sel.startsWith('nic:')) {
      const n = (t.net || []).find(x => x.id === sel.slice(4)); if (!n) { sel = 'cpu'; return build(); }
      const x = H.nic.get(n.id);
      el.innerHTML = `<div class="ph"><h2>${esc(n.name)}</h2><span class="model">${esc(n.desc)}</span></div>
        ${chartCard('pn-main', 'Throughput', lg('--c-down', 'receive') + lg('--c-up', 'send'), 'tall')}
        <div class="stats">${stat('Receive', 'ns-d')}${stat('Send', 'ns-u')}${stat('Total received', 'ns-rx')}${stat('Total sent', 'ns-tx')}${stat('Link speed', 'ns-sp')}${stat('Type', 'ns-ty', 1)}</div>
        <div class="card sec-gap"><div class="ch">Addressing</div><div class="kv" id="pn-kv"></div></div>`;
      charts.push(new LineChart($('#pn-main'), { series: [{ ring: x.down, color: '--c-down', label: 'recv', fmt: F.bits }, { ring: x.up, color: '--c-up', label: 'send', fill: false, fmt: F.bits }], max: 'auto', floor: 125000, fmt: v => F.bits(v).replace('/s', '') }));
    }
    builtFor = sel;
  }
  async function loadRam() {
    if (!ramInfo) try { ramInfo = await Host.call('sysInfo'); } catch { return; }
    const mods = ramInfo.ram || []; const e = $('#pm-mods'); if (!e) return;
    e.innerHTML = mods.map(m => `<span class="k">${esc(m.DeviceLocator || m.BankLabel)}</span><span class="v">${F.bytes(+m.Capacity, 0)} · ${m.ConfiguredClockSpeed || m.Speed} MT/s · ${esc((m.Manufacturer || '').trim())} ${esc((m.PartNumber || '').trim())}</span>`).join('')
      + `<span class="k">Slots used</span><span class="v">${mods.length}</span>`;
    const sp = mods[0] ? (mods[0].ConfiguredClockSpeed || mods[0].Speed) : ''; $('#pm-model').textContent = `${F.bytes(St.tick.mem.total, 1)} · ${sp} MT/s`;
  }
  function diskLat() {
    const y = D.sys;
    setT('ds-lat', y.dlr != null ? `${y.dlr.toFixed(2)} / ${(y.dlw || 0).toFixed(2)} ms` : '—'); setT('ds-q', y.dq != null ? String(y.dq) : '—'); setT('ds-iops', y.diops != null ? F.num(Math.round(y.diops)) : '—');
  }
  const sensKv = (el, list) => { if (el) setHTML(el, list.map(s => `<span class="k">${esc(s.n)} <span class="muted">${s.t}</span></span><span class="v">${F.sensor(s.t, s.v)} <span class="muted">(${F.sensor(s.t, s.mn)} – ${F.sensor(s.t, s.mx)})</span></span>`).join('')); };

  function tick() {
    const t = St.tick; const items = navItems();
    const sig = items.map(i => i.key).join('|'); if (sig !== navSig) { buildNav(items); navSig = sig; }
    if (!items.find(i => i.key === sel)) sel = 'cpu';
    $$('.pnav').forEach((n, i) => {
      const it = items[i]; n.classList.toggle('sel', it.key === sel);
      n.querySelector('.pn2').textContent = it.l2(); n.querySelector('.pn3').textContent = it.l3();
      spark(n.querySelector('canvas'), it.ring, it.color, it.max || null);
    });
    $('#pf-sub').textContent = `${S.history} samples @ ${S.interval}ms`;
    if (builtFor !== sel) build();
    charts.forEach(c => c.draw());
    if (sel === 'cpu') {
      setT('ps-util', F.pct(t.cpu.total)); setT('ps-spd', D.cpuClock ? F.mhz(D.cpuClock) : '—'); setT('ps-max', D.cpuClockMax ? F.mhz(D.cpuClockMax) : '—');
      setT('ps-tmp', `<span style="color:${tempColor(D.cpuTemp)}">${F.temp(D.cpuTemp)}</span>`); setT('ps-pwr', D.cpuPower != null ? D.cpuPower.toFixed(1) + ' W' : '—'); setT('ps-v', D.cpuVolt ? D.cpuVolt.toFixed(3) + ' V' : '—');
      setT('ps-proc', t.mem.procs); setT('ps-thr', F.num(t.mem.threads)); setT('ps-hnd', F.num(t.mem.handles)); setT('ps-up', F.dur(t.up)); setT('ps-lp', St.cores); setT('ps-k', F.pct(t.cpu.kernel));
      const y = D.sys; const n0 = v => v == null ? '—' : F.num(Math.round(v));
      setT('ps-eff', D.cpuEff ? F.mhz(D.cpuEff) : '—');
      setT('ps-ctx', n0(y.ctx)); setT('ps-int', n0(y.intr)); setT('ps-sysc', n0(y.sysc));
      setT('ps-dpc', y.dpc != null ? `${y.dpc.toFixed(2)}% / ${(y.irq || 0).toFixed(2)}%` : '—'); setT('ps-q', y.queue != null ? String(y.queue) : '—');
      setT('pc-main-v', F.pct(t.cpu.total)); setT('pc-clk-v', D.cpuClock ? F.mhz(D.cpuClock) : ''); setT('pc-tmp-v', F.temp(D.cpuTemp)); setT('pc-pwr-v', D.cpuPower != null ? D.cpuPower.toFixed(1) + ' W' : '');
      if (perThread) t.cpu.cores.forEach((v, i) => setT('pm-' + i, v.toFixed(0) + '%'));
      const cpuHw = hwOf('Cpu')[0]; sensKv($('#pc-sens'), cpuHw ? sensorsOf(cpuHw.id) : []);
    } else if (sel === 'mem') {
      const m = t.mem;
      setT('ms-use', F.bytes(m.used, 1)); setT('ms-av', F.bytes(m.avail, 1)); setT('ms-load', m.load + '%'); setT('ms-com', F.bytes(m.commit, 1)); setT('ms-lim', F.bytes(m.commitLimit, 1)); setT('ms-peak', F.bytes(m.commitPeak, 1));
      setT('ms-cache', F.bytes(m.cache, 1)); setT('ms-pp', F.bytes(m.paged, 0)); setT('ms-np', F.bytes(m.nonpaged, 0)); setT('ms-pf', F.bytes(Math.max(0, m.commitLimit - m.total), 1));
      setT('pm-main-v', `${F.bytes(m.used, 1)} / ${F.bytes(m.total, 0)}`); setT('pm-commit-v', `${F.bytes(m.commit, 1)} / ${F.bytes(m.commitLimit, 0)}`);
      const y = D.sys; const n0 = v => v == null ? '—' : F.num(Math.round(v));
      setT('ms-sb', y.standby != null ? F.bytes(y.standby, 1) : '—'); setT('ms-mod', y.modified != null ? F.bytes(y.modified, 1) : '—'); setT('ms-free', y.free != null ? F.bytes(y.free, 1) : '—');
      setT('ms-hf', n0(y.hard)); setT('ms-flt', n0(y.faults)); setT('ms-pio', `${n0(y.pagesIn)} / ${n0(y.pagesOut)}`);
      // Task-Manager composition when the page-list counters are available: in use | modified | standby | free
      const parts = y.standby != null && y.free != null
        ? [['In use', Math.max(0, m.used - (y.modified || 0)), '--c-mem'], ['Modified', y.modified || 0, '--warn'], ['Standby', y.standby, '--c-commit'], ['Free', y.free, '--border']]
        : [['In use', m.used, '--c-mem'], ['Cached / standby', Math.min(m.cache, m.avail), '--c-commit'], ['Free', Math.max(0, m.avail - Math.min(m.cache, m.avail)), '--border']];
      setHTML($('#pm-bar'), parts.map(p => `<i style="width:${(p[1] / m.total * 100).toFixed(2)}%;background:${p[2] === '--warn' ? 'var(--warn)' : col(p[2])}" title="${p[0]}"></i>`).join(''));
      setHTML($('#pm-leg'), parts.map(p => lg(p[2] === '--warn' ? 'var(--warn)' : p[2], `${p[0]} ${F.bytes(p[1], 1)}`)).join(''));
    } else if (sel.startsWith('gpu')) {
      const g = D.gpus[+sel.slice(3)]; if (!g) return;
      setT('gs-load', F.pct(g.load)); setT('gs-vram', g.vramUsed != null ? `${(g.vramUsed / 1024).toFixed(2)} / ${((g.vramTotal || 0) / 1024).toFixed(1)} GB` : '—');
      setT('gs-t', `<span style="color:${tempColor(g.temp)}">${F.temp(g.temp)}</span>`); setT('gs-hot', F.temp(g.hot)); setT('gs-mt', F.temp(g.memTemp)); setT('gs-p', g.power != null ? g.power.toFixed(1) + ' W' : '—');
      setT('gs-c', F.mhz(g.clock)); setT('gs-mc', F.mhz(g.memClock)); setT('gs-f', g.fan != null ? g.fan.toFixed(0) + ' rpm' + (g.fanPct != null ? ` (${g.fanPct.toFixed(0)}%)` : '') : '—'); setT('gs-vid', F.pct(g.video));
      setT('pg-load-v', F.pct(g.load)); setT('pg-vram-v', g.vramUsed != null ? (g.vramUsed / 1024).toFixed(2) + ' GB' : ''); setT('pg-temp-v', F.temp(g.temp)); setT('pg-pwr-v', g.power != null ? g.power.toFixed(0) + ' W' : '');
      sensKv($('#pg-sens'), g.sensors);
      const gp = St.procs.filter(p => p[10] > 0.1 || p[11] > 50 * 1048576).sort((a, b) => b[10] - a[10] || b[11] - a[11]).slice(0, 14);
      setHTML($('#pg-procs'), gp.map(p => `<div class="tr">${Icons.img(Meta.get(p[0]).path)}<span class="tn2">${esc(p[2])} <span class="muted mono">${p[0]}</span></span><span class="tv2">${p[10].toFixed(1)}% · ${F.bytes(p[11], 0)}</span></div>`).join('') || '<div class="muted">none</div>');
    } else if (sel.startsWith('disk:')) {
      const d = D.disks.find(x => x.id === sel.slice(5)); if (!d) return;
      setT('ds-act', F.pct(d.act)); setT('ds-r', F.rate(d.r)); setT('ds-w', F.rate(d.w)); setT('ds-t', `<span style="color:${tempColor(d.temp)}">${F.temp(d.temp)}</span>`);
      setT('ds-life', d.life != null ? d.life.toFixed(0) + '%' : '—'); setT('ds-used', F.pct(d.used)); setT('ds-rt', d.readTot != null ? F.sensor('Data', d.readTot) : '—'); setT('ds-wt', d.writeTot != null ? F.sensor('Data', d.writeTot) : '—');
      diskLat();
      setT('pd-act-v', F.pct(d.act)); setT('pd-rw-v', `R ${F.rate(d.r)} · W ${F.rate(d.w)}`); setT('pd-tmp-v', F.temp(d.temp));
      sensKv($('#pd-sens'), d.sensors);
    } else if (sel === 'diskall') {
      diskLat();
      setT('pa-act-v', D.diskAct != null ? D.diskAct.toFixed(1) + '%' : ''); setT('pa-rw-v', `R ${F.rate(D.diskR)} · W ${F.rate(D.diskW)}`);
    } else if (sel.startsWith('nic:')) {
      const n = (t.net || []).find(x => x.id === sel.slice(4)); if (!n) return;
      setT('ns-d', F.bits(n.down)); setT('ns-u', F.bits(n.up)); setT('ns-rx', F.bytes(n.rx, 2)); setT('ns-tx', F.bytes(n.tx, 2)); setT('ns-sp', n.speed > 0 ? F.bits(n.speed / 8).replace('/s', 'ps') : '—'); setT('ns-ty', n.type);
      setT('pn-main-v', `↓ ${F.bits(n.down)} · ↑ ${F.bits(n.up)}`);
      const v4 = n.ips.filter(i => !i.includes(':')), v6 = n.ips.filter(i => i.includes(':'));
      setHTML($('#pn-kv'), [['IPv4', v4.join(', ')], ['IPv6', v6.join(', ')], ['Gateway', n.gw.join(', ')], ['DNS', n.dns.join(', ')], ['MAC', n.mac], ['Adapter', n.desc]]
        .map(r => `<span class="k">${r[0]}</span><span class="v">${esc(r[1] || '—')}</span>`).join(''));
    }
  }
  $('#pf-cores-toggle').onclick = () => { perThread = !perThread; builtFor = null; if (St.tick) tick(); };
  return { tick, select, show() { builtFor = null; } };
})();
