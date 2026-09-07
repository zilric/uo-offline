#!/usr/bin/env python3
"""Author the missing dungeon stair records from what is actually in the world.

Input  : Data/Live/padmap_report.json  (written by [MapPads / padmap_request.txt)
Output : new DungeonDescend / DungeonAscend records merged into
         playerbots/data/Destinations/destinations.json

Why this exists
---------------
41% of dungeon room points sit on a floor whose waypoint component holds no
DungeonDescend record, so a crawler that lands there can never roll a way
deeper -- it shuffles between its two room points until the run timer ends.
That is why crawlers live on level 1 (L1 3376 : L2 95 : L3 3 in a 15 minute
soak).

The stairs are not missing from the WORLD. They are real Teleporter items,
already placed. Only the destination records are missing. This reads the
sweep of real teleporters and writes the records for the ones that sit in
mapped territory.

A staircase is several adjacent tiles that all lead to the same landing, so
tiles are grouped by (from floor, to floor, landing) and one record is
written per staircase -- on the tile closest to its waypoint anchor, because
that is the one a crawler can actually route to.

Run with --write to modify destinations.json; without it, it only reports.
"""

import argparse
import collections
import io
import json
import os
import shutil
import sys
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEST = os.path.join(REPO, "playerbots", "data", "Destinations", "destinations.json")

MAX_LEG = 38  # WaypointGraph.MaxLegDistance


def load(path):
    with io.open(path, encoding="utf-8-sig") as fh:
        return json.load(fh)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("report", help="path to padmap_report.json")
    ap.add_argument("--dest", default=DEST, help="destinations.json to merge into")
    ap.add_argument("--write", action="store_true", help="actually modify destinations.json")
    args = ap.parse_args()

    pads = load(args.report)
    doc = load(args.dest)
    dests = doc["Destinations"] if isinstance(doc, dict) else doc

    existing_names = {d.get("Name") for d in dests}

    # City tag per dungeon, copied from whatever records that dungeon already
    # has, so the new ones are tagged the same way as their neighbours.
    city_of = {}
    for d in dests:
        dg = d.get("Dungeon")
        if dg and d.get("City") and dg not in city_of:
            city_of[dg] = d["City"]

    # Usable = no record yet, both ends resolve to a mapped floor, the pad is
    # live, and a crawler can reach it.
    usable = [
        p for p in pads
        if not p["Covered"]
        and p["FromDungeon"] and p["ToDungeon"]
        and p["Active"]
        and p["NearestWaypoint"] and p["WaypointDist"] <= MAX_LEG
    ]

    # One record per STAIRCASE, not per tile. A staircase is several
    # adjacent pad tiles going to the same floor -- each tile has its own
    # slightly different landing point, so grouping on the exact landing
    # splits one three-tile stair into three records and triples that
    # floor's descend weight. Cluster adjacent source tiles instead.
    STAIR_SPAN = 3

    stairs = []
    for p in usable:
        placed = False
        for group in stairs:
            g = group[0]
            if (g["FromDungeon"], g["FromLevel"], g["ToDungeon"], g["ToLevel"]) !=                (p["FromDungeon"], p["FromLevel"], p["ToDungeon"], p["ToLevel"]):
                continue
            if any(max(abs(q["X"] - p["X"]), abs(q["Y"] - p["Y"])) <= STAIR_SPAN
                   for q in group):
                group.append(p)
                placed = True
                break
        if not placed:
            stairs.append([p])

    new_records = []
    skipped_same = 0
    stairs.sort(key=lambda g: (g[0]["FromDungeon"], g[0]["FromLevel"],
                               g[0]["ToDungeon"], g[0]["ToLevel"],
                               g[0]["X"], g[0]["Y"]))
    for tiles in stairs:
        g = tiles[0]
        from_dg, from_lv = g["FromDungeon"], g["FromLevel"]
        to_dg, to_lv = g["ToDungeon"], g["ToLevel"]

        # A pad that lands back on the floor it started from is a local
        # shortcut, not a stair. Recording it as a Descend would just give
        # the crawler a way to spin on the spot.
        if from_dg == to_dg and from_lv == to_lv:
            skipped_same += 1
            continue

        if to_lv > from_lv:
            kind, word = "DungeonDescend", "Descend"
        elif to_lv < from_lv:
            kind, word = "DungeonAscend", "Ascend"
        else:
            # Same depth, different segment: a way onward across a floor that
            # is split into disconnected islands. Descend is the type the
            # crawler rolls in normal mode, which is what gets it off a
            # two-room island; exit mode's mislabelled-pad fallback already
            # copes with it pointing sideways rather than up.
            kind, word = "DungeonDescend", "Descend"

        # The tile a crawler can best route to.
        tile = min(tiles, key=lambda t: t["WaypointDist"])

        name = f"{from_dg} {word} to L{to_lv}"
        n, suffix = name, 1
        while n in existing_names:
            suffix += 1
            n = f"{name} ({suffix})"
        existing_names.add(n)

        rec = {
            "Name": n,
            "X": tile["X"], "Y": tile["Y"], "Z": tile["Z"],
            "Type": kind,
            "City": city_of.get(from_dg, ""),
            "NearestWaypoint": tile["NearestWaypoint"],
            "Dungeon": from_dg,
            "Level": from_lv,
            "Arrivals": [{
                "X": tile["X"], "Y": tile["Y"], "Z": tile["Z"],
                "Waypoints": [tile["NearestWaypoint"]],
            }],
        }
        new_records.append(rec)
        print(f"  {kind:15s} {n}")
        print(f"      at ({tile['X']},{tile['Y']},{tile['Z']}) "
              f"-> {to_dg} L{to_lv}   wp {tile['NearestWaypoint']} "
              f"d{tile['WaypointDist']}   ({len(tiles)} pad tiles)")

    print()
    print(f"{len(pads)} teleporters swept, {len([p for p in pads if not p['Covered']])} with no record")
    print(f"{len(usable)} usable pad tiles -> {len(new_records)} new stair record(s)"
          f"{f', {skipped_same} same-floor shortcut(s) skipped' if skipped_same else ''}")

    unmapped = [p for p in pads
                if not p["Covered"] and (not p["FromDungeon"] or not p["ToDungeon"])]
    no_wp = [p for p in unmapped
             if not p["NearestWaypoint"] or p["WaypointDist"] > MAX_LEG]
    print(f"{len(unmapped)} pads still unusable: {len(no_wp)} of them are in dungeon "
          f"areas with NO waypoint coverage at all -- those need meshing first.")

    if not args.write:
        print("\n(dry run -- pass --write to merge)")
        return 0

    if not new_records:
        print("\nnothing to write")
        return 0

    backup = args.dest + ".bak-" + datetime.now().strftime("%Y%m%d-%H%M%S")
    shutil.copy2(args.dest, backup)
    print(f"\nbacked up {os.path.basename(args.dest)} -> {os.path.basename(backup)}")

    dests.extend(new_records)
    with io.open(args.dest, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"wrote {len(new_records)} record(s) into {args.dest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
