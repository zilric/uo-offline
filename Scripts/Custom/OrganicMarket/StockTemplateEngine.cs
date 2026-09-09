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
using Server.Logging;
using Server.Mobiles;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class StockTemplateEngine
{
    // SP-032: gated behind VerboseConfig.VendorStock - see StockVendor and
    // SellLoose below for what actually logs.
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(StockTemplateEngine));

    // ---- Price Dictionary ----------------------------------------------
    // Baseline gold prices for every loose good this engine generates that
    // isn't already priced inline at its own call site (a themed
    // subcontainer's price is always passed explicitly - this dictionary
    // only covers items SellLoose falls back to for a bare Type lookup).
    //
    // SP-036: internal rather than private - PlayerShopPatronageManager
    // reuses this as a secondary appraisal baseline (behind the real
    // IShopSellInfo/SBInfo pricing oracle) for common resources/tools a
    // real player vendor might also be listing.
    // SP-046: Master Pricing Model - these five raw-material baselines
    // (Iron Ingot, Leather, Board/Log, Cloth, Feather) are the ticket's
    // own reference points: a plain crafted piece made from one of these
    // materials prices at roughly 2x its raw baseline, and an
    // Exceptional/GM-quality piece steps that up another 1.5x on top
    // (e.g. Iron Ingot 8g raw -> 16g crafted -> 24g Exceptional/GM).
    // That multiplier governs the numbers below and this file's own
    // RawResources organizer stock; it is NOT retroactively applied to
    // the hundreds of already hand-tuned weapon/armor/furniture prices
    // elsewhere in this file - those already reflect their own
    // complexity and rarity independent of a flat material formula.
    internal static readonly Dictionary<Type, int> BasePrices = new()
    {
        [typeof(BlackPearl)]   = 3,
        [typeof(Bloodmoss)]    = 3,
        [typeof(Garlic)]       = 3,
        [typeof(Ginseng)]      = 3,
        [typeof(MandrakeRoot)] = 3,
        [typeof(Nightshade)]   = 3,
        [typeof(SpidersSilk)]  = 3,
        [typeof(SulfurousAsh)] = 3,

        [typeof(IronIngot)] = 8, // SP-046: master price list raw baseline
        [typeof(Board)]     = 4, // SP-046: master price list raw baseline (Wood)
        [typeof(Log)]       = 4, // SP-046: master price list raw baseline (Wood)
        [typeof(Leather)]   = 6, // SP-046: master price list raw baseline
        [typeof(Cloth)]     = 3, // SP-046: master price list raw baseline (unchanged)
        [typeof(Feather)]   = 4, // SP-046: master price list raw baseline
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

            if (VerboseConfig.VendorStock)
            {
                logger.Information("SellLoose: priced {Item} at {Price}gp for {Vendor}", item.GetType().Name, vi.Price, vendor.Serial);
            }
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

    // SP-047: nested counterpart of CreatePackagedSubcontainer - drops
    // the new priced sub-bundle into an arbitrary already-registered
    // parent container instead of the vendor's own root backpack, for a
    // priced bundle that itself lives inside a browse-and-buy-one display
    // organizer (e.g. one of the Armorer's suit Bags, nested inside its
    // "Armor" primary chest).
    private static TContainer CreateNestedPackagedSubcontainer<TContainer>(
        PlayerVendor vendor, Container parent, string name, int hue, int price
    ) where TContainer : Container, new()
    {
        var container = new TContainer { Hue = hue, Name = name };
        parent.DropItem(container);

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

    // SP-047: nested counterpart of CreateDisplayContainer - drops the
    // new display (not-for-sale) container into an arbitrary already-
    // registered parent container instead of the vendor's own root
    // backpack, for an organizer that lives inside another organizer
    // (e.g. the Armorer's own "Helmets" browse-and-buy-one bag, nested
    // inside its "Armor" primary chest).
    private static TContainer CreateNestedDisplayContainer<TContainer>(
        PlayerVendor vendor, Container parent, string name, int hue
    ) where TContainer : Container, new()
    {
        var container = new TContainer { Hue = hue, Name = name };
        parent.DropItem(container);

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

    // SP-047: builds one item, rolls its own independent Exceptional
    // chance, and individually prices it inside an already-registered
    // display container (see CreateDisplayContainer/
    // CreateNestedDisplayContainer) - the per-item quality roll the
    // Weaponsmith's own weapon/shield boxes and the Armorer's loose
    // Helmets bag both use, as opposed to AddArmorSuitBag's per-bundle
    // roll below. Amount is force-clamped to 1 as an explicit guardrail
    // (weapons/armor/shields aren't Stackable by default anyway, but the
    // ticket asks for this enforced rather than assumed).
    private static void AddQualityRolledDisplayItem<T>(
        PlayerVendor vendor, Container container, Func<T> factory, string name,
        int basePrice, int gmPrice, double exceptionalChance, string crafterName
    ) where T : Item
    {
        var item = factory();
        item.Amount = 1;

        if (Utility.RandomDouble() < exceptionalChance)
        {
            AddGMDisplayItem(vendor, container, item, name, gmPrice, crafterName);
        }
        else
        {
            AddDisplayItem(vendor, container, Named(item, name), basePrice);
        }
    }

    // SP-047: the Armorer's own per-bundle quality roll - every piece in
    // one suit Bag (plus the suit's own price/name) is uniformly either
    // Exceptional or standard together, unlike AddQualityRolledDisplayItem's
    // independent per-item roll. Builds each piece via its own factory so
    // a fresh, distinct instance goes into every suit (never a shared
    // reference), clamps Amount to 1 the same way, and drops the finished
    // suit into an already-registered display organizer (the Armorer's
    // own "Armor" chest).
    private static void AddArmorSuitBag(
        PlayerVendor vendor, Container armorChest, string standardName, int standardPrice,
        string gmName, int gmPrice, double exceptionalChance, string crafterName,
        params (Func<Item> Factory, string Name)[] pieces
    )
    {
        var isExceptional = Utility.RandomDouble() < exceptionalChance;
        var name = isExceptional ? gmName : standardName;
        var price = isExceptional ? gmPrice : standardPrice;

        var bag = CreateNestedPackagedSubcontainer<Bag>(vendor, armorChest, name, 0, price);

        foreach (var (factory, pieceName) in pieces)
        {
            var piece = factory();
            piece.Amount = 1;

            if (isExceptional)
            {
                ApplyExceptional(piece, pieceName, crafterName);
            }
            else
            {
                Named(piece, pieceName);
            }

            bag.DropItem(piece);
        }
    }

    // SP-046: Stack Clamp guardrail - every commodity pile this engine
    // builds routes through here, so this is the one place that needs to
    // enforce "non-stackable items never carry Amount > 1." A template
    // that wants N of an unstackable item (a weapon, a tool, a deed) has
    // to build N distinct instances via a loop instead - see, e.g.,
    // StockTailorFletcher's own GM bow/crossbow loops, which already do
    // exactly that.
    private static Item Stack(Item item, int amount)
    {
        item.Amount = item.Stackable ? amount : 1;
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

    // SP-049: universal single-vendor 50/50 roll - a 1-vendor shop's
    // single vendor lands on effective slot 0 or 1 with equal chance,
    // for EVERY archetype (not just MageApothecary, which used to be
    // the only one with this rule). A 2+-vendor shop keeps the existing
    // sequential slot->role mapping (0/1/2/3+) unchanged. vendorCount is
    // the shop's REAL total vendor count - never padded, see
    // OrganicMarketSpawner.SpawnVendors' own two-pass comment.
    private static int DetermineEffectiveSlot(int vendorIndex, int vendorCount) =>
        vendorCount <= 1 ? (Utility.RandomBool() ? 0 : 1) : vendorIndex % 4;

    // Returns the effective slot this vendor actually got stocked as, so
    // the caller can feed the SAME value into ApplyVendorTheme - title/
    // apparel has to match whatever tier was actually rolled here, and
    // the only way to guarantee that (rather than each method rolling
    // its own independent coin flip) is for one roll to drive both.
    public static int StockVendor(PlayerVendor vendor, MarketArchetype archetype, int vendorIndex, int vendorCount)
    {
        var slot = DetermineEffectiveSlot(vendorIndex, vendorCount);

        if (vendor?.Backpack == null)
        {
            return slot;
        }

        if (VerboseConfig.VendorStock)
        {
            logger.Information("StockVendor: populating {Vendor} as {Archetype} slot {Slot}", vendor.Serial, archetype, slot);
        }

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
        return slot;
    }

    // ==== BlacksmithArmory ==================================================

    private const string MasterBlacksmithName = "a Master Blacksmith";
    private const string MasterArmorerName = "a Master Armorer";

    // SP-047: master pricing matrix - Weaponsmith pools. Each entry is
    // (factory, single-click-safe name, standard/base price, Exceptional/
    // GM price). Dagger deliberately appears in both Swordsmanship and
    // Fencing at the same price - the ticket's own pool lists both list it.
    private static readonly (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] SwordsmanshipPool =
    {
        (() => new Dagger(), "a dagger", 50, 75),
        (() => new Cutlass(), "a cutlass", 130, 195),
        (() => new Katana(), "a katana", 130, 195),
        (() => new Scimitar(), "a scimitar", 160, 240),
        (() => new Broadsword(), "a broadsword", 160, 240),
        (() => new Longsword(), "a longsword", 190, 285),
        (() => new VikingSword(), "a viking sword", 225, 340),
        (() => new Axe(), "an axe", 225, 340),
        (() => new BattleAxe(), "a battle axe", 225, 340),
        (() => new DoubleAxe(), "a double axe", 190, 285),
        (() => new ExecutionersAxe(), "an executioner's axe", 225, 340),
        (() => new TwoHandedAxe(), "a two-handed axe", 255, 385),
        (() => new Halberd(), "a halberd", 320, 480),
        (() => new Bardiche(), "a bardiche", 290, 435)
    };

    private static readonly (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] MaceFightingPool =
    {
        (() => new Mace(), "a mace", 95, 145),
        (() => new Maul(), "a maul", 160, 240),
        (() => new WarMace(), "a war mace", 225, 340),
        (() => new WarHammer(), "a war hammer", 255, 385),
        (() => new HammerPick(), "a hammer pick", 255, 385),
        (() => new QuarterStaff(), "a quarterstaff", 50, 75),
        (() => new BlackStaff(), "a black staff", 70, 105),
        (() => new GnarledStaff(), "a gnarled staff", 55, 85)
    };

    private static readonly (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] FencingPool =
    {
        (() => new Dagger(), "a dagger", 50, 75),
        (() => new Kryss(), "a kryss", 130, 195),
        (() => new WarFork(), "a war fork", 190, 285),
        (() => new ShortSpear(), "a short spear", 95, 145),
        (() => new Spear(), "a spear", 190, 285)
    };

    // SP-047: "Tear Kite Shield" from the ticket's own pool has no
    // matching class anywhere in ModernUO (confirmed against the full
    // Items/Shields/ catalog) - WoodenKiteShield is substituted at the
    // same price tier as the real sibling to MetalKiteShield in the
    // kite-shield family.
    private static readonly (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] ShieldsPool =
    {
        (() => new Buckler(), "a buckler", 160, 240),
        (() => new BronzeShield(), "a bronze shield", 190, 285),
        (() => new MetalShield(), "a metal shield", 225, 340),
        (() => new MetalKiteShield(), "a metal kite shield", 255, 385),
        (() => new WoodenKiteShield(), "a wooden kite shield", 255, 385),
        (() => new HeaterShield(), "a heater shield", 290, 435)
    };

    // SP-047: 30% Exceptional (GM quality) roll per weapon/shield item -
    // the other 70% stays standard quality at the pool's own base price.
    private const double WeaponsmithExceptionalChance = 0.30;

    // SP-047: master pricing matrix - Armorer full-suit prices (standard/
    // Exceptional pairs) and the Helmets bag's own uniform helm price.
    private const int FullPlatemailSuitPrice = 1600;
    private const int FullGMPlatemailSuitPrice = 2400;
    private const int FullChainmailSuitPrice = 770;
    private const int FullGMChainmailSuitPrice = 1155;
    private const int FullRingmailSuitPrice = 1125;
    private const int FullGMRingmailSuitPrice = 1690;
    private const int HelmetBasePrice = 240;
    private const int HelmetGMPrice = 360;

    // SP-047: 35% Exceptional roll, applied per-bundle for a full suit
    // (every piece in the bag matches) and per-item for a loose helm.
    private const double ArmorerExceptionalChance = 0.35;

    // The Full Plate Suit's own random helm - PlateHelm/CloseHelm/
    // Bascinet/NorseHelm only, per the ticket's own wording; "Helmet" is
    // reserved for the standalone Helmets bag pool below.
    private static readonly (Func<Item> Factory, string Name)[] SuitHelmPool =
    {
        (() => new PlateHelm(), "a platemail helm"),
        (() => new CloseHelm(), "a close helm"),
        (() => new Bascinet(), "a bascinet"),
        (() => new NorseHelm(), "a norse helm")
    };

    private static readonly (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] HelmetPool =
    {
        (() => new PlateHelm(), "a platemail helm", HelmetBasePrice, HelmetGMPrice),
        (() => new CloseHelm(), "a close helm", HelmetBasePrice, HelmetGMPrice),
        (() => new Bascinet(), "a bascinet", HelmetBasePrice, HelmetGMPrice),
        (() => new NorseHelm(), "a norse helm", HelmetBasePrice, HelmetGMPrice),
        (() => new Helmet(), "a helmet", HelmetBasePrice, HelmetGMPrice)
    };

    // SP-047: fills one of the Weaponsmith's 4 labeled sub-containers -
    // picks minTypes-maxTypes distinct entries from the pool (never
    // repeating a type within one container) and spawns 4-6 individually
    // instantiated, individually quality-rolled units of each.
    private static void StockWeaponBox(
        PlayerVendor vendor, Container box,
        (Func<Item> Factory, string Name, int BasePrice, int GMPrice)[] pool,
        int minTypes, int maxTypes
    )
    {
        foreach (var entry in pool.RandomSample(Utility.RandomMinMax(minTypes, maxTypes)))
        {
            var unitCount = Utility.RandomMinMax(4, 6);
            for (var i = 0; i < unitCount; i++)
            {
                AddQualityRolledDisplayItem(
                    vendor, box, entry.Factory, entry.Name, entry.BasePrice, entry.GMPrice,
                    WeaponsmithExceptionalChance, MasterBlacksmithName
                );
            }
        }
    }

    private static void StockBlacksmithArmory(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // GM Weaponsmith - SP-047: 4 labeled sub-containers (browse & buy one),
                    // 30% per-item Exceptional roll, no stray loose weapons in the root pack.
                var swords = CreateDisplayContainer<WoodenBox>(vendor, "Swordsmanship Weapons", 0);
                StockWeaponBox(vendor, swords, SwordsmanshipPool, 4, 6);

                var maces = CreateDisplayContainer<WoodenBox>(vendor, "Mace Fighting Weapons", 0);
                StockWeaponBox(vendor, maces, MaceFightingPool, 4, 5);

                var fencing = CreateDisplayContainer<WoodenBox>(vendor, "Fencing Weapons", 0);
                StockWeaponBox(vendor, fencing, FencingPool, 3, 4);

                var shields = CreateDisplayContainer<WoodenBox>(vendor, "Shields", 0);
                StockWeaponBox(vendor, shields, ShieldsPool, 4, 4);
                break;

            case 1: // GM Armorer - SP-047: one primary "Armor" chest holding suit Bags (plate/
                    // chain/ring, each a uniformly-rolled bundle) plus a nested Helmets bag of
                    // individually quality-rolled loose helms.
                var armorChest = CreateDisplayContainer<WoodenBox>(vendor, "Armor", 0);

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    var (suitHelmFactory, suitHelmName) = SuitHelmPool[Utility.Random(SuitHelmPool.Length)];
                    AddArmorSuitBag(
                        vendor, armorChest, "Full Platemail Suit", FullPlatemailSuitPrice,
                        "Full GM Platemail Suit", FullGMPlatemailSuitPrice, ArmorerExceptionalChance, MasterArmorerName,
                        (() => new PlateGorget(), "a platemail gorget"),
                        (() => new PlateGloves(), "a pair of platemail gloves"),
                        (() => new PlateArms(), "a pair of platemail arms"),
                        (() => new PlateLegs(), "a pair of platemail legs"),
                        (() => new PlateChest(), "a platemail chest"),
                        (suitHelmFactory, suitHelmName)
                    );
                }

                for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
                {
                    AddArmorSuitBag(
                        vendor, armorChest, "Full Chainmail Suit", FullChainmailSuitPrice,
                        "Full GM Chainmail Suit", FullGMChainmailSuitPrice, ArmorerExceptionalChance, MasterArmorerName,
                        (() => new ChainCoif(), "a chain coif"),
                        (() => new ChainLegs(), "a pair of chainmail leggings"),
                        (() => new ChainChest(), "a chainmail tunic")
                    );
                }

                // SP-047: Ringmail Cap from the ticket's own piece list has
                // no matching class - classic UO ring mail suits have no
                // head slot at all (only Chest/Arms/Gloves/Legs exist under
                // Items/Armor/Ring/) - so the bag holds the 4 real pieces.
                for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
                {
                    AddArmorSuitBag(
                        vendor, armorChest, "Full Ringmail Suit", FullRingmailSuitPrice,
                        "Full GM Ringmail Suit", FullGMRingmailSuitPrice, ArmorerExceptionalChance, MasterArmorerName,
                        (() => new RingmailGloves(), "a pair of ringmail gloves"),
                        (() => new RingmailArms(), "a pair of ringmail sleeves"),
                        (() => new RingmailLegs(), "a pair of ringmail leggings"),
                        (() => new RingmailChest(), "a ringmail tunic")
                    );
                }

                var helmets = CreateNestedDisplayContainer<Bag>(vendor, armorChest, "Helmets", 0);
                foreach (var entry in HelmetPool.RandomSample(Utility.RandomMinMax(3, 4)))
                {
                    var unitCount = Utility.RandomMinMax(3, 5);
                    for (var i = 0; i < unitCount; i++)
                    {
                        AddQualityRolledDisplayItem(
                            vendor, helmets, entry.Factory, entry.Name, entry.BasePrice, entry.GMPrice,
                            ArmorerExceptionalChance, MasterArmorerName
                        );
                    }
                }
                break;

            case 2: // Colored Armorer - SP-049
                StockColoredArmorer(vendor);
                break;

            default: // Magic & Slayer Specialist - SP-049
                StockMagicSlayerSpecialist(vendor);
                break;
        }
    }

    // ---- Tier 3 (slot 2): Colored Armorer -----------------------------------

    private const string MasterColoredArmorerName = "a Master Colorsmith";

    // SP-049: metal rarity distribution, weights out of 100.
    private static readonly (CraftResource Resource, string OreName, int Weight)[] ColoredMetalPool =
    {
        (CraftResource.DullCopper, "Dull Copper", 30),
        (CraftResource.ShadowIron, "Shadow Iron", 20),
        (CraftResource.Copper, "Copper", 15),
        (CraftResource.Bronze, "Bronze", 12),
        (CraftResource.Gold, "Gold", 8),
        (CraftResource.Agapite, "Agapite", 6),
        (CraftResource.Verite, "Verite", 4),
        (CraftResource.Valorite, "Valorite", 5)
    };

    private static CraftResource RollColoredMetal(out string oreName)
    {
        var roll = Utility.Random(100);
        var cumulative = 0;
        foreach (var (resource, name, weight) in ColoredMetalPool)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                oreName = name;
                return resource;
            }
        }

        var last = ColoredMetalPool[^1];
        oreName = last.OreName;
        return last.Resource;
    }

    // SP-049: master price list - pre-packaged suit bag prices per metal.
    private static readonly Dictionary<CraftResource, int> FullColoredPlatePrice = new()
    {
        [CraftResource.DullCopper] = 3600,
        [CraftResource.ShadowIron] = 6000,
        [CraftResource.Copper] = 6000,
        [CraftResource.Bronze] = 7500,
        [CraftResource.Gold] = 9000,
        [CraftResource.Agapite] = 10500,
        [CraftResource.Verite] = 13500,
        [CraftResource.Valorite] = 24000
    };

    private static readonly Dictionary<CraftResource, int> FullColoredChainPrice = new()
    {
        [CraftResource.DullCopper] = 1730,
        [CraftResource.ShadowIron] = 2880,
        [CraftResource.Copper] = 2880,
        [CraftResource.Bronze] = 3600,
        [CraftResource.Gold] = 4320,
        [CraftResource.Agapite] = 5040,
        [CraftResource.Verite] = 6480,
        [CraftResource.Valorite] = 11520
    };

    private static readonly Dictionary<CraftResource, int> FullColoredRingPrice = new()
    {
        [CraftResource.DullCopper] = 2520,
        [CraftResource.ShadowIron] = 4200,
        [CraftResource.Copper] = 4200,
        [CraftResource.Bronze] = 5250,
        [CraftResource.Gold] = 6300,
        [CraftResource.Agapite] = 7350,
        [CraftResource.Verite] = 9450,
        [CraftResource.Valorite] = 16800
    };

    // SP-049: "Colored Shields & Helms" per-piece formula from the ticket
    // - Round5(Ingots * 1.5 * RawCost * 2). RawCost-per-metal isn't given
    // directly, so it was reverse-derived from the ticket's own Heater
    // Shield prices (18 ingots per DefBlacksmithy.cs) and verified to
    // reproduce every one of those 8 numbers exactly; the same per-metal
    // RawCost is then reused for the 4 helm types (15 ingots each per
    // DefBlacksmithy.cs), which the ticket gives no explicit prices for.
    private static readonly Dictionary<CraftResource, int> ColoredMetalRawCost = new()
    {
        [CraftResource.DullCopper] = 12,
        [CraftResource.ShadowIron] = 20,
        [CraftResource.Copper] = 20,
        [CraftResource.Bronze] = 25,
        [CraftResource.Gold] = 30,
        [CraftResource.Agapite] = 35,
        [CraftResource.Verite] = 45,
        [CraftResource.Valorite] = 80
    };

    private static int RoundToNearest5(double value) => (int)(Math.Round(value / 5.0) * 5);

    private static int ColoredShieldOrHelmPrice(CraftResource resource, int ingots) =>
        RoundToNearest5(ingots * 1.5 * ColoredMetalRawCost[resource] * 2);

    // Real DefBlacksmithy.cs ingot costs - Heater Shield 18, every helm
    // type (Bascinet/CloseHelm/NorseHelm/PlateHelm) 15.
    private const int HeaterShieldIngots = 18;
    private const int ColoredHelmIngots = 15;

    private static readonly (Func<Item> Factory, string Phrase, int Ingots)[] ColoredShieldsAndHelmsPool =
    {
        (() => new HeaterShield(), "heater shield", HeaterShieldIngots),
        (() => new PlateHelm(), "platemail helm", ColoredHelmIngots),
        (() => new CloseHelm(), "close helm", ColoredHelmIngots),
        (() => new Bascinet(), "bascinet", ColoredHelmIngots),
        (() => new NorseHelm(), "norse helm", ColoredHelmIngots)
    };

    // SP-049: every piece in one suit bag shares the SAME rolled metal
    // and is always GM/Exceptional quality (no quality roll, unlike
    // AddArmorSuitBag's own per-bundle Exceptional chance for the plain
    // Tier2 Armorer) - the ticket requires both unconditionally here.
    private static void AddColoredArmorSuitBag(
        PlayerVendor vendor, Container suitsBox, string name, int price, CraftResource resource,
        params (Func<Item> Factory, string Name)[] pieces
    )
    {
        var bag = CreateNestedPackagedSubcontainer<Bag>(vendor, suitsBox, name, 0, price);

        foreach (var (factory, pieceName) in pieces)
        {
            var piece = factory();
            piece.Amount = 1;
            ApplyExceptional(piece, pieceName, MasterColoredArmorerName);
            if (piece is BaseArmor armor)
            {
                armor.Resource = resource;
            }

            bag.DropItem(piece);
        }
    }

    private static void StockColoredArmorer(PlayerVendor vendor)
    {
        var suits = CreateDisplayContainer<WoodenBox>(vendor, "Colored Armor Suits", 0);
        var shieldsAndHelms = CreateDisplayContainer<WoodenBox>(vendor, "Colored Shields & Helms", 0);

        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            var resource = RollColoredMetal(out var oreName);
            var (suitHelmFactory, suitHelmName) = SuitHelmPool[Utility.Random(SuitHelmPool.Length)];
            AddColoredArmorSuitBag(
                vendor, suits, $"Full {oreName} Platemail Suit", FullColoredPlatePrice[resource], resource,
                (() => new PlateGorget(), "a platemail gorget"),
                (() => new PlateGloves(), "a pair of platemail gloves"),
                (() => new PlateArms(), "a pair of platemail arms"),
                (() => new PlateLegs(), "a pair of platemail legs"),
                (() => new PlateChest(), "a platemail chest"),
                (suitHelmFactory, suitHelmName)
            );
        }

        for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
        {
            var resource = RollColoredMetal(out var oreName);
            if (Utility.RandomBool())
            {
                AddColoredArmorSuitBag(
                    vendor, suits, $"Full {oreName} Chainmail Suit", FullColoredChainPrice[resource], resource,
                    (() => new ChainCoif(), "a chain coif"),
                    (() => new ChainLegs(), "a pair of chainmail leggings"),
                    (() => new ChainChest(), "a chainmail tunic")
                );
            }
            else
            {
                AddColoredArmorSuitBag(
                    vendor, suits, $"Full {oreName} Ringmail Suit", FullColoredRingPrice[resource], resource,
                    (() => new RingmailGloves(), "a pair of ringmail gloves"),
                    (() => new RingmailArms(), "a pair of ringmail sleeves"),
                    (() => new RingmailLegs(), "a pair of ringmail leggings"),
                    (() => new RingmailChest(), "a ringmail tunic")
                );
            }
        }

        for (var i = Utility.RandomMinMax(4, 6); i > 0; i--)
        {
            var resource = RollColoredMetal(out var oreName);
            var (factory, phrase, ingots) = ColoredShieldsAndHelmsPool[Utility.Random(ColoredShieldsAndHelmsPool.Length)];
            var price = ColoredShieldOrHelmPrice(resource, ingots);

            var item = factory();
            item.Amount = 1;
            ApplyExceptional(item, $"a {oreName.ToLowerInvariant()} {phrase}", MasterColoredArmorerName);
            if (item is BaseArmor armor)
            {
                armor.Resource = resource;
            }

            AddDisplayItem(vendor, shieldsAndHelms, item, price);
        }
    }

    // ---- Tier 4 (slot 3+): Magic & Slayer Specialist -------------------------

    private const string MasterMagicSmithName = "a Master Magic Smith";

    private enum WeaponRarityBand
    {
        Junk,
        Mid,
        Meta
    }

    private static readonly (Func<BaseWeapon> Factory, string Name, WeaponRarityBand Band)[] MagicSlayerWeaponPool =
    {
        // Meta
        (() => new Katana(), "a katana", WeaponRarityBand.Meta),
        (() => new Halberd(), "a halberd", WeaponRarityBand.Meta),
        (() => new HeavyCrossbow(), "a heavy crossbow", WeaponRarityBand.Meta),
        (() => new Kryss(), "a kryss", WeaponRarityBand.Meta),
        (() => new WarFork(), "a war fork", WeaponRarityBand.Meta),
        (() => new Spear(), "a spear", WeaponRarityBand.Meta),
        // Mid-Tier
        (() => new Broadsword(), "a broadsword", WeaponRarityBand.Mid),
        (() => new Longsword(), "a longsword", WeaponRarityBand.Mid),
        (() => new WarAxe(), "a war axe", WeaponRarityBand.Mid),
        (() => new BattleAxe(), "a battle axe", WeaponRarityBand.Mid),
        (() => new Bow(), "a bow", WeaponRarityBand.Mid),
        (() => new Crossbow(), "a crossbow", WeaponRarityBand.Mid),
        (() => new Mace(), "a mace", WeaponRarityBand.Mid),
        (() => new WarHammer(), "a war hammer", WeaponRarityBand.Mid),
        // Junk/Off-Meta
        (() => new Dagger(), "a dagger", WeaponRarityBand.Junk),
        (() => new SkinningKnife(), "a skinning knife", WeaponRarityBand.Junk),
        (() => new Club(), "a club", WeaponRarityBand.Junk),
        (() => new Pitchfork(), "a pitchfork", WeaponRarityBand.Junk),
        (() => new Hatchet(), "a hatchet", WeaponRarityBand.Junk),
        (() => new Pickaxe(), "a pickaxe", WeaponRarityBand.Junk)
    };

    // SP-049: the ticket's own top-level split (80% Ruin/Might/Force, 15%
    // Power, 5% Vanquishing) is preserved exactly (340+230+150+80=800,
    // 110+40=150, 30+15+5=50, all /1000). The bucket's own internal
    // split - which specific tier, and whether an accuracy bonus stacks
    // on top for Force/Power/Vanq - isn't specified by the ticket, so a
    // documented rarity curve was chosen: Ruin most common, then Might,
    // then Force; each of Force/Power/Vanq gets its own minority
    // sub-roll for a stacked Supreme/Exceeding/Eminent accuracy bonus
    // (never Ruin/Might, which the ticket gives no such variant for).
    private enum MagicWeaponTier
    {
        Ruin,
        Might,
        ForceNormal,
        ForceAccurate,
        PowerNormal,
        PowerAccurate,
        VanqNormal,
        VanqEminent,
        VanqSupreme
    }

    private static readonly (MagicWeaponTier Tier, int WeightPermille)[] MagicWeaponTierWeights =
    {
        (MagicWeaponTier.Ruin, 340),
        (MagicWeaponTier.Might, 230),
        (MagicWeaponTier.ForceNormal, 150),
        (MagicWeaponTier.ForceAccurate, 80),
        (MagicWeaponTier.PowerNormal, 110),
        (MagicWeaponTier.PowerAccurate, 40),
        (MagicWeaponTier.VanqNormal, 30),
        (MagicWeaponTier.VanqEminent, 15),
        (MagicWeaponTier.VanqSupreme, 5)
    };

    private static MagicWeaponTier RollMagicWeaponTier()
    {
        var roll = Utility.Random(1000);
        var cumulative = 0;
        foreach (var (tier, weight) in MagicWeaponTierWeights)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return tier;
            }
        }

        return MagicWeaponTierWeights[^1].Tier;
    }

    private static void ApplyMagicWeaponTier(BaseWeapon weapon, MagicWeaponTier tier)
    {
        switch (tier)
        {
            case MagicWeaponTier.Ruin:
                weapon.DamageLevel = WeaponDamageLevel.Ruin;
                break;
            case MagicWeaponTier.Might:
                weapon.DamageLevel = WeaponDamageLevel.Might;
                break;
            case MagicWeaponTier.ForceNormal:
                weapon.DamageLevel = WeaponDamageLevel.Force;
                break;
            case MagicWeaponTier.ForceAccurate:
                weapon.DamageLevel = WeaponDamageLevel.Force;
                weapon.AccuracyLevel = Utility.RandomBool() ? WeaponAccuracyLevel.Exceedingly : WeaponAccuracyLevel.Supremely;
                break;
            case MagicWeaponTier.PowerNormal:
                weapon.DamageLevel = WeaponDamageLevel.Power;
                break;
            case MagicWeaponTier.PowerAccurate:
                weapon.DamageLevel = WeaponDamageLevel.Power;
                weapon.AccuracyLevel = WeaponAccuracyLevel.Supremely;
                break;
            case MagicWeaponTier.VanqNormal:
                weapon.DamageLevel = WeaponDamageLevel.Vanq;
                break;
            case MagicWeaponTier.VanqEminent:
                weapon.DamageLevel = WeaponDamageLevel.Vanq;
                weapon.AccuracyLevel = WeaponAccuracyLevel.Eminently;
                break;
            case MagicWeaponTier.VanqSupreme:
                weapon.DamageLevel = WeaponDamageLevel.Vanq;
                weapon.AccuracyLevel = WeaponAccuracyLevel.Supremely;
                break;
        }

        weapon.Identified = true;
    }

    // SP-049: master price list - Magic Weapons matrix.
    private static int MagicWeaponPrice(MagicWeaponTier tier, WeaponRarityBand band) => (tier, band) switch
    {
        (MagicWeaponTier.Ruin, WeaponRarityBand.Junk) => 100,
        (MagicWeaponTier.Ruin, WeaponRarityBand.Mid) => 125,
        (MagicWeaponTier.Ruin, WeaponRarityBand.Meta) => 400,

        (MagicWeaponTier.Might, WeaponRarityBand.Junk) => 150,
        (MagicWeaponTier.Might, WeaponRarityBand.Mid) => 175,
        (MagicWeaponTier.Might, WeaponRarityBand.Meta) => 600,

        (MagicWeaponTier.ForceNormal, WeaponRarityBand.Junk) => 550,
        (MagicWeaponTier.ForceNormal, WeaponRarityBand.Mid) => 1750,
        (MagicWeaponTier.ForceNormal, WeaponRarityBand.Meta) => 4250,

        (MagicWeaponTier.ForceAccurate, WeaponRarityBand.Junk) => 900,
        (MagicWeaponTier.ForceAccurate, WeaponRarityBand.Mid) => 3000,
        (MagicWeaponTier.ForceAccurate, WeaponRarityBand.Meta) => 9000,

        (MagicWeaponTier.PowerNormal, WeaponRarityBand.Junk) => 1750,
        (MagicWeaponTier.PowerNormal, WeaponRarityBand.Mid) => 5500,
        (MagicWeaponTier.PowerNormal, WeaponRarityBand.Meta) => 14000,

        (MagicWeaponTier.PowerAccurate, WeaponRarityBand.Junk) => 3500,
        (MagicWeaponTier.PowerAccurate, WeaponRarityBand.Mid) => 10000,
        (MagicWeaponTier.PowerAccurate, WeaponRarityBand.Meta) => 30000,

        (MagicWeaponTier.VanqNormal, WeaponRarityBand.Junk) => 3500,
        (MagicWeaponTier.VanqNormal, WeaponRarityBand.Mid) => 17500,
        (MagicWeaponTier.VanqNormal, WeaponRarityBand.Meta) => 37500,

        (MagicWeaponTier.VanqEminent, WeaponRarityBand.Junk) => 6000,
        (MagicWeaponTier.VanqEminent, WeaponRarityBand.Mid) => 27500,
        (MagicWeaponTier.VanqEminent, WeaponRarityBand.Meta) => 62500,

        (MagicWeaponTier.VanqSupreme, WeaponRarityBand.Junk) => 15000,
        (MagicWeaponTier.VanqSupreme, WeaponRarityBand.Mid) => 60000,
        (MagicWeaponTier.VanqSupreme, WeaponRarityBand.Meta) => 175000,

        _ => 100
    };

    // SP-049: Silver Slayer sub-roll - the ticket gives no weights for
    // Force/Power/Vanquishing here (unlike the Magic Weapons box above),
    // so the same general rarity curve is reused: Force common, Power
    // uncommon, Vanquishing rare.
    private static readonly (WeaponDamageLevel Level, int WeightPermille)[] SilverSlayerWeights =
    {
        (WeaponDamageLevel.Force, 700),
        (WeaponDamageLevel.Power, 250),
        (WeaponDamageLevel.Vanq, 50)
    };

    private static WeaponDamageLevel RollSilverSlayerLevel()
    {
        var roll = Utility.Random(1000);
        var cumulative = 0;
        foreach (var (level, weight) in SilverSlayerWeights)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return level;
            }
        }

        return SilverSlayerWeights[^1].Level;
    }

    // SP-049: master price list - Silver Slayer matrix.
    private static int SilverSlayerPrice(WeaponDamageLevel level, WeaponRarityBand band) => (level, band) switch
    {
        (WeaponDamageLevel.Force, WeaponRarityBand.Junk) => 1500,
        (WeaponDamageLevel.Force, WeaponRarityBand.Mid) => 3500,
        (WeaponDamageLevel.Force, WeaponRarityBand.Meta) => 6000,

        (WeaponDamageLevel.Power, WeaponRarityBand.Junk) => 4500,
        (WeaponDamageLevel.Power, WeaponRarityBand.Mid) => 10000,
        (WeaponDamageLevel.Power, WeaponRarityBand.Meta) => 18500,

        (WeaponDamageLevel.Vanq, WeaponRarityBand.Junk) => 12500,
        (WeaponDamageLevel.Vanq, WeaponRarityBand.Mid) => 40000,
        (WeaponDamageLevel.Vanq, WeaponRarityBand.Meta) => 75000,

        _ => 1500
    };

    private enum ArmorSizeBand
    {
        Small,
        Mid,
        Chest,
        Shield
    }

    // SP-049: LeatherCap stands in as leather's own "helm" slot,
    // alongside Plate's own helm and Chain's Coif, for the same Mid band
    // every other material's head piece lands in.
    private static readonly (Func<Item> Factory, string Name, ArmorSizeBand Band)[] LooseMagicArmorPool =
    {
        (() => new PlateGorget(), "a platemail gorget", ArmorSizeBand.Small),
        (() => new PlateGloves(), "a pair of platemail gloves", ArmorSizeBand.Small),
        (() => new PlateArms(), "a pair of platemail arms", ArmorSizeBand.Mid),
        (() => new PlateHelm(), "a platemail helm", ArmorSizeBand.Mid),
        (() => new PlateLegs(), "a pair of platemail legs", ArmorSizeBand.Mid),
        (() => new PlateChest(), "a platemail chest", ArmorSizeBand.Chest),

        (() => new ChainCoif(), "a chain coif", ArmorSizeBand.Mid),
        (() => new ChainLegs(), "a pair of chainmail leggings", ArmorSizeBand.Mid),
        (() => new ChainChest(), "a chainmail tunic", ArmorSizeBand.Chest),

        (() => new LeatherGorget(), "a leather gorget", ArmorSizeBand.Small),
        (() => new LeatherGloves(), "a pair of leather gloves", ArmorSizeBand.Small),
        (() => new LeatherArms(), "a pair of leather arms", ArmorSizeBand.Mid),
        (() => new LeatherCap(), "a leather cap", ArmorSizeBand.Mid),
        (() => new LeatherLegs(), "a pair of leather legs", ArmorSizeBand.Mid),
        (() => new LeatherChest(), "a leather chest", ArmorSizeBand.Chest),

        (() => new HeaterShield(), "a heater shield", ArmorSizeBand.Shield)
    };

    // SP-049: the ticket's own combined buckets ("70% Defense/Guarding",
    // "5% Fortification/Invulnerability") are split evenly within each
    // pair, since no finer split is given.
    private static readonly (ArmorProtectionLevel Level, int WeightPermille)[] MagicArmorProtectionWeights =
    {
        (ArmorProtectionLevel.Defense, 350),
        (ArmorProtectionLevel.Guarding, 350),
        (ArmorProtectionLevel.Hardening, 250),
        (ArmorProtectionLevel.Fortification, 25),
        (ArmorProtectionLevel.Invulnerability, 25)
    };

    private static ArmorProtectionLevel RollMagicArmorProtection()
    {
        var roll = Utility.Random(1000);
        var cumulative = 0;
        foreach (var (level, weight) in MagicArmorProtectionWeights)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return level;
            }
        }

        return MagicArmorProtectionWeights[^1].Level;
    }

    // SP-049: master price list - Loose Magic Armor & Shields matrix.
    private static int MagicArmorPrice(ArmorProtectionLevel level, ArmorSizeBand band) => (level, band) switch
    {
        (ArmorProtectionLevel.Defense, ArmorSizeBand.Small) => 150,
        (ArmorProtectionLevel.Guarding, ArmorSizeBand.Small) => 375,
        (ArmorProtectionLevel.Hardening, ArmorSizeBand.Small) => 1150,
        (ArmorProtectionLevel.Fortification, ArmorSizeBand.Small) => 3750,
        (ArmorProtectionLevel.Invulnerability, ArmorSizeBand.Small) => 11500,

        (ArmorProtectionLevel.Defense, ArmorSizeBand.Mid) => 300,
        (ArmorProtectionLevel.Guarding, ArmorSizeBand.Mid) => 750,
        (ArmorProtectionLevel.Hardening, ArmorSizeBand.Mid) => 2250,
        (ArmorProtectionLevel.Fortification, ArmorSizeBand.Mid) => 7500,
        (ArmorProtectionLevel.Invulnerability, ArmorSizeBand.Mid) => 22500,

        (ArmorProtectionLevel.Defense, ArmorSizeBand.Chest) => 600,
        (ArmorProtectionLevel.Guarding, ArmorSizeBand.Chest) => 1500,
        (ArmorProtectionLevel.Hardening, ArmorSizeBand.Chest) => 5250,
        (ArmorProtectionLevel.Fortification, ArmorSizeBand.Chest) => 18500,
        (ArmorProtectionLevel.Invulnerability, ArmorSizeBand.Chest) => 60000,

        (ArmorProtectionLevel.Defense, ArmorSizeBand.Shield) => 800,
        (ArmorProtectionLevel.Guarding, ArmorSizeBand.Shield) => 2000,
        (ArmorProtectionLevel.Hardening, ArmorSizeBand.Shield) => 6500,
        (ArmorProtectionLevel.Fortification, ArmorSizeBand.Shield) => 15000,
        (ArmorProtectionLevel.Invulnerability, ArmorSizeBand.Shield) => 30000,

        _ => 150
    };

    private static void StockMagicSlayerSpecialist(PlayerVendor vendor)
    {
        var magicWeapons = CreateDisplayContainer<WoodenBox>(vendor, "Magic Weapons", 0);
        for (var i = Utility.RandomMinMax(4, 6); i > 0; i--)
        {
            var entry = MagicSlayerWeaponPool[Utility.Random(MagicSlayerWeaponPool.Length)];
            var tier = RollMagicWeaponTier();
            var weapon = entry.Factory();
            weapon.Amount = 1;
            ApplyExceptional(weapon, entry.Name, MasterMagicSmithName);
            ApplyMagicWeaponTier(weapon, tier);
            AddDisplayItem(vendor, magicWeapons, weapon, MagicWeaponPrice(tier, entry.Band));
        }

        var slayerWeapons = CreateDisplayContainer<WoodenBox>(vendor, "Slayer Weapons", 0);
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            var entry = MagicSlayerWeaponPool[Utility.Random(MagicSlayerWeaponPool.Length)];
            var level = RollSilverSlayerLevel();
            var weapon = entry.Factory();
            weapon.Amount = 1;
            ApplyExceptional(weapon, entry.Name, MasterMagicSmithName);
            weapon.DamageLevel = level;
            weapon.Slayer = SlayerName.Silver;
            weapon.Identified = true;
            AddDisplayItem(vendor, slayerWeapons, weapon, SilverSlayerPrice(level, entry.Band));
        }

        // SP-049: strictly loose items only - no bags or pre-packaged sets.
        var looseArmor = CreateDisplayContainer<WoodenBox>(vendor, "Loose Magic Armor & Shields", 0);
        for (var i = Utility.RandomMinMax(4, 6); i > 0; i--)
        {
            var entry = LooseMagicArmorPool[Utility.Random(LooseMagicArmorPool.Length)];
            var level = RollMagicArmorProtection();
            var item = entry.Factory();
            item.Amount = 1;
            ApplyExceptional(item, entry.Name, MasterMagicSmithName);
            if (item is BaseArmor armor)
            {
                armor.ProtectionLevel = level;
                armor.Identified = true;
            }

            AddDisplayItem(vendor, looseArmor, item, MagicArmorPrice(level, entry.Band));
        }
    }

    // ==== MageApothecary =====================================================

    private const string MasterApothecaryName = "a Master Apothecary";

    // SP-049: tier now maps directly off the effective slot
    // StockVendor.DetermineEffectiveSlot already resolved (0=Herbalist,
    // 1=Potion Brewer, 2=Master Brewer, 3+=Wand Merchant) - the universal
    // 1-vendor-shop 50/50 roll now lives in that one shared place instead
    // of being reimplemented per archetype.
    private static void StockMageApothecary(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0:
                StockHerbalist(vendor);
                break;
            case 1:
                StockPotionBrewer(vendor);
                break;
            case 2:
                StockMasterBrewer(vendor);
                break;
            default:
                StockWandMerchant(vendor);
                break;
        }
    }

    // ---- Tier 1: Herbalist --------------------------------------------------

    // SP-046: master price list - Tiered Reagents. Bulk (500+) tier,
    // +2g/unit premium for Bloodmoss and Black Pearl over the other six
    // reagent types.
    private const int ReagentBulkPricePerUnit = 4;
    private const int ReagentBulkPricePerUnitPremium = 6;

    // SP-048: master price list - "100 of Each Reagent (800 Total)" bundle.
    private const int EightHundredReagentBundlePrice = 4400;

    // The six reagent types that share the non-premium (Garlic/Ginseng/
    // Mandrake/Nightshade/Spider's Silk/Sulfurous Ash) rate - Bloodmoss
    // and Black Pearl are priced separately at the premium rate wherever
    // this is used. SP-029's own CS0310 note applies here too: a factory
    // delegate, not a `new()`-constrained generic, since every reagent
    // class declares its constructor as `(int amount = 1)`.
    private static readonly Func<Item>[] StandardReagentFactories =
    {
        () => new Garlic(),
        () => new Ginseng(),
        () => new MandrakeRoot(),
        () => new Nightshade(),
        () => new SpidersSilk(),
        () => new SulfurousAsh()
    };

    // SP-052: "Bulk Reagent Crate" wrapper is gone - 8 reagent-stack
    // varieties (6 standard + Black Pearl + Bloodmoss), under the
    // 12-variety threshold, now sell loose straight out of root. The
    // "100 of Each Reagent" bundle below is a genuine sealed bulk pack
    // (the policy's own explicit exception) and stays as-is.
    private static void StockHerbalist(PlayerVendor vendor)
    {
        foreach (var factory in StandardReagentFactories)
        {
            for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
            {
                SellLoose(vendor, Stack(factory(), 500), ReagentBulkPricePerUnit * 500);
            }
        }
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            SellLoose(vendor, Stack(new BlackPearl(), 500), ReagentBulkPricePerUnitPremium * 500);
        }
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            SellLoose(vendor, Stack(new Bloodmoss(), 500), ReagentBulkPricePerUnitPremium * 500);
        }

        // SP-048: replaces the old individually-browsable 100-stack
        // pouches with sealed "100 of Each Reagent" bags - 800 total
        // reagents (100 of all 8 types), sold as one lot.
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            SellBundle(
                vendor, new Bag(), 0x48E, EightHundredReagentBundlePrice, "100 of Each Reagent (800 Total)",
                Stack(new BlackPearl(), 100), Stack(new Bloodmoss(), 100), Stack(new Garlic(), 100),
                Stack(new Ginseng(), 100), Stack(new MandrakeRoot(), 100), Stack(new Nightshade(), 100),
                Stack(new SpidersSilk(), 100), Stack(new SulfurousAsh(), 100)
            );
        }
    }

    // ---- Tier 2 & 3: Potion Brewer / Master Brewer ---------------------------

    // SP-048: master price list - shared bottle/keg potion catalog. Tier 2
    // sells single bottles, Tier 3 sells 100-dose kegs of the same 8
    // effects, just packaged differently.
    private static readonly (Func<Item> BottleFactory, PotionEffect Effect, string Phrase, int BottlePrice, int KegPrice)[] BrewerPotionPool =
    {
        (() => new GreaterHealPotion(), PotionEffect.HealGreater, "greater heal potion", 40, 3700),
        (() => new GreaterCurePotion(), PotionEffect.CureGreater, "greater cure potion", 40, 3700),
        (() => new TotalRefreshPotion(), PotionEffect.RefreshTotal, "total refresh potion", 35, 3200),
        (() => new GreaterExplosionPotion(), PotionEffect.ExplosionGreater, "greater explosion potion", 50, 4800),
        (() => new DeadlyPoisonPotion(), PotionEffect.PoisonDeadly, "deadly poison potion", 100, 11700),
        (() => new GreaterStrengthPotion(), PotionEffect.StrengthGreater, "greater strength potion", 30, 2700),
        (() => new GreaterAgilityPotion(), PotionEffect.AgilityGreater, "greater agility potion", 25, 2300),
        (() => new NightSightPotion(), PotionEffect.Nightsight, "night sight potion", 15, 1200)
    };

    // SP-049: flattened straight into the vendor's own root backpack -
    // no "Bottled Potions" sub-box - VendorGridArranger grids/compacts
    // the loose bottles across the main backpack viewport on its own.
    private static void StockPotionBrewer(PlayerVendor vendor)
    {
        foreach (var entry in BrewerPotionPool)
        {
            for (var i = Utility.RandomMinMax(5, 10); i > 0; i--)
            {
                var potion = entry.BottleFactory();
                potion.Amount = 1; // SP-048 Stack Sanitization guardrail
                SellLoose(vendor, Named(potion, $"a {entry.Phrase}"), entry.BottlePrice);
            }
        }
    }

    private const int EmptyKegBasePrice = 200;
    private const int EmptyKegGMPrice = 300;
    private const double EmptyKegExceptionalChance = 0.30;

    // SP-049: flattened straight into the vendor's own root backpack -
    // no "Potion Kegs" sub-box - VendorGridArranger grids/compacts the
    // loose kegs across the main backpack viewport on its own.
    private static void StockMasterBrewer(PlayerVendor vendor)
    {
        foreach (var entry in BrewerPotionPool)
        {
            for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
            {
                var keg = new PotionKeg { Type = entry.Effect, Held = 100, Amount = 1 };
                SellLoose(vendor, Named(keg, $"a keg of {entry.Phrase}s"), entry.KegPrice);
            }
        }

        for (var i = Utility.RandomMinMax(2, 4); i > 0; i--)
        {
            AddQualityRolledDisplayItem(
                vendor, vendor.Backpack, () => new PotionKeg(), "an empty potion keg",
                EmptyKegBasePrice, EmptyKegGMPrice, EmptyKegExceptionalChance, MasterApothecaryName
            );
        }
    }

    // ---- Tier 4: Wand Merchant ------------------------------------------------

    // SP-049: 11-type wand pool with per-charge unit rates from the
    // master price list - real class names differ from the ticket's own
    // descriptive names for 3 of these (confirmed against the full
    // Items/Wands/ catalog - no other class matches): Identification ->
    // IDWand, Feeblemind -> FeebleWand, Clumsiness -> ClumsyWand.
    private static readonly (Func<BaseWand> Factory, string Name, int PricePerCharge)[] WandPool =
    {
        (() => new LightningWand(), "a lightning wand", 150),
        (() => new GreaterHealWand(), "a greater healing wand", 150),
        (() => new FireballWand(), "a fireball wand", 60),
        (() => new HarmWand(), "a harm wand", 40),
        (() => new MagicArrowWand(), "a magic arrow wand", 25),
        (() => new ManaDrainWand(), "a mana drain wand", 30),
        (() => new HealWand(), "a healing wand", 20),
        (() => new IDWand(), "an identification wand", 10),
        (() => new ClumsyWand(), "a clumsiness wand", 10),
        (() => new FeebleWand(), "a feeblemind wand", 10),
        (() => new WeaknessWand(), "a weakness wand", 10)
    };

    private const double WandDuplicateChance = 0.05;
    private const int WandChargesMin = 15;
    private const int WandChargesMax = 35;

    private const int TrappedPouchPrice = 75;
    private const int MortarPestlePrice = 75;
    private const int WandMerchantEmptyKegPrice = 200;

    private static void StockWandMerchant(PlayerVendor vendor)
    {
        foreach (var (factory, name, pricePerCharge) in WandPool.RandomSample(3))
        {
            var copies = Utility.RandomDouble() < WandDuplicateChance ? 2 : 1;
            for (var i = 0; i < copies; i++)
            {
                var wand = factory();
                wand.Charges = Utility.RandomMinMax(WandChargesMin, WandChargesMax);
                wand.Identified = true;
                wand.Amount = 1; // SP-048 Stack Sanitization guardrail
                // SP-049: dynamic per-charge pricing - price = charges * unitPricePerCharge.
                var price = wand.Charges * pricePerCharge;
                SellLoose(vendor, Named(wand, name), price);
            }
        }

        // SP-048: Trapped Pouches - the same real mechanic CustomBots/
        // EquipmentTable.cs's own AddTrappedPouches already uses for
        // PlayerBots (a plain Pouch with TrapType.MagicTrap/TrapPower/
        // TrapLevel set) - there's no distinct "TrappedPouch" class
        // anywhere in ModernUO.
        for (var i = Utility.RandomMinMax(2, 4); i > 0; i--)
        {
            var pouch = new Pouch { TrapType = TrapType.MagicTrap, TrapPower = 1, TrapLevel = 0 };
            SellLoose(vendor, Named(pouch, "a trapped pouch"), TrappedPouchPrice);
        }

        for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
        {
            AddGMItem(vendor, new MortarPestle(), "a mortar and pestle", MortarPestlePrice, MasterApothecaryName);
        }

        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            SellLoose(vendor, Named(new PotionKeg(), "an empty potion keg"), WandMerchantEmptyKegPrice);
        }
    }

    // ==== ScribeLibrary ======================================================

    // SP-046: master price list - Full 64-Spell Spellbook, 3,500g.
    private const int FullSpellbookPrice = 3500;

    // SP-046: master price list - Utility Inscription. Flat per-unit rate
    // (no bulk discount, unlike reagents): Recall/Mark 45g/unit (4,500g
    // per 100), Gate Travel 40g/unit (4,000g per 100).
    private const int RecallScrollPricePerUnit = 45;
    private const int GateTravelScrollPricePerUnit = 40;
    private const int MarkScrollPricePerUnit = 45;

    // SP-046: master price list - Blank Scrolls, 6g/unit bulk (500+) and
    // 7g/unit standard (100+), same +1g/unit premium pattern as the
    // tiered reagents above.
    private const int BlankScrollBulkPricePerUnit = 6;
    private const int BlankScrollStandardPricePerUnit = 7;

    private const string MasterScribeName = "a Master Scribe";

    private static void StockScribeLibrary(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0:
                StockScribeApprentice(vendor);
                break;
            case 1:
                StockScholarSpellbookBinder(vendor);
                break;
            case 2:
                StockMasterScribe(vendor);
                break;
            default:
                StockArcaneArchivist(vendor);
                break;
        }
    }

    // ---- Tier 1 (slot 0): Scribe Apprentice ---------------------------------

    // SP-050: master price list - 100-scroll bundle bags, same flat
    // per-unit rate as the loose singles (no bulk discount for travel
    // scrolls - see the per-unit constants above).
    private const int TravelScrollBundleSize = 100;
    private const int NonGMRunebookPrice = 900;

    // Pre-packages `count` scrolls of the given type into a Bag, priced
    // as one sealed lot inside an already-registered display parent.
    private static void AddTravelScrollBundleBag(
        PlayerVendor vendor, Container parent, string name, Func<Item> factory, int pricePerUnit
    )
    {
        var bag = CreateNestedPackagedSubcontainer<Bag>(vendor, parent, name, 0, pricePerUnit * TravelScrollBundleSize);
        bag.DropItem(Stack(factory(), TravelScrollBundleSize));
    }

    // SP-052: flattened straight into the vendor's own root backpack - 9
    // item/bundle varieties (2 blank-scroll stacks, 3 scroll types x
    // [singles + bundle bag], 1 runebook), well under the 12-variety
    // threshold. VendorGridArranger.GroupItems merges same-(Type, Hue,
    // Price) drops into one deck-of-cards grid slot regardless of how
    // many individual singles get dropped in a loop, so this doesn't
    // blow up the root grid the way it would look at first glance -
    // e.g. 10-15 loose Recall Scrolls collapse into exactly 1 slot, not
    // 10-15. Guardrail: Scribe's Pens (ScribesPen) are never stocked
    // anywhere in this tier (or archetype) - finished goods only, never
    // the crafting tool itself.
    private static void StockScribeApprentice(PlayerVendor vendor)
    {
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            SellLoose(vendor, Stack(new BlankScroll(), 500), BlankScrollBulkPricePerUnit * 500);
        }
        for (var i = Utility.RandomMinMax(3, 5); i > 0; i--)
        {
            SellLoose(vendor, Stack(new BlankScroll(), 100), BlankScrollStandardPricePerUnit * 100);
        }

        for (var i = Utility.RandomMinMax(10, 15); i > 0; i--)
        {
            SellLoose(vendor, Named(new RecallScroll(), "a recall scroll"), RecallScrollPricePerUnit);
        }
        for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
        {
            AddTravelScrollBundleBag(vendor, vendor.Backpack, "a bag of 100 recall scrolls", () => new RecallScroll(), RecallScrollPricePerUnit);
        }

        for (var i = Utility.RandomMinMax(10, 15); i > 0; i--)
        {
            SellLoose(vendor, Named(new GateTravelScroll(), "a gate travel scroll"), GateTravelScrollPricePerUnit);
        }
        for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
        {
            AddTravelScrollBundleBag(vendor, vendor.Backpack, "a bag of 100 gate travel scrolls", () => new GateTravelScroll(), GateTravelScrollPricePerUnit);
        }

        for (var i = Utility.RandomMinMax(10, 15); i > 0; i--)
        {
            SellLoose(vendor, Named(new MarkScroll(), "a mark scroll"), MarkScrollPricePerUnit);
        }
        for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
        {
            AddTravelScrollBundleBag(vendor, vendor.Backpack, "a bag of 100 mark scrolls", () => new MarkScroll(), MarkScrollPricePerUnit);
        }

        for (var i = Utility.RandomMinMax(2, 4); i > 0; i--)
        {
            SellLoose(vendor, Named(new Runebook(), "an empty runebook"), NonGMRunebookPrice);
        }
    }

    // ---- Tier 2 (slot 1): Scholar & Spellbook Binder ------------------------

    // SP-050: Starter Spellbook content bitmask - spells 0-31 (circles
    // 1-4), the lower 32 bits of Spellbook's own 64-bit Content field.
    private const ulong StarterSpellbookContent = 0xFFFFFFFF;
    private const int StarterSpellbookPrice = 1000;
    private const int GMRunebookPrice = 1200;
    private const int GMRunebookDefaultCharges = 10;

    // SP-051: flattened straight into the vendor's own root backpack -
    // fewer than a dozen item types here, none of them bundled/sealed,
    // so the old "Spellbooks & Master Runebooks" WoodenBox wrapper added
    // a browse step without actually organizing anything.
    private static void StockScholarSpellbookBinder(PlayerVendor vendor)
    {
        // Guardrail: never Content = 0 (a blank/empty spellbook) - every
        // spellbook this tier sells is either the full 64-spell set or
        // the 32-spell (circles 1-4) starter set.
        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            var book = new Spellbook(ulong.MaxValue) { Amount = 1 };
            SellLoose(vendor, book, FullSpellbookPrice);
        }

        for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
        {
            var book = new Spellbook(StarterSpellbookContent) { Amount = 1 };
            SellLoose(vendor, book, StarterSpellbookPrice);
        }

        for (var i = Utility.RandomMinMax(2, 4); i > 0; i--)
        {
            var runebook = new Runebook(GMRunebookDefaultCharges)
            {
                Amount = 1,
                Name = "a runebook",
                Quality = BookQuality.Exceptional,
                Crafter = MasterScribeName
            };
            SellLoose(vendor, runebook, GMRunebookPrice);
        }
    }

    // ---- Tier 3 (slot 2): Master Scribe --------------------------------------

    private const int HighCircleScrollPrice = 75;
    private const int HighCircleScrollBundleSize = 20;
    private const int HighCircleScrollBundlePrice = 1500;

    private static readonly (Func<Item> Factory, string Phrase)[] HighCircleScrollPool =
    {
        (() => new FlamestrikeScroll(), "flamestrike scroll"),
        (() => new ResurrectionScroll(), "resurrection scroll"),
        (() => new EnergyVortexScroll(), "energy vortex scroll"),
        (() => new EarthquakeScroll(), "earthquake scroll"),
        (() => new ChainLightningScroll(), "chain lightning scroll"),
        (() => new MeteorSwarmScroll(), "meteor swarm scroll"),
        (() => new SummonDaemonScroll(), "summon daemon scroll")
    };

    // SP-050: master price list - weighted treasure map level roll
    // (45/30/15/7/3, sums to 100). Level numbers are 1-indexed
    // constructor args, matching this file's own established convention
    // (TreasureMap's own level==0 is reserved for the Haven/beginner
    // island map, not a real named difficulty tier).
    private static readonly (int Level, int Weight, int Price)[] TreasureMapTiers =
    {
        (1, 45, 300),
        (2, 30, 750),
        (3, 15, 1800),
        (4, 7, 4500),
        (5, 3, 10000)
    };

    private static (int Level, int Price) RollTreasureMapTier()
    {
        var roll = Utility.Random(100);
        var cumulative = 0;
        foreach (var (level, weight, price) in TreasureMapTiers)
        {
            cumulative += weight;
            if (roll < cumulative)
            {
                return (level, price);
            }
        }

        var last = TreasureMapTiers[^1];
        return (last.Level, last.Price);
    }

    // SP-051: high circle scrolls (loose singles and sealed 20-packs)
    // now sell straight out of the root backpack - only 7 item types,
    // none needing category segregation, so the old "High Circle
    // Scrolls" WoodenBox wrapper is gone. Treasure maps keep their own
    // WoodenBox: a large, purely random-roll item family worth browsing
    // separately from everything else in the pack.
    private static void StockMasterScribe(PlayerVendor vendor)
    {
        foreach (var (factory, phrase) in HighCircleScrollPool)
        {
            for (var i = Utility.RandomMinMax(4, 6); i > 0; i--)
            {
                SellLoose(vendor, Named(factory(), $"a {phrase}"), HighCircleScrollPrice);
            }

            for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
            {
                AddTravelScrollBundleBag(
                    vendor, vendor.Backpack, $"a bag of {HighCircleScrollBundleSize} {phrase}s",
                    factory, HighCircleScrollBundlePrice / HighCircleScrollBundleSize
                );
            }
        }

        var maps = CreateDisplayContainer<WoodenBox>(vendor, "Treasure Maps", 0);
        var mapFacet = vendor.Map is { } m && m != Map.Internal ? m : Map.Felucca;
        for (var i = Utility.RandomMinMax(2, 4); i > 0; i--)
        {
            var (level, price) = RollTreasureMapTier();
            var map = new TreasureMap(level, mapFacet) { Amount = 1 };
            AddDisplayItem(vendor, maps, map, price);
        }
    }

    // ---- Tier 4 (slot 3+): Arcane Archivist -----------------------------------

    // SP-051: every pre-marked runebook's total price now bakes in the
    // uncharged runebook's own baseline value (NonGMRunebookPrice, the
    // same price Tier 1 sells a blank runebook for) on top of its marked
    // spots: TotalPrice = NonGMRunebookPrice + (spots x pricePerSpot).
    // Name/Description stay plain - no charge counts or price blurbs.
    private static Runebook CreateMarkedRunebook(string bookName, (Point3D Loc, Map Map, string Desc)[] runes)
    {
        var book = new Runebook(runes.Length) { Name = bookName, Amount = 1 };
        book.CurCharges = book.MaxCharges;

        foreach (var (loc, map, desc) in runes)
        {
            book.Entries.Add(new RunebookEntry(book, loc, map, desc));
        }

        return book;
    }

    // SP-050: single verified entrance points, reused for the "Dungeon
    // Entrances" core book below, as the Level-1/entrance entry in each
    // dungeon's own per-floor rune array further down, and (for
    // Destard/Hythloth/Delucia specifically) as the substitute
    // coordinate for the T2A Secret Passages and Boss/Farm Lair
    // consolidated books, which still have no verified interior-tunnel
    // or boss-camp data of their own.
    // SP-053: entrance coordinates corrected against the ticket's own
    // verified interior-dungeon survey (small deltas vs. the SP-050
    // originals - e.g. Despise x 1301->1304 - fixing blocked/wall-clip
    // tiles the earlier approximated entrances landed on). Fire/Ice keep
    // their SP-050 values unchanged (the ticket re-confirms them
    // as-is); every other dungeon's entrance below is the ticket's own
    // corrected number. These same consts still back DungeonEntrancesRunes,
    // T2ASecretPassagesRunes, and the SP-051 consolidated Boss/Lair and
    // T2A Passages books below, so the fix applies everywhere at once.
    private static readonly (Point3D Loc, Map Map, string Desc) CovetousSpot = (new Point3D(2498, 919, 0), Map.Felucca, "Covetous");
    private static readonly (Point3D Loc, Map Map, string Desc) DeceitSpot = (new Point3D(4111, 432, 5), Map.Felucca, "Deceit");
    private static readonly (Point3D Loc, Map Map, string Desc) DespiseSpot = (new Point3D(1304, 1080, 0), Map.Felucca, "Despise");
    private static readonly (Point3D Loc, Map Map, string Desc) DestardSpot = (new Point3D(1170, 2640, 0), Map.Felucca, "Destard");
    private static readonly (Point3D Loc, Map Map, string Desc) HythlothSpot = (new Point3D(4721, 3822, 0), Map.Felucca, "Hythloth");
    private static readonly (Point3D Loc, Map Map, string Desc) ShameSpot = (new Point3D(510, 1565, 0), Map.Felucca, "Shame");
    private static readonly (Point3D Loc, Map Map, string Desc) WrongSpot = (new Point3D(2043, 228, 14), Map.Felucca, "Wrong");
    private static readonly (Point3D Loc, Map Map, string Desc) FireSpot = (new Point3D(2923, 3407, 8), Map.Felucca, "Fire Dungeon");
    private static readonly (Point3D Loc, Map Map, string Desc) IceSpot = (new Point3D(1999, 81, 4), Map.Felucca, "Ice Dungeon");
    private static readonly (Point3D Loc, Map Map, string Desc) DeluciaSpot = (new Point3D(5272, 3995, 37), Map.Felucca, "Delucia");
    // SP-053: new dungeon added by this ticket - no prior SP-050/051
    // entrance const existed for it.
    private static readonly (Point3D Loc, Map Map, string Desc) OrcSpot = (new Point3D(1019, 1431, 0), Map.Felucca, "Orc Cave");

    private static readonly (Point3D Loc, Map Map, string Desc)[] BritanniaCitiesRunes =
    {
        (new Point3D(1434, 1699, 2), Map.Felucca, "Britain"),
        (new Point3D(4407, 1169, 0), Map.Felucca, "Moonglow"),
        (new Point3D(546, 992, 0), Map.Felucca, "Yew"),
        (new Point3D(1823, 2821, 0), Map.Felucca, "Trinsic"),
        (new Point3D(1378, 3817, 0), Map.Felucca, "Jhelom"),
        (new Point3D(596, 2138, 0), Map.Felucca, "Skara Brae"),
        (new Point3D(2899, 676, 0), Map.Felucca, "Vesper"),
        (new Point3D(2477, 407, 15), Map.Felucca, "Minoc"),
        (new Point3D(3734, 2163, 20), Map.Felucca, "Magincia"),
        (new Point3D(3650, 2519, 0), Map.Felucca, "Ocllo"),
        // SP-050: "Wind Entrance" from the ticket's own city list has no
        // matching real Britannia location anywhere in this codebase or
        // classic UO geography - Nujel'm (a real city with its own
        // independently-verified coordinate) is substituted as the 11th
        // real city so the book still has exactly 11 marked spots.
        // SP-053: the substituted Nujel'm coordinate itself landed on a
        // blocked tile - corrected to the ticket's own verified point.
        (new Point3D(3755, 1307, 0), Map.Felucca, "Nujel'm")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] DungeonEntrancesRunes =
    {
        (CovetousSpot.Loc, CovetousSpot.Map, CovetousSpot.Desc),
        (DeceitSpot.Loc, DeceitSpot.Map, DeceitSpot.Desc),
        (DespiseSpot.Loc, DespiseSpot.Map, DespiseSpot.Desc),
        (DestardSpot.Loc, DestardSpot.Map, DestardSpot.Desc),
        (HythlothSpot.Loc, HythlothSpot.Map, HythlothSpot.Desc),
        (ShameSpot.Loc, ShameSpot.Map, ShameSpot.Desc),
        (WrongSpot.Loc, WrongSpot.Map, WrongSpot.Desc)
    };

    // SP-053: switched from Map.Ilshenar to Map.Felucca and every
    // coordinate replaced with the ticket's own classic-Britannia
    // survey - the old Ilshenar PMList coordinates (copied from
    // PublicMoongate.cs, itself Ilshenar-only data) triggered "You
    // cannot recall to another facet" for any Felucca-based vendor/
    // player, since a Runebook entry's own Map has to match the
    // recaller's facet. Chaos in particular moves from the Ilshenar
    // virtue-shrine complex to the real Britannia/Blackthorn Chaos
    // shrine the ticket names explicitly - a different, non-virtue
    // shrine, not a reuse of the Ilshenar one.
    private static readonly (Point3D Loc, Map Map, string Desc)[] ShrinesRunes =
    {
        (new Point3D(1857, 875, -1), Map.Felucca, "Shrine of Compassion"),
        (new Point3D(4217, 563, 36), Map.Felucca, "Shrine of Honesty"),
        (new Point3D(1732, 3528, 0), Map.Felucca, "Shrine of Honor"),
        (new Point3D(4274, 3698, 0), Map.Felucca, "Shrine of Humility"),
        (new Point3D(1300, 634, 16), Map.Felucca, "Shrine of Justice"),
        (new Point3D(3355, 290, 4), Map.Felucca, "Shrine of Sacrifice"),
        (new Point3D(1606, 2490, 10), Map.Felucca, "Shrine of Spirituality"),
        (new Point3D(2493, 3931, 5), Map.Felucca, "Shrine of Valor"),
        // SP-054: SP-053's (1445, 1693, 0) landed inside West Britain by
        // the bank/mint - moved to the ticket's own corrected northern-
        // Britannia coordinate (north of Minoc, the real Blackthorn's
        // Chaos shrine area), still Map.Felucca.
        (new Point3D(1456, 844, 0), Map.Felucca, "Shrine of Chaos")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] PublicMoongatesRunes =
    {
        (new Point3D(4467, 1283, 5), Map.Felucca, "Moongate: Moonglow"),
        (new Point3D(1336, 1997, 5), Map.Felucca, "Moongate: Britain"),
        (new Point3D(1499, 3771, 5), Map.Felucca, "Moongate: Jhelom"),
        (new Point3D(771, 752, 5), Map.Felucca, "Moongate: Yew"),
        (new Point3D(2701, 692, 5), Map.Felucca, "Moongate: Minoc"),
        (new Point3D(1828, 2948, -20), Map.Felucca, "Moongate: Trinsic"),
        (new Point3D(643, 2067, 5), Map.Felucca, "Moongate: Skara Brae"),
        (new Point3D(3563, 2139, 5), Map.Felucca, "Moongate: Magincia")
    };

    // SP-050: "T2A Secret Passages" from the ticket names 4 obscure T2A
    // tunnel connectors (Serpent's Hold/Fire passage, Ice Dungeon
    // passage, Terathan Keep tunnels, a Trinsic/Delucia mountain
    // passage) that have no verified coordinate anywhere in this
    // codebase. Rather than fabricate unverified numbers that risk
    // landing in a wall or void, each stop is substituted with the
    // closest already-verified real destination: the real Fire/Ice
    // dungeon entrances for the first two, and Destard/Delucia (both
    // independently verified elsewhere in this file) standing in for
    // the two genuinely unmapped stops. SP-053: automatically picks up
    // Destard's corrected entrance via the shared spot const above.
    private static readonly (Point3D Loc, Map Map, string Desc)[] T2ASecretPassagesRunes =
    {
        (FireSpot.Loc, FireSpot.Map, "Fire Passage"),
        (IceSpot.Loc, IceSpot.Map, "Ice Passage"),
        (DestardSpot.Loc, DestardSpot.Map, "Terathan Keep (via Destard)"),
        (DeluciaSpot.Loc, DeluciaSpot.Map, "Trinsic/Delucia Passage")
    };

    // SP-051: PricePerSpot here is the rate NonGMRunebookPrice (the base
    // fee) is added on top of, reverse-derived from each core book's
    // original SP-050 total (e.g. Britannia Cities' old 825gp / 11 spots
    // = 75gp/spot) so every core book's new total is exactly its old
    // SP-050 price + the base fee - matching the ticket's own worked
    // examples for all 4 civic books exactly. SP-053 changed coordinates
    // but not spot counts for any of these 5 books, so none of these
    // rates or totals change.
    private static readonly (string Name, (Point3D Loc, Map Map, string Desc)[] Runes, int PricePerSpot)[] CoreArchivistRunebooks =
    {
        ("Britannia Cities Runebook", BritanniaCitiesRunes, 75),
        ("Dungeon Entrances Runebook", DungeonEntrancesRunes, 100),
        ("Shrines Runebook", ShrinesRunes, 75),
        ("Moongate Runebook", PublicMoongatesRunes, 75),
        ("T2A Secret Passages Runebook", T2ASecretPassagesRunes, 1875)
    };

    // SP-053: real per-floor interior coordinates from the ticket's own
    // survey, replacing SP-050/051's entrance-repeated placeholders
    // (RepeatRuneSpot is no longer used anywhere in this file as of this
    // change). Entrance/Level 1 is skipped as a separate line per the
    // ticket ("entrance serves as Level 1") - each array's first entry
    // IS the dungeon's own entrance const, reused directly.
    private static readonly (Point3D Loc, Map Map, string Desc)[] DespiseFloorRunes =
    {
        (DespiseSpot.Loc, DespiseSpot.Map, DespiseSpot.Desc),
        (new Point3D(5499, 570, 59), Map.Felucca, "Despise Level 2"),
        (new Point3D(5518, 673, 20), Map.Felucca, "Despise Level 3"),
        (new Point3D(5406, 859, 45), Map.Felucca, "Despise Level 4")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] DestardFloorRunes =
    {
        (DestardSpot.Loc, DestardSpot.Map, DestardSpot.Desc),
        (new Point3D(5144, 800, 7), Map.Felucca, "Destard Level 2"),
        (new Point3D(5137, 922, 0), Map.Felucca, "Destard Level 3")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] DeceitFloorRunes =
    {
        (DeceitSpot.Loc, DeceitSpot.Map, DeceitSpot.Desc),
        (new Point3D(5309, 533, 0), Map.Felucca, "Deceit Level 2"),
        (new Point3D(5141, 649, 0), Map.Felucca, "Deceit Level 3"),
        (new Point3D(5306, 654, 0), Map.Felucca, "Deceit Level 4")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] CovetousFloorRunes =
    {
        (CovetousSpot.Loc, CovetousSpot.Map, CovetousSpot.Desc),
        (new Point3D(5613, 1997, 0), Map.Felucca, "Covetous Level 2"),
        (new Point3D(5579, 1924, 0), Map.Felucca, "Covetous Level 3"),
        (new Point3D(5467, 1808, 0), Map.Felucca, "Covetous Lake Cave"),
        (new Point3D(5552, 1808, 0), Map.Felucca, "Covetous Torture Chamber")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] HythlothFloorRunes =
    {
        (HythlothSpot.Loc, HythlothSpot.Map, HythlothSpot.Desc),
        (new Point3D(5979, 170, 0), Map.Felucca, "Hythloth Level 2"),
        (new Point3D(6085, 145, -22), Map.Felucca, "Hythloth Level 3"),
        (new Point3D(6061, 89, 22), Map.Felucca, "Hythloth Level 4")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] ShameFloorRunes =
    {
        (ShameSpot.Loc, ShameSpot.Map, ShameSpot.Desc),
        (new Point3D(5517, 13, 0), Map.Felucca, "Shame Level 2"),
        (new Point3D(5516, 150, 20), Map.Felucca, "Shame Level 3"),
        (new Point3D(5517, 174, 0), Map.Felucca, "Shame Level 4"),
        (new Point3D(5878, 18, -10), Map.Felucca, "Shame Level 5")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] WrongFloorRunes =
    {
        (WrongSpot.Loc, WrongSpot.Map, WrongSpot.Desc),
        (new Point3D(5690, 573, 25), Map.Felucca, "Wrong Level 2"),
        (new Point3D(5703, 642, 0), Map.Felucca, "Wrong Level 3")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] IceFloorRunes =
    {
        (IceSpot.Loc, IceSpot.Map, "Ice Dungeon: Britain Entrance"),
        (new Point3D(5876, 148, 22), Map.Felucca, "Ice Dungeon: T2A Entrance"),
        (new Point3D(5834, 327, 18), Map.Felucca, "Ice Dungeon: Ratman Room"),
        (new Point3D(5699, 305, 0), Map.Felucca, "Ice Dungeon: Ice Demon Lair")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] FireFloorRunes =
    {
        (FireSpot.Loc, FireSpot.Map, "Fire Dungeon: Britain Entrance"),
        (new Point3D(5790, 1416, 40), Map.Felucca, "Fire Dungeon: T2A Entrance"),
        (new Point3D(5702, 1316, 1), Map.Felucca, "Fire Dungeon Level 2")
    };

    private static readonly (Point3D Loc, Map Map, string Desc)[] OrcFloorRunes =
    {
        (OrcSpot.Loc, OrcSpot.Map, OrcSpot.Desc),
        (new Point3D(5332, 1376, 0), Map.Felucca, "Orc Cave Level 2"),
        (new Point3D(5274, 2030, 0), Map.Felucca, "Orc Cave Level 3")
    };

    // SP-053: PricePerSpot here matches the ticket's own recalibrated
    // rate table exactly (100g/spot for every dungeon except Ice/Fire at
    // 150g/spot) - e.g. Despise: 900 + 4x100 = 1,300gp, Ice Dungeon:
    // 900 + 4x150 = 1,500gp, both matching the ticket's worked totals.
    private static readonly (string Name, (Point3D Loc, Map Map, string Desc)[] Runes, int PricePerSpot)[] RareDungeonFloorRunebooks =
    {
        ("Runebook of Covetous", CovetousFloorRunes, 100),
        ("Runebook of Deceit", DeceitFloorRunes, 100),
        ("Runebook of Despise", DespiseFloorRunes, 100),
        ("Runebook of Destard", DestardFloorRunes, 100),
        ("Runebook of Hythloth", HythlothFloorRunes, 100),
        ("Runebook of Shame", ShameFloorRunes, 100),
        ("Runebook of Wrong", WrongFloorRunes, 100),
        ("Runebook of Fire Dungeon", FireFloorRunes, 150),
        ("Runebook of Ice Dungeon", IceFloorRunes, 150),
        ("Runebook of Orc Cave", OrcFloorRunes, 100)
    };

    // SP-054: replaces SP-051's 8-spot compilation (4 dungeons x 2
    // duplicated copies each, with "T2A Ophidian & Wyvern Pits" falling
    // back to the Destard entrance for lack of a real coordinate) with 5
    // unique, authentic interior boss/farming spots from the ticket's
    // own survey - no repeated entries, no entrance-fallback substitute.
    // Ophidian Lair and Wyvern Pits are now their own distinct entries
    // (previously one combined placeholder).
    private static readonly (Point3D Loc, Map Map, string Desc)[] BossFarmingLairsRunes =
    {
        (new Point3D(5188, 637, 0), Map.Felucca, "Deceit Lich Lords"),
        (new Point3D(5141, 796, 0), Map.Felucca, "Destard Ancient Wyrms"),
        (new Point3D(6090, 247, 44), Map.Felucca, "Hythloth Daemons & Balrons"),
        (new Point3D(5760, 2634, 43), Map.Felucca, "T2A Ophidian Lair"),
        (new Point3D(5695, 3687, 0), Map.Felucca, "T2A Wyvern Pits")
    };
    private const int BossFarmingLairsPricePerSpot = 750;

    // SP-054: the "T2A Passages Runebook" (SP-051) was filled with
    // doubled/redundant runes and is removed outright, not replaced -
    // the always-stocked core "T2A Secret Passages Runebook" above
    // already covers this destination family without duplication.
    private static readonly (string Name, (Point3D Loc, Map Map, string Desc)[] Runes, int PricePerSpot)[] RareConsolidatedRunebooks =
    {
        ("Boss & Farming Lairs Runebook", BossFarmingLairsRunes, BossFarmingLairsPricePerSpot)
    };

    private const double RareRunebookChance = 0.10;

    // SP-052: box retained (renamed to the ticket's simpler "Runebook
    // Libraries") - up to 16 distinct runebook varieties can appear here
    // (5 core + 10 rare-floor + 1 rare-consolidated, SP-054's own removal
    // of the redundant T2A Passages Runebook), clearing the container-
    // flattening policy's own ">12 varieties" exception.
    private static void StockArcaneArchivist(PlayerVendor vendor)
    {
        var library = CreateDisplayContainer<WoodenBox>(vendor, "Runebook Libraries", 0);

        foreach (var (name, runes, pricePerSpot) in CoreArchivistRunebooks)
        {
            var price = NonGMRunebookPrice + runes.Length * pricePerSpot;
            for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
            {
                AddDisplayItem(vendor, library, CreateMarkedRunebook(name, runes), price);
            }
        }

        foreach (var (name, runes, pricePerSpot) in RareDungeonFloorRunebooks)
        {
            if (Utility.RandomDouble() < RareRunebookChance)
            {
                var price = NonGMRunebookPrice + runes.Length * pricePerSpot;
                AddDisplayItem(vendor, library, CreateMarkedRunebook(name, runes), price);
            }
        }

        foreach (var (name, runes, pricePerSpot) in RareConsolidatedRunebooks)
        {
            if (Utility.RandomDouble() < RareRunebookChance)
            {
                var price = NonGMRunebookPrice + runes.Length * pricePerSpot;
                AddDisplayItem(vendor, library, CreateMarkedRunebook(name, runes), price);
            }
        }
    }

    // ==== RawResources =======================================================

    // SP-055: master price list - per-unit baselines for the standard
    // (non-bulk) stack tier of each commodity. The 250-count Iron/Board
    // stacks apply a flat 10% volume discount on top of these rates
    // (e.g. Iron: 8g/unit standard -> 7.2g/unit at 250, 200 -> 1,800gp),
    // matching the ticket's own worked totals exactly.
    private const int IronIngotPricePerUnit = 8;
    private const int WoodPricePerUnit = 4;
    private const int LeatherPricePerUnit = 5;
    private const int ClothPricePerUnit = 2;
    private const int ThreadYarnPricePerUnit = 10;

    private const double BulkDiscountMultiplier = 0.9;

    // SP-055: complete rebuild onto a strict zero-subcontainer policy -
    // every stack sells loose straight out of vendor.Backpack via
    // SellLoose (this file's own "drop + price via VendorItem" mechanism
    // - PlayerVendor.SetVendorItem the ticket names is private to
    // Mobiles/Vendors/PlayerVendor.cs and not reachable from here).
    // VendorGridArranger groups same-(Type, Hue, Price) drops into one
    // grid slot on its own, so this stays clean in the root view without
    // any container wrapper needed. Tools, logs, shafts, and colored/
    // non-standard leathers are excluded entirely per the ticket - this
    // archetype now sells only raw, unworked commodity stacks.
    private static void StockRawResources(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Iron Smelter
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new IronIngot(), 25), IronIngotPricePerUnit * 25);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new IronIngot(), 50), IronIngotPricePerUnit * 50);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new IronIngot(), 100), IronIngotPricePerUnit * 100);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new IronIngot(), 250), (int)(IronIngotPricePerUnit * BulkDiscountMultiplier * 250));
                }
                break;

            case 1: // Lumberjack
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Board(), 25), WoodPricePerUnit * 25);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Board(), 50), WoodPricePerUnit * 50);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Board(), 100), WoodPricePerUnit * 100);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Board(), 250), (int)(WoodPricePerUnit * BulkDiscountMultiplier * 250));
                }
                break;

            case 2: // Tanner & Weaver
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Leather(), 100), LeatherPricePerUnit * 100);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Leather(), 500), (int)(LeatherPricePerUnit * BulkDiscountMultiplier * 500));
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new SpoolOfThread(), 20), ThreadYarnPricePerUnit * 20);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new SpoolOfThread(), 50), ThreadYarnPricePerUnit * 50);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(RandomYarn(), 20), ThreadYarnPricePerUnit * 20);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(RandomYarn(), 50), ThreadYarnPricePerUnit * 50);
                }

                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Cloth(), 100), ClothPricePerUnit * 100);
                }
                break;

            default: // Rare Ore Smelter
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new DullCopperIngot(), 50), 600);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new ShadowIronIngot(), 50), 1000);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new CopperIngot(), 50), 1000);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new BronzeIngot(), 50), 1250);
                }

                SellLoose(vendor, Stack(new GoldIngot(), 20), 600);
                SellLoose(vendor, Stack(new AgapiteIngot(), 20), 700);
                SellLoose(vendor, Stack(new VeriteIngot(), 20), 900);
                SellLoose(vendor, Stack(new ValoriteIngot(), 20), 1600);
                break;
        }
    }

    // SP-055: picks one of the two plain (non-Dark/Light-specific)
    // tailoring yarn colors per stack, the same "duplicate-set variety"
    // pattern this file already uses for dyed Boots elsewhere.
    private static Item RandomYarn() => Utility.RandomBool() ? new DarkYarn() : new LightYarn();

    // ==== TailorFletcher ======================================================

    private const string MasterBowyerName = "a Master Bowyer";
    private const string MasterTailorName = "a Master Tailor";

    // SP-031: classic palette hues - real hue table entries, unlike the
    // 0x0/0x481 values this replaced (0x0 is "no hue" - it renders an
    // item's native color, not black; 0x481 is one index off the real
    // Ice White).
    private const int PureBlackHue = 0x0455;
    private const int IceWhiteHue = 0x0480;

    // SP-046: master price list - Ammunition & Bows. Regular (base
    // quality) and GM (Exceptional) tiers for each ranged weapon; ammo is
    // a flat per-unit rate with no bulk discount (unlike reagents), same
    // pattern as the Utility Inscription scrolls above.
    private const int BowBasePrice = 100;
    private const int BowGMPrice = 150;
    private const int CrossbowBasePrice = 125;
    private const int CrossbowGMPrice = 190;
    private const int HeavyCrossbowBasePrice = 200;
    private const int HeavyCrossbowGMPrice = 300;
    private const int AmmoPricePerUnit = 7;
    private const int FeatherPricePerUnit = 4;

    // SP-056: Tier 3/4 category pools - each entry is a (hue -> item)
    // factory (every clothing constructor in this codebase takes a
    // single `int hue = 0` parameter, confirmed against Items/Clothing/
    // *.cs) plus its display name. "Skullcap" from the ticket's own
    // pool list has no matching class - the real ModernUO class is
    // `SkullCap` (capital C, Items/Clothing/Hats.cs) - substituted
    // directly, same "ticket name vs. real class name" gap this file
    // has hit before (see SP-050's MeteorSwarmScroll note).
    private static readonly (Func<int, BaseClothing> Factory, string Name)[] HatPool =
    {
        (hue => new SkullCap(hue), "a skull cap"),
        (hue => new Bandana(hue), "a bandana"),
        (hue => new FloppyHat(hue), "a floppy hat"),
        (hue => new Cap(hue), "a cap"),
        (hue => new WideBrimHat(hue), "a wide brim hat"),
        (hue => new StrawHat(hue), "a straw hat"),
        (hue => new TallStrawHat(hue), "a tall straw hat"),
        (hue => new WizardsHat(hue), "a wizard's hat"),
        (hue => new Bonnet(hue), "a bonnet"),
        (hue => new FeatheredHat(hue), "a feathered hat"),
        (hue => new TricorneHat(hue), "a tricorne hat"),
        (hue => new JesterHat(hue), "a jester hat")
    };

    private static readonly (Func<int, BaseClothing> Factory, string Name)[] ShirtPool =
    {
        (hue => new Doublet(hue), "a doublet"),
        (hue => new Shirt(hue), "a shirt"),
        (hue => new FancyShirt(hue), "a fancy shirt"),
        (hue => new Tunic(hue), "a tunic"),
        (hue => new Surcoat(hue), "a surcoat"),
        (hue => new PlainDress(hue), "a plain dress")
    };

    // SP-056: only 3 real types exist for this category (the ticket's
    // own "3-4 distinct types" request can never yield 4 from a 3-item
    // pool) - StockClothingBox's own pool.Length guard below handles
    // this the same safe way it handles Footwear's "always all 4"
    // request, rather than risking Utility.RandomSample hanging forever
    // trying to draw more unique picks than the pool actually has.
    private static readonly (Func<int, BaseClothing> Factory, string Name)[] PantsPool =
    {
        (hue => new ShortPants(hue), "a pair of short pants"),
        (hue => new LongPants(hue), "a pair of long pants"),
        (hue => new Kilt(hue), "a kilt")
    };

    private static readonly (Func<int, BaseClothing> Factory, string Name)[] MiscAttirePool =
    {
        (hue => new Skirt(hue), "a layered skirt"),
        (hue => new Cloak(hue), "a cloak"),
        (hue => new Robe(hue), "a robe"),
        (hue => new JesterSuit(hue), "a jester suit"),
        (hue => new FancyDress(hue), "a fancy dress"),
        (hue => new BodySash(hue), "a body sash"),
        (hue => new HalfApron(hue), "a half apron"),
        (hue => new FullApron(hue), "a full apron")
    };

    private static readonly (Func<int, BaseClothing> Factory, string Name)[] FootwearPool =
    {
        (hue => new Sandals(hue), "a pair of sandals"),
        (hue => new Shoes(hue), "a pair of shoes"),
        (hue => new Boots(hue), "a pair of boots"),
        (hue => new ThighBoots(hue), "a pair of thigh boots")
    };

    // SP-056: per-category base price ranges, reverse-derived from the
    // ticket's own worked examples so each anchor lands inside its
    // category's range (Hats 70-90 per the ticket's own number; Sandals
    // ~60 and ThighBoots priced up from there for Footwear 55-90; Cloak
    // ~85 and Robe ~100 for Misc Attire 60-100; "standard shirts/pants
    // ~50-75" applies directly to both Shirts and Pants). Tier 4 adds
    // RareHueSurcharge flat on top of the same ranges per the ticket's
    // own "+300gp" instruction.
    private const int HatPriceMin = 70;
    private const int HatPriceMax = 90;
    private const int ShirtPantsPriceMin = 50;
    private const int ShirtPantsPriceMax = 75;
    private const int MiscAttirePriceMin = 60;
    private const int MiscAttirePriceMax = 100;
    private const int FootwearPriceMin = 55;
    private const int FootwearPriceMax = 90;
    private const int RareHueSurcharge = 300;

    // SP-056: ordinary dye-tub-range hues for Tier 3 (Master Clothier) -
    // deliberately excludes every hue in RareHuePalette below (neon,
    // pure black, blaze, ice), per the ticket's own "avoiding neon, pure
    // black, blaze, and ice ranges" instruction. 0x021/0x059 are already
    // this file's own established "ordinary dye" values (SP-031's
    // "dyed footwear" stock); the rest are standard mid-range cloth
    // dye-tub hues from the same classic palette.
    private static readonly int[] StandardClothHues = { 0x021, 0x044, 0x059, 0x066, 0x08C, 0x0A5, 0x0BB, 0x03F };

    // SP-056: curated rare-hue palette for Tier 4. Ice White reuses this
    // file's own already-established, corrected IceWhiteHue (0x480) -
    // the ticket's own "0x481" is the exact off-by-one value SP-031's
    // header comment on IceWhiteHue already flagged as wrong (0x481
    // isn't the real Ice White). The ticket's Charcoal/Neon Pink each
    // give two overlapping options (0x497/0x455, 0x498/0x497) that
    // collide with Pure Black and each other - one non-colliding value
    // was kept per hue so all 8 curated hues stay distinct.
    private const int IceBlueHue = 0x482;
    private const int NeonPinkHue = 0x498;
    private const int BlazeHue = 0x489;
    private const int NeonGreenHue = 0x483;
    private const int CharcoalHue = 0x497;
    private const int AcidGreenHue = 0x485;

    private static readonly int[] RareHuePalette =
    {
        PureBlackHue, IceWhiteHue, IceBlueHue, NeonPinkHue, BlazeHue, NeonGreenHue, CharcoalHue, AcidGreenHue
    };

    private const double RareDyeTubChance = 0.02;

    private static void StockTailorFletcher(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Bowyer / Fletcher - SP-046: master price list Regular/GM tiers, SP-031's
                    // duplicate-set pattern retained for stock variety.
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Named(new Bow(), "a bow"), BowBasePrice);
                }
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    AddGMItem(vendor, new Bow(), "a bow", BowGMPrice, MasterBowyerName);
                }

                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Named(new HeavyCrossbow(), "a heavy crossbow"), HeavyCrossbowBasePrice);
                }
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    AddGMItem(vendor, new HeavyCrossbow(), "a heavy crossbow", HeavyCrossbowGMPrice, MasterBowyerName);
                }

                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Named(new Crossbow(), "a crossbow"), CrossbowBasePrice);
                }
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    AddGMItem(vendor, new Crossbow(), "a crossbow", CrossbowGMPrice, MasterBowyerName);
                }

                for (var i = 0; i < 2; i++)
                {
                    AddGMItem(vendor, new CompositeBow(), "a composite bow", 210, MasterBowyerName);
                }

                // SP-046: master price list - flat 7g/unit, no bulk discount.
                // Both the 500-stack (bulk supplier quantity) and the
                // ticket's own 100-pack reference size are offered.
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Arrow(), 500), AmmoPricePerUnit * 500);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Stack(new Arrow(), 100), AmmoPricePerUnit * 100);
                }
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Bolt(), 500), AmmoPricePerUnit * 500);
                }
                for (var i = 0; i < 2; i++)
                {
                    SellLoose(vendor, Stack(new Bolt(), 100), AmmoPricePerUnit * 100);
                }

                // SP-046: master price list raw baseline - fletching feathers.
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Feather(), 100), FeatherPricePerUnit * 100);
                }
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

            case 2: // Master Clothier - SP-056: 5 categorized WoodenBox displays, 3-4 GM/
                    // Exceptional pieces per chosen style, standard (non-rare) cloth hues.
                StockClothingBox(vendor, "Hats", HatPool, 4, 3, 4, HatPriceMin, HatPriceMax, false);
                StockClothingBox(vendor, "Shirts", ShirtPool, 4, 3, 4, ShirtPantsPriceMin, ShirtPantsPriceMax, false);
                StockClothingBox(
                    vendor, "Pants", PantsPool, Utility.RandomMinMax(3, 4), 3, 4,
                    ShirtPantsPriceMin, ShirtPantsPriceMax, false
                );
                StockClothingBox(vendor, "Misc Attire", MiscAttirePool, 4, 3, 4, MiscAttirePriceMin, MiscAttirePriceMax, false);
                StockClothingBox(
                    vendor, "Footwear", FootwearPool, FootwearPool.Length, 3, 4,
                    FootwearPriceMin, FootwearPriceMax, false
                );
                break;

            default: // High-Fashion & Rare Hue Specialist - SP-056: same 5 boxes, 1-2 pieces per
                     // chosen style, curated rare hues, +300gp surcharge, 2% rare Dye Tub roll.
                StockClothingBox(vendor, "Hats", HatPool, 4, 1, 2, HatPriceMin, HatPriceMax, true);
                StockClothingBox(vendor, "Shirts", ShirtPool, 4, 1, 2, ShirtPantsPriceMin, ShirtPantsPriceMax, true);
                StockClothingBox(
                    vendor, "Pants", PantsPool, Utility.RandomMinMax(3, 4), 1, 2,
                    ShirtPantsPriceMin, ShirtPantsPriceMax, true
                );
                StockClothingBox(vendor, "Misc Attire", MiscAttirePool, 4, 1, 2, MiscAttirePriceMin, MiscAttirePriceMax, true);
                StockClothingBox(
                    vendor, "Footwear", FootwearPool, FootwearPool.Length, 1, 2,
                    FootwearPriceMin, FootwearPriceMax, true
                );

                // SP-056: independent 2% restock roll for one high-value
                // curated-rare-hue Dye Tub, sold loose in root rather than
                // inside a box - a single rare drop doesn't need its own
                // display organizer.
                if (Utility.RandomDouble() < RareDyeTubChance)
                {
                    var tub = new DyeTub { DyedHue = Utility.RandomList(RareHuePalette), Amount = 1 };
                    SellLoose(vendor, tub, Utility.RandomMinMax(100, 300) * 1000);
                }
                break;
        }
    }

    // SP-056: sets Name/Quality/Crafter/Amount the same way ApplyExceptional
    // does for BaseArmor/BaseWeapon above, but for BaseClothing - a THIRD
    // distinct quality enum (ClothingQuality, not ArmorQuality/WeaponQuality),
    // so it needs its own helper rather than extending ApplyExceptional's
    // switch.
    private static T ApplyClothingQuality<T>(T item, string name, string crafterName = DefaultCrafterName) where T : BaseClothing
    {
        item.Name = name;
        item.Quality = ClothingQuality.Exceptional;
        item.Crafter = crafterName;
        item.Amount = 1;
        return item;
    }

    // SP-056: builds one categorized display box and populates it from a
    // (hue -> item) factory pool. typesToPick >= pool.Length falls back to
    // the whole pool directly instead of calling RandomSample - besides
    // being exactly what "pick all N types" (Footwear) means, this also
    // guards Pants' 3-item pool against a typesToPick of 4: Utility.
    // RandomSample loops until it draws `count` DISTINCT indices, so
    // asking for more than the pool actually has would spin forever and
    // hang the single-threaded game loop.
    private static void StockClothingBox(
        PlayerVendor vendor, string boxName, (Func<int, BaseClothing> Factory, string Name)[] pool,
        int typesToPick, int itemsPerTypeMin, int itemsPerTypeMax, int priceMin, int priceMax, bool rare
    )
    {
        var box = CreateDisplayContainer<WoodenBox>(vendor, boxName, 0);
        var picks = typesToPick >= pool.Length ? pool : pool.RandomSample(typesToPick);

        foreach (var (factory, name) in picks)
        {
            for (var i = Utility.RandomMinMax(itemsPerTypeMin, itemsPerTypeMax); i > 0; i--)
            {
                var hue = Utility.RandomList(rare ? RareHuePalette : StandardClothHues);
                var item = ApplyClothingQuality(factory(hue), name, MasterTailorName);
                var price = Utility.RandomMinMax(priceMin, priceMax) + (rare ? RareHueSurcharge : 0);
                AddDisplayItem(vendor, box, item, price);
            }
        }
    }

    // ==== TinkerCarpenter =====================================================

    // SP-057: 4-tier rebuild - Tier 1 Master Tinker (tools/gadgets), Tier 2
    // Apprentice Carpenter (furniture/storage), Tier 3 Master Woodworker
    // (shelving/instruments/utility), Tier 4 Architect & Outfitter (house
    // addon deeds). Every pool sells loose straight out of vendor.Backpack
    // (12 varieties for Tiers 1-3, 8 for Tier 4 - at or under the
    // container-flattening policy's own ">12 varieties" box exception, and
    // consistent with this archetype's own pre-existing all-root layout).
    //
    // Class-name verification against the live ModernUO source (several of
    // the ticket's own pool names don't match a real class 1:1):
    //   - Tier 2 "WoodenStool"/"Footstool"/"SmallTable"/"WritingDesk" don't
    //     exist - the real classes are `Stool`, `FootStool` (capital S),
    //     `PlainLowTable` (the real small/plain table - no generic
    //     "SmallTable" class exists; `LargeTable` already covers the large
    //     side), and `WritingTable` (matches this file's own pre-existing
    //     Carpenter pool usage).
    //   - Tier 3 "Bookcase"/"OpenKeg"/"Drum" don't exist - real classes are
    //     `FullBookcase` (matches this file's own pre-existing usage),
    //     `Keg` (no "open" variant exists; `Keg` is the same real container
    //     class this file's old CarpenterFurniturePool already sold), and
    //     `Drums` (plural).
    //   - Tier 4: every deed in ModernUO ships as directional East/South
    //     pairs except SmallForgeDeed/PentagramDeed/AbbatoirDeed/
    //     BallotBoxDeed, which have no direction variant at all - the
    //     ticket's own "(East or South deed)" notes for Anvil/SmallBed/
    //     LargeBed are honored by rolling one at spawn time; "DartOffDeed"
    //     doesn't exist under either of the ticket's own two guesses - the
    //     real class is `DartBoardEastDeed`/`DartBoardSouthDeed`. The
    //     ticket's own "AbbatoirDeed" spelling is confirmed correct
    //     (matches this file's pre-existing usage) - `AbattoirDeed` does
    //     not exist.
    //   - Every Tier 1-3 price below is the ticket's own explicit number,
    //     deliberately superseding SP-046's older master-list figures for
    //     the same deeds where they differ (e.g. SmallForgeDeed 1,860 ->
    //     1,490) - this ticket's own price table takes precedence.
    // SP-058: Quality tag drives per-item GM/Exceptional treatment in the
    // Master Tinker tier. Real inheritance verified against the live
    // ModernUO source rather than trusting the ticket's own "BaseTool"
    // example groupings - 2 items were misclassified there: `Pickaxe` is
    // actually a BaseWeapon (BaseAxe), not a BaseTool, and `Scissors` is
    // a plain Item with no quality mechanism at all, neither a BaseTool
    // nor a BaseWeapon. `Shovel` is BaseHarvestTool rather than BaseTool
    // directly, but exposes the exact same `Quality`/`Crafter` shape
    // (its own independently-declared ToolQuality property, not
    // inherited from BaseTool), so it's grouped under Tool below.
    private enum ToolQualityKind
    {
        None,
        Tool,
        Weapon
    }

    private const string MasterTinkerCraftName = "a Master Tinkerer";

    private static readonly (Func<Item> Factory, string Name, int Price, ToolQualityKind Quality)[] TinkerToolPool =
    {
        (() => new Lockpick(), "a lockpick", 25, ToolQualityKind.None),
        (() => new Shovel(), "a shovel", 95, ToolQualityKind.Tool),
        (() => new Pickaxe(), "a pickaxe", 95, ToolQualityKind.Weapon),
        (() => new SewingKit(), "a sewing kit", 50, ToolQualityKind.Tool),
        (() => new MortarPestle(), "a mortar and pestle", 75, ToolQualityKind.Tool),
        (() => new ScribesPen(), "a scribe's pen", 25, ToolQualityKind.Tool),
        (() => new TinkerTools(), "a set of tinker tools", 50, ToolQualityKind.Tool),
        (() => new Scissors(), "a pair of scissors", 50, ToolQualityKind.None),
        (() => new Tongs(), "a pair of tongs", 25, ToolQualityKind.Tool),
        (() => new Hammer(), "a hammer", 95, ToolQualityKind.Tool),
        (() => new Saw(), "a saw", 95, ToolQualityKind.Tool),
        (() => new DovetailSaw(), "a dovetail saw", 95, ToolQualityKind.Tool),
        (() => new DrawKnife(), "a draw knife", 95, ToolQualityKind.Tool),
        (() => new ButcherKnife(), "a butcher knife", 50, ToolQualityKind.Weapon),
        (() => new Cleaver(), "a cleaver", 50, ToolQualityKind.Weapon),
        (() => new SkinningKnife(), "a skinning knife", 50, ToolQualityKind.Weapon),
        (() => new Key(), "a key", 75, ToolQualityKind.None),
        (() => new KeyRing(), "a keyring", 75, ToolQualityKind.None),
        (() => new Clock(), "a clock", 145, ToolQualityKind.None),
        (() => new Sextant(), "a sextant", 120, ToolQualityKind.None),
        (() => new Spyglass(), "a spyglass", 95, ToolQualityKind.None),
        (() => new Globe(), "a globe", 95, ToolQualityKind.None)
    };

    // SP-058: BaseTool and BaseHarvestTool each independently declare
    // their own Quality (ToolQuality)/Crafter pair (no shared base class
    // or interface exposes them together), so both need their own case
    // arm here - a FOURTH distinct quality enum in this file, alongside
    // ArmorQuality/WeaponQuality (ApplyExceptional) and ClothingQuality
    // (SP-056's ApplyClothingQuality).
    private static void ApplyToolQuality(Item item, string name, string crafterName)
    {
        item.Name = name;

        switch (item)
        {
            case BaseTool tool:
                tool.Quality = ToolQuality.Exceptional;
                tool.Crafter = crafterName;
                break;
            case BaseHarvestTool harvest:
                harvest.Quality = ToolQuality.Exceptional;
                harvest.Crafter = crafterName;
                break;
        }
    }

    // SP-058: dedicated Tier 1 stocking path (rather than the shared
    // StockToolPool every other tier uses) since only Master Tinker
    // needs per-item quality branching - Tiers 2-4's furniture/
    // instruments/deeds never carry Quality/Crafter in this ticket.
    private static void StockTinkerToolPool(PlayerVendor vendor, int typesToPick, int unitsMin, int unitsMax)
    {
        var picks = typesToPick >= TinkerToolPool.Length ? TinkerToolPool : TinkerToolPool.RandomSample(typesToPick);

        foreach (var (factory, name, price, quality) in picks)
        {
            for (var i = Utility.RandomMinMax(unitsMin, unitsMax); i > 0; i--)
            {
                var item = factory();
                item.Amount = 1;

                switch (quality)
                {
                    case ToolQualityKind.Tool:
                        ApplyToolQuality(item, name, MasterTinkerCraftName);
                        break;
                    case ToolQualityKind.Weapon:
                        ApplyExceptional(item, name, MasterTinkerCraftName);
                        break;
                    default:
                        item.Name = name;
                        break;
                }

                SellLoose(vendor, item, price);
            }
        }
    }

    // Guardrail: no BaseAddonDeed anywhere in this pool - furniture and
    // storage only, per the ticket's own explicit exclusion.
    private static readonly (Func<Item> Factory, string Name, int Price)[] CarpenterFurniturePool =
    {
        (() => new WoodenChair(), "a wooden chair", 70),
        (() => new BambooChair(), "a bamboo chair", 70),
        (() => new Stool(), "a wooden stool", 50),
        (() => new FootStool(), "a footstool", 50),
        (() => new WoodenBench(), "a wooden bench", 180),
        (() => new PlainLowTable(), "a small table", 200),
        (() => new LargeTable(), "a large table", 325),
        (() => new WritingTable(), "a writing desk", 200),
        (() => new WoodenBox(), "a wooden box", 50),
        (() => new SmallCrate(), "a small crate", 95),
        (() => new MediumCrate(), "a medium crate", 180),
        (() => new LargeCrate(), "a large crate", 215),
        (() => new WoodenChest(), "a wooden chest", 240)
    };

    // Guardrail: no BaseAddonDeed anywhere in this pool either.
    private static readonly (Func<Item> Factory, string Name, int Price)[] WoodworkerPool =
    {
        (() => new RedArmoire(), "a red armoire", 420),
        (() => new Armoire(), "an armoire", 420),
        (() => new FullBookcase(), "a bookcase", 300),
        (() => new BarrelStaves(), "barrel staves", 60),
        (() => new BarrelLid(), "a barrel lid", 60),
        (() => new Keg(), "a keg", 350),
        (() => new LapHarp(), "a lap harp", 300),
        (() => new Lute(), "a lute", 360),
        (() => new Drums(), "a set of drums", 300),
        (() => new Tambourine(), "a tambourine", 240),
        (() => new TambourineTassel(), "a tambourine with tassel", 270),
        (() => new WoodenShield(), "a wooden shield", 155),
        (() => new Club(), "a club", 70),
        (() => new GnarledStaff(), "a gnarled staff", 85),
        (() => new QuarterStaff(), "a quarterstaff", 70)
    };

    private static readonly (Func<Item> Factory, string Name, int Price)[] ArchitectDeedPool =
    {
        (() => new SmallForgeDeed(), "a small forge deed", 1490),
        (() => new LargeForgeEastDeed(), "a large forge deed", 2930),
        (() => Utility.RandomBool() ? (Item)new AnvilEastDeed() : new AnvilSouthDeed(), "an anvil deed", 2930),
        (() => new LoomEastDeed(), "a loom deed", 935),
        (() => new SpinningWheelEastDeed(), "a spinning wheel deed", 840),
        (() => new TrainingDummyEastDeed(), "a training dummy deed", 815),
        (() => new PickpocketDipEastDeed(), "a pickpocket dip deed", 910),
        (() => new FlourMillEastDeed(), "a flour mill deed", 1920),
        (() => new StoneOvenEastDeed(), "a stone oven deed", 1730),
        (() => new WaterTroughEastDeed(), "a water trough deed", 1535),
        (() => new PentagramDeed(), "a pentagram deed", 2305),
        (() => new AbbatoirDeed(), "an abattoir deed", 2305),
        (() => new BallotBoxDeed(), "a ballot box deed", 70),
        (() => Utility.RandomBool() ? (Item)new DartBoardEastDeed() : new DartBoardSouthDeed(), "a dart board deed", 70),
        (() => Utility.RandomBool() ? (Item)new SmallBedEastDeed() : new SmallBedSouthDeed(), "a small bed deed", 1440),
        (() => Utility.RandomBool() ? (Item)new LargeBedEastDeed() : new LargeBedSouthDeed(), "a large bed deed", 2160)
    };

    // SP-057: shared stocking primitive for all 4 tiers - picks
    // typesToPick distinct varieties (falling back to the whole pool
    // whenever typesToPick >= pool.Length, the same RandomSample-hang
    // guard SP-056's StockClothingBox uses) and drops unitsMin-unitsMax
    // loose, individually-priced copies of each straight into root.
    private static void StockToolPool(
        PlayerVendor vendor, (Func<Item> Factory, string Name, int Price)[] pool,
        int typesToPick, int unitsMin, int unitsMax
    )
    {
        var picks = typesToPick >= pool.Length ? pool : pool.RandomSample(typesToPick);
        foreach (var (factory, name, price) in picks)
        {
            for (var i = Utility.RandomMinMax(unitsMin, unitsMax); i > 0; i--)
            {
                var item = factory();
                item.Amount = 1;
                SellLoose(vendor, Named(item, name), price);
            }
        }
    }

    private static void StockTinkerCarpenter(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Tier 1: Master Tinker - 12 of 22 tool/gadget types, 3-4 units each,
                    // SP-058 GM/Exceptional quality on real tools and bladed weapons
                StockTinkerToolPool(vendor, 12, 3, 4);
                break;

            case 1: // Tier 2: Apprentice Carpenter - 12 of 13 furniture/storage types, 3-4 units each
                StockToolPool(vendor, CarpenterFurniturePool, 12, 3, 4);
                break;

            case 2: // Tier 3: Master Woodworker - 12 of 15 shelving/instrument/utility types, 3-4 units each
                StockToolPool(vendor, WoodworkerPool, 12, 3, 4);
                break;

            default: // Tier 4: Architect & Outfitter - 8 of 16 addon deed types, 2-3 units each
                StockToolPool(vendor, ArchitectDeedPool, 8, 2, 3);
                break;
        }
    }

    // ==== FisherCurioBaker ====================================================

    // SP-059: full 4-tier rebuild - Tier 1 Master Baker & Provisioner
    // (bulk cooked food), Tier 2 Fishmonger & Cartographer (fish steaks,
    // fishing poles, cartography maps), Tier 3 Cellar Master & Tavern
    // Provisioner (kegs, roast pig, flour, cheese, honey, pitchers),
    // Tier 4 Grand Shipwright & Nautical Explorer (boat deeds, trophy
    // fish, navigation tools). Every stack/unit sells loose straight out
    // of vendor.Backpack - zero sub-containers anywhere in this
    // archetype, replacing the old Tier 1/2's "Basket of Cooked Ribs
    // (50)"/"Basket of Meat Pies (50)" packaged subcontainers.
    //
    // Class-name verification against the live ModernUO source (several
    // of the ticket's own names don't match a real class 1:1):
    //   - Tier 1: "CookedRibs" doesn't exist - the real class is `Ribs`
    //     (already this file's own pre-existing name for the same real
    //     item). "PeachPie" doesn't exist - `FruitPie` is the real
    //     second (non-apple) pie class. "Muffin" doesn't exist - the
    //     real class is `Muffins` (plural). "Pizza" doesn't exist - the
    //     real classes are `CheesePizza`/`SausagePizza` (no generic
    //     "Pizza"); one is picked at random per stack rather than
    //     arbitrarily favoring one.
    //   - Tier 3: "SackOfFlour"/"JarOfHoney"/"BeveragePitcher" don't
    //     exist - the real classes are `SackFlour`/`JarHoney` (word
    //     order swapped from the ticket's own names) and the real
    //     beverage-vessel class is simply `Pitcher` (takes a
    //     `BeverageType` constructor arg, the same pattern this file's
    //     own pre-existing `Jug(BeverageType)` calls already use).
    //     "Kegs of Ale/Wine/Cider": no dedicated beverage-filled keg
    //     class exists anywhere in ModernUO (confirmed - only the
    //     magic-only `PotionKeg` and the plain decorative `Keg`
    //     container exist) - the real `Keg` furniture class is used
    //     directly, individually named per drink via `Named()` since it
    //     has no BeverageType mechanism of its own to carry that detail.
    //   - Tier 4: "SmallShipDeed"/"SmallDragonShipDeed"/"MediumShipDeed"
    //     don't exist - ModernUO names every boat deed "Boat", not
    //     "Ship" (`SmallBoatDeed`/`SmallDragonBoatDeed`/`MediumBoatDeed`,
    //     confirmed against Multis/Boats/ - no "*ShipDeed" class exists
    //     anywhere in the engine).
    private static void StockFisherCurioBaker(PlayerVendor vendor, int slot)
    {
        switch (slot)
        {
            case 0: // Tier 1: Master Baker & Provisioner
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Ribs(), 50), 200);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new Ribs(), 100), 400);
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new CookedBird(), 50), 175);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new CookedBird(), 100), 350);
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new BreadLoaf(), 10), 80);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new BreadLoaf(), 40), 320);
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new MeatPie(), 10), 150);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new MeatPie(), 40), 600);
                }

                foreach (Func<Item> pieFactory in new Func<Item>[] { () => new ApplePie(), () => new FruitPie() })
                {
                    for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                    {
                        SellLoose(vendor, Stack(pieFactory(), 10), 200);
                    }
                    for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                    {
                        SellLoose(vendor, Stack(pieFactory(), 40), 800);
                    }
                }

                foreach (Func<Item> bakedFactory in new Func<Item>[]
                         { () => new Muffins(), () => Utility.RandomBool() ? new CheesePizza() : new SausagePizza() })
                {
                    for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                    {
                        SellLoose(vendor, Stack(bakedFactory(), 10), 120);
                    }
                    for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                    {
                        SellLoose(vendor, Stack(bakedFactory(), 40), 480);
                    }
                }
                break;

            case 1: // Tier 2: Fishmonger & Cartographer
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new RawFishSteak(), 100), 200);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new RawFishSteak(), 500), 1000);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new FishSteak(), 100), 300);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new FishSteak(), 500), 1500);
                }

                // Guardrail: pre-T2A plain FishingPole only - no durability
                // properties, resource tags, or MIB/SOS/green-net mechanics.
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    SellLoose(vendor, new FishingPole(), 35);
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, new LocalMap(), 30);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, new CityMap(), 50);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, new SeaChart(), 75);
                }
                for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
                {
                    SellLoose(vendor, new WorldMap(), 150);
                }
                break;

            case 2: // Tier 3: Cellar Master & Tavern Provisioner
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Named(new Keg(), "a keg of ale"), 250);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Named(new Keg(), "a keg of wine"), 250);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Named(new Keg(), "a keg of cider"), 250);
                }

                for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
                {
                    SellLoose(vendor, new RoastPig(), 150);
                }
                for (var i = Utility.RandomMinMax(3, 4); i > 0; i--)
                {
                    SellLoose(vendor, new SackFlour(), 20);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new CheeseWheel(), 10), 80);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, Stack(new JarHoney(), 10), 50);
                }

                for (var i = Utility.RandomMinMax(4, 6); i > 0; i--)
                {
                    var beverage = Utility.RandomList(BeverageType.Liquor, BeverageType.Ale, BeverageType.Wine);
                    SellLoose(vendor, new Pitcher(beverage), 20);
                }
                break;

            default: // Tier 4: Grand Shipwright & Nautical Explorer
                SellLoose(vendor, new SmallBoatDeed(), 12500);
                SellLoose(vendor, new SmallDragonBoatDeed(), 15000);
                SellLoose(vendor, new MediumBoatDeed(), 18000);

                for (var i = Utility.RandomMinMax(1, 2); i > 0; i--)
                {
                    var bigFish = new BigFish { Weight = Utility.RandomMinMax(100, 200) };
                    SellLoose(vendor, bigFish, 2500);
                }

                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, new Spyglass(), 95);
                }
                for (var i = Utility.RandomMinMax(2, 3); i > 0; i--)
                {
                    SellLoose(vendor, new Sextant(), 120);
                }
                break;
        }
    }

    // ==== Vendor Theming (SP-033/SP-034) ======================================
    //
    // Called once per spawn, after StockVendor - sets an overhead trade
    // title (Mobile.Title, renders as "Name the Title") and swaps in
    // themed apparel on top of whatever PlayerVendor.InitOutfit already
    // equipped. Every layer this touches is cleared first
    // (FindItemOnLayer(...)?.Delete()) rather than relying on AddItem to
    // resolve a same-layer conflict on its own - InitOutfit's own base
    // outfit (FancyShirt/LongPants/BodySash/Boots/Cloak) already occupies
    // several of the layers a themed piece needs.
    //
    // SP-034: keyed on (archetype, slot) instead of archetype alone - a
    // shop's own Vendor 1..4 sell genuinely different things (StockVendor's
    // own per-slot switch), so "the Weaponsmith" standing at the Shield
    // Specialist's counter read as wrong. slot = vendorIndex % 4, the same
    // wrap StockVendor itself uses, so slot 3 (and any overflow past 4 on
    // an odd house style) always lands on each archetype's own default/
    // "Specialty" title.
    //
    // Deliberately visual-only: no Say/PublicOverheadMessage/SayTo call
    // anywhere here or anywhere else this touches - vendors stay
    // completely silent beyond whatever reactive shop dialogue
    // PlayerVendor's own core transaction handling already speaks (out of
    // scope to touch - that's Mobiles/Vendors/PlayerVendor.cs, not this
    // directory).
    // SP-049: effectiveSlot must be the exact value StockVendor's own
    // DetermineEffectiveSlot already resolved for this same stocking
    // pass (its return value) - title/apparel has to match whatever
    // tier actually got stocked, and a 1-vendor shop's 50/50 roll only
    // stays consistent if both calls share one roll instead of each
    // independently re-rolling its own coin flip.
    public static void ApplyVendorTheme(PlayerVendor vendor, MarketArchetype archetype, int effectiveSlot)
    {
        if (vendor == null)
        {
            return;
        }

        var (apparel, title) = (archetype, effectiveSlot) switch
        {
            (MarketArchetype.BlacksmithArmory, 0) => (BlacksmithWeaponsmithApparel(), "the Weaponsmith"),
            (MarketArchetype.BlacksmithArmory, 1) => (BlacksmithArmorerApparel(), "the Armorer"),
            (MarketArchetype.BlacksmithArmory, 2) => (BlacksmithColoredArmorerApparel(), "the Colored Armorer"),
            (MarketArchetype.BlacksmithArmory, _) => (BlacksmithMagicSlayerSpecialistApparel(), "the Magic & Slayer Specialist"),

            (MarketArchetype.MageApothecary, 0) => (MageHerbalistApparel(), "the Herbalist"),
            (MarketArchetype.MageApothecary, 1) => (MagePotionBrewerApparel(), "the Potion Brewer"),
            (MarketArchetype.MageApothecary, 2) => (MageMasterBrewerApparel(), "the Master Brewer"),
            (MarketArchetype.MageApothecary, _) => (MageWandMerchantApparel(), "the Wand Merchant"),

            (MarketArchetype.ScribeLibrary, 0) => (ScribeApprenticeApparel(), "the Scribe Apprentice"),
            (MarketArchetype.ScribeLibrary, 1) => (ScribeScholarApparel(), "the Scholar & Spellbook Binder"),
            (MarketArchetype.ScribeLibrary, 2) => (ScribeMasterApparel(), "the Master Scribe"),
            (MarketArchetype.ScribeLibrary, _) => (ScribeArchivistApparel(), "the Arcane Archivist"),

            // SP-055: slot 0/1 swapped from the pre-SP-055 mapping (was
            // Lumberjack/Miner) to match the ticket's own tier order -
            // Tier 1 Iron Smelter, Tier 2 Lumberjack.
            (MarketArchetype.RawResources, 0) => (ResourcesIronSmelterApparel(), "the Iron Smelter"),
            (MarketArchetype.RawResources, 1) => (ResourcesLumberjackApparel(), "the Lumberjack"),
            (MarketArchetype.RawResources, 2) => (ResourcesTannerApparel(), "the Tanner & Weaver"),
            (MarketArchetype.RawResources, _) => (ResourcesRareOreSmelterApparel(), "the Rare Ore Smelter"),

            (MarketArchetype.TailorFletcher, 0) => (TailorBowyerApparel(), "the Bowyer & Fletcher"),
            (MarketArchetype.TailorFletcher, 1) => (TailorLeatherworkerApparel(), "the Leatherworker"),
            (MarketArchetype.TailorFletcher, 2) => (TailorClothierApparel(), "the Master Clothier"),
            (MarketArchetype.TailorFletcher, _) => (TailorMasterDyerApparel(), "the High-Fashion & Rare Hue Specialist"),

            // SP-057: slot 0/1 apparel swapped to match the ticket's own tier
            // content swap (Tier 1 is now tools, Tier 2 is now furniture).
            (MarketArchetype.TinkerCarpenter, 0) => (TinkerMasterTinkerApparel(), "the Master Tinker"),
            (MarketArchetype.TinkerCarpenter, 1) => (TinkerApprenticeCarpenterApparel(), "the Apprentice Carpenter"),
            (MarketArchetype.TinkerCarpenter, 2) => (TinkerMasterWoodworkerApparel(), "the Master Woodworker"),
            (MarketArchetype.TinkerCarpenter, _) => (TinkerArchitectOutfitterApparel(), "the Architect & Outfitter"),

            // SP-059: slot 0/1 apparel swapped from the pre-existing mapping
            // to match the new tier content (Tier 1 is now the Baker, Tier 2
            // is now the Fishmonger - the reverse of the old Deep Sea
            // Fisher/Tavern Cook order).
            (MarketArchetype.FisherCurioBaker, 0) => (FisherMasterBakerApparel(), "the Master Baker & Provisioner"),
            (MarketArchetype.FisherCurioBaker, 1) => (FisherFishmongerCartographerApparel(), "the Fishmonger & Cartographer"),
            (MarketArchetype.FisherCurioBaker, 2) => (FisherCellarMasterApparel(), "the Cellar Master & Tavern Provisioner"),
            (MarketArchetype.FisherCurioBaker, _) => (FisherGrandShipwrightApparel(), "the Grand Shipwright & Nautical Explorer"),

            _ => (Array.Empty<Item>(), null)
        };

        vendor.Title = title;

        foreach (var item in apparel)
        {
            vendor.FindItemOnLayer(item.Layer)?.Delete();
            vendor.AddItem(item);
        }
    }

    // A handful of tool classes (BaseTool subclasses that aren't also
    // weapons, e.g. MortarPestle/TinkerTools) don't set their own hand
    // Layer the way BaseWeapon/BaseArmor subclasses already do (both
    // derive it from tiledata in their own base constructor - `Layer =
    // (Layer)ItemData.Quality;` - so Hatchet/Pickaxe/Bow/HeaterShield/
    // Buckler/Katana all equip correctly with zero extra work here). This
    // forces a hand Layer on so a plain BaseTool prop actually equips
    // instead of silently failing to render as held.
    private static T Held<T>(T item) where T : Item
    {
        item.Layer = Layer.OneHanded;
        return item;
    }

    // ---- BlacksmithArmory ---------------------------------------------------

    private static Item[] BlacksmithWeaponsmithApparel() =>
    [
        new FullApron(),
        Held(new SmithHammer()),
        new RingmailLegs()
    ];

    private static Item[] BlacksmithArmorerApparel() =>
    [
        new FullApron(),
        Held(new SmithHammer()),
        new ChainCoif()
    ];

    private static Item[] BlacksmithColoredArmorerApparel() =>
    [
        new HalfApron(),
        Held(new SmithHammer()),
        new PlateGloves { Resource = CraftResource.Bronze }
    ];

    private static Item[] BlacksmithMagicSlayerSpecialistApparel() =>
    [
        new FullApron(),
        new RingmailLegs(),
        Held(new Katana())
    ];

    // ---- MageApothecary ------------------------------------------------------

    private static Item[] MageHerbalistApparel() =>
    [
        new Robe(),
        Held(new MortarPestle())
    ];

    private static Item[] MagePotionBrewerApparel() =>
    [
        new Robe { Hue = 0x489 },
        Held(new MortarPestle())
    ];

    private static Item[] MageMasterBrewerApparel() =>
    [
        new HalfApron(),
        new Boots()
    ];

    private static Item[] MageWandMerchantApparel() =>
    [
        new Robe { Hue = 0x489 },
        new WizardsHat { Hue = 0x489 },
        new Spellbook(0)
    ];

    // ---- ScribeLibrary --------------------------------------------------------

    private static Item[] ScribeApprenticeApparel() =>
    [
        new Robe(),
        new Cloak()
    ];

    // SP-050: worn spellbook is never Content = 0 (the same blank/empty
    // guardrail this tier's own stock honors) - StarterSpellbookContent
    // (circles 1-4) instead of a truly empty book.
    private static Item[] ScribeScholarApparel() =>
    [
        new Robe(),
        new Cloak(),
        new Spellbook(StarterSpellbookContent)
    ];

    private static Item[] ScribeMasterApparel() =>
    [
        new Robe(),
        new Cloak(),
        new Runebook()
    ];

    private static Item[] ScribeArchivistApparel() =>
    [
        new Robe { Hue = 0x481 },
        new Cloak { Hue = 0x481 }
    ];

    // ---- RawResources -----------------------------------------------------

    private static Item[] ResourcesLumberjackApparel() =>
    [
        new HalfApron(),
        new Shirt(),
        new Boots(),
        new Hatchet()
    ];

    private static Item[] ResourcesIronSmelterApparel() =>
    [
        new HalfApron(),
        new Shirt(),
        new Boots(),
        new Pickaxe()
    ];

    private static Item[] ResourcesTannerApparel() =>
    [
        new HalfApron(),
        new Shirt(),
        new Boots()
    ];

    private static Item[] ResourcesRareOreSmelterApparel() =>
    [
        new FullApron(),
        new Shirt(),
        new Boots()
    ];

    // ---- TailorFletcher ------------------------------------------------------

    private static Item[] TailorBowyerApparel() =>
    [
        new LeatherChest(),
        new Bow(),
        new FancyShirt()
    ];

    private static Item[] TailorLeatherworkerApparel() =>
    [
        new LeatherChest(),
        new FancyShirt()
    ];

    private static Item[] TailorClothierApparel() =>
    [
        new FancyShirt(),
        new Cloak()
    ];

    private static Item[] TailorMasterDyerApparel() =>
    [
        new Robe { Hue = 0x486 },
        new Cloak { Hue = 0x486 }
    ];

    // ---- TinkerCarpenter -----------------------------------------------------

    // SP-057: renamed from TinkerCarpenterSlotApparel and reassigned from
    // slot 0 to slot 1 - this SmithHammer-holding apron now dresses the
    // Apprentice Carpenter (furniture), matching the ticket's own tier
    // content swap.
    private static Item[] TinkerApprenticeCarpenterApparel() =>
    [
        new FullApron(),
        Held(new SmithHammer())
    ];

    // SP-057: renamed from TinkerTinkererApparel and reassigned from slot 1
    // to slot 0 - this TinkerTools-holding apron now dresses the Master
    // Tinker (tools/gadgets).
    private static Item[] TinkerMasterTinkerApparel() =>
    [
        new FullApron(),
        Held(new TinkerTools())
    ];

    private static Item[] TinkerMasterWoodworkerApparel() =>
    [
        new FullApron(),
        Held(new TinkerTools())
    ];

    private static Item[] TinkerArchitectOutfitterApparel() =>
    [
        new FullApron(),
        Held(new TinkerTools())
    ];

    // ---- FisherCurioBaker -----------------------------------------------------

    // SP-059: renamed from FisherTavernCookApparel and reassigned from
    // slot 1 to slot 0 - this HalfApron/Cap combo now dresses the Master
    // Baker & Provisioner.
    private static Item[] FisherMasterBakerApparel() =>
    [
        new HalfApron(),
        new Cap()
    ];

    // SP-059: renamed from FisherDeepSeaApparel and reassigned from slot
    // 0 to slot 1 - this FishingPole-holding combo now dresses the
    // Fishmonger & Cartographer.
    private static Item[] FisherFishmongerCartographerApparel() =>
    [
        new FloppyHat(),
        new FishingPole()
    ];

    private static Item[] FisherCellarMasterApparel() =>
    [
        new HalfApron(),
        Held(new Pitcher(BeverageType.Ale))
    ];

    private static Item[] FisherGrandShipwrightApparel() =>
    [
        new FancyShirt(),
        new Cloak { Hue = 0x489 }
    ];
}
