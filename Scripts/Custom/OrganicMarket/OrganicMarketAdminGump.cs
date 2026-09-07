// =========================================================================
// OrganicMarketAdminGump.cs — the [vh / [vendorhouse main panel.
//
// Pick a house style + archetype, then "Place Test House" throws a
// ground target (see MarketHousePlacementTarget). From here a GM can also jump
// to the paged directory, force a global restock, or wipe every tracked
// market house.
// =========================================================================

using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class OrganicMarketAdminGump : DynamicGump
{
    public override bool Singleton => true;

    private const int StyleGroup = 0;
    private const int ArchetypeGroup = 1;

    private const int SwitchStyleBase = 0;      // 0..2
    private const int SwitchArchetypeBase = 10; // 10..16

    private const int ButtonPlace = 1;
    private const int ButtonDirectory = 2;
    private const int ButtonGlobalRestock = 3;
    private const int ButtonWipeAll = 4;
    private const int ButtonSeedWorldFrontier = 8;
    private const int ButtonSeedTransitVendors = 9;

    private static readonly MarketHouseStyle[] Styles =
    {
        MarketHouseStyle.SmallShop,
        MarketHouseStyle.TwoStoryWoodPlaster,
        MarketHouseStyle.LargePatio
    };

    private static readonly MarketArchetype[] Archetypes =
    {
        MarketArchetype.BlacksmithArmory,
        MarketArchetype.MageApothecary,
        MarketArchetype.ScribeLibrary,
        MarketArchetype.RawResources,
        MarketArchetype.TailorFletcher,
        MarketArchetype.TinkerCarpenter,
        MarketArchetype.FisherCurioBaker
    };

    private OrganicMarketAdminGump() : base(50, 50)
    {
    }

    public static void DisplayTo(Mobile from)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new OrganicMarketAdminGump());
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        // +130px over the old 400px canvas, and generous (34-38px, up from
        // the old flat 16px) gaps between sections below. Both radio
        // groups fully populated plus the wider gaps push the last button
        // past the point a flat +100px would have covered, so this goes a
        // bit further than that to actually land clipping-free rather than
        // stop at a number that still clips by a few pixels.
        //
        // SP-028: +48px on top of that for the archetype catalog's growth
        // from 4 to 6 radios (afterArch below already scales with
        // Archetypes.Length automatically - only this fixed canvas height
        // needs a matching manual bump, or the wipe button at the bottom
        // clips past the background).
        //
        // SP-029: +24px more for the 7th archetype (FisherCurioBaker).
        // SP-049: -34px - Seed Dagger Isle's own row was removed once
        // WorldFrontierSeeder's uniform mainland scatter covered Dagger
        // Isle too (see DaggerIsleSeeder.cs's own removal).
        // SP-050: -74px - the legacy "Seed World Inhabitation (Filler
        // Houses)" and "Seed World Crossroads" rows (WorldHouseSeeder.cs)
        // are both gone now that file itself is deleted, leaving Seed
        // World (Frontier Density) as the only "seed world" action.
        // SP-050: +34px for the new "Seed Transit & Gate Vendors" row.
        const int width = 380;
        const int height = 676;
        const int radioRowHeight = 24;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>Organic Market Admin Tool</basefont></center>");

        var authority = MerchantGuildAuthority.Instance;
        var count = authority?.Count ?? 0;
        builder.AddLabel(20, 46, 0x480, $"Tracked market houses: {count}");

        builder.AddHtml(20, 72, width - 40, 20, "<basefont color=#7FFFD4>House Style</basefont>");
        builder.AddGroup(StyleGroup);
        for (var i = 0; i < Styles.Length; i++)
        {
            var y = 96 + i * radioRowHeight;
            builder.AddRadio(24, y, 210, 211, i == 0, SwitchStyleBase + i);
            builder.AddLabel(50, y, 0x480, OrganicMarketSpawner.StyleName(Styles[i]));
        }

        var archHeaderY = 96 + Styles.Length * radioRowHeight + 28;
        builder.AddHtml(20, archHeaderY, width - 40, 20, "<basefont color=#7FFFD4>Archetype Theme</basefont>");
        var archY = archHeaderY + 24;
        builder.AddGroup(ArchetypeGroup);
        for (var i = 0; i < Archetypes.Length; i++)
        {
            var y = archY + i * radioRowHeight;
            builder.AddRadio(24, y, 210, 211, i == 0, SwitchArchetypeBase + i);
            builder.AddLabel(50, y, 0x480, OrganicMarketSpawner.ArchetypeName(Archetypes[i]));
        }

        // Generous gap between the archetype radios and the action
        // controls below - the two groups reading as one cluster (and
        // the buttons clipping the bottom border) was the original bug.
        var afterArch = archY + Archetypes.Length * radioRowHeight + 38;

        builder.AddButton(24, afterArch, 4005, 4007, ButtonPlace);
        builder.AddLabel(60, afterArch, 0x84, "Place Test House (ground target)");

        var directoryY = afterArch + 34;
        builder.AddButton(24, directoryY, 4005, 4007, ButtonDirectory);
        builder.AddLabel(60, directoryY, 0x480, "Open Market House Directory");

        var frontierY = directoryY + 34;
        builder.AddButton(24, frontierY, 4005, 4007, ButtonSeedWorldFrontier);
        builder.AddLabel(60, frontierY, 0x59, "Seed World (Frontier Density)");

        var transitY = frontierY + 34;
        builder.AddButton(24, transitY, 4005, 4007, ButtonSeedTransitVendors);
        builder.AddLabel(60, transitY, 0x59, "Seed Transit & Gate Vendors (Compact Shops)");

        var restockY = transitY + 40;
        builder.AddHtml(20, restockY - 10, width - 40, 2, "<basefont color=#555555>________________________________</basefont>");
        builder.AddButton(24, restockY + 16, 4005, 4007, ButtonGlobalRestock);
        builder.AddLabel(60, restockY + 16, 0x44, "Force Global Restock (all vendors)");

        var wipeY = restockY + 56;
        builder.AddButton(24, wipeY, 4017, 4019, ButtonWipeAll);
        builder.AddLabel(60, wipeY, 0x25, "Wipe All Market Houses");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        switch (info.ButtonID)
        {
            case ButtonPlace:
            {
                var style = SelectedStyle(info);
                var archetype = SelectedArchetype(info);
                from.SendMessage("Target where the sign should go. Standard house placement rules apply.");
                from.Target = new MarketHousePlacementTarget(style, archetype);
                break;
            }

            case ButtonDirectory:
                OrganicMarketDirectoryGump.DisplayTo(from, 0);
                break;

            case ButtonGlobalRestock:
            {
                var authority = MerchantGuildAuthority.Instance;
                var restocked = authority?.RestockAll() ?? 0;
                from.SendMessage($"Force-restocked {restocked} vendor(s) across all market houses.");
                DisplayTo(from);
                break;
            }

            case ButtonWipeAll:
                OrganicMarketWipeConfirmGump.DisplayTo(from);
                break;

            case ButtonSeedWorldFrontier:
                from.SendMessage("OrganicMarket: seeding frontier-density houses across the mainland...");
                WorldFrontierSeeder.Seed(from);
                DisplayTo(from);
                break;

            case ButtonSeedTransitVendors:
                from.SendMessage("OrganicMarket: seeding transit-corridor and moongate compact shops...");
                TransitVendorSeeder.Seed(from);
                DisplayTo(from);
                break;
        }
    }

    private static MarketHouseStyle SelectedStyle(in RelayInfo info)
    {
        for (var i = 0; i < Styles.Length; i++)
        {
            if (info.IsSwitched(SwitchStyleBase + i))
            {
                return Styles[i];
            }
        }

        return Styles[0];
    }

    private static MarketArchetype SelectedArchetype(in RelayInfo info)
    {
        for (var i = 0; i < Archetypes.Length; i++)
        {
            if (info.IsSwitched(SwitchArchetypeBase + i))
            {
                return Archetypes[i];
            }
        }

        return Archetypes[0];
    }
}
