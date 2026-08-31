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
        MarketHouseStyle.SmallShop           => "Small Shop",
        MarketHouseStyle.TwoStoryWoodPlaster => "Two-Story Wood & Plaster",
        MarketHouseStyle.LargePatio          => "Large Patio House",
        _                                     => style.ToString()
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
        MarketHouseStyle.SmallShop           => 0xA0,
        MarketHouseStyle.TwoStoryWoodPlaster => 0x76,
        MarketHouseStyle.LargePatio          => 0x8C,
        _                                     => 0xA0
    };

    public static Point3D PlacementOffset(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.SmallShop           => new Point3D(-1, 4, 0),
        MarketHouseStyle.TwoStoryWoodPlaster => new Point3D(-3, 7, 0),
        MarketHouseStyle.LargePatio          => new Point3D(-4, 7, 0),
        _                                     => Point3D.Zero
    };

    // Builds and places everything at `center` (already validated by
    // MarketHousePlacementTarget via HousePlacement.Check), returns the
    // new registry slot index, or -1 on failure. `toMove` is whatever
    // HousePlacement.Check found standing where the house is going —
    // shifted to the house's ban location, same as a real deed placement.
    public static int PlaceTestHouse(
        Map map, Point3D center, MarketHouseStyle style, MarketArchetype archetype, List<IEntity> toMove
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
        house.Public = true;
        house.MoveToWorld(center, map);

        // house.Public only governs who may walk the interior region - the
        // doors themselves are built already locked, keyed to `authority`
        // (BaseHouse's AddEastDoor/AddSouthDoor helpers set Locked=true +
        // KeyValue at construction, same as a real player's house), and
        // that key never leaves authority's own bank box. Left alone, a
        // real player can never open a single one of these doors. Public
        // storefronts don't lock their own front door.
        UnlockDoors(house);

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

        // Clutter goes down BEFORE the vendor spot is chosen: InteriorTileFinder
        // reads house.LockDowns to steer clear of it, which only works if
        // it's already locked down by the time it scans.
        DynamicClutterGenerator.Furnish(house, archetype, authority);

        var vendor = SpawnVendor(authority, house, archetype);

        authority.Register(house, ArchetypeName(archetype), vendor);
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

    private static BaseHouse BuildHouse(MarketHouseStyle style, Mobile owner) => style switch
    {
        MarketHouseStyle.SmallShop           => new SmallShop(owner, 0xA0),
        MarketHouseStyle.TwoStoryWoodPlaster => new TwoStoryHouse(owner, 0x76),
        MarketHouseStyle.LargePatio          => new LargePatioHouse(owner),
        _                                     => null
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
