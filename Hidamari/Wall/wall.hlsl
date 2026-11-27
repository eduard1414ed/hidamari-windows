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
//   - HLSL's ?: works out both sides, so where Metal's ?: picks between costly calls (room(), treeShadow(), snowfall())
//     it is an if here: the same numbers, only the side Metal works out is worked out.
//   - As before, a second loop over the same letter in one function has a name of its own (i2, j2...), as fxc wants.
//     `line` is a word of HLSL, so rainfall()'s line is `streak`; prism()'s far is farW, to be safe; Metal's `in`
//     (a shader's input) is `i`.
//
// Mac update 1.1: the live motion of prototype/live-motion.html (uT, uWind, uWindLeaves; the "wind" block in crown(),
// the phase ph in caustic()), the same lines. With all three at 0 the picture is exactly the still one.
// Mac update 1.3: uMirror (Light from: Right) draws the frame as its mirror image, the same line as in wall.html.
// Mac update 1.4: the four new scenes of the approved sketch design/scenes/scenes.html, one to one, as wall.html has
// them: 4 Prism, 6 Moon Gate, 7 Tree, 8 Glass Blocks (5 left free). The Tree's shadow under the current sun (uTreeMap)
// is drawn before the frame by treeMapVertex/treeMapFragment below, the sketch's initTree3D() shaders. gGlass, a global
// in the sketch, is passed along as an inout parameter of room(). The motion of the window and the blinds
// (design/window-motion): the WALL_MOVING blocks, and rigVertex/rigFragment, ficusVertex/ficusFragment at the end.
//
// What is bound where (Direct3D 11, feature level 10_0: vs_4_0 / ps_4_0). The vertex and the pixel stage have slots
// of their own, so t0 of a vertex shader and t0 of a pixel shader are different things.
//   wallVertex / wallFragment:
//     PS b0  WallUniforms (12 float4, below)
//     PS t0 uM0, t1 uM1, t2 uPlant, t3 uFrost, t4 uTree, t5 uTreeMap
//     PS s0 sRepeat (wrap), s1 sClamp (clamp), s2 sMirror (mirrored repeat)
//   treeMapVertex / treeMapFragment:
//     VS+PS b1  TreeMapUniforms: float2 shift; float2 px; float time; float wind; float which (0 branches, 1 leaves); float unused
//     VS t0 treeGrid (Buffer<float2>, indexed by SV_VertexID: the grid is drawn indexed), t1 treeDepth (Texture2D, Load)
//     PS t0 treeLayers, s0 sLayers (mirrored repeat)
//   rigVertex / rigFragment (only with WALL_MOVING 1):
//     VS t0 rigInst (Buffer<float>, 11 floats per instance); VS+PS b1 RigPlace: float4 place; float2 size; float2 unused
//     PS t0 rigPhoto, t1 rigIds (Texture2D<uint4>, R8G8_UINT, Load), s0 sRig
//   ficusVertex / ficusFragment (only with WALL_MOVING 1):
//     VS t0 ficusShapes (Buffer<float>, 8 floats per instance); PS t0 ficusProfile (Buffer<float>, 257 floats)

// Mac update 1.4 (design/window-motion): the window and the blinds while they move are drawn by a shader of their own,
// this file compiled with WALL_MOVING = 1 (the sketch's uMoving; the app prepends "#define WALL_MOVING 1"). Everything
// of it is inside #if WALL_MOVING, so the still shader is, once preprocessed, exactly the code of 1.4 and every still
// picture is exactly that of 1.4.
#ifndef WALL_MOVING
#define WALL_MOVING 0
#endif

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
    float4 newScenes;           // uPhaseDay, uGlassNight, uRainbow (Mac update 1.4), unused
};

Texture2D uM0 : register(t0);
Texture2D uM1 : register(t1);
Texture2D uPlant : register(t2);
Texture2D uFrost : register(t3);
Texture2D uTree : register(t4);        // Mac update 1.4: the tree's photo
Texture2D uTreeMap : register(t5);     // Mac update 1.4: the tree's shadow under the current sun (treeMapFragment)
SamplerState sRepeat : register(s0);
SamplerState sClamp : register(s1);
SamplerState sMirror : register(s2);   // the tree's picture repeats mirrored past its edges (MIRRORED_REPEAT)

