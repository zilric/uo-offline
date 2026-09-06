// =========================================================================
// OrganicMarketDirectoryGump.cs — paged directory of every registered
// Organic Market house (7 per page), with per-entry Teleport / Restock /
// Delete actions and Prev/Next paging.
//
// SP-048: each row's info line now also shows the house's own type name
// (OrganicMarketSpawner.HouseTypeName) next to its coordinates, and the
// nav row gained "<< Prev 10"/"Next 10 >>" buttons alongside the existing
// single-step Prev/Next, for jumping through a long directory (WorldFrontier
// Seeder's own 1,500-house target can produce well over 200 pages at 7/page)
// without clicking through every page in between.
//
// SP-049: fixed a real button ID collision the 1,500-house target made
// live rather than theoretical. Teleport/Restock/Delete/Move Vendor were
// each `Base + i` with bases only 1,000 apart (1000/2000/3000/4000) - once
// the registry held more than 1,000 houses, index 1001's Teleport button
// (1000 + 1001 = 2001) was numerically identical to index 1's Restock
// button (2000 + 1), so OnResponse's own >= RestockBase check (which runs
// before >= TeleportBase) dispatched it as a restock instead - exactly
// the "reports restocking a vendor" symptom this was reported as. Bases
// are now ActionStride (1,000,000) apart, which stays fully collision-free
// for any realistic house count (WorldFrontierSeeder's own current ceiling
// is 1,500) while keeping the exact same Base + index encoding and >=
// dispatch order that was already correct - just too narrowly spaced.
// =========================================================================

