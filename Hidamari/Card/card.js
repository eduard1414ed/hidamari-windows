// The card of the Windows app: what it shows and what a click on it asks the app to do.
// It behaves as the Mac app's panel (Panel.swift, CardParts.swift, SunArc.swift, SkyPalette.swift, WallPicker.swift,
// SettingsView.swift, AppModel.swift); the arc, the sky colours, the film, the plants and the weather by hand are
// copied from the site's working panel (site/src/page.js), which is left as it is.
//
// The app (Card.cs) is the one that keeps the settings and draws the wall. It sends the whole state with
// chrome.webview.postMessage({ type: 'state', … }) whenever anything changed; the card shows it at once and tells the
// app what was chosen. Nothing here is kept by the page itself.
//
// Given by the app before this script runs: window.HidamariStrings (Strings.cs) and window.HidamariColour (the sun
// and colour maths of wall.html, taken out of that page as it is).
'use strict';
(() => {

const S = window.HidamariStrings;
const C = window.HidamariColour;
const host = window.chrome && window.chrome.webview;
const post = (m) => { if (host) host.postMessage(m); };
const $ = (id) => document.getElementById(id);
const el = (tag, cls, attrs = {}) => { const e = document.createElement(tag); if (cls) e.className = cls; for (const k in attrs) e.setAttribute(k, attrs[k]); return e; };
const attr = (e, k, v) => { v = String(v); if (e.getAttribute(k) !== v) e.setAttribute(k, v); };
const text = (e, v) => { if (e.textContent !== v) e.textContent = v; };
const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

// ---------- The state, as the app last sent it ----------
/** Everything the card shows. `shown`: the moment of the frame on the main screen (ms); `now`: the real moment. */
let A = {
  scene: 0, wall: 0, custom: { L: 0.88, x: 0.5, y: 0.8, k: 0.1, rough: 1, shade: null }, plant: 2,
  follow: true, pinned: 720, weather: 'clear',
  screens: 'all', desktops: 'all', desktopsOffered: false, windowOnRight: false, login: false, firstAnswered: true,
  shown: Date.now(), now: Date.now(), place: { name: 'Berlin', country: 'Germany', lat: 52.52, lng: 13.405, tz: 'Europe/Berlin' },
  placeMode: 'automatic', locationUnavailable: false, live: true, locationButton: false,
  credit: { weather: '', places: '' },
  ownThumbs: 0, glass: 'plain', anchor: 'bottom', insets: { left: 0, top: 0, right: 0, bottom: 0 }, check: false,
};
/** What only the open card knows. */
const ui = { dragging: false, pickerOpen: false, settingsOpen: false };
/** The city search: what is typed, the lines the app found for it, the line Enter picks (0 the current location,
 *  1… the cities, null none), and the number of the last letter sent, so older answers are dropped. */
const search = { open: false, query: '', results: [], highlight: null, id: 0 };
let firstState = true;

// ---------- Time at the place (site/src/page.js) ----------
const formats = new Map();
function offsetAt(tz, ms) {
  let f = formats.get(tz);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', { timeZone: tz, hourCycle: 'h23', year: 'numeric', month: 'numeric', day: 'numeric', hour: 'numeric', minute: 'numeric', second: 'numeric' });
    formats.set(tz, f);
  }
  const p = {};
  for (const x of f.formatToParts(new Date(ms))) p[x.type] = +x.value;
  return Math.round((Date.UTC(p.year, p.month - 1, p.day, p.hour % 24, p.minute, p.second) - Math.floor(ms / 1000) * 1000) / 60000);
}
const pad2 = (n) => String(n).padStart(2, '0');
const hhmm = (m) => { m = ((Math.floor(m) % 1440) + 1440) % 1440; return pad2(Math.floor(m / 60)) + ':' + pad2(m % 60); };
const minutesOf = (ms, tz) => (((ms / 60000 + offsetAt(tz, ms)) % 1440) + 1440) % 1440;

/** The moment the slider stands for, as the Mac's AppModel.sliderMoment: the frame's moment while following the sun,
 *  otherwise the pinned time of that day. */
function moment() {
  const p = A.place, tz = p.tz, base = A.shown || A.now;
  const baseMin = minutesOf(base, tz), midnight = base - baseMin * 60000;
  const min = A.follow ? baseMin : A.pinned, ms = A.follow ? base : midnight + A.pinned * 60000;
  return { p, ms, min, nowMin: minutesOf(A.now, tz), midnight };
}
const sunAltitude = (ms, lat, lng) => C.sky(+ms, +lat, +lng).alt / C.RAD;

// ---------- Card colours: the sky of the hour (SkyPalette.swift, as on the site) ----------
const hex = (h, a = 1) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16), a];
const SKY = {
  day: { stops: [[[178, 198, 232, 0.86], 0], [[214, 218, 226, 0.80], 0.46], [[246, 214, 184, 0.86], 1]], glow: hex('#ffb45e'), line: hex('#fff6e4'), halo: [255, 178, 84, 0.85], tint: hex('#ffd27a'), sw: [hex('#f0a457'), hex('#f6c76f')], ink: 'dark' },
  golden: { stops: [[[110, 112, 186, 0.88], 0], [[196, 134, 152, 0.84], 0.48], [[246, 168, 108, 0.90], 1]], glow: hex('#ff8e4a'), line: hex('#fff1dc'), halo: [255, 128, 64, 0.9], tint: hex('#ffb35c'), sw: [hex('#f0a457'), hex('#f6c76f')], ink: 'white' },
  night: { stops: [[[10, 15, 36, 0.90], 0], [[22, 31, 62, 0.88], 0.55], [[40, 52, 88, 0.88], 1]], glow: hex('#7f97ff'), line: hex('#cfd9ff'), halo: [150, 175, 255, 0.9], tint: hex('#e6edff'), sw: [hex('#7f93d8'), hex('#aebdf2')], ink: 'light' },
};
const INK = { dark: { text: [29, 33, 48], soft: 0.55 }, white: { text: [255, 255, 255], soft: 0.72 }, light: { text: [238, 241, 251], soft: 0.55 } };
/** How opaque the sky's colours are: as in the design over the system's blur or without blur; the site's glass is
 *  0.55 of that, made for a blurred wall behind it. Plain is the chosen one. */
