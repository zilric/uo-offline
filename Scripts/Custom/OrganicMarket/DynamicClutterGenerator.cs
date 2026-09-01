// =========================================================================
// DynamicClutterGenerator.cs — SP-022's themed furnishing pass, upgraded by
// SP-028 from unanchored wall scatter into two structured layers:
//
//   1. A service counter - 2-3 contiguous tiles near the front door,
//      placed before anything else so the entrance always reads as "here's
//      where you're served" rather than a random pile of furniture.
//   2. Themed workstation/backroom fixtures - per-archetype, still placed
//      wall-hugging (IsWallAdjacent) exactly like before, just now a
//      richer, more thematically distinct set per archetype instead of
//      2-3 generic pieces shared across a smaller catalog.
//
// Runs after the house is placed but before the vendor spot is chosen
// (same ordering the old inline two-fixture BuildFixtures used): every
// piece here gets locked down to the house, so InteriorTileFinder already
// steers the vendor clear of it, and MerchantGuildAuthority.DeleteAt
// already sweeps every BaseHouse.LockDowns entry on teardown - no separate
// cleanup path needed for any of this.
//
// Item choice: real content classes (Anvil, Forge, MortarPestle,
// WritingTable, Candelabra, Dressform, LargeTable, resource stacks) where
// one exists and fits, raw tile-art IDs (new Item(id)) only for pieces
// that have no dedicated class - a display pedestal, a weapon rack - the
// same approach the original SP-022 pass used, just not the only tool in
// the box any more.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class DynamicClutterGenerator
{
    // How close two clutter pieces can land to each other - keeps a
    // furnished room from reading as a pile-up against one stretch of
    // wall when the floor plan offers plenty of other wall tiles.
    private const int ClutterSpacing = 3;

    // SP-028: one themed fixture set per archetype, matching this ticket's
    // own per-archetype furniture brief. Every entry here is either a real
    // ModernUO content class or a tile-art ID already proven safe by the
    // original SP-022 pass (0xFB1 Forge/0xFAF Anvil/0x1508 weapon rack/
    // 0xE76 potion keg/0xA97 bookshelf/0x185B alchemist table/0x2815
    // display case/0x9AA display pedestal/0x1015 spinning wheel/0x1062
    // loom) - no newly-invented hex IDs, since a wrong guess there renders
    // as unpredictable art with no way to visually verify it up front.
    private static readonly Dictionary<MarketArchetype, Func<Item>[]> ThemedFixtures = new()
    {
        // Anvil and Forge along a wall (both real classes - both also
        // construct Movable = false, which FurnishItems below now handles
        // as "place it, skip the lockdown attempt" rather than treating a
        // failed lockdown as reason to delete it), a standing weapon rack,
        // and a real (plain, non-exceptional) plate chest hung as a wall-
        // mounted armor display - reusing a class already on hand rather
        // than guessing at a dedicated "armor stand" tile ID.
        [MarketArchetype.BlacksmithArmory] = new Func<Item>[]
        {
            () => new Anvil(),
            () => new Forge(),
            () => new Item(0x1508),
            () => new PlateChest()
        },

        // Alchemist's mortar and pestle (real class), the alchemist table/
        // vials tile, a potion keg tile, and a real PotionKeg as a "keg
        // stack" piece.
        [MarketArchetype.MageApothecary] = new Func<Item>[]
        {
            () => new MortarPestle(),
            () => new Item(0x185B),
            () => new Item(0xE76),
            () => new PotionKeg()
        },

        // Study desk (real WritingTable class) with a candelabra (real
        // class) standing on the same wall run, and two tall bookshelf
        // tiles.
        [MarketArchetype.ScribeLibrary] = new Func<Item>[]
        {
            () => new WritingTable(),
            () => new Candelabra(),
            () => new Item(0xA97),
            () => new Item(0xA97)
        },

        // Real resource stacks, not decoration standing in for the real
        // thing - a genuine 20-log pile, 20-board pile, 20-leather roll,
        // 20-bolt cloth stack, and a 500-ingot pile. Every one of these is
        // a real content class already used for StockTemplateEngine's own
        // RawResources stock, so there's nothing here that needed a
        // guessed tile ID at all.
        [MarketArchetype.RawResources] = new Func<Item>[]
        {
            () => new Log(20),
            () => new Board(20),
            () => new Leather(20),
            () => new BoltOfCloth(20),
            () => new IronIngot(500)
        },

        // Dress form/mannequin (real Dressform class), spinning wheel and
        // loom tiles, and the weapon-rack tile doing double duty as a bow
        // display rack - the exact three tile IDs the original SP-022
        // TailorFletcher set already used, now with a real dress-form
        // piece added on top.
        [MarketArchetype.TailorFletcher] = new Func<Item>[]
        {
            () => new Dressform(),
            () => new Item(0x1015),
            () => new Item(0x1062),
            () => new Item(0x1508)
        },

        // SP-029: TinkerCurio -> TinkerCarpenter, remit narrowed to
        // hardware/carpentry now that the old curio/lockbox side moved to
        // FisherCurioBaker's own archetype. Tinker's toolkit (real
        // TinkerTools class) standing in for "a workbench with toolkits,"
        // a real closed barrel, and a real wooden storage box in place of
        // the old display-case/pedestal tiles, which read as curio-shop
        // fixtures rather than a carpenter's workshop.
        [MarketArchetype.TinkerCarpenter] = new Func<Item>[]
        {
            () => new TinkerTools(),
            () => new ClosedBarrel(),
            () => new WoodenBox(),
            () => new LargeTable()
        }
    };

    // SP-024: "someone actually lives here" clutter for the ~90% ambient
    // filler houses - ordinary movable furniture classes rather than raw
    // tile IDs, chosen specifically to sidestep two traps: bed-shaped
    // items in this engine are almost all multi-tile IAddon deeds (wrong
    // shape for a single MoveToWorld+LockDown drop), and WallTorch
    // hardcodes Movable=false in its own constructor (LockDown requires
    // Movable), so neither fits this system's placement model. Bedroll
    // reads as a sleeping spot without being an addon; the rest are
    // ordinary single-tile furniture/containers.
    private static readonly Func<Item>[] ResidentialFurniture =
    {
        () => new WoodenChair(),
        () => new PlainLowTable(),
        () => new Nightstand(),
        () => new ClosedBarrel(),
        () => new Bedroll()
    };

    // SP-028: how many tiles long the front service counter tries to run -
    // the ticket's own "2-3 tile" brief. TryPlaceCounter degrades to
    // shorter runs (down to 2) on its own if a full 3-tile run doesn't fit
    // anywhere, rather than skipping the counter outright.
    private const int CounterLength = 3;
    private const int CounterMinLength = 2;

    // Furnishes `house` with `archetype`'s service counter plus themed
    // fixture set, locking each piece down under `authority`. Safe to call
    // on any house/map state; skips silently (never throws, never blocks
    // the placement pipeline) if a piece can't find a slot or fails to
    // lock down.
    public static void Furnish(BaseHouse house, MarketArchetype archetype, Mobile authority)
    {
        if (house?.Map is not { } map || map == Map.Internal || authority == null)
        {
            return;
        }

        // SP-028: deliberately SEPARATE placed-tile lists for the counter
        // pass and the themed-fixture pass, not one shared list. Sharing
        // one meant the counter's own 2-3 tiles (each excluding a further
        // ClutterSpacing=3 radius around itself via TooCloseToPlaced, on
        // top of the counter's own row) could blanket the ENTIRE interior
        // of a small house style (SmallShop's own floor plan is only
        // roughly 7x7) before the wall-fixture pass got a single
        // candidate to try - confirmed empirically: a BlacksmithArmory/
        // TailorFletcher SmallShop house placed its 3-tile counter and
        // then zero of its four themed fixtures, every single one
        // rejected by proximity to the counter rather than to each other.
        // The two passes only need to avoid crowding WITHIN their own
        // category; IsNearFixture (clearance 1, checked against
        // house.LockDowns directly) already keeps a themed fixture off
        // the counter itself once the counter is actually locked down,
        // without swallowing tiles a full 3-radius away from it too.
        PlaceCounter(house, map, authority, new List<Point3D>());

        if (ThemedFixtures.TryGetValue(archetype, out var factories))
        {
            FurnishItems(house, map, authority, factories, new List<Point3D>());
        }
    }

    // SP-024: same placement/lockdown machinery as Furnish, just with
    // residential furniture instead of an archetype's merchant set - see
    // OrganicMarketSpawner.PlaceHouse's ambient-filler branch. No service
    // counter for a private residence - that's a shop fixture.
    public static void FurnishResidential(BaseHouse house, Mobile authority)
    {
        if (house?.Map is not { } map || map == Map.Internal || authority == null)
        {
            return;
        }

        FurnishItems(house, map, authority, ResidentialFurniture, new List<Point3D>());
    }

    // SP-028: a short, straight run of LargeTable pieces near the front
    // door - "in front of the vendor stations facing the entrance," per
    // the ticket. Tries both axes (a door can open onto either a
    // horizontal or vertical run of floor) and both directions along
    // whichever axis, 2-4 tiles in from the door itself so the counter
    // reads as "just inside the entrance" without blocking the doorway
    // tile. Falls back from a 3-tile run to a 2-tile run before giving up
    // - a shop too small for either just doesn't get a counter, the same
    // "skip rather than crowd" philosophy the wall-fixture pass already
    // uses.
    private static void PlaceCounter(BaseHouse house, Map map, Mobile authority, List<Point3D> placed)
    {
        if (InteriorTileFinder.FrontDoorLocation(house) is not { } door)
        {
            return;
        }

        for (var length = CounterLength; length >= CounterMinLength; length--)
        {
            if (!TryFindCounterRun(house, map, door, length, out var run))
            {
                continue;
            }

            foreach (var loc in run)
            {
                var counter = new LargeTable();
                counter.MoveToWorld(loc, map);

                if (house.LockDown(authority, counter, false))
                {
                    placed.Add(loc);
                }
                else
                {
                    counter.Delete();
                }
            }

            return;
        }
    }

    private static bool TryFindCounterRun(BaseHouse house, Map map, Point3D door, int length, out List<Point3D> run)
    {
        foreach (var horizontal in new[] { true, false })
        {
            foreach (var dir in new[] { -1, 1 })
            {
                for (var offset = 2; offset <= 4; offset++)
                {
                    if (TryBuildRun(house, map, door, horizontal, dir, offset, length, out run))
                    {
                        return true;
                    }
                }
            }
        }

        run = null;
        return false;
    }

    private static bool TryBuildRun(
        BaseHouse house, Map map, Point3D door, bool horizontal, int dir, int offset, int length, out List<Point3D> run
    )
    {
        run = new List<Point3D>(length);

        for (var i = 0; i < length; i++)
        {
            var x = horizontal ? door.X + dir * offset + i : door.X;
            var y = horizontal ? door.Y : door.Y + dir * offset + i;

            if (!InteriorTileFinder.IsGroundFloorInterior(house, map, x, y, out var candidate) ||
                InteriorTileFinder.IsNearDoor(house, x, y, 1) ||
                InteriorTileFinder.IsNearSign(house, x, y, 1))
            {
                run = null;
                return false;
            }

            run.Add(candidate);
        }

        return true;
    }

    private static void FurnishItems(BaseHouse house, Map map, Mobile authority, Func<Item>[] factories, List<Point3D> placed)
    {
        foreach (var factory in factories)
        {
            if (!TryFindWallSlot(house, map, placed, out var loc))
            {
                continue; // floor plan ran out of clear wall tiles - skip this piece rather than crowd another
            }

            var clutter = factory();
            clutter.MoveToWorld(loc, map);

            // SP-028: some real content classes (Anvil, Forge) construct
            // themselves Movable = false - a genuine, heavy, can't-be-
            // stolen fixture in real UO, and BaseHouse.LockDown only ever
            // applies to movable items. Attempting (and failing) to lock
            // one down used to mean this method deleted it right back out
            // again on the very next line. An immovable item needs no
            // lockdown to begin with - it's already permanently exactly
            // where it was dropped - so it just gets placed and counted.
            if (!clutter.Movable)
            {
                placed.Add(loc);
            }
            else if (house.LockDown(authority, clutter, false))
            {
                placed.Add(loc);
            }
            else
            {
                // Couldn't lock it down (capacity, imbued flag, etc.) -
                // don't leave an unlocked, untracked item sitting loose in
                // the house; MerchantGuildAuthority's footprint sweep would
                // still catch it on deletion, but there's no reason to
                // furnish with something teardown has to work harder for.
                clutter.Delete();
            }
        }
    }

    private static bool TryFindWallSlot(BaseHouse house, Map map, List<Point3D> alreadyPlaced, out Point3D loc)
    {
        loc = Point3D.Zero;

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

                    if (!InteriorTileFinder.IsGroundFloorInterior(house, map, x, y, out var candidate))
                    {
                        continue;
                    }

                    if (!InteriorTileFinder.IsWallAdjacent(house, map, x, y))
                    {
                        continue; // open floor / main walking lane - leave it clear
                    }

                    if (InteriorTileFinder.IsNearDoor(house, x, y, 2) ||
                        InteriorTileFinder.IsNearSign(house, x, y, 1) ||
                        InteriorTileFinder.IsNearFixture(house, x, y, candidate.Z, 1) ||
                        TooCloseToPlaced(alreadyPlaced, x, y))
                    {
                        continue;
                    }

                    loc = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TooCloseToPlaced(List<Point3D> alreadyPlaced, int x, int y)
    {
        foreach (var p in alreadyPlaced)
        {
            if (Utility.InRange(p.X, p.Y, x, y, ClutterSpacing))
            {
                return true;
            }
        }

        return false;
    }
}
