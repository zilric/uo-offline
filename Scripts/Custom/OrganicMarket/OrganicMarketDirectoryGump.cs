// =========================================================================
// OrganicMarketDirectoryGump.cs — paged directory of every registered
// Organic Market house (7 per page), with per-entry Teleport / Restock /
// Delete actions and Prev/Next paging.
// =========================================================================

using System;
using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class OrganicMarketDirectoryGump : DynamicGump
{
    public override bool Singleton => true;

    private const int PerPage = 7;
    private const int ButtonBack = 1;
    private const int ButtonPrev = 2;
    private const int ButtonNext = 3;
    private const int TeleportBase = 1000;
    private const int RestockBase = 2000;
    private const int DeleteBase = 3000;
    private const int MoveVendorBase = 4000;

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
        const int width = 650;
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
        builder.AddHtml(20, 38, width - 40, 20, "<basefont color=#7FFFD4>ID   Archetype             Facet      X, Y, Z</basefont>");

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

            builder.AddLabel(24, y, alive ? 0x480 : 0x21, $"{id,-4} {archetype,-20} {facet,-9} {loc.X},{loc.Y},{loc.Z}");

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

        // Anchored together in the bottom-right region rather than
        // scattered across the row (Prev far left, Back far right, Next
        // stranded in the middle) - Prev/Next stay in fixed slots even
        // when one is hidden, so Back never jumps around page to page.
        if (page > 0)
        {
            builder.AddButton(310, navY, 4014, 4016, ButtonPrev);
            builder.AddLabel(336, navY, 0x480, "< Prev");
        }

        if (page < pageCount - 1)
        {
            builder.AddButton(430, navY, 4005, 4007, ButtonNext);
            builder.AddLabel(456, navY, 0x480, "Next >");
        }

        builder.AddButton(540, navY, 4017, 4019, ButtonBack);
        builder.AddLabel(566, navY, 0x480, "Back");
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
