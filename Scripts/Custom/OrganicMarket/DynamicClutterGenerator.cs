// =========================================================================
// DynamicClutterGenerator.cs — SP-022's themed furnishing pass. Runs after
// the house is placed but before the vendor spot is chosen (same ordering
// the old inline two-fixture BuildFixtures used): every piece here gets
// locked down to the house, so InteriorTileFinder already steers the
// vendor clear of it, and MerchantGuildAuthority.DeleteAt already sweeps
// every BaseHouse.LockDowns entry on teardown - no separate cleanup path
// needed for any of this.
//
// Placement deliberately hugs the walls (IsWallAdjacent) rather than
// scattering across the open floor, so the room's main walking lanes and
// the vendor's own spot stay clear. Item IDs are the exact tile-art
// references from the sprint ticket, built as plain Item(itemID) rather
// than named subclasses so the requested art lands even where no
// dedicated content class exists for it (a display pedestal, a bow rack).
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

    private static readonly Dictionary<MarketArchetype, int[]> ClutterItemIds = new()
    {
        // Small Forge, Anvil, Weapon/Armor Rack
        [MarketArchetype.Blacksmith] = new[] { 0xFB1, 0xFAF, 0x1508 },

        // Potion Keg, Full Bookshelf, Alchemist Table/Vials
        [MarketArchetype.MageAlchemist] = new[] { 0xE76, 0xA97, 0x185B },

        // Display Case, Display Pedestal/Chest
        [MarketArchetype.CurioRares] = new[] { 0x2815, 0x9AA },

        // Spinning Wheel, Loom, Bow Rack
        [MarketArchetype.TailorFletcher] = new[] { 0x1015, 0x1062, 0x1508 }
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

    // Furnishes `house` with `archetype`'s clutter set, locking each piece
    // down under `authority`. Safe to call on any house/map state; skips
    // silently (never throws, never blocks the placement pipeline) if a
    // piece can't find a wall slot or fails to lock down.
    public static void Furnish(BaseHouse house, MarketArchetype archetype, Mobile authority)
    {
        if (!ClutterItemIds.TryGetValue(archetype, out var itemIds) || itemIds.Length == 0)
        {
            return;
        }

        var factories = new Func<Item>[itemIds.Length];
        for (var i = 0; i < itemIds.Length; i++)
        {
            var id = itemIds[i]; // capture by value, not by the loop variable
            factories[i] = () => new Item(id);
        }

        FurnishItems(house, authority, factories);
    }

    // SP-024: same placement/lockdown machinery as Furnish, just with
    // residential furniture instead of an archetype's merchant set - see
    // OrganicMarketSpawner.PlaceHouse's ambient-filler branch.
    public static void FurnishResidential(BaseHouse house, Mobile authority) =>
        FurnishItems(house, authority, ResidentialFurniture);

    private static void FurnishItems(BaseHouse house, Mobile authority, Func<Item>[] factories)
    {
        if (house?.Map is not { } map || map == Map.Internal || authority == null)
        {
            return;
        }

        var placed = new List<Point3D>();

        foreach (var factory in factories)
        {
            if (!TryFindWallSlot(house, map, placed, out var loc))
            {
                continue; // floor plan ran out of clear wall tiles - skip this piece rather than crowd another
            }

            var clutter = factory();
            clutter.MoveToWorld(loc, map);

            if (house.LockDown(authority, clutter, false))
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