const GLASS = { acrylic: 1, plain: 1, site: 0.55 };
const mixC = (a, b, t) => a.map((v, i) => v + (b[i] - v) * t);
function mixSky(a, b, t) {
  return { stops: a.stops.map((s, i) => [mixC(s[0], b.stops[i][0], t), s[1] + (b.stops[i][1] - s[1]) * t]), glow: mixC(a.glow, b.glow, t), line: mixC(a.line, b.line, t),
    halo: mixC(a.halo, b.halo, t), tint: mixC(a.tint, b.tint, t), sw: [mixC(a.sw[0], b.sw[0], t), mixC(a.sw[1], b.sw[1], t)], ink: a.ink };
}
const lum = (c) => 0.2126729 * Math.pow(c[0] / 255, 2.4) + 0.7151522 * Math.pow(c[1] / 255, 2.4) + 0.0721750 * Math.pow(c[2] / 255, 2.4);
function apca(t, b) { const tl = lum(t), bl = lum(b); return bl > tl ? (Math.pow(bl, 0.56) - Math.pow(tl, 0.57)) * 1.14 : (Math.pow(tl, 0.62) - Math.pow(bl, 0.65)) * 1.14; }
function skyAt(h) {
  if (h >= 12) return SKY.day;
  if (h <= -6) return SKY.night;
  if (h >= 2) { const p = mixSky(SKY.golden, SKY.day, (h - 2) / 10), mid = p.stops[1][0]; p.ink = apca(INK.dark.text, mid) >= apca(INK.white.text, mid) ? 'dark' : 'white'; return p; }
  const t = (h + 6) / 8, p = mixSky(SKY.night, SKY.golden, t); p.ink = t >= 0.5 ? 'white' : 'light'; return p;
}
const rgba = (c, a) => `rgba(${Math.round(c[0])},${Math.round(c[1])},${Math.round(c[2])},${(+(a ?? (c[3] ?? 1))).toFixed(3)})`;

// ---------- The arc of the sun (SunArc.swift, 236×112; site/src/page.js) ----------
const ARC = { left: 4, span: 228, middle: 52, amp: 36 };
const RIM = 0.833;
function arcFor(midnight, p) {
  const alt = (m) => sunAltitude(midnight + m * 60000, p.lat, p.lng) + RIM;
  let rise = null, set = null, top = 0, best = -Infinity;
  let m0 = 0, a0 = alt(0);
  for (let m1 = 10; m1 <= 1440; m1 += 10) {
    const a1 = alt(m1);
    if ((a0 < 0) !== (a1 < 0)) {
      let lo = m0, hi = m1;
      while (hi - lo > 0.5 / 60) { const mid = (lo + hi) / 2; if ((alt(mid) < 0) === (a0 < 0)) lo = mid; else hi = mid; }
      if (a0 < 0) { if (rise === null) rise = hi; } else if (set === null) set = hi;
    }
    if (a1 > best) { best = a1; top = m1; }
    m0 = m1; a0 = a1;
  }
  let noon, len;
  if (rise !== null && set !== null && set > rise) { noon = (rise + set) / 2; len = set - rise; }
  else { noon = top; len = rise !== null && rise < noon ? 2 * (noon - rise) : set !== null && set > noon ? 2 * (set - noon) : (best >= 0 ? 1440 : 0); }
  const horizon = ARC.middle - ARC.amp * Math.cos(Math.PI * Math.min(Math.max(len, 0), 1440) / 1440);
  const x = (m) => ARC.left + ARC.span * Math.min(Math.max(m, 0), 1440) / 1440;
  const y = (m) => ARC.middle - ARC.amp * Math.cos(2 * Math.PI * (m - noon) / 1440);
  const below = [[]], above = [[]];
  let last = null;
  for (let i = 0; i <= 240; i++) {
    const m = i * 6, pt = [x(m), y(m)], up = pt[1] <= horizon;
    if (last && last.up !== up) {
      const t = (horizon - last.pt[1]) / (pt[1] - last.pt[1]), c = [last.pt[0] + (pt[0] - last.pt[0]) * t, horizon];
      (last.up ? above : below)[(last.up ? above : below).length - 1].push(c);
      (up ? above : below).push([c]);
    }
    (up ? above : below)[(up ? above : below).length - 1].push(pt);
    last = { pt, up };
  }
  const pts = (list) => list.filter((l) => l.length > 1).map((l) => l.map((q) => q[0].toFixed(1) + ',' + q[1].toFixed(1)).join(' '));
  return { rise, set, horizon, x, y, below: pts(below), above: pts(above) };
}

// ---------- Scenes, plants, walls, weather ----------
const SCENES = [0, 1, 2, 3];               // 0 dapples, 1 window, 2 blinds, 3 water
const SCENE_KEYS = ['dapples', 'window', 'blinds', 'water'];
const PLANTS = [0, 2, 5];
const OWN = -1;
const WEATHER = ['clear', 'cloudy', 'rain', 'frost'];
const WEATHER_LABELS = [S.weatherClear, S.weatherCloudy, S.weatherRain, S.weatherFrost];
/** Rain and frost are drawn only in Window; elsewhere only clear and cloudy are offered (ManualWeather.choices). */
const choices = (scene) => scene === 1 ? WEATHER : ['clear', 'cloudy'];
const shownWeather = (scene) => choices(scene).includes(A.weather) ? A.weather : 'cloudy';
/** A picture of the scenes or plants on the chosen wall: the app's own for the approved walls (made when it is built,
 *  as the site's), the ones drawn for the own wall (OwnWallThumbs on the Mac) from the app's folder. */
const thumb = (name) => A.wall === OWN ? `https://own.hidamari/${name}.jpg?v=${A.ownThumbs}` : `img/thumbs/${name}-${A.wall}.jpg`;
const sceneThumb = (sc) => thumb(sc === 1 ? `window-${A.plant}` : SCENE_KEYS[sc]);
/** Colour of each approved wall in the shade, as the prototype paints its colour buttons (wall.html, wallShades). */
const WALL_SHADES = C.WALLS.map((w) => C.linHex(C.hexLin(w.c).map((v, k) => v * w.amb[k])));

// ---------- Colour of the own wall (the prototype's maths, from wall.html) ----------
const copyWall = (c) => ({ L: c.L, x: c.x, y: c.y, k: c.k, rough: c.rough, shade: c.shade ? { x: c.shade.x, y: c.shade.y } : null });
/** The own wall as it starts from approved wall `i`: the prototype's customFrom, the texture kept (AppModel). */
function customFrom(i, rough) {
  const keep = copyWall(C.custom);
  C.customFrom(i);
  const out = copyWall(C.custom); out.rough = rough;
  Object.assign(C.custom, keep);
  return out;
}
/** The wall's colours as drawn: in full sun (lin) and in the shade (amb). */
function wallColors() {
  if (A.wall !== OWN) return { lin: C.hexLin(C.WALLS[A.wall].c), amb: C.WALLS[A.wall].amb };
  const keep = copyWall(C.custom);
  Object.assign(C.custom, copyWall(A.custom));
  const w = C.customWall();
  Object.assign(C.custom, keep);
  return w;
}
/** What the picker shows: the own wall, or where the chosen approved wall lies on the field. */
const pickerWall = () => A.wall === OWN ? A.custom : customFrom(A.wall, A.custom.rough);

