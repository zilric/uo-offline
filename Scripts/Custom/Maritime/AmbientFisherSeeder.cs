// =========================================================================
// AmbientFisherSeeder.cs — SP-048/SP-049: seeds ~55-65 ambient fishing
// boats (each with a fisher on deck and a scatter of clutter) across
// Britannia's harbors, coastlines, and open shipping lanes, plus the
// [seedfishers / [wipefishers GM commands.
//
// SP-049 fixes two reported bugs:
//   - Deck stacking: the fisher, the barrel, and the boat's own mast tile
//     were all placed at the exact same (X, Y) — only Z differed. Every
//     deck spot below is now computed from the boat's ACTUAL facing
//     (returned by MaritimeWaterFinder.TryFindClearBoatSpot, chosen
//     during placement rather than rerolled afterward) via the same
//     local-offset rotation FerryRouteRegistry.FerryStop already uses for
//     its own deck-landing math: mast/pivot stays clear, the fisher
//     stands one tile aft (toward the stern/tiller), the barrel sits one
//     tile forward (the cargo hold spot), and clutter goes to starboard —
//     four distinct tiles, no stacking.
//   - Placement collisions: TryFindOpenWater's old 4-neighbor manual
//     water check never validated a real hull footprint or checked
//     distance from the 13 charter moorings. Replaced with
//     TryFindClearBoatSpot, which runs actual BaseBoat.CanFit validation
//     plus an explicit 20-tile standoff from every FerryRouteRegistry
//     stop (see MaritimeWaterFinder.cs).
//
// Region reuse and rationale otherwise unchanged from SP-048 — ten of
// sixteen regions are centered on already client-verified charter
// coordinates rather than fresh guesses.
// =========================================================================

using System;
using Server;
using Server.Commands;
using Server.Engines.FerrySystem;
using Server.Items;
using Server.Multis;

namespace Server.Engines.Maritime;

public static class AmbientFisherSeeder
{
    private const int PlacementAttemptsPerBoat = 8;

    private static readonly TimeSpan FreshenInterval = TimeSpan.FromHours(6);

    // Decorative-only static graphic IDs — cosmetic clutter, not backed
    // by dedicated item classes the way Barrel/Lantern are.
    private const int TackleBoxStaticId = 0x09A8;
    private const int CoiledLineStaticId = 0x14F8;

    private sealed record MaritimeRegion(string Name, Map Map, Point3D Center, int Radius, int BoatCount);

    private static Point3D StopAnchor(string ferryStopName, Point3D fallback) =>
        FerryRouteRegistry.GetStop(ferryStopName)?.BoatLocation ?? fallback;

    private static Point3D Midpoint(string stopA, string stopB, Point3D fallback)
    {
        var a = FerryRouteRegistry.GetStop(stopA)?.BoatLocation;
        var b = FerryRouteRegistry.GetStop(stopB)?.BoatLocation;
        if (a == null || b == null)
        {
            return fallback;
        }

        return new Point3D((a.Value.X + b.Value.X) / 2, (a.Value.Y + b.Value.Y) / 2, 0);
    }

