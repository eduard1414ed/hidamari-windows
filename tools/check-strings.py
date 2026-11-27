#!/usr/bin/env python3
"""The Windows app's texts must be the Mac app's.

Every text in Hidamari/Strings.cs has the name of a text in shared/Strings.swift (the Mac app's) with a capital
first letter (FollowSun = followSun) and must say exactly the same. A text that is not on the Mac is allowed only
with TEMP in its comment, which means it is not approved yet, or with "Windows only", which means it was approved
for Windows alone. Every text the card's page asks for
(S.something in Hidamari/Card/card.js) must be in Strings.cs.

Run by the GitHub check before the app is built; exits with an error and the reason when something differs.
"""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SWIFT = ROOT / "shared/Strings.swift"
CS = ROOT / "Hidamari/Strings.cs"
CARD = ROOT / "Hidamari/Card/card.js"
# names that differ on purpose: the Windows name -> the Mac name
RENAMED = {"name": "menuBarIcon"}


def swift_texts():
    out = {}
    for m in re.finditer(r'static let (\w+) = ("(?:[^"\\]|\\.)*"|\[[^\]]*\])', SWIFT.read_text(encoding="utf-8")):
        out[m.group(1)] = parse(m.group(2))
    # texts with a value in them: static func name(_ v: String) -> String { "Ver \(v)" } is "Ver {0}" on Windows
    for m in re.finditer(r'static func (\w+)\(_ (\w+): String\) -> String \{ "((?:[^"\\]|\\.)*)" \}', SWIFT.read_text(encoding="utf-8")):
        out[m.group(1)] = m.group(3).replace("\\(" + m.group(2) + ")", "{0}")
    # the links: static let name = URL(string: "...")! must be the same in Links in Strings.cs
    links = re.search(r'enum Links \{(.*?)\n\}', SWIFT.read_text(encoding="utf-8"), re.S)
    for m in re.finditer(r'static let (\w+) = URL\(string: "([^"]*)"\)!', links.group(1) if links else ""):
        out["link." + m.group(1)] = m.group(2)
    return out


def parse(v):
    v = v.strip()
    if v.startswith('"'):
        return v[1:-1]
    pairs = re.findall(r'(\d+)\s*(?::|\]\s*=)\s*"([^"]*)"', v)
    if pairs:
        return {int(k): t for k, t in pairs}
    return re.findall(r'"([^"]*)"', v)


def cs_texts():
    text = CS.read_text(encoding="utf-8")
    out = {}
    lines = text.splitlines()
    for i, line in enumerate(lines):
        m = re.search(r'public (?:const string|static readonly [\w<>\[\], ]+) (\w+) = (.*);\s*$', line)
        if not m:
            continue
        name, value = m.group(1), m.group(2)
        comment = " ".join(l.strip() for l in lines[max(0, i - 3):i] if l.strip().startswith("///")) + line
        if value.startswith('"'):
            parsed = value.strip('"')
        elif re.search(r'\[\d+\]\s*=', value):
            parsed = {int(k): t for k, t in re.findall(r'\[(\d+)\]\s*=\s*"([^"]*)"', value)}
        else:
            parsed = re.findall(r'"([^"]*)"', value)
        prefix = "link." if in_links(lines, i) else ""
        out[prefix + name[0].lower() + name[1:]] = (parsed, "TEMP" in comment, "Windows only" in comment)
    return out


def in_links(lines, i):
    """Is line i inside `static class Links`?"""
    for line in reversed(lines[:i]):
        m = re.match(r'\s*(?:public )?static class (\w+)', line)
        if m:
            return m.group(1) == "Links"
    return False


def main():
    mac, win = swift_texts(), cs_texts()
    errors = []
    for key, (value, temp, windows) in win.items():
        mac_key = RENAMED.get(key, key)
        if mac_key not in mac:
            if not temp and not windows:
                errors.append(f"{key}: not a text of the Mac app and not marked TEMP or Windows only")
            continue
        if mac[mac_key] != value:
            errors.append(f"{key}: {value!r} on Windows, {mac[mac_key]!r} on the Mac ({mac_key})")
    asked = set(re.findall(r'\bS\.(\w+)', CARD.read_text(encoding="utf-8")))
    for key in sorted(asked - set(win)):
        errors.append(f"card.js uses S.{key}, which Strings.cs does not have")
    if errors:
        sys.exit("check-strings:\n  " + "\n  ".join(errors))
    temp = sorted(k for k, (_, t, _w) in win.items() if t)
    print(f"check-strings: {len(win)} texts match the Mac app" + (f"; TEMP: {', '.join(temp)}" if temp else "; none TEMP"))


if __name__ == "__main__":
    main()
