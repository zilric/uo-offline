// =========================================================================
// InteriorTileFinder.cs — finds a safe, walkable interior tile to stand a
// vendor on, instead of a hardcoded offset from the house's sign that can
// clip into a wall, a door, or a locked-down fixture depending on which
// house style got picked.
// =========================================================================

using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class InteriorTileFinder
{
    // How close a candidate tile can sit to a door before it's rejected -
    // close enough and a spawned vendor blocks the doorway.
    private const int DoorClearance = 1;

    // How close a candidate tile can sit to a locked-down fixture (forge,
    // anvil, keg, bookcase, ...) before it's rejected - the vendor
    // shouldn't spawn standing inside/on top of one.
    private const int FixtureClearance = 1;

    // Scans every rectangle in the house's own floor plan (BaseHouse.Area
    // - the same data HousePlacement.Check itself validates against) for
    // the first tile that's genuinely walkable, inside the house, clear
    // of doors and fixtures, and not the house's own multi tile. Returns
    // false (with Point3D.Zero / Direction.South) if nothing qualifies,
    // which callers should treat as "fall back to the sign location."
    public static bool TryFindVendorSpot(BaseHouse house, out Point3D loc, out Direction facing)
    {
        loc = Point3D.Zero;
        facing = Direction.South;

        var map = house?.Map;
        if (house == null || map == null || map == Map.Internal)
        {
            return false;
        }

        var faceTarget = house.Sign?.Location ?? house.BanLocation;

        foreach (var rect in house.Area)
        {
            var x0 = house.X + rect.X;
            var y0 = house.Y + rect.Y;

            for (var dx = 0; dx < rect.Width; dx++)
            {
                for (var dy = 0; dy < rect.Height; dy++)
                {
                    var x = x0 + dx;
                    var y = y0 + dy;

                    if (!TryCandidate(house, map, x, y, out var candidate))
                    {
                        continue;
                    }

                    loc = candidate;
                    facing = DirectionTo(candidate, faceTarget);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryCandidate(BaseHouse house, Map map, int x, int y, out Point3D candidate)
    {
        candidate = Point3D.Zero;

        var z = map.GetAverageZ(x, y);
        var loc = new Point3D(x, y, z);

        // Genuinely inside the house's floor plan, not just inside its
        // bounding rectangle (Area can be an L-shape / have carve-outs).
        if (!house.IsInside(loc, 16))
        {
            return false;
        }

        // Walls, doors as static blockers, other mobiles, no floor to
        // stand on, etc. - the same collision check used elsewhere in
        // this codebase for landing a mobile on a tile (see
        // MoongateTravel's arrival placement).
        if (!map.CanSpawnMobile(x, y, z))
        {
            return false;
        }

        if (TooCloseToADoor(house, x, y))
        {
            return false;
        }

        if (TooCloseToAFixture(house, x, y, z))
        {
            return false;
        }

        candidate = loc;
        return true;
    }

    private static bool TooCloseToADoor(BaseHouse house, int x, int y)
    {
        if (house.Doors == null)
        {
            return false;
        }

        foreach (var door in house.Doors)
        {
            if (door?.Deleted != false)
            {
                continue;
            }

            if (Utility.InRange(door.X, door.Y, x, y, DoorClearance))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TooCloseToAFixture(BaseHouse house, int x, int y, int z)
    {
        foreach (var fixture in house.LockDowns)
        {
            if (fixture?.Deleted != false)
            {
                continue;
            }

            if (Utility.InRange(fixture.X, fixture.Y, x, y, FixtureClearance) &&
                System.Math.Abs(fixture.Z - z) <= 4)
            {
                return true;
            }
        }

        return false;
    }

    // UO's Direction enum is NOT a bitwise compass (its [Flags] attribute
    // covers only the separate Running bit) - it's 8 sequential values in
    // its own rotated naming (North=0, Right=NE, East, Down=SE, South,
    // Left=SW, West, Up=NW), so diagonals need their own named value, not
    // an OR of two cardinals. Bearing is measured clockwise from North in
    // UO's screen-space axes (X east, Y south).
    private static Direction DirectionTo(Point3D from, Point3D to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        if (dx == 0 && dy == 0)
        {
            return Direction.South;
        }

        var bearing = System.Math.Atan2(dx, -dy);
        if (bearing < 0)
        {
            bearing += System.Math.Tau;
        }

        var index = (int)System.Math.Round(bearing / (System.Math.PI / 4.0)) % 8;

        return index switch
        {
            0 => Direction.North,
            1 => Direction.Right, // NE
            2 => Direction.East,
            3 => Direction.Down,  // SE
            4 => Direction.South,
            5 => Direction.Left,  // SW
            6 => Direction.West,
            7 => Direction.Up,    // NW
            _ => Direction.South
        };
    }
}
