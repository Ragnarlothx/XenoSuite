/* Tiny canvas charts — scrolling oscilloscope-style grid, gradient fills, crosshair tooltips. No libs. */
'use strict';
const Charts = { tick: 0, colors: {} };
function refreshColors() {
  Charts.colors = {};
  ['--c-cpu', '--c-mem', '--c-disk', '--c-down', '--c-up', '--c-gpu', '--c-gpu2', '--c-temp', '--c-commit', '--grid', '--gridtxt', '--accent', '--accent2', '--err', '--warn', '--ok', '--fg3', '--border', '--bg2']
    .forEach(k => Charts.colors[k] = css(k));
}
/* any other --token resolves lazily (before: '--accent2' / '--err' fell through as literal strings and the
   canvas threw on addColorStop, killing the CPU-power and GPU-hot-spot charts on every tick) */
const col = k => Charts.colors[k] || (typeof k === 'string' && k.startsWith('--') ? (Charts.colors[k] = css(k) || '#888') : k);
function alpha(c, a) {
  if (c.startsWith('#')) {
    const n = c.length === 4 ? c.slice(1).split('').map(x => x + x).join('') : c.slice(1, 7);
    return `rgba(${parseInt(n.slice(0, 2), 16)},${parseInt(n.slice(2, 4), 16)},${parseInt(n.slice(4, 6), 16)},${a})`;
  }
  return c;
}
/* Canvas sizes come from a ResizeObserver cache instead of clientWidth/clientHeight: reading those after the
   tick has written new text into the DOM forced a synchronous layout per canvas (40+ per tick on Overview). */
const CvSize = new WeakMap(); const cvObserved = new Set();
const cvRO = new ResizeObserver(es => { for (const e of es) CvSize.set(e.target, { w: e.contentRect.width, h: e.contentRect.height }); });
setInterval(() => { for (const cv of cvObserved) if (!cv.isConnected) { cvRO.unobserve(cv); cvObserved.delete(cv); CvSize.delete(cv); } }, 30000);
function fit(cv) {
  let sz = CvSize.get(cv);
  if (!sz) { sz = { w: cv.clientWidth, h: cv.clientHeight }; CvSize.set(cv, sz); cvRO.observe(cv); cvObserved.add(cv); }
  const dpr = window.devicePixelRatio || 1, w = sz.w, hh = sz.h;
  if (!w || !hh) return null;
  const pw = Math.round(w * dpr), ph = Math.round(hh * dpr);
  if (cv.width !== pw || cv.height !== ph) { cv.width = pw; cv.height = ph; }
  const ctx = cv._ctx || (cv._ctx = cv.getContext('2d')); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, w, hh);
  return { ctx, w, h: hh };
}

