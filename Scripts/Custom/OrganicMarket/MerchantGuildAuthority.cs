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
    SmallTower
}

public enum MarketArchetype
{
    Blacksmith,
    MageAlchemist,
    CurioRares,
    TailorFletcher
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

    // Deletes the vendor, the house, every item locked down in it OR
    // merely sitting in its footprint, and its door keys. Removes the slot
    // from every parallel list. Never throws — a half-torn-down entry
    // (house already gone, vendor already gone) is still cleanly dropped
    // from the registry.
    //
    // Vendor goes FIRST, house second — deliberately. BaseHouse.OnAfterDelete()
    // calls KillVendors(), which calls PlayerVendor.Destroy(true) on
    // anything still sitting in house.PlayerVendors, and Destroy(true)
    // drops a loose backpack with whatever's left in it onto the ground
    // before deleting the vendor — that's the real source of "deleting a
    // house drops the vendor's backpack," not anything Mobile.Delete()
    // itself does. Clearing and deleting the vendor here first means its
    // own OnAfterDelete already sets House = null, which — via the House
    // field's fieldChanged hook, PlayerVendor.OnHouseChanged — removes it
    // from house.PlayerVendors. By the time house.Delete() runs and
    // KillVendors() looks, there's nothing left in the list to evict.
    public bool DeleteAt(int i)
    {
        if (i < 0 || i >= _houses.Count)
        {
            return false;
        }

        var house = _houses[i];
        var vendor = _vendors[i];

        if (vendor?.Deleted == false)
        {
            // Belt-and-suspenders on top of the fieldChanged hook above -
            // guarantees KillVendors() (below, inside house.Delete()) has
            // nothing to find regardless of deletion order elsewhere.
            if (vendor is PlayerVendor && house != null)
            {
                house.PlayerVendors.Remove((PlayerVendor)vendor);
            }

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

    // "Restock" for a PlayerVendor — there's no SBInfo stock to
    // regenerate (that's a BaseVendor-only concept; PlayerVendor sells
    // whatever's actually been dropped on it and priced) — means
    // replenishing its commission back to the ceiling, undoing whatever
    // the daily PayTimer upkeep has chipped off. See
    // OrganicMarketSpawner.VendorCommissionCeiling.
    public bool RestockAt(int i)
    {
        if (i < 0 || i >= _vendors.Count)
        {
            return false;
        }

        if (_vendors[i] is PlayerVendor { Deleted: false } vendor)
        {
            vendor.HoldGold = OrganicMarketSpawner.VendorCommissionCeiling;
            vendor.BankAccount = OrganicMarketSpawner.VendorCommissionCeiling;
            return true;
        }

        return false;
    }

    public int RestockAll()
    {
        var count = 0;
        foreach (var vendor in _vendors)
        {
            if (vendor is PlayerVendor { Deleted: false } pv)
            {
                pv.HoldGold = OrganicMarketSpawner.VendorCommissionCeiling;
                pv.BankAccount = OrganicMarketSpawner.VendorCommissionCeiling;
                count++;
            }
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

    private void RemoveEntryAt(int i)
    {
        _houses.RemoveAt(i);
        _houseIds.RemoveAt(i);
        _archetypes.RemoveAt(i);
        _vendors.RemoveAt(i);
    }
}