// ---------- Building the card ----------
const card = $('card'), arcBox = $('arc'), slot = $('slot');
const SVGNS = 'http://www.w3.org/2000/svg';
function icon(viewBox, size, inner, strokeWidth) {
  const s = document.createElementNS(SVGNS, 'svg');
  s.setAttribute('width', size); s.setAttribute('height', size); s.setAttribute('viewBox', viewBox); s.setAttribute('aria-hidden', 'true');
  s.setAttribute('class', 'line-icon'); if (strokeWidth) s.setAttribute('stroke-width', strokeWidth);
  s.innerHTML = inner;
  return s;
}
const halfText = (b, t) => { const s = el('span'); s.textContent = t; b.textContent = ''; b.appendChild(s); };

$('followTitle').textContent = S.followSun;
$('liveTitle').textContent = S.liveWeather;
$('followSwitch').setAttribute('aria-label', S.followSun);
$('liveSwitch').setAttribute('aria-label', S.liveWeather);
arcBox.setAttribute('aria-label', S.timeOfDay);
$('quit').textContent = S.quit;
$('gear').setAttribute('aria-label', S.settings);
$('settingsBack').setAttribute('aria-label', S.back);
$('loginTitle').textContent = S.launchAtLogin;
$('loginSwitch').setAttribute('aria-label', S.launchAtLogin);
$('screensTitle').textContent = S.screens;
$('screensHalves').setAttribute('aria-label', S.screens);
halfText($('screensAll'), S.all);
halfText($('screensMain'), S.mainScreen);
$('desktopsTitle').textContent = S.desktops;
$('desktopsHalves').setAttribute('aria-label', S.desktops);
halfText($('desktopsAll'), S.all);
halfText($('desktopsThis'), S.thisDesktop);
$('lightTitle').textContent = S.lightFrom;
$('lightHalves').setAttribute('aria-label', S.lightFrom);
halfText($('lightLeft'), S.lightLeft);
halfText($('lightRight'), S.lightRight);
$('firstQuestion').textContent = S.firstLaunchQuestion;
halfText($('firstNotNow'), S.notNow);
halfText($('firstOpen'), S.openAtLogin);
$('wave').setAttribute('aria-label', S.texture);
$('dial').setAttribute('aria-label', S.lightStrength);
$('pad').setAttribute('aria-label', S.wallHandle);
$('shadeOff').setAttribute('aria-label', S.defaultShade);
$('shadeOn').setAttribute('aria-label', S.ownShade);
$('searchBack').setAttribute('aria-label', S.back);
$('searchInput').setAttribute('placeholder', S.searchCity);
$('searchInput').setAttribute('aria-label', S.searchCity);

/** Tells the app what was chosen; the card shows it at once, the app answers with the whole state. */
function choose(change) {
  Object.assign(A, change);
  render();
  post({ type: 'set', change });
}

const wallButtons = WALL_SHADES.map((c, i) => {
  const b = el('button', 'wall-dot', { type: 'button', 'aria-label': S.walls[i] });
  b.style.background = c;
  b.addEventListener('click', () => { if (A.wall !== i) choose({ wall: i }); });
  $('walls').appendChild(b);
  return b;
});
const ownButton = el('button', 'wall-dot own', { type: 'button', 'aria-label': S.ownWall });
ownButton.addEventListener('click', () => { ui.pickerOpen = !ui.pickerOpen; post({ type: 'picker', open: ui.pickerOpen }); render(); });
$('walls').appendChild(ownButton);

const sceneButtons = SCENES.map((sc) => {
  const b = el('button', 'scene' + (sc === 1 || sc === 2 ? ' left' : ''), { type: 'button', 'aria-label': S.scenes[sc] });
  const img = el('img', '', { alt: '', decoding: 'async', draggable: 'false' });
  const name = el('span', 'scene-name', { 'aria-hidden': 'true' }); name.textContent = S.scenes[sc];
  b.append(img, name);
  b.addEventListener('click', () => { if (A.scene !== sc) choose({ scene: sc }); });
  $('film').appendChild(b);
  return { b, img };
});

const plantButtons = PLANTS.map((pl) => {
  const b = el('button', 'plant', { type: 'button', 'aria-label': S.plants[pl] });
  const img = el('img', '', { alt: '', decoding: 'async', draggable: 'false' });
  b.appendChild(img);
  b.addEventListener('click', () => { if (A.plant !== pl) choose({ plant: pl }); });
  $('plants').appendChild(b);
  return { b, img };
});

const WEATHER_ICONS = [
  '<path d="M12.1 9A3.1 3.1 0 1 1 5.9 9A3.1 3.1 0 1 1 12.1 9Z M14.1 9L16 9M12.61 12.61L13.95 13.95M9 14.1L9 16M5.39 12.61L4.05 13.95M3.9 9L2 9M5.39 5.39L4.05 4.05M9 3.9L9 2M12.61 5.39L13.95 4.05"/>',
  '<path d="M5.2 14h7.6a3.2 3.2 0 0 0 .4-6.4A4.6 4.6 0 0 0 4.4 8.6 2.7 2.7 0 0 0 5.2 14z"/>',
  '<path d="M5.2 12.2h7.6a3.0 3.0 0 0 0 .4-6A4.4 4.4 0 0 0 4.6 7.2 2.5 2.5 0 0 0 5.2 12.2z M6.4 14.2l-.8 2M9.2 14.2l-.8 2M12 14.2l-.8 2"/>',
  '<path d="M9 9L16 9M13.4 9L14.94 10.43M13.4 9L14.94 7.57M9 9L12.5 15.06M11.2 12.81L10.73 14.86M11.2 12.81L13.21 13.43M9 9L5.5 15.06M6.8 12.81L4.79 13.43M6.8 12.81L7.27 14.86M9 9L2 9M4.6 9L3.06 7.57M4.6 9L3.06 10.43M9 9L5.5 2.94M6.8 5.19L7.27 3.14M6.8 5.19L4.79 4.57M9 9L12.5 2.94M11.2 5.19L13.21 4.57M11.2 5.19L10.73 3.14"/>',
];
const weatherButtons = WEATHER.map((w, i) => {
  const slot = el('span', 'weather-slot' + (i === 0 ? ' lead' : ''));
  const b = el('button', 'weather-icon', { type: 'button', 'aria-label': WEATHER_LABELS[i] });
  b.appendChild(icon('0 0 18 18', 14, WEATHER_ICONS[i], i === 3 ? 1.2 : 1.5));
  b.addEventListener('click', () => { if (shownWeather(A.scene) !== w || A.weather !== w) choose({ weather: w }); });
  slot.appendChild(b);
  $('weather').appendChild(slot);
  return { slot, b };
});

// Live sun: off pins the time the wall shows now; on, the wall goes back to the real moment
$('followSwitch').addEventListener('click', () => {
  if (A.follow) choose({ follow: false, pinned: Math.round(moment().min) });
  else choose({ follow: true });
});
// Live weather: on, the weather of the place; off, the weather by hand
$('liveSwitch').addEventListener('click', () => choose({ live: !A.live }));
// where the weather and the cities come from: their pages in the browser
$('creditsMet').addEventListener('click', () => post({ type: 'openLink', to: 'met' }));
$('creditsGeo').addEventListener('click', () => post({ type: 'openLink', to: 'geonames' }));

