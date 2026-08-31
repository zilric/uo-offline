// =========================================================================
// StockTemplateEngine.cs — populates a freshly commissioned PlayerVendor's
// inventory according to its archetype: themed bundled containers (armor
// sets, reagent packs, potion kegs) plus loose high-tier wares, all priced
// from a single era-accurate (1997-2001 T2A-standard) price dictionary.
//
// PlayerVendor has no public "set the price" entry point — VendorItem
// pricing is normally done through VendorPricePrompt, a player dragging
// an item onto the vendor and typing a number. The sanctioned way to do
// it from server-side code (no core edits, no reflection into a private
// method) is the same mechanism that prompt itself rides on:
// PlayerVendor.OnSubItemAdded auto-registers ANY item that becomes a
// direct child of the vendor's own Backpack at a default price of 999gp
// (see CanBeVendorItem/OnSubItemAdded in Mobiles/Vendors/PlayerVendor.cs).
// VendorItem.Price is a public settable property, so: drop the item in,
// then correct the price it was just auto-assigned.
//
// The same mechanism is what makes bundles work: CanBeVendorItem treats
// an item as sellable-on-its-own only if its parent ISN'T already a
// for-sale vendor item. So a themed sub-container (colored backpack,
// dyed pouch) gets dropped into the vendor and priced FIRST; only then
// do its contents go in, and PlayerVendor itself refuses to price them
// individually - the bundle sells as one unit, by construction.
// =========================================================================

using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Engines.OrganicMarket;

public static class StockTemplateEngine
{
    // ---- Price Dictionary ----------------------------------------------
    // Baseline gold prices for every loose good this engine generates.
    // Bundle aggregate prices are separate (below) - a bundle is priced
    // as a themed package, not the sum of its contents.
    private static readonly Dictionary<System.Type, int> BasePrices = new()
    {
        // Exceptional GM plate armor (loose, per piece)
        [typeof(PlateChest)]  = 300,
        [typeof(PlateArms)]   = 180,
        [typeof(PlateGloves)] = 150,
        [typeof(PlateGorget)] = 120,
        [typeof(PlateHelm)]   = 150,
        [typeof(PlateLegs)]   = 250,

        // Standard reagents (loose, per single reagent)
        [typeof(BlackPearl)]   = 3,
        [typeof(Bloodmoss)]    = 3,
        [typeof(Garlic)]       = 3,
        [typeof(Ginseng)]      = 3,
        [typeof(MandrakeRoot)] = 3,
        [typeof(Nightshade)]   = 3,
        [typeof(SpidersSilk)]  = 3,
        [typeof(SulfurousAsh)] = 3,

        // Exceptional GM weapons (loose)
        [typeof(Katana)]     = 150,
        [typeof(Halberd)]    = 200,
        [typeof(Broadsword)] = 175,
        [typeof(Bow)]        = 140,

        // High-tier / curio pieces
        [typeof(SilverEtchedMace)] = 1200,
        [typeof(PotionKeg)]        = 250,
        [typeof(Globe)]            = 185,
        [typeof(SmallUrn)]         = 65,
        [typeof(Vase)]             = 45,
        [typeof(LargeVase)]        = 90,
        [typeof(StatueSouth)]      = 220,
        [typeof(BustSouth)]        = 150,
        [typeof(Candelabra)]       = 90,
        [typeof(SewingKit)]        = 12,
        [typeof(FletcherTools)]    = 12,
        [typeof(BoltOfCloth)]      = 8
    };

    // Not simple per-type lookups (a spellbook's price depends on how
    // full it is, a treasure map's on its level, etc.) - named constants
    // instead of dictionary entries.
    private const int FullSpellbookPrice     = 5000; // 4,000-6,000 gp range
    private const int VanquishingPrice       = 3500;
    private const int TreasureMapLevel5Price = 750;
    private const int ArmorBundlePrice       = 2200; // 2,000-2,500 gp range
    private const int ReagentBundlePrice     = 650;  // 500-800 gp range
    private const int PotionKegBundlePrice   = 700;

    private const int ArmorBundleHue   = 0x489; // deep blue
    private const int ReagentPouchHue  = 0x48E; // moss green
    private const int PotionBagHue     = 0x497; // amber
    private const int ClothBagHue      = 0x47E; // violet

