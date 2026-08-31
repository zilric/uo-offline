// =========================================================================
// OrganicMarketSpawner.cs — the actual "place a themed test house" pipeline.
//
// Everything MarketHousePlacementTarget needs after a validated ground
// click: build the chosen house style, park it, drop and lock down the
// archetype's fixtures, commission a PlayerVendor for the house, and
// register the whole set with MerchantGuildAuthority. One place to
// change if a new style or archetype gets added later.
// =========================================================================

using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class OrganicMarketSpawner
{
    // A PlayerVendor's daily upkeep (ChargePerRealWorldDay) is at most a
    // few hundred gold even fully stocked; this ceiling is high enough
    // that the PayTimer's "pay > totalGold" dismissal check
    // (Mobiles/Vendors/PlayerVendor.cs) never trips, so a market vendor
    // never decays or dismisses itself for lack of funds.
    public const int VendorCommissionCeiling = 1_000_000_000;

    public static string StyleName(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.SmallShop               => "Small Shop",
        MarketHouseStyle.TwoStoryWoodPlaster     => "Two-Story Wood & Plaster",
        MarketHouseStyle.LargePatio              => "Large Patio House",
        MarketHouseStyle.SmallPlasterHouse       => "Small Plaster House",
        MarketHouseStyle.SmallStoneHouse         => "Small Stone House",
        MarketHouseStyle.SmallWoodHouse          => "Small Wood House",
        MarketHouseStyle.WoodAndPlasterHouse     => "Wood & Plaster House",
        MarketHouseStyle.StoneAndPlasterHouse    => "Stone & Plaster House",
        MarketHouseStyle.SandStonePatio          => "Sandstone Patio House",
        MarketHouseStyle.LogCabin                => "Log Cabin",
        MarketHouseStyle.SmallTower              => "Small Tower",
        MarketHouseStyle.SmallBrickHouse         => "Small Brick House",
        MarketHouseStyle.FieldStoneHouse         => "Field Stone House",
        MarketHouseStyle.TwoStoryStoneAndPlaster => "Two-Story Stone & Plaster",
        MarketHouseStyle.TwoStoryVilla           => "Two-Story Villa",
        MarketHouseStyle.TwoStoryLogCabin        => "Two-Story Log Cabin",
        MarketHouseStyle.SandstoneHouseWithPatio => "Sandstone House with Patio",
        MarketHouseStyle.MarbleHouseWithPatio    => "Marble House with Patio",
        MarketHouseStyle.LargeTower              => "Tower",
        MarketHouseStyle.Keep                    => "Keep",
        MarketHouseStyle.Castle                  => "Castle",
        _                                         => style.ToString()
    };

    public static string ArchetypeName(MarketArchetype archetype) => archetype switch
    {
        MarketArchetype.Blacksmith    => "Blacksmith",
        MarketArchetype.MageAlchemist => "Mage/Alchemist",
        MarketArchetype.CurioRares    => "Curio/Rares",
        MarketArchetype.TailorFletcher => "Tailor/Fletcher",
        _                              => archetype.ToString()
    };

    // The real house multi ID and deed placement offset for each style —
    // same values the stock house deeds use (Multis/Deeds.cs), so the
    // MultiTarget ghost outline and HousePlacement.Check both line up
    // with where the house actually lands.
    public static int MultiId(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.SmallShop               => 0xA0,
        MarketHouseStyle.TwoStoryWoodPlaster     => 0x76,
        MarketHouseStyle.LargePatio              => 0x8C,
        // SmallOldHouse variants (Multis/Houses/Houses.cs) - one class,
        // multi ID picks the visual (see Multis/Deeds.cs for the same
        // ID->name mapping stock deeds use). Only 6 real variants exist
        // for 7 requested small-house names, so SmallPlasterHouse takes
        // the one remaining unused variant (ThatchedRoofCottage) rather
        // than a name that actually matches its own art - none of the
        // other 6 fits "plaster" any better once Small/FieldStone,
        // SmallBrick, Wood, WoodPlaster, and StonePlaster are all spoken
        // for below.
        MarketHouseStyle.SmallPlasterHouse       => 0x6E, // ThatchedRoofCottageDeed
        MarketHouseStyle.SmallStoneHouse         => 0x66, // FieldStoneHouseDeed
        MarketHouseStyle.SmallWoodHouse          => 0x6A, // WoodHouseDeed
        MarketHouseStyle.WoodAndPlasterHouse     => 0x6C, // WoodPlasterHouseDeed
        MarketHouseStyle.StoneAndPlasterHouse    => 0x64, // StonePlasterHouseDeed
        MarketHouseStyle.SandStonePatio          => 0x9C,
        MarketHouseStyle.LogCabin                => 0x9A,
        MarketHouseStyle.SmallTower              => 0x98,
        MarketHouseStyle.SmallBrickHouse         => 0x68, // SmallBrickHouseDeed
        // "Field Stone House" and "Small Stone House" are the same OSI
        // concept under two names real players use interchangeably -
        // ModernUO has one art variant (0x66) for it, not two.
        MarketHouseStyle.FieldStoneHouse         => 0x66,
        MarketHouseStyle.TwoStoryStoneAndPlaster => 0x78, // TwoStoryStonePlasterHouseDeed
        MarketHouseStyle.TwoStoryVilla            => 0x9E,
        // No distinct "two-story" log cabin art exists; LogCabin's own
        // footprint (Area is a single 8x13 rect, taller than it is wide)
        // already reads as a tall, multi-level cabin, so this is a naming
        // alias onto the same multi ID rather than a second real variant.
        MarketHouseStyle.TwoStoryLogCabin        => 0x9A,
        // "Sandstone House with Patio" IS SandStonePatio's own class name
        // spelled out - same alias situation, not a second variant.
        MarketHouseStyle.SandstoneHouseWithPatio => 0x9C,
        MarketHouseStyle.MarbleHouseWithPatio    => 0x96, // LargeMarbleDeed
        // SP-029: the three grand structures, rare filler-only rolls (see
        // WorldHouseSeeder.SeedInhabitation) - values confirmed against
        // Multis/Houses/Houses.cs (Tower/Keep/Castle classes) and
        // Multis/Deeds.cs (TowerDeed/KeepDeed/CastleDeed).
        MarketHouseStyle.LargeTower              => 0x7A, // TowerDeed
        MarketHouseStyle.Keep                    => 0x7C, // KeepDeed
        MarketHouseStyle.Castle                  => 0x7E, // CastleDeed
        _                                          => 0xA0
    };

    public static Point3D PlacementOffset(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.SmallShop               => new Point3D(-1, 4, 0),
        MarketHouseStyle.TwoStoryWoodPlaster     => new Point3D(-3, 7, 0),
        MarketHouseStyle.LargePatio              => new Point3D(-4, 7, 0),
        // Every SmallOldHouse variant shares the same deed offset
        // (Multis/Deeds.cs) regardless of which multi ID above.
        MarketHouseStyle.SmallPlasterHouse       => new Point3D(0, 4, 0),
        MarketHouseStyle.SmallStoneHouse         => new Point3D(0, 4, 0),
        MarketHouseStyle.SmallWoodHouse          => new Point3D(0, 4, 0),
        MarketHouseStyle.WoodAndPlasterHouse     => new Point3D(0, 4, 0),
        MarketHouseStyle.StoneAndPlasterHouse    => new Point3D(0, 4, 0),
        MarketHouseStyle.SmallBrickHouse         => new Point3D(0, 4, 0),
        MarketHouseStyle.FieldStoneHouse         => new Point3D(0, 4, 0),
        MarketHouseStyle.SandStonePatio          => new Point3D(-1, 4, 0),
        MarketHouseStyle.LogCabin                => new Point3D(1, 6, 0),
        MarketHouseStyle.SmallTower              => new Point3D(3, 4, 0),
        MarketHouseStyle.TwoStoryStoneAndPlaster => new Point3D(-3, 7, 0),
        MarketHouseStyle.TwoStoryVilla            => new Point3D(3, 6, 0),
        MarketHouseStyle.TwoStoryLogCabin        => new Point3D(1, 6, 0),
        MarketHouseStyle.SandstoneHouseWithPatio => new Point3D(-1, 4, 0),
        MarketHouseStyle.MarbleHouseWithPatio    => new Point3D(-4, 7, 0),
        MarketHouseStyle.LargeTower              => new Point3D(0, 7, 0),
        MarketHouseStyle.Keep                    => new Point3D(0, 11, 0),
        MarketHouseStyle.Castle                  => new Point3D(0, 16, 0),
        _                                          => Point3D.Zero
    };

    // Builds and places everything at `center` (already validated by
    // MarketHousePlacementTarget via HousePlacement.Check), returns the
    // new registry slot index, or -1 on failure. `toMove` is whatever
    // HousePlacement.Check found standing where the house is going —
    // shifted to the house's ban location, same as a real deed placement.
    public static int PlaceTestHouse(
        Map map, Point3D center, MarketHouseStyle style, MarketArchetype archetype, List<IEntity> toMove
    ) => PlaceHouse(map, center, style, archetype, toMove);

    // SP-024: the ~90% "ambient filler" side of world inhabitation - same
    // pipeline, same registry, same teardown, just archetype: null steers
    // PlaceHouse to the residential branch instead of the vendor one below.
    public static int PlaceFillerHouse(Map map, Point3D center, MarketHouseStyle style, List<IEntity> toMove)
        => PlaceHouse(map, center, style, null, toMove);

    private static int PlaceHouse(
        Map map, Point3D center, MarketHouseStyle style, MarketArchetype? archetype, List<IEntity> toMove
    )
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null || map == null || map == Map.Internal)
        {
            return -1;
        }

        var house = BuildHouse(style, authority);
        if (house == null)
        {
            return -1;
        }

        house.RestrictDecay = true;
        house.MoveToWorld(center, map);

        if (toMove != null)
        {
            foreach (var o in toMove)
            {
                switch (o)
                {
                    case Mobile mobile:
                        mobile.Location = house.BanLocation;
                        break;
                    case Item item:
                        item.Location = house.BanLocation;
                        break;
                }
            }
        }

        if (archetype is { } a)
        {
            // house.Public only governs who may walk the interior region -
            // the doors themselves are built already locked, keyed to
            // `authority` (BaseHouse's AddEastDoor/AddSouthDoor helpers set
            // Locked=true + KeyValue at construction, same as a real
            // player's house), and that key never leaves authority's own
            // bank box. Left alone, a real player can never open a single
            // one of these doors. Public storefronts don't lock their own
            // front door.
            house.Public = true;
            UnlockDoors(house);

            // Clutter goes down BEFORE the vendor spot is chosen:
            // InteriorTileFinder reads house.LockDowns to steer clear of
            // it, which only works if it's already locked down by the
            // time it scans.
            DynamicClutterGenerator.Furnish(house, a, authority);

            // SP-028: 2-4 vendors, all under the same archetype, per shop
            // - see VendorCountFor. MerchantGuildAuthority.Register still
            // only tracks one "primary" vendor per house slot (its own
            // parallel-list schema predates this and a bigger migration
            // isn't warranted just to track a photo op); every vendor this
            // spawns is still fully live and tracked through
            // house.PlayerVendors (BaseHouse's own list, populated
            // automatically by PlayerVendor.House's fieldChanged hook),
            // which is what MerchantGuildAuthority.DeleteAt now iterates
            // for teardown instead of just the tracked primary.
            var vendors = SpawnVendors(authority, house, style, a);
            authority.Register(house, ArchetypeName(a), vendors[0]);
        }
        else
        {
            // Ambient filler: private residence, doors stay locked exactly
            // as BaseHouse built them (that's the classic UO "someone
            // lives here" read), residential clutter instead of a
            // merchant's craft station, and no PlayerVendor at all.
            house.Public = false;
            DynamicClutterGenerator.FurnishResidential(house, authority);
            authority.Register(house, "Ambient Residence", null);
        }

        return authority.Count - 1;
    }

    private static void UnlockDoors(BaseHouse house)
    {
        if (house.Doors == null)
        {
            return;
        }

        foreach (var door in house.Doors)
        {
            if (door == null)
            {
                continue;
            }

            door.Locked = false;
            door.KeyValue = 0;
        }
    }

    // Runs HousePlacement.Check the same way a real house deed would -
    // against a non-staff Mobile so an AccessLevel.GameMaster+ actor (or,
    // for WorldHouseSeeder, no actor at all) doesn't silently bypass every
    // rule the check exists to enforce (overlapping structures, bad
    // terrain, blocking statics). MerchantGuildAuthority is temporarily
    // relocated to `center` for exactly the duration of the check, then
    // restored - it never actually needs to be there, HousePlacement.Check
    // just needs a Mobile that isn't staff-privileged to test against.
    //
    // Callers still own their own Region.AllowHousing() pre-check (guard
    // zones, roads, "no housing" regions) - that one needs the caller's
    // own point (a clicked reticle position for the interactive target,
    // a search candidate for the seeder) and, for the interactive path,
    // its own region-specific failure messaging, so it isn't folded in
    // here.
    public static HousePlacementResult CheckPlacement(
        Map map, Point3D center, MarketHouseStyle style, out List<IEntity> toMove
    )
    {
        toMove = null;

        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return HousePlacementResult.BadRegion;
        }

        var multiId = MultiId(style);
        var savedMap = authority.Map;
        var savedLoc = authority.Location;
        HousePlacementResult result;
        try
        {
            authority.Map = map;
            authority.Location = center;
            result = HousePlacement.Check(authority, multiId, center, out toMove);
        }
        finally
        {
            authority.Location = savedLoc;
            authority.Map = savedMap;
        }

        if (result == HousePlacementResult.Valid && HasFootprintConflict(map, center, multiId))
        {
            toMove = null;
            return HousePlacementResult.BadStatic;
        }

        return result;
    }

    // SP-030: a single footprint-area static scan covering two distinct
    // bugs found via in-game visual testing, run together since both are
    // "scan the candidate's own multi footprint plus a margin for specific
    // static IDs/flags" and only need to walk the tile data once. Only
    // runs when result is already Valid - i.e. once per search, on the one
    // candidate that's actually about to get placed - never per ring
    // candidate. HousePlacement.Check itself already evaluates every
    // candidate's full footprint at real (non-trivial) cost; this adds one
    // more bounded scan on top of that same rare success case, not a
    // second per-tick cost source the way scanning every candidate would
    // be (see IsCemeteryRegion in WorldHouseSeeder for why that check stays
    // cheap and per-candidate instead, while this one doesn't).
    //
    // Bug 1 - building overlap: HousePlacement.Check's own static-collision
    // rule (rule #2, Multis/Houses/HousePlacement.cs) only rejects a static
    // that's Impassable, or Surface-but-not-Background - a walkable,
    // decorative floor overlay (a wooden dock deck, a paved courtyard) is
    // deliberately Background so players can walk across it, which means
    // it sails straight through that rule exactly like bare ground would.
    // Confirmed the real cause of a house landing on top of an existing
    // world building's deck (directory entry near 773,2144, Skara Brae
    // mainland) - the deck read as "flat ground," not "occupied." Walls,
    // roofs, and doors don't have that ambiguity - TileData.ItemTable flags
    // a building's structural pieces with Wall/Roof/Door regardless of
    // whether they're also Impassable, and every building with a walkable
    // floor also has walls/roof somewhere in its own footprint (a floor
    // with no walls anywhere nearby isn't "inside a building" in any sense
    // this check needs to care about). So instead of trying to fingerprint
    // "is this specific static a man-made floor" - fragile against
    // legitimate decorative ground overlays a house should still be
    // allowed to sit near - this looks for a Wall, Roof, or Door static
    // anywhere in the footprint. Finding one there is unambiguous.
    //
    // Bug 2 - cemetery grounds: an ambient house spawned among Moonglow's
    // gravestones (directory entry near 4552,1314) even though nothing
    // there is a NoHousingRegion or named "Cemetery"/"Graveyard" - on this
    // server's region data the grounds are dressed with gravestone statics
    // directly, no bounding Region at all (WorldHouseSeeder.IsCemeteryRegion
    // handles the case where one DOES exist; this handles the case where it
    // doesn't). Gravestone/tombstone static art IDs 0x1165-0x1184 and
    // 0x124B-0x1252 mark consecrated burial ground regardless.
    private const int FootprintConflictMargin = 10;

    private static bool HasFootprintConflict(Map map, Point3D center, int multiId)
    {
        var mcl = MultiData.GetComponents(multiId);
        var startX = center.X + mcl.Min.X - FootprintConflictMargin;
        var startY = center.Y + mcl.Min.Y - FootprintConflictMargin;
        var endX = center.X + mcl.Min.X + mcl.Width + FootprintConflictMargin;
        var endY = center.Y + mcl.Min.Y + mcl.Height + FootprintConflictMargin;

        for (var x = startX; x < endX; x++)
        {
            for (var y = startY; y < endY; y++)
            {
                foreach (var tile in map.Tiles.GetStaticAndMultiTiles(x, y))
                {
                    var id = tile.ID & TileData.MaxItemValue;

                    if ((TileData.ItemTable[id].Flags & (TileFlag.Wall | TileFlag.Roof | TileFlag.Door)) != 0)
                    {
                        return true;
                    }

                    if (id is >= 0x1165 and <= 0x1184 or >= 0x124B and <= 0x1252)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static BaseHouse BuildHouse(MarketHouseStyle style, Mobile owner) => style switch
    {
        MarketHouseStyle.SmallShop               => new SmallShop(owner, 0xA0),
        MarketHouseStyle.TwoStoryWoodPlaster     => new TwoStoryHouse(owner, 0x76),
        MarketHouseStyle.LargePatio              => new LargePatioHouse(owner),
        MarketHouseStyle.SmallPlasterHouse       => new SmallOldHouse(owner, 0x6E),
        MarketHouseStyle.SmallStoneHouse         => new SmallOldHouse(owner, 0x66),
        MarketHouseStyle.SmallWoodHouse          => new SmallOldHouse(owner, 0x6A),
        MarketHouseStyle.WoodAndPlasterHouse     => new SmallOldHouse(owner, 0x6C),
        MarketHouseStyle.StoneAndPlasterHouse    => new SmallOldHouse(owner, 0x64),
        MarketHouseStyle.SandStonePatio          => new SandStonePatio(owner),
        MarketHouseStyle.LogCabin                => new LogCabin(owner),
        MarketHouseStyle.SmallTower              => new SmallTower(owner),
        MarketHouseStyle.SmallBrickHouse         => new SmallOldHouse(owner, 0x68),
        MarketHouseStyle.FieldStoneHouse         => new SmallOldHouse(owner, 0x66),
        MarketHouseStyle.TwoStoryStoneAndPlaster => new TwoStoryHouse(owner, 0x78),
        MarketHouseStyle.TwoStoryVilla            => new TwoStoryVilla(owner),
        MarketHouseStyle.TwoStoryLogCabin        => new LogCabin(owner),
        MarketHouseStyle.SandstoneHouseWithPatio => new SandStonePatio(owner),
        MarketHouseStyle.MarbleHouseWithPatio    => new LargeMarbleHouse(owner),
        MarketHouseStyle.LargeTower              => new Tower(owner),
        MarketHouseStyle.Keep                    => new Keep(owner),
        MarketHouseStyle.Castle                  => new Castle(owner),
        _                                          => null
    };

    // SP-028: how many vendors a house of this style gets - small
    // footprints get the ticket's floor (2), the larger patio/two-story/
    // marble-tier styles get room for 3-4. InteriorTileFinder.
    // TryFindVendorSpots may still return fewer than this if the actual
    // floor plan doesn't have that many clear, spaced-out spots -
    // SpawnVendors below always spawns exactly what the finder returns
    // (at least 1, via its own fallback), never pads or errors on a
    // shortfall.
    private static int VendorCountFor(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.TwoStoryWoodPlaster     => 4,
        MarketHouseStyle.TwoStoryStoneAndPlaster => 4,
        MarketHouseStyle.TwoStoryVilla            => 4,
        MarketHouseStyle.MarbleHouseWithPatio    => 4,
        MarketHouseStyle.LargePatio              => 3,
        MarketHouseStyle.SandStonePatio          => 3,
        MarketHouseStyle.SandstoneHouseWithPatio => 3,
        MarketHouseStyle.LogCabin                => 3,
        MarketHouseStyle.TwoStoryLogCabin        => 3,
        // Filler-only styles (see MarketHouseStyle) never reach this path in
        // practice - PlaceFillerHouse always calls PlaceHouse with
        // archetype: null, which short-circuits before VendorCountFor is
        // ever consulted. Entries kept here only so the switch stays
        // exhaustive and correct if that ever changes.
        MarketHouseStyle.LargeTower              => 4,
        MarketHouseStyle.Keep                    => 4,
        MarketHouseStyle.Castle                  => 4,
        _                                          => 2
    };

    // Spawns 2-4 real commissioned PlayerVendors (never a stock town NPC),
    // all under the same archetype so a shop reads as one themed
    // storefront rather than a random assortment. Each links to the house
    // via its own constructor (BaseHouse.PlayerVendors picks it up
    // through the House property's fieldChanged hook - no extra
    // registration needed for that list), and each vendor's gold reserves
    // are set to VendorCommissionCeiling so the daily upkeep charge
    // (PayTimer.OnTick) never exceeds what it's holding.
    private static List<PlayerVendor> SpawnVendors(Mobile authority, BaseHouse house, MarketHouseStyle style, MarketArchetype archetype)
    {
        var vendors = new List<PlayerVendor>();
        var spots = InteriorTileFinder.TryFindVendorSpots(house, VendorCountFor(style));

        if (spots.Count == 0)
        {
            // Nothing on the floor plan qualified (a very small or oddly
            // shaped interior) - fall back to just inside the sign, same
            // as the original single-vendor spawner did, so there's still
            // always at least one vendor and it's still always somewhere
            // inside the house.
            var loc = house.Sign?.Location ?? new Point3D(house.X, house.Y - 1, house.Z);
            var faceTarget = InteriorTileFinder.FrontDoorLocation(house) ?? house.BanLocation;
            var facing = InteriorTileFinder.DirectionTo(loc, faceTarget);
            vendors.Add(SpawnOneVendor(authority, house, archetype, loc, facing));
            return vendors;
        }

        foreach (var (loc, facing) in spots)
        {
            vendors.Add(SpawnOneVendor(authority, house, archetype, loc, facing));
        }

        return vendors;
    }

    private static PlayerVendor SpawnOneVendor(
        Mobile authority, BaseHouse house, MarketArchetype archetype, Point3D loc, Direction facing
    )
    {
        var vendor = new PlayerVendor(authority, house)
        {
            ShopName = $"{ArchetypeName(archetype)} Test Shop",
            HoldGold = VendorCommissionCeiling,
            BankAccount = VendorCommissionCeiling
        };

        vendor.MoveToWorld(loc, house.Map);
        vendor.Direction = facing;

        // PlayerVendor's own constructor (Mobiles/Vendors/PlayerVendor.cs,
        // InitOutfit) equips a FancyShirt/LongPants/BodySash/Boots/Cloak
        // outfit without ever setting Name on any of it. This server runs
        // pre-UOTD, so single-clicking any of those pieces routes through
        // BaseClothing.OnSingleClickPreUOTD, which - like the weapons and
        // armor StockTemplateEngine sells - falls back to
        // Localization.GetText(LabelNumber) whenever Name is null, and
        // that always returns null here (cliloc loading is off by default
        // in Localization.Configure()). Left alone, every vendor this
        // spawner creates would crash a client the moment someone clicked
        // its shirt or boots.
        NameWornApparel(vendor);

        StockTemplateEngine.StockVendor(vendor, archetype);

        return vendor;
    }

    // Type-keyed so it stays correct if a future ModernUO version changes
    // what InitOutfit equips; anything unrecognized still gets a non-null
    // fallback rather than being left to crash on single-click.
    private static void NameWornApparel(Mobile vendor)
    {
        foreach (var item in vendor.Items)
        {
            if (item is BaseClothing { Name: null } clothing)
            {
                clothing.Name = ApparelName(clothing);
            }
        }
    }

    private static string ApparelName(BaseClothing clothing) => clothing switch
    {
        FancyShirt => "a fancy shirt",
        LongPants  => "a pair of long pants",
        BodySash   => "a body sash",
        Boots      => "a pair of boots",
        Cloak      => "a cloak",
        _          => clothing.GetType().Name
    };
}
