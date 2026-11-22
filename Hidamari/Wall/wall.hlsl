// The prototype's fragment shader (prototype/index.html, const FRAG) in HLSL, one to one with the Mac app's Metal copy
// (Sources/Hidamari/Resources/wall.metal in the Mac app's repository): the same formulas, numbers and order of
// operations, line by line. Nothing is "improved". The app compiles this file when the Direct3D drawing starts (D3DWall.cs).
//
// Differences that are only language:
//   - gl_FragCoord counts rows from the bottom; SV_Position from the top, so fc.y is turned over (as in Metal).
//   - GLSL's mod() is x - y * floor(x / y); HLSL's fmod() truncates, so glsl_mod() is used.
//   - fract is frac, mix is lerp, textureLod(m, uv, lod) is m.SampleLevel(s, uv, lod).
//   - GLSL's mat2(a, b, c, d) is column by column and m * v; HLSL's float2x2(a, b, c, d) is row by row, so m * v is
//     mul(v, m) here (the same numbers), and m * m * v is mul(v, mul(m, m)).
//   - The uniforms come in one block (WallUniforms), ints as floats, and are copied into globals named as in GLSL, so
//     the functions below read like the prototype's.
//   - The frame may be drawn into a part of a larger picture (a DirectComposition surface): its corner is subtracted.
//
// Mac update 1.1: the live motion of prototype/live-motion.html (uT, uWind, uWindLeaves; the "wind" block in crown(),
// the phase ph in caustic()), the same lines. With all three at 0 the picture is exactly the still one.
// Mac update 1.3: uMirror (Light from: Right) draws the frame as its mirror image, the same line as in wall.html.

cbuffer WallUniforms : register(b0)
{
    float4 resWallHTex;         // uRes.xy, uWallH, uTex
    float4 srcCornerPhaseCalm;  // uSrc, uCorner, uPhase, uCalm
    float4 depthWT;             // uDepth.xyz, uWT
    float4 plantRectU;          // uPlantRect
    float4 l1Rough;             // uL1.xyz, uRough
    float4 c1FrostBottom;       // uC1.xyz, uFrostBottom
    float4 win1U;               // uWin1
    float4 wallScene;           // uWall.xyz, uScene
    float4 ambLayers;           // uAmb.xyz, uLayers
    float4 weatherMotion;       // uWeather, uT, uWind, uWindLeaves
    float4 mirrorOrigin;        // uMirror, the frame's corner in the target (x, y pixels), unused
};

Texture2D uM0 : register(t0);
Texture2D uM1 : register(t1);
Texture2D uPlant : register(t2);
Texture2D uFrost : register(t3);
SamplerState sRepeat : register(s0);
SamplerState sClamp : register(s1);

static float2 uRes; static float uWallH, uTex, uSrc, uCorner, uPhase, uCalm;
static float3 uDepth; static int uLayers, uWeather; static float uWT;
static float4 uPlantRect; static int uScene;
static float3 uL1, uC1; static float4 uWin1; static float3 uWall, uAmb; static float uRough;
static float uFrostBottom;
static float uT, uWind, uWindLeaves;    // live motion: seconds, and wind strength 0..1 (0 = the approved still picture)

static const float SUN_R = 0.00465;    // angular radius of the sun disc
#define N 24