using System;
using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class OrganicMarketDirectoryGump : DynamicGump
{
    public override bool Singleton => true;

    private const int PerPage = 7;
    private const int PageJump = 10;
    private const int ButtonBack = 1;
    private const int ButtonPrev = 2;
    private const int ButtonNext = 3;
    private const int ButtonPrevJump = 4;
    private const int ButtonNextJump = 5;

    // SP-049: wide enough that TeleportBase + i can never reach RestockBase
    // (or any higher base) for any house index this server could
    // realistically ever register - see file header.
    private const int ActionStride = 1_000_000;
    private const int TeleportBase = ActionStride;
    private const int RestockBase = ActionStride * 2;
    private const int DeleteBase = ActionStride * 3;
    private const int MoveVendorBase = ActionStride * 4;

    private readonly int _page;

    private OrganicMarketDirectoryGump(int page) : base(50, 30)
    {
        _page = Math.Max(0, page);
    }

    public static void DisplayTo(Mobile from, int page)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new OrganicMarketDirectoryGump(page));
    }

    // Column x-positions for the four row-action buttons, spread out
    // horizontally in one line instead of the old cramped 2x2 cluster
    // (Teleport/Restock on one row, Delete/Move Vendor overlapping the
    // one below it).
    private const int ColTeleport   = 24;
    private const int ColRestock    = 180;
    private const int ColMoveVendor = 340;
    private const int ColDelete     = 520;

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        // Widened from 520 to 650 so all four row-action buttons get their
        // own clear horizontal slot instead of doubling up into a 2x2
        // grid, and rows grew from 30 to 40px so the two-line
        // info/actions layout below has clean breathing room.
        // SP-048: widened again, 650 to 820, for the new house-type-name
        // column in each row's info line (the longest style name,
        // "Sandstone House with Patio", needs real room next to the
        // existing ID/Archetype/Facet/coordinate text).
        const int width = 820;
        const int rowHeight = 40;
        const int listTop = 90;
        const int navY = listTop + PerPage * rowHeight + 20;
        const int height = navY + 30;

        var authority = MerchantGuildAuthority.Instance;
        var total = authority?.Count ?? 0;
        var pageCount = Math.Max(1, (total + PerPage - 1) / PerPage);
        var page = Math.Clamp(_page, 0, pageCount - 1);
        var start = page * PerPage;
        var end = Math.Min(start + PerPage, total);

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(
            20, 16, width - 40, 20,
            $"<center><basefont color=#FFD700>Market House Directory — page {page + 1}/{pageCount}</basefont></center>"
        );
        // SP-028: column widened for the archetype catalog's longer
        // friendly names ("Blacksmith Armory", "Scribe Library") -
        // OrganicMarketSpawner.ArchetypeName already produces the display
        // string this shows, stored verbatim at registration time
        // (MerchantGuildAuthority.ArchetypeAt), so there's nothing else to
        // reformat here beyond making sure the column is wide enough.
        builder.AddHtml(20, 38, width - 40, 20, "<basefont color=#7FFFD4>ID   Archetype             Facet      Type                        X, Y, Z</basefont>");

        if (total == 0)
        {
            builder.AddLabel(24, listTop, 0x480, "No market houses are registered yet.");
        }

        for (var i = start; i < end; i++)
        {
            var row = i - start;
            var y = listTop + row * rowHeight;
            var buttonY = y + 18;

            var house = authority.HouseAt(i);
            var alive = house?.Deleted == false;
            var id = authority.HouseIdAt(i);
            var archetype = authority.ArchetypeAt(i);
            var facet = alive ? house.Map?.ToString() ?? "?" : "(gone)";
            var loc = alive ? house.Location : Point3D.Zero;
            var typeName = OrganicMarketSpawner.HouseTypeName(house);

            builder.AddLabel(24, y, alive ? 0x480 : 0x21, $"{id,-4} {archetype,-20} {facet,-9} {typeName,-26} {loc.X},{loc.Y},{loc.Z}");

            builder.AddButton(ColTeleport, buttonY, 4005, 4007, TeleportBase + i);
            builder.AddLabel(ColTeleport + 24, buttonY, 0x59, "Teleport");

            // SP-026: an ambient residence has no vendor to restock or
            // move - those two columns would either be dead buttons or
            // (worse) silently act on whatever leftover _vendors[i] slot
            // happens to sit there. Delete moves up into Restock's own
            // column instead of staying pinned at the far-right Delete
            // slot, so the row reads as two buttons sitting cleanly next
            // to each other rather than two buttons with a wide gap where
            // Restock/Move Vendor would have been.
            if (archetype == OrganicMarketSpawner.AmbientResidenceArchetype)
            {
                builder.AddButton(ColRestock, buttonY, 4017, 4019, DeleteBase + i);
                builder.AddLabel(ColRestock + 24, buttonY, 0x25, "Delete");
            }
            else
            {
                builder.AddButton(ColRestock, buttonY, 4005, 4007, RestockBase + i);
                builder.AddLabel(ColRestock + 24, buttonY, 0x44, "Restock");

                builder.AddButton(ColMoveVendor, buttonY, 4005, 4007, MoveVendorBase + i);
                builder.AddLabel(ColMoveVendor + 24, buttonY, 0x59, "Move Vendor");

                builder.AddButton(ColDelete, buttonY, 4017, 4019, DeleteBase + i);
                builder.AddLabel(ColDelete + 24, buttonY, 0x25, "Delete");
            }
        }

        // SP-048: << Prev 10 / Next 10 >> sit right next to the existing
        // single-step Prev/Next, gated on the identical page>0 /
        // page<pageCount-1 conditions (jumping 10 pages is never valid
        // exactly when jumping 1 wouldn't be either) - a long directory
        // (WorldFrontierSeeder's own 1,500-house target can run past 200
        // pages at 7/page) would otherwise need dozens of single clicks to
        // cross. OnResponse doesn't need to separately clamp the target
        // page - DisplayTo's own constructor floors it at 0 and
        // BuildLayout's page/pageCount Math.Clamp above ceilings it, the
        // same guarantee the plain Prev/Next buttons already relied on.
        if (page > 0)
        {
            builder.AddButton(24, navY, 4014, 4016, ButtonPrevJump);
            builder.AddLabel(50, navY, 0x480, $"<< Prev {PageJump}");

            builder.AddButton(180, navY, 4014, 4016, ButtonPrev);
            builder.AddLabel(206, navY, 0x480, "< Prev");
        }

        if (page < pageCount - 1)
        {
            builder.AddButton(320, navY, 4005, 4007, ButtonNext);
            builder.AddLabel(346, navY, 0x480, "Next >");

            builder.AddButton(440, navY, 4005, 4007, ButtonNextJump);
            builder.AddLabel(466, navY, 0x480, $"Next {PageJump} >>");
        }

        builder.AddButton(width - 110, navY, 4017, 4019, ButtonBack);
        builder.AddLabel(width - 84, navY, 0x480, "Back");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        var authority = MerchantGuildAuthority.Instance;
        var buttonId = info.ButtonID;

        if (buttonId == ButtonBack)
        {
            OrganicMarketAdminGump.DisplayTo(from);
            return;
        }

        if (buttonId == ButtonPrev)
        {
            DisplayTo(from, _page - 1);
            return;
        }

        if (buttonId == ButtonNext)
        {
            DisplayTo(from, _page + 1);
            return;
        }

        if (buttonId == ButtonPrevJump)
        {
            DisplayTo(from, _page - PageJump);
            return;
        }

        if (buttonId == ButtonNextJump)
        {
            DisplayTo(from, _page + PageJump);
            return;
        }

        if (authority == null)
        {
            return;
        }

        if (buttonId >= MoveVendorBase)
        {
            var index = buttonId - MoveVendorBase;
            BeginMoveVendor(from, authority, index, _page);
            return;
        }

        if (buttonId >= DeleteBase)
        {
            var index = buttonId - DeleteBase;
            if (authority.DeleteAt(index))
            {
                from.SendMessage($"Deleted market house #{index}.");
            }

            DisplayTo(from, _page);
            return;
        }

        if (buttonId >= RestockBase)
        {
            var index = buttonId - RestockBase;
            from.SendMessage(
                authority.RestockAt(index)
                    ? "Vendor restocked."
                    : "That vendor could not be restocked (missing or already deleted)."
            );
            DisplayTo(from, _page);
            return;
        }

        if (buttonId >= TeleportBase)
        {
            var index = buttonId - TeleportBase;
            TeleportTo(from, authority, index);
            DisplayTo(from, _page);
        }
    }

    private static void TeleportTo(Mobile from, MerchantGuildAuthority authority, int index)
    {
        if (index < 0 || index >= authority.Count)
        {
            return;
        }

        var house = authority.HouseAt(index);
        if (house?.Deleted != false)
        {
            from.SendMessage("That house no longer exists.");
            return;
        }

        var dest = house.Sign?.Location ?? house.Location;
        var map = house.Map;
        if (map == null || map == Map.Internal)
        {
            from.SendMessage("That house has no valid map.");
            return;
        }

        from.MoveToWorld(dest, map);
        from.SendMessage($"Teleported to market house #{authority.HouseIdAt(index)}.");
    }

    private static void BeginMoveVendor(Mobile from, MerchantGuildAuthority authority, int index, int page)
    {
        if (index < 0 || index >= authority.Count)
        {
            return;
        }

        var house = authority.HouseAt(index);
        if (house?.Deleted != false)
        {
            from.SendMessage("That house no longer exists.");
            return;
        }

        var vendor = authority.VendorAt(index);
        if (vendor?.Deleted != false)
        {
            from.SendMessage("That vendor no longer exists.");
            return;
        }

        from.SendMessage("Target a tile inside the house to move the vendor there.");
        from.Target = new MoveVendorTarget(house, vendor, page);
    }
}