static float2 uRes; static float uWallH, uTex, uSrc, uCorner, uPhase, uCalm;
static float3 uDepth; static int uLayers, uWeather; static float uWT;
static float4 uPlantRect; static int uScene;
static float3 uL1, uC1; static float4 uWin1; static float3 uWall, uAmb; static float uRough;
static float uFrostBottom;
static float uT, uWind, uWindLeaves;    // live motion: seconds, and wind strength 0..1 (0 = the approved still picture)
static float uPhaseDay, uGlassNight, uRainbow;    // Mac update 1.4

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
#if WALL_MOVING
// ---------- Update 1.4: rain in motion (design/window-motion.html, one to one; only in the moving library) ----------
// Rain falling outside the window, seen as faint, fast streaks of shadow crossing the patch of light (as in Ed's video,
// where the rain is seen falling behind the glass). Two depths; the wind slants them (Ed: «не ровно вниз, там же ветер»).
float rainfall(float2 w){
  float T = 1.0;
  // the wind slants the rain: about 8 degrees in calm air, 15 at 4 m/s, 27 at 10 m/s, 38 at 15 m/s; gusts sway it a little
  float k0 = (0.10 + 2.5 * uWind) * (1.0 + 0.30 * (vnoise(float2(uT * 0.35, 5.0)) - 0.5) * 2.0);
  for (int k = 0; k < 2; k++){
    float fk = float(k), cw = 0.018 + 0.014 * fk, ch = 0.22 + 0.10 * fk;
    float sp = 2.6 - 0.8 * fk, kk = k0 * (1.0 - 0.15 * fk);              // further rain looks a little steeper
    // along a drop's path x + kk*y stays the same, so the streaks lie along the slant and move along it
    float2 q = float2(w.x + kk * w.y, w.y + sp * uT) + float2(19.0 * fk, 7.0 * fk);
    float2 id = floor(q / float2(cw, ch));
    for (int j = -1; j <= 1; j++){
      float2 c = id + float2(0.0, float(j));
      if (hash(c + 4.7 * fk) < 0.55) continue;
      float2 ctr = (c + float2(0.2 + 0.6 * hash(c + 1.1), 0.5)) * float2(cw, ch);
      float2 d = q - ctr;
      float across = abs(d.x) / sqrt(1.0 + kk * kk);                      // true distance from the slanted streak
      float len = (0.05 + 0.04 * hash(c + 2.3)) * (1.0 - 0.3 * fk);
      float streak = (1.0 - smoothstep(0.0005, 0.0018 + 0.001 * fk, across)) * (1.0 - smoothstep(0.6 * len, len, abs(d.y)));
      T *= 1.0 - (0.16 - 0.06 * fk) * streak;
    }
  }
  return T;
}
// Rain on the glass while there is motion, after Ed's video of rain on a window (25 November). The drops on the glass
// are the approved still picture and they stay put: they never grow or pop. Now and then one drop lets go and runs
// down very fast (the whole pane in well under a second) along a wavy line, sweeping away every drop on its way. Its
// wet trail dries over some seconds, a few beads last longer; the swept channel is slowly sown again by new small
// raindrops landing on it, one by one.
static const float RCOL = 0.045;                                        // width of the glass strip one runner keeps to
// One run in strip col: k = 0 the latest run, 1 the one before. Returns (when it set off, where from, speed, seed);
// speed 0 = no run this time.
float4 runOf(float col, float k, float4 win){
  float P = 7.0 + 9.0 * hash(float2(col, 2.2)), off = P * hash(float2(col, 5.5));     // runs are frequent; three runs back are kept,
                                                                                  // so a run's traces may last 3 x 7 = 21 s
  float ev = floor((uT + off) / P) - k;
  float2 e = float2(col, ev);
  float go = step(0.25, hash(e + 0.7));
  float t0 = ev * P - off + 0.7 * P * hash(e + 4.1);
  float top = lerp(win.y + 0.45 * (win.w - win.y), win.w + 0.03, hash(e + 2.9));
  return float4(t0, top, go * (0.9 + 0.8 * hash(e + 6.6)), hash(e + 1.3) + ev);
}
float runX(float col, float seed, float y){                       // the runner's wavy line, kept inside its strip
  return (col + 0.5) * RCOL + 0.011 * (hash(float2(seed, 8.1)) - 0.5)
       + 0.010 * (vnoise(float2(y * 26.0, seed * 7.0)) - 0.5) + 0.0025 * sin(y * 83.0 + seed * 6.3);
}
// How long ago (seconds) a runner swept the glass at point c (a drop of size r there); a large number if never.
float sweptAgo(float2 c, float r, float4 win){
  float col = floor(c.x / RCOL), ago = 1e5;
  for (int k = 0; k < 3; k++){
    float4 R = runOf(col, float(k), win);
    if (R.z <= 0.0 || c.y > R.y) continue;
    float since = uT - (R.x + (R.y - c.y) / R.z);
    if (since > 0.0 && abs(c.x - runX(col, R.w, c.y)) < 0.0065 + r) ago = min(ago, since);
  }
  return ago;
}
float rainLive(float2 w, float4 win){
  float T = 1.0;
  float2 g = w / 0.013, id = floor(g);                                             // mist (as in the still picture)
  // for speed (Ed's option 1): a mist drop sits 0.15–0.85 of its cell and reaches 5.2 mm (0.4 cell) at most, so a
  // neighbouring cell can reach w only from the nearest quarter of w's own cell; the others multiply T by exactly 1
  float2 fm = g - id;
  for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++){
    float2 c = id + float2(float(i), float(j));
    if (hash(c + 1.9) < 0.35) continue;
    float2 pc = (c + 0.15 + 0.7 * float2(hash(c), hash(c + 17.3))) * 0.013;
    float r = 0.0016 + 0.0036 * hash(c + 4.2) * hash(c + 8.4);
    // the app's only change to the sketch, for speed (Ed's option 1, 25 November 2026): a drop that does not reach w
    // multiplies T by exactly 1, so it is skipped before the costly look for runners; the picture is the same
    if (length(w - pc) >= r) continue;
    if (sweptAgo(pc, r, win) < 1.5 + 12.0 * hash(c + 2.2)) continue;            // swept away; a new raindrop lands here later
    T *= 1.0 - 0.70 * (1.0 - smoothstep(0.4 * r, r, length(w - pc)));
  }
  g = w / 0.052; id = floor(g);                                                  // large drops (as in the still picture)
  // for speed, as above: a large drop sits 0.2–0.8 of its cell and its shadow reaches 1.5 × 16 mm = 24 mm (0.46 cell)
  float2 fl = g - id;
  for (int j2 = -1; j2 <= 1; j2++) for (int i2 = -1; i2 <= 1; i2++){
    float2 c = id + float2(float(i2), float(j2));
    if (hash(c + 5.1) < 0.42) continue;
    float a = hash(c), b = hash(c + 17.3), life = frac(hash(c + 9.7) + uWT * (0.6 + 0.8 * a));
    float r = 0.016 * (0.40 + 0.60 * b * b) * smoothstep(0.0, 0.25, life) * (1.0 - smoothstep(0.88, 1.0, life));
    float2 pc = (c + 0.2 + 0.6 * float2(a, b)) * 0.052;
    // for speed, as above: past 1.5 r a drop's shadow is exactly 1 (its outline reaches 1.25 r, squeezed to 0.85
    // across; its glint 0.63 r), so lerp(1, 1, back) is 1 whatever the runners did
    if (r < 1e-4 || length(w - pc) > 1.5 * r) continue;
    float back = smoothstep(9.0, 18.0, sweptAgo(pc, 0.6 * r, win));              // a runner took it along; slowly it gathers again
    T *= lerp(1.0, drop(w - pc, r, a + b), back);
  }
  float col = floor(w.x / 0.070);                                                // old trails (as in the still picture)
  if (hash(float2(col, 3.3)) > 0.40){
    float a = hash(float2(col, 1.0)), life = frac(hash(float2(col, 7.7)) + uWT * 0.45);
    float x0 = (col + 0.25 + 0.5 * a) * 0.070 + 0.0045 * sin(w.y * 31.0 + a * 6.3) + 0.0025 * sin(w.y * 83.0 + a * 17.0);
    float top = lerp(-0.20, win.w, hash(float2(col, 4.4))), tip = top - 0.60 * life;
    float fade = 1.0 - smoothstep(0.82, 1.0, life), on = step(tip, w.y) * step(w.y, top) * fade;
    float wd = 0.0052 * (0.55 + 0.9 * vnoise(float2(col * 3.1, w.y * 45.0))) * (0.5 + 0.5 * smoothstep(top, top - 0.10, w.y));
    float dx = abs(w.x - x0);
    T = T * (1.0 - 0.72 * on * (1.0 - smoothstep(0.5 * wd, wd, dx))) + 0.45 * on * (1.0 - smoothstep(0.0, 0.35 * wd, dx));
    float seg = floor(w.y / 0.034), by = (seg + 0.5) * 0.034;
    if (hash(float2(col, seg)) > 0.55 && by > tip && by < top)
      T *= lerp(1.0, drop(float2(w.x - x0, w.y - by), 0.006 + 0.005 * hash(float2(seg, col)), a + seg), fade);
    T *= lerp(1.0, drop(float2(w.x - x0, w.y - tip), 0.015, a), fade);
  }
  // runners: the clean channel is already there (the drops in it were skipped above); draw the thin wet line that dries,
  // and the running drop itself
  float rc = floor(w.x / RCOL);
  for (int k = 0; k < 3; k++){
    float4 R = runOf(rc, float(k), win);
    if (R.z <= 0.0) continue;
    float tip = R.y - R.z * (uT - R.x);
    if (uT < R.x || w.y > R.y) continue;
    float since = uT - (R.x + (R.y - w.y) / R.z);
    float dx = abs(w.x - runX(rc, R.w, w.y));
    if (since > 0.0){
      // the trail left behind looks like the approved still trails (seen as a shadow on the wall, finer detail is lost in
      // the sun's blur); it dries: thinner and paler over some seconds, leaving a few beads that dry last
      float wet = exp(-since / 6.0) * (1.0 - smoothstep(10.0, 18.0, since));
      float wd = 0.0052 * (0.55 + 0.9 * vnoise(float2(rc * 3.1 + R.w, w.y * 45.0))) * (0.35 + 0.65 * wet);
      T = T * (1.0 - 0.72 * wet * (1.0 - smoothstep(0.5 * wd, wd, dx))) + 0.45 * wet * (1.0 - smoothstep(0.0, 0.35 * wd, dx));
      float seg = floor(w.y / 0.034), by = (seg + 0.5) * 0.034;
      if (hash(float2(rc + R.w, seg)) > 0.55 && by < R.y && by > tip){
        float bsn = max(uT - (R.x + (R.y - by) / R.z), 0.0), bs = exp(-bsn / 6.0) * (1.0 - smoothstep(11.0, 19.0, bsn));
        T *= lerp(1.0, drop(float2(w.x - runX(rc, R.w, by), w.y - by), 0.005 + 0.004 * hash(float2(seg, rc)), R.w + seg), bs);
      }
    }
    if (tip > win.y - 0.03){                                                       // the running drop, a little drawn out
      float2 dh = float2(w.x - runX(rc, R.w, tip), (w.y - tip) * 0.6);
      T *= drop(dh, 0.014, R.w);
    }
  }
  return clamp(T, 0.0, 1.8);
}
#endif
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

