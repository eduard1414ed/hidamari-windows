// The reference numbers of Tests/Program.cs, worked out by the Mac app's own Swift (Mac update 1.4 and on).
//
//   Tests/make-reference.sh <komorebi folder>
//
// builds this file together with the Mac's Sources/Wall/WallColor.swift, WallLighting.swift, Motion.swift and
// WindowLeaves.swift into a scratch folder (nothing is written into komorebi) and runs it:
//
//   make-reference <old lighting.json> <rig folder> <out folder>
//
// Writes into <out folder>:
// - lighting.json: the shader's numbers for every state. The inputs of the states the Mac's tools/windows-stage4.swift
//   once read from the prototype (the 119 compared on the screen, 624 more moments) are taken from the old file and
//   worked out again with today's Swift; beside them the states of update 1.4 (names "v14-…"): the four new scenes
//   at many moments and places, mirrored and not, the Tree rustling, the window and the blinds in motion with every
//   plant and weather.
// - motion.json: the motion's clocks (dapples, water, Tree, window) run over the same moments and winds as here,
//   frame by frame, and Wind.tree() for a list of winds.
// - leaves.json: WindowLeaves: the posed pieces of the hanging branch and the palm for a few moments, winds and layers,
//   the ficus's shapes for a few moments, where each layer goes (place()), the leaf's outline (leafProfile).
//
// This tool lives in the Windows repo (the Mac's tools/ are the Mac's); it reads the Mac's sources as they are.

import Foundation

let args = CommandLine.arguments
guard args.count >= 4 else { print("usage: make-reference <old lighting.json> <rig folder> <out folder>"); exit(64) }
let oldURL = URL(fileURLWithPath: args[1]), rigDir = URL(fileURLWithPath: args[2]), out = URL(fileURLWithPath: args[3])
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

// ---------- the shader's numbers ----------

func motionJSON(_ m: Motion) -> [String: Any] {
    ["t": m.t, "wind": m.wind, "windLeaves": m.windLeaves, "moving": m.moving, "windMs": m.windMs, "leafT": m.leafT, "wt": m.wt]
}
func customJSON(_ c: CustomWall) -> [String: Any] {
    var o: [String: Any] = ["L": c.L, "x": c.x, "y": c.y, "k": c.k, "rough": c.rough, "shade": NSNull()]
    if let s = c.shade { o["shade"] = ["x": s.x, "y": s.y] }
    return o
}
func inputJSON(_ f: WallFrameInput) -> [String: Any] {
    ["ms": f.ms, "minutes": f.minutes, "lat": f.lat, "lng": f.lng, "scene": f.scene, "wall": f.wall, "custom": customJSON(f.custom),
     "winPlant": f.winPlant, "cloud": f.cloud, "weather": f.weather, "width": f.width, "height": f.height, "mirror": f.mirror,
     "motion": motionJSON(f.motion)]
}
func uniformsJSON(_ u: WallUniformValues) -> [String: Any] {
    var o: [String: Any] = [
        "res": u.res, "wallH": u.wallH, "tex": u.tex, "src": u.src, "corner": u.corner, "phase": u.phase, "calm": u.calm,
        "depth": u.depth, "wt": u.wt, "plantRect": u.plantRect, "l1": u.l1, "rough": u.rough, "c1": u.c1, "frostBottom": u.frostBottom,
        "win1": u.win1, "wall": u.wall, "scene": u.scene, "amb": u.amb, "layers": u.layers, "weather": u.weather, "t": u.t,
        "wind": u.wind, "windLeaves": u.windLeaves, "mirror": u.mirror, "plant": "\(u.plant)", "sunDegrees": u.sunDegrees,
        // update 1.4
        "moving": u.moving, "phaseDay": u.phaseDay, "glassNight": u.glassNight, "rainbow": u.rainbow,
        "readsPlant": u.readsPlant, "readsTree": u.readsTree, "all": u.all, "leaves": NSNull(), "tree": NSNull(),
    ]
    if let l = u.leaves { o["leaves"] = ["plant": l.plant, "scene": l.scene, "ms": l.ms, "t": l.t] }
    if let k = u.tree { o["tree"] = ["offX": k.off.0, "offY": k.off.1, "k": k.k, "t": k.t, "wind": k.wind] }
    return o
}

