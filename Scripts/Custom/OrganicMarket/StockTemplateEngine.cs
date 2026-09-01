// =========================================================================
// StockTemplateEngine.cs — populates a freshly commissioned PlayerVendor's
// inventory according to its archetype AND its strict role within a
// multi-vendor shop: Vendor 1 through Vendor 4 each carry a fixed,
// distinct inventory table per archetype (SP-029), not a cycling tier.
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
// The same mechanism is what makes subcontainer packaging work:
// CanBeVendorItem treats an item as sellable-on-its-own only if its
// parent ISN'T already a for-sale vendor item. So a themed subcontainer
// (a Swordsmanship box, a colored suit backpack, a reagent pouch) gets
// dropped into the vendor and priced FIRST; only then do its contents go
// in, and PlayerVendor itself refuses to price them individually - the
// subcontainer sells as one unit, by construction. CreatePackagedSub-
// container/PackAndPriceSuit/SellBundle are three names for exactly this
// same mechanic, kept separate because each reads better at its own call
// site (a themed weapon box vs. a matching armor suit vs. an ad-hoc
// bundle of loose stacks).
//
// SP-029: every vendor's own 0-based slot index within its shop
// (OrganicMarketSpawner.SpawnVendors' loop index, StockVendor's
// vendorIndex param) now maps DIRECTLY to one of four fixed roles per
// archetype - "Vendor 1"/"Vendor 2"/"Vendor 3"/"Vendor 4 (Specialty)" -
// wrapping via vendorIndex % 4 for the rare house style with more than
// four vendor spots, rather than SP-028's 3-tier cycling scheme.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;

namespace Server.Engines.OrganicMarket;

public static class StockTemplateEngine
{
    // ---- Price Dictionary ----------------------------------------------
    // Baseline gold prices for every loose good this engine generates that
    // isn't already priced inline at its own call site (a themed
    // subcontainer's price is always passed explicitly - this dictionary
    // only covers items SellLoose falls back to for a bare Type lookup).
    private static readonly Dictionary<Type, int> BasePrices = new()
    {
        [typeof(BlackPearl)]   = 3,
        [typeof(Bloodmoss)]    = 3,
        [typeof(Garlic)]       = 3,
        [typeof(Ginseng)]      = 3,
        [typeof(MandrakeRoot)] = 3,
        [typeof(Nightshade)]   = 3,
        [typeof(SpidersSilk)]  = 3,
        [typeof(SulfurousAsh)] = 3,

        [typeof(IronIngot)] = 3,
        [typeof(Board)]     = 3,
        [typeof(Log)]       = 2,
        [typeof(Leather)]   = 4,
        [typeof(Granite)]   = 3,
        [typeof(Cloth)]     = 3,
        [typeof(Shaft)]     = 2,

        [typeof(Lockpick)]     = 5,
        [typeof(Key)]          = 8,
        [typeof(TinkerTools)]  = 30,
        [typeof(SewingKit)]    = 12,
        [typeof(FletcherTools)] = 12,
        [typeof(SmithHammer)]  = 20,
        [typeof(Pickaxe)]      = 25,

        [typeof(DyeTub)]    = 15,
        [typeof(BlackDyeTub)] = 400, // rare-dye tub, priced well above a plain one
        [typeof(Dyes)]      = 10
    };

    private const string DefaultCrafterName = "the Merchant Guild Authority";

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
        bundle.Name = description;
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

    // SP-029: creates an empty, already-priced subcontainer of type
    // TContainer (WoodenBox, Pouch, Backpack, Bag, ...) and drops it into
    // the vendor's pack, same pricing-before-contents rule SellBundle
    // uses. Returns the container so the caller can populate it in a loop
    // (a variable "2-3x"/"3-4x" repeat count doesn't fit SellBundle's
    // fixed params array cleanly).
    private static TContainer CreatePackagedSubcontainer<TContainer>(
        PlayerVendor vendor, string name, int hue, int price
    ) where TContainer : Container, new()
    {
        var container = new TContainer { Hue = hue, Name = name };
        vendor.Backpack.DropItem(container);

        var vi = vendor.GetVendorItem(container);
        if (vi != null)
        {
            vi.Price = price;
            vi.Description = name;
        }

        return container;
    }

    // SP-029: a complete matching armor suit, packed into a colored
    // backpack and sold as one purchasable unit - mechanically identical
    // to SellBundle with a Backpack container, kept as its own named
    // method because "this bundle IS a matching suit" reads better at
    // every Vendor 2 (Armorer/Leather Specialist) call site than a bare
    // SellBundle call would.
    private static void PackAndPriceSuit(PlayerVendor vendor, string name, int hue, int price, params Item[] items) =>
        SellBundle(vendor, new Backpack(), hue, price, name, items);

    // SP-030: an empty container registered as a vendor item but
    // explicitly NOT for sale (Price = -1) - the opposite intent of
    // CreatePackagedSubcontainer above. VendorItem.IsForSale is
    // `Price >= 0`, so 0 does NOT work here; only a negative price marks
    // a container "not for sale," matching the exact convention
    // PlayerVendor's own VendorPricePrompt uses when a player types a
    // non-numeric response (Mobiles/Vendors/PlayerVendor.cs,
    // VendorPricePrompt.SetInfo: "price < 0 // Not for sale"). This
    // matters because PlayerVendor.CanBeVendorItem only allows an item to
    // become its own individually-priced VendorItem when its parent
    // container IS registered AND is NOT for sale - a container with no
    // VendorItem at all, or one priced >= 0, both block its children from
    // ever being individually sellable. So a "browse and buy just one"
    // organizer (a weapon box, an ingot organizer, a tool rack) has to be
    // built this way; CreatePackagedSubcontainer's positive price is what
    // makes a bundle sell as one atomic unit instead. Returns the empty
    // container so the caller can populate it via AddDisplayItem/
    // AddGMDisplayItem.
    private static TContainer CreateDisplayContainer<TContainer>(PlayerVendor vendor, string name, int hue)
        where TContainer : Container, new()
    {
        var container = new TContainer { Hue = hue, Name = name };
        vendor.Backpack.DropItem(container);

        var vi = vendor.GetVendorItem(container);
        if (vi != null)
        {
            vi.Price = -1;
            vi.Description = name;
        }

        return container;
    }