    public static void StockVendor(PlayerVendor vendor, MarketArchetype archetype)
    {
        if (vendor?.Backpack == null)
        {
            return;
        }

        switch (archetype)
        {
            case MarketArchetype.Blacksmith:
                StockBlacksmith(vendor);
                break;
            case MarketArchetype.MageAlchemist:
                StockMageAlchemist(vendor);
                break;
            case MarketArchetype.CurioRares:
                StockCurioRares(vendor);
                break;
            case MarketArchetype.TailorFletcher:
                StockTailorFletcher(vendor);
                break;
        }
    }

    // ---- Selling primitives ---------------------------------------------

    // Drops a loose item straight into the vendor's pack and prices it
    // (dictionary lookup, or an explicit override for one-off items the
    // dictionary can't key on a bare Type).
    private static void SellLoose(PlayerVendor vendor, Item item, int? priceOverride = null)
    {
        vendor.Backpack.DropItem(item);
        var vi = vendor.GetVendorItem(item);
        if (vi != null)
        {
            vi.Price = priceOverride ?? BasePrices.GetValueOrDefault(item.GetType(), 100);
        }
    }

    // Prices the CONTAINER as one unit before populating it, so
    // PlayerVendor.CanBeVendorItem sees an already-for-sale parent and
    // never individually prices what goes in afterward.
    private static void SellBundle(
        PlayerVendor vendor, Container bundle, int hue, int price, string description, params Item[] contents
    )
    {
        bundle.Hue = hue;
        vendor.Backpack.DropItem(bundle);

        var vi = vendor.GetVendorItem(bundle);
        if (vi != null)
        {
            vi.Price = price;
            vi.Description = description;
        }

        foreach (var item in contents)
        {
            bundle.DropItem(item);
        }
    }

    // Fallback crafter name for exceptional pieces that don't pass their
    // own (see per-archetype callers below for the flavored names).
    private const string DefaultCrafterName = "the Merchant Guild Authority";

    // BaseWeapon.Crafter / BaseArmor.Crafter are plain strings (the
    // crafter's captured name, e.g. Crafter = from.RawName at craft
    // time) - not a Mobile reference, so there's nothing to dereference
    // on the null path in OnSingleClickPreUOTD.
    //
    // The actual null path there is Name: this server runs pre-UOTD
    // (Core.UOTD is false for a T2A-era shard), so every single-click on
    // a BaseWeapon/BaseArmor/BaseClothing routes through
    // OnSingleClickPreUOTD, and every branch of that method falls back to
    // `Localization.GetText(LabelNumber)` whenever Name is null - a call
    // that returns null on this server (Localization.Configure() only
    // loads cliloc files when the engine's own _loadLocalizationOnStartup
    // constant is true, and it's false), so `.ToLowerInvariant()` on that
    // null throws and disconnects the client. Giving every item an
    // explicit Name is the one guaranteed way to keep OnSingleClickPreUOTD
    // out of that fallback entirely, regardless of whether cliloc data is
    // ever loaded.
    //
    // Name needs an article baked in ("a katana") for the ordinary
    // exceptional-quality branch, which uses it as-is
    // (`{name} of exceptional quality`).
    private static T Exceptional<T>(T item, string name, string crafterName = DefaultCrafterName) where T : Item
    {
        item.Name = name;

        switch (item)
        {
            case BaseArmor armor:
                armor.Quality = ArmorQuality.Exceptional;
                armor.Crafter = crafterName;
                break;
            case BaseWeapon weapon:
                weapon.Quality = WeaponQuality.Exceptional;
                weapon.Crafter = crafterName;
                break;
        }

        return item;
    }

    // ---- Archetype templates ---------------------------------------------

    private const string MasterBlacksmithName = "a Master Blacksmith";
    private const string MasterBowyerName = "a Master Bowyer";