class LineChart {
  /* opts: series:[{ring, color, label, fill=true, width}], max:number|'auto', fmt, yLabels=true, grid=true, unit */
  constructor(canvas, opts) {
    this.cv = canvas; this.o = Object.assign({ max: 100, fmt: v => v.toFixed(0), yLabels: true, grid: true }, opts);
    this.hx = null;
    canvas.addEventListener('mousemove', e => { const r = canvas.getBoundingClientRect(); this.hx = e.clientX - r.left; this.hy = e.clientY; this.draw(); });
    canvas.addEventListener('mouseleave', () => { this.hx = null; $('#tip').classList.add('hidden'); this.draw(); });
  }
  scaleMax() {
    const o = this.o; if (typeof o.max === 'number') return o.max;
    let m = 0; for (const s of o.series) m = Math.max(m, s.ring.max());
    if (o.max === 'auto') { if (m <= 0) return o.floor || 1; const p = Math.pow(10, Math.floor(Math.log10(m))); return Math.max(o.floor || 0, Math.ceil(m * 1.1 / p) * p); }
    return typeof o.max === 'function' ? o.max(m) : m;
  }
  draw() {
    const f = fit(this.cv); if (!f) return; const { ctx, w, h } = f; const o = this.o;
    const padL = o.yLabels ? 44 : 0, padT = 6, padB = 4, cw = w - padL - 1, ch = h - padT - padB;
    const max = this.scaleMax() || 1; const n = o.series[0] ? o.series[0].ring.n : 60;
    // grid
    if (o.grid) {
      ctx.strokeStyle = col('--grid'); ctx.lineWidth = 1; ctx.beginPath();
      for (let i = 0; i <= 4; i++) { const y = Math.round(padT + ch * i / 4) + .5; ctx.moveTo(padL, y); ctx.lineTo(w, y); }
      const step = Math.max(5, Math.round(n / 12)), off = Charts.tick % step;
      for (let k = n - 1 - off; k >= 0; k -= step) { const x = Math.round(padL + cw * k / (n - 1)) + .5; ctx.moveTo(x, padT); ctx.lineTo(x, padT + ch); }
      ctx.stroke();
      ctx.strokeStyle = col('--border'); ctx.strokeRect(padL + .5, padT + .5, cw - .5, ch);
    }
    if (o.yLabels) {
      ctx.fillStyle = col('--gridtxt'); ctx.font = '10px Cascadia Mono, Consolas, monospace'; ctx.textAlign = 'right'; ctx.textBaseline = 'middle';
      for (let i = 0; i <= 4; i++) ctx.fillText(o.fmt(max * (4 - i) / 4), padL - 5, padT + ch * i / 4);
    }
    // series
    for (const s of o.series) {
      const c = col(s.color); const r = s.ring;
      const pts = []; for (let k = 0; k < n; k++) { const v = r.get(k); if (v != null) pts.push([padL + cw * k / (n - 1), padT + ch - clamp(v / max, 0, 1) * ch]); }
      if (pts.length < 2) continue;
      if (s.fill !== false) {
        const g = ctx.createLinearGradient(0, padT, 0, padT + ch); g.addColorStop(0, alpha(c, .32)); g.addColorStop(1, alpha(c, .02));
        ctx.fillStyle = g; ctx.beginPath(); ctx.moveTo(pts[0][0], padT + ch); pts.forEach(p => ctx.lineTo(p[0], p[1])); ctx.lineTo(pts[pts.length - 1][0], padT + ch); ctx.closePath(); ctx.fill();
      }
      ctx.strokeStyle = c; ctx.lineWidth = s.width || 1.5; ctx.lineJoin = 'round'; ctx.beginPath();
      pts.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1])); ctx.stroke();
      if (s.dash) ctx.setLineDash([]);
    }
    // crosshair
    if (this.hx != null && this.hx >= padL) {
      const k = Math.round((this.hx - padL) / cw * (n - 1)); const x = padL + cw * k / (n - 1);
      ctx.strokeStyle = alpha(col('--accent'), .6); ctx.setLineDash([3, 3]); ctx.beginPath(); ctx.moveTo(x + .5, padT); ctx.lineTo(x + .5, padT + ch); ctx.stroke(); ctx.setLineDash([]);
      const ago = Math.round((n - 1 - k) * S.interval / 1000);
      let html = `<div class="muted">-${ago}s</div>`;
      for (const s of o.series) { const v = s.ring.get(k); html += `<div><i style="background:${col(s.color)}"></i>${esc(s.label || '')} <b>${v == null ? '—' : (s.fmt || o.fmt)(v)}</b></div>`; }
      const tip = $('#tip'); tip.innerHTML = html; tip.classList.remove('hidden');
      const r = this.cv.getBoundingClientRect(); place(tip, r.left + x + 12, this.hy - 10);
    }
  }
}

/* sparkline: no axes, optional fill */
function spark(cv, ring, color, max, fill = true) {
  const f = fit(cv); if (!f) return; const { ctx, w, h } = f; const n = ring.n;
  const m = max || ring.max() || 1; const c = col(color);
  ctx.beginPath(); let started = false, first = 0, lastX = 0;
  for (let k = 0; k < n; k++) {
    const v = ring.get(k); if (v == null) continue;
    const x = w * k / (n - 1), y = h - 1 - clamp(v / m, 0, 1) * (h - 2);
    if (!started) { ctx.moveTo(x, y); started = true; first = x; } else ctx.lineTo(x, y); lastX = x;
  }
  if (!started) return;
  ctx.strokeStyle = c; ctx.lineWidth = 1.2; ctx.stroke();
  if (fill) { ctx.lineTo(lastX, h); ctx.lineTo(first, h); ctx.closePath(); ctx.fillStyle = alpha(c, .18); ctx.fill(); }
}

/* temperature → color (cool blue → green → amber → red) */
function tempColor(t) {
  if (t == null) return col('--fg3');
  if (t < 45) return '#4f9cf9'; if (t < 60) return '#4ec98a'; if (t < 75) return '#cca700'; if (t < 85) return '#f0a63a'; return '#f14c4c';
}
function loadColor(p) { if (p < 50) return col('--c-cpu'); if (p < 80) return '#cca700'; return '#f14c4c'; }