// ---------- The city search (PlaceSearch.swift) ----------
const searchInput = $('searchInput'), searchRows = $('searchRows');
const LOCATION_ARROW = '<path d="M12.6 1.4 1.6 6.2l4.8 1.4 1.4 4.8z"/>';
$('place').addEventListener('click', () => openSearch());
$('searchBack').addEventListener('click', () => closeSearch());
function openSearch() {
  if (ui.settingsOpen) return;
  Object.assign(search, { open: true, query: '', results: [], highlight: null });
  searchInput.value = '';
  post({ type: 'searchOpen', open: true });
  render();
  searchInput.focus({ preventScroll: true });
}
function closeSearch(focusPlace = true) {
  if (!search.open) return;
  search.open = false;
  post({ type: 'searchOpen', open: false });
  render();
  if (focusPlace) $('place').focus({ preventScroll: true });
}
searchInput.addEventListener('input', () => {
  search.query = searchInput.value;
  search.id++;
  if (search.query.trim() === '') { search.results = []; search.highlight = null; renderRows(); return; }
  post({ type: 'search', text: search.query, id: search.id });
});
searchInput.addEventListener('keydown', (e) => {
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); move(e.key === 'ArrowDown' ? 1 : -1); }
  else if (e.key === 'Enter') { e.preventDefault(); pick(search.highlight); }
});
/** The arrow keys: through the current location and the cities. */
function move(step) {
  const count = 1 + search.results.length;
  const from = search.highlight ?? (step > 0 ? -1 : count);
  search.highlight = clamp(from + step, 0, count - 1);
  renderRows();
}
function pick(line) {
  if (line === null || line === undefined) return;
  if (line === 0) post({ type: 'currentLocation' });
  else if (search.results[line - 1]) {
    const r = search.results[line - 1];
    // the card shows the city at once; the app answers with its time and its sun
    A.place = Object.assign({}, A.place, { name: r.title });
    post({ type: 'choosePlace', index: r.index });
  } else return;
  closeSearch();
}
let rowsKey = '';
function renderRows() {
  const key = JSON.stringify([search.results, A.locationUnavailable && A.locationButton]);
  if (key !== rowsKey) {
    rowsKey = key;
    searchRows.textContent = '';
    const row = (i, title, subtitle, arrow) => {
      const b = el('button', 'search-row', { type: 'button' });
      const slotIcon = el('span', 'slot-icon');
      if (arrow) slotIcon.appendChild(icon('0 0 14 14', 13, LOCATION_ARROW));
      const t = el('span', 'title'); t.textContent = title;
      b.append(slotIcon, t);
      if (subtitle) { const s = el('span', 'subtitle'); s.textContent = subtitle; b.appendChild(s); }
      b.setAttribute('aria-label', subtitle ? `${title}, ${subtitle}` : title);
      b.addEventListener('click', () => pick(i));
      // the pointer does not move the highlight: the keyboard's line stays where it was
      b.addEventListener('pointerdown', (e) => e.preventDefault());
      searchRows.appendChild(b);
    };
    row(0, S.currentLocation, null, true);
    if (A.locationUnavailable && A.locationButton) {
      const b = el('button', 'location-settings', { type: 'button' });
      b.textContent = S.openLocationSettings;
      b.addEventListener('click', () => post({ type: 'locationSettings' }));
      searchRows.appendChild(b);
    }
    search.results.forEach((r, i) => row(i + 1, r.title, r.subtitle, false));
  }
  const rows = [...searchRows.querySelectorAll('.search-row')];
  rows.forEach((b, i) => b.classList.toggle('lit', i === search.highlight));
  if (search.highlight !== null && rows[search.highlight]) rows[search.highlight].scrollIntoView({ block: 'nearest' });
}

$('quit').addEventListener('click', () => post({ type: 'quit' }));
$('gear').addEventListener('click', () => { closeSearch(false); ui.settingsOpen = true; post({ type: 'settings', open: true }); render(); $('settingsBack').focus({ preventScroll: true }); });
$('settingsBack').addEventListener('click', () => closeSettings());
function closeSettings() { ui.settingsOpen = false; post({ type: 'settings', open: false }); render(); }

$('loginSwitch').addEventListener('click', () => post({ type: 'login', on: !A.login }));
$('screensAll').addEventListener('click', () => { if (A.screens !== 'all') choose({ screens: 'all' }); });
$('screensMain').addEventListener('click', () => { if (A.screens !== 'main') choose({ screens: 'main' }); });
$('lightLeft').addEventListener('click', () => { if (A.windowOnRight) choose({ windowOnRight: false }); });
$('lightRight').addEventListener('click', () => { if (!A.windowOnRight) choose({ windowOnRight: true }); });
$('desktopsAll').addEventListener('click', () => { if (A.desktops !== 'all') choose({ desktops: 'all' }); });
$('desktopsThis').addEventListener('click', () => { if (A.desktops !== 'thisOne') choose({ desktops: 'thisOne' }); });
$('firstNotNow').addEventListener('click', () => { A.firstAnswered = true; render(); post({ type: 'firstLaunch', openAtLogin: false }); });
$('firstOpen').addEventListener('click', () => { A.firstAnswered = true; render(); post({ type: 'firstLaunch', openAtLogin: true }); });

// ---------- The arc: click, drag, double click, arrow keys ----------
function pin(minutes, dragging) {
  const m = clamp(Math.round(minutes), 0, 1439);
  if (!A.follow && A.pinned === m && dragging) return;
  A.follow = false; A.pinned = m;
  render();
  post({ type: 'pin', minutes: m, dragging });
}
function minutesAt(e) {
  const r = arcBox.getBoundingClientRect(), f = (e.clientX - r.left) / r.width;
  return ((f * 236 - ARC.left) / ARC.span) * 1440;
}
let lastPin = null, lastDown = { t: -1e9, x: 0, y: 0 };
// a double click told by the app (see onApp): presses at its place for a moment after it do not pin the time, in
// whichever order they reach the page (on a busy machine the presses can come after the app's word)
let doubleAt = null;
const nearDouble = (e) => doubleAt && performance.now() < doubleAt.until && Math.abs(e.clientX - doubleAt.x) < 8 && Math.abs(e.clientY - doubleAt.y) < 8;
arcBox.addEventListener('pointerdown', (e) => {
  if (e.button !== 0) return;
  e.preventDefault();
  arcBox.focus({ preventScroll: true });
  if (nearDouble(e)) return;
  // the second press of a double click follows the sun again; it does not pin the time first (the window's own
  // mouse handling does not always turn the pair into a dblclick for the page)
  const t = e.timeStamp, again = t - lastDown.t < 500 && Math.hypot(e.clientX - lastDown.x, e.clientY - lastDown.y) < 6;
  lastDown = again ? { t: -1e9, x: 0, y: 0 } : { t, x: e.clientX, y: e.clientY };
  if (again) { if (!A.follow) choose({ follow: true }); return; }
  arcBox.setPointerCapture?.(e.pointerId);
  ui.dragging = true;
  lastPin = minutesAt(e);
  pin(lastPin, true);
});
arcBox.addEventListener('pointermove', (e) => { if (ui.dragging) { lastPin = minutesAt(e); pin(lastPin, true); } });
const endDrag = () => {
  if (!ui.dragging) return;
  ui.dragging = false;
  // the first press of a double click lets go after the app's word: the sun stays followed
  if (doubleAt && performance.now() < doubleAt.until) { render(); return; }
  // the last place goes on the wall at once (stage 2, section 3)
  // the place under the hand when it let go, not whatever the app said back during the drag
  if (lastPin !== null) { A.follow = false; A.pinned = clamp(Math.round(lastPin), 0, 1439); }
  post({ type: 'pin', minutes: A.pinned, dragging: false });
  render();
};
arcBox.addEventListener('pointerup', endDrag);
arcBox.addEventListener('pointercancel', endDrag);
arcBox.addEventListener('lostpointercapture', endDrag);
// a double click follows the sun again, as on the Mac
arcBox.addEventListener('dblclick', () => { if (!A.follow) choose({ follow: true }); });
arcBox.addEventListener('keydown', (e) => {
  const keys = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1 };
  if (!(e.key in keys)) return;
  e.preventDefault();
  pin(moment().min + keys[e.key] * (e.shiftKey ? 60 : 5), false);
});