    private static void StockBlacksmith(PlayerVendor vendor)
    {
        // Full GM Plate Armor Set - one exceptional piece each, bundled.
        SellBundle(
            vendor, new Backpack(), ArmorBundleHue, ArmorBundlePrice, "Full GM Plate Armor Set (exceptional)",
            Exceptional(new PlateChest(), "a platemail chest", MasterBlacksmithName),
            Exceptional(new PlateArms(), "a pair of platemail arms", MasterBlacksmithName),
            Exceptional(new PlateGloves(), "a pair of platemail gloves", MasterBlacksmithName),
            Exceptional(new PlateGorget(), "a platemail gorget", MasterBlacksmithName),
            Exceptional(new PlateHelm(), "a platemail helm", MasterBlacksmithName),
            Exceptional(new PlateLegs(), "a pair of platemail legs", MasterBlacksmithName)
        );

        // Loose exceptional GM weapons.
        SellLoose(vendor, Exceptional(new Katana(), "a katana", MasterBlacksmithName));
        SellLoose(vendor, Exceptional(new Halberd(), "a halberd", MasterBlacksmithName));
        SellLoose(vendor, Exceptional(new Broadsword(), "a broadsword", MasterBlacksmithName));

        // High-tier era pieces: a Vanquishing weapon and a silver-etched
        // special craft. Identified=true so it takes OnSingleClickPreUOTD's
        // regular magic-item branch (which appends " of Vanquishing" to
        // Name) instead of the "an unidentified {Name}" branch, which
        // expects a bare noun rather than the article-prefixed Name every
        // other item here uses - a vendor selling it at a fixed price has
        // already appraised it anyway.
        var vanq = Exceptional(new Katana(), "a katana", MasterBlacksmithName);
        vanq.DamageLevel = WeaponDamageLevel.Vanq;
        vanq.Identified = true;
        SellLoose(vendor, vanq, VanquishingPrice);
        SellLoose(vendor, Exceptional(new SilverEtchedMace(), "a silver-etched mace", MasterBlacksmithName));
    }

    private static void StockMageAlchemist(PlayerVendor vendor)
    {
        // Reagent Bundle - 100 of each of the 8 standard reagents, in a
        // dyed pouch.
        SellBundle(
            vendor, new Pouch(), ReagentPouchHue, ReagentBundlePrice, "Reagent Bundle (100 of each)",
            Stack(new BlackPearl(), 100), Stack(new Bloodmoss(), 100), Stack(new Garlic(), 100),
            Stack(new Ginseng(), 100), Stack(new MandrakeRoot(), 100), Stack(new Nightshade(), 100),
            Stack(new SpidersSilk(), 100), Stack(new SulfurousAsh(), 100)
        );

        // Potion Keg Bundle - three kegs of the most-wanted brews.
        SellBundle(
            vendor, new Bag(), PotionBagHue, PotionKegBundlePrice, "Potion Keg Bundle",
            new PotionKeg { Type = PotionEffect.HealGreater, Held = 100 },
            new PotionKeg { Type = PotionEffect.CureGreater, Held = 100 },
            new PotionKeg { Type = PotionEffect.RefreshTotal, Held = 100 }
        );

        // Full 64-spell spellbook, loose.
        SellLoose(vendor, new Spellbook(ulong.MaxValue), FullSpellbookPrice);
    }

    private static void StockCurioRares(PlayerVendor vendor)
    {
        SellLoose(vendor, new Globe());
        SellLoose(vendor, new SmallUrn());
        SellLoose(vendor, new Vase());
        SellLoose(vendor, new LargeVase());
        SellLoose(vendor, new StatueSouth());
        SellLoose(vendor, new BustSouth());
        SellLoose(vendor, new Candelabra());

        // A Level 5 tattered treasure map - a rare curiosity in its own right.
        var map = vendor.Map is { } m && m != Map.Internal ? m : Map.Felucca;
        SellLoose(vendor, new TreasureMap(5, map), TreasureMapLevel5Price);
    }

    private static void StockTailorFletcher(PlayerVendor vendor)
    {
        SellLoose(vendor, Exceptional(new Bow(), "a bow", MasterBowyerName));
        SellLoose(vendor, new SewingKit());
        SellLoose(vendor, new FletcherTools());

        SellBundle(
            vendor, new Bag(), ClothBagHue, 300, "Bolt of Cloth Bundle",
            Stack(new BoltOfCloth(), 20), Stack(new BoltOfCloth(), 20)
        );
    }

    private static Item Stack(Item item, int amount)
    {
        item.Amount = amount;
        return item;
    }
}
