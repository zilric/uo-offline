// =========================================================================
// FerryCharterGump.cs — SP-043: the charter UI and instant-travel flow.
//
// SP-039 through SP-042 iterated through an Express/Scenic split (a
// +1,000gp instant option alongside a delayed sailing sequence). Per
// SP-043's simplified architecture — permanent moored boats, no more
// temporary voyage vessels — there is only one transit mode now: select
// a destination, pay the standard fare, hear the horn, arrive instantly
// on the destination boat's deck. No checkbox, no surcharge, no delay.
//
// Payment: backpack gold first (Container.ConsumeTotal), falling back to
// bank balance (Server.Mobiles.Banker.Withdraw). Any bonded/following pet
// within 8 tiles of the player's departure spot rides along, landing on
// the destination's DeckLanding (FerryRouteRegistry) alongside them.
//
// Layout: fixed-width (720px) two-column grid, height computed from the
// actual destination count (6/6 across the 13-stop network) so it never
// overshoots — see FerryRouteRegistry for the stop list.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Gumps;
using Server.Items;
using Server.Mobiles;
using Server.Network;

namespace Server.Engines.FerrySystem;

public class FerryCharterGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ButtonClose = 1;
    private const int CharterButtonBase = 100;

    private const int HornSoundId = 0x02D;
    private const int PetScanRange = 8;

    private const int GumpWidth = 720;
    private const int ColumnWidth = 330;
    private const int LeftColumnX = 20;
    private const int RightColumnX = 370;
    private const int ColumnTop = 68;
    private const int CardHeight = 64;
    private const int BottomMargin = 24;

    private readonly string _originStopName;
    private readonly List<FerryStop> _destinations;

    private FerryCharterGump(string originStopName) : base(40, 40)
    {
        _originStopName = originStopName;
        _destinations = FerryRouteRegistry.GetDestinationsFrom(originStopName);
    }

    public static void DisplayTo(Mobile from, string originStopName)
    {
        if (from?.NetState == null || string.IsNullOrEmpty(originStopName))
        {
            return;
        }

        from.SendGump(new FerryCharterGump(originStopName));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        var originStop = FerryRouteRegistry.GetStop(_originStopName);

        var leftCount = (_destinations.Count + 1) / 2;
        var rows = Math.Max(1, leftCount);
        var height = ColumnTop + rows * CardHeight + BottomMargin;

        builder.AddPage();
        builder.AddBackground(0, 0, GumpWidth, height, 5054);
        builder.AddAlphaRegion(10, 10, GumpWidth - 20, height - 20);

        builder.AddHtml(
            20, 14, GumpWidth - 60, 22,
            $"<basefont color=#F4E4BC><center>Charter a Vessel — {_originStopName}</center></basefont>"
        );
        builder.AddButton(GumpWidth - 34, 14, 4017, 4019, ButtonClose);

        builder.AddHtml(
            20, 40, GumpWidth - 40, 20,
            $"<basefont color=#C9B48F>{originStop?.Lore ?? "Charter passage to another port."}</basefont>"
        );

        if (_destinations.Count == 0)
        {
            builder.AddHtml(20, ColumnTop, GumpWidth - 40, 24, "<basefont color=#C9B48F>No charter routes are available from here.</basefont>");
            return;
        }

        for (var i = 0; i < _destinations.Count; i++)
        {
            var inLeftColumn = i < leftCount;
            var x = inLeftColumn ? LeftColumnX : RightColumnX;
            var row = inLeftColumn ? i : i - leftCount;
            var y = ColumnTop + row * CardHeight;
            var dest = _destinations[i];
            var fare = FerryRouteRegistry.ComputeFare(originStop, dest);

            builder.AddHtml(x, y, ColumnWidth, 16, $"<basefont color=#FFD700>{dest.Name}</basefont>");
            builder.AddHtml(x, y + 15, ColumnWidth - 70, 28, $"<basefont color=#C9B48F>{dest.Lore}</basefont>");
            builder.AddHtml(x, y + 44, ColumnWidth - 74, 18, $"<basefont color=#7FFFD4>{fare} gp</basefont>");
            builder.AddButton(x + ColumnWidth - 66, y + 42, 4005, 4007, CharterButtonBase + i);
            builder.AddLabel(x + ColumnWidth - 38, y + 42, 0x480, "Go");
        }
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null || info.ButtonID < CharterButtonBase)
        {
            return;
        }

        var index = info.ButtonID - CharterButtonBase;
        if (index < 0 || index >= _destinations.Count)
        {
            return;
        }

        var originStop = FerryRouteRegistry.GetStop(_originStopName);
        var destStop = _destinations[index];
        var fare = FerryRouteRegistry.ComputeFare(originStop, destStop);

        Charter(from, destStop, fare);
    }

    private static void Charter(Mobile from, FerryStop destStop, int fare)
    {
        if (from?.Backpack == null || from.Map == null || from.Map == Map.Internal)
        {
            return;
        }

        var paid = from.Backpack.ConsumeTotal(typeof(Gold), fare) || Banker.Withdraw(from, fare);
        if (!paid)
        {
            from.SendMessage($"Thou dost not have enough gold for passage — {fare} gp is required.");
            return;
        }

        var map = destStop.Map;
        if (map == null || map == Map.Internal)
        {
            return;
        }

        var pets = FerryPlacement.CollectPets(from, from.Map, from.Location, PetScanRange);

        from.PlaySound(HornSoundId);
        from.MoveToWorld(destStop.DeckLanding, map);
        from.SendMessage($"You arrive at {destStop.Name}.");

        for (var i = 0; i < pets.Count; i++)
        {
            var pet = pets[i];
            if (pet?.Deleted != false)
            {
                continue;
            }

            pet.MoveToWorld(FerryPlacement.SpreadPoint(destStop.DeckLanding, i), map);
        }
    }
}