// ---------- The own-wall picker (WallPicker.swift; the prototype's picker) ----------
const pad = $('pad'), padDots = $('padDots'), hLight = $('hLight'), hShade = $('hShade');
const STEP = 0.02;
let padTarget = 'light', padDrawn = '';
/** Changes the own wall (AppModel.editOwnWall). An approved wall first becomes an own wall of its colour. */
function editOwn(finished, edit) {
  let startedFrom = null, next = copyWall(A.custom);
  if (A.wall !== OWN) { startedFrom = A.wall; next = customFrom(A.wall, A.custom.rough); }
  edit(next);
  next.x = clamp(next.x, 0, 1); next.y = clamp(next.y, 0, 1); next.k = clamp(next.k, 0, 1); next.rough = clamp(next.rough, 0, 2);
  if (next.shade) next.shade = { x: clamp(next.shade.x, 0, 1), y: clamp(next.shade.y, 0, 1) };
  const same = JSON.stringify(next) === JSON.stringify(A.custom);
  if (same && startedFrom === null && !finished) return;
  A.custom = next; A.wall = OWN;
  render();
  post({ type: 'custom', custom: next, startedFrom, finished });
}
const local = (e, box) => { const r = box.getBoundingClientRect(); return [clamp((e.clientX - r.left) / r.width, 0, 1), clamp((e.clientY - r.top) / r.height, 0, 1)]; };
function dragOn(box, start, move, end) {
  let active = false;
  box.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    e.preventDefault(); box.setPointerCapture?.(e.pointerId); box.focus({ preventScroll: true });
    active = true; start?.(e); move(e, false);
  });
  box.addEventListener('pointermove', (e) => { if (active) move(e, false); });
  const stop = (e) => { if (!active) return; active = false; end(e); };
  box.addEventListener('pointerup', stop);
  box.addEventListener('pointercancel', stop);
}
// a press on a handle moves that handle; a press on the empty field moves the nearer one there
dragOn(pad, (e) => {
  const view = pickerWall(), shade = A.wall === OWN ? A.custom.shade : null;
  const r = pad.getBoundingClientRect(), px = e.clientX - r.left, py = e.clientY - r.top;
  if (!shade) { padTarget = 'light'; return; }
  if (Math.hypot(px - view.x * r.width, py - view.y * r.height) <= 13) padTarget = 'light';
  else if (Math.hypot(px - shade.x * r.width, py - shade.y * r.height) <= 9) padTarget = 'shade';
  else { const [fx, fy] = local(e, pad); padTarget = Math.hypot(fx - shade.x, fy - shade.y) < Math.hypot(fx - view.x, fy - view.y) ? 'shade' : 'light'; }
}, (e) => {
  const [x, y] = local(e, pad);
  editOwn(false, (c) => { if (padTarget === 'shade' && c.shade) c.shade = { x, y }; else { c.x = x; c.y = y; } });
}, () => editOwn(true, () => {}));
for (const b of [$('shadeOff'), $('shadeOn')]) b.addEventListener('pointerdown', (e) => e.stopPropagation());
$('shadeOff').addEventListener('click', () => { if ($('shadeOff').getAttribute('aria-disabled') === 'true') return; padTarget = 'light'; editOwn(true, (c) => { c.shade = null; }); });
$('shadeOn').addEventListener('click', () => { if ($('shadeOn').getAttribute('aria-disabled') === 'true') return; padTarget = 'shade'; editOwn(true, (c) => { c.shade = { x: c.x, y: clamp(c.y + 0.25, 0, 1) }; }); });

const WAVE = { x0: 10, x1: 190, scale: 0.95 };
const waveValue = (e) => { const r = $('wave').getBoundingClientRect(); return 2 * clamp(((e.clientX - r.left) / WAVE.scale - WAVE.x0) / (WAVE.x1 - WAVE.x0), 0, 1); };
dragOn($('wave'), null, (e) => editOwn(false, (c) => { c.rough = waveValue(e); }), (e) => editOwn(true, (c) => { c.rough = waveValue(e); }));
const DIAL = { a0: -135, a1: 135, ticks: 25 };
const dialValue = (e) => {
  const r = $('dial').getBoundingClientRect(), a = Math.atan2(e.clientX - r.left - 32, -(e.clientY - r.top - 32)) * 180 / Math.PI;
  return clamp((clamp(a, DIAL.a0, DIAL.a1) - DIAL.a0) / (DIAL.a1 - DIAL.a0), 0, 1);
};
dragOn($('dial'), null, (e) => editOwn(false, (c) => { c.k = dialValue(e); }), (e) => editOwn(true, (c) => { c.k = dialValue(e); }));
// keyboard: the arrows move whatever has the focus, one step 2 % (the prototype)
const delta = (k) => ({ ArrowLeft: [-STEP, 0], ArrowRight: [STEP, 0], ArrowUp: [0, -STEP], ArrowDown: [0, STEP] })[k];
pad.addEventListener('keydown', (e) => {
  const d = delta(e.key); if (!d) return; e.preventDefault();
  editOwn(true, (c) => { if (padTarget === 'shade' && c.shade) c.shade = { x: c.shade.x + d[0], y: c.shade.y + d[1] }; else { c.x += d[0]; c.y += d[1]; } });
});
$('wave').addEventListener('keydown', (e) => { const d = delta(e.key); if (!d) return; e.preventDefault(); editOwn(true, (c) => { c.rough += 2 * (d[0] - d[1]); }); });
$('dial').addEventListener('keydown', (e) => { const d = delta(e.key); if (!d) return; e.preventDefault(); editOwn(true, (c) => { c.k += d[0] - d[1]; }); });