float4 wallVertex(uint vid : SV_VertexID) : SV_Position
{
    // the prototype's one big triangle: (-1,-1), (3,-1), (-1,3)
    const float2 a[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    return float4(a[vid], 0.0, 1.0);
}

float glsl_mod(float x, float y) { return x - y * floor(x / y); }

float ign(float2 p){ return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }
float hash(float2 p){ p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
float vnoise(float2 p){ float2 i = floor(p), f = frac(p); f = f*f*(3.0-2.0*f);
  return lerp(lerp(hash(i), hash(i+float2(1,0)), f.x), lerp(hash(i+float2(0,1)), hash(i+float2(1,1)), f.x), f.y); }
float2 disc(int i, float rot){ float r = sqrt((float(i) + 0.5) / float(N)), a = float(i) * 2.39996323 + rot; return r * float2(cos(a), sin(a)); }

// Блики. Each gap in the crown throws an image of the sun: an ellipse stretched along the light.
float crown(Texture2D m, float2 p, float3 s, float dist, float drift, float tile, float seed, float rot){
  float z = max(s.z, 0.42), l = length(s.xy);
  float2 ax = l > 1e-4 ? s.xy / l : float2(0.0, 1.0);
  float2 q = p + drift * s.xy / z;
  q += 0.22 * float2(cos(uPhase + seed * 2.1), sin(uPhase * 2.0 + seed * 1.3));
  q += 0.045 * float2(sin(q.y * 2.6 + uPhase * 3.0 + seed) + 0.5 * sin(q.x * 6.1 - uPhase * 5.0),
                      cos(q.x * 2.2 - uPhase * 2.0 + seed) + 0.5 * cos(q.y * 5.3 + uPhase * 4.0));
  // wind: the crown sways as a whole, slowly and in gusts, and its leaves flutter fast and small, so the gaps open and close.
  // A gust crosses the wall rather than hitting all of it at once; every branch has its own phase.
  if (uWindLeaves > 0.0){
    float tt = uT - dot(p, float2(0.22, 0.06));
    float gust = 0.3 + 0.7 * vnoise(float2(tt * 0.12 + seed * 7.0, seed * 3.1)); gust *= gust;
    float br = vnoise(q * 0.8 + seed * 5.0) * 6.2832;
    float wd = max(uWindLeaves, 0.06 + 0.5 * uWindLeaves);       // leaves never stand quite still: light air still stirs a crown
    float A = wd * (0.35 + 0.65 * gust), f = 0.6 + 8.0 * wd;
    q += A * 0.055 * float2(sin(tt * f + br), 0.45 * sin(tt * f * 0.77 + br * 1.3));
    float fl = tt * (1.2 + 15.0 * wd);           // leaves flutter faster in stronger wind
    q += A * 0.040 * (float2(vnoise(q * 11.0 + float2(fl, seed * 9.0)), vnoise(q * 11.0 + float2(seed * 4.0, fl * 0.87))) - 0.5);
  }
  float R = SUN_R * uSrc * dist, a = R / (z * z), b = R / z;
  float texel = uTex / tile;
  float lod = log2(max(max(sqrt(3.14159 * a * b / float(N)) * texel, uWallH / uRes.y * texel), 1.0));
  float sum = 0.0;
  for (int i = 0; i < N; i++){ float2 e = disc(i, rot);
    sum += m.SampleLevel(sRepeat, (q + ax * (a * e.x) + float2(-ax.y, ax.x) * (b * e.y)) / tile + seed * 0.37, lod).r; }
  return sum / float(N);
}
float dapples(float2 p, float3 s, float rot){
  return crown(uM1, p, s, 7.0, 0.14, 3.70, 3.0, rot) * crown(uM0, p, s, 6.0, 0.11, 2.60, 2.0, rot + 2.1);
}

// One water drop on the glass, seen as its shadow.
float drop(float2 d, float r, float seed){
  if (r < 1e-4) return 1.0;
  float ang = atan2(d.y, d.x);
  float rr = r * (1.0 + 0.16 * sin(ang * 3.0 + seed * 6.3) + 0.09 * sin(ang * 5.0 + seed * 11.0));
  float dist = length(d * float2(1.0, d.y > 0.0 ? 1.25 : 0.85));
  float body = 1.0 - smoothstep(0.75 * rr, rr, dist);
  float rim = smoothstep(0.45 * rr, 0.9 * rr, dist) * body;
  float spark = 1.0 - smoothstep(0.0, 0.30 * r, length(d - float2(0.12 * r, -0.30 * r)));
  return (1.0 - 0.55 * body - 0.35 * rim) + 1.3 * spark * body;
}
// Rain on the glass: a mist of tiny drops, a few large ones that grow and then run down, and the trails they leave.
float rainGlass(float2 w, float4 win){
  float T = 1.0;
  float2 g = w / 0.013, id = floor(g);                                             // mist
  for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++){
    float2 c = id + float2(float(i), float(j));
    if (hash(c + 1.9) < 0.35) continue;
    float2 d = w - (c + 0.15 + 0.7 * float2(hash(c), hash(c + 17.3))) * 0.013;
    float r = 0.0016 + 0.0036 * hash(c + 4.2) * hash(c + 8.4);
    T *= 1.0 - 0.70 * (1.0 - smoothstep(0.4 * r, r, length(d)));
  }
  g = w / 0.052; id = floor(g);                                                  // large drops
  for (int j2 = -1; j2 <= 1; j2++) for (int i2 = -1; i2 <= 1; i2++){
    float2 c = id + float2(float(i2), float(j2));
    if (hash(c + 5.1) < 0.42) continue;
    float a = hash(c), b = hash(c + 17.3), life = frac(hash(c + 9.7) + uWT * (0.6 + 0.8 * a));
    float r = 0.016 * (0.40 + 0.60 * b * b) * smoothstep(0.0, 0.25, life) * (1.0 - smoothstep(0.88, 1.0, life));
    T *= drop(w - (c + 0.2 + 0.6 * float2(a, b)) * 0.052, r, a + b);
  }
  float col = floor(w.x / 0.070);                                                // one possible trail per column of glass
  if (hash(float2(col, 3.3)) > 0.40){
    float a = hash(float2(col, 1.0)), life = frac(hash(float2(col, 7.7)) + uWT * 0.45);
    float x0 = (col + 0.25 + 0.5 * a) * 0.070 + 0.0045 * sin(w.y * 31.0 + a * 6.3) + 0.0025 * sin(w.y * 83.0 + a * 17.0);
    float top = lerp(-0.20, win.w, hash(float2(col, 4.4))), tip = top - 0.60 * life;
    float fade = 1.0 - smoothstep(0.82, 1.0, life), on = step(tip, w.y) * step(w.y, top) * fade;
    float wd = 0.0052 * (0.55 + 0.9 * vnoise(float2(col * 3.1, w.y * 45.0))) * (0.5 + 0.5 * smoothstep(top, top - 0.10, w.y));
    float dx = abs(w.x - x0);
    T = T * (1.0 - 0.72 * on * (1.0 - smoothstep(0.5 * wd, wd, dx))) + 0.45 * on * (1.0 - smoothstep(0.0, 0.35 * wd, dx));
    float seg = floor(w.y / 0.034), by = (seg + 0.5) * 0.034;                    // beads left behind along the trail
    if (hash(float2(col, seg)) > 0.55 && by > tip && by < top)
      T *= lerp(1.0, drop(float2(w.x - x0, w.y - by), 0.006 + 0.005 * hash(float2(seg, col)), a + seg), fade);
    T *= lerp(1.0, drop(float2(w.x - x0, w.y - tip), 0.015, a), fade);            // the drop at the head of the trail
  }
  return clamp(T, 0.0, 1.8);
}
// Frost: a thin lumpy layer of snow on the crossbar (the frost on the glass itself is the uFrost picture).
float snowGlass(float2 w, float4 win, float barX, float barY){
  float T = 1.0;
  if (w.y > barY){
    float up = abs(w.x - barX) - 0.024, side = min(w.x - win.x, win.z - w.x);
    float lump = 0.010 + 0.016 * vnoise(float2(w.x * 21.0, 2.0)) + 0.010 * vnoise(float2(w.x * 55.0, 9.0))
               + 0.014 * exp(-max(up, 0.0) / 0.025) + 0.012 * exp(-side / 0.025);
    float top = barY + 0.020 + lump;
    T *= smoothstep(top - 0.009, top + 0.004, w.y);
  }
  return T;
}

// Растение / Жалюзи. What the window lets through at a point of its plane (metres).
float windowMask(float2 w, float4 win){
  if (w.x < win.x || w.x > win.z || w.y < win.y || w.y > win.w) return 0.0;
  if (uScene == 2)                                             // thin slats, two cords, one upright of the frame
    return step(0.28, frac((w.y - win.y) / 0.030))
         * step(0.0035, abs(w.x - lerp(win.x, win.z, 0.20))) * step(0.0035, abs(w.x - lerp(win.x, win.z, 0.80)))
         * step(0.016, abs(w.x - lerp(win.x, win.z, 0.40)));
  float barX = lerp(win.x, win.z, 0.34), barY = lerp(win.y, win.w, 0.74);
  float frame = step(0.024, abs(w.x - barX)) * step(0.020, abs(w.y - barY));
  if (uWeather == 1) frame *= rainGlass(w, win);
  if (uWeather == 2) frame *= snowGlass(w, win, barX, barY);
  return frame;
}
// For a point on our wall, look back towards the light: first through the window in the side wall,
// then at the plant standing outside it.
float3 room(float2 p, float3 s, float4 win, float rot){
  float3 a = normalize(cross(s, float3(0.0, 1.0, 0.0))), b = cross(s, a);
  float D = p.x - uCorner, t0 = D / max(-s.x, 1e-3), px = 1.2 * uWallH / uRes.y;
  float Rw = max(SUN_R * uSrc * (uScene == 2 ? 0.55 : uWeather > 0 ? 0.55 : 1.4), px / max(t0, 1e-3));
  float sw = 0.0;
  for (int i = 0; i < N; i++){ float2 e = disc(i, rot);
    float3 d = normalize(s + (a * e.x + b * e.y) * Rw);
    float t = D / max(-d.x, 1e-3);
    sw += windowMask(float2(t * d.z, p.y + t * d.y), win); }
  float plant = 1.0;
  for (int L = 0; L < 3; L++){
    if (L >= uLayers) break;
    float Dp = D + uDepth[L], Rp = SUN_R * uSrc * (uScene == 2 ? 0.7 + 0.5 * float(L) : 0.6 + 0.55 * float(L));
    float2 sh = (uDepth[L] - uDepth.x) * float2(1.19, 0.30);
    float4 rc = uPlantRect + float4(sh, sh);
    float lod = log2(max(Dp / max(-s.x, 1e-3) * Rp * uTex / (rc.z - rc.x) / sqrt(float(N)) * 1.6, 1.0));
    float sp = 0.0;
    for (int i2 = 0; i2 < N; i2++){ float2 e = disc(i2, rot + 1.7 * float(L + 1));
      float3 d = normalize(s + (a * e.x + b * e.y) * Rp);
      float t = Dp / max(-d.x, 1e-3);
      float2 n = (float2(t * d.z, p.y + t * d.y) - rc.xy) / (rc.zw - rc.xy);
      float3 tx = uPlant.SampleLevel(sClamp, float2(n.x, 1.0 - n.y), lod).rgb;
      sp += (n.x < 0.0 || n.x > 1.0 || n.y < 0.0 || n.y > 1.0) ? 1.0 : (L == 0 ? tx.r : L == 1 ? tx.g : tx.b); }
    float lo = uScene == 2 ? (L == 0 ? 0.16 : L == 1 ? 0.45 : 0.66) : (L == 0 ? 0.12 : L == 1 ? 0.36 : 0.58);
    plant *= lerp(lo, 1.0, sp / float(N));
  }
  float clear = 1.0;
  if (uWeather == 2){
    float2 wf = float2(t0 * s.z, p.y + t0 * s.y);
    float2 nf = float2((wf.x - win.x) / (win.z - win.x), (win.w - wf.y) / (win.w - uFrostBottom));
    uint fw, fh; uFrost.GetDimensions(fw, fh);
    float tpm = float(fw) / (win.z - win.x);
    if (nf.x >= 0.0 && nf.x <= 1.0 && nf.y >= 0.0 && nf.y <= 1.0)
      clear = uFrost.SampleLevel(sClamp, nf, log2(max(t0 * Rw * tpm * 0.2, 1.0))).r;
  }
  float v = sw / float(N) * (clear * plant + (1.0 - clear) * 0.24);
  float2 w0 = float2(t0 * s.z, p.y + t0 * s.y), n0 = (w0 - win.xy) / (win.zw - win.xy) - 0.5;
  float2 out2 = max(max(win.xy - w0, w0 - win.zw), 0.0);
  return float3(v * (1.0 - 0.30 * clamp(dot(n0, n0) * 2.0, 0.0, 1.0)), exp(-dot(out2, out2) / 0.16), 0.0);
}

float bumps(float2 p, float2x2 tw){ return vnoise(p * 75.0) * 0.30 + vnoise(mul(p, tw) * 170.0) * 0.35 + vnoise(mul(p, mul(tw, tw)) * 360.0) * 0.35; }

// Вода. Sunlight bent by a rippled water surface.
float caustic(float2 p, float3 s, float reach){
  float z = max(s.z, 0.42), l = length(s.xy);
  float2 ax = l > 1e-4 ? s.xy / l : float2(0.0, 1.0);
  float2 q = p + 0.12 * s.xy / z;
  q -= ax * dot(q, ax) * (1.0 - z) * 0.6;
  float ph = uPhase + uT * (0.05 + 2.25 * uWind);                 // live motion: the water keeps moving, faster in wind
  q += 0.07 * float2(sin(q.y * 3.1 + 1.0) + 0.5 * sin(q.x * 5.3 + ph), cos(q.x * 2.7 + 2.0) + 0.5 * cos(q.y * 4.7 - ph));
  float a = 0.0, b = 0.0, c = 0.0;
  for (int i = 0; i < 7; i++){
    float fi = float(i), ang = fi * 2.39996 + 0.6, f = 9.0 * pow(1.30, fi);
    float2 k = float2(cos(ang), sin(ang));
    float w = 0.62 / (1.0 + 0.28 * fi) * sin(f * dot(k, q) + fi * 1.7 + ph * (1.0 + glsl_mod(fi, 3.0)));
    a += w * k.x * k.x; b += w * k.x * k.y; c += w * k.y * k.y;
  }
  float det = (1.0 - reach * a) * (1.0 - reach * c) - reach * reach * b * b;
  return 1.0 / sqrt(det * det + 0.03 + uCalm);
}

float4 wallFragment(float4 position : SV_Position) : SV_Target
{
  uRes = resWallHTex.xy; uWallH = resWallHTex.z; uTex = resWallHTex.w;
  uSrc = srcCornerPhaseCalm.x; uCorner = srcCornerPhaseCalm.y; uPhase = srcCornerPhaseCalm.z; uCalm = srcCornerPhaseCalm.w;
  uDepth = depthWT.xyz; uWT = depthWT.w; uLayers = int(ambLayers.w); uWeather = int(weatherMotion.x);
  uPlantRect = plantRectU; uScene = int(wallScene.w);
  uL1 = l1Rough.xyz; uRough = l1Rough.w; uC1 = c1FrostBottom.xyz; uFrostBottom = c1FrostBottom.w; uWin1 = win1U;
  uWall = wallScene.xyz; uAmb = ambLayers.xyz;
  uT = weatherMotion.y; uWind = weatherMotion.z; uWindLeaves = weatherMotion.w;

  // gl_FragCoord: pixel centres at .5, rows counted from the bottom
  float2 at = position.xy - mirrorOrigin.yz;
  float2 fc = float2(at.x, uRes.y - at.y);
  if (mirrorOrigin.x > 0.5) fc.x = uRes.x - fc.x;                    // Light from: Right (Mac update 1.3), as wall.html
  float2 p = (fc - 0.5 * uRes) / uRes.y * uWallH;                     // position on the wall, metres
  float rot = ign(fc) * 6.2831853;
  float3 light = uAmb;
  float sun = 0.5;
  if (uScene == 3){
    float3 k = float3(caustic(p, uL1, 2.3), caustic(p, uL1, 2.4), caustic(p, uL1, 2.52));
    k = min(k, 6.0) * 0.17;
    float pool = 0.75 + 0.25 * sin(p.x * 2.3 + 0.7) * sin(p.y * 1.9 - 0.4);
    light += uC1 * (0.22 + k * pool);
  } else if (uC1.r + uC1.g + uC1.b > 0.002){
    float3 r = uScene == 0 ? float3(dapples(p * 1.3, uL1, rot), 0.0, 0.0) : room(p, uL1, uWin1, rot);
    float v = r.x; sun = v;
    light += uC1 * v + uC1 * float3(0.10, 0.04, -0.03) * (v * (1.0 - v) * 4.0);
    light += uC1 * r.y * 0.08;
    if (uScene == 1){
      float2 q = p - float2(0.30 * uWallH * uRes.x / uRes.y, -0.20); q = mul(q, float2x2(0.64, -0.77, 0.77, 0.64));
      light += uC1 * 0.13 * exp(-q.x * q.x / 0.0016 - q.y * q.y / 0.03);
    }
  }
  float2 edge = p / (0.5 * uWallH * float2(uRes.x / uRes.y, 1.0));
  light *= 1.0 - 0.24 * smoothstep(0.35, 1.25, length(edge));
  float3 col = uWall * light;
  float2x2 tw = float2x2(0.80, 0.60, -0.60, 0.80);
  float2 lit2 = normalize(uL1.xy + float2(1e-4, 0.0)), dq = lit2 * 0.0012;
  float h0 = bumps(p, tw), relief = (bumps(p + dq, tw) - h0) * 0.5;
  float strokes = vnoise(float2(p.x * 260.0, p.y * 9.0)) - 0.5;
  col *= 1.0 + ((h0 - 0.5) * 0.045 + relief * (0.30 + 0.50 * sun) + strokes * 0.02) * uRough;
  col = pow(clamp(col, 0.0, 1.0), float3(1.0 / 2.2, 1.0 / 2.2, 1.0 / 2.2));
  col += ((hash(fc + 3.7) - 0.5) * 0.028 + (hash(floor(fc / 2.0) + 9.1) - 0.5) * 0.010) * min(uRough, 1.4);
  return float4(col, 1.0);
}
