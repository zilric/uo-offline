// =========================================================================
// MaritimeWaterFinder.cs — SP-048/SP-049: water-tile and hull-clearance
// detection for the ambient fisher fleet.
//
// SP-049 root cause: SP-048's TryFindOpenWater only checked that a tile
// and its four orthogonal neighbors were water-tagged — it never
// validated an actual hull footprint against real map content (piers,
// other boats, the shoreline itself), and never knew where the 13
// charter moorings were, so ambient boats could and did land inside
// active ferry slips or with part of their hull over dry land. This pass
// replaces that check with BaseBoat.CanFit — the exact same validation
// the engine itself runs before letting a real boat move or dry-dock —
// plus an explicit minimum-distance check against every
// FerryRouteRegistry stop.
//
// CanFit is an instance method, but nothing it reads depends on the
// calling boat's own placement: `map` and the candidate point are both
// parameters, and its one self-referential check (`!Contains(item)`,
// letting a boat "fit" against its own already-placed hull) is always
// false for a probe that has never been moved onto a real map. So one
// SmallBoat instance, constructed once and never MoveToWorld'd, can
// safely validate every candidate for the whole seeding pass — cheaper
// than constructing a fresh throwaway boat per attempt, and exactly as
// accurate. AmbientFisherSeeder is responsible for deleting the probe
// once seeding finishes (it would otherwise leak an entry in BaseBoat's
// own static Boats list forever).
// =========================================================================

using Server;
using Server.Engines.FerrySystem;
using Server.Multis;

namespace Server.Engines.Maritime;

public static class MaritimeWaterFinder
{
    // Water land-tile IDs (168-171, 310-311) and water static tile IDs
    // (0x1796-0x17B2) — the same ranges BaseBoat.CanFit uses to decide
    // whether a tile is water, in Projects/UOContent/Multis/Boats/BaseBoat.cs.
    private static bool IsWaterLandTile(int id) => id is >= 168 and <= 171 or >= 310 and <= 311;

    private static bool IsWaterStaticTile(int id) => id is >= 0x1796 and <= 0x17B2;

    // Minimum tile distance an ambient boat must keep from every charter
    // mooring, so [seedfishers can never land one inside or immediately
    // beside an active ferry slip.
    private const int FerryStandoffTiles = 20;

    private static readonly Direction[] AllFacings =
    {
        Direction.North, Direction.East, Direction.South, Direction.West
    };

    public static bool TryGetWaterZ(Map map, int x, int y, out int z)
    {
        z = 0;
        if (map == null || map == Map.Internal)
        {
            return false;
        }

        var found = false;
        var landTile = map.Tiles.GetLandTile(x, y);
        if (IsWaterLandTile(landTile.ID))
        {
            z = landTile.Z;
            found = true;
        }

        foreach (var tile in map.Tiles.GetStaticAndMultiTiles(x, y))
        {
            if (IsWaterStaticTile(tile.ID) && (!found || tile.Z > z))
            {
                z = tile.Z;
                found = true;
            }
        }

        return found;
    }

    // True only if (x, y) is at least FerryStandoffTiles from every
    // charter mooring's BoatLocation (Chebyshev distance, matching how
    // CanFit's own bounding-box checks measure).
    public static bool IsClearOfFerryStops(int x, int y)
    {
        foreach (var stop in FerryRouteRegistry.Stops)
        {
            var dx = System.Math.Abs(stop.BoatLocation.X - x);
            var dy = System.Math.Abs(stop.BoatLocation.Y - y);
            if (System.Math.Max(dx, dy) < FerryStandoffTiles)
            {
                return false;
            }
        }

        return true;
    }

    // Random-samples a square region (±radius tiles around `center`) for
    // a spot that is simultaneously: real open water, at least
    // FerryStandoffTiles from every charter mooring, and passes a real
    // BaseBoat.CanFit hull-footprint check (no land, no statics, no other
    // multi) for a randomly chosen cardinal facing. Returns false
    // (leaving `result`/`facing` at their defaults) if nothing is found
    // within the attempt budget — callers should treat that as "skip
    // this boat," never force a bad placement.
    public static bool TryFindClearBoatSpot(
        Map map, Point3D center, int radius, SmallBoat probe,
        out Point3D result, out Direction facing, int attempts = 60
    )
    {
        result = center;
        facing = Direction.North;

        if (map == null || map == Map.Internal || probe == null)
        {
            return false;
        }

        for (var i = 0; i < attempts; i++)
        {
            var tx = center.X + Utility.RandomMinMax(-radius, radius);
            var ty = center.Y + Utility.RandomMinMax(-radius, radius);

            if (!IsClearOfFerryStops(tx, ty))
            {
                continue;
            }

            if (!TryGetWaterZ(map, tx, ty, out var tz))
            {
                continue;
            }

            var candidateFacing = AllFacings[Utility.Random(AllFacings.Length)];
            var itemId = candidateFacing switch
            {
                Direction.North => probe.NorthID,
                Direction.East  => probe.EastID,
                Direction.South => probe.SouthID,
                _               => probe.WestID
            };

            var candidate = new Point3D(tx, ty, tz);
            if (!probe.CanFit(candidate, map, itemId))
            {
                continue;
            }

            result = candidate;
            facing = candidateFacing;
            return true;
        }

        return false;
    }
}