/** The field of dots: across, hue with warm colours in the middle; down, from full colour to grey; fading towards
 *  its edges as the design's mask does (WallPicker.swift, PadDots). */
function drawDots(L) {
  const r = pad.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
  const key = `${r.width}x${r.height}@${dpr}:${L.toFixed(4)}`;
  if (!r.width || key === padDrawn) return;
  padDrawn = key;
  padDots.width = Math.round(r.width * dpr); padDots.height = Math.round(r.height * dpr);
  const g = padDots.getContext('2d'), w = r.width, h = r.height;
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  for (let y = 5.5; y < h; y += 11) for (let x = 5.5; x < w; x += 11) {
    const dx = (x - w / 2) / (0.75 * w), dy = (y - h / 2) / (0.72 * h), d = Math.hypot(dx, dy);
    const alpha = d <= 0.6 ? 1 : Math.max(0, 1 - (d - 0.6) / 0.4);
    if (alpha <= 0) continue;
    const hue = C.padHue(x / w), cc = (1 - y / h) * C.chromaCap(L, hue);
    g.globalAlpha = alpha;
    g.fillStyle = C.linHex(C.lch2lin(L - 0.12, cc * 1.4, hue).map((v) => clamp(v, 0, 1)));
    g.beginPath(); g.arc(x, y, 1.7, 0, 2 * Math.PI); g.fill();
  }
  g.globalAlpha = 1;
}
function inkColour() { return getComputedStyle(card).getPropertyValue('--ink').trim() || '#1d2130'; }
function drawWave(rough, ink) {
  const t = rough / 2, kx = WAVE.x0 + t * (WAVE.x1 - WAVE.x0);
  let d = `M${WAVE.x0} 24`;
  for (let x = WAVE.x0; x <= kx; x += 1.5) d += ` L${x.toFixed(1)} ${(24 - Math.sin((x - WAVE.x0) / 5.2) * 13 * t * Math.min(1, (x - WAVE.x0) / 30)).toFixed(2)}`;
  const off = (48 - 48 * WAVE.scale) / 2;
  $('waveSvg').innerHTML = `<g transform="translate(0 ${off}) scale(${WAVE.scale})">
    <line x1="${kx}" y1="24" x2="${WAVE.x1}" y2="24" stroke="${ink}" stroke-opacity="0.18" stroke-width="5" stroke-linecap="round"/>
    <path d="${d}" fill="none" stroke="${ink}" stroke-opacity="0.6" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round"/>
    <rect x="${kx - 9}" y="7" width="18" height="34" rx="9" fill="#fff" stroke="rgba(0,0,0,0.12)"/></g>`;
  attr($('wave'), 'aria-valuenow', Math.round(t * 100));
}
function drawDial(k, ink) {
  const R = Math.PI / 180, a = (DIAL.a0 + k * (DIAL.a1 - DIAL.a0)) * R;
  let dots = '';
  for (let i = 0; i < DIAL.ticks; i++) {
    const f = i / (DIAL.ticks - 1), b = (DIAL.a0 + f * (DIAL.a1 - DIAL.a0)) * R, on = f <= k + 1e-6;
    dots += `<circle cx="${(32 + 28 * Math.sin(b)).toFixed(2)}" cy="${(32 - 28 * Math.cos(b)).toFixed(2)}" r="${on ? 2.1 : 1.5}" fill="${ink}" fill-opacity="${on ? 0.72 : 0.22}"/>`;
  }
  // the pointer stands on the white face: always in the day's dark colour (WallPicker.swift)
  $('dialSvg').innerHTML = `${dots}<circle cx="32" cy="32" r="19" fill="#fff" stroke="rgba(0,0,0,0.12)"/>
    <line x1="${(32 + 9 * Math.sin(a)).toFixed(2)}" y1="${(32 - 9 * Math.cos(a)).toFixed(2)}" x2="${(32 + 16 * Math.sin(a)).toFixed(2)}" y2="${(32 - 16 * Math.cos(a)).toFixed(2)}" stroke="#1d2130" stroke-width="3" stroke-linecap="round"/>`;
  attr($('dial'), 'aria-valuenow', Math.round(k * 100));
}
function renderPicker() {
  if (!ui.pickerOpen) return;
  const view = pickerWall(), own = A.wall === OWN, shade = own ? A.custom.shade : null, W = wallColors(), ink = inkColour();
  drawDots(view.L);
  const r = pad.getBoundingClientRect();
  hLight.style.left = `${view.x * r.width}px`; hLight.style.top = `${view.y * r.height}px`;
  hLight.style.background = C.linHex(W.lin);
  hShade.hidden = !shade;
  if (shade) {
    hShade.style.left = `${shade.x * r.width}px`; hShade.style.top = `${shade.y * r.height}px`;
    hShade.style.background = C.linHex(W.lin.map((v, i) => v * W.amb[i] * 1.6));
  }
  attr($('shadeOff'), 'aria-disabled', !shade);
  attr($('shadeOn'), 'aria-disabled', !!shade);
  drawWave(A.custom.rough, ink);
  drawDial(view.k, ink);
}

// ---------- Escape, and a click beside the card ----------
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  e.preventDefault();
  // the city search first, then the settings, then the wall picker, then the card (Panel.swift)
  if (search.open) closeSearch();
  else if (ui.settingsOpen) closeSettings();
  else if (ui.pickerOpen) { ui.pickerOpen = false; post({ type: 'picker', open: false }); render(); }
  else post({ type: 'close', why: 'Escape' });
});
// the window is a little larger than the card, for its shadow: a press there is a press beside the card
document.addEventListener('pointerdown', (e) => { if (!card.contains(e.target)) post({ type: 'close', why: 'click outside' }); }, true);

// ---------- Drawing ----------
let arcKey = '', arc = null, inkNow = '', foldsInstant = true;
const css = {};
function setVar(name, value) { if (css[name] !== value) { css[name] = value; card.style.setProperty(name, value); } }
const sunG = $('sun'), sunCore = $('sunCore'), realSun = $('realSun'), drop = $('drop');
const folds = [
  { outer: $('firstFold'), inner: $('first'), open: () => !A.firstAnswered },
  { outer: $('plantsFold'), inner: $('plants'), open: () => A.scene === 1 },
  { outer: $('pickerFold'), inner: $('picker'), open: () => ui.pickerOpen },
];