    // Drops a plain item into an already-created display container (see
    // CreateDisplayContainer) and prices it individually - this is what
    // actually makes one item inside the organizer purchasable on its
    // own, since OnSubItemAdded's default 999gp auto-price still needs
    // correcting the same way every other sold item here does.
    private static T AddDisplayItem<T>(PlayerVendor vendor, Container container, T item, int price) where T : Item
    {
        container.DropItem(item);
        var vi = vendor.GetVendorItem(item);
        if (vi != null)
        {
            vi.Price = price;
        }

        return item;
    }

    // Display-container counterpart of AddGMItem's container overload -
    // applies GM Exceptional quality/crafter first, then individually
    // prices the piece inside the organizer rather than leaving it
    // unpriced as AddGMItem(container, ...) does for a true bundle.
    private static T AddGMDisplayItem<T>(
        PlayerVendor vendor, Container container, T item, string name, int price, string crafterName = DefaultCrafterName
    ) where T : Item
    {
        ApplyExceptional(item, name, crafterName);
        return AddDisplayItem(vendor, container, item, price);
    }

    // SP-029: the one place every "GM" weapon/armor piece in this file
    // routes through - sets Name (the pre-UOTD single-click safety
    // fallback every item here already needed, see the SP-028 comment
    // this replaces), Quality = Exceptional on whichever of BaseArmor/
    // BaseWeapon the item actually is (there's no single shared
    // ItemQuality enum - see ArmorQuality/WeaponQuality in Armor/Weapons
    // Enums.cs), and Crafter. Two overloads: drop into an already-priced
    // subcontainer (no individual price - the box sells as one unit), or
    // sell loose directly from the vendor's own pack at its own price.
    private static T AddGMItem<T>(Container container, T item, string name, string crafterName = DefaultCrafterName)
        where T : Item
    {
        ApplyExceptional(item, name, crafterName);
        container.DropItem(item);
        return item;
    }

    private static T AddGMItem<T>(
        PlayerVendor vendor, T item, string name, int price, string crafterName = DefaultCrafterName
    ) where T : Item
    {
        ApplyExceptional(item, name, crafterName);
        SellLoose(vendor, item, price);
        return item;
    }

