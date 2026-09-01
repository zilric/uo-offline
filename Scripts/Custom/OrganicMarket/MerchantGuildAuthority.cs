// =========================================================================
// MerchantGuildAuthority.cs — persistent registry + global owner for every
// Organic Market test house, its locked-down fixtures, and its vendor.
//
// A single, hidden, non-playable Mobile living on the internal map. It
// never appears in the world itself; it exists so BaseHouse has a
// non-null owner to hand its key/bank-box setup during construction, and
// so this whole subsystem has one persistent place to keep the directory
// (parallel lists indexed by house slot — see Register/DeleteAt).
//
// RestrictDecay is set on every spawned house (not on this authority),
// which is what actually keeps a house from ever condemning/decaying
// regardless of owner state — see BaseHouse.DecayType.
// =========================================================================

using System.Collections.Generic;
using ModernUO.Serialization;
using Server;
using Server.Mobiles;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public enum MarketHouseStyle
{
    SmallShop,
    TwoStoryWoodPlaster,
    LargePatio,

    // SP-024: added for visual variety across dozens of ambient/vendor
    // placements - see OrganicMarketSpawner.BuildHouse for the real
    // ModernUO class + multi ID each maps to.
    SmallPlasterHouse,
    SmallStoneHouse,
    SmallWoodHouse,
    WoodAndPlasterHouse,
    StoneAndPlasterHouse,
    SandStonePatio,
    LogCabin,
    SmallTower,

    // SP-028: rounds out the catalog to every classic BaseHouse-derived
    // style ModernUO actually ships (Multis/Houses/Houses.cs) - see
    // OrganicMarketSpawner.BuildHouse for what's a genuinely distinct
    // multi ID versus a documented naming alias for art this list already
    // covers (ModernUO's stock house set doesn't have a separate class or
    // multi ID for every name real-world UO players use for these).
    SmallBrickHouse,
    FieldStoneHouse,
    TwoStoryStoneAndPlaster,
    TwoStoryVilla,
    TwoStoryLogCabin,
    SandstoneHouseWithPatio,
    MarbleHouseWithPatio,

    // SP-029: the three grandest classic structures ModernUO ships - only
    // ever rolled as a rare ambient-filler pick (see WorldHouseSeeder.
    // SeedInhabitation), never a vendor-shop style. Castle's footprint
    // alone is 31x31; finding open ground for these is expected to fail
    // far more often than any other style, which is exactly why they're a
    // low-probability roll rather than part of the normal rotation.
    LargeTower,
    Keep,
    Castle,

    // SP-026: rounds the catalog out to every real classic-house deed
    // ModernUO ships that this list didn't already cover under a
    // different name - see OrganicMarketSpawner.MultiId/BuildHouse for
    // exactly which multi ID/class each maps to, and OrganicMarketSpawner.
    // GetBaseDeedPrice for why several existing entries above (SmallShop,
    // SandStonePatio/SandstoneHouseWithPatio, LogCabin/TwoStoryLogCabin,
    // SmallPlasterHouse, ...) already ARE one of the task's requested 20
    // names under an alias this codebase settled on earlier and isn't
    // worth renaming out from under every place that already references
    // it (VendorCountFor, InhabitationNodes' round-robin, ...).
    StoneWorkshop,
    MarbleWorkshop,
    ThreeRoomBrickHouse
}

// SP-028: expanded from 4 to 6 distinct commercial themes, and renamed
// three of the original four to match this ticket's own naming
// (Blacksmith -> BlacksmithArmory, MageAlchemist -> MageApothecary,
// CurioRares -> TinkerCurio - its old curio/treasure-map stock folds into
// TinkerCurio's own broader "rarities" remit) rather than adding six more
// values alongside four that would otherwise sit unused. TailorFletcher
// keeps its exact name since the ticket reuses it unchanged. See
// StockTemplateEngine for each archetype's actual stock list and
// DynamicClutterGenerator for its themed interior fixtures.
//
// SP-029: TinkerCurio -> TinkerCarpenter (its remit narrows to hardware/
// carpentry - furniture, tinker tools, addon deeds; see StockTemplateEngine
// for exactly what moved) now that TinkerCurio's old curio/rarity side has
// its own dedicated home in the new 7th archetype, FisherCurioBaker -
// deep-sea fishing, tavern food, treasure hunting, and curio antiquarian
// stock that didn't fit anywhere else in the catalog.
public enum MarketArchetype
{
    BlacksmithArmory,
    MageApothecary,
    ScribeLibrary,
    RawResources,
    TailorFletcher,
    TinkerCarpenter,
    FisherCurioBaker
}

