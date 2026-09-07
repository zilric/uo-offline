// =========================================================================
// VendorGridArranger.cs — SP-053: arranges a PlayerVendor's sale
// container(s) into a neat, adaptive grid instead of the unpositioned
// drop-order StockTemplateEngine leaves them in (confirmed by direct
// inspection: StockTemplateEngine never assigns Item.Location anywhere -
// this is a genuinely new layer, not a duplicate of anything already here).
//
// Bounding box: uses Container.Bounds directly (Server/Items/Container.cs,
// itself sourced from the real Distribution/Data/containers.cfg table,
// keyed by ItemID) rather than a hardcoded per-type coordinate table. This
// is deliberately more correct than hand-picked constants - it already
// gives every container type (Backpack, Bag, Basket, WoodenBox, Pouch, ...)
// its own real usable rectangle for free, verified against the live data:
// Backpack (44,65,142,94), Bag (29,34,108,94), Basket (35,38,110,78),
// WoodenBox (16,51,168,73). A hardcoded "Backpack/Chest/Default" table
// would either duplicate this data by hand or drift out of sync with it.
//
// Container discovery: vendor.Backpack is always the root sale container
// (PlayerVendor.InitOutfit builds a VendorBackpack there - confirmed no
// other root container exists). StockTemplateEngine only ever nests
// sub-containers one level deep (WoodenBox/Pouch/Basket/Bag/Backpack
// "organizer" or "bundle" containers dropped directly into vendor.Backpack,
// never nested further), but ArrangeRecursive below doesn't hardcode that
// depth-1 assumption - it just recurses into whatever it finds, so it stays
// correct even if a future ticket nests deeper.
//
// Bundle guardrail: a sub-container sold as ONE priced unit (VendorItem.
// IsForSale on the container itself - StockTemplateEngine.
// CreatePackagedSubcontainer/SellBundle) never has its own contents
// individually purchasable (PlayerVendor.CanBeVendorItem absorbs them into
// the parent sale), so rearranging its interior would be wasted work on
// something a buyer only ever sees as a single icon. A "display" organizer
// (CreateDisplayContainer, VendorItem.Price == -1 / not-for-sale on the
// container itself) IS recursed into, since its own children are each
// individually priced and purchasable.
//
// Post-purchase compaction: PlayerShopPatronageManager.CompletePurchase
// (the bot-shopper purchase path this codebase fully owns) gets a direct
// Arrange() call. A REAL player's purchase completes inside core's
// Server.Gumps.PlayerVendorGumps.PlayerVendorBuyGump.OnResponse, which has
// no virtual/event extension point Scripts/Custom can hook without editing
// that core file - confirmed by direct inspection, and out of bounds per
// this ticket's own scope. WatchSweep below is the only core-free way to
// still catch that path: a low-cost periodic scan (every 60s, matching the
// order of magnitude of this subsystem's other ambient timers) that
// re-arranges any vendor whose total item count changed since the last
// sweep - covers real purchases (and anything else that silently changes
// a vendor's stock) as a reactive fallback, at the cost of up to a 60s
// delay rather than an instant per-purchase hook.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;

namespace Server.Engines.OrganicMarket;

public static class VendorGridArranger
{
    // Bounds.Width/Height already describe the raw drag-and-drop-valid
    // rectangle (Container.cs/ContainerData), not an icon-safe one - this
    // margin keeps item art from visually hanging off the container's own
    // edge.
    private const int EdgeMargin = 8;

    // SP-054: hand-calibrated against the real Backpack gump art - see
    // ArrangeOne's own comment for why this overrides Container.Bounds'
    // real (44,65,142,94) rectangle for Backpack specifically rather than
    // just tightening EdgeMargin.
    private const int BackpackUsableX = 38;
    private const int BackpackUsableY = 62;
    private const int BackpackUsableWidth = 105;
    private const int BackpackUsableHeight = 75;

