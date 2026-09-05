// =========================================================================
// HouseDecorCommands.cs — SP-043/SP-044: [exportdecor <optional_name> and
// [importdecor. Both target a house sign (or, failing that, anything else
// sitting inside the house — BaseHouse.FindHouseAt infers it) and operate
// on that house's own decorative clutter, using HouseTemplateRegistry
// (footprint categorization + Data/HouseTemplates/<Category>/*.json
// storage) underneath.
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

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Items;
using Server.Multis;
using Server.Targeting;

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
    // only ever deletes what's left). Shared by [importdecor's own
    // pre-placement clear and AmbientHousePurchaseGump's Purchase Vacant
    // path, so both describe "vacant" identically.
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

    public static void Configure()
    {
        CommandSystem.Register("exportdecor", AccessLevel.GameMaster, OnExportDecor);
        CommandSystem.Register("importdecor", AccessLevel.GameMaster, OnImportDecor);
    }

    // Accepts a house sign directly (the common case), the house item
    // itself, or anything else (a chair, a wall) sitting inside one —
    // BaseHouse.FindHouseAt is the same "infer the house" lookup the core
    // engine uses for things like OnLogin's ban-location check.
    private static BaseHouse ResolveHouse(object targeted) => targeted switch
    {
        HouseSign sign => sign.Owner,
        BaseHouse house => house,
        Item item => BaseHouse.FindHouseAt(item),
        Mobile mobile => BaseHouse.FindHouseAt(mobile),
        _ => null
    };

    [Usage("exportdecor <name>")]
    [Description("Target a house sign (or anything inside a house) to export its decorative clutter as a reusable JSON blueprint. <name> is optional.")]
    private static void OnExportDecor(CommandEventArgs e)
    {
        var name = e.Length > 0 ? e.GetString(0) : null;
        var from = e.Mobile;
        from?.SendMessage("Target a house sign (or something inside a house) to export its decor.");
        from.Target = new ExportDecorTarget(name);
    }

    private class ExportDecorTarget : Target
    {
        private readonly string _requestedName;

        public ExportDecorTarget(string requestedName) : base(-1, false, TargetFlags.None) =>
            _requestedName = requestedName;

        protected override void OnTarget(Mobile from, object o)
        {
            var house = ResolveHouse(o);
            if (house?.Deleted != false)
            {
                from.SendMessage("That is not a house sign, or not something inside a house.");
                return;
            }

            var category = HouseTemplateRegistry.CategoryOf(house);
            if (category == HouseFootprintCategory.Unknown)
            {
                from.SendMessage(0x22, $"[ExportDecor] {house.GetType().Name} isn't a recognized footprint category — nothing exported.");
                return;
            }

            var template = new DecorTemplate { HouseTypeName = house.GetType().Name };

            foreach (var (item, isAddon) in CollectDecorItems(house))
            {
                template.Items.Add(new DecorItemRecord
                {
                    Dx = item.X - house.X,
                    Dy = item.Y - house.Y,
                    Dz = item.Z - house.Z,
                    ItemId = item.ItemID,
                    Hue = item.Hue,
                    Name = item.Name,
                    Movable = item.Movable,
                    IsAddon = isAddon
                });
            }

            var templateName = SanitizeFileName(
                string.IsNullOrWhiteSpace(_requestedName)
                    ? $"{category}-{Core.Now:yyyyMMdd-HHmmss}"
                    : _requestedName
            );

            HouseTemplateRegistry.SaveTemplate(category, templateName, template);
            var path = HouseTemplateRegistry.TemplatePath(category, templateName);

            from.SendMessage(0x59, $"[ExportDecor] Saved {template.Items.Count} item(s) from this {category} house to {path}.");
        }
    }

    [Usage("importdecor")]
    [Description("Target a house sign (or anything inside a house) to open a list of compatible saved decor blueprints and apply one.")]
    private static void OnImportDecor(CommandEventArgs e)
    {
        var from = e.Mobile;
        from?.SendMessage("Target a house sign (or something inside a house) to import decor into.");
        from.Target = new ImportDecorTarget();
    }

    private class ImportDecorTarget : Target
    {
        public ImportDecorTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object o)
        {
            var house = ResolveHouse(o);
            if (house?.Deleted != false)
            {
                from.SendMessage("That is not a house sign, or not something inside a house.");
                return;
            }

            var category = HouseTemplateRegistry.CategoryOf(house);
            if (category == HouseFootprintCategory.Unknown)
            {
                from.SendMessage(0x22, $"[ImportDecor] {house.GetType().Name} isn't a recognized footprint category.");
                return;
            }

            var templates = HouseTemplateRegistry.ListTemplates(category);
            if (templates.Count == 0)
            {
                from.SendMessage(0x22, $"[ImportDecor] No saved blueprints exist yet for the {category} category.");
                return;
            }

            HouseDecorGump.DisplayTo(from, house, category, templates);
        }
    }

    // Shared by HouseDecorGump's own selection button — the gump only
    // handles listing/picking, the actual clear-and-place operation lives
    // here so it's reachable (and testable) the same way
    // AmbientHousePurchaseGump.TryPurchase's logic is split from its gump.
    public static void ApplyTemplate(Mobile from, BaseHouse house, HouseFootprintCategory category, string templateName)
    {
        if (house?.Deleted != false)
        {
            from?.SendMessage("That house is no longer available.");
            return;
        }

        var template = HouseTemplateRegistry.LoadTemplate(category, templateName);
        if (template == null)
        {
            from?.SendMessage(0x22, $"[ImportDecor] Could not load blueprint '{templateName}'.");
            return;
        }

        // Clear existing decorative clutter first — LockDowns, every
        // placed addon, and any stray immovable prop alike (see
        // ClearDecor's own header).
        ClearDecor(house);

        var placed = 0;
        foreach (var record in template.Items)
        {
            var item = new Item(record.ItemId) { Hue = record.Hue };
            if (!string.IsNullOrEmpty(record.Name))
            {
                item.Name = record.Name;
            }

            item.MoveToWorld(new Point3D(house.X + record.Dx, house.Y + record.Dy, house.Z + record.Dz), house.Map);

            // house.Owner always satisfies IsCoOwner (IsOwner(m) is true
            // for m == Owner) whether the house still belongs to
            // MerchantGuildAuthority or a real player, so this locks down
            // correctly either way. checkIsInside: false matches
            // DynamicClutterGenerator's own convention — these offsets
            // are already known-good for this footprint category.
            if (house.LockDown(house.Owner, item, false))
            {
                placed++;
            }

            // A LockDown refusal (house lockdown/storage cap already
            // full) leaves the item exactly where it landed, movable and
            // unlocked, rather than forcing an inconsistent "immovable
            // but not tracked as a fixture" state — the honest partial
            // count below tells the GM capacity ran out.
        }

        from?.SendMessage(
            0x59,
            $"[ImportDecor] Placed {placed}/{template.Items.Count} item(s) from '{templateName}' into this house."
        );
    }

    // GM-supplied template names become file names — strip anything that
    // isn't safe across the filesystems this server might run on.
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        return new string(buffer);
    }
}