[SerializationGenerator(0, false)]
public partial class MerchantGuildAuthority : Mobile
{
    private static MerchantGuildAuthority _instance;

    public static MerchantGuildAuthority Instance => _instance;

    [SerializableField(0)]
    private List<BaseHouse> _houses;

    [SerializableField(1)]
    private List<int> _houseIds;

    [SerializableField(2)]
    private List<string> _archetypes;

    // PlayerVendor : Mobile (not BaseCreature — it isn't an AI-driven
    // creature), so the parallel list has to be typed for any Mobile.
    [SerializableField(3)]
    private List<Mobile> _vendors;

    [SerializableField(4)]
    private int _nextHouseId;

    public MerchantGuildAuthority()
    {
        Name = "Merchant Guild Authority";
        Body = 0x190;
        Hidden = true;
        Blessed = true;
        CantWalk = true;
        Frozen = true;

        _houses = new List<BaseHouse>();
        _houseIds = new List<int>();
        _archetypes = new List<string>();
        _vendors = new List<Mobile>();
        _nextHouseId = 1;

        MoveToWorld(Point3D.Zero, Map.Internal);
        _instance = this;
    }

    // Runs once per entity right after its own fields are deserialized —
    // safe here since it only touches this entity's own static backref.
    [AfterDeserialization]
    private void ReclaimInstance()
    {
        _instance = this;
    }

    // Deferred to after the whole world has loaded: safe to Delete-adjacent
    // cleanup here (removing dead entries), which touches game state.
    [AfterDeserialization(false)]
    private void PruneDeadEntries()
    {
        for (var i = _houses.Count - 1; i >= 0; i--)
        {
            if (_houses[i]?.Deleted != false)
            {
                RemoveEntryAt(i);
            }
        }
    }

    // Auto-discovered by AssemblyHandler.Invoke("Initialize") post-world-load.
    // Creates the singleton on a brand-new world; a restored world already
    // reclaimed it in ReclaimInstance above.
    public static void Initialize()
    {
        if (_instance?.Deleted != false)
        {
            _ = new MerchantGuildAuthority();
        }
    }

    public int Count => _houses.Count;

    public int HouseIdAt(int i) => _houseIds[i];

    public string ArchetypeAt(int i) => _archetypes[i];

    public BaseHouse HouseAt(int i) => _houses[i];

    public Mobile VendorAt(int i) => _vendors[i];

    public int Register(BaseHouse house, string archetype, Mobile vendor)
    {
        var id = _nextHouseId++;
        _houses.Add(house);
        _houseIds.Add(id);
        _archetypes.Add(archetype);
        _vendors.Add(vendor);
        return id;
    }

    // How far out from the house's own Location a footprint sweep looks
    // for stray items. Every style this tool places (SmallShop through
    // LargePatioHouse) fits well inside this radius; a little slack
    // beyond the real footprint is harmless since IsInside() still does
    // the precise filtering below.
    private const int FootprintSweepRange = 20;

