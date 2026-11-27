#!/bin/zsh
# Works out Tests/lighting.json.gz, Tests/motion.json.gz and Tests/leaves.json.gz again with the Mac app's own Swift
# (see Tests/make-reference.swift). Needs a Mac with Swift and the Mac app's folder; writes nothing into that folder.
#   Tests/make-reference.sh <komorebi folder>
set -euo pipefail
here="${0:A:h}"
mac="${1:?usage: make-reference.sh <komorebi folder>}"
mac="${mac:A}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$here/make-reference.swift" "$work/main.swift"
swiftc -O -o "$work/make-reference" "$mac/Sources/Wall/WallColor.swift" "$mac/Sources/Wall/WallLighting.swift" \
  "$mac/Sources/Wall/Motion.swift" "$mac/Sources/Wall/WindowLeaves.swift" "$work/main.swift"
gzip -dc "$here/lighting.json.gz" > "$work/old-lighting.json"
mkdir -p "$work/out"
# the rigs as the Windows app has them (shared/textures, the Mac's Resources/metal byte for byte)
"$work/make-reference" "$work/old-lighting.json" "$here/../shared/textures" "$work/out"
for f in lighting motion leaves; do gzip -9 -n -c "$work/out/$f.json" > "$here/$f.json.gz"; done
ls -l "$here"/*.json.gz
