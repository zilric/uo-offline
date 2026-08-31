// =========================================================================
// InteriorTileFinder.cs — finds safe, walkable, GROUND-FLOOR interior tiles
// for 1-4 vendors to stand on (TryFindVendorSpots) or for clutter to sit
// against a wall (the shared helpers below, also used by
// DynamicClutterGenerator). Nothing here ever resolves to a sign-adjacent
// exterior tile, a doorway, or an upper-floor offset.
// =========================================================================

using System.Collections.Generic;
using Server.Items;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class InteriorTileFinder
{
    // How close a candidate tile can sit to a door before it's rejected -
    // close enough and a spawned vendor (or a piece of clutter) blocks the
    // doorway.
    private const int DoorClearance = 1;

    // Same idea for the house sign - it stands just outside the front
    // door, and IsInside() alone doesn't always reject the tile directly
    // underneath/beside it depending on house shape.
    private const int SignClearance = 1;

    // How close a candidate tile can sit to a locked-down fixture (forge,
    // anvil, keg, bookcase, clutter, ...) before it's rejected - nothing
    // should spawn standing inside/on top of one.
    private const int FixtureClearance = 1;

    // Ground-floor surface window, relative to house.Z (the multi's own
    // placement Z - the fixed baseline for every style regardless of
    // where its actual floorboards render). The negative low end covers a
    // slightly recessed threshold; +18 comfortably covers a raised wooden
    // floor while staying well under where a second story's own floor
    // starts (a full story is ~20 Z units in UO's house art) - so this
    // window can never resolve to an upper floor, a rooftop, or a
    // basement on a multi-story style like TwoStoryHouse.
    private const int MinGroundFloorOffset = -4;
    private const int MaxGroundFloorOffset = 18;


    // How far apart two vendor spots (found in the same TryFindVendorSpots
    // call) must be - the task's requested 1-2 tile minimum clearance so
    // multiple vendors in one shop don't crowd or overlap.
    private const int VendorSpacing = 2;

    // SP-028: scans the house's own floor plan (BaseHouse.Area) for up to
    // `count` ground-floor interior tiles, each also kept clear of every
    // OTHER spot already chosen this call (VendorSpacing) as well as
    // doors/sign/fixtures. Returns however many it actually found - which
    // can be fewer than `count` on a small floor plan - so callers should
    // treat the result list's
    // own length as the real vendor count for this house, not assume it
    // always equals what was asked for.
    public static List<(Point3D Loc, Direction Facing)> TryFindVendorSpots(BaseHouse house, int count)
    {
        var results = new List<(Point3D Loc, Direction Facing)>();

        var map = house?.Map;
        if (house == null || map == null || map == Map.Internal || count <= 0)
        {
            return results;
        }

        var faceTarget = FrontDoorLocation(house) ?? house.Sign?.Location ?? house.BanLocation;

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

                    if (!IsGroundFloorInterior(house, map, x, y, out var candidate))
                    {
                        continue;
                    }

                    if (IsNearDoor(house, x, y, DoorClearance) ||
                        IsNearSign(house, x, y, SignClearance) ||
                        IsNearFixture(house, x, y, candidate.Z, FixtureClearance) ||
                        TooCloseToChosen(results, x, y, VendorSpacing))
                    {
                        continue;
                    }

                    results.Add((candidate, DirectionTo(candidate, faceTarget)));
                    if (results.Count >= count)
                    {
                        return results;
                    }
                }
            }
        }

        return results;
    }

    private static bool TooCloseToChosen(List<(Point3D Loc, Direction Facing)> chosen, int x, int y, int clearance)
    {
        foreach (var (loc, _) in chosen)
        {
            if (Utility.InRange(loc.X, loc.Y, x, y, clearance))
            {
                return true;
            }
        }

        return false;
    }

    // The door nearest the sign (the sign always stands at the front of
    // the house, right by the entrance a real player would use) - falls
    // back to null if the house has no sign or no doors yet, in which
    // case callers fall back further to the sign itself or BanLocation.
    public static Point3D? FrontDoorLocation(BaseHouse house)
    {
        if (house?.Doors is not { Count: > 0 } doors)
        {
            return null;
        }

        var signLoc = house.Sign?.Location;

        BaseDoor best = null;
        var bestDistSq = long.MaxValue;

        foreach (var door in doors)
        {
            if (door?.Deleted != false)
            {
                continue;
            }

            if (signLoc is not { } sl)
            {
                best ??= door;
                continue;
            }

            var dx = door.X - sl.X;
            var dy = door.Y - sl.Y;
            var distSq = (long)dx * dx + (long)dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                best = door;
            }
        }

        return best?.Location;
    }

    // True if (x,y) is a genuinely walkable ground-floor interior tile -
    // inside the house's own floor plan, on real floorboards, and clear
    // enough for something to stand/sit on. Shared with
    // DynamicClutterGenerator so both modules agree on what "ground floor"
    // means.
    public static bool IsGroundFloorInterior(BaseHouse house, Map map, int x, int y, out Point3D candidate)
    {
        candidate = Point3D.Zero;

        // Map.GetAverageZ only ever looks at raw TERRAIN - it has no idea
        // a house's own floorboards are static MULTI tiles sitting on top
        // of it, offset from house.Z by whatever that house style's art
        // says. Testing terrain Z against CanSpawnMobile rejected every
        // interior tile outright (nothing walkable exists at bare ground
        // level under a house's floor), so this always came up empty and
        // fell all the way back to the sign - a vendor
        // spawning outside, under the sign, was that fallback firing.
        //
        // CanSpawnMobile's ranged overload is the engine's own answer to
        // "where can something actually stand here": it walks land,
        // static, AND MULTI tiles (Tiles.GetStaticAndMultiTiles) across a
        // bounded Z window and returns the real surface it finds -
        // correctly landing on the house's own floor. Bounding the window
        // to house.Z's ground-floor offsets keeps it from ever resolving
        // to an upper story.
        if (!map.CanSpawnMobile(
                x, y, house.Z + MinGroundFloorOffset, house.Z + MaxGroundFloorOffset,
                canSwim: false, cantWalk: false, out var z
            ))
        {
            return false;
        }

        var loc = new Point3D(x, y, z);

        // Genuinely inside the house's floor plan, not just inside its
        // bounding rectangle (Area can be an L-shape / have carve-outs).
        if (!house.IsInside(loc, 16))
        {
            return false;
        }

        candidate = loc;
        return true;
    }

    // A tile against a wall/exterior boundary or tucked into a corner -
    // one of its eight surrounding neighbors (cardinal AND diagonal) fails
    // the same ground-floor-interior test this tile just passed. Diagonals
    // are included deliberately: a strictly-cardinal check starves small
    // interiors (SmallShop's tiny footprint especially) of eligible slots,
    // rejecting corner tiles that are visibly "against the wall" the
    // moment the room isn't a perfect open rectangle.
    public static bool IsWallAdjacent(BaseHouse house, Map map, int x, int y)
    {
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                if (!IsGroundFloorInterior(house, map, x + dx, y + dy, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsNearDoor(BaseHouse house, int x, int y, int clearance)
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

            if (Utility.InRange(door.X, door.Y, x, y, clearance))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsNearSign(BaseHouse house, int x, int y, int clearance)
    {
        var sign = house.Sign;
        return sign?.Deleted == false && Utility.InRange(sign.X, sign.Y, x, y, clearance);
    }

    public static bool IsNearFixture(BaseHouse house, int x, int y, int z, int clearance)
    {
        foreach (var fixture in house.LockDowns)
        {
            if (fixture?.Deleted != false)
            {
                continue;
            }

            if (Utility.InRange(fixture.X, fixture.Y, x, y, clearance) &&
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
    public static Direction DirectionTo(Point3D from, Point3D to)
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