    // "+2px per additional item in the group, clamped at +8px" per the
    // ticket - a deck-of-cards stack, not a second grid.
    private const int StackOffsetStep = 2;
    private const int StackOffsetMax = 8;

    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<PlayerVendor, int> _lastItemCounts = new();

    public static void Initialize()
    {
        Timer.DelayCall(WatchInterval, WatchInterval, WatchSweep);
    }

    // Arranges vendor.Backpack and every eligible sub-container beneath it.
    // Safe to call on an already-tidy vendor - fully idempotent, just
    // recomputes the same grid from current contents each time.
    public static (int Containers, int Items) Arrange(PlayerVendor vendor)
    {
        if (vendor?.Deleted != false || vendor.Backpack is not { Deleted: false } backpack)
        {
            return (0, 0);
        }

        var containers = 0;
        var items = 0;
        ArrangeRecursive(backpack, vendor, ref containers, ref items);
        return (containers, items);
    }

    private static void ArrangeRecursive(Container container, PlayerVendor vendor, ref int containers, ref int items)
    {
        if (container?.Deleted != false)
        {
            return;
        }

        containers++;
        items += ArrangeOne(container, vendor);

        foreach (var child in container.Items)
        {
            if (child is not Container childContainer)
            {
                continue;
            }

            // Bundle guardrail - see file header.
            var vi = vendor.GetVendorItem(childContainer);
            if (vi is { IsForSale: true })
            {
                continue;
            }

            ArrangeRecursive(childContainer, vendor, ref containers, ref items);
        }
    }

    // Positions the direct children of ONE container - no recursion here,
    // ArrangeRecursive owns descending into sub-containers. Returns how
    // many direct-child items got positioned.
    private static int ArrangeOne(Container container, PlayerVendor vendor)
    {
        var groups = GroupItems(container, vendor);
        if (groups.Count == 0)
        {
            return 0;
        }

        int usableX, usableY, usableWidth, usableHeight;

        if (container is Backpack)
        {
            // SP-054: Container.Bounds' real Backpack rectangle (44,65,
            // 142,94) is the raw drag-and-drop-valid area, not what reads
            // as "clean" against the actual client art - its lower-right
            // portion sits under the drawstring flap, biasing every grid
            // too far down and right. Hand-calibrated against the real
            // gump instead, and used directly as the final usable window
            // (no further EdgeMargin shrink - already tuned to fit the
            // upper-middle pocket the ticket asked for).
            usableX = BackpackUsableX;
            usableY = BackpackUsableY;
            usableWidth = BackpackUsableWidth;
            usableHeight = BackpackUsableHeight;
        }
        else
        {
            var bounds = container.Bounds;
            usableX = bounds.X + EdgeMargin;
            usableY = bounds.Y + EdgeMargin;
            usableWidth = Math.Max(1, bounds.Width - EdgeMargin * 2);
            usableHeight = Math.Max(1, bounds.Height - EdgeMargin * 2);
        }

        var (cols, rows) = GridSize(groups.Count);
        var stepX = usableWidth / (double)cols;
        var stepY = usableHeight / (double)rows;

        var placed = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            var col = i % cols;
            // Clamps any overflow beyond the grid's own capacity into the
            // last row instead of running the cell math outside the
            // container's bounds - "dynamically clamped to container
            // bounds" per the ticket.
            var row = Math.Min(i / cols, rows - 1);

            var cellX = usableX + (int)(col * stepX + stepX / 2);
            var cellY = usableY + (int)(row * stepY + stepY / 2);

            var group = groups[i];
            for (var j = 0; j < group.Count; j++)
            {
                var offset = Math.Min(j * StackOffsetStep, StackOffsetMax);
                group[j].Location = new Point3D(cellX + offset, cellY + offset, 0);
                placed++;
            }
        }