    // Deletes every vendor the house actually has (SP-028: 1-4, not just
    // the one MerchantGuildAuthority itself tracks as "primary" - see
    // Register/VendorAt), the house, every item locked down in it OR
    // merely sitting in its footprint, and its door keys. Removes the slot
    // from every parallel list. Never throws — a half-torn-down entry
    // (house already gone, vendor already gone) is still cleanly dropped
    // from the registry.
    //
    // Vendors go FIRST, house second — deliberately. BaseHouse.OnAfterDelete()
    // calls KillVendors(), which calls PlayerVendor.Destroy(true) on
    // anything still sitting in house.PlayerVendors, and Destroy(true)
    // drops a loose backpack with whatever's left in it onto the ground
    // before deleting the vendor — that's the real source of "deleting a
    // house drops the vendor's backpack," not anything Mobile.Delete()
    // itself does. Clearing and deleting every vendor here first means
    // each one's own OnAfterDelete already sets House = null, which — via
    // the House field's fieldChanged hook, PlayerVendor.OnHouseChanged —
    // removes it from house.PlayerVendors. By the time house.Delete() runs
    // and KillVendors() looks, there's nothing left in the list to evict.
    public bool DeleteAt(int i)
    {
        if (i < 0 || i >= _houses.Count)
        {
            return false;
        }

        var house = _houses[i];
        var vendor = _vendors[i];

        if (house?.Deleted == false)
        {
            // Copy first — removing a vendor from house.PlayerVendors (the
            // fieldChanged hook above) mutates that same list as it goes,
            // which would otherwise skip entries mid-loop.
            var allVendors = new List<PlayerVendor>(house.PlayerVendors);
            foreach (var houseVendor in allVendors)
            {
                if (houseVendor?.Deleted != false)
                {
                    continue;
                }

                // Belt-and-suspenders on top of the fieldChanged hook -
                // guarantees KillVendors() (below, inside house.Delete())
                // has nothing left to find regardless of deletion order
                // elsewhere.
                house.PlayerVendors.Remove(houseVendor);
                ClearVendorInventory(houseVendor);
                houseVendor.Delete();
            }
        }
        else if (vendor?.Deleted == false)
        {
            // House is already gone but the tracked primary vendor
            // somehow isn't (a half-torn-down entry) - there's no
            // house.PlayerVendors left to iterate, so clean this one up
            // directly.
            ClearVendorInventory(vendor);
            vendor.Delete();
        }

        if (house?.Deleted == false)
        {
            var map = house.Map;
            var loc = house.Location;

            // Copy first — deleting a locked-down item mutates LockDowns
            // as it goes, which would otherwise skip entries mid-loop.
            var toDelete = new List<Item>(house.LockDowns);

            // Belt-and-suspenders: anything sitting in the footprint that
            // was never actually locked down (dropped loose, missed by
            // LockDown's IsCoOwner/Movable checks, or - now that the
            // vendor above is already gone - any stray backpack some
            // other path still managed to drop) still gets swept and
            // deleted before the house itself goes, so nothing orphans on
            // the ground once the multi under it is gone.
            if (map != null && map != Map.Internal)
            {
                foreach (var item in map.GetItemsInRange<Item>(loc, FootprintSweepRange))
                {
                    if (item?.Deleted == false && item != house && item != house.Sign &&
                        house.IsInside(item) && !toDelete.Contains(item))
                    {
                        toDelete.Add(item);
                    }
                }
            }

            foreach (var item in toDelete)
            {
                if (item?.Deleted == false)
                {
                    item.Delete();
                }
            }

            // Door keys live in this authority's bank box (CreateKeys ran
            // against it at construction) — RemoveKeys needs the house's
            // Doors list, so this has to run before house.Delete().
            house.RemoveKeys(this);

            house.Delete();
        }

        RemoveEntryAt(i);
        return true;
    }

    private static void ClearVendorInventory(Mobile vendor)
    {
        if (vendor.Backpack is { Deleted: false } backpack)
        {
            DeleteAllItems(backpack.Items);
        }

        // Clothing, hair, and the (now-empty) backpack container itself.
        DeleteAllItems(vendor.Items);

        // FindBankNoCreate(), never the BankBox property — that getter
        // auto-creates a bank box the moment it's read, which would leave
        // a brand-new one behind for Delete() to clean up instead of
        // there simply being none.
        if (vendor.FindBankNoCreate() is { Deleted: false } bank)
        {
            DeleteAllItems(bank.Items);
        }
    }

