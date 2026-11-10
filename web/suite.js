/* XenoSuite shared tab bar — injected into Monitor, Disk and Audit pages. */
(function () {
  'use strict';
  const page = location.pathname.indexOf('/disk/') >= 0 ? 'disk' : location.pathname.indexOf('/audit/') >= 0 ? 'audit' : 'mon';
  function nav(to) { if (to === page) return; try { window.chrome.webview.postMessage({ id: 0, method: 'suiteNav', args: { to } }); } catch (e) { } }
  function build() {
    const mb = document.getElementById('menubar');
    if (!mb) return setTimeout(build, 120);
    if (document.getElementById('suite-tabs')) return;
    const css = document.createElement('style');
    css.textContent = '#suite-tabs{display:inline-flex;gap:2px;margin:0 8px;padding-left:8px;border-left:1px solid var(--border,#2e3035)}'
      + '.suite-tab{padding:3px 13px;border-radius:5px;cursor:pointer;color:var(--fg2,#a0a3a8);font-weight:600;font-size:12px;letter-spacing:.3px}'
      + '.suite-tab:hover{background:var(--hover,#2c2e33);color:var(--fg,#d4d4d4)}'
      + '.suite-tab.on{background:var(--accent,#4f9cf9);color:#fff}';
    document.head.appendChild(css);
    const tabs = document.createElement('span'); tabs.id = 'suite-tabs';
    [['mon', 'Monitor'], ['disk', 'Disk'], ['audit', 'Audit']].forEach(function (t) {
      const b = document.createElement('span'); b.className = 'suite-tab' + (t[0] === page ? ' on' : ''); b.textContent = t[1]; b.onclick = function () { nav(t[0]); }; tabs.appendChild(b);
    });
    const b2 = mb.querySelector('.brand2');
    if (b2 && b2.nextSibling) mb.insertBefore(tabs, b2.nextSibling); else mb.appendChild(tabs);
    const b1 = mb.querySelector('.brand'); if (b1) b1.textContent = 'XENO';
    if (b2) b2.textContent = 'SUITE';
    document.title = 'XenoSuite';
  }
  if (document.readyState !== 'loading') build(); else document.addEventListener('DOMContentLoaded', build);
})();