    // BaseWeapon.Crafter / BaseArmor.Crafter are plain strings (the
    // crafter's captured name, e.g. Crafter = from.RawName at craft
    // time) - not a Mobile reference, so there's nothing to dereference
    // on the null path in OnSingleClickPreUOTD.
    //
    // The actual null path there is Name: this server runs pre-UOTD
    // (Core.UOTD is false for a T2A-era shard), so every single-click on
    // a BaseWeapon/BaseArmor/BaseClothing routes through
    // OnSingleClickPreUOTD, and every branch of that method falls back to
    // `Localization.GetText(LabelNumber)` whenever Name is null, and
    // that always returns null here (cliloc loading is off by default
    // in Localization.Configure()). Left alone, every such item this
    // engine creates would crash a client the moment someone clicked it.
    private static void ApplyExceptional(Item item, string name, string crafterName)
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
    }

    // Same as ApplyExceptional, but returns the item - needed anywhere an
    // exceptional piece has to be built inline as one element of a
    // `params Item[]` array (PackAndPriceSuit/SellBundle's contents),
    // where the void two-arg AddGMItem(container, item, name) overload's
    // separate drop-into-container step doesn't fit.
    private static T Exceptional<T>(T item, string name, string crafterName = DefaultCrafterName) where T : Item
    {
        ApplyExceptional(item, name, crafterName);
        return item;
    }

    // Standard (non-exceptional) quality item that still needs the same
    // Name treatment for the pre-UOTD single-click safety fallback.
    private static T Named<T>(T item, string name) where T : Item
    {
        item.Name = name;
        return item;
    }

    private static Item Stack(Item item, int amount)
    {
        item.Amount = amount;
        return item;
    }

    // SP-029: 7% chance, Vendor 2 through Vendor 4 only (vendorIndex 1-3 -
    // "Vendor 1" never rolls this, it's the shop's own baseline stock) -
    // a single identified mid-tier magic find: a Ruin or Might weapon, a
    // Hardening armor piece, or a charged combat wand. Every archetype's
    // Vendor 2+ gets a chance at this, not just BlacksmithArmory -
    // "bought off an adventurer passing through" fits any shop.
    private const double WildernessLootChance = 0.07;

    private static void TryAddWildernessLoot(PlayerVendor vendor, int vendorIndex)
    {
        if (vendorIndex is < 1 or > 3 || Utility.RandomDouble() >= WildernessLootChance)
        {
            return;
        }

        // SP-030: economy rebalance - Ruin/Might/Hardening tier is
        // 800-1,800 gp across the board (a "found on an adventurer" magic
        // item is worth a real premium over this shop's own plain GM
        // stock, not a flat bargain price).
        switch (Utility.Random(4))
        {
            case 0:
                var ruin = AddGMItem(vendor, new Katana(), "a katana", Utility.RandomMinMax(800, 1800));
                ruin.DamageLevel = WeaponDamageLevel.Ruin;
                ruin.Identified = true;
                break;

            case 1:
                var might = AddGMItem(vendor, new WarMace(), "a war mace", Utility.RandomMinMax(800, 1800));
                might.DamageLevel = WeaponDamageLevel.Might;
                might.Identified = true;
                break;

            case 2:
                var hardened = AddGMItem(vendor, new PlateChest(), "a platemail chest", Utility.RandomMinMax(800, 1800));
                hardened.ProtectionLevel = ArmorProtectionLevel.Hardening;
                hardened.Identified = true;
                break;

            default:
                var wand = new LightningWand { Identified = true };
                SellLoose(vendor, wand, Utility.RandomMinMax(800, 1800));
                break;
        }
    }

    // ---- Dispatch ---------------------------------------------------------

    public static void StockVendor(PlayerVendor vendor, MarketArchetype archetype, int vendorIndex)
    {
        if (vendor?.Backpack == null)
        {
            return;
        }

        var slot = vendorIndex % 4;

        switch (archetype)
        {
            case MarketArchetype.BlacksmithArmory:
                StockBlacksmithArmory(vendor, slot);
                break;
            case MarketArchetype.MageApothecary:
                StockMageApothecary(vendor, slot);
                break;
            case MarketArchetype.ScribeLibrary:
                StockScribeLibrary(vendor, slot);
                break;
            case MarketArchetype.RawResources:
                StockRawResources(vendor, slot);
                break;
            case MarketArchetype.TailorFletcher:
                StockTailorFletcher(vendor, slot);
                break;
            case MarketArchetype.TinkerCarpenter:
                StockTinkerCarpenter(vendor, slot);
                break;
            case MarketArchetype.FisherCurioBaker:
                StockFisherCurioBaker(vendor, slot);
                break;
        }

        TryAddWildernessLoot(vendor, slot);
    }

    // ==== BlacksmithArmory ==================================================

    private const string MasterBlacksmithName = "a Master Blacksmith";
    private const string MasterArmorerName = "a Master Armorer";

    private static void StockBlacksmithArmory(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // GM Weaponsmith - SP-030: DISPLAY boxes (browse & buy one), zero bows/shields
                var swords = CreateDisplayContainer<WoodenBox>(vendor, "Swordsmanship Box", 0);
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    AddGMDisplayItem(vendor, swords, new Katana(), "a katana", 190, MasterBlacksmithName);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    AddGMDisplayItem(vendor, swords, new Halberd(), "a halberd", 230, MasterBlacksmithName);
                }
                AddGMDisplayItem(vendor, swords, new Broadsword(), "a broadsword", 180, MasterBlacksmithName);
                AddGMDisplayItem(vendor, swords, new Broadsword(), "a broadsword", 180, MasterBlacksmithName);
                AddGMDisplayItem(vendor, swords, new VikingSword(), "a viking sword", 170, MasterBlacksmithName);
                AddGMDisplayItem(vendor, swords, new VikingSword(), "a viking sword", 170, MasterBlacksmithName);

                var fencing = CreateDisplayContainer<WoodenBox>(vendor, "Fencing Box", 0);
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    AddGMDisplayItem(vendor, fencing, new Kryss(), "a kryss", 150, MasterBlacksmithName);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    AddGMDisplayItem(vendor, fencing, new WarFork(), "a war fork", 160, MasterBlacksmithName);
                }
                AddGMDisplayItem(vendor, fencing, new ShortSpear(), "a short spear", 140, MasterBlacksmithName);
                AddGMDisplayItem(vendor, fencing, new ShortSpear(), "a short spear", 140, MasterBlacksmithName);

                var maces = CreateDisplayContainer<WoodenBox>(vendor, "Mace Fighting Box", 0);
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    AddGMDisplayItem(vendor, maces, new WarHammer(), "a heavy war hammer", 200, MasterBlacksmithName);
                }
                AddGMDisplayItem(vendor, maces, new WarMace(), "a war mace", 180, MasterBlacksmithName);
                AddGMDisplayItem(vendor, maces, new WarMace(), "a war mace", 180, MasterBlacksmithName);
                AddGMDisplayItem(vendor, maces, new BlackStaff(), "a black staff", 150, MasterBlacksmithName);
                AddGMDisplayItem(vendor, maces, new BlackStaff(), "a black staff", 150, MasterBlacksmithName);
                break;

            case 1: // GM Armorer - full plate suits in colored bags, chain sets
                var suitHues = new[] { 0x455, 0x21, 0x102, 0x59 }; // Charcoal, Blood Red, Royal Blue, Forest Green
                var suitCount = Utility.RandomMinMax(3, 4);
                for (var i = 0; i < suitCount; i++)
                {
                    var hue = suitHues[i % suitHues.Length];
                    PackAndPriceSuit(
                        vendor, "Full GM Plate Armor Set (exceptional)", hue, Utility.RandomMinMax(2200, 2500),
                        Exceptional(new PlateChest(), "a platemail chest", MasterArmorerName),
                        Exceptional(new PlateArms(), "a pair of platemail arms", MasterArmorerName),
                        Exceptional(new PlateGloves(), "a pair of platemail gloves", MasterArmorerName),
                        Exceptional(new PlateGorget(), "a platemail gorget", MasterArmorerName),
                        Exceptional(new PlateHelm(), "a platemail helm", MasterArmorerName),
                        Exceptional(new PlateLegs(), "a pair of platemail legs", MasterArmorerName)
                    );
                }

                for (var i = 0; i < 2; i++)
                {
                    PackAndPriceSuit(
                        vendor, "GM Chainmail Coif & Tunic Set", 0x453, 950,
                        Exceptional(new ChainCoif(), "a chain coif", MasterArmorerName),
                        Exceptional(new ChainChest(), "a chainmail tunic", MasterArmorerName)
                    );
                }
                break;

            case 2: // Shield Specialist
                for (var i = 0; i < 3; i++)
                {
                    AddGMItem(vendor, new HeaterShield(), "a heater shield", 220);
                }
                for (var i = 0; i < 3; i++)
                {
                    AddGMItem(vendor, new MetalKiteShield(), "a metal kite shield", 250);
                }
                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new WoodenKiteShield(), "a wooden kite shield", 170);
                }
                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new Buckler(), "a buckler", 130);
                }
                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new BronzeShield(), "a bronze shield", 280);
                }
                break;

            default: // Specialty - Master Armory ladder: Force/Power weapons, Fortification/Invulnerability
                     // armor, Vanquishing/Supremely Accurate flagships, and Super Slayers.
                     // SP-030: full economy pricing ladder - see the tier bands documented above
                     // TryAddWildernessLoot (Ruin/Might/Hardening is that method's own 800-1,800 tier;
                     // this slot covers everything above it).
                var force = AddGMItem(vendor, new WarHammer(), "a heavy war hammer", Utility.RandomMinMax(2500, 4500), MasterBlacksmithName);
                force.DamageLevel = WeaponDamageLevel.Force;
                force.Identified = true;

                var power = AddGMItem(vendor, new ExecutionersAxe(), "an executioner's axe", Utility.RandomMinMax(6000, 9500), MasterBlacksmithName);
                power.DamageLevel = WeaponDamageLevel.Power;
                power.Identified = true;

                var fortified = AddGMItem(vendor, new PlateLegs(), "a pair of platemail legs", Utility.RandomMinMax(2500, 4500), MasterArmorerName);
                fortified.ProtectionLevel = ArmorProtectionLevel.Fortification;
                fortified.Identified = true;

                var invulnerable = AddGMItem(vendor, new PlateChest(), "a platemail chest", Utility.RandomMinMax(6000, 9500), MasterArmorerName);
                invulnerable.ProtectionLevel = ArmorProtectionLevel.Invulnerability;
                invulnerable.Identified = true;

                // Vanquishing/Supremely Accurate: 14,000-25,000, with the
                // flagship Katana and Halberd scaled to the top of that
                // band (18,000-22,000+) per the ticket's own example.
                var vanq = AddGMItem(vendor, new Katana(), "a katana", Utility.RandomMinMax(18000, 22000), MasterBlacksmithName);
                vanq.DamageLevel = WeaponDamageLevel.Vanq;
                vanq.Identified = true;

                var supAccurate = AddGMItem(vendor, new Broadsword(), "a broadsword", Utility.RandomMinMax(14000, 18000), MasterBlacksmithName);
                supAccurate.AccuracyLevel = WeaponAccuracyLevel.Supremely;
                supAccurate.Identified = true;

                // Super Slayers: 15,000-30,000.
                var silver = AddGMItem(vendor, new Halberd(), "a halberd", Utility.RandomMinMax(18000, 22000), MasterBlacksmithName);
                silver.Slayer = SlayerName.Silver;
                silver.Identified = true;

                var dragonSlayer = AddGMItem(vendor, new VikingSword(), "a viking sword", Utility.RandomMinMax(15000, 20000), MasterBlacksmithName);
                dragonSlayer.Slayer = SlayerName.DragonSlaying;
                dragonSlayer.Identified = true;

                var daemonDismissal = AddGMItem(vendor, new WarMace(), "a war mace", Utility.RandomMinMax(15000, 20000), MasterBlacksmithName);
                daemonDismissal.Slayer = SlayerName.DaemonDismissal;
                daemonDismissal.Identified = true;
                break;
        }
    }

    // ==== MageApothecary =====================================================

    private static void StockMageApothecary(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Bulk Reagent Supplier
                var bundleCount = Utility.RandomMinMax(3, 4);
                for (var i = 0; i < bundleCount; i++)
                {
                    SellBundle(
                        vendor, new Bag(), 0x48E, Utility.RandomMinMax(4600, 4800), "800-Reagent Bundle (100 of each)",
                        Stack(new BlackPearl(), 100), Stack(new Bloodmoss(), 100), Stack(new Garlic(), 100),
                        Stack(new Ginseng(), 100), Stack(new MandrakeRoot(), 100), Stack(new Nightshade(), 100),
                        Stack(new SpidersSilk(), 100), Stack(new SulfurousAsh(), 100)
                    );
                }

                // 8 single-reagent sub-pouches, each holding 3-4 duplicate
                // 100-stacks of that one reagent - priced at a conceptual
                // NPC base (4gp/reagent, in line with a T2A reagent
                // shop's own per-piece price) +15%, times how many
                // 100-stacks this particular pouch got.
                AddReagentPouch(vendor, "Black Pearl Pouch", () => new BlackPearl());
                AddReagentPouch(vendor, "Bloodmoss Pouch", () => new Bloodmoss());
                AddReagentPouch(vendor, "Garlic Pouch", () => new Garlic());
                AddReagentPouch(vendor, "Ginseng Pouch", () => new Ginseng());
                AddReagentPouch(vendor, "Mandrake Root Pouch", () => new MandrakeRoot());
                AddReagentPouch(vendor, "Nightshade Pouch", () => new Nightshade());
                AddReagentPouch(vendor, "Spider's Silk Pouch", () => new SpidersSilk());
                AddReagentPouch(vendor, "Sulfurous Ash Pouch", () => new SulfurousAsh());
                break;

            case 1: // Potion Brewer - SP-030: DISPLAY pouches (browse & buy one 10-stack), categorized by use
                var heal = CreateDisplayContainer<Pouch>(vendor, "Heal Pouch", 0x489);
                for (var i = 0; i < 4; i++)
                {
                    AddDisplayItem(vendor, heal, Stack(new GreaterHealPotion(), 10), 230);
                }

                var cure = CreateDisplayContainer<Pouch>(vendor, "Cure Pouch", 0x59);
                for (var i = 0; i < 4; i++)
                {
                    AddDisplayItem(vendor, cure, Stack(new GreaterCurePotion(), 10), 200);
                }

                var refresh = CreateDisplayContainer<Pouch>(vendor, "Refresh Pouch", 0x2C);
                for (var i = 0; i < 4; i++)
                {
                    AddDisplayItem(vendor, refresh, Stack(new TotalRefreshPotion(), 10), 180);
                }

                var stats = CreateDisplayContainer<Pouch>(vendor, "Strength/Agility Pouch", 0x453);
                for (var i = 0; i < 3; i++)
                {
                    AddDisplayItem(vendor, stats, Stack(new GreaterStrengthPotion(), 10), 140);
                }
                for (var i = 0; i < 3; i++)
                {
                    AddDisplayItem(vendor, stats, Stack(new GreaterAgilityPotion(), 10), 140);
                }

                var offense = CreateDisplayContainer<Pouch>(vendor, "Explosion/Poison Pouch", 0x21);
                for (var i = 0; i < 3; i++)
                {
                    AddDisplayItem(vendor, offense, Stack(new GreaterExplosionPotion(), 10), 260);
                }
                for (var i = 0; i < 3; i++)
                {
                    AddDisplayItem(vendor, offense, Stack(new DeadlyPoisonPotion(), 10), 190);
                }
                break;

            case 2: // Keg Master
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.HealGreater, Held = 100 }, 750);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.HealGreater, Held = 100 }, 750);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.ExplosionGreater, Held = 100 }, 900);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.ExplosionGreater, Held = 100 }, 900);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.RefreshTotal, Held = 100 }, 650);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.RefreshTotal, Held = 100 }, 650);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.CureGreater, Held = 100 }, 800);
                SellLoose(vendor, new PotionKeg { Type = PotionEffect.PoisonDeadly, Held = 100 }, 950);
                break;

            default: // Specialty - Archmage
                // BaseWand : BaseWeapon (a wand is mechanically a bashing
                // weapon that also casts), so it carries the same
                // WeaponQuality this shop's every other crafted piece
                // does - "high-charge" is a flavor description, not a
                // reason to leave it at Regular quality.
                AddGMItem(vendor, new LightningWand { Identified = true }, "a lightning wand", 850);
                AddGMItem(vendor, new FireballWand { Identified = true }, "a fireball wand", 900);
                AddGMItem(vendor, new GreaterHealWand { Identified = true }, "a greater healing wand", 950);
                SellLoose(vendor, new Spellbook(ulong.MaxValue), 5500);
                SellLoose(vendor, new Spellbook(ulong.MaxValue), 5500);

                SellBundle(
                    vendor, new Pouch(), 0x48D, 1400, "High-Circle Scroll Pouch",
                    Stack(new PolymorphScroll(), 5), Stack(new EarthquakeScroll(), 5),
                    Stack(new EnergyVortexScroll(), 3), Stack(new SummonDaemonScroll(), 3)
                );
                break;
        }
    }

    // Reagent NPC base (a conceptual per-piece T2A shop price) plus 15%,
    // for a 100-count stack - see StockMageApothecary slot 0.
    private const int ReagentNpcBasePerPiece = 4;

    // SP-029: takes a factory delegate rather than a `new()`-constrained
    // generic - every reagent class here declares its own public
    // constructor as `(int amount = 1)`, which satisfies ordinary `new
    // BlackPearl()` calls but NOT C#'s `new()` generic constraint (that
    // constraint only matches a truly zero-parameter constructor
    // declaration, not one made zero-argument-callable by a default
    // value) - confirmed by the compiler rejecting every reagent type
    // here with CS0310 on the first build attempt.
    private static void AddReagentPouch(PlayerVendor vendor, string name, Func<Item> factory)
    {
        var stackCount = Utility.RandomMinMax(3, 4);
        var per100Price = (int)Math.Round(100 * ReagentNpcBasePerPiece * 1.15);
        var pouch = CreatePackagedSubcontainer<Pouch>(vendor, name, 0x48E, per100Price * stackCount);

        for (var i = 0; i < stackCount; i++)
        {
            pouch.DropItem(Stack(factory(), 100));
        }
    }

    // ==== ScribeLibrary ======================================================

    // SP-030: the "Scribe Sanctum" rotation pool - 3-4 of these are picked
    // fresh every time this vendor's slot 0 stocks (initial spawn AND
    // every restock), alongside the always-guaranteed travel scrolls
    // above. Deliberately mid/high circle only (3rd through 7th) - this
    // slot's job is "useful combat scrolls," not the shop's own guaranteed
    // staples.
    private static readonly (Func<Item> Factory, string Name, int Price)[] ScribeCombatScrollPool =
    {
        (() => new PoisonScroll(), "a poison scroll", 2200),
        (() => new FireballScroll(), "a fireball scroll", 2200),
        (() => new TeleportScroll(), "a teleport scroll", 2000),
        (() => new WallOfStoneScroll(), "a wall of stone scroll", 2100),
        (() => new ArchProtectionScroll(), "an arch protection scroll", 2800),
        (() => new InvisibilityScroll(), "an invisibility scroll", 4200),
        (() => new EnergyBoltScroll(), "an energy bolt scroll", 4300),
        (() => new FlamestrikeScroll(), "a flamestrike scroll", 5100)
    };

    private static void StockScribeLibrary(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Travel & Combat Scrolls - guaranteed travel scrolls, SP-030: 3-4 random combat
                    // scrolls per restock cycle from a broader mid/high circle pool (see
                    // ScribeCombatScrollPool below)
                for (var i = 0; i < 3; i++)
                {
                    SellLoose(vendor, Stack(new RecallScroll(), 25), 560);
                }
                for (var i = 0; i < 3; i++)
                {
                    SellLoose(vendor, Stack(new RecallScroll(), 50), 1100);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Stack(new RecallScroll(), 100), 2150);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Stack(new GateTravelScroll(), 50), 2350);
                }

                foreach (var pick in ScribeCombatScrollPool.RandomSample(Utility.RandomMinMax(3, 4)))
                {
                    SellLoose(vendor, Stack(Named(pick.Factory(), pick.Name), 100), pick.Price);
                }
                break;

            case 1: // Librarian & Scribe
                var runebookCount = Utility.RandomMinMax(3, 4);
                for (var i = 0; i < runebookCount; i++)
                {
                    SellLoose(vendor, Named(new Runebook(), "an empty runebook"), 500);
                }

                var spellbookCount = Utility.RandomMinMax(2, 3);
                for (var i = 0; i < spellbookCount; i++)
                {
                    SellLoose(vendor, new Spellbook(ulong.MaxValue), 5500);
                }

                for (var i = 0; i < 3; i++)
                {
                    SellLoose(vendor, Stack(new BlankScroll(), 500), 2500);
                }
                break;

            case 2: // The Navigator - pre-marked 16-rune runebooks
                var cities = Named(new Runebook(16), "Britannia Cities & Banks Runebook");
                cities.Description = "Pre-marked with recall runes to every major city bank.";
                SellLoose(vendor, cities, 3500);

                var dungeons = Named(new Runebook(16), "Dungeon Entrances Runebook");
                dungeons.Description = "Pre-marked with recall runes to every classic dungeon entrance.";
                SellLoose(vendor, dungeons, 4000);

                var shrines = Named(new Runebook(16), "Shrines & Moongates Runebook");
                shrines.Description = "Pre-marked with recall runes to every virtue shrine and public moongate.";
                SellLoose(vendor, shrines, 3000);
                break;

            default: // Specialty - Ancient Tomes & Rares
                var map = vendor.Map is { } m && m != Map.Internal ? m : Map.Felucca;
                SellLoose(vendor, new TreasureMap(4, map), 550);
                SellLoose(vendor, new TreasureMap(5, map), 750);

                var rareBook = new Spellbook(ulong.MaxValue) { Hue = Utility.RandomList(0x481, 0x489, 0x497) };
                SellLoose(vendor, Named(rareBook, "an ancient spellbook"), 6500);

                SellLoose(vendor, Named(new BlankScroll(), "an ancient parchment curio"), 120);
                break;
        }
    }

    // ==== RawResources =======================================================

    private static void StockRawResources(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Lumberjack
                SellLoose(vendor, Stack(new Log(), 125), 250);
                SellLoose(vendor, Stack(new Board(), 125), 375);
                SellLoose(vendor, Stack(new Shaft(), 250), 500);
                break;

            case 1: // Smelter & Miner
                // SP-030: economy rebalance - Iron Ingot 7-9gp/unit (150 stack = 1,050-1,350gp).
                SellLoose(vendor, Stack(new IronIngot(), 150), Utility.RandomMinMax(1050, 1350));
                SellLoose(vendor, Stack(new Granite(), 100), 300);
                // "3x GM Pickaxes" per the ticket - Pickaxe : BaseAxe :
                // BaseWeapon (a mining tool that's mechanically an axe),
                // so "GM" means the same WeaponQuality.Exceptional every
                // other GM piece in this file gets.
                for (var i = 0; i < 3; i++)
                {
                    AddGMItem(vendor, new Pickaxe(), "a pickaxe", 35);
                }
                break;

            case 2: // Tanner & Weaver
                SellLoose(vendor, Stack(new Cloth(), 100), 300);
                SellLoose(vendor, Stack(new Leather(), 100), 400);
                SellLoose(vendor, Stack(new SpinedLeather(), 100), 500);
                break;

            default: // Specialty - Colored Ingot Organizer (SP-030: DISPLAY container, unit-scale pricing)
                var organizer = CreateDisplayContainer<Pouch>(vendor, "Colored Ingot Organizer", 0x453);
                AddColoredIngotStack(vendor, organizer, () => new DullCopperIngot(), 900, 1250);
                AddColoredIngotStack(vendor, organizer, () => new DullCopperIngot(), 900, 1250);
                AddColoredIngotStack(vendor, organizer, () => new ShadowIronIngot(), 900, 1250);
                AddColoredIngotStack(vendor, organizer, () => new ShadowIronIngot(), 900, 1250);
                AddColoredIngotStack(vendor, organizer, () => new CopperIngot(), 1750, 3000);
                AddColoredIngotStack(vendor, organizer, () => new BronzeIngot(), 1750, 3000);
                AddColoredIngotStack(vendor, organizer, () => new GoldIngot(), 1750, 3000);
                AddColoredIngotStack(vendor, organizer, () => new AgapiteIngot(), 4000, 6000);
                AddColoredIngotStack(vendor, organizer, () => new VeriteIngot(), 4000, 6000);
                AddColoredIngotStack(vendor, organizer, () => new ValoriteIngot(), 10000, 12500);
                break;
        }
    }

    // SP-030: economy rebalance - each 50-count colored-ingot stack is
    // individually priced within its own color's per-unit band (a 50
    // stack lands at the band's 50x-unit-price range, e.g. Dull Copper/
    // Shadow Iron 18-25gp/unit -> 900-1,250gp for the stack) rather than
    // one flat total for the whole pouch, since the pouch is now a
    // DISPLAY container (see CreateDisplayContainer) a player can buy
    // individual stacks out of. Same factory-delegate reasoning as
    // AddReagentPouch above - the ingot classes' own constructors are
    // `(int amount = 1)`, not a true zero-parameter signature the `new()`
    // generic constraint requires.
    private static void AddColoredIngotStack(
        PlayerVendor vendor, Container organizer, Func<Item> factory, int minPrice, int maxPrice
    ) => AddDisplayItem(vendor, organizer, Stack(factory(), 50), Utility.RandomMinMax(minPrice, maxPrice));

    // ==== TailorFletcher ======================================================

    private const string MasterBowyerName = "a Master Bowyer";
    private const string MasterTailorName = "a Master Tailor";

    private static void StockTailorFletcher(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Bowyer / Fletcher
                for (var i = 0; i < 3; i++)
                {
                    AddGMItem(vendor, new Bow(), "a bow", 190, MasterBowyerName);
                }
                for (var i = 0; i < 3; i++)
                {
                    AddGMItem(vendor, new HeavyCrossbow(), "a heavy crossbow", 260, MasterBowyerName);
                }
                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new Crossbow(), "a crossbow", 200, MasterBowyerName);
                }
                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new CompositeBow(), "a composite bow", 210, MasterBowyerName);
                }

                SellLoose(vendor, Stack(new Arrow(), 500), 400);
                SellLoose(vendor, Stack(new Bolt(), 500), 450);
                break;

            case 1: // Leather Specialist
                for (var i = 0; i < 3; i++)
                {
                    PackAndPriceSuit(
                        vendor, "Full GM Leather Suit (exceptional)", 0x3A6, 800,
                        Exceptional(new LeatherChest(), "a leather chest", MasterTailorName),
                        Exceptional(new LeatherArms(), "a pair of leather arms", MasterTailorName),
                        Exceptional(new LeatherGloves(), "a pair of leather gloves", MasterTailorName),
                        Exceptional(new LeatherGorget(), "a leather gorget", MasterTailorName),
                        Exceptional(new LeatherLegs(), "a pair of leather legs", MasterTailorName)
                    );
                }

                for (var i = 0; i < 3; i++)
                {
                    PackAndPriceSuit(
                        vendor, "Full GM Studded Suit (exceptional)", 0x455, 1000,
                        Exceptional(new StuddedChest(), "a studded chest", MasterTailorName),
                        Exceptional(new StuddedArms(), "a pair of studded arms", MasterTailorName),
                        Exceptional(new StuddedGloves(), "a pair of studded gloves", MasterTailorName),
                        Exceptional(new StuddedGorget(), "a studded gorget", MasterTailorName),
                        Exceptional(new StuddedLegs(), "a pair of studded legs", MasterTailorName)
                    );
                }

                for (var i = 0; i < 2; i++)
                {
                    PackAndPriceSuit(
                        vendor, "Full GM Female Leather Set (exceptional)", 0x489, 850,
                        Exceptional(new FemaleLeatherChest(), "a leather bustier", MasterTailorName),
                        Exceptional(new LeatherArms(), "a pair of leather arms", MasterTailorName),
                        Exceptional(new LeatherGloves(), "a pair of leather gloves", MasterTailorName),
                        Exceptional(new LeatherSkirt(), "a leather skirt", MasterTailorName)
                    );
                }
                break;

            case 2: // Clothier & Dyer
                SellLoose(vendor, Named(new FancyShirt(), "a fancy shirt"));
                SellLoose(vendor, Named(new Doublet(), "a doublet"));
                SellLoose(vendor, Named(new Cloak(), "a cloak"));
                SellLoose(vendor, Named(new Skirt(), "a layered skirt"));
                SellLoose(vendor, Named(new Robe(), "a full robe"));
                SellLoose(vendor, Named(new Boots { Hue = Utility.RandomList(0x489, 0x21, 0x59) }, "dyed footwear"));
                break;

            default: // Specialty - Rare Hued Garb
                PackAndPriceSuit(
                    vendor, "Pure Black Clothing Set", 0x0, 1600,
                    Named(new Robe { Hue = 0x0 }, "a robe"),
                    Named(new Cloak { Hue = 0x0 }, "a cloak"),
                    Named(new Boots { Hue = 0x0 }, "a pair of boots")
                );

                PackAndPriceSuit(
                    vendor, "Ice White Clothing Set", 0x481, 1600,
                    Named(new Robe { Hue = 0x481 }, "a robe"),
                    Named(new Cloak { Hue = 0x481 }, "a cloak"),
                    Named(new Boots { Hue = 0x481 }, "a pair of boots")
                );

                // Special Dye Tub - restocked rarely (a plain DyeTub is
                // this shop's everyday stock over on slot 2; a full black
                // dye tub is deliberately the rare exception here).
                if (Utility.RandomDouble() < 0.25)
                {
                    SellLoose(vendor, new BlackDyeTub());
                }
                break;
        }
    }

    // ==== TinkerCarpenter =====================================================

    // SP-030: the "Carpenter/Woodworker" rotation pool - 4-6 of these are
    // picked fresh every time slot 0 stocks (initial spawn AND every
    // restock). Sold loose (not a display container - task 1's container-
    // display examples don't cover furniture), same as the fixed list
    // this replaces.
    private static readonly (Func<Item> Factory, string Name, int Price)[] CarpenterFurniturePool =
    {
        (() => new WoodenChest(), "a wooden chest", 180),
        (() => new MetalBox(), "a metal chest", 220),
        (() => new Keg(), "a keg", 90),
        (() => new WoodenChair(), "a wooden chair", 40),
        (() => new LargeTable(), "a wooden table", 90),
        (() => new Armoire(), "an armoire", 260),
        (() => new Throne(), "a throne", 350),
        (() => new WritingTable(), "a writing desk", 110),
        (() => new FullBookcase(), "a bookshelf", 180),
        (() => new FootStool(), "a footstool", 35),
        (() => new WaterTroughEastDeed(), "a water trough deed", 800),
        (() => new SpinningWheelEastDeed(), "a spinning wheel deed", 1200)
    };

    // SP-030: the "Tinkerer/Hardware" rotation pool - 4-6 of these go into
    // slot 1's Tool Organizer DISPLAY container fresh every time it
    // stocks. Hatchet is the one entry that's a real BaseWeapon (BaseAxe),
    // so it routes through AddGMDisplayItem for the same Quality =
    // Exceptional treatment every other GM-sold weapon in this file gets;
    // everything else here is a plain tool/gadget.
    private const string MasterTinkerName = "a Master Tinkerer";

    private static readonly (Func<Item> Factory, string Name, int Price, bool IsWeapon)[] TinkerGadgetPool =
    {
        (() => new Clock(), "a clock", 120, false),
        (() => new Sextant(), "a sextant", 65, false),
        (() => new Spyglass(), "a spyglass", 90, false),
        (() => new Scissors(), "a pair of scissors", 15, false),
        (() => new KeyRing(), "a keyring", 20, false),
        (() => new HeatingStand(), "a heating stand", 45, false),
        (() => new Shovel(), "a shovel", 25, false),
        (() => new Hatchet(), "a hatchet", 30, true)
    };

    private static void StockTinkerCarpenter(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Master Carpenter - SP-030: 4-6 random furniture/addon items per restock cycle
                foreach (var pick in CarpenterFurniturePool.RandomSample(Utility.RandomMinMax(4, 6)))
                {
                    SellLoose(vendor, Named(pick.Factory(), pick.Name), pick.Price);
                }
                break;

            case 1: // Master Tinkerer - crafting-tool staples loose, SP-030: 4-6 random gadgets in a
                    // DISPLAY Tool Organizer per restock cycle
                SellLoose(vendor, Stack(new Lockpick(), 100), 90);
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, new TinkerTools(), 30);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, new SewingKit(), 15);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, new SmithHammer(), 25);
                }

                var toolOrganizer = CreateDisplayContainer<WoodenBox>(vendor, "Tool Organizer", 0);
                foreach (var pick in TinkerGadgetPool.RandomSample(Utility.RandomMinMax(4, 6)))
                {
                    if (pick.IsWeapon)
                    {
                        AddGMDisplayItem(vendor, toolOrganizer, pick.Factory(), pick.Name, pick.Price, MasterTinkerName);
                    }
                    else
                    {
                        AddDisplayItem(vendor, toolOrganizer, Named(pick.Factory(), pick.Name), pick.Price);
                    }
                }
                break;

            case 2: // Addon Crafters - forge, anvil, spinning wheel
                SellLoose(vendor, Named(new SmallForgeDeed(), "a small forge deed"), 2500);
                SellLoose(vendor, Named(new AnvilEastDeed(), "an anvil deed"), 2500);
                SellLoose(vendor, Named(new SpinningWheelEastDeed(), "a spinning wheel deed"), 1200);
                SellLoose(vendor, Named(new LoomEastDeed(), "a loom deed"), 1200);
                break;

            default: // Addon Crafters - training/utility deeds
                SellLoose(vendor, Named(new TrainingDummyEastDeed(), "a training dummy deed"), 1500);
                SellLoose(vendor, Named(new PickpocketDipEastDeed(), "a pickpocket dip deed"), 1500);
                SellLoose(vendor, Named(new WaterTroughEastDeed(), "a water trough deed"), 800);
                break;
        }
    }

    // ==== FisherCurioBaker ====================================================

    private static void StockFisherCurioBaker(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Deep Sea Fisher
                SellLoose(vendor, new MessageInABottle(), 150);
                SellLoose(vendor, new MessageInABottle(), 150);
                SellLoose(vendor, new SpecialFishingNet(), 300);
                SellLoose(vendor, Stack(new RawFishSteak(), 200), 600);
                SellLoose(vendor, Stack(new FishSteak(), 200), 700);
                SellLoose(vendor, Named(new BigFish(), "a strange, oversized fish"), 450);
                break;

            case 1: // Master Baker & Tavern Cook
                var ribBasket = CreatePackagedSubcontainer<Basket>(vendor, "Basket of Cooked Ribs (50)", 0x455, 900);
                for (var i = 0; i < 50; i++)
                {
                    ribBasket.DropItem(new Ribs());
                }

                var pieBasket = CreatePackagedSubcontainer<Basket>(vendor, "Basket of Meat Pies (50)", 0x489, 950);
                for (var i = 0; i < 50; i++)
                {
                    pieBasket.DropItem(new MeatPie());
                }

                SellLoose(vendor, new RoastPig(), 200);
                SellLoose(vendor, new CheeseWheel(), 60);
                SellLoose(vendor, new Jug(BeverageType.Ale), 40);
                SellLoose(vendor, new Jug(BeverageType.Wine), 55);
                break;

            case 2: // Treasure Hunter
                var map = vendor.Map is { } m && m != Map.Internal ? m : Map.Felucca;
                SellLoose(vendor, new TreasureMap(1, map), 120);
                SellLoose(vendor, new TreasureMap(2, map), 220);
                SellLoose(vendor, new TreasureMap(3, map), 350);
                SellLoose(vendor, new TreasureMap(4, map), 550);
                SellLoose(vendor, Stack(new Lockpick(), 50), 45);
                SellLoose(vendor, Named(new MetalChest(), "a recovered sunken chest"), 400);
                break;

            default: // Curio Antiquarian
                SellLoose(vendor, new Globe());
                SellLoose(vendor, new SmallUrn());
                SellLoose(vendor, new Vase());
                SellLoose(vendor, new LargeVase());
                SellLoose(vendor, new StatueSouth());
                SellLoose(vendor, new BustSouth());
                SellLoose(vendor, new Candelabra());
                SellLoose(vendor, Named(new Item(0x1725), "a fruit basket"), 35);
                SellLoose(vendor, Named(new Item(0x1006), "an open ceramic jar"), 25);
                SellLoose(vendor, Named(new Item(0x1367), "a smuggled lantern"), 80);
                break;
        }
    }
}