function render() {
  const t = moment(), p = t.p;
  // where the card stands in the window and how it looks there
  slot.className = 'slot ' + (A.anchor === 'top' ? 'top' : 'bottom');
  const ins = A.insets || {};
  Object.assign(slot.style, { left: (ins.left || 0) + 'px', right: (ins.right || 0) + 'px', top: A.anchor === 'top' ? (ins.top || 0) + 'px' : '', bottom: A.anchor === 'top' ? '' : (ins.bottom || 0) + 'px' });
  card.classList.toggle('shadow', A.glass !== 'acrylic');

  const key = p.tz + '|' + p.lat + '|' + p.lng + '|' + Math.round(t.midnight / 60000);
  if (key !== arcKey) {
    arcKey = key; arc = arcFor(t.midnight, p);
    attr($('horizon'), 'y1', arc.horizon.toFixed(1)); attr($('horizon'), 'y2', arc.horizon.toFixed(1));
    attr($('below1'), 'points', arc.below[0] || ''); attr($('below2'), 'points', arc.below[1] || '');
    attr($('aboveGlow'), 'points', arc.above[0] || ''); attr($('aboveLine'), 'points', arc.above[0] || '');
    const labelY = (arc.horizon - 5).toFixed(1);
    attr($('riseText'), 'x', arc.rise !== null ? (arc.x(arc.rise) - 6).toFixed(1) : 0); attr($('riseText'), 'y', labelY);
    attr($('setText'), 'x', arc.set !== null ? (arc.x(arc.set) + 6).toFixed(1) : 0); attr($('setText'), 'y', labelY);
    text($('riseText'), arc.rise !== null ? hhmm(Math.round(arc.rise)) : '');
    text($('setText'), arc.set !== null ? hhmm(Math.round(arc.set)) : '');
  }
  // colours of the sky of the moment the wall shows, also while the sun is dragged by hand
  const h = sunAltitude(t.ms, p.lat, p.lng), sky = skyAt(h), ink = INK[sky.ink], glass = GLASS[A.glass] ?? 1;
  setVar('--ink', rgba(ink.text, 1));
  setVar('--soft', rgba(ink.text, ink.soft));
  setVar('--card-bg', `linear-gradient(172deg, ${sky.stops.map((x) => rgba(x[0], x[0][3] * glass) + ' ' + (x[1] * 100).toFixed(1) + '%').join(', ')})`);
  setVar('--switch-on', `linear-gradient(90deg, ${rgba(sky.sw[0], 1)}, ${rgba(sky.sw[1], 1)})`);
  setVar('--arc-glow', rgba(sky.glow, 1));
  setVar('--arc-line', rgba(sky.line, 1));
  setVar('--halo', rgba(sky.halo));
  setVar('--tint', rgba(sky.tint, 1));
  const onDark = sky.ink !== 'dark';
  card.classList.toggle('on-dark', onDark);
  // the system's blur under the card is light or dark to suit the card's colours, never the system's look
  if (inkNow !== sky.ink) { inkNow = sky.ink; post({ type: 'ink', dark: onDark }); }

  // the top of the card
  text($('placeName'), p.name || S.currentLocation);
  attr($('place'), 'aria-label', `${S.location}: ${p.name || S.currentLocation}` + (A.placeMode === 'automatic' && A.locationUnavailable ? `, ${S.locationUnavailable}` : ''));
  card.classList.toggle('searching', search.open);
  $('search').hidden = !search.open;
  attr($('top').querySelector('.top-left'), 'aria-hidden', search.open);
  attr($('walls'), 'aria-hidden', search.open);
  if (search.open) renderRows();
  text($('time'), hhmm(t.min));
  attr(arcBox, 'aria-valuenow', Math.floor(t.min));
  attr(arcBox, 'aria-valuetext', hhmm(t.min));
  const sx = arc.x(t.min), sy = arc.y(t.min);
  attr(sunG, 'transform', `translate(${sx.toFixed(1)} ${sy.toFixed(1)})`);
  attr(drop, 'y2', (110 - sy).toFixed(1));
  attr(sunCore, 'r', ui.dragging ? 9 : 5.5);
  attr(sunCore, 'stroke-width', ui.dragging ? 2.5 : 2);
  arcBox.classList.toggle('dragging', ui.dragging);
  // while the sun is not followed, an empty ring stays where the real sun is now
  if (A.follow) attr(realSun, 'display', 'none');
  else { attr(realSun, 'display', 'inline'); attr(realSun, 'cx', arc.x(t.nowMin).toFixed(1)); attr(realSun, 'cy', arc.y(t.nowMin).toFixed(1)); }
  wallButtons.forEach((b, i) => attr(b, 'aria-pressed', i === A.wall));
  attr(ownButton, 'aria-pressed', A.wall === OWN);
  attr(ownButton, 'aria-label', ui.pickerOpen ? `${S.ownWall}, ${S.open}` : S.ownWall);

  // scenes, plants, switches, weather
  sceneButtons.forEach(({ b, img }, sc) => { attr(b, 'aria-pressed', sc === A.scene); b.classList.toggle('mirrored', !!A.windowOnRight && (sc === 1 || sc === 2)); const src = sceneThumb(sc); if (img.getAttribute('src') !== src) img.src = src; });
  plantButtons.forEach(({ b, img }, i) => { attr(b, 'aria-pressed', PLANTS[i] === A.plant); b.classList.toggle('mirrored', !!A.windowOnRight); const src = thumb(`plant-${PLANTS[i]}`); if (img.getAttribute('src') !== src) img.src = src; });
  attr($('followSwitch'), 'aria-checked', A.follow);
  attr($('liveSwitch'), 'aria-checked', !!A.live);
  $('weather').classList.toggle('live', !!A.live);
  // where the weather and the cities come from: in the settings only
  const cr = A.credit || {};
  text($('creditsMet'), cr.weather || ''); text($('creditsGeo'), cr.places || '');
  const offered = choices(A.scene), shown = shownWeather(A.scene);
  weatherButtons.forEach(({ slot: s, b }, i) => {
    const w = WEATHER[i];
    s.classList.toggle('gone', !offered.includes(w));
    attr(b, 'aria-pressed', w === shown);
    attr(b, 'tabindex', offered.includes(w) ? 0 : -1);
    attr(s, 'aria-hidden', !offered.includes(w));
  });

  // the settings and the first launch
  card.classList.toggle('settings-open', ui.settingsOpen);
  $('settings').hidden = !ui.settingsOpen;
  attr($('main'), 'aria-hidden', ui.settingsOpen);
  attr($('loginSwitch'), 'aria-checked', !!A.login);
  attr($('screensAll'), 'aria-pressed', A.screens !== 'main');
  attr($('screensMain'), 'aria-pressed', A.screens === 'main');
  attr($('lightLeft'), 'aria-pressed', !A.windowOnRight);
  attr($('lightRight'), 'aria-pressed', !!A.windowOnRight);
  $('desktopsGroup').hidden = !A.desktopsOffered;
  $('desktopsLine').hidden = !A.desktopsOffered;
  attr($('desktopsAll'), 'aria-pressed', A.desktops !== 'thisOne');
  attr($('desktopsThis'), 'aria-pressed', A.desktops === 'thisOne');
  attr($('firstNotNow'), 'aria-pressed', false);
  attr($('firstOpen'), 'aria-pressed', true);       // Open at login is the suggested answer: the light half

  // the folds: their height is set here, so the card's new height is known before it gets there
  if (foldsInstant) card.classList.add('instant');
  for (const f of folds) {
    const open = f.open(), want = open ? f.inner.offsetHeight : 0;
    f.outer.classList.toggle('open', open);
    if (foldsInstant) f.outer.style.transition = 'none';
    f.outer.style.height = want + 'px';
    attr(f.outer, 'aria-hidden', !open);
    f.outer.inert = !open;
  }
  if (foldsInstant) {
    void card.offsetHeight;
    for (const f of folds) f.outer.style.transition = '';
    foldsInstant = false;
    requestAnimationFrame(() => requestAnimationFrame(() => card.classList.remove('instant')));
  }
  renderPicker();
  reportSize();
}

