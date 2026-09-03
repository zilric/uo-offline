// =========================================================================
// DynamicClutterGenerator.cs — SP-022's themed furnishing pass, upgraded by
// SP-028 into a structured counter + wall-fixture layout, by SP-034 into a
// footprint-scaled decor matrix with a genuine vendor anchor, and by
// SP-035 into a genuinely multi-floor, multi-room furnishing pass:
//
//   1. A service counter - 2-3 contiguous tiles built INWARD from the
//      front door along whichever cardinal axis actually points at the
//      house's own interior (see ComputeInteriorCentroid/InwardAxis),
//      dressed with 1-2 small tabletop props. The primary vendor spot is
//      reserved one tile further inward than the counter's own center
//      tile, facing back out over the counter toward the door - see
//      Furnish's return value and OrganicMarketSpawner.SpawnVendors.
//      Ground floor, unchanged since SP-034 - untouched by SP-035.
//   2. Room- and floor-aware fixtures (FurnishFloors/FurnishFloor) - a
//      much larger budget (see DecorBandFor: 8-12 / 22-35 / 45-70+ by
//      footprint tier) split across every floor SP-035 can actually
//      detect (AnyFloorTileExists, walking InteriorTileFinder's new
//      per-floor IsFloorInterior/IsWallAdjacent overloads up in ~20-Z-
//      unit story increments), and within each floor further split by
//      distance-from-door into near/mid/far thirds so a room doesn't
//      exhaust the whole budget before a back room or upper floor gets
//      its share. Ground floor near/mid draws from the archetype's own
//      ThemedFixtures set, ground floor far draws from BackRoomProps,
//      every non-topmost upper floor draws from UpperQuartersProps, and
//      the single topmost floor of a 3+ story structure (Tower/Keep/
//      Castle - a genuine roof/balcony deck, not just another room)
//      draws from RoofPatioProps instead.
//
// Runs after the house is placed but before the vendor spots are chosen
// (same ordering the old inline two-fixture BuildFixtures used): every
// piece here gets locked down to the house, so InteriorTileFinder already
// steers additional vendors clear of it, and MerchantGuildAuthority.
// DeleteAt already sweeps every BaseHouse.LockDowns entry on teardown - no
// separate cleanup path needed for any of this (AmbientHousePurchaseGump.
// TryPurchase sweeps it too, on the residential-furniture side, for the
// one path where a player actually takes ownership of ambient decor).
//
// Item choice: real content classes (Anvil, Forge, MortarPestle,
// WritingTable, Candelabra, Dressform, LargeTable, resource stacks, real
// tool/container classes) where one exists and fits; raw tile-art IDs
// (new Item(id)) only for the handful of pieces that have no dedicated
// class (weapon rack, alchemist table, potion keg tile, bookshelf,
// spinning wheel, loom) - the same small set SP-022/SP-028 already proved
// out, not a newly-invented guess. SP-035's own new pools stick to the
// same rule - see BackRoomProps/UpperQuartersProps/RoofPatioProps for the
// real classes chosen, and their own comments for the couple of
// deliberately-skipped ticket suggestions (Bed, Telescope) that don't fit
// this single-tile placement model.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class DynamicClutterGenerator
{
    // How close two clutter pieces can land to each other - 0 means only
    // an EXACT duplicate tile is rejected (Utility.InRange's own range
    // parameter is inclusive, so 0 still guarantees two pieces never
    // literally occupy the same tile - it just no longer reserves any
    // buffer beyond that). SP-035: tightened from the original 3, through
    // intermediate 2 and 1 steps, down to 0 - live verification with the
    // new density bands (see DecorBandFor) found each step still capping
    // well short of the 45-70+ Tier 3 target purely on wall-tile
    // capacity (a real house's own wall run is only so long), never on
    // budget. 0 lets genuinely adjacent wall tiles both take a piece -
    // "wall-to-wall cozy," matching what the ticket's own numbers imply
    // a heavily-decorated real UO house actually looks like.
    private const int ClutterSpacing = 0;

    // SP-034: per-archetype themed prop palette, expanded well past the
    // old flat 4-piece set so DecorCountFor's Large tier (16-22 pieces)
    // has real variety to draw from instead of the same handful of items
    // repeated a dozen times. FurnishItems still cycles the pool (modulo)
    // once decorCount exceeds the palette's own length - repetition past
    // that point is expected and fine; TryFindWallSlot's own spacing/wall-
    // adjacency checks are what actually bounds how much a given floor
    // plan can hold, not the palette size.
    private static readonly Dictionary<MarketArchetype, Func<Item>[]> ThemedFixtures = new()
    {
        // Anvil and Forge (both real classes, both construct themselves
        // Movable = false - FurnishItems below handles that as "place it,
        // skip the lockdown attempt" rather than treating a failed
        // lockdown as reason to delete it), the smith's own hammer and
        // tongs, a genuine ingot stack, a closed barrel standing in for
        // "scrap," a standing weapon-rack tile, a plain plate chest as a
        // wall-mounted armor display, and a heater shield as a second,
        // distinct display piece.
        [MarketArchetype.BlacksmithArmory] = new Func<Item>[]
        {
            () => new Anvil(),
            () => new Forge(),
            () => new SmithHammer(),
            () => new Tongs(),
            () => new IronIngot(500),
            () => new ClosedBarrel(),
            () => new Item(0x1508), // weapon rack
            () => new PlateChest(),
            () => new HeaterShield()
        },

        // Alchemist's mortar and pestle (real class), the alchemist
        // table/vials tile, a potion keg tile plus a real PotionKeg as a
        // "keg stack" piece, an empty reagent jar, and a basket standing
        // in for loose dried herbs.
        [MarketArchetype.MageApothecary] = new Func<Item>[]
        {
            () => new MortarPestle(),
            () => new Item(0x185B), // alchemist table/vials
            () => new Item(0xE76),  // potion keg tile
            () => new PotionKeg(),
            () => new EmptyJar(),
            () => new Basket()
        },

        // Study desk (real WritingTable class) with a candelabra (real
        // class), two tall bookshelf tiles, a navigator's globe standing
        // in for the wall maps, and three real, distinct book classes
        // scattered as "open book stacks."
        [MarketArchetype.ScribeLibrary] = new Func<Item>[]
        {
            () => new WritingTable(),
            () => new Candelabra(),
            () => new Item(0xA97), // bookshelf
            () => new Item(0xA97),
            () => new Globe(),
            () => new BrownBook(),
            () => new BlueBook(),
            () => new RedBook()
        },

        // Real resource stacks, not decoration standing in for the real
        // thing - a genuine 20-log pile, 20-board pile, 20-leather roll,
        // 20-bolt cloth stack, a 500-ingot pile, and a granite chunk for
        // the "stone" side of the brief (Granite has no stackable-amount
        // constructor pre-ML - a single piece, not a pile). Every one of
        // these is a real content class already used for
        // StockTemplateEngine's own RawResources stock.
        [MarketArchetype.RawResources] = new Func<Item>[]
        {
            () => new Log(20),
            () => new Board(20),
            () => new Leather(20),
            () => new BoltOfCloth(20),
            () => new IronIngot(500),
            () => new Granite()
        },

        // Dress form/mannequin (real Dressform class), spinning wheel and
        // loom tiles, the weapon-rack tile doing double duty as a bow
        // display rack, real feather/shaft/arrow bundles for the fletcher
        // side of the brief.
        [MarketArchetype.TailorFletcher] = new Func<Item>[]
        {
            () => new Dressform(),
            () => new Item(0x1015), // spinning wheel
            () => new Item(0x1062), // loom
            () => new Item(0x1508), // bow/weapon rack
            () => new Feather(20),
            () => new Shaft(20),
            () => new Arrow(20)
        },

        // Tinker's toolkit (real TinkerTools class) standing in for "a
        // workbench with toolkits," a closed barrel as an empty keg, a
        // wooden storage box, a real LargeTable as a second workbench, a
        // real clock and loose clockwork parts, a metal lockbox, and a
        // large wood-supply crate.
        [MarketArchetype.TinkerCarpenter] = new Func<Item>[]
        {
            () => new TinkerTools(),
            () => new ClosedBarrel(),
            () => new WoodenBox(),
            () => new LargeTable(),
            () => new Clock(),
            () => new ClockParts(),
            () => new MetalBox(),
            () => new LargeCrate()
        },

        // SP-034: this archetype had NO themed fixture set at all before -
        // FisherCurioBaker shops got a counter and nothing else, silently
        // skipped by ThemedFixtures.TryGetValue. Real fishing pole, a
        // message in a bottle, a flour sack, a basket standing in for a
        // bread basket, a barrel and a beverage bottle for the wine/ale
        // cask brief, an empty jar as a curio piece, and a real fresh
        // catch.
        [MarketArchetype.FisherCurioBaker] = new Func<Item>[]
        {
            () => new FishingPole(),
            () => new MessageInABottle(),
            () => new SackFlour(),
            () => new Basket(),
            () => new Barrel(),
            () => new BeverageBottle(BeverageType.Wine),
            () => new EmptyJar(),
            () => new Fish()
        }
    };

    // SP-034: small, deliberately generic pool for the 1-2 pieces that
    // land directly on the counter's own surface (GetWorldTop) rather
    // than against a wall - "open ledger/book, scale, candelabra, mortar
    // & pestle, or artisan tool" per the ticket, kept archetype-neutral so
    // a RawResources shop's counter doesn't end up dressed with a 500-
    // ingot pile that dwarfs the table it's sitting on.
    private static readonly Func<Item>[] CounterDressing =
    {
        () => new BrownBook(),   // open ledger
        () => new Candelabra(),
        () => new MortarPestle()
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

    // SP-035: "Back Rooms & Workrooms" per the ticket - shelving/storage
    // flavor for the farther-from-the-door share of the ground floor
    // (see FurnishFloor's bucket split). Archetype-neutral, same
    // reasoning as CounterDressing above - a shop's back room reads as
    // storage regardless of what's sold up front.
    private static readonly Func<Item>[] BackRoomProps =
    {
        () => new WoodenBox(),
        () => new MetalBox(),
        () => new LargeCrate(),
        () => new SmallCrate(),
        () => new PlainWoodenChest(),
        () => new Item(0xA97), // shelving/bookshelf tile
        () => new HeatingStand(),
        () => new Pitcher()
    };

    // SP-035: "Upper Living Quarters / Bedrooms" per the ticket - used
    // for every detected floor above the ground floor that ISN'T the
    // topmost floor of a 3+ story structure (see FurnishFloors). No real
    // Bed class fits this placement model (multi-tile IAddon deed, same
    // trap ResidentialFurniture's own header comment already documents),
    // so a nightstand/dresser/seating/washstand set carries the "someone
    // sleeps up here" read instead.
    private static readonly Func<Item>[] UpperQuartersProps =
    {
        () => new Nightstand(),
        () => new Armoire(),
        () => new FancyArmoire(),
        () => new PlainLowTable(),
        () => new WoodenChair(),
        () => new BambooChair(),
        () => new Vase(),
        () => new Pitcher()
    };

    // SP-035: "Roof Patios & Balconies" per the ticket - only ever
    // handed to the SINGLE topmost floor of a 3+ story structure (Tower/
    // Keep/Castle) - see FurnishFloors. Potted plants, an outdoor
    // brazier (constructs itself Movable = false, same "place it, skip
    // the lockdown attempt" handling every other immovable fixture in
    // this file already gets), a lantern, and a throne standing in for
    // outdoor bench seating. RoofPatioProps(archetype) below appends one
    // archetype-flavored bonus piece on top of this shared set.
    private static readonly Func<Item>[] RoofPatioPropsGeneric =
    {
        () => new PottedPlant(),
        () => new PottedPlant1(),
        () => new PottedPlant2(),
        () => new Brazier(),
        () => new Lantern(),
        () => new WoodenThrone()
    };

    // "training dummies, archery targets (for fletcher/blacksmith),
    // telescopes/star charts (for mage/scribe)" per the ticket - a real
    // Telescope class exists but is a multi-component BaseAddon (the
    // same multi-tile trap this file avoids everywhere else), so Globe
    // (already proven safe - see ThemedFixtures' ScribeLibrary set)
    // stands in for "star chart" instead. TrainingDummy/ArcheryButte are
    // plain AddonComponent-derived single-tile items - safe to place and
    // lock down standalone, no parent BaseAddon required.
    private static Func<Item>[] RoofPatioProps(MarketArchetype archetype)
    {
        Func<Item> bonus = archetype switch
        {
            MarketArchetype.BlacksmithArmory => () => new TrainingDummy(),
            MarketArchetype.TailorFletcher   => () => new ArcheryButte(),
            MarketArchetype.MageApothecary   => () => new Globe(),
            MarketArchetype.ScribeLibrary    => () => new Globe(),
            _                                 => null
        };

        if (bonus == null)
        {
            return RoofPatioPropsGeneric;
        }

        var combined = new Func<Item>[RoofPatioPropsGeneric.Length + 1];
        Array.Copy(RoofPatioPropsGeneric, combined, RoofPatioPropsGeneric.Length);
        combined[^1] = bonus;
        return combined;
    }

    // SP-028: how many tiles long the front service counter tries to run -
    // the ticket's own "2-3 tile" brief. PlaceCounter degrades to shorter
    // runs (down to 2) on its own if a full 3-tile run doesn't fit
    // anywhere, rather than skipping the counter outright.
    private const int CounterLength = 3;
    private const int CounterMinLength = 2;

    // SP-034: how far inward from the door the counter's own row sits,
    // and how much further past that the vendor's own anchor tile is -
    // exactly 1 tile behind the counter's center, per the ticket.
    private const int CounterMinDistance = 2;
    private const int CounterMaxDistance = 4;
    private const int VendorBehindCounter = 1;

    // SP-035: how many stories to probe for eligible decor tiles -
    // covers every real style in the catalog (a single-story shop
    // through Castle's several stacked levels) without wasting time
    // probing floors nothing in the catalog will ever have. FloorFor's
    // own detection (AnyFloorTileExists) stops early once a floor comes
    // up completely empty, so this is a ceiling, not a guess at what
    // every house actually has.
    private const int MaxFloorsProbed = 4;

    // SP-035: how far (in tiles) an upper-floor candidate can sit from
    // that floor's own "access point" (the door's X/Y carried straight
    // up - this codebase has no direct staircase-footing query, so this
    // is a coarse stand-in for "roughly where the stairs land up here")
    // before it's still eligible. Deliberately more generous than the
    // ground floor's own DoorClearance=1 - the real door is an exact
    // point, this is only ever an approximation of one.
    private const int FloorAccessClearance = 2;

    // SP-034/SP-035: total decor item counts per footprint tier. SP-035
    // raised these substantially (4-6/8-14/16-22 -> 8-12/22-35/45-70)
    // after in-game testing found Tier 2/3 houses reading sparse - back
    // rooms with a single item, upper floors and roof decks completely
    // bare. This is the WALL-FIXTURE budget only (on top of the
    // counter's own 2-3 tiles + 1-2 countertop props, which are placed
    // separately and unaffected by this number) - see FurnishFloors for
    // how it's now split across every detected floor rather than handed
    // entirely to the ground floor. A random point within each band, not
    // a flat number, so two houses of the same style don't always read
    // identically furnished. Real floor-plan capacity (TooCloseToPlaced/
    // wall-adjacency) still bounds how much of this a given house can
    // actually fit - these are targets, not guarantees, same "skip
    // rather than crowd" philosophy as everywhere else in this file.
    private static (int Min, int Max) DecorBandFor(MarketHouseStyle style) => TierFor(style) switch
    {
        HouseDecorTier.Large  => (45, 70),
        HouseDecorTier.Medium => (22, 35),
        _                     => (8, 12)
    };

    private enum HouseDecorTier
    {
        Small,
        Medium,
        Large
    }

    // Mirrors OrganicMarketSpawner.VendorCountFor's own footprint
    // grouping (2/3/4 vendors already tracks small/medium/large floor
    // plans) rather than inventing a second, possibly-inconsistent
    // classification of the same style list.
    private static HouseDecorTier TierFor(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.TwoStoryWoodPlaster     => HouseDecorTier.Large,
        MarketHouseStyle.TwoStoryStoneAndPlaster => HouseDecorTier.Large,
        MarketHouseStyle.MarbleHouseWithPatio    => HouseDecorTier.Large,
        MarketHouseStyle.LargeTower              => HouseDecorTier.Large,
        MarketHouseStyle.Keep                    => HouseDecorTier.Large,
        MarketHouseStyle.Castle                  => HouseDecorTier.Large,

        MarketHouseStyle.LargePatio              => HouseDecorTier.Medium,
        MarketHouseStyle.SandStonePatio          => HouseDecorTier.Medium,
        MarketHouseStyle.SandstoneHouseWithPatio => HouseDecorTier.Medium,
        MarketHouseStyle.LogCabin                => HouseDecorTier.Medium,
        MarketHouseStyle.TwoStoryLogCabin        => HouseDecorTier.Medium,
        MarketHouseStyle.TwoStoryVilla           => HouseDecorTier.Medium,
        MarketHouseStyle.ThreeRoomBrickHouse     => HouseDecorTier.Medium,

        _ => HouseDecorTier.Small
    };

    // Furnishes `house` with `archetype`'s service counter (plus 1-2
    // countertop props) and its footprint-scaled themed fixture set,
    // locking each piece down under `authority`. Returns the primary
    // vendor's anchor spot/facing if a counter was actually built - null
    // if the house had no door, or no floor plan the counter run could
    // fit, in which case OrganicMarketSpawner falls back to
    // InteriorTileFinder.TryFindVendorSpots for every vendor exactly like
    // before SP-034. Safe to call on any house/map state; skips silently
    // (never throws, never blocks the placement pipeline) if a piece
    // can't find a slot or fails to lock down.
    public static (Point3D Spot, Direction Facing)? Furnish(
        BaseHouse house, MarketHouseStyle style, MarketArchetype archetype, Mobile authority
    )
    {
        if (house?.Map is not { } map || map == Map.Internal || authority == null)
        {
            return null;
        }

        // SP-036: computed once per house and threaded through every
        // placement decision below (counter, vendor anchor, every
        // floor's wall fixtures) - see InteriorTileFinder.
        // ComputeStairExclusionZones for why this replaced the old
        // ground-floor-door-only proxy (FloorAccessClearance still
        // exists as a belt-and-suspenders check below, but this is the
        // real fix).
        var stairZones = InteriorTileFinder.ComputeStairExclusionZones(house);

        // SP-034: deliberately SEPARATE placed-tile lists for the counter
        // pass and the themed-fixture pass, not one shared list - see the
        // original SP-028 note this replaces: the counter's own tiles
        // (each excluding a further ClutterSpacing=3 radius around
        // itself) could blanket a small floor plan before the wall-
        // fixture pass got a single candidate to try. The two passes only
        // need to avoid crowding WITHIN their own category; IsNearFixture
        // (checked against house.LockDowns directly) already keeps a
        // themed fixture off the counter itself once it's locked down.
        var anchor = PlaceCounter(house, map, authority, new List<Point3D>(), stairZones);

        if (ThemedFixtures.TryGetValue(archetype, out var factories))
        {
            var (min, max) = DecorBandFor(style);
            var decorCount = Utility.RandomMinMax(min, max);

            FurnishFloors(house, map, authority, archetype, factories, anchor?.Spot, decorCount, stairZones);
        }

        return anchor;
    }

    // SP-035: distributes `totalBudget` wall-fixture items across every
    // detected floor of the house (see AnyFloorTileExists) instead of
    // handing it all to the ground floor. The ground floor gets its own
    // even share (it's also the only floor with a counter, so it reads
    // "full" at a lower prop count than an empty upper room would); any
    // floors above it split the remainder evenly. A single-story house
    // (the overwhelming majority of the catalog) is unaffected in shape
    // - floors.Count == 1 just means the whole budget goes to floor 0,
    // same as before this ticket, just now ALSO split within that floor
    // between the archetype's own front-of-shop set and BackRoomProps
    // (see FurnishFloor) instead of only ever drawing from one pool.
    private static void FurnishFloors(
        BaseHouse house, Map map, Mobile authority, MarketArchetype archetype,
        Func<Item>[] frontFactories, Point3D? vendorAnchor, int totalBudget, HashSet<Point2D> stairZones
    )
    {
        var door = InteriorTileFinder.FrontDoorLocation(house);

        var floors = new List<int>();
        for (var f = 0; f < MaxFloorsProbed; f++)
        {
            if (AnyFloorTileExists(house, map, f))
            {
                floors.Add(f);
            }
            else if (f > 0)
            {
                // Real house art is always contiguous from the ground up
                // - once a floor comes up completely empty, nothing
                // above it will have a floor plan either.
                break;
            }
        }

        if (floors.Count == 0)
        {
            return;
        }

        var groundShare = floors.Count == 1 ? totalBudget : Math.Max(totalBudget / floors.Count, 1);
        var upperFloorCount = floors.Count - 1;
        var upperShare = upperFloorCount > 0 ? Math.Max((totalBudget - groundShare) / upperFloorCount, 1) : 0;

        for (var idx = 0; idx < floors.Count; idx++)
        {
            var floor = floors[idx];
            var budget = floor == 0 ? groundShare : upperShare;

            if (budget <= 0)
            {
                continue;
            }

            // SP-035: one placed-tile list PER FLOOR, not shared across
            // floors. TooCloseToPlaced only ever compares X/Y (it has no
            // reason to know about Z on the ground-floor-only counter/
            // wall-fixture pass it was written for), so a single shared
            // list here made an item placed on the ground floor
            // incorrectly exclude the SAME X/Y one or two stories up -
            // multi-story house art is almost always the same footprint
            // repeated per floor, so this starved every upper floor down
            // to almost nothing the first time this ran live (floor 1
            // landing 1-4 items against a 20+ item budget). The vendor's
            // own anchor tile only ever needs reserving on the floor it
            // actually stands on (0).
            var placed = new List<Point3D>();
            if (floor == 0 && vendorAnchor is { } anchor)
            {
                placed.Add(anchor);
            }

            Func<Item>[] roomPool;
            Func<Item>[] backPool = null;

            if (floor == 0)
            {
                roomPool = frontFactories;
                backPool = BackRoomProps;
            }
            else if (idx == floors.Count - 1 && floors.Count >= 3)
            {
                // Only the topmost floor of a 3+ story structure reads
                // as a genuine roof/balcony deck - a 2-story house's
                // floor 1 is still an ordinary upper room, not a
                // rooftop.
                roomPool = RoofPatioProps(archetype);
            }
            else
            {
                roomPool = UpperQuartersProps;
            }

            FurnishFloor(house, map, authority, floor, door, roomPool, backPool, placed, budget, stairZones);
        }
    }

    // Furnishes one floor: collects every eligible wall-adjacent tile on
    // that floor, buckets them by distance from the floor's own
    // reference point (near/mid/far - see CollectFloorCandidates),
    // places pieces in that interleaved near/mid/far order so budget
    // never exhausts on the near bucket alone, and stops once `budget`
    // pieces have actually landed or the candidate list runs out.
    private static void FurnishFloor(
        BaseHouse house, Map map, Mobile authority, int floor, Point3D? door,
        Func<Item>[] primaryPool, Func<Item>[] backPool, List<Point3D> placed, int budget, HashSet<Point2D> stairZones
    )
    {
        var candidates = CollectFloorCandidates(house, map, floor, door, stairZones);
        if (candidates.Count == 0)
        {
            return;
        }

        var placedOnFloor = 0;
        var frontIndex = 0;
        var backIndex = 0;

        foreach (var (loc, bucket) in candidates)
        {
            if (placedOnFloor >= budget)
            {
                break;
            }

            if (TooCloseToPlaced(placed, loc.X, loc.Y))
            {
                continue;
            }

            // bucket 0/1 (near/mid the door) stay with the floor's own
            // "front" pool - the archetype's shop set on the ground
            // floor, or the whole upper-floor pool everywhere else since
            // backPool is only ever set for floor 0. Only the farthest
            // third of the ground floor reads as the "back room."
            Func<Item> factory;
            if (bucket <= 1 || backPool == null)
            {
                factory = primaryPool[frontIndex % primaryPool.Length];
                frontIndex++;
            }
            else
            {
                factory = backPool[backIndex % backPool.Length];
                backIndex++;
            }

            var clutter = factory();
            clutter.MoveToWorld(loc, map);

            // SP-028: some real content classes (Anvil, Forge, Brazier)
            // construct themselves Movable = false - a genuine, heavy,
            // can't-be-stolen fixture in real UO, and BaseHouse.LockDown
            // only ever applies to movable items. An immovable item
            // needs no lockdown to begin with - it's already permanently
            // exactly where it was dropped - so it just gets placed and
            // counted.
            if (!clutter.Movable)
            {
                placed.Add(loc);
                placedOnFloor++;
            }
            else if (house.LockDown(authority, clutter, false))
            {
                placed.Add(loc);
                placedOnFloor++;
            }
            else
            {
                clutter.Delete();
            }
        }
    }

    // Every wall-adjacent, door/sign-clear tile on `floor`, sorted by
    // distance from the door's own X/Y (Z is irrelevant to this ranking
    // - a floor's own reference point stands in for "near the entrance/
    // stairwell" regardless of which story it's actually on) and
    // interleaved near/mid/far so FurnishFloor's placement order visits
    // every third of the room instead of exhausting budget on the
    // closest tiles alone. The tagged int is that tile's own bucket (0 =
    // near, 1 = mid, 2 = far), which FurnishFloor also uses to pick
    // which prop pool a given placement draws from.
    private static List<(Point3D Loc, int Bucket)> CollectFloorCandidates(
        BaseHouse house, Map map, int floor, Point3D? door, HashSet<Point2D> stairZones
    )
    {
        var raw = new List<Point3D>();

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

                    if (!InteriorTileFinder.IsFloorInterior(house, map, x, y, floor, out var candidate))
                    {
                        continue;
                    }

                    if (!InteriorTileFinder.IsWallAdjacent(house, map, x, y, floor))
                    {
                        continue; // open floor / main walking lane - leave it clear
                    }

                    // SP-036: the real fix for stair/ladder obstruction -
                    // stairZones already carries the full 1-tile buffer
                    // around every step, footing, and landing across
                    // every floor (see ComputeStairExclusionZones), so a
                    // single Z-agnostic lookup here catches it regardless
                    // of which floor this candidate is actually on.
                    if (stairZones.Contains(new Point2D(x, y)))
                    {
                        continue;
                    }

                    // SP-035: IsNearDoor checks every BaseDoor on the
                    // house regardless of floor (it has no Z filter of
                    // its own), which is exactly what's wanted here - an
                    // upper floor can have its own real interior doors
                    // (bedroom doors, etc.) that this needs to clear too,
                    // not just the ground floor's front entrance.
                    if (InteriorTileFinder.IsNearDoor(house, x, y, 2))
                    {
                        continue;
                    }

                    // SP-035: floor 0's own counter (and its 1-2
                    // countertop props) is already locked down by the
                    // time this runs - without this check, a themed
                    // fixture could land directly on/beside it, since
                    // TooCloseToPlaced only ever tracks THIS floor pass's
                    // own placements, not the counter's separate list.
                    if (InteriorTileFinder.IsNearFixture(house, x, y, candidate.Z, 1))
                    {
                        continue;
                    }

                    if (floor == 0)
                    {
                        if (InteriorTileFinder.IsNearSign(house, x, y, 1))
                        {
                            continue;
                        }
                    }
                    else if (door is { } d && Utility.InRange(d.X, d.Y, x, y, FloorAccessClearance))
                    {
                        // No direct staircase-footing query in this
                        // codebase - treating the ground floor door's
                        // own X/Y as a stand-in "stairs land roughly
                        // here" exclusion zone on every floor above it,
                        // so decor never crowds a floor transition.
                        continue;
                    }

                    raw.Add(candidate);
                }
            }
        }

        if (raw.Count == 0)
        {
            return new List<(Point3D, int)>();
        }

        var reference = door ?? raw[0];
        raw.Sort((a, b) => DistSq(a, reference).CompareTo(DistSq(b, reference)));

        var near = new List<Point3D>();
        var mid = new List<Point3D>();
        var far = new List<Point3D>();
        var third = Math.Max(raw.Count / 3, 1);

        for (var i = 0; i < raw.Count; i++)
        {
            if (i < third)
            {
                near.Add(raw[i]);
            }
            else if (i < third * 2)
            {
                mid.Add(raw[i]);
            }
            else
            {
                far.Add(raw[i]);
            }
        }

        var result = new List<(Point3D, int)>(raw.Count);
        var maxLen = Math.Max(near.Count, Math.Max(mid.Count, far.Count));

        for (var i = 0; i < maxLen; i++)
        {
            if (i < near.Count)
            {
                result.Add((near[i], 0));
            }

            if (i < mid.Count)
            {
                result.Add((mid[i], 1));
            }

            if (i < far.Count)
            {
                result.Add((far[i], 2));
            }
        }

        return result;
    }

    private static long DistSq(Point3D a, Point3D b)
    {
        var dx = (long)(a.X - b.X);
        var dy = (long)(a.Y - b.Y);
        return dx * dx + dy * dy;
    }

    // True the moment a single eligible tile is found on `floor` -
    // FurnishFloors uses this purely to detect whether a given floor
    // exists at all, not to collect placements (CollectFloorCandidates
    // does that, separately, only for floors this already confirmed are
    // real).
    private static bool AnyFloorTileExists(BaseHouse house, Map map, int floor)
    {
        foreach (var rect in house.Area)
        {
            var x0 = house.X + rect.X;
            var y0 = house.Y + rect.Y;

            for (var dx = 0; dx < rect.Width; dx++)
            {
                for (var dy = 0; dy < rect.Height; dy++)
                {
                    if (InteriorTileFinder.IsFloorInterior(house, map, x0 + dx, y0 + dy, floor, out _))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
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

        var stairZones = InteriorTileFinder.ComputeStairExclusionZones(house);
        FurnishItems(house, map, authority, ResidentialFurniture, new List<Point3D>(), ResidentialFurniture.Length, stairZones);
    }

    // SP-034: a short, straight run of LargeTable pieces built INWARD
    // from the front door along whichever cardinal axis actually points
    // at the house's own interior (see InwardAxis) - "in front of the
    // vendor stations facing the entrance," per the ticket. The vendor's
    // own anchor tile sits exactly one step further inward than the
    // run's center tile, facing back out toward the door across the
    // counter. Falls back from a 3-tile run to a 2-tile run, and tries a
    // small spread of inward distances and perpendicular centering
    // offsets, before giving up - a shop too small for any of that just
    // doesn't get a counter (or a vendor anchor), the same "skip rather
    // than crowd" philosophy the wall-fixture pass already uses.
    private static (Point3D Spot, Direction Facing)? PlaceCounter(
        BaseHouse house, Map map, Mobile authority, List<Point3D> placed, HashSet<Point2D> stairZones
    )
    {
        if (InteriorTileFinder.FrontDoorLocation(house) is not { } door)
        {
            return null;
        }

        if (ComputeInteriorCentroid(house, map) is not { } centroid)
        {
            return null;
        }

        var (primaryDx, primaryDy) = InwardAxis(door, centroid);

        // SP-034: the centroid-inferred direction is a good first guess,
        // not a guarantee - an L-shaped or patio floor plan can easily
        // have its actual open run of floor along the OTHER axis, or even
        // pointing the opposite way the centroid leans. Trying all 4
        // cardinal directions (centroid's own guess first, then its
        // opposite, then the two perpendiculars) matches the search
        // breadth the original SP-028 "try both axes, both directions"
        // counter search had, just still preferring the direction that's
        // actually most likely to be inward.
        var axes = new[]
        {
            (primaryDx, primaryDy),
            (-primaryDx, -primaryDy),
            (primaryDy, primaryDx),
            (-primaryDy, -primaryDx)
        };

        foreach (var (dx, dy) in axes)
        {
            for (var length = CounterLength; length >= CounterMinLength; length--)
            {
                for (var distance = CounterMinDistance; distance <= CounterMaxDistance; distance++)
                {
                    if (!TryBuildRun(house, map, door, dx, dy, distance, length, stairZones, out var run, out var vendorSpot))
                    {
                        continue;
                    }

                    var vendorFacing = InteriorTileFinder.DirectionTo(vendorSpot, door);
                    var lockedDown = 0;

                    foreach (var loc in run)
                    {
                        // Deliberately NOT Movable = false here before
                        // locking down - BaseHouse.LockDown's own success
                        // gate requires item.Movable == true going IN
                        // (that's how it tells a fresh, placeable item
                        // apart from an addon/secure item); it sets
                        // Movable = false itself, as part of SetLockdown,
                        // once the lockdown actually succeeds. A pre-set
                        // Movable = false item fails LockDown's gate
                        // unconditionally and falls straight to "You
                        // cannot lock that down," which silently deleted
                        // every counter tile this method ever built until
                        // this was caught in SP-034's own live
                        // verification pass.
                        var counter = new LargeTable();
                        counter.MoveToWorld(loc, map);

                        if (house.LockDown(authority, counter, false))
                        {
                            placed.Add(loc);
                            lockedDown++;
                        }
                        else
                        {
                            counter.Delete();
                        }
                    }

                    // The vendor anchor is only meaningful if the counter
                    // it's supposed to stand behind actually exists - a
                    // run where every tile failed to lock down (capacity,
                    // etc.) shouldn't still hand back an anchor for an
                    // empty room. Try the next distance/length/axis
                    // instead of returning an orphaned vendor spot.
                    if (lockedDown == 0)
                    {
                        continue;
                    }

                    DressCounter(house, map, authority, run);

                    return (vendorSpot, vendorFacing);
                }
            }
        }

        return null;
    }

    // Places 1-2 small props directly on the counter's own surface
    // (GetWorldTop, not the floor tile underneath it) - "non-blocking
    // tabletop props," per the ticket. Picks from the counter's own
    // already-placed run so it never lands on a tile the counter run
    // itself failed to claim.
    private static void DressCounter(BaseHouse house, Map map, Mobile authority, List<Point3D> counterRun)
    {
        if (counterRun.Count == 0)
        {
            return;
        }

        var propCount = Math.Min(Utility.RandomMinMax(1, 2), counterRun.Count);
        var usedSlots = new HashSet<int>();
        var usedProps = new HashSet<int>();

        for (var i = 0; i < propCount; i++)
        {
            int slot;
            do
            {
                slot = Utility.Random(counterRun.Count);
            } while (!usedSlots.Add(slot));

            int propIndex;
            do
            {
                propIndex = Utility.Random(CounterDressing.Length);
            } while (CounterDressing.Length > 1 && !usedProps.Add(propIndex));

            var prop = CounterDressing[propIndex]();
            var top = new Point3D(counterRun[slot].X, counterRun[slot].Y, counterRun[slot].Z + 5);
            prop.MoveToWorld(top, map);

            if (!prop.Movable)
            {
                continue;
            }

            if (!house.LockDown(authority, prop, false))
            {
                prop.Delete();
            }
        }
    }

    // The cardinal (dx, dy) unit step - exactly one of the four
    // compass directions in UO's X-east/Y-south axes - that most closely
    // points from the door toward `centroid` (the house's own interior
    // middle). Whichever axis has the larger absolute delta wins; ties
    // fall back to the X axis.
    private static (int Dx, int Dy) InwardAxis(Point3D door, Point3D centroid)
    {
        var dx = centroid.X - door.X;
        var dy = centroid.Y - door.Y;

        return Math.Abs(dx) >= Math.Abs(dy)
            ? (Math.Sign(dx) == 0 ? 1 : Math.Sign(dx), 0)
            : (0, Math.Sign(dy) == 0 ? 1 : Math.Sign(dy));
    }

    // The average (x, y) of every ground-floor interior tile in the
    // house's own floor plan - a stand-in for "the middle of the house"
    // that InwardAxis uses to figure out which way is actually inward
    // from the door, without needing BaseDoor to expose its own facing.
    private static Point3D? ComputeInteriorCentroid(BaseHouse house, Map map)
    {
        long sumX = 0;
        long sumY = 0;
        var count = 0;

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

                    if (!InteriorTileFinder.IsGroundFloorInterior(house, map, x, y, out _))
                    {
                        continue;
                    }

                    sumX += x;
                    sumY += y;
                    count++;
                }
            }
        }

        return count == 0 ? null : new Point3D((int)(sumX / count), (int)(sumY / count), house.Z);
    }

    // Builds one candidate counter run: `length` tiles perpendicular to
    // (dx, dy), centered on the door's own cross-axis coordinate,
    // starting `distance` tiles inward from the door along (dx, dy). Also
    // computes and validates the vendor's own anchor tile - exactly
    // VendorBehindCounter further inward than the run's center - as part
    // of the same candidate, so a run only succeeds if BOTH the counter
    // AND a legal vendor spot behind it actually exist.
    private static bool TryBuildRun(
        BaseHouse house, Map map, Point3D door, int dx, int dy, int distance, int length,
        HashSet<Point2D> stairZones, out List<Point3D> run, out Point3D vendorSpot
    )
    {
        run = null;
        vendorSpot = Point3D.Zero;

        // The perpendicular axis the run's own tiles spread across - the
        // one InwardAxis did NOT pick.
        var perpX = dx == 0 ? 1 : 0;
        var perpY = dy == 0 ? 1 : 0;

        // Integer centering, deliberately not (length - 1) / 2.0 with
        // Math.Round - a fractional center on an even length (2) rounds
        // both tiles' spread to the same integer under round-half-to-even,
        // colliding them onto one tile. Plain integer division instead:
        // asymmetric by half a tile on an even-length run, but every
        // offset it produces is always distinct.
        var centerOffset = (length - 1) / 2;
        var candidateRun = new List<Point3D>(length);

        for (var i = 0; i < length; i++)
        {
            var spread = i - centerOffset;
            var x = door.X + dx * distance + perpX * spread;
            var y = door.Y + dy * distance + perpY * spread;

            if (!InteriorTileFinder.IsGroundFloorInterior(house, map, x, y, out var candidate) ||
                InteriorTileFinder.IsNearDoor(house, x, y, 1) ||
                InteriorTileFinder.IsNearSign(house, x, y, 1) ||
                stairZones.Contains(new Point2D(x, y)))
            {
                return false;
            }

            candidateRun.Add(candidate);
        }

        var centerX = door.X + dx * distance;
        var centerY = door.Y + dy * distance;
        var behindX = centerX + dx * VendorBehindCounter;
        var behindY = centerY + dy * VendorBehindCounter;

        if (!InteriorTileFinder.IsGroundFloorInterior(house, map, behindX, behindY, out var behindCandidate) ||
            InteriorTileFinder.IsNearDoor(house, behindX, behindY, 1) ||
            InteriorTileFinder.IsNearSign(house, behindX, behindY, 1) ||
            stairZones.Contains(new Point2D(behindX, behindY)))
        {
            return false;
        }

        run = candidateRun;
        vendorSpot = behindCandidate;
        return true;
    }

    private static void FurnishItems(
        BaseHouse house, Map map, Mobile authority, Func<Item>[] factories, List<Point3D> placed, int count,
        HashSet<Point2D> stairZones
    )
    {
        for (var i = 0; i < count; i++)
        {
            if (!TryFindWallSlot(house, map, placed, stairZones, out var loc))
            {
                continue; // floor plan ran out of clear wall tiles - skip this piece rather than crowd another
            }

            var factory = factories[i % factories.Length];
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

    private static bool TryFindWallSlot(
        BaseHouse house, Map map, List<Point3D> alreadyPlaced, HashSet<Point2D> stairZones, out Point3D loc
    )
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
                        stairZones.Contains(new Point2D(x, y)) ||
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
