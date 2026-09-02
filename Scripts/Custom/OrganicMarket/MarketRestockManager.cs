// =========================================================================
// MarketRestockManager.cs — SP-033: the automated restock lifecycle. Two
// independent passes:
//   1. Startup threshold check (once, shortly after world load): tops up
//      any vendor whose own root Backpack is empty or nearly so, without
//      touching vendors that are already reasonably stocked - a restart
//      shouldn't reset a shop a player has been happily buying from.
//   2. A 3-hour recurring rotation restock across EVERY registered vendor
//      - wipes and re-stocks each one, which is what makes the dynamic
//      candidate pools (StockTemplateEngine.CarpenterFurniturePool/
//      ScribeCombatScrollPool/TinkerGadgetPool) actually rotate over a
//      long-running server instead of only ever rolling once at spawn.
//
// Both passes are thin wrappers around MerchantGuildAuthority's own
// restock methods (RestockVendor for the surgical startup pass,
// RestockAll for the blanket rotation pass) - this class owns only the
// scheduling/threshold policy, not the actual stock-clearing mechanics.
// =========================================================================

using System;
using Server.Logging;

namespace Server.Engines.OrganicMarket;

public static class MarketRestockManager
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(MarketRestockManager));

    // SP-033: "holding < 25% of baseline expected stock" - PlayerVendor
    // itself isn't ours to add a per-vendor stored baseline field to (it's
    // a core engine class), so this is a universal reference floor for
    // "freshly stocked" under SP-033's own parallel-organizer-plus-loose-
    // showcase layout (StockTemplateEngine now places 2-4 organizer
    // containers plus a handful of loose showcase pieces per vendor - 8+
    // top-level Backpack entries is the realistic freshly-stocked count).
    // 25% of that rounds to 2, so a vendor showing 0 or 1 top-level items
    // in its own root Backpack (freshly wiped, or sold down to almost
    // nothing) is what triggers an immediate fill; anything at or above
    // that is left alone, exactly per the "leave already stocked vendors
    // intact" instruction.
    private const int BaselineItemCount = 8;
    private const int RestockThreshold = BaselineItemCount / 4;

    // How long after world load the one-shot startup check runs - long
    // enough that world seeding/deserialization has settled, short enough
    // that a GM watching the boot log sees it happen promptly.
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RestockInterval = TimeSpan.FromHours(3);

    [CallPriority(10)]
    public static void Initialize()
    {
        Timer.DelayCall(StartupCheckDelay, RunStartupThresholdCheck);

        // count: 0 (the delay/interval-only DelayCall overload) means
        // "forever" - see Timer.DelayCall's own overload chain. Interval
        // doubles as the initial delay: the very first rotation restock
        // fires 3 hours after this Initialize() runs, not immediately -
        // a brand-new/just-seeded world doesn't need an immediate blanket
        // re-stock on top of what SeedAll/SeedInhabitation already placed.
        Timer.DelayCall(RestockInterval, RestockInterval, RunRotationRestock);
    }

    // Once, at startup: surgical per-vendor top-up, never touching a
    // vendor that already has reasonable stock.
    private static void RunStartupThresholdCheck()
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        var filled = 0;
        var checkedCount = 0;

        for (var i = 0; i < authority.Count; i++)
        {
            var house = authority.HouseAt(i);
            if (house?.Deleted != false)
            {
                continue;
            }

            foreach (var vendor in house.PlayerVendors)
            {
                if (vendor?.Deleted != false || vendor.Backpack == null)
                {
                    continue;
                }

                checkedCount++;

                if (vendor.Backpack.Items.Count < RestockThreshold && authority.RestockVendor(vendor))
                {
                    filled++;
                }
            }
        }

        if (VerboseConfig.VendorStock)
        {
            logger.Information(
                "MarketRestockManager: startup threshold check examined {Checked} vendor(s), filled {Filled} under-stocked",
                checkedCount, filled
            );
        }
    }

    // Every 3 hours of active runtime: blanket wipe-and-restock across
    // every registered vendor (MerchantGuildAuthority.RestockAll already
    // does exactly "clear and replenish missing stock, prune stale items,
    // re-roll dynamic candidate pools" - see its own header comment).
    private static void RunRotationRestock()
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        var count = authority.RestockAll();

        if (VerboseConfig.VendorStock)
        {
            logger.Information("MarketRestockManager: 3-hour rotation restock refreshed {Count} vendor(s)", count);
        }
    }
}