func number(_ o: [String: Any], _ k: String) -> Double { (o[k] as! NSNumber).doubleValue }
func int(_ o: [String: Any], _ k: String) -> Int { (o[k] as! NSNumber).intValue }

var lighting: [[String: Any]] = []

// the old states, worked out again
let oldData = try! Data(contentsOf: oldURL)
let old = try! JSONSerialization.jsonObject(with: oldData) as! [[String: Any]]
for item in old {
    let name = item["name"] as! String
    if name.hasPrefix("v14-") { continue }
    let i = item["input"] as! [String: Any], c = i["custom"] as! [String: Any], m = i["motion"] as! [String: Any]
    var shade: PadPoint? = nil
    if let s = c["shade"] as? [String: Any] { shade = PadPoint(x: number(s, "x"), y: number(s, "y")) }
    var f = WallFrameInput(ms: number(i, "ms"), minutes: number(i, "minutes"), lat: number(i, "lat"), lng: number(i, "lng"),
                           scene: int(i, "scene"), wall: int(i, "wall"),
                           custom: CustomWall(L: number(c, "L"), x: number(c, "x"), y: number(c, "y"), shade: shade, k: number(c, "k"), rough: number(c, "rough")),
                           winPlant: int(i, "winPlant"), cloud: number(i, "cloud"), weather: int(i, "weather"),
                           width: int(i, "width"), height: int(i, "height"))
    f.mirror = (i["mirror"] as! NSNumber).boolValue
    f.motion = Motion(t: number(m, "t"), wind: number(m, "wind"), windLeaves: number(m, "windLeaves"))
    if let mv = m["moving"] as? NSNumber {
        f.motion.moving = mv.boolValue; f.motion.windMs = number(m, "windMs"); f.motion.leafT = number(m, "leafT"); f.motion.wt = number(m, "wt")
    }
    lighting.append(["name": name, "input": inputJSON(f), "uniforms": uniformsJSON(WallLighting.uniforms(f))])
}
let oldCount = lighting.count

let places: [(Double, Double)] = [(52.52, 13.405), (-33.87, 151.21), (64.15, -21.94), (1.29, 103.85)]
let sizes: [(Int, Int)] = [(1920, 1080), (2560, 1440), (1366, 768), (2160, 1350), (1080, 1920)]
func custom(_ k: Int) -> CustomWall {
    CustomWall(L: 0.8 + 0.01 * Double(k % 10), x: Double(k % 11) / 10, y: Double(k % 7) / 6, shade: k % 3 == 0 ? PadPoint(x: 0.3, y: 0.2) : nil,
               k: Double(k % 5) / 4, rough: Double(k % 3))
}
func moment(_ doy: Int, _ m: Int) -> Double { (1_767_225_600 + Double((doy - 1) * 86400)) * 1000 + Double(m) * 60000 }   // 2026, UTC