// ---------- The card's size, for its window ----------
/** The card's height now and once its folds have settled: the window grows at once and shrinks afterwards. */
function reportSize() {
  const now = card.getBoundingClientRect().height;
  let target = now;
  for (const f of folds) target += parseFloat(f.outer.style.height || '0') - f.outer.getBoundingClientRect().height;
  post({ type: 'size', now: Math.round(now * 100) / 100, target: Math.round(target * 100) / 100 });
  if (A.check) layoutSoon();
}
new ResizeObserver(() => reportSize()).observe(card);

// ---------- For the checks on GitHub: where each control is, so the check can click it like a person ----------
let layoutTimer = 0;
function layoutSoon() { clearTimeout(layoutTimer); layoutTimer = setTimeout(layout, 400); }
function layout() {
  const r = (e) => { const b = e.getBoundingClientRect(); return [b.left, b.top, b.width, b.height].map((v) => Math.round(v * 100) / 100); };
  const searchLines = [...searchRows.querySelectorAll('.search-row')];
  const out = { card: r(card), arc: r(arcBox), pad: r(pad), place: r($('place')), searchBack: r($('searchBack')), searchInput: r(searchInput),
    creditsMet: r($('creditsMet')), creditsGeo: r($('creditsGeo')), locationSettings: searchRows.querySelector('.location-settings') ? r(searchRows.querySelector('.location-settings')) : null,
    search: { open: search.open, query: search.query, highlight: search.highlight, focused: document.activeElement === searchInput,
      lines: searchLines.map((b) => ({ text: b.getAttribute('aria-label'), rect: r(b), lit: b.classList.contains('lit') })) }, wave: r($('wave')), dial: r($('dial')), follow: r($('followSwitch')), live: r($('liveSwitch')),
    gear: r($('gear')), quit: r($('quit')), back: r($('settingsBack')), login: r($('loginSwitch')),
    screensAll: r($('screensAll')), screensMain: r($('screensMain')), lightLeft: r($('lightLeft')), lightRight: r($('lightRight')), desktopsAll: r($('desktopsAll')), desktopsThis: r($('desktopsThis')),
    firstNotNow: r($('firstNotNow')), firstOpen: r($('firstOpen')), own: r(ownButton), shadeOn: r($('shadeOn')), shadeOff: r($('shadeOff')),
    arcGeometry: ARC, picker: ui.pickerOpen, settings: ui.settingsOpen, time: $('time').textContent,
    checked: { follow: A.follow, login: !!A.login, screensMain: A.screens === 'main', lightRight: !!A.windowOnRight,
      mirroredScenes: sceneButtons.map(({ b }, i) => b.classList.contains('mirrored') ? i : -1).filter((i) => i >= 0).join(','),
      mirroredPlants: plantButtons.every(({ b }) => b.classList.contains('mirrored')), desktopsThis: A.desktops === 'thisOne',
      desktopsOffered: !!A.desktopsOffered, first: !A.firstAnswered, ink: inkNow, weather: shownWeather(A.scene),
      scene: A.scene, wall: A.wall, plant: A.plant, ownThumbs: A.ownThumbs, glass: A.glass, live: !!A.live,
      weatherIcons: !$('weather').classList.contains('live'), place: $('placeName').textContent, placeLabel: $('place').getAttribute('aria-label') } };
  sceneButtons.forEach(({ b }, i) => { out[`scene${i}`] = r(b); });
  plantButtons.forEach(({ b }, i) => { out[`plant${PLANTS[i]}`] = r(b); });
  wallButtons.forEach((b, i) => { out[`wall${i}`] = r(b); });
  weatherButtons.forEach(({ b }, i) => { out[`weather-${WEATHER[i]}`] = r(b); });
  post({ type: 'layout', rects: out });
}

// ---------- News from the app ----------
function onApp(m) {
  if (!m || typeof m !== 'object') return;
  if (m.type === 'dblclick') {
    // a double click as Windows counts it: on the arc, the sun is followed again
    const r = arcBox.getBoundingClientRect(), onArc = m.x >= r.left && m.x <= r.right && m.y >= r.top && m.y <= r.bottom;
    post({ type: 'dblclickSeen', onArc, x: m.x, y: m.y, arc: [r.left, r.top, r.width, r.height].map(Math.round) });
    if (onArc) {
      doubleAt = { x: m.x, y: m.y, until: performance.now() + 2000 };
      ui.dragging = false;
      if (!A.follow) choose({ follow: true });
    }
    return;
  }
  if (m.type === 'state') {
    const was = { place: A.place && A.place.tz }, hand = { follow: A.follow, pinned: A.pinned };
    A = Object.assign({}, A, m.state);
    if (m.reset) {
      // the card opens anew: the settings and the picker closed, nothing animates into place
      ui.pickerOpen = false; ui.settingsOpen = false; ui.dragging = false; foldsInstant = true; padDrawn = '';
      if (search.open) closeSearch(false);
    }
    // the hand wins while it drags: the app's word can be about an earlier place of the drag
    if (ui.dragging) { A.follow = hand.follow; A.pinned = hand.pinned; }
    if (was.place !== A.place.tz) arcKey = '';
    render();
    if (m.reset) requestAnimationFrame(() => requestAnimationFrame(() => post({ type: 'painted', id: m.id })));
  } else if (m.type === 'found') {
    // an answer for letters typed since is dropped: the newer one follows
    if (!search.open || m.id !== search.id) return;
    search.results = m.items || [];
    // the first city is what Enter picks; with no cities, nothing is highlighted
    search.highlight = search.results.length > 0 ? 1 : null;
    renderRows();
    if (A.check) layoutSoon();
  } else if (m.type === 'focus') {
    arcBox.blur();
  }
}
if (host) host.addEventListener('message', (e) => onApp(e.data));
// for trying the page in a browser: ?demo shows it with the starting state
if (!host || /[?&]demo\b/.test(location.search)) window.HidamariCard = { show: (s) => onApp({ type: 'state', state: s, reset: true, id: 0 }), found: (items) => onApp({ type: 'found', id: search.id, items }) };
document.fonts.ready.then(() => { padDrawn = ''; render(); post({ type: 'ready' }); });
render();
})();