#if WALL_MOVING
// Update 1.4 (design/window-motion.html, snowfall(), one to one): snow falling outside the window, seen as tiny shadows
// of flakes drifting down through the patch of light (a couple of millimetres across, as Ed asked): three depths of
// flakes, the nearer ones larger, sharper and quicker; the wind pushes them sideways and they waver as they fall. Only
// while there is motion (the moving pipeline): a still picture keeps the approved frost without flakes. w is a point in
// the window's plane (metres); returns how much light gets past the flakes.
float snowfall(float2 w){
  float T = 1.0;
  // the wind comes in gusts: how far it has carried the snow so far (always onwards, never back)
  // in light air the flakes also swing more lazily (tw runs slower than real time)
  float tw = uT * (0.5 + 2.5 * uWind);
  float carried = (0.012 + 0.12 * uWind) * (uT + 6.0 * vnoise(float2(uT * 0.15, 3.0)) + 2.0 * vnoise(float2(uT * 0.47, 8.0)));
  for (int k = 0; k < 3; k++){
    float fk = float(k), cell = 0.020 + 0.012 * fk;               // further flakes: a looser grid, slower, softer
    // in light air snow drifts down slowly; stronger wind drives it faster (up to about as fast as before at 15 m/s)
    float fall = (0.035 - 0.007 * fk) * (1.0 + 8.0 * uWind);
    float2 q = w + float2(-carried * (1.0 - 0.25 * fk), 0.0) + float2(37.0 * fk, 11.0 * fk);
    float col0 = floor(q.x / cell);
    for (int i = -2; i <= 2; i++){
      // every column of flakes falls at its own speed, so no two neighbours keep step
      float col = col0 + float(i), sp = fall * (0.65 + 0.7 * hash(float2(col, 5.3 + fk)));
      // the app's only change to the sketch, for speed (Ed's option 1, 25 November 2026): a flake of this column is
      // never further than 1.02 cells left or 2.02 cells right of the column (sway 1.05, tumble 0.17 cells) and
      // reaches at most 4 mm around it: outside that it multiplies T by exactly 1, so the column is skipped
      if (q.x < (col - 1.05) * cell - 0.004 || q.x > (col + 2.05) * cell + 0.004) continue;
      float hc = hash(float2(col, 2.7 + fk));
      // and drifts from side to side on its own, slowly and widely: flakes flutter down, they don't drop straight
      float sway = cell * (0.75 * sin(tw * (1.2 + 1.6 * hc) + 6.3 * hash(float2(col, 8.8))) + 0.30 * sin(tw * (2.9 + 1.3 * hc) + 3.0 * hc));   // with the flake's own swing, stays within two columns
      float qy = q.y + sp * uT + cell * hash(float2(col, 1.1 + fk)) * 7.0;
      float row0 = floor(qy / cell);
      for (int j = -1; j <= 1; j++){
        float2 c = float2(col, row0 + float(j));
        if (hash(c + 3.3 * fk) < 0.50) continue;
        float b = hash(c + 9.2 + fk);
        // for speed, as above: the flake's height wanders 0.12 cells around (row + 0.2 + 0.6 b) cells
        if (abs(qy - (c.y + 0.2 + 0.6 * b) * cell) > 0.13 * cell + 0.004) continue;
        float a = hash(c + 1.7), h3 = hash(c + 6.1);
        float2 ctr = (c + 0.2 + 0.6 * float2(a, b)) * cell;
        // each flake tumbles on its own: two side-to-side swings at unrelated speeds, and it lags and hurries a little
        float f1 = 0.7 + 1.1 * h3, f2 = 1.9 + 1.7 * a;
        ctr.x += sway + cell * (0.12 * sin(tw * f1 * 2.0 + 6.3 * b) + 0.05 * sin(tw * f2 * 2.0 + 6.3 * h3));
        ctr.y += cell * 0.12 * sin(tw * (0.5 + 0.6 * b) + 6.3 * a);
        float r = (0.0015 - 0.0003 * fk) * (0.7 + 0.6 * hash(c + 4.4)), soft = r * (0.9 + 0.5 * fk);
        float dsk = 1.0 - smoothstep(r - soft * 0.5, r + soft, length(float2(q.x, qy) - ctr));
        T *= 1.0 - (0.55 - 0.15 * fk) * dsk;
      }
    }
  }
  return T;
}
#endif