    private static readonly MaritimeRegion[] Regions =
    {
        // ---- Harbors, Estuaries & Bays (6 x 3 = 18) ----
        new("Britain Bay", Map.Felucca, StopAnchor("Britain Harbor Docks", new Point3D(1490, 1766, -2)), 24, 3),
        new("Vesper Canals", Map.Felucca, StopAnchor("Vesper East Harbor", new Point3D(3050, 836, -4)), 24, 3),
        new("Trinsic Bay", Map.Felucca, StopAnchor("Trinsic East Docks", new Point3D(2091, 2856, -4)), 24, 3),
        new("Jhelom Channel", Map.Felucca, StopAnchor("Jhelom Main Island Docks", new Point3D(1380, 3902, -4)), 24, 3),
        new("Moonglow Coves", Map.Felucca, StopAnchor("Moonglow West Pier", new Point3D(4425, 1036, -4)), 24, 3),
        new("Ocllo Bay", Map.Felucca, StopAnchor("Ocllo Town Docks", new Point3D(3641, 2678, -4)), 24, 3),

        // ---- Coastlines, Outer Reefs & Coves (7 x 4 = 28) ----
        new("Dagger Isle Floes", Map.Felucca, StopAnchor("Dagger Isle", new Point3D(4269, 596, -4)), 28, 4),
        new("Skara Brae Channel", Map.Felucca, StopAnchor("Skara Brae Ferry Bank", new Point3D(667, 2225, -4)), 28, 4),
        new("Yew / Deep Forest Shores", Map.Felucca, StopAnchor("Yew North Coast Pier", new Point3D(511, 800, -4)), 28, 4),
        new("Fire Island Shores", Map.Felucca, StopAnchor("Isle of Fire", new Point3D(2775, 3446, -4)), 28, 4),
        // First-pass region estimates (no existing verified anchor):
        new("Cape Dun", Map.Felucca, new Point3D(2900, 3300, 0), 35, 4),
        new("Serpent's Hold Outer Ring", Map.Felucca, new Point3D(2920, 3644, 0), 30, 4),
        // Buccaneer's Den bounding box from CustomBots/RedTerritory.cs
        // (x in [2600,2800], y in [2060,2290]) — center of that box, not
        // a fresh guess.
        new("Buccaneer's Den Coves", Map.Felucca, new Point3D(2700, 2175, 0), 30, 4),

        // ---- Deep Water Shipping Lanes (3 x 4 = 12) ----
        new("Trinsic-Jhelom Lane", Map.Felucca, Midpoint("Trinsic East Docks", "Jhelom Main Island Docks", new Point3D(1700, 3400, 0)), 55, 4),
        new("Britain-Moonglow Lane", Map.Felucca, Midpoint("Britain Harbor Docks", "Moonglow West Pier", new Point3D(2900, 1400, 0)), 55, 4),
        new("Vesper-Skara Brae Lane", Map.Felucca, Midpoint("Vesper East Harbor", "Skara Brae Ferry Bank", new Point3D(1800, 1800, 0)), 55, 4)
    };

    public static void Configure()
    {
        CommandSystem.Register("seedfishers", AccessLevel.GameMaster, OnSeed);
        CommandSystem.Register("wipefishers", AccessLevel.GameMaster, OnWipe);
    }

    public static void Initialize()
    {
        Timer.DelayCall(FreshenInterval, FreshenInterval, KeepFleetFresh);
    }

    [Usage("seedfishers")]
    [Description("Cleans up any existing ambient fishing fleet, then scatters ~60 fishing boats with fishers across Britannia's harbors, coastlines, and shipping lanes.")]
    private static void OnSeed(CommandEventArgs e)
    {
        var from = e.Mobile;
        var authority = MaritimeAuthority.Instance;
        if (authority == null)
        {
            from?.SendMessage("MaritimeAuthority is not ready yet.");
            return;
        }

        if (authority.IsSeeded)
        {
            authority.WipeAll();
        }

        var (placed, target) = SeedAll(authority);
        authority.IsSeeded = true;

        from?.SendMessage($"Ambient fisher fleet seeded: {placed}/{target} boats placed across {Regions.Length} regions.");
    }

    [Usage("wipefishers")]
    [Description("Removes every ambient fishing boat, clutter prop, and fisher seeded by [seedfishers.")]
    private static void OnWipe(CommandEventArgs e)
    {
        var from = e.Mobile;
        var authority = MaritimeAuthority.Instance;
        if (authority == null)
        {
            from?.SendMessage("MaritimeAuthority is not ready yet.");
            return;
        }

        authority.WipeAll();
        from?.SendMessage("Ambient fisher fleet wiped.");
    }

    private static (int placed, int target) SeedAll(MaritimeAuthority authority)
    {
        var placed = 0;
        var target = 0;

        // One reusable probe validates every candidate for the whole
        // pass (see MaritimeWaterFinder's header comment for why this is
        // safe) — never MoveToWorld'd, so it must be deleted afterward or
        // it leaks an entry in BaseBoat's own static Boats list forever.
        var probe = new SmallBoat();
        try
        {
            foreach (var region in Regions)
            {
                target += region.BoatCount;
                var placedInRegion = 0;

                for (var i = 0; i < region.BoatCount; i++)
                {
                    if (TryPlaceBoat(authority, region, probe))
                    {
                        placed++;
                        placedInRegion++;
                    }
                }

                if (placedInRegion < region.BoatCount)
                {
                    Console.WriteLine($"[AmbientFisherSeeder] {region.Name}: placed {placedInRegion}/{region.BoatCount} boats (not enough clear water found in range).");
                }
            }
        }
        finally
        {
            probe.Delete();
        }

        return (placed, target);
    }

