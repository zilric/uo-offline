// =========================================================================
// FerryPlacement.cs — SP-043: shared pet-handling helpers for the
// charter flow.
//
// Every water-detection/nudge-search helper that used to live here
// (IsWater, TryGetWaterZ, TryNudgeToWater) is gone — SP-043 retired
// procedural boat placement entirely in favor of the hardcoded
// BoatLocation/BoatFacing pairs in FerryRouteRegistry.cs, so nothing
// calls them anymore. What remains is the small, non-procedural pet
// bookkeeping FerryCharterGump's instant-charter flow needs.
// =========================================================================

using System.Collections.Generic;
using Server;
using Server.Mobiles;

namespace Server.Engines.FerrySystem;

public static class FerryPlacement
{
    // Fixed, non-procedural offsets used to spread pets around a landing
    // tile so they don't all stack on the exact same spot — not a search,
    // just a short deterministic ring.
    private static readonly Point3D[] SpreadOffsets =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0),
        new(1, 1, 0), new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0)
    };

    public static Point3D SpreadPoint(Point3D center, int index)
    {
        var o = SpreadOffsets[index % SpreadOffsets.Length];
        return new Point3D(center.X + o.X, center.Y + o.Y, center.Z);
    }

    // Bonded/following pets near `loc` belonging to `owner`.
    public static List<BaseCreature> CollectPets(Mobile owner, Map map, Point3D loc, int range)
    {
        var pets = new List<BaseCreature>();
        if (map == null || map == Map.Internal)
        {
            return pets;
        }

        foreach (var bc in map.GetMobilesInRange<BaseCreature>(loc, range))
        {
            if (bc.Controlled && bc.ControlMaster == owner)
            {
                pets.Add(bc);
            }
        }

        return pets;
    }
}