// the four new scenes at many moments: every 29 minutes of four days, a place, a wall, a size, mirrored or not
var k = 0
let newScenes = [4, 6, 7, 8]
for doy in [20, 100, 172, 300] {
    for m in stride(from: 0, to: 1440, by: 29) {
        let (lat, lng) = places[k % 4], (w, h) = sizes[k % 5]
        var f = WallFrameInput(ms: moment(doy, m), minutes: Double((m + 60 * (k % 5)) % 1440), lat: lat, lng: lng, scene: newScenes[k % 4],
                               wall: k % 7 == 6 ? -1 : k % 6, custom: custom(k), winPlant: [0, 2, 5][k % 3], cloud: Double(k % 5) / 4,
                               weather: k % 3, width: w, height: h)
        f.mirror = (k / 4) % 2 == 0
        // the Tree rustling, half the time (its clock's motion: uT, its strength in uWind)
        if f.scene == 7 && (k / 4) % 3 != 0 { f.motion = Motion(t: Double(k) * 2.3, wind: Wind.tree(Double(k % 16)), windLeaves: 0) }
        lighting.append(["name": "v14-scene\(f.scene)-\(k)", "input": inputJSON(f), "uniforms": uniformsJSON(WallLighting.uniforms(f))])
        k += 1
    }
}
// the window and the blinds in motion: every plant and weather, the rain's slow clock running or still
var n = 0
for scene in [1, 2] {
    for plant in [0, 2, 5] {
        for weather in [0, 1, 2] {
            for (doy, m) in [(172, 8 * 60), (100, 13 * 60 + 7), (300, 17 * 60 + 40), (20, 2 * 60)] {
                let (lat, lng) = places[n % 4], (w, h) = sizes[n % 5]
                var f = WallFrameInput(ms: moment(doy, m), minutes: Double(m), lat: lat, lng: lng, scene: scene, wall: n % 7 == 6 ? -1 : n % 6,
                                       custom: custom(n), winPlant: plant, cloud: weather == 0 ? Double(n % 3) / 3 : 0.2, weather: weather,
                                       width: w, height: h)
                f.mirror = n % 2 == 1
                let ms = [0.0, 4, 9.5, 15, 22][n % 5], t = [0.0, 0.7, 13.37, 600.25, 1199.9][(n / 5) % 5]
                let wt = n % 4 == 3 ? -1 : MotionClock.stillWT(basePhase: WallLighting.phase(minutes: f.minutes)) + t / 1500
                f.motion = MotionClock.windowMotion(t: t, ms: ms, leafT: t + Double(n) * 37.1, wt: wt)
                lighting.append(["name": "v14-moving-scene\(scene)-plant\(plant)-weather\(weather)-\(n)", "input": inputJSON(f),
                                 "uniforms": uniformsJSON(WallLighting.uniforms(f))])
                n += 1
            }
        }
    }
}
let data = try! JSONSerialization.data(withJSONObject: lighting, options: [.sortedKeys])
try! data.write(to: out.appendingPathComponent("lighting.json"))
print("lighting.json: \(lighting.count) states (\(oldCount) old ones worked out again, \(lighting.count - oldCount) of update 1.4)")

// ---------- the motion's clocks ----------

func frameJSON(_ fr: MotionFrame) -> [String: Any] {
    ["motion": motionJSON(fr.motion), "from": fr.from.map(motionJSON) ?? NSNull(), "mix": fr.mix]
}
var clocks: [[String: Any]] = []
let dts = [0.5, 1.1, 2.3, 7.7, 13.1, 1.0 / 30, 1.0 / 30, 0.25]
let winds = [4.0, 4, 9.5, 0, 15, 22, 6.2]
func phaseAt(_ now: Double) -> Double { WallLighting.phase(minutes: ((now / 300).rounded(.down) * 5).truncatingRemainder(dividingBy: 1440)) }
for (name, kind, scene) in [("dapples", MotionClock.Kind.leaves, 0), ("water", .water, 3), ("tree", .tree, 7), ("window", .window, 1), ("blinds", .window, 2)] {
    precondition(MotionClock.Kind(scene: scene) == kind)
    var now = 1000.0, wind = 4.0
    var clock = MotionClock(kind: kind, now: now, windMs: wind, basePhase: phaseAt(now))
    var frames: [[String: Any]] = []
    for i in 0..<800 {
        // a new wind now and then for the first 400 frames, then the same wind for longer than leavesSpan
        if i > 0 && i < 400 && i % 61 == 0 { wind = winds[(i / 61) % winds.count] }
        if i > 0 { now += dts[i % dts.count] }
        let fr = clock.frame(at: now, windMs: wind, basePhase: phaseAt(now))
        frames.append(["now": now, "windMs": wind, "basePhase": phaseAt(now), "frame": frameJSON(fr)])
    }
    clocks.append(["name": name, "scene": scene, "start": ["now": 1000.0, "windMs": 4.0, "basePhase": phaseAt(1000)], "frames": frames])
}
do {
    // the window's slow clock over days without a pause: it is set back once it has run past wtLimit
    var now = 50.0
    let wind = 4.0
    var clock = MotionClock(kind: .window, now: now, windMs: wind, basePhase: phaseAt(now))
    var frames: [[String: Any]] = []
    for i in 0..<60 {
        if i > 0 { now += i % 3 == 2 ? 2 : 90_000 }
        let fr = clock.frame(at: now, windMs: wind, basePhase: phaseAt(now))
        frames.append(["now": now, "windMs": wind, "basePhase": phaseAt(now), "frame": frameJSON(fr)])
    }
    clocks.append(["name": "window-days", "scene": 1, "start": ["now": 50.0, "windMs": wind, "basePhase": phaseAt(50)], "frames": frames])
}
let treeWinds = [-1.0, 0, 0.5, 2, 4, 7.3, 10, 13.6, 14, 20]
let motionOut: [String: Any] = ["clocks": clocks, "windTree": treeWinds.map { [$0, Wind.tree($0)] },
                                "scale": [0.0, 3, 7.5, 15, 30].map { let s = WindowLeaves.scale($0); return [$0, s.a, s.amp] }]