// Круглое окно: the opening itself, and the lattice set in it: thin rim, three uprights right of centre, three crossbars above the middle.
float roundOpen(float2 w, float4 win){ float2 c = 0.5 * (win.xy + win.zw); return step(length(w - c), 0.5 * (win.w - win.y)); }
float lattice(float2 w, float4 win){
  float2 c = 0.5 * (win.xy + win.zw); float2 q = (w - c) / (0.5 * (win.w - win.y));
  float open = step(length(q), 0.95);
  for (int i = 0; i < 3; i++){
    float fi = float(i);
    open *= step(0.032, abs(q.x - (0.13 + 0.165 * fi)));
    open *= step(0.032, abs(q.y - (0.18 + 0.205 * fi)));
  }
  return open;
}
// Растение / Жалюзи. What the window lets through at a point of its plane (metres).
float windowMask(float2 w, float4 win){
  if (w.x < win.x || w.x > win.z || w.y < win.y || w.y > win.w) return 0.0;
  if (uScene == 8){                                           // glass blocks: the blocks and the mortar between them; the
    float2 g = (w - win.xy) / 0.19, id = floor(g), f = frac(g);   // light inside each block is in glassCaustic()
    float mortar = smoothstep(0.035, 0.055, f.x) * smoothstep(0.965, 0.945, f.x) * smoothstep(0.035, 0.055, f.y) * smoothstep(0.965, 0.945, f.y);
    float edge = 1.0 - 0.30 * smoothstep(0.32, 0.47, max(abs(f.x - 0.5), abs(f.y - 0.5)));   // thick rims bend light away
    return mortar * edge;
  }
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
#if WALL_MOVING
// Update 1.4: windowMask() while the window moves (only the window and the blinds come here): the same, without the
// still rain on the glass, which room() then adds more cheaply (rainLive). A copy, so the still code stays as it was.
float windowMaskMoving(float2 w, float4 win){
  if (w.x < win.x || w.x > win.z || w.y < win.y || w.y > win.w) return 0.0;
  if (uScene == 8){                                           // glass blocks: the blocks and the mortar between them; the
    float2 g = (w - win.xy) / 0.19, id = floor(g), f = frac(g);   // light inside each block is in glassCaustic()
    float mortar = smoothstep(0.035, 0.055, f.x) * smoothstep(0.965, 0.945, f.x) * smoothstep(0.035, 0.055, f.y) * smoothstep(0.965, 0.945, f.y);
    float edge = 1.0 - 0.30 * smoothstep(0.32, 0.47, max(abs(f.x - 0.5), abs(f.y - 0.5)));   // thick rims bend light away
    return mortar * edge;
  }
  if (uScene == 2)                                             // thin slats, two cords, one upright of the frame
    return step(0.28, frac((w.y - win.y) / 0.030))
         * step(0.0035, abs(w.x - lerp(win.x, win.z, 0.20))) * step(0.0035, abs(w.x - lerp(win.x, win.z, 0.80)))
         * step(0.016, abs(w.x - lerp(win.x, win.z, 0.40)));
  float barX = lerp(win.x, win.z, 0.34), barY = lerp(win.y, win.w, 0.74);
  float frame = step(0.024, abs(w.x - barX)) * step(0.020, abs(w.y - barY));
  if (uWeather == 2) frame *= snowGlass(w, win, barX, barY);
  return frame;
}
#endif
float3 hue(float h){ return clamp(abs(frac(h + float3(0.0, 2.0/3.0, 1.0/3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0); }
// Glass blocks, after Ed's references: every block is a thick block of fluted glass. The flutes are straight little
// lenses, each focusing the sun into a fine bright line with a thin dark seam beside it. The glass inside is not even
// (pressed glass), so the light along each line comes in lumps, and each block smears the light into its own soft
// horizontal bands. The blocks are coloured: four families of glass mix across the wall (warm, pale, green, dark),
// cold at night (uGlassNight). See the sketch for the whole story.
float3 glassTint(float2 w, float4 win){
  float2 g = (w - win.xy) / 0.19, id = floor(g);
  float2 sh = 0.6 * (float2(hash(id + 21.7), hash(id + 8.9)) - 0.5);          // each block shifts the colour a little
  float2 q = float2((w.x - win.x) / 0.42, (w.y - win.y) / 0.16) + sh;         // smeared sideways inside the block
  float t = clamp((0.62 * vnoise(q) + 0.38 * vnoise(q * float2(2.1, 2.7) + 7.3) - 0.2) / 0.62, 0.0, 1.0);
  // which family of glass: four soft weights from big patches across the wall
  float2 r = (w - win.xy) / float2(0.36, 0.26) + 0.35 * sh;
  float4 fw = float4(vnoise(r + 1.3), vnoise(r + 9.1), vnoise(r + 17.7), vnoise(r + 27.3));
  fw = pow(fw, (float4)5.0) * float4(1.0, 0.8, 1.0, 0.9);
  fw /= fw.x + fw.y + fw.z + fw.w + 1e-4;
  // day glass (linear light)
  float3 warm  = t < 0.5 ? lerp(float3(1.00, 0.72, 0.26), float3(1.00, 0.40, 0.08), t * 2.0) : lerp(float3(1.00, 0.40, 0.08), float3(0.78, 0.25, 0.10), t * 2.0 - 1.0);
  float3 pale  = t < 0.5 ? lerp(float3(1.00, 0.95, 0.84), float3(1.00, 0.82, 0.60), t * 2.0) : lerp(float3(1.00, 0.82, 0.60), float3(0.58, 0.50, 0.42), t * 2.0 - 1.0);
  float3 green = t < 0.5 ? lerp(float3(0.78, 0.80, 0.34), float3(0.50, 0.58, 0.20), t * 2.0) : lerp(float3(0.50, 0.58, 0.20), float3(0.16, 0.42, 0.26), t * 2.0 - 1.0);
  float3 dark  = t < 0.5 ? lerp(float3(0.62, 0.30, 0.12), float3(0.40, 0.17, 0.08), t * 2.0) : lerp(float3(0.40, 0.17, 0.08), float3(0.42, 0.10, 0.10), t * 2.0 - 1.0);
  // the same glass at night: cold
  float3 nwarm  = t < 0.5 ? lerp(float3(0.62, 0.80, 1.00), float3(0.38, 0.55, 1.00), t * 2.0) : lerp(float3(0.38, 0.55, 1.00), float3(0.42, 0.40, 0.95), t * 2.0 - 1.0);
  float3 npale  = t < 0.5 ? lerp(float3(0.90, 0.95, 1.00), float3(0.75, 0.84, 0.98), t * 2.0) : lerp(float3(0.75, 0.84, 0.98), float3(0.48, 0.52, 0.62), t * 2.0 - 1.0);
  float3 ngreen = t < 0.5 ? lerp(float3(0.45, 0.88, 0.85), float3(0.22, 0.68, 0.72), t * 2.0) : lerp(float3(0.22, 0.68, 0.72), float3(0.12, 0.42, 0.55), t * 2.0 - 1.0);
  float3 ndark  = t < 0.5 ? lerp(float3(0.42, 0.30, 0.72), float3(0.28, 0.20, 0.58), t * 2.0) : lerp(float3(0.28, 0.20, 0.58), float3(0.14, 0.14, 0.42), t * 2.0 - 1.0);
  float3 cd = fw.x * warm + fw.y * pale + fw.z * green + fw.w * dark;
  float3 cn = fw.x * nwarm + fw.y * npale + fw.z * ngreen + fw.w * ndark;
  float3 c = lerp(cd, cn, uGlassNight);
  float l = dot(c, float3(0.2126, 0.7152, 0.0722));
  return clamp(lerp((float3)l, c, 1.3), 0.0, 1.2);                // a little richer than the photo: light on a pale wall washes colour out
}
float glassCaustic(float2 w, float3 s, float4 win){
  float2 g = (w - win.xy) / 0.19, id = floor(g), f = frac(g);
  float h1 = hash(id), h2 = hash(id + 3.7), h3 = hash(id + 7.1), h4 = hash(id + 11.3);
  float side = clamp(s.z / max(-s.x, 0.25), -2.0, 2.0), up = clamp(s.y / max(-s.x, 0.25), -2.0, 2.0);
  // the flutes: about 1 cm apart, straight, each block at its own offset; they creep sideways with the sun
  float ph = (w.x + 0.0003 * sin(w.y * 41.0 + 6.3 * h1) + 0.004 * side * (0.7 + 0.6 * h2)) / 0.0105 + 3.0 * h1;
  float rib = floor(ph), c = 0.5 + 0.5 * cos(6.2832 * ph);
  float seam = smoothstep(0.80, 1.0, frac(ph + 0.5)) * smoothstep(1.0, 0.90, frac(ph + 0.5));   // thin dark seam
  float lines = 0.45 + 1.15 * pow(c, 6.0) - 0.15 * seam;       // the glass also glows between the lines
  // lumps along each line (uneven glass), drifting with the sun's height
  float along = 0.65 + 0.70 * vnoise(float2(rib * 1.7 + 13.0 * h3, w.y / 0.018 + 3.0 * up));
  // soft horizontal bands, stretched across the block, different in every block, drifting with the sun
  float2 q = float2(f.x / 0.55 + 2.0 * side * (0.5 + h2), f.y / 0.11 + 1.5 * up * (0.5 + h1)) + 17.0 * float2(h3, h4);
  float n = 0.65 * vnoise(q) + 0.35 * vnoise(q * float2(1.9, 2.3) + 5.0);
  float bands = 0.20 + 1.75 * smoothstep(0.15, 0.85, n);
  float v = lines * along * bands * (0.85 + 0.30 * h4);
  return 2.0 * tanh(v / 2.0);                                   // the brightest lines stay below white, never clip
}
// Where the ray from a wall point towards the light crosses the window's side wall: (across, up, distance).
float3 hitWin(float2 p, float3 d, float D, float extra){
  float t = (D + extra) / max(-d.x, 1e-3);
  return float3(t * d.z, p.y + t * d.y, t);
}
// For a point on our wall, look back towards the light: first through the window in the side wall,
// then at the plant standing outside it.
float3 room(float2 p, float3 s, float4 win, float rot, inout float3 gGlass){
  float3 a = normalize(cross(s, float3(0.0, 1.0, 0.0))), b = cross(s, a);
  float D = p.x - uCorner, t0 = D / max(-s.x, 1e-3), px = 1.2 * uWallH / uRes.y;
  float Rw = max(SUN_R * uSrc * (uScene == 2 ? 0.55 : uScene == 8 ? 0.9 : uScene == 6 ? 1.7 : uWeather > 0 ? 0.55 : 1.4), px / max(t0, 1e-3));
  float sw = 0.0;
  // Moon Gate has a loop of its own, and the old scenes keep their own way of writing the sums (the sketch writes
  // them through hitWin()): so the old scenes round exactly as before update 1.4
  if (uScene == 6)
  for (int i6 = 0; i6 < N; i6++){ float2 e = disc(i6, rot);   // the opening's edge is soft; lattice and vase sit nearer, a little sharper
    float3 d = normalize(s + (a * e.x + b * e.y) * Rw);
    float3 d2 = normalize(s + (a * e.x + b * e.y) * Rw * 0.5);
    sw += roundOpen(hitWin(p, d, D, 0.0).xy, win) * lattice(hitWin(p, d2, D, 0.0).xy, win); }
  // update 1.4 (design/window-motion.html): in motion every third point of the sun's disc is enough (8 of 24): the frame
  // is redrawn 30 times a second, and the texture blur is raised to match, so the soft edges stay soft. Only in the
  // moving library; the still one compiles exactly the code of 1.4.
  else
#if WALL_MOVING
  {
  for (int i3 = 0; i3 < N; i3 += 3){ float2 e = disc(i3, rot);
    float3 d = normalize(s + (a * e.x + b * e.y) * Rw);
    float t = D / max(-d.x, 1e-3);
    sw += windowMaskMoving(float2(t * d.z, p.y + t * d.y), win); }
  sw *= float(N) / float(N / 3);
  // for speed (Ed's option 1, 25 November 2026): where no light comes through the window (sw = 0) the frame is 0
  // whatever the rain, the snow or the plant do, so they are worked out only inside the patch of light
  if (uWeather == 1 && sw > 0.0){
    // in motion the drops on the glass are worked out at 2 points of the sun's disc instead of all 24 (the still
    // picture keeps all 24; the pixel-by-pixel turn of the disc softens them the same)
    float rn = 0.0;
    for (int i4 = 0; i4 < 2; i4++){ float2 e = disc(8 + i4 * 10, rot);
      float3 d = normalize(s + (a * e.x + b * e.y) * Rw);
      float t = D / max(-d.x, 1e-3);
      rn += rainLive(float2(t * d.z, p.y + t * d.y), win); }
    sw *= rn * 0.5;
    sw *= rainfall(float2(t0 * s.z, p.y + t0 * s.y));        // and the rain itself falling outside
  }
  }
#else
  for (int i = 0; i < N; i++){ float2 e = disc(i, rot);
    float3 d = normalize(s + (a * e.x + b * e.y) * Rw);
    float t = D / max(-d.x, 1e-3);
    sw += windowMask(float2(t * d.z, p.y + t * d.y), win); }
#endif
  if (uScene == 8){ float2 wc = hitWin(p, s, D, 0.0).xy;
    sw *= glassCaustic(wc, s, win);                            // the fine lines from the central ray: sharp
    gGlass = glassTint(wc, win); }
  float plant = 1.0;
  for (int L = 0; L < 3; L++){
    if (L >= uLayers) break;
    float Dp = D + uDepth[L], Rp = SUN_R * uSrc * (uScene == 6 ? 0.85 : uScene == 2 ? 0.7 + 0.5 * float(L) : 0.6 + 0.55 * float(L));
    float2 sh = (uDepth[L] - uDepth.x) * float2(1.19, 0.30);
    float4 rc = uPlantRect + float4(sh, sh);
    float lod = log2(max(Dp / max(-s.x, 1e-3) * Rp * uTex / (rc.z - rc.x) / sqrt(float(N)) * 1.6, 1.0));
    if (uScene == 6) lod = max(lod - 1.0, 0.0);                  // the sun's disc already softens it; don't blur the mask twice
    float sp = 0.0;
#if WALL_MOVING
    if (sw <= 0.0) break;                                          // no light here: the plant changes nothing (see above)
    {
    float ns = float(N / 3);
    lod = log2(max(Dp / max(-s.x, 1e-3) * Rp * uTex / (rc.z - rc.x) / sqrt(ns) * 1.6, 1.0));
    for (int i5 = 0; i5 < N; i5 += 3){ float2 e = disc(i5, rot + 1.7 * float(L + 1));
      float3 d = normalize(s + (a * e.x + b * e.y) * Rp);
      float t = Dp / max(-d.x, 1e-3);
      float2 n = (float2(t * d.z, p.y + t * d.y) - rc.xy) / (rc.zw - rc.xy);
      float3 tx = uPlant.SampleLevel(sClamp, float2(n.x, 1.0 - n.y), lod).rgb;
      sp += (n.x < 0.0 || n.x > 1.0 || n.y < 0.0 || n.y > 1.0) ? 1.0 : (L == 0 ? tx.r : L == 1 ? tx.g : tx.b); }
    sp *= float(N) / ns;                                         // so the line below divides as the still picture does
    }
#else
    for (int i2 = 0; i2 < N; i2++){ float2 e = disc(i2, rot + 1.7 * float(L + 1));
      float3 d = normalize(s + (a * e.x + b * e.y) * Rp);
      float t = Dp / max(-d.x, 1e-3);
      float2 n = (float2(t * d.z, p.y + t * d.y) - rc.xy) / (rc.zw - rc.xy);
      float3 tx = uPlant.SampleLevel(sClamp, float2(n.x, 1.0 - n.y), lod).rgb;
      sp += (n.x < 0.0 || n.x > 1.0 || n.y < 0.0 || n.y > 1.0) ? 1.0 : (L == 0 ? tx.r : L == 1 ? tx.g : tx.b); }
#endif
    float lo = uScene == 6 ? 0.02 : uScene == 2 ? (L == 0 ? 0.16 : L == 1 ? 0.45 : 0.66) : (L == 0 ? 0.12 : L == 1 ? 0.36 : 0.58);
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
#if WALL_MOVING
  float flakes = 1.0;                                            // Metal: uWeather == 2 && sw > 0.0 ? snowfall(...) : 1.0
  if (uWeather == 2 && sw > 0.0) flakes = snowfall(float2(t0 * s.z, p.y + t0 * s.y));   // lost where frost scatters the light
  float v = sw / float(N) * (clear * plant * flakes + (1.0 - clear) * 0.24);
#else
  float v = sw / float(N) * (clear * plant + (1.0 - clear) * 0.24);
#endif
  float2 w0 = float2(t0 * s.z, p.y + t0 * s.y), n0 = (w0 - win.xy) / (win.zw - win.xy) - 0.5;
  float2 out2 = max(max(win.xy - w0, w0 - win.zw), 0.0);
  return float3(v * (1.0 - 0.30 * clamp(dot(n0, n0) * 2.0, 0.0, 1.0)), exp(-dot(out2, out2) / 0.16), 0.0);
}

// Большое дерево (update 1.4). The tree is a real object in front of the wall; its shadow under the current sun is in
// uTreeMap (drawn by treeMapVertex/treeMapFragment). Here it is softened: each patch of light is an image of the sun,
// long along the beam and soft when the sun is low. The edge of a house's shadow on the left crosses the wall over the
// day; the shaded strip on the left edge is the shadow of something else.
// as v but never past ±L: unchanged up to ±a, then bends smoothly towards the limit
float softLimit(float v, float a, float L){ float m = abs(v); return m <= a ? v : sign(v) * (a + (L - a) * tanh((m - a) / (L - a))); }
float treeShadow(float2 p, float3 s, float rot){
  float z = max(s.z, 0.30);
  float Ws = 0.5 * uWallH * uRes.x / uRes.y;
  float k = 2.0 * Ws / 865.0;                                                 // metres per pixel of the photo
  float2 lo = float2(-Ws - 20.0 * k, -435.0 * k), sz = float2(970.0, 900.0) * k;  // photo x 20..885 across the screen
  float low = 1.0 - smoothstep(0.0, 0.6, s.y);                              // 1 when the sun is near the horizon
  float2 along = normalize(s.xy + float2(1e-4, 0.0)), across = float2(-along.y, along.x);
  float R0 = SUN_R * uSrc * 0.3 / z;
  float Ra = 0.002 + 0.008 * low, Rc = 0.0015 + 0.0015 * low;
  // the shadow picture's pixels per photo height: 1800 as in the sketch (a test of a half-size picture gives 900)
  float lod0 = log2(max(R0 * 900.0 / sz.y, 1.0)), lod1 = log2(max(Rc * 1800.0 / sz.y, 1.0));
  float3 a = normalize(cross(s, float3(0.0, 1.0, 0.0))), b = cross(s, a);
  float st = 0.0, tr = 0.0;
  // from the photo's own coordinates to the picture with its 240 px margin (970 x 900 inside 1450 x 1380)
  float2 mg = float2(240.0, 240.0), tot = float2(1450.0, 1380.0);
  for (int i = 0; i < N; i++){ float2 e = disc(i, rot), ab = a.xy * e.x + b.xy * e.y;
    float2 n0 = (p + ab * R0 - lo) / sz, n1 = (p + across * (e.x * Rc) + along * (e.y * Ra) - lo) / sz;
    float2 t0 = (float2(n0.x, 1.0 - n0.y) * float2(970.0, 900.0) + mg) / tot, t1 = (float2(n1.x, 1.0 - n1.y) * float2(970.0, 900.0) + mg) / tot;
    st += uTree.SampleLevel(sMirror, t0, lod0).b;
    tr += uTreeMap.SampleLevel(sClamp, t1, lod1).r; }
  // a house off to the left: the edge of its shadow crosses the wall against the sun, evenly with the light's way
  float xl = (-0.13 + 0.53 * uPhaseDay) * Ws, edge = 0.04 + 0.05 * low;
  float band = smoothstep(xl - edge, xl + edge, p.x);
  return (st / float(N)) * (tr / float(N)) * band;
}
// Призма. Sunlight through cut glass lands on the wall as blades of light, each from one face of the glass, placed by
// real refraction through a 60° prism; near the angle where a face stops letting light out, its colours spread into
// a rainbow. See the sketch for the whole story.
float prismBend(float i, float n){                      // how far a ray is bent by a 60° prism; -1 when it cannot get out
  float A = 1.0472, r1 = asin(clamp(sin(i) / n, -1.0, 1.0)), s2 = n * sin(A - r1);
  return abs(s2) >= 1.0 ? -1.0 : i + asin(s2) - A;
}
float3 prism(float2 p, float3 s){
  float z = max(s.z, 0.35), tilt = clamp(s.x / z, -1.6, 1.6), low = 1.0 - smoothstep(0.12, 0.85, s.y);
  float W2 = 0.5 * uWallH * uRes.x / uRes.y;
  float g = 0.55 * tilt + 0.70 * s.y;                    // how the sun stands to the glass: changes through the day
  float3 sum = (float3)0.0;
  for (int k = 0; k < 11; k++){
    float fk = float(k), r1 = frac(fk * 0.618 + 0.13), r2 = frac(fk * 0.37 + 0.41), r3 = frac(fk * 0.71 + 0.07), r4 = frac(fk * 0.43 + 0.29);
    // this face's angle to the sun, and what the prism does with it
    float inc = 0.66 + (r3 - 0.35) * 0.95 + 0.42 * g * (r4 < 0.5 ? 1.0 : -0.8) + 0.16 * sin(uPhase * 3.0 + fk * 2.2);
    float dv = prismBend(inc, 1.515);
    if (dv < 0.0) continue;                              // this face lets no light out now
    float dvBlue = prismBend(inc, 1.535);
    float spread = dvBlue < 0.0 ? 3.0 : clamp((dvBlue - dv) / 0.02, 0.0, 3.0);   // how strongly the colours come apart
    float edgeOn = smoothstep(0.0, 0.10, prismBend(inc + 0.10, 1.535) < 0.0 ? 1.4 - inc : 1.0);
    float m = dv - 0.65;                                 // past or before the least bend: moves the blade, flips its curve
    bool across = k == 3 || k == 8;
    float th = (across ? 0.35 + 0.30 * r2 : 1.45 + 0.42 * sin(fk * 2.3 + 0.4)) + 0.9 * m * (r1 < 0.5 ? 1.0 : -1.0) + 0.28 * g * (r2 < 0.5 ? 1.0 : -1.0);
    float2 d = float2(cos(th), sin(th)), n = float2(-d.y, d.x);
    float2 dirO = normalize(float2(cos(fk * 2.4), sin(fk * 1.7)));
    float2 drift = normalize(float2(cos(fk * 1.3 + 0.7), 0.35 * sin(fk * 2.9)));
    float2 O = float2((r1 - 0.5) * 1.7 * W2, (r4 - 0.5) * 0.55 * uWallH) + dirO * m * 1.1 + drift * g * (0.30 + 0.35 * r2) * (r3 < 0.5 ? 1.0 : -1.0);   // every blade also slides with the sun, at its own speed
    float2 q = p - O;
    float along = dot(q, d);
    float L = (0.22 + 0.30 * r2) * (1.0 + 0.5 * low);
    float a = along / L;
    if (abs(a) > 1.0) continue;
    float curv = (0.05 + 0.22 * r3) * clamp(m * 4.0 + (r2 - 0.5), -1.0, 1.0);
    float side = r4 < 0.5 ? 1.0 : -1.0;
    float uu = side * (dot(q, n) - curv * along * along);
    float focus = 0.25 + 1.3 * abs(sin(3.2 * (inc - 0.5 - r1)));        // pinches into a sliver and opens again
    float grow = r3 < 0.5 ? (a + 1.0) * 0.5 : (1.0 - a) * 0.5;
    float W = (0.020 + 0.20 * r2 * r2 * r2) * focus * (1.0 + 0.6 * low) * pow(grow, 0.6) * pow(max(1.0 - a * a, 0.0), 0.35) + 0.001;
    float soft = 0.0020 + 0.0030 * low;
    float edge = smoothstep(-soft, soft, uu);
    float farW = 1.0 - smoothstep(0.35 * W, W, uu);          // Metal: far (an old reserved word of C compilers)
    float spike = exp(-max(uu, 0.0) / max(0.18 * W, 0.002));
    float tips = smoothstep(1.0, 0.55, abs(a));
    float bright = (0.45 + 0.9 * r1) * lerp(1.5, 0.7, smoothstep(0.05, 0.45, r2)) / sqrt(focus);   // a pinched blade is brighter
    float3 blade = (float3)(edge * farW * (0.45 + 0.85 * spike) * bright * tips);
    blade += 0.30 * bright * tips * exp(-abs(uu - 0.3 * W) / (0.08 + 0.8 * W));
    // the rainbow: outside the razor edge the light comes apart, red farthest, violet nearest.
    float bowAmt = (k == 1 ? 0.6 : 0.0) + smoothstep(1.6, 2.8, spread);
    if (bowAmt > 0.0){
      float rw = (0.014 + 0.020 * low) * (0.6 + 0.8 * spread);
      float t = -uu / rw;
      float env = smoothstep(-0.05, 0.12, t) * (1.0 - smoothstep(0.85, 1.05, t));
      float3 spec = hue(0.80 * clamp(1.0 - t, 0.0, 1.0));
      blade += min(bowAmt, 1.2) * uRainbow * tips * env * lerp((float3)1.0, spec, 0.85) * 1.05;
    }
    float on = edgeOn * smoothstep(-0.65, 0.35, sin(uPhase * 2.0 + fk * 1.9 + tilt * 1.4));
    float stroke = vnoise(float2(along * 14.0 + fk * 3.0, dot(q, n) * 140.0)) * vnoise(float2(along * 5.0 - fk, dot(q, n) * 55.0) + 7.0);
    sum += blade * on * (0.55 + 0.9 * stroke);
  }
  return sum;
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
  uPhaseDay = newScenes.x; uGlassNight = newScenes.y; uRainbow = newScenes.z;

  float3 gGlass = (float3)1.0;
  // gl_FragCoord: pixel centres at .5, rows counted from the bottom
  float2 at = position.xy - mirrorOrigin.yz;
  float2 fc = float2(at.x, uRes.y - at.y);
  if (mirrorOrigin.x > 0.5) fc.x = uRes.x - fc.x;                    // Light from: Right (Mac update 1.3), as wall.html
  float2 p = (fc - 0.5 * uRes) / uRes.y * uWallH;                     // position on the wall, metres
  float rot = ign(fc) * 6.2831853;
  float3 light = uAmb;
  float sun = 0.5;
  if (uScene == 4){
    float3 k = min(prism(p, uL1), 3.5);
    light = uAmb * 0.72 + uC1 * (0.02 + 1.7 * k);              // deeper shade between the blades
  } else if (uScene == 3){
    float3 k = float3(caustic(p, uL1, 2.3), caustic(p, uL1, 2.4), caustic(p, uL1, 2.52));
    k = min(k, 6.0) * 0.17;
    float pool = 0.75 + 0.25 * sin(p.x * 2.3 + 0.7) * sin(p.y * 1.9 - 0.4);
    light += uC1 * (0.22 + k * pool);
  } else if (uC1.r + uC1.g + uC1.b > 0.002){
    // Metal: r = uScene == 0 ? float3(dapples(...), 0, 0) : uScene == 7 ? float3(treeShadow(...), 0, 0) : room(...);
    // an if here, so only the scene's own function is worked out (HLSL's ?: works out every side)
    float3 r;
    if (uScene == 0) r = float3(dapples(p * 1.3, uL1, rot), 0.0, 0.0);
    else if (uScene == 7) r = float3(treeShadow(p, uL1, rot), 0.0, 0.0);
    else r = room(p, uL1, uWin1, rot, gGlass);
    float v = r.x; sun = v;
    if (uScene == 8){                                              // the old scenes keep their own line, as wall.html
    float3 tc = uScene == 8 ? gGlass : (float3)1.0;                 // coloured glass colours the light it lets through
    light += uC1 * v * tc + uC1 * tc * float3(0.10, 0.04, -0.03) * (v * (1.0 - v) * 4.0);   // warm edge where light meets shade
    } else
    light += uC1 * v + uC1 * float3(0.10, 0.04, -0.03) * (v * (1.0 - v) * 4.0);   // warm edge where light meets shade
    light += uC1 * r.y * 0.08;
    if (uScene == 8) light += uC1 * 0.10 * r.y;                // glass blocks scatter: the wall around the patch glows a little more
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

// ---------- Update 1.4: the Tree's shadow under the current sun (the sketch's initTree3D() and drawTreeMap()) ----------
// The tree is a real object: every point of it stands at its own distance from the wall (treeDepth). Its two layers,
// branches and leaves, are drawn as fine meshes over the picture; each point moves by its distance times the sun's
// slant (shift), and while the tree rustles it sways a little more the further it hangs from the wall. The two layers
// are multiplied into the shadow picture (blending: destination × source), which treeShadow() above reads.
// The picture's rows run as in WebGL's framebuffer: row 0 at clip y = -1, so y is turned over here (Direct3D's clip
// space and rows are Metal's).

struct TreeMapData {
    float2 shift;       // uShift: picture units per metre from the wall
    float2 px;          // uPx: one photo pixel in picture units
    float time;         // uTime: seconds
    float wind;         // uWind: 0..1, 0 = still
    float which;        // uWhich: 0 branches, 1 leaves (an int in Metal; here a float, read as int(which + 0.5))
    float unused;
};
cbuffer TreeMapUniforms : register(b1) { TreeMapData tm; };   // read by treeMapVertex and treeMapFragment

Buffer<float2> treeGrid : register(t0);       // treeMapVertex: the mesh's points (picture units), by SV_VertexID
Texture2D<float4> treeDepth : register(t1);   // treeMapVertex: each point's distance from the wall, read exactly (Load)
Texture2D treeLayers : register(t0);          // treeMapFragment: branches in red, leaves in green
SamplerState sLayers : register(s0);          // treeMapFragment: mirrored repeat

struct TreeOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };

float h3(float3 p){ p = frac(p * 0.1031); p += dot(p, p.zyx + 31.32); return frac((p.x + p.y) * p.z); }
float vnoise3(float3 p){ float3 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
  return lerp(lerp(lerp(h3(i), h3(i + float3(1,0,0)), f.x), lerp(h3(i + float3(0,1,0)), h3(i + float3(1,1,0)), f.x), f.y),
             lerp(lerp(h3(i + float3(0,0,1)), h3(i + float3(1,0,1)), f.x), lerp(h3(i + float3(0,1,1)), h3(i + float3(1,1,1)), f.x), f.y), f.z); }
// Rustling: every bit of the crown sways a little in the wind, the further from the wall the more (the trunk not at
// all). Slow sway of whole clumps, a quicker flutter of single leaves, and gusts rolling across the tree. Branches and
// leaves at the same place move together, so nothing comes apart.
float2 rustle(float2 px, float d){
  if (tm.wind <= 0.0) return (float2)0.0;
  // leaves sway everywhere (more where the crown hangs further out); branches only away from the trunk
  float a = int(tm.which + 0.5) == 1 ? lerp(0.45, 1.0, smoothstep(0.03, 0.12, d)) : smoothstep(0.035, 0.12, d);
  float gust = 0.55 + 0.45 * sin(tm.time * 0.45 - px.x * 0.006 + 0.8 * sin(tm.time * 0.17));
  float2 sway = float2(vnoise3(float3(px / 70.0, tm.time * 0.7)), vnoise3(float3(px / 70.0 + 17.3, tm.time * 0.7))) - 0.5;
  float2 flut = float2(vnoise3(float3(px / 22.0, tm.time * 2.6)), vnoise3(float3(px / 22.0 + 5.1, tm.time * 2.6))) - 0.5;
  return a * tm.wind * gust * (40.0 * sway + 16.0 * flut);                      // photo pixels (1 px = 1.7 mm)
}

TreeOut treeMapVertex(uint vid : SV_VertexID)
{
  float2 uv = treeGrid[vid];
  uint dw, dh; treeDepth.GetDimensions(dw, dh);
  int2 n = int2(dw, dh);
  float4 d4 = treeDepth.Load(int3(clamp(int2(uv * float2(n)), int2(0, 0), n - 1), 0));
  float d = int(tm.which + 0.5) == 0 ? (d4.r * 65280.0 + d4.g * 255.0) / 65535.0 * 0.5 : d4.b * 0.5;   // metres from the wall
  float2 w = rustle(uv / tm.px, d) * tm.px;
  float2 c = (uv + d * tm.shift + w) * 2.0 - 1.0;   // the shadow map covers the same picture
  TreeOut o; o.position = float4(c.x, -c.y, 0.0, 1.0); o.uv = uv; return o;
}

float4 treeMapFragment(TreeOut i) : SV_Target
{
  float4 c = treeLayers.SampleLevel(sLayers, i.uv, 0.0); float t = int(tm.which + 0.5) == 0 ? c.r : c.g; return float4(t, t, t, 1.0);
}

#if WALL_MOVING
// Update 1.4, the window and the blinds in motion (design/window-motion/window-motion.html): the plant in the wind is
// drawn into its picture before the frame, and the frame reads that picture instead of the still one.
//
// rigVertex/rigFragment: the sketch's rigInit() shaders. Every piece of a photo plant (WindowLeaves.swift poses them)
// is a box of the photo, turned and shifted; only the photo's pixels that belong to that piece (its number in the map
// of pieces) are kept. Drawn three times, one depth layer into each of red, green and blue, darkest wins (MIN).
// WebGL's row 0 is at the bottom of its clip space, Metal's (and Direct3D's) at the top: y is turned over so the rows
// are the same.
struct RigPlaceData { float4 place; float2 size; float2 unused; };
cbuffer RigPlace : register(b1) { RigPlaceData P; };   // read by rigVertex and rigFragment

Buffer<float> rigInst : register(t0);         // rigVertex: 11 floats per piece (instance)
Texture2D rigPhoto : register(t0);            // rigFragment: the photo
Texture2D<uint4> rigIds : register(t1);       // rigFragment: the map of pieces (R8G8_UINT), read exactly (Load)
SamplerState sRig : register(s0);             // rigFragment

struct RigOut { float4 position : SV_Position; float2 uv : TEXCOORD0; nointerpolation int id : TEXCOORD1; };

RigOut rigVertex(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
  uint base = iid * 11;                               // Metal: I = inst + iid * 11
  float I[11];
  for (uint k = 0; k < 11; k++) I[k] = rigInst[base + k];
  float2 c = float2(float(vid & 1u), float(vid >> 1));
  float2 q = lerp(float2(I[0], I[1]) - 1.0, float2(I[2], I[3]) + 1.0, c);
  float2 p = float2(dot(float3(I[4], I[5], I[6]), float3(q, 1.0)), dot(float3(I[7], I[8], I[9]), float3(q, 1.0)));
  float x = P.place.w > 0.0 ? P.place.y + P.place.w - p.x * P.place.x : P.place.y + p.x * P.place.x;
  float y = P.place.z + p.y * P.place.x;
  RigOut o; o.position = float4(x / 512.0 - 1.0, -(y / 512.0 - 1.0), 0.0, 1.0); o.uv = q; o.id = int(I[10] + 0.5);
  return o;
}

float4 rigFragment(RigOut i) : SV_Target
{
  int2 c = int2(floor(i.uv));
  if (c.x < 0 || c.y < 0 || c.x >= int(P.size.x) || c.y >= int(P.size.y)) discard;
  uint4 e = rigIds.Load(int3(c, 0));
  if (int(e.r) + 256 * int(e.g) != i.id) discard;
  return (float4)rigPhoto.Sample(sRig, i.uv / P.size).r;
}

// ficusVertex/ficusFragment: the drawn ficus of makeFicus() in the wind, the canvas's strokes with round ends and its
// leaves (leafPath(), two curves, its half width along the leaf in `profile`). Each shape darkens its own colour channel
// by how much of the pixel it covers, as the canvas's 'multiply' with the inks #0ff, #f0f, #ff0 does.
Buffer<float> ficusShapes : register(t0);     // ficusVertex: 8 floats per shape (instance)
Buffer<float> ficusProfile : register(t0);    // ficusFragment: the leaf's half width, 257 floats

struct FicusOut { float4 position : SV_Position; nointerpolation float4 a : TEXCOORD0; nointerpolation float4 b : TEXCOORD1; };

FicusOut ficusVertex(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
  uint base = iid * 8;                                // Metal: S = shapes + iid * 8
  float S[8];
  for (uint k = 0; k < 8; k++) S[k] = ficusShapes[base + k];
  float2 c = float2(float(vid & 1u), float(vid >> 1));
  float2 q;
  if (S[0] < 0.5) {                                   // a stroke: from (2, 3) to (4, 5), half width 6
    float2 a = float2(S[2], S[3]), b = float2(S[4], S[5]); float r = S[6] + 1.5;
    float2 d = b - a; float L = length(d); float2 t = L > 1e-6 ? d / L : float2(1.0, 0.0), n = float2(-t.y, t.x);
    q = a + t * lerp(-r, L + r, c.x) + n * lerp(-r, r, c.y);
  } else {                                            // a leaf: from (2, 3), turned by 4, length 5, half width 6
    float2 o = float2(S[2], S[3]); float L = S[5], w = S[6];
    float2 t = float2(cos(S[4]), sin(S[4])), n = float2(-t.y, t.x);
    q = o + t * lerp(-1.5, L + 1.5, c.x) + n * lerp(-w - 1.5, w + 1.5, c.y);
  }
  FicusOut o; o.position = float4(q.x / 512.0 - 1.0, -(q.y / 512.0 - 1.0), 0.0, 1.0);
  o.a = float4(S[0], S[1], S[2], S[3]); o.b = float4(S[4], S[5], S[6], S[7]);
  return o;
}

float4 ficusFragment(FicusOut i) : SV_Target
{
  float2 p = i.position.xy;                           // the canvas pixel's centre
  float d;                                            // signed distance to the edge, pixels, inside positive
  if (i.a.x < 0.5) {
    float2 a = i.a.zw, ba = i.b.xy - a, pa = p - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-12), 0.0, 1.0);
    d = i.b.z - length(pa - ba * h);
  } else {
    float2 r = p - i.a.zw; float cs = cos(i.b.x), sn = sin(i.b.x), L = i.b.y, w = i.b.z;
    float u = r.x * cs + r.y * sn, v = abs(-r.x * sn + r.y * cs);
    if (u <= 0.0) d = -length(float2(u, v));
    else if (u >= L) d = -length(float2(u - L, v));
    else {
      float s = u / L * 256.0; int k = min(int(s), 255); float f = s - float(k);
      float y0 = ficusProfile[k], y1 = ficusProfile[k + 1];
      float slope = w * (y1 - y0) * 256.0 / L;
      d = (w * lerp(y0, y1, f) - v) / sqrt(1.0 + slope * slope);
    }
  }
  float cover = clamp(0.5 + d, 0.0, 1.0);
  if (cover <= 0.0) discard;
  int layer = int(i.a.y + 0.5);
  return float4(layer == 0 ? 1.0 - cover : 1.0, layer == 1 ? 1.0 - cover : 1.0, layer == 2 ? 1.0 - cover : 1.0, 1.0);
}
#endif
