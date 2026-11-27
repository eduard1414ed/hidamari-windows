// The support window's page (SupportWindow.swift): what it shows and what a click on it asks the app to do. The app
// (SupportWindow.cs) sends { type: 'state', state: { now, place, version, insets, glass, check } }; the card takes the
// colours of the sky at that place now, as the card by the tray icon does, and follows the sky while it stays open.
//
// Given by the app before this script runs: window.HidamariStrings (Strings.cs), window.HidamariColour (the sun maths
// of wall.html); sky.js gives the sky's colours.
'use strict';
(() => {

const S = window.HidamariStrings;
const C = window.HidamariColour;
const K = window.HidamariSky;
const host = window.chrome && window.chrome.webview;
const post = (m) => { if (host) host.postMessage(m); };
const $ = (id) => document.getElementById(id);

let A = { now: Date.now(), place: { lat: 52.52, lng: 13.405 }, version: '', glass: 'plain', insets: { left: 0, top: 0, right: 0, bottom: 0 }, check: false };
// when the app's `now` came: the card's moment runs on from there
let at = performance.now();
const card = $('card'), slot = $('slot');

$('version').textContent = S.supportVersion.replace('{0}', '');
$('heading').textContent = S.supportHeading;
$('text').textContent = S.supportText;
$('supportLabel').textContent = S.supportButton;
$('thanks').textContent = S.supportThanks;
$('done').textContent = S.supportDone;
card.setAttribute('aria-label', S.supportHeading);

// the button opens Ed's donation page and closes the window; Done and Escape close it
$('support').addEventListener('click', () => post({ type: 'support' }));
$('done').addEventListener('click', () => post({ type: 'done', why: 'Done' }));
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); post({ type: 'done', why: 'Escape' }); } });

const css = {};
function setVar(name, value) { if (css[name] !== value) { css[name] = value; card.style.setProperty(name, value); } }

function render() {
  const ms = A.now + (performance.now() - at), p = A.place || {};
  const h = C.sky(ms, +p.lat, +p.lng).alt / C.RAD, sky = K.skyAt(h), ink = K.INK[sky.ink], glass = K.GLASS[A.glass] ?? 1;
  const dark = sky.ink !== 'dark';
  setVar('--ink', K.rgba(ink.text, 1));
  setVar('--soft', K.rgba(ink.text, ink.soft));
  // the text under the heading, the thanks and Done: the text colour, fainter (SupportView)
  setVar('--text-soft', K.rgba(ink.text, dark ? 0.75 : 0.72));
  setVar('--text-faint', K.rgba(ink.text, 0.5));
  setVar('--text-done', K.rgba(ink.text, dark ? 0.8 : 0.75));
  setVar('--card-bg', `linear-gradient(172deg, ${sky.stops.map((x) => K.rgba(x[0], x[0][3] * glass) + ' ' + (x[1] * 100).toFixed(1) + '%').join(', ')})`);
  card.classList.toggle('on-dark', dark);
  $('version').textContent = S.supportVersion.replace('{0}', A.version || '');
  const ins = A.insets || {};
  Object.assign(slot.style, { left: (ins.left || 0) + 'px', top: (ins.top || 0) + 'px', right: (ins.right || 0) + 'px', bottom: '' });
  slot.className = 'slot top';
  reportSize();
}

/** The card's height for its window, and where the card and its buttons are: a press elsewhere on the card drags it. */
function reportSize() {
  const r = (e) => { const b = e.getBoundingClientRect(); return [b.left, b.top, b.width, b.height].map((v) => Math.round(v * 100) / 100); };
  post({ type: 'size', height: Math.round(card.getBoundingClientRect().height * 100) / 100, card: r(card), buttons: [r($('support')), r($('done'))] });
  if (A.check) post({ type: 'layout', rects: { card: r(card), support: r($('support')), done: r($('done')), version: $('version').textContent,
    ink: dark() ? 'light' : 'dark' } });
}
const dark = () => card.classList.contains('on-dark');
new ResizeObserver(() => reportSize()).observe(card);

// the sky moves on while the window is open; once a minute is enough for a card
setInterval(render, 60000);

function onApp(m) {
  if (!m || typeof m !== 'object') return;
  if (m.type === 'state') {
    A = Object.assign({}, A, m.state);
    at = performance.now();
    render();
    if (m.reset) requestAnimationFrame(() => requestAnimationFrame(() => post({ type: 'painted', id: m.id })));
  } else if (m.type === 'shown') {
    document.body.classList.add('shown');
  }
}
if (host) host.addEventListener('message', (e) => onApp(e.data));
// for trying the page in a browser
if (!host) window.HidamariSupport = { show: (s) => { onApp({ type: 'state', state: s, reset: true, id: 0 }); onApp({ type: 'shown' }); } };
document.fonts.ready.then(() => { render(); post({ type: 'ready' }); });
render();
})();