    private static bool TryPlaceBoat(MaritimeAuthority authority, MaritimeRegion region, SmallBoat probe)
    {
        for (var attempt = 0; attempt < PlacementAttemptsPerBoat; attempt++)
        {
            if (!MaritimeWaterFinder.TryFindClearBoatSpot(region.Map, region.Center, region.Radius, probe, out var anchor, out var facing))
            {
                return false;
            }

            SeedBoat(authority, region.Map, anchor, facing);
            return true;
        }

        return false;
    }

    // Rotates a LOCAL (North-facing-frame) offset by the given facing —
    // the same quarter-turn rotation BaseBoat.Rotate / FerryStop's own
    // MarkOffset math uses, reproduced here since it's a tiny amount of
    // logic not worth sharing across namespaces for.
    private static (int dx, int dy) RotateLocal(int lx, int ly, Direction facing) =>
        facing switch
        {
            Direction.North => (lx, ly),
            Direction.East  => (-ly, lx),
            Direction.South => (-lx, -ly),
            Direction.West  => (ly, -lx),
            _               => (lx, ly)
        };

    private static void SeedBoat(MaritimeAuthority authority, Map map, Point3D anchor, Direction facing)
    {
        BaseBoat boat = Utility.RandomBool() ? new AmbientFishingBoat() : new AmbientDragonFishingBoat();
        boat.MoveToWorld(anchor, map);

        if (boat.Deleted)
        {
            Console.WriteLine("[AmbientFisherSeeder] boat failed to spawn at an otherwise-CanFit-validated tile — skipping.");
            return;
        }

        boat.Facing = facing;
        authority.Track(boat);

        var deckZ = anchor.Z + 3;

        // Deck layout, all relative to the boat's actual facing: mast
        // stays exactly at the pivot (anchor) with nothing on it; fisher
        // aft, barrel forward (the cargo-hold spot), clutter to
        // starboard — four distinct tiles, never stacked.
        var (aftX, aftY) = RotateLocal(0, 1, facing);
        var (fwdX, fwdY) = RotateLocal(0, -1, facing);
        var (starX, starY) = RotateLocal(1, 0, facing);

        var fisherSpot = new Point3D(anchor.X + aftX, anchor.Y + aftY, deckZ);
        var barrelSpot = new Point3D(anchor.X + fwdX, anchor.Y + fwdY, deckZ);

        var barrel = new Barrel { Movable = false };
        barrel.MoveToWorld(barrelSpot, map);
        authority.Track(barrel);

        // 1-2 extra clutter props beyond the barrel (2-3 total per boat),
        // both to starboard so they never land on the mast, fisher, or
        // barrel tiles.
        var extraCount = Utility.RandomBool() ? 1 : 2;
        var clutterOffsets = new[]
        {
            (starX, starY),
            (starX + aftX, starY + aftY)
        };

        for (var i = 0; i < extraCount; i++)
        {
            var (cx, cy) = clutterOffsets[i % clutterOffsets.Length];
            var spot = new Point3D(anchor.X + cx, anchor.Y + cy, deckZ);

            Item clutter = Utility.Random(3) switch
            {
                0 => new Lantern(),
                1 => new Static(TackleBoxStaticId),
                _ => new Static(CoiledLineStaticId)
            };
            clutter.Movable = false;
            clutter.MoveToWorld(spot, map);
            authority.Track(clutter);
        }

        // Cast toward the open water past the starboard side (the side
        // with nothing on it), a few tiles out.
        var waterTile = new Point3D(anchor.X + starX * 3, anchor.Y + starY * 3, anchor.Z);

        var fisher = new AmbientFisher();
        fisher.MoveToWorld(fisherSpot, map);
        fisher.Setup(barrel, waterTile, fisher.GetDirectionTo(waterTile));
        authority.Track(fisher);
    }

    private static void KeepFleetFresh()
    {
        var authority = MaritimeAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        foreach (var item in authority.TrackedItems)
        {
            if (item is BaseBoat boat && !boat.Deleted)
            {
                boat.Refresh();
            }
        }
    }
}
