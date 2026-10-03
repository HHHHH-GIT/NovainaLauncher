'use strict';
const viewer = new skinview3d.SkinViewer({ canvas: document.getElementById('skin'), width: innerWidth, height: innerHeight });
viewer.zoom = .8;
viewer.controls.enablePan = false;
viewer.controls.enableDamping = false;
viewer.controls.minDistance = 30;
viewer.controls.maxDistance = 110;
viewer.camera.position.set(30, 18, 60);
viewer.animation = new skinview3d.IdleAnimation();
viewer.animation.speed = .55;
viewer.renderPaused = true;
let active = false, animated = false, texture = '', model = '', revision = 0;
window.skinLoad = viewer.loadSkin('steve.png', { model: 'default' }).then(() => renderPolicy());
function renderPolicy() {
  viewer.renderPaused = !active || !animated;
  if (active) viewer.render();
}
viewer.controls.addEventListener('change', () => { if (active && viewer.renderPaused) viewer.render(); });
new ResizeObserver(() => { viewer.setSize(innerWidth, innerHeight); if (active) viewer.render(); }).observe(document.body);
chrome.webview.addEventListener('message', async event => {
  const state = event.data;
  if (state.reset) { viewer.camera.position.set(30, 18, 60); viewer.controls.target.set(0, 0, 0); viewer.controls.update(); if (active) viewer.render(); return; }
  active = state.active;
  animated = state.animated;
  viewer.animation.speed = state.calm ? .55 : .75;
  renderPolicy();
  if (state.texture && (texture !== state.texture || model !== state.model)) {
    texture = state.texture; model = state.model;
    const own = ++revision;
    try {
      // Serialize loads so an older image cannot replace a newer account after asynchronous decode.
      await (window.skinLoad = (window.skinLoad || Promise.resolve()).catch(() => {}).then(async () => {
        if (own !== revision) return;
        await viewer.loadSkin(state.texture, { model: state.model });
        if (own === revision) renderPolicy();
      }));
    } catch { chrome.webview.postMessage({ error: '皮肤渲染失败' }); }
  }
});
chrome.webview.postMessage({ ready: true });