        return placed;
    }

    // Groups direct children sharing type + hue + unit price into the same
    // layout slot, preserving first-seen order so the grid fills in a
    // stable, predictable sequence across repeated calls.
    //
    // SP-054: a sub-container is NEVER folded into this shared-key grouping
    // unless it's a genuine priced bundle (IsForSale on the container
    // itself - StockTemplateEngine.CreatePackagedSubcontainer/SellBundle,
    // same distinction ArrangeRecursive's own bundle guardrail already
    // uses). An organizer/display container (a chest of swords next to a
    // chest of fencing weapons, a gem bag next to a scroll bag, ...) shares
    // type + hue + "no individual price" with its siblings by construction
    // - grouping on that key collapsed three visually distinct chests into
    // one stacked slot, which was the reported bug. Each organizer gets its
    // own list, added directly to the result in true encounter order,
    // never touching the shared dictionary below.
    private static List<List<Item>> GroupItems(Container container, PlayerVendor vendor)
    {
        var result = new List<List<Item>>();
        var mergeableGroups = new Dictionary<(Type, int, int), List<Item>>();

        foreach (var item in container.Items)
        {
            if (item?.Deleted != false)
            {
                continue;
            }

            if (item is Container subContainer && vendor.GetVendorItem(subContainer) is not { IsForSale: true })
            {
                result.Add(new List<Item> { item });
                continue;
            }

            var price = vendor.GetVendorItem(item)?.Price ?? -1;
            var key = (item.GetType(), item.Hue, price);

            if (!mergeableGroups.TryGetValue(key, out var list))
            {
                list = new List<Item>();
                mergeableGroups[key] = list;
                // Reserves this group's position in encounter order now -
                // later items sharing this key just Add() into the same
                // list reference, already sitting at the right spot here.
                result.Add(list);
            }

            list.Add(item);
        }

        return result;
    }

    // Adaptive grid sizing per the ticket's own N-thresholds.
    private static (int Cols, int Rows) GridSize(int n) => n switch
    {
        <= 4  => (2, 2),
        <= 6  => (3, 2),
        <= 9  => (3, 3),
        <= 12 => (4, 3),
        _     => (5, 4)
    };

    // See file header - the only core-free way to catch a real player's
    // purchase, since PlayerVendorBuyGump.OnResponse (core) has no
    // extension point. Also doubles as a one-time tidy-up for any vendor
    // that existed before this feature was deployed, the first time this
    // sweep ever observes it.
    private static void WatchSweep()
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        var seen = new HashSet<PlayerVendor>();

        for (var i = 0; i < authority.Count; i++)
        {
            var house = authority.HouseAt(i);
            if (house?.Deleted != false)
            {
                continue;
            }

            foreach (var vendor in house.PlayerVendors)
            {
                if (vendor?.Deleted != false || vendor.Backpack is not { Deleted: false } backpack)
                {
                    continue;
                }

                seen.Add(vendor);

                var count = CountAllItems(backpack);
                var changed = !_lastItemCounts.TryGetValue(vendor, out var last) || last != count;
                _lastItemCounts[vendor] = count;

                if (changed)
                {
                    Arrange(vendor);
                }
            }
        }

        PruneStale(seen);
    }

    // Dictionary holds a strong Mobile reference per tracked vendor - has
    // to be pruned as houses/vendors disappear from MerchantGuildAuthority,
    // or a deleted vendor's reference would sit here forever.
    private static void PruneStale(HashSet<PlayerVendor> seen)
    {
        if (_lastItemCounts.Count <= seen.Count)
        {
            return;
        }

        List<PlayerVendor> stale = null;
        foreach (var tracked in _lastItemCounts.Keys)
        {
            if (!seen.Contains(tracked))
            {
                (stale ??= new List<PlayerVendor>()).Add(tracked);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (var v in stale)
        {
            _lastItemCounts.Remove(v);
        }
    }

    private static int CountAllItems(Container container)
    {
        var count = 0;
        foreach (var item in container.Items)
        {
            count++;
            if (item is Container child)
            {
                count += CountAllItems(child);
            }
        }

        return count;
    }
}