try! JSONSerialization.data(withJSONObject: motionOut, options: [.sortedKeys]).write(to: out.appendingPathComponent("motion.json"))
print("motion.json: \(clocks.count) clocks")

// ---------- WindowLeaves ----------

// floats written as Swift prints them (the shortest text that reads back as the same float)
func floats(_ a: [Float]) -> String { "[" + a.map { "\($0)" }.joined(separator: ",") + "]" }
func num(_ x: Double) -> String { "\(x)" }
var parts: [String] = []
parts.append("\"profile\":" + floats(WindowLeaves.leafProfile))
var rigParts: [String] = []
for plant in [2, 5] {
    let rig = try! WindowLeaves.Rig(json: Data(contentsOf: rigDir.appendingPathComponent("rig\(plant).json")))
    let places = (0..<3).map { l -> String in let p = WindowLeaves.place(plant: plant, rig: rig, layer: l); return floats([p.x, p.y, p.z, p.w]) }
    var poses: [String] = []
    // one rig asked in turn, as the drawing does (its memos carried from call to call)
    for (t, ms, layer) in [(0.0, 4.0, 0), (1.0 / 30, 4.0, 0), (0.7, 0, 1), (13.37, 9.5, 2), (13.37 + 1.0 / 30, 9.5, 2), (600.25, 15, 0), (3600.5, 22, 1), (86_400.125, 6.2, 0)] {
        let tt = t + WindowLeaves.layers[layer].off, seed = Double(31 * layer + 3)
        let inst = rig.pose(t: tt, ms: ms, seed: seed)
        poses.append("{\"t\":\(num(tt)),\"ms\":\(num(ms)),\"seed\":\(num(seed)),\"data\":\(floats(inst))}")
    }
    rigParts.append("{\"plant\":\(plant),\"w\":\(rig.w),\"h\":\(rig.h),\"count\":\(rig.count),\"places\":[\(places.joined(separator: ","))],\"poses\":[\(poses.joined(separator: ","))]}")
}
parts.append("\"rigs\":[" + rigParts.joined(separator: ",") + "]")
var ficusParts: [String] = []
let memos = WindowLeaves.Memos()
for (t, ms) in [(0.0, 4.0), (1.0 / 30, 4.0), (97.3, 12), (3600.5, 0)] {
    let shapes = WindowLeaves.ficus(t: t, ms: ms, memos: memos)
    ficusParts.append("{\"t\":\(num(t)),\"ms\":\(num(ms)),\"data\":\(floats(shapes))}")
}
parts.append("\"ficus\":[" + ficusParts.joined(separator: ",") + "]")
try! ("{" + parts.joined(separator: ",") + "}").write(to: out.appendingPathComponent("leaves.json"), atomically: true, encoding: .utf8)
print("leaves.json: two rigs, \(ficusParts.count) ficus poses, the leaf's outline")