    private static void DeleteAllItems(List<Item> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i]?.Deleted == false)
            {
                items[i].Delete();
            }
        }
    }

    // SP-030: clears only a vendor's own for-sale stock (its Backpack's
    // contents) - unlike ClearVendorInventory above, this leaves the
    // vendor's worn outfit and bank box alone, since a restock replaces
    // what's for sale, not the vendor itself.
    private static void ClearVendorStock(PlayerVendor vendor)
    {
        if (vendor.Backpack is { Deleted: false } backpack)
        {
            DeleteAllItems(backpack.Items);
        }
    }

    // "Restock" for a PlayerVendor — there's no SBInfo stock to
    // regenerate (that's a BaseVendor-only concept; PlayerVendor sells
    // whatever's actually been dropped on it and priced). SP-030: now
    // does two things per shop house - replenishes every one of its
    // vendors' commission back to the ceiling (undoing whatever the daily
    // PayTimer upkeep has chipped off, see OrganicMarketSpawner.
    // VendorCommissionCeiling), AND wipes + re-runs StockTemplateEngine
    // against every one of its vendors, which is what actually makes the
    // Carpenter/Scribe/Tinker archetypes' dynamic rotation pools
    // (StockTemplateEngine.CarpenterFurniturePool/ScribeCombatScrollPool/
    // TinkerGadgetPool) reroll on each restock instead of only at initial
    // spawn.
    //
    // house.PlayerVendors (not the single tracked "primary" _vendors[i])
    // is what's walked here - same reasoning as DeleteAt's teardown pass:
    // a shop has 2-4 real vendors (OrganicMarketSpawner.VendorCountFor),
    // and this registry's own parallel lists only ever tracked one. Each
    // vendor's own position in that list is its "Vendor 1..4" slot, since
    // PlayerVendor.OnHouseChanged appends to it in the exact order
    // SpawnVendors originally constructed them in - see
    // OrganicMarketSpawner.SpawnVendors' own vendorIndex loop.
    private int RestockHouseVendors(int i)
    {
        var house = _houses[i];
        if (house?.Deleted != false)
        {
            return 0;
        }

        var archetype = OrganicMarketSpawner.ArchetypeFromName(_archetypes[i]);
        var count = 0;

        if (archetype is { } a)
        {
            var vendorIndex = 0;
            foreach (var v in house.PlayerVendors)
            {
                if (v is { Deleted: false })
                {
                    ClearVendorStock(v);
                    StockTemplateEngine.StockVendor(v, a, vendorIndex);
                    v.HoldGold = OrganicMarketSpawner.VendorCommissionCeiling;
                    v.BankAccount = OrganicMarketSpawner.VendorCommissionCeiling;
                    count++;
                }

                vendorIndex++;
            }
        }
        else if (_vendors[i] is PlayerVendor { Deleted: false } vendor)
        {
            // Ambient residence slot (no archetype - nothing to restock),
            // or a legacy/unparsed archetype string - just keep the
            // original gold-refill behavior on whatever's tracked as this
            // slot's "primary."
            vendor.HoldGold = OrganicMarketSpawner.VendorCommissionCeiling;
            vendor.BankAccount = OrganicMarketSpawner.VendorCommissionCeiling;
            count++;
        }

        return count;
    }

    public bool RestockAt(int i)
    {
        if (i < 0 || i >= _houses.Count)
        {
            return false;
        }

        return RestockHouseVendors(i) > 0;
    }

    public int RestockAll()
    {
        var count = 0;
        for (var i = 0; i < _houses.Count; i++)
        {
            count += RestockHouseVendors(i);
        }

        return count;
    }

    public int WipeAll()
    {
        var count = 0;
        for (var i = _houses.Count - 1; i >= 0; i--)
        {
            if (DeleteAt(i))
            {
                count++;
            }
        }

        return count;
    }

    // SP-026: pulls a house's slot out of the registry WITHOUT touching the
    // house, its vendor, or anything locked down inside it - the opposite
    // of DeleteAt, which exists specifically for the moment a player
    // actually buys an ambient filler house (AmbientHousePurchaseGump).
    // From that point on the house is real, player-owned property; leaving
    // it in this registry would mean the next [Wipe All Market Houses]
    // deletes a home someone paid for. Index lookup by reference (rather
    // than requiring the caller to already know its slot) since the only
    // caller is the purchase flow, which only has the BaseHouse itself in
    // hand.
    // Cheap pre-purchase check (AmbientHousePurchaseGump) - confirms the
    // house is still a live registry entry before any gold changes hands,
    // so a stale gump (the house got deleted or bought out from under the
    // player between opening the gump and clicking Buy) fails cleanly
    // instead of debiting a player for a house that's no longer theirs to
    // buy.
    public bool IsRegistered(BaseHouse house) => house != null && _houses.Contains(house);

    public bool Deregister(BaseHouse house)
    {
        var index = _houses.IndexOf(house);
        if (index < 0)
        {
            return false;
        }

        RemoveEntryAt(index);
        return true;
    }

    private void RemoveEntryAt(int i)
    {
        _houses.RemoveAt(i);
        _houseIds.RemoveAt(i);
        _archetypes.RemoveAt(i);
        _vendors.RemoveAt(i);
    }
}
