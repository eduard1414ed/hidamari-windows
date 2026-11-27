// The card's colours by the sky of the hour (SkyPalette.swift, as on the site): the card's and the support window's
// (update 1.4, design/support/: the same three sets of colours and the same switch of the text's colour).
'use strict';
window.HidamariSky = (() => {
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

return { SKY, INK, GLASS, skyAt, rgba };
})();
