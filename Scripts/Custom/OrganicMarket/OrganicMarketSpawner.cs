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
        MarketHouseStyle.SmallShop            => "Small Shop",
        MarketHouseStyle.TwoStoryWoodPlaster  => "Two-Story Wood & Plaster",
        MarketHouseStyle.LargePatio           => "Large Patio House",
        MarketHouseStyle.SmallPlasterHouse    => "Small Brick & Plaster House",
        MarketHouseStyle.SmallStoneHouse      => "Small Fieldstone House",
        MarketHouseStyle.SmallWoodHouse       => "Small Wood House",
        MarketHouseStyle.WoodAndPlasterHouse  => "Wood & Plaster House",
        MarketHouseStyle.StoneAndPlasterHouse => "Stone & Plaster House",
        MarketHouseStyle.SandStonePatio       => "Sandstone Patio House",
        MarketHouseStyle.LogCabin             => "Log Cabin",
        MarketHouseStyle.SmallTower           => "Small Tower",
        _                                      => style.ToString()
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
        MarketHouseStyle.SmallShop            => 0xA0,
        MarketHouseStyle.TwoStoryWoodPlaster  => 0x76,
        MarketHouseStyle.LargePatio           => 0x8C,
        // SmallOldHouse variants (Multis/Houses/Houses.cs) - one class,
        // multi ID picks the visual (see Multis/Deeds.cs for the same
        // ID->name mapping stock deeds use).
        MarketHouseStyle.SmallPlasterHouse    => 0x68, // SmallBrickHouseDeed
        MarketHouseStyle.SmallStoneHouse      => 0x66, // FieldStoneHouseDeed
        MarketHouseStyle.SmallWoodHouse       => 0x6A, // WoodHouseDeed
        MarketHouseStyle.WoodAndPlasterHouse  => 0x6C, // WoodPlasterHouseDeed
        MarketHouseStyle.StoneAndPlasterHouse => 0x64, // StonePlasterHouseDeed
        MarketHouseStyle.SandStonePatio       => 0x9C,
        MarketHouseStyle.LogCabin             => 0x9A,
        MarketHouseStyle.SmallTower           => 0x98,
        _                                      => 0xA0
    };

    public static Point3D PlacementOffset(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.SmallShop            => new Point3D(-1, 4, 0),
        MarketHouseStyle.TwoStoryWoodPlaster  => new Point3D(-3, 7, 0),
        MarketHouseStyle.LargePatio           => new Point3D(-4, 7, 0),
        // Every SmallOldHouse variant shares the same deed offset
        // (Multis/Deeds.cs) regardless of which of the 5 multi IDs above.
        MarketHouseStyle.SmallPlasterHouse    => new Point3D(0, 4, 0),
        MarketHouseStyle.SmallStoneHouse      => new Point3D(0, 4, 0),
        MarketHouseStyle.SmallWoodHouse       => new Point3D(0, 4, 0),
        MarketHouseStyle.WoodAndPlasterHouse  => new Point3D(0, 4, 0),
        MarketHouseStyle.StoneAndPlasterHouse => new Point3D(0, 4, 0),
        MarketHouseStyle.SandStonePatio       => new Point3D(-1, 4, 0),
        MarketHouseStyle.LogCabin             => new Point3D(1, 6, 0),
        MarketHouseStyle.SmallTower           => new Point3D(3, 4, 0),
        _                                      => Point3D.Zero
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

            var vendor = SpawnVendor(authority, house, a);
            authority.Register(house, ArchetypeName(a), vendor);
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
        try
        {
            authority.Map = map;
            authority.Location = center;
            return HousePlacement.Check(authority, multiId, center, out toMove);
        }
        finally
        {
            authority.Location = savedLoc;
            authority.Map = savedMap;
        }
    }

    private static BaseHouse BuildHouse(MarketHouseStyle style, Mobile owner) => style switch
    {
        MarketHouseStyle.SmallShop            => new SmallShop(owner, 0xA0),
        MarketHouseStyle.TwoStoryWoodPlaster  => new TwoStoryHouse(owner, 0x76),
        MarketHouseStyle.LargePatio           => new LargePatioHouse(owner),
        MarketHouseStyle.SmallPlasterHouse    => new SmallOldHouse(owner, 0x68),
        MarketHouseStyle.SmallStoneHouse      => new SmallOldHouse(owner, 0x66),
        MarketHouseStyle.SmallWoodHouse       => new SmallOldHouse(owner, 0x6A),
        MarketHouseStyle.WoodAndPlasterHouse  => new SmallOldHouse(owner, 0x6C),
        MarketHouseStyle.StoneAndPlasterHouse => new SmallOldHouse(owner, 0x64),
        MarketHouseStyle.SandStonePatio       => new SandStonePatio(owner),
        MarketHouseStyle.LogCabin             => new LogCabin(owner),
        MarketHouseStyle.SmallTower           => new SmallTower(owner),
        _                                      => null
    };

    // A real commissioned PlayerVendor, not a stock town NPC: it links to
    // the house via the constructor (BaseHouse.PlayerVendors picks it up
    // through the House property's fieldChanged hook), and its gold
    // reserves are set to VendorCommissionCeiling so the daily upkeep
    // charge (PayTimer.OnTick) never exceeds what it's holding.
    private static PlayerVendor SpawnVendor(Mobile authority, BaseHouse house, MarketArchetype archetype)
    {
        var vendor = new PlayerVendor(authority, house)
        {
            ShopName = $"{ArchetypeName(archetype)} Test Shop",
            HoldGold = VendorCommissionCeiling,
            BankAccount = VendorCommissionCeiling
        };

        // Scan for a genuinely safe, walkable interior tile - clear of
        // walls, doors, and the fixtures just locked down - rather than
        // a fixed offset that can clip into whichever of those a given
        // house style happens to put there. Falls back to just inside
        // the sign if nothing on the floor plan qualifies (a very small
        // or oddly-shaped interior), which is still always inside the
        // house even if not ideal.
        Point3D loc;
        Direction facing;
        if (!InteriorTileFinder.TryFindVendorSpot(house, out loc, out facing))
        {
            loc = house.Sign?.Location ?? new Point3D(house.X, house.Y - 1, house.Z);
            var faceTarget = InteriorTileFinder.FrontDoorLocation(house) ?? house.BanLocation;
            facing = InteriorTileFinder.DirectionTo(loc, faceTarget);
        }

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
