#!/usr/bin/env python3
"""The app's own list of cities for the place search, from GeoNames (https://www.geonames.org, CC BY 4.0).

    python3 tools/make-cities.py            downloads what is missing into build/geonames/, writes Hidamari/Resources/cities.gz
    python3 tools/make-cities.py --fresh    downloads everything again (GeoNames changes daily)
    python3 tools/make-cities.py --sizes    only tells what each choice would cost, writes nothing

What goes in: every populated place of cities1000 (at least 1000 people, or a seat of government), without parts of
cities (PPLX) and places that are gone (historical, abandoned, destroyed). For each: the English name GeoNames gives,
the name in the country's first language when it is another one (Москва for Moscow, München for Munich), the country,
the region (to tell apart places of one name in one country), the point to 2 decimals (about 1 km), the time zone and
the population. The file is plain text, gzip-compressed; Hidamari/Core/Cities.cs reads it.
"""
import argparse
import csv
import gzip
import io
import sys
import unicodedata
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / "build" / "geonames"
OUT = ROOT / "Hidamari" / "Resources" / "cities.gz"
BASE = "https://download.geonames.org/export/dump/"
FILES = ["cities1000.zip", "countryInfo.txt", "admin1CodesASCII.txt", "alternateNamesV2.zip"]
LEFT_OUT = {"PPLX", "PPLH", "PPLQ", "PPLW", "PPLCH"}
csv.field_size_limit(10**9)


def fetch(fresh):
    CACHE.mkdir(parents=True, exist_ok=True)
    for f in FILES:
        path = CACHE / f
        if path.exists() and not fresh:
            continue
        print(f"downloading {BASE}{f}", file=sys.stderr)
        req = urllib.request.Request(BASE + f, headers={"User-Agent": "Hidamari city list builder"})
        with urllib.request.urlopen(req) as r, open(path.with_suffix(".part"), "wb") as out:
            while chunk := r.read(1 << 20):
                out.write(chunk)
        path.with_suffix(".part").rename(path)


def rows(name, inner=None):
    path = CACHE / name
    if name.endswith(".zip"):
        z = zipfile.ZipFile(path)
        stream = io.TextIOWrapper(z.open(inner or name.replace(".zip", ".txt")), encoding="utf-8")
    else:
        stream = open(path, encoding="utf-8")
    for r in csv.reader(stream, delimiter="\t", quoting=csv.QUOTE_NONE):
        if r and not r[0].startswith("#"):
            yield r


def fold(s):
    """As Cities.Fold in the app: no accents, no case (only to tell whether a native name differs)."""
    return "".join(c for c in unicodedata.normalize("NFKD", s) if not unicodedata.combining(c)).casefold()


def rough(n):
    """The population to two figures (123456 -> 120000): it only orders the search, and packs smaller."""
    if n < 100:
        return n
    k = 10 ** (len(str(n)) - 2)
    return round(n / k) * k


def clean(s):
    return s.replace("\t", " ").replace("\n", " ").strip()


def build(min_pop=0, natives=True):
    countries, languages = {}, {}
    for r in rows("countryInfo.txt"):
        countries[r[0]] = r[4]
        languages[r[0]] = r[15].split(",")[0].split("-")[0] if len(r) > 15 and r[15] else ""
    admins = {r[0]: r[1] for r in rows("admin1CodesASCII.txt")}
    cities = {}
    for r in rows("cities1000.zip"):
        if r[7] in LEFT_OUT or r[8] not in countries or not r[17]:
            continue
        if int(r[14] or 0) < min_pop and r[7] != "PPLC":
            continue
        cities[r[0]] = r
    native = {}
    if natives:
        best = {}
        for r in rows("alternateNamesV2.zip", "alternateNamesV2.txt"):
            c = cities.get(r[1])
            if c is None or not r[2] or r[2] != languages.get(c[8]) or r[6] == "1" or r[7] == "1":
                continue
            score = (r[4] == "1") * 2 + (r[5] != "1")
            if r[1] not in best or score > best[r[1]][0]:
                best[r[1]] = (score, clean(r[3]))
        native = {k: v[1] for k, v in best.items() if fold(v[1]) != fold(cities[k][1])}

    # by country, as GeoNames lists them: neighbours look alike and pack smaller. The app sorts by population itself.
    order = sorted(cities.values(), key=lambda r: (r[8], r[10], r[1]))
    used_countries = sorted({r[8] for r in order})
    country_index = {c: i for i, c in enumerate(used_countries)}
    # the region only where one country has two places of one name: elsewhere the country says enough
    seen = {}
    for r in order:
        seen[(r[8], fold(r[1]))] = seen.get((r[8], fold(r[1])), 0) + 1
    used_admins, admin_index = [], {}
    zones, zone_index = [], {}
    lines = []
    for r in order:
        admin = -1
        key = f"{r[8]}.{r[10]}"
        if seen[(r[8], fold(r[1]))] > 1 and key in admins:
            if key not in admin_index:
                admin_index[key] = len(used_admins)
                used_admins.append(admins[key])
            admin = admin_index[key]
        if r[17] not in zone_index:
            zone_index[r[17]] = len(zones)
            zones.append(r[17])
        lat, lng = round(float(r[4]) * 100), round(float(r[5]) * 100)
        lines.append(f"{clean(r[1])}\t{native.get(r[0], '')}\t{country_index[r[8]]}\t{'' if admin < 0 else admin}\t{lat}\t{lng}\t{zone_index[r[17]]}\t{rough(int(r[14] or 0))}")
    text = "\n".join(
        ["hidamari-cities 1"]
        + [f"C\t{c}\t{countries[c]}" for c in used_countries]
        + [f"A\t{a}" for a in used_admins]
        + [f"Z\t{z}" for z in zones]
        + ["-"]
        + lines) + "\n"
    return text.encode("utf-8"), len(order), len(native)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fresh", action="store_true")
    ap.add_argument("--sizes", action="store_true")
    a = ap.parse_args()
    fetch(a.fresh)
    if a.sizes:
        print("| places | how many | in the app, MB |")
        print("|---|---|---|")
        for pop in (15000, 5000, 1000):
            for nat in (False, True):
                data, n, k = build(pop if pop > 1000 else 0, nat)
                print(f"| at least {pop} people{', native names too' if nat else ', English names'} | {n}{f' ({k} native names)' if nat else ''} | {len(gzip.compress(data, 9, mtime=0)) / 1e6:.2f} |")
        return
    data, n, k = build()
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(gzip.compress(data, 9, mtime=0))
    print(f"{OUT.relative_to(ROOT)}: {n} places, {k} native names, {OUT.stat().st_size / 1e6:.2f} MB ({len(data) / 1e6:.2f} MB unpacked)")


if __name__ == "__main__":
    main()
