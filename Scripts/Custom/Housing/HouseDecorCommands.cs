// =========================================================================
// HouseDecorCommands.cs — SP-043/SP-044: originally also home to
// [exportdecor/[importdecor and the footprint-category-keyed template
// registry/picker gump. SP-055 replaced both commands outright with
// Scripts/Custom/OrganicMarket/ExportHouseCommand.cs/ImportHouseCommand.cs
// (classified by Ambient/Vendor+Archetype and stored under
// Data/HouseTemplates/, with small-house cross-style compatibility - see
// HouseTemplateManager.cs's own header) and removed
// HouseTemplateRegistry.cs/HouseDecorGump.cs entirely, since nothing else
// referenced them (confirmed via full-repo grep before deletion).
//
// This file survives only for CollectDecorItems/ClearDecor: the actual
// "scan a house for its decor"/"wipe it" primitives, which
// HouseTemplateManager.cs and AmbientHousePurchaseGump.cs both still call
// into directly - cross-namespace calls from Scripts/Custom/OrganicMarket/
// into Scripts/Custom/Housing/ were already an established, working
// pattern before this refactor and remain the correct way to reuse this
// logic rather than duplicating it.
//
// SP-044: "decorative clutter" is no longer just house.LockDowns.
// Verified against real content: Anvil/Forge (Items/Skill Items/Blacksmith
// Items/Misc/AnvilForge.cs) are plain Items that construct themselves
// Movable = false, and BaseHouse.LockDown's own gate
// (`item is BaseAddonContainer || item.Movable && ...`) can never register
// an item that's already non-movable — so DynamicClutterGenerator places
// them directly and never even attempts to lock them down. They were
// missing from every export not because they're BaseAddon/AddonComponent
// (in THIS codebase they aren't), but because LockDowns was never the
// complete picture for genuinely-immovable-on-construction props. A real
// water trough (Items/Addons/WaterTroughSouthAddon.cs etc.) IS a true
// BaseAddon, though — its root item is Movable = false AND Visible = false
// (see BaseAddon's own constructor), with the actual visible pieces living
// in its Components list (AddonComponent : Item) — so both mechanisms are
// real and CollectDecorItems below handles both, plus a spatial floor
// sweep to catch anything neither list tracks. Doors (house.Doors) and the
// house sign (house.Sign) are excluded by type, not by absence from a
// list, since the spatial sweep would otherwise happily pick them up too.
// =========================================================================

using System.Collections.Generic;
using Server.Items;
using Server.Multis;

namespace Server.Engines.Housing;

public static class HouseDecorCommands
{
    // The house's own multi footprint, in world coordinates — the bounds
    // CollectDecorItems' spatial sweep scans for stray non-movable props
    // that are in neither house.LockDowns nor house.Addons (Anvil/Forge).
    // Same MultiComponentList-based math OrganicMarketSpawner.
    // HasFootprintConflict already uses for its own bounding box.
    private static Rectangle2D FootprintBounds(BaseHouse house)
    {
        var mcl = house.Components;
        return new Rectangle2D(house.X + mcl.Min.X, house.Y + mcl.Min.Y, mcl.Width, mcl.Height);
    }

    // The one true source of "every decor item this house has," used by
    // export (serialize them), clearing (delete them), and the Furnished
    // purchase surcharge (count them) alike — three different call sites
    // all describing decor items differently would drift out of sync with
    // each other the moment one of them learned about a new prop
    // category and the others didn't.
    //
    // Three sources, deduplicated by object identity:
    //   1. house.LockDowns - ordinary locked-down furniture/clutter.
    //   2. Each BaseAddon in house.Addons, expanded to its own Components -
    //      the real, visible pieces of a placed addon (forge/anvil/trough
    //      variants, dye tubs, etc.) The invisible BaseAddon root itself
    //      is never included; there is nothing to render for it.
    //   3. A spatial sweep of the house's own footprint
    //      (map.GetItemsInBounds, filtered by house.IsInside — the
    //      CLAUDE.md-compliant bounded spatial query, not a raw
    //      World.Items walk) for anything neither list tracks: a prop
    //      that constructs itself Movable = false and so can never be
    //      locked down at all (confirmed root cause for Anvil/Forge).
    // isAddon on each result records which of the three found it, purely
    // for the JSON schema's own informational IsAddon field.
    public static List<(Item Item, bool IsAddon)> CollectDecorItems(BaseHouse house)
    {
        var results = new List<(Item, bool)>();
        var seen = new HashSet<Item>();

        void Add(Item item, bool isAddon)
        {
            if (item?.Deleted != false || !seen.Add(item))
            {
                return;
            }

            // Critical guard: map.GetItemsInBounds below walks the same
            // per-sector Item list a placed BaseHouse itself sits in
            // (confirmed against Map.OnEnter — GetSector(p).OnEnter(item)
            // runs for every Item, multis included, before the separate
            // AddMulti registration that also happens for them) — the
            // house's own root item's Location is, by construction,
            // inside its own footprint bounds. Without this, the spatial
            // sweep hands the house itself back as a "decor item," and
            // ClearDecor's own deletion loop would then delete the entire
            // building. item is BaseMulti covers the house and any other
            // multi (a boat, a second house) that might overlap the
            // bounds; item == house is kept alongside it as an explicit,
            // unambiguous belt-and-suspenders check.
            if (item == house || item is BaseMulti)
            {
                return;
            }

            // Core fixtures — the sign and every door — are never decor
            // either, regardless of how they were found. house.Doors.
            // Contains is redundant with the BaseDoor type check today
            // (every door in this engine is BaseDoor-derived) but is kept
            // as an explicit, cheap second guard against a future custom
            // door class that doesn't happen to inherit from it.
            if (item == house.Sign || item is HouseSign or BaseDoor or BaseAddon or VendorRentalContract ||
                house.Doors?.Contains(item as BaseDoor) == true)
            {
                return;
            }

            results.Add((item, isAddon));
        }

        foreach (var item in house.LockDowns)
        {
            Add(item, false);
        }

        foreach (var addonItem in house.Addons)
        {
            if (addonItem is BaseAddon addon)
            {
                foreach (var component in addon.Components)
                {
                    Add(component, true);
                }
            }
        }

        var map = house.Map;
        if (map != null && map != Map.Internal)
        {
            foreach (var item in map.GetItemsInBounds(FootprintBounds(house)))
            {
                if (item?.Deleted == false && house.IsInside(item.Location, item.ItemData.Height))
                {
                    Add(item, false);
                }
            }
        }

        return results;
    }

    // Deletes every decor item a GM would recognize as "the house's own
    // clutter" — every BaseAddon in house.Addons (Delete() on the root
    // cascades to every one of its Components, same mechanism
    // BaseAddon.OnChop already relies on), plus everything
    // CollectDecorItems finds (LockDowns and stray immovable props; any
    // addon component already went with its parent above, so this pass
    // only ever deletes what's left). Shared by ExportHouseGump's/
    // ImportHouseGump's own pre-placement clear and
    // AmbientHousePurchaseGump's unconditional purchase-time wipe, so
    // every caller describes "vacant" identically.
    public static void ClearDecor(BaseHouse house)
    {
        foreach (var addonItem in new List<Item>(house.Addons))
        {
            if (addonItem?.Deleted == false)
            {
                addonItem.Delete();
            }

            house.Addons.Remove(addonItem);
        }

        foreach (var (item, _) in CollectDecorItems(house))
        {
            if (item?.Deleted == false)
            {
                item.Delete();
            }
        }
    }
}
